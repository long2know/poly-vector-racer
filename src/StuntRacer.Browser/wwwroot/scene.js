import { Geometry } from './engine/graphics.js';
import { add, angleMix, cross, lerp3, mix, multiply, normalize, pose, random, scale, sub, transform } from './engine/math.js';
import { buildCourseScenery } from './course-scenery.js';
import { buildEv6 } from './ev6-geometry.js';

const colors = {
  steel: [0.20, 0.25, 0.25], dark: [0.055, 0.068, 0.075], orange: [0.98, 0.24, 0.035],
  rust: [0.43, 0.25, 0.13], cream: [0.82, 0.83, 0.74], metal: [0.48, 0.54, 0.55],
};

export class Builder extends Geometry {
  box(center, dimensions, color, material = 0, matrix = null) {
    const [x, y, z] = dimensions.map(v => v / 2);
    let points = [[-x, -y, -z], [x, -y, -z], [x, y, -z], [-x, y, -z],
      [-x, -y, z], [x, -y, z], [x, y, z], [-x, y, z]].map(p => add(p, center));
    if (matrix) points = points.map(p => transform(matrix, p));
    for (const face of [[0, 3, 2, 1], [4, 5, 6, 7], [0, 4, 7, 3],
      [1, 2, 6, 5], [3, 7, 6, 2], [0, 1, 5, 4]]) {
      this.quad(...face.map(i => points[i]), color, material);
    }
  }

  beam(a, b, radius, color, material = 0, sides = 4) {
    const direction = normalize(sub(b, a));
    const right = scale(normalize(cross(direction, Math.abs(direction[1]) > 0.95 ? [1, 0, 0] : [0, 1, 0])), radius);
    const up = scale(normalize(cross(direction, right)), radius);
    for (let i = 0; i < sides; i++) {
      const t = i * Math.PI * 2 / sides, nt = (i + 1) * Math.PI * 2 / sides;
      const offset = angle => add(scale(right, Math.cos(angle)), scale(up, Math.sin(angle)));
      const p = add(a, offset(t)), q = add(a, offset(nt));
      const r = add(b, offset(nt)), s = add(b, offset(t));
      this.quad(p, q, r, s, color, material);
      this.triangle(a, q, p, color, material);
      this.triangle(b, s, r, color, material);
    }
  }

  cylinder(center, radius, length, color, material = 0, sides = 16) {
    this.beam(add(center, [-length / 2, 0, 0]), add(center, [length / 2, 0, 0]), radius, color, material, sides);
  }
}

function roadPoint(point, lateral, offset = 0) {
  if (point.edge) {
    const t = lateral / point.width + 0.5, { left, right } = point.edge;
    const up = point.up ?? [0, 1, 0];
    return [mix(left.x, right.x, t) + up[0] * offset, mix(left.y, right.y, t) + up[1] * offset,
      mix(left.z, right.z, t) + up[2] * offset];
  }
  return [point.x + Math.cos(point.heading) * lateral,
    point.y + Math.sin(point.bank) * lateral + offset,
    point.z - Math.sin(point.heading) * lateral];
}

