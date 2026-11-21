import { cross, lerp3, normalize, sub, transform } from './engine/math.js';

const stunts = {
  shell: [.105, .055, .235], signal: [.32, .91, .82], launch: [1, .285, .48], landing: [.43, 1, .54],
};
const proving = {
  shell: [.095, .125, .148], signal: [1, .58, .08], launch: [1, .37, .045], landing: [.82, .93, 1],
};

const xyz = ({ x, y, z }) => [x, y, z];

function frameAt({ points, length }, s, side) {
  const distance = (s % length + length) % length;
  let low = 0, high = points.length - 1;
  while (low < high) {
    const middle = (low + high + 1) >> 1;
    if (points[middle].s <= distance) low = middle;
    else high = middle - 1;
  }
  const a = points[low], b = points[(low + 1) % points.length];
  if (!a.road || !b.road) return null;
  const t = (distance - a.s) / ((low + 1 === points.length ? length : b.s) - a.s);
  const left = lerp3(xyz(a.edge.left), xyz(b.edge.left), t);
  const right = lerp3(xyz(a.edge.right), xyz(b.edge.right), t);
  const across = normalize(sub(right, left));
  const up = normalize(cross(sub(xyz(b), xyz(a)), across));
  const forward = normalize(cross(across, up));
  return [...across, 0, ...up, 0, ...forward, 0, ...(side < 0 ? left : right), 1];
}

function stroke(mesh, frame, z, tint, path, width) {
  for (let i = 1; i < path.length; i++) {
    const [x, y] = path[i - 1], [xx, yy] = path[i];
    const scale = width / (2 * Math.hypot(xx - x, yy - y)), dx = (y - yy) * scale, dy = (xx - x) * scale;
    mesh.quad(...[[x + dx, y + dy, z], [xx + dx, yy + dy, z],
      [xx - dx, yy - dy, z], [x - dx, y - dy, z]].map(p => transform(frame, p)), tint, 4);
  }
}

function panel(mesh, frame, side, colors, symbol, size) {
  if (!frame) return;
  const [width, height] = size;
  const x = side * (width / 2 + .75), y = height / 2 + .74, z = -.145;
  const tint = symbol === 'gap' ? colors.launch : colors.signal;
  for (const leg of [-.30, .30])
    mesh.box([x + leg * width, .47, 0], [.145, 1.05, .24], colors.shell, 2, frame);
  mesh.box([x, y, 0], [width, height, .26], colors.shell, 2, frame);
  mesh.box([x, y + height / 2 - .075, -.041], [width, .15, .227], tint, 4, frame);
  const draw = (path, weight = .07) =>
    stroke(mesh, frame, z, tint, path.map(([u, v]) => [x + u * width, y + v * height]), weight * height);
  if (symbol === 'loop') {
    draw([[-.31, -.28], [-.12, -.28], ...Array.from({ length: 13 }, (_, i) => {
      const angle = -Math.PI / 2 + i * Math.PI * 2 / 12;
      return [.184 * Math.cos(angle), .222 * Math.sin(angle) - .055];
    }), [.31, -.28]]);
    draw([[.245, -.19], [.31, -.28], [.245, -.365]], .05);
  } else if (symbol === 'narrow') {
    for (const sign of [-1, 1]) {
      draw([[sign * .34, -.25], [sign * .19, -.04], [sign * .19, .23]]);
      draw([[sign * .35, .065], [sign * .27, .16], [sign * .35, .255]], .041);
    }
  } else {
    draw([[-.36, -.23], [-.24, -.23], [-.09, -.135]]);
    draw([[.09, -.135], [.36, -.135]]);
    draw([[-.18, -.03], [.12, .213], [.30, .244]], .057);
    draw([[.235, .139], [.30, .244], [.211, .324]], .057);
  }
}

function blade(mesh, frame, side, colors, light, height, offset = .46) {
  if (!frame) return;
  const x = offset * side;
  mesh.box([x, height * .38, 0], [.24, height * .92, .37], colors.shell, 2, frame);
  mesh.box([x, height * .81, -.045], [.27, height * .31, .45], light, 4, frame);
}

function features({ points, length }) {
  const slope = i => (points[i + 1].y - points[i].y) / (points[i + 1].s - points[i].s);
  let loop = points.findIndex((p, i) => p.road && points[i + 1]?.road && slope(i) > .65);
  if (loop !== -1)
    while (loop > 0 && points[loop - 1].road && slope(loop - 1) > .035) loop--;

  const min = Math.min(...points.filter(p => p.road).map(p => p.width));
  const narrow = p => p?.road && p.width <= min + .03;
  const start = points.findIndex(narrow);
  let end = start;
  while (end !== -1 && narrow(points[end + 1])) end++;
  const bridge = end > start && points[end].s - points[start].s >= 15 && min < points[0].width * .75
    ? [points[start].s, points[end].s] : null;

  const takeoff = points.findIndex((p, i) => p.road && points[i + 1]?.road === false);
  const landing = takeoff < 0 ? -1 : points.findIndex((p, i) => i > takeoff && p.road);
  const gap = landing < 0 || points[landing].s >= length ? null : [points[takeoff].s, points[landing].s];
  return { loop: points[loop]?.s, bridge, gap };
}

export function buildCourseScenery(mesh, track) {
  if (!track.isSpatial || !track.points.every(p => p.edge)) return null;
  const park = track.name === 'Skyline Loop Park — authored Stunts-inspired challenge';
  const hard = track.name === "Hard Drivin' Loop & Bridge Proving Ground — authored timed challenge";
  if (!park && !hard) return null;
  const colors = park ? stunts : proving, { loop, bridge, gap } = features(track);
  const sign = (s, side, symbol, w, h) => panel(mesh, frameAt(track, s, side), side, colors, symbol, [w, h]);
  const pair = (s, color, height, offset) => {
    for (const side of [-1, 1]) blade(mesh, frameAt(track, s, side), side, colors, color, height, offset);
  };
  if (loop !== undefined) {
    if (park) {
      sign(loop - 38, -1, 'loop', 2.9, 2.64);
      sign(loop - 29, 1, 'loop', 2.54, 2.30);
      pair(loop - 10, colors.signal, 1.48);
    } else {
      sign(loop - 54, 1, 'loop', 4.12, 3.04);
      pair(loop - 25, colors.signal, 1.18, .77);
      pair(loop - 6, colors.signal, .83, .77);
    }
  }
  if (bridge) {
    const [start, end] = bridge;
    sign(start - (park ? 17 : 26), park ? 1 : -1, 'narrow', park ? 2.47 : 3.24, 2.22);
    if (park) pair(start - 4, colors.signal, .92);
    else for (const d of [14, 6]) pair(start - d, colors.signal, 1.13, .57);
    const count = Math.min(park ? 10 : 9, Math.floor((end - start) / 5));
    for (let i = 0; i <= count; i++)
      pair(start + 1 + (end - start - 2) * i / count, colors.signal, park ? .49 : .73, .37);
  }
  if (gap) {
    const [takeoff, landing] = gap;
    sign(takeoff - (park ? 37 : 56), park ? -1 : 1, 'gap', park ? 2.72 : 3.64, 2.55);
    for (const d of park ? [7, .55] : [12, 3.8, .64])
      pair(takeoff - d, colors.launch, d < 1 ? 1.42 : .62, d < 1 ? .75 : .46);
    for (const d of park ? [.63, 6.8, 13] : [.76, 8.6, 18, 30])
      pair(landing + d, colors.landing, d < 1 ? 1.70 : .63, d < 1 ? .75 : .46);
  }
  return mesh.vertices.length ? mesh : null;
}
