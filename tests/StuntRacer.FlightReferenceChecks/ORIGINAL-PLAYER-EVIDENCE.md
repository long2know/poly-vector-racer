# Original-data complete player kernel — numerical receipt

Completed **2026-11-18**, Linux, .NET SDK 10.0.100 / GCC 13.3.0.
The library source was frozen for parent runtime integration after the complete
original-data proof passed. No Browser/JS/UI/Host, racing Core/Engine, authored
Modern Flight, ingestion project, existing tests, README or repository license
was modified in this lane. No branches/commits/pushes/SDK installs/new agents.
Existing ingestion-agent coordination was explicitly requested by the parent.

## What is implemented

`FlightPlayerKernel` is a runnable, ordered single-player **aircraft Free Flight**
kernel using original external data, not a call to Modern dynamics or a chain
of success-shaped placeholders:

1. Key-handler events, pause and original world-update pre-steps.
2. External dying/idle gates and counter semantics.
3. Command ramps/presets, direct warnings and throttle/fuel/speed/gust/sink.
4. Control surfaces, source-word sine folding, both Q14 matrix builders,
   bank-turn coupling, matrix composition, shared-divisor quotient rounding,
   pole handling, recentre/override/stall and the gimbal guard/kicks.
5. Full velocity smoothing, floor/deck altitude, lift-off/touchdown, hard
   touchdown/crash, rolling/stop/rearm outputs, X/Z/cell bookkeeping.
6. Gear progress/hook lamp, real terrain/pad/water rules, ship deck polygons,
   hull volumes/arrestor corridor, landmark boxes/planes and model ground test.
7. Contact result handling, no-result tail, frame/panel caches and event outputs.

The old component APIs and their checks are preserved. Their
`FlightReferenceKernel.RequireStage(FullPlayerStep)` still rejects that request:
full stepping belongs to the new `FlightPlayerKernel`, which requires the full
original-data contract and advertises `FullPlayerStep` in its own stage mask.

### Public integration API

Namespace `StuntRacer.FlightReference`; no dependency on the parent ingestion
library or any browser/runtime project:

```csharp
var data = FlightOriginalData.FromImages(
    flatImage, referenceImage,
    new FlightDataIdentity(executableSha256, flatSha256, referenceSha256,
        honestVariant, referenceRelocationProfile));
var kernel = new FlightPlayerKernel(data);
var environment = new FlightEnvironment(
    new FlightPlayerModel(actualSourceVertices, unitShift: 3),
    actualCarrierOrNull, originalLandmarkSnapshot, landmarkZBias: 48,
    FlightContactVertexPolicy.RotateModelAtEntry);
FlightPlayerState state = kernel.CreatePlayer(initialRawState, environment);
state = kernel.SetAttitude(state, pitch, heading, roll);
state = kernel.StepPlayer(state, new FlightInput(keys), environment);
```

- Buffers are full images indexed from **flat base 0x10000**, of equal length,
  covering at least through 0x49a11. No LoadSeg/header-offset mixing.
- Both buffer SHA-256 values are checked and copies made. The flat buffer
  supplies ordinary data/pointers; the reference-relocated buffer supplies
  numeric sine/arcsine/pitch-scale windows and neighbouring operand words.
- `FlightPlayerState.Raw` preserves the old raw component record; the new
  state adds both matrices, contact vertices, turn/gear/contact/crash/event
  fields. `FlightMatrix`, `FlightAngles`, `FlightContactBox/Plane`,
  `FlightLandmark/Node`, `FlightPlayerModel`, `FlightCarrier` and
  `FlightEnvironment` are typed public inputs.
- `BuildMatrixA`, `BuildMatrixB`, `BuildInverseAttitudeMatrix`,
  `ComposeAttitude`, `StepAttitude`, `QueryTerrain`, `RefreshContactVertices`
  and static `RotateContactVertices` are public numerical/adapter helpers.
- Source collision shift L is **3**, not the model-to-position exponent **5**.
  The player uses the first four exact source model-4 vertices; carrier contact
  uses the first 58 exact model-8 vertices, source-draw rotated.
- Caller owns scheduling (parent chose reference 8 Hz), the supplied world/
  previous-draw snapshot and visual conversion. `state.Paused` reproduces the
  source paused pre-steps. Events retain native sticky-until-consumed behavior.
  Parent owns the separate `IFlightSession` wrapper and event/audio consumer.

