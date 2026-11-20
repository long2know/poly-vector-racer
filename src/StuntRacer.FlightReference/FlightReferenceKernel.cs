namespace StuntRacer.FlightReference;

/// <summary>
/// Original-reference COMPONENTS, independently expressed from the pinned
/// functional specification cited in FlightData.cs. Not a complete flight step.
/// The caller controls ordering, eligibility, pause and the frame cadence.
/// In particular: convert commands, propulsion, then surfaces/fuel warnings;
/// attitude composition would be between surfaces and motion and is unavailable.
/// </summary>
public sealed partial class FlightReferenceKernel
{
    public const int AngleUnitsPerCircle = 28800;
    public const int SpeedWordsPerKnot = 12;
    public const int DisplacementPerSpeedWord = 4;
    public const int PositionUnitsPerFootNumerator = 1024;
    public const int PositionUnitsPerFootDenominator = 5;

    public const ReferenceStages SupportedStages =
        ReferenceStages.DigitalCommands | ReferenceStages.ThrottleFuelSpeed |
        ReferenceStages.ControlSurfaceRates | ReferenceStages.TargetVelocity |
        ReferenceStages.VelocitySmoothing | ReferenceStages.AirbornePosition;

    private readonly FlightReferenceTables tables;
    private readonly Func<int, short>? suppliedGustWord;

    public FlightReferenceKernel(FlightReferenceTables tables)
        => this.tables = tables ?? throw new ArgumentNullException(nameof(tables));

    internal FlightReferenceKernel(FlightReferenceTables tables, Func<int, short> suppliedGustWord) : this(tables)
        => this.suppliedGustWord = suppliedGustWord;

    private short GustWord(int index) => suppliedGustWord?.Invoke(index) ?? tables.SineWord(index);

    public static void RequireStage(ReferenceStages stage)
    {
        if (stage == ReferenceStages.None || (stage & ~SupportedStages) != 0)
            throw new NotSupportedException($"Unavailable original-reference stage: {stage}. No full-player-step implementation is supplied.");
    }

    // Narrowing at source word stores is intentional, including in checked callers.
    private static short Word(int value) => unchecked((short)value);
    private static sbyte Byte(int value) => unchecked((sbyte)value);
    private static short Lag(short value, int target, int shift)
        => Word(value - ((value - target) >> shift));
    private static short Slew(short value, int threshold, int shift)
        => Math.Abs((int)value) > threshold ? Word(value - (value >> shift)) : (short)0;
    private static bool Viewed(FlightRawState s) => s.ViewedEntityIndex == s.EntityIndex;

    private static void Validate(FlightRawState s)
    {
        if (s.Throttle is < -32 or > 120)
            throw new ArgumentOutOfRangeException(nameof(s), "Throttle must be -32..120.");
        if (s.PitchCommand is < -20 or > 20 || s.RudderCommand is < -20 or > 20 ||
            s.RollCommand is < -60 or > 60)
            throw new ArgumentOutOfRangeException(nameof(s), "Digital command bytes are outside their source ranges.");
        if (s.PointerHeight is < -960 or > 960 || s.PointerReference is < -960 or > 960)
            throw new ArgumentOutOfRangeException(nameof(s), "Pointer words must be -960..960.");
        if (s.EntityIndex > 15 || s.ViewedEntityIndex > 15)
            throw new ArgumentOutOfRangeException(nameof(s), "Entity indices must be 0..15.");
    }

    /// <summary>Section 2 key-handler events only; no gear animation, recorder or ejection entities.</summary>
    public static FlightRawState ApplyKeyEvents(FlightRawState state, FlightInput input)
    {
        Validate(state);
        if (input.FunctionKey is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(input), "FunctionKey must be 0 (none) or 1..10.");
        state.Keys = input.Keys;
        if (input.FunctionKey != 0)
        {
            state.Preset = input.FunctionKey == 10 ? (sbyte)121
                : input.FunctionKey == 1 && state.Throttle == 12 ? (sbyte)-1
                : (sbyte)(input.FunctionKey * 12);
            state.PointerHeight = state.PointerReference =
                (short)Math.Clamp(state.Preset * 8, 0, 960);
        }
        if (input.ToggleGear && !state.OnGround) state.GearEjection ^= 0x80;
        if (input.ToggleHook && state.AircraftClass == 0x11)
        {
            state.Configuration ^= 0x8000;
            state.HookLamp ^= 0x80;
        }
        if (input.ToggleBrake)
        {
            state.Status ^= 0x800;
            state.PointerHeight = state.PointerReference = 0;
            state.Preset = 0;
        }
        if (input.Eject) state.GearEjection = (byte)((state.GearEjection & 0xf0) | 10);
        return state;
    }

