#!/usr/bin/env python3
"""Fetch numeric elevation and public map extracts for the offline World Flight builder."""

import argparse
import json
import shutil
import time
from pathlib import Path
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen

from areas import BASE_ELEVATION, DETAIL_ELEVATION, OSM_EXTRACTS, YOSEMITE_DEM

NOAA = ("https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO2022/data/60s/"
        "60s_surface_elev_gtif/ETOPO_2022_v1_60s_N90W180_surface.tif")
USGS = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage"
NATURAL_EARTH = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson"


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    directory = parser.parse_args().output
    directory.mkdir(parents=True, exist_ok=True)
    fetch(NOAA, directory / "etopo60.tif")
    sources = {"etopo60.tif": NOAA}
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
            name, url = f"{area}.xml", f"https://api.openstreetmap.org/api/0.6/map?bbox={bbox}"
            if not (directory / name).is_file():
                fetch(url, directory / name)
                time.sleep(1)
            sources[name] = url
    (directory / "sources.json").write_text(json.dumps(sources, indent=2) + "\n")


if __name__ == "__main__":
    main()
