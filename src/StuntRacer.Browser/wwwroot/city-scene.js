import { Builder, RaceScene } from './scene.js';
import { add, lerp3, multiply, normalize, pose, scale, sub } from './engine/math.js';
import { appendWalker, makeWalkerRig } from './world-actors.js';

const asphalt = [0.105, 0.125, 0.14], pavement = [0.43, 0.46, 0.43];
const cream = [0.85, 0.86, 0.75], charcoal = [0.06, 0.08, 0.09];
const palette = {
  'brick-warehouse': [0.43, 0.25, 0.18], 'glass-blue': [0.18, 0.36, 0.43],
  'sandstone-tower': [0.62, 0.56, 0.41], terracotta: [0.52, 0.35, 0.28],
  civic: [0.65, 0.66, 0.56], industrial: [0.39, 0.45, 0.44],
};
const point = p => [p.x, p.y, p.z];

function rectangle(mesh, x0, x1, z0, z1, y, colour, material = 0) {
  if (x1 <= x0 || z1 <= z0) return;
  mesh.quad([x0, y, z0], [x0, y, z1], [x1, y, z1], [x1, y, z0], colour, material);
}

function intersections(world) {
  const result = [], seen = new Set();
  for (let i = 0; i < world.roads.length; i++) for (let j = i + 1; j < world.roads.length; j++) {
    const first = world.roads[i], second = world.roads[j];
    for (let a = 0; a < first.points.length - 1; a++) for (let b = 0; b < second.points.length - 1; b++) {
      const p = first.points[a], q = first.points[a + 1], r = second.points[b], s = second.points[b + 1];
      const dx = q.x - p.x, dz = q.z - p.z, ex = s.x - r.x, ez = s.z - r.z;
      const denominator = dx * ez - dz * ex;
      if (Math.abs(denominator) < 1e-6) continue;
      const t = ((r.x - p.x) * ez - (r.z - p.z) * ex) / denominator;
      const u = ((r.x - p.x) * dz - (r.z - p.z) * dx) / denominator;
      if (t < 0 || t > 1 || u < 0 || u > 1) continue;
      const x = p.x + dx * t, z = p.z + dz * t, y = p.y + (q.y - p.y) * t;
      if (Math.abs(y - r.y - (s.y - r.y) * u) > 0.25) continue;
      const key = `${Math.round(x * 10)},${Math.round(z * 10)}`;
      if (seen.has(key)) continue;
      seen.add(key);
      result.push({ x, y, z, width: Math.max(first.width, second.width), first: first.id, second: second.id });
    }
  }
  return result;
}

function streetStrip(mesh, a, b, offset, width, height, colour, material = 0) {
  const delta = sub(b, a), length = Math.hypot(...delta);
  if (length < 0.01) return;
  const heading = Math.atan2(delta[0], delta[2]);
  const pitch = Math.atan2(delta[1], Math.hypot(delta[0], delta[2]));
  mesh.box([offset, height / 2, 0], [width, height, length], colour, material,
    pose(lerp3(a, b, 0.5), heading, pitch));
}

function makeBuilding(mesh, building) {
  const { centerX: x, centerZ: z, width, depth, height, baseY, yaw, style } = building;
  const frame = pose([x, baseY, z], yaw);
  const colour = palette[style] ?? palette.industrial;
  mesh.box([0, height / 2, 0], [width, height, depth], colour, 2, frame);
  mesh.box([0, height + 0.5, 0], [width + 1.5, 1, depth + 1.5], charcoal, 2, frame);
  const rows = Math.min(13, Math.max(1, Math.floor(height / 8)));
  const columns = Math.min(6, Math.max(2, Math.floor(width / 12)));
  for (const side of [-1, 1]) for (let row = 0; row < rows; row++) for (let column = 0; column < columns; column++) {
    const wx = (column - (columns - 1) / 2) * width / columns;
    const wy = 4 + row * (height - 6) / rows;
    mesh.box([wx, wy, side * (depth / 2 + 0.035)], [width / columns * 0.57, 2.8, 0.06],
      (row + column) % 7 === 0 ? [0.62, 0.65, 0.45] : [0.09, 0.19, 0.22], 2, frame);
  }
  for (const side of [-1, 1]) {
    mesh.box([side * (width / 2 + 0.025), height * 0.48, 0],
      [0.05, height * 0.88, depth * 0.48], [0.12, 0.23, 0.26], 2, frame);
    if (style === 'brick-warehouse')
      mesh.box([side * width * 0.2, 2, -depth / 2 - 0.04], [width * 0.24, 4, 0.08], charcoal, 2, frame);
  }
  if (height > 75) mesh.beam([x, baseY + height, z], [x, baseY + height + 12, z], 0.25, cream, 2, 4);
}

