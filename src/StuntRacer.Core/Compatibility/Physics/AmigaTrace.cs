using System.Collections.ObjectModel;

namespace StuntRacer.Core.Compatibility.Physics;

public sealed record AmigaTraceBaseline(string? ExecutableSha256, string? Version, string? Region);

/// <summary>
/// A declared rational number of seconds per logical transition, or explicit unknown.
/// This neither sets a simulation clock nor establishes wall-pacing/race-timer rules.
/// </summary>
public sealed record AmigaTraceTiming(
    string Status, long? Numerator, long? Denominator, string? EvidenceSha256);

/// <summary>Declared capture provenance; a hash is an identifier, not authentication.</summary>
public sealed record AmigaTraceProvenance(string Kind, string Producer, string? EvidenceSha256);

public sealed record AmigaTraceHeader(
    int SchemaVersion,
    string StateSchema,
    long TransitionCount,
    AmigaTraceBaseline Baseline,
    string? TablesSha256,
    string? InputSha256,
    string? StateMappingSha256,
    AmigaTraceTiming Timing,
    AmigaTraceProvenance Provenance);

public readonly record struct AmigaTraceTransition(long Tick, AmigaRawInput Input, AmigaRawState After);

/// <summary>Validated immutable trace. Tick zero is the initial state, not a transition.</summary>
public sealed class AmigaTrace
{
    internal AmigaTrace(
        AmigaTraceHeader header, AmigaRawState initialState, List<AmigaTraceTransition> transitions)
    {
        Header = header;
        InitialState = initialState;
        Transitions = new ReadOnlyCollection<AmigaTraceTransition>(transitions);
    }

    public AmigaTraceHeader Header { get; }
    public AmigaRawState InitialState { get; }
    public IReadOnlyList<AmigaTraceTransition> Transitions { get; }
}

/// <summary>
/// Exact observation comparison only. Neither a match nor declared independent
/// provenance qualifies original dynamics, the complete executable, or reuse rights.
/// </summary>
/// <param name="Status">match, mismatch, or blocked.</param>
/// <param name="ComparedTicks">
/// Transitions inspected up to and including a differing transition, excluding tick zero.
/// A length difference counts only the shared transitions.
/// </param>
/// <param name="FirstMismatchTick">Zero for initial-state mismatch; null for metadata/blocked results.</param>
public sealed record AmigaTraceVerification(
    string Status,
    long ComparedTicks,
    long? FirstMismatchTick,
    string Reason,
    string Qualification,
    string OracleBasis,
    string? FirstMismatchField = null,
    string? ExpectedValue = null,
    string? ActualValue = null);
