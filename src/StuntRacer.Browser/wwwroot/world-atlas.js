export const atlasGrid = Object.freeze({
  id: 'eqc-wft1-v1', halfWidth: 19922944, halfHeight: 9961472, width: 39845888,
});

const { halfWidth, halfHeight, width } = atlasGrid;
const columns = 2432, rows = 1217, baseStep = 16384, side = 33, segments = 32;
const baseLength = 16 + columns * rows * 3, tileLength = 16 + side * side * 3;
const maxEntries = 360000, maxFeatureBytes = 1024 * 1024;
const maxSplits = 22, maxCachedGroups = 64, maxFeatureChunks = 48;
const maxCachedFeatures = maxFeatureChunks + 12;
const littleEndian = new Uint8Array(Uint16Array.of(1).buffer)[0] === 1;
const stepAt = level => baseStep >> (level + 1);
const spanAt = level => segments * stepAt(level);
const clamp = (value, low, high) => Math.min(high, Math.max(low, value));
const keyOf = (level, x, z) => `${level}/${x}/${z}`;
const slug = value => typeof value === 'string' && value.length <= 64 && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
const label = (value, limit) => typeof value === 'string' && value.trim().length !== 0 && value.length <= limit &&
  !/[\u0000-\u001f\u007f]/.test(value);
const reserved = (x, r) => x >= r ? x - 2 * r : x < -r ? x + 2 * r : x;
export const wrapAtlasX = x => reserved(x % width, halfWidth);
export const atlasCoordinates = (x, z) => [wrapAtlasX(x) * 180 / halfWidth, clamp(z, -halfHeight, halfHeight) * 90 / halfHeight];

function validKey(level, x, z) {
  if (!Number.isInteger(level) || level < -1 || level > 8 || !Number.isInteger(x) || !Number.isInteger(z))
    return false;
  const span = spanAt(level), a = halfWidth / span, b = halfHeight / span;
  return -a <= x && x < a && -b <= z && z < b;
}

function spec(level, tx, tz) {
  const step = stepAt(level), span = segments * step;
  return { key: keyOf(level, tx, tz), level, tx, tz, step, span, x: tx * span, z: tz * span };
}

function at(level, x, z) {
  const span = spanAt(level);
  return keyOf(level, Math.floor(wrapAtlasX(x) / span),
    clamp(Math.floor(z / span), -halfHeight / span, halfHeight / span - 1));
}

const child = (parent, i) => spec(parent.level + 1, parent.tx * 2 + (i & 1), parent.tz * 2 + (i >> 1));
const childrenOf = parent => [0, 1, 2, 3].map(i => child(parent, i));
const parentKey = (level, x, z) => keyOf(level - 1, Math.floor(x / 2), Math.floor(z / 2));

function distanceTo(tile, x, z) {
  const a = Math.max(0, Math.abs(wrapAtlasX(tile.x + tile.span / 2 - x)) - tile.span / 2);
  const b = Math.max(0, Math.abs(tile.z + tile.span / 2 - z) - tile.span / 2);
  return Math.hypot(a, b);
}

function windowAt(level, x, z, radius) {
  const span = spanAt(level), a = halfWidth / span, b = halfHeight / span, items = [];
  const top = clamp(Math.floor((z + radius) / span), -b, b - 1);
  for (let tz = clamp(Math.floor((z - radius) / span), -b, b - 1); tz <= top; ++tz)
    for (let tx = Math.floor((x - radius) / span); tx <= Math.floor((x + radius) / span); ++tx)
      items.push(spec(level, reserved(tx, a), tz));
  return items;
}

function nearest(tiles, x, z) {
  const center = tile => Math.hypot(wrapAtlasX(tile.x + tile.span / 2 - x), tile.z + tile.span / 2 - z);
  return tiles.sort((a, b) => distanceTo(a, x, z) - distanceTo(b, x, z) ||
    a.level - b.level || center(a) - center(b));
}

async function boundedBytes(request, url, limit, signal, cache) {
  const response = await request(url, { signal, cache, redirect: 'error' });
  if (!response.ok) {
    const error = new Error(`Atlas data returned HTTP ${response.status}.`);
    error.status = response.status;
    throw error;
  }
  if (!response.body) throw new Error('Atlas data is empty.');
  const reader = response.body.getReader();
  const chunks = [];
  let length = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.length;
    if (length > limit) {
      await reader.cancel();
      throw new Error('Atlas data exceeds its size limit.');
    }
    chunks.push(value);
  }
  if (chunks.length === 1) return chunks[0];
  const result = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

