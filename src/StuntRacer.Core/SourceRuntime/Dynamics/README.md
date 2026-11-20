# Optional reference-derived player

`SourceAmigaSimulation.Create(SourceAmigaPackage)` returns an `IGameSession`.
`Track` is the package's exact canonical `TrackDefinition`, not a replacement
authored circuit. `Snapshot`, `Advance(double, DriverInput)` and
`Command(string)` retain the shared DTO/command contract. Nothing here selects
this session automatically or changes the default authored 120 Hz model.

`Command("start")` starts the reference drop immediately, without a countdown.
`Step(input)` performs one integer movement tick while racing; it does not change
the wall accumulator. `pause`, `resume`, `restart` and `recover` are supported.
`RawState` is an immutable observation, with no position/state setters.

## Clock and observation

The configured reference pace is 50 callbacks/second and six callbacks/movement:
**0.12 seconds/tick**, not stock-PAL certification. Startup is deliberately
normalized to the first full interval, unlike the initially open source gate.
`Advance` consumes every elapsed tick; it never inherits the authored clock's
250 ms discard policy. Invalid/nonfinite deltas and calls longer than 3600 seconds
throw instead of discarding time. Interpolation only projects the prior/current
raw observations. `Snapshot` is authoritative current pose.

State consists of signed int32 positions/velocities/accelerations, wrapped uint16
angles, integer counters/resources and exactly three contact records (FL, FR,
rear-central). Multiplications use signed int64 intermediates; this is a declared
candidate-width contract, not LP64/68000 overflow certification.
`BodyVelocityX/Z` are the source stage's transformation of the incoming world
velocity; `WorldVelocityX/Y/Z` are the new, integrated values. Body Y speed is zero
in the selected routine. Each contact exposes old/new penetration, response,
actual/road height, geometric road membership and its independent damage byte.

## Implemented functional rules

The implementation is fresh mathematical code based on Lead/Fact Checker's
pinned `ptitSeb/stuntcarremake` facts, not translated upstream bodies:

- Q14 source Euler basis; coefficient division truncates toward zero, while
  velocity/acceleration transforms shift each product arithmetically.
- Previous-contact control gate, digital steering/latching throttle/brake,
  signed boost subcounter, final-reserve boost tick and no recharge.
- Gravity, three spring/previous-penetration responses, nested half-averages,
  external inclination table, collision/grip/engine wind reduction and drag.
- Wrapped steering/angular integration, source pitch/roll limits and airborne
  pitch tendency; active remake position multiplication, not its archived branch.
- Ordered per-contact damage, shared damage counter, once-per-tick damage/hole
  update, off-road floor/edge falloff and strict later-call `offRoadTicks > 64`
  reset/eligible-section placement. Chains and opponents are absent as declared.

`SourceDynamicsMath`, `SourcePlayerControl` and `SourceSnapshotAdapter` expose
actual component operations used by the kernel for independent numeric checks;
they are not no-op oracle adapters.

## Geometry, presentation and limits

The package supplies all real layout/common/lookup data. The source full lookup
has 81920 sine entries and direct angle-plus-quarter-turn cosine indexing.
65536-entry synthetic packages are supported with explicitly periodic cosine
indexing, a fixture/candidate difference rather than full source-table identity.

One native strip supplies both display and the integer triangle/nearest-edge
sampler. This is **not** the remake's arc/bilinear sampler or stock ASM sampler.
Steering observes per-segment geometric heading, another declared candidate
difference. Off-edge distances are integer PC units capped above the only needed
64-unit threshold; no display floating-point geometry feeds the kernel.

Native road `HeightQ14` is stored-H times 16384; source wheel height is
`(HeightQ14 / 2) >> 11`, retaining truncation before shift. Display X/Z is
`raw/16384*scale`; display Y is `raw/4096*scale`, positive up. The old C++ view
pointer's Y minus is not physical. Source positive roll lowers the right side,
so snapshot roll is negated. The existing authored body mesh receives a separate
64-PC-unit presentation-origin lift; rear compression is duplicated only to the
two visual rear wheels. Neither adjustment changes contacts or raw state.

`Metadata` identifies profile `amiga-derived-source-compatible`,
`OriginalStockParity=false`, clock phase, sampler/heading/input adaptations and
presentation lift. Positive analogue axes mean pressed source buttons, not
continuous source throttle/steering. The three-lap practice UI uses step time,
not original championship, opponent or BCD race-timer semantics. Native component
comparisons do not certify a whole player, original stock physics, timing or
redistribution rights. Private inputs remain external; no original arrays/assets
or executable bodies are included in these sources.
