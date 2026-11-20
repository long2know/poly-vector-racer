namespace StuntRacer.Core.SourceRuntime;

internal readonly record struct SourceVector(int X, int Y, int Z);

internal sealed class SourceOrientation
{
    private readonly int[] _sine;

    public SourceOrientation(ReadOnlyMemory<int> sine)
    {
        if (sine.Length is not (65_536 or 81_920))
            throw new ArgumentException("An external 65536- or 81920-entry Q14 sine lookup is required.", nameof(sine));
        _sine = sine.ToArray();
        if (_sine.Any(value => value is < -16_384 or > 16_384) ||
            _sine[0] != 0 || _sine[16_384] != 16_384 ||
            _sine[32_768] != 0 || _sine[49_152] != -16_384)
            throw new ArgumentException("The external Q14 sine lookup has invalid range or cardinal values.", nameof(sine));
    }

    public int Sin(ushort angle) => _sine[angle];
    public int Cos(ushort angle) => _sine.Length == 81_920 ?
        _sine[angle + 16_384] : _sine[unchecked((ushort)(angle + 16_384))];

    public SourceBasis At(ushort yaw, ushort pitch, ushort roll)
    {
        var sy = Sin(yaw);
        var cy = Cos(yaw);
        var sp = Sin(pitch);
        var cp = Cos(pitch);
        var sr = Sin(roll);
        var cr = Cos(roll);
        var pitchYaw = (long)sp * sy / SourceDynamicsMath.Precision;
        var pitchCosYaw = (long)sp * cy / SourceDynamicsMath.Precision;
        var right = new SourceVector(
            Coefficient((long)cy * cr + pitchYaw * sr),
            Coefficient(-(long)cp * sr),
            Coefficient(-(long)sy * cr + pitchCosYaw * sr));
        var up = new SourceVector(
            Coefficient((long)cy * sr - pitchYaw * cr),
            Coefficient((long)cp * cr),
            Coefficient(-(long)sy * sr - pitchCosYaw * cr));
        var forward = new SourceVector(
            Coefficient((long)sy * cp), sp, Coefficient((long)cy * cp));
        return new(right, up, forward);
    }

    private static int Coefficient(long numerator) =>
        unchecked((short)(numerator / SourceDynamicsMath.Precision));
}

internal readonly record struct SourceBasis(SourceVector Right, SourceVector Up, SourceVector Forward)
{
    public SourceVector ToBody(SourceVector world) => new(Dot(world, Right), Dot(world, Up), Dot(world, Forward));

    public SourceVector ToWorld(SourceVector body) => new(
        Dot(body, new(Right.X, Up.X, Forward.X)),
        Dot(body, new(Right.Y, Up.Y, Forward.Y)),
        Dot(body, new(Right.Z, Up.Z, Forward.Z)));

    private static int Dot(SourceVector a, SourceVector b) =>
        unchecked(SourceDynamicsMath.MultiplyQ14(a.X, b.X) +
                  SourceDynamicsMath.MultiplyQ14(a.Y, b.Y) +
                  SourceDynamicsMath.MultiplyQ14(a.Z, b.Z));
}
