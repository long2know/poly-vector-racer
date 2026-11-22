import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { setImmediate as settle } from 'node:timers/promises';
import test from 'node:test';
import {
  atlasCoordinates, atlasGrid, decodeAtlasBase, readAtlasBase, readAtlasCatalog, WorldAtlasTerrain,
} from '../../src/StuntRacer.Browser/wwwroot/world-atlas.js';

const width = 2432, rows = 1217, step = 16384, { halfWidth: dateLine, halfHeight: pole } = atlasGrid;
const revision = 'a'.repeat(64);
const credits = {
  'natural-earth': ['Made with Natural Earth', 'Public domain', 'https://www.naturalearthdata.com/'],
  osm: ['© OpenStreetMap contributors', 'ODbL', 'https://www.openstreetmap.org/copyright'],
};
const pack = (baseSha256, id, terrain = [], features = []) => ({
  kind: 'world-atlas-pack', schemaVersion: 1, grid: 'eqc-wft1-v1', id, name: id, baseSha256,
  terrain, features, presets: [], credits,
});

function baseBytes(height = 200) {
  const raw = new Uint8Array(16 + width * rows * 3), view = new DataView(raw.buffer);
  view.setUint32(0, 0x57464131);
  view.setUint32(4, width, true);
  view.setUint32(8, rows, true);
  view.setUint32(12, step, true);
  for (let i = 0; i < width * rows; ++i) view.setInt16(16 + i * 2, height, true);
  return raw;
}

function wft(level, x, z, height, water = 0) {
  const raw = new Uint8Array(3283), view = new DataView(raw.buffer);
  view.setUint32(0, 0x57465431);
  [level, x, z].forEach((value, index) => view.setInt32(4 * (index + 1), value, true));
  const i = 16 * 33 + 16;
  view.setInt16(16 + 2 * i, height, true);
  raw[16 + 2 * 33 * 33 + i] = water;
  return raw;
}

async function catalogFor(raw, makePacks) {
  const hash = createHash('sha256').update(raw).digest('hex');
  return readAtlasCatalog('./world-atlas/manifest.json', {
    fetch: async (url, options) => {
      assert.match(url.pathname, /\/world-atlas\/manifest\.json$/);
      assert.equal(options.cache, 'no-store');
      return Response.json({ kind: 'world-atlas', schemaVersion: 1, grid: 'eqc-wft1-v1', revision,
        packs: makePacks(hash) });
    },
  });
}

const visible = frame => frame.tiles.filter(tile => !tile.pending);
const pending = frame => frame.tiles.filter(tile => tile.pending);
const acknowledge = (terrain, frame, count = Infinity) =>
  pending(frame).slice(0, count).forEach(tile => terrain.ready(tile.key));

test('Atlas browser indexes shared supporting terrain once and keeps unique refinements in either pack order', async () => {
  const raw = baseBytes();
  const shared = [1, 0, 0, 2, 2];
  for (const reverse of [false, true]) {
    const catalog = await catalogFor(raw, hash => {
      const packs = [pack(hash, 'band', [shared]), pack(hash, 'refine', [shared, [2, 0, 0, 2, 2]])];
      return [pack(hash, 'base'), ...(reverse ? packs.reverse() : packs)];
    });
    assert.deepEqual([...catalog.groups].sort(), ['0/0/0', '1/0/0']);
  }
  await assert.rejects(catalogFor(raw, hash => [pack(hash, 'base'), pack(hash, 'duplicate', [shared, shared])]),
    /Duplicate Atlas terrain key in one pack/);
});

