# Squad Decisions

## Active Decisions

No decisions recorded yet.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

## Approved Commissioning — 2026-11-17

### Authority and installation boundary

- **Requested by:** Stephen Long. **Recorded:** 2026-11-17T15:06:00-08:00.
- Completed Phase 1 presented all nine exact roster lines; the user confirmed **"Yes, hire this team"**. Phase 2 installs that approved descriptive team only. The scaffold's earlier empty-ledger marker predates this entry.
- Specialists: **Lead — Architecture & Source Lead; Physics — Vehicle Physics Engineer; Graphics — WebGL2 & Technical Art Engineer; Gameplay — Gameplay & Track Engineer; Tester — QA & Browser Performance Engineer**.
- Fixed supports: **Scribe — Durable Decision Merger; Ralph — Work Monitor; Rai — RAI Reviewer; Fact Checker — Verification & Devil’s Advocate**. Preserve installed support charters/policies, including capitalized `agents/Rai/`. Supports are roster members, not specialist registry entries or domain-routing destinations. @copilot auto-assignment stays disabled.
- This is metadata commissioning, not implementation: no application/manifests/backend, imported original binaries/code/assets, physics spike, parity verdict or benchmark result. No development campaign, external messaging, dependency installation/upgrade, branches/commits/pushes/PRs/issues is authorized by this step. Initial assignments below are recorded future work.

### Approved product, architecture and ownership boundaries

- Build a faithful browser-native remake of the Amiga Stunt Car Racer with authentic driving feel, attractive accelerated visuals and smooth frame pacing; not a generic web app or a box-car demo relabeled as the target.
- Use **direct browser WebGL2**, with **fixed-step simulation independent of interpolated display-rate rendering**. Lead decides **TypeScript versus WASM physics after a small behavioral-fidelity/maintenance spike**, not by automatically inheriting all of NFL Blitz's .NET stack. Exact simulation rates/legacy conversions remain to be established, not copied from a reference's defaults.
- **Lead** owns architecture, pinned source/provenance matrix, coordinates/units/clocks and contracts, the TS/WASM spike decision and integration.
- **Gameplay** owns the **canonical track definition and shared sampled road surface**. **Physics consumes it; Graphics renders it**. No separate visual/collision road interpretations. Lead resolves units/axes/clock contracts; neither rendering nor rAF owns simulation state.
- **Physics** owns suspension/contact/banking/jumps, acceleration/braking/boost, airborne/landing/impacts/damage and physical recovery, plus deterministic reference scenarios and input traces. Preserve authentic feel; do not simply accelerate legacy ticks.
- **Graphics** owns recognizable car/cockpit, visible suspension, elevated-road silhouette, appropriate materials/lighting/atmosphere, instancing and GPU/DPR/resize/context lifecycle.
- **Gameplay** also owns input/gamepad/camera, HUD/audio, race flow and recovery interaction; Physics owns recovery dynamics, Graphics cockpit rendering. Rival AI/progression/additional tracks remain overall future scope.
- **Tester** owns reproducible fidelity scenarios, matching outcomes under varied render schedules, screenshot-based player-visible visual acceptance, representative browser/hardware/resolution profiles and objective CPU/GPU/frame-time acceptance. QA checks the actual contract, not rAF counts.

### First intended milestone and evidence gates

- One **polished drivable elevated track with ramp and banking**, responsive cockpit/camera and visible suspension, acceleration/braking/boost, airborne/landing/crash/recovery, HUD and audio. Placeholder graybox alone is **not final visual acceptance**.
- Rival AI, fuller original progression and more tracks remain overall project scope; do not promise every one in the first slice. No unrequested accounts/backend/online multiplayer.
- **Stable 60 FPS baseline on agreed representative hardware**, with **120/144 Hz headroom where capable**, are **goals, not measurements or guarantees**. Hardware/browser/resolution profiles and detailed timing budgets are still to be agreed.
- Require reproducible scenarios/input traces and outcome checks under varied render schedules, screenshot-based acceptance of the actual visual target, and real CPU/GPU timing/frame-time/pacing evidence. Label physical-GPU results separately from software-GPU/headless screenshots; screenshots alone prove neither acceleration nor pacing. Do not equate render-frequency independence with established cross-runtime determinism or original-game parity.

### Pinned reference handoff — completed Phase 1 observations

These findings and pins were supplied by the completed Phase 1 handoff; Phase 2 does not repeat remote research or claim new verification. They establish reference context, not incorporation, reuse clearance or proven original-game parity.

