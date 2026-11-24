// Pages Function body for GET /recipes/<id> and /<locale>/recipes/<id>. The static export prerenders a detail
// page for every recipe that was listed at build time. A recipe listed AFTER the build has no file, so the
// asset lookup 404s; the logic in lib/recipes/shell.ts then answers with the client-rendered placeholder page
// (/recipes/_), which reads the id from the URL and fetches /api/recipes/<id> in the browser. The shell is
// `noindex`, also as a header: only the prebuilt pages are indexed.
import registry from '../i18n/locales.json';
import { recipeShellResponse, type ShellContext } from '../lib/recipes/shell';

export type { ShellContext };

const CODES: string[] = registry.locales.map((l) => l.code);

export const recipeShell = (context: ShellContext, locale: string | undefined) =>
  recipeShellResponse(context, locale, { defaultLocale: registry.defaultLocale, codes: CODES });
