import { describe, expect, it } from 'vitest';
import en from '@/messages/en/editor-lib.json';
import it_ from '@/messages/it/editor-lib.json';
import { REASON_TABLES } from '../reason-tables';
import { sentenceMsg } from '../sentence';

const lookup = (cat: unknown, key: string): string | undefined => {
  const v = key.replace(/^editor-lib\./, '').split('.').reduce<unknown>((o, k) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[k] : undefined), cat);
  return typeof v === 'string' ? v : undefined;
};

describe('reason tables', () => {
  it('every entry points at a key present in both catalogues, with the same placeholders', () => {
    for (const [name, table] of Object.entries(REASON_TABLES)) {
      for (const entry of table as readonly (readonly unknown[])[]) {
        const [match, key, names] = entry as [string | RegExp, string, string[]?];
        const e = lookup(en, key);
        const i = lookup(it_, key);
        expect(e, `${name}: en ${key}`).toBeDefined();
        expect(i, `${name}: it ${key}`).toBeDefined();
        if (typeof match === 'string') expect(e, `${name}: ${key} must equal the lib sentence`).toBe(match);
        for (const n of names ?? []) { expect(e).toContain(`{${n}`); expect(i).toContain(`{${n}`); }
      }
    }
  });

  it('passes unknown text through unchanged', () => {
    const m = sentenceMsg(REASON_TABLES.transitions, 'A server sentence.');
    expect(m).toEqual({ $msg: 'editor-lib.common.raw', values: { text: 'A server sentence.' } });
  });
});
