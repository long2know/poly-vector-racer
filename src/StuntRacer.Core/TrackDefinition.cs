using System.Text.Json.Serialization;

namespace StuntRacer.Core;

public readonly record struct RoadSample(
    double S, double X, double Y, double Z, double Heading, double Bank,
    double Width, double Lateral, bool Road, double SlopeX, double SlopeZ);

public sealed class TrackDefinition
{
    private readonly Segment[] _segments;

    private TrackDefinition(string name, double width, TrackPoint[] points, double length,
        TrackSurfaceEdge[]? edges = null, bool spatial = false)
    {
        Name = name;
        Width = width;
        Points = points;
        Length = length;
        IsSpatial = spatial;
        Edges = edges ?? points.Select(p => new TrackSurfaceEdge(Edge(p, -1), Edge(p, 1))).ToArray();
        _segments = new Segment[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            _segments[i] = new Segment(a, b, i + 1 == points.Length ? length : b.S,
                Edges[i], Edges[(i + 1) % points.Length]);
        }
    }

    public string Name { get; }
    public double Length { get; }
    public double Width { get; }
    public TrackPoint[] Points { get; }
    public TrackSurfaceEdge[] Edges { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSpatial { get; }

    public static TrackDefinition FromGeometry(string name, double width, double length,
        TrackPoint[] points, TrackSurfaceEdge[] edges)
        => FromGeometryCore(name, width, length, points, edges, false);

    public static TrackDefinition FromSpatialGeometry(string name, double width, double length,
        TrackPoint[] points, TrackSurfaceEdge[] edges)
        => FromGeometryCore(name, width, length, points, edges, true);

    private static TrackDefinition FromGeometryCore(string name, double width, double length,
        TrackPoint[] points, TrackSurfaceEdge[] edges, bool spatial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(edges);
        if (name.Length > 128 || points.Length is < 3 or > 20_000 || edges.Length != points.Length)
            throw new ArgumentException("Track geometry requires a name and matching point/edge rows (3 to 20000).");
        if (!double.IsFinite(width) || width <= 0 || width > 1e6 ||
            !double.IsFinite(length) || length <= 0 || length > 1e9)
            throw new ArgumentOutOfRangeException(nameof(length), "Track dimensions must be finite, positive and within the supported range.");
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i] ?? throw new ArgumentException("A track point is missing.", nameof(points));
            if (!new[] { p.S, p.X, p.Y, p.Z, p.Heading, p.Bank, p.Width }.All(double.IsFinite) ||
                Math.Abs(p.X) > 1e6 || Math.Abs(p.Y) > 1e6 || Math.Abs(p.Z) > 1e6 ||
                Math.Abs(p.Heading) > 1e6 || Math.Abs(p.Bank) > 1e6 ||
                p.Width <= 0 || p.Width > 1e6 || p.S < 0 || p.S >= length ||
                i == 0 && p.S != 0 || i > 0 && p.S <= points[i - 1].S)
                throw new ArgumentException("Track points must be finite with positive width and increasing distances from zero.", nameof(points));
            var a = edges[i];
            var b = edges[(i + 1) % edges.Length];
            if (!Finite(a.Left) || !Finite(a.Right))
                throw new ArgumentException("Track edge vertices must be finite.", nameof(edges));
            var next = points[(i + 1) % points.Length] ??
                throw new ArgumentException("A track point is missing.", nameof(points));
            var degenerate = spatial
                ? Distance(p.X, p.Y, p.Z, next.X, next.Y, next.Z) < 1e-6 ||
                    Area3D(a.Left, a.Right, b.Left) < 1e-8 || Area3D(a.Right, b.Right, b.Left) < 1e-8
                : Distance(p.X, 0, p.Z, next.X, 0, next.Z) < 1e-6 ||
                    Math.Abs(Area(a.Left, a.Right, b.Left)) < 1e-8 || Math.Abs(Area(a.Right, b.Right, b.Left)) < 1e-8;
            if (degenerate)
                throw new ArgumentException(spatial
                    ? "Spatial track geometry contains a degenerate segment or road triangle."
                    : "Track geometry contains a degenerate horizontal segment or road triangle.", nameof(edges));
        }
        return new TrackDefinition(name, width, (TrackPoint[])points.Clone(), length,
            (TrackSurfaceEdge[])edges.Clone(), spatial);
    }

    private static bool Finite(TrackSurfaceVertex v) =>
        double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z) &&
        Math.Abs(v.X) <= 1e6 && Math.Abs(v.Y) <= 1e6 && Math.Abs(v.Z) <= 1e6;

    private static double Area(TrackSurfaceVertex a, TrackSurfaceVertex b, TrackSurfaceVertex c) =>
        (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

    private static double Area3D(TrackSurfaceVertex a, TrackSurfaceVertex b, TrackSurfaceVertex c)
    {
        var x = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y);
        var y = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
        var z = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return Math.Sqrt(x * x + y * y + z * z);
    }

    public static TrackDefinition Create()
    {
        const int count = 704;
        const double straight = 250;
        const double radius = 60;
        const double width = 12;
        var planarLength = 2 * straight + Math.Tau * radius;
        var points = new TrackPoint[count];
        double distance = 0;
        for (var i = 0; i < count; i++)
        {
            var u = planarLength * i / count;
            double x, z, heading, bank = 0;
            if (u < straight)
            {
                x = -radius;
                z = -straight / 2 + u;
                heading = 0;
            }
            else if (u < straight + Math.PI * radius)
            {
                var angle = (u - straight) / radius;
                x = -radius * Math.Cos(angle);
                z = straight / 2 + radius * Math.Sin(angle);
                heading = angle;
                bank = -0.24 * Math.Pow(Math.Sin(angle), 2);
            }
            else if (u < 2 * straight + Math.PI * radius)
            {
                x = radius;
                z = straight / 2 - (u - straight - Math.PI * radius);
                heading = Math.PI;
            }
            else
            {
                var angle = Math.PI + (u - 2 * straight - Math.PI * radius) / radius;
                x = -radius * Math.Cos(angle);
                z = -straight / 2 + radius * Math.Sin(angle);
                heading = angle;
                bank = -0.24 * Math.Pow(Math.Sin(angle), 2);
            }

            // Entirely authored geometry: a smooth launching crest, then a broad return dip.
            var y = 22 + Hump(u, 125, 90, 9) -
                Hump(u, straight + Math.PI * radius + 15, 220, 6);
            if (i > 0)
            {
                var previous = points[i - 1];
                distance += Distance(previous.X, previous.Y, previous.Z, x, y, z);
            }

            points[i] = new TrackPoint(distance, x, y, z, heading, bank, width, true);
        }

        var first = points[0];
        var last = points[^1];
        distance += Distance(last.X, last.Y, last.Z, first.X, first.Y, first.Z);
        return new TrackDefinition("Foundry Heights — authored time attack", width, points, distance);
    }

    public TrackPoint AtDistance(double s)
    {
        if (!double.IsFinite(s))
            throw new ArgumentOutOfRangeException(nameof(s));
        s = Wrap(s);
        var low = 0;
        var high = Points.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Points[middle].S <= s)
                low = middle;
            else
                high = middle - 1;
        }

        var segment = _segments[low];
        var t = (s - segment.A.S) / (segment.EndS - segment.A.S);
        return new TrackPoint(s,
            Lerp(segment.A.X, segment.B.X, t), Lerp(segment.A.Y, segment.B.Y, t),
            Lerp(segment.A.Z, segment.B.Z, t), AngleLerp(segment.A.Heading, segment.B.Heading, t),
            Lerp(segment.A.Bank, segment.B.Bank, t), Lerp(segment.A.Width, segment.B.Width, t),
            segment.A.Road && segment.B.Road);
    }

    public RoadSample Sample(double x, double z)
    {
        if (IsSpatial)
            throw new NotSupportedException("Spatial stunt geometry requires full 3D contacts, not heightfield sampling.");
        ValidatePosition(x, z);
        var index = FindNearest(x, z);
        return SampleNear(x, z, index);
    }

    internal int FindNearest(double x, double z)
    {
        var nearest = 0;
        var best = double.MaxValue;
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            var t = segment.Project(x, z);
            var dx = x - Lerp(segment.A.X, segment.B.X, t);
            var dz = z - Lerp(segment.A.Z, segment.B.Z, t);
            var distance = dx * dx + dz * dz;
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }
        return nearest;
    }

    internal RoadSample SampleNear(double x, double z, int near)
    {
        if (IsSpatial)
            throw new NotSupportedException("Spatial stunt geometry requires full 3D contacts, not heightfield sampling.");
        // Collision uses the rendered strip's triangles, not a second analytic road.
        // Winding/diagonal: [left_i,right_i,left_next], [right_i,right_next,left_next].
        var bestDistance = double.MaxValue;
        RoadSample nearest = default;
        for (var offset = -3; offset <= 3; offset++)
        {
            var segment = _segments[(near + offset + _segments.Length) % _segments.Length];
            if (TryTriangle(x, z, segment.LeftA, segment.RightA, segment.LeftB,
                    out var a, out var b, out var c))
                return Surface(segment, x, z, c, -a + b - c,
                    a * segment.LeftA.Y + b * segment.RightA.Y + c * segment.LeftB.Y,
                    segment.LeftA, segment.RightA, segment.LeftB);
            if (TryTriangle(x, z, segment.RightA, segment.RightB, segment.LeftB,
                    out a, out b, out c))
                return Surface(segment, x, z, b + c, a + b - c,
                    a * segment.RightA.Y + b * segment.RightB.Y + c * segment.LeftB.Y,
                    segment.RightA, segment.RightB, segment.LeftB);

            var t = segment.Project(x, z);
            var centerX = Lerp(segment.A.X, segment.B.X, t);
            var centerZ = Lerp(segment.A.Z, segment.B.Z, t);
            var dx = x - centerX;
            var dz = z - centerZ;
            var distance = dx * dx + dz * dz;
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            var heading = AngleLerp(segment.A.Heading, segment.B.Heading, t);
            var bank = Lerp(segment.A.Bank, segment.B.Bank, t);
            var lateral = dx * Math.Cos(heading) - dz * Math.Sin(heading);
            nearest = new RoadSample(Wrap(Lerp(segment.A.S, segment.EndS, t)), centerX,
                Lerp(segment.A.Y, segment.B.Y, t) + Math.Sin(bank) * lateral,
                centerZ, heading, bank, Lerp(segment.A.Width, segment.B.Width, t),
                lateral, false, 0, 0);
        }
        return nearest;
    }

    private RoadSample Surface(Segment segment, double x, double z, double t, double lateralUnit,
        double y, TrackSurfaceVertex a, TrackSurfaceVertex b, TrackSurfaceVertex c)
    {
        var dx1 = b.X - a.X;
        var dz1 = b.Z - a.Z;
        var dx2 = c.X - a.X;
        var dz2 = c.Z - a.Z;
        var determinant = dx1 * dz2 - dx2 * dz1;
        var slopeX = ((b.Y - a.Y) * dz2 - (c.Y - a.Y) * dz1) / determinant;
        var slopeZ = (dx1 * (c.Y - a.Y) - dx2 * (b.Y - a.Y)) / determinant;
        var width = Lerp(segment.A.Width, segment.B.Width, t);
        return new RoadSample(Wrap(Lerp(segment.A.S, segment.EndS, t)), x, y, z,
            AngleLerp(segment.A.Heading, segment.B.Heading, t),
            Lerp(segment.A.Bank, segment.B.Bank, t), width, lateralUnit * width / 2,
            segment.A.Road && segment.B.Road, slopeX, slopeZ);
    }

    private static bool TryTriangle(double x, double z, TrackSurfaceVertex a, TrackSurfaceVertex b, TrackSurfaceVertex c,
        out double wa, out double wb, out double wc)
    {
        var determinant = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        wa = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / determinant;
        wb = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / determinant;
        wc = 1 - wa - wb;
        return wa >= -1e-9 && wb >= -1e-9 && wc >= -1e-9;
    }

    private double Wrap(double s) => (s % Length + Length) % Length;
    private static double Hump(double s, double start, double length, double height) =>
        s <= start || s >= start + length ? 0 : height * Math.Pow(Math.Sin(Math.PI * (s - start) / length), 2);
    private static double Distance(double x, double y, double z, double bx, double by, double bz) =>
        Math.Sqrt((bx - x) * (bx - x) + (by - y) * (by - y) + (bz - z) * (bz - z));
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double AngleLerp(double a, double b, double t) => a + Math.IEEERemainder(b - a, Math.Tau) * t;

    private static void ValidatePosition(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Road queries must be finite.");
    }

    private static TrackSurfaceVertex Edge(TrackPoint p, int side)
    {
        var lateral = side * p.Width / 2;
        return new TrackSurfaceVertex(p.X + Math.Cos(p.Heading) * lateral,
            p.Y + Math.Sin(p.Bank) * lateral, p.Z - Math.Sin(p.Heading) * lateral);
    }

    private sealed class Segment
    {
        private readonly double _dx;
        private readonly double _dz;
        private readonly double _inverseLengthSquared;

        public Segment(TrackPoint a, TrackPoint b, double endS, TrackSurfaceEdge aEdge, TrackSurfaceEdge bEdge)
        {
            A = a;
            B = b;
            EndS = endS;
            LeftA = aEdge.Left;
            RightA = aEdge.Right;
            LeftB = bEdge.Left;
            RightB = bEdge.Right;
            _dx = b.X - a.X;
            _dz = b.Z - a.Z;
            _inverseLengthSquared = 1 / (_dx * _dx + _dz * _dz);
        }

        public TrackPoint A { get; }
        public TrackPoint B { get; }
        public double EndS { get; }
        public TrackSurfaceVertex LeftA { get; }
        public TrackSurfaceVertex RightA { get; }
        public TrackSurfaceVertex LeftB { get; }
        public TrackSurfaceVertex RightB { get; }
        public double Project(double x, double z) =>
            Math.Clamp(((x - A.X) * _dx + (z - A.Z) * _dz) * _inverseLengthSquared, 0, 1);
    }
}
