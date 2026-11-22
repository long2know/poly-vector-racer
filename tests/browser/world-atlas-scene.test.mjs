import assert from 'node:assert/strict';
import test from 'node:test';
import { WorldAtlasScene } from '../../src/StuntRacer.Browser/wwwroot/world-atlas-scene.js';
import { projectLandmarks } from '../../src/StuntRacer.Browser/wwwroot/flight-landmark-labels.js';

const half = 19922944, side = 33;
const atlasWorld = {
  name: 'Atlas test world', gearClearance: 2.4, seaHeight: 0, landingSurfaces: [],
  atlas: { destination: { id: 'fixture', name: 'Fixture departure', x: 0, z: 0 } },
};
const aircraft = {
  x: 80, y: 160, z: 240, yaw: 0, pitch: 0, roll: 0, speed: 120, throttle: .43, fuel: .65,
  gearDown: false, forward: { x: 0, y: 0, z: 1 }, up: { x: 0, y: 1, z: 0 },
};
const state = extra => ({ phase: 'racing', flight: { aircraft: { ...aircraft, ...extra } } });

function tile(key, step, x, z, height = () => 12, water = () => 0) {
  return {
    key, step, x, z,
    heights: Int16Array.from({ length: side * side }, (_, i) => height(i % side, Math.floor(i / side))),
    water: Uint8Array.from({ length: side * side }, (_, i) => water(i % side, Math.floor(i / side))),
  };
}

function fixture(world = atlasWorld) {
  const graphics = {
    meshes: new Set(), released: new Set(), uploads: [], meshBytes: 0,
    mesh({ vertices }, compact) {
      const mesh = { vertices, compact, bytes: (compact ? 24 : 40) * vertices.length / 10 };
      this.meshes.add(mesh);
      this.meshBytes += mesh.bytes;
      if (compact) {
        this.uploads.push(mesh);
        this.onUpload?.(mesh);
      }
      return mesh;
    },
    releaseMesh(mesh) {
      assert.ok(this.meshes.delete(mesh), 'no double release or use of an unowned buffer');
      this.meshBytes -= mesh.bytes;
      this.released.add(mesh);
    },
  };
  const terrain = {
    tiles: [], features: [], frames: [], waterQueries: [], readyKeys: [], disposed: 0,
    frame(...args) { this.frames.push(args); return this; },
    ready(key) { this.readyKeys.push(key); },
    sample: () => 9999,
    isWater(x, z) { this.waterQueries.push([x, z]); return x < 0; },
    diagnostics: () => ({ tier: 'base' }),
    dispose() { this.disposed++; },
  };
  return { terrain, graphics, scene: new WorldAtlasScene(graphics, world, terrain) };
}

const compactDraws = frame => frame.draws.filter(draw => draw.mesh.compact);
const terrainDraws = frame => compactDraws(frame).filter(draw => draw.mesh.vertices.length === 32 * 32 * 60);
const overlays = frame => compactDraws(frame).filter(draw => !terrainDraws(frame).includes(draw));
const points = (mesh, start = 0, stop = mesh.vertices.length) => {
  const result = [];
  for (let i = start; i < stop; i += 10) result.push([...mesh.vertices.slice(i, i + 3)]);
  return result;
};
const almost = (a, b, error = 1e-6) => assert.ok(Math.abs(a - b) <= error, `${a} ≉ ${b}`);

function stepUntil(scene, predicate, sceneState = state(), limit = 250, mode = 'chase') {
  for (let n = 0; n < limit; n++) {
    const frame = scene.update(sceneState, 1 / 60, mode);
    if (predicate(frame)) return frame;
  }
  assert.fail('the expected complete mesh/label did not arrive within the bounded work steps');
}

function faceHeight(mesh, x, z, step) {
  const col = Math.min(31, Math.floor(x / step)), row = Math.min(31, Math.floor(z / step));
  const start = (row * 32 + col) * 60 + (x - col * step > z - row * step ? 30 : 0);
  const [[ax, ay, az], [bx, by, bz], [cx, cy, cz]] = points(mesh, start, start + 30);
  const denom = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
  const a = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / denom;
  const b = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / denom;
  return a * ay + b * by + (1 - a - b) * cy;
}

