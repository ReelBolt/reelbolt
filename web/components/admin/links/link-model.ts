import { msg, type Msg } from '@/i18n';
import { PERSONAS, type Persona } from '@/lib/attribution/persona';
import {
  LINK_CHANNELS,
  LINK_CODE_MAX_LENGTH,
  LINK_CODE_PATTERN,
  LINK_DESTINATION_MAX_LENGTH,
  LINK_MEDIA,
  LINK_PLACEMENT_MAX_LENGTH,
  UTM_VALUE_PATTERN,
  type CreateMarketingLinkRequest,
  type FunnelAccountSteps,
  type LinkChannel,
  type LinkMedium,
  type MarketingLink,
  type MarketingLinkFunnelItem,
  type PatchMarketingLinkRequest,
} from '@/lib/types/marketing-links';

/**
 * The pure half of the Links page: the funnel maths, the code suggestion and the form rules. No
 * React and no English text; a message is a `Msg` the component resolves.
 *
 * The form rules mirror the Inference API's (`TrackedLinkRules`, `AcquisitionVocabulary`), so a
 * value this module accepts is one the server accepts. The server stays the authority: its refusal
 * codes are mapped back onto the fields by `fieldOfErrorCode`.
 */

// ------------------------------------------------------------------------------------ labels

export const CHANNEL_LABELS: Record<LinkChannel, Msg> = {
  hackernews: msg('adminLinks.channels.hackernews'),
  reddit: msg('adminLinks.channels.reddit'),
  linkedin: msg('adminLinks.channels.linkedin'),
  x: msg('adminLinks.channels.x'),
  producthunt: msg('adminLinks.channels.producthunt'),
  youtube: msg('adminLinks.channels.youtube'),
  tiktok: msg('adminLinks.channels.tiktok'),
  instagram: msg('adminLinks.channels.instagram'),
  facebook: msg('adminLinks.channels.facebook'),
  github: msg('adminLinks.channels.github'),
  discord: msg('adminLinks.channels.discord'),
  newsletter: msg('adminLinks.channels.newsletter'),
  email: msg('adminLinks.channels.email'),
  blog: msg('adminLinks.channels.blog'),
  directory: msg('adminLinks.channels.directory'),
  press: msg('adminLinks.channels.press'),
  partner: msg('adminLinks.channels.partner'),
  ads: msg('adminLinks.channels.ads'),
  other: msg('adminLinks.channels.other'),
};

export const MEDIUM_LABELS: Record<LinkMedium, Msg> = {
  social: msg('adminLinks.media.social'),
  community: msg('adminLinks.media.community'),
  video: msg('adminLinks.media.video'),
  email: msg('adminLinks.media.email'),
  referral: msg('adminLinks.media.referral'),
  paid: msg('adminLinks.media.paid'),
  owned: msg('adminLinks.media.owned'),
  press: msg('adminLinks.media.press'),
};

export const PERSONA_LABELS: Record<Persona, Msg> = {
  creator: msg('adminLinks.personas.creator'),
  business: msg('adminLinks.personas.business'),
  educator: msg('adminLinks.personas.educator'),
  nonprofit: msg('adminLinks.personas.nonprofit'),
  software: msg('adminLinks.personas.software'),
  agency: msg('adminLinks.personas.agency'),
  other: msg('adminLinks.personas.other'),
};

/**
 * The audiences a link may be given: the ones with a landing page. `other` is part of the shared
 * vocabulary but is chosen in the app only (docs/design/new-user-funnel.md section 2), so the forms
 * do not offer it. It keeps its label, because a link saved earlier may still carry it.
 */
export type LinkPersona = Exclude<Persona, 'other'>;
export const LINK_PERSONAS: readonly LinkPersona[] = PERSONAS.filter((persona): persona is LinkPersona => persona !== 'other');
export const isLinkPersona = (value: string): value is LinkPersona => (LINK_PERSONAS as readonly string[]).includes(value);

/**
 * The values the audience select lists, in order: the audiences a link may be given, then the
 * form's current value when it is not one of them (`other` on a link saved earlier, or a token a
 * newer server wrote). Listing it keeps that link showing what it really holds; an edit that leaves
 * the audience alone then sends no audience at all.
 */
