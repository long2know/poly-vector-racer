using StuntRacer.FlightReference;

namespace StuntRacer.FlightReferenceChecks;

internal static class Program
{
    private static readonly FlightReferenceTables Tables = InventedTables.Make();
    private static readonly FlightReferenceKernel Kernel = new(Tables);
    private static int passed;
    private static int failed;

    internal static FlightRawState Air() => new()
    {
        Status = 0x11c8, AircraftClass = 0x11, SimulationFlags = 0x40,
        Throttle = 60, Repeat = 1, GearEjection = 0xe0, Fuel = 1_000_000,
        AltitudeCeiling = 0x800000, Position = new(0x10000000, 0x400000, 0x10000000),
        DisplayMode = 1, PointerHeight = 480, PointerReference = 480
    };

    private static FlightRawState Ground() => Air() with
    {
        Configuration = 0x80, GearEjection = 0, Surface = 4,
        Position = new(0x10000000, 1800, 0x10000000)
    };

    internal static void Equal<T>(T expected, T actual, string field = "") where T : IEquatable<T>
    {
        if (!expected.Equals(actual)) throw new InvalidOperationException($"{field}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    private static int Main(string[] args)
    {
        Check("digital demand ramps, saturates and releases", () =>
        {
            var s = Air() with { Keys = DigitalKeys.StickForward | DigitalKeys.RudderRight | DigitalKeys.RollRight };
            for (var i = 1; i <= 25; i++)
            {
                s = FlightReferenceKernel.ConvertCommands(s);
                Equal((sbyte)Math.Min(i, 20), s.PitchCommand, "pitch");
                Equal((sbyte)Math.Min(i, 20), s.RudderCommand, "rudder");
                Equal((sbyte)Math.Min(3 * i, 60), s.RollCommand, "roll");
            }
            s.Keys = 0;
            s = FlightReferenceKernel.ConvertCommands(s);
            Equal((sbyte)0, s.PitchCommand);
            Equal((sbyte)0, s.RudderCommand);
            Equal((sbyte)0, s.RollCommand);
        });
        Check("digital reversal jumps sign; both keys choose negative", () =>
        {
            var s = Air() with { PitchCommand = 20, RudderCommand = 20, RollCommand = 60, Keys = (DigitalKeys)0xfc };
            s = FlightReferenceKernel.ConvertCommands(s);
            Equal((sbyte)-1, s.PitchCommand);
            Equal((sbyte)-1, s.RudderCommand);
            Equal((sbyte)-3, s.RollCommand);
            s.Keys = DigitalKeys.StickForward | DigitalKeys.RudderRight | DigitalKeys.RollRight;
            s = FlightReferenceKernel.ConvertCommands(s);
            Equal((sbyte)1, s.PitchCommand);
            Equal((sbyte)1, s.RudderCommand);
            Equal((sbyte)3, s.RollCommand);
        });
        Check("ejection skips command and preset conversion", () =>
        {
            var s = Air() with { GearEjection = 0xea, Preset = 121, PitchCommand = 13, Keys = (DigitalKeys)0xff };
            Equal(s, FlightReferenceKernel.ConvertCommands(s));
        });
        Check("control lock follows preset; unlock is strictly negative", () =>
        {
            var s = Air() with { Surface = 0x10, Counter = 0, Preset = 108, PitchCommand = 8, Keys = DigitalKeys.StickForward };
            var locked = FlightReferenceKernel.ConvertCommands(s);
            Equal((sbyte)0, locked.PitchCommand);
            Equal(1, (byte)locked.Keys & 3);
            locked.Counter = -1;
            var unlocked = FlightReferenceKernel.ConvertCommands(locked);
            Equal((sbyte)1, unlocked.PitchCommand);
            Equal(0, unlocked.Surface & 0x10);
        });
        Check("preset direction compares signed bytes before wrapped difference", () =>
        {
            var s = Air() with { Throttle = -28, Preset = 121, Repeat = 2, Keys = DigitalKeys.ThrottleDown };
            Equal(0, (byte)FlightReferenceKernel.ConvertCommands(s).Keys & 3);
            s.Repeat = 1;
            Equal(1, (byte)FlightReferenceKernel.ConvertCommands(s).Keys & 3);
            s.Throttle = 0;
            s.Preset = -1;
            s = FlightReferenceKernel.ConvertCommands(s);
            Equal((sbyte)0, s.Preset);
            Equal(0, (byte)s.Keys & 3);
        });
        Check("all F presets, repeated F1 and input event guards", () =>
        {
            for (var n = 1; n <= 10; n++)
            {
                var s = FlightReferenceKernel.ApplyKeyEvents(Air(), new(0, n));
                Equal((sbyte)(n == 10 ? 121 : n * 12), s.Preset);
                Equal((short)Math.Min(s.Preset * 8, 960), s.PointerHeight);
                Equal(s.PointerHeight, s.PointerReference);
            }
            Equal((sbyte)-1, FlightReferenceKernel.ApplyKeyEvents(Air() with { Throttle = 12 }, new(0, 1)).Preset);
            Equal((byte)0, FlightReferenceKernel.ApplyKeyEvents(Ground(), new(0, ToggleGear: true)).GearEjection);
            Equal((byte)0x60, FlightReferenceKernel.ApplyKeyEvents(Air(), new(0, ToggleGear: true)).GearEjection);
            Equal(0x8000, FlightReferenceKernel.ApplyKeyEvents(Air(), new(0, ToggleHook: true)).Configuration & 0x8000);
            Equal(0, FlightReferenceKernel.ApplyKeyEvents(Air() with { AircraftClass = 0x10 }, new(0, ToggleHook: true)).Configuration & 0x8000);
            var b = FlightReferenceKernel.ApplyKeyEvents(Air() with { Preset = 108 }, new(0, ToggleBrake: true, Eject: true));
            Equal(0x800, b.Status & 0x800);
            Equal((short)0, b.PointerHeight);
            Equal((sbyte)0, b.Preset);
            Equal(10, b.GearEjection & 15);
        });
        Check("throttle repeat preserves high nibble and both-key notice", () =>
        {
            var s = Kernel.StepThrottleFuelSpeed(Air() with { Throttle = 0, Repeat = 0xa0, Keys = (DigitalKeys)3 });
            Equal((byte)0xa1, s.Repeat);
            Equal((sbyte)0, s.Throttle);
            s = Kernel.StepThrottleFuelSpeed(s);
            Equal((byte)0xfe, s.CentreNotice);
            Equal((sbyte)0, s.Throttle);
            for (var i = 0; i < 12; i++) s = Kernel.StepThrottleFuelSpeed(s);
            Equal((byte)0xa8, s.Repeat);
            s.Keys = 0;
            Equal((byte)0xa1, Kernel.StepThrottleFuelSpeed(s).Repeat);
        });
        Check("military detent, second press afterburner, down-key cut", () =>
        {
            var s = Kernel.StepThrottleFuelSpeed(Air() with { Throttle = 119, Repeat = 7, Keys = DigitalKeys.ThrottleUp });
            Equal((sbyte)120, s.Throttle);
            True((s.Configuration & 0x2008) == 0x2000, "First press must stop at military detent.");
            Equal(0, (byte)s.Keys & 3);
            s.Keys = DigitalKeys.ThrottleUp;
            var fuel = s.Fuel;
            s = Kernel.StepThrottleFuelSpeed(s);
            True(s.Afterburner, "Second press must engage afterburner.");
            Equal(fuel - 1536, s.Fuel);
            Equal((byte)0xfd, s.CentreNotice);
            Equal((byte)6, s.NoticeDuration);
            s.Keys = DigitalKeys.ThrottleDown;
            s = Kernel.StepThrottleFuelSpeed(s);
            Equal((sbyte)119, s.Throttle);
            True(!s.Afterburner, "Down must cut afterburner.");
            Equal((byte)0xfc, s.CentreNotice);
            Equal((byte)6, s.NoticeDuration, "cut must preserve duration");
        });
        Check("idle detent requires second press before bounded reverse", () =>
        {
            var s = Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 2, Keys = DigitalKeys.ThrottleDown });
            Equal((sbyte)0, s.Throttle);
            True((s.Configuration & 0x400) != 0, "Idle detent must latch.");
            Equal(0, (byte)s.Keys & 3);
            s.Keys = DigitalKeys.ThrottleDown;
            s = Kernel.StepThrottleFuelSpeed(s);
            Equal((sbyte)-4, s.Throttle);
            for (var i = 0; i < 30; i++) s = Kernel.StepThrottleFuelSpeed(s);
            Equal((sbyte)-32, s.Throttle);
            s = Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 0, Surface = 8, Keys = DigitalKeys.ThrottleDown });
            Equal((sbyte)0, s.Throttle, "catapult forbids reverse crossing");
        });
        Check("F10 presses pass through the real detent sequence", () =>
        {
            var s = FlightReferenceKernel.ApplyKeyEvents(Air() with { Throttle = 118 }, new(0, 10));
            for (var i = 0; i < 20 && s.Preset != 0; i++)
                s = Kernel.StepThrottleFuelSpeed(FlightReferenceKernel.ConvertCommands(s));
            Equal((sbyte)120, s.Throttle);
            True(!s.Afterburner && s.Preset == 0, "First F10 must not bypass detent.");
            s = FlightReferenceKernel.ApplyKeyEvents(s, new(0, 10));
            for (var i = 0; i < 8 && !s.Afterburner; i++)
                s = Kernel.StepThrottleFuelSpeed(FlightReferenceKernel.ConvertCommands(s));
            True(s.Afterburner, "Second F10 must engage.");
        });
        Check("exact normal, reverse and afterburner fuel burns", () =>
        {
            foreach (var throttle in new sbyte[] { -32, -11, -1, 0, 1, 9, 60, 119, 120 })
            {
                var s = Kernel.StepThrottleFuelSpeed(Air() with { Throttle = throttle });
                Equal(1_000_000 - Math.Abs((int)throttle) / 2, s.Fuel, $"T={throttle}");
            }
            Equal(998464, Kernel.StepThrottleFuelSpeed(Air() with { Configuration = 8 }).Fuel);
        });
        Check("zero fuel skips repeat and pointer logic but runs idle", () =>
        {
            var s = Kernel.StepThrottleFuelSpeed(Air() with
            {
                Fuel = 0, Throttle = 120, Configuration = 8, Repeat = 5,
                Keys = DigitalKeys.ThrottleUp, PointerEnabled = true, PointerHeight = 960,
                TargetSpeed = 1000, IndicatedSpeed = 1000
            });
            Equal((sbyte)0, s.Throttle);
            Equal((byte)5, s.Repeat);
            Equal((short)0, s.PointerHeight);
            True(!s.Afterburner, "Fuel zero must cut afterburner.");
            Equal((short)970, s.TargetSpeed);
        });
        Check("pointer blank wrapping, disabled half delta and clamps", () =>
        {
            var s = Air() with { PointerEnabled = true, PointerHeight = 0 };
            Equal((short)56, FlightReferenceKernel.ApplyPointerBlank(s, 200).PointerHeight);
            Equal((short)128, FlightReferenceKernel.ApplyPointerBlank(s, -128).PointerHeight);
            Equal((short)960, FlightReferenceKernel.ApplyPointerBlank(s with { PointerHeight = 900 }, -100).PointerHeight);
            Equal((short)3, FlightReferenceKernel.ApplyPointerBlank(s with { PointerEnabled = false }, -5).PointerHeight);
        });
        Check("pointer follow/fire boundaries 15,16,17 and negative idle", () =>
        {
            foreach (var delta in new[] { 15, 16, 17 })
            {
                var s = Kernel.StepThrottleFuelSpeed(Air() with
                {
                    PointerEnabled = true, PointerHeight = (short)(500 + delta), PointerReference = 500
                });
                Equal((sbyte)(delta == 17 ? 64 : 0), s.Preset, $"fire delta {delta}");
            }
            var follow = Kernel.StepThrottleFuelSpeed(Air() with
            {
                PointerEnabled = true, PointerHeight = 515, PointerReference = 500,
                Keys = DigitalKeys.ThrottleUp, DisplayMode = 2
            });
            Equal((short)(follow.Throttle * 8), follow.PointerHeight, "external view still follows");
            var idle = Kernel.StepThrottleFuelSpeed(Air() with { PointerEnabled = true, PointerHeight = -50, PointerReference = 0 });
            Equal((sbyte)-1, idle.Preset);
            Equal((short)0, idle.PointerHeight);
        });
        Check("pointer top stop leaves after one step; afterburner threshold", () =>
        {
            var s = Air() with { Throttle = 9, PointerEnabled = true, PointerHeight = 960, PointerReference = 960 };
            s = Kernel.StepThrottleFuelSpeed(s);
            Equal((sbyte)120, s.Preset);
            s = Kernel.StepThrottleFuelSpeed(FlightReferenceKernel.ConvertCommands(s));
            Equal((sbyte)10, s.Throttle);
            Equal((short)80, s.PointerHeight);
            foreach (var height in new short[] { 860, 864, 870 })
            {
                var a = Kernel.StepThrottleFuelSpeed(Air() with
                {
                    Throttle = 120, Configuration = 8, PointerEnabled = true,
                    PointerHeight = height, PointerReference = 920
                });
                Equal((sbyte)(height == 860 ? 107 : 0), a.Preset, $"AB pointer {height}");
            }
        });
        Check("ground/air/rough ground/catapult acceleration are distinct", () =>
        {
            Equal((short)42, Kernel.StepThrottleFuelSpeed(Air() with { Throttle = 120 }).TargetSpeed);
            Equal((short)4, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 120 }).TargetSpeed);
            Equal((short)3, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 120, Surface = 0 }).TargetSpeed);
            Equal((short)0, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 9 }).TargetSpeed);
            Equal((short)0, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 107, Surface = 8 }).TargetSpeed);
            Equal((short)8, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 108, Surface = 8 }).TargetSpeed);
            Equal((short)9, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 108, Surface = 0xc8 }).TargetSpeed);
            Equal((short)4, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 120, Configuration = 0x88 }).TargetSpeed);
        });
        Check("ground reverse uses the ground lag, not airborne deceleration", () =>
        {
            Equal((short)913, Kernel.StepThrottleFuelSpeed(Air() with { Throttle = -10, TargetSpeed = 1000 }).TargetSpeed);
            Equal((short)992, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = -10, TargetSpeed = 1000 }).TargetSpeed);
            Equal((short)-100, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = -10, TargetSpeed = -100 }).TargetSpeed);
            Equal((short)-93, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = -10, TargetSpeed = -90 }).TargetSpeed);
            Equal((short)-98, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = -10, TargetSpeed = -100, Surface = 0 }).TargetSpeed);
        });
        Check("idle deceleration branch boundaries and asymmetric negatives", () =>
        {
            foreach (var pair in new (short Before, short After)[]
            {
                (29,29), (30,30), (31,1), (1499,1469), (1500,1440),
                (7501,7359), (-145,-63), (-144,-84), (-60,0)
            })
                Equal(pair.After, Kernel.StepThrottleFuelSpeed(Air() with { Throttle = 0, TargetSpeed = pair.Before }).TargetSpeed, $"S={pair.Before}");
            Equal((short)-1, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 0, TargetSpeed = -16 }).TargetSpeed);
            Equal((short)0, Kernel.StepThrottleFuelSpeed(Ground() with { Throttle = 0, TargetSpeed = -15 }).TargetSpeed);
        });
        Check("wheel, hook, belly and skid selection uses OLD speed and priority", () =>
        {
            var s = Ground() with { Throttle = 120, TargetSpeed = 1000, Status = 0x19c8 };
            Equal((short)938, Kernel.StepThrottleFuelSpeed(s).TargetSpeed);
            Equal((short)875, Kernel.StepThrottleFuelSpeed(s with { Configuration = 0xc080 }).TargetSpeed);
            Equal((short)-88, Kernel.StepThrottleFuelSpeed(Air() with { Configuration = 0xc000, TargetSpeed = -101 }).TargetSpeed);
            Equal((short)969, Kernel.StepThrottleFuelSpeed(s with { Status = 0x11c8, GearEjection = 0x80, Dynamics = 1 }).TargetSpeed);
            Equal((short)750, Kernel.StepThrottleFuelSpeed(s with { Status = 0x11c8, Dynamics = 1 }).TargetSpeed);
        });
        Check("gear and air brake change the actual speed transition", () =>
        {
            var s = Air() with { Throttle = 120, TargetSpeed = 6000, Position = new(0, 0x800000, 0) };
            Equal((short)6042, Kernel.StepThrottleFuelSpeed(s).TargetSpeed);
            Equal((short)6005, Kernel.StepThrottleFuelSpeed(s with { Status = 0x19c8 }).TargetSpeed);
            Equal((short)5940, Kernel.StepThrottleFuelSpeed(s with { Status = 0x19c8, GearEjection = 0 }).TargetSpeed);
        });
        Check("speed-rule inhibit still allows throttle, old-speed selection and gust", () =>
        {
            var s = Air() with
            {
                SimulationFlags = 0, TargetSpeed = 1000, Configuration = 0xc000,
                Keys = DigitalKeys.ThrottleUp, GustAndSinkEnabled = true, IndicatedSpeed = 0
            };
            s = Kernel.StepThrottleFuelSpeed(s);
            Equal((sbyte)61, s.Throttle);
            Equal(1_000_000, s.Fuel);
            Equal((short)875, s.TargetSpeed);
            Equal(Tables.Sink[0], s.Sink);
        });
        Check("sink uses OLD indicated speed; gust disabled means exactly zero", () =>
        {
            var s = Air() with { GustAndSinkEnabled = true, TargetSpeed = 5000, IndicatedSpeed = 0, Gust = 100 };
            var next = Kernel.StepThrottleFuelSpeed(s);
            Equal(Tables.Sink[0], next.Sink);
            Equal((short)99, next.Gust);
            next = Kernel.StepThrottleFuelSpeed(s with { IndicatedSpeed = 4096 });
            Equal(Tables.Sink[30], next.Sink);
            next = Kernel.StepThrottleFuelSpeed(s with { GustAndSinkEnabled = false, Sink = 100 });
            Equal((short)0, next.Sink);
            Equal((short)0, next.Gust);
        });
        Check("signed arithmetic shift and word overflow occur in real propulsion", () =>
        {
            var s = Air() with { GroundFuelFlags = 0x4000, GustAndSinkEnabled = true, TargetSpeed = 32767, Gust = 100 };
            Equal((short)-32670, Kernel.StepThrottleFuelSpeed(s).IndicatedSpeed);
            Equal((short)-9, Kernel.StepThrottleFuelSpeed(s with { Gust = -10, TargetSpeed = 0 }).Gust);
            var words = Tables.GustSineWords.ToArray();
            words[901 - FlightReferenceTables.FirstGustSineIndex] = -16384;
            var alternate = new FlightReferenceKernel(InventedTables.With(Tables, sine: words));
            s.Pitch = -3;
            True(alternate.StepThrottleFuelSpeed(s).Gust != Kernel.StepThrottleFuelSpeed(s).Gust, "Negative pitch must consume supplied adjacent word 901.");
            Kernel.StepThrottleFuelSpeed(s with { Pitch = short.MinValue });
        });
        Check("fuel warnings replace flags, honor equality thresholds and fire only once", () =>
        {
            foreach (var pair in new (int Fuel, int Flag)[]
            {
                (-1,0x40), (0,0x40), (460799,2), (460800,1), (767999,1), (768000,0)
            })
            {
                var s = Kernel.StepControlSurfaces(Air() with { Fuel = pair.Fuel, Warnings = 0x400, Configuration = 8 });
                Equal(0x400 | pair.Flag, s.Warnings);
                if (pair.Flag == 0x40)
                {
                    Equal(0, s.Fuel);
                    True(!s.Afterburner, "New exhaustion warning cuts afterburner.");
                    Equal((byte)2, s.ThrottleSound);
                    s.ThrottleSound = 0;
                    Equal((byte)0, Kernel.StepControlSurfaces(s).ThrottleSound, "warning must not repeat sound");
                }
            }
            Equal(3, Kernel.StepControlSurfaces(Air() with { Fuel = 10, Warnings = 3 }).Warnings);
        });
        Check("invented response tables drive actual pitch/yaw/roll rates", () =>
        {
            var s = Kernel.StepControlSurfaces(Air() with { PitchCommand = 1, RudderCommand = 1, RollCommand = 3, IndicatedSpeed = 1200 });
            Equal((short)8, s.PitchRate);
            Equal((short)5, s.HeadingRate);
            Equal((short)-29, s.RollRate);
            var ai = Kernel.StepControlSurfaces(Air() with { PitchCommand = 1, EntityIndex = 1 });
            Equal((short)12, ai.PitchRate);
            Equal((short)8, Kernel.StepControlSurfaces(Air() with { PitchCommand = 1, EntityIndex = 1, Configuration = 0x100 }).PitchRate);
        });
        Check("ground takeoff pitch uses half index; catapult suppresses steering", () =>
        {
            var s = Ground() with { TargetSpeed = 900, IndicatedSpeed = 900, PitchCommand = -20 };
            Equal((short)-79, Kernel.StepControlSurfaces(s).PitchRate);
            var locked = Kernel.StepControlSurfaces(s with { PitchCommand = 0, RudderCommand = 20, RollCommand = 60, Surface = 8 });
            Equal((short)0, locked.HeadingRate);
            Equal((short)0, locked.RollRate);
            var settle = Kernel.StepControlSurfaces(s with { Pitch = 200, PitchRate = 100 });
            Equal((short)0, settle.PitchRate);
            True((settle.Configuration & 0x40) != 0, "Ground nose-settle bit must be set.");
            var wreck = Kernel.StepControlSurfaces(s with { Pitch = 200, Status = 0x17c8 });
            Equal((byte)0, wreck.GroundSoundVolume, "crashed/dying nose settle must not request the live-aircraft contact sound");
        });
        Check("stall halves targets; ground term blocks nose-up rate", () =>
        {
            var s = Air() with { PitchCommand = -20, GroundTerm = -37, PitchRate = 100 };
            Equal((short)100, Kernel.StepControlSurfaces(s).PitchRate);
            s.GroundTerm = 0;
            s.PitchCommand = 20;
            var normal = Kernel.StepControlSurfaces(s);
            var stalled = Kernel.StepControlSurfaces(s with { Dynamics = 4 });
            True(stalled.PitchRate < normal.PitchRate, "Stall must reduce the real pitch response.");
            Equal(0x80, stalled.Warnings & 0x80);
            Equal(0x20u, stalled.Cues & 0x20);
            Equal(0, Kernel.StepControlSurfaces(stalled with { Fuel = 0 }).Warnings & 0x80);
        });
        Check("zero-decay quirk, negative lag and boosted rate word overflow", () =>
        {
            var s = Kernel.StepControlSurfaces(Air() with { Surface = 2, RudderCommand = 1 });
            Equal((short)-1, s.HeadingRate, "unconditional decay(0)");
            var response = Tables.PlayerResponse.ToArray();
            response[20] = 32767;
            var highGain = new FlightReferenceKernel(InventedTables.With(Tables, player: response));
            s = highGain.StepControlSurfaces(Air() with { PitchCommand = 20, PitchRate = 32767, TargetSpeed = 32767 });
            Equal((short)-32514, s.PitchRate, "actual source-word rate store");
            s = Kernel.StepControlSurfaces(Air() with { PitchRate = -17 });
            Equal((short)-13, s.PitchRate);
        });
        Check("surface stage disabled leaves warnings and all state untouched", () =>
        {
            var s = Air() with { SimulationFlags = 0, Warnings = 0x43, Fuel = -10, PitchRate = 33 };
            Equal(s, Kernel.StepControlSurfaces(s));
        });
        Check("direction-times-speed level/reverse/zero and explicit Q14 bounds", () =>
        {
            Equal(new RawVelocity(0, 0, -1200), Kernel.ComputeTargetVelocity(new(0, 0, 16384), 1200));
            Equal(new RawVelocity(0, 0, 1200), Kernel.ComputeTargetVelocity(new(0, 0, 16384), -1200));
            Equal(new RawVelocity(0, -32768, 0), Kernel.ComputeTargetVelocity(new(0, 16384, 0), -32768));
            Equal(new RawVelocity(0, 32768, 0), Kernel.ComputeTargetVelocity(new(0, -16384, 0), -32768));
            Equal(default(RawVelocity), Kernel.ComputeTargetVelocity(default, 1200));
            Equal(default(RawVelocity), Kernel.ComputeTargetVelocity(new(0, 0, 16384), 0));
            Throws<ArgumentOutOfRangeException>(() => Kernel.ComputeTargetVelocity(new(20000, 0, 0), 1200));
        });
        Check("velocity smoothing thresholds and ground unsmoothed X/Z", () =>
        {
            foreach (var d in new short[] { -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5 })
            {
                var expected = (short)(d >> (Math.Abs((int)d) > 4 ? 2 : Math.Abs((int)d) > 2 ? 1 : 0));
                var result = FlightReferenceKernel.SmoothVelocity(default, new(d, d, d), 0, false);
                Equal(new RawDisplacement(-4 * expected, -4 * expected, -4 * expected), result, $"delta {d}");
            }
            Equal(new RawDisplacement(-400, -80, 400), FlightReferenceKernel.SmoothVelocity(default, new(100, 0, -100), 20, true));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.SmoothVelocity(new(1, 0, 0), default, 0, false));
            Equal(-133672, FlightReferenceKernel.SmoothVelocity(new(0, -131068, 0), new(0, 32768, 0), 650, false).Y);
            Equal(new RawDisplacement(1610612736, 0, 1610612736),
                FlightReferenceKernel.SmoothVelocity(new(int.MinValue, 0, int.MaxValue - 3), default, 0, false));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.SmoothVelocity(default, new(int.MaxValue, 0, 0), 0, false));
        });
        Check("low-speed sink really reduces altitude in a motion slice", () =>
        {
            var s = Kernel.StepThrottleFuelSpeed(Air() with { Throttle = 0, GustAndSinkEnabled = true });
            s.Displacement = FlightReferenceKernel.SmoothVelocity(default, Kernel.ComputeTargetVelocity(new(0, 0, 16384), s.IndicatedSpeed), s.Sink, false);
            var moved = FlightReferenceKernel.AdvanceAirbornePosition(s);
            Equal(s.Position.Y - 4 * Tables.Sink[0], moved.Position.Y);
        });
        Check("airborne advancement applies negative-X/reset-Z and long overflow", () =>
        {
            var s = FlightReferenceKernel.AdvanceAirbornePosition(Air() with { Position = new(1, 100000, 1000), Displacement = new(-2, 0, 20) });
            Equal(new RawPosition(-1, 100000, 20), s.Position);
            Equal((short)-1, s.CellX);
            s = FlightReferenceKernel.AdvanceAirbornePosition(Air() with { Position = new(int.MaxValue - 3, 100000, 1000), Displacement = new(12, 0, -20) });
            Equal(unchecked(int.MaxValue - 3 + 12), s.Position.X);
            Equal(0, s.Position.Z);
            s = FlightReferenceKernel.AdvanceAirbornePosition(Air() with { Position = new(0x1ffffffe, 100000, 0x1ffffffe), Displacement = new(20, 0, 20) });
            Equal(0x1fffffff, s.Position.X);
            Equal(0x1fffffff, s.Position.Z);
        });
        Check("position ceiling, cell refresh and explicit floor/deck refusal", () =>
        {
            var s = FlightReferenceKernel.AdvanceAirbornePosition(Air() with { Position = new(0, 0x7ffff0, 0), Displacement = new(0, 100, 0), SubCell = 0 });
            Equal(0x800000, s.Position.Y);
            Equal((byte)15, s.SubCell);
            Equal((byte)15, s.Neighbour);
            Equal((byte)255, s.CellRefresh);
            Equal(0xb000, FlightReferenceKernel.AdvanceAirbornePosition(Air() with { AircraftClass = 0x15 }).Position.Y);
            Throws<NotSupportedException>(() => FlightReferenceKernel.AdvanceAirbornePosition(Ground()));
            Throws<NotSupportedException>(() => FlightReferenceKernel.AdvanceAirbornePosition(Air() with { Position = new(0, 751, 0), Displacement = new(0, -4, 0) }));
            Throws<NotSupportedException>(() => FlightReferenceKernel.AdvanceAirbornePosition(Air() with { Surface = 0x40 }));
        });
        Check("all external table lengths, ranges and defensive copies are validated", () =>
        {
            Throws<ArgumentException>(() => InventedTables.With(Tables, player: new short[61]));
            Throws<ArgumentException>(() => InventedTables.With(Tables, ai: new short[63]));
            Throws<ArgumentException>(() => InventedTables.With(Tables, gust: new short[30]));
            Throws<ArgumentException>(() => InventedTables.With(Tables, sink: new short[32]));
            Throws<ArgumentException>(() => InventedTables.With(Tables, sine: new short[901]));
            Throws<ArgumentException>(() => InventedTables.With(Tables, length: new short[256]));
            var gust = Tables.Gust.ToArray();
            gust[0] = -1;
            Throws<ArgumentOutOfRangeException>(() => InventedTables.With(Tables, gust: gust));
            var sine = Tables.GustSineWords.ToArray();
            sine[900] = -1; // sine index 0
            Throws<ArgumentException>(() => InventedTables.With(Tables, sine: sine));
            var player = Tables.PlayerResponse.ToArray();
            var copy = InventedTables.With(Tables, player: player);
            player[1] = 9999;
            Equal(Tables.PlayerResponse[1], copy.PlayerResponse[1]);
            Throws<ArgumentNullException>(() => new FlightReferenceKernel(null!));
        });
        Check("invalid raw inputs and unavailable stages fail explicitly", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => Kernel.StepThrottleFuelSpeed(Air() with { Throttle = 121 }));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.ConvertCommands(Air() with { PitchCommand = 21 }));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.ConvertCommands(Air() with { PointerHeight = 961 }));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.ConvertCommands(Air() with { EntityIndex = 16 }));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.ApplyKeyEvents(Air(), new(0, 11)));
            Throws<ArgumentOutOfRangeException>(() => FlightReferenceKernel.ApplyPointerBlank(Air(), 256));
            foreach (var stage in new[] { ReferenceStages.AttitudeComposition, ReferenceStages.GroundContact, ReferenceStages.Collisions, ReferenceStages.Missions, ReferenceStages.FullPlayerStep })
                Throws<NotSupportedException>(() => FlightReferenceKernel.RequireStage(stage));
            FlightReferenceKernel.RequireStage(ReferenceStages.DigitalCommands | ReferenceStages.ThrottleFuelSpeed);
        });
        Check("aircraft high-nibble eligibility and out-of-control gate", () =>
        {
            foreach (var cls in new byte[] { 0x10, 0x11, 0x14, 0x1f })
                True(FlightReferenceKernel.PropulsionEligible(Air() with { AircraftClass = cls }), $"aircraft {cls}");
            foreach (var cls in new byte[] { 0, 0x20, 0x30 })
                Throws<InvalidOperationException>(() => Kernel.StepThrottleFuelSpeed(Air() with { AircraftClass = cls }));
            Throws<InvalidOperationException>(() => Kernel.StepThrottleFuelSpeed(Air() with { Dynamics = 2 }));
        });
        Check("identical inputs have clock-independent one-step state", () =>
        {
            var start = Air() with { Keys = DigitalKeys.StickBack | DigitalKeys.ThrottleUp, GustAndSinkEnabled = true, TargetSpeed = 2000, IndicatedSpeed = 2000 };
            FlightRawState Once() => Kernel.StepControlSurfaces(Kernel.StepThrottleFuelSpeed(FlightReferenceKernel.ConvertCommands(start)));
            var first = Once();
            Thread.Sleep(3);
            Equal(first, Once());
            Equal((sbyte)0, start.PitchCommand, "input value must remain unchanged");
        });
        Check("clarified component edge cases remain regression protected", () =>
        {
            var pointer = Kernel.StepThrottleFuelSpeed(Air() with
            {
                Throttle = 0, Keys = DigitalKeys.ThrottleUp, Repeat = 6,
                PointerEnabled = true, PointerHeight = 960, PointerReference = 500
            });
            Equal((sbyte)3, pointer.Preset, "fire uses distance before following");
            var negativeFuel = Kernel.StepControlSurfaces(Air() with { Fuel = -1, Warnings = 0x40, Configuration = 8 });
            Equal(0, negativeFuel.Fuel);
            True(negativeFuel.Afterburner, "Existing exhausted flag does not repeat afterburner-cut side effect.");
            Equal((byte)0, negativeFuel.ThrottleSound);
            var staleCell = FlightReferenceKernel.RefreshPositionCells(Air() with
            {
                CellX = 0, CellZ = 0, ViewCellX = 0, ViewCellZ = 0,
                Position = new(0x10000000, 100000, 0x10000000), SubCell = 0
            });
            Equal((byte)15, staleCell.Neighbour, "nearness uses pre-refresh cell");
            staleCell.CellRefresh = 0;
            staleCell.Position = new(0x14000000, 100000, 0x10000000);
            Equal((byte)0, FlightReferenceKernel.RefreshPositionCells(staleCell).CellRefresh, "same subcell does not request refresh");
            foreach (var pitch in new short[] { -2912, -2920, -2928, -17088 })
                Throws<NotSupportedException>(() => Kernel.StepThrottleFuelSpeed(Air() with { Pitch = pitch, GustAndSinkEnabled = true }));
        });

        if (args.Length == 4 && args[0] == "--player-oracle" && args[2] == "--original-data")
            Check("original-data complete player differential", () => PlayerOracleProof.Run(args[1], args[3]));
        else if (args.Length == 2 && args[0] == "--oracle")
            Check("pinned native C differential evidence", () => OracleProof.Run(args[1]));
        else if (args.Length == 0)
            Console.WriteLine("DIFFERENTIAL NOT RUN (pass --oracle PATH). No original-image or whole-game parity claim.");
        else { failed++; Console.Error.WriteLine("Usage: [--oracle PATH] or [--player-oracle PATH --original-data PRIVATE_DIRECTORY]"); }

        var qualification = args.Contains("--player-oracle")
            ? "Component regressions plus supplied-x1 original-data player proof; not stock/whole-game certification."
            : "Synthetic tables only; NOT original ADF/stock/whole-game parity.";
        Console.WriteLine($"Checks: {passed} passed, {failed} failed. {qualification}");
        return failed == 0 ? 0 : 1;
    }
}
