#!/usr/bin/env python3
"""Validate World Atlas ZIP inventory, geometry, seams and local source fingerprints."""

import argparse
import json
import re
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from zipfile import BadZipFile, ZipFile

import numpy as np

from atlas import (BASE_BYTES, BUILDING_HEIGHT_SOURCES, FEATURE_STYLES, GRID, KIND, MAX_BYTES, MAX_ENTRIES,
                   MAX_FEATURE_BYTES, MAX_POINTS, MAX_ROWS, SEATTLE_ARCHIVE, SLUG, TILE_BYTES, decode_base, decode_terrain,
                   digest, extent, feature_path, interpolate, round_height, span, tile_path,
                   valid_building_height)
from sources import file_digest, require_https
from terrain import parent_edge

SHA = re.compile(r"[0-9a-f]{64}\Z")
MANIFEST_FIELDS = {"kind", "schemaVersion", "grid", "id", "name", "baseSha256",
                   "terrain", "features", "presets", "credits"}


def require(test, message):
    if not test:
        raise ValueError(message)


def integers(values, size):
    return isinstance(values, list) and len(values) == size and all(type(i) is int for i in values)


def label(value, limit=120):
    return (isinstance(value, str) and bool(value.strip()) and value.isprintable() and
            len(value.encode("utf-16-le")) <= 2 * limit)


def check_basic(manifest, path):
    require(isinstance(manifest, dict) and set(manifest) == MANIFEST_FIELDS, "manifest.json: wrong fields")
    require(manifest["kind"] == KIND and type(manifest["schemaVersion"]) is int and manifest["schemaVersion"] == 1
            and manifest["grid"] == GRID, "manifest.json: unsupported kind, schema or grid")
    pack_id = manifest["id"]
    require(label(pack_id, 64) and SLUG.fullmatch(pack_id) and path.name == f"{pack_id}.zip",
            "manifest.json: filename and lowercase pack ID must match")
    require(label(manifest["name"], 100), "manifest.json: invalid name")
    sha = manifest["baseSha256"]
    require(isinstance(sha, str) and SHA.fullmatch(sha), "manifest.json: invalid base SHA-256")
    require(isinstance(manifest["presets"], list) and isinstance(manifest["terrain"], list)
            and isinstance(manifest["features"], list), "manifest.json: invalid lists")
    seen = set()
    for preset in manifest["presets"]:
        require(isinstance(preset, dict) and set(preset) == {"id", "name", "lon", "lat"} and
                label(preset["id"], 64) and SLUG.fullmatch(preset["id"]) and preset["id"] not in seen and
                label(preset["name"], 100) and type(preset["lon"]) in (int, float) and -180 <= preset["lon"] <= 180 and
                type(preset["lat"]) in (int, float) and -90 <= preset["lat"] <= 90, "manifest.json: invalid preset")
        seen.add(preset["id"])
    require(isinstance(manifest["credits"], dict) and bool(manifest["credits"]), "manifest.json: missing credits")
    for source, credit in manifest["credits"].items():
        require(label(source, 64) and SLUG.fullmatch(source) and isinstance(credit, list) and len(credit) == 3 and
                label(credit[0], 384) and label(credit[1], 100), f"manifest.json: invalid credit for {source}")
        require_https(credit[2])