function makeCity(world) {
  const land = new Builder(), props = new Builder(), road = new Builder(), junctions = intersections(world);
  const bounds = world.bounds;
  let tiles = [[bounds.minX - 1600, bounds.maxX + 1600, bounds.minZ - 1600, bounds.maxZ + 1600]];
  for (const area of world.areas.filter(area => area.kind === 'water')) {
    const x0 = Math.min(...area.points.map(p => p.x)), x1 = Math.max(...area.points.map(p => p.x));
    const z0 = Math.min(...area.points.map(p => p.z)), z1 = Math.max(...area.points.map(p => p.z));
    tiles = tiles.flatMap(([a, b, c, d]) => {
      if (b <= x0 || a >= x1 || d <= z0 || c >= z1) return [[a, b, c, d]];
      return [[a, Math.max(a, x0), c, d], [Math.min(b, x1), b, c, d],
        [Math.max(a, x0), Math.min(b, x1), c, Math.max(c, z0)],
        [Math.max(a, x0), Math.min(b, x1), Math.min(d, z1), d]].filter(([l, r, n, s]) => r > l && s > n);
    });
  }
  for (const [x0, x1, z0, z1] of tiles) rectangle(land, x0, x1, z0, z1, world.groundY, [0.24, 0.33, 0.21], 3);
  for (const area of world.areas) {
    const colour = area.kind === 'water' ? [0.055, 0.27, 0.34] : area.kind === 'park' ? [0.22, 0.4, 0.19]
      : area.kind === 'industrial' ? [0.24, 0.27, 0.26] : pavement;
    land.quad(...area.points.map(point).reverse(), colour, area.kind === 'water' ? 5 : area.kind === 'park' ? 3 : 0);
  }
  for (const street of world.roads) for (let i = 0; i < street.points.length - 1; i++) {
    const a = point(street.points[i]), b = point(street.points[i + 1]);
    const delta = sub(b, a), length = Math.hypot(delta[0], delta[2]);
    const right = normalize([delta[2], 0, -delta[0]]);
    road.quad(add(a, scale(right, -street.width / 2)), add(b, scale(right, -street.width / 2)),
      add(b, scale(right, street.width / 2)), add(a, scale(right, street.width / 2)), asphalt, 1);
    const excluded = junctions.filter(j => j.first === street.id || j.second === street.id).map(j => ({
      s: ((j.x - a[0]) * delta[0] + (j.z - a[2]) * delta[2]) / length, radius: j.width / 2 + 5,
    })).filter(j => j.s >= 0 && j.s <= length);
    for (let s = 0; s < length; s += 4) {
      if (excluded.some(j => Math.abs(s + 2 - j.s) < j.radius)) continue;
      const p = lerp3(a, b, s / length), q = lerp3(a, b, Math.min(1, (s + 4) / length));
      for (const side of [-1, 1]) {
        streetStrip(road, add(p, [0, 0.012, 0]), add(q, [0, 0.012, 0]), side * 0.18, 0.12, 0.008, [0.96, 0.65, 0.12]);
        if (Math.floor(s / 4) % 3 === 0)
          streetStrip(road, add(p, [0, 0.012, 0]), add(q, [0, 0.012, 0]), side * street.width / 4, 0.12, 0.008, cream);
        if (street.sidewalkWidth > 0)
          streetStrip(road, p, q, side * (street.width + street.sidewalkWidth) / 2,
            street.sidewalkWidth, street.sidewalkHeight, pavement);
      }
    }
    if (!street.bridge) for (let s = 30; s < length - 20; s += 85) {
      if (excluded.some(j => Math.abs(s - j.s) < j.radius + 5)) continue;
      const p = add(lerp3(a, b, s / length), scale(right, street.width / 2 + 2.5));
      props.beam(p, add(p, [0, 7.5, 0]), 0.085, charcoal, 2, 4);
      props.beam(add(p, [0, 7.5, 0]), add(add(p, [0, 7.5, 0]), scale(right, -2.5)), 0.07, charcoal, 2, 4);
      props.box(add(add(p, [0, 7.5, 0]), scale(right, -2.5)), [0.7, 0.08, 0.45], [0.83, 0.9, 0.69], 4);
    }
  }
  for (const junction of junctions) for (const direction of [0, Math.PI / 2]) {
    const frame = pose([junction.x, junction.y + 0.016, junction.z], direction);
    for (const side of [-1, 1]) for (let stripe = -4; stripe <= 4; stripe++)
      road.box([stripe * 2.1, 0, side * (junction.width / 2 + 2.8)],
        [1.15, 0.015, 3.2], cream, 0, frame);
  }
  for (const walkway of world.walkways) for (let i = 0; i < walkway.points.length - 1; i++)
    streetStrip(land, point(walkway.points[i]), point(walkway.points[i + 1]), 0, walkway.width, 0.025, [0.62, 0.57, 0.43]);
  for (const building of world.buildings) makeBuilding(props, building);
  for (const structure of world.structures) {
    const { centerX: x, centerZ: z, baseY, width, depth, height, yaw, style } = structure;
    props.box([0, height / 2, 0], [width, height, depth],
      style.startsWith('bridge') ? [0.57, 0.26, 0.13] : [0.57, 0.57, 0.43], 2, pose([x, baseY, z], yaw));
    if (style === 'clocktower') {
      props.box([x, baseY + height - 4, z], [width + 3, 5, depth + 3], cream);
      props.box([x, baseY + height - 4, z - depth / 2 - 1.6], [3.3, 3.3, 0.1], charcoal);
    }
    if (style === 'crane')
      props.beam([x, baseY + height, z - 28], [x, baseY + height, z + 38], 0.8, [0.9, 0.55, 0.1], 2, 4);
  }
  const towers = world.structures.filter(s => s.style === 'bridge-tower');
  const bridge = world.roads.find(r => r.bridge);
  if (towers.length >= 4 && bridge) {
    const minX = Math.min(...towers.map(t => t.centerX)), maxX = Math.max(...towers.map(t => t.centerX));
    const minZ = Math.min(...towers.map(t => t.centerZ)), maxZ = Math.max(...towers.map(t => t.centerZ));
    const center = (minX + maxX) / 2, radius = (maxX - minX) / 2;
    const deck = Math.max(...bridge.points.map(p => p.y));
    const top = Math.min(...towers.map(t => t.baseY + t.height)) - 2;
    for (const x of [minX, maxX])
      props.beam([x, top, minZ], [x, top, maxZ], 0.7, [0.6, 0.26, 0.12], 2);
    for (const z of [minZ, maxZ]) for (let i = 0; i < 16; i++) {
      const x = minX + i * (maxX - minX) / 16, next = minX + (i + 1) * (maxX - minX) / 16;
      const y = deck + 5 + (top - deck - 5) * ((x - center) / radius) ** 2;
      const ny = deck + 5 + (top - deck - 5) * ((next - center) / radius) ** 2;
      props.beam([x, y, z], [next, ny, z], 0.16, [0.64, 0.32, 0.18], 2, 4);
      props.beam([x, deck, z], [x, y, z], 0.065, [0.45, 0.43, 0.32], 2, 4);
    }
  }
  return { land, props, road };
}

