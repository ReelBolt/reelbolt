import { useT, useFormat } from '@/i18n';
import type { Plan } from '@/lib/pricing';
import { planMaxResolution } from '@/lib/pricing/offers';
import { assistantAllowanceT, creditsLabelT, storageLabelT } from '@/lib/pricing/localized';

// Every row reads a field the billing service also enforces, so the table cannot promise something
// the product does not do. "Exports without a watermark" is phrased as the benefit rather than as
// the raw `watermark` flag, which is true for the trial and would read as a feature there.

// Every row reads a field the billing service also enforces, so the table cannot promise something
// the product does not do. "Exports without a watermark" is phrased as the benefit rather than as
// the raw `watermark` flag, which is true for the trial and would read as a feature there.
const ROW_KEYS = ['credits', 'watermark', 'resolution', 'assistant', 'storage', 'byo', 'rooms', 'videoGen', 'topUp', 'priority'] as const;

const CAPTION_ID = 'plan-comparison-caption';

export function PlanComparison({ plans }: { plans: readonly Plan[] }) {
  const t = useT('plans');
  const f = useFormat();
  const yesNo = (v: boolean) => t(v ? 'table.yes' : 'table.no');
  // The catalog sets no storage figure for Enterprise, so its cell says so rather than guessing.
  const value = (key: (typeof ROW_KEYS)[number], plan: Plan): string => {
    switch (key) {
      case 'credits': return creditsLabelT(t, f, plan);
      case 'watermark': return yesNo(!plan.limits.watermark);
      case 'resolution': return planMaxResolution(plan);
      case 'assistant': return assistantAllowanceT(t, f, plan);
      case 'storage': return storageLabelT(t, plan) ?? t('table.custom');
      case 'byo': return yesNo(plan.limits.byoProviders);
      case 'rooms': return yesNo(plan.limits.rooms);
      case 'videoGen': return yesNo(plan.limits.videoGeneration);
      case 'topUp': return yesNo(plan.topUpAllowed);
      case 'priority': return t(`comparison.priority.${plan.limits.priority}`);
    }
  };
  const CAPTION = t('comparison.caption');
  // The caption sits outside the scroller so the region's name and focus stay on the table. The
  // right-edge fade is a pseudo-element on the wrapper, so it does not scroll away with the rows.
  return (
    <div className="relative mt-10 after:pointer-events-none after:absolute after:inset-y-0 after:right-0 after:w-10 after:bg-linear-to-l after:from-paper after:to-transparent lg:after:hidden">
      <p id={CAPTION_ID} className="sr-only">
        {CAPTION}
      </p>
      <div
        role="region"
        aria-label={CAPTION}
        tabIndex={0}
        className="overflow-x-auto outline-offset-2 focus-visible:outline-2 focus-visible:outline-accent-strong"
      >
        <table aria-labelledby={CAPTION_ID} className="w-full min-w-[42rem] border-collapse text-left">
          <thead>
            <tr className="border-b border-line-strong">
              <th scope="col" className="sticky left-0 z-20 bg-paper py-4 pr-4 font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
                {t('comparison.compare')}
              </th>
              {plans.map((plan) => (
                <th
                  key={plan.id}
                  scope="col"
                  className="px-4 py-4 font-display text-base font-bold text-ink"
                >
                  {plan.name}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {ROW_KEYS.map((row) => (
              <tr key={row} className="border-b border-line align-top">
                <th scope="row" className="sticky left-0 z-10 bg-paper py-4 pr-4 text-sm font-normal text-ink-muted">
                  {t(`comparison.rows.${row}`)}
                </th>
                {plans.map((plan) => (
                  <td key={plan.id} className="px-4 py-4 text-sm text-ink">
                    {value(row, plan)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
