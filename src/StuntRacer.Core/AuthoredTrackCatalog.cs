namespace StuntRacer.Core;

public sealed record AuthoredTrackChoice(string Id, string Name, string Group, string Description);

public static class AuthoredTrackCatalog
{
    public static IReadOnlyList<AuthoredTrackChoice> Entries { get; } = Array.AsReadOnly(new[]
    {
        new AuthoredTrackChoice("foundry", "Foundry Heights", "Included circuits", "The original elevated ramp-and-banking course."),
        new AuthoredTrackChoice("ridgeline", "Ridgeline Run", "Included circuits", "Flowing switchbacks and a two-crest ridge."),
        new AuthoredTrackChoice("skyway", "Skyway Sprint", "Included circuits", "Wide high-speed bends and two launching crests.")
    });

    public static TrackDefinition Create(string id) => id switch
    {
        "foundry" => TrackDefinition.Create(),
        "ridgeline" => Variant("Ridgeline Run", 1.15, 1.1, 25, 11, 24, 10, 8),
        "skyway" => Variant("Skyway Sprint", 1.6, 1.8, 0, 14, 18, 9, 7),
        _ => throw new ArgumentException($"Unknown included circuit '{id}'.", nameof(id))
    };

    private static TrackDefinition Variant(string name, double xScale, double zScale,
        double sway, double width, double elevation, double firstCrest, double secondCrest)
    {
        var basis = TrackDefinition.Create().Points;
        var positions = new TrackSurfaceVertex[basis.Length];
        for (var i = 0; i < basis.Length; i++)
        {
            var u = i / (double)basis.Length;
            positions[i] = new(
                basis[i].X * xScale + sway * Math.Sin(u * Math.Tau * 2) * Math.Pow(Math.Sin(u * Math.PI), 2),
                elevation + Hump(u, 0.13, 0.17, firstCrest) + Hump(u, 0.61, 0.16, secondCrest),
                basis[i].Z * zScale);
        }
        var headings = new double[basis.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var previous = positions[(i - 1 + positions.Length) % positions.Length];
            var next = positions[(i + 1) % positions.Length];
            headings[i] = Math.Atan2(next.X - previous.X, next.Z - previous.Z);
        }
        var points = new TrackPoint[basis.Length];
        var edges = new TrackSurfaceEdge[basis.Length];
        double length = 0;
        for (var i = 0; i < positions.Length; i++)
        {
            if (i > 0) length += Distance(positions[i - 1], positions[i]);
            var p = positions[i];
            var turn = Math.IEEERemainder(headings[(i + 1) % headings.Length] -
                headings[(i - 1 + headings.Length) % headings.Length], Math.Tau);
            // A level start/finish apron keeps grid contacts clear of the banking transition.
            var apron = Math.Clamp((Math.Min(i, positions.Length - i) / (double)positions.Length - 0.01) / 0.02, 0, 1);
            var bank = Math.Clamp(-turn * 8, -0.26, 0.26) * apron * apron * (3 - 2 * apron);
            points[i] = new(length, p.X, p.Y, p.Z, headings[i], bank, width, true);
            var rightX = Math.Cos(headings[i]) * width / 2;
            var rightY = Math.Sin(bank) * width / 2;
            var rightZ = -Math.Sin(headings[i]) * width / 2;
            edges[i] = new(new(p.X - rightX, p.Y - rightY, p.Z - rightZ),
                new(p.X + rightX, p.Y + rightY, p.Z + rightZ));
        }
        length += Distance(positions[^1], positions[0]);
        return TrackDefinition.FromGeometry(name, width, length, points, edges);
    }

    private static double Hump(double u, double start, double span, double height) =>
        u <= start || u >= start + span ? 0 : height * Math.Pow(Math.Sin((u - start) / span * Math.PI), 2);

    private static double Distance(TrackSurfaceVertex a, TrackSurfaceVertex b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
}
