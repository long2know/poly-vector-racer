namespace StuntRacer.Engine;

public sealed class FixedStepClock
{
    private readonly int _maximumSteps;
    private double _accumulator;

    public FixedStepClock(double stepSeconds = 1.0 / 120.0, double maximumFrameSeconds = 0.25)
    {
        if (!double.IsFinite(stepSeconds) || stepSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepSeconds));
        if (!double.IsFinite(maximumFrameSeconds) || maximumFrameSeconds < stepSeconds ||
            maximumFrameSeconds / stepSeconds > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameSeconds));

        StepSeconds = stepSeconds;
        MaximumFrameSeconds = maximumFrameSeconds;
        _maximumSteps = (int)Math.Ceiling(maximumFrameSeconds / stepSeconds);
    }

    public double StepSeconds { get; }
    public double MaximumFrameSeconds { get; }
    public double Alpha => Math.Clamp(_accumulator / StepSeconds, 0, 1);
    public long Tick { get; private set; }

    public int Advance(double seconds, Func<double, bool> tryStep) =>
        Advance(seconds, MaximumFrameSeconds, _maximumSteps, tryStep);

    /// <summary>Advance simulated seconds with a per-call step budget, retaining the usual step size.</summary>
    public int Advance(double seconds, int maximumSteps, Func<double, bool> tryStep)
    {
        if (maximumSteps is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumSteps));
        return Advance(seconds, maximumSteps * StepSeconds, maximumSteps, tryStep);
    }

    private int Advance(double seconds, double frameBudget, int maximumSteps, Func<double, bool> tryStep)
    {
        ValidateDelta(seconds);
        ArgumentNullException.ThrowIfNull(tryStep);

        // A stall loses wall time beyond this budget, rather than banking unbounded catch-up.
        _accumulator += Math.Min(seconds, frameBudget);
        var steps = 0;
        while (_accumulator + StepSeconds * 1e-9 >= StepSeconds && steps < maximumSteps)
        {
            if (!tryStep(StepSeconds))
            {
                _accumulator = 0;
                break;
            }

            _accumulator = Math.Max(0, _accumulator - StepSeconds);
            Tick++;
            steps++;
        }

        return steps;
    }

    /// <summary>Discard unstepped time without resetting the authoritative tick count.</summary>
    public void DiscardRemainder() => _accumulator = 0;

    public void Reset()
    {
        DiscardRemainder();
        Tick = 0;
    }

    public static void ValidateDelta(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Elapsed time must be finite and nonnegative.");
    }
}
