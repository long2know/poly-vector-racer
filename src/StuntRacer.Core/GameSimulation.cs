using StuntRacer.Engine;

namespace StuntRacer.Core;

public sealed class GameSimulation : IGameSession
{
    // Authored first-slice dynamics, not original-game parity: world-space tire grip plus
    // four vertical spring/dampers. Y is up; positive yaw turns right, pitch raises nose,
    // roll raises the right edge. COM rests 1.00 m above the canonical triangle surface.
    private const int CheckpointCount = 12;
    private const int TotalLaps = 3;
    private const double RideHeight = 1.0;
    private const double DroopHeight = 1.3;
    private const double RecoveryPenalty = 5;
    private static readonly double[] WheelRight = [-0.9, 0.9, -0.9, 0.9];
    private static readonly double[] WheelForward = [1.3, 1.3, -1.3, -1.3];
    private readonly FixedStepClock _clock = new();
    private readonly Racer _player = new();
    private readonly ModernLapGhost _ghost = new();
    private readonly City.CitySurface? _citySurface;
    private Racer? _rival;
    private ModernCheckpointTrial? _trial;
    private DriverInput _input;
    private string _phase = "ready";
    private string _pausedPhase = "racing";
    private string _message = "Ready — start a three-lap time attack";
    private long _raceTicks;
    private double _countdown;
    private double _messageSeconds;
    private double _timeAttackBestLap;
    private double _raceBestLap;
    private double _trialBestLap;
    private bool? _playerWon;

    public GameSimulation(TrackDefinition? track = null)
    {
        Track = track ?? TrackDefinition.Create();
        PlaceCar(_player, 0, 1, 0);
    }

    internal GameSimulation(TrackDefinition track, City.CitySurface citySurface) : this(track)
    {
        _citySurface = citySurface;
    }

    public TrackDefinition Track { get; }
    public string GameMode { get; private set; } = "time-attack";

    // Observe is authoritative. Advance interpolates pose only, never timers/race decisions.
    public GameSnapshot Snapshot => CreateSnapshot(1);

