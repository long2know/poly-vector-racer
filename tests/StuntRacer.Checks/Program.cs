using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StuntRacer.Core;

internal static partial class Program
{
    private const double Step = 1.0 / 120;
    private static readonly DriverInput Idle = new(0, 0, 0, false);

    public static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("canonical road / bridge schema", TrackAndSchema),
            ("private geometry import / qualification boundary", PrivateGeometry),
            ("invalid input / bounded long frames", InputsAndBudget),
            ("render-schedule independence", ScheduleIndependence),
            ("throttle / brake / reverse / boost", DriveAndResources),
            ("Modern circuit launch and driven turn radius", ModernCircuitHandling),
            ("pause / countdown / restart", PauseAndRestart),
            ("real fall / impacts / recovery penalty", FallAndRecovery),
            ("natural jumps / suspension / ordered three-lap finish", DriveThreeLaps),
            ("whole reverse circuit cannot award a lap", ReverseCircuit),
            ("default Modern numerical baseline", DefaultGolden),
            ("race mode / equal-s grid / pause / reset", RaceModes),
            ("race render-schedule independence", RaceSchedules),
            ("physical rival completes three laps / rival win", RivalWins),
            ("public-input player win / per-mode best laps", PlayerWins),
            ("equal-response physical car contact", RaceContact),
            ("physical rival fall / shared recovery rules", RivalRecovery),
            ("independent reverse / recovery checkpoint state", IndependentRaceProgress),
            ("checkpoint clock / expiry / pause / recovery", CheckpointClock),
            ("ordered checkpoint bonuses / successful circuit / anti-farm", CheckpointCircuit),
            ("canonical length scaling / chronological deadlines", CheckpointScalingAndDeadlines),
            ("recorded best-lap ghost / interpolation / restart", GhostPlayback),
            ("ghost visibility / clean laps / session isolation", GhostCleanAndIsolation),
            ("bounded ghost capacity / partial-lap rejection", GhostCapacity),
            ("checkpoint and ghost display-schedule independence", TrialGhostSchedules),
            ("included catalog geometry / spawn / autonomous laps", IncludedCatalogLaps),
            ("spatial stunt geometry / separate contact contract", SpatialStuntGeometry),
            ("distinct Hard Drivin proving ground / real timed finish", HardDrivingProvingGround),
            ("garage navigation / ready boundary / recorded-best preservation", GarageNavigation)
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

    private static void TrackAndSchema()
    {
        var game = new GameSimulation();
        var track = game.Track;
        Require(track.Length is > 800 and < 1000 && track.Width == 12, "track dimensions outside authored contract");
        Require(track.Points.Length >= 512, "insufficient canonical road samples");
        var seam = Distance(track.Points[0], track.Points[^1]);
        Require(seam is > 0 and < 2, $"closed seam distance {seam}");
        Require(track.Points.Min(p => p.Y) < 17 && track.Points.Max(p => p.Y) > 30, "missing elevated dip/crest");
        Require(track.Points.Min(p => p.Bank) < -0.23, "missing meaningful banking");
        Require(track.AtDistance(60).Heading == 0 && track.AtDistance(60).Y == 22,
            "start does not have 60m of straight level road");
        double last = -1;
        double maximumError = 0;
        foreach (var point in track.Points)
        {
            Require(point.S > last && point.Width == track.Width && point.Road, "unordered/missing canonical road sample");
            Require(new[] { point.S, point.X, point.Y, point.Z, point.Heading, point.Bank, point.Width }
                .All(double.IsFinite), "non-finite canonical point");
            last = point.S;
        }

        for (var i = 0; i < track.Points.Length; i += 17)
        {
            var a = track.Points[i];
            var b = track.Points[(i + 1) % track.Points.Length];
            var left = Edge(a, -1);
            var right = Edge(a, 1);
            var nextLeft = Edge(b, -1);
            var nextRight = Edge(b, 1);
            Probe(left, right, nextLeft);
            Probe(right, nextRight, nextLeft);
        }
        var startRoad = track.AtDistance(50);
        Require(!track.Sample(startRoad.X - 9, startRoad.Z).Road, "road has an invisible extended collision shoulder");
        Near(track.AtDistance(-1).S, track.Length - 1, 1e-9, "negative distance wrap");
        Near(game.Snapshot.Player.Y - track.Points[0].Y, 1, 1e-12, "resting COM height");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var trackJson = JsonDocument.Parse(JsonSerializer.Serialize(track, options));
        Fields(trackJson.RootElement, "name", "length", "width", "points", "edges");
        Fields(trackJson.RootElement.GetProperty("points")[0], "s", "x", "y", "z", "heading", "bank", "width", "road");
        var edgeJson = trackJson.RootElement.GetProperty("edges");
        Require(edgeJson.GetArrayLength() == track.Points.Length, "render/collision edge rows are missing");
        Fields(edgeJson[0], "left", "right");
        Fields(edgeJson[0].GetProperty("left"), "x", "y", "z");
        Fields(edgeJson[0].GetProperty("right"), "x", "y", "z");
        game.Command("start");
        game.Advance(0.1, new DriverInput(0.1, 1, 0, true));
        using var snapshotJson = JsonDocument.Parse(JsonSerializer.Serialize(game.Snapshot, options));
        Fields(snapshotJson.RootElement, "player", "paceCar", "phase", "time", "lap", "laps", "lapTime",
            "bestLap", "progress", "countdown", "impact", "recoveries", "alpha", "tick", "message", "race", "trial", "ghost");
        Fields(snapshotJson.RootElement.GetProperty("player"), "x", "y", "z", "yaw", "pitch", "roll", "speed",
            "steer", "boost", "damage", "grounded", "boosting", "suspension");
        Require(snapshotJson.RootElement.GetProperty("paceCar").ValueKind == JsonValueKind.Null, "unexpected rival schema");
        Console.WriteLine($"  road {track.Length:F3}m / {track.Points.Length} samples; triangle height error {maximumError:E2}m");

        void Probe(Position a, Position b, Position c)
        {
            var x = (a.X + b.X + c.X) / 3;
            var z = (a.Z + b.Z + c.Z) / 3;
            var y = (a.Y + b.Y + c.Y) / 3;
            var road = track.Sample(x, z);
            Require(road.Road, $"rendered triangle has no physical road at {iLabel(x, z)}");
            var error = Math.Abs(y - road.Y);
            maximumError = Math.Max(maximumError, error);
            Near(road.Y, y, 1e-8, "render/collision triangle mismatch");
        }

        static string iLabel(double x, double z) => $"{x:F2},{z:F2}";
    }

