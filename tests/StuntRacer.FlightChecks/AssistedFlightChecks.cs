using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class AssistedFlightChecks
{
    private const double ReleaseCourse = Math.PI - .08;

    public static void BayReversal()
    {
        var cruise = new FlightInput(0, 0, 0, .35, false, false) { Assisted = true };
        var left = Turn("Bay left", FlightScenarios.AirStart(), cruise, -1, 38);
        var right = Turn("Bay right", FlightScenarios.AirStart(), cruise, 1, 38);
        CheckReversal(left, minSpeed: 85, maxSpeed: 145, maxHeightChange: 60, minRadius: 400, maxRadius: 1500);
        CheckReversal(right, minSpeed: 85, maxSpeed: 145, maxHeightChange: 60, minRadius: 400, maxRadius: 1500);
        Check.Near(left.Radius, right.Radius, 1e-8, "Sustained left/right input has symmetric ground-track radii");
    }

    public static void WorldReversal()
    {
        var cruise = new FlightInput(0, 0, 0, .47, false, false) { Assisted = true };
        foreach (var terrainHeight in new short[] { 0, 700 })
        {
            var atlas = new WorldAtlasElevation(new WorldAtlasBase(AtlasChecks.Fallback(terrainHeight)));
            var scenic = AirStart(FlightWorldDefinition.CreateAtlas(new("turn", "Turn departure", 0, 0, 0), atlas));
            var turn = Turn($"Atlas F/A-18 scenic {scenic.Snapshot.Aircraft.Speed:F0} m/s", scenic, cruise, 1, 40);
            CheckReversal(turn, minSpeed: 130, maxSpeed: 245, maxHeightChange: 65, minRadius: 1000, maxRadius: 3500);
            Check.That(turn.HoldSeconds < 40, "An ordinary Atlas departure reverses promptly at its default throttle.");
        }
    }

    public static void SupersonicArc()
    {
        var cruise = new FlightInput(0, 0, 0, .47, false, false) { Assisted = true };
        var elevation = new WorldFlightElevation();
        var tile = new byte[WorldFlightElevation.TileLength];
        "WFT1"u8.CopyTo(tile);
        elevation.AddTile(tile);
        FlightSimulation AtAltitude(double altitude)
        {
            var departure = new WorldFlightDestination("turn", "Turn departure", 100_000, 100_000, altitude, 0, 100_000, 100_000);
            return AirStart(FlightWorldDefinition.CreateGeographic("Test", departure, null, elevation));
        }

        var scenic = Turn("World F/A-18 scenic", AtAltitude(1400), cruise, -1, 40);
        CheckReversal(scenic, minSpeed: 130, maxSpeed: 245, maxHeightChange: 65, minRadius: 1000, maxRadius: 3500);
        var jet = AtAltitude(8500);
        var fullBurn = cruise with { Throttle = 1, Afterburner = true };
        FlightScenarios.Advance(jet, 60, fullBurn);
        var highMach = Turn("World F/A-18 supersonic arc", jet, fullBurn, 1, 24);
        var a = highMach.State.Aircraft;
        Check.That(highMach.State is { Phase: "flying", Recoveries: 0, Crashes: 0 } && !a.OnGround && !highMach.Stalled &&
                   highMach.HoldTrack is > .19 and < .50 && Math.Abs(highMach.Track - highMach.Nose) < .10 &&
                   highMach.HoldBank is > .50 and < .72 && highMach.MaxBank < .78 && Math.Abs(a.Roll) < .06,
            "A fast turn banks, changes ground track progressively and levels again without a stall or impact.");
        Check.That(highMach.State.Aircraft.Mach is > 1.65 and <= 1.805 &&
                   highMach.Radius is > 25_000 and < 70_000 && highMach.Radius > scenic.Radius * 8 &&
                   highMach.MinHeight > -65 && highMach.MaxHeight < 65 && highMach.MaxG < 3,
            "The same jet near its published Mach limit must fly a far wider arc without diving or rocketing upwards.");
    }

    private static FlightSimulation AirStart(FlightWorldDefinition world)
    {
        var simulation = new FlightSimulation(world);
        simulation.Command("air-start");
        return simulation;
    }

    private static void CheckReversal(TurnTrace trace, double minSpeed, double maxSpeed, double maxHeightChange,
        double minRadius, double maxRadius)
    {
        var a = trace.State.Aircraft;
        Check.That(trace.State is { Phase: "flying", Recoveries: 0, Crashes: 0 } && !a.OnGround && !trace.Stalled,
            $"{trace.Name}: the whole turn and rollout must be flown without recovery, contact, or stall.");
        Check.That(trace.HoldTrack >= ReleaseCourse && Math.Abs(trace.Track - Math.PI) < .12 &&
                   Math.Abs(trace.Track - trace.Nose) < .10 && trace.MaxTrackStep < .004,
            $"{trace.Name}: both the travelled course and nose must reverse progressively in the commanded direction.");
        Check.That(trace.HoldBank is > .5 and < 1.08 && trace.MaxBank < 1.10 && trace.LevelBank < .06 &&
                   Math.Abs(a.Roll) < .06,
            $"{trace.Name}: the full aileron input holds a bounded bank, and neutral levels the wings.");
        Check.That(Math.Abs(trace.Track - trace.LevelTrack) < .04,
            $"{trace.Name}: the ground track does not keep swinging after rollout.");
        Check.That(trace.MinSpeed > minSpeed && trace.MaxSpeed < maxSpeed && trace.MaxSpeed - trace.MinSpeed > 2 &&
                   trace.MinHeight > -maxHeightChange && trace.MaxHeight < maxHeightChange &&
                   trace.MaxHeight - trace.MinHeight > 1 && trace.MaxG < 3,
            $"{trace.Name}: credible speed, bounded load and finite climb/sink; no altitude lock.");
        Check.That(trace.Radius > minRadius && trace.Radius < maxRadius && trace.Range > minRadius * 2.5,
            $"{trace.Name}: a genuine travelling arc, not a spin on the spot.");
    }

    private static TurnTrace Turn(string name, FlightSimulation simulation, FlightInput cruise, int direction, int timeout)
    {
        var state = simulation.Snapshot;
        var start = state.Aircraft;
        var heading = start.Yaw;
        double track = 0, nose = 0, maxStep = 0, maxBank = 0, range = 0;
        double minHeight = 0, maxHeight = 0, minSpeed = start.Speed, maxSpeed = start.Speed, maxG = start.GForce;
        var stalled = false;
        var heldSteps = 0;
        var bank = cruise with { Roll = direction };
        for (; heldSteps < timeout * 120 && track < ReleaseCourse && state.Phase == "flying"; heldSteps++)
            Step(bank);
        var holdTrack = track;
        var holdBank = -direction * state.Aircraft.Roll;
        var holdSeconds = heldSteps * FlightScenarios.Step;
        var arcLength = range;
        // Horizontal arc length divided by course deflection estimates the average turn radius.
        var radius = holdTrack > .1 ? arcLength / holdTrack : double.NaN;
        for (var i = 0; i < 2 * 120 && state.Phase == "flying"; i++)
            Step(cruise);
        var levelBank = Math.Abs(state.Aircraft.Roll);
        for (var i = 0; i < 2 * 120 && state.Phase == "flying"; i++)
            Step(cruise);
        var levelTrack = track;
        for (var i = 0; i < 2 * 120 && state.Phase == "flying"; i++)
            Step(cruise);
        Console.WriteLine($"TRACE {name} held={holdSeconds:F2}s course={holdTrack * 180 / Math.PI:F1}° " +
            $"rollout={track * 180 / Math.PI:F1}° nose={nose * 180 / Math.PI:F1}° " +
            $"radius≈{radius:F0}m arc={arcLength:F0}m total={range:F0}m heightDelta=[{minHeight:F1},{maxHeight:F1}]m " +
            $"speed=[{minSpeed:F1},{maxSpeed:F1}]m/s bankMax={maxBank * 180 / Math.PI:F1}° loadMax={maxG:F2}G " +
            $"stall={stalled} {FlightScenarios.Describe(state)}");
        return new(name, state, stalled, holdSeconds, holdTrack, track, levelTrack, maxStep, nose, holdBank, maxBank, levelBank,
            radius, range, minSpeed, maxSpeed, minHeight, maxHeight, maxG);

        void Step(FlightInput input)
        {
            var previous = state.Aircraft;
            state = simulation.Advance(FlightScenarios.Step, input);
            var a = state.Aircraft;
            nose += direction * Math.IEEERemainder(a.Yaw - previous.Yaw, Math.Tau);
            var dx = a.X - previous.X;
            var dz = a.Z - previous.Z;
            range += Math.Sqrt(dx * dx + dz * dz);
            if (dx != 0 || dz != 0)
            {
                var next = Math.Atan2(dx, dz);
                var increment = Math.IEEERemainder(next - heading, Math.Tau);
                track += direction * increment;
                maxStep = Math.Max(maxStep, Math.Abs(increment));
                heading = next;
            }
            maxBank = Math.Max(maxBank, Math.Abs(a.Roll));
            minSpeed = Math.Min(minSpeed, a.Speed);
            maxSpeed = Math.Max(maxSpeed, a.Speed);
            minHeight = Math.Min(minHeight, a.Y - start.Y);
            maxHeight = Math.Max(maxHeight, a.Y - start.Y);
            maxG = Math.Max(maxG, Math.Abs(a.GForce));
            stalled |= a.Stalled;
        }
    }

    private sealed record TurnTrace(string Name, FlightSnapshot State, bool Stalled, double HoldSeconds,
        double HoldTrack, double Track, double LevelTrack, double MaxTrackStep, double Nose, double HoldBank,
        double MaxBank, double LevelBank, double Radius, double Range, double MinSpeed, double MaxSpeed,
        double MinHeight, double MaxHeight, double MaxG);
}
