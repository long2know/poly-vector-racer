# Original F/A-18 runtime data

Independent C# implementation of the documented OFS/HUNK/data formats. It does not execute
68000 code and does not depend on Browser, authored Flight, FlightReference, NuGet packages,
an emulator, or a reference checkout at runtime. No original data is distributed here.

## Public boundary

```csharp
using StuntRacer.FlightData;

FlightDiskPackage data = FlightDiskReader.Read(userChosenBytes);
// Optional plain source filename (never a filesystem path):
data = FlightDiskReader.Read(userChosenBytes, new FlightReadOptions("user-disk.zip"));
byte[] externalPackage = FlightPackageSerializer.WritePackage(data);
FlightDiskPackage checkedData = FlightPackageSerializer.ReadPackage(externalPackage);
```

`Read` accepts a raw 901120-byte DOS\0 OFS disk, a bounded ZIP containing exactly one such
ADF, or an unpacked HUNK executable. The game is selected structurally, not by its filename.
The supplied modified disk's renamed `x1` is supported; its separate `unit` intro is not
mistaken for the game. All reachable directories/files are checksum/pointer/sequence
validated, including file extension blocks and bitmap allocation. Free sectors are not
interpreted. ZIP traversal names, encryption, multi-member/multi-volume/ZIP64 archives,
oversized inflation and contradictory local/central declarations fail closed.

This version intentionally accepts only the verified executable/data profile:

* executable SHA256 `38b6e1f331460aa4939690173d95c38b105d1d089226359a4c8655052ea29ab5`
* flat image SHA256 `40388db2b579abf89048a844b5502b79e8818c26b9253eba83705529f32a35a2`
* 185 hunks, 298988 image bytes, 8441 relocations.

Every required flight/contact blob has a whole-table SHA256 pin; the pads also check the
published FNV fingerprints. No generated trigonometry, frozen tables, padding of missing
data, or authored-world fallback can turn an incompatible source into success.
`CleanExecutableIdentityVerified` remains false: the available evidence verifies this
supplied baseline, not equality to an unavailable pristine publisher disk.

Input is capped at 2 MiB; JSON packages at 16 MiB. The library performs no file I/O.
Malformed data throws `FlightDataException` with a stable category in `Code`.

## Images and reference-machine provenance

All `FlightImage` addresses are absolute **flat** addresses, base `0x10000`; byte-array
index zero is address `0x10000`. Reads are big endian and bounds checked.

* `FlatImage`: every original relocation targets the concatenated flat hunk address.
* `ReferenceImage`: every relocation VALUE uses the captured RAM base of its **target**
  hunk, retaining flat-address indexing. Do not dereference these values as flat pointers.
* `ReferenceWindowImage`: flat pointers everywhere except the two numeric lookup spans,
  where the 3 + 337 reference relocation values are applied. This matches the upstream
  nonexecuting extractor and is the preferred `FlightOriginalData.FromImages` second input.

`ReferenceSegments` and `Relocations` retain hunk indices, addends, flat/reference values
and provenance. `reference-segments.tsv` contains factual address/size metadata only,
from reference commit `2475dcd65f454a37b41a2be27629ad958f34ddc5`; it is one captured
Kickstart 1.2 loader layout, **not** a universal stock-ROM placement. All 185 flat starts,
sizes and types are checked before using it. In particular it is not the unrelated
`0x20000` plus LoadSeg-header layout.

Numeric windows are `[0x3249A,0x325B8)` and `[0x3AE92,0x49A12)`. Five runtime text bytes
`0x440C9..0x440CC` and `0x44EA1`, and runtime display/variable memory
`[0x49A12,0x4AE92)`, are **not** reproducible static data. Runtime consumers must use live
state or explicitly reject/count unresolved reads; a zero-filled file region is not a
snapshot of live memory. These limitations are also included in every package/receipt.

