using System.Buffers.Binary;
using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class AtlasChecks
{
    internal static byte[] Fallback(short height)
    {
        var bytes = new byte[WorldAtlasBase.ByteLength];
        "WFA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2432);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1217);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 16384);
        for (var i = 0; i < 2432 * 1217; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(16 + 2 * i), height);
        return bytes;
    }

    public static void FallbackAndWorldBounds()
    {
        var data = Fallback(200);
        void Point(int x, int z, short height, byte water = 0)
        {
            var i = z * 2432 + x;
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(16 + 2 * i), height);
            data[16 + 2 * 2432 * 1217 + i] = water;
        }

        Point(2431, 608, -200); Point(0, 608, 100, 2);
        Point(2431, 609, -40, 1); Point(0, 609, 300);
        Point(35, 0, -83, 2); Point(35, 1216, 712);
        var fallback = new WorldAtlasBase(data);
        const int east = 19922944;
        Check.That(fallback.Sample(east - 12288, 12288) == (5, 1), "wrapped cell uses signed upper triangle and nearest north-west water");
        Check.That(fallback.Sample(east - 4096, 4096) == (75, 2), "wrapped cell uses lower triangle and nearest wrapped east sample");
        Check.That(fallback.Sample(-east, 0) == (100, 2) && fallback.Sample(east, 0) == (100, 2) &&
            fallback.Sample(3 * east, 0) == (100, 2), "base has a single east-exclusive column at the date line");
        var x = -east + 35 * 16384;
        Check.That(fallback.Sample(x, -9961472) == (-83, 2) && fallback.Sample(x, 9961472) == (712, 0),
            "both polar rows are reachable, with no north wrap or invented sea level");
        Check.That(WorldAtlasGrid.Project(180, 90) == (-east, 9961472) &&
            WorldAtlasGrid.Project(-180, -90) == (-east, -9961472), "coordinates use canonical degrees and include the poles");
        Check.Throws<ArgumentOutOfRangeException>(() => WorldAtlasGrid.Project(181, 0), "out-of-domain longitude is rejected");
        data[12] = 1;
        Check.Throws<InvalidDataException>(() => new WorldAtlasBase(data), "WFA1 sample spacing is validated, not presumed");
    }

    public static void DetailActivation()
    {
        var elevation = new WorldAtlasElevation(new WorldAtlasBase(Fallback(200)));
        var root = new WorldAtlasTileKey(0, -1, -1);
        var child = root.Child(0);
        var quartet = Quartet(root, 350, 2);
        var fine = Quartet(child, -43, 0);
        Check.Throws<InvalidDataException>(() => elevation.Install(child, fine), "fine terrain cannot bypass its streamed parent");
        var wrong = (byte[])quartet.Clone();
        wrong[^WorldAtlasTile.ByteLength] = (byte)'X';
        Check.Throws<InvalidDataException>(() => elevation.Install(root, wrong), "a damaged last sibling rejects the entire quartet");
        Check.That(elevation.Sample(-220000, -220000) == (200, 0), "failed quartet did not change native terrain");
        elevation.Install(root, quartet);
        elevation.Install(child, fine);
        Check.That(elevation.Sample(-220000, -220000) == (-43, 0) && elevation.Sample(-3000, -3000) == (350, 2),
            "deepest active tile drives actual contact within its signed extent");
        Check.That(elevation.Sample(100, -3000) == (200, 0), "adjacent world retains fallback rather than entering a wait state");
        elevation.Remove(root);
        Check.That(elevation.Sample(-220000, -220000) == (200, 0) && elevation.Sample(-3000, -3000) == (200, 0),
            "retiring a parent atomically removes descendant contacts");
    }

    private static byte[] Quartet(WorldAtlasTileKey parent, short height, byte water)
    {
        var result = new byte[4 * WorldAtlasTile.ByteLength];
        for (var i = 0; i < 4; ++i)
        {
            var key = parent.Child(i);
            var tile = result.AsSpan(i * WorldAtlasTile.ByteLength, WorldAtlasTile.ByteLength);
            "WFT1"u8.CopyTo(tile);
            BinaryPrimitives.WriteInt32LittleEndian(tile[4..], key.Level);
            BinaryPrimitives.WriteInt32LittleEndian(tile[8..], key.X);
            BinaryPrimitives.WriteInt32LittleEndian(tile[12..], key.Z);
            for (var j = 0; j < 33 * 33; j++)
                BinaryPrimitives.WriteInt16LittleEndian(tile[(16 + j * 2)..], height);
            tile[(16 + 2 * 33 * 33)..].Fill(water);
        }
        return result;
    }

    public static void DepartureClearance()
    {
        var departure = new WorldAtlasDeparture("peak", "Peak departure", 86.925, 27.988);
        double StartAt(short baseHeight)
        {
            var elevation = new WorldAtlasElevation(new WorldAtlasBase(Fallback(baseHeight)));
            var sim = new FlightSimulation(FlightWorldDefinition.CreateAtlas(departure, elevation));
            sim.Command("air-start");
            return sim.Snapshot.Aircraft.Y;
        }
        Check.Near(StartAt(2500), 3900, 0, "normal terrain keeps its 1400 m launch clearance");
        Check.Near(StartAt(4500), 6900, 0, "extra high-mountain clearance ramps above normal terrain");
        Check.Near(StartAt(6504), 9904, 0, "an Everest-like coarse summit leaves room for 8849 m detail");
    }

    public static void ContinuousFlight()
    {
        const int east = 19922944, pole = 9961472;
        var fallback = new WorldAtlasBase(Fallback(200));
        FlightSimulation Launch(double lon, double lat, double heading)
        {
            var world = FlightWorldDefinition.CreateAtlas(new("test", "Test departure", lon, lat, heading), new(fallback));
            var sim = new FlightSimulation(world, "f15");
            sim.Command("air-start");
            return sim;
        }

        var input = new FlightInput(0, 0, 0, .47, false, false);
        var crossing = Launch(179.999, 0, Math.PI / 2);
        var otherSchedule = Launch(179.999, 0, Math.PI / 2);
        var original = crossing.Snapshot.Aircraft;
        for (var i = 0; i < 180; i++) crossing.Advance(1.0 / 60, input);
        for (var i = 0; i < 432; i++) otherSchedule.Advance(1.0 / 144, input);
        var after = crossing.Snapshot;
        Check.That(after == otherSchedule.Snapshot && after.Tick == 360, "date-line flight preserves the native fixed step across frame schedules");
        Check.That(after.Phase == "flying" && after.Time > 2.99 && after.Aircraft.X is > -east and < (-east + 600) &&
            !crossing.TerrainWaiting && after.Aircraft.Mach is > 0, "Atlas keeps integrating real flight when there is no optional detail");
        crossing.Command("recover");
        Check.That(crossing.Snapshot.Aircraft.X == original.X && crossing.Snapshot.Aircraft.Y == original.Y &&
            crossing.Snapshot.Recoveries == 1 && crossing.Snapshot.Time == after.Time, "recovery returns to the exact custom geographic departure");

        foreach (var sign in new[] { -1, 1 })
        {
            var outward = Launch(149, 90 * sign, sign > 0 ? 0 : Math.PI);
            var inward = Launch(149, 90 * sign, sign > 0 ? Math.PI : 0);
            var time = 1.0 / 120;
            var held = outward.Advance(time, input);
            var moved = inward.Advance(time, input);
            Check.That(held.Phase == "flying" && held.Tick == 1 && held.Aircraft.Z == pole * sign &&
                held.Aircraft.Speed < 5 && !outward.TerrainWaiting, "outward Z clamps and loses only outward travel at each pole");
            Check.That(moved.Phase == "flying" && moved.Aircraft.Z * sign < pole &&
                moved.Aircraft.Speed > 100, "inward flight can depart from either pole");
        }
    }
}
