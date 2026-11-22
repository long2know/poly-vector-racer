import { Builder, RaceScene } from './scene.js';
import { pose, random } from './engine/math.js';

const palette = {
  mint: { body: [0.13, 0.60, 0.44], light: [0.30, 0.94, 0.72] },
  coral: { body: [0.71, 0.19, 0.42], light: [1, 0.37, 0.62] },
  gold: { body: [0.81, 0.46, 0.10], light: [1, 0.75, 0.31] },
};
const frameColor = [0.15, 0.20, 0.32], railLight = [0.26, 0.49, 0.91];
const tickRate = 120, foamSeconds = 1.5, fadeSeconds = 0.35;
const burstLimit = 3, burstRangeSquared = 64 ** 2;
const chunkWidth = 0.45, gravity = 5;
const foamPieces = makeFoamPieces();

function strip(mesh, x0, z0, x1, z1, y, width, color, material = 0) {
  const dx = x1 - x0, dz = z1 - z0;
  const halfWidth = width / (2 * Math.hypot(dx, dz));
  const x = dz * halfWidth, z = -dx * halfWidth;
  mesh.quad([x0 - x, y, z0 - z], [x1 - x, y, z1 - z], [x1 + x, y, z1 + z], [x0 + x, y, z0 + z],
    color, material);
}

function rectangle(mesh, x0, z0, x1, z1, y, color) {
  mesh.quad([x0, y, z0], [x0, y, z1], [x1, y, z1], [x1, y, z0], color);
}

function outline({ minX, maxX, minZ, maxZ }) {
  return [{ x: minX, z: minZ }, { x: minX, z: maxZ }, { x: maxX, z: maxZ }, { x: maxX, z: minZ }];
}

function makeYard(world) {
  const land = new Builder(), road = new Builder(), props = new Builder();
  const { minX, maxX, minZ, maxZ } = world.bounds, y = world.groundY;
  rectangle(land, minX - 70, minZ - 70, maxX + 70, maxZ + 70, y - 0.4, [0.16, 0.17, 0.25]);
  rectangle(road, minX, minZ, maxX, maxZ, y, [0.095, 0.12, 0.19]);
  const grid = [0.16, 0.20, 0.28];
  for (let x = minX + 8; x < maxX; x += 8) strip(road, x, minZ, x, maxZ, y + 0.008, 0.045, grid);
  for (let z = minZ + 8; z < maxZ; z += 8) strip(road, minX, z, maxX, z, y + 0.008, 0.045, grid);

  for (const [index, { color, width, points }] of world.lanes.entries()) {
    const light = palette[color].light;
    const paint = light.map(channel => 0.07 + channel * 0.22);
    const height = y + 0.025 + index * 0.002;
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1], b = points[i], dx = b.x - a.x, dz = b.z - a.z;
      strip(road, a.x, a.z, b.x, b.z, height, width, paint);
      const length = Math.hypot(dx, dz), fx = dx / length, fz = dz / length;
      for (let along = 0.9; along + 1.35 < length; along += 3.6) for (const side of [-1, 1]) {
        const offset = side * (width / 2 - 0.38);
        const x = a.x + fx * along + fz * offset, z = a.z + fz * along - fx * offset;
        strip(road, x, z, x + fx * 1.35, z + fz * 1.35, height + 0.007, 0.18, light, 4);
      }
    }
  }

  const corners = outline(world.bounds);
  for (let i = 0; i < corners.length; i++) {
    const a = corners[i], b = corners[(i + 1) % corners.length], dx = b.x - a.x, dz = b.z - a.z;
    const length = Math.hypot(dx, dz);
    const wall = pose([a.x, y, a.z], Math.atan2(dx, dz));
    props.box([0, 0.56, length / 2], [0.6, 1.12, length + 0.6], frameColor, 0, wall);
    props.box([0, 1.13, length / 2], [0.52, 0.07, length + 0.5], railLight, 4, wall);
    for (let along = 4; along < length; along += 9)
      props.box([0, 0.53, along], [0.66, 0.18, 2.1], railLight, 4, wall);
  }

  const { x, z } = world.spawn, mint = palette.mint.light;
  for (let tile = -4; tile < 4; tile++) for (let row = 0; row < 2; row++)
    if ((tile + row) % 2 === 0)
      strip(road, x + tile, z + row + 3.5, x + tile + 0.96, z + row + 3.5, y + 0.043, 0.94, mint, 4);
  for (const side of [-1, 1]) {
    props.box([x + side * 4.8, y + 2.25, z + 4.5], [0.48, 4.5, 0.62], frameColor);
    props.box([x + side * 4.8, y + 2.27, z + 4.16], [0.16, 4.2, 0.08], mint, 4);
  }
  props.box([x, y + 4.5, z + 4.5], [10.08, 0.52, 0.62], frameColor);
  props.box([x, y + 4.46, z + 4.16], [9.64, 0.14, 0.08], mint, 4);
  return { land, road, props };
}

