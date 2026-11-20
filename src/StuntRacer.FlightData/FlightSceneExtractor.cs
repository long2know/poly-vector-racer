namespace StuntRacer.FlightData;

internal static class FlightSceneExtractor
{
    internal static FlightScene Extract(FlightImage image, IReadOnlyList<FlightPad> pads, IReadOnlyList<FlightShip> ships)
    {
        var tiers = new List<FlightTerrainTier>();
        var lists = new List<FlightTerrainList>();
        var listIndex = new Dictionary<int, int>();
        foreach (var (name, address, shift, grid) in new[] { ("A", 0x47374, 24, 32), ("B", 0x47474, 24, 32), ("C", 0x47574, 22, 128) })
        {
            var cells = new List<FlightTerrainCell>();
            for (int z = 0; z < grid; z++)
            {
                int row = address + image.ReadUInt16(address + 2 * z);
                int n2 = image.ReadInt16(row);
                if (n2 < 0) continue;
                if ((n2 & 1) != 0 || n2 / 2 > grid) Fail("Odd or oversized terrain row.", row);
                int n = n2 / 2, previous = -1;
                for (int i = 0; i < n; i++)
                {
                    int x = image.ReadUInt16(row + 2 + i * 2);
                    if (x <= previous || x >= grid) Fail("Terrain row keys are not strictly increasing/in range.", row);
                    previous = x;
                    int pointer = Address(image.ReadUInt32(row + 2 + n2 + i * 4));
                    if (!listIndex.TryGetValue(pointer, out int index))
                    {
                        index = lists.Count; listIndex.Add(pointer, index);
                        lists.Add(new FlightTerrainList(pointer, ReadItems(image, pointer)));
                    }
                    cells.Add(new FlightTerrainCell(x, z, index));
                }
            }
            tiers.Add(new FlightTerrainTier(name, shift, grid, cells.AsReadOnly()));
        }
        if (tiers[0].Cells.Count != 36 || tiers[1].Cells.Count != 8 || tiers[2].Cells.Count != 158)
            Fail("Terrain-tier cell inventory differs from the documented original.", 0x47374);
        var starts = new List<FlightStartBlock>();
        bool terminated = false;
        for (int i = 0; i < 16; i++)
        {
            int address = 0x47ADE + i * 16;
            if (image.ReadInt16(address) == -1) { terminated = true; break; }
            var words = Enumerable.Range(0, 8).Select(j => image.ReadInt16(address + 2 * j)).ToArray();
            starts.Add(new FlightStartBlock(i, address, Array.AsReadOnly(words)));
        }
        if (!terminated || starts.Count != 5) Fail("Invalid player-start table.", 0x47ADE);
        var positions = new List<FlightPositionBlock>();
        int lowest = 2048;
        for (int offset = 0; offset < lowest; offset += 2)
        {
            int pointer = image.ReadUInt16(0x2DCE8 + offset);
            if (pointer == 0) continue;
            lowest = Math.Min(lowest, pointer);
            if ((pointer & 1) != 0 || pointer < offset + 2 || lowest > 2048) Fail("Invalid position-block table.", 0x2DCE8 + offset);
            int p = 0x2DCE8 + pointer;
            positions.Add(new FlightPositionBlock(offset, image.ReadInt16(p), image.ReadInt16(p + 2),
                image.ReadInt16(p + 4), image.ReadInt16(p + 6), image.ReadInt16(p + 8)));
        }
        if (lowest == 2048) Fail("Unterminated position-block table.", 0x2DCE8);
        var (waterCells, waterRegions) = ReadWater(image);
        var models = FlightModelExtractor.Extract(image);
        var player = models.Single(m => m.Id == 4).Levels[0];
        var carrier = models.Single(m => m.Id == 8).Levels[0];
        if (player.Vertices.Count < 4 || player.Flags != 1 || (player.ScaleNibbles & 15) != 3 ||
            carrier.Vertices.Count < 58 || (carrier.Flags & 0x11) != 0x11)
            Fail("Original player/carrier contact vertex headers differ.", player.BlockAddress);
        return new FlightScene(models, tiers.AsReadOnly(), lists.AsReadOnly(), positions.AsReadOnly(), starts.AsReadOnly(),
            pads, ships, waterCells, waterRegions) { ModelCollisions = FlightCollisionExtractor.Extract(image, models) };
    }

