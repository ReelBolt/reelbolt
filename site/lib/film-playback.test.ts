import { test } from 'node:test';
import assert from 'node:assert/strict';
import { shouldAutoplay, FILM_AUTOPLAY_THRESHOLD } from './film-playback.ts';

test('the film plays only when it is on screen, motion is allowed and the visitor has not paused it', () => {
  assert.equal(shouldAutoplay({ reduced: false, intersecting: true, userPaused: false }), true);
});

test('reduced motion never autoplays, even on screen', () => {
  assert.equal(shouldAutoplay({ reduced: true, intersecting: true, userPaused: false }), false);
});

test('off screen never autoplays', () => {
  assert.equal(shouldAutoplay({ reduced: false, intersecting: false, userPaused: false }), false);
});

test('a visitor who paused it is never restarted by scrolling back', () => {
  assert.equal(shouldAutoplay({ reduced: false, intersecting: true, userPaused: true }), false);
});

test('the observer threshold is a fraction of the film, not a pixel count', () => {
  assert.ok(FILM_AUTOPLAY_THRESHOLD > 0 && FILM_AUTOPLAY_THRESHOLD <= 1);
});
