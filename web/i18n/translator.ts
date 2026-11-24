import type { ReactNode } from 'react';
import { isMsg, type Msg, type MsgValue } from './msg';

export type Values = Record<string, string | number | boolean | Date | null | undefined>;
export type RichValues = Record<string, string | number | boolean | Date | null | undefined | ((chunks: ReactNode) => ReactNode)>;

/** What `useT()` / `getT()` return. Keys are relative to the namespace; `msg()` keys are absolute. */
export interface T {
  (key: string, values?: Values): string;
  rich(key: string, values?: RichValues): ReactNode;
  has(key: string): boolean;
  raw(key: string): unknown;
  /** Resolves a `Msg` produced by non-component code (absolute key: `<namespace file>.<path>`). */
  msg(m: Msg): string;
}

/** The two next-intl translators a `T` is built from (scoped, and root for `msg`). */
export interface BaseTranslator {
  (key: string, values?: never): string;
  rich(key: string, values?: never): ReactNode;
  has(key: string): boolean;
  raw(key: string): unknown;
}

function resolveValues(values: Readonly<Record<string, MsgValue>> | undefined, root: (k: string, v?: Values) => string): Values | undefined {
  if (!values) return undefined;
  const out: Values = {};
  for (const [k, v] of Object.entries(values)) out[k] = isMsg(v) ? resolveMsg(v, root) : (v as Values[string]);
  return out;
}

function resolveMsg(m: Msg, root: (k: string, v?: Values) => string): string {
  return root(m.$msg, resolveValues(m.values, root));
}

export function buildT(scoped: BaseTranslator, root: BaseTranslator): T {
  const call = (key: string, values?: Values) => (scoped as unknown as (k: string, v?: Values) => string)(key, values);
  const rootCall = (key: string, values?: Values) => (root as unknown as (k: string, v?: Values) => string)(key, values);
  const t = ((key: string, values?: Values) => call(key, values)) as T;
  t.rich = (key, values) => (scoped.rich as (k: string, v?: RichValues) => ReactNode)(key, values);
  t.has = (key) => scoped.has(key);
  t.raw = (key) => scoped.raw(key);
  t.msg = (m) => resolveMsg(m, rootCall);
  return t;
}

const cache = new WeakMap<object, { root: object; t: T }>();

/** Stable per translator pair, so `t` is safe in dependency arrays. No React hooks: usable on the server. */
export function stableT(scoped: BaseTranslator, root: BaseTranslator): T {
  const hit = cache.get(scoped);
  if (hit && hit.root === root) return hit.t;
  const t = buildT(scoped, root);
  cache.set(scoped, { root, t });
  return t;
}
