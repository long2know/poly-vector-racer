# Bounded original-reference flight components — numerical receipt

> **Historical first-slice receipt.** These component checks remain green, but
> the library now also has an original-data full-player implementation.
> Current APIs, original executable/table identity, complete-step/trajectory
> evidence and current artifact hashes are in `ORIGINAL-PLAYER-EVIDENCE.md`.
> Statements below about absent original data/attitude/full stepping and DLL
> hashes describe the initial component-only run, not the current capability.

Verified locally on **2026-11-18**, Linux, .NET SDK **10.0.100**, GCC
**13.3.0**. This is independently authored C# implementing functional rules,
not the Modern prototype and not a complete original-game flight implementation.

## Delivered boundary

- Library: `src/StuntRacer.FlightReference/`, `net10.0`, no package dependencies
  or references to Engine, racing Core, Modern Flight, Browser or Host.
- Native executable checks: `tests/StuntRacer.FlightReferenceChecks/`.
- Optional C adapter/binaries: ignored `artifacts/flight-source/oracle/`.
  The pinned reference checkout was not modified.
- No original executable, ADF, extracted game arrays, or C implementation bodies
  were added to either new project. No repository license changes.
- No existing projects/tests/README were edited; no branches, commits, agents,
  remote operations, downloads, SDK installations, or `.env` reads.

### Public API

Namespace: `StuntRacer.FlightReference`.

| API | Contract |
|---|---|
| `FlightRawState` | Value record containing raw source words/bytes/longs and explicitly represented warning/notice outputs. |
| `DigitalKeys`, `FlightInput` | Original digital key bits and F-key/gear/hook/brake/eject events. |
| `RawVector` | Signed-word Q14 direction input, not a velocity. |
| `RawVelocity` | Signed-long target-velocity output; `+32768` is representable. |
| `RawPosition`, `RawDisplacement` | Signed-long position and stored per-step displacement. |
| `FlightReferenceTables(...)` | Required external data, privately copied and validated; no fallback tables. |
| `new FlightReferenceKernel(tables)` | Creates a component kernel; does not initialize an original mission/aircraft placement. |
| `ApplyKeyEvents(state, input)` | F1–F10 and repeated-F1 idle request; guarded gear/hook toggles; brake/pointer reset; ejection nibble. |
| `ApplyPointerBlank(state, counterDelta)` | One original pointer-interrupt delta, including byte wrapping, disabled-mode half-delta and per-blank clamp. |
| `ConvertCommands(state)` | Digital ramps/reversal/release, both-key precedence, wrapped preset difference, ejection and control-lock rules. |
| `PropulsionEligible(state)` | Aircraft-high-nibble, active-record and out-of-control call-site tests. |
| `StepThrottleFuelSpeed(state)` | Throttle repeat/detents/reverse/afterburner, pointer follow/fire, fuel burn, all section 4 speed branches, old-speed final selection, gust and old-W sink. Throws for an ineligible record. |
| `StepControlSurfaces(state)` | Table-driven pitch/yaw/roll rates, ground steering/settling, catapult suppression, stall response, boost/decay, fuel/stall warning outputs. Not attitude integration. |
| `ComputeTargetVelocity(direction, indicatedSpeed)` | External-table vector length, source normalization/truncation and direction-times-speed arithmetic. |
| `SmoothVelocity(previous, target, sink, onGround)` | Long-velocity smoothing; unsmoothed ground X/Z; smoothed Y plus sink. No floor/contact resolution. |
| `AdvanceAirbornePosition(state)` | Active, already-airborne, non-deck position subset: altitude ceiling, signed-long wrapping, negative-X/reset-Z quirk, world limits and cell refresh. Rejects floor crossings. |
| `RefreshPositionCells(state)` | Cell/subcell/fine-coordinate bookkeeping from an already resolved position. |
| `SupportedStages`, `RequireStage(stage)` | Explicit component support; requesting attitude/contact/collisions/missions/full-player-step throws. |

Methods other than the kernel instance methods are static on
`FlightReferenceKernel`. Caller owns pause, view eligibility, event ordering,
counter decrement and cadence. There is **no elapsed-time parameter or complete
`Step` API**. A caller cannot faithfully bridge surfaces to motion without the
omitted original attitude stage; motion therefore takes a direction explicitly.
AI response-table selection is exercised, but global-output propagation and
complete AI behavior are not supplied.

Units: signed 16-bit speed/rate/angle stores wrap; shifts are arithmetic and
division truncates toward zero. A circle is 28800 angle units, a knot 12 speed
words, a foot 1024/5 position units, and displacement is four times velocity
with the source sign convention. Long negation/addition/multiplication wrap
where required. Numeric public inputs are integer typed, so NaN/infinity cannot
enter these APIs.

