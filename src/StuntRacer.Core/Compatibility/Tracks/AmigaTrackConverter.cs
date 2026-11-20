using System.Security.Cryptography;

namespace StuntRacer.Core.Compatibility.Tracks;

public static class AmigaTrackConverter
{
    public static AmigaImportedTrack Convert(AmigaTrackLayout layout, AmigaCommonData commonData,
        double displayMetresPerSourceUnit) =>
        Convert(layout, commonData, displayMetresPerSourceUnit, "Private source-format track — unqualified candidate");

    public static AmigaImportedTrack Convert(AmigaTrackLayout layout, AmigaCommonData commonData,
        double displayMetresPerSourceUnit, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (commonData is null)
            throw new AmigaTrackBlockedException("Geometry conversion requires a decoded common-data interchange.");
        if (!double.IsFinite(displayMetresPerSourceUnit) || displayMetresPerSourceUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayMetresPerSourceUnit),
                "Display metres per source unit must be positive and finite.");
        ValidateName(name);
        RejectDynamicLayout(layout);
        try
        {
            return ConvertCore(layout, commonData, displayMetresPerSourceUnit, name);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Geometry arithmetic overflows signed 64-bit source coordinates.", exception);
        }
    }

    internal static AmigaImportedTrack ConvertReferenceRemake(
        AmigaTrackLayout layout, AmigaCommonData common, double scale, string name)
    {
        ValidateName(name);
        RejectDynamicLayout(layout);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("Reference display scale must be finite and positive.");
        try
        {
            return ConvertCore(layout, common, scale, name, referenceProfilePrefixes: true);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Reference geometry exceeds the supported integer range.", exception);
        }
    }

    private static AmigaImportedTrack ConvertCore(
        AmigaTrackLayout layout, AmigaCommonData common, double scale, string name,
        bool referenceProfilePrefixes = false)
    {
        var rawEdges = new List<AmigaTrackEdge>();
        var ranges = new (int Start, int End, int SegmentStart)[layout.SectionCount];
        var segmentCount = 0;
        foreach (var section in layout.Sections)
        {
            if (!common.TemplateById.TryGetValue(section.TemplateId, out var template))
                throw new InvalidDataException(
                    $"Section {section.Index} references unknown template {section.TemplateId}; supply its decoded template.");
            if (!common.ProfileById.TryGetValue(section.LeftProfileId, out var leftProfile))
                throw new InvalidDataException(
                    $"Section {section.Index} references unknown left height profile {section.LeftProfileId}.");
            if (!common.ProfileById.TryGetValue(section.RightProfileId, out var rightProfile))
                throw new InvalidDataException(
                    $"Section {section.Index} references unknown right height profile {section.RightProfileId}.");
            if (section.LeftStorageFlag != leftProfile.SourceStorage.LeftHighBit)
                throw new InvalidDataException(
                    $"Section {section.Index} left storage flag conflicts with profile {leftProfile.Id}'s declared storage.");
            var count = template.CrossSections.Count;
            if (referenceProfilePrefixes
                    ? leftProfile.SampleCount < count || rightProfile.SampleCount < count
                    : leftProfile.SampleCount != count || rightProfile.SampleCount != count)
                throw new InvalidDataException(
                    $"Section {section.Index} template requires {count} cross-sections; both profiles must have exactly {count} samples.");
            var firstEdge = rawEdges.Count;
            for (var i = 0; i < count; i++)
            {
                var templateIndex = section.Reversed ? count - 1 - i : i;
                var coordinates = template.CrossSections[templateIndex];
                var reverseHeights = section.Reversed && common.LayoutGeometry.ReversalHeightBinding == "template-ordered";
                var sampleIndex = reverseHeights ? templateIndex : i;
                var left = reverseHeights ? rightProfile : leftProfile;
                var right = reverseHeights ? leftProfile : rightProfile;
                var leftShift = reverseHeights ? section.RightHeightShift : section.LeftHeightShift;
                var rightShift = reverseHeights ? section.LeftHeightShift : section.RightHeightShift;
                var leftY = Height(left, sampleIndex, leftShift);
                var rightY = Height(right, sampleIndex, rightShift);
                var leftSource = Place(section,
                    section.Reversed ? coordinates.RightX : coordinates.LeftX, leftY,
                    section.Reversed ? coordinates.RightZ : coordinates.LeftZ, common.LayoutGeometry);
                var rightSource = Place(section,
                    section.Reversed ? coordinates.LeftX : coordinates.RightX, rightY,
                    section.Reversed ? coordinates.LeftZ : coordinates.RightZ, common.LayoutGeometry);
                rawEdges.Add(new AmigaTrackEdge(section.Index, i, templateIndex,
                    segmentCount + Math.Min(i, count - 2), leftSource, rightSource,
                    Project(leftSource, common.CoordinateTransform, scale),
                    Project(rightSource, common.CoordinateTransform, scale)));
            }

            ranges[section.Index] = (firstEdge, rawEdges.Count - 1, segmentCount);
            segmentCount += count - 1;
        }

        var edges = rawEdges.ToArray();
        var seams = new AmigaTrackSeam[layout.SectionCount];
        for (var i = 0; i < layout.SectionCount; i++)
        {
            var next = (i + 1) % layout.SectionCount;
            var endIndex = ranges[i].End;
            var startIndex = ranges[next].Start;
            var end = edges[endIndex];
            var start = edges[startIndex];
            var equal = end.SourceLeft == start.SourceLeft && end.SourceRight == start.SourceRight;
            if (!equal && common.LayoutGeometry.SeamPolicy == "require-equal")
                throw new InvalidDataException(
                    $"Section {i} end does not exactly meet section {next} start. Correct the data or supply evidenced next-section-start seam semantics.");
            if (!equal)
                edges[endIndex] = end with
                {
                    SourceLeft = start.SourceLeft, SourceRight = start.SourceRight,
                    Left = start.Left, Right = start.Right
                };
            seams[i] = new AmigaTrackSeam(i, next, endIndex, startIndex, !equal);
        }

        var points = new TrackPoint[segmentCount];
        var segments = new AmigaTrackSegment[segmentCount];
        var triangles = new AmigaRoadTriangle[segmentCount * 2];
        var minimumWidth = double.PositiveInfinity;
        foreach (var edge in edges)
        {
            var width = Distance(edge.Left, edge.Right);
            var horizontalWidth = PlanarDistance(edge.Left, edge.Right);
            if (!double.IsFinite(width) || !double.IsFinite(horizontalWidth) || width <= 0 || horizontalWidth <= 0)
                throw new InvalidDataException(
                    $"Section {edge.SectionIndex} cross-section {edge.CrossSectionIndex} has degenerate/nonfinite display width.");
            minimumWidth = Math.Min(minimumWidth, width);
        }

        double distance = 0;
        foreach (var section in layout.Sections)
        {
            var range = ranges[section.Index];
            for (var edgeIndex = range.Start; edgeIndex < range.End; edgeIndex++)
            {
                var start = edges[edgeIndex];
                var end = edges[edgeIndex + 1];
                var localSegment = edgeIndex - range.Start;
                var index = range.SegmentStart + localSegment;
                ValidateTriangle(start.Left, start.Right, end.Left, section.Index, localSegment);
                ValidateTriangle(start.Right, end.Right, end.Left, section.Index, localSegment);
                var a = Midpoint(start.Left, start.Right);
                var b = Midpoint(end.Left, end.Right);
                var length = Distance(a, b);
                var planarLength = PlanarDistance(a, b);
                var endS = distance + length;
                if (!double.IsFinite(length) || !double.IsFinite(endS) ||
                    !double.IsFinite(planarLength) || planarLength <= 0 || endS <= distance)
                    throw new InvalidDataException(
                        $"Section {section.Index} segment {localSegment} has degenerate or unrepresentable display length.");
                var width = Distance(start.Left, start.Right);
                points[index] = new TrackPoint(distance, a.X, a.Y, a.Z,
                    Math.Atan2(b.X - a.X, b.Z - a.Z),
                    Math.Atan2(start.Right.Y - start.Left.Y, PlanarDistance(start.Left, start.Right)),
                    width, true);
                segments[index] = new AmigaTrackSegment(index, section.Index, localSegment,
                    edgeIndex, edgeIndex + 1, distance, endS);
                triangles[index * 2] = new AmigaRoadTriangle(
                    section.Index, localSegment, index, 0, start.Left, start.Right, end.Left);
                triangles[index * 2 + 1] = new AmigaRoadTriangle(
                    section.Index, localSegment, index, 1, start.Right, end.Right, end.Left);
                distance = endS;
            }
        }
        return new AmigaImportedTrack(name, distance, minimumWidth, scale, layout, common, points,
            edges, rawEdges.ToArray(), segments, seams, triangles, ranges[layout.StartSectionIndex].SegmentStart);
    }

    private static long Height(AmigaHeightProfile profile, int index, short shift)
    {
        var normalization = profile.Normalization;
        return checked(AmigaCommonDataReader.NormalizeInteger(profile.Samples[index],
                normalization.SampleMultiplier, normalization.SampleDivisor, $"Profile {profile.Id} sample") +
            AmigaCommonDataReader.NormalizeInteger(shift,
                normalization.ShiftMultiplier, normalization.ShiftDivisor, $"Profile {profile.Id} shift"));
    }

    private static AmigaSourceVertex Place(
        AmigaTrackSection section, long x, long y, long z, AmigaLayoutGeometry geometry)
    {
        x = checked(x - geometry.RotationPivotX);
        z = checked(z - geometry.RotationPivotZ);
        for (var i = 0; i < section.QuarterTurns; i++)
            (x, z) = geometry.PositiveQuarterTurn == "x-to-negative-z"
                ? (z, checked(-x)) : (checked(-z), x);
        x = checked(checked(x + geometry.RotationPivotX) + checked(section.GridX * geometry.TileSizeX));
        z = checked(checked(z + geometry.RotationPivotZ) + checked(section.GridZ * geometry.TileSizeZ));
        return new AmigaSourceVertex(x, y, z);
    }

    private static AmigaWorldVertex Project(AmigaSourceVertex source, AmigaCoordinateTransform transform, double scale)
    {
        static double Axis(AmigaSourceVertex vertex, string axis, long divisor, double displayScale)
        {
            var raw = axis[1] switch { 'x' => vertex.X, 'y' => vertex.Y, _ => vertex.Z };
            if (axis[0] == '-')
                raw = checked(-raw);
            var value = raw / (double)divisor * displayScale;
            if (!double.IsFinite(value))
                throw new InvalidDataException("The supplied source transform/display scale produces a nonfinite world coordinate.");
            return value;
        }
        return new AmigaWorldVertex(Axis(source, transform.XAxis, transform.XDivisor, scale),
            Axis(source, transform.YAxis, transform.YDivisor, scale),
            Axis(source, transform.ZAxis, transform.ZDivisor, scale));
    }

    private static void ValidateTriangle(
        AmigaWorldVertex a, AmigaWorldVertex b, AmigaWorldVertex c, int section, int segment)
    {
        var determinant = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        if (!double.IsFinite(determinant) || determinant <= 0)
            throw new InvalidDataException(
                $"Section {section} segment {segment} has degenerate, crossed or reversed-left/right triangles in world XZ.");
        var slopeX = ((b.Y - a.Y) * (c.Z - a.Z) - (c.Y - a.Y) * (b.Z - a.Z)) / determinant;
        var slopeZ = ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / determinant;
        if (!double.IsFinite(slopeX) || !double.IsFinite(slopeZ))
            throw new InvalidDataException($"Section {section} segment {segment} has nonfinite geometric road slopes.");
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name != name.Trim() ||
            name.Any(character => char.IsControl(character) || character is '/' or '\\'))
            throw new ArgumentException("Track name must be a 1..160-character label, not a path.", nameof(name));
        var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (key.Contains("drawbridge", StringComparison.Ordinal))
            throw new InvalidDataException("DrawBridge requires unsupported dynamic geometry; a frozen original circuit is not available.");
    }

    private static void RejectDynamicLayout(AmigaTrackLayout layout)
    {
        var hash = System.Convert.ToHexStringLower(SHA256.HashData(layout.SourceBytes.Span));
        if (hash == "3ef368f94049c22324214ce4592e0ae1cb6751b9f08602a96b147e3243848e0d")
            throw new InvalidDataException("The known DrawBridge layout requires unsupported dynamic geometry; inspect only.");
    }

    internal static double Distance(AmigaWorldVertex a, AmigaWorldVertex b)
    {
        var x = b.X - a.X;
        var y = b.Y - a.Y;
        var z = b.Z - a.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    internal static double PlanarDistance(AmigaWorldVertex a, AmigaWorldVertex b)
    {
        var x = b.X - a.X;
        var z = b.Z - a.Z;
        return Math.Sqrt(x * x + z * z);
    }

    private static AmigaWorldVertex Midpoint(AmigaWorldVertex a, AmigaWorldVertex b) =>
        new(a.X / 2 + b.X / 2, a.Y / 2 + b.Y / 2, a.Z / 2 + b.Z / 2);
}
