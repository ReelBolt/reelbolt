import type { BlogFaq } from '@/lib/blog/types';

/**
 * The article's questions, rendered as disclosure widgets. The page emits FAQPage JSON-LD from the
 * same `faqs` array, so the markup and the visible text cannot drift.
 */
export function ArticleFaq({ faqs }: { faqs: BlogFaq[] }) {
  if (faqs.length === 0) return null;
  return (
    <section aria-labelledby="article-faq" className="mx-auto mt-14 max-w-[40rem] pt-8">
      <h2 id="article-faq" className="font-display text-2xl font-bold text-ink">
        Questions and answers
      </h2>
      <div className="mt-6 divide-y divide-line border-y border-line">
        {faqs.map((faq) => (
          <details key={faq.q} className="group py-4">
            <summary className="flex min-h-11 cursor-brand-pointer list-none items-center justify-between gap-4 font-display text-base font-bold text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong">
              {faq.q}
              <span aria-hidden="true" className="font-mono text-accent-ink transition-transform group-open:rotate-45">
                +
              </span>
            </summary>
            <p className="mt-3 font-sans text-[0.95rem] leading-relaxed text-ink-muted">{faq.a}</p>
          </details>
        ))}
      </div>
    </section>
  );
}
