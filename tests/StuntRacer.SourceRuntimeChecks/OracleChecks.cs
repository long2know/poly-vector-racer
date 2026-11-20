using System.Text.Json;
using System.Text.Json.Nodes;
using StuntRacer.CompatibilityChecks;
using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal static class OracleChecks
{
    public static IEnumerable<NativeCheck> All(string path)
    {
        yield return new("oracle / unmodified reviewed source scalar outputs match actual C# components", () =>
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            AssertOutputs(document.RootElement);
            Console.WriteLine($"  oracle {document.RootElement.GetProperty("compiler").GetString()}, " +
                              $"long={document.RootElement.GetProperty("longBits").GetInt32()} bits; " +
                              "bounded scalar comparison only, not full-player/source32/stock parity.");
        });
        yield return new("oracle / mutation of actual emitted source result turns comparison red", () =>
        {
            var emitted = JsonNode.Parse(File.ReadAllBytes(path))!;
            emitted["worldSpeed"]![0]!["x"] = 101;
            using var mutant = JsonDocument.Parse(emitted.ToJsonString());
            Check.Throws<InvalidOperationException>(() => AssertOutputs(mutant.RootElement),
                "actual source-result mutation must be detected");
        });
    }

    private static void AssertOutputs(JsonElement root)
    {
        Check.Equal(1, root.GetProperty("schemaVersion").GetInt32(), "component receipt schema");
        Check.Equal("ptitseb-reviewed-components-v1", root.GetProperty("kind").GetString(), "component receipt kind");
        Check.Equal("ba6db6b7122227640c6140621e161cec13744e1b2ef88763be76680f41fb053d",
            root.GetProperty("sourceSha256").GetString(), "reviewed unmodified external source identity");
        Check.Equal(32, root.GetProperty("intBits").GetInt32(), "oracle native int width");
        Check.Require(root.GetProperty("longBits").GetInt32() is 32 or 64, "unsupported native long width");
        var collision = root.GetProperty("collision");
        Check.Equal(2, collision.GetArrayLength(), "reviewed collision vector count");
        int[] collisionExpected = [0, -14];
        for (var index = 0; index < collision.GetArrayLength(); index++)
        {
            var row = collision[index];
            var observed = row.GetProperty("result").GetInt32();
            Check.Equal(collisionExpected[index], observed, "independent collision source result");
            Check.Equal(observed, SourceDynamicsMath.TwiceCollisionYAcceleration(
                row.GetProperty("touchingRoad").GetBoolean(), row.GetProperty("collisionY").GetInt32()),
                "C# collision component vs actual unmodified reference output");
        }
        var speeds = root.GetProperty("worldSpeed");
        Check.Equal(8, speeds.GetArrayLength(), "reviewed world-speed vector count");
        int[] deltas = [0, -1, 237, -238, 238, -238, 238, -239];
        for (var index = 0; index < speeds.GetArrayLength(); index++)
        {
            var row = speeds[index];
            var initial = row.GetProperty("initial").GetInt32();
            var candidate = SourceDynamicsMath.AdvanceVelocity(initial, row.GetProperty("acceleration").GetInt32());
            foreach (var axis in new[] { "x", "y", "z" })
            {
                var observed = row.GetProperty(axis).GetInt32();
                Check.Equal(initial + deltas[index], observed, $"independent source world {axis} vector {index}");
                Check.Equal(observed, candidate, $"actual C# world {axis} vs external source vector {index}");
            }
        }
    }
}
