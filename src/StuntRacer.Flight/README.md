# Authored Flight simulation

This independent C# game model does not contain upstream flight code, asset data,
disk images, extracted tables, or original-game geometry. It is not an emulator,
an original-interceptor parity claim, or real aircraft flight-training software.
Browser rendering, controls, audio and game selection are consumer responsibilities.

## Integration contract

`new FlightSimulation()` exposes `World`, `Snapshot`,
`Advance(double seconds, FlightInput input)` and `void Command(string command)`.
The assembly references only the shared `Engine` project. The shared clock runs at
120 Hz and accepts at most 0.25 seconds per frame, dropping excess stall time.
Invalid/non-finite elapsed time or controls throw, including in frozen phases.
Finite axes clamp to [-1,1]; absolute throttle clamps to [0,1].

The public records use properties suitable for camel-case JSON serialization.
Distances are game metres, speeds metres/second, angles radians. World axes:
+X right, +Y up, +Z forward. Positive input roll banks right, positive pitch pulls
up, positive yaw moves the nose right. Positive **render** roll raises the right
wing, so right banking gives negative `Aircraft.Roll`.
`Aircraft.Forward` and `Aircraft.Up` are authoritative orthonormal directions,
including during inverted flight and full loops. Euler angles necessarily change
representation at pitch poles; cameras should use the basis, not Euler differences.
`GForce` is approximate signed game-model load along the aircraft up axis,
not instrumentation calibrated to an F/A-18.

### World

`FlightWorldDefinition(Name, SeaHeight, Islands, LandingSurfaces, RunwayStart,
RunwayHeading, GearClearance)`. Both `FlightIsland` and `LandingSurface` have
`Name, CenterX, CenterZ, Width, Length, Height, Heading`; headings rotate +Z toward
+X. Width runs along local X, length along local Z; centers are horizontal X/Z.

Default **Interceptor Bay**:

| Geometry | Center X/Z | Width × length | Height | Heading |
|---|---|---|---|---|
| Sea | everywhere else | unbounded | 0 | — |
| Main Island | 0 / 0 | 4000 × 6000 | 8 | 0 |
| Bay Runway | 0 / 0 | 80 × 3000 | 8 | 0 |

`RunwayStart = (0,10.4,-1300)`, heading 0, `GearClearance = 2.4`. There is no
carrier. The aircraft origin is the body reference point, not the wheels. An
authored mesh approximately 11 m wide and 15 m long is appropriate. Native terrain
contacts and renderer meshes must consume the same rectangles. `Sample(x,z)`
returns `FlightSurfaceSample(Height, IsWater, Name, LandingSurface?)`;
`TerrainHeightAt(x,z)` returns that sample's height.

### World Flight Atlas

Atlas is a separate Modern flight experience. Configure `STUNT_WORLD_ATLAS` to
point at an absolute private directory outside the served WASM bundle. Atlas
uses `base.zip` and optional lowercase-slug area ZIPs. U.S. World Flight and
Seattle Scenic Drive instead use `seattle.zip` or `seattle/manifest.json`
in the same directory, without requiring `base.zip`. A malformed Atlas base
disables Atlas only; a malformed or conflicting optional Atlas pack is logged
and excluded. Restart the host to load a changed inventory, and reload the
browser to start a new Atlas revision. See the [pack builder](../../tools/WorldAtlas/README.md)
for the formats and source receipts.

Native contact samples this global WFA1 surface before launching or advancing
the same 120 Hz jet simulation. In this game projection, signed longitude and
latitude map to X and Z in metres: X wraps on `[-19922944, 19922944)`, Z includes
both poles at `±9961472`. Only outward Z velocity is removed at the poles.
There is no arrest or sea-level substitute when regional data is denied.

A regional WFT1 quartet adds detail only after all four data tiles and their
render meshes are ready, with the current terrain retained when a swap would
affect low flight. Same-pack parents and matched shared edges are validated
before serving. A regional ZIP may share a terrain key with another pack only
if both supply byte-identical WFT1; a conflict excludes the entire optional pack.
The Garage supports preset or arbitrary coordinate air starts;
high mountainous starts have extra clearance for peaks understated by the global
fallback. Airport points do not authorize landing there. Existing runway,
original flight, and Seattle behavior are unchanged. For play, not navigation.

### Flight and commands

- `start`: ready → three-second countdown → flying, initially on the runway.
  Apply throttle, rotate gently above 58 m/s, then ease the pull. Holding full
  pitch can stall the aircraft; this is not an automatic takeoff animation.
- `pause` / `resume`: freeze/restore countdown or flight, including fixed-clock
  accumulator, fuel and counters. Gear controls do not change a paused aircraft.
- `restart`: resets mission clock, fuel/counters and runway pose; starts countdown.
- `recover`: explicit return to the ready runway; increments recoveries, retains
  mission clock, fuel, landings and crashes. It does not silently refuel.
- `air-start`: explicit training spawn from ready/paused/crashed at
  `(0,258,-4000)`, 95 m/s toward +Z, slight nose-up trim, throttle .35, gear down.
  Retains mission clock, fuel and counters. Ignored during ordinary flight.
- `gear-toggle`: toggles in ready/countdown/flying; retracting on the ground will
  crash on the next active flight step.

`Time` is active simulated mission time, including countdown. Ready, paused and
crashed phases never advance it. Crashes remain crashed until an explicit command;
ordinary flight never teleports to recover. Landings count actual airborne-to-
ground transitions, not every ground tick.

The model integrates thrust, gravity, velocity-dependent lift/drag, angle-of-
attack lift loss, sideslip, aerodynamic control authority/damping, gear/airbrake
drag, wheel braking, fuel and high-throttle afterburner. There is no position
path, altitude lock or automatic bank leveling.

Only the paved runway is landable: gear down, sink no worse than 4.5 m/s, ground
speed no higher than 105 m/s, wings near level, sensible nose attitude and runway
alignment. Water, off-runway terrain, hard impact and gear-up contact crash.
Landing contacts settle the body onto the flat landing gear; individual wheel,
wingtip and strut dynamics are not simulated.

Interceptor Bay's authored limits are 450 m/s, ±200 km horizontal extent and 20 km ceiling.
Crossing the area/ceiling limit explicitly ends the flight with a recoverable
crash message, rather than wrapping position. Lift/control coefficients and
fuel/thrust tuning are game choices, not recovered original tables or military
engineering fidelity. No wind, combat, weapons, missions or external imports.

## Native checks

From the repository root, using the existing .NET SDK:

```sh
dotnet run --project tests/StuntRacer.FlightChecks
```

The standalone SDK assertion executable has no test-framework dependencies.
Scenarios use only public commands/inputs/observations: runway takeoff, mirrored
turns, stall/descent, actual gear-down approach/flare/braking versus hard/gear-up/
water impacts, full attitude maneuvers, fixed-step schedules and lifecycle.
