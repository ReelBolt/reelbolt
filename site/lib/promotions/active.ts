// Active promotions from the billing API (GET /api/v1/billing/promotions/active).
//
// The parsing and selection functions are pure and never throw: anything malformed is dropped, so a
// junk response reads as "no promotions". Nothing here computes or shows a price. Display text is
// the admin headline, a percent from `percentOff`, or the fallback copy. `amountOffMinor` is
// deliberately not part of the type.
import { PROMO_COPY } from '../../components/promo/copy.ts';
import { FOUNDER_PRICE_ENDS_ON, founderPriceOpen } from '../launch-offer.ts';
import { parsePlanPrices, type PlanPrice } from './plan-prices.ts';

export type PromotionKind = 'planDiscount' | 'packDiscount' | 'packBonus';
export type PromotionDiscountType = 'percentage' | 'flat';
export type PromotionDurationKind = 'once' | 'months' | 'forever';

export interface ActivePromotion {
  kind: PromotionKind;
  headline: string | null;
  endsAt: string | null;
  discountType: PromotionDiscountType | null;
  percentOff: number | null;
  bonusPercent: number | null;
  planKeys: string[];
  packCredits: number[];
  durationKind: PromotionDurationKind | null;
  durationMonths: number | null;
}

const KINDS: readonly PromotionKind[] = ['planDiscount', 'packDiscount', 'packBonus'];
const DISCOUNT_TYPES: readonly PromotionDiscountType[] = ['percentage', 'flat'];
const DURATION_KINDS: readonly PromotionDurationKind[] = ['once', 'months', 'forever'];

type Fields = Record<string, unknown>;

function isRecord(value: unknown): value is Fields {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : null;
}

function finiteOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function stringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string' && v.length > 0) : [];
}

function numberList(value: unknown): number[] {
  return Array.isArray(value)
    ? value.filter((v): v is number => typeof v === 'number' && Number.isFinite(v))
    : [];
}

/** Longest headline shown anywhere on the site, in characters (code points). */
export const HEADLINE_MAX_CHARS = 120;

/** Trims the admin headline and caps it at HEADLINE_MAX_CHARS, ending a cut headline with an ellipsis. */
function capHeadline(value: string): string | null {
  const trimmed = value.trim();
  if (trimmed === '') return null;
  const chars = Array.from(trimmed);
  if (chars.length <= HEADLINE_MAX_CHARS) return trimmed;
  return `${chars.slice(0, HEADLINE_MAX_CHARS - 1).join('').trimEnd()}…`;
}

/** One raw item to a promotion, or null when it is malformed, of an unknown kind, or already ended. */
function parseItem(raw: unknown, now: Date): ActivePromotion | null {
  if (!isRecord(raw)) return null;
  const kind = oneOf(raw.kind, KINDS);
  if (kind === null) return null;

  // A null or absent end date means open-ended. Anything else must be a parseable timestamp that is
  // still in the future: `endsAt` at or before `now` has ended.
  let endsAt: string | null = null;
  if (raw.endsAt !== null && raw.endsAt !== undefined) {
    if (typeof raw.endsAt !== 'string') return null;
    const endsMs = Date.parse(raw.endsAt);
    if (!Number.isFinite(endsMs) || endsMs <= now.getTime()) return null;
    endsAt = raw.endsAt;
  }

  // A percentage outside 0-100 cannot be a real discount; read it as absent rather than display it.
  const percentOff = finiteOrNull(raw.percentOff);
  const bonusPercent = finiteOrNull(raw.bonusPercent);

  return {
    kind,
    headline: typeof raw.headline === 'string' ? capHeadline(raw.headline) : null,
    endsAt,
    discountType: oneOf(raw.discountType, DISCOUNT_TYPES),
    percentOff: percentOff !== null && percentOff > 0 && percentOff <= 100 ? percentOff : null,
    bonusPercent: bonusPercent !== null && bonusPercent >= 0 ? bonusPercent : null,
    planKeys: stringList(raw.planKeys),
    packCredits: numberList(raw.packCredits),
    durationKind: oneOf(raw.durationKind, DURATION_KINDS),
    durationMonths: finiteOrNull(raw.durationMonths),
  };
}

/**
 * Reads the API body (or anything else) into the promotions that are live at `now`. Malformed items,
 * unknown kinds and ended promotions are dropped. Never throws: anything that is not `{ items: [...] }`
 * reads as an empty list.
 */
export function parseActivePromotions(json: unknown, now: Date): ActivePromotion[] {
  try {
    if (!isRecord(json) || !Array.isArray(json.items)) return [];
    const out: ActivePromotion[] = [];
    for (const raw of json.items) {
      const promotion = parseItem(raw, now);
      if (promotion) out.push(promotion);
    }
    return out;
  } catch {
    return [];
  }
}

/** Plan kinds that are never on sale: the free trial and the contact-only Enterprise plan. */
const NEVER_DISCOUNTED_KINDS: ReadonlySet<string> = new Set(['trial', 'contact']);

/**
 * Plan-discount promotions that apply to `plan`. An empty `planKeys` means every plan on sale (docs/billing.md),
 * so the trial and Enterprise plans never match, even when they are listed by id.
 */
export function promotionsForPlan(
  items: readonly ActivePromotion[],
  plan: { id: string; kind: string },
): ActivePromotion[] {
  if (NEVER_DISCOUNTED_KINDS.has(plan.kind)) return [];
  return items.filter(
    (p) => p.kind === 'planDiscount' && (p.planKeys.length === 0 || p.planKeys.includes(plan.id)),
  );
}

