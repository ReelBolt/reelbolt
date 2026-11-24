import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { getRequestConfig } from 'next-intl/server';
import { defaultLocale, isLocale, type Locale } from './config';

type Tree = { [key: string]: Tree | string };

const cache = new Map<Locale, Tree>();

/** Deep merge: `over` wins; used so a missing key in a locale falls back to English, never to a blank. */
function merge(base: Tree, over: Tree): Tree {
  const out: Tree = { ...base };
  for (const [k, v] of Object.entries(over)) {
    const b = out[k];
    out[k] = typeof v === 'object' && v !== null && typeof b === 'object' && b !== null ? merge(b, v) : v;
  }
  return out;
}

function readLocale(locale: Locale): Tree {
  // `locale` was allowlisted against the registry by the caller; re-checked because this joins a path.
  if (!isLocale(locale)) return {};
  const dir = join(process.cwd(), 'messages', locale);
  let files: string[] = [];
  try {
    files = readdirSync(dir);
  } catch {
    return {};
  }
  const tree: Tree = {};
  for (const file of files.sort()) {
    // `_flags.json` and friends are translator notes, not catalogues.
    if (!file.endsWith('.json') || file.startsWith('_')) continue;
    tree[file.slice(0, -'.json'.length)] = JSON.parse(readFileSync(join(dir, file), 'utf8')) as Tree;
  }
  return tree;
}

/** All namespaces of a locale as `{ <file name>: <contents> }`, English filling any gap. */
export function loadMessages(locale: Locale): Tree {
  const hit = cache.get(locale);
  if (hit) return hit;
  const en = readLocale(defaultLocale);
  const merged = locale === defaultLocale ? en : merge(en, readLocale(locale));
  cache.set(locale, merged);
  return merged;
}

export default getRequestConfig(async ({ requestLocale }) => {
  // The locale comes from the route (setRequestLocale in each page/layout), never from a header:
  // the site is prerendered and the static export has no server to read one.
  const requested = await requestLocale;
  const locale = isLocale(requested) ? requested : defaultLocale;
  return {
    locale,
    messages: loadMessages(locale),
    timeZone: 'UTC',
    onError: (error) => {
      if (process.env.NODE_ENV !== 'production') console.error(error.message);
    },
    getMessageFallback: ({ namespace, key }) => (namespace ? `${namespace}.${key}` : key),
  };
});
