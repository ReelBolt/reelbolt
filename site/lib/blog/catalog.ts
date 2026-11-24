// Figures for blog copy. Every `{catalog:<plan>.<field>}` placeholder in a draft renders through
// this function, so a number in an article can only come from the plan catalog in site/lib/pricing.
import { currency, plans, topUp } from '@/lib/pricing';
import { formatMoney, formatNumber, planById, priceUnit, resolutionLabel } from '@/lib/pricing/offers';

/**
 * Resolves a catalog path such as `team.priceMonthly`, `pro.entitlements.maxOutputHeight` or
 * `topUp.expiresAfterMonths` to display text. A per-seat price carries the unit the pricing page
 * uses (`priceUnit`); every other price is written with its period in the surrounding sentence.
 * Throws on an unknown path, so a placeholder can never render empty.
 */
export function catalog(path: string): string {
  const dot = path.indexOf('.');
  const head = dot === -1 ? path : path.slice(0, dot);
  const field = dot === -1 ? '' : path.slice(dot + 1);

  if (head === 'topUp') {
    if (!topUp) throw new Error('plans.catalog.json has no topUp block');
    if (field === 'expiresAfterMonths') return formatNumber(topUp.expiresAfterMonths);
    throw new Error(`Unsupported catalog path "${path}"`);
  }

  const plan = planById(plans, head);

  if (field === 'creditsOneTime') {
    if (plan.creditsOneTime === undefined) throw new Error(`Plan "${head}" has no creditsOneTime`);
    return formatNumber(plan.creditsOneTime);
  }
  if (field === 'priceMonthly') {
    if (plan.priceMonthly === undefined) throw new Error(`Plan "${head}" has no priceMonthly`);
    const money = formatMoney(currency, plan.priceMonthly);
    return plan.kind === 'perSeat' ? `${money} per ${priceUnit(plan).unitText}` : money;
  }
  if (field === 'entitlements.maxOutputHeight') {
    const height = plan.entitlements.maxOutputHeight;
    if (height === null || height === undefined) throw new Error(`Plan "${head}" has no maxOutputHeight`);
    return resolutionLabel(height);
  }

  throw new Error(`Unsupported catalog path "${path}"`);
}
