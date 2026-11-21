# World Flight data

World Flight runs locally from a versioned dataset. The browser requests tile
coordinates from the game host, not a third-party tile service. It does not use
Google imagery, tiles, or models.

Generate and run it from the repository root (Python 3.11+):

```bash
python3 -m pip install --target .tools/world-flight-deps -r tools/WorldFlight/requirements.txt
python3 tools/WorldFlight/fetch_sources.py --output artifacts/world-flight/raw
PYTHONPATH=.tools/world-flight-deps python3 tools/WorldFlight/build.py \
  --input artifacts/world-flight/raw --output artifacts/world-flight/package
python3 tools/WorldFlight/check.py artifacts/world-flight/package
bash scripts/build.sh
BASE_URL=/poly-vector-racer \
  STUNT_WORLD_FLIGHT="$PWD/artifacts/world-flight/package/manifest.json" bash scripts/run.sh
```

The generator takes a new output directory and writes the manifest only after
the terrain and scenery databases are complete. Downloads use public endpoints;
reuse the local raw cache rather than repeatedly querying them. Plan on about
1.5 GiB for work files; the compiled package is roughly 160 MiB on disk, not a
download paid up front by the browser. `sources.json` in the raw cache records
the upstream URLs, and the package's `receipt.json` records exact input hashes.
Use `fetch_sources.py --only-osm --output artifacts/world-flight/raw` to fetch
map files independently, e.g. when upgrading an existing raw elevation cache.
Keep this package outside the static bundle. A container can optionally mount it
under `/app/data/world-flight` through
[`deploy/compose.world-flight.yaml`](../../deploy/compose.world-flight.yaml).
Set `WORLD_FLIGHT_DIR` to the absolute package directory, or put the package
under `${DOCKER_DATA}/games/poly-vector-racer/world-flight`.

## Package as a portable ZIP

Pack an existing compiled package (no source downloads or Python dependencies
beyond the standard library):

```bash
python3 tools/WorldFlight/pack.py \
  --input artifacts/world-flight/package \
  --output artifacts/world-flight/world-flight.zip
```

The packer checks the manifest, receipt, declared tiles and scenery files. It
keeps their bytes and revision unchanged; the ZIP also contains
[`DATA-NOTICE.txt`](DATA-NOTICE.txt) with source and license information. It
does not include undeclared files or raw data, follow links, or overwrite
existing files. Use the finished ZIP after the command reports its size and
SHA-256. To check geographic quality and tile seams before packaging, run
`check.py` on the compiled directory as shown above.

Copy just `world-flight.zip`. Set `STUNT_WORLD_FLIGHT` to its absolute path,
outside the static bundle, and start the host:

```bash
BASE_URL=/poly-vector-racer \
  STUNT_WORLD_FLIGHT=/absolute/path/world-flight.zip bash scripts/run.sh
```

