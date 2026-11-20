import assert from 'node:assert/strict';
import test from 'node:test';
import { decodeTile, makeElevationMesh, makeElevationSeam, terrainTileSpan, WorldTerrain } from '../../src/StuntRacer.Browser/wwwroot/world-terrain.js';

function fixture(level, x, z, height = (i, j) => 7 + i + 2 * j, wet = 0) {
  const buffer = new ArrayBuffer(16 + 33 * 33 * 3);
  const view = new DataView(buffer);
  view.setUint32(0, 0x57465431, false);
  view.setInt32(4, level, true);
  view.setInt32(8, x, true);
  view.setInt32(12, z, true);
  for (let j = 0; j < 33; j++) for (let i = 0; i < 33; i++) {
    const n = j * 33 + i;
    view.setInt16(16 + 2 * n, height(i, j), true);
    view.setUint8(16 + 33 * 33 * 2 + n, wet);
  }
  return decodeTile(buffer, level, x, z);
}

test('streamed terrain keeps real signed heights and uses the same triangle diagonal as C# contacts', () => {
  const tile = fixture(8, -1, 0, (i, j) => j === 0 && i === 1 ? -5 : 100 + i * 4 + j * 10);
  const mesh = makeElevationMesh(tile);
  assert.deepEqual([0, 10, 20, 30, 40, 50].map(i => mesh.vertices.slice(i, i + 3)),
    [[0, 100, 0], [0, 110, 32], [32, 114, 32], [0, 100, 0], [32, 114, 32], [32, -5, 0]]);
  assert.equal(mesh.vertices.length, 32 * 32 * 6 * 10);
  assert.throws(() => decodeTile(tile.buffer, 8, 0, 0), /coordinates/);
  assert.throws(() => fixture(8, -1, 0, () => 1, 3), /terrain samples/);
  const lake = makeElevationMesh(fixture(8, 0, 0, () => 5, 2));
  assert.equal(lake.vertices[1], 5);
  assert.equal(lake.vertices[9], 5, 'water receives its own shader rather than grass');
});

test('a parent stays visible until four drawable children cover its full area', () => {
  const terrain = new WorldTerrain({}, { coverage: [[0, 0, 1, 1], [0, 0, 2, 2]] },
    { add() {}, remove() {} });
  const graphics = { mesh: () => ({}), releaseMesh() {} };
  terrain.attach(graphics);
  for (const [level, x, z] of [[0, 0, 0], [1, 0, 0], [1, 0, 1], [1, 1, 0], [1, 1, 1]]) {
    const tile = fixture(level, x, z);
    if (level === 0 || x === 0) tile.mesh = {};
    terrain.loaded.set(`${level}/${x}/${z}`, tile);
  }
  const first = terrain.draw(200, 200, 180000, false, 1);
  assert.deepEqual(first.map(draw => draw.offset), [[0, 0, 0]]);
  assert.equal(terrain.visible[0].level, 0);
  const refined = terrain.draw(200, 200, 180000, false, 200);
  const span = terrainTileSpan(1);
  assert.deepEqual(refined.map(draw => draw.offset), [[0, 0, 0], [span, 0, 0], [0, 0, span], [span, 0, span]]);
  assert.ok(terrain.visible.every(tile => tile.level === 1));
});

test('a level-of-detail join ends at the actual adjacent mesh, even when it is higher', () => {
  const coarse = fixture(7, -1, 0, (_i, j) => 125 + 2 * j);
  const fine = fixture(8, 0, 0, () => 110);
  const seam = makeElevationSeam(fine, 0, coarse);
  assert.deepEqual([0, 10, 20, 50].map(i => seam.vertices.slice(i, i + 3)),
    [[0, 110, 0], [0, 125, 0], [0, 126, 32], [0, 110, 32]]);
  const flat = fixture(8, 0, 0, (_i, j) => 125 + j);
  assert.equal(makeElevationSeam(flat, 0, coarse).vertices.length, 0, 'matching seams do not add overlapping faces');
});
