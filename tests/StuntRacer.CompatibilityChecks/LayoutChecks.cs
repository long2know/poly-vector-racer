using StuntRacer.Core.Compatibility.Tracks;

namespace StuntRacer.CompatibilityChecks;

internal static class LayoutChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("layout / signed big-endian shifts and separate raw flags", FieldsAndEndian);
        yield return new("layout / exact byte count and valid count/start", InvalidLengthAndHeader);
        yield return new("layout / unknown orientation semantics rejected", ReservedOrientation);
        yield return new("layout / raw input is defensively retained", ImmutableInput);
    }

    private static void FieldsAndEndian()
    {
        var bytes = SyntheticLayout.Create(
            new(0xd2, 0x10, 0x80, 0x01, short.MinValue, short.MaxValue),
            new(0x3f, 0xc1, 0x02, 0x83, 258, -257),
            new(0x10, 0x83, 0x04, 0x05, -1, 0),
            new(0x07, 0x54, 0x06, 0x87, 123, -456));
        bytes[1] = 2;
        var layout = AmigaTrackLayoutReader.Read(bytes);
        Check.Equal(4, layout.SectionCount, "layout.sectionCount");
        Check.Equal(2, layout.StartSectionIndex, "layout.startSectionIndex");
        Check.Equal(7, layout.StandardBoostReserve, "layout.standardBoostReserve");
        Check.Equal(11, layout.SuperBoostReserve, "layout.superBoostReserve");
        Check.Equal(4, layout.Sections.Count(), "layout.sections count");
        Check.Require(layout.SourceBytes.ToArray().SequenceEqual(bytes), "layout.sourceBytes changed raw input");

        var first = layout.Sections[0];
        Check.Equal(0, first.Index, "section[0].index");
        Check.Equal(2, first.GridX, "position low nibble x");
        Check.Equal(13, first.GridZ, "position high nibble z");
        Check.Equal(0, first.TemplateId, "template low nibble");
        Check.Equal(0, first.QuarterTurns, "quarter-turn zero");
        Check.Require(first.Reversed, "traversal bit 4 lost");
        Check.Equal((byte)0xd2, first.RawPosition, "raw position");
        Check.Equal((byte)0x10, first.RawTemplateOrientation, "raw orientation");
        Check.Equal((byte)0x80, first.RawLeftProfile, "left high-bit storage flag");
        Check.Equal((byte)0x01, first.RawRightProfile, "right colour flag must not be copied from left");
        Check.Equal(0, first.LeftProfileId, "left masked profile id");
        Check.Equal(1, first.RightProfileId, "right masked profile id");
        Check.Equal(short.MinValue, first.LeftHeightShift, "signed BE -32768");
        Check.Equal(short.MaxValue, first.RightHeightShift, "signed BE +32767");

        var second = layout.Sections[1];
        Check.Equal(15, second.GridX, "second low nibble");
        Check.Equal(3, second.GridZ, "second high nibble");
        Check.Equal(1, second.TemplateId, "second template id");
        Check.Equal(3, second.QuarterTurns, "bits 6..7");
        Check.Require(!second.Reversed, "quarter turns must not set traversal reversal");
        Check.Equal((byte)0x02, second.RawLeftProfile, "left byte not blanket swapped");
        Check.Equal((byte)0x83, second.RawRightProfile, "right high bit retained");
        Check.Equal(2, second.LeftProfileId, "left id");
        Check.Equal(3, second.RightProfileId, "right id");
        Check.Equal((short)258, second.LeftHeightShift, "0x0102 big endian");
        Check.Equal((short)-257, second.RightHeightShift, "0xfeff sign extension");
        Check.Equal(2, layout.Sections[2].QuarterTurns, "half turn");
        Check.Equal((short)-1, layout.Sections[2].LeftHeightShift, "0xffff sign extension");
        Check.Equal(1, layout.Sections[3].QuarterTurns, "quarter turn with reverse");
        Check.Require(layout.Sections[3].Reversed, "reversal with orientation");
    }

    private static void InvalidLengthAndHeader()
    {
        foreach (var length in new[] { 0, 1, 2, 803, 805, 1608 })
        {
            var bytes = new byte[length];
            if (length > 0)
                bytes[0] = 1;
            Check.Throws<InvalidDataException>(() => AmigaTrackLayoutReader.Read(bytes),
                $"layout length {length}");
        }

        foreach (var count in new byte[] { 0, 101, 255 })
        {
            var bytes = SyntheticLayout.Create(new SyntheticSection(0, 0));
            bytes[0] = count;
            Check.Throws<InvalidDataException>(() => AmigaTrackLayoutReader.Read(bytes), $"section count {count}");
        }

        foreach (var start in new byte[] { 2, 100, 255 })
        {
            var bytes = SyntheticLayout.Create(new(0, 0), new(1, 0));
            bytes[1] = start;
            Check.Throws<InvalidDataException>(() => AmigaTrackLayoutReader.Read(bytes), $"start index {start}");
        }
    }

    private static void ReservedOrientation()
    {
        var bytes = SyntheticLayout.Create(new SyntheticSection(0, 0x20));
        Check.Throws<InvalidDataException>(() => AmigaTrackLayoutReader.Read(bytes),
            "reserved template/orientation bit 5");
    }

    private static void ImmutableInput()
    {
        var bytes = SyntheticLayout.Create(new SyntheticSection(0x43, 0x40, 0x80, 0x81, -2, 513));
        var before = (byte[])bytes.Clone();
        var layout = AmigaTrackLayoutReader.Read(bytes);
        Array.Fill(bytes, (byte)0xff);
        Check.Require(layout.SourceBytes.ToArray().SequenceEqual(before),
            "layout.sourceBytes aliases caller-owned array");
        Check.Equal(3, layout.Sections[0].GridX, "decoded x mutated with source bytes");
        Check.Equal((short)-2, layout.Sections[0].LeftHeightShift, "decoded shift mutated with source bytes");
    }
}
