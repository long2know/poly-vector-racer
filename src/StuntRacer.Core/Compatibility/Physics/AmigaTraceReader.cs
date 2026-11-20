using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StuntRacer.Core.Compatibility.Physics;

/// <summary>
/// Strict UTF-8 JSONL v1 reader, leaving the caller's stream open. See README.md in
/// this directory for the candidate capture schema and input-hash byte contract.
/// All malformed/unsupported trace data throws InvalidDataException.
/// </summary>
public static class AmigaTraceReader
{
    public const int SchemaVersion = 1;
    public const string StateSchema = "amiga-raw-state-v1";
    public const int MaximumTransitions = 1_000_000;
    public const int MaximumRecordCharacters = 65_536;

    public static AmigaTrace Read(Stream utf8JsonLines) =>
        ReadOrNull(utf8JsonLines) ?? throw new InvalidDataException("The trace has no header or records.");

    /// <summary>
    /// SHA-256 of UTF-8 "amiga-physics-input-v1\n" followed by each transition's
    /// big-endian int64 tick, int16 steer, then throttle, brake, boost and recover
    /// bytes (booleans are 0/1). No JSON spelling or after-state bytes are hashed.
    /// </summary>
    public static string ComputeInputSha256(IEnumerable<AmigaTraceTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        using var hash = CreateInputHash();
        long tick = 0;
        foreach (var transition in transitions)
        {
            if (tick >= MaximumTransitions || transition.Tick != ++tick)
                throw new InvalidDataException("Input transitions must be contiguous ticks 1..N.");
            AppendInput(hash, transition.Tick, transition.Input);
        }
        if (tick == 0)
            throw new InvalidDataException("At least one input transition is required.");
        return FinishHash(hash);
    }

    internal static AmigaTrace? ReadOrNull(Stream utf8JsonLines)
    {
        ArgumentNullException.ThrowIfNull(utf8JsonLines);
        if (!utf8JsonLines.CanRead)
            throw new ArgumentException("A readable trace stream is required.", nameof(utf8JsonLines));

        using var reader = new StreamReader(utf8JsonLines, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var lineNumber = 0;
        string? NextLine() => ReadBoundedLine(reader, ++lineNumber);

        try
        {
            var first = NextLine();
            if (first is null)
                return null;

            AmigaTraceHeader header;
            using (var document = Parse(first))
                header = ReadHeader(document.RootElement);

            AmigaRawState initial;
            using (var document = Parse(NextLine() ??
                       throw new InvalidDataException("Missing initial-state record.")))
            {
                var root = document.RootElement;
                Object(root, "initial", "type", "tick", "state");
                Literal(root, "type", "initial", "initial");
                if (Integer(root.GetProperty("tick"), 0, 0, "initial.tick") != 0)
                    throw new InvalidDataException("The initial state must be tick zero.");
                initial = ReadState(root.GetProperty("state"), "initial.state");
            }

            var transitions = new List<AmigaTraceTransition>((int)Math.Min(header.TransitionCount, 4096));
            using var inputHash = CreateInputHash();
            for (long tick = 1; tick <= header.TransitionCount; tick++)
            {
                using var document = Parse(NextLine() ??
                    throw new InvalidDataException($"Premature EOF; missing transition {tick}."));
                var root = document.RootElement;
                Object(root, "tick", "type", "tick", "input", "after");
                Literal(root, "type", "tick", "tick");
                var recordedTick = Integer(root.GetProperty("tick"), 1, MaximumTransitions, "tick.tick");
                if (recordedTick != tick)
                    throw new InvalidDataException(
                        $"Ticks must be unique and contiguous; expected {tick}, found {recordedTick}.");
                var input = ReadInput(root.GetProperty("input"));
                var after = ReadState(root.GetProperty("after"), "after");
                transitions.Add(new(tick, input, after));
                AppendInput(inputHash, tick, input);
            }

            if (NextLine() is not null)
                throw new InvalidDataException("Unexpected trailing record after transitionCount.");
            var computedHash = FinishHash(inputHash);
            if (header.InputSha256 is not null && header.InputSha256 != computedHash)
                throw new InvalidDataException(
                    $"header.inputSha256 does not match the recorded input bytes; computed {computedHash}.");
            return new(header, initial, transitions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Trace line {lineNumber}: invalid JSON.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Trace line {lineNumber}: invalid UTF-8.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"Trace line {lineNumber}: {exception.Message}", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"Trace line {lineNumber}: invalid JSON value.", exception);
        }
    }

    private static JsonDocument Parse(string line) => JsonDocument.Parse(line, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    });

    private static string? ReadBoundedLine(StreamReader reader, int lineNumber)
    {
        var line = new StringBuilder();
        while (true)
        {
            var character = reader.Read();
            if (character < 0)
                return line.Length == 0 ? null : line.ToString();
            if (character == '\n')
                return line.ToString();
            if (line.Length >= MaximumRecordCharacters)
                throw new InvalidDataException($"Record {lineNumber} exceeds the character limit.");
            line.Append((char)character);
        }
    }

