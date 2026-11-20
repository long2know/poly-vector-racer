import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const base = new URL(process.env.STUNT_URL ?? 'http://127.0.0.1:5188/stunt-car-racer/');
const focus = process.env.STUNT_BROWSER_FOCUS;
const artifactRoot = resolve('artifacts/browser');
await mkdir(artifactRoot, { recursive: true });
const output = await mkdtemp(resolve(artifactRoot, 'run-'));
const profile = await mkdtemp(resolve(output, 'chrome-'));
const chrome = spawn(process.env.CHROME_BIN ?? '/usr/bin/google-chrome', [
  '--headless=new', ...(process.env.STUNT_ENABLE_GPU === '1' ? ['--enable-gpu'] : []),
  '--enable-automation', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--window-size=1440,1000',
  '--force-device-scale-factor=1', 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
let chromeErrors = '';
chrome.stderr.on('data', chunk => { chromeErrors = (chromeErrors + chunk.toString()).slice(-12000); });

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
        if (message.error) request.reject(new Error(message.error.message));
        else request.resolve(message.result);
      } else {
        for (const listener of this.listeners.get(message.method) ?? []) listener(message.params);
      }
    });
  }

  async call(method, params = {}) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  on(method, listener) {
    if (!this.listeners.has(method)) this.listeners.set(method, []);
    this.listeners.get(method).push(listener);
  }
}

let cdp, socket;
const errors = [], failedRequests = [], unexpectedUploads = [], evidence = {};
async function evaluate(expression) {
  const result = await cdp.call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text + ': ' + (result.result?.description ?? expression));
  return result.result.value;
}
async function waitFor(expression, timeout = 30000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const result = await evaluate(expression);
    if (result) return result;
    await sleep(80);
  }
  throw new Error(`Timed out waiting for ${expression}`);
}
async function click(id) {
  const rect = await evaluate(`(() => {const e=document.getElementById(${JSON.stringify(id)});e.scrollIntoView({block:"center",inline:"nearest"});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()`);
  await cdp.call('Input.dispatchMouseEvent', { type: 'mouseMoved', ...rect });
  await cdp.call('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...rect });
  await cdp.call('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...rect });
}
async function touchTap(id) {
  const point = await evaluate(`(() => {const e=document.getElementById(${JSON.stringify(id)});e.scrollIntoView({block:"center",inline:"nearest"});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,id:99};})()`);
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] });
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
}
async function releaseTouch(point, remaining) {
  try {
    await cdp.call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [point] });
  } catch (error) {
    if (!/TouchEnd.*must not.*touch points/.test(error.message)) throw error;
    await cdp.call('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: remaining });
  }
}
async function key(code, down, keyValue) {
  const value = keyValue ?? (code.startsWith('Key') ? code.slice(3).toLowerCase() : code === 'Space' ? ' ' : code);
  await cdp.call('Input.dispatchKeyEvent', {
    type: down ? 'keyDown' : 'keyUp', code, key: value,
    windowsVirtualKeyCode: code.startsWith('Key') ? code.charCodeAt(3) : code === 'Space' ? 32 : 0,
  });
}
async function tap(code) {
  await key(code, true);
  await key(code, false);
}
async function select(id, value) {
  await evaluate(`(() => {const e=document.getElementById(${JSON.stringify(id)});e.value=${JSON.stringify(value)};e.dispatchEvent(new Event("change",{bubbles:true}));})()`);
}
async function screenshot(name) {
  const { data } = await cdp.call('Page.captureScreenshot', { format: 'png' });
  await writeFile(resolve(output, name), Buffer.from(data, 'base64'));
}
async function chooseFile(button, file) {
  const opened = new Promise(resolve => cdp.on('Page.fileChooserOpened', resolve));
  await click(button);
  const chooser = await Promise.race([
    opened,
    sleep(5000, undefined, { ref: false }).then(() => { throw new Error('The local track file chooser did not open.'); }),
  ]);
  await cdp.call('DOM.setFileInputFiles', { files: [file], backendNodeId: chooser.backendNodeId });
}
async function observe() {
  return evaluate('stuntRacer.observe()');
}
async function ensureRacing() {
  if ((await observe()).phase === 'paused') {
    evidence.browserStallPauses = (evidence.browserStallPauses ?? 0) + 1;
    await click('resume-button');
  }
  await waitFor('stuntRacer.observe().phase === "racing"');
}
async function virtualPad(pressed = {}, steer = 0) {
  await evaluate(`window.__qualificationPad={index:0,id:"Automated virtual gamepad",connected:true,mapping:"standard",axes:[${steer},0,0,0],buttons:Array.from({length:17},(_,i)=>({pressed:!!(${JSON.stringify(pressed)})[i],value:(${JSON.stringify(pressed)})[i]??0}))};Object.defineProperty(navigator,"getGamepads",{configurable:true,value:()=>[window.__qualificationPad]});`);
}

async function startDriver() {
  await virtualPad();
  await evaluate(`(() => {
    const track=stuntRacer.track(), points=track.points;
    const clamp=(value,min,max)=>Math.max(min,Math.min(max,value));
    const angle=value=>Math.atan2(Math.sin(value),Math.cos(value));
    const at=s=>{
      s=((s%track.length)+track.length)%track.length;
      let low=0,high=points.length-1;
      while(low<high){const middle=(low+high+1)>>1;if(points[middle].s<=s)low=middle;else high=middle-1;}
      const a=points[low],b=points[(low+1)%points.length];
      const t=(s-a.s)/((low+1===points.length?track.length:b.s)-a.s);
      return {x:a.x+(b.x-a.x)*t,z:a.z+(b.z-a.z)*t,heading:a.heading+angle(b.heading-a.heading)*t};
    };
    window.__qualificationDriver=setInterval(()=>{
      const state=stuntRacer.observe(), car=state.player, pad=window.__qualificationPad;
      let steer=0,throttle=0,brake=0;
      if(state.phase==="racing"){
        let nearest=Infinity,s=0;
        for(let i=0;i<points.length;i++){
          const a=points[i],b=points[(i+1)%points.length],dx=b.x-a.x,dz=b.z-a.z;
          const t=clamp(((car.x-a.x)*dx+(car.z-a.z)*dz)/(dx*dx+dz*dz),0,1);
          const distance=(car.x-a.x-dx*t)**2+(car.z-a.z-dz*t)**2;
          if(distance<nearest){nearest=distance;s=a.s+((i+1===points.length?track.length:b.s)-a.s)*t;}
        }
        const speed=Math.abs(car.speed),look=10+speed*.4,target=at(s+look),road=at(s);
        const error=angle(Math.atan2(target.x-car.x,target.z-car.z)-car.yaw);
        const rate=2*Math.max(speed,8)*Math.sin(error)/look;
        const scale=clamp(car.speed/12,-1,1)*(1.35/(1+speed/13));
        steer=Math.abs(scale)<.03?0:clamp(rate/scale,-1,1);
        const desired=Math.abs(angle(at(s+38).heading-road.heading))>.06?20:29;
        throttle=clamp((desired-car.speed)*.65,0,1);
        brake=clamp((car.speed-desired-1)*.35,0,1);
      }
      pad.axes[0]=steer===0?0:Math.sign(steer)*(.13+.87*Math.abs(steer));
      for(const [index,value] of [[7,throttle],[6,brake]])pad.buttons[index]={pressed:value>.5,value};
    },16);
  })()`);
}

async function stopDriver() {
  await evaluate('clearInterval(window.__qualificationDriver)');
  await virtualPad();
  await sleep(80);
}

async function driveUntil(expression, timeout = 90000, heldKeys = []) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await evaluate(expression)) return;
    const state = await observe();
    if (state.phase === 'finished') throw new Error(`The run finished without reaching ${expression}`);
    if (state.phase === 'paused') {
      evidence.browserStallPauses = (evidence.browserStallPauses ?? 0) + 1;
      await click('resume-button');
      for (const [code, value] of heldKeys) await key(code, true, value);
    }
    await sleep(100);
  }
  throw new Error(`Public-input driving did not reach ${expression}`);
}

