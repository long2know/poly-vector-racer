import assert from 'node:assert/strict';
import test from 'node:test';
import { GameplayCapture, toggleFullscreen } from '../../src/StuntRacer.Browser/wwwroot/engine/display.js';
import { lookAt, multiply, orthographic, perspective, pose, transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

function project(matrix, point) {
  const clip = transform(matrix, point);
  const w = matrix[3] * point[0] + matrix[7] * point[1] + matrix[11] * point[2] + matrix[15];
  return clip.map(value => value / w);
}

test('positive vehicle steering projects to the right, and negative to the left, around the circuit', () => {
  for (const yaw of [0, Math.PI / 2, Math.PI, -Math.PI / 2]) {
    const body = pose([15, 20, -40], yaw);
    const eye = transform(body, [0, 2, -8]), target = transform(body, [0, 2, 10]);
    const viewProjection = multiply(perspective(Math.PI / 3, 16 / 9, 0.1, 200), lookAt(eye, target));
    const center = project(viewProjection, transform(body, [0, 1, 15]));
    for (const direction of [-1, 1]) {
      const steered = multiply(body, pose([0, 0, 0], direction * 0.2));
      const ahead = project(viewProjection, transform(steered, [0, 1, 15]));
      assert.ok(direction * (ahead[0] - center[0]) > 0.05,
        `${direction < 0 ? 'left' : 'right'} steering must move the projected heading ${direction < 0 ? 'left' : 'right'} at yaw ${yaw}`);
    }
  }
});

test('perspective and shadow projection retain WebGL near/far depth with positive-Z forward', () => {
  const view = lookAt([0, 0, 0], [0, 0, 1]);
  for (const projection of [perspective(Math.PI / 3, 1, 1, 100), orthographic(20, 1, 100)]) {
    const matrix = multiply(projection, view);
    assert.ok(Math.abs(project(matrix, [0, 0, 1])[2] + 1) < 1e-5);
    assert.ok(Math.abs(project(matrix, [0, 0, 100])[2] - 1) < 1e-5);
    assert.ok(project(matrix, [1, 0, 10])[0] > 0, 'vehicle right must stay screen-right');
    assert.ok(project(matrix, [0, 1, 10])[1] > 0, 'up must stay screen-up');
  }
});

test('render pose uses the same positive-right yaw and bank and nose-up pitch as C#', () => {
  const yaw = transform(pose([0, 0, 0], Math.PI / 2), [0, 0, 1]);
  assert.ok(Math.abs(yaw[0] - 1) < 1e-6);
  assert.ok(Math.abs(yaw[2]) < 1e-6);
  const bank = transform(pose([0, 10, 0], 0, 0, 0.2), [1, 0, 0]);
  assert.ok(bank[1] > 10, 'positive bank raises the right edge');
  const pitch = transform(pose([0, 10, 0], 0, 0.2), [0, 0, 1]);
  assert.ok(pitch[1] > 10, 'positive pitch raises the nose');
});

test('fullscreen calls stay within the user gesture and denied requests surface errors', async () => {
  let requested = false;
  const pending = toggleFullscreen({ requestFullscreen() { requested = true; return Promise.resolve(); } },
    { fullscreenEnabled: true, fullscreenElement: null });
  assert.equal(requested, true, 'request must happen before the first await');
  assert.equal(await pending, true);
  await assert.rejects(() => toggleFullscreen({}, { fullscreenEnabled: false }), /unavailable/);
  await assert.rejects(() => toggleFullscreen({ requestFullscreen: () => Promise.reject(new Error('Denied')) },
    { fullscreenEnabled: true }), /Denied/);
});

test('fullscreen exit uses the native exit path rather than another request', async () => {
  const viewport = {};
  let exits = 0;
  assert.equal(await toggleFullscreen(viewport, {
    fullscreenElement: viewport, exitFullscreen: async () => { exits++; },
  }), false);
  assert.equal(exits, 1);
});

test('a failed media encoder releases both tracks and cannot report a successful download', () => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'MediaRecorder');
  let stopped = 0, success = null;
  const errors = [];
  const tracks = [{ stop() { stopped++; } }];
  const stream = { addTrack(track) { tracks.push(track); }, getTracks: () => tracks };
  class Encoder {
    static isTypeSupported() { return true; }
    start() { this.state = 'recording'; }
    stop() { this.state = 'inactive'; this.onstop(); }
  }
  try {
    Object.defineProperty(globalThis, 'MediaRecorder', { value: Encoder, configurable: true });
    const capture = new GameplayCapture();
    capture.start({ captureStream: () => stream }, { captureTrack: () => ({ stop() { stopped++; } }) },
      downloaded => { success = downloaded; }, error => errors.push(error.message));
    capture.chunks.push(new Blob(['incomplete media']));
    capture.recorder.onerror({ error: new Error('Encoder failed') });
    assert.deepEqual(errors, ['Encoder failed']);
    assert.equal(success, false);
    assert.equal(stopped, 2);
    assert.equal(capture.stream, null);
    assert.equal(capture.recorder, null);
    Encoder.prototype.start = () => { throw new Error('Could not start encoder'); };
    const another = new GameplayCapture();
    const nextTracks = [{ stop() { stopped++; } }];
    assert.throws(() => another.start({
      captureStream: () => ({ addTrack: track => nextTracks.push(track), getTracks: () => nextTracks }),
    }, { captureTrack: () => ({ stop() { stopped++; } }) }, () => {}, () => {}), /Could not start encoder/);
    assert.equal(stopped, 4);
    assert.equal(another.recorder, null);
    assert.equal(another.stream, null);
  } finally {
    if (descriptor) Object.defineProperty(globalThis, 'MediaRecorder', descriptor);
    else delete globalThis.MediaRecorder;
  }
});
