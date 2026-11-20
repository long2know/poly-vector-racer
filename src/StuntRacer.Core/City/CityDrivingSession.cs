using StuntRacer.Engine;

namespace StuntRacer.Core.City;

/// <summary>Original city geometry and street activities, driven by the unchanged Modern tire/spring equations.</summary>
public sealed class CityDrivingSession : IGameSession
{
    private const double RecoveryPenalty = 5;
    private readonly FixedStepClock _clock = new();
    private readonly CitySurface _surface;
    private readonly GameSimulation _physics;
    private readonly GameSimulation.Racer _player = new();
    private readonly CityRouteDefinition? _route;
    private readonly CityPath _circuit;
    private readonly CityPath _blitz;
    private readonly DriverActor[] _traffic;
    private readonly WalkingActor[] _walkers;
    private readonly DriverActor? _rival;
    private readonly StreetProgress _playerProgress = new();
    private readonly StreetProgress _rivalProgress = new();
    private readonly string _mode;
    private DriverInput _input;
    private string _phase = "ready";
    private string _pausedPhase = "racing";
    private string _message = "";
    private double _messageSeconds;
    private double _countdown;
    private double _distance;
    private double _deadline;
    private long _raceTicks;
    private bool? _success;
    private CityPoint _safePoint;
    private double _safeHeading;

    public CityDrivingSession(string mode = "free-roam")
    {
        ArgumentNullException.ThrowIfNull(mode);
        _mode = mode.Trim().ToLowerInvariant();
        if (_mode is not ("free-roam" or "city-blitz" or "city-circuit"))
            throw new ArgumentException("City mode must be free-roam, city-blitz or city-circuit.", nameof(mode));
        World = CityWorldDefinition.Create();
        _surface = new CitySurface(World);
        var circuit = World.Routes.Single(r => r.Id == "city-circuit");
        var blitz = World.Routes.Single(r => r.Id == "city-blitz");
        _circuit = new CityPath(circuit.Points, true);
        _blitz = new CityPath(blitz.Points, false);
        Track = _circuit.Guide(circuit.Name);
        _physics = new GameSimulation(Track, _surface);
        _route = _mode == "city-circuit" ? circuit : _mode == "city-blitz" ? blitz : null;
        _traffic = CreateTraffic();
        _walkers = CreateWalkers();
        _rival = _mode == "city-circuit"
            ? new DriverActor("street-rival", "rival-cyan", _circuit, 3, 21, 0) : null;
        Reset(false);
    }

    public TrackDefinition Track { get; }
    public CityWorldDefinition World { get; }
    public GameSnapshot Snapshot => CreateSnapshot(1);

    public CityActivity Activity
    {
        get
        {
            var car = _player.Car;
            var road = _surface.RoadAt(car.X, car.Z);
            var area = World.Areas.FirstOrDefault(a => a.Kind != "water" && CitySurface.Contains(a.Points, car.X, car.Z));
            var traffic = new CityTrafficSnapshot[_traffic.Length];
            for (var i = 0; i < traffic.Length; i++)
                traffic[i] = new CityTrafficSnapshot(_traffic[i].Id, _traffic[i].Style,
                    GameSimulation.CarSnapshot(_traffic[i].Racer, 1));
            var walkers = new WalkerSnapshot[_walkers.Length];
            for (var i = 0; i < walkers.Length; i++)
            {
                var walker = _walkers[i];
                var pose = WalkerPose(walker);
                walkers[i] = new WalkerSnapshot(walker.Id, pose.Point.X, pose.Point.Y, pose.Point.Z,
                    pose.Heading, walker.Phase);
            }
            return new CityActivity(_mode, _distance, road?.Name ?? area?.Name ?? "Open ground",
                _surface.DistrictAt(car.X, car.Z), traffic, walkers,
                _route?.Id, _route?.Checkpoints ?? [], _playerProgress.Next,
                _playerProgress.Passed, (_route?.Checkpoints.Length ?? 0) * (_route?.Laps ?? 0),
                _playerProgress.Lap, _route?.Laps ?? 0, TimeRemaining, RaceTime(_player),
                _success is null ? _phase : _success.Value ? _mode == "city-blitz" ? "completed" : "player-won"
                    : _mode == "city-blitz" ? "timeout" : "rival-won", _success);
        }
    }

