# Project Context

- **Owner / requester:** Stephen Long
- **Project:** long2know/stunt-car-racer — browser-native remake of the Amiga Stunt Car Racer with authentic driving, polished accelerated visuals and smooth frame pacing.
- **Stack direction:** Direct browser WebGL2; fixed-step simulation plus interpolated display-rate rendering. TypeScript versus WASM physics remains a small Lead-owned fidelity/maintenance spike, not a decided stack.
- **Created:** 2026-11-17T15:06:00-08:00
- **Identity:** Lead — Architecture & Source Lead (descriptive universe).
- **Shared context:** Read `decisions.md` for the supplied Phase 1 source pins/findings, provenance gate, acceptance goals and initial assignments.

## Initial Assignment — Not Started

Own architecture, pinned source/provenance matrix, coordinates/units/clocks and contracts, the small TS/WASM spike decision and integration. Gameplay owns one canonical sampled road consumed by Physics and rendered by Graphics; Physics owns vehicle state. Do not copy NFL Blitz's full .NET architecture by default. Keep the first polished elevated track separate from later rival AI/progression/more tracks.

## Initialization Evidence

Only team metadata is installed; no application, spike, parity or benchmark result is claimed. Stable 60 FPS and capable-device 120/144 Hz headroom are goals pending agreed hardware and CPU/GPU/frame-time evidence. No original binaries/code/assets imported; rights/notices remain a gate. Source observations are handed-off Phase 1 evidence, not newly researched or blanket reuse clearance. No unrequested backend/accounts/online multiplayer.

## Learnings

<!-- Append durable learnings through governed runtime state tools. -->
