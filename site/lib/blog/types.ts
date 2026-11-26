import type { JSX } from 'react';

/** The topics a post can be filed under. Each one gets a `/blog/tag/<tag>` page. */
export const BLOG_TAGS = ['comparisons', 'guides', 'open-source', 'product'] as const;

export type BlogTag = (typeof BLOG_TAGS)[number];

const TAG_LABELS: Record<BlogTag, string> = {
  comparisons: 'Comparisons',
  guides: 'Guides',
  'open-source': 'Open source',
  product: 'Product',
};

export function tagLabel(tag: BlogTag): string {
  return TAG_LABELS[tag];
}

export function isBlogTag(value: string): value is BlogTag {
  return (BLOG_TAGS as readonly string[]).includes(value);
}

export interface BlogHero {
  /** Image name stem; the Figure resolves it to `/blog/img/<src>-<width>.{avif,webp}`. */
  src: string;
  alt: string;
  width: number;
  height: number;
}

export interface BlogFaq {
  q: string;
  a: string;
}

/** Everything about a post except its component. Lives in `content/blog/<slug>.tsx` as `meta`. */
export interface PostMeta {
  slug: string;
  title: string;
  description: string;
  /** Publication date, `YYYY-MM-DD`. */
  date: string;
  /** Last substantive edit, `YYYY-MM-DD`. Omit when the post has not changed since `date`. */
  updated?: string;
  tags: BlogTag[];
  readingMinutes: number;
  hero?: BlogHero;
  faqs?: BlogFaq[];
  /**
   * Built at its URL but kept out of every listing (index, tags, related, sitemap, RSS, llms) and
   * served with `noindex`. Omit or set `false` for a post that should be listed.
   */
  unlisted?: boolean;
}

export interface BlogPost extends PostMeta {
  Body: () => JSX.Element;
}
