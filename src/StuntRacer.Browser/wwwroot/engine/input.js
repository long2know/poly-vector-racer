import { clamp } from './math.js';

export function deadzone(value, threshold = 0.13) {
  if (!Number.isFinite(value)) return 0;
  return Math.abs(value) <= threshold ? 0
    : Math.sign(value) * clamp((Math.abs(value) - threshold) / (1 - threshold), 0, 1);
}

export function readGamepad(pad, layout = 'standard') {
  const button = index => clamp(pad?.buttons[index]?.value ?? 0, 0, 1);
  const axis = deadzone(pad?.axes[0] ?? 0);
  const vertical = deadzone(pad?.axes[1] ?? 0);
  return {
    steer: clamp(axis + button(15) - button(14), -1, 1),
    throttle: layout === 'joystick' ? Math.max(0, -vertical) : Math.max(button(7), button(12)),
    brake: layout === 'joystick' ? Math.max(0, vertical) : Math.max(button(6), button(13)),
    boost: button(0) > 0.5 || button(5) > 0.5,
    pause: button(9) > 0.5,
    recover: button(1) > 0.5,
    camera: button(3) > 0.5,
  };
}

export function readFlightGamepad(pad) {
  const button = index => clamp(pad?.buttons[index]?.value ?? 0, 0, 1);
  return {
    steer: deadzone(pad?.axes[0] ?? 0), pitch: deadzone(pad?.axes[1] ?? 0),
    yaw: deadzone(pad?.axes[2] ?? 0), throttle: button(7), brake: button(6),
    boost: button(0) > 0.5 || button(5) > 0.5, airBrake: button(2) > 0.5,
    pause: button(9) > 0.5, recover: button(1) > 0.5, camera: button(3) > 0.5,
    gear: button(10) > 0.5,
    hook: button(4) > 0.5,
    scan: button(11) > 0.5,
  };
}

const drivingKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight',
  'KeyW', 'KeyA', 'KeyS', 'KeyD', 'Space', 'ShiftLeft', 'ShiftRight']);
const flightKeys = new Set(['KeyQ', 'KeyE', 'KeyB', 'Equal', 'Minus', 'NumpadAdd', 'NumpadSubtract', 'Backspace']);
const actions = { KeyC: 'camera', KeyR: 'recover', KeyP: 'pause', Escape: 'pause',
  KeyM: 'mute', KeyF: 'fullscreen', Enter: 'start', KeyH: 'help', KeyG: 'gear', KeyK: 'hook', KeyV: 'flight-hud',
  KeyT: 'scan', F3: 'stats' };
const flightViews = ['rear-left', 'rear', 'rear-right', 'left', 'forward', 'right', 'forward-left', 'forward', 'forward-right'];
const throttleTrim = { 'throttle-up': 1, 'throttle-down': -1 };
const boostsOnSlide = control => control === 'throttle' || control === 'throttle-up';

export class Input {
  keys = new Set();
  touches = new Set();
  touchPointers = new Map();
  turboPointers = new Set();
  touchSteer = 0;
  touchPitch = 0;
  steeringPointer = null;
  touchEnabled = true;
  mode = 'racing';
  scanEnabled = false;
  flightThrottle = 0;
  pendingTouchTrim = 0;
  previous = {};
  layout = 'standard';
  connected = null;
  blocked = false;
  events = new AbortController();

