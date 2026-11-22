import { clamp, mix } from './engine/math.js';
import { atlasGrid } from './world-atlas.js';

const { halfWidth, halfHeight, width } = atlasGrid;
const bark = [.19, .143, .095];
const broadleaf = [[.115, .267, .157], [.164, .369, .188], [.202, .404, .207]];
const evergreen = [[.079, .229, .173], [.126, .325, .228], [.148, .358, .248]];
const ring = (count, lift, width) => Array.from({ length: count }, (_, i) => {
  const scale = width?.[i] ?? 1;
  return [scale * Math.cos(i * 2 * Math.PI / count), lift[i], scale * Math.sin(i * 2 * Math.PI / count)];
});
const lobes = ring(5, [.07, -.15, .16, -.09, .12], [1, .84, .93, .78, 1]);
const boughs = ring(4, [0, -.10, .02, -.07]);
const trunk = ring(3, [0, 0, 0]);

export function atlasHash(x, z, salt = 0) {
  let h = Math.imul(x, 0x1e35a7bd) ^ Math.imul(z, 0x61c88647) ^ salt;
  h = Math.imul(h ^ h >>> 16, 0x7feb352d);
  h = Math.imul(h ^ h >>> 15, 0x846ca68b);
  return ((h ^ h >>> 16) >>> 0) / 4294967296;
}

const smooth = t => t * t * (3 - 2 * t);

export function atlasPattern(x, z, span) {
  const u = (x + halfWidth) / span, v = z / span, i = Math.floor(u), j = Math.floor(v);
  // The atlas circumference is an integer number of cells at these scales.
  const period = width / span, a = ((i % period) + period) % period, b = a + 1 === period ? 0 : a + 1;
  const s = smooth(u - i), t = smooth(v - j);
  return mix(mix(atlasHash(a, j), atlasHash(b, j), s),
    mix(atlasHash(a, j + 1), atlasHash(b, j + 1), s), t);
}

export function atlasTree(y, z, forest, i, j) {
  const latitude = Math.abs(z) / halfHeight;
  const cool = smooth(clamp((latitude - .28) / .40, 0, 1));
  const alpine = clamp((y - 620) / 1750, 0, 1);
  const needle = atlasHash(i, j, 737) < (.07 + .65 * cool + .19 * alpine) * (forest ? 1 : .47);
  const height = (forest ? 15 : 12) + (forest ? 17 : 13) * atlasHash(i, j, 1987);
  const radius = height * (needle ? .29 : .49);
  const spin = atlasHash(i, j, 5147) * 2 * Math.PI;
  return { needle, height, radius, spin, tint: .86 + .25 * atlasHash(i, j, 3049) };
}

export function drawAtlasTree(mesh, x, y, z, tree, ground) {
  const { needle, radius, height, spin, tint } = tree;
  const sin = Math.sin(spin), cos = Math.cos(spin);
  const point = ([u, offset, v], r, h) => [x + r * (u * cos - v * sin), y + h + offset * r,
    z + r * (u * sin + v * cos)];
  const tints = (needle ? evergreen : broadleaf).map(colour => colour.map(value => value * tint));
  const stem = [x, y + height * (needle ? .51 : .52), z];
  const roots = trunk.map(v => {
    const root = point(v, height * .028, 0);
    root[1] = ground(root[0], root[2]) - .46;
    return root;
  });
  for (let i = 0; i < roots.length; i++) mesh.triangle(roots[i], stem, roots[(i + 1) % roots.length], bark, 0);
  if (needle) {
    const lower = boughs.map(v => point(v, radius, height * .29));
    const upper = boughs.map(v => point(v, radius * .67, height * .52));
    const lowerTip = [x + radius * .1 * sin, y + height * .82, z + radius * .08 * cos];
    const apex = [x - radius * .1 * cos, y + height, z + radius * .09 * sin];
    for (let i = 0; i < lower.length; i++) {
      const next = (i + 1) % lower.length;
      mesh.triangle(lower[i], lowerTip, lower[next], tints[i % 3 ? 0 : 1], 0);
      mesh.triangle(upper[i], apex, upper[next], tints[i % 2 + 1], 0);
    }
  } else {
    const belt = lobes.map(v => point(v, radius, height * .74));
    const apex = [x + radius * .26 * sin, y + height, z + radius * .22 * cos];
    for (let i = 0; i < belt.length; i++) {
      const next = (i + 1) % belt.length;
      mesh.triangle(belt[i], apex, belt[next], tints[i % 2 + 1], 0);
      mesh.triangle(belt[next], stem, belt[i], tints[0], 0);
    }
  }
}
