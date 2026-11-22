"""World Atlas v1 wire format and deterministic ZIP writers."""

import hashlib
import json
import math
import re
import struct
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

import numpy as np


GRID = "eqc-wft1-v1"
KIND = "world-atlas-pack"
SEATTLE_ARCHIVE = "seattle.zip"
WORLD_X, WORLD_Z = 19_922_944, 9_961_472
WIDTH, HEIGHT, BASE_STEP = 2432, 1217, 16384
SIDE, TILE_BYTES, BASE_BYTES = 33, 3283, 8_879_248
MAX_ENTRIES, MAX_BYTES = 60_000, 512 * 1024 ** 2
MAX_FEATURE_BYTES, MAX_ROWS, MAX_POINTS = 1024 ** 2, 4096, 32768
MAX_BUILDING_HEIGHT = 1000
BUILDING_HEIGHT_SOURCES = ("heightTag", "levelsEstimate")
SLUG = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
HEADER = struct.Struct("<4sIII")
TILE_HEADER = struct.Struct("<4siii")
PRESETS = [
    ("seattle", "Seattle • Puget Sound", -122.342, 47.609),
    ("cascades", "Snoqualmie Pass • I-90", -121.416, 47.421),
    ("san-francisco", "San Francisco • Golden Gate", -122.478, 37.808),
    ("grand-canyon", "Grand Canyon • South Rim", -112.118, 36.060),
    ("new-york", "New York • Manhattan", -73.981, 40.764),
    ("mexico-city", "Mexico City • Basin of Mexico", -99.147, 19.429),
    ("panama-canal", "Panama Canal • Pacific approach", -79.562, 8.992),
    ("machu-picchu", "Machu Picchu • Andes", -72.545, -13.163),
    ("rio-de-janeiro", "Rio de Janeiro • Guanabara Bay", -43.204, -22.951),
    ("patagonia", "Patagonia • Torres del Paine", -73.051, -50.942),
    ("alps", "Mont Blanc • Alps", 6.865, 45.832),
    ("cairo", "Cairo • Nile and Giza", 31.134, 29.979),
    ("cape-town", "Cape Town • Table Mountain", 18.414, -33.959),
    ("kilimanjaro", "Kilimanjaro • East Africa", 37.355, -3.067),
    ("everest", "Everest • Himalayas", 86.925, 27.988),
    ("tokyo", "Tokyo • Tokyo Bay", 139.763, 35.682),
    ("sydney", "Sydney • Port Jackson", 151.216, -33.857),
    ("milford-sound", "Milford Sound • New Zealand", 167.925, -44.672),
    ("antimeridian", "Antimeridian • Aleutians", 179.92, 51.962),
    ("north-polar", "High Arctic • North Pole approach", 0, 89.7),
    ("south-polar", "Antarctica • South Pole approach", 0, -89.7),
]
FEATURE_STYLES = {
    "road": ("motorway", "trunk", "primary", "secondary", "tertiary", "local", "service", "path"),
    "rail": ("rail", "light-rail", "tram"),
    "place": ("capital", "city", "town", "village", "hamlet"),
    "landmark": ("peak", "historic", "monument", "attraction", "viewpoint", "stadium", "natural"),
    "airport": ("airport", "airfield"),
    "building": ("building", "commercial", "industrial", "residential"),
    "landuse": ("forest", "park", "grass"),
}


def project(lon, lat):
    return lon * (WORLD_X / 180), lat * (WORLD_Z / 90)


def step(level):
    return 8192 >> level


def span(level):
    return step(level) * 32


def extent(level):
    return 76 << level, 38 << level


def wrap_cell(level, x):
    xlim, _ = extent(level)
    return (x + xlim) % (2 * xlim) - xlim


def round_height(values):
    return np.floor(values + .5).astype("<i2")


