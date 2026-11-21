import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import test from 'node:test';
import { Graphics } from '../../src/StuntRacer.Browser/wwwroot/engine/graphics.js';

// Exercise receipt policy without simulating shader compilation or the constructor.
function policy(fences = []) {
  const statuses = new Map(), waited = [], deleted = [], deletedQueries = [];
  const gl = {
    ALREADY_SIGNALED: 0x911a, TIMEOUT_EXPIRED: 0x911b, CONDITION_SATISFIED: 0x911c, WAIT_FAILED: 0x911d,
    QUERY_RESULT_AVAILABLE: 0x8867, QUERY_RESULT: 0x8866,
    isContextLost: () => false,
    clientWaitSync(fence, flags, timeout) {
      assert.equal(flags, 0);
      assert.equal(timeout, 0, 'GPU receipt must never block');
      waited.push(fence);
      return statuses.get(fence) ?? gl.TIMEOUT_EXPIRED;
    },
    deleteSync: fence => deleted.push(fence),
    deleteQuery: query => deletedQueries.push(query),
  };
  const graphics = Object.assign(Object.create(Graphics.prototype), {
    gl, pending: [...fences], completedFrames: 0, submittedFrames: fences.length,
    skippedFrames: 0, gpuMilliseconds: null, query: null,
  });
  return { graphics, gl, statuses, waited, deleted, deletedQueries };
}

test('one unsignaled submission leaves admission for the latest second frame', () => {
  const { graphics, waited, deleted } = policy(['first']);
  assert.equal(graphics.receipt(), true);
  assert.equal(graphics.pendingFrames, 1);
  assert.equal(graphics.completedFrames, 0);
  assert.deepEqual(waited, ['first']);
  assert.deepEqual(deleted, []);
  assert.throws(() => { graphics.pendingFrames = 99; }, TypeError, 'pendingFrames is read-only');
});

test('two pending submissions reject renders without claiming completion or storing stale input', () => {
  const { graphics, waited } = policy(['first', 'second']);
  const neverInspect = new Proxy({}, { get() { assert.fail('A rejected render must not inspect its state'); } });
  assert.equal(graphics.render(neverInspect, neverInspect, 1), false);
  assert.equal(graphics.render(neverInspect, neverInspect, 2), false);
  assert.equal(graphics.pendingFrames, 2);
  assert.equal(graphics.skippedFrames, 2);
  assert.equal(graphics.submittedFrames, 2);
  assert.equal(graphics.completedFrames, 0);
  assert.deepEqual(waited, ['first', 'first'], 'Only the ordered head may be polled while it is unsignaled');
});

test('both actual success statuses retire fences in order, exactly once', () => {
  const { graphics, gl, statuses, waited, deleted } = policy(['first', 'second']);
  statuses.set('first', gl.ALREADY_SIGNALED);
  assert.equal(graphics.receipt(), true);
  assert.equal(graphics.completedFrames, 1);
  assert.deepEqual(graphics.pending, ['second']);
  assert.deepEqual(deleted, ['first']);
  statuses.set('second', gl.CONDITION_SATISFIED);
  assert.equal(graphics.receipt(), true);
  assert.equal(graphics.receipt(), true);
  assert.equal(graphics.completedFrames, 2);
  assert.equal(graphics.pendingFrames, 0);
  assert.deepEqual(deleted, ['first', 'second']);
  assert.deepEqual(waited, ['first', 'second', 'second']);
});

test('a single receipt drains both signaled fences rather than unnecessarily skipping a display frame', () => {
  const { graphics, gl, statuses, deleted } = policy(['first', 'second']);
  statuses.set('first', gl.ALREADY_SIGNALED);
  statuses.set('second', gl.ALREADY_SIGNALED);
  assert.equal(graphics.receipt(), true);
  assert.equal(graphics.completedFrames, 2);
  assert.deepEqual(deleted, ['first', 'second']);
  assert.equal(graphics.pendingFrames, 0);
});

test('WAIT_FAILED stays visible and does not fabricate a completed frame', () => {
  const { graphics, gl, statuses, deleted } = policy(['first']);
  statuses.set('first', gl.WAIT_FAILED);
  assert.throws(() => graphics.receipt(), /GPU stopped completing/);
  assert.equal(graphics.completedFrames, 0);
  assert.equal(graphics.pendingFrames, 1);
  assert.deepEqual(deleted, []);
});