    public GameSnapshot Advance(double seconds, DriverInput input)
    {
        FixedStepClock.ValidateDelta(seconds);
        if (!double.IsFinite(input.Steer) || !double.IsFinite(input.Throttle) || !double.IsFinite(input.Brake))
            throw new ArgumentOutOfRangeException(nameof(input), "Driver axes must be finite.");
        _input = new DriverInput(Math.Clamp(input.Steer, -1, 1), Math.Clamp(input.Throttle, 0, 1),
            Math.Clamp(input.Brake, 0, 1), input.Boost);
        if (_phase is "ready" or "paused" or "finished")
            return Snapshot;

        // 120 Hz, at most 30 ticks/call; time beyond 250 ms is deliberately discarded.
        _clock.Advance(seconds, TryStep);
        return CreateSnapshot(_phase == "finished" ? 1 : _clock.Alpha);
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                if (_phase is "ready" or "finished")
                    ResetRun();
                break;
            case "restart":
                ResetRun();
                break;
            case "garage":
                ResetRun(false);
                break;
            case "pause":
                if (_phase is "countdown" or "racing")
                {
                    _pausedPhase = _phase;
                    _phase = "paused";
                    _message = "Paused";
                }
                break;
            case "resume":
                if (_phase == "paused")
                {
                    _phase = _pausedPhase;
                    _message = _phase == "countdown" ? "Get ready" : _trial is not null ? "Checkpoint run"
                        : _rival is null ? "Time attack" : "Head-to-head race";
                }
                break;
            case "recover":
                if (_phase == "racing")
                    Recover(_player);
                break;
            case "mode-time-attack":
                SetMode("time-attack");
                break;
            case "mode-race":
                SetMode("race");
                break;
            case "mode-checkpoint-run":
                SetMode("checkpoint-run");
                break;
            case "ghost-on":
                _ghost.Enabled = true;
                break;
            case "ghost-off":
                _ghost.Enabled = false;
                break;
            default:
                throw new ArgumentException($"Unknown simulation command: {command}", nameof(command));
        }
    }

    private double RaceTime(Racer racer) => _raceTicks * _clock.StepSeconds + racer.Penalties;

    private bool TryStep(double dt)
    {
        if (_phase is not ("countdown" or "racing"))
            return false;
        _player.Previous = _player.Car;
        if (_rival is not null)
            _rival.Previous = _rival.Car;
        if (_phase == "countdown")
        {
            _countdown = Math.Max(0, _countdown - dt);
            if (_countdown < 1e-9)
            {
                _countdown = 0;
                _phase = "racing";
                _message = _trial is not null ? "Go — beat the checkpoint clock"
                    : _rival is null ? "Go — three laps" : "Go — beat the cyan rival";
                if (GameMode == "time-attack")
                    _ghost.BeginLap(Pose(_player.Car));
            }
            return true;
        }

        _raceTicks++;
        _player.Impact *= Math.Exp(-3.5 * dt);
        _messageSeconds = Math.Max(0, _messageSeconds - dt);
        if (_messageSeconds == 0)
            _message = _trial is not null ? "Checkpoint run — cross each gate before time expires"
                : _rival is null ? "Time attack — stay on the elevated road" : "Race — first to finish three laps wins";
        IntegrateVehicle(_player, dt, _input);
        if (_rival is not null)
        {
            _rival.Impact *= Math.Exp(-3.5 * dt);
            IntegrateVehicle(_rival, dt, RivalInput(_rival));
            ResolveCarContact(_player, _rival);
        }
        if (_phase == "finished")
            return true;
        if (GameMode == "time-attack")
            _ghost.RecordTick(RaceTime(_player) - _player.LapStartedAt, Pose(_player.Car));
        var playerFinish = UpdateProgress(_player);
        var rivalFinish = _rival is null ? null : UpdateProgress(_rival);
        if (_trial is not null)
        {
            if (_phase != "finished")
            {
                if (playerFinish is not null)
                    FinishTrial(true);
                else if (_trial.Expired(RaceTime(_player)))
                    FinishTrial(false);
            }
            return true;
        }
        if (playerFinish is not null || rivalFinish is not null)
            // Same-tick finishes use crossing fraction; an exact tie goes to the player.
            Finish(playerFinish.HasValue && (!rivalFinish.HasValue || playerFinish.Value <= rivalFinish.Value));
        return true;
    }

    internal void IntegrateVehicle(Racer racer, double dt, DriverInput _input)
    {
        // Keep the shared contact integrator's operation order intact.
        ref var _car = ref racer.Car;
        ref var _groundSeconds = ref racer.GroundSeconds;
        ref var _unstableSeconds = ref racer.UnstableSeconds;
        ref var _safeS = ref racer.SafeS;
        // City actors share the integrator but keep their established handling.
        var circuitHandling = _citySurface is null;
        var wasGrounded = _car.Grounded;
        var speed = Math.Sqrt(_car.Vx * _car.Vx + _car.Vz * _car.Vz);
        _car.Steer += Math.Clamp(_input.Steer - _car.Steer, -3.2 * dt, 3.2 * dt);
        var oldForwardSpeed = _car.Vx * Math.Sin(_car.Yaw) + _car.Vz * Math.Cos(_car.Yaw);
        var maximumYawRate = MaximumYawRate(_car, speed, oldForwardSpeed, circuitHandling);
        var desiredYawRate = _car.Steer * Math.Clamp(oldForwardSpeed / 12, -1, 1) * maximumYawRate;
        if (!wasGrounded)
            desiredYawRate *= 0.22;
        _car.YawRate += (desiredYawRate - _car.YawRate) * (1 - Math.Exp(-(wasGrounded ? 7 : 1.2) * dt));
        _car.Yaw = Angle(_car.Yaw + _car.YawRate * dt);

        var sin = Math.Sin(_car.Yaw);
        var cos = Math.Cos(_car.Yaw);
        var near = _citySurface is null ? Track.FindNearest(_car.X, _car.Z) : 0;
        double ax = 0, az = 0, ay = -9.81, pitchAcceleration = 0, rollAcceleration = 0;
        double landingSpeed = 0;
        var contacts = 0;
        for (var i = 0; i < 4; i++)
        {
            var right = WheelRight[i];
            var forward = WheelForward[i];
            var wheelX = _car.X + cos * right + sin * forward;
            var wheelZ = _car.Z - sin * right + cos * forward;
            var road = _citySurface is null ? Track.SampleNear(wheelX, wheelZ, near)
                : _citySurface.Sample(wheelX, wheelZ, _car.Y);
            var height = _car.Y + Math.Sin(_car.Pitch) * forward + Math.Sin(_car.Roll) * right - road.Y;
            var compression = road.Road && height > -0.3 ? Math.Clamp(DroopHeight - height, 0, 0.6) : 0;
            _car.SetSuspension(i, compression / 0.6);
            if (compression <= 0)
                continue;

            contacts++;
            var wheelVx = _car.Vx + _car.YawRate * (cos * forward - sin * right);
            var wheelVz = _car.Vz + _car.YawRate * (-sin * forward - cos * right);
            var verticalSpeed = _car.Vy + _car.PitchRate * Math.Cos(_car.Pitch) * forward +
                _car.RollRate * Math.Cos(_car.Roll) * right - road.SlopeX * wheelVx - road.SlopeZ * wheelVz;
            landingSpeed = Math.Max(landingSpeed, -verticalSpeed);
            // Four unilateral spring/dampers; static compression .30 m, normalized .5.
            var force = Math.Clamp(8.175 * (DroopHeight - height) - 2.5 * verticalSpeed, 0, 85);
            ay += force;
            ax -= road.SlopeX * force;
            az -= road.SlopeZ * force;
            pitchAcceleration += force * forward / 2.2;
            rollAcceleration += force * right / 1.0;
        }

        var fraction = contacts / 4.0;
        _car.Grounded = contacts > 0;
        var forwardSpeed = _car.Vx * sin + _car.Vz * cos;
        var sideSpeed = _car.Vx * cos - _car.Vz * sin;
        var requestsBoost = _input.Boost && _input.Throttle > 0.05;
        _car.Boosting = requestsBoost && _car.Boost > 1e-12;
        var boostFraction = Math.Min(1, _car.Boost / (dt * 0.16));
        if (_car.Boosting)
            _car.Boost = Math.Max(0, _car.Boost - dt * 0.16);
        else if (!requestsBoost)
            _car.Boost = Math.Min(1, _car.Boost + dt * 0.045);

        var drive = _input.Throttle * 8.6 * (1 - 0.55 * _car.Damage);
        if (circuitHandling && contacts > 0)
            drive *= 1 + 0.26 * LowSpeedResponse(speed);
        if (_input.Brake > 0)
            drive -= _input.Brake * (forwardSpeed > 1 ? 18 : 4.8 * Math.Clamp(1 + forwardSpeed / 9, 0, 1));
        if (_car.Boosting)
            drive += 9.5 * boostFraction;
        drive *= fraction > 0 ? fraction : 0.06;
        var lateralAcceleration = Math.Clamp(-sideSpeed * 8, -10.8, 10.8) * fraction;
        var rolling = fraction * 0.35 * Math.Clamp(forwardSpeed * 4, -1, 1);
        var drag = contacts > 0 ? 0.0045 : 0.0009;
        ax += sin * (drive - rolling) + cos * lateralAcceleration - drag * speed * _car.Vx;
        az += cos * (drive - rolling) - sin * lateralAcceleration - drag * speed * _car.Vz;
        _car.Vx += ax * dt;
        _car.Vz += az * dt;
        _car.Vy += ay * dt;
        _car.X += _car.Vx * dt;
        _car.Z += _car.Vz * dt;
        _car.Y += _car.Vy * dt;

        pitchAcceleration -= _car.PitchRate * 0.8 + (contacts == 0 ? 0.16 : 0);
        rollAcceleration -= _car.RollRate * 0.8;
        _car.PitchRate = Math.Clamp(_car.PitchRate + pitchAcceleration * dt, -3, 3);
        _car.RollRate = Math.Clamp(_car.RollRate + rollAcceleration * dt, -4, 4);
        _car.Pitch = Math.Clamp(_car.Pitch + _car.PitchRate * dt, -1.2, 1.2);
        _car.Roll = Math.Clamp(_car.Roll + _car.RollRate * dt, -1.45, 1.45);
        if (!wasGrounded && contacts > 0 && landingSpeed > 3.5)
            ApplyImpact(racer, (landingSpeed - 3.5) / 16);

        if (_citySurface is null && _car.Y < 0.75)
        {
            if (_car.Vy < -1)
                ApplyImpact(racer, Math.Max(0.25, -_car.Vy / 22));
            _car.Y = 0.75;
            _car.Vy = _car.Vy < -2 ? -_car.Vy * 0.12 : 0;
            _car.Vx *= Math.Exp(-1.5 * dt);
            _car.Vz *= Math.Exp(-1.5 * dt);
            _car.PitchRate *= 0.9;
            _car.RollRate *= 0.9;
            _car.Grounded = true;
            _groundSeconds += dt;
        }

        // A city has real ground and independent streets, not a closed-ribbon recovery tether.
        if (_citySurface is not null)
            return;

        var center = Track.SampleNear(_car.X, _car.Z, Track.FindNearest(_car.X, _car.Z));
        if (_car.Y > 2)
            _groundSeconds = 0;
        if (center.Road && contacts >= 3 && Math.Abs(center.Lateral) < center.Width * 0.30 &&
            Math.Abs(_car.Roll) < 0.6)
            _safeS = center.S;
        _unstableSeconds = Math.Abs(_car.Roll) > 1.2 || Math.Abs(_car.Pitch) > 1.1
            ? _unstableSeconds + dt : 0;
        if (_groundSeconds > 1.0 || _unstableSeconds > 1.5 || _car.Damage >= 1 ||
            Math.Abs(_car.X) > 2_000 || Math.Abs(_car.Z) > 2_000)
            Recover(racer);
    }

    private static double LowSpeedResponse(double speed)
    {
        // Smoothly return to the original high-speed power and steering curves.
        var t = Math.Clamp((speed - 10) / 16, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    private static double MaximumYawRate(in Vehicle car, double speed, double forwardSpeed, bool circuitHandling)
    {
        var rate = 1.35 / (1 + speed / 13);
        if (circuitHandling && forwardSpeed > 0 && car.Grounded &&
            (car.FL > 0 || car.FR > 0 || car.RL > 0 || car.RR > 0))
            rate *= 1 + 0.32 * LowSpeedResponse(speed);
        return rate;
    }

    internal static void ApplyImpact(Racer racer, double severity)
    {
        ref var _car = ref racer.Car;
        severity = Math.Clamp(severity, 0, 1);
        racer.Impact = Math.Max(racer.Impact, severity);
        _car.Damage = Math.Clamp(_car.Damage + severity * 0.38, 0, 1);
        _car.Vx *= 1 - severity * 0.28;
        _car.Vz *= 1 - severity * 0.28;
    }

    private DriverInput RivalInput(Racer racer)
    {
        ref var car = ref racer.Car;
        var forwardSpeed = car.Vx * Math.Sin(car.Yaw) + car.Vz * Math.Cos(car.Yaw);
        var speed = Math.Abs(forwardSpeed);
        var road = Track.Sample(car.X, car.Z);
        var look = 10 + speed * 0.4;
        var target = Track.AtDistance(road.S + look);
        var lane = Math.Min(racer.GridLateral, Math.Max(0, target.Width / 2 - 1.6));
        var targetX = target.X + Math.Cos(target.Heading) * lane;
        var targetZ = target.Z - Math.Sin(target.Heading) * lane;
        var error = Angle(Math.Atan2(targetX - car.X, targetZ - car.Z) - car.Yaw);
        var rate = 2 * Math.Max(speed, 8) * Math.Sin(error) / look;
        var horizontalSpeed = Math.Sqrt(car.Vx * car.Vx + car.Vz * car.Vz);
        var scale = Math.Clamp(forwardSpeed / 12, -1, 1) * MaximumYawRate(car, horizontalSpeed, forwardSpeed, true);
        var steer = Math.Abs(scale) < 0.03 ? 0 : Math.Clamp(rate / scale, -1, 1);
        var ahead = Track.AtDistance(road.S + 38);
        var bend = Math.Abs(Angle(ahead.Heading - road.Heading));
        var desiredSpeed = bend > 0.06 ? 18.5 : 27;
        var clear = Track.AtDistance(road.S + 65);
        var boost = car.Grounded && car.Boost > 0.25 && bend < 0.02 &&
            Math.Abs(clear.Y - road.Y) < 2 && Math.Abs(error) < 0.08 && Math.Abs(car.Roll) < 0.25 &&
            forwardSpeed > 8 && forwardSpeed < desiredSpeed - 5;
        return new DriverInput(steer, Math.Clamp((desiredSpeed - forwardSpeed) * 0.65, 0, 1),
            Math.Clamp((forwardSpeed - desiredSpeed - 1) * 0.35, 0, 1), boost);
    }

    internal static void ResolveCarContact(Racer player, Racer rival)
    {
        ref var a = ref player.Car;
        ref var b = ref rival.Car;
        if (Math.Abs(a.Y - b.Y) > 1.3)
            return;

        // Equal-mass oriented chassis boxes, not barriers or road-following corrections.
        // A vertical gate prevents impacts between different levels of an overpass/jump.
        var arx = Math.Cos(a.Yaw);
        var arz = -Math.Sin(a.Yaw);
        var afx = -arz;
        var afz = arx;
        var brx = Math.Cos(b.Yaw);
        var brz = -Math.Sin(b.Yaw);
        var bfx = -brz;
        var bfz = brx;
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var minimum = double.MaxValue;
        double nx = 0, nz = 0;
        for (var i = 0; i < 4; i++)
        {
            var x = i switch { 0 => arx, 1 => afx, 2 => brx, _ => bfx };
            var z = i switch { 0 => arz, 1 => afz, 2 => brz, _ => bfz };
            var extentA = 1.05 * Math.Abs(arx * x + arz * z) + 2.0 * Math.Abs(afx * x + afz * z);
            var extentB = 1.05 * Math.Abs(brx * x + brz * z) + 2.0 * Math.Abs(bfx * x + bfz * z);
            var center = dx * x + dz * z;
            var overlap = extentA + extentB - Math.Abs(center);
            if (overlap <= 0)
                return;
            if (overlap < minimum)
            {
                minimum = overlap;
                var sign = center >= 0 ? 1 : -1;
                nx = x * sign;
                nz = z * sign;
            }
        }

        var separation = (minimum + 1e-5) * 0.5;
        a.X -= nx * separation;
        a.Z -= nz * separation;
        b.X += nx * separation;
        b.Z += nz * separation;
        var closing = -((b.Vx - a.Vx) * nx + (b.Vz - a.Vz) * nz);
        if (closing > 0)
        {
            var impulse = Math.Min(12, closing * 0.54);
            a.Vx -= nx * impulse;
            a.Vz -= nz * impulse;
            b.Vx += nx * impulse;
            b.Vz += nz * impulse;
            if (closing > 0.5)
            {
                var severity = Math.Clamp(closing / 24, 0, 0.4);
                ApplyImpact(player, severity);
                ApplyImpact(rival, severity);
            }
        }
    }

    private double? UpdateProgress(Racer racer)
    {
        ref var _car = ref racer.Car;
        var road = Track.Sample(_car.X, _car.Z);
        racer.ProgressS = road.S;
        var movement = Math.IEEERemainder(road.S - racer.LastTrackS, Track.Length);
        var forward = _car.Vx * Math.Sin(road.Heading) + _car.Vz * Math.Cos(road.Heading);
        var target = racer.NextCheckpoint == CheckpointCount ? 0 : Track.Length * racer.NextCheckpoint / CheckpointCount;
        var distanceToGate = (target - racer.LastTrackS + Track.Length) % Track.Length;
        double? finish = null;
        // Gates must be crossed in order, forward, over real nearby road. Recovery never crosses a gate.
        if (road.Road && Math.Abs(_car.Y - road.Y - RideHeight) < 8 &&
            forward > 0.5 && movement > 0 && movement < 2 &&
            distanceToGate > 1e-8 && distanceToGate <= movement + 1e-8)
        {
            if (_trial is not null && ReferenceEquals(racer, _player))
            {
                var crossingElapsed = RaceTime(racer) - _clock.StepSeconds +
                    distanceToGate / movement * _clock.StepSeconds;
                if (!_trial.Pass(crossingElapsed))
                {
                    racer.LastTrackS = road.S;
                    FinishTrial(false);
                    return null;
                }
            }
            racer.NextCheckpoint++;
            if (racer.NextCheckpoint > CheckpointCount)
            {
                var lapTime = RaceTime(racer) - racer.LapStartedAt;
                if (racer.BestLap == 0 || lapTime < racer.BestLap)
                    racer.BestLap = lapTime;
                if (GameMode == "time-attack" && ReferenceEquals(racer, _player))
                    _ghost.CompleteLap(lapTime, Pose(_car));
                racer.NextCheckpoint = 1;
                if (racer.Lap == (_trial is null ? TotalLaps : 1))
                    finish = distanceToGate / movement;
                else
                {
                    racer.Lap++;
                    racer.LapStartedAt = RaceTime(racer);
                    if (ReferenceEquals(racer, _player))
                    {
                        if (GameMode == "time-attack")
                            _ghost.BeginLap(Pose(_car));
                        _message = $"Lap {racer.Lap} of {TotalLaps}";
                        _messageSeconds = 3;
                    }
                }
            }
        }
        racer.LastTrackS = road.S;
        return finish;
    }

    private void Recover(Racer racer)
    {
        var boost = racer.Car.Boost;
        var damage = Math.Min(racer.Car.Damage, 0.45);
        PlaceCar(racer, racer.SafeS, boost, damage);
        racer.Penalties += RecoveryPenalty;
        racer.Recoveries++;
        racer.GroundSeconds = racer.UnstableSeconds = 0;
        if (ReferenceEquals(racer, _player))
        {
            _message = "Recovered — 5 second penalty";
            _messageSeconds = 3;
            if (GameMode == "time-attack")
                _ghost.InvalidateLap();
            if (_trial is not null && _trial.Expired(RaceTime(racer)))
                FinishTrial(false);
        }
    }

    private void PlaceCar(Racer racer, double s, double boost, double damage)
    {
        var point = Track.AtDistance(s);
        var lateral = Math.Clamp(racer.GridLateral, -Math.Max(0, point.Width / 2 - 1.1),
            Math.Max(0, point.Width / 2 - 1.1));
        var x = point.X + Math.Cos(point.Heading) * lateral;
        var z = point.Z - Math.Sin(point.Heading) * lateral;
        var road = Track.Sample(x, z);
        racer.Car = new Vehicle
        {
            X = x, Y = road.Y + RideHeight, Z = z, Yaw = point.Heading,
            Pitch = Math.Atan(road.SlopeX * Math.Sin(point.Heading) + road.SlopeZ * Math.Cos(point.Heading)),
            Roll = Math.Atan(road.SlopeX * Math.Cos(point.Heading) - road.SlopeZ * Math.Sin(point.Heading)),
            Boost = boost, Damage = damage, Grounded = true,
            FL = 0.5, FR = 0.5, RL = 0.5, RR = 0.5
        };
        racer.Previous = racer.Car;
        racer.ProgressS = racer.LastTrackS = point.S;
    }

    private void SetMode(string mode)
    {
        if (_phase is "countdown" or "racing")
            throw new InvalidOperationException("Pause or finish before changing game mode.");
        if (mode == "race" && Track.AtDistance(0).Width < 4.4)
            throw new InvalidOperationException("This track is too narrow for a safe two-car grid.");
        if (GameMode != mode)
        {
            if (GameMode == "time-attack")
                _timeAttackBestLap = _player.BestLap;
            else if (GameMode == "race")
                _raceBestLap = _player.BestLap;
            else
                _trialBestLap = _player.BestLap;
            GameMode = mode;
            _player.BestLap = mode == "time-attack" ? _timeAttackBestLap
                : mode == "race" ? _raceBestLap : _trialBestLap;
            _ghost.Clear();
        }
        // Even selecting the current mode explicitly abandons the run for a fresh ready grid.
        ResetRun(false);
    }

    private void ResetRun(bool countdown = true)
    {
        _clock.Reset();
        _raceTicks = 0;
        _messageSeconds = 0;
        _playerWon = null;
        _ghost.ResetCurrent();
        _trial = GameMode == "checkpoint-run" ? new ModernCheckpointTrial(Track.Length, CheckpointCount) : null;
        _countdown = countdown ? 3 : 0;
        _phase = countdown ? "countdown" : "ready";
        if (GameMode == "race")
        {
            _rival ??= new Racer();
            var offset = Math.Min(2.2, Track.AtDistance(0).Width / 2 - 1.1);
            ResetRacer(_player, -offset);
            ResetRacer(_rival, offset);
        }
        else
        {
            _rival = null;
            ResetRacer(_player, 0);
        }
        _message = countdown ? "Get ready" : _trial is not null ? "Ready — one timed checkpoint circuit" : _rival is null
            ? "Ready — start a three-lap time attack" : "Ready — head-to-head race";
    }

    private void ResetRacer(Racer racer, double lateral)
    {
        racer.Penalties = racer.LapStartedAt = racer.Impact = racer.SafeS = racer.GroundSeconds = racer.UnstableSeconds = 0;
        racer.NextCheckpoint = racer.Lap = 1;
        racer.Recoveries = 0;
        racer.GridLateral = lateral;
        PlaceCar(racer, 0, 1, 0);
    }

    private void Finish(bool playerWon)
    {
        _phase = "finished";
        _playerWon = _rival is null ? null : playerWon;
        _message = _rival is null ? "Finished — restart for another time attack"
            : playerWon ? "RACE WON — first to finish three laps" : "RIVAL WON — restart for a rematch";
        Stop(_player);
        if (_rival is not null)
            Stop(_rival);
    }

    private void FinishTrial(bool success)
    {
        _trial!.Finish(success);
        _phase = "finished";
        _message = success ? "CHECKPOINT RUN COMPLETE — all gates passed"
            : "TIME EXPIRED — checkpoint run failed";
        Stop(_player);
    }

    private static void Stop(Racer racer)
    {
        racer.Car.Vx = racer.Car.Vy = racer.Car.Vz = 0;
        racer.Car.Boosting = false;
        racer.Previous = racer.Car;
    }

    private GameSnapshot CreateSnapshot(double alpha)
    {
        var rival = _rival;
        RaceSnapshot? race = null;
        if (rival is not null)
        {
            var position = _playerWon is not null ? _playerWon.Value ? 1 : 2
                : StandingDistance(_player) >= StandingDistance(rival) ? 1 : 2;
            race = new RaceSnapshot("race", position, 2, rival.Lap, _playerWon,
                _playerWon is null ? _phase : _playerWon.Value ? "player-won" : "rival-won",
                rival.ProgressS / Track.Length, RaceTime(rival), rival.Recoveries, _raceTicks * _clock.StepSeconds);
        }
        return new GameSnapshot(CarSnapshot(_player, alpha), rival is null ? null : CarSnapshot(rival, alpha),
            _phase, RaceTime(_player), _player.Lap, _trial is null ? TotalLaps : 1, RaceTime(_player) - _player.LapStartedAt,
            _player.BestLap, _player.ProgressS / Track.Length, _countdown, _player.Impact,
            _player.Recoveries, alpha, _clock.Tick, _message, race, _trial?.Snapshot(RaceTime(_player)),
            GameMode == "time-attack" ? _ghost.Snapshot(RaceTime(_player) - _player.LapStartedAt) : null);
    }

    private double StandingDistance(Racer racer)
    {
        var spacing = Track.Length / CheckpointCount;
        var completed = racer.NextCheckpoint - 1;
        var partial = Math.IEEERemainder(racer.ProgressS - completed * spacing, Track.Length);
        return ((racer.Lap - 1) * CheckpointCount + completed) * spacing + Math.Clamp(partial, -spacing, spacing);
    }

    internal static CarSnapshot CarSnapshot(Racer racer, double alpha)
    {
        ref var _car = ref racer.Car;
        ref var _previous = ref racer.Previous;
        return new CarSnapshot(
            Lerp(_previous.X, _car.X, alpha), Lerp(_previous.Y, _car.Y, alpha),
            Lerp(_previous.Z, _car.Z, alpha), _previous.Yaw + Angle(_car.Yaw - _previous.Yaw) * alpha,
            Lerp(_previous.Pitch, _car.Pitch, alpha), Lerp(_previous.Roll, _car.Roll, alpha),
            _car.Vx * Math.Sin(_car.Yaw) + _car.Vz * Math.Cos(_car.Yaw),
            _car.Steer, _car.Boost, _car.Damage, _car.Grounded, _car.Boosting,
            [Lerp(_previous.FL, _car.FL, alpha), Lerp(_previous.FR, _car.FR, alpha),
             Lerp(_previous.RL, _car.RL, alpha), Lerp(_previous.RR, _car.RR, alpha)]);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Angle(double angle) => Math.IEEERemainder(angle, Math.Tau);

    private static GhostPose Pose(in Vehicle car) => new(
        car.X, car.Y, car.Z, car.Yaw, car.Pitch, car.Roll, car.Vx * Math.Sin(car.Yaw) + car.Vz * Math.Cos(car.Yaw),
        car.Steer, car.Boost, car.Damage, car.Grounded, car.Boosting, car.FL, car.FR, car.RL, car.RR);

    internal sealed class Racer
    {
        public Vehicle Car;
        public Vehicle Previous;
        public double Penalties, LapStartedAt, BestLap, Impact, ProgressS, LastTrackS, SafeS;
        public double GroundSeconds, UnstableSeconds, GridLateral;
        public int NextCheckpoint = 1;
        public int Lap = 1;
        public int Recoveries;
    }

    internal struct Vehicle
    {
        public double X, Y, Z, Vx, Vy, Vz, Yaw, Pitch, Roll, YawRate, PitchRate, RollRate;
        public double Steer, Boost, Damage, FL, FR, RL, RR;
        public bool Grounded, Boosting;

        public void SetSuspension(int index, double value)
        {
            switch (index)
            {
                case 0: FL = value; break;
                case 1: FR = value; break;
                case 2: RL = value; break;
                case 3: RR = value; break;
            }
        }
    }
}
