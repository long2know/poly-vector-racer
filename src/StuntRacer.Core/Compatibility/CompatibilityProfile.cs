namespace StuntRacer.Core.Compatibility;

public enum CompatibilityQualification
{
    AuthoredApproximation,
    UnqualifiedCandidate
}

public sealed class CompatibilityProfile
{
    public const string AuthoredId = "authored";
    public const string AmigaExperimentalId = "amiga-experimental";

    public static CompatibilityProfile Authored { get; } = new(
        AuthoredId, "Authored approximation — not original Amiga physics",
        1.0 / 120.0, CompatibilityQualification.AuthoredApproximation);

    public static CompatibilityProfile AmigaExperimental { get; } = new(
        AmigaExperimentalId, "Amiga compatibility candidate — unqualified; timing unknown",
        null, CompatibilityQualification.UnqualifiedCandidate);

    private CompatibilityProfile(string id, string displayName, double? simulationStepSeconds,
        CompatibilityQualification qualification)
    {
        Id = id;
        DisplayName = displayName;
        SimulationStepSeconds = simulationStepSeconds;
        Qualification = qualification;
    }

    public string Id { get; }
    public string DisplayName { get; }

    // Unknown source timing is not permission to use the authored or display clock.
    public double? SimulationStepSeconds { get; }
    public CompatibilityQualification Qualification { get; }
}

public sealed record CompatibilityAssetProvenance(
    string Role,
    long ByteLength,
    string Sha256,
    string SourceId,
    string? SourceRevision);
