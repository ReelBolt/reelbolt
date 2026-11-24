// Shared helpers for the catalogue tests: read messages/<locale>/<namespace>.json and flatten them.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, TYPE, type MessageFormatElement } from '@formatjs/icu-messageformat-parser';

export const siteRoot = fileURLToPath(new URL('../', import.meta.url));
export const messagesRoot = join(siteRoot, 'messages');

export interface Registry {
  defaultLocale: string;
  locales: { code: string }[];
}
export const registry: Registry = JSON.parse(readFileSync(join(siteRoot, 'i18n/locales.json'), 'utf8'));
export const localeCodes = registry.locales.map((l) => l.code);

/** Namespace file names (without .json) of a locale; `_flags.json` and other `_` files are translator notes. */
export function namespacesOf(locale: string): string[] {
  const dir = join(messagesRoot, locale);
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((f) => f.endsWith('.json') && !f.startsWith('_'))
    .map((f) => f.slice(0, -'.json'.length))
    .sort();
}

export function readNamespace(locale: string, ns: string): unknown {
  return JSON.parse(readFileSync(join(messagesRoot, locale, `${ns}.json`), 'utf8'));
}

/** `{a:{b:'x'}, c:['y']}` -> `a.b => x`, `c[0] => y`. Non-string leaves are reported as `typeof`. */
export function flatten(value: unknown, prefix = ''): Map<string, unknown> {
  const out = new Map<string, unknown>();
  const walk = (v: unknown, path: string) => {
    if (Array.isArray(v)) v.forEach((item, i) => walk(item, `${path}[${i}]`));
    else if (v !== null && typeof v === 'object') {
      for (const [k, item] of Object.entries(v)) walk(item, path ? `${path}.${k}` : k);
    } else out.set(path, v);
  };
  walk(value, prefix);
  return out;
}

export function isDirectory(path: string): boolean {
  return existsSync(path) && statSync(path).isDirectory();
}

/**
 * The shape a translation must keep: which arguments it takes, of what kind, and which rich-text tags
 * it uses. Sorted, so two messages with the same shape compare equal whatever the word order.
 */
export function icuShape(message: string): string[] {
  const out = new Set<string>();
  const kind = (t: TYPE) =>
    ({ [TYPE.argument]: 'arg', [TYPE.number]: 'number', [TYPE.date]: 'date', [TYPE.time]: 'time', [TYPE.select]: 'select', [TYPE.plural]: 'plural' })[t as number];
  const walk = (els: MessageFormatElement[]) => {
    for (const el of els) {
      if (el.type === TYPE.literal || el.type === TYPE.pound) continue;
      if (el.type === TYPE.tag) {
        out.add(`<${el.value}>`);
        walk(el.children);
        continue;
      }
      out.add(`${el.value}:${kind(el.type)}`);
      if (el.type === TYPE.select || el.type === TYPE.plural) {
        for (const opt of Object.values(el.options)) walk(opt.value);
      }
    }
  };
  walk(parse(message));
  return [...out].sort();
}
