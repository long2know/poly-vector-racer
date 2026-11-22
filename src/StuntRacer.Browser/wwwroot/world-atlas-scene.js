import { FlightScene } from './flight-scene.js';
import { clamp, pose } from './engine/math.js';
import {
  atlasBounds, atlasOverlap, indexAtlasFeatures, makeAtlasFeatures, makeAtlasGround, makeAtlasSeam, sampleAtlasTile,
} from './world-atlas-geometry.js';
import { atlasGrid, wrapAtlasX } from './world-atlas.js';

const { halfWidth: atlasHalf, halfHeight: atlasPole } = atlasGrid;
const groundLimit = 104, groundCacheLimit = 128;
const featureLimit = 48, overlayLimit = 64, overlayRetention = 4;
const regionSize = 2048, mapCell = 4096, mapScale = 280;
const labelRange = 32000, labelLimit = 12;
const prepareMilliseconds = 3, uploadByteLimit = 160 * 1024, uploadFloats = 1536 * 30;

const same = (a, b) => a.length === b.length && a.every((item, i) => item === b[i]);
const validKey = key => typeof key === 'string' && key.length > 0 && key.length <= 160;
const levelOf = tile => Math.log2(8192 / tile.step);
const tileSpan = tile => tile.step * 32;
const tileAddress = (x, z) => (x + atlasHalf) * (2 * atlasPole + 1) + z + atlasPole;
const tileMatches = (a, b) => a === b || a.x === b.x && a.z === b.z && a.step === b.step &&
  a.heights === b.heights && a.water === b.water;
const inTile = (bounds, x, z) => x >= bounds[0] && x < bounds[2] && z >= bounds[1] && z < bounds[3];
const touchesSamples = (bounds, [west, south, east, north]) =>
  (bounds[0] <= east && bounds[2] > west || east === atlasHalf && bounds[0] === -atlasHalf) &&
  bounds[1] <= north && bounds[3] > south;
const tileValid = tile => tile && validKey(tile.key) && Number.isSafeInteger(tile.step) && tile.step > 0 &&
  Number.isSafeInteger(tile.x) && Number.isSafeInteger(tile.z) &&
  tile.x >= -atlasHalf && tile.x + tileSpan(tile) <= atlasHalf &&
  tile.z >= -atlasPole && tile.z + tileSpan(tile) <= atlasPole &&
  tile.heights?.length === 1089 && tile.water?.length === 1089;
const featureValid = chunk => chunk && validKey(chunk.key) && Array.isArray(chunk.rows) &&
  Number.isSafeInteger(chunk.span) && chunk.span > 0 && Number.isSafeInteger(chunk.x) && Number.isSafeInteger(chunk.z) &&
  chunk.x >= -atlasHalf && chunk.x + chunk.span <= atlasHalf &&
  chunk.z >= -atlasPole && chunk.z + chunk.span <= atlasPole;

function distanceToBounds(bounds, x, z) {
  const center = (bounds[0] + bounds[2]) / 2;
  const dx = Math.max(0, Math.abs(wrapAtlasX(x - center)) - (bounds[2] - bounds[0]) / 2);
  return Math.hypot(dx, Math.max(0, bounds[1] - z, z - bounds[3]));
}

class FrameWork {
  deadline = performance.now() + prepareMilliseconds;
  uploadBytes = 0;
  uploads = 0;

  timeLeft() { return performance.now() < this.deadline; }

  advance(iterator, steps, milliseconds) {
    const deadline = Math.min(this.deadline, performance.now() + milliseconds);
    for (let i = 0; i < steps && performance.now() < deadline; i++) {
      const step = iterator.next();
      if (step.done) return step.value;
    }
    return null;
  }

  upload(graphics, geometry) {
    const bytes = geometry.vertices.length * 24 / 10;
    if (this.uploads >= 3 || this.uploadBytes + bytes > uploadByteLimit || !this.timeLeft()) return null;
    const mesh = graphics.mesh(geometry, true);
    this.uploadBytes += bytes;
    this.uploads++;
    return mesh;
  }
}