export class CityScene extends RaceScene {
  constructor(graphics, track, world) {
    super(graphics, track, makeCity(world));
    this.world = world;
    this.traffic = this.trafficBodies;
    this.walkerRig = makeWalkerRig(graphics);
  }

  update(state, dt, mode, compatibility) {
    const frame = super.update(state.trial ? { ...state, trial: null } : state, dt, mode, compatibility);
    frame.camera.far = 6000;
    frame.camera.fogDistance = 2700;
    frame.camera.logarithmicDepth = true;
    const activity = state.city;
    for (let index = 0; index < activity.traffic.length; index++) {
      const car = activity.traffic[index].car;
      const matrix = pose([car.x, car.y, car.z], car.yaw, car.pitch, car.roll);
      frame.draws.push({ mesh: this.traffic[index % this.traffic.length], model: matrix });
      for (const [x, z] of [[-1.06, 1.3], [1.06, 1.3], [-1.06, -1.22], [1.06, -1.22]])
        frame.draws.push({ mesh: this.wheel,
          model: multiply(matrix, pose([x, -0.48, z], 0, -car.speed * state.time / 0.52)) });
    }
    for (const walker of activity.pedestrians) appendWalker(frame.draws, this.walkerRig, walker);
    const next = activity.checkpoints[activity.nextCheckpointIndex];
    if (next && activity.success === null) frame.draws.push({ mesh: this.checkpointGate, shadow: false,
      model: pose([next.x, next.y, next.z], next.heading, 0, 0, [next.width, 1, 1]) });
    return frame;
  }

  mapPoint(x, z) {
    const b = this.world.bounds, span = Math.max(b.maxX - b.minX, b.maxZ - b.minZ);
    return [12 + (x - (b.minX + b.maxX - span) / 2) / span * 136,
      12 + (z - (b.minZ + b.maxZ - span) / 2) / span * 136];
  }

  mapRoadPath() {
    return this.world.roads.map(road => road.points.map((p, index) => {
      const [x, z] = this.mapPoint(p.x, p.z);
      return `${index ? 'L' : 'M'}${x.toFixed(2)} ${z.toFixed(2)}`;
    }).join(' ')).join(' ');
  }
}
