#!/usr/bin/env python3
"""Build local WFT1 terrain and independently licensed OSM scenery extracts."""

import argparse
import hashlib
import json
import math
import re
import struct
import xml.etree.ElementTree as ET
from functools import lru_cache
from pathlib import Path

import numpy as np
import rasterio as rio
from scipy.ndimage import distance_transform_edt
from rasterio.features import rasterize
from rasterio.transform import from_origin
from rasterio.warp import Resampling, reproject, transform, transform_bounds

from areas import (BASE_ELEVATION, DETAIL_ELEVATION, OSM_EXTRACTS, PACIFIC_NORTHWEST, WESTERN_CORES,
                   WESTERN_DETAIL, seattle_road_inputs)
from roads import extract_roads

SIDE = 33
SPACING = [8192 >> level for level in range(9)]
WGS = "EPSG:4326"
HAWAII = "+proj=lcc +lat_1=19 +lat_2=27 +lat_0=20 +lon_0=-162 +ellps=WGS84 +units=m +no_defs"

# Geodetic locations are lon/lat. Air tours start offset so the landmark is in front of the camera.
REGIONS = {
    "conus": ("Lower 48", "EPSG:5070", (-122.31, 47.53), [(-126.7, 23, -65, 51)], [
        ("boeing", "Boeing Field • runway", (-122.31, 47.53), (-122.34, 47.616), 700),
        ("seattle", "Seattle • skyline and Puget Sound", (-122.386, 47.610), (-122.344, 47.616), 475),
        ("rainier", "Mount Rainier • northwest approach", (-122.035, 47.026), (-121.7604, 46.8523), 3650),
        ("olympics", "Olympic Mountains • Mount Olympus", (-123.33, 47.79), (-123.7109, 47.8014), 2250),
        ("cascades", "North Cascades • Mount Baker", (-122.067, 48.77), (-121.8144, 48.7768), 2850),
        ("st-helens", "Mount St. Helens • Cascades", (-122.36, 46.34), (-122.1944, 46.1912), 2150),
        ("cruise", "Cascades • high-altitude cruise", (-122.12, 46.96), (-121.37, 46.78), 8500),
        ("bay", "San Francisco • Golden Gate", (-122.525, 37.811), (-122.473, 37.819), 500),
        ("san-francisco", "San Francisco • skyline and Bay", (-122.443, 37.831), (-122.398, 37.793), 550),
        ("yosemite", "Yosemite Valley • Half Dome", (-119.732, 37.723), (-119.532, 37.746), 2540),
        ("canyon", "Grand Canyon • south rim", (-112.115, 36.07), (-112.118, 36.09), 2380),
        ("rockies", "Colorado Rockies • Longs Peak", (-105.05, 40.26), (-105.616, 40.255), 3900),
        ("lakes", "Great Lakes • Lake Michigan", (-87.28, 41.92), (-87.62, 41.9), 1150),
        ("smokies", "Great Smoky Mountains", (-83.97, 35.51), (-83.498, 35.563), 2200),
        ("new-york", "New York • Hudson and Manhattan", (-74.16, 40.63), (-74.01, 40.728), 900),
        ("keys", "Florida Keys", (-80.64, 24.94), (-80.78, 24.83), 850),
    ]),
    "alaska": ("Alaska", "EPSG:3338", (-151, 63), [(-180, 50, -128, 72.5), (170, 50, 180, 56)], [
        ("denali", "Alaska • Denali", (-151.31, 62.86), (-151.007, 63.069), 5100),
        ("aleutians", "Alaska • western Aleutian Islands", (172.7, 52.8), (173.12, 52.93), 1450),
    ]),
    "hawaii": ("Hawaii", HAWAII, (-157.85, 21.3), [(-179, 17, -153, 30)], [
        ("oahu", "Hawaii • Oahu / Honolulu", (-158.03, 21.22), (-157.83, 21.3), 1000),
        ("mauna-loa", "Hawaii • Mauna Loa", (-155.94, 19.56), (-155.608, 19.475), 3800),
    ]),
}

