namespace StuntRacer.FlightReference;

// Functional specification: st-gr/interceptor-port, commit
// 2475dcd65f454a37b41a2be27629ad958f34ddc5, docs/re/05-flight-model.md,
// sections 2–5, 7 and their addenda. Independently authored C#; no game image,
// original tables, or implementation bodies from the C oracle are embedded.

[Flags]
public enum DigitalKeys : byte
{
    None = 0,
    ThrottleUp = 1,
    ThrottleDown = 2,
    RollRight = 4,
    RollLeft = 8,
    StickForward = 16,
    StickBack = 32,
    RudderRight = 64,
    RudderLeft = 128
}

[Flags]
public enum ReferenceStages
{
    None = 0,
    DigitalCommands = 1,
    ThrottleFuelSpeed = 2,
    ControlSurfaceRates = 4,
    TargetVelocity = 8,
    VelocitySmoothing = 16,
    AirbornePosition = 32,
    AttitudeComposition = 64,
    GroundContact = 128,
    Collisions = 256,
    Missions = 512,
    FullPlayerStep = 1024
}

public readonly record struct FlightInput(
    DigitalKeys Keys,
    int FunctionKey = 0,
    bool ToggleGear = false,
    bool ToggleHook = false,
    bool ToggleBrake = false,
    bool Eject = false);

public readonly record struct RawPosition(int X, int Y, int Z);
public readonly record struct RawDisplacement(int X, int Y, int Z);
public readonly record struct RawVector(short X, short Y, short Z);
public readonly record struct RawVelocity(int X, int Y, int Z);

/// <summary>
/// Raw source units, not SI units. Angles are signed word *bit patterns*, not
/// reduced modulo a circle. Pitch positive means nose down; world -X is east.
/// A component call takes/returns a value: it has no clock, shared state or IO.
/// This is deliberately not the complete original player record.
/// </summary>
public record struct FlightRawState
{
    public ushort Status { get; set; }                  // W0
    public ushort Configuration { get; set; }           // W2
    public byte Surface { get; set; }                   // B4: water/pad/deck/catapult/lock
    public byte Dynamics { get; set; }                  // B20: skid/out-of-control/stall
    public byte GearEjection { get; set; }
    public byte AircraftClass { get; set; }
    public sbyte PitchCommand { get; set; }
    public sbyte RudderCommand { get; set; }
    public sbyte RollCommand { get; set; }
    public sbyte Throttle { get; set; }
    public byte Repeat { get; set; }
    public DigitalKeys Keys { get; set; }
    public sbyte Preset { get; set; }
    public short Counter { get; set; }                  // already decremented by caller, if appropriate
    public short GroundTerm { get; set; }
    public short PitchRate { get; set; }
    public short HeadingRate { get; set; }
    public short RollRate { get; set; }
    public short Pitch { get; set; }
    public short Heading { get; set; }
    public short Roll { get; set; }
    public short TargetSpeed { get; set; }              // S
    public short IndicatedSpeed { get; set; }           // W
    public short Sink { get; set; }
    public short Gust { get; set; }
    public int Fuel { get; set; }
    public int AltitudeCeiling { get; set; }
    public ushort SimulationFlags { get; set; }         // bit 6: simulation enabled
    public ushort GroundFuelFlags { get; set; }         // bit 14: speed-rule inhibit
    public ushort FlightWarningFlags { get; set; }      // bit 14: overspeed
    public int Warnings { get; set; }
    public uint Cues { get; set; }
    public sbyte StallLampCounter { get; set; }
    public bool GustAndSinkEnabled { get; set; }
    public bool PointerEnabled { get; set; }
    public short PointerHeight { get; set; }
    public short PointerReference { get; set; }
    public byte DisplayMode { get; set; }               // 2 suppresses pointer firing, not following
    public ushort EntityIndex { get; set; }             // 0 is player; other indices select AI rate response, not an AI simulation
    public ushort ViewedEntityIndex { get; set; }
    public byte ThrottleSound { get; set; }
    public byte MotionSound { get; set; }
    public byte CentreNotice { get; set; }
    public byte NoticeDuration { get; set; }
    public byte HookLamp { get; set; }
    public byte GroundSoundVolume { get; set; }
    public RawPosition Position { get; set; }
    public RawDisplacement Displacement { get; set; }
    public short CellX { get; set; }
    public short CellZ { get; set; }
    public short FineX { get; set; }
    public short FineZ { get; set; }
    public int Altitude256 { get; set; }
    public byte SubCell { get; set; }
    public byte Neighbour { get; set; }
    public short ViewCellX { get; set; }
    public short ViewCellZ { get; set; }
    public byte CellRefresh { get; set; }