function lightCube({ body, light }) {
  const mesh = new Builder();
  const corners = [[-0.42, -0.5], [0.42, -0.5], [0.5, -0.42], [0.5, 0.42],
    [0.42, 0.5], [-0.42, 0.5], [-0.5, 0.42], [-0.5, -0.42]];
  const levels = [[0.018, 0.84], [0.11, 1], [0.42, 1], [0.52, 1], [0.89, 1], [1, 0.83]];
  const rings = levels.map(([y, scale]) => corners.map(([x, z]) => [x * scale, y, z * scale]));
  for (let i = 0; i < corners.length; i++) {
    const j = (i + 1) % corners.length;
    for (let level = 1; level < rings.length; level++) {
      const glow = level === 3 || level === 5;
      mesh.quad(rings[level - 1][i], rings[level][i], rings[level][j], rings[level - 1][j],
        glow ? light : body, glow ? 4 : 0);
    }
    mesh.triangle([0, 1, 0], rings.at(-1)[j], rings.at(-1)[i], body);
  }
  mesh.box([0, 1.008, 0], [0.47, 0.016, 0.12], light, 4);
  mesh.box([0, 1.008, 0], [0.12, 0.016, 0.47], light, 4);
  return mesh;
}

function foamChunk({ body, light }) {
  const mesh = new Builder();
  mesh.box([0, -0.1, 0], [1, 0.8, 1], body);
  mesh.box([0, 0.4, 0], [1, 0.2, 1], light, 4);
  return mesh;
}

function makeFoamPieces() {
  const rng = random(675101), pieces = [];
  // The shared trajectories are in cube widths: two layers of four foam sections.
  for (const y of [0.25, 0.75]) for (const x of [-0.25, 0.25]) for (const z of [-0.25, 0.25]) {
    const angle = Math.atan2(z, x) + (rng() - 0.5) * 0.55;
    const speed = 1.55 + rng() * 0.9;
    const lift = y - chunkWidth / 2;
    const rise = y < 0.5 ? 0.6 + rng() * 0.65 : 0.4 + rng() * 0.5;
    const impact = Math.sqrt(rise ** 2 + 2 * gravity * lift);
    const rebound = impact * 0.36;
    pieces.push({ x, z, lift, rise, land: (rise + impact) / gravity, rebound, bounce: 2 * rebound / gravity,
      vx: Math.cos(angle) * speed, vz: Math.sin(angle) * speed,
      spin: Math.sign(x * z) * (3 + rng() * 5), pitch: Math.sign(z) * (0.5 + rng() * 0.6),
      roll: Math.sign(x) * (0.4 + rng() * 0.5) });
  }
  return pieces;
}

