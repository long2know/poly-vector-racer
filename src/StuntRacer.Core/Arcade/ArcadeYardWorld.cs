namespace StuntRacer.Core.Arcade;

public readonly record struct ArcadeYardPoint(double X, double Z);
public readonly record struct ArcadeYardBounds(double MinX, double MaxX, double MinZ, double MaxZ);
public sealed record ArcadeYardLane(string Color, double Width, IReadOnlyList<ArcadeYardPoint> Points);
public sealed record ArcadeYardProp(string Id, double X, double Z, double Size, int Points, string Color);
public sealed record ArcadeYardPropSnapshot(string Id, double X, double Z, long? HitTick)
{
    public bool Hit => HitTick.HasValue;
}

public sealed record ArcadeYardActivity(int Score, int ScoreGoal, double TimeRemaining, double TimeLimit,
    double Elapsed, int PropsHit, int PropCount, bool? Success, IReadOnlyList<ArcadeYardPropSnapshot> Props);

public sealed class ArcadeYardWorld
{
    internal static ArcadeYardWorld Definition { get; } = new();

    private ArcadeYardWorld()
    {
        Props = Array.AsReadOnly<ArcadeYardProp>(
        [
            new("launch-1", 0, -28, 2.4, 100, "mint"),
            new("launch-2", 0, -15, 2.4, 100, "mint"),
            new("launch-3", 0, -2, 2.4, 100, "mint"),
            ..Branch("coral", 1),
            ..Branch("gold", -1)
        ]);
        Lanes = Array.AsReadOnly<ArcadeYardLane>(
        [
            new("mint", 8, Array.AsReadOnly<ArcadeYardPoint>([new(0, -48), new(0, -6)])),
            Lane("coral"),
            Lane("gold")
        ]);
    }

    public string Name => "Neon Yard";
    public double GroundY => 0;
    public ArcadeYardBounds Bounds { get; } = new(-54, 54, -56, 72);
    public ArcadeYardPoint Spawn { get; } = new(0, -42);
    public IReadOnlyList<ArcadeYardProp> Props { get; }
    public IReadOnlyList<ArcadeYardLane> Lanes { get; }

    private static ArcadeYardProp[] Branch(string color, int side) =>
    [
        new($"{color}-1", side * 5, 18, 2.8, 175, color),
        new($"{color}-2", side * 12, 31, 2.8, 175, color),
        new($"{color}-3", side * 23, 43, 2.8, 175, color),
        new($"{color}-4", side * 36, 53, 2.8, 175, color),
        new($"{color}-5", side * 43, 33, 2.8, 175, color)
    ];

    private ArcadeYardLane Lane(string color) => new(color, 6, Array.AsReadOnly<ArcadeYardPoint>(
        [new(0, -8), ..Props.Where(p => p.Color == color).Select(p => new ArcadeYardPoint(p.X, p.Z))]));

    internal TrackDefinition CreatePerimeterTrack()
    {
        const double railWidth = 4;
        var width = railWidth * Math.Sqrt(2);
        var b = Bounds;
        (double X, double Z, int InX, int InZ)[] corners =
            [(b.MinX, b.MinZ, 1, 1), (b.MinX, b.MaxZ, 1, -1), (b.MaxX, b.MaxZ, -1, -1), (b.MaxX, b.MinZ, -1, 1)];
        var points = new TrackPoint[corners.Length];
        var edges = new TrackSurfaceEdge[corners.Length];
        double length = 0;
        for (var i = 0; i < corners.Length; i++)
        {
            var (x, z, inX, inZ) = corners[i];
            var centerX = x + inX * railWidth / 2;
            var centerZ = z + inZ * railWidth / 2;
            points[i] = new(length, centerX, GroundY, centerZ, -Math.PI / 4 + i * Math.PI / 2, 0, width, true);
            edges[i] = new(new(x, GroundY, z), new(x + inX * railWidth, GroundY, z + inZ * railWidth));
            var next = corners[(i + 1) % corners.Length];
            length += Math.Abs(next.X - x) + Math.Abs(next.Z - z) - railWidth;
        }
        return TrackDefinition.FromGeometry(Name, width, length, points, edges);
    }
}
