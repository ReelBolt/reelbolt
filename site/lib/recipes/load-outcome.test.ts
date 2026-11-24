import { test } from 'node:test';
import assert from 'node:assert/strict';
import { outcomeFor } from './load-outcome.ts';

test('a readable 200 is ready, an unreadable 200 and a 404 are gone', () => {
  assert.equal(outcomeFor(200, true), 'ready');
  assert.equal(outcomeFor(200, false), 'gone');
  assert.equal(outcomeFor(404, false), 'gone');
});

test('server errors and rate limits are failures the visitor can retry', () => {
  for (const s of [429, 500, 502, 503]) assert.equal(outcomeFor(s, false), 'failed');
});
