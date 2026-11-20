using StuntRacer.Engine;
using static StuntRacer.Core.Stunts.StuntVector;

namespace StuntRacer.Core.Stunts;

/// <summary>
/// Authored 120 Hz arcade stunt dynamics. Contact is unilateral on the canonical XYZ
/// triangle strip; surface coordinates integrate player-driven tangent velocity, not a path follower.
/// </summary>
/// <remarks>
/// Start/restart gives a three-second countdown. Recovery cancels pending stunts, costs
/// 250 points/five seconds, and invalidates the clean-best recording; earned events remain.
/// Each feature scores once per run. Restart retains the session-local actual best ghost.
/// CreateHardDriving selects a separate spatial proving ground with timed ordered checkpoints.
/// Garage abandons the current run for ready state without deleting an actual best recording.
/// </remarks>
public sealed class StuntParkSession : IGameSession
{
    private readonly StuntParkCourse.Course _course;
    private readonly bool _hardDriving;
    private readonly StuntSurface _surface;
    private readonly FixedStepClock _clock = new();
    private readonly ModernLapGhost _ghost = new();
    private HardDrivingTrial? _trial;
    private StuntVector _position, _velocity, _forward, _up, _angularVelocity;
    private GhostPose _previous;
    private DriverInput _input;
    private string _phase = "ready";
    private string _pausedPhase = "racing";
    private string _message = "Stunt park — drive the loop, precision bridge and gap";
    private string _lastStunt = "No stunt yet";
    private string _airOrigin = "";
    private double _s, _q = 0.5, _steer, _boost = 1, _damage, _normalLoad;
    private double _yaw, _pitch, _roll, _countdown, _impact, _floorTime, _safeS;
    private double _airTime, _totalAirTime, _loopTurn, _messageTime, _penalties, _bestTime;
    private double _fl = 0.5, _fr = 0.5, _rl = 0.5, _rr = 0.5;
    private bool _onRoad = true, _onFloor, _boosting, _loopArmed, _loopInverted, _loopAwarded;
    private bool _jumpInFlight, _jumpCounted, _cleanGap, _cleanRun = true, _recordedBest;
    private bool? _success;
    private int _score, _loops, _jumps, _landings, _gate, _recoveries, _bestScore;
    private long _runTicks;

    public StuntParkSession() : this(StuntParkCourse.Build(), false)
    {
    }

    private StuntParkSession(StuntParkCourse.Course course, bool hardDriving)
    {
        _course = course;
        _hardDriving = hardDriving;
        _trial = hardDriving ? new(course) : null;
        _surface = new(_course.Track);
        Place(0);
        if (hardDriving)
            _message = "Hard Drivin' — beat the checkpoint clock through the loop and flyover";
    }

    /// <summary>A distinct authored timed proving ground, not the original Atari engine or track data.</summary>
    public static StuntParkSession CreateHardDriving() => new(HardDrivingSpatialCourse.Build(), true);

    public TrackDefinition Track => _course.Track;
    public GameSnapshot Snapshot => CreateSnapshot(1);

    public StuntChallengeSnapshot Challenge
    {
        get
        {
            var next = _course.Gates[Math.Min(_gate, _course.Gates.Length - 1)];
            return new(_hardDriving ? "hard-driving" : "stunts", _score, _loops, _jumps, _landings, _totalAirTime, _airTime,
                _onRoad ? _course.FeatureAt(_s) : _onFloor ? "Off-road — recover"
                    : _jumpInFlight ? "Gap flight" : $"Falling — {_airOrigin}",
                _lastStunt, _gate, _course.Gates.Length,
                _gate == _course.Gates.Length ? "Complete" : next.Name, next.S / Track.Length,
                _success, _cleanRun, _bestTime, _bestScore, _recordedBest, _normalLoad / 9.81);
        }
    }

