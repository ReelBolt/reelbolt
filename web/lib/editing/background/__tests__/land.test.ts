// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createEditorStore, undoStep } from '../../store';
import { assetRef, doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { MediaClip, RegionClipEffect } from '../../types';
import type { ObjectRequest } from '../../api/objects';
import { anchorForClips } from '../anchor';
import { entryFromServerItem, landObjectResult, settleLanded, type ServerObjectItem } from '../object-effect';
import { getResultsStore, resetResultsStores, type BackgroundEntry } from '../results-store';

const STEP = '00000000-0000-4000-8000-0000000000aa';
const request = (over: Partial<ObjectRequest> = {}): ObjectRequest => ({
  requestId: 'r1', executionId: 'e1', status: 'succeeded', prompt: 'face', matchScope: null, effect: 'blur', strength: 0.7, invert: false, featherPx: 0,
  analysisStepResultId: STEP, trackIds: ['g1'], coverageEndSec: null, failureCode: null, message: null, createdAt: 'x', ...over,
});
const regionEffect = (id: string): RegionClipEffect => ({ effect: 'blur', strength: 0.5, enabled: true, mask: { kind: 'segmentation', analysisStepResultId: id, trackIds: ['g1'], invert: false, featherPx: 0 } });
const clip = (store: ReturnType<typeof make>, id: string) => store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip;

function make(clips: MediaClip[] = [media('x', 0, 60, 0)]) {
  return createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', clips)]), revision: 1 });
}
function launched(store: ReturnType<typeof make>): BackgroundEntry {
  const results = getResultsStore('t');
  const entry: BackgroundEntry = {
    id: 'r1', tool: 'objectEffect', requestId: 'r1', anchor: anchorForClips(store.getState().doc, ['x'], { startSec: 0, endSec: null }),
    intent: { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0 }, state: 'running', createdAt: 'x',
  };
  results.launch(entry);
  return entry;
}
const stateOf = () => getResultsStore('t').getEntries()[0];

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('a result lands', () => {
  it('applies ONE history entry to the current document and undo removes it', async () => {
    const store = make();
    const entry = launched(store);
    // Meanwhile the user split the clip: the result follows to both pieces.
    store.dispatchBatch([{ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'y' }], 'Split');
    const before = store.getState().cursor;
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(store.getState().cursor).toBe(before + 1);
    expect(clip(store, 'x').regionEffects).toHaveLength(1);
    expect(clip(store, 'y').regionEffects).toHaveLength(1);
    expect(stateOf().state).toBe('applied');
    await undoStep(store);
    expect(clip(store, 'x').regionEffects ?? null).toBeNull();
    expect(clip(store, 'y').regionEffects ?? null).toBeNull();
  });
  it('skips a clip that already carries an identical entry', () => {
    const store = make();
    const entry = launched(store);
    const same: RegionClipEffect = { effect: 'blur', strength: 0.7, enabled: true, mask: { kind: 'segmentation', analysisStepResultId: STEP, trackIds: ['g1'], invert: false, featherPx: 0 } };
    store.dispatch({ type: 'setProperty', itemId: 'x', key: 'regionEffects', value: [same] }, 'seed');
    const before = store.getState().cursor;
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(store.getState().cursor).toBe(before);
    expect(clip(store, 'x').regionEffects).toHaveLength(1);
  });
  it('read-only: needs review, nothing written', () => {
    const store = make();
    const entry = launched(store);
    store.setReadOnly(true);
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(stateOf().state).toBe('needsReview');
    expect(stateOf().review?.blocked).toBe('readOnly');
    expect(clip(store, 'x').regionEffects ?? null).toBeNull();
  });
  it('a refusal thrown by the editor becomes needs review, never a silent drop', () => {
    const store = make();
    const entry = launched(store);
    vi.spyOn(store, 'dispatchBatch').mockImplementation(() => {
      throw new Error('The timeline moved on.');
    });
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(stateOf().state).toBe('needsReview');
    expect(stateOf().review).toMatchObject({ blocked: 'refused', refusal: 'The timeline moved on.', applicable: ['x'] });
  });
  it('the clip was deleted: needs review with the reason, nothing applied', () => {
    const store = make();
    const entry = launched(store);
    store.dispatch({ type: 'lift', itemId: 'x' } as never, 'Delete');
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(stateOf().state).toBe('needsReview');
    expect(stateOf().review?.reasons).toEqual(['targetGone']);
    expect(stateOf().review?.applicable).toEqual([]);
  });
  it('the video was replaced: needs review (assetChanged)', () => {
    const store = make();
    const entry = launched(store);
    store.dispatch({ type: 'setProperty', itemId: 'x', key: 'assetRef', value: assetRef(2) } as never, 'Replace');
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(stateOf().review?.reasons).toEqual(['assetChanged']);
  });
  it('a clip already holding 4 effects: needs review (limitReached)', () => {
    const store = make([media('x', 0, 60, 0, { regionEffects: [regionEffect('a'), regionEffect('b'), regionEffect('c'), regionEffect('d')] })]);
    const entry = launched(store);
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request(), label: 'Add' });
    expect(stateOf().review?.reasons).toEqual(['limitReached']);
  });
  it('the result covers less than the clip now plays: apply to the part inside is offered, not forced', () => {
    const store = make();
    const entry = launched(store);
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request({ coverageEndSec: 1 }), label: 'Add' });
    expect(stateOf().state).toBe('needsReview');
    expect(stateOf().review?.reasons).toEqual(['outsideCoverage']);
    expect(clip(store, 'x').regionEffects ?? null).toBeNull();
  });
  it('a failed run is failed, with the server code', () => {
    const store = make();
    const entry = launched(store);
    landObjectResult({ results: getResultsStore('t'), editor: store, entry, request: request({ status: 'failed', failureCode: 'OBJECT_NOT_FOUND', analysisStepResultId: null, trackIds: null }), label: 'Add' });
    expect(stateOf()).toMatchObject({ state: 'failed', failure: { code: 'OBJECT_NOT_FOUND' } });
  });
  it('"apply to what is left" applies to the applicable clips as one undo step', () => {
    const store = make([media('x', 0, 60, 0), media('w', 60, 120, 0)]);
    const results = getResultsStore('t');
    const entry: BackgroundEntry = {
      id: 'r1', tool: 'objectEffect', requestId: 'r1', anchor: anchorForClips(store.getState().doc, ['x', 'w'], { startSec: 0, endSec: 10 }),
      intent: { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0, analysisStepResultId: STEP, trackIds: ['g1'] }, state: 'needsReview', createdAt: 'x',
    };
    results.launch(entry);
    store.dispatch({ type: 'lift', itemId: 'w' } as never, 'Delete');
    const before = store.getState().cursor;
    settleLanded({ results, editor: store, entry, label: 'Add', autoApply: false });
    expect(store.getState().cursor).toBe(before + 1);
    expect(clip(store, 'x').regionEffects).toHaveLength(1);
    expect(stateOf().state).toBe('applied');
  });
});