async function verifyFlight() {
  await select('game-mode', 'flight');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.observe().flight?.phase === "ready"');
  const diagnostics = await evaluate('stuntRacer.diagnostics()');
  assert.equal(diagnostics.compatibility.vehicleMode, 'flight');
  assert.equal(diagnostics.compatibility.physicsProfile, 'authored-flight');
  assert.equal(diagnostics.compatibility.qualification, 'authored-flight-prototype-not-original-port');
  assert.equal(diagnostics.compatibility.originalStockParity, false);
  assert.equal(diagnostics.selectedTrack, 'flight:interceptor-bay');
  assert.equal(await evaluate('document.querySelector("#physics-mode").disabled'), false);
  const runway = (await observe()).flight.aircraft;
  assert.equal(runway.onGround, true);
  assert.equal(runway.gearDown, true);
  if (diagnostics.camera !== 'chase') await tap('KeyC');
  await screenshot('10-flight-runway-ready.png');
  await click('air-start-button');
  await waitFor('stuntRacer.observe().phase === "racing" && stuntRacer.observe().flight.aircraft.y > 250');
  await waitFor('stuntRacer.diagnostics().audioState === "running" && stuntRacer.diagnostics().audioRms > 0.0001');
  await tap('KeyR');
  await waitFor('stuntRacer.observe().phase === "ready" && document.body.dataset.phase === "ready"');
  await click('start-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  await key('Equal', true, '=');
  await driveUntil('stuntRacer.observe().flight.aircraft.throttle > 0.98', 15000, [['Equal', '=']]);
  await key('Equal', false, '=');
  await driveUntil('stuntRacer.observe().flight.aircraft.speed > 62', 45000);
  await key('KeyS', true);
  await driveUntil('!stuntRacer.observe().flight.aircraft.onGround', 15000, [['KeyS']]);
  await key('KeyS', false);
  await driveUntil(`stuntRacer.observe().flight.aircraft.y > ${runway.y + 5}`, 15000);
  evidence.flight = { takeoff: await observe() };
  assert.ok(evidence.flight.takeoff.flight.aircraft.y > runway.y + 2, 'real keyboard throttle and elevator must lift the aircraft');
  assert.ok(evidence.flight.takeoff.flight.aircraft.z > runway.z + 100);
  assert.equal(await evaluate('document.querySelector("#speed-unit").textContent'), 'KTS');
  await tap('KeyG');
  await waitFor('!stuntRacer.observe().flight.aircraft.gearDown');
  await key('Space', true);
  await sleep(600);
  assert.equal((await observe()).flight.aircraft.afterburner, true);
  await key('Space', false);
  await key('KeyD', true);
  await sleep(450);
  await key('KeyD', false);
  evidence.flight.banked = await observe();
  assert.ok(evidence.flight.banked.flight.aircraft.up.x > 0.1, 'right roll must really lower the right wing');
  assert.ok(evidence.flight.banked.flight.aircraft.fuel < 1);
  await screenshot('11-flight-bank-and-afterburner.png');
  await tap('KeyC');
  await waitFor('stuntRacer.diagnostics().camera === "cockpit"');
  await screenshot('12-flight-cockpit.png');
  await click('pause-button');
  await waitFor('stuntRacer.observe().phase === "paused"');
  const paused = (await observe()).flight;
  await sleep(250);
  assert.deepEqual((await observe()).flight, paused, 'pause must freeze the actual aircraft, fuel and flight clock');
  await cdp.call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  await touchTap('help-button');
  await touchTap('flight-air-start');
  await waitFor('stuntRacer.observe().phase === "racing" && document.body.dataset.phase === "racing" && stuntRacer.observe().flight.aircraft.y > 250 && document.querySelector("#touch-steering").getBoundingClientRect().width > 0');
  const before = (await observe()).flight.aircraft;
  const pad = await evaluate('(() => {const r=document.querySelector("#touch-steering").getBoundingClientRect();return {x:r.x+r.width*.6,y:r.y+r.height*.33,id:10};})()');
  const gas = await evaluate('(() => {const r=document.querySelector("[data-control=throttle]").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,id:20};})()');
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [pad, gas] });
  await sleep(650);
  evidence.flight.touch = await observe();
  assert.ok(evidence.flight.touch.flight.aircraft.forward.y > before.forward.y + 0.02, 'touch stick up must actually pitch the aircraft up');
  assert.ok(evidence.flight.touch.flight.aircraft.up.x > before.up.x + 0.03, 'touch stick right must bank the aircraft');
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [pad, { ...gas, y: gas.y - 42 }] });
  await sleep(450);
  assert.equal((await observe()).flight.aircraft.afterburner, true);
  await releaseTouch({ ...gas, y: gas.y - 42 }, [pad]);
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  await sleep(100);
  assert.equal((await observe()).flight.aircraft.afterburner, false);
  await touchTap('touch-gear');
  await waitFor('!stuntRacer.observe().flight.aircraft.gearDown');
  await screenshot('13-flight-phone-touch.png');
  await cdp.call('Emulation.setDeviceMetricsOverride', { width: 844, height: 390, deviceScaleFactor: 1, mobile: true });
  await sleep(200);
  const layout = await evaluate(`(() => {
    const r=document.querySelector("#touch-steering").getBoundingClientRect();
    const p=document.querySelector(".touch-pedal-group").getBoundingClientRect();
    return {width:innerWidth,scroll:document.documentElement.scrollWidth,left:r.left,right:r.right,pedalsLeft:p.left,pedalsRight:p.right};
  })()`);
  assert.equal(layout.scroll, 844);
  assert.ok(layout.left >= 0 && layout.right + 8 < layout.pedalsLeft && layout.pedalsRight <= 844);
  await screenshot('14-flight-landscape.png');
  await touchTap('touch-recover');
  await waitFor('stuntRacer.observe().phase === "ready" && stuntRacer.observe().flight.recoveries > 0');
  assert.equal((await observe()).flight.aircraft.onGround, true);
  await cdp.call('Emulation.clearDeviceMetricsOverride');
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: false });
  await select('game-mode', 'time-attack');
  await waitFor('stuntRacer.diagnostics().ready && !stuntRacer.observe().flight');
  assert.equal((await evaluate('stuntRacer.diagnostics()')).compatibility.physicsProfile, 'authored');
  assert.equal((await observe()).ghost.duration, 0, 'vehicle switching must not leak a race ghost into a fresh driving session');
}

