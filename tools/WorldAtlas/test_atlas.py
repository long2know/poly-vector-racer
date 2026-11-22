import gzip
import json
import os
import shutil
import struct
import subprocess
import sys
import unittest
from datetime import date
from io import BytesIO
from pathlib import Path
from types import SimpleNamespace
from zipfile import ZIP_DEFLATED, ZipFile

import numpy as np
import rasterio
from rasterio.transform import Affine

from atlas import MAX_FEATURE_BYTES, MAX_POINTS, MAX_ROWS, PackWriter, digest, feature_path, interpolate, project, span, tile_path
from build import area, base, overview, root_arg, terrain_arg
from check import check_feature, check_shared_terrain, validate
from features import Features, project_line
from osm_layer import building_height
from profiles import LONGITUDE_BANDS, PILOTS, stock
from sources import vector_sources


def geojson(path, features):
    path.write_text(json.dumps({"type": "FeatureCollection", "features": [
        {"type": "Feature", "properties": props, "geometry": {"type": kind, "coordinates": points}}
        for props, kind, points in features]}))


def rewrite(path, entry, mutate):
    changed = path.with_suffix(".mutated")
    with ZipFile(path) as archive, ZipFile(changed, "x", ZIP_DEFLATED) as output:
        for key in archive.namelist():
            data = archive.read(key)
            output.writestr(key, mutate(data) if key == entry else data)
    changed.replace(path)


