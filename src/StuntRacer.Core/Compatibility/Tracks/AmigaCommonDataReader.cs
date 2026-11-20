using System.Text.Json;

namespace StuntRacer.Core.Compatibility.Tracks;

public static class AmigaCommonDataReader
{
    public const int MaximumJsonBytes = 16 * 1024 * 1024;
    public const int MaximumSamplesPerProfile = 4096;

    public static AmigaCommonData Read(Stream utf8Json)
    {
        using var document = AmigaJson.ReadDocument(utf8Json, MaximumJsonBytes, "Common-data interchange");
        var root = AmigaJson.Object(document.RootElement, "common",
            "schemaVersion", "sourceId", "sourceRevision", "circuitMode",
            "encodingEvidenceId", "layoutGeometry", "coordinateTransform", "templates", "heightProfiles");
        AmigaJson.Integer(root["schemaVersion"], "common.schemaVersion", 1, 1);
        AmigaJson.Choice(root["circuitMode"], "common.circuitMode", "static");
        var sourceId = AmigaJson.Identifier(root["sourceId"], "common.sourceId");
        var revision = AmigaJson.NullableIdentifier(root["sourceRevision"], "common.sourceRevision");
        var evidence = AmigaJson.Identifier(root["encodingEvidenceId"], "common.encodingEvidenceId");
        var geometry = ReadGeometry(root["layoutGeometry"]);
        var transform = ReadTransform(root["coordinateTransform"]);

        var templates = new List<AmigaPieceTemplate>();
        var templateIds = new HashSet<int>();
        foreach (var element in AmigaJson.Array(root["templates"], "common.templates", 1, 16))
        {
            var item = AmigaJson.Object(element, "template", "id", "restartEligible", "crossSections");
            var id = AmigaJson.Integer(item["id"], "template.id", 0, 15);
            if (!templateIds.Add(id))
                throw new InvalidDataException($"Duplicate template id {id}.");
            var crossSections = new List<AmigaTemplateCrossSection>();
            foreach (var crossSection in AmigaJson.Array(
                         item["crossSections"], $"template {id}.crossSections", 2, MaximumSamplesPerProfile))
            {
                var coordinates = AmigaJson.Object(crossSection, $"template {id} cross-section",
                    "leftX", "leftZ", "rightX", "rightZ");
                var leftX = AmigaJson.Integer(coordinates["leftX"], "crossSection.leftX");
                var leftZ = AmigaJson.Integer(coordinates["leftZ"], "crossSection.leftZ");
                var rightX = AmigaJson.Integer(coordinates["rightX"], "crossSection.rightX");
                var rightZ = AmigaJson.Integer(coordinates["rightZ"], "crossSection.rightZ");
                if (leftX == rightX && leftZ == rightZ)
                    throw new InvalidDataException($"Template {id} contains a zero-planar-width cross-section.");
                crossSections.Add(new AmigaTemplateCrossSection(leftX, leftZ, rightX, rightZ));
            }
            templates.Add(new AmigaPieceTemplate(id,
                AmigaJson.Boolean(item["restartEligible"], "template.restartEligible"), crossSections.ToArray()));
        }

        var profiles = new List<AmigaHeightProfile>();
        var profileIds = new HashSet<int>();
        foreach (var element in AmigaJson.Array(root["heightProfiles"], "common.heightProfiles", 1, 128))
        {
            var item = AmigaJson.Object(element, "heightProfile",
                "id", "sampleCount", "sourceStorage", "normalization", "samples");
            var id = AmigaJson.Integer(item["id"], "heightProfile.id", 0, 127);
            if (!profileIds.Add(id))
                throw new InvalidDataException($"Duplicate height profile id {id}.");
            var count = AmigaJson.Integer(item["sampleCount"], "heightProfile.sampleCount", 1, MaximumSamplesPerProfile);
            var storage = ReadStorage(item["sourceStorage"]);
            var normalization = ReadNormalization(item["normalization"]);
            var samples = new List<long>();
            foreach (var sample in AmigaJson.Array(item["samples"], $"heightProfile {id}.samples", count, count))
            {
                var value = AmigaJson.Integer(sample, $"heightProfile {id} sample");
                NormalizeInteger(value, normalization.SampleMultiplier, normalization.SampleDivisor,
                    $"Height profile {id} sample");
                samples.Add(value);
            }
            profiles.Add(new AmigaHeightProfile(id, storage, normalization, samples.ToArray()));
        }

        return new AmigaCommonData(sourceId, revision, evidence, geometry, transform,
            templates.ToArray(), profiles.ToArray());
    }

