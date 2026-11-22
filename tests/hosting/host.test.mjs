import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { copyFile, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import test from 'node:test';
import { promisify } from 'node:util';

const assembly = resolve('src/StuntRacer.Host/bin/Release/net10.0/StuntRacer.Host.dll');
const bundle = resolve('src/StuntRacer.Browser/bin/Release/net10.0/browser-wasm/AppBundle');
const python = (...args) => promisify(execFile)('python3', ['-B', ...args]);

async function withHost(mount, check, sourcePackage = '', catalog = '', flightPackage = '', worldAtlas = '', extraEnv = {}) {
  const child = spawn('bash', ['scripts/dotnet.sh', assembly, '--urls', 'http://127.0.0.1:0'], {
    env: { ...process.env, BASE_URL: mount, STUNT_BUNDLE: bundle,
      STUNT_AMIGA_PACKAGE: sourcePackage, STUNT_TRACK_CATALOG: catalog, STUNT_FLIGHT_PACKAGE: flightPackage,
      STUNT_WORLD_ATLAS: worldAtlas, ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let log = '', timeout;
  const listening = new Promise((resolve, reject) => {
    child.stdout.on('data', data => {
      log += data.toString();
      const match = log.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) resolve(match[1]);
    });
    child.stderr.on('data', data => { log += data.toString(); });
    child.once('error', reject);
    child.once('exit', code => reject(new Error(`Host exited ${code}. Build the project first.\n${log}`)));
    timeout = setTimeout(() => reject(new Error(`Host did not listen.\n${log}`)), 15000);
  });
  try {
    const url = await listening;
    clearTimeout(timeout);
    await check(url, () => log);
  } finally {
    clearTimeout(timeout);
    if (child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill('SIGTERM');
      await Promise.race([exited, sleep(5000, undefined, { ref: false })]);
      if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGKILL');
        await exited;
      }
    }
  }
}

test('root deployment serves the actual WASM resources with correct MIME types and compression', async () => {
  await withHost('', async url => {
    const page = await fetch(url);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /src="\.\/main\.js"/);
    const symbols = await fetch(`${url}/_framework/dotnet.native.js.symbols`);
    assert.equal(symbols.status, 200, 'the runtime requests this deployed symbol file during startup');
    assert.equal(symbols.headers.get('content-type'), 'application/octet-stream');
    const wasm = await fetch(`${url}/_framework/dotnet.native.wasm`, { headers: { 'Accept-Encoding': 'br' } });
    assert.equal(wasm.status, 200);
    assert.equal(wasm.headers.get('content-type'), 'application/wasm');
    assert.equal(wasm.headers.get('content-encoding'), 'br');
    assert.deepEqual([...new Uint8Array(await wasm.arrayBuffer()).slice(0, 4)], [0, 97, 115, 109]);
    assert.equal((await (await fetch(`${url}/healthz`)).json()).baseUrl, '');
    const config = await (await fetch(`${url}/runtime-config.json`)).json();
    assert.equal(config.sourcePackageAvailable, false);
    assert.equal(config.worldAtlasUrl, null);
    assert.equal(config.worldFlightUrl, null);
    assert.equal((await fetch(`${url}/source-package.json`)).status, 404);
    assert.equal((await fetch(`${url}/flight-package.json`)).status, 404);
    assert.equal((await fetch(`${url}/world-flight/manifest.json`)).status, 404);
    assert.equal((await fetch(`${url}/world-atlas/manifest.json`)).status, 404);
    assert.equal((await fetch(`${url}/.env`)).status, 404);
  });
});

test('Atlas isolates invalid optional ZIPs and serves declared clipped data from a mounted byte revision', async () => {
  const folder = resolve('artifacts/hosting', `atlas-${randomUUID()}`);
  await mkdir(folder, { recursive: true });
  const road = ['road', 'primary', 'Clipped road', 'osm', [[0, 14], [8192, 32]]];
  const building = ['building', 'commercial', 'Tower', 'osm', [[20, 20], [50, 20], [50, 45], [20, 45]]];
  const detailRows = [road, building, ...[1, 228, 1000].map(height => [...building, height])];
  const invalidBuildingRows = [
    ['bad-height-zero', 5, [...building, 0]],
    ['bad-height-too-tall', 5, [...building, 1001]],
    ['bad-height-fraction', 5, [...building, 228.5]],
    ['bad-height-type', 5, [...building, '228']],
    ['bad-height-null', 5, [...building, null]],
    ['bad-height-extra', 5, [...building, 228, 43]],
    ['bad-height-road', 5, [...road, 228]],
    ['bad-height-level', 0, [...building, 228]],
    ['bad-height-source', 5, ['building', 'commercial', 'Tower', 'natural-earth', building[4], 228]],
    ['bad-height-style', 5, ['building', 'tower', 'Tower', 'osm', building[4], 228]],
    ['bad-legacy-style', 5, ['road', 'tram', road[2], road[3], road[4]]],
  ];
  const fixture = `
import hashlib,json,pathlib,struct,sys,zipfile
root = pathlib.Path(sys.argv[1])
base = bytearray(struct.pack('<4sIII', b'WFA1', 2432, 1217, 16384) + struct.pack('<h', 7) * (2432 * 1217) + bytes(2432 * 1217))
struct.pack_into('<h', base, 16, -225)
parts = {'natural-earth': ['Natural Earth', 'Public domain', 'https://www.naturalearthdata.com/'], 'osm': ['© OpenStreetMap contributors', 'ODbL', 'https://www.openstreetmap.org/copyright']}
def pack(id, extents, tiles, features, source='natural-earth', *, comment=b'', other_base=False, added=False, corrupt=False, conflict=False, feature_rows=None):
    source_credits = {source: parts[source]}
    if id == 'area':
        source_credits['copernicus-dem'] = ['Full licensed source credit. ' * 7, 'Copernicus DEM GLO-30 Public License', 'https://example.org/license']
    data = dict(kind='world-atlas-pack', schemaVersion=1, grid='eqc-wft1-v1', id=id, name=id,
        baseSha256='0' * 64 if other_base else hashlib.sha256(base).hexdigest(), terrain=extents,
        features=features, presets=[dict(id='date-line', name='Date line', lon=179.999, lat=0)] if id=='base' else [],
        credits=source_credits)
    with zipfile.ZipFile(root / (id + '.zip'), 'w', compression=zipfile.ZIP_DEFLATED) as out:
        out.comment = comment
        out.writestr('manifest.json', json.dumps(data))
        out.writestr('receipt.json', '{}')
        out.writestr('DATA-NOTICE.txt', 'Fixture test data.')
        if id == 'base':
            out.writestr('base.bin', base)
        for l,x,z in tiles:
            raw = bytearray(struct.pack('<4siii',b'WFT1', l,x,z) + struct.pack('<h', 7) * (33 * 33) + bytes(33 * 33))
            interior = 341 if l == 1 else 782
            if conflict and (l,x,z) == (1,-2,0):
                interior += 1
            struct.pack_into('<h', raw, 16 + 2 * (16 * 33 + 16), interior)
            if corrupt and (x,z) == (-2,0):
                struct.pack_into('<h', raw, 16, 8)
            out.writestr(f'terrain/{l}/{x}/{z}.bin', raw)
        for l,x,z in features:
            end = (262144 >> l) + (1 if id == 'bad-clip' else 0)
            row = ['road','primary','Clipped road',source,[[0,14],[end,32]]]
            rows = [row] if feature_rows is None else feature_rows
            out.writestr(f'features/{l}/{x}/{z}.json', json.dumps(dict(schemaVersion=1, tile=[l,x,z], rows=rows)))
        if added:
            out.writestr('unlisted.bin', b'not declared')

mode = sys.argv[2]
quartet = [(1,x,z) for z in [0,1] for x in [-2,-1]]
area = [[1,-2,0,0,2]]
def refinement(id, w):
    pack(id, area + [[2,w,0,w+2,2]], quartet + [(2,x,z) for x in [w,w+1] for z in [0,1]], [])
if mode in ('start','base-error'):
    if mode == 'base-error':
        struct.pack_into('<I', base, 12, 1024)
    pack('base', [], [], [[0,-1,0]])
if mode == 'start':
    pack('bad-hash', [], [], [], other_base=True)
    pack('bad-entries', [], [], [], added=True)
    pack('bad-clip', [], [], [[5,-1,0]], 'osm')
    pack('missing-support', [[2,0,0,2,2]], [(2,x,z) for x in [0,1] for z in [0,1]], [])
    pack('missing-sibling', [[1,0,0,2,1]], [(1,0,0),(1,1,0)], [])
    pack('wrong-seam', area, quartet, [], corrupt=True)
    pack('z-overlap', area, quartet, [], conflict=True)
    pack('bad-duplicate-key', area + area, quartet, [])
    refinement('a-refine', -4)
    refinement('z-refine', -2)
    good, invalid = json.loads(sys.argv[3])
    pack('layer', [], [], [[5,-1,0]], 'osm', feature_rows=good)
    for ident,level,row in invalid:
        pack(ident, [], [], [[level,-1,0]], row[3], feature_rows=[row])
    pack('osm-overview', [], [], [[0,-1,0]], 'osm')
if mode in ('start','accepted-change'):
    pack('area', area, quartet, [], comment=b'changed archive bytes' if mode=='accepted-change' else b'')
if mode == 'rejected-change':
    pack('bad-clip', [], [], [[5,-1,0]], 'osm', comment=b'rejected archive changed')
`;
  const sha = bytes => createHash('sha256').update(bytes).digest();
  const run = async (mode, check) => {
    await python('-c', fixture, folder, mode, JSON.stringify([detailRows, invalidBuildingRows]));
    await withHost('/atlas-game', check, '', '', '', folder);
  };
  const catalogUrl = '/atlas-game/world-atlas';
  let revision;
  try {
    await run('start', async (url, log) => {
      const config = await (await fetch(`${url}/atlas-game/runtime-config.json`)).json();
      assert.equal(config.worldAtlasUrl, './world-atlas/manifest.json');
      assert.equal(config.worldFlightUrl, null);
      const response = await fetch(`${url}${catalogUrl}/manifest.json`);
      assert.equal(response.headers.get('cache-control'), 'no-store');
      const doc = await response.json();
      assert.deepEqual([doc.kind, doc.schemaVersion, doc.grid], ['world-atlas', 1, 'eqc-wft1-v1']);
      const packIds = ['base', 'a-refine', 'area', 'layer', 'osm-overview', 'z-refine'];
      assert.deepEqual(doc.packs.map(pack => pack.id), packIds);
      assert.equal(doc.packs[0].presets[0].lon, 179.999);
      assert.deepEqual(doc.packs[3].credits.osm, ['© OpenStreetMap contributors', 'ODbL', 'https://www.openstreetmap.org/copyright']);
      revision = doc.revision;
      const accepted = await Promise.all(packIds.map(id => readFile(resolve(folder, `${id}.zip`))));
      assert.equal(revision, sha(Buffer.concat(accepted.map(sha))).toString('hex'));
      for (const name of ['bad-hash', 'bad-entries', 'bad-clip', 'missing-support', 'missing-sibling', 'wrong-seam', 'bad-duplicate-key'])
        assert.match(log(), new RegExp(`optional pack '${name}' excluded`));
      assert.match(log(), /optional pack 'z-overlap' excluded: Terrain key '1\/-2\/0' conflicts with accepted pack 'a-refine'/);

      const assets = `${url}${catalogUrl}/${revision}`;
      const fallback = await fetch(`${assets}/base.bin`);
      assert.equal(fallback.headers.get('cache-control'), 'public, max-age=31536000, immutable');
      const bytes = Buffer.from(await fallback.arrayBuffer());
      assert.equal(bytes.length, 8879248);
      assert.equal(bytes.toString('ascii', 0, 4), 'WFA1');
      assert.equal(bytes.readInt16LE(16), -225);
      assert.equal(sha(bytes).toString('hex'), doc.packs[0].baseSha256);
      const detail = await fetch(`${assets}/tiles/1/-2/0.bin`);
      const tile = Buffer.from(await detail.arrayBuffer());
      assert.match(detail.headers.get('cache-control'), /immutable/);
      assert.deepEqual([tile.readInt32LE(4), tile.readInt32LE(8), tile.readInt32LE(12), tile.readInt16LE(16 + 2 * (16 * 33 + 16))],
        [1, -2, 0, 341]);
      for (const x of [-4, -2]) {
        const fine = await fetch(`${assets}/tiles/2/${x}/0.bin`);
        assert.equal(fine.status, 200, `refinements ordered on both sides of the common parent pack are available at x=${x}`);
        const bytes = Buffer.from(await fine.arrayBuffer());
        assert.equal(bytes.readInt16LE(16 + 2 * (16 * 33 + 16)), 782);
      }
      const feature = await fetch(`${assets}/features/layer/5/-1/0.json`);
      assert.match(feature.headers.get('cache-control'), /immutable/);
      assert.deepEqual(await feature.json(), { schemaVersion: 1, tile: [5, -1, 0], rows: detailRows },
        'legacy rows and real building heights are served without changing the v1 shape or data');
      for (const [id, level] of invalidBuildingRows) {
        assert.match(log(), new RegExp(`optional pack '${id}' excluded`));
        assert.equal((await fetch(`${assets}/features/${id}/${level}/-1/0.json`)).status, 404, id);
      }
      const overview = await (await fetch(`${assets}/features/osm-overview/0/-1/0.json`)).json();
      const naturalEarth = await (await fetch(`${assets}/features/base/0/-1/0.json`)).json();
      assert.deepEqual(overview.rows[0].slice(3), ['osm', [[0, 14], [262144, 32]]]);
      assert.equal(naturalEarth.rows[0][3], 'natural-earth', 'independent L0 sources can share a tile key');
      for (const bad of [`${assets}/tiles/01/-2/0.bin`, `${assets}/tiles/1/-02/0.bin`, `${assets}/tiles/1/-2/-0.bin`,
        `${assets}/tiles/1/152/0.bin`, `${assets}/tiles/1/0/0.bin`, `${assets}/tiles/2/0/0.bin`,
        `${assets}/features/bad-clip/5/-1/0.json`, `${assets}/features/layer/0/-1/0.json`,
        `${assets}/receipt.json`, `${assets}/DATA-NOTICE.txt`, `${url}${catalogUrl}/base.zip`,
        `${url}${catalogUrl}/stale/base.bin`, `${url}/world-atlas/manifest.json`])
        assert.equal((await fetch(bad)).status, 404, bad);
    });
    await run('rejected-change', async url => {
      const doc = await (await fetch(`${url}${catalogUrl}/manifest.json`)).json();
      assert.equal(doc.revision, revision, 'excluded ZIPs cannot invalidate accepted immutable assets');
    });
    await run('accepted-change', async url => {
      const doc = await (await fetch(`${url}${catalogUrl}/manifest.json`)).json();
      assert.notEqual(doc.revision, revision, 'a different ZIP byte stream changes revision even if its entry content is the same');
      assert.equal((await fetch(`${url}${catalogUrl}/${revision}/base.bin`)).status, 404);
      assert.equal((await fetch(`${url}${catalogUrl}/${doc.revision}/tiles/1/-2/0.bin`)).status, 200);
    });
    await run('base-error', async (url, log) => {
      const config = await (await fetch(`${url}/atlas-game/runtime-config.json`)).json();
      assert.equal(config.worldAtlasUrl, null);
      assert.equal((await fetch(`${url}${catalogUrl}/manifest.json`)).status, 404);
      assert.match(log(), /World Flight Atlas disabled: Invalid WFA1/);
      assert.match(await (await fetch(`${url}/atlas-game/`)).text(), /src="\.\/main\.js"/);
      assert.equal((await (await fetch(`${url}/healthz`)).json()).status, 'ready');
    });
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

function worldFlightFixture() {
  const version = '0123456789abcdef0123';
  const regions = ['conus', 'alaska', 'hawaii'].map(id => ({
    id, coverage: [[0, 0, 1, 1], [0, 0, 2, 2], [0, 0, 4, 4]]
  }));
  const conus = regions[0];
  conus.coverage.push([0, 0, 2, 2]);
  conus.patches = [[], [], [], [[4, 0, 6, 2]]];
  conus.cities = [{ id: 'seattle' }, { id: 'san-francisco' }];
  const osm = {
    license: 'ODbL-1.0', attribution: '© OpenStreetMap contributors',
    licenseUrl: 'https://www.openstreetmap.org/copyright',
  };
  const roadPayload = {
    ...osm,
    roads: [[10, 'arterial', 'surface', [[1, 2], [4, 8]]], [11, 'freeway', 'bridge', [[4, 8, 24], [7, 9, 26]]]],
  };
  const catalogData = { schemaVersion: 1, kind: 'world-flight-terrain', side: 33, version,
    spacing: Array.from({ length: 9 }, (_, i) => 8192 >> i), regions };
  const roadArea = { id: 'seattle', featureCount: 2, pointCount: 4, bounds: [1, 2, 7, 9] };
  return { version, regions, conus, catalogData, roadArea, roadPayload,
    footprints: JSON.stringify({ ...osm, buildings: [[42, 18, 'height', [[1, 2], [3, 4], [6, 2]]]] }) };
}

test('geographic terrain serves only declared detail patches and scenery databases at the mounted revision', async () => {
  const root = resolve('artifacts/hosting');
  await mkdir(root, { recursive: true });
  const scenery = await mkdtemp(resolve(root, 'world-flight-'));
  const folder = resolve(scenery, 'seattle');
  const manifest = resolve(folder, 'manifest.json'), roadPath = resolve(folder, 'seattle-roads.json');
  const { version, conus, catalogData, roadArea, roadPayload, footprints } = worldFlightFixture();
  const roadBody = JSON.stringify(roadPayload);
  const body = JSON.stringify(catalogData);
  const tile = Buffer.alloc(16 + 33 * 33 * 3);
  tile.write('WFT1');
  tile.writeInt16LE(745, 16);
  try {
    await mkdir(folder);
    await writeFile(manifest, body);
    await writeFile(roadPath, roadBody);
    for (const city of [...conus.cities.map(city => city.id), 'other'])
      await writeFile(resolve(folder, `${city}-buildings.json`), footprints);
    for (const id of ['conus', 'alaska', 'hawaii']) {
      const tiles = resolve(folder, `tiles/${id}/0/0`);
      await mkdir(tiles, { recursive: true });
      await writeFile(resolve(tiles, '0.bin'), tile);
    }
    for (const x of [2, 4]) {
      const tiles = resolve(folder, `tiles/conus/3/${x}`);
      await mkdir(tiles, { recursive: true });
      await writeFile(resolve(tiles, '0.bin'), tile);
    }
    await withHost('/map', async (url, log) => {
      const config = await (await fetch(`${url}/map/runtime-config.json`)).json();
      assert.equal(config.worldFlightUrl, './world-flight/manifest.json');
      assert.equal(config.worldAtlasUrl, null);
      assert.doesNotMatch(log(), /missing base\.zip/);
      const catalog = await fetch(new URL(config.worldFlightUrl, `${url}/map/`));
      assert.equal(await catalog.text(), body);
      assert.equal(catalog.headers.get('cache-control'), 'no-store');
      const base = `${url}/map/world-flight/${version}`;
      const heightData = await fetch(`${base}/tiles/conus/3/4/0.bin`);
      assert.equal(heightData.status, 200);
      assert.match(heightData.headers.get('cache-control'), /immutable/);
      assert.deepEqual(Buffer.from(await heightData.arrayBuffer()), tile);
      for (const { id } of conus.cities)
        assert.equal(await (await fetch(`${base}/${id}-buildings.json`)).text(), footprints);
      for (const path of [`${base}/tiles/conus/3/2/0.bin`, `${base}/tiles/unknown/0/0/0.bin`, `${base}/other-buildings.json`,
        `${url}/map/world-flight/old/tiles/conus/0/0/0.bin`, `${url}/world-flight/manifest.json`, `${base}/seattle-roads.json`,
        `${url}/map/seattle/manifest.json`])
        assert.equal((await fetch(path)).status, 404);
    }, '', '', '', scenery);

    conus.roads = roadArea;
    await writeFile(manifest, JSON.stringify(catalogData));
    await withHost('/map', async url => {
      const base = `${url}/map/world-flight`;
      const layer = await fetch(`${base}/${version}/seattle-roads.json`);
      assert.equal(layer.status, 200);
      assert.match(layer.headers.get('cache-control'), /immutable/);
      assert.match(layer.headers.get('content-type'), /application\/json/);
      assert.equal(await layer.text(), roadBody);
      for (const path of [`${base}/old/seattle-roads.json`, `${base}/${version}/san-francisco-roads.json`])
        assert.equal((await fetch(path)).status, 404);
    }, '', '', '', scenery);
    roadPayload.roads[1][3][1][2] = 900;
    await writeFile(roadPath, JSON.stringify(roadPayload));
    await assert.rejects(withHost('', () => assert.fail('Out-of-range bridge deck must be rejected'),
      '', '', '', scenery), /Seattle road database or metadata is malformed/);
    await rm(roadPath);
    await assert.rejects(withHost('', () => assert.fail('A declared road database cannot be missing'),
      '', '', '', scenery), /Seattle road database is missing/);
    await writeFile(roadPath, roadBody);

    conus.patches[3][0] = [0, 0, 4, 2];
    await writeFile(manifest, JSON.stringify(catalogData));
    await assert.rejects(withHost('', () => assert.fail('Overlapping patches must be rejected'),
      '', '', '', scenery), /detail patches must not overlap/);
    await rm(manifest);
    await assert.rejects(withHost('', () => assert.fail('An extracted Seattle package cannot lack its manifest'),
      '', '', '', scenery), /missing seattle\/manifest\.json/);
  } finally { await rm(scenery, { recursive: true, force: true }); }
});

test('portable World Flight ZIP packs and serves exact compiled assets without exposing the archive', async t => {
  const root = resolve('artifacts/hosting');
  await mkdir(root, { recursive: true });
  const directory = await mkdtemp(resolve(root, 'world-flight-zip-'));
  const scenery = resolve(directory, 'scenery');
  const folder = resolve(scenery, 'seattle'), archive = resolve(directory, 'world-flight.zip');
  const seattle = resolve(scenery, 'seattle.zip');
  const { version, regions, conus, catalogData, roadArea, roadPayload, footprints } = worldFlightFixture();
  conus.roads = roadArea;
  const entries = new Map();
  for (const region of regions)
    for (const [level, coverage] of region.coverage.entries())
      for (const [west, south, east, north] of [coverage, ...(region.patches?.[level] ?? [])])
        for (let x = west; x < east; x++)
          for (let z = south; z < north; z++) {
            const tile = Buffer.alloc(16 + 33 * 33 * 3);
            tile.write('WFT1');
            [level, x, z].forEach((value, i) => tile.writeInt32LE(value, 4 * (i + 1)));
            tile.writeInt16LE(745 + entries.size, 16);
            entries.set(`tiles/${region.id}/${level}/${x}/${z}.bin`, tile);
          }
  const receipt = JSON.stringify({ dataRevision: version, tilesWritten: entries.size, inputSha256: { 'fixture.tif': 'a'.repeat(64) } });
  entries.set('manifest.json', JSON.stringify(catalogData));
  entries.set('receipt.json', receipt);
  entries.set('seattle-roads.json', JSON.stringify(roadPayload));
  entries.set('seattle-buildings.json', footprints);
  entries.set('san-francisco-buildings.json', footprints.replace('18', '27'));
  const pack = output => python('tools/WorldFlight/pack.py', '--input', folder, '--output', output);
  const host = (check, path = scenery, extraEnv = {}) => withHost('/map', check, '', '', '', path, extraEnv);
  try {
    await Promise.all([...entries].map(async ([name, data]) => {
      const path = resolve(folder, name);
      await mkdir(dirname(path), { recursive: true });
      await writeFile(path, data);
    }));
    await writeFile(resolve(folder, 'unused-raw.tif'), 'not compiled data');
    await pack(archive);
    const original = await readFile(archive);
    await assert.rejects(pack(archive), /File exists/);
    assert.deepEqual(await readFile(archive), original, 'a successful archive cannot be overwritten');
    const { stdout } = await python('-c', `
import json, sys, zipfile
from pathlib import Path
path, folder = map(Path, sys.argv[1:])
with zipfile.ZipFile(path) as source:
    if source.testzip() is not None:
        raise ValueError("Invalid archive CRC")
    print(json.dumps([source.namelist(), source.read("DATA-NOTICE.txt").decode(), source.read("receipt.json").decode()]))
    with zipfile.ZipFile(folder / "partial.zip", "w") as out:
        manifest = json.loads(source.read("manifest.json"))
        manifest["regions"][0].pop("roads")
        manifest["regions"][0].pop("cities")
        out.writestr("manifest.json", json.dumps(manifest))
        for name in source.namelist():
            if name not in ("manifest.json", "tiles/conus/3/1/1.bin"):
                out.writestr(name, source.read(name))
        outside = bytearray(source.read("tiles/conus/3/4/0.bin"))
        outside[8:12] = (2).to_bytes(4, "little")
        out.writestr("tiles/conus/3/2/0.bin", outside)
    with zipfile.ZipFile(folder / "duplicate.zip", "w") as out:
        out.writestr("manifest.json", source.read("manifest.json"))
        out.writestr("manifest.json", source.read("manifest.json"))
with zipfile.ZipFile(folder / "unsafe.zip", "w") as out:
    out.writestr("../manifest.json", "{}")
with zipfile.ZipFile(folder / "short-tile.zip", "w") as out:
    out.writestr("tiles/conus/3/4/0.bin", b"WFT1")
`, archive, directory);
    const [names, notice, packedReceipt] = JSON.parse(stdout);
    assert.deepEqual(names.sort(), [...entries.keys(), 'DATA-NOTICE.txt'].sort());
    assert.match(notice, /© OpenStreetMap contributors/);
    assert.match(notice, /Open Database License \(ODbL\) v1\.0/);
    assert.equal(packedReceipt, receipt);

    // A different, usable extracted version proves that the reserved ZIP takes precedence.
    await writeFile(resolve(folder, 'manifest.json'), JSON.stringify({ ...catalogData, version: 'e'.repeat(20) }));
    await copyFile(archive, seattle);
    assert.deepEqual(await readFile(seattle), original, 'migration is a byte-for-byte rename or copy');
    await host(async (url, log) => {
      const mount = `${url}/map/`;
      const config = await (await fetch(`${mount}runtime-config.json`)).json();
      assert.equal(config.worldFlightUrl, './world-flight/manifest.json');
      assert.equal(config.worldAtlasUrl, null);
      assert.doesNotMatch(log(), /missing base\.zip/);
      const manifest = await fetch(new URL(config.worldFlightUrl, mount));
      assert.equal(manifest.status, 200);
      assert.equal(manifest.headers.get('cache-control'), 'no-store');
      assert.equal(await manifest.text(), entries.get('manifest.json'));

      const base = `${mount}world-flight/${version}/`;
      const samples = ['tiles/conus/3/4/0.bin', 'tiles/hawaii/2/3/2.bin', 'tiles/alaska/1/1/0.bin',
        'seattle-buildings.json', 'san-francisco-buildings.json', 'seattle-roads.json'];
      await Promise.all([...samples, ...samples.slice(0, 3)].map(async (name, i) => {
        const response = await fetch(`${base}${name}?request=${i}`);
        assert.equal(response.status, 200, name);
        assert.match(response.headers.get('cache-control'), /immutable/, name);
        assert.match(response.headers.get('content-type'), name.endsWith('.bin') ? /application\/octet-stream/ : /application\/json/, name);
        assert.deepEqual(Buffer.from(await response.arrayBuffer()), Buffer.from(entries.get(name)), name);
      }));
      for (const path of [`${base}tiles/conus/-1/0/0.bin`, `${base}tiles/conus/3/4096/0.bin`,
        `${base}tiles/conus%2f..%2falaska/0/0/0.bin`, `${base}tiles/unknown/0/0/0.bin`,
        `${mount}world-flight/old/tiles/conus/0/0/0.bin`, `${mount}world-flight/old/seattle-roads.json`,
        `${url}/world-flight/manifest.json`, `${base}receipt.json`, `${base}DATA-NOTICE.txt`,
        `${base}world-flight.zip`, `${mount}world-flight.zip`, `${mount}seattle.zip`,
        `${mount}seattle/manifest.json`, `${mount}world-atlas/manifest.json`]) {
        assert.equal((await fetch(path)).status, 404, path);
      }
    });

    await writeFile(resolve(folder, 'manifest.json'), entries.get('manifest.json'));
    await copyFile(resolve(directory, 'partial.zip'), seattle);
    await host(async url => {
      const base = `${url}/map/world-flight/${version}`;
      assert.equal(await (await fetch(`${base}/seattle-buildings.json`)).text(), footprints);
      for (const path of ['tiles/conus/3/1/1.bin', 'tiles/conus/3/2/0.bin',
        'san-francisco-buildings.json', 'seattle-roads.json'])
        assert.equal((await fetch(`${base}/${path}`)).status, 404, path);
    });

    await t.test('invalid, ambiguous and linked archives fail at startup', async () => {
      await writeFile(resolve(directory, 'not-a-zip.zip'), 'not a ZIP');
      for (const [name, expected] of [
        ['not-a-zip.zip', /Cannot open World Flight ZIP/],
        ['duplicate.zip', /Duplicate package entry 'manifest.json'/],
        ['unsafe.zip', /Unexpected or unsafe package entry name/],
        ['short-tile.zip', /Invalid package entry size for 'tiles\/conus\/3\/4\/0\.bin'/],
      ]) {
        await copyFile(resolve(directory, name), seattle);
        await assert.rejects(host(() => assert.fail(`Must reject ${name} rather than fall back to the extracted copy`)), expected);
      }
      await rm(seattle);
      await symlink(archive, seattle);
      await assert.rejects(host(() => assert.fail('Must reject a linked Seattle ZIP')), /must not be symbolic links/);
      await rm(seattle);
    });

    await t.test('Seattle and Atlas share a private mount without changing either catalog revision', async () => {
      const combined = resolve(directory, 'combined'), alias = resolve(directory, 'private-alias');
      const baseFile = resolve(combined, 'base.zip'), seattleFile = resolve(combined, 'seattle.zip');
      await mkdir(combined);
      await symlink(combined, alias, 'dir');
      await python('-c', `
import hashlib,json,pathlib,struct,sys,zipfile
root = pathlib.Path(sys.argv[1])
base = struct.pack('<4sIII', b'WFA1', 2432, 1217, 16384) + struct.pack('<h', 23) * (2432 * 1217) + bytes(2432 * 1217)
for ident, keys, source in (('base', [], 'natural-earth'), ('harbor-osm', [[5,0,0]], 'osm')):
    manifest = dict(kind='world-atlas-pack', schemaVersion=1, grid='eqc-wft1-v1', id=ident, name=ident,
        baseSha256=hashlib.sha256(base).hexdigest(), terrain=[], features=keys, presets=[],
        credits={source: [source, 'fixture notice', 'https://example.org/license']})
    with zipfile.ZipFile(root / (ident + '.zip'), 'w', zipfile.ZIP_DEFLATED) as out:
        out.writestr('manifest.json', json.dumps(manifest))
        out.writestr('receipt.json', '{}')
        out.writestr('DATA-NOTICE.txt', 'Fixture data')
        if ident == 'base':
            out.writestr('base.bin', base)
        else:
            out.writestr('features/5/0/0.json', json.dumps(dict(schemaVersion=1, tile=[5,0,0],
                rows=[['building','commercial','Harbor Tower','osm',[[0,0],[300,0],[300,300]],96]])))
`, combined);

      let atlasManifest, atlasRevision;
      await host(async url => {
        const config = await (await fetch(`${url}/map/runtime-config.json`)).json();
        assert.equal(config.worldFlightUrl, null, 'the old setting alone no longer enables U.S. scenery');
        assert.equal(config.worldAtlasUrl, null);
      }, '', { STUNT_WORLD_FLIGHT: archive });
      await copyFile(archive, resolve(combined, 'world-flight.zip'));
      await copyFile(archive, resolve(combined, 'Seattle.zip'));
      await host(async url => {
        const mount = `${url}/map/`;
        const config = await (await fetch(`${mount}runtime-config.json`)).json();
        assert.equal(config.worldFlightUrl, null, 'neither the old setting nor a different filename may load Seattle');
        assert.equal(config.worldAtlasUrl, './world-atlas/manifest.json');
        assert.equal((await fetch(`${mount}world-flight/manifest.json`)).status, 404);
        const response = await fetch(new URL(config.worldAtlasUrl, mount));
        atlasManifest = await response.text();
        const doc = JSON.parse(atlasManifest);
        assert.deepEqual(doc.packs.map(pack => pack.id), ['base', 'harbor-osm']);
        atlasRevision = doc.revision;
      }, combined, { STUNT_WORLD_FLIGHT: archive });
      await rm(resolve(combined, 'world-flight.zip'));
      await rm(resolve(combined, 'Seattle.zip'));

      await copyFile(archive, seattleFile);
      // This is exactly the Atlas ZIP candidate limit (including base.zip), before Seattle is counted.
      await python('-c', `
import pathlib,sys
root = pathlib.Path(sys.argv[1])
for i in range(510):
    (root / f"Invalid-{i}.zip").touch()
`, combined);
      await host(async (url, log) => {
        const mount = `${url}/map/`;
        const config = await (await fetch(`${mount}runtime-config.json`)).json();
        assert.equal(config.worldAtlasUrl, './world-atlas/manifest.json');
        assert.equal(config.worldFlightUrl, './world-flight/manifest.json');
        assert.ok(!JSON.stringify(config).includes(directory));
        assert.equal(await (await fetch(new URL(config.worldAtlasUrl, mount))).text(), atlasManifest,
          'Seattle is not counted, parsed, or hashed by Atlas');
        assert.doesNotMatch(log(), /optional pack 'seattle'|too many ZIP candidates/);
        assert.equal(await (await fetch(new URL(config.worldFlightUrl, mount))).text(), entries.get('manifest.json'));
        const legacy = `${mount}world-flight/${version}/`;
        assert.equal(await (await fetch(`${legacy}seattle-roads.json`)).text(), entries.get('seattle-roads.json'));
        assert.deepEqual(Buffer.from(await (await fetch(`${legacy}tiles/conus/3/4/0.bin`)).arrayBuffer()),
          entries.get('tiles/conus/3/4/0.bin'));
        const atlas = `${mount}world-atlas/${atlasRevision}/`;
        assert.deepEqual((await (await fetch(`${atlas}features/harbor-osm/5/0/0.json`)).json()).rows[0],
          ['building', 'commercial', 'Harbor Tower', 'osm', [[0, 0], [300, 0], [300, 300]], 96]);
        for (const path of [`${mount}seattle.zip`, `${mount}world-flight.zip`, `${mount}base.zip`,
          `${mount}harbor-osm.zip`, `${legacy}seattle.zip`, `${atlas}seattle.zip`])
          assert.equal((await fetch(path)).status, 404, path);
      }, alias);

      await writeFile(baseFile, 'malformed Atlas base');
      await host(async (url, log) => {
        const config = await (await fetch(`${url}/map/runtime-config.json`)).json();
        assert.equal(config.worldAtlasUrl, null);
        assert.equal(config.worldFlightUrl, './world-flight/manifest.json');
        assert.match(log(), /World Flight Atlas disabled/);
        assert.equal(await (await fetch(`${url}/map/world-flight/${version}/seattle-roads.json`)).text(),
          entries.get('seattle-roads.json'));
      }, combined);
    });

    await t.test('missing scenery is diagnosed and a symlink into the public bundle is rejected', async () => {
      const empty = resolve(directory, 'empty'), publicBundle = resolve(directory, 'public');
      const publicScenery = resolve(publicBundle, 'scenery'), alias = resolve(directory, 'public-alias');
      await mkdir(empty);
      await host(async (url, log) => {
        const config = await (await fetch(`${url}/map/runtime-config.json`)).json();
        assert.equal(config.worldAtlasUrl, null);
        assert.equal(config.worldFlightUrl, null);
        assert.match(log(), /missing base\.zip/);
      }, empty);
      await host(async (_url, log) => {
        assert.match(log(), /configured scenery directory does not exist/);
      }, resolve(directory, 'does-not-exist'));
      await mkdir(publicScenery, { recursive: true });
      await writeFile(resolve(publicBundle, 'index.html'), '<title>Test bundle</title>');
      await copyFile(archive, resolve(publicScenery, 'seattle.zip'));
      await symlink(publicScenery, alias, 'dir');
      await assert.rejects(host(() => assert.fail('Must not serve a private archive under the public bundle'),
        alias, { STUNT_BUNDLE: publicBundle }), /outside the public WASM bundle/);
    });

    await t.test('pack fails cleanly for a missing, linked or mislabeled tile', async () => {
      const tile = resolve(folder, 'tiles/conus/3/1/1.bin'), failed = resolve(directory, 'failed.zip');
      await rm(tile);
      await assert.rejects(pack(failed), /tiles\/conus\/3\/1\/1\.bin/);
      await assert.rejects(readFile(failed), { code: 'ENOENT' });
      await symlink(resolve(folder, 'tiles/conus/3/1/0.bin'), tile);
      await assert.rejects(pack(failed), /links are not allowed/);
      await assert.rejects(readFile(failed), { code: 'ENOENT' });
      await rm(tile);
      await writeFile(tile, entries.get('tiles/conus/3/1/0.bin'));
      await assert.rejects(pack(failed), /tiles\/conus\/3\/1\/1\.bin: malformed WFT1 tile/);
      await assert.rejects(readFile(failed), { code: 'ENOENT' });
    });
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('original flight data is opt-in, mount-relative, non-cached and never served from a caller-selected path', async () => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'flight-'));
    const path = resolve(directory, 'original-flight.json');
    const payload = JSON.stringify({ schemaVersion: 1, kind: 'fa18-amiga-runtime', probe: 'synthetic-transport-only' });
    await writeFile(path, payload);
    try {
      await withHost('/stunt-car-racer', async url => {
        const base = `${url}/stunt-car-racer/`;
        const config = await (await fetch(`${base}runtime-config.json`)).json();
        assert.equal(config.flightPackageAvailable, true);
        assert.equal(config.flightPackageUrl, './flight-package.json');
        assert.ok(!JSON.stringify(config).includes(directory));
        const response = await fetch(new URL(config.flightPackageUrl, base));
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        assert.equal(await response.text(), payload);
        assert.equal((await fetch(`${url}/flight-package.json`)).status, 404);
        assert.equal((await fetch(`${base}original-flight.json`)).status, 404);
        assert.equal(await (await fetch(`${base}flight-package.json?path=../../other.json`)).text(), payload);
      }, '', '', path);
      await writeFile(path, JSON.stringify({ schemaVersion: 1, kind: 'not-flight' }));
      await assert.rejects(withHost('', () => assert.fail('Wrong-kind original data must not start'), '', '', path),
        /not a supported fa18-amiga-runtime/);
    } finally { await rm(directory, { recursive: true, force: true }); }
});

test('external source packages are opt-in, mount-relative and never cached or copied into static assets', async () => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'source-'));
    const path = resolve(directory, 'package.json');
    const payload = JSON.stringify({ schemaVersion: 1, kind: 'stunt-amiga-source-runtime', probe: 'synthetic-host-fixture' });
    await writeFile(path, payload);
    try {
      await withHost('/stunt-car-racer', async url => {
        const base = `${url}/stunt-car-racer/`;
        const configuration = await fetch(`${base}runtime-config.json`);
        assert.equal(configuration.headers.get('cache-control'), 'no-store');
        const config = await configuration.json();
        assert.equal(config.sourcePackageAvailable, true);
        assert.equal(config.sourcePackageUrl, './source-package.json?id=reference');
        assert.deepEqual(config.tracks.map(({ id, status }) => ({ id, status })),
          [{ id: 'reference', status: 'available' }]);
        const response = await fetch(new URL(config.sourcePackageUrl, base));
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        assert.equal(await response.text(), payload);
        assert.equal((await fetch(`${url}/source-package.json`)).status, 404);
        assert.equal((await fetch(`${base}artifacts/hosting/package.json`)).status, 404);
      }, path);
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });

test('external catalog exposes distinct mounted packages and explicit unsupported entries without filesystem paths', async () => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'catalog-'));
    const manifest = resolve(directory, 'track-catalog.json');
    const packageFor = name => JSON.stringify({ schemaVersion: 1, kind: 'stunt-amiga-source-runtime', name });
    const payloads = { first: packageFor('First fixture'), second: packageFor('Second fixture') };
    await writeFile(resolve(directory, 'first.json'), payloads.first);
    await writeFile(resolve(directory, 'second.json'), payloads.second);
    await writeFile(manifest, JSON.stringify({ schemaVersion: 1, tracks: [
      { id: 'first', name: 'First fixture', group: 'Reference', status: 'available', packagePath: 'first.json' },
      { id: 'second', name: 'Second fixture', group: 'TNT', status: 'available', packagePath: 'second.json' },
      { id: 'unsupported', name: 'Unsupported fixture', group: 'TNT', status: 'unsupported', packagePath: null,
        reason: 'Missing source height samples; no replacement road was invented.' },
    ] }));
    try {
      await withHost('/stunt-car-racer', async url => {
        const base = `${url}/stunt-car-racer/`;
        const response = await fetch(`${base}runtime-config.json`);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        const raw = await response.text(), config = JSON.parse(raw);
        assert.equal(config.sourcePackageUrl, './source-package.json?id=first');
        assert.equal(config.tracks.length, 3);
        assert.ok(!raw.includes(directory) && !raw.includes('packagePath'), 'configuration must not expose host paths');
        for (const id of ['first', 'second']) {
          const track = config.tracks.find(track => track.id === id);
          assert.equal(track.sourcePackageUrl, `./source-package.json?id=${id}`);
          const packageResponse = await fetch(new URL(track.sourcePackageUrl, base));
          assert.equal(packageResponse.headers.get('cache-control'), 'no-store');
          assert.equal(await packageResponse.text(), payloads[id]);
        }
        const unsupported = config.tracks.find(track => track.id === 'unsupported');
        assert.equal(unsupported.status, 'unsupported');
        assert.equal(unsupported.sourcePackageUrl, null);
        assert.match(unsupported.reason, /Missing source height samples/);
        for (const id of ['unsupported', 'unknown', '../first.json']) {
          assert.equal((await fetch(`${base}source-package.json?id=${encodeURIComponent(id)}`)).status, 404);
        }
        assert.equal((await fetch(`${url}/source-package.json?id=first`)).status, 404);
        assert.equal((await fetch(`${base}first.json`)).status, 404);
      }, '', manifest);
    } finally { await rm(directory, { recursive: true, force: true }); }
  });

