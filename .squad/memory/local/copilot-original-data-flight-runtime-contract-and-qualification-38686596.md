---
id: 38686596-17b3-4f1c-bcad-51fd50702b69
class: LOCAL
loadGuidance: [ON-DEMAND]
title: "Original-data flight runtime contract and qualification"
author: "Copilot"
createdAt: 2026-11-19T03:26:14.418Z
metadata: {}
---

OriginalFlightSession implements IFlightSession with the complete FlightPlayerKernel at 8 fixed steps/second and interpolated rendering; Modern flight remains a separate authored 120 Hz model. The original kernel requires the package FlatImage plus ReferenceWindowImage at base 0x10000, not the fully target-RAM-relocated ReferenceImage or the Grumbel LoadSeg/header layout. Contact-model rotation uses unitShift 3, not the drawing model-to-position shift 5. Original-data packages are external opt-in inputs (STUNT_FLIGHT_PACKAGE or local browser ADF/ZIP ingestion), never bundled original arrays/assets. Qualification covers player aircraft Free Flight, including original attitude/integration, terrain/pads, gear/hook, landing/crash and carrier/landmark rules. Enhanced-renderer landmark selection/refresh is explicit; combat, AI/missions, ejection, original screen/audio and unmaterialized drawing operations are not whole-game/retail-certified. Evidence: src/StuntRacer.FlightReference/FULL-STEP-CONTRACT.md; tests/StuntRacer.FlightReferenceChecks/ORIGINAL-PLAYER-EVIDENCE.md (2630 actual-data cases/347160 scalar comparisons, zero differences); tests/StuntRacer.FlightOriginalChecks/Program.cs (7 actual-data integration groups); artifacts/browser/run-Cajl4C/evidence.json (actual supplied ZIP imported in WASM; real RTX 3070 runway takeoff/carrier launch/mode switching/invalid ZIP rejection and phone/landscape/tablet touch). README now documents the playable original-data mode rather than the superseded component-only limitation.
