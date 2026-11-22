import { add, clamp, cross, lerp3, normalize, pose, scale, sub, transform } from './engine/math.js';

const bucketSize = 64, wallClearance = .4, eyeClearance = 1.1;
const key = (x, z) => `${x}/${z}`;
const bucket = n => Math.floor(n / bucketSize);

function indexBuildings(buildings, bounds) {
  const grid = new Map();
  for (const building of buildings ?? []) {
    const xs = building.ring.map(p => p[0]), zs = building.ring.map(p => p[1]);
    const minX = Math.min(...xs) - wallClearance, maxX = Math.max(...xs) + wallClearance;
    const minZ = Math.min(...zs) - wallClearance, maxZ = Math.max(...zs) + wallClearance;
    const entry = { ...building, minX, maxX, minZ, maxZ };
    const margin = 32;
    const x0 = bucket(Math.max(minX, (bounds?.minX ?? -Infinity) - margin));
    const x1 = bucket(Math.min(maxX, (bounds?.maxX ?? Infinity) + margin));
    const z0 = bucket(Math.max(minZ, (bounds?.minZ ?? -Infinity) - margin));
    const z1 = bucket(Math.min(maxZ, (bounds?.maxZ ?? Infinity) + margin));
    for (let x = x0; x <= x1; x++) for (let z = z0; z <= z1; z++) {
      const cell = key(x, z);
      if (!grid.has(cell)) grid.set(cell, []);
      grid.get(cell).push(entry);
    }
  }
  return grid;
}

function inFootprint(ring, x, z, margin = 0) {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [a, b] = ring[j], [c, d] = ring[i];
    if ((b > z) !== (d > z) && x < a + (z - b) * (c - a) / (d - b)) inside = !inside;
    if (!margin) continue;
    const dx = c - a, dz = d - b;
    const t = clamp(((x - a) * dx + (z - b) * dz) / (dx * dx + dz * dz || 1), 0, 1);
    if (Math.hypot(x - a - dx * t, z - b - dz * t) < margin) return true;
  }
  return inside;
}

function eyeHits(eye, b) {
  const [x, y, z] = eye;
  return x >= b.minX && x <= b.maxX && z >= b.minZ && z <= b.maxZ &&
    y >= b.bottom - wallClearance && y <= b.top + wallClearance && inFootprint(b.ring, x, z, wallClearance);
}

function rayHits(eye, point, b) {
  const [x, y, z] = eye, [px, py, pz] = point;
  if (Math.max(x, px) < b.minX || Math.min(x, px) > b.maxX ||
      Math.max(z, pz) < b.minZ || Math.min(z, pz) > b.maxZ) return false;
  const low = b.bottom - .12, high = b.top + .12;
  if (Math.min(y, py) > high || Math.max(y, py) < low) return false;
  // Clip the sightline to the roof/floor slab before testing the real, possibly concave footprint.
  const dy = py - y, enter = dy ? (low - y) / dy : 0, exit = dy ? (high - y) / dy : 1;
  const from = Math.max(0, Math.min(enter, exit)), to = Math.min(1, Math.max(enter, exit));
  const dx = px - x, dz = pz - z;
  if (inFootprint(b.ring, x + dx * from, z + dz * from) ||
      inFootprint(b.ring, x + dx * to, z + dz * to)) return true;
  for (let i = 0, j = b.ring.length - 1; i < b.ring.length; j = i++) {
    const [a, c] = b.ring[j], [q, r] = b.ring[i];
    const hx = q - a, hz = r - c, det = dx * hz - dz * hx;
    if (Math.abs(det) < 1e-8) continue;
    const t = ((a - x) * hz - (c - z) * hx) / det;
    const u = ((a - x) * dz - (c - z) * dx) / det;
    if (t >= from && t <= to && u >= 0 && u <= 1) return true;
  }
  return false;
}

function aim(focus, eye, fov, aspect, x, y) {
  const towards = sub(focus, eye), forward = normalize(towards);
  const right = normalize(cross([0, 1, 0], forward)), up = cross(forward, right);
  const span = Math.hypot(...towards) * Math.tan(fov / 2);
  return add(add(focus, scale(right, -x * span * aspect)), scale(up, -y * span));
}

export class SeattleCamera {
  constructor(terrain, bounds) {
    this.terrain = terrain;
    this.bounds = bounds;
  }

  reset() {
    this.offset = null;
    this.mode = null;
  }

  ground(x, z) {
    return Math.max(this.terrain.sample(x, z) ?? -Infinity, this.terrain.visualSample(x, z) ?? -Infinity);
  }

  groundBlocks(eye, point) {
    const [x, y, z] = eye, [dx, dy, dz] = sub(point, eye);
    const steps = Math.ceil(Math.hypot(dx, dz) / 1.25);
    for (let i = 1; i < steps; i++) {
      const t = i / steps;
      if (y + dy * t < this.ground(x + dx * t, z + dz * t) + .18) return true;
    }
    return false;
  }

