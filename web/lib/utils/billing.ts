import type {
  BillingSummary,
  CreditEstimateBasis,
  WorkflowCostEstimate,
  WorkflowStepCostEstimate,
} from '../types/billing';
import type { UsageKind } from '../types/pricing';
import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/** Where a dated row (a price, a multiplier) stands at `now`. */
export type EffectiveStatus = 'Scheduled' | 'Open' | 'Ended';

/**
 * Which of a dated row's three states it is in at `now`: `[effectiveFrom, effectiveTo)`. `Ended`
 * covers both a superseded row and a retired one — the API does not distinguish them, and neither
 * does the history.
 */
export function effectiveStatus(
  row: { effectiveFrom: string; effectiveTo: string | null },
  now: Date,
): EffectiveStatus {
  const from = new Date(row.effectiveFrom).getTime();
  if (Number.isFinite(from) && from > now.getTime()) return 'Scheduled';
  const to = row.effectiveTo ? new Date(row.effectiveTo).getTime() : null;
  if (to !== null && Number.isFinite(to) && to <= now.getTime()) return 'Ended';
  return 'Open';
}

/**
 * Whether a price can be retired at `now`. The API refuses an end that is not after the row's
 * start, so a row that has not begun cannot be retired, and re-ending a row that already ended would
 * rewrite history instead of ending anything.
 */
export function canRetirePrice(
  row: { effectiveFrom: string; effectiveTo: string | null },
  now: Date,
): boolean {
  return effectiveStatus(row, now) === 'Open';
}

const usdFormatter = new Intl.NumberFormat('en-US', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

/** A whole-dollar figure for the revenue/cost tiles. */
export function formatUsd(value: number): string {
  return `$${usdFormatter.format(Number.isFinite(value) ? value : 0)}`;
}

/**
 * Minor units of a known exponent in the given currency (never assumes USD). A null currency is
 * printed as a plain number; an unknown code falls back to the bare code.
 */
export function formatMinorAmount(
  minor: number,
  exponent: number,
  currencyCode: string | null,
): string {
  const value = minor / 10 ** exponent;
  if (!currencyCode) {
    return new Intl.NumberFormat('en-US', {
      minimumFractionDigits: exponent,
      maximumFractionDigits: exponent,
    }).format(value);
  }
  try {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: currencyCode.toUpperCase(),
      minimumFractionDigits: exponent,
      maximumFractionDigits: exponent,
    }).format(value);
  } catch {
    return `${currencyCode} ${value.toFixed(exponent)}`;
  }
}

/**
 * An amount in whole minor units (Paddle's format: `"500"` with `USD` is $5.00), or null when the
 * amount or currency is not one this can print. USD goes through `formatUsd`; another currency uses
 * its own number of decimals (`JPY` has none).
 */
export function formatMinorUnits(amount: string, currencyCode: string | null): string | null {
  if (!/^\d+$/.test(amount.trim())) return null;
  const minor = Number(amount.trim());
  const currency = (currencyCode ?? 'USD').trim().toUpperCase();
  if (currency === 'USD') return formatUsd(minor / 100);
  try {
    const formatter = new Intl.NumberFormat('en-US', { style: 'currency', currency });
    const digits = formatter.resolvedOptions().maximumFractionDigits ?? 2;
    return formatter.format(minor / 10 ** digits);
  } catch {
    return null;
  }
}

/**
 * A server preview amount for display (DEC-9): when the raw `minor` units and a currency are
 * present, format them with `Intl.NumberFormat` (a pure unit shift, `minor / 10**exponent`); else
 * (or for a currency Intl does not know) return the server's own string verbatim.
 */
export function formatPreviewMoney(
  serverText: string,
  minor: number | null | undefined,
  currencyCode: string | null | undefined,
  exponent: number | null | undefined,
  locale?: string,
): string {
  if (typeof minor !== 'number' || !Number.isFinite(minor)) return serverText;
  if (typeof exponent !== 'number' || !Number.isInteger(exponent) || exponent < 0 || exponent > 6) return serverText;
  const currency = currencyCode?.trim().toUpperCase();
  if (!currency || !/^[A-Z]{3}$/.test(currency)) return serverText;
  try {
    return new Intl.NumberFormat(locale, {
      style: 'currency',
      currency,
      minimumFractionDigits: exponent,
      maximumFractionDigits: exponent,
    }).format(minor / 10 ** exponent);
  } catch {
    return serverText;
  }
}

