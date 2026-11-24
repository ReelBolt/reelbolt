// Forwards a contact-form newsletter opt-in to the API's double opt-in endpoint
// (POST /api/v1/marketing/subscribe), which answers 202 { status: "checkEmail" } in every case and
// sends the confirmation email itself. Shared by the Node route (app/api/contact/route.ts) and the
// legacy, unused Pages Function (functions/api/contact.ts).
//
// It never throws: the enquiry has already been accepted by the time this runs, and a slow or
// broken API must not turn that into a failure. Errors are logged without the email address.
import { validateContact } from '../validation.ts';
import { isHoneypotFilled, isTooFastSubmission } from '../bot-checks.ts';

export type SubscribeSource = 'siteContact' | 'siteFooter';

export const SUBSCRIBE_TIMEOUT_MS = 5000;

export const SITE_TOKEN_HEADER = 'X-ReelBolt-Site-Token';
export const CLIENT_IP_HEADER = 'X-ReelBolt-Client-IP';

export interface ForwardSubscribeDeps {
  // The registry's current wording version. Injected (never imported here) so this module stays
  // free of JSON import attributes, which the Pages Functions bundler rejects.
  wordingVersion: string;
  fetch?: typeof fetch;
  timeoutMs?: number;
  log?: (message: string, detail?: unknown) => void;
  // Shared secret (SITE_FORWARD_TOKEN) that lets the API trust CLIENT_IP_HEADER, so its per-IP
  // limit counts the visitor rather than the site. Unset or blank: neither header is sent.
  siteToken?: string;
  // The visitor's address as this runtime knows it (clientIpKey for the Node route,
  // CF-Connecting-IP for the Pages function). Sent only together with the token.
  clientIp?: string;
  // The visitor's language (a registered locale code, already allowlisted). Sent as `locale` so the
  // confirmation mail can arrive in it; omitted when absent.
  locale?: string;
  // The visitor's optional first name, already passed through cleanFirstName. Omitted when absent.
  firstName?: string;
}

export const FIRST_NAME_MAX_LENGTH = 40;