function makeLandscape(track) {
  const land = new Builder(), rng = random(198907);
  const bounds = track.points.reduce((b, p) => [Math.min(b[0], p.x), Math.min(b[1], p.z),
    Math.max(b[2], p.x), Math.max(b[3], p.z)], [Infinity, Infinity, -Infinity, -Infinity]);
  const centerX = (bounds[0] + bounds[2]) / 2, centerZ = (bounds[1] + bounds[3]) / 2;
  const span = Math.max(bounds[2] - bounds[0], bounds[3] - bounds[1]);
  land.quad([centerX - 2400, -0.07, centerZ - 2400], [centerX - 2400, -0.07, centerZ + 2400],
    [centerX + 2400, -0.07, centerZ + 2400], [centerX + 2400, -0.07, centerZ - 2400], [0.24, 0.32, 0.19], 3);
  for (let layer = 0; layer < 3; layer++) {
    const n = 80, radius = Math.max(740, span * 1.25) + layer * 330;
    const peaks = Array.from({ length: n }, (_, i) => {
      const angle = i * Math.PI * 2 / n;
      return [centerX + Math.sin(angle) * radius, 45 + rng() * 110 + layer * 22, centerZ + Math.cos(angle) * radius];
    });
    for (let i = 0; i < n; i++) {
      const a = peaks[i], b = peaks[(i + 1) % n];
      const bottomA = [a[0], -1, a[2]], bottomB = [b[0], -1, b[2]];
      land.quad(bottomA, a, b, bottomB,
        [0.19 + layer * 0.035 + rng() * 0.025, 0.29 + layer * 0.027, 0.31 + layer * 0.025]);
    }
  }
  const props = new Builder();
  for (let i = 0; i < 220; i++) {
    const extent = Math.max(1120, span * 1.6);
    const x = centerX + (rng() - 0.5) * extent, z = centerZ + (rng() - 0.5) * extent;
    if (track.points.some((p, index) => index % 8 === 0 && Math.hypot(p.x - x, p.z - z) < 17)) continue;
    const height = 4 + rng() * 9, radius = height * (0.23 + rng() * 0.1);
    props.beam([x, 0, z], [x, height * 0.6, z], 0.25, [0.2, 0.16, 0.1]);
    for (let ring = 0; ring < 3; ring++) {
      const base = height * (0.15 + ring * 0.19), r = radius * (1 - ring * 0.2);
      const tip = [x, height * (0.7 + ring * 0.15), z];
      for (let side = 0; side < 6; side++) {
        const a = side * Math.PI / 3, b = (side + 1) * Math.PI / 3;
        props.triangle([x + Math.sin(a) * r, base, z + Math.cos(a) * r], tip,
          [x + Math.sin(b) * r, base, z + Math.cos(b) * r],
          [0.075 + rng() * 0.035, 0.17 + rng() * 0.055, 0.115 + rng() * 0.02]);
      }
    }
  }
  const first = track.points[0], forward = [Math.sin(first.heading), 0, Math.cos(first.heading)];
  const right = [Math.cos(first.heading), 0, -Math.sin(first.heading)];
  for (let i = 0; i < 4; i++) {
    const p = add(add([first.x, 0, first.z], scale(right, -26 - i * 8)), scale(forward, -18));
    props.box(add(p, [0, 1.8, 0]), [6, 3.6, 11], i % 2 ? colors.cream : colors.rust);
    props.box(add(p, [0, 3.8, 0]), [6.8, 0.35, 11.6], colors.dark, 2);
    props.box(add(p, [0, 1.4, 5.55]), [4.5, 2.8, 0.08], [0.12, 0.17, 0.19], 2);
  }
  return { land, props };
}

