namespace StuntRacer.FlightReference;

public sealed partial class FlightReferenceKernel
{
    /// <summary>
    /// Section 7 direction-times-speed rule. Direction is supplied explicitly
    /// from a caller's Q14 attitude matrix; this does NOT construct that matrix.
    /// Components outside [-16384,16384] are outside this bounded Q14 API.
    /// The length lookup is external; no floating point participates.
    /// </summary>
    public RawVelocity ComputeTargetVelocity(RawVector direction, short indicatedSpeed)
    {
        if (Math.Abs((int)direction.X) > 16384 || Math.Abs((int)direction.Y) > 16384 ||
            Math.Abs((int)direction.Z) > 16384)
            throw new ArgumentOutOfRangeException(nameof(direction), "A bounded Q14 direction component must be -16384..16384.");

        var a = Math.Max(Math.Abs((int)direction.X), Math.Abs((int)direction.Y));
        var b = Math.Min(Math.Abs((int)direction.X), Math.Abs((int)direction.Y));
        var xy = a * tables.VectorLength[b == 0 ? 0 : (256 * b) / a];
        var z = Math.Abs((int)direction.Z) * 16384;
        var major = Math.Max(xy, z) >> 14;
        var minor = Math.Min(xy, z);
        var index = major == 0 ? 0 : (minor / major) >> 6;
        if ((uint)index >= FlightReferenceTables.LengthTableCount)
            throw new ArgumentException("The supplied length table cannot normalize this direction.", nameof(direction));
        var length = Math.Min((tables.VectorLength[index] * major) >> 14, 0x7fff);
        var speed = Word(-indicatedSpeed);
        var magnitude = Math.Abs((int)speed);
        if (magnitude == 0 || length == 0) return default;

        int scaled;
        int exponent;
        if (magnitude <= length)
        {
            scaled = magnitude;
            exponent = 8;
            do { scaled *= 4; exponent += 2; } while (scaled <= length);
        }
        else
        {
            var shifts = 1;
            while (shifts < 4 && (magnitude >> (2 * shifts)) > length) shifts++;
            scaled = 4 * (magnitude >> (2 * shifts));
            exponent = 10 - 2 * shifts;
        }
        var numerator = scaled * 256;
        var quotient = numerator / length;
        if (quotient > ushort.MaxValue) quotient = unchecked((ushort)numerator);
        int Component(short component)
        {
            var value = (component * quotient) >> exponent;
            return speed < 0 ? -value : value;
        }
        return new(Component(direction.X), Component(direction.Y), Component(direction.Z));
    }

    /// <summary>
    /// Section 7 velocity smoothing and sink only, without floor/contact or
    /// scaled-motion modes. Stored displacements must be multiples of four.
    /// Unlike speed/rate words, velocity intermediates are signed LONGS.
    /// Ground X/Z are unsmoothed; Y always smooths then adds the signed sink word.
    /// The returned displacement must be resolved against a floor by an
    /// integrator before ground motion can be used as a complete motion step.
    /// </summary>
    public static RawDisplacement SmoothVelocity(
        RawDisplacement previous, RawVelocity target, short sink, bool onGround)
    {
        if (target.X is < -32768 or > 32768 || target.Y is < -32768 or > 32768 || target.Z is < -32768 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(target), "Bounded direction-times-speed components must be -32768..32768.");
        static int Decode(int displacement)
        {
            if ((displacement & 3) != 0)
                throw new ArgumentOutOfRangeException(nameof(previous), "Displacements must be multiples of four.");
            // Source NEG.L happens BEFORE division. INT_MIN negates to itself;
            // replacing this by -(displacement / 4) changes that boundary.
            return unchecked(-displacement) / 4;
        }
        static int Follow(int old, int desired)
        {
            var delta = desired - old;
            var magnitude = Math.Abs(delta);
            return old + (delta >> (magnitude > 4 ? 2 : magnitude > 2 ? 1 : 0));
        }
        var x = Decode(previous.X);
        var y = Decode(previous.Y);
        var z = Decode(previous.Z);
        var vx = onGround ? target.X : Follow(x, target.X);
        var vy = Follow(y, target.Y) + sink;
        var vz = onGround ? target.Z : Follow(z, target.Z);
        return new(unchecked(-4 * vx), unchecked(-4 * vy), unchecked(-4 * vz));
    }

    /// <summary>
    /// Section 7 airborne position/cell subset, using an ALREADY computed
    /// displacement and caller's viewed cell. Rejects any floor crossing or
    /// ground state rather than inventing touchdown, terrain, or lift-off rules.
    /// No attitude, collision, gear progress, statistics or mission effects.
    /// </summary>
    public static FlightRawState AdvanceAirbornePosition(FlightRawState state)
    {
        Validate(state);
        if ((state.Status & 0x1000) == 0)
            throw new InvalidOperationException("Position advancement requires the active-record bit.");
        if (state.OnGround)
            throw new NotSupportedException("Ground contact/lift-off resolution is not supplied by this component.");
        if (state.AltitudeCeiling < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Altitude ceiling must be nonnegative.");
        var clearance = !state.GearFullyDown ? 0x2ee : state.AircraftClass == 0x14 ? 0xa8c : 0x708;
        var floor = state.AircraftClass == 0 ? -0x200 : clearance;
        if ((state.Surface & 0xc0) != 0)
            throw new NotSupportedException("Deck-floor resolution requires external ship/contact state.");
        var y = Math.Min(unchecked(state.Position.Y + state.Displacement.Y), state.AltitudeCeiling);
        if (y <= floor)
            throw new NotSupportedException("This step reaches the floor; touchdown/terrain resolution is unavailable.");
        var x = unchecked(state.Position.X + state.Displacement.X);
        var z = unchecked((x < 0 ? 0 : state.Position.Z) + state.Displacement.Z);
        x = Math.Min(x, 0x1fffffff); // the negative-X/reset-Z source quirk is deliberate
        z = Math.Clamp(z, 0, 0x1fffffff);
        if (state.AircraftClass == 0x15) y = 0xb000;
        state.Position = new(x, y, z);
        return RefreshPositionCells(state);
    }

    /// <summary>Section 7 cell bookkeeping only, from an already resolved position. No automatic camera-cell write.</summary>
    public static FlightRawState RefreshPositionCells(FlightRawState state)
    {
        Validate(state);
        var (x, y, z) = state.Position;
        var oldX = state.CellX;
        var oldZ = state.CellZ;
        var oldSub = state.SubCell;
        var nearPreviousCell =
            Math.Abs((int)Word(oldX - state.ViewCellX)) <= 2 &&
            Math.Abs((int)Word(oldZ - state.ViewCellZ)) <= 2;
        state.CellX = Word(Word(x >> 16) >> 6);
        state.CellZ = Word(Word(z >> 16) >> 6);
        state.FineX = (short)((x & 0x3fffff) >> 8);
        state.FineZ = (short)((z & 0x3fffff) >> 8);
        state.Altitude256 = y >> 8;
        state.SubCell = (byte)(4 * (3 - (state.FineZ >> 12)) + (3 - (state.FineX >> 12)));
        if (state.SubCell != oldSub)
        {
            state.CellRefresh = 0xff;
            if (nearPreviousCell) state.Neighbour = 15;
        }
        return state;
    }
}
