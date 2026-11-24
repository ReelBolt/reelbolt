import type { ReactNode } from 'react';

/**
 * Long-form article body. Styles the plain HTML an author writes (h2, h3, p, lists, links,
 * blockquotes, code, tables) through child selectors, so posts are just JSX with no per-element
 * classes. Measure is capped at 40rem (shared with the title, figures, FAQ and CTA) and the face is `font-sans`, the readable choice for prose
 * (see docs/site-design-system.md "Typography").
 *
 * Author `<h2 id="...">` yourself when a section needs an anchor link; there is no slug library.
 */
export function Prose({ children }: { children: ReactNode }) {
  return (
    <div
      className={[
        'mx-auto max-w-[40rem] font-sans text-base leading-[1.75] text-ink-muted sm:text-[1.0625rem]',
        '[&_h2]:mt-14 [&_h2]:scroll-mt-24 [&_h2]:font-display [&_h2]:text-2xl [&_h2]:font-bold [&_h2]:leading-tight [&_h2]:text-ink',
        '[&_h3]:mt-10 [&_h3]:scroll-mt-24 [&_h3]:font-display [&_h3]:text-xl [&_h3]:font-bold [&_h3]:text-ink',
        '[&_p]:mt-5 [&_p:first-child]:mt-0',
        '[&_strong]:font-semibold [&_strong]:text-ink',
        '[&_ul]:mt-5 [&_ul]:list-disc [&_ul]:space-y-2 [&_ul]:pl-6',
        '[&_ol]:mt-5 [&_ol]:list-decimal [&_ol]:space-y-2 [&_ol]:pl-6',
        '[&_a]:font-medium [&_a]:text-accent-ink [&_a]:underline [&_a]:underline-offset-4 [&_a:hover]:text-accent-hover',
        '[&_blockquote]:mt-6 [&_blockquote]:border-l-2 [&_blockquote]:border-accent [&_blockquote]:pl-5 [&_blockquote]:text-ink-2',
        '[&_code]:rounded-[2px] [&_code]:bg-paper-2 [&_code]:px-1 [&_code]:py-0.5 [&_code]:font-mono [&_code]:text-[0.9em] [&_code]:text-ink-2',
        '[&_sup]:ml-0.5 [&_sup]:font-mono [&_sup]:text-xs [&_sup_a]:inline-block [&_sup_a]:px-1 [&_sup_a]:-mx-1 [&_sup_a]:py-2 [&_sup_a]:font-semibold [&_sup_a]:no-underline',
        '[&_table]:mt-6 [&_table]:w-full [&_table]:border-collapse [&_table]:text-sm',
        '[&_th]:border-b [&_th]:border-line-strong [&_th]:px-3 [&_th]:py-2 [&_th]:text-left [&_th]:font-semibold [&_th]:text-ink',
        '[&_td]:border-b [&_td]:border-line [&_td]:px-3 [&_td]:py-2 [&_td]:align-top',
      ].join(' ')}
    >
      {children}
    </div>
  );
}
