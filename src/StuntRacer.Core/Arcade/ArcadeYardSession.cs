using StuntRacer.Engine;

namespace StuntRacer.Core.Arcade;

public sealed class ArcadeYardSession : IGameSession
{
    private const int TickRate = 120;
    private const int CountdownTicks = 180;
    private const int TimeLimit = 30;
    private const int ScoreGoal = 1000;
    private const int RecoveryCost = 2 * TickRate;
    private const double RailClearance = 2.65;
    private readonly FixedStepClock _clock = new(1.0 / TickRate);
    private readonly GameSimulation _physics;
    private readonly GameSimulation.Racer _player = new();
    private readonly Func<double, bool> _step;
    private DriverInput _input;
    private IReadOnlyList<ArcadeYardPropSnapshot> _props = [];
    private string _phase = "ready";
    private string _pausedPhase = "racing";
    private string _cue = "";
    private int _cueUntil;
    private int _elapsedTicks;
    private int _penaltyTicks;
    private int _countdownTicks;
    private int _score;
    private int _propsHit;
    private bool? _success;

    public ArcadeYardSession()
    {
        Track = World.CreatePerimeterTrack();
        _physics = new(Track, new Floor(World), steeringScale: 1.16);
        _step = TryStep;
        Reset(false);
    }

    public TrackDefinition Track { get; }
    public ArcadeYardWorld World => ArcadeYardWorld.Definition;
    public GameSnapshot Snapshot => CreateSnapshot(1);
    public ArcadeYardActivity Activity => new(_score, ScoreGoal, (double)RemainingTicks / TickRate,
        TimeLimit, (double)_elapsedTicks / TickRate, _propsHit, _props.Count, _success, _props);

    private int RemainingTicks => Math.Max(0, TimeLimit * TickRate - _elapsedTicks - _penaltyTicks);

