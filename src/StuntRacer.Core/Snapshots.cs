namespace StuntRacer.Core;

public readonly record struct DriverInput(double Steer, double Throttle, double Brake, bool Boost);

public sealed record TrackPoint(
    double S, double X, double Y, double Z, double Heading, double Bank, double Width, bool Road);

public readonly record struct TrackSurfaceVertex(double X, double Y, double Z);

public readonly record struct TrackSurfaceEdge(TrackSurfaceVertex Left, TrackSurfaceVertex Right);

public sealed record CarSnapshot(
    double X, double Y, double Z, double Yaw, double Pitch, double Roll, double Speed,
    double Steer, double Boost, double Damage, bool Grounded, bool Boosting, double[] Suspension);

public sealed record RaceSnapshot(
    string Mode, int Position, int Participants, int RivalLap, bool? PlayerWon,
    string Result, double RivalProgress, double RivalTime, int RivalRecoveries, double Elapsed);

public sealed record TrialSnapshot(
    double TimeRemaining, int CheckpointsPassed, int CheckpointCount, double NextCheckpointProgress,
    double LastBonus, bool? Success, double Elapsed);

public sealed record GhostSnapshot(
    CarSnapshot? Car, double Duration, int SampleCount, bool Enabled, string Status);

public sealed record GameSnapshot(
    CarSnapshot Player, CarSnapshot? PaceCar, string Phase, double Time, int Lap, int Laps,
    double LapTime, double BestLap, double Progress, double Countdown, double Impact,
    int Recoveries, double Alpha, long Tick, string Message, RaceSnapshot? Race = null,
    TrialSnapshot? Trial = null, GhostSnapshot? Ghost = null);
