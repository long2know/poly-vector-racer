# Poly Vector Racer

A low-poly browser game with stunt racing, open-city driving, and supersonic
flight, inspired by Stunt Car Racer and other classic 3D games. It uses
**C#/.NET 10 WebAssembly simulation, direct WebGL2 rendering, and an ASP.NET Core
host**. The responsive Modern model remains the default. No Unity player or
original game installation is required for the included authored content.

Choose **Foundry Heights**, **Ridgeline Run** or **Skyway Sprint** directly on the
menu's **Choose track** card, or change tracks from Controls while paused.
The included circuits use newly authored geometry. The demo has
four-wheel spring/damper suspension, banked corners, a natural ramp jump,
limited turbo, crash damage, off-track falls and recovery penalties. Chase and
cockpit cameras, synthesized engine/wind/boost/impact audio, gamepads, touch
controls, fullscreen and downloadable gameplay recordings are included.
Select **Time Attack**, **Checkpoint Run** or **Head-to-head / AI rival** on the menu. The rival uses
the same vehicle physics, contacts, jumps and recovery rules, not an animated
track-following ghost. Modes can also be changed from Controls while paused.

The front menu also has direct **CITY**, **HARD DRIVIN'**, **STUNTS** and
**FLIGHT** experience buttons. These choose different worlds/courses and
sessions, not just alternate labels for Foundry Heights:

- **City Cruise** is free driving through the newly authored **Meridian City**:
  connected streets and intersections, a downtown skyline, park/plaza,
  industrial district, waterfront and elevated bridge. Leave the guide route,
  choose any turn, drive off-road or reverse. Eighteen moving, collidable traffic
  cars and twenty-six walking sidewalk/park actors populate the city.
- **City Blitz** races through ordered street checkpoints, with a clock and
  checkpoint bonuses. **City Circuit** is a two-lap street race against a
  physical rival through the same traffic-filled city. These are
  Midtown Madness-inspired activities, not a Chicago map or imported game code.
- **Hard Drivin'** opens the existing authored **Loop & Bridge Proving Ground**:
  2,230 metres with a 44-metre loop, four banked corners, an 8.4-metre flyover,
  a 22-metre gap and ten timed checkpoints. The alternate **Switchback Test
  Track** puts a right-right-left turn sequence before its eastbound 24-metre
  channel gap, then sends you across a narrow south timing bridge. Its nine gates
  share a tighter clock: clear the loop and jump and pass every gate before time
  runs out.
- **Stunts** opens **Skyline Loop Park**: a vertical loop, banked turns, a
  precision bridge and a 16-metre gap. On **Reservoir Run**, bank left along the
  dam, jump the 18-metre spillway gap, then take the dogleg to the elevated
  return bridge. Both courses require a full loop, clean gap landing and every
  route gate. Lose tire contact or steer off an edge and the car can really fall.
  Clean completed circuits can record a best-run ghost.
- **Block Rush / Neon Yard** is a closed fictional course with original
  glowing foam cubes and no traffic or people. Follow the painted lanes with
  **W/A/S/D**, the touch pedals, or a controller. Pop cubes for points and
  steer onto a branch to reach **1,000 points in 30 seconds**. Pause freezes the
  round; **Restart** or **Garage** resets the cubes and clock.

Find the alternates under **Stunts courses** and **Hard Drivin' courses** in
**Choose track** or in **Controls → Track** while paused. Use **W** to accelerate,
**S** to brake and **A/D** to steer. Keep straight and carry roughly **125–150 km/h**
into the loop; **Space** gives optional boost. On the alternates, the HUD calls
out bends and target speeds. Brake before the turns and straighten the car
before the ramp. Restart and Garage keep the selected course; switching courses
starts a fresh session with its own score, clock and recording.

The original **Checkpoint Run** remains available on the racing circuits:
pass the lime-green gates in order for time bonuses, finish before time expires,
and pay a clock penalty for recovery. **Time Attack** runs three laps and records
a clean completed best lap for translucent purple ghost
playback on subsequent laps and restarts. It is your recorded car, not the AI.
Recovery invalidates the current recording. Ghost playback can be hidden in
Controls; changing tracks or game mode clears the session recording. Nothing is
uploaded or saved as a Stunts-compatible replay file.

Use **GARAGE** in the top toolbar (or **Garage / Choose Mode** on the pause or
finish panel) to return to the first menu **without reloading the browser**.
Clicking the game logo also returns there. This ends the current run but
preserves the session's recorded best/ghost where supported. **H / Controls**
also lets you switch modes directly.

