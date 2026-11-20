using System.Globalization;

namespace StuntRacer.Core.Compatibility.Physics;

/// <summary>
/// Stream-only, exact raw comparison. Both complete traces are validated before
/// comparison, so an earlier difference cannot hide a malformed or extra tail.
/// Original vehicle dynamics are not implemented by this verifier.
/// </summary>
public static class AmigaTraceVerifier
{
    /// <summary>
    /// Leaves streams open. Malformed/unsupported records, widths, hashes or EOF
    /// throw InvalidDataException. Empty streams or missing qualification evidence
    /// return blocked, never match. Synthetic match is explicitly nonqualifying.
    /// </summary>
    public static AmigaTraceVerification Verify(Stream expected, Stream actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (ReferenceEquals(expected, actual))
            throw new ArgumentException("Expected and actual must be separate readable streams.", nameof(actual));

        var reference = Read(expected, "expected");
        var candidate = Read(actual, "actual");
        if (reference is null)
            return Blocked("Expected oracle trace is absent.", "missing", "expected");
        var basis = reference.Header.Provenance.Kind;
        if (candidate is null)
            return Blocked("Actual candidate trace is absent.", basis, "actual");

        var missing = MissingEvidence(reference.Header, "expected") ??
                      MissingEvidence(candidate.Header, "actual");
        if (missing is not null)
            return Blocked($"Required evidence is unknown: {missing}. No authored timing or state is substituted.",
                basis, missing);
        if (basis is not ("independent-executable" or "synthetic"))
            return Blocked(
                "The expected trace is not a declared independent executable capture or explicit synthetic fixture.",
                basis, "expected.header.provenance.kind");

        var metadata = CompareHeaders(reference.Header, candidate.Header, basis);
        if (metadata is not null)
            return metadata;

        var difference = StateDifference(0, "initial.state", reference.InitialState, candidate.InitialState);
        if (difference is not null)
            return Mismatch(difference, 0, basis);

        var commonLength = Math.Min(reference.Transitions.Count, candidate.Transitions.Count);
        for (var index = 0; index < commonLength; index++)
        {
            var left = reference.Transitions[index];
            var right = candidate.Transitions[index];
            difference = InputDifference(left.Tick, left.Input, right.Input) ??
                         StateDifference(left.Tick, "after", left.After, right.After);
            if (difference is not null)
                return Mismatch(difference, index + 1L, basis);
        }

        if (reference.Transitions.Count != candidate.Transitions.Count)
            return Mismatch(new(commonLength + 1L, "transitions.length",
                Format(reference.Transitions.Count), Format(candidate.Transitions.Count)), commonLength, basis);

        var synthetic = basis == "synthetic" || candidate.Header.Provenance.Kind == "synthetic";
        return new("match", commonLength, null,
            synthetic
                ? "Exact synthetic input/raw-state match only; no original-executable qualification."
                : "Exact input/raw-state match against declared independent-executable observations only. " +
                  "External evidence is not authenticated; original dynamics and reuse rights remain unqualified.",
            synthetic ? "synthetic-only" : "unqualified-candidate", basis);
    }

    private static AmigaTrace? Read(Stream stream, string role)
    {
        try
        {
            return AmigaTraceReader.ReadOrNull(stream);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{role} trace: {exception.Message}", exception);
        }
    }

    private static string? MissingEvidence(AmigaTraceHeader header, string role)
    {
        var prefix = role + ".header.";
        if (header.Baseline.ExecutableSha256 is null) return prefix + "baseline.executableSha256";
        if (header.Baseline.Version is null) return prefix + "baseline.version";
        if (header.Baseline.Region is null) return prefix + "baseline.region";
        if (header.TablesSha256 is null) return prefix + "tablesSha256";
        if (header.InputSha256 is null) return prefix + "inputSha256";
        if (header.StateMappingSha256 is null) return prefix + "stateMappingSha256";
        if (header.Timing.Status != "known") return prefix + "timing";
        if (header.Timing.EvidenceSha256 is null) return prefix + "timing.evidenceSha256";
        if (header.Provenance.Kind == "unknown") return prefix + "provenance.kind";
        if (header.Provenance.EvidenceSha256 is null) return prefix + "provenance.evidenceSha256";
        return null;
    }

