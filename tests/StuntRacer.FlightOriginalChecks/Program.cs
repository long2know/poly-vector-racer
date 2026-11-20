using StuntRacer.Flight;
using StuntRacer.FlightData;
using StuntRacer.FlightOriginal;
using System.Text.Json;

var at = Array.IndexOf(args, "--package");
if (at < 0 || at + 1 >= args.Length)
{
    Console.Error.WriteLine("Supply --package <external-original-flight.json>; no authored or invented-data fallback is used.");
    return 2;
}
var package = FlightPackageSerializer.ReadPackage(File.ReadAllBytes(args[at + 1]));
var idle = new FlightInput(0, 0, 0, 0, false, false);
var passed = 0;
var failed = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
void Run(OriginalFlightSession session, double seconds, FlightInput input)
{
    for (var i = 0; i < Math.Round(seconds * 60); i++) session.Advance(1.0 / 60, input);
}
void Pose(AircraftSnapshot a)
{
    Require(new[] { a.X, a.Y, a.Z, a.Speed, a.Yaw, a.Pitch, a.Roll }.All(double.IsFinite), "Non-finite source snapshot.");
    var f = a.Forward;
    var u = a.Up;
    Require(Math.Abs(f.X * f.X + f.Y * f.Y + f.Z * f.Z - 1) < 1e-8, "Forward is not normalized.");
    Require(Math.Abs(u.X * u.X + u.Y * u.Y + u.Z * u.Z - 1) < 1e-8, "Up is not normalized.");
    Require(Math.Abs(f.X * u.X + f.Y * u.Y + f.Z * u.Z) < 1e-8, "Render basis is not orthogonal.");
}