    /// <summary>
    /// Original pointer interrupt rule for ONE vertical blank, not an aggregated
    /// per-step approximation. The counter delta is wrapped to a signed byte.
    /// Works independently of pause. No wall clock is read.
    /// </summary>
    public static FlightRawState ApplyPointerBlank(FlightRawState state, int counterDelta)
    {
        Validate(state);
        if (counterDelta is < -255 or > 255)
            throw new ArgumentOutOfRangeException(nameof(counterDelta), "Supply the difference of two byte counters.");
        var delta = (int)Byte(counterDelta);
        if (!state.PointerEnabled && delta < 0) delta >>= 1;
        state.PointerHeight = (short)Math.Clamp(state.PointerHeight - delta, -960, 960);
        return state;
    }

    public static FlightRawState ConvertCommands(FlightRawState state)
    {
        Validate(state);
        if ((state.GearEjection & 15) != 0) return state;

        if (state.Preset != 0)
        {
            var requested = Math.Max((int)state.Preset, 0);
            var delta = Byte(requested - state.Throttle);
            var bits = 0;
            if (requested > state.Throttle)
                bits = delta > 8 || (state.Repeat & 15) < 2 ? 1 : 0;
            else if (requested < state.Throttle)
                bits = delta < -8 || (state.Repeat & 15) < 2 ? 2 : 0;
            else if (state.Throttle < 120 || state.Afterburner)
                state.Preset = 0;
            else
                bits = (byte)state.Keys & 3; // equal at military power: leave the held bits
            state.Keys = (DigitalKeys)(((byte)state.Keys & 0xfc) | bits);
        }
        if ((state.Surface & 0x10) != 0)
        {
            if (state.Counter >= 0)
            {
                state.PitchCommand = state.RudderCommand = state.RollCommand = 0;
                return state;
            }
            state.Surface &= 0xef;
        }

        state.PitchCommand = Ramp(state.PitchCommand, ((byte)state.Keys >> 4) & 3, 1, 20);
        state.RudderCommand = Ramp(state.RudderCommand, ((byte)state.Keys >> 6) & 3, 1, 20);
        state.RollCommand = Ramp(state.RollCommand, ((byte)state.Keys >> 2) & 3, 3, 60);
        return state;
    }

    private static sbyte Ramp(sbyte old, int pair, int increment, int limit) => pair switch
    {
        0 => 0,
        1 => (sbyte)Math.Min(old < 0 ? increment : old + increment, limit),
        _ => (sbyte)Math.Max(old > 0 ? -increment : old - increment, -limit)
    };

    /// <summary>Eligibility at the section 1 call site; not a claim that the rest of the player step is implemented.</summary>
    public static bool PropulsionEligible(FlightRawState state) =>
        (state.AircraftClass & 0xf0) == 0x10 && (state.Dynamics & 2) == 0 && (state.Status & 0x1000) != 0;

