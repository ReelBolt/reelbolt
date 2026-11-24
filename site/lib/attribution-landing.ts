// The landing of the current document, held in MEMORY only (module scope).
//
// Why it exists: the campaign parameters and the referrer are only on the first URL a visitor
// opens. Nothing may be written to the device before the visitor accepts cookies, so until then the
// landing lives here and nowhere else: no cookie, no localStorage, no sessionStorage. An Accept
// later in the same page life, or after any number of client-side navigations, therefore still
// records the real landing. A full document load starts again from that document's own URL, which
// is the price of storing nothing.
//
// The same memory also holds the audience ("persona") a signup link should carry. It is a product
// setting carried in a link, never stored. The rule, in order:
//
//  1. The link's own `?for=` (a tracked link adds it; the admin may set it independently of the
//     page the link points at). It beats the page it lands on, and it beats the page's own calls to
//     action, for as long as the visitor has not opened a DIFFERENT audience page.
//  2. Otherwise the audience page the visitor opened last.
//
// So `/for/creators?for=agency` carries `agency` on every signup link of that page and of the
// pages opened after it; opening `/for/educators` hands over to `educator`, and from then on the
// pages decide.
//
// The landing PATH is this document's own path, with one exception: a document opened from another
// page of this site through a link that carries the visit (`?lp=<path>` next to a campaign
// parameter: the language switcher, or a link from a translated page to an English-only one; see
// lib/attribution-signup-link.ts). That document is not where the visit began, and the link says
// where it did. `lp` is believed only when all three hold: the referrer is this host (so it was
// one of our own links, not something typed into a posted campaign link), the URL has a campaign
// parameter, and the value is a plain path. Otherwise the landing path is the document's own.
//
// Pure apart from the module-scope singleton at the bottom: no React, no DOM, no storage. The
// browser glue that reads `window.location` is components/analytics/landing.ts.
import { parseAttribution, sanitizeLandingPath, type Attribution } from './attribution.ts';
import { personaForPath, sanitizePersona, type LandingPersona } from './attribution-persona.ts';

export interface LandingInput {
  /** `window.location.search` of the first document. */
  search: string;
  /** `document.referrer`. */
  referrer: string;
  /** `window.location.pathname`, language prefix included (it is the landing path that is recorded). */
  pathname: string;
  /** The same path without the language prefix; decides which audience page this is. Defaults to `pathname`. */
  basePath?: string;
  ownHost?: string;
}

export interface LandingMemory {
  /** Records the landing. The FIRST call wins; every later call returns that first landing untouched. */
  remember(input: LandingInput): Attribution;
  /** The remembered landing, or null when nothing was remembered yet. */
  landing(): Attribution | null;
  /**
   * The path of the landing page WITHOUT the language prefix and without a trailing slash, or null
   * when nothing was remembered yet. It is the page the visitor saw first in this document, whatever
   * they have opened since. Always this document's own page, also when `landing().landingPath`
   * was carried in from an earlier document.
   */
  landingPage(): string | null;
  /** The audience remembered for the visit, if any: rule 1, else rule 2 above. */
  persona(): LandingPersona | undefined;
  /**
   * The audience ONE signup link should carry. `own` is the audience of the page or section the
   * link sits in. The link the visitor arrived through wins while it holds (rule 1); otherwise the
   * link's own audience, otherwise the remembered one.
   */
  personaFor(own?: LandingPersona): LandingPersona | undefined;
  /**
   * A page was opened (the landing page itself, or any page reached by client-side navigation);
   * `basePath` is its path without the language prefix. An audience page replaces the remembered
   * audience, with one exception: the landing page does not replace the audience its own link
   * named. A page that belongs to no audience changes nothing.
   */
  notePage(basePath: string | null | undefined): void;
  /** Called whenever what `persona()` or `personaFor()` answers changes. Returns the unsubscribe function. */
  subscribe(listener: () => void): () => void;
}

/** The query parameter a link of ours uses to say where the visit began (lib/attribution-signup-link.ts). */
const LANDING_PARAM = 'lp';

function isOwnReferrer(referrer: string, ownHost: string | undefined): boolean {
  if (!referrer || !ownHost) return false;
  try {
    return new URL(referrer).hostname.toLowerCase() === ownHost.trim().toLowerCase();
  } catch {
    return false;
  }
}

/** The landing path a link of ours carried into this document, or undefined (see the header). */
function carriedLandingPath(input: LandingInput, parsed: Attribution): string | undefined {
  const campaign = parsed.source ?? parsed.medium ?? parsed.campaign ?? parsed.content ?? parsed.term;
  if (campaign === undefined || !isOwnReferrer(input.referrer, input.ownHost)) return undefined;
  return sanitizeLandingPath(new URLSearchParams(input.search).get(LANDING_PARAM));
}

const stripTrailingSlash = (path: string) => (path.length > 1 ? path.replace(/\/+$/, '') : path);
const pagePath = (path: string) => stripTrailingSlash(path.split(/[?#]/)[0]);

export function createLandingMemory(): LandingMemory {
  let landing: Attribution | null = null;
  let landingPage: string | null = null;
  let persona: LandingPersona | undefined;
  // True while `persona` is the one the arrival link named and no other audience page was opened.
  let linkHolds = false;
  const listeners = new Set<() => void>();

  return {
    remember(input) {
      if (landing === null) {
        landing = parseAttribution(input);
        const carried = carriedLandingPath(input, landing);
        if (carried) landing.landingPath = carried;
        landingPage = pagePath(input.basePath ?? input.pathname);
        const fromLink = sanitizePersona(new URLSearchParams(input.search).get('for'));
        linkHolds = fromLink !== undefined;
        persona = fromLink ?? personaForPath(landingPage);
      }
      return landing;
    },
    landing: () => landing,
    landingPage: () => landingPage,
    persona: () => persona,
    personaFor: (own) => (linkHolds ? persona : (own ?? persona)),
    notePage(basePath) {
      if (!basePath) return;
      const page = pagePath(basePath);
      const next = personaForPath(page);
      if (!next) return;
      // The landing page itself: the link that led here already said whom it is for.
      if (linkHolds && page === landingPage) return;
      if (!linkHolds && next === persona) return;
      linkHolds = false;
      persona = next;
      listeners.forEach((listener) => listener());
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
  };
}

/** The one memory of this page life. A new document (full load) gets a new, empty one. */
export const landingMemory: LandingMemory = createLandingMemory();
