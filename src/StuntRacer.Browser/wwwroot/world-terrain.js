import { Builder } from './scene.js';

const side = 33, stride = side - 1, numSamples = side * side;
const spacing = Array.from({ length: 9 }, (_, i) => 8192 >> i);
const within = (box, x, z) => x >= box[0] && z >= box[1] && x < box[2] && z < box[3];
const covered = (region, level, x, z) => region.coverage[level] &&
  (within(region.coverage[level], x, z) || region.patches?.[level]?.some(box => within(box, x, z)));
const keyOf = (level, x, z) => `${level}/${x}/${z}`;
const spanAt = level => spacing[level] * stride;
const screenBand = [Infinity, 290000, 137000, 72000, 43000, 24000, 13000, 6800, 3800];

export async function fetchWorldJson(url, limit, signal) {
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`World Flight data returned HTTP ${response.status}.`);
  const value = await response.text();
  if (value.length > limit) throw new Error('World Flight data exceeded its size limit.');
  return JSON.parse(value);
}

export async function readWorldCatalog(path) {
  const root = new URL('.', import.meta.url), url = new URL(path, root);
  if (root.origin !== url.origin || !url.pathname.startsWith(root.pathname))
    throw new Error('The World Flight catalog must remain inside this game mount.');
  const data = await fetchWorldJson(url, 65536);
  if (data.kind !== 'world-flight-terrain' || data.schemaVersion !== 1 || data.side !== side ||
      typeof data.version !== 'string' || !/^[a-f0-9]{20}$/.test(data.version) ||
      !Array.isArray(data.spacing) || String(data.spacing) !== String(spacing) ||
      !Array.isArray(data.regions) || data.regions.length !== 3 ||
      !['conus', 'alaska', 'hawaii'].every(id => data.regions.some(region => region.id === id)))
    throw new Error('The World Flight terrain catalog is invalid.');
  for (const region of data.regions) {
    const validBox = b => Array.isArray(b) && b.length === 4 && b.every(n => Number.isSafeInteger(n) && Math.abs(n) <= 4096) &&
      b[2] > b[0] && b[3] > b[1];
    if (!Array.isArray(region.coverage) || region.coverage.length < 3 || region.coverage.length > spacing.length ||
        !region.coverage.every(validBox) ||
        (region.patches && (!Array.isArray(region.patches) || region.patches.length !== region.coverage.length ||
          region.patches.some(boxes => !Array.isArray(boxes) || boxes.length > 8 || !boxes.every(validBox)))) ||
        !Array.isArray(region.destinations) || !region.destinations.length ||
        region.destinations.some(d => typeof d.id !== 'string' || !/^[a-z-]+$/.test(d.id) ||
          typeof d.name !== 'string' || ![d.x, d.z, d.altitude, d.heading, d.targetX, d.targetZ].every(Number.isFinite)))
      throw new Error('World Flight has invalid coverage or departures.');
    region.cities ??= region.buildings ? [{ id: 'seattle', ...region.buildings }] : [];
    if (!Array.isArray(region.cities) || region.cities.length > 2 || region.cities.some(city =>
      !['seattle', 'san-francisco'].includes(city.id) || ![city.x, city.z].every(Number.isFinite) ||
      !Number.isInteger(city.featureCount) || city.featureCount < 1 || city.featureCount > 30000))
      throw new Error('World Flight has invalid building areas.');
  }
  return { ...data, tilesUrl: new URL(`${data.version}/`, url) };
}

export function decodeTile(buffer, level, x, z) {
  if (!Number.isInteger(level) || !Number.isInteger(x) || !Number.isInteger(z) || !spanAt(level) ||
      buffer.byteLength !== 16 + numSamples * 3)
    throw new Error('World Flight elevation tile has invalid dimensions.');
  const view = new DataView(buffer);
  if (view.getUint32(0, false) !== 0x57465431 || view.getInt32(4, true) !== level ||
      view.getInt32(8, true) !== x || view.getInt32(12, true) !== z)
    throw new Error('World Flight elevation tile has invalid coordinates.');
  const heights = new Int16Array(numSamples), water = new Uint8Array(buffer, 16 + numSamples * 2);
  for (let i = 0; i < heights.length; i++) {
    heights[i] = view.getInt16(16 + i * 2, true);
    if (heights[i] < -12000 || heights[i] > 10000 || water[i] > 2)
      throw new Error('World Flight elevation tile has invalid terrain samples.');
  }
  return { level, x, z, heights, water, buffer };
}