test('atlas draws upward SW–NE terrain triangles with signed heights and distinct sea and inland water', () => {
  const { scene, terrain } = fixture();
  terrain.tiles = [
    tile('land', 32, 0, 0, (x, z) => x === 1 && z === 0 ? -8 : 5 + x * 3 + z * 9),
    tile('water', 32, 2048, 0, (x, z) => x < 4 ? -2 : 42, x => x < 4 ? 1 : 2),
  ];
  const frame = stepUntil(scene, frame => terrainDraws(frame).length === 2);
  const [land, water] = terrainDraws(frame).map(draw => draw.mesh);
  assert.deepEqual(points(land, 0, 60), [
    [0, 5, 0], [0, 14, 32], [32, 17, 32], [0, 5, 0], [32, 17, 32], [32, -8, 0],
  ]);
  for (const offset of [0, 30]) assert.ok(land.vertices[offset + 4] > .75, 'triangles must light from the sky');
  const inland = 8 * 60;
  assert.deepEqual(points(water, 0, 30), [[0, -2, 0], [0, -2, 32], [32, -2, 32]]);
  assert.deepEqual(points(water, inland, inland + 30), [[256, 42, 0], [256, 42, 32], [288, 42, 32]]);
  assert.equal(water.vertices[9], 5);
  assert.equal(water.vertices[inland + 9], 5);
  assert.notDeepEqual(water.vertices.slice(6, 9), water.vertices.slice(inland + 6, inland + 9),
    'a lake is a separate water color without flattening inland elevations');
  assert.ok(!frame.draws.some(draw => draw.mesh === scene.sea || draw.mesh === scene.ground),
    'the generic FlightScene island and sea are not layered on atlas terrain');
  scene.dispose();
});

test('Sacred Valley elevations stay earthy while tropical summits and polar ground look icy', () => {
  const { scene, terrain } = fixture();
  const andes = -1423 * 1024, highArctic = 9961472 - 1024;
  terrain.tiles = [
    tile('valley', 32, 0, andes, x => x < 16 ? 2437 : 3820),
    tile('summit', 32, 1024, andes, () => 5320),
    tile('polar', 32, 0, highArctic, () => 38),
  ];
  const frame = stepUntil(scene, frame => terrainDraws(frame).length === 3, state({ z: andes + 100 }));
  const [valley, summit, polar] = terrainDraws(frame).map(draw => draw.mesh);
  for (const i of [0, 30, 24 * 60, 24 * 60 + 30]) {
    const [r, g, b] = valley.vertices.slice(i + 6, i + 9);
    assert.ok(r < .5 && g > b && g < .5, 'tropical valley facets should not inherit an indiscriminate snow palette');
  }
  for (const ice of [summit, polar]) assert.ok(ice.vertices[6] > .7 && ice.vertices[8] > ice.vertices[6]);
  scene.dispose();
});

test('atlas ribbons really cover the rendered terrain facets, are clipped at water, and reuse the road palette meshes', () => {
  const { scene, terrain, graphics } = fixture();
  terrain.tiles = [tile('saddle', 32, 0, 0, (x, z) => x * 3 + z * 8 + (x % 2 && z % 2 ? 23 : 0),
    (x, z) => x >= 12 && z <= 5 ? 2 : 0)];
  terrain.features = [{ key: 'detail', x: 0, z: 0, span: 1024, rows: [
    ['road', 'motorway', null, 'osm', [[5, 20], [95, 45], [430, 290]]],
    ['rail', 'rail', null, 'osm', [[12, 116], [640, 116]]],
  ] }];
  const frame = stepUntil(scene, frame => overlays(frame).length > 0);
  const [{ mesh: road }] = overlays(frame), [{ mesh: ground }] = terrainDraws(frame);
  assert.ok(road.vertices.length > 17 * 30, 'long lines must split at grid edges and diagonals rather than making one quad per segment');
  const railX = [], highwayX = [];
  for (let i = 0; i < road.vertices.length; i += 30) {
    const highway = road.vertices[i + 9] === 6;
    const lift = highway ? 1.66 : .9;
    const vertices = points(road, i, i + 30);
    const center = vertices[0].map((_, axis) => vertices.reduce((n, p) => n + p[axis] / 3, 0));
    for (const [x, y, z] of [...vertices, center]) {
      almost(y, faceHeight(ground, x, z, 32) + lift, 1e-4);
      (highway ? highwayX : railX).push(x);
    }
    assert.ok(road.vertices[i + 4] > 0, 'street ribbons must face upward');
  }
  assert.ok(Math.max(...highwayX) > 420);
  assert.ok(Math.max(...railX) > 345 && Math.max(...railX) < 400,
    'the railway remains visible on dry facets and does not fake a deck across the lake');
  const uploads = graphics.uploads.length;
  scene.setMapRoads(true);
  const palette = scene.update(state(), 1 / 60);
  assert.equal(palette.camera.mapRoads, true);
  assert.equal(scene.diagnostics().roadStyle, 'map');
  assert.equal(scene.diagnostics().renderedRoadMeshes, 1);
  assert.ok(palette.draws.some(draw => draw.mesh === road));
  assert.equal(graphics.uploads.length, uploads, 'palette changes cannot trigger rematerialization');
  scene.dispose();
});

