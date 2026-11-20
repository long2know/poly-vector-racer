namespace StuntRacer.Core.City;

public readonly record struct CityPoint(double X, double Y, double Z);

public sealed record CityBounds(double MinX, double MaxX, double MinZ, double MaxZ);

// Points define carriageway centerlines; linear Y and full Width define the actual contact quads.
// SidewalkHeight is relative to that road surface. Water areas cut holes in the GroundY plane.
public sealed record CityRoadDefinition(
    string Id, string Name, string District, double Width, double SidewalkWidth,
    double SidewalkHeight, bool Bridge, CityPoint[] Points);

public sealed record CityBuildingDefinition(
    string Id, string Name, double CenterX, double CenterZ, double Width, double Depth,
    double Height, double BaseY, double Yaw, string Style);

public sealed record CityAreaDefinition(string Id, string Name, string Kind, CityPoint[] Points);

public sealed record CityStructureDefinition(
    string Id, string Name, double CenterX, double CenterZ, double Width, double Depth,
    double Height, double BaseY, double Yaw, string Style, bool Solid);

public sealed record CityWalkwayDefinition(string Id, string Name, double Width, CityPoint[] Points);

// Y is the street surface, not the car COM. Heading uses the same positive-right radians as CarSnapshot.
public sealed record CityCheckpointDefinition(
    string Id, string Name, double X, double Y, double Z, double Heading, double Width);

// Route lines are navigation only; Roads/GroundY/Areas own physical and visible surfaces.
public sealed record CityRouteDefinition(
    string Id, string Name, bool Closed, CityPoint[] Points,
    CityCheckpointDefinition[] Checkpoints, int Laps, double TimeLimit);