**Vehicle Body** in the garage card or Controls selects a cage buggy, wedge GT,
four-door sedan, hatchback, high-roof delivery van or open-bed pickup. City
traffic uses distinct body geometries, not just repainted buggies. These compact
styles share the existing physical footprint/driving model; this is not a
claim of separately calibrated truck/sports-car dynamics. Enclosed styles use
a dedicated interior view, so an opaque exterior windshield cannot block the
cockpit camera.

**Flight / Interceptor Bay** is a separate jet activity in the same
C#/WASM/WebGL runtime, with runway takeoff, free flight, gear, afterburner,
fuel, stalls, landing/crash/recovery and chase/cockpit views. **Modern flight**
uses authored dynamics and scenery. Optional **Amiga Interceptor / original
flight** runs the complete original-data player Free Flight kernel in C#:
original attitude/integration, runway/terrain/scenery contacts, carrier
catapult/arrestor rules and integer readouts, with enhanced WebGL presentation.
**Air Start** skips the runway; original mode also offers **Carrier Start**.
**Landmark Tour** starts already airborne near an actual original suspension
bridge, so the Bay scenery is immediately accessible without a long flight
from the runway. Original bridges, road/ground patterns and city structures
are materialized from the disk's drawing programs. A clearly identified
enhanced service apron, moving service vehicles, walking ground crew and
ambient aircraft add activity; these are authored ambient enhancements, not
the original mission/combat AI.
Both support keyboard, controller and two-thumb touch. Neither is a real-world
aircraft-training model.

**World Flight / United States** is a separate Modern flight experience. Choose
**World Flight** on the first menu, then pick a **Departure** and **Aircraft**.
Take off at Boeing Field, fly over Seattle and Puget Sound, or take an airborne
sightseeing departure. Rainier, the San Francisco Bay, Yosemite, and Grand Canyon
have 32-metre elevation cores. Seattle and San Francisco have OpenStreetMap
buildings and authored landmarks, including the Golden Gate Bridge. The
offline package has mapped streets around Seattle only; coarser terrain covers
the lower 48, Alaska and Hawaii.

The default F/A-18 has a Mach 1.8 game speed limit. The F-14, F-15 and SR-71
Blackbird have distinct silhouettes and flight tuning. Change aircraft in the
**Garage** before launch; it stays selected when you change departures. For
the SR-71, try **Cascades / high-altitude cruise**. It starts already airborne
with gear up: set full throttle with **+** or **PWR +**, then hold **Space**
or **BURN** for afterburner. When starting from a runway, use **G** to retract
the gear after takeoff.

In World Flight, **Labels** shows names above visible landmarks. **Map Roads**
changes the appearance of mapped streets, not their coverage, and is unavailable
in regions without street data. The scenery controls work by mouse or touch
and are independent of **V**.
In third-person flight, press **V** or click **HUD** to hide or restore flight
telemetry; camera, pause, and touch controls still work. **C** switches to the
jet cockpit with a clear canopy, a glass HUD and working instruments.
The Amiga physics and map remain independent. See [World Flight setup and data credits](tools/WorldFlight/README.md)
to build and mount the optional terrain package.

**Seattle Scenic Drive** is separate from World Flight and Meridian City. It
uses the same optional World Flight ZIP, including its Seattle OpenStreetMap
roads, scenery and detailed terrain. In the **Garage**, select **Seattle** and
start the drive. Choose a stop in **Offline GPS** to show the street route on
the minimap, or enable **Autopilot** to let the game car drive and stop there.
Choose **Slow**, **Normal**, or **Fast** in **Autopilot Speed** without turning
Autopilot off. Steering, accelerating, braking or boosting returns control to you.
Stops are street approaches to Seattle Great Wheel,
Space Needle, Amazon Spheres, Lumen Field and T-Mobile Park.

Manual driving can explore dry ground within the downtown map boundary.
Offline GPS and Autopilot use only supported mapped surface streets; leave a
street to explore manually, and return to one to use guidance again. No bridges,
freeways, live traffic or routing services are used. Fictional sidewalk walkers
are decorative and never collide with the car. Pausing also pauses the walkers.
The offline map has no lane, one-way or turn-restriction rules; use it only
in the game. A ZIP without Seattle roads leaves this experience unavailable
but does not disable flight.

**Flight Training / Coppercloud Range** is a separate fictional island
activity; it needs no terrain package or original disk. Select it from
**Game Mode** and launch already airborne. Follow the initial heading toward
the moving practice drone and the ground beacon. Scan each in turn when the
range dashboard shows it is close enough. Both are non-destructive proximity
tags, and neither appears in World Flight, Interceptor Bay, or the Amiga world.
Tag both to clear the range; **Garage** or **One More Run** resets the score.