test('global roads stay visible outside a ready detail patch, without double drawing the chart line inside it', () => {
  const { scene, terrain } = fixture();
  terrain.tiles = [tile('base', 32, 0, 0)];
  terrain.features = [{ key: 'natural-earth', x: 0, z: 0, span: 1024, rows: [
    ['road', 'primary', null, 'natural-earth', [[0, 100], [1024, 100]]],
  ] }];
  const base = stepUntil(scene, frame => overlays(frame).length > 0);
  const [{ mesh: previous }] = overlays(base);
  assert.ok(points(previous).some(([x]) => x > 470 && x < 560));
  terrain.features = [...terrain.features, { key: 'open-detail', x: 256, z: 0, span: 512, rows: [
    ['road', 'primary', null, 'osm', [[0, 180], [512, 180]]],
    ['building', 'commercial', null, 'osm', [[160, 45], [206, 45], [206, 79], [160, 79]]],
    ['landuse', 'park', null, 'osm', [[176, 220], [270, 220], [270, 282], [176, 282]]],
  ] }];
  const layered = stepUntil(scene, frame => overlays(frame).some(draw => draw.mesh !== previous));
  const [{ mesh }] = overlays(layered);
  const all = points(mesh);
  const coarse = all.filter(([, , z]) => z >= 88 && z <= 112);
  assert.ok(coarse.some(([x]) => x < 80) && coarse.some(([x]) => x > 950));
  assert.ok(coarse.every(([x]) => x <= 256 || x >= 768), 'ready detail locally cuts out the base ribbon');
  assert.ok(all.some(([x, , z]) => x === 256 && z === 172) && all.some(([x, , z]) => x === 768 && z === 188));
  assert.ok(all.some(([x, y, z]) => x === 416 && y === 27 && z === 45), 'a supplied building has an elevated roof');
  assert.ok(all.some(([x, y, z]) => x === 432 && Math.abs(y - 12.31) < 1e-6 && z === 220),
    'supplied open landuse drapes rather than replacing the terrain');
  assert.equal(scene.diagnostics().buildings, 1);
  scene.dispose();
});

const footprint = (x, z, size = 32) => [[x, z], [x + size, z], [x + size, z + size], [x, z + size]];

