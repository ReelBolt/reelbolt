import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';

import { POST_AUDIENCES, audienceOfPost } from './blog-posts.ts';
import { LANDING_PERSONAS } from '../attribution-persona.ts';

test('every post with an audience exists, and its audience has a landing page', () => {
  for (const [slug, persona] of Object.entries(POST_AUDIENCES)) {
    const file = new URL(`../../content/blog/${slug}.tsx`, import.meta.url);
    assert.ok(existsSync(file), `content/blog/${slug}.tsx is gone: remove or rename its entry`);
    // The file is the post with that slug, not merely a file of that name.
    assert.match(readFileSync(file, 'utf8'), new RegExp(`slug: '${slug}'`), slug);
    assert.ok(LANDING_PERSONAS.includes(persona), `${slug}: ${persona}`);
  }
});

test('a post without an entry carries no audience', () => {
  assert.equal(audienceOfPost('lessons-into-short-clips'), 'educator');
  assert.equal(audienceOfPost('hello-reelbolt'), undefined);
  assert.equal(audienceOfPost('reelbolt-vs-capcut'), undefined);
  // Object keys that are not posts never leak through.
  for (const slug of ['constructor', 'toString', '__proto__', '']) assert.equal(audienceOfPost(slug), undefined, slug);
});

test('the article page hands the post audience to its call to action', () => {
  const page = readFileSync(new URL('../../app/(en)/blog/[slug]/page.tsx', import.meta.url), 'utf8');
  assert.match(page, /<PrimaryCta location="blog-article-cta" persona=\{audienceOfPost\(post\.slug\)\} \/>/);
  // The block under every article speaks to anyone with footage, not to software teams only.
  assert.doesNotMatch(page, /See it on your product|release video|the product you already have/);
});