export function personaChoices(current: string): string[] {
  return current && !isLinkPersona(current) ? [...LINK_PERSONAS, current] : [...LINK_PERSONAS];
}

export const isLinkChannel = (value: string): value is LinkChannel => (LINK_CHANNELS as readonly string[]).includes(value);
export const isLinkMedium = (value: string): value is LinkMedium => (LINK_MEDIA as readonly string[]).includes(value);
export const isPersona = (value: string): value is Persona => (PERSONAS as readonly string[]).includes(value);

/** The label of a channel token, or null for one this build does not know (the caller shows the token). */
export const channelLabel = (value: string): Msg | null => (isLinkChannel(value) ? CHANNEL_LABELS[value] : null);
export const mediumLabel = (value: string): Msg | null => (isLinkMedium(value) ? MEDIUM_LABELS[value] : null);
export const personaLabel = (value: string): Msg | null => (isPersona(value) ? PERSONA_LABELS[value] : null);

// --------------------------------------------------------------------------- channel -> medium

/**
 * The medium a channel usually means. A starting point only: the form preselects it and the admin
 * may change it (a newsletter someone else sends is `referral`, a sponsored post is `paid`).
 */
export const DEFAULT_MEDIUM: Record<LinkChannel, LinkMedium> = {
  hackernews: 'community',
  reddit: 'community',
  linkedin: 'social',
  x: 'social',
  producthunt: 'community',
  youtube: 'video',
  tiktok: 'video',
  instagram: 'social',
  facebook: 'social',
  github: 'community',
  discord: 'community',
  newsletter: 'email',
  email: 'email',
  blog: 'owned',
  directory: 'referral',
  press: 'press',
  partner: 'referral',
  ads: 'paid',
  other: 'referral',
};

// ----------------------------------------------------------------------------- landing pages

/** A landing page the form offers by name. `persona` is the audience the page speaks to. */
export interface LandingOption {
  path: string;
  label: Msg;
  persona?: Persona;
}

/** The value of the landing select that reveals the free path field. Never a path (it has no `/`). */
export const CUSTOM_LANDING = 'custom';

export const LANDING_OPTIONS: readonly LandingOption[] = [
  { path: '/', label: msg('adminLinks.landing.home') },
  { path: '/for/creators', label: msg('adminLinks.landing.creators'), persona: 'creator' },
  { path: '/for/small-business', label: msg('adminLinks.landing.smallBusiness'), persona: 'business' },
  { path: '/for/educators', label: msg('adminLinks.landing.educators'), persona: 'educator' },
  { path: '/for/nonprofits', label: msg('adminLinks.landing.nonprofits'), persona: 'nonprofit' },
  { path: '/for/software-teams', label: msg('adminLinks.landing.softwareTeams'), persona: 'software' },
  { path: '/for/agencies', label: msg('adminLinks.landing.agencies'), persona: 'agency' },
  { path: '/pricing', label: msg('adminLinks.landing.pricing') },
  { path: '/recipes', label: msg('adminLinks.landing.recipes') },
  { path: '/blog', label: msg('adminLinks.landing.blog') },
  // The sign-up page is deliberately not offered. The dashboard app shows no cookie banner (the
  // choice is made on the marketing site), so a person sent straight to `/app/signup` is never
  // asked: the link's Visitors would read 0 for good and, with URL capture off, so would its
  // signups. A landing page of the site asks first and then leads to sign-up with the campaign
  // attached. The path can still be typed as a custom one; the form then says what it costs.
];

/** The dashboard app's own pages: reachable as a custom landing path, but nobody is asked about cookies there. */
export function isAppLandingPath(path: string): boolean {
  const value = path.trim().toLowerCase();
  return value === '/app' || value.startsWith('/app/');
}

/** The audience of a landing page, or undefined when the page speaks to everyone. */
export function personaOfLanding(path: string): Persona | undefined {
  return LANDING_OPTIONS.find((option) => option.path === path)?.persona;
}

/**
 * The audience after the landing page changes. It follows the page for as long as it matched the
 * previous page (both "none" counts as a match), so picking `/for/educators` fills in educators, and
 * an audience the admin chose by hand is left alone.
 */
