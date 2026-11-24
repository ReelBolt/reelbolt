/** @vitest-environment node */
/**
 * W0.6 (P5-GEN-03, UI2-03): "Add colour card" and "Add title" at the playhead create a valid item —
 * the engine accepts the program, the item lands on the topmost video track with room, and a new
 * video track is added only when no video track has room.
 */
import { describe, expect, it } from 'vitest';
import { addAtPlayhead } from '../add-at-playhead';
import { apply, type EditOp } from '../../engine';
import { standardDoc, TRACKS } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`;
const run = (doc: EditTimeline, ops: readonly EditOp[]) => ops.reduce((d, op) => apply(d, op).doc, doc);

describe('addAtPlayhead', () => {
  it('a colour card goes on the topmost free video track for three seconds', () => {
    const doc = standardDoc();
    const { ops, itemId } = addAtPlayhead(doc, 'colorCard', 100, newId);
    expect(ops.map((o) => o.type)).toEqual(['placeOnTop']);
    const next = run(doc, ops);
    const v2 = next.tracks.find((t) => t.id === TRACKS.v2)!;
    const card = v2.items.find((i) => i.id === itemId)!;
    expect(card).toMatchObject({ type: 'generatorClip', generatorType: 'color', recordIn: 100, recordOut: 190, generatorConfig: { color: '#000000' } });
  });

  it('a title goes on a new video track when every video track is busy at the playhead', () => {
    const doc = standardDoc();
    const { ops, itemId } = addAtPlayhead(doc, 'title', 50, newId);
    expect(ops.map((o) => o.type)).toEqual(['addTrack']);
    const next = run(doc, ops);
    expect(next.tracks).toHaveLength(5);
    const located = next.tracks.flatMap((t) => t.items.map((i) => ({ t, i }))).find(({ i }) => i.id === itemId)!;
    expect(located.t.kind).toBe('video');
    expect(located.i).toMatchObject({ type: 'titleClip', text: 'Title', recordIn: 50, recordOut: 140 });
    // Video tracks stay before audio tracks.
    expect(next.tracks.map((t) => t.kind)).toEqual(['video', 'video', 'video', 'audio', 'audio']);
  });

  it('a locked track is skipped', () => {
    const doc = standardDoc();
    doc.tracks[1] = { ...doc.tracks[1], locked: true };
    const { ops } = addAtPlayhead(doc, 'title', 100, newId);
    expect(ops[0].type).toBe('addTrack');
  });
});
