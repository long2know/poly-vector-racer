"""Import a local, zoom-8 Protomaps PMTiles cutout as Atlas overview vectors."""

import gzip
import math
from collections import Counter
from functools import lru_cache
from io import BytesIO
from mmap import ACCESS_READ, mmap

from google.protobuf.message import DecodeError
from mapbox_vector_tile.Mapbox.vector_tile_pb2 import tile as VectorTile
from pmtiles.reader import Reader
from pmtiles.tile import Compression, TileType, deserialize_directory, find_tile, zxy_to_tileid

from atlas import WORLD_X, WORLD_Z
from features import Features, clip_segment, feature_name

ZOOM = 8
SIDE = 1 << ZOOM
MAX_ARCHIVE = 1024 ** 3
MAX_TILE = 16 * 1024 ** 2
LAYERS = frozenset(("roads", "places", "pois"))
TAGS = frozenset(("kind", "kind_detail", "name", "name:en", "ref", "shield_text", "capital", "iata"))
ROAD_STYLES = frozenset(("motorway", "trunk", "primary", "secondary", "tertiary"))
PLACE_STYLES = frozenset(("city", "town", "village", "hamlet"))
POI_STYLES = {
    "national_park": "natural", "nature_reserve": "natural", "protected_area": "natural",
    "park": "natural", "forest": "natural", "peak": "peak", "volcano": "peak",
    "viewpoint": "viewpoint", "stadium": "stadium", "memorial": "monument", "monument": "monument",
    "landmark": "historic", "historic": "historic", "attraction": "attraction", "museum": "attraction",
    "university": "attraction",
}


class ZoomTiles:
    def __init__(self, data):
        self.data = data
        self.reader = Reader(self.read)
        self.header = self.reader.header()
        h = self.header
        if h["max_zoom"] != ZOOM or h["tile_type"] != TileType.MVT or \
                h["internal_compression"] != Compression.GZIP or h["tile_compression"] not in (Compression.GZIP, Compression.NONE):
            raise ValueError("Expected a Protomaps PMTiles v3 vector extract capped at exactly zoom 8.")

    def read(self, offset, length):
        if not 0 <= offset <= offset + length <= len(self.data) or length > MAX_TILE:
            raise ValueError("Invalid PMTiles directory, metadata or tile byte range.")
        return self.data[offset:offset + length]

    @lru_cache(maxsize=32)
    def directory(self, offset, length):
        return deserialize_directory(self.read(offset, length))

    def get(self, x, y):
        h = self.header
        offset, length = h["root_offset"], h["root_length"]
        key = zxy_to_tileid(ZOOM, x, y)
        for _ in range(4):
            entry = find_tile(self.directory(offset, length), key)
            if entry is None:
                return None
            if entry.run_length:
                contents = self.read(h["tile_data_offset"] + entry.offset, entry.length)
                if h["tile_compression"] == Compression.GZIP:
                    with gzip.GzipFile(fileobj=BytesIO(contents)) as stream:
                        contents = stream.read(MAX_TILE + 1)
                if len(contents) > MAX_TILE:
                    raise ValueError(f"Expanded MVT {ZOOM}/{x}/{y} exceeds decode budget.")
                return contents
            offset, length = h["leaf_directory_offset"] + entry.offset, entry.length
        raise ValueError("Too many nested PMTiles directories.")


def geometry(commands, expected_type):
    """Decode MVT's delta-coded Point/LineString commands, preserving parts."""
    current, paths = [], []
    x = y = i = 0
    while i < len(commands):
        head = commands[i]
        i += 1
        command, count = head & 7, head >> 3
        end = i + 2 * count
        if command not in (1, 2) or not count or end > len(commands) or (
                command == 2 and (expected_type != 2 or not current)):
            raise ValueError("Invalid MVT point/line geometry.")
        if command == 1 and expected_type == 2 and count != 1:
            raise ValueError("Invalid MVT line start.")
        for i in range(i, end, 2):
            dx, dy = commands[i], commands[i + 1]
            x += (dx >> 1) ^ -(dx & 1)
            y += (dy >> 1) ^ -(dy & 1)
            if command == 1:
                if current:
                    paths.append(current)
                current = []
            current.append((x, y))
        i = end
    if current:
        paths.append(current)
    return paths


def unbuffer(points, size):
    """Clip to the source tile. Its east and south edges belong to its neighbors."""
    rect = (0, 0, size, size)
    current = []
    for a, b in zip(points, points[1:]):
        clipped = clip_segment(a, b, rect) if a != b else None
        if clipped:
            p, q = clipped
            if p[0] != size or q[0] != size:
                if p[1] != size or q[1] != size:
                    if not current or current[-1] != p:
                        if current:
                            yield current
                        current = [p]
                    current.append(q)
                    continue
        if current:
            yield current
            current = []
    if current:
        yield current


def projected_z(y):
    return 2 * WORLD_Z / math.pi * math.atan(math.sinh(math.pi * (1 - 2 * y / SIDE)))


def project_tile(x, y, size, p):
    u, v = p
    return WORLD_X * (2 * (x + u / size) / SIDE - 1), projected_z(y + v / size)


class OverviewRows(Features):
    def row(self, x, z, kind, style, name, points):
        if kind in ("road", "rail") and points[0] > points[-1]:
            points = points[::-1]
        super().row(x, z, kind, style, name, points)


