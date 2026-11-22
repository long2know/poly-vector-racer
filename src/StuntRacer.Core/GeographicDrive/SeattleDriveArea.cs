namespace StuntRacer.Core.GeographicDrive;

public readonly record struct DrivePoint(double X, double Z)
{
    internal double DistanceTo(DrivePoint other) => double.Hypot(other.X - X, other.Z - Z);
    internal double HeadingTo(DrivePoint other) => Math.Atan2(other.X - X, other.Z - Z);
    internal DrivePoint Toward(DrivePoint other, double fraction) =>
        new(X + (other.X - X) * fraction, Z + (other.Z - Z) * fraction);
}

public sealed record DriveBounds(double MinX, double MinZ, double MaxX, double MaxZ)
{
    internal bool Contains(DrivePoint point) =>
        point.X >= MinX && point.X <= MaxX && point.Z >= MinZ && point.Z <= MaxZ;
}

public sealed record DriveTile(int Level, int X, int Z);
public sealed record DriveDestination(string Id, string Name, string Approach, DrivePoint Point);
public sealed record DrivePlan(int Revision, IReadOnlyList<DrivePoint> Points, double Length);
public sealed record DriveActivity(
    string Status, string Message, bool Autopilot, string? DestinationId, int PlanRevision,
    double? Remaining, double DrivenDistance, DrivePoint? Waypoint, bool TerrainWaiting, string CruiseSpeed);

public static class SeattleDriveArea
{
    public const string Id = "seattle-drive";
    public const string Name = "Seattle Scenic Drive";
    public const string Coverage = "Explore dry downtown terrain manually. GPS and Autopilot use connected surface streets only.";
    public static DriveBounds Bounds { get; } = new(-640, 6800, 1000, 10740);
    public static DrivePoint Spawn { get; } = new(174.57, 8338.10);
    public static double SpawnHeading { get; } = Spawn.HeadingTo(new(183.23, 8340.37));

    public static IReadOnlyList<DriveTile> Tiles { get; } = Array.AsReadOnly(
        (from x in Enumerable.Range(-1, 2)
         from z in Enumerable.Range(6, 5)
         select new DriveTile(8, x, z)).ToArray());

    // Street-side stops, not landmark centroids (in particular, the Wheel is on a pier).
    internal static IReadOnlyList<DriveDestination> Stops { get; } = Array.AsReadOnly<DriveDestination>(
    [
        new("great-wheel", "Seattle Great Wheel", "Waterfront street", new(74.31, 8798.47)),
        new("space-needle", "Space Needle", "Seattle Center street", new(-101.41, 10349.52)),
        new("amazon-spheres", "Amazon Spheres", "Denny Triangle street", new(455.80, 9687.40)),
        new("lumen-field", "Lumen Field", "Stadium District street", new(273.13, 7403.61)),
        new("t-mobile-park", "T-Mobile Park", "Ballpark approach", new(229.05, 7094.54)),
    ]);
}
