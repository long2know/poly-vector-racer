namespace StuntRacer.Core.Stunts;

/// <summary>
/// Original high-speed proving ground: four banked corners, a 44 m vertical loop,
/// a narrow flyover and a separate ramp/22 m gap/landing bridge. Not Atari track data.
/// </summary>
public static class HardDrivingSpatialCourse
{
    public static TrackDefinition CreateTrack() => Build().Track;

    internal static StuntParkCourse.Course Build()
    {
        const double ground = 8;
        const double width = 16;
        const double landingHeight = ground + 6.24;
        var b = new StuntParkCourse.Builder();
        b.Add(new(-180, ground, -300), new(1, 0, 0), 0, width);
        b.Feature("Speed runway");
        b.Line(new(-180, ground, -80), 0, width);
        b.Gate("Speed trap", 60);
        b.Line(new(-180, ground, 0), 0, 14);
        var loopStart = b.Distance;
        b.Feature("Proving-ground loop");
        b.Samples(768, t =>
        {
            var angle = t * Math.Tau;
            return (new StuntVector(-180, ground + 22 * (1 - Math.Cos(angle)),
                22 * Math.Sin(angle) + 14 * t), new StuntVector(1, 0, 0), 0d, 14d, true);
        });
        var loopEnd = b.Distance;
        b.Feature("Fast loop exit");
        b.Line(new(-180, ground, 34), 0, width);
        b.Gate("Loop exit", loopEnd + 5);
        b.Line(new(-180, ground, 155), 0, width);
        b.Feature("North banking");
        b.Turn(-180, 155, 90, 0, ground, width, Math.PI / 2, 0.38);
        b.Gate("North banking", b.Distance - 3);
        b.Feature("Flyover climb");
        b.Samples(112, t => (new StuntVector(-90 + 70 * t, ground + 12 * Smooth(t), 245),
            new StuntVector(0, 0, -1), Math.PI / 2, width + (8.4 - width) * Smooth(t), true));
        b.Feature("Precision flyover");
        b.Line(new(55, 20, 245), Math.PI / 2, 8.4);
        b.Gate("Precision flyover", b.Distance - 8);
        b.Feature("Flyover descent");
        b.Samples(112, t => (new StuntVector(55 + 70 * t, 20 - 12 * Smooth(t), 245),
            new StuntVector(0, 0, -1), Math.PI / 2, 8.4 + (width - 8.4) * Smooth(t), true));
        b.Feature("East straight");
        b.Line(new(235, ground, 245), Math.PI / 2, width);
        b.Feature("East banking");
        b.Turn(235, 245, 75, Math.PI / 2, ground, width, Math.PI / 2, 0.38);
        b.Gate("East banking", b.Distance - 3);
        b.Feature("Ramp runway");
        b.Line(new(310, ground, 40), Math.PI, width);
        b.Gate("Ramp approach", b.Distance - 45);
        b.Feature("Jump ramp");
        b.Samples(80, t =>
        {
            var d = t * 32;
            var rise = d < 12 ? 0.24 * d * d / 24 : 1.44 + 0.24 * (d - 12);
            return (new StuntVector(310, ground + rise, 40 - d),
                new StuntVector(-1, 0, 0), Math.PI, width, true);
        });
        var rampEnd = b.Distance;
        b.Feature("Proving gap");
        b.Samples(32, t => (new StuntVector(310, landingHeight, 8 - 22 * t),
            new StuntVector(-1, 0, 0), Math.PI, width, t == 1));
        var landingStart = b.Distance;
        b.Feature("Landing bridge");
        b.Line(new(310, landingHeight, -104), Math.PI, width);
        b.Feature("Bridge runout");
        b.Samples(100, t => (new StuntVector(310, landingHeight - 6.24 * Smooth(t), -104 - 60 * t),
            new StuntVector(-1, 0, 0), Math.PI, width, true));
        var landingEnd = b.Distance;
        b.Gate("Bridge landing", b.Distance - 12);
        b.Feature("South straight");
        b.Line(new(310, ground, -300), Math.PI, width);
        b.Feature("South banking");
        b.Turn(310, -300, 90, Math.PI, ground, width, Math.PI / 2, 0.38);
        b.Gate("South banking", b.Distance - 3);
        b.Feature("Home sprint");
        b.Line(new(50, ground, -390), Math.PI * 1.5, width);
        b.Gate("Home sprint", b.Distance - 20);
        b.Line(new(-90, ground, -390), Math.PI * 1.5, width);
        b.Feature("West banking");
        b.Turn(-90, -390, 90, Math.PI * 1.5, ground, width, Math.PI / 2, 0.38);
        return b.Finish(loopStart, loopEnd, rampEnd, landingStart, landingEnd,
            "Hard Drivin' Loop & Bridge Proving Ground — authored timed challenge", width);
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
}
