import assert from 'node:assert/strict';
import test from 'node:test';
import { lookAt, multiply, perspective, pose, transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';
import { SeattleCamera } from '../../src/StuntRacer.Browser/wwwroot/world-drive-camera.js';
import { WorldDriveScene } from '../../src/StuntRacer.Browser/wwwroot/world-drive-scene.js';

const dt = 1 / 60, z = 8338.1, x = 174.57;
const canvas = { width: 1440, height: 857 };
const player = Object.freeze({ x, y: 6, z, yaw: Math.atan2(8.66, 2.27), pitch: 0, roll: 0, speed: 0, steer: 0 });
const way = [1154870285, 6.8, 'levels', [
  [154, 8327], [170.01, 8327.79], [167.69, 8336.27], [163.87, 8348.57], [153, 8351],
]];

test('the Seattle render clears the baked wall on the first chase frame and keeps cockpit and garage intact', () => {
  const city = { id: 'seattle', x, z, featureCount: 1 };
  const terrain = {
    region: { cities: [city] }, visible: [], loaded: new Map(), pending: new Map(), errors: new Map(),
    attach() {}, draw() { return []; }, at: () => ({ level: 8 }), sample: () => 5, visualSample: () => 5,
  };
  const graphics = { canvas, mesh: mesh => mesh, releaseMesh() {}, meshes: new Set() };
  const scene = new WorldDriveScene(graphics, { name: 'Seattle', bounds: {}, streets: [] }, terrain);
  scene.scenery.city = city;
  scene.scenery.buildingData = [way];
  const render = (phase, mode, tick) => {
    const state = { player, phase, tick, impact: 0 };
    const frame = scene.update(state, dt, mode);
    const camera = scene.diagnostics().camera;
    assert.deepEqual(frame.camera.eye, [camera.eye[0], camera.eye[1], camera.eye[2] - 8192]);
    assert.deepEqual(frame.camera.target, [camera.target[0], camera.target[1], camera.target[2] - 8192]);
    return { frame, camera };
  };
  const garage = render('ready', 'cockpit', 0);
  assert.equal(garage.camera.mode, 'orbit');
  assert.equal(garage.camera.clear, true);
  const wall = garage.frame.draws.find(draw => draw.mesh === scene.scenery.cityMesh);
  assert.ok(wall, 'The real mesh must still render on the same frame as the corrected camera.');
  assert.ok(wall.mesh.vertices.some((height, i) => i % 10 === 1 && height === 11.8), 'Use the rendered roof datum.');
  assert.ok(garage.frame.draws.some(draw => draw.mesh === scene.playerBodies.ev6));
  scene.setVehicleStyle('buggy');
  assert.ok(render('ready', 'chase', 0).frame.draws.some(draw => draw.mesh === scene.playerBodies.buggy));

  const chase = render('racing', 'chase', 1).camera;
  assert.equal(chase.obstruction, way[0]);
  assert.equal(chase.clear, true);
  assert.equal(chase.corrected, true);
  assert.ok(chase.requested[0] < 167.69 && chase.eye[0] > 168, 'The submitted eye crosses to the street side of the same wall.');
  assert.ok(Math.hypot(chase.eye[0] - x, chase.eye[2] - z) > 5, 'Do not retract inside the car.');
  const cockpit = render('racing', 'cockpit', 2);
  assert.deepEqual(cockpit.camera.eye, transform(pose([x, player.y, z], player.yaw), [0, .94, -.35]));
  assert.ok(cockpit.frame.draws.some(draw => draw.mesh === scene.cockpitBodies.buggy));
  const reset = render('ready', 'cockpit', 0).camera;
  assert.equal(reset.clear, true);
  assert.ok(Math.hypot(reset.eye[0] - garage.camera.eye[0], reset.eye[2] - garage.camera.eye[2]) < .1);
});

function chaseCamera() {
  return { eye: [0, 9.1, -7.8], target: [0, 6.65, 4.7], up: [0, 1, 0], fov: 65 * Math.PI / 180 };
}

function chase(solver, buildings) {
  return solver.update(chaseCamera(), { ...player, x: 0, z: 0, yaw: 0 }, dt, 'chase', false, 0, canvas, buildings);
}

const flat = { sample: () => 5, visualSample: () => 5 };

test('a thin wall between an outside chase eye and the car forces a lateral fallback and eases back after it clears', () => {
  const solver = new SeattleCamera(flat);
  const behind = [{ id: 71, bottom: 0, top: 16, ring: [[-4, -4.8], [4, -4.8], [4, -4.6], [-4, -4.6]] }];
  const blocked = chase(solver, behind);
  assert.equal(blocked.obstruction, 71);
  assert.equal(blocked.clear, true);
  assert.ok(Math.abs(blocked.eye[0]) > 3 && Math.hypot(blocked.eye[0], blocked.eye[2]) > 5,
    'A wall between two clear endpoints requires moving to the side, not only testing the lens or shrinking onto the bumper.');
  const returning = chase(solver, []);
  assert.equal(returning.obstruction, null);
  assert.ok(Math.abs(returning.eye[0]) < Math.abs(blocked.eye[0]) && Math.abs(returning.eye[0]) > 2,
    'Removing an obstruction releases the view gradually, not in one frame.');
  for (let i = 0; i < 90; i++) chase(solver, []);
  const open = chase(solver, []);
  assert.deepEqual(open.eye, chaseCamera().eye);
  assert.deepEqual(open.target, chaseCamera().target);
  assert.equal(open.corrected, false);
});

test('a concave courtyard or a roof below the actual ray does not spuriously retract the chase camera', () => {
  const solver = new SeattleCamera(flat);
  const courtyard = { id: 18, bottom: 0, top: 16,
    ring: [[-5, -9], [-3, -9], [-3, 3], [3, 3], [3, -9], [5, -9], [5, 5], [-5, 5]] };
  const lowRoof = { id: 19, bottom: 0, top: 6, ring: [[-2, -5], [2, -5], [2, -4], [-2, -4]] };
  const open = chase(solver, [courtyard, lowRoof]);
  assert.equal(open.clear, true);
  assert.deepEqual(open.eye, chaseCamera().eye);
});

test('visible terrain between the car and an above-ground chase eye is not traded for building clipping', () => {
  const ridge = (x, z) => 5 + 7 * Math.max(0, 1 - Math.abs(z + 4) / 1.6) * Math.max(0, 1 - Math.abs(x) / 3);
  const solver = new SeattleCamera({ sample: flat.sample, visualSample: ridge });
  const fixed = chase(solver, []);
  assert.equal(fixed.obstruction, 'terrain', 'Check the rendered surface along the sightline, not just under the lens.');
  assert.equal(fixed.clear, true);
  assert.ok(fixed.eye[1] >= ridge(fixed.eye[0], fixed.eye[2]) + 1.1);
  assert.notDeepEqual(fixed.eye, chaseCamera().eye);
});

test('an off-road player already inside a building still gets an exterior lens where possible', () => {
  const solver = new SeattleCamera(flat);
  const building = { id: 22, bottom: 0, top: 35, ring: [[-6, -15], [6, -15], [6, 15], [-6, 15]] };
  const blocked = chase(solver, [building]);
  assert.equal(blocked.clear, false, 'Moving only the camera cannot expose a car inside solid scenery.');
  assert.ok(Math.abs(blocked.eye[0]) > 6.4, 'Keep the lens outside even when there is no clear view of the car.');
});

test('the full portrait ready orbit fits in the measured toolbar-to-title gap beside the track card', () => {
  const phone = { width: 360, height: 800 }, solver = new SeattleCamera(flat);
  const building = { id: way[0], ring: way[3], bottom: 0, top: 11.8 };
  const local = ([x, y, z]) => [x, y, z - 8192];
  const body = pose(local([player.x, player.y, player.z]), player.yaw);
  for (const orbit of [-5 * Math.PI, 0, 5 * Math.PI]) {
    const view = solver.update(chaseCamera(), player, dt, 'chase', true, orbit, phone, [building]);
    assert.equal(view.clear, true);
    const matrix = multiply(perspective(view.fov, phone.width / phone.height, .12, 25000),
      lookAt(local(view.eye), local(view.target), view.up));
    for (const x of [-1.34, 1.34]) for (const y of [-1, 1.6]) for (const z of [-2.14, 2.14]) {
      const [a, b, c] = transform(body, [x, y, z]);
      const depth = matrix[3] * a + matrix[7] * b + matrix[11] * c + matrix[15];
      const px = (1 + (matrix[0] * a + matrix[4] * b + matrix[8] * c + matrix[12]) / depth) * phone.width / 2;
      const py = (1 - (matrix[1] * a + matrix[5] * b + matrix[9] * c + matrix[13]) / depth) * phone.height / 2;
      assert.ok(px > 6 && px < 154 && py > 78 && py < 165,
        `A vehicle corner at (${px.toFixed(1)}, ${py.toFixed(1)}) must stay clear of the measured phone overlays.`);
    }
  }
});
