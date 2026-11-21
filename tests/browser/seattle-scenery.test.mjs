import assert from 'node:assert/strict';
import test from 'node:test';
import { decodeTile, readWorldCatalog, WorldTerrain } from '../../src/StuntRacer.Browser/wwwroot/world-terrain.js';
import { WorldFlightScene } from '../../src/StuntRacer.Browser/wwwroot/world-flight-scene.js';

const roads = {
  license: 'ODbL-1.0', attribution: '© OpenStreetMap contributors', licenseUrl: 'https://www.openstreetmap.org/copyright',
  roads: [
    [1, 'arterial', 'surface', [[8, 20], [88, 106]]],
    [2, 'local', 'surface', [[144, 200], [228, 200]]],
    [3, 'local', 'surface', [[8, 10], [80, 10]]],
    [4, 'freeway', 'bridge', [[145, 220, 40], [232, 220, 50]]],
  ],
};
const roadArea = { id: 'seattle', featureCount: 4, pointCount: 8, bounds: [8, 10, 232, 220] };
const destination = { id: 'seattle', name: 'Seattle', x: 0, z: 0, altitude: 475, heading: 0, targetX: 0, targetZ: 0 };
const coverage = Array.from({ length: 9 }, () => [0, 0, 1, 1]);
const nextTurn = () => new Promise(resolve => setImmediate(resolve));

function fixture(level, x, z, height, wet = () => 0) {
  const buffer = new ArrayBuffer(16 + 33 * 33 * 3);
  const view = new DataView(buffer);
  view.setUint32(0, 0x57465431, false);
  view.setInt32(4, level, true);
  view.setInt32(8, x, true);
  view.setInt32(12, z, true);
  for (let j = 0; j < 33; j++) for (let i = 0; i < 33; i++) {
    const n = j * 33 + i;
    view.setInt16(16 + 2 * n, height(i, j), true);
    view.setUint8(16 + 33 * 33 * 2 + n, wet(i, j));
  }
  return decodeTile(buffer, level, x, z);
}
function worldTerrain(area) {
  const region = { id: 'conus', coverage, landmarks: [], cities: [], ...(area && { roads: area }) };
  const terrain = new WorldTerrain({ tilesUrl: new URL('http://game.example/world-flight/0123456789abcdef0123/') },
    region, { add() {}, remove() {} });
  const graphics = {
    meshes: new Set(),
    mesh(mesh) { this.meshes.add(mesh); return mesh; },
    releaseMesh(mesh) { assert.ok(this.meshes.delete(mesh), 'released an owned, live mesh'); },
  };
  terrain.attach(graphics);
  terrain.nextUpdate = Infinity;
  terrain.meshQueue = [];
  return { terrain, graphics };
}
function setVisible(terrain, ...tiles) {
  terrain.visible = tiles;
  terrain.rendered = new Map(tiles.map(tile => [`${tile.level}/${tile.x}/${tile.z}`, tile]));
  for (const [key, tile] of terrain.rendered) terrain.loaded.set(key, tile);
}
function vertices(mesh) {
  return Array.from({ length: mesh.vertices.length / 10 }, (_, i) => mesh.vertices.slice(i * 10, (i + 1) * 10));
}

