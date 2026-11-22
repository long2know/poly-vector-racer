import { Geometry } from './engine/graphics.js';
import { clamp, mix } from './engine/math.js';
import { triangulate } from './engine/triangulate.js';
import { atlasGrid, wrapAtlasX } from './world-atlas.js';
import { atlasHash, atlasPattern, atlasTree, drawAtlasTree } from './world-atlas-scenery.js';

const atlasPole = atlasGrid.halfHeight;
const side = 33, cells = side - 1;
const sea = [.057, .217, .31], lake = [.085, .293, .304];
const seaShelf = [.093, .348, .377], lakeShelf = [.123, .379, .337], shore = [.36, .424, .263];
const lowland = [.247, .377, .219], upland = [.216, .344, .218], highland = [.337, .365, .257];
const rock = [.413, .393, .352], snow = [.78, .84, .85];

export function atlasBounds(x, z, span) { return [x, z, x + span, z + span]; }
export function atlasOverlap(a, b) { return a[0] < b[2] && a[2] > b[0] && a[1] < b[3] && a[3] > b[1]; }

function wetFacet(tile, a, b, c) {
  const w = tile.water[a], v = tile.water[b], u = tile.water[c];
  if (w + v + u === 0) return 0;
  if (Number(Boolean(w)) + Number(Boolean(v)) + Number(Boolean(u)) < 2) return -1;
  return w === 2 && v === 2 || w === 2 && u === 2 || v === 2 && u === 2 ? 2 : 1;
}

function altitudeBands(z) {
  const latitude = Math.min(1, Math.abs(z) / atlasPole), snowline = 5175 - 5360 * latitude ** 1.25;
  return [snowline, snowline - (1200 - 430 * latitude), 1280 - 810 * latitude];
}

function landColour(h, slope, [snowline, treeline, foothills]) {
  if (h > snowline + Math.min(650, slope * 470)) return snow;
  if (h > snowline - 560 || slope > .97) return rock;
  if (h > treeline || slope > .63) return highland;
  return h > foothills ? upland : lowland;
}

export function* makeAtlasGround(tile) {
  const { step, heights, z } = tile;
  const vertices = new Float32Array(cells * cells * 60);
  const tone = new Float32Array(side * 2);
  const textureRow = j => {
    const latitude = z + j * step;
    for (let i = 0; i < side; i++) {
      const x = tile.x + i * step;
      tone[(j % 2) * side + i] = .51 * atlasPattern(x, latitude, 16384) +
        .33 * atlasPattern(x, latitude, 512) + .16 * atlasPattern(x, latitude, 128) - .5;
    }
  };
  let next = 0;
  const write = (x, z, row, col, nx, ny, nz, palette, wet) => {
    const index = row * side + col, swatch = tone[(row % 2) * side + col];
    const c = wet > 0 && !tile.water[index] ? wet === 2 ? lakeShelf : seaShelf :
      wet === -1 && tile.water[index] ? shore : palette;
    const texture = swatch * (wet > 0 || palette === snow ? .12 : .44);
    vertices[next++] = x; vertices[next++] = heights[index]; vertices[next++] = z;
    vertices[next++] = nx; vertices[next++] = ny; vertices[next++] = nz;
    vertices[next++] = c[0] * (1 + texture * 1.24);
    vertices[next++] = c[1] * (1 + texture * .78);
    vertices[next++] = c[2] * (1 - texture * .34);
    vertices[next++] = wet > 0 ? 5 : 0;
  };
  yield;
  textureRow(0);
  for (let j = 0; j < cells; j++) {
    yield;
    const south = j * step, north = south + step;
    textureRow(j + 1);
    const bands = altitudeBands(z + south + step / 2);
    for (let i = 0; i < cells; i++) {
      const west = i * step, east = west + step;
      const a = j * side + i, b = a + side, c = b + 1, d = a + 1, sw = heights[a], ne = heights[c];
      for (let face = 0; face < 2; face++) {
        const first = face === 0;
        const mid = first ? b : d, hm = heights[mid];
        const nx = -(first ? ne - hm : hm - sw) / step;
        const nz = -(first ? hm - sw : ne - hm) / step;
        const slope2 = nx * nx + nz * nz, norm = 1 / Math.sqrt(1 + slope2);
        const wet = wetFacet(tile, a, mid, c);
        const colour = wet === 2 ? lake : wet === 1 ? sea :
          landColour((sw + ne + hm) / 3, Math.sqrt(slope2), bands);
        write(west, south, j, i, nx * norm, norm, nz * norm, colour, wet);
        if (first) write(west, north, j + 1, i, nx * norm, norm, nz * norm, colour, wet);
        write(east, north, j + 1, i + 1, nx * norm, norm, nz * norm, colour, wet);
        if (!first) write(east, south, j, i + 1, nx * norm, norm, nz * norm, colour, wet);
      }
    }
  }
  return { vertices };
}

const atlasFeatureTriangles = 4096;

class FeatureGeometry {
  floatCount = 0;

  constructor(triangles = atlasFeatureTriangles) {
    this.data = new Float32Array(triangles * 30);
  }

