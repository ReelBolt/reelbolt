import { useT, useFormat } from '@/i18n';
import { Button } from '@/components/ui/Button';
import { Panel } from '@/components/ui/Panel';
import { PlanCtaButton } from '@/components/pricing/PlanCtaButton';
import { PlanPromoBadge } from '@/components/promo/PlanPromoBadge';
import { PlanPromoPrice } from '@/components/promo/PlanPromoPrice';
import type { Plan } from '@/lib/pricing';
import { annualMonthlyEquivalent, annualMonthsFree, planCtaHref } from '@/lib/pricing/offers';
import { creditsLabelT, formatMoneyT, planCardLinesT, planCtaLabelT } from '@/lib/pricing/localized';

/** What sits next to the amount: the recurring unit, or "to start" on the one-time trial. */
function priceSuffix(plan: Plan, t: ReturnType<typeof useT>): string {
  if (plan.kind === 'perSeat') return t('perSeatMonth');
  if (plan.kind === 'subscription') return t('perMonth');
  return t('toStart');
}

function Price({ plan, currency }: { plan: Plan; currency: string }) {
  const t = useT('plans.card');
  const f = useFormat();
  // A plan with no published price is the enterprise path: it says so instead of showing a figure
  // nobody has agreed to (decision D7 — Enterprise is "Contact us" only).
  if (plan.kind === 'contact') {
    return (
      <p className="mt-4 font-display text-3xl font-bold text-ink">
        {t('contactUs')}
      </p>
    );
  }

  const amount = plan.kind === 'trial' ? 0 : plan.priceMonthly;
  if (amount === undefined) return null;

  // A client island: it shows a live promotion over this catalog price, and reserves the same
  // block height without one, so the cards in a row stay aligned. The trial is never discounted.
  const perMonth = annualMonthlyEquivalent(plan);
  const monthly = (
    <PlanPromoPrice
      planId={plan.id}
      catalog={formatMoneyT(f, amount, currency)}
      unit={priceSuffix(plan, t)}
      promoEligible={plan.kind !== 'trial'}
    />
  );
  if (perMonth === undefined || plan.priceAnnual === undefined) return monthly;

  const free = annualMonthsFree(plan);
  // Both views sit in one grid cell, so the block is as tall as the taller one and the switch
  // moves nothing. The hidden view is out of the accessibility tree and the tab order.
  return (
    <div className="grid [&>*]:col-start-1 [&>*]:row-start-1">
      <div className="group-data-[billing=annual]/plans:invisible">{monthly}</div>
      <div className="invisible group-data-[billing=annual]/plans:visible" data-view="annual">
        <div className="mt-4">
          <p className="min-h-5 font-sans text-xs leading-5 text-ink-muted">
            {free !== undefined ? t('monthsFree', { count: free }) : ''}
          </p>
          <p className="flex flex-wrap items-baseline gap-x-2">
            <span className="font-display text-4xl font-bold tracking-tight text-ink">
              {formatMoneyT(f, perMonth, currency, 2)}
            </span>
            <span className="font-sans text-xs text-ink-muted">{priceSuffix(plan, t)}</span>
          </p>
          <p className="min-h-5 font-sans text-xs leading-5 text-ink-muted">
            {t(plan.kind === 'perSeat' ? 'billedYearlySeat' : 'billedYearly', {
              total: formatMoneyT(f, plan.priceAnnual, currency),
            })}
          </p>
        </div>
      </div>
    </div>
  );
}

export function PlanCard({
  plan,
  currency,
  signupEnabled,
  sharedLines = [],
}: {
  plan: Plan;
  currency: string;
  signupEnabled: boolean;
  /** Benefit lines the page prints once under the grid, so this card leaves them out. */
  sharedLines?: readonly string[];
}) {
  const t = useT('plans.card');
  const tp = useT('plans');
  const f = useFormat();
  const featured = plan.featured === true;
  const href = planCtaHref(plan, signupEnabled);

  return (
    // The anchor is the card itself: the Offer markup points at /pricing#<id>, so a search result
    // for a price lands on the price. `scroll-mt` keeps it clear of the sticky header.
    <div id={plan.id} className="scroll-mt-24">
      <Panel
        tone="paper-2"
        brackets={featured}
        bordered={featured}
        className={`flex h-full flex-col p-6 ${featured ? 'border-accent-strong' : ''}`}
      >
        {/* A client island: renders only while a plan discount applies, and is positioned over the
            card's top edge so it never moves the content below it. */}
        <PlanPromoBadge plan={plan} />
        <div className="flex items-start justify-between gap-3">
          <h3 className="font-display text-xl font-bold text-ink">
            {plan.name}
          </h3>
          {featured && (
            <span className="border border-accent-strong px-2 py-1 font-mono text-[10px] uppercase tracking-eyebrow text-accent-ink">
              {t('mostPopular')}
            </span>
          )}
        </div>

        <Price plan={plan} currency={currency} />

        <p className="mt-3 font-sans text-xs leading-relaxed text-ink-muted">{creditsLabelT(tp, f, plan)}</p>
        {plan.priceAnnual !== undefined && (
          <p className="invisible font-sans text-xs leading-relaxed text-ink-muted group-data-[billing=annual]/plans:visible">
            {t('creditsEveryMonth')}
          </p>
        )}
        {plan.kind === 'perSeat' && plan.minSeats !== undefined && (
          <p className="mt-1 font-sans text-xs text-ink-faint">
            {t('minSeats', { count: plan.minSeats })}
          </p>
        )}

        <ul className="mt-6 space-y-2 text-sm text-ink-muted">
          {planCardLinesT(tp, f, plan, sharedLines).map((highlight) => (
            <li key={highlight} className="flex items-start gap-3">
              <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 bg-accent" />
              <span>{highlight}</span>
            </li>
          ))}
        </ul>

        {/* Pushes the buttons onto one line across the row, however long the lists above are. */}
        <div className="mt-auto pt-8">
          {plan.cta === 'contact' ? (
            // Not the primary CTA: "Contact us" is not the site's main action, and it must not
            // inherit the "Start free" event or label.
            <Button href={href} variant="outline" size="lg" block>
              {planCtaLabelT(tp, plan, signupEnabled)}
            </Button>
          ) : (
            <PlanCtaButton
              location={`pricing-${plan.id}`}
              href={href}
              label={planCtaLabelT(tp, plan, signupEnabled)}
              variant={featured ? 'accent' : 'outline'}
            />
          )}
        </div>
      </Panel>
    </div>
  );
}
