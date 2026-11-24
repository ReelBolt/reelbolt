/**
 * R01.4 + R03.7: a corpus over every refusal string a person can see. Every `EditEngineError(...)` message
 * the engine can raise is lifted from its source, its `${...}` holes are filled with ids and numbers, and the
 * result goes through the same mapper the toast uses. Nothing may come out holding a GUID, an op name or a
 * "track-<id>" key.
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { refusalSentence } from '../refusal';
import { doc, media, track } from './fixtures';
import type { EditTimeline } from '../types';

const GUID_A = '5f0c2b1e-0d7a-4c55-9a51-1b2c3d4e5f60';
const GUID_B = '9a8b7c6d-1111-4222-8333-444455556666';
const GUID_C = 'c0ffee00-aaaa-4bbb-8ccc-0123456789ab';

const OP_IDS = [
  'addMarker', 'addTrack', 'addTransition', 'append', 'blade', 'closeGaps', 'deleteGap', 'extract', 'fitToFill', 'insert', 'join', 'lift', 'move',
  'moveKeyframe', 'moveTrack', 'nudge', 'overwrite', 'placeOnTop', 'removeKeyframe', 'removeMarker', 'removeTrack', 'removeTransition', 'replace',
  'retime', 'setBus', 'setColorGroup', 'setInOut', 'setKeyframe', 'setMarker', 'setProperty', 'setSequence', 'setTimelineSettings', 'setTrackProperty', 'trim',
];
// A single-word op id (move, trim) is also plain English, so it only counts as a leak in its "op: " prefix form;
// every camelCase id counts anywhere.
const OP_WORD = new RegExp(`\\b(?:${OP_IDS.filter((o) => /[A-Z]/.test(o)).join('|')})\\b|\\b(?:${OP_IDS.join('|')}):`);
const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

const fixture = (): EditTimeline => {
  const d = doc([track(GUID_A, 'video', [media(GUID_B, 0, 60)])]);
  d.tracks[0].name = 'Interviews';
  (d.tracks[0].items[0] as { name: string }).name = 'Opening shot';
  return d;
};

const HOLE = '\u0000';

/** Every message passed to `new EditEngineError(` in the engine, as a template with holes. */
function engineMessages(): string[] {
  const src = readFileSync(join(__dirname, '../engine/apply.ts'), 'utf8');
  const out: string[] = [];
  const start = /new EditEngineError\(\s*([`'"])/g;
  let m: RegExpExecArray | null;
  while ((m = start.exec(src)) !== null) {
    const quote = m[1];
    let i = start.lastIndex;
    let text = '';
    let depth = 0;
    for (; i < src.length; i++) {
      const c = src[i];
      if (c === '\\') { text += src[i + 1]; i++; continue; }
      if (c === '$' && src[i + 1] === '{' && (quote === '`' || depth > 0)) { if (depth === 0) text += HOLE; depth++; i++; continue; }
      if (depth > 0) { if (c === '}') depth--; else if (c === '{') depth++; continue; }
      if (c === quote) break;
      text += c;
    }
    out.push(text);
  }
  return out;
}

const holes = [GUID_A, GUID_B, GUID_C, '7', 'video'];
const fill = (template: string, pick: number): string => {
  let n = pick;
  return template.replace(/\u0000/g, () => holes[n++ % holes.length]);
};

describe('R01.4 refusal corpus', () => {
  const messages = engineMessages();

  it('finds the engine messages (guards the scanner)', () => {
    expect(messages.length).toBeGreaterThan(100);
  });

  it('no refusal a person can see carries a GUID, an op name or a track key', () => {
    const offenders: string[] = [];
    for (const template of messages) {
      for (let pick = 0; pick < holes.length; pick++) {
        const filled = fill(template, pick);
        for (const withOp of [undefined, ...OP_IDS]) {
          const raw = withOp ? `${withOp}: ${filled}` : filled;
          const text = refusalSentence(new Error(raw), fixture());
          if (GUID.test(text) || OP_WORD.test(text) || /\btrack-[0-9a-z]/i.test(text) || /object Object|[{}]/.test(text)) {
            offenders.push(`${raw}  =>  ${text}`);
          }
        }
      }
    }
    expect(offenders.slice(0, 10)).toEqual([]);
  });

  it('a locked track names the track from the document', () => {
    const text = refusalSentence(new Error(`nudge: track '${GUID_A}' is locked`), fixture());
    expect(text).toContain('“Interviews”');
  });
});
