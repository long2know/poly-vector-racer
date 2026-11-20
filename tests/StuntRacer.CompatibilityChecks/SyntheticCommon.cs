using System.Text;
using System.Text.Json.Nodes;

namespace StuntRacer.CompatibilityChecks;

internal static class SyntheticCommon
{
    public static byte[] Layout() => SyntheticLayout.Create(
        new(0, 0x00, 0, 1, 10, 20),
        new(0, 0x40, 0, 1, 10, 20),
        new(0, 0x80, 0, 1, 10, 20),
        new(0, 0xc0, 0, 1, 10, 20));

    public static byte[] Bytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");

    public static JsonObject Create() => new()
    {
        ["schemaVersion"] = 1,
        ["sourceId"] = "synthetic-compatibility-check",
        ["sourceRevision"] = "fixture-v1",
        ["circuitMode"] = "static",
        ["encodingEvidenceId"] = "synthetic-decoded-integers-not-native",
        ["layoutGeometry"] = new JsonObject
        {
            ["tileSizeX"] = 100L,
            ["tileSizeZ"] = 200L,
            ["rotationPivotX"] = 0L,
            ["rotationPivotZ"] = 0L,
            ["positiveQuarterTurn"] = "x-to-negative-z",
            ["reversalHeightBinding"] = "travel-ordered",
            ["seamPolicy"] = "require-equal",
            ["evidenceId"] = "synthetic-layout-units"
        },
        ["coordinateTransform"] = new JsonObject
        {
            ["xAxis"] = "+x", ["yAxis"] = "+y", ["zAxis"] = "+z",
            ["xDivisor"] = 1L, ["yDivisor"] = 1L, ["zDivisor"] = 1L,
            ["evidenceId"] = "synthetic-coordinate-units"
        },
        ["templates"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = 0,
                ["restartEligible"] = true,
                ["crossSections"] = new JsonArray
                {
                    Section(-6, 0, -2, 0),
                    Section(-5, 2, -1, 1),
                    Section(-2, 5, -1, 2),
                    Section(0, 6, 0, 2)
                }
            }
        },
        ["heightProfiles"] = new JsonArray
        {
            Profile(0, 0, 2, 7, 0),
            Profile(1, 3, 5, 11, 3)
        }
    };

    private static JsonObject Section(long leftX, long leftZ, long rightX, long rightZ) => new()
    {
        ["leftX"] = leftX, ["leftZ"] = leftZ, ["rightX"] = rightX, ["rightZ"] = rightZ
    };

    private static JsonObject Profile(int id, params long[] samples) => new()
    {
        ["id"] = id,
        ["sampleCount"] = samples.Length,
        ["sourceStorage"] = new JsonObject
        {
            ["kind"] = "normalized-integers",
            ["leftHighBit"] = false,
            ["evidenceId"] = "synthetic-decoded-storage"
        },
        ["normalization"] = new JsonObject
        {
            ["sampleMultiplier"] = 1L,
            ["sampleDivisor"] = 1L,
            ["shiftMultiplier"] = 1L,
            ["shiftDivisor"] = 1L,
            ["evidenceId"] = "synthetic-explicit-height-units"
        },
        ["samples"] = new JsonArray(samples.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };
}
