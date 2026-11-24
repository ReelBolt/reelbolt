import { msg, type Msg } from '@/i18n/msg';

/** Any text we cannot map to a key shows as it is (a server message, an unknown type). */
export const rawMsg = (text: string): Msg => msg('editor-lib.common.raw', { text });

/** Maps an English sentence the lib returned (exact, or by pattern from `reason-tables.ts`) to its catalogue message; unknown text passes through. */
export type ReasonEntry = readonly [string | RegExp, string] | readonly [string | RegExp, string, readonly string[]];
export function sentenceMsg(table: readonly ReasonEntry[], text: string): Msg {
  for (const entry of table) {
    const [match, key] = entry;
    const names = entry.length === 3 ? entry[2] : [];
    if (typeof match === 'string') {
      if (match === text) return msg(key);
    } else {
      const m = match.exec(text);
      if (m) {
        const values: Record<string, string | number> = {};
        names.forEach((name, i) => {
          const raw = m[i + 1];
          values[name] = /^-?\d+(\.\d+)?$/.test(raw) ? Number(raw) : raw;
        });
        return msg(key, values);
      }
    }
  }
  return rawMsg(text);
}

