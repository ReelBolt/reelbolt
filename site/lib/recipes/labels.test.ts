import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeKey, parseLimit } from './labels.ts';

test('keys normalise across spellings', () => {
  assert.equal(normalizeKey('VideoAnalyze'), 'videoanalyze');
  assert.equal(normalizeKey('Video analyze'), 'videoanalyze');
  assert.equal(normalizeKey('video_analyze'), 'videoanalyze');
});

test('known limit shapes map, unknown ones are hidden', () => {
  assert.deepEqual(parseLimit('Review loop, up to 5 rounds'), { key: 'reviewRounds', values: { count: 5 } });
  assert.deepEqual(parseLimit('Pass mark 7 out of 10'), { key: 'passMark', values: { score: '7' } });
  assert.deepEqual(parseLimit('Spend limited to 12.5 credits'), { key: 'spendCap', values: { credits: '12.5' } });
  assert.equal(parseLimit('<b>Anything else</b>'), null);
});
