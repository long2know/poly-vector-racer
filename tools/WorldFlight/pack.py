#!/usr/bin/env python3
"""Package a compiled World Flight directory as a ZIP that the host can read directly."""

import argparse
import hashlib
import json
import math
import os
import re
import stat
import struct
import zipfile
from pathlib import Path

from roads import ATTRIBUTION, LICENSE_URL, MAX_BYTES, validate_roads

REGIONS = {"conus", "alaska", "hawaii"}
CITIES = {"seattle", "san-francisco"}
JSON_MAX = 16 * 1024 * 1024
TILE_SIZE = 16 + 33 * 33 * 3


def require_type(path, predicate):
    info = path.lstat()
    if not predicate(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError(f"{path}: expected a regular file or directory; links are not allowed.")
    return info


def read(folder, name, limit):
    path = folder / name
    for parent in path.parents:
        if parent == folder:
            break
        require_type(parent, stat.S_ISDIR)
    size = require_type(path, stat.S_ISREG).st_size
    if not 1 <= size <= limit:
        raise ValueError(f"{name}: invalid package file size ({size} bytes).")
    with path.open("rb") as file:
        data = file.read(size + 1)
    if len(data) != size:
        raise ValueError(f"{name}: file size changed while reading.")
    return data


def read_json(folder, name, limit=JSON_MAX):
    def unique(pairs):
        fields = {}
        for key, value in pairs:
            if key in fields:
                raise ValueError(f"duplicate JSON key {key!r}")
            fields[key] = value
        return fields

    def invalid_number(token):
        raise ValueError(f"invalid JSON number {token}")

    data = read(folder, name, limit)
    try:
        document = json.loads(data.decode("utf-8"), object_pairs_hook=unique, parse_constant=invalid_number)
        if not isinstance(document, dict):
            raise ValueError("expected a JSON object")
    except (ValueError, RecursionError) as error:
        raise ValueError(f"{name}: {error}") from error
    return data, document


def rectangle(box):
    return (isinstance(box, list) and len(box) == 4 and
            all(type(value) is int and -4096 <= value <= 4096 for value in box) and
            box[0] < box[2] and box[1] < box[3])


def tile_windows(regions):
    for region_id, region in regions.items():
        coverage = region.get("coverage")
        if not isinstance(coverage, list) or not 3 <= len(coverage) <= 9:
            raise ValueError(f"{region_id}: invalid tile coverage.")
        patches = region.get("patches", [[]] * len(coverage))
        if not isinstance(patches, list) or len(patches) != len(coverage):
            raise ValueError(f"{region_id}: patches must be grouped by tile level.")
        parents = []
        for level, (base, detail) in enumerate(zip(coverage, patches)):
            if not isinstance(detail, list) or len(detail) > (0 if level < 3 else 8):
                raise ValueError(f"{region_id}/{level}: invalid detail patch count.")
            boxes = [base, *detail]
            for index, box in enumerate(boxes):
                if not rectangle(box):
                    raise ValueError(f"{region_id}/{level}: invalid tile rectangle.")
                west, south, east, north = box
                if any(west < a[2] and east > a[0] and south < a[3] and north > a[1] for a in boxes[:index]):
                    raise ValueError(f"{region_id}/{level}: overlapping tile rectangles.")
                if level and (any(v % 2 for v in box) or not any(
                        p[0] * 2 <= west < east <= p[2] * 2 and p[1] * 2 <= south < north <= p[3] * 2 for p in parents)):
                    raise ValueError(f"{region_id}/{level}: incomplete parent coverage.")
                yield region_id, level, box
            parents = boxes


def validate_buildings(city, data):
    rows = data.get("buildings")
    if (data.get("license") != "ODbL-1.0" or data.get("attribution") != ATTRIBUTION or
            data.get("licenseUrl") != LICENSE_URL or not isinstance(rows, list) or
            len(rows) != city.get("featureCount", len(rows))):
        raise ValueError(f"{city['id']}: malformed buildings or missing ODbL attribution.")

    def numeric(value):
        return type(value) in (int, float) and math.isfinite(value)

    for row in rows:
        if (not isinstance(row, list) or len(row) != 4 or type(row[0]) is not int or
                not 1 <= row[0] < 2**53 or not numeric(row[1]) or not 3 <= row[1] <= 360 or
                row[2] not in ("height", "levels", "estimated") or not isinstance(row[3], list) or len(row[3]) < 3 or
                any(not isinstance(point, list) or len(point) != 2 or not all(map(numeric, point)) for point in row[3])):
            raise ValueError(f"{city['id']}: malformed building footprint.")
    measured = sum(row[2] == "height" for row in rows)
    if measured != city.get("taggedHeights", measured):
        raise ValueError(f"{city['id']}: building height count differs from the manifest.")


def write_entry(archive, name, data):
    info = zipfile.ZipInfo(name)
    info.create_system = 3
    info.external_attr = (stat.S_IFREG | 0o644) << 16
    archive.writestr(info, data, zipfile.ZIP_DEFLATED, 6)


def pack(folder, output):
    folder = Path(os.path.abspath(folder))
    for directory in (*reversed(folder.parents), folder):
        require_type(directory, stat.S_ISDIR)
    if output.suffix.lower() != ".zip" or output.resolve().is_relative_to(folder):
        raise ValueError("Output must be a .zip file outside the compiled package directory.")

    manifest_bytes, manifest = read_json(folder, "manifest.json")
    receipt_bytes, receipt = read_json(folder, "receipt.json")
    version, spacing = manifest.get("version"), manifest.get("spacing")
    if (type(manifest.get("schemaVersion")) is not int or manifest["schemaVersion"] != 1 or
            manifest.get("kind") != "world-flight-terrain" or type(manifest.get("side")) is not int or
            manifest["side"] != 33 or not isinstance(version, str) or not re.fullmatch(r"[0-9a-f]{20}", version) or
            spacing != [8192 >> level for level in range(9)] or any(type(v) is not int for v in spacing)):
        raise ValueError("manifest.json: unsupported World Flight schema, revision or grid.")
    regions = manifest.get("regions")
    if (not isinstance(regions, list) or len(regions) != len(REGIONS) or
            any(not isinstance(r, dict) or r.get("id") not in ("conus", "alaska", "hawaii") for r in regions) or
            {r["id"] for r in regions} != REGIONS or any("roads" in r for r in regions if r["id"] != "conus")):
        raise ValueError("manifest.json: invalid or duplicate World Flight regions.")
    regions = {r["id"]: r for r in regions}
    windows = list(tile_windows(regions))
    count = sum((east - west) * (north - south) for _, _, (west, south, east, north) in windows)
    hashes = receipt.get("inputSha256")
    if (set(receipt) != {"dataRevision", "tilesWritten", "inputSha256"} or receipt.get("dataRevision") != version or
            type(receipt.get("tilesWritten")) is not int or receipt["tilesWritten"] != count or
            not isinstance(hashes, dict) or not hashes or any(
                not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", name) or not isinstance(digest, str) or
                not re.fullmatch(r"[0-9a-f]{64}", digest) for name, digest in hashes.items())):
        raise ValueError("receipt.json: invalid source hashes, revision or declared tile count.")
    conus = regions["conus"]
    cities = conus.get("cities", [{"id": "seattle"}])
    if (not isinstance(cities, list) or any(
            not isinstance(city, dict) or city.get("id") not in ("seattle", "san-francisco") for city in cities) or
            len({city["id"] for city in cities}) != len(cities)):
        raise ValueError("manifest.json: invalid or duplicate building areas.")

    output.parent.mkdir(parents=True, exist_ok=True)
    file = output.open("x+b")
    try:
        with file:
            with zipfile.ZipFile(file, "w") as archive:
                write_entry(archive, "manifest.json", manifest_bytes)
                write_entry(archive, "receipt.json", receipt_bytes)
                write_entry(archive, "DATA-NOTICE.txt", Path(__file__).with_name("DATA-NOTICE.txt").read_bytes())
                for city in cities:
                    name = f"{city['id']}-buildings.json"
                    data, buildings = read_json(folder, name, MAX_BYTES)
                    validate_buildings(city, buildings)
                    write_entry(archive, name, data)
                if "roads" in conus:
                    name = "seattle-roads.json"
                    data, roads = read_json(folder, name, MAX_BYTES)
                    validate_roads(conus["roads"], roads)
                    write_entry(archive, name, data)
                for region, level, (west, south, east, north) in windows:
                    for x in range(west, east):
                        for z in range(south, north):
                            name = f"tiles/{region}/{level}/{x}/{z}.bin"
                            tile = read(folder, name, TILE_SIZE)
                            if (len(tile) != TILE_SIZE or struct.unpack_from("<4siii", tile) != (b"WFT1", level, x, z) or
                                    max(tile[-33 * 33:]) > 2):
                                raise ValueError(f"{name}: malformed WFT1 tile.")
                            write_entry(archive, name, tile)
            size = file.tell()
            file.seek(0)
            digest = hashlib.file_digest(file, "sha256").hexdigest()
    except BaseException:
        output.unlink()
        raise
    print(f"{output}: revision {version}, {count:,} tiles, {size:,} bytes, SHA-256 {digest}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="compiled package directory")
    parser.add_argument("--output", type=Path, required=True, help="new .zip file (never overwritten)")
    args = parser.parse_args()
    try:
        pack(args.input, args.output)
    except (OSError, ValueError, OverflowError) as error:
        parser.exit(1, f"World Flight pack: {error}\n")