async function verifyOriginalFlight() {
  const zip = process.env.STUNT_ORIGINAL_ZIP;
  const configured = await evaluate('stuntRacer.diagnostics().originalFlightConfigurationAvailable');
  if (!zip && !configured) {
    if (process.env.STUNT_REQUIRE_ORIGINAL_FLIGHT === '1') assert.fail('Original-flight qualification requires the actual supplied disk/package.');
    evidence.originalFlight = { requested: false, reason: 'No external original disk/package configured.' };
    return;
  }
  if (zip) {
    await click('help-button');
    await chooseFile('import-flight-button', resolve(zip));
  } else await select('game-mode', 'flight-original');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.flightSourceActive', 90000);
  const initial = await evaluate('stuntRacer.diagnostics()');
  assert.equal(initial.compatibility.physicsProfile, 'amiga-flight-source-compatible');
  assert.equal(initial.compatibility.simulationStepSeconds, 0.125);
  assert.equal(initial.compatibility.originalStockParity, false);
  assert.equal(initial.compatibility.originalFlightIdentity.requiredTablesValidated, true);
  assert.equal(initial.compatibility.originalFlightIdentity.contactGeometryValidated, true);
  assert.equal(initial.compatibility.originalFlightIdentity.executableSha256,
    '38b6e1f331460aa4939690173d95c38b105d1d089226359a4c8655052ea29ab5');
  assert.ok(initial.flightWorld.sourceSurfaces > 100 && initial.flightWorld.sourceObjects > 0);
  assert.equal((await observe()).flight.display.altitudeFeet, 5);
  assert.equal((await observe()).flight.display.headingDegrees, 28);
  assert.equal(await evaluate('document.querySelector("#physics-mode").value'), 'flight-original');
  await screenshot('15-original-amiga-runway.png');
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  await key('Equal', true, '=');
  await driveUntil('stuntRacer.observe().flight.aircraft.throttle > 0.99', 30000, [['Equal', '=']]);
  await key('Equal', false, '=');
  await driveUntil('stuntRacer.observe().flight.display.speedKnots > 130', 45000);
  await key('KeyS', true);
  await driveUntil('!stuntRacer.observe().flight.aircraft.onGround', 15000, [['KeyS']]);
  await key('KeyS', false);
  await driveUntil('stuntRacer.observe().flight.aircraft.y > 6', 15000);
  const takeoff = await observe();
  assert.ok(takeoff.flight.aircraft.fuel < 1);
  await tap('KeyG');
  await waitFor('!stuntRacer.observe().flight.aircraft.gearDown');
  await tap('KeyK');
  await waitFor('stuntRacer.observe().flight.configuration.hookDown');
  await key('Space', true);
  await driveUntil('stuntRacer.observe().flight.aircraft.afterburner', 10000, [['Space']]);
  await key('Space', false);
  await screenshot('16-original-amiga-airborne.png');
  await click('help-button');
  await waitFor('stuntRacer.observe().phase === "paused"');
  const frozen = (await observe()).flight;
  await sleep(250);
  assert.deepEqual((await observe()).flight, frozen);
  await click('flight-carrier-start');
  await waitFor('stuntRacer.observe().phase === "ready" && document.body.dataset.phase === "ready"');
  const deck = await observe();
  assert.equal(deck.flight.configuration.onCarrier, true);
  assert.equal(deck.flight.display.altitudeFeet, 145);
  await screenshot('17-original-carrier-deck.png');
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  await key('Equal', true, '=');
  await driveUntil('stuntRacer.observe().flight.aircraft.throttle > 0.99', 30000, [['Equal', '=']]);
  await key('Equal', false, '=');
  await driveUntil('stuntRacer.observe().flight.display.speedKnots > 80', 20000);
  const catapult = await observe();
  assert.ok(Math.hypot(catapult.player.x - deck.player.x, catapult.player.z - deck.player.z) > 5);
  await screenshot('18-original-carrier-launch.png');
  await click('help-button');
  await select('physics-mode', 'modern');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.physicsProfile === "authored-flight"');
  await click('help-button');
  await select('physics-mode', 'flight-original');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.flightSourceActive');
  await click('help-button');
  await click('flight-hook-button');
  await waitFor('stuntRacer.observe().flight.configuration.hookDown');
  await click('flight-hook-button');
  await waitFor('!stuntRacer.observe().flight.configuration.hookDown');
  await click('close-help');
  if (zip) {
    const invalid = Buffer.from(await readFile(zip));
    invalid[14] ^= 1;
    const path = resolve(output, 'damaged-original-disk.zip');
    await writeFile(path, invalid);
    try {
      await click('help-button');
      await chooseFile('import-flight-button', path);
      await waitFor('document.querySelector("#toast").textContent.startsWith("Original disk rejected:")');
      assert.equal((await evaluate('stuntRacer.diagnostics()')).compatibility.flightSourceActive, true);
      await click('close-help');
    } finally { await rm(path); }
  }
  await cdp.call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  await touchTap('help-button');
  await touchTap('flight-air-start');
  await driveUntil('stuntRacer.observe().phase === "racing" && document.body.dataset.phase === "racing"', 10000);
  const beforeTouch = (await observe()).flight.aircraft;
  const pad = await evaluate('(() => {const r=document.querySelector("#touch-steering").getBoundingClientRect();return {x:r.x+r.width*.65,y:r.y+r.height*.33,id:10};})()');
  const gas = await evaluate('(() => {const r=document.querySelector("[data-control=throttle]").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,id:20};})()');
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [pad, gas] });
  await driveUntil(`stuntRacer.observe().flight.aircraft.forward.y > ${beforeTouch.forward.y + 0.02} &&
    stuntRacer.observe().flight.aircraft.up.x > ${beforeTouch.up.x + 0.03}`, 10000);
  const touch = await observe();
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [pad, { ...gas, y: gas.y - 42 }] });
  await driveUntil('stuntRacer.observe().flight.aircraft.afterburner', 10000);
  await releaseTouch({ ...gas, y: gas.y - 42 }, [pad]);
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  await driveUntil('!stuntRacer.observe().flight.aircraft.afterburner', 10000);
  const gearDown = (await observe()).flight.aircraft.gearDown;
  await touchTap('touch-gear');
  await waitFor(`stuntRacer.observe().flight.aircraft.gearDown === ${!gearDown}`);
  await touchTap('touch-hook');
  await waitFor('stuntRacer.observe().flight.configuration.hookDown');
  await touchTap('help-button');
  await touchTap('flight-air-start');
  await waitFor('stuntRacer.observe().phase === "racing" && document.body.dataset.phase === "racing"');
  await touchTap('touch-hook');
  await waitFor('stuntRacer.observe().flight.configuration.hookDown');
  if ((await evaluate('stuntRacer.diagnostics().camera')) !== 'cockpit') await touchTap('camera-button');
  await waitFor('document.querySelector("#toast").hidden', 10000);
  const layouts = [];
  for (const [width, height, image] of [[390, 844, '19-original-phone-cockpit.png'],
    [844, 390, '20-original-landscape.png'], [1024, 768, '21-original-tablet.png']]) {
    await cdp.call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
    await sleep(200);
    const layout = await evaluate(`(() => {
      const rect=id=>{const r=document.getElementById(id).getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:r.width,height:r.height};};
      return {width:innerWidth,scroll:document.documentElement.scrollWidth,
        steer:rect("touch-steering"),gear:rect("touch-gear"),hook:rect("touch-hook")};
    })()`);
    assert.equal(layout.scroll, width, 'original-flight layouts must not overflow horizontally');
    assert.ok(layout.steer.left >= 0 && layout.steer.right + 8 < layout.gear.left);
    for (const rect of [layout.gear, layout.hook])
      assert.ok(rect.width >= 44 && rect.height >= 44 && rect.left >= 0 && rect.right <= width && rect.bottom <= height,
        'original gear and hook must remain separate, reachable touch targets');
    assert.ok(layout.gear.right <= layout.hook.left, 'gear and hook targets must not overlap');
    layouts.push(layout);
    await screenshot(image);
  }
  await touchTap('touch-recover');
  await waitFor('stuntRacer.observe().phase === "ready" && stuntRacer.observe().flight.recoveries > 0');
  await cdp.call('Emulation.clearDeviceMetricsOverride');
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: false });
  evidence.originalFlight = { initial, takeoff, deck, catapult, touch, layouts };
  await select('game-mode', 'time-attack');
  await waitFor('stuntRacer.diagnostics().ready && !stuntRacer.observe().flight');
}

async function chooseExperienceMode(mode) {
  if ((await observe()).phase !== 'ready' && !await evaluate('document.querySelector("#help-dialog").open'))
    await click('help-button');
  await select(await evaluate('document.querySelector("#help-dialog").open') ? 'session-mode' : 'game-mode', mode);
  await waitFor(`stuntRacer.diagnostics().ready && document.querySelector("#game-mode").value === ${JSON.stringify(mode)}`);
}

