using System.Text.Json.Serialization;

namespace StuntRacer.Flight;

/// <summary>Selection metadata for an authored game airframe. Maximum Mach is a World Flight limit.</summary>
public sealed class FlightAircraftProfile
{
    internal FlightAircraftProfile(string id, string name, double maximumMach, FlightAircraftTuning tuning)
    {
        Id = id;
        Name = name;
        MaximumMach = maximumMach;
        Tuning = tuning;
    }

    [JsonPropertyName("id")]
    public string Id { get; }
    [JsonPropertyName("name")]
    public string Name { get; }
    [JsonPropertyName("maximumMach")]
    public double MaximumMach { get; }

    internal FlightAircraftTuning Tuning { get; }
}

internal sealed record FlightAircraftTuning(
    double DryThrust,
    double WetThrust,
    double BoostAcceleration,
    double RollAuthority,
    double FastRollReduction,
    double FastPitchAuthority,
    double HighAltitudeBoost = 0);