test('context loss is visible both before a poll and when WAIT_FAILED reports it', () => {
  const first = policy(['first']);
  first.gl.isContextLost = () => true;
  assert.throws(() => first.graphics.receipt(), /context was lost/);
  assert.deepEqual(first.waited, []);
  const second = policy(['second']);
  second.statuses.set('second', second.gl.WAIT_FAILED);
  let contextChecks = 0;
  second.gl.isContextLost = () => ++contextChecks > 1;
  assert.throws(() => second.graphics.receipt(), /context was lost/);
  assert.equal(second.graphics.completedFrames, 0);
});

test('unknown fence statuses are not mistaken for actual completion', () => {
  const { graphics, statuses, deleted } = policy(['first']);
  statuses.set('first', 0xdead);
  assert.throws(() => graphics.receipt(), /invalid rendering completion status/);
  assert.equal(graphics.completedFrames, 0);
  assert.deepEqual(deleted, []);
});

test('a pending head does not prevent nonblocking retrieval of the one finished timer query', () => {
  const { graphics, gl, deletedQueries } = policy(['first', 'second']);
  graphics.query = 'query';
  graphics.timer = { GPU_DISJOINT_EXT: 0x8fbb };
  gl.getQueryParameter = (query, parameter) => {
    assert.equal(query, 'query');
    return parameter === gl.QUERY_RESULT_AVAILABLE ? true : 2500000;
  };
  gl.getParameter = parameter => {
    assert.equal(parameter, graphics.timer.GPU_DISJOINT_EXT);
    return false;
  };
  assert.equal(graphics.receipt(), false);
  assert.equal(graphics.gpuMilliseconds, 2.5);
  assert.equal(graphics.completedFrames, 0);
  assert.equal(graphics.query, null);
  assert.deepEqual(deletedQueries, ['query']);
});

test('an unavailable timer result is not read and a disjoint result is not published', () => {
  const { graphics, gl, deletedQueries } = policy(['first']);
  graphics.query = 'query';
  graphics.timer = { GPU_DISJOINT_EXT: 0x8fbb };
  gl.getQueryParameter = (_, parameter) => {
    assert.equal(parameter, gl.QUERY_RESULT_AVAILABLE);
    return false;
  };
  assert.equal(graphics.receipt(), true);
  assert.equal(graphics.query, 'query');
  gl.getQueryParameter = (_, parameter) => {
    assert.equal(parameter, gl.QUERY_RESULT_AVAILABLE, 'Do not read a disjoint result');
    return true;
  };
  gl.getParameter = () => true;
  graphics.receipt();
  assert.equal(graphics.gpuMilliseconds, null);
  assert.deepEqual(deletedQueries, ['query']);
});

test('dispose deletes every pending fence and the query without counting them as completed', () => {
  const { graphics, gl, deleted, deletedQueries } = policy(['first', 'second']);
  graphics.query = 'query';
  graphics.meshes = new Set();
  graphics.scene = graphics.flightScene = graphics.depth = graphics.sky = { handle: 'program' };
  gl.deleteProgram = gl.deleteTexture = gl.deleteFramebuffer = gl.deleteVertexArray = () => {};
  graphics.dispose();
  graphics.dispose();
  assert.deepEqual(deleted, ['first', 'second']);
  assert.deepEqual(deletedQueries, ['query']);
  assert.equal(graphics.pendingFrames, 0);
  assert.equal(graphics.completedFrames, 0);
});

test('quality validation preserves the last valid selection and its shadow allocation', () => {
  const { graphics } = policy();
  const sizes = [];
  graphics.createShadow = size => { sizes.push(size); graphics.shadowBuffer = {}; };
  graphics.setQuality('high');
  graphics.setQuality('high');
  assert.equal(graphics.quality, 'high');
  graphics.setQuality('balanced');
  graphics.setQuality('balanced');
  assert.equal(graphics.quality, 'balanced');
  for (const invalid of ['HIGH', '', null, undefined, 'automatic']) {
    assert.throws(() => graphics.setQuality(invalid), /Unknown graphics quality/);
    assert.equal(graphics.quality, 'balanced');
  }
  assert.deepEqual(sizes, [2048, 1024]);
});

