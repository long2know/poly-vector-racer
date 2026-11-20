namespace StuntRacer.Core.City;

public sealed record CityTrafficSnapshot(string Id, string Style, CarSnapshot Car);

public sealed record WalkerSnapshot(string Id, double X, double Y, double Z, double Yaw, double Phase);

// Actor IDs/order are stable for a session. Checkpoints is one lap; CheckpointCount includes every lap.
// All poses here are authoritative; Snapshot/Advance supply the interpolated player and physical rival.
public sealed record CityActivity(
    string Mode, double DrivenDistance, string CurrentRoad, string District,
    CityTrafficSnapshot[] Traffic, WalkerSnapshot[] Pedestrians,
    string? RouteId, CityCheckpointDefinition[] Checkpoints, int NextCheckpointIndex,
    int CheckpointsPassed, int CheckpointCount, int Lap, int Laps,
    double TimeRemaining, double Elapsed, string Result, bool? Success);
