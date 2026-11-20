import { Graphics } from './engine/graphics.js';
import { Input } from './engine/input.js';
import { RacingAudio } from './engine/audio.js';
import { GameplayCapture, toggleFullscreen } from './engine/display.js';
import { clamp } from './engine/math.js';
import { RaceScene, vehicleStyles } from './scene.js';
import { FlightScene, flightReadouts } from './flight-scene.js';
import { WorldFlightScene } from './world-flight-scene.js';
import { readWorldCatalog, WorldTerrain } from './world-terrain.js';
import { CityScene } from './city-scene.js';

const $ = id => document.getElementById(id);
const canvas = $('game'), audio = new RacingAudio(), capture = new GameplayCapture();
const emptyInput = { steer: 0, throttle: 0, brake: 0, boost: false };
let graphics, scene, input, api, runtime, state, frame, previousTime, failed = false, disposed = false;
let camera = 'chase', ready = false, toastTimer, starting = false, showStats = false, soundBlocked = false;
let chaseHud = true;
let airTime = 0, fps = 0, fpsTime = performance.now(), fpsFrames = 0, cpuMs = 0, lastPhase;
let callbacks = 0, lastCallbacks = 0, lastSubmitted = 0, lastSkipped = 0;
let callbackRate = 0, submittedRate = 0, skippedRate = 0, simulationMs = 0, submissionMs = 0;
let activeInput = emptyInput;
let startupDeadline;
let compatibility = { physicsProfile: 'authored', originalPhysicsAvailable: false, trackData: 'authored' };
let sourceConfiguration = { sourcePackageAvailable: false, sourcePackageUrl: null, flightPackageAvailable: false, flightPackageUrl: null };
let sourcePackageText = null;
let originalFlightPackageText = null;
let worldCatalog, selectedDeparture = 'boeing';
let touchSeen = false;
let trackChoices = [];
let selectedTrack = 'included:foundry';
let lastRacingTrack = 'included:foundry';
let sourcePackageId = null;
let ghostEnabled = true, lastCheckpoint = 0, checkpointNoticeUntil = 0, checkpointBonus = 0;
let trackBusy = false;
let vehicleStyle = 'buggy';
const drivingExperiences = {
  'free-roam': { name: 'CITY CRUISE', course: 'Meridian City / free driving', group: 'Open-world city',
    title: 'OWN<br>THE<br><em>STREETS.</em>', description: 'Explore a connected polygon city. Choose your own turns. Traffic, sidewalks, a skyline and a waterfront bridge.' },
  'city-blitz': { name: 'CITY BLITZ', course: 'Meridian City / checkpoint rush', group: 'Open-world city',
    title: 'CITY.<br>CLOCK.<br><em>GO.</em>', description: 'Race through ordered street checkpoints. Find your line through the city and its moving traffic.' },
  'city-circuit': { name: 'CITY CIRCUIT', course: 'Meridian City / street circuit', group: 'Open-world city',
    title: 'TAKE<br>THE<br><em>BLOCK.</em>', description: 'A street circuit through the city. Intersections, real traffic and a rival to beat.' },
  'hard-driving': { name: "HARD DRIVIN'", course: "Hard Drivin' / proving ground", group: 'Driving experiences',
    title: 'FAST.<br>HIGH.<br><em>HARD.</em>', description: 'A separate timed proving ground. A 44-metre loop, banked corners, precision bridge and real 22-metre ramp gap.' },
  stunts: { name: 'STUNTS', course: 'Stunts / loop & jump park', group: 'Driving experiences',
    title: 'LOOP.<br>LEAP.<br><em>LAND.</em>', description: 'A real loop, ramps and airborne gaps. Carry speed, keep your line and earn clean stunt landings.' },
};

function text(id, value) {
  const element = $(id);
  if (element.textContent !== String(value)) element.textContent = value;
}

function updateInputLabel() {
  const touch = document.body.dataset.touch === 'on';
  text('device-label', input?.connected ? touch ? 'TOUCH + CONTROLLER READY' : 'CONTROLLER CONNECTED'
    : touch ? 'TOUCH CONTROLS READY' : 'KEYBOARD READY');
}

function applyTouchPreference() {
  const choice = $('touch-mode').value;
  const detected = touchSeen || (navigator.maxTouchPoints ?? 0) > 0 ||
    window.matchMedia('(any-pointer: coarse)').matches;
  const enabled = choice === 'on' || choice === 'auto' && detected;
  document.body.dataset.touch = enabled ? 'on' : 'off';
  input?.setTouchEnabled(enabled);
  updateInputLabel();
}

function time(value) {
  const total = Math.floor(Math.max(0, value) * 1000 + 1e-5);
  return `${String(Math.floor(total / 60000)).padStart(2, '0')}:${String(Math.floor(total / 1000) % 60).padStart(2, '0')}.${String(total % 1000).padStart(3, '0')}`;
}

function toast(message, duration = 5500) {
  clearTimeout(toastTimer);
  text('toast', message);
  $('toast').hidden = false;
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, duration);
}

function dismissToast() {
  clearTimeout(toastTimer);
  $('toast').hidden = true;
}

function fail(error) {
  if (failed || disposed) return;
  failed = true;
  ready = false;
  clearTimeout(startupDeadline);
  cancelAnimationFrame(frame);
  audio.silence();
  capture.stop();
  input?.clear();
  document.body.dataset.phase = 'error';
  $('error-panel').hidden = false;
  $('start-button').disabled = true;
  $('import-track-button').disabled = true;
  $('physics-mode').disabled = true;
  $('game-mode').disabled = true;
  $('session-mode').disabled = true;
  $('source-track-button').disabled = true;
  $('track-select').disabled = true;
  $('session-track').disabled = true;
  $('ghost-mode').disabled = true;
  $('import-flight-button').disabled = true;
  $('original-flight-button').disabled = true;
  if ($('help-dialog').open) $('help-dialog').close();
  text('error-message', error instanceof Error ? error.message : String(error));
  console.error(error);
}

function phaseChanged() {
  if (lastPhase === state.phase) return;
  lastPhase = state.phase;
  document.body.dataset.phase = state.phase;
  $('hud').hidden = state.phase === 'ready';
  $('pause-panel').hidden = state.phase !== 'paused';
  $('finish-panel').hidden = state.phase !== 'finished';
  if (state.phase === 'finished') {
    if (state.city) {
      const city = state.city, circuit = city.mode === 'city-circuit';
      text('finish-eyebrow', circuit ? 'STREET CIRCUIT / FINISH' : 'CITY BLITZ / FINISH');
      text('finish-title', city.success ? circuit ? 'RACE WON.' : 'CITY CLEAR.' : circuit ? 'RIVAL WINS.' : 'TIME UP.');
      text('finish-time-label', 'DRIVING TIME');
      text('finish-best-label', circuit ? 'BEST LAP' : 'GATES CLEARED');
      text('finish-time', time(city.elapsed));
      text('finish-best', circuit ? state.bestLap > 0 ? time(state.bestLap) : '--:--.---'
        : `${city.checkpointsPassed} / ${city.checkpointCount}`);
      return;
    }
    if (state.stunt && currentMode() !== 'hard-driving') {
      text('finish-eyebrow', 'STUNTS / LOOP & JUMP PARK');
      text('finish-title', state.stunt.success ? 'STUNTS CLEAR.' : 'RUN ENDED.');
      text('finish-time-label', 'RUN TIME');
      text('finish-best-label', 'STUNT SCORE');
      text('finish-time', time(state.time));
      text('finish-best', `${state.stunt.score} PTS`);
      return;
    }
    if (state.flight) {
      text('finish-eyebrow', 'FLIGHT ENDED / RECOVER OR RELAUNCH');
      text('finish-title', 'AIRCRAFT LOST.');
      text('finish-time-label', 'FLIGHT TIME');
      text('finish-best-label', 'LANDINGS');
      text('finish-time', time(state.time));
      text('finish-best', state.flight.landings);
      return;
    }
    const race = state.race?.mode === 'race';
    const trial = state.trial;
    text('finish-eyebrow', trial ? trial.success ? 'ALL CHECKPOINTS CLEARED' : 'THE CLOCK RAN OUT'
      : race ? 'CHEQUERED FLAG' : 'THREE LAPS. NICELY DONE.');
    text('finish-title', trial ? trial.success ? 'TRIAL CLEAR.' : 'TIME UP.'
      : race ? state.race.playerWon ? 'RACE WON.' : 'RIVAL WINS.' : 'RUN COMPLETE.');
    text('finish-time-label', trial ? 'ELAPSED' : 'TOTAL TIME');
    text('finish-best-label', trial ? 'TIME LEFT' : 'BEST LAP');
    text('finish-time', time(trial?.elapsed ?? state.time));
    text('finish-best', trial ? time(trial.timeRemaining) : state.bestLap > 0 ? time(state.bestLap) : '--:--.---');
  }
}

