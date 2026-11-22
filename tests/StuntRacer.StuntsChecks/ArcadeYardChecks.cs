using System.Text.Json;
using StuntRacer.Core;
using StuntRacer.Core.Arcade;

internal static class ArcadeYardChecks
{
    private const double Step = 1d / 120;
    private static readonly DriverInput Forward = new(0, 1, 0, false);

    public static void DriveAndRecover()
    {
        var game = Started();
        var launch = game.Snapshot.Player;
        var before = game.Activity;
        var oldTick = game.Snapshot.Tick;
        game.Advance(Step / 2, Forward);
        Require(game.Snapshot.Tick == oldTick && game.Snapshot.Player.Z == launch.Z, "a partial tick moved the real car");
        var render = game.Advance(Step, Forward);
        var firstTick = game.Snapshot;
        Require(firstTick.Tick == oldTick + 1 && firstTick.Time == Step && firstTick.Player.Z > launch.Z &&
            render.Player.Z > launch.Z && render.Player.Z < firstTick.Player.Z && render.Time == firstTick.Time,
            "120 Hz native displacement and interpolated browser pose disagree");

        TickUntil(game, 2.2, Forward);
        var prop = game.World.Props.First(p => p.Id == "launch-1");
        var hit = game.Activity.Props.Single(p => p.Id == prop.Id);
        Require(game.Snapshot.Player.Z > launch.Z + 15 && game.Activity.Score == 100 &&
            game.Activity.PropsHit == 1 && hit is { Hit: true, HitTick: > 0 } &&
            hit.HitTick <= game.Snapshot.Time * 120 && game.Activity.Success is null,
            "centered throttle did not physically reach the first light cube");
        Require(before.Props.All(p => !p.Hit), "a later hit mutated an earlier activity snapshot");

        var json = JsonSerializer.SerializeToElement(new { game.World, game.Activity },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var wire = json.GetProperty("activity");
        var renderedProp = wire.GetProperty("props").EnumerateArray().Single(p => p.GetProperty("id").GetString() == prop.Id);
        Require(wire.GetProperty("score").GetInt32() == 100 && renderedProp.GetProperty("hit").GetBoolean() &&
            renderedProp.GetProperty("hitTick").GetInt64() == hit.HitTick &&
            renderedProp.GetProperty("x").GetDouble() == prop.X && renderedProp.GetProperty("z").GetDouble() == prop.Z &&
            json.GetProperty("world").GetProperty("bounds").GetProperty("maxZ").GetDouble() == game.World.Bounds.MaxZ,
            "native prop contact/bounds do not survive the browser wire format");

        var moving = game.Snapshot;
        var remaining = game.Activity.TimeRemaining;
        game.Command("pause");
        game.Advance(0.25, new(1, 1, 0, true));
        Require(game.Snapshot.Phase == "paused" && game.Snapshot.Tick == moving.Tick &&
            Distance(moving.Player, game.Snapshot.Player) == 0 && game.Activity.TimeRemaining == remaining,
            "pause let the car or the active deadline advance");
        game.Command("resume");
        TickUntil(game, 8, Forward);
        var stoppedAtRail = game.Snapshot.Player;
        var scored = game.Activity;
        Require(stoppedAtRail.Z > game.World.Bounds.MaxZ - 4 && stoppedAtRail.Z < game.World.Bounds.MaxZ &&
            stoppedAtRail.Grounded && game.Snapshot.Recoveries == 0, "the authored back rail did not contain the moving car");
        Require(scored is { Score: 300, PropsHit: 3, Success: null } &&
            scored.Props.Where(p => p.Hit).All(p => p.Id.StartsWith("launch-", StringComparison.Ordinal)),
            "driving past the branch cubes at a distance awarded points or a win");

        game.Command("recover");
        Require(game.Snapshot.Recoveries == 1 && Distance(game.Snapshot.Player, launch) == 0 &&
            game.Snapshot.Player.Boost == stoppedAtRail.Boost && game.Activity.TimeRemaining == scored.TimeRemaining - 2,
            "recovery did not preserve boost, return to the launch or spend two seconds");
        TickUntil(game, 12, Forward);
        Require(game.Snapshot.Player.Z > -2 && game.Activity.Score == scored.Score &&
            game.Activity.Props.Select(p => p.HitTick).SequenceEqual(scored.Props.Select(p => p.HitTick)),
            "driving through the old contact points after recovery scored the same props again");
    }

    public static void WinBothBranches()
    {
        foreach (var (steer, color) in new[] { (-1, "gold"), (1, "coral") })
        {
            var game = Started();
            var prior = game.Snapshot.Player;
            for (var tick = 0; tick < 9 * 120 && game.Snapshot.Phase == "racing"; tick++)
            {
                prior = game.Snapshot.Player;
                game.Advance(Step, Forward with { Steer = game.Activity.Elapsed < 2.85 ? 0 : steer });
            }
            var finish = game.Snapshot;
            var outcome = game.Activity;
            Require(finish.Phase == "finished" && outcome.Success == true && outcome.Score == outcome.ScoreGoal &&
                outcome.PropsHit == 7 && outcome.TimeRemaining > 20 && finish.Recoveries == 0 &&
                outcome.Props.Count(p => p.Hit && p.Id.StartsWith(color + "-", StringComparison.Ordinal)) == 4,
                $"the real {color} turn could not win: {outcome.Score} pts at {outcome.Elapsed:F2}s");
            Require(finish.Player.X * steer > 20 && Distance(finish.Player, prior) is > 0 and < 0.5 &&
                outcome.Props.Where(p => p.Hit).Max(p => p.HitTick) == (long)Math.Round(finish.Time * 120),
                $"the {color} finish did not follow the final contact tick's moving pose");
            game.Advance(0.25, Forward);
            Require(game.Snapshot.Tick == finish.Tick && Distance(game.Snapshot.Player, finish.Player) == 0,
                "the car continued after the result");
            game.Command("restart");
            Require(game.Snapshot.Phase == "countdown" && game.Activity is { Score: 0, Success: null } &&
                game.Activity.Props.All(p => !p.Hit) && outcome.Props.Count(p => p.Hit) == 7,
                "restart did not reset the prop field or overwrote the previous outcome");
        }
    }

    public static void Lifecycle()
    {
        var game = new ArcadeYardSession();
        var start = game.Advance(0.25, Forward);
        Require(start is { Phase: "ready", Tick: 0 } && game.Activity.TimeRemaining == 30 &&
            game.Track.Name == game.World.Name && game.World.Props.Select(p => p.Id).Distinct().Count() == game.Activity.PropCount,
            "ready did not expose a fresh, uniquely identified fictional yard");
        game.Command("start");
        game.Advance(0.25, Forward);
        var countdown = game.Snapshot;
        game.Command("pause");
        game.Advance(0.25, Forward);
        Require(game.Snapshot.Phase == "paused" && game.Snapshot.Countdown == countdown.Countdown &&
            game.Snapshot.Tick == countdown.Tick, "the paused countdown still advanced");
        game.Command("resume");
        FinishCountdown(game);
        TickUntil(game, 30 - Step, default);
        Require(game.Snapshot.Phase == "racing" && game.Activity.TimeRemaining == Step &&
            game.Activity.PropsHit == 0 && game.Activity.Success is null, "timeout fired before the last permitted tick");
        var finish = game.Advance(0.25, default);
        Require(finish.Phase == "finished" && finish.Tick == 3_780 && finish.Time == 30 &&
            game.Activity is { Score: 0, TimeRemaining: 0, Success: false } &&
            Distance(finish.Player, start.Player) == 0, "timeout did not stop exactly on its native deadline");
        game.Command("start");
        FinishCountdown(game);
        TickUntil(game, 2.2, Forward);
        Require(game.Activity.PropsHit == 1 && game.Activity.TimeRemaining > 27 && game.Activity.Success is null,
            "starting from a result did not create a fresh, playable run");
        game.Command("garage");
        Require(game.Snapshot is { Phase: "ready", Tick: 0 } && game.Activity is { Score: 0, Success: null },
            "returning to the garage left a live arcade run");
    }

    private static ArcadeYardSession Started()
    {
        var game = new ArcadeYardSession();
        game.Command("start");
        FinishCountdown(game);
        return game;
    }

    private static void FinishCountdown(ArcadeYardSession game)
    {
        for (var i = 0; i < 6 && game.Snapshot.Phase == "countdown"; i++)
            game.Advance(0.25, Forward);
        Require(game.Snapshot.Phase == "racing" && game.Activity.Elapsed == 0 &&
            game.Snapshot.Player.Z == game.World.Spawn.Z, "the countdown consumed drive time or moved the car");
    }

    private static void TickUntil(ArcadeYardSession game, double elapsed, DriverInput input)
    {
        var budget = (int)Math.Ceiling((elapsed - game.Activity.Elapsed) / Step) + 1;
        for (var i = 0; i < budget && game.Snapshot.Phase == "racing" && game.Activity.Elapsed < elapsed; i++)
            game.Advance(Step, input);
    }

    private static double Distance(CarSnapshot a, CarSnapshot b) =>
        Math.Max(Math.Abs(a.Y - b.Y), Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)));

    private static void Require(bool result, string message)
    {
        if (!result)
            throw new InvalidOperationException(message);
    }
}