const utf8 = new TextDecoder('utf-8', { fatal: true });
const json = bytes => JSON.parse(utf8.decode(bytes));
const validCredit = credit => {
  if (!Array.isArray(credit) || credit.length !== 3 || !label(credit[0], 384) ||
      !label(credit[1], 100) || !label(credit[2], 512)) return false;
  try {
    const url = new URL(credit[2]);
    return url.protocol === 'https:' && !url.username && !url.password;
  } catch { return false; }
};

export async function readAtlasCatalog(path, { fetch: request = globalThis.fetch, signal } = {}) {
  const root = new URL('.', import.meta.url), url = new URL(path, root);
  if (url.origin !== root.origin || !url.pathname.startsWith(root.pathname))
    throw new Error('The Atlas catalog must remain inside this game mount.');
  const doc = json(await boundedBytes(request, url, 5 * 1024 * 1024, signal, 'no-store'));
  if (doc?.kind !== 'world-atlas' || doc.schemaVersion !== 1 || doc.grid !== atlasGrid.id ||
      typeof doc.revision !== 'string' || !/^[0-9a-f]{64}$/.test(doc.revision) ||
      !Array.isArray(doc.packs) || !doc.packs.length || doc.packs.length > 64 || doc.packs[0]?.id !== 'base')
    throw new Error('Invalid World Flight Atlas catalog.');

  const packIds = new Set(), groups = new Set(), features = new Map(), presets = [];
  const baseHash = doc.packs[0].baseSha256;
  if (typeof baseHash !== 'string' || !/^[0-9a-f]{64}$/.test(baseHash)) throw new Error('Invalid Atlas base identity.');
  let total = 0;
  for (const pack of doc.packs) {
    if (pack?.kind !== 'world-atlas-pack' || pack.schemaVersion !== 1 || pack.grid !== atlasGrid.id ||
        !slug(pack.id) || packIds.has(pack.id) || !label(pack.name, 100) || pack.baseSha256 !== baseHash ||
        !Array.isArray(pack.terrain) || !Array.isArray(pack.features) || !Array.isArray(pack.presets) ||
        pack.presets.length > 128 || !pack.credits || typeof pack.credits !== 'object' || Array.isArray(pack.credits) ||
        !Object.entries(pack.credits).every(([id, credit]) => slug(id) && validCredit(credit)))
      throw new Error('Invalid World Flight Atlas pack index.');
    packIds.add(pack.id);
    const keys = new Set();
    const packGroups = new Map();
    for (const box of pack.terrain) {
      if (!Array.isArray(box) || box.length !== 5) throw new Error('Invalid Atlas terrain inventory.');
      const [level, w, s, e, n] = box;
      if (level < 1 || !Number.isInteger(e) || !Number.isInteger(n) || w >= e || s >= n ||
          !validKey(level, w, s) || !validKey(level, e - 1, n - 1) || (total += (e - w) * (n - s)) > maxEntries)
        throw new Error('Invalid Atlas terrain extent.');
      for (let tz = s; tz < n; ++tz) for (let tx = w; tx < e; ++tx) {
        const key = keyOf(level, tx, tz);
        if (keys.has(key)) throw new Error('Duplicate Atlas terrain key in one pack.');
        keys.add(key);
        const parent = parentKey(level, tx, tz);
        packGroups.set(parent, (packGroups.get(parent) ?? 0) + 1);
      }
    }
    for (const [key, count] of packGroups) {
      if (count !== 4 || !key.startsWith('0/') && !keys.has(key)) throw new Error('Atlas terrain hierarchy is incomplete.');
      // The host verifies duplicate keys byte-for-byte before publishing a revision.
      groups.add(key);
    }
    const featureKeys = new Set(), presetIds = new Set();
    for (const row of pack.features) {
      if (++total > maxEntries || !Array.isArray(row) || row.length !== 3 ||
          ![0, 5].includes(row[0]) || !validKey(...row)) throw new Error('Invalid Atlas feature inventory.');
      const key = keyOf(...row);
      if (featureKeys.has(key)) throw new Error('Duplicate Atlas feature chunk.');
      featureKeys.add(key);
      if (!features.has(key)) features.set(key, []);
      features.get(key).push(pack);
    }
    for (const preset of pack.presets) {
      if (!slug(preset?.id) || presetIds.has(preset.id) || !label(preset.name, 100) ||
          !Number.isFinite(preset.lon) || Math.abs(preset.lon) > 180 ||
          !Number.isFinite(preset.lat) || Math.abs(preset.lat) > 90)
        throw new Error('Invalid Atlas preset.');
      presetIds.add(preset.id);
      presets.push({ id: preset.id, name: preset.name, lon: preset.lon, lat: preset.lat, key: `${pack.id}/${preset.id}`, area: pack.name });
    }
  }
  return { revision: doc.revision, baseHash, packs: doc.packs, groups, features, presets,
    assets: new URL(`${doc.revision}/`, url) };
}