function updateHud(dt) {
  phaseChanged();
  $('driving-guidance').hidden = !state.city && !state.stunt;
  $('map-target').hidden = true;
  if (state.flight) { updateFlightHud(); return; }
  text('speed-unit', 'KM/H');
  text('best-heading', 'BEST LAP');
  text('boost-label', 'TURBO RESERVE');
  text('integrity-label', 'CHASSIS INTEGRITY');
  const car = state.player, speed = Math.abs(car.speed) * 3.6;
  const race = state.race?.mode === 'race';
  const trial = state.trial;
  $('race-position-box').hidden = !race;
  if (race) text('race-position', `${state.race.position} / ${state.race.participants}`);
  text('speed', String(Math.round(speed)).padStart(3, '0'));
  text('gear', car.speed < -0.5 ? 'R' : speed < 1 ? 'N' : String(Math.min(5, 1 + Math.floor(Math.abs(car.speed) / 12))));
  text('lap-heading', trial ? 'GATES' : 'LAP');
  text('time-heading', trial ? 'TIME LEFT' : 'CURRENT LAP');
  text('lap', String(trial ? trial.checkpointsPassed : Math.min(state.lap, state.laps)).padStart(2, '0'));
  text('lap-total', ` / ${String(trial?.checkpointCount ?? state.laps).padStart(2, '0')}`);
  text('lap-time', time(trial?.timeRemaining ?? state.lapTime));
  $('lap-time').classList.toggle('clock-critical', Boolean(trial && trial.timeRemaining < 10));
  text('best-time', state.bestLap > 0 ? time(state.bestLap) : '--:--.---');
  text('boost-value', `${Math.round(car.boost * 100)}%`);
  text('integrity-value', `${Math.round((1 - car.damage) * 100)}%`);
  $('boost-fill').style.transform = `scaleX(${clamp(car.boost, 0, 1)})`;
  $('integrity-fill').style.transform = `scaleX(${clamp(1 - car.damage, 0, 1)})`;
  $('integrity-fill').style.background = car.damage > 0.6 ? '#ff6335' : '';
  const rpm = car.speed > 0.2 ? ((Math.abs(car.speed) % 12) / 12 * 8 + activeInput.throttle * 3 + 1) : 0;
  document.querySelectorAll('.rev-bars i').forEach((bar, i) => bar.classList.toggle('on', i < rpm));
  airTime = !car.grounded && state.phase === 'racing' ? airTime + dt : 0;
  let event = '';
  if (state.phase === 'racing') {
    event = car.damage > 0.9 ? 'CHASSIS CRITICAL / R TO RECOVER'
      : car.y < 3 ? 'OFF TRACK / R TO RECOVER'
        : airTime > 0.18 ? 'AIRBORNE' : car.boosting ? 'TURBO' : '';
  }
  if (trial) {
    if (trial.checkpointsPassed > lastCheckpoint) {
      checkpointNoticeUntil = performance.now() + 2200;
      checkpointBonus = trial.lastBonus;
    }
    lastCheckpoint = trial.checkpointsPassed;
    if (state.phase === 'racing' && performance.now() < checkpointNoticeUntil)
      event = `CHECKPOINT +${checkpointBonus.toFixed(1)}s`;
  }
  text('race-event', event);
  $('ghost-status').hidden = !state.ghost;
  if (state.ghost) {
    const ghost = state.ghost;
    text('ghost-status', !ghost.enabled ? 'BEST-LAP GHOST OFF'
      : ghost.status === 'playing' ? `PURPLE GHOST / BEST ${time(ghost.duration)}`
        : ghost.status === 'recording' ? 'RECORDING YOUR LAP / GHOST AFTER A CLEAN FINISH'
          : ghost.status === 'lap-invalidated' ? 'RECOVERY USED / THIS LAP WILL NOT REPLACE YOUR GHOST'
            : ghost.status === 'capacity-exceeded' ? 'GHOST LIMIT REACHED / THIS LAP WILL NOT BE SAVED'
              : 'CLEAN BEST LAP BECOMES YOUR GHOST');
  }
  const countdown = state.phase === 'countdown' ? String(Math.max(1, Math.ceil(state.countdown)))
    : state.phase === 'racing' && state.time < 0.65 ? 'GO.' : '';
  text('countdown', countdown);
  const [x, y] = scene.mapPoint(car.x, car.z);
  $('map-player').setAttribute('cx', x.toFixed(2));
  $('map-player').setAttribute('cy', y.toFixed(2));
  if (state.city) updateCityHud();
  else if (state.stunt) updateStuntHud();
}

function updateDrivingTarget(target) {
  $('route-arrow').hidden = !target;
  if (!target) return;
  const [x, z] = scene.mapPoint(target.x, target.z);
  $('map-target').hidden = false;
  $('map-target').setAttribute('cx', x.toFixed(2));
  $('map-target').setAttribute('cy', z.toFixed(2));
  const bearing = Math.atan2(target.x - state.player.x, target.z - state.player.z) - state.player.yaw;
  $('route-arrow').style.transform = `rotate(${bearing}rad)`;
}

function updateCityHud() {
  const city = state.city, free = city.mode === 'free-roam', blitz = city.mode === 'city-blitz';
  text('lap-heading', free ? 'DISTANCE' : 'STREET GATES');
  text('lap', free ? (city.drivenDistance / 1000).toFixed(1) : String(city.checkpointsPassed).padStart(2, '0'));
  text('lap-total', free ? ' KM' : ` / ${String(city.checkpointCount).padStart(2, '0')}`);
  text('time-heading', blitz ? 'TIME LEFT' : 'DRIVE TIME');
  text('lap-time', time(blitz ? city.timeRemaining : city.elapsed));
  $('lap-time').classList.toggle('clock-critical', blitz && city.timeRemaining < 15);
  text('best-heading', 'DISTRICT');
  text('best-time', city.district);
  $('race-position-box').hidden = !state.race;
  if (state.race) text('race-position', `${state.race.position} / ${state.race.participants}`);
  $('ghost-status').hidden = true;
  const next = city.checkpoints[city.nextCheckpointIndex];
  text('world-district', city.currentRoad.toUpperCase());
  text('world-objective', next && city.success === null
    ? `${next.name} / ${Math.round(Math.hypot(next.x - state.player.x, next.z - state.player.z))} M`
    : `${city.traffic.length} TRAFFIC CARS / ${city.pedestrians.length} WALKERS / EXPLORE ANY STREET`);
  updateDrivingTarget(city.success === null ? next : null);
  text('race-event', state.phase !== 'racing' ? '' : state.impact > 0.15 ? 'CONTACT / KEEP MOVING'
    : state.player.boosting ? 'TURBO' : !state.player.grounded && airTime > 0.18 ? 'AIRBORNE' : '');
}

function updateStuntHud() {
  const stunt = state.stunt;
  const timed = currentMode() === 'hard-driving';
  if (!timed) {
    text('lap-heading', 'STUNT SCORE');
    text('lap', stunt.score);
    text('lap-total', ' PTS');
    text('time-heading', 'RUN TIME');
    text('lap-time', time(state.time));
    text('best-heading', 'LOOPS / LANDINGS');
    text('best-time', `${stunt.loopsCompleted} / ${stunt.cleanLandings}`);
  } else {
    text('best-heading', 'STUNT SCORE');
    text('best-time', `${stunt.score} PTS`);
  }
  text('world-district', stunt.feature.toUpperCase());
  text('world-objective', `${stunt.nextGate} / ${stunt.gatesPassed} OF ${stunt.gateCount} GATES / ${stunt.loopsCompleted} LOOPS`);
  const matrix = scene.checkpointFrame(stunt.nextGateProgress);
  updateDrivingTarget(stunt.success === null ? { x: matrix[12], z: matrix[14] } : null);
  if (!timed || performance.now() >= checkpointNoticeUntil)
    text('race-event', state.phase !== 'racing' ? '' : stunt.score > 0 ? stunt.lastStunt
      : state.player.boosting ? 'TURBO' : '');
}

