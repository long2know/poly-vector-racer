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
- **Product:** A faithful browser-native remake of the Amiga Stunt Car Racer,
  with attractive accelerated visuals and smooth frame pacing; not a generic
  web app or a box-car demo relabeled as the finished game.
- **Approved direction:** Direct browser WebGL2; fixed-step simulation independent
  of interpolated display-rate rendering. Lead will choose TypeScript versus
  WASM physics after a small behavioral-fidelity/maintenance spike; no automatic
  adoption of NFL Blitz's entire .NET stack.
- **First intended milestone:** One polished drivable elevated track with ramp
  and banking, responsive cockpit/camera and visible suspension,
  acceleration/braking/boost, airborne/landing/crash/recovery, HUD and audio.
  Graybox alone is not final visual acceptance. Rival AI, fuller original
  progression and more tracks remain later overall scope.
- **Acceptance goals:** Stable 60 FPS on representative hardware still to be
  agreed, with 120/144 Hz headroom where capable. These are goals, not measured
  results or guarantees. Validate behavioral fidelity, player-visible visuals,
  real CPU/GPU timing and frame pacing; distinguish physical-GPU results from
  software-GPU/headless screenshots.
- **Source context:** Pinned Phase 1 observations, rights/provenance restrictions,
  shared surface/units boundaries and initial assignments live in
  [decisions.md](decisions.md). References are evidence, not imported code/assets
  or proof of original-game parity.
- **Current status:** Team metadata commissioned; no application implementation,
  physics spike, rendering demo or performance result is claimed.
- **Scope exclusions:** No unrequested accounts, backend or online multiplayer.
  Initial assignments are recorded, not authorization to start a development
  campaign. The four built-ins retain their installed charters/policies and are
  not specialist registry entries or domain-routing destinations; @copilot
  auto-assignment remains disabled.
