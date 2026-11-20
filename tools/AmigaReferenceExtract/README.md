# Private reference-data extraction

This package-free .NET 10 tool **reads numeric declarations; it never compiles,
executes, evaluates or loads upstream code**. It supports the explicitly mapped
ptitSeb/fluffyfreak reference-remake track format, not packed Amiga disk files.
Original source/data and generated packages remain external, ignored and private.
The authored 120 Hz game remains the default; Classic/reference selection belongs
to the host and is explicit.

## Reproduce the real LittleRamp package

From the repository root, using already supplied files:

```sh
bash scripts/dotnet.sh run --project tools/AmigaReferenceExtract -- extract \
  --track-source artifacts/amiga-source/reference/ptitSeb/Track.cpp \
  --track-header artifacts/amiga-source/reference/ptitSeb/Track.h \
  --behavior-source artifacts/amiga-source/reference/ptitSeb/Car_Behaviour.cpp \
  --engine-source 'artifacts/amiga-source/reference/fluffyfreak/3D Engine.cpp' \
  --engine-header 'artifacts/amiga-source/reference/fluffyfreak/3D Engine.h' \
  --behavior-spec artifacts/amiga-source/source-behavior-spec.json \
  --layout artifacts/amiga-source/reference/fluffyfreak/Tracks/LittleRamp.bin \
  --source-id ptitSeb/stuntcarremake \
  --revision c56327db5685c2ae63ea3c73d64ef100183b9359 \
  --engine-source-id fluffyfreak/stuntcarracer \
  --engine-revision 306be6764b4ecee80c0758a0fd9f664c66bbb0d0 \
  --layout-source-id fluffyfreak/stuntcarracer \
  --layout-revision 306be6764b4ecee80c0758a0fd9f664c66bbb0d0 \
  --display-metres-per-pc-unit 0.015625 --name LittleRamp \
  --out artifacts/amiga-source/runtime/LittleRamp.source-package.json
```

All options shown are required, unique and case-sensitive. The display scale is
an explicit presentation choice, not a calibration of stock physical metres.
An existing output is refused; choose another private filename for a separate run.
Exit 0 means extraction/validation succeeded; exit 2 means invalid/missing,
unsupported or unsafe input/output. Success writes one summary JSON object to
stdout; errors write stderr, without a partial success package.

The actual first extraction produced 44 sections, 398 segments, 442 section
cross-sections, seven populated templates, 124 populated profiles (four of the
128 descriptors are NULL), 81,920 generated sine entries and 128 externally
extracted cosine-conversion values. No source arrays are bundled in this tool.

## Recognized declaration grammar and bounds

The scanner makes comments and quoted literals opaque. Named arrays must have
one unambiguous declaration and a complete semicolon-terminated initializer.
Values are decimal/hex signed integers, whitelisted `TRUE`/`FALSE`/`NULL`,
nested brace lists, pointer identifiers in descriptor slots, or
`sizeof(the-same-named-byte-array)` in height descriptor size slots.
Array dimensions and selected numeric object-like defines accept parentheses,
known dimension identifiers and checked integer `+ - * /`; division must be exact.
The `PI` define accepts a finite decimal literal only.

Calls, casts, assignments, arbitrary expressions, octal-style ambiguity,
duplicate definitions, unmatched comments/literals/braces, descriptor mismatches,
out-of-range bytes/integers and inconsistent array sizes fail. The scanner is not
a general C++ parser or preprocessor; ambiguous conditional declarations fail
rather than executing/preprocessing the source.

Source inputs are capped at 4 MiB each, layout at exactly 804 bytes, initializer
nesting at eight levels, and a brace list at 100,000 values. Dimensions are checked
against supplied values, not used to allocate unbounded arrays. Numeric dimensions
and defines are limited to 256 characters; declaration regex timeouts fail closed.

The tool extracts:

- The seven populated `Amiga_Piece*_XZ` byte arrays and `Piece_Templates`
  metadata, decoding signed little-endian 16-bit X/Z pairs.
- All 128 `Amiga_Piece_Y` descriptors and their referenced B/W byte arrays.
  Word samples are unsigned big-endian values modulo 32768. Byte samples use
  their low base-16 digit as a 256-unit contribution and their next three bits
  as a 32-unit contribution; they are not signed byte heights.
- The sixteen restart flags, preserving their bytes in template metadata.
- `Cosine_Conversion_Table` from the external behavior file.
- Coordinate constants from the supplied headers and scalar behavior defines.
  Additional independently verified functional constants and chosen clock
  parameters come from `source-behavior-spec.json` (`runtimeConstants`, `clock`).
