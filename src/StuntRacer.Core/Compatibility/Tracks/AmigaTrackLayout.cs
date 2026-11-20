using System.Buffers.Binary;

namespace StuntRacer.Core.Compatibility.Tracks;

public readonly record struct AmigaTrackSection(
    int Index,
    byte RawPosition,
    byte RawTemplateOrientation,
    byte RawLeftProfile,
    byte RawRightProfile,
    short LeftHeightShift,
    short RightHeightShift)
{
    public int GridX => RawPosition & 15;
    public int GridZ => RawPosition >> 4;
    public int TemplateId => RawTemplateOrientation & 15;
    public int QuarterTurns => RawTemplateOrientation >> 6;
    public bool Reversed => (RawTemplateOrientation & 16) != 0;
    public int LeftProfileId => RawLeftProfile & 127;
    public int RightProfileId => RawRightProfile & 127;
    public bool LeftStorageFlag => (RawLeftProfile & 128) != 0;
    public bool RightColourFlag => (RawRightProfile & 128) != 0;
}

public sealed class AmigaTrackLayout
{
    private readonly byte[] _sourceBytes;

    internal AmigaTrackLayout(byte[] sourceBytes, AmigaTrackSection[] sections)
    {
        _sourceBytes = sourceBytes;
        Sections = Array.AsReadOnly(sections);
    }

    public int SectionCount => Sections.Count;
    public int StartSectionIndex => _sourceBytes[1];
    public byte StandardBoostReserve => _sourceBytes[802];
    public byte SuperBoostReserve => _sourceBytes[803];
    public IReadOnlyList<AmigaTrackSection> Sections { get; }
    public ReadOnlyMemory<byte> SourceBytes => new((byte[])_sourceBytes.Clone());
}

public static class AmigaTrackLayoutReader
{
    public const int ByteLength = 804;
    public const int MaximumSectionCount = 100;

    public static AmigaTrackLayout Read(ReadOnlySpan<byte> bytes)
        => ReadCore(bytes, allowReferenceMetadataBit: false);

    internal static AmigaTrackLayout ReadReferenceRemake(ReadOnlySpan<byte> bytes)
        => ReadCore(bytes, allowReferenceMetadataBit: true);

    private static AmigaTrackLayout ReadCore(ReadOnlySpan<byte> bytes, bool allowReferenceMetadataBit)
    {
        if (bytes.Length != ByteLength)
            throw new InvalidDataException(
                $"A source-format layout must contain exactly {ByteLength} bytes; received {bytes.Length}.");
        var count = bytes[0];
        if (count is < 1 or > MaximumSectionCount)
            throw new InvalidDataException("Layout section count must be in 1..100.");
        if (bytes[1] >= count)
            throw new InvalidDataException("Layout start section must be below its section count.");

        var sections = new AmigaTrackSection[count];
        for (var i = 0; i < count; i++)
        {
            var orientation = bytes[102 + i];
            if (!allowReferenceMetadataBit && (orientation & 32) != 0)
                throw new InvalidDataException(
                    $"Section {i} uses unsupported template/orientation bit 5 (0x20).");
            sections[i] = new AmigaTrackSection(
                i, bytes[2 + i], orientation, bytes[202 + i], bytes[302 + i],
                BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(402 + i * 2, 2)),
                BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(602 + i * 2, 2)));
        }
        return new AmigaTrackLayout(bytes.ToArray(), sections);
    }
}