function flightWorld(world) {
  // Atlas owns all ground geometry; map point data must never become an authored landing surface.
  return { ...world, islands: [], landingSurfaces: [], sourceWorld: null,
    geography: { destination: world.atlas?.destination } };
}

export class WorldAtlasScene extends FlightScene {
  constructor(graphics, world, terrain, aircraftId = 'f18') {
    super(graphics, flightWorld(world), aircraftId);
    this.terrain = terrain;
    this.destination = world.atlas?.destination ?? null;
    this.navigationWorld = { ...world, landingSurfaces: world.landingSurfaces ?? [] };
    const start = world.runwayStart ?? this.destination;
    this.track.points = Number.isFinite(start?.x) && Number.isFinite(start?.z) ? [{ x: start.x, z: start.z }] : [];
    this.x = wrapAtlasX(start?.x ?? 0);
    this.z = clamp(start?.z ?? 0, -atlasPole, atlasPole);
    this.frameId = 0;
    this.groundCache = new Map();
    this.active = [];
    this.activeSet = new Set();
    this.activeGrid = new Map();
    this.groundChanges = [];
    this.unaligned = [];
    this.seams = new Map();
    this.seamTargets = new Map();
    this.chunks = new Map();
    this.overlays = new Map();
    this.overlayTargets = new Map();
    this.overlayHolds = new Map();
    this.blockedOverlays = new Set();
    this.retired = [];
    this.renderWork = { peakMilliseconds: 0, peakTransportMilliseconds: 0, peakUploadBytes: 0 };
    this.labelPoints = [];
    this.nearbyLandmarks = [];
    this.mapRoads = false;
    this.buildingCount = 0;
  }

  setMapRoads(enabled) { this.mapRoads = Boolean(enabled); }

