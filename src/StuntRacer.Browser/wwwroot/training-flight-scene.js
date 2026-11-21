import { FlightScene } from './flight-scene.js';
import { Builder } from './scene.js';
import { pose } from './engine/math.js';

const teal = [.24, .96, .94], gold = [1, .57, .15], green = [.43, 1, .42];
const slate = [.13, .20, .24], ivory = [.93, .95, .82];

function practiceDrone(color) {
  const mesh = new Builder();
  mesh.box([0, 0, 0], [10, 5, 15], slate);
  mesh.box([0, 2, 1], [6, 2, 12], color, 4);
  for (const x of [-10, 10]) {
    mesh.beam([0, 0, 0], [x, 0, 2], .9, ivory);
    mesh.beam([x, -.8, 2], [x, .8, 2], 4.5, color, 4, 8);
    mesh.beam([x, .8, 2], [x, 1.4, 2], 2.8, slate, 0, 8);
  }
  return mesh;
}

function groundBeacon(color) {
  const mesh = new Builder();
  mesh.beam([0, .1, 0], [0, 1.5, 0], 24, slate, 0, 8);
  mesh.beam([0, 1.5, 0], [0, 2.2, 0], 18, color, 4, 8);
  mesh.beam([0, 2.2, 0], [0, 16, 0], 5, slate);
  mesh.beam([0, 15, 0], [0, 19, 0], 9, ivory, 0, 6);
  mesh.beam([0, 19, 0], [0, 26, 0], 7, color, 4, 6);
  return mesh;
}

export class TrainingFlightScene extends FlightScene {
  constructor(graphics, world) {
    super(graphics, world);
    this.practiceMeshes = {
      drone: [graphics.mesh(practiceDrone(teal)), graphics.mesh(practiceDrone(green))],
      beacon: [graphics.mesh(groundBeacon(gold)), graphics.mesh(groundBeacon(green))]
    };
  }

  update(state, dt, mode) {
    const frame = super.update(state, dt, mode);
    for (const { kind, position, tagged } of state.flight.training.props) {
      frame.draws.push({
        mesh: this.practiceMeshes[kind][Number(tagged)],
        model: pose([position.x, position.y, position.z], kind === 'drone' ? state.flight.time * .5 : 0),
        shadow: false
      });
    }
    return frame;
  }

  dispose() {
    for (const choices of Object.values(this.practiceMeshes)) {
      for (const mesh of choices) this.graphics.releaseMesh(mesh);
    }
    super.dispose?.();
  }
}
