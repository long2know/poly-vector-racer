using System.Text.Json;
using StuntRacer.Core;

internal static partial class Program
{
    private static void CheckpointClock()
    {
        var game = new GameSimulation();
        game.Command("mode-checkpoint-run");
        var ready = game.Snapshot;
        var initial = 8 + game.Track.Length / 240;
        Require(game.GameMode == "checkpoint-run" && ready.Laps == 1 && ready.Phase == "ready" &&
                ready.Trial is { Success: null, CheckpointsPassed: 0, CheckpointCount: 12, LastBonus: 0, Elapsed: 0 } &&
                ready.Ghost is null && ready.Race is null && ready.PaceCar is null, "trial ready state/schema is incoherent");
        Near(ready.Trial!.TimeRemaining, initial, 1e-10, "trial initial budget");
        Near(ready.Trial.NextCheckpointProgress, 1.0 / 12, 1e-12, "first timed gate marker");
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(ready,
                   new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))
            Fields(json.RootElement.GetProperty("trial"), "timeRemaining", "checkpointsPassed", "checkpointCount",
                "nextCheckpointProgress", "lastBonus", "success", "elapsed");
        game.Advance(1000, new DriverInput(1, 1, 0, true));
        Same(ready, game.Snapshot, "ready trial moved");
        Require(ready.Trial == game.Snapshot.Trial, "ready trial consumed its clock");
        game.Command("start");
        Run(game, 1, Idle);
        Throws<InvalidOperationException>(() => game.Command("mode-checkpoint-run"));
        Throws<InvalidOperationException>(() => game.Command("mode-time-attack"));
        game.Command("pause");
        var pausedCountdown = game.Snapshot;
        game.Advance(1000, new DriverInput(1, 1, 0, true));
        Same(pausedCountdown, game.Snapshot, "trial countdown pause advanced");
        Require(pausedCountdown.Trial == game.Snapshot.Trial, "trial countdown consumed checkpoint time");
        game.Command("resume");
        Run(game, 2, Idle);
        Require(game.Snapshot.Phase == "racing" && game.Snapshot.Trial == ready.Trial,
            "trial timer ran during the three-second countdown");
        Run(game, 2, new DriverInput(0, 1, 0, false));
        game.Command("pause");
        var paused = game.Snapshot;
        game.Advance(1000, new DriverInput(1, 1, 0, true));
        game.Command("recover");
        Same(paused, game.Snapshot, "paused trial changed control/time");
        Require(paused.Trial == game.Snapshot.Trial, "pause/recovery changed trial timer");
        game.Command("mode-time-attack");
        Require(game.GameMode == "time-attack" && game.Snapshot.Phase == "ready" &&
                game.Snapshot.Trial is null && game.Snapshot.Laps == 3, "trial mode switch did not reset ready state");

        var expired = NewTrial();
        for (var tick = 0; tick < 120 * 30 && expired.Snapshot.Phase != "finished"; tick++)
            ValidateTrial(expired.Advance(Step, Idle));
        var finish = expired.Snapshot;
        Require(finish.Phase == "finished" && finish.Trial is { Success: false, TimeRemaining: 0, CheckpointsPassed: 0 } &&
                finish.Message.StartsWith("TIME EXPIRED", StringComparison.Ordinal), "idle trial did not genuinely expire");
        Near(finish.Trial!.Elapsed, Math.Ceiling(initial / Step) * Step, 1e-9, "fixed-tick idle deadline");
        expired.Advance(1000, new DriverInput(1, 1, 0, true));
        expired.Command("recover");
        Same(finish, expired.Snapshot, "expired trial did not freeze controls/timers");
        Require(finish.Trial == expired.Snapshot.Trial, "expired trial timer changed");
        expired.Command("restart");
        Require(expired.Snapshot.Phase == "countdown" &&
                expired.Snapshot.Trial is { Elapsed: 0, Success: null, CheckpointsPassed: 0, LastBonus: 0 },
            "trial restart retained its failure/counters");
        Near(expired.Snapshot.Trial!.TimeRemaining, initial, 1e-10, "trial restart budget");