function heightAndWater(bytes, size) {
  let heights;
  const water = bytes.subarray(16 + 2 * size);
  if (littleEndian && bytes.byteOffset % 2 === 0) {
    heights = new Int16Array(bytes.buffer, bytes.byteOffset + 16, size);
  } else {
    const data = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    heights = new Int16Array(size);
    for (let i = 0; i < size; ++i) heights[i] = data.getInt16(16 + 2 * i, true);
  }
  for (let i = 0; i < size; ++i) {
    const h = heights[i];
    if (h < -12000 || h > 10000 || water[i] > 2)
      throw new Error('Atlas elevation contains an invalid height or water code.');
  }
  return { heights, water };
}

export function decodeAtlasBase(bytes) {
  if (bytes.byteLength !== baseLength) throw new Error('Invalid WFA1 fallback size.');
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (v.getUint32(0) !== 0x57464131 || v.getUint32(4, true) !== columns ||
      v.getUint32(8, true) !== rows || v.getUint32(12, true) !== baseStep)
    throw new Error('Invalid WFA1 fallback header.');
  return heightAndWater(bytes, columns * rows);
}

export async function readAtlasBase(catalog, { fetch: request = globalThis.fetch, signal } = {}) {
  const bytes = await boundedBytes(request, new URL('base.bin', catalog.assets), baseLength, signal);
  if (globalThis.crypto?.subtle) {
    const hash = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', bytes));
    if (Array.from(hash, value => value.toString(16).padStart(2, '0')).join('') !== catalog.baseHash)
      throw new Error('World Flight Atlas fallback identity does not match the catalog.');
  }
  return { bytes, base: decodeAtlasBase(bytes) };
}

function decodeTile(bytes, tile) {
  if (bytes.byteLength !== tileLength) throw new Error(`Invalid WFT1 size for ${tile.key}.`);
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (v.getUint32(0) !== 0x57465431 || v.getInt32(4, true) !== tile.level ||
      v.getInt32(8, true) !== tile.tx || v.getInt32(12, true) !== tile.tz)
    throw new Error(`Invalid WFT1 coordinates for ${tile.key}.`);
  return { ...tile, ...heightAndWater(bytes, side * side) };
}

function sampleGrid(grid, cols, count, u, v, wrap) {
  const i = Math.min(Math.floor(u), cols - (wrap ? 1 : 2)), j = Math.min(Math.floor(v), count - 2);
  const tx = u - i, tz = v - j;
  const a = j * cols + i, c = j * cols + (wrap && i === cols - 1 ? 0 : i + 1);
  const b = a + cols, d = c + cols, h = grid.heights;
  const y = tx <= tz ? (1 - tz) * h[a] + (tz - tx) * h[b] + tx * h[d]
    : (1 - tx) * h[a] + tz * h[d] + (tx - tz) * h[c];
  return [y, grid.water[(tx < .5 ? a : c) + (tz < .5 ? 0 : cols)]];
}

function sampleBase(base, x, z) {
  const u = (wrapAtlasX(x) + halfWidth) / baseStep;
  return sampleGrid(base, columns, rows, u < columns ? u : 0,
    (clamp(z, -halfHeight, halfHeight) + halfHeight) / baseStep, true);
}

function sampleTile(tile, x, z) {
  return sampleGrid(tile, side, side,
    clamp((x - tile.x) / tile.step, 0, segments), clamp((z - tile.z) / tile.step, 0, segments), false);
}

