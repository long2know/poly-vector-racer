import assert from 'node:assert/strict';
import test from 'node:test';
import { triangulate } from '../../src/StuntRacer.Browser/wwwroot/engine/triangulate.js';

test('concave source coastlines retain their notch under either winding', () => {
  const outline = [[0, 0], [3, 0], [3, 3], [2, 3], [2, 1], [1, 1], [1, 3], [0, 3]];
  for (const shape of [outline, [...outline].reverse()]) {
    const points = shape.map(([x, z]) => [x, 0, z]);
    const triangles = triangulate(points);
    assert.equal(triangles.length, points.length - 2);
    let area = 0;
    for (const triangle of triangles) {
      const [a, b, c] = triangle.map(i => points[i]);
      area += Math.abs((b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0])) / 2;
      const x = (a[0] + b[0] + c[0]) / 3, z = (a[2] + b[2] + c[2]) / 3;
      assert.equal(x > 1 && x < 2 && z > 1, false, 'no triangle may fill the original coastline notch');
    }
    assert.equal(area, 7);
  }
});

test('source vertical faces and redundant edge vertices stay valid while bad input is explicit', () => {
  const points = [[0, 0, 2], [1, 0, 2], [2, 0, 2], [2, 2, 2], [0, 2, 2]];
  const area = triangulate(points).reduce((sum, triangle) => {
    const [a, b, c] = triangle.map(i => points[i]);
    return sum + Math.abs((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) / 2;
  }, 0);
  assert.equal(area, 4);
  assert.deepEqual(triangulate([[0, 0, 0], [1, 0, 0], [2, 0, 0]]), []);
  assert.throws(() => triangulate([[0, 0, 0], [1, 0, 0]]), /finite 3D vertices/);
  assert.throws(() => triangulate([[0, 0, 0], [1, NaN, 0], [0, 1, 0]]), /finite 3D vertices/);
});
