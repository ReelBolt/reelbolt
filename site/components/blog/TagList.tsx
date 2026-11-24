import { Link } from '@/i18n/link';
import { tagLabel, type BlogTag } from '@/lib/blog/types';

/**
 * Filter links: an "All posts" chip, then one per tag page. `current` marks the tag being shown and
 * `allActive` marks the unfiltered index. Both use `aria-current`.
 */
export function TagList({ tags, current, allActive = false }: { tags: BlogTag[]; current?: BlogTag; allActive?: boolean }) {
  const chipClass = (active: boolean) =>
    `inline-flex min-h-11 items-center border px-4 font-mono text-xs uppercase tracking-eyebrow transition-colors hover:text-ink ${
      active ? 'border-accent text-ink' : 'border-line-strong text-ink-muted'
    }`;
  return (
    <nav aria-label="Filter articles by tag">
      <ul className="flex flex-wrap gap-2">
        <li>
          <Link href="/blog" aria-current={allActive ? 'page' : undefined} className={chipClass(allActive)}>
            All posts
          </Link>
        </li>
        {tags.map((tag) => {
          const active = tag === current;
          return (
            <li key={tag}>
              <Link href={`/blog/tag/${tag}`} aria-current={active ? 'page' : undefined} className={chipClass(active)}>
                {tagLabel(tag)}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
