using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StuntRacer.FlightReference;

namespace StuntRacer.FlightReferenceChecks;

internal static class PlayerOracleProof
{
    private sealed record Case(string Name, int Stage, bool Chain, FlightPlayerState Before,
        FlightPlayerState Expected, FlightInput Input, FlightEnvironment Environment);
    private static readonly (string Name, Func<FlightPlayerState, long> Read)[] Fields = MakeFields();

    private static (string Name, Func<FlightPlayerState, long> Read)[] MakeFields()
    {
        var fields = OracleProof.Fields.Select(f =>
            (f.Name, (Func<FlightPlayerState, long>)(s => f.Read(s.Raw)))).ToList();
        for (var matrix = 0; matrix < 2; matrix++)
            for (var element = 0; element < 9; element++)
            {
                var which = matrix; var index = element;
                fields.Add(($"Matrix{(which == 0 ? "A" : "B")}[{index}]",
                    s => which == 0 ? s.MatrixA[index] : s.MatrixB[index]));
            }
        for (var vertex = 0; vertex < 4; vertex++)
            for (var axis = 0; axis < 3; axis++)
            {
                var slot = vertex; var coordinate = axis;
                fields.Add(($"Vertex{slot}[{coordinate}]", s =>
                {
                    var v = slot switch { 0 => s.Vertex0, 1 => s.Vertex1, 2 => s.Vertex2, _ => s.Vertex3 };
                    return coordinate == 0 ? v.X : coordinate == 1 ? v.Y : v.Z;
                }));
            }
        string[] names =
        [
            "RollSine","RollCosine","TurnState","TurnSign","TurnFollower","TurnLimit","CoupledPitch","DeckHeight",
            "AircraftState","LiftOffBits","ModelPartId","ModelUnitShift","Group","BankTurnEnabled","FreezeSurfaces",
            "ExternalView","CollisionsEnabled","Immune","Paused","ScaledMotion","ScaledHorizontal","LandingFlags",
            "MessageCode","ContactFlag","CrashHandover","CrashView","GearSound","GearWarningLatch","CrashCount",
            "EventLiftOff","EventTouchdown","EventCrash","EventJolt","EventRearm","EventBump","EventBeep",
            "StepCount","PanelSpeed","PanelAltitude","DynamicReadCount"
        ];
        foreach (var name in names)
        {
            var property = typeof(FlightPlayerState).GetProperty(name)!;
            fields.Add((name, s => property.GetValue(s) is bool value ? value ? 1 : 0 : Convert.ToInt64(property.GetValue(s), CultureInfo.InvariantCulture)));
        }
        return fields.ToArray();
    }