    private static AmigaTraceHeader ReadHeader(JsonElement root)
    {
        Object(root, "header", "type", "schemaVersion", "stateSchema", "transitionCount",
            "baseline", "tablesSha256", "inputSha256", "stateMappingSha256", "timing", "provenance");
        Literal(root, "type", "header", "header");
        Integer(root.GetProperty("schemaVersion"), SchemaVersion, SchemaVersion, "header.schemaVersion");
        Literal(root, "stateSchema", StateSchema, "header");
        var count = Integer(root.GetProperty("transitionCount"), 1, MaximumTransitions,
            "header.transitionCount");

        var baseline = root.GetProperty("baseline");
        Object(baseline, "header.baseline", "executableSha256", "version", "region");
        var executable = new AmigaTraceBaseline(
            Hash(baseline, "executableSha256", "header.baseline"),
            Text(baseline.GetProperty("version"), "header.baseline.version", nullable: true),
            Text(baseline.GetProperty("region"), "header.baseline.region", nullable: true));

        var timing = root.GetProperty("timing");
        Object(timing, "header.timing", "status", "numerator", "denominator", "evidenceSha256");
        var timingStatus = Text(timing.GetProperty("status"), "header.timing.status")!;
        if (timingStatus is not ("known" or "unknown"))
            throw new InvalidDataException("header.timing.status must be known or unknown.");
        long? numerator = null;
        long? denominator = null;
        if (timingStatus == "known")
        {
            numerator = Integer(timing.GetProperty("numerator"), 1, long.MaxValue, "header.timing.numerator");
            denominator = Integer(timing.GetProperty("denominator"), 1, long.MaxValue, "header.timing.denominator");
        }
        else if (timing.GetProperty("numerator").ValueKind != JsonValueKind.Null ||
                 timing.GetProperty("denominator").ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException("Unknown timing must have null numerator and denominator.");
        }

        var provenance = root.GetProperty("provenance");
        Object(provenance, "header.provenance", "kind", "producer", "evidenceSha256");
        var kind = Text(provenance.GetProperty("kind"), "header.provenance.kind")!;
        if (kind is not ("independent-executable" or "candidate-implementation" or
            "same-implementation" or "remake" or "synthetic" or "unknown"))
            throw new InvalidDataException("Unsupported header.provenance.kind.");

        return new(SchemaVersion, StateSchema, count, executable,
            Hash(root, "tablesSha256", "header"), Hash(root, "inputSha256", "header"),
            Hash(root, "stateMappingSha256", "header"),
            new(timingStatus, numerator, denominator, Hash(timing, "evidenceSha256", "header.timing")),
            new(kind, Text(provenance.GetProperty("producer"), "header.provenance.producer")!,
                Hash(provenance, "evidenceSha256", "header.provenance")));
    }

    private static AmigaRawInput ReadInput(JsonElement root)
    {
        Object(root, "input", "steer", "throttle", "brake", "boost", "recover");
        return new(Int16(root, "steer", "input"), UInt8(root, "throttle", "input"),
            UInt8(root, "brake", "input"), Boolean(root, "boost", "input"), Boolean(root, "recover", "input"));
    }

    private static AmigaRawState ReadState(JsonElement root, string path)
    {
        Object(root, path, "position", "angles", "linearVelocity", "angularVelocity",
            "contacts", "resources", "road", "runtime");

        var position = root.GetProperty("position");
        Object(position, path + ".position", "x", "y", "z");
        var rawPosition = new AmigaRawPosition(Int32(position, "x", path + ".position"),
            Int32(position, "y", path + ".position"), Int32(position, "z", path + ".position"));

        var angles = root.GetProperty("angles");
        Object(angles, path + ".angles", "yaw", "pitch", "roll");
        var rawAngles = new AmigaRawAngles(UInt16(angles, "yaw", path + ".angles"),
            UInt16(angles, "pitch", path + ".angles"), UInt16(angles, "roll", path + ".angles"));

        var linear = root.GetProperty("linearVelocity");
        Object(linear, path + ".linearVelocity", "x", "y", "z");
        var rawLinear = new AmigaRawLinearVelocity(Int16(linear, "x", path + ".linearVelocity"),
            Int16(linear, "y", path + ".linearVelocity"), Int16(linear, "z", path + ".linearVelocity"));

        var angular = root.GetProperty("angularVelocity");
        Object(angular, path + ".angularVelocity", "yaw", "pitch", "roll");
        var rawAngular = new AmigaRawAngularVelocity(Int16(angular, "yaw", path + ".angularVelocity"),
            Int16(angular, "pitch", path + ".angularVelocity"), Int16(angular, "roll", path + ".angularVelocity"));

        var contacts = root.GetProperty("contacts");
        Object(contacts, path + ".contacts", "frontLeft", "frontRight", "rear");
        var rawContacts = new AmigaRawContacts(
            ReadContact(contacts.GetProperty("frontLeft"), path + ".contacts.frontLeft"),
            ReadContact(contacts.GetProperty("frontRight"), path + ".contacts.frontRight"),
            ReadContact(contacts.GetProperty("rear"), path + ".contacts.rear"));

        var resources = root.GetProperty("resources");
        Object(resources, path + ".resources", "boostReserve", "boostSubunit", "damage", "damageLimit", "engineRevs");
        var rawResources = new AmigaRawResources(UInt8(resources, "boostReserve", path + ".resources"),
            UInt8(resources, "boostSubunit", path + ".resources"), UInt8(resources, "damage", path + ".resources"),
            UInt8(resources, "damageLimit", path + ".resources"), UInt16(resources, "engineRevs", path + ".resources"));

        var road = root.GetProperty("road");
        Object(road, path + ".road", "section", "segment", "lateralPosition", "distanceIntoSection");
        var rawRoad = new AmigaRawRoadState(UInt16(road, "section", path + ".road"),
            UInt16(road, "segment", path + ".road"), Int16(road, "lateralPosition", path + ".road"),
            Int32(road, "distanceIntoSection", path + ".road"));

        var runtime = root.GetProperty("runtime");
        Object(runtime, path + ".runtime", "flags", "offTrackTicks", "recoveryTicks",
            "clockAccumulator", "damageCooldown");
        var rawRuntime = new AmigaRawRuntimeState(
            (uint)Integer(runtime.GetProperty("flags"), 0, uint.MaxValue, path + ".runtime.flags"),
            UInt16(runtime, "offTrackTicks", path + ".runtime"),
            UInt16(runtime, "recoveryTicks", path + ".runtime"),
            UInt8(runtime, "clockAccumulator", path + ".runtime"),
            UInt8(runtime, "damageCooldown", path + ".runtime"));

        return new(rawPosition, rawAngles, rawLinear, rawAngular, rawContacts,
            rawResources, rawRoad, rawRuntime);
    }