  triangle(a, b, c, colour, material, tint) {
    const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
    const vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
    const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    const unit = 1 / (Math.hypot(nx, ny, nz) || 1);
    const v = this.data;
    for (const point of [a, b, c]) {
      let i = this.floatCount;
      v[i++] = point[0]; v[i++] = point[1]; v[i++] = point[2];
      v[i++] = nx * unit; v[i++] = ny * unit; v[i++] = nz * unit;
      const t = tint ? tint(point[0], point[1], point[2]) : 1;
      v[i++] = colour[0] * t; v[i++] = colour[1] * t; v[i++] = colour[2] * t; v[i++] = material;
      this.floatCount = i;
    }
  }

  quad(a, b, c, d, colour, material, tint) {
    this.triangle(a, b, c, colour, material, tint);
    this.triangle(a, c, d, colour, material, tint);
  }
}

export function sampleAtlasTile(tile, dx, dz) {
  const { step, heights } = tile;
  const u = clamp(dx / step, 0, cells), v = clamp(dz / step, 0, cells);
  const i = Math.min(cells - 1, Math.floor(u)), j = Math.min(cells - 1, Math.floor(v));
  const a = j * side + i, x = u - i, z = v - j;
  return x <= z
    ? (1 - z) * heights[a] + (z - x) * heights[a + side] + x * heights[a + side + 1]
    : (1 - x) * heights[a] + (x - z) * heights[a + 1] + z * heights[a + side + 1];
}

export function makeAtlasSeam(tile, edge, neighbor) {
  const mesh = new Geometry(), span = tile.step * cells;
  const at = i => {
    const d = i * tile.step;
    if (edge === 0) return [0, tile.heights[i * side], d, tile.water[i * side]];
    if (edge === 1) return [span, tile.heights[i * side + cells], d, tile.water[i * side + cells]];
    if (edge === 2) return [d, tile.heights[i], 0, tile.water[i]];
    return [d, tile.heights[cells * side + i], span, tile.water[cells * side + i]];
  };
  const onNeighbor = (x, z) => [x, sampleAtlasTile(neighbor, wrapAtlasX(tile.x + x - neighbor.x), tile.z + z - neighbor.z), z];
  for (let i = 0; i < cells; i++) {
    const [x0, y0, z0, w0] = at(i), [x1, y1, z1, w1] = at(i + 1);
    const a = [x0, y0, z0], b = [x1, y1, z1], c = onNeighbor(x0, z0), d = onNeighbor(x1, z1);
    const start = a[1] - c[1], end = b[1] - d[1];
    if (Math.abs(start) < 1e-6 && Math.abs(end) < 1e-6) continue;
    const wet = w0 && w1, colour = wet ? w0 === 2 && w1 === 2 ? lake : sea :
      landColour((a[1] + b[1]) / 2, 0, altitudeBands(tile.z + (z0 + z1) / 2));
    const material = wet ? 5 : 0;
    if (start * end < 0) {
      const t = start / (start - end), m = a.map((n, axis) => n + (b[axis] - n) * t);
      mesh.triangle(a, c, m, colour, material);
      mesh.triangle(m, d, b, colour, material);
    } else {
      if (start) mesh.triangle(a, c, d, colour, material);
      if (end) mesh.triangle(a, d, b, colour, material);
    }
  }
  return mesh;
}

const roads = {
  motorway: [12, 1.66, 6, [.63, .53, .39], 0], trunk: [10, 1.52, 6, [.61, .52, .39], 0],
  primary: [8, 1.32, 7, [.55, .49, .37], 1], secondary: [6.5, 1.22, 7, [.51, .47, .37], 2],
  tertiary: [5, 1.04, 8, [.49, .47, .397], 3], local: [3.8, .8, 8, [.39, .4, .369], 4],
  service: [2.7, .74, 8, [.37, .38, .356], 5], path: [1.4, .66, 8, [.43, .45, .365], 6],
};
const rails = {
  rail: [3.3, .9, 0, [.43, .29, .21], 2], 'light-rail': [2.5, .86, 0, [.47, .30, .22], 4],
  tram: [2, .86, 0, [.48, .31, .225], 5],
};
const areas = {
  building: [8, [.65, .66, .59]], commercial: [15, [.55, .62, .63]],
  industrial: [10, [.58, .54, .49]], residential: [7, [.64, .56, .46]],
  forest: [0, [.146, .315, .186]], park: [0, [.238, .402, .212]], grass: [0, [.341, .443, .233]],
};
const cover = Object.freeze({ park: 1, forest: 2, grass: 3 });
const pointRanks = { landmark: 0, airport: 1, place: 2 };
const maxRows = 4096, maxPoints = 32768, maxRing = 40, maxCoverRing = 2048, maxFootprint = 8192;
const lineTiers = 3, lineGroups = 7;
const sceneryBin = 1024, sceneryPad = 42, sceneryTriangles = 2096, siteSpacing = 32;
const sceneryCell = (i, j) => i * 32768 + j;
const sitePhases = [[0, 0], [2, 2], [1, 1], [3, 3], [0, 2], [2, 0], [0, 1], [2, 3],
  [1, 0], [3, 2], [0, 3], [2, 1], [1, 2], [3, 0], [1, 3], [3, 1]];

