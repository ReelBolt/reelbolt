// The `/` language decision, kept apart from _middleware.ts so node --test can drive it (the
// middleware also imports the registry JSON, which plain node cannot load without an attribute).
import picker from './_pick-locale.js';
import { PERSONA_LANDING_SLUGS, sanitizePersona } from '../lib/attribution-persona.ts';

export const VARY = 'Accept-Language, Cookie';

/**
 * GET / opens the visitor's language: 302 to /<code> (query kept) for a non-default pick, else the
 * page itself with Vary. Any other method or path, and every deep link, is passed through untouched,
 * so /it/... can never redirect and loops are impossible.
 */
export async function homeResponse(
  request: Request,
  codes: string[],
  next: () => Promise<Response>,
): Promise<Response> {
  const url = new URL(request.url);
  if (request.method !== 'GET' || url.pathname !== '/') return next();

  const picked: string = picker.pickLocale(request.headers.get('cookie'), request.headers.get('accept-language'), codes);
  const prefix = picked !== codes[0] ? `/${picked}` : '';
  // `/?for=creator&utm_...` links exist in the wild: the audience page is /for/<slug>. Invalid
  // values (and `other`) are ignored. The whole query rides along, `for` included.
  const persona = sanitizePersona(url.searchParams.get('for'));
  const path = persona ? `${prefix}/for/${PERSONA_LANDING_SLUGS[persona]}` : prefix;
  if (path !== '') {
    // 302, never 301: the answer depends on the visitor. Campaign parameters ride along.
    return new Response(null, {
      status: 302,
      headers: { Location: `${path}${url.search}`, 'Cache-Control': 'private, no-store', Vary: VARY },
    });
  }
  // The English page itself: unchanged, but a cache must not hand it to someone who should be redirected.
  const response = await next();
  const out = new Response(response.body, response);
  out.headers.append('Vary', VARY);
  return out;
}