/** The promotion the site banner advertises: the first automatic plan or pack discount. */
export function bannerPromotion(items: readonly ActivePromotion[]): ActivePromotion | null {
  return items.find((p) => p.kind === 'planDiscount' || p.kind === 'packDiscount') ?? null;
}

/** "Offer open until 31 January 2027" from the one date constant in launch-offer.ts; empty once it has passed. */
export function offerOpenUntil(now: Date = new Date()): string {
  return founderPriceOpen(now) ? `Offer open until ${FOUNDER_PRICE_ENDS_ON}` : '';
}

/**
 * A plan discount never shows how long it lasts (Owner ruling): its label is built here from `percentOff`
 * and the one end date, and the admin headline, `durationKind` and `durationMonths` are never read for
 * it, so a coupon that lasts "forever" or "3 months" cannot leak into the copy through any of them.
 */
function planDiscountLabel(p: ActivePromotion, now: Date): string | null {
  if (p.kind !== 'planDiscount') return null;
  if (p.discountType !== 'percentage' || p.percentOff === null) return PROMO_COPY.fallbackOffer;
  const until = offerOpenUntil(now);
  return until ? `${p.percentOff}% off, ${until.charAt(0).toLowerCase()}${until.slice(1)}` : `${p.percentOff}% off`;
}

/** The short badge on a pricing card: "{percentOff}% off" for a plan discount (the card prints the end date). */
export function promoBadgeLabel(p: ActivePromotion, now: Date = new Date()): string {
  if (p.kind === 'planDiscount') {
    return p.discountType === 'percentage' && p.percentOff !== null ? `${p.percentOff}% off` : PROMO_COPY.fallbackOffer;
  }
  return promoLabel(p, now);
}

/**
 * Short display label. A plan discount reads "50% off, offer open until 31 January 2027" and never a
 * duration. Other promotions: the headline, else "{percentOff}% off" for a percentage, else the fallback.
 */
export function promoLabel(p: ActivePromotion, now: Date = new Date()): string {
  const planLabel = planDiscountLabel(p, now);
  if (planLabel !== null) return planLabel;
  const headline = p.headline?.trim();
  if (headline) return headline;
  if (p.discountType === 'percentage' && p.percentOff !== null) return `${p.percentOff}% off`;
  return PROMO_COPY.fallbackOffer;
}

export const PROMOTIONS_PATH = '/api/v1/billing/promotions/active';
export const PLAN_PRICES_PATH = '/api/v1/billing/promotions/plan-prices';
export const PROMOTIONS_TIMEOUT_MS = 3000;
export const PROMOTIONS_CACHE_CONTROL = 'public, max-age=60';

export interface PromotionsDeps {
  fetch?: typeof fetch;
  now?: () => Date;
  timeoutMs?: number;
}

/** The same-origin body: the live promotions and the plan prices, each empty on its own failure. */
export interface PromotionsBody {
  items: ActivePromotion[];
  planPrices: PlanPrice[];
}

/** GET a path and return its JSON body, or null on a non-200, a timeout, a network error or bad JSON. */
async function fetchJson(url: string, doFetch: typeof fetch, timeoutMs: number): Promise<unknown> {
  try {
    const res = await doFetch(url, {
      headers: { accept: 'application/json' },
      signal: AbortSignal.timeout(timeoutMs),
    });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

/**
 * Server side: fetches the live promotions and the plan prices in parallel for the same-origin proxy
 * (functions/api/promotions.ts and app/api/promotions/route.ts). Each side fails on its own: an unset
 * URL, a non-200 answer (404 when billing is off), a timeout or a network error gives that side an
 * empty list. Ended entries are dropped here too, since the response is cached for a minute.
 * Never throws.
 */
/** The merged body plus whether both upstream reads answered (a cache must not keep a partial one). */
export async function loadPromotionsResult(
  apiUrl: string | undefined,
  deps: PromotionsDeps = {},
): Promise<{ body: PromotionsBody; complete: boolean }> {
  const base = apiUrl?.trim().replace(/\/+$/, '');
  if (!base) return { body: { items: [], planPrices: [] }, complete: false };
  const doFetch = deps.fetch ?? fetch;
  const now = deps.now ?? (() => new Date());
  const timeoutMs = deps.timeoutMs ?? PROMOTIONS_TIMEOUT_MS;
  const [promotionsJson, planPricesJson] = await Promise.all([
    fetchJson(`${base}${PROMOTIONS_PATH}`, doFetch, timeoutMs),
    fetchJson(`${base}${PLAN_PRICES_PATH}`, doFetch, timeoutMs),
  ]);
  return {
    body: {
      items: parseActivePromotions(promotionsJson, now()),
      planPrices: parsePlanPrices(planPricesJson, now()),
    },
    complete: promotionsJson !== null && planPricesJson !== null,
  };
}

export async function loadPromotionsBody(
  apiUrl: string | undefined,
  deps: PromotionsDeps = {},
): Promise<PromotionsBody> {
  return (await loadPromotionsResult(apiUrl, deps)).body;
}

/**
 * The edge-cache key for GET /api/promotions: origin and path only, so a query string can neither
 * bypass the cache nor create extra entries (S-P1).
 */
export function promotionsCacheKey(requestUrl: string): string {
  const url = new URL(requestUrl);
  return `${url.origin}${url.pathname}`;
}