function* surfaceOutline(path) {
  if (path.length <= maxRing) return path;
  const [x, z] = path[0];
  let far = 0, distance = 0, probes = 0;
  for (let i = 1; i < path.length; i++) {
    if (i % 48 === 0) yield;
    const [u, v] = path[i], d = (u - x) ** 2 + (v - z) ** 2;
    if (d > distance) { far = i; distance = d; }
  }
  const section = function* (a, b) {
    const [ax, az] = path[a], [bx, bz] = path[b % path.length];
    let at = a, error = 0;
    for (let i = a + 1; i < b; i++) {
      if (++probes % 48 === 0) yield;
      if (probes > 24000) return { error: Infinity };
      const [x, z] = path[i], d = distanceToSegment2(x, z, ax, az, bx, bz);
      if (d > error) { at = i; error = d; }
    }
    return { a, b, at, error };
  };
  const keep = [0, far], sections = [yield* section(0, far), yield* section(far, path.length)];
  while (true) {
    let best = 0;
    for (let i = 1; i < sections.length; i++) if (sections[i].error > sections[best].error) best = i;
    const { a, b, at, error } = sections[best];
    if (error <= 2 ** 2 || keep.length === maxRing) {
      // Large rings still retain their full outlines for planting; only the floor can be simplified.
      return error <= 10 ** 2 && keep.length >= 3 ? keep.sort((a, b) => a - b).map(i => path[i]) : null;
    }
    if (!Number.isFinite(error)) return null;
    keep.push(at);
    sections.splice(best, 1, yield* section(a, at), yield* section(at, b));
  }
}

function binScenery(index, entry) {
  const [w, s, e, n] = entry.bounds;
  const i0 = Math.floor(w / sceneryBin), j0 = Math.floor(s / sceneryBin);
  const i1 = Math.floor(e / sceneryBin), j1 = Math.floor(n / sceneryBin);
  if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4) {
    index.broad.push(entry);
    return;
  }
  for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) {
    const key = sceneryCell(i, j);
    let bucket = index.bins.get(key);
    if (!bucket) index.bins.set(key, bucket = []);
    bucket.push(entry);
  }
}

function* sceneryEntries(index, bounds) {
  const [w, s, e, n] = bounds, seen = new Set();
  for (const entry of index.broad) yield entry;
  for (let j = Math.floor(s / sceneryBin); j <= Math.floor(n / sceneryBin); j++)
    for (let i = Math.floor(w / sceneryBin); i <= Math.floor(e / sceneryBin); i++)
      for (const entry of index.bins.get(sceneryCell(i, j)) ?? []) {
        if (seen.has(entry)) continue;
        seen.add(entry);
        yield entry;
      }
}

function sceneryShift(bounds, tileBounds) {
  const dx = (bounds[0] + bounds[2] - tileBounds[0] - tileBounds[2]) * .5;
  return wrapAtlasX(dx) - dx;
}

export function atlasSceneryIntersects(bounds, tileBounds) {
  const shift = sceneryShift(bounds, tileBounds);
  return bounds[0] + shift - sceneryPad <= tileBounds[2] && bounds[2] + shift + sceneryPad >= tileBounds[0] &&
    bounds[1] - sceneryPad <= tileBounds[3] && bounds[3] + sceneryPad >= tileBounds[1];
}