function makeTrack(track) {
  const mesh = new Builder(), { points } = track, count = points.length;
  for (let i = 0; i < count; i++) {
    const p = points[i], q = points[(i + 1) % count], w = p.width / 2, qw = q.width / 2;
    if (!p.road || !q.road) continue;
    mesh.triangle(roadPoint(p, -w), roadPoint(q, -qw), roadPoint(p, w), [0.13, 0.145, 0.145], 1);
    mesh.triangle(roadPoint(p, w), roadPoint(q, -qw), roadPoint(q, qw), [0.13, 0.145, 0.145], 1);
    for (const side of [-1, 1]) {
      const l = w * side, r = qw * side;
      const stripe = Math.floor(p.s / 4) % 2 ? colors.cream : colors.orange;
      mesh.quad(roadPoint(p, l - side * 0.45, 0.025), roadPoint(q, r - side * 0.45, 0.025),
        roadPoint(q, r, 0.025), roadPoint(p, l, 0.025), stripe);
      mesh.quad(roadPoint(p, l), roadPoint(q, r), roadPoint(q, r, -0.75), roadPoint(p, l, -0.75), colors.rust);
      mesh.beam(roadPoint(p, l, -1.65), roadPoint(q, r, -1.65), 0.10, colors.steel, 2);
      if (i % 5 === 0) {
        const end = points[(i + 5) % count];
        mesh.beam(roadPoint(p, l, -0.7), roadPoint(end, end.width / 2 * side, -1.65),
          0.065, colors.rust, 2);
      }
    }
    if (Math.floor(p.s / 9) % 2 === 0) {
      mesh.quad(roadPoint(p, -0.07, 0.029), roadPoint(q, -0.07, 0.029),
        roadPoint(q, 0.07, 0.029), roadPoint(p, 0.07, 0.029), [0.55, 0.54, 0.43]);
    }
    if (!track.isSpatial && i % 18 === 0) {
      for (const side of [-1, 1]) {
        const top = roadPoint(p, w * side * 0.8, -1.1), foot = [top[0], 0.2, top[2]];
        mesh.beam(foot, top, 0.32, colors.steel, 2);
        mesh.box([foot[0], 0.2, foot[2]], [2, 0.4, 2], [0.42, 0.42, 0.34]);
        const brace = roadPoint(p, -w * side * 0.8, -1.3);
        mesh.beam([foot[0], Math.max(0.7, top[1] - 8), foot[2]], brace, 0.14, colors.rust, 2);
      }
      mesh.beam(roadPoint(p, -w, -0.9), roadPoint(p, w, -0.9), 0.22, colors.steel, 2);
    }
  }
  const start = points[0], gate = pose([start.x, start.y, start.z], start.heading, 0, start.bank);
  for (let row = 0; row < 2; row++) for (let cell = 0; cell < 18; cell++) {
    const left = -start.width / 2 + cell * start.width / 18, right = left + start.width / 18;
    const z = row * 0.6 - 0.6;
    mesh.quad(...[[left, 0.04, z], [left, 0.04, z + 0.6],
      [right, 0.04, z + 0.6], [right, 0.04, z]].map(p => transform(gate, p)),
    (row + cell) % 2 ? colors.cream : colors.dark);
  }
  for (const side of [-1, 1]) {
    mesh.box([side * (start.width / 2 + 0.5), 2.8, 0], [0.4, 5.6, 0.5], colors.dark, 2, gate);
    mesh.box([side * (start.width / 2 + 0.5), 2.5, 0], [0.42, 0.8, 0.52], colors.orange, 0, gate);
  }
  mesh.box([0, 5.35, 0], [start.width + 1.5, 0.8, 0.65], colors.dark, 2, gate);
  for (let i = -4; i <= 4; i++) {
    mesh.box([i * 1.05, 5.36, -0.34], [0.48, 0.18, 0.03], colors.cream, 4, gate);
  }
  return mesh;
}

function makeCar(color = colors.orange) {
  const mesh = new Builder(), dark = colors.dark;
  mesh.box([0, -0.27, 0], [1.32, 0.25, 3.4], dark, 2);
  mesh.box([0, -0.02, -0.78], [1.45, 0.48, 1.35], color, 2);
  const hood = [[-0.64, -0.13, 0.1], [0.64, -0.13, 0.1], [-0.46, -0.10, 1.95], [0.46, -0.10, 1.95],
    [-0.59, 0.39, 0.1], [0.59, 0.39, 0.1], [-0.45, 0.04, 1.95], [0.45, 0.04, 1.95]];
  mesh.quad(hood[4], hood[6], hood[7], hood[5], color, 2);
  mesh.quad(hood[0], hood[2], hood[6], hood[4], color, 2);
  mesh.quad(hood[1], hood[5], hood[7], hood[3], color, 2);
  mesh.quad(hood[2], hood[3], hood[7], hood[6], [0.65, 0.13, 0.025], 2);
  mesh.quad([-0.07, 0.402, 0.1], [-0.07, 0.055, 1.93], [0.07, 0.055, 1.93], [0.07, 0.402, 0.1], colors.cream);
  for (const side of [-1, 1]) {
    mesh.box([side * 0.66, 0.1, -0.09], [0.16, 0.54, 0.85], color, 2);
    mesh.beam([side * 0.5, 0.12, -0.45], [side * 0.56, 1.28, -0.5], 0.035, dark, 2, 8);
    mesh.beam([side * 0.56, 1.28, -0.5], [side * 0.55, 1.27, 0.7], 0.035, dark, 2, 8);
    mesh.beam([side * 0.55, 1.27, 0.7], [side * 0.58, 0.21, 0.88], 0.035, dark, 2, 8);
    mesh.beam([side * 0.55, 0.18, -0.45], [side * 0.55, 0.82, -0.7], 0.04, colors.metal, 2, 6);
    for (const z of [-1.22, 1.3]) {
      mesh.beam([side * 0.44, -0.1, z - 0.28], [side * 1.08, -0.43, z], 0.045, colors.metal, 2, 6);
      mesh.beam([side * 0.44, -0.1, z + 0.28], [side * 1.08, -0.43, z], 0.045, colors.metal, 2, 6);
      mesh.beam([side * 0.51, 0.1, z], [side * 0.97, -0.39, z], 0.065, color, 2, 8);
    }
    mesh.beam([side * 0.39, 0.19, -1.25], [side * 0.39, 0.42, -1.91], 0.095, colors.metal, 2, 10);
  }
  mesh.beam([-0.56, 1.28, -0.5], [0.56, 1.28, -0.5], 0.035, dark, 2, 8);
  mesh.beam([-0.55, 1.27, 0.7], [0.55, 1.27, 0.7], 0.035, dark, 2, 8);
  mesh.box([0, 0.12, -0.3], [0.77, 0.2, 0.74], [0.045, 0.05, 0.055]);
  mesh.box([0, 0.44, -0.65], [0.66, 0.68, 0.16], dark);
  mesh.box([0, 0.33, -0.754], [0.12, 0.6, 0.02], [0.7, 0.05, 0.03]);
  mesh.box([0, 0.23, -1.3], [0.79, 0.43, 0.66], [0.24, 0.28, 0.29], 2);
  for (let i = -3; i <= 3; i++) mesh.box([i * 0.1, 0.47, -1.3], [0.035, 0.08, 0.6], colors.metal, 2);
  mesh.box([0, -0.16, -1.89], [1.38, 0.13, 0.17], dark, 2);
  for (const side of [-1, 1]) {
    mesh.box([side * 0.54, -0.02, -1.86], [0.19, 0.12, 0.045], [0.9, 0.025, 0.01], 4);
    mesh.box([side * 0.29, -0.03, 1.965], [0.23, 0.085, 0.04], colors.cream, 4);
  }
  return mesh;
}

