# Full player contract — validated original-data implementation

The public records and data factory are in `PlayerFlightContracts.cs`.
`FlightPlayerKernel` now implements and advertises the ordered aircraft
`FullPlayerStep`, verified on the supplied original data against the retained
native C oracle. The old `FlightReferenceKernel` deliberately remains a
component-only API. Exact evidence/qualifications:
`tests/StuntRacer.FlightReferenceChecks/ORIGINAL-PLAYER-EVIDENCE.md`.

## Data handoff (stable)

```csharp
var data = FlightOriginalData.FromImages(
    flatBytes, referenceRelocatedBytes,
    new FlightDataIdentity(executableSha256, flatSha256, referenceSha256,
        variantDescription, referenceRelocationProfile));
```

Both byte buffers start at flat address **0x10000**, so array index zero means
address 0x10000. They must have equal lengths and cover at least through
0x49a11. The factory checks their SHA-256 receipts and copies them. Never pass a
LoadSeg 0x20000+header image. `ExecutableSha256` identifies the actual extracted
cracked/modified executable; `Variant` must not silently call it stock.

The flat buffer supplies ordinary tables/pointers/terrain/pads/ship structures.
The reference-relocated buffer supplies numeric sine/inverse-sine/pitch-scale
reads, including adjacent instruction/operand words. Original data stays
external; the factory performs no archive/disk extraction or relocation.

## Environment handoff (stable)

```csharp
var model = new FlightPlayerModel(firstFourActualNearModelVertices, unitShift: 3);
var environment = new FlightEnvironment(
    model, carrierOrNull, landmarkSlots, landmarkZBias: 48,
    FlightContactVertexPolicy.RotateModelAtEntry);
```

- Model vertices are unrotated original model words, at least the first four.
- `FlightCarrier` takes exactly **58 already draw-rotated record-slot vertices**,
  position, matrix B, class 0x20 (carrier) / 0x21 (second ship), flags, group,
  unit shift and attitude. Null means explicitly no ship in this scene.
- `FlightLandmark` has cell bytes, outer box, nodes and reference/normal planes,
  in last-built/current scan order, maximum 11 slots / four nodes / eight planes.
  An explicitly empty list means no supplied landmark contact volumes.
- Landmark z-bias is explicit: 48 for the documented reference-machine profile,
  1 for the flat Musashi rig; do not infer it from an unrelated pointer base.
- `RotateModelAtEntry` is an explicit no-original-renderer contact profile.
  `SuppliedPreviousDraw` instead uses `state.Vertex0..3` and `ModelPartId`
  exactly as the previous original draw left them. Part id remains explicit:
  negative IDs disable the source three-vertex ground test.
- `RuntimeWord(address)` is optional live memory for dynamic over-reads.
  Missing genuinely dynamic values are rejected, NOT silently filled with zero.
  `CallerOwnsViewedCell` preserves supplied camera cell words when true.

## Full stepping API

```csharp
var kernel = new FlightPlayerKernel(data);
FlightPlayerState state = kernel.CreatePlayer(initialRawState, environment);
state = kernel.SetAttitude(state, pitchWord, headingWord, rollWord);
state = kernel.StepPlayer(state, digitalInput, environment);
```

`FlightPlayerState.Raw` is the unchanged component `FlightRawState`; the new
record adds both nine-word matrices, turn/stall/contact/gear/crash/event fields.
`CreatePlayer` does not choose a mission or original world start: parent supplies
position/flags/angles/fuel/throttle/class from its start adapter.

Carrier/contact helpers:

```csharp
FlightMatrix inverse = kernel.BuildInverseAttitudeMatrix(pitch, heading, roll);
RawVector[] rotated = FlightPlayerKernel.RotateContactVertices(sourceVertices, inverse);
```

`BuildMatrixA`, `BuildMatrixB`, `ComposeAttitude`, `StepAttitude`, `QueryTerrain`
and `RefreshContactVertices` are also public for focused validation and adapter
work. `BuildMatrixB` takes the raw builder arguments; prefer
`BuildInverseAttitudeMatrix` when starting from an aircraft/ship attitude.

The collision `UnitShift` is the original record's +0x7D low nibble **3** for
these player/carrier models. It is not the model-to-position exponent **5**.

Events (`EventLiftOff`, `EventTouchdown`, `EventCrash`, `EventRearm`, etc.) retain
the native core's sticky-until-consumed behavior. A runtime consumer should clear
them after processing; the flight step does not invent an audio/log consumer.
The sound-request fields are requests, not an original audio implementation.

Caller owns the 8-Hz scheduling choice and rendering conversion. Pause is
`state.Paused`; a call reproduces the source paused-player pre-steps.
No Browser/JS/UI/Host/Modern Flight dependencies are introduced. This slice
owns the aircraft flight step, not combat/AI/mission dispatcher or crash-menu
restart. Unsupported entity/ejection/scaled-motion/dynamic-read paths are explicitly
rejected rather than substituted with authored flight dynamics. Unknown live
lookups throw unless `RuntimeWord` supplies that actual word. A supported frame
never returns a success-shaped partial step.

Ordering is key events; pause/world pre-steps; dying/idle view gates; command
conversion/direct warnings/propulsion; surface/attitude integration; complete
motion/floor/lift-off/touchdown; X/Z/cells; gear/hook progress; ship/landmark/ground
contact and result/tail; frame/panel caches. The supplied scene/draw snapshot is
the boundary: original display-list construction/visibility, combat/AI,
mission crash-menu/restart and original screen/audio are separate systems.
