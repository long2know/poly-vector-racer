import { Builder } from './scene.js';
import { add, clamp, cross, lerp3, mix, multiply, normalize, pose, random, scale, sub, transform } from './engine/math.js';
import { triangulate } from './engine/triangulate.js';
import { appendWalker, makeServiceCar, makeWalkerRig } from './world-actors.js';

const steel = [0.31, 0.36, 0.38], dark = [0.045, 0.065, 0.08], orange = [1, 0.27, 0.045];
const phosphor = [0.23, 0.87, 0.53];
const pilotEye = [0, 1.59, .89];
const cockpitFov = 72 * Math.PI / 180;
const viewSteps = { forward: 0, 'forward-left': -1, 'forward-right': 1, left: -2, right: 2,
  'rear-left': -3, 'rear-right': 3, rear: 4 };
const orbitPitchLimit = 50 * Math.PI / 180;
const displayFrames = [-1, 0, 1].map(side =>
  pose([side * .61, side === 0 ? .81 : .93, 2.45 - Math.abs(side) * .11], .21 * side, -.24));

export function flightReadouts(aircraft, world) {
  const runway = world.landingSurfaces.reduce((best, surface) => {
    const distance = Math.hypot(surface.centerX - aircraft.x, surface.centerZ - aircraft.z);
    return !best || distance < best.distance ? { surface, distance } : best;
  }, null);
  const bearing = runway ? Math.atan2(runway.surface.centerX - aircraft.x, runway.surface.centerZ - aircraft.z) - aircraft.yaw : null;
  return {
    heading: ((aircraft.yaw * 180 / Math.PI) % 360 + 360) % 360,
    pitch: aircraft.pitch * 180 / Math.PI,
    bank: aircraft.roll * 180 / Math.PI,
    runway: runway?.surface.name ?? null,
    range: runway?.distance ?? null,
    bearing: bearing === null ? null : Math.atan2(Math.sin(bearing), Math.cos(bearing)),
  };
}

export function aircraftFrame(aircraft) {
  const forward = normalize([aircraft.forward.x, aircraft.forward.y, aircraft.forward.z]);
  const up = normalize([aircraft.up.x, aircraft.up.y, aircraft.up.z]);
  const right = normalize(cross(up, forward));
  return new Float32Array([...right, 0, ...up, 0, ...forward, 0, aircraft.x, aircraft.y, aircraft.z, 1]);
}

function makeJet() {
  const jet = new Builder();
  jet.box([0, 0, -0.5], [1.65, 1.5, 10.5], steel, 2);
  jet.box([0, 0.25, 1.5], [1.3, 1.25, 5], [0.39, 0.44, 0.45], 2);
  const tip = [0, -0.04, 8.3], nose = [[-0.65, -0.45, 4], [0.65, -0.45, 4], [0.65, 0.7, 4], [-0.65, 0.7, 4]];
  for (let i = 0; i < 4; i++) jet.triangle(nose[i], nose[(i + 1) % 4], tip, [0.45, 0.49, 0.48], 2);
  for (const side of [-1, 1]) {
    const wing = [[side * 0.7, -0.05, 2], [side * 5.7, -0.25, -2.7],
      [side * 5.8, -0.25, -4.1], [side * 0.7, -0.05, -2.4]];
    jet.quad(...wing, steel, 2);
    jet.quad(...wing.map(point => add(point, [0, -0.17, 0])).reverse(), [0.20, 0.25, 0.28], 2);
    for (let i = 0; i < 4; i++) jet.quad(wing[i], wing[(i + 1) % 4],
      add(wing[(i + 1) % 4], [0, -0.17, 0]), add(wing[i], [0, -0.17, 0]), [0.22, 0.27, 0.29], 2);
    jet.box([side * 5.72, -0.2, -3.2], [0.16, 0.18, 2.2], orange);
    jet.quad([side * 0.65, 0.15, -3.8], [side * 3.1, 0.08, -6.0],
      [side * 3, 0.08, -7.2], [side * 0.6, 0.15, -6.1], steel, 2);
    jet.quad([side * 0.75, 0.5, -2.6], [side * 1.55, 3.3, -5.3],
      [side * 1.7, 3.2, -6.6], [side * 0.8, 0.5, -6.1], [0.27, 0.33, 0.36], 2);
    jet.beam([side * 0.72, -0.25, -4.5], [side * 0.72, -0.25, -6.6], 0.55, dark, 2, 12);
    jet.box([side * 0.91, -0.25, 0.3], [0.4, 0.85, 2.1], dark, 2);
    jet.box([side * 0.85, 1.0, -5.9], [0.2, 0.5, 0.15], orange);
  }
  return jet;
}

