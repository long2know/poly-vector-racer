using StuntRacer.Engine;

namespace StuntRacer.Core.GeographicDrive;

public sealed class SeattleDrivingSession : IGameSession
{
    private const double StreetSteeringScale = 1.65;
    private readonly FixedStepClock _clock = new();
    private readonly SeattleTerrain _surface;
    private readonly SeattleStreetGraph _streets;
    private readonly GameSimulation _physics;
    private readonly GameSimulation.Racer _player = new();
    private string _phase = "ready";
    private string _status = "manual";
    private string _message = "";
    private DriverInput _input;
    private DriveDestination? _destination;
    private SeattleDriveRoute? _route;
    private double _station, _distance, _stillSeconds, _nextReroute;
    private int _revision;
    private bool _auto, _parked, _terrainWaiting;

    public SeattleDrivingSession(string roadsJson, Func<double, double, (double Height, bool IsWater)?> ground)
    {
        ArgumentNullException.ThrowIfNull(roadsJson);
        ArgumentNullException.ThrowIfNull(ground);
        _surface = new(ground);
        _streets = new(roadsJson, _surface);
        // The game-session adapter requires a track; Seattle contacts, progress and rendering never use it.
        Track = TrackDefinition.Create();
        _physics = new GameSimulation(Track, _surface, StreetSteeringScale);
        Reset(false);
    }

    public TrackDefinition Track { get; }
    public GameSnapshot Snapshot => CreateSnapshot(1);
    public IReadOnlyList<DriveDestination> Destinations => _streets.Destinations;
    public IEnumerable<double[]> MapStreets => _streets.MapStreets;
    public DrivePlan? Plan => _route?.Plan;
    public DriveActivity Activity => new(_status,
        _terrainWaiting ? "Waiting for required Seattle street terrain." : _message, _auto,
        _destination?.Id, _revision, _route is null ? null : _status == "arrived" ? 0 : _route.Plan.Length - _station,
        _distance, _route is null || _status == "arrived" ? null : _route.At(_station + 35), _terrainWaiting);

    private DrivePoint Position => new(_player.Car.X, _player.Car.Z);
    private double Elapsed => _clock.Tick * _clock.StepSeconds;

    public GameSnapshot Advance(double seconds, DriverInput input)
    {
        FixedStepClock.ValidateDelta(seconds);
        if (!double.IsFinite(input.Steer) || !double.IsFinite(input.Throttle) || !double.IsFinite(input.Brake))
            throw new ArgumentOutOfRangeException(nameof(input), "Driver axes must be finite.");
        if (_phase != "racing") return Snapshot;

        _input = new DriverInput(Math.Clamp(input.Steer, -1, 1), Math.Clamp(input.Throttle, 0, 1),
            Math.Clamp(input.Brake, 0, 1), input.Boost);
        if (_input.Steer != 0 || _input.Throttle != 0 || _input.Brake != 0 || _input.Boost)
        {
            if (_auto) StopAuto("guidance", "Manual control — autopilot disengaged by driver input.");
            _parked = false;
        }
        _clock.Advance(seconds, TryStep);
        return _terrainWaiting ? Snapshot : CreateSnapshot(_clock.Alpha);
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = command.Trim().ToLowerInvariant();
        if (name.StartsWith("destination:", StringComparison.Ordinal))
        {
            var id = name["destination:".Length..];
            _destination = Destinations.SingleOrDefault(destination => destination.Id == id) ??
                throw new ArgumentException("Unknown Seattle Drive destination.", nameof(command));
            _auto = false;
            Replan(false);
            return;
        }
        switch (name)
        {
            case "gps-clear":
                _destination = null;
                _auto = false;
                Replan(false);
                break;
            case "autopilot-on":
                if (_phase != "racing")
                    throw new InvalidOperationException("Start or resume the drive before engaging autopilot.");
                if (_destination is null)
                    throw new InvalidOperationException("Choose a GPS destination before engaging autopilot.");
                if (_terrainWaiting)
                    throw new InvalidOperationException("Seattle street terrain is not available.");
                if (_status == "arrived") break;
                if (!Replan(true)) break;
                _parked = false;
                _auto = true;
                SetStatus("autopilot", $"Autopilot to {_destination.Name} — steer, accelerate or brake to take over.");
                break;
            case "autopilot-off":
                if (_auto) StopAuto("guidance", "Manual control — GPS guidance remains on.");
                break;
            case "start":
                if (_phase == "ready") Reset(true);
                break;
            case "restart":
                Reset(true);
                break;
            case "garage":
                Reset(false);
                break;
            case "pause":
                if (_phase == "racing") _phase = "paused";
                break;
            case "resume":
                if (_phase == "paused") _phase = "racing";
                break;
            case "recover":
                if (_phase != "racing") break;
                PlaceAtSpawn();
                _player.Recoveries++;
                _auto = false;
                _parked = false;
                Replan(false);
                break;
            default:
                throw new ArgumentException($"Unknown Seattle Drive command: {command}", nameof(command));
        }
    }

