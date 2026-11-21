import assert from 'node:assert/strict';
import test from 'node:test';
import { DEFAULT_VEHICLE_STYLE, RaceScene, makeVehicleGeometry, vehicleStyles } from '../../src/StuntRacer.Browser/wwwroot/scene.js';
import { cross, dot, lookAt, sub } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

const rows = geometry => Array.from({ length: geometry.vertices.length / 10 },
  (_, i) => geometry.vertices.slice(i * 10, i * 10 + 10));

function intercepts(geometry, origin, ray, limit = Infinity) {
  const vertices = rows(geometry);
  for (let i = 0; i < vertices.length; i += 3) {
    const [a, b, c] = vertices.slice(i, i + 3);
    const ab = sub(b, a), ac = sub(c, a), p = cross(ray, ac), det = dot(ab, p);
    if (Math.abs(det) < 1e-8) continue;
    const t = sub(origin, a), u = dot(t, p) / det, q = cross(t, ab), v = dot(ray, q) / det;
    const distance = dot(ac, q) / det;
    if (u >= 0 && v >= 0 && u + v <= 1 && distance > 0 && distance < limit) return true;
  }
  return false;
}

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
  scene.setVehicleStyle('buggy');
  const buggy = scene.car;
  scene.setVehicleStyle('sedan');
  assert.notEqual(scene.car, buggy);
  const car = { x: 0, y: 12, z: 5, yaw: 0, pitch: 0, roll: 0, speed: 10, steer: 0, suspension: [.5, .5, .5, .5] };
  const frame = scene.update({ player: car, phase: 'racing', impact: 0, lapTime: 3 }, 1 / 60, 'cockpit');
  assert.ok(frame.draws.some(draw => draw.mesh === scene.cockpitBody));
  assert.equal(frame.draws.some(draw => draw.mesh === scene.car), false);
  assert.throws(() => scene.setVehicleStyle('unknown'), /Unknown vehicle body/);
});

test('default EV6 has real arch openings, a continuous rear-facing light bar and a clear cockpit cutaway', () => {
  const scene = new RaceScene({ mesh: geometry => geometry }, fixture());
  assert.equal(scene.vehicleStyle, 'ev6');
  assert.equal(scene.vehicleStyle, DEFAULT_VEHICLE_STYLE);
  for (const style of ['ev6', 'buggy', 'coupe', 'sedan', 'hatch', 'van', 'pickup'])
    assert.ok(vehicleStyles.some(choice => choice.id === style), `missing selectable ${style}`);

  const exterior = scene.car;
  for (const axle of [-1.22, 1.30])
    assert.equal(intercepts(exterior, [1.32, .06, axle], [-1, 0, 0], .44), false, 'fenders must open over the tyres');
  assert.ok(intercepts(exterior, [1.32, .06, 0], [-1, 0, 0], .44), 'the body between arches must still be solid');
  const ext = rows(exterior), rear = [];
  for (let i = 0; i < ext.length; i += 3) {
    const triangle = ext.slice(i, i + 3);
    if (triangle.every(v => v[9] === 4 && v[5] < -.25 && v[6] > 4 * v[7] && v[2] < -1.7))
      rear.push([Math.min(...triangle.map(v => v[0])), Math.max(...triangle.map(v => v[0]))]);
  }
  rear.sort((a, b) => a[0] - b[0]);
  assert.ok(rear.length && rear[0][0] < -.85, 'rear light must start at the outer fender');
  let end = rear[0][0];
  for (const [left, right] of rear) {
    assert.ok(left <= end + .001, 'rear lamps must join across the centre rather than remain two clusters');
    end = Math.max(end, right);
  }
  assert.ok(end > .85, 'rear light must reach the other fender');

  const car = { x: 0, y: 12, z: 5, yaw: 0, pitch: 0, roll: 0, speed: 10, steer: .25, suspension: [.2, .4, .6, .8] };
  const state = { player: car, phase: 'racing', impact: 0, lapTime: 3 };
  const cockpit = scene.update(state, 0, 'cockpit');
  assert.ok(cockpit.draws.some(draw => draw.mesh === scene.cockpitBody));
  assert.ok(cockpit.draws.every(draw => draw.mesh !== exterior), 'opaque EV body must remain outside cockpit draws');
  const instruments = rows(scene.cockpitBody).filter(v => v[9] === 4 && v[5] < -.5 && v[2] > .5);
  for (const [l, r] of [[-.27, .27], [.31, .68]])
    assert.ok(instruments.some(v => v[0] > l && v[0] < r), 'driver and console displays need forward-mounted instruments');
  const eye = sub(cockpit.camera.eye, [car.x, car.y, car.z]);
  for (const x of [-.35, 0, .35]) for (const y of [-.16, 0, .42])
    assert.equal(intercepts(scene.cockpitBody, eye, [x, y, 1]), false, 'road and sky must remain visible from the existing eye');

  const footprint = mesh => rows(mesh).map(v => v.slice(0, 3));
  assert.deepEqual(footprint(scene.playerWheel), footprint(scene.wheel), 'the new trim must not move any tyre vertices');
  const wheels = frame => frame.draws.filter(draw => draw.mesh === scene.playerWheel).map(draw => [...draw.model]);
  const evWheels = wheels(cockpit);
  scene.setVehicleStyle('buggy');
  assert.equal(evWheels.length, 4);
  assert.deepEqual(evWheels, wheels(scene.update(state, 0, 'cockpit')), 'steer and suspension must use the existing wheel rig');
});