function makeCanopy() {
  const glass = new Builder();
  glass.quad([-0.65, 0.8, 0], [-0.5, 1.9, 0.2], [-0.38, 1.55, 3], [-0.63, 0.8, 3.3], [0.2, 0.62, 0.7], 2);
  glass.quad([0.65, 0.8, 0], [0.63, 0.8, 3.3], [0.38, 1.55, 3], [0.5, 1.9, 0.2], [0.2, 0.62, 0.7], 2);
  glass.quad([-0.5, 1.9, 0.2], [0.5, 1.9, 0.2], [0.38, 1.55, 3], [-0.38, 1.55, 3], [0.24, 0.66, 0.72], 2);
  glass.quad([-0.38, 1.55, 3], [0.38, 1.55, 3], [0.63, 0.8, 3.3], [-0.63, 0.8, 3.3], [0.2, 0.62, 0.7], 2);
  return glass;
}

function makeGear() {
  const gear = new Builder();
  for (const [x, z] of [[-1.1, -2], [1.1, -2], [0, 4.1]]) {
    gear.beam([x * 0.65, -0.6, z], [x, -2.0, z], 0.1, [0.6, 0.64, 0.62], 2, 8);
    gear.cylinder([x, -2.05, z], 0.35, 0.28, dark, 0, 12);
  }
  return gear;
}

