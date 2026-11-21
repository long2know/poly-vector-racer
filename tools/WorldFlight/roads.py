"""Adapt public OSM highway centerlines for Seattle's visual road layer."""

import json
import math
import xml.etree.ElementTree as ET
from collections import Counter

from areas import SEATTLE_LOCAL_BOUNDS, SEATTLE_ROAD_BOUNDS

ATTRIBUTION = "© OpenStreetMap contributors"
LICENSE_URL = "https://www.openstreetmap.org/copyright"
MAX_BYTES = 4 * 1024 * 1024
MAX_ROADS = 12000
MAX_POINTS = 100000
MAX_SEGMENT = 128
CLASS = {road: kind for kind, roads in (
    ("freeway", ("motorway", "trunk")),
    ("arterial", ("primary", "secondary", "tertiary")),
    ("local", ("residential", "unclassified", "living_street")),
) for road in roads}


def classify(tags):
    kind = CLASS.get(tags.get("highway", "").removesuffix("_link"))
    layer = tags.get("layer", "0")
    if (kind is None or tags.get("access") == "private" or tags.get("area") == "yes" or
            tags.get("location") == "underground" or tags.get("tunnel", "no") != "no" or
            tags.get("covered", "no") != "no" or layer.startswith("-")):
        return None
    return kind, "bridge" if tags.get("bridge", "no") != "no" else "surface"


def inside(point, bounds):
    x, z = point
    return bounds[0] <= x <= bounds[2] and bounds[1] <= z <= bounds[3]


def clip_segment(a, b, bounds):
    low, high = 0, 1
    for i in range(2):
        direction = b[i] - a[i]
        if direction == 0:
            if not bounds[i] <= a[i] <= bounds[i + 2]:
                return None
            continue
        times = sorted(((bounds[i] - a[i]) / direction, (bounds[i + 2] - a[i]) / direction))
        low, high = max(low, times[0]), min(high, times[1])
        if low >= high:
            return None
    def at(t):
        return a if t == 0 else b if t == 1 else tuple(p + (q - p) * t for p, q in zip(a, b))
    return at(low), at(high)


def clipped_paths(points, bounds):
    current = []
    for a, b in zip(points, points[1:]):
        line = clip_segment(a, b, bounds)
        if line is None or (current and current[-1] != line[0]):
            if len(current) > 1:
                yield current
            current = []
        if line is not None:
            if not current:
                current.append(line[0])
            if current[-1] != line[1]:
                current.append(line[1])
    if len(current) > 1:
        yield current


def osm_roads(inputs):
    ways = {}
    for path in inputs:
        root = ET.parse(path).getroot()
        if root.tag != "osm":
            raise ValueError(f"Expected an OSM XML extract: {path}")
        nodes = {node.attrib["id"]: (float(node.attrib["lon"]), float(node.attrib["lat"]))
                 for node in root.iterfind("node")}
        for way in root.iterfind("way"):
            tags = {tag.attrib["k"]: tag.attrib["v"] for tag in way.iterfind("tag")}
            if result := classify(tags):
                feature = int(way.attrib["id"])
                version = int(way.get("version", "0"))
                if feature in ways and ways[feature][0] >= version:
                    continue
                refs = [node.attrib["ref"] for node in way.iterfind("nd")]
                if len(refs) >= 2 and all(ref in nodes for ref in refs):
                    ways[feature] = version, *result, [nodes[ref] for ref in refs]
    for feature, (_, kind, structure, points) in sorted(ways.items()):
        bounds = SEATTLE_LOCAL_BOUNDS if kind == "local" else SEATTLE_ROAD_BOUNDS
        if structure == "bridge" and not all(inside(p, bounds) for p in points):
            continue  # Do not draw a geographic extract ending mid-span.
        for path in clipped_paths(points, bounds):
            yield feature, kind, structure, path


def subdivide(points, max_segment):
    result = []
    for a, b in zip(points, points[1:]):
        parts = max(1, math.ceil(math.dist(a, b) / max_segment))
        for i in range(parts):
            point = tuple(round(p + (q - p) * i / parts, 2) for p, q in zip(a, b))
            if not result or point != result[-1]:
                result.append(point)
    last = tuple(round(v, 2) for v in points[-1])
    if not result or result[-1] != last:
        result.append(last)
    return result


