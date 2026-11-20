namespace StuntRacer.FlightReference;

public readonly record struct FlightAngles(short Pitch, short Heading, short Roll);

/// <summary>
/// Single-player aircraft rules independently expressed from the pinned
/// functional notes. Original tables/environment are explicit; no Modern model
/// is referenced. Frame/mission dispatch and rendering are not this class.
/// </summary>
public sealed partial class FlightPlayerKernel
{
    /// <summary>Validated aircraft-only Free Flight with the explicitly supplied scene/draw snapshot; no combat/AI/mission dispatcher.</summary>
    public const ReferenceStages SupportedStages = FlightReferenceKernel.SupportedStages |
        ReferenceStages.AttitudeComposition | ReferenceStages.GroundContact |
        ReferenceStages.Collisions | ReferenceStages.FullPlayerStep;

    public static void RequireStage(ReferenceStages stage)
    {
        if (stage == ReferenceStages.None || (stage & ~SupportedStages) != 0)
            throw new NotSupportedException($"Not an aircraft Free Flight stage: {stage}.");
    }

    private readonly FlightOriginalData data;
    private readonly FlightReferenceKernel components;
    public FlightPlayerKernel(FlightOriginalData data)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        components = new(data.ComponentTables);
    }

    private static short W(int value) => unchecked((short)value);
    private static int L(long value) => unchecked((int)value);
    private static int Abs(int value) => value < 0 ? unchecked(-value) : value;
    private static short NegAngle(short value) => value == 0 ? (short)0 : W(28800 - value);
    private static int RoundShift(int value, int bits) => (value >> bits) + ((value >> (bits - 1)) & 1);
    private static short Difference(short a, short b) => W(Math.Abs((int)a - b));

    private short ImageWord(int address, ref FlightPlayerState state, FlightEnvironment? environment)
    {
        var text = (address <= 0x440cc && address + 1 >= 0x440c9) ||
                   (address <= 0x44ea1 && address + 1 >= 0x44ea1);
        if (!text && ((address >= 0x3249a && address + 1 < 0x325b8) ||
                      (address >= 0x3ae92 && address + 1 < 0x49a12)))
            return data.ReferenceWord(address);
        short? live = address switch
        {
            0x4a7b2 => W(state.Raw.AltitudeCeiling >> 16),
            0x4a7b4 => W(state.Raw.AltitudeCeiling),
            0x4a982 => W(state.LandingFlags),
            0x4a984 => W(state.Raw.SimulationFlags),
            0x4a986 => W(state.Raw.GroundFuelFlags),
            0x4a98a => W(state.Raw.FlightWarningFlags),
            0x4a994 => W(state.Raw.ViewedEntityIndex),
            0x4a996 => W(state.Raw.ViewedEntityIndex << 9),
            0x4a9fe => state.CoupledPitch,
            0x4aa04 => state.Raw.ViewCellX,
            0x4aa06 => state.Raw.ViewCellZ,
            0x4ab98 => W(state.MessageCode),
            0x4ac08 => W(state.Raw.Warnings >> 16),
            0x4ac0a => W(state.Raw.Warnings),
            0x4ac0c => W((int)(state.Raw.Cues >> 16)),
            0x4ac0e => W((int)state.Raw.Cues),
            _ => environment?.RuntimeWord?.Invoke(address)
        };
        if (!live.HasValue)
            throw new NotSupportedException($"Live original word 0x{address:x} is required; a static image/zero substitute is not sufficient.");
        state.DynamicReadCount++;
        return live.Value;
    }

    private (short Sin, short Cos) SinCos(int tenthDegrees, ref FlightPlayerState state, FlightEnvironment? environment)
    {
        int si, ci, ss, cs;
        if (tenthDegrees <= 900) { si = tenthDegrees; ci = 900 - tenthDegrees; ss = cs = 1; }
        else if (tenthDegrees <= 1800) { si = 1800 - tenthDegrees; ci = tenthDegrees - 900; ss = 1; cs = -1; }
        else if (tenthDegrees <= 2700) { si = tenthDegrees - 1800; ci = 2700 - tenthDegrees; ss = cs = -1; }
        else { si = 3600 - tenthDegrees; ci = tenthDegrees - 2700; ss = -1; cs = 1; }
        return (W(ss * ImageWord(0x436e8 + 2 * si, ref state, environment)),
            W(cs * ImageWord(0x436e8 + 2 * ci, ref state, environment)));
    }

    private FlightMatrix Matrix(short a, short b, short c, bool second,
        ref FlightPlayerState state, FlightEnvironment? environment)
    {
        var (sa, ca) = SinCos((ushort)a >> 3, ref state, environment);
        var (sb, cb) = SinCos((ushort)b >> 3, ref state, environment);
        var (sc, cc) = SinCos((ushort)c >> 3, ref state, environment);
        static short Q(short x, short y) => W((x * y) >> 14);
        static short Sum(int p, int q) => W(unchecked(p + q) >> 14);
        static short Sub(int p, int q) => W(unchecked(p - q) >> 14);
        if (!second)
        {
            var bs = Q(sb, sa);
            var cs = Q(cb, sa);
            return new(
                Sum(bs * sc, cb * cc), W(-Q(ca, sc)), Sub(sb * cc, cs * sc),
                Sub(cb * sc, bs * cc), Q(ca, cc), Sum(cs * cc, sb * sc),
                W(-Q(sb, ca)), W(-sa), Q(cb, ca));
        }
        var t = Q(sc, sa);
        var u = Q(cc, sa);
        return new(
            Sub(cc * cb, t * sb), W(-Sum(sc * cb, u * sb)), Q(ca, sb),
            Q(sc, ca), Q(cc, ca), sa,
            W(-Sum(cc * sb, t * cb)), Sub(sc * sb, u * cb), Q(ca, cb));
    }

    public FlightMatrix BuildMatrixA(short pitch, short heading, short roll)
    {
        var scratch = default(FlightPlayerState);
        return Matrix(pitch, heading, roll, false, ref scratch, null);
    }
    public FlightMatrix BuildMatrixB(short a, short b, short c)
    {
        var scratch = default(FlightPlayerState);
        return Matrix(a, b, c, true, ref scratch, null);
    }
    public FlightMatrix BuildInverseAttitudeMatrix(short pitch, short heading, short roll)
        => BuildMatrixB(NegAngle(pitch), NegAngle(heading), NegAngle(roll));

    public FlightPlayerState SetAttitude(FlightPlayerState state, short pitch, short heading, short roll)
    {
        state.Raw = state.Raw with { Pitch = pitch, Heading = heading, Roll = roll };
        RebuildMatrices(ref state, null);
        return state;
    }
    private void RebuildMatrices(ref FlightPlayerState state, FlightEnvironment? environment)
    {
        var r = state.Raw;
        state.MatrixA = Matrix(r.Pitch, r.Heading, r.Roll, false, ref state, environment);
        state.MatrixB = Matrix(NegAngle(r.Pitch), NegAngle(r.Heading), NegAngle(r.Roll), true, ref state, environment);
    }

    private static short Quotient(int numerator, ref short divisor)
    {
        var round = divisor > 0x147;
        if (round) numerator >>= 6;
        else divisor = W(divisor * 64);
        if (divisor == 0) throw new NotSupportedException("The original composition quotient would divide by zero.");
        var wide = (long)numerator / divisor;
        var fits = wide is >= short.MinValue and <= short.MaxValue;
        var q = fits ? W((int)wide) : W(numerator);
        if (round)
        {
            var remainder = fits ? numerator % divisor : W(numerator >> 16);
            if (Math.Abs(remainder) >= (Math.Abs((int)divisor) >> 1))
                q = W(q + (q < 0 ? -1 : 1));
        }
        return q;
    }

    private short AsinOffset(short quotient, ref FlightPlayerState state, FlightEnvironment? environment)
        => ImageWord(0x42e92 + W(2 * Math.Abs((int)quotient)), ref state, environment);

    private FlightAngles Compose(FlightMatrix initial, int pitchRate, int headingRate, int rollRate,
        ref FlightPlayerState state, FlightEnvironment? environment)
    {
        static short RateAngle(int value) => W(value < 0 ? value + 28800 : value);
        var delta = Matrix(RateAngle(pitchRate), RateAngle(headingRate), RateAngle(rollRate), false, ref state, environment);
        Span<int> product = stackalloc int[9];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                product[row * 3 + col] = unchecked(initial[col] * delta[row * 3] +
                    initial[3 + col] * delta[row * 3 + 1] + initial[6 + col] * delta[row * 3 + 2]);
        var sine = unchecked(-product[7]);
        var mag = sine < 0 ? unchecked(0u - (uint)sine) : (uint)sine;
        int index;
        int table;
        if (mag < 0xe210000) { index = RoundShift(sine, 20); table = 0x42e92; }
        else if (mag < 0xff60000)
        {
            index = Math.Min((int)((mag >> 16) + ((mag >> 15) & 1)) - 0xddb, 0x225);
            if (sine < 0) index = -index;
            table = 0x43098;
        }
        else
        {
            index = Math.Min((int)((mag >> 12) + ((mag >> 11) & 1)) - 0xff08, 0xf8);
            if (sine < 0) index = -index;
            table = 0x434e8;
        }
        var pitchValue = ImageWord(table + 2 * Math.Abs(index), ref state, environment);
        var pitch = index < 0 ? 3600 - pitchValue : pitchValue;
        var divisor = ImageWord(0x436e8 + 2 * (900 - pitchValue), ref state, environment);
        if (divisor < 0x8f) { pitch = index < 0 ? 2690 : 910; divisor = -286; }

        short Axis(int negativeSine, int cosine, ref short sharedDivisor,
            ref FlightPlayerState targetState)
        {
            var first = Quotient(unchecked(-negativeSine), ref sharedDivisor);
            var offset = W(2 * Math.Abs((int)first));
            short angle = offset <= 0x180
                ? AsinOffset(first, ref targetState, environment)
                : W(900 - AsinOffset(Quotient(cosine, ref sharedDivisor), ref targetState, environment));
            if (first < 0) angle = W(3600 - angle);
            if (negativeSine >= 0 && cosine < 0) angle = W((angle > 1800 ? 5400 : 1800) - angle);
            else if (negativeSine < 0 && cosine < 0) angle = W(1800 - angle);
            return W(Math.Abs(angle) * 8);
        }
        var heading = Axis(product[6], product[8], ref divisor, ref state);
        var roll = Axis(product[1], product[4], ref divisor, ref state);
        return new(W(pitch * 8), heading, roll);
    }

    public FlightAngles ComposeAttitude(FlightMatrix initial, short pitchRate, short headingRate, short rollRate)
    {
        var scratch = default(FlightPlayerState);
        return Compose(initial, pitchRate, headingRate, rollRate, ref scratch, null);
    }

    private void Couple(ref FlightPlayerState state, ref int pitchRate, ref int headingRate, FlightEnvironment? environment)
    {
        var r = state.Raw.Roll >> 3;
        var inverted = r is >= 900 and < 2700;
        var angle = r < 900 ? r : r < 1800 ? 1800 - r : r < 2700 ? 3600 - r : r - 1800;
        var (sine, cosine) = SinCos(angle, ref state, environment);
        if (inverted) cosine = W(-cosine);
        state.RollSine = sine;
        state.RollCosine = cosine;
        var phase = inverted ? (short)-1 : (short)0;
        if ((state.TurnState == 0) != (phase == 0)) state.TurnFollower = W(-state.TurnFollower);
        state.TurnState = phase;
        var target = Math.Abs((int)sine) * (cosine < 0 ? -1 : 1);
        var error = state.TurnFollower - target;
        var change = Math.Abs(error) > 7 ? error >> 3 : Math.Sign(error);
        state.TurnFollower = W(state.TurnFollower - change);
        var follower = state.TurnFollower;
        if ((follower < 0 && cosine < 0) || (follower >= 0 && cosine > 0)) state.TurnSign = phase;
        else if (((follower < 0 && cosine >= 256) || (follower >= 0 && cosine <= -256)) && phase == state.TurnSign)
        {
            sine = W(-sine);
            cosine = W(-cosine);
        }
        var foldedPitch = (int)state.Raw.Pitch;
        if (foldedPitch >= 14400) foldedPitch = 28800 - foldedPitch;
        if (foldedPitch > 3600) { state.CoupledPitch = 0; return; }
        var magnitude = Math.Min(Math.Abs((int)follower), state.TurnLimit);
        magnitude -= magnitude >> 2;
        var p = ((-(sine << 7)) >> 13) * magnitude >> 13;
        var h = ((-(cosine << 7)) >> 13) * magnitude >> 13;
        var scale = ImageWord(0x3259a + 2 * (foldedPitch >> 8), ref state, environment);
        p = W((p * scale) >> 8);
        h = W((h * scale) >> 8);
        pitchRate = W(pitchRate + p);
        headingRate = W(headingRate + h);
        state.CoupledPitch = W(p);
    }

    private static short Recentre(short roll)
    {
        var angle = (int)roll;
        var destination = angle < 7200 ? 0 : angle < 21600 ? 14400 : 28800;
        var distance = destination - angle;
        var amount = Math.Min(Math.Abs(distance) >> 1, 80);
        if (distance != 0 && amount == 0) amount = 1;
        var result = angle + Math.Sign(distance) * amount;
        return result >= 28800 ? (short)0 : W(result);
    }

    public FlightPlayerState StepAttitude(FlightPlayerState state, FlightEnvironment? environment = null)
    {
        var old = state.Raw;
        if (old.SimulationRunning) state.Raw = components.StepControlSurfaces(state.Raw);
        int p = state.FreezeSurfaces ? 0 : state.Raw.PitchRate,
            h = state.FreezeSurfaces ? 0 : state.Raw.HeadingRate,
            r = state.FreezeSurfaces ? 0 : state.Raw.RollRate;
        if (state.BankTurnEnabled && !state.FreezeSurfaces) Couple(ref state, ref p, ref h, environment);
        var result = Compose(state.MatrixA, p, h, r, ref state, environment);
        if ((state.Raw.Configuration & 0x10) != 0)
        {
            state.Raw = state.Raw with { Configuration = (ushort)(state.Raw.Configuration & ~0x10) };
            result = result with { Roll = Recentre(old.Roll) };
        }
        var overridden = state.AircraftState == 10 || (state.Raw.Configuration & 0x40) != 0;
        if (overridden)
        {
            var amount = state.AircraftState == 10 ? 80 : 160;
            var value = old.Pitch >= 14400 ? old.Pitch + amount : Math.Max(old.Pitch - amount, 0);
            result = result with { Pitch = value >= 28800 ? (short)0 : W(value) };
        }
        else if (state.Raw.GustAndSinkEnabled)
        {
            var drop = (state.Raw.Dynamics & 2) != 0;
            if (!drop && !state.Raw.OnGround)
            {
                var halfPitch = old.Pitch >> 1;
                if (halfPitch < 7200) halfPitch = Math.Min(halfPitch, 3600) + 14400;
                var u = ((ushort)(18000 - halfPitch) * 108) >> 8;
                var threshold = W(u + (int)(((uint)(ushort)(state.RollSine >> 8) * (ushort)u) >> 6));
                if (threshold >= state.Raw.IndicatedSpeed)
                {
                    state.Raw = state.Raw with { Dynamics = (byte)(state.Raw.Dynamics | 4) };
                    drop = (state.Raw.PitchRate == 0 && state.Raw.HeadingRate == 0 && state.Raw.RollRate == 0) ||
                           (threshold >> 1) >= state.Raw.IndicatedSpeed;
                }
                else state.Raw = state.Raw with { GroundTerm = 0, Dynamics = (byte)(state.Raw.Dynamics & ~4) };
            }
            if (drop)
            {
                var ground = W(state.Raw.GroundTerm + ((80 - state.Raw.GroundTerm) >> 2));
                var value = old.Pitch + ground;
                var pitch = value >= 28800 ? (short)0 : value >= 14400 || value <= 4800 ? value : W(value - 80);
                state.Raw = state.Raw with { PitchRate = 0, GroundTerm = ground };
                result = result with { Pitch = W(pitch) };
                overridden = true;
            }
        }
        if (!overridden && old.Pitch is > 6000 and < 22800)
        {
            var dh = Difference(result.Heading, old.Heading);
            var dr = Difference(result.Roll, old.Roll);
            var hFlip = dh is > 1200 and < 27600;
            var rFlip = dr is > 1200 and < 27600;
            if (hFlip) dh = Difference(dh, 14400);
            if (rFlip) dr = Difference(dr, 14400);
            if ((!rFlip && hFlip) || (rFlip && Difference(dr, dh) >= 800))
            {
                var kick = data.ComponentTables.PlayerResponse[6];
                var raw = state.Raw;
                if ((raw.Configuration & 4) == 0)
                {
                    raw.Configuration |= 4;
                    if (raw.PitchCommand != 0) raw.PitchRate = W(raw.PitchRate + (raw.PitchRate < 0 ? -kick : kick));
                }
                else raw.HeadingRate = W(raw.HeadingRate + (raw.HeadingRate < 0 ? -kick : kick));
                state.Raw = raw;
                return state;
            }
        }
        state.Raw = state.Raw with
        {
            Pitch = result.Pitch, Heading = result.Heading, Roll = result.Roll,
            Configuration = (ushort)(state.Raw.Configuration & ~4)
        };
        RebuildMatrices(ref state, environment);
        return state;
    }
}