function hit(tile, tx, tz) {
  const step = spacing[tile.level], span = spanAt(tile.level);
  const u = Math.max(0, Math.min(stride, (tx - span * tile.x) / step));
  const v = Math.max(0, Math.min(stride, (tz - span * tile.z) / step));
  const i = Math.min(stride - 1, Math.floor(u)), j = Math.min(stride - 1, Math.floor(v));
  const h = tile.heights, a = j * side + i, b = a + side, x = u - i, z = v - j;
  return x <= z ? (1 - z) * h[a] + (z - x) * h[b] + x * h[b + 1]
    : (1 - x) * h[a] + z * h[b + 1] + (x - z) * h[a + 1];
}

function colourAt(a, b, c, step, water) {
  if (water >= 2) return [[0.065, 0.235, 0.29], 5];
  if (water === 1) return [[0.15, 0.26, 0.2], 0];
  const altitude = (a[1] + b[1] + c[1]) / 3;
  const slope = (Math.max(a[1], b[1], c[1]) - Math.min(a[1], b[1], c[1])) / step;
  if (altitude > 2100 + Math.min(850, slope * 660)) return [[0.8, 0.865, 0.88], 0];
  if (altitude > 1720 || slope > 0.6) return [[0.355, 0.36, 0.325], 0];
  if (altitude > 600) return [[0.163, 0.28, 0.195], 0];
  return [[0.23, 0.355, 0.225], 0];
}

export function makeElevationMesh(tile) {
  const mesh = new Builder(), step = spacing[tile.level];
  const at = (x, z) => [x * step, tile.heights[z * side + x], z * step];
  for (let z = 0; z < stride; z++) for (let x = 0; x < stride; x++) {
    const corners = [[x, z], [x, z + 1], [x + 1, z + 1], [x + 1, z]];
    const [a, b, c, d] = corners.map(([i, j]) => at(i, j));
    const wet = corners.map(([i, j]) => tile.water[j * side + i] !== 0 ? 1 : 0);
    const first = colourAt(a, b, c, step, wet[0] + wet[1] + wet[2]);
    const last = colourAt(a, c, d, step, wet[0] + wet[2] + wet[3]);
    mesh.triangle(a, b, c, ...first);
    mesh.triangle(a, c, d, ...last);
  }
  return mesh;
}

export function makeElevationSeam(tile, edge, neighbor) {
  const mesh = new Builder(), step = spacing[tile.level];
  const span = spanAt(tile.level);
  for (let i = 0; i < stride; i++) {
    const along = n => edge === 0 ? [0, n] : edge === 1 ? [n, stride] : edge === 2 ? [stride, n] : [n, 0];
    const [x0, z0] = along(i), [x1, z1] = along(i + 1);
    const i0 = z0 * side + x0, i1 = z1 * side + x1;
    const a = [x0 * step, tile.heights[i0], z0 * step], b = [x1 * step, tile.heights[i1], z1 * step];
    const join = p => [p[0], hit(neighbor, p[0] + tile.x * span, p[2] + tile.z * span), p[2]];
    const c = join(a), d = join(b);
    const wet = tile.water[i0] && tile.water[i1] ? 3 : 0;
    const style = colourAt(a, a, b, step, wet);
    if (a[1] !== c[1]) mesh.triangle(a, c, d, ...style);
    if (b[1] !== d[1]) mesh.triangle(a, d, b, ...style);
  }
  return mesh;
}

const encode = tile => btoa(String.fromCharCode(...new Uint8Array(tile.buffer)));

export class WorldTerrain {
  constructor(catalog, region, bridge) {
    this.catalog = catalog;
    this.region = region;
    this.bridge = bridge;
    this.loaded = new Map();
    this.pending = new Map();
    this.errors = new Map();
    this.simulation = new Set();
    this.visible = [];
    this.maxTiles = 512;
    this.epoch = 0;
  }

  async fetchTile(level, x, z, signal) {
    const url = new URL(`tiles/${this.region.id}/${level}/${x}/${z}.bin`, this.catalog.tilesUrl);
    const response = await fetch(url, { signal });
    if (!response.ok) throw new Error(`Terrain tile ${level}/${x}/${z} returned HTTP ${response.status}.`);
    const tile = decodeTile(await response.arrayBuffer(), level, x, z);
    const key = keyOf(level, x, z);
    this.loaded.set(key, tile);
    return tile;
  }

