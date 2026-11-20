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
the terrain and footprints are complete. Downloads use public endpoints; reuse
the local raw cache rather than repeatedly querying them. The work files take
less than 1 GiB, and the compiled package is roughly 160 MiB on disk, not a
download paid up front by the browser. `receipt.json` records exact input hashes.
Keep this package outside the static bundle. A container can optionally mount it
under `/app/data/world-flight` through
[`deploy/compose.world-flight.yaml`](../../deploy/compose.world-flight.yaml).
Set `WORLD_FLIGHT_DIR` to the absolute package directory, or put the package
under `${DOCKER_DATA}/games/poly-vector-racer/world-flight`.

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
  Transamerica Pyramid and Golden Gate Bridge are newly authored. Buildings are visual scenery, not collision
  surfaces; geographic ground and the runway use the same height data as flight
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
  Do not relicense them as game code. The scene and credits identify OpenStreetMap.
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
