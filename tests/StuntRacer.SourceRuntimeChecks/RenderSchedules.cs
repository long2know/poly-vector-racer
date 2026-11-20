using StuntRacer.Core;

namespace StuntRacer.SourceRuntimeChecks;

internal readonly record struct InputSegment(int Ticks, DriverInput Input);

internal static class RenderSchedules
{
    public static readonly (string Name, double[] Seconds)[] All =
    [
        ("30Hz", [1.0 / 30]),
        ("60Hz", [1.0 / 60]),
        ("120Hz", [1.0 / 120]),
        ("144Hz", [1.0 / 144]),
        ("jitter", [1.0 / 90, 1.0 / 144, 1.0 / 60, 1.0 / 75, 1.0 / 30])
    ];

    public static readonly InputSegment[] Inputs =
    [
        new(12, new(0, 0, 0, false)),
        new(40, new(0, 1, 0, false)),
        new(18, new(0.35, 1, 0, true)),
        new(20, new(-0.2, 0, 1, false)),
        new(30, new(0, 0.6, 0, false))
    ];
}
