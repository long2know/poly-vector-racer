namespace StuntRacer.Core;

internal sealed class ModernCheckpointTrial(double trackLength, int checkpointCount)
{
    // Before the finish gate: 8 + length/20 seconds, i.e. 8 + 1.45*(length/29).
    private readonly double _bonus = trackLength / (checkpointCount * 20.0);
    private double _budget = 8 + trackLength / (checkpointCount * 20.0);

    public int CheckpointsPassed { get; private set; }
    public bool? Success { get; private set; }
    public double LastBonus { get; private set; }

    public bool Expired(double elapsed) => elapsed >= _budget;

    public bool Pass(double crossingElapsed)
    {
        // Expiry wins a tie. A late crossing cannot resurrect an expired clock.
        if (Success is not null || Expired(crossingElapsed) || CheckpointsPassed >= checkpointCount)
            return false;
        CheckpointsPassed++;
        LastBonus = _bonus;
        _budget += _bonus;
        return true;
    }

    public void Finish(bool success) => Success = success;

    public TrialSnapshot Snapshot(double elapsed) => new(
        Math.Max(0, _budget - elapsed), CheckpointsPassed, checkpointCount,
        CheckpointsPassed >= checkpointCount ? 0 : (CheckpointsPassed + 1) % checkpointCount / (double)checkpointCount,
        LastBonus, Success, elapsed);
}