| Reference and exact pin | Supplied inspected evidence | Limits / intended use |
|---|---|---|
| `fluffyfreak/stuntcarracer` @ `306be6764b4ecee80c0758a0fd9f664c66bbb0d0` | Partial C/C++ Windows DirectX/Direct3D Amiga conversion; README notes original Amiga tracks, samples and algorithms. Actual `Car Behaviour.cpp` lines 1–145 show fixed-point/global-state lineage, differing PC/Amiga coordinate scales, direction-dependent road logic and an explicitly failed higher-framerate experiment. `Track.cpp` lines 1–120 document road pieces/orientations and separate left/right height encodings. | Behavioral/track reference, **not** a transplantable modern renderer. Do not treat faster legacy ticks as a fidelity fix. |
| `retro-foundry/multistuntcar` @ `a15e583416429bd6a8cd62ee4c7b038deebb4b0c` | Remake descendant using SDL/OpenGL, Emscripten web build and modern refresh-rate work. Inspected `src/PhysicsConfig.h`: `PHYSICS_UPDATE_HZ=60`, `PHYSICS_REFERENCE_STEP_SECONDS=0.14`; `src/StuntCarRacer.cpp`: separate logic/physics accumulators and render interpolation. | Implementation evidence, **not established Amiga parity** or required project rates. WebRTC streams a host canvas; it is not evidence of deterministic network simulation. |
| `Vesuri/stuntcarracer` @ `3cce78d193c3d0208be34d0bbfc3155e787cd406` | README describes manually analyzed ReSource Amiga assembly disassembly `StuntCarRacer.s`, marked modifications and claimed 50/60 FPS unlock without speed/physics change. | Independent assembly cross-check candidate, **not a C decompilation or proof its parity claims are true**. |
| `ptitSeb/stuntcarremake` @ `c56327db5685c2ae63ea3c73d64ef100183b9359` | Linux/SDL/OpenGL and Emscripten/gl4es port. | Same remake lineage as fluffyfreak/multistuntcar, **not an independent physics source**. |
| Public `long2know/nflblitz` @ `5cee0091be688813c67d277dbc310191d1bd8fe5` | Inspected README, `tools/source-player/README.md` and actual `tools/source-player/wwwroot/{renderer.js,main.js}`. Browser runtime is C#/.NET 10 browser-WASM simulation plus **direct WebGL2 JS rendering**. Renderer consumes simulation packets with verified geometry/textures and source material/projection/shadow/fog/HUD semantics; scheduling uses measured active elapsed and rAF rather than fabricated render-owned game state. | Unity player is not required; retained Unity is a separate path. Overall original-game parity remains explicitly incomplete. Reuse fidelity discipline/architecture, **not NFL assets or false completion claims**. |
| Public `long2know/mahjong-web` @ `0cef8c9aa65cb026011d95f37b3caec296308cac` | Inspected README, `docs/integration/renderer-v1.md` and actual `src/frontend/autotable-src/src/renderer/{webgl-scene.ts,webgl-host.ts}`. TS/custom WebGL2 GLSL/instanced geometry with small Three math primitives; data-only `RendererLayout`/`RendererFrame` snapshots; one GPU frame in flight, real fence completion, latest-frame coalescing and rAF scheduling; DPR/viewport/focus/visibility lifecycle, explicit context-loss/unsupported errors and teardown. | **Not an engine/WASM player**. Adopt relevant rendering/lifecycle discipline; no need to inherit Mahjong's backend for the racing slice. |

### Code, data and asset provenance gate

No blanket code/data/art/audio reuse permissions are established. License metadata `null` is **not reuse clearance**. Track separate provenance and inspect rights/notices before incorporation or distribution, including track data and audio; do not assert speculative current copyright owners as fact. No original binaries, code or game assets have been imported. Authored replacement assets are an option.

### Initial assignment handoff — future work only

1. **Lead:** Source/provenance matrix, coordinates/units/clocks and contracts, small TS/WASM behavioral-fidelity/maintenance spike decision, integration.
2. **Physics:** Suspension/contact/banking/jumps/boost/impacts/damage/recovery behavior; deterministic scenarios and input traces without simply accelerating legacy ticks.
3. **Graphics:** Car/cockpit and suspension fidelity, elevated-road silhouette, lighting/materials/atmosphere, instancing/resource lifecycle and DPR/resize/context handling.
4. **Gameplay:** Canonical track/shared sampled road, input/gamepad/camera/HUD/audio/race flow/recovery interaction; later rivals/progression/additional tracks.
5. **Tester:** Repeatable fidelity/outcome checks under varied render schedules, screenshot-based acceptance, agreed browser/hardware/resolution profiles and real CPU/GPU/frame-pacing evidence.

**Stop condition for this commissioning request:** Persistent validated team metadata and kickoff context. Game implementation requires a subsequent request.