  update(state, dt, mode = 'chase') {
    if (this.disposed) throw new Error('The World Atlas scene has been disposed.');
    const aircraft = state.flight.aircraft, now = performance.now();
    const x = this.x = wrapAtlasX(aircraft.x), z = this.z = clamp(aircraft.z, -atlasPole, atlasPole);
    const ox = Math.round(x / regionSize) * regionSize, oz = Math.round(z / regionSize) * regionSize;
    if (this.origin && this.eye) {
      const dx = wrapAtlasX(this.origin[0] - ox), dz = this.origin[1] - oz;
      if (dx || dz) {
        this.eye = [this.eye[0] + dx, this.eye[1], this.eye[2] + dz];
        this.target = [this.target[0] + dx, this.target[1], this.target[2] + dz];
      }
    }
    this.origin = [ox, oz];
    const navigation = this.navigationWorld;
    const localX = px => wrapAtlasX(px - x) + (x - ox);
    this.navigationWorld = { ...navigation, landingSurfaces: navigation.landingSurfaces.map(surface =>
      ({ ...surface, centerX: localX(surface.centerX), centerZ: surface.centerZ - oz })) };
    let frame;
    try {
      frame = super.update({ ...state, flight: { ...state.flight, aircraft: { ...aircraft, x: x - ox, z: z - oz } } }, dt, mode);
    } finally {
      this.navigationWorld = navigation;
    }

    const balanced = this.graphics.quality === 'balanced', far = balanced ? 140000 : 238000;
    const transportStart = performance.now();
    const { tiles, features } = this.terrain.frame(x, z, far, now);
    const transportMilliseconds = performance.now() - transportStart, budget = new FrameWork();
    this.flushRetired(budget);
    const featureTurn = this.overlayJob?.built && this.frameId % 3 === 1;
    const changed = this.updateGround(tiles, budget, !featureTurn);
    const newFeatures = this.indexChunks(features, budget);
    this.updateOverlays(changed || newFeatures, budget);
    this.finishOverlayHandoffs();
    this.updateSeams(changed, budget);
    this.updateLabelPoints(now, changed || newFeatures);

    const modelAt = tile => pose([localX(tile.x + tileSpan(tile) / 2) - tileSpan(tile) / 2, 0, tile.z - oz]);
    const draws = [];
    let renderedRoadMeshes = 0, renderedFeatures = 0, buildings = 0;
    const appendOverlay = (record, model) => {
      const overlay = this.overlays.get(record);
      if (!overlay?.meshes.length) return;
      for (const mesh of overlay.meshes) draws.push({ mesh, model, shadow: false });
      renderedFeatures += overlay.meshes.length;
      renderedRoadMeshes += overlay.roadMeshes;
      buildings += overlay.buildings;
    };
    for (const record of this.active) {
      const model = modelAt(record.tile);
      draws.push({ mesh: record.mesh, model, shadow: false });
      if (!this.blockedOverlays.has(record)) appendOverlay(record, model);
      for (const seam of record.edges ?? []) if (seam?.mesh)
        draws.push({ mesh: seam.mesh, model, shadow: false });
    }
    for (const record of this.overlayHolds.keys()) appendOverlay(record, modelAt(record.tile));
    this.renderedFeatures = renderedFeatures;
    this.renderedRoadMeshes = renderedRoadMeshes;
    this.buildingCount = buildings;
    frame.draws = draws.concat(frame.draws);
    frame.landmarkAnchors = this.labelPoints.map(point =>
      ({ name: point.name, position: [localX(point.x), point.y, point.z - oz] }));
    frame.camera.far = far;
    frame.camera.fogDistance = balanced ? 95000 : 137000;
    frame.camera.mapRoads = this.mapRoads;
    this.flushRetired(budget);
    const milliseconds = Math.max(0, performance.now() - now - transportMilliseconds);
    this.renderWork = {
      milliseconds, transportMilliseconds, uploadBytes: budget.uploadBytes, uploads: budget.uploads,
      peakMilliseconds: Math.max(this.renderWork.peakMilliseconds, milliseconds),
      peakTransportMilliseconds: Math.max(this.renderWork.peakTransportMilliseconds, transportMilliseconds),
      peakUploadBytes: Math.max(this.renderWork.peakUploadBytes, budget.uploadBytes),
    };
    return frame;
  }

  flushRetired(budget) {
    for (let i = 0; i < 8 && this.retired.length && budget.timeLeft(); i++)
      this.graphics.releaseMesh(this.retired.pop());
  }