test('Atlas base preload verifies signed WFA1 and renders identical canonical samples across the date line and poles', async () => {
  const raw = baseBytes(), view = new DataView(raw.buffer);
  const point = (x, z, y, water = 0) => {
    const i = z * width + x;
    view.setInt16(16 + i * 2, y, true);
    raw[16 + width * rows * 2 + i] = water;
  };
  point(2431, 608, -200); point(0, 608, 100, 2);
  point(2431, 609, -40, 1); point(0, 609, 300);
  point(0, 0, -72, 2); point(0, 1216, 815);
  const catalog = await catalogFor(raw, hash => [pack(hash, 'base')]);
  const { base, bytes } = await readAtlasBase(catalog, { fetch: async url => {
    assert.match(url.pathname, new RegExp(`/${revision}/base\\.bin$`));
    return new Response(raw);
  } });
  assert.equal(bytes.byteLength, 8879248);
  const terrain = new WorldAtlasTerrain(catalog, base);
  assert.equal(terrain.sample(dateLine - 12288, 12288), 5);
  assert.equal(terrain.sample(dateLine - 4096, 4096), 75);
  assert.equal(terrain.isWater(dateLine - 4096, 4096), true);
  assert.equal(terrain.sample(dateLine, 0), 100);
  assert.equal(terrain.sample(-dateLine, pole), 815);
  assert.equal(terrain.sample(dateLine, -pole), -72);
  assert.deepEqual(atlasCoordinates(dateLine, pole), [-180, 90]);
  const east = terrain.frame(dateLine - 1, pole, 140000, performance.now());
  const west = terrain.frame(-dateLine + 1, pole, 140000, performance.now());
  const identities = frame => frame.tiles.map(tile => tile.key).sort();
  assert.deepEqual(identities(east), identities(west));
  assert.equal(new Set(identities(east)).size, east.tiles.length);
  const lastEast = east.tiles.find(tile => tile.x + 32 * tile.step === dateLine);
  const firstWest = west.tiles.find(tile => tile.x === -dateLine);
  assert.equal(lastEast.heights[33 * 33 - 1], 815);
  assert.equal(firstWest.heights[32 * 33], 815);
  assert.ok(west.tiles.every(tile => tile.z < pole && tile.z + 32 * tile.step <= pole), 'there is no wrapped Z row');
  terrain.dispose();

  raw[20] ^= 1;
  await assert.rejects(readAtlasBase(catalog, { fetch: async () => new Response(raw) }), /fallback identity/);
  view.setUint32(12, 8192, true);
  assert.throws(() => decodeAtlasBase(raw), /WFA1 fallback header/);
});

