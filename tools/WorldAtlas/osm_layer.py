"""Select public OSM ways, areas, and named points without downloading map tiles."""

import re
from collections import Counter
from decimal import Decimal, ROUND_HALF_UP

import osmium

from atlas import MAX_BUILDING_HEIGHT, project
from features import Features, feature_name, project_line

ROADS = {
    "motorway": "motorway", "trunk": "trunk", "primary": "primary", "secondary": "secondary",
    "tertiary": "tertiary", "unclassified": "local", "residential": "local", "living_street": "local",
    "service": "service", "pedestrian": "path", "footway": "path", "cycleway": "path",
    "bridleway": "path", "path": "path", "track": "path", "steps": "path",
}
RAIL = {"rail": "rail", "light_rail": "light-rail", "tram": "tram", "narrow_gauge": "rail"}
PLACES = {"city", "town", "village", "hamlet"}
OSM_KEYS = ("highway", "railway", "place", "aeroway", "tourism", "historic", "natural", "leisure",
            "landuse", "building", "man_made", "amenity")
HEIGHT = re.compile(r"([0-9]+(?:\.[0-9]+)?|\.[0-9]+)\s*(m|ft)?", re.IGNORECASE)
FEET_INCHES = re.compile(r"""([0-9]+)\s*['′]\s*(?:([0-9]{1,2})\s*["″])?""")
LEVELS = re.compile(r"([0-9]+)(?:\.0+)?")
FOOT = Decimal("0.3048")
INCH = Decimal("0.0254")


def building_height(tags):
    height = (tags.get("height") or "").strip()
    metres = Decimal(0)
    if match := HEIGHT.fullmatch(height):
        metres = Decimal(match[1])
        if (match[2] or "").lower() == "ft":
            metres *= FOOT
    elif match := FEET_INCHES.fullmatch(height):
        inches = int(match[2] or 0)
        if inches < 12:
            metres = Decimal(match[1]) * FOOT + inches * INCH
    if 0 < metres <= MAX_BUILDING_HEIGHT:
        rounded = int(metres.to_integral_value(rounding=ROUND_HALF_UP))
        if rounded:
            return rounded, "heightTag"
    if match := LEVELS.fullmatch((tags.get("building:levels") or "").strip()):
        levels = Decimal(match[1])
        if 0 < levels <= MAX_BUILDING_HEIGHT // 3:
            return int(levels) * 3, "levelsEstimate"
    return None, None


def building_style(tags):
    building = tags.get("building")
    if building in (None, "no", "construction", "ruins"):
        return None
    if building in ("commercial", "office", "retail"):
        return "commercial"
    if building in ("industrial", "warehouse"):
        return "industrial"
    if building in ("apartments", "residential", "house", "detached", "terrace", "semidetached_house"):
        return "residential"
    return "building"


def classify(tags, *, way):
    name = feature_name(tags.get("name:en"), tags.get("name"), tags.get("ref"))
    if way:
        road = ROADS.get(tags.get("highway", "").removesuffix("_link"))
        if road and tags.get("area") != "yes":
            return "road", road, name, "line"
        if rail := RAIL.get(tags.get("railway")):
            return "rail", rail, name, "line"
    if tags.get("aeroway") in ("aerodrome", "airstrip") and name:
        return "airport", "airport" if tags.get("iata") else "airfield", name, "point"
    place = tags.get("place")
    if place in PLACES and name:
        return "place", "capital" if tags.get("capital") == "yes" else place, name, "point"
    tourism, natural, leisure = tags.get("tourism"), tags.get("natural"), tags.get("leisure")
    if name:
        landmark = ("peak" if natural in ("peak", "volcano") else
                    "viewpoint" if tourism == "viewpoint" else "stadium" if leisure == "stadium" else
                    "monument" if tags.get("historic") in ("monument", "memorial") else
                    "historic" if tags.get("historic") not in (None, "no") else
                    "attraction" if tourism in ("attraction", "museum", "theme_park", "zoo") else
                    "natural" if natural in ("cape", "rock", "waterfall", "cave_entrance") else None)
        if landmark:
            return "landmark", landmark, name, "point"
    if way:
        if style := building_style(tags):
            return "building", style, name, "polygon"
        landuse = tags.get("landuse")
        if natural == "wood" or landuse == "forest":
            return "landuse", "forest", name, "polygon"
        if leisure in ("park", "garden", "nature_reserve"):
            return "landuse", "park", name, "polygon"
        if landuse in ("grass", "meadow") or natural == "grassland":
            return "landuse", "grass", name, "polygon"
    return None


def selected_osm(source, bounds, *, index="sparse_mem_array"):
    layer = Features(5, "osm", bounds)
    processor = osmium.FileProcessor(str(source.path), osmium.osm.NODE | osmium.osm.WAY)
    header = processor.header
    snapshot = {name: value for name, value in (
        ("replicationTimestamp", header.get("osmosis_replication_timestamp")),
        ("replicationSequence", header.get("osmosis_replication_sequence_number")),
        ("replicationUrl", header.get("osmosis_replication_base_url")),
    ) if value}
    box = header.box()
    w, s, e, n = bounds
    if box.valid():
        source_box = [box.bottom_left.lon, box.bottom_left.lat, box.top_right.lon, box.top_right.lat]
        snapshot["sourceBoundsDegrees"] = source_box
        if e < source_box[0] or w > source_box[2] or n < source_box[1] or s > source_box[3]:
            raise ValueError(f"{source.path}: requested feature bounds do not intersect the extract")
    stats = Counter()
    processor.with_locations(index).with_filter(osmium.filter.KeyFilter(*OSM_KEYS))
    for obj in processor:
        if obj.is_node():
            location = obj.location
            if location.valid() and w <= location.lon <= e and s <= location.lat <= n:
                attrs = classify(obj.tags, way=False)
                if attrs:
                    layer.point(*attrs[:3], project(location.lon, location.lat))
                    stats["selectedNodes"] += 1
            continue
        # Skip nonrenderable ways before allocating their coordinate sequence.
        attrs = classify(obj.tags, way=True)
        if not attrs:
            continue
        coords = []
        for ref in obj.nodes:
            p = ref.location
            if not p.valid():
                stats["waysWithMissingNodes"] += 1
                break
            coords.append((p.lon, p.lat))
        else:
            if len(coords) < 2:
                continue
            lon, lat = zip(*coords)
            if min(lon) > e or max(lon) < w or min(lat) > n or max(lat) < s:
                continue
            kind, style, name, shape = attrs
            points = list(project_line(coords))
            if shape == "line":
                layer.line(kind, style, name, points)
            else:
                closed = len(points) >= 4 and points[0] == points[-1]
                if closed:
                    footprint = style if kind == "building" else building_style(obj.tags)
                    if footprint:
                        height, height_source = building_height(obj.tags)
                        layer.polygon("building", footprint, name, points, height=height, height_source=height_source)
                    elif shape == "polygon":
                        layer.polygon(kind, style, name, points)
                if shape == "point":
                    if closed:
                        from shapely import Polygon
                        p = Polygon(points).representative_point()
                        if not p.is_empty:
                            layer.point(kind, style, name, (p.x, p.y))
                    else:
                        layer.point(kind, style, name, points[len(points) // 2])
            stats["selectedWays"] += 1
    return layer, snapshot, dict(stats)
