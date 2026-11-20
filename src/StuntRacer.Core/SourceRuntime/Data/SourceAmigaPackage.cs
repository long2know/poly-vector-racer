using StuntRacer.Core.Compatibility;
using StuntRacer.Core.Compatibility.Tracks;

namespace StuntRacer.Core.SourceRuntime;

public sealed record SourceAmigaUnits(
    int PcFactor, int PositionPrecision, int LocalYFactor, int RoadHeightDivisor,
    double DisplayMetresPerPcUnit, string EvidenceId);

public sealed record SourceAmigaClock(
    int CallbackHertz, int CallbacksPerStep, bool InitialCallbackRunsImmediately,
    double SimulationStepSeconds, string EvidenceId);

public sealed record SourceTemplateMetadata(
    int Id, int NumSegments, bool CurveToLeft, int Type,
    int LengthReduction, int SteeringAmount, int RestartFlags);

public sealed class SourceAmigaLookups
{
    internal SourceAmigaLookups(int[] sine, int[] cosine, Dictionary<string, long[]> tables)
    {
        SinQ14 = sine;
        CosineConversion = Array.AsReadOnly(cosine);
        NumericTables = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<long>>(
            tables.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<long>)Array.AsReadOnly(pair.Value), StringComparer.Ordinal));
    }

    public ReadOnlyMemory<int> SinQ14 { get; }
    public IReadOnlyList<int> CosineConversion { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<long>> NumericTables { get; }
}

public sealed record SourceAmigaStart(
    int LayoutSectionIndex, int SectionIndex, int SegmentIndex, double S,
    long XQ14, long ZQ14, long HeightQ14, long InitialRawY, int HeadingAngle);

public sealed record SourceRoadSegment(
    int Index, int SectionIndex, int SectionSegmentIndex,
    AmigaSourceVertex LeftStart, AmigaSourceVertex RightStart,
    AmigaSourceVertex LeftEnd, AmigaSourceVertex RightEnd, int HeadingAngle);

public readonly record struct SourceRoadContact(
    long HeightQ14, long SlopeXQ14, long SlopeZQ14, long LateralQ14,
    long WidthQ14, int HeadingAngle, int SectionIndex, int SegmentIndex);