    private static IReadOnlyList<FlightTerrainItem> ReadItems(FlightImage image, int pointer)
    {
        var items = new List<FlightTerrainItem>();
        for (int p = pointer; items.Count < 4096; p += 6)
        {
            int first = image.ReadByte(p);
            if (first == 255) return items.AsReadOnly();
            int id = image.ReadByte(p + 1);
            if ((id & 128) != 0) Fail("Unverified terrain default-offset opcode is not decoded.", p);
            int x = image.ReadUInt16(p + 2), z = image.ReadUInt16(p + 4);
            if (x > 4095 || z > 4095 || (id & 127) + 16 >= 134) Fail("Invalid terrain item id or fine coordinates.", p);
            items.Add(new FlightTerrainItem(first & 15, (id & 127) + 16, x, z));
        }
        throw new FlightDataException("scene.terrain-budget", "Unterminated terrain item list.", pointer);
    }

    private static (IReadOnlyList<FlightWaterCell>, IReadOnlyList<FlightWaterRegion>) ReadWater(FlightImage image)
    {
        var pointers = new SortedSet<int>();
        for (int i = 0; i < 1024; i++)
        {
            short off = image.ReadInt16(0x47F40 + i * 2);
            int p = 0x47F40 + off;
            if (off <= 0 || (off & 1) != 0 || p < 0x48740 || p >= 0x495CC) Fail("Invalid water-region grid pointer.", 0x47F40 + i * 2);
            pointers.Add(p);
        }
        var sorted = pointers.ToArray();
        if (sorted.Length != 103) Fail("Original water-region inventory differs.", 0x47F40);
        var regions = new List<FlightWaterRegion>();
        var indices = new Dictionary<int, int>();
        for (int i = 0; i < sorted.Length; i++)
        {
            int address = sorted[i], end = i + 1 < sorted.Length ? sorted[i + 1] : 0x495CC;
            short h0 = image.ReadInt16(address), h1 = image.ReadInt16(address + 2);
            var polygons = new List<FlightWaterPolygon>();
            if (h0 >= 0)
            {
                int p = address + 4;
                bool terminated = false;
                for (int steps = 0; steps < 256 && p + 2 <= end; steps++)
                {
                    int polygonAddress = p;
                    short n = image.ReadInt16(p); p += 2;
                    if (n == -1) { terminated = true; break; }
                    // Non-positive count words are markers: the next word replaces the count.
                    if (n <= 0) continue;
                    if (n < 3 || n > 256 || p + n * 4 > end) Fail("Invalid/truncated water polygon.", polygonAddress);
                    var vertices = new List<FlightWaterVertex>();
                    for (int j = 0; j < n; j++)
                    {
                        vertices.Add(new FlightWaterVertex(image.ReadInt16(p), image.ReadInt16(p + 2)));
                        p += 4;
                    }
                    polygons.Add(new FlightWaterPolygon(polygonAddress, vertices.AsReadOnly()));
                }
                if (!terminated) Fail("Water polygon record has no terminator.", address);
            }
            indices.Add(address, regions.Count);
            regions.Add(new FlightWaterRegion(address, h0, h1, h0 < 0, polygons.AsReadOnly()));
        }
        if (regions.Count(r => r.NoWater) != 38) Fail("Original no-water region inventory differs.", 0x48740);
        var cells = new List<FlightWaterCell>();
        for (int z = 0; z < 32; z++)
            for (int x = 0; x < 32; x++)
                cells.Add(new FlightWaterCell(x, z, indices[0x47F40 + image.ReadInt16(0x47F40 + 2 * (x + 32 * z))]));
        return (cells.AsReadOnly(), regions.AsReadOnly());
    }

    internal static int Address(uint value)
    {
        if (value is < 0x10000 or >= 0x58FEC || (value & 1) != 0) Fail($"Invalid flat image pointer 0x{value:X}.", 0);
        return (int)value;
    }
    private static void Fail(string message, int address) => throw new FlightDataException("scene.structure", message, address);
}
