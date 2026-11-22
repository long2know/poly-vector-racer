"""Public source URLs, credits, and per-input provenance."""

import hashlib
import json
import shutil
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

NOAA = ("https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO2022/data/60s/"
        "60s_surface_elev_gtif/ETOPO_2022_v1_60s_N90W180_surface.tif")
NE = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector"
NE_VERSION = "v5.1.2"
NE_VECTORS = {"roads": "roads", "places": "populated_places_simple",
              "airports": "airports", "physical": "geography_regions_points"}
NE_EXTENDED = {"peaks": "geography_regions_elevation_points", "railroads": "railroads"}
GEOFABRIK = "https://download.geofabrik.de"
COPERNICUS = "https://copernicus-dem-30m.s3.eu-central-1.amazonaws.com"
COP_NOTICE = ("produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and "
              "Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved")
OSM_NOTICE = ("© OpenStreetMap contributors. OpenStreetMap-derived feature data is available under "
              "the Open Database License (ODbL) 1.0. https://www.openstreetmap.org/copyright")
CREDITS = {
    "noaa-etopo": ["NOAA NCEI ETOPO 2022 v1 60 arc-second Ice Surface", "U.S. Government data",
                   "https://www.ncei.noaa.gov/products/etopo-global-relief-model"],
    "natural-earth": ["Made with Natural Earth", "Public domain",
                      "https://www.naturalearthdata.com/about/terms-of-use/"],
    "osm": ["© OpenStreetMap contributors", "ODbL 1.0", "https://www.openstreetmap.org/copyright"],
    "usgs-3dep": ["U.S. Geological Survey 3D Elevation Program", "Public domain",
                  "https://www.usgs.gov/3d-elevation-program"],
    "copernicus-dem": [COP_NOTICE, "Copernicus DEM GLO-30 Public License",
                       "https://dataspace.copernicus.eu/explore-data/data-collections/"
                       "copernicus-contributing-missions/collections-description/COP-DEM"],
}
GEOS = {
    "washington": "north-america/us/washington/washington-latest.osm.pbf",
    "panama": "central-america/panama/panama-latest.osm.pbf",
    "peru": "south-america/peru/peru-latest.osm.pbf",
    "sudeste": "brazil/sudeste/sudeste-latest.osm.pbf",
}


def require_https(url):
    if not isinstance(url, str):
        raise ValueError("Source URL must be a public HTTPS string.")
    parsed = urlsplit(url)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("Source URL must be public HTTPS without credentials.")
    return url


def file_digest(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


@dataclass(frozen=True)
class Source:
    key: str
    path: Path
    url: str
    release: str = ""

    def receipt(self):
        record = {"source": self.key, "file": self.path.name, "url": require_https(self.url),
                  "bytes": self.path.stat().st_size, "sha256": file_digest(self.path)}
        if self.release:
            record["release"] = self.release
        sidecar = self.path.with_suffix(self.path.suffix + ".download.json")
        if sidecar.is_file():
            metadata = json.loads(sidecar.read_text())
            if metadata.get("sha256") == record["sha256"] and metadata.get("url") == self.url:
                for field in ("lastModified", "etag"):
                    if metadata.get(field):
                        record[field] = metadata[field]
        return record


def terrain_sources(raw):
    # Prefer the cached URL receipt when reusing World Flight inputs.
    path = raw / "sources.json"
    urls = json.loads(path.read_text()) if path.is_file() else {}
    result = [Source("noaa-etopo", raw / "etopo60.tif", NOAA, "2022 v1")]
    for theme in ("land", "lakes"):
        name = f"ne-{theme}.geojson"
        pinned = f"{NE}/{NE_VERSION}/geojson/ne_10m_{theme}.geojson"
        url = urls.get(name, pinned)
        result.append(Source("natural-earth", raw / name, url, NE_VERSION if url == pinned else "cached master snapshot"))
    return result


def vector_sources(raw, extended=False):
    return {
        theme: Source("natural-earth", raw / f"ne_10m_{suffix}.geojson",
                      f"{NE}/{NE_VERSION}/geojson/ne_10m_{suffix}.geojson", NE_VERSION)
        for theme, suffix in (NE_VECTORS | (NE_EXTENDED if extended else {})).items()
    }


def osm_source(raw, name):
    return Source("osm", raw / f"{name}.osm.pbf", f"{GEOFABRIK}/{GEOS[name]}")


def cop_source(raw, coord, *, bucket=COPERNICUS, release="GLO-30"):
    stem = f"Copernicus_DSM_COG_10_{coord}_DEM"
    return Source("copernicus-dem", raw / f"{stem}.tif", f"{bucket}/{stem}/{stem}.tif", release)


def credits(sources):
    return {key: CREDITS[key] for key in sorted({source.key for source in sources})}


def notice(pack_name, credit_rows):
    lines = [f"World Atlas data — {pack_name}", "Derived game scenery, not for navigation.", ""]
    for key, (label, license_name, url) in credit_rows.items():
        lines += [f"{label} — {license_name}", url]
        if key == "osm":
            lines += [OSM_NOTICE, "ODbL 1.0 text: https://opendatacommons.org/licenses/odbl/1-0/",
                      "The independently sourced ODbL derivative database is provided in this ZIP's "
                      "features/ entries, separate from the terrain entries and the base Natural Earth vectors."]
        lines.append("")
    return ("\n".join(lines) + "\n").encode()


def fetch(source, *, refresh=False, pin=None, max_bytes=2 * 1024 ** 3):
    """One bounded whole-file HTTPS request; no tiled map/API scraping."""
    path = source.path
    if path.is_file() and not refresh:
        if pin and file_digest(path) != pin:
            raise ValueError(f"{path}: cached SHA-256 differs from the requested pin")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    part = path.with_suffix(path.suffix + ".part")
    request = Request(require_https(source.url), headers={"User-Agent": "Poly Vector Racer World Atlas compiler/1"})
    with part.open("xb") as output:
        try:
            with urlopen(request, timeout=120) as response:
                require_https(response.geturl())
                supplied = response.headers.get("Content-Length")
                if supplied and int(supplied) > max_bytes:
                    raise ValueError(f"{path}: HTTP response exceeds the download budget")
                shutil.copyfileobj(_BoundedReader(response, max_bytes), output, 1024 * 1024)
                metadata = {"url": source.url, "lastModified": response.headers.get("Last-Modified"),
                            "etag": response.headers.get("ETag")}
            output.close()
            fp = file_digest(part)
            if pin and fp != pin:
                raise ValueError(f"{path}: downloaded SHA-256 differs from the requested pin")
            if not part.stat().st_size:
                raise ValueError(f"{path}: empty download")
            part.replace(path)
            path.with_suffix(path.suffix + ".download.json").write_text(
                json.dumps(metadata | {"sha256": fp}, indent=2) + "\n")
        finally:
            part.unlink(missing_ok=True)


class _BoundedReader:
    def __init__(self, response, budget):
        self.response, self.budget = response, budget

    def read(self, size):
        chunk = self.response.read(min(self.budget + 1, size))
        self.budget -= len(chunk)
        if self.budget < 0:
            raise ValueError("HTTP response exceeds the download budget")
        return chunk
