import assert from 'node:assert/strict';
import { setTimeout as sleep } from 'node:timers/promises';
import { atlasGrid, wrapAtlasX } from '../../src/StuntRacer.Browser/wwwroot/world-atlas.js';

const state = 'stuntRacer.observe()';
const diag = 'stuntRacer.diagnostics()';
const pilot = `${state}.flight.atlasAutopilot`;
const aircraft = `${state}.flight.aircraft`;
const ready = `${diag}.selectedTrack === 'flight:atlas' && ${state}.phase === 'ready'`;
const flying = `${state}.phase === 'racing'`;
const flightSample = `({ flight: ${state}.flight, now: performance.now() })`;

export async function verifyAtlasAutopilot({ evaluate, observe, waitFor, click, touchTap, select, tap, key, setPhone, screenshot }) {
  assert.ok(await evaluate(`${diag}.worldAtlasConfigurationAvailable`), 'Atlas needs mounted geography for this scenario');
  await select('game-mode', 'flight-atlas');
  await waitFor(ready, 60000);

  async function departure(id, category = 'voyage') {
    const preset = `${category}-${id}`;
    await select('atlas-departure', preset);
    await waitFor(`${ready} && ${diag}.worldAtlas.departure.id === '${preset}'`);
    const { lon, lat } = await evaluate(`${diag}.worldAtlas.departure`);
    assert.ok(Number.isFinite(lon) && Number.isFinite(lat), `${id} must be a real authored departure`);
    return { lon, lat };
  }

  async function start(activate = click) {
    await activate('start-button');
    await waitFor(`${flying} && document.getElementById('atlas-ap-button').checkVisibility()`, 15000);
  }

  async function showPlan(activate = click) {
    await activate('atlas-ap-button');
    await waitFor("document.getElementById('atlas-ap-panel').checkVisibility()");
  }

  async function inputValues(values) {
    await evaluate(`(() => {
      for (const [id, value] of Object.entries(${JSON.stringify(values)})) document.getElementById(id).value = String(value);
    })()`);
  }

  async function measureFlight(milliseconds = 900) {
    const { flight: before, now: started } = await evaluate(flightSample);
    await sleep(milliseconds);
    const { flight: after, now: ended } = await evaluate(flightSample);
    const wall = (ended - started) / 1000;
    const acceleration = (after.time - before.time) / wall;
    const traveled = Math.hypot(wrapAtlasX(after.aircraft.x - before.aircraft.x), after.aircraft.z - before.aircraft.z);
    const gpsProgress = before.atlasAutopilot.remainingM - after.atlasAutopilot.remainingM;
    return { before, after, started, wall, acceleration, traveled, gpsProgress };
  }

  async function startVoyage(id) {
    const origin = await departure(id);
    assert.equal(await evaluate(`${diag}.worldAircraft.id`), 'f18');
    await start();
    await showPlan();
    assert.equal(await evaluate("document.getElementById('atlas-ap-destination').value"), id);
    await select('atlas-ap-rate', '10');
    const engagedAt = await evaluate('performance.now()');
    await click('atlas-ap-engage');
    await waitFor(`${flying} && ${pilot}.status !== 'off'`);
    const launch = (await observe()).flight;
    const plan = launch.atlasAutopilot;
    assert.equal(plan.mode, 'route');
    assert.equal(plan.requestedRate, 10);
    assert.equal(plan.effectiveRate, 1, 'The low-altitude departure is still acquiring a safe climb');
    assert.equal(plan.playEtaSeconds, undefined, 'Acquisition is not an arrival prediction');
    assert.ok(launch.aircraft.y < 2500, 'Use the actual low-altitude unscripted departure');
    assert.ok(plan.remainingM > 7_000_000, `${id} has a full ocean-length route, not a local shortcut`);
    assert.ok(plan.fuelRateScale > 0 && plan.fuelRateScale < .1, 'Atlas endurance is represented explicitly');
    await waitFor(`${flying} && ${pilot}.status === 'climbing' && ${pilot}.effectiveRate === 10`, 29000);
    const early = await measureFlight();
    const activationSeconds = (early.started - engagedAt) / 1000;
    assert.ok(activationSeconds < 30 && early.before.aircraft.y < 6000 && early.after.aircraft.y > early.before.aircraft.y,
      `${id} accelerates in a real climb within 30 wall seconds (${activationSeconds.toFixed(2)} s)`);
    assert.ok(early.acceleration > 7 && early.acceleration < 11,
      `${id} advances the flight clock faster before cruise, measured ${early.acceleration}`);
    assert.ok(early.traveled > 1000 && early.traveled < 3700 && Math.abs(early.gpsProgress - early.traveled) < 30 &&
      early.after.aircraft.fuel < early.before.aircraft.fuel && early.after.crashes === 0,
      `${id} makes reachable GPS and aircraft progress (${early.gpsProgress.toFixed(1)} m) before cruise`);
    assert.ok(early.after.atlasAutopilot.playEtaSeconds > 3300 && early.after.atlasAutopilot.playEtaSeconds < 6500,
      `${id} forecasts close to an hour or longer even at 10×`);
    assert.match(await evaluate("document.getElementById('atlas-ap-status').textContent"), /Climbing GPS · 10× requested/);
    return { origin, plan, early: { ...early, activationSeconds } };
  }

  async function returnToGarage() {
    await click('garage-button');
    await waitFor(ready);
  }

  async function phonePlan() {
    const layout = await evaluate(`(() => {
      const f=document.getElementById('atlas-ap-panel').getBoundingClientRect();
      const b=document.getElementById('atlas-ap-button').getBoundingClientRect();
      return {left:f.left,right:f.right,top:f.top,bottom:f.bottom,w:innerWidth,h:innerHeight,
        reach:document.elementFromPoint(b.x+b.width/2,b.y+b.height/2)?.id,
        etaVisible:document.getElementById('atlas-ap-eta').checkVisibility()};
    })()`);
    assert.ok(layout.left >= 0 && layout.right <= layout.w && layout.top >= 0 && layout.bottom <= layout.h,
      `phone flight plan fits inside the playable viewport: ${JSON.stringify(layout)}`);
    assert.equal(layout.reach, 'atlas-ap-button');
    return layout;
  }

  const atlantic = await startVoyage('atlantic');
  assert.ok(atlantic.origin.lon < -70 && atlantic.origin.lat > 35 && atlantic.plan.target.x < 0);
  await click('atlas-ap-take-control');
  await waitFor(`${pilot}.status === 'off' && ${pilot}.effectiveRate === 1`);
  await returnToGarage();

  const pacific = await startVoyage('pacific');
  assert.ok(pacific.origin.lon > 130 && pacific.plan.target.x > atlasGrid.halfWidth,
    'Tokyo to San Francisco is routed east across the date line, not around the other way');
  await showPlan();
  pacific.rateChanges = [];
  for (const rate of [5, 2, 10]) {
    await select('atlas-ap-rate', String(rate));
    await waitFor(`${pilot}.requestedRate === ${rate} && ${pilot}.effectiveRate === ${rate}`, 3000);
    const change = await measureFlight(500);
    assert.ok(change.acceleration > rate * .7 && change.acceleration < rate * 1.15,
      `Changing the selector applies ${rate}× without re-engaging (measured ${change.acceleration})`);
    assert.deepEqual(change.after.atlasAutopilot.target, pacific.plan.target, 'Rate changes preserve the same GPS target');
    assert.ok(change.gpsProgress > 0 && change.after.atlasAutopilot.status === 'climbing');
    pacific.rateChanges.push({ requested: rate, measured: change.acceleration, gpsProgress: change.gpsProgress });
  }
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
  await waitFor(`${flying} && ${pilot}.status === 'cruise' && ${pilot}.effectiveRate === 10`, 90000);

  const { before, after, acceleration, traveled } = await measureFlight();
  assert.ok(acceleration > 7 && acceleration < 11, `10× cruise advances simulated time, measured ${acceleration}`);
  assert.ok(traveled > 1000 && traveled < 3700 && after.aircraft.fuel < before.aircraft.fuel,
    'Time compression moves the actual jet and consumes fuel without teleporting');
  assert.equal(after.atlasAutopilot.effectiveRate, 10);
  await screenshot('atlas-autopilot-10x.png');

  await tap('KeyP');
  await waitFor(`${state}.phase === 'paused'`);
  const paused = (await observe()).flight;
  assert.equal(paused.atlasAutopilot.playEtaSeconds, undefined, 'Pause does not show an active ETA');
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
  assert.equal(handoff.atlasAutopilot.playEtaSeconds, undefined, 'Manual flight has no active GPS ETA');
  assert.ok(handoff.time - manual.time < 1.5 && handoff.recoveries === 0, 'Manual takeover discards accelerated remainder');
  const manualWall = await evaluate('performance.now()');
  await sleep(550);
  const manualRate = ((await observe()).flight.time - handoff.time) / ((await evaluate('performance.now()') - manualWall) / 1000);
  assert.ok(manualRate > .55 && manualRate < 1.3, `pilot is back at ordinary speed (${manualRate})`);
  assert.ok(Math.abs((await observe()).flight.aircraft.throttle - .47) < .12, 'Manual throttle returns to the stored lever');
  await returnToGarage();

  const scenic = { origin: await departure('california-coast', 'scenic') };
  assert.equal(await evaluate('document.getElementById("atlas-departure").selectedOptions[0].parentElement.label'), 'Scenic flights');
  await setPhone(true);
  await start(touchTap);
  scenic.manual = (await observe()).flight;
  assert.ok(scenic.manual.aircraft.y < 2000 && scenic.manual.atlasAutopilot.status === 'off' &&
    scenic.manual.atlasAutopilot.target == null, 'Scenic departure starts low and manual');
  await showPlan(touchTap);
  const preselection = await evaluate(`(() => {
    const target = document.getElementById('atlas-ap-destination').selectedOptions[0];
    return {id: target.value, label: target.textContent, group: target.parentElement.label,
      guidance: document.getElementById('atlas-ap-mode').value, rate: document.getElementById('atlas-ap-rate').value};
  })()`);
  assert.deepEqual(preselection, { id: 'california-coast', label: 'Golden Gate / California Coast',
    group: 'Scenic flights', guidance: 'route', rate: '1' }, 'Only the matched GPS target is preselected');
  const mobile = await phonePlan();
  assert.equal(mobile.etaVisible, false, 'A preselected but inactive route has no ETA');
  await screenshot('atlas-autopilot-phone-plan.png');
  const scenicStartedAt = await evaluate('performance.now()');
  await touchTap('atlas-ap-engage');
  await waitFor(`${pilot}.mode === 'route'`);
  scenic.engaged = (await observe()).flight;
  const route = scenic.engaged.atlasAutopilot;
  assert.ok(route.remainingM > 38_000 && route.remainingM < 41_000 && route.effectiveRate === 1);
  assert.ok(Math.abs(route.target.x - -122.4783 * atlasGrid.halfWidth / 180) < 1 &&
    Math.abs(route.target.z - 37.8199 * atlasGrid.halfHeight / 90) < 1,
    'The actual native target is the Golden Gate, not the nearby SFO preset');
  await waitFor(`${pilot}.effectiveRate === 1 && ${pilot}.playEtaSeconds > 0 &&
    document.getElementById('atlas-ap-eta').textContent.startsWith('Play ETA ~')`, 33000);
  scenic.normal = (await observe()).flight;
  assert.equal(await evaluate("document.getElementById('atlas-ap-eta').closest('[aria-live], [role=status]')"), null,
    'Changing ETA is not announced live');
  await showPlan(touchTap);
  scenic.liveMobile = await phonePlan();
  assert.ok(scenic.liveMobile.etaVisible, 'Phone flight plan still fits when ETA is displayed');
  await select('atlas-ap-rate', '10');
  await waitFor(`${pilot}.requestedRate === 10 && ${pilot}.effectiveRate > 1 && ${pilot}.playEtaSeconds > 0`);
  await touchTap('atlas-ap-button');
  scenic.fast = await measureFlight();
  assert.ok(scenic.fast.gpsProgress > 200 && scenic.fast.after.atlasAutopilot.playEtaSeconds > 0 &&
    scenic.fast.after.atlasAutopilot.playEtaSeconds < scenic.normal.atlasAutopilot.playEtaSeconds,
    'Compression makes real approach progress and reduces play ETA');
  await waitFor(`${pilot}.effectiveRate === 1 && ${pilot}.remainingM < 25000 && ${pilot}.playEtaSeconds > 0`, 45000);
  scenic.approach = (await observe()).flight;
  assert.ok(scenic.approach.atlasAutopilot.playEtaSeconds > 60, 'Final approach still needs roughly a real minute or more');
  assert.match(await evaluate("document.getElementById('atlas-ap-status').textContent"), /Approach GPS · 10× requested \/ 1× effective/);
  await screenshot('atlas-scenic-phone-approach.png');
  await waitFor(`${flying} && ${pilot}.status === 'arrived'`, 135000);
  scenic.arrived = (await observe()).flight;
  scenic.captureWallSeconds = ((await evaluate('performance.now()')) - scenicStartedAt) / 1000;
  const { aircraft: jet, atlasAutopilot: arrival } = scenic.arrived;
  assert.ok(Math.hypot(wrapAtlasX(jet.x - arrival.target.x), jet.z - arrival.target.z) <= 5000 &&
    !jet.onGround && arrival.effectiveRate === 1 && arrival.playEtaSeconds === undefined &&
    scenic.arrived.crashes === 0 && scenic.arrived.landings === 0, 'Scenic AP reaches the real 1× capture circle and stays airborne');
  await setPhone(false);
  await returnToGarage();
  await select('game-mode', 'flight');
  await waitFor(`${diag}.selectedTrack === 'flight:interceptor-bay' && ${state}.phase === 'ready'`);
  assert.equal(await evaluate(`${state}.flight.atlasAutopilot`), undefined, 'Atlas feature stays out of normal practice flight');
  return { atlantic, pacific, acceleration, traveled, manualRate, before, after, handoff, mobile, scenic };
}
