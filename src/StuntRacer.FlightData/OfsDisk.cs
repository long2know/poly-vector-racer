using System.Buffers.Binary;
using System.Text;

namespace StuntRacer.FlightData;

internal sealed class OfsDisk
{
    private const int Blocks = 1760, BlockSize = 512, Root = 880;
    private readonly byte[] disk;
    private readonly HashSet<int> claimed = [0, 1];
    internal List<(FlightDiskFile Metadata, byte[] Bytes)> Files { get; } = [];
    internal int ValidatedBlocks => claimed.Count;
    internal string VolumeName { get; }

    internal OfsDisk(byte[] disk)
    {
        this.disk = disk;
        if (disk.Length != BoundedZip.AdfBytes || !disk.AsSpan(0, 4).SequenceEqual("DOS\0"u8))
            Fail("ofs.format", "Expected a 901120-byte DOS\\0 (OFS) disk.");
        ulong sum = 0;
        for (int p = 0; p < 1024; p += 4)
        {
            sum += U(disk.AsSpan(0, 1024), p);
            sum = (sum & uint.MaxValue) + (sum >> 32);
        }
        if (sum != uint.MaxValue) Fail("ofs.boot-checksum", "Invalid Amiga boot-block checksum.");
        if (U(disk, 8) != Root) Fail("ofs.root", "Boot block does not identify the standard root block.");
        var root = Claim(Root);
        if (U(root, 0) != 2 || U(root, 4) != 0 || U(root, 12) != 72 || S(root, 508) != 1)
            Fail("ofs.root", "Invalid OFS root header.", Root);
        VolumeName = Name(root);
        if (U(root, 312) != uint.MaxValue || U(root, 416) != 0)
            Fail("ofs.bitmap", "Disk bitmap is invalid or uses an unsupported extension.", Root);
        int bitmapBlock = Pointer(U(root, 316));
        if (bitmapBlock == 0) Fail("ofs.bitmap", "Missing disk bitmap.", Root);
        for (int i = 1; i < 25; i++)
            if (U(root, 316 + 4 * i) != 0) Fail("ofs.bitmap", "Unexpected extra bitmap on a DD floppy.", Root);
        Claim(bitmapBlock);
        ReadDirectory(Root, "", 0, root);
        var bitmap = Block(bitmapBlock);
        foreach (int block in claimed.Where(x => x >= 2))
        {
            int bit = block - 2;
            if ((U(bitmap, 4 + 4 * (bit / 32)) & (1u << (bit % 32))) != 0)
                Fail("ofs.bitmap", $"Referenced block {block} is marked free.", block);
        }
    }