export function* indexAtlasFeatures(chunk) {
  const lines = Array.from({ length: lineTiers }, () => Array.from({ length: lineGroups }, () => []));
  const buildings = [], landuse = [], labels = [], byHeight = new Map();
  const scenery = { bins: new Map(), broad: [], plantBounds: null };
  let points = 0, lineMask = 0, highest = 0;
  for (let i = 0; i < Math.min(chunk.rows.length, maxRows); i++) {
    yield;
    const row = chunk.rows[i];
    if (!Array.isArray(row) || (row.length !== 5 && row.length !== 6)) continue;
    const [kind, style, name, source, path, height] = row, tagged = row.length === 6;
    if (tagged && (kind !== 'building' || source !== 'osm' || chunk.level !== 5 ||
        !Number.isInteger(height) || height < 1 || height > 1000)) continue;
    if (!Array.isArray(path) || (points += path.length) > maxPoints) break;
    if (source !== 'natural-earth' && source !== 'osm') continue;
    if (Object.hasOwn(pointRanks, kind)) {
      const p = path[0];
      if (path.length === 1 && validAtlasPoint(p, chunk.span) && typeof name === 'string' && labels.length < 384) {
        const title = name.slice(0, 120).trim();
        if (title) labels.push({ x: chunk.x + p[0], z: chunk.z + p[1], name: kind === 'airport' &&
          !/\b(airport|airfield|aerodrome)\b/i.test(title) ? `${title} · airport` : title,
        rank: kind === 'place' && (style === 'capital' || style === 'city') ? 2 : kind === 'place' ? 3 : pointRanks[kind] });
      }
      continue;
    }
    const line = kind === 'road' && Object.hasOwn(roads, style) ? roads[style] :
      kind === 'rail' && Object.hasOwn(rails, style) ? rails[style] : null;
    const validArea = kind === 'building' && Object.hasOwn(areas, style) && areas[style][0] ||
      kind === 'landuse' && Object.hasOwn(cover, style);
    const area = validArea ? areas[style] : null;
    if (!(line && path.length >= 2 || area && path.length >= 3 &&
        path.length <= (kind === 'landuse' ? maxCoverRing : maxRing))) continue;
    const bounds = [Infinity, Infinity, -Infinity, -Infinity];
    let valid = true;
    for (let j = 0; j < path.length; j++) {
      if (j > 0 && j % 16 === 0) yield;
      const p = path[j];
      if (!validAtlasPoint(p, chunk.span)) { valid = false; break; }
      const x = chunk.x + p[0], z = chunk.z + p[1];
      bounds[0] = Math.min(bounds[0], x); bounds[1] = Math.min(bounds[1], z);
      bounds[2] = Math.max(bounds[2], x); bounds[3] = Math.max(bounds[3], z);
    }
    if (!valid) continue;
    const entry = { kind, source, path, bounds, style: line ?? area };
    if (kind === 'landuse') {
      entry.cover = cover[style];
      entry.surface = yield* surfaceOutline(path);
      if (source === 'osm' && style !== 'grass') {
        const p = scenery.plantBounds ??= [...bounds];
        p[0] = Math.min(p[0], bounds[0]); p[1] = Math.min(p[1], bounds[1]);
        p[2] = Math.max(p[2], bounds[2]); p[3] = Math.max(p[3], bounds[3]);
      }
    }
    binScenery(scenery, entry);
    if (line) {
      const overview = chunk.level === 0 || chunk.level === undefined && chunk.span >= 262144;
      const tier = source === 'natural-earth' ? 0 : overview ? 1 : 2, group = line[4];
      lineMask |= 1 << tier;
      entry.category = kind === 'rail' ? style === 'rail' ? 'rail' : 'transit' :
        group === 0 ? 'express-road' : group <= 2 ? 'through-road' : group === 3 ? 'connector' : 'local-road';
      lines[tier][group].push(entry);
    } else if (kind === 'building' && area[0]) {
      entry.height = tagged ? height : area[0];
      if (tagged) {
        if (!byHeight.has(height)) byHeight.set(height, []);
        byHeight.get(height).push(entry);
        highest = Math.max(highest, height);
      } else {
        buildings.push(entry);
      }
    } else if (kind === 'landuse' && !area[0]) landuse.push(entry);
  }
  const ranked = [];
  for (let height = highest; height > 0; height--) {
    if (height % 32 === 0) yield;
    const group = byHeight.get(height);
    if (!group) continue;
    for (const entry of group) {
      if (ranked.length % 32 === 0) yield;
      ranked.push(entry);
    }
  }
  return { chunk, bounds: atlasBounds(chunk.x, chunk.z, chunk.span), lines, lineMask,
    buildings: ranked.length ? ranked.concat(buildings) : buildings, taggedCount: ranked.length, landuse, labels, scenery };
}

function validAtlasPoint(p, span) {
  return Array.isArray(p) && p.length === 2 && p.every(n => Number.isInteger(n) && n >= 0 && n <= span);
}

function clip(polygon, distance) {
  if (polygon.length === 0) return polygon;
  const result = [];
  let a = polygon.at(-1), da = distance(a);
  for (const b of polygon) {
    const db = distance(b);
    if ((da < 0) !== (db < 0)) {
      const t = da / (da - db);
      result.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]);
    }
    if (db >= 0) result.push(b);
    a = b; da = db;
  }
  return result;
}

const clipBox = (p, [w, s, e, n]) => clip(clip(clip(clip(p, q => q[0] - w), q => e - q[0]), q => q[1] - s), q => n - q[1]);
const toLocal = (p, t) => [p[0] - t.x, p[1] - t.z, p[2] - t.x, p[3] - t.z];
const inRange = ([aw, as, ae, an], [bw, bs, be, bn], width = 0) =>
  aw - width <= be && ae + width >= bw && as - width <= bn && an + width >= bs;
const toGround = (p, chunk, tile) => [p[0] + chunk.x - tile.x, p[1] + chunk.z - tile.z];
const pointsBounds = p => p.reduce((b, q) =>
  [Math.min(b[0], q[0]), Math.min(b[1], q[1]), Math.max(b[2], q[0]), Math.max(b[3], q[1])], [Infinity, Infinity, -Infinity, -Infinity]);

function appendTop(mesh, points, colour, material, limit, tint) {
  for (let i = 1; i < points.length - 1 && mesh.floatCount < limit; i++) {
    const a = points[0], b = points[i], c = points[i + 1];
    const area = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
    if (Math.abs(area) >= 1e-5) mesh.triangle(a, ...(area > 0 ? [b, c] : [c, b]), colour, material, tint);
  }
}