test('Seattle ribbons are grouped on visible terrain facets, skip ordinary water and survive LOD replacement', async t => {
  const urls = [];
  t.mock.method(globalThis, 'fetch', async url => {
    urls.push(url.href);
    return Response.json(roads);
  });
  const { terrain, graphics } = worldTerrain(roadArea);
  const coarse = fixture(7, 0, 0, (i, j) => i === 1 && j === 1 ? 34 : i + j * 2, (_i, j) => j >= 3 ? 2 : 0);
  coarse.mesh = {};
  const fine = fixture(8, 0, 0, () => 63);
  fine.mesh = {};
  terrain.loaded.set('8/0/0', fine);
  setVisible(terrain, coarse);

  assert.equal(terrain.sample(20, 20), 63, 'the finer collision data is already cached');
  assert.notEqual(terrain.visualSample(20, 20), 63, 'the road must align to the displayed parent, not the cache');
  assert.equal(terrain.draw(5, 5, 140000, false, 1).length, 1);
  await nextTurn();
  const [base, road] = terrain.draw(5, 5, 140000, false, 2);
  assert.equal(base.mesh, coarse.mesh);
  assert.deepEqual(road.offset, base.offset);
  assert.equal(road.shadow, false);
  assert.deepEqual(urls, ['http://game.example/world-flight/0123456789abcdef0123/seattle-roads.json']);
  const mesh = vertices(road.mesh);
  const dry = mesh.filter(p => p[2] < 130);
  const bridge = mesh.filter(p => p[2] > 208);
  assert.ok(dry.length > 12 && bridge.length > 6, 'both local/arterial slopes and the elevated span have triangles');
  assert.equal(new Set(mesh.map(p => p.slice(6, 9).join(','))).size, 3, 'three road classes use distinct readable colors');
  assert.ok(dry.every(([x, y, z, , ny]) => {
    const gap = y - terrain.visualSample(x, z);
    return gap > .7 && gap < 1.1 && ny > 0;
  }), 'both lateral edges and clipped corners follow the visible piecewise terrain');
  assert.ok(dry.some(([x, , z]) => Math.abs(x - z) < 1e-8), 'the ribbon is actually split on terrain triangle diagonals');
  assert.ok(dry.some(([, y]) => y > 24), 'the arterial follows a real interior ridge, not just its end heights');
  assert.ok(bridge.every(([, y, z]) => y > 40 && y < 51 && z > 208), 'a mapped span keeps its separate sloping deck over water');
  assert.ok(!mesh.some(([, , z]) => z > 185 && z < 207), 'the non-bridge local street never paints the lake');

  setVisible(terrain, fine);
  const refined = terrain.draw(5, 5, 140000, false, 3)[1];
  assert.ok(vertices(refined.mesh).every(([, y]) => y > 63 && y < 65));
  assert.ok(!graphics.meshes.has(road.mesh) && graphics.meshes.size === 1, 'only the rendered LOD retains its road mesh');
  assert.equal(terrain.draw(24_000, 24_000, 140000, false, 4).length, 1);
  assert.equal(graphics.meshes.size, 0, 'leaving Seattle drops the road meshes');
  terrain.dispose();
});

test('old catalogs still run without roads; a missing or malformed declared layer is explicit', async t => {
  const data = {
    kind: 'world-flight-terrain', schemaVersion: 1, side: 33, version: '0123456789abcdef0123',
    spacing: Array.from({ length: 9 }, (_, i) => 8192 >> i),
    regions: ['conus', 'alaska', 'hawaii'].map(id => ({
      id, coverage, destinations: [destination],
    })),
  };
  let response = () => Response.json(data);
  const fetch = t.mock.method(globalThis, 'fetch', async () => response());
  const old = await readWorldCatalog('./world-flight/manifest.json');
  const legacy = new WorldTerrain(old, old.regions[0], {});
  legacy.nextUpdate = Infinity;
  legacy.meshQueue = [];
  assert.equal(legacy.draw(5, 5, 140000, false, 1).length, 0);
  assert.equal(legacy.status, '');
  assert.equal(fetch.mock.callCount(), 1, 'absence of the layer does not request the fixed URL');

  data.regions[2].roads = roadArea;
  await assert.rejects(readWorldCatalog('./world-flight/manifest.json'), /invalid Seattle road metadata/);
  delete data.regions[2].roads;
  data.regions[0].roads = roadArea;
  const upgraded = await readWorldCatalog('./world-flight/manifest.json');
  for (const [result, description] of [
    [new Response(null, { status: 404 }), /Seattle roads: World Flight data returned HTTP 404/],
    [Response.json({ ...roads, roads: roads.roads.map((row, i) => i ? row : [1, 'path', 'surface', row[3]]) }),
      /Seattle roads: Invalid road class/],
  ]) {
    response = () => result;
    const { terrain, graphics } = worldTerrain(upgraded.regions[0].roads);
    terrain.draw(5, 5, 140000, false, 1);
    await nextTurn();
    assert.match(terrain.status, description);
    assert.equal(graphics.meshes.size, 0);
    terrain.dispose();
  }
  const { terrain } = worldTerrain(upgraded.regions[0].roads);
  fetch.mock.mockImplementation((_url, { signal }) => new Promise((_resolve, reject) =>
    signal.addEventListener('abort', () => reject(signal.reason), { once: true })));
  terrain.draw(5, 5, 140000, false, 1);
  const signal = fetch.mock.calls.at(-1).arguments[1].signal;
  terrain.dispose();
  assert.ok(signal.aborted, 'a scene switch cancels the optional dataset request');
  await nextTurn();
  assert.equal(terrain.status, '');
});