function fallbackTile(base, tile) {
  const heights = tile.level === -1 ? new Int16Array(side * side) : new Float32Array(side * side);
  const water = new Uint8Array(side * side);
  for (let j = 0; j < side; ++j) for (let i = 0; i < side; ++i) {
    const n = j * side + i, x = tile.x + i * tile.step, z = tile.z + j * tile.step;
    if (tile.level === -1) {
      const sample = (z + halfHeight) / baseStep * columns + ((x + halfWidth) / baseStep) % columns;
      heights[n] = base.heights[sample];
      water[n] = base.water[sample];
    } else {
      const [height, wet] = sampleBase(base, x, z);
      heights[n] = height;
      water[n] = wet;
    }
  }
  return { ...tile, heights, water };
}

const featureStyles = {
  road: ['motorway', 'trunk', 'primary', 'secondary', 'tertiary', 'local', 'service', 'path'],
  rail: ['rail', 'light-rail', 'tram'],
  place: ['capital', 'city', 'town', 'village', 'hamlet'],
  landmark: ['peak', 'historic', 'monument', 'attraction', 'viewpoint', 'stadium', 'natural'],
  airport: ['airport', 'airfield'],
  building: ['building', 'commercial', 'industrial', 'residential'],
  landuse: ['forest', 'park', 'grass'],
};

function* decodeFeatures(data, tile, pack) {
  if (data?.schemaVersion !== 1 || !Array.isArray(data.tile) || data.tile.length !== 3 ||
      data.tile[0] !== tile.level || data.tile[1] !== tile.tx || data.tile[2] !== tile.tz ||
      !Array.isArray(data.rows) || data.rows.length > 4096)
    throw new Error(`Invalid Atlas feature chunk ${pack.id}/${tile.key}.`);
  let count = 0;
  const invalid = `Invalid source, kind/style, or clipped coordinates in Atlas feature chunk ${pack.id}/${tile.key}.`;
  for (const row of data.rows) {
    if (!Array.isArray(row) || row.length < 5 || row.length > 6) throw new Error('Invalid Atlas feature row.');
    const [kind, style, name, source, points, height] = row;
    if (row.length === 6 && (kind !== 'building' || source !== 'osm' || tile.level !== 5 ||
        !Number.isInteger(height) || height < 1 || height > 1000))
      throw new Error(`Invalid building height in Atlas feature chunk ${pack.id}/${tile.key}.`);
    const point = ['place', 'landmark', 'airport'].includes(kind);
    const min = point ? 1 : ['road', 'rail'].includes(kind) ? 2
      : ['building', 'landuse'].includes(kind) ? 3 : Infinity;
    if (typeof kind !== 'string' || !Object.hasOwn(featureStyles, kind) || !featureStyles[kind].includes(style) ||
        name !== null && !label(name, 160) ||
        !(source === 'osm' || tile.level === 0 && source === 'natural-earth') || !Object.hasOwn(pack.credits, source) ||
        !Array.isArray(points) || points.length < min || points.length > (point ? 1 : 2048) ||
        (count += points.length) > 32768)
      throw new Error(invalid);
    for (let i = 0; i < points.length; ++i) {
      const p = points[i];
      if (!Array.isArray(p) || p.length !== 2 ||
          !Number.isInteger(p[0]) || p[0] < 0 || p[0] > tile.span ||
          !Number.isInteger(p[1]) || p[1] < 0 || p[1] > tile.span)
        throw new Error(invalid);
      if ((i & 127) === 127) yield;
    }
    yield;
  }
  return { ...tile, key: `${pack.id}/${tile.key}`, rows: data.rows };
}

function remember(map, key, value, limit) {
  map.delete(key);
  map.set(key, value);
  while (map.size > limit) map.delete(map.keys().next().value);
  return value;
}

export class WorldAtlasTerrain {
  constructor(catalog, base, bridge = {}, { fetch: request = globalThis.fetch } = {}) {
    this.catalog = catalog;
    this.base = base;
    this.bridge = bridge;
    this.request = request;
    this.abort = new AbortController();
    this.fallback = new Map();
    this.groups = new Map();
    this.loading = new Map();
    this.splits = new Map();
    this.active = new Map();
    this.featureChunks = new Map();
    this.featureLoading = new Map();
    this.errors = new Map();
    this.deniedTerrain = this.deniedFeatures = 0;
    this.status = '';
    this.pending = null;
    this.warmed = new Set();
    this.last = {};
    this.timing = { frameMaxMilliseconds: 0, tileDecodeMaxMilliseconds: 0, nativeSwapMaxMilliseconds: 0,
      featureParseMaxMilliseconds: 0, featureWorkMaxMilliseconds: 0 };
  }