export function personaAfterLandingChange(previousPath: string, nextPath: string, persona: string): string {
  return persona === (personaOfLanding(previousPath) ?? '') ? (personaOfLanding(nextPath) ?? '') : persona;
}

/** The select value for a stored destination: the path itself when the form lists it, else `custom`. */
export function landingChoiceOf(destinationPath: string): string {
  return LANDING_OPTIONS.some((option) => option.path === destinationPath) ? destinationPath : CUSTOM_LANDING;
}

// ---------------------------------------------------------------------------------- validation

export function isLinkCode(value: string): boolean {
  return LINK_CODE_PATTERN.test(value) && !value.includes('\n');
}

export function isUtmValue(value: string): boolean {
  return UTM_VALUE_PATTERN.test(value) && !value.includes('\n');
}

/**
 * A campaign token as typed: trimmed, lowercased, inner white space turned into hyphens. What is
 * left may still be invalid (an accent, a slash); the field then says so instead of guessing.
 */
export function normalizeCampaign(raw: string): string {
  return raw.trim().toLowerCase().replace(/\s+/g, '-');
}

/**
 * An own-site destination, exactly as the API checks it: one leading `/`, only ASCII letters,
 * digits and `/ _ . ~ -`, never `//`, no `.` or `..` segment, at most 200 characters, and not a
 * tracked link itself (`/l` or `/l/...`), which would loop. So no host, query or fragment.
 */
export function isDestinationPath(path: string): boolean {
  if (path.length > LINK_DESTINATION_MAX_LENGTH) return false;
  if (!/^\/[A-Za-z0-9/_.~-]{0,199}$/.test(path) || path.includes('\n') || path.includes('//')) return false;
  if (path.split('/').some((segment) => segment === '.' || segment === '..')) return false;
  return path !== '/l' && !path.startsWith('/l/');
}

/** A placement note the API accepts: 1 to 160 characters after trimming, none a control character. */
export function isPlacement(value: string): boolean {
  const text = value.trim();
  return text.length > 0 && text.length <= LINK_PLACEMENT_MAX_LENGTH && !/[\u0000-\u001F\u007F-\u009F]/.test(text);
}

export function isCurrencyCode(value: string): boolean {
  return /^[A-Z]{3}$/.test(value);
}

// ------------------------------------------------------------------------------------- money

/**
 * ISO 4217 minor-unit exponents that are not 2. A fixed table, on purpose: the stored cost is an
 * integer of minor units, so the number written for an amount must be the same in every browser
 * and for every reader of the table (the reports included). `Intl` cannot be the source: its
 * currency digits come from the browser's own locale data, which rounds several currencies to
 * whole units for display (HUF, IDR, COP, PKR, IQD) where ISO 4217 gives them 2 or 3 decimals.
 *
 * Active codes of the ISO 4217 list. A code that is in neither list has 2 decimals. (The two
 * accounting units ISO lists with 4 decimals, CLF and UYW, are not currencies anything is paid in;
 * they fall under the default too.)
 */
const ZERO_DECIMAL_CURRENCIES: ReadonlySet<string> = new Set([
  'BIF', 'CLP', 'DJF', 'GNF', 'ISK', 'JPY', 'KMF', 'KRW', 'PYG', 'RWF', 'UGX', 'UYI', 'VND', 'VUV', 'XAF', 'XOF', 'XPF',
]);
const THREE_DECIMAL_CURRENCIES: ReadonlySet<string> = new Set(['BHD', 'IQD', 'JOD', 'KWD', 'LYD', 'OMR', 'TND']);

/**
 * How many decimals a currency's minor unit has under ISO 4217: 0 for JPY, 3 for KWD, 2 for EUR and
 * for every code not listed above. The one exponent used to write a cost and to show it.
 */
export function minorUnitDigits(currency: string): number {
  const code = currency.trim().toUpperCase();
  if (ZERO_DECIMAL_CURRENCIES.has(code)) return 0;
  if (THREE_DECIMAL_CURRENCIES.has(code)) return 3;
  return 2;
}

/**
 * The decimals a stored cost is shown with: exactly the ones it was written with. Passed to the
 * currency formatter so its own default (the browser's, which differs between browsers for several
 * currencies) is never what rounds the figure on screen.
 */