def classify(layer, tags, geom_type):
    kind, detail = tags.get("kind"), tags.get("kind_detail", "")
    if layer == "roads" and geom_type == 2:
        if kind == "rail":
            if detail in ("abandoned", "disused", "construction"):
                return None
            return "rail", {"light_rail": "light-rail", "tram": "tram"}.get(detail, "rail")
        style = detail.removesuffix("_link")
        if kind in ("highway", "major_road", "minor_road") and style in ROAD_STYLES:
            return "road", style
    elif geom_type == 1:
        if layer == "places" and kind == "locality" and detail in PLACE_STYLES:
            return "place", "capital" if tags.get("capital") in ("yes", "2") else detail
        if layer == "pois":
            if kind in ("aerodrome", "airport", "airfield"):
                return "airport", "airport" if tags.get("iata") else "airfield"
            if kind in POI_STYLES:
                return "landmark", POI_STYLES[kind]
    return None


def collect_tile(out, raw, x, y, seen, selected):
    try:
        tile = VectorTile.FromString(raw)
    except DecodeError as error:
        raise ValueError(f"Invalid MVT payload {ZOOM}/{x}/{y}.") from error
    for layer in tile.layers:
        if layer.name not in LAYERS:
            continue
        size = layer.extent
        if layer.version != 2 or not 0 < size <= 65536:
            raise ValueError(f"Unsupported MVT layer distribution in {ZOOM}/{x}/{y}/{layer.name}.")
        keys = [key if key in TAGS else None for key in layer.keys]
        values = [value.string_value for value in layer.values]
        seen[layer.name] += len(layer.features)
        for feature in layer.features:
            tags = {keys[i]: values[j] for i, j in zip(feature.tags[::2], feature.tags[1::2]) if keys[i]}
            if not (style := classify(layer.name, tags, feature.type)):
                continue
            name = feature_name(tags.get("name:en"), tags.get("name"), tags.get("ref"), tags.get("shield_text"))
            if feature.type == 1 and not name:
                continue
            selected[layer.name] += 1
            for part in geometry(list(feature.geometry), feature.type):
                if feature.type == 1:
                    u, v = part[0]
                    if 0 <= u < size and 0 <= v < size:
                        out.point(*style, name, project_tile(x, y, size, part[0]))
                else:
                    for clipped in unbuffer(part, size):
                        out.line(*style, name, [project_tile(x, y, size, point) for point in clipped])


def write_overview(path, writer):
    if not 0 < path.stat().st_size <= MAX_ARCHIVE:
        raise ValueError("Expected a local overview extract no larger than 1 GiB, not a full planet file.")
    features = OverviewRows(0, "osm")
    data_seen, data_selected, totals, styles, largest, hemisphere = (Counter() for _ in range(6))
    keys = []
    input_count = buffered_peak = 0

    def emit(future_north):
        finished = Features(0, "osm")
        finished.rows = {key: features.rows.pop(key) for key in list(features.rows)
                         if key[1] * features.size > future_north}
        ready, stats = finished.write(writer)
        keys.extend(ready)
        hemisphere.update("north" if z >= 0 else "south" for _, _, z in ready)
        totals.update({key: stats[key] for key in ("tiles", "candidateRows", "rows", "points", "droppedForTileBudget")})
        styles.update(stats["classes"])
        for key, value in stats["largestTile"].items():
            largest[key] = max(largest[key], value)

    with path.open("rb") as f, mmap(f.fileno(), 0, access=ACCESS_READ) as mapping:
        source = ZoomTiles(mapping)
        metadata = source.reader.metadata()
        h = source.header
        for y in range(SIDE):
            for x in range(SIDE):
                raw = source.get(x, y)
                if raw is not None:
                    input_count += 1
                    collect_tile(features, raw, x, y, data_seen, data_selected)
            buffered_peak = max(buffered_peak, len(features.rows))
            emit(projected_z(y + 1))
            if (y + 1) % 32 == 0:
                print(f"  MVT scan: {y + 1}/{SIDE} rows, {input_count:,} zoom-8 tiles", flush=True)
        emit(-math.inf)
    if not keys or not any(k.startswith("road/") for k in styles):
        raise ValueError("No supported roads found in the staged map.")
    stats = dict(totals) | {"classes": dict(sorted(styles.items())), "largestTile": dict(largest),
                            "chunksByHemisphere": dict(hemisphere), "peakBufferedAtlasCells": buffered_peak}
    provenance = {"inputZoom": ZOOM, "addressedInputTiles": input_count, "tileGrid": "Web Mercator",
                  "sourceBoundsDegrees": [h[f"{key}_e7"] / 1e7 for key in ("min_lon", "min_lat", "max_lon", "max_lat")],
                  "providerVersion": metadata.get("version"),
                  "replicationTimestamp": metadata.get("planetiler:osm:osmosisreplicationtime"),
                  "replicationSequence": metadata.get("planetiler:osm:osmosisreplicationseq"),
                  "mvtFeaturesByLayer": dict(sorted(data_seen.items())),
                  "selectedMvtFeaturesByLayer": dict(sorted(data_selected.items())),
                  "method": "one Z8 MVT decode per addressed tile; unbuffer and half-open edges; "
                            "project and clip to L0; flush stripes, exact-row deduplication and Atlas density caps"}
    return sorted(keys), stats, provenance
