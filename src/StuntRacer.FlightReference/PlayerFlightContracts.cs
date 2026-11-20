using System.Security.Cryptography;

namespace StuntRacer.FlightReference;

/// <summary>Provenance supplied by the extractor. A cracked/modified variant is not stock certification.</summary>
public sealed record FlightDataIdentity(
    string ExecutableSha256,
    string FlatImageSha256,
    string ReferenceImageSha256,
    string Variant,
    string RelocationProfile);

/// <summary>Big-endian images start at BaseAddress, not at file offset zero in an Amiga executable.</summary>
public sealed class FlightOriginalData
{
    public const int DefaultBaseAddress = 0x10000;
    public const int MinimumEndAddress = 0x49a12;
    private readonly byte[] flat;
    private readonly byte[] reference;
    public int BaseAddress { get; }
    public FlightDataIdentity Identity { get; }
    public FlightReferenceTables ComponentTables { get; }
    public bool OriginalStockCertified => false;

    private FlightOriginalData(byte[] flat, byte[] reference, FlightDataIdentity identity, int baseAddress)
    {
        this.flat = flat;
        this.reference = reference;
        Identity = identity;
        BaseAddress = baseAddress;
        short[] Words(int address, int count, bool relocated = false)
        {
            var result = new short[count];
            for (var i = 0; i < count; i++) result[i] = relocated ? ReferenceWord(address + i * 2) : FlatWord(address + i * 2);
            return result;
        }
        ComponentTables = new(
            Words(0x425d8, 62), Words(0x42654, 64), Words(0x2ab18, 31), Words(0x2aad8, 31),
            Words(0x436e8 - 1800, FlightReferenceTables.GustSineWordCount, true), Words(0x208e8, 257));
    }

    /// <summary>
    /// Both images are REQUIRED. Flat pointers stay flat; the reference-relocated
    /// numeric lookup spans reproduce the selected reference machine's operands.
    /// This does not read an archive/ADF, unpack hunks, or perform relocations.
    /// </summary>
    public static FlightOriginalData FromImages(
        ReadOnlySpan<byte> flatImage, ReadOnlySpan<byte> referenceRelocatedImage,
        FlightDataIdentity identity, int baseAddress = DefaultBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (baseAddress != DefaultBaseAddress)
            throw new ArgumentOutOfRangeException(nameof(baseAddress), "This specification uses the flat 0x10000 convention, not LoadSeg addresses.");
        if (flatImage.Length < MinimumEndAddress - baseAddress || referenceRelocatedImage.Length != flatImage.Length)
            throw new ArgumentException("Supply equal-length complete flat/reference images covering at least 0x10000..0x49a11.");
        static void Digest(ReadOnlySpan<byte> bytes, string expected, string name)
        {
            if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new ArgumentException($"A complete SHA-256 is required for {name}.");
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{name} SHA-256 does not match the extraction receipt.");
        }
        Digest(flatImage, identity.FlatImageSha256, "flat image");
        Digest(referenceRelocatedImage, identity.ReferenceImageSha256, "reference image");
        if (identity.ExecutableSha256 is null || identity.ExecutableSha256.Length != 64 ||
            !identity.ExecutableSha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(identity.Variant) || string.IsNullOrWhiteSpace(identity.RelocationProfile))
            throw new ArgumentException("Executable digest, honest variant identity and relocation profile are required.", nameof(identity));
        return new(flatImage.ToArray(), referenceRelocatedImage.ToArray(), identity, baseAddress);
    }

    private int Offset(int address, int bytes)
    {
        var index = (long)address - BaseAddress;
        if (index < 0 || index + bytes > flat.Length)
            throw new NotSupportedException($"Original-data read 0x{address:x} (+{bytes}) is outside the supplied image.");
        return (int)index;
    }
    internal short FlatWord(int address)
    {
        var i = Offset(address, 2);
        return unchecked((short)((flat[i] << 8) | flat[i + 1]));
    }
    internal int FlatLong(int address)
        => unchecked(((int)(ushort)FlatWord(address) << 16) | (ushort)FlatWord(address + 2));
    internal short ReferenceWord(int address)
    {
        var i = Offset(address, 2);
        return unchecked((short)((reference[i] << 8) | reference[i + 1]));
    }
}