export function costFormatOptions(currency: string): { minimumFractionDigits: number; maximumFractionDigits: number } {
  const digits = minorUnitDigits(currency);
  return { minimumFractionDigits: digits, maximumFractionDigits: digits };
}

/** An amount in the currency's main unit as minor units (12.5 EUR -> 1250). */
export function toMinorUnits(amount: number, currency: string): number {
  return Math.round(amount * 10 ** minorUnitDigits(currency));
}

/** Minor units as an amount in the currency's main unit (1250 -> 12.5 EUR). */
export function fromMinorUnits(minor: number, currency: string): number {
  return minor / 10 ** minorUnitDigits(currency);
}

// ---------------------------------------------------------------------------------- the forms

export type LinkFormField = 'placement' | 'channel' | 'medium' | 'campaign' | 'destination' | 'persona' | 'cost' | 'code';
export type LinkFormErrors = Partial<Record<LinkFormField, Msg>>;

/** What both forms hold, as typed. `landing` is a listed path or `CUSTOM_LANDING`. */
export interface LinkDetailsValues {
  placement: string;
  landing: string;
  customPath: string;
  /** A persona token, or '' for none. */
  persona: string;
  /** The amount in the currency's main unit, or '' for no cost. */
  cost: number | '';
  currency: string;
}

export interface NewLinkValues extends LinkDetailsValues {
  channel: string;
  medium: string;
  campaign: string;
  code: string;
}

/** The destination a form means: the listed path, or the trimmed custom one. */
export function destinationOf(values: Pick<LinkDetailsValues, 'landing' | 'customPath'>): string {
  return values.landing === CUSTOM_LANDING ? values.customPath.trim() : values.landing;
}

function validateDetails(values: LinkDetailsValues): LinkFormErrors {
  const errors: LinkFormErrors = {};
  if (!values.placement.trim()) errors.placement = msg('adminLinks.errors.placementRequired');
  else if (!isPlacement(values.placement)) errors.placement = msg('adminLinks.errors.placementInvalid');

  const destination = destinationOf(values);
  if (!destination) errors.destination = msg('adminLinks.errors.destinationRequired');
  else if (!isDestinationPath(destination)) errors.destination = msg('adminLinks.errors.destinationInvalid');

  if (values.persona && !isPersona(values.persona)) errors.persona = msg('adminLinks.errors.personaInvalid');

  if (values.cost !== '') {
    if (!Number.isFinite(values.cost) || values.cost < 0) errors.cost = msg('adminLinks.errors.costInvalid');
    else if (!isCurrencyCode(values.currency.trim().toUpperCase())) errors.cost = msg('adminLinks.errors.currencyInvalid');
  }
  return errors;
}

/** Field -> message for the edit form; empty when it can be saved. */
export function validateEditLink(values: LinkDetailsValues): LinkFormErrors {
  return validateDetails(values);
}

/**
 * Field -> message for the new-link form; empty when it can be sent. `takenCodes` are the codes of
 * every loaded link, archived ones included: a code is never reused.
 */
export function validateNewLink(values: NewLinkValues, takenCodes: readonly string[]): LinkFormErrors {
  const errors = validateDetails(values);
  // A new link takes only an audience the form offers. An edit still accepts `other` (above), so a
  // link saved with it earlier can be changed without touching its audience.
  if (values.persona && !isLinkPersona(values.persona)) errors.persona = msg('adminLinks.errors.personaInvalid');
  if (!values.channel) errors.channel = msg('adminLinks.errors.channelRequired');
  else if (!isLinkChannel(values.channel)) errors.channel = msg('adminLinks.errors.channelInvalid');
  if (!values.medium || !isLinkMedium(values.medium)) errors.medium = msg('adminLinks.errors.mediumRequired');

  const campaign = normalizeCampaign(values.campaign);
  if (!campaign) errors.campaign = msg('adminLinks.errors.campaignRequired');
  else if (!isUtmValue(campaign)) errors.campaign = msg('adminLinks.errors.campaignInvalid');

  const code = values.code.trim();
  if (!code) errors.code = msg('adminLinks.errors.codeRequired');
  else if (!isLinkCode(code)) errors.code = msg('adminLinks.errors.codeInvalid');
  else if (takenCodes.includes(code)) errors.code = msg('adminLinks.errors.codeTaken');
  return errors;
}

