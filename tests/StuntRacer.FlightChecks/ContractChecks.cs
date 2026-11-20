using System.Text.Json;
using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class ContractChecks
{
    public static void WorldAndJson()
    {
        var simulation = new FlightSimulation();
        var world = simulation.World;
        var aircraft = simulation.Snapshot.Aircraft;
        Check.That(world.Name == "Interceptor Bay" && world.SeaHeight == 0, "Authored bay world");
        Check.That(world.Sample(aircraft.X, aircraft.Z).LandingSurface?.Name == "Bay Runway",
            "Default runway spawn belongs to the actual landable rectangle");
        Check.Near(aircraft.Y, world.TerrainHeightAt(aircraft.X, aircraft.Z) + world.GearClearance,
            1e-12, "Ground aircraft agrees with canonical terrain");
        Check.Near(world.TerrainHeightAt(100, 0), 8, 0, "Island height outside runway");
        Check.That(world.Sample(100, 0).LandingSurface is null && !world.Sample(100, 0).IsWater,
            "Grass is terrain, not a landing surface");
        Check.That(world.Sample(0, -3001).IsWater && world.Sample(2001, 0).IsWater,
            "Beyond island bounds is sea");
        Check.That(world.Sample(40, 1500).LandingSurface is not null &&
                   world.Sample(40.01, 1500).LandingSurface is null, "Runway rectangle edges");
        var remaining = world.LandingSurfaces[0].Length / 2 - aircraft.Z;
        Check.That(remaining > 2500, "At least 2500 m runway remains at start");

        var rotated = world with
        {
            LandingSurfaces = new[] { new LandingSurface("Rotated", 0, 0, 20, 200, 8, Math.PI / 2) }
        };
        Check.That(rotated.Sample(80, 0).LandingSurface?.Name == "Rotated" &&
                   rotated.Sample(0, 80).LandingSurface is null,
            "Positive rectangle heading rotates +Z to +X");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var snapshotJson = JsonDocument.Parse(JsonSerializer.Serialize(simulation.Snapshot, options));
        var jsonAircraft = snapshotJson.RootElement.GetProperty("aircraft");
        Check.That(jsonAircraft.GetProperty("forward").EnumerateObject().Count() == 3 &&
                   jsonAircraft.GetProperty("up").GetProperty("y").GetDouble() == 1,
            "Vector data uses serializable X/Y/Z properties, not omitted Numerics fields");
        using var worldJson = JsonDocument.Parse(JsonSerializer.Serialize(world, options));
        Check.That(worldJson.RootElement.GetProperty("runwayStart").GetProperty("z").GetDouble() == -1300 &&
                   worldJson.RootElement.GetProperty("landingSurfaces")[0].GetProperty("length").GetDouble() == 3000,
            "Published renderer world schema is present");
    }

    public static void InvalidAndClampedInputs()
    {
        var simulation = new FlightSimulation();
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check.Throws<ArgumentOutOfRangeException>(() => simulation.Advance(invalid, FlightScenarios.Idle),
                "Non-finite elapsed seconds are rejected even while ready");
            FlightInput[] inputs =
            [
                new(invalid, 0, 0, 0, false, false),
                new(0, invalid, 0, 0, false, false),
                new(0, 0, invalid, 0, false, false),
                new(0, 0, 0, invalid, false, false)
            ];
            foreach (var input in inputs)
                Check.Throws<ArgumentOutOfRangeException>(() => simulation.Advance(0, input),
                    "Every non-finite control axis is rejected even at zero delta");
            Check.Throws<ArgumentOutOfRangeException>(() => simulation.World.Sample(invalid, 0),
                "World rejects invalid X");
            Check.Throws<ArgumentOutOfRangeException>(() => simulation.World.Sample(0, invalid),
                "World rejects invalid Z");
        }
        Check.Throws<ArgumentOutOfRangeException>(() => simulation.Advance(-0.1, FlightScenarios.Idle),
            "Negative elapsed time is rejected");
        Check.Throws<ArgumentNullException>(() => simulation.Advance(0, null!), "Null controls rejected");
        Check.Throws<ArgumentException>(() => simulation.Command("not-a-flight-command"), "Unknown command rejected");

        var excessive = FlightScenarios.AirStart();
        var normalized = FlightScenarios.AirStart();
        FlightScenarios.Advance(excessive, 0.5,
            new FlightInput(double.MaxValue, -double.MaxValue, double.MaxValue, double.MaxValue, true, false));
        FlightScenarios.Advance(normalized, 0.5, new FlightInput(1, -1, 1, 1, true, false));
        Check.That(excessive.Snapshot == normalized.Snapshot, "Finite excessive controls clamp, never wrap");

        excessive.Command("pause");
        Check.Throws<ArgumentOutOfRangeException>(() => excessive.Advance(double.NaN, FlightScenarios.Idle),
            "Paused mode still rejects invalid delta");
        Check.Throws<ArgumentOutOfRangeException>(() => excessive.Advance(0,
            new FlightInput(0, 0, 0, double.NaN, false, false)), "Paused mode still rejects invalid controls");
    }

    public static void FixedStepSchedules()
    {
        var input = new FlightInput(0.04, 0.02, 0.015, 0.7, false, false);
        FlightSnapshot? baseline = null;
        foreach (var rate in new[] { 60, 120, 144 })
        {
            var simulation = FlightScenarios.AirStart();
            for (var frame = 0; frame < rate * 8; frame++)
                simulation.Advance(1.0 / rate, input);
            var snapshot = simulation.Snapshot;
            Check.That(snapshot.Tick == 960 && snapshot.Phase == "flying", $"{rate} Hz advances 960 actual ticks");
            FlightScenarios.Pose(snapshot.Aircraft, $"{rate} Hz");
            if (baseline is not null)
            {
                Check.That(snapshot.Aircraft == baseline.Aircraft, $"{rate} Hz has identical physical state");
                Check.Near(snapshot.Time, baseline.Time, 0, $"{rate} Hz simulated time");
            }
            baseline = snapshot;
            Console.WriteLine($"TRACE {rate}Hz {FlightScenarios.Describe(snapshot)}");
        }
    }

    public static void StallBudget()
    {
        var stalledFrame = FlightScenarios.AirStart();
        var cappedFrame = FlightScenarios.AirStart();
        var input = new FlightInput(0, 0.05, 0, 0.7, false, false);
        stalledFrame.Advance(9, input);
        cappedFrame.Advance(0.25, input);
        Check.That(stalledFrame.Snapshot.Tick == 30 &&
                   stalledFrame.Snapshot.Aircraft == cappedFrame.Snapshot.Aircraft,
            "A nine-second frame accepts only .25 s / 30 fixed steps");
        var before = stalledFrame.Snapshot;
        stalledFrame.Advance(0, input);
        Check.That(stalledFrame.Snapshot == before, "Dropped stall time is not banked for catch-up");
        stalledFrame.Advance(FlightScenarios.Step, input);
        Check.That(stalledFrame.Snapshot.Tick == 31, "Subsequent frame only advances its own fixed step");
    }

    public static void Lifecycle()
    {
        var simulation = new FlightSimulation();
        var ready = simulation.Snapshot;
        simulation.Advance(20, new FlightInput(1, 1, 1, 1, true, true));
        Check.That(simulation.Snapshot == ready, "Ready phase freezes aircraft, fuel and clock");
        simulation.Command("start");
        FlightScenarios.Advance(simulation, 1, FlightScenarios.Idle);
        simulation.Command("pause");
        var pausedCountdown = simulation.Snapshot;
        simulation.Advance(12, FlightScenarios.Idle);
        Check.That(simulation.Snapshot == pausedCountdown, "Paused countdown freezes clock/countdown");
        simulation.Command("resume");
        FlightScenarios.Advance(simulation, 2, FlightScenarios.Idle);
        Check.That(simulation.Snapshot.Phase == "flying" && simulation.Snapshot.Aircraft.OnGround &&
                   simulation.Snapshot.Tick == 360 && simulation.Snapshot.Countdown == 0,
            "Exactly three simulated seconds release countdown onto the runway");
        FlightScenarios.Advance(simulation, 1, FlightScenarios.Idle);
        Check.That(simulation.Snapshot.Phase == "flying" && simulation.Snapshot.Aircraft.OnGround &&
                   simulation.Snapshot.Aircraft.Speed == 0, "Neutral ground flight remains stationary and flying");
        simulation.Command("pause");
        var paused = simulation.Snapshot;
        simulation.Command("gear-toggle");
        simulation.Advance(50, new FlightInput(1, 1, 1, 1, true, true));
        Check.That(simulation.Snapshot == paused, "Paused aircraft, gear, fuel, counters and accumulator freeze");
        simulation.Command("air-start");
        Check.That(simulation.Snapshot.Phase == "flying" && !simulation.Snapshot.Aircraft.OnGround,
            "Explicit training air-start works from paused");
        Check.Near(simulation.Snapshot.Aircraft.Y, 258, 0, "Explicit training altitude");
        simulation.Command("gear-toggle");
        Check.That(!simulation.Snapshot.Aircraft.GearDown, "Gear retracts");
        simulation.Command("gear-toggle");
        Check.That(simulation.Snapshot.Aircraft.GearDown, "Gear extends");

        FlightScenarios.Advance(simulation, 12, new FlightInput(0, -0.8, 0, 0, false, true));
        var crash = simulation.Snapshot;
        Check.That(crash.Phase == "crashed" && crash.Crashes == 1 &&
                   crash.Message.StartsWith("Water impact", StringComparison.Ordinal),
            $"Nose-down public inputs produce a water crash: {FlightScenarios.Describe(crash)}");
        simulation.Advance(80, new FlightInput(0, 1, 0, 1, true, false));
        Check.That(simulation.Snapshot == crash, "Crash stays frozen until an explicit command");
        simulation.Command("recover");
        var recovered = simulation.Snapshot;
        Check.That(recovered.Phase == "ready" && recovered.Recoveries == 1 && recovered.Crashes == 1 &&
                   recovered.Aircraft.OnGround && recovered.Aircraft.GearDown, "Explicit recovery preserves counters");
        Check.Near(recovered.Aircraft.Fuel, crash.Aircraft.Fuel, 0, "Recovery cannot silently refuel");
        Check.Near(recovered.Aircraft.Z, simulation.World.RunwayStart.Z, 0, "Recovery uses canonical spawn");
        Check.That(recovered.Tick == crash.Tick && recovered.Time == crash.Time, "Recovery retains mission clock");
        simulation.Command("restart");
        Check.That(simulation.Snapshot.Phase == "countdown" && simulation.Snapshot.Countdown == 3 &&
                   simulation.Snapshot.Tick == 0 && simulation.Snapshot.Time == 0 &&
                   simulation.Snapshot.Crashes == 0 && simulation.Snapshot.Recoveries == 0 &&
                   simulation.Snapshot.Aircraft.Fuel == 1, "Restart resets the mission and starts a fresh countdown");
        Console.WriteLine($"TRACE crash {FlightScenarios.Describe(crash)}");
    }
}
