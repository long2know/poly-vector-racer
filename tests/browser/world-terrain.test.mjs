import assert from 'node:assert/strict';
import test from 'node:test';
import { decodeTile, makeElevationMesh, makeElevationSeam, terrainTileSpan, WorldTerrain } from '../../src/StuntRacer.Browser/wwwroot/world-terrain.js';
import { WorldScenery } from '../../src/StuntRacer.Browser/wwwroot/world-flight-scene.js';

function fixture(level, x, z, height = (i, j) => 7 + i + 2 * j, wet = 0) {
  const buffer = new ArrayBuffer(16 + 33 * 33 * 3);
  const view = new DataView(buffer);
  view.setUint32(0, 0x57465431, false);
  view.setInt32(4, level, true);
  view.setInt32(8, x, true);
  view.setInt32(12, z, true);
  for (let j = 0; j < 33; j++) for (let i = 0; i < 33; i++) {
    const n = j * 33 + i;
    view.setInt16(16 + 2 * n, height(i, j), true);
    view.setUint8(16 + 33 * 33 * 2 + n, typeof wet === 'function' ? wet(i, j) : wet);
  }
  return decodeTile(buffer, level, x, z);
}

test('streamed terrain keeps real signed heights and uses the same triangle diagonal as C# contacts', () => {
  const tile = fixture(8, -1, 0, (i, j) => j === 0 && i === 1 ? -5 : 100 + i * 4 + j * 10);
  const mesh = makeElevationMesh(tile);
  assert.deepEqual([0, 10, 20, 30, 40, 50].map(i => mesh.vertices.slice(i, i + 3)),
    [[0, 100, 0], [0, 110, 32], [32, 114, 32], [0, 100, 0], [32, 114, 32], [32, -5, 0]]);
  assert.equal(mesh.vertices.length, 32 * 32 * 6 * 10);
  assert.throws(() => decodeTile(tile.buffer, 8, 0, 0), /coordinates/);
  assert.throws(() => fixture(8, -1, 0, () => 1, 3), /terrain samples/);
  const lake = makeElevationMesh(fixture(8, 0, 0, () => 5, 2));
  assert.equal(lake.vertices[1], 5);
  assert.equal(lake.vertices[9], 5, 'water receives its own shader rather than grass');
});

test('a parent stays visible until four drawable children cover its full area', () => {
  const terrain = new WorldTerrain({}, { coverage: [[0, 0, 1, 1], [0, 0, 2, 2]] },
    { add() {}, remove() {} });
  const graphics = { mesh: () => ({}), releaseMesh() {} };
  terrain.attach(graphics);
  for (const [level, x, z] of [[0, 0, 0], [1, 0, 0], [1, 0, 1], [1, 1, 0], [1, 1, 1]]) {
    const tile = fixture(level, x, z);
    if (level === 0 || x === 0) tile.mesh = {};
    terrain.loaded.set(`${level}/${x}/${z}`, tile);
  }
  const first = terrain.draw(200, 200, 180000, false, 1);
  assert.deepEqual(first.map(draw => draw.offset), [[0, 0, 0]]);
  assert.equal(terrain.visible[0].level, 0);
  const refined = terrain.draw(200, 200, 180000, false, 200);
  const span = terrainTileSpan(1);
  assert.deepEqual(refined.map(draw => draw.offset), [[0, 0, 0], [span, 0, 0], [0, 0, span], [span, 0, span]]);
  assert.ok(terrain.visible.every(tile => tile.level === 1));
});

test('a level-of-detail join ends at the actual adjacent mesh, even when it is higher', () => {
  const coarse = fixture(7, -1, 0, (_i, j) => 125 + 2 * j);
  const fine = fixture(8, 0, 0, () => 110);
  const seam = makeElevationSeam(fine, 0, coarse);
  assert.deepEqual([0, 10, 20, 50].map(i => seam.vertices.slice(i, i + 3)),
    [[0, 110, 0], [0, 125, 0], [0, 126, 32], [0, 110, 32]]);
  const flat = fixture(8, 0, 0, (_i, j) => 125 + j);
  assert.equal(makeElevationSeam(flat, 0, coarse).vertices.length, 0, 'matching seams do not add overlapping faces');
});

const fineCoverage = Array.from({ length: 9 }, () => [-4, -4, 4, 4]);
const bridge = { add() {}, remove() {} };
const graphicsFixture = () => ({
  meshes: new Set(),
  mesh(builder) {
    const mesh = { vertices: builder.vertices, count: builder.vertices.length / 10 };
    this.meshes.add(mesh);
    return mesh;
  },
  releaseMesh(mesh) { this.meshes.delete(mesh); },
});

