#!/usr/bin/env python3
"""Compile attributed, offline World Atlas ZIPs from local public data."""

import argparse
import json
from contextlib import contextmanager
from datetime import date
from pathlib import Path
from zipfile import BadZipFile, ZipFile

from atlas import (SEATTLE_ARCHIVE, SLUG, PackWriter, archive_manifest, base_presets, bounds_valid, decode_base,
                   digest, json_bytes, span)
from features import natural_earth
from profiles import LONGITUDE_BANDS, OCEAN_FEATURES, OCEAN_TERRAIN, PILOTS, REFINEMENTS, stock
from sources import (Source, credits, fetch, notice, osm_source, require_https, terrain_sources,
                     vector_sources)
from terrain import build_base, plan_boxes, rectangles, root_groups, write_tiles

RAW = Path("artifacts/world-flight-graphics/raw")
INPUTS = Path("artifacts/world-atlas-inputs")
OUTPUT = Path("artifacts/world-atlas")
PROTOMAPS_DOCS = "https://docs.protomaps.com/basemaps/downloads"


def write_metadata(writer, manifest, receipt, notes=""):
    writer.add("manifest.json", json_bytes(manifest))
    writer.add("receipt.json", json_bytes(receipt))
    writer.add("DATA-NOTICE.txt", notice(manifest["name"], manifest["credits"]) + notes.encode())


def base(raw, inputs, output, *, extended=False):
    dem = terrain_sources(raw)
    vectors = vector_sources(inputs, extended)
    data_sources = dem + list(vectors.values())
    source_receipts = [source.receipt() for source in data_sources]
    print("Sampling worldwide ETOPO and Natural Earth water", flush=True)
    data = build_base(*(source.path for source in dem))
    heights, water = decode_base(data)
    print("Clipping worldwide Natural Earth features", flush=True)
    features = natural_earth({key: source.path for key, source in vectors.items()})
    sha = digest(data)
    name = "World Atlas • global base"
    with PackWriter(output) as writer:
        writer.add("base.bin", data)
        keys, counts = features.write(writer)
        manifest = archive_manifest("base", name, sha, [], keys, base_presets(), credits(data_sources))
        receipt = {"schemaVersion": 1, "id": "base", "baseSha256": sha, "sources": source_receipts,
                   "terrain": {"extentDegrees": [-180, -90, 180, 90], "gridSpacingMetres": 16384,
                               "elevationRangeMetres": [int(heights.min()), int(heights.max())],
                               "waterSamples": [int((water == i).sum()) for i in range(3)]},
                   "features": counts}
        write_metadata(writer, manifest, receipt)
    return receipt


def box_arg(value):
    try:
        bounds = tuple(map(float, value.split(",")))
        if len(bounds) == 4 and bounds_valid(bounds):
            return bounds
    except ValueError:
        pass
    raise argparse.ArgumentTypeError("expected W,S,E,N degrees within [-180,-90,180,90], with W<E and S<N")


def terrain_arg(value):
    box, mark, level = value.partition("@")
    if not mark or not level.isdigit() or not 1 <= int(level) <= 8:
        raise argparse.ArgumentTypeError("expected W,S,E,N@LEVEL (1–8)")
    return box_arg(box), int(level)


def root_rect(value):
    try:
        w, s, e, n = map(int, value.split(","))
        if -76 <= w < e <= 76 and -38 <= s < n <= 38:
            return w, s, e, n
    except ValueError:
        pass
    raise argparse.ArgumentTypeError("expected exact L0 cells W,S,E,N within [-76,-38,76,38]")


def root_arg(value):
    rect, mark, level = value.partition("@")
    if not mark or not level.isdigit() or not 1 <= int(level) <= 8:
        raise argparse.ArgumentTypeError("expected L0 W,S,E,N@LEVEL (1–8)")
    return root_rect(rect), int(level)