def interpolate(heights, water, x, z, *, periodic=False):
    """Sample cell coordinates on the SW–NE diagonal; nearest water ties NE."""
    x, z = np.asarray(x, dtype=np.float64), np.asarray(z, dtype=np.float64)
    h, w = heights.shape
    x = x % w if periodic else np.clip(x, 0, w - 1)
    z = np.clip(z, 0, h - 1)
    ix, iz = np.floor(x).astype(np.intp), np.floor(z).astype(np.intp)
    fx, fz = x - ix, z - iz
    jx, jz = (ix + 1) % w if periodic else np.minimum(ix + 1, w - 1), np.minimum(iz + 1, h - 1)
    sw, ne = heights[iz, ix], heights[jz, jx]
    se, nw = heights[iz, jx], heights[jz, ix]
    y = ((1 - np.maximum(fx, fz)) * sw + np.minimum(fx, fz) * ne
         + np.maximum(fx - fz, 0) * se + np.maximum(fz - fx, 0) * nw)
    return y, water[np.where(fz < .5, iz, jz), np.where(fx < .5, ix, jx)]


def terrain_bytes(level, x, z, heights, water):
    return TILE_HEADER.pack(b"WFT1", level, x, z) + heights.astype("<i2", copy=False).tobytes() + water.tobytes()


def base_bytes(heights, water):
    return HEADER.pack(b"WFA1", WIDTH, HEIGHT, BASE_STEP) + heights.astype("<i2", copy=False).tobytes() + water.tobytes()


def decode_base(data):
    if len(data) != BASE_BYTES or HEADER.unpack_from(data) != (b"WFA1", WIDTH, HEIGHT, BASE_STEP):
        raise ValueError("base.bin: invalid WFA1 header/size")
    return _decode(data, HEIGHT, WIDTH)


def decode_terrain(data, key):
    if len(data) != TILE_BYTES or TILE_HEADER.unpack_from(data) != (b"WFT1", *key):
        raise ValueError(f"{tile_path(*key)}: invalid WFT1 address/header/size")
    return _decode(data, SIDE, SIDE)


def _decode(data, height, width):
    n = height * width
    return (np.frombuffer(data, "<i2", n, 16).reshape(height, width),
            np.frombuffer(data, np.uint8, n, 16 + 2 * n).reshape(height, width))


def tile_path(level, x, z):
    return f"terrain/{level}/{x}/{z}.bin"


def feature_path(level, x, z):
    return f"features/{level}/{x}/{z}.json"


def valid_building_height(level, kind, source, height):
    return level == 5 and kind == "building" and source == "osm" and type(height) is int and 1 <= height <= MAX_BUILDING_HEIGHT


def json_bytes(data):
    return json.dumps(data, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def archive_manifest(pack_id, name, digest, terrain, features, presets, credits):
    return {"kind": KIND, "schemaVersion": 1, "grid": GRID, "id": pack_id, "name": name,
            "baseSha256": digest, "terrain": terrain, "features": features, "presets": presets,
            "credits": credits}


def base_presets():
    return [dict(id=i, name=n, lon=x, lat=z) for i, n, x, z in PRESETS]


class PackWriter:
    def __init__(self, path):
        self.path = Path(path)
        self.part = self.path.with_suffix(".zip.part")
        self.entries = set()
        self.size = 0

    def __enter__(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.archive = ZipFile(self.part, "x", compression=ZIP_DEFLATED, allowZip64=False)
        return self

    def add(self, name, data):
        if name in self.entries or len(self.entries) == MAX_ENTRIES or self.size + len(data) > MAX_BYTES:
            raise ValueError(f"{self.path}: duplicate path or pack budget exceeded at {name}; split bounds")
        self.entries.add(name)
        self.size += len(data)
        entry = ZipInfo(name, (1980, 1, 1, 0, 0, 0))
        entry.create_system = 3
        entry.external_attr = 0o100644 << 16
        self.archive.writestr(entry, data, compress_type=ZIP_DEFLATED, compresslevel=5)

    def __exit__(self, exc_type, exc, traceback):
        try:
            self.archive.close()
            if exc_type is None:
                if self.part.stat().st_size > MAX_BYTES:
                    raise ValueError(f"{self.path}: compressed pack budget exceeded; split bounds")
                self.part.replace(self.path)
        finally:
            self.part.unlink(missing_ok=True)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def bounds_valid(bounds):
    w, s, e, n = bounds
    return all(map(math.isfinite, bounds)) and -180 <= w < e <= 180 and -90 <= s < n <= 90