function updateFlightHud() {
  const aircraft = state.flight.aircraft;
  const readout = flightReadouts(aircraft, scene.navigationWorld);
  const display = state.flight.display;
  const speed = display?.speedKnots ?? Math.round(aircraft.speed * 1.943844);
  const altitude = display?.altitudeFeet ?? Math.round(aircraft.y * 3.28084);
  $('race-position-box').hidden = true;
  $('ghost-status').hidden = true;
  text('speed', String(speed).padStart(3, '0'));
  $('flight-mach').hidden = aircraft.mach == null;
  if (aircraft.mach != null) text('flight-mach', `MACH ${aircraft.mach.toFixed(2)}`);
  text('cockpit-airspeed', `${speed} KT`);
  text('cockpit-altitude', `${altitude} FT`);
  text('cockpit-mach', aircraft.mach == null ? '' : `M ${aircraft.mach.toFixed(2)}`);
  text('speed-unit', 'KTS');
  text('gear', aircraft.gearDown ? 'DN' : 'UP');
  text('lap-heading', 'ALTITUDE');
  text('lap', altitude);
  text('lap-total', ' FT');
  text('time-heading', 'FLIGHT TIME');
  text('lap-time', time(state.time));
  $('lap-time').classList.remove('clock-critical');
  text('best-heading', 'VERTICAL / FT/MIN');
  const climb = Math.round(aircraft.verticalSpeed * 196.8504);
  text('best-time', `${climb > 0 ? '+' : ''}${climb}`);
  text('boost-label', aircraft.afterburner ? 'AFTERBURNER' : 'THROTTLE');
  text('boost-value', `${display?.thrustPercent ?? Math.round(aircraft.throttle * 100)}%`);
  $('boost-fill').style.transform = `scaleX(${aircraft.throttle})`;
  text('integrity-label', 'FUEL REMAINING');
  text('integrity-value', `${Math.round(aircraft.fuel * 100)}%`);
  $('integrity-fill').style.transform = `scaleX(${aircraft.fuel})`;
  $('integrity-fill').style.background = aircraft.fuel < 0.15 ? '#ff6335' : '';
  text('flight-gear-button', `GEAR ${aircraft.gearDown ? 'DOWN' : 'UP'} / G`);
  text('touch-gear', aircraft.gearDown ? 'GEAR DN' : 'GEAR UP');
  text('flight-heading', String((display?.headingDegrees ?? Math.round(readout.heading)) % 360).padStart(3, '0'));
  $('flight-ladder').setAttribute('transform', `rotate(${readout.bank.toFixed(2)}) translate(0,${(readout.pitch * 3).toFixed(2)})`);
  text('flight-nav', readout.runway ? `${readout.runway.toUpperCase()} / ${(readout.range / 1852).toFixed(1)} NM` : 'NO AIRFIELD IN RANGE');
  const configuration = state.flight.configuration;
  text('flight-config', `${aircraft.gearDown ? 'GEAR DOWN' : 'GEAR UP'}${configuration ? configuration.hookDown ? ' / HOOK DOWN' : ' / HOOK UP' : ''}${aircraft.onGround ? ' / GROUND' : ''}`);
  text('touch-hook', configuration?.hookDown ? 'HOOK DN' : 'HOOK UP');
  text('flight-load', `${aircraft.gForce.toFixed(1)} G`);
  if (scene.terrain) {
    const terrain = scene.terrain;
    text('world-stream-status', state.terrainWaiting ? terrain.status || state.message
      : scene.buildingError || terrain.status);
    const path = scene.mapWaterPath(performance.now());
    if (path !== $('map-water').getAttribute('d')) $('map-water').setAttribute('d', path);
  }
  text('race-event', state.phase !== 'racing' ? '' : aircraft.stalled ? 'STALL / LOWER NOSE, ADD POWER'
    : aircraft.onGround ? 'RUNWAY / POWER UP, THEN PULL BACK'
      : aircraft.fuel < 0.15 ? 'LOW FUEL' : aircraft.mach >= 1 ? 'SUPERSONIC'
        : aircraft.afterburner ? 'AFTERBURNER' : '');
  text('countdown', state.phase === 'countdown' ? String(Math.max(1, Math.ceil(state.countdown))) : '');
  const [x, y] = scene.mapPoint(aircraft.x, aircraft.z);
  $('map-player').setAttribute('cx', x.toFixed(2));
  $('map-player').setAttribute('cy', y.toFixed(2));
}

function updateFlightHudMode() {
  const flightChase = Boolean(state?.flight) && camera === 'chase';
  document.body.dataset.flightHud = flightChase && !chaseHud ? 'off' : 'on';
  const button = $('flight-hud-button');
  button.hidden = !flightChase;
  button.setAttribute('aria-pressed', String(chaseHud));
  text('flight-hud-button', chaseHud ? 'HUD ON' : 'HUD OFF');
}

async function unlockAudio() {
  try {
    await audio.unlock();
    soundBlocked = false;
    text('sound-button', audio.muted ? 'SOUND OFF' : 'SOUND ON');
  } catch (error) {
    soundBlocked = true;
    text('sound-button', 'RETRY SOUND');
    toast(`${error.message} You can still drive; click RETRY SOUND to try again.`, 8000);
  }
}

async function startRun(restart = false) {
  if (!ready || failed || starting || trackBusy) return;
  starting = true;
  try {
    await unlockAudio();
    input.clear();
    api.Command(restart ? 'restart' : 'start');
    if (state.flight) {
      state = JSON.parse(api.Observe());
      phaseChanged();
      input.setFlightThrottle(state.flight.aircraft.throttle);
    }
    previousTime = undefined;
    canvas.focus({ preventScroll: true });
  } finally {
    starting = false;
  }
}

async function action(name) {
  if (failed || disposed) return;
  if (name === 'help') {
    if ($('help-dialog').open) $('help-dialog').close();
    else {
      if (state && ['racing', 'countdown'].includes(state.phase)) pause();
      $('help-dialog').showModal();
    }
    return;
  }
  if (name === 'fullscreen') {
    try { await toggleFullscreen($('viewport')); }
    catch (error) { toast(error.message); }
    canvas.focus({ preventScroll: true });
    return;
  }
  if (name === 'mute') {
    await unlockAudio();
    if (soundBlocked) return;
    audio.setMuted(!audio.muted);
    text('sound-button', audio.muted ? 'SOUND OFF' : 'SOUND ON');
    $('sound-button').setAttribute('aria-pressed', String(audio.muted));
    canvas.focus({ preventScroll: true });
    return;
  }
  if (name === 'stats') {
    showStats = !showStats;
    $('stats').hidden = !showStats;
    if (showStats && graphics && state) updateStats();
    return;
  }
  if (!api || !ready || trackBusy) return;
  if (name === 'garage') {
    capture.stop();
    input.clear();
    audio.silence();
    api.Command('garage');
    state = JSON.parse(api.Observe());
    previousTime = undefined;
    phaseChanged();
    updateModeLabels();
    text('start-label', startLabel());
    if ($('help-dialog').open) $('help-dialog').close();
    toast('Back in the garage. Choose a vehicle, course or experience.', 3500);
    canvas.focus({ preventScroll: true });
  } else if (name === 'camera') {
    camera = camera === 'chase' ? 'cockpit' : 'chase';
    document.body.dataset.camera = camera;
    $('camera-button').childNodes[0].nodeValue = `${camera.toUpperCase()} `;
    updateFlightHudMode();
    canvas.focus({ preventScroll: true });
  } else if (name === 'flight-hud' && state.flight && camera === 'chase') {
    chaseHud = !chaseHud;
    updateFlightHudMode();
    canvas.focus({ preventScroll: true });
  } else if (name === 'start') {
    if (state.phase === 'ready') await startRun();
    else if (state.phase === 'paused') await resume();
    else if (state.phase === 'finished') await startRun(true);
  } else if (name === 'pause') {
    if (state.phase === 'ready') await startRun();
    else if (state.phase === 'paused') await resume();
    else if (['racing', 'countdown'].includes(state.phase)) pause();
  } else if (name === 'recover' && state.phase === 'racing') {
    api.Command('recover');
    if (state.flight) {
      state = JSON.parse(api.Observe());
      phaseChanged();
      input.setFlightThrottle(state.flight.aircraft.throttle);
      toast(scene.terrain ? 'Returned to the selected departure.' : 'Aircraft recovered to the runway.', 2500);
    } else toast('Car recovered. A time penalty was added to your run.', 2500);
  } else if (name === 'gear' && state.flight) {
    if (state.phase === 'paused') toast('Resume the flight to change landing gear.', 2500);
    else api.Command('gear-toggle');
  } else if (name === 'hook' && state.flight && compatibility.flightSourceActive) {
    if (state.phase === 'paused') toast('Resume the flight to change the arrestor hook.', 2500);
    else api.Command('hook-toggle');
  } else if (['air-start', 'carrier-start', 'landmark-tour'].includes(name) && state.flight) {
    if (['carrier-start', 'landmark-tour'].includes(name) && !compatibility.flightSourceActive) {
      toast('Select the original Amiga flight model for original carrier/scenery practice.');
      return;
    }
    await unlockAudio();
    if (!['ready', 'paused', 'finished'].includes(state.phase)) pause();
    api.Command(name);
    state = JSON.parse(api.Observe());
    phaseChanged();
    input.clear();
    input.setFlightThrottle(state.flight.aircraft.throttle);
    previousTime = undefined;
    if ($('help-dialog').open) $('help-dialog').close();
    if (name === 'landmark-tour') toast('Original bridge scenery ahead. Source world geometry / enhanced ambient activity.', 7000);
    canvas.focus({ preventScroll: true });
  }
}

function pause() {
  if (!api || !['racing', 'countdown'].includes(state.phase)) return;
  api.Command('pause');
  state = JSON.parse(api.Observe());
  phaseChanged();
  audio.silence();
  input.clear();
}

function updateStats() {
  const interval = fps > 0 ? (1000 / fps).toFixed(1) : '--';
  text('stats', `${fps.toFixed(0)} FPS (GPU-completed) / ${interval} ms per frame\n` +
    `Browser ${callbackRate.toFixed(0)} callbacks/s / submitted ${submittedRate.toFixed(0)} frames/s\n` +
    `CPU ${cpuMs.toFixed(2)} ms / C# ${simulationMs.toFixed(2)} ms / draw ${submissionMs.toFixed(2)} ms\n` +
    `GPU timer ${graphics.gpuMilliseconds?.toFixed(2) ?? 'unavailable'} ms\n` +
    `GPU queue ${graphics.pendingFrames} / 2 / wait skips ${skippedRate.toFixed(0)}/s\n` +
    `${canvas.width} x ${canvas.height} / ${graphics.quality} / simulation tick ${state.tick}\n` +
    `Physics ${compatibility.physicsProfile} / data ${compatibility.trackData}\n${graphics.device}`);
}

