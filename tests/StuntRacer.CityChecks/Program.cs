using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using StuntRacer.Core;
using StuntRacer.Core.City;

internal static class Program
{
    private const double Step = 1.0 / 120;
    private static readonly DriverInput Idle = default;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("canonical authored city / typed JSON / no disconnected bridge", WorldContract),
            ("ready/countdown/pause freeze all physical and walking actors", ActorLifecycle),
            ("real building and equal-response traffic impacts", Collisions),
            ("city Modern throttle / brake / reverse / boost resources", DriveControls),
            ("actual bridge ramp / off-guide ground / river recovery", SurfacesAndRecovery),
            ("finite inputs / bounded stalls / command validation", InputsAndBudget),
            ("60/120/144 display schedules preserve all city state", Schedules),
            ("ordered directional street gates / blitz win / timeout", BlitzGates),
            ("public-input cross-city blitz through downtown/foundry/bridge", DriveBlitz),
            ("public-input city streets / intersections / off-guide exploration", ExploreStreets),
            ("same-physics rival drives and finishes both street laps", RivalCircuit),
            ("public-input player completes and wins the street circuit", PlayerCircuit)
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
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} city checks passed in {watch.Elapsed.TotalSeconds:F2}s.");
        return failures == 0 ? 0 : 1;
    }

    private static void WorldContract()
    {
        var game = new CityDrivingSession();
        var world = game.World;
        Require(world.Bounds.MaxX - world.Bounds.MinX is > 1_000 and < 2_000 &&
            world.Bounds.MaxZ - world.Bounds.MinZ is > 1_000 and < 2_000, "city extent is not a real 1–2km street network");
        Require(world.Roads.Length >= 15 && world.Buildings.Length >= 60 && world.Buildings.Max(b => b.Height) > 120,
            "city lacks connected streets and a distinct downtown skyline");
        Require(world.Areas.Any(a => a.Kind == "park") && world.Areas.Any(a => a.Kind == "water") &&
            world.Structures.Any(a => a.Style == "bridge-tower"), "park/waterfront/bridge canonical geometry is missing");
        var bridge = world.Roads.Single(r => r.Bridge);
        Require(bridge.Points[0].Y < 0.1 && bridge.Points[^1].Y < 0.1 && bridge.Points.Max(p => p.Y) >= 20,
            "bridge is cosmetic rather than a continuous sloped street");
        Require(bridge.Points[0].X <= 400 && bridge.Points[^1].X >= 780, "bridge does not meet both actual street networks");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { game.World, game.Activity }, Json));
        var root = document.RootElement;
        Require(root.GetProperty("world").GetProperty("roads")[0].GetProperty("points")[0].TryGetProperty("y", out _) &&
            root.GetProperty("world").GetProperty("buildings")[0].TryGetProperty("baseY", out _) &&
            root.GetProperty("activity").GetProperty("traffic")[0].GetProperty("car").TryGetProperty("suspension", out _),
            "typed world/activity JSON cannot directly feed the renderer");
        foreach (var mode in new[] { "city-blitz", "city-circuit" })
        {
            var session = new CityDrivingSession(mode);
            Require(session.Snapshot.Player.Grounded && session.Snapshot.Player.Y is > 0.9 and < 1.2,
                $"{mode} does not spawn over its actual city road");
            Require(session.Activity.Checkpoints.Length > 0, $"{mode} has no spatial street gates");
        }
        Require(game.Snapshot.PaceCar is null && new CityDrivingSession("city-circuit").Snapshot.PaceCar is not null,
            "free roam/circuit physical rival distinction is missing");
        Console.WriteLine($"  {world.Roads.Length} roads, {world.Buildings.Length} footprints, " +
            $"{game.Activity.Traffic.Length} cars/{game.Activity.Pedestrians.Length} walkers; {game.Track.Length:F0}m street circuit");
    }

    private static void ActorLifecycle()
    {
        var game = new CityDrivingSession();
        var ready = State(game);
        game.Advance(100, new DriverInput(1, 1, 0, true));
        Require(State(game) == ready, "ready city moved");
        game.Command("start");
        var actors = Actors(game);
        Run(game, 2.5, new DriverInput(1, 1, 0, true));
        Require(Actors(game) == actors && game.Snapshot.Time == 0, "countdown advanced actors or running time");
        game.Command("pause");
        var paused = State(game);
        game.Advance(100, Idle);
        Require(State(game) == paused, "paused countdown advanced city state");
        game.Command("resume");
        Run(game, 0.5, Idle);
        Require(game.Snapshot.Phase == "racing" && Actors(game) == actors, "start tick moved a frozen countdown actor");
        var before = game.Activity;
        Run(game, 2, Idle);
        var after = game.Activity;
        Require(before.Traffic.Zip(after.Traffic).Count(p => Distance(p.First.Car, p.Second.Car) > 1) >= 14 &&
            before.Pedestrians.Zip(after.Pedestrians).Count(p => Squared(p.First.X - p.Second.X, p.First.Z - p.Second.Z) > 1) >= 20,
            "city actors are not genuinely moving in C#");
        foreach (var walker in after.Pedestrians)
        foreach (var road in game.World.Roads)
        for (var i = 0; i < road.Points.Length - 1; i++)
        {
            var a = road.Points[i];
            var b = road.Points[i + 1];
            var t = ((walker.X - a.X) * (b.X - a.X) + (walker.Z - a.Z) * (b.Z - a.Z)) /
                Squared(b.X - a.X, b.Z - a.Z);
            if (t is > 0 and < 1)
                Require(Math.Sqrt(Squared(walker.X - a.X - (b.X - a.X) * t,
                    walker.Z - a.Z - (b.Z - a.Z) * t)) >= road.Width / 2,
                    $"pedestrian {walker.Id} is walking in {road.Name}");
        }
        game.Command("pause");
        paused = State(game);
        game.Advance(1000, new DriverInput(-1, 1, 1, true));
        Require(State(game) == paused, "paused actors/resources/timers changed");
        game.Command("restart");
        Require(game.Snapshot.Phase == "countdown" && game.Snapshot.Tick == 0 && Actors(game) == actors,
            "restart did not reset the entire city to the same actor order/poses");
    }

    private static void Collisions()
    {
        var game = Racing();
        var building = game.World.Buildings.First(b => b.CenterX > 100 && b.CenterZ < -400);
        PlacePlayer(game, building.CenterX - building.Width / 2 - 7, building.CenterZ, Math.PI / 2, 18);
        var before = game.Snapshot.Player;
        Run(game, 0.6, new DriverInput(0, 0.3, 0, false));
        var after = game.Snapshot;
        Require(after.Player.X > before.X && after.Player.X < building.CenterX - building.Width / 2 - 1 &&
            after.Player.Damage > 0.1 && after.Impact > 0.05 && after.Player.Speed < 10,
            $"canonical building did not stop/damage the real vehicle: x={after.Player.X:F2}, damage={after.Player.Damage:F3}");

        var index = Array.IndexOf(game.World.Buildings, building);
        game.World.Buildings[index] = building with { CenterX = -60, CenterZ = 150 };
        PlacePlayer(game, building.CenterX - building.Width / 2 - 7, building.CenterZ, Math.PI / 2, 18);
        Run(game, 0.6, new DriverInput(0, 0.3, 0, false));
        Require(game.Snapshot.Player.X > building.CenterX - building.Width / 2 + 1 && game.Snapshot.Player.Damage == 0,
            "mutating the canonical footprint left an invisible wall at its old rendered location");
        PlacePlayer(game, -60 - building.Width / 2 - 7, 150, Math.PI / 2, 18);
        Run(game, 0.6, new DriverInput(0, 0.3, 0, false));
        Require(game.Snapshot.Player.Damage > 0.1, "moved canonical footprint has no matching contact boundary");

        var trafficGame = Racing();
        var baseline = Racing();
        Run(trafficGame, 2, Idle);
        Run(baseline, 2, Idle);
        var traffic = trafficGame.Activity.Traffic[0].Car;
        PlacePlayer(trafficGame, traffic.X - Math.Sin(traffic.Yaw) * 7,
            traffic.Z - Math.Cos(traffic.Yaw) * 7, traffic.Yaw, traffic.Speed + 19, traffic.Y);
        Run(trafficGame, 0.5, new DriverInput(0, 1, 0, false));
        Run(baseline, 0.5, Idle);
        var struck = trafficGame.Activity.Traffic[0].Car;
        Require(trafficGame.Snapshot.Player.Damage > 0 && struck.Damage > 0 &&
            Distance(struck, baseline.Activity.Traffic[0].Car) > 0.4,
            "traffic collision was cosmetic/one-sided rather than authoritative equal-response contact");
        Console.WriteLine($"  building damage={after.Player.Damage:F3}; traffic impact damage=" +
            $"{trafficGame.Snapshot.Player.Damage:F3}/{struck.Damage:F3}");
    }

    private static void DriveControls()
    {
        var ordinary = Racing();
        var boosted = Racing();
        PlacePlayer(ordinary, -640, -705, 0, 0);
        PlacePlayer(boosted, -640, -705, 0, 0);
        Run(ordinary, 2, new DriverInput(0, 1, 0, false));
        Run(boosted, 2, new DriverInput(0, 1, 0, true));
        Require(boosted.Snapshot.Player.Speed > ordinary.Snapshot.Player.Speed + 12 &&
            boosted.Snapshot.Player.Boost < 0.7 && boosted.Snapshot.Player.Boosting,
            "city surface lost Modern acceleration or boost consumption");
        Run(ordinary, 1, new DriverInput(0, 0, 1, false));
        Require(ordinary.Snapshot.Player.Speed < 2, "city brakes did not decelerate a physical road car");
        Run(ordinary, 3, new DriverInput(0, 0, 1, false));
        Require(ordinary.Snapshot.Player.Speed < -5 && ordinary.Snapshot.Player.Grounded,
            "held brake did not engage Modern reverse on city ground");
        var fuel = boosted.Snapshot.Player.Boost;
        boosted.Command("recover");
        Near(boosted.Snapshot.Player.Boost, fuel, 1e-12, "city recovery refilled boost");
        Run(boosted, 1, Idle);
        Require(boosted.Snapshot.Player.Boost > fuel && !boosted.Snapshot.Player.Boosting,
            "released city boost did not regenerate");
    }

    private static void SurfacesAndRecovery()
    {
        var game = Racing();
        PlacePlayer(game, -350, 155, Math.PI / 2, 8);
        var recoveries = game.Snapshot.Recoveries;
        Run(game, 5, new DriverInput(0, 0.35, 0, false));
        Require(game.Snapshot.Player.X > -295 && game.Snapshot.Player.Grounded &&
            game.Snapshot.Recoveries == recoveries && game.Snapshot.Player.Y is > 0.8 and < 1.2,
            "real park ground is not drivable independently of the guide ribbon");
        PlacePlayer(game, 400, 83, Math.PI / 2, 13);
        double maximumY = 0;
        for (var i = 0; i < 30 * 120 && game.Snapshot.Player.X < 765; i++)
        {
            game.Advance(Step, new DriverInput(0, 0.45, 0, false));
            maximumY = Math.Max(maximumY, game.Snapshot.Player.Y);
        }
        Require(maximumY > 20 && game.Snapshot.Player.X > 740 && game.Snapshot.Player.Y < 5 &&
            game.Snapshot.Recoveries == recoveries,
            $"car did not physically cross the visible bridge: x={game.Snapshot.Player.X:F1}, peak={maximumY:F1}, y={game.Snapshot.Player.Y:F1}");
        var onShore = game.Snapshot.Player;
        game.Command("recover");
        Require(game.Snapshot.Recoveries == recoveries + 1 && Distance(game.Snapshot.Player, onShore) < 30 &&
            game.Snapshot.Player.Speed == 0, "deliberate recovery returned to the guide rather than the last actual street");
        var deck = game.World.Roads.Single(r => r.Bridge).Points.Max(p => p.Y);
        PlacePlayer(game, 560, 80, Math.PI / 2, 0, deck + 1);
        Run(game, 1, Idle);
        Near(game.Snapshot.Player.Y, deck + 1, 1e-8, "visible bridge deck and Modern four-wheel contact height differ");
        PlacePlayer(game, 455, -105, Math.PI / 2, 8);
        Run(game, 2, Idle);
        Require(game.Snapshot.Recoveries == recoveries + 2 && game.Snapshot.Player.Y > 0.8 &&
            game.Snapshot.Message.Contains("River", StringComparison.OrdinalIgnoreCase),
            "water was an invisible floor or failed to recover to the safe street");
    }

    private static void InputsAndBudget()
    {
        var game = new CityDrivingSession();
        Throws<ArgumentOutOfRangeException>(() => game.Advance(double.NaN, Idle));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(-1, Idle));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(0, new DriverInput(double.NaN, 0, 0, false)));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(0, new DriverInput(0, double.PositiveInfinity, 0, false)));
        Throws<ArgumentOutOfRangeException>(() => game.Advance(0, new DriverInput(0, 0, double.NegativeInfinity, false)));
        Throws<InvalidOperationException>(() => game.Command("recover"));
        Throws<InvalidOperationException>(() => game.Command("resume"));
        Throws<NotSupportedException>(() => game.Command("ghost-on"));
        Throws<NotSupportedException>(() => game.Command("ghost-off"));
        Throws<ArgumentException>(() => new CityDrivingSession("fake-race"));
        Throws<ArgumentException>(() => game.Command("teleport"));
        game.Command("start");
        Throws<InvalidOperationException>(() => game.Command("start"));
        game.Advance(10_000, Idle);
        Require(game.Snapshot.Tick == 30, "city did not keep the bounded 30-tick stall policy");
        Near(game.Snapshot.Countdown, 2.75, 1e-10, "countdown stall budget");
        game.Advance(0, Idle);
        Require(game.Snapshot.Tick == 30, "discarded stall time leaked into the next call");
        var a = Racing();
        var b = Racing();
        Run(a, 2, new DriverInput(4, 3, -1, false));
        Run(b, 2, new DriverInput(1, 1, 0, false));
        Require(State(a) == State(b), "finite axis clamping changed normalized city dynamics");
    }

    private static void Schedules()
    {
        var reference = Trace(120);
        foreach (var hz in new[] { 60, 144 })
            Require(Trace(hz) == reference, $"{hz}Hz changed authoritative player/traffic/walker/race state");

        static string Trace(int hz)
        {
            var game = new CityDrivingSession("city-circuit");
            game.Command("start");
            foreach (var (seconds, input) in new[]
            {
                (3.0, Idle), (4.0, new DriverInput(0, 0.9, 0, true)),
                (2.0, new DriverInput(0.35, 0.5, 0, false)), (2.0, new DriverInput(-0.2, 0, 1, false))
            })
                Run(game, seconds, input, 1.0 / hz);
            return State(game);
        }
    }

    private static void BlitzGates()
    {
        var game = Racing("city-blitz");
        var gates = game.Activity.Checkpoints;
        Cross(game, gates[1]);
        Require(game.Activity.CheckpointsPassed == 0, "out-of-order spatial gate awarded progress");
        Cross(game, gates[0], true);
        Require(game.Activity.CheckpointsPassed == 0, "backwards gate awarded progress");
        var deadline = game.Activity.TimeRemaining + game.Activity.Elapsed;
        Cross(game, gates[0]);
        Require(game.Activity.CheckpointsPassed == 1, "forward street gate did not award progress");
        Near(game.Activity.TimeRemaining + game.Activity.Elapsed, deadline + 8, 1e-8, "checkpoint clock bonus");
        game.Command("pause");
        var paused = State(game);
        Throws<InvalidOperationException>(() => game.Command("recover"));
        game.Advance(100, Idle);
        Require(State(game) == paused, "paused blitz advanced its checkpoint clock or actors");
        game.Command("resume");
        var time = game.Snapshot.Time;
        game.Command("recover");
        Near(game.Snapshot.Time, time + 5, 1e-9, "blitz recovery did not charge its real clock");
        Require(game.Activity.CheckpointsPassed == 1, "recovery crossed or reset a street checkpoint");
        for (var i = 1; i < gates.Length; i++)
        {
            Cross(game, gates[i]);
            Require(game.Activity.CheckpointsPassed == i + 1, $"real gate {gates[i].Id} was not crossed in order");
        }
        Require(game.Snapshot.Phase == "finished" && game.Activity is { Success: true, Result: "completed" } &&
            game.Snapshot.Trial is { Success: true, CheckpointsPassed: 6 },
            "six physical street crossings did not complete city blitz");
        var finished = State(game);
        game.Advance(100, new DriverInput(0, 1, 0, true));
        Require(State(game) == finished, "finished blitz kept simulating city actors");
        var timeout = Racing("city-blitz");
        for (var i = 0; i < 1000 && timeout.Snapshot.Phase != "finished"; i++)
            timeout.Advance(0.25, Idle);
        Require(timeout.Activity is { Success: false, Result: "timeout", CheckpointsPassed: 0 } &&
            timeout.Snapshot.Time is >= 245 and < 246, "idle city blitz did not expire at the real running deadline");

        static void Cross(CityDrivingSession game, CityCheckpointDefinition gate, bool reverse = false)
        {
            var heading = gate.Heading + (reverse ? Math.PI : 0);
            PlacePlayer(game, gate.X - Math.Sin(heading) * 6, gate.Z - Math.Cos(heading) * 6,
                heading, 16, gate.Y + 1);
            Run(game, 0.7, new DriverInput(0, 0.4, 0, false));
        }
    }

    private static void ExploreStreets()
    {
        var game = Racing();
        var points = Rounded(
            [new(-160, -290), new(-160, 560), new(-640, 560), new(-640, -160), new(-400, -160)], false);
        var driver = new CheckDriver(points, false, 17, 0);
        var roads = new HashSet<string>();
        double offGuide = 0;
        for (var i = 0; i < 170 * 120; i++)
        {
            game.Advance(Step, driver.Input(game.Snapshot.Player));
            if (i % 120 != 0)
                continue;
            var activity = game.Activity;
            roads.Add(activity.CurrentRoad);
            var car = game.Snapshot.Player;
            var guide = game.Track.Sample(car.X, car.Z);
            offGuide = Math.Max(offGuide, Math.Sqrt(Squared(car.X - guide.X, car.Z - guide.Z)));
        }
        Require(roads.Count >= 3 && offGuide > 200 && game.Activity.DrivenDistance > 1700 &&
            game.Snapshot.Recoveries == 0 && game.Snapshot.Phase == "racing",
            $"exploration failed: roads={string.Join('/', roads)}, away={offGuide:F0}m, " +
            $"distance={game.Activity.DrivenDistance:F0}, recoveries={game.Snapshot.Recoveries}, " +
            $"pose={game.Snapshot.Player.X:F1}/{game.Snapshot.Player.Z:F1}, yaw={game.Snapshot.Player.Yaw:F2}, " +
            $"damage={game.Snapshot.Player.Damage:F2}, {driver.Diagnostics}");
        Console.WriteLine($"  drove {game.Activity.DrivenDistance:F0}m over {roads.Count} streets, {offGuide:F0}m beyond the guide, no forced recovery");
    }

    private static void DriveBlitz()
    {
        var game = Racing("city-blitz");
        var points = game.World.Routes.Single(r => r.Id == "city-blitz").Points.Select(p => new Point(p.X, p.Z)).ToArray();
        var driver = new CheckDriver(points, false, 27, 0);
        var peak = 0.0;
        var roads = new HashSet<string>();
        for (var i = 0; i < 300 * 120 && game.Snapshot.Phase != "finished"; i++)
        {
            game.Advance(Step, driver.Input(game.Snapshot.Player));
            peak = Math.Max(peak, game.Snapshot.Player.Y);
            if (i % 120 == 0)
                roads.Add(game.Activity.CurrentRoad);
        }
        Require(game.Activity is { Success: true, CheckpointsPassed: 6 } &&
            game.Activity.DrivenDistance > 4300 && peak > 20 && roads.Count >= 7,
            $"public blitz failed: {game.Activity.Result}, passed={game.Activity.CheckpointsPassed}, " +
            $"distance={game.Activity.DrivenDistance:F0}, peak={peak:F1}, roads={roads.Count}, " +
            $"pose={game.Snapshot.Player.X:F1}/{game.Snapshot.Player.Z:F1}, rec={game.Snapshot.Recoveries}, {driver.Diagnostics}");
        Console.WriteLine($"  cross-city blitz drove {game.Activity.DrivenDistance:F0}m through {roads.Count} streets " +
            $"and the 20m bridge, finished in {game.Snapshot.Time:F1}s with {game.Activity.TimeRemaining:F1}s left");
    }

    private static void RivalCircuit()
    {
        var game = Racing("city-circuit");
        PlacePlayer(game, -110, 135, 0, 0);
        var previous = game.Snapshot.PaceCar!;
        double distance = 0, peak = 0;
        var visited = new HashSet<string>();
        for (var i = 0; i < 2000 && game.Snapshot.Phase != "finished"; i++)
        {
            game.Advance(0.25, Idle);
            var car = game.Snapshot.PaceCar!;
            distance += Distance(previous, car);
            peak = Math.Max(peak, car.Speed);
            previous = car;
            if (i % 40 == 0)
                visited.Add($"{Math.Round(car.X / 150)}/{Math.Round(car.Z / 150)}");
        }
        Require(game.Snapshot.Race is { PlayerWon: false, Result: "rival-won", RivalLap: 2 } &&
            distance > 4700 && peak > 18 && visited.Count > 10,
            $"rival did not physically finish: phase={game.Snapshot.Phase}, " +
            $"lap={game.Snapshot.Race!.RivalLap}, dist={distance:F0}, recoveries={game.Snapshot.Race.RivalRecoveries}, " +
            $"pose={previous.X:F1}/{previous.Z:F1}, speed={previous.Speed:F1}");
        Console.WriteLine($"  physical rival drove {distance:F0}m at up to {peak:F1}m/s, completed two laps at " +
            $"{game.Snapshot.Time:F1}s with {game.Snapshot.Race!.RivalRecoveries} recoveries");
    }

    private static void PlayerCircuit()
    {
        var game = Racing("city-circuit");
        var points = game.World.Routes.Single(r => r.Id == "city-circuit").Points.Select(p => new Point(p.X, p.Z)).ToArray();
        var driver = new CheckDriver(points, true, 27, 0);
        for (var i = 0; i < 400 * 120 && game.Snapshot.Phase != "finished"; i++)
            game.Advance(Step, driver.Input(game.Snapshot.Player));
        Require(game.Snapshot.Race is { PlayerWon: true, Result: "player-won" } &&
            game.Activity.CheckpointsPassed == 12 && game.Activity.DrivenDistance > 4700,
            $"player did not win actual street laps: {game.Snapshot.Phase}, passed={game.Activity.CheckpointsPassed}, " +
            $"distance={game.Activity.DrivenDistance:F0}, rec={game.Snapshot.Recoveries}, " +
            $"pose={game.Snapshot.Player.X:F1}/{game.Snapshot.Player.Z:F1}, speed={game.Snapshot.Player.Speed:F1}, " +
            $"rival={game.Snapshot.Race!.RivalLap}/{game.Snapshot.Race.Result}, yaw={game.Snapshot.Player.Yaw:F2}, " +
            $"damage={game.Snapshot.Player.Damage:F2}, {driver.Diagnostics}");
    }

    private static CityDrivingSession Racing(string mode = "free-roam")
    {
        var game = new CityDrivingSession(mode);
        game.Command("start");
        Run(game, 3, Idle);
        return game;
    }

    private static void Run(CityDrivingSession game, double seconds, DriverInput input, double frame = Step)
    {
        double elapsed = 0;
        while (elapsed < seconds - 1e-10)
        {
            var delta = Math.Min(frame, seconds - elapsed);
            game.Advance(delta, input);
            elapsed += delta;
        }
    }

    // Controlled scenario placement is confined to this native harness. Every assertion then
    // advances actual public inputs through Modern contacts; no test writes race/checkpoint state.
    private static void PlacePlayer(CityDrivingSession game, double x, double z, double heading, double speed, double y = 1.04)
    {
        var racer = typeof(CityDrivingSession).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var field = racer.GetType().GetField("Car")!;
        var car = field.GetValue(racer)!;
        foreach (var item in new (string Name, object Value)[]
        {
            ("X", x), ("Y", y), ("Z", z), ("Vx", Math.Sin(heading) * speed),
            ("Vz", Math.Cos(heading) * speed), ("Vy", 0.0), ("Yaw", heading),
            ("Pitch", 0.0), ("Roll", 0.0), ("YawRate", 0.0), ("PitchRate", 0.0), ("RollRate", 0.0),
            ("Damage", 0.0), ("Steer", 0.0), ("Grounded", true)
        })
            car.GetType().GetField(item.Name)!.SetValue(car, item.Value);
        field.SetValue(racer, car);
        racer.GetType().GetField("Previous")!.SetValue(racer, car);
    }

    private static string State(CityDrivingSession game) => JsonSerializer.Serialize(new { game.Snapshot, game.Activity }, Json);
    private static string Actors(CityDrivingSession game) => JsonSerializer.Serialize(
        new { game.Snapshot.Player, game.Snapshot.PaceCar, game.Activity.Traffic, game.Activity.Pedestrians }, Json);
    private static double Squared(double x, double z) => x * x + z * z;
    private static double Distance(CarSnapshot a, CarSnapshot b) => Math.Sqrt(Squared(a.X - b.X, a.Z - b.Z));
    private static void Near(double a, double b, double tolerance, string message) => Require(Math.Abs(a - b) <= tolerance, $"{message}: {a:R}/{b:R}");
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private readonly record struct Point(double X, double Z);

    private static Point[] Rounded(Point[] corners, bool closed)
    {
        var points = new List<Point>();
        for (var i = 0; i < corners.Length; i++)
        {
            var b = corners[i];
            if (!closed && (i == 0 || i == corners.Length - 1))
            {
                points.Add(b);
                continue;
            }
            var a = corners[(i + corners.Length - 1) % corners.Length];
            var c = corners[(i + 1) % corners.Length];
            var before = Math.Sqrt(Squared(b.X - a.X, b.Z - a.Z));
            var after = Math.Sqrt(Squared(c.X - b.X, c.Z - b.Z));
            var trim = Math.Min(38, Math.Min(before, after) * 0.4);
            var entry = new Point(b.X - (b.X - a.X) / before * trim, b.Z - (b.Z - a.Z) / before * trim);
            var exit = new Point(b.X + (c.X - b.X) / after * trim, b.Z + (c.Z - b.Z) / after * trim);
            for (var j = 0; j <= 12; j++)
            {
                var t = j / 12.0;
                var u = 1 - t;
                points.Add(new Point(u * u * entry.X + 2 * u * t * b.X + t * t * exit.X,
                    u * u * entry.Z + 2 * u * t * b.Z + t * t * exit.Z));
            }
        }
        return points.ToArray();
    }

    private sealed class CheckDriver
    {
        private readonly Point[] _points;
        private readonly bool _closed;
        private readonly double[] _distance;
        private readonly double _cruise;
        private readonly double _lane;
        private double _s;
        private double _stuck;
        public string Diagnostics { get; private set; } = "";

        public CheckDriver(Point[] points, bool closed, double cruise, double lane)
        {
            _points = points;
            _closed = closed;
            _cruise = cruise;
            _lane = lane;
            _distance = new double[(closed ? points.Length : points.Length - 1) + 1];
            for (var i = 0; i < _distance.Length - 1; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                _distance[i + 1] = _distance[i] + Math.Sqrt(Squared(b.X - a.X, b.Z - a.Z));
            }
        }

        public DriverInput Input(CarSnapshot car)
        {
            var best = double.MaxValue;
            var anchor = _s;
            for (var i = 0; i < _distance.Length - 1; i++)
            {
                var a = _points[i];
                var b = _points[(i + 1) % _points.Length];
                var dx = b.X - a.X;
                var dz = b.Z - a.Z;
                var t = Math.Clamp(((car.X - a.X) * dx + (car.Z - a.Z) * dz) / Squared(dx, dz), 0, 1);
                var candidate = _distance[i] + (_distance[i + 1] - _distance[i]) * t;
                if (Math.Abs(_closed ? Math.IEEERemainder(candidate - anchor, _distance[^1]) : candidate - anchor) > 100)
                    continue;
                var error = Squared(car.X - a.X - dx * t, car.Z - a.Z - dz * t);
                if (error >= best)
                    continue;
                best = error;
                _s = candidate;
            }
            var speed = Math.Abs(car.Speed);
            var look = 10 + speed * 0.55;
            var target = At(_s + look, _lane);
            var direction = Math.IEEERemainder(Math.Atan2(target.X - car.X, target.Z - car.Z) - car.Yaw, Math.Tau);
            var heading = Heading(_s);
            var bend = Math.Abs(Math.IEEERemainder(Heading(_s + 46) - heading, Math.Tau));
            var desired = bend > 0.10 ? 10.5 : _cruise;
            if (Math.Abs(direction) > 0.6)
                desired = Math.Min(desired, 7);
            if (!_closed && _s > _distance[^1] - 25)
                desired = 0;
            var scale = Math.Clamp(car.Speed / 12, -1, 1) * 1.35 / (1 + speed / 13);
            var steer = Math.Abs(scale) < 0.03 ? 0 : Math.Clamp(2 * Math.Max(speed, 7) * Math.Sin(direction) / look / scale, -1, 1);
            Diagnostics = $"s={_s:F1}, target={target.X:F1}/{target.Z:F1}, error={direction:F2}, steer={steer:F2}";
            _stuck = speed < 0.6 ? _stuck + Step : Math.Max(0, _stuck - Step);
            if (_stuck is > 2 and < 4)
                return new DriverInput(-steer, 0, 0.7, false);
            if (_stuck >= 4)
                _stuck = 0;
            return new DriverInput(steer, Math.Clamp((desired - car.Speed) * 0.7, 0, 1),
                Math.Clamp((car.Speed - desired - 0.5) * 0.4, 0, 1), false);
        }

        private double Heading(double s)
        {
            var a = At(s);
            var b = At(s + 0.01);
            return Math.Atan2(b.X - a.X, b.Z - a.Z);
        }

        private Point At(double s, double lane = 0)
        {
            var length = _distance[^1];
            s = _closed ? (s % length + length) % length : Math.Clamp(s, 0, length - 1e-7);
            var i = Array.BinarySearch(_distance, s);
            i = i < 0 ? ~i - 1 : Math.Min(i, _distance.Length - 2);
            var a = _points[i];
            var b = _points[(i + 1) % _points.Length];
            var t = (s - _distance[i]) / (_distance[i + 1] - _distance[i]);
            var heading = Math.Atan2(b.X - a.X, b.Z - a.Z);
            return new Point(a.X + (b.X - a.X) * t + Math.Cos(heading) * lane,
                a.Z + (b.Z - a.Z) * t - Math.Sin(heading) * lane);
        }
    }
}