function* drape(mesh, tile, polygon, lift, colour, material, limit, tint) {
  if (polygon.length < 3) return;
  const { step } = tile, range = pointsBounds(polygon);
  const firstRow = Math.max(0, Math.floor(range[1] / step));
  const lastRow = Math.min(cells - 1, Math.floor(range[3] / step));
  for (let j = firstRow; j <= lastRow && mesh.floatCount < limit; j++) {
    yield;
    const s = j * step;
    const strip = clip(clip(polygon, p => p[1] - s), p => s + step - p[1]);
    if (strip.length < 3) continue;
    const box = pointsBounds(strip);
    const firstCol = Math.max(0, Math.floor(box[0] / step));
    const lastCol = Math.min(cells - 1, Math.floor(box[2] / step));
    for (let i = firstCol; i <= lastCol && mesh.floatCount < limit; i++) {
      yield;
      const w = i * step, a = j * side + i, c = a + side + 1;
      const cell = clip(clip(strip, p => p[0] - w), p => w + step - p[0]);
      for (const sign of [1, -1]) {
        if (wetFacet(tile, a, c, a + (sign === 1 ? side : 1)) > 0) continue;
        const face = clip(cell, p => sign * (p[1] - s - p[0] + w));
        const raised = face.map(([x, z]) => [x, sampleAtlasTile(tile, x, z) + lift, z]);
        appendTop(mesh, raised, colour, material, limit, tint);
      }
    }
  }
}

function* outsidePatches(polygon, boxes) {
  let pieces = [polygon];
  for (const box of boxes) {
    const next = [];
    const [w, s, e, n] = box;
    const planes = [p => p[0] - w, p => e - p[0], p => p[1] - s, p => n - p[1]];
    for (let piece of pieces) {
      yield;
      if (next.length >= 32) break;
      if (!atlasOverlap(pointsBounds(piece), box)) { next.push(piece); continue; }
      for (const distance of planes) {
        const outer = clip(piece, p => -distance(p));
        if (outer.length >= 3 && next.length < 32) next.push(outer);
        piece = clip(piece, distance);
        if (piece.length < 3) break;
      }
    }
    pieces = next;
    if (!pieces.length) break;
  }
  return pieces;
}

const unit = (a, b) => {
  if (!a || !b) return null;
  const x = b[0] - a[0], z = b[1] - a[1], length = Math.hypot(x, z);
  return length ? [x / length, z / length] : null;
};

function join(before, after, direction, width) {
  const a = before ?? direction, b = after ?? direction;
  const n = unit([0, 0], [-a[1] - b[1], a[0] + b[0]]) ?? [-direction[1], direction[0]];
  const length = width / Math.max(.6, -direction[1] * n[0] + direction[0] * n[1]);
  return [n[0] * length, n[1] * length];
}

function* drawLine(mesh, tile, part, line, exclusions, limit, tier) {
  const { chunk, bounds } = part, { path } = line;
  const [rawWidth, rawLift, material, colour] = line.style;
  const coarse = tier < 2;
  const width = coarse ? Math.max(rawWidth, Math.min(86, tile.step * .037)) : rawWidth;
  const lift = coarse ? Math.max(rawLift, Math.min(6, tile.step * .004)) : rawLift;
  if (!inRange(line.bounds, atlasBounds(tile.x, tile.z, tile.step * cells), width * 2)) return;
  const onBoundary = p => p[0] === 0 || p[1] === 0 || p[0] === chunk.span || p[1] === chunk.span;
  const [w, s, e, n] = toLocal(bounds, tile), span = tile.step * cells;
  const localBox = [Math.max(0, w), Math.max(0, s), Math.min(span, e), Math.min(span, n)];
  if (exclusions?.some(([w, s, e, n]) => w <= localBox[0] && s <= localBox[1] && e >= localBox[2] && n >= localBox[3])) return;
  for (let i = 1; i < path.length && mesh.floatCount < limit; i++) {
    yield;
    const a = path[i - 1], b = path[i], dir = unit(a, b);
    if (!dir) continue;
    const p = toGround(a, chunk, tile), q = toGround(b, chunk, tile);
    const pPad = i === 1 && onBoundary(a) ? width * 2 : 0, qPad = i === path.length - 1 && onBoundary(b) ? width * 2 : 0;
    const pa = join(unit(path[i - 2], a), dir, dir, width), pb = join(dir, unit(b, path[i + 1]), dir, width);
    const corner = (center, offset, extend, side) =>
      [center[0] + offset[0] * side + dir[0] * extend, center[1] + offset[1] * side + dir[1] * extend];
    const quad = clipBox([corner(p, pa, -pPad, 1), corner(p, pa, -pPad, -1),
      corner(q, pb, qPad, -1), corner(q, pb, qPad, 1)], localBox);
    if (quad.length < 3) continue;
    const polygons = exclusions?.length ? yield* outsidePatches(quad, exclusions) : [quad];
    for (const polygon of polygons) yield* drape(mesh, tile, polygon, lift, colour, material, limit);
  }
}

