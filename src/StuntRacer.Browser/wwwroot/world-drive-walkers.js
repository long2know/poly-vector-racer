import { clamp, cross, dot, normalize, random, sub } from './engine/math.js';
import { appendWalker, makeWalkerRig } from './world-actors.js';
import { seattleSidewalkLift, seattleSidewalks } from './world-ground-roads.js';
import { terrainTileSpan } from './world-terrain.js';

const cellSize = 64, range = 112, visibleLimit = 12, sectionLength = 21;
const bodyMargin = .39, carClearance = 3.35;
const landmarkClearance = { needle: 26, wheel: 36, stadium: 124, ballpark: 150, spheres: 38 };
const palettes = [
  { shirt: [.78, .32, .21], skin: [.76, .53, .36], trousers: [.16, .19, .23] },
  { shirt: [.14, .55, .66], skin: [.53, .34, .23], trousers: [.10, .15, .16] },
  { shirt: [.83, .67, .25], skin: [.85, .65, .49], trousers: [.25, .20, .22] },
];
const cell = n => Math.floor(n / cellSize);
const key = (x, z) => `${x}/${z}`;
const streetKey = (x, z, a, b) => x < a || (x === a && z < b) ? `${x},${z}/${a},${b}` : `${a},${b}/${x},${z}`;
const smooth = (from, to, n) => {
  const t = clamp((n - from) / (to - from), 0, 1);
  return t * t * (3 - 2 * t);
};
const inside = (bounds, x, z) => x >= bounds.minX && x <= bounds.maxX && z >= bounds.minZ && z <= bounds.maxZ;

function addToGrid(grid, item, x0, z0, x1, z1) {
  for (let iz = cell(z0); iz <= cell(z1); iz++) for (let ix = cell(x0); ix <= cell(x1); ix++) {
    const bucket = key(ix, iz);
    if (!grid.has(bucket)) grid.set(bucket, []);
    grid.get(bucket).push(item);
  }
}

function distanceToLine(x, z, a, b, c, d) {
  const dx = c - a, dz = d - b;
  const t = clamp(((x - a) * dx + (z - b) * dz) / (dx * dx + dz * dz || 1), 0, 1);
  return Math.hypot(x - a - dx * t, z - b - dz * t);
}

function onFootprint(x, z, ring) {
  let enclosed = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [a, b] = ring[j], [c, d] = ring[i];
    if (distanceToLine(x, z, a, b, c, d) < 1.1) return true;
    if ((b > z) !== (d > z) && x < a + (z - b) * (c - a) / (d - b)) enclosed = !enclosed;
  }
  return enclosed;
}

function footprintGrid(buildings, bounds) {
  const grid = new Map();
  for (const [, , , ring] of buildings) {
    const xs = ring.map(p => p[0]), zs = ring.map(p => p[1]);
    addToGrid(grid, ring, Math.max(Math.min(...xs) - 1.1, bounds.minX), Math.max(Math.min(...zs) - 1.1, bounds.minZ),
      Math.min(Math.max(...xs) + 1.1, bounds.maxX), Math.min(Math.max(...zs) + 1.1, bounds.maxZ));
  }
  return grid;
}

function indexPaths(world, roads) {
  const connected = new Set(world.streets.map(street => streetKey(...street)));
  const paths = new Map(), roadways = new Map(), bounds = world.bounds;
  let id = 0;
  for (const [way, kind, structure, line] of roads) for (let i = 1; i < line.length; i++) {
    const [x, z] = line[i - 1], [a, b] = line[i];
    const band = structure === 'surface' && seattleSidewalks[kind];
    const width = band ? band.inner : 12;
    addToGrid(roadways, [width, x, z, a, b], Math.max(Math.min(x, a) - width, bounds.minX),
      Math.max(Math.min(z, b) - width, bounds.minZ), Math.min(Math.max(x, a) + width, bounds.maxX),
      Math.min(Math.max(z, b) + width, bounds.maxZ));
    if (!band || !connected.delete(streetKey(x, z, a, b))) continue;
    const length = Math.hypot(a - x, b - z);
    if (length < 6.3) continue;
    const dx = (a - x) / length, dz = (b - z) / length, pieces = Math.ceil(length / sectionLength);
    const piece = length / pieces, offset = band.inner + .46, shift = Math.min(.34, band.outer - .44 - offset);
    for (let part = 0; part < pieces; part++) for (const side of [-1, 1]) {
      const start = part * piece + .65;
      const nx = -dz * side, nz = dx * side;
      const cx = x + dx * (start + (piece - 1.3) / 2) + nx * offset;
      const cz = z + dz * (start + (piece - 1.3) / 2) + nz * offset;
      const variant = random(way ^ Math.imul(i, 53047) ^ Math.imul(part + 1, 8317) ^ side);
      const path = {
        id: ++id, x: x + dx * start + nx * offset, z: z + dz * start + nz * offset, cx, cz,
        dx, dz, nx, nz, length: piece - 1.3, heading: Math.atan2(dx, dz), shift,
        start: variant(), speed: 1.05 + variant() * .52, scale: .88 + variant() * .16,
        palette: Math.floor(variant() * palettes.length),
      };
      addToGrid(paths, path, cx, cz, cx, cz);
    }
  }
  return { paths, roadways };
}