    /// <summary>
    /// Sections 3–4 routine, including its internal simulation/fuel-inhibit gate.
    /// Call only when PropulsionEligible and the caller's pause/view gates allow.
    /// Does not silently clamp negative post-burn fuel: the surfaces' once-only
    /// warning rule owns that write, exactly as specified.
    /// </summary>
    public FlightRawState StepThrottleFuelSpeed(FlightRawState state)
    {
        Validate(state);
        if (!PropulsionEligible(state))
            throw new InvalidOperationException("The source call-site conditions for the propulsion routine are not satisfied.");
        var oldSpeed = state.TargetSpeed;
        var oldIndicated = state.IndicatedSpeed;
        Throttle(ref state);
        var candidate = (int)oldSpeed;
        var command = Word(state.Throttle * 256);
        var drag = Drag(state);

        if (state.SimulationRunning && (state.GroundFuelFlags & 0x4000) == 0)
        {
            if (state.Fuel == 0 || (command == 0 && !state.Afterburner))
            {
                if (state.OnGround)
                {
                    state.GroundTerm = Lag(state.GroundTerm, 0, 3);
                    candidate = Math.Abs((int)oldSpeed) <= 15 ? 0
                        : oldSpeed < 0 ? oldSpeed + 15 : oldSpeed - 15;
                }
                else if (oldSpeed < 0)
                {
                    var magnitude = -(int)oldSpeed;
                    candidate = magnitude > 0x90 ? oldSpeed + 0x52
                        : magnitude > 0x3c ? oldSpeed + 0x3c : 0;
                }
                else
                {
                    if (oldSpeed > 0x1d4c) candidate -= 0x52;
                    if (oldSpeed < 0x5dc)
                    {
                        if (oldSpeed > 0x1e) candidate -= 0x1e;
                    }
                    else candidate = oldSpeed > 0x3c ? candidate - 0x3c : 0;
                }
            }
            else if (state.Afterburner)
            {
                state.Fuel = unchecked(state.Fuel - 0x600);
                var target = Math.Min(0x3a98 - drag, 0x4650);
                if (state.GearFullyDown) target -= target >> 2;
                if ((state.Status & 0x800) != 0) target -= target >> 2;
                if (oldSpeed < target)
                {
                    if (oldSpeed < 0) candidate += 0x78;
                    else if (!state.OnGround) candidate += oldSpeed > 0x1d4c ? 0x2d : 0x36;
                    else
                    {
                        state.GroundTerm = Lag(state.GroundTerm, -37, 3);
                        candidate -= state.GroundTerm;
                    }
                }
                else if (oldSpeed > target + 0x3c) candidate -= 0x3c;
            }
            else
            {
                var thrust = Math.Abs((int)command);
                state.Fuel = unchecked(state.Fuel - (thrust >> 9));
                if (state.OnGround)
                {
                    var rate = 0;
                    int target;
                    if (command >= 0)
                    {
                        target = command >> 3;
                        target -= target >> 2;
                        if ((state.Surface & 8) == 0 && state.Throttle < 12) target = 0;
                        if ((state.Surface & 0x44) == 0) target >>= 1;
                        else if ((state.Dynamics & 1) != 0) target = 0;
                        if (oldSpeed < target)
                            rate = oldSpeed < 0 ? -0x78
                                : -(((target - oldSpeed) >> 8) + ((state.Surface & 8) != 0 ? 0x42 : 0x16));
                        else if (oldSpeed > target + 15) rate = 15;
                    }
                    else
                    {
                        target = thrust >> 4;
                        target -= target >> 2;
                        if ((state.Surface & 4) == 0) target >>= 1;
                        if (oldSpeed > 0) rate = 0x3c;
                        else if (-oldSpeed > target) rate = -22;
                        else if (-oldSpeed < target - 22) rate = 22;
                    }
                    state.GroundTerm = Lag(state.GroundTerm, rate, 3);
                    if (rate != 0) candidate -= state.GroundTerm;
                }
                else if (command < 0)
                {
                    var target = thrust - (thrust >> 2);
                    if (oldSpeed > 0) candidate -= 0x57;
                    else if (-oldSpeed > target) candidate += 0x2a;
                    else if (-oldSpeed < target - 0x2a) candidate -= 0x2a;
                }
                else
                {
                    var a = (command >> 1) - (command >> 3);
                    var target = a - (a >> 2);
                    if ((state.Dynamics & 1) != 0) target >>= 2;
                    target = Math.Min(target, 0x1fef) - drag;
                    if ((state.Status & 0x800) != 0) target -= target >> 2;
                    if (state.GearFullyDown) target -= target >> 2;
                    if (oldSpeed < target)
                        candidate = Math.Min(target, candidate + (oldSpeed < 0 ? 0x78 : target - oldSpeed < 0x150 ? 5 : 0x2a));
                    else if (oldSpeed > target + 0x3c)
                        candidate -= oldSpeed > 0x1d4c ? 0x52 : oldSpeed - target < 0x1e0 ? 7 : 0x3c;
                }
            }
        }

        candidate = Word(candidate);
        // These are this routine's warning outputs. Propagating/discarding a
        // non-player record's global outputs belongs to the omitted world loop.
        if (candidate >= 0x20d0 && (state.FlightWarningFlags & 0x4000) == 0)
        {
            state.FlightWarningFlags |= 0x4000;
            state.MotionSound = 3;
            state.CentreNotice = 0xfa;
            state.NoticeDuration = 2;
        }
        else if (candidate < 0x20d0) state.FlightWarningFlags &= 0xbfff;

        state.TargetSpeed = (state.Configuration & 0xc000) == 0xc000 ? Slew(oldSpeed, 7, 3)
            : state.OnGround && (state.Status & 0x800) != 0 && oldSpeed > 0x40 ? Slew(oldSpeed, 15, 4)
            : state.OnGround && (state.GearEjection & 0x80) != 0 ? Slew(oldSpeed, 31, 5)
            : (state.Dynamics & 1) != 0 ? Slew(oldSpeed, 3, 2)
            : (state.Surface & 8) != 0 && Math.Abs((int)state.Throttle) < 108 ? Slew(oldSpeed, 7, 3)
            : Word(candidate);

        if (!state.GustAndSinkEnabled) state.Gust = state.Sink = 0;
        else
        {
            var coefficient = tables.Gust[Math.Min(Math.Abs((int)state.TargetSpeed) >> 7, 30)];
            var p = state.Pitch >> 3;
            var factor = p <= 1800 ? 256 - (GustWord(900 - p) >> 6)
                : (GustWord(p - 2700) >> 6) - 256;
            var target = Word((coefficient * factor) >> 3);
            if ((state.GearEjection & 0x80) == 0) target = Word(target - (target >> 2));
            if (state.OnGround) state.Gust = 0;
            else if (state.Gust > target)
            {
                var delta = state.Gust - target;
                state.Gust = delta > 255 ? Word(state.Gust - (delta >> 8))
                    : state.Gust > 0 ? Word(state.Gust - 1) : (short)0;
            }
            else state.Gust = Lag(state.Gust, target, 5);
            state.Sink = tables.Sink[Math.Min(Math.Abs((int)oldIndicated) >> 7, 30)];
        }
        state.IndicatedSpeed = Word(state.TargetSpeed + state.Gust);
        return state;
    }