export const DEFAULT_VEHICLE_STYLE = 'ev6';

export const vehicleStyles = [
  { id: 'ev6', name: 'EV6-inspired / electric crossover' },
  { id: 'buggy', name: 'Stunt Buggy / open cage' },
  { id: 'coupe', name: 'Vector GT / wedge coupe' },
  { id: 'sedan', name: 'Touring Sedan / four door' },
  { id: 'hatch', name: 'Club Hatch / compact' },
  { id: 'van', name: 'Delivery Van / high roof' },
  { id: 'pickup', name: 'Compact Pickup / open bed' },
];

export function makeVehicleGeometry(style, color, interior = false) {
  if (!vehicleStyles.some(choice => choice.id === style)) throw new Error(`Unknown vehicle body: ${style}`);
  if (style === 'ev6') return buildEv6(new Builder(), color, interior);
  color ??= colors.orange;
  if (style === 'buggy') return makeCar(color);
  const mesh = new Builder(), glass = [0.11, 0.26, 0.3], dark = colors.dark;
  if (interior) {
    mesh.box([0, 0.28, 0.55], [1.73, 0.25, 0.6], dark, 2);
    mesh.box([0, 0.03, 1.35], [1.65, 0.18, 1.2], color, 2);
    for (const side of [-1, 1])
      mesh.beam([side * .84, .2, .5], [side * .7, 1.3, .7], .038, dark, 2, 6);
    mesh.beam([-.7, 1.3, .7], [.7, 1.3, .7], .038, dark, 2, 6);
    return mesh;
  }
  mesh.box([0, -.12, 0], [1.86, .54, 3.8], color, 2);
  mesh.box([0, -.35, 0], [1.68, .14, 3.45], dark, 2);
  const cabin = (front, rear, roofFront, roofRear, roofHeight, width = .77) => {
    const base = .15, roofWidth = width * .88;
    const a = [-width, base, front], b = [width, base, front];
    const c = [width, base, rear], d = [-width, base, rear];
    const e = [-roofWidth, roofHeight, roofFront], f = [roofWidth, roofHeight, roofFront];
    const g = [roofWidth, roofHeight, roofRear], h = [-roofWidth, roofHeight, roofRear];
    mesh.quad(a, b, f, e, glass, 2);
    mesh.quad(d, h, g, c, glass, 2);
    mesh.quad(a, e, h, d, glass, 2);
    mesh.quad(b, c, g, f, glass, 2);
    mesh.quad(e, f, g, h, color.map(value => Math.min(1, value * 1.1)), 2);
    for (const [p, q] of [[a, e], [b, f], [c, g], [d, h], [e, f], [g, h]])
      mesh.beam(p, q, .026, color, 2, 4);
    if (style === 'sedan' || style === 'hatch')
      for (const side of [-1, 1]) mesh.beam([side * width, base, -.25],
        [side * roofWidth, roofHeight, -.25], .035, dark, 2, 4);
  };
  if (style === 'coupe') {
    cabin(.85, -1.05, .08, -.65, .73);
    mesh.quad([-.88, .15, .82], [.88, .15, .82], [.84, -.05, 1.94], [-.84, -.05, 1.94], color, 2);
    mesh.box([0, .35, -1.6], [1.85, .09, .25], dark, 2);
    for (const x of [-.65, .65]) mesh.box([x, .24, -1.6], [.07, .2, .12], colors.metal, 2);
  } else if (style === 'sedan') {
    cabin(1.0, -1.25, .35, -.78, 1.0);
    mesh.box([0, .2, -1.58], [1.77, .12, .58], color, 2);
  } else if (style === 'hatch') {
    cabin(.95, -1.7, .23, -1.28, 1.1);
    mesh.box([0, .62, -1.78], [1.65, .08, .18], dark, 2);
  } else if (style === 'van') {
    mesh.box([0, .74, -.3], [1.84, 1.66, 2.95], color, 2);
    cabin(1.65, .4, 1.12, .4, 1.57, .81);
    mesh.box([0, .45, -1.82], [1.65, .018, .04], dark);
    mesh.box([0, .8, -1.84], [.025, 1.35, .04], dark);
    for (const x of [-.45, .45]) mesh.box([x, .98, -1.85], [.68, .53, .035], glass, 2);
    for (const side of [-1, 1]) mesh.box([side * .925, 1.08, -.45], [.02, .38, 1.72], [0.8, .83, .72]);
  } else if (style === 'pickup') {
    cabin(1.02, -.35, .57, -.22, 1.03, .79);
    mesh.box([0, .03, -1.12], [1.53, .12, 1.45], dark, 2);
    for (const side of [-1, 1]) mesh.box([side * .86, .27, -1.12], [.16, .55, 1.5], color, 2);
    mesh.box([0, .27, -1.86], [1.76, .55, .13], color, 2);
  }
  for (const side of [-1, 1]) {
    mesh.box([side * .85, .02, .7], [.16, .15, .27], color, 2);
    mesh.box([side * .62, -.01, 1.93], [.36, .14, .04], colors.cream, 4);
    mesh.box([side * .67, .0, -1.94], [.28, .17, .04], [.84, .03, .01], 4);
    mesh.box([side * .64, .1, -.08], [.18, .025, .04], colors.metal, 2);
  }
  mesh.box([0, -.22, 1.94], [1.81, .12, .1], dark, 2);
  mesh.box([0, -.22, -1.95], [1.81, .12, .1], dark, 2);
  mesh.box([0, -.015, 1.955], [.68, .13, .025], dark);
  return mesh;
}