// Starts with a letter; then only letters, combining marks, spaces, apostrophes (' and U+2019), hyphens.
const FIRST_NAME_ALLOWED = /^\p{L}[\p{L}\p{M} '\u2019-]*$/u;

// Script ids, a port of the API's MarketingName.CleanFormName (authoritative). The site is a
// pre-filter and is meant to refuse at least everything the API refuses; keep the two in step.
const JAPANESE_GROUP = 100;
const isJapanese = (id: number) => id === JAPANESE_GROUP || id === 101 || id === 102 || id === 103;
const inRange = (c: number, lo: number, hi: number) => c >= lo && c <= hi;

/** The script id of a letter by explicit block allowlist; 0 when the letter is not allowed. */
function scriptOf(c: number): number {
  if (
    inRange(c, 0x41, 0x5a) || inRange(c, 0x61, 0x7a) || (inRange(c, 0xc0, 0xff) && c !== 0xd7 && c !== 0xf7) ||
    inRange(c, 0x100, 0x24f) || inRange(c, 0x1e00, 0x1eff)
  ) return 1; // Latin
  if (inRange(c, 0x370, 0x3ff) || inRange(c, 0x1f00, 0x1fff)) return 2; // Greek
  if (inRange(c, 0x400, 0x52f)) return 3; // Cyrillic
  if (inRange(c, 0x531, 0x587)) return 4; // Armenian
  if (inRange(c, 0x5d0, 0x5ea)) return 5; // Hebrew
  if (inRange(c, 0x620, 0x64a) || inRange(c, 0x66e, 0x6d3) || inRange(c, 0x6fa, 0x6fc)) return 6; // Arabic
  if (inRange(c, 0x900, 0x97f)) return 7; // Devanagari
  if (inRange(c, 0x980, 0x9ff)) return 8; // Bengali
  if (inRange(c, 0xa00, 0xa7f)) return 9; // Gurmukhi
  if (inRange(c, 0xa80, 0xaff)) return 10; // Gujarati
  if (inRange(c, 0xb00, 0xb7f)) return 11; // Oriya
  if (inRange(c, 0xb80, 0xbff)) return 12; // Tamil
  if (inRange(c, 0xc00, 0xc7f)) return 13; // Telugu
  if (inRange(c, 0xc80, 0xcff)) return 14; // Kannada
  if (inRange(c, 0xd00, 0xd7f)) return 15; // Malayalam
  if (inRange(c, 0xe01, 0xe45)) return 16; // Thai letters only
  if (inRange(c, 0x10d0, 0x10ff)) return 17; // Georgian
  if (inRange(c, 0x1100, 0x11ff) || inRange(c, 0x3130, 0x318f) || inRange(c, 0xac00, 0xd7af)) return 18; // Hangul
  if (inRange(c, 0x3041, 0x3096)) return 101; // Hiragana
  if (inRange(c, 0x30a1, 0x30fa)) return 102; // Katakana
  if (inRange(c, 0x4e00, 0x9fff) || inRange(c, 0x3400, 0x4dbf)) return 103; // Han
  return 0;
}

/** The script id owning a script-specific combining mark; 0 for none. */
function markScriptOf(c: number): number {
  if (inRange(c, 0x591, 0x5c7)) return 5;
  if (inRange(c, 0x610, 0x61a) || inRange(c, 0x64b, 0x65f) || c === 0x670 || inRange(c, 0x6d6, 0x6ed)) return 6;
  if (inRange(c, 0x900, 0x97f)) return 7;
  if (inRange(c, 0x980, 0x9ff)) return 8;
  if (inRange(c, 0xa00, 0xa7f)) return 9;
  if (inRange(c, 0xa80, 0xaff)) return 10;
  if (inRange(c, 0xb00, 0xb7f)) return 11;
  if (inRange(c, 0xb80, 0xbff)) return 12;
  if (inRange(c, 0xc00, 0xc7f)) return 13;
  if (inRange(c, 0xc80, 0xcff)) return 14;
  if (inRange(c, 0xd00, 0xd7f)) return 15;
  if (c === 0xe31 || inRange(c, 0xe34, 0xe3a) || inRange(c, 0xe47, 0xe4e)) return 16; // Thai
  return 0;
}

const MODIFIER_LETTER = /^\p{Lm}$/u;
const LETTER = /^\p{L}$/u;
const MARK = /^\p{M}$/u;

/** The API's per-character rules over an already NFC-normalized, trimmed, collapsed name. */
function passesFormNameRules(name: string): boolean {
  let script = 0;
  let lastLetter = 0;
  let afterLetter = false;
  let first = true;
  let markRun = 0;
  for (const ch of name) {
    const v = ch.codePointAt(0) as number;
    if (v === 0x3164 || v === 0x115f || v === 0x1160 || v === 0xffa0) return false; // blank-looking Hangul fillers
    if (v === 0x30fc && !first && script === JAPANESE_GROUP) {
      afterLetter = false;
      markRun = 0;
      continue;
    }
    if (MODIFIER_LETTER.test(ch)) return false;
    if (LETTER.test(ch)) {
      const id = scriptOf(v);
      if (id === 0) return false;
      if (script === 0) script = id;
      else if (script !== id && !(isJapanese(script) && isJapanese(id))) return false;
      if (isJapanese(id)) script = JAPANESE_GROUP;
      lastLetter = id;
      afterLetter = true;
      markRun = 0;
    } else if (MARK.test(ch)) {
      if (!afterLetter) return false;
      const generic = inRange(v, 0x300, 0x36f) && v !== 0x34f && v !== 0x337 && v !== 0x338;
      if (!generic && markScriptOf(v) !== lastLetter) return false;
      if (++markRun > (lastLetter === 5 || lastLetter === 6 ? 3 : 2)) return false;
    } else if (v === 0x20 || v === 0x27 || v === 0x2019 || v === 0x2d) {
      if (first) return false;
      afterLetter = false;
      markRun = 0;
    } else {
      return false;
    }
    first = false;
  }
  return script !== 0;
}

/**
 * The optional first name a body claims, a port of the API's MarketingName.CleanFormName (the API is
 * authoritative; this pre-filter must refuse at least what the API refuses): NFC-normalized, inner
 * whitespace collapsed, trimmed, at most 40 characters, starting with a letter, letters in one allowed
 * script (explicit block ranges), only space, apostrophe, hyphen and combining marks besides. Anything
 * else is dropped (undefined), so a bad name never fails the subscribe.
 */
export function cleanFirstName(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  const name = value.normalize('NFC').replace(/\s+/g, ' ').trim();
  if (!name || name.length > FIRST_NAME_MAX_LENGTH || !FIRST_NAME_ALLOWED.test(name)) return undefined;
  return passesFormNameRules(name) ? name : undefined;
}

/** The contact form's name for the newsletter: only ever read when the opt-in is ticked. */
export function contactOptInFirstName(payload: { marketingOptIn?: unknown; firstName?: unknown }): string | undefined {
  return payload.marketingOptIn === true ? cleanFirstName(payload.firstName) : undefined;
}

/**
 * The `locale` a request body claims, kept only if it is one of the registered codes. Anything else
 * (absent, wrong type, unknown, a crafted string) is dropped, never forwarded.
 */
export function allowedLocale(value: unknown, allowed: readonly string[] | undefined): string | undefined {
  return typeof value === 'string' && allowed !== undefined && allowed.includes(value) ? value : undefined;
}

/** Trusted-forward headers for the API (contract C3). Empty when no token is configured. */
export function trustedForwardHeaders(siteToken?: string, clientIp?: string): Record<string, string> {
  const token = siteToken?.trim();
  if (!token) return {};
  const headers: Record<string, string> = { [SITE_TOKEN_HEADER]: token };
  const ip = clientIp?.trim();
  if (ip) headers[CLIENT_IP_HEADER] = ip;
  return headers;
}

export function subscribeUrl(apiUrl: string): string {
  return `${apiUrl.replace(/\/+$/, '')}/api/v1/marketing/subscribe`;
}

export async function forwardSubscribe(
  email: string,
  source: SubscribeSource,
  apiUrl: string,
  deps: ForwardSubscribeDeps,
): Promise<void> {
  const doFetch = deps.fetch ?? fetch;
  const log = deps.log ?? ((message: string, detail?: unknown) => console.error(message, detail ?? ''));
  try {
    const res = await doFetch(subscribeUrl(apiUrl), {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...trustedForwardHeaders(deps.siteToken, deps.clientIp) },
      body: JSON.stringify({
        email: email.trim(),
        wordingVersion: deps.wordingVersion,
        source,
        ...(deps.locale ? { locale: deps.locale } : {}),
        ...(cleanFirstName(deps.firstName) ? { firstName: cleanFirstName(deps.firstName) } : {}),
      }),
      signal: AbortSignal.timeout(deps.timeoutMs ?? SUBSCRIBE_TIMEOUT_MS),
    });
    if (!res.ok) log('[contact] newsletter subscribe responded with', res.status);
  } catch (err) {
    log('[contact] newsletter subscribe failed', err instanceof Error ? err.name : 'unknown error');
  }
}

