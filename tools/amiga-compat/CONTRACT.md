# Amiga compatibility integration contract — 2026-11-18

## Focused review / decisions

This is an additive contract, not an original-physics implementation or a parity
receipt. Evidence is the supplied `artifacts/amiga-source/investigation.json` and
accepted `.squad/decisions.md`; no original source bodies were used for this work.
The investigation's earlier integration stop describes that investigation, not
the current authorization to implement independently authored compatibility code.

- Preserve the authored default, current controls, commands and frame DTOs.
- Separate source-format track support, authored dynamics and original-physics
  qualification. Import success and integer helpers do not establish original dynamics.
- Keep original layouts, common data, tables, executables, derived geometry and
  oracle recordings private under ignored `artifacts/`, never in tracked fixtures,
  WASM/static assets or Docker layers. Parent must enforce Docker-context exclusion;
  `.gitignore` alone does not do that.
- No copying/translating upstream bodies or embedding original arrays. Public
  repositories, null license metadata and empty license categories are not grants.
  User-supplied files and acknowledgement switches do not grant code/data rights.

## Shared public API (implemented here)

Namespace: `StuntRacer.Core.Compatibility`; file:
`src/StuntRacer.Core/Compatibility/CompatibilityProfile.cs`.

- `CompatibilityProfile.AuthoredId = "authored"` and
  `CompatibilityProfile.AmigaExperimentalId = "amiga-experimental"`.
- Immutable singleton properties `CompatibilityProfile.Authored` and
  `CompatibilityProfile.AmigaExperimental`.
- Instance properties: `string Id`, `string DisplayName`,
  `double? SimulationStepSeconds`, `CompatibilityQualification Qualification`.
  Authored is `1.0 / 120.0`; experimental is **null**, not a runnable source clock.
- `CompatibilityQualification.AuthoredApproximation` and `.UnqualifiedCandidate`.
  There is deliberately no faithful/complete/verified enum member or setter.
- Passive metadata record
  `CompatibilityAssetProvenance(string Role, long ByteLength, string Sha256,
  string SourceId, string? SourceRevision)`. This is not a validator or permission
  attestation; readers validate its contents against actual inputs.

Do not serialize this metadata into `GameSnapshot`, `CarSnapshot` or the stable
track DTO. Use a separate import/initialization report. Wire enum spellings below
are explicit strings, not the serializer's default numeric enum representation.

## Lane boundaries / immediate owner actions

| Owner | Files and next action |
|---|---|
| Gameplay | Only `src/StuntRacer.Core/Compatibility/Tracks/` and `tools/AmigaTrackImport/`: strict layout reader, private common-package reader, converter and canonical imported track data. Do not change existing `TrackDefinition.cs` or simulation. |
| Physics | Only `src/StuntRacer.Core/Compatibility/Physics/`: explicit integer/angle/state helpers and strict trace verifier. No existing simulation edits, float spring retuning or invented original dynamics. |
| Tester | Tests only: synthetic reader/converter/arithmetic/trace cases; retain all eight authored native cases. Do not require original files in CI. |
| Parent | Existing simulation/track factory, browser bridge/JS/UI, Host/scripts/root documentation/Docker and browser checks. Integrate explicit profile selection, exact canonical edges and metadata without weakening the default. Coordinate rendering with the already-active Graphics owner; do not duplicate that lane. |

Lead owns only this file and `CompatibilityProfile.cs`. No branch operations,
dependency changes or governance-memory writes are part of this deliverable.

## Proposed lane API (names reserved; not implemented here)

All track types live in `StuntRacer.Core.Compatibility.Tracks`:

```csharp
AmigaTrackLayout AmigaTrackLayoutReader.Read(ReadOnlySpan<byte> bytes);
AmigaCommonData AmigaCommonDataReader.Read(Stream utf8Json);
AmigaImportedTrack AmigaTrackConverter.Convert(
    AmigaTrackLayout layout, AmigaCommonData commonData,
    double displayMetresPerSourceUnit);
```

These are static entry points. `AmigaImportedTrack` must retain section/segment
identity, raw layout flags, source-unit left/right cross-sections and the supplied
common model. Its display projection exposes `Name`, `Length`, `Width`, `Points`
(`TrackPoint[]`) and `Edges` (ordered exact left/right world-space vertices with
section/segment IDs). Gameplay defines the supporting records in its lane.
One canonical imported model supplies both rendering and road queries; Physics
must not regenerate a separate road.

Parent integration hook proposal:
`TrackDefinition.FromCompatibility(AmigaImportedTrack track)`. The existing
constructor is private, so this needs a **parent-owned** additive factory and
exact-edge support; the Gameplay lane cannot complete that by itself.
Midpoint/heading/bank/width cannot represent arbitrary skewed source edges.
Do not reconstruct those edges from the legacy points or declare triangle
collision to be the original integer road-height sampler. Preserve the stable
track DTO and coordinate an optional separate exact-geometry payload with Graphics.