/** The subtotal and total of a checkout preview, formatted per DEC-9 (raw minor units when sent). */
export function previewAmounts(preview: {
  currencyCode: string;
  subtotal: string;
  total: string;
  subtotalMinor?: number | null;
  totalMinor?: number | null;
  minorUnitExponent?: number | null;
}): { subtotal: string; total: string } {
  return {
    subtotal: formatPreviewMoney(preview.subtotal, preview.subtotalMinor, preview.currencyCode, preview.minorUnitExponent),
    total: formatPreviewMoney(preview.total, preview.totalMinor, preview.currencyCode, preview.minorUnitExponent),
  };
}

const unitFormatter = new Intl.NumberFormat('en-US', {
  minimumFractionDigits: 0,
  maximumFractionDigits: 8,
});

/**
 * A per-unit price, which for tokens is routinely below a millionth of a dollar — so this keeps up
 * to 8 decimals instead of rounding a real price to "$0.00".
 */
export function formatUsdPerUnit(value: number): string {
  return `$${unitFormatter.format(Number.isFinite(value) ? value : 0)}`;
}

/** Millicredits to credits (1,000 millicredits = 1 credit, decision D8). */
export function formatCreditsFromMillis(millis: number): string {
  const credits = (Number.isFinite(millis) ? millis : 0) / 1000;
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: 3 }).format(credits);
}

/** Millicredits without the conversion to credits. */
export function formatMillicredits(millis: number): string {
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: 6 }).format(
    Number.isFinite(millis) ? millis : 0,
  );
}

/** A rate multiplier: `1`, `0.25`, `0`. */
export function formatMultiplier(value: number): string {
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 }).format(
    Number.isFinite(value) ? value : 0,
  );
}

/** Percent with a fixed number of decimals; the API hands back a number, never a string. */
export function formatPercent(value: number, fractionDigits = 1): string {
  return `${(Number.isFinite(value) ? value : 0).toFixed(fractionDigits)}%`;
}

// ---------------------------------------------------------------- per-unit prices

const TOKEN_UNITS: ReadonlySet<UsageKind> = new Set<UsageKind>([
  'LlmInputTokens',
  'LlmOutputTokens',
  'LlmCachedInputTokens',
  'EmbeddingTokens',
]);

/** True for the per-token units, whose prices are shown per million tokens. */
export function isTokenUnit(unit: UsageKind): boolean {
  return TOKEN_UNITS.has(unit);
}

/** How many stored units one displayed unit is: a million for tokens, one for everything else. */
export function unitScale(unit: UsageKind): number {
  return isTokenUnit(unit) ? 1_000_000 : 1;
}

const UNIT_NOUN: Record<UsageKind, string> = {
  LlmInputTokens: '1M tokens',
  LlmOutputTokens: '1M tokens',
  LlmCachedInputTokens: '1M tokens',
  EmbeddingTokens: '1M tokens',
  AsrAudioSeconds: 'second',
  TtsInputBytes: 'byte',
  VideoGenSeconds: 'second',
  RenderOutputSeconds: 'second',
  EncodeOutputSeconds: 'second',
  AnalyzeMediaSeconds: 'second',
  StorageGbDay: 'GB-day',
  ImageGenImages: 'image',
  ImageEditImages: 'image',
  SegmentationImages: 'image',
  DepthImages: 'image',
  MusicGenMinutes: 'minute',
  SoundEffectGenSeconds: 'second',
  VideoSegmentationFrames: 'frame',
  ModerationCalls: 'call',
  RerankSearches: 'search',
  WebSearches: 'search',
  AudioCleanupMinutes: 'minute',
  VideoEnhanceSeconds: 'second',
  VideoEnhanceComputeSeconds: 'compute second',
};

/** What a displayed price is per: "1M tokens", "second", "image". */
export function displayUnitNoun(unit: UsageKind): string {
  return UNIT_NOUN[unit] ?? unit;
}

/** Drops binary floating-point noise (2.5e-6 × 1e6 = 2.4999999999999996) without losing real digits. */
function clean(value: number): number {
  return Number.isFinite(value) ? Number(value.toPrecision(12)) : 0;
}

/** A stored per-unit figure as the figure a person reads (per million tokens for token units). */
export function toDisplayUnit(perUnit: number, unit: UsageKind): number {
  return clean(perUnit * unitScale(unit));
}

/** A figure a person typed (per million tokens for token units) as the per-unit figure the API stores. */
export function fromDisplayUnit(perDisplayUnit: number, unit: UsageKind): number {
  return clean(perDisplayUnit / unitScale(unit));
}

/** "$2.5 / 1M tokens", "$0.05 / second". */
export function formatPriceCost(costUsdPerUnit: number, unit: UsageKind): string {
  return `${formatUsdPerUnit(toDisplayUnit(costUsdPerUnit, unit))} / ${displayUnitNoun(unit)}`;
}