  update(camera, player, dt, mode, ready, orbit, canvas, volumes) {
    const active = ready ? 'orbit' : mode;
    if (this.mode !== active) this.reset();
    this.mode = active;
    if (active === 'cockpit')
      return { mode: active, eye: camera.eye, target: camera.target, up: camera.up, fov: camera.fov, corrected: false };
    if (volumes !== this.volumes || !this.grid) {
      this.volumes = volumes;
      this.grid = indexBuildings(volumes, this.bounds);
    }
    const { x, y, z, yaw, pitch, roll } = player;
    const width = canvas.clientWidth || canvas.width, height = canvas.clientHeight || canvas.height;
    const aspect = width / height || 1, portrait = width <= 700 && aspect < .85;
    const focus = [x, y + .28, z];
    const body = pose([x, y, z], yaw, pitch, roll);
    // Include both ends and the wheel span, not just a sightline to the roof or centre.
    const probes = [focus];
    for (const side of [-1.34, 1.34]) for (const end of [-2.14, 2.14])
      probes.push(transform(body, [side, -.55, end]));

    let requested = camera.eye;
    if (ready) {
      // The two-row portrait menu leaves a shallow slot between the toolbar and the title.
      const angle = yaw + (portrait ? 2.42 : 1.08) + Math.sin(orbit * .1) * .08;
      const radius = portrait ? 30 : 10.2;
      requested = [x - Math.sin(angle) * radius, y + (portrait ? 4.5 : 5.6), z - Math.cos(angle) * radius];
      if (portrait) camera.fov = 77 * Math.PI / 180;
    } else if (portrait) {
      requested = [requested[0], Math.max(requested[1], y + 4.2), requested[2]];
      camera.fov += 6 * Math.PI / 180;
    }
    const [dx, , dz] = sub(requested, focus), radius = Math.hypot(dx, dz);
    const reach = Math.max(radius, 5) + wallClearance;
    const nearby = new Set();
    for (let a = bucket(x - reach); a <= bucket(x + reach); a++) for (let b = bucket(z - reach); b <= bucket(z + reach); b++)
      for (const building of this.grid.get(key(a, b)) ?? []) nearby.add(building);
    const floor = eye => [eye[0], Math.max(eye[1], this.ground(eye[0], eye[2]) + eyeClearance), eye[2]];
    const blocker = eye => {
      for (const building of nearby)
        if (eyeHits(eye, building) || probes.some(point => rayHits(eye, point, building))) return building.id;
      return probes.some(point => this.groundBlocks(eye, point)) ? 'terrain' : null;
    };
    const angle = Math.atan2(dx, dz);
    const candidate = (offset, distance, level = requested[1]) =>
      floor([x + Math.sin(angle + offset) * distance, level, z + Math.cos(angle + offset) * distance]);
    const preferred = floor(requested);
    const obstruction = blocker(preferred);
    let eye = preferred, clear = obstruction === null;
    if (!clear) {
      const close = Math.min(radius, portrait && ready ? 27 : 5.3);
      const lensClear = eye => {
        for (const building of nearby) if (eyeHits(eye, building)) return false;
        return true;
      };
      let unembedded = lensClear(preferred) ? preferred : null;
      // Retract along the boom first. If an obstruction is close to the subject, look around it.
      search: for (const offset of [0, .32, -.32, .65, -.65, 1.1, -1.1, 1.7, -1.7, Math.PI]) {
        for (const length of offset ? [radius, close] : [.9 * radius, .8 * radius, .66 * radius, close]) {
          if (length < close) continue;
          const alternative = candidate(offset, length);
          if (blocker(alternative) === null) {
            eye = alternative;
            clear = true;
            break search;
          }
          if (!unembedded && lensClear(alternative)) unembedded = alternative;
        }
      }
      if (!clear) {
        // A high close view also keeps the lens outside a wall if the car itself is inside scenery.
        for (const lift of [7, 11, 15]) {
          const alternative = candidate(0, 3.2, y + lift);
          if (!lensClear(alternative)) continue;
          unembedded ??= alternative;
          if (blocker(alternative) === null) {
            eye = alternative;
            clear = true;
            break;
          }
        }
        if (!clear && unembedded) eye = unembedded;
      }
    }
    if (clear && this.offset) {
      const eased = floor(lerp3(add(preferred, this.offset), eye, 1 - Math.exp(-dt * 5)));
      if (blocker(eased) === null) eye = eased;
    }
    const offset = sub(eye, preferred);
    this.offset = Math.hypot(...offset) > .02 ? offset : null;
    const correction = Math.hypot(...sub(eye, requested));
    if (ready || portrait) {
      const [screenX, screenY] = ready ? (portrait ? [-.37, .71] : [.25, -.10]) : [0, -.21];
      camera.target = aim(focus, eye, camera.fov, aspect, screenX, screenY);
    } else if (correction > 0) {
      camera.target = lerp3(focus, camera.target, clamp(1 - correction * .12, .35, 1));
    }
    camera.eye = eye;
    return { mode: active, eye, target: camera.target, up: camera.up, fov: camera.fov,
      requested, corrected: correction > .02, obstruction, clear };
  }
}
