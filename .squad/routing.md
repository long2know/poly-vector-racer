# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, source evidence/provenance and integration | Lead | Pinned source matrix, rights gates, dependency and integration decisions |
| Units, coordinates, clocks and cross-domain contracts | Lead | Fixed-step/interpolation boundaries, small TypeScript-versus-WASM physics spike |
| Vehicle dynamics and physical recovery | Physics | Suspension/contact, banking, jumps, boost, impacts, damage, recovery behavior |
| Deterministic physics reference cases | Physics | Behavioral scenarios and input traces for Tester to validate |
| WebGL2 rendering and technical art | Graphics | Recognizable car/cockpit, visible suspension, elevated-road silhouette, lighting/materials |
| GPU resources and rendering lifecycle | Graphics | Instancing, measured GPU work, DPR/resize, context loss and teardown |
| Canonical track definition and sampled road surface | Gameplay | One surface consumed by Physics and displayed by Graphics; ramps, banking and edges |
| Player controls, presentation and race flow | Gameplay | Keyboard/gamepad, camera, HUD/audio, recovery interaction and race state |
| Later racing systems | Gameplay | Rival AI, fuller original progression and additional tracks; not all first-slice requirements |
| Fidelity, visual acceptance and repeatability | Tester | Outcome checks across render schedules, reproducible traces, screenshot-based player-visible acceptance |
| Browser/device compatibility and performance evidence | Tester | Agreed hardware/resolution profiles, CPU/GPU timing, frame pacing, physical versus software GPU |
| Unmatched or disputed ownership | Lead | Choose one accountable specialist and resolve contract boundaries |

Routes use the five approved registered specialist names. Scribe, Ralph, Rai and
Fact Checker retain their built-in support protocols; they are not domain-routing
destinations. The Coordinator records accepted decisions in `decisions.md`;
Scribe merges accepted decisions only when needed. Ralph monitors work without
starting an unrequested campaign. Rai reviews safety/privacy/content, and Fact
Checker verifies claims or challenges assumptions when commissioned.

## Shared Ownership Boundaries

- Lead owns the source/provenance matrix and coordinates/units/clocks contracts.
- Gameplay owns the canonical track definition and shared sampled road surface.
  Physics consumes it for contact; Graphics renders that same surface, without
  inventing separate collision or visual road definitions.
- Physics owns vehicle state and behavior. Graphics consumes simulation snapshots
  and interpolates display frames; render cadence must not own game state.
- Gameplay owns recovery interaction/race flow; Physics owns recovery dynamics.
  Graphics owns cockpit rendering; Gameplay owns camera behavior.
- Tester checks the actual visible/driving contract and measured frame times,
  not merely rAF counts. Goals are not reported as benchmark results.

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
