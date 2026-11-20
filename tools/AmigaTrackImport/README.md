# Private source-format track importer

Package-free .NET 10 native CLI and independently authored Core readers. This is
a strict reader for the mapped **804-byte source format** and a **new decoded
common-data interchange**, not a C++ initializer extractor, native packed-height
decoder, original physics implementation, rights clearance or parity receipt.
No native layouts, profiles, tables, assets or executable bodies are bundled.

Run from the repository root with the already available SDK:

```sh
bash scripts/dotnet.sh run --project tools/AmigaTrackImport -- inspect \
  --layout artifacts/private-input/layout.bin

bash scripts/dotnet.sh run --project tools/AmigaTrackImport -- convert \
  --layout artifacts/private-input/layout.bin \
  --common artifacts/private-input/common-data.json \
  --provenance artifacts/private-input/inputs.json \
  --display-metres-per-source-unit 0.01 --name "Private static candidate" \
  --out artifacts/amiga-compat/private-static.track.json

bash scripts/dotnet.sh run --project tools/AmigaTrackImport -- verify-trace \
  --expected artifacts/private-input/oracle.jsonl \
  --actual artifacts/private-input/candidate.jsonl
```

The example display scale is a caller choice, **not an established Amiga scale**.
There is no network access, source execution, evaluation or automatic extraction.
Use `--help` for the option list. Duplicate/unknown options are rejected.

Exit codes: **0** inspected/converted/trace matched; **2** malformed, unsupported,
invalid arguments, byte-count/hash mismatch or unsafe output; **3** trace mismatch;
**4** blocked by missing inputs/evidence. Diagnostics go to stderr. Success emits
one JSON object on stdout, not track bytes. Trace comparison emits its structured
match/mismatch/blocked result on stdout; malformed traces emit only stderr.

## Core API

Namespace `StuntRacer.Core.Compatibility.Tracks`:

```csharp
AmigaTrackLayout AmigaTrackLayoutReader.Read(ReadOnlySpan<byte> bytes);
AmigaCommonData AmigaCommonDataReader.Read(Stream utf8Json);
AmigaImportedTrack AmigaTrackConverter.Convert(
    AmigaTrackLayout layout, AmigaCommonData commonData, double displayMetresPerSourceUnit);
AmigaImportedTrack AmigaTrackConverter.Convert(
    AmigaTrackLayout layout, AmigaCommonData commonData,
    double displayMetresPerSourceUnit, string name);
```

Readers leave streams open. Malformed/unsupported data throws
`InvalidDataException`. A missing common model throws
`AmigaTrackBlockedException : IOException`. Bad API arguments throw argument
exceptions. No importer failure silently falls back to authored road.

`AmigaTrackLayout` exposes `SectionCount`, `StartSectionIndex`,
`StandardBoostReserve`, `SuperBoostReserve`, read-only `Sections` and a defensive
`SourceBytes` copy (`ReadOnlyMemory<byte>`). Each `AmigaTrackSection` exposes:
`Index`, `RawPosition`, `RawTemplateOrientation`, `RawLeftProfile`,
`RawRightProfile`, signed `LeftHeightShift`/`RightHeightShift`, `GridX`, `GridZ`,
`TemplateId`, `QuarterTurns`, `Reversed`, `LeftProfileId`, `RightProfileId`,
`LeftStorageFlag`, `RightColourFlag`.

`AmigaCommonData` exposes the schema fields below as read-only models:
`AmigaLayoutGeometry`, `AmigaCoordinateTransform`, `AmigaPieceTemplate` and
`AmigaHeightProfile`; profile descriptors are `AmigaProfileStorage` and
`AmigaHeightNormalization`. Templates/profiles are sorted by ID.

`AmigaImportedTrack` exposes `Name`, `Length`, `Width`, a defensive `Points`
(`TrackPoint[]`) copy, `Layout`, `CommonData`, `Profile`,
`DisplayMetresPerSourceUnit`, `StartSegmentIndex`, `StartDistance`,
`RestartEligibleSections`, and read-only geometry:

| Model/list | Exact members |
|---|---|
| `Edges`, `UnstitchedEdges`: `AmigaTrackEdge` | `SectionIndex`, `CrossSectionIndex`, `TemplateCrossSectionIndex`, `SegmentIndex`, `SourceLeft`, `SourceRight`, `Left`, `Right` |
| `AmigaSourceVertex` | signed int64 `X`, `Y`, `Z` |
| `AmigaWorldVertex` | double `X`, `Y`, `Z` |
| `Segments`: `AmigaTrackSegment` | `Index`, `SectionIndex`, `SectionSegmentIndex`, `StartEdgeIndex`, `EndEdgeIndex`, `StartS`, `EndS` |
| `Seams`: `AmigaTrackSeam` | `EndingSectionIndex`, `StartingSectionIndex`, `EndEdgeIndex`, `StartEdgeIndex`, `Adjusted` |
| `Triangles`: `AmigaRoadTriangle` | `SectionIndex`, `SectionSegmentIndex`, `SegmentIndex`, `TriangleIndex`, vertices `A`, `B`, `C` |

`TrySampleGeometricRoad(double x, double z, double referenceY,
out AmigaRoadContact contact)` uses these same triangles, selecting the containing
triangle closest vertically to `referenceY` (important at overpasses), with
canonical order breaking ties. It returns false off the strip.
`AmigaRoadContact` has `SectionIndex`, `SectionSegmentIndex`, `SegmentIndex`,
`TriangleIndex`, `Sample` (`RoadSample`) and `WeightA/B/C`. This is **geometric
triangle lookup, not the original integer section-relative road-height sampler**.
It does not supply vehicle contacts/dynamics.

## Layout facts and validation

The supplied investigation's `trackDecode.layout`/`decoderEvidence` documents
these source-format observations. They are not stock-executable parity evidence.

| Offset | Bytes | Meaning |
|---|---:|---|
| 0, 1 | 1 each | section count, start section |
| 2 | 100 | packed positions, X low nibble / Z high nibble |
| 102 | 100 | template low nibble, reverse bit 4, reserved bit 5, quarter turns bits 6–7 |
| 202, 302 | 100 each | left/right raw profile bytes |
| 402, 602 | 200 each | left/right signed **big-endian int16** shifts |
| 802, 803 | 1 each | standard/super boost reserve |

Exactly 804 bytes, count 1..100 and start below count are required. Active bit 5
is rejected, not discarded. Profile IDs are masked with 127; the raw bytes remain
separate. Left high-bit storage and right high-bit colour are never blanket-swapped
or interpreted as gaps. Inactive slots are retained in `SourceBytes`, not parsed
as active road. Missing common data permits inspection only. Conversion rejects
unknown referenced template/profile IDs and mismatched counts.

## Decoded common-data JSON v1: exact shape

All keys shown are **required**, including nullable `sourceRevision`. No extra,
duplicate, case-folded or missing keys are accepted at any object level.
Comments, trailing commas/content, fractional/exponent-form integer tokens,
nonfinite numbers and out-of-int64 integers are rejected.