    public GameSnapshot Advance(double seconds, DriverInput input)
    {
        FixedStepClock.ValidateDelta(seconds);
        if (!double.IsFinite(input.Steer) || !double.IsFinite(input.Throttle) || !double.IsFinite(input.Brake))
            throw new ArgumentOutOfRangeException(nameof(input), "Driver axes must be finite.");
        _input = new DriverInput(Math.Clamp(input.Steer, -1, 1), Math.Clamp(input.Throttle, 0, 1),
            Math.Clamp(input.Brake, 0, 1), input.Boost);
        if (_phase is "ready" or "paused" or "finished")
            return Snapshot;
        _clock.Advance(seconds, TryStep);
        return CreateSnapshot(_phase == "finished" ? 1 : _clock.Alpha);
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                RequirePhase(command, "ready", "finished");
                Reset(true);
                break;
            case "restart":
                Reset(true);
                break;
            case "garage":
                Reset(false);
                break;
            case "pause":
                RequirePhase(command, "countdown", "racing");
                _pausedPhase = _phase;
                _phase = "paused";
                _message = "Paused — city simulation frozen";
                break;
            case "resume":
                RequirePhase(command, "paused");
                _phase = _pausedPhase;
                SetMessage(_phase == "countdown" ? "Get ready" : RunningMessage(), 0);
                break;
            case "recover":
                RequirePhase(command, "racing");
                RecoverPlayer("Recovered");
                break;
            case "ghost-on":
            case "ghost-off":
                throw new NotSupportedException("City sessions have live traffic and a physical street rival; recorded lap ghosts are unavailable.");
            default:
                throw new ArgumentException($"Unknown city command: {command}", nameof(command));
        }
    }

    private double RaceTime(GameSimulation.Racer racer) => _raceTicks * _clock.StepSeconds + racer.Penalties;
    private double TimeRemaining => _mode == "city-blitz" ? Math.Max(0, _deadline - RaceTime(_player)) : 0;

    private bool TryStep(double dt)
    {
        if (_phase is not ("countdown" or "racing"))
            return false;
        if (_phase == "countdown")
        {
            _countdown = Math.Max(0, _countdown - dt);
            if (_countdown < 1e-9)
            {
                _countdown = 0;
                _phase = "racing";
                SetMessage(RunningMessage(), 0);
            }
            return true;
        }
        _raceTicks++;
        _messageSeconds = Math.Max(0, _messageSeconds - dt);
        if (_messageSeconds == 0)
            _message = RunningMessage();
        BeginStep(_player, dt);
        foreach (var actor in _traffic)
            BeginStep(actor.Racer, dt);
        if (_rival is not null)
            BeginStep(_rival.Racer, dt);
        _physics.IntegrateVehicle(_player, dt, _input);
        foreach (var actor in _traffic)
            Drive(actor, dt);
        if (_rival is not null)
            Drive(_rival, dt);
        var hit = CityCollisions.ResolveWorld(_player, World);
        if (hit is not null && _player.Impact > 0.05)
            SetMessage($"Impact — {hit}", 2);
        foreach (var actor in _traffic)
            CityCollisions.ResolveWorld(actor.Racer, World);
        if (_rival is not null)
            CityCollisions.ResolveWorld(_rival.Racer, World);
        ResolveTraffic();
        CityCollisions.ResolveWorld(_player, World);
        var dx = _player.Car.X - _player.Previous.X;
        var dz = _player.Car.Z - _player.Previous.Z;
        _distance += Math.Sqrt(dx * dx + dz * dz);
        foreach (var walker in _walkers)
        {
            walker.Distance += walker.Speed * dt;
            walker.Phase = (walker.Phase + walker.Speed * dt * 0.85) % 1;
        }
        if (_player.Car.Y < -4 || _player.Car.Damage >= 1)
        {
            RecoverPlayer(_player.Car.Damage >= 1 ? "Vehicle wrecked" :
                _surface.IsWater(_player.Car.X, _player.Car.Z) ? "River recovery" : "City boundary recovery");
            if (_phase == "finished")
                return true;
        }
        else
            RememberSafeGround();
        var playerFinish = UpdateProgress(_player, _playerProgress);
        var rivalFinish = _rival is null ? null : UpdateProgress(_rival.Racer, _rivalProgress);
        if (_phase == "finished")
            return true;
        if (_mode == "city-blitz")
        {
            if (playerFinish is not null)
                Finish(true);
            else if (TimeRemaining <= 1e-9)
                Finish(false);
        }
        else if (_rival is not null && (playerFinish is not null || rivalFinish is not null))
            Finish(playerFinish.HasValue && (!rivalFinish.HasValue || playerFinish.Value <= rivalFinish.Value));
        return true;
    }

    private static void BeginStep(GameSimulation.Racer racer, double dt)
    {
        racer.Previous = racer.Car;
        racer.Impact *= Math.Exp(-3.5 * dt);
    }

    private void Drive(DriverActor actor, double dt)
    {
        var car = actor.Racer.Car;
        var s = actor.Path.Nearest(car.X, car.Z, actor.PathS);
        actor.PathS = s;
        var forward = car.Vx * Math.Sin(car.Yaw) + car.Vz * Math.Cos(car.Yaw);
        var speed = Math.Abs(forward);
        var look = 10 + speed * 0.55;
        var target = actor.Path.At(s + look, actor.Lane);
        var heading = actor.Path.At(s).Heading;
        var ahead = actor.Path.At(s + 45).Heading;
        var bend = Math.Abs(Angle(ahead - heading));
        var desired = bend > 0.10 ? Math.Min(actor.CruiseSpeed, 10.5) : actor.CruiseSpeed;
        var error = Angle(Math.Atan2(target.Point.X - car.X, target.Point.Z - car.Z) - car.Yaw);
        var rate = 2 * Math.Max(speed, 7) * Math.Sin(error) / look;
        var scale = Math.Clamp(forward / 12, -1, 1) * (1.35 / (1 + speed / 13));
        var steer = Math.Abs(scale) < 0.03 ? 0 : Math.Clamp(rate / scale, -1, 1);
        if (Math.Abs(error) > 0.6)
            desired = Math.Min(desired, 7);
        if (ReferenceEquals(actor, _rival))
        {
            var gapX = _player.Car.X - car.X;
            var gapZ = _player.Car.Z - car.Z;
            if (gapX * Math.Sin(car.Yaw) + gapZ * Math.Cos(car.Yaw) is > 0 and < 11 &&
                Math.Abs(gapX * Math.Cos(car.Yaw) - gapZ * Math.Sin(car.Yaw)) < 2.4)
                target = actor.Path.At(s + look, -3);
            error = Angle(Math.Atan2(target.Point.X - car.X, target.Point.Z - car.Z) - car.Yaw);
            rate = 2 * Math.Max(speed, 7) * Math.Sin(error) / look;
            steer = Math.Abs(scale) < 0.03 ? 0 : Math.Clamp(rate / scale, -1, 1);
        }
        else
        {
            foreach (var other in _traffic)
            {
                if (ReferenceEquals(other, actor))
                    continue;
                var gapX = other.Racer.Car.X - car.X;
                var gapZ = other.Racer.Car.Z - car.Z;
                var along = gapX * Math.Sin(car.Yaw) + gapZ * Math.Cos(car.Yaw);
                var across = gapX * Math.Cos(car.Yaw) - gapZ * Math.Sin(car.Yaw);
                if (along is > 0 and < 13 && Math.Abs(across) < 2.6 && Math.Abs(other.Racer.Car.Y - car.Y) < 2)
                    desired = Math.Min(desired, Math.Max(0, (along - 5) * 1.4));
            }
        }
        actor.StuckSeconds = speed < 0.6 ? actor.StuckSeconds + dt : Math.Max(0, actor.StuckSeconds - dt);
        DriverInput input;
        if (actor.StuckSeconds > 3 && actor.StuckSeconds < 5)
            input = new DriverInput(-steer, 0, 0.6, false);
        else
        {
            if (actor.StuckSeconds >= 5)
                actor.StuckSeconds = 0;
            input = new DriverInput(steer, Math.Clamp((desired - forward) * 0.7, 0, 1),
                Math.Clamp((forward - desired - 0.5) * 0.4, 0, 1), false);
        }
        _physics.IntegrateVehicle(actor.Racer, dt, input);
        if (actor.Racer.Car.Y < -4 || actor.Racer.Car.Damage >= 1)
        {
            var boost = actor.Racer.Car.Boost;
            var damage = Math.Min(actor.Racer.Car.Damage, 0.3);
            Place(actor.Racer, actor.Path, actor.PathS - 10, actor.Lane, boost, damage);
            actor.Racer.Recoveries++;
            actor.Racer.Penalties += RecoveryPenalty;
        }
    }

    private void ResolveTraffic()
    {
        for (var i = 0; i < _traffic.Length; i++)
        {
            var actor = _traffic[i].Racer;
            var damage = _player.Car.Damage;
            GameSimulation.ResolveCarContact(_player, actor);
            if (_player.Car.Damage > damage)
                SetMessage("Traffic impact — both vehicles take contact", 2);
            if (_rival is not null)
                GameSimulation.ResolveCarContact(_rival.Racer, actor);
            for (var j = i + 1; j < _traffic.Length; j++)
                GameSimulation.ResolveCarContact(actor, _traffic[j].Racer);
        }
        if (_rival is not null)
            GameSimulation.ResolveCarContact(_player, _rival.Racer);
    }

    private double? UpdateProgress(GameSimulation.Racer racer, StreetProgress progress)
    {
        if (_route is null)
            return null;
        var gate = _route.Checkpoints[progress.Next];
        var crossing = CrossGate(racer.Previous, racer.Car, gate);
        if (crossing is null)
            return null;
        var crossingTime = RaceTime(racer) - _clock.StepSeconds + crossing.Value * _clock.StepSeconds;
        if (_mode == "city-blitz" && crossingTime > _deadline + 1e-9)
        {
            Finish(false);
            return null;
        }
        progress.Passed++;
        progress.Next++;
        if (ReferenceEquals(racer, _player))
        {
            if (_mode == "city-blitz" && progress.Next < _route.Checkpoints.Length)
                _deadline += 8;
            SetMessage($"{gate.Name} — checkpoint {progress.Passed}" +
                (_mode == "city-blitz" && progress.Next < _route.Checkpoints.Length ? " (+8 s)" : ""), 2);
        }
        if (progress.Next < _route.Checkpoints.Length)
            return null;
        var lapTime = crossingTime - progress.LapStartedAt;
        if (progress.BestLap == 0 || lapTime < progress.BestLap)
            progress.BestLap = lapTime;
        if (progress.Lap == _route.Laps)
        {
            progress.Next = _route.Checkpoints.Length - 1;
            return crossing;
        }
        progress.Lap++;
        progress.Next = 0;
        progress.LapStartedAt = crossingTime;
        return null;
    }

    private static double? CrossGate(in GameSimulation.Vehicle a, in GameSimulation.Vehicle b,
        CityCheckpointDefinition gate)
    {
        var fx = Math.Sin(gate.Heading);
        var fz = Math.Cos(gate.Heading);
        var before = (a.X - gate.X) * fx + (a.Z - gate.Z) * fz;
        var after = (b.X - gate.X) * fx + (b.Z - gate.Z) * fz;
        if (before >= 0 || after < 0 || after <= before)
            return null;
        var t = -before / (after - before);
        var x = a.X + (b.X - a.X) * t - gate.X;
        var z = a.Z + (b.Z - a.Z) * t - gate.Z;
        var y = a.Y + (b.Y - a.Y) * t;
        if (Math.Abs(x * fz - z * fx) > gate.Width / 2 || Math.Abs(y - gate.Y - 1) > 4)
            return null;
        return t;
    }

    private void RememberSafeGround()
    {
        ref var car = ref _player.Car;
        var road = _surface.RoadAt(car.X, car.Z);
        if (road is null || !car.Grounded || Math.Abs(car.Roll) > 0.6 || Math.Abs(car.Pitch) > 0.8)
            return;
        var sample = _surface.Sample(car.X, car.Z, car.Y);
        if (!sample.Road || Math.Abs(sample.Lateral) > road.Width / 2 - 2 || Math.Abs(car.Y - sample.Y - 1) > 0.5)
            return;
        _safePoint = new CityPoint(car.X, sample.Y, car.Z);
        _safeHeading = sample.Heading;
        if (Math.Cos(car.Yaw - _safeHeading) < 0)
            _safeHeading = Angle(_safeHeading + Math.PI);
    }

    private void RecoverPlayer(string reason)
    {
        var point = _safePoint;
        for (var back = 0; back <= 24; back += 8)
        {
            var candidate = new CityPoint(_safePoint.X - Math.Sin(_safeHeading) * back, _safePoint.Y,
                _safePoint.Z - Math.Cos(_safeHeading) * back);
            var surface = _surface.Sample(candidate.X, candidate.Z, double.MaxValue);
            if (!surface.Road || surface.Width <= 0 ||
                _traffic.Any(a => Squared(a.Racer.Car.X - candidate.X, a.Racer.Car.Z - candidate.Z) < 30) ||
                _rival is not null && Squared(_rival.Racer.Car.X - candidate.X, _rival.Racer.Car.Z - candidate.Z) < 30)
                continue;
            point = candidate;
            break;
        }
        PlaceAt(_player, point, _safeHeading, _player.Car.Boost, Math.Min(_player.Car.Damage, 0.45));
        _player.Recoveries++;
        if (_route is not null)
            _player.Penalties += RecoveryPenalty;
        SetMessage(_route is null ? $"{reason} — back on the last safe street" : $"{reason} — 5 second penalty", 3);
        if (_mode == "city-blitz" && TimeRemaining <= 1e-9)
            Finish(false);
    }

    private void Reset(bool countdown)
    {
        _clock.Reset();
        _raceTicks = 0;
        _distance = 0;
        _deadline = _route?.TimeLimit ?? 0;
        _success = null;
        _countdown = countdown ? 3 : 0;
        _phase = countdown ? "countdown" : "ready";
        _messageSeconds = 0;
        _input = default;
        _player.Penalties = _player.Impact = 0;
        _player.Recoveries = 0;
        _playerProgress.Reset();
        _rivalProgress.Reset();
        var spawnPath = _mode == "city-blitz" ? _blitz : _circuit;
        Place(_player, spawnPath, 0, _rival is null ? 4.5 : -3, 1, 0);
        _safePoint = new CityPoint(_player.Car.X, _player.Car.Y - 1, _player.Car.Z);
        _safeHeading = _player.Car.Yaw;
        foreach (var actor in _traffic)
            ResetActor(actor);
        if (_rival is not null)
            ResetActor(_rival);
        foreach (var walker in _walkers)
        {
            walker.Distance = walker.SpawnDistance;
            walker.Phase = walker.SpawnDistance % 1;
        }
        SetMessage(countdown ? "Get ready — the city starts with you" : "Ready — " + RunningMessage(), 0);
    }

    private void ResetActor(DriverActor actor)
    {
        actor.PathS = actor.SpawnDistance;
        actor.StuckSeconds = 0;
        actor.Racer.Penalties = actor.Racer.Impact = 0;
        actor.Racer.Recoveries = 0;
        Place(actor.Racer, actor.Path, actor.SpawnDistance, actor.Lane, 1, 0);
    }

    private void Place(GameSimulation.Racer racer, CityPath path, double s, double lane, double boost, double damage)
    {
        var pose = path.At(s, lane);
        PlaceAt(racer, pose.Point, pose.Heading, boost, damage);
    }

    private void PlaceAt(GameSimulation.Racer racer, CityPoint point, double heading, double boost, double damage)
    {
        var surface = _surface.Sample(point.X, point.Z, double.MaxValue);
        racer.Car = new GameSimulation.Vehicle
        {
            X = point.X, Y = surface.Y + 1, Z = point.Z, Yaw = heading, Boost = boost, Damage = damage,
            Pitch = Math.Atan(surface.SlopeX * Math.Sin(heading) + surface.SlopeZ * Math.Cos(heading)),
            Roll = Math.Atan(surface.SlopeX * Math.Cos(heading) - surface.SlopeZ * Math.Sin(heading)),
            Grounded = true, FL = 0.5, FR = 0.5, RL = 0.5, RR = 0.5
        };
        racer.Previous = racer.Car;
    }

    private void Finish(bool success)
    {
        _success = success;
        _phase = "finished";
        SetMessage(_mode == "city-blitz"
            ? success ? "CITY BLITZ COMPLETE — all six street checkpoints" : "TIME EXPIRED — city blitz failed"
            : success ? "CITY CIRCUIT WON — two real street laps" : "RIVAL WON — two real street laps", 0);
        Stop(_player);
        foreach (var actor in _traffic)
            Stop(actor.Racer);
        if (_rival is not null)
            Stop(_rival.Racer);
    }

    private static void Stop(GameSimulation.Racer racer)
    {
        racer.Car.Vx = racer.Car.Vy = racer.Car.Vz = 0;
        racer.Car.Boosting = false;
        racer.Previous = racer.Car;
    }

    private GameSnapshot CreateSnapshot(double alpha)
    {
        RaceSnapshot? race = null;
        if (_rival is not null)
        {
            var playerAhead = _playerProgress.Passed > _rivalProgress.Passed ||
                _playerProgress.Passed == _rivalProgress.Passed && RemainingGateDistance(_player, _playerProgress) <=
                RemainingGateDistance(_rival.Racer, _rivalProgress);
            race = new RaceSnapshot("city-circuit", _success is null ? playerAhead ? 1 : 2 : _success.Value ? 1 : 2,
                2, _rivalProgress.Lap, _success, _success is null ? _phase : _success.Value ? "player-won" : "rival-won",
                _rival.PathS / _circuit.Length, RaceTime(_rival.Racer), _rival.Racer.Recoveries, _raceTicks * _clock.StepSeconds);
        }
        TrialSnapshot? trial = _mode == "city-blitz" ? new TrialSnapshot(TimeRemaining, _playerProgress.Passed,
            _route!.Checkpoints.Length, (double)_playerProgress.Next / _route.Checkpoints.Length,
            _playerProgress.Passed > 0 && _playerProgress.Passed < _route.Checkpoints.Length ? 8 : 0,
            _success, RaceTime(_player)) : null;
        return new GameSnapshot(GameSimulation.CarSnapshot(_player, alpha),
            _rival is null ? null : GameSimulation.CarSnapshot(_rival.Racer, alpha),
            _phase, RaceTime(_player), _playerProgress.Lap, _route?.Laps ?? 0,
            RaceTime(_player) - _playerProgress.LapStartedAt, _playerProgress.BestLap,
            _route is null ? 0 : (double)_playerProgress.Passed / (_route.Checkpoints.Length * _route.Laps),
            _countdown, _player.Impact, _player.Recoveries, alpha, _clock.Tick, _message, race, trial);
    }

    private double RemainingGateDistance(GameSimulation.Racer racer, StreetProgress progress)
    {
        var gate = _route!.Checkpoints[progress.Next];
        return Squared(racer.Car.X - gate.X, racer.Car.Z - gate.Z);
    }

    private string RunningMessage() => _mode switch
    {
        "city-blitz" => "City blitz — six ordered street gates; +8 seconds per checkpoint",
        "city-circuit" => "City circuit — beat the cyan driver over two street laps",
        _ => "Free roam — explore the streets, park, riverfront and Meridian Bridge"
    };

    private void SetMessage(string message, double duration)
    {
        _message = message;
        _messageSeconds = duration;
    }

    private void RequirePhase(string command, params string[] phases)
    {
        if (!phases.Contains(_phase))
            throw new InvalidOperationException($"Cannot {command} a city session while {_phase}.");
    }

    private static double Angle(double angle) => Math.IEEERemainder(angle, Math.Tau);
    private static double Squared(double x, double z) => x * x + z * z;

    private DriverActor[] CreateTraffic()
    {
        static CityPoint P(double x, double z, double y = 0.04) => new(x, y, z);
        var foundry = CityPath.Round([P(-640, -520), P(-640, -160), P(-400, -160), P(-400, -640), P(-640, -640)], true, 35);
        var park = CityPath.Round([P(-400, 180), P(-400, 560), P(-160, 560), P(-160, 80), P(-400, 80)], true, 35);
        var north = CityPath.Round([P(80, 420), P(80, 560), P(400, 560), P(400, 320), P(80, 320)], true, 35);
        var bridge = CityPath.Round(
            [P(400, -160), P(400, 80), P(520, 80, 20), P(600, 80, 20), P(780, 80),
             P(780, 560), P(870, 560), P(870, -160), P(780, -160), P(780, 80),
             P(600, 80, 20), P(520, 80, 20), P(400, 80), P(400, 320), P(80, 320), P(80, -160)],
            true, 35);
        var paths = new[]
        {
            _circuit, new CityPath(World.Routes[0].Points.Reverse().ToArray(), true),
            new CityPath(foundry, true), new CityPath(park, true), new CityPath(north, true), new CityPath(bridge, true)
        };
        var actors = new DriverActor[18];
        string[] styles = ["ochre", "blue", "cream", "red", "slate", "delivery"];
        for (var i = 0; i < actors.Length; i++)
        {
            var path = paths[i % paths.Length];
            var spawn = i == 0 ? 85 : path.Length * ((i / paths.Length + 0.15) / 3);
            actors[i] = new DriverActor($"traffic-{i:00}", styles[i % styles.Length], path,
                4.6, 11 + i % 4, spawn);
        }
        return actors;
    }

    private WalkingActor[] CreateWalkers()
    {
        var actors = new WalkingActor[26];
        for (var i = 0; i < actors.Length; i++)
        {
            var definition = World.Walkways[i % World.Walkways.Length];
            var points = definition.Points;
            var closed = points[0] == points[^1];
            var path = new CityPath(closed ? points[..^1] : points, closed);
            actors[i] = new WalkingActor($"walker-{i:00}", path, closed, 0.85 + i % 5 * 0.11,
                path.Length * (i / 4.0 % 6.5) / 6.5);
        }
        return actors;
    }

    private static (CityPoint Point, double Heading) WalkerPose(WalkingActor actor)
    {
        if (actor.Closed)
            return actor.Path.At(actor.Distance);
        var distance = (actor.Distance % (2 * actor.Path.Length) + 2 * actor.Path.Length) % (2 * actor.Path.Length);
        var reverse = distance > actor.Path.Length;
        var pose = actor.Path.At(reverse ? 2 * actor.Path.Length - distance : distance);
        return (pose.Point, reverse ? Angle(pose.Heading + Math.PI) : pose.Heading);
    }

    private sealed class DriverActor(string id, string style, CityPath path, double lane, double speed, double spawn)
    {
        public readonly string Id = id;
        public readonly string Style = style;
        public readonly CityPath Path = path;
        public readonly GameSimulation.Racer Racer = new();
        public readonly double Lane = lane;
        public readonly double CruiseSpeed = speed;
        public readonly double SpawnDistance = spawn;
        public double PathS, StuckSeconds;
    }

    private sealed class WalkingActor(string id, CityPath path, bool closed, double speed, double spawn)
    {
        public readonly string Id = id;
        public readonly CityPath Path = path;
        public readonly bool Closed = closed;
        public readonly double Speed = speed;
        public readonly double SpawnDistance = spawn;
        public double Distance, Phase;
    }

    private sealed class StreetProgress
    {
        public int Next, Passed;
        public int Lap = 1;
        public double LapStartedAt, BestLap;

        public void Reset()
        {
            Next = Passed = 0;
            Lap = 1;
            LapStartedAt = BestLap = 0;
        }
    }
}
