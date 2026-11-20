namespace StuntRacer.Core.Compatibility.Tracks;

public readonly record struct AmigaSourceVertex(long X, long Y, long Z);
public readonly record struct AmigaWorldVertex(double X, double Y, double Z);

public sealed record AmigaTrackEdge(
    int SectionIndex, int CrossSectionIndex, int TemplateCrossSectionIndex, int SegmentIndex,
    AmigaSourceVertex SourceLeft, AmigaSourceVertex SourceRight,
    AmigaWorldVertex Left, AmigaWorldVertex Right);

public sealed record AmigaTrackSegment(
    int Index, int SectionIndex, int SectionSegmentIndex,
    int StartEdgeIndex, int EndEdgeIndex, double StartS, double EndS);

public sealed record AmigaTrackSeam(
    int EndingSectionIndex, int StartingSectionIndex,
    int EndEdgeIndex, int StartEdgeIndex, bool Adjusted);

public sealed record AmigaRoadTriangle(
    int SectionIndex, int SectionSegmentIndex, int SegmentIndex, int TriangleIndex,
    AmigaWorldVertex A, AmigaWorldVertex B, AmigaWorldVertex C);

public readonly record struct AmigaRoadContact(
    int SectionIndex, int SectionSegmentIndex, int SegmentIndex, int TriangleIndex,
    RoadSample Sample, double WeightA, double WeightB, double WeightC);

public sealed class AmigaImportedTrack
{
    private readonly TrackPoint[] _points;

    internal AmigaImportedTrack(string name, double length, double width, double displayScale,
        AmigaTrackLayout layout, AmigaCommonData commonData, TrackPoint[] points,
        AmigaTrackEdge[] edges, AmigaTrackEdge[] unstitchedEdges, AmigaTrackSegment[] segments,
        AmigaTrackSeam[] seams, AmigaRoadTriangle[] triangles, int startSegmentIndex)
    {
        Name = name;
        Length = length;
        Width = width;
        DisplayMetresPerSourceUnit = displayScale;
        Layout = layout;
        CommonData = commonData;
        _points = points;
        Edges = Array.AsReadOnly(edges);
        UnstitchedEdges = Array.AsReadOnly(unstitchedEdges);
        Segments = Array.AsReadOnly(segments);
        Seams = Array.AsReadOnly(seams);
        Triangles = Array.AsReadOnly(triangles);
        StartSegmentIndex = startSegmentIndex;
        RestartEligibleSections = Array.AsReadOnly(layout.Sections
            .Where(section => commonData.TemplateById[section.TemplateId].RestartEligible)
            .Select(section => section.Index).ToArray());
    }

    public string Name { get; }
    public double Length { get; }
    public double Width { get; }
    public TrackPoint[] Points => (TrackPoint[])_points.Clone();
    public IReadOnlyList<AmigaTrackEdge> Edges { get; }
    public IReadOnlyList<AmigaTrackEdge> UnstitchedEdges { get; }
    public IReadOnlyList<AmigaTrackSegment> Segments { get; }
    public IReadOnlyList<AmigaTrackSeam> Seams { get; }
    public IReadOnlyList<AmigaRoadTriangle> Triangles { get; }
    public IReadOnlyList<int> RestartEligibleSections { get; }
    public int StartSegmentIndex { get; }
    public double StartDistance => _points[StartSegmentIndex].S;
    public double DisplayMetresPerSourceUnit { get; }
    public AmigaTrackLayout Layout { get; }
    public AmigaCommonData CommonData { get; }
    public CompatibilityProfile Profile => CompatibilityProfile.AmigaExperimental;

    public bool TrySampleGeometricRoad(double x, double z, double referenceY, out AmigaRoadContact contact)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || !double.IsFinite(referenceY))
            throw new ArgumentOutOfRangeException(nameof(x), "Geometric road query coordinates must be finite.");
        contact = default;
        var found = false;
        var bestVerticalDistance = double.PositiveInfinity;
        foreach (var triangle in Triangles)
        {
            if (!TryWeights(triangle, x, z, out var a, out var b, out var c))
                continue;
            var y = a * triangle.A.Y + b * triangle.B.Y + c * triangle.C.Y;
            if (!double.IsFinite(y))
                continue;
            var verticalDistance = Math.Abs(y - referenceY);
            if (found && verticalDistance >= bestVerticalDistance)
                continue;

            var segment = Segments[triangle.SegmentIndex];
            var start = Edges[segment.StartEdgeIndex];
            var end = Edges[segment.EndEdgeIndex];
            var t = Math.Clamp(triangle.TriangleIndex == 0 ? c : b + c, 0, 1);
            var lateralUnit = triangle.TriangleIndex == 0 ? -a + b - c : a + b - c;
            var left = Lerp(start.Left, end.Left, t);
            var right = Lerp(start.Right, end.Right, t);
            var width = AmigaTrackConverter.Distance(left, right);
            var bank = Math.Atan2(right.Y - left.Y,
                AmigaTrackConverter.PlanarDistance(left, right));
            var dx1 = triangle.B.X - triangle.A.X;
            var dz1 = triangle.B.Z - triangle.A.Z;
            var dx2 = triangle.C.X - triangle.A.X;
            var dz2 = triangle.C.Z - triangle.A.Z;
            var determinant = dx1 * dz2 - dx2 * dz1;
            var slopeX = ((triangle.B.Y - triangle.A.Y) * dz2 -
                          (triangle.C.Y - triangle.A.Y) * dz1) / determinant;
            var slopeZ = (dx1 * (triangle.C.Y - triangle.A.Y) -
                          dx2 * (triangle.B.Y - triangle.A.Y)) / determinant;
            var s = segment.StartS + (segment.EndS - segment.StartS) * t;
            if (s >= Length)
                s = 0;
            var sample = new RoadSample(s, x, y, z, _points[segment.Index].Heading, bank,
                width, lateralUnit * width / 2, true, slopeX, slopeZ);
            contact = new AmigaRoadContact(triangle.SectionIndex, triangle.SectionSegmentIndex,
                triangle.SegmentIndex, triangle.TriangleIndex, sample, a, b, c);
            bestVerticalDistance = verticalDistance;
            found = true;
        }
        return found;
    }

    private static bool TryWeights(AmigaRoadTriangle triangle, double x, double z,
        out double a, out double b, out double c)
    {
        var determinant = (triangle.B.Z - triangle.C.Z) * (triangle.A.X - triangle.C.X) +
                          (triangle.C.X - triangle.B.X) * (triangle.A.Z - triangle.C.Z);
        a = ((triangle.B.Z - triangle.C.Z) * (x - triangle.C.X) +
             (triangle.C.X - triangle.B.X) * (z - triangle.C.Z)) / determinant;
        b = ((triangle.C.Z - triangle.A.Z) * (x - triangle.C.X) +
             (triangle.A.X - triangle.C.X) * (z - triangle.C.Z)) / determinant;
        c = 1 - a - b;
        return double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) &&
               a >= -1e-10 && b >= -1e-10 && c >= -1e-10;
    }

    private static AmigaWorldVertex Lerp(AmigaWorldVertex a, AmigaWorldVertex b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
}
