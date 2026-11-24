import { useT, useFormat } from '@/i18n';
import { formatMoneyT } from '@/lib/pricing/localized';
import { Panel } from '@/components/ui/Panel';
import type { TopUp } from '@/lib/pricing';

// Credit rules in buyer language: credits are only for capacity ReelBolt itself supplies, and a
// failure on our side is refunded. No model names, no vendor names — the copy is in
// docs/marketing/copy-deck.md, section 5.
const POINTS = ['supplied', 'estimate', 'free', 'byo', 'runner', 'refund'] as const;

export function CreditsExplainer({ currency, topUp }: { currency: string; topUp?: TopUp }) {
  const t = useT('plans.explainer');
  const f = useFormat();
  return (
    <div className="grid gap-8 lg:grid-cols-12">
      <div className="lg:col-span-5">
        <h2 className="font-display text-2xl font-bold text-ink">
          {t('title')}
        </h2>
        <p className="mt-4 text-ink-muted">
          {t('intro')}
        </p>
        {topUp && (
          <Panel tone="paper" className="mt-6 p-5">
            <p className="font-sans text-xs font-medium text-ink-muted">
              {t('packs')}
            </p>
            <ul className="mt-3 divide-y divide-line">
              {topUp.packs.map((pack) => {
                const label = {
                  credits: t('packCredits', { n: f.number(pack.credits) }),
                  price: formatMoneyT(f, pack.price, currency),
                };
                return (
                  <li key={pack.credits} className="flex items-baseline justify-between gap-4 py-2">
                    <span className="font-display text-lg font-bold text-ink">
                      {label.credits}
                    </span>
                    <span className="font-mono text-sm text-ink-muted">{label.price}</span>
                  </li>
                );
              })}
            </ul>
            <p className="mt-3 text-sm text-ink-muted">
              {t('packsNote', { months: topUp.expiresAfterMonths })}
            </p>
          </Panel>
        )}
      </div>
      <ul className="space-y-4 lg:col-span-7">
        {POINTS.map((point) => (
          <li key={point} className="flex items-start gap-3 text-ink-muted">
            <span aria-hidden="true" className="mt-2 h-2 w-2 shrink-0 bg-accent" />
            <span>{t(`points.${point}`)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
