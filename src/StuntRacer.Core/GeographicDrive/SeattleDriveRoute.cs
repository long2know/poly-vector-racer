namespace StuntRacer.Core.GeographicDrive;

internal sealed class SeattleDriveRoute
{
    private readonly double[] _station;
    private readonly List<(double Station, double Angle)> _turns = [];

    public SeattleDriveRoute(int revision, DrivePoint[] points)
    {
        _station = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            _station[i] = _station[i - 1] + points[i - 1].DistanceTo(points[i]);
        Plan = new(revision, Array.AsReadOnly(points), _station[^1]);
        for (var i = 1; i < points.Length - 1; i++)
        {
            var before = At(Math.Max(0, _station[i] - 13));
            var after = At(Math.Min(Plan.Length, _station[i] + 13));
            var angle = Math.Abs(SeattleStreetGraph.Angle(before.HeadingTo(points[i]) - points[i].HeadingTo(after)));
            if (angle >= 0.28)
                _turns.Add((_station[i], angle));
        }
    }

    public DrivePlan Plan { get; }
    public DrivePoint End => Plan.Points[^1];

    public DrivePoint At(double station)
    {
        if (station <= 0) return Plan.Points[0];
        if (station >= Plan.Length) return End;
        var index = SegmentAt(station);
        return Plan.Points[index].Toward(Plan.Points[index + 1],
            (station - _station[index]) / (_station[index + 1] - _station[index]));
    }

    public (double Station, double Error) Project(DrivePoint position, double station)
    {
        var best = station;
        var error = position.DistanceTo(At(station));
        var min = Math.Max(0, station - 14);
        var max = Math.Min(Plan.Length, station + 30);
        for (var i = SegmentAt(min); i < _station.Length - 1 && _station[i] <= max; i++)
        {
            var length = _station[i + 1] - _station[i];
            var fraction = SeattleStreetGraph.Fraction(position, Plan.Points[i], Plan.Points[i + 1], length);
            var distance = Math.Clamp(_station[i] + length * fraction, min, max);
            var nextError = position.DistanceTo(At(distance));
            if (nextError >= error) continue;
            best = distance;
            error = nextError;
        }
        return (best, error);
    }

    public double SpeedLimit(double station)
    {
        const double cruise = 13;
        const double comfortBrake = 1.8;
        var remaining = Math.Max(0, Plan.Length - station - 0.8);
        var speed = Math.Min(cruise, Math.Sqrt(2 * comfortBrake * remaining));
        foreach (var turn in _turns)
        {
            var distance = turn.Station - station;
            if (distance < -8) continue;
            if (distance > 60) break;
            var cornerSpeed = Math.Clamp(10.6 - 3.9 * turn.Angle, 3.4, cruise);
            speed = Math.Min(speed, Math.Sqrt(cornerSpeed * cornerSpeed + 2 * comfortBrake * Math.Max(0, distance - 7)));
        }
        return speed;
    }

    private int SegmentAt(double station)
    {
        var index = Array.BinarySearch(_station, station);
        return index >= 0 ? index : ~index - 1;
    }
}
