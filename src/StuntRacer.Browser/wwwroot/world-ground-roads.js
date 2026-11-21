// The widths are game artwork over the existing Seattle centerlines, not surveyed lanes.
export const seattleSidewalks = Object.freeze({
  local: Object.freeze({ inner: 6.48, outer: 7.65 }),
  arterial: Object.freeze({ inner: 9.18, outer: 10.55 }),
});
export const seattleSidewalkLift = .24;

const asphaltLift = .075, paintLift = .10, backBevel = .14;
const widths = { local: 6.3, arterial: 9, freeway: 10.4 };
const materials = {
  asphalt: { material: 12, colour: [.104, .111, .123], wetLimit: 2 },
  yellow: { material: 13, colour: [.94, .62, .14], wetLimit: 1 },
  white: { material: 13, colour: [.82, .83, .77], wetLimit: 1 },
  sidewalk: { material: 14, colour: [.43, .418, .382], wetLimit: 1 },
  curb: { material: 15, colour: [.62, .605, .55], wetLimit: 1 },
  back: { material: 15, colour: [.34, .334, .30], wetLimit: 1 },
};
const envelope = kind => (seattleSidewalks[kind]?.outer ?? widths[kind]) + backBevel;
const cross = (a, b, c) => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
const dot = (a, b) => a.dx * b.dx + a.dz * b.dz;

export function roadBounds(polygon) {
  let x0 = Infinity, z0 = Infinity, x1 = -Infinity, z1 = -Infinity;
  for (const [x, z] of polygon) {
    x0 = Math.min(x0, x); z0 = Math.min(z0, z);
    x1 = Math.max(x1, x); z1 = Math.max(z1, z);
  }
  return [x0, z0, x1, z1];
}

function hull(points) {
  points.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const sweep = order => {
    const edge = [];
    for (const p of order) {
      while (edge.length > 1 && cross(edge.at(-2), edge.at(-1), p) <= 1e-8) edge.pop();
      edge.push(p);
    }
    return edge.slice(0, -1);
  };
  return [...sweep(points), ...sweep(points.reverse())];
}

function crossSection(arm, distance, width) {
  return [arm.point[0] + distance * arm.dx + width * arm.mx,
    arm.point[1] + distance * arm.dz + width * arm.mz, asphaltLift];
}

function cornerDistance(arm, other, width) {
  const cosine = dot(arm, other);
  if (cosine < -.92) return 0;
  const sine = Math.abs(arm.dx * other.dz - arm.dz * other.dx);
  return Math.min(24, (width(other.kind) + width(arm.kind) * cosine) / Math.max(sine, .05));
}

function planNode(arms) {
  if (arms.length < 2) return null;
  if (arms.length > 8) {
    // Ambiguous coincidence: keep the original mouths, but no detailed finish on them.
    for (const arm of arms) arm.trim = 24;
    return null;
  }
  const [a, b] = arms;
  const cosine = dot(a, b);
  if (arms.length === 2 && a.kind === b.kind) {
    a.continues = b; b.continues = a;
    if (cosine < -.42) {
      const mx = (b.dz - a.dz) / (1 - cosine), mz = (a.dx - b.dx) / (1 - cosine);
      const skew = Math.abs(mx * a.dx + mz * a.dz) * envelope(a.kind);
      if (skew <= .44 * Math.min(a.segment.length, b.segment.length)) {
        a.mx = mx; a.mz = mz; b.mx = -mx; b.mz = -mz;
        a.miter = b.miter = true;
        return null;
      }
    }
  }
  arms.sort((a, b) => Math.atan2(a.dz, a.dx) - Math.atan2(b.dz, b.dx));
  const pad = arms.length === 2 && a.kind === b.kind && cosine < -.77 ? .7 : 3.4;
  const corners = [];
  for (let i = 0; i < arms.length; i++) {
    const arm = arms[i], before = arms[(i + arms.length - 1) % arms.length], after = arms[(i + 1) % arms.length];
    const corner = width => Math.max(cornerDistance(arm, before, width), cornerDistance(arm, after, width));
    const half = widths[arm.kind];
    arm.cut = Math.min(arm.segment.length * .43, Math.max(pad, corner(kind => widths[kind])));
    arm.trim = Math.max(pad, corner(envelope) + .85);
    corners.push(crossSection(arm, arm.cut, -half), crossSection(arm, arm.cut, half));
  }
  return hull(corners);
}

