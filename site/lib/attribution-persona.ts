// The audience ("persona") closed set, shared with the app, the API and BI
// (docs/design/new-user-funnel.md sections 2 and 4). Pure: no React, no storage.
//
// The site only ever carries a persona that has a landing page. `other` exists so the type matches
// the shared vocabulary, but it is chosen in the app only: the site never puts it in a link.

export const PERSONAS = ['creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other'] as const;
export type Persona = (typeof PERSONAS)[number];

/** A persona with a landing page: what `?for=` on a signup link may carry. */
export type LandingPersona = Exclude<Persona, 'other'>;

/** Persona token -> the slug of its landing page, `/for/<slug>`. */
export const PERSONA_LANDING_SLUGS: Readonly<Record<LandingPersona, string>> = {
  creator: 'creators',
  business: 'small-business',
  educator: 'educators',
  nonprofit: 'nonprofits',
  software: 'software-teams',
  agency: 'agencies',
};

export const LANDING_PERSONAS = Object.keys(PERSONA_LANDING_SLUGS) as LandingPersona[];

const SLUG_TO_PERSONA = new Map<string, LandingPersona>(
  LANDING_PERSONAS.map((persona) => [PERSONA_LANDING_SLUGS[persona], persona]),
);

/** `/for/creators` for `creator`. */
export function personaLandingPath(persona: LandingPersona): string {
  return `/for/${PERSONA_LANDING_SLUGS[persona]}`;
}

/**
 * A persona the site may carry: trimmed and lowercased first, then it must be one of the six with a
 * landing page. Anything else (including `other`) is dropped, never rejected.
 */
export function sanitizePersona(value: string | null | undefined): LandingPersona | undefined {
  if (!value) return undefined;
  const token = value.trim().toLowerCase();
  return (LANDING_PERSONAS as string[]).includes(token) ? (token as LandingPersona) : undefined;
}

/** The persona of a landing-page slug (`small-business` -> `business`), or undefined. */
export function personaForSlug(slug: string | null | undefined): LandingPersona | undefined {
  return slug ? SLUG_TO_PERSONA.get(slug) : undefined;
}

/**
 * The persona a page belongs to, from its path WITHOUT the language prefix: `/for/<slug>` for a
 * known slug, with or without a trailing slash. Every other path belongs to no persona.
 */
export function personaForPath(basePath: string | null | undefined): LandingPersona | undefined {
  if (!basePath) return undefined;
  const m = /^\/for\/([a-z0-9-]+)\/?$/.exec(basePath.split(/[?#]/)[0]);
  return m ? personaForSlug(m[1]) : undefined;
}