function costOf(values: LinkDetailsValues): { costMinor: number; costCurrency: string } | null {
  if (values.cost === '') return null;
  const costCurrency = values.currency.trim().toUpperCase();
  return { costMinor: toMinorUnits(values.cost, costCurrency), costCurrency };
}

/** The POST body of a valid new-link form. Persona and cost are left out when not set. */
export function createRequestOf(values: NewLinkValues): CreateMarketingLinkRequest {
  const cost = costOf(values);
  return {
    code: values.code.trim(),
    channel: values.channel,
    medium: values.medium,
    campaign: normalizeCampaign(values.campaign),
    placement: values.placement.trim(),
    destinationPath: destinationOf(values),
    ...(values.persona ? { persona: values.persona } : {}),
    ...(cost ?? {}),
  };
}

/** What the edit form starts from for a stored link. */
export function detailsOf(link: MarketingLink, fallbackCurrency: string): LinkDetailsValues {
  const landing = landingChoiceOf(link.destinationPath);
  const hasCost = link.costMinor !== null && link.costCurrency !== null;
  return {
    placement: link.placement,
    landing,
    customPath: landing === CUSTOM_LANDING ? link.destinationPath : '',
    persona: link.persona ?? '',
    cost: hasCost ? fromMinorUnits(link.costMinor as number, link.costCurrency as string) : '',
    currency: hasCost ? (link.costCurrency as string) : fallbackCurrency,
  };
}

/**
 * The PATCH body of a valid edit form: only the fields that differ from the stored link. A cleared
 * persona or cost is sent as `null`; the two cost fields always travel together, because the API
 * accepts both or neither. An empty object means there is nothing to save.
 */
export function patchRequestOf(link: MarketingLink, values: LinkDetailsValues): PatchMarketingLinkRequest {
  const patch: PatchMarketingLinkRequest = {};
  const placement = values.placement.trim();
  if (placement !== link.placement) patch.placement = placement;
  const destination = destinationOf(values);
  if (destination !== link.destinationPath) patch.destinationPath = destination;
  const persona = values.persona || null;
  if (persona !== link.persona) patch.persona = persona;
  const cost = costOf(values);
  if ((cost?.costMinor ?? null) !== link.costMinor || (cost?.costCurrency ?? null) !== link.costCurrency) {
    patch.costMinor = cost?.costMinor ?? null;
    patch.costCurrency = cost?.costCurrency ?? null;
  }
  return patch;
}

/** The form field a server refusal is about, or null when it is about none of them. */
export function fieldOfErrorCode(code: string | null): LinkFormField | null {
  switch (code) {
    case 'code_invalid':
    case 'link_code_taken':
      return 'code';
    case 'channel_invalid':
      return 'channel';
    case 'medium_invalid':
      return 'medium';
    case 'campaign_invalid':
      return 'campaign';
    case 'placement_invalid':
      return 'placement';
    case 'destination_path_invalid':
      return 'destination';
    case 'persona_invalid':
      return 'persona';
    case 'cost_invalid':
      return 'cost';
    default:
      return null;
  }
}

/** The sentence for a server refusal of one field. */
export function messageOfErrorCode(code: string | null): Msg | null {
  switch (code) {
    case 'code_invalid':
      return msg('adminLinks.errors.codeInvalid');
    case 'link_code_taken':
      return msg('adminLinks.errors.codeTaken');
    case 'channel_invalid':
      return msg('adminLinks.errors.channelInvalid');
    case 'medium_invalid':
      return msg('adminLinks.errors.mediumRequired');
    case 'campaign_invalid':
      return msg('adminLinks.errors.campaignInvalid');
    case 'placement_invalid':
      return msg('adminLinks.errors.placementInvalid');
    case 'destination_path_invalid':
      return msg('adminLinks.errors.destinationInvalid');
    case 'persona_invalid':
      return msg('adminLinks.errors.personaInvalid');
    case 'cost_invalid':
      return msg('adminLinks.errors.costInvalid');
    default:
      return null;
  }
}

// ----------------------------------------------------------------------------- code suggestion

