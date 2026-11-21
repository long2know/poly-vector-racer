namespace StuntRacer.Flight;

public sealed record FlightRangeProp(string Id, string Name, string Kind, FlightVector Position,
    double Distance, bool Tagged);

public sealed record FlightRangeState(int Score, int Remaining, string Status, double CooldownSeconds,
    string? InRangeId, IReadOnlyList<FlightRangeProp> Props);

public sealed class FlightTagRange : IFlightSession
{
    private const double ScanRange = 210;
    private const double ScanInterval = 0.75;
    private const string DroneId = "cloud-hopper";
    private const string BeaconId = "signal-mesa";
    private const double BeaconGround = 145;
    private static readonly FlightWorldDefinition RangeWorld = new(
        "Coppercloud Range", 0,
        Array.AsReadOnly(new FlightIsland[]
        {
            new("Coppercloud Flight Strip", -180, -3950, 1150, 1700, 26, 0),
            new("Signal Mesa", -105, -3310, 275, 280, BeaconGround, 0.12),
            new("Coral Shelf", 760, -3450, 550, 1700, 54, -0.21),
            new("Far Reef", 460, -1850, 1150, 750, 35, 0.33)
        }),
        Array.AsReadOnly(new LandingSurface[] { new("Coppercloud Strip", -490, -3840, 70, 1150, 26, 0) }),
        new(-490, 28.4, -4340), 0, 2.4);

    private readonly FlightSimulation _flight = new(RangeWorld);
    private readonly HashSet<string> _tagged = new(StringComparer.Ordinal);
    private double _nextScan;
    private string _status = "Bring both practice props within scanner range.";
    private bool _complete;

    public FlightWorldDefinition World => RangeWorld;
    public FlightSnapshot Snapshot => WithRange(_flight.Snapshot);

    public FlightSnapshot Advance(double seconds, FlightInput input) => WithRange(_flight.Advance(seconds, input));

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "garage":
                Reset();
                return;
            case "restart":
                Reset();
                Launch();
                return;
            case "start":
            case "air-start":
                if (_flight.Snapshot.Phase == "ready") Launch();
                return;
            case "scan":
                Scan();
                return;
            default:
                if (!_complete) _flight.Command(command);
                return;
        }
    }

    private void Launch()
    {
        _flight.Command("air-start");
        if (_flight.Snapshot.Aircraft.GearDown) _flight.Command("gear-toggle");
        _status = "Fly close to the drone and beacon, then scan.";
    }

    private void Reset()
    {
        _flight.Command("garage");
        _tagged.Clear();
        _nextScan = 0;
        _complete = false;
        _status = "Bring both practice props within scanner range.";
    }

    private void Scan()
    {
        var snapshot = _flight.Snapshot;
        if (snapshot.Phase != "flying" || snapshot.Time < _nextScan) return;
        _nextScan = snapshot.Time + ScanInterval;
        var props = Props(snapshot);
        var candidate = FindInRange(props);
        if (candidate is null)
        {
            _status = "Out of range. Fly closer and try again.";
            return;
        }

        _tagged.Add(candidate.Id);
        _status = $"{candidate.Name} tagged.";
        if (_tagged.Count != props.Length) return;
        _complete = true;
        _flight.Command("pause");
        _status = "Range clear. Both practice props tagged.";
    }

    private FlightSnapshot WithRange(FlightSnapshot snapshot)
    {
        var props = Props(snapshot);
        var message = snapshot.Phase switch
        {
            "ready" => "Coppercloud Range ready for airborne launch.",
            "paused" when !_complete => "Range paused.",
            "crashed" => "Range run ended. Try another flight.",
            _ => _status
        };
        return snapshot with
        {
            Phase = _complete ? "finished" : snapshot.Phase,
            Message = message,
            Training = new(_tagged.Count, props.Length - _tagged.Count, _status,
                Math.Max(0, _nextScan - snapshot.Time), FindInRange(props)?.Id, props)
        };
    }

    private FlightRangeProp[] Props(FlightSnapshot snapshot)
    {
        var time = snapshot.Time;
        return
        [
            Make(DroneId, "Cloud Hopper drone", "drone", new(
                42 + Math.Sin(time * 0.85) * 55, 258 + Math.Sin(time * 1.15) * 12,
                -3680 + Math.Sin(time * 0.55) * 60), snapshot.Aircraft),
            Make(BeaconId, "Signal Mesa beacon", "beacon", new(-105, BeaconGround, -3310), snapshot.Aircraft)
        ];
    }

    private FlightRangeProp Make(string id, string name, string kind, FlightVector position, AircraftSnapshot aircraft)
    {
        var dx = aircraft.X - position.X;
        var dy = aircraft.Y - position.Y;
        var dz = aircraft.Z - position.Z;
        return new(id, name, kind, position, Math.Sqrt(dx * dx + dy * dy + dz * dz), _tagged.Contains(id));
    }

    private static FlightRangeProp? FindInRange(IEnumerable<FlightRangeProp> props) =>
        props.Where(prop => !prop.Tagged && prop.Distance <= ScanRange).MinBy(prop => prop.Distance);
}
