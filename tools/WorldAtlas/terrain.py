"""Sample the licensed DEM/water inputs and emit nested, edge-locked terrain."""

import json
import math
from collections import defaultdict
from contextlib import ExitStack

import numpy as np
import rasterio
from rasterio.features import rasterize
from rasterio.transform import Affine
from rasterio.windows import Window

from atlas import (BASE_STEP, HEIGHT, MAX_ENTRIES, WIDTH, WORLD_X, WORLD_Z, base_bytes,
                   extent, interpolate, project, round_height, span, step, terrain_bytes, tile_path)

SCALE = WORLD_X / 180


class Dem:
    def __init__(self, dataset, periodic=False):
        self.data, self.periodic = dataset, periodic
        aff = dataset.transform
        if (dataset.count != 1 or dataset.crs is None or dataset.crs.to_epsg() not in (4326, 9518) or
                aff.b != 0 or aff.d != 0 or aff.a <= 0 or aff.e >= 0):
            raise ValueError(f"{dataset.name}: expected single-band WGS84 north-up elevation in metres")
        if periodic and not np.allclose(
                tuple(dataset.bounds), (-180, -90, 180, 90), rtol=0, atol=1e-6):
            raise ValueError(f"{dataset.name}: expected a global raster with exact ±180°/±90° edges")
        self.poles = {}
        if periodic:
            for row, lat in [(0, 90), (dataset.height - 1, -90)]:
                values = dataset.read([1], window=Window(0, row, dataset.width, 1))[0, 0]
                valid = np.isfinite(values) & (values != dataset.nodata)
                if not np.any(valid):
                    raise ValueError(f"{dataset.name}: polar row at {lat}° has no valid elevation")
                self.poles[lat] = float(values[valid].mean())

    def sample(self, lon, lat):
        d = self.data
        a = d.transform
        x, y = (lon - a.c) / a.a - .5, (lat - a.f) / a.e - .5
        if self.periodic:
            x %= d.width
            inside = np.ones((len(lat), len(lon)), bool)
        else:
            inside = ((x >= -.5) & (x <= d.width - .5))[None, :] & (
                (y >= -.5) & (y <= d.height - .5))[:, None]
            if not np.any(inside):
                return np.full((len(lat), len(lon)), np.nan)
            x = np.clip(x, 0, d.width - 1)
        y = np.clip(y, 0, d.height - 1)
        ix, iy = np.floor(x).astype(np.intp), np.floor(y).astype(np.intp)
        jx = (ix + 1) % d.width if self.periodic else np.minimum(ix + 1, d.width - 1)
        jy = np.minimum(iy + 1, d.height - 1)
        left, top = min(ix.min(), jx.min()), iy.min()
        data = d.read([1], window=Window(left, top, max(ix.max(), jx.max()) - left + 1, jy.max() - top + 1))[0]
        a, b = np.ix_(iy - top, ix - left), np.ix_(iy - top, jx - left)
        c, e = np.ix_(jy - top, ix - left), np.ix_(jy - top, jx - left)
        fx, fy = (x - ix)[None, :], (y - iy)[:, None]
        def blend(p, q, t):
            return p * (1 - t) + q * t
        values = blend(blend(data[a], data[b], fx), blend(data[c], data[e], fx), fy)
        valid = inside & np.isfinite(values)
        if d.nodata is not None and np.isfinite(d.nodata):
            valid &= (data[a] != d.nodata) & (data[b] != d.nodata) & (data[c] != d.nodata) & (data[e] != d.nodata)
        if self.periodic:
            for pole, elevation in self.poles.items():
                values[lat == pole] = elevation
                valid[lat == pole] = np.isfinite(elevation)
        return np.where(valid, values, np.nan)

    def feather(self, lon, lat):
        b = self.data.bounds
        dx = np.minimum(lon - b.left, b.right - lon)
        dz = np.minimum(lat - b.bottom, b.top - lat)
        t = np.clip(np.minimum(dx[None, :], dz[:, None]) * SCALE / 2000, 0, 1)
        return t * t * (3 - 2 * t)


