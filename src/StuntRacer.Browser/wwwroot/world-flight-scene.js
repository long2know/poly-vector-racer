import { FlightScene } from './flight-scene.js';
import { Builder } from './scene.js';
import { cross, dot, pose, sub } from './engine/math.js';
import { triangulate } from './engine/triangulate.js';
import { fetchWorldJson, groundRoadLowering } from './world-terrain.js';

const metal = [.69, .74, .72], glass = [.16, .37, .44], pale = [.9, .88, .75];
const buildingColours = [[.61, .65, .61], [.53, .59, .62], [.43, .56, .63], [.62, .55, .46], [.52, .60, .61]];
const bridgeDeckTop = 70.2;
const landmarkRange = 28000, anchorLimit = 12;

function ring(mesh, y, radius, depth, colour, sides = 16) {
  const at = (i, h) => [Math.cos(i * Math.PI * 2 / sides) * radius, h, Math.sin(i * Math.PI * 2 / sides) * radius];
  for (let i = 0; i < sides; i++) {
    const bottomA = at(i, y), bottomB = at(i + 1, y), topA = at(i, y + depth), topB = at(i + 1, y + depth);
    mesh.quad(bottomA, topA, topB, bottomB, colour, 2);
    mesh.triangle([0, y + depth, 0], topB, topA, colour, 2);
  }
}

function top(mesh, outline, y, colour, material = 0) {
  const ring = outline.map(([x, z]) => [x, y, z]);
  const area = outline.reduce((sum, [x, z], i) => {
    const [a, b] = outline[(i + 1) % outline.length];
    return sum + x * b - z * a;
  }, 0);
  if (area > 0) ring.reverse();
  for (const face of triangulate(ring)) mesh.triangle(...face.map(i => ring[i]), colour, material);
}

function ballpark(mesh) {
  const grass = [.16, .50, .23], clay = [.67, .34, .18], brick = [.43, .23, .17];
  const seat = [.08, .27, .29], deck = [.24, .33, .34], chalk = [.95, .89, .73];
  const home = [0, -50], fieldY = 5, first = [19.4, -30.6], second = [0, -11.2], third = [-19.4, -30.6];
  const outfield = [[-45, 101], [-32, 113], [-16, 120], [0, 122], [17, 116], [32, 110], [45, 99]]
    .map(([angle, radius]) => [radius * Math.sin(angle * Math.PI / 180), home[1] + radius * Math.cos(angle * Math.PI / 180)]);
  const edge = [...outfield, [29, -36], [17, -58], [7, -66], [-7, -66], [-17, -58], [-29, -36]];
  const podium = edge.map(([x, z]) => [x * 1.48, z * 1.4]);
  const at = ([x, z], y) => [x, y, z];
  top(mesh, podium, fieldY - .1, brick);
  top(mesh, edge, fieldY, grass);
  for (let i = 0; i < edge.length; i++) {
    const j = (i + 1) % edge.length;
    mesh.quad(at(podium[i], -15), at(podium[j], -15), at(podium[j], fieldY), at(podium[i], fieldY), brick, 0);
    if (i < outfield.length - 1) {
      const trim = ([x, z]) => [x * .935, home[1] + (z - home[1]) * .935];
      top(mesh, [edge[i], edge[j], trim(edge[j]), trim(edge[i])], fieldY + .07, clay);
      if (i % 2 === 0)
        top(mesh, [[0, 8], trim(edge[i]), trim(edge[j])], fieldY + .035, [.19, .54, .24]);
    }
    const row = (n, index, bottom) => {
      const [x, z] = edge[index], back = index < outfield.length ? .62 : 1;
      return [x * (1 + n * .12), fieldY + n * 5.2 * back - (bottom ? 1.4 : 0), z * (1 + n * .1)];
    };
    for (let tier = 1; tier <= 4; tier++) {
      mesh.quad(row(tier - 1, i, false), row(tier, i, true),
        row(tier, j, true), row(tier - 1, j, false), tier % 2 ? seat : deck);
      mesh.quad(row(tier, i, true), row(tier, i, false), row(tier, j, false), row(tier, j, true), deck);
    }
  }
  top(mesh, [[0, -55], [25, -33], [22, -19], [13, -7], [0, -4], [-13, -7], [-22, -19], [-25, -33]],
    fieldY + .12, clay);
  top(mesh, [[0, -45], [14, -30.6], [0, -16.3], [-14, -30.6]], fieldY + .16, grass);
  top(mesh, Array.from({ length: 10 }, (_, i) =>
    [4.1 * Math.cos(i * Math.PI / 5), -32 + 4.1 * Math.sin(i * Math.PI / 5)]), fieldY + .22, clay);
  for (const [x, z] of [home, first, second, third])
    top(mesh, [[x, z - 1.65], [x + 1.65, z], [x, z + 1.65], [x - 1.65, z]], fieldY + .23, chalk);
  for (const corner of [outfield[0], outfield.at(-1)]) {
    const [x, z] = corner;
    top(mesh, [home, [home[0] + 1.25, home[1]], [x + 1.25, z], corner], fieldY + .25, chalk);
    mesh.beam([x, fieldY, z], [x, 30, z], .5, [.96, .72, .19], 2);
  }
  // The roof is parked beyond the outfield, so the diamond remains open to the sky.
  for (const x of [-59, 2, 63]) {
    mesh.box([x, 17, 97], [6, 34, 9], brick);
    mesh.beam([x, 32, 74], [x, 49, 94], 1.65, metal, 2);
    mesh.beam([x, 49, 94], [x, 35, 119], 1.65, metal, 2);
    mesh.beam([x, 33, 75], [x, 33, 118], 1.1, metal, 2);
  }
  for (let x = -76; x < 82; x += 16) {
    mesh.quad([x, 49, 94], [x, 35, 119], [x + 15.6, 35, 119], [x + 15.6, 49, 94], [.68, .75, .77], 2);
    mesh.quad([x, 32, 74], [x, 49, 94], [x + 15.6, 49, 94], [x + 15.6, 32, 74], [.59, .68, .71], 2);
  }
  mesh.box([-42, 23, 71], [28, 12, 3.2], [.095, .15, .16], 2);
}

