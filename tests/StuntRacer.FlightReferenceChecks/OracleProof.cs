using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StuntRacer.FlightReference;

namespace StuntRacer.FlightReferenceChecks;

internal static class OracleProof
{
    private sealed record Case(string Name, int Stage, FlightRawState Before, FlightRawState Expected,
        RawVector Direction = default, RawVelocity Target = default);

    // Must match the ignored adapter's value protocol. These are all the stored
    // raw fields exposed by this library (not an assertion about the entire C
    // flight_state_t, original variable block, or full game state).
    internal static readonly (string Name, Func<FlightRawState, long> Read)[] Fields =
    [
        ("Status", s=>s.Status), ("Configuration", s=>s.Configuration),
        ("Surface", s=>s.Surface), ("Dynamics", s=>s.Dynamics),
        ("GearEjection", s=>s.GearEjection), ("AircraftClass", s=>s.AircraftClass),
        ("PitchCommand", s=>s.PitchCommand), ("RudderCommand", s=>s.RudderCommand),
        ("RollCommand", s=>s.RollCommand), ("Throttle", s=>s.Throttle),
        ("Repeat", s=>s.Repeat), ("Keys", s=>(byte)s.Keys), ("Preset", s=>s.Preset),
        ("Counter", s=>s.Counter), ("GroundTerm", s=>s.GroundTerm),
        ("PitchRate", s=>s.PitchRate), ("HeadingRate", s=>s.HeadingRate), ("RollRate", s=>s.RollRate),
        ("Pitch", s=>s.Pitch), ("Heading", s=>s.Heading), ("Roll", s=>s.Roll),
        ("TargetSpeed", s=>s.TargetSpeed), ("IndicatedSpeed", s=>s.IndicatedSpeed),
        ("Sink", s=>s.Sink), ("Gust", s=>s.Gust), ("Fuel", s=>s.Fuel),
        ("AltitudeCeiling", s=>s.AltitudeCeiling), ("SimulationFlags", s=>s.SimulationFlags),
        ("GroundFuelFlags", s=>s.GroundFuelFlags), ("FlightWarningFlags", s=>s.FlightWarningFlags),
        ("Warnings", s=>s.Warnings), ("Cues", s=>s.Cues), ("StallLampCounter", s=>s.StallLampCounter),
        ("GustAndSinkEnabled", s=>s.GustAndSinkEnabled?1:0),
        ("PointerEnabled", s=>s.PointerEnabled?1:0),
        ("PointerHeight", s=>s.PointerHeight), ("PointerReference", s=>s.PointerReference),
        ("DisplayMode", s=>s.DisplayMode), ("EntityIndex", s=>s.EntityIndex),
        ("ViewedEntityIndex", s=>s.ViewedEntityIndex),
        ("ThrottleSound", s=>s.ThrottleSound), ("MotionSound", s=>s.MotionSound),
        ("CentreNotice", s=>s.CentreNotice), ("NoticeDuration", s=>s.NoticeDuration),
        ("HookLamp", s=>s.HookLamp), ("GroundSoundVolume", s=>s.GroundSoundVolume),
        ("X", s=>s.Position.X), ("Y", s=>s.Position.Y), ("Z", s=>s.Position.Z),
        ("DX", s=>s.Displacement.X), ("DY", s=>s.Displacement.Y), ("DZ", s=>s.Displacement.Z),
        ("CellX", s=>s.CellX), ("CellZ", s=>s.CellZ),
        ("FineX", s=>s.FineX), ("FineZ", s=>s.FineZ), ("Altitude256", s=>s.Altitude256),
        ("SubCell", s=>s.SubCell), ("Neighbour", s=>s.Neighbour),
        ("ViewCellX", s=>s.ViewCellX), ("ViewCellZ", s=>s.ViewCellZ), ("CellRefresh", s=>s.CellRefresh)
    ];