Physics namespace: `StuntRacer.Core.Compatibility.Physics`. Reserve
`AmigaIntegerMath`, `AmigaAngle` and
`AmigaTraceVerification AmigaTraceVerifier.Verify(Stream expected, Stream actual)`.
Physics defines its explicit-width raw state and verification result in its lane.
The result reports `Status` (`match`, `mismatch`, `blocked`), `ComparedTicks`,
`FirstMismatchTick` and `Reason`; a synthetic match is not an original qualification.

## Strict layout / common-data path

Known layout facts: exactly 804 bytes; count at 0, start at 1; 100-byte packed
positions at 2; template/orientation bytes at 102; left/right profile bytes at
202/302; 100 signed **big-endian int16** left/right shifts at 402/602; standard/super
boost bytes at 802/803. Count must be 1..100 and start must be below count.
Position x is the low nibble, z the high nibble. Template is the low nibble,
quarter-turn orientation bits 6..7, traversal reversal bit 4. Preserve raw profile
bytes alongside masked IDs: left high-bit storage semantics and right high-bit
colour are not interchangeable. Do not blanket-swap byte fields.

Reject truncation, trailing bytes, unknown/unsupported template or profile IDs,
missing referenced common data, invalid lengths and unsupported flags/semantics.
Do not silently discard unknown bit 5, invent profiles, smooth/resample to the
authored 704 points, make colour markers into gaps or supply dummy road.
Preserve oriented/reversed left/right traversal and seam/section identities.
DrawBridge dynamics are outside the first static-track slice: reject a requested
dynamic original circuit rather than silently freezing it under its original name.

`common-data.json` is a **new private interchange format**, not a claimed native
Amiga file or an upstream source parser. Its required version-1 fields are:

| Field | Contract |
|---|---|
| `schemaVersion` | Integer `1`. |
| `sourceId`, `sourceRevision` | Nonempty provenance identifier and nullable exact revision. |
| `templates` | Unique supported `id`; `restartEligible`; ordered `crossSections` of signed integer `leftX`, `leftZ`, `rightX`, `rightZ` in declared source units. No prebundled defaults. |
| `heightProfiles` | Unique `id` in 0..127; `sourceStorage` descriptor; decoded integer `samples` and their declared normalization. Counts must satisfy every referenced template. |
| `coordinateTransform` | `xAxis`, `yAxis`, `zAxis`: each one of `+x,-x,+y,-y,+z,-z`, using each absolute axis once; positive integer `xDivisor`, `yDivisor`, `zDivisor`; nonempty `evidenceId`. No inferred sign/precision defaults. |
| `encodingEvidenceId` | Identifies the independently established decoding/normalization, not a permission claim. |

Gameplay documents exact supporting JSON shapes with its reader. Accept synthetic
normalized samples now. Original packed byte/word decoding, normalization and
storage flags require separately established facts; this mapping does not supply
the complete packed-height algorithm. If those semantics or the transform are
unknown, allow layout inspection only and block conversion, rather than inventing
an extractor or treating a user declaration as verified original data.
For example, `worldX = displayMetresPerSourceUnit * signedRawAxis(xAxis) / xDivisor`;
the corresponding Y/Z rules use their declared axes/divisors.

## CLI proposal / deterministic outputs

Gameplay implements this package-free native tool; commands below are proposals,
not commands supplied by the Lead deliverable:

```text
dotnet run --project tools/AmigaTrackImport -- inspect --layout <file>
dotnet run --project tools/AmigaTrackImport -- convert --layout <file> --common <common-data.json> --provenance <inputs.json> --display-metres-per-source-unit <positive-number> --name <label> --out artifacts/amiga-compat/<name>.track.json
dotnet run --project tools/AmigaTrackImport -- verify-trace --expected <oracle.jsonl> --actual <candidate.jsonl>
```

No network access, built-in original data, automatic extraction, implicit scale,
tick default or original-data output outside ignored `artifacts/`. Reject an
existing `--out` rather than silently overwrite it; validate before creating it.
`inspect` emits one JSON object with byte hash, count/start, raw section fields
and qualification; it does not fabricate geometry or require common data.
`convert` writes one private JSON document with `schemaVersion:1`, stable `track`
fields (`name,length,width,points`), separate `geometry` exact edges/identities,
`provenance` and `compatibility`. Emit one summary object on stdout, diagnostics
on stderr. No wall timestamps or absolute local paths in deterministic output.
`verify-trace` delegates to Physics and emits its structured result on stdout.