test('Seattle scene draws a diamond beside the original football stadium and three glazed domes', () => {
  const { terrain, graphics } = worldTerrain();
  const ballpark = { kind: 'ballpark', name: 'T-Mobile Park', x: 213, z: 6991, heading: 1.09 };
  const stadium = { kind: 'stadium', name: 'Lumen Field', x: 393, z: 7375 };
  const spheres = { kind: 'spheres', name: 'Amazon Spheres', x: 440, z: 9709, heading: 1.02 };
  terrain.region.landmarks = [ballpark, stadium, spheres];
  const world = { name: 'Seattle', seaHeight: 0, islands: [], landingSurfaces: [],
    runwayStart: { x: 0, z: 0 }, geography: { destination } };
  const scene = new WorldFlightScene(graphics, world, terrain);
  const state = { phase: 'racing', flight: { aircraft: {
    x: 0, y: 475, z: 8000, yaw: 0, pitch: 0, roll: 0, speed: 90, gearDown: true,
    forward: { x: 0, y: 0, z: 1 }, up: { x: 0, y: 1, z: 0 },
  } } };
  assert.equal(scene.update(state, .016, 'chase').draws.some(draw => draw.mesh === scene.landmarks[0].mesh), false,
    'the ballpark does not float before Seattle detail terrain is visible');
  const tiles = [[6, 11], [7, 7], [9, 33]].map(([z, height]) => {
    const tile = fixture(8, 0, z, () => height);
    tile.mesh = {};
    return tile;
  });
  setVisible(terrain, ...tiles);
  const { draws, camera } = scene.update(state, .016, 'chase');
  const features = scene.landmarks.map(({ mesh }) => draws.find(draw => draw.mesh === mesh));
  assert.deepEqual(features.map(({ model }) => model[13]), [10, 6, 32]);
  assert.ok(camera.logarithmicDepth && features.every(draw => draw.shadow === false));
  assert.ok(Math.abs(features[0].model[8] - Math.sin(ballpark.heading)) < 1e-6,
    'home plate-to-center axis is oriented in world coordinates');
  const diamond = vertices(features[0].mesh);
  const bases = diamond.filter(([, y, , , ny]) => Math.abs(y - 5.23) < 1e-8 && ny > .99);
  for (const [x, z] of [[0, -50], [19.4, -30.6], [0, -11.2], [-19.4, -30.6]])
    assert.ok(bases.some(p => Math.hypot(p[0] - x, p[2] - z) < 1.7), 'the open baseball infield has four real bases');
  assert.ok(diamond.some(([, y, z]) => y >= 48 && z > 80), 'parked roof sits beyond the outfield');
  const glass = vertices(features[2].mesh).filter(p => p[1] > 15 && p[6] < .31 && p[8] > .50);
  for (const [z, height] of [[-23, 23], [0, 28.5], [23, 21.7]])
    assert.ok(glass.some(p => Math.abs(p[2] - z) < .1 && Math.abs(p[1] - height) < .1),
      'each separate terrarium has its own glazed apex and scale');
  assert.deepEqual(scene.nearbyLandmarks, [ballpark.name, stadium.name, spheres.name]);
  scene.dispose();
  assert.ok(features.every(draw => !graphics.meshes.has(draw.mesh)));
});