function visible(terrain, ...tiles) {
  terrain.visible = tiles;
  terrain.rendered = new Map(tiles.map(tile => {
    tile.mesh ??= terrain.graphics.mesh(makeElevationMesh(tile));
    const key = `${tile.level}/${tile.x}/${tile.z}`;
    terrain.loaded.set(key, tile);
    return [key, tile];
  }));
  terrain.meshQueue = [];
  terrain.nextUpdate = Infinity;
}

test('forest draws sit on dry, shallow detail facets, not water, runway, snow, steep hills or downtown', () => {
  const runway = { centerX: 120, centerZ: 140, heading: -.62, length: 510, width: 90 };
  const region = { coverage: fineCoverage, runway, cities: [{ x: -3400, z: 0 }] };
  const terrain = new WorldTerrain({}, region, bridge), graphics = graphicsFixture();
  terrain.attach(graphics);
  const dry = fixture(8, 0, 0, (i, j) => 35 + i * 2 + j * 3, (i, j) => i > 24 && j < 11 ? 2 : 0);
  const lake = fixture(8, 1, 0, () => 83, 2), snow = fixture(8, 0, 1, () => 2410);
  const cliff = fixture(8, 1, 1, (i, j) => 45 + i * 31 + j * 18);
  const downtown = fixture(8, -2, -1, () => 62);
  visible(terrain, dry, lake, snow, cliff, downtown);
  let draws;
  for (let i = 0; i < 50; i++) draws = terrain.draw(600, 440, 150000, false, i);
  const trees = draws.filter(draw => !terrain.visible.some(tile => draw.mesh === tile.mesh));
  assert.equal(trees.length, 1, 'positive elevation alone is not enough to plant on any other loaded tile');
  const mesh = trees[0].mesh;
  assert.ok(mesh.count > 330 && terrain.forest.count > 10, 'the dry lowland produces an actual grove draw');
  const v = mesh.vertices;
  for (let i = 0; i < v.length; i += 330) {
    const [x, y, z] = v.slice(i + 10, i + 13);
    const [crownY] = v.slice(i + 221, i + 222);
    const floor = terrain.visualSample(x, z);
    assert.ok(Math.abs((y + .35 - floor) / (crownY - floor) - .55) < 1e-10, 'trunk roots reach the visible triangles');
    assert.ok(!terrain.isWater(x, z));
    assert.ok(Math.abs((x - runway.centerX) * Math.cos(runway.heading) - (z - runway.centerZ) * Math.sin(runway.heading)) >
      runway.width / 2 || Math.abs((x - runway.centerX) * Math.sin(runway.heading) + (z - runway.centerZ) * Math.cos(runway.heading)) >
      runway.length / 2, 'even a rotated runway stays clear');
  }
  terrain.dispose();
  assert.equal(graphics.meshes.size, 0, 'leaving the scene frees both terrain and forest buffers');
});

test('upgrading a visible tile reuses its forest positions, re-roots them and releases the previous batch', () => {
  const terrain = new WorldTerrain({}, { coverage: fineCoverage }, bridge), graphics = graphicsFixture();
  terrain.attach(graphics);
  const coarse = fixture(7, 0, 0, () => 100), fine = fixture(8, 0, 0, () => 109);
  const getPatch = () => {
    let draws;
    for (let i = 0; i < 60; i++) draws = terrain.draw(400, 440, 150000, false, i);
    return draws.find(draw => draw.offset.every(value => value === 0) && draw.mesh !== terrain.visible[0].mesh)?.mesh;
  };
  const roots = mesh => Array.from({ length: mesh.vertices.length / 330 }, (_, i) => mesh.vertices.slice(i * 330 + 10, i * 330 + 13));
  visible(terrain, coarse);
  const previous = getPatch();
  const before = roots(previous);
  assert.ok(before.length > 10);
  visible(terrain, fine);
  const after = roots(getPatch());
  const plan = rows => rows.map(([x, , z]) => [x, z]);
  assert.deepEqual(plan(after), plan(before));
  assert.ok(after.every((p, i) => Math.abs(p[1] - before[i][1] - 9) < 1e-8), 'the visible surface determines tree height');
  assert.equal(graphics.meshes.has(previous), false, 'a parent and child must never draw the same forest together');
  terrain.dispose();
  terrain.attach(graphics);
  visible(terrain, fine);
  assert.deepEqual(roots(getPatch()), after, 're-attaching graphics regenerates the same batches without stale handles');
  terrain.dispose();
});

