using System.IO.Compression;
using System.Text.Json;
using StuntRacer.Core;
using StuntRacer.Core.GeographicDrive;
using StuntRacer.Flight;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SeattleDriveChecks /path/to/world-flight.zip");
    return 2;
}

using var package = ZipFile.OpenRead(args[0]);
using var reader = new StreamReader(package.GetEntry("seattle-roads.json")!.Open());
var roads = reader.ReadToEnd();
var elevation = new WorldFlightElevation();
foreach (var tile in SeattleDriveArea.Tiles)
{
    using var entry = package.GetEntry($"tiles/conus/{tile.Level}/{tile.X}/{tile.Z}.bin")!.Open();
    var data = new byte[WorldFlightElevation.TileLength];
    entry.ReadExactly(data);
    elevation.AddTile(tile.Level, tile.X, tile.Z, data);
}
bool terrainAvailable = true;
SeattleDrivingSession Make() => new(roads, (x, z) => terrainAvailable ? elevation.Sample(x, z) : null);

var game = Make();
Check(game.Destinations.Count >= 3, "The real ZIP supports at least three stops.");
Check(game.MapStreets.Count() > 3500, "Connected streets must come from the real map.");
Check(game.Activity.CruiseSpeed == "normal", "New drives must keep the established normal Autopilot cruise.");
Expect<ArgumentException>(() => game.Command("autopilot-speed:invalid"));
SeattleGroundChecks.CheckSpawn(package, game, elevation);
using var streetData = JsonDocument.Parse(roads);
var edges = new HashSet<(DrivePoint, DrivePoint)>();
foreach (var row in streetData.RootElement.GetProperty("roads").EnumerateArray())
{
    if (row[1].GetString() == "freeway" || row[2].GetString() != "surface") continue;
    var points = row[3].EnumerateArray().Select(p => new DrivePoint(p[0].GetDouble(), p[1].GetDouble())).ToArray();
    for (var i = 1; i < points.Length; i++)
    {
        edges.Add((points[i - 1], points[i]));
        edges.Add((points[i], points[i - 1]));
    }
}
foreach (var edge in game.MapStreets)
{
    Check(edges.Contains((new(edge[0], edge[1]), new(edge[2], edge[3]))), "Map line must be an actual surface link.");
    Check(edge[0] is >= -640 and <= 1000 && edge[2] is >= -640 and <= 1000 &&
        edge[1] is >= 6800 and <= 10740 && edge[3] is >= 6800 and <= 10740 &&
        elevation.Sample((edge[0] + edge[2]) / 2, (edge[1] + edge[3]) / 2) is { IsWater: false },
        "Published street map must stay on the supported dry downtown terrain.");
}
Console.WriteLine($"Real ZIP: {game.MapStreets.Count()} connected surface links, {game.Destinations.Count} destinations.");
Expect<InvalidDataException>(() => _ = new SeattleDrivingSession("""{"roads":[]}""", elevation.Sample));
terrainAvailable = false;
Expect<InvalidDataException>(() => _ = Make());
terrainAvailable = true;
game.Command("start");
Expect<InvalidOperationException>(() => game.Command("autopilot-on"));
Expect<ArgumentException>(() => game.Command("destination:not-a-stop"));
Expect<ArgumentOutOfRangeException>(() => game.Advance(.1, new DriverInput(double.NaN, 0, 0, false)));
terrainAvailable = false;
var frozen = game.Advance(.25, new DriverInput(0, 1, 0, false));
Check(frozen.Tick == 0 && game.Activity.TerrainWaiting && Position(frozen) == SeattleDriveArea.Spawn,
    "Missing ground must freeze time and preserve world-space pose.");
terrainAvailable = true;
game.Command("restart");

var wheel = Arrive(game, "great-wheel", checkPause: true);
var normalWheelTicks = game.Snapshot.Tick;
var spheres = Arrive(game, "amazon-spheres");
var needle = Arrive(game, "space-needle");
game.Command("restart");
var stadium = Arrive(game, "lumen-field");
var park = Arrive(game, "t-mobile-park");