  constructor(canvas, onAction, onDevice) {
    this.canvas = canvas;
    this.onAction = onAction;
    this.onDevice = onDevice;
    const signal = this.events.signal;
    window.addEventListener('keydown', event => {
      if (event.target instanceof Element && event.target.closest('button, input, select, textarea')) return;
      if (this.mode === 'flight' && !event.altKey && !event.ctrlKey && !event.metaKey) {
        const digit = /^(?:Digit|Numpad)([1-9])$/.exec(event.code);
        if (digit) {
          event.preventDefault();
          if (!event.repeat) onAction('flight-view', flightViews[Number(digit[1]) - 1]);
          return;
        }
      }
      const action = actions[event.code];
      if (action === 'flight-hud' && this.mode !== 'flight') return;
      if (action === 'scan' && (!this.scanEnabled || event.altKey || event.ctrlKey || event.metaKey)) return;
      if (drivingKeys.has(event.code) || action ||
          this.mode === 'flight' && flightKeys.has(event.code)) event.preventDefault();
      this.keys.add(event.code);
      if (!event.repeat && action) onAction(action);
    }, { signal });
    window.addEventListener('keyup', event => this.keys.delete(event.code), { signal });
    window.addEventListener('blur', () => this.clear(), { signal });
    document.addEventListener('visibilitychange', () => { if (document.hidden) this.clear(); }, { signal });
    this.touchButtons = Array.from(document.querySelectorAll('[data-control]'));
    for (const button of this.touchButtons) {
      button.addEventListener('pointerdown', event => {
        if (!this.touchEnabled) return;
        event.preventDefault();
        button.setPointerCapture(event.pointerId);
        const control = button.dataset.control;
        if (this.mode === 'flight' && !this.touches.has(control))
          this.pendingTouchTrim += throttleTrim[control] ?? 0;
        this.touchPointers.set(event.pointerId, { control, element: button, startY: event.clientY });
        this.syncTouch();
        canvas.focus({ preventScroll: true });
      }, { signal });
      button.addEventListener('pointermove', event => {
        const pointer = this.touchPointers.get(event.pointerId);
        if (!pointer || !boostsOnSlide(pointer.control)) return;
        if (pointer.startY - event.clientY >= 28) this.turboPointers.add(event.pointerId);
        else this.turboPointers.delete(event.pointerId);
        this.syncTouch();
      }, { signal });
      const release = event => {
        this.touchPointers.delete(event.pointerId);
        this.turboPointers.delete(event.pointerId);
        this.syncTouch();
      };
      button.addEventListener('pointerup', release, { signal });
      button.addEventListener('pointercancel', release, { signal });
      button.addEventListener('lostpointercapture', release, { signal });
    }
    this.steeringPad = document.getElementById?.('touch-steering');
    this.steeringThumb = document.getElementById?.('touch-steering-thumb');
    if (this.steeringPad) {
      const steer = event => {
        if (event.pointerId !== this.steeringPointer) return;
        const rect = this.steeringPad.getBoundingClientRect();
        this.touchSteer = deadzone(clamp((event.clientX - rect.left - rect.width / 2) /
          Math.max(1, rect.width * 0.42), -1, 1), 0.04);
        this.touchPitch = this.mode === 'flight' ? deadzone(clamp((rect.top + rect.height / 2 - event.clientY) /
          Math.max(1, rect.height * 0.42), -1, 1), 0.04) : 0;
        this.drawTouchSteering();
      };
      this.steeringPad.addEventListener('pointerdown', event => {
        if (!this.touchEnabled || this.steeringPointer !== null) return;
        event.preventDefault();
        this.steeringPointer = event.pointerId;
        this.steeringPad.setPointerCapture(event.pointerId);
        steer(event);
        canvas.focus({ preventScroll: true });
      }, { signal });
      this.steeringPad.addEventListener('pointermove', steer, { signal });
      const releaseSteering = event => {
        if (event.pointerId !== this.steeringPointer) return;
        this.steeringPointer = null;
        this.touchSteer = 0;
        this.touchPitch = 0;
        this.drawTouchSteering();
      };
      for (const name of ['pointerup', 'pointercancel', 'lostpointercapture'])
        this.steeringPad.addEventListener(name, releaseSteering, { signal });
    }
    window.addEventListener('resize', () => this.clearTouch(), { signal });
  }

  syncTouch() {
    this.touches.clear();
    for (const pointer of this.touchPointers.values()) this.touches.add(pointer.control);
    if (this.turboPointers.size) this.touches.add('boost');
    for (const button of this.touchButtons) {
      button.classList?.toggle('is-held', Array.from(this.touchPointers.values()).some(p => p.element === button));
      button.classList?.toggle('touch-turbo-active', boostsOnSlide(button.dataset.control) &&
        Array.from(this.turboPointers).some(id => this.touchPointers.get(id)?.element === button));
    }
  }

  drawTouchSteering() {
    const travel = this.steeringPad ? Math.max(1, (this.steeringPad.getBoundingClientRect().width - 48) / 2) : 42;
    const vertical = this.steeringPad ? Math.max(1, (this.steeringPad.getBoundingClientRect().height - 48) / 2) : 0;
    if (this.steeringThumb) this.steeringThumb.style.transform = this.mode === 'flight'
      ? `translate(${this.touchSteer * travel}px,${-this.touchPitch * vertical}px)`
      : `translateX(${this.touchSteer * travel}px)`;
    this.steeringPad?.setAttribute('aria-valuenow', this.touchSteer.toFixed(2));
  }

  clearTouch() {
    this.touchPointers.clear();
    this.turboPointers.clear();
    this.pendingTouchTrim = 0;
    this.steeringPointer = null;
    this.touchSteer = 0;
    this.touchPitch = 0;
    this.syncTouch();
    this.drawTouchSteering();
  }

  setTouchEnabled(enabled) {
    this.touchEnabled = Boolean(enabled);
    if (!this.touchEnabled) this.clearTouch();
  }

