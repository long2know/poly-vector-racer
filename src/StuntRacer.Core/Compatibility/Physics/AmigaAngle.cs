namespace StuntRacer.Core.Compatibility.Physics;

/// <summary>
/// Wrapping 16-bit angles. Radians/interpolation are display-only and never returned
/// as a rounded state angle. No sine/cosine table or original trig routine is supplied.
/// </summary>
public static class AmigaAngle
{
    public const int FullTurn = 65_536;
    public const int QuarterTurn = 16_384;

    public static ushort Wrap(long raw) => unchecked((ushort)raw);

    /// <summary>Shortest signed delta from/to; an exact half-turn chooses -32768.</summary>
    public static short ShortestDelta(ushort from, ushort to) => unchecked((short)(to - from));

    /// <summary>
    /// Projects the unsigned raw turn into radians with a required +1/-1 direction.
    /// The caller establishes the source-to-display sign; no axis mapping is inferred.
    /// </summary>
    public static double ToDisplayRadians(ushort raw, int directionSign)
    {
        ValidateDirection(directionSign);
        return directionSign * raw * (Math.Tau / FullTurn);
    }

    /// <summary>
    /// Shortest-arc, unwrapped display projection. Alpha must be finite in [0,1].
    /// The endpoint may differ by a full turn from ToDisplayRadians(current).
    /// </summary>
    public static double InterpolateDisplayRadians(
        ushort previous, ushort current, double alpha, int directionSign)
    {
        ValidateDirection(directionSign);
        if (!double.IsFinite(alpha) || alpha is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(alpha));
        return directionSign * (previous + ShortestDelta(previous, current) * alpha) *
               (Math.Tau / FullTurn);
    }

    private static void ValidateDirection(int directionSign)
    {
        if (directionSign is not (1 or -1))
            throw new ArgumentOutOfRangeException(nameof(directionSign), "Use exactly +1 or -1.");
    }
}