function makeCockpit() {
  const shell = new Builder(), glass = new Builder();
  const panel = [.14, .17, .18], bezel = [.25, .28, .29], sill = [.19, .23, .24];
  const edge = [.30, .35, .35], caution = [.92, .48, .11];
  const skin = [.32, .36, .34], matte = [.07, .089, .096];
  const profile = [
    [-1.17, .63, 2.21], [-1.05, 1.14, 2.44], [-.72, 1.32, 2.55],
    [0, 1.37, 2.63], [.72, 1.32, 2.55], [1.05, 1.14, 2.44], [1.17, .63, 2.21],
  ];
  const nose = [[-.63, .76, 4.42], [-.56, .91, 4.65], [-.37, .98, 4.8], [0, 1.01, 4.95],
    [.37, .98, 4.8], [.56, .91, 4.65], [.63, .76, 4.42]];
  const apron = profile.map(([x, y, z]) => [x * .9, Math.min(.60, y - .18), z - .28]);
  for (let i = 1; i < profile.length; i++) {
    shell.quad(profile[i - 1], nose[i - 1], nose[i], profile[i], i % 2 ? matte : skin, 2);
    shell.quad(apron[i - 1], profile[i - 1], profile[i], apron[i], panel, 2);
    shell.beam(profile[i - 1], profile[i], .014, bezel, 2, 4);
    shell.quad(apron[i - 1], apron[i], [apron[i][0], -.18, .8],
      [apron[i - 1][0], -.18, .8], dark, 2);
  }
  for (const [i, frame] of displayFrames.entries()) {
    const height = i === 1 ? .46 : .47, width = .49;
    shell.box([0, 0, .003], [width + .15, height + .14, .14], matte, 2, frame);
    shell.box([0, 0, -.076], [width + .073, height + .066, .04], bezel, 2, frame);
    shell.box([0, 0, -.108], [width, height, .028], [.008, .041, .035], 4, frame);
    for (const side of [-1, 1]) for (let key = 0; key < 5; key++) {
      shell.box([side * (width / 2 + .044), -.17 + key * .082, -.104],
        [.033, .045, .031], key === 2 ? [.48, .52, .47] : [.21, .24, .24], 2, frame);
    }
    for (let key = 0; key < 5; key++)
      shell.box([-.18 + key * .09, -height / 2 - .045, -.104],
        [.048, .032, .027], steel, 2, frame);
    for (const x of [-width / 2, width / 2]) for (const y of [-height / 2 - .026, height / 2 + .026])
      shell.box([x, y, -.109], [.024, .023, .017], [.43, .47, .44], 2, frame);
  }
  // The windscreen bow follows the bubble near the pilot, not a roof cage in front of the view.
  const arc = [[-1.09, 1.04, 2.08], [-1.17, 1.34, 1.89], [-1.13, 1.80, 1.71],
    [-.98, 2.12, 1.63], [-.65, 2.35, 1.59], [0, 2.51, 1.57],
    [.65, 2.35, 1.59], [.98, 2.12, 1.63], [1.13, 1.80, 1.71], [1.17, 1.34, 1.89], [1.09, 1.04, 2.08]];
  for (let i = 1; i < arc.length; i++) {
    shell.beam(arc[i - 1], arc[i], .025, sill, 2, 6);
    shell.beam(add(arc[i - 1], [0, 0, -.019]), add(arc[i], [0, 0, -.019]), .008, edge, 2, 4);
  }
  for (const s of [-1, 1]) {
    const a = [s * 1.24, 1.05, .6], b = [s * .97, .87, 2.27], c = [s * .68, .64, 2.13], d = [s * .64, .77, .59];
    shell.quad(a, b, c, d, panel, 2);
    shell.quad(a, d, [s * .65, .05, .59], [s * 1.15, .12, .61], dark, 2);
    shell.quad([s * 1.24, 1.12, .67], [s * 1.10, 1.09, 2.27], b, a, sill, 2);
    shell.beam([s * 1.24, 1.13, .67], [s * 1.10, 1.1, 2.27], .014, edge, 2, 6);
    for (let i = 0; i < 5; i++) {
      const z = .96 + i * .24, y = .96 - i * .024, x = s * (.97 - i * .019);
      shell.box([x, y, z], [.126, .027, .11], matte, 2);
      shell.beam([x, y, z], [x, y + .048, z + .01], .012, i % 4 === 0 ? caution : bezel, 2, 4);
    }
  }
  shell.box([0, 1.202, 2.45], [.38, .075, .13], matte, 2);
  for (let row = 0; row < 2; row++) for (let key = 0; key < 7; key++)
    shell.box([-.15 + key * .05, 1.18 + row * .036, 2.379], [.027, .021, .016],
      key % 3 === 0 ? edge : dark, 2);
  const base = [[-.296, 1.36, 2.91], [.296, 1.36, 2.91]];
  const rim = [base[0], [-.337, 1.72, 3.0], [-.304, 1.88, 3.06], [-.249, 1.914, 3.07],
    [.249, 1.914, 3.07], [.304, 1.88, 3.06], [.337, 1.72, 3.0], base[1]];
  for (let i = 1; i < rim.length - 1; i++) glass.triangle(rim[0], rim[i], rim[i + 1], [.12, .43, .34], 4);
  for (let i = 1; i < rim.length; i++)
    shell.beam(rim[i - 1], rim[i], .005, edge, 2, 4);
  for (let i = 0; i < 2; i++) {
    shell.box(add(base[i], [0, -.026, 0]), [.036, .064, .12], matte, 2);
  }
  return { shell, glass };
}

function paintRunway(mesh, points) {
  if (points.length !== 4) throw new Error('An original landing-pad footprint must contain four vertices.');
  const distance = (a, b) => Math.hypot(a[0] - b[0], a[2] - b[2]);
  const edge = points.map((a, i) => distance(a, points[(i + 1) % 4]))
    .reduce((best, length, i, lengths) => length > lengths[best] ? i : best, 0);
  const a = points[edge], b = points[(edge + 1) % 4], c = points[(edge + 2) % 4], d = points[(edge + 3) % 4];
  const start = lerp3(a, d, 0.5), end = lerp3(b, c, 0.5), length = distance(start, end);
  const forward = normalize(sub(end, start));
  const right = [forward[2], 0, -forward[0]];
  const width = (distance(a, d) + distance(b, c)) / 2;
  const frame = pose([start[0], 0.06, start[2]], Math.atan2(forward[0], forward[2]));
  for (let z = 25; z < length - 25; z += 65)
    mesh.box([0, 0, z], [0.8, 0.015, Math.min(28, length - z)], [0.82, 0.85, 0.76], 0, frame);
  for (const side of [-1, 1]) {
    const offset = side * Math.max(0, width / 2 - 1.5);
    const p = add(start, scale(right, offset)), q = add(end, scale(right, offset));
    mesh.beam([p[0], 0.06, p[2]], [q[0], 0.06, q[2]], 0.12, [0.82, 0.85, 0.76], 0);
  }
}

