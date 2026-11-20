namespace StuntRacer.Core.SourceRuntime;

public readonly record struct SourceContactResponse(int Penetration, int Response);

/// <summary>
/// Fresh mathematical implementation of the pinned, unretuned reference's scalar
/// rules. These are not the archived ORIGINAL integration branch or stock certification.
/// </summary>
public static class SourceDynamicsMath
{
    public const int Precision = 16_384;
    public const int Gravity = 317;
    public const int Reduction = 238;
    public const int Increase = 276;
    public const int CarHalfWidth = 64;
    public const int CarHalfLength = 128;
    public const int EnginePower = 240;
    public const int ForwardSpeedLimit = 30_720;

    public static int IntegrateHorizontal(int position, int newVelocity, int pcFactor = 2)
    {
        if (pcFactor != 2)
            throw new ArgumentOutOfRangeException(nameof(pcFactor), "This reference uses PC_FACTOR=2.");
        return unchecked((int)(position + (long)newVelocity * Reduction * pcFactor));
    }

    public static int IntegrateVertical(int position, int newVelocity) =>
        unchecked((int)(position + (((long)newVelocity * Reduction) >> 1)));

    public static int AdvanceVelocity(int velocity, int acceleration) =>
        unchecked((int)(velocity + (((long)acceleration * Reduction) >> 8)));

    public static int TwiceCollisionYAcceleration(bool touchingRoad, int collisionY) =>
        touchingRoad ? unchecked(collisionY * 2) : 0;

    public static SourceContactResponse ContactResponse(
        int roadHeight, int actualHeight, int previousPenetration, int wreckReduction = 0)
    {
        var penetration = (int)Math.Clamp((long)roadHeight - actualHeight - wreckReduction, -768, 5120);
        var response = Math.Max(0, penetration + (((long)penetration - previousPenetration) * Increase >> 8));
        return new(penetration, (int)response);
    }

    public static int AverageResponse(int frontLeft, int frontRight, int rear) =>
        unchecked(((frontLeft + frontRight) >> 1) + rear) >> 1;

    public static int RollResponse(int frontLeft, int frontRight) =>
        (int)Math.Clamp(3L * (frontLeft - frontRight), -4096, 4096);

    public static int PitchResponse(int frontLeft, int frontRight, int rear) =>
        unchecked(((frontLeft + frontRight) >> 1) - rear);

    public static int MultiplyQ14(int value, int coefficient) =>
        unchecked((int)(((long)value * coefficient) >> 14));

    public static int MultiplyQ8(int value, int coefficient) =>
        unchecked((int)(((long)value * coefficient) >> 8));

    internal static int SumQ14(int a, int aCoefficient, int b, int bCoefficient) =>
        unchecked((int)(((long)a * aCoefficient + (long)b * bCoefficient) >> 14));

    internal static int ClampSignedWord(long value) => (int)Math.Clamp(value, short.MinValue, short.MaxValue);
    internal static ushort WrapAngle(int value) => unchecked((ushort)value);
}
