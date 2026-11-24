import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  MAX_CLOCK_SKEW_MS,
  MIN_FILL_TIME_MS,
  isHoneypotFilled,
  isLikelyBot,
  isTooFastSubmission,
} from './bot-checks.ts';

const NOW = 1_760_000_000_000;

test('the thresholds are 3 seconds and 60 seconds', () => {
  assert.equal(MIN_FILL_TIME_MS, 3000);
  assert.equal(MAX_CLOCK_SKEW_MS, 60_000);
});

test('a missing or non-numeric startedAt counts as too fast', () => {
  for (const value of [undefined, null, '', String(NOW - 10_000), NaN, Infinity, -Infinity, {}, [NOW - 10_000]]) {
    assert.equal(isTooFastSubmission(value, NOW), true, `value ${String(value)}`);
  }
});

test('a form sent less than 3 seconds after it opened counts as too fast', () => {
  assert.equal(isTooFastSubmission(NOW, NOW), true);
  assert.equal(isTooFastSubmission(NOW - 1, NOW), true);
  assert.equal(isTooFastSubmission(NOW - 2999, NOW), true);
});

test('a form sent 3 seconds or more after it opened passes', () => {
  assert.equal(isTooFastSubmission(NOW - 3000, NOW), false);
  assert.equal(isTooFastSubmission(NOW - 45_000, NOW), false);
  assert.equal(isTooFastSubmission(NOW - 24 * 60 * 60 * 1000, NOW), false);
});

test('a startedAt more than 60 seconds in the future counts as too fast', () => {
  assert.equal(isTooFastSubmission(NOW + 60_001, NOW), true);
  assert.equal(isTooFastSubmission(NOW + 3_600_000, NOW), true);
});

test('a startedAt up to 60 seconds ahead is tolerated as clock skew', () => {
  assert.equal(isTooFastSubmission(NOW + 1, NOW), false);
  assert.equal(isTooFastSubmission(NOW + 60_000, NOW), false);
});

test('the honeypot counts only when it holds non-blank text', () => {
  assert.equal(isHoneypotFilled('https://spam.example'), true);
  assert.equal(isHoneypotFilled('   '), false);
  assert.equal(isHoneypotFilled(''), false);
  assert.equal(isHoneypotFilled(undefined), false);
  assert.equal(isHoneypotFilled(42), false);
});

test('isLikelyBot flags a filled honeypot or a fast or missing timestamp', () => {
  assert.equal(isLikelyBot({ website: '', startedAt: NOW - 10_000 }, NOW), false);
  assert.equal(isLikelyBot({ website: 'x', startedAt: NOW - 10_000 }, NOW), true);
  assert.equal(isLikelyBot({ website: '', startedAt: NOW - 500 }, NOW), true);
  assert.equal(isLikelyBot({ website: '' }, NOW), true);
  assert.equal(isLikelyBot({}, NOW), true);
});