    internal static void Run(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the ignored pinned-source adapter first.", executable);
        var storedScalars = typeof(FlightRawState).GetProperties().Where(p => p.SetMethod != null)
            .Sum(p => p.PropertyType == typeof(RawPosition) || p.PropertyType == typeof(RawDisplacement) ? 3 : 1);
        Program.Equal(storedScalars, Fields.Length, "all stored raw-state scalars must be compared");
        Program.Equal(Fields.Length, Fields.Select(f => f.Name).Distinct().Count(), "unique wire fields");
        var sourceRoot = Path.Combine(Path.GetDirectoryName(executable)!, "..", "reference", "interceptor-cleanroom");
        var source = Path.GetFullPath(Path.Combine(sourceRoot, "src", "core", "flight.c"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));
        Program.Equal("2158ead620b2d5919236c2cc08c33d0a2c8c812bf8e906c8705fca32427e3f28", hash, "pinned C source SHA256");
        Console.WriteLine("ORACLE source commit: 2475dcd65f454a37b41a2be27629ad958f34ddc5");
        Console.WriteLine($"ORACLE flight.c SHA256: {hash}");
        Console.WriteLine($"ORACLE executable SHA256: {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)))}");
        Console.WriteLine($"ORACLE raw scalar fields: {Fields.Length}; target-vector outputs: 3.");
        long total = 0, mismatchedCases = 0, mismatchedFields = 0, comparisons = 0, maximumError = 0;
        var stages = new SortedDictionary<int, int>();
        var differences = new SortedDictionary<string, int>();
        var diagnosticKinds = new HashSet<string>();
        var diagnostics = new List<string>();
        for (var variant = 0; variant < 2; variant++)
        {
            var tables = InventedTables.Make(variant);
            var cases = Cases(new FlightReferenceKernel(tables));
            var lines = Execute(executable, tables, cases);
            Program.Equal(cases.Count, lines.Length, "native result count");
            for (var i = 0; i < cases.Count; i++)
            {
                var test = cases[i];
                var tokens = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Program.Equal(Fields.Length + 4, tokens.Length, $"native field count at {i}");
                Program.Equal(i, int.Parse(tokens[0], CultureInfo.InvariantCulture), "native case ordinal");
                var errors = new List<string>();
                var expected = Fields.Select(f => f.Read(test.Expected))
                    .Concat([(long)test.Target.X, test.Target.Y, test.Target.Z]).ToArray();
                for (var n = 0; n < expected.Length; n++)
                {
                    var actual = long.Parse(tokens[n + 1], CultureInfo.InvariantCulture);
                    comparisons++;
                    if (actual == expected[n]) continue;
                    mismatchedFields++;
                    maximumError = Math.Max(maximumError, Math.Abs(expected[n] - actual));
                    var name = n < Fields.Length ? Fields[n].Name : $"Target[{n - Fields.Length}]";
                    errors.Add($"{name}: C#={expected[n]}, pinned-C={actual}");
                    var kind = $"stage {test.Stage}/{name}";
                    differences[kind] = differences.GetValueOrDefault(kind) + 1;
                    if (diagnosticKinds.Add(kind))
                        diagnostics.Add($"table-set={variant} {test.Name}: {name}: C#={expected[n]}, pinned-C={actual}");
                }
                if (errors.Count != 0)
                {
                    mismatchedCases++;
                }
                total++;
                stages[test.Stage] = stages.GetValueOrDefault(test.Stage) + 1;
            }
        }
        foreach (var (stage, count) in stages)
            Console.WriteLine($"DIFF stage {stage} ({StageName(stage)}): {count} cases.");
        foreach (var (field, count) in differences) Console.Error.WriteLine($"DIFF field {field}: {count} differing cases.");
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine($"DIFF MISMATCH {diagnostic}");
        Console.WriteLine($"DIFF total={total}; scalar-comparisons={comparisons}; mismatched-cases={mismatchedCases}; mismatched-fields={mismatchedFields}; max-absolute-error={maximumError}.");
        Console.WriteLine("DIFF: same invented tables and raw inputs, real pinned C routines in a separate native process.");
        Console.WriteLine("LIMITS: event handlers/pointer interrupt and airborne X/Y/Z advancement have direct regressions, not a component-oracle verdict. Motion differential is airborne only.");
        Console.WriteLine("LIMITS: no original image, stock tables, attitude composition, contact/collisions, whole-step or whole-game parity.");
        if (mismatchedCases != 0) throw new InvalidOperationException($"{mismatchedCases} native differential cases disagree; see exact fields above.");
    }

    private static string StageName(int stage) => stage switch
    {
        1 => "input_convert", 2 => "speed_step", 3 => "surfaces", 4 => "target_velocity",
        5 => "motion_step, airborne velocity/Y outputs", 6 => "update_cells",
        _ => "invalid"
    };

