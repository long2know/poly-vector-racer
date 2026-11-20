namespace StuntRacer.Core.Stunts;

internal sealed class StuntSurface
{
    public const double RideHeight = 1;
    private readonly TrackDefinition _track;
    private readonly StuntVector[] _normals;
    private readonly Triangle[] _triangles;

    public StuntSurface(TrackDefinition track)
    {
        _track = track;
        _normals = new StuntVector[track.Points.Length];
        var triangles = new List<Triangle>(track.Points.Length * 2);
        for (var i = 0; i < track.Points.Length; i++)
        {
            var current = track.Points[i];
            var previous = track.Points[(i + track.Points.Length - 1) % track.Points.Length];
            var next = track.Points[(i + 1) % track.Points.Length];
            var before = previous;
            var after = next;
            if (current.Road && !previous.Road)
                before = current;
            if (current.Road && !next.Road)
                after = current;
            var along = new StuntVector(after.X - before.X, after.Y - before.Y, after.Z - before.Z);
            var edge = track.Edges[i];
            _normals[i] = StuntVector.Cross(along, StuntVector.From(edge.Right) - StuntVector.From(edge.Left)).Unit;
            if (!track.Points[i].Road || !next.Road)
                continue;
            var j = (i + 1) % track.Points.Length;
            var a = StuntVector.From(edge.Left);
            var b = StuntVector.From(edge.Right);
            var c = StuntVector.From(track.Edges[j].Left);
            var d = StuntVector.From(track.Edges[j].Right);
            // Same diagonal as the canonical browser strip.
            triangles.Add(new(a, c, b, i, false));
            triangles.Add(new(b, c, d, i, true));
        }
        _triangles = triangles.ToArray();
    }

    public Sample At(double s, double q)
    {
        s = Wrap(s);
        var points = _track.Points;
        var low = 0;
        var high = points.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (points[mid].S <= s)
                low = mid;
            else
                high = mid - 1;
        }
        var next = (low + 1) % points.Length;
        var length = (next == 0 ? _track.Length : points[next].S) - points[low].S;
        var t = (s - points[low].S) / length;
        var left = StuntVector.From(_track.Edges[low].Left);
        var right = StuntVector.From(_track.Edges[low].Right);
        var nextLeft = StuntVector.From(_track.Edges[next].Left);
        var nextRight = StuntVector.From(_track.Edges[next].Right);
        StuntVector position, along, across;
        if (q + t <= 1)
        {
            along = (nextLeft - left) / length;
            across = right - left;
            position = left + across * q + (nextLeft - left) * t;
        }
        else
        {
            along = (nextRight - right) / length;
            across = nextRight - nextLeft;
            position = nextRight + (right - nextRight) * (1 - t) + (nextLeft - nextRight) * (1 - q);
        }

        var interpolated = StuntVector.Lerp(_normals[low], _normals[next], t);
        var normal = interpolated.Unit;
        var normalDerivative = ((_normals[next] - _normals[low]) / length).OnPlane(normal) / interpolated.Length;
        return new(position, normal, along + normalDerivative * RideHeight, across, normalDerivative,
            points[low].Road && points[next].Road, length, low, t);
    }

    public bool Sweep(StuntVector from, StuntVector to, out Hit hit)
    {
        hit = default;
        var earliest = double.PositiveInfinity;
        foreach (var triangle in _triangles)
        {
            if (!triangle.Near(from, to, RideHeight + 0.05))
                continue;
            for (var side = 1; side >= -1; side -= 2)
            {
                var normal = triangle.Normal * side;
                var a = StuntVector.Dot(from - triangle.A, normal) - RideHeight;
                var b = StuntVector.Dot(to - triangle.A, normal) - RideHeight;
                if (a < -0.04 || b > 0 || a - b < 1e-9)
                    continue;
                var fraction = Math.Clamp(a / (a - b), 0, 1);
                if (fraction >= earliest)
                    continue;
                var contact = StuntVector.Lerp(from, to, fraction) - normal * RideHeight;
                if (!triangle.Contains(contact, out var wb, out var wc))
                    continue;
                var row = triangle.Row;
                var next = (row + 1) % _track.Points.Length;
                var length = (next == 0 ? _track.Length : _track.Points[next].S) - _track.Points[row].S;
                // Triangle rows are [left,nextLeft,right] and [right,nextLeft,nextRight].
                var t = triangle.Second ? wb + wc : wb;
                var q = triangle.Second ? 1 - wb : wc;
                earliest = fraction;
                hit = new(Wrap(_track.Points[row].S + t * length), q, normal, fraction, side > 0);
            }
        }
        return double.IsFinite(earliest);
    }

    public double Wrap(double s) => (s % _track.Length + _track.Length) % _track.Length;

    internal readonly record struct Sample(
        StuntVector Position, StuntVector Normal, StuntVector Along, StuntVector Across,
        StuntVector NormalDerivative, bool Road, double SegmentLength, int Row, double T)
    {
        public StuntVector Center => Position + Normal * RideHeight;

        public (double S, double Q) Rates(StuntVector velocity)
        {
            var a = StuntVector.Dot(Along, Along);
            var b = StuntVector.Dot(Along, Across);
            var c = StuntVector.Dot(Across, Across);
            var d = StuntVector.Dot(velocity, Along);
            var e = StuntVector.Dot(velocity, Across);
            var determinant = a * c - b * b;
            return ((d * c - e * b) / determinant, (e * a - d * b) / determinant);
        }
    }

    internal readonly record struct Hit(double S, double Q, StuntVector Normal, double Fraction, bool Front);

    private readonly struct Triangle
    {
        public Triangle(StuntVector a, StuntVector b, StuntVector c, int row, bool second)
        {
            A = a;
            B = b;
            C = c;
            Row = row;
            Second = second;
            Normal = StuntVector.Cross(b - a, c - a).Unit;
            Minimum = new(Math.Min(a.X, Math.Min(b.X, c.X)), Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                Math.Min(a.Z, Math.Min(b.Z, c.Z)));
            Maximum = new(Math.Max(a.X, Math.Max(b.X, c.X)), Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                Math.Max(a.Z, Math.Max(b.Z, c.Z)));
        }

        public readonly StuntVector A, B, C, Normal, Minimum, Maximum;
        public readonly int Row;
        public readonly bool Second;

        public bool Near(StuntVector from, StuntVector to, double margin) =>
            Math.Max(from.X, to.X) + margin >= Minimum.X && Math.Min(from.X, to.X) - margin <= Maximum.X &&
            Math.Max(from.Y, to.Y) + margin >= Minimum.Y && Math.Min(from.Y, to.Y) - margin <= Maximum.Y &&
            Math.Max(from.Z, to.Z) + margin >= Minimum.Z && Math.Min(from.Z, to.Z) - margin <= Maximum.Z;

        public bool Contains(StuntVector p, out double wb, out double wc)
        {
            var u = B - A;
            var v = C - A;
            var w = p - A;
            var uu = StuntVector.Dot(u, u);
            var uv = StuntVector.Dot(u, v);
            var vv = StuntVector.Dot(v, v);
            var wu = StuntVector.Dot(w, u);
            var wv = StuntVector.Dot(w, v);
            var determinant = uu * vv - uv * uv;
            wb = (wu * vv - wv * uv) / determinant;
            wc = (wv * uu - wu * uv) / determinant;
            return wb >= -1e-7 && wc >= -1e-7 && wb + wc <= 1 + 1e-7;
        }
    }
}