/** Short forms of the channels whose name is long and whose abbreviation is the one people use. */
const CHANNEL_CODE_PREFIX: Partial<Record<LinkChannel, string>> = {
  hackernews: 'hn',
  producthunt: 'ph',
  youtube: 'yt',
  instagram: 'ig',
  facebook: 'fb',
  github: 'gh',
};

/** How many words of the placement note go into a suggested code. */
const PLACEMENT_WORDS_IN_CODE = 3;

/** Lowercase ASCII words joined by hyphens: accents folded, everything else a separator. */
export function slugify(text: string): string {
  return text
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/** `words` joined by hyphens, dropping trailing words until it fits `max`; one over-long word is cut. */
function fitWords(words: string[], max: number): string {
  const kept = [...words];
  while (kept.length > 1 && kept.join('-').length > max) kept.pop();
  return kept.join('-').slice(0, max).replace(/-+$/, '');
}

/**
 * A code built from the channel, the campaign and the first words of the placement note
 * (`hn-launch-2026-11-show-hn-first`), unique among `takenCodes` by a numeric suffix. Always a valid
 * link code, or '' while there is too little to build one from.
 */
export function suggestLinkCode(
  parts: { channel: string; campaign: string; placement: string },
  takenCodes: readonly string[],
): string {
  const channel = isLinkChannel(parts.channel) ? (CHANNEL_CODE_PREFIX[parts.channel] ?? parts.channel) : slugify(parts.channel);
  const words = [
    ...slugify(channel).split('-'),
    ...slugify(parts.campaign).split('-'),
    ...slugify(parts.placement).split('-').slice(0, PLACEMENT_WORDS_IN_CODE),
  ].filter(Boolean);
  const base = fitWords(words, LINK_CODE_MAX_LENGTH);
  if (!isLinkCode(base)) return '';
  if (!takenCodes.includes(base)) return base;
  for (let n = 2; n < 10_000; n += 1) {
    const suffix = `-${n}`;
    const candidate = fitWords(words, LINK_CODE_MAX_LENGTH - suffix.length) + suffix;
    if (isLinkCode(candidate) && !takenCodes.includes(candidate)) return candidate;
  }
  return '';
}

// --------------------------------------------------------------------------------- the funnel

/** The account steps in funnel order. Each one is a column of the table. */
export const ACCOUNT_STEPS = ['signups', 'verified', 'onboarded', 'firstProject', 'firstRun', 'firstVideo', 'paid'] as const;
export type AccountStep = (typeof ACCOUNT_STEPS)[number];

export const STEP_LABELS: Record<AccountStep, Msg> = {
  signups: msg('adminLinks.steps.signups'),
  verified: msg('adminLinks.steps.verified'),
  onboarded: msg('adminLinks.steps.onboarded'),
  firstProject: msg('adminLinks.steps.firstProject'),
  firstRun: msg('adminLinks.steps.firstRun'),
  firstVideo: msg('adminLinks.steps.firstVideo'),
  paid: msg('adminLinks.steps.paid'),
};

/** The sentence that names where a link's accounts stop, by the step they did not reach. */
export const STOP_LABELS: Record<Exclude<AccountStep, 'signups'>, Msg> = {
  verified: msg('adminLinks.stop.verified'),
  onboarded: msg('adminLinks.stop.onboarded'),
  firstProject: msg('adminLinks.stop.firstProject'),
  firstRun: msg('adminLinks.stop.firstRun'),
  firstVideo: msg('adminLinks.stop.firstVideo'),
  paid: msg('adminLinks.stop.paid'),
};

/** `part / whole` as a fraction, or null when there is nothing to divide by. */
export function share(part: number, whole: number): number | null {
  return whole > 0 ? part / whole : null;
}

/**
 * Each account step as a share of the step before it (signups has none: it is the cohort). The steps
 * are counted independently, so a share can be above 1: a person can pay before a video finishes.
 */
export function stepShares(steps: FunnelAccountSteps): Record<AccountStep, number | null> {
  const out = {} as Record<AccountStep, number | null>;
  ACCOUNT_STEPS.forEach((step, index) => {
    out[step] = index === 0 ? null : share(steps[step], steps[ACCOUNT_STEPS[index - 1]]);
  });
  return out;
}

/** Each account step as a share of the signups, clamped to 0..1: the length of its funnel bar. */
export function barShares(steps: FunnelAccountSteps): Record<AccountStep, number> {
  const out = {} as Record<AccountStep, number>;
  for (const step of ACCOUNT_STEPS) out[step] = Math.min(1, Math.max(0, share(steps[step], steps.signups) ?? 0));
  return out;
}

export interface BiggestDrop {
  /** The last step these accounts did reach. */
  from: AccountStep;
  /** The step they did not reach: the column the table marks. */
  step: Exclude<AccountStep, 'signups'>;
  /** How many accounts were lost between the two. */
  lost: number;
  /** How many had reached `from`. */
  of: number;
}

/**
 * Where most accounts stop: the pair of neighbouring account steps with the largest loss in
 * accounts (not in percent, which a step with three accounts would win). The earliest pair wins a
 * tie. Null when no step lost anyone, which includes a link with no signups.
 *
 * Clicks and visitors are deliberately not part of this: they count different people (everyone,
 * and those who accepted cookies), so "clicked but did not sign up" is not a loss of accounts.
 */
export function biggestDrop(steps: FunnelAccountSteps): BiggestDrop | null {
  let best: BiggestDrop | null = null;
  for (let i = 1; i < ACCOUNT_STEPS.length; i += 1) {
    const from = ACCOUNT_STEPS[i - 1];
    const step = ACCOUNT_STEPS[i] as BiggestDrop['step'];
    const lost = steps[from] - steps[step];
    if (lost > 0 && (best === null || lost > best.lost)) best = { from, step, lost, of: steps[from] };
  }
  return best;
}

export type FunnelCellKey = 'clicks' | 'visitors' | AccountStep;

/** One figure of a funnel row, with everything the table and the phone list draw around it. */
export interface FunnelCell {
  key: FunnelCellKey;
  /** Null when the row has no such figure (clicks of the unattributed accounts, a link the funnel did not carry). */
  count: number | null;
  /** The share shown under the count, or null when there is nothing to compare with. */
  share: number | null;
  /** What `share` is a share of: the clicks, or the account step before. */
  shareOf: 'clicks' | 'previous' | null;
  /** The length of the funnel bar (0..1, share of the signups); null for the figures that are not accounts. */
  bar: number | null;
  /** True on the account step where most of the row's accounts stopped. */
  marked: boolean;
}

export interface FunnelRowCounts {
  clicks: number | null;
  visitors: number | null;
  steps: FunnelAccountSteps | null;
}

/**
 * A funnel row as its nine cells, in column order: clicks, visitors, then the account steps.
 * Visitors and signups are shown as a share of the clicks (a different population, so a rate, not a
 * loss); every later step as a share of the step before, with the biggest loss marked.
 */
export function funnelCells(row: FunnelRowCounts): FunnelCell[] {
  const ofClicks = (count: number | null) => (count !== null && row.clicks !== null ? share(count, row.clicks) : null);
  const cells: FunnelCell[] = [
    { key: 'clicks', count: row.clicks, share: null, shareOf: null, bar: null, marked: false },
    {
      key: 'visitors',
      count: row.visitors,
      share: ofClicks(row.visitors),
      shareOf: ofClicks(row.visitors) === null ? null : 'clicks',
      bar: null,
      marked: false,
    },
  ];
  if (row.steps === null) {
    for (const step of ACCOUNT_STEPS) cells.push({ key: step, count: null, share: null, shareOf: null, bar: null, marked: false });
    return cells;
  }
  const shares = stepShares(row.steps);
  const bars = barShares(row.steps);
  const drop = biggestDrop(row.steps);
  for (const step of ACCOUNT_STEPS) {
    const value = step === 'signups' ? ofClicks(row.steps.signups) : shares[step];
    cells.push({
      key: step,
      count: row.steps[step],
      share: value,
      shareOf: value === null ? null : step === 'signups' ? 'clicks' : 'previous',
      bar: bars[step],
      marked: drop?.step === step,
    });
  }
  return cells;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Does a period of `days` ending at `asOf` reach back to the day the link was created? Only then
 * are the link's signups in the period all of its signups, which is what its whole cost is for.
 * An unreadable date answers no.
 */
export function periodCoversLink(createdAt: string, days: number, asOf: string | number | Date): boolean {
  const created = Date.parse(createdAt);
  const end = asOf instanceof Date ? asOf.getTime() : typeof asOf === 'number' ? asOf : Date.parse(asOf);
  return Number.isFinite(created) && Number.isFinite(end) && created >= end - days * DAY_MS;
}

/**
 * Cost per signup, in minor units: the link's whole cost over ALL of its signups. A link's cost is
 * one figure for its whole life, so the division is only made when the period shown covers that
 * whole life (`coversLife`, see {@link periodCoversLink}). Dividing by the signups of a shorter
 * period would make the same placement read cheap at 30 days and expensive at 7, an answer that
 * changes with the selector and says nothing about the campaign. Null when the cost is missing,
 * there is no signup, or the period is shorter than the link's life.
 */
export function costPerSignupMinor(costMinor: number | null, signups: number, coversLife = true): number | null {
  return costMinor === null || signups <= 0 || !coversLife ? null : costMinor / signups;
}

export interface FunnelTotals extends FunnelAccountSteps {
  links: number;
  clicks: number;
  botHits: number;
  visitors: number;
  /** The summed cost per currency, in the order the currencies first appear. Empty when no link has a cost. */
  costs: { currency: string; minor: number }[];
  /**
   * Cost per signup over the links that have a cost, when they all share one currency and the
   * period covers the whole life of every one of them: their summed cost over their summed signups.
   * Null with no cost, mixed currencies, no signup on those links, or a costed link older than the
   * period (its cost is for signups the period does not show).
   */
  costPerSignup: { currency: string; minor: number } | null;
}

/**
 * The totals row: every count summed over `items`. `coversLife` says, per link, whether the period
 * reaches back to its creation (see {@link periodCoversLink}); left out, every link is taken as covered.
 */
export function funnelTotals(
  items: readonly MarketingLinkFunnelItem[],
  coversLife: (item: MarketingLinkFunnelItem) => boolean = () => true,
): FunnelTotals {
  const totals: FunnelTotals = {
    links: items.length,
    clicks: 0,
    botHits: 0,
    visitors: 0,
    signups: 0,
    verified: 0,
    onboarded: 0,
    firstProject: 0,
    firstRun: 0,
    firstVideo: 0,
    paid: 0,
    costs: [],
    costPerSignup: null,
  };
  let costedSignups = 0;
  let everyCostedLinkCovered = true;
  for (const item of items) {
    totals.clicks += item.clicks;
    totals.botHits += item.botHits;
    totals.visitors += item.visitors;
    for (const step of ACCOUNT_STEPS) totals[step] += item[step];
    if (item.costMinor !== null && item.costCurrency) {
      const entry = totals.costs.find((cost) => cost.currency === item.costCurrency);
      if (entry) entry.minor += item.costMinor;
      else totals.costs.push({ currency: item.costCurrency, minor: item.costMinor });
      costedSignups += item.signups;
      if (!coversLife(item)) everyCostedLinkCovered = false;
    }
  }
  if (totals.costs.length === 1 && costedSignups > 0 && everyCostedLinkCovered) {
    totals.costPerSignup = { currency: totals.costs[0].currency, minor: totals.costs[0].minor / costedSignups };
  }
  return totals;
}

/**
 * The funnel row of a link, or null for a link the funnel response does not carry (one created after
 * the funnel was read). Null is shown as "no figure", never as zeros: a count nobody read is not 0.
 */
export function funnelRowOf(link: MarketingLink, items: readonly MarketingLinkFunnelItem[]): MarketingLinkFunnelItem | null {
  return items.find((item) => item.linkId === link.id) ?? null;
}

/** The distinct campaign tokens of the loaded links, most recently created first (the list's own order). */
export function campaignSuggestions(links: readonly MarketingLink[]): string[] {
  return [...new Set(links.map((link) => link.campaign).filter(Boolean))];
}

/** The currency a cost starts in before any link has one: the operator's own (an Italian business). */
export const DEFAULT_COST_CURRENCY = 'EUR';

/** The currency of the newest link that has a cost, so a second paid placement starts in the same one. */
export function startingCurrency(links: readonly MarketingLink[]): string {
  return links.find((link) => link.costCurrency)?.costCurrency ?? DEFAULT_COST_CURRENCY;
}