class Water:
    def __init__(self, land, lakes):
        self.shapes = []
        for path, kind in [(land, 0), (lakes, 2)]:
            with path.open(encoding="utf-8") as f:
                features = json.load(f)["features"]
            for feature in features:
                shape = feature["geometry"]
                if shape is None:
                    continue
                if shape["type"] not in ("Polygon", "MultiPolygon"):
                    raise ValueError(f"{path}: expected polygon water masks")
                polys = [shape["coordinates"]] if shape["type"] == "Polygon" else shape["coordinates"]
                for poly in polys:
                    lon, lat = zip(*(p[:2] for p in poly[0]))
                    self.shapes.append(((min(lon), min(lat), max(lon), max(lat)),
                                        ({"type": "Polygon", "coordinates": poly}, kind)))
        self.poles = {pole: self.raster([0], [pole - math.copysign(1e-7, pole)], 1e-5)[0, 0]
                      for pole in (-90, 90)}

    def raster(self, lon, lat, spacing):
        left, right = lon[0] - spacing / 2, lon[-1] + spacing / 2
        bottom, top = lat[0] - spacing / 2, lat[-1] + spacing / 2
        shapes = [entry for (w, s, e, n), entry in self.shapes if w <= right and e >= left and s <= top and n >= bottom]
        shape = len(lat), len(lon)
        if not shapes:
            return np.ones(shape, np.uint8)
        transform = Affine(spacing, 0, left, 0, -spacing, top)
        return rasterize(shapes, shape, fill=1, transform=transform, dtype="uint8")[::-1]

    def sample(self, lon, lat, spacing):
        mask = self.raster(lon, lat, spacing)
        seam = (lon == -180) | (lon == 180)
        if np.any(seam):
            mask[:, seam] = self.raster([-180 + 1e-7], lat, spacing)
        for pole, kind in self.poles.items():
            mask[lat == pole] = kind
        return mask


class Terrain:
    def __init__(self, stack, etopo, land, lakes, local=()):
        self.global_dem = Dem(stack.enter_context(rasterio.open(etopo)), periodic=True)
        self.local_dems = [Dem(stack.enter_context(rasterio.open(path))) for path in local]
        self.water = Water(land, lakes)

    def sample(self, x, z, spacing, level=0):
        # Coordinates at the meridian/poles must be exactly canonical before sampling.
        lon = np.where(x == WORLD_X, 180, np.where(x == -WORLD_X, -180, x / SCALE))
        lat = np.where(z == WORLD_Z, 90, np.where(z == -WORLD_Z, -90, z / SCALE))
        water = self.water.sample(lon, lat, spacing / SCALE)
        if np.all(water == 1):
            return np.zeros(water.shape, "<i2"), water
        elevation = self.global_dem.sample(lon, lat)
        if level >= 3:
            for dem in self.local_dems:
                local = dem.sample(lon, lat)
                alpha = np.where(np.isfinite(local), dem.feather(lon, lat), 0)
                elevation += alpha * (np.where(np.isfinite(local), local, elevation) - elevation)
        if not np.all(np.isfinite(elevation) & (elevation >= -12000) & (elevation <= 10000)):
            raise ValueError(f"invalid DEM data at L{level} {lon[0]:.4f}°, {lat[0]:.4f}°")
        return round_height(np.where(water == 1, 0, elevation)), water


def build_base(etopo, land, lakes):
    with ExitStack() as stack:
        stack.enter_context(rasterio.Env(GDAL_CACHEMAX=96 * 1024 ** 2))
        source = Terrain(stack, etopo, land, lakes)
        x = -WORLD_X + BASE_STEP * np.arange(WIDTH)
        z = -WORLD_Z + BASE_STEP * np.arange(HEIGHT)
        heights, water = np.empty((HEIGHT, WIDTH), "<i2"), np.empty((HEIGHT, WIDTH), np.uint8)
        for index in range(0, HEIGHT, 64):
            heights[index:index + 64], water[index:index + 64] = source.sample(x, z[index:index + 64], BASE_STEP)
    return base_bytes(heights, water)


def groups_for(bounds, level):
    wx, sz = project(*bounds[:2])
    ex, nz = project(*bounds[2:])
    group_span = 2 * span(level)
    xlim, zlim = extent(level)
    xs = range(max(-xlim, 2 * math.floor(wx / group_span)), min(xlim, 2 * math.ceil(ex / group_span)), 2)
    zs = range(max(-zlim, 2 * math.floor(sz / group_span)), min(zlim, 2 * math.ceil(nz / group_span)), 2)
    return bounded_groups(xs, zs)


def bounded_groups(xs, zs):
    if 4 * len(xs) * len(zs) + 3 > MAX_ENTRIES:
        raise ValueError("A terrain rectangle exceeds the ZIP budget; divide it along L0 root boundaries.")
    return {(x, z) for x in xs for z in zs}


def root_groups(rect, level):
    w, s, e, n = rect
    scale = 1 << level
    return bounded_groups(range(w * scale, e * scale, 2), range(s * scale, n * scale, 2))