def bridge_profile(points, ground):
    distances = [0]
    for a, b in zip(points, points[1:]):
        distances.append(distances[-1] + math.dist(a, b))
    def anchor(point):
        height, wet = ground(*point)
        return height + (5 if wet else 1.6)
    a, b = anchor(points[0]), anchor(points[-1])
    rise = min(7, distances[-1] * .085)
    return [(*p, round(a + (b - a) * t + rise * math.sin(math.pi * t), 2))
            for p, d in zip(points, distances) for t in [d / distances[-1]]]


def extract_roads(inputs, output, project, ground):
    paths = list(osm_roads(inputs))
    projected = iter(project([p for _, _, _, line in paths for p in line]))
    rows = []
    for feature, kind, structure, path in paths:
        points = subdivide([next(projected) for _ in path], 22 if structure == "bridge" else MAX_SEGMENT)
        if len(points) < 2:
            continue
        if structure == "bridge":
            points = bridge_profile(points, ground)
        for i in range(0, len(points) - 1, 255):
            rows.append([feature, kind, structure, points[i:i + 256]])

    coordinates = [point for row in rows for point in row[3]]
    if not coordinates:
        raise ValueError("The Seattle road extracts contain no mapped roads.")
    bounds = [round(op(p[i] for p in coordinates), 2) for op in (min, max) for i in range(2)]
    metadata = dict(id="seattle", featureCount=len(rows), pointCount=len(coordinates), bounds=bounds)
    path = output / "seattle-roads.json"
    payload = dict(license="ODbL-1.0", attribution=ATTRIBUTION, licenseUrl=LICENSE_URL,
                   modifications="Selected and classified OSM highway ways, clipped and projected to metres; "
                                 "elevated bridge decks are estimates from the terrain, not surveyed heights.",
                   roads=rows)
    validate_roads(metadata, payload)
    body = json.dumps(payload, ensure_ascii=False, allow_nan=False, separators=(",", ":")) + "\n"
    if len(body.encode()) > MAX_BYTES:
        raise ValueError("The Seattle road extract exceeds its size limit.")
    path.write_text(body, encoding="utf-8")
    return metadata


def validate_roads(metadata, data):
    def numeric(v):
        return type(v) in (int, float) and math.isfinite(v)
    if not isinstance(metadata, dict) or metadata.get("id") != "seattle" or not isinstance(data, dict):
        raise ValueError("Invalid World Flight Seattle road metadata.")
    rows, bounds = data.get("roads"), metadata.get("bounds")
    if (data.get("license") != "ODbL-1.0" or data.get("attribution") != ATTRIBUTION or
            data.get("licenseUrl") != LICENSE_URL or not isinstance(rows, list) or
            type(metadata.get("featureCount")) is not int or
            not 1 <= len(rows) == metadata["featureCount"] <= MAX_ROADS or
            type(metadata.get("pointCount")) is not int or
            not 2 * len(rows) <= metadata["pointCount"] <= MAX_POINTS or
            not isinstance(bounds, list) or len(bounds) != 4 or
            not all(numeric(v) and abs(v) <= 40000 for v in bounds) or
            not all(0 < bounds[i + 2] - bounds[i] <= 20000 for i in range(2))):
        raise ValueError("Invalid World Flight Seattle road metadata, size or attribution.")
    points = 0
    counts = Counter()
    for row in rows:
        if not isinstance(row, (tuple, list)) or len(row) != 4:
            raise ValueError("Invalid World Flight Seattle road row.")
        feature, kind, structure, line = row
        if (type(feature) is not int or not 1 <= feature < 2**53 or
                kind not in ("freeway", "arterial", "local") or structure not in ("surface", "bridge") or
                not isinstance(line, (tuple, list)) or not 2 <= len(line) <= 256):
            raise ValueError("Invalid World Flight Seattle road kind or shape.")
        for p in line:
            if (not isinstance(p, (tuple, list)) or len(p) != (3 if structure == "bridge" else 2) or
                    not all(numeric(v) for v in p) or not inside(p[:2], bounds) or
                    (structure == "bridge" and not -50 <= p[2] <= 300)):
                raise ValueError("Invalid World Flight Seattle road coordinate.")
        if any(not 0 < math.dist(a[:2], b[:2]) <= MAX_SEGMENT + .03 for a, b in zip(line, line[1:])):
            raise ValueError("Invalid World Flight Seattle road segment.")
        points += len(line)
        if points > metadata["pointCount"]:
            raise ValueError("Seattle road point count differs from the terrain catalog.")
        counts[kind] += 1
        if structure == "bridge":
            counts["bridge"] += 1
    if points != metadata["pointCount"]:
        raise ValueError("Seattle road point count differs from the terrain catalog.")
    return counts