async function verifyGeographicFlight() {
  const configured = await evaluate('stuntRacer.diagnostics().worldFlightConfigurationAvailable');
  if (!configured) {
    assert.notEqual(focus, 'geographic-flight', 'The geographic-flight check requires real hosted elevation data.');
    return;
  }
  const world = 'stuntRacer.diagnostics().geographicFlight';
  const depart = async (id, name) => {
    const phase = (await observe()).phase;
    if (phase !== 'ready') {
      await click('garage-button');
      await waitFor('stuntRacer.observe().phase === "ready"');
    }
    await select('world-departure-menu', id);
    await waitFor(`${world}?.destination === ${JSON.stringify(id)} && stuntRacer.diagnostics().ready`, 30000);
    assert.match(await evaluate('document.querySelector("#circuit-name").textContent'), name);
  };
  await click('world-flight-button');
  await waitFor(`${world}?.destination === "boeing" && ${world}.finest >= 7`, 45000);
  const runway = await observe();
  assert.equal(runway.flight.aircraft.onGround, true);
  assert.ok(Math.abs(runway.flight.aircraft.y - 8.8) < .02, 'Boeing Field has real runway elevation');
  assert.match(await evaluate('document.querySelector("#start-label").textContent'), /BOEING FIELD/);
  await screenshot('51-world-flight-boeing-field.png');

  await depart('seattle', /SEATTLE/);
  await waitFor(`${world}.finest === 8 && ${world}.buildings >= 1200 && ${world}.visible > 12`, 45000);
  const scenic = await observe();
  assert.equal(scenic.flight.aircraft.onGround, false);
  assert.ok(scenic.flight.aircraft.y > 400 && scenic.flight.aircraft.speed > 120);
  assert.ok(await evaluate('document.querySelector(".world-credit").textContent.includes("OpenStreetMap")'));
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing" && stuntRacer.observe().flight.time > 4', 24000);
  const flying = await observe();
  assert.ok(Math.hypot(flying.player.x - scenic.player.x, flying.player.z - scenic.player.z) > 40,
    'C# jet actually travels across streamed geography');
  assert.equal(flying.terrainWaiting, false);
  assert.ok(await evaluate('document.querySelector("#map-water").getAttribute("d").length > 100'),
    'the geographic minimap shows real Puget Sound water');
  await screenshot('52-world-flight-seattle.png');
  const seattle = await evaluate(world);
  if ((await evaluate('stuntRacer.diagnostics()')).camera !== 'chase') await tap('KeyC');
  await tap('KeyV');
  const layer = (selector) => `getComputedStyle(document.querySelector(${JSON.stringify(selector)})).display !== "none"`;
  for (const selector of ['.race-timing', '.minimap', '.telemetry', '.speedometer', '#flight-guidance'])
    assert.equal(await evaluate(layer(selector)), false, `${selector} disappears in clean chase view`);
  assert.equal(await evaluate(layer('#pause-button')), true, 'clean view must not remove pause controls');
  assert.equal(await evaluate('document.querySelector("#flight-hud-button").getAttribute("aria-pressed")'), 'false');
  await screenshot('58-world-flight-clean-chase.png');
  await tap('KeyC');
  assert.equal(await evaluate(layer('#flight-guidance')), true, 'the cockpit keeps its own instruments');
  assert.equal(await evaluate(layer('.combiner-metrics')), true);
  assert.match(await evaluate('document.querySelector("#cockpit-mach").textContent'), /^M 0\./);
  assert.equal(await evaluate(layer('#flight-hud-button')), false, 'no nonfunctional chase switch in cockpit');
  await screenshot('59-world-flight-jet-cockpit.png');
  await tap('KeyV');
  await tap('KeyC');
  assert.equal(await evaluate(layer('#flight-guidance')), false, 'cockpit keypress does not change the stored chase setting');
  await click('flight-hud-button');
  assert.equal(await evaluate(layer('#flight-guidance')), true, 'the toolbar switch restores flight data');

  await depart('rainier', /RAINIER/);
  await waitFor(`${world}.finest >= 6 && ${world}.targetElevation > 4150 && ${world}.visible > 15`, 50000);
  const rainier = await evaluate(world);
  assert.ok(rainier.targetElevation < 4430);
  assert.equal(rainier.errors, 0);
  await screenshot('53-world-flight-rainier.png');

  await depart('olympics', /OLYMPIC/);
  await waitFor(`${world}.finest >= 6 && ${world}.targetElevation > 2200`, 45000);
  await screenshot('54-world-flight-olympics.png');
  await depart('cascades', /CASCADES/);
  await waitFor(`${world}.finest >= 6 && ${world}.targetElevation > 2980`, 45000);
  await screenshot('55-world-flight-cascades.png');

  let expanded;
  if (await evaluate('Boolean(document.querySelector("#world-departure-menu option[value=cruise]"))')) {
    await depart('bay', /SAN FRANCISCO/);
    await waitFor(`${world}.finest === 8 && ${world}.landmarks.includes("Golden Gate Bridge")`, 55000);
    await screenshot('60-world-flight-golden-gate.png');
    await depart('san-francisco', /SAN FRANCISCO/);
    await waitFor(`${world}.buildings >= 5000 && ${world}.finest === 8`, 60000);
    const bay = await evaluate(world);
    await screenshot('61-world-flight-san-francisco.png');
    await depart('yosemite', /YOSEMITE/);
    // The valley under the aircraft is 32 m; Half Dome is still a lower LOD at this distance.
    await waitFor(`${world}.finest === 8 && ${world}.targetElevation > 2300`, 55000);
    await screenshot('62-world-flight-yosemite.png');
    const yosemite = await evaluate(world);
    await depart('canyon', /GRAND CANYON/);
    await waitFor(`${world}.finest === 8 && ${world}.targetElevation > 900 && ${world}.targetElevation < 1500`, 55000);
    await screenshot('63-world-flight-grand-canyon.png');
    const canyon = await evaluate(world);

    await depart('cruise', /CASCADES/);
    await click('air-start-button');
    await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
    const cruise = await observe();
    assert.equal(cruise.flight.aircraft.gearDown, false);
    await key('Equal', true);
    await key('Space', true);
    try {
      await driveUntil('stuntRacer.observe().flight.aircraft.speed > 515 && stuntRacer.observe().flight.aircraft.mach > 1.69',
        110000, [['Equal'], ['Space']]);
    } finally {
      await key('Equal', false);
      await key('Space', false);
    }
    const supersonic = (await observe()).flight.aircraft;
    assert.ok(Math.hypot(supersonic.x - cruise.flight.aircraft.x, supersonic.z - cruise.flight.aircraft.z) > 6000);
    assert.match(await evaluate('document.querySelector("#flight-mach").textContent'), /^MACH 1\./);
    assert.equal((await observe()).terrainWaiting, false);
    await screenshot('64-world-flight-supersonic.png');
    expanded = { bay, yosemite, canyon, supersonic };
  }

  await depart('new-york', /NEW YORK/);
  await waitFor(`${world}.finest === 2`, 45000);
  assert.equal((await evaluate(world)).buildings, 0, 'Seattle building detail is not misrepresented as nationwide');
  await depart('denali', /ALASKA/);
  await waitFor(`${world}.finest === 2 && ${world}.targetElevation > 4600`, 45000);
  const alaska = await evaluate(world);
  await screenshot('56-world-flight-alaska.png');
  await depart('mauna-loa', /HAWAII/);
  await waitFor(`${world}.finest === 2 && ${world}.targetElevation > 3750`, 45000);
  const hawaii = await evaluate(world);
  await screenshot('57-world-flight-hawaii.png');
  await chooseExperienceMode('flight');
  assert.equal(await evaluate('stuntRacer.diagnostics().compatibility.physicsProfile'), 'authored-flight');
  assert.equal(await evaluate(world), null, 'leaving geographic flight must release that scene');
  if (await evaluate('stuntRacer.diagnostics().originalFlightConfigurationAvailable')) {
    await chooseExperienceMode('flight-original');
    assert.equal(await evaluate('stuntRacer.diagnostics().compatibility.flightSourceActive'), true);
    assert.ok(await evaluate('stuntRacer.diagnostics().flightWorld.sourceObjects >= 400'));
  }
  await chooseExperienceMode('time-attack');
  evidence.geographicFlight = { seattle, rainier, alaska, hawaii, runwayAltitude: runway.flight.aircraft.y, expanded };
}

async function verifyStuntsLoop(mode = 'stunts') {
  const hard = mode === 'hard-driving';
  await chooseExperienceMode(mode);
  assert.equal((await evaluate('stuntRacer.diagnostics()')).spatialDriving, true);
  if (hard) assert.ok((await observe()).trial?.checkpointCount >= 8, 'Hard Drivin needs an actual timed checkpoint challenge');
  if ((await evaluate('stuntRacer.diagnostics().camera')) !== 'chase') await tap('KeyC');
  await screenshot(hard ? '36-hard-drivin-loop-course.png' : '38-stunts-loop-park.png');
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  await key('KeyW', true);
  await key('Space', true);
  await driveUntil('stuntRacer.observe().time>5.5', 15000, [['KeyW'], ['Space']]);
  await screenshot(hard ? '36b-hard-drivin-loop-approach.png' : '38b-stunts-loop-approach.png');
  await driveUntil('Math.abs(stuntRacer.observe().player.pitch)>1.2 && stuntRacer.observe().player.y>15', 25000,
    [['KeyW'], ['Space']]);
  await screenshot(hard ? '37-hard-drivin-driving-the-loop.png' : '39-stunts-driving-the-loop.png');
  await driveUntil('stuntRacer.observe().stunt.loopsCompleted >= 1', 20000, [['KeyW'], ['Space']]);
  await key('KeyW', false);
  await key('Space', false);
  const driven = await observe();
  evidence.experiences[hard ? 'hardDriving' : 'stuntsLoop'] = driven;
  assert.ok(driven.stunt.score > 0);
  assert.equal(driven.recoveries, 0, 'a scored loop must be driven without recovery or a fake animation');
  await screenshot(hard ? '37b-hard-drivin-loop-cleared.png' : '40-stunts-loop-cleared.png');
  const page = await evaluate('performance.timeOrigin');
  await click('garage-button');
  await waitFor('stuntRacer.observe().phase === "ready" && document.body.dataset.phase === "ready"');
  assert.equal(await evaluate('performance.timeOrigin'), page, 'Garage must not reload the browser');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("#menu")).display !== "none"'), true);
}

async function verifyOriginalWorldScene() {
  await chooseExperienceMode('flight-original');
  const original = await evaluate('stuntRacer.diagnostics()');
  assert.ok(original.flightWorld.sourceObjects >= 400, 'original scenery must include the newly materialized source models');
  assert.ok((await observe()).flight.activity.actors.length >= 12);
  assert.ok(await evaluate('document.querySelector("#map-road").getAttribute("d").length > 1000'),
    'the original map must show real coastlines, not only a bounding rectangle');
  await screenshot('41-completed-original-airfield.png');
  await click('landmark-tour-button');
  await driveUntil('stuntRacer.observe().phase === "racing" && stuntRacer.observe().flight.aircraft.y>100', 15000);
  await sleep(500);
  const bridgeView = await evaluate(`(() => {
    const world=stuntRacer.world(), mesh=world.sourceWorld.objects.find(o=>o.name==="Original scenery 48");
    const m=window.__renderedViewProjection;
    return {faces:mesh.faces.length,visible:mesh.vertices.filter(p=>{
      const w=m[3]*p.x+m[7]*p.y+m[11]*p.z+m[15];
      return w>0&&Math.abs((m[0]*p.x+m[4]*p.y+m[8]*p.z+m[12])/w)<1&&
        Math.abs((m[1]*p.x+m[5]*p.y+m[9]*p.z+m[13])/w)<1;
    }).length};
  })()`);
  assert.ok(bridgeView.faces > 10 && bridgeView.visible > 10, 'the actual original bridge must be present in the rendered camera');
  await ensureRacing();
  await driveUntil('stuntRacer.observe().flight.time>0.25', 10000);
  evidence.experiences.originalWorld = { info: original, tour: await observe(), bridgeView };
  await screenshot('42-original-suspension-bridge-tour.png');
}