test('Atlas retains the current terrain while detail is downloading, during low flight, and until the whole GPU quartet is ready', async () => {
  const raw = baseBytes(0);
  const catalog = await catalogFor(raw, hash => [pack(hash, 'base'), pack(hash, 'area', [[1, 0, 0, 2, 2]])]);
  const downloads = new Map(), installed = [], removed = [];
  const craft = { y: 1900, speed: 150, onGround: false };
  const terrain = new WorldAtlasTerrain(catalog, decodeAtlasBase(raw), {
    aircraft: () => craft,
    install: (...args) => installed.push(args),
    remove: (...args) => removed.push(args),
  }, { fetch: url => new Promise(resolve => downloads.set(url.pathname.split('/tiles/')[1], resolve)) });
  let x = 65536;
  const frame = () => terrain.frame(x, 65536, 238000, performance.now());
  try {
    const bootstrap = frame();
    assert.ok(visible(bootstrap).some(tile => tile.key === '-1/0/0' && tile.heights[0] === 0));
    assert.deepEqual(pending(bootstrap).map(tile => tile.step), [8192, 8192, 8192, 8192]);
    acknowledge(terrain, bootstrap);
    const beforeDownload = frame();
    assert.ok(visible(beforeDownload).some(tile => tile.key === '0/0/0'));
    assert.equal(downloads.size, 4);
    const reply = key => {
      const [, tx, tz] = key.match(/1\/(\d)\/(\d)\.bin$/);
      downloads.get(key)(new Response(wft(1, +tx, +tz, 380, 2)));
    };
    [...downloads.keys()].slice(0, 3).forEach(reply);
    await settle();
    assert.deepEqual(pending(frame()), [], 'three byte-complete siblings do not start a GPU cut');
    assert.equal(terrain.sample(65536, 65536), 0);
    reply([...downloads.keys()].at(-1));
    await settle();

    craft.y = 420;
    for (let i = 0; i < 3; ++i) {
      assert.deepEqual(pending(frame()), []);
      assert.equal(terrain.diagnostics().terrainPending, 1, 'buffered siblings are decoded across frames, not in one fetch batch');
    }
    assert.deepEqual(pending(frame()), []);
    assert.equal(terrain.diagnostics().deferred, 1);
    assert.equal(installed.length, 0, 'native contact does not pop below the aircraft');
    craft.y = 1900;
    const warm = frame();
    assert.equal(pending(warm).length, 4);
    assert.ok(visible(warm).some(tile => tile.key === '0/0/0'));
    acknowledge(terrain, warm, 3);
    const incomplete = frame();
    assert.ok(visible(incomplete).some(tile => tile.key === '0/0/0'));
    assert.equal(installed.length, 0);
    acknowledge(terrain, incomplete);
    const active = frame();
    assert.ok(!visible(active).some(tile => tile.key === '0/0/0'));
    assert.deepEqual(visible(active).filter(tile => tile.level === 1).map(tile => tile.key).sort(),
      ['1/0/0', '1/0/1', '1/1/0', '1/1/1']);
    assert.deepEqual(installed[0].slice(0, 3), [0, 0, 0]);
    assert.equal(installed[0][3].byteLength, 4 * 3283);
    assert.equal(terrain.sample(65536, 65536), 380);
    assert.equal(terrain.isWater(65536, 65536), true);

    x = 490000;
    const retire = frame();
    assert.deepEqual(pending(retire).map(tile => tile.key), ['0/0/0']);
    assert.equal(removed.length, 0, 'coarsening also waits for the GPU parent');
    acknowledge(terrain, retire);
    assert.ok(visible(frame()).some(tile => tile.key === '0/0/0'));
    assert.deepEqual(removed, [[0, 0, 0]]);
    assert.equal(terrain.sample(65536, 65536), 0);
  } finally {
    terrain.dispose();
  }
});

test('Atlas denied detail does not create a hole, duplicate date-line caches, or block clipped overview features', async () => {
  const raw = baseBytes(185);
  const catalog = await catalogFor(raw, hash => [
    pack(hash, 'base', [], [[0, -76, 0]]),
    pack(hash, 'west', [[1, -152, 0, -150, 2]], [[5, -2432, 0]]),
    pack(hash, 'world-osm', [], [[0, -76, 0]]),
  ]);
  const requests = new Map(), coords = [0, -76, 0];
  const row = ['road', 'primary', null, 'natural-earth', [[0, 2], [262144, 8]]];
  const osm = ['rail', 'rail', 'OSM overview', 'osm', [[1, 5], [262144, 11]]];
  const terrain = new WorldAtlasTerrain(catalog, decodeAtlasBase(raw), {
    install: () => assert.fail('A denied terrain quartet must not reach native contacts.'),
  }, { fetch: async url => {
    const path = url.pathname.split(`/${revision}/`)[1];
    requests.set(path, (requests.get(path) ?? 0) + 1);
    if (path === 'features/base/0/-76/0.json') return Response.json({ schemaVersion: 1, tile: coords, rows: [row] });
    if (path === 'features/world-osm/0/-76/0.json') return Response.json({ schemaVersion: 1, tile: coords, rows: [osm] });
    if (path === 'features/west/5/-2432/0.json') return Response.json({
      schemaVersion: 1, tile: [5, -2432, 0],
      rows: [['road', 'primary', null, 'osm', [[0, 1], [8193, 4]]]],
    });
    return new Response('', { status: 404 });
  } });
  try {
    for (let i = 0; i < 8; ++i) {
      const x = i % 2 ? dateLine - 50 : -dateLine + 50;
      const frame = terrain.frame(x, 400, 75000, performance.now());
      assert.equal(terrain.sample(x, 400), 185);
      assert.ok(visible(frame).some(tile => tile.x <= x && x < tile.x + tile.span && tile.z === 0));
      assert.equal(new Set(frame.tiles.map(tile => tile.key)).size, frame.tiles.length);
      acknowledge(terrain, frame);
      await settle();
    }
    const frame = terrain.frame(-dateLine + 50, 400, 75000, performance.now());
    assert.deepEqual(frame.features.map(chunk => [chunk.key, chunk.rows]),
      [['base/0/-76/0', [row]], ['world-osm/0/-76/0', [osm]]]);
    assert.equal(requests.get('tiles/1/-152/0.bin'), 1);
    assert.equal(requests.get('features/base/0/-76/0.json'), 1, 'request key stays canonical across date-line crossings');
    assert.equal(terrain.diagnostics().deniedTerrain, 1);
    assert.equal(terrain.diagnostics().deniedFeatures, 1);
    assert.equal(terrain.diagnostics().detailLevel, -1);
  } finally {
    terrain.dispose();
  }
  assert.deepEqual(terrain.frame(0, 0, 10000, performance.now()), { tiles: [], features: [] });
});

