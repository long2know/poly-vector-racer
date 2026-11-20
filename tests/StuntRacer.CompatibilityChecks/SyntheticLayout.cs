using System.Buffers.Binary;

namespace StuntRacer.CompatibilityChecks;

internal readonly record struct SyntheticSection(
    byte Position, byte TemplateOrientation, byte LeftProfile = 0, byte RightProfile = 0,
    short LeftShift = 0, short RightShift = 0);

internal static class SyntheticLayout
{
    public static byte[] Create(params SyntheticSection[] sections)
    {
        if (sections.Length is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(sections), "Synthetic fixtures use only 1..4 sections.");

        var bytes = new byte[804];
        bytes[0] = checked((byte)sections.Length);
        bytes[802] = 7;
        bytes[803] = 11;
        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            bytes[2 + index] = section.Position;
            bytes[102 + index] = section.TemplateOrientation;
            bytes[202 + index] = section.LeftProfile;
            bytes[302 + index] = section.RightProfile;
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(402 + index * 2, 2), section.LeftShift);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(602 + index * 2, 2), section.RightShift);
        }
        return bytes;
    }
}