public sealed class SourceAmigaPackage
{
    internal SourceAmigaPackage(string name, AmigaTrackLayout layout, AmigaCommonData common,
        SourceAmigaUnits units, SourceAmigaClock clock, SourceAmigaLookups lookups,
        SourceTemplateMetadata[] metadata, Dictionary<string, long> constants,
        CompatibilityAssetProvenance[] provenance)
    {
        Units = units;
        Clock = clock;
        Lookups = lookups;
        Constants = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(constants);
        Provenance = Array.AsReadOnly(provenance);
        TemplateMetadata = Array.AsReadOnly(metadata);
        Layout = layout;
        ImportedTrack = AmigaTrackConverter.ConvertReferenceRemake(layout, common,
            units.PcFactor * units.DisplayMetresPerPcUnit, name);
        var points = ImportedTrack.Points;
        var startIndex = ImportedTrack.StartSegmentIndex;
        var orderedPoints = new TrackPoint[points.Length];
        var orderedEdges = new TrackSurfaceEdge[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var index = (i + startIndex) % points.Length;
            orderedPoints[i] = points[index] with
            {
                S = points[index].S - points[startIndex].S + (index < startIndex ? ImportedTrack.Length : 0)
            };
            var edge = ImportedTrack.Edges[ImportedTrack.Segments[index].StartEdgeIndex];
            orderedEdges[i] = new TrackSurfaceEdge(
                new TrackSurfaceVertex(edge.Left.X, edge.Left.Y, edge.Left.Z),
                new TrackSurfaceVertex(edge.Right.X, edge.Right.Y, edge.Right.Z));
        }
        Track = TrackDefinition.FromGeometry(name, ImportedTrack.Width, ImportedTrack.Length, orderedPoints, orderedEdges);
        AmigaSourceVertex Pc(AmigaSourceVertex vertex) => new(
            checked(vertex.X * units.PcFactor), checked(vertex.Y * units.PcFactor), checked(vertex.Z * units.PcFactor));
        RawRoad = Array.AsReadOnly(ImportedTrack.Segments.Select(segment =>
        {
            var a = ImportedTrack.Edges[segment.StartEdgeIndex];
            var b = ImportedTrack.Edges[segment.EndEdgeIndex];
            return new SourceRoadSegment(segment.Index, segment.SectionIndex, segment.SectionSegmentIndex,
                Pc(a.SourceLeft), Pc(a.SourceRight), Pc(b.SourceLeft), Pc(b.SourceRight),
                Angle(points[segment.Index].Heading));
        }).ToArray());
        var maximumPc = int.MaxValue / units.PositionPrecision;
        var minimumPc = int.MinValue / units.PositionPrecision;
        foreach (var segment in RawRoad)
            foreach (var vertex in new[] { segment.LeftStart, segment.RightStart, segment.LeftEnd, segment.RightEnd })
                if (vertex.X < minimumPc || vertex.X > maximumPc ||
                    vertex.Y < minimumPc || vertex.Y > maximumPc ||
                    vertex.Z < minimumPc || vertex.Z > maximumPc)
                    throw new InvalidDataException("A native reference vertex exceeds signed 32-bit Q14 coordinates.");
        var sectionIndex = layout.StartSectionIndex;
        for (var attempt = 0; attempt < layout.SectionCount; attempt++)
        {
            if (common.Templates.Single(template => template.Id == layout.Sections[sectionIndex].TemplateId).RestartEligible)
                break;
            sectionIndex = (sectionIndex + layout.SectionCount - 1) % layout.SectionCount;
            if (attempt + 1 == layout.SectionCount)
                throw new InvalidDataException("No reference restart-eligible section exists.");
        }
        var section = layout.Sections[sectionIndex];
        var startTemplate = metadata.Single(item => item.Id == section.TemplateId);
        var heading = (section.QuarterTurns * 16384 + (section.Reversed ? 32768 : 0) +
                       (startTemplate.Type == 64 ? 8192 : 0)) & 65535;
        var tileX = checked(common.LayoutGeometry.TileSizeX * units.PcFactor * units.PositionPrecision);
        var tileZ = checked(common.LayoutGeometry.TileSizeZ * units.PcFactor * units.PositionPrecision);
        var centerX = checked(section.GridX * tileX + tileX / 2);
        var centerZ = checked(section.GridZ * tileZ + tileZ / 2);
        if (!TrySampleRawRoad(centerX, centerZ, 0, out var initialRoad))
            throw new InvalidDataException("The selected reference restart cube centre does not intersect the extracted road.");
        var lane = constants["restartLaneOffsetPcUnits"];
        var sin = lookups.SinQ14.Span[heading];
        var cos = lookups.SinQ14.Span[lookups.SinQ14.Length == 81920
            ? heading + 16384 : (heading + 16384) & 65535];
        var startX = checked(centerX + lane * cos);
        var startZ = checked(centerZ - lane * sin);
        var display = Track.Sample(startX / (double)units.PositionPrecision * units.DisplayMetresPerPcUnit,
            startZ / (double)units.PositionPrecision * units.DisplayMetresPerPcUnit);
        var wheelHeight = (initialRoad.HeightQ14 / units.PcFactor) >> 11;
        Start = new SourceAmigaStart(layout.StartSectionIndex, sectionIndex, initialRoad.SegmentIndex, display.S,
            startX, startZ, initialRoad.HeightQ14,
            checked((wheelHeight + constants["restartDropWheelUnits"]) * 256), heading);
        SineQ14 = lookups.SinQ14.ToArray().Select(value => checked((short)value)).ToArray();
    }

