import { test } from 'node:test';
import assert from 'node:assert/strict';

import { findBySlug, listedOnly, postProblems, validatePosts } from './validate.ts';
import type { PostMeta } from './types.ts';

// posts.ts imports TSX content modules, which `node --test` cannot load. The registry rules live in
// validate.ts instead, and posts.ts calls validatePosts over the real entries at import time, so
// `next build` fails on a bad post. These cases pin the rules themselves.

const good: PostMeta = {
  slug: 'hello-reelbolt',
  title: 'What ReelBolt is',
  description: 'ReelBolt is an AI video team for product marketing teams.',
  date: '2026-10-17',
  tags: ['product'],
  readingMinutes: 2,
};

test('a valid registry passes', () => {
  assert.deepEqual(postProblems([good, { ...good, slug: 'second-post', date: '2026-10-01' }]), []);
  assert.doesNotThrow(() => validatePosts([good]));
});

test('duplicate slugs are rejected', () => {
  const problems = postProblems([good, { ...good, date: '2026-09-01' }]);
  assert.ok(problems.some((p) => p.includes('duplicate slug')), problems.join('\n'));
  assert.throws(() => validatePosts([good, good]), /duplicate slug/);
});

test('date must match YYYY-MM-DD and be a real calendar date', () => {
  for (const date of ['2026-10-7', '17-10-2026', '2026/10/17', '2026-02-30', 'today', '']) {
    assert.ok(
      postProblems([{ ...good, date }]).some((p) => p.includes('date must be a real YYYY-MM-DD')),
      `"${date}" must be rejected`,
    );
  }
});

test('updated, when present, follows the same date rule', () => {
  assert.deepEqual(postProblems([{ ...good, updated: '2026-11-02' }]), []);
  assert.ok(postProblems([{ ...good, updated: 'soon' }]).some((p) => p.includes('updated')));
});

test('every post needs one or more known tags', () => {
  assert.ok(postProblems([{ ...good, tags: [] }]).some((p) => p.includes('at least one tag')));
  assert.ok(
    postProblems([{ ...good, tags: ['news' as PostMeta['tags'][number]] }]).some((p) =>
      p.includes('unknown tag'),
    ),
  );
});

test('slugs must be kebab-case', () => {
  for (const slug of ['Hello', 'hello_world', 'hello world', '-hello', 'hello-']) {
    assert.ok(
      postProblems([{ ...good, slug }]).some((p) => p.includes('kebab-case')),
      `"${slug}" must be rejected`,
    );
  }
});

test('reading time must be a positive integer', () => {
  assert.ok(postProblems([{ ...good, readingMinutes: 0 }]).length > 0);
  assert.ok(postProblems([{ ...good, readingMinutes: 1.5 }]).length > 0);
});

test('unlisted must be a boolean when present', () => {
  assert.deepEqual(postProblems([{ ...good, unlisted: false }]), []);
  assert.ok(
    postProblems([{ ...good, unlisted: 'yes' as unknown as boolean }]).some((p) =>
      p.includes('unlisted must be true or false'),
    ),
  );
});

test('an unlisted post is excluded from listings but kept by slug lookup', () => {
  const hidden: PostMeta = { ...good, slug: 'hidden-post', unlisted: true };
  const registry = [good, hidden];

  assert.deepEqual(
    listedOnly(registry).map((post) => post.slug),
    ['hello-reelbolt'],
  );
  assert.equal(findBySlug(registry, 'hidden-post')?.slug, 'hidden-post');
  assert.equal(findBySlug(registry, 'missing'), undefined);
});