def area_setup(args):
    if f"{args.id}.zip" == SEATTLE_ARCHIVE:
        raise ValueError(f"{SEATTLE_ARCHIVE} is reserved for the World Flight package; use 'puget-sound' for Atlas terrain.")
    custom = (args.name or args.terrain_box or args.root_rect or args.exclude_root_rect or
              args.osm or args.osm_url or args.feature_box or args.dem)
    profile = stock(args.id, args.raw, args.inputs) if not custom else None
    if profile is not None:
        return profile
    if not any((args.terrain_box, args.root_rect, args.osm)):
        raise ValueError("Unknown preset; specify --terrain-box, --root-rect, or --osm.")
    if any(args.id in profiles for profiles in (LONGITUDE_BANDS, PILOTS, REFINEMENTS, OCEAN_TERRAIN, OCEAN_FEATURES)) or (
            args.id.endswith("-osm") and args.id[:-4] in PILOTS):
        raise ValueError("A custom pack must use a new ID, not a stock profile ID.")
    if args.osm and not (args.osm_url and args.feature_box):
        raise ValueError("--osm requires --osm-url and --feature-box for an auditable clipped extract.")
    if not args.osm and (args.osm_url or args.feature_box):
        raise ValueError("--osm-url/--feature-box require --osm.")
    if args.osm and (args.terrain_box or args.root_rect or args.dem):
        raise ValueError("Create a separate feature-only overlay for ODbL data.")
    dem = []
    for key, path, url in args.dem:
        if key not in ("usgs-3dep", "copernicus-dem"):
            raise ValueError("--dem requires the supported redistribution terms 'usgs-3dep' or 'copernicus-dem'.")
        require_https(url)
        dem.append(Source(key, Path(path), url))
    exclude = set().union(*(root_groups(r, 1) for r in args.exclude_root_rect))
    plan = plan_boxes(args.terrain_box, roots=args.root_rect, exclude_roots=exclude)
    osm = Source("osm", args.osm, require_https(args.osm_url)) if args.osm else None
    return args.name or args.id.replace("-", " ").title(), plan, osm, args.feature_box, dem


@contextmanager
def location_index(mode, index_file):
    if index_file is None:
        yield mode
        return
    path = index_file.resolve()
    if not path.is_relative_to(Path.cwd()) or "," in str(path):
        raise ValueError("Choose an unused OSM index path in this repository, without a comma.")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.touch(exist_ok=False)
    try:
        yield f"sparse_file_array,{path}"
    finally:
        path.unlink(missing_ok=True)