    private static short Drag(FlightRawState s)
    {
        if (s.TargetSpeed < 0x800) return 0;
        var pitch = (int)s.Pitch;
        var bank = (int)s.Roll;
        if (pitch >= 14400) pitch -= AngleUnitsPerCircle;
        if (bank >= 14400) bank = AngleUnitsPerCircle - bank;
        if (bank >= 7200) bank = 14400 - bank;
        var density = unchecked(s.AltitudeCeiling - s.Position.Y) >> (s.Afterburner ? 11 : 13);
        return Word(2 * Math.Abs((int)s.PitchRate) + (bank >> 3) - (pitch >> 1) + density);
    }

    private static void Throttle(ref FlightRawState s)
    {
        if (s.Fuel == 0)
        {
            s.Throttle = 0;
            s.Configuration &= 0xfff7;
            if (s.EntityIndex == 0 && s.PointerEnabled) s.PointerHeight = s.PointerReference = 0;
            return;
        }
        var heldAtEntry = (byte)s.Keys & 3;
        var repeat = heldAtEntry == 0 ? 1 : Math.Min((s.Repeat & 15) + 1, 8);
        s.Repeat = (byte)((s.Repeat & 0xf0) | repeat);
        var step = repeat >> 1;
        if (heldAtEntry != 0 && step != 0 && s.Throttle == 0 && Viewed(s)) s.CentreNotice = 0xfe;

        if (heldAtEntry == 1)
        {
            s.Configuration &= 0xfbff;
            var next = s.Throttle + step;
            if (next >= 120)
            {
                if ((s.Configuration & 0x2000) != 0)
                {
                    if (!s.Afterburner)
                    {
                        s.Configuration |= 8;
                        s.ThrottleSound = 2;
                        if (Viewed(s))
                        {
                            s.CentreNotice = 0xfd;
                            s.NoticeDuration = 6;
                        }
                    }
                }
                else
                {
                    s.Configuration |= 0x2000;
                    s.Keys = (DigitalKeys)((byte)s.Keys & 0xfc);
                    s.Preset = 0;
                }
                next = 120;
            }
            s.Throttle = Byte(next);
        }
        else if (heldAtEntry == 2)
        {
            s.Configuration &= 0xdfff;
            var next = (int)s.Throttle;
            if (s.Afterburner)
            {
                s.Configuration &= 0xfff7;
                s.ThrottleSound = 2;
                if (Viewed(s)) s.CentreNotice = 0xfc;
                next--;
            }
            else if (next >= 0)
            {
                next -= 4;
                if (next < 0)
                {
                    if ((s.Surface & 8) != 0) next = 0;
                    else if ((s.Configuration & 0x400) == 0)
                    {
                        next = 0;
                        s.Configuration |= 0x400;
                        s.Keys = (DigitalKeys)((byte)s.Keys & 0xfc);
                    }
                }
            }
            else next = Math.Max(next - step, -32);
            s.Throttle = Byte(Math.Max(next, -32));
        }
        if (s.EntityIndex == 0 && s.PointerEnabled) PointerRules(ref s, heldAtEntry != 0);
    }

