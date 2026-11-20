using System.Buffers.Binary;
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
        FlightSimulation FlyAt(double altitude)
        {
            var start = new WorldFlightDestination("cruise", "Cruise", 10000, 10000, altitude, 0, 10000, 90000);
            var jet = new FlightSimulation(FlightWorldDefinition.CreateGeographic("Test", start, null, coast));
            jet.Command("air-start");
            return jet;
        }

        var fullBurn = new FlightInput(0, 0, 0, 1, true, false);
        var high = FlyAt(8500);
        var low = FlyAt(700);
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
    }
}
