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

#### Atlas autopilot and time compression

Only Atlas publishes `FlightSnapshot.AtlasAutopilot`. Use
`ConfigureAtlasAutopilot(AtlasAutopilotCommand)` to `engage` a `route` toward
`destination: {lonDeg, latDeg}` or `hold` a compass `headingDeg` (0–360, omitted
to hold the current course). Both accept `altitudeM` (1500–12000), `speedMps`
(180–300) and `rate` (1, 2, 5 or 10). `setRate` changes the desired rate without
changing the flight plan; `takeControl` disengages it. The defaults are
10,500 m, 250 m/s and 1× (or the rate previously selected for this flight).

Route targets are the nearest fixed unwrapped X copy in the Atlas projection;
exact antipodes tie west. Compare X to the wrapped aircraft position using the
short delta, not raw subtraction. Capture occurs within 5 km, at 1×, and
continues airborne on the captured course without autoland. Polar routes
approach the boundary obliquely; outward heading holds near either pole follow
a tangent. The native floor is fixed at 10,500 m (500 m over the maximum valid
WFA1 or WFT1 height); lower altitude commands show the clamp in `reason`.
The climb begins from the actual pose and examines nearby active terrain;
engagement and optional detail loading do not raise the aircraft.

Call `AdvanceAtlas(seconds, input, manualIntent)` once per wall frame. The
pilot drives the existing roll, pitch and dry throttle controls at 120 Hz.
Compression starts in a stable climb or cruise. Before cruise, a clean jet
must be on course, nearly wings-level, climbing at 4–60 m/s and within the
safe speed band. Below the global clearance height the pilot requires 850 m
over active terrain, using the existing 30-second lookahead and a conservative
bound on nearby cells to account for narrow streamed ridges. Turns, unsafe
terrain/attitude, gear down, low fuel, approach and arrival stay at 1×.

Compressed frames advance at most 60 fixed ticks instead of the usual 30.
`requestedRate` is the selected multiplier; `effectiveRate` reflects the
applied rate after the frame budget, and may be fractional. The cap drops
excess time rather than queuing it. Pause, takeover or loss of eligibility
drop any pending accelerated remainder. Roll, pitch, yaw, airbrake,
afterburner, gear, or explicit manual intent cancel the pilot before manual
physics; throttle alone does not.

The pilot consumes 1% of the normal dry fuel rate as a game endurance setting.
Manual and afterburner burn remain unchanged, and engagement never refuels.
`projectedFuel` is an estimate on active routes: expected level dry thrust
plus an allowance for initial climb and turning, not a guaranteed reserve.
`Aircraft.Fuel` remains the authoritative tank level.

`playEtaSeconds` estimates real play time to the 5 km capture, not simulated
flight time. It uses projected distance and current horizontal closing speed
(capped at the commanded speed), at `effectiveRate` until the 25 km approach
and at 1× thereafter. It includes a stable, forward 1× approach but omits
unstable acquisition and turns, unsafe terrain/attitude, low fuel or a low
projected reserve, polar captures, and wall frames that discarded elapsed time
at the outer frame limit. Off, hold, paused, and arrived states have no ETA.
The estimate assumes the current pace and rate; it is not a guaranteed time.

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
drag, wheel braking, fuel and high-throttle afterburner. There is no canned
path or altitude lock. Native `FlightInput` defaults to direct roll-rate control.
Set `Assisted = true` to command a bank angle and level the wings on release.
Lift and yaw trim work within the available aerodynamic load and follow the
velocity path; they do not prevent low-energy stalls. Runway rudder is unchanged.

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
Atlas autopilot checks cover command validation, early climb compression,
fixed-step equivalence, takeover, fuel, streamed terrain bounds, capture and
polar/date-line navigation. The play-time estimate checks include a frame-budget
cap and the 1× approach; two scenic departures fly to capture at 1× and 10×
requested rates. To verify their actual low-altitude origins and captures with
the shipped coarse fallback:

```sh
dotnet run -c Release --project tests/StuntRacer.FlightChecks -- \
  --atlas-scenic artifacts/world-atlas/base.zip
```

The opt-in endurance check verifies early compression on the JFK and Haneda
voyage starts and flies NYC–London in both directions, Tokyo–San
Francisco across the date line, and two eastbound equatorial laps, first using
hold and route waypoints and then heading hold alone on the same tank. Supply
the bundled `base.zip` or an explicit extracted WFA1 `base.bin`:

```sh
dotnet run -c Release --project tests/StuntRacer.FlightChecks -- \
  --atlas-endurance artifacts/world-atlas/base.zip
```
