using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;

namespace StuntRacer.FlightData;

internal static class ReferenceLayout
{
    internal const string ReferenceCommit = "2475dcd65f454a37b41a2be27629ad958f34ddc5";
    internal const string SegmentSourceSha256 = "12c6a3dbf0f6a61174130613012bb88393e85b548bb416e5826ec272c1d57644";
    internal static readonly IReadOnlyList<FlightReferenceSegment> Segments = Load();
    private static IReadOnlyList<FlightReferenceSegment> Load()
    {
        using var stream = typeof(ReferenceLayout).Assembly.GetManifestResourceStream("StuntRacer.FlightData.reference-segments.tsv")
            ?? throw new InvalidOperationException("Missing reference segment metadata.");
        using var reader = new StreamReader(stream);
        var result = new List<FlightReferenceSegment>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) throw new InvalidOperationException("Invalid reference segment metadata.");
            int i = int.Parse(parts[0], CultureInfo.InvariantCulture);
            if (i != result.Count) throw new InvalidOperationException("Nonsequential reference segments.");
            result.Add(new FlightReferenceSegment(i, int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                uint.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture)));
        }
        if (result.Count != 185) throw new InvalidOperationException("Incomplete reference segment metadata.");
        return result.AsReadOnly();
    }

    internal static FlightHunkImage VerifyAndRelocate(FlightHunkImage hunks, out FlightImage reference)
    {
        if (hunks.Hunks.Count != 185 || hunks.FlatImage.Length != 298988 || hunks.Relocations.Count != 8441)
            throw new FlightDataException("layout.counts", $"Incompatible executable: {hunks.Hunks.Count} hunks, {hunks.FlatImage.Length} image bytes, {hunks.Relocations.Count} relocations; expected 185/298988/8441.");
        foreach (var h in hunks.Hunks)
        {
            var s = Segments[h.Index];
            string expectedType = h.Index switch
            {
                1 or 78 or 81 or 84 or 88 or 91 or 94 or 97 or 100 or 103 or 106 or 109 or 112 or 115 or 118 or 121 or 124 or 127 or 130 or 133 or 136 or 139 or 142 or 145 or 148 or 156 or 168 or 176 or 178 or 180 or 182 or 183 => "DATA",
                2 or 79 or 82 or 85 or 89 or 92 or 95 or 98 or 101 or 104 or 107 or 110 or 113 or 116 or 119 or 122 or 125 or 128 or 131 or 134 or 137 or 140 or 143 or 146 or 149 or 157 or 169 => "BSS",
                _ => "CODE"
            };
            if (h.FlatAddress != s.FlatAddress || h.AllocatedBytes != s.ByteCount || h.Type != expectedType)
                throw new FlightDataException("layout.hunk", $"Hunk {h.Index} does not match the pinned reference layout.");
        }
        var bytes = hunks.FlatImage.Bytes.ToArray();
        var relocs = new List<FlightRelocation>(hunks.Relocations.Count);
        int pitch = 0, trig = 0;
        foreach (var r in hunks.Relocations)
        {
            uint v = unchecked(r.Addend + Segments[r.TargetHunk].RamAddress);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(r.FlatAddress - 0x10000, 4), v);
            relocs.Add(r with { ReferenceValue = v });
            if (r.FlatAddress >= 0x3249A && r.FlatAddress + 4 <= 0x325B8) pitch++;
            if (r.FlatAddress >= 0x3AE92 && r.FlatAddress + 4 <= 0x49A12) trig++;
        }
        if (pitch != 3 || trig != 337) throw new FlightDataException("layout.spans", $"Required span relocations differ: pitch {pitch}/3, trig {trig}/337.");
        reference = new FlightImage(bytes);
        return new FlightHunkImage(hunks.FlatImage, hunks.Hunks.ToList(), relocs, hunks.Symbols.ToList());
    }
}