function drawFoam(draws, rig, prop, ground, age) {
  const opacity = Math.min(1, (foamSeconds - age) / fadeSeconds);
  const width = rig.size * chunkWidth, size = [width, width, width];
  for (const piece of foamPieces) {
    const air = Math.min(age, piece.land), hop = Math.min(age - air, piece.bounce);
    const flight = air / piece.land, bounce = hop / piece.bounce;
    const drift = air + hop * 0.48 * (1 - bounce / 2);
    const lift = age <= piece.land ? piece.lift + air * (piece.rise - gravity * air / 2)
      : hop * (piece.rebound - gravity * hop / 2);
    const tilt = 4 * (flight * (1 - flight) + 0.3 * bounce * (1 - bounce));
    const model = pose([prop.x + rig.size * (piece.x + piece.vx * drift), ground + rig.size * Math.max(0, lift),
      prop.z + rig.size * (piece.z + piece.vz * drift)], piece.spin * drift, piece.pitch * tilt, piece.roll * tilt, size);
    // Lift is clearance above the deck; add the rotated box's vertical half-extent.
    model[13] += (Math.abs(model[1]) + Math.abs(model[5]) + Math.abs(model[9])) / 2;
    draws.push({ mesh: rig.foam, model, opacity, shadow: false });
  }
}

export class ArcadeYardScene extends RaceScene {
  constructor(graphics, track, world) {
    super(graphics, track, makeYard(world));
    this.world = world;
    const colors = Object.fromEntries(Object.entries(palette).map(([color, swatch]) => [color, {
      cube: graphics.mesh(lightCube(swatch)),
      foam: graphics.mesh(foamChunk(swatch)),
    }]));
    this.cubes = new Map(world.props.map(prop => [prop.id, {
      ...colors[prop.color], size: prop.size,
    }]));
    this.resultAge = 0;
    this.resultTick = null;

    const mapLine = points => points.map(({ x, z }, i) =>
      `${i ? 'L' : 'M'}${this.mapPoint(x, z).map(v => v.toFixed(2)).join(' ')}`).join(' ');
    this.mapPath = [mapLine(outline(world.bounds)) + ' Z', ...world.lanes.map(lane => mapLine(lane.points))].join(' ');
  }

  update(state, dt, mode, compatibility) {
    const frame = super.update(state, dt, mode, compatibility);
    frame.camera.far = 260;
    frame.camera.fogDistance = 240;
    const activity = state.arcade, finished = activity && state.phase === 'finished';
    this.resultAge = finished && this.resultTick === state.tick ? Math.min(foamSeconds, this.resultAge + dt) : 0;
    this.resultTick = finished ? state.tick : null;
    // The session tick includes the countdown; the hit tick is on the active-play clock.
    const elapsedTick = Math.round((activity?.elapsed ?? 0) * tickRate);
    const nearby = [], y = this.world.groundY;
    for (const prop of activity?.props ?? this.world.props) {
      const rig = this.cubes.get(prop.id);
      if (!rig) continue;
      if (!prop.hit) {
        frame.draws.push({ mesh: rig.cube, model: pose([prop.x, y, prop.z], 0, 0, 0, [rig.size, rig.size, rig.size]) });
        continue;
      }
      if (!Number.isInteger(prop.hitTick) || prop.hitTick <= 0 || prop.hitTick > elapsedTick) continue;
      if ((elapsedTick - prop.hitTick) / tickRate + this.resultAge >= foamSeconds) continue;
      if ((prop.x - state.player.x) ** 2 + (prop.z - state.player.z) ** 2 > burstRangeSquared) continue;
      let index = nearby.length;
      while (index > 0 && nearby[index - 1].hitTick < prop.hitTick) index--;
      if (index < burstLimit) {
        nearby.splice(index, 0, prop);
        if (nearby.length > burstLimit) nearby.pop();
      }
    }
    for (const prop of nearby) drawFoam(frame.draws, this.cubes.get(prop.id), prop, y,
      (elapsedTick - prop.hitTick) / tickRate + this.resultAge);
    return frame;
  }

  mapPoint(x, z) {
    const { minX, maxX, minZ, maxZ } = this.world.bounds, span = Math.max(maxX - minX, maxZ - minZ);
    return [80 + (x - (minX + maxX) / 2) / span * 136, 80 + (z - (minZ + maxZ) / 2) / span * 136];
  }

  mapRoadPath() {
    return this.mapPath;
  }
}