    private bool TryStep(double dt)
    {
        var before = _player.Car;
        if (_surface.At(before.X, before.Z) is null)
        {
            _terrainWaiting = true;
            return false;
        }

        _surface.Missing = false;
        _player.Previous = before;
        _physics.IntegrateVehicle(_player, dt, _auto ? AutoInput() : _input);
        var ground = _surface.At(_player.Car.X, _player.Car.Z);
        if (_surface.Missing || ground is null)
        {
            _player.Car = before;
            _terrainWaiting = true;
            return false;
        }

        _terrainWaiting = false;
        _player.Impact *= Math.Exp(-3.5 * dt);
        var blocked = ground.Value.IsWater || !_streets.OnStreet(Position);
        if (blocked || _parked)
        {
            // A street boundary is a solid stop at the current position, never a path correction.
            _player.Car.X = before.X;
            _player.Car.Z = before.Z;
            _player.Car.Vx = _player.Car.Vz = 0;
            if (blocked)
            {
                if (_auto) StopAuto("boundary", "Autopilot stopped at the street boundary — take manual control.");
                else if (_status != "boundary")
                    SetStatus("boundary", "End of supported street — back up or recover to the start.");
            }
        }

        var movement = Position.DistanceTo(new(before.X, before.Z));
        _distance += movement;
        if (!blocked && _status == "boundary" && movement > 0.001)
            SetStatus(_route is null ? "manual" : "guidance", "Manual control — back on the supported streets.");
        UpdateNavigation(dt);
        return true;
    }

    private DriverInput AutoInput()
    {
        var route = _route!;
        var car = _player.Car;
        var speed = double.Hypot(car.Vx, car.Vz);
        var forward = car.Vx * Math.Sin(car.Yaw) + car.Vz * Math.Cos(car.Yaw);
        var look = 6.7 + speed * 0.5;
        var target = route.At(_station + look);
        var error = SeattleStreetGraph.Angle(Position.HeadingTo(target) - car.Yaw);
        var curvature = 2 * Math.Sin(error) / Math.Max(4.5, Position.DistanceTo(target));
        var yawAuthority = (1.35 * StreetSteeringScale / (1 + speed / 13)) / Math.Max(12, Math.Abs(forward));
        var steer = Math.Clamp(curvature / yawAuthority, -1, 1);
        var desired = route.SpeedLimit(_station);
        if (Math.Abs(error) > 0.42) desired = Math.Min(desired, 4.6);
        if (route.Plan.Length - _station < 5 && Position.DistanceTo(route.End) > 2.0)
            desired = Math.Max(Math.Abs(error) < 1.1 ? 1.0 : 0, desired);
        return new DriverInput(steer, Math.Clamp((desired - forward) * 0.58, 0, 0.82),
            forward > 0.55 ? Math.Clamp((forward - desired - 0.08) * 0.35, 0, 0.62) : 0, false);
    }

