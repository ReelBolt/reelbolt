import type { ReactNode } from 'react';

/** A boxed aside inside an article: a note, a caveat, or a short takeaway. Not a live region. */
export function Callout({ title, children }: { title: string; children: ReactNode }) {
  return (
    <aside aria-label={title} className="my-8 bg-paper-2 p-5 sm:p-6">
      <p className="font-mono text-[11px] uppercase tracking-eyebrow text-accent-ink">{title}</p>
      <div className="mt-3 font-sans text-[0.95rem] leading-relaxed text-ink-muted [&_p]:mt-3 [&_p:first-child]:mt-0">
        {children}
      </div>
    </aside>
  );
}
