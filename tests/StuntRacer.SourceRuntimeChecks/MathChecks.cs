using StuntRacer.CompatibilityChecks;
using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal static class MathChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("source math / active position branch preserves per-axis precision", ActivePosition);
        yield return new("source math / contact penetration, damping and airborne history", Contact);
        yield return new("source math / three-contact nested response and inclination", ThreeContactResponse);
        yield return new("source math / fixed-point products retain negative shift rounding", Products);
    }

    private static void ActivePosition()
    {
        foreach (var (velocity, horizontal, vertical) in ReferenceCases.ActivePosition)
        {
            Check.Equal(horizontal, SourceDynamicsMath.IntegrateHorizontal(0, velocity),
                $"active horizontal integration velocity {velocity}");
            Check.Equal(vertical, SourceDynamicsMath.IntegrateVertical(0, velocity),
                $"active vertical integration velocity {velocity}");
            Check.Equal(12345 + horizontal, SourceDynamicsMath.IntegrateHorizontal(12345, velocity),
                $"horizontal delta is added to raw position {velocity}");
        }
        Check.Throws<ArgumentOutOfRangeException>(() => SourceDynamicsMath.IntegrateHorizontal(0, 1, 1),
            "unsupported PC_FACTOR cannot silently change source position units");
        Check.Equal(unchecked(int.MaxValue + 476), SourceDynamicsMath.IntegrateHorizontal(int.MaxValue, 1),
            "declared int32 candidate position wraps explicitly");
    }

    private static void Contact()
    {
        foreach (var (difference, previous, response) in ReferenceCases.Contact)
        {
            var actual = SourceDynamicsMath.ContactResponse(difference, 0, previous);
            Check.Equal(difference, actual.Penetration, $"contact raw penetration {difference}/{previous}");
            Check.Equal(response, actual.Response, $"contact source response {difference}/{previous}");
        }
        var airborne = SourceDynamicsMath.ContactResponse(-10000, 0, 0);
        Check.Equal(-768, airborne.Penetration, "airborne negative penetration clamp");
        Check.Equal(0, airborne.Response, "airborne contact cannot pull downward");
        var deep = SourceDynamicsMath.ContactResponse(10000, 0, 5120);
        Check.Equal(5120, deep.Penetration, "positive penetration clamp");
        Check.Equal(5120, deep.Response, "4607 damage cap must not be applied to every contact response");
        var impact = SourceDynamicsMath.ContactResponse(5120, 0, 0);
        Check.Equal(10640, impact.Response, "large contact derivative remains available to damage logic");
        var wreck = SourceDynamicsMath.ContactResponse(100, 0, 50, wreckReduction: 50);
        Check.Equal(50, wreck.Penetration, "wreck reduction precedes derivative");
        Check.Equal(50, wreck.Response, "equal reduced/current penetration has no derivative impulse");
    }

    private static void ThreeContactResponse()
    {
        Check.Equal(350, SourceDynamicsMath.AverageResponse(100, 300, 500),
            "front pair half, then rear half; never a three-contact arithmetic mean");
        Check.Equal(1, SourceDynamicsMath.AverageResponse(0, 3, 2), "nested halves truncate at each source operation");
        Check.Equal(0, SourceDynamicsMath.AverageResponse(1, 0, 0), "tiny one-contact response does not imply weighted road contact");
        Check.Equal(-600, SourceDynamicsMath.RollResponse(100, 300), "signed front contact roll difference");
        Check.Equal(-4096, SourceDynamicsMath.RollResponse(0, 5000), "negative roll acceleration clamp");
        Check.Equal(4096, SourceDynamicsMath.RollResponse(5000, 0), "positive roll acceleration clamp");
        Check.Equal(-300, SourceDynamicsMath.PitchResponse(100, 300, 500), "front average minus rear pitch response");
    }

    private static void Products()
    {
        Check.Equal(-1, SourceDynamicsMath.MultiplyQ14(-1, 8192), "Q14 arithmetic shift is not truncating division");
        Check.Equal(1, SourceDynamicsMath.MultiplyQ14(3, 8192), "Q14 positive half");
        Check.Equal(-2, SourceDynamicsMath.MultiplyQ14(-3, 8192), "Q14 negative half");
        Check.Equal(32767, SourceDynamicsMath.MultiplyQ14(32767, 16384), "Q14 identity coefficient");
        Check.Equal(-1, SourceDynamicsMath.MultiplyQ8(-1, 128), "Q8 arithmetic shift is not truncating division");
        Check.Equal(1, SourceDynamicsMath.MultiplyQ8(3, 128), "Q8 positive half");
        Check.Equal(-2, SourceDynamicsMath.MultiplyQ8(-3, 128), "Q8 negative half");
    }
}