    private static AmigaTraceVerification? CompareHeaders(
        AmigaTraceHeader expected, AmigaTraceHeader actual, string basis)
    {
        var difference =
            HeaderDifference("header.schemaVersion", expected.SchemaVersion, actual.SchemaVersion, basis) ??
            HeaderDifference("header.stateSchema", expected.StateSchema, actual.StateSchema, basis) ??
            HeaderDifference("header.baseline.executableSha256",
                expected.Baseline.ExecutableSha256, actual.Baseline.ExecutableSha256, basis) ??
            HeaderDifference("header.baseline.version", expected.Baseline.Version, actual.Baseline.Version, basis) ??
            HeaderDifference("header.baseline.region", expected.Baseline.Region, actual.Baseline.Region, basis) ??
            HeaderDifference("header.tablesSha256", expected.TablesSha256, actual.TablesSha256, basis) ??
            HeaderDifference("header.stateMappingSha256", expected.StateMappingSha256, actual.StateMappingSha256, basis);
        if (difference is not null)
            return difference;

        var expectedNumerator = expected.Timing.Numerator.GetValueOrDefault();
        var expectedDenominator = expected.Timing.Denominator.GetValueOrDefault();
        var actualNumerator = actual.Timing.Numerator.GetValueOrDefault();
        var actualDenominator = actual.Timing.Denominator.GetValueOrDefault();
        if ((Int128)expectedNumerator * actualDenominator != (Int128)actualNumerator * expectedDenominator)
            return Blocked("Incompatible logical tick timing.", basis, "header.timing",
                $"{Format(expectedNumerator)}/{Format(expectedDenominator)}",
                $"{Format(actualNumerator)}/{Format(actualDenominator)}");
        return HeaderDifference("header.timing.evidenceSha256",
            expected.Timing.EvidenceSha256, actual.Timing.EvidenceSha256, basis);
    }

    private static AmigaTraceVerification? HeaderDifference<T>(
        string field, T expected, T actual, string basis) =>
        EqualityComparer<T>.Default.Equals(expected, actual) ? null :
            Blocked($"Incompatible trace metadata at {field}.", basis, field, Format(expected), Format(actual));

    private static AmigaTraceVerification Blocked(
        string reason, string basis, string? field = null, string? expected = null, string? actual = null) =>
        new("blocked", 0, null, reason, Qualification(basis), basis, field, expected, actual);

    private static AmigaTraceVerification Mismatch(Difference difference, long comparedTicks, string basis) =>
        new("mismatch", comparedTicks, difference.Tick,
            $"Exact raw comparison differs at tick {difference.Tick}, field {difference.Field}.",
            Qualification(basis), basis, difference.Field, difference.Expected, difference.Actual);

    private static string Qualification(string basis) => basis == "synthetic" ? "synthetic-only" : "unqualified-candidate";

    private static Difference? InputDifference(long tick, AmigaRawInput expected, AmigaRawInput actual) =>
        Different(tick, "input.steer", expected.Steer, actual.Steer) ??
        Different(tick, "input.throttle", expected.Throttle, actual.Throttle) ??
        Different(tick, "input.brake", expected.Brake, actual.Brake) ??
        Different(tick, "input.boost", expected.Boost, actual.Boost) ??
        Different(tick, "input.recover", expected.Recover, actual.Recover);