function band(segment, start, end, low, high, lowY, highY = lowY) {
  const { a, b, length } = segment, span = length - a.cut - b.cut;
  const from = (distance, width, lift) => {
    const t = (distance - a.cut) / span;
    return [a.point[0] + distance * a.dx + width * ((1 - t) * a.mx - t * b.mx),
      a.point[1] + distance * a.dz + width * ((1 - t) * a.mz - t * b.mz), lift];
  };
  return [from(start, low, lowY), from(end, low, lowY), from(end, high, highY), from(start, high, highY)];
}

function addGrid(map, span, box, entry) {
  const [left, bottom, right, top] = box.map(n => Math.floor(n / span));
  for (let z = bottom; z <= top; z++) for (let x = left; x <= right; x++) {
    const key = `${x}/${z}`;
    if (!map.has(key)) map.set(key, []);
    map.get(key).push(entry);
  }
}

const barrierCell = 128;
export function* indexGroundRoads(rows, tileSpan) {
  const nodes = new Map(), segments = [], barriers = new Map(), buckets = [new Map(), new Map()];
  const index = (entry, box) => buckets.forEach((map, i) => addGrid(map, tileSpan(i + 7), box, entry));
  const joinAt = (point, arm) => {
    const key = `${point[0]}/${point[1]}`;
    if (!nodes.has(key)) nodes.set(key, []);
    nodes.get(key).push(arm);
  };
  let work = 0;
  for (const [, kind, structure, line] of rows) {
    let phase = 0;
    for (let i = 1; i < line.length; i++) {
      const p = line[i - 1], q = line[i], dx = q[0] - p[0], dz = q[1] - p[1], length = Math.hypot(dx, dz);
      const ux = dx / length, uz = dz / length;
      if (structure === 'bridge' || kind === 'freeway') {
        const pad = widths[kind] + 1;
        addGrid(barriers, barrierCell, [Math.min(p[0], q[0]) - pad, Math.min(p[1], q[1]) - pad,
          Math.max(p[0], q[0]) + pad, Math.max(p[1], q[1]) + pad], { p, ux, uz, length, pad });
      }
      if (structure === 'surface') {
        const a = { point: p, kind, dx: ux, dz: uz, mx: -uz, mz: ux, cut: 0, trim: 0 };
        const b = { point: q, kind, dx: -ux, dz: -uz, mx: uz, mz: -ux, cut: 0, trim: 0 };
        const segment = { a, b, kind, length, phase };
        a.other = b; b.other = a; a.segment = b.segment = segment;
        joinAt(p, a); joinAt(q, b);
        segments.push(segment);
      }
      phase += length;
      if (++work % 64 === 0) yield;
    }
  }
  for (const arms of nodes.values()) {
    // Freeway mergers are not local intersections; bridge decks never participate.
    for (const group of [arms.filter(arm => arm.kind !== 'freeway'), arms.filter(arm => arm.kind === 'freeway')]) {
      const patch = planNode(group);
      if (patch?.length >= 3) index(patch, roadBounds(patch));
    }
    if (++work % 64 === 0) yield;
  }
  for (const segment of segments) {
    if (segment.kind !== 'freeway') for (const end of [segment.a, segment.b]) {
      // Short source segments do not reset an intersection setback.
      for (let arm = end, distance = end.trim, hops = 0; arm && distance > 0 && hops < 64; hops++) {
        arm.skip = Math.max(arm.skip ?? 0, distance);
        distance -= arm.segment.length;
        arm = arm.other.continues;
      }
    }
    const width = envelope(segment.kind);
    index(segment, roadBounds(band(segment, segment.a.cut, segment.length - segment.b.cut, -width, width, 0)));
    if (++work % 64 === 0) yield;
  }
  return { buckets, barriers };
}

export function emitGroundPavement(entry, emit) {
  if (Array.isArray(entry)) return emit(entry, materials.asphalt);
  const width = widths[entry.kind];
  return emit(band(entry, entry.a.cut, entry.length - entry.b.cut, -width, width, asphaltLift), materials.asphalt);
}

