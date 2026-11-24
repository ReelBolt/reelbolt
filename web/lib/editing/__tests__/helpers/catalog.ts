import { expect } from 'vitest';
import en from '@/messages/en/editor-lib.json';
import it_ from '@/messages/it/editor-lib.json';
import type { Msg } from '@/i18n/msg';

const lookup = (cat: unknown, key: string): unknown => key.replace(/^editor-lib\./, '').split('.').reduce<unknown>((o, k) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[k] : undefined), cat);

/** The Msg (and any nested Msg value) resolves to a string in BOTH editor-lib catalogues. */
export function ok(m: Msg | null | undefined): void {
  expect(m).toBeTruthy();
  if (!m) return;
  expect(typeof lookup(en, m.$msg), `en ${m.$msg}`).toBe('string');
  expect(typeof lookup(it_, m.$msg), `it ${m.$msg}`).toBe('string');
  for (const v of Object.values(m.values ?? {})) if (typeof v === 'object' && v !== null && '$msg' in v) ok(v as Msg);
}