test('invalid catalogs fail startup instead of serving ambiguous, escaping or linked packages', async t => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'invalid-catalog-'));
    const manifest = resolve(directory, 'track-catalog.json');
    const packagePath = resolve(directory, 'package.json');
    const entry = { id: 'fixture', name: 'Fixture', group: 'Reference', status: 'available', packagePath: 'package.json' };
    await writeFile(packagePath, JSON.stringify({ schemaVersion: 1, kind: 'stunt-amiga-source-runtime', name: 'Fixture' }));
    await symlink(packagePath, resolve(directory, 'linked.json'));
    try {
      for (const [name, tracks, expected] of [
        ['duplicate identities', [entry, entry], /duplicate external track id/],
        ['parent traversal', [{ ...entry, packagePath: '../package.json' }], /beneath the catalog directory/],
        ['absolute package paths', [{ ...entry, packagePath }], /beneath the catalog directory/],
        ['linked packages', [{ ...entry, packagePath: 'linked.json' }], /beneath the catalog directory/],
        ['unsupported without explanation', [{ ...entry, status: 'unsupported', packagePath: null }], /reason/],
      ]) {
        await t.test(name, async () => {
          await writeFile(manifest, JSON.stringify({ schemaVersion: 1, tracks }));
          await assert.rejects(withHost('', () => assert.fail('Invalid configuration must not start'), '', manifest), expected);
        });
      }
      await t.test('conflicting single and catalog configuration', async () => {
        await assert.rejects(withHost('', () => assert.fail('Conflicting configuration must not start'), packagePath, manifest),
          /either STUNT_TRACK_CATALOG or STUNT_AMIGA_PACKAGE/);
      });
    } finally { await rm(directory, { recursive: true, force: true }); }
});

