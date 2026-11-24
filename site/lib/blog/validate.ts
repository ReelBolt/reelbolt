// Pure registry checks. No framework imports so `node --test` can load this file directly; posts.ts
// calls validatePosts at module load, so a malformed post fails `next build` instead of shipping.
import { BLOG_TAGS, type PostMeta } from './types.ts';

const SLUG = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function isRealDate(value: string): boolean {
  if (!DATE.test(value)) return false;
  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}

/** Every problem found in the metas, as human-readable lines. Empty when the registry is valid. */
export function postProblems(metas: readonly PostMeta[]): string[] {
  const problems: string[] = [];
  const seen = new Set<string>();

  for (const meta of metas) {
    const where = `post "${meta.slug}"`;

    if (!SLUG.test(meta.slug)) problems.push(`${where}: slug must be kebab-case`);
    if (seen.has(meta.slug)) problems.push(`${where}: duplicate slug`);
    seen.add(meta.slug);

    if (!meta.title.trim()) problems.push(`${where}: title is empty`);
    if (!meta.description.trim()) problems.push(`${where}: description is empty`);
    if (!isRealDate(meta.date)) problems.push(`${where}: date must be a real YYYY-MM-DD`);
    if (meta.updated !== undefined && !isRealDate(meta.updated)) {
      problems.push(`${where}: updated must be a real YYYY-MM-DD`);
    }
    if (!Array.isArray(meta.tags) || meta.tags.length === 0) {
      problems.push(`${where}: needs at least one tag`);
    } else {
      for (const tag of meta.tags) {
        if (!(BLOG_TAGS as readonly string[]).includes(tag)) {
          problems.push(`${where}: unknown tag "${tag}"`);
        }
      }
    }
    if (!Number.isInteger(meta.readingMinutes) || meta.readingMinutes < 1) {
      problems.push(`${where}: readingMinutes must be a positive integer`);
    }
    if (meta.hero && (meta.hero.width < 1 || meta.hero.height < 1 || !meta.hero.alt.trim())) {
      problems.push(`${where}: hero needs positive dimensions and alt text`);
    }
    if (meta.unlisted !== undefined && typeof meta.unlisted !== 'boolean') {
      problems.push(`${where}: unlisted must be true or false`);
    }
    for (const faq of meta.faqs ?? []) {
      if (!faq.q.trim() || !faq.a.trim()) problems.push(`${where}: FAQ entries need q and a`);
    }
  }

  return problems;
}

/** The posts a listing shows: everything except the ones marked `unlisted`. Keeps order. */
export function listedOnly<T extends { unlisted?: boolean }>(posts: readonly T[]): T[] {
  return posts.filter((post) => post.unlisted !== true);
}

/** The post with this slug, listed or not. Unlisted posts still resolve at their URL. */
export function findBySlug<T extends { slug: string }>(posts: readonly T[], slug: string): T | undefined {
  return posts.find((post) => post.slug === slug);
}

export function validatePosts(metas: readonly PostMeta[]): void {
  const problems = postProblems(metas);
  if (problems.length > 0) {
    throw new Error(`Invalid blog registry:\n- ${problems.join('\n- ')}`);
  }
}