function sidewalkY(terrain, tile, x, z) {
  if (terrain.visualTileAt(x, z, 8) !== tile) return null;
  // Match the visible terrain cell, but leave an entire cell's shoreline margin.
  const span = terrainTileSpan(tile.level), step = span / 32;
  const u = (x - tile.x * span) / step, v = (z - tile.z * span) / step;
  const i = Math.floor(u), j = Math.floor(v), a = j * 33 + i, b = a + 33;
  const h = tile.heights, wet = tile.water;
  if (wet[a] || wet[a + 1] || wet[b] || wet[b + 1]) return null;
  const slope = u - i <= v - j ? Math.hypot(h[b + 1] - h[b], h[b] - h[a])
    : Math.hypot(h[a + 1] - h[a], h[b + 1] - h[a + 1]);
  if (!(slope <= step * .42)) return null;
  const y = terrain.visualSample(x, z, 8);
  return Number.isFinite(y) && y >= 0 ? y + seattleSidewalkLift : null;
}

function pointOnPath(path, along, lateral = 0) {
  return [path.x + path.dx * along + path.nx * lateral, path.z + path.dz * along + path.nz * lateral];
}

function walkingSpan(path, supported) {
  // Test the whole occupied band (including the outward step), not just the initial pose.
  const steps = Math.ceil(path.length / 1.1);
  let from = 0, best = null;
  for (let i = 0; i <= steps; i++) {
    const along = i * path.length / steps;
    if (![0, -bodyMargin, path.shift + bodyMargin].every(lateral => supported(...pointOnPath(path, along, lateral)))) {
      from = (i + 1) * path.length / steps;
      continue;
    }
    const length = along - from;
    if (length >= 5 && (!best || length > best.length)) best = { from, length };
  }
  return best;
}

function walkerPose(path, span, time, car) {
  const cycle = path.start + time * path.speed / (Math.PI * span.length);
  const turn = cycle * Math.PI * 2, direction = Math.sin(turn);
  const fraction = (1 - Math.cos(turn)) / 2;
  const along = span.from + span.length * fraction;
  const [baseX, baseZ] = pointOnPath(path, along);
  const dx = car.x - baseX, dz = car.z - baseZ;
  const yielding = (1 - smooth(-.6, 0, dx * path.nx + dz * path.nz)) *
    (1 - smooth(3.5, 13, Math.hypot(dx, dz)));
  const [x, z] = pointOnPath(path, along, path.shift * yielding);
  const distance = Math.hypot(x - car.x, z - car.z);
  if (distance <= carClearance || distance > range) return null;
  const leg = Math.floor(cycle * 2);
  const passed = leg + (leg % 2 ? 1 - fraction : fraction);
  return {
    id: path.id, x, z, yaw: path.heading + Math.PI * (1 - smooth(-.16, .16, direction)),
    phase: passed * span.length * (2 * Math.PI / (1.25 * path.scale)),
    stride: smooth(0, .45, Math.abs(direction)), scale: path.scale,
    palette: path.palette, distance, yielding, opacity: smooth(carClearance, 5.25, distance),
  };
}