```text
{
  "schemaVersion": 1,
  "sourceId": IDENTIFIER,
  "sourceRevision": IDENTIFIER | null,
  "circuitMode": "static",
  "encodingEvidenceId": IDENTIFIER,
  "layoutGeometry": {
    "tileSizeX": POSITIVE_INT64,
    "tileSizeZ": POSITIVE_INT64,
    "rotationPivotX": INT64,
    "rotationPivotZ": INT64,
    "positiveQuarterTurn": "x-to-negative-z" | "x-to-positive-z",
    "reversalHeightBinding": "travel-ordered" | "template-ordered",
    "seamPolicy": "require-equal" | "next-section-start",
    "evidenceId": IDENTIFIER
  },
  "coordinateTransform": {
    "xAxis": AXIS, "yAxis": AXIS, "zAxis": AXIS,
    "xDivisor": POSITIVE_INT64,
    "yDivisor": POSITIVE_INT64,
    "zDivisor": POSITIVE_INT64,
    "evidenceId": IDENTIFIER
  },
  "templates": [
    {
      "id": INTEGER_0_TO_15,
      "restartEligible": BOOLEAN,
      "crossSections": [
        {"leftX": INT64, "leftZ": INT64, "rightX": INT64, "rightZ": INT64}
      ]
    }
  ],
  "heightProfiles": [
    {
      "id": INTEGER_0_TO_127,
      "sampleCount": POSITIVE_INTEGER,
      "sourceStorage": {
        "kind": "normalized-integers" | "decoded-packed-byte" | "decoded-big-endian-word",
        "leftHighBit": BOOLEAN,
        "evidenceId": IDENTIFIER
      },
      "normalization": {
        "sampleMultiplier": POSITIVE_INT64,
        "sampleDivisor": POSITIVE_INT64,
        "shiftMultiplier": POSITIVE_INT64,
        "shiftDivisor": POSITIVE_INT64,
        "evidenceId": IDENTIFIER
      },
      "samples": [INT64]
    }
  ]
}
```

`IDENTIFIER` is 1..512 trimmed characters without control characters or an
absolute local/file-URI path. It identifies supplied evidence; it does not
authenticate that evidence or grant rights. `AXIS` is one of
`+x,-x,+y,-y,+z,-z`, using every absolute axis once. Only vertical-preserving
transforms (`yAxis` is `+y` or `-y`) are supported. Horizontal-to-vertical
projection, skew matrices and undeclared sign/precision defaults are rejected.

There must be 1..16 unique templates, each with **2..4096** cross-sections, and
1..128 unique profiles with **1..4096** samples. Every profile's `sampleCount`
must equal its array length and each referenced template's cross-section count.
These upper bounds and the 16 MiB common-JSON limit are implementation safety
limits, not claims about original template sizes.

### Exact interpretation

1. All template X/Z, tile dimensions, pivots, normalized heights and shifts share
   the declared integer source-coordinate space. There are no built-in Amiga
   tile/grid constants, implicit PC factors, pivot offsets or height divisors.
2. Before grid placement, subtract the declared pivot. For each raw quarter
   turn, `x-to-negative-z` maps `(x,z)` to `(z,-x)`; `x-to-positive-z` maps it to
   `(-z,x)`. Add the pivot, then `(gridX*tileSizeX, gridZ*tileSizeZ)`.
3. Reverse flag set: reverse template cross-section order **and swap its X/Z
   sides**. For `travel-ordered`, left/right profiles and shifts already describe
   the resulting layout traversal; use sample `i` without height-side swapping.
   For `template-ordered`, also reverse sample order and swap the selected
   profile/shift sides. This affects vertex assignment, never the stored raw flag
   bytes. No other reversal interpretation is silently inferred.
4. For each selected height profile,
   `Y = sample*sampleMultiplier/sampleDivisor + rawShift*shiftMultiplier/shiftDivisor`.
   Both terms must divide **exactly** with checked int64 products/sums; no
   truncation, packed-byte decoding or interpolation is invented. Original
   packed-height extraction/normalization remains externally required.
5. `sourceStorage.kind` describes how the supplier obtained decoded samples;
   the importer does **not** implement those native byte/word decoders.
   `leftHighBit` declares the expected raw left flag for that profile. Validate it
   against the original layout-left field **before** reversal. Right high-bit is
   independently retained colour metadata, not a storage selector.
6. `require-equal` requires every section's final left/right integer vertices to
   equal the next section's initial vertices, including last-to-first. For
   `next-section-start`, the previous final cross-section uses the next initial
   vertices exactly. `UnstitchedEdges` preserves all pre-adjustment vertices;
   `Seams.Adjusted` identifies replacements. There is no smoothing or procedural
   closure. Supply separate evidence before choosing this policy.
