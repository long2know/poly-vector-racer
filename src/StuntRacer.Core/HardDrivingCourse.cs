namespace StuntRacer.Core;

public static class HardDrivingCourse
{
    public static TrackDefinition Create()
    {
        (double X, double Z)[] anchors =
        [
            (-180, -380), (-180, -260), (-180, -80), (-130, 150),
            (0, 320), (240, 320), (380, 200), (380, 0),
            (280, -120), (120, -130), (80, -260), (200, -360),
            (220, -520), (40, -620), (-140, -560), (-180, -440)
        ];
        const int subdivisions = 40;
        var positions = new TrackSurfaceVertex[anchors.Length * subdivisions];
        for (var i = 0; i < positions.Length; i++)
        {
            var segment = i / subdivisions;
            var t = (i % subdivisions) / (double)subdivisions;
            var a = anchors[(segment + anchors.Length - 1) % anchors.Length];
            var b = anchors[segment];
            var c = anchors[(segment + 1) % anchors.Length];
            var d = anchors[(segment + 2) % anchors.Length];
            var u = i / (double)positions.Length;
            positions[i] = new(Spline(a.X, b.X, c.X, d.X, t),
                4 + Crest(u, 0.18, 0.25, 22) + Crest(u, 0.58, 0.085, 8),
                Spline(a.Z, b.Z, c.Z, d.Z, t));
        }
        var points = new TrackPoint[positions.Length];
        var edges = new TrackSurfaceEdge[positions.Length];
        var headings = positions.Select((p, i) =>
        {
            var before = positions[(i - 1 + positions.Length) % positions.Length];
            var after = positions[(i + 1) % positions.Length];
            return Math.Atan2(after.X - before.X, after.Z - before.Z);
        }).ToArray();
        double length = 0;
        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            if (i > 0) length += Distance(positions[i - 1], p);
            var turn = Math.IEEERemainder(headings[(i + 1) % headings.Length] -
                headings[(i - 1 + headings.Length) % headings.Length], Math.Tau);
            var u = i / (double)positions.Length;
            var width = 15 - Crest(u, 0.46, 0.11, 5);
            var apron = Math.Clamp(Math.Min(u, 1 - u) / 0.035, 0, 1);
            var bank = Math.Clamp(-turn * 11, -0.42, 0.42) * apron;
            points[i] = new(length, p.X, p.Y, p.Z, headings[i], bank, width, true);
            var rx = Math.Cos(headings[i]) * width / 2;
            var ry = Math.Sin(bank) * width / 2;
            var rz = -Math.Sin(headings[i]) * width / 2;
            edges[i] = new(new(p.X - rx, p.Y - ry, p.Z - rz), new(p.X + rx, p.Y + ry, p.Z + rz));
        }
        length += Distance(positions[^1], positions[0]);
        return TrackDefinition.FromGeometry("Hard Drivin' Proving Ground", 15, length, points, edges);
    }

    private static double Spline(double a, double b, double c, double d, double t) =>
        0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t * t +
            (-a + 3 * b - 3 * c + d) * t * t * t);

    private static double Crest(double u, double start, double span, double height) =>
        u <= start || u >= start + span ? 0 : height * Math.Pow(Math.Sin((u - start) / span * Math.PI), 2);

    private static double Distance(TrackSurfaceVertex a, TrackSurfaceVertex b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
}
