using System.Buffers.Binary;
using System.Text.Json;
using StuntRacer.FlightData;

internal static class ModelChecks
{
    internal static void Synthetic(Action<string, Action> check, Action<bool, string> require)
    {
        check("Model strips retain Q4 truncation, interleaved return slots and original scale", () =>
        {
            var vertices = new[] { new FlightVertex(0, 3, 5, 7) };
            short[] section = [1, -7, 9, 13, 10, -6, 32, 3, 1, -7, 0, 13, 3, -1];
            var result = Build(vertices, 8, section, []);
            var slots = result.SourceVertices.ToDictionary(v => v.Slot);
            require(slots.Count == 8 && slots[3] == new FlightVertex(3, -2, 4, 19) &&
                slots[5] == new FlightVertex(5, 4, -1, 25) && slots[6] == new FlightVertex(6, 4, -4, 25) &&
                slots[2] == new FlightVertex(2, -7, 0, 13), "Strip interpolation or return interleaving changed.");
            result = Build(vertices, 8, section, [Op(13, 1, 6, 0x8000 | 42)]);
            require(result.Lines.Count == 1 && result.Vertices.Any(p => p == new FlightModelPoint(-7 * 128, 9 * 128, 13 * 128)),
                "Materialized endpoints were not converted to original position units.");
            section[8] = unchecked((short)0x8001);
            result = Build(vertices, 8, section, []);
            require(result.SourceVertices.Any(v => v.Slot == 8) && result.SourceVertices.All(v => v.Slot != 2),
                "The strip return's high bit was mistaken for a negative terminator/count.");
        });
        check("Relative sections use later-minus-earlier, zero-count pairs, continuation points and word wrap", () =>
        {
            FlightVertex[] vertices = [new(0, 1, 0, 2), new(1, 4, 0, 6)];
            short[] section = [0, 32766, 32766, 32767, 7, 11, 12, 18, 1, 100, 200, -1];
            var result = Build(vertices, 6, section, []);
            var slots = result.SourceVertices.ToDictionary(v => v.Slot);
            require(slots.Count == 8 && slots[3] == new FlightVertex(3, -32767, 0, -32766) &&
                slots[6] == new FlightVertex(6, 100, 0, 200) && slots[7] == new FlightVertex(7, 105, 0, 207),
                "Relative groups lost signed wrapping or reused the first group's delta.");
        });
        check("Building synthesis keeps negative odd shifts and all derived corners", () =>
        {
            FlightVertex[] vertices = [new(0, 1, 9, 0), new(1, 1, 0, 0), new(2, 0, 0, 0), new(3, 3, 0, 4)];
            var geometry = Build(vertices, 0, [], [Op(34, 0, ushort.MaxValue)]);
            require(geometry.SourceVertices.Count == 10 &&
                geometry.SourceVertices.Single(v => v.Slot == 8).X == 5,
                "Synthesis added a shifted negation instead of subtracting the arithmetic-shifted difference.");
        });
        check("Hatches use the fourth corner and accumulated half-steps; three-slot row lines need no fourth slot", () =>
        {
            FlightVertex[] vertices = [new(0, 10, 0, 0), new(1, 0, 0, 9), new(2, 0, 0, 0), new(3, 2, 0, 4)];
            var geometry = Build(vertices, 2, [], [Op(20, 0, 3)]);
            require(geometry.Triangles.Count == 12 &&
                geometry.Vertices.Contains(new(12 * 128, 0, 4 * 128)) &&
                geometry.Vertices.Contains(new(2 * 128, 0, 13 * 128)),
                "Strips were guessed from three points or used k*half(Q) instead of half(k*Q).");
            geometry = Build(vertices[..3], 2, [], [Op(22, 7, 0, 1, 2)]);
            require(geometry.Lines.Count == 2, "A line-row operation incorrectly requires a fourth source slot.");
        });
        check("Concave source polygons are triangulated without filling their notch", () =>
        {
            FlightVertex[] vertices = [new(0, 0, 0, 0), new(1, 6, 0, 0), new(2, 6, 0, 6),
                new(3, 3, 0, 3), new(4, 0, 0, 6)];
            var geometry = Build(vertices, 2, [], [Op(25, 5, 7)]);
            long twiceArea = geometry.Triangles.Sum(t =>
            {
                var a = geometry.Vertices[t.A]; var b = geometry.Vertices[t.B]; var c = geometry.Vertices[t.C];
                return Math.Abs((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X));
            });
            require(geometry.Triangles.Count == 3 && twiceArea == 54L * 128 * 128,
                "Source concavity was replaced with a triangle fan covering the notch.");
        });
        check("Unavailable generated slots and malformed sections fail instead of becoming zero geometry", () =>
        {
            foreach (Action action in new Action[]
            {
                () => Build([new(0, 0, 0, 0)], 0, [], [Op(2, 0, 6, 7)]),
                () => Build([new(0, 0, 0, 0), new(1, 1, 0, 1)], 6, [300], []),
                () => Build([new(0, 0, 0, 0)], 8, [1, 0, 0, 0, 1, 1, 1, 513], [])
            })
            {
                try { action(); }
                catch (FlightDataException e) when (e.Code == "models.materialize") { continue; }
                throw new InvalidOperationException("An undefined slot or oversized source program was accepted.");
            }
        });
    }

