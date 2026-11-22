import assert from 'node:assert/strict';
import { setTimeout as sleep } from 'node:timers/promises';

export async function verifyStuntCourses({ evaluate, observe, waitFor, driveUntil, select, click, key, tap, screenshot }) {
  const alternatives = [
    { id: 'stunts-reservoir', name: 'Reservoir Run', mode: 'stunts', group: 'Stunts courses', picker: 'track-select' },
    { id: 'hard-switchback', name: 'Switchback Test Track', mode: 'hard-driving', group: "Hard Drivin' courses", picker: 'session-track' },
  ];
  const diagnostics = 'stuntRacer.diagnostics()';
  const state = 'stuntRacer.observe()';
  const ready = `${diagnostics}.ready && ${state}.phase === "ready"`;
  const courseReady = id => `${ready} && ${diagnostics}.selectedTrack === ${JSON.stringify(id)}`;
  const page = await evaluate('performance.timeOrigin');
  const catalog = await evaluate(`${diagnostics}.trackChoices`);
  const evidence = {};

  await select('game-mode', 'race');
  await waitFor(`${state}.race?.mode === "race"`);
  for (const { id, name, mode, group, picker } of alternatives) {
    const hard = mode === 'hard-driving';
    const value = `stunt:${id}`;
    assert.deepEqual(catalog.filter(choice => choice.group === group && choice.available).map(choice => choice.id),
      [`experience:${mode}`, value], `${group} must offer both the existing and new course`);
    if (hard) {
      await select('game-mode', mode);
      await waitFor(courseReady(`experience:${mode}`));
      await click('start-button');
      await driveUntil(`${state}.phase === "racing"`, 12000);
      await click('help-button');
      await waitFor(`document.querySelector("#help-dialog").open && ${state}.phase === "paused"`);
    }
    assert.equal(await evaluate(`document.querySelector('#${picker} option[value="${value}"]').parentElement.label`), group);
    await select(picker, value);
    await waitFor(courseReady(value));
    const track = await evaluate('stuntRacer.track()');
    const initial = await observe();
    const info = await evaluate(diagnostics);
    assert.match(track.name, new RegExp(`^${name} —`));
    assert.equal(track.isSpatial, true);
    assert.equal(info.compatibility.experience, mode);
    assert.equal(info.compatibility.stuntCourseId, id);
    assert.equal(initial.stunt.mode, mode);
    assert.equal(initial.stunt.score, 0);
    assert.equal(initial.ghost.car, null);
    assert.equal(initial.trial !== null, hard);
    const { loopStart, loopEnd } = track.course;
    assert.deepEqual([track.course.id, track.course.mode], [id, mode]);
    assert.ok(loopStart > 180 && loopEnd > loopStart + 95 && loopEnd < track.course.rampEnd);
    assert.deepEqual(await evaluate('["track-select","session-track","game-mode","session-mode"].map(id=>document.getElementById(id).value)'),
      [value, value, mode, mode]);
    if (info.camera !== 'chase') await tap('KeyC');
    await click('start-button');
    await driveUntil(`${state}.phase === "racing"`, 12000);
    const launchCue = hard ? 'SPEED RUNWAY' : 'LOOP AHEAD';
    await waitFor(`document.querySelector("#race-event").textContent.startsWith(${JSON.stringify(launchCue)})`);

    const controls = [['KeyW'], ['Space']];
    const capture = condition => `(() => {
      const run=${state}, car=run.player, station=run.progress*${track.length};
      return run.phase==="racing" && ${condition} && run;
    })()`;
    let approach, inverted, cleared;
    try {
      for (const [code] of controls) await key(code, true);
      approach = await driveUntil(capture(`station > ${loopStart - 32} && station < ${loopStart - 18}`), 20000, controls);
      assert.ok(approach.player.grounded && approach.player.speed > 34 && approach.stunt.score === 0);
      await screenshot(`${id}-approach.png`);
      inverted = await driveUntil(capture(`car.grounded && Math.cos(car.pitch)*Math.cos(car.roll) < -0.8
        && station > ${loopStart} && station < ${loopEnd}`), 16000, controls);
      assert.ok(inverted.stunt.normalLoad > 0 && inverted.stunt.loopsCompleted === 0);
      assertCanonicalContact(track, inverted);
      await screenshot(`${id}-inverted.png`);
      cleared = await driveUntil(capture(`run.stunt.loopsCompleted === 1 && run.stunt.gatesPassed >= ${hard ? 2 : 1}
        && station > ${loopEnd}`), 16000, controls);
    } finally {
      for (const [code] of controls) await key(code, false);
    }
    assert.ok(cleared.player.pitch - approach.player.pitch > 5.8, 'the player must rotate through the full vertical loop');
    assert.equal(cleared.stunt.score, 1000);
    assert.equal(cleared.stunt.cleanLandings, 0);
    assert.equal(cleared.recoveries, 0);
    if (hard) {
      assert.equal(approach.trial.checkpointsPassed, 1);
      assert.equal(cleared.trial.checkpointsPassed, 2);
      assert.ok(cleared.trial.timeRemaining > 0 && cleared.trial.lastBonus > 0);
      const expected = initial.trial.timeRemaining + approach.trial.lastBonus + cleared.trial.lastBonus - cleared.time;
      assert.ok(Math.abs(cleared.trial.timeRemaining - expected) < 1e-8, 'native elapsed time and gate bonuses must drive the clock');
    }
    await screenshot(`${id}-loop-cleared.png`);
    await click('pause-button');
    await waitFor(`${state}.phase === "paused"`);
    const frozen = await observe();
    await sleep(300);
    assert.deepEqual(await observe(), frozen, 'pause must freeze the selected route score, clock and car');
    await click('restart-button');
    await waitFor(`${state}.phase === "countdown" && ${state}.stunt.score === 0`);
    assert.equal(await evaluate(`${diagnostics}.selectedTrack`), value);
    await click('garage-button');
    await waitFor(courseReady(value));
    const garage = await observe();
    assert.equal(garage.time, 0);
    assert.equal(garage.player.x, initial.player.x);
    assert.equal(garage.player.z, initial.player.z);
    assert.equal(garage.trial?.timeRemaining, initial.trial?.timeRemaining);
    evidence[id] = { info, course: track.course, approach, inverted, cleared, garage };
  }
  await select('game-mode', 'time-attack');
  await waitFor(courseReady('included:foundry'));
  assert.equal((await observe()).stunt, undefined);
  assert.equal(await evaluate('performance.timeOrigin'), page, 'course, Restart and Garage changes must stay on the same page');
  return evidence;
}

function assertCanonicalContact(track, run) {
  const s = run.progress * track.length;
  const index = track.points.findIndex(point => point.s > s);
  const a = track.points[index - 1], b = track.points[index], t = (s - a.s) / (b.s - a.s);
  const { yaw, pitch, roll, x, y, z } = run.player;
  const { sin, cos } = Math;
  const up = [-cos(yaw) * sin(roll) - sin(yaw) * sin(pitch) * cos(roll), cos(pitch) * cos(roll),
    sin(yaw) * sin(roll) - cos(yaw) * sin(pitch) * cos(roll)];
  const error = Math.hypot(...['x', 'y', 'z'].map((axis, i) => [x, y, z][i] - up[i] - a[axis] - (b[axis] - a[axis]) * t));
  assert.ok(error < 0.85, `supported inverted car must line up with the canonical spatial road, error ${error.toFixed(3)} m`);
}
