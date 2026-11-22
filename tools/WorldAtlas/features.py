"""Clip geographic geometry to signed Atlas cells and write bounded vector chunks."""

import json
import math
from collections import Counter, defaultdict, deque

from atlas import (BUILDING_HEIGHT_SOURCES, FEATURE_STYLES, MAX_FEATURE_BYTES, MAX_POINTS, MAX_ROWS, WORLD_X,
                   WORLD_Z, extent, feature_path, json_bytes, project, span, valid_building_height, wrap_cell)

PRIORITY = {"landmark": 0, "airport": 1, "place": 2, "road": 3, "rail": 4, "landuse": 5, "building": 6}
MAIN_ROADS = frozenset(("motorway", "trunk", "primary", "secondary", "tertiary"))
L5_AREA_BUDGETS = {
    "building": (MAX_ROWS // 4, MAX_POINTS // 4, MAX_FEATURE_BYTES // 4),
    "landuse": (MAX_ROWS // 32, MAX_POINTS // 16, MAX_FEATURE_BYTES // 16),
}
L5_SUBDIVISIONS = 8


def feature_name(*values):
    for value in values:
        if isinstance(value, str):
            cleaned = " ".join("".join(c if c.isprintable() else " " for c in value).split())
            cleaned = cleaned.encode("utf-16-le")[:240].decode("utf-16-le", errors="ignore").strip()
            if cleaned and cleaned not in ("-99", "-1"):
                return cleaned
    return None


def project_line(coords):
    previous = None
    for lon, lat, *_ in coords:
        x, z = project(lon, lat)
        if previous is not None:
            x += round((previous - x) / (2 * WORLD_X)) * 2 * WORLD_X
        yield x, z
        previous = x


def clip_segment(a, b, rect):
    w, s, e, n = rect
    ax, az = a
    dx, dz = b[0] - ax, b[1] - az
    lo, hi = 0., 1.
    for p, d, low, high in ((ax, dx, w, e), (az, dz, s, n)):
        if d == 0:
            if p < low or p > high:
                return None
        else:
            a0, a1 = sorted(((low - p) / d, (high - p) / d))
            lo, hi = max(lo, a0), min(hi, a1)
            if lo >= hi:
                return None
    return ((ax + lo * dx, az + lo * dz), (ax + hi * dx, az + hi * dz))


class Features:
    def __init__(self, level, source, bounds=None):
        self.level, self.source, self.size = level, source, span(level)
        self.zlim = extent(level)[1]
        self.clip = (*project(*bounds[:2]), *project(*bounds[2:])) if bounds else None
        self.rows = defaultdict(set)
        self.height_sources = defaultdict(dict)

    def cell(self, x, z):
        return math.floor(x / self.size), min(self.zlim - 1, math.floor(z / self.size))

    def local(self, point, x, z):
        return tuple(max(0, min(self.size, round(p - i * self.size))) for p, i in zip(point, (x, z)))

    def row(self, x, z, kind, style, name, points, *, height=None, height_source=None):
        cell = wrap_cell(self.level, x), z
        row = kind, style, name, self.source, tuple(points)
        if height is not None or height_source is not None:
            if not valid_building_height(self.level, kind, self.source, height) or height_source not in BUILDING_HEIGHT_SOURCES:
                raise ValueError("Building height requires an L5 OSM building, a valid metre value and tag provenance.")
            row += (height,)
            sources = self.height_sources[cell]
            if sources.get(row) != "heightTag":
                sources[row] = height_source
        self.rows[cell].add(row)

    def point(self, kind, style, name, p):
        if not name:
            return
        x, z = p
        if self.clip:
            w, s, e, n = self.clip
            if not (w <= x <= e and s <= z <= n):
                return
        if not (-WORLD_X <= x <= WORLD_X and -WORLD_Z <= z <= WORLD_Z):
            return
        ix, iz = self.cell(x, z)
        self.row(ix, iz, kind, style, name, (self.local(p, ix, iz),))

    def line(self, kind, style, name, points):
        key, fragment, seen = None, [], set()
        size = self.size

        def flush():
            if len(fragment) >= 2:
                self.row(*key, kind, style, name, fragment)
            fragment.clear()
            seen.clear()

        for a, b in zip(points, points[1:]):
            if a == b:
                continue
            if self.clip:
                hit = clip_segment(a, b, self.clip)
                if hit is None:
                    flush()
                    continue
                a, b = hit
            ax, az = a
            dx, dz = b[0] - ax, b[1] - az
            cuts = {0., 1.}
            for p, delta, q in ((ax, dx, b[0]), (az, dz, b[1])):
                if delta:
                    cuts.update(t for g in range(math.floor(min(p, q) / size) + 1, math.ceil(max(p, q) / size))
                                if 0 < (t := (g * size - p) / delta) < 1)
            ts = sorted(cuts)
            for start, end in zip(ts, ts[1:]):
                m = (start + end) / 2
                current = self.cell(ax + m * dx, az + m * dz)
                p = self.local((ax + start * dx, az + start * dz), *current)
                q = self.local((ax + end * dx, az + end * dz), *current)
                if p == q:
                    continue
                if key != current or not fragment or fragment[-1] != p or q in seen or len(fragment) == 2048:
                    flush()
                    key = current
                    fragment.append(p)
                    seen.add(p)
                fragment.append(q)
                seen.add(q)
        flush()

    def polygon(self, kind, style, name, points, *, height=None, height_source=None):
        from shapely import Polygon, box
        if len(points) < 4 or points[0] != points[-1]:
            return
        shape = Polygon(points)
        if not shape.is_valid or shape.is_empty or shape.area < 16:
            return
        if self.clip:
            shape = shape.intersection(box(*self.clip))
        if shape.is_empty:
            return
        w, s, e, n = shape.bounds
        lo_x, lo_z = self.cell(w, s)
        hi_x, hi_z = self.cell(e, n)
        size = self.size
        for x in range(lo_x, hi_x + 1):
            for z in range(lo_z, hi_z + 1):
                clipped = shape.intersection(box(x * size, z * size, (x + 1) * size, (z + 1) * size))
                for geometry in clipped.geoms if hasattr(clipped, "geoms") else (clipped,):
                    if geometry.geom_type != "Polygon" or geometry.interiors or geometry.area < 16:
                        continue
                    ring = []
                    for p in geometry.exterior.coords:
                        local = self.local(p, x, z)
                        if not ring or ring[-1] != local:
                            ring.append(local)
                    if len(ring) >= 4 and ring[0] == ring[-1]:
                        ring.pop()
                        if len(ring) == len(set(ring)) and len(ring) <= 2048:
                            # Quantization can collapse a source ring; don't publish zero-area geometry.
                            quantized = Polygon(ring)
                            if quantized.is_valid and quantized.area >= 8:
                                self.row(x, z, kind, style, name, ring, height=height, height_source=height_source)

    def write(self, writer):
        tile_keys = []
        count, classes, candidate_kinds, retained_kinds = Counter(), Counter(), Counter(), Counter()
        height_counts = dict.fromkeys(BUILDING_HEIGHT_SOURCES, 0)
        densest = {"rows": 0, "points": 0, "bytes": 0}
        for (x, z), values in sorted(self.rows.items()):
            count["candidateRows"] += len(values)
            body = []
            header = json_bytes({"schemaVersion": 1, "tile": [self.level, x, z]})[:-1] + b',"rows":['
            # Counting one comma per row leaves room for the closing brackets, minus the last comma.
            budget = MAX_ROWS, MAX_POINTS, MAX_FEATURE_BYTES - len(header) - 1
            if self.level == 5:
                candidate_kinds.update(row[0] for row in values)
                chosen = local_selection(values, self.size, budget)
            else:
                chosen, _ = fit_rows(sorted(values, key=row_order), budget)
            num_points = 0
            for row, encoded in chosen:
                num_points += len(row[4])
                body.append(encoded)
                classes[f"{row[0]}/{row[1]}"] += 1
                if self.level == 5:
                    retained_kinds[row[0]] += 1
                    if len(row) == 6:
                        height_counts[self.height_sources[x, z][row]] += 1
            if body:
                path = feature_path(self.level, x, z)
                data = header + b",".join(body) + b"]}"
                writer.add(path, data)
                tile_keys.append([self.level, x, z])
                count["rows"] += len(body)
                count["points"] += num_points
                for field, value in (("rows", len(body)), ("points", num_points), ("bytes", len(data))):
                    densest[field] = max(densest[field], value)
        stats = {"tiles": len(tile_keys), "candidateRows": count["candidateRows"],
                 "rows": count["rows"], "points": count["points"],
                 "droppedForTileBudget": count["candidateRows"] - count["rows"],
                 "largestTile": densest, "classes": dict(sorted(classes.items()))}
        if self.level == 5:
            stats["candidateRowsByKind"] = dict(sorted(candidate_kinds.items()))
            stats["droppedRowsByKind"] = {kind: n - retained_kinds[kind] for kind, n in sorted(candidate_kinds.items())}
            stats["buildingHeightRows"] = height_counts
        return tile_keys, stats


def row_order(row):
    kind, style, name, source, points = row[:5]
    return PRIORITY[kind], FEATURE_STYLES[kind].index(style), name is None, name or "", source, points


def fit_rows(ordered, budget):
    rows, points, size = budget
    chosen = []
    for row in ordered:
        if not rows:
            break
        n = len(row[4])
        if n > points:
            continue
        encoded = json_bytes(row)
        if len(encoded) + 1 > size:
            continue
        chosen.append((row, encoded))
        rows, points, size = rows - 1, points - n, size - len(encoded) - 1
    return chosen, (rows, points, size)


def spatial_areas(rows, size):
    buckets = defaultdict(list)
    for row in rows:
        xs, zs = zip(*row[4])
        w, s, e, n = min(xs), min(zs), max(xs), max(zs)
        cell = tuple(min(L5_SUBDIVISIONS - 1, coord * L5_SUBDIVISIONS // (2 * size))
                     for coord in (w + e, s + n))
        height = row[5] if len(row) == 6 else 0
        buckets[cell].append((-height, row[2] is None, -(e - w) * (n - s), row_order(row), row))

    # Lower bits first visits widely separated cells before nearby ones.
    def spread(cell):
        return tuple(i & bit for bit in (1, 2, 4) for i in cell)

    queue = deque(iter(sorted(buckets[cell])) for cell in sorted(buckets, key=spread))
    while queue:
        bucket = queue.popleft()
        if item := next(bucket, None):
            yield item[-1]
            queue.append(bucket)


def local_selection(values, size, budget):
    high, low = [], []
    areas = {kind: [] for kind in L5_AREA_BUDGETS}
    for row in values:
        kind, style = row[:2]
        if kind in areas:
            areas[kind].append(row)
        elif kind != "road" or style in MAIN_ROADS:
            high.append(row)
        else:
            low.append(row)

    reserved, spill = [], []
    for kind, limits in L5_AREA_BUDGETS.items():
        reserve, _ = fit_rows(spatial_areas(areas[kind], size), limits)
        reserved.extend(reserve)
        spill.append((areas[kind], {row for row, _ in reserve}))
    held = len(reserved), sum(len(row[4]) for row, _ in reserved), sum(len(data) + 1 for _, data in reserved)
    first, remaining = fit_rows(sorted(high, key=row_order), tuple(cap - count for cap, count in zip(budget, held)))
    second, remaining = fit_rows(sorted(low, key=row_order), remaining)
    yield from first
    yield from reserved
    yield from second
    for candidates, picked in reversed(spill):
        if not remaining[0] or remaining[1] < 3:
            break
        extra, remaining = fit_rows(spatial_areas((row for row in candidates if row not in picked), size), remaining)
        yield from extra


def natural_earth(paths):
    layer = Features(0, "natural-earth")
    for theme, path in paths.items():
        with path.open(encoding="utf-8") as f:
            features = json.load(f)["features"]
        for feature in features:
            props, geometry = feature["properties"], feature["geometry"]
            if geometry is None:
                continue
            kind, coords = geometry["type"], geometry["coordinates"]
            if theme == "railroads":
                if props.get("featurecla") != "Railroad":
                    continue
                if kind not in ("LineString", "MultiLineString"):
                    raise ValueError(f"{path}: unexpected railroad geometry {kind}")
                for item in [coords] if kind == "LineString" else coords:
                    layer.line("rail", "rail", None, list(project_line(item)))
            elif theme == "roads":
                category = str(props.get("type") or "").lower()
                if "ferry" in category or "ferry" in str(props.get("featurecla") or "").lower() or category == "track":
                    continue
                style = ("motorway" if props.get("expressway") == 1 else
                         {"major highway": "trunk", "secondary highway": "secondary", "beltway": "primary",
                          "bypass": "primary", "road": "tertiary"}.get(category, "local"))
                if kind not in ("LineString", "MultiLineString"):
                    raise ValueError(f"{path}: unexpected road geometry {kind}")
                for item in [coords] if kind == "LineString" else coords:
                    layer.line("road", style, feature_name(props.get("name"), props.get("label")),
                               list(project_line(item)))
            elif kind == "Point":
                name = feature_name(props.get("name_en"), props.get("name"), props.get("nameascii"))
                category = str(props.get("featurecla") or "").lower()
                if theme == "places" and category in ("scientific station", "meteorological station", "historic place"):
                    kind, style = "landmark", "historic" if category == "historic place" else "attraction"
                elif theme == "places":
                    kind = "place"
                    style = ("capital" if props.get("adm0cap") == 1 else
                             "city" if (props.get("pop_max") or 0) >= 100_000 or props.get("worldcity") else "town")
                elif theme == "airports":
                    kind, style = "airport", "airport" if props.get("iata_code") else "airfield"
                else:
                    kind, style = "landmark", "peak" if theme == "peaks" and category == "mountain" else "natural"
                layer.point(kind, style, name, project(*coords[:2]))
    return layer
