using StuntRacer.Flight;
using StuntRacer.FlightData;
using StuntRacer.FlightReference;

namespace StuntRacer.FlightOriginal;

internal sealed class OriginalWorldBuilder
{
    private readonly FlightDiskPackage package;
    private readonly RawPosition origin;
    private readonly FlightTerrainPlacement[] placements;
    private readonly Dictionary<int, FlightModelCollision> collisions;
    private readonly Dictionary<int, FlightModelGeometry> materialized = [];
    public FlightWorldDefinition World { get; }

    public OriginalWorldBuilder(FlightDiskPackage package, RawPosition origin, FlightCarrier carrier)
    {
        this.package = package;
        this.origin = origin;
        placements = FlightScenePlacements.Enumerate(package.Scene).ToArray();
        collisions = package.Scene.ModelCollisions.ToDictionary(model => model.ModelId);
        var surfaces = new List<FlightWorldSurface>();
        var landingSurfaces = new List<LandingSurface>();
        var meshes = new List<FlightWorldMesh>();
        var notes = new HashSet<string>
        {
            "Static scenery uses original nearest-detail programs with BSP/visibility branches united, not original painter order.",
            "Source line endpoints are retained; thin polygon beams and a ground-detail depth bias are presentation only, not contact geometry.",
            "Source palette pens are retained; original screen-space stipple/bitplane toggling is not emulated by solid WebGL faces."
        };
        foreach (var cell in package.Scene.WaterCells)
        {
            var region = package.Scene.WaterRegions[cell.RegionIndex];
            foreach (var polygon in region.Polygons)
            {
                var points = polygon.Vertices.Select(v => Position(
                    ((long)cell.X << 24) + (long)v.X * 4096, 0,
                    ((long)cell.Z << 24) + (long)v.Z * 4096)).ToArray();
                surfaces.Add(new("water", FaceUp(points, 0.008)));
            }
        }
        var direction = package.Tables["flight_dir"];
        foreach (var pad in package.Scene.Pads.Where(p => p.Words[1] >= 0))
        {
            var word = pad.Words;
            long x = ((long)word[1] << 24) + direction.ReadInt16(word[3] * 2) * 1024L + word[4] * 1024L;
            long z = ((long)word[2] << 24) + direction.ReadInt16(word[3] * 2 + 1) * 1024L + word[5] * 1024L;
            for (var i = 0; i + 7 < pad.QuadWords.Count && pad.QuadWords[i] != -1; i += 8)
            {
                var points = Enumerable.Range(0, 4)
                    .Select(j => Position(x + pad.QuadWords[i + j * 2] * 256L, 0,
                        z + pad.QuadWords[i + j * 2 + 1] * 256L)).ToArray();
                surfaces.Add(new("runway", FaceUp(points, 0.025)));
                var longest = Enumerable.Range(0, 4).OrderByDescending(j =>
                    Distance(points[j], points[(j + 1) % 4])).First();
                var a = points[longest];
                var b = points[(longest + 1) % 4];
                var length = Distance(a, b);
                var width = Distance(b, points[(longest + 2) % 4]);
                landingSurfaces.Add(new($"Airfield {pad.Index + 1} / strip {i / 8 + 1}",
                    points.Average(p => p.X), points.Average(p => p.Z), width, length, 0,
                    Math.Atan2(b.X - a.X, b.Z - a.Z)));
            }
        }
        AddModel(meshes, notes, 8, carrier.Position.X, carrier.Position.Y, carrier.Position.Z, "Original carrier");
        var deck = package.Scene.Models.Single(item => item.Id == 8).Levels[0].Vertices.Take(58)
            .Where(vertex => vertex.Y == 896)
            .Select(vertex => Position(carrier.Position.X + vertex.X * 32L, 28672,
                carrier.Position.Z + vertex.Z * 32L)).ToArray();
        if (deck.Length < 3) throw new InvalidOperationException("The original carrier deck vertices were not found.");
        landingSurfaces.Add(new("Carrier deck", (deck.Min(p => p.X) + deck.Max(p => p.X)) / 2,
            (deck.Min(p => p.Z) + deck.Max(p => p.Z)) / 2, deck.Max(p => p.X) - deck.Min(p => p.X),
            deck.Max(p => p.Z) - deck.Min(p => p.Z), 28672 * OriginalFlightSession.MetresPerPosition, 0));
        var emitted = new HashSet<(int Model, long X, long Z)>();
        foreach (var placement in placements)
        {
            if (placement.RuntimeModelSlot)
            {
                notes.Add("Original runtime object slots 16..32 are not replaced with invented static meshes.");
                continue;
            }
            if (emitted.Add((placement.ModelId, placement.PositionX, placement.PositionZ)))
                AddModel(meshes, notes, placement.ModelId, placement.PositionX, placement.PositionY,
                    placement.PositionZ, $"Original scenery {placement.ModelId}");
        }
        var navigation = landingSurfaces.Select(p => new FlightVector(p.CenterX, 0, p.CenterZ))
            .Append(Position(carrier.Position.X, 0, carrier.Position.Z)).Append(new(0, 0, 0)).ToArray();
        var minX = navigation.Min(p => p.X) - 5000;
        var maxX = navigation.Max(p => p.X) + 5000;
        var minZ = navigation.Min(p => p.Z) - 5000;
        var maxZ = navigation.Max(p => p.Z) + 5000;
        FlightVector[] outline = [new(minX, 0, minZ), new(maxX, 0, minZ), new(maxX, 0, maxZ), new(minX, 0, maxZ)];
        var heading = package.Scene.Starts[0].Words[7] * Math.PI / 180;
        World = new("Amiga Interceptor Bay", 0, [], landingSurfaces,
            Position(origin.X, origin.Y, origin.Z), heading, 1800 * OriginalFlightSession.MetresPerPosition,
            new(surfaces, meshes, outline, notes.Order().ToArray()));
    }

