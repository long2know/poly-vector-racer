#!/usr/bin/env python3
"""Verify the shipped geography independently of the browser and the tile builder."""

import argparse
import json
import math
import struct
from pathlib import Path

SIDE = 33
N = SIDE * SIDE
SIZE = 16 + N * 3


def decode(path, level, x, z):
    data = path.read_bytes()
    if len(data) != SIZE or struct.unpack_from("<4siii", data) != (b"WFT1", level, x, z):
        raise ValueError(f"Bad tile header at {path}")
    return data


def rectangles(region, level):
    return [region["coverage"][level], *region.get("patches", [[]] * len(region["coverage"]))[level]]


def nearest(folder, region, x, z, spacing):
    for level in reversed(range(len(region["coverage"]))):
        step = spacing[level]
        span = step * (SIDE - 1)
        i, j = math.floor(x / span), math.floor(z / span)
        if any(west <= i < east and south <= j < north for west, south, east, north in rectangles(region, level)):
            path = folder / "tiles" / region["id"] / str(level) / str(i) / f"{j}.bin"
            samples = decode(path, level, i, j)
            index = round((z - j * span) / step) * SIDE + round((x - i * span) / step)
            return (level, struct.unpack_from("<h", samples, 16 + index * 2)[0], samples[16 + N * 2 + index])
    raise ValueError(f"No {region['id']} terrain at {x}, {z}")


def check(folder):
    manifest = json.loads((folder / "manifest.json").read_text())
    receipt = json.loads((folder / "receipt.json").read_text())
    if manifest["version"] != receipt["dataRevision"]:
        raise ValueError("Receipt and terrain manifest revisions differ.")
    if {r["id"] for r in manifest["regions"]} != {"conus", "alaska", "hawaii"}:
        raise ValueError("Terrain catalog is missing a geographic US region.")
    count = 0
    expected = {"rainier": (6, 4250, 4430), "olympics": (6, 2250, 2450),
                "cascades": (6, 3000, 3350), "denali": (2, 4600, 6250), "mauna-loa": (2, 3750, 4225)}
    checked = {}
    for region in manifest["regions"]:
        windows = []
        for level in range(len(region["coverage"])):
            boxes = rectangles(region, level)
            for index, box in enumerate(boxes):
                west, south, east, north = box
                if west >= east or south >= north or any(west < a[2] and east > a[0] and south < a[3] and north > a[1]
                                                         for a in boxes[:index]):
                    raise ValueError(f"{region['id']}/{level} has an empty or overlapping tile area.")
                if level and (any(v % 2 for v in box) or not any(
                        p[0] * 2 <= west < east <= p[2] * 2 and p[1] * 2 <= south < north <= p[3] * 2
                        for p in rectangles(region, level - 1))):
                    raise ValueError(f"{region['id']}/{level} has incomplete parent coverage.")
                windows.append((level, box))
        for level, (west, south, east, north) in windows:
            previous = {}
            for z in range(south, north):
                last, current = None, {}
                for x in range(west, east):
                    tile = decode(folder / "tiles" / region["id"] / str(level) / str(x) / f"{z}.bin", level, x, z)
                    heights = struct.unpack_from(f"<{N}h", tile, 16)
                    water = tile[16 + N * 2:]
                    if min(heights) < -12000 or max(heights) > 10000 or max(water) > 2:
                        raise ValueError(f"Invalid height or water at {region['id']}/{level}/{x}/{z}")
                    if last is not None and (heights[0::SIDE], water[0::SIDE]) != last:
                        raise ValueError(f"East-west seam differs at {region['id']}/{level}/{x}/{z}")
                    if x in previous and (heights[:SIDE], water[:SIDE]) != previous[x]:
                        raise ValueError(f"North-south seam differs at {region['id']}/{level}/{x}/{z}")
                    last = heights[SIDE - 1::SIDE], water[SIDE - 1::SIDE]
                    current[x] = heights[-SIDE:], water[-SIDE:]
                    count += 1
                previous = current
        for d in region["destinations"]:
            level, start, wet = nearest(folder, region, d["x"], d["z"], manifest["spacing"])
            if d["altitude"] < start + 120:
                raise ValueError(f"{d['id']} departure intersects real terrain.")
            if d["id"] == "seattle" and (level, start, wet) != (8, 0, 1):
                raise ValueError(f"Seattle scenic approach should start over Elliott Bay, not land: {(level, start, wet)}")
            if d["id"] == "san-francisco" and (level, start, wet) != (8, 0, 1):
                raise ValueError(f"San Francisco scenic approach should start over the bay: {(level, start, wet)}")
            if region.get("patches") and d["id"] in {"rainier", "canyon", "yosemite", "bay", "san-francisco"}:
                target = nearest(folder, region, d["targetX"], d["targetZ"], manifest["spacing"])
                if target[0] != 8:
                    raise ValueError(f"{d['id']} should have a 32 m terrain core: {target}")
                if d["id"] == "yosemite" and not (level == 8 and 1200 <= start <= 2000 and 2540 < target[1] < 2750):
                    raise ValueError("Yosemite valley-to-dome relief does not match the high-resolution USGS product.")
                if d["id"] == "canyon":
                    rim = max(nearest(folder, region, d["x"] + dx, d["z"] + dz, manifest["spacing"])[1]
                              for dx in [-2250, -1500, -750, 0, 750, 1500, 2250] for dz in [-1500, -750, 0, 750])
                    if not (level == 8 and 1200 < start < 2250 and rim - target[1] > 800 and target[2] == 0):
                        raise ValueError("Grand Canyon has no rim-to-floor relief near the flight path.")
                checked[d["id"]] = target[1]
            if d["id"] in expected:
                minimum_level, lo, hi = expected[d["id"]]
                result = nearest(folder, region, d["targetX"], d["targetZ"], manifest["spacing"])
                if result[0] < minimum_level or not lo <= result[1] <= hi or result[2] != 0:
                    raise ValueError(f"{d['id']} elevation not represented by real terrain: {result}")
                checked[d["id"]] = result[1]
    if count != receipt["tilesWritten"]:
        raise ValueError(f"Tile count {count} differs from receipt.")
    conus = next(region for region in manifest["regions"] if region["id"] == "conus")
    cities = conus.get("cities", [{"id": "seattle"}])
    for city in cities:
        city_id = city["id"]
        buildings = json.loads((folder / f"{city_id}-buildings.json").read_text())
        rows = buildings["buildings"]
        measured = sum(row[2] == "height" for row in rows)
        if (buildings["license"] != "ODbL-1.0" or len(rows) != city.get("featureCount", len(rows)) or
                measured != city.get("taggedHeights", measured) or
                (city_id == "seattle" and (len(rows) < 1200 or measured < 100)) or
                (city_id == "san-francisco" and (len(rows) < 5000 or measured < 3000))):
            raise ValueError(f"{city_id} data has no provenance or lacks actual heights.")
        print(f"{city_id}: {len(rows):,} footprints, {measured:,} height tags")
    print(f"{count:,} tiles and shared edges verified; sampled terrain heights (metres): {checked}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    check(parser.parse_args().folder)