function makeWorld(world) {
  const sea = new Builder(), ground = new Builder(), buildings = new Builder();
  if (!world.geography) sea.quad([-50000, world.seaHeight - 0.02, -50000], [-50000, world.seaHeight - 0.02, 50000],
    [50000, world.seaHeight - 0.02, 50000], [50000, world.seaHeight - 0.02, -50000],
    world.sourceWorld ? [0.21, 0.32, 0.18] : [0.07, 0.27, 0.34], world.sourceWorld ? 3 : 5);
  if (world.sourceWorld) {
    const vertex = point => [point.x, point.y, point.z];
    for (const surface of world.sourceWorld.surfaces) {
      const points = surface.vertices.map(vertex);
      const water = surface.kind === 'water';
      const colour = water ? [0.07, 0.27, 0.34] : surface.kind === 'runway' ? [0.13, 0.145, 0.15] : [0.23, 0.33, 0.19];
      for (const triangle of triangulate(points))
        ground.triangle(...triangle.map(i => points[i]), colour, water ? 5 : surface.kind === 'runway' ? 1 : 3);
      if (surface.kind === 'runway') paintRunway(ground, points);
    }
    for (const object of world.sourceWorld.objects) {
      const points = object.vertices.map(vertex);
      for (const face of object.faces) {
        const polygon = face.vertices.map(i => {
          if (!points[i]) throw new Error(`Missing original vertex in ${object.name}.`);
          return points[i];
        });
        for (const triangle of triangulate(polygon))
          buildings.triangle(...triangle.map(i => polygon[i]), face.colour, 2);
      }
    }
    return { sea, ground, buildings };
  }
  for (const island of world.islands) {
    const frame = pose([island.centerX, world.seaHeight, island.centerZ], island.heading);
    ground.box([0, (island.height - world.seaHeight) / 2, 0],
      [island.width, island.height - world.seaHeight, island.length], [0.21, 0.32, 0.18], 3, frame);
  }
  for (const runway of world.landingSurfaces) {
    const frame = pose([runway.centerX, runway.height + 0.025, runway.centerZ], runway.heading);
    ground.box([0, 0, 0], [runway.width, 0.04, runway.length], [0.12, 0.14, 0.15], 1, frame);
    for (const side of [-1, 1]) {
      ground.box([side * (runway.width / 2 - 2), 0.04, 0], [0.6, 0.03, runway.length], [0.82, 0.83, 0.74], 0, frame);
      for (let z = -runway.length / 2 + 25; z < runway.length / 2; z += 65)
        buildings.box([side * (runway.width / 2 + 3), 0.35, z], [0.6, 0.65, 0.6], [0.2, 0.7, 0.85], 4, frame);
    }
    for (let z = -runway.length / 2 + 25; z < runway.length / 2; z += 60)
      ground.box([0, 0.05, z], [1.3, 0.03, 30], [0.83, 0.85, 0.78], 0, frame);
    for (const end of [-1, 1]) for (let x = -3; x <= 3; x++) {
      ground.box([x * 7, 0.05, end * (runway.length / 2 - 90)], [3.3, 0.03, 52], [0.83, 0.85, 0.78], 0, frame);
    }
    if (world.geography) continue;
    for (let i = 0; i < 5; i++) {
      const z = -runway.length / 2 + 200 + i * 120;
      buildings.box([runway.width / 2 + 160, 12, z], [60, 24, 85], [0.35, 0.39, 0.38], 2, frame);
      buildings.box([runway.width / 2 + 160, 25, z], [66, 2, 91], dark, 2, frame);
    }
    buildings.box([runway.width / 2 + 105, 22, -200], [16, 44, 16], [0.56, 0.57, 0.49], 2, frame);
    buildings.box([runway.width / 2 + 105, 47, -200], [28, 10, 28], [0.18, 0.36, 0.4], 2, frame);
  }
  const rng = random(198818);
  for (let i = 0; i < 70; i++) {
    const x = (rng() - 0.5) * 2600, z = (rng() - 0.5) * 4800;
    if (Math.abs(x) < 450) continue;
    const island = world.islands.find(item => Math.abs(x - item.centerX) < item.width / 2 &&
      Math.abs(z - item.centerZ) < item.length / 2 && item.heading === 0);
    if (!island) continue;
    const height = 10 + rng() * 65, width = 25 + rng() * 50;
    buildings.box([x, island.height + height / 2, z], [width, height, width], [0.39 + rng() * 0.12, 0.43, 0.39], 2);
  }
  return { sea, ground, buildings };
}

