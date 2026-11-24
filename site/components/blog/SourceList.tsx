export interface Source {
  /** Anchor for an in-text `[n]` marker, e.g. `source-1`. */
  id?: string;
  label: string;
  url: string;
  /** Date the source was read, `YYYY-MM-DD`. */
  accessed: string;
}

/**
 * Where a claim came from. External links are `nofollow` so the site does not vouch for a vendor's
 * page, and each one carries the date it was read so a stale comparison is visible.
 */
export function SourceList({ sources }: { sources: Source[] }) {
  return (
    <section aria-labelledby="article-sources" className="mt-14 pt-8">
      <h2 id="article-sources" className="font-display text-lg font-bold text-ink">
        Sources
      </h2>
      <ol className="mt-4 list-decimal space-y-2 pl-6 font-sans text-sm leading-relaxed text-ink-muted">
        {sources.map((source) => (
          <li key={source.url} id={source.id} className="scroll-mt-24">
            <a
              href={source.url}
              rel="nofollow noopener"
              className="text-accent-ink underline underline-offset-4 hover:text-accent-hover"
            >
              {source.label}
            </a>{' '}
            <span className="font-mono text-[11px] text-ink-faint">accessed {source.accessed}</span>
          </li>
        ))}
      </ol>
    </section>
  );
}
