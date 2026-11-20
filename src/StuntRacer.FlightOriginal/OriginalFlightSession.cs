using StuntRacer.Engine;
using StuntRacer.Flight;
using StuntRacer.FlightData;
using StuntRacer.FlightReference;
using FlightControls = StuntRacer.Flight.FlightInput;
using SourceInput = StuntRacer.FlightReference.FlightInput;

namespace StuntRacer.FlightOriginal;

public sealed class OriginalFlightSession : IFlightSession
{
    public const double StepSeconds = 1.0 / 8;
    public const double MetresPerPosition = 0.3048 / 204.8;
    private const int FullFuel = 0x61a800;
    private readonly FixedStepClock clock = new(StepSeconds, 0.25);
    private readonly FlightDiskPackage package;
    private readonly FlightPlayerKernel kernel;
    private readonly FlightReferenceKernel components;
    private readonly FlightPlayerModel playerModel;
    private readonly FlightCarrier carrier;
    private readonly OriginalWorldBuilder scenery;
    private readonly FlightAmbientSimulation ambient;
    private readonly RawPosition runway;
    private readonly short runwayHeading;
    private FlightPlayerState state;
    private FlightPlayerState previous;
    private string phase = "ready", resumedPhase = "flying";
    private FlightControls controls = new(0, 0, 0, 0, false, false);
    private double elapsed, countdown;
    private int landings, crashes, recoveries;

    public FlightWorldDefinition World { get; }
    public FlightDiskPackage Package => package;
    public FlightPlayerState RawState => state;
    public FlightSnapshot Snapshot => CreateSnapshot(1);
    public string ContactProfile => "original-model-vertices / enhanced-renderer contact refresh";