function makeWheel(hub = colors.orange) {
  const mesh = new Builder();
  mesh.cylinder([0, 0, 0], 0.52, 0.43, [0.036, 0.042, 0.045], 0, 24);
  mesh.cylinder([0, 0, 0], 0.31, 0.445, [0.14, 0.18, 0.2], 2, 16);
  mesh.cylinder([0, 0, 0], 0.09, 0.48, hub, 2, 12);
  for (const side of [-1, 1]) {
    for (let i = 0; i < 5; i++) {
      const t = i * Math.PI * 2 / 5;
      mesh.beam([side * 0.23, 0, 0], [side * 0.23, Math.sin(t) * 0.28, Math.cos(t) * 0.28],
        0.039, colors.metal, 2, 4);
    }
  }
  return mesh;
}

function makeSteeringWheel() {
  const mesh = new Builder(), radius = 0.24;
  for (let i = 0; i < 18; i++) {
    const a = i * Math.PI / 9, b = (i + 1) * Math.PI / 9;
    mesh.beam([Math.cos(a) * radius, Math.sin(a) * radius, 0],
      [Math.cos(b) * radius, Math.sin(b) * radius, 0], 0.028, colors.dark, 0, 6);
  }
  for (let i = 0; i < 3; i++) {
    const a = i * Math.PI * 2 / 3 - Math.PI / 2;
    mesh.beam([0, 0, 0], [Math.cos(a) * radius, Math.sin(a) * radius, 0], 0.022, colors.metal, 2);
  }
  return mesh;
}

