using System.Text;
using System.Text.Json.Nodes;
using StuntRacer.Core.Compatibility.Tracks;

namespace StuntRacer.CompatibilityChecks;

internal static class CommonChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("common / all required fields and evidence are explicit", RequiredFields);
        yield return new("common / strict JSON, unique IDs and exact sample counts", StrictStructure);
        yield return new("common / unsupported storage, axes and flag semantics fail closed", UnsupportedSemantics);
        yield return new("common / integer normalization rejects fractions and overflow", Normalization);
    }

    public static AmigaCommonData Read(JsonNode json)
    {
        using var stream = new MemoryStream(SyntheticCommon.Bytes(json), writable: false);
        return AmigaCommonDataReader.Read(stream);
    }

    private static void RequiredFields()
    {
        var valid = SyntheticCommon.Create();
        foreach (var field in valid.Select(property => property.Key).ToArray())
            Reject(json => json.Remove(field), $"common missing {field}");
        foreach (var objectName in new[] { "layoutGeometry", "coordinateTransform" })
        {
            var fields = valid[objectName]!.AsObject().Select(property => property.Key).ToArray();
            foreach (var field in fields)
                Reject(json => json[objectName]!.AsObject().Remove(field), $"{objectName} missing {field}");
        }
        foreach (var field in valid["heightProfiles"]![0]!["normalization"]!.AsObject().Select(property => property.Key).ToArray())
            Reject(json => json["heightProfiles"]![0]!["normalization"]!.AsObject().Remove(field), $"normalization missing {field}");
        foreach (var field in valid["heightProfiles"]![0]!["sourceStorage"]!.AsObject().Select(property => property.Key).ToArray())
            Reject(json => json["heightProfiles"]![0]!["sourceStorage"]!.AsObject().Remove(field), $"storage missing {field}");
        Reject(json => json["templates"] = new JsonArray(), "no bundled native templates");
        Reject(json => json["heightProfiles"] = new JsonArray(), "no bundled native profiles");
        Reject(json => json["encodingEvidenceId"] = "", "empty encoding evidence");
        Reject(json => json["coordinateTransform"]!["evidenceId"] = "", "empty transform evidence");
        Reject(json => json["layoutGeometry"]!["evidenceId"] = "", "empty geometry evidence");

        var common = Read(valid);
        Check.Equal(4, common.Templates[0].CrossSections.Count, "synthetic cross-section count");
        Check.Equal(-5L, common.Templates[0].CrossSections[1].LeftX, "signed decoded geometry");
        Check.Equal(7L, common.HeightProfiles[0].Samples[2], "decoded profile sample");
        Check.Equal("synthetic-decoded-integers-not-native", common.EncodingEvidenceId, "interchange evidence identity");
        valid["heightProfiles"]![0]!["samples"]![2] = 999;
        Check.Equal(7L, common.HeightProfiles[0].Samples[2], "parsed common data aliases mutable input");
    }

    private static void StrictStructure()
    {
        Reject(json => json["schemaVersion"] = 2, "unknown schema version");
        Reject(json => json["unexpected"] = 1, "unknown common property");
        Reject(json => json["coordinateTransform"]!["expression"] = "1+2", "unknown transform expression");
        Reject(json => json["templates"]!.AsArray().Add(json["templates"]![0]!.DeepClone()), "duplicate template ID");
        Reject(json => json["heightProfiles"]!.AsArray().Add(json["heightProfiles"]![0]!.DeepClone()), "duplicate profile ID");
        Reject(json => json["templates"]![0]!["id"] = 16, "out-of-range template ID");
        Reject(json => json["heightProfiles"]![0]!["id"] = 128, "out-of-range profile ID");
        Reject(json => json["heightProfiles"]![0]!["sampleCount"] = 3, "declared sample count disagrees");
        Reject(json => json["heightProfiles"]![0]!["samples"]!.AsArray().RemoveAt(0), "truncated samples");
        Reject(json => json["heightProfiles"]![0]!["samples"]!.AsArray().Add(0), "trailing samples");
        Reject(json => json["templates"]![0]!["crossSections"] = new JsonArray(
            json["templates"]![0]!["crossSections"]![0]!.DeepClone()), "single cross-section template");
        Reject(json => json["templates"]![0]!["crossSections"]![0]!["leftX"] = "1+2", "coordinates are not expressions");
        Reject(json => json["heightProfiles"]![0]!["samples"]![0] = 1.5, "samples are typed integers");
        Reject(json => json["heightProfiles"]![0]!["sampleCount"] = "4", "count string coercion");
        Reject(json => json["templates"]![0]!["restartEligible"] = 1, "boolean coercion");
        var text = SyntheticCommon.Create().ToJsonString();
        RejectText(text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            "duplicate JSON property");
        RejectText(text + "{}", "trailing JSON document");
        RejectText(text[..^1], "truncated JSON");
    }

    private static void UnsupportedSemantics()
    {
        Reject(json => json["circuitMode"] = "dynamic", "dynamic circuit is not silently frozen");
        Reject(json => json["heightProfiles"]![0]!["sourceStorage"]!["kind"] = "native-default", "unknown storage");
        Reject(json => json["heightProfiles"]![0]!["sourceStorage"]!["leftHighBit"] = 128, "storage flag is explicit boolean");
        Reject(json => json["layoutGeometry"]!["positiveQuarterTurn"] = "guess", "unknown quarter-turn semantics");
        Reject(json => json["layoutGeometry"]!["reversalHeightBinding"] = "guess", "unknown reversal-height binding");
        Reject(json => json["layoutGeometry"]!["seamPolicy"] = "smooth", "unknown seam semantics");
        Reject(json => json["coordinateTransform"]!["xAxis"] = "+w", "unknown source axis");
        Reject(json => json["coordinateTransform"]!["zAxis"] = "-x", "duplicate source axis");
        Reject(json =>
        {
            json["coordinateTransform"]!["xAxis"] = "+y";
            json["coordinateTransform"]!["yAxis"] = "+x";
        }, "unsupported horizontal-to-height transform");
        foreach (var field in new[] { "xDivisor", "yDivisor", "zDivisor" })
        {
            Reject(json => json["coordinateTransform"]![field] = 0, $"zero {field}");
            Reject(json => json["coordinateTransform"]![field] = -1, $"negative {field}");
        }
    }

    private static void Normalization()
    {
        Reject(json => json["heightProfiles"]![0]!["normalization"]!["sampleDivisor"] = 3,
            "fractional samples must not round");
        Reject(json => json["heightProfiles"]![0]!["normalization"]!["sampleMultiplier"] = long.MaxValue,
            "sample normalization overflow");
        foreach (var field in new[] { "sampleMultiplier", "sampleDivisor", "shiftMultiplier", "shiftDivisor" })
            Reject(json => json["heightProfiles"]![0]!["normalization"]![field] = 0, $"zero normalization {field}");
    }

    private static void Reject(Action<JsonObject> mutate, string context)
    {
        var json = SyntheticCommon.Create();
        mutate(json);
        Check.Throws<InvalidDataException>(() => Read(json), context);
    }

    private static void RejectText(string text, string context)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
        Check.Throws<InvalidDataException>(() => AmigaCommonDataReader.Read(stream), context);
    }
}
