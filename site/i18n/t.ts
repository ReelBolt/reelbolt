import type { ReactNode } from 'react';
import type { Msg, MsgValue } from './msg';

type Values = Record<string, MsgValue>;
type RichValues = Record<string, MsgValue | ((chunks: ReactNode) => ReactNode)>;

/** The translator both `useT` and `getT` return (same shape in the site and the dashboard). */
export interface T {
  (key: string, values?: Values): string;
  rich(key: string, values: RichValues): ReactNode;
  has(key: string): boolean;
  raw(key: string): unknown;
  /** Resolves a `Msg` produced by non-component code (full `<namespace>.<path>` key). */
  msg(m: Msg): string;
}

/** Minimal surface of a next-intl translator that we rely on. */
export interface Base {
  (key: string, values?: never): string;
  rich(key: string, values?: never): ReactNode;
  has(key: string): boolean;
  raw(key: string): unknown;
}

export function decorate(t: Base, root: Base): T {
  const fn = ((key: string, values?: Values) => t(key, values as never)) as T;
  fn.rich = (key, values) => t.rich(key, values as never);
  fn.has = (key) => t.has(key);
  fn.raw = (key) => t.raw(key);
  fn.msg = (m) => root(m.key, m.values as never);
  return fn;
}