    private static void PointerRules(ref FlightRawState s, bool keyAtEntry)
    {
        var entryDistance = Math.Abs(s.PointerHeight - s.PointerReference);
        if (keyAtEntry && (s.PointerHeight >= 960 || entryDistance < 16))
        {
            s.PointerReference = (short)(Math.Max((int)s.Throttle, 0) * 8);
            if (s.PointerHeight >= 0) s.PointerHeight = s.PointerReference;
        }
        if (s.DisplayMode == 2 || (s.PointerHeight < 960 && entryDistance <= 16))
            return;
        if (s.PointerHeight < 0)
        {
            s.PointerHeight = 0;
            s.Preset = -1;
        }
        else if (s.Afterburner)
        {
            if (s.PointerHeight < 864) s.Preset = Byte(s.PointerHeight >> 3);
        }
        else if (s.PointerHeight != 0)
        {
            if (s.Throttle == 0 && Viewed(s)) s.CentreNotice = 0xfe;
            s.Preset = Byte(s.PointerHeight >> 3);
        }
        s.PointerReference = s.PointerHeight;
    }

    public FlightRawState StepControlSurfaces(FlightRawState state)
    {
        Validate(state);
        if (!state.SimulationRunning) return state;
        if ((state.AircraftClass & 0xf0) != 0x10)
            throw new InvalidOperationException("This bounded surface-rate component supports aircraft classes only.");
        state.Configuration &= 0xffbf;
        FuelWarnings(ref state);
        var response = state.EntityIndex == 0 || (state.Configuration & 0x100) != 0
            ? tables.PlayerResponse : tables.AiResponse;
        var pitch = state.PitchCommand;
        var rudder = (state.Surface & 8) != 0 ? 0 : (int)state.RudderCommand;
        var roll = (state.Surface & 8) != 0 ? 0 : (int)state.RollCommand;
        if (pitch != 0)
        {
            if (!state.OnGround) PitchRate(ref state, Target(response, pitch));
            else if ((state.Dynamics & 1) != 0 || state.TargetSpeed <= 0x300 ||
                     (state.Pitch != 0 && state.Pitch <= 0x6d60))
            {
                SettleNose(ref state);
                ResetRoll(ref state);
            }
            else if (pitch < 0) PitchRate(ref state, -response[Math.Abs((int)pitch) >> 1]);
            else
            {
                SettleNose(ref state);
                ResetRoll(ref state);
            }
        }
        else if (state.OnGround) SettleNose(ref state);
        else if (state.PitchRate != 0 && state.GroundTerm == 0)
            state.PitchRate = Decay(state.PitchRate, state.EntityIndex == 0 ? 2 : 1);

        if (rudder != 0)
        {
            if ((state.Surface & 2) == 0 && state.IndicatedSpeed != 0)
                YawRate(ref state, Target(response, rudder));
            else state.HeadingRate = Decay(state.HeadingRate, 1);
        }
        else if (!(roll != 0 && state.OnGround) && state.HeadingRate != 0)
            state.HeadingRate = Decay(state.HeadingRate, 1);

        if (roll != 0)
        {
            if (state.OnGround) roll = Math.Clamp(roll, -10, 10);
            var target = Target(response, roll);
            if (!state.OnGround) RollRate(ref state, -target);
            else if ((state.Surface & 2) == 0 && state.IndicatedSpeed != 0)
            {
                YawRate(ref state, target);
                var nearLevel = state.Roll < 0x50 || state.Roll > 0x7030;
                if ((nearLevel && state.IndicatedSpeed > 0x360) ||
                    (state.Roll < 0x3840 ? roll >= 0 : roll <= 0))
                    RollRate(ref state, -target);
                else state.RollRate = 0;
            }
            else
            {
                state.HeadingRate = Decay(state.HeadingRate, 1);
                state.RollRate = Decay(state.RollRate, 1);
            }
        }
        else
        {
            if (state.OnGround) ResetRoll(ref state);
            if (state.RollRate != 0) state.RollRate = Decay(state.RollRate, 1);
        }

        if (state.Fuel != 0 && (state.Dynamics & 4) != 0 && !state.OnGround)
        {
            if ((state.Warnings & 0x80) == 0)
            {
                state.Warnings |= 0x80;
                state.Cues |= 0x20;
            }
            if (state.StallLampCounter < 0) state.StallLampCounter = 2;
        }
        else state.Warnings &= ~0x80;
        return state;
    }

