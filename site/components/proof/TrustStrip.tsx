import { useFormat, useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY_DAYS } from '@/lib/legal-placeholders';
import { trialFacts } from '@/lib/audiences/plan-gates';
import { trustItems } from '@/lib/proof/trust';
import { siteConfig } from '@/lib/site-config';

// The trust facts that are true today, and the only proof the site shows while the proof registry
// (`lib/proof/registry`) is empty. Each one has a fixed meaning (docs/marketing/copy-deck.md,
// "Trust facts"): the wording may be adapted, the meaning may not. No customer, count, rating or
// time saving appears here, because none exists to state.
//
// Which facts are shown follows the signup switch (`trustItems`): "no card" and "first video on
// us" describe starting a free account, so they are stated only on a build where the page's button
// really does that.
export function TrustStrip({ id = 'trust' }: { id?: string }) {
  const t = useT('proof.trust');
  const f = useFormat();
  const items = trustItems(siteConfig.signupEnabled);
  // Every figure is read at render time: the first-video grant and its lifetime from the constants
  // the Terms and the pricing page use, the trial's credits and export ceiling from the plan catalog.
  const trial = trialFacts();
  const values = {
    credits: f.number(FIRST_VIDEO_GRANT_CREDITS),
    days: FIRST_VIDEO_GRANT_EXPIRY_DAYS,
    trialCredits: f.number(trial.credits),
    resolution: trial.resolution,
  };
  return (
    <Section id={id} className="bg-paper-2">
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">{t('title')}</h2>
      </div>
      {/* Six facts fill a three-column grid; the four left when signup is off fill two columns.
          The gaps are the grid's own background, so a row must never be left half empty. */}
      <ul
        className={`mt-10 grid grid-cols-1 gap-6 sm:grid-cols-2 ${
          items.length % 3 === 0 ? 'lg:grid-cols-3' : ''
        }`}
      >
        {items.map((item) => (
          <li key={item} className="bg-paper-2 p-6">
            <h3 className="font-display text-base font-bold text-accent-ink">{t(`items.${item}.title`)}</h3>
            <p className="mt-2 text-sm text-ink-muted">{t(`items.${item}.body`, values)}</p>
          </li>
        ))}
      </ul>
    </Section>
  );
}
