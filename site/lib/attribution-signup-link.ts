// Carries what the visitor's own link said in the links that leave the current document: the
// audience (`?for=<persona>`), the campaign parameters of the landing (`utm_*`) and, next to a
// campaign parameter, the page of this site the visit began on (`lp=<path>`). Pure: no React,
// no storage, no DOM. The values travel in the URL the person clicks, so nothing is written to
// their device (docs/design/new-user-funnel.md sections 3 and 7).
//
// Two kinds of link get them:
//
//  - a link into the app that can create an account (`decorateEntryHref`): the signup page, and
//    the sign-in page, whose provider buttons create the account when none exists;
//  - a link to another page of this site that the browser opens as a NEW document
//    (`decorateCarryHref`): the language switcher, and a link from a translated page to an
//    English-only one. The landing is held in memory only (lib/attribution-landing.ts), and a new
//    document starts with an empty memory, so without the parameters in its URL the campaign and
//    the audience would end there for a visitor who has not accepted cookies.
//
// Rules, the same for both:
//  - Every other href comes back as it was.
//  - The existing query (`?plan=creator`) is kept byte for byte, and a parameter the href already
//    has is never overwritten, so decorating twice changes nothing.
//  - Every carried value goes through the same sanitizers as the first-touch cookie; an invalid
//    value is dropped. With nothing to carry the href comes back unchanged.
//  - The landing path (`lp`) is a path and nothing more: no query, no fragment, no host. It rides
//    only on a link that also has a campaign parameter, because on its own it would describe a
//    touch with no source behind it. The dashboard reads it under the same name and the same rule
//    (web/lib/auth/signup-carry.ts) and sends it as the landing page of the signup; the next
//    document of this site reads it back as the landing page of the visit
//    (lib/attribution-landing.ts). The referrer is never put in a link.
import { sanitizeLandingPath, sanitizeUtm, type Attribution } from './attribution.ts';
import { sanitizePersona } from './attribution-persona.ts';
import { isSitePagePath } from '../i18n/config.ts';
import { LOGIN_PATH, SIGNUP_PATH } from './site-config.ts';

/** The query parameter that carries the persona. */
export const PERSONA_PARAM = 'for';

/** The query parameter that names the page of this site the visit began on. A path, never a URL. */
export const LANDING_PARAM = 'lp';

/** Link parameter -> the landing field it carries, in the order they are appended. */
export const CAMPAIGN_PARAMS = [
  ['utm_source', 'source'],
  ['utm_medium', 'medium'],
  ['utm_campaign', 'campaign'],
  ['utm_content', 'content'],
  ['utm_term', 'term'],
] as const satisfies readonly (readonly [string, keyof Attribution])[];

export interface SignupCarry {
  /** The audience to suggest at signup. Dropped unless it is a persona with a landing page. */
  persona?: string | null;
  /** The landing attribution held in memory (lib/attribution-landing.ts). */
  landing?: Attribution | null;
}

const pathOf = (href: string) => href.split(/[?#]/)[0];
const isPath = (href: string, path: string) => pathOf(href) === path || pathOf(href) === `${path}/`;

/** True for `/app/signup`, with or without a query or fragment. */
export function isSignupHref(href: string): boolean {
  return isPath(href, SIGNUP_PATH);
}

/** True for the two app pages an account can be created from: `/app/signup` and `/app/login`. */
export function isEntryHref(href: string): boolean {
  return isSignupHref(href) || isPath(href, LOGIN_PATH);
}

/** True for a root-relative link to a page of this site (`/blog`, `/it/pricing?x=1`); see `isSitePagePath`. */
export function isSitePageHref(href: string): boolean {
  return isSitePagePath(href);
}

function appendCarry(href: string, carry: SignupCarry): string {
  const hashAt = href.indexOf('#');
  const hash = hashAt === -1 ? '' : href.slice(hashAt);
  const beforeHash = hashAt === -1 ? href : href.slice(0, hashAt);
  const queryAt = beforeHash.indexOf('?');
  const path = queryAt === -1 ? beforeHash : beforeHash.slice(0, queryAt);
  const query = queryAt === -1 ? '' : beforeHash.slice(queryAt + 1);
  const existing = new URLSearchParams(query);

  const additions: string[] = [];
  const add = (name: string, value: string | undefined) => {
    // encodeURIComponent matters for `+`, which a UTM value may contain and a query reads as a space.
    if (value && !existing.has(name)) additions.push(`${name}=${encodeURIComponent(value)}`);
  };
  add(PERSONA_PARAM, sanitizePersona(carry.persona));
  // True once the link has a campaign parameter: one it came with, or one appended here.
  let campaign = false;
  for (const [param, field] of CAMPAIGN_PARAMS) {
    const before = additions.length;
    add(param, sanitizeUtm(carry.landing?.[field]));
    campaign ||= existing.has(param) || additions.length > before;
  }
  // The landing page goes last and only next to a campaign. sanitizeLandingPath cuts a query or a
  // fragment off and refuses anything that is not a plain path.
  if (campaign) add(LANDING_PARAM, sanitizeLandingPath(carry.landing?.landingPath));

  if (additions.length === 0) return href;
  return `${path}?${query === '' ? '' : `${query}&`}${additions.join('&')}${hash}`;
}

/** A link to the signup or the sign-in page, with the audience, the campaign and its landing page appended. */
export function decorateEntryHref(href: string, carry: SignupCarry): string {
  return isEntryHref(href) ? appendCarry(href, carry) : href;
}

/**
 * A link to another page of this site that opens as a new document, with the audience, the
 * campaign and its landing page appended, so that document's own landing reads the same values
 * back (the landing page included: without it the new document would take itself for the landing).
 */
export function decorateCarryHref(href: string, carry: SignupCarry): string {
  return isSitePageHref(href) ? appendCarry(href, carry) : href;
}