test('compact scenery preserves positions and class IDs with 40% fewer uploaded bytes', () => {
  const uploads = [], attributes = [], deleted = [];
  const gl = {
    ARRAY_BUFFER: 34962, STATIC_DRAW: 35044, FLOAT: 5126, SHORT: 5122, UNSIGNED_BYTE: 5121,
    createVertexArray: () => ({}), createBuffer: () => ({}), bindVertexArray() {}, bindBuffer() {},
    bufferData: (_target, data) => uploads.push(data),
    enableVertexAttribArray() {}, vertexAttribPointer: (...args) => attributes.push(args),
    deleteBuffer: buffer => deleted.push(buffer), deleteVertexArray() {},
  };
  const graphics = Object.assign(Object.create(Graphics.prototype), { gl, meshes: new Set(), meshBytes: 0 });
  const scenery = { vertices: [1.25, 641.52, -111.32, .5, -.8, -.33166, .065, .235, .29, 11] };
  const compact = graphics.mesh(scenery, true), regular = graphics.mesh(scenery);
  const data = new DataView(uploads[0]);
  assert.deepEqual([0, 4, 8].map(i => data.getFloat32(i, true)), [...uploads[1].slice(0, 3)]);
  assert.ok([12, 14, 16].every((offset, i) => Math.abs(data.getInt16(offset, true) / 32767 - scenery.vertices[i + 3]) < .00004));
  assert.ok([18, 19, 20].every((offset, i) => Math.abs(data.getUint8(offset) / 255 - scenery.vertices[i + 6]) < .002));
  assert.equal(data.getUint8(21), 11);
  assert.deepEqual(attributes.slice(0, 4), [
    [0, 3, gl.FLOAT, false, 24, 0], [1, 3, gl.SHORT, true, 24, 12],
    [2, 3, gl.UNSIGNED_BYTE, true, 24, 18], [3, 1, gl.UNSIGNED_BYTE, false, 24, 21],
  ]);
  assert.equal(graphics.meshBytes, regular.bytes + compact.bytes);
  assert.equal(compact.bytes, regular.bytes * .6);
  graphics.releaseMesh(compact);
  graphics.releaseMesh(compact);
  assert.equal(graphics.meshBytes, regular.bytes);
  assert.deepEqual(deleted, [compact.buffer]);
  graphics.releaseMesh(regular);
  assert.equal(graphics.meshBytes, 0);
});

// Independent analytic expectation for the fixture's real fragment output. Its
// 3x3 depth texture has four lit corners and a shadowed center: High must average
// four of nine samples, while Balanced must use only that center.
function expectedPixel(visibility, width, height) {
  const x = (Math.floor(width / 2) + 0.5) * 2 / width - 1;
  const y = (Math.floor(height / 2) + 0.5) * 2 / height - 1;
  const distance = Math.hypot(x, y, 4), view = [-x / distance, -y / distance, 4 / distance];
  const spec = ((1 + view[2]) / Math.hypot(view[0], view[1], 1 + view[2])) ** 22;
  const fog = 1 - Math.exp(-((distance / 920) ** 1.6));
  return [0, 1, 2].map(channel => {
    let lit = 0.5 * ([0.345, 0.405, 0.435][channel] + [1, 0.88, 0.71][channel] * visibility * 1.02)
      + [1, 0.89, 0.76][channel] * spec * 0.09 * visibility;
    lit = lit * (1 - fog) + [0.65, 0.74, 0.76][channel] * fog;
    lit = lit * (2.51 * lit + 0.03) / (lit * (2.43 * lit + 0.59) + 0.14);
    return Math.round(Math.max(0, Math.min(1, lit)) ** 0.9 * 255);
  });
}

