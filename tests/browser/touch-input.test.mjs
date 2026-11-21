import assert from 'node:assert/strict';
import test from 'node:test';
import { Input, readFlightGamepad } from '../../src/StuntRacer.Browser/wwwroot/engine/input.js';

class TouchElement extends EventTarget {
  constructor(control) {
    super();
    this.dataset = { control };
    this.style = {};
    this.attributes = {};
    this.classList = { toggle() {} };
  }
  setPointerCapture() {}
  setAttribute(name, value) { this.attributes[name] = value; }
  getBoundingClientRect() { return { left: 0, top: 0, width: 160, height: 80 }; }
}

function withTouch(check) {
  const names = ['window', 'document', 'navigator', 'Element'];
  const saved = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const buttons = Object.fromEntries(['left', 'right', 'throttle', 'throttle-up', 'throttle-down', 'brake', 'boost']
    .map(name => [name, new TouchElement(name)]));
  const pad = new TouchElement(), thumb = new TouchElement();
  const actions = [];
  let input, gamepad = null;
  try {
    for (const [name, value] of Object.entries({
      window: new EventTarget(),
      document: Object.assign(new EventTarget(), {
        querySelectorAll: () => Object.values(buttons),
        getElementById: id => id === 'touch-steering' ? pad : id === 'touch-steering-thumb' ? thumb : null,
      }),
      navigator: { getGamepads: () => [gamepad] },
      Element: TouchElement,
    })) Object.defineProperty(globalThis, name, { value, writable: true, configurable: true });
    input = new Input({ focus() {} }, (...action) => actions.push(action), () => {});
    check(input, buttons, pad, thumb, { actions, setGamepad: value => { gamepad = value; } });
  } finally {
    input?.dispose();
    for (const [name, descriptor] of saved) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
}

function pointer(element, type, id, x = 80, y = 200) {
  element.dispatchEvent(Object.assign(new Event(type, { cancelable: true }),
    { pointerId: id, clientX: x, clientY: y }));
}

test('only training captures scan keys and right-stick clicks, once per press', () => {
  withTouch((input, _buttons, _pad, _thumb, { actions, setGamepad }) => {
    const key = properties => window.dispatchEvent(Object.assign(new Event('keydown', { cancelable: true }),
      { code: 'KeyT', ...properties }));
    const pad = clicked => ({ index: 0, id: 'Scan controller', connected: true, axes: [0, 0, 0],
      buttons: Array.from({ length: 17 }, (_, i) => ({ value: i === 11 && clicked ? 1 : 0 })) });
    input.setMode('flight');
    input.sample();
    assert.equal(key(), true, 'normal flight does not capture T');
    setGamepad(pad(true));
    input.sample();
    assert.deepEqual(actions, []);

    input.setMode('flight', .35, true);
    input.sample();
    assert.deepEqual(actions, [], 'a held stick click cannot carry into training');
    setGamepad(pad(false));
    input.sample();
    setGamepad(pad(true));
    input.sample();
    input.sample();
    assert.equal(key({ ctrlKey: true }), true, 'browser Ctrl+T stays available');
    assert.equal(key(), false);
    key({ repeat: true });
    assert.deepEqual(actions, [['scan'], ['scan']], 'one scan per gamepad or keyboard press');
    input.setMode('racing');
    assert.equal(key(), true, 'driving does not capture T');
  });
});

test('two thumbs can steer and accelerate, with slide-up turbo and independent release', () => {
  withTouch((input, buttons, pad) => {
    pointer(pad, 'pointerdown', 1, 15, 20);
    pointer(buttons.throttle, 'pointerdown', 2);
    let controls = input.sample();
    assert.ok(controls.steer < -0.8);
    assert.equal(controls.throttle, 1);
    assert.equal(controls.boost, false);
    pointer(buttons.throttle, 'pointermove', 2, 80, 160);
    assert.equal(input.sample().boost, true);
    pointer(buttons.throttle, 'pointermove', 2, 80, 195);
    assert.equal(input.sample().boost, false);
    assert.equal(input.sample().throttle, 1);
    pointer(buttons.brake, 'pointerdown', 3);
    assert.equal(input.sample().brake, 1);
    pointer(buttons.brake, 'pointercancel', 3);
    assert.equal(input.sample().brake, 0);
    pointer(buttons.throttle, 'pointerup', 2);
    controls = input.sample();
    assert.equal(controls.throttle, 0);
    assert.ok(controls.steer < -0.8);
    pointer(pad, 'lostpointercapture', 1);
    assert.equal(input.sample().steer, 0);
  });
});

test('releasing one of two contacts on the same pedal does not release the other finger', () => {
  withTouch((input, buttons) => {
    pointer(buttons.throttle, 'pointerdown', 10);
    pointer(buttons.throttle, 'pointerdown', 11);
    pointer(buttons.throttle, 'pointerup', 10);
    assert.equal(input.sample().throttle, 1);
    pointer(buttons.throttle, 'lostpointercapture', 11);
    assert.equal(input.sample().throttle, 0);
    pointer(buttons.left, 'pointerdown', 12);
    pointer(buttons.right, 'pointerdown', 13);
    assert.equal(input.sample().steer, 0);
    pointer(buttons.left, 'pointerup', 12);
    assert.equal(input.sample().steer, 1);
  });
});

test('focus loss, orientation resize and explicit touch-off clear controls and thumb state', () => {
  withTouch((input, buttons, pad, thumb) => {
    pointer(pad, 'pointerdown', 1, 150, 20);
    pointer(buttons.throttle, 'pointerdown', 2);
    pointer(buttons.throttle, 'pointermove', 2, 80, 150);
    window.dispatchEvent(new Event('blur'));
    assert.deepEqual(input.sample(), { steer: 0, throttle: 0, brake: 0, boost: false });
    assert.equal(thumb.style.transform, 'translateX(0px)');
    assert.equal(pad.attributes['aria-valuenow'], '0.00');
    pointer(buttons.throttle, 'pointerdown', 3);
    window.dispatchEvent(new Event('resize'));
    assert.equal(input.sample().throttle, 0);
    pointer(buttons.boost, 'pointerdown', 4);
    input.setTouchEnabled(false);
    pointer(buttons.throttle, 'pointerdown', 5);
    assert.deepEqual(input.sample(), { steer: 0, throttle: 0, brake: 0, boost: false });
    input.setTouchEnabled(true);
    pointer(buttons.throttle, 'pointerdown', 6);
    assert.equal(input.sample().throttle, 1);
  });
});

test('flight touch trims persistent power both ways while pitch, bank, brake and afterburner stay independent', () => {
  withTouch((input, buttons, pad) => {
    input.setMode('flight', 0.4);
    pointer(pad, 'pointerdown', 1, 140, 8);
    pointer(buttons['throttle-up'], 'pointerdown', 2);
    let flight = input.sample(0.25);
    assert.ok(flight.roll > 0.8 && flight.pitch > 0.8, 'drag right/up must bank right and pull up');
    const raised = flight.throttle;
    assert.ok(raised > 0.4 && raised < 0.7, 'flight power adjusts instead of jumping to full');
    pointer(buttons['throttle-up'], 'pointermove', 2, 80, 160);
    assert.equal(input.sample().afterburner, true);
    pointer(pad, 'pointercancel', 1);
    flight = input.sample();
    assert.equal(flight.roll, 0);
    assert.equal(flight.pitch, 0);
    pointer(buttons['throttle-up'], 'pointerup', 2);
    flight = input.sample();
    assert.equal(flight.afterburner, false);
    assert.equal(flight.throttle, raised, 'releasing power up must leave the new setting');
    pointer(buttons['throttle-down'], 'pointerdown', 3);
    pointer(buttons.brake, 'pointerdown', 4);
    flight = input.sample(0.16);
    assert.equal(flight.brake, true);
    assert.equal(flight.afterburner, false);
    assert.ok(flight.throttle < raised && flight.throttle > 0);
    const lowered = flight.throttle;
    pointer(buttons['throttle-down'], 'pointerup', 3);
    assert.equal(input.sample(0.25).throttle, lowered, 'holding the brake must not also move the lever');
    pointer(buttons['throttle-up'], 'pointerdown', 5);
    pointer(buttons['throttle-up'], 'pointerup', 5);
    const tapped = input.sample(1 / 60).throttle;
    assert.ok(tapped > lowered, 'a tap between frames must still trim power');
    pointer(buttons['throttle-down'], 'pointerdown', 6);
    window.dispatchEvent(new Event('blur'));
    assert.equal(input.sample(0.25).throttle, tapped, 'blur releases held trim, not the set power');
    assert.equal(input.sample().brake, false);
    input.setMode('racing');
    pointer(pad, 'pointerdown', 7, 140, 8);
    pointer(buttons.throttle, 'pointerdown', 8);
    assert.equal(input.sample().pitch, undefined, 'flight axes must not leak into racing');
    assert.equal(input.sample().throttle, 1, 'racing still uses a full-power pedal');
  });
});

test('flight keyboard throttle is elapsed-time based and pulling back is nose-up', () => {
  withTouch(input => {
    input.setMode('flight');
    const key = (type, code) => window.dispatchEvent(Object.assign(new Event(type), { code }));
    key('keydown', 'Equal');
    key('keydown', 'KeyS');
    const flight = input.sample(0.25);
    assert.equal(flight.throttle, 0.125);
    assert.equal(flight.pitch, 1);
    key('keyup', 'Equal');
    key('keyup', 'KeyS');
    key('keydown', 'KeyQ');
    assert.equal(input.sample().yaw, -1);
    window.dispatchEvent(new Event('blur'));
    assert.equal(input.sample().yaw, 0);
    assert.equal(input.sample().throttle, 0.125, 'focus release clears held controls, not the deliberate lever setting');
    key('keydown', 'Minus');
    assert.equal(input.sample(0.125).throttle, 0.0625);
    key('keyup', 'Minus');
    assert.equal(input.sample(0.25).throttle, 0.0625);
  });
});

test('number row and numpad snap the flight view without changing flight axes or capturing racing digits', () => {
  withTouch((input, _buttons, _pad, _thumb, { actions }) => {
    const key = (code, options = {}) => window.dispatchEvent(Object.assign(new Event('keydown', { cancelable: true }), { code, ...options }));
    assert.equal(key('Digit4'), true, 'racing does not consume flight look keys');
    input.setMode('flight', 0.3);
    assert.equal(key('Digit4'), false);
    key('Numpad6');
    key('Digit2');
    key('Numpad9');
    key('Digit1');
    key('Numpad5');
    key('Digit5', { repeat: true });
    assert.equal(key('Digit1', { ctrlKey: true }), true, 'do not override browser shortcuts');
    assert.deepEqual(actions, [
      ['flight-view', 'left'], ['flight-view', 'right'], ['flight-view', 'rear'],
      ['flight-view', 'forward-right'], ['flight-view', 'rear-left'], ['flight-view', 'forward'],
    ]);
    assert.deepEqual(input.sample(0.2), { roll: 0, pitch: 0, yaw: 0, throttle: 0.3, afterburner: false, brake: false });
    key('KeyD');
    key('KeyQ');
    const { roll, yaw } = input.sample();
    assert.deepEqual({ roll, yaw }, { roll: 1, yaw: -1 }, 'roll and rudder remain independent while looking around');
  });
});

test('flight gamepad trims throttle in either direction and ignores held triggers during pause and focus loss', () => {
  withTouch((input, _buttons, _pad, _thumb, { setGamepad }) => {
    const buttons = Array.from({ length: 17 }, () => ({ value: 0 }));
    setGamepad({ connected: true, axes: [0, 0, 0], buttons });
    input.setMode('flight', 0.35);
    assert.equal(input.sample().throttle, 0.35);
    buttons[7].value = 0.7;
    const raised = input.sample(0.25).throttle;
    assert.ok(raised > 0.35);
    buttons[7].value = 0;
    assert.equal(input.sample(0.25).throttle, raised);
    buttons[6].value = 0.45;
    const lower = input.sample(0.25);
    assert.ok(lower.throttle < raised);
    assert.equal(lower.brake, false, 'LT adjusts throttle, not the airbrake');
    window.dispatchEvent(new Event('blur'));
    assert.equal(input.sample(0.25).throttle, lower.throttle);
    buttons[6].value = 0;
    assert.equal(input.sample(0.25).throttle, lower.throttle);
    input.onAction = action => { assert.equal(action, 'pause'); input.clear(); };
    buttons[9].value = 1;
    buttons[7].value = 1;
    assert.equal(input.sample(0.25).throttle, lower.throttle, 'pausing must not integrate a stale trigger from that frame');
  });
});

test('flight gamepads expose pitch and rudder independently from throttle and button actions', () => {
  const buttons = Array.from({ length: 17 }, () => ({ value: 0 }));
  buttons[7].value = 0.7;
  buttons[10].value = 1;
  buttons[4].value = 1;
  const flight = readFlightGamepad({ axes: [0.6, 0.7, -0.8], buttons });
  assert.ok(flight.steer > 0 && flight.pitch > 0 && flight.yaw < 0);
  assert.equal(flight.throttle, 0.7);
  assert.equal(flight.gear, true);
  assert.equal(flight.hook, true);
  assert.equal(flight.brake, 0);
});
