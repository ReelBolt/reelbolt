import { Link } from '@/i18n/link';
import { useT } from '@/i18n';
import { Section } from '@/components/layout/Section';
import { CAPABILITIES, isPaidGate, type Audience, type CapabilityId } from '@/lib/audiences/registry';
import { planNamesFor, resolutionTiers } from '@/lib/audiences/plan-gates';
import { joinNamesT } from '@/lib/pricing/localized';

/**
 * "What you can make today" for one audience. The list comes from the audience registry, so a
 * page can only name a capability that ships; each card says whether it runs on the free trial or
 * needs a paid plan, and the plan names are read from the catalog, never typed.
 */
export function CapabilityList({ audience }: { audience: Audience }) {
  const t = useT('audiences');
  const tp = useT('plans');
  // "No watermark" comes with a different export ceiling on different plans. Each plan is named
  // with its OWN ceiling, read from the catalog: "up to 1080p on Creator and up to 4K on Pro, …".
  const tiers = joinNamesT(
    tp,
    resolutionTiers().map((tier) =>
      t('page.make.resolutionTier', {
        resolution: tier.resolution,
        plans: joinNamesT(tp, tier.plans),
      }),
    ),
  );

  const gateLabel = (id: CapabilityId): string => {
    const { gate } = CAPABILITIES[id];
    if (isPaidGate(gate)) return t('page.make.gate.paid', { plans: joinNamesT(tp, planNamesFor(gate)) });
    return t('page.make.gate.trial');
  };

  return (
    <Section id="make">
      <div className="max-w-3xl">
        <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
          {t('page.make.title')}
        </h2>
        <p className="mt-4 text-ink-muted">{t('page.make.intro')}</p>
      </div>
      <ul className="mt-10 grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
        {audience.capabilities.map((id) => {
          const paid = isPaidGate(CAPABILITIES[id].gate);
          return (
            <li key={id} className={`flex flex-col border bg-paper-2 p-6 ${paid ? 'border-dashed border-line-strong' : 'border-line'}`}>
              <span
                className={`self-start border px-2 py-1 font-mono text-[11px] uppercase tracking-eyebrow ${
                  paid ? 'border-line-strong text-ink-muted' : 'border-accent bg-accent-tint text-accent-ink'
                }`}
              >
                {gateLabel(id)}
              </span>
              <h3 className="mt-4 font-display text-base font-bold text-ink">{t(`capabilities.${id}.title`)}</h3>
              <p className="mt-2 text-sm text-ink-muted">{t(`capabilities.${id}.description`, { tiers })}</p>
            </li>
          );
        })}
      </ul>
      <p className="mt-10">
        <Link href="/pricing" className="font-medium text-accent-ink underline underline-offset-4">
          {t('page.make.pricing')}
        </Link>
      </p>
    </Section>
  );
}
