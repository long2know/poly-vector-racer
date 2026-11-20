using StuntRacer.Core;

internal static partial class Program
{
    private static void IncludedCatalogLaps()
    {
        Require(AuthoredTrackCatalog.Entries.Select(choice => choice.Id).Order()
            .SequenceEqual(new[] { "foundry", "ridgeline", "skyway" }), "included catalog factory inventory changed");
        var spawnFailures = new List<string>();
        foreach (var choice in AuthoredTrackCatalog.Entries)
        {
            var track = AuthoredTrackCatalog.Create(choice.Id);
            var independent = AuthoredTrackCatalog.Create(choice.Id);
            Require(!ReferenceEquals(track, independent) && !ReferenceEquals(track.Points, independent.Points) &&
                    !ReferenceEquals(track.Edges, independent.Edges), $"{choice.Id}: factories share mutable geometry arrays");
            Require(track.Name == independent.Name && track.Length == independent.Length &&
                    track.Width == independent.Width && track.Points.SequenceEqual(independent.Points) &&
                    track.Edges.SequenceEqual(independent.Edges), $"{choice.Id}: factory geometry is not reproducible");
            if (choice.Id == "foundry")
            {
                var original = new GameSimulation().Track;
                Require(track.Name == original.Name && track.Length == original.Length && track.Width == original.Width &&
                        track.Points.SequenceEqual(original.Points) && track.Edges.SequenceEqual(original.Edges),
                    "catalog Foundry differs from the original default/golden circuit");
            }

            var points = track.Points;
            var edges = track.Edges;
            Require(double.IsFinite(track.Length) && track.Length > 0 && double.IsFinite(track.Width) && track.Width > 0 &&
                    points.Length >= 3 && edges.Length == points.Length, $"{choice.Id}: invalid canonical geometry dimensions");
            Near(points[0].S, 0, 0, $"{choice.Id}: canonical start distance");
            double lastS = -1, segmentSum = 0, maximumInteriorSegment = 0, maximumHeightError = 0;
            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];
                var edge = edges[i];
                Require(new[] { point.S, point.X, point.Y, point.Z, point.Heading, point.Bank, point.Width,
                        edge.Left.X, edge.Left.Y, edge.Left.Z, edge.Right.X, edge.Right.Y, edge.Right.Z }.All(double.IsFinite) &&
                        point.S > lastS && point.S < track.Length && point.Width > 0 && point.Road,
                    $"{choice.Id}: non-finite, unordered, or missing canonical sample {i}");
                lastS = point.S;
                Near((edge.Left.X + edge.Right.X) / 2, point.X, 1e-9, $"{choice.Id}: edge/center X {i}");
                Near((edge.Left.Y + edge.Right.Y) / 2, point.Y, 1e-9, $"{choice.Id}: edge/center Y {i}");
                Near((edge.Left.Z + edge.Right.Z) / 2, point.Z, 1e-9, $"{choice.Id}: edge/center Z {i}");
                var next = (i + 1) % points.Length;
                var segment = Distance(point, points[next]);
                Require(segment > 0 && double.IsFinite(segment), $"{choice.Id}: degenerate segment {i}");
                segmentSum += segment;
                if (next != 0)
                    maximumInteriorSegment = Math.Max(maximumInteriorSegment, segment);
                Probe(edge.Left, edge.Right, edges[next].Left, i);
                Probe(edge.Right, edges[next].Right, edges[next].Left, i);
            }
            Near(segmentSum, track.Length, 1e-8, $"{choice.Id}: canonical distance does not close");
            Require(Distance(points[^1], points[0]) <= maximumInteriorSegment * 2,
                $"{choice.Id}: closing seam is an unexplained gap");
            var wrapped = track.AtDistance(track.Length);
            Near(wrapped.X, points[0].X, 1e-9, $"{choice.Id}: wrapped center X");
            Near(wrapped.Y, points[0].Y, 1e-9, $"{choice.Id}: wrapped center Y");
            Near(wrapped.Z, points[0].Z, 1e-9, $"{choice.Id}: wrapped center Z");

