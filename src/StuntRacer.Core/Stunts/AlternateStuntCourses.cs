using static System.Math;
using static StuntRacer.Core.Stunts.StuntParkCourse;

namespace StuntRacer.Core.Stunts;

internal static class AlternateStuntCourses
{
    public static Course Reservoir()
    {
        const double y = 12, w = 12, west = -PI / 2, south = -PI, east = PI / 2;
        var b = new Builder();
        b.Add(new(390, y, -260), new(1, 0, 0), 0, w);
        b.Feature("Loop approach", "LOOP AHEAD — build 125–145 km/h and keep straight");
        b.Line(new(390, y, -40), 0, w);
        var (loopStart, loopEnd) = Loop(b, 17, 20, w, "Vertical loop");
        b.Feature("Loop runout", "BRAKE to 70–80 km/h for the left turn");
        b.Line(new(390, y, 4), 0, w);
        b.Gate("Loop exit", loopEnd + 5);
        b.Line(new(390, y, 90), 0, w);
        b.Feature("Dam bend", "DAM BEND — steer left at 70–80 km/h");
        b.Turn(390, 90, 52, 0, y, w, -PI / 2, 0.32);
        b.Gate("Dam bend", b.Distance - 3);
        b.Feature("Dam straight", "FOLLOW THE DAM — keep left at the next bend");
        b.Line(new(10, y, 142), west, w);
        b.Feature("Spillway bend", "SPILLWAY BEND — steer left, then line up the ramp");
        b.Turn(10, 142, 49, west, y, w, -PI / 2, 0.32);
        b.Gate("Spillway bend", b.Distance - 3);
        b.Feature("Spillway approach", "GAP AHEAD — line up straight at 95–110 km/h");
        b.Line(new(-39, y, 15), south, w);
        var (rampEnd, landingStart, landingEnd) = Jump(b, south, w, 26, 18, 56, 48, 0.25,
            "SPILLWAY JUMP — stay aligned over the 18 m gap", "LANDING RUNWAY — brake to 70–80 km/h for a left bend");
        b.Line(new(-39, y, -147), south, w);
        b.Feature("Spillway return", "SPILLWAY RETURN — steer left, then right through the dogleg");
        b.Turn(-39, -147, 48, south, y, w, -PI / 2, 0.32);
        b.Line(new(24, y, -195), east, w);
        b.Feature("Dogleg right", "DOGLEG — steer right at 70–75 km/h");
        b.Turn(24, -195, 44, east, y, w, PI / 2, 0.32);
        b.Gate("Dogleg right", b.Distance - 3);
        b.Feature("Dogleg straight", "NEXT BEND LEFT — line up for the narrow bridge");
        b.Line(new(68, y, -294), PI, w);
        b.Feature("Dogleg left", "DOGLEG — steer left at 70–75 km/h");
        b.Turn(68, -294, 45, PI, y, w, -PI / 2, 0.32);
        Bridge(b, east, w, 55, 62, 8, 6.4, "Reservoir bridge",
            "RESERVOIR BRIDGE — 6.4 m wide; keep straight at 70–80 km/h");
        b.Line(new(340, y, -339), east, w);
        b.Feature("Home bend", "HOME BEND — steer left at 70–80 km/h");
        b.Turn(340, -339, 50, east, y, w, -PI / 2, 0.32);
        b.Feature("Finish straight", "FINISH — full loop, clean gap and all route gates");
        b.Line(new(390, y, -260), 0, w);
        return b.Finish(loopStart, loopEnd, rampEnd, landingStart, landingEnd,
            "Reservoir Run — authored Stunts challenge", w);
    }