function polygonFaces(path, chunk, tile) {
  const points = path.map(p => toGround(p, chunk, tile));
  try {
    const faces = triangulate(points.map(([x, z]) => [x, 0, z]));
    return { points, faces };
  } catch {
    return { points, faces: [] };
  }
}

function dryAt(tile, x, z) {
  const i = clamp(Math.round(x / tile.step), 0, cells), j = clamp(Math.round(z / tile.step), 0, cells);
  return tile.water[j * side + i] === 0;
}

function insideRing(points, x, z) {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [a, b] = points[j], [c, d] = points[i];
    if (x >= Math.min(a, c) && x <= Math.max(a, c) && z >= Math.min(b, d) && z <= Math.max(b, d) &&
        (x - a) * (d - b) === (z - b) * (c - a)) return true;
    if ((b > z) !== (d > z) && x < a + (c - a) * (z - b) / (d - b)) inside = !inside;
  }
  return inside;
}

function* scenerySites(sites, grid, [w, s, e, n], pad, visit, granular = false) {
  const { pitch, west, south, cols, rows } = grid;
  const first = Math.max(0, Math.floor((s - pad) / pitch) - south);
  const last = Math.min(rows - 1, Math.floor((n + pad) / pitch) - south);
  const left = Math.max(0, Math.floor((w - pad) / pitch) - west);
  const right = Math.min(cols - 1, Math.floor((e + pad) / pitch) - west);
  for (let j = first; j <= last; j++) {
    yield;
    for (let i = left; i <= right; i++) {
      const site = sites[j * cols + i];
      if (!site || site.blocked || site.x < w - pad || site.x > e + pad || site.z < s - pad || site.z > n + pad) continue;
      if (granular) yield;
      visit(site);
    }
  }
}

function distanceToSegment2(x, z, ax, az, bx, bz) {
  const dx = bx - ax, dz = bz - az, length2 = dx * dx + dz * dz;
  const t = length2 ? clamp(((x - ax) * dx + (z - az) * dz) / length2, 0, 1) : 0;
  return (x - ax - t * dx) ** 2 + (z - az - t * dz) ** 2;
}

function dryCrown(tile, x, z, radius) {
  const { step, water } = tile;
  const left = Math.floor((x - radius) / step), right = Math.ceil((x + radius) / step);
  const bottom = Math.floor((z - radius) / step), top = Math.ceil((z + radius) / step);
  if (left < 0 || bottom < 0 || right > cells || top > cells) return false;
  for (let j = bottom; j <= top; j++) for (let i = left; i <= right; i++) if (water[j * side + i]) return false;
  return true;
}