CITIES = {"seattle": (-122.344, 47.61), "san-francisco": (-122.403, 37.792)}
LANDMARKS = [
    ("Space Needle", "needle", -122.34933, 47.62051, 29),
    ("Seattle Great Wheel", "wheel", -122.34252, 47.60609, 21),
    ("Lumen Field", "stadium", -122.33163, 47.59520, 107),
    ("T-Mobile Park", "ballpark", -122.33253, 47.59141, 121),
    ("Amazon Spheres", "spheres", -122.339465, 47.61565, 16),
    ("Transamerica Pyramid", "pyramid", -122.40278, 37.79520, 26),
    ("Coit Tower", "coit", -122.40578, 37.80239, 19),
]
LANDMARK_AXES = {
    "ballpark": ((-122.333001, 47.59109), (-122.331825, 47.59185)),
    "spheres": ((-122.33972, 47.61548), (-122.33921, 47.61586)),
}


def landmarks_for_region(local):
    landmarks = [dict(name=name, kind=kind, x=x, z=z)
                 for name, kind, lon, lat, _ in LANDMARKS for x, z in [local(lon, lat)]]
    for location in landmarks:
        if axis := LANDMARK_AXES.get(location["kind"]):
            a, b = [local(*point) for point in axis]
            location["heading"] = math.atan2(b[0] - a[0], b[1] - a[1])
    a, b = local(-122.47761, 37.81362), local(-122.47884, 37.82501)
    landmarks.append(dict(name="Golden Gate Bridge", kind="golden-gate",
                          x=(a[0] + b[0]) / 2, z=(a[1] + b[1]) / 2,
                          heading=math.atan2(b[0] - a[0], b[1] - a[1]), span=math.dist(a, b)))
    return landmarks


def project(crs, lon_lat):
    a, b = transform(WGS, crs, [lon_lat[0]], [lon_lat[1]])
    return a[0], b[0]


def combined_bounds(crs, boxes):
    extents = [transform_bounds(WGS, crs, *bbox, densify_pts=101) for bbox in boxes]
    return tuple(func(rect[i] for rect in extents)
                 for i, func in enumerate((min, min, max, max)))


def tile_bounds(extent, origin, level, align):
    span = SPACING[level] * (SIDE - 1) * align
    return [(math.floor if i < 2 else math.ceil)((value - origin[i % 2]) / span) * align
            for i, value in enumerate(extent)]


def intersect(a, b):
    return [max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])]