def addresses(manifest):
    terrain, features, groups = set(), set(), set()
    for region in manifest["terrain"]:
        require(integers(region, 5), "manifest.json: invalid terrain rectangle")
        level, w, s, e, n = region
        require(1 <= level <= 8, f"manifest.json: terrain level {level} outside 1–8")
        xlim, zlim = extent(level)
        require(-xlim <= w < e <= xlim and -zlim <= s < n <= zlim and not any(i % 2 for i in (w, s, e, n)),
                f"manifest.json: terrain bounds outside canonical/even grid {region}")
        require(len(terrain) + (e - w) * (n - s) <= MAX_ENTRIES, "manifest.json: terrain inventory too large")
        keys = {(level, x, z) for x in range(w, e) for z in range(s, n)}
        require(not terrain & keys, f"manifest.json: overlapping terrain rectangle {region}")
        terrain.update(keys)
        groups.update((level, x, z) for x in range(w, e, 2) for z in range(s, n, 2))
    for level, x, z in groups:
        require(level == 1 or (level - 1, x // 2, z // 2) in terrain,
                f"manifest.json: sibling group L{level}/{x}/{z} has no same-pack parent")
    for entry in manifest["features"]:
        require(integers(entry, 3), "manifest.json: invalid feature tile address")
        level, x, z = entry
        require(level in ((0,) if manifest["id"] == "base" else (0, 5)), "manifest.json: unsupported feature layer")
        xlim, zlim = extent(level)
        require(-xlim <= x < xlim and -zlim <= z < zlim and tuple(entry) not in features,
                f"manifest.json: duplicate or non-canonical feature address {entry}")
        features.add(tuple(entry))
    return terrain, features, groups


def pack_address(archive, manifest, terrain, features):
    names = {"manifest.json", "receipt.json", "DATA-NOTICE.txt"}
    if manifest["id"] == "base":
        require(not terrain, "base ZIP cannot declare WFT1 terrain details")
        names.add("base.bin")
    else:
        require(bool(terrain or features), "optional pack is empty")
    names.update(tile_path(*key) for key in terrain)
    names.update(feature_path(*key) for key in features)
    infos = archive.infolist()
    actual = {i.filename for i in infos}
    require(len(actual) == len(infos), "ZIP contains duplicate entry names")
    require(names == actual, f"ZIP inventory differs: missing {sorted(names - actual)[:3]}, "
                            f"undeclared {sorted(actual - names)[:3]}")
    for info in infos:
        if info.filename.startswith("terrain/"):
            require(info.file_size == TILE_BYTES, f"{info.filename}: wrong WFT1 file size")
        if info.filename.startswith("features/"):
            require(info.file_size <= MAX_FEATURE_BYTES, f"{info.filename}: exceeds JSON byte budget")


def check_feature(archive, key, credits, expected_source):
    path = feature_path(*key)
    data = json.loads(archive.read(path))
    require(isinstance(data, dict) and set(data) == {"schemaVersion", "tile", "rows"} and
            type(data["schemaVersion"]) is int and data["schemaVersion"] == 1 and
            integers(data["tile"], 3) and data["tile"] == list(key), f"{path}: wrong feature header/address")
    rows = data["rows"]
    require(isinstance(rows, list) and 0 < len(rows) <= MAX_ROWS, f"{path}: invalid row count")
    size, used, heights = span(key[0]), 0, 0
    for row in rows:
        require(isinstance(row, list) and len(row) in (5, 6), f"{path}: invalid feature row")
        kind, style, name, source, coords = row[:5]
        require(isinstance(kind, str) and kind in FEATURE_STYLES and style in FEATURE_STYLES[kind] and
                (name is None or label(name)) and source == expected_source and source in credits,
                f"{path}: bad row class/style/name/credit: {row[:4]}")
        if len(row) == 6:
            require(valid_building_height(key[0], kind, source, row[5]),
                    f"{path}: building height must be an integer in 1..1000 on an L5 OSM building")
            heights += 1
        require(isinstance(coords, list), f"{path}: missing points")
        n = len(coords)
        require((2 <= n <= 2048 if kind in ("road", "rail") else 3 <= n <= 2048 if kind in ("landuse", "building") else n == 1) and
                all(integers(p, 2) and all(0 <= v <= size for v in p) for p in coords) and
                len({tuple(p) for p in coords}) == n, f"{path}: {kind}/{style} has invalid or repeated local vertices")
        used += n
    require(used <= MAX_POINTS, f"{path}: exceeds point budget")
    return len(rows), used, heights


def check_base(data, sha):
    require(len(data) == BASE_BYTES and digest(data) == sha, "base.bin: incorrect WFA1 length or SHA-256")
    heights, water = decode_base(data)
    require(np.min(heights) >= -12000 and np.max(heights) <= 10000 and np.max(water) <= 2,
            "base.bin: invalid elevation or water")
    require(np.all(heights[water == 1] == 0), "base.bin: open ocean samples must be at sea level")
    for h, w in ((heights[0], water[0]), (heights[-1], water[-1])):
        require(np.all(h == h[0]) and np.all(w == w[0]), "base.bin: longitudes disagree at a pole")
    a = interpolate(heights, water, 0, np.arange(heights.shape[0]), periodic=True)
    b = interpolate(heights, water, heights.shape[1], np.arange(heights.shape[0]), periodic=True)
    require(all(np.array_equal(x, y) for x, y in zip(a, b)), "base.bin: longitude aliases disagree")
    return heights, water


def edges(tiles):
    for values in zip(*tiles):
        sw, se, nw, ne = values
        yield np.r_[sw[0], se[0, 1:], nw[-1], ne[-1, 1:],
                    sw[1:, 0], nw[1:-1, 0], se[1:, -1], ne[1:-1, -1]]


def check_tiles(archive, groups, base):
    @lru_cache(maxsize=4096)
    def read(level, x, z):
        data = decode_terrain(archive.read(tile_path(level, x, z)), (level, x, z))
        h, w = data
        require(np.min(h) >= -12000 and np.max(h) <= 10000 and np.max(w) <= 2,
                f"{tile_path(level, x, z)}: invalid elevation or water")
        return data

    for level, x, z in sorted(groups):
        tiles = (read(level, x, z), read(level, x + 1, z), read(level, x, z + 1), read(level, x + 1, z + 1))
        for sw, se, nw, ne in zip(*tiles):
            require(np.array_equal(sw[:, -1], se[:, 0]) and np.array_equal(nw[:, -1], ne[:, 0]) and
                    np.array_equal(sw[-1], nw[0]) and np.array_equal(se[-1], ne[0]),
                    f"terrain L{level}/{x}/{z}: interior sibling edges disagree")
        parent = read(level - 1, x // 2, z // 2) if level > 1 else None
        h, w = parent_edge(level, x, z, base, parent)
        yh, yw = edges(tiles)
        require(np.array_equal(yh, round_height(h)) and np.array_equal(yw, w),
                f"terrain L{level}/{x}/{z}: sibling-group perimeter does not match direct parent")


def check_receipt(archive, manifest, source_dirs, hash_cache):
    receipt = json.loads(archive.read("receipt.json"))
    require(isinstance(receipt, dict) and type(receipt.get("schemaVersion")) is int and receipt["schemaVersion"] == 1
            and receipt.get("id") == manifest["id"] and receipt.get("baseSha256") == manifest["baseSha256"],
            "receipt.json: wrong pack ID, schema or base fingerprint")
    require(isinstance(receipt.get("sources"), list) and receipt["sources"], "receipt.json: missing input sources")
    credit_rows = manifest["credits"]
    seen = set()
    for record in receipt["sources"]:
        require(isinstance(record, dict), "receipt.json: invalid source record")
        key, name = record.get("source"), record.get("file")
        require(isinstance(key, str) and key in credit_rows and label(name, 255) and "/" not in name and "\\" not in name and
                name not in (".", "..") and type(record.get("bytes")) is int and record["bytes"] > 0 and
                isinstance(record.get("sha256"), str) and SHA.fullmatch(record["sha256"]) and (key, name) not in seen,
                "receipt.json: invalid or repeated source fingerprint")
        require_https(record.get("url"))
        seen.add((key, name))
        if source_dirs:
            matches = []
            for root in source_dirs:
                file = root / name
                if file.is_file() and file.stat().st_size == record["bytes"]:
                    if file not in hash_cache:
                        hash_cache[file] = file_digest(file)
                    matches.append(hash_cache[file])
            require(record["sha256"] in matches, f"receipt.json: {name} is missing or does not match its source SHA-256")
    require({key for key, name in seen} == set(credit_rows), "receipt.json: credited source has no fingerprint")
    notice = archive.read("DATA-NOTICE.txt").decode()
    for label_text, license_name, url in credit_rows.values():
        require(all(field in notice for field in (label_text, license_name, url)), "DATA-NOTICE.txt: missing exact credit")
    return receipt


@dataclass
class Report:
    manifest: dict
    receipt: dict
    terrain: set
    features: set
    rows: int
    points: int
    base: tuple


def check_shared_terrain(path, terrain, occupied):
    by_owner = {}
    for key in terrain & occupied.keys():
        by_owner.setdefault(occupied[key], []).append(key)
    if by_owner:
        with ZipFile(path) as candidate:
            for owner, keys in sorted(by_owner.items()):
                with ZipFile(owner) as accepted:
                    for key in sorted(keys):
                        entry = tile_path(*key)
                        require(candidate.read(entry) == accepted.read(entry),
                                f"{path}: {entry} conflicts with accepted pack {owner.name}")


def validate(path, source_base=None, source_dirs=(), hash_cache=None):
    path = Path(path)
    require(path.stat().st_size <= MAX_BYTES, f"{path}: compressed ZIP exceeds budget")
    with ZipFile(path) as archive:
        infos = archive.infolist()
        require(len(infos) <= MAX_ENTRIES and sum(info.file_size for info in infos) <= MAX_BYTES,
                f"{path}: ZIP entry or uncompressed byte budget exceeded")
        require(all(info.file_size <= 4 * 1024 ** 2 for info in infos if info.filename in
                    ("manifest.json", "receipt.json", "DATA-NOTICE.txt")), f"{path}: metadata size exceeded")
        manifest = json.loads(archive.read("manifest.json"))
        check_basic(manifest, path)
        terra, features, groups = addresses(manifest)
        pack_address(archive, manifest, terra, features)
        receipt = check_receipt(archive, manifest, source_dirs, hash_cache if hash_cache is not None else {})
        sha = manifest["baseSha256"]
        if manifest["id"] == "base":
            base = check_base(archive.read("base.bin"), sha)
        else:
            require(source_base is not None and source_base.manifest["baseSha256"] == sha,
                    f"{path}: requires the matching base ZIP")
            base = source_base.base
        check_tiles(archive, groups, base)
        rows = points = height_rows = 0
        expected_source = "natural-earth" if manifest["id"] == "base" else "osm"
        for key in sorted(features):
            r, p, h = check_feature(archive, key, manifest["credits"], expected_source)
            rows, points = rows + r, points + p
            height_rows += h
        if features:
            stats = receipt.get("features", {})
            require((stats.get("tiles"), stats.get("rows"), stats.get("points")) == (len(features), rows, points),
                    "receipt.json: feature counts do not match archive")
            if "buildingHeightRows" in stats:
                counts = stats["buildingHeightRows"]
                require(isinstance(counts, dict) and set(counts) == set(BUILDING_HEIGHT_SOURCES) and
                        all(type(n) is int and n >= 0 for n in counts.values()) and sum(counts.values()) == height_rows,
                        "receipt.json: building height counts do not match archive")
        if terra:
            stats = receipt.get("terrain", {})
            require(stats.get("tileCount") == len(terra) and stats.get("coverage") == manifest["terrain"] and
                    stats.get("exclusiveL0Roots") == [[x // 2, z // 2] for _, x, z in sorted(g for g in groups if g[0] == 1)],
                    "receipt.json: terrain counts, roots or bounds do not match archive")
    return Report(manifest, receipt, terra, features, rows, points, base)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("packs", type=Path, nargs="+", help="ZIPs and/or a directory containing pack ZIPs")
    p.add_argument("--base", type=Path, help="matching base ZIP, if not listed as an input")
    p.add_argument("--source-dir", type=Path, action="append", default=[], help="also verify actual source sizes/hashes")
    p.add_argument("--report", type=Path, help="save data provenance, coverage, and density for validated ZIPs")
    args = p.parse_args()
    try:
        files = set()
        seattle_error = (f"{SEATTLE_ARCHIVE} is World Flight data, not an Atlas pack. "
                         "Use tools/WorldFlight/check.py on the extracted package.")
        for path in args.packs:
            require(path.name != SEATTLE_ARCHIVE, seattle_error)
            if path.is_dir():
                files.update(file.resolve() for file in path.glob("*.zip") if file.name != SEATTLE_ARCHIVE)
            else:
                files.add(path.resolve())
        if args.base:
            require(args.base.name != SEATTLE_ARCHIVE, seattle_error)
            files.add(args.base.resolve())
        require(bool(files), "No World Atlas packs found")
        require(all(path.name != SEATTLE_ARCHIVE for path in files), seattle_error)
        base, seen_ids, occupied, claimed, hashes = None, set(), {}, set(), {}
        summary = []
        for path in sorted(files, key=lambda path: (path.name != "base.zip", str(path))):
            try:
                report = validate(path, base, args.source_dir, hashes)
            except (ValueError, KeyError, OSError, BadZipFile) as error:
                raise ValueError(f"{path}: {error}") from error
            pack_id = report.manifest["id"]
            claims = {("natural-earth" if pack_id == "base" else "osm", key) for key in report.features}
            require(pack_id not in seen_ids, f"{path}: repeats pack {pack_id}")
            check_shared_terrain(path, report.terrain, occupied)
            require(not (claims & claimed), f"{path}: overlaps an already mounted feature tile from the same source")
            seen_ids.add(pack_id)
            for key in report.terrain:
                occupied.setdefault(key, path)
            claimed.update(claims)
            if pack_id == "base":
                base = report
            size = path.stat().st_size
            if args.report:
                summary.append({"id": pack_id, "archiveBytes": size, "archiveSha256": file_digest(path),
                                "terrainEntries": len(report.terrain), "featureEntries": len(report.features),
                                "rows": report.rows, "points": report.points, "receipt": report.receipt})
            print(f"{path.name}: {size:,} bytes; {len(report.terrain):,} WFT1, "
                  f"{len(report.features):,} feature chunks, {report.rows:,} rows, {report.points:,} points", flush=True)
        if args.report:
            data = {"schemaVersion": 1, "baseSha256": base.manifest["baseSha256"], "packs": summary,
                    "uniqueTerrainEntries": len(occupied),
                    "totals": {field: sum(row[field] for row in summary) for field in (
                        "archiveBytes", "terrainEntries", "featureEntries", "rows", "points")}}
            args.report.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Validated {len(seen_ids):,} compatible packs and {len(occupied):,} unique WFT1 tiles.", flush=True)
    except (ValueError, KeyError, OSError, BadZipFile) as error:
        p.exit(1, f"World Atlas: {error}\n")


if __name__ == "__main__":
    main()
