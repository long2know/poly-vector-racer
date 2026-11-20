using System.Buffers.Binary;

namespace StuntRacer.FlightData;

internal static class FlightTableExtractor
{
    // Hashes are metadata, not data. These are of the independently decoded, user-supplied
    // d69fad72 disk, compared against the pinned functional format and its published samples.
    // They identify this supported variant; they do not assert a pristine publisher disk.
    private static readonly (string Name, int Address, int Count, string Pin)[] Words =
    [
        ("flight_stick_player", 0x425D8, 62, "6d13140cadb28fb38fc91a79f9b0b5427ab9d1b3c10a51c768b80851d4492d9c"),
        ("flight_stick_ai", 0x42654, 64, "84c69909601f6734f126db9720c0bae9c4bdd7ec6c6e2be2d6d0f28ba6280628"),
        ("flight_gust", 0x2AB18, 31, "7e55ff3d4920bbc27aa7ee941bb0964243232783da437e20f677edfbdcf2caeb"),
        ("flight_sink", 0x2AAD8, 31, "51cd4ef3a6f93474057dfcd3717cc55864846d3cb4092699a67f254741db4da1"),
        ("flight_len", 0x208E8, 257, "1e9614fe1c1bcddeb3b159a8c1c337f12435ac44c9165eaa4ae00760c16c1eb3"),
        ("flight_terrain", 0x47F40, 2886, "1f5151b4fedb549f0ace3820d7a854f7b4942a35ff5c97e17ac89e00b8e2d816"),
        ("flight_dir", 0x206F2, 32, "6a5004053af4f99cacddeb107ac118115f12293e04c36679c61753f4068bfe7a")
    ];

