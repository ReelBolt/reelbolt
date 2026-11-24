import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isoDuration, recipeDetailSchema, recipeGallerySchema } from './schema.ts';
import { parseDetail } from './types.ts';
import { recipeSitemapEntries } from './sitemap.ts';

const ID = '3f2b8c1e-5d4a-4b7e-9c61-0a1b2c3d4e5f';
const raw = { id: ID, name: 'Teaser', description: 'desc', category: 'm', language: 'en', costClass: 'No credits', stepCount: 2, stepTypes: [], authorLabel: 'Ana', copyCount: 3, ratingAverage: 4.5, ratingCount: 5, previewSeconds: 75, listedAt: '2026-01-01T00:00:00Z', version: 1 };
const origin = 'https://reelbolt.ai';
const opts = { origin, pathFor: (p: string) => `/it${p}` };

test('detail JSON-LD: real fields only', () => {
  const ld = recipeDetailSchema(parseDetail(raw)!, opts) as Record<string, unknown> & { video: Record<string, string>; creator: { name: string } };
  assert.equal(ld['@type'], 'CreativeWork');
  assert.equal(ld.creator.name, 'Ana');
  assert.equal(ld.url, `${origin}/it/recipes/${ID}`);
  assert.equal(ld.video.uploadDate, '2026-01-01T00:00:00Z');
  assert.equal(ld.video.duration, 'PT1M15S');
  assert.equal(ld.video.contentUrl, `${origin}/api/recipes/${ID}/preview`);
  assert.doesNotMatch(JSON.stringify(ld), /offers|price/i);
});

test('aggregateRating is never emitted, whatever the counts', () => {
  for (const ratingCount of [0, 4, 5, 500]) {
    const ld = recipeDetailSchema(parseDetail({ ...raw, ratingCount })!, opts);
    assert.equal('aggregateRating' in ld, false);
    assert.doesNotMatch(JSON.stringify(ld), /rating/i);
  }
  const none = recipeDetailSchema(parseDetail({ ...raw, previewSeconds: 0, authorLabel: '', listedAt: null })!, opts);
  assert.equal('video' in none || 'creator' in none, false);
});

test('gallery JSON-LD and duration', () => {
  const ld = recipeGallerySchema([parseDetail(raw)!], { ...opts, name: 'n', description: 'd' }) as { mainEntity: { itemListElement: { position: number }[] } };
  assert.equal(ld.mainEntity.itemListElement[0].position, 1);
  assert.equal(isoDuration(120), 'PT2M');
});

test('sitemap entries', () => {
  assert.deepEqual(recipeSitemapEntries([]), []);
  const e = recipeSitemapEntries([{ id: ID, listedAt: '2026-02-03T00:00:00Z' }, { id: 'b', listedAt: null }]);
  assert.deepEqual(e.map((x) => x.path), ['/recipes', `/recipes/${ID}`, '/recipes/b']);
  assert.equal(e[1].lastModified?.toISOString(), '2026-02-03T00:00:00.000Z');
  assert.equal(e[2].lastModified, undefined);
});
