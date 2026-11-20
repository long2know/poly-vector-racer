import assert from 'node:assert/strict';
import test from 'node:test';
import { deadzone, Input, readGamepad } from '../../src/StuntRacer.Browser/wwwroot/engine/input.js';

const pad = (axes = [0, 0], pressed = {}) => ({
  index: 0, id: 'Test standard controller', connected: true, mapping: 'standard', axes,
  buttons: Array.from({ length: 17 }, (_, i) => ({ value: pressed[i] ?? 0, pressed: (pressed[i] ?? 0) > 0.5 })),
});

test('analogue deadzone removes drift without losing full steering or analogue triggers', () => {
  assert.equal(deadzone(0.1), 0);
  assert.equal(deadzone(-0.1), 0);
  assert.equal(deadzone(1), 1);
  assert.equal(deadzone(-1), -1);
  const driving = readGamepad(pad([0.6, 0], { 7: 0.7, 6: 0.25, 0: 1 }));
  assert.ok(driving.steer > 0.5 && driving.steer < 0.6);
  assert.equal(driving.throttle, 0.7);
  assert.equal(driving.brake, 0.25);
  assert.equal(driving.boost, true);
  assert.equal(readGamepad(pad([1, 0], { 15: 1 })).steer, 1);
});

test('classic joystick uses vertical axis and fire rather than inventing trigger values', () => {
  const up = readGamepad(pad([-1, -1], { 0: 1 }), 'joystick');
  assert.equal(up.steer, -1);
  assert.equal(up.throttle, 1);
  assert.equal(up.brake, 0);
  assert.equal(up.boost, true);
  const down = readGamepad(pad([0, 1]), 'joystick');
  assert.equal(down.brake, 1);
  assert.equal(down.throttle, 0);
  assert.equal(readGamepad(null).throttle, 0);
  assert.equal(readGamepad(null).boost, false);
});

test('focus release requires a neutral gamepad before held throttle can drive again', () => {
  const names = ['window', 'document', 'navigator', 'Element'];
  const descriptors = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  let gamepad = pad([0, 0], { 7: 1 });
  const devices = [], actions = [];
  let input;
  try {
    for (const [name, value] of Object.entries({
      window: new EventTarget(),
      document: Object.assign(new EventTarget(), { querySelectorAll: () => [] }),
      navigator: { getGamepads: () => gamepad ? [gamepad] : [] },
      Element: class Element {},
    })) Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
    input = new Input({ focus() {} }, action => actions.push(action), device => devices.push(device?.id ?? null));
    assert.equal(input.sample().throttle, 1);
    window.dispatchEvent(new Event('blur'));
    assert.equal(input.sample().throttle, 0, 'a held trigger must not resume acceleration after blur');
    gamepad = pad();
    assert.equal(input.sample().throttle, 0);
    gamepad = pad([0, 0], { 7: 0.8, 9: 1 });
    assert.equal(input.sample().throttle, 0.8);
    input.sample();
    assert.deepEqual(actions, ['pause'], 'a held Start button toggles pause only once');
    gamepad = null;
    assert.equal(input.sample().throttle, 0);
    assert.deepEqual(devices, ['Test standard controller', null]);
  } finally {
    input?.dispose();
    for (const [name, descriptor] of descriptors) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
});
