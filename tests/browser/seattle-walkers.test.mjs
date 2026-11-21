import assert from 'node:assert/strict';
import test from 'node:test';
import { transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';
import { loadSeattleDrive, WorldDriveScene } from '../../src/StuntRacer.Browser/wwwroot/world-drive-scene.js';
import { SeattleWalkers } from '../../src/StuntRacer.Browser/wwwroot/world-drive-walkers.js';
import { WorldFlightScene } from '../../src/StuntRacer.Browser/wwwroot/world-flight-scene.js';

const z = 8352;
const bounds = { minX: 0, minZ: 8240, maxX: 280, maxZ: 8460 };
const segment = [35, z, 121, z];
const world = { name: 'Seattle', bounds, streets: [segment] };
const roads = [[9, 'arterial', 'surface', [[segment[0], segment[1]], [segment[2], segment[3]]]]];

function graphics() {
  return {
    canvas: { width: 1440, height: 900 }, meshes: new Set(),
    mesh(builder) { this.meshes.add(builder); return builder; },
    releaseMesh(mesh) { assert.ok(this.meshes.delete(mesh), 'release a live owned mesh exactly once'); },
  };
}

function state(time = 0, phase = 'racing', player = {}) {
  return {
    time, tick: time * 120, phase, drive: {}, impact: 0,
    player: Object.freeze({ x: 24, y: 13, z, yaw: Math.PI / 2, speed: 0, steer: 0, ...player }),
  };
}

const ground = (x, z) => 12 + x / 32 + (z - 8192) / 32;
const cameraFrame = () => ({
  draws: [], camera: { eye: [14, 17, z], target: [140, 13, z], up: [0, 1, 0], fov: 1.15 },
});
const onArterial = (x, pz) => x >= 35 && x <= 121 && Math.abs(pz - z) >= 9.18 && Math.abs(pz - z) <= 10.55;

function fixture(source = roads) {
  const renderer = graphics(), tile = { level: 8, x: 0, z: 8, water: new Uint8Array(33 * 33),
    heights: Int16Array.from({ length: 33 * 33 }, (_, i) => 12 + i % 33 + Math.floor(i / 33)) };
  const terrain = { region: {}, support: {}, visualTileAt: () => tile, visualSample: ground,
    hasVisibleSidewalk: onArterial, visibleSidewalksAt() { return this.support; } };
  const walkers = new SeattleWalkers(renderer, world, terrain, source);
  const display = (snapshot, scenery = {}) => {
    const frame = cameraFrame();
    walkers.append(frame, snapshot, scenery);
    return { ...walkers.diagnostics(), draws: frame.draws };
  };
  return { renderer, tile, terrain, walkers, display };
}

test('Seattle poses put the actual animated feet on the rendered sidewalk and only use native time', () => {
  const { display, renderer, walkers } = fixture();
  const ready = display(state(0, 'ready'));
  const walking = display(state(1));
  assert.ok(ready.visible >= 4);
  assert.equal(ready.draws.length, ready.visible * 5, 'diagnosed actors have an actual body and four limbs');
  assert.ok(walking.poses.some(p => {
    const start = ready.poses.find(first => first.id === p.id);
    return start && Math.hypot(p.x - start.x, p.z - start.z) > .3 && Math.abs(p.phase - start.phase) > .2;
  }), 'native elapsed time moves bodies and changes the gait');
  for (const [i, pose] of walking.poses.entries()) {
    assert.ok(onArterial(pose.x, pose.z) && Number.isFinite(pose.yaw));
    assert.equal(pose.y, ground(pose.x, pose.z) + .24);
    const limb = walking.draws[5 * i + 1];
    const floor = Math.min(...Array.from({ length: limb.mesh.vertices.length / 10 }, (_, n) =>
      transform(limb.model, limb.mesh.vertices.slice(10 * n, 10 * n + 3))[1]));
    assert.ok(Math.abs(floor - pose.y) < 1e-5, 'the real limb vertices meet the sidewalk height');
  }
  const frozen = display(state(1, 'paused'));
  assert.deepEqual(frozen, walking);
  assert.deepEqual(display(state(1, 'paused')), frozen);
  assert.deepEqual(display(state(0, 'ready')), ready, 'garage returns to the same placement and gait');
  assert.ok(renderer.meshes.size >= 4 && renderer.meshes.size <= 6, 'visible actors share a few distinct rigs');
  walkers.dispose();
  assert.equal(renderer.meshes.size, 0);
});

test('empty, junction, wet and obstructed sidewalk spans do not produce poses', () => {
  const { tile, terrain, display } = fixture();
  terrain.support = null;
  assert.equal(display(state()).visible, 0, 'cached terrain without its visible sidewalk mesh is not support');
  terrain.support = {};
  const baseline = display(state());
  assert.ok(baseline.poses.some(p => p.x > 55 && p.x < 101));
  terrain.hasVisibleSidewalk = (x, z) => onArterial(x, z) && !(x >= 55 && x <= 101);
  terrain.support = {};
  const trimmed = display(state(1));
  assert.ok(trimmed.visible > 0 && trimmed.poses.every(p => p.x < 55 || p.x > 101),
    'a rendered intersection gap clips the whole walking span');
  const crossing = fixture([...roads, [10, 'local', 'surface', [[65, z - 22], [65, z + 22]]]]);
  assert.ok(baseline.poses.some(p => Math.abs(p.x - 65) < 6.3));
  const guarded = crossing.display(state());
  assert.ok(guarded.visible > 0 && guarded.poses.every(p => Math.abs(p.x - 65) > 6.3),
    'a source road crossing an otherwise drawn sidewalk cannot put walkers in the road');
  terrain.hasVisibleSidewalk = onArterial;
  terrain.support = {};
  for (let i = 0; i < 33; i++) tile.water[4 * 33 + i] = 2;
  const dry = display(state());
  assert.ok(dry.visible > 0 && dry.poses.every(p => p.z > z), 'a mixed shoreline cell is excluded');
  const building = [1, 18, 'levels', [[35, z + 8], [121, z + 8], [121, z + 12], [35, z + 12]]];
  assert.equal(display(state(), { cityMesh: {}, buildingData: [building] }).visible, 0,
    'a mapped footprint covering the other sidewalk cannot produce a walker');
  tile.water.fill(0);
  tile.heights.set(tile.heights.map(y => y * 20));
  terrain.support = {};
  assert.equal(display(state()).visible, 0, 'steep visible terrain is not used as a walking path');
});

test('walkers step toward the outside of their own band and leave a minimum visual clearance', () => {
  const { display } = fixture();
  const walker = display(state(.45)).poses.find(p => p.z > z);
  assert.ok(walker);
  const approaching = state(.45, 'racing', { x: walker.x - 4.1, z: walker.z - 1.1 });
  const yielding = display(approaching).poses.find(p => p.id === walker.id);
  assert.ok(yielding?.yielding > .75 && yielding.z - walker.z > .18 && onArterial(yielding.x, yielding.z),
    'stepping aside moves farther out but still stands on the same visible sidewalk');
  assert.ok(yielding.opacity > 0 && yielding.opacity < 1);
  assert.equal(approaching.player.z, walker.z - 1.1, 'the decorative layer does not move the car');
  const close = state(.45, 'racing', { x: walker.x, z: walker.z - 2 });
  assert.ok(display(close).poses.every(p => p.id !== walker.id &&
    Math.hypot(p.x - close.player.x, p.z - close.player.z) > 3.35), 'no walking draw remains next to the car');
});

function elevationBuffer() {
  const buffer = new ArrayBuffer(16 + 33 * 33 * 3);
  const view = new DataView(buffer);
  view.setUint32(0, 0x57465431, false);
  view.setInt32(4, 8, true);
  view.setInt32(12, 8, true);
  for (let i = 0; i < 33 * 33; i++) view.setInt16(16 + 2 * i, 12, true);
  return buffer;
}

test('the Seattle loader uses validated roads, renders rebased walkers only in its ground scene, and releases their rigs', async t => {
  const region = { id: 'conus', coverage: Array.from({ length: 9 }, () => [0, 0, 2, 10]),
    roads: { id: 'seattle', featureCount: 1, pointCount: 2, bounds: [20, 8300, 160, 8370] } };
  const catalog = { regions: [region], tilesUrl: new URL('http://game.example/example/') };
  const roadDoc = { license: 'ODbL-1.0', attribution: '© OpenStreetMap contributors',
    licenseUrl: 'https://www.openstreetmap.org/copyright', roads };
  const requests = [];
  t.mock.method(globalThis, 'fetch', async url => {
    requests.push(url.pathname);
    return url.pathname.endsWith('seattle-roads.json') ? Response.json(roadDoc) : new Response(elevationBuffer());
  });
  const { terrain } = await loadSeattleDrive({
    SeattleDriveAreaInfo: () => JSON.stringify({ tiles: [{ level: 8, x: 0, z: 8 }] }),
    SelectSeattleDrive(json) {
      assert.deepEqual(JSON.parse(json), roadDoc);
      return JSON.stringify({ kind: 'seattle-drive', world });
    },
  }, catalog);
  const renderer = graphics(), scene = new WorldDriveScene(renderer, world, terrain);
  const tile = terrain.at(54, z);
  tile.mesh = renderer.mesh({ vertices: [] });
  terrain.visible = [tile];
  terrain.rendered.set('8/0/8', tile);
  terrain.meshQueue = [];
  terrain.nextUpdate = Infinity;
  let frame;
  for (let i = 0; i < 5; i++) frame = scene.update(state(0), .016, 'chase');
  const { poses } = scene.diagnostics().pedestrians;
  assert.ok(poses.length > 0, 'actual rendered sidewalk triangles enable actual walker draws');
  const rigs = scene.walkers.rigs.flatMap(rig => [rig.body, rig.limb]);
  const drawnBodies = frame.draws.filter(draw => scene.walkers.rigs.some(rig => draw.mesh === rig.body));
  assert.equal(drawnBodies.length, poses.length);
  assert.ok(drawnBodies.every(({ model }, i) => Math.abs(model[12] - poses[i].x) < .001 &&
    Math.abs(model[14] - (poses[i].z - 8192)) < .001), 'walker meshes follow the same world rebasing as the road and car');
  const otherState = { ...state(), drive: undefined };
  assert.ok(scene.update(otherState, .016, 'chase').draws.every(draw => !rigs.includes(draw.mesh)));
  const flight = new WorldFlightScene(renderer, { name: 'Seattle', islands: [], landingSurfaces: [],
    seaHeight: 0, runwayStart: { x: 0, z: 0 }, geography: { destination: { name: 'Seattle', x: 0, z: z } } }, terrain);
  const flightFrame = flight.update({ phase: 'racing', flight: { aircraft: { x: 200, y: 230, z, yaw: 0, pitch: 0, roll: 0,
    speed: 50, gearDown: true, forward: { x: 0, y: 0, z: 1 }, up: { x: 0, y: 1, z: 0 } } } }, .016, 'chase');
  assert.equal(flight.diagnostics().pedestrians, undefined, 'World Flight does not opt into Seattle walking');
  assert.ok(flightFrame.draws.every(draw => !rigs.includes(draw.mesh)));
  assert.deepEqual(requests, ['/example/seattle-roads.json', '/example/tiles/conus/8/0/8.bin']);
  scene.dispose();
  assert.ok(rigs.every(mesh => !renderer.meshes.has(mesh)));
});
