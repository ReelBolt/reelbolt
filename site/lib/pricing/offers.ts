// Pure derivations from the vendored plan catalog (see `./index`). The pricing page, the
// schema.org Offer markup and the tests all read this one implementation, so a price can never be
// written twice — and nothing here is a price literal, every number comes in as an argument from
// the catalog. Only `import type` is allowed in this file: it must stay loadable by `node --test`
// (which has no JSON import attributes and no `@/` path alias), so the type-only import below is
// erased at compile time and never resolved at runtime.
import type { Plan, PlanEntitlements, RawPlan, TopUpPack } from './index';

/** `1,000`. Hand-rolled rather than `toLocaleString`, whose output depends on the runtime's ICU. */
export function formatNumber(amount: number): string {
  return amount.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

const CURRENCY_SYMBOL: Record<string, string> = { USD: '$' };

/** `$29` — the one place an amount becomes display text. Falls back to `USD 29` for a currency
 *  the site has no symbol for, rather than printing a wrong one. */
export function formatMoney(currency: string, amount: number): string {
  const symbol = CURRENCY_SYMBOL[currency];
  return symbol ? `${symbol}${formatNumber(amount)}` : `${currency} ${formatNumber(amount)}`;
}

/** What a plan's price is charged for. `billingDuration` is absent where nothing recurs. */
export function priceUnit(plan: Plan): { unitText: string; billingDuration?: string } {
  if (plan.kind === 'perSeat') return { unitText: 'seat per month', billingDuration: 'P1M' };
  if (plan.kind === 'subscription') return { unitText: 'month', billingDuration: 'P1M' };
  // The trial is 100 one-time credits, not a $0 monthly subscription, so it carries no billing
  // duration — claiming one would describe a recurring plan the catalog does not sell.
  return { unitText: 'one-time' };
}

/** The price of one month on the yearly plan: the yearly price over 12, rounded to the cent.
 *  Undefined where the plan has no yearly price (trial, enterprise). */
export function annualMonthlyEquivalent(plan: Plan): number | undefined {
  if (plan.priceAnnual === undefined) return undefined;
  return Math.round((plan.priceAnnual / 12) * 100) / 100;
}

/** Whole months the yearly price saves against paying monthly for a year: derived, never typed. */
export function annualMonthsFree(plan: Plan): number | undefined {
  if (plan.priceAnnual === undefined || plan.priceMonthly === undefined || plan.priceMonthly <= 0) return undefined;
  const months = Math.floor((plan.priceMonthly * 12 - plan.priceAnnual) / plan.priceMonthly);
  return months > 0 ? months : undefined;
}

/** The credits line under a plan's price. Reads `kind`, so Trial never claims a monthly grant. */
export function creditsLabel(plan: Plan): string {
  if (plan.kind === 'trial') {
    return `${formatNumber(plan.creditsOneTime ?? 0)} credits, one time`;
  }
  if (plan.kind === 'perSeat') {
    return `${formatNumber(plan.includedCredits)} credits per seat every month, pooled`;
  }
  if (plan.kind === 'subscription') {
    return `${formatNumber(plan.includedCredits)} credits every month`;
  }
  return 'Volume and terms on arrangement';
}

/**
 * The catalog's own bullets for a card, minus any that merely restate the credits line printed
 * directly under the price — the three paid plans' first highlight is byte-identical to
 * `creditsLabel`, and printing the same sentence twice in a row reads as a bug. Exact match only:
 * a bullet that says something more (the trial's "100 credits to try ReelBolt") is kept.
 */
export function cardHighlights(plan: Plan): string[] {
  const credits = creditsLabel(plan);
  return plan.highlights.filter((highlight) => highlight !== credits);
}

export interface OfferNode {
  '@type': 'Offer';
  name: string;
  price: number;
  priceCurrency: string;
  url: string;
  availability: string;
  priceSpecification: {
    '@type': 'UnitPriceSpecification';
    price: number;
    priceCurrency: string;
    unitText: string;
    billingDuration?: string;
    referenceQuantity?: { '@type': 'QuantitativeValue'; value: number; unitCode: string };
  };
}

const AVAILABILITY_IN_STOCK = 'https://schema.org/InStock';

/**
 * One Offer for every plan that publishes a price. The trial is included at 0 (signing up really
 * does cost nothing); a `contact` plan has no price and is therefore omitted entirely — inventing
 * a figure for it, or quoting a range, would be fabricated markup (decision D7 keeps Enterprise
 * "Contact us" only).
 */
export function pricedPlanOffer(plan: Plan, currency: string, siteUrl: string): OfferNode | undefined {
  const price = plan.kind === 'trial' ? 0 : plan.priceMonthly;
  if (price === undefined) return undefined;

  const { unitText, billingDuration } = priceUnit(plan);
  return {
    '@type': 'Offer',
    name: plan.name,
    price,
    priceCurrency: currency,
    // The anchor the card itself carries, so the markup points at the price a reader can see.
    url: `${siteUrl}/pricing#${plan.id}`,
    availability: AVAILABILITY_IN_STOCK,
    priceSpecification: {
      '@type': 'UnitPriceSpecification',
      price,
      priceCurrency: currency,
      unitText,
      ...(billingDuration ? { billingDuration } : {}),
    },
  };
}

/**
 * The yearly Offer for a plan that publishes a yearly price: the same card anchor, billed once a
 * year (`P1Y`, reference quantity of one year, unit code ANN). Plans without a yearly price (trial,
 * Enterprise) have none, and the monthly Offer is untouched.
 */
export function annualPlanOffer(plan: Plan, currency: string, siteUrl: string): OfferNode | undefined {
  const price = plan.priceAnnual;
  if (price === undefined) return undefined;
  const per = plan.kind === 'perSeat' ? 'seat per year' : 'year';
  return {
    '@type': 'Offer',
    name: `${plan.name} (annual)`,
    price,
    priceCurrency: currency,
    url: `${siteUrl}/pricing#${plan.id}`,
    availability: AVAILABILITY_IN_STOCK,
    priceSpecification: {
      '@type': 'UnitPriceSpecification',
      price,
      priceCurrency: currency,
      unitText: per,
      billingDuration: 'P1Y',
      referenceQuantity: { '@type': 'QuantitativeValue', value: 1, unitCode: 'ANN' },
    },
  };
}

export function annualPlanOffers(plans: readonly Plan[], currency: string, siteUrl: string): OfferNode[] {
  return plans
    .map((plan) => annualPlanOffer(plan, currency, siteUrl))
    .filter((offer): offer is OfferNode => offer !== undefined);
}

export function pricedPlanOffers(
  plans: readonly Plan[],
  currency: string,
  siteUrl: string,
): OfferNode[] {
  return plans
    .map((plan) => pricedPlanOffer(plan, currency, siteUrl))
    .filter((offer): offer is OfferNode => offer !== undefined);
}

export interface AggregateOfferNode {
  '@type': 'AggregateOffer';
  lowPrice: number;
  highPrice: number;
  priceCurrency: string;
  offerCount: number;
}

/**
 * The range summary for the homepage. The endpoints are the cheapest and dearest *recurring*
 * prices only — the free trial is a one-time grant, not a subscription, and letting its 0 set
 * `lowPrice` would advertise the range as starting at nothing.
 */
export function aggregateOfferNode(
  plans: readonly Plan[],
  offers: readonly OfferNode[],
  currency: string,
): AggregateOfferNode | undefined {
  const recurring = plans
    .map((plan) => plan.priceMonthly)
    .filter((price): price is number => price !== undefined);

  if (recurring.length === 0 || offers.length === 0) return undefined;

  return {
    '@type': 'AggregateOffer',
    lowPrice: Math.min(...recurring),
    highPrice: Math.max(...recurring),
    priceCurrency: currency,
    offerCount: offers.length,
  };
}

/**
 * Where a plan's button points. `/app/*` is the dashboard's own Next.js app (reached through
 * nginx), never a route in this one. A `contact` plan goes to the enterprise topic; until
 * self-serve signup is live (decision D25) every other plan falls back to the contact path rather
 * than sending a visitor into a signup route that does not answer yet.
 */
export function planCtaHref(plan: Plan, signupEnabled: boolean): string {
  if (plan.cta === 'contact') return '/contact?topic=enterprise';
  return signupEnabled ? `/app/signup?plan=${plan.id}` : '/contact';
}

/**
 * The button's words, matching `planCtaHref`. With signup live, the trial starts free, a paid plan
 * is chosen by name, and Enterprise is a conversation. With signup off every button is the contact
 * path, so every button says so.
 */
export function planCtaLabel(plan: Plan, signupEnabled: boolean): string {
  if (!signupEnabled || plan.cta === 'contact') return 'Talk to us';
  if (plan.kind === 'trial') return 'Start free';
  return `Choose ${plan.name}`;
}

/** Fills in the entitlement keys a plan omits: an absent figure becomes `null`, an absent flag `false`. */
export function normalizePlan(raw: RawPlan): Plan {
  const e: Partial<PlanEntitlements> = raw.entitlements ?? {};
  return {
    ...raw,
    topUpAllowed: raw.topUpAllowed === true,
    entitlements: {
      maxOutputHeight: e.maxOutputHeight ?? null,
      assistantMonthlyCredits: e.assistantMonthlyCredits ?? null,
      assistantMonthlyCreditsPerSeat: e.assistantMonthlyCreditsPerSeat ?? null,
      assistantUnlimited: e.assistantUnlimited === true,
      storageGb: e.storageGb ?? null,
      roomsAllowed: e.roomsAllowed === true,
      videoGenAllowed: e.videoGenAllowed === true,
      runnerAllowed: e.runnerAllowed === true,
      byoAllowed: e.byoAllowed === true,
      segmentationAllowed: e.segmentationAllowed === true,
      audioGenAllowed: e.audioGenAllowed === true,
      audioCleanupAllowed: e.audioCleanupAllowed === true,
      maxConcurrentExecutions: e.maxConcurrentExecutions ?? null,
    },
  };
}

/** The boolean entitlements the copy names by plan ("On Pro, Team and Enterprise"). */
export type BooleanEntitlement = 'byoAllowed' | 'roomsAllowed' | 'videoGenAllowed' | 'runnerAllowed';

/** `Pro, Team and Enterprise`, `Pro and Team`, `Pro`. Empty for none, so callers can drop the line. */
export function joinNames(names: readonly string[]): string {
  if (names.length <= 1) return names[0] ?? '';
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

/** The names of the plans whose boolean entitlement is set, in catalog order. */
export function plansWith(plans: readonly Plan[], key: BooleanEntitlement): string {
  return joinNames(plans.filter((plan) => plan.entitlements[key]).map((plan) => plan.name));
}

/** The names of the plans whose maximum output height is exactly `height`. */
export function plansWithMaxOutputHeight(plans: readonly Plan[], height: number): string {
  return joinNames(
    plans.filter((plan) => plan.entitlements.maxOutputHeight === height).map((plan) => plan.name),
  );
}

/** The label a buyer reads for a frame height: `720p`, `1080p`, `4K` or `8K`. */
export function resolutionLabel(height: number): string {
  if (height === 2160) return '4K';
  if (height === 4320) return '8K';
  return `${height}p`;
}

/** The plan with the largest maximum output height. The first in catalog order wins a tie. */
export function highestResolutionPlan(plans: readonly Plan[]): Plan | undefined {
  let best: Plan | undefined;
  for (const plan of plans) {
    const height = plan.entitlements.maxOutputHeight;
    if (height === null) continue;
    if (best === undefined || height > (best.entitlements.maxOutputHeight ?? 0)) best = plan;
  }
  return best;
}

/** The top export the catalog offers and the plans that reach it: `4K` on `Pro and Team`. */
export function topExport(plans: readonly Plan[]): { label: string; plans: string } | undefined {
  const top = highestResolutionPlan(plans);
  if (!top) return undefined;
  return {
    label: top.limits.maxResolution,
    plans: plansWithMaxOutputHeight(plans, top.entitlements.maxOutputHeight ?? 0),
  };
}

/** The plan with this id. Throws rather than returning a placeholder, so a renamed id fails the build. */
export function planById(plans: readonly Plan[], id: string): Plan {
  const found = plans.find((plan) => plan.id === id);
  if (!found) throw new Error(`plans.catalog.json has no plan with id "${id}"`);
  return found;
}

/** The export ceiling a buyer reads: from `maxOutputHeight`, else the catalog's own label. */
export function planMaxResolution(plan: Plan): string {
  const height = plan.entitlements.maxOutputHeight;
  return height === null ? plan.limits.maxResolution : resolutionLabel(height);
}

/** The assistant allowance as a table cell: a number, `N per seat`, `Unlimited`, or `Not included`. */
export function assistantAllowance(plan: Plan): string {
  const e = plan.entitlements;
  if (e.assistantUnlimited) return 'Unlimited';
  if (e.assistantMonthlyCredits !== null) return formatNumber(e.assistantMonthlyCredits);
  if (e.assistantMonthlyCreditsPerSeat !== null) {
    return `${formatNumber(e.assistantMonthlyCreditsPerSeat)} per seat`;
  }
  return 'Not included';
}

/** The storage cell: `1 GB`, `100 GB shared`. Undefined where the catalog sets no figure. */
export function storageLabel(plan: Plan): string | undefined {
  const gb = plan.entitlements.storageGb;
  if (gb === null) return undefined;
  return plan.kind === 'perSeat' ? `${gb} GB shared` : `${gb} GB`;
}

/**
 * The benefit lines the pricing page adds under a card, one per fact the entitlements state. Each
 * line appears only where its condition holds, and every number comes from the catalog.
 */
export function benefitLines(plan: Plan): string[] {
  const e = plan.entitlements;
  const lines: string[] = [];
  const exportLimit = planMaxResolution(plan);
  // The watermark is part of the export, so it stays on the line that replaces "720p exports with a watermark".
  const watermark = plan.limits.watermark ? ' with a watermark' : '';
  lines.push(e.maxOutputHeight === 2160 ? `Export in ${exportLimit}` : `Export up to ${exportLimit}${watermark}`);
  if (e.assistantMonthlyCredits !== null) {
    lines.push(
      `${formatNumber(e.assistantMonthlyCredits)} assistant credits a month included, in the dashboard and in Claude Code`,
    );
  }
  if (e.assistantMonthlyCreditsPerSeat !== null) {
    lines.push(
      `${formatNumber(e.assistantMonthlyCreditsPerSeat)} assistant credits per seat a month included, in the dashboard and in Claude Code`,
    );
  }
  if (e.assistantUnlimited) lines.push('Unlimited assistant use');
  if (e.storageGb !== null) {
    lines.push(
      plan.kind === 'perSeat'
        ? `${e.storageGb} GB of shared storage for your footage and exports`
        : `${e.storageGb} GB of storage for your footage and exports`,
    );
  }
  if (plan.kind === 'subscription' || plan.kind === 'perSeat') {
    lines.push('Cancel anytime and manage your plan yourself');
  }
  if (plan.topUpAllowed) lines.push('Top up with credit packs when you need more');
  return lines;
}

/**
 * The capabilities a plan switches on, in beginner words, one line each. A line appears only where
 * the entitlement is on; every figure comes from the catalog. Cards only: Offers never read these.
 */
export function featureLines(plan: Plan): string[] {
  const e = plan.entitlements;
  const lines: string[] = [];
  if (!plan.limits.watermark) lines.push('No watermark on your exports');
  if (e.segmentationAllowed) lines.push('Cut people and objects out of your video');
  if (e.audioGenAllowed) lines.push('Create music and sound effects');
  if (e.audioCleanupAllowed) lines.push('Clean up noisy audio');
  if (e.byoAllowed) lines.push('Use your own AI provider');
  if (e.maxConcurrentExecutions !== null) {
    lines.push(
      e.maxConcurrentExecutions === 1
        ? 'One video job at a time'
        : `Up to ${e.maxConcurrentExecutions} video jobs at the same time`,
    );
  }
  if (e.storageGb === null && plan.kind === 'contact') lines.push('Unlimited storage');
  return lines;
}

/**
 * The numbers and resolution labels a bullet states: `300`, `1000` (from `1,000`), `4K`, `1080p`.
 * Two bullets that share one of these say the same fact twice.
 */
function factTokens(line: string): string[] {
  const numbers = [...line.matchAll(/(?<![\w.])\d[\d,]*(?!\w)/g)].map(
    ([token]) => `number ${token.replace(/,/g, '')}`,
  );
  const resolutions = [...line.matchAll(/\b(?:\d{3,4}p|\d+K)\b/g)].map(([label]) => `resolution ${label}`);
  return [...numbers, ...resolutions];
}

const TOP_UP = /top.?up/i;
const BYO_HIGHLIGHT = /^bring your own/i;
const RESOLUTION_EXPORT = /^(?:\d{3,4}p|\d+K) exports\b/;

/**
 * The benefit lines every paid card states word for word (the cancel line and the top-up line).
 * The pricing page prints these once under the grid, and each card leaves them out.
 */
export function sharedPaidLines(plans: readonly Plan[]): string[] {
  const paid = plans.filter((plan) => plan.kind === 'subscription' || plan.kind === 'perSeat');
  if (paid.length === 0) return [];
  const [first, ...rest] = paid.map(benefitLines);
  return first.filter((line) => rest.every((lines) => lines.includes(line)));
}

/**
 * A card's bullets. A catalog highlight is dropped when a benefit line states the same number or
 * resolution label, because the benefit line is the one that carries the page's wording. A
 * top-up highlight is dropped when a benefit line already talks about top-ups. On a plan that says
 * "Everything in Pro", the Pro priority highlight is dropped too. The export line always stays, so
 * every card states its own export ceiling (spec: the Team card lists 4K). Benefit lines in `shared` are left for the grid footnote.
 * The remaining catalog bullets come first, then each benefit line the card does not already print.
 */
export function planCardLines(plan: Plan, shared: readonly string[] = []): string[] {
  const e = plan.entitlements;
  const features = featureLines(plan);
  const benefits = benefitLines(plan);
  const highlights = cardHighlights(plan);
  const covered = new Set(benefits.flatMap(factTokens));
  const benefitsTalkTopUp = benefits.some((line) => TOP_UP.test(line));
  const inheritsPro = highlights.includes('Everything in Pro');
  const lines = highlights.filter(
    (highlight) =>
      !factTokens(highlight).some((token) => covered.has(token)) &&
      !(TOP_UP.test(highlight) && benefitsTalkTopUp) &&
      !(inheritsPro && (highlight === 'Priority rendering' || RESOLUTION_EXPORT.test(highlight))) &&
      !(features.length > 0 && BYO_HIGHLIGHT.test(highlight) && e.byoAllowed),
  );
  // "Everything in Pro" already covers the capabilities; only the figure that differs (job count) is stated.
  const ownFeatures = inheritsPro ? features.filter((line) => /video jobs?/.test(line)) : features;
  for (const line of [...benefits, ...ownFeatures]) {
    if (!shared.includes(line) && !lines.includes(line)) lines.push(line);
  }
  return lines;
}

/** One top-up pack's two figures, formatted for display. */
export function packLabels(currency: string, pack: TopUpPack): { credits: string; price: string } {
  return {
    credits: `${formatNumber(pack.credits)} credits`,
    price: formatMoney(currency, pack.price),
  };
}
