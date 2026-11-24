// Typed loader for the plan catalog the billing service also reads. `plans.catalog.json` here is a
// byte-identical vendored copy of inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json
// (the site's Docker build context is ./site only, so it cannot reach the canonical file). CI diffs
// the two. The canonical file carries billing-only keys (entitlements, topUpAllowed, ...); this
// loader validates only the display fields the site renders and ignores everything else.
import raw from './plans.catalog.json';
import { normalizePlan } from './offers';

export type PlanKind = 'trial' | 'subscription' | 'perSeat' | 'contact';
export type PlanCta = 'signup' | 'contact';

export interface PlanLimits {
  maxResolution: string;
  watermark: boolean;
  byoProviders: boolean;
  rooms: boolean;
  videoGeneration: boolean;
  priority: string;
}

/** What a plan entitles its workspace to. A `null` means the catalog sets no figure for it. */
export interface PlanEntitlements {
  maxOutputHeight: number | null;
  assistantMonthlyCredits: number | null;
  assistantMonthlyCreditsPerSeat: number | null;
  assistantUnlimited: boolean;
  storageGb: number | null;
  roomsAllowed: boolean;
  videoGenAllowed: boolean;
  runnerAllowed: boolean;
  byoAllowed: boolean;
  segmentationAllowed: boolean;
  audioGenAllowed: boolean;
  audioCleanupAllowed: boolean;
  maxConcurrentExecutions: number | null;
}

/** A plan as the catalog file spells it: the entitlement keys a plan omits are simply absent. */
export interface RawPlan {
  id: string;
  name: string;
  kind: PlanKind;
  priceMonthly?: number;
  /** Yearly price (per seat on a per-seat plan); 10 x priceMonthly. Absent on trial/enterprise. */
  priceAnnual?: number;
  minSeats?: number;
  includedCredits: number;
  creditsOneTime?: number;
  highlights: string[];
  limits: PlanLimits;
  cta: PlanCta;
  featured?: boolean;
  topUpAllowed?: boolean;
  entitlements?: Partial<PlanEntitlements>;
}

/** A plan as the site reads it: entitlements normalised so every key is present. */
export interface Plan extends Omit<RawPlan, 'topUpAllowed' | 'entitlements'> {
  topUpAllowed: boolean;
  entitlements: PlanEntitlements;
}

export interface TopUpPack {
  credits: number;
  price: number;
}

export interface TopUp {
  credits: number;
  price: number;
  packs: TopUpPack[];
  paidPlansOnly: boolean;
  expiresAfterMonths: number;
}

export interface PlanCatalog {
  schemaVersion: number;
  currency: string;
  taxNote: string;
  plans: RawPlan[];
  topUp?: TopUp;
}

const KINDS: readonly string[] = ['trial', 'subscription', 'perSeat', 'contact'];
const CTAS: readonly string[] = ['signup', 'contact'];