  updateGround(tiles, budget, allowUpload) {
    const wanted = new Map(), requests = [], warming = [];
    for (let i = 0; i < Math.min(tiles.length, groundLimit); i++) {
      const tile = tiles[i];
      if (!tileValid(tile) || wanted.has(tile.key)) continue;
      wanted.set(tile.key, tile);
      (tile.pending ? warming : requests).push(tile);
    }
    this.frameId++;
    const loaded = tile => {
      const record = this.groundCache.get(tile.key);
      return record && tileMatches(record.tile, tile) ? record : null;
    };
    const candidates = requests.concat(warming), nextTile = candidates.find(tile => !loaded(tile));
    if (this.groundJob && (!nextTile || nextTile.key !== this.groundJob.tile.key || !tileMatches(nextTile, this.groundJob.tile))) {
      this.groundJob.iterator.return();
      this.groundJob = null;
    }
    if (nextTile && budget.timeLeft()) {
      this.groundJob ??= { tile: nextTile, iterator: makeAtlasGround(nextTile) };
      const job = this.groundJob;
      job.geometry ??= budget.advance(job.iterator, 40, 1.25);
      const mesh = allowUpload && job.geometry ? budget.upload(this.graphics, job.geometry) : null;
      if (mesh) {
        const previous = this.groundCache.get(nextTile.key);
        this.groundCache.set(nextTile.key, { tile: nextTile, mesh,
          bounds: atlasBounds(nextTile.x, nextTile.z, tileSpan(nextTile)) });
        if (previous) this.retired.push(previous.mesh);
        this.groundJob = null;
      }
    }
    for (const tile of candidates) {
      const record = loaded(tile);
      if (record) {
        record.used = this.frameId;
        if (tile.pending) this.terrain.ready?.(tile.key);
      }
    }

    // Also keep the previous cut when a transport supplies an already-selected group rather than a pending list.
    const missing = requests.filter(tile => !loaded(tile)).map(tile => atlasBounds(tile.x, tile.z, tileSpan(tile)));
    const held = missing.length ? this.active.filter(record => !wanted.has(record.tile.key) &&
      missing.some(bounds => atlasOverlap(record.bounds, bounds))) : [];
    const next = requests.map(loaded).filter(record => record &&
      !held.some(old => atlasOverlap(old.bounds, record.bounds))).concat(held);
    const changed = this.activeSet.size !== next.length || next.some(record => !this.activeSet.has(record));
    this.active = next;
    for (const record of held) record.used = this.frameId;

    let excess = this.groundCache.size - groundCacheLimit;
    for (const [key, record] of this.groundCache) {
      if (record.used === this.frameId || excess <= 0 && this.frameId - record.used <= 48) continue;
      this.retired.push(record.mesh);
      this.groundCache.delete(key);
      excess--;
    }
    this.groundChanges = [];
    if (changed) {
      const current = new Set(next);
      for (const tile of this.activeSet) if (!current.has(tile)) this.groundChanges.push(tile.bounds);
      for (const tile of current) if (!this.activeSet.has(tile)) this.groundChanges.push(tile.bounds);
      this.activeSet = current;
      this.activeGrid = new Map();
      this.unaligned = [];
      for (const record of this.active) {
        const { x, z } = record.tile, span = tileSpan(record.tile);
        if (x % span || z % span) { this.unaligned.push(record); continue; }
        if (!this.activeGrid.has(span)) this.activeGrid.set(span, new Map());
        this.activeGrid.get(span).set(tileAddress(x, z), record);
      }
    }
    return changed;
  }

  groundAt(x, z) {
    if (z < -atlasPole || z > atlasPole) return null;
    if (z === atlasPole) z -= .001;
    x = wrapAtlasX(x);
    for (const [span, grid] of this.activeGrid) {
      const record = grid.get(tileAddress(Math.floor(x / span) * span, Math.floor(z / span) * span));
      if (record) return record.tile;
    }
    return this.unaligned.find(record => inTile(record.bounds, x, z))?.tile ?? null;
  }

  updateSeams(changed, budget) {
    if (changed) {
      const targets = new Map();
      for (const record of this.active) {
        const t = record.tile, [w, s, e, n] = record.bounds;
        const x = (w + e) / 2, z = (s + n) / 2;
        const probes = [[w - .5, z], [e + .5, z], [x, s - .5], [x, n + .5]];
        record.edges = probes.map(([x, z], edge) => {
          const neighbor = this.groundAt(x, z);
          if (!neighbor || neighbor === t || neighbor.step < t.step || neighbor.step === t.step && edge % 2 === 0) return null;
          const key = JSON.stringify([t.key, edge, neighbor.key]);
          let seam = this.seams.get(key);
          if (seam && (seam.tile !== t || seam.neighbor !== neighbor)) {
            if (seam.mesh) this.retired.push(seam.mesh);
            this.seams.delete(key);
            seam = null;
          }
          seam ??= { tile: t, edge, neighbor };
          targets.set(key, seam);
          return seam;
        });
      }
      for (const [key, seam] of this.seams) if (!targets.has(key)) {
        if (seam.mesh) this.retired.push(seam.mesh);
        this.seams.delete(key);
      }
      this.seamTargets = targets;
    }
    let checked = 0;
    for (const [key, seam] of this.seamTargets) {
      if (this.seams.has(key)) continue;
      if (!budget.timeLeft() || ++checked > 6) break;
      const geometry = seam.geometry ??= makeAtlasSeam(seam.tile, seam.edge, seam.neighbor);
      seam.mesh = geometry.vertices.length ? budget.upload(this.graphics, geometry) : null;
      if (geometry.vertices.length && !seam.mesh) break;
      seam.geometry = null;
      this.seams.set(key, seam);
    }
  }