test('OSM L5 height extrudes roofs and walls from the drawn ground while legacy buildings and point labels survive', () => {
  const { scene, terrain } = fixture();
  terrain.tiles = [tile('grade', 32, 0, 0, (x, z) => 20 + 3 * x + 2 * z)];
  const tower = footprint(128, 320), legacy = footprint(256, 320);
  const malformed = [0, 1001, 70.5, '180', null].map((height, i) =>
    ['building', 'commercial', null, 'osm', footprint(384 + 64 * i, 320), height]);
  terrain.features = [
    { key: 'local/5/0/0', level: 5, x: 0, z: 0, span: 8192, rows: [
      ...malformed,
      ['building', 'commercial', 'Grade Tower', 'osm', tower, 240],
      ['building', 'commercial', null, 'osm', legacy],
      ['building', 'commercial', null, 'natural-earth', footprint(704, 320), 900],
      ['road', 'primary', null, 'osm', [[64, 600], [940, 600]], 600],
      ['landmark', 'monument', 'Grade Tower', 'osm', [[144, 336]]],
    ] },
    { key: 'overview/0/0/0', level: 0, x: 0, z: 0, span: 262144, rows: [
      ['building', 'commercial', null, 'osm', footprint(832, 320), 999],
      ['road', 'primary', null, 'natural-earth', [[64, 850], [940, 850]]],
    ] },
  ];
  const frame = stepUntil(scene, frame => scene.diagnostics().renderedRoadMeshes && frame.landmarkAnchors.length);
  const [{ mesh: ground }] = terrainDraws(frame);
  const geometry = overlays(frame).flatMap(({ mesh }) => points(mesh));
  for (const [ring, height] of [[tower, 240], [legacy, 15]]) {
    const base = ring.map(([x, z]) => faceHeight(ground, x, z, 32));
    const top = Math.max(...base) + height;
    const atFootprint = ([x, , z]) => ring.some(([u, v]) => x === u && z === v);
    const faces = [];
    for (let i = 0; i < geometry.length; i += 3) {
      const face = geometry.slice(i, i + 3);
      if (face.every(atFootprint)) faces.push(face);
    }
    assert.equal(faces.filter(face => face.every(([, y]) => y === top)).length, 2, 'A flat roof uses the tagged or legacy height');
    assert.equal(faces.filter(face => face.some(([, y]) => y === top) && face.some(([, y]) => y < top)).length, 8);
    for (const [[x, z], y] of ring.map((point, i) => [point, base[i]])) {
      assert.ok(geometry.some(([a, b, c]) => a === x && b === top && c === z));
      assert.ok(geometry.some(([a, b, c]) => a === x && b === y - .5 && c === z), 'walls meet the slope at their own corners');
    }
  }
  assert.ok(!geometry.some(([x, , z]) => x >= 384 && z >= 320 && z <= 352),
    'invalid or out-of-tier heights do not publish fallback or tall geometry');
  assert.ok(!geometry.some(([, , z]) => z > 590 && z < 610), 'non-building rows cannot take an extra height');
  assert.ok(geometry.some(([, , z]) => z > 842 && z < 858), 'a neighboring legacy row is not discarded');
  assert.deepEqual(frame.landmarkAnchors.map(point => point.name), ['Grade Tower']);
  scene.dispose();
});

test('tall tagged towers win the existing building cap even when their pack is indexed last', () => {
  const { scene, terrain } = fixture();
  terrain.tiles = [tile('dense-skyline', 32, 0, 0)];
  const low = Array.from({ length: 80 }, (_, i) =>
    ['building', 'residential', null, 'osm', footprint(32 + (i % 10) * 48, 96 + Math.floor(i / 10) * 48), 35]);
  const tallest = footprint(800, 500);
  terrain.features = [
    { key: 'a-neighborhood/5/0/0', level: 5, x: 0, z: 0, span: 8192, rows: low },
    { key: 'z-skyline/5/0/0', level: 5, x: 0, z: 0, span: 8192, rows: [
      ['building', 'commercial', null, 'osm', footprint(720, 500), 5],
      ['building', 'commercial', 'Test Tower', 'osm', tallest, 300],
    ] },
  ];
  const published = face => overlays(face).flatMap(({ mesh }) => points(mesh));
  const frame = stepUntil(scene, frame => published(frame).some(([x, , z]) => x === 800 && z === 500));
  const geometry = published(frame);
  assert.ok(geometry.some(([x, y, z]) => x === 800 && y === 312 && z === 500),
    'The late tower retains its mapped height above the 12 m ground');
  assert.ok(geometry.some(([x, y, z]) => x === 32 && y === 47 && z === 96), 'smaller tagged buildings still fill the remaining slots');
  assert.ok(!geometry.some(([x, , z]) => x === 720 && z === 500), 'the smallest tagged footprint cannot starve a taller one');
  assert.equal(scene.diagnostics().buildings, 72);
  scene.dispose();
});

function footprintFaces(frame, ring, originX = 0) {
  const faces = [];
  for (const { mesh, model } of overlays(frame)) for (let i = 0; i < mesh.vertices.length; i += 30) {
    const face = points(mesh, i, i + 30).map(([x, y, z]) => [x + model[12] + originX, y, z + model[14]]);
    if (face.every(([x, , z]) => ring.some(([u, v]) => x === u && z === v))) faces.push(face);
  }
  return faces;
}