  coarse(tile) {
    return remember(this.fallback, tile.key, this.fallback.get(tile.key) ?? fallbackTile(this.base, tile), 72);
  }

  at(x, z) {
    let tile;
    for (let level = 1; level <= 8; ++level) {
      const next = this.active.get(at(level, x, z));
      if (!next) break;
      tile = next;
    }
    return tile;
  }

  hit(x, z) {
    const tile = this.at(x, z);
    return tile ? sampleTile(tile, wrapAtlasX(x), z) : sampleBase(this.base, x, z);
  }

  sample(x, z) { return this.hit(x, z)[0]; }
  isWater(x, z) { return this.hit(x, z)[1] !== 0; }

  ready(key) {
    if (this.pending?.descriptors.some(tile => tile.key === key)) this.warmed.add(key);
  }

  canChange(parent, replacement, x, z) {
    const aircraft = this.bridge.aircraft?.();
    if (parent.level < 0 || !aircraft || distanceTo(parent, x, z) > 1500 + 6 * aircraft.speed) return true;
    const east = clamp(parent.x + wrapAtlasX(x - parent.x), parent.x, parent.x + parent.span);
    const north = clamp(z, parent.z, parent.z + parent.span);
    const target = replacement ? replacement[Number(east >= parent.x + parent.span / 2) +
      2 * Number(north >= parent.z + parent.span / 2)] : parent;
    const candidate = target.level > 0 ? sampleTile(target, east, north)[0] : sampleBase(this.base, east, north)[0];
    return !aircraft.onGround &&
      aircraft.y + 3 * Math.min(0, aircraft.verticalSpeed ?? 0) - Math.max(this.sample(x, z), candidate) >= 450;
  }

  adjust(stage) {
    const { parent, children, packed } = stage;
    if (!children) {
      this.retire(parent);
      return;
    }
    if (parent.level >= 0) {
      const start = performance.now();
      this.bridge.install?.(parent.level, parent.tx, parent.tz, packed);
      this.recordTiming('nativeSwapMaxMilliseconds', start);
      for (const tile of children) this.active.set(tile.key, tile);
    }
    this.splits.set(parent.key, { parent, children });
  }

  retire(parent) {
    const start = performance.now();
    if (parent.level >= 0) this.bridge.remove?.(parent.level, parent.tx, parent.tz);
    else for (const c of childrenOf(parent))
      if (this.splits.has(c.key)) this.bridge.remove?.(c.level, c.tx, c.tz);
    this.recordTiming('nativeSwapMaxMilliseconds', start);
    const remove = tile => {
      const split = this.splits.get(tile.key);
      if (split) for (const c of split.children) {
        this.active.delete(c.key);
        remove(c);
      }
      this.splits.delete(tile.key);
    };
    remove(parent);
  }

  trees(roots) {
    const leaves = [], branches = [];
    const visit = tile => {
      const split = this.splits.get(tile.key);
      if (!split) { leaves.push(tile); return; }
      branches.push(tile);
      split.children.forEach(visit);
    };
    roots.forEach(visit);
    return { leaves, branches };
  }

  shouldRefine(tile, x, z, far) {
    if (tile.level < 0) {
      if (![0, 1, 2, 3].some(i => this.catalog.groups.has(child(tile, i).key))) return false;
    } else if (!this.catalog.groups.has(tile.key)) return false;
    return distanceTo(tile, x, z) <= Math.min(far, 2200 + .45 * tile.span);
  }

  loadGroup(parent) {
    if (this.loading.has(parent.key) || this.loading.size >= 2 || this.errors.has(`t/${parent.key}`)) return;
    const abort = new AbortController(), targets = childrenOf(parent);
    const job = { parent, abort, targets, children: [] };
    const signal = AbortSignal.any([this.abort.signal, abort.signal, AbortSignal.timeout(12000)]);
    this.loading.set(parent.key, job);
    Promise.all(targets.map(tile =>
      boundedBytes(this.request, new URL(`tiles/${tile.key}.bin`, this.catalog.assets), tileLength, signal)))
      .then(bytes => {
        if (this.loading.get(parent.key) === job) job.bytes = bytes;
      }).catch(error => {
        abort.abort();
        if (this.loading.get(parent.key) !== job) return;
        this.loading.delete(parent.key);
        this.noteError('t', parent.key, error);
      });
  }

