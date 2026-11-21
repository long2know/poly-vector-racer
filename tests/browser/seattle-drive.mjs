import assert from 'node:assert/strict';
import { setTimeout as sleep } from 'node:timers/promises';

export async function verifySeattleDrive({
  focus, evaluate, waitFor, click, select, key, observe, driveUntil, screenshot,
}) {
  const state = 'stuntRacer.observe()';
  const diagnostics = 'stuntRacer.diagnostics()';
  const geography = `${diagnostics}.geographicDrive`;
  const availability = await evaluate(`${diagnostics}.trackChoices.find(t => t.id === 'experience:seattle-drive')`);
  assert.ok(availability, 'Seattle must be a distinct course.');
  if (!availability.available) {
    assert.notEqual(focus, 'seattle-drive', 'Seattle arrival proof needs the real hosted road ZIP.');
    assert.match(availability.reason, /no Seattle roads|no World Flight terrain package|could not read/i);
    if (focus === 'seattle-drive-legacy') {
      assert.equal(await evaluate(`${diagnostics}.worldFlightConfigurationAvailable`), true, 'Test the legacy manifest, not an absent package.');
      assert.match(availability.reason, /no Seattle roads/i);
    } else if (focus === 'seattle-drive-no-package') {
      assert.match(availability.reason, /no World Flight terrain package/i);
    }
    assert.ok(await evaluate(`document.querySelector("#experience-seattle").disabled &&
      !document.querySelector("#seattle-availability").hidden`), 'Unavailable data should be explained, not silently hidden.');
    return { unavailable: availability.reason };
  }
  assert.ok(!['seattle-drive-no-package', 'seattle-drive-legacy'].includes(focus),
    'No-package and legacy tests must disable Seattle Drive.');

  if ((await observe()).phase !== 'ready') {
    await click('garage-button');
    await waitFor(`${state}.phase === 'ready'`);
  }
  await click('experience-seattle');
  await waitFor(`${diagnostics}.ready && ${geography}?.detailedGround && !document.querySelector('#start-button').disabled`, 70000);
  const origin = await observe();
  assert.equal(origin.flight, undefined, 'Seattle is an independent game car, not a flight preset.');
  assert.ok(Math.hypot(origin.player.x - 167.69, origin.player.z - 8336.27) < .02);
  await waitFor(`${geography}.roadMeshes > 0 && ${geography}.buildings >= 1200 &&
    ${geography}.landmarks.includes('Seattle Great Wheel') && ${geography}.landmarks.includes('Space Needle')`, 60000);
  const scenery = await evaluate(geography);
  assert.ok(scenery.streets > 3500, 'The map uses the bounded real Seattle street component.');
  assert.ok(await evaluate(`document.querySelector('.world-credit').checkVisibility() &&
    document.querySelector('.world-credit').textContent.includes('OpenStreetMap')`));
  await screenshot('61-seattle-drive-garage.png');

  await click('start-button');
  await waitFor(`${state}.phase === 'racing' && !document.querySelector('#seattle-destination').disabled`);
  const stops = await evaluate(`Array.from(document.querySelector('#seattle-destination').options, option => option.value).filter(Boolean)`);
  assert.ok(stops.includes('great-wheel') && stops.includes('space-needle') && stops.includes('amazon-spheres'));
  await select('seattle-destination', 'great-wheel');
  const guide = await evaluate(`${geography}.plan`);
  assert.ok(guide.points.length > 10 && guide.length > 500 && guide.length < 520);
  assert.ok(await evaluate(`document.querySelector('#map-drive-route').getAttribute('d').length > 100`));
  await click('seattle-autopilot');
  await driveUntil(`${state}.drive.autopilot && ${state}.drive.drivenDistance > 10`, 12000);
  await click('pause-button');
  await waitFor(`${state}.phase === 'paused'`);
  const paused = await observe();
  await sleep(350);
  const frozen = await observe();
  assert.equal(frozen.tick, paused.tick);
  assert.equal(frozen.drive.remaining, paused.drive.remaining);
  assert.equal(frozen.player.x, paused.player.x);
  assert.equal(frozen.player.z, paused.player.z);
  assert.equal(frozen.drive.autopilot, true);
  await click('resume-button');
  await driveUntil(`${state}.drive.autopilot && Math.abs(${state}.player.yaw - ${origin.player.yaw}) > .7 && ${state}.player.speed > 2`, 18000);
  const turn = await observe();
  assert.ok(turn.player.yaw !== origin.player.yaw, 'The WASM car must actually turn on the route.');
  await screenshot('62-seattle-drive-autopilot.png');
  await driveUntil(`${state}.drive.status === 'arrived'`, 145000);
  const arrived = await observe();
  assert.equal(arrived.drive.destinationId, 'great-wheel');
  assert.equal(arrived.drive.autopilot, false);
  assert.equal(arrived.drive.remaining, 0);
  assert.ok(arrived.drive.drivenDistance > 490 && arrived.drive.drivenDistance < 530);
  assert.ok(Math.hypot(arrived.player.x - 74.31, arrived.player.z - 8798.47) < 2.4 && Math.abs(arrived.player.speed) < .08,
    'The car reaches and stops at the actual waterfront street.');
  await sleep(550);
  const parked = await observe();
  assert.deepEqual([parked.player.x, parked.player.z], [arrived.player.x, arrived.player.z]);
  assert.match(await evaluate(`document.querySelector('#seattle-gps-status').textContent`), /Arrived.*Seattle Great Wheel/);
  await screenshot('63-seattle-drive-arrived.png');

  await select('seattle-destination', 'amazon-spheres');
  await waitFor(`${state}.drive.destinationId === 'amazon-spheres' && ${geography}.plan?.revision > ${guide.revision}`);
  const reroute = await evaluate(`${geography}.plan`);
  assert.ok(Math.hypot(reroute.points[0].x - parked.player.x, reroute.points[0].z - parked.player.z) < 4);
  assert.deepEqual(reroute.points.at(-1), { x: 455.8, z: 9687.4 });
  await click('seattle-autopilot');
  await driveUntil(`${state}.drive.autopilot && ${state}.drive.drivenDistance > ${arrived.drive.drivenDistance + 8}`, 15000);
  const before = await observe();
  await key('KeyD', true);
  await key('KeyW', true);
  let manual;
  try {
    await waitFor(`!${state}.drive.autopilot && /driver input/.test(${state}.drive.message)`, 5000);
    await waitFor(`${state}.player.yaw - ${before.player.yaw} > .12 &&
      Math.hypot(${state}.player.x - ${before.player.x}, ${state}.player.z - ${before.player.z}) > 2`, 5000);
    manual = await observe();
  } finally {
    await key('KeyW', false);
    await key('KeyD', false);
  }
  await screenshot('64-seattle-drive-manual.png');
  await click('garage-button');
  await waitFor(`${state}.phase === 'ready' && ${state}.tick === 0`);
  assert.equal((await observe()).drive.autopilot, false);
  await select('game-mode', 'time-attack');
  await waitFor(`${diagnostics}.ready && ${diagnostics}.selectedTrack === 'included:foundry' && !${state}.drive`);
  return { origin, scenery, guide, paused, turn, arrived, reroute, before, manual };
}
