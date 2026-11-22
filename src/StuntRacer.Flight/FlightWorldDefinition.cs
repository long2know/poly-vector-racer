using System.Text.Json.Serialization;

namespace StuntRacer.Flight;

/// <summary>Authored rectangles; heading is radians clockwise from world +Z toward +X.</summary>
public sealed record FlightIsland(
    string Name, double CenterX, double CenterZ, double Width, double Length,
    double Height, double Heading);

public sealed record LandingSurface(
    string Name, double CenterX, double CenterZ, double Width, double Length,
    double Height, double Heading);

public sealed record FlightSurfaceSample(
    double Height, bool IsWater, string Name, LandingSurface? LandingSurface);

public sealed record FlightWorldSurface(string Kind, IReadOnlyList<FlightVector> Vertices);
public sealed record FlightWorldFace(IReadOnlyList<int> Vertices, IReadOnlyList<double> Colour);
public sealed record FlightWorldMesh(
    string Name, IReadOnlyList<FlightVector> Vertices, IReadOnlyList<FlightWorldFace> Faces);
public sealed record FlightSourceWorld(
    IReadOnlyList<FlightWorldSurface> Surfaces,
    IReadOnlyList<FlightWorldMesh> Objects,
    IReadOnlyList<FlightVector> MapOutline,
    IReadOnlyList<string> GeometryNotes);

public sealed record WorldAtlasDeparture(string Id, string Name, double Lon, double Lat, double Heading = Math.PI / 2);
public sealed record WorldAtlasDetails(string Name, WorldFlightDestination Destination,
    double Lon, double Lat)
{
    public int WrapX => WorldAtlasGrid.Width;
    public int MinX => -WorldAtlasGrid.HalfWidth;
    public int MaxX => WorldAtlasGrid.HalfWidth;
    public int MinZ => -WorldAtlasGrid.HalfHeight;
    public int MaxZ => WorldAtlasGrid.HalfHeight;
}

