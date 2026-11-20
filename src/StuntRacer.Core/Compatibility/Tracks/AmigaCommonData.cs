namespace StuntRacer.Core.Compatibility.Tracks;

public sealed record AmigaTemplateCrossSection(long LeftX, long LeftZ, long RightX, long RightZ);

public sealed class AmigaPieceTemplate
{
    internal AmigaPieceTemplate(int id, bool restartEligible, AmigaTemplateCrossSection[] crossSections)
    {
        Id = id;
        RestartEligible = restartEligible;
        CrossSections = Array.AsReadOnly(crossSections);
    }

    public int Id { get; }
    public bool RestartEligible { get; }
    public IReadOnlyList<AmigaTemplateCrossSection> CrossSections { get; }
}

public sealed record AmigaProfileStorage(string Kind, bool LeftHighBit, string EvidenceId);

public sealed record AmigaHeightNormalization(
    long SampleMultiplier, long SampleDivisor,
    long ShiftMultiplier, long ShiftDivisor, string EvidenceId);

public sealed class AmigaHeightProfile
{
    internal AmigaHeightProfile(int id, AmigaProfileStorage sourceStorage,
        AmigaHeightNormalization normalization, long[] samples)
    {
        Id = id;
        SourceStorage = sourceStorage;
        Normalization = normalization;
        Samples = Array.AsReadOnly(samples);
    }

    public int Id { get; }
    public int SampleCount => Samples.Count;
    public AmigaProfileStorage SourceStorage { get; }
    public AmigaHeightNormalization Normalization { get; }
    public IReadOnlyList<long> Samples { get; }
}

public sealed record AmigaLayoutGeometry(
    long TileSizeX, long TileSizeZ, long RotationPivotX, long RotationPivotZ,
    string PositiveQuarterTurn, string ReversalHeightBinding, string SeamPolicy, string EvidenceId);

public sealed record AmigaCoordinateTransform(
    string XAxis, string YAxis, string ZAxis,
    long XDivisor, long YDivisor, long ZDivisor, string EvidenceId);

public sealed class AmigaCommonData
{
    internal AmigaCommonData(string sourceId, string? sourceRevision, string encodingEvidenceId,
        AmigaLayoutGeometry layoutGeometry, AmigaCoordinateTransform coordinateTransform,
        AmigaPieceTemplate[] templates, AmigaHeightProfile[] heightProfiles)
    {
        SourceId = sourceId;
        SourceRevision = sourceRevision;
        EncodingEvidenceId = encodingEvidenceId;
        LayoutGeometry = layoutGeometry;
        CoordinateTransform = coordinateTransform;
        Templates = Array.AsReadOnly(templates.OrderBy(template => template.Id).ToArray());
        HeightProfiles = Array.AsReadOnly(heightProfiles.OrderBy(profile => profile.Id).ToArray());
        TemplateById = templates.ToDictionary(template => template.Id);
        ProfileById = heightProfiles.ToDictionary(profile => profile.Id);
    }

    public int SchemaVersion => 1;
    public string SourceId { get; }
    public string? SourceRevision { get; }
    public string CircuitMode => "static";
    public string EncodingEvidenceId { get; }
    public AmigaLayoutGeometry LayoutGeometry { get; }
    public AmigaCoordinateTransform CoordinateTransform { get; }
    public IReadOnlyList<AmigaPieceTemplate> Templates { get; }
    public IReadOnlyList<AmigaHeightProfile> HeightProfiles { get; }
    internal IReadOnlyDictionary<int, AmigaPieceTemplate> TemplateById { get; }
    internal IReadOnlyDictionary<int, AmigaHeightProfile> ProfileById { get; }
}

public sealed class AmigaTrackBlockedException : IOException
{
    public AmigaTrackBlockedException(string message) : base(message) { }
}
