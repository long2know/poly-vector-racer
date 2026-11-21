import { lerp3, mix } from './engine/math.js';

const teal = [.095, .55, .50], glass = [.085, .17, .205], roof = [.035, .066, .080];
const cladding = [.051, .067, .075], well = [.022, .029, .034], satin = [.48, .55, .54];
const coral = [1, .075, .047], runningLight = [.75, .97, .91];

// Each station defines the shoulder and crowned deck at that distance along the body.
const stations = [
  [-2.06, .69, .29, .54, .35], [-1.97, .84, .37, .68, .47],
  [-1.77, .955, .43, .76, .52], [-1.22, .985, .44, .81, .53],
  [-.57, .925, .365, .81, .43], [.29, .92, .33, .81, .395],
  [.91, .973, .345, .80, .38], [1.30, .98, .325, .79, .365],
  [1.71, .938, .26, .77, .315], [1.94, .848, .18, .67, .24],
  [2.06, .69, .12, .55, .185],
];
const arches = [[-1.22, .65], [1.30, .67]];
const profile = [...new Set([...stations.map(([z]) => z), ...arches.flatMap(([z, radius]) =>
  Array.from({ length: 13 }, (_, i) => z + radius * Math.cos(i * Math.PI / 12)))])].sort((a, b) => a - b);
const cabin = [
  // [belt z, belt y, half belt width, roof z, roof y, half roof width]
  [-1.77, .52, .75, -1.03, .965, .61],
  [-1.08, .516, .81, -.75, 1.055, .673],
  [-.34, .438, .82, -.33, 1.118, .698],
  [.33, .395, .815, .10, 1.127, .685],
  [.94, .38, .79, .34, 1.089, .643],
];

const shade = (color, gain) => color.map(value => Math.min(1, value * gain));

function station(z) {
  const i = Math.max(1, stations.findIndex(row => row[0] >= z));
  const a = stations[i - 1], b = stations[i];
  return a.map((value, j) => mix(value, b[j], (z - a[0]) / (b[0] - a[0])));
}

function deckRow([z, width, shoulder, inset, height]) {
  return [[-width, shoulder, z], [-inset, height, z], [0, height + .023, z],
    [inset, height, z], [width, shoulder, z]];
}

function sideRow([z, width, shoulder], side) {
  const distance = Math.min(...arches.map(([center, radius]) => Math.abs(z - center) / radius));
  const curve = Math.sqrt(Math.max(0, 1 - distance * distance));
  const lip = -.33 + Math.max(0, Math.abs(z) - 1.77) * .28 + .54 * curve;
  const trim = lip + .058, inset = .077 - .085 * curve;
  return [[side * width, shoulder, z], [side * (width + .016), mix(shoulder, trim, .28), z],
    [side * (width - inset + .014), trim, z], [side * (width - inset), lip, z],
    [side * Math.min(.76, width - .105), lip + .015, z]];
}

function addDeck(mesh, color, rows) {
  for (let i = 1; i < rows.length; i++) {
    const a = deckRow(rows[i - 1]), b = deckRow(rows[i]);
    for (let j = 0; j < 4; j++)
      mesh.quad(a[j], b[j], b[j + 1], a[j + 1], shade(color, j === 0 || j === 3 ? 1 : 1.11), 2);
  }
}

function addShell(mesh, color, rows) {
  for (const side of [-1, 1]) {
    const panels = [shade(color, .97), shade(color, .77), cladding, well];
    for (let i = 1; i < rows.length; i++) {
      const a = sideRow(rows[i - 1], side), b = sideRow(rows[i], side);
      for (let j = 0; j < panels.length; j++) {
        const face = [a[j], a[j + 1], b[j + 1], b[j]];
        mesh.quad(...(side < 0 ? face : face.reverse()), panels[j], j < 2 ? 2 : 0);
      }
    }
    for (const z of [-.47, .12]) {
      const handle = dz => {
        const [a, b] = sideRow(station(z + dz), side);
        const p = lerp3(a, b, .69);
        p[0] += side * .009;
        return p;
      };
      mesh.beam(handle(-.084), handle(.084), .011, satin, 2);
    }
    for (const [z] of arches)
      mesh.beam([side * .69, .18, z], [side * .96, -.4, z], .047, satin, 2, 6);
  }
  addDeck(mesh, color, rows);
  for (const row of [rows[0], rows.at(-1)]) {
    const outline = [...sideRow(row, -1).slice(1).reverse(), ...deckRow(row), ...sideRow(row, 1).slice(1)];
    if (row[0] > 0) outline.reverse();
    for (let i = 0; i < outline.length; i++)
      mesh.triangle([0, -.025, row[0]], outline[i], outline[(i + 1) % outline.length], color, 2);
  }
  mesh.box([0, -.365, 0], [1.46, .155, 3.63], well);
}

function cabinCorner(row, side, top) {
  const i = top ? 3 : 0;
  return [side * row[i + 2], row[i + 1], row[i]];
}

