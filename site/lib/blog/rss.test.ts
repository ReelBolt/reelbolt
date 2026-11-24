import { test } from 'node:test';
import assert from 'node:assert/strict';

import { buildRss, escapeXml } from './rss.ts';

const SITE = 'https://reelbolt.example';

const post = {
  slug: 'compare-a-and-b',
  title: 'Tools & Friends <beta> "quoted"',
  description: 'Fish & chips: 5 < 6 > 4, "fair" comparison',
  date: '2026-10-17',
  tags: ['comparisons' as const],
};

test('escapes &, <, >, and quotes in every text field', () => {
  const xml = buildRss([post], SITE);
  assert.ok(xml.includes('<title>Tools &amp; Friends &lt;beta&gt; &quot;quoted&quot;</title>'), xml);
  assert.ok(xml.includes('Fish &amp; chips: 5 &lt; 6 &gt; 4, &quot;fair&quot; comparison'), xml);
  // Raw markup from the post must never reach the document.
  assert.ok(!xml.includes('<beta>'));
  assert.ok(!/&(?!amp;|lt;|gt;|quot;|apos;)/.test(xml), 'no bare ampersands');
});

test('escapeXml escapes each reserved character once', () => {
  assert.equal(escapeXml('&<>"\''), '&amp;&lt;&gt;&quot;&apos;');
  assert.equal(escapeXml('&amp;'), '&amp;amp;');
});

test('links are absolute, built from the site URL, with or without a trailing slash', () => {
  const xml = buildRss([post], `${SITE}/`);
  assert.ok(xml.includes(`<link>${SITE}/blog/compare-a-and-b</link>`), xml);
  assert.ok(xml.includes(`<link>${SITE}/blog</link>`), xml);
  assert.ok(xml.includes(`href="${SITE}/blog/rss.xml"`), xml);
  assert.ok(!xml.includes('https://reelbolt.example//'), 'no double slash');
});

test('produces an RSS 2.0 document with one item per post, in the order given', () => {
  const second = { ...post, slug: 'second', title: 'Second', date: '2026-09-01' };
  const xml = buildRss([post, second], SITE);
  assert.ok(xml.startsWith('<?xml version="1.0" encoding="UTF-8"?>'));
  assert.ok(xml.includes('<rss version="2.0"'));
  assert.equal(xml.match(/<item>/g)?.length, 2);
  assert.ok(xml.indexOf('compare-a-and-b') < xml.indexOf('/second'));
  assert.ok(xml.includes('<pubDate>Sat, 17 Oct 2026 00:00:00 GMT</pubDate>'), xml);
  assert.ok(xml.includes('<category>comparisons</category>'));
});

test('an empty blog still yields a valid channel', () => {
  const xml = buildRss([], SITE);
  assert.ok(xml.includes('<channel>') && xml.includes('</channel>'));
  assert.equal(xml.match(/<item>/g), null);
});