    private static AmigaLayoutGeometry ReadGeometry(JsonElement element)
    {
        var item = AmigaJson.Object(element, "layoutGeometry", "tileSizeX", "tileSizeZ",
            "rotationPivotX", "rotationPivotZ", "positiveQuarterTurn",
            "reversalHeightBinding", "seamPolicy", "evidenceId");
        return new AmigaLayoutGeometry(
            AmigaJson.Positive(item["tileSizeX"], "layoutGeometry.tileSizeX"),
            AmigaJson.Positive(item["tileSizeZ"], "layoutGeometry.tileSizeZ"),
            AmigaJson.Integer(item["rotationPivotX"], "layoutGeometry.rotationPivotX"),
            AmigaJson.Integer(item["rotationPivotZ"], "layoutGeometry.rotationPivotZ"),
            AmigaJson.Choice(item["positiveQuarterTurn"], "layoutGeometry.positiveQuarterTurn",
                "x-to-negative-z", "x-to-positive-z"),
            AmigaJson.Choice(item["reversalHeightBinding"], "layoutGeometry.reversalHeightBinding",
                "travel-ordered", "template-ordered"),
            AmigaJson.Choice(item["seamPolicy"], "layoutGeometry.seamPolicy",
                "require-equal", "next-section-start"),
            AmigaJson.Identifier(item["evidenceId"], "layoutGeometry.evidenceId"));
    }

    private static AmigaCoordinateTransform ReadTransform(JsonElement element)
    {
        var item = AmigaJson.Object(element, "coordinateTransform",
            "xAxis", "yAxis", "zAxis", "xDivisor", "yDivisor", "zDivisor", "evidenceId");
        string Axis(string field) => AmigaJson.Choice(item[field], $"coordinateTransform.{field}",
            "+x", "-x", "+y", "-y", "+z", "-z");
        var x = Axis("xAxis");
        var y = Axis("yAxis");
        var z = Axis("zAxis");
        if (new[] { x[1], y[1], z[1] }.Distinct().Count() != 3)
            throw new InvalidDataException("coordinateTransform must use each absolute source axis exactly once.");
        if (y[1] != 'y' || x[1] == 'y' || z[1] == 'y')
            throw new InvalidDataException(
                "coordinateTransform must preserve vertical Y; projecting a horizontal axis into height is unsupported.");
        return new AmigaCoordinateTransform(x, y, z,
            AmigaJson.Positive(item["xDivisor"], "coordinateTransform.xDivisor"),
            AmigaJson.Positive(item["yDivisor"], "coordinateTransform.yDivisor"),
            AmigaJson.Positive(item["zDivisor"], "coordinateTransform.zDivisor"),
            AmigaJson.Identifier(item["evidenceId"], "coordinateTransform.evidenceId"));
    }

    private static AmigaProfileStorage ReadStorage(JsonElement element)
    {
        var item = AmigaJson.Object(element, "sourceStorage", "kind", "leftHighBit", "evidenceId");
        return new AmigaProfileStorage(
            AmigaJson.Choice(item["kind"], "sourceStorage.kind",
                "normalized-integers", "decoded-packed-byte", "decoded-big-endian-word"),
            AmigaJson.Boolean(item["leftHighBit"], "sourceStorage.leftHighBit"),
            AmigaJson.Identifier(item["evidenceId"], "sourceStorage.evidenceId"));
    }

    private static AmigaHeightNormalization ReadNormalization(JsonElement element)
    {
        var item = AmigaJson.Object(element, "normalization",
            "sampleMultiplier", "sampleDivisor", "shiftMultiplier", "shiftDivisor", "evidenceId");
        return new AmigaHeightNormalization(
            AmigaJson.Positive(item["sampleMultiplier"], "normalization.sampleMultiplier"),
            AmigaJson.Positive(item["sampleDivisor"], "normalization.sampleDivisor"),
            AmigaJson.Positive(item["shiftMultiplier"], "normalization.shiftMultiplier"),
            AmigaJson.Positive(item["shiftDivisor"], "normalization.shiftDivisor"),
            AmigaJson.Identifier(item["evidenceId"], "normalization.evidenceId"));
    }

    internal static long NormalizeInteger(long value, long multiplier, long divisor, string description)
    {
        try
        {
            var numerator = checked(value * multiplier);
            if (numerator % divisor != 0)
                throw new InvalidDataException($"{description} normalization is fractional; no rounding is permitted.");
            return numerator / divisor;
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"{description} normalization overflows signed 64-bit source coordinates.", exception);
        }
    }
}