function configureTrack(track) {
  const flying = track.kind === 'flight';
  const city = track.kind === 'city', road = city ? track.track : track;
  if (!flying && (!Array.isArray(road.points) || road.points.length < 3)) throw new Error('The C# track definition is invalid.');
  scene = track.terrain ? new WorldFlightScene(graphics, track.world, track.terrain)
    : flying ? new FlightScene(graphics, track.world) : city ? new CityScene(graphics, road, track.world) : new RaceScene(graphics, road);
  if (!flying) scene.setVehicleStyle(vehicleStyle);
  state = JSON.parse(api.Observe());
  compatibility = JSON.parse(api.DescribeCompatibility());
  const original = compatibility.flightSourceActive === true;
  const world = Boolean(track.terrain);
  document.body.dataset.vehicle = flying ? 'flight' : 'racing';
  document.body.dataset.flightSource = original ? 'original' : 'authored';
  document.body.dataset.worldFlight = world ? 'on' : 'off';
  document.body.dataset.experience = compatibility.experience ?? 'racing';
  document.body.dataset.camera = camera;
  updateFlightHudMode();
  input.setMode(flying ? 'flight' : 'racing', state.flight?.aircraft.throttle ?? 0);
  text('steer-label', flying ? 'BANK / PULL UP' : 'DRAG TO STEER');
  text('touch-gas-hint', flying ? 'SLIDE UP FOR AFTERBURNER' : 'SLIDE UP FOR TURBO');
  $('touch-steering').setAttribute('aria-label', flying ? 'Drag left or right to bank; drag up to climb' : 'Drag horizontally to steer');
  if (compatibility.physicsProfile === 'authored' || state.stunt) {
    api.Command(ghostEnabled ? 'ghost-on' : 'ghost-off');
    state = JSON.parse(api.Observe());
  }
  if (compatibility.authoredTrackId) selectedTrack = `included:${compatibility.authoredTrackId}`;
  if (!flying && !drivingExperiences[compatibility.experience]) lastRacingTrack = selectedTrack;
  text('track-length', world ? '50 STATES' : flying ? 'FREE' : city ? 'OPEN' : Math.round(road.length).toLocaleString('en-US'));
  const circuitName = (world ? track.world.geography.destination.name.split(' • ')[0]
    : flying ? track.name : city ? track.world.name : road.name).replace(/\s+\u2014.*$/, '').toUpperCase();
  text('circuit-name', circuitName);
  updateModeLabels();
  const path = city ? scene.mapRoadPath() : flying && track.world.sourceWorld ? scene.mapSurfacePaths() : scene.track.points.map((p, index) => {
    const [x, y] = scene.mapPoint(p.x, p.z);
    return `${index ? 'L' : 'M'}${x.toFixed(2)} ${y.toFixed(2)}`;
  }).join(' ') + (world ? '' : ' Z');
  $('map-outline').setAttribute('d', path);
  $('map-road').setAttribute('d', path);
  const start = flying ? track.world.runwayStart : city ? state.player : road.points[0];
  const [x, y] = scene.mapPoint(start.x, start.z);
  $('map-start').setAttribute('cx', x);
  $('map-start').setAttribute('cy', y);
  const special = drivingExperiences[compatibility.experience];
  const imported = compatibility.trackData !== 'authored' && !special && !world;
  const classic = compatibility.sourcePhysicsActive === true;
  const sourceData = compatibility.trackData === 'amiga-derived-reference-data';
  text('track-mode', world ? 'WORLD FLIGHT / GEOGRAPHIC TERRAIN / NOT FOR NAVIGATION'
    : special ? `${special.name} / NEWLY AUTHORED VECTOR DRIVING EXPERIENCE`
    : original ? 'AMIGA INTERCEPTOR / ORIGINAL FLIGHT RULES + VERIFIED USER-DISK DATA / ENHANCED WEBGL'
    : flying ? 'INTERCEPTOR-INSPIRED FLIGHT / AUTHORED AERODYNAMICS'
    : classic ? 'CLASSIC / AMIGA-DERIVED SOURCE COMPATIBILITY - STOCK PARITY UNVERIFIED'
    : sourceData ? 'REFERENCE TRACK DATA / MODERN RESPONSIVE PHYSICS'
      : imported ? 'PRIVATE IMPORTED GEOMETRY / PROTOTYPE PHYSICS - NOT ORIGINAL AMIGA DYNAMICS'
        : 'INSPIRED BY THE AMIGA ORIGINAL / MODERN PLAYABLE PROTOTYPE');
  text('track-import-status', world ? 'World Flight uses Modern jet physics. The Amiga model and original scenery remain separate.'
    : special ? `${special.name}: newly authored geometry and driving rules. Inspired by classic driving games; not an imported historical engine.`
    : original ? 'Original Amiga player rules and verified user-disk tables/world are active. Enhanced rendering uses the canonical source contact profile; clean publisher-disk equality is not claimed.'
    : flying ? 'Modern authored jet model selected. Load an original ADF/ZIP or choose Amiga flight to use verified original player rules and disk data.'
    : classic
    ? `Loaded ${track.name} with Amiga-derived reference physics. Exact stock-Amiga parity is unverified.`
    : sourceData ? `Loaded ${track.name} with Modern physics. Classic/reference comparison is optional.`
      : imported ? `Loaded ${track.name}. Geometry is experimental; vehicle dynamics remain the authored prototype, not original Amiga physics.`
        : 'Modern physics are the default. Reference tracks and optional Classic physics require an external source-data package.');
  $('reset-track-button').disabled = !imported;
  $('physics-mode').value = original ? 'flight-original' : classic ? 'classic' : 'modern';
  text('original-flight-status', compatibility.originalFlightIdentity
    ? `Original executable verified: ${compatibility.originalFlightIdentity.executableSha256.slice(0, 16)}... / required tables and contact data validated. Original bytes remain external.`
    : sourceConfiguration.flightPackageAvailable ? 'An original-flight package is configured. Select Amiga flight to load it.'
      : 'Load your original ADF/ZIP locally. The disk stays in this browser; no original data is bundled or uploaded.');
  updateSourceOptions();
  renderTrackChoices();
  lastCheckpoint = 0;
  checkpointNoticeUntil = 0;
}

function updateModeLabels() {
  const mode = currentMode();
  const flying = mode.startsWith('flight');
  const world = mode === 'flight-world';
  const special = drivingExperiences[mode], city = mode === 'free-roam' || mode.startsWith('city-');
  const name = special?.name ?? (world ? 'WORLD FLIGHT' : mode === 'flight-original' ? 'AMIGA FLIGHT / INTERCEPTOR' : flying ? 'FLIGHT / INTERCEPTOR' : mode === 'checkpoint-run' ? 'CHECKPOINT RUN' : mode === 'race' ? 'RIVAL RACE' : 'TIME ATTACK');
  $('game-mode').value = mode;
  $('session-mode').value = mode;
  text('map-label', `${scene.track.name.replace(/\s+\u2014.*$/, '').toUpperCase()} / ${name}`);
  text('circuit-mode', `01 / ${name}`);
  text('circuit-laps', flying || city || mode === 'stunts' ? '--' : ['checkpoint-run', 'hard-driving'].includes(mode) ? '1' : '3');
  text('track-length-unit', world ? 'TERRAIN COVERAGE' : flying ? 'FLIGHT' : city ? 'CITY' : 'METRES');
  text('menu-intro', world ? 'Fly the U.S. in polygons. Explore Seattle, the Golden Gate, Yosemite and Grand Canyon, or go supersonic over the Cascades.'
    : special ? special.description : flying ? 'A runway. An open sky. A very different kind of stunt. Climb out, bank over the bay, then bring it home.'
    : 'A ribbon of tarmac. A very long way down. Find your line. Save your boost. Stick the landing.');
  $('menu-title').innerHTML = special?.title ?? (world ? 'WIDE<br><em>OPEN.</em>' : flying ? 'LEAVE<br>THE<br><em>ROAD.</em>' : 'DRIVE<br>ABOVE<br><em>IT ALL.</em>');
  text('mode-description', world ? 'Stream real U.S. terrain. For Mach flight, raise throttle to full, retract the gear and hold Space.'
    : special ? special.description : mode === 'flight-original' ? 'Original Amiga flight rules. Actual user-disk world. Runway, carrier and free-flight practice.'
    : flying ? 'Modern jet aerodynamics. Runway takeoff, free flight and landing practice. No original disk required.'
    : mode === 'checkpoint-run' ? 'One circuit. Ordered gates. Extra time at each checkpoint.'
    : mode === 'race' ? 'Same-physics rival. Three laps. First to the flag wins.'
      : 'Three laps. Set a clean best, then chase your own ghost.');
  $('ghost-mode').disabled = !ready || trackBusy || !['time-attack', 'stunts'].includes(mode) || compatibility.sourcePhysicsActive === true;
  for (const button of document.querySelectorAll('.experience-picker [data-experience]')) {
    button.disabled = !ready || trackBusy;
    const active = button.dataset.experience === 'flight' ? flying && !world
      : button.dataset.experience === 'free-roam' ? city : mode === button.dataset.experience;
    button.setAttribute('aria-pressed', String(active));
  }
  for (const id of ['game-mode', 'session-mode']) {
    $(id).disabled = !ready || trackBusy;
    for (const option of $(id).options)
      option.disabled = option.value === 'flight-original'
        ? !originalFlightAvailable()
        : option.value === 'flight-world' ? !sourceConfiguration.worldFlightUrl
        : compatibility.sourcePhysicsActive === true && !['time-attack', 'flight'].includes(option.value) && !drivingExperiences[option.value];
  }
}

