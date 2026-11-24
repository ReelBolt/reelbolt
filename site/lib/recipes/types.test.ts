import { test } from 'node:test';
import assert from 'node:assert/strict';
import { costKey, formatSeconds, humanizeKey, isRecipeId, parseCard, parseDetail, parseFacets, parseList } from './types.ts';

const ID = '3f2b8c1e-5d4a-4b7e-9c61-0a1b2c3d4e5f';
const base = { id: ID, name: 'Teaser', description: 'desc', category: 'marketing', language: 'en', costClass: 'No credits', stepCount: 2, stepTypes: [], authorLabel: 'Ana', copyCount: 3, ratingAverage: 4.5, ratingCount: 5, previewSeconds: 10, listedAt: '2026-01-01T00:00:00Z', version: 2 };

test('rating average only from five ratings', () => {
  assert.equal(parseCard(base)?.ratingAverage, 4.5);
  assert.equal(parseCard({ ...base, ratingCount: 4 })?.ratingAverage, null);
  assert.equal(parseCard({ ...base, ratingAverage: 9 })?.ratingAverage, null);
});

test('malformed input reads as empty', () => {
  assert.equal(parseCard({ ...base, id: 'x' }), null);
  assert.deepEqual(parseList('nope'), { items: [], nextCursor: null });
  assert.equal(parseList({ items: [null, base] }).items.length, 1);
  assert.deepEqual(parseFacets(null).costClasses, []);
  assert.equal(parseFacets({ costClasses: [{ key: 'No credits', count: 2 }] }).costClasses[0].key, 'No credits');
});

test('XSS fixture: markup, markdown and URLs stay inert text; control characters go; limits kept', () => {
  const name = '<script>alert(1)</script> **bold** [x](javascript:alert(1))\u0007';
  const d = parseDetail({ ...base, name, description: '<img src=x onerror=alert(1)>\nhttps://evil.example\u0000', prompts: [{ stepOrder: 1, field: 'p', text: '<b>hi</b> `c`' }], steps: [{ order: 1, type: 'Loop', label: '<iframe>', limits: ['3 rounds', 'Up to 5 credits'] }] });
  assert.ok(d);
  assert.equal(d.name, name.replace('\u0007', ''));
  assert.equal(d.prompts[0].text, '<b>hi</b> `c`');
  assert.deepEqual(d.steps[0].limits, ['3 rounds', 'Up to 5 credits']);
  assert.doesNotMatch(JSON.stringify(d), /\\u0007|\\u0000/);
});

test('ids, labels, cost words', () => {
  assert.equal(isRecipeId(ID), true);
  assert.equal(isRecipeId('../etc'), false);
  assert.equal(humanizeKey('GenerateImage'), 'Generate image');
  assert.equal(formatSeconds(75), '1:15');
  assert.equal(costKey('Uses credits and AI shots'), 'creditsShots');
  assert.equal(costKey('weird'), null);
});
