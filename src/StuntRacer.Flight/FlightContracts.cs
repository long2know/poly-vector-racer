using System.Text.Json.Serialization;

namespace StuntRacer.Flight;

public sealed record FlightInput(
    double Roll,
    double Pitch,
    double Yaw,
    double Throttle,
    bool Afterburner,
    bool Brake)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Assisted { get; init; }

    internal FlightInput Normalize()
    {
        RequireFinite(Roll, nameof(Roll));
        RequireFinite(Pitch, nameof(Pitch));
        RequireFinite(Yaw, nameof(Yaw));
        RequireFinite(Throttle, nameof(Throttle));
        return this with
        {
            Roll = Math.Clamp(Roll, -1, 1),
            Pitch = Math.Clamp(Pitch, -1, 1),
            Yaw = Math.Clamp(Yaw, -1, 1),
            Throttle = Math.Clamp(Throttle, 0, 1)
        };
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Flight controls must be finite.");
    }
}

public sealed record FlightVector(double X, double Y, double Z);

public sealed record AircraftSnapshot(
    double X,
    double Y,
    double Z,
    double Yaw,
    double Pitch,
    double Roll,
    double Speed,
    double VerticalSpeed,
    double Throttle,
    double Fuel,
    double GForce,
    bool Afterburner,
    bool Stalled,
    bool OnGround,
    bool GearDown,
    bool Crashed,
    FlightVector Forward,
    FlightVector Up)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Mach { get; init; }
}

public sealed record FlightDisplaySnapshot(
    int SpeedKnots, int AltitudeFeet, int HeadingDegrees, int ThrustPercent, int FuelGauge);

public sealed record FlightConfigurationSnapshot(bool HookDown, bool OnCarrier, int GearProgress);

public sealed record AtlasDestination(
    [property: JsonRequired] double LonDeg,
    [property: JsonRequired] double LatDeg);

public sealed record AtlasAutopilotCommand(
    string Action,
    string? Mode = null,
    AtlasDestination? Destination = null,
    double? HeadingDeg = null,
    double? AltitudeM = null,
    double? SpeedMps = null,
    double? Rate = null);

public sealed record AtlasAutopilotTarget(double X, double Z);

public sealed record AtlasAutopilotSnapshot(
    string Status, string Reason, string? Mode, int RequestedRate, double EffectiveRate,
    AtlasAutopilotTarget? Target, double? RemainingM, double FuelRateScale,
    double? SpeedMps, double? AltitudeM, double? HeadingDeg)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ProjectedFuel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlayEtaSeconds { get; init; }
}

public sealed record FlightSnapshot(
    AircraftSnapshot Aircraft,
    string Phase,
    double Time,
    double Countdown,
    double Alpha,
    long Tick,
    int Landings,
    int Crashes,
    int Recoveries,
    string Message,
    FlightDisplaySnapshot? Display = null,
    FlightConfigurationSnapshot? Configuration = null,
    FlightWorldActivity? Activity = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlightRangeState? Training { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlightDuelState? Duel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AtlasAutopilotSnapshot? AtlasAutopilot { get; init; }
}