test('a half-open seam owner samples neighboring walls and a hidden crest as terrain streams in and out', t => {
  t.mock.method(performance, 'now', () => 0);
  const { scene, terrain } = fixture();
  const east = tile('east', 32, 1024, 0, () => 20);
  const west = tile('west', 32, 0, 0, (x, z) => x === 31 && z === 11 ? 400 : 46);
  const ring = footprint(976, 320, 96); // Its midpoint is exactly on the shared edge.
  terrain.tiles = [east];
  terrain.features = [{ key: 'crest/5/0/0', level: 5, x: 0, z: 0, span: 8192, rows: [
    ['building', 'commercial', 'Crest Tower', 'osm', ring, 443],
  ] }];
  const initial = stepUntil(scene, frame => footprintFaces(frame, ring).length > 0);
  const published = frame => footprintFaces(frame, ring);
  const roofY = faces => Math.max(...faces.flatMap(face => face.map(([, y]) => y)));
  assert.equal(published(initial).length, 10, 'no artificial wall on the tile seam');
  assert.equal(roofY(published(initial)), 463, 'missing neighboring ground falls back to the clamped owner');
  assert.ok(overlays(initial).some(({ mesh, model }) => model[12] === 1024 && points(mesh).some(([x]) => x < 0)),
    'the eastern owner stores the western corners in its own local space');

  terrain.tiles = [west, east];
  const resolved = stepUntil(scene, frame => {
    const faces = published(frame);
    assert.equal(faces.length, 10, 'a partial or duplicate facade must not appear during resampling');
    return roofY(faces) === 843;
  });
  const walls = published(resolved).flat();
  assert.ok(walls.some(([x, y, z]) => x === 976 && y === 45.5 && z === 320), 'west wall touches its actual terrain');
  assert.ok(walls.some(([x, y, z]) => x === 1072 && y === 19.5 && z === 320), 'east wall keeps the owner height');
  assert.ok(walls.every(([x, y, z]) => x !== 992 || y !== 400 || z !== 352), 'the crest is not a fabricated polygon vertex');
  const crossing = scene.update(state({ x: 1024.25 }), 1 / 60);
  assert.deepEqual(footprintFaces(crossing, ring, 2048), published(resolved), 'crossing the render-origin boundary moves no footprint');

  terrain.tiles = [east];
  stepUntil(scene, frame => {
    const faces = published(frame);
    assert.equal(faces.length, 10);
    return roofY(faces) === 463;
  });
  scene.dispose();
});

test('a complete tower stays visible once while its parent and child terrain owners hand off', t => {
  t.mock.method(performance, 'now', () => 0);
  const { scene, terrain } = fixture();
  const parent = tile('parent', 64, 0, 0, () => 28);
  const children = [
    tile('sw', 32, 0, 0, () => 41), tile('se', 32, 1024, 0, () => 51),
    tile('nw', 32, 0, 1024), tile('ne', 32, 1024, 1024),
  ];
  const ring = footprint(988, 520, 60);
  terrain.tiles = [parent];
  terrain.features = [{ key: 'nyc/5/0/0', level: 5, x: 0, z: 0, span: 8192, rows: [
    ['building', 'commercial', 'Tile Edge Tower', 'osm', ring, 443],
  ] }];
  const first = stepUntil(scene, frame => footprintFaces(frame, ring).length === 10);
  assert.ok(footprintFaces(first, ring).flat().some(([x, y]) => x === 1048 && y === 471));
  for (const [tiles, expected] of [[children, 494], [[parent], 471]]) {
    terrain.tiles = tiles;
    stepUntil(scene, frame => {
      const faces = footprintFaces(frame, ring);
      assert.equal(faces.length, 10, 'keep exactly one complete owner while the replacement layer builds');
      assert.ok(scene.diagnostics().renderWork.uploadBytes <= 160 * 1024);
      return faces.flat().some(([x, y]) => x === 1048 && y === expected) && terrainDraws(frame).length === tiles.length;
    });
  }
  scene.dispose();
});