7. Project only after source geometry is complete:
   `worldX = signedSourceAxis(xAxis)/xDivisor * displayMetresPerSourceUnit`;
   analogously for Y/Z. Display scale must be finite and positive; it is separate
   from all source-axis signs/divisors and never feeds source integer state.

World axes remain +X right, +Y up, +Z forward. Triangles must have consistent
left/right ordering, nonzero XZ area and finite slopes/widths/lengths. Unsupported
mirrored/crossed/degenerate geometry is rejected rather than fixed by reversing
controls. Nonfinite coordinates, precision-collapsed segments, overflow and
fractional source normalization fail.

Each section keeps its final endpoint, even where it duplicates the next start.
An endpoint's `SegmentIndex` identifies its preceding segment. There is one
`TrackPoint` for each segment start, with `S` accumulated along the 3D midpoint
polyline, heading `atan2(forwardX,forwardZ)`, bank
`atan2(rightY-leftY,horizontalEdgeSeparation)` and full 3D edge width. Track
`Width` is the minimum supplied cross-section width; `Length` is this display
polyline length, **not original integer race distance**. No resampling to the
authored point count occurs. Exact edges, not midpoint/heading reconstruction,
are canonical.

Triangles use `[leftStart,rightStart,leftEnd]` and
`[rightStart,rightEnd,leftEnd]`. The static strip is present throughout;
colour markers do not create holes. Dynamic circuit modes, DrawBridge-labelled
requests and the investigation's known DrawBridge layout fingerprint are
rejected rather than frozen as an original circuit.

Tiny newly authored normalized profile example (not a complete circuit or
original data): samples `[-2,4]`, sample multiplier/divisor `3/2`, raw shift `-1`
and shift multiplier/divisor `2/1` produce integer heights `[-5,4]`.
That profile still needs explicit storage/evidence and a matching supplied
two-cross-section template; the importer supplies no road by default.

## Provenance JSON v1: exact input shape

```text
{
  "schemaVersion": 1,
  "assets": [
    {
      "role": ROLE,
      "byteLength": NONNEGATIVE_INT64,
      "sha256": LOWERCASE_64_HEX,
      "sourceId": IDENTIFIER,
      "sourceRevision": IDENTIFIER | null
    }
  ],
  "rights": {
    "status": "unresolved",
    "evidenceSha256": LOWERCASE_64_HEX | null
  }
}
```

Required unique roles: `layout`, `commonData`. Optional roles: `executable`,
`tables`, `expectedTrace`, `rightsEvidence`, or `tables:ID`, where `ID` is
1..73 ASCII letters/digits/dot/underscore/hyphen. Supply each optional file with
`--asset ROLE=FILE`; declared-but-unsupplied assets block conversion and undeclared
consumed assets fail. Up to 64 assets are accepted. Optional files are hashed,
never executed or treated as established original evidence.

Every full byte sequence is counted and SHA-256 verified before output.
`layout.byteLength` must be 804. Common asset `sourceId`/`sourceRevision` must
exactly match the decoded common JSON. Hashes are lowercase 64-digit hex;
duplicate roles, unknown/missing/duplicate object keys and incorrect hashes fail.
If present, `rights.evidenceSha256` must match the supplied `rightsEvidence`
asset; it remains **unresolved**. The importer refuses caller rights upgrades.

The provenance manifest itself is hashed separately in output
`provenance.manifest:{byteLength,sha256}`; it cannot recursively hash itself in its
own asset list. Input manifest limit: 1 MiB. Additional asset limit: 256 MiB/file,
stream-hashed without loading their contents into the model. Trace CLI inputs
are bounded at 64 MiB each and their complete hashes/counts are emitted.

## Deterministic output JSON v1

All property names use camel case. The output root has exactly:

