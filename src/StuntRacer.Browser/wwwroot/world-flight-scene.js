import { FlightScene } from './flight-scene.js';
import { Builder } from './scene.js';
import { pose, sub } from './engine/math.js';
import { triangulate } from './engine/triangulate.js';
import { fetchWorldJson } from './world-terrain.js';

const metal = [.69, .74, .72], glass = [.16, .37, .44], pale = [.9, .88, .75];
const buildingColours = [[.61, .65, .61], [.53, .59, .62], [.43, .56, .63], [.62, .55, .46], [.52, .60, .61]];

function ring(mesh, y, radius, depth, colour, sides = 16) {
  const at = (i, h) => [Math.cos(i * Math.PI * 2 / sides) * radius, h, Math.sin(i * Math.PI * 2 / sides) * radius];
  for (let i = 0; i < sides; i++) {
    const bottomA = at(i, y), bottomB = at(i + 1, y), topA = at(i, y + depth), topB = at(i + 1, y + depth);
    mesh.quad(bottomA, topA, topB, bottomB, colour, 2);
    mesh.triangle([0, y + depth, 0], topB, topA, colour, 2);
  }
}

function landmarkGeometry({ kind, span }) {
  const mesh = new Builder();
  if (kind === 'needle') {
    for (let i = 0; i < 3; i++) {
      const angle = i * Math.PI * 2 / 3;
      const at = (r, y) => [r * Math.sin(angle), y, r * Math.cos(angle)];
      mesh.beam(at(15, 0), at(3.2, 103), 2.2, metal, 2);
      mesh.beam(at(3.2, 103), at(10, 149), 1.7, metal, 2);
    }
    mesh.box([0, 68, 0], [5, 136, 5], metal, 2);
    ring(mesh, 139, 10, 7, pale);
    ring(mesh, 146, 19.5, 3.8, pale);
    ring(mesh, 150, 17.4, 6, glass);
    ring(mesh, 156, 20.25, 2.8, pale);
    ring(mesh, 158.8, 13, 2.5, metal);
    mesh.beam([0, 160, 0], [0, 184.4, 0], .65, pale, 2);
    mesh.box([0, 184.2, 0], [1.4, .9, 1.4], [.9, .2, .08], 4);
  } else if (kind === 'wheel') {
    const center = [0, 28, 0], points = Array.from({ length: 16 }, (_, i) =>
      [Math.cos(i * Math.PI / 8) * 25, 28 + Math.sin(i * Math.PI / 8) * 25, 0]);
    for (let i = 0; i < points.length; i++) {
      mesh.beam(points[i], points[(i + 1) % points.length], .75, pale, 2);
      mesh.beam(center, points[i], .27, metal, 2, 3);
      mesh.box(points[i], [3.9, 3.4, 3], glass, 2);
    }
    mesh.beam([-14, 0, 5], center, 1.5, metal, 2);
    mesh.beam([14, 0, -5], center, 1.5, metal, 2);
    mesh.box([0, -1.6, 0], [61, 3.3, 20], [.38, .32, .25]);
  } else if (kind === 'stadium') {
    mesh.box([0, 2, 0], [145, 4, 191], [.26, .27, .29], 2);
    mesh.box([0, 4.1, 0], [69, .7, 111], [.24, .45, .2]);
    for (const sign of [-1, 1]) {
      mesh.box([sign * 50, 13, 0], [29, 22, 161], glass, 2);
      mesh.box([0, 9, sign * 71], [77, 14, 18], glass, 2);
      mesh.beam([sign * 57, 29, -85], [sign * 57, 29, 85], 5.3, pale, 2, 6);
      mesh.beam([sign * 65, 24, -77], [sign * 65, 24, 77], 5.3, metal, 2, 6);
    }
  } else if (kind === 'ballpark') {
    mesh.box([0, 3, 0], [173, 6, 186], [.36, .27, .23], 2);
    mesh.box([0, 6.1, 0], [111, .7, 122], [.23, .42, .2]);
    mesh.box([0, 15, 77], [179, 25, 33], [.45, .28, .23], 2);
    for (const sign of [-1, 1]) {
      mesh.box([sign * 74, 14, 0], [26, 19, 122], [.45, .28, .23], 2);
      mesh.beam([sign * 81, 33, -81], [sign * 81, 33, 85], 1.6, metal, 2);
    }
    mesh.quad([-82, 33, -76], [-82, 48, -15], [82, 48, -15], [82, 33, -76], pale, 2);
  } else if (kind === 'pyramid') {
    const corners = (width, y) => [[-width, y, -width], [-width, y, width], [width, y, width], [width, y, -width]];
    const base = corners(23, 8), upper = corners(2.4, 223);
    for (let i = 0; i < base.length; i++) {
      mesh.quad(base[i], base[(i + 1) % 4], upper[(i + 1) % 4], upper[i], [.75, .76, .68], 2);
    }
    mesh.box([0, 5, 0], [52, 10, 52], metal, 2);
    mesh.box([0, 240, 0], [1.9, 35, 1.9], pale, 2);
    for (const x of [-1, 1]) mesh.box([x * 9, 98, 0], [3, 92, 12], [.70, .72, .66], 2);
  } else if (kind === 'coit') {
    ring(mesh, 0, 8.9, 5, pale, 12);
    ring(mesh, 5, 6.3, 51, [.77, .73, .63], 12);
    ring(mesh, 56, 7.3, 2.5, pale, 12);
    ring(mesh, 58.5, 6.8, 4, [.62, .60, .51], 12);
    ring(mesh, 62.5, 7.4, 1.5, pale, 12);
  } else if (kind === 'golden-gate') {
    if (!Number.isFinite(span) || span < 1000 || span > 1600)
      throw new Error('Invalid span for the Golden Gate landmark.');
    const red = [.72, .20, .09], deck = 68, tower = 227, sideSpan = 340;
    const length = span + 2 * sideSpan;
    mesh.box([0, deck, 0], [28, 4, length], red, 2);
    mesh.box([0, deck + 2.1, 0], [25.8, .2, length], [.19, .2, .20]);
    for (const z of [-span / 2, span / 2]) {
      for (const x of [-13.7, 13.7])
        mesh.box([x, (tower - 4) / 2, z], [8, tower + 4, 10], red, 2);
      for (const y of [86, 126, 166, 207])
        mesh.box([0, y, z], [28, 5.5, 8], red, 2);
    }
    for (const x of [-14, 14]) for (let i = 0; i < 38; i++) {
      const z = -length / 2 + i * length / 38, next = z + length / 38;
      const cableHeight = p => {
        const u = Math.abs(p);
        return u < span / 2 ? deck + 17 + (tower - deck - 17) * (2 * u / span) ** 2
          : tower + (deck + 11 - tower) * ((u - span / 2) / sideSpan) ** 0.8;
      };
      mesh.beam([x, cableHeight(z), z], [x, cableHeight(next), next], 1.2, red, 2);
      mesh.beam([x, deck, z], [x, cableHeight(z), z], .43, red, 2);
    }
  } else throw new Error(`Unknown authored landmark ${kind}.`);
  return mesh;
}

