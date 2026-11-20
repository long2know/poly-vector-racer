using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StuntRacer.Core.Compatibility;
using StuntRacer.Core.SourceRuntime;

namespace AmigaReferenceExtract;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static int Main(string[] args)
    {
        try
        {
            var options = Options(args);
            var sourceId = Required(options, "--source-id");
            var revision = Required(options, "--revision");
            var engineId = Required(options, "--engine-source-id");
            var engineRevision = Required(options, "--engine-revision");
            var layoutId = Required(options, "--layout-source-id");
            var layoutRevision = Required(options, "--layout-revision");
            var inputs = new List<CompatibilityAssetProvenance>();
            byte[] Load(string flag, string role, string id, string pin, int limit = 4 * 1024 * 1024)
            {
                using var input = File.OpenRead(Required(options, flag));
                using var buffer = new MemoryStream();
                var block = new byte[8192];
                int count;
                while ((count = input.Read(block, 0, block.Length)) != 0)
                {
                    if (buffer.Length + count > limit)
                        throw new InvalidDataException($"{role} exceeds its safety byte limit.");
                    buffer.Write(block, 0, count);
                }
                var bytes = buffer.ToArray();
                inputs.Add(new CompatibilityAssetProvenance(role, bytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), id, pin));
                return bytes;
            }
            var textEncoding = new UTF8Encoding(false, true);
            var track = new NumericInitializers(textEncoding.GetString(Load("--track-source", "trackSource", sourceId, revision)));
            var trackHeader = new NumericInitializers(textEncoding.GetString(Load("--track-header", "trackHeader", sourceId, revision)));
            var behavior = new NumericInitializers(textEncoding.GetString(Load("--behavior-source", "behaviorSource", sourceId, revision)));
            var engineHeader = new NumericInitializers(textEncoding.GetString(Load("--engine-header", "engineHeader", engineId, engineRevision)));
            _ = Load("--engine-source", "engineSource", engineId, engineRevision);
            var layout = Load("--layout", "layout", layoutId, layoutRevision, 805);
            if (layout.Length != 804)
                throw new InvalidDataException("A reference layout requires exactly 804 bytes.");
            var specBytes = Load("--behavior-spec", "behaviorSpec", "independent-functional-facts", revision);
            using var spec = JsonDocument.Parse(specBytes);
            RejectDuplicateProperties(spec.RootElement);
            var constants = ReadConstants(spec.RootElement.GetProperty("runtimeConstants"));
            foreach (var name in new[] { "GRAVITY_ACCELERATION", "ROAD_WIDTH", "SURFACE_SIZE",
                         "LOG_SURFACE_SIZE", "OFF_ROAD_HEIGHT", "OFF_TRACK_LIMIT", "LOCAL_Y_FACTOR" })
            {
                var number = behavior.Define(name);
                if (constants.TryGetValue(name, out var existing) && existing != number)
                    throw new InvalidDataException($"Behavior facts and source disagree on '{name}'.");
                constants[name] = number;
            }
            var factor = checked((int)trackHeader.Define("PC_FACTOR"));
            var precision = checked((int)engineHeader.Define("PRECISION"));
            var maxAngle = checked((int)engineHeader.Define("MAX_ANGLE"));
            var cube = trackHeader.Define("CUBE_SIZE");
            var tile = cube / precision / factor;
            if (factor != 2 || precision != 16384 || maxAngle != 65536 || tile != 2048)
                throw new InvalidDataException("Unsupported reference coordinate/angle profile.");
            var scaleText = Required(options, "--display-metres-per-pc-unit");
            if (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
                !double.IsFinite(scale) || scale <= 0)
                throw new InvalidDataException("Display metres per PC unit must be positive and finite.");

            var flags = track.Bytes("sections_car_can_be_put_on");
            if (flags.Length != 16)
                throw new InvalidDataException("Restart eligibility requires exactly sixteen source flags.");
            var templateRows = track.Array("Piece_Templates");
            if (templateRows.Count != 11)
                throw new InvalidDataException("The selected reference format requires eleven template descriptors.");
            var templates = new List<object>();
            var metadata = new List<SourceTemplateMetadata>();
            for (var id = 0; id < templateRows.Count; id++)
            {
                var row = templateRows[id].Row(6);
                var segments = checked((int)row[0].Integer());
                var curve = row[1].Integer();
                var type = row[2].Integer();
                var reduction = row[3].Integer();
                var steering = row[4].Integer();
                if (curve is not (0 or 1) || type is < 0 or > 255 ||
                    reduction is < 0 or > 255 || steering is < 0 or > 255)
                    throw new InvalidDataException($"Template {id} has invalid boolean/byte metadata.");
                if (segments == 0)
                {
                    if (row[5].Symbol != "NULL" || curve != 0 || type != 0 || reduction != 0 || steering != 0)
                        throw new InvalidDataException("An unused template requires a NULL pointer and zero metadata.");
                    continue;
                }
                if (segments is < 1 or > 13 || row[5].Symbol != $"Piece{id}_XZ")
                    throw new InvalidDataException($"Template {id} has unsupported segment/pointer metadata.");
                var encoded = track.Bytes($"Amiga_Piece{id}_XZ");
                if (encoded.Length != (segments + 1) * 8)
                    throw new InvalidDataException($"Template {id} has an inconsistent encoded byte count.");
                var crossSections = new List<object>();
                for (var i = 0; i < encoded.Length; i += 8)
                    crossSections.Add(new
                    {
                        leftX = BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(i, 2)),
                        leftZ = BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(i + 2, 2)),
                        rightX = BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(i + 4, 2)),
                        rightZ = BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(i + 6, 2))
                    });
                templates.Add(new { id, restartEligible = (flags[id] & 128) == 0, crossSections });
                metadata.Add(new SourceTemplateMetadata(id, segments, curve == 1,
                    (int)type, (int)reduction, (int)steering, flags[id]));
            }
            var dimensions = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["NUM_AMIGA_PIECE_Y"] = track.Define("NUM_AMIGA_PIECE_Y")
            };
            var profileRows = track.Array("Amiga_Piece_Y", dimensions);
            if (profileRows.Count != 128)
                throw new InvalidDataException("The selected reference format requires 128 profile descriptors.");
            var profiles = new List<object>();
            for (var id = 0; id < profileRows.Count; id++)
            {
                var row = profileRows[id].Row(3);
                if (row[0].Symbol == "NULL")
                {
                    if (row[1].Integer() != 0 || row[2].Integer() != 0)
                        throw new InvalidDataException("A NULL height descriptor must have zero size and FALSE storage.");
                    continue;
                }
                var symbol = row[0].Symbol ?? throw new InvalidDataException("Height descriptor must name a numeric byte array.");
                if (row[2].SizeOf != symbol)
                    throw new InvalidDataException("Height descriptor size must be sizeof(its-own-byte-array).");
                var encoded = track.Bytes(symbol);
                var wordFlag = row[1].Integer();
                if (wordFlag is not (0 or 1) || encoded.Length == 0 || (wordFlag == 1 && encoded.Length % 2 != 0))
                    throw new InvalidDataException("Invalid height profile storage/byte count.");
                var words = wordFlag == 1;
                if (encoded.Length / (words ? 2 : 1) > 14)
                    throw new InvalidDataException("A reference height profile exceeds its fourteen-sample destination.");
                var samples = new List<int>();
                for (var i = 0; i < encoded.Length; i += words ? 2 : 1)
                    samples.Add(words
                        ? BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(i, 2)) % 32768
                        : (encoded[i] % 16) * 256 + ((encoded[i] / 16) % 8) * 32);
                profiles.Add(new
                {
                    id, sampleCount = samples.Count,
                    sourceStorage = new { kind = words ? "decoded-big-endian-word" : "decoded-packed-byte",
                        leftHighBit = words, evidenceId = "reference:Track.cpp/ConvertAmigaPieceY" },
                    normalization = new { sampleMultiplier = 1, sampleDivisor = 1,
                        shiftMultiplier = 1, shiftDivisor = 1, evidenceId = "reference:Track.cpp/profile-plus-shift" },
                    samples
                });
            }
            var cosine = behavior.Array("Cosine_Conversion_Table").Select(value => checked((int)value.Integer())).ToArray();
            if (cosine.Length != 128)
                throw new InvalidDataException("Cosine conversion lookup requires 128 external values.");
            var pi = engineHeader.FloatingDefine("PI");
            if (pi is < 3.14 or > 3.15)
                throw new InvalidDataException("Reference PI declaration is outside supported bounds.");
            var sine = new int[maxAngle + maxAngle / 4];
            var phase = 0.0;
            var phaseIncrement = 2 * pi / maxAngle;
            for (var i = 0; i < sine.Length; i++, phase += phaseIncrement)
                sine[i] = checked((short)Math.Truncate(Math.Sin(phase) * precision));
            var clockFacts = spec.RootElement.GetProperty("clock");
            var callbackHertz = clockFacts.GetProperty("configuredCallbackHertz").GetInt32();
            var callbacksPerStep = clockFacts.GetProperty("callbacksPerMovementStep").GetInt32();
            var simulationStepSeconds = clockFacts.GetProperty("simulationStepSeconds").GetDouble();
            var common = new
            {
                schemaVersion = 1, sourceId, sourceRevision = revision, circuitMode = "static",
                encodingEvidenceId = "reference:Track.cpp/numeric-declarations-and-decoders",
                layoutGeometry = new { tileSizeX = tile, tileSizeZ = tile,
                    rotationPivotX = tile / 2, rotationPivotZ = tile / 2,
                    positiveQuarterTurn = "x-to-negative-z", reversalHeightBinding = "travel-ordered",
                    seamPolicy = "next-section-start", evidenceId = "reference:Track.cpp/rotation-traversal-seams" },
                coordinateTransform = new { xAxis = "+x", yAxis = "+y", zAxis = "+z",
                    xDivisor = 1, yDivisor = 4, zDivisor = 1,
                    evidenceId = "reference:Track.cpp/GetPieceVertex-Y-quarter" },
                templates, heightProfiles = profiles
            };
            var package = new
            {
                schemaVersion = 1, kind = "stunt-amiga-source-runtime",
                profileId = "amiga-derived-source-compatible", originalStockParity = false,
                name = Required(options, "--name"), layoutBase64 = Convert.ToBase64String(layout), commonData = common,
                units = new { pcFactor = factor, positionPrecision = precision,
                    localYFactor = constants["LOCAL_Y_FACTOR"], roadHeightDivisor = 4,
                    displayMetresPerPcUnit = scale, evidenceId = "functional-facts:coordinates/v2-native-height" },
                clock = new { callbackHertz, callbacksPerStep, initialCallbackRunsImmediately = true,
                    simulationStepSeconds, evidenceId = "functional-facts:clock/native-linux-target" },
                lookups = new { sinQ14 = sine, cosineConversion = cosine, numericTables = new Dictionary<string, long[]>() },
                templateMetadata = metadata, constants,
                provenance = inputs.OrderBy(input => input.Role, StringComparer.Ordinal).ToArray()
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            using var validation = new MemoryStream(json, writable: false);
            var parsed = SourceAmigaPackageReader.Read(validation);
            var relativePath = WriteNew(Required(options, "--out"), json);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "extracted", output = relativePath, byteLength = json.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(json)),
                sections = parsed.Layout.SectionCount, segments = parsed.RawRoad.Count,
                edges = parsed.ImportedTrack.Edges.Count, templates = metadata.Count, profiles = profiles.Count,
                sineValues = sine.Length, cosineValues = cosine.Length,
                parsed.ProfileId, parsed.OriginalStockParity, parsed.Start,
                inputHashes = inputs
            }, JsonOptions));
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or JsonException or
                                           InvalidOperationException or KeyNotFoundException or OverflowException or
                                           UnauthorizedAccessException or TimeoutException)
        {
            Console.Error.WriteLine($"invalid reference input: {exception.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        if (args.Length == 0 || args[0] != "extract")
            throw new ArgumentException("Use extract with explicit source/header/layout/spec paths, provenance IDs, display scale, name and private --out.");
        var known = new HashSet<string>(new[] { "--source-id", "--revision", "--engine-source-id", "--engine-revision",
            "--layout-source-id", "--layout-revision", "--track-source", "--track-header", "--behavior-source",
            "--engine-source", "--engine-header", "--layout", "--behavior-spec", "--display-metres-per-pc-unit", "--name", "--out" },
            StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !known.Contains(args[i]) ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !result.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown/duplicate option or missing argument value.");
        return result;
    }

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");

    private static Dictionary<string, long> ReadConstants(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Functional facts require a runtimeConstants object.");
        var constants = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!property.Value.TryGetInt64(out var number) || !constants.TryAdd(property.Name, number))
                throw new InvalidDataException("Runtime constants require unique integer fields.");
        return constants;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate functional-facts property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                RejectDuplicateProperties(child);
    }

    private static string WriteNew(string requested, byte[] bytes)
    {
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        var prefix = Path.Combine(root, "artifacts", "amiga-source", "runtime") + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(requested, root);
        if (!File.Exists(Path.Combine(root, "tools", "AmigaReferenceExtract", "AmigaReferenceExtract.csproj")) ||
            !output.StartsWith(prefix, StringComparison.Ordinal) || !output.EndsWith(".source-package.json", StringComparison.Ordinal))
            throw new InvalidDataException("Output must be a new artifacts/amiga-source/runtime/*.source-package.json from repository-root cwd.");
        void CheckLinks()
        {
            var current = Path.GetPathRoot(output)!;
            foreach (var part in output[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("Private output and every ancestor must not be symlinks/reparse points.");
            }
        }
        CheckLinks();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        CheckLinks();
        var created = false;
        try
        {
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            CheckLinks();
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
                File.Delete(output);
            throw;
        }
        return Path.GetRelativePath(root, output).Replace(Path.DirectorySeparatorChar, '/');
    }
}
