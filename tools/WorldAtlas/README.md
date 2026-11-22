# World Atlas data

World Atlas is an optional, offline scenery set. Put `base.zip` and any
compatible, lowercase `area-id.zip` files in one private scenery directory.
The same directory may contain `seattle.zip` for U.S. World Flight and Seattle
Scenic Drive; that is a [separate dataset](../WorldFlight/README.md) and not an
Atlas area. The host serves individual entries; neither you nor the browser
needs to unpack the archives. The packs are not navigation data.

## Build from licensed inputs

Run from the repository root with Python 3.11 or later:

```sh
mkdir -p .tools
TMPDIR="$PWD/.tools" python -m pip install --target .tools/world-atlas-deps \
  -r tools/WorldAtlas/requirements.txt
export PYTHONPATH="$PWD/.tools/world-atlas-deps"
export PYTHONDONTWRITEBYTECODE=1
```

If NumPy and Rasterio are already installed in `.tools/world-flight-deps`, you
can put both targets on `PYTHONPATH` and install only missing dependencies.

`build.py base` reuses `etopo60.tif`, `ne-land.geojson`, and `ne-lakes.geojson`
from `artifacts/world-flight-graphics/raw` by default. Its other inputs go in
`artifacts/world-atlas-inputs`. To start with one new cache instead:

```sh
RAW=artifacts/world-atlas-inputs
python tools/WorldAtlas/build.py fetch base --raw "$RAW" --extended-vectors
python tools/WorldAtlas/build.py base --raw "$RAW" --extended-vectors
python tools/WorldAtlas/build.py area western-americas --raw "$RAW"
```

Omit `--extended-vectors` on both commands if you don't want the global mountain
labels and railways. This does not change `base.bin`, so existing terrain packs
continue to match it. With the existing World Flight elevation/masks cache,
omit `--raw` on the build commands. The compiler never rewrites the legacy ZIP.

### Stock terrain and mapping

