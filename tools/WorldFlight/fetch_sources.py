#!/usr/bin/env python3
"""Fetch numeric elevation and public map extracts for the offline World Flight builder."""

import argparse
import json
import shutil
import time
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen

from areas import BASE_ELEVATION, DETAIL_ELEVATION, OSM_EXTRACTS, ROAD_SPLIT_LIMIT, SEATTLE_ROAD_EXTRACTS, YOSEMITE_DEM

NOAA = ("https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO2022/data/60s/"
        "60s_surface_elev_gtif/ETOPO_2022_v1_60s_N90W180_surface.tif")
USGS = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage"
NATURAL_EARTH = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson"
OSM_MAP = "https://api.openstreetmap.org/api/0.6/map?bbox="


def fetch(url, output):
    if output.is_file():
        return
    print(f"Downloading {output.name} …", flush=True)
    request = Request(url, headers={"User-Agent": "stunt-car-racer World Flight geography builder"})
    part = output.with_suffix(output.suffix + ".part")
    try:
        with urlopen(request, timeout=100) as response, part.open("wb") as file:
            shutil.copyfileobj(response, file, 256 * 1024)
        part.replace(output)
    finally:
        part.unlink(missing_ok=True)


def elevation(directory, name, bbox, size):
    query = urlencode({
        "f": "json", "bbox": bbox, "bboxSR": "4326", "imageSR": "4326", "size": size,
        "format": "tiff", "pixelType": "F32", "interpolation": "RSP_BilinearInterpolation",
        "adjustAspectRatio": "false", "renderingRule": '{"rasterFunction":"None"}',
    })
    output = directory / f"{name}-3dep.tif"
    if not output.is_file():
        with urlopen(f"{USGS}?{query}", timeout=130) as response:
            data = json.load(response)
        href = urlsplit(data.get("href", ""))
        if href.scheme != "https" or href.netloc != "elevation.nationalmap.gov" or not href.path.endswith(".tif"):
            raise RuntimeError(f"3DEP produced no numerical GeoTIFF for {name}: {data}")
        fetch(href.geturl(), output)
    return f"{USGS}?{query}"


def fetch_map(url, path):
    if path.is_file():
        return
    for delay in (20, 40, None):
        try:
            fetch(url, path)
            time.sleep(2)
            return
        except HTTPError as error:
            if error.code != 429 or delay is None:
                raise
            retry_after = error.headers.get("Retry-After", "")
            if retry_after.isdecimal():
                delay = int(retry_after)
            if delay > 120:
                raise  # Leave fetched windows cached for a later run.
            print(f"OSM rate limited the extract; retrying in {delay} seconds …", flush=True)
            time.sleep(delay)


def road_extract(directory, name, bbox, depth=0):
    filename = f"{name}.xml"
    path = directory / filename
    url = OSM_MAP + bbox
    if path.is_file():
        return {filename: url}
    if not any(directory.glob(f"{name}-*.xml")):
        try:
            fetch_map(url, path)
            return {filename: url}
        except HTTPError as error:
            if (depth == ROAD_SPLIT_LIMIT or error.code != 400 or
                    b"too many nodes" not in error.read(256)):
                raise
            time.sleep(1)
    if depth == ROAD_SPLIT_LIMIT:
        raise ValueError(f"OSM road extract {name} has too many cached subdivisions.")
    a = list(map(float, bbox.split(",")))
    b = a.copy()
    axis = 0 if (a[2] - a[0]) * .67 > a[3] - a[1] else 1
    a[axis + 2] = b[axis] = (a[axis] + a[axis + 2]) / 2
    return (road_extract(directory, f"{name}-a", ",".join(f"{p:.6f}" for p in a), depth + 1) |
            road_extract(directory, f"{name}-b", ",".join(f"{p:.6f}" for p in b), depth + 1))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--only-osm", action="store_true", help="fetch map extracts without the elevation inputs")
    args = parser.parse_args()
    directory = args.output
    directory.mkdir(parents=True, exist_ok=True)
    receipt = directory / "sources.json"
    sources = json.loads(receipt.read_text()) if receipt.is_file() else {}
    if not args.only_osm:
        fetch(NOAA, directory / "etopo60.tif")
        sources["etopo60.tif"] = NOAA
        for name, (bbox, size) in (BASE_ELEVATION | DETAIL_ELEVATION).items():
            sources[f"{name}-3dep.tif"] = elevation(directory, name, ",".join(map(str, bbox)), ",".join(map(str, size)))
        fetch(YOSEMITE_DEM, directory / "yosemite-usgs-degree.tif")
        sources["yosemite-usgs-degree.tif"] = YOSEMITE_DEM
        for theme in ("land", "lakes"):
            name, url = f"ne-{theme}.geojson", f"{NATURAL_EARTH}/ne_10m_{theme}.geojson"
            fetch(url, directory / name)
            sources[name] = url
    for extracts in OSM_EXTRACTS.values():
        for area, bbox in extracts.items():
            name, url = f"{area}.xml", OSM_MAP + bbox
            fetch_map(url, directory / name)
            sources[name] = url
    for name, bbox in SEATTLE_ROAD_EXTRACTS.items():
        sources.update(road_extract(directory, name, bbox))
    receipt.write_text(json.dumps(sources, indent=2) + "\n")


if __name__ == "__main__":
    main()