The frame has no float/elapsed-time transition ownership. Positions/velocity
are source longs, speed/rates/angles source words; all relevant wrap, arithmetic
shifts and truncated divisions are explicit.

## Actual original input identity

The input is the user's supplied archive, not a downloaded replacement.
It is a **renamed/cracked/modified variant**, not asserted identical to retail:

| Input | Identity |
|---|---|
| ZIP | 476196 bytes; SHA-256 `8364ffb955558fa6b31c1c51ddee09e519a4f1c2126b88743c3bd132735d1cff` |
| ADF | 901120 bytes, DOS0 OFS, ZIP CRC d69fad72; SHA-256 `7fdde9ef981deda7c2b4d338601b11d7ae63a83cf530956f4bcf36e4e084828b` |
| Actual executable | Root **x1**, 331232 bytes; SHA-256 `38b6e1f331460aa4939690173d95c38b105d1d089226359a4c8655052ea29ab5` |
| Layout | 185 hunks, 8441 RELOC32 entries, 298988-byte flat image |
| Flat image | `40388db2b579abf89048a844b5502b79e8818c26b9253eba83705529f32a35a2` |
| Reference numeric-window image | `46522e1f13bc01d9bb0fbf09554d3f9b6d89bcd7cba6e21a2981ff212fda4ca0` |
| Fully target-RAM-relocated image, data-agent receipt | `ccc6143347a4c23e4d634435bc8c67d5a271d88489a29007fc4b483c180444b2` |
| Final production package | `3fa1c540b6618503f9795498607de67014897f345533bc6c9c99ce0b1614cd68` |
| Scene snapshot | `c51b9a6ae134fd99a3fc95bab7d751090497f2bbd45c6c5d2360d8d2d6d86fcd` |

The separate crack intro is not the game executable. No filename assumption or
blanket original-data/license stop was used by the kernel: it consumes the
parent's verified image contract. Archive/disk/source bytes remain private,
ignored and unmodified. No original numeric table/model arrays or C bodies are
embedded in tracked C# or test fixtures.

Original fixtures used here:

- Production C# package/scene/images/tables:
  `artifacts/flight-source/ingestion/user-d69fad72/`.
- Parent's independent nonexecuting upstream extraction:
  `artifacts/flight-source/private/upstream-user-d69fad72/` and
  `private/upstream-complete-user-d69fad72/`.
- The 12 actual flight tables, source model/contact vertices and 13 genuine
  landmark footprints (14 nodes / 66 planes) are used, not invented replacements.
  All source table/image bytes were independently checked by the ingestion
  owner against the upstream extraction. Footprint boxes/planes were also
  checked against the separate original `lists_land` decoder.

## Actual C# versus whole-player native C result

Pinned source oracle: **st-gr/interceptor-port**
`2475dcd65f454a37b41a2be27629ad958f34ddc5`.
The original checkout is unmodified. The ignored full adapter includes its
actual `flight.c` and invokes its routines; the real table loader, collision
implementation, asset loader and fixed-point routines are compiled alongside.
This is a separate local process, not linked/distributed GPL code in C#.

**Release build: 0 warnings / 0 errors. Original-data run: 40 checks passed,
0 failed** (39 preserved direct regression groups plus the full-player group).
The separate preserved component run also passes **40/40**, including all
**15546 synthetic component cases / 1010490 scalar comparisons**, zero errors.

The whole-player value protocol compares **all 132 exposed stored player
scalars** (including 18 matrix elements, 12 transformed-vertex elements,
position/velocity, rates, flags, fuel, turn/contact/gear/events and caches).
Its inventory is checked against the public state. It is not a comparison of
unrepresented combat/mission variables or an entire Amiga memory dump.

| Original-data stage | Cases |
|---|---:|
| Matrix A | 147 |
| Matrix B | 147 |
| Full matrix/rate composition, including poles/rounding | 196 |
| Surface/attitude/bank/stall/override/gimbal guard | 422 |
| Actual terrain/water/pad positions | 166 |
| Ordered complete player steps | 1552 |
| **Total** | **2630** |

**347160 scalar comparisons, 0 mismatched cases, 0 differing fields,
maximum absolute numerical discrepancy 0.**

Complete player steps include **1319 steps over 15 trajectories** and
**233 contact snapshots** (9 hull + 224 landmark cases). Native state is retained
across every trajectory: the adapter ignores the supplied C# state after the
first frame, so accumulated divergence is not erased by reseeding.
For all 209 ship-bearing cases the adapter also calls the pinned
`flight_ship_build` on the actual unrotated original carrier vertices and
byte-compares its matrix and all 58 rotated slots with the managed helper
before using the independently native-built contact geometry.