test('L5 lines take priority over matching L0 OSM and Natural Earth, without replacing unbacked classes or point labels', () => {
  const { scene, terrain } = fixture();
  terrain.tiles = [tile('detail-ground', 256, 0, 0), tile('overview-ground', 256, 8192, 0)];
  const line = (kind, style, source, z, end = 16384) => [kind, style, null, source, [[0, z], [end, z]]];
  terrain.features = [
    { key: 'base/0/0/0', level: 0, x: 0, z: 0, span: 262144, rows: [
      line('road', 'primary', 'natural-earth', 100), line('road', 'motorway', 'natural-earth', 260),
      line('rail', 'rail', 'natural-earth', 330), line('rail', 'tram', 'natural-earth', 410),
      ['place', 'town', 'Legacy Town', 'natural-earth', [[201, 401]]],
    ] },
    { key: 'world-osm/0/0/0', level: 0, x: 0, z: 0, span: 262144, rows: [
      line('road', 'primary', 'osm', 180), line('rail', 'rail', 'osm', 370),
      ['landmark', 'monument', 'Overview Monument', 'osm', [[201, 502]]],
    ] },
    { key: 'pilot-osm/5/0/0', level: 5, x: 0, z: 0, span: 8192, rows: [
      line('road', 'secondary', 'osm', 220, 8192), line('road', 'local', 'osm', 460, 8192),
    ] },
  ];
  const rowDrawn = (meshes, z) => meshes.some(mesh => points(mesh).some(([, , n]) => Math.abs(n - z) < 12));
  const at = (frame, x) => overlays(frame).filter(draw => draw.model[12] === x).map(draw => draw.mesh);
  const frame = stepUntil(scene, frame => rowDrawn(at(frame, 0), 460) && rowDrawn(at(frame, 8192), 180));
  const fine = at(frame, 0), overview = at(frame, 8192);
  assert.ok(rowDrawn(fine, 220) && !rowDrawn(fine, 180));
  assert.ok(!rowDrawn(fine, 100) && !rowDrawn(overview, 100));
  for (const meshes of [fine, overview]) {
    assert.ok(!rowDrawn(meshes, 330) && rowDrawn(meshes, 370), 'mapped OSM rail replaces the equivalent Natural Earth rail');
    assert.ok(rowDrawn(meshes, 260) && rowDrawn(meshes, 410), 'express roads and local transit retain their own source coverage');
  }
  assert.deepEqual(frame.landmarkAnchors.map(point => point.name), ['Overview Monument', 'Legacy Town']);
  scene.dispose();
});

test('wrap uses sub-metre aircraft geometry and continuous chase smoothing, with one seam and shared label/map space', () => {
  const { scene, terrain } = fixture();
  const z = 8388608;
  terrain.tiles = [
    tile('east', 32, half - 1024, z, () => 12), tile('west', 32, -half, z, () => 16),
  ];
  terrain.features = [{ key: 'date-line', x: -half, z, span: 1024, rows: [
    ['landmark', 'monument', 'Crossing Monument', 'osm', [[25, 512]]],
    ['airport', 'airport', 'Point Field', 'osm', [[68, 514]]],
  ] }];
  const east = { x: half - .375, z: z + 512.25, y: 20, yaw: Math.PI / 2, forward: { x: 1, y: 0, z: 0 } };
  const west = { ...east, x: -half + .375 };
  const cockpit = stepUntil(scene, frame => frame.landmarkAnchors.length === 2 && overlays(frame).length === 1,
    state(east), 30, 'cockpit');
  almost(cockpit.camera.eye[0], .515, 1e-5);
  almost(cockpit.camera.eye[2], 512.25, 1e-5);
  assert.equal(cockpit.draws.find(draw => draw.mesh === scene.cockpit).model[12], -.375);
  assert.equal(cockpit.draws.filter(draw => draw.mesh === scene.instrumentBar).length, 2);
  assert.equal(cockpit.draws.some(draw => draw.mesh === scene.instrumentNeedle), false, 'an airport point is not a licensed runway');
  assert.deepEqual(terrain.frames[0].slice(0, 2), [east.x, east.z], 'only rendering uses the rebased aircraft');
  assert.deepEqual(terrainDraws(cockpit).map(draw => [draw.model[12], draw.model[14]]), [[-1024, 0], [0, 0]]);
  const seams = overlays(cockpit);
  assert.equal(seams.length, 1, 'shared date-line edge must not be drawn by both tiles');
  assert.ok(points(seams[0].mesh).every(([x, y]) => x + seams[0].model[12] === 0 && (y === 12 || y === 16)));
  assert.deepEqual(cockpit.landmarkAnchors, [
    { name: 'Crossing Monument', position: [25, 20, 512] }, { name: 'Point Field · airport', position: [68, 20, 514] },
  ], 'labels use the active ground and share the rebased render frame');
  const pixels = projectLandmarks(cockpit.landmarkAnchors, cockpit.camera, 16 / 9, 800, 450);
  assert.ok(pixels.some(point => point.name === 'Crossing Monument' && Math.abs(point.x - 400) < 5));

  const before = scene.update(state(east), 1 / 60);
  const dt = 1 / 60, after = scene.update(state(west), dt);
  almost(after.camera.eye[0] - before.camera.eye[0], .75 * (1 - Math.exp(-dt * 8)));
  almost(after.camera.target[0] - before.camera.target[0], .75 * (1 - Math.exp(-dt * 8)));
  assert.equal(after.draws.find(draw => draw.mesh === scene.jet).model[12], .375);
  assert.equal(after.camera.fogDistance, before.camera.fogDistance);
  assert.equal(after.camera.far, before.camera.far);
  assert.ok(after.camera.logarithmicDepth);
  assert.deepEqual(after.landmarkAnchors, cockpit.landmarkAnchors);
  almost(scene.mapPoint(half - 140, z)[0], 79.5);
  almost(scene.mapPoint(-half + 140, z)[0], 80.5);
  assert.ok(scene.mapWaterPath(1).startsWith('M80 0h80v4H80Z'));
  assert.ok(terrain.waterQueries.some(([x]) => x > 0));
  assert.ok(terrain.waterQueries.some(([x]) => x < 0));
  assert.ok(terrain.waterQueries.every(([x]) => x >= -half && x < half));

  scene.setFlightView('forward');
  const nearOriginChange = { ...west, x: -half + 1023.75, z: z + 1023.75 };
  const beforeRebase = scene.update(state(nearOriginChange), dt);
  const afterRebase = scene.update(state({ ...nearOriginChange, x: nearOriginChange.x + .5, z: nearOriginChange.z + .5 }), dt);
  for (const axis of [0, 2]) {
    almost(afterRebase.camera.eye[axis] + 2048 - beforeRebase.camera.eye[axis], .5 * (1 - Math.exp(-dt * 8)));
    almost(afterRebase.camera.target[axis] + 2048 - beforeRebase.camera.target[axis], .5 * (1 - Math.exp(-dt * 8)));
  }
  scene.dispose();
});

