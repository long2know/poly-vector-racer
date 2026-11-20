namespace StuntRacer.FlightReference;

public sealed partial class FlightPlayerKernel
{
    public FlightPlayerState CreatePlayer(FlightRawState raw, FlightEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ValidatePlayer(raw);
        var state = new FlightPlayerState
        {
            Raw = FlightReferenceKernel.RefreshPositionCells(raw),
            TurnLimit = 0x1400, BankTurnEnabled = true, CollisionsEnabled = true,
            ModelPartId = 0xff, ModelUnitShift = environment.PlayerModel.UnitShift
        };
        RebuildMatrices(ref state, environment);
        return RefreshContactVertices(state, environment.PlayerModel);
    }

    private static void ValidatePlayer(FlightRawState raw)
    {
        if (raw.EntityIndex != 0 || (raw.AircraftClass & 0xf0) != 0x10 || raw.AircraftClass == 0x15)
            throw new NotSupportedException("Full stepping is for the single player aircraft, not missiles/AI/other entity classes.");
        if ((raw.GearEjection & 15) != 0)
            throw new NotSupportedException("The ejection-seat/entity cycle is outside aircraft-only Free Flight.");
        if ((raw.Displacement.X & 3) != 0 || (raw.Displacement.Y & 3) != 0 || (raw.Displacement.Z & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(raw), "Source motion displacements must be multiples of four.");
    }

    public static FlightPlayerState RefreshContactVertices(FlightPlayerState state, FlightPlayerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var matrix = state.MatrixB;
        RawVector Transform(RawVector vertex)
        {
            short Row(int row) => W(unchecked((matrix[row] >> 6) * vertex.X +
                (matrix[row + 1] >> 6) * vertex.Y + (matrix[row + 2] >> 6) * vertex.Z) >> 8);
            return new(Row(0), Row(3), Row(6));
        }
        state.Vertex0 = Transform(model.Vertices[0]);
        state.Vertex1 = Transform(model.Vertices[1]);
        state.Vertex2 = Transform(model.Vertices[2]);
        state.Vertex3 = Transform(model.Vertices[3]);
        state.ModelUnitShift = model.UnitShift;
        return state;
    }

    /// <summary>Source draw rotation: Q14 matrix -> Q8, signed-word products/sums -> words. Suitable for all 58 carrier slots.</summary>
    public static RawVector[] RotateContactVertices(ReadOnlySpan<RawVector> vertices, FlightMatrix matrixB)
    {
        var result = new RawVector[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            short Row(int row) => W(unchecked((matrixB[row] >> 6) * v.X +
                (matrixB[row + 1] >> 6) * v.Y + (matrixB[row + 2] >> 6) * v.Z) >> 8);
            result[i] = new(Row(0), Row(3), Row(6));
        }
        return result;
    }

    private static RawVector Vertex(FlightPlayerState state, int index) => index switch
    {
        0 => state.Vertex0, 1 => state.Vertex1, 2 => state.Vertex2, 3 => state.Vertex3,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static void GearCue(ref FlightPlayerState state)
    {
        if (state.Raw.TargetSpeed >= 0x1200 && state.Raw.GearFullyDown)
        {
            if (state.GearWarningLatch == 0) state.EventBeep = 4;
            state.GearWarningLatch = 1;
        }
        else state.GearWarningLatch = 0;
    }

    private static void GearAndHook(ref FlightPlayerState state)
    {
        var raw = state.Raw;
        var progress = (raw.GearEjection >> 4) & 7;
        var next = (raw.GearEjection & 0x80) != 0 ? progress < 6 ? progress + 1 : progress : Math.Max(progress - 1, 0);
        if (next != progress)
        {
            raw.GearEjection = (byte)((raw.GearEjection & 0x80) | (next << 4));
            state.GearSound = 3;
        }
        var hookProgress = raw.HookLamp & 0x7f;
        if ((raw.HookLamp & 0x80) != 0)
        {
            if (hookProgress < 5) hookProgress++;
        }
        else if (hookProgress != 0) hookProgress--;
        raw.HookLamp = (byte)((raw.HookLamp & 0x80) | hookProgress);
        state.Raw = raw;
    }

    private static bool Post(ref FlightPlayerState state, ushort code)
    {
        if (state.MessageCode == code) return false;
        state.MessageCode = code;
        var raw = state.Raw;
        raw.SimulationFlags |= 1;
        if ((code & 0x2000) != 0) raw.GroundFuelFlags = (ushort)((raw.GroundFuelFlags | 0x2000) & 0x7fff);
        else if (code is 0x4000 or 0x4800) raw.GroundFuelFlags &= 0xdfff;
        state.Raw = raw;
        return true;
    }

    private static bool StateWarnings(ref FlightPlayerState state)
    {
        var raw = state.Raw;
        var descending = raw.Displacement.Y < 0;
        if ((raw.Dynamics & 2) != 0 && descending)
        {
            if (unchecked(Abs(raw.Displacement.Y) * 64) >= raw.Position.Y && Post(ref state, 0xd02a))
            {
                state.Raw = state.Raw with { Cues = state.Raw.Cues | 0x40 };
                return true;
            }
        }
        else
        {
            var imminent = descending && raw.Pitch is > 0x1e0 and < 0x3840 &&
                           unchecked(Abs(raw.Displacement.Y) * 16) >= raw.Position.Y;
            imminent |= raw.Roll is > 0x4b0 and < 0x6bd0 && raw.Position.Y <= 0x1800;
            if (imminent && !raw.OnGround && Post(ref state, 0x9c06))
            {
                state.Raw = state.Raw with { Cues = state.Raw.Cues | 0x80 };
                return true;
            }
        }
        if ((raw.Configuration & 1) != 0 && !state.ExternalView && raw.ViewedEntityIndex == 0 && Post(ref state, 0xd00a))
            state.Raw = state.Raw with { Cues = state.Raw.Cues | 0x80 };
        return false;
    }

    private void Propulsion(ref FlightPlayerState state, FlightEnvironment environment)
    {
        var context = state;
        var full = new FlightReferenceKernel(data.ComponentTables,
            index => ImageWord(0x436e8 + 2 * index, ref context, environment));
        var raw = full.StepThrottleFuelSpeed(state.Raw);
        state.DynamicReadCount = context.DynamicReadCount;
        state.Raw = raw;
    }

    /// <summary>
    /// One ordered source player/world-update pass. Caller schedules it, supplies
    /// the scene/draw contact context and owns the separate mission dispatcher.
    /// All changes are returned by value; an unsupported path throws atomically.
    /// </summary>
    public FlightPlayerState StepPlayer(FlightPlayerState state, FlightInput input, FlightEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ValidatePlayer(state.Raw);
        if (input.Eject) throw new NotSupportedException("Ejection requires the omitted seat/entity world pass.");
        if (state.ScaledMotion != 0 || state.ScaledHorizontal != 0)
            throw new NotSupportedException("Unused scaled-motion modes are not Free Flight.");
        if (state.ModelUnitShift > 15) throw new ArgumentOutOfRangeException(nameof(state));
        if (environment.VertexPolicy == FlightContactVertexPolicy.RotateModelAtEntry)
            state = RefreshContactVertices(state, environment.PlayerModel);

        // Key handlers precede pause and the world pre-steps; F-key request is
        // after the brake event, matching the native frame input wrapper.
        state.Raw = FlightReferenceKernel.ApplyKeyEvents(state.Raw, input with { FunctionKey = 0 });
        if (input.FunctionKey != 0)
            state.Raw = FlightReferenceKernel.ApplyKeyEvents(state.Raw, new(input.Keys, input.FunctionKey));
        state.Raw = state.Raw with
        {
            Configuration = (ushort)(state.Raw.Configuration & ~1),
            Status = (ushort)(state.Raw.Status & ~2)
        };
        GearCue(ref state);
        if (state.Paused) return state;
        state.Raw = state.Raw with { Counter = W(state.Raw.Counter - 1) };
        var raw = state.Raw;
        var expired = state.ExternalView && (raw.Status & 0x400) != 0 && raw.Counter <= 0;
        if (expired)
        {
            if ((raw.Status & 8) == 0 && !raw.OnGround) state.CrashCount++;
            raw.Status &= unchecked((ushort)~0x6440);
            raw.Counter = 20;
            state.Raw = raw;
        }
        else
        {
            if (state.ExternalView && (raw.Status & 0x400) != 0)
                state.Raw = state.Raw with { Status = (ushort)(state.Raw.Status & ~0x2000) };
            var idle = state.ExternalView && (state.Raw.Status & 0x1100) == 0;
            if (idle) state.Raw = FlightReferenceKernel.RefreshPositionCells(state.Raw);
            else
            {
                if (state.Raw.SimulationRunning)
                {
                    state.Raw = FlightReferenceKernel.ConvertCommands(state.Raw);
                    var skip = StateWarnings(ref state);
                    if (!skip && FlightReferenceKernel.PropulsionEligible(state.Raw))
                        Propulsion(ref state, environment);
                }
                if ((state.Raw.Status & 0x8000) == 0) state = StepAttitude(state, environment);
                if (!state.Raw.SimulationRunning) state.Raw = state.Raw with { Displacement = default };
                else Motion(ref state, environment);
                if ((state.Raw.Status & 0x1000) != 0)
                {
                    var x = unchecked(state.Raw.Position.X + state.Raw.Displacement.X);
                    var z = unchecked((x < 0 ? 0 : state.Raw.Position.Z) + state.Raw.Displacement.Z);
                    state.Raw = state.Raw with
                    {
                        Position = new(Math.Min(x, 0x1fffffff), state.Raw.Position.Y, Math.Clamp(z, 0, 0x1fffffff))
                    };
                    state.Raw = FlightReferenceKernel.RefreshPositionCells(state.Raw);
                }
            }
            GearAndHook(ref state);
            var before = idle ? state.Raw.Position : new RawPosition(
                unchecked(state.Raw.Position.X - state.Raw.Displacement.X),
                unchecked(state.Raw.Position.Y - state.Raw.Displacement.Y),
                unchecked(state.Raw.Position.Z - state.Raw.Displacement.Z));
            if ((state.Raw.Status & 0x400) == 0)
            {
                var contact = Contact(ref state, environment, before);
                if (contact != 0) HandleContact(ref state, environment, contact, before);
                else if ((state.Raw.Dynamics & 2) != 0 && state.Raw.OnGround)
                {
                    state.Raw = state.Raw with { Status = (ushort)((state.Raw.Status & ~0x1000) | 0x600), Counter = 15 };
                }
                else
                {
                    raw = state.Raw;
                    raw.Status &= 0x7fff;
                    if ((raw.Status & 0x200) != 0) { raw.Status |= 0x400; raw.Counter = 15; }
                    else raw.Configuration = (ushort)((raw.Configuration & ~2) | (state.ContactFlag != 0 ? 2 : 0));
                    state.Raw = raw;
                }
                state.ContactFlag = 0;
            }
        }
        state.StepCount++;
        raw = state.Raw;
        if (!environment.CallerOwnsViewedCell)
        {
            raw.ViewCellX = raw.CellX;
            raw.ViewCellZ = raw.CellZ;
        }
        if ((state.StepCount & 3) == 0) state.PanelAltitude = 5 * (raw.Position.Y >> 10);
        if ((state.StepCount & 3) == 2) state.PanelSpeed = W(Math.Abs((int)raw.IndicatedSpeed) / 12);
        state.Raw = raw;
        return state;
    }

    private RawVelocity Velocity(FlightPlayerState state)
    {
        var n = state.MatrixB;
        var x = Math.Abs((int)n.M2);
        var y = Math.Abs((int)n.M5);
        var z = Math.Abs((int)n.M8);
        var a = Math.Max(x, y);
        var b = Math.Min(x, y);
        var xy = a * data.ComponentTables.VectorLength[b == 0 ? 0 : 256 * b / a];
        var zz = z * 16384;
        var major = Math.Max(xy, zz) >> 14;
        var index = major == 0 ? 0 : (Math.Min(xy, zz) / major) >> 6;
        if ((uint)index > 256) throw new NotSupportedException("Source vector-length lookup leaves its supplied table.");
        var length = Math.Min((data.ComponentTables.VectorLength[index] * major) >> 14, 32767);
        var speed = W(-state.Raw.IndicatedSpeed);
        var magnitude = Math.Abs((int)speed);
        if (length == 0 || magnitude == 0) return default;
        int scaled, exponent;
        if (magnitude <= length)
        {
            scaled = magnitude; exponent = 8;
            do { scaled *= 4; exponent += 2; } while (scaled <= length);
        }
        else
        {
            var shifts = 1;
            while (shifts < 4 && (magnitude >> (2 * shifts)) > length) shifts++;
            scaled = 4 * (magnitude >> (2 * shifts)); exponent = 10 - 2 * shifts;
        }
        var numerator = scaled * 256;
        var q = numerator / length;
        if (q > 65535) q = (ushort)numerator;
        int Part(short value)
        {
            var v = unchecked(value * q) >> exponent;
            return speed < 0 ? unchecked(-v) : v;
        }
        return new(Part(n.M2), Part(n.M5), Part(n.M8));
    }

    private void Motion(ref FlightPlayerState state, FlightEnvironment environment)
    {
        var raw = state.Raw;
        var target = Velocity(state);
        int Smooth(int displacement, int desired, bool vertical)
        {
            var old = unchecked(-displacement) / 4;
            if (vertical || !raw.OnGround)
            {
                var d = unchecked(desired - old);
                old = unchecked(old + (d >> (Abs(d) > 4 ? 2 : Abs(d) >= 3 ? 1 : 0)));
            }
            else old = desired;
            return vertical ? unchecked(old + raw.Sink) : old;
        }
        var vx = Smooth(raw.Displacement.X, target.X, false);
        var vy = Smooth(raw.Displacement.Y, target.Y, true);
        var vz = Smooth(raw.Displacement.Z, target.Z, false);
        raw.Displacement = new(unchecked(-4 * vx), unchecked(-4 * vy), unchecked(-4 * vz));
        var altitude = unchecked(raw.Position.Y + raw.Displacement.Y);
        var clearance = !raw.GearFullyDown ? 750 : raw.AircraftClass == 0x14 ? 2700 : 1800;
        var floor = (raw.Surface & 0xc0) != 0 ? Math.Max(unchecked((state.DeckHeight << 8) + clearance), clearance) : clearance;
        if (altitude > floor)
        {
            if (raw.OnGround)
            {
                if ((raw.Surface & 8) != 0) raw.MotionSound = 4;
                raw.Configuration &= 0xef7f;
                raw.Surface &= 0xf5;
                raw.FlightWarningFlags &= 0xfffd;
                state.LiftOffBits |= 1;
                state.LandingFlags |= 0x100;
                state.EventLiftOff = 1;
                state.EventJolt = 1;
            }
            else altitude = Math.Min(altitude, raw.AltitudeCeiling);
        }
        else
        {
            raw.Dynamics &= 0xfb;
            altitude = floor;
            if ((raw.Surface & 0xc0) == 0 && (!raw.OnGround || (raw.Counter & 7) == 0))
            {
                raw.Surface = QueryTerrain(raw.Position.X, raw.Position.Z, raw.Surface);
                if ((raw.Surface & 2) != 0 && raw.GearFullyDown)
                {
                    raw.GearEjection |= 0x80;
                    raw.Dynamics |= 1;
                }
            }
            if ((raw.Status & 0x200) != 0) raw.Displacement = raw.Displacement with { Y = 0 };
            else if (!raw.OnGround)
            {
                raw.Configuration |= 0x80;
                raw.GroundTerm = 0;
                raw.MotionSound = 1;
                state.EventTouchdown = 1;
                if (vy > 0x180) state.EventJolt = 1;
                if (vy >= 0x240)
                {
                    if (!state.Immune)
                    {
                        raw.Status |= 0x200;
                        state.CrashHandover = 1; state.CrashView = 4; state.EventCrash = 1;
                    }
                }
                else if (raw.IndicatedSpeed != 0)
                {
                    raw.TargetSpeed = raw.IndicatedSpeed;
                    raw.CentreNotice = 0xfe;
                    if (((raw.Surface & 2) != 0 || !raw.GearFullyDown) && raw.IndicatedSpeed >= 960)
                        raw.Configuration |= 0x1000;
                    raw.GroundSoundVolume = (byte)(vy > 0x180 ? 40 : 30);
                }
                raw.Displacement = raw.Displacement with { Y = 0 };
            }
            else
            {
                if (raw.IndicatedSpeed != 0)
                {
                    if ((raw.FlightWarningFlags & 2) != 0)
                    {
                        raw.FlightWarningFlags &= 0xfffd; raw.MotionSound = 3; raw.Cues |= 0x200;
                    }
                }
                else
                {
                    if ((raw.FlightWarningFlags & 2) == 0)
                    {
                        raw.MotionSound = 4;
                        if ((state.LandingFlags & 0x100) != 0)
                        {
                            state.LandingFlags &= 0xfeff;
                            if ((raw.Surface & 4) != 0 || (raw.Configuration & 0xc000) == 0xc000)
                            {
                                if (!environment.SuppressRearm)
                                {
                                    raw.Fuel = 6400000;
                                    state.EventRearm = 1;
                                    state.Raw = raw;
                                    Post(ref state, 0x4005);
                                    raw = state.Raw;
                                }
                            }
                        }
                        raw.FlightWarningFlags |= 2;
                    }
                    if (raw.Fuel == 0) raw.GroundFuelFlags |= 0x4000;
                }
                raw.Displacement = raw.Displacement with { Y = 0 };
            }
            if (raw.IndicatedSpeed < 960) raw.Configuration &= 0xefff;
        }
        raw.Position = raw.Position with { Y = altitude };
        state.Raw = raw;
    }

    private static void HandleContact(ref FlightPlayerState state, FlightEnvironment environment, int result, RawPosition previous)
    {
        var raw = state.Raw;
        if (result == 0x20)
        {
            raw.PitchRate = raw.HeadingRate = raw.RollRate = 0;
            raw.Position = previous with { Y = raw.OnGround ? raw.Position.Y : previous.Y };
        }
        raw = FlightReferenceKernel.RefreshPositionCells(raw);
        if (result == 0x10) raw.Configuration |= 0x80;
        else
        {
            if (!state.CollisionsEnabled) { state.Raw = raw; return; }
            if ((raw.Status & 0x8000) == 0 && raw.IndicatedSpeed <= 960)
            {
                if (environment.SoundSystemRunning) state.EventBump = 1;
                else { raw.CentreNotice = 0xfa; raw.NoticeDuration = 2; }
            }
        }
        raw.Status |= 0x8000;
        if (raw.IndicatedSpeed > 960 && !state.Immune)
        {
            raw.Status |= 0x200;
            state.CrashHandover = 1; state.CrashView = 4; state.EventCrash = 1;
            if (state.ExternalView)
            {
                raw.Dynamics |= 2;
                raw.Status = (ushort)((raw.Status & ~0x1000) | 0x600);
                raw.Counter = 15;
            }
        }
        state.Raw = raw;
    }
}
