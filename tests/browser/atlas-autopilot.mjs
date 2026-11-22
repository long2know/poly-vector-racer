import assert from 'node:assert/strict';
import { setTimeout as sleep } from 'node:timers/promises';
import { atlasGrid, wrapAtlasX } from '../../src/StuntRacer.Browser/wwwroot/world-atlas.js';

const state = 'stuntRacer.observe()';
const diag = 'stuntRacer.diagnostics()';
const pilot = `${state}.flight.atlasAutopilot`;
const aircraft = `${state}.flight.aircraft`;
const ready = `${diag}.selectedTrack === 'flight:atlas' && ${state}.phase === 'ready'`;
const flying = `${state}.phase === 'racing'`;

export async function verifyAtlasAutopilot({ evaluate, observe, waitFor, click, select, tap, key, setPhone, screenshot }) {
  assert.ok(await evaluate(`${diag}.worldAtlasConfigurationAvailable`), 'Atlas needs mounted geography for this scenario');
  await select('game-mode', 'flight-atlas');
  await waitFor(ready, 60000);

  async function departure(id) {
    await select('atlas-departure', `voyage-${id}`);
    await waitFor(`${ready} && ${diag}.worldAtlas.departure.id === 'voyage-${id}'`);
    const { lon, lat } = await evaluate(`${diag}.worldAtlas.departure`);
    assert.ok(Number.isFinite(lon) && Number.isFinite(lat), `${id} must be a real authored departure`);
    return { lon, lat };
  }

  async function start() {
    await click('start-button');
    await waitFor(`${flying} && document.getElementById('atlas-ap-button').checkVisibility()`, 15000);
  }

  async function showPlan() {
    await click('atlas-ap-button');
    await waitFor("document.getElementById('atlas-ap-panel').checkVisibility()");
  }

  async function inputValues(values) {
    await evaluate(`(() => {
      for (const [id, value] of Object.entries(${JSON.stringify(values)})) document.getElementById(id).value = String(value);
    })()`);
  }

  async function startVoyage(id) {
    const origin = await departure(id);
    await start();
    await showPlan();
    assert.equal(await evaluate("document.getElementById('atlas-ap-destination').value"), id);
    await select('atlas-ap-rate', '10');
    await click('atlas-ap-engage');
    await waitFor(`${flying} && ${pilot}.status !== 'off'`);
    const plan = (await observe()).flight.atlasAutopilot;
    assert.equal(plan.mode, 'route');
    assert.equal(plan.requestedRate, 10);
    assert.ok(plan.remainingM > 7_000_000, `${id} has a full ocean-length route, not a local shortcut`);
    assert.ok(plan.fuelRateScale > 0 && plan.fuelRateScale < .1, 'Atlas endurance is represented explicitly');
    return { origin, plan };
  }

  async function returnToGarage() {
    await click('garage-button');
    await waitFor(ready);
  }

  const atlantic = await startVoyage('atlantic');
  assert.ok(atlantic.origin.lon < -70 && atlantic.origin.lat > 35 && atlantic.plan.target.x < 0);
  await click('atlas-ap-take-control');
  await waitFor(`${pilot}.status === 'off' && ${pilot}.effectiveRate === 1`);
  await returnToGarage();

  const pacific = await startVoyage('pacific');
  assert.ok(pacific.origin.lon > 130 && pacific.plan.target.x > atlasGrid.halfWidth,
    'Tokyo to San Francisco is routed east across the date line, not around the other way');
  await returnToGarage();

  await inputValues({ 'atlas-lon': 179.85, 'atlas-lat': 39, 'atlas-heading': 90 });
  await click('atlas-start');
  await waitFor(`${ready} && ${diag}.worldAtlas.departure.id === 'coordinates'`);
  await start();
  await showPlan();
  await select('atlas-ap-mode', 'route');
  await select('atlas-ap-destination', 'custom');
  await inputValues({ 'atlas-ap-lon': -178, 'atlas-ap-lat': 39, 'atlas-ap-altitude': 10500, 'atlas-ap-speed': 250 });
  await select('atlas-ap-rate', '10');
  await click('atlas-ap-engage');
  await waitFor(`${flying} && ${pilot}.effectiveRate === 10`, 420000);

  const before = (await observe()).flight;
  const wallStart = await evaluate('performance.now()');
  await sleep(900);
  const after = (await observe()).flight;
  const wall = (await evaluate('performance.now()') - wallStart) / 1000;
  const acceleration = (after.time - before.time) / wall;
  const traveled = Math.hypot(wrapAtlasX(after.aircraft.x - before.aircraft.x), after.aircraft.z - before.aircraft.z);
  assert.ok(acceleration > 7 && acceleration < 11, `10× cruise advances simulated time, measured ${acceleration}`);
  assert.ok(traveled > 1000 && traveled < 3700 && after.aircraft.fuel < before.aircraft.fuel,
    'Time compression moves the actual jet and consumes fuel without teleporting');
  assert.equal(after.atlasAutopilot.effectiveRate, 10);
  await screenshot('atlas-autopilot-10x.png');

  await tap('KeyP');
  await waitFor(`${state}.phase === 'paused'`);
  const paused = (await observe()).flight;
  await sleep(350);
  assert.deepEqual((await observe()).flight, paused, 'Pause freezes pose, flight time and fuel at 10×');
  await click('resume-button');
  await waitFor(flying);
  await waitFor(`${pilot}.effectiveRate === 10`);
  const manual = (await observe()).flight;
  await key('KeyD', true);
  try { await waitFor(`${pilot}.status === 'off' && ${pilot}.effectiveRate === 1`, 3000); }
  finally { await key('KeyD', false); }
  const handoff = (await observe()).flight;
  assert.ok(handoff.time - manual.time < 1.5 && handoff.recoveries === 0, 'Manual takeover discards accelerated remainder');
  const manualWall = await evaluate('performance.now()');
  await sleep(550);
  const manualRate = ((await observe()).flight.time - handoff.time) / ((await evaluate('performance.now()') - manualWall) / 1000);
  assert.ok(manualRate > .55 && manualRate < 1.3, `pilot is back at ordinary speed (${manualRate})`);
  assert.ok(Math.abs((await observe()).flight.aircraft.throttle - .47) < .12, 'Manual throttle returns to the stored lever');
  await returnToGarage();

  await setPhone(true);
  await start();
  await showPlan();
  const mobile = await evaluate(`(() => {
    const f=document.getElementById('atlas-ap-panel').getBoundingClientRect();
    const b=document.getElementById('atlas-ap-button').getBoundingClientRect();
    return {left:f.left,right:f.right,top:f.top,bottom:f.bottom,w:innerWidth,h:innerHeight,
      reach:document.elementFromPoint(b.x+b.width/2,b.y+b.height/2)?.id};
  })()`);
  assert.ok(mobile.left >= 0 && mobile.right <= mobile.w && mobile.top >= 0 && mobile.bottom <= mobile.h,
    `phone flight plan fits inside the playable viewport: ${JSON.stringify(mobile)}`);
  assert.equal(mobile.reach, 'atlas-ap-button');
  await screenshot('atlas-autopilot-phone-plan.png');
  await click('atlas-ap-button');
  await setPhone(false);
  await returnToGarage();
  await select('game-mode', 'flight');
  await waitFor(`${diag}.selectedTrack === 'flight:interceptor-bay' && ${state}.phase === 'ready'`);
  assert.equal(await evaluate(`${state}.flight.atlasAutopilot`), undefined, 'Atlas feature stays out of normal practice flight');
  return { atlantic, pacific, acceleration, traveled, manualRate, before, after, handoff, mobile };
}