### Required external data and explicit limits

Constructor argument order/counts:

1. Player response: **62** signed words, nonnegative.
2. AI response: **64** signed words, nonnegative.
3. Gust: **31** signed words, nonnegative.
4. Sink: **31** signed words, nonnegative.
5. Gust sine/address window: **5897** words, indices **-900..4996**.
   The actual quarter-wave portion 0..900 is constrained to 0..16384.
   Adjacent words are opaque caller data, **not folded/generated by the kernel**.
6. Vector-length lookup: **257** nondecreasing Q14 words, beginning at 16384.

Incorrect lengths, invalid table values, invalid throttle/command/pointer/entity
inputs, out-of-range Q14 directions/target velocities and non-multiple-of-four
stored displacements are rejected. The four gust-window indices **1264, 1265,
1266 and 3036** intersect run-time text bytes described in section 17.9: they
throw `NotSupportedException`, rather than treating a static table or the C
port's counted-zero fallback as live original data.

Direction components are bounded to -16384..16384; velocity targets to
-32768..32768. Ground/deck floor resolution, lift-off/touchdown, scaled-motion
switches, clock statistics, gear/ejection animation and recording/playback are
not implemented as a complete motion/world pass.

## Provenance and reproducible commands

Behavior specification and C component oracle:

`artifacts/flight-source/reference/interceptor-cleanroom`

Upstream **st-gr/interceptor-port**, pinned commit:

`2475dcd65f454a37b41a2be27629ad958f34ddc5`

Rules were authored from `docs/re/05-flight-model.md` sections 2–5, 7 and
addenda, particularly 17.4/17.5/17.7/17.9/17.10/17.18. After initial independent
implementation, C-component comparisons and narrow inspections resolved the
clarifications below. This is not a claim of authors being unexposed to the
oracle source. No C bodies were translated line-for-line or copied into C#.

Only the cleanroom flat-image convention (base 0x10000) is relevant. The
separately pinned Grumbel checkout at
`73a1f3ef297c51c4648bcad9340f46ddea4e8f24` was not a numerical baseline; no
LoadSeg 0x20000/address-header conventions were mixed in.

Read the pinned checkout's Makefile before building. The command below compiles
only the local adapter and C core dependencies; no upstream scripts, SDL,
original assets, installers, submodules or network access are required:

```sh
gcc -std=c99 -O2 -Wall -Wextra -fwrapv -ffunction-sections -fdata-sections \
  artifacts/flight-source/oracle/flight_reference_oracle.c \
  artifacts/flight-source/reference/interceptor-cleanroom/src/core/flight_coll.c \
  artifacts/flight-source/reference/interceptor-cleanroom/src/core/fx.c \
  -Wl,--gc-sections -lm \
  -o artifacts/flight-source/oracle/flight_reference_oracle

dotnet restore tests/StuntRacer.FlightReferenceChecks/StuntRacer.FlightReferenceChecks.csproj \
  --configfile tests/StuntRacer.FlightReferenceChecks/NuGet.Config -p:NuGetAudit=false
dotnet build tests/StuntRacer.FlightReferenceChecks/StuntRacer.FlightReferenceChecks.csproj \
  --no-restore -c Release
dotnet run --project tests/StuntRacer.FlightReferenceChecks/StuntRacer.FlightReferenceChecks.csproj \
  --no-build --no-restore -c Release -- \
  --oracle "$PWD/artifacts/flight-source/oracle/flight_reference_oracle"
```

The adapter includes the unmodified pinned `flight.c` to invoke its actual
static routines. It does **not** substitute authored arithmetic for those
routines. C is a separate, local GPL oracle process, not a linked or distributed
dependency of the C# library. Both sides receive exactly the same synthetic
table words and scalar states via a value protocol, with no struct-padding
assumptions. All 62 exported raw-state scalars and three target-velocity
components are compared for every case; the adapter is **not** comparing the
entire C `flight_state_t` or original game variable block.

Checks without `--oracle` explicitly print **DIFFERENTIAL NOT RUN**. An absent
adapter or wrong pinned `flight.c` hash fails the requested differential run.

### SHA-256 receipt

| File | SHA-256 |
|---|---|
| Pinned `docs/re/05-flight-model.md` | `c4f7fab22fff7119c95f66c60565c09d589d0a1e779def3a0ec6999f5e7f3161` |
| Pinned `src/core/flight.c` | `2158ead620b2d5919236c2cc08c33d0a2c8c812bf8e906c8705fca32427e3f28` |
| Pinned `src/core/flight_coll.c` | `27425fa951bebf2f09e3995c0cfa657b41b437a045cdf0d8c66c6127af6dc366` |
| Pinned `src/core/fx.c` | `5d370e6126bd9f94648cfd8ac8b124ad49aaad086169d031a799b198d3593a62` |
| Ignored adapter `.c` | `8015d5c9595ee472afb1a8a41b945227ffe9ed5acac3a445141012a191c12f37` |
| Normal oracle executable | `5df9ac103523cd083c48cb34d66cb2f8827adeb4adcf15a70bb23799be814e3e` |
| Release library DLL | `c87e12a4104990e4100ba0916d37204bd6583a6eba4173cf3467e884494e755c` |
| Release checks DLL | `992969118f92f4d821993f6abe1979679921bde4fd69698368e6d249d500d526` |