async function browserFixture(moduleUrl) {
  const { Graphics, Geometry } = await import(moduleUrl);
  const canvas = document.createElement('canvas');
  canvas.style.cssText = 'position:fixed;left:0;top:0;width:64px;height:64px';
  document.body.append(canvas);
  const graphics = new Graphics(canvas), gl = graphics.gl;
  const camera = { eye: [0, 0, -4], target: [0, 0, 0], up: [0, 1, 0], fov: Math.PI / 3 };
  const result = { renderer: graphics.device, devicePixelRatio, qualities: [] };
  let fixtureTexture;
  async function drain() {
    const deadline = performance.now() + 10000;
    while (graphics.pendingFrames && performance.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 20));
      graphics.receipt();
    }
    if (graphics.pendingFrames) throw new Error('Real GPU fences did not complete');
  }
  try {
    result.accepted = [graphics.render([], camera, 0), graphics.render([], camera, 1),
      graphics.render([], { ...camera, eye: [99, 0, -4] }, 2)];
    result.beforeReceipt = { pending: graphics.pendingFrames, completed: graphics.completedFrames,
      submitted: graphics.submittedFrames, skipped: graphics.skippedFrames };
    await drain();
    result.afterReceipt = { pending: graphics.pendingFrames, completed: graphics.completedFrames };
    const latestCamera = { ...camera, eye: [0, 0, -6] };
    graphics.render([], latestCamera, 3);
    result.latestEye = [...gl.getUniform(graphics.scene.handle, graphics.scene.uniforms.eye)];
    await drain();
    const geometry = new Geometry();
    geometry.quad([-1, -1, 0], [1, -1, 0], [1, 1, 0], [-1, 1, 0], [0.5, 0.5, 0.5]);
    const mesh = graphics.mesh(geometry);
    fixtureTexture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, fixtureTexture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH_COMPONENT24, 3, 3, 0, gl.DEPTH_COMPONENT,
      gl.UNSIGNED_INT, new Uint32Array([0xffffffff, 0, 0xffffffff, 0, 0, 0, 0xffffffff, 0, 0xffffffff]));
    for (const parameter of [gl.TEXTURE_MIN_FILTER, gl.TEXTURE_MAG_FILTER]) {
      gl.texParameteri(gl.TEXTURE_2D, parameter, gl.NEAREST);
    }
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const identity = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
    const fixedShadowPosition = new Float32Array(16);
    fixedShadowPosition[15] = 1;
    for (const quality of ['high', 'balanced', 'high']) {
      graphics.setQuality(quality);
      graphics.render([], camera, 0);
      await drain();
      const u = graphics.scene.uniforms;
      const sample = { quality, width: canvas.width, height: canvas.height, shadowSize: graphics.shadowSize,
        softShadows: gl.getUniform(graphics.scene.handle, u.softShadows),
        shadowTexel: gl.getUniform(graphics.scene.handle, u.shadowTexel) };
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.disable(gl.DEPTH_TEST);
      gl.disable(gl.BLEND);
      gl.clearColor(0, 0, 0, 1);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.useProgram(graphics.scene.handle);
      gl.uniformMatrix4fv(u.model, false, identity);
      gl.uniformMatrix4fv(u.viewProjection, false, identity);
      gl.uniformMatrix4fv(u.lightProjection, false, fixedShadowPosition);
      gl.uniform3fv(u.eye, [0, 0, 4]);
      gl.uniform3fv(u.sun, [0, 0, 1]);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, fixtureTexture);
      gl.uniform1f(u.shadowTexel, 1 / 3);
      // softShadows is intentionally NOT set here: test the actual render path.
      gl.bindVertexArray(mesh.vao);
      gl.drawArrays(gl.TRIANGLES, 0, mesh.count);
      const pixel = new Uint8Array(4);
      gl.readPixels(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
      sample.pixel = [...pixel];
      sample.error = gl.getError();
      result.qualities.push(sample);
    }
    result.pendingFramesReadOnly = !Object.getOwnPropertyDescriptor(Graphics.prototype, 'pendingFrames').set;
    graphics.setQuality('balanced');
    const ghostGeometry = new Geometry();
    ghostGeometry.quad([-1, -1, -0.5], [1, -1, -0.5], [1, 1, -0.5], [-1, 1, -0.5], [0.65, 0.25, 1]);
    const ghost = graphics.mesh(ghostGeometry);
    const read = async (draws, view = camera) => {
      if (!graphics.render(draws, view, 0)) throw new Error('The drained GPU queue rejected a fixture frame');
      const pixel = new Uint8Array(4);
      gl.readPixels(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
      await drain();
      return [...pixel];
    };
    const behind = identity.slice();
    behind[14] = 2;
    result.transparency = {
      base: await read([{ mesh, shadow: false }]),
      ghost: await read([{ mesh: ghost, shadow: false }]),
      blended: await read([{ mesh: ghost, opacity: 0.4, shadow: false }, { mesh, shadow: false }]),
      occluded: await read([{ mesh: ghost, model: behind, opacity: 0.4, shadow: false }, { mesh, shadow: false }]),
      depthWrite: gl.getParameter(gl.DEPTH_WRITEMASK), blending: gl.isEnabled(gl.BLEND),
      opacity: gl.getUniform(graphics.scene.handle, graphics.scene.uniforms.opacity),
      error: gl.getError(),
    };
    const roadView = { ...camera, logarithmicDepth: true, far: 25000 }, roadSlice = material => {
      const geometry = new Geometry();
      geometry.quad([-1, -.28, 0], [1, -.28, 0], [1, .28, 0], [-1, .28, 0], [.35, .37, .33], material);
      return [{ mesh: graphics.mesh(geometry, true), shadow: false }];
    };
    const road = [6, 7, 8, 9].map(roadSlice);
    result.roadPalette = [];
    for (const draws of road) result.roadPalette.push({
      terrain: await read(draws, roadView),
      map: await read(draws, { ...roadView, mapRoads: true }),
    });
    const street = new Geometry();
    street.quad([-4, 0, -4], [-4, 0, 4], [4, 0, 4], [4, 0, -4], [.104, .111, .123], 12);
    const asphalt = graphics.mesh(street, true);
    // Sample several 6.25 cm aggregate cells at a sub-centimeter pixel footprint.
    canvas.style.width = canvas.style.height = '192px';
    const sampleSize = 64;
    const asphaltPixels = async (mapRoads, origin = 0) => {
      const model = identity.slice();
      model[12] = origin;
      const ground = { ...roadView, eye: [origin, 1.2, 0], target: [origin, 0, 0], up: [0, 0, 1], mapRoads };
      if (!graphics.render([{ mesh: asphalt, model, shadow: false }], ground, 0))
        throw new Error('The drained GPU queue rejected an asphalt frame');
      const pixels = new Uint8Array(sampleSize * sampleSize * 4);
      gl.readPixels((canvas.width - sampleSize) / 2, (canvas.height - sampleSize) / 2,
        sampleSize, sampleSize, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
      await drain();
      return [...pixels].filter((_, i) => i % 4 === 0);
    };
    result.asphalt = {
      sampleSize,
      pixels: await asphaltPixels(false), map: await asphaltPixels(true), rebased: await asphaltPixels(false, 2048), error: gl.getError(),
    };
    const { FlightScene } = await import(new URL('../flight-scene.js', moduleUrl));
    canvas.style.width = '640px';
    canvas.style.height = '360px';
    const world = {
      name: 'Takeoff depth regression', seaHeight: 0, gearClearance: 2.4,
      islands: [{ centerX: 0, centerZ: 0, width: 4000, length: 6000, height: 8, heading: 0 }],
      landingSurfaces: [{ name: 'Runway', centerX: 0, centerZ: 0, width: 80, length: 3000, height: 8, heading: 0 }],
    };
    const original = {
      ...world, islands: [],
      sourceWorld: {
        surfaces: [
          { kind: 'water', vertices: [[-10000, -10000], [-10000, 10000], [10000, 10000], [10000, -10000]]
            .map(([x, z]) => ({ x, y: 0.008, z })) },
          { kind: 'runway', vertices: [[-40, -1500], [-40, 1500], [40, 1500], [40, -1500]]
            .map(([x, z]) => ({ x, y: 0.025, z })) },
        ], objects: [], mapOutline: [{ x: -10000, z: -10000 }, { x: 10000, z: 10000 }],
      },
    };
    result.flightGround = [];
    for (const [kind, definition] of [['modern', world], ['original', original]]) {
      let groundGeometry;
      const flight = new FlightScene({ mesh(geometry) {
        const mesh = graphics.mesh(geometry);
        if (geometry.vertices.some((value, i) => i % 10 === 9 && value === 1)) groundGeometry = geometry;
        return mesh;
      } }, definition);
      const withoutLand = new Geometry();
      for (let i = 0; i < groundGeometry.vertices.length; i += 30)
        if (groundGeometry.vertices[i + 9] !== 3)
          withoutLand.vertices.push(...groundGeometry.vertices.slice(i, i + 30));
      const isolatedRunway = graphics.mesh(withoutLand);
      for (const quality of ['high', 'balanced']) {
        graphics.setQuality(quality);
        for (const mode of ['chase', 'cockpit']) for (const altitude of [20, 120, 350]) {
          const aircraft = { x: 0.013, y: altitude, z: -1300.011, yaw: 0, pitch: 0, roll: 0,
            forward: { x: 0, y: 0, z: 1 }, up: { x: 0, y: 1, z: 0 },
            speed: 90, gearDown: false, afterburner: false, throttle: 0.8, fuel: 1 };
          const frame = flight.update({ phase: 'racing', flight: { aircraft } }, 1 / 8, mode);
          const draws = frame.draws.filter(draw => [flight.sea, flight.ground].includes(draw.mesh))
            .map(draw => ({ ...draw, shadow: false }));
          const capture = quality === 'high' && mode === 'chase' && altitude === 120;
          let image;
          const pixels = async list => {
            if (!graphics.render(list, frame.camera, 0)) throw new Error('Drained GPU rejected a ground frame');
            const buffer = new Uint8Array(canvas.width * canvas.height * 4);
            gl.readPixels(0, 0, canvas.width, canvas.height, gl.RGBA, gl.UNSIGNED_BYTE, buffer);
            if (capture) image = canvas.toDataURL();
            await drain();
            return buffer;
          };
          const actual = await pixels(draws);
          const actualImage = image;
          const handle = gl.getParameter(gl.CURRENT_PROGRAM);
          const matrix = gl.getUniform(handle, gl.getUniformLocation(handle, 'viewProjection'));
          const reference = await pixels(kind === 'original' ? draws.filter(draw => draw.mesh !== flight.sea)
            : draws.map(draw => draw.mesh === flight.ground ? { ...draw, mesh: isolatedRunway } : draw));
          const referenceImage = image;
          const sky = await pixels([]);
          const y = kind === 'original' ? 0.008 : 8.045, halfWidth = kind === 'original' ? 2000 : 20;
          const project = ([x, z]) => {
            const w = matrix[3] * x + matrix[7] * y + matrix[11] * z + matrix[15];
            return [(matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12]) / w * canvas.width / 2 + canvas.width / 2,
              (matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13]) / w * canvas.height / 2 + canvas.height / 2];
          };
          const polygon = [[-halfWidth, -1100], [-halfWidth, kind === 'original' ? 2500 : 300],
            [halfWidth, kind === 'original' ? 2500 : 300], [halfWidth, -1100]].map(project);
          const area = polygon.reduce((sum, p, i) => {
            const q = polygon[(i + 1) % polygon.length];
            return sum + p[0] * q[1] - p[1] * q[0];
          }, 0);
          let tested = 0, visible = 0, covered = 0;
          for (let row = 0; row < canvas.height; row++) for (let column = 0; column < canvas.width; column++) {
            if (!polygon.every((p, i) => {
              const q = polygon[(i + 1) % polygon.length], dx = q[0] - p[0], dy = q[1] - p[1];
              return Math.sign(area) * (dx * (row + 0.5 - p[1]) - dy * (column + 0.5 - p[0])) > Math.hypot(dx, dy) * 2;
            })) continue;
            tested++;
            const offset = (row * canvas.width + column) * 4;
            if ([0, 1, 2].some(channel => Math.abs(reference[offset + channel] - sky[offset + channel]) > 2)) visible++;
            if ([0, 1, 2].some(channel => Math.abs(actual[offset + channel] - reference[offset + channel]) > 2)) covered++;
          }
          const sample = { kind, quality, mode, altitude, tested, visible, covered, error: gl.getError() };
          if (actualImage) {
            sample.actualImage = actualImage;
            sample.referenceImage = referenceImage;
          }
          result.flightGround.push(sample);
        }
      }
    }
    return result;
  } finally {
    gl.deleteTexture(fixtureTexture);
    graphics.dispose();
    canvas.remove();
  }
}

