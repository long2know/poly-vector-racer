using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StuntRacer.CompatibilityChecks;

internal static class CliChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("CLI / inspect reports actual bytes, flags and unresolved qualification", Inspect);
        yield return new("CLI / convert emits deterministic exact geometry and full-byte provenance", ConvertAndMutateArtifact);
        yield return new("CLI / invalid provenance and missing data leave no partial output", ProvenanceFailures);
        yield return new("CLI / optional consumed evidence must be uniquely declared and hashed", OptionalEvidence);
        yield return new("CLI / overwrite, escaping paths and symlinks are refused", PrivatePaths);
        yield return new("CLI / malformed arguments and layout lengths fail without output", MalformedArguments);
        yield return new("CLI / real trace results include first-field diagnostics and input hashes", VerifyTrace);
    }

    private static void Inspect()
    {
        using var fixture = new PrivateFixture();
        var bytes = SyntheticLayout.Create(
            new(0xd2, 0x10, 0x80, 0x01, short.MinValue, short.MaxValue),
            new(0x3f, 0xc1, 0x02, 0x83, 258, -257));
        bytes[1] = 1;
        var path = fixture.Write("inspect-layout.bin", bytes);
        var result = fixture.Invoke("inspect", "--layout", path);
        Check.Equal(0, result.ExitCode, $"inspect stderr: {result.StandardError}");
        using var document = result.Json("inspect");
        var root = document.RootElement;
        Check.Equal("inspected", root.GetProperty("status").GetString(), "inspect status");
        Check.Equal(804L, root.GetProperty("byteLength").GetInt64(), "inspect complete byte count");
        Check.Equal(Hash(bytes), root.GetProperty("sha256").GetString(), "inspect complete raw-byte digest");
        Check.Equal(2, root.GetProperty("sectionCount").GetInt32(), "inspect active count");
        Check.Equal(1, root.GetProperty("startSectionIndex").GetInt32(), "inspect start");
        var sections = root.GetProperty("sections");
        Check.Equal(2, sections.GetArrayLength(), "inspect must not invent inactive sections");
        Check.Equal(-32768, sections[0].GetProperty("leftHeightShift").GetInt32(), "inspect signed left shift");
        Check.Equal(32767, sections[0].GetProperty("rightHeightShift").GetInt32(), "inspect signed right shift");
        Check.Equal(128, sections[0].GetProperty("rawLeftProfile").GetInt32(), "inspect raw storage flag");
        Check.Equal(1, sections[0].GetProperty("rawRightProfile").GetInt32(), "inspect raw right not swapped");
        Check.Equal(15, sections[1].GetProperty("gridX").GetInt32(), "inspect low nibble");
        Check.Equal(3, sections[1].GetProperty("gridZ").GetInt32(), "inspect high nibble");
        Qualification(root.GetProperty("compatibility"));
        Check.Require(!root.TryGetProperty("track", out _), "inspection fabricated a runnable road");
    }

    private static void ConvertAndMutateArtifact()
    {
        using var fixture = new PrivateFixture();
        var inputs = new Inputs(fixture);
        var firstPath = fixture.Output("first.track.json");
        var secondPath = fixture.Output("second.track.json");
        var first = fixture.Invoke(inputs.Arguments(firstPath));
        var second = fixture.Invoke(inputs.Arguments(secondPath));
        Check.Equal(0, first.ExitCode, $"convert stderr: {first.StandardError}");
        Check.Equal(0, second.ExitCode, $"repeat convert stderr: {second.StandardError}");
        var firstBytes = File.ReadAllBytes(firstPath);
        Check.Require(firstBytes.SequenceEqual(File.ReadAllBytes(secondPath)), "repeat conversion changed actual output bytes");
        using (var document = JsonDocument.Parse(firstBytes))
            AssertConverted(document.RootElement, inputs);
        using (var summary = first.Json("convert summary"))
        {
            Check.Equal("converted", summary.RootElement.GetProperty("status").GetString(), "conversion summary");
            Check.Equal(Hash(firstBytes), summary.RootElement.GetProperty("sha256").GetString(), "summary hashes actual output bytes");
            Check.Equal(12, summary.RootElement.GetProperty("segmentCount").GetInt32(), "summary source segments");
            Check.Equal(16, summary.RootElement.GetProperty("edgeCount").GetInt32(), "summary exact edges");
        }
        Check.Require(!Encoding.UTF8.GetString(firstBytes).Contains(fixture.RepositoryRoot, StringComparison.Ordinal),
            "deterministic output leaked an absolute local path");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(firstPath);
            if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                Console.WriteLine($"  filesystem reports output mode {mode}; filesystem privacy is not qualified.");
        }

        var mutated = JsonNode.Parse(firstBytes)!.AsObject();
        mutated["geometry"]!["edges"]![0]!["left"]!["x"] = -2;
        var mutantPath = fixture.Write("mutated-emitted-edge.track.json", SyntheticCommon.Bytes(mutated));
        using var mutant = JsonDocument.Parse(File.ReadAllBytes(mutantPath));
        Check.Throws<InvalidOperationException>(() => AssertConverted(mutant.RootElement, inputs),
            "real emitted geometry mutation must turn the exact-coordinate assertion red");
    }

    private static void ProvenanceFailures()
    {
        using var fixture = new PrivateFixture();
        var inputs = new Inputs(fixture);
        (string Name, Action<JsonObject> Mutate, int Exit)[] cases =
        [
            ("wrong-layout-hash", json => json["assets"]![0]!["sha256"] = new string('a', 64), 2),
            ("wrong-layout-count", json => json["assets"]![0]!["byteLength"] = 803, 2),
            ("wrong-common-count", json => json["assets"]![1]!["byteLength"] = inputs.CommonBytes.Length + 1, 2),
            ("negative-count", json => json["assets"]![1]!["byteLength"] = -1, 2),
            ("duplicate-role", json => json["assets"]!.AsArray().Add(json["assets"]![0]!.DeepClone()), 2),
            ("missing-common-role", json => json["assets"]![1]!["role"] = "expectedTrace", 4),
            ("unknown-role", json => json["assets"]![1]!["role"] = "invented", 2),
            ("wrong-source-id", json => json["assets"]![1]!["sourceId"] = "different-synthetic-source", 2),
            ("wrong-revision", json => json["assets"]![1]!["sourceRevision"] = null, 2),
            ("caller-rights-upgrade", json => json["rights"]!["status"] = "cleared", 2),
            ("unknown-attestation", json => json["isFaithful"] = true, 2),
            ("uppercase-hash", json => json["assets"]![0]!["sha256"] = new string('A', 64), 2)
        ];
        foreach (var (name, mutate, exit) in cases)
        {
            var manifest = inputs.Manifest();
            mutate(manifest);
            var path = fixture.Write(name + ".json", manifest);
            var output = fixture.Output(name + ".track.json");
            Failure(fixture.Invoke(inputs.Arguments(output, manifest: path)), exit, output, name);
        }

        var changedCommon = (byte[])inputs.CommonBytes.Clone();
        changedCommon[^1] = (byte)' ';
        var changedPath = fixture.Write("common-same-json-different-raw-bytes.json", changedCommon);
        var hashOutput = fixture.Output("raw-byte-hash-mismatch.track.json");
        var mismatch = fixture.Invoke(inputs.Arguments(hashOutput, common: changedPath));
        Failure(mismatch, 2, hashOutput, "complete raw input bytes, not reformatted JSON");
        Check.Require(mismatch.StandardError.Contains("SHA-256", StringComparison.Ordinal), "raw-byte mismatch lacks digest diagnostic");
        var missingOutput = fixture.Output("missing-common.track.json");
        Failure(fixture.Invoke(inputs.Arguments(missingOutput, common: Path.Combine(fixture.DirectoryPath, "absent-common.json"))),
            4, missingOutput, "missing common file");
        var noCommon = inputs.Arguments(fixture.Output("omitted-common.track.json")).ToList();
        var commonIndex = noCommon.IndexOf("--common");
        noCommon.RemoveRange(commonIndex, 2);
        Failure(fixture.Invoke(noCommon.ToArray()), 4, noCommon[^1], "omitted common input");
    }

    private static void OptionalEvidence()
    {
        using var fixture = new PrivateFixture();
        var inputs = new Inputs(fixture);
        var evidence = SyntheticTrace.Bytes(SyntheticTrace.Create());
        var evidencePath = fixture.Write("explicit-synthetic-evidence.jsonl", evidence);
        var manifest = inputs.Manifest();
        manifest["assets"]!.AsArray().Add(Asset("expectedTrace", evidence, "synthetic-fixture-only", null));
        var manifestPath = fixture.Write("with-evidence.json", manifest);
        var output = fixture.Output("with-evidence.track.json");
        var arguments = inputs.Arguments(output, manifestPath).Concat(new[] { "--asset", "expectedTrace=" + evidencePath }).ToArray();
        var result = fixture.Invoke(arguments);
        Check.Equal(0, result.ExitCode, $"hashed optional evidence: {result.StandardError}");
        using var document = JsonDocument.Parse(File.ReadAllBytes(output));
        var asset = document.RootElement.GetProperty("provenance").GetProperty("assets").EnumerateArray()
            .Single(item => item.GetProperty("role").GetString() == "expectedTrace");
        Check.Equal(Hash(evidence), asset.GetProperty("sha256").GetString(), "consumed evidence whole-byte digest");
        Check.Equal((long)evidence.Length, asset.GetProperty("byteLength").GetInt64(), "consumed evidence exact byte count");
        Qualification(document.RootElement.GetProperty("compatibility"));

        var unsupplied = fixture.Output("declared-not-supplied.track.json");
        Failure(fixture.Invoke(inputs.Arguments(unsupplied, manifestPath)), 4, unsupplied, "declared evidence not supplied");
        var undeclared = fixture.Output("supplied-not-declared.track.json");
        Failure(fixture.Invoke(inputs.Arguments(undeclared).Concat(new[] { "--asset", "expectedTrace=" + evidencePath }).ToArray()),
            2, undeclared, "consumed evidence absent from provenance");
        var duplicate = fixture.Output("duplicate-supplied-role.track.json");
        Failure(fixture.Invoke(inputs.Arguments(duplicate, manifestPath).Concat(
            new[] { "--asset", "expectedTrace=" + evidencePath, "--asset", "expectedTrace=" + evidencePath }).ToArray()),
            2, duplicate, "duplicate supplied evidence role");
    }

    private static void PrivatePaths()
    {
        using var fixture = new PrivateFixture();
        var inputs = new Inputs(fixture);
        var existing = fixture.Output("existing.track.json");
        var success = fixture.Invoke(inputs.Arguments(existing));
        Check.Equal(0, success.ExitCode, $"initial private conversion: {success.StandardError}");
        var original = File.ReadAllBytes(existing);
        var overwrite = fixture.Invoke(inputs.Arguments(existing));
        Check.Equal(2, overwrite.ExitCode, "refused overwrite");
        Check.Require(original.SequenceEqual(File.ReadAllBytes(existing)), "refused overwrite changed existing bytes");
        var escape = fixture.EscapingOutput();
        Failure(fixture.Invoke(inputs.Arguments(escape)), 2, escape, "normalized escape from private output root");
        var wrongExtension = fixture.Output("not-a-track.json");
        Failure(fixture.Invoke(inputs.Arguments(wrongExtension)), 2, wrongExtension, "wrong private output suffix");

        var sentinel = "synthetic private sentinel"u8.ToArray();
        var target = fixture.Write("symlink-target.track.json", sentinel);
        var link = fixture.LinkFile("symlink.track.json", target);
        var linked = fixture.Invoke(inputs.Arguments(link));
        Check.Equal(2, linked.ExitCode, "output symlink refusal");
        Check.Require(linked.StandardError.Contains("symlink", StringComparison.OrdinalIgnoreCase), "symlink-specific diagnostic");
        Check.Require(File.ReadAllBytes(target).SequenceEqual(sentinel), "symlink refusal changed target");
        var targetDirectory = fixture.CreateDirectory("target-directory");
        fixture.LinkDirectory("linked-directory", targetDirectory);
        var child = fixture.Output(Path.Combine("linked-directory", "child.track.json"));
        Failure(fixture.Invoke(inputs.Arguments(child)), 2, child, "symlink ancestor refusal");
        Check.Require(!File.Exists(Path.Combine(targetDirectory, "child.track.json")), "symlink ancestor created target output");
        var absentTarget = fixture.Output("absent-link-target.track.json");
        var dangling = fixture.LinkFile("dangling.track.json", absentTarget);
        var danglingResult = fixture.Invoke(inputs.Arguments(dangling));
        Check.Equal(2, danglingResult.ExitCode, "dangling output link refusal");
        Check.Require(!File.Exists(absentTarget), "dangling-link refusal created target");
    }

    private static void MalformedArguments()
    {
        using var fixture = new PrivateFixture();
        var inputs = new Inputs(fixture);
        string[][] invalid =
        [
            [], ["unknown"],
            ["inspect", "--layout"],
            ["inspect", "--layout", inputs.LayoutPath, "--layout", inputs.LayoutPath],
            ["inspect", "--layout", inputs.LayoutPath, "--unknown", "value"],
            ["inspect", "--layout", inputs.LayoutPath, "trailing"],
            ["convert", "--layout", inputs.LayoutPath, "--common", inputs.CommonPath, "--provenance", inputs.ManifestPath,
                "--display-metres-per-source-unit", "0.5", "--name", "Synthetic CLI strip"]
        ];
        foreach (var arguments in invalid)
        {
            var result = fixture.Invoke(arguments);
            Check.Equal(2, result.ExitCode, $"malformed arguments [{string.Join(" ", arguments)}]");
            Check.Require(string.IsNullOrEmpty(result.StandardOutput) && result.StandardError.StartsWith("invalid:", StringComparison.Ordinal),
                "malformed arguments emitted success-shaped stdout or no invalid diagnostic");
        }
        foreach (var scale in new[] { "0", "-1", "NaN", "Infinity", "1,5", " 0.5 " })
        {
            var output = fixture.Output("bad-scale-" + Guid.NewGuid().ToString("N") + ".track.json");
            var arguments = inputs.Arguments(output);
            arguments[Array.IndexOf(arguments, "--display-metres-per-source-unit") + 1] = scale;
            Failure(fixture.Invoke(arguments), 2, output, $"CLI explicit finite scale {scale}");
        }
        foreach (var length in new[] { 803, 805 })
        {
            var bytes = new byte[length];
            Array.Copy(inputs.LayoutBytes, bytes, Math.Min(bytes.Length, inputs.LayoutBytes.Length));
            var layout = fixture.Write($"layout-{length}.bin", bytes);
            var result = fixture.Invoke("inspect", "--layout", layout);
            Check.Equal(2, result.ExitCode, $"actual CLI layout length {length}");
            Check.Require(string.IsNullOrEmpty(result.StandardOutput), "invalid layout emitted inspection/road output");
        }
    }

    private static void VerifyTrace()
    {
        using var fixture = new PrivateFixture();
        var expectedBytes = SyntheticTrace.Bytes(SyntheticTrace.Create());
        var expected = fixture.Write("expected-synthetic.jsonl", expectedBytes);
        var actual = fixture.Write("actual-synthetic.jsonl", expectedBytes);
        var matched = fixture.Invoke("verify-trace", "--expected", expected, "--actual", actual);
        Check.Equal(0, matched.ExitCode, $"trace CLI match: {matched.StandardError}");
        using (var document = matched.Json("trace CLI match"))
        {
            var root = document.RootElement;
            Check.Equal("match", root.GetProperty("status").GetString(), "trace CLI result");
            Check.Equal(2L, root.GetProperty("comparedTicks").GetInt64(), "trace CLI compared transitions");
            Check.Equal("synthetic-only", root.GetProperty("qualification").GetString(), "trace CLI no original qualification");
            Check.Equal("synthetic", root.GetProperty("oracleBasis").GetString(), "trace CLI explicit fixture basis");
            var input = root.GetProperty("inputs").GetProperty("actual");
            Check.Equal(Hash(expectedBytes), input.GetProperty("sha256").GetString(), "trace CLI full-file digest");
            Check.Equal((long)expectedBytes.Length, input.GetProperty("byteLength").GetInt64(), "trace CLI exact file length");
        }
        var changed = SyntheticTrace.Create();
        changed[2]["after"]!["position"]!["y"] = 99;
        var mismatchPath = fixture.Write("actual-mismatch.jsonl", SyntheticTrace.Bytes(changed));
        var mismatch = fixture.Invoke("verify-trace", "--expected", expected, "--actual", mismatchPath);
        Check.Equal(3, mismatch.ExitCode, "trace CLI mismatch exit");
        using (var document = mismatch.Json("trace CLI mismatch"))
        {
            var root = document.RootElement;
            Check.Equal("mismatch", root.GetProperty("status").GetString(), "trace CLI mismatch result");
            Check.Equal(1L, root.GetProperty("firstMismatchTick").GetInt64(), "trace CLI first differing tick");
            Check.Equal("after.position.y", root.GetProperty("firstMismatchField").GetString(), "trace CLI first differing field");
            Check.Equal("-14", root.GetProperty("expectedValue").GetString(), "trace CLI expected raw value");
            Check.Equal("99", root.GetProperty("actualValue").GetString(), "trace CLI actual raw value");
        }
        var unknown = SyntheticTrace.Create();
        unknown[0]["timing"]!["status"] = "unknown";
        unknown[0]["timing"]!["numerator"] = null;
        unknown[0]["timing"]!["denominator"] = null;
        var unknownPath = fixture.Write("unknown-timing.jsonl", SyntheticTrace.Bytes(unknown));
        var blocked = fixture.Invoke("verify-trace", "--expected", expected, "--actual", unknownPath);
        Check.Equal(4, blocked.ExitCode, "trace CLI unknown timing exit");
        using (var document = blocked.Json("trace CLI blocked"))
        {
            Check.Equal("blocked", document.RootElement.GetProperty("status").GetString(), "trace CLI cannot fall back to authored timing");
            Check.Equal("actual.header.timing", document.RootElement.GetProperty("firstMismatchField").GetString(), "trace CLI timing blocker");
        }
        var empty = fixture.Write("empty-oracle.jsonl", []);
        var noOracle = fixture.Invoke("verify-trace", "--expected", empty, "--actual", actual);
        Check.Equal(4, noOracle.ExitCode, "trace CLI empty oracle exit");
        using (var document = noOracle.Json("trace CLI empty oracle"))
            Check.Equal("expected", document.RootElement.GetProperty("firstMismatchField").GetString(), "trace CLI concrete missing oracle");
        var malformedPath = fixture.Write("malformed-tail.jsonl", expectedBytes.Concat("{}\n"u8.ToArray()).ToArray());
        var malformed = fixture.Invoke("verify-trace", "--expected", expected, "--actual", malformedPath);
        Check.Equal(2, malformed.ExitCode, "trace CLI malformed tail exit");
        Check.Require(string.IsNullOrEmpty(malformed.StandardOutput) && malformed.StandardError.StartsWith("invalid:", StringComparison.Ordinal),
            "malformed trace emitted a comparison-shaped success");
    }

    private static void AssertConverted(JsonElement root, Inputs inputs)
    {
        Check.Fields(root, "converted artifact", "schemaVersion", "track", "geometry", "source", "provenance", "compatibility");
        Check.Equal(1, root.GetProperty("schemaVersion").GetInt32(), "converted schema");
        var track = root.GetProperty("track");
        Check.Fields(track, "stable converted track DTO", "name", "length", "width", "points");
        Check.Equal("Synthetic CLI strip", track.GetProperty("name").GetString(), "explicit track label");
        Check.Equal(12, track.GetProperty("points").GetArrayLength(), "actual source point count");
        Check.Near(Math.Sqrt(185) / 2, track.GetProperty("width").GetDouble(), "actual minimum exact width");
        var geometry = root.GetProperty("geometry");
        Check.Equal("exact-source-edge-strip", geometry.GetProperty("kind").GetString(), "exact geometry kind");
        Check.Equal("geometric-triangles-not-original-integer-sampler", geometry.GetProperty("roadLookup").GetString(),
            "geometric query must not claim original road sampler");
        Check.Equal(16, geometry.GetProperty("edges").GetArrayLength(), "actual emitted exact edges");
        Check.Equal(24, geometry.GetProperty("triangles").GetArrayLength(), "actual canonical triangle count");
        var edge = geometry.GetProperty("edges")[0];
        Check.Equal(0, edge.GetProperty("sectionIndex").GetInt32(), "actual edge source section");
        Check.Equal(0, edge.GetProperty("crossSectionIndex").GetInt32(), "actual edge source cross-section");
        Check.Equal(-6L, edge.GetProperty("sourceLeft").GetProperty("x").GetInt64(), "actual raw left x");
        Check.Equal(10L, edge.GetProperty("sourceLeft").GetProperty("y").GetInt64(), "actual raw left height");
        Check.Equal(23L, edge.GetProperty("sourceRight").GetProperty("y").GetInt64(), "actual raw right height");
        Check.Near(-3, edge.GetProperty("left").GetProperty("x").GetDouble(), "actual emitted world left x");
        Check.Near(5, edge.GetProperty("left").GetProperty("y").GetDouble(), "actual emitted world left y");
        Check.Near(-1, edge.GetProperty("right").GetProperty("x").GetDouble(), "actual emitted world right x");
        var provenance = root.GetProperty("provenance");
        var assets = provenance.GetProperty("assets").EnumerateArray().ToDictionary(item => item.GetProperty("role").GetString()!);
        Check.Equal(2, assets.Count, "actual distinct required provenance roles");
        foreach (var (role, bytes) in new[] { ("layout", inputs.LayoutBytes), ("commonData", inputs.CommonBytes) })
        {
            Check.Equal((long)bytes.Length, assets[role].GetProperty("byteLength").GetInt64(), $"{role} entire input length");
            Check.Equal(Hash(bytes), assets[role].GetProperty("sha256").GetString(), $"{role} entire input digest");
        }
        var manifestBytes = File.ReadAllBytes(inputs.ManifestPath);
        Check.Equal(Hash(manifestBytes), provenance.GetProperty("manifest").GetProperty("sha256").GetString(), "actual manifest digest");
        Check.Equal((long)manifestBytes.Length, provenance.GetProperty("manifest").GetProperty("byteLength").GetInt64(), "actual manifest byte count");
        Check.Equal("unresolved", provenance.GetProperty("rights").GetProperty("status").GetString(), "provenance no rights upgrade");
        Qualification(root.GetProperty("compatibility"));
    }

    private static void Qualification(JsonElement compatibility)
    {
        Check.Equal("amiga-experimental", compatibility.GetProperty("profileId").GetString(), "experimental profile");
        Check.Equal("unqualified-candidate", compatibility.GetProperty("qualification").GetString(), "unqualified import");
        Check.Equal("not-implemented", compatibility.GetProperty("dynamics").GetString(), "no invented original dynamics");
        Check.Equal(JsonValueKind.Null, compatibility.GetProperty("simulationStepSeconds").ValueKind, "unknown original tick");
        Check.Equal("unresolved", compatibility.GetProperty("rights").GetProperty("status").GetString(), "unresolved rights");
        Check.Require(compatibility.GetProperty("blockers").GetArrayLength() > 0, "missing original evidence blockers");
    }

    private static void Failure(CommandResult result, int exit, string output, string context)
    {
        Check.Equal(exit, result.ExitCode, $"{context}: {result.StandardError}");
        Check.Require(!File.Exists(output), $"{context}: partial output exists at {output}");
        Check.Require(string.IsNullOrEmpty(result.StandardOutput), $"{context}: failure emitted success-shaped stdout");
        Check.Require(result.StandardError.StartsWith(exit == 4 ? "blocked:" : "invalid:", StringComparison.Ordinal),
            $"{context}: incorrect failure diagnostic {result.StandardError}");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonObject Asset(string role, byte[] bytes, string sourceId, string? revision) => new()
    {
        ["role"] = role, ["byteLength"] = bytes.LongLength, ["sha256"] = Hash(bytes),
        ["sourceId"] = sourceId, ["sourceRevision"] = revision
    };

    private sealed class Inputs
    {
        public Inputs(PrivateFixture fixture)
        {
            LayoutPath = fixture.Write("layout.bin", LayoutBytes);
            CommonPath = fixture.Write("common.json", CommonBytes);
            ManifestPath = fixture.Write("inputs.json", Manifest());
        }

        public byte[] LayoutBytes { get; } = SyntheticCommon.Layout();
        public byte[] CommonBytes { get; } = SyntheticCommon.Bytes(SyntheticCommon.Create());
        public string LayoutPath { get; }
        public string CommonPath { get; }
        public string ManifestPath { get; }

        public JsonObject Manifest() => new()
        {
            ["schemaVersion"] = 1,
            ["assets"] = new JsonArray
            {
                Asset("layout", LayoutBytes, "synthetic-layout", null),
                Asset("commonData", CommonBytes, "synthetic-compatibility-check", "fixture-v1")
            },
            ["rights"] = new JsonObject { ["status"] = "unresolved", ["evidenceSha256"] = null }
        };

        public string[] Arguments(string output, string? manifest = null, string? common = null) =>
        [
            "convert", "--layout", LayoutPath, "--common", common ?? CommonPath,
            "--provenance", manifest ?? ManifestPath, "--display-metres-per-source-unit", "0.5",
            "--name", "Synthetic CLI strip", "--out", output
        ];
    }
}