/**
 * Credits for one displayed unit, from millicredits per stored unit. Small figures keep four
 * significant digits so a real charge never rounds to "0"; larger ones keep three decimals.
 */
export function formatPriceCredits(millisPerUnit: number, unit: UsageKind): string {
  const credits = toDisplayUnit((Number.isFinite(millisPerUnit) ? millisPerUnit : 0) / 1000, unit);
  const formatted =
    credits !== 0 && Math.abs(credits) < 1
      ? new Intl.NumberFormat('en-US', { maximumSignificantDigits: 4 }).format(credits)
      : new Intl.NumberFormat('en-US', { maximumFractionDigits: 3 }).format(credits);
  return `${formatted} credits / ${displayUnitNoun(unit)}`;
}

/**
 * A `<input type="datetime-local">` value for `date`, in the browser's own timezone.
 *
 * Built from the local getters rather than `toISOString().slice(0, 16)`, which would show the UTC
 * wall clock in a field the browser then re-interprets as local time — an operator in UTC+2 would
 * see the card start two hours earlier than it does.
 */
export function toDateTimeLocalValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/**
 * The ISO instant a `datetime-local` field holds, or null when it is empty or unparseable — the
 * caller sends null and the server defaults to "now", rather than posting an invalid date.
 */
export function parseDateTimeLocalValue(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = new Date(trimmed);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

// ---------------------------------------------------------------- pre-run estimate and balance

/**
 * How low "low" is: a workspace is warned once its available credits fall below this share of what
 * its plan grants for a period (B10c-UI). A share rather than a flat number because the plans differ
 * by more than an order of magnitude, and 15% is early enough to top up before a run is refused
 * rather than after.
 */
export const LOW_BALANCE_FRACTION = 0.15;

const creditFormatters = new Map<string, Intl.NumberFormat>();
function creditFormatter(locale: string): Intl.NumberFormat {
  let f = creditFormatters.get(locale);
  if (!f) creditFormatters.set(locale, (f = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 })));
  return f;
}

/** How a credit figure is shown. */
export interface CreditAmountOptions {
  /**
   * Round UP to whole credits — for a "needs N available to start" figure, which must never read
   * lower than what the run actually holds (12.4 shows as 13, never 12).
   */
  roundUp?: boolean;
  /** BCP 47 locale (`useFormat().locale`); defaults to `en-US`. */
  locale?: string;
}

/** A credit figure as it appears in prose: "12.5", "1,000". */
export function formatCreditAmount(credits: number, options: CreditAmountOptions = {}): string {
  const value = Number.isFinite(credits) ? credits : 0;
  const fmt = creditFormatter(options.locale === undefined || options.locale === 'en' || options.locale === 'en-XA' ? 'en-US' : options.locale);
  if (!options.roundUp) return fmt.format(value);
  // Snap to whole millicredits first, so float noise (12.000000001) does not add a credit.
  return fmt.format(Math.ceil(Math.round(value * 1000) / 1000));
}

/** "Needs 13 credits available to start; it can cost more." — the always-visible hold line. */
export function creditsNeededLine(requiredCredits: number, t: T = enT, locale?: string): string | null {
  if (!Number.isFinite(requiredCredits) || requiredCredits <= 0) return null;
  const needed = formatCreditAmount(requiredCredits, { roundUp: true, locale });
  return t('libLabels.billing.needsLine', { amount: needed, count: needed === '1' ? 1 : 2 });
}

/**
 * "≈ 12.5–20 credits" for a range, collapsing to "≈ 20 credits" when both ends agree. The estimate
 * is always an approximation — hence the ≈ and the word "about" everywhere it appears — because it is
 * built from a p50/p90 of past runs, or from defaults when there are none.
 */
export function creditRange(low: number, high: number, t: T = enT, locale?: string): string {
  const lo = Number.isFinite(low) ? low : 0;
  const hi = Number.isFinite(high) ? high : 0;
  if (Math.round(lo * 100) === Math.round(hi * 100)) return t('libLabels.billing.approx', { amount: formatCreditAmount(hi, { locale }) });
  return t('libLabels.billing.approxRange', { low: formatCreditAmount(lo, { locale }), high: formatCreditAmount(hi, { locale }) });
}

/**
 * Where a step's figure came from, in the words a user reads it in. A DEFAULT says so: the whole
 * reason B7 publishes `Basis` per step is so a cold-start estimate is never dressed up as the
 * customer's own measurement. An unknown member is passed through unchanged — the enum is
 * append-only, so a value this build has not heard of is a newer backend, not a bug.
 */