`Tables` keys omit `.bin`; original word values are stored as `i16le` for the upstream
table contract, spans/ship structures as `raw-be`. `ToInt16Array()` returns a copy.
There are twelve original extractor-compatible blobs, plus five exact source tables:
pitch scale, sine (901 words), and inverse sine (259/552/256 words).

## Original world and contact API

`Scene` exposes:

* sparse terrain tiers A/B/C (36/8/158 populated cells), 36 shared item lists;
* the original 32x32 water grid, 103 unique regions (38 no-water), 103 water polygons
  with 676 vertices; **land is the y=0 complement**, not a fabricated bay rectangle;
* four airfield pad entries and ten original quad footprints, five start blocks and
  59 source position blocks;
* original model records, normal/type-B levels, raw source vertex slots, original BSP
  nodes, source command words and materializable static polygon references;
* ship contact lists/structures and thirteen valid static landmark footprint records.

For the kernel:

```csharp
var hornet = data.Scene.Models.Single(m => m.Id == 4).Levels[0];
var carrier = data.Scene.Models.Single(m => m.Id == 8).Levels[0];
// hornet.Vertices.Take(4): exact unrotated original contact slots.
// carrier.Vertices.Take(58): exact unrotated original ship contact slots.
```

The near Hornet block is `0x39C04`, with 21 initial vertices; the near carrier block is
`0x3E95E`, with 69 initial vertices. `Vertices` also includes original extra-vertex ranges,
in slot order; `InitialVertexCount` distinguishes these from the initial b8 slots.
`ScaleLowNibble` is 3 for both. `ModelToPositionShift` is 5 (left shift: 32 position
units/model unit). **Do not pass 5 as the collision's right-shift L**: the runtime must
supply the actual previous-draw record `+0x7D` low nibble (near-player value 3), part id,
rotated contact vertices and matrix/contact policy.

### Terrain positions

`FlightScenePlacements.Place(tier, cell, item)` / `.Enumerate(scene)` implement:

```text
s = tier.PositionShift                 # 22 for C, 24 for A/B
subX = 3 - (item.Layer & 3)
subZ = 3 - (item.Layer >> 2)
x = (cell.X << s) + (subX << (s-2)) + (item.FineX << (s-14))
z = (cell.Z << s) + (subZ << (s-2)) + (item.FineZ << (s-14))
y = 0
```

Thus C's fine offsets use `<<8`, A/B's `<<10`; the sub-cell corner must not be omitted.
`ModelId` already includes the original item-id +16 conversion. `RuntimeModelSlot`
identifies source slots 16..32 that need a real spawner binding, not a made-up model.
Static ground items have base y=0; height lives in their model vertices. Position-block
fine coordinates and heights are instead in units of 256 position units. Water-polygon
coordinates use `(cell << 24) + (vertex << 12)`, at y=0. Pad quads use the reference R
from their eight source entry words and direction table; quad coordinates are `<<8`.

### Landmark boxes / planes

`Scene.ModelCollisions` supplies `ModelId`, original `FootprintAddress`, captured RAM
address, `ReferenceLandmarkZBias`, outer box, nodes and all point/normal planes. The
five box shorts are `(maximumHeight, minX, maxX, minZ, maxZ)` in collision fine units
(256 position units). Source mapping:

1. `M = BE32(0x25ECC + 20*modelId + 4)`; select its first detail leaf;
2. `block = M + (leaf & 0xFFF)`; `F = M + (BE16(block) & 0xFFF)`;
3. outer box at F, first node at F+10;
4. node box at +0, next long at +10 (negative terminates), plane-count word at +14;
5. from +16, `max(count,1)` 12-byte planes `(refX,refY,refZ,nX,nY,nZ)`.

