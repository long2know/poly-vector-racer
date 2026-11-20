using System.Text.Json;
using StuntRacer.Core;

namespace StuntRacer.CompatibilityChecks;

internal static class GeometryFactoryChecks
{
    public static IEnumerable<NativeCheck> All()
    {
        yield return new("factory / skewed nonplanar exact triangles drive geometric road height", ExactTriangleSurface);
        yield return new("factory / malformed geometry rejects and caller arrays cannot alias", InvalidGeometryAndAliasing);
    }

    private static void ExactTriangleSurface()
    {
        var fixture = Geometry();
        var track = Create(fixture);
        Check.Equal(new TrackSurfaceVertex(-3, 5, 0), track.Edges[0].Left, "factory exact initial left vertex");
        Check.Equal(new TrackSurfaceVertex(-1, 11.5, 0), track.Edges[0].Right, "factory exact initial right vertex");
        Check.Equal(new TrackSurfaceVertex(-2.5, 6, 1), track.Edges[1].Left, "factory skewed next left vertex");
        Check.Equal(new TrackSurfaceVertex(-0.5, 12.5, 0.5), track.Edges[1].Right, "factory skewed next right vertex");
        var firstTriangle = track.Sample(-6.5 / 3, 1.0 / 3);
        Check.Require(firstTriangle.Road, "exact rendered triangle centroid must be road");
        Check.Near(7.5, firstTriangle.Y, "manual barycentric Y on left/right/next-left triangle");
        Check.Near(3.25, firstTriangle.SlopeX, "manual exact triangle cross-slope");
        Check.Near(-0.625, firstTriangle.SlopeZ, "manual exact triangle forward slope");
        var secondTriangle = track.Sample(-4.0 / 3, 0.5);
        Check.Require(secondTriangle.Road, "second exact rendered triangle centroid must be road");
        Check.Near(10, secondTriangle.Y, "manual barycentric Y on right/next-right/next-left triangle");
        var firstPlaneAtNextRight = 5 + 3.25 * 2.5 - 0.625 * 0.5;
        Check.Require(Math.Abs(firstPlaneAtNextRight - track.Edges[1].Right.Y) > 0.3,
            "fixture must remain genuinely nonplanar, not a single midpoint/bank plane");
        var point = track.Points[0];
        var reconstructedLeftZ = point.Z + Math.Sin(point.Heading) * Math.Cos(point.Bank) * point.Width / 2;
        Check.Require(Math.Abs(reconstructedLeftZ - track.Edges[0].Left.Z) > 0.5,
            "fixture must distinguish supplied skewed edges from midpoint/heading/bank reconstruction");

        using var rendered = JsonDocument.Parse(JsonSerializer.Serialize(track,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var renderedEdges = rendered.RootElement.GetProperty("edges");
        Check.Equal(12, renderedEdges.GetArrayLength(), "serialized exact edge count");
        Check.Near(-3, renderedEdges[0].GetProperty("left").GetProperty("x").GetDouble(), "rendered exact left x");
        Check.Near(12.5, renderedEdges[1].GetProperty("right").GetProperty("y").GetDouble(), "rendered nonplanar right y");
        var simulation = new GameSimulation(track);
        Check.Require(ReferenceEquals(track, simulation.Track), "custom geometry silently fell back to the authored track");
        Check.Near(-2, simulation.Snapshot.Player.X, "custom track initial car x");
        Check.Near(1, simulation.Snapshot.Player.Y -
            simulation.Track.Sample(simulation.Snapshot.Player.X, simulation.Snapshot.Player.Z).Y,
            "custom initial car uses the same canonical road height");
    }

    private static void InvalidGeometryAndAliasing()
    {
        var fixture = Geometry();
        Check.Throws<ArgumentNullException>(() => TrackDefinition.FromGeometry("Synthetic", fixture.Width, fixture.Length, null!, fixture.Edges),
            "missing point rows");
        Check.Throws<ArgumentNullException>(() => TrackDefinition.FromGeometry("Synthetic", fixture.Width, fixture.Length, fixture.Points, null!),
            "missing exact edge rows");
        foreach (var name in new[] { "", " ", new string('x', 129) })
            Check.Throws<ArgumentException>(() => TrackDefinition.FromGeometry(name, fixture.Width, fixture.Length, fixture.Points, fixture.Edges),
                "missing/oversized track label");
        foreach (var count in new[] { 0, 2, 20001 })
            Check.Throws<ArgumentException>(() => TrackDefinition.FromGeometry("Synthetic", fixture.Width, fixture.Length,
                new TrackPoint[count], new TrackSurfaceEdge[count]), $"unsupported row count {count}");
        Check.Throws<ArgumentException>(() => TrackDefinition.FromGeometry("Synthetic", fixture.Width, fixture.Length,
            fixture.Points, fixture.Edges[..^1]), "point/edge row mismatch");
        foreach (var dimension in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            Check.Throws<ArgumentOutOfRangeException>(() => TrackDefinition.FromGeometry("Synthetic", dimension,
                fixture.Length, fixture.Points, fixture.Edges), "invalid track width");
            Check.Throws<ArgumentOutOfRangeException>(() => TrackDefinition.FromGeometry("Synthetic", fixture.Width,
                dimension, fixture.Points, fixture.Edges), "invalid track length");
        }
        Action<Fixture>[] invalid =
        [
            item => item.Points[1] = null!,
            item => item.Points[0] = item.Points[0] with { S = 1 },
            item => item.Points[1] = item.Points[1] with { S = 0 },
            item => item.Points[1] = item.Points[1] with { S = -1 },
            item => item.Points[^1] = item.Points[^1] with { S = item.Length },
            item => item.Points[0] = item.Points[0] with { X = double.NaN },
            item => item.Points[0] = item.Points[0] with { Y = double.PositiveInfinity },
            item => item.Points[0] = item.Points[0] with { Heading = double.NaN },
            item => item.Points[0] = item.Points[0] with { Bank = double.PositiveInfinity },
            item => item.Points[0] = item.Points[0] with { Width = 0 },
            item => item.Edges[0] = item.Edges[0] with { Left = new(double.NaN, 0, 0) },
            item => item.Edges[0] = item.Edges[0] with { Right = new(0, double.PositiveInfinity, 0) },
            item => item.Edges[0] = item.Edges[0] with { Right = item.Edges[0].Left },
            item => item.Edges[1] = item.Edges[1] with { Left = item.Edges[0].Left },
            item => item.Edges[1] = item.Edges[1] with { Right = item.Edges[0].Right },
            item => item.Points[1] = item.Points[1] with { X = item.Points[0].X, Z = item.Points[0].Z }
        ];
        for (var index = 0; index < invalid.Length; index++)
        {
            var candidate = Geometry();
            invalid[index](candidate);
            Check.Throws<ArgumentException>(() => Create(candidate), $"malformed geometry case {index}");
        }

        var aliased = Geometry();
        var track = Create(aliased);
        var savedPoint = track.Points[0];
        var savedEdge = track.Edges[0];
        aliased.Points[0] = aliased.Points[0] with { X = 9999, Y = -9999 };
        aliased.Edges[0] = default;
        Check.Equal(savedPoint, track.Points[0], "factory must clone the caller's point array");
        Check.Equal(savedEdge, track.Edges[0], "factory must clone the caller's exact-edge array");
        Check.Near(7.5, track.Sample(-6.5 / 3, 1.0 / 3).Y, "caller array mutation cannot change canonical road");
    }

    private static TrackDefinition Create(Fixture fixture) => TrackDefinition.FromGeometry(
        "Synthetic exact-edge factory strip", fixture.Width, fixture.Length, fixture.Points, fixture.Edges);

    private static Fixture Geometry()
    {
        var imported = TrackChecks.Import();
        var edges = imported.Segments.Select(segment =>
        {
            var edge = imported.Edges[segment.StartEdgeIndex];
            return new TrackSurfaceEdge(new(edge.Left.X, edge.Left.Y, edge.Left.Z),
                new(edge.Right.X, edge.Right.Y, edge.Right.Z));
        }).ToArray();
        return new(imported.Points, edges, imported.Width, imported.Length);
    }

    private sealed record Fixture(TrackPoint[] Points, TrackSurfaceEdge[] Edges, double Width, double Length);
}
