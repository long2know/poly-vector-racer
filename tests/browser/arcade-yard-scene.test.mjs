import assert from 'node:assert/strict';
import test from 'node:test';
import { ArcadeYardScene } from '../../src/StuntRacer.Browser/wwwroot/arcade-yard-scene.js';

const track = { points: [{ x: -20, y: 0, z: -30 }, { x: -20, y: 0, z: 38 },
  { x: 20, y: 0, z: 38 }, { x: 20, y: 0, z: -30 }] };
const world = { groundY: .75, bounds: { minX: -20, maxX: 20, minZ: -30, maxZ: 38 }, spawn: { x: 0, z: -20 },
  lanes: [{ color: 'mint', width: 8, points: [{ x: 0, z: -25 }, { x: 0, z: 1 }] },
    { color: 'gold', width: 6, points: [{ x: 0, z: 1 }, { x: -6, z: 12 }, { x: -14, z: 24 }] }],
  props: [{ id: 'first', x: 0, z: -9, size: 2.4, color: 'mint' }, { id: 'side', x: -6, z: 12, size: 2.8, color: 'gold' }] };
const car = { x: 0, y: world.groundY + 1, z: -20, yaw: 0, pitch: 0, roll: 0, speed: 0, steer: 0, suspension: [.5, .5, .5] };
const hitTick = 240;
const state = { player: car, phase: 'racing', impact: 0, tick: hitTick + 180, arcade: { elapsed: hitTick / 120,
  props: [{ id: 'first', x: 1.5, z: -9, hit: false }, { id: 'side', x: -6, z: 12, hit: false }] } };
const vertices = mesh => Array.from({ length: mesh.vertices.length / 10 }, (_, i) => mesh.vertices.slice(i * 10, i * 10 + 10));
const debris = draws => draws.filter(draw => draw.model && draw.shadow === false);
const clearance = ({ mesh, model }) => Math.min(...vertices(mesh).map(([x, y, z]) =>
  model[13] + x * model[1] + y * model[5] + z * model[9] - world.groundY));
const scoredProps = [{ ...state.arcade.props[0], hit: true, hitTick }, state.arcade.props[1]];
const yardSnapshot = (elapsedTicks, phase = 'racing', props = scoredProps) => ({
  ...state, phase, tick: elapsedTicks + 180, arcade: { elapsed: elapsedTicks / 120, props },
});

function yardScene(yard = world) {
  const meshes = [];
  const scene = new ArcadeYardScene({ mesh: geometry => (meshes.push(geometry), geometry) }, track, yard);
  return { meshes, scene, draw: (snapshot, dt = 1 / 60) => scene.update(snapshot, dt).draws };
}

test('the Neon Yard meshes use the authored fence, marked branch and glowing start opening', () => {
  const { scene, draw } = yardScene();
  assert.ok(draw(state).some(item => item.mesh === scene.road));
  const road = vertices(scene.road);
  assert.ok(road.some(([x, y, z]) => x === world.bounds.minX && y === world.groundY && z === world.bounds.maxZ),
    'the playable deck did not reach the native boundary');
  assert.ok(road.some(v => v[0] < -7 && v[2] > 15 && v[9] === 4), 'the side branch has no visible colored lane paint');
  const fixtures = vertices(scene.props);
  assert.ok(fixtures.some(v => v[0] > world.bounds.maxX && v[1] >= 1 && v[9] === 4),
    'the authored boundary has no raised luminous rail');
  assert.ok(fixtures.some(v => v[1] > 4 && Math.abs(v[2] - world.spawn.z - 4) < 1 && v[9] === 4),
    'the start opening is not visibly illuminated at the launch');
  const map = scene.mapRoadPath();
  assert.equal((map.match(/M/g) ?? []).length, world.lanes.length + 1);
  assert.match(map, / Z M/, 'map should have a closed outline and separate driving routes');
  assert.ok([...road, ...fixtures].every(v => v.every(Number.isFinite)));
});

