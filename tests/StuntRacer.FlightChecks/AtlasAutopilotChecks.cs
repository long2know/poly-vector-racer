using System.IO.Compression;
using System.Text.Json;
using StuntRacer.Flight;

namespace StuntRacer.FlightChecks;

internal static class AtlasAutopilotChecks
{
    private static readonly FlightInput Idle = new(0, 0, 0, 0, false, false);
    private static readonly Lazy<WorldAtlasBase> Flat = new(() => new(AtlasChecks.Fallback(0)));
    private const double Step = 1.0 / 120;

    public static void CommandsAndWire()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var simulation = Create(179, 2, start: false);
        var model = JsonSerializer.Deserialize<AtlasAutopilotCommand>(
            """{"action":"engage","mode":"route","destination":{"lonDeg":-178,"latDeg":3},"altitudeM":2500,"rate":10}""", json)!;
        simulation.ConfigureAtlasAutopilot(model);
        var ready = simulation.Snapshot;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(ready, json));
        var pilot = wire.RootElement.GetProperty("atlasAutopilot");
        var target = pilot.GetProperty("target");
        var projected = WorldAtlasGrid.Project(-178, 3);
        Check.That(pilot.GetProperty("status").GetString() == "acquiring" &&
            pilot.GetProperty("mode").GetString() == "route" &&
            pilot.GetProperty("requestedRate").GetInt32() == 10 &&
            pilot.GetProperty("effectiveRate").GetInt32() == 1 &&
            pilot.GetProperty("remainingM").GetDouble() is > 300_000 and < 400_000 &&
            pilot.GetProperty("projectedFuel").GetDouble() is > .95 and < 1 &&
            target.GetProperty("x").GetDouble() == projected.X + WorldAtlasGrid.Width &&
            target.GetProperty("z").GetDouble() == projected.Z, "Camel-case Atlas packet carries a fixed short-route target");
        Check.That(pilot.GetProperty("reason").GetString()!.Contains("terrain clearance", StringComparison.Ordinal) &&
            pilot.GetProperty("altitudeM").GetDouble() >= 10_400,
            "A low commanded altitude reports a conservative terrain clamp");
        Check.Throws<JsonException>(() => JsonSerializer.Deserialize<AtlasAutopilotCommand>(
            """{"action":"engage","mode":"route","destination":{"lonDeg":15}}""", json),
            "An absent destination latitude cannot silently become the equator");
        using var unrelated = JsonDocument.Parse(JsonSerializer.Serialize(new FlightSimulation().Snapshot, json));
        Check.That(!unrelated.RootElement.TryGetProperty("atlasAutopilot", out _), "Bay snapshot omits Atlas-only state");
        Check.Throws<InvalidOperationException>(() => new FlightSimulation().ConfigureAtlasAutopilot(model),
            "An unrelated flight mode cannot select Atlas autopilot");

        foreach (var (from, to) in new[] { (0.0, 180.0), (-180.0, 0.0), (-179.0, 1.0), (1.0, -179.0) })
        {
            var antipodal = Create(from);
            antipodal.ConfigureAtlasAutopilot(Route(to, 0));
            Check.Near(antipodal.Snapshot.AtlasAutopilot!.Target!.X - antipodal.Snapshot.Aircraft.X,
                -WorldAtlasGrid.HalfWidth, 1e-8, "An exact antipodal longitude consistently ties to the west");
        }

        AtlasAutopilotCommand[] invalid =
        [
            new("ENGAGE", "hold"), new("engage", "Hold"), new("engage"), new("engage", "route"),
            new("engage", "route", new(181, 0)), new("engage", "route", new(0, double.NaN)),
            new("engage", "route", new(0, -91)), new("engage", "hold", HeadingDeg: -1),
            new("engage", "hold", HeadingDeg: double.PositiveInfinity),
            new("engage", "hold", AltitudeM: 12_001), new("engage", "hold", AltitudeM: double.NaN),
            new("engage", "hold", SpeedMps: 179), new("engage", "hold", SpeedMps: double.NegativeInfinity),
            new("setRate"), new("setRate", Rate: 3), new("setRate", Rate: 2.001), new("setRate", Rate: double.NaN)
        ];
        foreach (var command in invalid)
        {
            Check.Throws<ArgumentException>(() => simulation.ConfigureAtlasAutopilot(command), $"Reject {command}");
            Check.That(simulation.Snapshot == ready, "Rejected command does not mutate the active plan or aircraft");
        }
        Check.That(simulation.AdvanceAtlas(300, Idle, false) == ready, "Ready flight holds its plan without integrating");
        simulation.Command("start");
        Check.That(simulation.AdvanceAtlas(30, Idle, false).Tick == 30, "Queued rate does not accelerate countdown");
        for (var i = 0; i < 11; i++) simulation.AdvanceAtlas(.25, Idle, false);
        var departing = simulation.Snapshot;
        Check.That(departing.Phase == "flying" && departing.Tick == 360 && departing.Aircraft == ready.Aircraft &&
            departing.AtlasAutopilot!.Status == "acquiring", "Countdown completes without moving or refueling the aircraft");
    }

    public static void FixedStepsAndManual()
    {
        var one = Create();
        var ten = Create();
        one.ConfigureAtlasAutopilot(Hold());
        ten.ConfigureAtlasAutopilot(Hold(10));
        var spawn = ten.Snapshot;
        Check.That(ten.AdvanceAtlas(0, Idle, false).Aircraft == spawn.Aircraft, "Engagement cannot change aircraft pose or speed");
        var cruise = Cruise(ten);
        while (one.Snapshot.Tick < cruise.Tick) one.AdvanceAtlas(.25, Idle, false);
        Console.WriteLine($"TRACE Atlas acquisition {FlightScenarios.Describe(cruise)}");
        Check.That(cruise.Aircraft == one.Snapshot.Aircraft && cruise.Tick == one.Snapshot.Tick &&
            !cruise.Aircraft.Afterburner && cruise.Aircraft.Y > spawn.Aircraft.Y + 8000,
            "Climb is actual 1× physics with the same initial conditions");

        var throttleOnly = Idle with { Throttle = 1 };
        for (var i = 0; i < 576; i++) one.AdvanceAtlas(1.0 / 144, throttleOnly, false);
        for (var i = 0; i < 8; i++) ten.AdvanceAtlas(.05, Idle, false);
        var before = ten.Snapshot;
        Check.That(before.Tick == cruise.Tick + 480 && before.Aircraft == one.Snapshot.Aircraft &&
            before.Time == one.Snapshot.Time && before.AtlasAutopilot!.EffectiveRate == 10,
            "1x at 144 Hz and 10x at 20 Hz integrate exactly the same state and fuel; throttle release is not takeover");

        var capped = ten.AdvanceAtlas(double.MaxValue, Idle, false);
        Check.That(capped.Tick == before.Tick + 60 && ten.AdvanceAtlas(0, Idle, false).Tick == capped.Tick,
            "Accelerated stalls are capped at 60 native ticks and never bank extra whole steps");
        one.AdvanceAtlas(.25, Idle, false);
        one.AdvanceAtlas(.25, Idle, false);
        ten.ConfigureAtlasAutopilot(new("setRate", Rate: 2));
        var doubled = ten.AdvanceAtlas(1.0 / 60, Idle, false);
        Check.That(doubled.Tick == capped.Tick + 4 && doubled.AtlasAutopilot is { RequestedRate: 2, EffectiveRate: 2 },
            "Changing selected rate changes actual tick progression without changing dynamics");
        one.AdvanceAtlas(1.0 / 30, Idle, false);
        ten.ConfigureAtlasAutopilot(new("setRate", Rate: 10));
        one.ConfigureAtlasAutopilot(new("takeControl"));
        var manualInput = new FlightInput(.5, 0, 0, .8, false, false);
        var swapped = ten.AdvanceAtlas(2, manualInput, false);
        var reference = one.Advance(.25, manualInput);
        Check.That(swapped.Tick == doubled.Tick + 30 && swapped.Aircraft == reference.Aircraft &&
            swapped.AtlasAutopilot is { Status: "off", EffectiveRate: 1, FuelRateScale: 1 } &&
            swapped.Aircraft.Throttle > doubled.Aircraft.Throttle,
            "Roll cancels before any accelerated tick; unchanged manual physics receives the same throttle");
        var later = ten.AdvanceAtlas(Step, manualInput, false);
        Check.Near(swapped.Aircraft.Fuel - later.Aircraft.Fuel, (.00004 + .00045 * later.Aircraft.Throttle) * Step,
            1e-14, "Manual takeover restores the full dry fuel burn next tick");

        foreach (var (input, explicitIntent) in new (FlightInput, bool)[]
        {
            (Idle with { Pitch = -.2 }, false), (Idle with { Yaw = .1 }, false),
            (Idle with { Brake = true }, false), (Idle with { Afterburner = true }, false), (Idle, true)
        })
        {
            ten.ConfigureAtlasAutopilot(Hold(10));
            var old = ten.Snapshot;
            var canceled = ten.AdvanceAtlas(0, input, explicitIntent);
            Check.That(canceled.AtlasAutopilot is { Status: "off", EffectiveRate: 1 } &&
                canceled.Tick == old.Tick && canceled.Aircraft == old.Aircraft,
                $"Manual input {input} / explicit={explicitIntent} cancels before integrating");
        }
        ten.ConfigureAtlasAutopilot(Hold(10));
        ten.Command("gear-toggle");
        Check.That(ten.Snapshot.Aircraft.GearDown && ten.Snapshot.AtlasAutopilot is { Status: "off" },
            "Gear command itself relinquishes the pilot before moving gear");
    }

    public static void PauseAndFuel()
    {
        var simulation = Create();
        simulation.ConfigureAtlasAutopilot(Hold(10));
        var cruise = Cruise(simulation);
        var fuelStep = simulation.AdvanceAtlas(Step / 10, Idle, false);
        Check.That(fuelStep.Tick == cruise.Tick + 1 && fuelStep.AtlasAutopilot is { FuelRateScale: .01 },
            "Requested rate scales fixed tick count, not the cruise fuel scale");
        Check.Near(cruise.Aircraft.Fuel - fuelStep.Aircraft.Fuel,
            .01 * (.00004 + .00045 * fuelStep.Aircraft.Throttle) * Step, 1e-14,
            "Dry autopilot fuel is exactly 1% of the authored dry burn and is not refilled");
        var half = simulation.AdvanceAtlas(Step / 20, Idle, false);
        Check.That(half.Alpha is > .49 and < .51, "Fixture has an accelerated fractional tick to discard on pause");
        simulation.Command("pause");
        var paused = simulation.Snapshot;
        Check.That(paused.AtlasAutopilot is { EffectiveRate: 1, RequestedRate: 10 } &&
            paused.Tick == half.Tick && paused.Aircraft == half.Aircraft && paused.Alpha == 0,
            "Pause ends compression without touching fuel, time, or pose");
        simulation.Command("gear-toggle");
        Check.That(simulation.AdvanceAtlas(300, Idle, true) == paused, "Paused frames and gear cannot mutate the plane");
        simulation.ConfigureAtlasAutopilot(new("setRate", Rate: 5));
        Check.That(simulation.Snapshot.AtlasAutopilot is { RequestedRate: 5, EffectiveRate: 1 },
            "A paused plan can accept a new desired rate without advancing");
        simulation.Command("resume");
        Check.That(simulation.AdvanceAtlas(Step / 10, Idle, false).Tick == half.Tick,
            "Resume starts with fresh wall time, not the pre-pause accelerated remainder");
        Check.That(simulation.AdvanceAtlas(Step / 10, Idle, false).Tick == half.Tick + 1,
            "Recombining new frames adds exactly one 120 Hz tick");
        var fuel = simulation.Snapshot.Aircraft.Fuel;
        simulation.Command("recover");
        Check.That(simulation.Snapshot is { Phase: "ready", AtlasAutopilot.Status: "off" } &&
            simulation.Snapshot.Aircraft.Fuel == fuel, "Recover releases the plan without refueling");
        simulation.ConfigureAtlasAutopilot(Hold(2));
        simulation.Command("air-start");
        Check.That(simulation.Snapshot is { Phase: "flying", AtlasAutopilot.RequestedRate: 2 },
            "An armed ready plan survives the ordinary scenic air start");
        simulation.Command("restart");
        Check.That(simulation.Snapshot is { Tick: 0, Aircraft.Fuel: 1, AtlasAutopilot.Status: "off" },
            "Only explicit restart resets fuel and plan");
    }

    public static void HoldAndTerrain()
    {
        foreach (var airframe in FlightSimulation.WorldAircraft)
        {
            var simulation = Create(aircraftId: airframe.Id);
            simulation.ConfigureAtlasAutopilot(Hold(5) with { HeadingDeg = 315, AltitudeM = 11_300, SpeedMps = 210 });
            Cruise(simulation);
            for (var i = 0; i < 520; i++) simulation.AdvanceAtlas(.1, Idle, false);
            var held = simulation.Snapshot;
            Check.That(held.AtlasAutopilot is { Status: "cruise", EffectiveRate: 5, RemainingM: null, Target: null } &&
                held.Aircraft is { Y: > 11_297 and < 11_303, Speed: > 209.8 and < 210.2, Afterburner: false } &&
                held.Aircraft.Z > 40_000 && held.Aircraft.Yaw is > -.80 and < -.77,
                $"{airframe.Name} holds commanded altitude, dry speed and northwest heading: {FlightScenarios.Describe(held)}");
        }

        var terrainFlight = Create();
        terrainFlight.ConfigureAtlasAutopilot(Hold(10) with { AltitudeM = 2500 });
        var before = Cruise(terrainFlight);
        var root = new WorldAtlasTileKey(0, 0, 0);
        terrainFlight.World.AtlasElevation!.Install(root, AtlasChecks.Quartet(root, 10_000, 0));
        Check.That(terrainFlight.World.TerrainHeightAt(before.Aircraft.X, before.Aircraft.Z) == 10_000 &&
            terrainFlight.Snapshot == before, "An authored maximum-height detail activation does not move aircraft or change the safe floor");
        var after = terrainFlight.AdvanceAtlas(.05, Idle, false);
        Check.That(after.Tick == before.Tick + 60 && after.Phase == "flying" && after.Aircraft.Y > 10_300 &&
            after.Aircraft.Y - before.Aircraft.Y < 4, "Aircraft still integrates actual clearance above installed Atlas detail");
    }

    public static void LowFuel()
    {
        var simulation = Create();
        simulation.ConfigureAtlasAutopilot(Hold(10));
        Cruise(simulation);
        simulation.ConfigureAtlasAutopilot(new("takeControl"));
        var burner = Idle with { Throttle = 1, Afterburner = true, Assisted = true };
        var snapshot = simulation.Snapshot;
        while (snapshot.Aircraft.Fuel > .022 && snapshot.Time < 800 && snapshot.Phase == "flying")
            snapshot = simulation.AdvanceAtlas(.25, burner, false);
        Check.That(snapshot.Aircraft is { Afterburner: true, Fuel: > 0 and < .025 } && snapshot.Phase == "flying",
            $"Only real manual burner use drained the tank: {FlightScenarios.Describe(snapshot)}");
        var burned = simulation.AdvanceAtlas(Step, burner, false);
        Check.Near(snapshot.Aircraft.Fuel - burned.Aircraft.Fuel,
            (.00004 + .00045 * burned.Aircraft.Throttle + .0019) * Step, 1e-14,
            "Afterburner gets no game endurance allowance");
        simulation.ConfigureAtlasAutopilot(Route(170, 0));
        var low = simulation.Snapshot;
        Check.That(low.AtlasAutopilot is { Status: "acquiring", RequestedRate: 10, EffectiveRate: 1, ProjectedFuel: 0 } pilot &&
            pilot.Reason.Contains("Low fuel", StringComparison.Ordinal) && low.Aircraft == burned.Aircraft,
            "Low fuel remains visible, predicts an empty reserve and denies time compression without refueling");
        var advancing = simulation.AdvanceAtlas(5, Idle, false);
        Check.That(advancing.Tick == low.Tick + 30 && advancing.Aircraft.Fuel < low.Aircraft.Fuel &&
            !advancing.Aircraft.Afterburner, "Low-fuel flight remains live only at 1× and the autopilot never requests afterburner");
    }

    public static void CaptureAndDateLine()
    {
        var simulation = Create(179.4);
        var departure = simulation.Snapshot;
        simulation.ConfigureAtlasAutopilot(Route(-179.7, 0));
        var target = simulation.Snapshot.AtlasAutopilot!.Target!;
        Check.That(target.X > WorldAtlasGrid.HalfWidth, "Eastbound date-line route keeps the nearby unwrapped target");
        var arrived = RunLeg(simulation, 1000, requireDateLine: true, checkTransition: true);
        Check.That(arrived.Aircraft.X < -WorldAtlasGrid.HalfWidth + 100_000 && arrived.Aircraft.X > -WorldAtlasGrid.HalfWidth &&
            arrived.Aircraft.Y is > 10_400 and < 10_600 && arrived.Aircraft.Speed is > 248 and < 252,
            $"Arrival retains dry airborne cruise speed and altitude: {FlightScenarios.Describe(arrived)}");
        var next = simulation.AdvanceAtlas(30, Idle, false);
        Check.That(next.Tick == arrived.Tick + 30 && next.Aircraft.X > arrived.Aircraft.X + 55 &&
            next.AtlasAutopilot is { Status: "arrived", EffectiveRate: 1 } && next.Aircraft.Fuel < arrived.Aircraft.Fuel &&
            next.Landings == departure.Landings, "Arrival keeps a live airborne flight path at 1× without autoland");

        simulation.ConfigureAtlasAutopilot(Route(-179.1, 0));
        Check.That(simulation.Snapshot.AtlasAutopilot is { Target.X: > WorldAtlasGrid.HalfWidth, RemainingM: > 50_000 and < 85_000 },
            "A new target after the seam uses the actual unwrapped flight position");
    }

    public static void PolarHold()
    {
        foreach (var sign in new[] { -1, 1 })
        {
            var simulation = Create(30, sign * 90);
            simulation.ConfigureAtlasAutopilot(Hold(10) with { HeadingDeg = sign > 0 ? 360 : 180 });
            Check.That(simulation.Snapshot.AtlasAutopilot is { HeadingDeg: 90 } pilot &&
                pilot.Reason.Contains("Polar boundary", StringComparison.Ordinal),
                "Outward hold at either pole requests a tangent course and explains why");
            var cruise = Cruise(simulation);
            Check.That(!cruise.Aircraft.Stalled && Math.Abs(cruise.Aircraft.Z) <= WorldAtlasGrid.HalfHeight &&
                cruise.Aircraft.X > WorldAtlasGrid.Project(30, 0).X + 30_000,
                "An eastbound polar departure stays in bounds and climbs without losing horizontal progress");
        }
    }

    public static void PolarRoute()
    {
        foreach (var sign in new[] { -1, 1 })
        {
            var simulation = Create(30, sign * 89);
            simulation.ConfigureAtlasAutopilot(Route(30, sign * 90));
            var arrival = RunLeg(simulation, 1000);
            for (var i = 0; i < 280; i++) simulation.AdvanceAtlas(.25, Idle, false);
            var onward = simulation.Snapshot;
            Check.That(onward.Phase == "flying" && onward.Aircraft.Speed is > 246 and < 254 &&
                Math.Abs(onward.Aircraft.Z) < WorldAtlasGrid.HalfHeight &&
                Math.Abs(onward.Aircraft.X - arrival.Aircraft.X) > 15_000 &&
                onward.AtlasAutopilot is { Status: "arrived", EffectiveRate: 1 }, "Capturing a polar destination leaves room for a safe tangent departure");
        }
    }

    public static void ActualAtlas(string path)
    {
        var fallback = LoadBase(path);

        foreach (var leg in new[]
        {
            (Name: "NYC → London", Lon: -74.006, Lat: 40.713, EndLon: -.128, EndLat: 51.507, East: true, Crosses: false),
            (Name: "London → NYC", Lon: -.128, Lat: 51.507, EndLon: -74.006, EndLat: 40.713, East: false, Crosses: false),
            (Name: "Tokyo → San Francisco", Lon: 139.692, Lat: 35.689, EndLon: -122.419, EndLat: 37.775, East: true, Crosses: true)
        })
        {
            // Start the westbound leg from the Atlas default eastbound departure as well.
            var simulation = Create(leg.Lon, leg.Lat, fallback);
            simulation.ConfigureAtlasAutopilot(Route(leg.EndLon, leg.EndLat));
            var requested = simulation.Snapshot.AtlasAutopilot!;
            var x = simulation.Snapshot.Aircraft.X;
            Check.That(requested.Target is { } target && (target.X > x) == leg.East &&
                Math.Abs(target.X - x) <= WorldAtlasGrid.HalfWidth && requested.ProjectedFuel >= .15,
                $"{leg.Name} chooses the short signed route with a projected reserve");
            var arrived = RunLeg(simulation, requested.RemainingM!.Value / 200 + 900, leg.Crosses);
            Report(leg.Name, arrived.Time, arrived, requested.ProjectedFuel);
        }

        var around = Create(0, 0, fallback);
        around.ConfigureAtlasAutopilot(Hold(10));
        // Hold crosses successive regions, then route waypoints complete the lap on the same fuel load.
        var firstHold = RunEastboundHold(around, 9_000_000, expectedSeamCrossings: 0);
        Report("Equator eastbound hold (9,000 km)", firstHold.Time, firstHold);

        foreach (var longitude in new[] { 160.0, -100.0, 0.06 })
        {
            var start = around.Snapshot;
            around.ConfigureAtlasAutopilot(Route(longitude, 0));
            var plan = around.Snapshot.AtlasAutopilot!;
            Check.That(plan.ProjectedFuel >= .15, "Each lap waypoint projects adequate fuel on the same initial tank");
            var arrived = RunLeg(around, start.Time + plan.RemainingM!.Value / 200 + 900, longitude == -100);
            Report($"Equator route to {longitude}°", arrived.Time - start.Time, arrived, plan.ProjectedFuel);
        }
        var end = around.Snapshot;
        Check.That(end.AtlasAutopilot is { Target.X: > WorldAtlasGrid.Width, Status: "arrived" } &&
            end.Time is > 155_000 and < 165_000 && Math.Abs(end.Aircraft.Z) < 5000,
            "The same aircraft reaches a full circumference eastbound in equirectangular metres");
        Report("Equator circumnavigation total", end.Time, end);

        around.ConfigureAtlasAutopilot(Hold(10));
        var secondLap = RunEastboundHold(around, WorldAtlasGrid.Width, expectedSeamCrossings: 1);
        Report("Equator hold-only second lap", secondLap.Time - end.Time, secondLap);
        around.ConfigureAtlasAutopilot(Route(.3, 0));
        Check.That(around.Snapshot.AtlasAutopilot is { Target.X: > 2 * WorldAtlasGrid.Width, RemainingM: < 40_000 },
            "Retargeting after multiple laps still selects the nearest unwrapped copy");
    }

    private static FlightSnapshot RunEastboundHold(FlightSimulation simulation, double distance, int expectedSeamCrossings)
    {
        var previous = simulation.Snapshot;
        var start = previous;
        var eastward = 0.0;
        var seams = 0;
        var limit = start.Time + distance / 200 + 900;
        while (eastward < distance && previous.Time < limit && previous.Phase == "flying")
        {
            var next = simulation.AdvanceAtlas(.25, Idle, false);
            var delta = next.Aircraft.X - previous.Aircraft.X;
            if (Math.Abs(delta) > WorldAtlasGrid.HalfWidth) seams++;
            eastward += WorldAtlasGrid.Wrap(delta);
            previous = next;
        }
        Check.That(eastward >= distance && seams == expectedSeamCrossings &&
            Math.Abs(previous.Aircraft.Z) < 5000 && previous.Aircraft.Fuel >= .15 &&
            previous.Aircraft.Fuel < start.Aircraft.Fuel && previous.AtlasAutopilot is { Status: "cruise", EffectiveRate: 10 },
            $"Eastbound hold follows the real equator with no boundary or fuel reset: {FlightScenarios.Describe(previous)}");
        return previous;
    }

    private static WorldAtlasBase LoadBase(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return ReadBase(stream, stream.Length);
        }

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("base.bin") ?? throw new InvalidDataException("Atlas base archive is missing base.bin.");
        using var zipped = entry.Open();
        return ReadBase(zipped, entry.Length);
    }

    private static WorldAtlasBase ReadBase(Stream stream, long length)
    {
        if (length != WorldAtlasBase.ByteLength) throw new InvalidDataException("Unexpected WFA1 length.");
        var bytes = new byte[WorldAtlasBase.ByteLength];
        stream.ReadExactly(bytes);
        return new(bytes);
    }

    private static FlightSimulation Create(double lon = 0, double lat = 0, WorldAtlasBase? fallback = null,
        bool start = true, string aircraftId = "f18")
    {
        var simulation = new FlightSimulation(FlightWorldDefinition.CreateAtlas(
            new("native-test", "Native Atlas test", lon, lat), new(fallback ?? Flat.Value)), aircraftId);
        if (start) simulation.Command("air-start");
        return simulation;
    }

    private static AtlasAutopilotCommand Hold(double rate = 1) => new("engage", "hold", HeadingDeg: 90, Rate: rate);
    private static AtlasAutopilotCommand Route(double lon, double lat) => new("engage", "route", new(lon, lat), Rate: 10);

    private static FlightSnapshot Cruise(FlightSimulation simulation)
    {
        var snapshot = simulation.Snapshot;
        while (snapshot.AtlasAutopilot!.Status != "cruise" && snapshot.Phase == "flying" && snapshot.Time < 800)
            snapshot = simulation.AdvanceAtlas(.25, Idle, false);
        Check.That(snapshot.AtlasAutopilot!.Status == "cruise",
            $"The real airframe must acquire dry cruise: {FlightScenarios.Describe(snapshot)} / {snapshot.AtlasAutopilot}");
        return snapshot;
    }

    private static FlightSnapshot RunLeg(FlightSimulation simulation, double timeLimit, bool requireDateLine = false, bool checkTransition = false)
    {
        var previous = simulation.Snapshot;
        var crossed = false;
        var compressed = false;
        var droppedApproachRemainder = false;
        while (previous.Phase == "flying" && previous.AtlasAutopilot!.Status != "arrived" && previous.Time < timeLimit)
        {
            var next = simulation.AdvanceAtlas(.25, Idle, false);
            var ticks = next.Tick - previous.Tick;
            var wasCompressed = previous.AtlasAutopilot.EffectiveRate > 1;
            if (ticks > 30) compressed = true;
            Check.That(ticks > 0 && ticks <= (wasCompressed ? 60 : 30), "Advance respects the current native tick budget");
            var dx = WorldAtlasGrid.Wrap(next.Aircraft.X - previous.Aircraft.X);
            var dy = next.Aircraft.Y - previous.Aircraft.Y;
            var dz = next.Aircraft.Z - previous.Aircraft.Z;
            var displacement = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            Check.That(displacement <= (Math.Max(next.Aircraft.Speed, previous.Aircraft.Speed) + 10) * ticks * Step + 1e-6,
                "Each simulated displacement is physically reachable with no position snap, including at the seam");
            crossed |= Math.Abs(next.Aircraft.X - previous.Aircraft.X) > WorldAtlasGrid.HalfWidth;
            if (checkTransition && wasCompressed && next.AtlasAutopilot is { EffectiveRate: 1, RemainingM: < 25_000 })
            {
                Check.That(next.Alpha == 0 && simulation.AdvanceAtlas(0, Idle, false).Tick == next.Tick,
                    "Crossing the capture approach drops unused compressed time immediately");
                droppedApproachRemainder = true;
            }
            if (next.AtlasAutopilot is { Status: "arrived" })
                Check.That(!wasCompressed && ticks <= 30, "Arrival must occur on a 1× physics frame");
            previous = next;
        }
        var arrived = previous.AtlasAutopilot!;
        Check.That(previous.Phase == "flying" && !previous.Aircraft.OnGround && !previous.Aircraft.Afterburner &&
            compressed && (!requireDateLine || crossed) && (!checkTransition || droppedApproachRemainder) &&
            arrived is { Status: "arrived", EffectiveRate: 1, RemainingM: 0, Target: not null },
            $"Route should safely arrive after true cruise: {FlightScenarios.Describe(previous)} / {arrived}");
        var x = WorldAtlasGrid.Wrap(arrived.Target!.X - previous.Aircraft.X);
        var z = arrived.Target.Z - previous.Aircraft.Z;
        Check.That(Math.Sqrt(x * x + z * z) <= 5000 && previous.Aircraft.Fuel >= .15,
            "Physical aircraft is inside the 5 km arrival circle with at least 15% fuel remaining");
        return previous;
    }

    private static void Report(string name, double time, FlightSnapshot snapshot, double? projected = null)
    {
        if (projected is { } predicted)
        {
            Check.That(predicted >= .15 && Math.Abs(snapshot.Aircraft.Fuel - predicted) < .012,
                $"{name} reserve projection {predicted:P3} vs actual {snapshot.Aircraft.Fuel:P3}");
        }
        Console.WriteLine($"ATLAS {name}: simulated {time:F3} s ({time / 3600:F3} h); " +
            $"fuel {snapshot.Aircraft.Fuel:P3}" + (projected is { } p ? $" (projected {p:P3})" : "") +
            $"; {snapshot.Aircraft.Speed:F2} m/s at {snapshot.Aircraft.Y:F1} m");
    }
}