function startLabel() {
  const mode = currentMode();
  return mode === 'flight-world' ? state.flight.aircraft.onGround ? 'TAKE OFF / BOEING FIELD' : 'START SCENIC FLIGHT'
    : mode === 'free-roam' ? 'START CRUISING'
    : mode === 'city-blitz' ? 'START CITY BLITZ'
      : mode === 'city-circuit' ? 'START STREET RACE'
        : mode === 'hard-driving' ? 'START TIMED RUN'
          : mode === 'stunts' ? 'ENTER LOOP PARK'
            : state.flight?.configuration?.onCarrier ? 'CARRIER LAUNCH'
              : state.flight ? 'TAKE OFF / RUNWAY' : 'START YOUR RUN';
}

function renderTrackChoices() {
  for (const id of ['track-select', 'session-track']) {
    const select = $(id);
    select.replaceChildren();
    const groups = new Map();
    for (const choice of trackChoices) {
      if (!groups.has(choice.group)) {
        const group = document.createElement('optgroup');
        group.label = choice.group;
        groups.set(choice.group, group);
        select.append(group);
      }
      const option = document.createElement('option');
      option.value = choice.id;
      option.textContent = choice.name + (choice.available ? '' : ' (unavailable)');
      option.disabled = !choice.available;
      option.title = choice.reason ?? choice.description ?? '';
      groups.get(choice.group).append(option);
    }
    if (!trackChoices.some(choice => choice.id === selectedTrack)) {
      const option = document.createElement('option');
      option.value = selectedTrack;
      option.textContent = `Local package: ${scene?.track.name ?? 'imported track'}`;
      select.append(option);
    }
    select.value = selectedTrack;
  }
  const unavailable = trackChoices.filter(choice => !choice.available);
  text('track-catalog-status', `${trackChoices.filter(choice => choice.available).length} courses and experiences available. ` +
    unavailable.map(choice => `${choice.name}: ${choice.reason}`).join(' '));
}

function currentMode() {
  return compatibility.experience === 'flight-world' || drivingExperiences[compatibility.experience] ? compatibility.experience
    : state?.flight ? compatibility.flightSourceActive ? 'flight-original' : 'flight'
    : state?.trial ? 'checkpoint-run' : state?.race?.mode ?? 'time-attack';
}

function originalFlightAvailable() {
  return sourceConfiguration.flightPackageAvailable || compatibility.originalFlightDataAvailable;
}

function updateSourceOptions() {
  const available = sourceConfiguration.sourcePackageAvailable || compatibility.sourcePackageLoaded;
  const special = Boolean(drivingExperiences[currentMode()]);
  const world = currentMode() === 'flight-world';
  $('classic-option').disabled = Boolean(state?.flight) || special || !available;
  $('classic-option').hidden = Boolean(state?.flight);
  $('flight-original-option').hidden = !state?.flight;
  $('flight-original-option').disabled = !originalFlightAvailable();
  $('physics-mode').disabled = !ready || trackBusy || special || world;
  $('world-flight-button').disabled = !ready || trackBusy || !sourceConfiguration.worldFlightUrl;
  for (const id of ['world-departure', 'world-departure-menu']) $(id).disabled = !ready || trackBusy || !world;
  $('source-track-button').disabled = !ready || trackBusy || Boolean(state?.flight) || !sourceConfiguration.sourcePackageAvailable;
  $('original-flight-button').disabled = !ready || trackBusy || !originalFlightAvailable();
  $('import-flight-button').disabled = !ready || trackBusy;
  const choice = trackChoices.find(item => item.id === 'flight:amiga-interceptor');
  if (choice) choice.available = Boolean(originalFlightAvailable());
  const worldChoice = trackChoices.find(item => item.id === 'flight:world');
  if (worldChoice) worldChoice.available = Boolean(sourceConfiguration.worldFlightUrl);
}

function setTrackBusy(busy) {
  trackBusy = busy;
  for (const id of ['start-button', 'track-select', 'session-track', 'import-track-button', 'physics-mode'])
    $(id).disabled = busy || !ready;
  for (const id of ['game-mode', 'session-mode'])
    $(id).disabled = busy || !ready;
  $('reset-track-button').disabled = busy || !ready || compatibility.trackData === 'authored';
  updateSourceOptions();
  updateModeLabels();
}

function cachedSourceChoice() {
  if (sourcePackageId === 'local') return 'local:source';
  if (sourcePackageId && sourcePackageId !== 'default-reference') return `source:${sourcePackageId}`;
  return trackChoices.find(choice => choice.id.startsWith('source:') &&
    choice.sourcePackageUrl === sourceConfiguration.sourcePackageUrl)?.id ?? 'local:source';
}

async function loadHostedSource(classic, trackId = null) {
  const choice = trackId ? trackChoices.find(item => item.id === `source:${trackId}`) : null;
  const packageKey = trackId ?? 'default-reference';
  if (!sourcePackageText || sourcePackageId !== packageKey) {
    if (!sourceConfiguration.sourcePackageAvailable || !sourceConfiguration.sourcePackageUrl)
      throw new Error('Load a local source-runtime package or configure an external host package first.');
    if (trackId && (!choice || !choice.available)) throw new Error('That reference track is unavailable.');
    const contents = await fetchExternalPackage(choice?.sourcePackageUrl ?? sourceConfiguration.sourcePackageUrl, 'reference');
    const track = JSON.parse(api.LoadSourcePackage(contents, classic));
    sourcePackageText = contents;
    sourcePackageId = packageKey;
    selectedTrack = choice?.id ?? trackChoices.find(item => item.id.startsWith('source:') && item.sourcePackageUrl === sourceConfiguration.sourcePackageUrl)?.id ?? 'local:source';
    return track;
  }
  const track = JSON.parse(api.LoadSourcePackage(sourcePackageText, classic));
  selectedTrack = choice?.id ?? cachedSourceChoice();
  return track;
}

async function fetchExternalPackage(relative, label) {
  const url = new URL(relative, import.meta.url), base = new URL('.', import.meta.url);
  if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname))
    throw new Error(`The configured ${label} package must remain within this game mount.`);
  const response = await fetch(url, { cache: 'no-store' });
  if (!response.ok) throw new Error(`The ${label} package could not load (HTTP ${response.status}).`);
  const contents = await response.text();
  if (contents.length > 16 * 1024 * 1024) throw new Error(`The ${label} package exceeds sixteen MiB.`);
  return contents;
}

async function loadOriginalFlight() {
  if (compatibility.originalFlightDataAvailable) return JSON.parse(api.SelectOriginalFlight());
  if (!sourceConfiguration.flightPackageAvailable || !sourceConfiguration.flightPackageUrl)
    throw new Error('Load your original flight ADF/ZIP or configure an external flight package first.');
  originalFlightPackageText ??= await fetchExternalPackage(sourceConfiguration.flightPackageUrl, 'original flight');
  return JSON.parse(api.LoadFlightPackage(originalFlightPackageText));
}

function populateWorldDepartures(catalog) {
  for (const id of ['world-departure', 'world-departure-menu']) {
    const field = $(id);
    field.replaceChildren();
    for (const region of catalog.regions) {
      const group = document.createElement('optgroup');
      group.label = region.name;
      for (const destination of region.destinations) {
        const option = document.createElement('option');
        option.value = destination.id;
        option.textContent = destination.name;
        group.append(option);
      }
      field.append(group);
    }
    field.value = selectedDeparture;
  }
}

async function loadWorldFlight(departure) {
  if (!sourceConfiguration.worldFlightUrl) throw new Error('No World Flight terrain package is configured.');
  worldCatalog ??= await readWorldCatalog(sourceConfiguration.worldFlightUrl);
  const region = worldCatalog.regions.find(item => item.destinations.some(destination => destination.id === departure));
  if (!region) throw new Error('Unknown World Flight departure.');
  const destination = region.destinations.find(item => item.id === departure);
  const terrain = new WorldTerrain(worldCatalog, region, {
    add: (level, x, z, bytes) => api.AddWorldTerrainTile(level, x, z, bytes),
    remove: (level, x, z) => api.RemoveWorldTerrainTile(level, x, z),
  });
  const runway = departure === 'boeing' ? region.runway : null;
  const seed = await terrain.seed(destination.x, destination.z);
  const track = JSON.parse(api.SelectWorldFlight(region.name, JSON.stringify(destination),
    runway && JSON.stringify(runway), seed));
  selectedDeparture = departure;
  populateWorldDepartures(worldCatalog);
  return { ...track, terrain };
}