  async seed(x, z) {
    let level = this.region.coverage.length - 1;
    while (level > 0 && !covered(this.region, level, Math.floor(x / spanAt(level)), Math.floor(z / spanAt(level))))
      level--;
    const seeds = [];
    for (const l of new Set([0, level])) {
      const span = spanAt(l), ix = Math.floor(x / span), iz = Math.floor(z / span);
      const radius = l === 0 ? 1 : 0;
      for (let dx = -radius; dx <= radius; dx++) for (let dz = -radius; dz <= radius; dz++)
        if (covered(this.region, l, ix + dx, iz + dz))
          seeds.push(this.fetchTile(l, ix + dx, iz + dz));
    }
    if (seeds.length === 0) throw new Error('The World Flight departure is outside the elevation catalog.');
    const tiles = await Promise.all(seeds);
    for (const tile of tiles) this.simulation.add(keyOf(tile.level, tile.x, tile.z));
    return tiles.map(encode).join(';');
  }

  at(x, z) {
    for (let level = this.region.coverage.length - 1; level >= 0; level--) {
      const span = spanAt(level), tile = this.loaded.get(keyOf(level, Math.floor(x / span), Math.floor(z / span)));
      if (tile) return tile;
    }
    return null;
  }

  sample(x, z) {
    const tile = this.at(x, z);
    return tile ? hit(tile, x, z) : null;
  }

  isWater(x, z) {
    const tile = this.at(x, z);
    if (!tile) return false;
    const span = spanAt(tile.level), step = spacing[tile.level];
    const u = Math.max(0, Math.min(stride, Math.round((x - tile.x * span) / step)));
    const v = Math.max(0, Math.min(stride, Math.round((z - tile.z * span) / step)));
    return tile.water[v * side + u] !== 0;
  }

  attach(graphics) { this.graphics = graphics; }

  request(level, x, z, candidates) {
    const key = keyOf(level, x, z);
    const tile = this.loaded.get(key);
    if (tile) {
      tile.used = this.epoch;
      return tile;
    }
    if (!this.pending.has(key) && (!this.errors.has(key) || this.errors.get(key).retryAt <= performance.now()))
      candidates.set(key, { key, level, x, z });
    return null;
  }