function detailSpans(segment, start, end, barriers) {
  const { a, b, kind } = segment, p = a.point, q = b.point, nearby = new Set();
  const width = envelope(kind) * Math.max(Math.hypot(a.mx, a.mz), Math.hypot(b.mx, b.mz));
  const x = Math.floor((Math.min(p[0], q[0]) - width) / barrierCell);
  const z = Math.floor((Math.min(p[1], q[1]) - width) / barrierCell);
  const right = Math.floor((Math.max(p[0], q[0]) + width) / barrierCell);
  const top = Math.floor((Math.max(p[1], q[1]) + width) / barrierCell);
  for (let j = z; j <= top; j++) for (let i = x; i <= right; i++) {
    for (const barrier of barriers.get(`${i}/${j}`) ?? []) {
      nearby.add(barrier);
      if (nearby.size > 128) return [];
    }
  }
  const masked = [];
  for (const barrier of nearby) {
    const { p: origin, ux, uz, length, pad } = barrier, dx = p[0] - origin[0], dz = p[1] - origin[1];
    const margin = pad + width;
    let lo = start, hi = end;
    for (const [at, along, min, max] of [
      [dx * ux + dz * uz, a.dx * ux + a.dz * uz, -margin, length + margin],
      [dz * ux - dx * uz, a.dz * ux - a.dx * uz, -margin, margin],
    ]) {
      if (Math.abs(along) < 1e-8) {
        if (at < min || at > max) hi = lo;
      } else {
        const d0 = (min - at) / along, d1 = (max - at) / along;
        lo = Math.max(lo, Math.min(d0, d1)); hi = Math.min(hi, Math.max(d0, d1));
      }
    }
    if (lo < hi) masked.push([lo, hi]);
  }
  const spans = [];
  let head = start;
  for (const [lo, hi] of masked.sort((a, b) => a[0] - b[0])) {
    if (lo - head > .04) spans.push([head, lo]);
    head = Math.max(head, hi);
  }
  if (end - head > .04) spans.push([head, end]);
  return spans;
}

export function emitGroundDetails(entry, barriers, emit) {
  const sidewalk = seattleSidewalks[entry.kind];
  if (!sidewalk) return;
  const { a, b, length, phase, kind } = entry, { inner, outer } = sidewalk;
  const trim = arm => Math.max(arm.cut, arm.skip ?? (arm.miter ? 0 : .65));
  const half = widths[kind], start = trim(a), end = length - trim(b);
  if (end <= start) return;
  for (const [from, to] of entry.spans ??= detailSpans(entry, start, end, barriers)) {
    const strip = (lo, hi, y0, y1, material) => emit(band(entry, from, to, lo, hi, y0, y1), material);
    strip(half, inner, asphaltLift, seattleSidewalkLift, materials.curb);
    strip(-inner, -half, seattleSidewalkLift, asphaltLift, materials.curb);
    strip(inner, outer, seattleSidewalkLift, seattleSidewalkLift, materials.sidewalk);
    strip(-outer, -inner, seattleSidewalkLift, seattleSidewalkLift, materials.sidewalk);
    strip(outer, outer + backBevel, seattleSidewalkLift, .035, materials.back);
    strip(-outer - backBevel, -outer, .035, seattleSidewalkLift, materials.back);
    if (kind === 'arterial') {
      strip(-half + .32, -half + .50, paintLift, paintLift, materials.white);
      strip(half - .50, half - .32, paintLift, paintLift, materials.white);
      strip(-.25, -.11, paintLift, paintLift, materials.yellow);
      strip(.11, .25, paintLift, paintLift, materials.yellow);
    } else {
      for (let t = Math.floor((from + phase) / 10) * 10 - phase; t < to; t += 10) {
        const lo = Math.max(t, from), hi = Math.min(t + 3.2, to);
        if (hi > lo + .08) emit(band(entry, lo, hi, -.085, .085, paintLift), materials.yellow);
      }
    }
  }
}

export class VisibleSidewalks {
  cells = new Map();

  add(polygon) {
    addGrid(this.cells, 64, roadBounds(polygon), polygon);
  }

  has(x, z) {
    const point = [x, z];
    return (this.cells.get(`${Math.floor(x / 64)}/${Math.floor(z / 64)}`) ?? []).some(polygon => {
      let direction = 0;
      for (let i = 0; i < polygon.length; i++) {
        const side = cross(polygon[i], polygon[(i + 1) % polygon.length], point);
        if (Math.abs(side) <= 1e-6) continue;
        if (direction * side < 0) return false;
        direction = side;
      }
      return true;
    });
  }
}