function spheres(mesh) {
  mesh.box([0, -2.5, 0], [25, 8, 63], [.32, .40, .37]);
  const bands = 8, sides = 12;
  for (const [x, y, z, radius, height] of [[-2, 11, -23, 12.8, 12], [2, 13.5, 0, 16, 15], [-1, 10.2, 23, 11.6, 11.5]]) {
    const middle = [x, y, z], joints = [];
    for (let j = 0; j <= bands; j++) {
      const angle = Math.PI * (j / bands - .5), slice = Math.cos(angle);
      for (let k = 0; k < sides; k++) {
        const azimuth = (k + (j % 2) * .5) * Math.PI * 2 / sides;
        joints.push([x + radius * slice * Math.cos(azimuth), y + height * Math.sin(angle),
          z + radius * slice * Math.sin(azimuth)]);
      }
    }
    const ribs = new Set();
    const panel = (a, b, c, band) => {
      const p = joints[a], q = joints[b], r = joints[c];
      const tint = ((a + 11 * b + 7 * c) % 7) * .017;
      const colour = band < 4 ? [.13 + tint, .39 + tint, .29 + tint / 2] : [.19 + tint, .45 + tint, .52 + tint];
      const outward = dot(cross(sub(q, p), sub(r, p)), sub(p, middle)) > 0;
      mesh.triangle(p, ...(outward ? [q, r] : [r, q]), colour, 2);
      for (const [i, j] of [[a, b], [b, c], [c, a]]) {
        const key = Math.min(i, j) * joints.length + Math.max(i, j);
        if (ribs.has(key)) continue;
        ribs.add(key);
        mesh.beam(joints[i], joints[j], .19, [.75, .83, .77], 2, 3);
      }
    };
    for (let j = 0; j < bands; j++) for (let k = 0; k < sides; k++) {
      const a = j * sides + k, b = j * sides + (k + 1) % sides;
      const c = (j + 1) * sides + (k + j % 2) % sides, d = (j + 1) * sides + (k + j % 2 + sides - 1) % sides;
      if (j > 0) panel(a, b, c, j);
      if (j < bands - 1) panel(a, c, d, j);
    }
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
    ballpark(mesh);
  } else if (kind === 'spheres') {
    spheres(mesh);
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
    mesh.box([0, bridgeDeckTop - .1, 0], [25.8, .2, length], [.19, .2, .20]);
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

export class WorldScenery {
  constructor(graphics, terrain, runways = []) {
    this.graphics = graphics;
    this.terrain = terrain;
    terrain.attach(graphics, runways);
    this.airfields = runways.map(surface => ({ surface, paved: true }));
    if (terrain.region.runway && !runways.some(surface => surface.name === terrain.region.runway.name))
      this.airfields.push({ surface: terrain.region.runway, paved: false });
    this.landmarks = (terrain.region.landmarks ?? []).map(location => {
      const builder = landmarkGeometry(location);
      let anchorHeight = -Infinity;
      for (let i = 1; i < builder.vertices.length; i += 10) anchorHeight = Math.max(anchorHeight, builder.vertices[i]);
      return { location, mesh: graphics.mesh(builder, true),
        anchorHeight: location.kind === 'golden-gate' ? bridgeDeckTop : anchorHeight };
    });
    this.buildingCount = 0;
    this.setMapRoads(false);
  }

  setMapRoads(enabled) {
    this.mapRoads = Boolean(enabled) && Boolean(this.terrain.region.roads);
  }

  decorate(frame, x, z, options = {}) {
    const origin = [Math.round(x / 2048) * 2048, 0, Math.round(z / 2048) * 2048];
    const balanced = this.graphics.quality === 'balanced';
    const far = options.far ?? (balanced ? 140000 : 238000);
    const now = performance.now();
    const tiles = this.terrain.draw(x, z, far, balanced, now);
    this.updateBuildings(x, z, now);
    if (this.cityMesh) frame.draws.push({ mesh: this.cityMesh, shadow: false });
    frame.draws = frame.draws.map(draw => {
      const model = draw.model ? draw.model.slice() : pose([0, 0, 0]);
      model[12] -= origin[0];
      model[14] -= origin[2];
      return { ...draw, model };
    });
    frame.landmarkAnchors = this.appendLandmarks(frame.draws, x, z, origin, Math.min(landmarkRange, far));
    frame.draws.unshift(...tiles.map(({ mesh, offset, shadow }) => ({ mesh, model: pose(sub(offset, origin)), shadow })));
    frame.camera.eye = sub(frame.camera.eye, origin);
    frame.camera.target = sub(frame.camera.target, origin);
    frame.camera.far = far;
    frame.camera.fogDistance = options.fogDistance ?? (options.view === 'ground' ? far * .86 : 137000);
    frame.camera.logarithmicDepth = true;
    frame.camera.mapRoads = this.mapRoads;
    frame.camera.roadLowering = options.view === 'ground' ? groundRoadLowering : undefined;
    return frame;
  }

  appendLandmarks(draws, x, z, origin, range) {
    const distance = (px, pz) => Math.hypot(px - x, pz - z);
    const candidates = [];
    const addAnchor = (name, position, priority, metres) => {
      if (typeof name !== 'string' || !name.trim() || !position.every(Number.isFinite) || !(metres < range)) return;
      candidates.push({ name: name.trim(), position, priority, metres });
    };
    this.nearbyLandmarks = [];
    for (const { location, mesh, anchorHeight } of this.landmarks) {
      const metres = distance(location.x, location.z);
      if (!(metres < range)) continue;
      const y = ['ballpark', 'spheres'].includes(location.kind)
        ? this.terrain.visualSample(location.x, location.z, 7) : this.terrain.sample(location.x, location.z);
      if (!Number.isFinite(y)) continue;
      const heading = Number.isFinite(location.heading) ? location.heading : location.kind === 'ballpark' ? 1.09 : 0;
      const model = pose([location.x - origin[0], location.kind === 'golden-gate' ? 0 : y - 1, location.z - origin[2]], heading);
      this.nearbyLandmarks.push(location.name);
      draws.push({ mesh, model, shadow: false });
      addAnchor(location.name, [model[12], model[13] + anchorHeight, model[14]], 0, metres);
    }
    for (const { surface, paved } of this.airfields) {
      const { name, centerX, centerZ, height } = surface;
      const metres = distance(centerX, centerZ);
      if (!(metres < range)) continue;
      const y = paved ? height + .045 : this.terrain.visualSample?.(centerX, centerZ);
      addAnchor(name, [centerX - origin[0], y, centerZ - origin[2]], paved ? 1 : 2, metres);
    }
    for (const target of this.terrain.region.destinations ?? []) {
      const { id, targetX, targetZ } = target, metres = distance(targetX, targetZ);
      if (id === 'boeing' || !(metres < range) || this.landmarks.some(({ location }) =>
        Math.hypot(targetX - location.x, targetZ - location.z) < 1200)) continue;
      const [place, detail] = target.name.split(' • ');
      const name = ['Alaska', 'Hawaii'].includes(place) ? detail : place;
      addAnchor(name, [targetX - origin[0], this.terrain.visualSample?.(targetX, targetZ), targetZ - origin[2]], 3, metres);
    }
    const anchors = [], names = new Set();
    for (const { name, position } of candidates.sort((a, b) => a.priority - b.priority || a.metres - b.metres)) {
      if (anchors.length >= anchorLimit) break;
      const key = name.toLowerCase();
      if (names.has(key)) continue;
      names.add(key);
      anchors.push({ name, position });
    }
    return anchors;
  }

  updateBuildings(x, z, now) {
    const city = this.terrain.region.cities?.find(p => Math.hypot(x - p.x, z - p.z) < 27500);
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
      this.cityMesh = this.graphics.mesh(mesh, true);
      this.buildingCount = count;
      this.cityDetail = detailLevel;
    }
  }

  diagnostics() {
    const terrain = this.terrain, visible = terrain.visible.filter(tile => tile.mesh);
    return {
      region: terrain.region.id, buildings: this.buildingCount,
      visible: visible.length, loaded: terrain.loaded.size,
      finest: Math.max(-1, ...visible.map(tile => tile.level)),
      pending: terrain.pending.size, errors: terrain.errors.size,
      meshCount: this.graphics.meshes.size,
      meshBytes: this.graphics.meshBytes,
      city: this.city?.id ?? null,
      landmarks: this.nearbyLandmarks ?? [],
      roadMeshes: terrain.roads?.meshCount ?? 0,
      roadStyle: this.mapRoads ? 'map' : 'terrain',
      trees: terrain.forest?.count ?? 0,
    };
  }

  dispose() {
    this.buildingLoad?.abort();
    this.terrain.dispose();
    if (this.cityMesh) this.graphics.releaseMesh(this.cityMesh);
    this.cityMesh = null;
    for (const { mesh } of this.landmarks) this.graphics.releaseMesh(mesh);
    this.landmarks = [];
  }
}

export class WorldFlightScene extends FlightScene {
  constructor(graphics, world, terrain, aircraftId = 'f18') {
    super(graphics, world, aircraftId);
    this.terrain = terrain;
    this.scenery = new WorldScenery(graphics, terrain, world.landingSurfaces);
    const { destination } = world.geography;
    this.navigationWorld.landingSurfaces.push({ name: destination.name.split(' • ')[0],
      centerX: destination.targetX, centerZ: destination.targetZ });
    this.track.points = [world.runwayStart, { x: destination.targetX, z: destination.targetZ }];
  }

  get landmarks() { return this.scenery.landmarks; }

  get nearbyLandmarks() { return this.scenery.nearbyLandmarks; }

  get buildingCount() { return this.scenery.buildingCount; }

  setMapRoads(enabled) { this.scenery.setMapRoads(enabled); }

  update(state, dt, mode) {
    const frame = super.update(state, dt, mode), { aircraft } = state.flight;
    return this.scenery.decorate(frame, aircraft.x, aircraft.z);
  }

  updateBuildings(aircraft, now) { this.scenery.updateBuildings(aircraft.x, aircraft.z, now); }

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
      ...this.scenery.diagnostics(),
      destination: destination.id,
      targetElevation: this.terrain.sample(destination.targetX, destination.targetZ),
    };
  }

  dispose() {
    this.scenery.dispose();
    super.dispose();
  }
}