    public OriginalFlightSession(FlightDiskPackage package)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));
        var identity = new FlightDataIdentity(package.Source.Executable.Sha256, package.FlatImage.Sha256,
            package.ReferenceWindowImage.Sha256, package.Compatibility.Profile,
            "captured Kickstart 1.2 segment layout / reference2475dcd");
        var source = FlightOriginalData.FromImages(
            package.FlatImage.Bytes.Span, package.ReferenceWindowImage.Bytes.Span, identity);
        kernel = new FlightPlayerKernel(source);
        components = new FlightReferenceKernel(source.ComponentTables);
        var model = package.Scene.Models.Single(item => item.Id == 4).Levels[0];
        playerModel = new FlightPlayerModel(model.Vertices.Take(4)
            .Select(v => new RawVector(v.X, v.Y, v.Z)).ToArray(), 3);
        var start = package.Scene.Starts[0].Words;
        var direction = package.Tables["flight_dir"];
        runway = new(
            (start[0] << 24) + direction.ReadInt16(start[2] * 2) * 1024 + start[3] * 1024 + start[5] * 16,
            1800,
            (start[1] << 24) + direction.ReadInt16(start[2] * 2 + 1) * 1024 + start[4] * 1024 + start[6] * 16);
        runwayHeading = checked((short)(start[7] * 80));
        var shipModel = package.Scene.Models.Single(item => item.Id == 8).Levels[0];
        carrier = new FlightCarrier(shipModel.Vertices.Take(58)
            .Select(v => new RawVector(v.X, v.Y, v.Z)).ToArray())
        {
            Position = new(295174144, 0, 274202624), Status = 0x404c,
            AircraftClass = 0x20, Group = 0x0e, UnitShift = 3, MatrixB = FlightMatrix.Identity
        };
        scenery = new OriginalWorldBuilder(package, runway, carrier);
        World = scenery.World;
        ambient = new(World);
        ResetPlayer(FullFuel);
    }

    private FlightEnvironment EnvironmentFor(FlightPlayerState current) => new(
        playerModel, carrier, scenery.Landmarks(current.Raw.Position), 48,
        FlightContactVertexPolicy.RotateModelAtEntry) { SoundSystemRunning = true };

    private void ResetPlayer(int fuel)
    {
        var raw = new FlightRawState
        {
            Status = 0x11c8, Configuration = 0x80, Surface = 4, AircraftClass = 0x11,
            Throttle = 9, Repeat = 1, Fuel = fuel, Position = runway, Heading = runwayHeading,
            SimulationFlags = 0x840, AltitudeCeiling = 0x800000,
            GustAndSinkEnabled = true, PointerEnabled = true,
            PointerHeight = 72, PointerReference = 72, DisplayMode = 1
        };
        state = kernel.CreatePlayer(raw, EnvironmentFor(default));
        state.ModelPartId = 0xff;
        previous = state;
        controls = new(0, 0, 0, 9.0 / 120, false, false);
    }

    public FlightSnapshot Advance(double seconds, FlightControls input)
    {
        FixedStepClock.ValidateDelta(seconds);
        ArgumentNullException.ThrowIfNull(input);
        if (!new[] { input.Roll, input.Pitch, input.Yaw, input.Throttle }.All(double.IsFinite))
            throw new ArgumentOutOfRangeException(nameof(input), "Original-flight controls must be finite.");
        controls = input with
        {
            Roll = Math.Clamp(input.Roll, -1, 1), Pitch = Math.Clamp(input.Pitch, -1, 1),
            Yaw = Math.Clamp(input.Yaw, -1, 1), Throttle = Math.Clamp(input.Throttle, 0, 1)
        };
        if (phase is "flying" or "countdown") clock.Advance(seconds, Step);
        return CreateSnapshot(phase == "flying" ? clock.Alpha : 1);
    }

    private bool Step(double dt)
    {
        if (phase == "countdown")
        {
            countdown = Math.Max(0, countdown - dt);
            if (countdown == 0) phase = "flying";
            return true;
        }
        if (phase != "flying") return false;
        previous = state;
        var raw = state.Raw;
        raw.PointerHeight = (short)Math.Round(controls.Throttle * 960);
        var keys = DigitalKeys.None;
        if (controls.Roll > 0.04) keys |= DigitalKeys.RollRight;
        if (controls.Roll < -0.04) keys |= DigitalKeys.RollLeft;
        if (controls.Pitch > 0.04) keys |= DigitalKeys.StickBack;
        if (controls.Pitch < -0.04) keys |= DigitalKeys.StickForward;
        if (controls.Yaw > 0.04) keys |= DigitalKeys.RudderRight;
        if (controls.Yaw < -0.04) keys |= DigitalKeys.RudderLeft;
        if (raw.Afterburner && !controls.Afterburner) keys |= DigitalKeys.ThrottleDown;
        state.Raw = raw;
        var request = new SourceInput(keys,
            controls.Afterburner && controls.Throttle > 0.95 && !raw.Afterburner ? 10 : 0,
            ToggleBrake: controls.Brake != ((raw.Status & 0x800) != 0));
        state = kernel.StepPlayer(state, request, EnvironmentFor(state));
        elapsed += dt;
        if ((state.Raw.Status & 0x200) != 0)
        {
            crashes++;
            phase = "crashed";
        }
        else if (!previous.Raw.OnGround && state.Raw.OnGround) landings++;
        return true;
    }

    public void Command(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Trim().ToLowerInvariant())
        {
            case "start":
                if (phase == "ready") { countdown = 3; phase = "countdown"; }
                break;
            case "pause":
                if (phase is "flying" or "countdown") { resumedPhase = phase; phase = "paused"; }
                break;
            case "resume":
                if (phase == "paused") phase = resumedPhase;
                break;
            case "restart":
            case "garage":
                ResetPlayer(FullFuel);
                clock.Reset(); elapsed = 0; landings = crashes = recoveries = 0;
                var garage = command.Trim().Equals("garage", StringComparison.OrdinalIgnoreCase);
                countdown = garage ? 0 : 3; phase = garage ? "ready" : "countdown";
                break;
            case "recover":
                ResetPlayer(Math.Max(0, state.Raw.Fuel));
                recoveries++; countdown = 0; phase = "ready";
                break;
            case "gear-toggle":
            case "hook-toggle":
                if (phase is "ready" or "flying" or "countdown")
                {
                    var input = new SourceInput(state.Raw.Keys,
                        ToggleGear: command.Trim().Equals("gear-toggle", StringComparison.OrdinalIgnoreCase),
                        ToggleHook: command.Trim().Equals("hook-toggle", StringComparison.OrdinalIgnoreCase));
                    state.Raw = FlightReferenceKernel.ApplyKeyEvents(state.Raw, input);
                    previous = state;
                }
                break;
            case "air-start":
                if (phase is "ready" or "paused" or "crashed")
                {
                    ResetPlayer(Math.Max(0, state.Raw.Fuel));
                    var heading = runwayHeading * Math.Tau / 28800;
                    state.Raw = state.Raw with
                    {
                        Position = new(
                            runway.X + (int)(Math.Sin(heading) * 2000 / MetresPerPosition),
                            (int)(250 / MetresPerPosition),
                            runway.Z - (int)(Math.Cos(heading) * 2000 / MetresPerPosition)),
                        Configuration = (ushort)(state.Raw.Configuration & ~0x80),
                        Surface = 0, TargetSpeed = 3600, IndicatedSpeed = 3600, Throttle = 72,
                        PointerHeight = 576, PointerReference = 576
                    };
                    state = kernel.SetAttitude(state, 0, runwayHeading, 0);
                    var target = components.ComputeTargetVelocity(
                        new(state.MatrixB.M2, state.MatrixB.M5, state.MatrixB.M8), state.Raw.IndicatedSpeed);
                    state.Raw = state.Raw with { Displacement = new(-4 * target.X, -4 * target.Y, -4 * target.Z) };
                    previous = state;
                    phase = "flying"; countdown = 0;
                }
                break;
            case "carrier-start":
                if (phase is "ready" or "paused" or "crashed")
                {
                    ResetPlayer(Math.Max(0, state.Raw.Fuel));
                    state.Raw = state.Raw with
                    {
                        Position = new(carrier.Position.X + 11264, 30472, carrier.Position.Z + 106496),
                        Surface = 0xc8, Heading = 0
                    };
                    state.DeckHeight = 112;
                    state = kernel.SetAttitude(state, 0, 0, 0);
                    previous = state;
                    phase = "ready"; countdown = 0;
                }
                break;
            case "landmark-tour":
                if (phase is "ready" or "paused" or "crashed")
                {
                    var bridge = World.SourceWorld!.Objects.FirstOrDefault(mesh =>
                        mesh.Name is "Original scenery 48" or "Original scenery 49")
                        ?? throw new InvalidOperationException("The original suspension bridge is unavailable in this world.");
                    var x = (bridge.Vertices.Min(p => p.X) + bridge.Vertices.Max(p => p.X)) / 2;
                    var z = (bridge.Vertices.Min(p => p.Z) + bridge.Vertices.Max(p => p.Z)) / 2;
                    var alongX = bridge.Vertices.Max(p => p.X) - bridge.Vertices.Min(p => p.X) >
                        bridge.Vertices.Max(p => p.Z) - bridge.Vertices.Min(p => p.Z);
                    var startX = x - (alongX ? 570 : 760);
                    var startZ = z - (alongX ? 760 : 570);
                    var yaw = Math.Atan2(x - startX, z - startZ);
                    ResetPlayer(Math.Max(0, state.Raw.Fuel));
                    var height = Math.Max(180, bridge.Vertices.Max(p => p.Y) + 45);
                    state.Raw = state.Raw with
                    {
                        Position = new(runway.X - (int)(startX / MetresPerPosition),
                            (int)(height / MetresPerPosition),
                            runway.Z + (int)(startZ / MetresPerPosition)),
                        Configuration = (ushort)(state.Raw.Configuration & ~0x80), Surface = 0,
                        TargetSpeed = 3600, IndicatedSpeed = 3600, Throttle = 72,
                        PointerHeight = 576, PointerReference = 576
                    };
                    state = kernel.SetAttitude(state, 0, (short)Math.Round(yaw * 28800 / Math.Tau), 0);
                    var velocity = components.ComputeTargetVelocity(
                        new(state.MatrixB.M2, state.MatrixB.M5, state.MatrixB.M8), state.Raw.IndicatedSpeed);
                    state.Raw = state.Raw with { Displacement = new(-4 * velocity.X, -4 * velocity.Y, -4 * velocity.Z) };
                    previous = state; phase = "flying"; countdown = 0;
                }
                break;
            default: throw new ArgumentException($"Unknown original-flight command: {command}", nameof(command));
        }
    }

    private FlightSnapshot CreateSnapshot(double alpha)
    {
        var raw = state.Raw;
        static double Mix(double a, double b, double t) => a + (b - a) * t;
        static double Angle(short a, short b, double t)
        {
            var x = a * Math.Tau / 28800;
            var delta = Math.IEEERemainder((b - a) * Math.Tau / 28800, Math.Tau);
            return Math.IEEERemainder(x + delta * t, Math.Tau);
        }
        var forward = Basis(previous.MatrixB, state.MatrixB, 2, alpha);
        var sourceUp = Basis(previous.MatrixB, state.MatrixB, 1, alpha);
        var dot = forward.X * sourceUp.X + forward.Y * sourceUp.Y + forward.Z * sourceUp.Z;
        var ux = sourceUp.X - forward.X * dot;
        var uy = sourceUp.Y - forward.Y * dot;
        var uz = sourceUp.Z - forward.Z * dot;
        var upLength = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        if (upLength < 1e-8) throw new InvalidOperationException("The source attitude up vector became degenerate.");
        var up = new FlightVector(ux / upLength, uy / upLength, uz / upLength);
        var c = Math.Abs((int)state.CoupledPitch) > 1 ? state.CoupledPitch : 0;
        var g = -(((raw.PitchRate + (raw.PitchRate >> 3) + c) >> 3) +
            ((state.LandingFlags & 2) != 0 ? 10 : -10)) / 10.0;
        var aircraft = new AircraftSnapshot(
            -Mix(previous.Raw.Position.X - (double)runway.X, raw.Position.X - (double)runway.X, alpha) * MetresPerPosition,
            Mix(previous.Raw.Position.Y, raw.Position.Y, alpha) * MetresPerPosition,
            Mix(previous.Raw.Position.Z - (double)runway.Z, raw.Position.Z - (double)runway.Z, alpha) * MetresPerPosition,
            Angle(previous.Raw.Heading, raw.Heading, alpha), -Angle(previous.Raw.Pitch, raw.Pitch, alpha),
            Angle(previous.Raw.Roll, raw.Roll, alpha), Math.Abs((int)raw.IndicatedSpeed) / 12.0 * 0.5144444444,
            raw.Displacement.Y * MetresPerPosition / StepSeconds,
            Math.Clamp(raw.Throttle / 120.0, 0, 1), Math.Clamp(raw.Fuel / (double)FullFuel, 0, 1), g,
            raw.Afterburner, (raw.Dynamics & 4) != 0, raw.OnGround, raw.GearFullyDown,
            phase == "crashed", forward, up);
        var display = new FlightDisplaySnapshot(Math.Abs((int)raw.IndicatedSpeed) / 12,
            5 * (raw.Position.Y >> 10), ((ushort)raw.Heading >> 3) / 10,
            Math.Abs((int)raw.Throttle) * 256 / 307, raw.Fuel >> 8);
        var message = phase == "crashed" ? "Original flight model: impact — recover or restart."
            : phase == "ready" ? "Original Amiga flight / user disk loaded."
              : (raw.Dynamics & 4) != 0 ? "Original stall response — lower the nose and add power."
                : raw.OnGround ? "Original runway roll — power up and pull back to rotate."
                  : raw.Afterburner ? "Original afterburner engaged." : "Original Amiga player flight.";
        return new(aircraft, phase, elapsed, countdown, alpha, clock.Tick, landings, crashes, recoveries, message,
            display, new((raw.Configuration & 0x8000) != 0, (raw.Surface & 0xc0) != 0, (raw.GearEjection >> 4) & 7),
            ambient.Sample(phase == "flying" ? Math.Max(0, elapsed - StepSeconds * (1 - alpha)) : elapsed));
    }

    private static FlightVector Basis(FlightMatrix previous, FlightMatrix current, int column, double alpha)
    {
        var x = -previous[column] + (previous[column] - current[column]) * alpha;
        var y = previous[column + 3] + (current[column + 3] - previous[column + 3]) * alpha;
        var z = previous[column + 6] + (current[column + 6] - previous[column + 6]) * alpha;
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length < 1e-8) throw new InvalidOperationException("The source attitude basis became degenerate.");
        return new(x / length, y / length, z / length);
    }
}