    private void UpdateNavigation(double dt)
    {
        if (_route is null || _status == "arrived") return;
        var before = _station;
        var match = _route.Project(Position, before);
        _station = _auto ? Math.Max(before, match.Station) : match.Station;
        var car = _player.Car;
        var speed = double.Hypot(car.Vx, car.Vz);
        if (Position.DistanceTo(_route.End) < 2.35 && speed < 0.7 &&
            _route.Plan.Length - _station < 2.5)
        {
            _parked = _auto;
            if (_parked) _player.Car.Vx = _player.Car.Vz = 0;
            StopAuto("arrived", $"Arrived — {_destination!.Name} ({_destination.Approach}).");
            return;
        }
        if (_auto)
        {
            _stillSeconds = _station - before > dt * 0.45 ? 0 : _stillSeconds + dt;
            if (match.Error > 14 || _stillSeconds > 5)
                StopAuto("guidance", "Autopilot lost the route — take manual control or re-engage from an aligned street.");
        }
        else if (match.Error > 18 && Elapsed >= _nextReroute)
        {
            _nextReroute = Elapsed + 0.8;
            Replan(false);
        }
    }

    private bool Replan(bool forward)
    {
        _stillSeconds = 0;
        if (_destination is null)
        {
            _route = null;
            _station = 0;
            _revision++;
            SetStatus("manual", "Choose a GPS destination or explore in manual control.");
            return false;
        }
        var points = _streets.Route(Position, _player.Car.Yaw, _destination, forward);
        if (points is null)
        {
            if (forward)
                StopAuto(_route is null ? "unreachable" : "guidance",
                    "No forward autopilot route here — align with a highlighted street and try again.");
            else
            {
                _route = null;
                _station = 0;
                _revision++;
                SetStatus("unreachable", "No connected mapped street route from here — no guidance available.");
            }
            return false;
        }
        _route = new(++_revision, points);
        _station = 0;
        SetStatus("guidance", $"GPS to {_destination.Name} — {_destination.Approach}.");
        if (points.Length != 1) return true;
        if (Position.DistanceTo(_route.End) < 2.35)
            SetStatus("arrived", $"Already at {_destination.Name} ({_destination.Approach}).");
        else if (forward)
            SetStatus("guidance", "Align with the marked stopping point or drive there manually.");
        return false;
    }

    private void StopAuto(string status, string message)
    {
        _auto = false;
        SetStatus(status, message);
    }

    private void SetStatus(string status, string message) => (_status, _message) = (status, message);

    private void Reset(bool start)
    {
        PlaceAtSpawn();
        _clock.Reset();
        _phase = start ? "racing" : "ready";
        _distance = _nextReroute = 0;
        _player.Recoveries = 0;
        _input = default;
        _auto = _parked = _terrainWaiting = false;
        Replan(false);
    }

    private void PlaceAtSpawn()
    {
        var spawn = SeattleDriveArea.Spawn;
        var sample = _surface.At(spawn.X, spawn.Z) ??
            throw new InvalidDataException("Seattle Drive is missing the start terrain.");
        if (sample.IsWater) throw new InvalidDataException("Seattle Drive's start is not on land.");
        _player.Car = _player.Previous = new GameSimulation.Vehicle
        {
            X = spawn.X, Y = sample.Height + 1, Z = spawn.Z, Yaw = SeattleDriveArea.SpawnHeading,
            Grounded = true, Boost = 1, FL = 0.5, FR = 0.5, RL = 0.5, RR = 0.5
        };
        _player.Impact = 0;
    }

    private GameSnapshot CreateSnapshot(double alpha) => new(GameSimulation.CarSnapshot(_player, alpha), null,
        _phase, Elapsed, 1, 1, Elapsed, 0, _route is null || _route.Plan.Length == 0 ? 0 : _station / _route.Plan.Length,
        0, _player.Impact, _player.Recoveries, alpha, _clock.Tick, _phase == "paused" ? "Paused — driving and GPS frozen."
        : _terrainWaiting ? "Waiting for required Seattle street terrain." : _message);
}