def json_file(path, payload):
    with path.open("w", encoding="utf-8") as file:
        json.dump(payload, file, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
        file.write("\n")


def raster_input(path, expected_bounds=None):
    with rio.open(path) as dataset:
        if dataset.count != 1 or (expected_bounds is not None and
                                  not np.allclose(dataset.bounds, expected_bounds, rtol=0, atol=1e-6)):
            raise ValueError(f"Unexpected elevation raster layout: {path}")
        return dataset.read(1), dataset.transform, dataset.nodata


def warp(source, crs, affine, shape, dtype, method, fill, target=None):
    samples, matrix, no_data = source
    dst = target if target is not None else np.full(shape, fill, dtype=dtype)
    reproject(samples, dst, src_transform=matrix, src_crs=WGS, src_nodata=no_data,
              dst_transform=affine, dst_crs=crs, dst_nodata=fill, resampling=method,
              num_threads=2, init_dest_nodata=target is None)
    return dst


def water_input(raw):
    # Keep one global longitude band so Alaska's actual land and water also cross the dateline.
    affine = from_origin(-180, 90, 1 / 60, 1 / 60)
    shapes = []
    for theme, value in [("land", 0), ("lakes", 2)]:
        geo = json.loads((raw / f"ne-{theme}.geojson").read_text())
        shapes += [(feature["geometry"], value) for feature in geo["features"]]
    return rasterize(shapes, (5400, 21600), fill=1, transform=affine, dtype=np.uint8), affine, None


def meters(text):
    if not text:
        return None
    match = re.fullmatch(r"\s*(\d+(?:[.,]\d+)?)\s*(m|ft|')?\s*", text)
    if not match:
        return None
    value = float(match[1].replace(",", "."))
    return value * (0.3048 if match[2] in ("ft", "'") else 1)


def extract_buildings(city, inputs, output, local):
    nodes, ways = {}, {}
    for path in inputs:
        root = ET.parse(path).getroot()
        if root.tag != "osm":
            raise ValueError(f"Expected an OSM XML extract: {path}")
        for node in root.iterfind("node"):
            nodes[node.attrib["id"]] = float(node.attrib["lon"]), float(node.attrib["lat"])
        for way in root.iterfind("way"):
            tags = {tag.attrib["k"]: tag.attrib["v"] for tag in way.iterfind("tag")}
            if (tags.get("building") not in (None, "no", "construction", "roof") and
                    (city != "seattle" or tags.get("wikidata") != "Q48596158")):
                refs = [node.attrib["ref"] for node in way.iterfind("nd")]
                if 4 <= len(refs) <= 200 and refs[0] == refs[-1]:
                    ways[way.attrib["id"]] = tags, refs[:-1]
    ids = list(dict.fromkeys(ref for _, refs in ways.values() for ref in refs if ref in nodes))
    ll = [nodes[ref] for ref in ids]
    xx, zz = transform(WGS, REGIONS["conus"][1], [p[0] for p in ll], [p[1] for p in ll])
    origin = project(REGIONS["conus"][1], REGIONS["conus"][2])
    locations = dict(zip(ids, ((x - round(origin[0]), z - round(origin[1])) for x, z in zip(xx, zz))))
    authored = [(local(lon, lat), radius) for _, _, lon, lat, radius in LANDMARKS]
    buildings = []
    for id_, (tags, refs) in ways.items():
        if not all(ref in locations for ref in refs):
            continue
        ring = list(dict.fromkeys(locations[ref] for ref in refs))
        if len(ring) < 3:
            continue
        twice_area = sum(x * b - z * a for (x, z), (a, b) in zip(ring, ring[1:] + ring[:1]))
        if abs(twice_area) < 24:
            continue
        x, z = (sum(point[a] for point in ring) / len(ring) for a in range(2))
        if any(math.hypot(x - p[0], z - p[1]) < radius for p, radius in authored):
            continue
        height = meters(tags.get("height"))
        levels = meters(tags.get("building:levels"))
        evidence = "height" if height else "levels" if levels else "estimated"
        height = max(3, min(360, height or (levels * 3.4 if levels else 9 if abs(twice_area) > 400 else 5.5)))
        # Preserve alternating corners and measured polygon outlines; keep the ODbL database separate.
        buildings.append([int(id_), round(height, 1), evidence, [[round(a, 2), round(b, 2)] for a, b in ring]])
    if len(buildings) < 700 or sum(row[2] == "height" for row in buildings) < 20:
        raise ValueError(f"{city} building extract lacks sufficient actual footprint/height coverage.")
    json_file(output / f"{city}-buildings.json", {
        "license": "ODbL-1.0", "attribution": "© OpenStreetMap contributors",
        "licenseUrl": "https://www.openstreetmap.org/copyright",
        "modifications": "Filtered closed building ways, projected footprints; tag heights or annotated estimates; hand-built landmark footprints omitted.",
        "buildings": buildings,
    })
    x, z = local(*CITIES[city])
    return {"id": city, "x": x, "z": z, "featureCount": len(buildings),
            "taggedHeights": sum(row[2] == "height" for row in buildings)}


def seattle_road_terrain(folder):
    step = SPACING[-1]
    span = step * (SIDE - 1)

    @lru_cache(maxsize=128)
    def tile(x, z):
        data = (folder / "tiles" / "conus" / "8" / str(x) / f"{z}.bin").read_bytes()
        if len(data) != 16 + SIDE * SIDE * 3 or struct.unpack_from("<4siii", data) != (b"WFT1", 8, x, z):
            raise ValueError(f"Seattle bridge has an invalid elevation tile at {x}, {z}.")
        return data

    def sample(x, z):
        tx, tz = math.floor(x / span), math.floor(z / span)
        data = tile(tx, tz)
        u, v = (x - span * tx) / step, (z - span * tz) / step
        i, j = int(u), int(v)
        fx, fz = u - i, v - j
        index = j * SIDE + i
        a, b, c, d = (struct.unpack_from("<h", data, 16 + 2 * n)[0]
                      for n in (index, index + SIDE, index + SIDE + 1, index + 1))
        height = ((1 - fz) * a + (fz - fx) * b + fx * c if fx <= fz
                  else (1 - fx) * a + fz * c + (fx - fz) * d)
        wet = data[16 + 2 * SIDE * SIDE + round(v) * SIDE + round(u)] != 0
        return height, wet

    return sample


def make_catalog(output, raw):
    national = raster_input(raw / "etopo60.tif", (-180, -90, 180, 90))
    elevation_inputs = [(name, bbox, raster_input(raw / f"{name}-3dep.tif", bbox))
                        for name, (bbox, _) in (BASE_ELEVATION | DETAIL_ELEVATION).items()]
    elevation_inputs.append(("yosemite-tile", (-120.002, 36.998, -118.998, 38.002),
                             raster_input(raw / "yosemite-usgs-degree.tif")))
    water_data = water_input(raw)
    catalog, count, runway = [], 0, None
    for key, (name, crs, ref, extents, spots) in REGIONS.items():
        origin = tuple(round(v) for v in project(crs, ref))

        def local(lon, lat):
            x, z = project(crs, (lon, lat))
            return x - origin[0], z - origin[1]

        initial = tile_bounds(combined_bounds(crs, extents), origin, 0, 1)
        coverage = [initial, [v * 2 for v in initial], [v * 4 for v in initial]]
        patches = [[] for _ in SPACING]
        if key == "conus":
            for level, boxes in PACIFIC_NORTHWEST.items():
                scopes = [tile_bounds(combined_bounds(crs, [bbox]), origin, level, 2) for bbox in boxes]
                coverage.append(intersect(scopes[0], [v * 2 for v in coverage[-1]]))
                if len(scopes) > 1:
                    parent = patches[level - 1][0] if level == 8 else coverage[level - 1]
                    patches[level].append(intersect(scopes[1], [v * 2 for v in parent]))
            for area, bbox in WESTERN_DETAIL.items():
                parent = coverage[2]
                for level in range(3, 9):
                    window = WESTERN_CORES[area] if level >= 7 else bbox
                    scope = tile_bounds(combined_bounds(crs, [window]), origin, level, 2)
                    parent = intersect(scope, [v * 2 for v in parent])
                    patches[level].append(parent)
            a, b = local(-122.291252, 47.516745), local(-122.311372, 47.540549)
            center = [(x + y) / 2 for x, y in zip(a, b)]
            runway = dict(name="Boeing Field / 32L", centerX=center[0], centerZ=center[1], width=61,
                          length=math.dist(a, b), height=6.4, heading=math.atan2(b[0] - a[0], b[1] - a[1]))
            cities = [extract_buildings(city, [raw / f"{part}.xml" for part in extracts], output, local)
                      for city, extracts in OSM_EXTRACTS.items()]

        windows = [(level, box) for level, base in enumerate(coverage) for box in [base, *patches[level]]]
        for level, box in windows:
            step, span = SPACING[level], SPACING[level] * (SIDE - 1)
            x0, z0, x1, z1 = box
            width, height = (x1 - x0) * (SIDE - 1) + 1, (z1 - z0) * (SIDE - 1) + 1
            affine = from_origin(origin[0] + x0 * span - step / 2, origin[1] + z1 * span + step / 2, step, step)
            shape = height, width
            dem = warp(national, crs, affine, shape, "float32", Resampling.bilinear, -99999)
            water = warp(water_data, crs, affine, shape, "uint8", Resampling.nearest, 255)
            if key == "alaska":
                # Warp in two longitude domains: finite global rasters can leave a one-pixel
                # no-data seam where a destination pixel straddles the antimeridian.
                for values, source, method, missing in ((dem, national, Resampling.bilinear, -99999),
                                                        (water, water_data, Resampling.nearest, 255)):
                    if not np.any(values == missing):
                        continue
                    samples, matrix, no_data = source
                    half = samples.shape[1] // 2
                    shifted = np.concatenate((samples[:, half:], samples[:, :half]), axis=1)
                    alternate = rio.Affine(matrix.a, matrix.b, 0, matrix.d, matrix.e, matrix.f)
                    repair = warp((shifted, alternate, no_data), crs, affine, shape,
                                  values.dtype, method, missing)
                    values[values == missing] = repair[values == missing]
            if np.any(water == 255):
                row, col = np.where(water == 255)
                longitude, latitude = transform(crs, WGS,
                    (affine.c + (col + .5) * affine.a).tolist(),
                    (affine.f + (row + .5) * affine.e).tolist())
                c = np.floor((np.asarray(longitude) + 180) * 60).astype(int) % water_data[0].shape[1]
                r = np.floor((90 - np.asarray(latitude)) * 60).astype(int)
                if np.any(r < 0) or np.any(r >= water_data[0].shape[0]):
                    raise ValueError(f"{key} tile bounds leave the source coastline domain.")
                water[row, col] = water_data[0][r, c]
            if not np.all(np.isfinite(dem) & (dem >= -12000) & (dem <= 10000)) or np.any(water > 2):
                bad = ~(np.isfinite(dem) & (dem >= -12000) & (dem <= 10000) & (water <= 2))
                row, col = np.argwhere(bad)[0]
                lon, lat = transform(crs, WGS, *([v] for v in affine * (col + .5, row + .5)))
                raise ValueError(f"{np.count_nonzero(bad)} invalid elevation/water cells in {key}/{level}; "
                                 f"first at {lon[0]}, {lat[0]}: {dem[row,col]}, {water[row,col]}.")
            if key == "conus":
                # USGS topography takes precedence, with a short crossfade into the national basemap at raster borders.
                for name_, bbox, source in elevation_inputs:
                    # Restrict work to windows with data and sample a halo so the blend
                    # has the same weights on either side of neighboring patch boundaries.
                    bounds = combined_bounds(crs, [bbox])
                    west, south, east, north = bounds
                    if (affine.c > east or affine.c + width * step < west or affine.f < south or
                            affine.f - height * step > north):
                        continue
                    halo = math.ceil(2200 / step) + 1
                    expanded = affine * rio.Affine.translation(-halo, -halo)
                    usgs = warp(source, crs, expanded, (height + 2 * halo, width + 2 * halo),
                                "float32", Resampling.bilinear, -99999)
                    # The blended service returns zero in uncovered inland source cells.
                    # Keep lower-resolution land there; the staged 1-arc-second file fills
                    # Yosemite Valley instead of flattening the valley to sea level.
                    where = usgs > (120 if name_.startswith("yosemite") else -50)
                    if not where.any():
                        continue
                    weight = np.minimum(1, distance_transform_edt(where) * step / 2200).astype(np.float32)[halo:-halo, halo:-halo]
                    usgs, where = usgs[halo:-halo, halo:-halo], where[halo:-halo, halo:-halo]
                    wet = where & (weight > .5)
                    dem = np.where(where, dem + (usgs - dem) * weight, dem)
                    if name_ in ("pnw", "seattle"):
                        # The Seattle tidal surface includes values just below the datum.
                        water[wet] = np.where(usgs[wet] <= .15, 1, np.where(water[wet] == 2, 2, 0))
                    elif name_ == "san-francisco":
                        water[wet] = np.where((usgs[wet] <= .15) | ((water[wet] == 1) & (usgs[wet] <= 2.5)),
                                              1, np.where(water[wet] == 2, 2, 0))
                # Lake Washington and Lake Union have nonzero flat water in 3DEP. Classify only the
                # same near-level raster cells within their geographic bounding neighborhoods.
                if level >= 7:
                    for x, z, radius_x, radius_z, h in [(*local(-122.244, 47.606), 4800, 15300, 5),
                                                         (*local(-122.334, 47.640), 1270, 1350, 5)]:
                        vx = (np.arange(width) * step + x0 * span - x) / radius_x
                        vz = (z1 * span - np.arange(height) * step - z) / radius_z
                        lake = (vx[None, :] ** 2 + vz[:, None] ** 2 < 1) & (abs(dem - h) < .24)
                        water[lake] = 2
            dem[water == 1] = 0
            if key == "conus":
                vx = (np.arange(width) * step + x0 * span - runway["centerX"])[None, :]
                vz = (z1 * span - np.arange(height) * step - runway["centerZ"])[:, None]
                sine, cosine = math.sin(runway["heading"]), math.cos(runway["heading"])
                edge = np.maximum(abs(vx * cosine - vz * sine) - runway["width"] / 2,
                                  abs(vx * sine + vz * cosine) - runway["length"] / 2)
                fade = np.clip(edge / 36, 0, 1)
                dem = runway["height"] - 0.6 + (dem - runway["height"] + 0.6) * fade
                water[edge <= 0] = 0
            dem = np.rint(dem[::-1]).astype("<i2")
            water = water[::-1]
            for ix in range(x0, x1):
                folder = output / "tiles" / key / str(level) / str(ix)
                folder.mkdir(parents=True, exist_ok=True)
                start = (ix - x0) * (SIDE - 1)
                for iz in range(z0, z1):
                    row = (iz - z0) * (SIDE - 1)
                    section = np.s_[row:row + SIDE, start:start + SIDE]
                    with (folder / f"{iz}.bin").open("wb") as file:
                        file.write(struct.pack("<4siii", b"WFT1", level, ix, iz))
                        file.write(dem[section].tobytes())
                        file.write(water[section].tobytes())
                    count += 1
            print(f"{key} L{level} ({step} m): {(x1-x0)*(z1-z0)} tiles", flush=True)

        destinations = []
        for id_, label, start, look, altitude in spots:
            x, z = local(*start)
            tx, tz = local(*look)
            destinations.append(dict(id=id_, name=label, x=x, z=z, altitude=altitude,
                                     heading=math.atan2(tx - x, tz - z), targetX=tx, targetZ=tz))
        region = dict(id=key, name=name, coverage=coverage, patches=patches[:len(coverage)], destinations=destinations,
                      landmarks=landmarks_for_region(local) if key == "conus" else [],
                      runway=runway if key == "conus" else None,
                      cities=cities if key == "conus" else [])
        if key == "conus":
            def project_roads(points):
                if not points:
                    return []
                xx, zz = transform(WGS, crs, *map(list, zip(*points)))
                return [(x - origin[0], z - origin[1]) for x, z in zip(xx, zz)]

            region["roads"] = extract_roads(seattle_road_inputs(raw), output, project_roads, seattle_road_terrain(output))
        catalog.append(region)
    return catalog, count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    # Publish a manifest only after every tile and separate scenery database has been written.
    args.output.mkdir(parents=True)
    sources = {}
    for name in ["etopo60.tif", "ne-land.geojson", "ne-lakes.geojson", "yosemite-usgs-degree.tif",
                 *(f"{name}-3dep.tif" for name in (BASE_ELEVATION | DETAIL_ELEVATION)),
                 *(f"{name}.xml" for extracts in OSM_EXTRACTS.values() for name in extracts),
                 *(path.name for path in seattle_road_inputs(args.input))]:
        with (args.input / name).open("rb") as file:
            sources[name] = hashlib.file_digest(file, "sha256").hexdigest()
    code = b"".join(Path(__file__).with_name(file).read_bytes() for file in ("build.py", "areas.py", "roads.py"))
    identity = hashlib.sha256(code + json.dumps(sources, sort_keys=True).encode()).hexdigest()[:20]
    regions, count = make_catalog(args.output, args.input)
    json_file(args.output / "manifest.json", {
        "schemaVersion": 1, "kind": "world-flight-terrain", "version": identity, "spacing": SPACING,
        "side": SIDE, "regions": regions,
    })
    json_file(args.output / "receipt.json", {"dataRevision": identity, "inputSha256": sources, "tilesWritten": count})
    print(f"Published {count} tiles and data revision {identity} to {args.output}")


if __name__ == "__main__":
    main()