## Actual result

**Release build: 0 warnings, 0 errors. Checks: 40 passed, 0 failed**
(39 direct regression groups plus one differential group).

Two explicitly invented data profiles are used. One has moderate gains and
mathematically generated sine/length words. The other stresses signed-word
overflow with saturated response gains, large gust coefficients and invented
opaque words outside the mathematical quarter wave. Neither profile is
presented as original aircraft tuning or stock data.

| Actual pinned C routine/output slice | Cases, both table profiles combined |
|---|---:|
| `input_convert` | 4098 |
| `speed_step` | 5964 |
| `surfaces` | 3600 |
| `target_velocity` | 360 |
| `motion_step`, already-airborne velocity/Y slice | 724 |
| `update_cells` | 800 |
| **Total** | **15546** |

**1,010,490 scalar comparisons; 0 mismatched cases; 0 mismatched fields;
maximum absolute numerical discrepancy 0.**

Coverage includes all 256 digital key bytes, reversal/release, signed-byte
preset wrapping, both throttle keys, detents and afterburner cut, exact fuel
burns, ground/air/idle/normal/afterburner/reverse, rough/pad/catapult conditions,
hook/brake/belly/skid priority, negative arithmetic, low-speed sink, signed-word
and long boundaries, warning thresholds and protocol-preservation sentinels.
Event handlers/pointer interrupt and airborne X/Y/Z advancement have direct
regressions, **not an independent component-oracle verdict**. Ground X/Z
smoothing has direct checks; the `motion_step` differential is airborne only.

### Corrections/clarifications exposed by numerical checking

The initial 15540-case run had 1314 mismatched cases / 2426 fields (maximum
absolute discrepancy 65536), not a passing parity result. Corrections concerned
warning predicates/record context, pointer follow/fire ordering, reverse clamp,
velocity width and cell bookkeeping; two hand-calculated ground expectations
were also corrected. Expanded long-boundary coverage exposed the order of
wrapped negation, with a transient discrepancy of 3221225472 before correction.
Final counts/errors above are from the strengthened passing run.

Where prose is incomplete or inconsistent, the following are **observed pinned
C-component behaviors**, not newly established original-68000 facts:

- Pointer fire uses the entry distance, before following can rewrite P/Q.
- Negative fuel is normalized to zero on every surface pass; afterburner-cut
  and sound side effects remain once-only. Section 17.5's wording can imply
  that normalization itself is once-only.
- Stall warning's “wheels up” means not on the ground, not gear retract progress.
- The standalone cell helper requests refresh on subcell change, not cell-word
  change alone, and uses the **pre-refresh** cell for neighbour nearness.
- Target velocity and smoothed velocity are longs, not speed words. NEG.L of
  stored displacement wraps **before** division by four; INT_MIN is not
  equivalent to negating after division.
- Non-player global-warning propagation is outside this component boundary;
  internal routine outputs are not proof of full AI/world behavior.

### Negative-control verification

Compiled the same adapter with `-DFLIGHT_REFERENCE_MUTATION_PROBE` and output
name `flight_reference_oracle_mutant`. This deliberately flips one bit of the
real `speed_step` target-speed output; the native process still exits zero.
The numerical checks correctly exited **1**:

**5964 differing cases, 5964 differing fields, maximum absolute discrepancy 1.**

Mutant executable SHA-256:
`b03fadb8bcb6356e893453bb64cb8919d7044336c0882234892569eab8915352`.
The normal oracle was rebuilt without that macro and the complete 40-check
suite passed again.

## What this does NOT prove or implement

No original ADF/data was used. This proves bounded equivalence to the pinned
native C **source components with invented inputs**, not stock table identity,
an original 68000/machine verdict, original timing, full-kernel equivalence,
whole-game parity, or a WebGL/WASM integration/performance result.

Full attitude matrix/gimbal/rotation composition and stall attitude integration,
terrain and ship contact/collisions, landmark scans, complete AI, weapons,
missions, rearm/statistics and complete world/player stepping remain unavailable.
Modern Flight and the racing/browser projects remain untouched. Termination:
the bounded component slice builds and its focused checks pass; integration
and any broader fidelity work belong to a separate task.
