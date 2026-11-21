import assert from 'node:assert/strict';
import test from 'node:test';
import { FlightScene, aircraftFrame, flightReadouts } from '../../src/StuntRacer.Browser/wwwroot/flight-scene.js';
import { WorldFlightScene, WorldScenery } from '../../src/StuntRacer.Browser/wwwroot/world-flight-scene.js';
import { cross, dot, lookAt, normalize, pose, sub, transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

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

function points(mesh) {
  const result = [];
  for (let i = 0; i < mesh.vertices.length; i += 10) result.push(mesh.vertices.slice(i, i + 3));
  return result;
}

test('aircraft selection changes swept wings, twin tails and length without moving the pilot', () => {
  const graphics = { mesh: mesh => mesh };
  const stock = new FlightScene(graphics, world);
  const byId = Object.fromEntries([['f18', 2.4], ['f14', 2.1], ['f15', 2.4], ['sr71', 3.2]]
    .map(([id, gearClearance]) => [id, new FlightScene(graphics, { ...world, gearClearance }, id)]));
  assert.deepEqual(byId.f18.jet.vertices, stock.jet.vertices, 'the default Hornet keeps its authored geometry');
  const silhouette = scene => {
    const shape = points(scene.jet), halfSpan = Math.max(...shape.map(p => Math.abs(p[0])));
    return { halfSpan, length: Math.max(...shape.map(p => p[2])) - Math.min(...shape.map(p => p[2])),
      tipLead: Math.max(...shape.filter(p => p[0] > halfSpan * .92).map(p => p[2])),
      tails: new Set(shape.filter(p => p[1] > 3 && p[2] < -4).map(p => Math.sign(p[0]))) };
  };
  const [hornet, tomcat, eagle, blackbird] = Object.values(byId).map(silhouette);
  assert.ok(tomcat.halfSpan > eagle.halfSpan && eagle.halfSpan > hornet.halfSpan + 1);
  assert.ok(tomcat.tipLead < eagle.tipLead - 2.5, 'Tomcat outer wing is swept farther aft than the Eagle');
  assert.ok(blackbird.length > tomcat.length * 1.5 && blackbird.length > eagle.length * 1.5);
  for (const shape of [tomcat, eagle, blackbird]) assert.deepEqual(shape.tails, new Set([-1, 1]));

  const state = { phase: 'racing', flight: { aircraft: { ...aircraft, afterburner: true, up: { x: 0, y: 1, z: 0 } } } };
  const hornetChase = stock.update(state, 0).camera.eye;
  const hornetCockpit = stock.update(state, 0, 'cockpit').camera.eye;
  for (const scene of [byId.f14, byId.f15, byId.sr71]) {
    const frame = scene.update(state, 0);
    for (const mesh of [scene.jet, scene.canopy, scene.gear, scene.flame])
      assert.ok(frame.draws.some(draw => draw.mesh === mesh));
    const gearModel = frame.draws.find(draw => draw.mesh === scene.gear).model;
    const bottom = Math.min(...points(scene.gear).map(point => transform(gearModel, point)[1]));
    assert.ok(Math.abs(bottom - (aircraft.y - scene.world.gearClearance)) < .0001,
      `${scene.aircraftId} tyres reach the modeled gear clearance`);
    const plume = points(scene.flame), body = points(scene.jet);
    const z = Math.max(...plume.map(p => p[2]));
    for (const side of [-1, 1]) {
      const base = plume.filter(p => p[2] === z && p[0] * side > 0);
      const x = (Math.min(...base.map(p => p[0])) + Math.max(...base.map(p => p[0]))) / 2;
      assert.ok(body.some(p => Math.abs(p[0] - x) < .02 && Math.abs(p[2] - z - .08) < .02),
        `${scene.aircraftId} burner starts at an actual nozzle`);
    }
    if (scene.aircraftId === 'sr71') {
      assert.ok(frame.camera.eye[2] < hornetChase[2] - 10, 'the longer jet receives more chase clearance');
      assert.ok(Math.max(...plume.map(p => p[0])) > 4, 'both separate nacelles have a matching plume');
    }
    assert.deepEqual(scene.update(state, 0, 'cockpit').camera.eye, hornetCockpit);
  }
});

test('cockpit snap views rotate in the banked aircraft frame and center on its nose without moving the pilot', () => {
  const scene = new FlightScene({ mesh: mesh => mesh }, world);
  const matrix = pose([50, 200, 300], 1.2, 0.3, -0.5);
  const vector = index => ({ x: matrix[index], y: matrix[index + 1], z: matrix[index + 2] });
  const state = { phase: 'racing', flight: { aircraft: { ...aircraft, yaw: 1.2, forward: vector(8), up: vector(4) } } };
  const right = Array.from(matrix.slice(0, 3)), forward = Array.from(matrix.slice(8, 11));
  const { eye } = scene.update(state, 0, 'cockpit').camera;
  for (const [view, rightDot, forwardDot] of [['left', -1, 0], ['right', 1, 0], ['rear', 0, -1],
    ['forward-right', Math.SQRT1_2, Math.SQRT1_2], ['rear-left', -Math.SQRT1_2, -Math.SQRT1_2], ['forward', 0, 1]]) {
    scene.setFlightView(view);
    const camera = scene.update(state, 0, 'cockpit').camera;
    const direction = normalize(sub(camera.target, camera.eye));
    assert.deepEqual(camera.eye, eye, `pilot eye must not move when snapping to ${view}`);
    assert.ok(Math.abs(dot(direction, right) - rightDot) < 1e-6, `${view} right component`);
    assert.ok(Math.abs(dot(direction, forward) - forwardDot) < 1e-6, `${view} nose component`);
    assert.ok([...lookAt(camera.eye, camera.target, camera.up)].every(Number.isFinite));
  }
});

test('chase snaps and orbits around the aircraft, caps elevation and keeps runway clearance', () => {
  const scene = new FlightScene({ mesh: mesh => mesh }, world);
  const jet = { ...aircraft, yaw: Math.PI / 2, forward: { x: 1, y: 0, z: 0 }, up: { x: 0, y: 1, z: 0 } };
  const state = { phase: 'racing', flight: { aircraft: jet } };
  const view = (s = state) => scene.update(s, 0, 'chase').camera;
  const centered = view();
  assert.ok(centered.eye[0] < jet.x && Math.abs(centered.eye[2] - jet.z) < 1e-6, 'tail chase follows the aircraft heading');
  for (const [preset, axis, sign] of [['left', 2, 1], ['right', 2, -1], ['rear', 0, 1]]) {
    scene.setFlightView(preset);
    const camera = view();
    assert.ok((camera.eye[axis] - [jet.x, jet.y, jet.z][axis]) * sign > 20, `${preset} snap moves to the correct side`);
    assert.deepEqual(camera.target, [jet.x, jet.y, jet.z]);
  }
  scene.setFlightView('forward');
  scene.orbitFlightView(230, -10000);
  const high = view();
  assert.ok(high.eye[2] < jet.z - 8 && high.eye[1] > centered.eye[1] + 10);
  assert.ok(normalize(sub(high.eye, high.target))[1] < .95, 'orbit stops short of an overhead singularity');
  scene.orbitFlightView(0, -10000);
  assert.deepEqual(view().eye, high.eye, 'further upward drag at the limit has no effect');
  scene.orbitFlightView(0, 10000);
  const low = view();
  assert.ok(low.eye[1] < jet.y - 8, 'downward orbit is available in the air');
  const runway = { ...state, flight: { aircraft: { ...jet, y: 10.4, onGround: true } } };
  const ground = view(runway);
  assert.ok(ground.eye[1] > runway.flight.aircraft.y + 1.9, 'same orbit stays clear of the runway');
  scene.orbitFlightView(0, -12);
  assert.ok(view(runway).eye[1] > ground.eye[1] + 1, 'dragging up from the runway limit immediately lifts the eye');
  scene.setFlightView('forward');
  assert.deepEqual(view().eye, centered.eye, 'center restores the trailing eye without waiting for smoothing');
  assert.deepEqual(view().target, centered.target);
});

test('World Flight inherits snaps through the three-argument scene update and rebases both camera points', () => {
  const terrain = { attach() {}, draw: () => [], region: { landmarks: [], cities: [] } };
  const worldScene = new WorldFlightScene({ mesh: mesh => mesh }, {
    ...world, runwayStart: { x: 0, z: 0 }, geography: { destination: { name: 'Departure', targetX: 0, targetZ: 0 } },
  }, terrain);
  const state = { phase: 'racing', flight: { aircraft: { ...aircraft, x: 6_145, z: -6_141, up: { x: 0, y: 1, z: 0 } } } };
  worldScene.setFlightView('rear');
  const { camera } = worldScene.update(state, 0, 'cockpit');
  assert.ok(Math.abs(camera.eye[0] - 1) < 1e-5 && Math.abs(camera.eye[2] - 3.89) < 1e-5);
  assert.ok(dot(normalize(sub(camera.target, camera.eye)), [0, 0, -1]) > .99999);
  assert.ok(camera.logarithmicDepth && camera.far > 100_000);
});

test('World Flight and ground views project authored anchors in the same frame as the drawn models', () => {
  const [x, z] = [1_048_651, -1_048_547], origin = [1_048_576, 0, -1_048_576];
  const tile = { mesh: {}, offset: [origin[0], 0, origin[2]], shadow: false };
  const region = { id: 'conus', cities: [], landmarks: [
    { name: 'Space Needle', kind: 'needle', x: x + .125, z: z + 64.5 },
    { name: 'SPACE NEEDLE', kind: 'needle', x: x + 26, z: z + 120 },
    { name: 'Amazon Spheres', kind: 'spheres', x: x - 15, z: z + 200, heading: 1.02 },
    { name: 'Bad coordinate', kind: 'coit', x: NaN, z },
    { name: 'Too far', kind: 'coit', x, z: z + 30000 },
  ], destinations: Array.from({ length: 14 }, (_, i) =>
    ({ id: `place-${i}`, name: `Departure ${i}`, targetX: x + 1800 + i * 300, targetZ: z })) };
  const terrain = { attach() {}, dispose() {}, draw: () => [tile], region, sample: () => 37,
    visualSample: (_x, _z, level) => level === 7 ? 51 : 98 };
  const graphics = { mesh: mesh => mesh };
  const localWorld = { ...world, runwayStart: { x, z }, geography: { destination: { name: 'Departure', targetX: x, targetZ: z } },
    landingSurfaces: [{ ...world.landingSurfaces[0], centerX: x - 80, centerZ: z + 8 }] };
  const scene = new WorldFlightScene(graphics, localWorld, terrain, 'f14');
  const state = { phase: 'racing', flight: { aircraft: { ...aircraft, x, z, up: { x: 0, y: 1, z: 0 } } } };
  for (const mode of ['chase', 'cockpit']) {
    const frame = scene.update(state, 0, mode);
    assert.equal(scene.aircraftId, 'f14');
    assert.equal(frame.landmarkAnchors.length, 12, 'anchor work stays bounded even with more nearby targets');
    assert.ok(frame.landmarkAnchors.every(anchor => anchor.position.every(Number.isFinite)));
    assert.equal(frame.landmarkAnchors.filter(anchor => anchor.name.toLowerCase() === 'space needle').length, 1);
    assert.ok(!frame.landmarkAnchors.some(anchor => ['Bad coordinate', 'Too far'].includes(anchor.name)));
    for (const [name, base] of [['Space Needle', 36], ['Amazon Spheres', 50]]) {
      const { mesh } = scene.scenery.landmarks.find(item => item.location.name === name);
      const draw = frame.draws.find(item => item.mesh === mesh);
      const anchor = frame.landmarkAnchors.find(item => item.name === name);
      assert.equal(draw.model[13], base);
      assert.deepEqual(anchor.position, transform(draw.model, [0, Math.max(...points(mesh).map(p => p[1])), 0]));
      assert.ok(dot(sub(anchor.position, frame.camera.eye), sub(frame.camera.target, frame.camera.eye)) > 0);
    }
    const runway = frame.landmarkAnchors.find(item => item.name === 'Runway');
    assert.deepEqual(runway.position, [-5, 8.045, 37]);
    assert.deepEqual(scene.nearbyLandmarks, ['Space Needle', 'SPACE NEEDLE', 'Amazon Spheres']);
  }

  const scenery = new WorldScenery(graphics, terrain);
  const persistent = { mesh: {}, model: pose([x + 5, 42, z - 7]) };
  const makeFrame = () => ({ draws: [persistent], camera: { eye: [x, 43, z], target: [x, 43, z + 5] } });
  const input = makeFrame(), drive = scenery.decorate(input, x, z, { far: 25000, view: 'ground' });
  assert.equal(drive, input);
  assert.deepEqual(drive.camera.eye, [75, 43, 29]);
  assert.equal(drive.camera.far, 25000);
  assert.equal(drive.camera.logarithmicDepth, true);
  assert.deepEqual([...drive.draws.find(item => item.mesh === persistent.mesh).model.slice(12, 15)], [80, 42, 22]);
  assert.deepEqual([...persistent.model.slice(12, 15)], [x + 5, 42, z - 7], 'decoration must not rebase persistent draw objects');
  assert.deepEqual(scenery.decorate(makeFrame(), x, z).landmarkAnchors, drive.landmarkAnchors);
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
