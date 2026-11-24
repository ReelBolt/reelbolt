// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, waitFor } from '@testing-library/react';
import { ApiError } from '@/lib/api/client';
import { undoStep } from '@/lib/editing/store';
import type { ObjectRequest } from '@/lib/editing/api/objects';
import { getResultsStore, resetResultsStores } from '@/lib/editing/background/results-store';
import { useBackgroundResultWatcher } from '../useBackgroundResultWatcher';
import { clipOf, launchOn, makeStore, STEP } from './helpers';

const api = vi.hoisted(() => ({ fetchObject: vi.fn(), pending: vi.fn(), resolve: vi.fn() }));
vi.mock('@/lib/editing/api/objects', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/objects')>()), fetchObject: api.fetchObject }));
vi.mock('@/lib/editing/background/object-effect', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/background/object-effect')>()),
  fetchPendingServerResults: api.pending,
  resolveObjectOnServer: api.resolve,
}));

const done = (over: Partial<ObjectRequest> = {}): ObjectRequest => ({
  requestId: 'r1', executionId: 'e1', status: 'succeeded', prompt: 'face', matchScope: null, effect: 'blur', strength: 0.7, invert: false, featherPx: 0,
  analysisStepResultId: STEP, trackIds: ['g1'], coverageEndSec: null, failureCode: null, message: null, createdAt: 'x', ...over,
});
function Mount({ store }: { store: ReturnType<typeof makeStore> }) {
  useBackgroundResultWatcher({ store, projectId: 'p', timelineId: 't' });
  return null;
}
const entryState = () => getResultsStore('t').getEntries()[0]?.state;

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
  api.fetchObject.mockReset();
  api.pending.mockReset().mockResolvedValue(null);
  api.resolve.mockReset().mockResolvedValue(undefined);
});
afterEach(cleanup);

describe('background result watcher', () => {
  it('polls a running entry and applies it as ONE history entry that undo removes (no panel mounted)', async () => {
    const store = makeStore();
    launchOn(store);
    api.fetchObject.mockResolvedValueOnce(done({ status: 'running' })).mockResolvedValue(done());
    const before = store.getState().cursor;
    render(<Mount store={store} />);
    await waitFor(() => expect(entryState()).toBe('applied'), { timeout: 6000 });
    expect(store.getState().cursor).toBe(before + 1);
    expect(clipOf(store, 'x').regionEffects).toHaveLength(1);
    expect(api.resolve).toHaveBeenCalledWith('p', expect.any(String), 'r1', 'applied', 1);
    await undoStep(store);
    expect(clipOf(store, 'x').regionEffects ?? null).toBeNull();
  }, 15000);
  it('picks up an entry that was running before a reload', async () => {
    const store = makeStore();
    launchOn(store);
    resetResultsStores();
    api.fetchObject.mockResolvedValue(done());
    render(<Mount store={store} />);
    await waitFor(() => expect(entryState()).toBe('applied'));
  });
  it('a fatal poll answer stalls the entry (retry flag), it is not dropped', async () => {
    const store = makeStore();
    launchOn(store);
    api.fetchObject.mockRejectedValue(new ApiError('gone', 404));
    render(<Mount store={store} />);
    await waitFor(() => expect(getResultsStore('t').getEntries()[0].stalled).toBe(true));
    expect(entryState()).toBe('running');
  });
  it('adopts a result the assistant started as a landed entry that needs the user\'s OK', async () => {
    const store = makeStore();
    api.pending.mockResolvedValue([{ requestId: 'srv1', executionId: 'e9', mediaRef: 'file:00000000-0000-4000-8000-000000000001', clipIds: ['x'], prompt: 'logo', effect: 'blur', strength: 0.5, invert: false, featherPx: 0, coverageEndSec: 1, analysisStepResultId: STEP, trackIds: ['g1'], createdAt: 'x' }]);
    render(<Mount store={store} />);
    await waitFor(() => expect(getResultsStore('t').getEntries()).toHaveLength(1));
    const e = getResultsStore('t').getEntries()[0];
    expect(e.state).toBe('needsReview');
    expect(e.anchor.coverage).toEqual({ startSec: 0, endSec: 1 });
    expect(e.review?.reasons).toEqual(['outsideCoverage']);
  });
});