async function instantiateChoice(choice) {
  if (!choice?.available) throw new Error(choice?.reason ?? 'That course is unavailable.');
  if (choice.id === 'flight:amiga-interceptor') return loadOriginalFlight();
  if (choice.id === 'flight:world') return loadWorldFlight(selectedDeparture);
  if (choice.id === 'flight:interceptor-bay') return JSON.parse(api.SelectFlight());
  if (choice.id.startsWith('experience:')) return JSON.parse(api.SelectExperience(choice.id.slice(11)));
  if (choice.id.startsWith('source:'))
    return loadHostedSource(compatibility.sourcePhysicsActive === true, choice.id.slice(7));
  if (choice.id.startsWith('included:')) return JSON.parse(api.SelectAuthoredTrack(choice.id.slice(9)));
  throw new Error('The selected course has an unsupported identifier.');
}

async function selectGameMode(mode, departure = selectedDeparture) {
  if (!ready || trackBusy) return;
  try {
    if (capture.recorder) throw new Error('Stop recording before changing experience.');
    pause();
    setTrackBusy(true);
    let track;
    if (drivingExperiences[mode]) {
      track = JSON.parse(api.SelectExperience(mode));
      selectedTrack = `experience:${mode}`;
    } else if (mode.startsWith('flight')) {
      if (!state.flight && !drivingExperiences[compatibility.experience]) lastRacingTrack = selectedTrack;
      track = mode === 'flight-world' ? await loadWorldFlight(departure)
        : mode === 'flight-original' ? await loadOriginalFlight() : JSON.parse(api.SelectFlight());
      selectedTrack = mode === 'flight-world' ? 'flight:world'
        : mode === 'flight-original' ? 'flight:amiga-interceptor' : 'flight:interceptor-bay';
    } else {
      if (state.flight || drivingExperiences[compatibility.experience]) {
        const previous = trackChoices.find(choice => choice.id === lastRacingTrack &&
          (choice.id.startsWith('included:') || choice.id.startsWith('source:')) && choice.available)
          ?? trackChoices.find(choice => choice.id === 'included:foundry');
        track = await instantiateChoice(previous);
        selectedTrack = previous.id;
      }
      if (!compatibility.sourcePhysicsActive || track) api.Command(`mode-${mode}`);
      else if (mode !== 'time-attack') throw new Error('Classic driving currently supports Time Attack only.');
    }
    if (track) replaceTrack(track);
    state = JSON.parse(api.Observe());
    phaseChanged();
    updateModeLabels();
    lastCheckpoint = 0;
    checkpointNoticeUntil = 0;
    previousTime = undefined;
    if ($('help-dialog').open) $('help-dialog').close();
    canvas.focus({ preventScroll: true });
  } catch (error) {
    for (const id of ['world-departure', 'world-departure-menu']) $(id).value = selectedDeparture;
    if (!failed) { updateModeLabels(); toast(`Mode change rejected: ${error.message}`, 8000); }
  } finally { setTrackBusy(false); }
}

async function configureSourceAvailability() {
  const response = await fetch(new URL('./runtime-config.json', import.meta.url), { cache: 'no-store' });
  if (response.status === 404) {
    text('track-import-status', 'This static host has no reference package configured. Local package import and Modern mode remain available.');
    return;
  }
  if (!response.ok) throw new Error(`Reference-package configuration failed (HTTP ${response.status}).`);
  const configuration = await response.json();
  if (typeof configuration.sourcePackageAvailable !== 'boolean' ||
      configuration.sourcePackageAvailable && typeof configuration.sourcePackageUrl !== 'string')
    throw new Error('The reference-package configuration is invalid.');
  sourceConfiguration = configuration;
  if (configuration.flightPackageAvailable !== undefined && typeof configuration.flightPackageAvailable !== 'boolean' ||
      configuration.flightPackageAvailable && typeof configuration.flightPackageUrl !== 'string')
    throw new Error('The original-flight package configuration is invalid.');
  if (configuration.worldFlightUrl != null && typeof configuration.worldFlightUrl !== 'string')
    throw new Error('The World Flight package configuration is invalid.');
  updateSourceOptions();
  if (configuration.tracks !== undefined && !Array.isArray(configuration.tracks))
    throw new Error('The configured track catalog is invalid.');
  const references = configuration.tracks ?? (configuration.sourcePackageAvailable ? [{
    id: 'reference', name: 'External reference track', group: 'Reference', status: 'available',
    sourcePackageUrl: configuration.sourcePackageUrl
  }] : []);
  const ids = new Set(trackChoices.map(choice => choice.id));
  for (const track of references) {
    const id = `source:${track.id}`;
    if (typeof track.id !== 'string' || typeof track.name !== 'string' || typeof track.group !== 'string' ||
        !['available', 'unsupported'].includes(track.status) || ids.has(id) ||
        track.status === 'available' && typeof track.sourcePackageUrl !== 'string')
      throw new Error('An external track catalog entry is invalid.');
    ids.add(id);
    trackChoices.push({ ...track, id, available: track.status === 'available' });
  }
}

function replaceTrack(track) {
  try {
    dismissToast();
    cancelAnimationFrame(frame);
    audio.silence();
    input.clear();
    scene?.dispose?.();
    graphics.dispose();
    graphics = new Graphics(canvas);
    graphics.setQuality($('graphics-quality').value);
    configureTrack(track);
    ready = false;
    lastPhase = undefined;
    previousTime = undefined;
    fps = 0; fpsFrames = 0; lastSubmitted = 0; lastSkipped = 0;
    lastCallbacks = callbacks; fpsTime = performance.now();
    $('start-button').disabled = true;
    $('import-track-button').disabled = true;
    $('physics-mode').disabled = true;
    $('game-mode').disabled = true;
    $('session-mode').disabled = true;
    $('source-track-button').disabled = true;
    $('track-select').disabled = true;
    $('session-track').disabled = true;
    $('ghost-mode').disabled = true;
    document.body.dataset.ready = 'false';
    text('start-label', 'PREPARING TRACK');
    text('load-status', 'Preparing the new track GPU frame...');
    frame = requestAnimationFrame(tick);
  } catch (error) {
    fail(error);
    throw error;
  }
}

async function resume() {
  await unlockAudio();
  dismissToast();
  input.clear();
  api.Command('resume');
  state = JSON.parse(api.Observe());
  phaseChanged();
  previousTime = undefined;
  canvas.focus({ preventScroll: true });
}

function tick(now) {
  if (failed || disposed || document.hidden) return;
  try {
    const begin = performance.now();
    callbacks++;
    let dt = previousTime === undefined ? 0 : (now - previousTime) / 1000;
    previousTime = now;
    if (dt > 0.25 && state && ['racing', 'countdown'].includes(state.phase)) {
      pause();
      toast('Paused after a long browser stall. Resume when you are ready.');
      dt = 0;
    }
    activeInput = input.sample(state?.phase === 'racing' ? Math.min(dt, 0.25) : 0);
    const simulationStart = performance.now();
    state = JSON.parse(state?.flight
      ? api.FlightFrame(Math.min(dt, 0.25), activeInput.roll, activeInput.pitch, activeInput.yaw,
        activeInput.throttle, activeInput.afterburner, activeInput.brake)
      : api.Frame(Math.min(dt, 0.25), activeInput.steer, activeInput.throttle, activeInput.brake, activeInput.boost));
    simulationMs = simulationMs * 0.95 + (performance.now() - simulationStart) * 0.05;
    if (!state.player || !Number.isFinite(state.player.x) || !Number.isFinite(state.player.speed)) {
      throw new Error('The C# simulation returned an invalid vehicle snapshot.');
    }
    const { draws, camera: view } = scene.update(state, Math.min(dt, 0.1), camera, compatibility);
    const submissionStart = performance.now();
    graphics.render(draws, view, now / 1000);
    submissionMs = submissionMs * 0.95 + (performance.now() - submissionStart) * 0.05;
    if (!ready && !trackBusy && graphics.completedFrames > 0) {
      ready = true;
      clearTimeout(startupDeadline);
      setTrackBusy(false);
      text('start-label', startLabel());
      text('load-status', 'ENGINE READY / CLICK TO DRIVE');
      document.body.dataset.ready = 'true';
    }
    updateHud(Math.min(dt, 0.25));
    audio.update(state, activeInput);
    cpuMs = cpuMs * 0.95 + (performance.now() - begin) * 0.05;
    if (now - fpsTime >= 1000) {
      fps = (graphics.completedFrames - fpsFrames) * 1000 / (now - fpsTime);
      callbackRate = (callbacks - lastCallbacks) * 1000 / (now - fpsTime);
      submittedRate = (graphics.submittedFrames - lastSubmitted) * 1000 / (now - fpsTime);
      skippedRate = (graphics.skippedFrames - lastSkipped) * 1000 / (now - fpsTime);
      fpsFrames = graphics.completedFrames;
      lastCallbacks = callbacks;
      lastSubmitted = graphics.submittedFrames;
      lastSkipped = graphics.skippedFrames;
      fpsTime = now;
      if (showStats) updateStats();
    }
    frame = requestAnimationFrame(tick);
  } catch (error) { fail(error); }
}