**Air Tag Duel / Amberline Range** is a separate, fictional Modern flight
activity. Launch airborne and keep level for the first pass. Follow the bright
Kite glider using its distance and direction in the scoreboard. When **SCAN**
lights up, press **T**, click or tap **SCAN**, or click the controller's right
stick. Score three separate close passes in **32 seconds**; staying close on a
single pass cannot score twice. Tags are non-destructive and only add points.
Pause freezes both aircraft and the clock. **Restart** or **Garage** resets the
duel. The range and glider use original assets and need no external package.

Optional **Classic/reference** mode now has an independently authored,
Amiga-derived three-contact integer player runtime and an actual externally
extracted LittleRamp package. It is not certified stock-Amiga or whole-reference
equivalence. The same source track can use Modern physics instead. Reference
geometry/tables stay external; the public image includes only authored content.

## Play locally

Requires .NET 10 with `wasm-tools`. Node 24 is needed for npm shortcuts and
automated checks; the browser check also needs Chrome. If the WASM workload is
missing, the optional setup script installs an isolated SDK/workload in ignored
`.tools/`, without modifying the shared SDK:

```sh
bash scripts/setup.sh
bash scripts/build.sh
BASE_URL=/stunt-car-racer bash scripts/run.sh
```

Open `http://127.0.0.1:5188/stunt-car-racer/`, then **Start your run**. Audio
unlocks on interaction. Omit `BASE_URL` to serve at `/`; `STUNT_PORT` changes
the local port. Run `npm run build` again after source changes.

| Action | Keyboard | Standard controller |
|---|---|---|
| Accelerate / brake, then reverse | W / S or up / down | RT / LT |
| Steer | A / D or left / right | Left stick or D-pad |
| Turbo | Space or Shift | A / Cross or RB |
| Recover, with time penalty | R | B / Circle |
| Change camera | C | Y / Triangle |
| Pause / resume | P | Start |
| Sound / fullscreen / controls | M / F / H | On-screen buttons |

Press a controller button to make it visible to the browser. Controls/settings
also offers **Classic joystick** for up/down-axis USB joystick adapters. Browser
qualification uses a virtual standard controller; physical controllers still
need device-specific playtesting.

**Phones and tablets do not need a controller.** Touch controls appear
automatically on a touchscreen; Controls also provides **Auto / On / Off**.
Drag the left pad to steer and hold **GAS** with the right thumb. Slide the gas
thumb upward for turbo, then slide back or release to stop turbo. Separate
brake/boost buttons and tappable recovery, camera and pause are also available.
Independent contacts allow steering and pedals together; cancellation,
orientation changes and focus loss release held controls. Portrait, landscape
and tablet layouts are supported.

In **all flight modes**, **A/D** or **Left/Right** are the ailerons: they bank
the aircraft in the air. **Q/E** are the separate rudder controls; use them to
steer while rolling on the runway. **W/Up** pushes the nose down; **S/Down**
pulls it up. Hold **=/+** or **−** to raise or lower the throttle (numpad +/−
also work). It stays set on release and through a pause or focus loss. Hold
**Space/Shift** for afterburner at high power, **B/Backspace** for brakes, and
press **G** for gear. Original flight adds **K** for the arrestor hook.

In **Modern flight**, **Assisted** is the default: bank and hold to turn, then
ease off to level the wings. Supersonic turns are much wider; release afterburner
and reduce throttle and speed before turning back. No flaps are needed. Select
**Controls → Flight Handling → Direct** for full roll authority and aerobatics.
The handling choice lasts for the browser session and does not affect Amiga flight.

On a controller, the left stick banks/pitches, right-stick X is rudder, and
**RT/LT** set throttle. **A** is afterburner, **X** is airbrake, left-stick
click toggles gear, and **LB** toggles the original hook. On touch, drag the
left pad sideways to bank or up to raise the nose. Tap or hold **PWR +/−** to
set throttle. The percentage shows the selected setting, not the current
engine response. Hold **BURN** or slide up from **PWR +** for afterburner at
high power; release to stop it. Brakes, gear and recovery are separate touch
buttons; original flight adds a hook button.

In **Flight Training** and **Air Tag Duel** only, press **T**, click or tap
**SCAN**, or click the right stick. Scanning has a short recharge; pausing
freezes the moving props and recharge and disables scans.

**C** switches between cockpit and chase; **V** toggles the chase overlay.
Use the **number row or numpad** for snap views: **4/6** left/right, **2**
aft, **7/9** forward diagonals, **1/3** rear diagonals, and **5 or 8** to
center. Cockpit snaps look through the canopy in that direction; chase snaps
move around the aircraft (aft puts the camera ahead of the nose). In chase,
drag the scene with a mouse or finger to orbit; **Center View** returns behind
the aircraft. The touch cockpit also has left, aft and right buttons. Flight
controls always act relative to the aircraft, not the camera. The pitch
ladder hides when looking away from center so it is not mistaken for a
view-aligned instrument.