    public GameSnapshot Advance(double seconds, DriverInput input)
    {
        FixedStepClock.ValidateDelta(seconds);
        if (!double.IsFinite(input.Steer) || !double.IsFinite(input.Throttle) || !double.IsFinite(input.Brake))
            throw new ArgumentOutOfRangeException(nameof(input), "Driver axes must be finite.");
        _input = new(Math.Clamp(input.Steer, -1, 1), Math.Clamp(input.Throttle, 0, 1),
            Math.Clamp(input.Brake, 0, 1), input.Boost);
        if (_phase is "ready" or "paused" or "finished")
            return Snapshot;
        _clock.Advance(seconds, Step);
        return CreateSnapshot(_phase == "finished" ? 1 : _clock.Alpha);
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                if (_phase is "ready" or "finished")
                    Reset();
                break;
            case "restart":
                Reset();
                break;
            case "garage":
                Reset(false);
                break;
            case "pause":
                if (_phase is "countdown" or "racing")
                {
                    _pausedPhase = _phase;
                    _phase = "paused";
                }
                break;
            case "resume":
                if (_phase == "paused")
                    _phase = _pausedPhase;
                break;
            case "recover":
                if (_phase == "racing")
                    Recover();
                break;
            case "ghost-on":
                _ghost.Enabled = true;
                break;
            case "ghost-off":
                _ghost.Enabled = false;
                break;
            default:
                throw new ArgumentException($"Unknown stunt-park command: {command}", nameof(command));
        }
    }

    private double Elapsed => _runTicks * _clock.StepSeconds;
    private double RunTime => Elapsed + _penalties;
    private StuntVector Right => Cross(_up, _forward).Unit;

    private bool Step(double dt)
    {
        if (_phase is not ("countdown" or "racing"))
            return false;
        _previous = Pose();
        if (_phase == "countdown")
        {
            _countdown = Math.Max(0, _countdown - dt);
            if (_countdown < 1e-9)
            {
                _countdown = 0;
                _phase = "racing";
                Say(_hardDriving ? "GO — timed proving ground; pass every checkpoint and drive the loop"
                    : "GO — build speed for the loop; steer, brake and boost are yours", 4);
                _ghost.BeginLap(Pose());
            }
            return true;
        }

        _runTicks++;
        _impact *= Math.Exp(-3.5 * dt);
        _messageTime = Math.Max(0, _messageTime - dt);
        if (_messageTime == 0)
            _message = Hint();
        _steer += Math.Clamp(_input.Steer - _steer, -3.2 * dt, 3.2 * dt);
        var oldS = _s;
        var wasOnRoad = _onRoad;
        if (_onRoad)
            IntegrateContact(dt);
        else
            IntegrateFlight(dt);
        UpdateEuler();

        if (wasOnRoad && _onRoad)
        {
            var movement = Math.IEEERemainder(_s - oldS, Track.Length);
            UpdateLoop(oldS, movement);
            UpdateRoute(oldS, movement);
        }
        else if (!_onRoad)
            _loopArmed = false;

        if (_phase == "racing" && _trial is not null && _trial.Expired(RunTime))
            Finish(true);
        if (_phase == "racing")
            _ghost.RecordTick(Elapsed, Pose());
        if (_onFloor)
            _floorTime += dt;
        else
            _floorTime = 0;
        if (_phase == "racing" &&
            (_floorTime > 1.4 || _position.Y < -20 || Math.Abs(_position.X) > 1_000 || Math.Abs(_position.Z) > 1_000))
            Recover();
        return true;
    }

    private void IntegrateContact(double dt)
    {
        var road = _surface.At(_s, _q);
        _up = road.Normal;
        _forward = _forward.OnPlane(_up).Unit;
        _velocity = _velocity.OnPlane(_up);
        var rates = road.Rates(_velocity);
        // v·dn/dt is negative inside the loop. A negative required reaction would
        // pull the tires toward the road, which a real open road cannot do.
        _normalLoad = -Dot(_velocity, road.NormalDerivative * rates.S) - Dot(Gravity, _up);
        if (_normalLoad < 0 || !road.Road || _q is < 0 or > 1)
        {
            Detach();
            IntegrateFlight(dt);
            return;
        }

        var speed = _velocity.Length;
        var forwardSpeed = Dot(_velocity, _forward);
        var yawRate = _steer * 1.35 / (1 + speed / 35) * Math.Clamp(forwardSpeed / 8, -1, 1);
        _forward = _forward.Rotate(_up, yawRate * dt).OnPlane(_up).Unit;
        var right = Right;
        var sideSpeed = Dot(_velocity, right);
        var asksBoost = _input.Boost && _input.Throttle > 0.05;
        _boosting = asksBoost && _boost > 1e-12 && _normalLoad > 1;
        var boostFraction = Math.Min(1, _boost / (dt * 0.16));
        if (_boosting)
            _boost = Math.Max(0, _boost - dt * 0.16);
        else if (!asksBoost)
            _boost = Math.Min(1, _boost + dt * 0.045);
        var drive = _input.Throttle * 8.8 * (1 - 0.5 * _damage);
        if (_input.Brake > 0)
            drive -= _input.Brake * (forwardSpeed > 1 ? 20 : 4.8 * Math.Clamp(1 + forwardSpeed / 9, 0, 1));
        if (_boosting)
            drive += 10 * boostFraction;
        drive = Math.Clamp(drive, -_normalLoad * 1.7, _normalLoad * 1.7);
        var grip = Math.Min(21, _normalLoad * 1.65);
        var sideForce = Math.Clamp(-sideSpeed * 9, -grip, grip);
        var rolling = 0.28 * Math.Clamp(forwardSpeed * 4, -1, 1);
        var acceleration = Gravity.OnPlane(_up) + _forward * (drive - rolling) +
            right * sideForce - _velocity * (0.0045 * speed);
        _velocity = (_velocity + acceleration * dt).OnPlane(_up);

        rates = road.Rates(_velocity);
        var nextS = _surface.Wrap(_s + rates.S * dt);
        var nextQ = _q + rates.Q * dt;
        var next = _surface.At(nextS, nextQ);
        _s = nextS;
        _q = nextQ;
        if (!next.Road || nextQ is < 0 or > 1)
        {
            // No reaction or body rotation is borrowed from a nonexistent triangle.
            _position += _velocity * dt;
            Detach();
            return;
        }
        var nextNormal = next.Normal;
        _angularVelocity = Cross(_up, nextNormal) / dt + _up * yawRate;
        _velocity = _velocity.Transport(_up, nextNormal);
        _forward = _forward.Transport(_up, nextNormal).OnPlane(nextNormal).Unit;
        _up = nextNormal;
        _position = next.Center;
        _onFloor = false;
        UpdateSuspension(next);
    }

    private void IntegrateFlight(double dt)
    {
        _normalLoad = 0;
        _boosting = false;
        _fl = _fr = _rl = _rr = 0;
        if (!(_input.Boost && _input.Throttle > 0.05))
            _boost = Math.Min(1, _boost + dt * 0.045);
        var from = _position;
        _velocity += (Gravity - _velocity * (0.0007 * _velocity.Length)) * dt;
        var to = from + _velocity * dt;
        _angularVelocity = _angularVelocity * Math.Exp(-0.45 * dt) + _up * (_steer * 0.25 * dt);
        var angularSpeed = _angularVelocity.Length;
        if (angularSpeed > 1e-9)
        {
            var axis = _angularVelocity / angularSpeed;
            _forward = _forward.Rotate(axis, angularSpeed * dt).Unit;
            _up = _up.Rotate(axis, angularSpeed * dt).OnPlane(_forward).Unit;
        }
        if (!_onFloor)
        {
            _airTime += dt;
            _totalAirTime += dt;
        }
        if (_jumpInFlight && !_jumpCounted && _airTime >= 0.15)
        {
            _jumpCounted = true;
            _jumps++;
            Say("GAP JUMP — airborne; line up the landing", 2);
        }

        if (_surface.Sweep(from, to, out var hit))
        {
            var normalSpeed = -Dot(_velocity, hit.Normal);
            var landing = _surface.At(hit.S, hit.Q);
            var alignment = Dot(_up, landing.Normal);
            if (hit.Front && alignment > 0.62 && normalSpeed < 15)
            {
                var lateralSpeed = Math.Abs(Dot(_velocity, landing.Across.Unit));
                var clean = _airTime >= 0.15 && normalSpeed < 8 && alignment > 0.88 && lateralSpeed < 5 &&
                    hit.Q * landing.Across.Length >= 1 && (1 - hit.Q) * landing.Across.Length >= 1;
                _velocity = _velocity.OnPlane(landing.Normal) * (clean ? 0.98 : 0.84);
                _forward = _forward.OnPlane(landing.Normal).Unit;
                _up = landing.Normal;
                _s = hit.S;
                _q = Math.Clamp(hit.Q, 0, 1);
                _position = landing.Center;
                _onRoad = true;
                _onFloor = false;
                _angularVelocity = default;
                _normalLoad = Math.Max(0, -Dot(Gravity, _up));
                if (_airTime >= 0.15)
                {
                    if (clean && _jumpCounted && _jumpInFlight &&
                        hit.S >= _course.LandingStart + 2.5 && hit.S <= _course.LandingEnd - 2.5)
                    {
                        _landings++;
                        if (!_cleanGap)
                        {
                            _score += 500 + (int)Math.Min(250, Math.Round(_airTime * 75));
                            _cleanGap = true;
                        }
                        _lastStunt = $"CLEAN GAP LANDING — {_airTime:F2}s airborne";
                        Say(_lastStunt, 4);
                    }
                    else if (!clean)
                    {
                        ApplyImpact(Math.Clamp((normalSpeed - 3) / 18 + (1 - alignment) * 0.4, 0.12, 0.9));
                        _lastStunt = "HARD LANDING — no clean-stunt score";
                        Say(_lastStunt, 4);
                    }
                }
                _airTime = 0;
                _jumpInFlight = _jumpCounted = false;
                UpdateSuspension(landing);
                return;
            }
            _position = Lerp(from, to, hit.Fraction) + hit.Normal * 0.03;
            _velocity = _velocity.OnPlane(hit.Normal) * 0.65 + hit.Normal * Math.Min(3, normalSpeed * 0.1);
            ApplyImpact(Math.Clamp(normalSpeed / 18 + 0.15, 0.15, 1));
            _jumpInFlight = false;
            _lastStunt = "CRASH — upright landing required";
            Say(_lastStunt, 3);
            return;
        }

        _position = to;
        if (_position.Y <= 0.8)
        {
            if (!_onFloor)
            {
                ApplyImpact(Math.Clamp(-_velocity.Y / 20, 0.2, 1));
                _lastStunt = "FALL — recover to the last safe gate";
                Say(_lastStunt, 3);
            }
            _position = new(_position.X, 0.8, _position.Z);
            _velocity = new(_velocity.X * Math.Exp(-2 * dt), 0, _velocity.Z * Math.Exp(-2 * dt));
            _angularVelocity *= Math.Exp(-4 * dt);
            _onFloor = true;
            _airTime = 0;
            _jumpInFlight = false;
        }
    }

    private void Detach()
    {
        _onRoad = false;
        _onFloor = false;
        _normalLoad = 0;
        _boosting = false;
        _fl = _fr = _rl = _rr = 0;
        _airTime = 0;
        _airOrigin = _course.FeatureAt(_s);
        _jumpInFlight = _s >= _course.RampEnd - 6 && _s < _course.LandingStart &&
            Dot(_velocity, _forward) > 8;
        _jumpCounted = false;
        _loopArmed = false;
        if (!_jumpInFlight)
            Say(_airOrigin is "Vertical loop" or "Proving-ground loop" ? "LOOP CONTACT LOST — gravity wins at low speed"
                : "ROAD EDGE / CREST — airborne", 3);
    }

    private void UpdateLoop(double oldS, double movement)
    {
        if (movement <= 0 || movement >= 2)
        {
            _loopArmed = false;
            return;
        }
        if (Crossed(oldS, movement, _course.LoopStart))
        {
            _loopArmed = true;
            _loopInverted = false;
            _loopTurn = 0;
        }
        if (!_loopArmed)
            return;
        _loopTurn += _pitch - _previous.Pitch;
        _loopInverted |= _up.Y < -0.75 && _position.Y > 40;
        if (Crossed(oldS, movement, _course.LoopEnd))
        {
            if (_loopInverted && _loopTurn > 5.8)
            {
                _loops++;
                var points = _loopAwarded ? 0 : 1_000;
                _score += points;
                _loopAwarded = true;
                _lastStunt = $"FULL LOOP — 360° on tire contact +{points}";
                Say(_lastStunt, 5);
            }
            _loopArmed = false;
        }
    }

    private void UpdateRoute(double oldS, double movement)
    {
        if (movement <= 0 || movement >= 2 || _gate == _course.Gates.Length)
            return;
        var gate = _course.Gates[_gate];
        if (!Crossed(oldS, movement, gate.S))
            return;
        if (_trial is not null)
        {
            var distance = (gate.S - oldS + Track.Length) % Track.Length;
            var crossingTime = RunTime - _clock.StepSeconds + distance / movement * _clock.StepSeconds;
            if (!_trial.Pass(crossingTime))
            {
                Finish(true);
                return;
            }
        }
        _gate++;
        if (_gate == _course.Gates.Length)
        {
            Finish();
            return;
        }
        _safeS = gate.RecoveryS;
        if (_trial is not null)
            Say($"CHECKPOINT {_gate}/{_course.Gates.Length} — +{_trial.Snapshot(RunTime).LastBonus:F1}s", 3);
    }

    private bool Crossed(double oldS, double movement, double target)
    {
        var distance = (target - oldS + Track.Length) % Track.Length;
        return distance > 1e-9 && distance <= movement + 1e-8;
    }

    private void UpdateSuspension(StuntSurface.Sample road)
    {
        var compression = Math.Clamp(0.25 + _normalLoad / 39.24, 0.05, 1);
        var width = road.Across.Length;
        var left = _q * width;
        var right = (1 - _q) * width;
        var transfer = Math.Clamp(_steer * _velocity.Length * 0.005, -0.18, 0.18);
        _fl = _rl = left >= 0.8 ? Math.Clamp(compression + transfer, 0, 1) : 0;
        _fr = _rr = right >= 0.8 ? Math.Clamp(compression - transfer, 0, 1) : 0;
    }

    private void ApplyImpact(double severity)
    {
        _impact = Math.Max(_impact, severity);
        _damage = Math.Clamp(_damage + severity * 0.4, 0, 1);
        _cleanRun = false;
        _ghost.InvalidateLap();
    }

    private void Recover()
    {
        _recoveries++;
        _penalties += 5;
        _score = Math.Max(0, _score - 250);
        _cleanRun = false;
        _ghost.InvalidateLap();
        _damage = Math.Min(_damage, 0.5);
        Place(_safeS);
        _lastStunt = "RECOVERY — active stunt cancelled; -250 / +5s; clean ghost invalid";
        Say(_lastStunt, 4);
        if (_trial is not null && _trial.Expired(RunTime))
            Finish(true);
    }

    private void Place(double s)
    {
        _s = _surface.Wrap(s);
        _q = 0.5;
        var road = _surface.At(_s, _q);
        _position = road.Center;
        _up = road.Normal;
        _forward = road.Along.OnPlane(_up).Unit;
        _velocity = _angularVelocity = default;
        _onRoad = true;
        _onFloor = _boosting = _loopArmed = _jumpInFlight = _jumpCounted = false;
        _airTime = _floorTime = _steer = 0;
        _normalLoad = Math.Max(0, -Dot(Gravity, _up));
        _yaw = _pitch = _roll = 0;
        UpdateEuler();
        UpdateSuspension(road);
        _previous = Pose();
    }

    private void Reset(bool countdown = true)
    {
        _clock.Reset();
        _runTicks = 0;
        _score = _loops = _jumps = _landings = _gate = _recoveries = 0;
        _impact = _safeS = _penalties = _totalAirTime = _loopTurn = 0;
        _boost = 1;
        _damage = 0;
        _loopInverted = _loopAwarded = _cleanGap = _recordedBest = false;
        _cleanRun = true;
        _success = null;
        _lastStunt = "No stunt yet";
        _airOrigin = "";
        _phase = countdown ? "countdown" : "ready";
        _countdown = countdown ? 3 : 0;
        _trial = _hardDriving ? new(_course) : null;
        _ghost.ResetCurrent();
        Place(0);
        Say(!countdown ? "Garage — ready; recorded best retained"
            : _hardDriving ? "Get ready — timed loop / flyover / ramp proving ground"
            : "Get ready — loop → banked bend → bridge → clean gap → finish", 3);
    }

    private void Finish(bool timedOut = false)
    {
        _success = !timedOut && _loops > 0 && (_hardDriving ? _jumps > 0 : _cleanGap);
        _trial?.Finish(_success.Value);
        if (_success == true && _cleanRun)
        {
            _recordedBest = _bestTime == 0 || Elapsed < _bestTime;
            if (_recordedBest)
                _bestTime = Elapsed;
            _bestScore = Math.Max(_bestScore, _score);
            _ghost.CompleteLap(Elapsed, Pose());
        }
        else
        {
            _ghost.InvalidateLap();
            _ghost.CompleteLap(Elapsed, Pose());
        }
        _phase = "finished";
        Say(timedOut ? "TIME EXPIRED — Hard Drivin' proving ground"
            : _hardDriving ? _success == true ? "PROVING GROUND COMPLETE — loop and all timed checkpoints!"
                : "Route finished — full loop AND ramp jump required"
            : _success == true ? "STUNT CIRCUIT COMPLETE — full loop and clean gap!"
            : "Circuit finished — full loop AND clean gap landing required", 10);
        _boosting = false;
        _previous = Pose();
    }

    private string Hint() => _hardDriving ? HardDrivingHint() : _course.FeatureAt(_s) switch
    {
        "Loop approach" => "LOOP AHEAD — build 35–40 m/s; keep straight; boost is optional",
        "Vertical loop" => "VERTICAL LOOP — hold the line; contact needs speed",
        "Loop runout" => "BRAKE for the banked right turn — aim for 20–23 m/s",
        "Banked turn" or "Return banking" => "BANKED RIGHT — steer through the bend; no auto-driver",
        "Bridge climb" or "Precision bridge" => "PRECISION BRIDGE — narrow road; aim for 18–22 m/s",
        "Bridge descent" or "Ramp approach" => "GAP AHEAD — line up at 26–30 m/s",
        "Gap ramp" or "Open gap" => "RAMP / OPEN GAP — real takeoff; stay aligned for landing",
        "Landing runway" => "LANDING RUNWAY — brake for the return bend",
        _ => "FINISH — loop + clean gap + all route gates"
    };

    private string HardDrivingHint() => _course.FeatureAt(_s) switch
    {
        "Speed runway" => "SPEED RUNWAY — build 38–42 m/s for the full loop; checkpoint clock is live",
        "Proving-ground loop" => "PROVING LOOP — real 360° contact; keep speed and hold your line",
        "Fast loop exit" => "LOOP EXIT — brake to 28–30 m/s for the banked right turn",
        "North banking" or "East banking" or "South banking" or "West banking" =>
            "HIGH-SPEED BANKING — steer through the right-hand corner; stay on the road",
        "Flyover climb" or "Precision flyover" or "Flyover descent" =>
            "PRECISION FLYOVER — 8.4 m bridge; aim for 23–25 m/s",
        "East straight" or "Ramp runway" => "JUMP RUNWAY — line up at 30–32 m/s; checkpoints add time",
        "Jump ramp" or "Proving gap" => "RAMP / 22 m OPEN GAP — no invisible bridge",
        "Landing bridge" or "Bridge runout" => "LANDING BRIDGE — settle the car before the south banking",
        _ => "HOME SPRINT — beat the clock through the remaining ordered checkpoints"
    };

    private void Say(string text, double seconds)
    {
        _message = text;
        _messageTime = seconds;
    }

    private void UpdateEuler()
    {
        var right = Right;
        var pitch = Math.Asin(Math.Clamp(_forward.Y, -1, 1));
        var yaw = Math.Atan2(_forward.X, _forward.Z);
        var roll = Math.Atan2(right.Y, _up.Y);
        var a = new StuntVector(NearAngle(yaw, _yaw), NearAngle(pitch, _pitch), NearAngle(roll, _roll));
        var b = new StuntVector(NearAngle(yaw + Math.PI, _yaw), NearAngle(Math.PI - pitch, _pitch),
            NearAngle(roll + Math.PI, _roll));
        var old = new StuntVector(_yaw, _pitch, _roll);
        var chosen = (a - old).LengthSquared <= (b - old).LengthSquared ? a : b;
        _yaw = chosen.X;
        _pitch = chosen.Y;
        _roll = chosen.Z;
    }

    private static double NearAngle(double angle, double near) =>
        near + Math.IEEERemainder(angle - near, Math.Tau);

    private GhostPose Pose() => new(_position.X, _position.Y, _position.Z, _yaw, _pitch, _roll,
        Dot(_velocity, _forward), _steer, _boost, _damage, _onRoad || _onFloor, _boosting,
        _fl, _fr, _rl, _rr);

    private GameSnapshot CreateSnapshot(double alpha) => new(
        GhostPose.Interpolate(_previous, Pose(), alpha), null, _phase, RunTime, 1, 1, RunTime, _bestTime,
        _s / Track.Length, _countdown, _impact, _recoveries, alpha, _clock.Tick,
        _phase == "paused" ? "Paused — stunt physics and score frozen" : _message,
        Trial: _trial?.Snapshot(RunTime), Ghost: _ghost.Snapshot(Elapsed));
}