function inView(camera, aspect) {
  const forward = normalize(sub(camera.target, camera.eye));
  const right = normalize(cross(camera.up, forward)), up = cross(forward, right);
  const fov = Math.tan(camera.fov / 2);
  return ({ x, y, z }) => {
    const delta = sub([x, y + .85, z], camera.eye), depth = dot(delta, forward);
    return depth > .12 && Math.abs(dot(delta, up)) < fov * depth + .85 &&
      Math.abs(dot(delta, right)) < fov * depth * aspect + .45;
  };
}

export class SeattleWalkers {
  rigs = [];
  poses = [];
  time = 0;
  confirmed = new WeakMap();

  constructor(graphics, world, terrain, roads) {
    this.graphics = graphics;
    this.terrain = terrain;
    this.bounds = world.bounds;
    this.landmarks = terrain.region.landmarks?.filter(site => Object.hasOwn(landmarkClearance, site.kind)) ?? [];
    this.needsBuildings = terrain.region.cities?.some(city => city.id === 'seattle');
    Object.assign(this, indexPaths(world, roads));
  }

  append(frame, state, scenery) {
    this.poses = [];
    if (!state.drive) return;
    this.time = state.phase === 'ready' ? 0 : state.time;
    if (!Number.isFinite(this.time) || (this.needsBuildings && !scenery.cityMesh)) return;
    const buildings = scenery.buildingData ?? null;
    if (!this.footprints || this.buildings !== buildings) {
      this.footprints = footprintGrid(buildings ?? [], this.bounds);
      this.buildings = buildings;
      this.confirmed = new WeakMap();
    }
    const car = state.player, candidates = [], radius = range + sectionLength / 2;
    const canvas = this.graphics.canvas;
    const visible = inView(frame.camera, canvas ? canvas.width / Math.max(1, canvas.height) : 1);
    for (let iz = cell(car.z - radius); iz <= cell(car.z + radius); iz++)
      for (let ix = cell(car.x - radius); ix <= cell(car.x + radius); ix++)
        for (const path of this.paths.get(key(ix, iz)) ?? []) {
          if (Math.hypot(path.cx - car.x, path.cz - car.z) > radius) continue;
          const tile = this.terrain.visualTileAt(path.cx, path.cz, 8);
          const support = tile && this.terrain.visibleSidewalksAt(tile);
          if (!support) continue;
          let known = this.confirmed.get(path);
          if (known?.support !== support) {
            known = { support, span: walkingSpan(path, (x, z) => this.supported(tile, x, z)) };
            this.confirmed.set(path, known);
          }
          if (!known.span) continue;
          const walker = walkerPose(path, known.span, this.time, car);
          if (!walker || !this.terrain.hasVisibleSidewalk(walker.x, walker.z)) continue;
          walker.y = sidewalkY(this.terrain, tile, walker.x, walker.z);
          if (walker.y !== null && visible(walker) && this.unobstructed(walker.x, walker.z)) candidates.push(walker);
        }
    this.poses = candidates.sort((a, b) => a.distance - b.distance || a.id - b.id).slice(0, visibleLimit);
    for (const walker of this.poses) {
      const index = walker.palette;
      this.rigs[index] ??= makeWalkerRig(this.graphics, palettes[index]);
      appendWalker(frame.draws, this.rigs[index], walker, { grounded: true, shadow: false, opacity: walker.opacity });
    }
  }

  supported(tile, x, z) {
    return inside(this.bounds, x, z) && this.terrain.hasVisibleSidewalk(x, z)
      && sidewalkY(this.terrain, tile, x, z) !== null && this.unobstructed(x, z);
  }

  unobstructed(x, z) {
    const bucket = key(cell(x), cell(z));
    // OSM strips can intersect geometrically without sharing a junction node.
    return !this.roadways.get(bucket)?.some(([width, ...line]) => distanceToLine(x, z, ...line) < width)
      && !this.footprints.get(bucket)?.some(ring => onFootprint(x, z, ring))
      && !this.landmarks.some(site => Math.hypot(x - site.x, z - site.z) < landmarkClearance[site.kind]);
  }

  diagnostics() {
    return { time: this.time, visible: this.poses.length, poses: this.poses.map(walker => ({ ...walker })) };
  }

  dispose() {
    for (const rig of this.rigs) if (rig) {
      this.graphics.releaseMesh(rig.body);
      this.graphics.releaseMesh(rig.limb);
    }
    this.rigs = [];
    this.poses = [];
  }
}
