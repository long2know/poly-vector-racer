using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using StuntRacer.Core.Compatibility;
using StuntRacer.Core.Compatibility.Physics;
using StuntRacer.Core.Compatibility.Tracks;

namespace AmigaTrackImport;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "help")
            {
                Console.WriteLine("AmigaTrackImport inspect --layout FILE\n" +
                    "AmigaTrackImport convert --layout FILE --common FILE --provenance FILE " +
                    "--display-metres-per-source-unit POSITIVE --name LABEL --out artifacts/amiga-compat/NAME.track.json " +
                    "[--asset ROLE=FILE ...]\n" +
                    "AmigaTrackImport verify-trace --expected FILE --actual FILE\n" +
                    "Private decoded interchange only; experimental, unqualified, rights unresolved.");
                return 0;
            }
            if (args.Length == 0)
                throw new ArgumentException("Expected inspect, convert or verify-trace; use --help for exact options.");
            var options = ParseOptions(args);
            return args[0] switch
            {
                "inspect" => Inspect(options),
                "convert" => ConvertTrack(options),
                "verify-trace" => VerifyTrace(options),
                _ => throw new ArgumentException($"Unsupported command '{args[0]}'.")
            };
        }
        catch (AmigaTrackBlockedException exception)
        {
            return Error(4, "blocked", exception.Message);
        }
        catch (FileNotFoundException)
        {
            return Error(4, "blocked", "A supplied local input file is missing; provide every required file.");
        }
        catch (DirectoryNotFoundException)
        {
            return Error(4, "blocked", "An input directory is missing; run from the repository root and check input paths.");
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or
                                           IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Error(2, "invalid", exception.Message);
        }
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var allowed = args[0] switch
        {
            "inspect" => new[] { "--layout" },
            "convert" => new[] { "--layout", "--common", "--provenance", "--display-metres-per-source-unit", "--name", "--out", "--asset" },
            "verify-trace" => new[] { "--expected", "--actual" },
            _ => throw new ArgumentException($"Unsupported command '{args[0]}'.")
        };
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            var key = args[i];
            if (!allowed.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown option '{key}' for {args[0]}.");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException($"Option '{key}' requires a value.");
            if (!result.TryGetValue(key, out var values))
                result.Add(key, values = []);
            else if (key != "--asset")
                throw new ArgumentException($"Duplicate option '{key}'.");
            values.Add(args[i + 1]);
        }
        return result;
    }

    private static int Inspect(Dictionary<string, List<string>> options)
    {
        var input = HashedInput.Read(Required(options, "--layout", input: true),
            AmigaTrackLayoutReader.ByteLength + 1, "layout");
        var layout = AmigaTrackLayoutReader.Read(input.Bytes);
        Print(new
        {
            schemaVersion = 1,
            status = "inspected",
            byteLength = input.ByteLength,
            sha256 = input.Sha256,
            layout.SectionCount,
            layout.StartSectionIndex,
            layout.StandardBoostReserve,
            layout.SuperBoostReserve,
            sections = layout.Sections,
            compatibility = Qualification()
        });
        return 0;
    }

    private static int ConvertTrack(Dictionary<string, List<string>> options)
    {
        var layoutPath = Required(options, "--layout", input: true);
        var commonPath = Required(options, "--common", input: true);
        var provenancePath = Required(options, "--provenance", input: true);
        var scaleText = Required(options, "--display-metres-per-source-unit");
        var name = Required(options, "--name");
        var output = Required(options, "--out");
        if (scaleText != scaleText.Trim() ||
            !double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("--display-metres-per-source-unit requires an explicit positive finite invariant number.");
        var inputs = new Dictionary<string, HashedInput>(StringComparer.Ordinal)
        {
            ["layout"] = HashedInput.Read(layoutPath, AmigaTrackLayoutReader.ByteLength + 1, "layout"),
            ["commonData"] = HashedInput.Read(commonPath, AmigaCommonDataReader.MaximumJsonBytes, "commonData")
        };
        var manifestInput = HashedInput.Read(provenancePath, 1024 * 1024, "provenance manifest");
        if (options.TryGetValue("--asset", out var supplied))
            foreach (var argument in supplied)
            {
                var separator = argument.IndexOf('=');
                if (separator <= 0 || separator + 1 == argument.Length)
                    throw new ArgumentException("--asset requires ROLE=FILE.");
                var role = argument[..separator];
                InputManifest.ValidateRole(role);
                if (role is "layout" or "commonData" || inputs.ContainsKey(role))
                    throw new ArgumentException($"Duplicate supplied asset role '{role}'.");
                inputs.Add(role, HashedInput.HashOnly(argument[(separator + 1)..], role));
            }
        var layout = AmigaTrackLayoutReader.Read(inputs["layout"].Bytes);
        using var commonStream = inputs["commonData"].Open();
        var common = AmigaCommonDataReader.Read(commonStream);
        var provenance = InputManifest.Validate(manifestInput, inputs, common);
        var track = AmigaTrackConverter.Convert(layout, common, scale, name);
        var compatibility = new
        {
            profileId = CompatibilityProfile.AmigaExperimentalId,
            qualification = "unqualified-candidate",
            dynamics = "not-implemented",
            simulationStepSeconds = (double?)null,
            displayMetresPerSourceUnit = scale,
            coordinateTransform = common.CoordinateTransform,
            encodingEvidenceId = common.EncodingEvidenceId,
            layoutGeometry = common.LayoutGeometry,
            rights = new { status = "unresolved", evidenceSha256 = provenance.RightsEvidenceSha256 },
            blockers = Blockers()
        };
        var document = new
        {
            schemaVersion = 1,
            track = new { track.Name, track.Length, track.Width, track.Points },
            geometry = new
            {
                kind = "exact-source-edge-strip",
                roadLookup = "geometric-triangles-not-original-integer-sampler",
                track.StartSegmentIndex, track.StartDistance, track.RestartEligibleSections,
                track.Edges, track.UnstitchedEdges, track.Segments, track.Seams, track.Triangles
            },
            source = new
            {
                layout = new
                {
                    layout.SectionCount, layout.StartSectionIndex,
                    layout.StandardBoostReserve, layout.SuperBoostReserve,
                    sections = layout.Sections
                },
                commonData = common
            },
            provenance = new
            {
                schemaVersion = 1,
                assets = provenance.Assets,
                manifest = new { manifestInput.ByteLength, manifestInput.Sha256 },
                rights = new { status = "unresolved", evidenceSha256 = provenance.RightsEvidenceSha256 }
            },
            compatibility
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var relativeOutput = PrivateOutput.WriteNew(output, bytes);
        Print(new
        {
            status = "converted",
            output = relativeOutput,
            track.Name,
            track.Length,
            sectionCount = layout.SectionCount,
            segmentCount = track.Segments.Count,
            edgeCount = track.Edges.Count,
            sha256 = System.Convert.ToHexStringLower(SHA256.HashData(bytes)),
            compatibility
        });
        return 0;
    }

    private static int VerifyTrace(Dictionary<string, List<string>> options)
    {
        var expected = HashedInput.Read(Required(options, "--expected", input: true), 64 * 1024 * 1024, "expected trace");
        var actual = HashedInput.Read(Required(options, "--actual", input: true), 64 * 1024 * 1024, "actual trace");
        using var expectedStream = expected.Open();
        using var actualStream = actual.Open();
        var verification = AmigaTraceVerifier.Verify(expectedStream, actualStream);
        Print(new
        {
            verification.Status, verification.ComparedTicks, verification.FirstMismatchTick,
            verification.Reason, verification.Qualification, verification.OracleBasis,
            verification.FirstMismatchField, verification.ExpectedValue, verification.ActualValue,
            inputs = new
            {
                expected = new { expected.ByteLength, expected.Sha256 },
                actual = new { actual.ByteLength, actual.Sha256 }
            }
        });
        return verification.Status switch
        {
            "match" => 0,
            "mismatch" => 3,
            "blocked" => 4,
            _ => throw new InvalidDataException("Physics verifier returned an unsupported status.")
        };
    }

    private static object Qualification() => new
    {
        profileId = CompatibilityProfile.AmigaExperimentalId,
        qualification = "unqualified-candidate",
        dynamics = "not-implemented",
        simulationStepSeconds = (double?)null,
        rights = new { status = "unresolved", evidenceSha256 = (string?)null },
        blockers = Blockers()
    };

    private static string[] Blockers() =>
    [
        "Code/data rights remain unresolved; import grants no reuse or distribution permission.",
        "Decoded storage, normalization, tile/pivot/reversal/seam and axis evidence are supplied declarations, not original verification.",
        "Selected original executable/version/region, native table identity and packed-height decoding are not verified.",
        "Original integer road-height sampling and source vehicle dynamics are not implemented; triangle lookup is geometric only.",
        "Independent original tick/state traces and measured timing/overrun/race-timer semantics remain required."
    ];

    private static string Required(Dictionary<string, List<string>> options, string key, bool input = false)
    {
        if (options.TryGetValue(key, out var values))
            return values[0];
        if (input)
            throw new AmigaTrackBlockedException($"Missing required private input {key}; supply its local file.");
        throw new ArgumentException($"Missing required option {key}.");
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static int Error(int code, string status, string message)
    {
        Console.Error.WriteLine($"{status}: {message}");
        return code;
    }
}