    private static string[] Execute(string executable, FlightReferenceTables t, List<Case> cases)
    {
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using (var binary = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: true))
        {
            binary.Write(new byte[] { (byte)'S', (byte)'F', (byte)'R', (byte)'1' });
            foreach (var word in t.PlayerResponse) binary.Write(word);
            foreach (var word in t.AiResponse) binary.Write(word);
            foreach (var word in t.Gust) binary.Write(word);
            foreach (var word in t.Sink) binary.Write(word);
            foreach (var word in t.GustSineWords) binary.Write(word);
            foreach (var word in t.VectorLength) binary.Write(word);
        }
        using (var text = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: true))
        {
            text.WriteLine(cases.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var c in cases)
            {
                text.Write(c.Stage.ToString(CultureInfo.InvariantCulture));
                foreach (var f in Fields)
                {
                    text.Write(' ');
                    text.Write(f.Read(c.Before).ToString(CultureInfo.InvariantCulture));
                }
                text.WriteLine(FormattableString.Invariant($" {c.Direction.X} {c.Direction.Y} {c.Direction.Z}"));
            }
        }
        process.StandardInput.Close();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Bounded native differential run exceeded 60 seconds.");
        }
        var stderr = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Native adapter exited {process.ExitCode}: {stderr}");
        if (stderr.Length != 0) throw new InvalidOperationException($"Unexpected native diagnostics: {stderr}");
        return output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static List<Case> Cases(FlightReferenceKernel kernel)
    {
        var cases = new List<Case>();
        var sentinel = Program.Air() with
        {
            Status = 0x5a6b, Configuration = 0xaabb, Surface = 0xdf, Dynamics = 0xe5,
            GearEjection = 0x65, AircraftClass = 0x14, PitchCommand = -17, RudderCommand = 19,
            RollCommand = -57, Throttle = -31, Repeat = 0xc7, Keys = (DigitalKeys)0xda, Preset = -1,
            Counter = -300, GroundTerm = -301, PitchRate = -302, HeadingRate = 303, RollRate = -304,
            Pitch = -305, Heading = 306, Roll = -307, TargetSpeed = -308, IndicatedSpeed = 309,
            Sink = 310, Gust = -311, Fuel = 123456, AltitudeCeiling = 0x730000, SimulationFlags = 0x8c40,
            GroundFuelFlags = 0x2def, FlightWarningFlags = 0x8abc, Warnings = unchecked((int)0xbadc0ffe),
            Cues = 0xcafebabe, StallLampCounter = -19, GustAndSinkEnabled = true, PointerEnabled = true,
            PointerHeight = -912, PointerReference = 876, DisplayMode = 3, EntityIndex = 1,
            ViewedEntityIndex = 2, ThrottleSound = 6, MotionSound = 7, CentreNotice = 0xfe,
            NoticeDuration = 11, HookLamp = 0x85, GroundSoundVolume = 13,
            Position = new(-1234567, 2345678, -3456789), Displacement = new(-400, 800, -1200),
            CellX = -312, CellZ = 313, FineX = 314, FineZ = 315, Altitude256 = -123456,
            SubCell = 12, Neighbour = 5, ViewCellX = 316, ViewCellZ = -317, CellRefresh = 0x43
        };
        // The nonzero ejection nibble makes input_convert return unchanged.
        // Distinct sentinels also check that the adapter is not swapping fields.
        cases.Add(new("all-field protocol preservation sentinel", 1, sentinel, sentinel));
        for (var keys = 0; keys < 256; keys++)
        for (var family = 0; family < 8; family++)
        {
            var s = Program.Air() with
            {
                Keys = (DigitalKeys)keys, PitchCommand = (sbyte)(keys % 41 - 20),
                RudderCommand = (sbyte)((keys * 3) % 41 - 20), RollCommand = (sbyte)(keys % 121 - 60),
                Repeat = (byte)(0xa0 | (keys % 9)), Counter = (short)(keys % 3 - 1)
            };
            switch (family)
            {
                case 1: s.Preset = 121; s.Throttle = -28; s.Repeat = 1; break;
                case 2: s.Preset = 121; s.Throttle = -28; s.Repeat = 2; break;
                case 3: s.Preset = -1; s.Throttle = (sbyte)(keys % 121); break;
                case 4: s.Preset = 120; s.Throttle = 120; s.Configuration = 0x2000; break;
                case 5: s.Surface = 0x10; s.Preset = 108; break;
                case 6: s.GearEjection = 0xea; s.Preset = 121; break;
                case 7: s.Preset = (sbyte)(12 * (keys % 10)); s.Configuration = (ushort)((keys & 1) * 8); break;
            }
            cases.Add(new($"input keys={keys} family={family}", 1, s, FlightReferenceKernel.ConvertCommands(s)));
        }

        sbyte[] throttles = [-32, -11, -1, 0, 1, 9, 11, 12, 60, 107, 108, 119, 120];
        short[] speeds = [-32768, -2000, -145, -60, -1, 0, 30, 31, 768, 1500, 2048, 7499, 7501, 8400, 32767];
        short[] pitches = [0, 2400, 6000, 8000, 16000, 22000, 28000, -3, -32768, 32767];
        short[] rolls = [0, 1600, 7200, 14400, 24000, 28000, -123];
        short[] rates = [-32768, -1300, -1, 0, 1, 400, 32767];
        short[] ground = [-77, -8, 0, 7, 37, 32767, -32768];
        short[] gusts = [-32768, -1000, -1, 0, 1, 1000, 32767];
        var serial = 0;
        foreach (var throttle in throttles)
        foreach (var speed in speeds)
        for (var branch = 0; branch < 14; branch++)
        {
            var n = serial++;
            var s = Program.Air() with
            {
                Throttle = throttle, TargetSpeed = speed, IndicatedSpeed = speeds[(n + 4) % speeds.Length],
                Pitch = pitches[n % pitches.Length], Roll = rolls[n % rolls.Length],
                PitchRate = rates[n % rates.Length], GroundTerm = ground[n % ground.Length],
                Gust = gusts[n % gusts.Length], Sink = 99, GustAndSinkEnabled = n % 5 != 0,
                FlightWarningFlags = (ushort)(n % 3 == 0 ? 0x4002 : 2),
                Fuel = n % 11 == 0 ? 1 : 1_000_000, CentreNotice = 0x55, NoticeDuration = 19,
                EntityIndex = (ushort)(n % 13 == 0 ? 1 : 0), ViewedEntityIndex = (ushort)(n % 2)
            };
            if (branch is >= 4 and <= 9 or 11)
            {
                s.Configuration |= 0x80;
                s.Surface = 4;
                s.GearEjection = 0;
            }
            switch (branch)
            {
                case 1: s.GearEjection = 0; break;
                case 2: s.Status |= 0x800; break;
                case 3: s.Configuration |= 8; break;
                case 5: s.Surface = 0; break;
                case 6: s.Surface = 8; break;
                case 7: s.Configuration |= 0xc000; break;
                case 8: s.Status |= 0x800; break;
                case 9: s.GearEjection |= 0x80; break;
                case 10: s.Dynamics |= 1; break;
                case 11: s.Dynamics |= 1; break;
                case 12: s.SimulationFlags = 0; break;
                case 13: s.GroundFuelFlags = 0x4000; break;
            }
            cases.Add(new($"speed T={throttle} S={speed} branch={branch}", 2, s, kernel.StepThrottleFuelSpeed(s)));
        }

        // Key repeat/detents and pointer paths also pass through real speed_step,
        // not an authored C copy of the throttle rules.
        foreach (var throttle in new sbyte[] { -32, -4, 0, 1, 9, 119, 120 })
        for (var keys = 0; keys <= 3; keys++)
        for (var repeat = 0; repeat <= 8; repeat++)
        {
            var s = Program.Air() with
            {
                Throttle = throttle, Keys = (DigitalKeys)keys, Repeat = (byte)(0xb0 | repeat),
                Configuration = (ushort)(repeat % 3 == 0 ? 0x2008 : repeat % 3 == 1 ? 0x400 : 0),
                PointerEnabled = true, PointerHeight = (short)(repeat % 3 == 0 ? 960 : repeat % 3 == 1 ? -50 : 517),
                PointerReference = 500, DisplayMode = (byte)(repeat % 2 == 0 ? 1 : 2),
                Fuel = repeat == 8 ? 0 : 1_000_000, NoticeDuration = 31
            };
            cases.Add(new($"throttle T={throttle} keys={keys} repeat={repeat}", 2, s, kernel.StepThrottleFuelSpeed(s)));
        }

        var random = new Random(0x18fa);
        sbyte[] commands = [-20, -1, 0, 1, 20];
        sbyte[] rollCommands = [-60, -3, 0, 3, 60];
        short[] surfaceRates = [-32768, -1280, -16, -15, -1, 0, 1, 15, 16, 1279, 1280, 32767];
        int[] fuels = [-1, 0, 1, 460799, 460800, 767999, 768000, 1_000_000];
        for (var n = 0; n < 1800; n++)
        {
            var s = Program.Air() with
            {
                PitchCommand = commands[random.Next(commands.Length)], RudderCommand = commands[random.Next(commands.Length)],
                RollCommand = rollCommands[random.Next(rollCommands.Length)],
                PitchRate = surfaceRates[random.Next(surfaceRates.Length)],
                HeadingRate = surfaceRates[random.Next(surfaceRates.Length)],
                RollRate = surfaceRates[random.Next(surfaceRates.Length)],
                TargetSpeed = speeds[random.Next(speeds.Length)], IndicatedSpeed = speeds[random.Next(speeds.Length)],
                Pitch = pitches[random.Next(pitches.Length)], Roll = rolls[random.Next(rolls.Length)],
                GroundTerm = (short)(n % 3 == 0 ? -37 : 0),
                Configuration = (ushort)((n % 2 == 0 ? 0x80 : 0) | (n % 3 == 0 ? 0x100 : 0) | 8 | (n % 5 == 0 ? 0x40 : 0)),
                AircraftClass = (byte)(n % 4 == 0 ? 0x14 : 0x11),
                Surface = (byte)(n % 4 == 0 ? 2 : n % 4 == 1 ? 8 : 4),
                Dynamics = (byte)(n % 3 == 0 ? 4 : n % 3 == 1 ? 1 : 0),
                GearEjection = (byte)(n % 3 == 0 ? 0 : 0xe0),
                Fuel = fuels[n % fuels.Length], Warnings = n % 4 == 0 ? 0x43 : 0x1000,
                Cues = 0x100, StallLampCounter = -1,
                EntityIndex = (ushort)(n % 7 == 0 ? 1 : 0), SimulationFlags = (ushort)(n % 17 == 0 ? 0 : 0x40)
            };
            cases.Add(new($"surfaces n={n}", 3, s, kernel.StepControlSurfaces(s)));
        }

        RawVector[] directions =
        [
            default, new(0,0,16384), new(0,0,-16384), new(16384,0,0), new(-16384,0,0),
            new(0,16384,0), new(0,-16384,0), new(-8192,4096,13500), new(8191,-4097,-13000),
            new(16384,16384,16384), new(1,0,0), new(1,-2,3)
        ];
        foreach (var direction in directions)
        foreach (var speed in speeds)
        {
            var s = Program.Air() with { IndicatedSpeed = speed };
            var target = kernel.ComputeTargetVelocity(direction, speed);
            cases.Add(new($"target n={direction} W={speed}", 4, s, s, direction, target));
        }

        for (var n = 0; n < 360; n++)
        {
            var direction = directions[n % directions.Length];
            var s = Program.Air() with
            {
                IndicatedSpeed = speeds[n % speeds.Length], Sink = (short)(n % 3 == 0 ? 650 : n % 3 == 1 ? 17 : 0),
                Displacement = new(-4 * (n % 17 - 8), -4 * (n % 31 - 15), -4 * (n % 7 - 3))
            };
            var target = kernel.ComputeTargetVelocity(direction, s.IndicatedSpeed);
            var expected = s;
            expected.Displacement = FlightReferenceKernel.SmoothVelocity(s.Displacement, target, s.Sink, false);
            expected.Position = s.Position with { Y = Math.Min(s.Position.Y + expected.Displacement.Y, s.AltitudeCeiling) };
            cases.Add(new($"airborne motion n={n}", 5, s, expected, direction, target));
        }
        {
            var direction = new RawVector(0, -16384, 0);
            var s = Program.Air() with { IndicatedSpeed = -32768, Sink = 650, Displacement = new(0, -131068, 0) };
            var target = kernel.ComputeTargetVelocity(direction, s.IndicatedSpeed);
            var expected = s;
            expected.Displacement = FlightReferenceKernel.SmoothVelocity(s.Displacement, target, s.Sink, false);
            expected.Position = s.Position with { Y = s.Position.Y + expected.Displacement.Y };
            cases.Add(new("airborne long-velocity overflow boundary", 5, s, expected, direction, target));
        }
        {
            var s = Program.Air() with { Sink = 17, Displacement = new(int.MinValue, 0, int.MaxValue - 3) };
            var expected = s;
            expected.Displacement = FlightReferenceKernel.SmoothVelocity(s.Displacement, default, s.Sink, false);
            expected.Position = s.Position with { Y = s.Position.Y + expected.Displacement.Y };
            cases.Add(new("airborne full-long displacement decode", 5, s, expected));
        }
        for (var n = 0; n < 400; n++)
        {
            var s = Program.Air() with
            {
                Position = new(n % 7 == 0 ? -1 - n * 1711 : random.Next(0x20000000),
                    random.Next(0x800000), random.Next(0x20000000)),
                CellX = (short)(n % 127), CellZ = (short)(n % 63),
                SubCell = (byte)(n % 16), Neighbour = 6, ViewCellX = 64, ViewCellZ = 64
            };
            cases.Add(new($"cells n={n}", 6, s, FlightReferenceKernel.RefreshPositionCells(s)));
        }
        return cases;
    }
}