Exit codes: `0` inspected/converted or full trace matched; `2` invalid arguments,
malformed/unsupported inputs or hash mismatch; `3` trace mismatch; `4` blocked
(missing common data, baseline, table, timing, transform or qualification evidence).
No trace is **blocked**, not a passing empty comparison. No failure emits a
success-shaped track, partial replacement or silent authored fallback.

## Provenance / qualification schema

`inputs.json` and output `provenance` use `schemaVersion:1` and `assets`, an array
of camel-case `CompatibilityAssetProvenance` records. Required roles are `layout`
and `commonData`; optional distinct roles are `executable`, `tables`,
`expectedTrace`, `rightsEvidence`. Reject duplicate roles. `byteLength` is the exact
nonnegative input byte count (`layout` must be 804); `sha256` is 64 lowercase hex
digits over the **entire input bytes**, not parsed/reformatted content. Recompute
and compare; filenames are not identity. `sourceId` is nonempty; `sourceRevision`
is an exact supplied revision or null, never an inferred upstream pin. Every
actually consumed additional table/evidence file must also be hashed; extend the
role with a stable identifier if more than one such file is required.

`rights` contains `status` and nullable `evidenceSha256`. Use `unresolved` until
separately reviewed evidence establishes the relevant scope; the importer never
upgrades this field. Pin context is `fluffyfreak/stuntcarracer` at
`306be6764b4ecee80c0758a0fd9f664c66bbb0d0`, not a grant or executable identity.

Output `compatibility` always includes:

- `profileId:"amiga-experimental"`, `qualification:"unqualified-candidate"`,
  `dynamics:"not-implemented"` and `simulationStepSeconds:null` for this path.
- `displayMetresPerSourceUnit`, the explicit coordinate transform and its evidence ID.
- `blockers`: concrete remaining data, numeric, timing, trace and rights gates.
  These are observations, not caller-set attestations or an `isFaithful` boolean.

Original qualification remains blocked on **all** of: separately reviewed
code/data rights; identified executable/version/region and table hashes; exact
integer operation/order and original road-sampler evidence; independent executable
input/state traces; measured timing/overrun and race-timer semantics. Missing
`tables.bin`/sine data is not replaced with generated trigonometry. The C++ road
height is explicitly different from Amiga. Three original contacts are not the
authored four springs. A private layout alone cannot clear these gates.

## Axes, units, clocks and acceptance

- World is left-handed: +X right, +Y up, +Z forward. Positive steer is right;
  positive yaw rotates +Z toward +X. Heading is `atan2(forwardX, forwardZ)`;
  positive pitch raises the nose and positive bank/roll raises the right edge.
  Never fix a source transform by reversing user inputs.
- Preserve raw source integers. First apply the documented source axis/sign and
  per-axis precision normalization; **then** apply the one finite positive user
  metres-per-source-unit scale. Source player-Y's sign/precision is not a
  universal geometry transform. Source angles wrap at 65536; radians are display
  projections only. No rounded display values feed simulation.
- Keep fixed raw ticks, wall pacing, original displayed race timers and render
  interpolation separate. Original cadence is unknown: six PAL VBL comments are
  a hypothesis (including a possible 6+1 interpretation), not a tick setting.
  Neither descendant 0.14 s/60 Hz nor authored 120 Hz is an oracle. Do not start
  source dynamics with a null source step or silently inherit the authored 250 ms
  discard policy. Oracle runs are tick-indexed and never discard ticks.
- Keep existing frame fields and normalized resources unchanged, `paceCar:null`
  and suspension order FL/FR/RL/RR. A future three-contact adapter may display the
  rear-central value twice, explicitly as visualization, never as two extra
  physical contacts. Original dynamics require a real implementation and trace.
- Tester checks synthetic big-endian shifts, raw flags/IDs, missing common data,
  reversal/orientation/seams/exact edges, scale-versus-axis separation, positive
  right steering, wrapping/sign/rounding/carry cases and all trace failure modes.
  Traces require matching baseline/table/input/schema metadata, an initial state,
  contiguous unique tick records, exact typed raw fields, at least one transition,
  equal lengths and strict equality; missing fields, duplicate ticks, early EOF,
  extra tails and mismatches fail. A same-implementation or remake trace is not
  an independent original oracle.
- Retain the eight authored checks; compare tick-indexed runs under 30/60/120/144
  Hz and jittered rendering without altering state during interpolation.
  No FPS, original parity or rights completion claim follows from these checks.

## Lead validation receipt

`dotnet build src/StuntRacer.Core/StuntRacer.Core.csproj --no-restore --nologo --verbosity quiet`
succeeded on 2026-11-18: zero warnings/errors, 2.60 s. No dependencies restored;
native/browser suites were not rerun in this contract-only lane.