    private static AmigaRawContact ReadContact(JsonElement root, string path)
    {
        Object(root, path, "roadHeight", "actualHeight", "compression", "previousCompression", "damage", "flags");
        return new(Int32(root, "roadHeight", path), Int32(root, "actualHeight", path),
            Int16(root, "compression", path), Int16(root, "previousCompression", path),
            UInt8(root, "damage", path), UInt8(root, "flags", path));
    }

    private static void Object(JsonElement root, string path, params string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"{path}.{property.Name} is duplicated.");
            if (Array.IndexOf(fields, property.Name) < 0)
                throw new InvalidDataException($"{path}.{property.Name} is unknown.");
        }
        foreach (var field in fields)
        {
            if (!seen.Contains(field))
                throw new InvalidDataException($"{path}.{field} is required.");
        }
    }

    private static void Literal(JsonElement root, string name, string expected, string path)
    {
        if (Text(root.GetProperty(name), path + "." + name) != expected)
            throw new InvalidDataException($"{path}.{name} must be {expected}.");
    }

    private static string? Text(JsonElement value, string path, bool nullable = false)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path} must be a nonempty string{(nullable ? " or null" : "")}.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
            throw new InvalidDataException($"{path} must be a nonempty string of at most 4096 characters.");
        return text;
    }

    private static string? Hash(JsonElement root, string name, string path)
    {
        var text = Text(root.GetProperty(name), path + "." + name, nullable: true);
        if (text is null)
            return null;
        if (text.Length != 64 || text.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"{path}.{name} must be 64 lowercase hexadecimal digits or null.");
        return text;
    }

    private static long Integer(JsonElement value, long minimum, long maximum, string path)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0 ||
            !value.TryGetInt64(out var number) || number < minimum || number > maximum)
            throw new InvalidDataException($"{path} must be an integer in [{minimum}, {maximum}].");
        return number;
    }

    private static short Int16(JsonElement root, string name, string path) =>
        (short)Integer(root.GetProperty(name), short.MinValue, short.MaxValue, path + "." + name);

    private static ushort UInt16(JsonElement root, string name, string path) =>
        (ushort)Integer(root.GetProperty(name), 0, ushort.MaxValue, path + "." + name);

    private static int Int32(JsonElement root, string name, string path) =>
        (int)Integer(root.GetProperty(name), int.MinValue, int.MaxValue, path + "." + name);

    private static byte UInt8(JsonElement root, string name, string path) =>
        (byte)Integer(root.GetProperty(name), 0, byte.MaxValue, path + "." + name);

    private static bool Boolean(JsonElement root, string name, string path)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{path}.{name} must be a JSON boolean.");
        return value.GetBoolean();
    }

    private static IncrementalHash CreateInputHash()
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("amiga-physics-input-v1\n"u8);
        return hash;
    }

    private static void AppendInput(IncrementalHash hash, long tick, AmigaRawInput input)
    {
        Span<byte> bytes = stackalloc byte[14];
        BinaryPrimitives.WriteInt64BigEndian(bytes, tick);
        BinaryPrimitives.WriteInt16BigEndian(bytes[8..], input.Steer);
        bytes[10] = input.Throttle;
        bytes[11] = input.Brake;
        bytes[12] = input.Boost ? (byte)1 : (byte)0;
        bytes[13] = input.Recover ? (byte)1 : (byte)0;
        hash.AppendData(bytes);
    }

    private static string FinishHash(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}