The global base has a periodic 16,384 m height/water grid, Natural Earth major
roads, places, airports and physical names, and worldwide departure presets.
The optional `world-osm.zip` adds a **worldwide road and place overview**
derived from OpenStreetMap via the Protomaps basemap. It is a separate L0
vector overlay; the base Natural Earth labels and regional L5 overlays remain
available. See [Building the overview](#building-the-overview).
The eight stock terrain bands provide **4,096 m (L1)** and **2,048 m (L2)**
sample spacing over their whole 45° span, including oceans and poles:

| Band | Longitude interval |
|---|---:|
| `north-pacific` | −180° to −135° |
| `western-americas` | −135° to −90° |
| `eastern-americas` | −90° to −45° |
| `western-atlantic` | −45° to 0° |
| `europe-africa` | 0° to 45° |
| `western-asia` | 45° to 90° |
| `eastern-asia` | 90° to 135° |
| `western-pacific` | 135° to 180° |

The following terrain pilots own exclusive L0 root cells carved out of the
bands. Each fills the entire carved footprint at L1/L2 and adds detail within
it. Mount the bands **and** the pilots for complete 2 km coverage. The base
still covers any gaps left by unmounted packs. Tiles and roots are listed
exactly in each ZIP's manifest and receipt.

| Terrain pilot | Fine terrain | Independent OSM feature box (W, S, E, N) |
|---|---|---|
| `puget-sound` | Seattle shore and I-90/Cascades; USGS 3DEP near Seattle if cached, Copernicus near Snoqualmie; 32 m Seattle core | −122.63, 47.37, −121.32, 47.80 |
| `panama-canal` | Canal and both coastal approaches; Copernicus N08/N09 W080, with a 32 m Panama City core | −80.16, 8.76, −79.34, 9.46 |
| `machu-picchu` | Sacred Valley and Cusco; Copernicus S14 W073/W072, with a 32 m Machu Picchu core | −73.03, −13.66, −71.78, −12.95 |
| `rio-de-janeiro` | Guanabara Bay and Sugarloaf; Copernicus S23 W044, with a 32 m Rio core | −43.83, −23.10, −43.04, −22.70 |

```sh
python tools/WorldAtlas/build.py list
python tools/WorldAtlas/build.py fetch machu-picchu  # public Copernicus DEM
python tools/WorldAtlas/build.py fetch peru         # Geofabrik OSM extract
python tools/WorldAtlas/build.py area machu-picchu
python tools/WorldAtlas/build.py area machu-picchu-osm
```

The other OSM targets are `washington`, `panama`, and `sudeste`; their matching
outputs are `puget-sound-osm.zip`, `panama-canal-osm.zip`, and
`rio-de-janeiro-osm.zip`. Run `fetch puget-sound`, `fetch panama-canal`, and
`fetch rio-de-janeiro` for their Copernicus tiles. Without the optional 3DEP
rasters and URL receipt in the reused World Flight cache, Seattle proper is
NOAA-based; it does not gain new elevation information just by increasing the
mesh resolution. All DEMs use Natural Earth for water classification. Ocean
heights are flattened to mean sea level, except along mixed-height LOD edges;
small islands/shorelines and inland water may be imperfect at this scale.

Eight additional profiles reproduce the staged GLO-30 refinements:
`alps-detail` (Mont Blanc/Matterhorn), `cape-town-detail` (Table Mountain),
`himalayas-detail` (Everest/Khumbu), `japan-kanto-detail` (Fuji/Tokyo),
`kilimanjaro-detail` (summit/Moshi), `nile-giza-detail` (Cairo/Giza),
`patagonia-detail` (Torres del Paine), and `sydney-detail` (harbour/coast).
They repeat byte-identical supporting tiles from the bands so they can be
mounted without rebuilding the bands. Run, for example,
`python tools/WorldAtlas/build.py fetch alps-detail` and
`python tools/WorldAtlas/build.py area alps-detail`.

The long-haul scenery profiles add 32 m mesh cores at London/Heathrow,
San Francisco Bay, Oahu and Reykjavík/Reykjanes. Independent OpenStreetMap
overlays provide mapped buildings, roads, parks and landmarks in those areas
and in Tokyo, which can use the existing `japan-kanto-detail.zip`. Hawaiʻi's
other islands and Iceland's south coast have wider but coarser coverage.

| Area | Terrain profile | Feature profile |
|---|---|---|
| London | `london-detail` | `london-osm` |
| San Francisco | `san-francisco-detail` | `san-francisco-osm` |
| Tokyo | existing `japan-kanto-detail` | `tokyo-osm` |
| Hawaiʻi | `hawaii-detail` | `hawaii-osm` |
| Iceland | `iceland-detail` | `iceland-osm` |

Build each missing profile with `build.py fetch <profile>` and
`build.py area <profile>`, using the same inputs and `base.zip` as the
installed Atlas. Copy the new ZIPs into `STUNT_WORLD_ATLAS` and restart the
host. City extracts come from BBBike and the island extracts use dated
Geofabrik snapshots; the archive receipts retain the exact source URLs and
hashes. These are additions, not replacements for the global bands or Seattle
archive.

### Custom areas and refreshing

Create terrain and OSM as independent ZIPs. Use exclusive L0 root rectangles
with half-open integer bounds in `[-76,-38,76,38]` to partition new coverage
exactly. `--terrain-box=W,S,E,N@LEVEL` instead snaps degrees outward to sibling
groups and supplies all ancestors, with optional repeatable finer boxes.
`--exclude-root-rect W,S,E,N` reserves complete roots for another pack. Terrain
packs may repeat *byte-identical* supporting tiles to add new refinements,
but conflicting common keys reject the optional pack. Keep every pack's own
complete sibling groups and parents. Avoid duplicate feature keys **from the
same source**. Separate Natural Earth and OSM can use the same L0 cells. Split
a box crossing the antimeridian at ±180°.

```sh
python tools/WorldAtlas/build.py area custom-band --root-rect=19,-38,38,38@2
python tools/WorldAtlas/build.py area local-mountains \
  --terrain-box=6.75,45.65,7.04,46.02@7 \
  --terrain-box=6.82,45.78,6.96,45.90@8
```

Mount these terrain examples with overlapping stock packs only if the checker
confirms their common tiles are byte-identical. For higher resolution, add
`--dem CREDIT path/to/elevation.tif https://source.example/...`,
where `CREDIT` is `usgs-3dep` or `copernicus-dem` and the URL is the actual
public provenance. Supply only licensed, single-band WGS84 geographic
north-up GeoTIFF elevations in metres. The compiler blends the local data
into NOAA from L3 upward, feathering its bounds and falling back outside them.

Use any regional `.osm.pbf` or local OSM XML you are licensed to redistribute:

```sh
python tools/WorldAtlas/build.py fetch osm --output artifacts/world-atlas-inputs/region.osm.pbf \
  --url https://download.geofabrik.de/south-america/peru/peru-latest.osm.pbf --refresh
python tools/WorldAtlas/build.py area local-osm \
  --osm artifacts/world-atlas-inputs/region.osm.pbf \
  --osm-url https://download.geofabrik.de/south-america/peru/peru-latest.osm.pbf \
  --feature-box=-73.03,-13.66,-71.78,-12.95
```

The downloader makes one bounded HTTPS request per file. There is no planet
download, map API fan-out, proprietary map input, or image/tile harvesting.
`--max-mib` raises the per-file limit for a larger extract and `--sha256`
optionally pins one download. A country/continent input is streamed with a
node-location index proportional to its input size. To keep a large index on
disk instead of in memory, pass an unused project-local path such as
`--osm-index-file .tools/world-atlas-nodes.bin`; the compiler removes it after
use. For a small window in a very large extract, prefer a regional download or
preclip locally with `osmium extract --strategy complete_ways -b W,S,E,N` to
retain road nodes.

L5 OSM chunks contain selected road/rail ways, named places, airports and
landmarks, simple building footprints, and forest/park/grass polygons. They
do not contain a routing graph, all tags, or relation/multipolygon coverage.
Closed buildings that also qualify as named landmarks keep both the
footprint and the point label. An OSM `height` value in metres (bare number
or `m`) or in feet (`ft` or explicit feet/inches) takes priority. If no valid
height is tagged, a positive whole `building:levels` count gives a coarse
estimate of 3 metres per level. Ambiguous or out-of-range values do not
produce a height; no height is invented for untagged buildings. A reported
OSM height is not necessarily a survey measurement.

Dense L5 chunks prioritize landmarks, airports, places, major roads and
railways. They reserve up to 1,024 rows / 8,192 vertices for buildings and
128 rows / 2,048 vertices for land use, within the same chunk byte cap.
The compiler samples those polygons round-robin from an 8×8 grid of roughly
1,024 projected metres per side. Within each subcell, buildings with reported
or estimated heights are ordered tallest first, then by name presence and
footprint size. Unused reservation goes back to other features. This keeps
representative buildings and land use across a busy city at the expense of some
local streets and paths; it is not full footprint coverage. L0 Natural
Earth and Protomaps selection is unchanged. The `receipt.json` records
candidate and budget-dropped rows by kind, retained classes, largest chunk,
source PBF snapshot time when available, and clip bounds. `buildingHeightRows`
counts retained footprint rows separately for `heightTag` and `levelsEstimate`.
Outside those regional L5 bounds, an installed `world-osm.zip` supplies
generalized L0 roads and names where it has data. Natural Earth remains the
fallback when the overview is absent or does not cover that category and cell.

`fetch --refresh` replaces a source only after a successful download. Keep
copies of the exact input bytes as long as you need reproducibility: `latest`
URLs change. Use the same inputs, compiler, dependencies, and build options;
the manifests/receipts identify the data actually used by SHA-256 and size.
Refresh only an OSM overlay when its source changes. If any global elevation
or water input changes, rebuild `base.zip` and build matching terrain packs
before deploying the replacement directory together.

### Rebuilding the additional city and regional packs

These use the generic builder, not new stock IDs. Reuse the exact cached
GeoTIFFs and PBF snapshots in the pack receipts for repeatable ZIPs. The
terrain commands below give both the required local TIFF paths and their
public download URLs. Output defaults to `artifacts/world-atlas/`; pass an
explicit `--output` with the same ZIP filename to stage elsewhere.

```sh
inputs=artifacts/world-atlas-inputs
dem_bucket=https://copernicus-dem-30m.s3.amazonaws.com
mx=Copernicus_DSM_COG_10_N19_00_W100_00_DEM
nyw=Copernicus_DSM_COG_10_N40_00_W075_00_DEM
nye=Copernicus_DSM_COG_10_N40_00_W074_00_DEM

python tools/WorldAtlas/build.py area mexico-city-detail \
  --name "Mexico City: Basin and City Core" \
  --terrain-box=-99.74,19.08,-99.02,19.73@7 --terrain-box=-99.38,19.22,-99.02,19.64@8 \
  --dem copernicus-dem "$inputs/$mx.tif" "$dem_bucket/$mx/$mx.tif"

python tools/WorldAtlas/build.py area new-york-city-detail \
  --name "New York City: Manhattan and Harbor" \
  --terrain-box=-74.60,40.46,-73.67,40.99@7 --terrain-box=-74.24,40.56,-73.79,40.94@8 \
  --dem copernicus-dem "$inputs/$nyw.tif" "$dem_bucket/$nyw/$nyw.tif" \
  --dem copernicus-dem "$inputs/$nye.tif" "$dem_bucket/$nye/$nye.tif"
```

Choose the corresponding invocation below for the separate ODbL overlay.
The fetch command reuses a cached extract if present. New York uses the
**New York State** extract, not a complete tri-state metro extract.

```sh
inputs=artifacts/world-atlas-inputs
geofabrik=https://download.geofabrik.de
atlas_osm() {
  python tools/WorldAtlas/build.py fetch osm --output "$2" --url "$3" &&
    python tools/WorldAtlas/build.py area "$1" --osm "$2" --osm-url "$3" \
      --feature-box="$4" --name "$5"
}

atlas_osm mexico-city-osm "$inputs/mexico.osm.pbf" \
  "$geofabrik/north-america/mexico-latest.osm.pbf" -99.40,19.20,-98.97,19.64 \
  "Mexico City: metropolitan core OpenStreetMap"
atlas_osm new-york-city-osm "$inputs/new-york.osm.pbf" \
  "$geofabrik/north-america/us/new-york/new-york-latest.osm.pbf" -74.24,40.52,-73.73,40.92 \
  "New York City: OpenStreetMap from NY extract"
atlas_osm khumbu-osm "$inputs/nepal.osm.pbf" \
  "$geofabrik/asia/nepal-latest.osm.pbf" 86.55,27.62,87.02,28.02 \
  "Nepal: Khumbu and Everest OpenStreetMap"
atlas_osm cape-town-osm "$inputs/south-africa.osm.pbf" \
  "$geofabrik/africa/south-africa-latest.osm.pbf" 18.25,-34.08,18.85,-33.65 \
  "South Africa: Cape Town OpenStreetMap"
```

### Building the overview

Use a bounded local vector extract from the official
[Protomaps downloads](https://docs.protomaps.com/basemaps/downloads). For the
November 20, 2026 build, the provider's `pmtiles` CLI can download only the
relevant zoom levels using HTTP ranges:

```sh
mkdir -p artifacts/world-atlas-inputs
pmtiles extract https://build.protomaps.com/20261120.pmtiles \
  artifacts/world-atlas-inputs/protomaps-20261120-z8.pmtiles --maxzoom=8
pmtiles verify artifacts/world-atlas-inputs/protomaps-20261120-z8.pmtiles
python tools/WorldAtlas/build.py overview --build-date 2026-11-20
```

The compiler only reads the **local** extract (limit 1 GiB, maximum zoom
exactly 8). It decodes Z8 tiles once, trims MVT buffers in source tile space,
owns shared east/south edges only in the neighboring tile, and clips the
projected vectors into Atlas L0 cells. It flushes latitude stripes as they
finish, instead of retaining the entire globe in memory. `--input` can point
at a staged extract elsewhere. New provider dates can be substituted if the
archive is still published; the local extract SHA-256 and OSM snapshot time,
not just the URL, identify the import. These archives are not fetched live
by the browser.

The overview is **generalized**: it contains the provided major-road
classes, any eligible rail ways, and selected named localities and POIs from
the Z8 layers. It excludes ferries, minor streets, addresses, buildings and
unselected features. Protomaps can combine OpenStreetMap and public-domain
Natural Earth places; a name is not proof of an individual OSM entity.
It covers the Web Mercator latitude range (approximately ±85.05°), with
source data and coverage varying by cell and category. Do not assume complete
rail, airport, or municipal coverage or survey-accurate centerlines.
The November 20 extract supplied no eligible Z8 rail or airport rows;
those still come from Natural Earth or regional overlays where available.
Each receipt lists actual emitted classes, hemisphere chunk counts and
excluded-for-budget rows. The ODbL overlay, its data notice, and the runtime
OpenStreetMap credit must be retained when redistributing.

### Validate and mount

```sh
python tools/WorldAtlas/check.py artifacts/world-atlas \
  --source-dir artifacts/world-flight-graphics/raw \
  --source-dir artifacts/world-atlas-inputs \
  --report artifacts/world-atlas/validation.json
python -m unittest discover -s tools/WorldAtlas -p 'test_*.py'
STUNT_WORLD_ATLAS="$PWD/artifacts/world-atlas" bash scripts/run.sh
```

`--source-dir` hashes original inputs when available. Omit it to check the
archives alone. `--report` optionally saves archive hashes and sizes, coverage,
density, and source receipts. For an individual area use `--base path/to/base.zip`.
The checker verifies exact archive contents, parent/sibling seams, periodic
base longitude and poles, local coordinates, feature density and compatible
mounted keys. The filename `seattle.zip` is reserved: the Atlas builder rejects
`area seattle`, and the Atlas checker skips that exact file in directory
scans. To validate Seattle data, use the
[World Flight checker](../WorldFlight/README.md#world-flight-data) on its
extracted package. Mount outside the public WASM bundle. The directory and
ZIPs must be readable by the host, including UID/GID `1000:1000` in the container.

```sh
WORLD_ATLAS_DIR="$PWD/artifacts/world-atlas" \
  docker compose -f compose.yaml -f deploy/compose.world-atlas.yaml up -d
```

Use an Atlas-capable image with optional building-height support before
mounting rebuilt height-enabled overlays. Older five-field packs still
work. Restart the host after changing the mounted pack set; it validates
on startup and serves by catalog revision. No original game data or
privately supplied maps belong in this directory.

## Format and credits

The ZIPs contain `manifest.json`, `receipt.json`, and `DATA-NOTICE.txt`.
Only `base.zip` contains `base.bin`: WFA1 `2432×1217`, int16 metres then uint8
water (0 dry, 1 sea, 2 inland), rows south to north, west to east without a
duplicate +180° column. WFT1 tiles at `terrain/L/x/z.bin` contain `33×33`
samples (same layout), with each aligned sibling quartet's perimeter sampled
from its direct parent using SW–NE height triangles and nearest water.
`features/L/x/z.json` contains clipped, integer-local-metre projected
geometry on the same grid. The row `[kind, style, name, source, points]`
has an optional sixth field only for L5 OSM buildings: height rounded to
integer metres in `1..1000`. When unavailable, the field is absent rather than
null. A pack cannot exceed 60,000 entries or 512 MiB;
each feature chunk is capped at 1 MiB, 4,096 rows, and 32,768 points.

- NOAA NCEI [ETOPO 2022 v1](https://www.ncei.noaa.gov/products/etopo-global-relief-model),
  60 arc-second Ice Surface. Topography is resampled; bathymetry is not modeled.
- Made with [Natural Earth](https://www.naturalearthdata.com/about/terms-of-use/),
  public domain. The feature sources are pinned to v5.1.2.
- [U.S. Geological Survey 3D Elevation Program](https://www.usgs.gov/3d-elevation-program),
  public-domain supplemental terrain if used, credited in its pack.
- Copernicus DEM GLO-30 Public License:
  produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.
  See the [collection and requirements](https://dataspace.copernicus.eu/explore-data/data-collections/copernicus-contributing-missions/collections-description/COP-DEM).
- **© [OpenStreetMap contributors](https://www.openstreetmap.org/copyright)**.
  OSM-derived overlays are independent database extracts under
  [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/); keep the machine-readable
  overlay files, archive notices, and player-facing attribution when redistributing.
