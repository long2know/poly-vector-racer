namespace StuntRacer.Core.City;

internal sealed class CityPath
{
    private readonly CityPoint[] _points;
    private readonly double[] _distances;
    private readonly bool _closed;

    public CityPath(CityPoint[] points, bool closed)
    {
        _points = points;
        _closed = closed;
        var count = closed ? points.Length : points.Length - 1;
        _distances = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            _distances[i + 1] = _distances[i] + Math.Sqrt(
                (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        }
    }

    public double Length => _distances[^1];

    public (CityPoint Point, double Heading) At(double s, double lateral = 0)
    {
        s = _closed ? (s % Length + Length) % Length : Math.Clamp(s, 0, Length);
        var index = Array.BinarySearch(_distances, s);
        index = index < 0 ? ~index - 1 : Math.Min(index, _distances.Length - 2);
        var a = _points[index];
        var b = _points[(index + 1) % _points.Length];
        var t = (s - _distances[index]) / (_distances[index + 1] - _distances[index]);
        var heading = Math.Atan2(b.X - a.X, b.Z - a.Z);
        return (new CityPoint(a.X + (b.X - a.X) * t + Math.Cos(heading) * lateral,
            a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t - Math.Sin(heading) * lateral), heading);
    }

    public double Nearest(double x, double z, double? around = null)
    {
        var best = double.MaxValue;
        double distance = 0;
        for (var i = 0; i < _distances.Length - 1; i++)
        {
            var a = _points[i];
            var b = _points[(i + 1) % _points.Length];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var t = Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / (dx * dx + dz * dz), 0, 1);
            var candidate = _distances[i] + (_distances[i + 1] - _distances[i]) * t;
            if (around.HasValue && Math.Abs(Math.IEEERemainder(candidate - around.Value, Length)) > 110)
                continue;
            var ex = x - a.X - dx * t;
            var ez = z - a.Z - dz * t;
            var squared = ex * ex + ez * ez;
            if (squared >= best)
                continue;
            best = squared;
            distance = candidate;
        }
        return distance;
    }

    public TrackDefinition Guide(string name)
    {
        var points = new TrackPoint[_points.Length];
        var edges = new TrackSurfaceEdge[_points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = _points[i];
            var heading = At(_distances[i] + 0.001).Heading;
            points[i] = new TrackPoint(_distances[i], p.X, p.Y, p.Z, heading, 0, 28, true);
            var rx = Math.Cos(heading) * 14;
            var rz = -Math.Sin(heading) * 14;
            edges[i] = new TrackSurfaceEdge(new(p.X - rx, p.Y, p.Z - rz), new(p.X + rx, p.Y, p.Z + rz));
        }
        return TrackDefinition.FromGeometry(name, 28, Length, points, edges);
    }

    public static CityPoint[] Round(CityPoint[] corners, bool closed, double trim)
    {
        var result = new List<CityPoint>();
        for (var i = 0; i < corners.Length; i++)
        {
            var b = corners[i];
            if (!closed && (i == 0 || i == corners.Length - 1))
            {
                result.Add(b);
                continue;
            }
            var a = corners[(i + corners.Length - 1) % corners.Length];
            var c = corners[(i + 1) % corners.Length];
            var incoming = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            var outgoing = Math.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Z - b.Z) * (c.Z - b.Z));
            var ix = (b.X - a.X) / incoming;
            var iz = (b.Z - a.Z) / incoming;
            var ox = (c.X - b.X) / outgoing;
            var oz = (c.Z - b.Z) / outgoing;
            if (Math.Abs(ix * oz - iz * ox) < 1e-6)
            {
                result.Add(b);
                continue;
            }
            var cut = Math.Min(trim, Math.Min(incoming, outgoing) * 0.4);
            var entry = new CityPoint(b.X - ix * cut, b.Y - (b.Y - a.Y) * cut / incoming, b.Z - iz * cut);
            var exit = new CityPoint(b.X + ox * cut, b.Y + (c.Y - b.Y) * cut / outgoing, b.Z + oz * cut);
            for (var step = 0; step <= 12; step++)
            {
                var t = step / 12.0;
                var u = 1 - t;
                result.Add(new CityPoint(u * u * entry.X + 2 * u * t * b.X + t * t * exit.X,
                    u * u * entry.Y + 2 * u * t * b.Y + t * t * exit.Y,
                    u * u * entry.Z + 2 * u * t * b.Z + t * t * exit.Z));
            }
        }
        return result.ToArray();
    }
}