**REC** records the WebGL canvas and game audio to WebM, downloaded when stopped.
The HTML HUD is not burned into the recording. It never requests microphone,
webcam or screen-capture access.

## Docker and reverse-proxy hosting

The production [`compose.yaml`](compose.yaml) pulls
`ghcr.io/long2know/poly-vector-racer:latest` and publishes port 8080 in the
container on host port **8913**. If the package is private, sign in with
`docker login ghcr.io` using a credential with package read access. For the
shared games host:

```sh
docker compose pull
docker compose up -d
```

Add [`deploy/nginx/poly-vector-racer.conf`](deploy/nginx/poly-vector-racer.conf)
inside the existing TLS `server` block for `games.long2know.com`, then test and
reload nginx:

```sh
sudo nginx -t && sudo nginx -s reload
```

The intended URL is `https://games.long2know.com/poly-vector-racer/`. Keep
the prefix on proxied requests (`proxy_pass` has no trailing slash) and set
nginx's upstream port to the same `STUNT_PORT` used by Compose. The published
port is not restricted to loopback; restrict access separately if it should
only be reachable through nginx. The sample uses `127.0.0.1:8913` for nginx on
the Docker host. For nginx on another machine, use the Docker host's address;
for nginx on a shared Docker network, use `poly-vector-racer:8080`.

`BASE_URL` is a runtime mount path. The default is `/poly-vector-racer`;
`BASE_URL=` serves at root, and `/stunt-car-racer` still works when configured
for an existing deployment. `/healthz` is always at the upstream root.
`POLY_VECTOR_RACER_IMAGE` can select an immutable image digest or a
`sha-<commit>` tag for repeatable deployment or rollback.

No volume is needed for the base game. `STUNT_WORLD_ATLAS` is the only
geographic scenery setting. Point it at an absolute directory outside the web
root. Put either or both datasets there:

- **U.S. World Flight and Seattle Scenic Drive:** `seattle.zip` (exact lowercase
  name). An existing extracted package can instead go at
  `seattle/manifest.json`, with its sibling data files. A present ZIP takes
  precedence; an invalid ZIP fails startup rather than using the extracted copy.
- **World Flight Atlas:** `base.zip` for worldwide terrain, with any compatible
  lowercase area ZIPs alongside it. `seattle.zip` does not change the Atlas
  revision, and Seattle alone does not need `base.zip`.