            var game = new GameSimulation(track);
            var isolated = new GameSimulation(independent);
            var isolatedReady = isolated.Snapshot;
            var spawn = game.Snapshot;
            Validate(spawn);
            var center = track.Sample(spawn.Player.X, spawn.Player.Z);
            Require(ReferenceEquals(game.Track, track) && spawn.Phase == "ready" && spawn.Player.Grounded && center.Road,
                $"{choice.Id}: constructor did not use a supported canonical start");
            Near(spawn.Player.Y - center.Y, 1, 1e-9, $"{choice.Id}: spawn COM height");
            foreach (var right in new[] { -0.9, 0.9 })
            foreach (var forward in new[] { -1.3, 1.3 })
            {
                var yaw = spawn.Player.Yaw;
                var road = track.Sample(spawn.Player.X + Math.Cos(yaw) * right + Math.Sin(yaw) * forward,
                    spawn.Player.Z - Math.Sin(yaw) * right + Math.Cos(yaw) * forward);
                var height = spawn.Player.Y + Math.Sin(spawn.Player.Pitch) * forward +
                    Math.Sin(spawn.Player.Roll) * right - road.Y;
                if (!(road.Road && height is > 0.5 and < 1.3))
                    spawnFailures.Add(
                        $"{choice.Id}: spawn wheel right={right:F1}, forward={forward:F1} unsupported/outside travel; " +
                        $"road={road.Road}, height={height:R}, pose pitch={spawn.Player.Pitch:R}, roll={spawn.Player.Roll:R}, " +
                        $"center=({spawn.Player.X:R},{spawn.Player.Y:R},{spawn.Player.Z:R})");
            }

            game.Command("start");
            Run(game, 3, Idle);
            double travelled = 0, previousS = 0;
            for (var frame = 0; frame < 60 * 180 && game.Snapshot.Lap == 1 && game.Snapshot.Phase == "racing"; frame++)
            {
                game.Advance(1.0 / 60, Drive(game, false));
                var snapshot = game.Snapshot;
                Validate(snapshot);
                var s = snapshot.Progress * track.Length;
                travelled += Math.IEEERemainder(s - previousS, track.Length);
                previousS = s;
                Require(snapshot.Recoveries == 0,
                    $"{choice.Id}: public-input controller required recovery at {snapshot.Time:F3}s, " +
                    $"progress={snapshot.Progress:F4}, position=({snapshot.Player.X:F2},{snapshot.Player.Y:F2},{snapshot.Player.Z:F2})");
            }
            var lap = game.Snapshot;
            Require(lap.Lap == 2 && lap.BestLap > 0 && lap.Phase == "racing" &&
                    travelled >= track.Length && travelled < track.Length + 2,
                $"{choice.Id}: no genuine ordered circuit within 180s; lap={lap.Lap}, time={lap.Time:F3}, travelled={travelled:F3}m");
            Near(lap.BestLap + lap.LapTime, lap.Time, 1e-9, $"{choice.Id}: first-lap timing");
            Require(lap.LapTime is >= 0 and < 1.0 / 60,
                $"{choice.Id}: first-lap result included more than the completing display frame");
            Same(isolatedReady, isolated.Snapshot, $"{choice.Id}: driving one factory instance changed another session");
            Require(isolated.Snapshot.Ghost is { Duration: 0, SampleCount: 0, Car: null },
                $"{choice.Id}: factory sessions leaked a recorded lap");
            Console.WriteLine($"  catalog {choice.Id}: length={track.Length:F3}m, lap={lap.BestLap:F6}s, " +
                              $"distance={travelled:F3}m, recoveries=0, triangle error={maximumHeightError:E2}m");

            void Probe(TrackSurfaceVertex a, TrackSurfaceVertex b, TrackSurfaceVertex c, int segment)
            {
                var x = a.X * 0.21 + b.X * 0.33 + c.X * 0.46;
                var y = a.Y * 0.21 + b.Y * 0.33 + c.Y * 0.46;
                var z = a.Z * 0.21 + b.Z * 0.33 + c.Z * 0.46;
                var sample = track.Sample(x, z);
                Require(sample.Road && new[] { sample.S, sample.X, sample.Y, sample.Z, sample.Heading, sample.Bank,
                        sample.Width, sample.Lateral, sample.SlopeX, sample.SlopeZ }.All(double.IsFinite),
                    $"{choice.Id}: rendered triangle has no finite canonical collision surface at segment {segment}");
                maximumHeightError = Math.Max(maximumHeightError, Math.Abs(sample.Y - y));
                Near(sample.Y, y, 1e-8, $"{choice.Id}: rendered/collision triangle height at segment {segment}");
            }
        }
        Require(spawnFailures.Count == 0, string.Join("; ", spawnFailures));
    }
}
