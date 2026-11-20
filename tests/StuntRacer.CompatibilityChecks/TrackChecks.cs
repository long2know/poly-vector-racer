using System.Text.Json.Nodes;
using StuntRacer.Core.Compatibility;
using StuntRacer.Core.Compatibility.Tracks;

namespace StuntRacer.CompatibilityChecks;

internal static class TrackChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("geometry / exact synthetic edges, sections and cumulative distances", ExactEdges);
        yield return new("geometry / explicit axes, divisors, tile origin and rotation pivot", ExplicitTransforms);
        yield return new("geometry / reversal bindings preserve raw storage/colour flags", Reversal);
        yield return new("geometry / evidenced seam joins retain unstitched source vertices", Seams);
        yield return new("geometry / triangles and geometric road query share exact edges", GeometricSurface);
        yield return new("geometry / missing data, unsupported IDs and flags never invent road", MissingAndUnsupported);
        yield return new("geometry / scale, source overflow and degeneracy fail closed", InvalidGeometry);
        yield return new("geometry / height samples and shifts normalize independently", HeightNormalization);
    }

    public static AmigaImportedTrack Import(JsonObject? common = null, byte[]? bytes = null, double scale = 0.5) =>
        AmigaTrackConverter.Convert(AmigaTrackLayoutReader.Read(bytes ?? SyntheticCommon.Layout()),
            CommonChecks.Read(common ?? SyntheticCommon.Create()), scale, "Synthetic closed strip");

    private static void ExactEdges()
    {
        var bytes = SyntheticCommon.Layout();
        bytes[1] = 2;
        var track = Import(bytes: bytes);
        Check.Equal(16, track.Edges.Count, "exact edges include each section endpoint");
        Check.Equal(16, track.UnstitchedEdges.Count, "unstitched edge count");
        Check.Equal(12, track.Points.Length, "one canonical point per source segment, not authored resampling");
        Check.Equal(12, track.Segments.Count, "source segment count");
        Check.Equal(4, track.Seams.Count, "source seam count");
        Check.Equal(24, track.Triangles.Count, "two triangles per segment");
        Source(track.Edges[0], new(-6, 10, 0), new(-2, 23, 0), "q0 start");
        Source(track.Edges[1], new(-5, 12, 2), new(-1, 25, 1), "q0 skewed cross-section");
        Source(track.Edges[2], new(-2, 17, 5), new(-1, 31, 2), "q0 sloping cross-section");
        Source(track.Edges[3], new(0, 10, 6), new(0, 23, 2), "q0 end");
        Source(track.Edges[5], new(2, 12, 5), new(1, 25, 1), "q1");
        Source(track.Edges[9], new(5, 12, -2), new(1, 25, -1), "q2");
        Source(track.Edges[13], new(-2, 12, -5), new(-1, 25, -1), "q3");
        World(track.Edges[0].Left, -3, 5, 0, "display left start");
        World(track.Edges[0].Right, -1, 11.5, 0, "display right start");
        Check.Require(track.Edges[0].Right.X > track.Edges[0].Left.X, "+X remains right");
        Check.Near(Math.Atan2(2, 3), track.Points[0].Heading, "heading uses +Z forward / yaw toward +X");
        Check.Near(Math.Atan2(13, 4), track.Points[0].Bank, "positive bank raises right edge");
        Check.Near(Math.Sqrt(185) / 2, track.Width, "minimum 3D source width");
        var sectionLength = (Math.Sqrt(7.25) + Math.Sqrt(36.5) + Math.Sqrt(58.75)) / 2;
        Check.Near(4 * sectionLength, track.Length, "closed cumulative 3D length");
        Check.Equal(6, track.StartSegmentIndex, "start section maps to canonical source segment");
        Check.Near(2 * sectionLength, track.StartDistance, "start distance");
        Check.Require(track.Profile == CompatibilityProfile.AmigaExperimental &&
                      track.Profile.SimulationStepSeconds is null &&
                      track.Profile.Qualification == CompatibilityQualification.UnqualifiedCandidate,
            "imported geometry must not acquire authored timing or original qualification");

        for (var index = 0; index < track.Segments.Count; index++)
        {
            var segment = track.Segments[index];
            Check.Equal(index, segment.Index, "stable segment index");
            Check.Equal(index / 3, segment.SectionIndex, "stable source section identity");
            Check.Equal(index % 3, segment.SectionSegmentIndex, "stable local segment identity");
            Check.Equal(index / 3 * 4 + index % 3, segment.StartEdgeIndex, "segment start edge");
            Check.Equal(segment.StartEdgeIndex + 1, segment.EndEdgeIndex, "segment end edge");
            Check.Near(segment.StartS, track.Points[index].S, "point and segment share distance");
            Check.Require(segment.EndS > segment.StartS && track.Points[index].Road, "positive source segment length/road");
            if (index > 0)
                Check.Near(track.Segments[index - 1].EndS, segment.StartS, "distance seam continuity");
        }
        var points = track.Points;
        points[0] = points[0] with { X = 9999 };
        Check.Near(-2, track.Points[0].X, "canonical points are defensively exposed");
    }

    private static void ExplicitTransforms()
    {
        var json = SyntheticCommon.Create();
        var transform = json["coordinateTransform"]!;
        transform["xAxis"] = "-z";
        transform["yAxis"] = "-y";
        transform["zAxis"] = "+x";
        transform["xDivisor"] = 2;
        transform["yDivisor"] = 4;
        transform["zDivisor"] = 5;
        var bytes = SyntheticCommon.Layout();
        Array.Fill(bytes, (byte)0x21, 2, 4);
        var track = Import(json, bytes);
        Source(track.Edges[0], new(94, 10, 400), new(98, 23, 400), "tile origin stays in source units");
        World(track.Edges[0].Left, -100, -1.25, 9.4, "axis/sign/divisor then display scale");
        World(track.Edges[0].Right, -100, -2.875, 9.8, "right edge independent normalization");
        var twice = Import(json, bytes, 1);
        Check.Equal(track.Edges[0].SourceLeft, twice.Edges[0].SourceLeft, "display scale must not alter raw geometry");
        Check.Near(track.Length * 2, twice.Length, "one scale applied exactly once");

        var pivot = SyntheticCommon.Create();
        pivot["layoutGeometry"]!["rotationPivotX"] = 2;
        pivot["layoutGeometry"]!["rotationPivotZ"] = 3;
        foreach (var section in pivot["templates"]![0]!["crossSections"]!.AsArray())
        {
            section!["leftX"] = section["leftX"]!.GetValue<long>() + 2;
            section["rightX"] = section["rightX"]!.GetValue<long>() + 2;
            section["leftZ"] = section["leftZ"]!.GetValue<long>() + 3;
            section["rightZ"] = section["rightZ"]!.GetValue<long>() + 3;
        }
        Source(Import(pivot).Edges[5], new(4, 12, 8), new(3, 25, 4), "declared nonzero rotation pivot");
    }

    private static void Reversal()
    {
        var bytes = SyntheticLayout.Create(
            new(0, 0xd0, 0, 0x81, 10, 20),
            new(0, 0x90, 0, 0x81, 10, 20),
            new(0, 0x50, 0, 0x81, 10, 20),
            new(0, 0x10, 0, 0x81, 10, 20));
        var travel = Import(bytes: bytes);
        Source(travel.Edges[0], new(-2, 10, 0), new(-6, 23, 0), "travel-ordered reversal start");
        Source(travel.Edges[1], new(-2, 12, -1), new(-5, 25, -2), "travel-ordered reversal next");
        Check.Equal(3, travel.Edges[0].TemplateCrossSectionIndex, "reverse template sample order");
        Check.Equal(2, travel.Edges[1].TemplateCrossSectionIndex, "reverse next template sample");
        Check.Equal(0, travel.Edges[0].CrossSectionIndex, "forward traversal cross-section index");

        var common = SyntheticCommon.Create();
        common["layoutGeometry"]!["reversalHeightBinding"] = "template-ordered";
        common["heightProfiles"]![0]!["sourceStorage"]!["leftHighBit"] = true;
        Array.Fill(bytes, (byte)0x80, 202, 4);
        var template = Import(common, bytes);
        Source(template.Edges[0], new(-2, 23, 0), new(-6, 10, 0), "template-ordered reversal sides/shifts");
        Source(template.Edges[1], new(-2, 31, -1), new(-5, 17, -2), "template-ordered reversed height samples");
        Source(template.Edges[2], new(-1, 25, -1), new(-2, 12, -5), "asymmetric height sample ordering");
        Check.Equal((byte)0x80, template.Layout.Sections[0].RawLeftProfile, "raw left storage flag is not swapped");
        Check.Equal((byte)0x81, template.Layout.Sections[0].RawRightProfile, "raw right colour flag is not swapped");
        Check.Require(template.Layout.SourceBytes.ToArray().SequenceEqual(bytes), "reversal mutated raw layout");
    }

    private static void Seams()
    {
        var common = SyntheticCommon.Create();
        common["templates"]![0]!["crossSections"]![3]!["leftX"] = 1;
        common["templates"]![0]!["crossSections"]![3]!["rightX"] = 1;
        Check.Throws<InvalidDataException>(() => Import(common), "unequal source seams require explicit policy");
        common["layoutGeometry"]!["seamPolicy"] = "next-section-start";
        var track = Import(common);
        var original = Import();
        Source(track.UnstitchedEdges[3], new(1, 10, 6), new(1, 23, 2), "pre-seam source retained");
        Source(track.Edges[3], new(0, 10, 6), new(0, 23, 2), "next-section-start canonical seam");
        foreach (var seam in track.Seams)
        {
            Check.Require(seam.Adjusted, $"seam {seam.EndingSectionIndex} adjustment identity");
            var end = track.Edges[seam.EndEdgeIndex];
            var start = track.Edges[seam.StartEdgeIndex];
            Check.Equal(start.SourceLeft, end.SourceLeft, "canonical source left seam");
            Check.Equal(start.SourceRight, end.SourceRight, "canonical source right seam");
            Check.Equal(start.Left, end.Left, "canonical display left seam");
            Check.Equal(start.Right, end.Right, "canonical display right seam");
            Check.Equal((seam.EndingSectionIndex + 1) % 4, seam.StartingSectionIndex, "closed source section order");
        }
        Check.Require(track.Points.SequenceEqual(original.Points), "evidenced seam policy forked canonical points");
        Check.Equal(1L, track.CommonData.Templates[0].CrossSections[3].LeftX, "seam stitching mutated common data");
    }

    private static void GeometricSurface()
    {
        var track = Import();
        foreach (var triangle in track.Triangles)
        {
            var segment = track.Segments[triangle.SegmentIndex];
            var start = track.Edges[segment.StartEdgeIndex];
            var end = track.Edges[segment.EndEdgeIndex];
            Check.Equal(triangle.TriangleIndex == 0 ? start.Left : start.Right, triangle.A, "triangle A canonical edge");
            Check.Equal(triangle.TriangleIndex == 0 ? start.Right : end.Right, triangle.B, "triangle B canonical edge");
            Check.Equal(end.Left, triangle.C, "triangle C canonical edge");
            var x = (triangle.A.X + triangle.B.X + triangle.C.X) / 3;
            var y = (triangle.A.Y + triangle.B.Y + triangle.C.Y) / 3;
            var z = (triangle.A.Z + triangle.B.Z + triangle.C.Z) / 3;
            Check.Require(track.TrySampleGeometricRoad(x, z, y, out var contact),
                $"rendered triangle {triangle.SegmentIndex}/{triangle.TriangleIndex} has no geometric road");
            Check.Near(y, contact.Sample.Y, "geometric query/render height equality");
            Check.Equal(triangle.SectionIndex, contact.SectionIndex, "query source section");
            Check.Equal(triangle.SegmentIndex, contact.SegmentIndex, "query source segment");
            Check.Equal(triangle.TriangleIndex, contact.TriangleIndex, "query triangle identity");
            Check.Near(1.0 / 3, contact.WeightA, "barycentric A");
            Check.Near(1.0 / 3, contact.WeightB, "barycentric B");
            Check.Near(1.0 / 3, contact.WeightC, "barycentric C");
        }
        Check.Require(track.TrySampleGeometricRoad(-6.5 / 3, 1.0 / 3, 7.5, out var first), "manual triangle centroid");
        Check.Near(7.5, first.Sample.Y, "manually computed synthetic height");
        Check.Near(3.25, first.Sample.SlopeX, "manually computed cross-bank slope");
        Check.Near(-0.625, first.Sample.SlopeZ, "manually computed longitudinal slope");
        Check.Require(!track.TrySampleGeometricRoad(10000, 10000, 0, out _), "no inferred extended road");
        Check.Throws<ArgumentOutOfRangeException>(() => track.TrySampleGeometricRoad(double.NaN, 0, 0, out _), "query NaN x");
        Check.Throws<ArgumentOutOfRangeException>(() => track.TrySampleGeometricRoad(0, double.PositiveInfinity, 0, out _), "query infinite z");
        Check.Throws<ArgumentOutOfRangeException>(() => track.TrySampleGeometricRoad(0, 0, double.NaN, out _), "query NaN reference height");
    }

    private static void MissingAndUnsupported()
    {
        var layout = AmigaTrackLayoutReader.Read(SyntheticCommon.Layout());
        Check.Throws<AmigaTrackBlockedException>(() => AmigaTrackConverter.Convert(layout, null!, 0.5),
            "missing common data blocks conversion");
        foreach (var offset in new[] { 102, 202, 302 })
        {
            var bytes = SyntheticCommon.Layout();
            bytes[offset] = 15;
            Check.Throws<InvalidDataException>(() => Import(bytes: bytes), $"unknown referenced id at byte {offset}");
        }
        var flagged = SyntheticCommon.Layout();
        flagged[202] = 0x80;
        Check.Throws<InvalidDataException>(() => Import(bytes: flagged), "unknown left storage semantics");
        var common = SyntheticCommon.Create();
        common["heightProfiles"]![0]!["samples"]!.AsArray().RemoveAt(0);
        common["heightProfiles"]![0]!["sampleCount"] = 3;
        Check.Throws<InvalidDataException>(() => Import(common), "profile/template count mismatch");
        Check.Throws<InvalidDataException>(() => AmigaTrackConverter.Convert(layout, CommonChecks.Read(SyntheticCommon.Create()), 0.5,
            "DrawBridge"), "dynamic original label is not silently frozen");
    }

    private static void InvalidGeometry()
    {
        foreach (var scale in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check.Throws<ArgumentOutOfRangeException>(() => Import(scale: scale), $"invalid explicit scale {scale}");
        foreach (var scale in new[] { double.Epsilon, double.MaxValue })
            Check.Throws<InvalidDataException>(() => Import(scale: scale), $"unrepresentable geometry at scale {scale}");
        var origin = SyntheticCommon.Create();
        origin["layoutGeometry"]!["tileSizeX"] = long.MaxValue;
        var bytes = SyntheticCommon.Layout();
        Array.Fill(bytes, (byte)2, 2, 4);
        Check.Throws<InvalidDataException>(() => Import(origin, bytes), "source tile origin overflow");
        var crossed = SyntheticCommon.Create();
        foreach (var crossSection in crossed["templates"]![0]!["crossSections"]!.AsArray())
        {
            var leftX = crossSection!["leftX"]!.GetValue<long>();
            var leftZ = crossSection["leftZ"]!.GetValue<long>();
            crossSection["leftX"] = crossSection["rightX"]!.GetValue<long>();
            crossSection["leftZ"] = crossSection["rightZ"]!.GetValue<long>();
            crossSection["rightX"] = leftX;
            crossSection["rightZ"] = leftZ;
        }
        Check.Throws<InvalidDataException>(() => Import(crossed), "crossed/reversed world left/right winding");
        var degenerate = SyntheticCommon.Create();
        degenerate["templates"]![0]!["crossSections"]![1] = degenerate["templates"]![0]!["crossSections"]![0]!.DeepClone();
        Check.Throws<InvalidDataException>(() => Import(degenerate), "zero-planar-area road triangle");
        var zeroWidth = SyntheticCommon.Create();
        zeroWidth["templates"]![0]!["crossSections"]![0]!["rightX"] = -6;
        Check.Throws<InvalidDataException>(() => CommonChecks.Read(zeroWidth), "zero-planar-width source edge");
    }

    private static void HeightNormalization()
    {
        var common = SyntheticCommon.Create();
        foreach (var profile in common["heightProfiles"]!.AsArray())
        {
            profile!["normalization"]!["sampleMultiplier"] = 2;
            profile["normalization"]!["shiftDivisor"] = 2;
        }
        Source(Import(common).Edges[1], new(-5, 9, 2), new(-1, 20, 1), "separate sample/shift normalization");
        var fractional = SyntheticCommon.Create();
        fractional["heightProfiles"]![0]!["normalization"]!["shiftDivisor"] = 3;
        Check.Throws<InvalidDataException>(() => Import(fractional), "fractional shifts must not round");
        var overflow = SyntheticCommon.Create();
        overflow["heightProfiles"]![0]!["normalization"]!["shiftMultiplier"] = long.MaxValue;
        Check.Throws<InvalidDataException>(() => Import(overflow), "shift normalization overflow");
        var sum = SyntheticCommon.Create();
        sum["heightProfiles"]![0]!["samples"]![0] = long.MaxValue - 5;
        Check.Throws<InvalidDataException>(() => Import(sum), "normalized height plus shift overflow");
    }

    private static void Source(AmigaTrackEdge edge, AmigaSourceVertex left, AmigaSourceVertex right, string context)
    {
        Check.Equal(left, edge.SourceLeft, $"{context}.sourceLeft");
        Check.Equal(right, edge.SourceRight, $"{context}.sourceRight");
    }

    private static void World(AmigaWorldVertex vertex, double x, double y, double z, string context)
    {
        Check.Near(x, vertex.X, $"{context}.x");
        Check.Near(y, vertex.Y, $"{context}.y");
        Check.Near(z, vertex.Z, $"{context}.z");
    }
}
