namespace StuntRacer.Core.Stunts;

/// <summary>
/// Actual stunt/contact events, not predicted animation. Times are seconds, NextGateProgress
/// is canonical route distance / length, and NormalLoad is unilateral road reaction in g.
/// Clean complete circuits alone can promote BestTime, BestScore and the recorded ghost.
/// </summary>
public sealed record StuntChallengeSnapshot(
    string Mode, int Score, int LoopsCompleted, int Jumps, int CleanLandings,
    double AirTime, double CurrentAirTime, string Feature, string LastStunt,
    int GatesPassed, int GateCount, string NextGate, double NextGateProgress,
    bool? Success, bool CleanRun, double BestTime, int BestScore, bool RecordedBest,
    double NormalLoad);