function buildingsGeometry(rows, terrain) {
  const mesh = new Builder();
  let count = 0;
  for (const row of rows) {
    if (!Array.isArray(row) || row.length !== 4 || !Number.isSafeInteger(row[0]) ||
        !Number.isFinite(row[1]) || row[1] < 3 || row[1] > 360 ||
        !['height', 'levels', 'estimated'].includes(row[2]) || !Array.isArray(row[3]) ||
        row[3].length < 3 || row[3].length > 200 ||
        row[3].some(p => !Array.isArray(p) || p.length !== 2 || !p.every(Number.isFinite)))
      throw new Error('World Flight footprint data contains an invalid building.');
    const [id, height, , ring] = row;
    const x = ring.reduce((a, p) => a + p[0], 0) / ring.length;
    const z = ring.reduce((a, p) => a + p[1], 0) / ring.length;
    const h = terrain.sample(x, z);
    if (h === null) throw new Error('Mapped building has no underlying terrain.');
    const roof = ring.map(p => [p[0], h + height, p[1]]);
    if (ring.reduce((a, p, i) => {
      const q = ring[(i + 1) % ring.length];
      return a + p[0] * q[1] - p[1] * q[0];
    }, 0) > 0) roof.reverse();
    const faces = triangulate(roof);
    if (!faces.length) continue;
    const color = buildingColours[id % buildingColours.length];
    for (const face of faces) mesh.triangle(...face.map(i => roof[i]), color.map(c => c * 1.04), 0);
    for (let i = 0; i < roof.length; i++) {
      const a = roof[i], b = roof[(i + 1) % roof.length];
      if (a[0] === b[0] && a[2] === b[2]) continue;
      mesh.quad([a[0], h - 5, a[2]], a, b, [b[0], h - 5, b[2]], color, 2);
    }
    count++;
  }
  return { mesh, count };
}

export class WorldFlightScene extends FlightScene {
  constructor(graphics, world, terrain) {
    super(graphics, world);
    this.terrain = terrain;
    terrain.attach(graphics);
    const { destination } = world.geography;
    this.navigationWorld.landingSurfaces.push({ name: destination.name.split(' • ')[0],
      centerX: destination.targetX, centerZ: destination.targetZ });
    this.landmarks = terrain.region.landmarks.map(location => ({ location, mesh: graphics.mesh(landmarkGeometry(location)) }));
    this.track.points = [world.runwayStart, { x: destination.targetX, z: destination.targetZ }];
    this.buildingCount = 0;
  }