test('real WebGL2: queue, shadows, transparency, flight depth and map roads',
  { skip: process.env.GRAPHICS_BROWSER !== '1', timeout: 60000 }, async () => {
    const base = new URL(process.env.STUNT_URL ?? 'http://127.0.0.1:5188/stunt-car-racer/');
    assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname));
    const moduleUrl = new URL('engine/graphics.js', base).href;
    const flightUrl = new URL('flight-scene.js', base).href;
    const source = await readFile(new URL('../../src/StuntRacer.Browser/wwwroot/engine/graphics.js', import.meta.url), 'utf8');
    const flightSource = await readFile(new URL('../../src/StuntRacer.Browser/wwwroot/flight-scene.js', import.meta.url), 'utf8');
    const root = resolve('artifacts/performance');
    await mkdir(root, { recursive: true });
    const output = await mkdtemp(resolve(root, 'graphics-test-'));
    const profile = await mkdtemp(resolve(output, 'chrome-'));
    const chrome = spawn(process.env.CHROME_BIN ?? '/usr/bin/google-chrome', [
      '--headless=new', '--enable-automation', '--no-first-run', '--no-default-browser-check',
      ...(process.env.STUNT_ENABLE_GPU === '1' ? ['--enable-gpu'] : []),
      '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank',
    ], { stdio: ['ignore', 'ignore', 'pipe'] });
    let chromeErrors = '', socket, nextId = 0;
    const pending = new Map(), interceptionErrors = [];
    chrome.stderr.on('data', data => { chromeErrors = (chromeErrors + data.toString()).slice(-12000); });
    function call(method, params = {}) {
      return new Promise((resolve, reject) => {
        const id = ++nextId, timeout = setTimeout(() => {
          pending.delete(id);
          reject(new Error(`CDP timeout: ${method}`));
        }, 15000);
        pending.set(id, { resolve, reject, timeout });
        socket.send(JSON.stringify({ id, method, params }));
      });
    }
    try {
      let port;
      for (let attempt = 0; attempt < 200; attempt++) {
        assert.equal(chrome.exitCode, null, chromeErrors);
        try { port = (await readFile(resolve(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; }
        catch (error) { if (error.code !== 'ENOENT') throw error; }
        if (port) break;
        await sleep(80);
      }
      assert.ok(port, chromeErrors);
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10000) })).json();
      socket = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
      await once(socket, 'open', { signal: AbortSignal.timeout(10000) });
      socket.addEventListener('message', event => {
        const message = JSON.parse(event.data), request = pending.get(message.id);
        if (request) {
          pending.delete(message.id);
          clearTimeout(request.timeout);
          if (message.error) request.reject(new Error(message.error.message));
          else request.resolve(message.result);
        } else if (message.method === 'Fetch.requestPaused') {
          const { requestId, request: intercepted } = message.params;
          const isModule = intercepted.url === moduleUrl || intercepted.url === flightUrl;
          void call('Fetch.fulfillRequest', { requestId, responseCode: 200,
            responseHeaders: [{ name: 'Content-Type', value: isModule ? 'text/javascript' : 'text/html' }],
            body: Buffer.from(intercepted.url === flightUrl ? flightSource : isModule ? source
              : '<!doctype html><html><body></body></html>').toString('base64'),
          }).catch(error => interceptionErrors.push(error.message));
        }
      });
      await call('Page.enable');
      await call('Runtime.enable');
      await call('Fetch.enable', { patterns: [{ urlPattern: base.href }, { urlPattern: moduleUrl }, { urlPattern: flightUrl }] });
      await call('Emulation.setDeviceMetricsOverride', { width: 800, height: 600, deviceScaleFactor: 2, mobile: false });
      await call('Page.navigate', { url: base.href });
      await sleep(200);
      const response = await call('Runtime.evaluate', {
        expression: `(${browserFixture.toString()})(${JSON.stringify(moduleUrl)})`, awaitPromise: true, returnByValue: true,
      });
      assert.equal(response.exceptionDetails, undefined, response.result?.description);
      assert.deepEqual(interceptionErrors, []);
      const result = response.result.value;
      for (const sample of result.flightGround) {
        for (const name of ['actualImage', 'referenceImage']) if (sample[name]) {
          await writeFile(resolve(output, `${sample.kind}-${name}.png`), Buffer.from(sample[name].split(',')[1], 'base64'));
          delete sample[name];
        }
      }
      await writeFile(resolve(output, 'report.json'), JSON.stringify(result, null, 2));
      console.log(`Real graphics evidence: ${resolve(output, 'report.json')} (${result.renderer})`);
      if (process.env.STUNT_ENABLE_GPU === '1') {
        assert.match(result.renderer, /NVIDIA/i);
        assert.doesNotMatch(result.renderer, /SwiftShader|llvmpipe|software/i);
      }
      assert.deepEqual(result.accepted, [true, true, false]);
      assert.deepEqual(result.beforeReceipt, { pending: 2, completed: 0, submitted: 2, skipped: 1 });
      assert.deepEqual(result.afterReceipt, { pending: 0, completed: 2 });
      assert.deepEqual(result.latestEye, [0, 0, -6]);
      assert.equal(result.pendingFramesReadOnly, true);
      assert.equal(result.devicePixelRatio, 2);
      for (const sample of result.qualities) {
        const high = sample.quality === 'high';
        assert.equal(Boolean(sample.softShadows), high);
        assert.equal(sample.shadowSize, high ? 2048 : 1024);
        assert.equal(sample.shadowTexel, 1 / sample.shadowSize);
        assert.equal(sample.width, high ? 112 : 64);
        assert.equal(sample.height, sample.width);
        assert.equal(sample.error, 0, 'The actual compiled shader must draw without WebGL errors');
        const expected = expectedPixel(high ? 0.34 + 0.66 * 4 / 9 : 0.34, sample.width, sample.height);
        for (let channel = 0; channel < 3; channel++) {
          assert.ok(Math.abs(sample.pixel[channel] - expected[channel]) <= 2,
            `${sample.quality} pixel ${sample.pixel} must match its actual shadow sampling: ${expected}`);
        }
        for (const sample of result.flightGround) {
          assert.equal(sample.error, 0, 'flight depth rendering must not emit WebGL errors');
          assert.ok(sample.tested > 25, `The ${sample.kind}/${sample.mode} ground interior must actually be visible`);
          assert.ok(sample.visible > sample.tested * 0.99, 'a missing terrain draw must not masquerade as stable ground');
          assert.equal(sample.covered, 0,
            `${sample.kind}/${sample.quality}/${sample.mode} at ${sample.altitude}m: underlying ground must not flicker through the visible surface`);
        }
        assert.equal(sample.pixel[3], 255);
      }
      assert.ok(result.qualities[0].pixel[0] > result.qualities[1].pixel[0] + 10);
      assert.deepEqual(result.qualities[0].pixel, result.qualities[2].pixel, 'Returning to High must restore its original output');
      const alpha = result.transparency;
      assert.equal(alpha.error, 0);
      assert.equal(alpha.depthWrite, true);
      assert.equal(alpha.blending, false);
      assert.equal(alpha.opacity, 1);
      assert.deepEqual(alpha.occluded, alpha.base, 'solid geometry must still occlude the non-physical ghost');
      assert.ok(alpha.ghost.some((value, i) => i < 3 && Math.abs(value - alpha.base[i]) > 10));
      for (let channel = 0; channel < 3; channel++) {
        const expected = alpha.ghost[channel] * 0.4 + alpha.base[channel] * 0.6;
        assert.ok(Math.abs(alpha.blended[channel] - expected) <= 3,
          'ghosts must blend over opaque geometry even when submitted first');
      }
      const [freeway, arterial, local, bridge] = result.roadPalette;
      assert.ok(freeway.map[2] > freeway.map[0] + 35, `readable blue freeway: ${freeway.map}`);
      assert.ok(arterial.map[0] > arterial.map[2] + 35, `readable amber arterial: ${arterial.map}`);
      assert.ok(local.map.slice(0, 3).every((channel, i) => channel > local.terrain[i] + 30),
        `pale local street: ${local.map} vs. ${local.terrain}`);
      assert.deepEqual(bridge.map, freeway.map, 'the same class has the same color on a bridge');
      const street = result.asphalt;
      assert.equal(street.error, 0);
      assert.ok(Math.max(...street.pixels) < 100 && Math.min(...street.pixels) > 10, 'the asphalt renders dark without becoming black');
      assert.ok(street.pixels.some((p, i) => i % street.sampleSize > 0 && Math.abs(p - street.pixels[i - 1]) >= 5),
        'asphalt renders visible high-frequency aggregate even with uniform vertex color');
      assert.deepEqual(street.pixels, street.map, 'the map palette cannot recolor the ground asphalt material');
      assert.ok(street.pixels.filter((p, i) => Math.abs(p - street.rebased[i]) <= 2).length > street.pixels.length * .98,
        'asphalt aggregate stays fixed when the camera/world origin shifts by 2048 m');
    } finally {
      for (const request of pending.values()) clearTimeout(request.timeout);
      socket?.close();
      if (chrome.exitCode === null && chrome.signalCode === null) {
        const exited = once(chrome, 'exit');
        chrome.kill('SIGTERM');
        await Promise.race([exited, sleep(5000, undefined, { ref: false })]);
        if (chrome.exitCode === null && chrome.signalCode === null) {
          chrome.kill('SIGKILL');
          await exited;
        }
      }
      await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
      if (chromeErrors) await writeFile(resolve(output, 'chrome.log'), chromeErrors);
    }
  });
