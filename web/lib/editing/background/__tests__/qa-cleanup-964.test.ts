// @vitest-environment jsdom
/** QA probes (T-051, PR #964): audio cleanup adapter against T-015 and owner addendum rules 1-5. */
import { beforeEach, describe, expect, it } from 'vitest';
import { createEditorStore } from '../../store';
import { assetRef, doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditOp } from '../../engine';
import type { MediaClip } from '../../types';
import type { CleanupJob } from '../../cleanup/types';
import { entryForCleanup, isCleanupReady, landCleanupResult, settleCleanup } from '../audio-cleanup';
import { entriesAffectedBy } from '../edit-guard';
import { getResultsStore, resetResultsStores } from '../results-store';

const OUT = '00000000-0000-4000-8000-0000000000bb';
const job = (over: Partial<CleanupJob> = {}): CleanupJob => ({ executionId: 'e1', state: 'ready', target: 'Hum', applied: true, outputFileId: OUT, ...over });
const make = () => createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.a1, 'audio', [media('x', 0, 60, 0), media('o', 60, 120, 0, { assetRef: assetRef(2) })])]), revision: 1 });
type Store = ReturnType<typeof make>;
const clip = (s: Store, id: string) => s.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip | undefined;
function launched(s: Store) {
  const results = getResultsStore('t');
  const entry = entryForCleanup({ doc: s.getState().doc, clipId: 'x', executionId: 'e1', outputKind: 'Audio', now: () => 'now' });
  if (!entry) throw new Error('no entry');
  results.launch(entry);
  return { results, entry };
}
const only = (s: Store) => getResultsStore('t').getEntries()[0];

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('QA T-051: landing never applies silently and rebases', () => {
  it('trim: lands READY, nothing written', () => {
    const s = make();
    const { results, entry } = launched(s);
    s.dispatch({ type: 'trim', itemId: 'x', edge: 'in', mode: 'ripple', deltaFrames: 10 } as never);
    const cursor = s.getState().cursor;
    landCleanupResult({ results, editor: s, entry, job: job() });
    expect(s.getState().cursor).toBe(cursor);
    expect(isCleanupReady(only(s))).toBe(true);
    expect(only(s).review?.applicable).toEqual(['x']);
  });
  it('move: lands READY on the moved clip', () => {
    const s = make();
    const { results, entry } = launched(s);
    s.dispatch({ type: 'move', itemId: 'x', toTrackId: TRACKS.a1, toRecordIn: 200 } as never);
    landCleanupResult({ results, editor: s, entry, job: job() });
    expect(isCleanupReady(only(s))).toBe(true);
    settleCleanup({ results, editor: s, entry: only(s), autoApply: false });
    expect(clip(s, 'x')?.assetRef.projectFileId).toBe(OUT);
    expect(clip(s, 'o')?.assetRef).toEqual(assetRef(2));
  });
  it('asset swap: needs your OK (assetChanged), nothing applicable, nothing written', () => {
    const s = make();
    const { results, entry } = launched(s);
    s.dispatch({ type: 'setProperty', itemId: 'x', key: 'assetRef', value: assetRef(3) } as never);
    const cursor = s.getState().cursor;
    landCleanupResult({ results, editor: s, entry, job: job() });
    expect(only(s).state).toBe('needsReview');
    expect(only(s).review?.reasons).toContain('assetChanged');
    expect(only(s).review?.applicable).toEqual([]);
    expect(isCleanupReady(only(s))).toBe(false);
    settleCleanup({ results, editor: s, entry: only(s), autoApply: false });
    expect(s.getState().cursor).toBe(cursor);
    expect(only(s).state).toBe('needsReview');
    // The paid result is kept: the cleaned file id stays on the entry for a later apply.
    expect((only(s).intent as { outputFileId?: string }).outputFileId).toBe(OUT);
  });
});

describe('QA T-051: one undo step, a held batch is not applied', () => {
  it('guard holds -> needsReview, cursor unchanged; "Go on" re-dispatch of the same ops -> applied, +1 undo step', () => {
    const s = make();
    const { results, entry } = launched(s);
    s.dispatchBatch([{ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'y' }] as EditOp[], 'Split');
    landCleanupResult({ results, editor: s, entry, job: job() });
    let held: { ops: readonly EditOp[]; label: string } | null = null;
    s.setEditGuard((ops, _d, label) => {
      held = { ops, label };
      return false;
    });
    const cursor = s.getState().cursor;
    settleCleanup({ results, editor: s, entry: only(s), autoApply: false });
    expect(held).not.toBeNull();
    expect(only(s).state).toBe('needsReview');
    expect(s.getState().cursor).toBe(cursor);
    expect(clip(s, 'x')?.assetRef).toEqual(assetRef(1));
    s.setEditGuard(null);
    const h = held as unknown as { ops: readonly EditOp[]; label: string };
    expect(s.dispatchBatch(h.ops, h.label)).toBe(true);
    expect(only(s).state).toBe('applied');
    expect(s.getState().cursor).toBe(cursor + 1);
    expect(clip(s, 'x')?.assetRef.projectFileId).toBe(OUT);
    expect(clip(s, 'y')?.assetRef.projectFileId).toBe(OUT);
  });
});

describe('QA T-051: busy warning while cleanup runs', () => {
  it('trim/delete/move of the target and of a split piece are flagged; an unrelated clip is not', () => {
    const s = make();
    const { results } = launched(s);
    const running = () => results.getEntries();
    const d = () => s.getState().doc;
    expect(entriesAffectedBy([{ type: 'trim', itemId: 'x', edge: 'out', mode: 'ripple', deltaFrames: -5 }] as EditOp[], d(), running(), new Set())).toHaveLength(1);
    expect(entriesAffectedBy([{ type: 'lift', itemId: 'x' }] as EditOp[], d(), running(), new Set())).toHaveLength(1);
    expect(entriesAffectedBy([{ type: 'move', itemId: 'o', toTrackId: TRACKS.a1, toRecordIn: 300 }] as EditOp[], d(), running(), new Set())).toHaveLength(0);
    s.dispatchBatch([{ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'y' }] as EditOp[], 'Split');
    expect(entriesAffectedBy([{ type: 'lift', itemId: 'y' }] as EditOp[], d(), running(), new Set())).toHaveLength(1);
    // Busy mark: both pieces show it, the unrelated clip does not.
    expect([...results.busyClipIds(d())].sort()).toEqual(['x', 'y']);
  });
});
