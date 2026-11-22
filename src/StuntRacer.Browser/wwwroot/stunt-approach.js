import { add, clamp, dot, lerp3, mix, normalize, scale, sub } from './engine/math.js';

const palettes = {
  stunts: { entry: [.06, .65, .55], crown: [.38, .57, .65], exit: [.96, .38, .15] },
  'hard-driving': { entry: [.99, .55, .12], crown: [.55, .57, .58], exit: [.14, .54, .93] },
};

export function stuntCourseMode(track) {
  if (track.course?.mode != null) return track.course.mode;
  if (track.name === 'Skyline Loop Park — authored Stunts-inspired challenge') return 'stunts';
  if (track.name === "Hard Drivin' Loop & Bridge Proving Ground — authored timed challenge") return 'hard-driving';
  return null;
}

const xyz = ({ x, y, z }) => [x, y, z];
const smooth = (start, end, x) => {
  const t = clamp((x - start) / (end - start), 0, 1);
  return t * t * (3 - 2 * t);
};

export function prepareStuntLoop(track) {
  const { mode, loopStart: start, loopEnd: end } = track.course ?? {};
  if (!track.isSpatial || !Object.hasOwn(palettes, mode)
    || !Number.isFinite(start) || !Number.isFinite(end) || !(start >= 0 && start < end && end < track.length)) return null;
  const colors = palettes[mode];
  const points = track.points.filter(point => point.s >= start - 1e-6 && point.s <= end + 1e-6);
  if (points.length < 3 || points.some(point => !point.road || !point.edge)) return null;
  const entry = points[0], exit = points.at(-1), peak = points.reduce((a, b) => a.y > b.y ? a : b);
  const forward = normalize(sub(xyz(points[1]), xyz(entry)));
  const arrows = [
    ...[-42, -30, -18, -8, 2, 13, 25].map(offset => ({ s: start + offset, color: colors.entry })),
    ...[-13, 2, 14, 26].map(offset => ({ s: end + offset, color: colors.exit })),
  ];
  return { start, end, colors, arrows, forward, up: entry.up, width: entry.width, exitWidth: exit.width,
    focus: lerp3(xyz(entry), xyz(peak), .48) };
}

export function loopEdgeTint(loop, station) {
  if (!loop || station < loop.start - 46 || station > loop.end + 32) return null;
  const t = (station - loop.start) / (loop.end - loop.start);
  return t < .5 ? lerp3(loop.colors.entry, loop.colors.crown, smooth(.27, .48, t))
    : lerp3(loop.colors.crown, loop.colors.exit, smooth(.54, .72, t));
}

export function stuntChaseCamera(loop, length, progress, position, forward, right, up, distance, fov) {
  const trailingEye = add(add(position, scale(forward, -distance)), scale(up, 3.1));
  const nearTarget = add(add(position, scale(forward, 4.7)), scale(up, .65));
  if (!loop || !Number.isFinite(progress)) return { eye: trailingEye, target: nearTarget, fov };
  const around = ((progress * length - loop.start) % length + length) % length;
  const fromEntry = around > length / 2 ? around - length : around;
  const span = loop.end - loop.start, fromExit = fromEntry - span;
  const runout = smooth(-.37 * span, -.15 * span, fromExit) * (1 - smooth(5, 30, fromExit));
  if (runout) {
    // The car exits beneath the rising strip. Shorten the tether and pass its edge, not through it.
    const clearance = add(scale(right, -1.9 * loop.exitWidth), scale(forward, Math.min(4.6, distance / 2)));
    return { eye: add(trailingEye, scale(clearance, runout)), target: nearTarget, fov };
  }
  const approach = smooth(-155, -75, fromEntry);
  const shoulder = approach * (1 - smooth(3, .39 * span, fromEntry));
  if (!shoulder) return { eye: trailingEye, target: nearTarget, fov };

  // The return leg crosses the rising toe: stay high and outside until the vehicle climbs clear.
  const lift = 7 * smooth(-20, 0, fromEntry) * (1 - smooth(4, 36, fromEntry));
  const tuck = 6 * smooth(-8, 4, fromEntry) * (1 - smooth(12, 42, fromEntry));
  const eye = add(add(trailingEye, scale(forward, tuck - 8.5 * shoulder)),
    add(scale(right, -(20 + .9 * loop.width) * shoulder), add(scale(up, 3.2 * shoulder), scale(loop.up, lift))));
  const heading = smooth(.45, .95, dot(forward, loop.forward));
  const reveal = approach * (1 - smooth(-35, 6, fromEntry)) * heading;
  if (!reveal) return { eye, target: nearTarget, fov };
  // Mix sight directions, not world positions: the distant crown must not push the car offscreen.
  const towardsLoop = normalize(sub(loop.focus, eye));
  const towardsCar = normalize(sub(nearTarget, eye));
  const look = normalize(lerp3(towardsCar, towardsLoop, .69 * reveal));
  const target = add(eye, scale(look, Math.hypot(...sub(nearTarget, eye))));
  return { eye, target, fov: mix(fov, 78 * Math.PI / 180, reveal) };
}