| Retained-native trajectory | Steps | Required outcome observed and compared |
|---|---:|---|
| Runway idle | 24 | Grounded at floor |
| Runway throttle/takeoff | 220 | Real lift-off; final y 1636476, S/W 7128/7128 |
| Banked manoeuvre and gear | 180 | Changed attitude/velocity and gear transitions |
| Soft landing/stop/rearm | 220 | Touchdown, stopped, rearm output |
| Hard touchdown/external expiry | 40 | Crash event, native dying/countdown path |
| Low-speed stall/descent | 90 | Stall flag, sink and crash behavior |
| Carrier catapult | 100 | Deck release/lift-off; final y 14996, S/W 3557/3557 |
| Arrestor approach/catch | 100 | Actual hook corridor catch and S/W 0/0 |
| Wheel-brake stop | 110 | S 0 |
| Paused key/gear-warning pre-steps | 8 | No step/countdown advance |
| Frozen integration/surface state | 30 | Source freeze semantics, rates still represented |
| Hook lamp/gear transitions | 40 | Source event/progress behavior |
| Fuel-exhaustion glide | 100 | Fuel/throttle 0, subsequent descent |
| Ceiling | 12 | y clamped at 8388608 |
| Actual water/gear loss/ditching | 45 | Gear loss/skid and landing behavior |

The real hull snapshots hit **9/9**. The landmark inputs supply all 13 original
definitions / 14 nodes / 66 planes, translated into an explicit slot snapshot;
**12/224** cases hit and the boundary/previous-contact/miss cases are also
compared. These are source-rule contact tests, not a claim that the original
display-list builder's visibility/order/staleness has been reproduced.

### Numerical correction and mutation gate

The first original-data run had 76 differing frames, all the same cosmetic
`GroundSoundVolume` predicate: a crashed/dying/water nose-settle must not request
the live-aircraft contact sound. One targeted correction added the documented
active/not-crashed/not-water conditions. Physics/matrices/other fields already
agreed; the corrected and expanded run above has zero discrepancies.

For the negative control the adapter is compiled with
`-DPLAYER_ORACLE_MUTATION_PROBE`: after the **real** native complete player call,
one returned matrix-B word is deliberately bit-flipped. Native exit stays zero.
The numerical checks correctly exit **1**, detecting **1528 bad cases /
1528 differing MatrixB[2] fields / maximum absolute discrepancy 1**.
This is a numeric-output gate, not a process-exit/status-only check.

## Actual original 68000 evidence supplied by the parent

The parent separately built the unmodified pinned C/Musashi rig against the
same supplied `x1` image and actual extracted assets. This lane did not rebuild,
edit or share outputs with that worktree.

Read-back evidence:
`artifacts/flight-source/oracle/original-user-d69fad72-flight.log`

SHA-256:
`881337fd50690dd7ec5640585bc74d2c8275d07ee2484450d914a18ef33ac138`

It reports the worked runway/air/takeoff/stall/touchdown/catapult/arrestor
examples matching, 100 envelope sequences / 8000 steps and 100 adversarial
sequences / 8000 steps with zero diverged, 433216 matrix-angle sets with zero
compared differences, 300 deck + 300 seat-viewed deck sweeps with zero
differences, 3179 terrain positions with zero differences, and **0 problems**.
The parent's Musashi pin is
`313ebf1bd9f4d0d93341eb5ce21fd8a119e9dbdd`.

Important exclusions are preserved, not hidden: 235 matrix reads touching
run-time text were counted but not compared; the rig describes an unavailable
runtime-memory sequence endpoint, and `V:4A910` is not compared in 2733 steps
after a seat spawn. The seat-viewed sweep is explicitly informational.

This triangulates **C# ↔ pinned C on these actual inputs** with the parent's
**pinned C ↔ original 68000** result. It is not a claim that every C# case was
directly replayed against the 68000 or that all original machine state, drawing,
timing and retail-stock identity are certified.

## Reproduction

From the repository root, with the supplied private package already produced:

```sh
gcc -std=c99 -O2 -Wall -Wextra -fwrapv -ffunction-sections -fdata-sections \
  artifacts/flight-source/oracle/flight_player_oracle.c \
  artifacts/flight-source/reference/interceptor-cleanroom/src/core/flight_tables.c \
  artifacts/flight-source/reference/interceptor-cleanroom/src/core/assets.c \
  artifacts/flight-source/reference/interceptor-cleanroom/src/core/flight_coll.c \
  artifacts/flight-source/reference/interceptor-cleanroom/src/core/fx.c \
  -Wl,--gc-sections -lm -o artifacts/flight-source/oracle/flight_player_oracle

dotnet restore tests/StuntRacer.FlightReferenceChecks/StuntRacer.FlightReferenceChecks.csproj \
  --configfile tests/StuntRacer.FlightReferenceChecks/NuGet.Config -p:NuGetAudit=false
dotnet build tests/StuntRacer.FlightReferenceChecks/StuntRacer.FlightReferenceChecks.csproj \
  --no-restore -c Release
dotnet tests/StuntRacer.FlightReferenceChecks/bin/Release/net10.0/StuntRacer.FlightReferenceChecks.dll \
  --player-oracle "$PWD/artifacts/flight-source/oracle/flight_player_oracle" \
  --original-data "$PWD/artifacts/flight-source/ingestion/user-d69fad72"
```

For the full-player mutation control, use the same GCC command with
`-DPLAYER_ORACLE_MUTATION_PROBE` and output name `flight_player_oracle_mutant`,
then pass that executable to the same checks; failure is required.
The prior component command is documented in `EVIDENCE.md` and remains valid.

### Current artifact SHA-256 receipt

| Artifact | SHA-256 |
|---|---|
| Release kernel DLL | `346a4e1e9d16a73e994ffa6685eeb80ff7c675b801384abfba27557fd1febb76` |
| Release checks DLL | `c5ae7c1623061a3a6498ce58d7aefbe8ca693c8161139e07c948d068befe6f82` |
| Full C adapter | `fc5c01a088686041ab4b1e5b27b536eab47952c8e71f6dff5cfb5d1177023730` |
| Normal full oracle | `93615b6b11407ce670b3d0d791bd8c1b865ac6ae379416c1456bbb3770fac2e9` |
| Mutant full oracle | `c22c379fd76723d1191fbe5af7ff3bfdc976395ea048c3d22122cc8404a27d30` |
| Pinned `flight.c` | `2158ead620b2d5919236c2cc08c33d0a2c8c812bf8e906c8705fca32427e3f28` |
| Pinned `flight_coll.c` | `27425fa951bebf2f09e3995c0cfa657b41b437a045cdf0d8c66c6127af6dc366` |
| Pinned `fx.c` | `5d370e6126bd9f94648cfd8ac8b124ad49aaad086169d031a799b198d3593a62` |
| Pinned `flight_tables.c` | `811b6d0e3568b405b8affa08ae84a01cfcc62cdcba171520c4f096e37d1974e1` |
| Pinned `assets.c` | `4cde902ccc24a2fb96969a60da6c4701b8760288ca826aef530b58a4f94d9793` |

## Remaining explicit boundaries

- **No blanket stock certification.** Variant provenance is explicit and
  `FlightOriginalData.OriginalStockCertified` stays false.
- Combat, AI/entity-world management, ejection/seat animation, mission dispatcher,
  crash-menu/lives/restart, original display lists/screens and original audio are
  not this aircraft-only Free Flight task. Ejection/non-player/scaled-motion
  requests are rejected; original crash/rearm outputs are exposed for the parent.
- A source step consumes a scene/draw snapshot. `RotateModelAtEntry` is an
  explicit renderer-free profile; `SuppliedPreviousDraw` uses the caller's exact
  prior slots/part id. No implicit claim of the original animated draw program,
  pitch-band/eye/LOD part-id schedule or active-landmark queue construction.
- Unknown live image over-reads require `FlightEnvironment.RuntimeWord` and
  otherwise throw atomically. Mapped represented word globals are read from current
  state. Explicit missing-runtime rejection and resolver consumption are tested;
  no silent counted-zero fallback is used by the full kernel.
- The kernel does not refetch/re-extract the archive, choose mission starts or
  own wall-clock/render pacing. Parent owns `FlightOriginal` session/browser
  integration and the production data reader. No browser/performance result is
  claimed by this numerical lane.

Termination: a genuine ordered full aircraft step, actual takeoff/landing/crash/
carrier/contact behavior and original-data native trajectories are implemented
and numerically green; no component-only/unavailable-attitude finish remains.