    private static void FuelWarnings(ref FlightRawState s)
    {
        var flag = s.Fuel <= 0 ? 0x40 : s.Fuel < 0x70800 ? 2 : s.Fuel < 0xbb800 ? 1 : 0;
        // Observed pinned-component clarification: normalization of the fuel
        // long precedes the once-only warning effects (see checks/EVIDENCE.md).
        if (s.Fuel < 0) s.Fuel = 0;
        if (flag != 0 && (s.Warnings & flag) != 0) return;
        s.Warnings = (s.Warnings & ~0x43) | flag;
        if (flag == 0x40)
        {
            s.Fuel = 0;
            s.Configuration &= 0xfff7;
            s.ThrottleSound = 2;
        }
    }

    private static int Target(ReadOnlySpan<short> response, int command)
        => command < 0 ? -response[-command] : response[command];
    private static short Decay(short value, int shift) => value < -15
        ? Word(value + ((-(int)value) >> shift))
        : value > 15 ? Word(value - (value >> shift)) : Word(value + (value < 0 ? 1 : -1));
    private static short Boost(short rate, short speed)
        => Word(rate + (rate >= 0x500 ? speed >> 7 : rate <= -0x500 ? -(speed >> 7) : 0));
    private static void PitchRate(ref FlightRawState s, int target)
    {
        if (!s.OnGround && s.GroundTerm != 0 && target <= 0) return;
        if ((s.Dynamics & 4) != 0) target >>= 1;
        s.PitchRate = Boost(Lag(s.PitchRate, target, 2), s.TargetSpeed);
    }
    private static int YawTarget(FlightRawState s, int target)
    {
        var result = (target >> 1) + (target >> 3);
        return (s.Dynamics & 4) != 0 ? result >> 1 : result;
    }
    private static void YawRate(ref FlightRawState s, int target)
        => s.HeadingRate = Boost(Lag(s.HeadingRate, YawTarget(s, target), 2), s.TargetSpeed);
    private static void RollRate(ref FlightRawState s, int target)
        => s.RollRate = Boost(Lag(s.RollRate, YawTarget(s, target), s.AircraftClass == 0x14 ? 2 : 1), s.TargetSpeed);
    private static void SettleNose(ref FlightRawState s)
    {
        if (s.Pitch <= 0) return;
        if (s.Pitch < 0x190)
        {
            if ((s.Configuration & 0x40) == 0 && (s.Status & 0x1600) == 0x1000 && (s.Surface & 2) == 0)
                s.GroundSoundVolume = 15;
            s.Configuration |= 0x40;
            s.PitchRate = 0;
        }
        else s.PitchRate = Lag(s.PitchRate, s.Pitch < 0x3840 ? -0x40 : 0x40, s.TargetSpeed >= 0x6c0 ? 1 : 0);
    }
    private static void ResetRoll(ref FlightRawState s)
        => s.RollRate = s.Roll == 0 ? (short)0 : s.Roll > 0x3840 ? (short)0x20 : (short)-0x20;
}
