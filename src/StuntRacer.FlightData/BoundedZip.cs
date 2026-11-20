using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace StuntRacer.FlightData;

internal static class BoundedZip
{
    internal const int MaximumInputBytes = 2 * 1024 * 1024;
    internal const int AdfBytes = 901120;

    internal static (byte[] Disk, string Name) Read(ReadOnlySpan<byte> input)
    {
        // Inspect both directory and local records ourselves: ZipArchive alone permits
        // multiple entries, traversal names and declared sizes unrelated to decompressed size.
        if (input.Length < 22 || input.Length > MaximumInputBytes) Fail("Archive size is outside the input budget.");
        int end = -1;
        for (int p = input.Length - 22; p >= Math.Max(0, input.Length - 65557); p--)
        {
            if (U32(input, p) == 0x06054B50 && p + 22 + U16(input, p + 20) == input.Length)
            { end = p; break; }
        }
        if (end < 0) Fail("Missing ZIP end-of-directory record.");
        if (U16(input, end + 4) != 0 || U16(input, end + 6) != 0 ||
            U16(input, end + 8) != 1 || U16(input, end + 10) != 1)
            Fail("Exactly one ADF member on one ZIP volume is required.");
        int centralLength = Int32(input, end + 12), central = Int32(input, end + 16);
        if (central < 30 || centralLength < 46 || (long)central + centralLength != end) Fail("Invalid ZIP directory bounds.");
        if (U32(input, central) != 0x02014B50) Fail("Invalid ZIP central header.");
        int flags = U16(input, central + 8), method = U16(input, central + 10);
        int version = U16(input, central + 6);
        if (version > 20 || U16(input, 4) != version) Fail("Unsupported or contradictory ZIP version-needed fields.");
        // Deflate bits 1/2 describe the compressor's effort, not encryption or a new format.
        if ((flags & ~0x80E) != 0 || (method != 0 && method != 8) || (method == 0 && (flags & 6) != 0))
            Fail("Only unencrypted stored/deflated ADF entries are supported.");
        uint crc = U32(input, central + 16);
        int compressed = Int32(input, central + 20), size = Int32(input, central + 24);
        int nameLength = U16(input, central + 28), extra = U16(input, central + 30), comment = U16(input, central + 32);
        if (size != AdfBytes || compressed <= 0 || compressed > MaximumInputBytes ||
            nameLength is < 1 or > 255 || extra > 4096 ||
            46 + nameLength + extra + comment != centralLength ||
            U16(input, central + 34) != 0 || U32(input, central + 42) != 0)
            Fail("Invalid ADF size, member name, local offset or ZIP64/multi-volume metadata.");
        var nameBytes = Slice(input, central + 46, nameLength);
        if (nameBytes.ContainsAny((byte)0, (byte)'/', (byte)'\\') || nameBytes.Contains((byte)':') ||
            nameBytes.IndexOfAnyInRange((byte)0, (byte)31) >= 0)
            Fail("Archive members must have a plain ADF file name, not a path.");
        string name;
        try { name = (flags & 0x800) != 0 ? new UTF8Encoding(false, true).GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes); }
        catch (DecoderFallbackException) { throw new FlightDataException("zip.name", "Invalid UTF-8 archive name."); }
        if (!name.EndsWith(".adf", StringComparison.OrdinalIgnoreCase) || name is "." or "..") Fail("The sole ZIP member must be an ADF.");
        CheckExtra(Slice(input, central + 46 + nameLength, extra));
        if (U32(input, 0) != 0x04034B50 || U16(input, 6) != flags || U16(input, 8) != method ||
            U16(input, 26) != nameLength) Fail("Local and central ZIP headers disagree.");
        int localExtra = U16(input, 28);
        if (localExtra > 4096 || !Slice(input, 30, nameLength).SequenceEqual(nameBytes)) Fail("Local ZIP member name differs.");
        CheckExtra(Slice(input, 30 + nameLength, localExtra));
        int data = 30 + nameLength + localExtra;
        int dataEnd = checked(data + compressed);
        if (dataEnd > central) Fail("Compressed data overlaps the ZIP directory.");
        if ((flags & 8) == 0)
        {
            if (U32(input, 14) != crc || Int32(input, 18) != compressed || Int32(input, 22) != size || dataEnd != central)
                Fail("ZIP member sizes/CRC or data boundaries disagree.");
        }
        else
        {
            int descriptor = dataEnd;
            if (U32(input, descriptor) == 0x08074B50) descriptor += 4;
            if (descriptor + 12 != central || U32(input, descriptor) != crc ||
                Int32(input, descriptor + 4) != compressed || Int32(input, descriptor + 8) != size)
                Fail("Invalid ZIP data descriptor.");
            // A streaming local record may use zeroes; non-zero declarations must agree.
            if ((U32(input, 14) != 0 && U32(input, 14) != crc) ||
                (Int32(input, 18) != 0 && Int32(input, 18) != compressed) ||
                (Int32(input, 22) != 0 && Int32(input, 22) != size))
                Fail("Contradictory streaming ZIP local metadata.");
        }
        byte[] disk = new byte[AdfBytes];
        if (method == 0)
        {
            if (compressed != disk.Length) Fail("Stored ADF has inconsistent sizes.");
            Slice(input, data, compressed).CopyTo(disk);
        }
        else
        {
            using var compressedStream = new MemoryStream(Slice(input, data, compressed).ToArray(), false);
            using var inflater = new DeflateStream(compressedStream, CompressionMode.Decompress);
            int read = 0;
            try
            {
                while (read < disk.Length)
                {
                    int n = inflater.Read(disk.AsSpan(read));
                    if (n == 0) Fail("Deflated ADF is shorter than its declared length.");
                    read += n;
                }
                if (inflater.ReadByte() != -1) Fail("Deflated ADF exceeds its fixed size.");
            }
            catch (InvalidDataException e) { throw new FlightDataException("zip.deflate", $"Invalid deflate stream: {e.Message}"); }
        }
        if (Crc32(disk) != crc) throw new FlightDataException("zip.crc", "ADF member CRC32 does not match.");
        return (disk, name);
    }

    private static void CheckExtra(ReadOnlySpan<byte> bytes)
    {
        for (int p = 0; p < bytes.Length;)
        {
            if (bytes.Length - p < 4) Fail("Truncated ZIP extra record.");
            int id = U16(bytes, p), n = U16(bytes, p + 2);
            if (id == 1 || n > bytes.Length - p - 4) Fail("ZIP64 or truncated ZIP extra record is unsupported.");
            p += 4 + n;
        }
    }

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return ~crc;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> b, int p, int n)
    {
        if (p < 0 || n < 0 || (long)p + n > b.Length) Fail("Truncated ZIP record.");
        return b.Slice(p, n);
    }
    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(b, p, 2));
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(b, p, 4));
    private static int Int32(ReadOnlySpan<byte> b, int p)
    {
        uint n = U32(b, p);
        if (n > int.MaxValue) Fail("ZIP64/oversized ZIP field.");
        return (int)n;
    }
    private static void Fail(string message) => throw new FlightDataException("zip.structure", message);
}
