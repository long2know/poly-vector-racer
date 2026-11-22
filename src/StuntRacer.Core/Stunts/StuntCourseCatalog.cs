namespace StuntRacer.Core.Stunts;

public sealed record StuntCourseOption(string Id, string Mode, string Name, string Description, bool IsDefault);

/// <summary>Feature stations on the canonical spatial track, measured in metres from the start.</summary>
public sealed record StuntCourseFeatures(string Id, string Mode,
    double LoopStart, double LoopEnd, double RampEnd, double LandingStart);

internal static class StuntCourseCatalog
{
    internal sealed record Definition(StuntCourseOption Option, Func<StuntParkCourse.Course> Build);

    internal static readonly Definition Skyline = new(
        new("stunts-skyline", "stunts", "Skyline Loop Park",
            "A vertical loop, banked right turns, precision bridge and 16-metre gap.", true),
        StuntParkCourse.Build);

    internal static readonly Definition ProvingGround = new(
        new("hard-proving-ground", "hard-driving", "Loop & Bridge Proving Ground",
            "A 44-metre loop, fast banked corners, a narrow flyover and a 22-metre gap on the checkpoint clock.", true),
        HardDrivingSpatialCourse.Build);

    private static readonly Definition[] Definitions =
    [
        Skyline,
        new(new("stunts-reservoir", "stunts", "Reservoir Run",
            "Loop above the reservoir, bank left to jump the spillway, then cross the narrow return bridge.", false),
            AlternateStuntCourses.Reservoir),
        ProvingGround,
        new(new("hard-switchback", "hard-driving", "Switchback Test Track",
            "Brake for the switchbacks, jump the channel and cross the south timing bridge before the clock runs out.", false),
            AlternateStuntCourses.Switchback)
    ];

    internal static IReadOnlyList<StuntCourseOption> Options { get; } =
        Array.AsReadOnly(Definitions.Select(d => d.Option).ToArray());

    internal static Definition Get(string id) =>
        Definitions.FirstOrDefault(d => d.Option.Id == id)
        ?? throw new ArgumentException($"Unknown spatial course '{id}'.", nameof(id));
}
