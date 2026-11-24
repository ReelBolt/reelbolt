// Which plans a paid capability needs, read from the plan catalog and never typed. An audience page
// says "On Pro, Team and Enterprise" only because the catalog's own entitlement says so; a plan
// change in the catalog moves the words on the next build.
//
// The typed loader in `lib/pricing` normalises the entitlements the pricing pages render. Two of
// the gates here (`audioGenAllowed`, `apiAccess`) are not among them, so this module reads the
// vendored catalog file directly. Pure, so `node --test` can load it.
import catalog from '../pricing/plans.catalog.json' with { type: 'json' };
import type { PaidGate } from './registry.ts';

interface CatalogPlan {
  id: string;
  name: string;
  kind: string;
  creditsOneTime?: number;
  limits: { maxResolution: string; watermark: boolean };
  entitlements?: Record<string, unknown>;
}

const PLANS = (catalog as { plans: CatalogPlan[] }).plans;

const flag = (key: string) => (plan: CatalogPlan) => plan.entitlements?.[key] === true;

const GATE_TEST: Record<PaidGate, (plan: CatalogPlan) => boolean> = {
  videoGen: flag('videoGenAllowed'),
  audioGen: flag('audioGenAllowed'),
  ownProvider: flag('byoAllowed'),
  api: flag('apiAccess'),
  noWatermark: (plan) => plan.limits.watermark === false,
};

/** The names of the plans that include a paid capability, in catalog order. */
export function planNamesFor(gate: PaidGate, plans: readonly CatalogPlan[] = PLANS): string[] {
  return plans.filter(GATE_TEST[gate]).map((plan) => plan.name);
}

/** The free trial's own facts, for the lines that state them: credits, export ceiling, watermark. */
export function trialFacts(plans: readonly CatalogPlan[] = PLANS): { credits: number; resolution: string; watermark: boolean } {
  const trial = plans.find((plan) => plan.kind === 'trial');
  if (!trial || trial.creditsOneTime === undefined) {
    throw new Error('plans.catalog.json has no trial plan with one-time credits');
  }
  return { credits: trial.creditsOneTime, resolution: trial.limits.maxResolution, watermark: trial.limits.watermark };
}

export interface ResolutionTier {
  /** The export ceiling as the catalog labels it (`1080p`, `4K`). */
  resolution: string;
  /** The names of the plans WITHOUT a watermark whose ceiling this is, in catalog order. */
  plans: string[];
}

/**
 * What "no watermark" comes with, plan by plan: the plans without a watermark, grouped by their
 * own export ceiling, lowest first. The card that says "no watermark" names a resolution too, and
 * the plans in its label do not share one, so each plan is listed with the ceiling the catalog
 * gives IT. One sentence with the highest ceiling for all of them told Creator buyers they get 4K.
 *
 * A plan with no numeric `maxOutputHeight` (no cap, by arrangement) sorts with the tier its label
 * puts it in.
 */
export function resolutionTiers(plans: readonly CatalogPlan[] = PLANS): ResolutionTier[] {
  const tiers = new Map<string, { plans: string[]; height: number }>();
  for (const plan of plans) {
    if (plan.limits.watermark !== false) continue;
    const height = typeof plan.entitlements?.maxOutputHeight === 'number' ? plan.entitlements.maxOutputHeight : Infinity;
    const tier = tiers.get(plan.limits.maxResolution);
    if (tier) {
      tier.plans.push(plan.name);
      tier.height = Math.min(tier.height, height);
    } else {
      tiers.set(plan.limits.maxResolution, { plans: [plan.name], height });
    }
  }
  if (tiers.size === 0) throw new Error('plans.catalog.json has no plan without a watermark');
  return [...tiers.entries()]
    .sort(([, a], [, b]) => a.height - b.height)
    .map(([resolution, tier]) => ({ resolution, plans: tier.plans }));
}
