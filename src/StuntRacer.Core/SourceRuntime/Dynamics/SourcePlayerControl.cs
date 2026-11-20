namespace StuntRacer.Core.SourceRuntime;

public readonly record struct SourcePlayerControlState(
    bool AcceleratingLatch,
    int EnginePower,
    int Steering,
    int BoostReserve,
    int BoostSubunit,
    bool Boosting);

/// <summary>Reference button/latch and signed boost-subcounter rules, independently implemented.</summary>
public static class SourcePlayerControl
{
    public static SourcePlayerControlState Step(
        SourcePlayerControlState previous, DriverInput input, int forwardSpeed,
        bool touchingRoad, bool onChains = false, int wreckReduction = 0)
    {
        Validate(input);
        var accelerating = input.Throttle > 0;
        var braking = !accelerating && input.Brake > 0;
        var latch = previous.AcceleratingLatch;
        var power = 0;
        if (forwardSpeed < SourceDynamicsMath.ForwardSpeedLimit && !onChains && wreckReduction == 0)
        {
            if (accelerating)
            {
                latch = true;
                power = SourceDynamicsMath.EnginePower;
            }
            else if (braking)
            {
                latch = false;
                power = -SourceDynamicsMath.EnginePower;
            }
            else if (latch)
                power = SourceDynamicsMath.EnginePower;
        }
        var steering = !touchingRoad || onChains ? 0 : input.Steer > 0 ? 15 : input.Steer < 0 ? -15 : 0;

        var reserve = previous.BoostReserve;
        var subunit = previous.BoostSubunit;
        var boosting = input.Boost && wreckReduction == 0 && (latch || accelerating || braking) && reserve > 0;
        if (boosting)
        {
            subunit--;
            if (subunit < 0)
            {
                subunit = 16;
                reserve--;
            }
            power *= 2;
        }
        return new(latch, power, steering, reserve, subunit, boosting);
    }

    internal static void Validate(DriverInput input)
    {
        if (!double.IsFinite(input.Steer) || !double.IsFinite(input.Throttle) || !double.IsFinite(input.Brake))
            throw new ArgumentOutOfRangeException(nameof(input), "Driver axes must be finite.");
    }
}