test('runtime BASE_URL mounts HTML, modules and WASM without rebuilding the bundle', async () => {
  await withHost('/stunt-car-racer/', async url => {
    const canonical = await fetch(`${url}/stunt-car-racer?from=proxy`, { redirect: 'manual' });
    assert.equal(canonical.status, 308);
    assert.equal(canonical.headers.get('location'), '/stunt-car-racer/?from=proxy');
    assert.equal((await fetch(`${url}/`)).status, 404);
    assert.equal((await fetch(`${url}/main.js`)).status, 404);
    assert.equal((await fetch(`${url}/stunt-car-racer-extra/main.js`)).status, 404);
    const page = await fetch(`${url}/stunt-car-racer/`);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /href="\.\/styles\.css"/);
    const entry = await fetch(`${url}/stunt-car-racer/main.js`);
    assert.equal(entry.status, 200);
    assert.match(await entry.text(), /import\('\.\/_framework\/dotnet\.js'\)/);
    assert.equal((await fetch(`${url}/stunt-car-racer/engine/input.js`)).status, 200);
    assert.equal((await fetch(`${url}/stunt-car-racer/_framework/StuntRacer.Core.wasm`)).status, 200);
    const health = await (await fetch(`${url}/healthz`)).json();
    assert.equal(health.status, 'ready');
    assert.equal(health.baseUrl, '/stunt-car-racer');
  });
});
