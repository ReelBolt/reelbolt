import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { createRateLimiter } from './rate-limit.ts';

const WINDOW = 10 * 60 * 1000;

test('allows maxRequests per window, then limits, then recovers', () => {
  const limiter = createRateLimiter({ windowMs: WINDOW, maxRequests: 5 });
  for (let i = 0; i < 5; i++) assert.equal(limiter.isRateLimited('a', 1000 + i), false);
  assert.equal(limiter.isRateLimited('a', 2000), true);
  assert.equal(limiter.isRateLimited('b', 2000), false);
  assert.equal(limiter.isRateLimited('a', 1000 + WINDOW + 10), false);
});

test('keys with no request inside the window are pruned once the map passes the threshold', () => {
  const limiter = createRateLimiter({ windowMs: WINDOW, maxRequests: 5, pruneThreshold: 100 });
  for (let i = 0; i < 500; i++) limiter.isRateLimited(`stale-${i}`, 1000);
  assert.equal(limiter.size(), 500); // all inside the window: nothing to drop yet
  limiter.isRateLimited('fresh', 1000 + WINDOW + 1);
  assert.equal(limiter.size(), 1);
});

test('pruning never drops a key that is still inside the window', () => {
  const limiter = createRateLimiter({ windowMs: WINDOW, maxRequests: 2, pruneThreshold: 1 });
  limiter.isRateLimited('old', 0);
  limiter.isRateLimited('busy', WINDOW - 5);
  limiter.isRateLimited('busy', WINDOW - 4);
  limiter.isRateLimited('other', WINDOW + 1);
  assert.equal(limiter.size(), 2); // 'old' dropped, 'busy' kept
  assert.equal(limiter.isRateLimited('busy', WINDOW + 2), true);
});

// The two contact handlers cannot be imported under `node --test` (next/server, extensionless
// imports), so pin that both use the pruning limiter and neither keeps its own unpruned map.
for (const file of ['app/api/contact/route.ts', 'functions/api/contact.ts']) {
  test(`${file} uses the pruning rate limiter`, () => {
    const source = readFileSync(path.join(import.meta.dirname, '..', file), 'utf8');
    assert.match(source, /createRateLimiter\(/);
    assert.doesNotMatch(source, /new Map</);
  });
}

test('the Pages contact function rejects non-object JSON bodies through readContactBody', () => {
  const source = readFileSync(path.join(import.meta.dirname, '..', 'functions/api/contact.ts'), 'utf8');
  assert.match(source, /await readContactBody\(request\)/);
  assert.doesNotMatch(source, /await request\.json\(\)/);
});