- A reference-style 81,920-entry Q14 sine/overlap table generated from the
  supplied `PI`, precision and full-angle declarations with cumulative double
  phases and truncation toward zero. It is not the stock Amiga sine table;
  cross-libm byte identity is not asserted.

Every consumed input's **complete bytes** are SHA-256 hashed and counted. Source
IDs/revisions identify the caller-supplied files; hashes are fingerprints, not
proof that a pin, permission or stock-equivalence assertion is authentic.

## Exact runtime package schema

All keys below are required. Unknown, duplicate or missing fields in the package,
common data, units, clock, metadata and provenance are rejected. Numeric table and
constant objects deliberately have caller-named, unique bounded keys.

```text
{
  "schemaVersion": 1,
  "kind": "stunt-amiga-source-runtime",
  "profileId": "amiga-derived-source-compatible",
  "originalStockParity": false,
  "name": LABEL,
  "layoutBase64": CANONICAL_BASE64_OF_804_BYTES,
  "commonData": COMMON_DATA_V1,
  "units": {
    "pcFactor": 2, "positionPrecision": 16384,
    "localYFactor": 4, "roadHeightDivisor": 4,
    "displayMetresPerPcUnit": POSITIVE_FINITE_NUMBER, "evidenceId": IDENTIFIER
  },
  "clock": {
    "callbackHertz": POSITIVE_INTEGER,
    "callbacksPerStep": POSITIVE_INTEGER,
    "initialCallbackRunsImmediately": BOOLEAN,
    "simulationStepSeconds": callbacksPerStep / callbackHertz,
    "evidenceId": IDENTIFIER
  },
  "lookups": {
    "sinQ14": [INTEGER_IN_MINUS_16384_TO_16384],
    "cosineConversion": [INTEGER_IN_0_TO_256],
    "numericTables": { TABLE_NAME: [INT64] }
  },
  "templateMetadata": [
    {
      "id": INTEGER_0_TO_15, "numSegments": INTEGER_1_TO_13,
      "curveToLeft": BOOLEAN, "type": BYTE,
      "lengthReduction": BYTE, "steeringAmount": BYTE, "restartFlags": BYTE
    }
  ],
  "constants": { CONSTANT_NAME: INT32 },
  "provenance": [
    {
      "role": IDENTIFIER, "byteLength": NONNEGATIVE_INT64,
      "sha256": LOWERCASE_64_HEX, "sourceId": IDENTIFIER,
      "sourceRevision": IDENTIFIER | null
    }
  ]
}
```

`COMMON_DATA_V1` is the strict decoded format documented in
`../AmigaTrackImport/README.md`. This reference path requires 2048-unit tiles,
pivot `(1024,1024)`, `x-to-negative-z`, `travel-ordered` heights,
`next-section-start` seams, identity X/Z, display Y divisor four and integer
sample/shift normalization factors one. Common source identity must equal the
`trackSource` provenance asset. The embedded layout hash/count must match the
`layout` asset; every provenance role must be unique.
Reference height profiles contain at most fourteen samples, matching the mapped
destination capacity; shorter profiles must still cover every referenced template.

Only the **explicit reference reader** permits raw orientation metadata bit 5,
preserving it unchanged, and consumes profile prefixes when profiles are longer
than a template. The strict compatibility reader's reserved-bit and exact-count
behavior is unchanged. Raw left storage flags remain checked against descriptor
storage; right colour flags are never treated as storage selectors or gaps.

`sinQ14` has 81,920 entries for real reference extraction, permitting direct
cosine indexing at `angle+16384`. A 65,536-entry periodic table is also accepted
for explicitly synthetic/candidate fixtures; Physics distinguishes the indexing.
Other lengths fail. `cosineConversion` has exactly 128 entries. Up to 64 numeric
tables with 1..65536 values/table and up to 128 int32 constants are supported.
Required constants include `restartLaneOffsetPcUnits`, `restartDropWheelUnits`.
The complete JSON is limited to 16 MiB. Native vertices must fit signed int32 Q14.

## Public C# API and units

Namespace `StuntRacer.Core.SourceRuntime`, source files under
`src/StuntRacer.Core/SourceRuntime/Data/`:

```csharp
SourceAmigaPackage SourceAmigaPackageReader.Read(string json);
SourceAmigaPackage SourceAmigaPackageReader.Read(Stream utf8Json);
bool SourceAmigaPackage.TrySampleRawRoad(
    long worldXQ14, long worldZQ14, long referenceHeightQ14,
    out SourceRoadContact contact);
```

The stream stays open. Malformed/unsupported packages throw `InvalidDataException`.
`SourceAmigaPackage` exposes:

- `Track`: the exact `TrackDefinition.FromGeometry` result, rotated to the layout
  start section. Both Modern-on-source-content and Classic may use this same
  object; creating/reading a package does not select or run either mode.
- `ImportedTrack`: canonical section-order `AmigaImportedTrack` with raw layout,
  common data, integer source edges, seam identities and exact display triangles.
- `Layout`, `Units`, `Clock`, `Lookups`, `Start`, `RawRoad`, `TemplateMetadata`,
  `Constants`, `Provenance`, `ProfileId`, `OriginalStockParity`.
- Convenience aliases `DisplayMetresPerSourceUnit` (metres/PC XZ unit),
  `SimulationStepSeconds`, `SineQ14` (`ReadOnlyMemory<short>`),
  `StartSectionIndex`, `StandardBoostReserve`, `SuperBoostReserve`.

`SourceAmigaUnits` and `SourceAmigaClock` expose the PascalCase equivalents of
their JSON fields. `Lookups.SinQ14` is `ReadOnlyMemory<int>`;
`Lookups.CosineConversion` is `IReadOnlyList<int>` and `NumericTables` is a
read-only dictionary of read-only integer lists.

`RawRoad` is a read-only list of `SourceRoadSegment` records:
`Index,SectionIndex,SectionSegmentIndex,LeftStart,RightStart,LeftEnd,RightEnd,HeadingAngle`.
Vertices are `AmigaSourceVertex(long X,long Y,long Z)` in **native PC units**;
Y is the stored positive-up height **H before display division by four**.
These are the same seam-welded source vertices used to form `Track`, not a second
procedural road.

`SourceRoadContact` is
`HeightQ14,SlopeXQ14,SlopeZQ14,LateralQ14,WidthQ14,HeadingAngle,SectionIndex,SegmentIndex`.
Height is **native stored H × 16384**, not display-divided Y. Slopes use that
native height metric. Width/lateral are horizontal native-PC Q14. Queries use
the exact canonical triangle diagonal, Int128 barycentric arithmetic and
truncation toward zero, selecting the containing layer closest to
`referenceHeightQ14`; a body-centre reference is `rawPlayerY*16`.
This independently authored geometric sampler is not a claim of identical
reference curved/bilinear sampling or the stock integer road algorithm.

The agreed adapter equations are:

```text
wheelRoadHeight = ASHR_11(TZDIV(contact.HeightQ14, 2))
world X/Z       = rawPlayerX/Z / 16384 * displayScale
world player Y  = rawPlayerY / 4096 * displayScale
world road Y    = nativeStoredH / 4 * displayScale
```

Do not apply the old negative view-coordinate Y bridge to positive-up snapshots.
The new canonical projection retains fractional quarters; the old reference
vertex display used integer division by four, an explicit display precision
difference rather than a second contact road.

`Start` has `LayoutSectionIndex,SectionIndex,SegmentIndex,S,XQ14,ZQ14,HeightQ14,
InitialRawY,HeadingAngle`. It resolves restart eligibility backwards with wrap,
samples the selected cube centre, applies the extracted yaw/reversal/diagonal
metadata, then the supplied lane offset. Initial Y uses the source drop-height
configuration; an all-ineligible layout is rejected instead of looping forever.

The chosen real package uses the verified **configured** Linux reference target
50 callbacks/s and six callbacks/step: 0.12 s. The source gate initially runs
immediately; the deterministic Classic session explicitly normalizes startup to
its first full step interval. This is not a measured stock PAL cadence.

## Privacy and qualification

Output is restricted to new `artifacts/amiga-source/runtime/*.source-package.json`
files, with full-path containment and output/ancestor symlink checks. Everything
is parsed, converted and validated before `CreateNew`. No original numeric arrays
or output fixtures belong in tracked source/tests or static/browser/Docker assets.
Parent owns private serving, exclusions and explicit mode selection.

`originalStockParity` is always false. Source-remake data extraction and a running
Classic kernel are concrete functionality, not stock executable/state-trace
certification, a legal reuse grant, or completion of opponent/dynamic-DrawBridge
behavior. DrawBridge requests remain rejected. Those qualifications do not block
this independently authored optional reference mode.
