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
- **Hard Drivin' / Proving Ground** has a separate 2,230-metre spatial course:
  a traversable 44-metre-high loop, four banked corners, an 8.4-metre precision
  bridge and a real 22-metre ramp gap. Ten ordered checkpoints award clock
  bonuses; the course supports real success and timeout.
- **Stunts / Loop & Jump Park** is a separate spatial-contact session, with a
  driveable vertical loop, banked turns, a precision bridge, a ramp and a real
  sixteen-metre gap. Carry enough speed through the loop; insufficient contact
  load or steering off an edge produces a real fall. Clean loop/jump/landing
  events earn score, and clean completed circuits can record a best-run ghost.

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
**World Flight** on the first menu, then a **Departure**. Take off at Boeing
Field, fly over Seattle and Puget Sound, or take an airborne sightseeing
departure. Rainier, the San Francisco Bay, Yosemite, and Grand Canyon have
32-metre elevation cores. Seattle and San Francisco have OpenStreetMap
buildings and authored landmarks, including the Golden Gate Bridge.
Coarser terrain covers the lower 48, Alaska and Hawaii.

The **World Flight** jet can reach Mach 1.8: retract the gear with **G**, hold
**+** to set full throttle, then hold **Space** for afterburner. **Cascades /
high-altitude cruise** starts airborne and lets you accelerate immediately.
In third-person flight, press **V** or click **HUD** to hide or restore flight
telemetry; camera, pause, and touch controls still work. **C** switches to the
jet cockpit with a clear canopy, a glass HUD and working instruments.
The Amiga physics and map remain independent. See [World Flight setup and data credits](tools/WorldFlight/README.md)
to build and mount the optional terrain package.

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

In **Flight**, A/D or left/right bank; W/Up pushes the nose down, S/Down pulls
up. Hold `+`/`-` to adjust the throttle, Space/Shift for afterburner, Q/E for
rudder, B/Backspace for brakes, and G for gear. Original flight adds K for the
arrestor hook. On a controller the left stick
flies, RT/LT change throttle, right-stick X is rudder, A is afterburner, X is
airbrake, left-stick click toggles gear and LB toggles the original hook.
On touch, drag the left pad in two
dimensions (**up to climb**) and hold GAS with the right thumb; sliding up adds
afterburner. Gear and, in original mode, hook buttons appear beside recovery.
The cockpit has live attitude, navigation, throttle and fuel instruments; the
HUD adds a pitch ladder, heading, nearest landing area and gear/hook status.

**REC** records the WebGL canvas and game audio to WebM, downloaded when stopped.
The HTML HUD is not burned into the recording. It never requests microphone,
webcam or screen-capture access.

## Docker and reverse-proxy hosting

The production [`compose.yaml`](compose.yaml) pulls
`ghcr.io/long2know/poly-vector-racer:latest` and publishes port 8080 in the
container on `127.0.0.1:5188`. If the package is private, sign in with
`docker login ghcr.io` using a credential with package read access. For the
shared games host:

```sh
BASE_URL=/poly-vector-racer STUNT_PORT=5188 docker compose pull
BASE_URL=/poly-vector-racer STUNT_PORT=5188 docker compose up -d
```

Add [`deploy/nginx/poly-vector-racer.conf`](deploy/nginx/poly-vector-racer.conf)
inside the existing TLS `server` block for `games.long2know.com`, then test and
reload nginx:

```sh
sudo nginx -t && sudo nginx -s reload
```

The intended URL is `https://games.long2know.com/poly-vector-racer/`. Keep
the prefix on proxied requests (`proxy_pass` has no trailing slash) and set
nginx's upstream port to the same `STUNT_PORT` used by Compose. The nginx
sample assumes it runs on the Docker host. If nginx itself is containerized,
use a shared Docker network and the service hostname/port instead of loopback.

`BASE_URL` is a runtime mount path. The default is `/poly-vector-racer`;
`BASE_URL=` serves at root, and `/stunt-car-racer` still works when configured
for an existing deployment. `/healthz` is always at the upstream root.
`POLY_VECTOR_RACER_IMAGE` can select an immutable image digest or a
`sha-<commit>` tag for repeatable deployment or rollback.

For **World Flight**, build the optional
[terrain package](tools/WorldFlight/README.md), then mount its directory
read-only:

```sh
BASE_URL=/poly-vector-racer WORLD_FLIGHT_DIR=/absolute/path/to/world-flight/package \
  docker compose -f compose.yaml -f deploy/compose.world-flight.yaml up -d
```

For the optional original/reference modes, [`deploy/compose.reference.yaml`](deploy/compose.reference.yaml)
accepts an external `REFERENCE_TRACK_DIR` (containing `track-catalog.json`) and
`REFERENCE_FLIGHT_PACKAGE` (the extracted flight `package.json`). It mounts
both read-only. Add it as another `-f` argument if you are authorized to serve
those files. Public hosts should leave them unconfigured unless distribution
is permitted; original flight also supports local browser import.

For a **local source build** instead of the registry image:

```sh
STUNT_PORT=5189 docker compose -f compose.yaml -f compose.build.yaml up --build -d
```

The final image runs as the .NET image's non-root application user. Compose
uses a read-only root filesystem, a temporary `/tmp`, dropped capabilities and
no-new-privileges. The image does not include original disks, reference
packages, or the generated geographic data. The Container workflow publishes
from `main`; the example does not provision DNS, TLS or a live server.

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
`STUNT_BROWSER_FOCUS=experiences` exercises the visible experience buttons,
city traffic/walkers, street races, proving-ground gates, a genuinely driven
Stunts loop, the original bridge tour and city touch layouts.
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
engines. The separate authored Stunts activity does include a physical loop and
jump course; a Stunts editor and original replay-file compatibility are not
implemented. The Hard Drivin' ST source is not part of this implementation.