def area(args, output):
    name, plan, osm, feature_bounds, dem = area_setup(args)
    if not (plan or osm):
        raise ValueError("Area has neither terrain detail nor features.")
    if args.osm_index_file and not osm:
        raise ValueError("--osm-index-file requires an OSM overlay.")
    if not name.strip() or not name.isprintable() or len(name.encode("utf-16-le")) > 200:
        raise ValueError("Pack name must be printable and at most 100 UTF-16 code units.")
    with ZipFile(args.base) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        base_data = archive.read("base.bin")
        base_receipts = {(src["source"], src["file"]): src["sha256"]
                         for src in json.loads(archive.read("receipt.json"))["sources"]}
    if manifest.get("id") != "base" or digest(base_data) != manifest.get("baseSha256"):
        raise ValueError("Incompatible or corrupted base ZIP.")
    source_base = decode_base(base_data)
    sha = manifest["baseSha256"]
    bounds = rectangles(plan)
    source_dem = terrain_sources(args.raw) if plan else []
    data_sources = source_dem + dem + ([osm] if osm else [])
    receipts = [source.receipt() for source in data_sources]
    for src in source_dem:
        receipt = next(r for r in receipts if r["file"] == src.path.name and r["source"] == src.key)
        key = src.key, src.path.name
        if base_receipts.get(key) != receipt["sha256"]:
            raise ValueError(f"{src.path}: hash differs from the input used by base ZIP; rebuild/select matching base.")
    receipt = {"schemaVersion": 1, "id": args.id, "baseSha256": sha, "sources": receipts}
    keys = []
    with PackWriter(output) as writer:
        if plan:
            if max(plan) < 3 and dem:
                raise ValueError("A local DEM is only used with detail L3 or finer.")
            print(f"Sampling licensed elevation for {args.id}", flush=True)
            write_tiles(writer, plan, source_base, *(source.path for source in source_dem),
                        local=[source.path for source in dem])
            receipt["terrain"] = {
                "tileCount": sum(4 * len(groups) for groups in plan.values()),
                "tilesByLevel": {str(level): 4 * len(groups) for level, groups in sorted(plan.items())},
                "exclusiveL0Roots": [[x // 2, z // 2] for x, z in sorted(plan[1])],
                "spacingMetres": [span(level) // 32 for level in sorted(plan)],
                "coverage": bounds,
            }
        if osm:
            from osm_layer import selected_osm
            print(f"Clipping selected OpenStreetMap layers for {args.id}", flush=True)
            with location_index(args.osm_index, args.osm_index_file) as index:
                layer, snapshot, import_stats = selected_osm(osm, feature_bounds, index=index)
            receipts[-1].update(snapshot)
            keys, details = layer.write(writer)
            if not keys:
                raise ValueError("No supported OSM geometry intersects the requested feature bounds.")
            receipt["features"] = {
                "requestedBoundsDegrees": list(feature_bounds), **details, "import": import_stats,
                "selection": "selected named points and mapped ways; no relations, no routing graph",
            }
        manifest = archive_manifest(args.id, name, sha, bounds, keys, [], credits(data_sources))
        write_metadata(writer, manifest, receipt)
    return receipt


def fetch_inputs(args):
    if args.target == "base":
        inputs = terrain_sources(args.raw) + list(vector_sources(args.inputs, args.extended_vectors).values())
    elif args.target == "osm":
        if not args.output or not args.url:
            raise ValueError("fetch osm requires --output FILE.osm.pbf and --url HTTPS_URL.")
        if not args.output.name.endswith(".osm.pbf") or "planet" in args.output.name.lower() or "planet" in args.url.lower():
            raise ValueError("Use a bounded regional .osm.pbf extract; planet downloads are disabled.")
        inputs = [Source("osm", args.output, require_https(args.url))]
    elif args.target in ("washington", "panama", "peru", "sudeste"):
        inputs = [osm_source(args.inputs, args.target)]
    elif any(args.target in profiles for profiles in (PILOTS, REFINEMENTS, OCEAN_TERRAIN, OCEAN_FEATURES)):
        _, _, osm, _, local = stock(args.target, args.raw, args.inputs)
        inputs = [osm] if osm else [src for src in local if src.key == "copernicus-dem"]
        if not inputs:
            raise ValueError("This pilot does not require a separate public DEM.")
    else:
        raise ValueError("Unknown source target; see --help.")
    if args.sha256 and (len(args.sha256) != 64 or any(ch not in "0123456789abcdef" for ch in args.sha256) or len(inputs) != 1):
        raise ValueError("--sha256 pins one download and must contain 64 lowercase hexadecimal digits.")
    if not 1 <= args.max_mib <= 16384:
        raise ValueError("--max-mib must be between 1 and 16,384.")
    for item in inputs:
        print(f"  {item.path}", flush=True)
        fetch(item, refresh=args.refresh, pin=args.sha256, max_bytes=args.max_mib * 1024 ** 2)


def overview(args, output):
    from overview import MAX_ARCHIVE, write_overview

    day = args.build_date.strftime("%Y%m%d")
    src = Source("osm", args.input or args.inputs / f"protomaps-{day}-z8.pmtiles",
                 f"https://build.protomaps.com/{day}.pmtiles", args.build_date.isoformat())
    if not 0 < src.path.stat().st_size <= MAX_ARCHIVE:
        raise ValueError("Expected a local overview extract no larger than 1 GiB, not a full planet file.")
    source_receipt = src.receipt()
    source_receipt.update({"provider": "Protomaps Basemap", "providerNotice": PROTOMAPS_DOCS,
                           "extractMethod": "pmtiles extract --maxzoom=8"})
    with ZipFile(args.base) as archive:
        data = archive.read("base.bin")
        current = json.loads(archive.read("manifest.json"))
    sha = digest(data)
    if current.get("id") != "base" or current.get("baseSha256") != sha:
        raise ValueError("Incompatible or corrupted base ZIP.")
    decode_base(data)
    name = "World roads and places • OpenStreetMap via Protomaps"
    credit_rows = credits([src])
    credit_rows["osm"] = ["© OpenStreetMap contributors · Protomaps Basemap",
                          "Open Database License (ODbL) 1.0", "https://www.openstreetmap.org/copyright"]
    with PackWriter(output) as writer:
        print(f"Converting bounded Protomaps zoom-8 tiles from {src.path.name}", flush=True)
        keys, stats, extraction = write_overview(src.path, writer)
        source_receipt["replicationTimestamp"] = extraction["replicationTimestamp"]
        receipt = {"schemaVersion": 1, "id": "world-osm", "baseSha256": sha, "sources": [source_receipt],
                   "features": stats | {"overview": extraction}}
        manifest = archive_manifest("world-osm", name, sha, [], keys, [], credit_rows)
        write_metadata(writer, manifest, receipt, "Source: Protomaps Basemap, distributed as an ODbL Produced Work.\n"
                       f"{PROTOMAPS_DOCS}\nThe source basemap also uses public-domain Natural Earth data.\n")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    global_parser = commands.add_parser("base", help="build worldwide WFA1 terrain and Natural Earth features")
    global_parser.add_argument("--raw", type=Path, default=RAW, help="NOAA and Natural Earth coast/lake cache")
    global_parser.add_argument("--inputs", type=Path, default=INPUTS, help="pinned Natural Earth vector cache")
    global_parser.add_argument("--output", type=Path, default=OUTPUT / "base.zip")
    global_parser.add_argument("--extended-vectors", action="store_true", help="add Natural Earth peaks and railroads")
    region = commands.add_parser("area", help="build a stock area or custom terrain / OSM pack")
    region.add_argument("id", help="a stock area ID (see list), or a new lowercase slug for custom data")
    region.add_argument("--name", help="custom display name")
    region.add_argument("--base", type=Path, default=OUTPUT / "base.zip")
    region.add_argument("--raw", type=Path, default=RAW)
    region.add_argument("--inputs", type=Path, default=INPUTS)
    region.add_argument("--output", type=Path, help="default: artifacts/world-atlas/ID.zip")
    region.add_argument("--terrain-box", action="append", type=terrain_arg, default=[], metavar="W,S,E,N@LEVEL")
    region.add_argument("--root-rect", action="append", type=root_arg, default=[], metavar="W,S,E,N@LEVEL")
    region.add_argument("--exclude-root-rect", action="append", type=root_rect, default=[], metavar="W,S,E,N")
    region.add_argument("--dem", nargs=3, action="append", default=[], metavar=("CREDIT", "TIFF", "URL"))
    region.add_argument("--osm", type=Path, help="local regional OSM PBF or OSM XML input")
    region.add_argument("--osm-url", help="public source HTTPS URL")
    region.add_argument("--feature-box", type=box_arg, metavar="W,S,E,N")
    region.add_argument("--osm-index", choices=("sparse_mem_array", "flex_mem"), default="sparse_mem_array")
    region.add_argument("--osm-index-file", type=Path, help="use and clean an unused project-local disk-backed index")
    commands.add_parser("list", help="list available built-in terrain and OSM area IDs")
    download = commands.add_parser("fetch", help="fetch licensed datasets in bounded whole-file downloads")
    download.add_argument("target", help="'base', a scenery profile ID, 'washington', 'panama', 'peru', 'sudeste', or 'osm'")
    download.add_argument("--inputs", type=Path, default=INPUTS)
    download.add_argument("--raw", type=Path, default=INPUTS)
    download.add_argument("--output", type=Path, help="destination for 'fetch osm'")
    download.add_argument("--url", help="public extract HTTPS URL for 'fetch osm'")
    download.add_argument("--max-mib", type=int, default=2048, help="maximum bytes per download in MiB (default: 2048)")
    download.add_argument("--sha256", help="optional known SHA-256 for one source")
    download.add_argument("--refresh", action="store_true", help="atomically replace cached downloads")
    download.add_argument("--extended-vectors", action="store_true", help="for 'fetch base': also fetch peaks and railroads")
    world = commands.add_parser("overview", help="build world-osm.zip from a local Protomaps zoom-8 PMTiles")
    world.add_argument("--input", type=Path, help="path to a local bounded extract")
    world.add_argument("--inputs", type=Path, default=INPUTS)
    world.add_argument("--build-date", type=date.fromisoformat, default=date(2026, 11, 20))
    world.add_argument("--base", type=Path, default=OUTPUT / "base.zip")
    world.add_argument("--output", type=Path, default=OUTPUT / "world-osm.zip")
    args = parser.parse_args()
    try:
        if args.action == "list":
            for key, (name, w, e) in LONGITUDE_BANDS.items():
                print(f"{key:22} {name}; roots {w},-38,{e},38 (excluding stock pilots)")
            for key, pilot in PILOTS.items():
                print(f"{key:22} {pilot.name}")
                print(f"{key + '-osm':22} {pilot.name}; OSM-only overlay")
            for key, (name, _, _) in REFINEMENTS.items():
                print(f"{key:22} {name}; drop-in terrain refinement")
            for key, (name, *_) in OCEAN_TERRAIN.items():
                print(f"{key:22} {name}; drop-in terrain refinement")
            for key, (name, *_) in OCEAN_FEATURES.items():
                print(f"{key:22} {name}; OSM-only overlay")
            return
        if args.action == "fetch":
            fetch_inputs(args)
            return
        pack_id = {"base": "base", "overview": "world-osm"}.get(args.action) or args.id
        if not SLUG.fullmatch(pack_id) or len(pack_id) > 64 or (args.action == "area" and pack_id in ("base", "world-osm")):
            raise ValueError("Area ID must be a lowercase slug (max 64 characters), other than base or world-osm.")
        output = args.output or OUTPUT / f"{pack_id}.zip"
        if output.name != f"{pack_id}.zip":
            raise ValueError("Output filename must match the pack ID, including lowercase '.zip'.")
        if args.action == "base":
            result = base(args.raw, args.inputs, output, extended=args.extended_vectors)
        elif args.action == "overview":
            result = overview(args, output)
        else:
            result = area(args, output)
        print(f"{output}: {output.stat().st_size:,} bytes")
        summary = {key: value for key, value in result.items() if key != "sources"}
        if "terrain" in summary:
            summary["terrain"] = {k: v for k, v in summary["terrain"].items() if k not in ("exclusiveL0Roots", "coverage")}
        print(json.dumps(summary, ensure_ascii=False))
    except (ValueError, OSError, KeyError, BadZipFile, RuntimeError) as error:
        parser.exit(1, f"World Atlas: {error}\n")


if __name__ == "__main__":
    main()