The host indexes the archive when it starts and reads individual files on
demand without extracting them. Use the packer rather than zipping a parent
directory: archive entries use canonical file names at the archive root, with
no directory entries, duplicates or links. Restart the host to switch packages
instead of modifying a running host's archive. For a read-only Docker bind mount,
set `WORLD_FLIGHT_ZIP` to the file's absolute path and use
[`deploy/compose.world-flight-zip.yaml`](../../deploy/compose.world-flight-zip.yaml)
instead of the directory overlay. See the [deployment examples](../../README.md#docker-and-reverse-proxy-hosting).
The older `STUNT_WORLD_FLIGHT=/absolute/path/package/manifest.json` setting
continues to work, including packages without a road layer.

## What is modeled

- The lower 48, Alaska (including the western Aleutians), and Hawaii are three
  geographic regions with their own metre projections. The game streams between
  lower-48 destinations without a loading screen as you fly; the departure
  selector switches regions for Alaska and Hawaii. This is a game, not a
  navigational or flight-training product.
- The nationwide baseline uses a roughly 2 km gameplay grid. Western Washington
  has 128 m mountain terrain, and Seattle, Mount Rainier, San Francisco Bay,
  Yosemite and Grand Canyon have 32 m detail cores.
  Mountain and shoreline meshes use sampled elevations, not hand-placed peaks.
  This is stylized low-poly scenery, not satellite imagery or a global 3D city
  database. Boeing Field's main runway is the modeled landing strip.
- Seattle and San Francisco use real building outlines. Explicit `height` values take
  precedence over floor-count estimates; when both are missing, the builder
  records a fallback estimate. Landmark models such as the Space Needle,
  T-Mobile Park (with an open diamond beside Lumen Field), the three Amazon
  Spheres, Transamerica Pyramid and Golden Gate Bridge are newly authored.
- Seattle freeways and arterials cover the map window from 122.379°W to
  122.287°W and 47.566°N to 47.662°N, including portions of I-5, I-90, SR 99
  and their interchanges. Local streets are mapped in the smaller window
  122.365°W–122.304°W, 47.579°N–47.649°N, from SoDo through downtown,
  Queen Anne and South Lake Union to the Ship Canal neighborhoods. No other
  city gets a road layer. Motorway/trunk, primary/secondary/tertiary and
  residential/unclassified/living-street ways each have distinct visual
  widths and colors; service drives, paths, tunnels and covered ways are left
  out. Marked spans crossing the window edge are omitted rather than truncated
  over water. Bridge deck elevations are visual estimates, not measured clearances.
- Buildings and roads are visual scenery, not collision surfaces. Geographic
  ground and the runway use the same height data as flight
  contact. World Flight uses a supersonic variant of the Modern game model.
  Its Mach number is computed from its actual velocity and a simple atmosphere;
  it does not change the Amiga or Interceptor Bay physics.

## Data credits and licensing

- **USGS 3D Elevation Program (3DEP), The National Map.** Public-domain bare-earth
  elevation, projected and downsampled into game meshes. The Seattle preview
  also offsets/grades the terrain under the modeled runway for gameplay. See
  [USGS 3DEP](https://www.usgs.gov/3d-elevation-program/about-3dep-products-services).
- **NOAA National Centers for Environmental Information, ETOPO 2022 v1,
  60 arc-second surface model.** National base elevation, projected and
  downsampled. See [NOAA NCEI ETOPO](https://www.ncei.noaa.gov/products/etopo-global-relief-model).
- **Natural Earth**, public domain: 1:10m land and lakes, rasterized into water
  masks. The Seattle-area tidal surface and lakes also use USGS elevations.
  See [Natural Earth terms](https://www.naturalearthdata.com/about/terms-of-use/).
- **© OpenStreetMap contributors**, under the
  [Open Database License 1.0](https://www.openstreetmap.org/copyright).
  `seattle-buildings.json` and `san-francisco-buildings.json` are separately
  distributed modified databases of closed building ways, projected footprints,
  and height estimates, served unchanged under `world-flight/<revision>/`.
  `seattle-roads.json` is a separate modified database of OSM highway
  centerlines selected by road class, clipped, projected, and annotated with
  estimated bridge deck elevations. The public OSM map API bounding boxes
  are in `areas.py`; overloaded windows are subdivided when the map API
  reports its node limit. Keep the downloaded cache if a rate limit requires
  finishing later. Do not relicense these databases as game code.
  The scene and credits identify OpenStreetMap.
- Runway threshold coordinates are from the public-domain
  [OurAirports runway data](https://github.com/davidmegginson/ourairports-data);
  the strip dimensions/elevation were checked against King County's Boeing
  Field information. No provider endorses the game.

## Runtime format

`manifest.json` specifies the revision, regions, departures and half-open
coverage rectangles. Additional `patches`, grouped by level, cover disjoint
detail zones with complete quadtree parents. Each 3,283-byte `WFT1` tile contains a four-byte magic,
three little-endian 32-bit integers (level, x, z), a 33×33 array of little-endian
signed metre heights (west to east, then south to north), and 33×33 water
codes (0 land, 1 tidal/sea, 2 inland). Adjacent tiles share their outer samples.
The host exposes only declared coordinates. The renderer retains a coarse
parent until all four child meshes are ready, and limits concurrent requests,
mesh uploads, and the terrain working set. Missing collision tiles explicitly
hold the simulation; their heights are not substituted.

An optional lower-48 `roads` entry declares the Seattle layer, its projected
metre bounds, row count and point count. Its only route is
`world-flight/<revision>/seattle-roads.json`. Each road row contains its OSM
way ID, one of three road classes, `surface` or `bridge`, and a centerline
with points `[x,z]` or `[x,z,deckElevation]`. The host checks a declared
database at startup; older manifests with no entry still run without roads.
The browser fetches it only near Seattle and groups ribbons by displayed
64/32 m terrain tile. Ribbons follow the same visible triangles (including
both road edges); ground roads are suppressed on water faces, with actual
mapped bridges overriding that rule. Tile meshes are bounded and released
when no longer used or when leaving the scene. Large tiles are tessellated
over several frames instead of uploading the whole city at once. The layer
adds no collision or live map requests.
