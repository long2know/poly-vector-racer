import assert from 'node:assert/strict';
import test from 'node:test';
import { FlightScene, aircraftFrame, flightReadouts } from '../../src/StuntRacer.Browser/wwwroot/flight-scene.js';
import { cross, dot, lookAt, sub, transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

const world = {
  name: 'Authored flight fixture', seaHeight: 0, gearClearance: 2.4,
  islands: [{ name: 'Island', centerX: 0, centerZ: 0, width: 4000, length: 6000, height: 8, heading: 0 }],
  landingSurfaces: [{ name: 'Runway', centerX: 0, centerZ: 0, width: 80, length: 3000, height: 8, heading: 0 }],
};
const aircraft = {
  x: 50, y: 200, z: 300, yaw: 0, pitch: 0, roll: -0.5, speed: 90,
  forward: { x: 0, y: 0, z: 1 }, up: { x: 0.5, y: Math.sqrt(0.75), z: 0 },
  gearDown: true, afterburner: false,
  throttle: 0.6, fuel: 0.8,
};

test('flight instruments derive the nearest field, relative bearing and wrapped heading from actual state', () => {
  const state = { ...aircraft, x: 0, z: -1000, yaw: Math.PI / 2, pitch: 0.2, roll: -0.5 };
  const readout = flightReadouts(state, world);
  assert.equal(readout.heading, 90);
  assert.equal(readout.range, 1000);
  assert.equal(readout.runway, 'Runway');
  assert.ok(Math.abs(readout.bearing + Math.PI / 2) < 1e-12, 'a north runway is left of an eastbound jet');
  assert.ok(readout.pitch > 0 && readout.bank < 0);
  assert.equal(flightReadouts({ ...state, yaw: -Math.PI / 2 }, world).heading, 270);
});

test('jet pose uses the authoritative orthogonal flight basis, including right-wing-down banking', () => {
  const matrix = aircraftFrame(aircraft);
  assert.deepEqual([...matrix.slice(12, 15)], [50, 200, 300]);
  const wing = transform(matrix, [1, 0, 0]);
  assert.ok(wing[0] > aircraft.x && wing[1] < aircraft.y);
  const nose = transform(matrix, [0, 0, 10]);
  assert.deepEqual(nose, [50, 200, 310]);
});

test('flight world geometry uses the supplied island and runway, not the racing surface', () => {
  const scene = new FlightScene({ mesh: mesh => mesh }, world);
  const vertices = scene.ground.vertices;
  const has = (x, y, z) => vertices.some((value, i) => i % 10 === 0 &&
    Math.abs(value - x) < 1e-6 && Math.abs(vertices[i + 1] - y) < 1e-6 && Math.abs(vertices[i + 2] - z) < 1e-6);
  assert.ok(has(-2000, 8, -3000) && has(2000, 8, 3000), 'the displayed top must match the native island height and extent');
  assert.ok(has(-40, 8.045, -1500) && has(40, 8.045, 1500), 'the runway must use its native extents with only a tiny paint offset');
});

test('original flight renders source pad outlines and enhanced paint without an authored-island replacement', () => {
  const source = {
    ...world, islands: [],
    sourceWorld: {
      surfaces: [{ kind: 'runway', vertices: [
        { x: -20, y: 0.025, z: -100 }, { x: -20, y: 0.025, z: 100 },
        { x: 20, y: 0.025, z: 100 }, { x: 20, y: 0.025, z: -100 },
      ] }],
      objects: [], geometryNotes: [],
      mapOutline: [{ x: -100, y: 0, z: -200 }, { x: 100, y: 0, z: 200 }],
    },
  };
  const scene = new FlightScene({ mesh: mesh => mesh }, source);
  assert.ok(scene.ground.vertices.length > 60, 'source quad and deliberate enhanced paint must both be present');
  assert.deepEqual(scene.track.points, source.sourceWorld.mapOutline);
  assert.deepEqual(scene.mapPoint(0, 0), [80, 80]);
});

test('gear, afterburner and cockpit draw actual jet geometry and vertical flight keeps a valid camera', () => {
  const scene = new FlightScene({ mesh: mesh => mesh }, world);
  const state = { phase: 'racing', flight: { aircraft } };
  let frame = scene.update(state, 1 / 60);
  assert.ok(frame.draws.some(draw => draw.mesh === scene.jet));
  assert.ok(frame.draws.some(draw => draw.mesh === scene.gear));
  assert.equal(frame.draws.some(draw => draw.mesh === scene.flame), false);
  frame = scene.update({ ...state, flight: { aircraft: { ...aircraft, gearDown: false, afterburner: true } } }, 1 / 60);
  assert.equal(frame.draws.some(draw => draw.mesh === scene.gear), false);
  assert.ok(frame.draws.some(draw => draw.mesh === scene.flame));
  frame = scene.update(state, 1 / 60, 'cockpit');
  assert.ok(frame.draws.some(draw => draw.mesh === scene.cockpit));
  assert.equal(frame.draws.some(draw => draw.mesh === scene.jet), false);
  assert.ok(frame.draws.some(draw => draw.mesh === scene.combiner && draw.opacity > 0 && draw.opacity < .2),
    'the HUD combiner is a transparent plate, not an opaque dashboard across the windshield');
  assert.deepEqual(frame.camera.up, [aircraft.up.x, aircraft.up.y, aircraft.up.z]);
  const bars = frame.draws.filter(draw => draw.mesh === scene.instrumentBar);
  assert.equal(bars.length, 2, 'cockpit displays must use live throttle and fuel, not static painted rectangles');
  assert.ok(bars.every(draw => draw.shadow === false));
  assert.notDeepEqual([...bars[0].model], [...bars[1].model]);
  const vertical = { ...aircraft, forward: { x: 0, y: 1, z: 0 }, up: { x: 0, y: 0, z: -1 } };
  frame = scene.update({ ...state, flight: { aircraft: vertical } }, 1 / 60);
  assert.ok([...lookAt(frame.camera.eye, frame.camera.target, frame.camera.up)].every(Number.isFinite));
  assert.ok(frame.camera.far > 10000 && frame.camera.fogDistance > 1000);
  const distant = { ...aircraft, x: 100000, z: -100000 };
  frame = scene.update({ ...state, flight: { aircraft: distant } }, 1 / 60);
  const sea = frame.draws.find(draw => draw.mesh === scene.sea);
  assert.deepEqual(transform(sea.model, [0, world.seaHeight, 0]), [distant.x, world.seaHeight, distant.z],
    'the sea must remain beneath long free flights instead of ending at the starting-area mesh bounds');
});

test('the jet cockpit keeps the central windscreen free of opaque bracing', () => {
  const scene = new FlightScene({ mesh: mesh => mesh }, world);
  const straight = { ...aircraft, x: 0, y: 0, z: 0, roll: 0, up: { x: 0, y: 1, z: 0 } };
  const { camera } = scene.update({ phase: 'racing', flight: { aircraft: straight } }, .016, 'cockpit');
  const occludes = direction => {
    const v = scene.cockpit.vertices;
    for (let i = 0; i < v.length; i += 30) {
      const a = v.slice(i, i + 3), b = v.slice(i + 10, i + 13), c = v.slice(i + 20, i + 23);
      const ab = sub(b, a), ac = sub(c, a), side = cross(direction, ac);
      const det = dot(ab, side);
      if (Math.abs(det) < 1e-8) continue;
      const d = sub(camera.eye, a), u = dot(d, side) / det;
      const q = cross(d, ab), w = dot(direction, q) / det;
      if (u >= 0 && w >= 0 && u + w <= 1 && dot(ac, q) / det > .12) return true;
    }
    return false;
  };
  for (const x of [-.42, -.28, 0, .28, .42])
    for (const y of [-.06, 0, .17, .30])
      assert.equal(occludes([x, y, 1]), false, `opaque structure in line of sight at ${x}, ${y}`);
  assert.ok(occludes([0, -.45, 1]), 'the instrument panel is still present below the windscreen');
});
