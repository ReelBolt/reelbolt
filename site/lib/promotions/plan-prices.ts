// Plan prices for the pricing cards (GET /api/v1/billing/promotions/plan-prices, merged by /api/promotions).
//
// Every money string is the payment provider's, shown verbatim. This module never parses, formats or computes an
// amount; the only comparison is that `original` and `discounted` differ. Pure, and never throws:
// anything malformed is dropped, so a junk response reads as "no plan prices".

// The duration is validated so a malformed item is dropped, but it is never rendered: the site does not
// say how long a discount lasts (Owner ruling); the pricing card shows the offer's end date instead.
export type PlanPriceDurationKind = 'once' | 'months' | 'forever';

export type PlanPrice = {
  planKey: string;
  perSeat: boolean;
  currencyCode: string;
  original: string;
  discounted: string;
  headline: string | null;
  durationKind: PlanPriceDurationKind;
  durationMonths: number | null;
  endsAt: string | null;
};

/** Longest string the site shows from a plan price, in characters (code points). */
export const PLAN_PRICE_TEXT_MAX_CHARS = 32;

const DURATION_KINDS: readonly PlanPriceDurationKind[] = ['once', 'months', 'forever'];

type Fields = Record<string, unknown>;

function isRecord(value: unknown): value is Fields {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The value when it is a non-blank string of at most PLAN_PRICE_TEXT_MAX_CHARS characters, else null. */
function shortText(value: unknown): string | null {
  if (typeof value !== 'string' || value.trim() === '') return null;
  return Array.from(value).length <= PLAN_PRICE_TEXT_MAX_CHARS ? value : null;
}

/** One raw item to a plan price, or null when it is malformed, of an unknown duration, or already ended. */
function parseItem(raw: unknown, now: Date): PlanPrice | null {
  if (!isRecord(raw)) return null;

  const planKey = shortText(raw.planKey);
  const currencyCode = shortText(raw.currencyCode);
  const original = shortText(raw.original);
  const discounted = shortText(raw.discounted);
  if (planKey === null || currencyCode === null || original === null || discounted === null) return null;
  if (original === discounted) return null;
  if (typeof raw.perSeat !== 'boolean') return null;

  // The headline is optional: null or absent reads as no headline. A present one must be a usable string.
  // The money strings are capped at PLAN_PRICE_TEXT_MAX_CHARS; the headline is not (the API caps it
  // at 120 like /active). A malformed headline is ignored, never a reason to hide the provider's price.
  const headline =
    typeof raw.headline === 'string' && raw.headline.trim() !== '' ? raw.headline.trim().slice(0, 120) : null;

  const durationKind = DURATION_KINDS.find((k) => k === raw.durationKind) ?? null;
  if (durationKind === null) return null;
  let durationMonths: number | null = null;
  if (durationKind === 'months') {
    const months = raw.durationMonths;
    if (typeof months !== 'number' || !Number.isInteger(months) || months < 1) return null;
    durationMonths = months;
  }

  // A null or absent end date means open-ended. Anything else must be a short, parseable timestamp
  // still in the future: `endsAt` at or before `now` has ended.
  let endsAt: string | null = null;
  if (raw.endsAt !== null && raw.endsAt !== undefined) {
    endsAt = shortText(raw.endsAt);
    if (endsAt === null) return null;
    const endsMs = Date.parse(endsAt);
    if (!Number.isFinite(endsMs) || endsMs <= now.getTime()) return null;
  }

  return {
    planKey,
    perSeat: raw.perSeat,
    currencyCode,
    original,
    discounted,
    headline,
    durationKind,
    durationMonths,
    endsAt,
  };
}

/**
 * Reads the upstream body `{ items: [...] }` into the plan prices that are live at `now`. Malformed
 * items, unknown duration kinds and ended prices are dropped. Never throws: anything else reads as an
 * empty list.
 */
export function parsePlanPrices(json: unknown, now: Date): PlanPrice[] {
  try {
    if (!isRecord(json) || !Array.isArray(json.items)) return [];
    const out: PlanPrice[] = [];
    for (const raw of json.items) {
      const price = parseItem(raw, now);
      if (price) out.push(price);
    }
    return out;
  } catch {
    return [];
  }
}

/** The first plan price for `planKey`, or null when that plan has none. */
export function planPriceFor(items: readonly PlanPrice[], planKey: string): PlanPrice | null {
  return items.find((p) => p.planKey === planKey) ?? null;
}