// ---------------------------------------------------------------------------------------------
// Footer newsletter (POST /api/subscribe, app/api/subscribe/route.ts and functions/api/subscribe.ts).
// Both runtimes call handleFooterSubscribe; the request checks are pure so they can be tested
// without a server.

export type FooterSubscribeCheck = { ok: true; email: string; firstName?: string } | { ok: false; status: 400; error: string };

/** Email rule of the contact form (lib/validation.ts), reused so the two cannot drift apart. */
export function isValidSubscribeEmail(email: string): boolean {
  // The other ContactInput fields are filled only to satisfy the type; their errors are ignored.
  return !validateContact({ name: 'Newsletter', email, topic: 'general', message: '' }).some(
    (e) => e.field === 'email',
  );
}

/**
 * Checks a footer subscribe body. Bot signals (honeypot, fill time) and every other failure come back
 * as a 400 with a short code; the caller never forwards anything for a failed check. `now` is ms since
 * the epoch.
 */
export function validateFooterSubscribe(body: unknown, now: number, wordingVersion: string): FooterSubscribeCheck {
  const fail = (error: string): FooterSubscribeCheck => ({ ok: false, status: 400, error });
  if (typeof body !== 'object' || body === null) return fail('invalid_request');
  const fields = body as Record<string, unknown>;

  // Same bot signals as the contact form (lib/bot-checks.ts). Rejected visibly here, unlike the
  // contact form's silent success, because the newsletter has no enquiry to hide behind.
  if (isHoneypotFilled(fields.website) || isTooFastSubmission(fields.startedAt, now)) return fail('rejected');

  if (fields.consent !== true) return fail('consent_required');
  if (fields.wordingVersion !== wordingVersion) return fail('stale_wording');

  const email = typeof fields.email === 'string' ? fields.email.trim() : '';
  if (!email || email.length > 254 || !isValidSubscribeEmail(email)) return fail('invalid_email');
  const firstName = cleanFirstName(fields.firstName);
  return { ok: true, email, ...(firstName ? { firstName } : {}) };
}