export function* makeAtlasScenery(tile, parts) {
  const { x, z, step } = tile, span = step * cells, bounds = atlasBounds(x, z, span);
  const pitch = step <= 32 ? siteSpacing : step <= 64 ? 2 * siteSpacing : 4 * siteSpacing;
  const west = Math.floor(x / pitch), south = Math.floor(z / pitch);
  const cols = Math.ceil(bounds[2] / pitch) - west, rows = Math.ceil(bounds[3] / pitch) - south;
  const grid = { pitch, west, south, cols, rows }, sites = new Array(cols * rows);
  const rootHeight = (dx, dz) => sampleAtlasTile(tile, dx, dz);
  for (let j = 0; j < rows; j++) {
    yield;
    for (let i = 0; i < cols; i++) {
      // Coarser ground cuts take a subset of the same jittered world sites.
      const gx = (west + i) * pitch / siteSpacing, gz = (south + j) * pitch / siteSpacing;
      const u = gx * siteSpacing + siteSpacing * (.17 + .66 * atlasHash(gx, gz, 681));
      const v = gz * siteSpacing + siteSpacing * (.17 + .66 * atlasHash(gx, gz, 439));
      if (u >= x && u < bounds[2] && v >= z && v < bounds[3]) sites[j * cols + i] = { x: u, z: v, gx, gz, cover: 0 };
    }
  }
  const blockers = [];
  for (const part of parts) {
    const shift = sceneryShift(part.bounds, bounds), cx = part.chunk.x + shift, cz = part.chunk.z;
    const search = [x - shift - sceneryPad, z - sceneryPad, bounds[2] - shift + sceneryPad, bounds[3] + sceneryPad];
    for (const entry of sceneryEntries(part.scenery, search)) {
      yield;
      if (!inRange(entry.bounds, search)) continue;
      if (entry.kind !== 'landuse') {
        blockers.push({ entry, cx, cz, shift, coarse: part.chunk.level === 0 });
      } else if (entry.source === 'osm' && shift === 0 && atlasOverlap(entry.bounds, bounds)) {
        yield* scenerySites(sites, grid, entry.bounds, 0, site => {
          if (site.cover < entry.cover && insideRing(entry.path, site.x - cx, site.z - cz)) site.cover = entry.cover;
        }, entry.path.length > 128);
      }
    }
  }
  let candidates = 0;
  for (let i = 0; i < sites.length; i++) {
    yield;
    const site = sites[i];
    if (!site) continue;
    const forest = site.cover === cover.forest, park = site.cover === cover.park, { gx, gz } = site;
    if (!forest && !park) { sites[i] = null; continue; }
    const patch = .65 * atlasPattern(site.x, site.z, 128) + .35 * atlasPattern(site.x, site.z, 512);
    const density = forest ? .65 + .29 * patch : .06 + 1.8 * clamp(patch - .27, 0, .63) ** 2;
    if (atlasHash(gx, gz, 9127) > density) { sites[i] = null; continue; }
    const dx = site.x - x, dz = site.z - z, y = rootHeight(dx, dz);
    const [, treeline] = altitudeBands(site.z);
    if (y + atlasHash(gx, gz, 37) * 190 > treeline ||
        Math.abs(rootHeight(dx - 5, dz) - rootHeight(dx + 5, dz)) > 11 ||
        Math.abs(rootHeight(dx, dz - 5) - rootHeight(dx, dz + 5)) > 11) { sites[i] = null; continue; }
    const tree = atlasTree(y, site.z, forest, gx, gz);
    if (!dryCrown(tile, dx, dz, tree.radius)) { sites[i] = null; continue; }
    site.y = y;
    site.tree = tree;
    candidates++;
  }
  if (!candidates) return { vertices: null, trees: 0 };

  for (const { entry, cx, cz, shift, coarse } of blockers) {
    yield;
    const { bounds: [w, s, e, n], path, kind } = entry;
    if (kind === 'building') {
      yield* scenerySites(sites, grid, [w + shift, s, e + shift, n], sceneryPad, site => {
        const tx = site.x - cx, tz = site.z - cz, r2 = (site.tree.radius + 1) ** 2;
        if (insideRing(path, tx, tz) || path.some(([a, b], i) => {
          const [u, v] = path[(i + 1) % path.length];
          return distanceToSegment2(tx, tz, a, b, u, v) < r2;
        })) site.blocked = true;
      });
      continue;
    }
    const width = (coarse ? Math.max(entry.style[0], Math.min(86, step * .037)) : entry.style[0]) * 1.8 + 1;
    for (let i = 1; i < path.length; i++) {
      yield;
      const [a, b] = path[i - 1], [c, d] = path[i];
      const segment = [cx + Math.min(a, c), cz + Math.min(b, d), cx + Math.max(a, c), cz + Math.max(b, d)];
      if (!inRange(segment, bounds, sceneryPad)) continue;
      yield* scenerySites(sites, grid, segment, sceneryPad, site => {
        if (distanceToSegment2(site.x - cx, site.z - cz, a, b, c, d) < (width + site.tree.radius) ** 2)
          site.blocked = true;
      });
    }
  }

  const mesh = new FeatureGeometry(Math.min(sceneryTriangles, candidates * 13));
  let trees = 0;
  // Spread a capped forest across the tile rather than clipping the last rows off.
  for (const [px, pz] of sitePhases)
    for (let j = pz; j < rows; j += 4) for (let i = px; i < cols; i += 4) {
      yield;
      const site = sites[j * cols + i];
      if (!site || site.blocked || mesh.floatCount + (site.tree.needle ? 11 : 13) * 30 > mesh.data.length) continue;
      drawAtlasTree(mesh, site.x - x, site.y, site.z - z, site.tree, rootHeight);
      trees++;
    }
  return { vertices: trees ? mesh.data.subarray(0, mesh.floatCount) : null, trees };
}

function* drawBuilding(mesh, tile, part, entry, limit, sampleAdjacent) {
  const [west, south, east, north] = toLocal(entry.bounds, tile), span = tile.step * cells;
  const cx = (west + east) * .5, cz = (south + north) * .5;
  const owns = (x, z) => x >= 0 && x < span && z >= 0 && z < span;
  // The midpoint owns the complete footprint, including any corners past its tile edge.
  if (!owns(cx, cz) || !dryAt(tile, cx, cz) || Math.max(east - west, north - south) > maxFootprint) return false;
  const { points, faces } = polygonFaces(entry.path, part.chunk, tile);
  if (!faces.length) return false;
  const colour = entry.style[1], variation = atlasHash(Math.floor(tile.x + cx), Math.floor(tile.z + cz), 510);
  const roofColour = colour.map((c, channel) => c * (.89 + .33 * variation) + (channel === 2 ? .018 : 0));
  const walls = colour.map(c => c * (.76 + .14 * variation));
  const floor = (x, z) => (owns(x, z) ? null : sampleAdjacent?.(tile.x + x, tile.z + z)) ?? sampleAtlasTile(tile, x, z);
  const base = points.map(([x, z]) => floor(x, z));
  let crest = Math.max(...base);
  if (!owns(west, south) || !owns(east, north)) {
    // A ridge under a seam-crossing roof can be higher than any corner. Probe a bounded local lattice.
    let grid = 32;
    while ((Math.floor((east - west) / grid) + 1) * (Math.floor((north - south) / grid) + 1) > 64) grid *= 2;
    const startX = Math.ceil((tile.x + west) / grid) * grid - tile.x;
    const startZ = Math.ceil((tile.z + south) / grid) * grid - tile.z;
    for (let z = startZ; z <= north; z += grid) for (let x = startX; x <= east; x += grid) {
      yield;
      if (insideRing(points, x, z)) crest = Math.max(crest, floor(x, z));
    }
  }
  const top = crest + entry.height;
  const facadeShade = (_, y) => mix(.78, 1.09, clamp((y - crest) / entry.height, 0, 1));
  const roof = points.map(([x, z]) => [x, top, z]);
  for (const face of faces) appendTop(mesh, face.map(i => roof[i]), roofColour, 0, limit);
  for (let i = 0; i < points.length && mesh.floatCount + 60 <= limit; i++) {
    yield;
    const next = (i + 1) % points.length, a = roof[i], b = roof[next];
    if (a[0] === b[0] && a[2] === b[2]) continue;
    const [x, , z] = a, [u, , v] = b;
    mesh.quad([x, base[i] - .5, z], a, b, [u, base[next] - .5, v], walls, 0, facadeShade);
  }
  return true;
}