var alternate = Make();
alternate.Command("start");
var wheelAlternate = Arrive(alternate, "great-wheel", frameDeltas: [.024, .17, .013, .12, .195]);
Check(Distance(wheel, wheelAlternate) < 1e-8, "Fixed-step arrival position must not depend on frame cadence.");

var fast = Make();
fast.Command("autopilot-speed:fast");
Check(fast.Snapshot.Phase == "ready" && fast.Activity.CruiseSpeed == "fast", "Cruise can be selected in the garage.");
fast.Command("start");
Arrive(fast, "great-wheel");
Check(fast.Snapshot.Tick < normalWheelTicks - 240, "Fast Autopilot must reach the Wheel at least two seconds sooner.");
foreach (var id in new[] { "amazon-spheres", "space-needle", "lumen-field", "t-mobile-park" })
{
    fast.Command("restart");
    Arrive(fast, id);
}

var slow = Make();
slow.Command("autopilot-speed:slow");
slow.Command("start");
Arrive(slow, "great-wheel");
Check(slow.Snapshot.Tick > normalWheelTicks + 240, "Slow Autopilot must cruise below normal speed and still reach the stop.");

var speedChange = Make();
speedChange.Command("start");
speedChange.Command("destination:great-wheel");
speedChange.Command("autopilot-on");
for (var i = 0; i < 12; i++) speedChange.Advance(.25, default);
var beforeCruise = speedChange.Snapshot;
var beforeCruiseActivity = speedChange.Activity;
speedChange.Command("autopilot-speed:slow");
Check(speedChange.Activity == (beforeCruiseActivity with { CruiseSpeed = "slow" }) &&
    Position(speedChange.Snapshot) == Position(beforeCruise) && speedChange.Snapshot.Tick == beforeCruise.Tick &&
    speedChange.Snapshot.Player.Speed == beforeCruise.Player.Speed, "Changing cruise while moving must not interrupt Autopilot or snap the car.");
speedChange.Command("pause");
speedChange.Command("autopilot-speed:fast");
speedChange.Advance(.2, new(1, 1, 0, false));
Check(speedChange.Activity is { Autopilot: true, CruiseSpeed: "fast" } && speedChange.Snapshot.Phase == "paused" &&
    speedChange.Snapshot.Tick == beforeCruise.Tick && Position(speedChange.Snapshot) == Position(beforeCruise),
    "Changing cruise while paused must not move the car or relinquish Autopilot.");
speedChange.Command("resume");
speedChange.Advance(.25, default);
Check(speedChange.Activity.Autopilot && speedChange.Snapshot.Tick > beforeCruise.Tick &&
    Distance(Position(speedChange.Snapshot), Position(beforeCruise)) > 1, "Autopilot must continue after changing cruise and resuming.");

game.Command("restart");
game.Command("destination:great-wheel");
game.Command("autopilot-on");
for (var i = 0; i < 7; i++) game.Advance(.25, default);
var before = game.Snapshot;
game.Advance(0, new DriverInput(-0.7, 0.55, 0, false));
Check(!game.Activity.Autopilot && game.Activity.Message.Contains("driver input") && game.Snapshot.Tick == before.Tick,
    "Any driver input disengages autopilot before the next physics step.");
for (var i = 0; i < 6; i++) game.Advance(.125, new DriverInput(-0.7, 0.55, 0, false));
var manual = game.Snapshot;
Check(Distance(Position(before), Position(manual)) > 2 && Math.IEEERemainder(manual.Player.Yaw - before.Player.Yaw, Math.Tau) < -.08,
    "Manual throttle and steer must drive and change native yaw.");
game.Command("garage");
Check(game.Snapshot.Phase == "ready" && game.Snapshot.Tick == 0 && Position(game.Snapshot) == SeattleDriveArea.Spawn,
    "Garage returns to the street start and clears autopilot.");
Console.WriteLine($"Manual takeover at tick {before.Tick}; advanced {Distance(Position(before), Position(manual)):F1} m and yaw changed " +
    $"{Math.IEEERemainder(manual.Player.Yaw - before.Player.Yaw, Math.Tau):F2} rad.");
