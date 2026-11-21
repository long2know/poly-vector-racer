import { mix, random } from './engine/math.js';
import { Builder } from './scene.js';

const patchSize = 1024, cells = 8, cellSize = patchSize / cells;
const bark = [.14, .095, .056];

function seed(x, z) {
  const h = Math.imul(x, 0x1e35a7bd) ^ Math.imul(z, 0x61c88647) ^ 0x4ab61d39;
  return Math.imul(h ^ h >>> 16, 0x7feb352d) ^ Math.imul(h, 0x846ca68b);
}

function noise(x, z) {
  const ix = Math.floor(x), iz = Math.floor(z);
  const smooth = t => t * t * (3 - 2 * t);
  const u = smooth(x - ix), v = smooth(z - iz);
  const point = (a, b) => (seed(a, b) >>> 0) / 4294967296;
  return mix(mix(point(ix, iz), point(ix + 1, iz), u), mix(point(ix, iz + 1), point(ix + 1, iz + 1), u), v);
}

function canopy(mesh, x, y, z, radius, height, angle, colour, sides) {
  const tip = [x, y + height, z];
  const at = i => [x + radius * Math.cos(angle + i * Math.PI * 2 / sides), y,
    z + radius * Math.sin(angle + i * Math.PI * 2 / sides)];
  for (let i = 0; i < sides; i++) mesh.triangle(at(i), tip, at(i + 1), colour);
}

function conifer(mesh, x, y, z, height, angle, tint) {
  canopy(mesh, x, y - .35, z, height * .028, height * .55, angle, bark, 3);
  canopy(mesh, x, y + height * .25, z, height * .24, height * .60, angle, [.067, .198 + tint, .12 + tint], 4);
  canopy(mesh, x, y + height * .47, z, height * .18, height * .53, angle + .3, [.082, .255 + tint, .145 + tint], 4);
}

export class ForestLayer {
  patches = new Map();
  wanted = [];
  count = 0;

  constructor(region) {
    this.region = region;
    this.cities = region.cities ?? [];
    this.setRunways([]);
  }

  setRunways(runways) {
    this.runways = [...runways, this.region.runway].filter(runway => runway &&
      [runway.centerX, runway.centerZ, runway.heading, runway.width, runway.length].every(Number.isFinite))
      .map(runway => ({ ...runway, sin: Math.sin(runway.heading), cos: Math.cos(runway.heading) }));
  }

  excluded(x, z) {
    return this.cities.some(city => Math.hypot(x - city.x, z - city.z) < 2600) || this.runways.some(runway => {
      const dx = x - runway.centerX, dz = z - runway.centerZ;
      return Math.abs(dx * runway.cos - dz * runway.sin) < runway.width / 2 + 260 &&
        Math.abs(dx * runway.sin + dz * runway.cos) < runway.length / 2 + 280;
    });
  }

  select(terrain, x, z, balanced) {
    const radius = balanced ? 3000 : 4050, limit = balanced ? 22 : 36;
    const next = [];
    for (let iz = Math.floor((z - radius) / patchSize); iz <= Math.floor((z + radius) / patchSize); iz++)
      for (let ix = Math.floor((x - radius) / patchSize); ix <= Math.floor((x + radius) / patchSize); ix++) {
        const px = ix * patchSize, pz = iz * patchSize;
        const cx = px + patchSize / 2, cz = pz + patchSize / 2, distance = Math.hypot(cx - x, cz - z);
        if (distance > radius) continue;
        // A patch aligns with both fine terrain levels, but its seed never depends on which one is visible.
        const tile = terrain.visualTileAt(cx, cz, 7);
        if (tile) next.push({ key: `${ix}/${iz}`, ix, iz, px, pz, distance, tile });
      }
    this.wanted = next.sort((a, b) => a.distance - b.distance).slice(0, limit);
    const tiles = new Map(this.wanted.map(patch => [patch.key, patch.tile]));
    for (const [key, patch] of this.patches) if (tiles.get(key) !== patch.tile) {
      if (patch.mesh) terrain.graphics.releaseMesh(patch.mesh);
      this.patches.delete(key);
    }
    if (this.job && tiles.get(this.job.key) !== this.job.tile) this.job = null;
  }

  appendCell(job, index, ground) {
    const i = job.ix * cells + index % cells, j = job.iz * cells + Math.floor(index / cells);
    const x = (i + .5) * cellSize, z = (j + .5) * cellSize;
    const cover = noise(x / 830, z / 830) * .68 + noise(x / 275 + 28, z / 275 - 17) * .32;
    if (cover < .47 || this.excluded(x, z)) return;
    const rand = random(seed(i, j));
    const cx = x + (rand() - .5) * 55, cz = z + (rand() - .5) * 55;
    const count = 3 + Math.floor(rand() * 3);
    const spin = rand() * Math.PI * 2;
    for (let k = 0; k < count; k++) {
      const angle = spin + k * Math.PI * 2 / count, r = 9 + rand() * 22;
      const tx = cx + Math.cos(angle) * r, tz = cz + Math.sin(angle) * r;
      const height = 13 + rand() * 16, tint = rand() * .047, yaw = rand() * Math.PI;
      if (this.excluded(tx, tz)) continue;
      const y = ground(job.tile, tx, tz);
      if (y === null) continue;
      conifer(job.builder, tx - job.px, y, tz - job.pz, height, yaw, tint);
      job.count++;
    }
  }

  draw(terrain, x, z, balanced, ground) {
    if (this.visible !== terrain.rendered || this.balanced !== balanced) {
      this.visible = terrain.rendered;
      this.balanced = balanced;
      this.select(terrain, x, z, balanced);
    }
    if (!this.job) {
      const patch = this.wanted.find(({ key }) => !this.patches.has(key));
      if (patch) this.job = { ...patch, builder: new Builder(), cursor: 0, count: 0 };
    }
    const job = this.job;
    if (job) {
      const deadline = performance.now() + 1.35, end = Math.min(job.cursor + 32, cells * cells);
      while (job.cursor < end) {
        this.appendCell(job, job.cursor++, ground);
        if (job.cursor % 4 === 0 && performance.now() > deadline) break;
      }
      if (job.cursor === cells * cells) {
        this.patches.set(job.key, { tile: job.tile, count: job.count, offset: [job.px, 0, job.pz],
          mesh: job.count ? terrain.graphics.mesh(job.builder, true) : null });
        this.job = null;
      }
    }
    this.count = 0;
    const draws = [];
    for (const { count, mesh, offset } of this.patches.values()) if (mesh) {
      this.count += count;
      draws.push({ mesh, offset, shadow: false });
    }
    return draws;
  }

  clear(graphics) {
    for (const { mesh } of this.patches.values()) if (mesh) graphics.releaseMesh(mesh);
    this.patches.clear();
    this.wanted = [];
    this.visible = this.job = null;
    this.count = 0;
  }
}
