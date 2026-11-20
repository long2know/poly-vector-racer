using System.Text.Json;
using StuntRacer.Core;

internal static partial class Program
{
    private static void SpatialStuntGeometry()
    {
        TrackPoint[] points =
        [
            new(0, 0, 0, 0, 0, 0, 4, true),
            new(10, 0, 10, 0, 0, 0, 4, true),
            new(20, 0, 10, 10, 0, 0, 4, true),
            new(30, 0, 0, 10, 0, 0, 4, true)
        ];
        var edges = points.Select(p => new TrackSurfaceEdge(
            new(p.X - 2, p.Y, p.Z), new(p.X + 2, p.Y, p.Z))).ToArray();
        try
        {
            TrackDefinition.FromGeometry("Vertical must not become a heightfield", 4, 40, points, edges);
            throw new InvalidOperationException("The legacy heightfield path accepted vertical triangles.");
        }
        catch (ArgumentException) { }
        var track = TrackDefinition.FromSpatialGeometry("Spatial contact fixture", 4, 40, points, edges);
        Require(track.IsSpatial && track.Edges[1].Left.Y == 10, "Vertical edge data was flattened.");
        Near(track.AtDistance(5).Y, 5, 1e-12, "spatial centerline interpolation");
        try
        {
            track.Sample(0, 0);
            throw new InvalidOperationException("The heightfield sampler silently accepted a spatial track.");
        }
        catch (NotSupportedException) { }
        var broken = (TrackSurfaceEdge[])edges.Clone();
        broken[0] = new(broken[0].Left, broken[0].Left);
        try
        {
            TrackDefinition.FromSpatialGeometry("Degenerate spatial fixture", 4, 40, points, broken);
            throw new InvalidOperationException("A zero-area spatial contact triangle was accepted.");
        }
        catch (ArgumentException) { }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(track,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Require(json.RootElement.GetProperty("isSpatial").GetBoolean(), "The renderer cannot identify spatial geometry.");
    }

    private static void HardDrivingProvingGround()
    {
        var track = HardDrivingCourse.Create();
        Require(track.Length > 1800 && track.Points.Max(p => p.Y) > 25 &&
            track.Points.Min(p => p.Width) < 11 && track.Points.Max(p => p.Width) == 15,
            "The proving ground lacks its highway, high climb or narrow precision section.");
        Require(track.Points.Any(p => Math.Abs(p.Bank) > 0.3), "The proving ground has no substantial banked bend.");
        var game = new GameSimulation(track);
        game.Command("mode-checkpoint-run");
        game.Command("start");
        Run(game, 3, Idle);
        for (var tick = 0; tick < 120 * 240 && game.Snapshot.Phase == "racing"; tick++)
            Validate(game.Advance(Step, Drive(game, false)));
        Require(game.Snapshot.Trial is { Success: true, CheckpointsPassed: 12 } &&
            game.Snapshot.Recoveries == 0, $"The actual timed proving-ground drive failed: {game.Snapshot.Message}, gates={game.Snapshot.Trial?.CheckpointsPassed}, recoveries={game.Snapshot.Recoveries}.");
    }

    private static void GarageNavigation()
    {
        IGameSession[] sessions = [new GameSimulation(), new StuntRacer.Core.City.CityDrivingSession(),
            new StuntRacer.Core.Stunts.StuntParkSession(), StuntRacer.Core.Stunts.StuntParkSession.CreateHardDriving()];
        foreach (var session in sessions)
        {
            session.Command("start");
            for (var i = 0; i < 600; i++) session.Advance(Step, new(0, 1, 0, false));
            session.Command("pause");
            session.Command("garage");
            Require(session.Snapshot.Phase == "ready" && session.Snapshot.Time == 0,
                $"{session.GetType().Name} did not return to a fresh ready menu.");
            var ready = session.Snapshot;
            session.Advance(.1, new(1, 1, 0, true));
            Require(JsonSerializer.Serialize(session.Snapshot.Player) == JsonSerializer.Serialize(ready.Player) &&
                session.Snapshot.Tick == ready.Tick,
                "A garage session continued driving.");
        }
        var game = NewRace();
        for (var i = 0; i < 120 * 75 && game.Snapshot.Lap == 1; i++)
            game.Advance(Step, Drive(game, false));
        var best = game.Snapshot.Ghost!.Duration;
        Require(best > 0, "The garage preservation check needs a genuinely recorded complete lap.");
        game.Command("garage");
        Require(game.Snapshot.Phase == "ready" && game.Snapshot.Ghost!.Duration == best &&
            game.Snapshot.BestLap > 0, "Returning to the garage erased the actual best lap/ghost.");
    }
}
