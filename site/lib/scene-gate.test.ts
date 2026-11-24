import { test } from 'node:test';
import assert from 'node:assert/strict';
import { shouldMountScene, whenFirstInteraction } from './scene-gate.ts';

test('wide desktop with 8 cores and no Save-Data mounts the scene', () => {
  assert.equal(shouldMountScene({ wide: true, cores: 8, saveData: false }), true);
});

test('narrow viewport never mounts the scene', () => {
  assert.equal(shouldMountScene({ wide: false, cores: 8, saveData: false }), false);
});

test('fewer than 4 cores never mounts the scene', () => {
  assert.equal(shouldMountScene({ wide: true, cores: 2, saveData: false }), false);
  assert.equal(shouldMountScene({ wide: true, cores: 3, saveData: false }), false);
});

test('Save-Data never mounts the scene', () => {
  assert.equal(shouldMountScene({ wide: true, cores: 8, saveData: true }), false);
});

test('unknown core count (0) never mounts the scene', () => {
  assert.equal(shouldMountScene({ wide: true, cores: 0, saveData: false }), false);
});

// whenFirstInteraction reads the global `window`; point it at a bare EventTarget for the test.
function withFakeWindow<T>(target: EventTarget, run: () => T): T {
  const g = globalThis as unknown as { window?: EventTarget };
  const previous = g.window;
  g.window = target;
  try {
    return run();
  } finally {
    g.window = previous;
  }
}

test('whenFirstInteraction runs the callback exactly once, even after two events', () => {
  const target = new EventTarget();
  let calls = 0;
  withFakeWindow(target, () => whenFirstInteraction(() => { calls += 1; }));
  target.dispatchEvent(new Event('pointermove'));
  target.dispatchEvent(new Event('keydown'));
  assert.equal(calls, 1);
});

test('whenFirstInteraction cleanup removes every listener', () => {
  const target = new EventTarget();
  let calls = 0;
  const cleanup = withFakeWindow(target, () => whenFirstInteraction(() => { calls += 1; }));
  cleanup();
  for (const type of ['pointermove', 'pointerdown', 'keydown', 'touchstart', 'wheel', 'scroll']) {
    target.dispatchEvent(new Event(type));
  }
  assert.equal(calls, 0);
});