  advanceTileDecode(x, z, far) {
    let ready, distance = Infinity;
    for (const [key, job] of this.loading) {
      const d = distanceTo(job.parent, x, z);
      if (d > far + 35000) {
        this.loading.delete(key);
        job.abort.abort();
      } else if (job.bytes && d < distance) {
        ready = job;
        distance = d;
      }
    }
    if (!ready) return;
    const start = performance.now(), { parent, children, bytes, targets } = ready;
    try {
      const i = children.length;
      children.push(decodeTile(bytes[i], targets[i]));
      if (children.length === 4) {
        const packed = new Uint8Array(4 * tileLength);
        bytes.forEach((tile, i) => packed.set(tile, i * tileLength));
        remember(this.groups, parent.key, { children, packed }, maxCachedGroups);
        this.loading.delete(parent.key);
      }
    } catch (error) {
      this.loading.delete(parent.key);
      this.noteError('t', parent.key, error);
    } finally { this.recordTiming('tileDecodeMaxMilliseconds', start); }
  }

  noteError(kind, key, error) {
    remember(this.errors, `${kind}/${key}`, performance.now() + (error.status === 404 ? Infinity : 30000), 256);
    if (kind === 't') this.deniedTerrain++;
    else this.deniedFeatures++;
  }

  loadFeatures(tile, pack, key) {
    const job = { tile, pack, key };
    this.featureLoading.set(key, job);
    const signal = AbortSignal.any([this.abort.signal, AbortSignal.timeout(12000)]);
    boundedBytes(this.request, new URL(`features/${key}.json`, this.catalog.assets), maxFeatureBytes, signal)
      .then(bytes => { if (this.featureLoading.get(key) === job) job.bytes = bytes; })
      .catch(error => {
        if (this.featureLoading.get(key) !== job) return;
        this.featureLoading.delete(key);
        this.noteError('f', key, error);
      });
  }

  advanceFeatureDecode() {
    for (const [key, job] of this.featureLoading) {
      if (!job.bytes && !job.work) continue;
      const start = performance.now();
      const timing = job.work ? 'featureWorkMaxMilliseconds' : 'featureParseMaxMilliseconds';
      try {
        if (!job.work) {
          job.work = decodeFeatures(json(job.bytes), job.tile, job.pack);
          job.bytes = null;
        } else {
          // Yield validation back to rendering even if several JSON downloads finished together.
          for (let i = 0; i < 384 && (i % 16 !== 0 || performance.now() < start + 2); ++i) {
            const step = job.work.next();
            if (!step.done) continue;
            remember(this.featureChunks, key, step.value, maxCachedFeatures);
            this.featureLoading.delete(key);
            break;
          }
        }
      } catch (error) {
        this.featureLoading.delete(key);
        this.noteError('f', key, error);
      } finally { this.recordTiming(timing, start); }
      break;
    }
  }

  recordTiming(key, start) {
    this.timing[key] = Math.max(this.timing[key], performance.now() - start);
  }

  updateFeatures(x, z, far) {
    const result = [];
    for (const [level, radius] of [[0, Math.min(far, 245000)], [5, Math.min(far, 18000)]]) {
      let considered = 0;
      for (const tile of nearest(windowAt(level, x, z, radius), x, z)) {
        if (distanceTo(tile, x, z) > radius) break;
        for (const pack of this.catalog.features.get(tile.key) ?? []) {
          if (considered++ >= maxFeatureChunks / 2) break;
          const key = `${pack.id}/${tile.key}`, chunk = this.featureChunks.get(key);
          if (chunk) result.push(remember(this.featureChunks, key, chunk, maxCachedFeatures));
          else if (this.featureLoading.size < 4 && !this.featureLoading.has(key) && !this.errors.has(`f/${key}`))
            this.loadFeatures(tile, pack, key);
        }
        if (considered >= maxFeatureChunks / 2) break;
      }
    }
    return nearest(result, x, z);
  }