  setMode(mode, throttle = 0, scanner = false) {
    if (!['racing', 'flight'].includes(mode)) throw new Error(`Unknown input mode: ${mode}`);
    this.mode = mode;
    this.scanEnabled = mode === 'flight' && scanner;
    this.setFlightThrottle(throttle);
    this.clear();
  }

  setFlightThrottle(value) {
    if (!Number.isFinite(value)) throw new Error('Flight throttle must be finite.');
    this.flightThrottle = clamp(value, 0, 1);
  }

  clear() {
    this.keys.clear();
    this.clearTouch();
    this.previous = {};
    this.blocked = true;
  }

  sample(seconds = 0) {
    const pads = navigator.getGamepads?.() ?? [];
    const pad = Array.from(pads).find(item => item?.connected) ?? null;
    const id = pad ? `${pad.index}:${pad.id}` : null;
    if (id !== this.connected) {
      this.connected = id;
      this.previous = {};
      this.onDevice(pad);
    }
    let gamepad = this.mode === 'flight' ? readFlightGamepad(pad) : readGamepad(pad, this.layout);
    if (this.blocked) {
      const active = Math.abs(gamepad.steer) + gamepad.throttle + gamepad.brake
        + Number(gamepad.boost) + Number(gamepad.pause) + Number(gamepad.recover) + Number(gamepad.camera);
      const flightActive = this.mode === 'flight'
        ? Math.abs(gamepad.pitch) + Math.abs(gamepad.yaw) + Number(gamepad.gear) + Number(gamepad.hook)
          + Number(gamepad.airBrake) + Number(this.scanEnabled && gamepad.scan) : 0;
      if (active + flightActive < 0.001) this.blocked = false;
      else gamepad = this.mode === 'flight' ? readFlightGamepad(null) : readGamepad(null);
    }
    for (const action of this.mode === 'flight' ? ['pause', 'recover', 'camera', 'gear', 'hook'] : ['pause', 'recover', 'camera']) {
      if (gamepad[action] && !this.previous[action]) this.onAction(action);
    }
    if (this.scanEnabled && gamepad.scan && !this.previous.scan) this.onAction('scan');
    this.previous = gamepad;
    const held = (...codes) => codes.some(code => this.keys.has(code));
    let steer = Number(held('ArrowRight', 'KeyD') || this.touches.has('right'))
      - Number(held('ArrowLeft', 'KeyA') || this.touches.has('left'));
    if (Math.abs(this.touchSteer) > Math.abs(steer)) steer = this.touchSteer;
    if (this.mode === 'flight') {
      if (!Number.isFinite(seconds) || seconds < 0) throw new Error('Flight input time must be finite and nonnegative.');
      // An action such as pause can clear the controls during this sample.
      if (this.blocked) gamepad = readFlightGamepad(null);
      const pitch = Number(held('ArrowDown', 'KeyS')) - Number(held('ArrowUp', 'KeyW'));
      const demandedPitch = Math.abs(pitch) > Math.abs(this.touchPitch) ? pitch : this.touchPitch;
      const throttleRate = Number(held('Equal', 'NumpadAdd')) - Number(held('Minus', 'NumpadSubtract'))
        + gamepad.throttle - gamepad.brake
        + Number(this.touches.has('throttle-up')) - Number(this.touches.has('throttle-down'));
      const touchStep = seconds > 0 ? this.pendingTouchTrim * 0.05 : 0;
      this.pendingTouchTrim = 0;
      this.flightThrottle = clamp(this.flightThrottle + touchStep + throttleRate * Math.min(seconds, 0.25) * 0.5, 0, 1);
      return {
        roll: Math.abs(steer) > Math.abs(gamepad.steer) ? steer : gamepad.steer,
        pitch: Math.abs(demandedPitch) > Math.abs(gamepad.pitch) ? demandedPitch : gamepad.pitch,
        yaw: held('KeyE') ? 1 : held('KeyQ') ? -1 : gamepad.yaw,
        throttle: this.flightThrottle,
        afterburner: held('Space', 'ShiftLeft', 'ShiftRight') || this.touches.has('boost') || gamepad.boost,
        brake: held('Backspace', 'KeyB') || this.touches.has('brake') || gamepad.airBrake,
      };
    }
    return {
      steer: Math.abs(steer) > Math.abs(gamepad.steer) ? steer : gamepad.steer,
      throttle: Math.max(Number(held('ArrowUp', 'KeyW') || this.touches.has('throttle')), gamepad.throttle),
      brake: Math.max(Number(held('ArrowDown', 'KeyS') || this.touches.has('brake')), gamepad.brake),
      boost: held('Space', 'ShiftLeft', 'ShiftRight') || this.touches.has('boost') || gamepad.boost,
    };
  }

  dispose() {
    this.events.abort();
    this.clear();
  }
}
