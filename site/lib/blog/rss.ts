// RSS 2.0 for the blog. Pure: the caller passes the posts and the site URL, so the test runs
// under `node --test` without Next.
import type { BlogTag } from './types.ts';

export const RSS_ALTERNATE = { 'application/rss+xml': '/blog/rss.xml' } as const;

export interface RssPost {
  slug: string;
  title: string;
  description: string;
  date: string;
  tags?: BlogTag[];
}

const TITLE = 'ReelBolt blog';
const DESCRIPTION = 'Notes on making product promo videos with ReelBolt, and how it compares with other tools.';

export function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function rfc822(date: string): string {
  return new Date(`${date}T00:00:00Z`).toUTCString();
}

export function buildRss(posts: readonly RssPost[], siteUrl: string): string {
  const base = siteUrl.replace(/\/+$/, '');
  const blogUrl = `${base}/blog`;
  const items = posts.map((post) => {
    const link = `${blogUrl}/${post.slug}`;
    const categories = (post.tags ?? [])
      .map((tag) => `    <category>${escapeXml(tag)}</category>`)
      .join('\n');
    return [
      '  <item>',
      `    <title>${escapeXml(post.title)}</title>`,
      `    <link>${escapeXml(link)}</link>`,
      `    <guid isPermaLink="true">${escapeXml(link)}</guid>`,
      `    <description>${escapeXml(post.description)}</description>`,
      `    <pubDate>${rfc822(post.date)}</pubDate>`,
      categories,
      '  </item>',
    ]
      .filter((line) => line !== '')
      .join('\n');
  });

  return [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">',
    '<channel>',
    `  <title>${escapeXml(TITLE)}</title>`,
    `  <link>${escapeXml(blogUrl)}</link>`,
    `  <description>${escapeXml(DESCRIPTION)}</description>`,
    '  <language>en-us</language>',
    `  <atom:link href="${escapeXml(`${blogUrl}/rss.xml`)}" rel="self" type="application/rss+xml" />`,
    ...items,
    '</channel>',
    '</rss>',
    '',
  ].join('\n');
}
