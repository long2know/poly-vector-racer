# Squad Team

> Browser-native Stunt Car Racer remake — approved descriptive team.

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status | Scope |
|------|------|---------|--------|-------|
| Lead | Architecture & Source Lead | [.squad/agents/lead/charter.md](agents/lead/charter.md) | 🏗️ Active | Architecture, provenance, units/contracts and integration. |
| Physics | Vehicle Physics Engineer | [.squad/agents/physics/charter.md](agents/physics/charter.md) | 🔧 Active | Suspension, banking, jumps, boost, damage and recovery. |
| Graphics | WebGL2 & Technical Art Engineer | [.squad/agents/graphics/charter.md](agents/graphics/charter.md) | ⚛️ Active | Car/track fidelity, lighting, materials and GPU lifecycle. |
| Gameplay | Gameplay & Track Engineer | [.squad/agents/gameplay/charter.md](agents/gameplay/charter.md) | 🎮 Active | Shared track surface, input/camera, HUD/audio, race flow and rival AI. |
| Tester | QA & Browser Performance Engineer | [.squad/agents/tester/charter.md](agents/tester/charter.md) | 🧪 Active | Fidelity regressions, repeatability, compatibility and CPU/GPU frame pacing. |
| Scribe | Durable Decision Merger | [.squad/agents/scribe/charter.md](agents/scribe/charter.md) | 📋 Support | Accepted decisions and governed shared memory. |
| Ralph | Work Monitor | [.squad/agents/ralph/charter.md](agents/ralph/charter.md) | 🔄 Support | Work queue, blockers and readiness monitoring. |
| Rai | RAI Reviewer | [.squad/agents/Rai/charter.md](agents/Rai/charter.md) | 🛡️ Support | Safety, privacy and content review. |
| Fact Checker | Verification & Devil’s Advocate | [.squad/agents/fact-checker/charter.md](agents/fact-checker/charter.md) | 🔍 Support | Source claims, evidence and assumption challenges. |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Project:** stunt-car-racer
- **Created:** 2026-11-17
- **Owner / requester:** Stephen Long
- **Repository:** long2know/stunt-car-racer
- **Team approved:** Phase 1 completed; user confirmed "Yes, hire this team".
- **Installed:** 2026-11-17T15:06:00-08:00 (Phase 2 team initialization only).
- **Naming:** descriptive, per the preserved installed casting policy.
- **Product:** Browser-native Stunt Car Racer remake in progress, aiming for
  authentic driving feel, attractive accelerated visuals and smooth frame
  pacing. North star: an excellent Modern polygon stunt racer, physics chosen
  by feel, time attack/AI racing and cockpit/chase views. Modern authored
  physics remains default; optional Classic/reference is runnable, not certified
  stock-Amiga parity or a claim that the whole broader game is finished.
- **Approved direction:** C# .NET 10 browser-WASM owns the default 120 Hz
  Modern simulation and optional configured-reference Classic session; direct
  custom WebGL2 JavaScript consumes the canonical C# track
  and snapshots, independently of interpolated display-rate rendering. Stephen
  Long's 2026-11-18 implementation authorization settles C# for this slice;
  no TypeScript-versus-WASM spike or metadata-only stop condition remains.
  Apply relevant NFL Blitz architecture and Mahjong rendering/lifecycle patterns
  without inheriting either project's entire stack.
- **Reusable runtime:** Thin C# clock and browser graphics/math/input/audio/capture
  helpers support the playable; no speculative general-purpose-engine detour.
- **Implemented first slice:** Authored approximately 880 m Foundry Heights
  three-lap time attack with suspension, banked/elevated road, natural
  jumps/landings, boost, damage, falls and recovery; HUD, chase/cockpit views,
  keyboard/standard gamepad/classic joystick/touch controls, synthesized audio
  and downloadable WebM canvas-plus-audio recording. Time attack and actual
  same-physics AI rival racing now work in Modern. Fuller progression and
  separately requested future features remain later scope.
- **Working Classic/content path — 2026-11-18:** Actual independently authored
  three-contact integer player kernel, `SourceAmigaSimulation : IGameSession`,
  strict package reader and non-executing reference-data extractor run on real
  external LittleRamp data: 44 sections/398 segments/442 edges. Configured
  reference pace is 0.12 s/step, not stock timing certification. The exact track
  also works with Modern physics; Classic is explicit opt-in only, never
  auto-selected by package availability. Profile:
  `amiga-derived-source-compatible`, `originalStockParity=false`.