  indexChunks(features, budget) {
    const current = new Map();
    let changed = false;
    for (let i = 0; i < Math.min(features.length, featureLimit); i++) {
      const chunk = features[i];
      if (!featureValid(chunk) || current.has(chunk.key)) continue;
      const old = this.chunks.get(chunk.key);
      const reuse = old?.chunk.rows === chunk.rows && old.chunk.x === chunk.x && old.chunk.z === chunk.z &&
        old.chunk.span === chunk.span && old.chunk.level === chunk.level;
      current.set(chunk.key, reuse ? old : { chunk, work: indexAtlasFeatures(chunk) });
    }
    for (const [key, value] of this.chunks) if (current.get(key) !== value) {
      value.work?.return();
      changed ||= Boolean(value.index);
    }
    this.chunks = current;
    const job = [...current.values()].find(value => value.work);
    if (job) {
      const result = budget.advance(job.work, 315, .7);
      if (result) { job.index = result; job.work = null; changed = true; }
    }
    return changed;
  }

  updateOverlays(changed, budget) {
    if (changed) {
      const needsResampling = layer => layer.sampleBounds &&
        this.groundChanges.some(bounds => touchesSamples(bounds, layer.sampleBounds));
      const indexed = [...this.chunks.values()].flatMap(value => value.index ? [value.index] : [])
        .sort((a, b) => a.chunk.key.localeCompare(b.chunk.key));
      const targets = new Map(), nearest = this.active.map(record => [record, distanceToBounds(record.bounds, this.x, this.z)])
        .sort((a, b) => a[1] - b[1]);
      for (const [record] of nearest) {
        const relevant = indexed.filter(part => atlasOverlap(part.bounds, record.bounds) &&
          (part.lineMask || record.tile.step <= 256 && (part.landuse.length || part.buildings.length)));
        if (relevant.length) targets.set(record, relevant);
        if (targets.size >= overlayLimit) break;
      }
      const holds = new Map();
      for (const [record, previous] of this.overlays) {
        const parts = targets.get(record);
        if (parts && previous.parts.every(part => parts.includes(part))) {
          previous.dirty ||= needsResampling(previous);
          continue;
        }
        if (previous.buildings && !this.activeSet.has(record) && holds.size < overlayRetention &&
            previous.parts.every(part => indexed.includes(part))) {
          const successors = [...targets.keys()].filter(candidate => atlasOverlap(candidate.bounds, record.bounds));
          if (successors.length) { holds.set(record, successors); continue; }
        }
        this.retired.push(...previous.meshes);
        this.overlays.delete(record);
      }
      this.overlayTargets = targets;
      this.overlayHolds = holds;
      const job = this.overlayJob;
      if (job && (needsResampling(job) || !job.parts.every(part => targets.get(job.record)?.includes(part)))) {
        job.work.return();
        this.retired.push(...job.meshes);
        this.overlayJob = null;
      }
    }

    if (!this.overlayJob) {
      for (const [record, parts] of this.overlayTargets) {
        if (this.overlayReady(record, parts)) continue;
        const job = { record, parts, meshes: [], uploadedFloats: 0 };
        job.work = makeAtlasFeatures(record.tile, parts, (x, z) => {
          const bounds = job.sampleBounds ??= [x, z, x, z];
          bounds[0] = Math.min(bounds[0], x); bounds[1] = Math.min(bounds[1], z);
          bounds[2] = Math.max(bounds[2], x); bounds[3] = Math.max(bounds[3], z);
          const adjacent = this.groundAt(x, z);
          return adjacent ? sampleAtlasTile(adjacent, wrapAtlasX(x - adjacent.x), z - adjacent.z) : null;
        });
        this.overlayJob = job;
        break;
      }
    }
    const job = this.overlayJob;
    if (!job) return;
    job.built ??= budget.advance(job.work, 240, 1.2);
    if (!job.built) return;
    const { record, parts, meshes } = job, { vertices, lineFloats, buildings } = job.built;
    if (job.uploadedFloats < vertices.length) {
      const end = Math.min(vertices.length, job.uploadedFloats + uploadFloats);
      const mesh = budget.upload(this.graphics, { vertices: vertices.subarray(job.uploadedFloats, end) });
      if (!mesh) return;
      meshes.push(mesh);
      job.uploadedFloats = end;
      if (end < vertices.length) return;
    }
    // A layer becomes visible only after all of its bounded uploads have completed.
    const previous = this.overlays.get(record);
    if (previous) this.retired.push(...previous.meshes);
    this.overlays.set(record, { parts, meshes, roadMeshes: Math.ceil(lineFloats / uploadFloats),
      buildings, sampleBounds: job.sampleBounds });
    this.overlayJob = null;
  }

