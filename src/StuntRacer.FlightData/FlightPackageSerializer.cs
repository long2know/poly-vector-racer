using System.Text.Json;
using System.Text.Json.Serialization;

namespace StuntRacer.FlightData;

/// <summary>Strict versioned, external-only package boundary. Original bytes must never be published.</summary>
public static class FlightPackageSerializer
{
    public const int MaximumPackageBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = 64,
        NumberHandling = JsonNumberHandling.Strict
    };

    /// <summary>
    /// Includes the actual selected source input and extracted bytes, not just a manifest.
    /// ReadPackage re-ingests that source and compares ALL images/tables/layout/scene/provenance.
    /// </summary>
    public static byte[] WritePackage(FlightDiskPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Envelope(package), Options);
        if (bytes.Length > MaximumPackageBytes) throw new FlightDataException("package.size", "Package exceeds sixteen MiB.");
        return bytes;
    }

    public static FlightDiskPackage ReadPackage(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 2 or > MaximumPackageBytes)
            throw new FlightDataException("package.size", "Package is truncated or exceeds sixteen MiB.");
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            CheckDuplicates(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out int v) || v != 1 ||
                !root.TryGetProperty("kind", out var kind) || kind.GetString() != "fa18-amiga-runtime")
                throw new FlightDataException("package.schema", "Expected schemaVersion 1, kind fa18-amiga-runtime.");
            if (!root.TryGetProperty("sourceBytes", out var data) || data.ValueKind != JsonValueKind.String ||
                data.GetRawText().Length > 4 * ((FlightDiskReader.MaximumInputBytes + 2) / 3) + 2)
                throw new FlightDataException("package.source", "Missing or oversized original source bytes.");
            if (!root.TryGetProperty("inputFileName", out var fileName) || fileName.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new FlightDataException("package.source", "Missing inputFileName.");
            if (fileName.ValueKind == JsonValueKind.String && fileName.GetString()!.Length > 255)
                throw new FlightDataException("package.source", "Oversized input file name.");
            byte[] input = data.GetBytesFromBase64();
            var parsed = FlightDiskReader.Read(input, new FlightReadOptions(fileName.GetString()));
            JsonElement expected = JsonSerializer.SerializeToElement(Envelope(parsed), Options);
            foreach (var property in expected.EnumerateObject())
            {
                if (!root.TryGetProperty(property.Name, out var actual) || !JsonElement.DeepEquals(actual, property.Value))
                    throw new FlightDataException("package.integrity", $"Package field '{property.Name}' differs from re-ingested source/pinned data.");
            }
            if (root.EnumerateObject().Count() != expected.EnumerateObject().Count())
                throw new FlightDataException("package.schema", "Unknown package fields are not accepted.");
            return parsed;
        }
        catch (JsonException e) { throw new FlightDataException("package.json", $"Invalid package JSON: {e.Message}"); }
        catch (FormatException e) { throw new FlightDataException("package.base64", $"Invalid package source encoding: {e.Message}"); }
        catch (InvalidOperationException e) { throw new FlightDataException("package.json-type", $"Invalid package JSON value type: {e.Message}"); }
    }

    /// <summary>Metadata only; intentionally excludes original bytes/vertices/operation arrays.</summary>
    public static byte[] WriteReceipt(FlightDiskPackage p) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, kind = p.Kind, source = p.Source, layout = Layout(p), compatibility = p.Compatibility,
        tables = p.Tables.Values.Select(t => t.Metadata),
        scene = new
        {
            models = p.Scene.Models.Count, modelsWithFileGeometry = p.Scene.Models.Count(m => m.Levels.Count > 0),
            levels = p.Scene.Models.Sum(m => m.Levels.Count), originalVertexSlots = p.Scene.Models.Sum(m => m.Levels.Sum(l => l.Vertices.Count)),
            staticPolygonReferences = p.Scene.Models.Sum(m => m.Levels.Sum(l => l.Polygons.Count)),
            modelOperations = p.Scene.Models.Sum(m => m.Levels.Sum(l => l.Operations.Count)),
            partialDrawingLevels = p.Scene.Models.Sum(m => m.Levels.Count(l => l.Limitations.Count > 0)),
            terrainTiers = p.Scene.TerrainTiers.Select(t => new { t.Name, t.PositionShift, t.GridSize, cells = t.Cells.Count }),
            terrainLists = p.Scene.TerrainLists.Count, terrainItems = p.Scene.TerrainLists.Sum(l => l.Items.Count),
            waterCells = p.Scene.WaterCells.Count, waterRegions = p.Scene.WaterRegions.Count,
            noWaterRegions = p.Scene.WaterRegions.Count(r => r.NoWater),
            waterPolygons = p.Scene.WaterRegions.Sum(r => r.Polygons.Count),
            waterPolygonVertices = p.Scene.WaterRegions.Sum(r => r.Polygons.Sum(poly => poly.Vertices.Count)),
            pads = p.Scene.Pads.Count, padQuads = p.Scene.Pads.Sum(pad => (pad.QuadWords.Count - 1) / 8),
            starts = p.Scene.Starts.Count, positionBlocks = p.Scene.PositionBlocks.Count, ships = p.Scene.Ships.Count,
            sourceFootprints = p.Scene.ModelCollisions.Count,
            footprintNodes = p.Scene.ModelCollisions.Sum(c => c.Nodes.Count),
            footprintPlanes = p.Scene.ModelCollisions.Sum(c => c.Nodes.Sum(n => n.Planes.Count)),
            reorderReferenceModels = p.Scene.ModelCollisions.Count(c => c.ReorderPlanes.Count > 0),
            originalTerrainPlacements = FlightScenePlacements.Enumerate(p.Scene).Count(),
            playerContact = Contact(p, 4), carrierContact = Contact(p, 8)
        }
    }, new JsonSerializerOptions(Options) { WriteIndented = true });

    public static byte[] WriteScene(FlightDiskPackage package) => JsonSerializer.SerializeToUtf8Bytes(package.Scene, Options);

    private static object Envelope(FlightDiskPackage p) => new
    {
        schemaVersion = p.SchemaVersion, kind = p.Kind, inputFileName = p.InputFileName,
        source = p.Source, layout = Layout(p), validation = p.Compatibility,
        pins = p.Tables.Values.Select(t => t.Metadata),
        sourceBytes = p.InputBytes, flatImage = p.FlatImage.Bytes, referenceImage = p.ReferenceImage.Bytes,
        tables = p.Tables.Values.Select(t => new { metadata = t.Metadata, bytes = t.Bytes }),
        hunks = p.Hunks, relocations = p.Relocations, symbols = p.Symbols, referenceSegments = p.ReferenceSegments,
        scene = p.Scene
    };
    private static object Layout(FlightDiskPackage p) => new
    {
        imageBase = p.FlatImage.ImageBase, imageByteCount = p.FlatImage.Length,
        flatImageSha256 = p.FlatImage.Sha256, referenceImageSha256 = p.ReferenceImage.Sha256,
        referenceWindowImageSha256 = p.ReferenceWindowImage.Sha256,
        hunkCount = p.Hunks.Count, relocationCount = p.Relocations.Count,
        pitchSpanRelocations = p.Relocations.Count(r => r.FlatAddress >= 0x3249A && r.FlatAddress + 4 <= 0x325B8),
        trigSpanRelocations = p.Relocations.Count(r => r.FlatAddress >= 0x3AE92 && r.FlatAddress + 4 <= 0x49A12),
        referenceCommit = ReferenceLayout.ReferenceCommit, referenceSegmentSourceSha256 = ReferenceLayout.SegmentSourceSha256,
        referenceRelocationRule = "file addend + captured RAM base of the relocation TARGET hunk; flat-address indexing",
        sourceProfile = p.Compatibility.Profile
    };
    private static object Contact(FlightDiskPackage p, int id)
    {
        var level = p.Scene.Models.Single(m => m.Id == id).Levels[0];
        return new { modelId = id, level.BlockAddress, level.PartId, level.Flags, level.ScaleNibbles, level.ScaleLowNibble, level.ModelToPositionShift, level.InitialVertexCount, originalVertices = level.Vertices.Count };
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new FlightDataException("package.duplicate", $"Duplicate JSON property '{p.Name}'.");
                CheckDuplicates(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
}
