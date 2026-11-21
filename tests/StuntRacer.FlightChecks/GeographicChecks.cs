using System.Buffers.Binary;
using System.Text.Json;
using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class GeographicChecks
{
    private static byte[] Tile(int level, int x, int z, Func<int, int, short> height, byte water = 0)
    {
        var buffer = new byte[WorldFlightElevation.TileLength];
        "WFT1"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), level);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), x);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), z);
        for (var j = 0; j < WorldFlightElevation.Side; j++)
        for (var i = 0; i < WorldFlightElevation.Side; i++)
        {
            var sample = j * WorldFlightElevation.Side + i;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(16 + sample * 2), height(i, j));
            buffer[16 + WorldFlightElevation.Side * WorldFlightElevation.Side * 2 + sample] = water;
        }
        return buffer;
    }

    public static void MeshAlignment()
    {
        var ground = new WorldFlightElevation();
        ground.AddTile(Tile(0, -1, 0, (_, _) => 1025));
        ground.AddTile(Tile(8, -1, 0, (i, j) => (short)(i == 31 && j == 0 ? 1110
            : i == 30 && j == 1 ? 1120 : i == 31 && j == 1 ? 1380 : 1100)));
        Check.Near(ground.Sample(-56, 24)!.Value.Height, 1180, 0, "upper triangle samples the same SW to NE diagonal as the GPU");
        Check.Near(ground.Sample(-40, 8)!.Value.Height, 1175, 0, "lower triangle samples the same altitude as the GPU");
        ground.RemoveTile(8, -1, 0);
        Check.Near(ground.Sample(-56, 24)!.Value.Height, 1025, 0, "coarse tile supports physical contacts before detail arrives");
        ground.AddTile(Tile(8, 0, -1, (_, _) => 12, 2));
        Check.That(ground.Sample(1, -3) is { Height: 12, IsWater: true }, "lakes retain their elevation, not sea level");
        Check.That(ground.Sample(400000, 30) is null, "unloaded terrain is not invented");
        Check.Throws<InvalidDataException>(() => ground.AddTile(8, 0, -1, Tile(8, 0, 0, (_, _) => 0)),
            "tile address spoofing is rejected");
        Check.Throws<InvalidDataException>(() => ground.AddTile(Tile(8, 0, 0, (_, _) => 0, 3)),
            "unknown water codes are rejected");
    }

    public static void Contacts()
    {
        var ground = new WorldFlightElevation();
        var destination = new WorldFlightDestination("mountain", "Mountain", 1100, 1100, 1800, 0, 1100, 1700);
        var low = Tile(5, 0, 0, (_, _) => 380);
        Check.Throws<InvalidDataException>(() => FlightWorldDefinition.CreateGeographic("Test", destination, null, ground),
            "cannot enter geographic flight before fetching terrain");
        ground.AddTile(low);
        var world = FlightWorldDefinition.CreateGeographic("Test", destination, null, ground);
        var session = new FlightSimulation(world);
        session.Command("air-start");
        session.Advance(.1, FlightScenarios.Idle);
        var before = session.Snapshot;
        ground.RemoveTile(5, 0, 0);
        session.Advance(.25, FlightScenarios.Idle);
        Check.That(session.TerrainWaiting && before.Aircraft == session.Snapshot.Aircraft &&
            before.Tick == session.Snapshot.Tick, "missing tiles must hold flight rather than letting it cross invented terrain");
        ground.AddTile(Tile(5, 0, 0, (_, _) => 2100));
        session.Advance(.1, FlightScenarios.Idle);
        Check.That(session.Snapshot.Phase == "crashed" && !session.TerrainWaiting &&
            session.Snapshot.Message.Contains("Terrain impact", StringComparison.Ordinal), "a loaded mountain changes actual flight contacts");
        Check.Near(session.Snapshot.Aircraft.Y, 2101, 0, "impacts resolve to the terrain surface");
    }

    public static void SupersonicPerformance()
    {
        var coast = new WorldFlightElevation();
        coast.AddTile(Tile(0, 0, 0, (_, _) => 0));
        FlightWorldDefinition WorldAt(double altitude)
        {
            var start = new WorldFlightDestination("cruise", "Cruise", 10000, 10000, altitude, 0, 10000, 90000);
            return FlightWorldDefinition.CreateGeographic("Test", start, null, coast);
        }

        var fullBurn = new FlightInput(0, 0, 0, 1, true, false);
        var cruiseWorld = WorldAt(8500);
        var lowWorld = WorldAt(700);
        var high = AirStart(new FlightSimulation(cruiseWorld));
        var low = AirStart(new FlightSimulation(lowWorld));
        for (var i = 0; i < 1500; i++)
        {
            high.Advance(.1, fullBurn);
            low.Advance(.1, fullBurn);
        }
        var cruise = high.Snapshot.Aircraft;
        var seaLevel = low.Snapshot.Aircraft;
        Console.WriteLine($"World jet at altitude: Mach {cruise.Mach:F3}, {cruise.Speed:F1} m/s, {cruise.Y:F0} m; low: Mach {seaLevel.Mach:F3} at {seaLevel.Y:F0} m");
        Check.That(high.Snapshot.Phase == "flying" && cruise.Mach is > 1.65 and < 1.805 &&
            cruise.Speed > 510 && cruise.Z - 10000 > 55000 && cruise.Fuel < .75,
            "afterburner produces real supersonic travel, not just a changed readout");
        Check.That(low.Snapshot.Phase == "flying" && seaLevel.Mach > 1.25,
            "the clean jet can also break Mach 1 without a dive or a high-altitude start");

        for (var i = 0; i < 40; i++)
            high.Advance(.1, new FlightInput(0, 0, 0, 0, false, true));
        Check.That(!high.Snapshot.Aircraft.Afterburner && high.Snapshot.Aircraft.Speed < cruise.Speed - 50,
            "braking and cutting power still remove real velocity");
        Check.That(new FlightSimulation().Snapshot.Aircraft.Mach is null, "trainer keeps its original telemetry contract");

        CheckAircraftSelection(cruiseWorld, fullBurn);
        CheckAircraftPerformance(cruiseWorld, lowWorld, fullBurn);
    }

    private static void CheckAircraftSelection(FlightWorldDefinition world, FlightInput fullBurn)
    {
        var ids = new[] { "f18", "f14", "f15", "sr71" };
        var published = JsonSerializer.SerializeToElement(FlightSimulation.WorldAircraft);
        Check.That(published.EnumerateArray().Select(p => p.GetProperty("id").GetString()).SequenceEqual(ids),
            "World Flight publishes the four supported airframe IDs, with F/A-18 first");
        foreach (var profile in published.EnumerateArray())
            Check.That(profile.EnumerateObject().Count() == 3 &&
                !string.IsNullOrWhiteSpace(profile.GetProperty("name").GetString()) &&
                profile.GetProperty("maximumMach").GetDouble() > 1,
                "World Flight metadata contains only the ID, display name and Mach limit");

        Check.Throws<ArgumentException>(() => new FlightSimulation(world, "unknown"), "unknown airframes are rejected");
        Check.Throws<ArgumentException>(() => new FlightSimulation(world, ""), "empty airframe IDs are rejected");
        Check.Throws<ArgumentNullException>(() => new FlightSimulation(world, null!), "null airframe IDs are rejected");
        foreach (var id in ids.Skip(1))
            Check.Throws<ArgumentException>(() => new FlightSimulation(FlightWorldDefinition.Default, id),
                $"{id} cannot be selected in Interceptor Bay");
        var originalWorld = FlightWorldDefinition.Default with { SourceWorld = new([], [], [], []) };
        Check.Throws<ArgumentException>(() => new FlightSimulation(originalWorld, "f18"),
            "original worlds must still use the original session");

        var implicitDefault = AirStart(new FlightSimulation(world));
        var explicitDefault = AirStart(new FlightSimulation(world, "f18"));
        var defaultAlias = AirStart(new FlightSimulation(world, "default"));
        for (var frame = 0; frame < 300; frame++)
        {
            implicitDefault.Advance(.1, fullBurn);
            explicitDefault.Advance(.1, fullBurn);
            defaultAlias.Advance(.1, fullBurn);
        }
        Check.That(implicitDefault.Aircraft.Id == "f18" && defaultAlias.Aircraft.Id == "f18" &&
            implicitDefault.Snapshot == explicitDefault.Snapshot && implicitDefault.Snapshot == defaultAlias.Snapshot,
            "default selection keeps the existing geographic flight behavior");
    }

    private static void CheckAircraftPerformance(FlightWorldDefinition cruiseWorld, FlightWorldDefinition lowWorld,
        FlightInput fullBurn)
    {
        (string Id, double Mach, double Distance)[] targets =
        [
            ("f18", 1.8, 28_000), ("f14", 2.1, 31_000),
            ("f15", 2.4, 35_000), ("sr71", 3.2, 44_000)
        ];
        var rolls = new Dictionary<string, double>();
        foreach (var (id, mach, distance) in targets)
        {
            var jet = AirStart(new FlightSimulation(cruiseWorld, id));
            var start = jet.Snapshot.Aircraft;
            Check.That(jet.Aircraft.Id == id && start.Mach is < .6 && start.Y == 8500 && !start.GearDown,
                $"{id} starts from the same low-speed clean cruise departure");
            var beforeFinalSecond = start;
            for (var i = 0; i < 600; i++)
            {
                if (i == 590) beforeFinalSecond = jet.Snapshot.Aircraft;
                jet.Advance(.1, fullBurn);
            }
            var snapshot = jet.Snapshot;
            var cruise = snapshot.Aircraft;
            var travelled = cruise.Z - start.Z;
            Console.WriteLine($"{id} 60 s cruise: Mach {cruise.Mach:F3}, {cruise.Speed:F1} m/s, {travelled / 1000:F2} km, {cruise.Y:F0} m");
            Check.That(snapshot.Phase == "flying" && snapshot.Tick == 7200 && !jet.TerrainWaiting &&
                cruise.Afterburner && cruise.Fuel is > .85 and < .87 && cruise.Y is > 8000 and < 9300,
                $"{id} sustains the full fixed-step cruise near departure altitude with normal fuel consumption");
            Check.Near(cruise.Mach ?? double.NaN, mach, .025, $"{id} reaches its target Mach in at most 60 sim seconds");
            Check.Near(cruise.Mach ?? double.NaN, jet.Aircraft.MaximumMach, .01, $"{id} attains its published Mach limit");
            Check.That(travelled > distance && cruise.Speed > mach * 300,
                $"{id} gains real speed and distance, not just Mach telemetry");
            Check.Near(cruise.Z - beforeFinalSecond.Z, cruise.Speed, 3, $"{id} final second of travel matches actual speed");

            var bank = AirStart(new FlightSimulation(cruiseWorld, id));
            FlightScenarios.Advance(bank, 1, new FlightInput(.65, 0, 0, .47, false, false));
            rolls[id] = -bank.Snapshot.Aircraft.Roll;

            if (id == "sr71") CheckCruiseHandling(jet, cruiseWorld, fullBurn);
        }
        Check.That(rolls["f15"] > rolls["f14"] + .12 && rolls["f14"] > rolls["sr71"] + .08 && rolls["sr71"] > .45,
            "F-15 rolls more readily than F-14; SR-71 rolls more gently without losing bank control");

        var low = AirStart(new FlightSimulation(lowWorld, "sr71"));
        FlightScenarios.Advance(low, 60, fullBurn);
        var beforeBraking = low.Snapshot.Aircraft;
        Check.That(low.Snapshot.Phase == "flying" && beforeBraking.Y < 2500 && beforeBraking.Mach is > 1.4 and < 2.25,
            "SR-71 cruise thrust does not engage near a low-altitude departure");
        low.Command("gear-toggle");
        FlightScenarios.Advance(low, 4, new FlightInput(0, 0, 0, 0, false, true));
        var braking = low.Snapshot.Aircraft;
        Check.That(low.Snapshot.Phase == "flying" && braking.GearDown && !braking.Afterburner &&
            braking.Speed < beforeBraking.Speed - 200, "SR-71 can shed real energy for an approach");
    }

    private static void CheckCruiseHandling(FlightSimulation jet, FlightWorldDefinition world, FlightInput fullBurn)
    {
        var equivalent = AirStart(new FlightSimulation(world, "sr71"));
        for (var i = 0; i < 144 * 60; i++) equivalent.Advance(1.0 / 144, fullBurn);
        Check.That(jet.Snapshot.Tick == equivalent.Snapshot.Tick &&
            jet.Snapshot.Aircraft == equivalent.Snapshot.Aircraft, "SR-71 cruise is independent of frame schedule");

        FlightScenarios.Advance(equivalent, 1, fullBurn with { Roll = .65 });
        var bank = equivalent.Snapshot.Aircraft;
        Check.That(equivalent.Snapshot.Phase == "flying" && bank.Mach > 3.1 &&
            bank.Roll is > -.38 and < -.20, "SR-71 remains bankable, with damped roll at Mach 3");

        var before = jet.Snapshot.Aircraft;
        FlightScenarios.Advance(jet, 2, fullBurn with { Pitch = 1 });
        var climbing = jet.Snapshot.Aircraft;
        Check.That(jet.Snapshot.Phase == "flying" && climbing.Y - before.Y is > 30 and < 100 &&
            climbing.VerticalSpeed is > 30 and < 85 && climbing.GForce is > 1 and < 6,
            "full pitch at Mach 3 starts a real but bounded climb");
        FlightScenarios.Advance(jet, 12, fullBurn);
        Check.That(jet.Snapshot.Phase == "flying" &&
            jet.Snapshot.Aircraft.VerticalSpeed is > -5 and < 25 &&
            jet.Snapshot.Aircraft.Y > climbing.Y, "releasing pitch at cruise damps the climb without locking the altitude");
    }

    private static FlightSimulation AirStart(FlightSimulation jet)
    {
        jet.Command("air-start");
        return jet;
    }
}