- **Optional TNT data result:** Seven external packages generated under
  `artifacts/amiga-source/runtime/TNT/`; all eight 804-byte layouts matched the
  pinned manifest hashes. JerkilyJump is unsupported with the current common
  tables (section 22/template 10 needs 12 samples, profile 77 supplies nine).
  Seven passed reader/geometry/spawn validation, not play-testing or stock
  certification. No padding, partial package, code/UI/catalog or image change.
  Exact paths/hashes/provenance:
  `artifacts/amiga-source/runtime/TNT/generation-receipt.json`.
- **Private geometry path — verified 2026-11-18:** Strict source-format CLI and
  private converted-file UI share exact render/contact edges. Real local file
  chooser load/reset and unsupported original-dynamics rejection passed in fresh
  WASM on normally sandboxed, GPU-enabled Chrome; no HTTP upload or persistence.
  Imported geometry uses explicitly **PROTOTYPE/AUTHORED dynamics**;
  `originalPhysicsAvailable=false`. Evidence: `artifacts/browser/run-s7K1rm/`.
- **Controls/diagnostics:** Steering/view handedness is corrected (+X right,
  +Y up, +Z forward; positive steer/yaw right). Actual keyboard, analogue and
  touch projection checks pass. F3 reports real callback/submission/GPU-completion
  and CPU/GPU timings; rendering has a bounded two-frame queue.
- **Hosting and deployment:** ASP.NET Core static host supports runtime
  `BASE_URL` at root or `/stunt-car-racer`, appropriate WASM/symbol MIME types
  and negotiated Brotli/gzip. `Dockerfile`, `compose.yaml` and `README.md`
  document source builds, running and reverse-proxy setup. Intended future
  hosting is `games.long2know.com/stunt-car-racer`; no public deployment,
  registry push or DNS/proxy changes have been made.
- **Hardware measurement — 2026-11-18:** Parent's actual RTX 3070 run at
  1440x857 measured 60 FPS for GPU-completed/submitted/callback rates during
  driving, CPU 1.23 ms and GPU 0.159 ms. Ready baseline was already 60 FPS:
  no 120 FPS or improvement-attribution claim. Broader hardware targets and
  physical-controller certification remain open; earlier SwiftShader evidence
  is distinct from this measured physical-GPU run.
- **Verified checks — 2026-11-18:** Parent reports Modern 17/17 with preserved
  behavior/numerical goldens and AI race; real-source suite 20/20 including the
  reviewed native component oracle; compatibility 39/39 with 49 CLI invocations;
  Node 21 plus previously qualified optional GPU case; hosting 3/3. Actual RTX
  3070 full WASM browser flow passed default Modern Foundry, moving AI rival/
  position/pause, LittleRamp with Modern, explicit Classic start/drive/right-
  steer/boost/pause, switching back to Modern on the same track and restoration.
  Evidence: `artifacts/browser/run-RBVQPz/`. Classic advanced 31 ticks and
  reached 24.585 m/s during input; stock parity stayed false. Modern measured
  60 FPS; no 120-FPS or new Classic-FPS claim.
- **Source context:** Pinned Phase 1 observations, rights/provenance restrictions,
  shared surface/units boundaries and initial assignments live in
  [decisions.md](decisions.md). References are evidence, not imported code/assets
  or proof of original-game parity.
- **Fidelity qualification, not a coding stop:** The working reference-derived
  kernel and real extraction are implemented and verified, superseding the
  earlier helpers-only/blanket-blocked status. Exact stock parity remains
  unqualified: triangle/per-segment heading differs from reference bilinear/arc
  and stock ASM road sampling; generated trig identity, stock clocks/timers,
  whole-player/long32 overflow oracle, native-disk decoding and reuse rights
  remain limitations. Further fidelity work remains requested where desired;
  these limits do not cancel or block the working candidate.
- **Current status:** Core and browser implementation are locally playable at
  `/stunt-car-racer/`. Parent configured live port 5188 with the ignored,
  absolute external LittleRamp package while keeping Modern Foundry default.
  The combined source image also passed the full actual RTX 3070 browser flow:
  UID 1654/read-only/cap-drop ALL/no-new-privileges, external LittleRamp JSON
  bound read-only outside `/app/wwwroot`, matching served source and no data
  artifact directories in the image. Evidence: `artifacts/browser/run-DnljXk/`;
  image/source hashes are in decisions and runtime-contract. Helper removed;
  no public deployment was performed. Optional TNT generation does not block
  this delivered Modern/Classic slice or claim all future features complete.
- **Scope exclusions:** No unrequested accounts, gameplay backend or online
  multiplayer. Local implementation is authorized; no Git commits, PRs, issues
  or comments have been made. The four built-ins retain their installed charters/policies and are
  not specialist registry entries or domain-routing destinations; @copilot
  auto-assignment remains disabled. Stunts editor/loops/replays are future
  inspiration only; no such feature or Hard Drivin' game-code port is claimed.
