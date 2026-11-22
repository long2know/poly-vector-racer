import { Geometry } from './engine/graphics.js';
import { clamp } from './engine/math.js';
import { triangulate } from './engine/triangulate.js';
import { atlasGrid, wrapAtlasX } from './world-atlas.js';

const atlasPole = atlasGrid.halfHeight;
const side = 33, cells = side - 1;
const sea = [.061, .216, .302], lake = [.087, .306, .336], shore = [.27, .37, .246];
const lowland = [.246, .375, .22], upland = [.214, .329, .209], highland = [.33, .351, .252];
const rock = [.39, .386, .343], snow = [.78, .84, .85];

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
  let next = 0;
  const write = (x, y, z, nx, ny, nz, colour, material) => {
    vertices[next++] = x; vertices[next++] = y; vertices[next++] = z;
    vertices[next++] = nx; vertices[next++] = ny; vertices[next++] = nz;
    vertices[next++] = colour[0]; vertices[next++] = colour[1]; vertices[next++] = colour[2];
    vertices[next++] = material;
  };
  for (let j = 0; j < cells; j++) {
    yield;
    const south = j * step, north = south + step;
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
        const wet = wetFacet(tile, a, mid, c), material = wet > 0 ? 5 : 0;
        const colour = wet === 2 ? lake : wet === 1 ? sea : wet === -1 ? shore :
          landColour((sw + ne + hm) / 3, Math.sqrt(slope2), bands);
        write(west, sw, south, nx * norm, norm, nz * norm, colour, material);
        if (first) write(west, hm, north, nx * norm, norm, nz * norm, colour, material);
        write(east, ne, north, nx * norm, norm, nz * norm, colour, material);
        if (!first) write(east, hm, south, nx * norm, norm, nz * norm, colour, material);
      }
    }
  }
  return { vertices };
}

const atlasFeatureTriangles = 4096;

class FeatureGeometry {
  data = new Float32Array(atlasFeatureTriangles * 30);
  floatCount = 0;

  triangle(a, b, c, colour, material) {
    const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
    const vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
    const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    const unit = 1 / (Math.hypot(nx, ny, nz) || 1);
    const v = this.data;
    for (const point of [a, b, c]) {
      let i = this.floatCount;
      v[i++] = point[0]; v[i++] = point[1]; v[i++] = point[2];
      v[i++] = nx * unit; v[i++] = ny * unit; v[i++] = nz * unit;
      v[i++] = colour[0]; v[i++] = colour[1]; v[i++] = colour[2]; v[i++] = material;
      this.floatCount = i;
    }
  }

  quad(a, b, c, d, colour, material) {
    this.triangle(a, b, c, colour, material);
    this.triangle(a, c, d, colour, material);
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
  forest: [0, [.156, .337, .193]], park: [0, [.23, .421, .233]], grass: [0, [.32, .433, .245]],
};
const pointRanks = { landmark: 0, airport: 1, place: 2 };
const maxRows = 4096, maxPoints = 32768, maxRing = 40, maxFootprint = 8192;
const lineTiers = 3, lineGroups = 7;

export function* indexAtlasFeatures(chunk) {
  const lines = Array.from({ length: lineTiers }, () => Array.from({ length: lineGroups }, () => []));
  const buildings = [], landuse = [], labels = [], byHeight = new Map();
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
    const area = (kind === 'building' || kind === 'landuse') && Object.hasOwn(areas, style) ? areas[style] : null;
    if (!(line && path.length >= 2 || area && path.length >= 3 && path.length <= maxRing)) continue;
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
    buildings: ranked.length ? ranked.concat(buildings) : buildings, taggedCount: ranked.length, landuse, labels };
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

function appendTop(mesh, points, colour, material, limit) {
  for (let i = 1; i < points.length - 1 && mesh.floatCount < limit; i++) {
    const a = points[0], b = points[i], c = points[i + 1];
    const area = (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]);
    if (Math.abs(area) >= 1e-5) mesh.triangle(a, ...(area > 0 ? [b, c] : [c, b]), colour, material);
  }
}

function* drape(mesh, tile, polygon, lift, colour, material, limit) {
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
        appendTop(mesh, raised, colour, material, limit);
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

function* drawBuilding(mesh, tile, part, entry, limit, sampleAdjacent) {
  const [west, south, east, north] = toLocal(entry.bounds, tile), span = tile.step * cells;
  const cx = (west + east) * .5, cz = (south + north) * .5;
  const owns = (x, z) => x >= 0 && x < span && z >= 0 && z < span;
  // The midpoint owns the complete footprint, including any corners past its tile edge.
  if (!owns(cx, cz) || !dryAt(tile, cx, cz) || Math.max(east - west, north - south) > maxFootprint) return false;
  const { points, faces } = polygonFaces(entry.path, part.chunk, tile);
  if (!faces.length) return false;
  const colour = entry.style[1];
  const roofColour = colour.map(c => c * 1.1), walls = colour.map(c => c * .84);
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
  const roof = points.map(([x, z]) => [x, top, z]);
  for (const face of faces) appendTop(mesh, face.map(i => roof[i]), roofColour, 0, limit);
  for (let i = 0; i < points.length && mesh.floatCount + 60 <= limit; i++) {
    yield;
    const next = (i + 1) % points.length, a = roof[i], b = roof[next];
    if (a[0] === b[0] && a[2] === b[2]) continue;
    const [x, , z] = a, [u, , v] = b;
    mesh.quad([x, base[i] - .5, z], a, b, [u, base[next] - .5, v], walls, 0);
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
        if (!atlasOverlap(entry.bounds, bounds)) continue;
        const { points, faces } = polygonFaces(entry.path, part.chunk, tile);
        for (const face of faces) yield* drape(mesh, tile, clipBox(face.map(i => points[i]), clipTo), .31, entry.style[1], 0, parkLimit);
      }
    }
  }
  return { vertices: mesh.data.subarray(0, mesh.floatCount), lineFloats, buildings };
}