  syncCollision(x, z) {
    const near = new Set();
    for (let level = 0; level < this.region.coverage.length; level++) {
      const span = spanAt(level), ix = Math.floor(x / span), iz = Math.floor(z / span);
      for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) {
        const key = keyOf(level, ix + dx, iz + dz), tile = this.loaded.get(key);
        if (!tile) continue;
        tile.used = this.epoch;
        near.add(key);
        if (!this.simulation.has(key)) this.bridge.add(level, ix + dx, iz + dz, encode(tile));
      }
    }
    for (const key of this.simulation) if (!near.has(key)) {
      const tile = this.loaded.get(key);
      if (tile) this.bridge.remove(tile.level, tile.x, tile.z);
    }
    this.simulation = near;
  }

  select(x, z, far, balanced, now) {
    // Keep view traffic and collision updates off the 120 Hz physics clock.
    if (this.nextUpdate > now) return;
    this.nextUpdate = now + 145;
    this.epoch++;
    const farRadius = far + spanAt(2);
    const candidates = new Map(), meshQueue = new Set(), visible = [];
    const radius = balanced ? .74 : 1;
    const see = (level, tx, tz) => {
      const span = spanAt(level);
      const dx = Math.max(0, tx * span - x, x - (tx + 1) * span);
      const dz = Math.max(0, tz * span - z, z - (tz + 1) * span);
      const distance = Math.hypot(dx, dz);
      if (distance > farRadius) return;
      const tile = this.request(level, tx, tz, candidates);
      if (!tile) return;
      if (!tile.mesh) meshQueue.add(tile);
      const next = level + 1;
      if (distance < screenBand[next] * radius && covered(this.region, next, tx * 2, tz * 2)) {
        const children = [[0, 0], [1, 0], [0, 1], [1, 1]];
        const tiles = children.map(([a, b]) => this.request(next, tx * 2 + a, tz * 2 + b, candidates));
        for (const child of tiles) if (child && !child.mesh) meshQueue.add(child);
        if (tiles.every(child => child?.mesh)) {
          for (const [a, b] of children) see(next, tx * 2 + a, tz * 2 + b);
          return;
        }
      }
      visible.push(tile);
    };
    const span = spanAt(0), bounds = this.region.coverage[0];
    this.outside = !within(bounds, Math.floor(x / span), Math.floor(z / span));
    for (let tx = Math.max(bounds[0], Math.floor((x - farRadius) / span)); tx <= Math.min(bounds[2] - 1, Math.floor((x + farRadius) / span)); tx++)
      for (let tz = Math.max(bounds[1], Math.floor((z - farRadius) / span)); tz <= Math.min(bounds[3] - 1, Math.floor((z + farRadius) / span)); tz++)
        see(0, tx, tz);
    this.visible = visible;
    const priority = task => {
      const size = spanAt(task.level);
      return Math.hypot(Math.max(0, task.x * size - x, x - (task.x + 1) * size),
        Math.max(0, task.z * size - z, z - (task.z + 1) * size)) + task.level * 1250;
    };
    this.meshQueue = [...meshQueue].sort((a, b) => priority(a) - priority(b));
    for (const task of [...candidates.values()].sort((a, b) => priority(a) - priority(b))) {
      if (this.pending.size >= 8) break;
      const controller = new AbortController();
      this.pending.set(task.key, controller);
      this.fetchTile(task.level, task.x, task.z, controller.signal).then(tile => {
        tile.used = this.epoch;
        this.errors.delete(task.key);
      }).catch(error => {
        if (error.name !== 'AbortError')
          this.errors.set(task.key, { message: error.message, retryAt: performance.now() + 12000 });
      }).finally(() => this.pending.delete(task.key));
    }
    this.syncCollision(x, z);
    const visibleTiles = new Map(visible.map(tile => [keyOf(tile.level, tile.x, tile.z), tile]));
    const visibleAt = (x, z) => {
      for (let i = 0; i < this.region.coverage.length; i++) {
        const span = spanAt(i);
        const tile = visibleTiles.get(keyOf(i, Math.floor(x / span), Math.floor(z / span)));
        if (tile) return tile;
      }
      return null;
    };
    const offsets = [[-1, 0], [0, 1], [1, 0], [0, -1]];
    for (const tile of visible) {
      const span = spanAt(tile.level), x = (tile.x + .5) * span, z = (tile.z + .5) * span;
      for (let edge = 0; edge < 4; edge++) {
        const [dx, dz] = offsets[edge];
        const neighbor = visibleAt(x + dx * (span / 2 + 1), z + dz * (span / 2 + 1));
        const previous = tile.seams?.get(edge);
        const key = neighbor && neighbor.level < tile.level && keyOf(neighbor.level, neighbor.x, neighbor.z);
        if (key === (previous?.key ?? false)) continue;
        if (previous?.mesh) this.graphics.releaseMesh(previous.mesh);
        tile.seams?.delete(edge);
        if (!key) continue;
        const seam = makeElevationSeam(tile, edge, neighbor);
        tile.seams ??= new Map();
        tile.seams.set(edge, { key, mesh: seam.vertices.length ? this.graphics.mesh(seam) : null });
      }
    }
    if (this.loaded.size > this.maxTiles) {
      const removable = [...this.loaded].filter(([key, tile]) => tile.used !== this.epoch && !this.simulation.has(key));
      removable.sort((a, b) => (a[1].used ?? 0) - (b[1].used ?? 0));
      for (const [key, tile] of removable.slice(0, this.loaded.size - this.maxTiles)) {
        if (tile.mesh) this.graphics.releaseMesh(tile.mesh);
        for (const { mesh } of tile.seams?.values() ?? []) if (mesh) this.graphics.releaseMesh(mesh);
        this.loaded.delete(key);
      }
    }
  }

  draw(x, z, far, balanced, now) {
    this.select(x, z, far, balanced, now);
    for (const tile of this.meshQueue.splice(0, 3))
      tile.mesh = this.graphics.mesh(makeElevationMesh(tile));
    return this.visible.filter(tile => tile.mesh).flatMap(tile => {
      const span = spanAt(tile.level), x = tile.x * span, z = tile.z * span;
      const draw = { mesh: tile.mesh, offset: [x, 0, z], shadow: false };
      return [draw, ...[...(tile.seams?.values() ?? [])].filter(seam => seam.mesh)
        .map(seam => ({ ...draw, mesh: seam.mesh }))];
    });
  }

  get status() {
    if (this.outside) return 'Scenery boundary — choose another departure in Controls.';
    if (this.errors.size) return `${this.errors.values().next().value.message} Using available terrain.`;
    return this.pending.size ? 'Streaming terrain…' : '';
  }

  dispose() {
    for (const controller of this.pending.values()) controller.abort();
    this.pending.clear();
  }
}

export const terrainTileSpan = spanAt;
