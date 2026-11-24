import IntlMessageFormat from 'intl-messageformat';
import { isMsg, type Msg } from '@/i18n/msg';
import type { T, Values } from '@/i18n/translator';
import assistant from '../messages/en/assistant.json';
import errors from '../messages/en/errors.json';
import failures from '../messages/en/failures.json';
import libLabels from '../messages/en/libLabels.json';

/**
 * English translator for non-component helpers that take an OPTIONAL translator: callers that do not
 * pass one (older call sites, tests, server utilities) keep getting the English sentence. Keys are
 * absolute (`errors.generic.network`) like `msg()` keys. Components should pass `useT()` instead.
 */
type Tree = { [key: string]: string | Tree };
const ROOT: Tree = { assistant, errors, failures, libLabels } as unknown as Tree;
const compiled = new Map<string, IntlMessageFormat>();

function lookup(key: string): string | Tree | undefined {
  let node: string | Tree | undefined = ROOT;
  for (const part of key.split('.')) {
    if (typeof node !== 'object' || node === null) return undefined;
    node = node[part];
  }
  return node;
}

function format(key: string, values?: Values): string {
  const raw = lookup(key);
  if (typeof raw !== 'string') return key;
  let fmt = compiled.get(key);
  if (!fmt) compiled.set(key, (fmt = new IntlMessageFormat(raw, 'en')));
  return String(fmt.format((values ?? {}) as Record<string, string | number | boolean | Date>));
}

function resolve(m: Msg): string {
  const values: Values = {};
  for (const [k, v] of Object.entries(m.values ?? {})) values[k] = isMsg(v) ? resolve(v) : (v as Values[string]);
  return format(m.$msg, values);
}

export const enT: T = Object.assign((key: string, values?: Values) => format(key, values), {
  rich: (key: string, values?: Parameters<T['rich']>[1]) => format(key, values as Values | undefined),
  has: (key: string) => typeof lookup(key) === 'string',
  raw: (key: string) => lookup(key),
  msg: resolve,
}) as T;

/** Catalogue key segment for a table key: `File Processing` -> `FileProcessing`. */
export function labelSlug(key: string): string {
  return key.replace(/[^A-Za-z0-9]+(.)?/g, (_, c: string | undefined) => (c ? c.toUpperCase() : ''));
}

/** English `Record<K, string>` for a label table kept in `libLabels.<prefix>` (old exports stay English). */
export function enLabels<K extends string>(prefix: string, keys: readonly K[]): Record<K, string> {
  const out = {} as Record<K, string>;
  for (const k of keys) out[k] = format(`libLabels.${prefix}.${labelSlug(k)}`);
  return out;
}

/** `t(libLabels.<prefix>.<key>)` when the catalogue has it, else `fallback ?? key` (unknown values stay readable). */
export function labelOf(t: T, prefix: string, key: string | null | undefined, fallback?: string): string {
  if (key) {
    const full = `libLabels.${prefix}.${labelSlug(key)}`;
    if (t.has(full)) return t(full);
  }
  return fallback ?? key ?? '';
}
