using System.Text;
using System.Text.Json.Nodes;

namespace StuntRacer.CompatibilityChecks;

internal static class SyntheticTrace
{
    public const string InputSha256 = "774b2b06b0a0193fd07ba88d72403e3bd5f12c3789ab6a903abd79bf98a73f57";

    public static List<JsonObject> Create() =>
    [
        new()
        {
            ["type"] = "header",
            ["schemaVersion"] = 1,
            ["stateSchema"] = "amiga-raw-state-v1",
            ["transitionCount"] = 2,
            ["baseline"] = new JsonObject
            {
                ["executableSha256"] = new string('1', 64),
                ["version"] = "synthetic-not-an-executable",
                ["region"] = "synthetic"
            },
            ["tablesSha256"] = new string('2', 64),
            ["inputSha256"] = InputSha256,
            ["stateMappingSha256"] = new string('3', 64),
            ["timing"] = new JsonObject
            {
                ["status"] = "known",
                ["numerator"] = 7,
                ["denominator"] = 41,
                ["evidenceSha256"] = new string('4', 64)
            },
            ["provenance"] = new JsonObject
            {
                ["kind"] = "synthetic",
                ["producer"] = "synthetic-fixture-only",
                ["evidenceSha256"] = new string('5', 64)
            }
        },
        new()
        {
            ["type"] = "initial",
            ["tick"] = 0,
            ["state"] = State(0)
        },
        Transition(1, 1234, 255, 0, true, false),
        Transition(2, -1234, 0, 127, false, true)
    ];

    public static byte[] Bytes(IEnumerable<JsonObject> records, bool finalNewline = true) =>
        Encoding.UTF8.GetBytes(string.Join("\n", records.Select(record => record.ToJsonString())) +
                               (finalNewline ? "\n" : ""));

    private static JsonObject Transition(int tick, short steer, byte throttle, byte brake, bool boost, bool recover) => new()
    {
        ["type"] = "tick",
        ["tick"] = tick,
        ["input"] = new JsonObject
        {
            ["steer"] = steer, ["throttle"] = throttle, ["brake"] = brake,
            ["boost"] = boost, ["recover"] = recover
        },
        ["after"] = State(tick)
    };

    private static JsonObject State(int tick) => new()
    {
        ["position"] = new JsonObject { ["x"] = int.MinValue + tick, ["y"] = -15 + tick, ["z"] = int.MaxValue - tick },
        ["angles"] = new JsonObject { ["yaw"] = tick == 0 ? 65535 : tick, ["pitch"] = 16384, ["roll"] = 32768 },
        ["linearVelocity"] = new JsonObject { ["x"] = short.MinValue, ["y"] = -3, ["z"] = short.MaxValue },
        ["angularVelocity"] = new JsonObject { ["yaw"] = -7, ["pitch"] = short.MinValue, ["roll"] = short.MaxValue },
        ["contacts"] = new JsonObject
        {
            ["frontLeft"] = Contact(10 + tick, -2, 255),
            ["frontRight"] = Contact(20 + tick, 3, 128),
            ["rear"] = Contact(30 + tick, -4, 1)
        },
        ["resources"] = new JsonObject
        {
            ["boostReserve"] = 255 - tick, ["boostSubunit"] = 0, ["damage"] = 1,
            ["damageLimit"] = 255, ["engineRevs"] = 65535
        },
        ["road"] = new JsonObject
        {
            ["section"] = 65535, ["segment"] = tick, ["lateralPosition"] = short.MinValue,
            ["distanceIntoSection"] = -123456 + tick
        },
        ["runtime"] = new JsonObject
        {
            ["flags"] = uint.MaxValue - (uint)tick, ["offTrackTicks"] = 65535,
            ["recoveryTicks"] = tick, ["clockAccumulator"] = 255, ["damageCooldown"] = 0
        }
    };

    private static JsonObject Contact(int height, short compression, byte flags) => new()
    {
        ["roadHeight"] = height, ["actualHeight"] = -height, ["compression"] = compression,
        ["previousCompression"] = -compression, ["damage"] = 255, ["flags"] = flags
    };
}