        var penalty = NewTrial();
        Run(penalty, 7, Idle);
        var before = penalty.Snapshot;
        penalty.Command("recover");
        var penalized = penalty.Snapshot;
        Require(penalized.Phase == "finished" && penalized.Trial is { Success: false, TimeRemaining: 0, CheckpointsPassed: 0 },
            "recovery did not immediately consume the remaining checkpoint clock");
        Near(penalized.Time, before.Time + 5, 1e-12, "trial +5s recovery cost");
        Require(penalized.Tick == before.Tick && penalized.Recoveries == 1, "manual recovery advanced physics or lost its count");
        Console.WriteLine($"  checkpoint initial={initial:F6}s; idle expiry={finish.Time:F6}s; " +
                          $"recovery at 7s expires immediately at {penalized.Time:F3}s");
    }

    private static void CheckpointCircuit()
    {
        var game = NewTrial();
        var bonus = game.Track.Length / 240;
        var initial = 8 + bonus;
        var passed = 0;
        for (var tick = 0; tick < 120 * 90 && game.Snapshot.Phase != "finished"; tick++)
        {
            var snapshot = game.Advance(Step, Drive(game, false));
            ValidateTrial(snapshot);
            var trial = snapshot.Trial!;
            Require(trial.CheckpointsPassed is >= 0 and <= 12 && trial.CheckpointsPassed <= passed + 1,
                "one tick skipped timed gates");
            if (trial.CheckpointsPassed > passed)
            {
                passed++;
                Near(trial.LastBonus, bonus, 1e-12, "gate extension was not the authored amount");
                Near(trial.NextCheckpointProgress, passed >= 12 ? 0 : (passed + 1) % 12 / 12.0,
                    1e-12, "next timed arch does not match the ordered gate");
            }
            Near(trial.TimeRemaining + trial.Elapsed, initial + passed * bonus, 1e-8,
                "trial did not consume/award time exactly once");
        }
        var finish = game.Snapshot;
        Require(finish.Phase == "finished" && finish.Lap == 1 && finish.Laps == 1 && finish.Recoveries == 0 &&
                finish.Trial is { Success: true, CheckpointsPassed: 12 } &&
                finish.Message.StartsWith("CHECKPOINT RUN COMPLETE", StringComparison.Ordinal),
            $"public-input checkpoint circuit did not succeed: {finish.Message}, passed={finish.Trial!.CheckpointsPassed}");
        game.Advance(1000, new DriverInput(1, 1, 0, true));
        game.Command("recover");
        Same(finish, game.Snapshot, "successful trial did not freeze its car");
        Require(finish.Trial == game.Snapshot.Trial, "successful trial did not freeze its remaining clock");

        var reversing = NewTrial();
        for (var tick = 0; tick < 120 * 15 && reversing.Snapshot.Trial!.CheckpointsPassed == 0; tick++)
            reversing.Advance(Step, Drive(reversing, false));
        Require(reversing.Snapshot.Trial!.CheckpointsPassed == 1, "anti-farm trace never reached its first real gate");
        var gate = reversing.Track.Length / 12;
        for (var tick = 0; tick < 120 * 10 &&
             reversing.Snapshot.Progress * reversing.Track.Length > gate - 1 &&
             reversing.Snapshot.Phase == "racing"; tick++)
            reversing.Advance(Step, new DriverInput(0, 0, 1, false));
        Require(reversing.Snapshot.Phase == "racing" &&
                reversing.Snapshot.Progress * reversing.Track.Length < gate - 1 &&
                reversing.Snapshot.Player.Speed < 0, "anti-farm trace did not actually reverse across the awarded gate");
        for (var tick = 0; tick < 120 * 5 &&
             reversing.Snapshot.Progress * reversing.Track.Length < gate + 1 &&
             reversing.Snapshot.Phase == "racing"; tick++)
            reversing.Advance(Step, new DriverInput(0, 1, 0, true));
        var repeated = reversing.Snapshot;
        Require(repeated.Phase == "racing" && repeated.Progress * reversing.Track.Length > gate + 1 &&
                repeated.Trial!.CheckpointsPassed == 1, "repeated forward crossing farmed a timed gate");
        Near(repeated.Trial!.TimeRemaining + repeated.Trial.Elapsed, initial + bonus, 1e-8, "reverse/start-line bonus farming");
        reversing.Command("recover");
        Require(reversing.Snapshot.Trial!.CheckpointsPassed == 1 &&
                reversing.Snapshot.Trial.LastBonus == repeated.Trial.LastBonus,
            "recovery farmed or reset ordered bonuses");
        Console.WriteLine($"  all 12 gates in {finish.Trial!.Elapsed:F6}s; bonus={bonus:F6}s, remaining={finish.Trial.TimeRemaining:F6}s; " +
                          "real reverse/forward re-cross awarded nothing");
    }

    private static void CheckpointScalingAndDeadlines()
    {
        var standard = NewTrial();
        var scaledTrack = ScaledCanonicalTrack(2);
        var scaled = new GameSimulation(scaledTrack);
        scaled.Command("mode-checkpoint-run");
        Require(ReferenceEquals(scaled.Track, scaledTrack), "trial replaced the constructor's canonical road");
        Near(scaled.Snapshot.Trial!.TimeRemaining - 8, 2 * (standard.Snapshot.Trial!.TimeRemaining - 8),
            1e-9, "checkpoint budgets did not scale with actual canonical length");

        GameSnapshot before = standard.Snapshot;
        for (var tick = 0; tick < 120 * 15 && standard.Snapshot.Trial!.CheckpointsPassed == 0; tick++)
        {
            before = standard.Snapshot;
            standard.Advance(Step, Drive(standard, false));
        }
        var after = standard.Snapshot;
        Require(after.Trial!.CheckpointsPassed == 1, "deadline reference did not cross a gate");
        var gate = standard.Track.Length / 12;
        var oldS = before.Progress * standard.Track.Length;
        var newS = after.Progress * standard.Track.Length;
        var crossing = before.Time + (gate - oldS) / (newS - oldS) * Step;
        var initial = 8 + standard.Track.Length / 240;
        var earlyWaitTicks = (int)Math.Floor((initial - crossing) / Step);
        // Active idle still has real suspension/contact motion. Calibrate that actual delayed
        // approach in Time Attack, rather than assuming waiting teleports a stationary trace.
        var calibration = NewRace();
        for (var tick = 0; tick < earlyWaitTicks; tick++)
            calibration.Advance(Step, Idle);
        var calibrationBefore = calibration.Snapshot;
        for (var tick = 0; tick < 120 * 15 &&
             (calibration.Snapshot.Progress > 0.5 ||
              calibration.Snapshot.Progress * calibration.Track.Length < gate); tick++)
        {
            calibrationBefore = calibration.Snapshot;
            calibration.Advance(Step, Drive(calibration, false));
        }
        var calibrationAfter = calibration.Snapshot;
        var calibrationOldS = calibrationBefore.Progress * calibration.Track.Length;
        var calibrationNewS = calibrationAfter.Progress * calibration.Track.Length;
        Require(calibrationOldS < gate && calibrationNewS >= gate,
            "delayed calibration did not actually cross the first gate");
        var actualCrossing = calibrationBefore.Time +
            (gate - calibrationOldS) / (calibrationNewS - calibrationOldS) * Step;
        earlyWaitTicks += (int)Math.Floor((initial - actualCrossing) / Step);
        var early = DelayedGate(earlyWaitTicks);
        var late = DelayedGate(earlyWaitTicks + 1);
        Require(early.Phase == "racing" && early.Trial!.CheckpointsPassed == 1,
            $"pre-deadline trace lost bonus: reference crossing={crossing:R}, budget={initial:R}, wait={earlyWaitTicks}; " +
            $"actual phase={early.Phase}, time={early.Time:R}, s={early.Progress * standard.Track.Length:R}, " +
            $"gate={gate:R}, passed={early.Trial!.CheckpointsPassed}");
        Require(late.Phase == "finished" && late.Trial is { Success: false, CheckpointsPassed: 0, LastBonus: 0 },
            $"post-deadline trace survived: calibrated crossing={actualCrossing:R}, wait={earlyWaitTicks + 1}; " +
            $"phase={late.Phase}, time={late.Time:R}, passed={late.Trial!.CheckpointsPassed}");
        Console.WriteLine($"  2x canonical length => {scaled.Snapshot.Trial.TimeRemaining:F6}s initial; " +
                          $"one tick separates legitimate gate bonus from expiry ({earlyWaitTicks}/{earlyWaitTicks + 1} idle ticks)");

        GameSnapshot DelayedGate(int waitTicks)
        {
            var simulation = NewTrial();
            for (var i = 0; i < waitTicks; i++)
                simulation.Advance(Step, Idle);
            for (var i = 0; i < 120 * 15 && simulation.Snapshot.Phase == "racing" &&
                 simulation.Snapshot.Trial!.CheckpointsPassed == 0; i++)
                simulation.Advance(Step, Drive(simulation, false));
            return simulation.Snapshot;
        }
    }

    private static void GhostPlayback()
    {
        var game = NewRace();
        Require(game.Snapshot.Ghost is { Car: null, Duration: 0, Status: "recording", SampleCount: 1, Enabled: true },
            "an imaginary ghost exists before a real completed lap");
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(game.Snapshot,
                   new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))
            Fields(json.RootElement.GetProperty("ghost"), "car", "duration", "sampleCount", "enabled", "status");
        var first = CaptureLap(game, true);
        var firstGhost = game.Snapshot.Ghost!;
        Near(firstGhost.Duration, first.Duration, 1e-9, "first clean lap was not promoted");
        Require(firstGhost.Car is not null && firstGhost.Status == "playing", "a genuine first clean lap produced no ghost");
        SameCar(first.Poses[0], firstGhost.Car!, "first ghost pose was fabricated instead of recorded");
        var best = CaptureLap(game, false);
        var saved = game.Snapshot.Ghost!;
        Require(best.Duration < first.Duration, "second moving-start lap was not a genuine improvement");
        Near(saved.Duration, best.Duration, 1e-9, "recording did not improve the best ghost during playback");
        var ticks = best.Poses.Count - 1;
        Require(saved.SampleCount == 1 + ticks / 4 + (ticks % 4 == 0 ? 0 : 1),
            "ghost is not authoritative 30Hz samples plus actual lap endpoints");
        game.Command("restart");
        Near(game.Snapshot.Ghost!.Duration, saved.Duration, 1e-12, "restart discarded the best clean lap");
        SameCar(best.Poses[0], game.Snapshot.Ghost.Car!, "restart did not use the actual saved first pose");
        Require(game.Snapshot.Ghost.Car!.Speed > 10 && game.Snapshot.Player.Speed == 0,
            "restart replaced a genuine moving-start best ghost with a fabricated stationary car");
        Run(game, 3, Idle);
        var maximumError = 0.0;
        var interpolationOffset = 0.0;
        var subSampleChecks = 0;
        for (var tick = 1; tick <= ticks + 120; tick++)
        {
            game.Advance(Step, Idle);
            var replay = game.Snapshot.Ghost!;
            var expected = RecordedPoseAt(best.Poses, tick);
            SameGhostPose(expected, replay.Car!, $"actual recorded trajectory playback at tick {tick}");
            maximumError = Math.Max(maximumError, PositionError(expected, replay.Car!));
            if (tick < ticks && tick % 4 != 0)
            {
                subSampleChecks++;
                interpolationOffset = Math.Max(interpolationOffset, PositionError(expected, best.Poses[tick]));
            }
            if (tick == 61)
            {
                game.Command("pause");
                var paused = game.Snapshot;
                game.Advance(1000, new DriverInput(1, 1, 0, true));
                Same(paused, game.Snapshot, "ghost playback pause altered its player");
                SameCar(paused.Ghost!.Car!, game.Snapshot.Ghost!.Car!, "ghost moved while paused");
                game.Command("resume");
            }
        }
        Require(subSampleChecks > 100 && interpolationOffset > 1e-5,
            "playback comparison never exercised real interpolation between compact stored poses");
        SameCar(best.Poses[^1], game.Snapshot.Ghost!.Car!, "over-duration playback looped instead of holding the recorded last pose");
        Console.WriteLine($"  genuine best={saved.Duration:F6}s / {saved.SampleCount} poses; " +
                          $"{subSampleChecks} sub-sample comparisons, max position error={maximumError:E2}m; restart/hold verified");
    }

    private static void GhostCleanAndIsolation()
    {
        var visible = NewRace();
        var hidden = NewRace();
        hidden.Command("ghost-off");
        for (var tick = 0; tick < 120 * 90 && visible.Snapshot.Lap == 1; tick++)
        {
            var input = Drive(visible, false);
            visible.Advance(Step, input);
            hidden.Advance(Step, input);
            Same(visible.Snapshot, hidden.Snapshot, "ghost visibility changed authoritative physics");
            Require(hidden.Snapshot.Ghost is { Enabled: false, Car: null, Status: "disabled" },
                "ghost-off did not hide playback while recording");
        }
        var recorded = visible.Snapshot.Ghost!;
        Require(recorded.Car is not null && hidden.Snapshot.Ghost!.Duration == recorded.Duration &&
                hidden.Snapshot.Ghost.SampleCount == recorded.SampleCount, "hidden mode stopped best-lap recording");
        hidden.Command("ghost-on");
        SameCar(recorded.Car!, hidden.Snapshot.Ghost!.Car!, "ghost-on did not reveal the same actual recording");
        hidden.Command("pause");
        var paused = hidden.Snapshot;
        hidden.Command("ghost-off");
        Same(paused, hidden.Snapshot, "paused ghost toggle changed control/timers");
        hidden.Command("ghost-on");
        SameCar(paused.Ghost!.Car!, hidden.Snapshot.Ghost!.Car!, "paused toggle rewound playback");

        var recovered = NewRace();
        Run(recovered, 2, new DriverInput(0, 1, 0, false));
        var count = recovered.Snapshot.Ghost!.SampleCount;
        recovered.Command("recover");
        Require(recovered.Snapshot.Ghost is { Status: "lap-invalidated", Car: null, Duration: 0 },
            "recovery did not invalidate the current clean-lap recording");
        Run(recovered, 1, new DriverInput(0, 1, 0, false));
        Require(recovered.Snapshot.Ghost!.SampleCount == count, "invalidated recording kept appending poses");
        for (var tick = 0; tick < 120 * 90 && recovered.Snapshot.Lap == 1; tick++)
            recovered.Advance(Step, Drive(recovered, false));
        Require(recovered.Snapshot.Lap == 2 && recovered.Snapshot.Ghost is { Car: null, Duration: 0, Status: "recording" },
            "partial recovered lap was promoted as a clean ghost");
        for (var tick = 0; tick < 120 * 90 && recovered.Snapshot.Lap == 2; tick++)
            recovered.Advance(Step, Drive(recovered, false));
        Require(recovered.Snapshot.Lap == 3 && recovered.Snapshot.Ghost!.Car is not null,
            "a later genuine clean lap could not replace an invalidated attempt");

        var isolated = new GameSimulation(visible.Track);
        Require(isolated.Snapshot.Ghost is { Car: null, Duration: 0, SampleCount: 0, Status: "waiting" },
            "new sessions sharing canonical geometry leaked a ghost recording");
        var differentTrack = new GameSimulation(ScaledCanonicalTrack(1.1));
        Require(differentTrack.Snapshot.Ghost is { Car: null, Duration: 0, SampleCount: 0 },
            "a new track session leaked recorded poses");
        visible.Command("pause");
        visible.Command("mode-race");
        Require(visible.Snapshot.Ghost is null, "physical race exposed a Time Attack ghost");
        visible.Command("mode-checkpoint-run");
        Require(visible.Snapshot.Ghost is null && visible.Snapshot.Trial is not null, "trial exposed a ghost");
        visible.Command("mode-time-attack");
        Require(visible.Snapshot.Ghost is { Car: null, Duration: 0, SampleCount: 0, Status: "waiting" },
            "a true mode switch did not clear the session's ghost buffers");
        var ready = visible.Snapshot;
        visible.Command("ghost-off");
        Same(ready, visible.Snapshot, "ready ghost toggle changed the run");
        Require(visible.Snapshot.Ghost!.Status == "disabled", "ready ghost toggle was ignored");
        Console.WriteLine($"  visible/hidden inputs identical through a full lap; recovery rejects {count} partial poses; " +
                          "later clean lap promotes; session/track/mode isolation verified");
    }

    private static void GhostCapacity()
    {
        var game = NewRace();
        for (var frame = 0; frame < 2400; frame++)
            game.Advance(0.25, Idle);
        var capped = game.Snapshot;
        Require(capped.Phase == "racing" && capped.Time == 600 &&
                capped.Ghost is { Car: null, Duration: 0, SampleCount: 18_000, Status: "capacity-exceeded" },
            $"long real lap did not hit bounded ghost capacity: {capped.Ghost}");
        for (var frame = 0; frame < 40; frame++)
            game.Advance(0.25, Idle);
        Require(game.Snapshot.Ghost!.SampleCount == 18_000, "capacity-exceeded recording appended unbounded samples");
        for (var frame = 0; frame < 60 * 90 && game.Snapshot.Lap == 1; frame++)
            game.Advance(1.0 / 60, Drive(game, false));
        Require(game.Snapshot.Lap == 2 && game.Snapshot.Ghost is { Car: null, Duration: 0, Status: "recording" },
            "capacity-exceeded partial lap was promoted or permanently prevented fresh recording");
        Console.WriteLine("  600s real idle lap capped at 18,000 poses; +10s did not grow; completed partial lap not promoted");
    }

    private static void TrialGhostSchedules()
    {
        var trialReference = TrialTrace(120);
        foreach (var hz in new[] { 30, 60, 144 })
        {
            var actual = TrialTrace(hz);
            Same(trialReference, actual, "timed trial display cadence changed authoritative state");
            Require(trialReference.Trial == actual.Trial, "display cadence changed ordered bonuses/deadline");
        }
        var reference = NewRace();
        var trace = new List<DriverInput>();
        for (var frame = 0; frame < 30 * 90 && reference.Snapshot.Lap == 1; frame++)
        {
            var input = Drive(reference, false);
            trace.Add(input);
            reference.Advance(1.0 / 30, input);
        }
        var saved = reference.Snapshot;
        Require(saved.Lap == 2 && saved.Ghost!.Car is not null, "display trace did not record a real clean lap");
        foreach (var hz in new[] { 30, 60, 144 })
        {
            var game = NewRace();
            foreach (var input in trace)
            {
                var elapsed = 0.0;
                while (elapsed < 1.0 / 30 - 1e-11)
                {
                    var dt = Math.Min(1.0 / hz, 1.0 / 30 - elapsed);
                    game.Advance(dt, input);
                    elapsed += dt;
                }
            }
            var actual = game.Snapshot;
            Same(saved, actual, "time-aligned input trace changed ghost's player");
            Require(actual.Ghost!.SampleCount == saved.Ghost!.SampleCount &&
                    actual.Ghost.Status == saved.Ghost.Status, "display cadence changed fixed-tick capture");
            Near(actual.Ghost.Duration, saved.Ghost.Duration, 1e-10, "display cadence changed saved lap duration");
            SameCar(saved.Ghost.Car!, actual.Ghost.Car!, "display cadence changed recorded playback");
        }
        Console.WriteLine($"  30/60/144Hz timed-gate state identical; real {saved.Ghost!.Duration:F6}s / " +
                          $"{saved.Ghost.SampleCount}-pose ghost identical under time-aligned input");

        static GameSnapshot TrialTrace(int hz)
        {
            var game = NewTrial();
            var elapsed = 0.0;
            while (elapsed < 6 - 1e-11)
            {
                var dt = Math.Min(1.0 / hz, 6 - elapsed);
                game.Advance(dt, new DriverInput(0, 1, 0, false));
                elapsed += dt;
            }
            return game.Snapshot;
        }
    }

    private static (List<CarSnapshot> Poses, double Duration) CaptureLap(GameSimulation game, bool first)
    {
        var start = game.Snapshot;
        var poses = new List<CarSnapshot> { start.Player };
        for (var tick = 0; tick < 120 * 90 && game.Snapshot.Lap == start.Lap; tick++)
        {
            game.Advance(Step, Drive(game, false));
            var snapshot = game.Snapshot;
            Validate(snapshot);
            Require(snapshot.Recoveries == 0, "clean recording controller required recovery");
            if (first && snapshot.Lap == start.Lap)
                Require(snapshot.Ghost!.Car is null && snapshot.Ghost.Duration == 0,
                    "ghost playback appeared before a completed clean lap");
            poses.Add(snapshot.Player);
        }
        Require(game.Snapshot.Lap == start.Lap + 1, "recording trace failed to complete its real circuit");
        return (poses, game.Snapshot.Time - start.Time);
    }

    private static CarSnapshot RecordedPoseAt(List<CarSnapshot> poses, int tick)
    {
        if (tick >= poses.Count - 1)
            return poses[^1];
        var low = tick / 4 * 4;
        var high = Math.Min(low + 4, poses.Count - 1);
        var alpha = (tick - low) / (double)(high - low);
        var a = poses[low];
        var b = poses[high];
        return new CarSnapshot(
            Blend(a.X, b.X), Blend(a.Y, b.Y), Blend(a.Z, b.Z),
            Rotate(a.Yaw, b.Yaw), Rotate(a.Pitch, b.Pitch), Rotate(a.Roll, b.Roll),
            Blend(a.Speed, b.Speed), Blend(a.Steer, b.Steer), Blend(a.Boost, b.Boost), Blend(a.Damage, b.Damage),
            alpha < 0.5 ? a.Grounded : b.Grounded, alpha < 0.5 ? a.Boosting : b.Boosting,
            Enumerable.Range(0, 4).Select(i => Blend(a.Suspension[i], b.Suspension[i])).ToArray());

        double Blend(double x, double y) => x + (y - x) * alpha;
        double Rotate(double x, double y) => x + Math.IEEERemainder(y - x, Math.Tau) * alpha;
    }

    private static double PositionError(CarSnapshot a, CarSnapshot b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    private static void SameGhostPose(CarSnapshot expected, CarSnapshot actual, string context) =>
        SameCar(expected, actual with
        {
            Yaw = expected.Yaw + Math.IEEERemainder(actual.Yaw - expected.Yaw, Math.Tau),
            Pitch = expected.Pitch + Math.IEEERemainder(actual.Pitch - expected.Pitch, Math.Tau),
            Roll = expected.Roll + Math.IEEERemainder(actual.Roll - expected.Roll, Math.Tau)
        }, context);

    private static GameSimulation NewTrial()
    {
        var game = new GameSimulation();
        game.Command("mode-checkpoint-run");
        game.Command("start");
        Run(game, 3, Idle);
        Require(game.Snapshot.Phase == "racing" && game.Snapshot.Trial is { Elapsed: 0, Success: null },
            "checkpoint countdown did not start at exactly zero active elapsed");
        return game;
    }

    private static void ValidateTrial(GameSnapshot snapshot)
    {
        Validate(snapshot);
        var trial = snapshot.Trial ?? throw new InvalidOperationException("checkpoint mode lost its typed state");
        Require(new[] { trial.TimeRemaining, trial.NextCheckpointProgress, trial.LastBonus, trial.Elapsed }.All(double.IsFinite) &&
                trial.TimeRemaining >= 0 && trial.Elapsed >= 0 && trial.CheckpointsPassed is >= 0 and <= 12 &&
                trial.CheckpointCount == 12 && trial.NextCheckpointProgress is >= 0 and < 1 &&
                snapshot.Laps == 1 && snapshot.Ghost is null && snapshot.Race is null,
            "trial outputs are non-finite/outside normalized ranges");
    }

    private static TrackDefinition ScaledCanonicalTrack(double scale)
    {
        var source = TrackDefinition.Create();
        var points = source.Points.Select(p => p with
        {
            S = p.S * scale, X = p.X * scale, Y = p.Y * scale, Z = p.Z * scale, Width = p.Width * scale
        }).ToArray();
        var edges = source.Edges.Select(e => new TrackSurfaceEdge(
            new TrackSurfaceVertex(e.Left.X * scale, e.Left.Y * scale, e.Left.Z * scale),
            new TrackSurfaceVertex(e.Right.X * scale, e.Right.Y * scale, e.Right.Z * scale))).ToArray();
        var package = new
        {
            schemaVersion = 1,
            track = new { name = "Synthetic scaled canonical road", length = source.Length * scale, width = source.Width * scale, points },
            geometry = new
            {
                kind = "exact-source-edge-strip", roadLookup = "geometric-triangles-not-original-integer-sampler",
                startSegmentIndex = 0, edges,
                segments = Enumerable.Range(0, points.Length).Select(i => new
                {
                    index = i, startEdgeIndex = i, endEdgeIndex = (i + 1) % points.Length
                })
            },
            compatibility = new
            {
                profileId = "amiga-experimental", qualification = "unqualified-candidate",
                dynamics = "not-implemented", simulationStepSeconds = (double?)null
            },
            source = new { kind = "synthetic" }, provenance = new { kind = "synthetic-not-original" }
        };
        return PrivateTrackGeometryReader.Read(JsonSerializer.Serialize(package,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
