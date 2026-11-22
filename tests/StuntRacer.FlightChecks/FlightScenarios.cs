using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class FlightScenarios
{
    public const double Step = 1.0 / 120.0;
    public static FlightInput Idle => new(0, 0, 0, 0, false, false);

    public static FlightSimulation AirStart()
    {
        var simulation = new FlightSimulation();
        simulation.Command("air-start");
        return simulation;
    }

    public static void Advance(FlightSimulation simulation, double seconds, FlightInput input)
    {
        var steps = (int)Math.Round(seconds / Step);
        for (var i = 0; i < steps; i++)
            simulation.Advance(Step, input);
    }

    public static FlightInput Climb(AircraftSnapshot aircraft, double pitch = 0.24)
    {
        return new FlightInput(
            0, Math.Clamp((pitch - aircraft.Pitch) * 3, -0.5, 0.65),
            0, 1, true, false);
    }

    public static ApproachResult Approach(bool gearDown = true, bool flare = true, bool assisted = false)
    {
        var simulation = AirStart();
        if (!gearDown)
            simulation.Command("gear-toggle");
        var runway = simulation.World.LandingSurfaces[0];
        var previous = simulation.Snapshot.Aircraft;
        var lowestApproachSink = 0.0;
        for (var i = 0; i < 120 * 100; i++)
        {
            var aircraft = simulation.Snapshot.Aircraft;
            if (aircraft.OnGround || aircraft.Crashed)
                return new ApproachResult(simulation, previous, lowestApproachSink);
            var height = aircraft.Y - runway.Height - simulation.World.GearClearance;
            var wantedSink = flare && height < 28 ? -1.8 : flare ? -6.5 : -8;
            var trim = Math.Clamp(0.06 + (95 - aircraft.Speed) * 0.0028, 0.03, 0.18);
            var pathPitch = Math.Asin(Math.Clamp(wantedSink / Math.Max(55, aircraft.Speed), -0.3, 0.3));
            var targetPitch = pathPitch + trim +
                              Math.Clamp((wantedSink - aircraft.VerticalSpeed) * 0.018, -0.08, 0.08);
            var pitchInput = Math.Clamp(
                (targetPitch - aircraft.Pitch) * 3.2 + (trim - 0.06) * 1.25, -0.65, 0.65);
            var throttle = Math.Clamp(0.2 + (83 - aircraft.Speed) * 0.025, 0, 0.55);
            previous = aircraft;
            lowestApproachSink = Math.Min(lowestApproachSink, aircraft.VerticalSpeed);
            simulation.Advance(Step, new FlightInput(0, pitchInput, 0, throttle, false, aircraft.Speed > 87) { Assisted = assisted });
        }
        return new ApproachResult(simulation, previous, lowestApproachSink);
    }

    public static string Describe(FlightSnapshot snapshot)
    {
        var a = snapshot.Aircraft;
        return $"phase={snapshot.Phase} tick={snapshot.Tick} t={snapshot.Time:F3} " +
               $"xyz=({a.X:F2},{a.Y:F2},{a.Z:F2}) speed={a.Speed:F3} vs={a.VerticalSpeed:F3} " +
               $"ypr=({a.Yaw:F4},{a.Pitch:F4},{a.Roll:F4}) fuel={a.Fuel:F6} " +
               $"ground={a.OnGround} stall={a.Stalled} landings={snapshot.Landings} " +
               $"crashes={snapshot.Crashes} message={snapshot.Message}";
    }

    public static void Pose(AircraftSnapshot a, string context)
    {
        double[] numbers =
        [
            a.X, a.Y, a.Z, a.Yaw, a.Pitch, a.Roll, a.Speed, a.VerticalSpeed,
            a.Throttle, a.Fuel, a.GForce,
            a.Forward.X, a.Forward.Y, a.Forward.Z, a.Up.X, a.Up.Y, a.Up.Z
        ];
        Check.That(numbers.All(double.IsFinite), $"{context}: all snapshot numbers finite");
        Check.Near(Dot(a.Forward, a.Forward), 1, 2e-12, $"{context}: normalized Forward");
        Check.Near(Dot(a.Up, a.Up), 1, 2e-12, $"{context}: normalized Up");
        Check.Near(Dot(a.Forward, a.Up), 0, 2e-12, $"{context}: perpendicular Forward/Up");
        Check.That(a.Speed <= 450.000001 && a.Fuel is >= 0 and <= 1 &&
                   a.Throttle is >= 0 and <= 1 && Math.Abs(a.X) <= 200_000 &&
                   Math.Abs(a.Z) <= 200_000 && a.Y is >= 0 and <= 20_000,
            $"{context}: numerical safety envelope");
    }

    private static double Dot(FlightVector a, FlightVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    internal sealed record ApproachResult(
        FlightSimulation Simulation, AircraftSnapshot BeforeContact, double LowestSink);
}