ManualSteeringChecks.Run(Make());
SeattleGroundChecks.Run(roads, elevation);
return 0;

DrivePoint Arrive(SeattleDrivingSession session, string id, bool checkPause = false, double[]? frameDeltas = null)
{
    frameDeltas ??= [.25];
    var origin = Position(session.Snapshot);
    var previous = origin;
    var before = session.Activity.DrivenDistance;
    var start = session.Snapshot.Time;
    session.Command($"destination:{id}");
    session.Command("autopilot-on");
    var plan = session.Plan!;
    Check(session.Activity.Autopilot && plan.Points.Count > 10, $"{id}: must engage with a multi-link plan. {session.Activity.Message}");
    for (var i = 2; i < plan.Points.Count; i++)
        Check(edges.Contains((plan.Points[i - 1], plan.Points[i])), $"{id}: route must follow exact shared nodes.");
    Check(Distance(origin, plan.Points[0]) < 4, $"{id}: route must begin at an actual nearby street.");
    var minY = session.Snapshot.Player.Y;
    var maxY = minY;
    var maxSpeed = 0.0;
    var minTurnSpeed = double.PositiveInfinity;
    var steerSamples = 0;
    for (var frame = 0; session.Activity.Autopilot && session.Snapshot.Time - start < 750; frame++)
    {
        if (checkPause && frame == 8)
        {
            session.Command("pause");
            var paused = session.Snapshot;
            session.Advance(.2, new DriverInput(1, 1, 0, false));
            Check(session.Snapshot.Tick == paused.Tick && Position(paused) == Position(session.Snapshot) && session.Activity.Autopilot,
                "Pause freezes auto, position and tick.");
            session.Command("resume");
        }
        var s = session.Advance(frameDeltas[frame % frameDeltas.Length], default);
        var actual = Position(s);
        var step = Distance(previous, actual);
        Check(step < 4.1, $"{id}: position moved {step:F2} m in one frame (possible route snapping).");
        previous = actual;
        minY = Math.Min(minY, s.Player.Y);
        maxY = Math.Max(maxY, s.Player.Y);
        maxSpeed = Math.Max(maxSpeed, Math.Abs(s.Player.Speed));
        if (s.Player.Speed > 2 && Math.Abs(s.Player.Steer) > .27)
        {
            steerSamples++;
            minTurnSpeed = Math.Min(minTurnSpeed, s.Player.Speed);
        }
    }
    var finish = session.Snapshot;
    var driven = session.Activity.DrivenDistance - before;
    var end = Position(finish);
    Console.WriteLine($"{id} ({session.Activity.CruiseSpeed}): {session.Activity.Status} in {finish.Time - start:F2}s, " +
        $"plan {plan.Length:F1} m, drove {driven:F1} m, " +
        $"speed {finish.Player.Speed:F2}/{maxSpeed:F1} m/s, turn {minTurnSpeed:F1} m/s, height {minY:F1}–{maxY:F1} m; " +
        $"pos {end}, remaining {session.Activity.Remaining:F1} m.");
    Check(session.Activity.Status == "arrived", $"{id}: {session.Activity.Message}");
    Check(driven > plan.Length * .8 && driven < plan.Length * 1.15 && Distance(end, plan.Points[^1]) < 2.4,
        $"{id}: actually travel the mapped route and reach its street stop.");
    Check(maxSpeed > 8 && steerSamples >= 5 && minTurnSpeed < maxSpeed * .75,
        $"{id}: accelerate, steer, and slow for turns.");
    for (var i = 0; i < 12; i++) session.Advance(.25, default);
    Check(Position(session.Snapshot) == end && Math.Abs(session.Snapshot.Player.Speed) < .05,
        $"{id}: the car must stay stopped after arrival.");
    return end;
}

static DrivePoint Position(GameSnapshot snapshot) => new(snapshot.Player.X, snapshot.Player.Z);
static double Distance(DrivePoint a, DrivePoint b) => double.Hypot(a.X - b.X, a.Z - b.Z);
static void Check(bool success, string failure)
{
    if (!success) throw new InvalidOperationException(failure);
}
static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