    internal static void Package(FlightDiskPackage package, string? oraclePath,
        Action<string, Action> check, Action<bool, string> require)
    {
        check("EXTERNAL every placed static model materializes original primitives without mutating the package scene", () =>
        {
            byte[] before = FlightPackageSerializer.WriteScene(package);
            var ids = FlightScenePlacements.Enumerate(package.Scene).Where(p => !p.RuntimeModelSlot)
                .Select(p => p.ModelId).Append(8).Distinct().Order().ToArray();
            int triangleCount = 0, lineCount = 0;
            foreach (int id in ids)
            {
                var geometry = FlightModelMaterializer.Materialize(package, id);
                require(geometry.Triangles.Count + geometry.Lines.Count > 0, $"Placed model {id} is still invisible.");
                require(geometry.Triangles.All(t => new[] { t.A, t.B, t.C }.All(v => v >= 0 && v < geometry.Vertices.Count)) &&
                    geometry.Lines.All(l => l.A >= 0 && l.B >= 0 && l.A < geometry.Vertices.Count && l.B < geometry.Vertices.Count),
                    $"Model {id} references missing materialized endpoints.");
                triangleCount += geometry.Triangles.Count;
                lineCount += geometry.Lines.Count;
            }
            require(before.AsSpan().SequenceEqual(FlightPackageSerializer.WriteScene(package)),
                "Runtime materialization changed the serialized scene/package ingestion contract.");
            Console.WriteLine($"  materialized {ids.Length} original models / {triangleCount} triangles / {lineCount} lines before placement");
        });
        check("EXTERNAL original bridge strips and relative/ground programs are no longer empty meshes", () =>
        {
            foreach (int id in Enumerable.Range(48, 10).Append(65))
            {
                var model = package.Scene.Models.Single(m => m.Id == id);
                var geometry = FlightModelMaterializer.Materialize(package, id);
                require(geometry.Lines.Count > 0 && geometry.SourceVertices.Count > model.Levels[0].InitialVertexCount,
                    $"Bridge/strip model {id} still has no source-generated geometry.");
                if (id != 65) require(geometry.Triangles.Count > 0, $"Bridge {id} has no original deck/faces.");
            }
            foreach (int id in new[] { 46, 72, 73, 95, 43, 44, 45, 59, 60, 68, 69, 70, 71, 74, 75, 76, 77, 78, 79, 91, 92, 93, 94, 97 })
                require(FlightModelMaterializer.Materialize(package, id).Triangles.Count > 0,
                    $"Relative-group or hatch model {id} still has no source polygons.");
            foreach (int id in new[] { 61, 62, 63, 64, 90, 102, 103, 107, 108 })
            {
                var geometry = FlightModelMaterializer.Materialize(package, id);
                require(geometry.Triangles.Count >= 6 && geometry.Vertices.Max(v => v.Y) > geometry.Vertices.Min(v => v.Y),
                    $"Building {id} lost its original synthesized volume.");
            }
        });
        if (oraclePath == null) return;
        check("EXTERNAL generated slots and bridge line endpoints match independently computed actual-data oracle", () =>
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(oraclePath));
            var root = document.RootElement;
            require(root.GetProperty("flatImageSha256").GetString() == package.FlatImage.Sha256, "Geometry oracle image identity differs.");
            var oracleModels = root.GetProperty("models");
            var modelLines = root.GetProperty("lines");
            foreach (var id in new[] { "48", "50", "54" })
                require(oracleModels.GetProperty(id).GetArrayLength() > 1 && modelLines.GetProperty(id).GetArrayLength() > 0,
                    $"Geometry oracle omits representative strip/line model {id}.");
            foreach (var model in oracleModels.EnumerateObject())
            {
                int id = int.Parse(model.Name, System.Globalization.CultureInfo.InvariantCulture);
                var geometry = FlightModelMaterializer.Materialize(package, id);
                var source = geometry.SourceVertices.ToDictionary(v => v.Slot);
                int slot = 0;
                foreach (var vertex in model.Value.EnumerateArray())
                {
                    int state = vertex[3].GetInt32();
                    if (state is 1 or 2 or 3 or 5 && source.TryGetValue(slot, out var actual))
                        require(actual.X == vertex[0].GetInt32() && actual.Y == vertex[1].GetInt32() &&
                            actual.Z == vertex[2].GetInt32(), $"Model {id}, slot {slot} differs from independent source geometry.");
                    if (state is 2 or 3 or 5) require(source.ContainsKey(slot), $"Model {id} lost oracle generated slot {slot}.");
                    slot++;
                }
            }
            foreach (var model in modelLines.EnumerateObject())
            {
                int id = int.Parse(model.Name, System.Globalization.CultureInfo.InvariantCulture);
                var geometry = FlightModelMaterializer.Materialize(package, id);
                var actual = geometry.Lines.Select(line => Key(geometry.Vertices[line.A], geometry.Vertices[line.B], line.ColourWord & 15)).ToHashSet();
                var expected = model.Value.EnumerateArray().Select(line => Key(
                    new(line[0].GetInt64(), line[1].GetInt64(), line[2].GetInt64()),
                    new(line[3].GetInt64(), line[4].GetInt64(), line[5].GetInt64()), line[6].GetInt32())).ToHashSet();
                require(actual.SetEquals(expected), $"Model {id} bridge lines differ: actual {actual.Count}, oracle {expected.Count}.");
            }
        });
    }

    private static string Key(FlightModelPoint a, FlightModelPoint b, int pen)
    {
        string x = $"{a.X},{a.Y},{a.Z}", y = $"{b.X},{b.Y},{b.Z}";
        return string.CompareOrdinal(x, y) <= 0 ? $"{x}|{y}|{pen}" : $"{y}|{x}|{pen}";
    }

    private static FlightModelOperation Op(int operation, params ushort[] words) =>
        new(0x10100, operation, words, "invented test operation") { ProgramAddress = 0x10100 };

    private static FlightModelGeometry Build(FlightVertex[] vertices, byte flags, short[] section, FlightModelOperation[] operations)
    {
        byte[] bytes = new byte[256];
        int p = 10 + vertices.Length * ((flags & 2) != 0 ? 4 : 6);
        foreach (short word in section)
        {
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(p, 2), word);
            p += 2;
        }
        var level = new FlightModelLevel(0x10000, 0x10000, 0, 0, flags, 1, 0, vertices,
            [], operations, "invented model program", []) { InitialVertexCount = vertices.Length };
        var model = new FlightModel(200, 0, 0x10000, 0x10000, 0x10000, 0, "invented model", [level]);
        return FlightModelMaterializer.Materialize(new FlightImage(bytes), model, level);
    }
}
