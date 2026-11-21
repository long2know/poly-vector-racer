import { Builder } from './scene.js';
import { triangulate } from './engine/triangulate.js';

const charcoal = [.042, .055, .064];
const shade = (colour, amount) => colour.map(value => value * amount);

function ring(x, y, z, width, height, sides) {
  return Array.from({ length: sides }, (_, i) => {
    const a = i * Math.PI * 2 / sides;
    return [x + width * Math.cos(a), y + height * Math.sin(a), z];
  });
}

function fuselage(mesh, x, sections, colour, sides = 8) {
  const rings = sections.map(([z, y, w, h]) => ring(x, y, z, w, h, sides));
  for (let j = 1; j < rings.length; j++) for (let i = 0; i < sides; i++) {
    const next = (i + 1) % sides;
    mesh.quad(rings[j - 1][i], rings[j - 1][next], rings[j][next], rings[j][i],
      shade(colour, i < sides / 2 ? 1.08 : .76), 2);
  }
  for (const [j, front] of [[0, false], [rings.length - 1, true]]) {
    const [z, y] = sections[j], end = rings[j];
    for (let i = 0; i < sides; i++) {
      const a = end[i], b = end[(i + 1) % sides];
      mesh.triangle([x, y, z], ...(front ? [a, b] : [b, a]), colour, 2);
    }
  }
}

function panel(mesh, side, outline, thickness, colour, axis = 1) {
  const top = outline.map(([x, y, z]) => [x * side, y, z]);
  // The two wings (or fins) reflect across the centre line and need opposite winding.
  if (axis === 0 ? side > 0 : side < 0) top.reverse();
  const bottom = top.map(p => p.map((v, i) => i === axis ? v - (axis === 0 ? side : 1) * thickness : v));
  for (const [a, b, c] of triangulate(top)) {
    mesh.triangle(top[a], top[b], top[c], colour, 2);
    mesh.triangle(bottom[a], bottom[c], bottom[b], shade(colour, .71), 2);
  }
  for (let i = 0; i < top.length; i++) {
    const j = (i + 1) % top.length;
    mesh.quad(top[i], bottom[i], bottom[j], top[j], shade(colour, .79), 2);
  }
}

function intake(mesh, x, y, z, width, height) {
  mesh.box([x, y, z - .36], [width + .14, height + .16, .7], [.25, .29, .30], 2);
  mesh.box([x, y, z], [width, height, .04], charcoal);
}

function exhaust(mesh, x, y, z, radius) {
  const outer = ring(x, y, z, radius, radius, 10);
  const forward = ring(x, y, z + .3, radius * 1.06, radius * 1.06, 10);
  const inner = ring(x, y, z, radius * .78, radius * .78, 10);
  for (let i = 0; i < outer.length; i++) {
    const j = (i + 1) % outer.length;
    mesh.quad(outer[i], outer[j], forward[j], forward[i], [.19, .21, .22], 2);
    mesh.quad(outer[j], outer[i], inner[i], inner[j], [.23, .25, .25], 2);
    mesh.triangle(inner[j], inner[i], [x, y, z + .08], charcoal);
  }
}

function tomcat() {
  const mesh = new Builder(), skin = [.51, .54, .52], edge = [.42, .47, .46];
  fuselage(mesh, 0, [[-7.5, -.11, .38, .28], [-3.6, -.02, .91, .59], [1.6, .04, 1.04, .83],
    [3.6, .09, .76, .78], [6.8, .01, .49, .47], [9.6, -.08, .045, .045]], skin);
  for (const side of [-1, 1]) {
    const x = side * 1.58;
    fuselage(mesh, x, [[-7.77, -.23, .63, .63], [-6.0, -.18, .77, .77], [-1.4, -.16, .74, .82],
      [2.4, -.21, .71, .58]], skin);
    intake(mesh, x, -.28, 2.67, 1.17, .87);
    exhaust(mesh, x, -.23, -8, .61);
    panel(mesh, side, [[.8, .19, 3.38], [3.15, .12, 1.42], [8.35, -.11, -3.72],
      [8.5, -.12, -5.15], [2.9, .06, -4.25], [.8, .22, -3.40]], .18, skin);
    panel(mesh, side, [[1.8, .21, -4.3], [4.57, .08, -6.61], [4.42, .07, -8.67],
      [1.85, .18, -7.18]], .145, edge);
    panel(mesh, side, [[1.69, .44, -3.1], [1.97, 3.48, -5.02], [2.16, 3.41, -6.65],
      [1.8, .46, -7.25]], .13, edge, 0);
    mesh.box([side * 8.29, -.09, -4.42], [.08, .06, .36], side < 0 ? [.68, .14, .11] : [.08, .51, .34], 4);
  }
  return mesh;
}