    public static Course Switchback()
    {
        const double y = 10, w = 16, west = 3 * PI / 2, south = PI, east = PI / 2;
        var b = new Builder();
        b.Add(new(-250, y, -250), new(1, 0, 0), 0, w);
        b.Feature("Speed runway", "SPEED RUNWAY — build 135–150 km/h; the checkpoint clock is live");
        b.Line(new(-250, y, -65), 0, w);
        b.Gate("Speed trap", 60);
        b.Line(new(-250, y, 35), 0, w);
        var (loopStart, loopEnd) = Loop(b, 20, 18, w, "Proving-ground loop");
        b.Feature("Fast loop exit", "LOOP EXIT — brake to 95–105 km/h for the right bends");
        b.Line(new(-250, y, 72), 0, w);
        b.Gate("Loop exit", loopEnd + 5);
        b.Line(new(-250, y, 147), 0, w);
        b.Feature("Upper banking", "UPPER BANKING — steer right at 95–105 km/h");
        b.Turn(-250, 147, 72, 0, y, w, PI / 2, 0.38);
        b.Feature("Switchback approach", "NEXT BEND RIGHT — brake to 85–95 km/h");
        b.Line(new(-100, y, 219), east, w);
        b.Feature("Switchback right", "SWITCHBACK — tight right at 85–95 km/h, then left ahead");
        b.Turn(-100, 219, 58, east, y, w, PI / 2, 0.38);
        b.Gate("Switchback right", b.Distance - 3);
        b.Feature("Channel approach", "SHORT STRAIGHT — prepare for a left bend at 85–95 km/h");
        b.Line(new(-42, y, 89), south, w);
        b.Feature("Channel bend", "CHANNEL BEND — left at 85–95 km/h; straighten before accelerating");
        b.Turn(-42, 89, 54, south, y, w, -PI / 2, 0.38);
        b.Gate("Channel bend", b.Distance - 3);
        b.Feature("Channel runway", "CHANNEL JUMP — line up at 110–125 km/h");
        b.Line(new(110, y, 35), east, w);
        var (rampEnd, landingStart, landingEnd) = Jump(b, east, w, 30, 24, 100, 60, 0.23,
            "CHANNEL JUMP — real takeoff over the 24 m gap", "LANDING RUNWAY — settle at 100–110 km/h for the right bend");
        b.Line(new(378, y, 35), east, w);
        b.Feature("East banking", "EAST BANKING — steer right at 95–110 km/h");
        b.Turn(378, 35, 77, east, y, w, PI / 2, 0.38);
        b.Feature("South straight", "SOUTH STRAIGHT — build speed, then brake to 100–110 km/h for the right bend");
        b.Line(new(455, y, -205), south, w);
        b.Gate("South straight", b.Distance - 40);
        b.Line(new(455, y, -287), south, w);
        b.Feature("South banking", "SOUTH BANKING — steer right, then straighten across the timing bridge");
        b.Turn(455, -287, 73, south, y, w, PI / 2, 0.38);
        b.Line(new(357, y, -360), west, w);
        Bridge(b, west, w, 65, 82, 11, 8, "Timing bridge",
            "TIMING BRIDGE — 8 m wide; stay straight at 95–105 km/h");
        b.Feature("Home sprint", "HOME SPRINT — gain time here; final right bend after the gate");
        b.Line(new(-115, y, -360), west, w);
        b.Gate("Home sprint", b.Distance - 30);
        b.Feature("Home approach", "BRAKE to 100–110 km/h for the final right");
        b.Line(new(-172, y, -360), west, w);
        b.Feature("West banking", "WEST BANKING — steer right then sprint to the flag");
        b.Turn(-172, -360, 78, west, y, w, PI / 2, 0.38);
        b.Feature("Finish straight", "FINISH — clear the loop, jump and every gate before time expires");
        b.Line(new(-250, y, -250), Tau, w);
        return b.Finish(loopStart, loopEnd, rampEnd, landingStart, landingEnd,
            "Switchback Test Track — authored Hard Drivin' timed challenge", w, checkpointSlack: 2, checkpointPace: 33);
    }

    private static (double Start, double End) Loop(Builder b, double radius, double offset, double width, string name)
    {
        var origin = b.Center;
        var start = b.Distance;
        b.Feature(name, "VERTICAL LOOP — keep straight; contact needs speed");
        b.Samples(720, t =>
        {
            var a = t * Tau;
            return (origin + new StuntVector(0, radius * (1 - Cos(a)), radius * Sin(a) + offset * t),
                new StuntVector(1, 0, 0), 0, width, true);
        });
        return (start, b.Distance);
    }

    private static void Bridge(Builder b, double heading, double width, double climb, double deck,
        double rise, double deckWidth, string name, string hint)
    {
        var origin = b.Center;
        var ahead = Direction(heading);
        var right = Direction(heading + PI / 2);
        b.Feature("Bridge climb", hint);
        b.Samples((int)Ceiling(climb / 0.6), t => (origin + ahead * (climb * t) + new StuntVector(0, rise * Smooth(t), 0),
            right, heading, width + (deckWidth - width) * Smooth(t), true));
        b.Feature(name, hint);
        b.Line(b.Center + ahead * deck, heading, deckWidth);
        b.Gate(name, b.Distance - 8);
        var top = b.Center;
        b.Feature("Bridge descent", hint);
        b.Samples((int)Ceiling(climb / 0.6), t => (top + ahead * (climb * t) + new StuntVector(0, -rise * Smooth(t), 0),
            right, heading, deckWidth + (width - deckWidth) * Smooth(t), true));
    }

    private static (double RampEnd, double LandingStart, double LandingEnd) Jump(Builder b, double heading,
        double width, double ramp, double gap, double runway, double descent, double slope, string jumpHint, string landingHint)
    {
        var origin = b.Center;
        var ahead = Direction(heading);
        var right = Direction(heading + PI / 2);
        const double easing = 12;
        b.Feature("Gap ramp", jumpHint);
        b.Samples((int)Ceiling(ramp / 0.4), t =>
        {
            var d = ramp * t;
            var rise = slope * (d < easing ? d * d / (2 * easing) : d - easing / 2);
            return (origin + ahead * d + new StuntVector(0, rise, 0), right, heading, width, true);
        });
        var rampEnd = b.Distance;
        var lip = b.Center;
        b.Feature("Open gap", jumpHint);
        b.Samples((int)Ceiling(gap / 0.65), t => (lip + ahead * (gap * t), right, heading, width, t == 1));
        var landingStart = b.Distance;
        b.Feature("Landing runway", landingHint);
        b.Line(b.Center + ahead * runway, heading, width);
        var top = b.Center;
        b.Samples((int)Ceiling(descent / 0.6), t => (top + ahead * (descent * t) +
            new StuntVector(0, (origin.Y - top.Y) * Smooth(t), 0), right, heading, width, true));
        b.Gate("Landing runout", b.Distance - 10);
        return (rampEnd, landingStart, b.Distance);
    }

    private static StuntVector Direction(double heading) => new(Sin(heading), 0, Cos(heading));
    private static double Smooth(double t) => t * t * (3 - 2 * t);
}