export function estimateBasisLabel(basis: CreditEstimateBasis, t: T = enT): string {
  const key = `libLabels.billing.basis.${basis}`;
  return t.has(key) ? t(key) : basis;
}

/** A finite number, or 0 for whatever else the API might send. */
function finiteOrZero(value: number | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

/**
 * The run's headline figure: "≈ 40 credits (usually 30–52)" — the typical cost, then the usual range
 * around it. Collapses to "≈ 40 credits" when the two ends of the range agree to the cent.
 */
export function creditEstimateHeadline(typical: number, low: number, high: number, t: T = enT, locale?: string): string {
  const mid = finiteOrZero(typical);
  const lo = finiteOrZero(low);
  const hi = finiteOrZero(high);
  const head = t('libLabels.billing.approx', { amount: formatCreditAmount(mid, { locale }) });
  if (Math.round(lo * 100) === Math.round(hi * 100)) return head;
  return t('libLabels.billing.headlineRange', { head, low: formatCreditAmount(lo, { locale }), high: formatCreditAmount(hi, { locale }) });
}

/** The reason code that says a fixed-price step sits inside a review loop and can be ordered again. */
const VIDEO_GENERATION_IN_REVIEW_LOOP = 'video_generation_in_review_loop';
/** The reason code that says a review loop may re-make earlier steps, whose worst case is part of the ceiling. */
const REVIEW_STEP_RERUNS = 'review_step_reruns';

/**
 * The fixed-price part of a run (generated video, priced per clip), which is kept out of the usual
 * range and stated as a ceiling instead. Null when there is none.
 */
export function fixedPriceLine(
  fixedMaxCredits: number | undefined,
  reasons: readonly string[] | undefined,
  t: T = enT,
  locale?: string,
): string | null {
  const amount = finiteOrZero(fixedMaxCredits);
  if (amount <= 0) return null;
  if (reasons?.includes(REVIEW_STEP_RERUNS)) return t('libLabels.billing.fixedLineReviewReruns', { amount: formatCreditAmount(amount, { locale }) });
  const line = t('libLabels.billing.fixedLine', { amount: formatCreditAmount(amount, { locale }) });
  return reasons?.includes(VIDEO_GENERATION_IN_REVIEW_LOOP) ? t('libLabels.billing.fixedLineLoop', { line }) : line;
}

/** The spread reason codes this build has a sentence for (catalogue: `libLabels.billing.spread.<code>`). */
const SPREAD_REASONS: ReadonlySet<string> = new Set(['review_loop_repeats', 'footage_length', 'foreach_items_unknown', 'own_provider_free']);

/**
 * Why the usual range is as wide as it is, one line per known reason, in the server's order.
 * Unknown codes are dropped (a newer backend), and the `video_generation_*` codes are said by
 * {@link fixedPriceLine} instead.
 */
export function spreadReasonLines(reasons: readonly string[] | undefined, t: T = enT): string[] {
  if (!reasons) return [];
  const lines: string[] = [];
  for (const code of reasons) {
    if (!SPREAD_REASONS.has(code)) continue;
    const line = t(`libLabels.billing.spread.${code}`);
    if (!lines.includes(line)) lines.push(line);
  }
  return lines;
}

/**
 * One sentence saying what the estimate was built from. A newer backend says so in `rangeBasis`
 * (and how many past runs, for `PastRuns`); an older one only answers `hasHistory` — the server's
 * own answer about the organization's ledger, not a guess from the numbers.
 */
export function estimateBasisSentence(
  estimate: Pick<WorkflowCostEstimate, 'hasHistory' | 'rangeBasis' | 'pastRunCount'>,
  t: T = enT,
): string {
  switch (estimate.rangeBasis) {
    case 'PastRuns': {
      const n = finiteOrZero(estimate.pastRunCount);
      if (n > 0) return t('libLabels.billing.basisPastRuns', { count: n });
      break;
    }
    case 'StepHistory':
      return t('libLabels.billing.basisStepHistory');
    case 'FirstRun':
      return t('libLabels.billing.basisFirstRun');
    default:
      break;
  }
  return t(estimate.hasHistory ? 'libLabels.billing.basisHistory' : 'libLabels.billing.basisNoHistory');
}

/**
 * The credits the workspace's current plan grants for one period — what "the plan allowance" in a
 * low-balance warning means.
 *
 * Null when the summary does not say, which is the honest answer rather than a zero: the endpoint
 * lists only the plans it can SELL (Subscription and PerSeat), so a workspace on Trial or Enterprise
 * has no allowance on the wire at all, and there is no number here to divide by. A per-seat plan
 * grants its credits per seat — the same multiplication the subscription's own monthly grant uses.
 */
export function planAllowanceCredits(
  summary: Pick<BillingSummary, 'plans' | 'subscription'>,
): number | null {
  const current = summary.plans.find((p) => p.current);
  if (!current || !(current.includedCredits > 0)) return null;
  const seats = current.kind === 'PerSeat' ? Math.max(1, summary.subscription?.seats ?? 1) : 1;
  return current.includedCredits * seats;
}

/** How the balance stands against the plan allowance. */
export interface LowBalance {
  /** The organization's available credits (balance minus open holds). */
  available: number;
  /** What the current plan grants for a period. */
  allowance: number;
  /** `LOW_BALANCE_FRACTION` of the allowance. */
  threshold: number;
  /** True when the balance is below the threshold — at or above it is not a warning. */
  low: boolean;
}

/**
 * Whether the workspace is running low, or null when the allowance is unknown (see
 * {@link planAllowanceCredits}) — in which case the UI shows no warning rather than one it cannot
 * justify. The comparison is on AVAILABLE credits, the same figure the execute preflight checks, not
 * on the ledger balance that still includes credits already held for runs in flight.
 */
export function lowBalance(summary: BillingSummary | undefined): LowBalance | null {
  if (!summary) return null;
  const allowance = planAllowanceCredits(summary);
  if (allowance === null) return null;
  const available = summary.credits.available;
  if (!Number.isFinite(available)) return null;
  const threshold = allowance * LOW_BALANCE_FRACTION;
  return { available, allowance, threshold, low: available < threshold };
}

/**
 * One step as a line in the breakdown: its label (or its type when it has none), the range, and
 * where the range came from.
 */
export function stepEstimateLine(step: WorkflowStepCostEstimate, t: T = enT, locale?: string): string {
  const name = step.label?.trim() ? step.label.trim() : step.stepType;
  // A step type nothing bills has no range to report — "≈ 0 credits" would read as a measurement.
  if (step.basis === 'NotCharged') return t('libLabels.billing.stepNotCharged', { name });
  // A fixed-price step is a ceiling, not a range: it is what the most clips it can order would cost.
  const fixedMax = finiteOrZero(step.fixedMaxCredits);
  if (fixedMax > 0) return t('libLabels.billing.stepUpTo', { name, amount: formatCreditAmount(fixedMax, { locale }) });
  const again = finiteOrZero(step.expectedRuns) > 1.05 ? t('libLabels.billing.stepAgain') : '';
  return t('libLabels.billing.stepRange', { name, range: creditRange(step.lowCredits, step.highCredits, t, locale), again, basis: estimateBasisLabel(step.basis, t) });
}

/**
 * Whether the run may stop partway because the balance runs out (the cold-start hold re-plan). The balance is
 * enough to START the run (`affordable`), but below what such a run typically costs: the typical
 * variable figure plus the fixed-price part, which is held in full when its step starts. Never true
 * when billing is off, the balance is unknown, the run cannot start at all (the shortfall message
 * covers that), or an older backend sent no typical figure to compare against.
 */
export function mayRunOutPartway(
  estimate: Pick<
    WorkflowCostEstimate,
    'billingEnabled' | 'affordable' | 'balanceCredits' | 'typicalCredits' | 'fixedMaxCredits'
  >,
): boolean {
  if (!estimate.billingEnabled || !estimate.affordable) return false;
  const balance = estimate.balanceCredits;
  if (balance === null || !Number.isFinite(balance)) return false;
  if (typeof estimate.typicalCredits !== 'number' || !Number.isFinite(estimate.typicalCredits)) return false;
  return balance < estimate.typicalCredits + finiteOrZero(estimate.fixedMaxCredits);
}

/**
 * What one step was charged, as prose: "12.5 credits", "1 credit", "No charge". Null when the server recorded no
 * charge for the step (`creditsCharged` null), so a run from before per-step charges existed shows nothing rather
 * than a misleading zero.
 */
export function stepChargeLabel(creditsCharged: number | null | undefined, t: T = enT, locale?: string): string | null {
  if (creditsCharged == null || !Number.isFinite(creditsCharged)) return null;
  if (creditsCharged <= 0) return t('libLabels.billing.chargeNone');
  // A real charge below the 2-decimal display must not read as "0 credits".
  if (creditsCharged < 0.01) return t('libLabels.billing.chargeTiny', { amount: formatCreditAmount(0.01, { locale }) });
  const amount = formatCreditAmount(creditsCharged, { locale });
  return t('libLabels.billing.creditCount', { amount, count: amount === '1' ? 1 : 2 });
}
