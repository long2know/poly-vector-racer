import assert from 'node:assert/strict';
import { atlasGrid, wrapAtlasX } from '../../src/StuntRacer.Browser/wwwroot/world-atlas.js';

const state = 'stuntRacer.observe()';
const diagnostics = 'stuntRacer.diagnostics()';
const handling = 'flight-handling';
const radians = degrees => degrees * Math.PI / 180;
const wrap = delta => Math.atan2(Math.sin(delta), Math.cos(delta));
const course = (a, b) => Math.atan2(wrapAtlasX(b.x - a.x), b.z - a.z);
const readyFlight = track => `${state}.flight?.phase === 'ready' && ${diagnostics}.selectedTrack === '${track}'`;

export async function verifyFlightTurns({ evaluate, observe, waitFor, click, select, tap, key, setPhone, virtualPad,
  touchDown, touchUp, flightInput, screenshot }) {
  assert.ok(await evaluate(`${diagnostics}.worldAtlasConfigurationAvailable`),
    'The turnaround check needs the real Atlas data for an unambiguous air start.');
  await select('game-mode', 'flight-atlas');
  const atlasReady = readyFlight('flight:atlas');
  await waitFor(atlasReady, 60000);
  const departure = await evaluate(`(() => {
    const field = document.getElementById('atlas-departure');
    return [...field.options].find(option => option.value.endsWith('/antimeridian'))?.value;
  })()`);
  assert.ok(departure, 'The atlas must provide its roomy Aleutians air start.');
  await select('atlas-departure', departure);
  await waitFor(`${atlasReady} && ${diagnostics}.worldAtlas?.departure.id === 'antimeridian'`);

  const setting = () => evaluate(`document.getElementById('${handling}')?.value`);
  assert.equal(await setting(), 'assisted', 'Modern handling defaults to the steady bank profile.');
  const hook = on => evaluate(`(() => {
    if (!${on}) {
      cancelAnimationFrame(window.__flightTurnFrame);
      return window.__flightTurnSamples;
    }
    const capture = () => {
      const { phase, flight, terrainWaiting } = ${state};
      const { aircraft: a, time, recoveries, crashes } = flight;
      const samples = window.__flightTurnSamples;
      if (samples.at(-1)?.time !== time || samples.at(-1)?.phase !== phase)
        samples.push({ phase, time, waiting:terrainWaiting, recoveries, crashes,
          x:a.x, z:a.z, y:a.y, yaw:a.yaw, roll:a.roll, pitch:a.pitch, up:a.up.y,
          speed:a.speed, stalled:a.stalled });
      window.__flightTurnFrame = requestAnimationFrame(capture);
    };
    window.__flightTurnSamples = [];
    window.__flightTurnFrame = requestAnimationFrame(capture);
  })()`);

  async function begin() {
    await click('start-button');
    await waitFor(`['racing','paused'].includes(${state}.phase)`, 12000);
    await flightInput(null, `${state}.flight.time >= 3`);
    await hook(true);
    await waitFor('window.__flightTurnSamples.length >= 5');
  }

  async function returnToGarage() {
    await click('garage-button');
    await waitFor(atlasReady);
  }

  async function turn(name, sign, held, released) {
    await begin();
    const before = (await observe()).flight;
    let releasedAt;
    try {
      releasedAt = await flightInput(held, `(() => {
        const trail = window.__flightTurnSamples;
        const p = trail.at(-1), q = trail.at(-9);
        if (!p || !q || p.time < ${before.time + 4}) return false;
        const dx = p.x - q.x;
        const displacement = Math.atan2(dx - ${atlasGrid.width} * Math.round(dx / ${atlasGrid.width}), p.z - q.z) - ${before.aircraft.yaw};
        const turn = Math.atan2(Math.sin(displacement), Math.cos(displacement));
        return turn * ${sign} > ${radians(167)} && p.time;
      })()`, 50000);
    } finally { await released(); }
    await screenshot(`${name}-in-turn.png`);
    await flightInput(null, `${state}.flight.time > ${releasedAt + 2.4} && Math.abs(${state}.flight.aircraft.roll) < ${radians(8)}`, 6000);
    const samples = (await hook(false)).filter(sample => sample.time >= before.time && sample.phase === 'racing');
    const turning = samples.filter(sample => sample.time < releasedAt);
    const last = samples.at(-1), track = course(samples.at(-12), last);
    const duration = releasedAt - before.time;
    const finalTrack = Math.abs(wrap(track - before.aircraft.yaw));
    const altitudeRange = Math.max(...turning.map(sample => Math.abs(sample.y - before.aircraft.y)));
    const maxBank = Math.max(...turning.map(sample => Math.abs(sample.roll)));
    assert.ok(duration > 4 && duration < 40, `${name}: traveled course reversed in ${duration.toFixed(2)} s`);
    assert.ok(finalTrack > radians(165) && Math.abs(wrap(last.yaw - track)) < radians(9),
      `${name}: nose and actual travel point near the reciprocal after leveling`);
    assert.ok(maxBank >= radians(30) && maxBank < radians(78), `${name}: useful bank without rolling inverted`);
    assert.ok(altitudeRange < 220, `${name}: neutral pitch drift was ${altitudeRange.toFixed(1)} m`);
    assert.ok(samples.every(p => p.phase === 'racing' && !p.waiting && !p.recoveries && !p.crashes && !p.stalled &&
      p.up > .18 && p.speed > 65), `${name}: remained upright, energized and under the player's control`);
    await screenshot(`${name}-leveled.png`);
    await returnToGarage();
    return { before: before.aircraft, duration, finalTrack, altitudeRange, maxBank, leveled: last };
  }

  const keyboard = await turn('flight-turn-keyboard-right', 1, () => key('KeyD', true), () => key('KeyD', false));

  await setPhone(true);
  const touch = await turn('flight-turn-touch-left', -1, async () => {
    const point = await evaluate(`(() => {
      const rect = document.getElementById('touch-steering').getBoundingClientRect();
      return { x:rect.x + rect.width * .08, y:rect.y + rect.height * .5, id:42 };
    })()`);
    await touchDown(point);
  }, touchUp);
  await setPhone(false);

  await virtualPad();
  const gamepad = await turn('flight-turn-gamepad-right', 1, () => virtualPad({}, 1), () => virtualPad());
  assert.ok(Math.abs(keyboard.duration - gamepad.duration) < 1.5 &&
    Math.abs(keyboard.duration - touch.duration) < 1.5, 'Each device reaches the same fixed-step handling.');

  await click('help-button');
  await waitFor(`document.querySelector('#help-dialog').open && !document.getElementById('${handling}').disabled`);
  await select(handling, 'direct');
  await click('close-help');
  await waitFor(`!document.querySelector('#help-dialog').open`);
  await begin();
  try {
    await key('KeyD', true);
    await waitFor(`${state}.flight.aircraft.up.y < -.35 && ${state}.flight.phase === 'flying'`, 6500);
  } finally { await key('KeyD', false); }
  const direct = (await observe()).flight;
  assert.equal(await setting(), 'direct');
  assert.ok(direct.aircraft.up.y < -.35 && direct.recoveries === 0, 'Direct handling still reaches inverted flight.');
  await screenshot('flight-turn-direct-inverted.png');
  await hook(false);
  await returnToGarage();
  await click('help-button');
  await select(handling, 'assisted');
  await click('close-help');
  await waitFor(`!document.querySelector('#help-dialog').open`);

  await click('experience-flight');
  const trainerReady = readyFlight('flight:interceptor-bay');
  await waitFor(`${trainerReady} && document.getElementById('air-start-button').checkVisibility()`);
  await click('air-start-button');
  await waitFor(`['racing','paused'].includes(${state}.phase)`);
  await flightInput(null, `${state}.flight.time > 0`);
  await tap('KeyG');
  await waitFor(`!${state}.flight.aircraft.gearDown`);
  const initial = (await observe()).flight;
  try {
    await flightInput(() => key('KeyA', true),
      `${state}.flight.time >= ${initial.time + 5.5} && ${state}.flight.aircraft.roll > ${radians(35)}`, 10000);
  } finally { await key('KeyA', false); }
  await waitFor(`${state}.flight.phase === 'flying' && Math.abs(${state}.flight.aircraft.roll) < ${radians(8)}`, 6000);
  const trainer = (await observe()).flight;
  assert.ok(trainer.aircraft.y > 150 && Math.abs(wrap(trainer.aircraft.yaw - initial.aircraft.yaw)) > radians(20));
  await click('garage-button');
  await waitFor(trainerReady);
  return { keyboard, touch, gamepad, direct, trainer };
}