describe('entryFromServerItem', () => {
  const item = (over: Partial<ServerObjectItem> = {}): ServerObjectItem => ({
    requestId: 'r9', executionId: 'e9', mediaRef: 'file:x', clipIds: ['x'], prompt: 'face', effect: 'blur', strength: 0.5, invert: false, featherPx: 0, coverageEndSec: null, analysisStepResultId: STEP, trackIds: ['g1'], createdAt: '2026-01-01T00:00:00Z', ...over,
  });
  const d = doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0)])]);
  it('records the workflowId from a needs_review list item so the job can be stopped', () => {
    expect(entryFromServerItem(d, item({ workflowId: 'w9' })).intent).toMatchObject({ workflowId: 'w9' });
  });
  it('leaves it out when the server did not send one', () => {
    expect('workflowId' in entryFromServerItem(d, item()).intent).toBe(false);
  });
});

describe('assistant result adopted after a split during the run', () => {
  it('reaches both pieces', () => {
    const s = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0)])]), revision: 1 });
    s.dispatch({ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'x2' } as never);
    const e = entryFromServerItem(s.getState().doc, { requestId: 'r', executionId: 'e', mediaRef: 'file:' + '00000000-0000-4000-8000-000000000001', clipIds: ['x'], prompt: 'f', effect: 'blur', strength: 0.5, invert: false, featherPx: 0, coverageEndSec: null, analysisStepResultId: STEP, trackIds: ['g1'], createdAt: '2026-01-01T00:00:00Z' });
    expect(e.anchor.assetClipIdsAtLaunch).toEqual(['x']);
  });
});