test('named spatial courses draw distinct approach and edge cues outside canonical edges and leave the gap open', () => {
  const samples = [
    // distance, height, road width, road present
    [0, 6, 12], [80, 6, 12], [110, 6, 12], [120, 7, 12], [126, 12, 12], [134, 20, 12], [166, 6, 12],
    [220, 6, 12], [240, 9, 10], [258, 16, 6], [310, 16, 6], [338, 6, 12], [380, 6, 12],
    [404, 11, 12], [408, 11, 12, false], [423, 11, 12, false], [426, 11, 12], [465, 11, 12], [480, 6, 12],
  ];
  const forward = [.6, 0, .8], across = [.8 * Math.cos(.27), Math.sin(.27), -.6 * Math.cos(.27)];
  const up = cross(forward, across);
  const points = samples.map(([s, y, width, road = true]) =>
    ({ s, x: 120 + s * forward[0], y, z: -40 + s * forward[2], width, road, heading: 0, bank: 0 }));
  const edges = points.map(p => {
    const at = sign => Object.fromEntries(['x', 'y', 'z'].map((k, i) => [k, p[k] + sign * across[i] * p.width / 2]));
    return { left: at(-1), right: at(1) };
  });
  const track = { isSpatial: true, width: 12, length: 510, points, edges };
  const make = name => new RaceScene({ mesh: geometry => geometry }, { ...track, name });
  const park = make('Skyline Loop Park — authored Stunts-inspired challenge');
  const proving = make("Hard Drivin' Loop & Bridge Proving Ground — authored timed challenge");
  const canonical = make('Other spatial course');
  assert.equal(canonical.courseScenery, null);
  const car = { ...points[0], yaw: Math.atan2(forward[0], forward[2]), pitch: 0, roll: 0, speed: 13, steer: 0 };
  const state = { player: car, phase: 'racing', impact: 0 };

  const local = v => {
    const s = dot(sub(v, [120, 0, -40]), forward);
    const i = samples.findIndex(([distance]) => distance > s);
    const [a, b] = [samples[i - 1], samples[i]], t = (s - a[0]) / (b[0] - a[0]);
    const center = [120 + s * forward[0], a[1] + t * (b[1] - a[1]), -40 + s * forward[2]];
    return [s, dot(sub(v, center), across), (a[2] + t * (b[2] - a[2])) / 2, dot(sub(v, center), up)];
  };
  const layouts = [park, proving].map(scene => {
    assert.deepEqual(scene.road.vertices, canonical.road.vertices, 'scenery must not replace or reshape the road strip');
    assert.ok(scene.courseScenery, `${scene.track.name} is missing the mode-specific mesh`);
    for (const mode of ['chase', 'cockpit']) {
      const progress = 285 / track.length;
      const { draws } = scene.update({ ...state, trial: { success: null, nextCheckpointProgress: progress } }, 0, mode);
      assert.deepEqual(draws.filter(draw => draw.mesh === scene.courseScenery),
        [{ mesh: scene.courseScenery, shadow: false }], 'one batched world-space draw in both cameras');
      const gate = draws.find(draw => draw.mesh === scene.checkpointGate).model;
      assert.ok(dot(gate.slice(0, 3), across) > 5.99 && dot(gate.slice(8, 11), forward) > .999,
        'trial gate must track the banked cross-section and direction of travel');
    }

    const lights = [];
    for (const v of rows(scene.courseScenery)) {
      const [s, lateral, halfWidth, height] = local(v);
      assert.ok(Math.abs(lateral) - halfWidth > .12, 'cosmetic art may not protrude into the drivable cross-section');
      assert.ok(s < 404 || s > 426, 'no lamp, sign or fabricated decking may extend into the air gap');
      if (v[9] === 4) lights.push({ s, lateral, height, normal: v.slice(3, 6), color: v.slice(6, 9).join() });
    }
    const near = (l, r) => lights.filter(light => light.s > l && light.s < r);
    assert.ok(near(50, 86).some(v => v.height > 2 && dot(v.normal, forward) < -.95),
      'loop approach symbol must face the driven direction rather than world axes');
    const sides = marks => new Set(marks.map(mark => Math.sign(mark.lateral)));
    assert.equal(sides(near(258, 310)).size, 2, 'narrow bridge needs visible cues on both sides');
    const takeoff = near(402, 404), landing = near(426, 428);
    assert.equal(sides(takeoff).size, 2, 'edge lights must mark both sides of the actual launch lip');
    assert.equal(sides(landing).size, 2, 'landing lights must mark the far side of the actual gap');
    assert.notEqual(takeoff[0].color, landing[0].color, 'launch and landing must be visually distinguishable');
    return lights;
  });
  const loopSides = lights => new Set(lights.filter(v => v.s < 85 && v.height > 2).map(v => Math.sign(v.lateral)));
  assert.equal(loopSides(layouts[0]).size, 2, 'the park has paired loop signs');
  assert.equal(loopSides(layouts[1]).size, 1, 'the proving ground uses a single board beside survey blades');
});
