// The audience ("persona") closed set, shared with the marketing site, the Go API and BI
// (docs/design/new-user-funnel.md sections 2 and 4). Pure: no React, no storage.
//
// A persona reaches the app in the signup link (`/app/signup?for=creator`). It is a product
// setting the person carries in their own link, so reading it needs no cookie consent; it shapes
// the signup page and travels in the signup request, where the API stores it as a suggestion.

export const PERSONAS = ['creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other'] as const;
export type Persona = (typeof PERSONAS)[number];

/** The query parameter that carries the persona, on the signup link and on the OAuth start endpoint. */
export const PERSONA_PARAM = 'for';

/**
 * A persona token: trimmed and lowercased first, then it must be one of the closed set. Anything
 * else is dropped, never rejected: a signup must not fail on marketing data.
 */
export function sanitizePersona(value: string | null | undefined): Persona | undefined {
  if (!value) return undefined;
  const token = value.trim().toLowerCase();
  return (PERSONAS as readonly string[]).includes(token) ? (token as Persona) : undefined;
}