function addPavilion(mesh, color) {
  for (const side of [-1, 1]) {
    for (let i = 1; i < cabin.length; i++) {
      const a = cabin[i - 1], b = cabin[i];
      const face = [cabinCorner(a, side, true), cabinCorner(a, side, false),
        cabinCorner(b, side, false), cabinCorner(b, side, true)];
      mesh.quad(...(side < 0 ? face : face.reverse()), glass, 2);
    }
    for (const [i, width] of [[0, .036], [2, .019], [4, .024]]) {
      const row = cabin[i];
      mesh.beam(cabinCorner(row, side, false), cabinCorner(row, side, true),
        width, i === 2 ? roof : shade(color, .76), 2, 6);
    }
    mesh.box([side * .855, .432, .70], [.19, .083, .16], roof, 2);
    mesh.box([side * .884, .48, .705], [.131, .03, .122], color, 2);
  }
  for (let i = 1; i < cabin.length; i++) {
    const a = cabin[i - 1], b = cabin[i], crown = row => [0, row[4] + .045, row[3]];
    mesh.quad(cabinCorner(a, -1, true), cabinCorner(b, -1, true), crown(b), crown(a), roof, 2);
    mesh.quad(crown(a), crown(b), cabinCorner(b, 1, true), cabinCorner(a, 1, true), roof, 2);
  }
  for (const row of [cabin[0], cabin.at(-1)]) {
    const sign = Math.sign(row[0]);
    const lower = [0, row[1] + .023, row[0] + sign * .037], upper = [0, row[4] + .045, row[3]];
    for (const side of [-1, 1]) {
      const face = [cabinCorner(row, side, false), lower, upper, cabinCorner(row, side, true)];
      mesh.quad(...(side === sign ? face.reverse() : face), shade(glass, side < 0 ? 1.17 : 1.06), 2);
    }
  }
}

function lens(mesh, path, direction, tint, height) {
  for (let i = 1; i < path.length; i++) {
    const edge = ([x, y, z], upper, inset) =>
      [x, y + (upper ? 1 : -1) * (height / 2 + inset), z + direction * (upper ? -.019 : .009)];
    const a = path[i - 1], b = path[i];
    for (const [inset, color, material] of [[.016, well, 0], [0, tint, 4]]) {
      const p = ([x, y, z]) => [x, y, z + (inset === 0 ? direction * .006 : 0)];
      const face = [p(edge(a, false, inset)), p(edge(b, false, inset)),
        p(edge(b, true, inset)), p(edge(a, true, inset))];
      mesh.quad(...(direction > 0 ? face : face.reverse()), color, material);
    }
  }
}

function addLamps(mesh) {
  const outer = [[.57, .303, -2.078], [.70, .323, -2.062],
    [.84, .399, -1.993], [.928, .453, -1.851]];
  lens(mesh, [...outer.map(([x, y, z]) => [-x, y, z]).reverse(), ...outer], -1, coral, .063);
  mesh.box([0, .035, -2.065], [1.2, .259, .105], cladding, 2);
  mesh.box([0, .095, -2.125], [.37, .087, .016], [.25, .31, .315], 2);
  mesh.box([0, -.15, 2.062], [1.31, .166, .113], cladding, 2);
  mesh.box([0, -.122, 2.123], [.91, .065, .014], well);
  for (const side of [-1, 1]) {
    const front = [[.34, .115, 2.072], [.65, .115, 2.067], [.79, .155, 1.998], [.863, .201, 1.913]];
    lens(mesh, (side < 0 ? [...front].reverse() : front).map(([x, y, z]) => [side * x, y, z]),
      1, runningLight, .044);
  }
}

function addCockpit(mesh, color, rows) {
  // Omit the exterior glazing and headliner: the existing centred camera sees the road through this cutaway.
  addDeck(mesh, color, rows.filter(([z]) => z >= .91));
  mesh.box([0, .274, .765], [1.62, .17, .51], well);
  mesh.quad([-.783, .36, .51], [-.80, .384, .988], [.80, .384, .988], [.783, .36, .51], cladding, 2);
  mesh.box([0, .343, .505], [1.52, .026, .024], shade(color, .80), 2);
  mesh.box([.49, .151, -.09], [.29, .27, 1.22], cladding, 2);
  mesh.box([.49, .292, .09], [.18, .018, .08], satin, 2);
  for (const side of [-1, 1]) {
    const front = cabin.at(-1);
    mesh.beam(cabinCorner(front, side, false), cabinCorner(front, side, true), .028, cladding, 2, 6);
    mesh.box([side * .805, .305, .135], [.102, .16, 1.54], cladding, 2);
  }
  for (const [x, width, height, z] of [[0, .53, .125, .626], [.49, .357, .157, .562]]) {
    mesh.box([x, .353, z + .034], [width + .034, .064, .12], roof, 2);
    const screen = (u, v, inset) => [x + width * (u - .5), .378 + v * height, z + v * .06 - inset];
    const pane = (l, b, r, t, tint, material) =>
      mesh.quad(...[[l, b], [l, t], [r, t], [r, b]].map(([u, v]) => screen(u, v, material === 4 ? .004 : 0)),
        tint, material);
    pane(0, 0, 1, 1, glass, 2);
    for (const [u, v, w] of [[.09, .66, .32], [.42, .66, .23], [.71, .66, .19], [.12, .27, .18], [.54, .27, .34]])
      pane(u, v, u + w, v + .054, runningLight, 4);
  }
}

export function buildEv6(mesh, color = teal, interior = false) {
  const rows = profile.map(station);
  if (interior) addCockpit(mesh, color, rows);
  else {
    addShell(mesh, color, rows);
    addPavilion(mesh, color);
    addLamps(mesh);
  }
  return mesh;
}