test('bounded uploads acknowledge drawable children without hiding a parent or drawing overlapping ground', t => {
  t.mock.method(performance, 'now', () => 0);
  const { scene, terrain, graphics } = fixture();
  const parent = tile('parent', 64, 0, 0, () => 100);
  const children = [[0, 0], [1024, 0], [0, 1024], [1024, 1024]].map(([x, z], i) =>
    tile(`child${i}`, 32, x, z, () => 101 + i));
  terrain.tiles = [parent];
  const initial = stepUntil(scene, frame => terrainDraws(frame).length === 1);
  const [{ mesh: parentMesh }] = terrainDraws(initial);
  terrain.tiles = [parent, ...children.map(child => ({ ...child, pending: true }))];
  const before = graphics.uploads.length;
  for (let i = 0; i < 4; i++) {
    const warming = scene.update(state(), 1 / 60);
    assert.deepEqual(terrainDraws(warming).map(draw => draw.mesh), [parentMesh]);
    assert.equal(graphics.uploads.length - before, i + 1, 'only one full ground buffer fits the byte budget');
    assert.equal(scene.diagnostics().renderWork.uploadBytes, 32 * 32 * 6 * 24);
    assert.deepEqual([...new Set(terrain.readyKeys)], children.slice(0, i + 1).map(child => child.key));
  }
  terrain.tiles = children;
  const replacement = scene.update(state(), 1 / 60);
  const actual = terrainDraws(replacement);
  assert.deepEqual(actual.map(draw => [draw.model[12], draw.mesh.vertices[1], draw.model[14]]),
    [[0, 101, 0], [1024, 102, 0], [0, 103, 1024], [1024, 104, 1024]]);
  assert.ok(actual.every(draw => draw.mesh !== parentMesh), 'an active child does not overlap its old parent');
  assert.ok(actual.every(draw => graphics.uploads.indexOf(draw.mesh) < before + 4), 'activation reuses the warmed handles');
  terrain.tiles = [];
  for (let i = 0; i < 50; i++) scene.update(state(), 1 / 60);
  assert.ok(graphics.released.has(parentMesh) && actual.every(draw => graphics.released.has(draw.mesh)));
  scene.dispose();
  scene.dispose();
  assert.equal(terrain.disposed, 1);
  assert.equal(graphics.meshBytes, 0);
});