  update(state, dt, mode) {
    const frame = super.update(state, dt, mode), { aircraft } = state.flight;
    const origin = [Math.round(aircraft.x / 2048) * 2048, 0, Math.round(aircraft.z / 2048) * 2048];
    const far = this.graphics.quality === 'balanced' ? 140000 : 238000;
    const now = performance.now();
    const tiles = this.terrain.draw(aircraft.x, aircraft.z, far, this.graphics.quality === 'balanced', now);
    this.updateBuildings(aircraft, now);
    if (this.cityMesh) frame.draws.push({ mesh: this.cityMesh, shadow: false });
    this.nearbyLandmarks = [];
    for (const { location, mesh } of this.landmarks) {
      const y = this.terrain.sample(location.x, location.z);
      if (y !== null && Math.hypot(location.x - aircraft.x, location.z - aircraft.z) < 28000) {
        this.nearbyLandmarks.push(location.name);
        frame.draws.push({ mesh, model: pose([location.x, location.kind === 'golden-gate' ? 0 : y - 1, location.z],
          location.heading ?? 0), shadow: false });
      }
    }
    for (const draw of frame.draws) {
      const model = draw.model ? draw.model.slice() : pose([0, 0, 0]);
      model[12] -= origin[0];
      model[14] -= origin[2];
      draw.model = model;
    }
    frame.draws.unshift(...tiles.map(({ mesh, offset, shadow }) => ({ mesh, model: pose(sub(offset, origin)), shadow })));
    frame.camera.eye = sub(frame.camera.eye, origin);
    frame.camera.target = sub(frame.camera.target, origin);
    frame.camera.far = far;
    frame.camera.fogDistance = 137000;
    return frame;
  }

  updateBuildings(aircraft, now) {
    const city = this.terrain.region.cities.find(p => Math.hypot(aircraft.x - p.x, aircraft.z - p.z) < 27500);
    if (city !== this.city) {
      this.buildingLoad?.abort();
      this.buildingLoad = null;
      this.buildingData = null;
      this.buildingError = '';
      this.buildingRetryAt = 0;
      if (this.cityMesh) this.graphics.releaseMesh(this.cityMesh);
      this.cityMesh = null;
      this.buildingCount = 0;
      this.city = city;
    }
    if (!city) return;
    const tile = this.terrain.at(city.x, city.z);
    const detailLevel = tile?.level ?? -1;
    if (!this.buildingData && !this.buildingLoad && this.buildingRetryAt <= now) {
      const load = new AbortController();
      this.buildingLoad = load;
      fetchWorldJson(new URL(`${city.id}-buildings.json`, this.terrain.catalog.tilesUrl), 4 * 1024 * 1024, load.signal).then(data => {
        if (load.signal.aborted) return;
        if (data.license !== 'ODbL-1.0' || !Array.isArray(data.buildings) || data.buildings.length !== city.featureCount)
          throw new Error('The building footprints do not match the terrain catalog.');
        this.buildingData = data.buildings;
        this.buildingError = '';
      }).catch(error => {
        if (load.signal.aborted) return;
        this.buildingError = error.message;
        this.buildingRetryAt = performance.now() + 12000;
      }).finally(() => { if (this.buildingLoad === load) this.buildingLoad = null; });
    }
    if (this.buildingData && detailLevel >= 6 &&
        (!this.cityMesh || detailLevel > this.cityDetail)) {
      const { mesh, count } = buildingsGeometry(this.buildingData, this.terrain);
      if (this.cityMesh) this.graphics.releaseMesh(this.cityMesh);
      this.cityMesh = this.graphics.mesh(mesh);
      this.buildingCount = count;
      this.cityDetail = detailLevel;
    }
  }

  mapPoint(x, z) {
    const [centerX, centerZ, metresPerPixel] = this.mapFrame();
    return [80 + (x - centerX) / metresPerPixel, 80 - (z - centerZ) / metresPerPixel];
  }

  mapFrame() {
    const d = this.world.geography.destination;
    const scale = Math.max(17500, Math.hypot(d.x - d.targetX, d.z - d.targetZ) * 1.7) / 70;
    return [d.targetX, d.targetZ, scale];
  }

  mapWaterPath(now) {
    if (now < (this.mapRefresh ?? 0)) return this.mapWater ?? '';
    this.mapRefresh = now + 900;
    const [x, z, scale] = this.mapFrame();
    const commands = [];
    for (let row = 0; row < 40; row++) {
      let run = -1;
      for (let col = 0; col <= 40; col++) {
        const w = col < 40 && this.terrain.isWater(x + (col * 4 - 78) * scale, z - (row * 4 - 78) * scale);
        if (w && run < 0) run = col;
        if (!w && run >= 0) {
          commands.push(`M${run * 4} ${row * 4}h${(col - run) * 4}v4H${run * 4}Z`);
          run = -1;
        }
      }
    }
    return this.mapWater = commands.join('');
  }

  diagnostics() {
    const destination = this.world.geography.destination;
    return {
      region: this.terrain.region.id, destination: destination.id, buildings: this.buildingCount,
      visible: this.terrain.visible.filter(tile => tile.mesh).length, loaded: this.terrain.loaded.size,
      finest: Math.max(-1, ...this.terrain.visible.filter(tile => tile.mesh).map(tile => tile.level)),
      pending: this.terrain.pending.size, errors: this.terrain.errors.size,
      targetElevation: this.terrain.sample(destination.targetX, destination.targetZ),
      meshCount: this.graphics.meshes.size,
      city: this.city?.id ?? null,
      landmarks: this.nearbyLandmarks ?? [],
    };
  }

  dispose() {
    this.buildingLoad?.abort();
    this.terrain.dispose();
  }
}