function makeCheckpointGate() {
  const mesh = new Builder(), color = [0.55, 0.94, 0.17];
  for (const side of [-1, 1]) {
    mesh.box([side * 0.535, 3.2, 0], [0.025, 6.4, 0.45], colors.dark, 2);
    mesh.box([side * 0.535, 4.7, -0.02], [0.028, 1.1, 0.49], color, 4);
  }
  mesh.box([0, 6.3, 0], [1.1, 0.5, 0.5], color, 4);
  for (const side of [-1, 1]) {
    mesh.box([side * 0.08, 5.65, 0], [0.04, 0.6, 0.15], color, 4);
  }
  return mesh;
}

export class RaceScene {
  orbit = 0;
  spin = 0;
  cameraEye = null;
  cameraTarget = null;
  previousMode = null;

  constructor(graphics, track, geometry = null) {
    this.graphics = graphics;
    this.track = { ...track, points: track.points.map((p, i) => {
      const edge = track.edges?.[i];
      const q = track.points[(i + 1) % track.points.length];
      return { ...p, edge, up: track.isSpatial && edge
        ? normalize(cross([q.x - p.x, q.y - p.y, q.z - p.z],
          [edge.right.x - edge.left.x, edge.right.y - edge.left.y, edge.right.z - edge.left.z])) : undefined };
    }) };
    const landscape = geometry ?? makeLandscape(this.track);
    this.land = graphics.mesh(landscape.land);
    this.props = graphics.mesh(landscape.props);
    this.road = graphics.mesh(geometry ? geometry.road : makeTrack(this.track));
    const scenery = geometry ? null : buildCourseScenery(new Builder(), this.track);
    this.courseScenery = scenery ? graphics.mesh(scenery) : null;
    this.playerBodies = Object.fromEntries(vehicleStyles.map(style => [style.id, graphics.mesh(makeVehicleGeometry(style.id))]));
    this.ghostBodies = Object.fromEntries(vehicleStyles.map(style => [style.id, graphics.mesh(makeVehicleGeometry(style.id, [.62, .24, .95]))]));
    this.cockpitBodies = Object.fromEntries(vehicleStyles.map(style => [style.id, graphics.mesh(makeVehicleGeometry(style.id, undefined, true))]));
    this.trafficBodies = [
      graphics.mesh(makeVehicleGeometry('sedan', [.78, .62, .17])),
      graphics.mesh(makeVehicleGeometry('coupe', [.08, .4, .64])),
      graphics.mesh(makeVehicleGeometry('van', [.8, .81, .66])),
      graphics.mesh(makeVehicleGeometry('pickup', [.62, .18, .11])),
      graphics.mesh(makeVehicleGeometry('hatch', [.22, .45, .27])),
    ];
    this.pace = graphics.mesh(makeVehicleGeometry('coupe', [.03, .6, .72]));
    this.checkpointGate = graphics.mesh(makeCheckpointGate());
    this.wheel = graphics.mesh(makeWheel());
    this.evWheel = graphics.mesh(makeWheel([.21, .5, .47]));
    this.setVehicleStyle(DEFAULT_VEHICLE_STYLE);
    this.steering = graphics.mesh(makeSteeringWheel());
    const flame = new Builder();
    for (const side of [-1, 1]) {
      flame.beam([side * 0.39, 0.42, -1.95], [side * 0.39, 0.4, -2.7], 0.12, [1, 0.29, 0.025], 4, 6);
      flame.beam([side * 0.39, 0.42, -1.96], [side * 0.39, 0.4, -2.45], 0.065, [1, 0.84, 0.23], 4, 6);
    }
    this.flame = graphics.mesh(flame);
  }

  setVehicleStyle(style) {
    if (!this.playerBodies[style]) throw new Error(`Unknown vehicle body: ${style}`);
    this.vehicleStyle = style;
    this.car = this.playerBodies[style];
    this.ghost = this.ghostBodies[style];
    this.cockpitBody = this.cockpitBodies[style];
    this.playerWheel = style === 'ev6' ? this.evWheel : this.wheel;
  }