  overlayReady(record, parts) {
    const layer = this.overlays.get(record);
    return layer && !layer.dirty && same(parts, layer.parts);
  }

  finishOverlayHandoffs() {
    if (!this.overlayHolds.size && !this.blockedOverlays.size) return;
    this.blockedOverlays.clear();
    // A retiring owner and its successors must never draw the same footprint together.
    for (const [record, successors] of this.overlayHolds) {
      if (successors.every(next => this.overlayReady(next, this.overlayTargets.get(next)))) {
        this.retired.push(...this.overlays.get(record).meshes);
        this.overlays.delete(record);
        this.overlayHolds.delete(record);
      } else for (const next of successors) {
        this.blockedOverlays.add(next);
      }
    }
  }

  updateLabelPoints(now, changed) {
    if (!changed && now < (this.labelRefresh ?? 0) && Math.hypot(
      wrapAtlasX(this.x - this.labelX), this.z - this.labelZ) < 800) return;
    this.labelRefresh = now + 160;
    this.labelX = this.x;
    this.labelZ = this.z;
    const candidates = [];
    const offer = point => {
      if (!Number.isFinite(point.x) || !Number.isFinite(point.z) || !point.name) return;
      const dx = wrapAtlasX(point.x - this.x), dz = point.z - this.z;
      const distance = dx * dx + dz * dz;
      if (distance > labelRange ** 2) return;
      const last = candidates.at(-1);
      if (candidates.length >= 48 && (point.rank > last.rank || point.rank === last.rank && distance >= last.distance)) return;
      const i = candidates.findIndex(p => point.rank < p.rank || point.rank === p.rank && distance < p.distance);
      candidates.splice(i < 0 ? candidates.length : i, 0, { ...point, distance });
      if (candidates.length > 48) candidates.pop();
    };
    for (const { index } of this.chunks.values()) {
      if (!index || distanceToBounds(index.bounds, this.x, this.z) > labelRange) continue;
      for (const point of index.labels) offer(point);
    }
    for (const field of this.navigationWorld.landingSurfaces) offer({
      name: field.name, x: field.centerX, z: field.centerZ, y: field.height, rank: 1,
    });
    const names = new Set(), points = [];
    for (const point of candidates) {
      const key = point.name.toLowerCase();
      if (names.has(key)) continue;
      const tile = this.groundAt(point.x, point.z);
      if (!tile) continue;
      const y = point.y ?? sampleAtlasTile(tile, wrapAtlasX(point.x - tile.x), point.z - tile.z) + 4;
      if (!Number.isFinite(y)) continue;
      points.push({ name: point.name, x: point.x, y, z: point.z });
      names.add(key);
      if (points.length === labelLimit) break;
    }
    this.labelPoints = points;
    this.nearbyLandmarks = points.map(point => point.name);
  }

