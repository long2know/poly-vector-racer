// Focused renderer profiler. Run against an already-running local demo:
//   node tests/browser/performance.mjs
// Compare the current local renderer without publishing or changing the server:
//   PERF_LOCAL_GRAPHICS=1 PERF_SCENARIOS=high-one-pending,high,balanced node tests/browser/performance.mjs
// Opt in to normal GPU autodetection, and require an actual NVIDIA renderer:
//   STUNT_ENABLE_GPU=1 PERF_LOCAL_GRAPHICS=1 PERF_SCENARIOS=served-high,high,balanced node tests/browser/performance.mjs
// PERF_DPR=2 also exercises High's existing 1.75 internal DPR cap.
// Other diagnostic scenarios: no-pcf,no-shadow-pass,single-tap,flat-sky.
// No gameplay state, production files, existing tests, or server lifecycle is changed.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const base = new URL(process.env.STUNT_URL ?? 'http://127.0.0.1:5188/stunt-car-racer/');
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname), 'Profile a loopback demo only');
const localGraphicsPath = resolve('src/StuntRacer.Browser/wwwroot/engine/graphics.js');
const overrideLocalGraphics = process.env.PERF_LOCAL_GRAPHICS === '1';
const enableGpu = process.env.STUNT_ENABLE_GPU === '1';
const deviceScaleFactor = Number(process.env.PERF_DPR ?? 1);
assert.ok(Number.isFinite(deviceScaleFactor) && deviceScaleFactor >= 1 && deviceScaleFactor <= 2,
  'Use a bounded device scale factor from one to two');
const graphicsUrl = new URL('engine/graphics.js', base).href;
const definitions = {
  'served-high': { quality: 'high', source: 'served', queueLimit: 1 },
  'high-one-pending': { quality: 'high', queueLimit: 1 },
  high: { quality: 'high' },
  balanced: { quality: 'balanced' },
  'no-pcf': { quality: 'high', sampling: 'off' },
  'no-shadow-pass': { quality: 'high', sampling: 'off', skipShadow: true },
  'single-tap': { quality: 'high', sampling: 'single' },
  'flat-sky': { quality: 'high', flatSky: true },
};
const scenarios = (process.env.PERF_SCENARIOS ?? 'high,balanced').split(',');
assert.ok(scenarios.length >= 1 && scenarios.length <= 5, 'Use one to five bounded experiments');
for (const name of scenarios) assert.ok(definitions[name], `Unknown scenario: ${name}`);
assert.ok(overrideLocalGraphics || !scenarios.some(name => definitions[name].queueLimit && definitions[name].source !== 'served'),
  'The queue-cap comparison requires PERF_LOCAL_GRAPHICS=1 to identify the exact tested source');
const warmupMs = Number(process.env.PERF_WARMUP_MS ?? 3000);
const measureMs = Number(process.env.PERF_MEASURE_MS ?? 8000);
assert.ok(Number.isFinite(warmupMs) && warmupMs >= 2000 && warmupMs <= 15000);
assert.ok(Number.isFinite(measureMs) && measureMs >= 6000 && measureMs <= 20000);
const root = resolve('artifacts/performance');
await mkdir(root, { recursive: true });
const output = await mkdtemp(resolve(root, 'run-'));
const profile = await mkdtemp(resolve(output, 'chrome-'));
const chrome = spawn(process.env.CHROME_BIN ?? '/usr/bin/google-chrome', [
  '--headless=new', '--enable-automation', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--window-size=1440,1000',
  ...(enableGpu ? ['--enable-gpu'] : []),
  '--force-device-scale-factor=1', 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
let chromeErrors = '';
chrome.stderr.on('data', data => { chromeErrors = (chromeErrors + data.toString()).slice(-12000); });
chrome.on('error', error => { chromeErrors += `\n${error.message}`; });

class Cdp {
  nextId = 0;
  pending = new Map();
  listeners = new Map();
  constructor(socket) {
    this.socket = socket;
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const request = this.pending.get(message.id);
        if (!request) return;
        this.pending.delete(message.id);
        clearTimeout(request.timeout);
        if (message.error) request.reject(new Error(message.error.message));
        else request.resolve(message.result);
      } else {
        for (const listener of this.listeners.get(message.method) ?? []) listener(message.params);
      }
    });
    socket.addEventListener('close', () => {
      for (const request of this.pending.values()) {
        clearTimeout(request.timeout);
        request.reject(new Error('Chrome debugging connection closed'));
      }
      this.pending.clear();
    });
  }
  call(method, params = {}) {
    return new Promise((resolve, reject) => {
      const id = ++this.nextId;
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`CDP timeout: ${method}`));
      }, 20000);
      this.pending.set(id, { resolve, reject, timeout });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
  on(method, listener) {
    if (!this.listeners.has(method)) this.listeners.set(method, []);
    this.listeners.get(method).push(listener);
  }
}