    private FlightVector Position(long x, long y, long z) => new(
        -(x - origin.X) * OriginalFlightSession.MetresPerPosition,
        y * OriginalFlightSession.MetresPerPosition,
        (z - origin.Z) * OriginalFlightSession.MetresPerPosition);

    private static double Distance(FlightVector a, FlightVector b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));

    private static FlightVector[] FaceUp(FlightVector[] points, double height)
    {
        var area = points.Select((p, i) => p.X * points[(i + 1) % points.Length].Z -
            points[(i + 1) % points.Length].X * p.Z).Sum();
        var result = points.Select(p => p with { Y = height }).ToArray();
        if (area > 0) Array.Reverse(result);
        return result;
    }

    private void AddModel(List<FlightWorldMesh> meshes, HashSet<string> notes,
        int id, long x, long y, long z, string name)
    {
        var model = package.Scene.Models.Single(item => item.Id == id);
        if (model.Levels.Count == 0)
            throw new InvalidOperationException($"Original static model {id} has no decoded geometry.");
        if (!materialized.TryGetValue(id, out var geometry))
        {
            geometry = FlightModelMaterializer.Materialize(package, id);
            materialized.Add(id, geometry);
        }
        foreach (var limitation in geometry.Limitations) notes.Add($"Model {id}: {limitation}");
        if (geometry.DegeneratePolygons != 0)
            notes.Add("Literal zero-area source polygons are not replaced with invented terrain; their original line detail is retained.");
        bool ground = geometry.Vertices.All(vertex => vertex.Y == 0);
        var vertices = geometry.Vertices.Select(vertex =>
        {
            var position = Position(x + vertex.X, y + vertex.Y, z + vertex.Z);
            return ground ? position with { Y = position.Y + 0.015 } : position;
        }).ToList();
        var faces = new List<FlightWorldFace>();
        double[] Colour(ushort word)
        {
            int rgb = package.FlatImage.ReadUInt16(0x29FA4 + (word & 15) * 2);
            return [((rgb >> 8) & 15) / 15.0, ((rgb >> 4) & 15) / 15.0, (rgb & 15) / 15.0];
        }
        foreach (var triangle in geometry.Triangles)
            // Source X is reflected into display X; reverse the original winding.
            faces.Add(new([triangle.C, triangle.B, triangle.A], Colour(triangle.ColourWord)));
        foreach (var line in geometry.Lines)
            AddLineBeam(vertices, faces, vertices[line.A], vertices[line.B], Colour(line.ColourWord), ground);
        if (faces.Count == 0)
            throw new InvalidOperationException($"Original static model {id} produced no visible source primitives.");
        meshes.Add(new(name, vertices, faces));
    }

    private static void AddLineBeam(List<FlightVector> vertices, List<FlightWorldFace> faces,
        FlightVector a, FlightVector b, double[] colour, bool ground)
    {
        const double halfWidth = 0.4;
        if (ground) { a = a with { Y = a.Y + 0.025 }; b = b with { Y = b.Y + 0.025 }; }
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double horizontal = Math.Sqrt(dx * dx + dz * dz), length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length == 0) throw new InvalidOperationException("A materialized source line has coincident endpoints.");
        var side = horizontal > 1e-12
            ? new FlightVector(-dz / horizontal * halfWidth, 0, dx / horizontal * halfWidth)
            : new FlightVector(halfWidth, 0, 0);
        var up = new FlightVector(
            (dy * side.Z - dz * side.Y) / length,
            (dz * side.X - dx * side.Z) / length,
            (dx * side.Y - dy * side.X) / length);
        FlightVector Offset(FlightVector p, FlightVector d, int sign) =>
            new(p.X + d.X * sign, p.Y + d.Y * sign, p.Z + d.Z * sign);
        int first = vertices.Count;
        vertices.AddRange([Offset(a, side, -1), Offset(a, side, 1), Offset(b, side, 1), Offset(b, side, -1),
            Offset(a, up, -1), Offset(a, up, 1), Offset(b, up, 1), Offset(b, up, -1)]);
        faces.Add(new([first, first + 1, first + 2, first + 3], colour));
        faces.Add(new([first + 4, first + 5, first + 6, first + 7], colour));
    }

    public IReadOnlyList<FlightLandmark> Landmarks(RawPosition player)
    {
        var cellX = (short)(player.X >> 22);
        var cellZ = (short)(player.Z >> 22);
        var candidates = placements.Where(p => p.Tier == "C" && collisions.ContainsKey(p.ModelId) &&
            Math.Abs(p.CellX - cellX) <= 1 && Math.Abs(p.CellZ - cellZ) <= 1).Reverse().Take(11).ToArray();
        var result = new List<FlightLandmark>();
        foreach (var placement in candidates)
        {
            var source = collisions[placement.ModelId];
            if (source.ReferenceLandmarkZBias != 48)
                throw new NotSupportedException("The selected source landmark uses another reference RAM-placement profile.");
            short X(short value) => unchecked((short)(value + placement.FineXInCCell));
            short Z(short value) => unchecked((short)(value + placement.FineZInCCell));
            FlightContactBox Box(FlightCollisionBox box) => new(box.MaximumHeight,
                X(box.MinimumX), X(box.MaximumX), Z(box.MinimumZ), Z(box.MaximumZ));
            var nodes = source.Nodes.Select(node => new FlightLandmarkNode(Box(node.Box),
                node.Planes.Select(plane => new FlightContactPlane(
                    new(X(plane.ReferenceX), plane.ReferenceY, Z(plane.ReferenceZ)),
                    new(plane.NormalX, plane.NormalY, plane.NormalZ))).ToArray())).ToArray();
            result.Add(new(placement.CellX, placement.CellZ, Box(source.OuterBox), nodes, (byte)result.Count));
        }
        return result;
    }
}