    private static void PrivateGeometry()
    {
        var source = TrackDefinition.Create();
        const int start = 17;
        var package = new
        {
            schemaVersion = 1,
            track = new { name = "Synthetic private geometry", source.Length, source.Width, source.Points },
            geometry = new
            {
                kind = "exact-source-edge-strip",
                roadLookup = "geometric-triangles-not-original-integer-sampler",
                startSegmentIndex = start,
                source.Edges,
                segments = Enumerable.Range(0, source.Points.Length).Select(i => new
                {
                    index = i, startEdgeIndex = i, endEdgeIndex = (i + 1) % source.Points.Length
                })
            },
            compatibility = new
            {
                profileId = "amiga-experimental", qualification = "unqualified-candidate",
                dynamics = "not-implemented", simulationStepSeconds = (double?)null
            },
            source = new { kind = "synthetic" },
            provenance = new { kind = "synthetic-not-original" }
        };
        var json = JsonSerializer.Serialize(package,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var imported = PrivateTrackGeometryReader.Read(json);
        Near(imported.Points[0].S, 0, 0, "source start was not normalized");
        Near(imported.Points[0].X, source.Points[start].X, 1e-12, "wrong source start point");
        var a = imported.Edges[0].Left;
        var b = imported.Edges[0].Right;
        var c = imported.Edges[1].Left;
        var sample = imported.Sample(a.X * 0.2 + b.X * 0.3 + c.X * 0.5,
            a.Z * 0.2 + b.Z * 0.3 + c.Z * 0.5);
        Require(sample.Road, "imported exact triangle has no road");
        Near(sample.Y, a.Y * 0.2 + b.Y * 0.3 + c.Y * 0.5, 1e-8, "imported edge height mismatch");
        _ = new GameSimulation(imported);
        Reject(node => node["compatibility"]!["dynamics"] = "original");
        Reject(node => node["compatibility"]!["simulationStepSeconds"] = 0.14);
        Reject(node => node["geometry"]!["segments"]![0]!["endEdgeIndex"] = 5);
        Reject(node => node["geometry"]!.AsObject().Remove("edges"));
        Console.WriteLine("  private geometry preserves source start/edges; false dynamics/tick claims and gaps reject");

        void Reject(Action<JsonObject> change)
        {
            var node = JsonNode.Parse(json)!.AsObject();
            change(node);
            Throws<InvalidDataException>(() => PrivateTrackGeometryReader.Read(node.ToJsonString()));
        }
    }

    private static void InputsAndBudget()
    {
        var game = new GameSimulation();
        var ready = game.Snapshot;
        game.Advance(100, new DriverInput(1, 1, 0, true));
        Same(ready, game.Snapshot, "ready preview moved");
        Throws<ArgumentOutOfRangeException>(() => game.Advance(-0.1, Idle));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(double.NaN, Idle));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(double.PositiveInfinity, Idle));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(0, new DriverInput(double.NaN, 0, 0, false)));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(0, new DriverInput(0, double.PositiveInfinity, 0, false)));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(0, new DriverInput(0, 0, double.NegativeInfinity, false)));
        Throws<ArgumentException>(() => game.Command("teleport"));
        Throws<ArgumentOutOfRangeException>(() => game.Track.Sample(double.NaN, 0));
        Throws<ArgumentOutOfRangeException>(() => game.Track.AtDistance(double.PositiveInfinity));
        game.Command("start");
        game.Advance(10_000, Idle);
        Require(game.Snapshot.Tick == 30, $"long frame ran {game.Snapshot.Tick} ticks, expected bounded 30");
        Near(game.Snapshot.Countdown, 2.75, 1e-10, "long frame countdown budget");
        game.Advance(0, Idle);
        Require(game.Snapshot.Tick == 30, "stall backlog leaked into a later call");
        var a = NewRace();
        var b = NewRace();
        Run(a, 2, new DriverInput(4, 3, -1, false));
        Run(b, 2, new DriverInput(1, 1, 0, false));
        Same(a.Snapshot, b.Snapshot, "finite axis clamping changed normalized input behavior");
        Console.WriteLine("  rejected all non-finite axes/deltas; 10,000s stall accepted exactly 30 ticks");
    }

    private static void ScheduleIndependence()
    {
        (double Seconds, DriverInput Input)[] trace =
        [
            (3, Idle),
            (4, new DriverInput(0, 0.8, 0, false)),
            (2, new DriverInput(0.13, 1, 0, true)),
            (2, new DriverInput(-0.2, 0, 1, false)),
            (3, new DriverInput(0, 0.4, 0, false))
        ];
        var reference = Trace([Step]);
        foreach (var schedule in new[]
                 {
                     new[] { 1.0 / 30 }, new[] { 1.0 / 60 }, new[] { 1.0 / 144 },
                     new[] { 1.0 / 90, 1.0 / 144, 1.0 / 60, 1.0 / 75 }
                 })
            Same(reference, Trace(schedule), "display cadence changed authoritative simulation");
        Require(reference.Tick == 1680, $"trace tick count {reference.Tick}");
        Console.WriteLine($"  30/60/120/144Hz + jitter: identical {reference.Tick} ticks, time={reference.Time:F6}s");

        GameSnapshot Trace(double[] schedule)
        {
            var simulation = new GameSimulation();
            simulation.Command("start");
            var frame = 0;
            foreach (var segment in trace)
            {
                var elapsed = 0.0;
                while (elapsed < segment.Seconds - 1e-11)
                {
                    var dt = Math.Min(schedule[frame++ % schedule.Length], segment.Seconds - elapsed);
                    Validate(simulation.Advance(dt, segment.Input));
                    elapsed += dt;
                }
            }
            return simulation.Snapshot;
        }
    }

    private static void DriveAndResources()
    {
        var throttle = NewRace();
        var boosted = NewRace();
        Run(throttle, 4, new DriverInput(0, 1, 0, false));
        Run(boosted, 4, new DriverInput(0, 1, 0, true));
        var boostedSpeed = boosted.Snapshot.Player.Speed;
        Require(throttle.Snapshot.Player.Speed > 20, "throttle did not produce useful acceleration");
        Require(boosted.Snapshot.Player.Speed > throttle.Snapshot.Player.Speed + 8, "boost did not add real acceleration");
        Near(boosted.Snapshot.Player.Boost, 0.36, 1e-9, "boost consumption");
        var fuel = boosted.Snapshot.Player.Boost;
        Run(boosted, 1, new DriverInput(0, 0.5, 0, false));
        Require(boosted.Snapshot.Player.Boost > fuel + 0.04, "boost did not regenerate");
        var depleted = NewRace();
        Run(depleted, 7, new DriverInput(0, 1, 0, true));
        Require(depleted.Snapshot.Player.Boost < 1e-10 && !depleted.Snapshot.Player.Boosting,
            "held empty boost regenerated into free acceleration pulses");
        Run(depleted, 0.5, new DriverInput(0, 0, 0, false));
        Require(depleted.Snapshot.Player.Boost > 0.02, "empty boost did not regenerate after release");
        var coasting = NewRace();
        var braking = NewRace();
        Run(coasting, 3, new DriverInput(0, 1, 0, false));
        Run(braking, 3, new DriverInput(0, 1, 0, false));
        Run(coasting, 1, Idle);
        Run(braking, 1, new DriverInput(0, 0, 1, false));
        Require(coasting.Snapshot.Player.Speed > braking.Snapshot.Player.Speed + 12, "brake did not decelerate versus coast");
        var reverse = NewRace();
        Run(reverse, 1.5, new DriverInput(0, 0, 1, false));
        Require(reverse.Snapshot.Player.Speed < -3 && reverse.Snapshot.Player.Z < reverse.Track.Points[0].Z - 2,
            "brake from rest did not engage world-space reverse");
        Console.WriteLine($"  4s throttle/boost={throttle.Snapshot.Player.Speed:F2}/{boostedSpeed:F2}m/s; " +
                          $"coast/brake={coasting.Snapshot.Player.Speed:F2}/{braking.Snapshot.Player.Speed:F2}m/s; fuel=.360");
    }

    private static void ModernCircuitHandling()
    {
        var full = Launch(1);
        var partial = Launch(0.55);
        var right = Corner(0.65);
        var left = Corner(-0.65);
        var gentle = Corner(0.32);
        Console.WriteLine($"  1.5s unboosted full/55%={full.Speed:F3}/{partial.Speed:F3}m/s; " +
                          $"{right.EntrySpeed:F2}m/s entry, right/left/gentle path radii=" +
                          $"{right.Radius:F2}/{left.Radius:F2}/{gentle.Radius:F2}m");

        Require(full.Speed is > 14 and < 17 && partial.Speed is > 7.5 and < 10 && full.Speed > partial.Speed + 5,
            "Modern launch is not noticeably quicker while respecting analog throttle");
        Require(right.Bend > 0 && left.Bend < 0 && gentle.Bend > 0,
            "positive steering must bend the traveled path right, negative left");
        Require(right.Radius is > 18 and < 29 && left.Radius is > 18 and < 29 &&
                Math.Abs(right.Radius - left.Radius) < 0.1 && gentle.Radius > right.Radius * 1.6,
            "mid-speed steering is too wide, twitchy, asymmetrical, or loses analog response");

        var straight = NewRace(ScaledCanonicalTrack(6));
        Run(straight, 12, new DriverInput(0, 1, 0, false));
        var fast = straight.Snapshot.Player;
        Require(fast.Grounded && straight.Snapshot.Recoveries == 0 && fast.Speed is > 40 and < 43,
            $"launch assist changed high-speed unboosted behavior on the long flat: {fast.Speed:F2}m/s");
        Console.WriteLine($"  long flat, still unboosted: {fast.Speed:F2}m/s after 12s");

        static CarSnapshot Launch(double throttle)
        {
            var game = NewRace();
            Run(game, 1.5, new DriverInput(0, throttle, 0, false));
            var car = game.Snapshot.Player;
            Require(car.Grounded && !car.Boosting && car.Boost == 1, "launch probe did not stay on the road without boost");
            return car;
        }

        static (double EntrySpeed, double Radius, double Bend) Corner(double steer)
        {
            var game = NewRace();
            for (var tick = 0; tick < 240 && game.Snapshot.Player.Speed < 13; tick++)
                game.Advance(Step, new DriverInput(0, 1, 0, false));
            var entrySpeed = game.Snapshot.Player.Speed;
            Require(entrySpeed is >= 13 and < 13.15, "turn probe did not reach its matched entry speed");

            var a = Sample();
            var b = Sample();
            var c = Sample();
            var ax = b.X - a.X;
            var az = b.Z - a.Z;
            var bx = c.X - b.X;
            var bz = c.Z - b.Z;
            // Circumradius of three driven positions, independent of body yaw and steering pose.
            var bend = az * bx - ax * bz;
            return (entrySpeed, Math.Sqrt((ax * ax + az * az) * (bx * bx + bz * bz) *
                ((ax + bx) * (ax + bx) + (az + bz) * (az + bz))) / (2 * Math.Abs(bend)), bend);

            CarSnapshot Sample()
            {
                for (var tick = 0; tick < 36; tick++)
                {
                    game.Advance(Step, new DriverInput(steer, 0, 0, false));
                    Require(game.Snapshot.Player.Suspension.All(compression => compression > 0),
                        "turn probe left the road or unloaded a wheel");
                }
                return game.Snapshot.Player;
            }
        }
    }

    private static void PauseAndRestart()
    {
        var game = new GameSimulation();
        game.Command("start");
        Run(game, 1, Idle);
        game.Command("pause");
        var countdown = game.Snapshot;
        game.Advance(100, new DriverInput(1, 1, 0, true));
        Same(countdown, game.Snapshot, "paused countdown advanced");
        game.Command("resume");
        Run(game, 2, Idle);
        Require(game.Snapshot.Phase == "racing" && game.Snapshot.Time == 0, "countdown was not exactly 3 active seconds");
        Run(game, 2, new DriverInput(0, 1, 0, true));
        game.Command("pause");
        var paused = game.Snapshot;
        for (var i = 0; i < 10; i++)
            game.Advance(1000, new DriverInput(-1, 1, 1, true));
        game.Command("recover");
        Same(paused, game.Snapshot, "pause changed ticks/time/fuel/pose or accepted recovery");
        game.Command("resume");
        Run(game, 0.1, new DriverInput(0, 1, 0, true));
        Require(game.Snapshot.Time > paused.Time && game.Snapshot.Player.Boost < paused.Player.Boost,
            "resume did not restore active simulation");
        game.Command("restart");
        Require(game.Snapshot.Phase == "countdown" && game.Snapshot.Tick == 0 && game.Snapshot.Time == 0 &&
                game.Snapshot.Recoveries == 0 && game.Snapshot.Player.Boost == 1, "restart did not reset the run");
        Console.WriteLine($"  paused {paused.Tick} ticks / {paused.Time:F3}s / fuel {paused.Player.Boost:F3} remained exact");
    }

    private static void FallAndRecovery()
    {
        var game = NewRace();
        Run(game, 2.5, new DriverInput(0, 1, 0, true));
        var minimumY = game.Snapshot.Player.Y;
        var maximumDamage = 0.0;
        var maximumImpact = 0.0;
        var wasAirborne = false;
        for (var i = 0; i < 120 * 14 && game.Snapshot.Recoveries == 0; i++)
        {
            var snapshot = game.Advance(Step, new DriverInput(1, 1, 0, false));
            Validate(snapshot);
            minimumY = Math.Min(minimumY, snapshot.Player.Y);
            maximumDamage = Math.Max(maximumDamage, snapshot.Player.Damage);
            maximumImpact = Math.Max(maximumImpact, snapshot.Impact);
            wasAirborne |= !snapshot.Player.Grounded;
        }
        var recovered = game.Snapshot;
        Require(wasAirborne && minimumY < 2, $"vehicle did not really fall to terrain (minY={minimumY:F2})");
        Require(maximumDamage > 0.1 && maximumImpact > 0.4, "terrain impact did not cause damage/intensity");
        Require(recovered.Recoveries == 1, "real fall did not automatically recover");
        Near(recovered.Time, (recovered.Tick - 360) * Step + 5, 1e-8, "automatic recovery penalty");
        var road = game.Track.Sample(recovered.Player.X, recovered.Player.Z);
        Require(road.Road && recovered.Player.Grounded && Math.Abs(recovered.Player.Y - road.Y - 1) < 0.01,
            "recovery did not place a supported car on actual road");
        var boost = recovered.Player.Boost;
        var time = recovered.Time;
        var progress = recovered.Progress;
        game.Command("recover");
        Require(game.Snapshot.Recoveries == 2 && game.Snapshot.Lap == 1, "manual recovery count/lap exploit");
        Near(game.Snapshot.Time, time + 5, 1e-12, "manual recovery penalty");
        Near(game.Snapshot.Player.Boost, boost, 1e-12, "recovery refilled fuel");
        Near(game.Snapshot.Progress, progress, 1e-10, "repeated recovery teleported forward");
        Console.WriteLine($"  real fall minY={minimumY:F2}m; peak damage={maximumDamage:F3}, impact={maximumImpact:F3}; +5s each recovery");
    }

    private static void DriveThreeLaps()
    {
        var game = NewRace();
        var airTicks = 0;
        var naturalLandings = 0;
        var maximumClearance = 0.0;
        var maximumSuspensionSpread = 0.0;
        var minimumRoll = 0.0;
        var maximumLateral = 0.0;
        var previousGrounded = true;
        var previousLap = 1;
        var lapTransitions = 0;
        for (var frame = 0; frame < 60 * 210 && game.Snapshot.Phase != "finished"; frame++)
        {
            var snapshot = game.Advance(1.0 / 60, Drive(game, false));
            Validate(snapshot);
            var road = game.Track.Sample(snapshot.Player.X, snapshot.Player.Z);
            maximumLateral = Math.Max(maximumLateral, Math.Abs(road.Lateral));
            maximumClearance = Math.Max(maximumClearance, snapshot.Player.Y - road.Y);
            minimumRoll = Math.Min(minimumRoll, snapshot.Player.Roll);
            maximumSuspensionSpread = Math.Max(maximumSuspensionSpread,
                snapshot.Player.Suspension.Max() - snapshot.Player.Suspension.Min());
            if (!snapshot.Player.Grounded && road.Road && snapshot.Player.Y > road.Y + 1.2)
                airTicks += 2;
            if (!previousGrounded && snapshot.Player.Grounded && road.Road)
                naturalLandings++;
            previousGrounded = snapshot.Player.Grounded;
            if (snapshot.Lap > previousLap)
            {
                Require(snapshot.Progress < 0.02, "lap awarded somewhere other than the ordered finish line");
                lapTransitions++;
                previousLap = snapshot.Lap;
            }
            if (snapshot.Recoveries > 0)
                throw new InvalidOperationException($"public-input driver fell at t={snapshot.Time:F2}, progress={snapshot.Progress:F3}, " +
                                                    $"max lateral={maximumLateral:F2}, clearance={maximumClearance:F2}");
        }

        var finish = game.Snapshot;
        Require(finish.Phase == "finished" && finish.Lap == 3 && lapTransitions == 2,
            $"three ordered laps did not finish: lap={finish.Lap}, progress={finish.Progress:F3}, time={finish.Time:F2}");
        Require(finish.BestLap > 20 && finish.BestLap < 80, $"implausible best lap {finish.BestLap}");
        Require(airTicks >= 12 && naturalLandings >= 3 && maximumClearance > 1.5,
            $"no natural crest jumps/landings: air ticks={airTicks}, landings={naturalLandings}, clearance={maximumClearance:F2}");
        Require(maximumSuspensionSpread > 0.1 && minimumRoll < -0.08,
            $"suspension/banking not meaningful: spread={maximumSuspensionSpread:F3}, roll={minimumRoll:F3}");
        game.Advance(100, new DriverInput(1, 1, 0, true));
        game.Command("recover");
        Same(finish, game.Snapshot, "finished run continued time/control or accepted recovery");
        game.Command("restart");
        Near(game.Snapshot.BestLap, finish.BestLap, 1e-12, "restart discarded best lap");
        Console.WriteLine($"  three laps={finish.Time:F3}s, best={finish.BestLap:F3}s, recoveries=0; " +
                          $"air={airTicks / 120.0:F3}s, landings={naturalLandings}, clearance={maximumClearance:F2}m, " +
                          $"roll={minimumRoll:F3}rad, suspension spread={maximumSuspensionSpread:F3}");
    }

    private static void ReverseCircuit()
    {
        var game = NewRace();
        var distance = 0.0;
        var lastS = 0.0;
        for (var frame = 0; frame < 60 * 180 && distance < game.Track.Length + 8; frame++)
        {
            var snapshot = game.Advance(1.0 / 60, Drive(game, true));
            Validate(snapshot);
            var s = snapshot.Progress * game.Track.Length;
            distance -= Math.IEEERemainder(s - lastS, game.Track.Length);
            lastS = s;
            Require(snapshot.Lap == 1 && snapshot.BestLap == 0 && snapshot.Phase == "racing",
                "reverse movement awarded a lap");
            if (snapshot.Recoveries != 0)
                throw new InvalidOperationException($"reverse controller left road at {distance:F1}m, t={snapshot.Time:F2}s");
        }
        Require(distance > game.Track.Length, $"did not drive a genuine whole reverse circuit ({distance:F1}m)");
        game.Command("recover");
        game.Command("recover");
        Require(game.Snapshot.Lap == 1 && game.Snapshot.BestLap == 0 && game.Snapshot.Recoveries == 2,
            "recovery crossed an unearned checkpoint");
        Console.WriteLine($"  reversed {distance:F2}m in {game.Snapshot.Time - 10:F2}s; lap remains 1, bestLap=0");
    }

    private static void DefaultGolden()
    {
        var straight = NewRace();
        Run(straight, 4, new DriverInput(0, 1, 0, false));
        var corner = NewRace();
        for (var frame = 0; frame < 60 * 18; frame++)
            corner.Advance(1.0 / 60, Drive(corner, false));
        // Foundry's low-speed power and steering gains intentionally change these Modern traces.
        Check("straight", straight.Snapshot,
            [-59.99997572763949, 23.000000000001197, -54.20788119549281, 0,
             -3.9889480860481806E-11, 3.4161337645711545E-12, 30.215348204947716, 0, 1, 0,
             0.5000000000933477, 0.5000000000823934, 0.49999999991333166, 0.4999999999023774,
             4, 4, 0, 0.08048516030767028, 0]);
        Check("after crest and bend", corner.Snapshot,
            [41.18881745038335, 23.023432435999457, 168.89599301052098, 2.348588780276507,
             -0.0030424301939872217, -0.12959063942536564, 19.56045671901202, 0.5478688616521423,
             1, 0.2528794069844933, 0.47180136073271356, 0.49140987089029775, 0.49406886486931145,
             0.4817685284718276, 18, 18, 0, 0.4452064614301925, 4.209172638195008E-14]);
        Require(straight.Snapshot.Tick == 840 && corner.Snapshot.Tick == 2520,
            "default golden simulation tick count changed");
        Require(straight.GameMode == "time-attack" && straight.Snapshot.Race is null && straight.Snapshot.PaceCar is null,
            "default Time Attack constructed or updated a rival");
        Console.WriteLine("  tuned Foundry straight + crest/bend goldens: 38 authoritative values within 1e-9");

        static void Check(string label, GameSnapshot snapshot, double[] expected)
        {
            var actual = GoldenValues(snapshot);
            for (var i = 0; i < expected.Length; i++)
                Near(actual[i], expected[i], 1e-9, $"default {label} baseline field {i}");
        }
    }

    private static double[] GoldenValues(GameSnapshot snapshot)
    {
        var car = snapshot.Player;
        return [car.X, car.Y, car.Z, car.Yaw, car.Pitch, car.Roll, car.Speed, car.Steer, car.Boost, car.Damage,
            .. car.Suspension, snapshot.Time, snapshot.LapTime, snapshot.BestLap, snapshot.Progress, snapshot.Impact];
    }

    private static void RaceModes()
    {
        var game = new GameSimulation();
        var original = game.Snapshot;
        game.Command("mode-race");
        var grid = game.Snapshot;
        Require(game.GameMode == "race" && grid.Phase == "ready" && grid.Tick == 0 &&
                grid.Race is { Mode: "race", Participants: 2, PlayerWon: null, Result: "ready" },
            "mode-race did not create an unstarted two-car race");
        var rival = grid.PaceCar ?? throw new InvalidOperationException("race grid has no physical rival");
        var a = game.Track.Sample(grid.Player.X, grid.Player.Z);
        var b = game.Track.Sample(rival.X, rival.Z);
        Require(a.Road && b.Road && grid.Player.Grounded && rival.Grounded, "grid is not physically supported");
        Near(Math.IEEERemainder(a.S - b.S, game.Track.Length), 0, 1e-8, "unexplained rival grid headstart");
        Near((grid.Player.X + rival.X) / 2, original.Player.X, 1e-10, "grid center moved from original start");
        Near((grid.Player.Z + rival.Z) / 2, original.Player.Z, 1e-10, "grid longitudinal center moved");
        Near(grid.Player.Y - a.Y, 1, 1e-10, "player grid ride height");
        Near(rival.Y - b.Y, 1, 1e-10, "rival grid ride height");
        Require(Math.Abs(a.Lateral - b.Lateral) > 4, "side-by-side grid bodies overlap");
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(grid,
                   new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))
            Fields(json.RootElement.GetProperty("race"), "mode", "position", "participants", "rivalLap",
                "playerWon", "result", "rivalProgress", "rivalTime", "rivalRecoveries", "elapsed");
        game.Advance(1000, new DriverInput(1, 1, 0, true));
        Same(grid, game.Snapshot, "ready race AI moved");
        game.Command("start");
        Run(game, 0.5, new DriverInput(1, 1, 0, true));
        var countdown = game.Snapshot;
        Throws<InvalidOperationException>(() => game.Command("mode-race"));
        Throws<InvalidOperationException>(() => game.Command("mode-time-attack"));
        Same(countdown, game.Snapshot, "rejected countdown mode command changed the run");
        Near(game.Snapshot.PaceCar!.X, rival.X, 1e-12, "AI drove during countdown");
        game.Command("pause");
        game.Advance(100, Idle);
        game.Command("mode-time-attack");
        Require(game.GameMode == "time-attack" && game.Snapshot.Race is null && game.Snapshot.PaceCar is null &&
                game.Snapshot.Phase == "ready", "paused mode switch did not abandon the run coherently");
        Same(original, game.Snapshot, "switch back to Time Attack changed its original ready state");
        game.Command("mode-race");
        game.Command("start");
        Run(game, 3, Idle);
        var racing = game.Snapshot;
        Throws<InvalidOperationException>(() => game.Command("mode-race"));
        Throws<InvalidOperationException>(() => game.Command("mode-time-attack"));
        Same(racing, game.Snapshot, "rejected active mode command changed the run");
        Run(game, 2, new DriverInput(0, 1, 0, true));
        game.Command("pause");
        var paused = game.Snapshot;
        game.Advance(1000, new DriverInput(-1, 1, 1, true));
        game.Command("recover");
        Same(paused, game.Snapshot, "pause changed either car, timers, AI fuel, or progress");
        game.Command("resume");
        var beforeRecovery = game.Snapshot;
        game.Command("recover");
        var recovered = game.Snapshot;
        Near(recovered.Time, beforeRecovery.Time + 5, 1e-12, "race recovery penalty");
        SameCar(beforeRecovery.PaceCar!, recovered.PaceCar!, "player recover altered rival physics");
        Require(beforeRecovery.Race == recovered.Race && recovered.Recoveries == 1,
            "player recover altered rival progress/timing or lost its own count");
        game.Command("restart");
        var reset = game.Snapshot;
        Require(reset.Phase == "countdown" && reset.Tick == 0 && reset.Time == 0 && reset.Countdown == 3 &&
                reset.Recoveries == 0 && reset.Race is { Elapsed: 0, RivalTime: 0, RivalRecoveries: 0, RivalLap: 1 },
            "race restart did not reset both run states");
        SameCar(grid.Player, reset.Player, "restart player grid");
        SameCar(grid.PaceCar!, reset.PaceCar!, "restart rival grid");

        var timeAttack = new GameSimulation();
        var switched = new GameSimulation();
        switched.Command("mode-race");
        var raceBytes = Allocations(switched);
        switched.Command("mode-time-attack");
        var defaultBytes = Allocations(timeAttack);
        Near(Allocations(switched), defaultBytes, 0, "returning to Time Attack retained rival snapshot allocations");
        Require(raceBytes > defaultBytes, "race snapshots did not isolate their extra allocations");
        Console.WriteLine($"  equal-s grid lateral={a.Lateral:F2}/{b.Lateral:F2}m; mode rejection/pause/reset exact; " +
                          $"ready snapshot allocation TA/race={defaultBytes / 100}/{raceBytes / 100} bytes");

        static long Allocations(GameSimulation simulation)
        {
            for (var i = 0; i < 100; i++)
                simulation.Advance(0, Idle);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
                simulation.Advance(0, Idle);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    private static void RaceSchedules()
    {
        var reference = Trace(120);
        foreach (var hz in new[] { 30, 60, 144 })
            Same(reference, Trace(hz), $"race display cadence {hz}Hz changed either authoritative car");
        Require(reference.Tick == 1680 && reference.Race is { Elapsed: 11 },
            "race trace lost fixed ticks or mixed recovery penalty with shared elapsed time");
        Console.WriteLine($"  30/60/120/144Hz: identical player+rival at {reference.Tick} ticks; " +
                          $"rival progress={reference.Race!.RivalProgress:F3}, elapsed={reference.Race.Elapsed:F3}s");

        static GameSnapshot Trace(int hz)
        {
            var game = new GameSimulation();
            game.Command("mode-race");
            game.Command("start");
            (double Seconds, DriverInput Input)[] trace =
            [
                (3, Idle), (4, new DriverInput(0, 0.8, 0, false)),
                (2, new DriverInput(0.13, 1, 0, true)), (2, new DriverInput(-0.2, 0, 1, false)),
                (3, new DriverInput(0, 0.4, 0, false))
            ];
            foreach (var segment in trace)
            {
                var elapsed = 0.0;
                while (elapsed < segment.Seconds - 1e-11)
                {
                    var dt = Math.Min(1.0 / hz, segment.Seconds - elapsed);
                    Validate(game.Advance(dt, segment.Input));
                    elapsed += dt;
                }
            }
            return game.Snapshot;
        }
    }

    private static void RivalWins()
    {
        var game = HeadToHead();
        var airSeconds = 0.0;
        var landings = 0;
        var lastGrounded = true;
        var lastLap = 1;
        var lapTransitions = 0;
        var maximumSpread = 0.0;
        var maximumClearance = 0.0;
        var minimumFuel = 1.0;
        for (var frame = 0; frame < 60 * 190 && game.Snapshot.Phase != "finished"; frame++)
        {
            var snapshot = game.Advance(1.0 / 60, Idle);
            Validate(snapshot);
            var car = snapshot.PaceCar!;
            var road = game.Track.Sample(car.X, car.Z);
            maximumSpread = Math.Max(maximumSpread, car.Suspension.Max() - car.Suspension.Min());
            maximumClearance = Math.Max(maximumClearance, car.Y - road.Y);
            minimumFuel = Math.Min(minimumFuel, car.Boost);
            if (!car.Grounded && road.Road)
                airSeconds += 1.0 / 60;
            if (!lastGrounded && car.Grounded && road.Road)
                landings++;
            lastGrounded = car.Grounded;
            if (snapshot.Race!.RivalLap > lastLap)
            {
                Require(snapshot.Race.RivalProgress < 0.02, "rival skipped an ordered lap checkpoint");
                lapTransitions++;
                lastLap = snapshot.Race.RivalLap;
            }
            Require(snapshot.Race.RivalRecoveries == 0, "unimpeded physical AI cannot complete Foundry without falling");
            Require(snapshot.Lap == 1 && snapshot.BestLap == 0, "rival checkpoints leaked into idle player");
        }
        var finish = game.Snapshot;
        Require(finish.Phase == "finished" &&
                finish.Race is { PlayerWon: false, Result: "rival-won", RivalLap: 3, Position: 2 } &&
                finish.Message.StartsWith("RIVAL WON", StringComparison.Ordinal), "AI finish did not report a rival win");
        Require(lapTransitions == 2 && landings >= 3 && airSeconds > 0.2 &&
                maximumSpread > 0.1 && maximumClearance > 1.5 && minimumFuel < 0.98,
            $"AI did not physically use suspension/air/landings/boost: {lapTransitions} laps, {landings} landings, " +
            $"air={airSeconds:F2}s, spread={maximumSpread:F2}, clearance={maximumClearance:F2}, fuel={minimumFuel:F3}");
        game.Advance(1000, new DriverInput(1, 1, 0, true));
        game.Command("recover");
        Same(finish, game.Snapshot, "rival win did not freeze both cars and authoritative clocks");
        game.Command("mode-time-attack");
        Require(game.Snapshot.Phase == "ready" && game.Snapshot.Race is null && game.Snapshot.BestLap == 0,
            "finished race could not return to fresh Time Attack");
        Console.WriteLine($"  rival won in {finish.Race!.Elapsed:F3}s / 3 laps, recoveries=0; air={airSeconds:F3}s, " +
                          $"landings={landings}, clearance={maximumClearance:F2}m, suspension spread={maximumSpread:F3}, fuel min={minimumFuel:F3}");
    }

    private static void PlayerWins()
    {
        var game = HeadToHead();
        for (var frame = 0; frame < 60 * 190 && game.Snapshot.Phase != "finished"; frame++)
            Validate(game.Advance(1.0 / 60, Drive(game, false)));
        var finish = game.Snapshot;
        Require(finish.Phase == "finished" && finish.Lap == 3 &&
                finish.Race is { PlayerWon: true, Result: "player-won", Position: 1 } &&
                finish.Message.StartsWith("RACE WON", StringComparison.Ordinal),
            $"public-input driver did not win: lap={finish.Lap}, phase={finish.Phase}, result={finish.Race!.Result}, " +
            $"elapsed={finish.Race.Elapsed:F2}, recoveries={finish.Recoveries}/{finish.Race.RivalRecoveries}");
        Require(finish.Recoveries == 0 && finish.BestLap > 20, "player win used recovery or invalid lap timing");
        game.Advance(1000, new DriverInput(-1, 1, 1, true));
        Same(finish, game.Snapshot, "player win did not freeze the rival");
        game.Command("restart");
        Require(game.GameMode == "race" && game.Snapshot.Phase == "countdown", "restart unexpectedly changed race mode");
        Near(game.Snapshot.BestLap, finish.BestLap, 1e-10, "race restart lost best lap");
        game.Command("pause");
        game.Command("mode-time-attack");
        Near(game.Snapshot.BestLap, 0, 0, "race best lap contaminated Time Attack best lap");
        game.Command("mode-race");
        Near(game.Snapshot.BestLap, finish.BestLap, 1e-10, "returning to race lost its per-mode best lap");
        Console.WriteLine($"  player won in {finish.Race!.Elapsed:F3}s, best={finish.BestLap:F3}s, " +
                          $"rival lap={finish.Race.RivalLap}, recoveries={finish.Recoveries}/{finish.Race.RivalRecoveries}");
    }

    private static void RaceContact()
    {
        var collision = HeadToHead();
        var clear = HeadToHead();
        var maximumDeflection = 0.0;
        var equalDamageSeen = false;
        var maximumRivalDamage = 0.0;
        for (var tick = 0; tick < 120 * 4; tick++)
        {
            var hit = collision.Advance(Step, new DriverInput(1, 1, 0, false));
            var baseline = clear.Advance(Step, Idle);
            Validate(hit);
            var rival = hit.PaceCar!;
            var control = baseline.PaceCar!;
            var deflection = Math.Sqrt(Math.Pow(rival.X - control.X, 2) + Math.Pow(rival.Z - control.Z, 2));
            maximumDeflection = Math.Max(maximumDeflection, deflection);
            maximumRivalDamage = Math.Max(maximumRivalDamage, rival.Damage);
            if (tick < 60)
                SameCar(rival, control, "rival reacted to distant player instead of actual contact");
            if (rival.Damage > 0 && !equalDamageSeen)
            {
                Near(hit.Player.Damage, rival.Damage, 1e-10, "chassis contact damage response was not equal");
                Require(Math.Abs(hit.Player.Y - rival.Y) < 1.3, "car contact applied across different heights");
                equalDamageSeen = true;
            }
        }
        Require(equalDamageSeen && maximumDeflection > 0.05 && maximumRivalDamage > 0.001,
            $"physical rival was not deflected by player contact: deflection={maximumDeflection:F3}, damage={maximumRivalDamage:F3}");
        Console.WriteLine($"  actual player contact deflected AI {maximumDeflection:F3}m vs clear run; " +
                          $"equal first-contact damage, rival peak damage={maximumRivalDamage:F3}");
    }

    private static void IndependentRaceProgress()
    {
        var game = HeadToHead();
        for (var frame = 0; frame < 60 * 48; frame++)
        {
            var snapshot = game.Advance(1.0 / 60, Drive(game, true));
            Validate(snapshot);
            Require(snapshot.Lap == 1 && snapshot.BestLap == 0, "reverse/recovery awarded player a race lap");
            if (frame is 360 or 720 or 1080)
            {
                var before = game.Snapshot;
                game.Command("recover");
                var after = game.Snapshot;
                SameCar(before.PaceCar!, after.PaceCar!, "player recover repositioned rival");
                Require(before.Race == after.Race, "player recovery advanced or reset rival checkpoint state");
            }
        }
        Require(game.Snapshot.Race is { RivalLap: 2, RivalRecoveries: 0 } && game.Snapshot.Recoveries >= 3,
            "independent rival did not complete its own ordered first lap");
        Console.WriteLine($"  reverse/recovering player remains lap 1; independent rival lap={game.Snapshot.Race!.RivalLap}, " +
                          $"player recoveries={game.Snapshot.Recoveries}");
    }

    private static void RivalRecovery()
    {
        var source = TrackDefinition.Create();
        var points = source.Points.Select(p => p with { Width = 5 }).ToArray();
        var edges = points.Select(p =>
        {
            var left = Edge(p, -1);
            var right = Edge(p, 1);
            return new TrackSurfaceEdge(new TrackSurfaceVertex(left.X, left.Y, left.Z),
                new TrackSurfaceVertex(right.X, right.Y, right.Z));
        }).ToArray();
        var package = new
        {
            schemaVersion = 1,
            track = new { name = "Synthetic narrow contact/recovery scenario", source.Length, width = 5, points },
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
        var track = PrivateTrackGeometryReader.Read(JsonSerializer.Serialize(package,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var game = new GameSimulation(track);
        game.Command("mode-race");
        game.Command("start");
        Run(game, 3, Idle);
        var minimumY = double.MaxValue;
        var maximumBelowRoad = 0.0;
        var airborne = false;
        var previous = game.Snapshot;
        for (var tick = 0; tick < 120 * 20 && game.Snapshot.Race!.RivalRecoveries == 0; tick++)
        {
            previous = game.Snapshot;
            var input = tick < 120 * 3 ? new DriverInput(1, 1, 0, false) : Idle;
            Validate(game.Advance(Step, input));
            var rival = game.Snapshot.PaceCar!;
            minimumY = Math.Min(minimumY, rival.Y);
            maximumBelowRoad = Math.Max(maximumBelowRoad, track.Sample(rival.X, rival.Z).Y - rival.Y);
            airborne |= !rival.Grounded;
        }
        var recovered = game.Snapshot;
        // A rolled car can trigger the existing instability recovery before hitting terrain.
        Require(airborne && maximumBelowRoad > 5 && recovered.Race!.RivalRecoveries == 1,
            $"rival did not physically fall/recover on narrow contact scenario: minY={minimumY:F2}, " +
            $"recoveries={recovered.Race!.RivalRecoveries}");
        Near(recovered.Race.RivalTime - recovered.Race.Elapsed, 5, 1e-9, "rival recovery timing penalty");
        Require(recovered.Race.RivalLap == previous.Race!.RivalLap, "AI recovery awarded a lap");
        Require(Math.Abs(recovered.PaceCar!.Boost - previous.PaceCar!.Boost) < 0.002,
            "AI recovery refilled boost instead of sharing player rules");
        var road = track.Sample(recovered.PaceCar.X, recovered.PaceCar.Z);
        Require(road.Road && recovered.PaceCar.Grounded, "AI recovery did not return to supported canonical road");
        Console.WriteLine($"  ordinary contact on synthetic 5m road: AI fell to {minimumY:F2}m, " +
                          $"recovered once at {recovered.Race.Elapsed:F3}s, +5s, no fuel refill/lap award");
    }

    private static GameSimulation HeadToHead()
    {
        var game = new GameSimulation();
        game.Command("mode-race");
        game.Command("start");
        Run(game, 3, Idle);
        Require(game.Snapshot.Phase == "racing" && game.Snapshot.Race is { Elapsed: 0, RivalTime: 0 },
            "head-to-head countdown did not start both cars together");
        return game;
    }

    private static DriverInput Drive(GameSimulation game, bool reverse)
    {
        var car = game.Snapshot.Player;
        var track = game.Track;
        var road = track.Sample(car.X, car.Z);
        var speed = Math.Abs(car.Speed);
        var look = reverse ? 7 + speed * 0.35 : 10 + speed * 0.4;
        var target = track.AtDistance(road.S + (reverse ? -look : look));
        var bearing = Math.Atan2(target.X - car.X, target.Z - car.Z) + (reverse ? Math.PI : 0);
        var error = Angle(bearing - car.Yaw);
        var rate = 2 * Math.Max(speed, reverse ? 4 : 8) * Math.Sin(error) / look;
        var scale = Math.Clamp(car.Speed / 12, -1, 1) * (1.35 / (1 + speed / 13));
        var steer = Math.Abs(scale) < 0.03 ? 0 : Math.Clamp(rate / scale, -1, 1);
        if (reverse)
            return new DriverInput(steer, 0, 1, false);
        var ahead = track.AtDistance(road.S + 38);
        var bend = Math.Abs(Angle(ahead.Heading - road.Heading));
        var desiredSpeed = bend > 0.06 ? 20 : 29;
        return new DriverInput(steer, Math.Clamp((desiredSpeed - car.Speed) * 0.65, 0, 1),
            Math.Clamp((car.Speed - desiredSpeed - 1) * 0.35, 0, 1), false);
    }

    private static GameSimulation NewRace(TrackDefinition? track = null)
    {
        var game = new GameSimulation(track);
        game.Command("start");
        Run(game, 3, Idle);
        Require(game.Snapshot.Phase == "racing" && game.Snapshot.Time == 0 && game.Snapshot.Tick == 360,
            "race did not start after exactly 360 countdown ticks");
        return game;
    }

    private static void Run(GameSimulation game, double seconds, DriverInput input)
    {
        var ticks = (int)Math.Round(seconds / Step);
        Near(ticks * Step, seconds, 1e-8, "test trace must align with fixed ticks");
        for (var i = 0; i < ticks; i++)
            Validate(game.Advance(Step, input));
    }

    private static void Validate(GameSnapshot snapshot)
    {
        var car = snapshot.Player;
        Require(new[]
        {
            car.X, car.Y, car.Z, car.Yaw, car.Pitch, car.Roll, car.Speed, car.Steer, car.Boost, car.Damage,
            snapshot.Time, snapshot.LapTime, snapshot.BestLap, snapshot.Progress, snapshot.Countdown,
            snapshot.Impact, snapshot.Alpha
        }.All(double.IsFinite), "non-finite simulation output");
        Require(car.Boost is >= 0 and <= 1 && car.Damage is >= 0 and <= 1 && car.Steer is >= -1 and <= 1,
            "car resource/control outside normalized ranges");
        Require(car.Suspension.Length == 4 && car.Suspension.All(x => double.IsFinite(x) && x is >= 0 and <= 1),
            "suspension must be FL/FR/RL/RR normalized compression");
        Require(snapshot.Progress is >= 0 and < 1 && snapshot.Alpha is >= 0 and <= 1 &&
                snapshot.Impact is >= 0 and <= 1 && snapshot.Time >= 0 && snapshot.LapTime >= 0 &&
                snapshot.Lap is >= 1 and <= 3 && snapshot.Countdown is >= 0 and <= 3,
            "race/display metadata outside ranges");
        if (snapshot.Race is { } race)
        {
            var rival = snapshot.PaceCar ?? throw new InvalidOperationException("race has no physical rival snapshot");
            Require(new[] { rival.X, rival.Y, rival.Z, rival.Yaw, rival.Pitch, rival.Roll, rival.Speed, rival.Steer,
                    rival.Boost, rival.Damage, race.RivalTime, race.Elapsed, race.RivalProgress }.All(double.IsFinite),
                "non-finite rival/race state");
            Require(rival.Boost is >= 0 and <= 1 && rival.Damage is >= 0 and <= 1 && rival.Steer is >= -1 and <= 1 &&
                    rival.Suspension.Length == 4 && rival.Suspension.All(x => double.IsFinite(x) && x is >= 0 and <= 1),
                "physical rival controls/resources/suspension outside normalized ranges");
            Require(race.Mode == "race" && race.Participants == 2 && race.Position is 1 or 2 &&
                    race.RivalLap is >= 1 and <= 3 && race.RivalProgress is >= 0 and < 1 &&
                    race.RivalRecoveries >= 0 && race.RivalTime >= race.Elapsed && race.Elapsed >= 0,
                "invalid typed head-to-head race state");
        }
        else
            Require(snapshot.PaceCar is null, "Time Attack unexpectedly updated a rival");
    }

    private static void Same(GameSnapshot a, GameSnapshot b, string context)
    {
        Require(a.Phase == b.Phase && a.Tick == b.Tick && a.Lap == b.Lap && a.Recoveries == b.Recoveries &&
                a.Player.Grounded == b.Player.Grounded && a.Player.Boosting == b.Player.Boosting,
            $"{context}: discrete state mismatch ({a.Tick}/{b.Tick} ticks)");
        double[] left = [a.Player.X, a.Player.Y, a.Player.Z, a.Player.Yaw, a.Player.Pitch, a.Player.Roll,
            a.Player.Speed, a.Player.Steer, a.Player.Boost, a.Player.Damage, a.Time, a.LapTime, a.BestLap,
            a.Progress, a.Countdown, a.Impact];
        double[] right = [b.Player.X, b.Player.Y, b.Player.Z, b.Player.Yaw, b.Player.Pitch, b.Player.Roll,
            b.Player.Speed, b.Player.Steer, b.Player.Boost, b.Player.Damage, b.Time, b.LapTime, b.BestLap,
            b.Progress, b.Countdown, b.Impact];
        for (var i = 0; i < left.Length; i++)
            Near(left[i], right[i], 1e-9, $"{context}: field {i}");
        for (var i = 0; i < 4; i++)
            Near(a.Player.Suspension[i], b.Player.Suspension[i], 1e-9, $"{context}: suspension {i}");
        Require((a.Race is null) == (b.Race is null) && (a.PaceCar is null) == (b.PaceCar is null),
            $"{context}: mode/rival presence changed");
        if (a.Race is { } ar && b.Race is { } br)
        {
            Require(ar.Mode == br.Mode && ar.Position == br.Position && ar.Participants == br.Participants &&
                    ar.RivalLap == br.RivalLap && ar.PlayerWon == br.PlayerWon && ar.Result == br.Result &&
                    ar.RivalRecoveries == br.RivalRecoveries, $"{context}: typed race discrete state");
            Near(ar.Elapsed, br.Elapsed, 1e-9, $"{context}: shared elapsed time");
            Near(ar.RivalTime, br.RivalTime, 1e-9, $"{context}: rival penalty-inclusive time");
            Near(ar.RivalProgress, br.RivalProgress, 1e-9, $"{context}: rival ordered progress");
            SameCar(a.PaceCar!, b.PaceCar!, $"{context}: rival physics");
        }
    }

    private static void SameCar(CarSnapshot a, CarSnapshot b, string context)
    {
        Require(a.Grounded == b.Grounded && a.Boosting == b.Boosting, $"{context}: contact/boost state");
        double[] left = [a.X, a.Y, a.Z, a.Yaw, a.Pitch, a.Roll, a.Speed, a.Steer, a.Boost, a.Damage, .. a.Suspension];
        double[] right = [b.X, b.Y, b.Z, b.Yaw, b.Pitch, b.Roll, b.Speed, b.Steer, b.Boost, b.Damage, .. b.Suspension];
        for (var i = 0; i < left.Length; i++)
            Near(left[i], right[i], 1e-9, $"{context}: car field {i}");
    }

    private static void Fields(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Require(actual.SequenceEqual(expected.Order()), $"bridge schema mismatch: {string.Join(",", actual)}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static void Near(double actual, double expected, double tolerance, string context) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{context}: actual={actual:R}, expected={expected:R}");
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static Position Edge(TrackPoint p, int side)
    {
        var lateral = side * p.Width / 2;
        return new Position(p.X + Math.Cos(p.Heading) * lateral,
            p.Y + Math.Sin(p.Bank) * lateral, p.Z - Math.Sin(p.Heading) * lateral);
    }
    private static double Distance(TrackPoint a, TrackPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
    private static double Angle(double angle) => Math.IEEERemainder(angle, Math.Tau);
    private readonly record struct Position(double X, double Y, double Z);
}
