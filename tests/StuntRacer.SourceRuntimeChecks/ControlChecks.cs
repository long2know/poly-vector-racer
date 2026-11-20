using StuntRacer.CompatibilityChecks;
using StuntRacer.Core;
using StuntRacer.Core.SourceRuntime;

namespace StuntRacer.SourceRuntimeChecks;

internal static class ControlChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("source controls / acceleration latch, braking and button priority", LatchAndBrake);
        yield return new("source controls / boost first charge, final reserve and no recharge", BoostBoundaries);
        yield return new("source controls / right-positive steer depends on previous road contact", SteeringAndValidation);
    }

    private static void LatchAndBrake()
    {
        var initial = new SourcePlayerControlState(false, 0, 0, 2, 0, false);
        var throttle = SourcePlayerControl.Step(initial, new(0, 1, 0, false), 0, touchingRoad: true);
        Check.Require(throttle.AcceleratingLatch && throttle.EnginePower == 240, "throttle did not set the source latch/engine");
        var coast = SourcePlayerControl.Step(throttle, new(0, 0, 0, false), 0, touchingRoad: true);
        Check.Require(coast.AcceleratingLatch && coast.EnginePower == 240, "release must retain source acceleration latch");
        var brake = SourcePlayerControl.Step(coast, new(0, 0, 1, false), 0, touchingRoad: true);
        Check.Require(!brake.AcceleratingLatch && brake.EnginePower == -240, "brake must clear latch and reverse engine power");
        var both = SourcePlayerControl.Step(brake, new(0, 1, 1, false), 0, touchingRoad: true);
        Check.Require(both.AcceleratingLatch && both.EnginePower == 240, "acceleration button must win simultaneous brake");
        Check.Equal(0, SourcePlayerControl.Step(initial, new(0, 1, 0, false), 30720, true).EnginePower, "source speed-limit gate");
        Check.Equal(0, SourcePlayerControl.Step(initial, new(0, 1, 0, false), 0, true, onChains: true).EnginePower, "chains gate");
        Check.Equal(0, SourcePlayerControl.Step(initial, new(0, 1, 0, false), 0, true, wreckReduction: 512).EnginePower, "wreck gate");
    }

    private static void BoostBoundaries()
    {
        var state = new SourcePlayerControlState(true, 240, 0, 2, 0, false);
        for (var activation = 1; activation <= 19; activation++)
        {
            state = SourcePlayerControl.Step(state, new(0, 1, 0, true), 0, true);
            foreach (var expected in ReferenceCases.Boost.Where(item => item.Activation == activation))
            {
                Check.Equal(expected.Reserve, state.BoostReserve, $"boost activation {activation} reserve");
                Check.Equal(expected.Subunit, state.BoostSubunit, $"boost activation {activation} signed subunit");
                Check.Equal(expected.EnginePower, state.EnginePower, $"boost activation {activation} engine power");
                Check.Equal(expected.BoostFlag != 0, state.Boosting, $"boost activation {activation} boundary flag");
            }
        }
        for (var tick = 0; tick < 100; tick++)
            state = SourcePlayerControl.Step(state, new(0, 0, 0, false), 0, true);
        Check.Require(state.BoostReserve == 0 && state.BoostSubunit == 16 && !state.Boosting,
            "depleted source reserve regenerated or pulsed free boost");
        var idle = SourcePlayerControl.Step(new(false, 0, 0, 2, 0, false), new(0, 0, 0, true), 0, true);
        Check.Require(idle.BoostReserve == 2 && idle.BoostSubunit == 0 && !idle.Boosting,
            "boost without a drive/brake/latch request consumed fuel");
    }

    private static void SteeringAndValidation()
    {
        var initial = new SourcePlayerControlState(false, 0, 0, 0, 0, false);
        Check.Equal(15, SourcePlayerControl.Step(initial, new(1, 0, 0, false), 0, true).Steering, "+steer means right");
        Check.Equal(-15, SourcePlayerControl.Step(initial, new(-1, 0, 0, false), 0, true).Steering, "-steer means left");
        Check.Equal(0, SourcePlayerControl.Step(initial, new(1, 0, 0, false), 0, false).Steering, "airborne previous contact blocks steer");
        foreach (var input in new DriverInput[]
                 {
                     new(double.NaN, 0, 0, false), new(0, double.PositiveInfinity, 0, false),
                     new(0, 0, double.NegativeInfinity, false)
                 })
            Check.Throws<ArgumentOutOfRangeException>(() => SourcePlayerControl.Step(initial, input, 0, true),
                "nonfinite source input");
    }
}
