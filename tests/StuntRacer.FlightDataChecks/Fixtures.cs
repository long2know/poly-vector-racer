using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using StuntRacer.FlightData;

internal static class Fixtures
{
    // Invented bytes only. No game code, original tables or original geometry in fixtures.
    internal static byte[] Hunk(bool compact = false)
    {
        var words = new List<uint>
        {
            0x3F3, 0, 2, 0, 1, 4, 2,
            0x3E9, 4, 0x11223344, 7, 3, 0,
            compact ? 0x3FCu : 0x3ECu
        };
        if (compact)
        {
            words.Add(0x00010001); words.Add(0x00040001); words.Add(0x00010008); words.Add(0);
        }
        else
        {
            words.AddRange(new uint[] { 1, 1, 4, 1, 1, 8, 0 });
        }
        words.AddRange(new uint[] { 0x3F0, 2, 0x73616D70, 0x6C650000, 4, 0, 0x3F2, 0x3EA, 2, 0x55667788, 0x99AABBCC, 0x3F2 });
        byte[] bytes = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++) W(bytes, i * 4, words[i]);
        return bytes;
    }

    internal static byte[] Ofs()
    {
        byte[] disk = new byte[901120];
        "DOS\0"u8.CopyTo(disk);
        W(disk, 8, 880);
        int root = 880 * 512, bitmap = 881 * 512, header = 882 * 512, data = 883 * 512;
        W(disk, root, 2); W(disk, root + 12, 72); W(disk, root + 508, 1);
        W(disk, root + 312, uint.MaxValue); W(disk, root + 316, 881);
        Name(disk, root, "Invented Flight");
        W(disk, root + 24 + 4 * NameHash("example.bin"), 882);
        W(disk, header, 2); W(disk, header + 4, 882); W(disk, header + 8, 1);
        W(disk, header + 16, 883); W(disk, header + 308, 883); W(disk, header + 324, 12);
        W(disk, header + 500, 880); W(disk, header + 508, unchecked((uint)-3));
        Name(disk, header, "example.bin");
        W(disk, data, 8); W(disk, data + 4, 882); W(disk, data + 8, 1); W(disk, data + 12, 12);
        "invented-123"u8.CopyTo(disk.AsSpan(data + 24));
        for (int p = bitmap + 4; p < bitmap + 512; p += 4) W(disk, p, uint.MaxValue);
        foreach (int block in new[] { 880, 881, 882, 883 })
        {
            int bit = block - 2, p = bitmap + 4 + 4 * (bit / 32);
            W(disk, p, U(disk, p) & ~(1u << (bit % 32)));
        }
        foreach (int block in new[] { 880, 881, 882, 883 }) Checksum(disk, block, block == 881 ? 0 : 20);
        ulong sum = 0;
        for (int p = 0; p < 1024; p += 4)
        {
            sum += U(disk, p); sum = (sum & uint.MaxValue) + (sum >> 32);
        }
        W(disk, 4, ~(uint)sum);
        return disk;
    }

    internal static byte[] Zip(byte[] disk, string name = "invented.adf", bool twoEntries = false, bool stored = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using (var output = entry.Open()) output.Write(disk);
            if (twoEntries)
                using (var output = zip.CreateEntry("extra.txt").Open()) output.Write("not-an-adf"u8);
        }
        return buffer.ToArray();
    }
    internal static int Central(byte[] zip) => Find(zip, 0x02014B50);
    internal static int Find(byte[] bytes, uint magic)
    {
        for (int p = 0; p <= bytes.Length - 4; p++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4)) == magic) return p;
        throw new InvalidOperationException("Invented fixture marker not found.");
    }
    internal static void W(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), value);
    internal static uint U(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));
    internal static void Little16(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), value);
    internal static void Little32(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), value);
    internal static void Checksum(byte[] disk, int block, int checksumOffset = 20)
    {
        int start = block * 512;
        W(disk, start + checksumOffset, 0);
        uint sum = 0;
        for (int p = start; p < start + 512; p += 4) sum = unchecked(sum + U(disk, p));
        W(disk, start + checksumOffset, unchecked(0u - sum));
    }
    private static void Name(byte[] disk, int blockStart, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name); disk[blockStart + 432] = (byte)bytes.Length;
        bytes.CopyTo(disk, blockStart + 433);
    }
    private static int NameHash(string name)
    {
        int hash = name.Length;
        foreach (char c in name.ToUpperInvariant()) hash = (hash * 13 + c) & 0x7FF;
        return hash % 72;
    }
}