test('Atlas streams optional OSM building heights unchanged and rejects invalid feature chunks atomically', async () => {
  const raw = baseBytes(176);
  const building = ['building', 'residential', null, 'osm', [[20, 20], [50, 20], [50, 45]]];
  const road = ['road', 'primary', 'Legacy road', 'natural-earth', [[0, 0], [50, 50]]];
  const valid = [building, ...[1, 244, 1000].map(height => [...building, height])];
  const invalid = [
    ['below-minimum', 5, [...building, -4]],
    ['above-maximum', 5, [...building, 1001]],
    ['fractional', 5, [...building, 244.5]],
    ['numeric-string', 5, [...building, '244']],
    ['null-height', 5, [...building, null]],
    ['extra-field', 5, [...building, 244, 10]],
    ['wrong-kind', 5, ['landuse', 'park', null, 'osm', building[4], 244]],
    ['wrong-level', 0, [...building, 244]],
    ['wrong-source', 5, ['building', 'residential', null, 'natural-earth', building[4], 244]],
    ['unknown-style', 5, ['building', 'tower', null, 'osm', building[4], 244]],
    ['unknown-legacy-style', 0, ['road', 'tram', ...road.slice(2)]],
  ];
  const layers = [['base', 0, [road]], ['buildings', 5, valid], ...invalid.map(([id, level, row]) =>
    [id, level, [level === 0 ? road : building, row]])];
  const catalog = await catalogFor(raw, hash => layers.map(([id, level]) => pack(hash, id, [], [[level, 0, 0]])));
  const responses = new Map(layers.map(([id, level, rows]) =>
    [`${id}/${level}/0/0.json`, { schemaVersion: 1, tile: [level, 0, 0], rows }]));
  const terrain = new WorldAtlasTerrain(catalog, decodeAtlasBase(raw), {}, { fetch: async url => {
    const selected = responses.get(url.pathname.split('/features/')[1]);
    assert.ok(selected, 'transport only fetches declared feature addresses');
    return Response.json(selected);
  } });
  const frame = () => terrain.frame(20, 20, 10000, performance.now());
  try {
    for (let i = 0; i < 2 * layers.length + 8 && terrain.diagnostics().deniedFeatures < invalid.length; ++i) {
      frame();
      await settle();
    }
    assert.deepEqual(frame().features.map(({ key, rows }) => [key, rows]),
      [['base/0/0/0', [road]], ['buildings/5/0/0', valid]],
      'invalid chunks cannot leak their preceding good row; mixed v1 shapes are preserved');
    assert.equal(terrain.diagnostics().deniedFeatures, invalid.length);
    assert.equal(terrain.sample(20, 20), 176, 'feature validation does not affect fallback elevation');
  } finally {
    terrain.dispose();
  }
});
