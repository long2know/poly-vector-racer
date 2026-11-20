using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace StuntRacer.FlightData;

public sealed class FlightHunkImage
{
    public FlightImage FlatImage { get; }
    public IReadOnlyList<FlightHunk> Hunks { get; }
    public IReadOnlyList<FlightRelocation> Relocations { get; }
    public IReadOnlyList<FlightSymbol> Symbols { get; }
    internal FlightHunkImage(FlightImage image, List<FlightHunk> hunks, List<FlightRelocation> relocations, List<FlightSymbol> symbols)
    { FlatImage = image; Hunks = hunks.AsReadOnly(); Relocations = relocations.AsReadOnly(); Symbols = symbols.AsReadOnly(); }
}

/// <summary>Nonexecuting Amiga load-file reader. No CPU, OS calls, overlays or resident libraries.</summary>
public static class FlightHunkReader
{
    public static FlightHunkImage Read(ReadOnlySpan<byte> executable)
    {
        if (executable.Length is < 24 or > BoundedZip.MaximumInputBytes)
            throw new FlightDataException("hunk.size", "Hunk executable exceeds the input budget or is truncated.");
        var r = new HunkCursor(executable);
        if (r.Long() != 0x3F3) r.Fail("Missing HUNK_HEADER.");
        if (r.Long() != 0) r.Fail("Resident library names are not supported.");
        uint table = r.Long(), first = r.Long(), last = r.Long();
        if (first != 0 || last >= 512 || table != last + 1) r.Fail("Unsupported hunk table bounds.");
        int count = (int)last + 1;
        var sizes = new int[count];
        var flags = new uint[count];
        var bases = new int[count];
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            uint size = r.Long();
            flags[i] = size & 0xC0000000;
            sizes[i] = r.Size(size);
            bases[i] = 0x10000 + total;
            total = checked(total + sizes[i]);
            if (total > 2 * 1024 * 1024) r.Fail("Allocated image exceeds two MiB.");
        }
        byte[] image = new byte[total];
        var hunks = new List<FlightHunk>(count);
        var relocations = new List<FlightRelocation>();
        var pending = new List<(int Source, int Offset, int Target)>();
        var relocationOffsets = new HashSet<(int Source, int Byte)>();
        var symbols = new List<FlightSymbol>();
        int current = -1;
        bool ended = true;
        while (!r.AtEnd)
        {
            uint rawType = r.Long();
            if ((rawType & 0xC0000000) == 0xC0000000) r.Fail("Unsupported extended hunk memory flags.");
            uint type = rawType & 0x3FFFFFFF;
            if (type is 0x3E9 or 0x3EA or 0x3EB)
            {
                if (!ended || ++current >= count) r.Fail("Missing HUNK_END or extra hunk.");
                ended = false;
                int storedSize = r.Size(r.Long());
                if (storedSize > sizes[current]) r.Fail("Hunk payload exceeds its allocation.");
                string kind = type switch { 0x3E9 => "CODE", 0x3EA => "DATA", _ => "BSS" };
                hunks.Add(new FlightHunk(current, kind, bases[current], sizes[current], type == 0x3EB ? 0 : storedSize, flags[current]));
                if (type != 0x3EB) r.Take(storedSize).CopyTo(image.AsSpan(bases[current] - 0x10000));
                continue;
            }
            if (type == 0x3E8 && ended) { r.Take(r.Size(r.Long())); continue; }
            if (current < 0 || ended) r.Fail("Hunk sub-record outside an open hunk.");
            if (type == 0x3F2) { ended = true; continue; }
            if (type is 0x3E8 or 0x3F1) { r.Take(r.Size(r.Long())); continue; }
            if (type == 0x3F0)
            {
                while (true)
                {
                    uint words = r.Long();
                    if (words == 0) break;
                    if ((words & 0xFF000000) != 0 || words > 256 || symbols.Count >= 8192) r.Fail("Oversized/flagged symbol record.");
                    string name = Encoding.Latin1.GetString(r.Take((int)words * 4)).TrimEnd('\0');
                    uint offset = r.Long();
                    if (name.Length == 0 || offset > sizes[current]) r.Fail("Invalid symbol offset/name.");
                    symbols.Add(new FlightSymbol(name, current, offset, checked(bases[current] + (int)offset)));
                }
                continue;
            }
            if (type is 0x3EC or 0x3FC or 0x3F7)
            {
                bool compact = type != 0x3EC;
                while (true)
                {
                    uint n = compact ? r.Word() : r.Long();
                    if (n == 0) break;
                    if (n > 65536 || pending.Count + n > 65536) r.Fail("Too many hunk relocations.");
                    uint target = compact ? r.Word() : r.Long();
                    if (target >= count) r.Fail("Relocation target is outside the hunk table.");
                    for (int i = 0; i < n; i++)
                    {
                        uint off = compact ? r.Word() : r.Long();
                        if ((off & 1) != 0 || off > int.MaxValue || off + 4UL > (ulong)sizes[current])
                            r.Fail("Relocation offset is odd or outside its source hunk.");
                        for (int b = 0; b < 4; b++)
                            if (!relocationOffsets.Add((current, (int)off + b))) r.Fail("Duplicate or overlapping relocation.");
                        pending.Add((current, (int)off, (int)target));
                    }
                }
                if (compact) r.Align();
                continue;
            }
            r.Fail($"Unsupported hunk record 0x{type:X}; no speculative decoding.");
        }
        if (!ended || hunks.Count != count) r.Fail("Truncated or missing hunk/end record.");
        foreach (var (source, offset, target) in pending)
        {
            int address = bases[source] + offset;
            var bytes = image.AsSpan(address - 0x10000, 4);
            uint addend = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            uint value = unchecked(addend + (uint)bases[target]);
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            relocations.Add(new FlightRelocation(source, offset, target, addend, address, value, 0));
        }
        return new FlightHunkImage(new FlightImage(image), hunks, relocations, symbols);
    }

    private ref struct HunkCursor(ReadOnlySpan<byte> input)
    {
        private readonly ReadOnlySpan<byte> bytes = input;
        private int position;
        internal bool AtEnd => position == bytes.Length;
        internal uint Long() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ushort Word() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > bytes.Length - position) Fail("Truncated hunk record.");
            var result = bytes.Slice(position, n); position += n; return result;
        }
        internal int Size(uint word)
        {
            if ((word & 0xC0000000) == 0xC0000000 || (word & 0x3FFFFFFF) > 524288) Fail("Oversized/extended-memory hunk.");
            return (int)(word & 0x3FFFFFFF) * 4;
        }
        internal void Align()
        {
            if ((position & 3) != 0)
                if (Word() != 0) Fail("Nonzero compact relocation padding.");
        }
        internal void Fail(string message) => throw new FlightDataException("hunk.structure", message, position);
    }
}