    internal static Dictionary<string, FlightTable> Extract(FlightImage flat, FlightImage reference,
        out IReadOnlyList<FlightPad> pads, out IReadOnlyList<FlightShip> ships)
    {
        var result = new Dictionary<string, FlightTable>(StringComparer.Ordinal);
        foreach (var w in Words)
            AddWords(result, flat, w.Name, w.Address, w.Count, w.Pin);
        AddWords(result, flat, "flight_pitch_scale", 0x3259A, 15);
        AddWords(result, flat, "flight_sine", 0x436E8, 901);
        AddWords(result, flat, "flight_asin_0", 0x42E92, 259);
        AddWords(result, flat, "flight_asin_1", 0x43098, 552);
        AddWords(result, flat, "flight_asin_2", 0x434E8, 256);
        AddRaw(result, reference, "flight_span_pitch", [new(0x3249A, 286)],
            "0b5b4a6abcef43cf913f944ebb67846f4c9cc11a20288c2aebc5934584df694b");
        AddRaw(result, reference, "flight_span_trig", [new(0x3AE92, 60288)],
            "62c7caac339f553b4ab62b7bd2ae117eae7ce857450d4e73f797fde1010278c7");
        var padList = ReadPads(flat);
        var words = new List<short>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 8; j++) words.Add(flat.ReadInt16(0x47B72 + 16 * i + j * 2));
        foreach (var pad in padList) { words.Add((short)pad.QuadWords.Count); words.AddRange(pad.QuadWords); }
        var padRanges = new List<FlightTableRange> { new(0x47B72, 80) };
        padRanges.AddRange(padList.Where(p => p.QuadWords.Count > 0).Select(p => new FlightTableRange(p.QuadAddress, p.QuadWords.Count * 2)));
        Add(result, "flight_pads", "i16le", words.Count, LittleEndian(words), padRanges.AsReadOnly(),
            "b9f9c667126d53ad821cb91e4647e3485539bba983f2d4238179f910dea7f107");
        // Documented independent FNV pin, note 05, not solely a user-baseline hash.
        if (Fnv1A(result["flight_pads"].Bytes.Span) != 0xE89381F3 ||
            Fnv1A(LittleEndian(padList[0].QuadWords.Take(8))) != 0x8D45F03F)
            throw new FlightDataException("tables.pad-fingerprint", "Airfield data differs from the published pad fingerprints.");
        pads = padList.AsReadOnly();
        ships = new[]
        {
            ReadShip(result, flat, "flight_carrier", 0x3E0F8, 124, 0x3E174, 44, 0x3E6AE, 684,
                "d3ff24c6b04e80d9ee66b40cf19c7d95deae9eae8d31cadd527c4450dc4eb5f0"),
            ReadShip(result, flat, "flight_ship2", 0x3EDCC, 32, 0x3EDEC, 44, 0x3F2B6, 362,
                "df4c823f0b05c06d99cec1b075278264445764f4b11a6bf3009c53da8cf38bba")
        };
        return result;
    }

    private static List<FlightPad> ReadPads(FlightImage image)
    {
        if (image.ReadInt16(0x47B72 + 16 * 4 + 2) != -1)
            throw new FlightDataException("tables.pad-terminator", "Fifth airfield pad entry is not the terminator.");
        var pads = new List<FlightPad>();
        for (int i = 0; i < 4; i++)
        {
            int address = 0x47B72 + i * 16;
            short[] header = Enumerable.Range(0, 8).Select(j => image.ReadInt16(address + 2 * j)).ToArray();
            if (header[0] < 0 || header[0] % 20 != 0 || header[0] / 20 + 16 >= 134 || header[3] is < 0 or >= 16)
                throw new FlightDataException("tables.pad-descriptor", $"Invalid airfield descriptor/direction {i}.", address);
            int pointer = checked((int)image.ReadUInt32(0x2600C + header[0] + 0x10));
            var quads = new List<short>();
            if (header[1] >= 0)
            {
                for (int k = 0; k < 64; k++)
                {
                    short value = image.ReadInt16(pointer + k * 2);
                    quads.Add(value);
                    if (value == -1) break;
                }
                if (quads.Count == 0 || quads[^1] != -1 || (quads.Count - 1) % 8 != 0)
                    throw new FlightDataException("tables.pad-quads", $"Invalid or unterminated quad list for pad {i}.", pointer);
            }
            pads.Add(new FlightPad(i, address, Array.AsReadOnly(header), pointer, quads.AsReadOnly()));
        }
        return pads;
    }

    private static FlightShip ReadShip(Dictionary<string, FlightTable> tables, FlightImage image, string name,
        int list, int listBytes, int hull, int hullBytes, int structures, int structureBytes, string pin)
    {
        var quadAddresses = new List<int>();
        var hullAddresses = new List<int>();
        ReadList(list, listBytes, true); ReadList(hull, hullBytes, false);
        AddRaw(tables, image, name, [new(list, listBytes), new(hull, hullBytes), new(structures, structureBytes)], pin);
        return new FlightShip(name, quadAddresses.AsReadOnly(), hullAddresses.AsReadOnly(), tables[name]);

        void ReadList(int start, int count, bool deckList)
        {
            bool first = true;
            for (int p = start; p < start + count;)
            {
                if (image.ReadUInt16(p) == 0xFFFF) { p += 2; first = true; continue; }
                if (p + 4 > start + count) throw new FlightDataException("tables.ship-list", $"Partial pointer in {name}.", p);
                uint pointer = image.ReadUInt32(p);
                if ((pointer & 1) != 0 || pointer < structures || pointer >= structures + structureBytes)
                    throw new FlightDataException("tables.ship-pointer", $"Pointer 0x{pointer:X} is outside {name} structures.", p);
                if (deckList && first) quadAddresses.Add((int)pointer); else hullAddresses.Add((int)pointer);
                first = false; p += 4;
            }
        }
    }

    private static void AddWords(Dictionary<string, FlightTable> tables, FlightImage image, string name, int address, int count, string? pin = null)
    {
        var words = new short[count];
        for (int i = 0; i < count; i++) words[i] = image.ReadInt16(address + i * 2);
        Add(tables, name, "i16le", count, LittleEndian(words), [new(address, count * 2)], pin);
    }
    private static void AddRaw(Dictionary<string, FlightTable> tables, FlightImage image, string name, FlightTableRange[] ranges, string? pin)
    {
        byte[] bytes = new byte[ranges.Sum(r => r.ByteCount)];
        int p = 0;
        foreach (var r in ranges) { image.Slice(r.Address, r.ByteCount).CopyTo(bytes.AsSpan(p)); p += r.ByteCount; }
        Add(tables, name, "raw-be", bytes.Length, bytes, Array.AsReadOnly(ranges), pin);
    }
    private static void Add(Dictionary<string, FlightTable> tables, string name, string encoding, int elements, byte[] bytes,
        IReadOnlyList<FlightTableRange> ranges, string? pin)
    {
        string hash = Hashes.Sha256(bytes);
        if (pin != null && hash != pin)
            throw new FlightDataException("tables.fingerprint", $"{name}: SHA256 {hash} differs from supported original-data variant {pin}.");
        tables.Add(name, new FlightTable(new FlightTableMetadata(name, encoding, elements, bytes.Length, hash, ranges,
            pin == null ? "source-image pinned" : "full-table SHA256 pinned"), bytes));
    }
    private static byte[] LittleEndian(IEnumerable<short> words)
    {
        var values = words.ToArray(); byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
        return bytes;
    }
    internal static uint Fnv1A(ReadOnlySpan<byte> bytes)
    {
        uint h = 2166136261;
        foreach (byte value in bytes) h = unchecked((h ^ value) * 16777619);
        return h;
    }
}
