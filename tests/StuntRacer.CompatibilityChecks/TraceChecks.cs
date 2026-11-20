using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StuntRacer.Core.Compatibility.Physics;

namespace StuntRacer.CompatibilityChecks;

internal static class TraceChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("trace / full typed raw state and independently hashed synthetic inputs", ValidSyntheticTrace);
        yield return new("trace / every raw field and first differing tick are compared", RawDifferences);
        yield return new("trace / every recorded input field is compared", InputDifferences);
        yield return new("trace / absent oracle/evidence/timing cannot become an authored fallback", MissingEvidence);
        yield return new("trace / baseline, table, mapping and rational timing compatibility", Metadata);
        yield return new("trace / required, duplicate and unknown JSON fields are strict", StrictFields);
        yield return new("trace / integer widths, decimals and wrong types are rejected", StrictTypes);
        yield return new("trace / contiguous ticks, EOF, length and malformed tails are strict", SequenceAndEof);
    }

    public static AmigaTrace Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return AmigaTraceReader.Read(stream);
    }

    public static AmigaTraceVerification Verify(byte[] expected, byte[] actual)
    {
        using var left = new MemoryStream(expected, writable: false);
        using var right = new MemoryStream(actual, writable: false);
        return AmigaTraceVerifier.Verify(left, right);
    }

    public static void Rehash(List<JsonObject> records)
    {
        records[0]["inputSha256"] = null;
        var parsed = Read(SyntheticTrace.Bytes(records));
        records[0]["inputSha256"] = AmigaTraceReader.ComputeInputSha256(parsed.Transitions);
    }

    private static void ValidSyntheticTrace()
    {
        var bytes = SyntheticTrace.Bytes(SyntheticTrace.Create());
        using var stream = new MemoryStream(bytes, writable: false);
        var trace = AmigaTraceReader.Read(stream);
        Check.Require(stream.CanRead, "trace reader closed caller stream");
        Check.Equal(2, trace.Transitions.Count, "trace transition count excludes initial state");
        Check.Equal(int.MinValue, trace.InitialState.Position.X, "raw signed int32 minimum");
        Check.Equal((ushort)65535, trace.InitialState.Angles.Yaw, "raw unsigned angle maximum");
        Check.Equal(short.MinValue, trace.InitialState.LinearVelocity.X, "raw signed int16 minimum");
        Check.Equal(uint.MaxValue, trace.InitialState.Runtime.Flags, "raw unsigned int32 maximum");
        Check.Equal((byte)255, trace.InitialState.Contacts.FrontLeft.Damage, "raw contact byte maximum");
        Check.Equal((short)-1234, trace.Transitions[1].Input.Steer, "signed recorded input");
        Check.Equal((byte)127, trace.Transitions[1].Input.Brake, "recorded brake");
        Check.Require(trace.Transitions[1].Input.Recover && !trace.Transitions[1].Input.Boost, "typed recorded input booleans");
        Check.Equal(SyntheticTrace.InputSha256, AmigaTraceReader.ComputeInputSha256(trace.Transitions),
            "independent Python struct >qhBBBB two-tick input digest");
        var result = Verify(bytes, SyntheticTrace.Bytes(SyntheticTrace.Create(), finalNewline: false));
        Check.Equal("match", result.Status, "same synthetic raw trace with/without final newline");
        Check.Equal(2L, result.ComparedTicks, "all synthetic transitions compared");
        Check.Require(result.FirstMismatchTick is null && result.FirstMismatchField is null, "match has mismatch diagnostics");
        Check.Equal("synthetic-only", result.Qualification, "synthetic match must remain nonqualifying");
        Check.Equal("synthetic", result.OracleBasis, "synthetic provenance must not become executable provenance");
        Check.Require(result.Reason.Contains("no original", StringComparison.OrdinalIgnoreCase), "synthetic result omits qualification boundary");
        Check.Throws<InvalidDataException>(() => AmigaTraceReader.ComputeInputSha256([]), "empty input digest");
        Check.Throws<InvalidDataException>(() => AmigaTraceReader.ComputeInputSha256(
            trace.Transitions.Select(transition => transition with { Tick = 1 })), "duplicate digest ticks");
    }

    private static void RawDifferences()
    {
        var expected = SyntheticTrace.Bytes(SyntheticTrace.Create());
        var paths = LeafPaths(SyntheticTrace.Create()[3]["after"]!).ToArray();
        foreach (var path in paths)
        {
            var actual = SyntheticTrace.Create();
            var before = At(actual[3]["after"]!, path)!;
            var value = long.Parse(before.ToJsonString(), CultureInfo.InvariantCulture);
            Set(actual[3]["after"]!, path, value > 0 ? value - 1 : value < 0 ? value + 1 : 1);
            var result = Verify(expected, SyntheticTrace.Bytes(actual));
            Mismatch(result, 2, 2, "after." + path);
            Check.Equal(value.ToString(CultureInfo.InvariantCulture), result.ExpectedValue, $"raw expected value {path}");
            Check.Require(result.ActualValue != result.ExpectedValue, $"raw diagnostics conceal difference {path}");
        }

        var initial = SyntheticTrace.Create();
        initial[1]["state"]!["contacts"]!["rear"]!["previousCompression"] = 0;
        Mismatch(Verify(expected, SyntheticTrace.Bytes(initial)), 0, 0, "initial.state.contacts.rear.previousCompression");
        var earliest = SyntheticTrace.Create();
        earliest[2]["after"]!["position"]!["z"] = 5;
        earliest[3]["after"]!["position"]!["x"] = 6;
        Mismatch(Verify(expected, SyntheticTrace.Bytes(earliest)), 1, 1, "after.position.z");
    }

    private static void InputDifferences()
    {
        var expected = SyntheticTrace.Bytes(SyntheticTrace.Create());
        (string Field, JsonNode Value)[] mutations =
        [
            ("steer", JsonValue.Create(-1234)!),
            ("throttle", JsonValue.Create(254)!),
            ("brake", JsonValue.Create(1)!),
            ("boost", JsonValue.Create(false)!),
            ("recover", JsonValue.Create(true)!)
        ];
        foreach (var (field, value) in mutations)
        {
            var actual = SyntheticTrace.Create();
            actual[2]["input"]![field] = value.DeepClone();
            Check.Throws<InvalidDataException>(() => Read(SyntheticTrace.Bytes(actual)), $"stale input digest after {field} mutation");
            Rehash(actual);
            Mismatch(Verify(expected, SyntheticTrace.Bytes(actual)), 1, 1, "input." + field);
        }
    }

    private static void MissingEvidence()
    {
        var valid = SyntheticTrace.Bytes(SyntheticTrace.Create());
        Blocked(Verify([], valid), "expected");
        Blocked(Verify(valid, []), "actual");
        Check.Throws<InvalidDataException>(() => Read([]), "empty reader trace");
        foreach (var path in new[]
                 {
                     "baseline.executableSha256", "baseline.version", "baseline.region", "tablesSha256",
                     "inputSha256", "stateMappingSha256", "timing.evidenceSha256", "provenance.evidenceSha256"
                 })
        {
            var unknown = SyntheticTrace.Create();
            Set(unknown[0], path, null);
            Blocked(Verify(SyntheticTrace.Bytes(unknown), valid), "expected.header." + path);
        }
        var timing = SyntheticTrace.Create();
        timing[0]["timing"]!["status"] = "unknown";
        timing[0]["timing"]!["numerator"] = null;
        timing[0]["timing"]!["denominator"] = null;
        var bytes = SyntheticTrace.Bytes(timing);
        var parsed = Read(bytes);
        Check.Require(parsed.Header.Timing.Numerator is null && parsed.Header.Timing.Denominator is null,
            "unknown timing acquired a numeric stock/authored step");
        Blocked(Verify(bytes, bytes), "expected.header.timing");
        foreach (var kind in new[] { "same-implementation", "candidate-implementation", "remake", "unknown" })
        {
            var records = SyntheticTrace.Create();
            records[0]["provenance"]!["kind"] = kind;
            Blocked(Verify(SyntheticTrace.Bytes(records), valid), "expected.header.provenance.kind");
        }
    }

    private static void Metadata()
    {
        var expected = SyntheticTrace.Bytes(SyntheticTrace.Create());
        foreach (var path in new[]
                 {
                     "baseline.executableSha256", "baseline.version", "baseline.region",
                     "tablesSha256", "stateMappingSha256", "timing.evidenceSha256"
                 })
        {
            var actual = SyntheticTrace.Create();
            Set(actual[0], path, path.EndsWith("Sha256", StringComparison.Ordinal)
                ? new string('a', 64) : "different-synthetic-metadata");
            var result = Verify(expected, SyntheticTrace.Bytes(actual));
            Blocked(result, "header." + path);
            Check.Require(result.ExpectedValue != result.ActualValue, $"metadata diagnostics {path}");
        }
        var differentTiming = SyntheticTrace.Create();
        differentTiming[0]["timing"]!["denominator"] = 42;
        Blocked(Verify(expected, SyntheticTrace.Bytes(differentTiming)), "header.timing");
        var equivalentTiming = SyntheticTrace.Create();
        equivalentTiming[0]["timing"]!["numerator"] = 14;
        equivalentTiming[0]["timing"]!["denominator"] = 82;
        Check.Equal("match", Verify(expected, SyntheticTrace.Bytes(equivalentTiming)).Status, "equivalent exact timing rationals");
        Reject(records => records[0]["schemaVersion"] = 2, "unknown trace schema");
        Reject(records => records[0]["stateSchema"] = "invented-state", "unknown raw-state schema");
        Reject(records => records[0]["timing"]!["denominator"] = 0, "zero known tick denominator");
        Reject(records => records[0]["tablesSha256"] = new string('A', 64), "uppercase digest");
    }

    private static void StrictFields()
    {
        foreach (var field in SyntheticTrace.Create()[0].Select(property => property.Key).ToArray())
            Reject(records => records[0].Remove(field), $"required header {field}");
        foreach (var path in FieldPaths(SyntheticTrace.Create()[1]["state"]!).ToArray())
            Reject(records => Remove(records[1]["state"]!, path), $"required full raw state {path}");
        foreach (var field in new[] { "steer", "throttle", "brake", "boost", "recover" })
            Reject(records => records[2]["input"]!.AsObject().Remove(field), $"required input {field}");
        Reject(records => records[2].Remove("input"), "missing tick input");
        Reject(records => records[2].Remove("after"), "missing tick after state");
        Reject(records => records[0]["unknown"] = 1, "unknown header field");
        Reject(records => records[1]["state"]!["contacts"]!["fourthWheel"] = 0, "unknown physical contact");
        Reject(records => records[2]["input"]!["unknown"] = 0, "unknown input field");
        Reject(records => records[3]["after"]!["runtime"]!["unknown"] = 0, "unknown raw-state field");
        var text = Encoding.UTF8.GetString(SyntheticTrace.Bytes(SyntheticTrace.Create()));
        RejectText(text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            "duplicate header field");
        RejectText(text.Replace("\"x\":-2147483648", "\"x\":-2147483648,\"x\":-2147483648", StringComparison.Ordinal),
            "duplicate raw-state field");
    }

    private static void StrictTypes()
    {
        (string Path, JsonNode Value)[] invalid =
        [
            ("position.x", JsonValue.Create(2147483648L)!),
            ("position.y", JsonValue.Create(-2147483649L)!),
            ("angles.yaw", JsonValue.Create(-1)!),
            ("angles.pitch", JsonValue.Create(65536)!),
            ("linearVelocity.x", JsonValue.Create(-32769)!),
            ("angularVelocity.roll", JsonValue.Create(32768)!),
            ("contacts.frontLeft.damage", JsonValue.Create(256)!),
            ("contacts.rear.flags", JsonValue.Create(-1)!),
            ("resources.engineRevs", JsonValue.Create(65536)!),
            ("road.section", JsonValue.Create(65536)!),
            ("road.lateralPosition", JsonValue.Create(32768)!),
            ("runtime.flags", JsonValue.Create(4294967296L)!),
            ("runtime.offTrackTicks", JsonValue.Create(65536)!),
            ("runtime.clockAccumulator", JsonValue.Create(256)!),
            ("position.z", JsonValue.Create("3")!)
        ];
        foreach (var (path, value) in invalid)
            Reject(records => Set(records[1]["state"]!, path, value.DeepClone()), $"typed raw field {path}");
        Reject(records => records[2]["input"]!["steer"] = -32769, "input signed width");
        Reject(records => records[2]["input"]!["throttle"] = 256, "input unsigned width");
        Reject(records => records[2]["input"]!["brake"] = -1, "input negative byte");
        Reject(records => records[2]["input"]!["boost"] = 1, "input numeric boolean");
        var text = Encoding.UTF8.GetString(SyntheticTrace.Bytes(SyntheticTrace.Create()));
        foreach (var number in new[] { "1.0", "1e0", "1E0" })
            RejectText(text.Replace("\"x\":-2147483648", "\"x\":" + number, StringComparison.Ordinal),
                $"integer lexeme {number}");
    }

    private static void SequenceAndEof()
    {
        var expected = SyntheticTrace.Bytes(SyntheticTrace.Create());
        Reject(records => records[0]["transitionCount"] = 0, "no logical transition");
        Reject(records => records[0]["transitionCount"] = 1000001, "bounded transition count");
        Reject(records => records.RemoveAt(1), "missing initial state");
        Reject(records => records[1]["tick"] = 1, "initial tick must be zero");
        Reject(records => records[3]["tick"] = 1, "duplicate transition");
        Reject(records => records[3]["tick"] = 3, "missing contiguous transition");
        Reject(records => records.RemoveAt(3), "premature EOF");
        Reject(records => records.Add((JsonObject)records[3].DeepClone()), "extra tail");
        var text = Encoding.UTF8.GetString(expected);
        RejectText(text + "\n", "blank trailing record");
        RejectText(text.Replace("\n", "\n\n", StringComparison.Ordinal), "blank interior record");
        RejectText(text[..^3], "truncated final JSON");
        Check.Throws<InvalidDataException>(() => Read([0xef, 0xbb, 0xbf, .. expected]), "UTF8 BOM");
        Check.Throws<InvalidDataException>(() => Read([0xff, .. expected]), "invalid UTF8");
        var shorter = SyntheticTrace.Create();
        shorter.RemoveAt(3);
        shorter[0]["transitionCount"] = 1;
        Rehash(shorter);
        Mismatch(Verify(expected, SyntheticTrace.Bytes(shorter)), 2, 1, "transitions.length");
        var longer = SyntheticTrace.Create();
        var third = (JsonObject)longer[3].DeepClone();
        third["tick"] = 3;
        longer.Add(third);
        longer[0]["transitionCount"] = 3;
        Rehash(longer);
        Mismatch(Verify(expected, SyntheticTrace.Bytes(longer)), 3, 2, "transitions.length");
        var corruptTail = SyntheticTrace.Create();
        corruptTail[1]["state"]!["position"]!["x"] = 5;
        var malformedAfterEarlierMismatch = SyntheticTrace.Bytes(corruptTail).Concat("{}\n"u8.ToArray()).ToArray();
        Check.Throws<InvalidDataException>(() => Verify(expected, malformedAfterEarlierMismatch),
            "early mismatch must not conceal malformed later tail");
    }

    private static void Mismatch(AmigaTraceVerification result, long tick, long compared, string field)
    {
        Check.Equal("mismatch", result.Status, field);
        Check.Equal<long?>(tick, result.FirstMismatchTick, $"{field} first tick");
        Check.Equal(compared, result.ComparedTicks, $"{field} compared transitions");
        Check.Equal(field, result.FirstMismatchField, "exact mismatch field");
        Check.Require(result.ExpectedValue is not null && result.ActualValue is not null, $"{field} missing values");
    }

    private static void Blocked(AmigaTraceVerification result, string field)
    {
        Check.Equal("blocked", result.Status, field);
        Check.Equal(0L, result.ComparedTicks, $"{field} must not compare incompatible traces");
        Check.Require(result.FirstMismatchTick is null, $"{field} invented mismatch tick");
        Check.Equal(field, result.FirstMismatchField, "concrete blocking field");
    }

    private static void Reject(Action<List<JsonObject>> mutate, string context)
    {
        var records = SyntheticTrace.Create();
        mutate(records);
        Check.Throws<InvalidDataException>(() => Read(SyntheticTrace.Bytes(records)), context);
    }

    private static void RejectText(string text, string context) =>
        Check.Throws<InvalidDataException>(() => Read(Encoding.UTF8.GetBytes(text)), context);

    private static IEnumerable<string> LeafPaths(JsonNode node, string prefix = "")
    {
        foreach (var (name, value) in node.AsObject())
        {
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            if (value is JsonObject child)
                foreach (var nested in LeafPaths(child, path))
                    yield return nested;
            else
                yield return path;
        }
    }

    private static IEnumerable<string> FieldPaths(JsonNode node, string prefix = "")
    {
        foreach (var (name, value) in node.AsObject())
        {
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            yield return path;
            if (value is JsonObject child)
                foreach (var nested in FieldPaths(child, path))
                    yield return nested;
        }
    }

    private static JsonNode? At(JsonNode root, string path)
    {
        JsonNode? current = root;
        foreach (var component in path.Split('.'))
            current = current![component];
        return current;
    }

    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var components = path.Split('.');
        for (var index = 0; index < components.Length - 1; index++)
            root = root[components[index]]!;
        root[components[^1]] = value;
    }

    private static void Remove(JsonNode root, string path)
    {
        var components = path.Split('.');
        for (var index = 0; index < components.Length - 1; index++)
            root = root[components[index]]!;
        root.AsObject().Remove(components[^1]);
    }
}