    public readonly bool OnGround => (Configuration & 0x80) != 0;
    public readonly bool GearFullyDown => (GearEjection & 0x70) == 0;
    public readonly bool Afterburner => (Configuration & 8) != 0;
    public readonly bool SimulationRunning => (SimulationFlags & 0x40) != 0;
}

/// <summary>
/// Immutable caller-owned-data boundary (the constructor makes private copies).
/// No stock bytes or default/fallback tables. Even the words adjoining the sine
/// table must be supplied: a source gust lookup is NOT a folded sine function.
/// </summary>
public sealed class FlightReferenceTables
{
    public const int PlayerResponseCount = 62;
    public const int AiResponseCount = 64;
    public const int SpeedTableCount = 31;
    public const int LengthTableCount = 257;
    public const int FirstGustSineIndex = -900;
    public const int LastGustSineIndex = 4996;
    public const int GustSineWordCount = LastGustSineIndex - FirstGustSineIndex + 1;

    private readonly short[] player;
    private readonly short[] ai;
    private readonly short[] gust;
    private readonly short[] sink;
    private readonly short[] sineWindow;
    private readonly short[] length;

    public ReadOnlySpan<short> PlayerResponse => player;
    public ReadOnlySpan<short> AiResponse => ai;
    public ReadOnlySpan<short> Gust => gust;
    public ReadOnlySpan<short> Sink => sink;
    public ReadOnlySpan<short> GustSineWords => sineWindow;
    public ReadOnlySpan<short> VectorLength => length;

    public FlightReferenceTables(
        ReadOnlySpan<short> playerResponse, ReadOnlySpan<short> aiResponse,
        ReadOnlySpan<short> gust, ReadOnlySpan<short> sink,
        ReadOnlySpan<short> gustSineWords, ReadOnlySpan<short> vectorLength)
    {
        player = Copy(playerResponse, PlayerResponseCount, nameof(playerResponse), 0);
        ai = Copy(aiResponse, AiResponseCount, nameof(aiResponse), 0);
        this.gust = Copy(gust, SpeedTableCount, nameof(gust), 0);
        this.sink = Copy(sink, SpeedTableCount, nameof(sink), 0);
        sineWindow = Copy(gustSineWords, GustSineWordCount, nameof(gustSineWords), short.MinValue);
        length = Copy(vectorLength, LengthTableCount, nameof(vectorLength), 16384);
        if (length[0] != 16384)
            throw new ArgumentException("The vector-length Q14 table must begin at 16384.", nameof(vectorLength));
        for (var i = 1; i < length.Length; i++)
            if (length[i] < length[i - 1])
                throw new ArgumentException("Vector-length entries must be nondecreasing.", nameof(vectorLength));
        for (var i = 0; i <= 900; i++)
            if (SineWord(i) is < 0 or > 16384)
                throw new ArgumentException("The quarter-wave words must be unsigned-magnitude Q14 (0..16384).", nameof(gustSineWords));
    }

    private static short[] Copy(ReadOnlySpan<short> values, int count, string name, int min)
    {
        if (values.Length != count)
            throw new ArgumentException($"Expected exactly {count} words, received {values.Length}.", name);
        foreach (var value in values)
            if (value < min)
                throw new ArgumentOutOfRangeException(name, $"A table word must be at least {min}.");
        return values.ToArray();
    }

    internal short SineWord(int index)
    {
        if (index is < FirstGustSineIndex or > LastGustSineIndex)
            throw new NotSupportedException($"Gust sine-word index {index} is outside the explicitly supplied window.");
        // These four words intersect the five run-time text bytes identified in
        // section 17.9. A static image/table cannot establish their live value.
        // Do not silently use synthetic data (or the C port's counted zero).
        if (index is >= 1264 and <= 1266 or 3036)
            throw new NotSupportedException($"Gust sine-word index {index} intersects unavailable run-time text state.");
        return sineWindow[index - FirstGustSineIndex];
    }
}
