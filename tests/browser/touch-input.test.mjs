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
  const buttons = Object.fromEntries(['left', 'right', 'throttle', 'brake', 'boost']
    .map(name => [name, new TouchElement(name)]));
  const pad = new TouchElement(), thumb = new TouchElement();
  let input;
  try {
    for (const [name, value] of Object.entries({
      window: new EventTarget(),
      document: Object.assign(new EventTarget(), {
        querySelectorAll: () => Object.values(buttons),
        getElementById: id => id === 'touch-steering' ? pad : id === 'touch-steering-thumb' ? thumb : null,
      }),
      navigator: { getGamepads: () => [] },
      Element: TouchElement,
    })) Object.defineProperty(globalThis, name, { value, writable: true, configurable: true });
    input = new Input({ focus() {} }, () => {}, () => {});
    check(input, buttons, pad, thumb);
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

test('flight touch uses both stick axes and an independent power thumb without changing racing controls', () => {
  withTouch((input, buttons, pad) => {
    input.setMode('flight', 0.4);
    pointer(pad, 'pointerdown', 1, 140, 8);
    pointer(buttons.throttle, 'pointerdown', 2);
    let flight = input.sample(1 / 60);
    assert.ok(flight.roll > 0.8 && flight.pitch > 0.8, 'drag right/up must bank right and pull up');
    assert.equal(flight.throttle, 1);
    pointer(buttons.throttle, 'pointermove', 2, 80, 160);
    assert.equal(input.sample().afterburner, true);
    pointer(pad, 'pointercancel', 1);
    flight = input.sample();
    assert.equal(flight.roll, 0);
    assert.equal(flight.pitch, 0);
    assert.equal(flight.throttle, 1);
    pointer(buttons.throttle, 'pointerup', 2);
    flight = input.sample();
    assert.equal(flight.afterburner, false);
    assert.equal(flight.throttle, 0.4, 'releasing a pedal returns to the deliberate throttle-lever setting');
    input.setMode('racing');
    pointer(pad, 'pointerdown', 3, 140, 8);
    assert.equal(input.sample().pitch, undefined, 'flight axes must not leak into racing');
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