  update(state, dt, mode = 'chase', compatibility = null) {
    const car = state.player, position = [car.x, car.y, car.z], ready = state.phase === 'ready';
    const rig = compatibility?.sourcePhysicsActive
      ? [compatibility.contactSpanMetres / 2.12, 1, compatibility.contactAxleDistanceMetres / 2.52] : [1, 1, 1];
    const body = pose(position, car.yaw, car.pitch, car.roll, rig);
    this.spin += car.speed * dt / 0.52;
    this.orbit += dt;
    const draws = [{ mesh: this.land, shadow: false }, { mesh: this.props }, { mesh: this.road },
      { mesh: !ready && mode === 'cockpit' ? this.cockpitBody : this.car, model: body },
      { mesh: this.steering, model: multiply(body, pose([0, 0.49, 0.38], 0, 0.18, -car.steer * 1.1)) }];
    if (this.courseScenery) draws.push({ mesh: this.courseScenery, shadow: false });
    for (let i = 0; i < 4; i++) {
      const side = i % 2 ? 1 : -1, z = i < 2 ? 1.3 : -1.22;
      const compression = car.suspension?.[i] ?? 0.5;
      draws.push({ mesh: this.playerWheel, model: multiply(body,
        pose([side * 1.06, -0.48 + (compression - 0.5) * 0.24, z],
          i < 2 ? car.steer * 0.35 : 0, -this.spin, 0)) });
    }
    if (car.boosting) draws.push({ mesh: this.flame, model: body, shadow: false });
    if (state.paceCar) {
      const pace = state.paceCar;
      const matrix = pose([pace.x, pace.y, pace.z], pace.yaw, pace.pitch, pace.roll);
      draws.push({ mesh: this.pace, model: matrix });
      for (let i = 0; i < 4; i++) draws.push({ mesh: this.wheel, model: multiply(matrix,
        pose([i % 2 ? 1.06 : -1.06, -0.48, i < 2 ? 1.3 : -1.22], 0, -this.spin)) });
    }
    if (state.trial && state.trial.success === null) {
      draws.push({ mesh: this.checkpointGate,
        model: this.checkpointFrame(state.trial.nextCheckpointProgress), shadow: false });
    }
    if (!state.trial && state.stunt?.success === null) {
      draws.push({ mesh: this.checkpointGate,
        model: this.checkpointFrame(state.stunt.nextGateProgress), shadow: false });
    }
    if (state.ghost?.enabled && state.ghost.car) {
      const ghost = state.ghost.car;
      const matrix = pose([ghost.x, ghost.y, ghost.z], ghost.yaw, ghost.pitch, ghost.roll);
      draws.push({ mesh: this.ghost, model: matrix, opacity: 0.4, shadow: false });
      for (let i = 0; i < 4; i++) draws.push({ mesh: this.playerWheel, opacity: 0.4, shadow: false,
        model: multiply(matrix, pose([i % 2 ? 1.06 : -1.06,
          -0.48 + ((ghost.suspension?.[i] ?? 0.5) - 0.5) * 0.24, i < 2 ? 1.3 : -1.22],
        i < 2 ? ghost.steer * 0.35 : 0, -ghost.speed * state.lapTime / 0.52)) });
    }
    const activeMode = ready ? 'orbit' : mode;
    const spatial = this.track.isSpatial === true;
    const forward = spatial ? normalize([body[8], body[9], body[10]]) : [Math.sin(car.yaw), 0, Math.cos(car.yaw)];
    const right = spatial ? normalize([body[0], body[1], body[2]]) : [Math.cos(car.yaw), 0, -Math.sin(car.yaw)];
    const vehicleUp = spatial ? normalize([body[4], body[5], body[6]]) : [0, 1, 0];
    let eye, target, up = [0, 1, 0], fov;
    if (activeMode === 'cockpit') {
      eye = transform(body, [0, 0.94, -0.35]);
      target = transform(body, [car.steer * 0.22, 0.88, 30]);
      up = spatial ? vehicleUp : normalize(transform(pose([0, 0, 0], car.yaw, car.pitch * 0.65, car.roll * 0.7), [0, 1, 0]));
      fov = (80 + Math.min(Math.abs(car.speed) * 0.13, 7)) * Math.PI / 180;
    } else if (activeMode === 'orbit') {
      const angle = car.yaw + 0.75 + Math.sin(this.orbit * 0.1) * 0.15;
      eye = [car.x - Math.sin(angle) * 10, car.y + 5.6, car.z - Math.cos(angle) * 10];
      target = add(add(position, scale(forward, 1.8)), scale(right, -1.3));
      fov = 63 * Math.PI / 180;
    } else {
      const distance = 7.8 + Math.min(Math.abs(car.speed) * 0.027, 1.5);
      if (spatial) {
        const approach = /approach/i.test(state.stunt?.feature ?? '');
        eye = add(add(position, scale(forward, -(distance + (approach ? 3 : 0)))), scale(vehicleUp, approach ? 4.5 : 3.1));
        target = add(add(position, scale(forward, approach ? 35 : 4.7)), scale(vehicleUp, approach ? 12 : 0.65));
        up = vehicleUp;
      } else {
        this.cameraYaw = this.cameraYaw === undefined ? car.yaw : angleMix(this.cameraYaw, car.yaw, 1 - Math.exp(-dt * 5));
        const trailing = [-Math.sin(this.cameraYaw), 0, -Math.cos(this.cameraYaw)];
        eye = add(add(position, scale(trailing, distance)), [0, 3.1 + Math.abs(car.speed) * 0.014, 0]);
        eye[1] = Math.max(2, eye[1]);
        target = add(add(position, scale(forward, 4.7)), [0, 0.65, 0]);
      }
      fov = (65 + Math.min(Math.abs(car.speed) * 0.11, 6)) * Math.PI / 180;
      if (spatial && /approach/i.test(state.stunt?.feature ?? '')) fov = 78 * Math.PI / 180;
    }
    if (this.previousMode !== activeMode || activeMode === 'cockpit') {
      this.cameraEye = eye;
      this.cameraTarget = target;
    } else {
      const smoothing = 1 - Math.exp(-dt * 9);
      this.cameraEye = lerp3(this.cameraEye, eye, smoothing);
      this.cameraTarget = lerp3(this.cameraTarget, target, smoothing);
    }
    this.previousMode = activeMode;
    const shake = Math.min(state.impact, 0.6) * 0.09;
    const camera = {
      eye: add(this.cameraEye, [Math.sin(this.orbit * 83) * shake, Math.sin(this.orbit * 97) * shake, 0]),
      target: this.cameraTarget, up, fov,
      logarithmicDepth: spatial,
    };
    return { draws, camera };
  }