function* taggedBuildings(parts) {
  const heads = parts.filter(part => part.taggedCount).map(part => ({ part, index: 0 }));
  while (heads.length) {
    let tallest = 0;
    for (let i = 1; i < heads.length; i++) {
      const { part, index } = heads[i], first = heads[tallest];
      if (part.buildings[index].height > first.part.buildings[first.index].height) tallest = i;
    }
    const head = heads[tallest];
    yield [head.part, head.part.buildings[head.index]];
    if (++head.index === head.part.taggedCount) heads.splice(tallest, 1);
  }
}

export function* makeAtlasFeatures(tile, parts, sampleAdjacent) {
  const mesh = new FeatureGeometry(), span = tile.step * cells, bounds = atlasBounds(tile.x, tile.z, span);
  const limit = atlasFeatureTriangles * 30, lineLimit = limit * .75;
  const layerMask = parts.reduce((mask, part) => mask | part.lineMask, 0), cuts = new Map();
  for (let tier = lineTiers - 1; tier >= 0; tier--) {
    if (!(layerMask & (1 << tier))) continue;
    const reserve = Number(tier > 0 && Boolean(layerMask & 1)) + Number(tier > 1 && Boolean(layerMask & 2));
    const layerLimit = lineLimit - reserve * 30 * Math.floor(atlasFeatureTriangles * .16);
    const emitted = new Map();
    for (let group = 0; group < lineGroups && mesh.floatCount < layerLimit; group++) {
      for (const part of parts) {
        for (const line of part.lines[tier][group]) {
          yield;
          if (mesh.floatCount >= layerLimit) break;
          const before = mesh.floatCount, category = line.category;
          yield* drawLine(mesh, tile, part, line, cuts.get(category), layerLimit, tier);
          if (tier > 0 && mesh.floatCount > before) {
            if (!emitted.has(category)) emitted.set(category, new Set());
            emitted.get(category).add(part);
          }
        }
      }
    }
    // Only ready replacement geometry displaces lower-tier lines of the same category.
    for (const [category, visible] of emitted)
      cuts.set(category, (cuts.get(category) ?? []).concat([...visible].map(part => toLocal(part.bounds, tile))));
  }
  const lineFloats = mesh.floatCount;
  let buildings = 0;
  // The area rows are optional; coarse tiles still show their surveyed lines and point labels.
  if (tile.step <= 256) {
    const buildingLimit = Math.floor(limit * .93 / 30) * 30;
    const appendBuilding = function* (part, entry) {
      if (yield* drawBuilding(mesh, tile, part, entry, buildingLimit, sampleAdjacent)) buildings++;
    };
    for (const [part, entry] of taggedBuildings(parts)) {
      yield;
      if (buildings >= 72 || mesh.floatCount >= buildingLimit) break;
      yield* appendBuilding(part, entry);
    }
    const vegetationTint = (x, y, z) => .87 + .26 * atlasPattern(tile.x + x, tile.z + z, 256);
    for (const part of parts) {
      const clipTo = toLocal(part.bounds, tile);
      for (let i = part.taggedCount; i < part.buildings.length && buildings < 72 && mesh.floatCount < buildingLimit; i++) {
        yield;
        yield* appendBuilding(part, part.buildings[i]);
      }
      const parkLimit = Math.min(limit, mesh.floatCount + 288 * 30);
      for (const entry of part.landuse) {
        yield;
        if (mesh.floatCount >= parkLimit) break;
        if (!entry.surface || !atlasOverlap(entry.bounds, bounds)) continue;
        const { points, faces } = polygonFaces(entry.surface, part.chunk, tile);
        const lift = entry.cover === cover.grass ? .37 : entry.cover === cover.forest ? .30 : .31;
        for (const face of faces)
          yield* drape(mesh, tile, clipBox(face.map(i => points[i]), clipTo), lift, entry.style[1], 0, parkLimit, vegetationTint);
      }
    }
  }
  return { vertices: mesh.data.subarray(0, mesh.floatCount), lineFloats, buildings };
}