function eagle() {
  const mesh = new Builder(), skin = [.42, .49, .53], shoulder = [.49, .55, .58];
  fuselage(mesh, 0, [[-7.51, -.13, .92, .48], [-4.6, -.04, 1.56, .64], [-.3, .04, 1.46, .83],
    [3.8, .08, .88, .8], [6.2, .04, .58, .54], [9.2, -.06, .048, .045]], skin);
  for (const side of [-1, 1]) {
    const x = side * 1.25;
    fuselage(mesh, x, [[-7.49, -.21, .67, .67], [-4.6, -.17, .85, .82], [1.72, -.14, .94, .64],
      [3.24, -.1, .84, .57]], skin);
    intake(mesh, x, -.16, 3.54, 1.41, .83);
    exhaust(mesh, x, -.21, -7.7, .64);
    panel(mesh, side, [[1.36, .34, 3.75], [6.91, .02, -.4], [7.03, .005, -1.65],
      [5.06, .03, -3.24], [1.39, .31, -3.88]], .24, shoulder);
    panel(mesh, side, [[1.41, .32, -4.41], [3.99, .27, -5.72], [3.92, .25, -7.67],
      [1.5, .28, -7.79]], .18, skin);
    panel(mesh, side, [[1.53, .42, -3.35], [1.55, 3.75, -4.56], [1.55, 3.75, -6.12],
      [1.55, 1.37, -7.11], [1.53, .42, -7.29]], .16, shoulder, 0);
    mesh.box([side * 6.85, .05, -.99], [.08, .07, .28], side < 0 ? [.68, .14, .11] : [.08, .51, .34], 4);
  }
  return mesh;
}

function blackbird() {
  const mesh = new Builder(), skin = [.097, .116, .14], chine = [.127, .149, .173];
  fuselage(mesh, 0, [[-13.75, -.08, .09, .07], [-10.65, -.02, .68, .42], [-3.2, .05, 1.37, .71],
    [2.9, .12, 1.1, .74], [6.4, .11, .79, .64], [10.5, .02, .42, .34], [15.2, -.13, .044, .035]], skin, 10);
  for (const side of [-1, 1]) {
    const x = side * 4.07;
    panel(mesh, side, [[.09, -.12, 14.19], [2.49, .06, 3.18], [8.49, -.04, -6.35],
      [8.56, -.09, -9.35], [5.48, -.04, -10.92], [.09, -.13, -13.6]], .21, chine);
    fuselage(mesh, x, [[-11.31, -.13, .81, .81], [-8.7, -.1, 1.18, 1.08], [-3.15, -.08, 1.2, 1.18],
      [.94, -.05, 1.02, 1.02], [2.82, -.11, .09, .09]], skin, 12);
    fuselage(mesh, x, [[.16, -.05, 1.045, 1.045], [.91, -.05, 1.035, 1.035]], [.16, .18, .19], 12);
    exhaust(mesh, x, -.13, -11.58, .79);
    panel(mesh, side, [[4.13, .79, -6.11], [3.37, 3.21, -7.81], [3.34, 3.17, -9.27],
      [4.05, .72, -10.82]], .13, chine, 0);
    mesh.box([side * 8.45, -.045, -7.56], [.08, .07, .37], side < 0 ? [.68, .14, .11] : [.08, .51, .34], 4);
  }
  return mesh;
}

export const airframes = {
  f14: { build: tomcat, cameraScale: 1.12, mainGear: [1.6, -1.6, 1.58, -.52], noseGear: [0, 4.4, 0, -.45],
    nozzle: [1.58, -.23, -8, .43] },
  f15: { build: eagle, cameraScale: 1.1, mainGear: [1.43, -1.85, 1.42, -.52], noseGear: [0, 4.36, 0, -.44],
    nozzle: [1.25, -.21, -7.7, .46] },
  sr71: { build: blackbird, cameraScale: 1.52, mainGear: [3.63, -4.7, 3.88, -.88], noseGear: [0, 5.2, 0, -.33],
    nozzle: [4.07, -.13, -11.58, .53] },
};