def root_cell(x, z, level):
    width = 1 << level
    return 2 * (x // width), 2 * (z // width)


def plan_boxes(boxes, *, roots=(), include_roots=None, exclude_roots=()):
    plan = defaultdict(set)
    for bounds, maxlevel in boxes:
        for level in range(1, maxlevel + 1):
            plan[level].update(groups_for(bounds, level))
    for rect, maxlevel in roots:
        for level in range(1, maxlevel + 1):
            plan[level].update(root_groups(rect, level))
    excluded = set(exclude_roots)
    if include_roots is not None:
        include_roots = set(include_roots)
        plan[1].update(include_roots)
        # A pilot replacing a continental root owns the whole root at L2.
        for x, z in include_roots:
            plan[2].update((2 * x + dx, 2 * z + dz) for dx, dz in ((0, 0), (2, 0), (0, 2), (2, 2)))
    for level in list(plan):
        plan[level] = {key for key in plan[level]
                       if root_cell(*key, level) not in excluded and
                       (include_roots is None or root_cell(*key, level) in include_roots)}
        if not plan[level]:
            del plan[level]
    if 4 * sum(map(len, plan.values())) + 3 > MAX_ENTRIES:
        raise ValueError("Terrain alone exceeds ZIP entry budget; split this area into disjoint L1 root groups.")
    return dict(plan)


def rectangles(plan):
    """Convert nonoverlapping sibling groups to exact, even aligned rectangles."""
    result = []
    for level, groups in sorted(plan.items()):
        rows = defaultdict(list)
        for x, z in groups:
            rows[z].append(x)
        active = {}
        for z, xs in sorted(rows.items()):
            runs = []
            for x in sorted(xs):
                if runs and runs[-1][1] == x:
                    runs[-1] = (runs[-1][0], x + 2)
                else:
                    runs.append((x, x + 2))
            continued = {}
            for w, e in runs:
                old = active.pop((w, e), None)
                if old and old[4] == z:
                    old[4] += 2
                    continued[w, e] = old
                else:
                    if old:
                        result.append(old)
                    continued[w, e] = [level, w, z, e, z + 2]
            result.extend(active.values())
            active = continued
        result.extend(active.values())
    return sorted(result)


EDGE = np.arange(65, dtype=np.float64)
EDGE_X = np.r_[EDGE, EDGE, np.zeros(63), np.full(63, 64)]
EDGE_Z = np.r_[np.zeros(65), np.full(65, 64), EDGE[1:-1], EDGE[1:-1]]
EDGE_AT = EDGE_Z.astype(int), EDGE_X.astype(int)


def parent_edge(level, x, z, base, parent=None):
    if level == 1:
        return interpolate(*base, (x * span(level) + EDGE_X * step(level) + WORLD_X) / BASE_STEP,
                           (z * span(level) + EDGE_Z * step(level) + WORLD_Z) / BASE_STEP, periodic=True)
    if parent is None:
        raise ValueError(f"L{level} sibling group ({x},{z}): missing same-pack parent")
    return interpolate(*parent, EDGE_X / 2, EDGE_Z / 2)


def write_tiles(writer, plan, base, etopo, land, lakes, local=()):
    bands = rectangles(plan)
    with ExitStack() as stack:
        stack.enter_context(rasterio.Env(GDAL_CACHEMAX=96 * 1024 ** 2))
        source = Terrain(stack, etopo, land, lakes, local)
        parents = {}
        for level in sorted(plan):
            print(f"  terrain L{level}: {4 * len(plan[level]):,} tiles", flush=True)
            next_parents = {}
            needed = {(x // 2, z // 2) for x, z in plan.get(level + 1, ())}
            spacing = step(level)
            for _, w, s, e, n in (b for b in bands if b[0] == level):
                for z0 in range(s, n, 8):
                    for x0 in range(w, e, 8):
                        nz, nx = min(z0 + 8, n) - z0, min(x0 + 8, e) - x0
                        x = (x0 * 32 + np.arange(nx * 32 + 1)) * spacing
                        z = (z0 * 32 + np.arange(nz * 32 + 1)) * spacing
                        h, water = source.sample(x, z, spacing, level)
                        for j in range(0, nz, 2):
                            for i in range(0, nx, 2):
                                tx, tz = x0 + i, z0 + j
                                area = np.s_[j * 32:j * 32 + 65, i * 32:i * 32 + 65]
                                yh, yw = parent_edge(level, tx, tz, base, parents.get((tx // 2, tz // 2)))
                                h[area][EDGE_AT], water[area][EDGE_AT] = round_height(yh), yw
                                for dx, dz in ((0, 0), (1, 0), (0, 1), (1, 1)):
                                    key = tx + dx, tz + dz
                                    tile = np.s_[(j + dz) * 32:(j + dz) * 32 + 33, (i + dx) * 32:(i + dx) * 32 + 33]
                                    writer.add(tile_path(level, *key), terrain_bytes(level, *key, h[tile], water[tile]))
                                    if key in needed:
                                        next_parents[key] = h[tile].copy(), water[tile].copy()
            parents = next_parents
