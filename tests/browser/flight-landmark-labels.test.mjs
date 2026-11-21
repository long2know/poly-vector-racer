import assert from 'node:assert/strict';
import test from 'node:test';
import { projectLandmarks } from '../../src/StuntRacer.Browser/wwwroot/flight-landmark-labels.js';

test('landmark projection uses a rebased camera and clips backwards, near and offscreen anchors', () => {
  const camera = { eye: [120, 9, -50], target: [120, 9, -40], up: [0, 1, 0],
    fov: Math.PI / 2, far: 100 };
  const anchors = [
    { name: 'above-right', position: [140, 19, -10] },
    { name: 'center', position: [120, 9, -35] },
    { name: 'backwards', position: [120, 9, -55] },
    { name: 'too near', position: [120, 9, -49.95] },
    { name: 'offscreen', position: [190, 9, -35] },
    { name: 'past the far plane', position: [120, 9, 60] }
  ];
  assert.deepEqual(projectLandmarks(anchors, camera, 2, 800, 400), [
    { name: 'center', x: 400, y: 200, depth: 15 },
    { name: 'above-right', x: 500, y: 150, depth: 40 }
  ]);
  const rear = { ...camera, target: [120, 9, -60] };
  assert.deepEqual(projectLandmarks(anchors, rear, 2, 800, 400).map(p => p.name), ['backwards']);
});