function fail(path: string, why: string): never {
  throw new Error(`plans.catalog.json invalid at ${path}: ${why}`);
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function str(v: unknown, path: string): string {
  if (typeof v !== 'string' || v.trim() === '') fail(path, 'expected a non-empty string');
  return v;
}

function num(v: unknown, path: string, min = 0): number {
  if (typeof v !== 'number' || !Number.isFinite(v) || v < min) fail(path, `expected a number >= ${min}`);
  return v;
}

function bool(v: unknown, path: string): boolean {
  if (typeof v !== 'boolean') fail(path, 'expected a boolean');
  return v;
}

/** Validates the display fields; unknown keys are accepted. Throws on the first problem. */
export function assertCatalog(input: unknown): asserts input is PlanCatalog {
  if (!isRecord(input)) fail('$', 'expected an object');
  if (input.schemaVersion !== 1) fail('schemaVersion', 'unsupported (expected 1)');
  if (input.currency !== 'USD') fail('currency', 'expected "USD"');
  str(input.taxNote, 'taxNote');
  if (!Array.isArray(input.plans) || input.plans.length === 0) fail('plans', 'expected a non-empty array');

  const ids = new Set<string>();
  input.plans.forEach((p: unknown, i: number) => {
    const at = `plans[${i}]`;
    if (!isRecord(p)) fail(at, 'expected an object');
    const id = str(p.id, `${at}.id`);
    if (ids.has(id)) fail(`${at}.id`, `duplicate id "${id}"`);
    ids.add(id);
    str(p.name, `${at}.name`);
    const kind = str(p.kind, `${at}.kind`);
    if (!KINDS.includes(kind)) fail(`${at}.kind`, `unknown kind "${kind}"`);
    if (!CTAS.includes(str(p.cta, `${at}.cta`))) fail(`${at}.cta`, 'expected "signup" or "contact"');
    num(p.includedCredits, `${at}.includedCredits`);
    if (p.creditsOneTime !== undefined) num(p.creditsOneTime, `${at}.creditsOneTime`);
    if (p.featured !== undefined) bool(p.featured, `${at}.featured`);
    if (kind === 'subscription' || kind === 'perSeat') {
      num(p.priceMonthly, `${at}.priceMonthly`, 0.01);
    } else if (p.priceMonthly !== undefined) {
      fail(`${at}.priceMonthly`, `not allowed on a "${kind}" plan`);
    }
    if (p.priceAnnual !== undefined) {
      if (kind !== 'subscription' && kind !== 'perSeat') fail(`${at}.priceAnnual`, `not allowed on a "${kind}" plan`);
      num(p.priceAnnual, `${at}.priceAnnual`, 0.01);
    }
    if (kind === 'perSeat') num(p.minSeats, `${at}.minSeats`, 1);
    if (!Array.isArray(p.highlights) || p.highlights.length === 0) fail(`${at}.highlights`, 'expected a non-empty array');
    p.highlights.forEach((h: unknown, j: number) => str(h, `${at}.highlights[${j}]`));
    if (!isRecord(p.limits)) fail(`${at}.limits`, 'expected an object');
    str(p.limits.maxResolution, `${at}.limits.maxResolution`);
    str(p.limits.priority, `${at}.limits.priority`);
    for (const k of ['watermark', 'byoProviders', 'rooms', 'videoGeneration'] as const) {
      bool(p.limits[k], `${at}.limits.${k}`);
    }
    if (p.topUpAllowed !== undefined) bool(p.topUpAllowed, `${at}.topUpAllowed`);
    if (!isRecord(p.entitlements)) fail(`${at}.entitlements`, 'expected an object');
    const en = p.entitlements;
    for (const k of ['maxOutputHeight', 'assistantMonthlyCredits', 'assistantMonthlyCreditsPerSeat', 'storageGb', 'maxConcurrentExecutions'] as const) {
      if (en[k] !== undefined && en[k] !== null) num(en[k], `${at}.entitlements.${k}`);
    }
    for (const k of ['segmentationAllowed', 'audioGenAllowed', 'audioCleanupAllowed'] as const) {
      if (en[k] !== undefined) bool(en[k], `${at}.entitlements.${k}`);
    }
    if (en.assistantUnlimited !== undefined) bool(en.assistantUnlimited, `${at}.entitlements.assistantUnlimited`);
    for (const k of ['roomsAllowed', 'videoGenAllowed', 'runnerAllowed', 'byoAllowed'] as const) {
      bool(en[k], `${at}.entitlements.${k}`);
    }
  });

  if (input.topUp !== undefined) {
    if (!isRecord(input.topUp)) fail('topUp', 'expected an object');
    num(input.topUp.credits, 'topUp.credits', 1);
    num(input.topUp.price, 'topUp.price', 0.01);
    if (!Array.isArray(input.topUp.packs) || input.topUp.packs.length === 0) {
      fail('topUp.packs', 'expected a non-empty array');
    }
    input.topUp.packs.forEach((pack: unknown, i: number) => {
      const at = `topUp.packs[${i}]`;
      if (!isRecord(pack)) fail(at, 'expected an object');
      num(pack.credits, `${at}.credits`, 1);
      num(pack.price, `${at}.price`, 0.01);
    });
    bool(input.topUp.paidPlansOnly, 'topUp.paidPlansOnly');
    num(input.topUp.expiresAfterMonths, 'topUp.expiresAfterMonths', 1);
  }
}

assertCatalog(raw);
const catalog: PlanCatalog = raw;

export const currency = catalog.currency;
export const taxNote = catalog.taxNote;
export const plans: readonly Plan[] = catalog.plans.map(normalizePlan);
export const topUp: TopUp | undefined = catalog.topUp;

/** Plans with a monthly price (subscription and per-seat). */
export const paidPlans: readonly Plan[] = plans.filter((p) => p.priceMonthly !== undefined);

const prices = paidPlans.map((p) => p.priceMonthly as number);
export const lowPrice: number | undefined = prices.length ? Math.min(...prices) : undefined;
export const highPrice: number | undefined = prices.length ? Math.max(...prices) : undefined;