Check("actual original data creates a real source world and exact source HUD", () =>
{
    var session = new OriginalFlightSession(package);
    var snapshot = session.Snapshot;
    Pose(snapshot.Aircraft);
    Require(snapshot.Phase == "ready" && snapshot.Aircraft.OnGround, "Original runway spawn is not ready/on-ground.");
    Require(snapshot.Display is { AltitudeFeet: 5, HeadingDegrees: 28, SpeedKnots: 0, FuelGauge: 25000 },
        "Original integer HUD units/rounding were replaced with authored SI readouts.");
    var world = session.World.SourceWorld ?? throw new InvalidOperationException("No original world was published.");
    Require(world is { Surfaces.Count: > 100, Objects.Count: > 0 },
        "Original world did not use real source coast/pad/model data.");
    Require(session.World.Islands.Count == 0 && session.World.LandingSurfaces.Count(p => p.Name.StartsWith("Airfield")) == 10 &&
        session.World.LandingSurfaces.Any(p => p.Name == "Carrier deck"),
        "An authored bay rectangle was substituted for original pad quads.");
    Console.WriteLine($"  source world {world.Surfaces.Count} surfaces / {world.Objects.Count} meshes");
});
Check("actual placed bridges, synthesized scenery and ground programs survive the world mesh boundary", () =>
{
    var session = new OriginalFlightSession(package);
    var world = session.World.SourceWorld!;
    var expected = FlightScenePlacements.Enumerate(package.Scene).Where(p => !p.RuntimeModelSlot)
        .Select(p => (p.ModelId, p.PositionX, p.PositionZ)).Distinct().Count() + 1;
    Require(world.Objects.Count == expected, $"Only {world.Objects.Count} of {expected} original placed meshes were emitted.");
    foreach (var id in Enumerable.Range(48, 10).Append(65).Concat([46, 72, 73, 95, 61, 62, 90, 91, 92, 102, 103, 109, 112]))
    {
        var meshes = world.Objects.Where(m => m.Name == $"Original scenery {id}").ToArray();
        Require(meshes.Length > 0 && meshes.All(m => m.Faces.Count > 0), $"Original scenery {id} remains invisible.");
    }
    Require(world.Objects.SelectMany(m => m.Vertices).All(v => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z)) &&
        world.Objects.All(m => m.Faces.All(f => f.Vertices.All(i => i >= 0 && i < m.Vertices.Count) &&
            f.Colour.All(c => double.IsFinite(c) && c is >= 0 and <= 1))), "World geometry contains invalid positions, indices or colours.");
    var bridge = world.Objects.First(m => m.Name == "Original scenery 48");
    Require(bridge.Vertices.Max(v => v.Z) - bridge.Vertices.Min(v => v.Z) > 1000 &&
        bridge.Vertices.Max(v => v.Y) is > 100 and < 400 &&
        bridge.Faces.Any(f => f.Colour[0] > f.Colour[1] * 2 && f.Colour[0] > f.Colour[2] * 2),
        "The original bridge scale, height or source red pen became a generic/grey placeholder.");
    foreach (var id in new[] { 48, 50, 54, 46 })
    {
        var placement = FlightScenePlacements.Enumerate(package.Scene).First(p => p.ModelId == id);
        var geometry = FlightModelMaterializer.Materialize(package, id);
        var mesh = world.Objects.First(m => m.Name == $"Original scenery {id}");
        var origin = session.RawState.Raw.Position;
        bool ground = geometry.Vertices.All(v => v.Y == 0);
        for (int i = 0; i < geometry.Vertices.Count; i++)
        {
            var source = geometry.Vertices[i];
            var actual = mesh.Vertices[i];
            double x = -(placement.PositionX + source.X - origin.X) * OriginalFlightSession.MetresPerPosition;
            double y = (placement.PositionY + source.Y) * OriginalFlightSession.MetresPerPosition + (ground ? 0.015 : 0);
            double z = (placement.PositionZ + source.Z - origin.Z) * OriginalFlightSession.MetresPerPosition;
            Require(Math.Abs(actual.X - x) < 1e-8 && Math.Abs(actual.Y - y) < 1e-8 && Math.Abs(actual.Z - z) < 1e-8,
                $"Model {id}, rendered vertex {i}: source placement/reflection/scale was applied incorrectly.");
        }
    }
    Require(world.GeometryNotes.All(n => !n.Contains("source-only", StringComparison.Ordinal) &&
        !n.Contains("no materialized static faces", StringComparison.Ordinal)),
        "A placed static source program is still excluded from rendering.");
    Console.WriteLine($"  materialized world {world.Objects.Sum(m => m.Faces.Count)} faces / {world.Objects.Sum(m => m.Vertices.Count)} vertices");
});
Check("original carrier spawn, catapult hold/release and hook use the actual ship contacts", () =>
{
    var session = new OriginalFlightSession(package);
    session.Command("carrier-start");
    Require(session.Snapshot.Configuration is { OnCarrier: true } &&
        session.Snapshot.Display?.AltitudeFeet == 145, "Original deck height/start was not preserved.");
    session.Command("hook-toggle");
    Require(session.Snapshot.Configuration?.HookDown == true, "Original arrestor hook did not deploy.");
    session.Command("hook-toggle");
    session.Command("start");
    Run(session, 3, idle);
    Run(session, 2, idle);
    Require(session.RawState.Raw.TargetSpeed == 0, "Original catapult did not hold at low power.");
    var started = session.RawState.Raw.Position;
    for (var i = 0; i < 60 * 25 && session.Snapshot.Phase == "flying"; i++)
        session.Advance(1.0 / 60, new(0, 0.1, 0, 1, true, false));
    Require(session.RawState.Raw.Position != started && session.Snapshot.Display!.SpeedKnots > 80,
        "Original carrier/catapult failed to release with source throttle.");
});
Check("ready, pause and explicit original restart freeze/reset correctly", () =>
{
    var session = new OriginalFlightSession(package);
    var ready = session.RawState;
    session.Advance(10, new(1, 1, 1, 1, true, true));
    Require(session.RawState == ready, "Ready source aircraft advanced.");
    session.Command("start");
    Run(session, 3, idle);
    Require(session.Snapshot.Phase == "flying" && session.Snapshot.Countdown == 0, "Original countdown is not three seconds.");
    Run(session, 2, new(0, 0, 0, 1, false, false));
    session.Command("pause");
    var raw = session.RawState;
    var snapshot = session.Snapshot;
    session.Advance(20, new(1, 1, 1, 1, true, true));
    Require(session.RawState == raw && session.Snapshot == snapshot, "Paused source state/fuel/clock changed.");
    session.Command("restart");
    Require(session.Snapshot is { Phase: "countdown", Recoveries: 0, Crashes: 0 } &&
        session.Snapshot.Display?.FuelGauge == 25000, "Original restart did not reset the source session.");
});
Check("original fixed ticks are independent of 60/120/144 Hz displays", () =>
{
    OriginalFlightSession? baseline = null;
    foreach (var rate in new[] { 60, 120, 144 })
    {
        var session = new OriginalFlightSession(package);
        session.Command("air-start");
        var input = new FlightInput(0.15, 0.1, 0, 0.7, false, false);
        for (var i = 0; i < rate * 4; i++) session.Advance(1.0 / rate, input);
        Pose(session.Snapshot.Aircraft);
        Require(session.Snapshot.Tick == 32, "Source pace is not eight fixed steps per second.");
        if (baseline is not null) Require(session.RawState == baseline.RawState, $"{rate}Hz altered original raw state.");
        baseline = session;
    }
});
Check("actual source throttle/elevator lift off and consume original fuel", () =>
{
    var session = new OriginalFlightSession(package);
    session.Command("start");
    Run(session, 3, idle);
    var airborne = false;
    for (var frame = 0; frame < 60 * 70 && session.Snapshot.Phase == "flying"; frame++)
    {
        var a = session.Snapshot.Aircraft;
        var pitch = a.Speed * 1.943844 > 125 ? 0.6 : 0;
        session.Advance(1.0 / 60, new(0, pitch, 0, 1, false, false));
        Pose(session.Snapshot.Aircraft);
        if (!session.Snapshot.Aircraft.OnGround && session.Snapshot.Aircraft.Y > 8)
        {
            airborne = true;
            break;
        }
    }
    var snapshot = session.Snapshot;
    Require(airborne && snapshot.Aircraft.Fuel < 1 && snapshot.Aircraft.Speed > 30,
        $"No real source takeoff: phase={snapshot.Phase},t={snapshot.Time},y={snapshot.Aircraft.Y},speed={snapshot.Aircraft.Speed},raw={session.RawState.Raw}");
    Console.WriteLine($"  source takeoff t={snapshot.Time:F3}s,y={snapshot.Aircraft.Y:F3}m,speed={snapshot.Display?.SpeedKnots}kt");
});
Check("original right roll, gear and recovery are actual source state transitions", () =>
{
    var session = new OriginalFlightSession(package);
    session.Command("air-start");
    session.Command("gear-toggle");
    Run(session, 2, new(0.6, 0, 0, 0.8, false, false));
    var snapshot = session.Snapshot;
    Require(snapshot.Aircraft.Roll < 0 && snapshot.Aircraft.Up.X > 0 && !snapshot.Aircraft.GearDown,
        "Original right-positive input/gear animation was not mapped to source state.");
    Require(session.RawState.Raw.Roll != 0 && snapshot.Display!.SpeedKnots > 0, "Source rates/velocity did not advance.");
    var fuel = snapshot.Aircraft.Fuel;
    session.Command("recover");
    Require(session.Snapshot is { Phase: "ready", Recoveries: 1 } && session.Snapshot.Aircraft.OnGround &&
        Math.Abs(session.Snapshot.Aircraft.Fuel - fuel) < 1e-12, "Recovery did not preserve source fuel.");
});
Check("bounded stalls and malformed controls never substitute authored dynamics", () =>
{
    var a = new OriginalFlightSession(package);
    var b = new OriginalFlightSession(package);
    a.Command("air-start");
    b.Command("air-start");
    a.Advance(100, idle);
    b.Advance(0.25, idle);
    Require(a.RawState == b.RawState && a.Snapshot.Tick == 2, "Source long-frame budget is not bounded to two steps.");
    try { a.Advance(0, new(0, double.NaN, 0, 0, false, false)); }
    catch (ArgumentOutOfRangeException) { return; }
    throw new InvalidOperationException("Non-finite original controls were accepted.");
});
Check("original bridge tour and explicitly enhanced world activity are live and pause-safe", () =>
{
    var session = new OriginalFlightSession(package);
    session.Command("start");
    Run(session, 3, idle);
    var before = session.Snapshot.Activity ?? throw new InvalidOperationException("No enhanced world activity.");
    Run(session, 2, idle);
    var moved = session.Snapshot.Activity!;
    Require(moved.Actors.Any(a => a.Kind == "service-car") && moved.Actors.Any(a => a.Kind == "walker") &&
        moved.Actors.Any(a => a.Kind == "aircraft"), "The world activity has no actual service/crew/air traffic.");
    Require(moved.Actors.Zip(before.Actors).Any(pair => pair.First.X != pair.Second.X || pair.First.Z != pair.Second.Z),
        "Enhanced world actors did not move with the simulation.");
    session.Command("pause");
    var frozen = session.Snapshot;
    session.Advance(12, idle);
    Require(session.Snapshot == frozen, "Paused world activity moved or regenerated a different state.");
    session.Command("landmark-tour");
    var bridge = session.World.SourceWorld!.Objects.First(mesh => mesh.Name == "Original scenery 48");
    var x = (bridge.Vertices.Min(p => p.X) + bridge.Vertices.Max(p => p.X)) / 2;
    var z = (bridge.Vertices.Min(p => p.Z) + bridge.Vertices.Max(p => p.Z)) / 2;
    var aircraft = session.Snapshot.Aircraft;
    var distance = Math.Sqrt(Math.Pow(x - aircraft.X, 2) + Math.Pow(z - aircraft.Z, 2));
    Require(session.Snapshot.Phase == "flying" && distance is > 900 and < 1000 &&
        aircraft.Y > bridge.Vertices.Max(p => p.Y), "Tour did not start above and near the actual original bridge.");
    Require(((x - aircraft.X) * aircraft.Forward.X + (z - aircraft.Z) * aircraft.Forward.Z) / distance > 0.99,
        "Landmark Tour is not actually facing the original bridge.");
    session.Command("garage");
    Require(session.Snapshot.Phase == "ready" && session.Snapshot.Aircraft.OnGround && session.Snapshot.Time == 0,
        "Original flight did not return to the garage/runway without reloading the application.");
    var modern = new FlightSimulation();
    modern.Command("air-start");
    modern.Advance(.1, idle);
    modern.Command("garage");
    Require(modern.Snapshot.Phase == "ready" && modern.Snapshot.Aircraft.OnGround && modern.Snapshot.Time == 0,
        "Modern flight did not return to the garage/runway.");
});
Console.WriteLine($"Original flight session: {passed} passed, {failed} failed; supplied data, reference-compatible profile, not a whole Amiga machine.");
var worldAt = Array.IndexOf(args, "--world");
if (failed == 0 && worldAt >= 0)
{
    if (worldAt + 1 >= args.Length) throw new ArgumentException("--world requires an explicit output path.");
    File.WriteAllText(args[worldAt + 1], JsonSerializer.Serialize(new OriginalFlightSession(package).World,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}
return failed == 0 ? 0 : 1;