export class FlightScene {
  constructor(graphics, world) {
    this.graphics = graphics;
    this.world = world;
    const landmarks = [];
    for (const object of world.sourceWorld?.objects ?? []) {
      const id = Number(object.name.match(/^Original scenery (\d+)$/)?.[1]);
      if (![48, 49, 50, 51, 52, 53, 62, 102, 103].includes(id)) continue;
      const x = object.vertices.map(p => p.x), z = object.vertices.map(p => p.z);
      landmarks.push({ name: id < 54 ? 'Original bridge' : id === 62 ? 'Original city pyramid' : 'Original harbor tower',
        centerX: (Math.min(...x) + Math.max(...x)) / 2, centerZ: (Math.min(...z) + Math.max(...z)) / 2 });
    }
    this.navigationWorld = { ...world, landingSurfaces: [...world.landingSurfaces, ...landmarks] };
    const geometry = makeWorld(world), island = world.islands[0];
    this.track = {
      name: world.name, length: 0, edges: [],
      points: world.sourceWorld ? world.sourceWorld.mapOutline : world.geography ? [] : [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([x, z]) => ({
        x: island.centerX + x * island.width / 2, z: island.centerZ + z * island.length / 2
      }))
    };
    for (const [name, mesh] of Object.entries(geometry)) this[name] = graphics.mesh(mesh);
    this.jet = graphics.mesh(makeJet());
    this.canopy = graphics.mesh(makeCanopy());
    this.gear = graphics.mesh(makeGear());
    const interior = makeCockpit();
    this.cockpit = graphics.mesh(interior.shell);
    this.combiner = graphics.mesh(interior.glass);
    const line = new Builder(), needle = new Builder(), bar = new Builder();
    line.box([0, 0, 0], [.28, .009, .008], phosphor, 4);
    for (const x of [-1, 1]) line.box([x * .137, -.014, 0], [.009, .036, .008], phosphor, 4);
    needle.beam([0, -0.07, 0], [0, 0.085, 0], 0.005, phosphor, 4);
    needle.triangle([-0.025, 0.045, 0], [0, 0.09, 0], [0.025, 0.045, 0], phosphor, 4);
    bar.box([0, 0, 0], [1, 0.022, 0.012], phosphor, 4);
    this.instrumentLine = graphics.mesh(line);
    this.instrumentNeedle = graphics.mesh(needle);
    this.instrumentBar = graphics.mesh(bar);
    const scales = new Builder();
    for (const [i, frame] of displayFrames.entries()) {
      for (const x of [-.20, .20]) for (let j = 0; j < 5; j++)
        scales.box([x, (j - 2) * .075, -.131], [j === 2 ? .032 : .018, .006, .004], [.12, .37, .26], 4, frame);
      if (i === 1) {
        for (let j = 0; j < 12; j++) {
          const a = j * Math.PI / 6;
          scales.box([Math.sin(a) * .148, Math.cos(a) * .148, -.13], [.013, .013, .005], [.17, .42, .29], 4, frame);
        }
      }
    }
    this.instrumentScales = graphics.mesh(scales);
    const flame = new Builder();
    for (const side of [-1, 1])
      flame.beam([side * 0.72, -0.25, -6.6], [side * 0.72, -0.25, -11.2], 0.42, [0.23, 0.47, 1], 4, 10);
    this.flame = graphics.mesh(flame);
    this.walkerRig = makeWalkerRig(graphics);
    this.serviceCar = makeServiceCar(graphics);
    this.previewTime = 0;
    this.setFlightView('forward');
  }