async function verifyDrivingExperiences() {
  const chooseMode = chooseExperienceMode;
  evidence.experiences = {};
  if (['stunts', 'hard-driving'].includes(process.env.STUNT_BROWSER_FOCUS)) {
    await verifyStuntsLoop(process.env.STUNT_BROWSER_FOCUS);
    return;
  }
  if (process.env.STUNT_BROWSER_FOCUS === 'world-tour') {
    await verifyOriginalWorldScene();
    return;
  }
  const shortcuts = await evaluate(`Array.from(document.querySelectorAll(".experience-picker [data-experience]"), b => {
    const r=b.getBoundingClientRect();return {text:b.textContent,width:r.width,height:r.height,top:r.top,bottom:r.bottom};
  })`);
  assert.equal(shortcuts.length, 4);
  assert.ok(shortcuts.every(b => b.width > 44 && b.height >= 44 && b.top >= 0));
  assert.ok(shortcuts.some(b => b.text.includes("HARD DRIVIN")));
  assert.ok(shortcuts.some(b => b.text.includes("STUNTS")));
  await screenshot('30-visible-driving-experiences.png');
  await click('experience-city');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.observe().city?.mode === "free-roam"');
  const readyCity = await observe();
  const cityInfo = await evaluate('stuntRacer.diagnostics()');
  assert.ok(cityInfo.cityWorld.roads >= 15 && cityInfo.cityWorld.buildings >= 60);
  assert.ok(readyCity.city.traffic.length >= 12 && readyCity.city.pedestrians.length >= 16);
  await select('vehicle-quick', 'coupe');
  assert.equal(await evaluate('stuntRacer.diagnostics().vehicleStyle'), 'coupe');
  await screenshot('31-city-cruise-ready.png');
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  await key('KeyW', true);
  await driveUntil('stuntRacer.observe().city.drivenDistance > 65', 20000, [['KeyW']]);
  await key('KeyW', false);
  const drivenCity = await observe();
  assert.ok(drivenCity.city.traffic.some((actor, i) => actor.car.x !== readyCity.city.traffic[i].car.x ||
    actor.car.z !== readyCity.city.traffic[i].car.z), 'traffic must move in the actual native city session');
  assert.ok(drivenCity.city.pedestrians.some((actor, i) => actor.x !== readyCity.city.pedestrians[i].x ||
    actor.z !== readyCity.city.pedestrians[i].z), 'sidewalk walkers must move in the native world');
  await screenshot('32-city-traffic-driving.png');
  if ((await evaluate('stuntRacer.diagnostics().camera')) !== 'cockpit') await tap('KeyC');
  await screenshot('33-city-cockpit.png');
  await click('help-button');
  await waitFor('stuntRacer.observe().phase === "paused" && document.body.dataset.phase === "paused"');
  const pausedCity = await observe();
  await sleep(250);
  assert.deepEqual((await observe()).city, pausedCity.city, 'pause must freeze traffic, walkers and city race clocks');
  evidence.experiences.city = { info: cityInfo, ready: readyCity, driven: drivenCity };
  await chooseMode('city-circuit');
  const rivalReady = (await observe()).paceCar;
  assert.ok(rivalReady, 'street circuit must instantiate a real rival');
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  await driveUntil(`Math.hypot(stuntRacer.observe().paceCar.x-(${rivalReady.x}),stuntRacer.observe().paceCar.z-(${rivalReady.z}))>15`, 15000);
  evidence.experiences.cityCircuit = await observe();
  await screenshot('34-city-street-race.png');
  await chooseMode('city-blitz');
  await click('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  await key('KeyW', true);
  await driveUntil('stuntRacer.observe().city.checkpointsPassed >= 1', 35000, [['KeyW']]);
  await key('KeyW', false);
  evidence.experiences.cityBlitz = await observe();
  await screenshot('35-city-blitz-checkpoint.png');
  await verifyStuntsLoop('hard-driving');
  await verifyStuntsLoop();
  await verifyOriginalWorldScene();
  await chooseMode('free-roam');
  await cdp.call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  await screenshot('43-city-phone-menu.png');
  await touchTap('start-button');
  await driveUntil('stuntRacer.observe().phase === "racing"', 20000);
  const pad = await evaluate('(() => {const r=document.querySelector("#touch-steering").getBoundingClientRect();return {x:r.x+r.width*.55,y:r.y+r.height*.5,id:10};})()');
  const gas = await evaluate('(() => {const r=document.querySelector("[data-control=throttle]").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,id:20};})()');
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [pad, gas] });
  await driveUntil('stuntRacer.observe().player.speed > 5', 10000);
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  evidence.experiences.cityTouch = await observe();
  await screenshot('44-city-phone-driving.png');
  await cdp.call('Emulation.setDeviceMetricsOverride', { width: 844, height: 390, deviceScaleFactor: 1, mobile: true });
  await sleep(200);
  assert.equal(await evaluate('document.documentElement.scrollWidth'), 844);
  await screenshot('45-city-landscape-driving.png');
  await cdp.call('Emulation.clearDeviceMetricsOverride');
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: false });
  await chooseMode('time-attack');
  assert.equal((await evaluate('stuntRacer.diagnostics()')).compatibility.physicsProfile, 'authored');
}

async function headingProjection() {
  return evaluate('({player:stuntRacer.observe().player,matrix:window.__renderedViewProjection})');
}

function assertSteeringDirection(before, after, direction, label) {
  const { player, matrix } = before;
  assert.ok(Array.isArray(matrix) && matrix.length === 16, 'inspect the actual WebGL camera matrix');
  const projectHeading = yaw => {
    const x = player.x + Math.sin(yaw) * 10, y = player.y, z = player.z + Math.cos(yaw) * 10;
    return (matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12]) /
      (matrix[3] * x + matrix[7] * y + matrix[11] * z + matrix[15]);
  };
  const displacement = projectHeading(after.player.yaw) - projectHeading(player.yaw);
  assert.ok(direction * displacement > 0.001,
    `${label} must steer ${direction < 0 ? 'left' : 'right'} through the actual C# state and WebGL projection; delta=${displacement}`);
}