// Installed before application modules. Every scenario has the same lightweight
// wrappers. There are no per-frame getParameter/readPixels/finish/blocking waits.
function installExperiment(spec) {
  const state = globalThis.__rendererProfile = {
    sceneShaders: 0, shaderPatches: 0, skyPatches: 0, shadowSize: null,
    pendingFrames: 0, maxPendingFrames: 0,
    counts: { mainDraws: 0, mainVertices: 0, shadowDraws: 0, shadowVertices: 0,
      skippedShadowDraws: 0, skippedShadowClears: 0, fences: 0, retiredFences: 0, timeoutPolls: 0,
      animationCallbacks: 0 },
  };
  const requestFrame = globalThis.requestAnimationFrame;
  globalThis.requestAnimationFrame = callback => requestFrame.call(globalThis, timestamp => {
    state.counts.animationCallbacks++;
    callback(timestamp);
  });
  const proto = WebGL2RenderingContext.prototype;
  const depthBuffers = new Set(), liveFences = new Set();
  let framebuffer = null;
  const original = Object.fromEntries(
    ['shaderSource', 'bindFramebuffer', 'framebufferTexture2D', 'viewport', 'drawArrays', 'clear',
      'fenceSync', 'deleteSync', 'clientWaitSync']
      .map(name => [name, proto[name]]));
  proto.shaderSource = function(shader, source) {
    if (source.includes('float shadow(vec3 n)')) {
      state.sceneShaders++;
      if (spec.sampling) {
        const pattern = /float shadow\(vec3 n\) \{[\s\S]*?\n\}/;
        if (!pattern.test(source)) throw new Error('Shadow shader changed; experiment refused');
        const replacement = spec.sampling === 'off'
          ? 'float shadow(vec3 n) { return 1.0; }'
          : `float shadow(vec3 n) {
  vec3 p=shadowPosition.xyz/shadowPosition.w*.5+.5;
  if(p.x<0.0||p.y<0.0||p.x>1.0||p.y>1.0||p.z>1.0) return 1.0;
  float bias=max(.0004,.0015*(1.0-max(dot(n,sun),0.0)));
  return p.z-bias<=texture(shadowMap,p.xy).r?1.0:.34;
}`;
        source = source.replace(pattern, replacement);
        state.shaderPatches++;
      }
    }
    if (spec.flatSky && source.includes('float cloud=noise(p)')) {
      const pattern = /float cloud=noise\(p\)[^;]+;/;
      if (!pattern.test(source)) throw new Error('Sky shader changed; experiment refused');
      source = source.replace(pattern, 'float cloud=0.0;');
      state.skyPatches++;
    }
    return original.shaderSource.call(this, shader, source);
  };
  proto.bindFramebuffer = function(target, buffer) {
    if (target === this.FRAMEBUFFER || target === this.DRAW_FRAMEBUFFER) framebuffer = buffer;
    return original.bindFramebuffer.call(this, target, buffer);
  };
  proto.framebufferTexture2D = function(target, attachment, ...args) {
    if (attachment === this.DEPTH_ATTACHMENT && framebuffer) depthBuffers.add(framebuffer);
    return original.framebufferTexture2D.call(this, target, attachment, ...args);
  };
  proto.viewport = function(x, y, width, height) {
    if (depthBuffers.has(framebuffer)) state.shadowSize = [width, height];
    return original.viewport.call(this, x, y, width, height);
  };
  proto.drawArrays = function(mode, first, count) {
    const shadow = depthBuffers.has(framebuffer);
    if (shadow && spec.skipShadow) { state.counts.skippedShadowDraws++; return; }
    state.counts[shadow ? 'shadowDraws' : 'mainDraws']++;
    state.counts[shadow ? 'shadowVertices' : 'mainVertices'] += count;
    return original.drawArrays.call(this, mode, first, count);
  };
  proto.clear = function(mask) {
    if (spec.skipShadow && depthBuffers.has(framebuffer)) { state.counts.skippedShadowClears++; return; }
    return original.clear.call(this, mask);
  };
  proto.fenceSync = function(...args) {
    const fence = original.fenceSync.apply(this, args);
    state.counts.fences++;
    if (fence) liveFences.add(fence);
    state.pendingFrames = liveFences.size;
    state.maxPendingFrames = Math.max(state.maxPendingFrames, state.pendingFrames);
    return fence;
  };
  proto.deleteSync = function(fence) {
    if (liveFences.delete(fence)) state.counts.retiredFences++;
    state.pendingFrames = liveFences.size;
    return original.deleteSync.call(this, fence);
  };
  proto.clientWaitSync = function(...args) {
    const status = original.clientWaitSync.apply(this, args);
    if (status === this.TIMEOUT_EXPIRED) state.counts.timeoutPolls++;
    return status;
  };
}