    public string ProfileId => "amiga-derived-source-compatible";
    public bool OriginalStockParity => false;
    public TrackDefinition Track { get; }
    public AmigaImportedTrack ImportedTrack { get; }
    public AmigaTrackLayout Layout { get; }
    public SourceAmigaUnits Units { get; }
    public SourceAmigaClock Clock { get; }
    public SourceAmigaLookups Lookups { get; }
    public SourceAmigaStart Start { get; }
    public IReadOnlyList<SourceRoadSegment> RawRoad { get; }
    public IReadOnlyList<SourceTemplateMetadata> TemplateMetadata { get; }
    public IReadOnlyDictionary<string, long> Constants { get; }
    public IReadOnlyList<CompatibilityAssetProvenance> Provenance { get; }
    public double DisplayMetresPerSourceUnit => Units.DisplayMetresPerPcUnit;
    public double SimulationStepSeconds => Clock.SimulationStepSeconds;
    public ReadOnlyMemory<short> SineQ14 { get; }
    public int StartSectionIndex => Layout.StartSectionIndex;
    public byte StandardBoostReserve => Layout.StandardBoostReserve;
    public byte SuperBoostReserve => Layout.SuperBoostReserve;

    public bool TrySampleRawRoad(long worldXQ14, long worldZQ14, long referenceHeightQ14,
        out SourceRoadContact contact)
    {
        if (worldXQ14 is < -(1L << 40) or > (1L << 40) ||
            worldZQ14 is < -(1L << 40) or > (1L << 40))
            throw new ArgumentOutOfRangeException(nameof(worldXQ14), "Source road queries exceed the supported Q14 range.");
        contact = default;
        var result = default(SourceRoadContact);
        var found = false;
        Int128 bestDistance = Int128.MaxValue;
        foreach (var segment in RawRoad)
        {
            Test(segment, 0, segment.LeftStart, segment.RightStart, segment.LeftEnd);
            Test(segment, 1, segment.RightStart, segment.RightEnd, segment.LeftEnd);
        }
        contact = result;
        return found;

        void Test(SourceRoadSegment segment, int triangle,
            AmigaSourceVertex a, AmigaSourceVertex b, AmigaSourceVertex c)
        {
            var precision = Units.PositionPrecision;
            var ax = (Int128)a.X * precision;
            var az = (Int128)a.Z * precision;
            var bx = (Int128)b.X * precision - ax;
            var bz = (Int128)b.Z * precision - az;
            var cx = (Int128)c.X * precision - ax;
            var cz = (Int128)c.Z * precision - az;
            var px = worldXQ14 - ax;
            var pz = worldZQ14 - az;
            var determinant = bx * cz - bz * cx;
            var wb = px * cz - pz * cx;
            var wc = bx * pz - bz * px;
            var wa = determinant - wb - wc;
            if (determinant <= 0 || wa < 0 || wb < 0 || wc < 0)
                return;
            var height = checked((long)(((Int128)a.Y * wa + (Int128)b.Y * wb + (Int128)c.Y * wc) *
                                         precision / determinant));
            var verticalDistance = Int128.Abs((Int128)height - referenceHeightQ14);
            if (found && verticalDistance >= bestDistance)
                return;
            var dxY = ((Int128)b.Y - a.Y) * precision;
            var dzY = ((Int128)c.Y - a.Y) * precision;
            var slopeX = checked((long)((dxY * cz - dzY * bz) * precision / determinant));
            var slopeZ = checked((long)((bx * dzY - cx * dxY) * precision / determinant));
            var t = (double)(triangle == 0 ? wc : wb + wc) / (double)determinant;
            var lx = segment.LeftStart.X + (segment.LeftEnd.X - segment.LeftStart.X) * t;
            var lz = segment.LeftStart.Z + (segment.LeftEnd.Z - segment.LeftStart.Z) * t;
            var rx = segment.RightStart.X + (segment.RightEnd.X - segment.RightStart.X) * t;
            var rz = segment.RightStart.Z + (segment.RightEnd.Z - segment.RightStart.Z) * t;
            var width = checked((long)(Math.Sqrt((rx - lx) * (rx - lx) + (rz - lz) * (rz - lz)) * precision));
            var lateralWeight = triangle == 0 ? -wa + wb - wc : wa + wb - wc;
            var lateral = checked((long)(lateralWeight * width / (2 * determinant)));
            result = new SourceRoadContact(height, slopeX, slopeZ, lateral, width,
                segment.HeadingAngle, segment.SectionIndex, segment.Index);
            bestDistance = verticalDistance;
            found = true;
        }
    }

    private static int Angle(double radians) =>
        ((int)Math.Round(radians * 65536 / Math.Tau, MidpointRounding.ToEven)) & 65535;
}
