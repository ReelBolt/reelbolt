import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { ERROR_COPY, errorCopyFor } from './error-copy.ts';

const catalogue = (l: string) =>
  JSON.parse(readFileSync(new URL(`../messages/${l}/layout.json`, import.meta.url), 'utf8')).errorpage;

test('global error copy picks Italian for it-*, English otherwise', () => {
  assert.equal(errorCopyFor('it-IT'), ERROR_COPY.it);
  assert.equal(errorCopyFor('en-US'), ERROR_COPY.en);
  assert.equal(errorCopyFor(undefined), ERROR_COPY.en);
});

test('global error copy matches the layout.errorpage catalogue in both languages', () => {
  for (const l of ['en', 'it'] as const) {
    const c = catalogue(l);
    assert.equal(ERROR_COPY[l].heading, c.heading);
    assert.equal(ERROR_COPY[l].retry, c.retry);
    assert.equal(ERROR_COPY[l].home, c.home);
    for (const k of ['heading', 'body', 'retry', 'home', 'contact']) assert.ok(c[k], `${l}.${k}`);
  }
});