  checkpointFrame(progress) {
    const { points, length } = this.track;
    const distance = ((progress % 1) + 1) % 1 * length;
    let low = 0, high = points.length - 1;
    while (low < high) {
      const middle = (low + high + 1) >> 1;
      if (points[middle].s <= distance) low = middle;
      else high = middle - 1;
    }
    const a = points[low], b = points[(low + 1) % points.length];
    const t = (distance - a.s) / ((low + 1 === points.length ? length : b.s) - a.s);
    const left = lerp3(roadPoint(a, -a.width / 2), roadPoint(b, -b.width / 2), t);
    const right = lerp3(roadPoint(a, a.width / 2), roadPoint(b, b.width / 2), t);
    const x = sub(right, left), center = lerp3(left, right, 0.5);
    const up = normalize(cross(sub([b.x, b.y, b.z], [a.x, a.y, a.z]), x));
    const forward = normalize(cross(x, up));
    return new Float32Array([...x, 0, ...up, 0, ...forward, 0, ...center, 1]);
  }

  mapPoint(x, z) {
    this.bounds ??= this.track.points.reduce((b, p) =>
      [Math.min(b[0], p.x), Math.min(b[1], p.z), Math.max(b[2], p.x), Math.max(b[3], p.z)],
    [Infinity, Infinity, -Infinity, -Infinity]);
    const [minX, minZ, maxX, maxZ] = this.bounds;
    const size = Math.max(maxX - minX, maxZ - minZ);
    return [mix(12, 148, (x - (minX + maxX - size) / 2) / size),
      mix(12, 148, (z - (minZ + maxZ - size) / 2) / size)];
  }
}