let cdp, socket, scriptId;
let activeGraphicsSource = null, moduleOverrides = 0;
const interceptionErrors = [];
const sourceHashes = new Map();
const startedAt = new Date().toISOString();
const deadline = Date.now() + 240000;
const report = {
  startedAt, base: base.href, warmupMs, measureMs, viewport: { width: 1440, height: 900, dpr: deviceScaleFactor },
  gpuRequested: enableGpu, display: process.env.DISPLAY ?? null,
  graphicsSource: overrideLocalGraphics ? { delivery: 'CDP Fetch.fulfillRequest', path: localGraphicsPath }
    : { delivery: 'existing local demo server', url: graphicsUrl },
  method: 'Delta of application GPU-fence completedFrames / page monotonic elapsed time, not rAF callbacks.',
  limitations: [
    'Headless Chrome renderer is recorded, not assumed to represent the user physical GPU.',
    'Main-thread milliseconds is the application EWMA sampled once/second, not isolated GPU time.',
    'GPU fence receipt includes browser scheduling/polling delay; FPS is not inverse GPU shader execution time.',
    'Shadow removal variants deliberately change lighting and are diagnostic only; main scene and sky still render.',
    'Shadow-pass variant skips depth-FBO draws/clears, but retains JS uniform and framebuffer setup.',
    'Ready preview is stationary; camera/sky continue their real application animation. No gameplay state is injected.',
    'high-one-pending changes only the admission cap in the current renderer module to one; shader workload is identical to High.',
    'served-high uses the exact server module without source override; each result records its own source delivery and hash.',
    'animationCallbacks counts real page requestAnimationFrame callbacks, not GPU completions.',
  ],
  scenarios: [],
};
async function evaluate(expression) {
  const value = await cdp.call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (value.exceptionDetails) throw new Error(`${value.exceptionDetails.text}: ${value.result?.description ?? expression}`);
  return value.result.value;
}
async function waitFor(expression, timeout = 60000) {
  const until = Math.min(Date.now() + timeout, deadline);
  while (Date.now() < until) {
    if (await evaluate(expression)) return;
    await sleep(100);
  }
  throw new Error(`Timed out waiting for ${expression}`);
}
async function digestGraphics(useLocal = overrideLocalGraphics) {
  if (useLocal) {
    return createHash('sha256').update(await readFile(localGraphicsPath, 'utf8')).digest('hex');
  }
  const response = await fetch(new URL('engine/graphics.js', base), { cache: 'no-store', signal: AbortSignal.timeout(10000) });
  assert.equal(response.status, 200);
  return createHash('sha256').update(await response.text()).digest('hex');
}
const sampleExpression = `(() => {
  const d=stuntRacer.diagnostics(), state=stuntRacer.observe(), canvas=document.querySelector('#game');
  return { now:performance.now(), diagnostics:d, phase:state.phase, tick:state.tick,
    speed:state.player.speed, hidden:document.hidden,
    viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio},
    canvas:{width:canvas.width,height:canvas.height},
    instrument:structuredClone(__rendererProfile) };
})()`;
const mean = values => values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;

