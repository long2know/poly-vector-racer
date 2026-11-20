using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class DynamicsChecks
{
    public static void RunwayTakeoffAndClimb()
    {
        var simulation = new FlightSimulation();
        simulation.Command("start");
        FlightScenarios.Advance(simulation, 3, FlightScenarios.Idle);
        FlightSnapshot? liftoff = null;
        for (var i = 0; i < 120 * 32; i++)
        {
            var a = simulation.Snapshot.Aircraft;
            var pitch = a.OnGround ? a.Speed < 58 ? 0 : 0.45 : Math.Clamp((0.2 - a.Pitch) * 3, -0.5, 0.5);
            simulation.Advance(FlightScenarios.Step, new FlightInput(0, pitch, 0, 1, false, false));
            if (liftoff is null && !simulation.Snapshot.Aircraft.OnGround)
                liftoff = simulation.Snapshot;
        }
        var snapshot = simulation.Snapshot;
        Check.That(liftoff is not null, $"Dry-thrust runway rotation actually lifts off: {FlightScenarios.Describe(snapshot)}");
        Check.That(liftoff!.Aircraft.Z < 1000 && liftoff.Aircraft.Speed > 58,
            $"Takeoff has runway remaining and useful flying speed: {FlightScenarios.Describe(liftoff)}");
        Check.That(snapshot.Phase == "flying" && !snapshot.Aircraft.OnGround &&
                   snapshot.Aircraft.Y > 100 && snapshot.Aircraft.VerticalSpeed > 3 &&
                   snapshot.Aircraft.Z > simulation.World.RunwayStart.Z + 1000 &&
                   snapshot.Aircraft.Fuel < 1,
            $"Throttle and pull-up produce an actual powered climb: {FlightScenarios.Describe(snapshot)}");
        FlightScenarios.Pose(snapshot.Aircraft, "Runway climb");
        Console.WriteLine($"TRACE takeoff {FlightScenarios.Describe(liftoff)}");
        Console.WriteLine($"TRACE climb {FlightScenarios.Describe(snapshot)}");
    }

    public static void ControlSigns()
    {
        static AircraftSnapshot Probe(FlightInput input)
        {
            var simulation = FlightScenarios.AirStart();
            FlightScenarios.Advance(simulation, 0.5, input);
            return simulation.Snapshot.Aircraft;
        }
        var rightRoll = Probe(new FlightInput(0.6, 0, 0, 0.5, false, false));
        var leftRoll = Probe(new FlightInput(-0.6, 0, 0, 0.5, false, false));
        Check.That(rightRoll.Roll < -0.1 && rightRoll.Up.X > 0.1 &&
                   leftRoll.Roll > 0.1 && leftRoll.Up.X < -0.1,
            "Positive roll banks RIGHT, lowering the right wing (negative render Roll)");
        var rightYaw = Probe(new FlightInput(0, 0, 0.8, 0.5, false, false));
        var leftYaw = Probe(new FlightInput(0, 0, -0.8, 0.5, false, false));
        Check.That(rightYaw.Yaw > 0.05 && rightYaw.Forward.X > 0.05 &&
                   leftYaw.Yaw < -0.05 && leftYaw.Forward.X < -0.05,
            "Positive yaw points the nose RIGHT");
        var pull = Probe(new FlightInput(0, 0.5, 0, 0.5, false, false));
        var push = Probe(new FlightInput(0, -0.5, 0, 0.5, false, false));
        Check.That(pull.Pitch > 0.1 && pull.Forward.Y > push.Forward.Y + 0.1 &&
                   pull.VerticalSpeed > push.VerticalSpeed + 2,
            "Positive pitch pulls UP and changes the flight path, not only the mesh");
        foreach (var a in new[] { rightRoll, leftRoll, rightYaw, leftYaw, pull, push })
            FlightScenarios.Pose(a, "Control sign probe");
    }

    public static void BankedTurnsBothDirections()
    {
        static FlightSnapshot Turn(double direction)
        {
            var simulation = FlightScenarios.AirStart();
            simulation.Command("gear-toggle");
            FlightScenarios.Advance(simulation, 1.5, new FlightInput(direction * 0.35, 0, 0, 0.7, false, false));
            FlightScenarios.Advance(simulation, 8.5, new FlightInput(0, 0, 0, 0.7, false, false));
            return simulation.Snapshot;
        }
        var right = Turn(1);
        var left = Turn(-1);
        Check.That(right.Phase == "flying" && left.Phase == "flying" &&
                   right.Aircraft.X > 30 && left.Aircraft.X < -30 &&
                   right.Aircraft.Yaw > 0.05 && left.Aircraft.Yaw < -0.05,
            $"Banks redirect lift into meaningful signed turns: right {FlightScenarios.Describe(right)}; " +
            $"left {FlightScenarios.Describe(left)}");
        Check.Near(right.Aircraft.X, -left.Aircraft.X, 1e-8, "Mirrored turn displacement");
        Check.Near(right.Aircraft.Yaw, -left.Aircraft.Yaw, 1e-8, "Mirrored turn heading");
        Console.WriteLine($"TRACE right turn {FlightScenarios.Describe(right)}");
        Console.WriteLine($"TRACE left turn {FlightScenarios.Describe(left)}");
    }

    public static void StallAndDescent()
    {
        var simulation = FlightScenarios.AirStart();
        var initialSpeed = simulation.Snapshot.Aircraft.Speed;
        var sawStall = false;
        var sawDescendingStall = false;
        var minimumSpeed = initialSpeed;
        FlightSnapshot? descending = null;
        for (var i = 0; i < 120 * 20 && simulation.Snapshot.Phase == "flying"; i++)
        {
            simulation.Advance(FlightScenarios.Step, new FlightInput(0, 0.28, 0, 0, false, true));
            var snapshot = simulation.Snapshot;
            var a = snapshot.Aircraft;
            minimumSpeed = Math.Min(minimumSpeed, a.Speed);
            sawStall |= a.Stalled;
            if (a.Stalled && a.VerticalSpeed < -5)
            {
                sawDescendingStall = true;
                descending ??= snapshot;
            }
        }
        Check.That(sawStall && sawDescendingStall && minimumSpeed < initialSpeed - 20,
            $"Low energy with airbrake/pull-up loses speed and descends, never hovers: " +
            $"minSpeed={minimumSpeed:F3} {FlightScenarios.Describe(simulation.Snapshot)}");
        Console.WriteLine($"TRACE descending stall {FlightScenarios.Describe(descending!)} minSpeed={minimumSpeed:F3}");
    }

    public static void AfterburnerFuelGearAndBrake()
    {
        var dry = FlightScenarios.AirStart();
        var wet = FlightScenarios.AirStart();
        FlightScenarios.Advance(dry, 3, new FlightInput(0, 0, 0, 1, false, false));
        FlightScenarios.Advance(wet, 3, new FlightInput(0, 0, 0, 1, true, false));
        Check.That(wet.Snapshot.Aircraft.Afterburner && !dry.Snapshot.Aircraft.Afterburner &&
                   wet.Snapshot.Aircraft.Speed > dry.Snapshot.Aircraft.Speed + 4 &&
                   wet.Snapshot.Aircraft.Fuel < dry.Snapshot.Aircraft.Fuel - 0.002,
            $"Afterburner trades real fuel for thrust: dry {FlightScenarios.Describe(dry.Snapshot)}; " +
            $"wet {FlightScenarios.Describe(wet.Snapshot)}");
        var lowThrottle = FlightScenarios.AirStart();
        FlightScenarios.Advance(lowThrottle, 2, new FlightInput(0, 0, 0, 0.4, true, false));
        Check.That(!lowThrottle.Snapshot.Aircraft.Afterburner, "Afterburner is gated by high actual throttle");

        var gearDown = FlightScenarios.AirStart();
        var gearUp = FlightScenarios.AirStart();
        var brake = FlightScenarios.AirStart();
        gearUp.Command("gear-toggle");
        var cruise = new FlightInput(0, 0, 0, 0.3, false, false);
        FlightScenarios.Advance(gearDown, 5, cruise);
        FlightScenarios.Advance(gearUp, 5, cruise);
        FlightScenarios.Advance(brake, 5, cruise with { Brake = true });
        Check.That(gearUp.Snapshot.Aircraft.Speed > gearDown.Snapshot.Aircraft.Speed + 1 &&
                   brake.Snapshot.Aircraft.Speed < gearDown.Snapshot.Aircraft.Speed - 5,
            "Retracting gear reduces drag and airborne brake increases drag");
        Console.WriteLine($"TRACE dry {FlightScenarios.Describe(dry.Snapshot)}");
        Console.WriteLine($"TRACE afterburner {FlightScenarios.Describe(wet.Snapshot)}");
    }

    public static void GentleLandingAndBraking()
    {
        var approach = FlightScenarios.Approach();
        var simulation = approach.Simulation;
        var touchdown = simulation.Snapshot;
        Console.WriteLine($"TRACE approach before contact speed={approach.BeforeContact.Speed:F3} " +
                          $"vs={approach.BeforeContact.VerticalSpeed:F3} z={approach.BeforeContact.Z:F2}");
        Check.That(touchdown.Phase == "flying" && touchdown.Aircraft.OnGround &&
                   touchdown.Landings == 1 && touchdown.Crashes == 0 &&
                   approach.BeforeContact.VerticalSpeed is > -4.5 and < -0.1 &&
                   simulation.World.Sample(touchdown.Aircraft.X, touchdown.Aircraft.Z).LandingSurface is not null,
            $"Public-input approach and flare gently land on the canonical runway: " +
            FlightScenarios.Describe(touchdown));
        FlightScenarios.Advance(simulation, 20, new FlightInput(0, 0, 0, 0, false, true));
        Check.That(simulation.Snapshot.Phase == "flying" && simulation.Snapshot.Aircraft.OnGround &&
                   simulation.Snapshot.Aircraft.Speed < 0.1 && simulation.Snapshot.Landings == 1,
            $"Ground brakes stop the jet without recounting landings: {FlightScenarios.Describe(simulation.Snapshot)}");
        Console.WriteLine($"TRACE gentle touchdown {FlightScenarios.Describe(touchdown)}");
        Console.WriteLine($"TRACE stopped {FlightScenarios.Describe(simulation.Snapshot)}");
    }

    public static void HardAndGearUpLanding()
    {
        var hard = FlightScenarios.Approach(flare: false);
        var hardSnapshot = hard.Simulation.Snapshot;
        Check.That(hardSnapshot.Phase == "crashed" && hardSnapshot.Crashes == 1 &&
                   hardSnapshot.Landings == 0 && hard.BeforeContact.VerticalSpeed < -4.5 &&
                   hardSnapshot.Message.StartsWith("Hard landing", StringComparison.Ordinal),
            $"A public-input runway approach with no flare actually crashes: {FlightScenarios.Describe(hardSnapshot)}");
        var gearUp = FlightScenarios.Approach(gearDown: false);
        var gearSnapshot = gearUp.Simulation.Snapshot;
        Check.That(gearSnapshot.Phase == "crashed" && gearSnapshot.Crashes == 1 &&
                   gearSnapshot.Landings == 0 && !gearSnapshot.Aircraft.GearDown &&
                   gearSnapshot.Message.StartsWith("Gear-up landing", StringComparison.Ordinal),
            $"An otherwise controlled approach with retracted gear actually crashes: " +
            FlightScenarios.Describe(gearSnapshot));
        Console.WriteLine($"TRACE hard touchdown {FlightScenarios.Describe(hardSnapshot)} " +
                          $"preSink={hard.BeforeContact.VerticalSpeed:F3}");
        Console.WriteLine($"TRACE gear-up touchdown {FlightScenarios.Describe(gearSnapshot)} " +
                          $"preSink={gearUp.BeforeContact.VerticalSpeed:F3}");
    }

    public static void FullRollLoopAndLongPose()
    {
        static FlightSimulation ClimbForManeuvers()
        {
            var simulation = FlightScenarios.AirStart();
            simulation.Command("gear-toggle");
            for (var i = 0; i < 120 * 30; i++)
                simulation.Advance(FlightScenarios.Step, FlightScenarios.Climb(simulation.Snapshot.Aircraft, 0.2));
            Check.That(simulation.Snapshot.Phase == "flying" && simulation.Snapshot.Aircraft.Y > 700,
                $"Public-input climb supplies maneuver height: {FlightScenarios.Describe(simulation.Snapshot)}");
            return simulation;
        }

        var roll = ClimbForManeuvers();
        var sawInvertedRoll = false;
        var sawRollReturn = false;
        for (var i = 0; i < 120 * 6; i++)
        {
            roll.Advance(FlightScenarios.Step, new FlightInput(1, 0, 0, 1, true, false));
            var a = roll.Snapshot.Aircraft;
            FlightScenarios.Pose(a, $"Full roll step {i}");
            sawInvertedRoll |= a.Up.Y < -0.7;
            sawRollReturn |= sawInvertedRoll && a.Up.Y > 0.7;
        }
        Check.That(roll.Snapshot.Phase == "flying" && sawInvertedRoll && sawRollReturn,
            $"Full roll passes inverted and returns upright with a stable basis: {FlightScenarios.Describe(roll.Snapshot)}");

        var loop = ClimbForManeuvers();
        var sawBackward = false;
        var sawDown = false;
        var completedLoop = false;
        for (var i = 0; i < 120 * 35 && loop.Snapshot.Phase == "flying"; i++)
        {
            loop.Advance(FlightScenarios.Step, new FlightInput(0, 0.65, 0, 1, true, false));
            var a = loop.Snapshot.Aircraft;
            FlightScenarios.Pose(a, $"Full loop step {i}");
            sawBackward |= a.Forward.Z < -0.7;
            sawDown |= sawBackward && a.Forward.Y < -0.6;
            completedLoop |= sawDown && a.Forward.Z > 0.7 && a.Up.Y > 0.6;
            if (completedLoop)
                break;
        }
        Check.That(loop.Snapshot.Phase == "flying" && completedLoop,
            $"Full pitch loop crosses vertical/inverted/downward and returns forward: " +
            $"backward={sawBackward} down={sawDown} {FlightScenarios.Describe(loop.Snapshot)}");

        // Continue a long public-input banked flight instead of only checking frozen crash data.
        var longFlight = ClimbForManeuvers();
        var activeSteps = 0;
        for (var i = 0; i < 120 * 180 && longFlight.Snapshot.Phase == "flying"; i++)
        {
            var a = longFlight.Snapshot.Aircraft;
            var targetVs = Math.Clamp((1500 - a.Y) * 0.02, -15, 15);
            var targetPitch = Math.Asin(Math.Clamp(targetVs / Math.Max(60, a.Speed), -0.3, 0.3)) + 0.03;
            var rollInput = Math.Clamp((a.Roll + 0.45) * 1.1, -0.6, 0.6);
            var pitchInput = Math.Clamp((targetPitch - a.Pitch) * 3, -0.5, 0.5);
            longFlight.Advance(FlightScenarios.Step, new FlightInput(rollInput, pitchInput, 0, 0.6, false, false));
            FlightScenarios.Pose(longFlight.Snapshot.Aircraft, $"Long maneuver step {i}");
            activeSteps++;
        }
        Check.That(activeSteps == 120 * 180 && longFlight.Snapshot.Phase == "flying",
            $"180 active seconds of finite bounded maneuvering: {FlightScenarios.Describe(longFlight.Snapshot)}");
        Console.WriteLine($"TRACE full roll {FlightScenarios.Describe(roll.Snapshot)}");
        Console.WriteLine($"TRACE full loop {FlightScenarios.Describe(loop.Snapshot)}");
        Console.WriteLine($"TRACE long flight {FlightScenarios.Describe(longFlight.Snapshot)}");
    }
}