/// <summary>The shared definition for native contacts and consumer-generated scenery.</summary>
public sealed record FlightWorldDefinition(
    string Name,
    double SeaHeight,
    IReadOnlyList<FlightIsland> Islands,
    IReadOnlyList<LandingSurface> LandingSurfaces,
    FlightVector RunwayStart,
    double RunwayHeading,
    double GearClearance,
    FlightSourceWorld? SourceWorld = null)
{
    [JsonIgnore]
    internal WorldFlightElevation? Elevation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldFlightDetail? Geography { get; init; }

    [JsonIgnore]
    public WorldAtlasElevation? AtlasElevation { get; private init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldAtlasDetails? Atlas { get; init; }

    public static FlightWorldDefinition CreateAtlas(WorldAtlasDeparture departure, WorldAtlasElevation elevation)
    {
        ArgumentNullException.ThrowIfNull(departure);
        ArgumentNullException.ThrowIfNull(elevation);
        if (departure.Id is not { Length: > 0 and <= 64 } ||
            departure.Name is not { Length: > 0 and <= 100 } ||
            !double.IsFinite(departure.Heading) || Math.Abs(departure.Heading) > Math.Tau)
            throw new ArgumentException("Invalid World Flight Atlas departure.", nameof(departure));
        var (x, z) = WorldAtlasGrid.Project(departure.Lon, departure.Lat);
        var ground = elevation.Sample(x, z).Height;
        // The sparse global surface can understate individual high peaks by kilometres.
        var mountainClearance = 2000 * Math.Clamp((ground - 2500) / 4000, 0, 1);
        var altitude = Math.Max(850, ground + 1400 + mountainClearance);
        var start = new WorldFlightDestination(departure.Id, departure.Name, x, z, altitude, departure.Heading, x, z);
        return new($"World Flight Atlas / {departure.Name}", 0, [], [], new(x, altitude, z), departure.Heading, 2.4)
        {
            Atlas = new("World Flight Atlas", start, departure.Lon, departure.Lat),
            AtlasElevation = elevation
        };
    }

    public static FlightWorldDefinition CreateGeographic(string name, WorldFlightDestination destination,
        LandingSurface? runway, WorldFlightElevation elevation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(elevation);
        if (name.Length > 80 || destination.Name.Length is 0 or > 100 ||
            !new[] { destination.X, destination.Z, destination.TargetX, destination.TargetZ,
                destination.Altitude, destination.Heading }.All(double.IsFinite) ||
            Math.Abs(destination.X) > 10_000_000 || Math.Abs(destination.Z) > 10_000_000 ||
            destination.Altitude is < 150 or > 15000 || Math.Abs(destination.Heading) > Math.Tau)
            throw new ArgumentException("World Flight destination is invalid.", nameof(destination));

        var start = new FlightVector(destination.X, destination.Altitude, destination.Z);
        var heading = destination.Heading;
        if (runway is not null)
        {
            if (!new[] { runway.CenterX, runway.CenterZ, runway.Height, runway.Width,
                    runway.Length, runway.Heading }.All(double.IsFinite) ||
                runway.Width is <= 0 or > 300 || runway.Length is < 500 or > 8000 ||
                Math.Abs(runway.CenterX) > 10_000_000 || Math.Abs(runway.CenterZ) > 10_000_000 ||
                runway.Height is < -100 or > 5000 || Math.Abs(runway.Heading) > Math.Tau)
                throw new ArgumentException("World Flight runway geometry is invalid.", nameof(runway));
            const double clearance = 2.4;
            var offset = runway.Length / 2 - 220;
            start = new(runway.CenterX - Math.Sin(runway.Heading) * offset, runway.Height + clearance,
                runway.CenterZ - Math.Cos(runway.Heading) * offset);
            heading = runway.Heading;
        }
        var world = new FlightWorldDefinition($"World Flight / {name}", 0, [],
            runway is null ? [] : [runway], start, heading, 2.4)
        {
            Elevation = elevation,
            Geography = new(name, destination)
        };
        if (elevation.Sample(start.X, start.Z) is null)
            throw new InvalidDataException("World Flight needs terrain at the selected departure point.");
        var ground = world.TrySample(start.X, start.Z)
            ?? throw new InvalidDataException("World Flight needs terrain at the selected departure point.");
        if (runway is null && start.Y < ground.Height + 120)
            throw new InvalidDataException("World Flight departure must clear the sampled terrain by at least 120 metres.");
        return world;
    }

    public static FlightWorldDefinition Default { get; } = new(
        "Interceptor Bay",
        0,
        Array.AsReadOnly(new[]
        {
            new FlightIsland("Main Island", 0, 0, 4000, 6000, 8, 0)
        }),
        Array.AsReadOnly(new[]
        {
            new LandingSurface("Bay Runway", 0, 0, 80, 3000, 8, 0)
        }),
        new FlightVector(0, 10.4, -1300),
        0,
        2.4);

    public double TerrainHeightAt(double x, double z) => Sample(x, z).Height;

    public FlightSurfaceSample Sample(double x, double z) => TrySample(x, z)
        ?? throw new InvalidOperationException("World Flight terrain has not loaded at this position.");

    internal FlightSurfaceSample? TrySample(double x, double z)
    {
        if (SourceWorld is not null)
            throw new NotSupportedException("Original-world contacts must use the source player kernel, not authored rectangle sampling.");
        if (!double.IsFinite(x))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(z));

        if (AtlasElevation is not null)
        {
            var sample = AtlasElevation.Sample(x, z);
            return new(sample.Height, sample.Water != 0, sample.Water != 0 ? "Water" : "Terrain", null);
        }

        if (Elevation is not null)
        {
            foreach (var runway in LandingSurfaces)
                if (Contains(x, z, runway.CenterX, runway.CenterZ, runway.Width, runway.Length, runway.Heading))
                    return new(runway.Height, false, runway.Name, runway);
            var ground = Elevation.Sample(x, z);
            return ground is { } sample
                ? new(sample.Height, sample.IsWater, sample.IsWater ? "Water" : "Terrain", null) : null;
        }

        var result = new FlightSurfaceSample(SeaHeight, true, "Sea", null);
        foreach (var island in Islands)
        {
            if (island.Height >= result.Height &&
                Contains(x, z, island.CenterX, island.CenterZ,
                    island.Width, island.Length, island.Heading))
                result = new FlightSurfaceSample(island.Height, false, island.Name, null);
        }

        foreach (var surface in LandingSurfaces)
        {
            if (surface.Height >= result.Height &&
                Contains(x, z, surface.CenterX, surface.CenterZ,
                    surface.Width, surface.Length, surface.Heading))
                result = new FlightSurfaceSample(surface.Height, false, surface.Name, surface);
        }

        return result;
    }

    public static bool Contains(
        double x, double z, double centerX, double centerZ,
        double width, double length, double heading)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        var cos = Math.Cos(heading);
        var sin = Math.Sin(heading);
        var localX = dx * cos - dz * sin;
        var localZ = dx * sin + dz * cos;
        return Math.Abs(localX) <= width * 0.5 && Math.Abs(localZ) <= length * 0.5;
    }
}
