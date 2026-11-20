using System.Diagnostics;
using System.Text.Json;
using StuntRacer.Core;
using StuntRacer.Core.Stunts;

internal static class Program
{
    private const double Step = 1.0 / 120;
    private static readonly DriverInput Idle = new(0, 0, 0, false);

    public static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("authored spatial loop / gap / canonical edges / serializable challenge", Geometry),
            ("real-input full loop / continuous rotation / no recovery", FastLoop),
            ("low-speed loop loses unilateral contact", SlowLoop),
            ("steering off the road causes a real fall", EdgeFall),
            ("banked bend cannot steer the car automatically", NoAutoDriver),
            ("public-input full stunt circuit / ballistic gap / clean recorded ghost", CircuitAndGhost),
            ("short ramp attempt falls through the real gap / no reward", ShortGap),
            ("airborne pause / recovery cancels pending landing / no farm", AirPauseAndRecovery),
            ("reverse start-line crossing cannot skip ordered stunt gates", ReverseGates),
            ("brake / boost / finite controls / bounded delta", Controls),
            ("pause / countdown / restart / interrupted stunt recovery", Lifecycle),
            ("60 / 120 / 144 Hz authoritative independence", Schedules),
            ("distinct Hard spatial loop / banked flyover / timed public-input circuit", HardCircuit),
            ("Hard timeout / countdown and driving pause / frozen finish", HardTimeout),
            ("Hard chronological checkpoint bonus / late-gate rejection / recovery penalty", HardDeadlines),
            ("Hard 60 / 120 / 144 Hz timed physics independence", HardSchedules),
            ("garage ready reset / recorded-best preservation / both spatial modes", Garage)
        ];
        var failures = 0;
        var watch = Stopwatch.StartNew();
        foreach (var check in checks)
        {
            try
            {
                check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {check.Name}: {exception.Message}");
            }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed in {watch.Elapsed.TotalSeconds:F2}s.");
        return failures == 0 ? 0 : 1;
    }

    private static void Geometry()
    {
        var game = new StuntParkSession();
        var track = game.Track;
        Require(track.IsSpatial && track.Points.Length > 1_500, "not a genuinely spatial canonical course");
        Require(track.Name.Contains("Stunts") && track.Length is > 1_100 and < 1_500, "wrong authored park");
        Require(track.Points.Max(p => p.Y) >= 48 && track.Points.Min(p => p.Width) < 6,
            "missing full-height loop or narrow bridge");
        Require(track.Points.Min(p => p.Bank) < -0.3 && track.Points.Any(p => !p.Road),
            "missing banking or an actual open road gap");
        var descending = 0;
        for (var i = 0; i < track.Points.Length; i++)
        {
            var p = track.Points[i];
            var edge = track.Edges[i];
            Near((edge.Left.X + edge.Right.X) / 2, p.X, 1e-10, "edge/center X");
            Near((edge.Left.Y + edge.Right.Y) / 2, p.Y, 1e-10, "edge/center Y");
            Near((edge.Left.Z + edge.Right.Z) / 2, p.Z, 1e-10, "edge/center Z");
            if (i > 0 && p.Y > 30 && p.Z < track.Points[i - 1].Z)
                descending++;
        }
        Require(descending > 100, "loop never turns back through its inverted upper half");
        Throws<NotSupportedException>(() => track.Sample(-70, 20));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(game.Challenge,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        foreach (var name in new[] { "mode", "score", "loopsCompleted", "jumps", "cleanLandings", "airTime",
            "feature", "nextGate", "success", "cleanRun", "normalLoad" })
            Require(json.RootElement.TryGetProperty(name, out _), $"challenge field {name} missing");
        Console.WriteLine($"  {track.Length:F2} m / {track.Points.Length} XYZ edge rows; loop 36 m high, bridge 5.4 m");
    }

    private static void FastLoop()
    {
        var game = Started();
        var previous = game.Snapshot.Player;
        var highest = previous.Y;
        var inverted = false;
        var lowestLoadAtCrown = double.PositiveInfinity;
        for (var i = 0; i < 3_600 && game.Challenge.LoopsCompleted == 0; i++)
        {
            game.Advance(Step, new(0, 1, 0, false));
            var car = game.Snapshot.Player;
            Require(Distance(car, previous) < 0.8, "loop position jumped rather than integrating motion");
            Require(Math.Abs(car.Pitch - previous.Pitch) < 0.08, "loop pose discontinuity / clamped pitch");
            Require(car.Grounded && game.Snapshot.Recoveries == 0, "fast loop detached or recovered");
            var up = Up(car);
            var canonical = game.Track.AtDistance(game.Snapshot.Progress * game.Track.Length);
            Near(car.X - up.X, canonical.X, 1e-8, "loop contact/render X mismatch");
            Near(car.Y - up.Y, canonical.Y, 1e-8, "loop contact/render Y mismatch");
            Near(car.Z - up.Z, canonical.Z, 1e-8, "loop contact/render Z mismatch");
            highest = Math.Max(highest, car.Y);
            if (Up(car).Y < -0.8)
            {
                inverted = true;
                lowestLoadAtCrown = Math.Min(lowestLoadAtCrown, game.Challenge.NormalLoad);
            }
            previous = car;
        }
        Require(game.Challenge.LoopsCompleted == 1 && game.Challenge.Score == 1_000,
            $"full-speed loop not physically completed: {State(game)}");
        Require(inverted && highest > 46 && previous.Pitch > 6.1 && lowestLoadAtCrown > 0,
            "missing supported inverted pose / continuous complete rotation");
        Require(game.Challenge.Jumps == 0 && game.Challenge.CleanLandings == 0,
            "loop fabricated unrelated jump events");
        Console.WriteLine($"  loop at {game.Snapshot.Time:F2}s, exit {previous.Speed:F2}m/s, crown load ≥{lowestLoadAtCrown:F2}g");
    }

    private static void SlowLoop()
    {
        var game = Started();
        var lostContact = false;
        double detachY = 0;
        for (var i = 0; i < 7_200; i++)
        {
            var car = game.Snapshot.Player;
            var target = 11d;
            game.Advance(Step, new(0, Math.Clamp((target - car.Speed) * 0.6, 0, 1),
                Math.Clamp((car.Speed - target) * 0.5, 0, 1), false));
            car = game.Snapshot.Player;
            if (!car.Grounded && game.Challenge.Feature.Contains("Vertical loop"))
            {
                lostContact = true;
                detachY = car.Y;
                break;
            }
        }
        Require(lostContact && detachY > 26 && game.Challenge.LoopsCompleted == 0,
            $"slow loop did not lose real contact: {State(game)}");
        var startY = game.Snapshot.Player.Y;
        var fell = false;
        for (var i = 0; i < 360 && game.Snapshot.Recoveries == 0; i++)
        {
            game.Advance(Step, Idle);
            fell |= game.Snapshot.Player.Y < startY - 5;
        }
        Require(fell && game.Challenge.Score == 0 && game.Challenge.LoopsCompleted == 0,
            "failed loop levitated / awarded completion");
        Console.WriteLine($"  contact lost at {detachY:F2} m; actual fall, no loop reward");
    }

    private static void EdgeFall()
    {
        var game = Started();
        for (var i = 0; i < 240; i++)
            game.Advance(Step, new(0, 1, 0, false));
        var fell = false;
        var detached = false;
        for (var i = 0; i < 600 && game.Snapshot.Recoveries == 0; i++)
        {
            game.Advance(Step, new(1, 1, 0, false));
            var car = game.Snapshot.Player;
            detached |= !car.Grounded;
            fell |= car.Y < 8;
        }
        Require(detached && fell && game.Challenge.LoopsCompleted == 0 &&
            game.Challenge.CleanLandings == 0 && game.Challenge.Score == 0,
            $"road had invisible shoulders or awarded a fall: {State(game)}");
    }

    private static void CircuitAndGhost()
    {
        var game = Started();
        var driver = new ParkDriver(game.Track);
        var seenFlight = false;
        var seenFallThenRise = false;
        var airborneSamples = 0;
        var landingImpact = 0d;
        CarSnapshot? takeoff = null;
        CarSnapshot? lastAir = null;
        var previousGrounded = true;
        var rose = false;
        var curvatureSamples = 0;
        CarSnapshot? olderAir = null;
        CarSnapshot? recordedAtThreeSeconds = null;
        for (var i = 0; i < 18_000 && game.Snapshot.Phase == "racing"; i++)
        {
            var before = game.Snapshot.Player;
            game.Advance(Step, driver.Input(game));
            var car = game.Snapshot.Player;
            if (game.Challenge.Feature == "Vertical loop")
                Require(Math.Abs(car.Pitch - before.Pitch) < 0.1 && Distance(car, before) < 0.8,
                    "ordinary three-dimensional steering caused a loop pose discontinuity");
            if (i == 359)
                recordedAtThreeSeconds = car;
            if (game.Challenge.Feature == "Gap flight")
            {
                seenFlight = true;
                takeoff ??= before;
                if (lastAir is not null)
                {
                    var verticalVelocity = (car.Y - lastAir.Y) / Step;
                    seenFallThenRise |= verticalVelocity < -1;
                    rose |= verticalVelocity > 1;
                    if (olderAir is not null)
                    {
                        var acceleration = (car.Y - 2 * lastAir.Y + olderAir.Y) / (Step * Step);
                        Require(acceleration is > -10.2 and < -9.4,
                            $"gap flight did not follow actual gravity: {acceleration:F5}m/s²");
                        curvatureSamples++;
                    }
                }
                olderAir = lastAir;
                lastAir = car;
                airborneSamples++;
                Require(!car.Grounded && car.Suspension.All(x => x == 0), "fake airborne pose retained tire support");
            }
            if (!previousGrounded && car.Grounded)
                landingImpact = Math.Max(landingImpact, game.Snapshot.Impact);
            previousGrounded = car.Grounded;
            Require(game.Snapshot.Recoveries == 0, $"full-circuit public driver fell: {State(game)}");
        }
        Require(game.Snapshot.Phase == "finished" && game.Challenge.Success == true,
            $"stunt circuit not completed: {State(game)}");
        Require(seenFlight && airborneSamples > 90 && seenFallThenRise && rose && curvatureSamples > 80 && takeoff is not null &&
            lastAir is not null && Distance(takeoff, lastAir) > 25,
            "gap did not produce extended rising/falling ballistic flight");
        Require(game.Challenge.LoopsCompleted == 1 && game.Challenge.Jumps == 1 &&
            game.Challenge.CleanLandings == 1 && game.Challenge.Score > 1_500 &&
            game.Challenge.GatesPassed == game.Challenge.GateCount && game.Challenge.CleanRun &&
            game.Challenge.RecordedBest && landingImpact == 0,
            $"stunt counters / physical clean landing incorrect: {State(game)}");
        var best = game.Snapshot.Ghost!;
        Require(best.Car is not null && best.Duration > 30 && best.SampleCount > 900 &&
            best.Status == "playing", "actual clean complete circuit was not recorded");
        var bestTime = game.Challenge.BestTime;
        var score = game.Challenge.Score;
        game.Command("restart");
        Countdown(game);
        for (var i = 0; i < 360; i++)
            game.Advance(Step, Idle);
        var ghost = game.Snapshot.Ghost!;
        Require(ghost.Car is not null && ghost.Car.Z > game.Snapshot.Player.Z + 10 &&
            ghost.Duration == best.Duration && game.Challenge.BestTime == bestTime &&
            game.Challenge.Score == 0 && game.Challenge.LoopsCompleted == 0,
            "restart ghost is fabricated from idle current run or record was lost");
        Require(recordedAtThreeSeconds is not null, "missing original physical pose");
        Near(ghost.Car!.X, recordedAtThreeSeconds!.X, 1e-10, "ghost is not the actual recorded X");
        Near(ghost.Car.Y, recordedAtThreeSeconds.Y, 1e-10, "ghost is not the actual recorded Y");
        Near(ghost.Car.Z, recordedAtThreeSeconds.Z, 1e-10, "ghost is not the actual recorded Z");
        Near(ghost.Car.Speed, recordedAtThreeSeconds.Speed, 1e-10, "ghost is not the actual recorded velocity");
        game.Command("ghost-off");
        Require(game.Snapshot.Ghost!.Car is null && game.Snapshot.Ghost.Status == "disabled", "ghost-off ignored");
        game.Command("ghost-on");
        Require(game.Snapshot.Ghost!.Car is not null, "ghost-on lost actual recording");
        Require(new StuntParkSession().Snapshot.Ghost!.Car is null, "best recording leaked across sessions");
        Console.WriteLine($"  clean circuit {bestTime:F3}s / score {score}; gap {airborneSamples / 120d:F2}s; {best.SampleCount} real ghost samples");
    }

    private static void NoAutoDriver()
    {
        var game = Started();
        for (var i = 0; i < 2_400 && game.Challenge.LoopsCompleted == 0; i++)
            game.Advance(Step, new(0, 1, 0, false));
        Require(game.Challenge.LoopsCompleted == 1, "no-auto-driver loop setup failed");
        var detached = false;
        for (var i = 0; i < 2_400 && game.Snapshot.Recoveries == 0; i++)
        {
            var speed = game.Snapshot.Player.Speed;
            game.Advance(Step, new(0, Math.Clamp((22 - speed) * 0.65, 0, 1),
                Math.Clamp((speed - 22) * 0.5, 0, 1), false));
            if (!game.Snapshot.Player.Grounded)
            {
                detached = true;
                break;
            }
        }
        Require(detached && game.Challenge.GatesPassed == 1,
            $"road orientation secretly steered a no-steering car around the bend: {State(game)}");
    }

    private static void ShortGap()
    {
        var game = Started();
        var driver = new ParkDriver(game.Track, 14);
        var actualJump = false;
        var fellBelowLanding = false;
        for (var i = 0; i < 12_000 && game.Snapshot.Recoveries == 0; i++)
        {
            game.Advance(Step, driver.Input(game));
            actualJump |= game.Challenge.Jumps == 1 && !game.Snapshot.Player.Grounded;
            fellBelowLanding |= actualJump && game.Snapshot.Player.Y < 12;
            if (fellBelowLanding && game.Snapshot.Player.Damage > 0)
                break;
        }
        Require(actualJump && fellBelowLanding && game.Challenge.CleanLandings == 0 &&
            game.Challenge.Success is null && game.Snapshot.Player.Damage > 0 && !game.Challenge.CleanRun &&
            game.Challenge.Score <= 1_000 && game.Snapshot.Ghost!.Status == "lap-invalidated",
            $"slow ramp crossed an invisible gap bridge or awarded a crash: {State(game)}");
    }

    private static void AirPauseAndRecovery()
    {
        var game = Started();
        var driver = new ParkDriver(game.Track);
        for (var i = 0; i < 12_000 && game.Challenge.CurrentAirTime < 0.25; i++)
            game.Advance(Step, driver.Input(game));
        Require(game.Challenge.Feature == "Gap flight" && game.Challenge.Jumps == 1 &&
            !game.Snapshot.Player.Grounded, "airborne recovery setup missed actual ramp departure");
        var airTime = game.Challenge.AirTime;
        game.Command("pause");
        var paused = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
        game.Command("recover");
        game.Advance(10, new(1, 1, 1, true));
        Require(paused == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "paused flight advanced gravity/score/ghost");
        game.Command("resume");
        game.Command("recover");
        Require(game.Challenge.CurrentAirTime == 0 && game.Snapshot.Player.Grounded &&
            game.Challenge.CleanLandings == 0 && game.Challenge.LoopsCompleted == 1 &&
            game.Challenge.Score == 750 && game.Snapshot.Recoveries == 1 &&
            game.Challenge.AirTime == airTime && !game.Challenge.CleanRun,
            "recovery auto-completed/counted a pending landing or lost honest airborne time");
        var boost = game.Snapshot.Player.Boost;
        game.Command("recover");
        Require(game.Challenge.Score == 500 && game.Challenge.CleanLandings == 0 &&
            game.Snapshot.Player.Boost == boost, "repeated recoveries farm score or refill boost");
        for (var i = 0; i < 240; i++)
            game.Advance(Step, Idle);
        Require(game.Challenge.CleanLandings == 0 && game.Challenge.Jumps == 1 &&
            game.Challenge.AirTime == airTime, "cancelled jump generated a delayed landing");
    }

    private static void ReverseGates()
    {
        var game = Started();
        for (var i = 0; i < 600; i++)
            game.Advance(Step, new(0, 0, 1, false));
        Require(game.Snapshot.Player.Speed < -4 && game.Snapshot.Progress > 0.9 &&
            game.Challenge.GatesPassed == 0 && game.Challenge.Score == 0 && game.Challenge.LoopsCompleted == 0,
            "reverse crossing of the finish line bypassed the stunt route");
    }

    private static void Controls()
    {
        foreach (var delta in new[] { double.NaN, double.PositiveInfinity, -1 })
            Throws<ArgumentOutOfRangeException>(() => new StuntParkSession().Advance(delta, Idle));
        foreach (var input in new[]
        {
            new DriverInput(double.NaN, 0, 0, false), new DriverInput(0, double.PositiveInfinity, 0, false),
            new DriverInput(0, 0, double.NegativeInfinity, false)
        })
            Throws<ArgumentOutOfRangeException>(() => new StuntParkSession().Advance(0, input));
        var normal = Started();
        var boost = Started();
        for (var i = 0; i < 360; i++)
        {
            normal.Advance(Step, new(0, 1, 0, false));
            boost.Advance(Step, new(0, 1, 0, true));
        }
        Require(boost.Snapshot.Player.Speed > normal.Snapshot.Player.Speed + 10 &&
            boost.Snapshot.Player.Boost < 0.53 && boost.Snapshot.Player.Boosting,
            "boost is not an actual finite-resource acceleration");
        var initial = normal.Snapshot.Player.Speed;
        for (var i = 0; i < 90; i++)
            normal.Advance(Step, new(0, 0, 1, false));
        Require(normal.Snapshot.Player.Speed < initial / 2, "braking does not remove actual velocity");
        var bounded = Started();
        var tick = bounded.Snapshot.Tick;
        bounded.Advance(10_000, new(0, 20, -20, false));
        Require(bounded.Snapshot.Tick - tick == 30, "long frame banked unbounded physics steps");
        var clipped = Started();
        clipped.Advance(0.25, new(0, 1, 0, false));
        Near(bounded.Snapshot.Player.Z, clipped.Snapshot.Player.Z, 1e-12, "axis clamping / stall budget");
    }

    private static void Lifecycle()
    {
        var game = new StuntParkSession();
        game.Advance(100, new(0, 1, 0, true));
        Require(game.Snapshot.Tick == 0 && game.Snapshot.Time == 0, "ready mode advanced physics");
        game.Command("start");
        game.Advance(0.25, Idle);
        game.Command("pause");
        var frozen = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
        game.Advance(100, new(1, 1, 0, true));
        Require(frozen == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "paused countdown changed state");
        game.Command("resume");
        Countdown(game);
        for (var i = 0; i < 1_800 && game.Challenge.LoopsCompleted == 0; i++)
            game.Advance(Step, new(0, 1, 0, false));
        Require(game.Challenge.LoopsCompleted == 1, "lifecycle setup failed actual loop");
        game.Command("pause");
        frozen = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
        game.Command("recover");
        game.Advance(100, new(1, 1, 0, true));
        Require(frozen == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "paused score/pose/events changed");
        game.Command("resume");
        var time = game.Snapshot.Time;
        game.Command("recover");
        Require(game.Snapshot.Recoveries == 1 && game.Snapshot.Player.Speed == 0 &&
            game.Snapshot.Time == time + 5 && game.Challenge.Score == 750 &&
            game.Challenge.LoopsCompleted == 1 && !game.Challenge.CleanRun &&
            game.Snapshot.Ghost!.Status == "lap-invalidated" && game.Challenge.CurrentAirTime == 0,
            "recovery failed penalty / cancellation / actual-event preservation / clean-run invalidation");
        game.Command("restart");
        Require(game.Snapshot.Phase == "countdown" && game.Snapshot.Tick == 0 &&
            game.Snapshot.Recoveries == 0 && game.Challenge.Score == 0 && game.Challenge.LoopsCompleted == 0 &&
            game.Challenge.CleanRun && game.Challenge.GatesPassed == 0,
            "restart did not reset the stunt challenge");
    }

    private static void Schedules()
    {
        StuntParkSession? reference = null;
        foreach (var hz in new[] { 60, 120, 144 })
        {
            var game = new StuntParkSession();
            game.Command("start");
            for (var second = 0; second < 23; second++)
            {
                var input = second < 16 ? new DriverInput(0, 1, 0, second < 2)
                    : second < 19 ? new DriverInput(0.3, 0.4, 0, false)
                    : new DriverInput(-0.2, 0, 0.8, false);
                for (var frame = 0; frame < hz; frame++)
                    game.Advance(1d / hz, input);
            }
            if (reference is null)
                reference = game;
            else
            {
                Require(JsonSerializer.Serialize(reference.Challenge) == JsonSerializer.Serialize(game.Challenge),
                    $"{hz}Hz changed stunt decisions/counters");
                Require(JsonSerializer.Serialize(reference.Snapshot) == JsonSerializer.Serialize(game.Snapshot),
                    $"{hz}Hz changed authoritative physical state or clock");
            }
        }
    }

    private static void HardCircuit()
    {
        var game = StuntParkSession.CreateHardDriving();
        var stunts = new StuntParkSession();
        Require(game.Track.IsSpatial && game.Challenge.Mode == "hard-driving" && game.Snapshot.Trial is not null,
            "Hard factory did not select spatial/timed machinery");
        Require(!HardDrivingCourse.Create().IsSpatial && stunts.Snapshot.Trial is null &&
            stunts.Challenge.Mode == "stunts", "legacy Hard or default Stunts model was replaced");
        Require(game.Track.Name != stunts.Track.Name && game.Track.Length > stunts.Track.Length * 1.7 &&
            game.Track.Points.Max(p => p.X) - game.Track.Points.Min(p => p.X) > 480 &&
            game.Track.Points.Max(p => p.Y) >= 52 && game.Track.Points.Min(p => p.Width) is > 8 and < 9,
            "Hard geometry is just the renamed/scaled Stunts oval");
        game.Command("start");
        Countdown(game);
        var driver = new ParkDriver(game.Track, 32);
        var previous = game.Snapshot;
        var initialBudget = game.Snapshot.Trial!.TimeRemaining;
        var bonuses = 0d;
        var bonusCount = 0;
        var inverted = false;
        var airTicks = 0;
        for (var i = 0; i < 18_000 && game.Snapshot.Phase == "racing"; i++)
        {
            game.Advance(Step, driver.Input(game));
            var snapshot = game.Snapshot;
            var trial = snapshot.Trial!;
            Require(snapshot.Recoveries == 0 && Distance(previous.Player, snapshot.Player) < 1,
                $"Hard public-input driver fell or teleported: {State(game)}");
            if (game.Challenge.Feature == "Proving-ground loop")
            {
                Require(snapshot.Player.Grounded && Math.Abs(snapshot.Player.Pitch - previous.Player.Pitch) < 0.1,
                    "Hard loop did not maintain genuine continuous supported rotation");
                inverted |= Up(snapshot.Player).Y < -0.8;
            }
            if (game.Challenge.Feature == "Gap flight")
            {
                Require(!snapshot.Player.Grounded && snapshot.Player.Suspension.All(x => x == 0),
                    "Hard jump was an animated supported pose");
                airTicks++;
            }
            if (trial.CheckpointsPassed > previous.Trial!.CheckpointsPassed)
            {
                Require(trial.CheckpointsPassed == previous.Trial.CheckpointsPassed + 1,
                    "Hard route skipped ordered checkpoints");
                bonuses += trial.LastBonus;
                if (trial.LastBonus > 0)
                    bonusCount++;
            }
            Near(trial.TimeRemaining, initialBudget + bonuses - snapshot.Time, 1e-9,
                "Hard checkpoint clock/bonus was not authoritative");
            Near(trial.NextCheckpointProgress,
                trial.CheckpointsPassed == trial.CheckpointCount ? 0 : game.Challenge.NextGateProgress,
                1e-12, "timed HUD gate is not the actual next authored spatial gate");
            previous = snapshot;
        }
        Require(game.Snapshot.Phase == "finished" && game.Snapshot.Trial!.Success == true &&
            game.Challenge.Success == true && inverted && airTicks > 100 &&
            game.Challenge.LoopsCompleted == 1 && game.Challenge.Jumps == 1 &&
            game.Challenge.CleanLandings == 1 && game.Challenge.CleanRun &&
            game.Snapshot.Trial.CheckpointsPassed == game.Snapshot.Trial.CheckpointCount &&
            game.Challenge.GatesPassed == game.Challenge.GateCount && bonusCount == game.Challenge.GateCount - 1,
            $"Hard real loop/timed route did not finish: {State(game)}");
        Require(game.Snapshot.Ghost!.Car is not null && game.Snapshot.Ghost.SampleCount > 2_000 &&
            game.Challenge.RecordedBest, "clean actual Hard route did not record its own best ghost");
        Console.WriteLine($"  Hard {game.Track.Length:F2}m / {game.Track.Points.Length} XYZ rows; " +
            $"{game.Snapshot.Time:F3}s, {bonusCount} checkpoint bonuses, " +
            $"{game.Snapshot.Trial.TimeRemaining:F2}s left; actual 44m loop / {airTicks / 120d:F2}s gap flight");
    }

    private static void HardTimeout()
    {
        var game = StuntParkSession.CreateHardDriving();
        var budget = game.Snapshot.Trial!.TimeRemaining;
        game.Command("start");
        game.Advance(0.25, Idle);
        Near(game.Snapshot.Trial!.TimeRemaining, budget, 0, "Hard countdown spent checkpoint time");
        game.Command("pause");
        var frozen = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
        game.Advance(100, new(1, 1, 0, true));
        Require(frozen == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "Hard paused countdown moved");
        game.Command("resume");
        Countdown(game);
        for (var i = 0; i < 360; i++)
            game.Advance(Step, Idle);
        game.Command("pause");
        frozen = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
        game.Advance(100, new(1, 1, 1, true));
        Require(frozen == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "Hard pause spent time or advanced stunts");
        game.Command("resume");
        for (var i = 0; i < 6_000 && game.Snapshot.Phase == "racing"; i++)
            game.Advance(Step, Idle);
        Require(game.Snapshot.Phase == "finished" && game.Snapshot.Trial!.Success == false &&
            game.Challenge.Success == false && game.Snapshot.Trial.TimeRemaining == 0 &&
            game.Snapshot.Time >= budget && game.Snapshot.Time < budget + Step + 1e-9 &&
            game.Snapshot.Trial.CheckpointsPassed == 0 && game.Challenge.Score == 0 &&
            game.Snapshot.Ghost!.Car is null && game.Challenge.BestTime == 0,
            $"Hard idle timeout was false success/late/unrecorded: {State(game)}");
        frozen = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
        game.Advance(100, new(0, 1, 0, true));
        Require(frozen == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "Hard timeout did not freeze the session");
    }

    private static void HardDeadlines()
    {
        var measured = HardStarted();
        var budget = measured.Snapshot.Trial!.TimeRemaining;
        var driveTicks = 0;
        while (measured.Snapshot.Trial!.CheckpointsPassed == 0 && driveTicks < 3_000)
        {
            measured.Advance(Step, new(0, 1, 0, false));
            driveTicks++;
        }
        Require(measured.Snapshot.Trial!.CheckpointsPassed == 1, "failed to measure actual first timed gate");
        var idleTicks = (int)Math.Ceiling(budget / Step - 1e-9) - driveTicks;
        Require(idleTicks > 0, "authored first-sector budget cannot cover the real drive");
        var early = Attempt(idleTicks);
        Require(early.Snapshot.Trial!.CheckpointsPassed == 1 && early.Snapshot.Phase == "racing" &&
            early.Snapshot.Trial.LastBonus > 0 && early.Snapshot.Trial.TimeRemaining > 0,
            "a legitimate within-tick gate bonus lost to end-of-tick expiry");
        var late = Attempt(idleTicks + 1);
        Require(late.Snapshot.Phase == "finished" && late.Snapshot.Trial!.Success == false &&
            late.Snapshot.Trial.CheckpointsPassed == 0 && late.Snapshot.Trial.LastBonus == 0 &&
            late.Challenge.GatesPassed == 0, "one-tick-late checkpoint resurrected the expired clock");
        var recovery = HardStarted();
        var untilRecoveryExpires = (int)Math.Ceiling((budget - 4.9) / Step);
        for (var i = 0; i < untilRecoveryExpires; i++)
            recovery.Advance(Step, Idle);
        recovery.Command("recover");
        Require(recovery.Snapshot.Phase == "finished" && recovery.Snapshot.Trial!.Success == false &&
            recovery.Snapshot.Trial.CheckpointsPassed == 0 && recovery.Snapshot.Recoveries == 1,
            "Hard five-second recovery penalty did not immediately expire the checkpoint clock");

        static StuntParkSession Attempt(int idleTicks)
        {
            var game = HardStarted();
            for (var i = 0; i < idleTicks; i++)
                game.Advance(Step, Idle);
            for (var i = 0; i < 3_000 && game.Snapshot.Phase == "racing" &&
                game.Snapshot.Trial!.CheckpointsPassed == 0; i++)
                game.Advance(Step, new(0, 1, 0, false));
            return game;
        }
    }

    private static void HardSchedules()
    {
        StuntParkSession? reference = null;
        foreach (var hz in new[] { 60, 120, 144 })
        {
            var game = StuntParkSession.CreateHardDriving();
            game.Command("start");
            for (var second = 0; second < 30; second++)
            {
                var input = second < 20 ? new DriverInput(0, 1, 0, false)
                    : second < 26 ? new DriverInput(0.3, 0.7, 0, false)
                    : new DriverInput(-0.2, 0, 0.8, false);
                for (var frame = 0; frame < hz; frame++)
                    game.Advance(1d / hz, input);
            }
            if (reference is null)
                reference = game;
            else
                Require(JsonSerializer.Serialize((reference.Snapshot, reference.Challenge)) ==
                    JsonSerializer.Serialize((game.Snapshot, game.Challenge)),
                    $"{hz}Hz changed Hard timing/gate bonuses/physical state");
        }
    }

    private static void Garage()
    {
        foreach (var hard in new[] { false, true })
        {
            var game = hard ? HardStarted() : Started();
            var driver = new ParkDriver(game.Track, hard ? 32 : 28);
            for (var i = 0; i < 18_000 && game.Snapshot.Phase == "racing"; i++)
                game.Advance(Step, driver.Input(game));
            Require(game.Challenge.Success == true && game.Snapshot.Ghost!.Car is not null,
                $"garage record setup did not drive a genuine complete circuit: {State(game)}");
            var best = game.Snapshot.Ghost!;
            var time = game.Challenge.BestTime;
            var score = game.Challenge.BestScore;
            game.Command("garage");
            CheckReady();
            var ready = JsonSerializer.Serialize((game.Snapshot, game.Challenge));
            game.Advance(100, new(1, 1, 0, true));
            Require(ready == JsonSerializer.Serialize((game.Snapshot, game.Challenge)), "garage ready state advanced");
            game.Command("start");
            Countdown(game);
            for (var i = 0; i < 360; i++)
                game.Advance(Step, new(0, 1, 0, false));
            game.Command("pause");
            game.Command("garage");
            CheckReady();
            game.Command("resume");
            Require(game.Snapshot.Phase == "ready", "resume revived the abandoned pre-garage run");

            void CheckReady()
            {
                Require(game.Snapshot.Phase == "ready" && game.Snapshot.Time == 0 && game.Snapshot.Tick == 0 &&
                    game.Snapshot.Countdown == 0 && game.Snapshot.Player.Speed == 0 &&
                    game.Snapshot.Recoveries == 0 && game.Snapshot.Player.Boost == 1 &&
                    game.Snapshot.Player.Damage == 0 && game.Challenge.Score == 0 &&
                    game.Challenge.LoopsCompleted == 0 && game.Challenge.Jumps == 0 &&
                    game.Challenge.CleanLandings == 0 && game.Challenge.GatesPassed == 0 &&
                    game.Challenge.Success is null && game.Challenge.CleanRun && !game.Challenge.RecordedBest,
                    "garage did not abandon/reset the active run to a clean stationary ready grid");
                Require(game.Challenge.BestTime == time && game.Challenge.BestScore == score &&
                    game.Snapshot.Ghost!.Duration == best.Duration &&
                    game.Snapshot.Ghost.SampleCount == best.SampleCount && game.Snapshot.Ghost.Car is not null,
                    "garage deleted or replaced the actual session-local best recording");
                Require(hard ? game.Snapshot.Trial is { Success: null, CheckpointsPassed: 0, Elapsed: 0 } :
                    game.Snapshot.Trial is null, "garage did not reset/retain the correct timing model");
            }
        }
    }

    private static StuntParkSession HardStarted()
    {
        var game = StuntParkSession.CreateHardDriving();
        game.Command("start");
        Countdown(game);
        return game;
    }

    private static StuntParkSession Started()
    {
        var game = new StuntParkSession();
        game.Command("start");
        Countdown(game);
        return game;
    }

    private static void Countdown(StuntParkSession game)
    {
        for (var i = 0; i < 400 && game.Snapshot.Phase == "countdown"; i++)
            game.Advance(Step, Idle);
        Require(game.Snapshot.Phase == "racing", "countdown failed");
    }

    private static string State(StuntParkSession game) =>
        $"t={game.Snapshot.Time:F2} phase={game.Snapshot.Phase} feature={game.Challenge.Feature} " +
        $"p=({game.Snapshot.Player.X:F2},{game.Snapshot.Player.Y:F2},{game.Snapshot.Player.Z:F2}) " +
        $"speed={game.Snapshot.Player.Speed:F2} pitch={game.Snapshot.Player.Pitch:F2} " +
        $"gate={game.Challenge.GatesPassed} loops={game.Challenge.LoopsCompleted} " +
        $"jumps={game.Challenge.Jumps} landings={game.Challenge.CleanLandings} recovery={game.Snapshot.Recoveries} " +
        $"damage={game.Snapshot.Player.Damage:F3} last={game.Challenge.LastStunt}";

    private static (double X, double Y, double Z) Forward(CarSnapshot car) =>
        (Math.Sin(car.Yaw) * Math.Cos(car.Pitch), Math.Sin(car.Pitch), Math.Cos(car.Yaw) * Math.Cos(car.Pitch));
    private static (double X, double Y, double Z) Up(CarSnapshot car) =>
        (-Math.Cos(car.Yaw) * Math.Sin(car.Roll) - Math.Sin(car.Yaw) * Math.Sin(car.Pitch) * Math.Cos(car.Roll),
            Math.Cos(car.Pitch) * Math.Cos(car.Roll),
            Math.Sin(car.Yaw) * Math.Sin(car.Roll) - Math.Cos(car.Yaw) * Math.Sin(car.Pitch) * Math.Cos(car.Roll));
    private static double Distance(CarSnapshot a, CarSnapshot b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static void Near(double a, double b, double tolerance, string message) =>
        Require(Math.Abs(a - b) <= tolerance, $"{message}: {a:R} != {b:R}");
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    // Test driver supplies only ordinary player controls; the simulation has no steering,
    // target-speed, route-following or test-pose API.
    private sealed class ParkDriver(TrackDefinition track, double jumpSpeed = 28)
    {
        private int _near;
        public DriverInput Input(StuntParkSession game)
        {
            var car = game.Snapshot.Player;
            var up = Up(car);
            var x = car.X - up.X;
            var y = car.Y - up.Y;
            var z = car.Z - up.Z;
            var best = double.PositiveInfinity;
            var nearest = _near;
            for (var offset = -20; offset <= 90; offset++)
            {
                var index = (_near + offset + track.Points.Length) % track.Points.Length;
                var p = track.Points[index];
                var distance = Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2) + Math.Pow(z - p.Z, 2);
                if (distance < best)
                {
                    best = distance;
                    nearest = index;
                }
            }
            _near = nearest;
            var look = 10 + Math.Abs(car.Speed) * 0.45;
            var target = track.AtDistance(track.Points[_near].S + look);
            var dx = target.X - x;
            var dy = target.Y - y;
            var dz = target.Z - z;
            var forward = Forward(car);
            var right = (X: up.Y * forward.Z - up.Z * forward.Y,
                Y: up.Z * forward.X - up.X * forward.Z, Z: up.X * forward.Y - up.Y * forward.X);
            var error = Math.Atan2(dx * right.X + dy * right.Y + dz * right.Z,
                dx * forward.X + dy * forward.Y + dz * forward.Z);
            var rate = 2 * Math.Max(8, car.Speed) * Math.Sin(error) / look;
            var steeringScale = 1.35 / (1 + Math.Abs(car.Speed) / 35) * Math.Clamp(car.Speed / 8, -1, 1);
            var steer = Math.Abs(steeringScale) < 0.05 ? 0 : Math.Clamp(rate / steeringScale, -1, 1);
            var speed = game.Challenge.Feature switch
            {
                "Loop approach" => 38d,
                "Vertical loop" => 42,
                "Loop runout" => 22,
                "Banked turn" or "Return banking" => 21,
                "Bridge climb" or "Precision bridge" => 19,
                "Bridge descent" => 22,
                "Ramp approach" or "Gap ramp" or "Open gap" or "Gap flight" => jumpSpeed,
                "Landing runway" => 22,
                "Speed runway" => 40,
                "Proving-ground loop" => 42,
                "Fast loop exit" => 30,
                "North banking" or "South banking" or "West banking" => 29,
                "East banking" => 27,
                "Flyover climb" or "Precision flyover" or "Flyover descent" => 24,
                "East straight" => 29,
                "Ramp runway" or "Jump ramp" or "Proving gap" => jumpSpeed,
                "Landing bridge" or "Bridge runout" => 28,
                "South straight" => 32,
                "Home sprint" => 36,
                _ => 28
            };
            return new(steer, Math.Clamp((speed - car.Speed) * 0.65, 0, 1),
                Math.Clamp((car.Speed - speed) * 0.5, 0, 1), false);
        }
    }
}
