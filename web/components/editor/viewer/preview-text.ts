import { msg, type Msg } from '@/i18n/msg';
import { reasonMsg } from '@/lib/editing/reason-msg';

/**
 * The preview libs return English sentences (the honesty badge, the viewer's session notes). This maps one to the
 * catalogue and resolves it with `tm` (a `useT().msg`): a whole sentence from the honesty, parity, session, readiness
 * and device tables; the `Simple preview: ...` / `Reduced quality: ...` wrappers with their inner reason translated
 * too; and `Clip "name": reason` lines (noun and reason translated, the name kept). Unknown text shows as it is.
 */
const TABLES = ['honesty', 'parity', 'previewSession', 'readiness', 'device'] as const;
const RAW = 'editor-lib.common.raw';
const NOUNS = 'Clip|Title|Caption|Generator|Insert|Adjustment|Transition|Compound clip|Multicam clip|Item|A clip';
const LINE = new RegExp(`^(${NOUNS})(?: "([^"]*)")?: ([\\s\\S]+)$`);
const SIMPLE = /^Simple preview: ([\s\S]+?) Your edits are still exact in the final render\.$/;
const REDUCED = /^Reduced quality: ([\s\S]+)$/;

function mapped(text: string): Msg | null {
  for (const table of TABLES) {
    const m = reasonMsg(table, text);
    if (m.$msg !== RAW) return m;
  }
  return null;
}

export function previewText(text: string, tm: (m: Msg) => string): string {
  const simple = SIMPLE.exec(text);
  if (simple) return tm(msg('editor-lib.honesty.simpleFallback', { reason: previewText(simple[1], tm) }));
  const reduced = REDUCED.exec(text);
  if (reduced) return tm(msg('editor-lib.honesty.reducedQuality', { reason: previewText(reduced[1], tm) }));
  const whole = mapped(text);
  if (whole) return tm(whole);
  const line = LINE.exec(text);
  if (line) {
    const noun = previewText(line[1], tm);
    return `${noun}${line[2] !== undefined ? ` "${line[2]}"` : ''}: ${previewText(line[3], tm)}`;
  }
  return text;
}