test('a hit replaces only its cube with modular foam that descends, bounces, and fades at rest', () => {
  const { scene, draw } = yardScene();
  const first = scene.cubes.get('first').cube, side = scene.cubes.get('side').cube;
  const before = draw(state);
  assert.deepEqual([...before.find(item => item.mesh === first).model.slice(12, 15)], [1.5, world.groundY, -9]);
  assert.equal(debris(before).length, 0);

  const after = draw(yardSnapshot(hitTick));
  assert.ok(!after.some(item => item.mesh === first), 'a scored cube still draws at its old position');
  assert.deepEqual(after.find(item => item.mesh === side), before.find(item => item.mesh === side));
  const chunks = debris(after);
  assert.equal(chunks.length, 8);
  assert.ok(chunks.every(({ model }) => Math.hypot(model[12] - 1.5, model[14] + 9) < 1),
    'the eight foam sections did not start at the native hit position');
  assert.equal(new Set(chunks.map(({ model }) => model.slice(12, 15).join(','))).size, 8);

  const upper = chunks.findIndex(({ model }) => model[13] > world.groundY + 1.5);
  assert.notEqual(upper, -1, 'no chunk detached from the upper half of the cube');
  const path = Array.from({ length: 133 }, (_, age) => debris(draw(yardSnapshot(hitTick + age))));
  const heights = path.map(items => clearance(items[upper]));
  const curvature = age => heights[age + 12] - 2 * heights[age] + heights[age - 12];
  assert.ok(curvature(32) < -.05 && Math.abs(curvature(32) - curvature(44)) < .0005,
    'the rendered descent lacked consistent downward acceleration');
  const landing = heights.findIndex((height, age) => age > 56 && height < .03);
  assert.ok(landing > 56 && landing < 96, 'an upper chunk did not reach the yard deck');
  assert.ok(Math.max(...heights.slice(landing + 1)) > .10, 'the chunk did not visibly rebound after contact');
  assert.ok(path.flat().every(item => clearance(item) >= -.001), 'a tumbling chunk went through the yard deck');
  const settled = path.at(-1);
  assert.ok(settled.every(item => Math.abs(clearance(item)) < .001 && item.opacity === 1),
    'chunks did not come to rest on the deck before fading');
  assert.notEqual(settled[upper].model[12], chunks[upper].model[12], 'a chunk never scattered');
  const fading = debris(draw(yardSnapshot(hitTick + 162)));
  assert.deepEqual(fading.map(item => item.model), settled.map(item => item.model));
  assert.ok(fading.every(item => item.opacity > 0 && item.opacity < 1));
  assert.equal(debris(draw(yardSnapshot(hitTick + 180))).length, 0);
});

test('native elapsed ticks drive debris; pauses freeze it and completed runs cleanly wind down', () => {
  const { scene, draw } = yardScene();
  assert.equal(debris(draw(yardSnapshot(hitTick - 1))).length, 0, 'debris appeared before the native hit tick');
  const active = yardSnapshot(hitTick + 24);
  const airborne = debris(draw(active));
  assert.ok(airborne.length);
  assert.deepEqual(debris(yardScene().draw(active, .1)), airborne, 'a new scene did not reproduce the same hit');
  const paused = { ...active, phase: 'paused' };
  for (const dt of [.002, .1, .04]) assert.deepEqual(debris(draw(paused, dt)), airborne, 'pause advanced the foam pieces');
  assert.notDeepEqual(debris(draw(yardSnapshot(hitTick + 30))), airborne, 'resuming did not advance native motion');

  const finish = { ...active, phase: 'finished' };
  assert.deepEqual(debris(draw(finish, .1)), airborne, 'finishing skipped the last native pose');
  assert.deepEqual(debris(draw(finish, .1)), debris(yardScene().draw(yardSnapshot(hitTick + 36))),
    'the finished screen failed to advance the same trajectory at its frozen native tick');
  for (let i = 0; i < 15; i++) draw(finish, .1);
  assert.equal(debris(draw(finish)).length, 0, 'finishing left debris frozen in the yard');

  const restart = draw({ ...yardSnapshot(0, 'countdown', state.arcade.props), tick: 0 });
  assert.equal(debris(restart).length, 0);
  assert.ok(restart.some(item => item.mesh === scene.cubes.get('first').cube), 'restart did not restore the cube');
  assert.deepEqual(debris(draw(finish)), airborne, 'restart retained aged debris from the previous result');
  const outsideYard = { player: car, phase: 'racing', tick: 1, impact: 0 };
  assert.equal(debris(draw(outsideYard)).length, 0, 'another mode inherited the yard debris');
});

test('only the three newest nearby bursts draw and all use the prebuilt palette meshes', () => {
  const makeProp = (id, x, z) => ({ id, x, z, size: 2.4, color: 'mint' });
  const nearby = Array.from({ length: 10 }, (_, i) => makeProp(`near-${i}`, -18 + 4 * i, -9));
  const far = makeProp('far', 0, 75), stale = makeProp('stale', 0, 10);
  const { meshes, draw } = yardScene({ ...world, bounds: { ...world.bounds, maxZ: 90 }, props: [...nearby, far, stale] });
  const count = meshes.length;
  const now = 600;
  const stamp = ({ id, x, z }, age) => ({ id, x, z, hit: true, hitTick: now - age });
  const alone = [stamp(far, 0), stamp(stale, 240)];
  assert.equal(debris(draw(yardSnapshot(now, 'racing', alone))).length, 0, 'distant or expired debris drew anyway');
  const crowded = [...nearby.map((prop, i) => stamp(prop, 10 - i)), ...alone];
  const bursts = debris(draw(yardSnapshot(now, 'racing', crowded)));
  assert.equal(bursts.length, 24, 'debris exceeded the three-burst budget');
  for (const prop of nearby.slice(-3)) {
    assert.equal(bursts.filter(({ model }) => Math.abs(model[12] - prop.x) < 2).length, 8,
      `the budget dropped the recent nearby hit at ${prop.id}`);
  }
  assert.equal(new Set(bursts.map(item => item.mesh)).size, 1, 'same-color bursts did not reuse one mesh');
  assert.equal(meshes.length, count, 'hit processing allocated GPU meshes');
  assert.equal(count, yardScene().meshes.length, 'mesh allocation scaled with the prop population');
});