try {
  let port;
  for (let attempt = 0; attempt < 250; attempt++) {
    if (chrome.exitCode !== null) throw new Error(`Chrome exited: ${chromeErrors}`);
    try { port = (await readFile(resolve(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (port) break;
    await sleep(80);
  }
  assert.ok(port, `Chrome did not become ready: ${chromeErrors}`);
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10000) })).json();
  const target = targets.find(item => item.type === 'page');
  assert.ok(target, 'Chrome must expose its owned blank page');
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('WebSocket connection timeout')), 10000);
    socket.addEventListener('open', () => { clearTimeout(timeout); resolve(); }, { once: true });
    socket.addEventListener('error', error => { clearTimeout(timeout); reject(error); }, { once: true });
  });
  cdp = new Cdp(socket);
  await cdp.call('Page.enable');
  await cdp.call('Runtime.enable');
  await cdp.call('Network.enable');
  await cdp.call('Network.setCacheDisabled', { cacheDisabled: true });
  if (overrideLocalGraphics) {
    cdp.on('Fetch.requestPaused', event => {
      if (activeGraphicsSource === null) {
        void cdp.call('Fetch.continueRequest', { requestId: event.requestId })
          .catch(error => interceptionErrors.push(error.message));
        return;
      }
      void cdp.call('Fetch.fulfillRequest', { requestId: event.requestId, responseCode: 200,
        responseHeaders: [{ name: 'Content-Type', value: 'text/javascript' }, { name: 'Cache-Control', value: 'no-store' }],
        body: Buffer.from(activeGraphicsSource).toString('base64'),
      }).then(() => { moduleOverrides++; }).catch(error => interceptionErrors.push(error.message));
    });
    await cdp.call('Fetch.enable', { patterns: [{ urlPattern: `${graphicsUrl}*`, requestStage: 'Request' }] });
  }
  await cdp.call('Emulation.setDeviceMetricsOverride', {
    width: 1440, height: 900, deviceScaleFactor, mobile: false,
  });
  report.browser = await cdp.call('Browser.getVersion');
  for (const name of scenarios) {
    assert.ok(Date.now() + warmupMs + measureMs < deadline, 'Four-minute profiling budget exceeded');
    const spec = definitions[name], useLocal = overrideLocalGraphics && spec.source !== 'served';
    const sourceKind = useLocal ? 'local' : 'served';
    const graphicsSha256Before = await digestGraphics(useLocal);
    if (sourceHashes.has(sourceKind)) assert.equal(graphicsSha256Before, sourceHashes.get(sourceKind),
      `${sourceKind} graphics source changed between scenarios; experiment refused`);
    else sourceHashes.set(sourceKind, graphicsSha256Before);
    if (scriptId) await cdp.call('Page.removeScriptToEvaluateOnNewDocument', { identifier: scriptId });
    let injectedGraphicsSha256 = null;
    const overridesBefore = moduleOverrides;
    activeGraphicsSource = null;
    if (useLocal) {
      activeGraphicsSource = await readFile(localGraphicsPath, 'utf8');
      if (spec.queueLimit) {
        const guard = 'return this.pending.length < 2;';
        assert.equal(activeGraphicsSource.split(guard).length, 2, 'The bounded queue guard changed; experiment refused');
        activeGraphicsSource = activeGraphicsSource.replace(guard, `return this.pending.length < ${spec.queueLimit};`);
      }
      injectedGraphicsSha256 = createHash('sha256').update(activeGraphicsSource).digest('hex');
    }
    ({ identifier: scriptId } = await cdp.call('Page.addScriptToEvaluateOnNewDocument', {
      source: `(${installExperiment.toString()})(${JSON.stringify(spec)})`,
    }));
    await cdp.call('Page.navigate', { url: base.href });
    await waitFor('Boolean(globalThis.stuntRacer?.diagnostics().ready || document.querySelector("#error-panel:not([hidden])"))');
    assert.equal(await evaluate('Boolean(globalThis.stuntRacer?.diagnostics().ready)'), true,
      await evaluate('document.querySelector("#error-message")?.textContent'));
    assert.deepEqual(interceptionErrors, [], 'The renderer module override must actually succeed');
    assert.equal(moduleOverrides - overridesBefore, useLocal ? 1 : 0, 'Measure the selected module, not a stale or unintended bundle');
    // Exercise the real UI change handler without opening help or altering game state.
    await evaluate(`(() => {
      const select=document.querySelector('#graphics-quality');
      if (!select || ![...select.options].some(o=>o.value===${JSON.stringify(spec.quality)}))
        throw new Error('Quality selector contract changed');
      select.value=${JSON.stringify(spec.quality)};
      select.dispatchEvent(new Event('change',{bubbles:true}));
    })()`);
    await sleep(warmupMs);
    const first = await evaluate(sampleExpression), samples = [first];
    assert.equal(first.phase, 'ready', 'Compare only the stationary real ready preview');
    assert.equal(first.instrument.sceneShaders, 1, 'Expected one recognized main scene shader');
    assert.equal(first.instrument.shaderPatches, spec.sampling ? 1 : 0);
    assert.equal(first.instrument.skyPatches, spec.flatSky ? 1 : 0);
    if (enableGpu) {
      assert.match(first.diagnostics.renderer, /NVIDIA/i, 'Hardware proof requires the actual NVIDIA renderer');
      assert.doesNotMatch(first.diagnostics.renderer, /SwiftShader|llvmpipe|software/i,
        'GPU opt-in must not be mislabeled if Chrome still uses software rendering');
    }
    assert.deepEqual(first.viewport, report.viewport, 'Viewport and DPR must be identical across comparisons');
    while (samples.at(-1).now - first.now < measureMs) {
      await sleep(1000);
      samples.push(await evaluate(sampleExpression));
    }
    const last = samples.at(-1), elapsedMs = last.now - first.now;
    for (const sample of samples) {
      assert.equal(sample.phase, 'ready');
      assert.equal(sample.hidden, false);
      assert.equal(sample.diagnostics.failed, false);
      assert.equal(sample.diagnostics.renderer, first.diagnostics.renderer);
      assert.deepEqual(sample.canvas, first.canvas, 'Drawing buffer must not change during a sample');
      assert.ok(Number.isFinite(sample.diagnostics.completedFrames));
      assert.ok(sample.instrument.pendingFrames <= (spec.queueLimit ?? 2), 'Outstanding GPU work must stay bounded');
    }
    const completed = last.diagnostics.completedFrames - first.diagnostics.completedFrames;
    const submitted = last.diagnostics.submittedFrames - first.diagnostics.submittedFrames;
    const counts = Object.fromEntries(Object.keys(first.instrument.counts)
      .map(key => [key, last.instrument.counts[key] - first.instrument.counts[key]]));
    assert.ok(completed > 0 && counts.mainDraws > 0, 'Only count genuine newly rendered GPU-completed frames');
    assert.equal(counts.fences, submitted, 'Every submitted frame must retain the real GPU fence');
    assert.equal(counts.retiredFences, completed, 'Only actual fence retirements may increment completedFrames');
    assert.equal(submitted - completed, last.instrument.pendingFrames - first.instrument.pendingFrames);
    assert.ok(Math.abs(submitted - completed) <= (spec.queueLimit ?? 2), 'Do not hide a growing GPU queue');
    if (spec.skipShadow) assert.ok(counts.skippedShadowDraws > 0 && counts.shadowDraws === 0);
    else assert.ok(counts.shadowDraws > 0 && counts.skippedShadowDraws === 0);
    const graphicsSha256After = await digestGraphics(useLocal);
    assert.equal(graphicsSha256After, graphicsSha256Before, 'Served graphics source changed during this measurement; rerun it');
    const result = {
      name, spec, renderer: first.diagnostics.renderer, canvas: first.canvas,
      hardwareVerified: enableGpu,
      graphicsSource: useLocal ? { delivery: 'CDP Fetch.fulfillRequest', path: localGraphicsPath }
        : { delivery: 'existing local demo server', url: graphicsUrl },
      shadowSize: last.instrument.shadowSize, graphicsSha256: graphicsSha256Before,
      injectedGraphicsSha256, moduleOverrides: moduleOverrides - overridesBefore,
      maxPendingFrames: last.instrument.maxPendingFrames, pendingFrames: last.instrument.pendingFrames,
      elapsedMs, completedFrames: completed, submittedFrames: submitted,
      completedFramesPerSecond: completed * 1000 / elapsedMs,
      submittedFramesPerSecond: submitted * 1000 / elapsedMs,
      animationCallbacksPerSecond: counts.animationCallbacks * 1000 / elapsedMs,
      mainThreadMillisecondsMean: mean(samples.map(s => s.diagnostics.mainThreadMilliseconds)),
      gpuMillisecondsMean: mean(samples.map(s => s.diagnostics.gpuMilliseconds).filter(Number.isFinite)),
      counts, samples,
    };
    report.scenarios.push(result);
    await writeFile(resolve(output, 'report.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify({ name, fps: result.completedFramesPerSecond,
      submittedFps: result.submittedFramesPerSecond, callbackFps: result.animationCallbacksPerSecond,
      cpuMs: result.mainThreadMillisecondsMean, gpuMs: result.gpuMillisecondsMean,
      canvas: result.canvas, shadowSize: result.shadowSize, maxPendingFrames: result.maxPendingFrames,
      renderer: result.renderer }));
  }
} catch (error) {
  report.failure = error.stack;
  process.exitCode = 1;
} finally {
  report.finishedAt = new Date().toISOString();
  report.chromeErrors = chromeErrors;
  socket?.close();
  if (chrome.exitCode === null && chrome.signalCode === null) {
    const exited = new Promise(resolve => chrome.once('exit', resolve));
    chrome.kill('SIGTERM');
    await Promise.race([exited, sleep(5000, undefined, { ref: false })]);
    if (chrome.exitCode === null && chrome.signalCode === null) {
      chrome.kill('SIGKILL');
      await exited;
    }
  }
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  await writeFile(resolve(output, 'report.json'), JSON.stringify(report, null, 2));
  console.log(`Renderer profiling report: ${resolve(output, 'report.json')}`);
  if (report.failure) console.error(report.failure);
}