```text
{
  "schemaVersion": 1,
  "track": {"name": STRING, "length": DOUBLE, "width": DOUBLE, "points": [TrackPoint]},
  "geometry": {
    "kind": "exact-source-edge-strip",
    "roadLookup": "geometric-triangles-not-original-integer-sampler",
    "startSegmentIndex": INTEGER,
    "startDistance": DOUBLE,
    "restartEligibleSections": [INTEGER],
    "edges": [AmigaTrackEdge],
    "unstitchedEdges": [AmigaTrackEdge],
    "segments": [AmigaTrackSegment],
    "seams": [AmigaTrackSeam],
    "triangles": [AmigaRoadTriangle]
  },
  "source": {"layout": LayoutInspection, "commonData": CommonDataV1},
  "provenance": {
    "schemaVersion": 1,
    "assets": [ValidatedAsset],
    "manifest": {"byteLength": INT64, "sha256": LOWERCASE_64_HEX},
    "rights": {"status": "unresolved", "evidenceSha256": LOWERCASE_64_HEX | null}
  },
  "compatibility": CompatibilityReport
}
```

`TrackPoint` has the existing `s,x,y,z,heading,bank,width,road` properties. Geometry
records have precisely the camel-case members in the API table; vertices are
`{x,y,z}`. `CommonDataV1` is the exact common schema above. `LayoutInspection` is
`{sectionCount,startSectionIndex,standardBoostReserve,superBoostReserve,sections}`.
Section members are the camel-case raw and decoded properties listed under Core
API. No raw file paths or wall-clock timestamps enter JSON.

`CompatibilityReport` has exactly `profileId:"amiga-experimental"`,
`qualification:"unqualified-candidate"`, `dynamics:"not-implemented"`,
`simulationStepSeconds:null`, `displayMetresPerSourceUnit`, `coordinateTransform`,
`encodingEvidenceId`, `layoutGeometry`, `rights` (same unresolved shape), and
`blockers` (string array). Inspect has no transform/scale/common data: its
`compatibility` contains only the four fixed qualification fields, `rights`,
and `blockers`; its other root fields are `schemaVersion:1,status:"inspected"`,
`byteLength,sha256,sectionCount,startSectionIndex,standardBoostReserve`,
`superBoostReserve,sections`.

Conversion stdout is a summary with `status:"converted"`, relative `output`,
`name,length,sectionCount,segmentCount,edgeCount,sha256` (output bytes), and the
compatibility report. Identical bytes/options produce identical file bytes; no
original-qualification boolean or source timestep is invented.

`verify-trace` delegates to `AmigaTraceVerifier.Verify(Stream,Stream)` and emits
`status,comparedTicks,firstMismatchTick,reason,qualification,oracleBasis`,
`firstMismatchField,expectedValue,actualValue,inputs`. `inputs.expected` and
`inputs.actual` each contain `byteLength,sha256`. The exact strict JSONL schema is
owned by Physics in
`src/StuntRacer.Core/Compatibility/Physics/README.md`. Synthetic matches are
synthetic-only, never an independent original oracle.

## Privacy and remaining gates

Output must be a **new** `.track.json` strictly beneath repository-root
`artifacts/amiga-compat/`; running conversion from another directory is rejected.
Full paths are checked, existing files refused, and symlinks/reparse points in
the output and all ancestors rejected. Validation/serialization finishes before
`CreateNew`; a failed write removes the newly created file. On Unix the tool
requests owner-read/write mode `0600`; some mounted/emulated filesystems do not
honor Unix creation modes. Do not treat this request as a verified filesystem
access-control boundary. There are no committed output fixtures.

Keep all original input bytes, decoded common data, generated geometry and traces
private under ignored `artifacts/`, outside browser/static content.
**Parent must retain both Git ignore and Docker build-context exclusions**;
this tool cannot enforce publishing policy or establish legal permission.

Still blocked: independently established native packed-height/storage/normalization
semantics; selected executable/version/region and original table identities;
original integer road sampler and dynamics/order; independent executable traces;
measured simulation cadence/overruns and displayed race-timer semantics; separately
reviewed code/data rights. User declarations, successful parsing, integer edge
retention or a rendered circuit do not clear those gates.

Parent owns additive `TrackDefinition.FromCompatibility`/exact-edge integration.
The existing constructor and authored defaults are unchanged here.
