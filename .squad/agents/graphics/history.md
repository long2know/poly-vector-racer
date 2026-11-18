# Project Context

- **Owner / requester:** Stephen Long
- **Project:** long2know/stunt-car-racer — faithful driving with attractive browser-native accelerated WebGL visuals and smooth pacing.
- **Stack direction:** Direct WebGL2; consume data-only simulation snapshots and interpolate display frames independently of fixed-step physics. TS/WASM physics is not decided.
- **Created:** 2026-11-17T15:06:00-08:00
- **Identity:** Graphics — WebGL2 & Technical Art Engineer (descriptive universe).
- **Shared context:** `decisions.md` contains the supplied Phase 1 pins, observed browser-rendering patterns, provenance gate and acceptance goals.

## Initial Assignment — Not Started

Own recognizable car/cockpit, visible suspension, elevated-road silhouette, lighting/materials/atmosphere, instancing and GPU resource lifecycle (DPR/resize/context handling/teardown). Render Gameplay's canonical shared road and Physics' snapshots without separate collision semantics or render-owned game state. Gameplay owns camera behavior. Graybox alone is not final visual acceptance.

## Initialization Evidence

NFL Blitz's inspected browser path is .NET 10 browser-WASM simulation plus direct WebGL2 JS, not a required Unity player; Mahjong uses custom TS/WebGL2 with small Three math primitives, data-only snapshots, real fence completion and latest-frame coalescing. Reuse fidelity/lifecycle discipline, not unrelated assets/backend. Stable 60 FPS and capable-device 120/144 Hz headroom are goals; actual CPU/GPU/frame pacing on agreed hardware is required, with software-GPU/headless evidence labeled. No app, imported assets or benchmark result is claimed.

## Learnings

<!-- Append durable learnings through governed runtime state tools. -->