try {
  let port;
  for (let i = 0; i < 250; i++) {
    if (chrome.exitCode !== null) throw new Error(`Chrome exited: ${chromeErrors}`);
    try { port = (await readFile(resolve(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (port) break;
    await sleep(80);
  }
  assert.ok(port, `Chrome did not become ready: ${chromeErrors}`);
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const target = targets.find(item => item.type === 'page');
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  cdp = new Cdp(socket);
  cdp.on('Runtime.exceptionThrown', event => errors.push(event.exceptionDetails.exception?.description ?? event.exceptionDetails.text));
  cdp.on('Network.loadingFailed', event => { if (!event.canceled) failedRequests.push(event.errorText); });
  cdp.on('Network.requestWillBeSent', event => {
    if (/^https?:/.test(event.request.url) && event.request.method !== 'GET')
      unexpectedUploads.push(`${event.request.method} ${event.request.url}`);
  });
  cdp.on('Network.responseReceived', event => {
    if (event.response.status >= 400) failedRequests.push(`${event.response.status} ${event.response.url}`);
  });
  await cdp.call('Page.enable');
  await cdp.call('Runtime.enable');
  await cdp.call('Network.enable');
  await cdp.call('Page.setInterceptFileChooserDialog', { enabled: true });
  await cdp.call('Page.addScriptToEvaluateOnNewDocument', { source: `(() => {
    const names=new WeakMap(),p=WebGL2RenderingContext.prototype;
    const locate=p.getUniformLocation,upload=p.uniformMatrix4fv,scalar=p.uniform1f;
    p.getUniformLocation=function(program,name){const location=locate.call(this,program,name);if(location)names.set(location,name);return location;};
    p.uniformMatrix4fv=function(location,transpose,value,...rest){if(names.get(location)==="viewProjection")window.__renderedViewProjection=Array.from(value);return upload.call(this,location,transpose,value,...rest);};
    p.uniform1f=function(location,value){if(names.get(location)==="opacity"&&value>0&&value<1)window.__ghostOpacitySubmissions=(window.__ghostOpacitySubmissions??0)+1;return scalar.call(this,location,value);};
  })();` });
  await cdp.call('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: output });
  const response = await fetch(base);
  assert.equal(response.status, 200);
  assert.match(await response.text(), /src="\.\/main\.js"/);
  const health = await (await fetch(new URL('/healthz', base))).json();
  assert.equal(health.status, 'ready');
  await cdp.call('Page.navigate', { url: base.href });
  await waitFor('Boolean(globalThis.stuntRacer?.diagnostics().ready || document.querySelector("#error-panel:not([hidden])"))', 120000);
  const errorMessage = await evaluate('document.querySelector("#error-panel").hidden ? "" : document.querySelector("#error-message").textContent');
  assert.equal(errorMessage, '', errorMessage);
  evidence.initial = await evaluate('stuntRacer.diagnostics()');
  assert.equal(evidence.initial.selectedTrack, 'included:foundry');
  assert.equal(evidence.initial.trackChoices.filter(track => track.group === 'Included circuits').length, 3);
  assert.equal(await evaluate('document.querySelector("#track-select").disabled'), false);
  assert.equal(evidence.initial.mainAssembly, 'StuntRacer.Browser.dll');
  assert.equal(evidence.initial.runtime, '.NET 10 browser-WASM');
  assert.ok(evidence.initial.completedFrames > 0);
  if (!['flight', 'original-flight', 'experiences', 'stunts', 'hard-driving', 'world-tour', 'geographic-flight'].includes(focus)) {
  await tap('F3');
  await waitFor('!document.querySelector("#stats").hidden && /FPS.*GPU-completed/.test(document.querySelector("#stats").textContent)');
  await tap('F3');
  assert.equal(await evaluate('document.querySelector("#stats").hidden'), true);
  await screenshot('01-ready.png');
  await click('start-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  await key('KeyW', true);
  await sleep(2200);
  evidence.accelerating = await observe();
  assert.ok(evidence.accelerating.player.speed > 5, 'trusted keyboard throttle must move the C# vehicle');
  evidence.audio = await evaluate('stuntRacer.diagnostics()');
  assert.equal(evidence.audio.audioState, 'running');
  assert.ok(evidence.audio.audioRms > 0.0001, 'live game audio must contain a real signal');
  await key('Space', true);
  await sleep(700);
  evidence.boosting = await observe();
  assert.ok(evidence.boosting.player.boost < evidence.accelerating.player.boost);
  assert.equal(evidence.boosting.player.boosting, true);
  await screenshot('02-driving.png');
  await key('Space', false);
  await key('KeyW', false);
  await ensureRacing();
  await virtualPad();
  await sleep(100);
  let airborne = null, landing = null;
  const rampDeadline = Date.now() + 15000;
  while (Date.now() < rampDeadline) {
    await ensureRacing();
    const sample = await observe();
    await virtualPad({ 7: Math.max(0, Math.min(1, (29 - sample.player.speed) * 0.65)) });
    if (!sample.player.grounded && sample.player.y > 31) airborne ??= sample;
    if (airborne && sample.player.grounded) { landing = sample; break; }
    await sleep(70);
  }
  assert.ok(airborne, 'the real WASM car must naturally leave the crest');
  assert.ok(landing, 'the real WASM car must land back on the road');
  assert.ok(landing.player.damage < 0.5, 'a controlled ramp landing should remain driveable');
  evidence.naturalJump = { takeoff: airborne, landing };
  await virtualPad();
  await ensureRacing();
  await tap('KeyP');
  await waitFor('stuntRacer.observe().phase === "paused"');
  const paused = await observe();
  await sleep(400);
  const stillPaused = await observe();
  assert.equal(stillPaused.time, paused.time);
  assert.equal(stillPaused.player.boost, paused.player.boost);
  assert.equal(stillPaused.player.x, paused.player.x);
  await click('restart-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  await tap('KeyR');
  await sleep(200);
  assert.ok((await observe()).recoveries > 0);
  await virtualPad();
  await sleep(100);
  await virtualPad({ 7: 1 });
  await sleep(1300);
  evidence.gamepadThrottle = await observe();
  assert.ok(evidence.gamepadThrottle.player.speed > 3, 'virtual controller RT must reach the C# simulation');
  await virtualPad({ 6: 1 });
  await sleep(500);
  evidence.gamepadBrake = await observe();
  assert.ok(evidence.gamepadBrake.player.speed < evidence.gamepadThrottle.player.speed, 'LT must brake rather than accelerate');
  evidence.gamepadDirections = [];
  for (const direction of [-1, 1]) {
    const before = await headingProjection();
    await virtualPad({ 7: 1 }, direction * 0.6);
    await sleep(550);
    const after = await headingProjection();
    assertSteeringDirection(before, after, direction, 'Gamepad stick');
    evidence.gamepadDirections.push({ direction, before: before.player, after: after.player });
    await virtualPad();
    await sleep(650);
  }
  await evaluate('Object.defineProperty(navigator,"getGamepads",{configurable:true,value:()=>[]})');
  await sleep(100);
  assert.equal((await evaluate('stuntRacer.diagnostics()')).controller, null);
  evidence.keyboardDirections = [];
  await key('KeyW', true);
  for (const [code, direction] of [['KeyA', -1], ['KeyD', 1]]) {
    const before = await headingProjection();
    await key(code, true);
    await sleep(350);
    await key(code, false);
    const after = await headingProjection();
    assertSteeringDirection(before, after, direction, code);
    evidence.keyboardDirections.push({ code, before: before.player, after: after.player });
    await sleep(650);
  }
  await key('KeyW', false);
  const beforeCamera = await evaluate('stuntRacer.diagnostics().completedFrames');
  await tap('KeyC');
  await waitFor(`stuntRacer.diagnostics().camera === "cockpit" && stuntRacer.diagnostics().completedFrames >= ${beforeCamera + 3}`);
  await screenshot('03-cockpit.png');
  await click('capture-button');
  await waitFor('stuntRacer.diagnostics().recording === "recording"');
  await sleep(1200);
  await click('capture-button');
  await waitFor('stuntRacer.diagnostics().recording === "inactive"');
  for (let i = 0; i < 100; i++) {
    const files = await readdir(output);
    if (files.some(file => file.endsWith('.webm'))) break;
    await sleep(100);
  }
  const video = (await readdir(output)).find(file => file.endsWith('.webm'));
  assert.ok(video, 'recording must produce a downloadable WebM');
  const videoBytes = await readFile(resolve(output, video));
  assert.ok(videoBytes.length > 10000, 'recording must contain actual media data');
  evidence.video = { file: video, bytes: videoBytes.length };
  await ensureRacing();
  await tap('KeyP');
  await waitFor('stuntRacer.observe().phase === "paused"');
  await cdp.call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  await sleep(300);
  const layout = await evaluate('({width:innerWidth,canvas:document.querySelector("#game").getBoundingClientRect().width,scroll:document.documentElement.scrollWidth})');
  assert.equal(layout.width, 390);
  assert.ok(Math.abs(layout.canvas - 390) < 1);
  assert.equal(layout.scroll, 390);
  await touchTap('restart-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  assert.equal(await evaluate('document.body.dataset.touch'), 'on', 'Auto must discover a touchscreen without a controller');
  const beforeTouch = await observe();
  const pedal = await evaluate('(() => {const r=document.querySelector("[data-control=throttle]").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,width:r.width};})()');
  const padPoint = await evaluate('(() => {const r=document.querySelector("#touch-steering").getBoundingClientRect();return {x:r.x+r.width*.66,y:r.y+r.height*.6,id:1};})()');
  assert.ok(pedal.width >= 44, 'phone pedals must have usable thumb targets');
  const touchHeading = await headingProjection();
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchStart',
    touchPoints: [padPoint, { x: pedal.x, y: pedal.y, id: 2 }] });
  await sleep(650);
  evidence.touchDrive = await observe();
  assert.ok(evidence.touchDrive.player.speed > beforeTouch.player.speed + 0.5, 'the actual touch pedal must accelerate the WASM car');
  assertSteeringDirection(touchHeading, await headingProjection(), 1, 'Analog two-thumb touch steering');
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchMove',
    touchPoints: [padPoint, { x: pedal.x, y: pedal.y - 42, id: 2 }] });
  await sleep(400);
  evidence.touchTurbo = await observe();
  assert.equal(evidence.touchTurbo.player.boosting, true, 'the gas thumb must also engage turbo by sliding up');
  assert.ok(evidence.touchTurbo.player.boost < evidence.touchDrive.player.boost);
  await releaseTouch({ x: pedal.x, y: pedal.y - 42, id: 2 }, [padPoint]);
  await sleep(150);
  assert.equal((await observe()).player.boosting, false);
  assert.ok((await observe()).player.steer > 0.1, 'releasing the gas finger must not release the steering finger');
  await cdp.call('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  await sleep(150);
  assert.ok(Math.abs((await observe()).player.steer) < 0.01, 'native touch cancellation must recenter steering');
  evidence.touchDirections = [];
  for (const [control, direction] of [['left', -1], ['right', 1]]) {
    const before = await headingProjection();
    const steering = await evaluate(`(() => {const r=document.querySelector('[data-control="${control}"]').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()`);
    await cdp.call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...steering, id: 2 }] });
    await sleep(400);
    await cdp.call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    const after = await headingProjection();
    assertSteeringDirection(before, after, direction, `Touch ${control}`);
    evidence.touchDirections.push({ control, before: before.player, after: after.player });
    await sleep(650);
  }
  const recoveries = (await observe()).recoveries;
  await touchTap('touch-recover');
  await waitFor(`stuntRacer.observe().recoveries > ${recoveries}`);
  const touchCamera = await evaluate('stuntRacer.diagnostics().camera');
  await touchTap('camera-button');
  await waitFor(`stuntRacer.diagnostics().camera !== ${JSON.stringify(touchCamera)}`);
  await screenshot('04-mobile.png');
  evidence.touchLayouts = [];
  for (const [width, height] of [[320, 740], [844, 390], [1024, 768], [390, 844]]) {
    await cdp.call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
    await sleep(160);
    const layout = await evaluate(`(() => {
      const rect=selector=>{const r=document.querySelector(selector).getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:r.width,height:r.height};};
      return {width:innerWidth,height:innerHeight,scroll:document.documentElement.scrollWidth,
        steer:rect("#touch-steering"),pedals:rect(".touch-pedal-group"),gas:rect("[data-control=throttle]"),
        speed:rect(".speedometer"),fuel:rect(".telemetry")};
    })()`);
    assert.equal(layout.scroll, width, 'touch layouts must not overflow horizontally');
    assert.ok(layout.steer.left >= 0 && layout.pedals.right <= width);
    assert.ok(layout.steer.right + 8 < layout.pedals.left, 'thumb groups must remain separate');
    assert.ok(layout.gas.width >= 44 && layout.gas.height >= 44);
    assert.ok(layout.speed.bottom + 4 < layout.pedals.top, 'speed must remain above the right thumb controls');
    assert.ok(layout.fuel.bottom + 4 < layout.steer.top, 'fuel and damage must remain above the steering pad');
    evidence.touchLayouts.push(layout);
    if (width === 844) await screenshot('04b-mobile-landscape.png');
  }
  await touchTap('pause-button');
  await waitFor('stuntRacer.observe().phase === "paused"');
  await touchTap('help-button');
  await select('touch-mode', 'off');
  assert.equal(await evaluate('document.body.dataset.touch'), 'off');
  await touchTap('close-help');
  await touchTap('resume-button');
  await waitFor('stuntRacer.observe().phase === "racing"');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("#touch-controls")).display'), 'none');
  await touchTap('pause-button');
  await waitFor('stuntRacer.observe().phase === "paused"');
  await touchTap('help-button');
  await select('touch-mode', 'on');
  assert.equal(await evaluate('document.body.dataset.touch'), 'on');
  await select('touch-mode', 'auto');
  assert.equal(await evaluate('document.body.dataset.touch'), 'on');
  await touchTap('close-help');
  await cdp.call('Emulation.clearDeviceMetricsOverride');
  await cdp.call('Emulation.setTouchEmulationEnabled', { enabled: false });
  await click('resume-button');
  await tap('KeyC');
  await click('help-button');
  assert.equal(await evaluate('document.querySelector("#help-dialog").open'), true);
  await waitFor('stuntRacer.observe().phase === "paused"');
  await click('close-help');
  await click('resume-button');
  await click('fullscreen-button');
  await sleep(200);
  evidence.fullscreen = await evaluate('({active:!!document.fullscreenElement,message:document.querySelector("#toast").textContent})');
  assert.ok(evidence.fullscreen.active || /fullscreen/i.test(evidence.fullscreen.message),
    'fullscreen must either work or explain a denial');
  if (evidence.fullscreen.active) await evaluate('document.exitFullscreen()');
  await click('help-button');
  await chooseFile('import-track-button', resolve('artifacts/amiga-compat/e2e-synthetic.track.json'));
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().track.name.startsWith("Synthetic E2E strip")');
  evidence.privateImport = await evaluate('stuntRacer.diagnostics()');
  assert.equal(evidence.privateImport.compatibility.originalPhysicsAvailable, false);
  assert.equal(evidence.privateImport.compatibility.physicsProfile, 'authored');
  assert.equal(evidence.privateImport.compatibility.trackData, 'private-source-format-geometry');
  assert.match(await evaluate('document.querySelector("#track-mode").textContent'), /PROTOTYPE PHYSICS/);
  await screenshot('05-private-synthetic-import.png');
  const invalidPackage = JSON.parse(await readFile('artifacts/amiga-compat/e2e-synthetic.track.json', 'utf8'));
  invalidPackage.compatibility.dynamics = 'original';
  const invalidFile = resolve(output, 'unsupported-physics.track.json');
  await writeFile(invalidFile, JSON.stringify(invalidPackage));
  await click('help-button');
  await chooseFile('import-track-button', invalidFile);
  await waitFor('document.querySelector("#toast").textContent.startsWith("Track import rejected:")');
  assert.equal((await evaluate('stuntRacer.diagnostics()')).track.name, evidence.privateImport.track.name);
  assert.equal((await evaluate('stuntRacer.diagnostics()')).compatibility.originalPhysicsAvailable, false);
  await click('reset-track-button');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().track.name.startsWith("Foundry Heights")');
  evidence.restoredImport = await evaluate('stuntRacer.diagnostics()');
  assert.equal(evidence.restoredImport.compatibility.trackData, 'authored');
  const choices = evidence.restoredImport.trackChoices;
  if (process.env.STUNT_REQUIRE_CATALOG === '1') {
    assert.equal(choices.filter(choice => choice.available && /^(included|source):/.test(choice.id)).length, 17);
    assert.ok(choices.some(choice => choice.available && choice.id === 'flight:interceptor-bay'));
    assert.equal(choices.filter(choice => !choice.available && !choice.id.startsWith('flight:')).length, 2);
    assert.equal(choices.filter(choice => choice.available && choice.group === 'TNT').length, 7);
  }
  evidence.catalog = [];
  for (const choice of choices.filter(choice => choice.available && /^(included|source):/.test(choice.id))) {
    await select('track-select', choice.id);
    await waitFor(`stuntRacer.diagnostics().ready && stuntRacer.diagnostics().selectedTrack === ${JSON.stringify(choice.id)}`);
    const diagnostics = await evaluate('stuntRacer.diagnostics()');
    assert.equal(diagnostics.compatibility.physicsProfile, 'authored', 'track availability must not opt the player into Classic');
    assert.equal(await evaluate('document.querySelector("#session-track").value'), choice.id);
    const canonical = await evaluate('stuntRacer.track()');
    assert.ok(canonical.points.length > 3 && canonical.edges.length === canonical.points.length);
    assert.ok(canonical.points.every(point => [point.x, point.y, point.z].every(Number.isFinite)));
    evidence.catalog.push({ id: choice.id, ...diagnostics.track,
      minimumY: Math.min(...canonical.points.map(point => point.y)),
      maximumY: Math.max(...canonical.points.map(point => point.y)),
      start: canonical.points[0] });
  }
  const included = evidence.catalog.filter(track => track.id.startsWith('included:'));
  assert.equal(new Set(included.map(track => Math.round(track.length))).size, 3, 'included choices must load distinct circuits, not rename one road');
  const unavailable = choices.find(choice => !choice.available);
  if (unavailable) {
    const before = await evaluate('stuntRacer.diagnostics()');
    assert.equal(await evaluate(`document.querySelector('#track-select option[value="${unavailable.id}"]').disabled`), true);
    assert.ok(unavailable.reason);
    await select('track-select', unavailable.id);
    await waitFor('document.querySelector("#toast").textContent.startsWith("Track selection rejected:")');
    assert.equal((await evaluate('stuntRacer.diagnostics()')).selectedTrack, before.selectedTrack);
    assert.equal((await evaluate('stuntRacer.diagnostics()')).track.name, before.track.name);
  }
  await select('track-select', 'included:foundry');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().selectedTrack === "included:foundry"');
  await select('game-mode', 'checkpoint-run');
  await waitFor('stuntRacer.observe().trial?.checkpointsPassed === 0');
  assert.equal((await observe()).laps, 1);
  assert.equal((await observe()).paceCar, null);
  await startDriver();
  await click('start-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  assert.equal(await evaluate('document.querySelector("#time-heading").textContent'), 'TIME LEFT');
  const initialClock = (await observe()).trial.timeRemaining;
  await driveUntil('stuntRacer.observe().trial.checkpointsPassed >= 1');
  evidence.checkpointGate = await observe();
  assert.ok(evidence.checkpointGate.trial.lastBonus > 0);
  assert.equal(evidence.checkpointGate.trial.nextCheckpointProgress,
    2 / evidence.checkpointGate.trial.checkpointCount);
  await screenshot('05b-checkpoint-bonus.png');
  await driveUntil('stuntRacer.observe().phase === "finished"');
  await stopDriver();
  evidence.checkpointFinish = await observe();
  assert.equal(evidence.checkpointFinish.trial.success, true);
  assert.equal(evidence.checkpointFinish.trial.checkpointsPassed, evidence.checkpointFinish.trial.checkpointCount);
  assert.equal(evidence.checkpointFinish.recoveries, 0);
  assert.ok(evidence.checkpointFinish.trial.timeRemaining > 0);
  assert.ok(initialClock > 0);
  assert.equal(await evaluate('document.querySelector("#finish-title").textContent'), 'TRIAL CLEAR.');
  await screenshot('05c-checkpoint-finish.png');
  await click('help-button');
  await select('session-mode', 'time-attack');
  await waitFor('stuntRacer.observe().phase === "ready" && stuntRacer.observe().trial === null');
  assert.equal((await observe()).ghost.duration, 0);
  assert.equal((await observe()).ghost.car, null, 'a ghost must not appear without a completed clean lap');
  await startDriver();
  await click('start-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  await driveUntil('stuntRacer.observe().lap >= 2 && Boolean(stuntRacer.observe().ghost?.car)');
  await stopDriver();
  await virtualPad({ 6: 1 });
  await sleep(1200);
  evidence.bestLapGhost = await observe();
  const ghost = evidence.bestLapGhost.ghost;
  assert.equal(ghost.status, 'playing');
  assert.ok(ghost.duration > 20 && ghost.sampleCount > 300);
  assert.equal(evidence.bestLapGhost.recoveries, 0, 'the recording must be an actual clean driven lap');
  assert.ok(Math.hypot(ghost.car.x - evidence.bestLapGhost.player.x, ghost.car.z - evidence.bestLapGhost.player.z) > 2);
  assert.ok(await evaluate('(window.__ghostOpacitySubmissions??0) > 10'), 'the scene must really submit translucent ghost geometry');
  await screenshot('05d-best-lap-ghost.png');
  await click('help-button');
  await waitFor('stuntRacer.observe().phase === "paused"');
  await select('ghost-mode', 'off');
  assert.equal((await observe()).ghost.enabled, false);
  assert.equal((await observe()).ghost.car, null);
  const hiddenDraws = await evaluate('window.__ghostOpacitySubmissions');
  await sleep(200);
  assert.equal(await evaluate('window.__ghostOpacitySubmissions'), hiddenDraws, 'Ghost Off must remove the actual draws');
  await select('ghost-mode', 'on');
  assert.equal((await observe()).ghost.enabled, true);
  assert.ok((await observe()).ghost.car);
  await click('close-help');
  await virtualPad();
  await click('restart-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  assert.equal((await observe()).ghost.duration, ghost.duration, 'restart must preserve the recorded best lap');
  await click('help-button');
  await select('session-track', 'included:ridgeline');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().selectedTrack === "included:ridgeline"');
  assert.equal((await observe()).ghost.duration, 0, 'a different circuit must not inherit another track recording');
  assert.equal((await observe()).ghost.car, null);
  await select('track-select', 'included:foundry');
  await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().selectedTrack === "included:foundry"');
  await evaluate('Object.defineProperty(navigator,"getGamepads",{configurable:true,value:()=>[]})');
  await select('game-mode', 'race');
  await waitFor('stuntRacer.observe().phase === "ready" && stuntRacer.observe().race?.mode === "race"');
  const raceGrid = await observe();
  assert.ok(raceGrid.paceCar, 'head-to-head mode must create a real rival');
  assert.equal(raceGrid.race.participants, 2);
  await click('start-button');
  await waitFor('stuntRacer.observe().phase === "racing"', 12000);
  const rivalBefore = (await observe()).paceCar;
  await key('KeyW', true);
  await sleep(1800);
  await key('KeyW', false);
  evidence.rivalRace = await observe();
  assert.ok(Math.hypot(evidence.rivalRace.paceCar.x - rivalBefore.x,
    evidence.rivalRace.paceCar.z - rivalBefore.z) > 2, 'the physical rival must actually drive');
  assert.ok(evidence.rivalRace.player.speed > 3);
  assert.ok([1, 2].includes(evidence.rivalRace.race.position));
  assert.equal(await evaluate('document.querySelector("#race-position-box").hidden'), false);
  await screenshot('06-modern-rival-race.png');
  await click('help-button');
  await waitFor('stuntRacer.observe().phase === "paused"');
  const frozenRival = (await observe()).paceCar;
  await sleep(300);
  assert.deepEqual((await observe()).paceCar, frozenRival, 'pause must freeze the rival physics too');
  await select('session-mode', 'time-attack');
  await waitFor('stuntRacer.observe().phase === "ready" && stuntRacer.observe().paceCar === null');
  const sourceAvailable = await evaluate('stuntRacer.diagnostics().sourceConfigurationAvailable');
  if (process.env.STUNT_REQUIRE_SOURCE === '1') assert.equal(sourceAvailable, true, 'explicit source qualification needs configured real data');
  if (sourceAvailable) {
    await click('help-button');
    await click('source-track-button');
    await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.trackData === "amiga-derived-reference-data"');
    evidence.modernReference = await evaluate('stuntRacer.diagnostics()');
    assert.equal(evidence.modernReference.compatibility.physicsProfile, 'authored', 'reference data must not replace the liked modern physics');
    assert.match(evidence.modernReference.track.name, /Little.?Ramp/i);
    await screenshot('07-modern-little-ramp.png');
    await click('help-button');
    await select('physics-mode', 'classic');
    await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.sourcePhysicsActive === true');
    evidence.classicReference = await evaluate('stuntRacer.diagnostics()');
    assert.equal(evidence.classicReference.compatibility.physicsProfile, 'amiga-derived-source-compatible');
    assert.equal(evidence.classicReference.compatibility.originalStockParity, false);
    assert.equal(await evaluate('document.querySelector("#game-mode option[value=race]").disabled'), true);
    assert.equal(await evaluate('document.querySelector("#game-mode option[value=flight]").disabled'), false);
    await screenshot('08-classic-little-ramp.png');
    await click('start-button');
    await waitFor('stuntRacer.observe().phase === "racing"', 12000);
    const sourceBefore = await observe();
    await key('KeyW', true);
    await sleep(2400);
    const sourceDriven = await observe();
    assert.ok(Math.hypot(sourceDriven.player.x - sourceBefore.player.x,
      sourceDriven.player.z - sourceBefore.player.z) > 0.1, 'source candidate must use real input to drive on real LittleRamp data');
    assert.ok(Math.abs(sourceDriven.player.speed) > 0.01);
    const sourceHeadingBefore = await headingProjection();
    await key('KeyD', true);
    await sleep(450);
    await key('KeyD', false);
    assertSteeringDirection(sourceHeadingBefore, await headingProjection(), 1, 'Classic right steering');
    const sourceCamera = await evaluate('stuntRacer.diagnostics()');
    await tap('KeyC');
    await waitFor(`stuntRacer.diagnostics().camera !== ${JSON.stringify(sourceCamera.camera)} && stuntRacer.diagnostics().completedFrames >= ${sourceCamera.completedFrames + 3}`);
    await screenshot('09-classic-alternate-camera.png');
    await tap('KeyC');
    await key('Space', true);
    await sleep(900);
    evidence.classicDriven = await observe();
    await key('Space', false);
    await key('KeyW', false);
    assert.ok(evidence.classicDriven.tick > sourceDriven.tick);
    assert.ok(evidence.classicDriven.player.boost <= sourceDriven.player.boost);
    await click('help-button');
    await waitFor('stuntRacer.observe().phase === "paused"');
    const sourcePaused = await observe();
    await sleep(300);
    assert.equal((await observe()).tick, sourcePaused.tick);
    await select('physics-mode', 'modern');
    await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.physicsProfile === "authored"');
    assert.equal((await evaluate('stuntRacer.diagnostics()')).track.name, evidence.modernReference.track.name);
    await click('help-button');
    await click('reset-track-button');
    await waitFor('stuntRacer.diagnostics().ready && stuntRacer.diagnostics().compatibility.trackData === "authored"');
  }
  }
  if (!['flight', 'original-flight', 'geographic-flight'].includes(focus)) await verifyDrivingExperiences();
  if (!['original-flight', 'experiences', 'stunts', 'hard-driving', 'world-tour', 'geographic-flight'].includes(focus)) await verifyFlight();
  if (!['experiences', 'stunts', 'hard-driving', 'world-tour', 'geographic-flight'].includes(focus)) await verifyOriginalFlight();
  if (!focus || focus === 'geographic-flight') await verifyGeographicFlight();
  evidence.final = await evaluate('stuntRacer.diagnostics()');
  assert.deepEqual(failedRequests, [], 'all WASM and local assets must load at the actual base path');
  assert.deepEqual(unexpectedUploads, [], 'local geometry files must never be uploaded');
  assert.deepEqual(errors, [], 'no browser runtime exceptions during normal play');
  await evaluate('document.querySelector("#game").getContext("webgl2").getExtension("WEBGL_lose_context").loseContext()');
  await waitFor('!document.querySelector("#error-panel").hidden');
  assert.match(await evaluate('document.querySelector("#error-message").textContent'), /context was lost/);
  evidence.contextLoss = true;
  await writeFile(resolve(output, 'evidence.json'), JSON.stringify(evidence, null, 2));
  console.log(`Playable WASM/WebGL checks passed. GPU: ${evidence.initial.renderer}`);
  console.log(`Screenshots and recorded gameplay: ${output}`);
} catch (error) {
  if (cdp) {
    try {
      evidence.failureState = await evaluate('({state:globalThis.stuntRacer?.observe(),diagnostics:globalThis.stuntRacer?.diagnostics(),focused:document.activeElement?.id})');
    } catch (diagnosticError) { console.error(diagnosticError.message); }
  }
  await writeFile(resolve(output, 'failure.json'), JSON.stringify({ error: error.stack, errors, failedRequests, chromeErrors, evidence }, null, 2));
  console.error(`Browser failure evidence: ${output}`);
  if (cdp) {
    try { await screenshot('failure.png'); } catch (captureError) { console.error(captureError.message); }
  }
  throw error;
} finally {
  socket?.close();
  chrome.kill('SIGTERM');
  await Promise.race([new Promise(resolve => chrome.once('exit', resolve)), sleep(5000, undefined, { ref: false })]);
  if (chrome.exitCode === null && chrome.signalCode === null) {
    chrome.kill('SIGKILL');
    await new Promise(resolve => chrome.once('exit', resolve));
  }
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
