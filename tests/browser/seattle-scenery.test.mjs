import assert from 'node:assert/strict';
import test from 'node:test';
import { decodeTile, readWorldCatalog, WorldTerrain } from '../../src/StuntRacer.Browser/wwwroot/world-terrain.js';
import { WorldFlightScene, WorldScenery } from '../../src/StuntRacer.Browser/wwwroot/world-flight-scene.js';

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

function surfaceAt(rows, x, z, material) {
  const cross = (a, b, x, z) => (b[0] - a[0]) * (z - a[2]) - (b[2] - a[2]) * (x - a[0]);
  for (let i = 0; i < rows.length; i += 3) {
    const [a, b, c] = rows.slice(i, i + 3);
    if (a[9] !== material) continue;
    const area = cross(a, b, c[0], c[2]);
    const weights = [cross(b, c, x, z), cross(c, a, x, z), cross(a, b, x, z)].map(n => n / area);
    if (weights.every(n => n >= -1e-8))
      return { y: weights[0] * a[1] + weights[1] * b[1] + weights[2] * c[1], colour: a.slice(6, 9) };
  }
  return null;
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

test('Seattle street meshes pave exact junctions and fit dry paint, curbs and walkable tops to the rendered facets', async t => {
  const streets = [
    [1, 'local', 'surface', [[12, 144], [72, 144], [110, 144], [118, 144], [124, 144], [184, 144], [244, 144], [308, 144]]],
    [2, 'arterial', 'surface', [[118, 35], [118, 138], [118, 144], [118, 150], [118, 224]]],
    [3, 'freeway', 'bridge', [[220, 75, 46], [220, 195, 47]]],
    [4, 'local', 'surface', [[36, 220], [76, 220], [110, 237]]],
    [5, 'local', 'surface', [[12, 282], [125, 282]]],
    [6, 'local', 'surface', [[12, 308], [125, 308]]],
  ];
  t.mock.method(globalThis, 'fetch', async () => Response.json({ ...roads, roads: streets }));
  const { terrain, graphics } = worldTerrain({ id: 'seattle', featureCount: streets.length,
    pointCount: streets.reduce((sum, row) => sum + row[3].length, 0), bounds: [12, 35, 308, 308] });
  const scenery = new WorldScenery(graphics, terrain), tile = fixture(8, 0, 0, (i, j) => i + j * 2 + (i === 2 ? 3 : 0),
    (_i, j) => j >= 9 ? 2 : 0);
  tile.mesh = {};
  setVisible(terrain, tile);
  const decorate = options => scenery.decorate({
    draws: [], camera: { eye: [42, 23, 140], target: [62, 18, 146] },
  }, 42, 144, options);
  const roadMesh = frame => frame.draws.find(({ mesh }) => mesh !== tile.mesh)?.mesh;
  const document = await terrain.loadRoads(), aerial = roadMesh(decorate());
  assert.deepEqual(document.roads, streets, 'render styling cannot replace or mutate the drive document');
  scenery.setMapRoads(true);
  let actorHook;
  const options = { view: 'ground', appendGroundActors: frame => {
    actorHook = terrain.visibleSidewalksAt(tile);
    assert.deepEqual(frame.camera.eye, [42, 23, 140]);
  } };
  let ground;
  for (let i = 0; i < 30 && !actorHook; i++) ground = decorate(options);
  const street = roadMesh(ground), rows = vertices(street);
  assert.notEqual(street, aerial, 'the ground view replaces the colored flight ribbon, not just its height');
  assert.equal(ground.camera.mapRoads, false);
  assert.equal(scenery.diagnostics().roadStyle, 'street');
  assert.equal(scenery.diagnostics().roadMeshes, 1, 'all street material layers share the one tile draw');
  assert.ok(actorHook && actorHook === terrain.visibleSidewalksAt(tile));
  assert.ok(!graphics.meshes.has(aerial), 'the old view does not retain a second mesh');
  assert.deepEqual(rows.filter(p => p[9] === 9), vertices(aerial).filter(p => p[9] === 9), 'the bridge deck mesh is unchanged');
  assert.ok(rows.every(p => p[9] === 9 || p[9] >= 12));
  const on = (x, z, material) => surfaceAt(rows, x, z, material);
  const gap = (x, z, material) => on(x, z, material)?.y - terrain.visualSample(x, z);
  assert.ok(on(42.5, 149.9, 12).colour.every(c => c < .15), 'wider dark asphalt reaches the edge of the drive corridor');
  assert.ok(Math.abs(gap(42.5, 149.9, 12) - .075) < 1e-8);
  assert.ok(Math.abs(gap(42.5, 144, 13) - .10) < 1e-8, 'a visible painted dash rides just above the asphalt');
  assert.equal(on(48, 144, 13), null, 'the local centerline has gaps between its short yellow dashes');
  assert.ok(on(126.6, 75, 13).colour[2] > .7 && on(118.18, 75, 13).colour[2] < .2,
    'arterial edges are white, while the paired center stripes are yellow');
  assert.ok(Math.abs(gap(42.5, 150.39, 15) - .1575) < 1e-8, 'the raised curb bevel links road and sidewalk');
  assert.ok(Math.abs(gap(42.5, 151.065, 14) - .24) < 1e-8);
  assert.ok(terrain.hasVisibleSidewalk(42.5, 151.065) && terrain.hasVisibleSidewalk(127.865, 75));
  assert.ok(!terrain.hasVisibleSidewalk(42.5, 149.9), 'drive-corridor pavement is not a sidewalk');
  assert.ok(terrain.hasVisibleSidewalk(74.29, 227.065) && terrain.hasVisibleSidewalk(74.39, 227.065),
    'the sidewalks join across a safe bend rather than ending at each source point');

  for (const [x, z] of [[118, 144], [116, 150], [124, 144], [128.5, 144]]) {
    assert.ok(on(x, z, 12), 'the entire junction and its mouths stay paved');
    assert.ok(!on(x, z, 13) && !on(x, z, 14) && !on(x, z, 15), 'corner paving does not carry sidewalk or paint through an intersection');
  }
  assert.ok(!terrain.hasVisibleSidewalk(128.5, 151.065), 'the trim continues across a short source segment');
  assert.ok(terrain.hasVisibleSidewalk(132, 151.065), 'the sidewalk resumes beyond the intersecting street');
  assert.ok(on(222.5, 144, 12) && !on(222.5, 144, 13) && !terrain.hasVisibleSidewalk(220, 151.065),
    'crossing mapped elevated geometry keeps the surface road but not the finish');
  assert.ok(on(62.5, 282, 12) && !on(62.5, 282, 13), 'even a mixed shoreline facet omits paint');
  assert.ok(!on(62.5, 308, 12) && !terrain.hasVisibleSidewalk(62.5, 315.065), 'wet faces do not get paving or sidewalks');
  assert.equal(terrain.sample(42.5, 151.065), terrain.visualSample(42.5, 151.065), 'the .24 m step is visual only');

  scenery.dispose();
  assert.equal(terrain.visibleSidewalksAt(tile), null);
  assert.ok(!terrain.hasVisibleSidewalk(42.5, 151.065) && graphics.meshes.size === 0);
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
