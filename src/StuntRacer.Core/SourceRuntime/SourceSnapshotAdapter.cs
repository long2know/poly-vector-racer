namespace StuntRacer.Core.SourceRuntime;

/// <summary>
/// Source-to-canonical presentation only. Raw player Y is up; the old renderer's
/// negated view-coordinate pointer is deliberately not a physical coordinate.
/// </summary>
public static class SourceSnapshotAdapter
{
    public static int ToWheelRoadHeight(long nativeHeightQ14) =>
        checked((int)((nativeHeightQ14 / 2) >> 11));

    public static double ToDisplayXz(int raw, double metresPerPcUnit)
    {
        ValidateScale(metresPerPcUnit);
        return raw / 16_384.0 * metresPerPcUnit;
    }

    public static double ToDisplayY(int raw, double metresPerPcUnit)
    {
        ValidateScale(metresPerPcUnit);
        return raw / 4096.0 * metresPerPcUnit;
    }

    internal static CarSnapshot Project(SourcePlayerRawState state, SourceAmigaPackage package)
    {
        var scale = package.Units.DisplayMetresPerPcUnit;
        var speed = Math.Sqrt((double)state.WorldVelocityX * state.WorldVelocityX +
                              (double)state.WorldVelocityZ * state.WorldVelocityZ) *
                    (476.0 / 16_384) * scale / package.Clock.SimulationStepSeconds;
        // The existing authored body mesh has an elevated centre. This is a
        // presentation origin offset, not a spring rest length or physics input.
        var bodyOriginLift = 64 * scale;
        return new(
            ToDisplayXz(state.X, scale), ToDisplayY(state.Y, scale) + bodyOriginLift,
            ToDisplayXz(state.Z, scale), state.Yaw * (Math.Tau / 65_536),
            unchecked((short)state.Pitch) * (Math.Tau / 65_536),
            -unchecked((short)state.Roll) * (Math.Tau / 65_536),
            speed, state.Steering / 15.0,
            package.StandardBoostReserve == 0 ? 0 : Math.Clamp(state.BoostReserve / (double)package.StandardBoostReserve, 0, 1),
            Math.Clamp(state.Damage / 255.0, 0, 1), state.TouchingRoad, state.Boosting,
            [Suspension(state.FrontLeft), Suspension(state.FrontRight),
                Suspension(state.Rear), Suspension(state.Rear)]);
    }

    internal static CarSnapshot Interpolate(
        SourcePlayerRawState previous, SourcePlayerRawState current, SourceAmigaPackage package, double alpha)
    {
        var b = Project(current, package);
        if (previous.Recoveries != current.Recoveries)
            return b;
        var a = Project(previous, package);
        return b with
        {
            X = Lerp(a.X, b.X, alpha),
            Y = Lerp(a.Y, b.Y, alpha),
            Z = Lerp(a.Z, b.Z, alpha),
            Yaw = Angle(a.Yaw, b.Yaw, alpha),
            Pitch = Angle(a.Pitch, b.Pitch, alpha),
            Roll = Angle(a.Roll, b.Roll, alpha),
            Suspension =
            [
                Lerp(a.Suspension[0], b.Suspension[0], alpha),
                Lerp(a.Suspension[1], b.Suspension[1], alpha),
                Lerp(a.Suspension[2], b.Suspension[2], alpha),
                Lerp(a.Suspension[3], b.Suspension[3], alpha)
            ]
        };
    }

    private static double Suspension(SourcePlayerContact contact) => Math.Clamp(contact.Penetration / 1024.0, 0, 1);
    private static double Lerp(double a, double b, double alpha) => a + (b - a) * alpha;
    private static double Angle(double a, double b, double alpha) => a + Math.IEEERemainder(b - a, Math.Tau) * alpha;

    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
    }
}