function bind() {
  const on = (id, name) => $(id).addEventListener('click', () => { void action(name).catch(fail); });
  on('garage-button', 'garage');
  on('garage-pause', 'garage');
  on('garage-finish', 'garage');
  document.querySelector('.brand').addEventListener('click', event => {
    if (!ready || failed) return;
    event.preventDefault();
    void action('garage').catch(fail);
  });
  for (const id of ['vehicle-style', 'vehicle-quick']) {
    const select = $(id);
    for (const style of vehicleStyles) {
      const option = document.createElement('option');
      option.value = style.id;
      option.textContent = style.name;
      select.append(option);
    }
    select.value = vehicleStyle;
    select.addEventListener('change', event => {
      if (!vehicleStyles.some(style => style.id === event.target.value)) {
        toast('Unknown vehicle body style.');
        return;
      }
      vehicleStyle = event.target.value;
      $('vehicle-style').value = $('vehicle-quick').value = vehicleStyle;
      if (!state?.flight && scene) scene.setVehicleStyle(vehicleStyle);
    });
  }
  on('camera-button', 'camera');
  on('flight-hud-button', 'flight-hud');
  on('sound-button', 'mute');
  on('fullscreen-button', 'fullscreen');
  on('help-button', 'help');
  on('menu-help', 'help');
  on('pause-button', 'pause');
  on('touch-recover', 'recover');
  on('touch-gear', 'gear');
  on('flight-gear-button', 'gear');
  on('air-start-button', 'air-start');
  on('flight-air-start', 'air-start');
  on('flight-carrier-start', 'carrier-start');
  on('landmark-tour-button', 'landmark-tour');
  on('flight-landmark-tour', 'landmark-tour');
  on('touch-hook', 'hook');
  on('flight-hook-button', 'hook');
  $('close-help').addEventListener('click', () => $('help-dialog').close());
  $('help-dialog').addEventListener('close', () => canvas.focus({ preventScroll: true }));
  $('start-button').addEventListener('click', () => { void startRun().catch(fail); });
  $('resume-button').addEventListener('click', () => { void resume().catch(fail); });
  for (const id of ['restart-button', 'again-button']) {
    $(id).addEventListener('click', () => { void startRun(true).catch(fail); });
  }
  $('reload-button').addEventListener('click', () => location.reload());
  window.addEventListener('unhandledrejection', event => {
    event.preventDefault();
    fail(event.reason ?? new Error('An asynchronous game resource failed to load.'));
  });
  $('graphics-quality').addEventListener('change', event => {
    try { graphics.setQuality(event.target.value); }
    catch (error) { fail(error); }
  });
  const changeGameMode = event => { void selectGameMode(event.target.value); };
  $('game-mode').addEventListener('change', changeGameMode);
  $('session-mode').addEventListener('change', changeGameMode);
  $('world-flight-button').addEventListener('click', () => { void selectGameMode('flight-world'); });
  for (const id of ['world-departure', 'world-departure-menu']) {
    $(id).addEventListener('change', event => { void selectGameMode('flight-world', event.target.value); });
  }
  for (const button of document.querySelectorAll('.experience-picker [data-experience]')) button.addEventListener('click', () => {
    const mode = button.dataset.experience;
    void selectGameMode(mode === 'flight' && originalFlightAvailable() ? 'flight-original' : mode);
  });
  $('ghost-mode').addEventListener('change', event => {
    if (!ready || trackBusy || !['time-attack', 'stunts'].includes(currentMode()) || compatibility.sourcePhysicsActive) return;
    ghostEnabled = event.target.value === 'on';
    api.Command(ghostEnabled ? 'ghost-on' : 'ghost-off');
    state = JSON.parse(api.Observe());
  });
  const changeTrack = async event => {
    if (!ready || trackBusy) return;
    const previous = selectedTrack;
    try {
      if (capture.recorder) throw new Error('Stop recording before selecting a track.');
      pause();
      setTrackBusy(true);
      const choice = trackChoices.find(item => item.id === event.target.value);
      if (!choice?.available) throw new Error(choice?.reason ?? 'That track is unavailable.');
      const mode = currentMode();
      const flying = choice.id.startsWith('flight:');
      const special = choice.id.startsWith('experience:');
      if (flying && !state.flight && !drivingExperiences[compatibility.experience]) lastRacingTrack = previous;
      const source = choice.id.startsWith('source:');
      const classic = source && compatibility.sourcePhysicsActive === true;
      const originalFlight = choice.id === 'flight:amiga-interceptor';
      const track = await instantiateChoice(choice);
      selectedTrack = choice.id;
      if (!flying && !special && !classic && ['race', 'checkpoint-run'].includes(mode)) api.Command(`mode-${mode}`);
      replaceTrack(track);
      if ($('help-dialog').open) $('help-dialog').close();
      toast(choice.id === 'flight:world' ? 'World Flight / geographic terrain and Modern jet physics.'
        : `Loaded ${choice.name} / ${special ? 'authored driving experience' : originalFlight ? 'original Amiga flight' : flying ? 'authored jet-flight' : classic ? 'Classic reference' : 'Modern'} physics.`);
    } catch (error) {
      selectedTrack = previous;
      renderTrackChoices();
      toast(`Track selection rejected: ${error.message}`, 8000);
    } finally { setTrackBusy(false); }
  };
  $('track-select').addEventListener('change', changeTrack);
  $('session-track').addEventListener('change', changeTrack);
  $('controller-layout').addEventListener('change', event => {
    input.layout = event.target.value;
    input.clear();
  });
  $('touch-mode').addEventListener('change', applyTouchPreference);
  window.matchMedia('(any-pointer: coarse)').addEventListener('change', applyTouchPreference);
  window.addEventListener('pointerdown', event => {
    if (event.pointerType === 'touch' && !touchSeen) {
      touchSeen = true;
      applyTouchPreference();
    }
  }, { capture: true });
  $('physics-mode').addEventListener('change', async event => {
    if (!ready || trackBusy) return;
    const classic = event.target.value === 'classic';
    try {
      if (capture.recorder) throw new Error('Stop recording before changing physics.');
      pause();
      setTrackBusy(true);
      const flying = Boolean(state.flight);
      const original = event.target.value === 'flight-original';
      const track = flying ? original ? await loadOriginalFlight() : JSON.parse(api.SelectFlight())
        : compatibility.sourcePackageLoaded ? JSON.parse(api.SetSourcePhysics(classic)) : await loadHostedSource(classic);
      selectedTrack = flying ? original ? 'flight:amiga-interceptor' : 'flight:interceptor-bay' : cachedSourceChoice();
      replaceTrack(track);
      $('help-dialog').close();
      toast(flying ? original ? 'Original Amiga flight rules and verified user-disk data selected.' : 'Modern authored jet model selected.'
        : classic ? 'Classic/reference physics selected. Stock-Amiga parity remains unverified.'
        : 'Modern responsive physics selected on the same reference track.');
    } catch (error) {
      event.target.value = compatibility.flightSourceActive ? 'flight-original' : compatibility.sourcePhysicsActive ? 'classic' : 'modern';
      toast(`Physics change rejected: ${error.message}`, 8000);
    } finally { setTrackBusy(false); }
  });
  $('original-flight-button').addEventListener('click', async () => {
    if (!ready || trackBusy) return;
    try {
      if (capture.recorder) throw new Error('Stop recording before changing vehicle.');
      pause(); setTrackBusy(true);
      if (!state.flight) lastRacingTrack = selectedTrack;
      const world = await loadOriginalFlight();
      selectedTrack = 'flight:amiga-interceptor';
      replaceTrack(world);
      $('help-dialog').close();
      toast('Original Amiga flight model and actual user-disk world loaded.');
    } catch (error) { if (!failed) toast(`Original flight rejected: ${error.message}`, 8000); }
    finally { setTrackBusy(false); }
  });
  $('import-flight-button').addEventListener('click', () => {
    if (!ready || trackBusy) return;
    if (capture.recorder) { toast('Stop recording before loading an original flight disk.'); return; }
    pause();
    $('flight-file').click();
  });
  $('flight-file').addEventListener('change', async event => {
    const file = event.target.files[0];
    if (!file) return;
    if (!ready || trackBusy) { toast('Wait for the current package to finish loading.'); event.target.value = ''; return; }
    try {
      if (capture.recorder) throw new Error('Stop recording before loading a disk.');
      setTrackBusy(true);
      const json = file.name.toLowerCase().endsWith('.json');
      if (file.size > (json ? 16 : 2) * 1024 * 1024) throw new Error('Original disks/ZIPs are limited to two MiB; packages to sixteen MiB.');
      if (!state.flight) lastRacingTrack = selectedTrack;
      let world;
      if (json) world = JSON.parse(api.LoadFlightPackage(await file.text()));
      else {
        const bytes = new Uint8Array(await file.arrayBuffer());
        let binary = '';
        for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
        world = JSON.parse(api.LoadFlightDisk(btoa(binary), file.name));
      }
      selectedTrack = 'flight:amiga-interceptor';
      replaceTrack(world);
      $('help-dialog').close();
      toast('Original flight disk verified and loaded locally. Nothing was uploaded.');
    } catch (error) { if (!failed) toast(`Original disk rejected: ${error.message}`, 9000); }
    finally { event.target.value = ''; setTrackBusy(false); }
  });
  $('source-track-button').addEventListener('click', async () => {
    if (!ready || trackBusy) return;
    try {
      if (capture.recorder) throw new Error('Stop recording before changing track data.');
      pause();
      setTrackBusy(true);
      const track = await loadHostedSource(compatibility.sourcePhysicsActive === true);
      replaceTrack(track);
      $('help-dialog').close();
      toast('External reference track loaded. Modern remains the default physics model.');
    } catch (error) { toast(`Reference track rejected: ${error.message}`, 8000); }
    finally { setTrackBusy(false); }
  });
  $('import-track-button').addEventListener('click', () => {
    if (!ready || trackBusy) return;
    if (capture.recorder) { toast('Stop the recording before changing track data.'); return; }
    pause();
    $('track-file').click();
  });
  $('track-file').addEventListener('change', async event => {
    const file = event.target.files[0];
    if (!file) return;
    if (!ready || trackBusy) {
      toast('Wait for the current track to finish loading.');
      event.target.value = '';
      return;
    }
    try {
      if (capture.recorder) throw new Error('Stop the recording before changing track data.');
      setTrackBusy(true);
      if (file.size > 16 * 1024 * 1024) throw new Error('Track JSON files must not exceed 16 MiB.');
      const contents = await file.text();
      const header = JSON.parse(contents);
      const source = header.kind === 'stunt-amiga-source-runtime';
      const track = JSON.parse(source
        ? api.LoadSourcePackage(contents, compatibility.sourcePhysicsActive === true)
        : api.LoadPrivateGeometry(contents));
      selectedTrack = source ? 'local:source' : 'local:geometry';
      if (source) { sourcePackageText = contents; sourcePackageId = 'local'; }
      try { replaceTrack(track); }
      catch (error) { fail(error); return; }
      $('help-dialog').close();
      toast(source ? 'Local source-runtime package loaded. The selected physics model is shown in diagnostics.'
        : 'Local track geometry loaded. Physics remain the authored prototype, not original Amiga dynamics.', 8000);
    } catch (error) {
      toast(`Track import rejected: ${error.message}`, 8000);
    } finally { event.target.value = ''; setTrackBusy(false); }
  });
  $('reset-track-button').addEventListener('click', () => {
    if (!ready || trackBusy) return;
    if (capture.recorder) { toast('Stop the recording before changing track data.'); return; }
    pause();
    try {
      setTrackBusy(true);
      const track = JSON.parse(api.ResetAuthoredTrack());
      replaceTrack(track);
      $('help-dialog').close();
      toast('Authored circuit restored.');
    } catch (error) { fail(error); }
    finally { setTrackBusy(false); }
  });
  $('capture-button').addEventListener('click', async () => {
    if (capture.recorder) {
      capture.stop();
      canvas.focus({ preventScroll: true });
      return;
    }
    await unlockAudio();
    if (soundBlocked) return;
    try {
      capture.start(canvas, audio, downloaded => {
        $('capture-button').classList.remove('recording');
        text('capture-button', 'REC');
        if (downloaded) toast('Recording downloaded. The video contains the game canvas and game audio.');
      }, error => toast(error.message));
      $('capture-button').classList.add('recording');
      text('capture-button', 'STOP REC');
      toast('Recording the game canvas and game audio. Click STOP REC to download.', 3000);
      canvas.focus({ preventScroll: true });
    } catch (error) { toast(error.message); }
  });
  canvas.addEventListener('webglcontextlost', event => {
    event.preventDefault();
    fail(new Error('The WebGL context was lost. Reload to recreate GPU resources.'));
  });
  document.addEventListener('fullscreenchange', () => {
    $('fullscreen-button').setAttribute('aria-pressed', String(Boolean(document.fullscreenElement)));
  });
  window.addEventListener('blur', () => {
    if (!failed) pause();
  });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      pause();
      cancelAnimationFrame(frame);
      previousTime = undefined;
    } else if (api && !failed) {
      previousTime = undefined;
      frame = requestAnimationFrame(tick);
    }
  });
  window.addEventListener('pagehide', () => {
    disposed = true;
    cancelAnimationFrame(frame);
    capture.stop();
    input?.dispose();
    scene?.dispose?.();
    graphics?.dispose();
    void audio.dispose();
  }, { once: true });
}

