namespace StuntRacer.Core.Compatibility.Physics;

public readonly record struct AmigaRawPosition(int X, int Y, int Z);
public readonly record struct AmigaRawAngles(ushort Yaw, ushort Pitch, ushort Roll);
public readonly record struct AmigaRawLinearVelocity(short X, short Y, short Z);
public readonly record struct AmigaRawAngularVelocity(short Yaw, short Pitch, short Roll);

public readonly record struct AmigaRawContact(
    int RoadHeight, int ActualHeight, short Compression, short PreviousCompression,
    byte Damage, byte Flags);

/// <summary>Three capture contacts; Rear is central, not two physical rear springs.</summary>
public readonly record struct AmigaRawContacts(
    AmigaRawContact FrontLeft, AmigaRawContact FrontRight, AmigaRawContact Rear);

public readonly record struct AmigaRawResources(
    byte BoostReserve, byte BoostSubunit, byte Damage, byte DamageLimit, ushort EngineRevs);

public readonly record struct AmigaRawRoadState(
    ushort Section, ushort Segment, short LateralPosition, int DistanceIntoSection);

public readonly record struct AmigaRawRuntimeState(
    uint Flags, ushort OffTrackTicks, ushort RecoveryTicks, byte ClockAccumulator,
    byte DamageCooldown);

/// <summary>
/// Version-1 candidate capture envelope, NOT a stock memory layout or a complete
/// executable save state. Every field is required in a trace, including zero values.
/// </summary>
/// <remarks>
/// Field widths here define a new interchange contract, not independently established
/// stock widths, offsets, scaling or flag meanings. The header's stateMappingSha256
/// must identify that separate capture/mapping evidence. Raw source signs/precision
/// are retained; display axes/scales must be explicit and must not mutate this state.
/// Extra original state/order dependencies require a new schema, not a dictionary
/// of optional fields or an assertion that these observations prove full parity.
/// </remarks>
public readonly record struct AmigaRawState(
    AmigaRawPosition Position,
    AmigaRawAngles Angles,
    AmigaRawLinearVelocity LinearVelocity,
    AmigaRawAngularVelocity AngularVelocity,
    AmigaRawContacts Contacts,
    AmigaRawResources Resources,
    AmigaRawRoadState Road,
    AmigaRawRuntimeState Runtime);

/// <summary>
/// New typed input envelope. Positive steering is right in the capture contract;
/// throttle/brake byte encodings and any stock-control mapping need explicit evidence.
/// </summary>
public readonly record struct AmigaRawInput(
    short Steer, byte Throttle, byte Brake, bool Boost, bool Recover);
