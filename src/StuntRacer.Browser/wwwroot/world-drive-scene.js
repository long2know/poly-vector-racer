import { RaceScene } from './scene.js';
import { SeattleCamera } from './world-drive-camera.js';
import { SeattleWalkers } from './world-drive-walkers.js';
import { WorldScenery } from './world-flight-scene.js';
import { WorldTerrain } from './world-terrain.js';

const driveRoads = new WeakMap();

export function seattleRegion(catalog) {
  return catalog?.regions.find(region => region.id === 'conus' && region.roads?.id === 'seattle') ?? null;
}

export async function loadSeattleDrive(api, catalog) {
  const region = seattleRegion(catalog);
  if (!region) throw new Error('This World Flight package does not include Seattle road data.');
  const area = JSON.parse(api.SeattleDriveAreaInfo());
  const terrain = new WorldTerrain(catalog, region, { add() {}, remove() {} });
  const controller = new AbortController();
  try {
    const [roads, tiles] = await Promise.all([
      terrain.loadRoads(),
      Promise.all(area.tiles.map(({ level, x, z }) => terrain.fetchTile(level, x, z, controller.signal))),
    ]);
    if (!roads) throw new Error('Seattle road data is not available.');
    const seed = tiles.map(tile => btoa(String.fromCharCode(...new Uint8Array(tile.buffer)))).join(';');
    const definition = JSON.parse(api.SelectSeattleDrive(JSON.stringify(roads), seed));
    driveRoads.set(terrain, roads.roads);
    return { ...definition, terrain };
  } catch (error) {
    controller.abort();
    terrain.dispose();
    throw error;
  }
}

export class WorldDriveScene extends RaceScene {
  constructor(graphics, world, terrain) {
    const empty = { vertices: [] };
    super(graphics, { name: world.name, length: 0, points: [] }, { land: empty, props: empty, road: empty });
    this.world = world;
    this.terrain = terrain;
    this.scenery = new WorldScenery(graphics, terrain);
    this.seattleCamera = new SeattleCamera(terrain, world.bounds);
    this.walkers = new SeattleWalkers(graphics, world, terrain, driveRoads.get(terrain) ?? []);
  }

  update(state, dt, mode, compatibility) {
    const { x, z } = state.player;
    if (state.tick < this.lastTick || (this.lastPosition &&
        Math.hypot(x - this.lastPosition[0], z - this.lastPosition[1]) > 24)) {
      this.cameraYaw = undefined;
      this.previousMode = null;
      this.seattleCamera.reset();
    }
    this.lastTick = state.tick;
    this.lastPosition = [x, z];
    const frame = super.update(state, dt, mode, compatibility);
    return this.scenery.decorate(frame, x, z, {
      far: 25000, view: 'ground', appendGroundActors: frame => {
        this.driveCamera = this.seattleCamera.update(frame.camera, state.player, dt, mode,
          state.phase === 'ready', this.orbit, this.graphics.canvas, this.scenery.buildingVolumes);
        this.walkers.append(frame, state, this.scenery);
      },
    });
  }

  hasDetailedGround({ x, z }) {
    return Boolean(this.terrain.visualTileAt(x, z, 8));
  }

  mapPoint(x, z) {
    const { minX, minZ, maxX, maxZ } = this.world.bounds;
    const scale = 136 / Math.max(maxX - minX, maxZ - minZ);
    return [80 + (x - (minX + maxX) / 2) * scale, 80 - (z - (minZ + maxZ) / 2) * scale];
  }

  mapStreetPath() {
    return this.world.streets.map(([x, z, a, b]) =>
      `M${this.mapPoint(x, z).map(n => n.toFixed(2)).join(' ')}L${this.mapPoint(a, b).map(n => n.toFixed(2)).join(' ')}`).join('');
  }

  mapPlanPath(points) {
    return points.map(({ x, z }, i) => `${i === 0 ? 'M' : 'L'}${this.mapPoint(x, z).map(n => n.toFixed(2)).join(' ')}`).join('');
  }

  diagnostics() { return { ...this.scenery.diagnostics(), camera: this.driveCamera, pedestrians: this.walkers.diagnostics() }; }

  dispose() {
    this.walkers.dispose();
    this.scenery.dispose();
  }
}
