import { cross, sub } from './math.js';

export function triangulate(points) {
  if (points.length < 3 || points.some(point => point.length !== 3 || !point.every(Number.isFinite)))
    throw new Error('A source polygon needs at least three finite 3D vertices.');
  let normal;
  for (let i = 1; i < points.length - 1 && !normal; i++) {
    for (let j = i + 1; j < points.length; j++) {
      const candidate = cross(sub(points[i], points[0]), sub(points[j], points[0]));
      if (Math.hypot(...candidate) > 1e-8) { normal = candidate; break; }
    }
  }
  if (!normal) return [];
  const drop = normal.map(Math.abs).indexOf(Math.max(...normal.map(Math.abs)));
  const p = points.map(point => point.filter((_, axis) => axis !== drop));
  const area = p.reduce((sum, a, i) => {
    const b = p[(i + 1) % p.length];
    return sum + a[0] * b[1] - a[1] * b[0];
  }, 0);
  if (Math.abs(area) < 1e-8) return [];
  const sign = Math.sign(area), indices = points.map((_, i) => i), result = [];
  const turn = (a, b, c) => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
  while (indices.length > 3) {
    let found = false;
    for (let j = 0; j < indices.length; j++) {
      const a = indices[(j + indices.length - 1) % indices.length], b = indices[j],
        c = indices[(j + 1) % indices.length];
      const bend = turn(p[a], p[b], p[c]) * sign;
      if (Math.abs(bend) < 1e-8) {
        const between = (p[b][0] - p[a][0]) * (p[b][0] - p[c][0]) +
          (p[b][1] - p[a][1]) * (p[b][1] - p[c][1]) <= 1e-8;
        if (between) { indices.splice(j, 1); found = true; break; }
        continue;
      }
      if (bend < 0) continue;
      const inside = indices.some(i => i !== a && i !== b && i !== c &&
        turn(p[a], p[b], p[i]) * sign >= -1e-8 && turn(p[b], p[c], p[i]) * sign >= -1e-8 &&
        turn(p[c], p[a], p[i]) * sign >= -1e-8);
      if (inside) continue;
      result.push([a, b, c]);
      indices.splice(j, 1);
      found = true;
      break;
    }
    if (!found) throw new Error('The original polygon cannot be triangulated without changing its outline.');
  }
  if (indices.length === 3) result.push([...indices]);
  return result;
}
