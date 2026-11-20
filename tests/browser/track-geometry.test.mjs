import assert from 'node:assert/strict';
import test from 'node:test';
import { RaceScene, makeVehicleGeometry, vehicleStyles } from '../../src/StuntRacer.Browser/wwwroot/scene.js';
import { lookAt } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

function fixture() {
  return {
    name: 'Synthetic imported geometry', length: 70, width: 10,
    points: [
      { s: 0, x: 0, y: 11, z: 0, heading: 0, bank: 0, width: 10, road: true },
      { s: 20, x: 0, y: 15, z: 20, heading: 0, bank: 0, width: 12, road: true },
      { s: 45, x: 20, y: 12, z: 10, heading: Math.PI / 2, bank: 0, width: 8, road: true },
    ],
    edges: [
      { left: { x: -5, y: 10, z: 2 }, right: { x: 5, y: 12, z: -2 } },
      { left: { x: -6, y: 14, z: 22 }, right: { x: 6, y: 16, z: 18 } },
      { left: { x: 18, y: 12, z: 14 }, right: { x: 22, y: 12, z: 6 } },
    ],
  };
}

test('rendered road preserves supplied skewed/banked edge vertices instead of rebuilding a perpendicular strip', () => {
  const track = fixture();
  const scene = new RaceScene({ mesh: geometry => geometry }, track);
  const actual = [0, 10, 20, 30, 40, 50].map(offset => scene.road.vertices.slice(offset, offset + 3));
  const vertex = point => [point.x, point.y, point.z];
  assert.deepEqual(actual, [
    vertex(track.edges[0].left), vertex(track.edges[1].left), vertex(track.edges[0].right),
    vertex(track.edges[0].right), vertex(track.edges[1].left), vertex(track.edges[1].right),
  ]);
});

test('the active checkpoint frame spans the canonical interpolated edges and advances to the finish', () => {
  const track = fixture(), scene = new RaceScene({ mesh: geometry => geometry }, track);
  const matrix = scene.checkpointFrame(10 / track.length);
  const project = x => [matrix[0] * x + matrix[12], matrix[1] * x + matrix[13], matrix[2] * x + matrix[14]];
  assert.deepEqual(project(-0.5), [-5.5, 12, 12]);
  assert.deepEqual(project(0.5), [5.5, 14, 8]);
  const finish = scene.checkpointFrame(0);
  assert.deepEqual([...finish.slice(12, 15)], [0, 11, 0]);
  assert.ok([...matrix].every(Number.isFinite));
});

test('only an actual enabled recorded ghost adds translucent non-shadowing car draws', () => {
  const scene = new RaceScene({ mesh: geometry => geometry }, fixture());
  const car = { x: 0, y: 12, z: 5, yaw: 0, pitch: 0, roll: 0, speed: 10, steer: 0, suspension: [0.5, 0.5, 0.5, 0.5] };
  const state = { player: car, phase: 'racing', impact: 0, lapTime: 3, ghost: { enabled: true, car: null } };
  assert.equal(scene.update(state, 1 / 60).draws.some(draw => draw.mesh === scene.ghost), false);
  const ghost = { ...car, x: 3, z: 18 };
  const draws = scene.update({ ...state, ghost: { enabled: true, car: ghost } }, 1 / 60).draws;
  const replay = draws.filter(draw => draw.opacity !== undefined);
  assert.equal(replay.length, 5, 'the recorded body and all four wheels must be rendered');
  assert.ok(replay.every(draw => draw.opacity === 0.4 && draw.shadow === false));
  assert.deepEqual([...replay[0].model.slice(12, 15)], [3, 12, 18], 'draw the recorded pose, not the live player');
  assert.equal(scene.update({ ...state, ghost: { enabled: false, car: ghost } }, 1 / 60)
    .draws.some(draw => draw.mesh === scene.ghost), false);
  const trial = { ...state, trial: { success: null, nextCheckpointProgress: 0.25 } };
  assert.equal(scene.update(trial, 1 / 60).draws.some(draw => draw.mesh === scene.checkpointGate), true);
  assert.equal(scene.update({ ...trial, trial: { ...trial.trial, success: true } }, 1 / 60)
    .draws.some(draw => draw.mesh === scene.checkpointGate), false);
});

