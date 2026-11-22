using StuntRacer.FlightChecks;

if (args is ["--atlas-endurance", var baseArchive])
{
    try
    {
        AtlasAutopilotChecks.ActualAtlas(baseArchive);
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL Atlas endurance: {exception.Message}");
        return 1;
    }
}
if (args.Contains("--atlas-endurance", StringComparer.Ordinal))
{
    Console.Error.WriteLine("Usage: --atlas-endurance /path/to/base.zip-or-extracted-base.bin");
    return 1;
}

(string Name, Action Run)[] checks =
[
    ("world and JSON contract", ContractChecks.WorldAndJson),
    ("finite validation and clamps", ContractChecks.InvalidAndClampedInputs),
    ("60/120/144 Hz fixed-step schedules", ContractChecks.FixedStepSchedules),
    (".25 second frame-stall budget", ContractChecks.StallBudget),
    ("pause/restart/crash/recover lifecycle", ContractChecks.Lifecycle),
    ("runway takeoff and climb", DynamicsChecks.RunwayTakeoffAndClimb),
    ("roll/yaw/pitch input signs", DynamicsChecks.ControlSigns),
    ("meaningful banked turns both ways", DynamicsChecks.BankedTurnsBothDirections),
    ("assisted Bay keyboard turns reverse the track and level off", AssistedFlightChecks.BayReversal),
    ("assisted Atlas scenic flight reverses by ground track", AssistedFlightChecks.WorldReversal),
    ("assisted World Flight arcs widen at supersonic speed", AssistedFlightChecks.SupersonicArc),
    ("low-energy stall and descent", DynamicsChecks.StallAndDescent),
    ("afterburner/fuel/gear/airbrake", DynamicsChecks.AfterburnerFuelGearAndBrake),
    ("actual gentle landing and ground braking", DynamicsChecks.GentleLandingAndBraking),
    ("actual hard and gear-up runway crashes", DynamicsChecks.HardAndGearUpLanding),
    ("full roll/loop and long finite pose", DynamicsChecks.FullRollLoopAndLongPose),
    ("training flight scans the moving drone and the ground beacon", TrainingChecks.ScanBothProps),
    ("training flight pause, restart and garage lifecycle", TrainingChecks.PauseAndReplay),
    ("air tag duel checks proximity, new passes, cooldown and pause", DuelChecks.ProximityAndPause),
    ("air tag duel stops at its actual deadline and resets", DuelChecks.DeadlineAndReplay),
    ("air tag duel can be won by flying three passes", DuelChecks.ThreePassClear),
    ("air tag duel state is isolated from other flight sessions", DuelChecks.IsolatedActivity),
    ("geographic terrain triangle and fallback alignment", GeographicChecks.MeshAlignment),
    ("geographic aircraft contacts and missing data wait", GeographicChecks.Contacts),
    ("geographic afterburner produces supersonic flight", GeographicChecks.SupersonicPerformance),
    ("atlas WFA1 triangle, wrapping and both polar rows", AtlasChecks.FallbackAndWorldBounds),
    ("atlas signed detail activates and retires as complete hierarchies", AtlasChecks.DetailActivation),
    ("atlas high-mountain departure clears peaks understated by the fallback", AtlasChecks.DepartureClearance),
    ("atlas flight crosses the date line and departs both poles without waiting", AtlasChecks.ContinuousFlight),
    ("atlas autopilot command validation and JSON contract", AtlasAutopilotChecks.CommandsAndWire),
    ("atlas autopilot fixed-state compression and manual takeover", AtlasAutopilotChecks.FixedStepsAndManual),
    ("atlas autopilot pause, lifecycle and dry fuel accounting", AtlasAutopilotChecks.PauseAndFuel),
    ("atlas autopilot heading/altitude/speed holds and installed terrain clearance", AtlasAutopilotChecks.HoldAndTerrain),
    ("atlas autopilot low fuel limits compression, no manual fuel bonus", AtlasAutopilotChecks.LowFuel),
    ("atlas autopilot date-line route, 1x capture and onward flight", AtlasAutopilotChecks.CaptureAndDateLine),
    ("atlas autopilot polar hold remains tangent to bounded world", AtlasAutopilotChecks.PolarHold),
    ("atlas autopilot approach captures near each pole and continues safely", AtlasAutopilotChecks.PolarRoute)
];

var failed = 0;
var executed = 0;
foreach (var (name, run) in checks)
{
    if (args.Length > 0 && !args.Any(filter => name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        continue;
    executed++;
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}
Console.WriteLine($"Flight checks: {executed - failed}/{executed} passed.");
return failed == 0 && executed > 0 ? 0 : 1;