    private void ReadDirectory(int number, string path, int depth, ReadOnlySpan<byte> header)
    {
        if (depth > 64 || path.Length > 4096) Fail("ofs.budget", "Directory nesting exceeds the parsing budget.", number);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int bucket = 0; bucket < 72; bucket++)
        {
            int next = Pointer(U(header, 24 + 4 * bucket));
            while (next != 0)
            {
                int current = next;
                var b = Claim(current);
                if (U(b, 0) != 2 || U(b, 4) != current || U(b, 500) != number)
                    Fail("ofs.header", "Directory entry has an invalid type, own key or parent.", current);
                string name = Name(b);
                if (!names.Add(name) || NameHash(name) != bucket)
                    Fail("ofs.hash", "Duplicate name or wrong OFS hash bucket.", current);
                string full = path.Length == 0 ? name : path + "/" + name;
                next = Pointer(U(b, 496));
                int secondary = S(b, 508);
                if (secondary == 2)
                    ReadDirectory(current, full, depth + 1, b);
                else if (secondary == -3)
                {
                    byte[] content = ReadFile(current, b);
                    Files.Add((new FlightDiskFile(full, current, content.Length, Hashes.Sha256(content)), content));
                }
                else Fail("ofs.type", $"Unsupported directory entry type {secondary}; links are not followed.", current);
            }
        }
    }

    private byte[] ReadFile(int key, ReadOnlySpan<byte> firstHeader)
    {
        uint fileLength = U(firstHeader, 324);
        if (fileLength > BoundedZip.AdfBytes) Fail("ofs.size", "File exceeds the entire disk size.", key);
        var pointers = new List<int>();
        int extension;
        int first = Pointer(U(firstHeader, 16));
        AddPointers(firstHeader, pointers, key);
        extension = Pointer(U(firstHeader, 504));
        while (extension != 0)
        {
            int current = extension;
            var b = Claim(current);
            if (U(b, 0) != 16 || U(b, 4) != current || U(b, 500) != key || S(b, 508) != -3)
                Fail("ofs.extension", "Invalid file extension header or parent.", current);
            AddPointers(b, pointers, current);
            extension = Pointer(U(b, 504));
        }
        int expected = ((int)fileLength + 487) / 488;
        if (pointers.Count != expected || (expected == 0 ? first != 0 : first != pointers[0]))
            Fail("ofs.pointers", "File length, data count and first-data pointer disagree.", key);
        byte[] result = new byte[fileLength];
        int offset = 0;
        for (int i = 0; i < pointers.Count; i++)
        {
            int number = pointers[i];
            var b = Claim(number);
            int size = (int)U(b, 12);
            if (U(b, 0) != 8 || U(b, 4) != key || U(b, 8) != i + 1 ||
                size != Math.Min(488, result.Length - offset) ||
                U(b, 16) != (i + 1 == pointers.Count ? 0 : pointers[i + 1]))
                Fail("ofs.data", "OFS data type, header, sequence, length or next pointer disagrees.", number);
            b.Slice(24, size).CopyTo(result.AsSpan(offset));
            offset += size;
        }
        return result;
    }

    private static void AddPointers(ReadOnlySpan<byte> b, List<int> pointers, int number)
    {
        uint n = U(b, 8);
        if (n > 72) Fail("ofs.pointers", "More than 72 pointers in a file header.", number);
        for (int i = 0; i < 72; i++)
        {
            int value = Pointer(U(b, 308 - 4 * i));
            if (i < n)
            {
                if (value == 0) Fail("ofs.pointers", "Missing declared data pointer.", number);
                pointers.Add(value);
            }
            else if (value != 0) Fail("ofs.pointers", "Nonzero unused file pointer.", number);
        }
    }

    private ReadOnlySpan<byte> Claim(int number)
    {
        if (number < 2 || number >= Blocks || !claimed.Add(number))
            Fail("ofs.cycle", $"Invalid, shared or cyclic OFS block {number}.", number);
        var b = Block(number);
        uint sum = 0;
        for (int i = 0; i < BlockSize; i += 4) sum = unchecked(sum + U(b, i));
        if (sum != 0) Fail("ofs.checksum", $"Checksum failure in OFS block {number}.", number);
        return b;
    }

    private ReadOnlySpan<byte> Block(int number) => disk.AsSpan(number * BlockSize, BlockSize);
    private static int Pointer(uint value)
    {
        if (value >= Blocks || value == 1) Fail("ofs.pointer", $"OFS pointer {value} is outside the floppy.");
        return (int)value;
    }
    private static string Name(ReadOnlySpan<byte> b)
    {
        int n = b[432];
        if (n is < 1 or > 30) Fail("ofs.name", "Invalid OFS BSTR file name.");
        var bytes = b.Slice(433, n);
        foreach (byte v in bytes)
            if (v is 0 or (byte)'/' or (byte)':' || v < 32) Fail("ofs.name", "Unsafe OFS file name.");
        return Encoding.Latin1.GetString(bytes);
    }
    private static int NameHash(string name)
    {
        int h = name.Length;
        foreach (char ch in name)
        {
            // DOS\0 uses the original ASCII-only case fold, not modern Unicode casing.
            int c = ch is >= 'a' and <= 'z' ? ch - 32 : ch;
            h = ((h * 13) + c) & 0x7FF;
        }
        return h % 72;
    }
    private static uint U(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p, 4));
    private static int S(ReadOnlySpan<byte> b, int p) => unchecked((int)U(b, p));
    private static void Fail(string code, string message, int? block = null) =>
        throw new FlightDataException(code, message, block is null ? null : block * BlockSize);
}