    private static Difference? StateDifference(long tick, string path, AmigaRawState expected, AmigaRawState actual) =>
        Different(tick, path + ".position.x", expected.Position.X, actual.Position.X) ??
        Different(tick, path + ".position.y", expected.Position.Y, actual.Position.Y) ??
        Different(tick, path + ".position.z", expected.Position.Z, actual.Position.Z) ??
        Different(tick, path + ".angles.yaw", expected.Angles.Yaw, actual.Angles.Yaw) ??
        Different(tick, path + ".angles.pitch", expected.Angles.Pitch, actual.Angles.Pitch) ??
        Different(tick, path + ".angles.roll", expected.Angles.Roll, actual.Angles.Roll) ??
        Different(tick, path + ".linearVelocity.x", expected.LinearVelocity.X, actual.LinearVelocity.X) ??
        Different(tick, path + ".linearVelocity.y", expected.LinearVelocity.Y, actual.LinearVelocity.Y) ??
        Different(tick, path + ".linearVelocity.z", expected.LinearVelocity.Z, actual.LinearVelocity.Z) ??
        Different(tick, path + ".angularVelocity.yaw", expected.AngularVelocity.Yaw, actual.AngularVelocity.Yaw) ??
        Different(tick, path + ".angularVelocity.pitch", expected.AngularVelocity.Pitch, actual.AngularVelocity.Pitch) ??
        Different(tick, path + ".angularVelocity.roll", expected.AngularVelocity.Roll, actual.AngularVelocity.Roll) ??
        ContactDifference(tick, path + ".contacts.frontLeft", expected.Contacts.FrontLeft, actual.Contacts.FrontLeft) ??
        ContactDifference(tick, path + ".contacts.frontRight", expected.Contacts.FrontRight, actual.Contacts.FrontRight) ??
        ContactDifference(tick, path + ".contacts.rear", expected.Contacts.Rear, actual.Contacts.Rear) ??
        Different(tick, path + ".resources.boostReserve", expected.Resources.BoostReserve, actual.Resources.BoostReserve) ??
        Different(tick, path + ".resources.boostSubunit", expected.Resources.BoostSubunit, actual.Resources.BoostSubunit) ??
        Different(tick, path + ".resources.damage", expected.Resources.Damage, actual.Resources.Damage) ??
        Different(tick, path + ".resources.damageLimit", expected.Resources.DamageLimit, actual.Resources.DamageLimit) ??
        Different(tick, path + ".resources.engineRevs", expected.Resources.EngineRevs, actual.Resources.EngineRevs) ??
        Different(tick, path + ".road.section", expected.Road.Section, actual.Road.Section) ??
        Different(tick, path + ".road.segment", expected.Road.Segment, actual.Road.Segment) ??
        Different(tick, path + ".road.lateralPosition", expected.Road.LateralPosition, actual.Road.LateralPosition) ??
        Different(tick, path + ".road.distanceIntoSection", expected.Road.DistanceIntoSection, actual.Road.DistanceIntoSection) ??
        Different(tick, path + ".runtime.flags", expected.Runtime.Flags, actual.Runtime.Flags) ??
        Different(tick, path + ".runtime.offTrackTicks", expected.Runtime.OffTrackTicks, actual.Runtime.OffTrackTicks) ??
        Different(tick, path + ".runtime.recoveryTicks", expected.Runtime.RecoveryTicks, actual.Runtime.RecoveryTicks) ??
        Different(tick, path + ".runtime.clockAccumulator", expected.Runtime.ClockAccumulator, actual.Runtime.ClockAccumulator) ??
        Different(tick, path + ".runtime.damageCooldown", expected.Runtime.DamageCooldown, actual.Runtime.DamageCooldown);

    private static Difference? ContactDifference(
        long tick, string path, AmigaRawContact expected, AmigaRawContact actual) =>
        Different(tick, path + ".roadHeight", expected.RoadHeight, actual.RoadHeight) ??
        Different(tick, path + ".actualHeight", expected.ActualHeight, actual.ActualHeight) ??
        Different(tick, path + ".compression", expected.Compression, actual.Compression) ??
        Different(tick, path + ".previousCompression", expected.PreviousCompression, actual.PreviousCompression) ??
        Different(tick, path + ".damage", expected.Damage, actual.Damage) ??
        Different(tick, path + ".flags", expected.Flags, actual.Flags);

    private static Difference? Different<T>(long tick, string field, T expected, T actual) =>
        EqualityComparer<T>.Default.Equals(expected, actual) ? null :
            new(tick, field, Format(expected), Format(actual));

    private static string Format<T>(T value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private sealed record Difference(long Tick, string Field, string Expected, string Actual);
}
