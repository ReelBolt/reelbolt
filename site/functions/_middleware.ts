// LEGACY, UNUSED: nothing deploys this Pages Function (reelbolt.ai is the `site` container behind nginx).
// Cloudflare Pages middleware. It acts on exactly one request, GET / (see _home.ts); _routes.json
// (public/) keeps this Function off static assets entirely. The pick is i18n/pick-locale.js, vendored
// as ./_pick-locale.js; nginx/i18n.js does the same for the self-hosted stack. A plain JSON import
// (no import attribute), as in function-wording.ts: the Pages bundler loads it natively.
import registry from '../i18n/locales.json';
import { homeResponse } from './_home';

// Default language first: the picker returns codes[0] when nothing else matches.
const CODES: string[] = [
  registry.defaultLocale,
  ...registry.locales.map((l) => l.code).filter((c) => c !== registry.defaultLocale),
];

interface Context {
  request: Request;
  next: () => Promise<Response>;
}

export const onRequest = ({ request, next }: Context): Promise<Response> => homeResponse(request, CODES, next);