Counts are not silently normalized: a node with count 0 retains its original first plane,
which the original evaluates before checking the count. At most four nodes/eight planes
and a 256-byte translated slot are supported, checked against this source's inventory.
All thirteen static footprint boxes/planes compare exactly with the independent local
upstream `lists_land.bin`. Its loose recognizer also reports effect model 34 at an odd
address with inverted bounds; that effect is never a static terrain placement and is
explicitly not promoted into a fabricated collision volume.

`ReorderAuxiliaryAddress` is a **different** structure, present for static models
58/86/104: count word c at descriptor auxiliary +0, plane pointer at +6, and c+1 reference
planes. It is not the footprint pointer.

To form a live collision slot, add source instance fine offsets to box X/Z bounds and
plane reference X/Z, preserving the original 16-bit wrapping. The builder's offset is
`(queuePositionWord << queueLod) & 0x3FFF`; cell bytes come from the queue key. For an
exact, unshifted C placement these are `FineXInCCell` / `FineZInCCell`. Keep original
reverse-generated/current queue ordering, eleven-slot limit, rebuild timing and stale-slot
rules in the runtime; ingestion does not pretend a static nearest-eleven selection is the
original display-list builder. Captured reference addresses give z bias 48; the flat-image
rig's bias is different and must not silently replace it.

### Drawing limitations

This is not a complete original renderer. Source vertices/BSP/operation words are exact.
`GeometryStatus`, per-level `Limitations`, and per-operation `Status` explicitly flag
unmaterialized hatching, strips, relative groups, animated synthesis/selectors and effect
programs. A polygon needing such slots is not emitted as invented geometry. The alternate
cockpit detail-list address is retained but its extra animated levels are not materialized.
Consumers must not label these partial static meshes complete, nor replace them with an
authored rectangle/aircraft and call that original.

## External package / tool

Run from the repository root:

```sh
dotnet run --project tools/InterceptorReferenceExtract -- extract \
  --input /absolute/path/to/user-disk.zip \
  --output artifacts/flight-source/ingestion/user-disk

dotnet run --project tools/InterceptorReferenceExtract -- verify-package \
  --input artifacts/flight-source/ingestion/user-disk/package.json
```

`inspect --input ...` prints metadata only. `--oracle LOCAL_BASELINE_DIR` compares all
twelve original blobs plus flat/reference-window images against an existing local oracle;
it never downloads or executes source code. Existing outputs require explicit `--overwrite`.
Extraction stages all files and re-reads the package before installing the final output
directory. Output is restricted to children of ignored `artifacts/flight-source/ingestion/`;
no source/archive entry determines a filesystem path. Symlink/reparse output paths are
rejected. Filesystem permission hardening is requested but depends on the underlying mount.

Outputs: `package.json`, metadata-only `receipt.json`, `scene.json`, flat/full-reference/
reference-window `.bin` buffers and the seventeen `tables/*.bin` files.
The JSON header is `schemaVersion:1`, `kind:"fa18-amiga-runtime"`. It carries actual source,
image and table bytes; source identities, layout, pins, relocations, reference metadata,
scene and qualifications are explicit. `ReadPackage` re-ingests the original input and
checks **every** declared field/data array against it, rejecting duplicate/unknown fields.
No partial/corrupt package becomes a valid runtime manifest.

Keep all these outputs external/ignored. Do not commit, upload, publish to wwwroot, copy
into a Docker image, or auto-download original data. The parent Host/runtime owns any
opt-in external package configuration and browser adapter.

## Native checks

```sh
dotnet run --project tests/StuntRacer.FlightDataChecks
dotnet run --project tests/StuntRacer.FlightDataChecks -- \
  --source /absolute/path/to/user-disk.zip \
  --package artifacts/flight-source/ingestion/user-disk/package.json \
  --oracle /local/quick-baseline \
  --complete-oracle /local/complete-baseline
```

Default fixtures are invented. Opt-in checks parse the actual whole source, compare actual
arrays/planes, and prove realistic source/table/layout/reference-map/scene mutations fail.
No original source bodies or data arrays are included as test fixtures.
