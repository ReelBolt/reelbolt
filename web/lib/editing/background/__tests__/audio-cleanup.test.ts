// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import { createEditorStore, undoStep } from '../../store';
import { assetRef, doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { MediaClip } from '../../types';
import type { CleanupJob } from '../../cleanup/types';
import { entryForCleanup, isCleanupReady, landCleanupResult, settleCleanup } from '../audio-cleanup';
import { getResultsStore, resetResultsStores } from '../results-store';

const OUT = '00000000-0000-4000-8000-0000000000bb';
const job = (over: Partial<CleanupJob> = {}): CleanupJob => ({ executionId: 'e1', state: 'ready', target: 'VoiceBasic', applied: true, outputFileId: OUT, ...over });

function make() {
  return createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0)])]), revision: 1 });
}
const clip = (store: ReturnType<typeof make>, id: string) => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip | undefined;
function launched(store: ReturnType<typeof make>) {
  const results = getResultsStore('t');
  const entry = entryForCleanup({ doc: store.getState().doc, clipId: 'x', executionId: 'e1', outputKind: 'Audio', now: () => 'now' });
  if (!entry) throw new Error('no entry');
  results.launch(entry);
  return { results, entry };
}

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('audio cleanup lands on the current document', () => {
  it('registers the run as a running audioCleanup entry covering the whole source', () => {
    const { entry } = launched(make());
    expect(entry.tool).toBe('audioCleanup');
    expect(entry.state).toBe('running');
    expect(entry.anchor.clipIds).toEqual(['x']);
    expect(entry.anchor.coverage.endSec).toBeNull();
  });
  it('a landed cleanup is READY, not applied; Apply then covers both split pieces in one undo step', async () => {
    const store = make();
    const { results, entry } = launched(store);
    store.dispatchBatch([{ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'y' }], 'Split');
    const before = store.getState().cursor;
    landCleanupResult({ results, editor: store, entry, job: job() });
    expect(store.getState().cursor).toBe(before);
    expect(clip(store, 'x')?.assetRef).toEqual(assetRef(1));
    const ready = results.getEntries()[0];
    expect(ready.state).toBe('needsReview');
    expect(isCleanupReady(ready)).toBe(true);
    settleCleanup({ results, editor: store, entry: ready, autoApply: false });
    expect(store.getState().cursor).toBe(before + 1);
    expect(clip(store, 'x')?.assetRef.projectFileId).toBe(OUT);
    expect(clip(store, 'y')?.assetRef.projectFileId).toBe(OUT);
    expect(results.getEntries()[0].state).toBe('applied');
    await undoStep(store);
    expect(clip(store, 'x')?.assetRef).toEqual(assetRef(1));
    expect(clip(store, 'y')?.assetRef).toEqual(assetRef(1));
  });
  it('Discard leaves the document alone', () => {
    const store = make();
    const { results, entry } = launched(store);
    landCleanupResult({ results, editor: store, entry, job: job() });
    const cursor = store.getState().cursor;
    results.resolve(entry.id, 'discarded');
    expect(results.getEntries()[0].state).toBe('discarded');
    expect(store.getState().cursor).toBe(cursor);
  });
  it('a deleted target needs the user OK instead of a silent no-op', () => {
    const store = make();
    const { results, entry } = launched(store);
    store.dispatch({ type: 'lift', itemId: 'x' } as never);
    landCleanupResult({ results, editor: store, entry, job: job() });
    const landed = results.getEntries()[0];
    expect(landed.state).toBe('needsReview');
    expect(landed.review?.reasons).toContain('targetGone');
  });
  it('read-only blocks Apply and parks the result', () => {
    const store = make();
    const { results, entry } = launched(store);
    landCleanupResult({ results, editor: store, entry, job: job() });
    store.setReadOnly(true);
    settleCleanup({ results, editor: store, entry: results.getEntries()[0], autoApply: false });
    expect(results.getEntries()[0].review?.blocked).toBe('readOnly');
    expect(clip(store, 'x')?.assetRef).toEqual(assetRef(1));
  });
  it('a failed run is recorded as failed; a run with nothing to remove changes nothing', () => {
    const store = make();
    const { results, entry } = launched(store);
    landCleanupResult({ results, editor: store, entry, job: job({ state: 'failed', outputFileId: null, failureCode: 'boom' }) });
    expect(results.getEntries()[0].state).toBe('failed');
    resetResultsStores();
    const second = launched(store);
    const cursor = store.getState().cursor;
    landCleanupResult({ results: second.results, editor: store, entry: second.entry, job: job({ applied: false, outputFileId: null }) });
    expect(second.results.getEntries()[0].state).toBe('applied');
    expect(store.getState().cursor).toBe(cursor);
  });
  it('"apply to what is left" applies the pieces that remain', () => {
    const store = make();
    const { results, entry } = launched(store);
    landCleanupResult({ results, editor: store, entry, job: job({ outputFileId: null, applied: true }) });
    expect(results.getEntries()[0].state).toBe('failed');
    settleCleanup({ results, editor: store, entry: { ...entry, intent: { ...entry.intent, outputFileId: OUT } }, autoApply: false });
    expect(clip(store, 'x')?.assetRef.projectFileId).toBe(OUT);
  });
});