class AtlasTests(unittest.TestCase):
    def setUp(self):
        self.work = Path("artifacts/world-atlas-checks") / f"test-{os.getpid()}-{self._testMethodName}"
        self.work.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.work)

    def test_triangle_and_periodic_longitude(self):
        heights, water = np.array([[0, 8], [4, 24]]), np.array([[0, 1], [2, 0]])
        h, w = interpolate(heights, water, [.75, .25, .5], [.25, .75, .5])
        np.testing.assert_array_equal(h, [10, 8, 12])
        np.testing.assert_array_equal(w, [1, 2, 0])
        np.testing.assert_equal(interpolate(heights, water, 2, 1, periodic=True), (4, 2))

    def test_seattle_archive_is_reserved_in_a_shared_mount(self):
        raw, inputs = self.create_sources()
        mount = self.work / "scenery"
        mount.mkdir()
        atlas_base, seattle = mount / "base.zip", mount / "seattle.zip"
        base(raw, inputs, atlas_base)
        with ZipFile(seattle, "w") as archive:
            archive.writestr("manifest.json", json.dumps({"kind": "world-flight-terrain", "schemaVersion": 1}))
        previous = seattle.read_bytes()

        def cli(script, *args):
            return subprocess.run([sys.executable, "-B", f"tools/WorldAtlas/{script}.py", *map(str, args)],
                                  capture_output=True, text=True, check=False)

        build = cli("build", "area", "seattle", "--terrain-box=0,0,0.1,0.1@1", "--base", atlas_base, "--output", seattle)
        self.assertIn("seattle.zip is reserved for the World Flight package", build.stderr)
        self.assertEqual(build.returncode, 1)
        self.assertEqual(seattle.read_bytes(), previous)

        scan = cli("check", mount)
        self.assertEqual(scan.returncode, 0, scan.stderr)
        self.assertIn("Validated 1 compatible packs", scan.stdout)
        self.assertNotIn("seattle.zip", scan.stdout)
        for args in [(seattle,), (atlas_base, "--base", seattle)]:
            explicit = cli("check", *args)
            self.assertEqual(explicit.returncode, 1)
            self.assertIn("Use tools/WorldFlight/check.py on the extracted package", explicit.stderr)

        shutil.copyfile(seattle, mount / "Seattle.zip")
        wrong_case = cli("check", mount)
        self.assertEqual(wrong_case.returncode, 1)
        self.assertIn("Seattle.zip:", wrong_case.stderr)

    def test_dateline_and_loop_clipping(self):
        features = Features(0, "natural-earth")
        features.line("road", "primary", "Date Line", list(project_line([(179.9, 0), (-179.9, 0)])))
        west, east = features.rows[-76, 0], features.rows[75, 0]
        self.assertEqual(len(west), 1)
        self.assertEqual(len(east), 1)
        self.assertEqual(next(iter(east))[4][-1], (span(0), 0))
        self.assertEqual(next(iter(west))[4][0], (0, 0))
        features.line("road", "primary", "Loop", [(4, 3), (7, 3), (5, 5), (4, 3)])
        loops = [row[4] for row in features.rows[0, 0]]
        self.assertCountEqual(loops, [((4, 3), (7, 3), (5, 5)), ((5, 5), (4, 3))])

    def test_tile_budget_keeps_major_routes_and_declares_dropped_rows(self):
        layer = Features(5, "osm")
        for z in range(4100):
            layer.line("road", "local", None, [(1, z), (4, z)])
        layer.line("road", "motorway", "Main route", [(8, 7), (9, 7)])
        path = self.work / "tile.zip"
        with PackWriter(path) as writer:
            keys, counts = layer.write(writer)
        self.assertEqual(keys, [[5, 0, 0]])
        self.assertEqual(counts["droppedForTileBudget"], 5)
        with ZipFile(path) as archive:
            rows = json.loads(archive.read(feature_path(5, 0, 0)))["rows"]
            self.assertEqual(len(rows), 4096)
            self.assertIn(["road", "motorway", "Main route", "osm", [[8, 7], [9, 7]]], rows)

    def test_dense_local_chunks_keep_spatial_buildings_and_landuse(self):
        for road_count, vertices, saturated in ((4200, 2, "rows"), (500, 100, "points")):
            with self.subTest(saturated=saturated):
                layer = Features(5, "osm")
                for z in range(road_count):
                    layer.row(0, 0, "road", "local", None, [(x, z) for x in range(vertices)])
                required = [
                    ("landmark", "peak", "Summit", [(21, 21)]),
                    ("airport", "airport", "Field", [(31, 31)]),
                    ("place", "city", "City", [(41, 41)]),
                    ("road", "motorway", "Main route", [(80, 70), (90, 70)]),
                    ("rail", "rail", "Main railway", [(500, 500), (600, 550)]),
                ]
                for kind, style, name, points in required:
                    layer.row(0, 0, kind, style, name, points)

                def square(kind, style, x, z, **metadata):
                    layer.row(0, 0, kind, style, None, [(x, z), (x + 8, z), (x + 8, z + 8), (x, z + 8)], **metadata)

                distant = {(1, 1), (1, 7), (7, 1), (7, 7)}
                for kind, style in (("building", "building"), ("landuse", "park")):
                    for i in range(2000 if kind == "building" else 300):
                        square(kind, style, 20 + i % 100, 20 + i // 100)
                    for x, z in sorted(distant):
                        square(kind, style, x * 1024 + 30, z * 1024 + 30)
                square("building", "building", 990, 990, height=370, height_source="heightTag")
                path = self.work / f"cap-{saturated}.zip"
                with PackWriter(path) as writer:
                    keys, stats = layer.write(writer)
                self.assertEqual(keys, [[5, 0, 0]])
                with ZipFile(path) as archive:
                    data = archive.read(feature_path(5, 0, 0))
                rows = json.loads(data)["rows"]
                self.assertEqual([row[:3] for row in rows[:len(required)]], [list(row[:3]) for row in required])
                for kind in ("building", "landuse"):
                    cells = {(row[4][0][0] // 1024, row[4][0][1] // 1024) for row in rows if row[0] == kind}
                    self.assertTrue(distant <= cells, (kind, cells))
                taller = [row for row in rows if row[0] == "building" and len(row) == 6]
                self.assertEqual([row[5] for row in taller], [370])
                self.assertEqual(stats["buildingHeightRows"], {"heightTag": 1, "levelsEstimate": 0})
                points = sum(len(row[4]) for row in rows)
                self.assertLessEqual(len(rows), MAX_ROWS)
                self.assertLessEqual(points, MAX_POINTS)
                self.assertLessEqual(len(data), MAX_FEATURE_BYTES)
                if saturated == "rows":
                    self.assertEqual(len(rows), MAX_ROWS)
                else:
                    self.assertGreater(points, MAX_POINTS - vertices)
                self.assertEqual(stats["droppedForTileBudget"], stats["candidateRows"] - len(rows))
                self.assertEqual(sum(stats["droppedRowsByKind"].values()), stats["droppedForTileBudget"])
                self.assertEqual(stats["droppedRowsByKind"]["road"], road_count + 1 - sum(row[0] == "road" for row in rows))

    def test_building_height_prefers_clear_reported_units_before_levels(self):
        for tagged, levels, expected in (
            ("381", "5", (381, "heightTag")), ("14.5 m", "3", (15, "heightTag")),
            ("100ft", "12", (30, "heightTag")), ("12'6\"", None, (4, "heightTag")),
            ("1000 m", None, (1000, "heightTag")), (None, "8", (24, "levelsEstimate")),
            ("17;21", "6.0", (18, "levelsEstimate")), ("1100", "3", (9, "levelsEstimate")),
            ("unknown", None, (None, None)), (None, "4.5", (None, None)),
            ("1001 m", "334", (None, None)), ("1,200", "0", (None, None)),
            ("12'12\"", "-1", (None, None)), ("0.1 m", None, (None, None)),
        ):
            with self.subTest(height=tagged, levels=levels):
                self.assertEqual(building_height({"height": tagged, "building:levels": levels}), expected)

    def test_feature_checker_rejects_bad_sixth_fields(self):
        coords = [[10, 10], [40, 10], [40, 40], [10, 40]]
        cases = [(5, "building", "building", "osm", value) for value in (None, True, "8", 8.0, 0, -1, 1001)]
        cases.extend(((5, "road", "primary", "osm", 8), (5, "landuse", "park", "osm", 8),
                      (5, "building", "building", "natural-earth", 8),
                      (0, "building", "building", "osm", 8), (0, "building", "building", "natural-earth", 8)))
        for level, kind, style, source, value in cases:
            with self.subTest(level=level, kind=kind, source=source, height=value):
                archive_data = BytesIO()
                key = (level, 0, 0)
                with ZipFile(archive_data, "w") as archive:
                    archive.writestr(feature_path(*key), json.dumps({
                        "schemaVersion": 1, "tile": key, "rows": [[kind, style, None, source, coords, value]],
                    }))
                with ZipFile(archive_data) as archive, self.assertRaisesRegex(ValueError, "building height"):
                    check_feature(archive, key, {source: []}, source)

    def test_osm_building_height_footprint_and_landmark_roundtrip(self):
        raw, inputs = self.create_sources()
        base_path = self.work / "base.zip"
        base(raw, inputs, base_path)
        source_base = validate(base_path)
        osm = inputs / "buildings.osm"
        osm.write_text("""<osm version="0.6" generator="atlas test">
<node id="1" lon="0.004" lat="0.004"/><node id="2" lon="0.008" lat="0.004"/>
<node id="3" lon="0.008" lat="0.008"/><node id="4" lon="0.004" lat="0.008"/>
<way id="1"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/>
<tag k="building" v="commercial"/><tag k="name" v="Tower"/><tag k="tourism" v="attraction"/>
<tag k="height" v="920 ft"/><tag k="building:levels" v="20"/></way>
<way id="2"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/>
<tag k="building" v="apartments"/><tag k="building:levels" v="12"/></way>
<way id="3"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/>
<tag k="building" v="warehouse"/><tag k="height" v="5;9"/></way>
</osm>""")
        args = self.args(raw, inputs, "test-buildings", osm=osm, osm_url="https://www.openstreetmap.org/",
                         feature_box=(0, 0, .02, .02))
        path = self.work / "test-buildings.zip"
        area(args, path)
        report = validate(path, source_base, source_dirs=(inputs,))
        self.assertEqual((report.rows, report.points), (4, 13))
        self.assertEqual(report.receipt["features"]["buildingHeightRows"], {"heightTag": 1, "levelsEstimate": 1})
        feature = feature_path(5, 0, 0)
        with ZipFile(path) as archive:
            rows = json.loads(archive.read(feature))["rows"]
        self.assertIn(["landmark", "attraction", "Tower", "osm", [[664, 664]]], rows)
        footprints = {row[1]: row for row in rows if row[0] == "building"}
        self.assertEqual(footprints["commercial"][2:4], ["Tower", "osm"])
        self.assertEqual(footprints["commercial"][5], 280)
        self.assertEqual(footprints["residential"][5], 36)
        self.assertEqual(len(footprints["industrial"]), 5)
        self.assertEqual(footprints["commercial"][4], footprints["industrial"][4])

        def remove_height(data):
            payload = json.loads(data)
            row = next(row for row in payload["rows"] if row[:2] == ["building", "commercial"])
            row.pop()
            return json.dumps(payload).encode()

        rewrite(path, feature, remove_height)
        with self.assertRaisesRegex(ValueError, "height counts do not match"):
            validate(path, source_base)

    def test_l0_wire_bytes_unchanged_for_both_global_sources(self):
        expected = {
            "natural-earth": "9039eae0419858b3dbd0bbb5f51eaccd4d6fcc5b112dd7b32595dc11f712bd27",
            "osm": "a4829005a6c0282538b80c9fd3f889089f7709ad1cf86058b50e013a6e562f87",
        }
        for source, golden in expected.items():
            with self.subTest(source=source):
                layer = Features(0, source)
                for z in range(4100):
                    layer.row(0, 0, "road", "local", None, [(5, z), (6, z)])
                layer.row(0, 0, "road", "motorway", "Main route", [(80, 70), (90, 70)])
                layer.row(0, 0, "landmark", "peak", "Local summit", [(200, 200)])
                path = self.work / f"{source}.zip"
                with PackWriter(path) as writer:
                    _, stats = layer.write(writer)
                with ZipFile(path) as archive:
                    self.assertEqual(digest(archive.read(feature_path(0, 0, 0))), golden)
                self.assertNotIn("droppedRowsByKind", stats)

    def test_stock_bands_and_pilots_partition_all_roots(self):
        claimed = set()
        for pack_id in [*LONGITUDE_BANDS, *PILOTS]:
            _, plan, _, _, _ = stock(pack_id, self.work, self.work)
            roots = plan[1]
            self.assertFalse(roots & claimed, pack_id)
            self.assertEqual(plan[2], {(2 * x + dx, 2 * z + dz) for x, z in roots
                                      for dx in (0, 2) for dz in (0, 2)}, pack_id)
            claimed.update(roots)
        self.assertEqual(claimed, {(2 * x, 2 * z) for x in range(-76, 76) for z in range(-38, 38)})

    def test_build_real_zip_roundtrip_hashes_and_parent_seam_mutation(self):
        raw, inputs = self.create_sources()
        base_file = self.work / "base.zip"
        base(raw, inputs, base_file)
        global_report = validate(base_file, source_dirs=(raw, inputs))
        self.assertEqual(len(global_report.manifest["presets"]), 21)
        self.assertEqual(int(global_report.base[1][-1, -1]), 1)
        self.assertEqual(int(global_report.base[1][0, -1]), 0)
        target = self.work / "test-ridge.zip"
        args = self.args(raw, inputs, "test-ridge", root_rect=[root_arg("0,0,1,1@2")])
        area(args, target)
        local_report = validate(target, global_report, source_dirs=(raw, inputs))
        self.assertEqual(local_report.terrain, {(l, x, z) for l in (1, 2)
                                               for x in range(1 << l) for z in range(1 << l)})
        self.assertGreater(int(global_report.base[0][604, 1200]), 0)
        copy = self.work / "repeat" / target.name
        area(args, copy)
        self.assertEqual(copy.read_bytes(), target.read_bytes())
        occupied = {key: target for key in local_report.terrain}
        check_shared_terrain(copy, local_report.terrain, occupied)

        def alter_center(data):
            changed = bytearray(data)
            offset = 16 + 2 * (16 * 33 + 16)
            struct.pack_into("<h", changed, offset, struct.unpack_from("<h", data, offset)[0] + 1)
            return changed

        rewrite(copy, tile_path(1, 0, 0), alter_center)
        validate(copy, global_report)
        with self.assertRaisesRegex(ValueError, "terrain/1/0/0.bin conflicts with accepted pack"):
            check_shared_terrain(copy, local_report.terrain, occupied)

        def alter_corner(data):
            changed = bytearray(data)
            struct.pack_into("<h", changed, 16, struct.unpack_from("<h", data, 16)[0] + 1)
            return changed

        rewrite(target, tile_path(1, 0, 0), alter_corner)
        with self.assertRaisesRegex(ValueError, "perimeter does not match direct parent"):
            validate(target, global_report)
        lake = raw / "ne-lakes.geojson"
        lake.write_text(lake.read_text() + "\n")
        with self.assertRaisesRegex(ValueError, "ne-lakes.geojson .* SHA-256"):
            validate(base_file, source_dirs=(raw, inputs))

    def test_convert_osm_xml_attribution_clipping_and_road_class(self):
        raw, inputs = self.create_sources()
        base(raw, inputs, self.work / "base.zip")
        osm = inputs / "fixture.osm"
        osm.write_text("""<osm version="0.6" generator="atlas test">
<node id="1" lon="-0.036" lat="0.004"/>
<node id="2" lon="0.036" lat="0.004"/>
<node id="3" lon="0.005" lat="0.009"><tag k="natural" v="peak"/><tag k="name" v="Test Peak"/></node>
<way id="1"><nd ref="1"/><nd ref="2"/><tag k="highway" v="motorway_link"/><tag k="ref" v="Test Road"/></way>
</osm>""")
        args = self.args(raw, inputs, "test-overlay", osm=osm,
                         osm_url="https://www.openstreetmap.org/", feature_box=terrain_arg("-0.02,0,0.02,0.02@5")[0])
        path = self.work / "test-overlay.zip"
        area(args, path)
        report = validate(path, validate(self.work / "base.zip"), source_dirs=(inputs,))
        self.assertEqual(report.features, {(5, -1, 0), (5, 0, 0)})
        self.assertEqual(report.rows, 3)
        self.assertEqual(set(report.manifest["credits"]), {"osm"})
        with ZipFile(path) as archive:
            data = json.loads(archive.read(feature_path(5, 0, 0)))["rows"]
            self.assertIn(["landmark", "peak", "Test Peak", "osm", [[553, 996]]], data)
            y = round(project(0, .004)[1])
            length = round(project(.02, 0)[0])
            self.assertIn(["road", "motorway", "Test Road", "osm", [[0, y], [length, y]]], data)
            self.assertIn("© OpenStreetMap contributors", archive.read("DATA-NOTICE.txt").decode())
        disk = self.work / "disk" / path.name
        index = self.work / "node-index.bin"
        process = subprocess.run([
            sys.executable, "tools/WorldAtlas/build.py", "area", "test-overlay", "--base", str(args.base),
            "--osm", str(osm), "--osm-url", args.osm_url, "--feature-box=-0.02,0,0.02,0.02",
            "--output", str(disk), "--osm-index-file", str(index),
        ], capture_output=True, text=True)
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertEqual(disk.read_bytes(), path.read_bytes())
        self.assertFalse(index.exists())

    def test_overview_unbuffers_source_edges_across_hemispheres_and_dateline(self):
        raw, inputs = self.create_sources()
        base_path = self.work / "base.zip"
        base(raw, inputs, base_path)
        pm = inputs / "fixture.pmtiles"
        self.make_pmtiles(pm)
        path = self.work / "world-osm.zip"
        args = SimpleNamespace(input=pm, inputs=inputs, build_date=date(2026, 11, 20), base=base_path)
        overview(args, path)
        report = validate(path, validate(base_path), source_dirs=(inputs,))
        self.assertEqual(report.receipt["features"]["overview"]["addressedInputTiles"], 3)
        self.assertEqual(report.receipt["features"]["chunksByHemisphere"], {"north": 2, "south": 1})
        rows_by_tile = {}
        with ZipFile(path) as archive:
            for key in report.features:
                rows_by_tile[key] = json.loads(archive.read(feature_path(*key)))["rows"]
        rows = [row for matching in rows_by_tile.values() for row in matching]
        seams = [row for row in rows if row[2] == "Along the seam"]
        self.assertEqual(len(seams), 1)
        self.assertTrue(all(x == 0 for x, _ in seams[0][4]))
        north_x = {x: [row[4] for row in rows if row[2] == "Across the seam"][0]
                   for (_, x, z), rows in rows_by_tile.items() if z >= 0}
        self.assertEqual(set(north_x), {-76, 75})
        self.assertEqual(north_x[75][-1], [span(0), north_x[-76][0][1]])
        self.assertTrue(any(row[:4] == ["place", "city", "Southern City", "osm"] for row in rows))

    @staticmethod
    def make_pmtiles(path):
        from mapbox_vector_tile.Mapbox.vector_tile_pb2 import tile as VectorTile
        from pmtiles.tile import Compression, Entry, TileType, zxy_to_tileid
        from pmtiles.writer import finalize_header

        def make_layer(tile, layer_name, geometry_type, kind, detail, entries):
            layer = tile.layers.add(name=layer_name, version=2, extent=4096, keys=["kind", "kind_detail", "name"])
            for value in [kind, detail] + [label for label, _ in entries]:
                layer.values.add(string_value=value)
            for index, (_, points) in enumerate(entries):
                feature = layer.features.add(type=geometry_type, tags=[0, 0, 1, 1, 2, index + 2])
                feature.geometry.append(9)
                x = y = 0
                for i, (a, b) in enumerate(points):
                    if i == 1:
                        feature.geometry.append(2 | ((len(points) - 1) << 3))
                    feature.geometry.extend(((a - x) * 2 if a >= x else (x - a) * 2 - 1,
                                             (b - y) * 2 if b >= y else (y - b) * 2 - 1))
                    x, y = a, b

        east, west, south = VectorTile(), VectorTile(), VectorTile()
        make_layer(east, "roads", 2, "major_road", "primary",
                   [("Across the seam", [(3872, 256), (4256, 256)]), ("Along the seam", [(4096, 300), (4096, 500)])])
        make_layer(west, "roads", 2, "major_road", "primary",
                   [("Across the seam", [(-160, 256), (224, 256)]), ("Along the seam", [(0, 300), (0, 500)])])
        make_layer(south, "places", 1, "locality", "city", [("Southern City", [(400, 400)])])
        parts = sorted((zxy_to_tileid(8, x, y), gzip.compress(tile.SerializeToString(), mtime=0))
                       for x, y, tile in ((255, 60, east), (0, 60, west), (48, 188, south)))
        body, directory = bytearray(), []
        for key, data in parts:
            directory.append(Entry(key, len(body), len(data), 1))
            body.extend(data)
        head = {"tile_compression": Compression.GZIP, "tile_type": TileType.MVT,
                "min_lat_e7": -850511287, "max_lat_e7": 850511287}
        fields = finalize_header(head, len(parts), directory, len(parts), {"version": "test"}, True, len(body))
        path.write_bytes(b"".join((*fields, body)))

    def create_sources(self):
        raw, inputs = self.work / "raw", self.work / "inputs"
        raw.mkdir()
        inputs.mkdir()
        with rasterio.open(raw / "etopo60.tif", "w", driver="GTiff", height=4, width=8, count=1,
                           dtype="float32", crs="EPSG:4326", transform=Affine(45, 0, -180, 0, -45, 90)) as dem:
            dem.write(np.arange(32, dtype=np.float32).reshape((4, 8)) * 200, 1)
        land = [[(-180, -90), (180, -90), (180, 24), (-180, 24), (-180, -90)]]
        geojson(raw / "ne-land.geojson", [({}, "Polygon", land)])
        geojson(raw / "ne-lakes.geojson", [])
        for key, source in vector_sources(inputs).items():
            if key == "roads":
                features = [({"type": "Major Highway", "name": "Seam Road"}, "LineString", [(179.9, 0), (-179.9, 0)])]
            else:
                features = [({"name": key, "pop_max": 120000, "iata_code": "TST"}, "Point", (0, 1))]
            geojson(source.path, features)
        return raw, inputs

    def args(self, raw, inputs, pack_id, **options):
        args = dict(id=pack_id, raw=raw, inputs=inputs, base=self.work / "base.zip", name=None,
                    terrain_box=[], root_rect=[], exclude_root_rect=[], osm=None, osm_url=None,
                    feature_box=None, dem=[], osm_index="sparse_mem_array", osm_index_file=None)
        return SimpleNamespace(**(args | options))


if __name__ == "__main__":
    unittest.main()