  mapFrame() {
    return [wrapAtlasX(Math.round(this.x / mapCell) * mapCell), Math.round(this.z / mapCell) * mapCell, mapScale];
  }

  mapPoint(x, z) {
    const [centerX, centerZ, scale] = this.mapFrame();
    return [80 + wrapAtlasX(x - centerX) / scale, 80 - (z - centerZ) / scale];
  }

  mapWaterPath(now = performance.now()) {
    const [x, z, scale] = this.mapFrame();
    if (this.waterX === x && this.waterZ === z && now < this.mapRefresh) return this.mapWater;
    this.waterX = x;
    this.waterZ = z;
    this.mapRefresh = now + 900;
    const path = [];
    for (let row = 0; row < 40; row++) {
      const latitude = z - (row * 4 - 78) * scale;
      if (latitude < -atlasPole || latitude > atlasPole) continue;
      let run = -1;
      for (let col = 0; col <= 40; col++) {
        const wet = col < 40 && this.terrain.isWater(wrapAtlasX(x + (col * 4 - 78) * scale), latitude);
        if (wet && run < 0) run = col;
        if (!wet && run >= 0) {
          path.push(`M${run * 4} ${row * 4}h${(col - run) * 4}v4H${run * 4}Z`);
          run = -1;
        }
      }
    }
    return this.mapWater = path.join('');
  }

  mapSurfacePaths() { return this.mapWaterPath(); }

  diagnostics() {
    const transport = this.terrain.diagnostics?.() ?? {};
    const renderedFinest = this.active.reduce((max, record) => Math.max(max, levelOf(record.tile)), -1);
    const destination = this.destination;
    return {
      ...transport,
      renderedTiles: this.active.length,
      renderedFeatures: this.renderedFeatures ?? 0,
      renderedRoadMeshes: this.renderedRoadMeshes ?? 0,
      renderedFinest,
      renderedLandmarks: this.nearbyLandmarks,
      visible: this.active.length,
      finest: renderedFinest,
      roadMeshes: this.renderedRoadMeshes ?? 0,
      roadStyle: this.mapRoads ? 'map' : 'terrain',
      buildings: this.buildingCount,
      landmarks: this.nearbyLandmarks,
      meshCount: this.graphics.meshes?.size,
      meshBytes: this.graphics.meshBytes,
      renderWork: this.renderWork,
      destination: destination?.id ?? null,
      targetElevation: destination ? this.terrain.sample(wrapAtlasX(destination.x), destination.z) : null,
    };
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    for (const { mesh } of this.groundCache.values()) this.graphics.releaseMesh(mesh);
    for (const { mesh } of this.seams.values()) if (mesh) this.graphics.releaseMesh(mesh);
    for (const { meshes } of this.overlays.values()) this.retired.push(...meshes);
    if (this.overlayJob) this.retired.push(...this.overlayJob.meshes);
    for (const mesh of this.retired) this.graphics.releaseMesh(mesh);
    this.retired.length = 0;
    this.groundJob?.iterator.return();
    for (const { work } of this.chunks.values()) work?.return();
    this.overlayJob?.work.return();
    this.groundCache.clear();
    this.active = [];
    this.activeSet.clear();
    this.activeGrid.clear();
    this.groundChanges = [];
    this.unaligned = [];
    this.seams.clear();
    this.seamTargets.clear();
    this.overlays.clear();
    this.overlayTargets.clear();
    this.overlayHolds.clear();
    this.blockedOverlays.clear();
    this.chunks.clear();
    this.groundJob = null;
    this.overlayJob = null;
    try { this.terrain.dispose(); } finally { super.dispose(); }
  }
}