test('a spatial stunt loop retains its crown and both cameras follow the inverted vehicle basis', () => {
  const points = [], edges = [], count = 24;
  for (let i = 0; i < count; i++) {
    const angle = i * Math.PI * 2 / count, y = 20 - 16 * Math.cos(angle), z = 16 * Math.sin(angle);
    points.push({ s: i, x: 0, y, z, heading: 0, bank: 0, width: 8, road: true });
    edges.push({ left: { x: -4, y, z }, right: { x: 4, y, z } });
  }
  const scene = new RaceScene({ mesh: geometry => geometry }, { name: 'Spatial loop', isSpatial: true,
    points, edges, length: count, width: 8 });
  assert.ok(scene.road.vertices.some((value, i) => i % 10 === 1 && value >= 36));
  assert.ok(Math.min(...scene.road.vertices.filter((_, i) => i % 10 === 1)) > 2,
    'generic ground piers and their cross-braces must not cut through the spatial loop');
  const car = { x: 0, y: 35, z: 0, yaw: 0, pitch: Math.PI, roll: 0, speed: 24, steer: 0,
    suspension: [.5, .5, .5, .5] };
  const state = { player: car, phase: 'racing', impact: 0, lapTime: 2 };
  for (const mode of ['chase', 'cockpit']) {
    const { camera } = scene.update(state, 1 / 60, mode);
    assert.ok(camera.up[1] < -.99, 'loop cameras must not silently revert to global upright');
    assert.ok([...lookAt(camera.eye, camera.target, camera.up)].every(Number.isFinite));
    assert.equal(camera.logarithmicDepth, true);
  }
  const approach = new RaceScene({ mesh: geometry => geometry }, { name: 'Spatial approach', isSpatial: true,
    points, edges, length: count, width: 8 });
  const view = approach.update({ ...state, player: { ...car, y: 5, z: -40, pitch: 0 },
    stunt: { feature: 'Loop approach', success: null, nextGateProgress: .25 } }, 1 / 60, 'chase').camera;
  assert.ok(view.target[1] > 11 && view.fov > 1.3,
    'the approach camera must reveal the rising loop instead of framing only a wall at its base');
});

test('vehicle families have different silhouettes, and enclosed cockpit mode does not draw an opaque exterior shell', () => {
  const geometries = vehicleStyles.map(style => makeVehicleGeometry(style.id, [.4, .4, .4]));
  const shapes = geometries.map(geometry => JSON.stringify(geometry.vertices.filter((_, i) => i % 10 < 3)));
  assert.equal(new Set(shapes).size, vehicleStyles.length, 'body variety must be geometry, not only paint colours');
  const height = style => Math.max(...geometries[vehicleStyles.findIndex(s => s.id === style)].vertices.filter((_, i) => i % 10 === 1));
  assert.ok(height('van') > height('coupe') + .7);
  const scene = new RaceScene({ mesh: geometry => geometry }, fixture());
  const buggy = scene.car;
  scene.setVehicleStyle('sedan');
  assert.notEqual(scene.car, buggy);
  const car = { x: 0, y: 12, z: 5, yaw: 0, pitch: 0, roll: 0, speed: 10, steer: 0, suspension: [.5, .5, .5, .5] };
  const frame = scene.update({ player: car, phase: 'racing', impact: 0, lapTime: 3 }, 1 / 60, 'cockpit');
  assert.ok(frame.draws.some(draw => draw.mesh === scene.cockpitBody));
  assert.equal(frame.draws.some(draw => draw.mesh === scene.car), false);
  assert.throws(() => scene.setVehicleStyle('unknown'), /Unknown vehicle body/);
});
