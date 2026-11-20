export const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
export const mix = (a, b, t) => a + (b - a) * t;
export const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
export const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
export const scale = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
export const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
export const cross = (a, b) => [
  a[1] * b[2] - a[2] * b[1],
  a[2] * b[0] - a[0] * b[2],
  a[0] * b[1] - a[1] * b[0],
];
export const normalize = a => scale(a, 1 / (Math.hypot(...a) || 1));
export const lerp3 = (a, b, t) => a.map((v, i) => mix(v, b[i], t));
export const angleMix = (a, b, t) => a + Math.atan2(Math.sin(b - a), Math.cos(b - a)) * t;
export const identity = () => new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

export function multiply(a, b) {
  const out = new Float32Array(16);
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
    out[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1]
      + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
  }
  return out;
}

export function perspective(fov, aspect, near, far) {
  const f = 1 / Math.tan(fov / 2), nf = 1 / (far - near);
  return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0,
    0, 0, (far + near) * nf, 1, 0, 0, -2 * far * near * nf, 0]);
}

export function orthographic(radius, near, far) {
  return new Float32Array([1 / radius, 0, 0, 0, 0, 1 / radius, 0, 0,
    0, 0, 2 / (far - near), 0, 0, 0, -(far + near) / (far - near), 1]);
}

export function lookAt(eye, target, up = [0, 1, 0]) {
  // Match the simulation's +X right, +Y up, +Z forward convention.
  const z = normalize(sub(target, eye)), x = normalize(cross(up, z)), y = cross(z, x);
  return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0,
    x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
}

export function pose(position, yaw = 0, pitch = 0, roll = 0, size = [1, 1, 1]) {
  const sy = Math.sin(yaw), cy = Math.cos(yaw), sp = Math.sin(pitch), cp = Math.cos(pitch);
  const sr = Math.sin(roll), cr = Math.cos(roll);
  const forward = [sy * cp, sp, cy * cp], right0 = [cy, 0, -sy];
  const up0 = cross(forward, right0);
  const right = add(scale(right0, cr), scale(up0, sr));
  const up = sub(scale(up0, cr), scale(right0, sr));
  return new Float32Array([right[0] * size[0], right[1] * size[0], right[2] * size[0], 0,
    up[0] * size[1], up[1] * size[1], up[2] * size[1], 0,
    forward[0] * size[2], forward[1] * size[2], forward[2] * size[2], 0,
    ...position, 1]);
}

export function transform(m, p) {
  return [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12],
    m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13],
    m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]];
}

export function random(seed = 7319) {
  return () => {
    seed = (Math.imul(seed, 1664525) + 1013904223) | 0;
    return (seed >>> 0) / 4294967296;
  };
}