test('road ribbons share one validated download, preserve coverage and switch palette without new meshes', async t => {
  const sampleRoads = [
    [1, 'freeway', 'surface', [[110, 150], [225, 150]]],
    [2, 'arterial', 'surface', [[110, 220], [225, 220]]],
    [3, 'local', 'surface', [[110, 290], [225, 290]]],
    [4, 'freeway', 'bridge', [[602, 150, 69], [720, 150, 69]]],
    [5, 'local', 'surface', [[602, 290], [720, 290]]],
  ];
  const source = { license: 'ODbL-1.0', attribution: '© OpenStreetMap contributors',
    licenseUrl: 'https://www.openstreetmap.org/copyright', roads: sampleRoads };
  const fetchRoad = t.mock.method(globalThis, 'fetch', async url => {
    assert.equal(url.pathname, '/fixture/seattle-roads.json');
    return new Response(JSON.stringify(source));
  });
  const roadArea = { id: 'seattle', featureCount: 5, pointCount: 10, bounds: [0, 0, 1024, 1024] };
  const catalog = { tilesUrl: new URL('http://localhost/fixture/') };
  const region = { id: 'conus', coverage: fineCoverage, roads: roadArea };
  const terrain = new WorldTerrain(catalog, region, bridge), graphics = graphicsFixture();
  const scenery = new WorldScenery(graphics, terrain), x = 340, z = 230;
  visible(terrain, fixture(8, 0, 0, () => 25, i => i >= 16 ? 2 : 0));
  const draw = options => scenery.decorate({ draws: [], camera: { eye: [x, 35, z], target: [x, 30, z + 10] } }, x, z, options);
  const [document, same] = await Promise.all([terrain.loadRoads(), terrain.loadRoads()]);
  assert.equal(document, same);
  assert.deepEqual(document, source);
  let frame = draw();
  const road = frame.draws.find(({ mesh }) => mesh.vertices[9] === 6)?.mesh;
  assert.ok(road?.count > 40);
  const groups = new Map();
  for (let i = 0; i < road.vertices.length; i += 10) {
    const v = road.vertices.slice(i, i + 10), material = v[9];
    if (!groups.has(material)) groups.set(material, []);
    groups.get(material).push(v);
  }
  assert.deepEqual([...groups.keys()], [6, 7, 8, 9], 'three surface classes and actual bridges are available; wet surface roads are omitted');
  assert.deepEqual([...groups.values()].map(rows => Math.max(...rows.map(p => p[2])) - Math.min(...rows.map(p => p[2])))
    .map(width => Math.round(width * 10) / 10), [20.8, 13.4, 8.3, 20.8]);
  assert.ok(groups.get(9).every(p => p[1] >= 69.3 && p[0] > 512), 'the bridge keeps the authored deck over water');
  assert.equal(scenery.diagnostics().roadStyle, 'terrain');
  scenery.setMapRoads(true);
  frame = draw();
  assert.equal(frame.camera.mapRoads, true);
  assert.equal(scenery.diagnostics().roadStyle, 'map');
  assert.equal(scenery.diagnostics().roadMeshes, 1);
  assert.ok(frame.draws.some(draw => draw.mesh === road), 'aerial palette toggles reuse the identical GPU mesh');
  assert.equal(terrain.sample(x, z), 25, 'palette switches do not alter collision height');
  scenery.setMapRoads(false);
  assert.equal(draw().camera.mapRoads, false);
  assert.equal(fetchRoad.mock.callCount(), 1);
  scenery.dispose();
  assert.equal(graphics.meshes.size, 0);

  const oldPackage = new WorldTerrain(catalog, { ...region, roads: undefined }, bridge);
  const oldScene = new WorldScenery(graphics, oldPackage);
  visible(oldPackage, fixture(8, 0, 0, () => 25));
  assert.equal(await oldPackage.loadRoads(), null);
  oldScene.setMapRoads(true);
  const old = oldScene.decorate({ draws: [], camera: { eye: [x, 35, z], target: [x, 30, z + 10] } }, x, z);
  assert.ok(old.draws.some(draw => draw.mesh === oldPackage.visible[0].mesh));
  assert.equal(oldScene.diagnostics().roadStyle, 'terrain');
  assert.equal(oldScene.diagnostics().roadMeshes, 0);
  assert.equal(fetchRoad.mock.callCount(), 1, 'older packages do not attempt to load the optional layer');
  oldScene.dispose();
});
