import { Builder } from './scene.js';
import { multiply, pose } from './engine/math.js';

export function makeWalkerRig(graphics, {
  shirt = [0.12, 0.47, 0.55], skin = [0.7, 0.5, 0.34], trousers = [0.06, 0.08, 0.09],
} = {}) {
  const body = new Builder(), limb = new Builder();
  body.box([0, 1.18, 0], [0.44, 0.65, 0.27], shirt);
  body.box([0, 1.68, 0], [0.28, 0.31, 0.28], skin);
  limb.box([0, -0.34, 0], [0.14, 0.68, 0.16], trousers);
  return { body: graphics.mesh(body), limb: graphics.mesh(limb) };
}

export function appendWalker(draws, rig, walker, { grounded = false, ...style } = {}) {
  const swing = Math.sin(walker.phase) * 0.48 * (walker.stride ?? 1);
  const size = walker.scale ?? 1;
  // Place the lowest swinging foot on the sidewalk rather than the rig origin.
  const foot = grounded ? size * (0.91 - 0.68 * Math.cos(swing) - 0.08 * Math.abs(Math.sin(swing))) : 0;
  const matrix = pose([walker.x, walker.y - foot, walker.z], walker.yaw, 0, 0, [size, size, size]);
  draws.push({ mesh: rig.body, model: matrix, ...style });
  for (const side of [-1, 1]) {
    draws.push({ mesh: rig.limb, model: multiply(matrix, pose([side * 0.11, 0.91, 0], 0, swing * side)), ...style });
    draws.push({ mesh: rig.limb, model: multiply(matrix, pose([side * 0.29, 1.44, 0], 0, -swing * side * 0.8)), ...style });
  }
}

export function makeServiceCar(graphics) {
  const car = new Builder();
  car.box([0, 0.68, 0], [1.8, 0.65, 3.8], [0.85, 0.56, 0.12], 2);
  car.box([0, 1.2, -0.25], [1.5, 0.65, 1.9], [0.22, 0.37, 0.4], 2);
  for (const x of [-0.86, 0.86]) for (const z of [-1.12, 1.12])
    car.cylinder([x, 0.36, z], 0.35, 0.22, [0.045, 0.06, 0.07], 0, 8);
  for (const x of [-0.58, 0.58]) {
    car.box([x, 0.71, 1.92], [0.34, 0.18, 0.05], [0.94, 0.9, 0.65], 4);
    car.box([x, 0.71, -1.92], [0.34, 0.15, 0.05], [0.9, 0.11, 0.04], 4);
  }
  return graphics.mesh(car);
}