  setFlightView(view) {
    if (!Object.hasOwn(viewSteps, view)) throw new Error(`Unknown flight view: ${view}`);
    this.flightView = view;
    this.viewYaw = viewSteps[view] * Math.PI / 4;
    this.viewPitch = 0;
    this.eye = null;
  }

  orbitFlightView(dx, dy) {
    const yaw = this.viewYaw + dx * .007;
    this.viewYaw = Math.atan2(Math.sin(yaw), Math.cos(yaw));
    this.viewPitch = clamp(this.viewPitch - dy * .007, -orbitPitchLimit, orbitPitchLimit);
    this.flightView = 'orbit';
  }

  update(state, dt, mode = 'chase') {
    const aircraft = state.flight.aircraft, matrix = aircraftFrame(aircraft);
    const position = [aircraft.x, aircraft.y, aircraft.z];
    const forward = [aircraft.forward.x, aircraft.forward.y, aircraft.forward.z];
    const up = [aircraft.up.x, aircraft.up.y, aircraft.up.z];
    const ready = state.phase === 'ready', cockpit = !ready && mode === 'cockpit';
    const destination = this.world.geography?.destination;
    const draws = destination ? [] : [{ mesh: this.sea, model: pose([aircraft.x, 0, aircraft.z]), shadow: false }];
    if (this.ground.count !== 0) draws.push({ mesh: this.ground });
    if (this.buildings.count !== 0) draws.push({ mesh: this.buildings });
    if (state.flight.activity) {
      const { apron, actors } = state.flight.activity;
      if (!this.activityApron) {
        const mesh = new Builder(), frame = pose([apron.centerX, apron.height, apron.centerZ], apron.heading);
        mesh.quad([-apron.width / 2, 0, -apron.length / 2], [-apron.width / 2, 0, apron.length / 2],
          [apron.width / 2, 0, apron.length / 2], [apron.width / 2, 0, -apron.length / 2], [0.17, 0.19, 0.18], 1);
        for (const x of [-13, 13])
          mesh.box([x, 0.015, 0], [0.15, 0.015, apron.length - 12], [0.86, 0.81, 0.55]);
        this.activityApron = { mesh: this.graphics.mesh(mesh), model: frame };
      }
      draws.push(this.activityApron);
      for (const actor of actors) {
        if (actor.kind === 'walker') appendWalker(draws, this.walkerRig, actor);
        else if (actor.kind === 'aircraft' || actor.kind === 'service-car')
          draws.push({ mesh: actor.kind === 'aircraft' ? this.jet : this.serviceCar,
            model: pose([actor.x, actor.y, actor.z], actor.yaw, actor.pitch, actor.roll) });
        else throw new Error(`Unknown ambient world actor: ${actor.kind}`);
      }
    }
    if (cockpit) {
      const readout = flightReadouts(aircraft, this.navigationWorld);
      const displayModel = (i, p, roll, size) => multiply(matrix,
        multiply(displayFrames[i], pose(p, 0, 0, roll, size)));
      draws.push({ mesh: this.cockpit, model: matrix, shadow: false });
      draws.push({ mesh: this.combiner, model: matrix, opacity: .095, shadow: false });
      draws.push({ mesh: this.instrumentScales, model: matrix, shadow: false });
      draws.push({ mesh: this.instrumentLine, shadow: false,
        model: displayModel(0, [0, -clamp(aircraft.pitch, -.5, .5) * .07, -.144], -aircraft.roll) });
      if (readout.bearing !== null) draws.push({ mesh: this.instrumentNeedle, shadow: false,
        model: displayModel(1, [0, 0, -.143], -readout.bearing, [1.4, 1.4, 1]) });
      for (const [level, y] of [[aircraft.throttle, .056], [aircraft.fuel, -.056]]) {
        if (level > 0) draws.push({ mesh: this.instrumentBar, shadow: false,
          model: displayModel(2, [-.158 + level * .158, y, -.142], 0, [level * .316, 1, 1]) });
      }
    }
    else {
      draws.push({ mesh: this.jet, model: matrix }, { mesh: this.canopy, model: matrix, opacity: 0.65, shadow: false });
      if (aircraft.gearDown) draws.push({ mesh: this.gear,
        model: multiply(matrix, pose([0, 2.4 - this.world.gearClearance, 0])) });
      if (aircraft.afterburner) draws.push({ mesh: this.flame, model: matrix, shadow: false });
    }
    this.previewTime += dt;
    let eye, target, cameraUp = [0, 1, 0];
    const preview = ready && (!destination || aircraft.onGround);
    if (cockpit) {
      eye = transform(matrix, pilotEye);
      const right = normalize(cross(up, forward));
      target = add(eye, scale(add(scale(forward, Math.cos(this.viewYaw)), scale(right, Math.sin(this.viewYaw))), 180));
      cameraUp = up;
    } else if (preview) {
      const angle = aircraft.yaw + 0.7 + Math.sin(this.previewTime * 0.15) * 0.1;
      eye = add(position, [-Math.sin(angle) * 22, 9, -Math.cos(angle) * 22]);
      target = add(position, [0, 0.5, 0]);
    } else {
      // Orbit about the aircraft's heading, not its bank: a roll must not bring the camera under the runway.
      const distance = 26 + Math.min(aircraft.speed * 0.025, 10);
      const radius = Math.hypot(distance, 8);
      const clearance = aircraft.onGround ? 2 : this.world.seaHeight + 1 - aircraft.y;
      const baseElevation = Math.atan2(8, distance);
      const minimumElevation = Math.asin(clamp(clearance / radius, -1, .95));
      if (aircraft.onGround && this.flightView === 'orbit')
        this.viewPitch = Math.max(this.viewPitch, minimumElevation - baseElevation);
      const elevation = Math.max(baseElevation + this.viewPitch, minimumElevation);
      const angle = aircraft.yaw - this.viewYaw, flat = radius * Math.cos(elevation);
      eye = add(position, [-Math.sin(angle) * flat, radius * Math.sin(elevation), -Math.cos(angle) * flat]);
      const ahead = 25 * Math.max(0, Math.cos(this.viewYaw)) * (1 - Math.abs(this.viewPitch) / orbitPitchLimit);
      target = add(position, [Math.sin(aircraft.yaw) * ahead, 0, Math.cos(aircraft.yaw) * ahead]);
    }
    const activeMode = preview ? 'preview' : cockpit ? 'cockpit' : aircraft.onGround ? 'chase-ground' : 'chase';
    if (!this.eye || this.mode !== activeMode || cockpit || !preview && this.flightView !== 'forward') {
      this.eye = eye; this.target = target;
    }
    else {
      const blend = 1 - Math.exp(-dt * 8);
      this.eye = lerp3(this.eye, eye, blend);
      this.target = lerp3(this.target, target, blend);
    }
    this.mode = activeMode;
    return { draws, camera: { eye: this.eye, target: this.target, up: cameraUp,
      fov: cockpit ? cockpitFov : 65 * Math.PI / 180, far: 30000, fogDistance: 10000, logarithmicDepth: true } };
  }

  mapPoint(x, z) {
    if (this.world.sourceWorld) {
      this.mapBounds ??= this.track.points.reduce((b, p) =>
        [Math.min(b[0], p.x), Math.min(b[1], p.z), Math.max(b[2], p.x), Math.max(b[3], p.z)],
      [Infinity, Infinity, -Infinity, -Infinity]);
      const [minX, minZ, maxX, maxZ] = this.mapBounds;
      const span = Math.max(maxX - minX, maxZ - minZ);
      return [mix(12, 148, (x - (minX + maxX - span) / 2) / span),
        mix(12, 148, (z - (minZ + maxZ - span) / 2) / span)];
    }
    return [mix(12, 148, (x + 5000) / 10000), mix(12, 148, (z + 5000) / 10000)];
  }

  mapSurfacePaths() {
    return this.world.sourceWorld.surfaces.filter(surface => surface.kind === 'water').map(surface =>
      surface.vertices.map((point, index) => {
        const [x, z] = this.mapPoint(point.x, point.z);
        return `${index ? 'L' : 'M'}${x.toFixed(2)} ${z.toFixed(2)}`;
      }).join(' ') + ' Z').join(' ');
  }
}
