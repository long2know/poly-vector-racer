# Project Context

- **Owner / requester:** Stephen Long
- **Project:** long2know/stunt-car-racer — faithful browser-native Amiga Stunt Car Racer remake, with polished direct WebGL visuals and stable pacing.
- **Stack direction:** Direct WebGL2; fixed-step simulation independent of interpolated display rendering. Lead will decide TS/WASM physics after a small fidelity/maintenance spike.
- **Created:** 2026-11-17T15:06:00-08:00
- **Identity:** Tester — QA & Browser Performance Engineer (descriptive universe).
- **Shared context:** `decisions.md` records supplied Phase 1 pins/limitations, provenance gate, commissioning goals and initial assignments.

## Initial Assignment — Not Started

Own reproducible fidelity scenarios, repeatability and matching outcomes under varied render schedules. Validate Physics' input traces and Gameplay/Graphics' shared surface, driving/cockpit/track presentation and lifecycle. Establish screenshot-based visual acceptance beyond graybox and representative browser/hardware/resolution profiles with objective CPU/GPU/frame-time acceptance. Check actual player-visible behavior, not rAF counts.

## Initialization Evidence

Stable 60 FPS baseline on agreed representative hardware and capable-device 120/144 Hz headroom are goals, not measurements/guarantees. Hardware profiles and detailed budgets are not yet agreed. Distinguish physical GPU results from software-GPU/headless screenshots; screenshots alone prove neither acceleration nor pacing. Original-game parity remains unproven, including reference-implementation claims. No application/tests/performance evidence is claimed by this metadata installation. A rejected artifact requires a different revision author under the reviewer protocol.

## Learnings

<!-- Append durable learnings through governed runtime state tools. -->