public sealed record CityWorldDefinition(
    string Name, CityBounds Bounds, double GroundY,
    CityRoadDefinition[] Roads, CityBuildingDefinition[] Buildings, CityAreaDefinition[] Areas,
    CityStructureDefinition[] Structures, CityWalkwayDefinition[] Walkways, CityRouteDefinition[] Routes)
{
    public static CityWorldDefinition Create()
    {
        var roads = new List<CityRoadDefinition>();
        double[] xs = [-640, -400, -160, 80, 400];
        string[] avenues = ["Foundry Road", "Parkside Avenue", "Commerce Street", "Central Avenue", "Quayside"];
        for (var i = 0; i < xs.Length; i++)
            roads.Add(new CityRoadDefinition($"avenue-{i}", avenues[i],
                i == 0 ? "Foundry" : i == 1 ? "Garden Quarter" : i == 4 ? "Waterfront" : "Downtown",
                i == 3 ? 32 : 28, 3.2, 0.14, false, [P(xs[i], -730), P(xs[i], 690)]));
        double[] zs = [-640, -400, -160, 80, 320, 560];
        string[] streets = ["Freight Street", "Southgate", "Market Street", "Civic Boulevard", "Museum Street", "Northgate"];
        for (var i = 0; i < zs.Length; i++)
            roads.Add(new CityRoadDefinition($"street-{i}", streets[i],
                i < 2 ? "Foundry" : i > 3 ? "Garden Quarter" : "Downtown",
                i == 3 ? 32 : 28, 3.2, 0.14, false, [P(-740, zs[i]), P(420, zs[i])]));
        roads.Add(new CityRoadDefinition("harbor-avenue", "Harbor Avenue", "East Harbor",
            28, 3.2, 0.14, false, [P(780, -730), P(780, 690)]));
        roads.Add(new CityRoadDefinition("harbor-service", "Terminal Road", "East Harbor",
            18, 2.4, 0.14, false, [P(870, -160), P(870, 560)]));
        foreach (var z in new[] { -640.0, -160, 560 })
            roads.Add(new CityRoadDefinition($"harbor-{z}", "Wharf Street", "East Harbor",
                24, 3.2, 0.14, false, [P(678, z), P(888, z)]));
        roads.Add(new CityRoadDefinition("meridian-bridge", "Meridian Bridge", "Waterfront",
            28, 0, 0, true,
            [P(400, 80), P(420, 80), P(480, 80, 12), P(520, 80, 20),
             P(600, 80, 20), P(660, 80, 12), P(720, 80), P(878, 80)]));

        var areas = new[]
        {
            Rectangle("meridian-river", "Meridian River", "water", 440, 640, -820, 820, -1.5),
            Rectangle("garden-park", "Lantern Park", "park", -381, -179, 99, 301, 0.02),
            Rectangle("civic-plaza", "Civic Plaza", "plaza", -141, 61, 99, 301, 0.02),
            Rectangle("river-promenade", "Riverwalk", "plaza", 419, 438, -700, 690, 0.02),
            Rectangle("harbor-yard", "East Harbor Yard", "industrial", 668, 892, -610, -190, 0.02)
        };
        var buildings = new List<CityBuildingDefinition>();
        for (var ix = 0; ix < xs.Length - 1; ix++)
        for (var iz = 0; iz < zs.Length - 1; iz++)
        {
            if (iz == 3 && ix is 1 or 2)
                continue;
            var industrial = iz == 0 || ix == 0 && iz == 1;
            var downtown = ix >= 2 && iz is >= 1 and <= 3;
            var columns = ix == 3 ? 3 : 2;
            for (var row = 0; row < 2; row++)
            for (var column = 0; column < columns; column++)
            {
                var cellWidth = (xs[ix + 1] - xs[ix] - 50) / columns;
                var cellDepth = (zs[iz + 1] - zs[iz] - 50) / 2;
                var x = xs[ix] + 25 + cellWidth * (column + 0.5);
                var z = zs[iz] + 25 + cellDepth * (row + 0.5);
                var seed = ix * 31 + iz * 17 + row * 7 + column * 11;
                var height = industrial ? 13 + seed % 4 * 4
                    : downtown ? 46 + seed % 7 * 18 : 19 + seed % 5 * 7;
                buildings.Add(new CityBuildingDefinition($"block-{ix}-{iz}-{row}-{column}",
                    industrial ? "Foundry Workshops" : downtown ? "Meridian Exchange" : "Garden Apartments",
                    x, z, cellWidth - 16, cellDepth - 17, height, 0, 0,
                    industrial ? "brick-warehouse" : downtown ? seed % 2 == 0 ? "glass-blue" : "sandstone-tower"
                        : "terracotta"));
            }
        }
        buildings.Add(new CityBuildingDefinition("city-hall", "Meridian City Hall", -40, 223,
            96, 59, 27, 0, 0, "civic"));
        buildings.Add(new CityBuildingDefinition("harbor-terminal", "Harbor Terminal", 709, 339,
            62, 142, 20, 0, 0, "brick-warehouse"));
        buildings.Add(new CityBuildingDefinition("harbor-silo", "Harbor Grain House", 707, -362,
            48, 96, 34, 0, 0, "industrial"));

        var structures = new List<CityStructureDefinition>
        {
            new("clock", "Civic Clock", -42, 138, 8, 8, 34, 0, 0, "clocktower", true),
            new("park-pavilion", "Lantern Pavilion", -280, 225, 22, 18, 8, 0, 0, "pavilion", true),
            new("harbor-crane", "East Harbor Crane", 688, -525, 10, 10, 55, 0, 0, "crane", true)
        };
        foreach (var x in new[] { 480.0, 640 })
        foreach (var z in new[] { 61.0, 99 })
            structures.Add(new CityStructureDefinition($"bridge-tower-{x}-{z}", "Meridian Bridge Tower",
                x, z, 7, 7, 49, -4, 0, "bridge-tower", true));
        foreach (var x in new[] { 500.0, 600 })
            structures.Add(new CityStructureDefinition($"bridge-pier-{x}", "Bridge Pier",
                x, 80, 9, 24, 19, -4, 0, "bridge-pier", true));

        CityWalkwayDefinition[] walkways =
        [
            new("park-loop", "Lantern Park Path", 2.8,
                [P(-372, 108, 0.04), P(-188, 108, 0.04), P(-188, 292, 0.04),
                 P(-372, 292, 0.04), P(-372, 108, 0.04)]),
            new("plaza-walk", "Civic Plaza Walk", 3.0,
                [P(-126, 116, 0.04), P(46, 116, 0.04), P(46, 182, 0.04),
                 P(-126, 182, 0.04), P(-126, 116, 0.04)]),
            new("river-walk", "Riverwalk Path", 3.0,
                [P(429, -610, 0.04), P(429, -100, 0.04)]),
            new("market-sidewalk", "Market Sidewalk", 2.0,
                [P(108, -177, 0.18), P(370, -177, 0.18)])
        ];

        var circuit = CityPath.Round(
            [P(-160, -290), P(-160, 320), P(400, 320), P(400, -400), P(-160, -400)],
            true, 35);
        var blitz = CityPath.Round(
            [P(80, -530), P(80, 80), P(-640, 80), P(-640, -640), P(400, -640),
             P(400, 80), P(480, 80, 12), P(520, 80, 20),
             P(600, 80, 20), P(660, 80, 12), P(780, 80), P(780, 450)],
            false, 35);
        CityRouteDefinition[] routes =
        [
            new("city-circuit", "Downtown / Quayside Circuit", true, circuit,
                [Gate("civic", "Civic Boulevard", -160, 80, 0, 28),
                 Gate("museum", "Museum Street", 80, 320, Math.PI / 2, 28),
                 Gate("quayside", "Quayside", 400, 80, Math.PI, 28),
                 Gate("market", "Market Street South", 400, -160, Math.PI, 28),
                 Gate("southgate", "Southgate", 80, -400, -Math.PI / 2, 28),
                 Gate("finish", "Commerce Finish", -160, -290, 0, 28)], 2, 0),
            new("city-blitz", "Cross-City Blitz", false, blitz,
                [Gate("market", "Market Street", 80, -160, 0, 32),
                 Gate("park", "Lantern Park", -400, 80, -Math.PI / 2, 32),
                 Gate("foundry", "Foundry Road", -640, -400, Math.PI, 28),
                 Gate("quayside", "Quayside", 400, -160, 0, 28),
                 Gate("bridge", "Meridian Bridge", 560, 80, Math.PI / 2, 28, 20),
                 Gate("harbor", "Harbor Finish", 780, 400, 0, 28)], 1, 245)
        ];
        return new CityWorldDefinition("Meridian — original open city", new CityBounds(-840, 920, -820, 820), 0,
            roads.ToArray(), buildings.ToArray(), areas, structures.ToArray(), walkways, routes);
    }

    private static CityPoint P(double x, double z, double y = 0.04) => new(x, y, z);

    private static CityCheckpointDefinition Gate(string id, string name, double x, double z,
        double heading, double width, double y = 0.04) => new(id, name, x, y, z, heading, width);

    private static CityAreaDefinition Rectangle(string id, string name, string kind,
        double minX, double maxX, double minZ, double maxZ, double y) =>
        new(id, name, kind, [new(minX, y, minZ), new(maxX, y, minZ), new(maxX, y, maxZ), new(minX, y, maxZ)]);
}
