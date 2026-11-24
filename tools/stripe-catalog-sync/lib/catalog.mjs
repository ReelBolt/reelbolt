import { readFile } from 'node:fs/promises';
import { toMinorUnits } from './money.mjs';

export const MARKER_KEY = 'reelbolt_catalog';
export const OWNER_KEY = 'reelbolt_key';

export async function readJsonFile(path, label) {
  let text;
  try {
    text = await readFile(path, 'utf8');
  } catch (error) {
    throw new Error(`cannot read ${label} at ${path}: ${error.code ?? error.message}`);
  }
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${label} at ${path} is not valid JSON`);
  }
}

/**
 * Turn the canonical plans.catalog.json into the Stripe objects we want. Every amount comes
 * from the catalog; nothing in this tool carries a price of its own.
 */
export function buildDesired(catalog, { lookupPrefix = 'rb' } = {}) {
  if (!catalog || typeof catalog !== 'object' || !Array.isArray(catalog.plans)) {
    throw new Error('catalog has no "plans" array');
  }
  const currency = String(catalog.currency ?? '').toLowerCase();
  if (!/^[a-z]{3}$/.test(currency)) {
    throw new Error('catalog "currency" must be a three-letter ISO code');
  }

  const plans = [];
  const skippedPlans = [];
  for (const plan of catalog.plans) {
    const id = String(plan.id ?? '');
    if (!/^[a-z0-9][a-z0-9_-]*$/.test(id)) {
      throw new Error(`plan id ${JSON.stringify(plan.id)} is not a safe identifier`);
    }
    if (plan.kind !== 'subscription' && plan.kind !== 'perSeat') {
      skippedPlans.push({ planId: id, reason: `kind "${plan.kind}" has no provider price` });
      continue;
    }
    const unitAmount = toMinorUnits(plan.priceMonthly, currency);
    const annual =
      plan.priceAnnual === undefined || plan.priceAnnual === null
        ? null
        : { lookupKey: `${lookupPrefix}:${id}:year:${currency}`, unitAmount: toMinorUnits(plan.priceAnnual, currency) };
    plans.push({
      planId: id,
      annual,
      name: String(plan.name ?? id),
      kind: plan.kind,
      productId: `${lookupPrefix}_plan_${id}`,
      lookupKey: `${lookupPrefix}:${id}:month:${currency}`,
      currency,
      unitAmount,
      minSeats: plan.kind === 'perSeat' && Number.isInteger(plan.minSeats) ? plan.minSeats : null,
    });
  }

  const packs = [];
  const seen = new Set();
  for (const pack of catalog.topUp?.packs ?? []) {
    if (!Number.isInteger(pack.credits) || pack.credits <= 0) {
      throw new Error(`top-up pack credits ${JSON.stringify(pack.credits)} must be a positive integer`);
    }
    if (seen.has(pack.credits)) {
      throw new Error(`duplicate top-up pack of ${pack.credits} credits`);
    }
    seen.add(pack.credits);
    packs.push({
      credits: pack.credits,
      productId: `${lookupPrefix}_topup_${pack.credits}`,
      lookupKey: `${lookupPrefix}:topup:${pack.credits}:${currency}`,
      currency,
      unitAmount: toMinorUnits(pack.price, currency),
    });
  }

  if (plans.length === 0 && packs.length === 0) {
    throw new Error('catalog yields nothing to sync');
  }
  return { currency, plans, skippedPlans, packs };
}

const COUPON_NAME_MAX = 40;
export const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Printable, bounded form of an untrusted id for the diff table. */
function displayId(value) {
  const text = String(value).replace(/[^\x20-\x7e]/g, '?');
  return text.length > 60 ? `${text.slice(0, 57)}...` : text;
}
const FIVE_YEARS_SEC = 5 * 365 * 24 * 3600;

/**
 * Map exported promotion rows to desired coupons. Returns { eligible, skipped, notes }.
 * Anything we cannot express exactly is skipped with a reason; we never guess a target.
 */
export function mapPromotions(rows, desired, { includeInactive = false, lookupPrefix = 'rb', now = new Date() } = {}) {
  if (!Array.isArray(rows)) {
    throw new Error('promotions file must be a JSON array');
  }
  const eligible = [];
  const skipped = [];
  const nowSec = Math.floor(now.getTime() / 1000);
  const planProducts = new Map(desired.plans.map((p) => [p.planId, p.productId]));
  const packProducts = new Map(desired.packs.map((p) => [p.credits, p.productId]));
  const seen = new Set();

  for (const row of rows) {
    const promotionId = String(row?.id ?? '');
    const skip = (reason) => skipped.push({ promotionId: promotionId ? displayId(promotionId) : '(missing id)', reason });
    if (promotionId === '') {
      skip('row has no id');
      continue;
    }
    // The id becomes part of a Stripe URL path (/v1/coupons/{id}) and a SQL literal: only a
    // UUID may get that far, so dot-segments, slashes and quotes can never reach either.
    if (!UUID_PATTERN.test(promotionId)) {
      skip('id is not a UUID');
      continue;
    }
    if (seen.has(promotionId)) {
      skip('duplicate id in the export');
      continue;
    }
    seen.add(promotionId);

    const kind = String(row.kind ?? '');
    if (kind !== 'PlanDiscount' && kind !== 'PackDiscount') {
      skip(`kind ${kind || '(none)'} has no provider discount`);
      continue;
    }
    const status = String(row.status ?? '').toLowerCase();
    if (status !== 'active' && !includeInactive) {
      skip(`status ${row.status ?? '(none)'} (use --include-inactive to sync it)`);
      continue;
    }

    try {
      const couponId = `${lookupPrefix}_promo_${promotionId}`;
      const coupon = {
        id: couponId,
        name: String(row.name ?? promotionId).slice(0, COUPON_NAME_MAX),
        metadata: { [MARKER_KEY]: '1', [OWNER_KEY]: couponId, promotion_id: promotionId },
      };

      const type = String(row.discountType ?? '');
      if (type === 'Percentage') {
        const pct = typeof row.discountAmount === 'string' && row.discountAmount.trim() === '' ? Number.NaN : Number(row.discountAmount);
        if (!(pct > 0 && pct <= 100)) {
          throw new Error('percentage must be above 0 and at most 100');
        }
        coupon.percent_off = pct;
      } else if (type === 'Flat') {
        const currency = String(row.currencyCode ?? '').toLowerCase();
        if (currency !== desired.currency) {
          throw new Error(`flat discount currency ${currency || '(none)'} differs from the catalog currency ${desired.currency}`);
        }
        // The promotions table keeps Paddle's format: flat amounts are WHOLE MINOR UNITS ("500"
        // is 5.00 USD). No conversion here; just require a positive integer.
        const text = String(row.discountAmount ?? '').trim();
        if (!/^[1-9]\d{0,14}$/.test(text)) {
          throw new Error('flat discountAmount must be a positive whole number of minor units');
        }
        coupon.amount_off = Number(text);
        coupon.currency = currency;
      } else {
        throw new Error(`discountType ${type || '(none)'} is not Percentage or Flat`);
      }

      const durationKind = String(row.durationKind ?? '');
      if (durationKind === 'Once') {
        coupon.duration = 'once';
      } else if (durationKind === 'Months') {
        if (!Number.isInteger(row.durationMonths) || row.durationMonths < 1) {
          throw new Error('durationMonths must be a positive integer');
        }
        coupon.duration = 'repeating';
        coupon.duration_in_months = row.durationMonths;
      } else if (durationKind === 'Forever') {
        coupon.duration = 'forever';
      } else {
        throw new Error(`durationKind ${durationKind || '(none)'} is not Once, Months or Forever`);
      }

      if (row.maxRedemptions !== null && row.maxRedemptions !== undefined) {
        if (!Number.isInteger(row.maxRedemptions) || row.maxRedemptions < 1) {
          throw new Error('maxRedemptions must be a positive integer');
        }
        coupon.max_redemptions = row.maxRedemptions;
      }

      if (row.endsAt) {
        const ms = Date.parse(row.endsAt);
        if (Number.isNaN(ms)) {
          throw new Error('endsAt is not a date');
        }
        const redeemBy = Math.floor(ms / 1000);
        if (redeemBy <= nowSec) {
          throw new Error('endsAt is in the past');
        }
        if (redeemBy > nowSec + FIVE_YEARS_SEC) {
          throw new Error('endsAt is more than 5 years ahead (Stripe limit)');
        }
        coupon.redeem_by = redeemBy;
      }

      const { products, keys } = resolveProducts(row, kind, planProducts, packProducts);
      coupon.applies_to = { products };
      // `targets` is what the SQL needs to point the promotion's target_price_ids_json at the
      // new provider's prices (keys are already expanded: an empty export list means every one).
      const targets = kind === 'PlanDiscount' ? { kind: 'plan', planKeys: keys } : { kind: 'pack', packCredits: keys };
      eligible.push({ promotionId, couponId: coupon.id, coupon, targets });
    } catch (error) {
      skip(error.message);
    }
  }
  return { eligible, skipped };
}

function resolveProducts(row, kind, planProducts, packProducts) {
  const configured = kind === 'PlanDiscount' ? row.targetPlanKeys : row.targetPackCredits;
  const table = kind === 'PlanDiscount' ? planProducts : packProducts;
  const wanted = Array.isArray(configured) && configured.length > 0 ? configured : [...table.keys()];
  const products = [];
  const keys = [];
  for (const raw of wanted) {
    const key = kind === 'PlanDiscount' ? String(raw) : Number(raw);
    const product = table.get(key);
    if (!product) {
      throw new Error(`target ${JSON.stringify(raw)} is not a synced ${kind === 'PlanDiscount' ? 'plan' : 'pack'}`);
    }
    products.push(product);
    keys.push(key);
  }
  if (products.length === 0) {
    throw new Error('no target products');
  }
  return { products: [...new Set(products)].sort(), keys: [...new Set(keys)] };
}

/**
 * The public-alpha founder price as a coupon entry, built from founder-offer.json (the only
 * place its numbers live). Same shape as a mapped promotion, so it reconciles like one.
 * `promotionId` is the optional uuid of the platform promotion row to point at the coupon.
 * Once the offer window has closed it returns `{ skip: true, couponId, reason }` instead of
 * throwing, so the tool keeps working after 31 January 2027 (the coupon is left untouched).
 * Every other problem with the offer file stays a fatal config error.
 */
export function founderEntry(offer, desired, { lookupPrefix = 'rb', promotionId = null, now = new Date() } = {}) {
  if (!offer || typeof offer !== 'object') {
    throw new Error('founder offer file must be a JSON object');
  }
  if (!/^[a-z][a-z0-9_]{0,40}$/.test(String(offer.couponKey ?? ''))) {
    throw new Error('founder offer needs a couponKey of lowercase letters, digits and underscores');
  }
  const pct = Number(offer.percentOff);
  if (!(pct > 0 && pct <= 100)) {
    throw new Error('founder offer percentOff must be above 0 and at most 100');
  }
  if (offer.duration !== 'forever') {
    throw new Error('founder offer duration must be "forever"');
  }
  if (!/[+-]\d{2}:\d{2}$|Z$/.test(String(offer.redeemBy ?? ''))) {
    throw new Error('founder offer redeemBy must be an ISO date-time with an explicit offset (for example +01:00)');
  }
  const ms = Date.parse(offer.redeemBy);
  if (Number.isNaN(ms)) {
    throw new Error('founder offer redeemBy is not a date');
  }
  const redeemBy = Math.floor(ms / 1000);
  const windowClosed = redeemBy <= Math.floor(now.getTime() / 1000);
  if (offer.appliesTo !== 'subscriptionPlans') {
    throw new Error('founder offer appliesTo must be "subscriptionPlans"');
  }
  const products = [...new Set(desired.plans.map((p) => p.productId))].sort();
  if (products.length === 0) {
    throw new Error('founder offer has no subscription plan products to apply to');
  }
  if (promotionId !== null && !UUID_PATTERN.test(promotionId)) {
    throw new Error('--founder-promotion-id must be a UUID');
  }
  if (offer.maxRedemptions !== null && offer.maxRedemptions !== undefined && (!Number.isInteger(offer.maxRedemptions) || offer.maxRedemptions < 1)) {
    throw new Error('founder offer maxRedemptions must be a positive integer');
  }
  const couponId = `${lookupPrefix}_${offer.couponKey}`;
  if (windowClosed) {
    return { skip: true, couponId, reason: 'founder window closed (redeem_by passed); existing coupon left untouched' };
  }
  const coupon = {
    id: couponId,
    name: String(offer.name ?? 'Founder price').slice(0, COUPON_NAME_MAX),
    metadata: { [MARKER_KEY]: '1', [OWNER_KEY]: 'founder' },
    percent_off: pct,
    duration: 'forever',
    redeem_by: redeemBy,
    applies_to: { products },
  };
  if (offer.maxRedemptions !== null && offer.maxRedemptions !== undefined) {
    coupon.max_redemptions = offer.maxRedemptions;
  }
  // The founder price covers every subscription and per-seat plan, never a top-up pack.
  const targets = { kind: 'plan', planKeys: desired.plans.map((p) => p.planId) };
  return { promotionId, couponId, coupon, founder: true, targets };
}