    internal static void Run(string executable, string directory)
    {
        executable = Path.GetFullPath(executable);
        directory = Path.GetFullPath(directory);
        var flatBytes = File.ReadAllBytes(Path.Combine(directory, "flat-image.bin"));
        var referenceBytes = File.ReadAllBytes(Path.Combine(directory, "reference-window-image.bin"));
        var flatHash = Convert.ToHexStringLower(SHA256.HashData(flatBytes));
        var referenceHash = Convert.ToHexStringLower(SHA256.HashData(referenceBytes));
        Program.Equal("40388db2b579abf89048a844b5502b79e8818c26b9253eba83705529f32a35a2", flatHash, "supplied x1 flat image");
        Program.Equal("46522e1f13bc01d9bb0fbf09554d3f9b6d89bcd7cba6e21a2981ff212fda4ca0", referenceHash, "reference numeric windows");
        var identity = new FlightDataIdentity(
            "38b6e1f331460aa4939690173d95c38b105d1d089226359a4c8655052ea29ab5",
            flatHash, referenceHash, "user-supplied renamed x1; cracked/modified archive, not stock-certified",
            "pinned segments.txt target RAM placements; 340 numeric-window operand relocations");
        var data = FlightOriginalData.FromImages(flatBytes, referenceBytes, identity);
        var kernel = new FlightPlayerKernel(data);
        var storedScalars = typeof(FlightPlayerState).GetProperties().Where(p => p.SetMethod != null)
            .Sum(p => p.PropertyType == typeof(FlightRawState) ? OracleProof.Fields.Length
                : p.PropertyType == typeof(FlightMatrix) ? 9 : p.PropertyType == typeof(RawVector) ? 3 : 1);
        Program.Equal(storedScalars, Fields.Length, "every stored player scalar must be compared");
        FlightPlayerKernel.RequireStage(ReferenceStages.FullPlayerStep);
        using var scene = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "scene.json")));
        RawVector[] Model(int id, int count) => scene.RootElement.GetProperty("models").EnumerateArray()
            .First(m => m.GetProperty("id").GetInt32() == id).GetProperty("levels")[0].GetProperty("vertices").EnumerateArray()
            .Where(v => v.GetProperty("slot").GetInt32() < count)
            .OrderBy(v => v.GetProperty("slot").GetInt32())
            .Take(count).Select(v => new RawVector(v.GetProperty("x").GetInt16(), v.GetProperty("y").GetInt16(), v.GetProperty("z").GetInt16())).ToArray();
        var model = new FlightPlayerModel(Model(4, 4), 3);
        var shipModel = Model(8, 58);
        Program.Equal(58, shipModel.Length, "actual original carrier vertices");
        var environment = new FlightEnvironment(model, null, [], 48);
        var words = scene.RootElement.GetProperty("starts")[0].GetProperty("words").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        short FlatWord(int address) { var at = address - 0x10000; return unchecked((short)((flatBytes[at] << 8) | flatBytes[at + 1])); }
        var startX = unchecked((words[0] << 24) + (FlatWord(0x206f2 + words[2] * 4) + words[3]) * 1024 + words[5] * 16);
        var startZ = unchecked((words[1] << 24) + (FlatWord(0x206f4 + words[2] * 4) + words[4]) * 1024 + words[6] * 16);
        var startHeading = (short)(words[7] * 80);
        FlightPlayerState Air(short pitch = 0, short heading = 0, short roll = 0) =>
            kernel.CreatePlayer(Program.Air() with
            {
                Fuel = 6400000, Throttle = 60, TargetSpeed = 4000, IndicatedSpeed = 4000,
                Position = new(startX, 1000000, startZ), GustAndSinkEnabled = true,
                Pitch = pitch, Heading = heading, Roll = roll, SimulationFlags = 0x840
            }, environment);
        FlightPlayerState Ground() => kernel.CreatePlayer(Program.Air() with
        {
            Fuel = 6400000, Throttle = 9, TargetSpeed = 0, IndicatedSpeed = 0,
            Position = new(startX, 1800, startZ), Configuration = 0x80, Surface = 4,
            Heading = startHeading, GearEjection = 0, GustAndSinkEnabled = true, SimulationFlags = 0x840
        }, environment);
        FlightCarrier Ship(RawPosition position, short heading)
        {
            var matrix = kernel.BuildInverseAttitudeMatrix(0, heading, 0);
            return new(FlightPlayerKernel.RotateContactVertices(shipModel, matrix))
            {
                Status = 0x404c, AircraftClass = 0x20, Group = 14, UnitShift = 3,
                Position = position, MatrixB = matrix, Heading = heading
            };
        }
        var cases = new List<Case>();
        short[] pitches = [0, 8, 400, 2400, 6000, 7192, 7200, 7208, 10000, 14392, 14400, 14408, 18000, 21592, 21600, 21608, 24000, 28000, 28792, -3, -38];
        short[] headings = [0, 2240, 7200, 14392, 14400, 21600, 28600];
        short[] rolls = [0, 2400, 7192, 7200, 14400, 21600, 28000];
        for (var i = 0; i < 147; i++)
        {
            var before = Air(pitches[i % pitches.Length], headings[i % headings.Length], rolls[(i * 3) % rolls.Length]);
            cases.Add(new($"matrix-A {i}", 1, false, before, before with { MatrixA = kernel.BuildMatrixA(before.Raw.Pitch, before.Raw.Heading, before.Raw.Roll) }, default, environment));
            cases.Add(new($"matrix-B {i}", 2, false, before, before with { MatrixB = kernel.BuildMatrixB(before.Raw.Pitch, before.Raw.Heading, before.Raw.Roll) }, default, environment));
        }
        for (var i = 0; i < 196; i++)
        {
            var before = Air(pitches[i % 19], headings[(i * 3) % headings.Length], rolls[(i * 5) % rolls.Length]);
            before.Raw = before.Raw with { PitchRate = (short)(i % 7 * 91 - 273), HeadingRate = (short)(i % 11 * 67 - 335), RollRate = (short)(i % 13 * 83 - 498) };
            var angles = kernel.ComposeAttitude(before.MatrixA, before.Raw.PitchRate, before.Raw.HeadingRate, before.Raw.RollRate);
            var expected = before with { Raw = before.Raw with { Pitch = angles.Pitch, Heading = angles.Heading, Roll = angles.Roll } };
            cases.Add(new($"composition {i}", 3, false, before, expected, default, environment));
        }
        for (var i = 0; i < 420; i++)
        {
            var before = Air(pitches[i % pitches.Length], headings[i % headings.Length], rolls[i % rolls.Length]);
            before.Raw = before.Raw with
            {
                PitchCommand = (sbyte)(i % 5 * 10 - 20), RudderCommand = (sbyte)(i % 3 * 20 - 20), RollCommand = (sbyte)(i % 5 * 30 - 60),
                TargetSpeed = (short)(i % 3 == 0 ? 1200 : 6000), IndicatedSpeed = (short)(i % 3 == 0 ? 1200 : 6000),
                Configuration = (ushort)(i % 7 == 0 ? 0x80 : i % 11 == 0 ? 0x10 : 0),
                GroundTerm = (short)(i % 13 == 0 ? -37 : 0), Dynamics = (byte)(i % 19 == 0 ? 2 : 0)
            };
            before.TurnFollower = (short)(i % 5 * 1500 - 3000);
            before.TurnState = (short)(i % 2 == 0 ? 0 : -1);
            before.TurnSign = (short)(i % 3 == 0 ? 0 : -1);
            before.FreezeSurfaces = i % 17 == 0;
            before.BankTurnEnabled = i % 23 != 0;
            before.AircraftState = (byte)(i % 31 == 0 ? 10 : 0);
            cases.Add(new($"attitude/stall/bank {i}", 4, false, before, kernel.StepAttitude(before, environment), default, environment));
        }
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var before = Air(7200);
            before.MatrixA = kernel.BuildMatrixA(0, 7200, 0);
            before.BankTurnEnabled = false;
            before.Raw = before.Raw with { PitchCommand = 1, Configuration = (ushort)(repeat * 4) };
            var after = kernel.StepAttitude(before, environment);
            Program.Equal(4, after.Raw.Configuration & 4, "gimbal guard must abandon the inconsistent pole composition");
            cases.Add(new($"gimbal guard kick {repeat}", 4, false, before, after, default, environment));
        }
        var random = new Random(198818);
        for (var i = 0; i < 160; i++)
        {
            var before = Ground();
            before.Raw = before.Raw with { Position = new(random.Next(0x20000000), 1800, random.Next(0x20000000)), Surface = 0x38 };
            var expected = before with { Raw = before.Raw with { Surface = kernel.QueryTerrain(before.Raw.Position.X, before.Raw.Position.Z, before.Raw.Surface) } };
            cases.Add(new($"original terrain {i}", 6, false, before, expected, default, environment));
        }
        foreach (var offset in new[] { -768, -1, 0, 1, 768, 4096 })
        {
            var before = Ground();
            before.Raw = before.Raw with { Position = new(startX + offset, 1800, startZ - offset) };
            cases.Add(new($"original runway/pad {offset}", 6, false, before,
                before with { Raw = before.Raw with { Surface = kernel.QueryTerrain(before.Raw.Position.X, before.Raw.Position.Z, before.Raw.Surface) } }, default, environment));
        }
        var summaries = new List<string>();
        void Trajectory(string name, FlightPlayerState initial, FlightEnvironment env, int steps, Func<int, FlightInput> input,
            Action<FlightPlayerState[]>? validate = null)
        {
            var states = new FlightPlayerState[steps];
            var state = initial;
            for (var frame = 0; frame < steps; frame++)
            {
                var before = state;
                var command = input(frame);
                state = kernel.StepPlayer(state, command, env);
                states[frame] = state;
                cases.Add(new($"{name} frame={frame + 1}", 8, frame != 0, before, state, command, env));
            }
            validate?.Invoke(states);
            summaries.Add($"{name}: {steps} steps; final pos={state.Raw.Position}, S/W={state.Raw.TargetSpeed}/{state.Raw.IndicatedSpeed}, flags=0x{state.Raw.Status:x4}/0x{state.Raw.Configuration:x4}, events lift/touch/crash/rearm={state.EventLiftOff}/{state.EventTouchdown}/{state.EventCrash}/{state.EventRearm}");
        }
        static void Require(bool good, string description) { if (!good) throw new InvalidOperationException(description); }
        Trajectory("runway idle", Ground(), environment, 24, _ => default,
            states => Require(states.All(s => s.Raw.OnGround && s.Raw.Position.Y == 1800), "Idle runway did not remain grounded."));
        Trajectory("runway throttle then takeoff", Ground(), environment, 220,
            f => new((f < 45 ? DigitalKeys.ThrottleUp : 0) | (f is >= 120 and < 151 ? DigitalKeys.StickBack : 0)),
            states => Require(states.Any(s => s.EventLiftOff != 0 && !s.Raw.OnGround), "Original takeoff never lifted off."));
        Trajectory("banked manoeuvre and gear", Air(28000, startHeading, 0), environment, 180,
            f => new((f % 90 < 35 ? DigitalKeys.RollRight : 0) | (f % 80 < 12 ? DigitalKeys.StickBack : 0), ToggleGear: f is 25 or 90));
        var soft = Air(0, startHeading, 0);
        soft.Raw = soft.Raw with { Position = new(startX, 3000, startZ), Throttle = 0, TargetSpeed = 2200, IndicatedSpeed = 2200, GearEjection = 0, Displacement = new(0, -400, 0) };
        soft.LandingFlags = 0x100;
        Trajectory("soft landing and stop/rearm", soft, environment, 220, _ => default,
            states => Require(states.Any(s => s.EventTouchdown != 0 && (s.Raw.Status & 0x200) == 0) && states[^1].EventRearm != 0, "Original soft landing/rearm did not complete."));
        var hard = Air(2000, startHeading, 0);
        hard.Raw = hard.Raw with { Position = new(startX, 3500, startZ), Throttle = 0, GearEjection = 0, Displacement = new(0, -4000, 0) };
        hard.ExternalView = true;
        hard.ModelPartId = 0;
        Trajectory("hard touchdown/external expiry", hard, environment, 40, _ => default,
            states => Require(states.Any(s => s.EventCrash != 0), "Hard touchdown did not produce the real crash event."));
        var stall = Air();
        stall.Raw = stall.Raw with { Position = new(startX, 20000, startZ), Throttle = 0, TargetSpeed = 1700, IndicatedSpeed = 1700, GearEjection = 0 };
        Trajectory("low-speed stall/descent", stall, environment, 90, _ => default,
            states => Require(states.Any(s => (s.Raw.Dynamics & 4) != 0), "Original low-speed stall flag never appeared."));
        var ship = Ship(new(295174144, 0, 274202624), 0);
        var deckEnv = new FlightEnvironment(model, ship, [], 48) { SuppressRearm = true };
        var deck = Ground();
        deck.Raw = deck.Raw with { Position = new(ship.Position.X + 11264, 30472, ship.Position.Z + 106496), Heading = 0, Configuration = 0x82, Surface = 0xc8 };
        deck = kernel.SetAttitude(deck, 0, 0, 0);
        deck.Raw = FlightReferenceKernel.RefreshPositionCells(deck.Raw);
        deck.DeckHeight = 112;
        Trajectory("original carrier catapult", deck, deckEnv, 100, f => new(f < 45 ? DigitalKeys.ThrottleUp : 0),
            states => Require(states.Any(s => s.EventLiftOff != 0), "Catapult never left the real deck."));
        var demoShip = Ship(new(286785536, 0, 286785536), 28600);
        var arrestEnv = new FlightEnvironment(model, demoShip, [], 48) { SuppressRearm = true };
        var arrest = Air(0, 28600, 0);
        arrest.Raw = arrest.Raw with { Position = new(demoShip.Position.X, 37472, demoShip.Position.Z - 260000), Configuration = 0x8000, Throttle = 40, TargetSpeed = 1700, IndicatedSpeed = 1700, GearEjection = 0 };
        arrest.Raw = FlightReferenceKernel.RefreshPositionCells(arrest.Raw);
        Trajectory("original arrestor approach/catch", arrest, arrestEnv, 100, _ => default,
            states => Require(states.Any(s => (s.Raw.Configuration & 0xc000) == 0xc000) && states[^1].Raw.TargetSpeed == 0, "The original arrestor did not catch and stop."));
        var brake = Ground();
        brake.Raw = brake.Raw with { Throttle = 0, TargetSpeed = 3000, IndicatedSpeed = 3000 };
        Trajectory("wheel-brake stop", brake, environment, 110, f => new(0, ToggleBrake: f == 0),
            states => Require(states[^1].Raw.TargetSpeed == 0, "Wheel brakes did not stop."));
        var paused = Air();
        paused.Paused = true;
        paused.Raw = paused.Raw with { Configuration = 1, Status = 0x11ca, TargetSpeed = 6000, GearEjection = 0 };
        Trajectory("paused key/gear-warning presteps", paused, environment, 8, f => new(0, ToggleHook: f is 0 or 4),
            states => Require(states.All(s => s.StepCount == 0 && s.Raw.Counter == paused.Raw.Counter), "Pause advanced the player clock/countdown."));
        var frozen = Air(2400, 2240, 4000);
        frozen.FreezeSurfaces = true;
        Trajectory("frozen integration preserves surface state", frozen, environment, 30, _ => new(DigitalKeys.StickBack | DigitalKeys.RollRight));
        Trajectory("hook lamp and gear transitions", Air(28000, 0, 0), environment, 40,
            f => new(0, ToggleGear: f is 0 or 20, ToggleHook: f is 0 or 15));
        var exhausted = Air();
        exhausted.Raw = exhausted.Raw with { Fuel = 1, Throttle = 120, Position = new(startX, 80000, startZ) };
        Trajectory("fuel exhaustion gliding", exhausted, environment, 100, _ => default,
            states => Require(states[^1].Raw.Fuel == 0 && states[^1].Raw.Throttle == 0, "Fuel exhaustion did not stop thrust."));
        var ceiling = Air(28000);
        ceiling.Raw = ceiling.Raw with { Position = new(startX, 0x7ffff0, startZ) };
        Trajectory("original altitude ceiling", ceiling, environment, 12, _ => default,
            states => Require(states.Any(s => s.Raw.Position.Y == s.Raw.AltitudeCeiling), "Source ceiling was not applied."));
        RawPosition water = default;
        for (var i = 0; i < 300; i++)
        {
            var candidate = new RawPosition(random.Next(0x20000000), 2000, random.Next(0x20000000));
            if ((kernel.QueryTerrain(candidate.X, candidate.Z, 0) & 2) != 0) { water = candidate; break; }
        }
        Require(water.Y != 0, "No actual original water polygon was sampled.");
        var ditch = Air();
        ditch.Raw = ditch.Raw with { Position = water, GearEjection = 0, Throttle = 0, TargetSpeed = 1800, IndicatedSpeed = 1800, Displacement = new(0, -400, 0) };
        Trajectory("actual water gear loss/ditching", ditch, environment, 45, _ => default,
            states => Require(states.Any(s => (s.Raw.GearEjection & 0x80) != 0 && (s.Raw.Dynamics & 1) != 0), "Water touchdown did not lose gear/set skid."));

        var hullHits = 0;
        foreach (var dx in new[] { -10000, 0, 10000 })
        foreach (var dz in new[] { -100000, 0, 100000 })
        {
            var before = Air();
            before.Raw = before.Raw with
            {
                Position = new(ship.Position.X + dx, 10000, ship.Position.Z + dz),
                SimulationFlags = 0, TargetSpeed = 1500, IndicatedSpeed = 1500
            };
            before.Raw = FlightReferenceKernel.RefreshPositionCells(before.Raw);
            before.ExternalView = true;
            var after = kernel.StepPlayer(before, default, deckEnv);
            if (after.EventCrash != 0) hullHits++;
            cases.Add(new($"actual carrier hull x={dx} z={dz}", 8, false, before, after, default, deckEnv));
        }
        Require(hullHits > 0, "The original hull-volume cases never made contact.");

        var footprints = scene.RootElement.GetProperty("modelCollisions").EnumerateArray().ToArray();
        Program.Equal(13, footprints.Length, "all genuine original static footprint definitions");
        FlightContactBox Box(JsonElement box) => new(
            box.GetProperty("maximumHeight").GetInt16(),
            (short)(box.GetProperty("minimumX").GetInt16() + 8192),
            (short)(box.GetProperty("maximumX").GetInt16() + 8192),
            (short)(box.GetProperty("minimumZ").GetInt16() + 8192),
            (short)(box.GetProperty("maximumZ").GetInt16() + 8192));
        var landmarkCases = 0;
        var landmarkHits = 0;
        foreach (var footprint in footprints)
        {
            var nodes = footprint.GetProperty("nodes").EnumerateArray().Select(node =>
                new FlightLandmarkNode(Box(node.GetProperty("box")),
                    node.GetProperty("planes").EnumerateArray().Select(plane => new FlightContactPlane(
                        new((short)(plane.GetProperty("referenceX").GetInt16() + 8192), plane.GetProperty("referenceY").GetInt16(),
                            (short)(plane.GetProperty("referenceZ").GetInt16() + 8192)),
                        new(plane.GetProperty("normalX").GetInt16(), plane.GetProperty("normalY").GetInt16(), plane.GetProperty("normalZ").GetInt16()))).ToArray())).ToArray();
            var landmark = new FlightLandmark(64, 64, Box(footprint.GetProperty("outerBox")), nodes);
            var lmEnv = new FlightEnvironment(model, null, [landmark], footprint.GetProperty("referenceLandmarkZBias").GetInt32());
            foreach (var node in nodes)
            foreach (var boundary in new[] { 0, 1, 2, 3 })
            foreach (var previousContact in new[] { false, true })
            foreach (var speed in new short[] { 500, 1500 })
            {
                var b = node.Box;
                var x = boundary == 1 ? b.MinimumX : boundary == 2 ? b.MaximumX : (b.MinimumX + b.MaximumX) / 2;
                var z = (b.MinimumZ + b.MaximumZ) / 2;
                var y = boundary == 3 ? b.MaximumY : Math.Max(1, b.MaximumY / 2);
                var before = Air();
                before.Raw = before.Raw with
                {
                    Position = new((64 << 22) + (x << 8), y << 8, (64 << 22) + ((z - lmEnv.LandmarkZBias) << 8)),
                    SimulationFlags = 0, Configuration = (ushort)(previousContact ? 2 : 0),
                    TargetSpeed = speed, IndicatedSpeed = speed
                };
                before.Raw = FlightReferenceKernel.RefreshPositionCells(before.Raw);
                var after = kernel.StepPlayer(before, default, lmEnv);
                landmarkCases++;
                if ((after.Raw.Status & 0x8000) != 0) landmarkHits++;
                cases.Add(new($"original footprint model={footprint.GetProperty("modelId").GetInt32()} boundary={boundary} oldContact={previousContact} W={speed}",
                    8, false, before, after, default, lmEnv));
            }
        }
        Require(landmarkHits > 0, "Original footprint-plane cases never hit.");
        summaries.Add($"scenery contacts: {hullHits}/9 real hull hits; {landmarkHits}/{landmarkCases} actual original footprint hits, all13 definitions/14nodes/66planes supplied");

        void Unsupported(Action operation, string description)
        {
            try { operation(); }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException($"Unsupported path silently succeeded: {description}");
        }
        Unsupported(() => kernel.StepPlayer(Air(), new(0, Eject: true), environment), "ejection");
        Unsupported(() => kernel.StepPlayer(Air() with { ScaledMotion = 1 }, default, environment), "unused scaled motion");
        Unsupported(() => FlightPlayerKernel.RequireStage(ReferenceStages.Missions), "mission dispatcher");
        var dynamicWord = Air();
        dynamicWord.Raw = dynamicWord.Raw with { Pitch = -2912 };
        Unsupported(() => kernel.StepPlayer(dynamicWord, default, environment), "unavailable live runtime text");
        var dynamicEnv = new FlightEnvironment(model, null, [], 48) { RuntimeWord = address => address == 0x440c8 ? (short)0 : null };
        Require(kernel.StepPlayer(dynamicWord, default, dynamicEnv).DynamicReadCount == 1, "Explicit runtime resolver was not consumed/counted.");

        var lines = Execute(executable, directory, cases, shipModel);
        Program.Equal(cases.Count, lines.Length, "full-player native result count");
        long differences = 0, badCases = 0, maxError = 0;
        var examples = new HashSet<string>();
        var counts = new SortedDictionary<int, int>();
        var fieldCounts = new SortedDictionary<string, int>();
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var tokens = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Program.Equal(Fields.Length + 1, tokens.Length, $"full-player native field count {i}");
            Program.Equal(i, int.Parse(tokens[0], CultureInfo.InvariantCulture), "full-player ordinal");
            var bad = false;
            for (var f = 0; f < Fields.Length; f++)
            {
                var actual = long.Parse(tokens[f + 1], CultureInfo.InvariantCulture);
                var expected = Fields[f].Read(c.Expected);
                if (actual == expected) continue;
                bad = true; differences++; maxError = Math.Max(maxError, Math.Abs(actual - expected));
                var field = $"{c.Stage}/{Fields[f].Name}";
                fieldCounts[field] = fieldCounts.GetValueOrDefault(field) + 1;
                if (examples.Add(field))
                    Console.Error.WriteLine($"PLAYER DIFF {c.Name} {Fields[f].Name}: C#={expected}, pinned-C={actual}");
            }
            if (bad) badCases++;
            counts[c.Stage] = counts.GetValueOrDefault(c.Stage) + 1;
        }
        foreach (var pair in counts) Console.WriteLine($"PLAYER stage={pair.Key} cases={pair.Value}");
        foreach (var pair in fieldCounts) Console.Error.WriteLine($"PLAYER differing {pair.Key}: {pair.Value}");
        foreach (var summary in summaries) Console.WriteLine($"TRAJECTORY {summary}");
        Console.WriteLine($"PLAYER ORIGINAL exe={identity.ExecutableSha256}; flat={flatHash}; reference={referenceHash}");
        Console.WriteLine($"PLAYER fields={Fields.Length}; cases={cases.Count}; comparisons={(long)Fields.Length * cases.Count}; bad-cases={badCases}; differing-fields={differences}; max-error={maxError}");
        Console.WriteLine("PLAYER proof uses real supplied x1 tables/model contacts and independent retained native trajectories, not reseeded C# outputs.");
        if (badCases != 0) throw new InvalidOperationException($"Original-data full-player differential has {badCases} mismatched cases.");
    }

    private static string[] Execute(string executable, string directory, List<Case> cases, RawVector[] sourceCarrierVertices)
    {
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(directory);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using (var writer = process.StandardInput)
        {
            void N(long value) { writer.Write(value.ToString(CultureInfo.InvariantCulture)); writer.Write(' '); }
            void V(RawVector value) { N(value.X); N(value.Y); N(value.Z); }
            void Box(FlightContactBox value) { N(value.MaximumY); N(value.MinimumX); N(value.MaximumX); N(value.MinimumZ); N(value.MaximumZ); }
            N(cases.Count); writer.WriteLine();
            foreach (var c in cases)
            {
                N(c.Stage); N(c.Chain ? 1 : 0);
                foreach (var f in Fields) N(f.Read(c.Before));
                N((byte)c.Input.Keys);
                N(c.Input.FunctionKey == 10 ? 121 : c.Input.FunctionKey == 1 && c.Before.Raw.Throttle == 12 ? -1 : c.Input.FunctionKey * 12);
                N(c.Input.ToggleGear ? 1 : 0); N(c.Input.ToggleHook ? 1 : 0); N(c.Input.ToggleBrake ? 1 : 0);
                var e = c.Environment;
                N(e.CallerOwnsViewedCell ? 1 : 0); N(e.SuppressRearm ? 1 : 0); N(e.SoundSystemRunning ? 0 : 1);
                N(e.LandmarkZBias); N(e.VertexPolicy == FlightContactVertexPolicy.RotateModelAtEntry ? 1 : 0);
                foreach (var v in e.PlayerModel.Vertices[..4]) V(v);
                N(e.Carrier == null ? 0 : 1);
                if (e.Carrier is { } ship)
                {
                    N(ship.Status); N(ship.AircraftClass); N(ship.Group); N(ship.UnitShift);
                    N(ship.Position.X); N(ship.Position.Y); N(ship.Position.Z); N(ship.Pitch); N(ship.Heading); N(ship.Roll);
                    foreach (var word in ship.MatrixB.ToArray()) N(word);
                    foreach (var v in ship.RotatedVertices) V(v);
                    foreach (var v in sourceCarrierVertices) V(v);
                }
                N(e.Landmarks.Count);
                foreach (var landmark in e.Landmarks)
                {
                    N(landmark.CellX); N(landmark.CellZ); N(landmark.Ordinal); Box(landmark.Box); N(landmark.Nodes.Count);
                    foreach (var node in landmark.Nodes)
                    {
                        Box(node.Box); N(node.Planes.Count);
                        foreach (var plane in node.Planes) { V(plane.Reference); V(plane.Normal); }
                    }
                }
                writer.WriteLine();
            }
        }
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("Whole-player native oracle exceeded its budget."); }
        var diagnostics = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || diagnostics.Length != 0) throw new InvalidOperationException($"Native player oracle exit={process.ExitCode}: {diagnostics}");
        return output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
