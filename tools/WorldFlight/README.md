# World Flight data

World Flight runs locally from a versioned dataset. The browser requests tile
coordinates from the game host, not a third-party tile service. It does not use
Google imagery, tiles, or models.

Generate and run it from the repository root (Python 3.11+):

```bash
python3 -m pip install --target .tools/world-flight-deps -r tools/WorldFlight/requirements.txt
python3 tools/WorldFlight/fetch_sources.py --output artifacts/world-flight/raw
PYTHONPATH=.tools/world-flight-deps python3 tools/WorldFlight/build.py \
  --input artifacts/world-flight/raw --output artifacts/world-atlas/seattle
python3 tools/WorldFlight/check.py artifacts/world-atlas/seattle
bash scripts/build.sh
BASE_URL=/poly-vector-racer \
  STUNT_WORLD_ATLAS="$PWD/artifacts/world-atlas" bash scripts/run.sh
```

The generator takes a new output directory and writes the manifest only after
the terrain and scenery databases are complete. Downloads use public endpoints;
reuse the local raw cache rather than repeatedly querying them. Plan on about
1.5 GiB for work files; the compiled package is roughly 160 MiB on disk, not a
download paid up front by the browser. `sources.json` in the raw cache records
the upstream URLs, and the package's `receipt.json` records exact input hashes.
Use `fetch_sources.py --only-osm --output artifacts/world-flight/raw` to fetch
map files independently, e.g. when upgrading an existing raw elevation cache.
Keep this package outside the static bundle. The host looks for the extracted
package at `seattle/manifest.json` inside the configured scenery directory,
including packages without a road layer. A present `seattle.zip` takes
precedence; a damaged ZIP is rejected rather than falling back to extraction.

## Package as a portable ZIP

Pack an existing compiled package (no source downloads or Python dependencies
beyond the standard library):

```bash
python3 tools/WorldFlight/pack.py \
  --input artifacts/world-atlas/seattle \
  --output artifacts/world-atlas/seattle.zip
```

The packer checks the manifest, receipt, declared tiles and scenery files. It
keeps their bytes and revision unchanged; the ZIP also contains
[`DATA-NOTICE.txt`](DATA-NOTICE.txt) with source and license information. It
does not include undeclared files or raw data, follow links, or overwrite
existing files. Use the finished ZIP after the command reports its size and
SHA-256. To check geographic quality and tile seams before packaging, run
`check.py` on the compiled directory as shown above.

If you already have `world-flight.zip`, copy or rename it to exactly
`seattle.zip` in the scenery directory; there is no archive conversion. Set
`STUNT_WORLD_ATLAS` to that directory's absolute path and start the host:

```bash
BASE_URL=/poly-vector-racer \
  STUNT_WORLD_ATLAS=/absolute/path/scenery bash scripts/run.sh
```

The host indexes the archive when it starts and reads individual files on
demand without extracting them. Use the packer rather than zipping a parent
directory: archive entries use canonical file names at the archive root, with
no directory entries, duplicates or links. Restart the host to switch packages
instead of modifying a running host's archive. Atlas ZIPs can use the same
directory but are not required for the U.S. modes. For a Docker bind mount, set
`WORLD_ATLAS_DIR` to the host directory and use
[`deploy/compose.world-atlas.yaml`](../../deploy/compose.world-atlas.yaml).
See the [deployment and migration examples](../../README.md#docker-and-reverse-proxy-hosting).

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
  Optional labels point to nearby authored landmarks, to the modeled airport,
  and to selected catalog targets when their terrain is visible. This is not a
  geographic inventory.
- Seattle freeways and arterials cover the map window from 122.379°W to
  122.287°W and 47.566°N to 47.662°N, including portions of I-5, I-90, SR 99
  and their interchanges. Local streets are mapped in the smaller window
  122.365°W–122.304°W, 47.579°N–47.649°N, from SoDo through downtown,
  Queen Anne and South Lake Union to the Ship Canal neighborhoods. No other
  city gets a road layer. Motorway/trunk, primary/secondary/tertiary and
  residential/unclassified/living-street ways each have distinct visual
  widths and colors. Map Roads switches the same geometry to pale local
  streets, amber arterials and blue freeways; it adds no coverage. Service
  drives, paths, tunnels and covered ways are left out. Marked spans crossing
  the window edge are omitted rather than truncated over water. Bridge deck
  elevations are visual estimates, not measured clearances.
- Low-poly conifers grow in procedural clusters on nearby rendered 64 m and
  32 m terrain. They omit wet, steep and high terrain, the modeled runway and
  its vicinity, and central Seattle and San Francisco. Their positions do not
  come from tree surveys or land-cover maps, and they have no collision.
- The rendered building and road meshes are not collision surfaces. Geographic
  ground and the runway use the same height data as flight
  contact. World Flight uses a supersonic variant of the Modern game model.
  Its Mach number is computed from its actual velocity and a simple atmosphere;
  it does not change the Amiga or Interceptor Bay physics.
- The F-14, F-15 and SR-71 exteriors are newly authored low-poly meshes, not
  imports. They share the game's simplified first-person cockpit; the F/A-18
  retains its original model.

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
The browser fetches it only near Seattle and groups roads by displayed
64/32 m terrain tile. Flight retains its raised ribbons and palette switch
on the existing buffers. Seattle Scenic Drive uses separate near-ground
textured asphalt and exact-node corner paving, with stylized paint, curbs
and sidewalks on nearby detailed local and arterial streets. Markings and
sidewalks leave space at junctions and are omitted on wet facets and where
elevated roads or freeway geometry would conflict. This is game artwork
over the mapped centerlines, not surveyed lanes. Both views keep the mapped
bridge decks and follow the visible terrain triangles for surface roads.
Tile work is spread across frames and the compact meshes are released out of
range or on scene exit. The layer adds no collision or live map requests.
The ground view can share the validated road document with the renderer
through `WorldTerrain.loadRoads()`.

The scenery renderer batches trees into bounded, kilometre-scale patches
sampled against the currently drawn terrain triangles. Their horizontal
placement is stable across terrain detail changes; heights follow the visible
surface. Trees, roads, terrain and landmark anchors use the same local origin
as the player camera. `WorldScenery` lets flight and ground views share these
layers without sharing vehicle meshes or physics. Scenery uses 24-byte GPU
vertices instead of 40-byte vertices, keeping positions at full precision
while storing normals and colors in normalized integer attributes. Scene
diagnostics report total vertex-buffer bytes, not browser or driver memory.