export type ForwardSubscribeFn = (
  email: string,
  source: SubscribeSource,
  apiUrl: string,
  deps: ForwardSubscribeDeps,
) => Promise<void>;

export interface FooterSubscribeInput {
  body: unknown;
  now: number;
  /** The registry's current wording version (see ForwardSubscribeDeps.wordingVersion). */
  wordingVersion: string;
  /** Public API origin. Unset: the address is accepted but nothing is forwarded (logged). */
  apiUrl?: string;
  siteToken?: string;
  clientIp?: string;
  /** The registered locale codes the body's optional `locale` is checked against. */
  locales?: readonly string[];
}

export interface FooterSubscribeOutcome {
  status: 202 | 400;
  payload: { status: 'checkEmail' } | { ok: false; error: string };
  /** The pending API call. The runtime hands it to after() or waitUntil; null when nothing is forwarded. */
  forwarded: Promise<void> | null;
}

/**
 * The footer subscribe request, minus the runtime plumbing. A valid request is always answered
 * 202 { status: "checkEmail" }: the confirmation mail is the API's job, so a real submit never claims
 * the address is subscribed. `forward` is injected so tests can observe the call without a network.
 */
export function handleFooterSubscribe(
  input: FooterSubscribeInput,
  forward: ForwardSubscribeFn = forwardSubscribe,
): FooterSubscribeOutcome {
  const check = validateFooterSubscribe(input.body, input.now, input.wordingVersion);
  if (!check.ok) return { status: check.status, payload: { ok: false, error: check.error }, forwarded: null };

  const accepted = { status: 202 as const, payload: { status: 'checkEmail' as const } };
  if (!input.apiUrl) {
    console.info('[subscribe] newsletter opt-in not forwarded: REELBOLT_API_URL is not set');
    return { ...accepted, forwarded: null };
  }
  const forwarded = forward(check.email, 'siteFooter', input.apiUrl, {
    wordingVersion: input.wordingVersion,
    siteToken: input.siteToken,
    clientIp: input.clientIp,
    locale: allowedLocale((input.body as Record<string, unknown>).locale, input.locales),
    firstName: check.firstName,
  });
  return { ...accepted, forwarded };
}