**Migration:** Stop passing `STUNT_WORLD_FLIGHT`. Copy or rename your existing
[`world-flight.zip`](tools/WorldFlight/README.md#package-as-a-portable-zip) to
`seattle.zip` in the new directory; don't unpack or rebuild it. The manifest,
asset bytes and `/world-flight/` URLs are unchanged. Move an extracted package
under `seattle/` instead. Use the scenery-directory overlay below in place of
either old World Flight Compose override.

```sh
BASE_URL=/poly-vector-racer STUNT_WORLD_ATLAS=/absolute/path/scenery bash scripts/run.sh
```

For Docker, bind-mount the directory read-only using
[`deploy/compose.world-atlas.yaml`](deploy/compose.world-atlas.yaml). It must be
readable by the container's UID/GID `1000:1000`. The host serves individual
entries; the browser never downloads the whole ZIP. Restart the host after
changing the pack set.

```sh
WORLD_ATLAS_DIR=/absolute/path/scenery \
  docker compose -f compose.yaml -f deploy/compose.world-atlas.yaml up -d
```

For the optional original/reference modes, [`deploy/compose.reference.yaml`](deploy/compose.reference.yaml)
uses `tracks/track-catalog.json` and `flight-package.json` under
`${DOCKER_DATA}/games/poly-vector-racer` by default. Override those locations with
`REFERENCE_TRACK_DIR` and `REFERENCE_FLIGHT_PACKAGE` if needed. The overrides
mount data under `/app/data`; UID/GID `1000:1000` must be able to read the files.
Only mount and serve original/reference files when distribution is
permitted. Original flight also supports local browser import.

For a **local source build** instead of the registry image:

```sh
STUNT_PORT=8914 docker compose -f compose.yaml -f compose.build.yaml up --build -d
```

Compose runs the process directly as `1000:1000`; `PUID`/`PGID` variables do not
configure this image. There is no `tmpfs` mount. The image does not include
original disks, reference packages, or the generated geographic data. The
Container workflow publishes from `main`; the example does not provision DNS,
TLS or a live server.

After building, `STUNT_CONTAINER_IMAGE=poly-vector-racer:local npm run test:hosting`
also checks that the non-root image can read every published file and serve its
actual SDK-generated boot configuration under the mounted path.

## Runtime boundaries

- `src/Engine`: reusable fixed-step clock with an explicit bounded-stall policy.
- `src/StuntRacer.Core`: canonical track surface, vehicle dynamics and race
  state, city-world contacts/traffic/races and a separate spatial Stunts session.
  The browser does not implement substitute driving physics. City rendering and
  collisions consume the same canonical roads and building footprints.
- `src/StuntRacer.Browser`: C# exports, small WebGL/math/input/audio/capture
  helpers, authored scene and presentation. At most two GPU submissions are
  pending; rendering never blocks on a GPU fence or queues unlimited stale frames.
- `src/StuntRacer.Host`: static bundle delivery and runtime path mounting.

Modern driving and flight physics step at 120 Hz, separately from interpolated
display-rate rendering. Classic driving uses its identified reference clock
(50 callbacks / 6 = 0.12 seconds per step); original flight uses eight fixed
steps per second. These are explicit runtime profiles, not a claim about every
original PAL/NTSC executable.
Spatial stunt courses use full 3D contacts, not the racing heightfield sampler.
City, flight and spatial-course cameras opt into logarithmic depth; ordinary
racing retains its existing early-depth/shadow path. Ground/runway layers must
not bleed through one another as the camera climbs.
The visual road uses the same samples and triangle diagonal as collision.
Hidden/unfocused gameplay pauses and clears controls; controller input must
return to neutral after focus loss. Unsupported WebGL, startup failure and
context loss are visible errors, not another renderer or fake-ready fallback.

This is the seed of a reusable WASM/WebGL runtime, **not an engine-first
framework project**. Keep the clock and browser platform helpers reusable;
stabilize a broader engine API only when another game demonstrates the need.

**Performance:** Normally sandboxed hardware-enabled Chrome on the available
RTX 3070 measured 60 GPU-completed/submitted frames/s while driving at 1440x857.
That browser run has a 60 Hz callback cadence; 120 FPS display delivery is not
certified. F3 toggles GPU-completed
FPS/frame intervals, callback/submission rates, C#/draw CPU costs, available GPU
timings, pending/skipped submissions, actual resolution, quality and renderer.
These are not physical display-FPS guarantees. High retains soft shadows;
Balanced uses cheaper hard-edged shadows and lower resolution limits.
Headless/software-GPU results must not be presented as hardware-accelerated
performance.

## Reference tracks and optional Classic physics

The supplied ptitSeb/fluffyfreak numeric declarations and real 804-byte layout
are read by a non-executing extractor:
[`tools/AmigaReferenceExtract/README.md`](tools/AmigaReferenceExtract/README.md).
The local LittleRamp package contains 44 source sections, 398 geometric segments,
124 populated height profiles and external/generated reference numeric lookups.
No upstream source bodies or original data arrays are embedded in our code.

For a selectable collection of external packages, configure a version-1 catalog:

```sh
BASE_URL=/stunt-car-racer \
STUNT_TRACK_CATALOG="$PWD/artifacts/amiga-source/runtime/track-catalog.json" \
bash scripts/run.sh
```

The local research catalog contains seven supported original reference tracks
and seven TNT tracks, shown alongside the three included circuits. Draw Bridge
and Jerkily Jump are disabled with explicit reasons rather than substituted
roads. All fourteen packages passed strict reader, geometry and spawn checks;
this is not all-track playtesting or original-game certification. The ignored
`track-catalog-receipt.json` records source pins, hashes and qualification.
The separate Interceptor Bay flight activity is also selectable; it is not
counted as an additional racing circuit.

Catalog entries contain `id`, `name`, `group`, `status`, `packagePath` and
`reason`; the root has `schemaVersion: 1` and `tracks`. Available package paths
must be relative files beneath the manifest directory, outside static assets,
without symlinks. Unsupported entries require a reason and a null package path.
Only configured IDs are served; clients cannot request arbitrary host files.

Alternatively, retain the single-package configuration:

```sh
BASE_URL=/stunt-car-racer \
STUNT_AMIGA_PACKAGE="$PWD/artifacts/amiga-source/runtime/LittleRamp.source-package.json" \
bash scripts/run.sh
```

The menu's **Choose track** selector lists configured reference/TNT content.
In **H / Controls**, **Load reference track** loads the configured default with
Modern physics. Choose **Classic / Amiga-derived reference** to compare the
three-contact fixed-point candidate on the same road. This never auto-replaces
the liked Modern default. `?physics=classic` is an explicit startup opt-in.
Local source-package JSON can also be selected with **Load local track package**.
Classic is initially time-attack only; AI rival racing, checkpoint runs and
best-lap ghosts use Modern dynamics.

`DrawBridge` requires unsupported dynamic geometry. `JerkilyJump` is unsupported
with the current shared profile data (section 22 needs twelve samples; the
referenced profile has nine). Neither a frozen bridge nor a padded/partial road
is emitted.

For containers, bind-mount the catalog directory read-only outside
`/app/wwwroot` and set `STUNT_TRACK_CATALOG` to its absolute container manifest
path. For a single package, bind that file and use `STUNT_AMIGA_PACKAGE` instead;
do not configure both variables. Packages are never copied into the image.
Without them, Modern and the three included circuits remain available. Package
URLs are mount-relative and `no-store`. The host's content root is the deployed
assembly directory, so startup does not recursively watch a large checkout.

The candidate uses source-derived control/integration/contact behavior, with
three authoritative contacts and rear-only wheel duplication for rendering.
The road adapter uses integer barycentric triangles from the exact source
geometry; it is not the stock ASM or reference curved/bilinear road sampler.
Reference-generated Q14 trig is not proof of stock table-byte identity.
Reviewed bounded source-component comparisons are not full-player, Windows-long32
or original-executable equivalence. Detailed scope and actual runtime checks:
[`tests/StuntRacer.SourceRuntimeChecks/README.md`](tests/StuntRacer.SourceRuntimeChecks/README.md).

## Strict geometry import and trace tools

The squad added a strict 804-byte layout reader, an externally supplied decoded
common-template/profile format, exact source-edge conversion, integer/angle
helpers and a tick-indexed trace verifier. See
[`tools/AmigaTrackImport/README.md`](tools/AmigaTrackImport/README.md) for the actual
`inspect`, `convert` and `verify-trace` commands and required provenance files.
The generic converted-geometry format remains separate from the runnable
source-runtime package. No native ADF packed-data extractor or stock integer
road-height sampler is supplied. An 804-byte layout alone is insufficient.

To try a converted local `.track.json`, open **H / Controls**, then **Load local
track package**. The C# importer and renderer retain the exact edge vertices,
including skewed/banked strips; they do not rebuild them from centre points.
The browser explicitly labels the run **PROTOTYPE PHYSICS**. Unsupported dynamics
claims, gaps or malformed geometry reject without replacing the current track.
**Restore authored circuit** returns to the included demo. Local files are read
in the browser, not uploaded, cached as server data or added to the image.

Stock-original certification still requires identified executable/version and
table evidence, independent complete raw-state traces and original timing/road
sampler verification. Import or synthetic trace success is not Amiga parity or a
redistribution grant. Source compatibility is labelled separately and runnable;
these certification gaps do not turn Modern or the reference candidate into a
blocked application.

Original inputs, decoded data, converted geometry and oracle recordings must
stay outside source/static assets and Docker layers. Tool outputs are quarantined
under ignored `artifacts/`, but **git-ignore is not filesystem privacy**: this
development mount reports `0777`, so owner-only permissions are unqualified.
Use appropriate host storage/ACLs for confidential inputs.

## Original Amiga flight

Choose **Flight / Amiga original model**, then use **Controls / Load original
flight ADF / ZIP** to select a compatible local disk. Parsing and extraction run
in C#/WASM in the browser; the disk is not uploaded. An invalid or unsupported
input reports an error without replacing the current session. Modern flight
and all included racing content remain available without a disk.

The supplied modified/cracked disk was identified by its actual game
executable, `x1`, SHA-256
`38b6e1f331460aa4939690173d95c38b105d1d089226359a4c8655052ea29ab5`.
Its separate crack intro is not used as the game. The bounded ZIP/OFS/HUNK
reader validates the executable layout, relocations, images, tables and
contact/scene data. Only the verified variant is currently accepted; retail
or other modified disks are not silently treated as equivalent.

For opt-in hosted data, extract a package outside static assets:

```sh
bash scripts/dotnet.sh run --project tools/InterceptorReferenceExtract -c Release -- \
  extract --input '/path/to/your-original-disk.zip' \
  --output artifacts/flight-source/ingestion/local

BASE_URL=/stunt-car-racer \
STUNT_FLIGHT_PACKAGE="$PWD/artifacts/flight-source/ingestion/local/package.json" \
bash scripts/run.sh
```

`STUNT_FLIGHT_PACKAGE` can be used alongside the racing track catalog. In Docker,
bind-mount the package read-only outside `/app/wwwroot` and set that variable to
its container path. The mount-relative `/flight-package.json` endpoint is
opt-in and `no-store`; original disks, tables and models are not copied into
the public bundle or image.

The C# port now includes the ordered player attitude, propulsion, motion,
terrain/pad, gear/hook, landing/crash, carrier and landmark-contact stages.
The playable uses actual extracted coastlines, landing-pad quads and
materialized static source geometry, including bridge-strip generation,
relative vertices, linework and ground programs, with authored aircraft/cockpit,
lighting, water and runway paint. It does not substitute Modern flight or an
authored rectangular bay for original dynamics/terrain.

**Scope:** player aircraft Free Flight, not the complete historical game.
Combat, AI aircraft, missions, ejection/seat simulation, original screens and
audio are not ported. The source world contains 499 meshes and 7,883 faces across
all 96 placed static model types. Original camera-sized sprites, animated
effects and painter/bitplane presentation are not pixel-identical. Authored
ambient activity is visual enhancement, not additional original collision/AI
certification. Scenery contact selection/refresh follows an
identified enhanced-renderer profile, not certified original display-list
order/stale-cache behavior. The user disk is modified; no blanket retail,
whole-machine or redistribution claim is made.

### Source and numerical qualification

The supplied `Grumbel/fa18-interceptor-port` was fetched at
`73a1f3ef297c51c4648bcad9340f46ddea4e8f24`. It statically recompiles/interprets
the original 68000 program and still needs an external original disk. A second,
more directly useful source was found and fetched:
`st-gr/interceptor-port` at `2475dcd65f454a37b41a2be27629ad958f34ddc5`.
It contains a native clean-room C99 player model and detailed recovered rules,
including `docs/re/05-flight-model.md` and `src/core/flight.c`.
Both checkouts remain under ignored `artifacts/flight-source/reference/`;
no upstream implementation bodies or original data arrays are embedded in
tracked code.

The reference investigation identifies 16-bit wrapping state, 28,800 angle
units per revolution, table-driven control response, speed/gust/sink rules and
ground/deck contact. Its documented reference pace is eight steps per second,
not the Modern flight model's 120 Hz. The original does not use the Modern
prototype's aerodynamic lift/drag implementation. Exact reference stepping
also needs externally extracted tables and relocated image spans; generating
trigonometry or inventing replacement tables is not original-data parity.

The user's local ZIP supplied the original data; no replacement ADF was
downloaded. All original bytes and independent oracle checkouts remain under
ignored external artifacts, never source/static assets or Docker layers.

The complete player differential passed **2,630 actual-data cases and 347,160
scalar comparisons with zero differences**, including retained-state takeoff,
landing, stall/crash, carrier catapult/arrestor, brakes, water and gear
trajectories. A separate Musashi run corroborated the pinned native rules
against the supplied executable's actual 68000 routines, with documented
runtime-memory exclusions. These results do not certify every original game
subsystem. Commands, hashes, boundaries and independent/mutation evidence:
[`tests/StuntRacer.FlightReferenceChecks/ORIGINAL-PLAYER-EVIDENCE.md`](tests/StuntRacer.FlightReferenceChecks/ORIGINAL-PLAYER-EVIDENCE.md).
Earlier invented-table component evidence remains separately recorded in
[`EVIDENCE.md`](tests/StuntRacer.FlightReferenceChecks/EVIDENCE.md).

## Checks and demo evidence

After building:

```sh
npm test
npm run test:simulation
npm run test:city
npm run test:stunts
npm run test:flight
npm run test:flight-reference
npm run test:compatibility
npm run test:hosting
STUNT_URL=http://127.0.0.1:5188/stunt-car-racer/ npm run test:browser
```

The browser command first generates a synthetic compatibility fixture, then
launches its own normally sandboxed Chrome profile, uses
actual keyboard/mouse actions and a clearly labelled virtual gamepad, and drives
the real WASM car over the ramp. It also checks pause/recovery, live audio
signal, actual WebM download, cockpit/mobile layout, fullscreen, local synthetic
geometry import/rejection/restoration, real rival movement/pause, selectable
canonical tracks, ordered checkpoint completion, a genuinely driven clean lap
and rendered ghost, multi-touch steering/gas/slide-up turbo, touch settings and
phone/tablet layouts, and visible context-loss handling. With a configured external package it also drives real
LittleRamp data, switches Modern/Classic on the same track and checks source
steering/pause/boost and restoration.
`STUNT_REQUIRE_CATALOG=1` additionally requires the full local catalog
(seventeen racing circuits, separate Modern/original flight choices, two
explicitly unsupported circuits).
`STUNT_BROWSER_FOCUS=flight` runs the focused real-WASM flight input/camera/mobile
checks; the default run includes them after the racing checks.
`STUNT_BROWSER_FOCUS=flight-training` checks the native range through keyboard
and phone input; `STUNT_BROWSER_FOCUS=world-flight-settings` checks the aircraft
selector, landmark projection and road styling with the hosted terrain ZIP.
`STUNT_BROWSER_FOCUS=arcade-activities` checks real WASM Air Tag Duel and
Block Rush in Chrome, with keyboard, touch, and a virtual gamepad. It needs no
external content.
`STUNT_BROWSER_FOCUS=world-flight-speed` runs the focused SR-71 Mach 3 check
at the Cascades departure without preceding mobile or sightseeing scenes.
`STUNT_BROWSER_FOCUS=flight-turns` uses the hosted Atlas to check that keyboard,
touch and gamepad reverse the actual flight path and level off in Assisted,
and that Direct still allows a full roll.
`STUNT_BROWSER_FOCUS=seattle-drive` checks the real ZIP's street map, scenery,
sidewalk activity, GPS arrival, pause, replanning and keyboard takeover in Chrome. The related
`seattle-drive-legacy` and `seattle-drive-no-package` focuses check unavailable
states. To run the deterministic native arrival and takeover checks:

```bash
bash scripts/dotnet.sh run --project tests/StuntRacer.SeattleDriveChecks -c Release -- /absolute/path/scenery/seattle.zip
```

`STUNT_BROWSER_FOCUS=experiences` exercises the visible experience buttons,
city traffic/walkers, street races, proving-ground gates, a genuinely driven
Stunts loop, the original bridge tour and city touch layouts.
`STUNT_BROWSER_FOCUS=stunt-courses node tests/browser/playable.mjs` selects the
two alternate courses in Chrome, drives their supported loops with keyboard
input, and checks scoring, the timed gates, pause, Restart and Garage.
`npm run test:stunts` drives both complete alternate circuits with native
public inputs, including their open gaps, clean landings and all ordered gates.
`STUNT_BROWSER_FOCUS=original-flight STUNT_REQUIRE_ORIGINAL_FLIGHT=1` requires
the original runway/carrier/touch flow. Configure the external package or set
`STUNT_ORIGINAL_ZIP` to a local disk ZIP to also exercise real browser ingestion
and damaged-archive rejection.
Set `CHROME_BIN` for another Chrome executable.

The original-data player oracle is opt-in and remains outside the image:

```sh
npm run test:flight-reference -- \
  --player-oracle "$PWD/artifacts/flight-source/oracle/flight_player_oracle" \
  --original-data "$PWD/artifacts/flight-source/ingestion/local"
npm run test:flight-data -- --source '/path/to/your-original-disk.zip'
npm run test:flight-original -- \
  --package "$PWD/artifacts/flight-source/ingestion/local/package.json"
```

Its build command is in the numerical receipt above. The preserved component
oracle uses `--oracle .../flight_reference_oracle`. Without an oracle, the
suite explicitly reports that differential evidence was not run; a requested
missing/mismatched oracle fails rather than falling back.

The external-data native suite is opt-in:

```sh
bash scripts/dotnet.sh run --project tests/StuntRacer.SourceRuntimeChecks -c Release -- \
  --package artifacts/amiga-source/runtime/LittleRamp.source-package.json \
  --component-oracle artifacts/amiga-source/oracle/receipt.json
```

Screenshots, gameplay recordings and machine-readable results are retained in
ignored `artifacts/browser/run-*/`. The profile and test servers are cleaned up.
Software-GPU screenshots establish appearance/function, not a physical-device
frame-rate claim.

On a host with an available GPU/display, `STUNT_ENABLE_GPU=1` opts the browser
check into Chrome's normal hardware-GPU autodetection. The evidence still records
the renderer actually selected; this does not disable the browser sandbox or
guarantee that hardware acceleration is available.

## References and provenance

The team examined `fluffyfreak/stuntcarracer`, `retro-foundry/multistuntcar`,
`ptitSeb/stuntcarremake` and `Vesuri/stuntcarracer`, alongside
`long2know/nflblitz` and `long2know/mahjong-web`. Exact research pins and limits
are recorded in `.squad/decisions.md`. The related C++ remakes are not
independent corroboration of physics accuracy. Their code, track data and
audio are not automatically cleared for reuse merely because they are public.
The supplied Midtown Madness gameplay reference was inspected for its connected
street network, visible traffic/pedestrians, checkpoint guidance and bridge
driving. Meridian City uses newly authored geometry and behavior.
`malespiaut/hard-drivin-st` identifies itself as the **Atari ST port**, not an
arcade-engine equivalence baseline. `a2stuff/fs2` is an **Apple II FS2 symbolic
disassembly**, not simply a portable modern-map source release. They are
research references; their code, original scenery databases and assets are not
included or automatically cleared for redistribution.

Original Stunt Car Racer by Geoff Crammond. The included Foundry circuit,
implementation, vehicle/environment art and synthesized sounds are newly
authored. Reference layouts/tables, when configured, retain their separate
provenance and distribution limits. Checkpoint trials and best-lap ghosts are
independently authored inspired features, not imported Hard Drivin' or Stunts
engines. All four Stunts and Hard Drivin' courses have authored physical loops
and jumps. A Stunts editor and original replay-file compatibility are not
implemented. The Hard Drivin' ST source is not part of this implementation.
