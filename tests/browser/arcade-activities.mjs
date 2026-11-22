import assert from 'node:assert/strict';
import { setTimeout as sleep } from 'node:timers/promises';

export async function verifyArcadeActivities({
  evaluate, waitFor, click, touchTap, tap, key, select, observe, setPhone, verifyTouchTargets,
  virtualPad, driveUntil, flightInput, screenshot,
}) {
  const state = 'stuntRacer.observe()';
  const duel = `${state}.flight.duel`;
  const yard = `${state}.arcade`;
  const ready = mode => waitFor(`stuntRacer.diagnostics().ready && ${state}.phase === "ready" &&
    stuntRacer.diagnostics().compatibility.experience === ${JSON.stringify(mode)}`);
  const content = id => evaluate(`document.getElementById(${JSON.stringify(id)}).textContent`);
  const scannerReady = `${duel}.inRange && ${duel}.nextPass && ${duel}.cooldownSeconds === 0`;

  await click('garage-button');
  for (const [mode, track] of [['flight-duel', 'flight:duel'], ['arcade-yard', 'experience:arcade-yard']]) {
    assert.ok(await evaluate(`['game-mode','session-mode'].every(id =>
      [...document.getElementById(id).options].some(option => option.value === '${mode}' && !option.disabled))`));
    assert.ok(await evaluate(`['track-select','session-track'].every(id =>
      [...document.getElementById(id).options].some(option => option.value === '${track}' && !option.disabled))`));
  }
  await setPhone(true);
  const garageLayout = await verifyTouchTargets(['experience-air-tag', 'experience-block-rush', 'start-button']);
  await touchTap('experience-air-tag');
  await ready('flight-duel');
  const duelReady = await observe();
  assert.ok(duelReady.flight.duel.timeRemaining > 0 && duelReady.flight.duel.scoreGoal > 1);
  assert.equal(duelReady.flight.training, undefined);
  assert.equal(await evaluate('Boolean(stuntRacer.world().geography || stuntRacer.world().sourceWorld)'), false);
  assert.equal(await evaluate('document.querySelector("#track-select").value'), 'flight:duel');
  await touchTap('help-button');
  assert.match(await content('help-dialog'), /Tag on three separate close passes/);
  await touchTap('close-help');
  await setPhone(false);
  await click('start-button');
  await driveUntil(scannerReady, 10000);
  const near = (await observe()).flight;
  assert.ok(!near.aircraft.onGround && near.aircraft.z > -3960);
  assert.notDeepEqual(near.duel.rival.position, duelReady.flight.duel.rival.position);
  assert.ok(near.duel.timeRemaining < duelReady.flight.duel.timeRemaining);
  assert.match(await content('duel-rival'), /Kite glider · \d+ m/);
  assert.match(await content('training-range'), /In range.*SCAN now/);
  await flightInput(() => tap('KeyT'), `${duel}.score === 1`);
  const keyboardTag = (await observe()).flight.duel;
  assert.match(await content('training-score'), /1 \/ 3 TAGS/);
  await tap('KeyP');
  await waitFor(`${state}.phase === "paused"`);
  const pausedFlight = (await observe()).flight;
  await tap('KeyT');
  await sleep(220);
  assert.deepEqual((await observe()).flight, pausedFlight, 'pause freezes the rival, clock and scan');
  await setPhone(true);
  await touchTap('restart-button');
  await driveUntil(scannerReady, 10000);
  assert.equal((await observe()).flight.duel.score, 0, 'restarting the duel resets tags');
  const scanLayout = await verifyTouchTargets(['training-scan-button', 'flight-view-reset', 'pause-button']);
  assert.equal(await evaluate('document.querySelector("#training-scan-button").getAttribute("aria-label")'), 'Scan nearby rival glider');
  await touchTap('training-scan-button');
  await waitFor(`${duel}.score === 1`);
  assert.equal(await evaluate('document.activeElement.id'), 'game', 'touch scan gives flight keys back to the canvas');
  await screenshot('80-air-tag-duel-touch.png');
  await touchTap('garage-button');
  await ready('flight-duel');
  assert.deepEqual((await observe()).flight, duelReady.flight);
  await setPhone(false);

  await virtualPad();
  await click('start-button');
  await driveUntil(scannerReady, 10000);
  await flightInput(() => virtualPad({ 11: 1 }), `${duel}.score === 1`);
  await virtualPad();
  const beforeBank = (await observe()).flight.aircraft;
  await key('KeyD', true);
  try {
    await driveUntil(`Math.abs(${state}.flight.aircraft.roll - ${beforeBank.roll}) > .3`, 5000, [['KeyD']]);
  } finally { await key('KeyD', false); }
  await click('garage-button');
  await ready('flight-duel');

  await select('track-select', 'flight:interceptor-bay');
  await ready('flight');
  await click('air-start-button');
  await driveUntil(`${state}.phase === "racing"`, 10000);
  await tap('KeyT');
  await virtualPad({ 11: 1 });
  await sleep(150);
  const freeFlight = (await observe()).flight;
  assert.equal(freeFlight.duel, undefined);
  assert.equal(freeFlight.training, undefined);
  assert.equal(await evaluate('document.querySelector("#training-scan-button").hidden'), true);
  await virtualPad();

  await click('help-button');
  await select('session-track', 'experience:arcade-yard');
  await ready('arcade-yard');
  const yardReady = await observe();
  assert.equal(await evaluate('stuntRacer.world().name'), 'Neon Yard');
  assert.equal(await evaluate('document.querySelector("#game-mode").value'), 'arcade-yard');
  assert.equal(await evaluate('document.querySelector("#experience-block-rush").getAttribute("aria-pressed")'), 'true');
  assert.match(await content('start-label'), /START BLOCK RUSH/);
  assert.ok(yardReady.arcade.props.every(prop => !prop.hit));
  assert.equal(new Set(yardReady.arcade.props.map(prop => prop.id)).size, yardReady.arcade.propCount);
  await click('start-button');
  await driveUntil(`${state}.phase === "racing"`, 12000);
  await key('KeyW', true);
  try {
    await driveUntil(`${yard}.propsHit === 1`, 10000, [['KeyW']]);
    await tap('KeyP');
    await waitFor(`${state}.phase === "paused"`);
  } finally { await key('KeyW', false); }
  const scored = await observe();
  const firstCube = yardReady.arcade.props.filter(prop => prop.x === yardReady.player.x && prop.z > yardReady.player.z)
    .sort((a, b) => a.z - b.z)[0];
  const popped = scored.arcade.props.filter(prop => prop.hit && prop.hitTick > 0);
  assert.deepEqual(popped.map(prop => prop.id), [firstCube.id]);
  const bumperGap = firstCube.z - scored.player.z;
  assert.ok(scored.player.z - yardReady.player.z > 10 && bumperGap >= 0 && bumperGap <= 3.5,
    'the advancing car popped the first cube at bumper range, before its center reached the cube');
  assert.equal(scored.arcade.score, 100);
  assert.ok(scored.arcade.timeRemaining < yardReady.arcade.timeRemaining);
  assert.match(await content('lap-heading'), /BLOCK RUSH SCORE/);
  assert.equal(await content('lap'), '100');
  assert.match(await content('time-heading'), /TIME LEFT/);
  await sleep(220);
  assert.deepEqual(await observe(), scored, 'pause freezes the Neon Yard car, foam props and clock');
  // Follow the native winning trace through the real gamepad path. Sampling inputs per frame
  // avoids overshooting the branch while the test process polls for a keyboard turn.
  await virtualPad();
  await evaluate(`(() => {
    const pad = window.__qualificationPad;
    const idle = pad.buttons[7], throttle = { pressed: true, value: 1 };
    Object.defineProperty(navigator, 'getGamepads', { configurable: true, value: () => {
      const sample = ${state};
      const active = sample.arcade && (sample.phase === 'racing' ||
        sample.phase === 'countdown' && sample.tick >= 30);
      pad.buttons[7] = active ? throttle : idle;
      pad.axes[0] = active && sample.arcade.elapsed >= 2.85 ? 1 : 0;
      return [pad];
    } });
  })()`);
  try {
    await click('restart-button');
    await waitFor(`${state}.phase === "countdown" && ${yard}.score === 0`);
    assert.deepEqual((await observe()).arcade, yardReady.arcade);
    await driveUntil(`${yard}.success === true`, 15000);
  } finally {
    await virtualPad();
  }
  const yardClear = await observe();
  assert.equal(yardClear.phase, 'finished');
  assert.ok(yardClear.arcade.score >= yardClear.arcade.scoreGoal && yardClear.arcade.timeRemaining > 0);
  assert.ok(yardClear.arcade.props.some(prop => prop.hit && prop.x > 5), 'a steered branch was actually popped');
  assert.match(await content('finish-title'), /YARD CLEAR/);
  await screenshot('81-block-rush-clear.png');
  await click('garage-finish');
  await ready('arcade-yard');
  assert.deepEqual((await observe()).arcade, yardReady.arcade);
  await select('game-mode', 'time-attack');
  await ready('racing');
  assert.equal((await observe()).arcade, undefined);
  return { keyboardTag, garageLayout, scanLayout, yardFirstHit: scored.arcade.score, yardClear: yardClear.arcade };
}
