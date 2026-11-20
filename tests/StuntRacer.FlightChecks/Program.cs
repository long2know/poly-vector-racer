using StuntRacer.FlightChecks;

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
    ("low-energy stall and descent", DynamicsChecks.StallAndDescent),
    ("afterburner/fuel/gear/airbrake", DynamicsChecks.AfterburnerFuelGearAndBrake),
    ("actual gentle landing and ground braking", DynamicsChecks.GentleLandingAndBraking),
    ("actual hard and gear-up runway crashes", DynamicsChecks.HardAndGearUpLanding),
    ("full roll/loop and long finite pose", DynamicsChecks.FullRollLoopAndLongPose),
    ("geographic terrain triangle and fallback alignment", GeographicChecks.MeshAlignment),
    ("geographic aircraft contacts and missing data wait", GeographicChecks.Contacts),
    ("geographic afterburner produces supersonic flight", GeographicChecks.SupersonicPerformance)
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
