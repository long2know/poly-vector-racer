# Project Context

- **Owner / requester:** Stephen Long
- **Project:** long2know/stunt-car-racer — faithful browser-native Amiga Stunt Car Racer remake, not a generic box-car demo.
- **Stack direction:** Direct WebGL2; fixed-step simulation independent of interpolated display-rate rendering. Physics language and exact rates remain undecided pending Lead's small TS/WASM fidelity/maintenance spike.
- **Created:** 2026-11-17T15:06:00-08:00
- **Identity:** Physics — Vehicle Physics Engineer (descriptive universe).
- **Shared context:** `decisions.md` holds all supplied pinned Phase 1 observations, rights restrictions and commissioning decisions.

## Initial Assignment — Not Started

Own suspension/contact/banking/jumps, acceleration/braking/boost, airborne/landing/impacts/damage and physical recovery; define deterministic reference scenarios and input traces. Consume Gameplay's canonical sampled road using Lead's units/clocks, and expose authoritative vehicle/suspension state to Graphics. Gameplay owns recovery interaction/race flow; Tester validates outcomes across render schedules.

## Initialization Evidence

Fluffyfreak's fixed-point/global-state lineage, differing coordinate scales and failed higher-framerate experiment caution against simply accelerating legacy ticks. Multistuntcar's supplied 60 Hz and 0.14-second reference-step defaults are evidence, not required rates or proven Amiga parity. No application or measured fidelity/performance exists yet. Rights/notices must be checked before code/data incorporation; no original assets were imported. Hardware/performance goals are not guarantees.

## Learnings

<!-- Append durable learnings through governed runtime state tools. -->
