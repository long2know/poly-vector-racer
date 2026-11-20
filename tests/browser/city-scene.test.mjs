import assert from 'node:assert/strict';
import test from 'node:test';
import { CityScene } from '../../src/StuntRacer.Browser/wwwroot/city-scene.js';

const p = (x, y, z) => ({ x, y, z });
const road = { id: 'bridge', name: 'Bridge', district: 'Riverfront', width: 12, sidewalkWidth: 0, sidewalkHeight: 0,
  bridge: true, points: [p(-80, .04, 0), p(10, .04, 0), p(20, 8, 0), p(40, 8, 0), p(55, .04, 0), p(90, .04, 0)] };
const track = { name: 'City guide', length: 300, width: 12,
  points: [{ s: 0, ...p(-80, .04, 0), heading: Math.PI / 2, bank: 0, width: 12, road: true },
    { s: 100, ...p(90, .04, 0), heading: Math.PI, bank: 0, width: 12, road: true },
    { s: 200, ...p(0, .04, 80), heading: -Math.PI / 2, bank: 0, width: 12, road: true }] };
const world = { name: 'City fixture', bounds: { minX: -100, maxX: 100, minZ: -100, maxZ: 100 }, groundY: 0,
  roads: [road], buildings: [{ id: 'tower', name: 'Tower', centerX: -40, centerZ: 40, width: 20, depth: 24,
    height: 40, baseY: 0, yaw: .3, style: 'glass-blue' }],
  areas: [{ id: 'river', name: 'River', kind: 'water', points: [p(20, -1.5, -100), p(40, -1.5, -100), p(40, -1.5, 100), p(20, -1.5, 100)] }],
  structures: [], walkways: [], routes: [] };
const car = { x: -80, y: 1.04, z: 0, yaw: Math.PI / 2, pitch: 0, roll: 0, speed: 10, steer: 0,
  suspension: [.5, .5, .5, .5], boost: 1, damage: 0 };
const state = { player: car, phase: 'racing', impact: 0, time: 4, lapTime: 4,
  city: { traffic: [{ id: 'traffic', car: { ...car, x: -30 } }],
    pedestrians: [{ id: 'walker', x: -50, y: .18, z: 8, yaw: 0, phase: 1 }],
    checkpoints: [{ x: 25, y: 8, z: 0, heading: Math.PI / 2, width: 12 }], nextCheckpointIndex: 0, success: null } };

test('city graphics retain canonical elevated road vertices and leave actual water holes in the ground', () => {
  const scene = new CityScene({ mesh: geometry => geometry }, track, world);
  assert.equal(new Set(scene.traffic.map(mesh => JSON.stringify(mesh.vertices.filter((_, i) => i % 10 < 3)))).size, 5,
    'city traffic must contain five distinct body silhouettes');
  const vertices = scene.road.vertices;
  assert.ok(vertices.some((x, i) => i % 10 === 0 && x === 20 && vertices[i + 1] === 8 && Math.abs(vertices[i + 2]) === 6));
  const land = scene.land.vertices;
  const covers = (x, z) => {
    for (let i = 0; i < land.length; i += 30) {
      if (land[i + 9] !== 3 || land[i + 1] !== 0) continue;
      const points = [0, 10, 20].map(j => [land[i + j], land[i + j + 2]]);
      const signs = points.map((a, j) => {
        const b = points[(j + 1) % 3];
        return (b[0] - a[0]) * (z - a[1]) - (b[1] - a[1]) * (x - a[0]);
      });
      if (signs.every(s => s >= -1e-8) || signs.every(s => s <= 1e-8)) return true;
    }
    return false;
  };
  assert.equal(covers(30, 40), false, 'the river must not be hidden underneath an opaque ground rectangle');
  assert.equal(covers(-90, 40), true);
  assert.match(scene.mapRoadPath(), /^M/);
});

test('city cars, walkers and gates use authoritative world poses rather than a canned scenery animation', () => {
  const scene = new CityScene({ mesh: geometry => geometry }, track, world);
  const frame = scene.update(state, 1 / 60, 'chase');
  const traffic = frame.draws.find(draw => draw.mesh === scene.traffic[0] && draw.model?.[12] === -30);
  assert.ok(traffic);
  const walker = frame.draws.find(draw => draw.mesh === scene.walkerRig.body);
  assert.deepEqual([...walker.model.slice(12, 15)], [-50, .18, 8].map(Math.fround));
  const gate = frame.draws.find(draw => draw.mesh === scene.checkpointGate);
  assert.deepEqual([...gate.model.slice(12, 15)], [25, 8, 0]);
  const next = scene.update({ ...state, city: { ...state.city,
    pedestrians: [{ ...state.city.pedestrians[0], x: -47 }] } }, 1 / 60, 'chase');
  assert.equal(next.draws.find(draw => draw.mesh === scene.walkerRig.body).model[12], -47);
  assert.equal(frame.camera.logarithmicDepth, true);
});
