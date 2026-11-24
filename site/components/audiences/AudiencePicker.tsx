import { Link } from '@/i18n/link';
import { useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import { AUDIENCES, type Audience } from '@/lib/audiences/registry';
import type { LandingPersona } from '@/lib/attribution-persona';

function Arrow() {
  return (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true" focusable="false" className="shrink-0">
      <path d="M2 7h10M8 3l4 4-4 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/**
 * One audience as a card. The whole card is the link, so its accessible name is the card's own
 * text: who it is for, the problem in that audience's words and what they get. The first-person
 * line is the audience's situation, not a quote from a customer: it carries no quotation marks and
 * no name, because ReelBolt has no testimonials to show (see `lib/proof/registry`).
 */
function AudienceCard({ audience }: { audience: Audience }) {
  const t = useT('audiences');
  const item = `items.${audience.messageKey}`;
  return (
    <Link
      href={audience.path}
      className="group flex h-full flex-col bg-paper-2 p-6 transition-colors hover:bg-paper-3 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
    >
      <span className="font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">{t(`${item}.who`)}</span>
      <span className="mt-3 font-display text-xl font-bold text-ink">{t(`${item}.name`)}</span>
      <span className="mt-3 text-sm text-ink-2">{t(`${item}.pain`)}</span>
      <span className="mt-2 text-sm text-ink-muted">{t(`${item}.outcome`)}</span>
      <span className="mt-auto flex items-center gap-2 pt-5 text-sm font-medium text-accent-ink group-hover:text-accent">
        {t('picker.link', { audience: t(`${item}.audience`) })}
        <Arrow />
      </span>
    </Link>
  );
}

/**
 * "What is your story?": the six audience pages, as cards. The home page shows all six; an
 * audience page passes `exclude` and gets the other five under a quieter heading.
 */
export function AudiencePicker({ id, exclude }: { id?: string; exclude?: LandingPersona }) {
  const t = useT('audiences');
  const shown = AUDIENCES.filter((audience) => audience.persona !== exclude);
  const heading = exclude ? 'page.others' : 'picker';
  return (
    <Section id={id}>
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
          {t(`${heading}.title`)}
        </h2>
        {!exclude && <p className="mt-4 text-ink-muted">{t('picker.intro')}</p>}
      </div>
      <ul className="mt-10 grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
        {shown.map((audience) => (
          <li key={audience.slug}>
            <AudienceCard audience={audience} />
          </li>
        ))}
      </ul>
    </Section>
  );
}
