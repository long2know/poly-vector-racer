import assert from 'node:assert/strict';
import { setTimeout as sleep } from 'node:timers/promises';
import { pose, transform } from '../../src/StuntRacer.Browser/wwwroot/engine/math.js';

export async function verifySeattleDrive({
  focus, evaluate, waitFor, click, select, key, observe, driveUntil, screenshot, virtualPad, setPhone,
}) {
  const state = 'stuntRacer.observe()';
  const diagnostics = 'stuntRacer.diagnostics()';
  const geography = `${diagnostics}.geographicDrive`;
  const pedestrians = `${geography}.pedestrians`;
  const camera = `${geography}.camera`;
  const walkers = () => evaluate(`({ ...${pedestrians}, player: ${state}.player })`);
  const garageOverlays = ['#menu-title', '.circuit-card', '.topbar'];
  const driveOverlays = ['#seattle-gps', '#seattle-guidance', '#driving-guidance', '.minimap', '#pause-button'];
  const separated = (a, b, gap = 0) =>
    a.right + gap < b.left || a.left - gap > b.right || a.bottom + gap < b.top || a.top - gap > b.bottom;
  const verifyCarView = async (mode, overlaySelectors = []) => {
    await waitFor(`${camera}?.mode === '${mode}' && Boolean(window.__renderedViewProjection)`);
    const view = await evaluate(`(() => {
      const rect = element => element.getBoundingClientRect().toJSON();
      const overlays = ${JSON.stringify(overlaySelectors)}.flatMap(selector => {
        const element = document.querySelector(selector);
        return element?.checkVisibility() ? [{ selector, rect: rect(element) }] : [];
      });
      return { camera: ${camera}, player: ${state}.player, matrix: window.__renderedViewProjection,
        viewport: rect(document.querySelector('#game')), overlays };
    })()`);
    const { matrix: m, viewport, overlays, player: { x, y, z, yaw, pitch, roll } } = view;
    assert.equal(view.camera.clear, true, `${mode} must have clear sightlines to the vehicle.`);
    const originX = Math.round(x / 2048) * 2048, originZ = Math.round(z / 2048) * 2048;
    const body = pose([x - originX, y, z - originZ], yaw, pitch, roll);
    const points = [];
    for (const side of [-1.34, 1.34]) for (const up of [-1, 1.6]) for (const end of [-2.14, 2.14]) {
      const [a, b, c] = transform(body, [side, up, end]);
      const w = m[3] * a + m[7] * b + m[11] * c + m[15];
      assert.ok(w > .12, `${mode} must not put the car past the camera.`);
      const px = (m[0] * a + m[4] * b + m[8] * c + m[12]) / w;
      const py = (m[1] * a + m[5] * b + m[9] * c + m[13]) / w;
      points.push([viewport.left + (px + 1) / 2 * viewport.width, viewport.top + (1 - py) / 2 * viewport.height]);
    }
    const left = Math.min(...points.map(p => p[0])), right = Math.max(...points.map(p => p[0]));
    const top = Math.min(...points.map(p => p[1])), bottom = Math.max(...points.map(p => p[1]));
    const bounds = { left, right, top, bottom };
    // The body envelope is wider than the modeled silhouette in portrait chase.
    const portraitChase = mode === 'chase' && viewport.width === 360;
    assert.ok((portraitChase || left > viewport.left + 2 && right < viewport.right - 2) && top > viewport.top + 2 &&
      bottom < viewport.bottom - 2 && right - left > 50 && bottom - top > 35,
    `The rendered ${mode} projection must frame the full front and rear: ${JSON.stringify(bounds)}`);
    const gap = viewport.width === 360 ? 6 : 0;
    for (const { selector, rect } of overlays)
      assert.ok(separated(bounds, rect, gap),
        `The rendered ${mode} car must be clear of ${selector}: ${JSON.stringify({ bounds, overlay: rect })}`);
    return { camera: view.camera, bounds, viewport };
  };
  const verifySeattleGps = async active => {
    const { submittedFrames } = await driveUntil(
      `${state}.phase === 'racing' && { submittedFrames: ${diagnostics}.submittedFrames }`, 8000);
    // Resume can update native phase before the next HUD frame.
    await waitFor(`${state}.phase === 'racing' && ${diagnostics}.submittedFrames > ${submittedFrames}`);
    const view = await verifyCarView('chase', driveOverlays);
    const layout = await evaluate(`(() => {
      const panel = document.querySelector('#seattle-gps');
      const guide = document.querySelector('#seattle-guidance');
      const arrow = document.querySelector('#seattle-route-arrow');
      const speed = document.querySelector('#seattle-cruise-speed');
      const rect = selector => document.querySelector(selector).getBoundingClientRect().toJSON();
      const speedRect = speed.getBoundingClientRect().toJSON();
      return { panelVisible: panel.checkVisibility(), panel: rect('#seattle-gps'), pause: rect('#pause-button'),
        map: rect('.minimap'), centeredGuide: document.querySelector('#driving-guidance').checkVisibility(),
        guide: guide.checkVisibility(), guideInPanel: panel.contains(guide) && guide.contains(arrow),
        arrow: arrow.checkVisibility() && arrow.style.transform,
        distance: document.querySelector('#seattle-route-distance').textContent,
        speed: { rect: speedRect, label: speed.labels[0]?.textContent, enabled: !speed.disabled,
          reachable: document.elementFromPoint(speedRect.x + speedRect.width / 2,
            speedRect.y + speedRect.height / 2) === speed } };
    })()`);
    assert.ok(layout.panelVisible && !layout.centeredGuide, 'Seattle only uses its corner GPS panel.');
    assert.equal(layout.guide, active, 'The waypoint row is visible only for an active route.');
    if (active) {
      assert.ok(layout.guideInPanel);
      assert.match(layout.arrow, /^rotate\(/);
      assert.match(layout.distance, /^[\d,]+ M VIA MAPPED STREETS$/);
    }
    assert.ok(separated(layout.panel, layout.pause) && separated(layout.panel, layout.map),
      'The GPS panel must stay clear of the pause control and minimap.');
    if (view.viewport.width === 360) {
      const { rect, enabled, label, reachable } = layout.speed;
      assert.ok(enabled && reachable && rect.width >= 44 && rect.height >= 38 &&
        rect.left >= 0 && rect.top >= 0 && rect.right <= 360 && rect.bottom <= 800,
      `Phone Autopilot Speed must be reachable: ${JSON.stringify(layout.speed)}`);
      assert.equal(label, 'AUTOPILOT SPEED');
    }
    return { ...view, gps: layout };
  };
  const resizePhone = async enabled => {
    const before = await evaluate(`${diagnostics}.submittedFrames`);
    await setPhone(enabled);
    const size = enabled ? `innerWidth === 360 && ${diagnostics}.resolution.width === 360`
      : `innerWidth !== 360 && ${diagnostics}.resolution.width > 360`;
    await waitFor(`${size} && ${diagnostics}.submittedFrames > ${before}`);
  };
  const verifyClearWalkers = ({ visible, poses, player }) => {
    assert.equal(visible, poses.length);
    for (const pose of poses) {
      assert.ok([pose.x, pose.y, pose.z, pose.phase, pose.yaw].every(Number.isFinite));
      assert.ok(Math.hypot(pose.x - player.x, pose.z - player.z) > 3.34,
        'Rendered pedestrians make room for the game car.');
    }
  };
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
  const { spawn, streets } = await evaluate('stuntRacer.world()');
  assert.equal(origin.flight, undefined, 'Seattle is an independent game car, not a flight preset.');
  assert.ok(Math.hypot(origin.player.x - spawn.x, origin.player.z - spawn.z) < .02,
    'The physical car starts at the published Seattle spawn.');
  const spawnShift = Math.hypot(spawn.x - 167.69, spawn.z - 8336.27);
  assert.ok(spawnShift >= 7 && spawnShift < 7.3 && streets.some(([ax, az, bx, bz]) =>
    (ax === spawn.x && az === spawn.z) || (bx === spawn.x && bz === spawn.z)),
  'The spawn is a connected road node beyond the old building-edge position.');
  await waitFor(`${geography}.roadMeshes > 0 && ${geography}.buildings >= 1200 &&
    ${geography}.landmarks.includes('Seattle Great Wheel') && ${geography}.landmarks.includes('Space Needle')`, 60000);
  const scenery = await evaluate(geography);
  assert.ok(scenery.streets > 3500, 'The map uses the bounded real Seattle street component.');
  assert.ok(await evaluate(`document.querySelector('.world-credit').checkVisibility() &&
    document.querySelector('.world-credit').textContent.includes('OpenStreetMap')`));
  await waitFor(`${pedestrians}.visible >= 2`, 45000);
  const garageWalkers = await walkers();
  verifyClearWalkers(garageWalkers);
  assert.equal(garageWalkers.time, 0);
  const garageView = await verifyCarView('orbit', garageOverlays);
  await screenshot('61-seattle-drive-garage.png');

  const setBody = async body => {
    const before = await evaluate(`${diagnostics}.submittedFrames`);
    await select('vehicle-quick', body);
    await waitFor(`${diagnostics}.vehicleStyle === '${body}' && ${diagnostics}.submittedFrames > ${before}`);
  };
  const previousBody = await evaluate(`${diagnostics}.vehicleStyle`);
  const alternateBody = previousBody === 'buggy' ? 'ev6' : 'buggy';
  await setBody(alternateBody);
  await verifyCarView('orbit', garageOverlays);
  await screenshot('61b-seattle-drive-selected-body.png');
  let phoneView, phoneBuggyView;
  if (setPhone) {
    await resizePhone(true);
    try {
      await setBody('ev6');
      phoneView = await verifyCarView('orbit', garageOverlays);
      await screenshot('61c-seattle-drive-phone-garage.png');
      await setBody('buggy');
      phoneBuggyView = await verifyCarView('orbit', garageOverlays);
      await screenshot('61e-seattle-drive-phone-selected-body.png');
    } finally {
      await resizePhone(false);
    }
  }
  await setBody(previousBody);

  if (await evaluate(`${diagnostics}.camera`) !== 'chase') await click('camera-button');
  await click('start-button');
  await waitFor(`${state}.phase === 'racing' && !document.querySelector('#seattle-destination').disabled`);
  const chaseView = await verifySeattleGps(false);
  assert.equal(chaseView.camera.obstruction, 1154870285, 'The uncorrected chase eye still intersects the real building.');
  assert.equal(chaseView.camera.corrected, true);
  assert.ok(Math.hypot(chaseView.camera.eye[0] - spawn.x, chaseView.camera.eye[2] - spawn.z) > 4.5,
    'The chase correction must remain outside the car.');
  await screenshot('61d-seattle-drive-first-chase.png');
  await click('camera-button');
  await waitFor(`${camera}.mode === 'cockpit'`);
  const cockpit = await evaluate(camera);
  assert.equal(cockpit.corrected, false);
  assert.ok(Math.hypot(cockpit.eye[0] - spawn.x, cockpit.eye[2] - spawn.z) < 1,
    'The actual cockpit eye stays in the player body.');
  await click('camera-button');
  await waitFor(`${camera}.mode === 'chase'`);
  const startingWalkers = await walkers();
  await driveUntil(`(() => {
    const current = ${pedestrians}, previous = ${JSON.stringify(startingWalkers.poses)};
    return current.visible >= 2 && current.time > ${startingWalkers.time} && current.poses.some(pose => {
      const start = previous.find(candidate => candidate.id === pose.id);
      return start && Math.hypot(pose.x - start.x, pose.z - start.z) > .25 && Math.abs(pose.phase - start.phase) > .25;
    });
  })()`, 8000);
  const walking = await walkers();
  verifyClearWalkers(walking);
  const stops = await evaluate(`Array.from(document.querySelector('#seattle-destination').options, option => option.value).filter(Boolean)`);
  assert.ok(stops.includes('great-wheel') && stops.includes('space-needle') && stops.includes('amazon-spheres'));
  let phoneChaseView, phoneGuideView;
  if (setPhone) {
    await resizePhone(true);
    try {
      phoneChaseView = await verifySeattleGps(false);
      await select('seattle-destination', 'great-wheel');
      phoneGuideView = await verifySeattleGps(true);
      await screenshot('61f-seattle-drive-phone-guidance.png');
    } finally {
      await resizePhone(false);
    }
  } else await select('seattle-destination', 'great-wheel');
  const guideView = await verifySeattleGps(true);
  const guide = await evaluate(`${geography}.plan`);
  assert.ok(guide.points.length > 10 && Math.abs(guide.length - 503.3) < 1,
    'The route starts from the adjacent node and still takes the same waterfront streets.');
  assert.ok(await evaluate(`document.querySelector('#map-drive-route').getAttribute('d').length > 100`));
  assert.equal((await observe()).drive.cruiseSpeed, 'normal');
  await click('seattle-autopilot');
  await waitFor(`${state}.drive.autopilot`);
  await select('seattle-cruise-speed', 'fast');
  await waitFor(`${state}.drive.autopilot && ${state}.drive.cruiseSpeed === 'fast'`);
  await driveUntil(`${state}.drive.autopilot && ${state}.drive.drivenDistance > 10 &&
    ${pedestrians}.poses.some(pose => pose.yielding > .05)`, 16000);
  verifyClearWalkers(await walkers());
  await click('pause-button');
  await waitFor(`${state}.phase === 'paused'`);
  // The pause command updates native state before the scene replaces its interpolated poses.
  const pauseSubmission = await evaluate(`${diagnostics}.submittedFrames`);
  await waitFor(`${diagnostics}.submittedFrames >= ${pauseSubmission + 2} &&
    ${pedestrians}.time === ${state}.time && ${pedestrians}.visible > 0 &&
    !document.querySelector('#seattle-guidance').checkVisibility()`);
  const paused = await observe();
  const pausedWalkers = await walkers();
  await sleep(350);
  const frozen = await observe();
  const frozenWalkers = await walkers();
  assert.equal(frozen.tick, paused.tick);
  assert.equal(frozen.drive.remaining, paused.drive.remaining);
  assert.equal(frozen.player.x, paused.player.x);
  assert.equal(frozen.player.z, paused.player.z);
  assert.equal(frozen.drive.autopilot, true);
  assert.equal(frozenWalkers.time, pausedWalkers.time);
  const stillVisible = frozenWalkers.poses.filter(walker => pausedWalkers.poses.some(previous => previous.id === walker.id));
  assert.ok(stillVisible.length > 0);
  for (const pose of stillVisible) assert.deepEqual(pose, pausedWalkers.poses.find(previous => previous.id === pose.id));
  await click('resume-button');
  await driveUntil(`${state}.drive.autopilot && Math.abs(${state}.player.yaw - ${origin.player.yaw}) > .7 && ${state}.player.speed > 2`, 18000);
  const turn = await observe();
  assert.ok(turn.player.yaw !== origin.player.yaw, 'The WASM car must actually turn on the route.');
  verifyClearWalkers(await walkers());
  const turnGuideView = await verifySeattleGps(true);
  await screenshot('62-seattle-drive-autopilot.png');
  await driveUntil(`${state}.drive.autopilot && ${state}.player.speed > 14`, 20000);
  const fast = await observe();
  assert.ok(fast.player.speed < 17 && fast.drive.cruiseSpeed === 'fast');
  await driveUntil(`${state}.drive.status === 'arrived'`, 145000);
  const arrived = await observe();
  assert.equal(arrived.drive.destinationId, 'great-wheel');
  assert.equal(arrived.drive.autopilot, false);
  assert.equal(arrived.drive.remaining, 0);
  assert.ok(arrived.drive.drivenDistance > 497 && arrived.drive.drivenDistance < 504);
  assert.ok(Math.hypot(arrived.player.x - 74.31, arrived.player.z - 8798.47) < 2.4 && Math.abs(arrived.player.speed) < .08,
    'The car reaches and stops at the actual waterfront street.');
  await sleep(550);
  const parked = await observe();
  assert.deepEqual([parked.player.x, parked.player.z], [arrived.player.x, arrived.player.z]);
  assert.match(await evaluate(`document.querySelector('#seattle-gps-status').textContent`), /Arrived.*Seattle Great Wheel/);
  await screenshot('63-seattle-drive-arrived.png');

  await select('seattle-cruise-speed', 'normal');
  await waitFor(`${state}.drive.cruiseSpeed === 'normal'`);
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
  const reset = await observe();
  assert.equal(reset.drive.autopilot, false);
  assert.ok(Math.hypot(reset.player.x - spawn.x, reset.player.z - spawn.z) < .02, 'Garage resets to the authored spawn.');
  await verifyCarView('orbit', garageOverlays);
  await waitFor(`${pedestrians}.time === 0 && ${pedestrians}.visible >= 2`);
  const resetWalkers = await walkers();
  const resetPose = resetWalkers.poses.find(pose => garageWalkers.poses.some(previous => previous.id === pose.id));
  assert.ok(resetPose);
  assert.deepEqual(resetPose, garageWalkers.poses.find(previous => previous.id === resetPose.id));

  await click('start-button');
  await waitFor(`${state}.phase === 'racing' && !document.querySelector('#seattle-destination').disabled`);
  await select('seattle-destination', 'great-wheel');
  await waitFor(`${geography}.plan?.points.length > 10`);
  await click('garage-button');
  await waitFor(`${state}.phase === 'ready' && ${state}.tick === 0`);
  await virtualPad();
  await evaluate(`(() => {
    const pad = window.__qualificationPad;
    let leftStreet = false;
    Object.defineProperty(navigator, 'getGamepads', { configurable: true, value: () => {
      const sample = ${state};
      const tick = sample.tick;
      const active = sample.drive && sample.phase === 'racing';
      leftStreet ||= sample.drive?.status === 'off-road';
      pad.axes[0] = active && !leftStreet && tick >= 118 && tick < 210 ? -(.13 + .87 * .55) : 0;
      const value = !active || leftStreet ? 0 : tick < 118 ? 1 : tick < 210 ? .45 : .55;
      pad.buttons[7] = { pressed: value > .5, value };
      return [pad];
    } });
  })()`);
  let offRoad;
  try {
    await click('start-button');
    await driveUntil(`${state}.drive.status === 'off-road'`, 16000);
    const leftStreet = await observe();
    await driveUntil(`${state}.drive.status === 'off-road' && ${state}.player.grounded &&
      ${state}.drive.drivenDistance > ${leftStreet.drive.drivenDistance + 5}`, 8000);
    offRoad = await observe();
  } finally {
    await virtualPad();
  }
  assert.equal(offRoad.drive.destinationId, 'great-wheel');
  assert.equal(offRoad.drive.autopilot, false);
  assert.equal(offRoad.drive.waypoint, null);
  await waitFor(`${geography}.plan === null && document.querySelector('#seattle-autopilot').disabled &&
    document.querySelector('#map-drive-route').getAttribute('d') === ''`);
  assert.match(await evaluate('document.querySelector("#seattle-gps-status").textContent'), /return to a mapped street/i);
  await screenshot('65-seattle-drive-off-road.png');

  await select('game-mode', 'time-attack');
  await waitFor(`${diagnostics}.ready && ${diagnostics}.selectedTrack === 'included:foundry' &&
    !${state}.drive && ${geography} === null`);
  return { origin, scenery, garageView, phoneView, phoneBuggyView, chaseView, phoneChaseView, phoneGuideView,
    guideView, turnGuideView, cockpit, garageWalkers, walking, guide, paused, pausedWalkers, turn, fast, arrived,
    reroute, before, manual, offRoad };
}
