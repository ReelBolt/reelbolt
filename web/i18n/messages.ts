import { readdirSync, readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { DEFAULT_LOCALE, PSEUDO_LOCALE, isLocale, pseudoEnabled } from './config';
import { pseudoize } from './pseudo';

/**
 * Message loader. A catalogue is `messages/<locale>/<namespace>.json`; the file name is the top-level
 * key, so `messages/en/files.json` => `{ files: {...} }` and `useT('files.manager')` reads
 * `files.manager.*`. Files whose name starts with `_` (`_flags.json`) are review metadata, not messages.
 * English is the fallback for any key a locale lacks, so a half-translated language never shows a raw key.
 */
export type Messages = { [key: string]: string | Messages };

const ROOT = path.join(process.cwd(), 'messages');
const cache = new Map<string, Messages>();

function readNamespaces(locale: string): Messages {
  const dir = path.join(ROOT, locale);
  if (!existsSync(dir)) return {};
  const out: Messages = {};
  for (const file of readdirSync(dir).sort()) {
    if (!file.endsWith('.json') || file.startsWith('_')) continue;
    out[file.slice(0, -'.json'.length)] = JSON.parse(readFileSync(path.join(dir, file), 'utf8')) as Messages;
  }
  return out;
}

function deepMerge(base: Messages, over: Messages): Messages {
  const out: Messages = { ...base };
  for (const [key, value] of Object.entries(over)) {
    const current = out[key];
    out[key] =
      typeof value === 'object' && value !== null && typeof current === 'object' && current !== null
        ? deepMerge(current, value)
        : value;
  }
  return out;
}

/** Every namespace of `locale`, English filled in underneath. `locale` MUST already be allowlisted. */
export function loadMessages(locale: string): Messages {
  if (!isLocale(locale)) throw new Error('loadMessages: locale is not in the registry');
  const hit = process.env.NODE_ENV === 'production' ? cache.get(locale) : undefined;
  if (hit) return hit;
  const english = readNamespaces(DEFAULT_LOCALE);
  let merged: Messages;
  if (locale === PSEUDO_LOCALE && pseudoEnabled()) merged = pseudoize(english);
  else if (locale === DEFAULT_LOCALE) merged = english;
  else merged = deepMerge(english, readNamespaces(locale));
  cache.set(locale, merged);
  return merged;
}

/**
 * The hook point for per-route-group splitting: a layout passes only the catalogue files its
 * subtree needs (`messagesFor(locale, ['common', 'files'])`) to its own `IntlProvider`, so the rest
 * is never serialised into that page's payload. A namespace may be a dotted path (`files.manager`);
 * only its file (`files`) is selected. Omit `namespaces` for everything.
 */
export function messagesFor(locale: string, namespaces?: readonly string[]): Messages {
  const all = loadMessages(locale);
  if (!namespaces) return all;
  const files = new Set(namespaces.map((n) => n.split('.')[0]));
  return Object.fromEntries(Object.entries(all).filter(([file]) => files.has(file)));
}