async function boot() {
  bind();
  startupDeadline = setTimeout(() => fail(new Error(
    'The WebAssembly engine did not finish loading. Check the connection and reload the game.')), 60000);
  graphics = new Graphics(canvas);
  input = new Input(canvas, name => { void action(name).catch(fail); }, pad => {
    updateInputLabel();
    if (pad) {
      toast(pad.mapping === 'standard' ? 'Controller connected. RT accelerates, LT brakes, A boosts.'
        : 'Controller detected. Open controls and choose Classic joystick if its mapping is non-standard.');
    }
  });
  applyTouchPreference();
  text('load-status', 'Loading .NET WebAssembly. The first run may take a moment...');
  const { dotnet } = await import('./_framework/dotnet.js');
  runtime = await dotnet.withDiagnosticTracing(false).create();
  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  api = exports.StuntRacer?.Browser?.Browser;
  if (!api?.Initialize || !api?.Frame || !api?.Command || !api?.Observe ||
      !api?.DescribeCompatibility || !api?.LoadPrivateGeometry || !api?.ResetAuthoredTrack ||
      !api?.LoadSourcePackage || !api?.SetSourcePhysics || !api?.AuthoredTracks || !api?.SelectAuthoredTrack ||
      !api?.SelectFlight || !api?.SelectRacing || !api?.FlightFrame ||
      !api?.SelectOriginalFlight || !api?.LoadFlightPackage || !api?.LoadFlightDisk || !api?.SelectExperience ||
      !api?.SelectWorldFlight || !api?.AddWorldTerrainTile || !api?.RemoveWorldTerrainTile) {
    throw new Error('The compiled C# simulation exports are missing. No JavaScript gameplay fallback was started.');
  }
  await runtime.runMain();
  trackChoices = JSON.parse(api.AuthoredTracks()).map(choice => ({
    ...choice, id: `included:${choice.id}`, available: true
  }));
  for (const [mode, details] of Object.entries(drivingExperiences))
    trackChoices.push({ id: `experience:${mode}`, name: details.course, group: details.group,
      available: true, description: details.description });
  trackChoices.push({ id: 'flight:interceptor-bay', name: 'Interceptor Bay / jet flight',
    group: 'Flight activity', available: true, description: 'Authored free-flight prototype; not original Amiga dynamics.' });
  trackChoices.push({ id: 'flight:amiga-interceptor', name: 'Amiga Interceptor / original flight',
    group: 'Flight activity', available: false, reason: 'Load an original flight ADF/ZIP or configure the external original-flight package.' });
  trackChoices.push({ id: 'flight:world', name: 'World Flight / United States',
    group: 'Flight activity', available: false, reason: 'This host has not configured a World Flight terrain package.' });
  await configureSourceAvailability();
  const requestedPhysics = new URLSearchParams(location.search).get('physics');
  const track = requestedPhysics === 'classic' ? await loadHostedSource(true) : JSON.parse(api.Initialize());
  configureTrack(track);
  globalThis.stuntRacer = Object.freeze({
    observe: () => structuredClone(state),
    track: () => structuredClone(scene.track),
    world: () => scene.world ? structuredClone(scene.world) : null,
    diagnostics: () => ({
      ready, failed, runtime: '.NET 10 browser-WASM', mainAssembly: config.mainAssemblyName,
      renderer: graphics.device, completedFrames: graphics.completedFrames,
      submittedFrames: graphics.submittedFrames, completedFramesPerSecond: fps,
      mainThreadMilliseconds: cpuMs, gpuMilliseconds: graphics.gpuMilliseconds,
      simulationMilliseconds: simulationMs, submissionMilliseconds: submissionMs,
      callbackRate, submittedRate, skippedRate, pendingFrames: graphics.pendingFrames,
      skippedFrames: graphics.skippedFrames, quality: graphics.quality,
      resolution: { width: canvas.width, height: canvas.height },
      audioState: audio.context?.state ?? 'locked', audioRms: audio.level(),
      audioEvents: audio.events, muted: audio.muted, camera,
      flightHud: document.body.dataset.flightHud !== 'off',
      controller: input.connected, recording: capture.recorder?.state ?? 'inactive',
      vehicleStyle,
      track: { name: scene.track.name, length: scene.track.length, points: scene.track.points.length },
      compatibility: { ...compatibility },
      sourceConfigurationAvailable: sourceConfiguration.sourcePackageAvailable,
      originalFlightConfigurationAvailable: sourceConfiguration.flightPackageAvailable === true,
      worldFlightConfigurationAvailable: Boolean(sourceConfiguration.worldFlightUrl),
      geographicFlight: scene.terrain ? scene.diagnostics() : null,
      flightWorld: state.flight && scene.world ? {
        sourceSurfaces: scene.world.sourceWorld?.surfaces.length ?? 0,
        sourceObjects: scene.world.sourceWorld?.objects.length ?? 0,
        landingAreas: scene.world.landingSurfaces.length,
      } : null,
      cityWorld: state.city && scene.world ? {
        roads: scene.world.roads.length, buildings: scene.world.buildings.length,
        traffic: state.city.traffic.length, pedestrians: state.city.pedestrians.length,
      } : null,
      spatialDriving: scene.track.isSpatial === true,
      selectedTrack, trackChoices: trackChoices.map(({ id, name, group, available, reason }) => ({ id, name, group, available, reason })),
    }),
  });
  text('load-status', 'Preparing the first GPU frame...');
  fpsTime = performance.now();
  frame = requestAnimationFrame(tick);
}

void boot().catch(fail);