public readonly record struct FlightMatrix(
    short M0, short M1, short M2, short M3, short M4, short M5, short M6, short M7, short M8)
{
    public static FlightMatrix Identity => new(16384, 0, 0, 0, 16384, 0, 0, 0, 16384);
    public short this[int index] => index switch
    {
        0 => M0, 1 => M1, 2 => M2, 3 => M3, 4 => M4, 5 => M5, 6 => M6, 7 => M7, 8 => M8,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    public short[] ToArray() => [M0, M1, M2, M3, M4, M5, M6, M7, M8];
    public static FlightMatrix FromWords(ReadOnlySpan<short> words)
    {
        if (words.Length != 9) throw new ArgumentException("A matrix contains nine words.", nameof(words));
        return new(words[0], words[1], words[2], words[3], words[4], words[5], words[6], words[7], words[8]);
    }
}

public enum FlightContactVertexPolicy
{
    /// <summary>State.Vertex0..3 and ModelPartId are the previous original draw's outputs.</summary>
    SuppliedPreviousDraw,
    /// <summary>Explicit no-original-renderer profile: rotate the supplied near-model vertices using the entry matrix.</summary>
    RotateModelAtEntry
}

public sealed class FlightPlayerModel
{
    private readonly RawVector[] vertices;
    public ReadOnlySpan<RawVector> Vertices => vertices;
    public byte UnitShift { get; }
    public FlightPlayerModel(ReadOnlySpan<RawVector> vertices, byte unitShift)
    {
        if (vertices.Length < 4) throw new ArgumentException("The player's first four actual near-model vertices are required.", nameof(vertices));
        if (unitShift > 15) throw new ArgumentOutOfRangeException(nameof(unitShift));
        this.vertices = vertices.ToArray();
        UnitShift = unitShift;
    }
}

public readonly record struct FlightContactBox(short MaximumY, short MinimumX, short MaximumX, short MinimumZ, short MaximumZ);
public readonly record struct FlightContactPlane(RawVector Reference, RawVector Normal);

public sealed record FlightLandmarkNode(FlightContactBox Box, IReadOnlyList<FlightContactPlane> Planes);
public sealed record FlightLandmark(byte CellX, byte CellZ, FlightContactBox Box, IReadOnlyList<FlightLandmarkNode> Nodes, byte Ordinal = 0);

/// <summary>Only the carrier/second-ship contact record, not ship AI or a world entity manager.</summary>
public sealed class FlightCarrier
{
    private readonly RawVector[] vertices;
    public ReadOnlySpan<RawVector> RotatedVertices => vertices;
    public ushort Status { get; init; }
    public byte AircraftClass { get; init; } = 0x20;
    public byte Group { get; init; }
    public byte UnitShift { get; init; } = 3;
    public RawPosition Position { get; init; }
    public FlightMatrix MatrixB { get; init; } = FlightMatrix.Identity;
    public short Pitch { get; init; }
    public short Heading { get; init; }
    public short Roll { get; init; }

    /// <param name="rotatedVertices">All 58 record slots, after the source Q8 draw rotation, not unrotated model units.</param>
    public FlightCarrier(ReadOnlySpan<RawVector> rotatedVertices)
    {
        if (rotatedVertices.Length != 58) throw new ArgumentException("Exactly 58 ship record vertices are required.", nameof(rotatedVertices));
        vertices = rotatedVertices.ToArray();
    }
}

public sealed class FlightEnvironment
{
    public FlightPlayerModel PlayerModel { get; }
    public FlightCarrier? Carrier { get; }
    public IReadOnlyList<FlightLandmark> Landmarks { get; }
    public int LandmarkZBias { get; }
    public FlightContactVertexPolicy VertexPolicy { get; }
    public bool CallerOwnsViewedCell { get; init; }
    public bool SuppressRearm { get; init; }
    public bool SoundSystemRunning { get; init; } = true;
    /// <summary>Optional live word resolver for genuinely dynamic image-overread addresses; null means unsupported, not zero.</summary>
    public Func<int, short?>? RuntimeWord { get; init; }

    public FlightEnvironment(FlightPlayerModel playerModel, FlightCarrier? carrier,
        IReadOnlyList<FlightLandmark> landmarks, int landmarkZBias,
        FlightContactVertexPolicy vertexPolicy = FlightContactVertexPolicy.RotateModelAtEntry)
    {
        PlayerModel = playerModel ?? throw new ArgumentNullException(nameof(playerModel));
        ArgumentNullException.ThrowIfNull(landmarks);
        if (landmarks.Count > 11) throw new ArgumentException("At most 11 source landmark slots are supported.", nameof(landmarks));
        if (landmarkZBias is < 0 or > 8191) throw new ArgumentOutOfRangeException(nameof(landmarkZBias));
        foreach (var slot in landmarks)
        {
            if (slot.Nodes.Count > 4 || slot.Nodes.Any(n => n.Planes.Count is < 1 or > 8))
                throw new ArgumentException("A landmark has at most four nodes with one to eight planes each.", nameof(landmarks));
        }
        if (!Enum.IsDefined(vertexPolicy)) throw new ArgumentOutOfRangeException(nameof(vertexPolicy));
        if (carrier != null && (carrier.UnitShift > 15 || carrier.AircraftClass is not (0x20 or 0x21)))
            throw new ArgumentException("Carrier contact supports the original class 0x20/0x21 ship shapes and shifts 0..15.", nameof(carrier));
        Carrier = carrier;
        Landmarks = landmarks.ToArray();
        LandmarkZBias = landmarkZBias;
        VertexPolicy = vertexPolicy;
    }
}

/// <summary>Full player state extends, without changing, the already verified component value record.</summary>
public record struct FlightPlayerState
{
    public FlightRawState Raw { get; set; }
    public FlightMatrix MatrixA { get; set; }
    public FlightMatrix MatrixB { get; set; }
    public RawVector Vertex0 { get; set; }
    public RawVector Vertex1 { get; set; }
    public RawVector Vertex2 { get; set; }
    public RawVector Vertex3 { get; set; }
    public short RollSine { get; set; }
    public short RollCosine { get; set; }
    public short TurnState { get; set; }
    public short TurnSign { get; set; }
    public short TurnFollower { get; set; }
    public short TurnLimit { get; set; }
    public short CoupledPitch { get; set; }
    public short DeckHeight { get; set; }
    public byte AircraftState { get; set; }
    public byte LiftOffBits { get; set; }
    public byte ModelPartId { get; set; }
    public byte ModelUnitShift { get; set; }
    public byte Group { get; set; }
    public bool BankTurnEnabled { get; set; }
    public bool FreezeSurfaces { get; set; }
    public bool ExternalView { get; set; }
    public bool CollisionsEnabled { get; set; }
    public bool Immune { get; set; }
    public bool Paused { get; set; }
    public byte ScaledMotion { get; set; }
    public byte ScaledHorizontal { get; set; }
    public ushort LandingFlags { get; set; }
    public ushort MessageCode { get; set; }
    public byte ContactFlag { get; set; }
    public byte CrashHandover { get; set; }
    public byte CrashView { get; set; }
    public byte GearSound { get; set; }
    public byte GearWarningLatch { get; set; }
    public byte CrashCount { get; set; }
    public byte EventLiftOff { get; set; }
    public byte EventTouchdown { get; set; }
    public byte EventCrash { get; set; }
    public byte EventJolt { get; set; }
    public byte EventRearm { get; set; }
    public byte EventBump { get; set; }
    public byte EventBeep { get; set; }
    public uint StepCount { get; set; }
    public short PanelSpeed { get; set; }
    public int PanelAltitude { get; set; }
    public uint DynamicReadCount { get; set; }
}