  frame(x, z, far, now) {
    if (this.abort.signal.aborted) return { tiles: [], features: [] };
    const start = performance.now();
    x = wrapAtlasX(x);
    z = clamp(z, -halfHeight, halfHeight);
    far = clamp(far, 1000, 850000);
    for (const [key, retry] of this.errors)
      if (retry <= now) this.errors.delete(key);
    this.advanceTileDecode(x, z, far);
    this.advanceFeatureDecode();
    const roots = nearest(windowAt(-1, x, z, far), x, z).map(tile => this.coarse(tile));
    const visibleRoots = new Set(roots.map(tile => tile.key));
    for (const { parent } of this.splits.values())
      if (parent.level === -1 && !visibleRoots.has(parent.key) && distanceTo(parent, x, z) > far + 35000)
        this.retire(parent);

    if (this.pending) {
      const stage = this.pending, distance = distanceTo(stage.parent, x, z);
      if (distance > far || !this.canChange(stage.parent, stage.children, x, z)) {
        this.pending = null;
        this.warmed.clear();
      } else if (stage.descriptors.every(tile => this.warmed.has(tile.key))) {
        this.adjust(stage);
        this.pending = null;
        this.warmed.clear();
      }
    }

    const { leaves, branches } = this.trees(roots);
    const wanted = nearest(leaves.filter(tile => this.shouldRefine(tile, x, z, far)), x, z);
    let deferred = 0;
    if (!this.pending) {
      let promotion;
      for (const parent of wanted) {
        const decoded = parent.level < 0 ? { children: childrenOf(parent).map(tile => this.coarse(tile)) }
          : this.groups.get(parent.key);
        if (!decoded) { this.loadGroup(parent); continue; }
        if (!this.canChange(parent, decoded.children, x, z)) { ++deferred; continue; }
        promotion ??= { parent, ...decoded };
      }
      const depart = nearest(branches.filter(tile => this.splits.get(tile.key).children.every(c => !this.splits.has(c.key))),
        x, z).reverse().find(tile => {
        const distance = distanceTo(tile, x, z);
        return (distance > 6000 + .8 * tile.span ||
          this.splits.size >= maxSplits && promotion && distance > distanceTo(promotion.parent, x, z) + 2000) &&
          this.canChange(tile, null, x, z);
      });
      if (depart) this.pending = { parent: depart, descriptors: [{ ...depart, pending: true }] };
      else if (promotion && this.splits.size < maxSplits) {
        const { parent, children, packed } = promotion;
        if (packed) remember(this.groups, parent.key, { children, packed }, maxCachedGroups);
        this.pending = { ...promotion, descriptors: children.map(tile => ({ ...tile, pending: true })) };
      }
    }

    const tiles = nearest(leaves, x, z);
    if (this.pending) tiles.push(...nearest(this.pending.descriptors.slice(), x, z));
    const features = this.updateFeatures(x, z, far);
    const ground = this.hit(x, z), detailLevel = this.at(x, z)?.level ?? -1;
    this.last = { groundHeight: ground[0], water: ground[1] !== 0, detailLevel, deferred,
      tiles: tiles.filter(tile => !tile.pending).map(tile => tile.key),
      pendingTiles: tiles.filter(tile => tile.pending).map(tile => tile.key),
      features: features.map(feature => feature.key) };
    this.status = deferred ? 'Low altitude: holding current terrain' : this.loading.size ? 'Streaming regional terrain'
      : detailLevel < 0 && this.deniedTerrain ? 'Global terrain active; some regional detail is unavailable' : '';
    this.recordTiming('frameMaxMilliseconds', start);
    return { tiles, features };
  }

  diagnostics() {
    return { revision: this.catalog.revision, packs: this.catalog.packs.map(p => p.id),
      baseLoaded: !this.abort.signal.aborted, terrainPending: this.loading.size, featurePending: this.featureLoading.size,
      deniedTerrain: this.deniedTerrain, deniedFeatures: this.deniedFeatures, transportTiming: { ...this.timing }, ...this.last };
  }

  dispose() {
    this.abort.abort();
    this.pending = null;
    this.warmed.clear();
    for (const cache of [this.loading, this.featureLoading, this.fallback, this.groups, this.splits, this.active,
      this.featureChunks, this.errors]) cache.clear();
  }
}