test('the renderer stops preparing and submitting new work when time is spent inside a frame', t => {
  let clock = 0, rowReads = 0;
  t.mock.method(performance, 'now', () => clock);
  const { scene, terrain, graphics } = fixture();
  terrain.tiles = [tile('near', 32, 0, 0), tile('neighbor', 32, 1024, 0, () => 22)];
  const rows = Array.from({ length: 20 }, () => null);
  const row = ['road', 'primary', null, 'osm', [[12, 116], [640, 116]]];
  for (let i = 0; i < rows.length; i++) Object.defineProperty(rows, i, { get() {
    clock += .3;
    rowReads++;
    return row;
  } });
  terrain.features = [{ key: 'budget-detail', x: 0, z: 0, span: 1024, rows }];
  scene.update(state(), 1 / 60);
  assert.ok(rowReads > 0 && rowReads < 6, 'slow feature indexing is interrupted rather than traversing the packet');
  graphics.onUpload = mesh => { if (mesh.vertices.length === 32 * 32 * 60) clock += 5; };
  const before = rowReads, afterDelay = scene.update(state(), 1 / 60);
  assert.equal(terrainDraws(afterDelay).length, 2);
  assert.equal(overlays(afterDelay).length, 0, 'a blocking ground upload defers the smaller seam and feature work');
  assert.equal(rowReads, before);
  delete graphics.onUpload;
  const next = scene.update(state(), 1 / 60);
  assert.ok(overlays(next).length === 1 && points(overlays(next)[0].mesh).every(([x]) => x === 1024),
    'deferred shared-edge geometry is sent on a later bounded frame');
  assert.ok(rowReads > before);
  scene.dispose();
});

test('dense feature work is deferred, upload-sliced, and canceled cleanly after a partial upload', t => {
  t.mock.method(performance, 'now', () => 0);
  const { scene, terrain, graphics } = fixture();
  const path = Array.from({ length: 29 }, (_, i) => [20 + i * 34, i % 2 ? 220 : 65]);
  const rows = Array.from({ length: 1024 }, () => ['road', 'local', null, 'osm', path]);
  rows.push(['road', 'motorway', null, 'osm', [[5, 420], [1000, 420]]]);
  const heavy = { key: 'dense-detail', x: 0, z: 0, span: 1024, rows };
  terrain.tiles = [tile('start', 32, 0, 0)];
  terrain.features = [heavy];
  assert.equal(overlays(scene.update(state(), 1 / 60)).length, 0, 'the first dense packet cannot monopolize the cockpit update');
  const built = stepUntil(scene, frame => {
    assert.ok(scene.diagnostics().renderWork.uploadBytes <= 160 * 1024);
    return overlays(frame).length > 0;
  });
  const submitted = overlays(built).map(draw => draw.mesh), [mesh] = submitted;
  assert.ok(submitted.length > 1 && submitted.reduce((n, part) => n + part.vertices.length, 0) <= 4096 * 30);
  assert.ok(submitted.every(part => part.vertices.length <= 1536 * 30));
  assert.ok(points(mesh, 0, 30).every(([, , z]) => z >= 408 && z <= 432) && mesh.vertices[9] === 6,
    'the only motorway survives the many local roads even though the input puts it last');
  const firstNewUpload = graphics.uploads.length;
  terrain.features = [{ ...heavy, key: 'unfinished-detail' }];
  stepUntil(scene, frame => {
    assert.equal(overlays(frame).length, 0, 'partial feature batches cannot appear in the flight view');
    return graphics.uploads.length > firstNewUpload;
  });
  const staged = graphics.uploads[firstNewUpload];
  assert.ok(submitted.every(part => graphics.released.has(part)), 'removed packs do not keep their visible buffers alive');
  terrain.features = [];
  const uploadCount = graphics.uploads.length;
  for (let n = 0; n < 80; n++) assert.equal(overlays(scene.update(state(), 1 / 60)).length, 0);
  assert.equal(graphics.uploads.length, uploadCount, 'aborted chunk CPU work must not publish a stale mesh');
  assert.ok(graphics.released.has(staged), 'a canceled, never-drawn upload is also freed');
  scene.dispose();
  assert.equal(graphics.meshBytes, 0);
});
