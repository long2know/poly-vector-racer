using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StuntRacer.Core.Compatibility;
using StuntRacer.Core.Compatibility.Tracks;

namespace StuntRacer.Core.SourceRuntime;

public static class SourceAmigaPackageReader
{
    public const int MaximumJsonBytes = 16 * 1024 * 1024;

    public static SourceAmigaPackage Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length is < 1 or > MaximumJsonBytes)
            throw new InvalidDataException("Source package text must contain 1..16 MiB characters.");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false);
        return Read(stream);
    }

    public static SourceAmigaPackage Read(Stream utf8Json)
    {
        try
        {
            return ReadCore(utf8Json);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid source package: {exception.Message}", exception);
        }
    }

    private static SourceAmigaPackage ReadCore(Stream stream)
    {
        using var document = AmigaJson.ReadDocument(stream, MaximumJsonBytes, "Reference source package");
        var root = AmigaJson.Object(document.RootElement, "package", "schemaVersion", "kind", "profileId",
            "originalStockParity", "name", "layoutBase64", "commonData", "units", "clock",
            "lookups", "templateMetadata", "constants", "provenance");
        AmigaJson.Integer(root["schemaVersion"], "schemaVersion", 1, 1);
        AmigaJson.Choice(root["kind"], "kind", "stunt-amiga-source-runtime");
        AmigaJson.Choice(root["profileId"], "profileId", "amiga-derived-source-compatible");
        if (AmigaJson.Boolean(root["originalStockParity"], "originalStockParity"))
            throw new InvalidDataException("Reference packages cannot claim original stock parity.");
        var name = AmigaJson.Text(root["name"], "name");
        if (root["layoutBase64"].ValueKind != JsonValueKind.String)
            throw new InvalidDataException("layoutBase64 must contain exactly one encoded 804-byte layout.");
        var encoded = root["layoutBase64"].GetString()!;
        if (encoded.Length != 1072)
            throw new InvalidDataException("layoutBase64 must contain exactly 1072 canonical Base64 characters.");
        var bytes = Convert.FromBase64String(encoded);
        if (Convert.ToBase64String(bytes) != encoded)
            throw new InvalidDataException("layoutBase64 must be canonical, without whitespace.");
        var layout = AmigaTrackLayoutReader.ReadReferenceRemake(bytes);
        using var commonStream = new MemoryStream(Encoding.UTF8.GetBytes(root["commonData"].GetRawText()), writable: false);
        var common = AmigaCommonDataReader.Read(commonStream);
        var units = Units(root["units"]);
        var clock = Clock(root["clock"]);
        var lookups = Lookups(root["lookups"]);
        var constants = Constants(root["constants"]);
        foreach (var required in new[] { "restartLaneOffsetPcUnits", "restartDropWheelUnits" })
            if (!constants.ContainsKey(required))
                throw new InvalidDataException($"constants.{required} is required.");
        var metadata = Metadata(root["templateMetadata"]);
        foreach (var template in common.Templates)
        {
            var item = metadata.SingleOrDefault(item => item.Id == template.Id);
            if (item is null || item.NumSegments + 1 != template.CrossSections.Count ||
                template.RestartEligible != ((item.RestartFlags & 128) == 0))
                throw new InvalidDataException($"Template {template.Id} metadata does not match the common template.");
        }
        if (metadata.Length != common.Templates.Count)
            throw new InvalidDataException("templateMetadata must describe exactly the supplied populated templates.");
        ValidateCommon(common, units);
        var provenance = Provenance(root["provenance"], bytes, common);
        return new SourceAmigaPackage(name, layout, common, units, clock, lookups, metadata, constants, provenance);
    }

    private static SourceAmigaUnits Units(JsonElement element)
    {
        var item = AmigaJson.Object(element, "units", "pcFactor", "positionPrecision", "localYFactor",
            "roadHeightDivisor", "displayMetresPerPcUnit", "evidenceId");
        return new SourceAmigaUnits(
            AmigaJson.Integer(item["pcFactor"], "units.pcFactor", 2, 2),
            AmigaJson.Integer(item["positionPrecision"], "units.positionPrecision", 16384, 16384),
            AmigaJson.Integer(item["localYFactor"], "units.localYFactor", 4, 4),
            AmigaJson.Integer(item["roadHeightDivisor"], "units.roadHeightDivisor", 4, 4),
            PositiveNumber(item["displayMetresPerPcUnit"], "units.displayMetresPerPcUnit"),
            AmigaJson.Identifier(item["evidenceId"], "units.evidenceId"));
    }

    private static SourceAmigaClock Clock(JsonElement element)
    {
        var item = AmigaJson.Object(element, "clock", "callbackHertz", "callbacksPerStep",
            "initialCallbackRunsImmediately", "simulationStepSeconds", "evidenceId");
        var hz = AmigaJson.Integer(item["callbackHertz"], "clock.callbackHertz", 1, 1000);
        var gap = AmigaJson.Integer(item["callbacksPerStep"], "clock.callbacksPerStep", 1, 1000);
        var step = PositiveNumber(item["simulationStepSeconds"], "clock.simulationStepSeconds");
        if (step != gap / (double)hz)
            throw new InvalidDataException("clock.simulationStepSeconds must exactly equal callbacksPerStep/callbackHertz.");
        return new SourceAmigaClock(hz, gap,
            AmigaJson.Boolean(item["initialCallbackRunsImmediately"], "clock.initialCallbackRunsImmediately"),
            step, AmigaJson.Identifier(item["evidenceId"], "clock.evidenceId"));
    }

    private static SourceAmigaLookups Lookups(JsonElement element)
    {
        var item = AmigaJson.Object(element, "lookups", "sinQ14", "cosineConversion", "numericTables");
        if (item["sinQ14"].ValueKind != JsonValueKind.Array ||
            item["sinQ14"].GetArrayLength() is not (65536 or 81920))
            throw new InvalidDataException("lookups.sinQ14 requires 65536 periodic or 81920 reference-overlap entries.");
        var sine = AmigaJson.Array(item["sinQ14"], "lookups.sinQ14", 65536, 81920)
            .Select(value => AmigaJson.Integer(value, "sinQ14 value", -16384, 16384)).ToArray();
        var cosine = AmigaJson.Array(item["cosineConversion"], "lookups.cosineConversion", 128, 128)
            .Select(value => AmigaJson.Integer(value, "cosineConversion value", 0, 256)).ToArray();
        if (item["numericTables"].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("lookups.numericTables must be an object.");
        var tables = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var property in item["numericTables"].EnumerateObject())
        {
            if (tables.Count >= 64 || string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 80 ||
                property.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new InvalidDataException("Numeric table names/count are invalid.");
            var values = AmigaJson.Array(property.Value, "numeric table", 1, 65536)
                .Select(value => AmigaJson.Integer(value, "numeric table value")).ToArray();
            if (!tables.TryAdd(property.Name, values))
                throw new InvalidDataException($"Duplicate numeric table '{property.Name}'.");
        }
        return new SourceAmigaLookups(sine, cosine, tables);
    }

    private static Dictionary<string, long> Constants(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("constants must be an object.");
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (result.Count >= 128 || property.Name.Length is < 1 or > 80 ||
                property.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new InvalidDataException("Constant names/count are invalid.");
            if (!result.TryAdd(property.Name, AmigaJson.Integer(property.Value, $"constant {property.Name}")))
                throw new InvalidDataException($"Duplicate constant '{property.Name}'.");
        }
        if (result.Values.Any(value => value is < int.MinValue or > int.MaxValue))
            throw new InvalidDataException("Reference constants must fit signed 32-bit values.");
        return result;
    }

    private static SourceTemplateMetadata[] Metadata(JsonElement element)
    {
        var result = new Dictionary<int, SourceTemplateMetadata>();
        foreach (var row in AmigaJson.Array(element, "templateMetadata", 1, 16))
        {
            var item = AmigaJson.Object(row, "templateMetadata row", "id", "numSegments", "curveToLeft",
                "type", "lengthReduction", "steeringAmount", "restartFlags");
            var id = AmigaJson.Integer(item["id"], "templateMetadata.id", 0, 15);
            var value = new SourceTemplateMetadata(id,
                AmigaJson.Integer(item["numSegments"], "numSegments", 1, 13),
                AmigaJson.Boolean(item["curveToLeft"], "curveToLeft"),
                AmigaJson.Integer(item["type"], "type", 0, 255),
                AmigaJson.Integer(item["lengthReduction"], "lengthReduction", 0, 255),
                AmigaJson.Integer(item["steeringAmount"], "steeringAmount", 0, 255),
                AmigaJson.Integer(item["restartFlags"], "restartFlags", 0, 255));
            if (!result.TryAdd(id, value))
                throw new InvalidDataException($"Duplicate template metadata id {id}.");
        }
        return result.Values.OrderBy(item => item.Id).ToArray();
    }

    private static CompatibilityAssetProvenance[] Provenance(JsonElement element, byte[] layout, AmigaCommonData common)
    {
        var result = new Dictionary<string, CompatibilityAssetProvenance>(StringComparer.Ordinal);
        foreach (var row in AmigaJson.Array(element, "provenance", 2, 32))
        {
            var item = AmigaJson.Object(row, "provenance asset",
                "role", "byteLength", "sha256", "sourceId", "sourceRevision");
            var role = AmigaJson.Identifier(item["role"], "asset.role");
            var length = AmigaJson.Integer(item["byteLength"], "asset.byteLength");
            var hash = AmigaJson.Text(item["sha256"], "asset.sha256");
            if (length < 0 || hash.Length != 64 ||
                hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Provenance requires nonnegative exact lengths and lowercase SHA-256 hashes.");
            var asset = new CompatibilityAssetProvenance(role, length, hash,
                AmigaJson.Identifier(item["sourceId"], "asset.sourceId"),
                AmigaJson.NullableIdentifier(item["sourceRevision"], "asset.sourceRevision"));
            if (!result.TryAdd(role, asset))
                throw new InvalidDataException($"Duplicate provenance role '{role}'.");
        }
        if (!result.TryGetValue("layout", out var layoutAsset) ||
            layoutAsset.ByteLength != layout.Length ||
            layoutAsset.Sha256 != Convert.ToHexStringLower(SHA256.HashData(layout)))
            throw new InvalidDataException("Embedded layout byte count/SHA-256 must match provenance.layout.");
        if (!result.TryGetValue("trackSource", out var sourceAsset) ||
            sourceAsset.SourceId != common.SourceId || sourceAsset.SourceRevision != common.SourceRevision)
            throw new InvalidDataException("Common-data identity must match provenance.trackSource.");
        return result.Values.OrderBy(asset => asset.Role, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateCommon(AmigaCommonData common, SourceAmigaUnits units)
    {
        var geometry = common.LayoutGeometry;
        var transform = common.CoordinateTransform;
        if (geometry.TileSizeX != 2048 || geometry.TileSizeZ != 2048 ||
            geometry.RotationPivotX != 1024 || geometry.RotationPivotZ != 1024 ||
            geometry.PositiveQuarterTurn != "x-to-negative-z" || geometry.ReversalHeightBinding != "travel-ordered" ||
            geometry.SeamPolicy != "next-section-start" ||
            transform.XAxis != "+x" || transform.YAxis != "+y" || transform.ZAxis != "+z" ||
            transform.XDivisor != 1 || transform.YDivisor != units.RoadHeightDivisor || transform.ZDivisor != 1)
            throw new InvalidDataException("This reference package requires declared 2048-unit tiles, centre rotation, travel heights, next-start seams and Y/4 display.");
        foreach (var profile in common.HeightProfiles)
            if (profile.SampleCount > 14 ||
                profile.Normalization is not { SampleMultiplier: 1, SampleDivisor: 1, ShiftMultiplier: 1, ShiftDivisor: 1 })
                throw new InvalidDataException("Reference decoded profiles and shifts must already share their native integer units.");
    }

    private static double PositiveNumber(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value <= 0)
            throw new InvalidDataException($"{path} must be finite and positive.");
        return value;
    }
}