    public GameSnapshot Advance(double seconds, DriverInput input)
    {
        FixedStepClock.ValidateDelta(seconds);
        if (!double.IsFinite(input.Steer) || !double.IsFinite(input.Throttle) || !double.IsFinite(input.Brake))
            throw new ArgumentOutOfRangeException(nameof(input), "Driver axes must be finite.");
        if (_phase is not ("countdown" or "racing"))
            return Snapshot;
        _input = new(Math.Clamp(input.Steer, -1, 1), Math.Clamp(input.Throttle, 0, 1),
            Math.Clamp(input.Brake, 0, 1), input.Boost);
        _clock.Advance(seconds, _step);
        return CreateSnapshot(_phase == "finished" ? 1 : _clock.Alpha);
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                if (_phase is "ready" or "finished")
                    Reset(true);
                break;
            case "restart":
                Reset(true);
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
            default:
                throw new ArgumentException($"Unknown Block Rush command: {command}", nameof(command));
        }
    }

    private bool TryStep(double dt)
    {
        if (_phase == "countdown")
        {
            if (--_countdownTicks == 0)
                _phase = "racing";
            return true;
        }
        if (_phase != "racing")
            return false;

        _elapsedTicks++;
        _player.Previous = _player.Car;
        _player.Impact *= Math.Exp(-5 * dt);
        _physics.IntegrateVehicle(_player, dt, _input);
        KeepInsideRails();
        ScoreContacts();
        if (_score >= ScoreGoal)
            Finish(true);
        else if (RemainingTicks == 0)
            Finish(false);
        return true;
    }

    private void KeepInsideRails()
    {
        var b = World.Bounds;
        ref var car = ref _player.Car;
        var x = Math.Clamp(car.X, b.MinX + RailClearance, b.MaxX - RailClearance);
        var z = Math.Clamp(car.Z, b.MinZ + RailClearance, b.MaxZ - RailClearance);
        if (x == car.X && z == car.Z)
            return;
        var railSpeed = Math.Abs(x == car.X ? 0 : car.Vx) + Math.Abs(z == car.Z ? 0 : car.Vz);
        if (x != car.X)
            car.Vx *= -0.15;
        if (z != car.Z)
            car.Vz *= -0.15;
        car.X = x;
        car.Z = z;
        if (railSpeed > 2)
        {
            SetCue("Soft rail — steer away or recover");
            _player.Impact = Math.Max(_player.Impact, 0.16);
        }
    }

    private void ScoreContacts()
    {
        // Three swept disks approximate the footprint; they sample both bumpers without taxing the tire solver.
        var before = _player.Previous;
        var car = _player.Car;
        var bumperY = Math.Min(before.Y, car.Y) - World.GroundY - 0.65;
        var center = new Sweep(before.X, before.Z, car.X, car.Z);
        const double bumper = 1.15;
        var previousX = bumper * Math.Sin(before.Yaw);
        var previousZ = bumper * Math.Cos(before.Yaw);
        var currentX = bumper * Math.Sin(car.Yaw);
        var currentZ = bumper * Math.Cos(car.Yaw);
        var nose = new Sweep(before.X + previousX, before.Z + previousZ, car.X + currentX, car.Z + currentZ);
        var tail = new Sweep(before.X - previousX, before.Z - previousZ, car.X - currentX, car.Z - currentZ);
        ArcadeYardPropSnapshot[]? changed = null;
        var points = 0;
        for (var i = 0; i < _props.Count; i++)
        {
            if (_props[i].Hit)
                continue;
            var prop = World.Props[i];
            if (bumperY > prop.Size)
                continue;
            var radius = 1.04 + prop.Size / 2;
            if (!center.Touches(prop, radius) && !nose.Touches(prop, radius) && !tail.Touches(prop, radius))
                continue;
            changed ??= _props.ToArray();
            changed[i] = _props[i] with { HitTick = _elapsedTicks };
            points += prop.Points;
            _propsHit++;
        }
        if (changed is null)
            return;
        _props = Array.AsReadOnly(changed);
        _score += points;
        _player.Impact = Math.Max(_player.Impact, 0.2);
        SetCue($"+{points} — light cubes popped");
    }

    private void SetCue(string text)
    {
        _cue = text;
        _cueUntil = _elapsedTicks + TickRate;
    }

    private void Recover()
    {
        _player.Recoveries++;
        _penaltyTicks += RecoveryCost;
        if (RemainingTicks == 0)
        {
            Finish(false);
            return;
        }
        PlaceCar(_player.Car.Boost);
        SetCue("Back at launch — 2 seconds spent");
    }

    private void PlaceCar(double boost)
    {
        _player.Previous = _player.Car = new GameSimulation.Vehicle
        {
            X = World.Spawn.X, Y = World.GroundY + 1, Z = World.Spawn.Z,
            Boost = boost, Grounded = true, FL = 0.5, FR = 0.5, RL = 0.5, RR = 0.5
        };
        _player.Impact = 0;
    }

    private void Reset(bool countdown)
    {
        _clock.Reset();
        _phase = countdown ? "countdown" : "ready";
        _countdownTicks = countdown ? CountdownTicks : 0;
        _elapsedTicks = _penaltyTicks = _score = _propsHit = _cueUntil = _player.Recoveries = 0;
        _success = null;
        _props = Array.AsReadOnly(World.Props.Select(prop => new ArcadeYardPropSnapshot(prop.Id, prop.X, prop.Z, null)).ToArray());
        PlaceCar(1);
    }

    private void Finish(bool success)
    {
        _phase = "finished";
        _success = success;
        _player.Car.Vx = _player.Car.Vy = _player.Car.Vz = 0;
        _player.Car.Boosting = false;
    }

    private GameSnapshot CreateSnapshot(double alpha)
    {
        var elapsed = (double)_elapsedTicks / TickRate;
        var message = _phase switch
        {
            "ready" => "Block Rush — pop light cubes before time runs out",
            "countdown" => "Get ready",
            "paused" => "Block Rush paused",
            "finished" => _success is true ? "Score goal reached!" : "Time's up — try another route",
            _ => _elapsedTicks < _cueUntil ? _cue : "Pop light cubes — pick a branch"
        };
        return new(GameSimulation.CarSnapshot(_player, alpha), null, _phase, elapsed, 0, 0, 0, 0, 0,
            (double)_countdownTicks / TickRate, _player.Impact, _player.Recoveries, alpha, _clock.Tick, message);
    }

    private readonly record struct Sweep(double X0, double Z0, double X1, double Z1)
    {
        public bool Touches(ArcadeYardProp prop, double radius)
        {
            var dx = X1 - X0;
            var dz = Z1 - Z0;
            var lengthSquared = dx * dx + dz * dz;
            var x = prop.X - X0;
            var z = prop.Z - Z0;
            var t = lengthSquared > 0 ? Math.Clamp((x * dx + z * dz) / lengthSquared, 0, 1) : 0;
            x -= dx * t;
            z -= dz * t;
            return x * x + z * z <= radius * radius;
        }
    }

    private sealed class Floor(ArcadeYardWorld world) : IOpenDrivingSurface
    {
        public RoadSample Sample(double x, double z, double carY) =>
            new(0, x, world.GroundY, z, 0, 0, 0, 0,
                x >= world.Bounds.MinX && x <= world.Bounds.MaxX && z >= world.Bounds.MinZ && z <= world.Bounds.MaxZ, 0, 0);
    }
}
