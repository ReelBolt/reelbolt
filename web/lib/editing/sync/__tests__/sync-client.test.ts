/** @vitest-environment node */
/**
 * The sync client, under fake timers with a fake API:
 *   1. debounce: three edits → one PATCH carrying baseRevision, all three ops and the full document;
 *   2. 409 → `conflict`, no retry, further edits are not sent;
 *   3. 400 → `rejected` with the server's violations, no retry;
 *   4. network failure → retry with backoff, then success;
 *   5. an invalid document is never sent (local validation before commit);
 *   6. edits that land mid-flight are sent in the next flush against the new revision, undo intact.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, extractErrorList, extractErrorMessage } from '../../../api/client';
import { createEditorStore } from '../../store/editor-store';
import { reasonsFor, SyncClient } from '../sync-client';
import { userScopeFromEmail } from '../user-scope';
import type { EditTimelineSyncApi, PatchEditTimelineRequest } from '../api';
import { standardDoc } from '../../__tests__/fixtures';

const USER = userScopeFromEmail('me@x.test');

// These fixtures name things `c1`/`timeline-1`, and the production code under test checks documents
// with the strict engine invariants. Only the Guid id shape is relaxed here (it is pinned in
// `engine/__tests__/apply.contract.test.ts`); every structural invariant still runs.
vi.mock('../../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../../engine')>();
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(doc, { ids: 'opaque' }) };
});

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function fakeApi() {
  const calls: PatchEditTimelineRequest[] = [];
  const callOptions: Array<{ keepalive?: boolean } | undefined> = [];
  const queue: Array<{ kind: 'ok'; revision: number } | { kind: 'error'; error: unknown } | { kind: 'hold' }> = [];
  const held: Array<ReturnType<typeof deferred<unknown>>> = [];
  const api: EditTimelineSyncApi = {
    patchEditTimeline: vi.fn(async (_p, _t, request, options?: { keepalive?: boolean }) => {
      calls.push(structuredClone(request));
      callOptions.push(options);
      const next = queue.shift() ?? { kind: 'ok' as const, revision: 100 + calls.length };
      if (next.kind === 'error') throw next.error;
      if (next.kind === 'hold') {
        const d = deferred<unknown>();
        held.push(d);
        await d.promise;
      }
      const revision = next.kind === 'ok' ? next.revision : 100 + calls.length;
      return { id: 't', projectId: 'p', name: 'n', revision, document: request.document, seedStepResultId: null, createdAt: '', updatedAt: '' };
    }),
    getEditTimeline: vi.fn(),
  };
  return { api, calls, callOptions, queue, held };
}

describe('SyncClient', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  const make = (queue?: ReturnType<typeof fakeApi>['queue']) => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 7 });
    const f = fakeApi();
    if (queue) f.queue.push(...queue);
    const client = new SyncClient({ store, api: f.api, debounceMs: 500, retryBaseMs: 100, maxRetries: 3, userId: () => USER });
    return { store, client, ...f };
  };

  it('debounces several edits into one PATCH with baseRevision, ops and the full document', async () => {
    const { store, client, calls, api } = make();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'c' });
    expect(client.getState().status).toBe('dirty');
    expect(api.patchEditTimeline).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(499);
    expect(api.patchEditTimeline).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await vi.runAllTicks();

    expect(calls).toHaveLength(1);
    expect(calls[0].baseRevision).toBe(7);
    expect(calls[0].ops).toHaveLength(3);
    expect(calls[0].document.tracks[0].items[0].name).toBe('c');
    expect(client.getState().status).toBe('saved');
    expect(client.getState().revision).toBe(101);
    expect(store.getState().revision).toBe(101);
    expect(store.getState().dirty).toBe(false);
    expect(store.canUndo()).toBe(true);
  });

  it('a 409 is a conflict: no retry, no further sends, read-only state with the server revision', async () => {
    const { store, client, calls, api } = make([{ kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 9 }) }]);
    // The server moved two revisions and its change log cannot be read: the edits cannot be proven mergeable.
    api.getEditTimeline = vi.fn(async () => ({ id: 't', projectId: 'p', name: 'n', revision: 9, document: standardDoc(), seedStepResultId: null, createdAt: '', updatedAt: '' }));
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(600);
    expect(client.getState().status).toBe('conflict');
    expect(client.getState().conflictRevision).toBe(9);
    expect(client.getState().rebaseReason).toMatch(/could not be read/);

    // The store is frozen while the read-only banner shows (eng D12): the edit is refused outright.
    expect(() => store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' })).toThrow(/read-only/);
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(1);
    expect(client.getState().status).toBe('conflict');
  });

  it('a 400 is a rejection carrying the server violations, with no retry', async () => {
    const violations = [{ path: 'tracks[0].items[0].assetRef', message: 'foreign' }];
    const { store, client, calls } = make([{ kind: 'error', error: new ApiError('bad', 400, [], violations) }]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(600);
    expect(client.getState().status).toBe('rejected');
    expect(client.getState().violations).toEqual(violations);
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(1);
  });

  it('retries a network failure with backoff and then saves', async () => {
    const { store, client, calls } = make([
      { kind: 'error', error: new TypeError('fetch failed') },
      { kind: 'error', error: new ApiError('boom', 503) },
    ]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500); // debounce → first attempt fails
    expect(client.getState().status).toBe('retrying');
    expect(client.getState().attempt).toBe(1);
    await vi.advanceTimersByTimeAsync(100); // backoff 100 ms → second attempt fails
    expect(client.getState().attempt).toBe(2);
    await vi.advanceTimersByTimeAsync(200); // backoff 200 ms → third attempt succeeds
    expect(calls).toHaveLength(3);
    expect(client.getState().status).toBe('saved');
  });

  it('gives up after maxRetries and settles in error; a later edit re-arms', async () => {
    const { store, client, calls } = make([
      { kind: 'error', error: new TypeError('1') },
      { kind: 'error', error: new TypeError('2') },
      { kind: 'error', error: new TypeError('3') },
      { kind: 'error', error: new TypeError('4') },
    ]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500 + 100 + 200 + 400 + 50);
    expect(calls).toHaveLength(4);
    expect(client.getState().status).toBe('error');
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    await vi.advanceTimersByTimeAsync(600);
    expect(calls).toHaveLength(5);
    expect(client.getState().status).toBe('saved');
  });

  it('never sends a document the engine invariants refuse', async () => {
    const { store, client, calls } = make();
    // Corrupt the document behind the engine's back (a bug elsewhere, simulated): overlapping items.
    const broken = structuredClone(store.getState().doc);
    broken.tracks[0].items[1].recordIn = 10;
    store.replaceDocument(broken, store.getState().revision, { markDirty: true });
    await vi.advanceTimersByTimeAsync(600);
    expect(calls).toHaveLength(0);
    expect(client.getState().status).toBe('invalid');
    expect(client.getState().message).toMatch(/overlaps/);
  });

  it('edits that land while a save is in flight are sent next, against the new revision, with undo intact', async () => {
    const { store, client, calls, held } = make([{ kind: 'hold' }]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(1);
    expect(client.getState().status).toBe('saving');

    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    held[0].resolve(undefined);
    await vi.runAllTicks();
    await vi.advanceTimersByTimeAsync(0);
    expect(client.getState().status).toBe('dirty');
    expect(store.getState().revision).toBe(101);
    expect(store.getState().history).toHaveLength(2);

    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(2);
    expect(calls[1].baseRevision).toBe(101);
    expect(calls[1].ops).toHaveLength(1);
    expect(client.getState().status).toBe('saved');
  });

  it('attachOps rides a non-engine marker along with the next flush', async () => {
    const { store, client, calls } = make();
    store.replaceDocument(standardDoc(), store.getState().revision, { markDirty: true });
    client.attachOps([{ type: 'restore', revision: 3 }]);
    await client.flush();
    expect(calls).toHaveLength(1);
    expect(calls[0].ops).toEqual([{ type: 'restore', revision: 3 }]);
  });

  it('dispose stops scheduling', async () => {
    const { store, client, calls } = make();
    client.dispose();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(2000);
    expect(calls).toHaveLength(0);
  });
});

/** A window stand-in: real EventTargets for the window and its document, a settable visibility. */
function fakeWindow() {
  const doc = Object.assign(new EventTarget(), { visibilityState: 'visible' as DocumentVisibilityState });
  const win = Object.assign(new EventTarget(), { document: doc });
  return { win: win as unknown as Window, doc };
}

describe('SyncClient — saves never silently stop (T-014: eng D8-D12)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  const make = (queue?: ReturnType<typeof fakeApi>['queue'], options?: { retryBaseMs?: number }) => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 7 });
    const f = fakeApi();
    if (queue) f.queue.push(...queue);
    const client = new SyncClient({ store, api: f.api, debounceMs: 500, retryBaseMs: options?.retryBaseMs ?? 100, maxRetries: 3, queueStorage: null, isOnline: () => true, userId: () => USER });
    return { store, client, ...f };
  };

  it('eng D8: concurrent flushes share ONE in-flight request; no two PATCHes ever carry the same base', async () => {
    const { store, client, calls, held } = make([{ kind: 'hold' }]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    const first = client.flush();
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);

    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    const second = client.flush();
    const third = client.flush();
    expect(second).toBe(third);
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1); // waiters do not start their own request

    held[0].resolve(undefined);
    await Promise.all([first, second, third]);
    await vi.advanceTimersByTimeAsync(2000);

    expect(calls.map((c) => c.baseRevision)).toEqual([7, 101]);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().dirty).toBe(false);
  });

  it('eng D9: playhead and selection ticks do not starve the autosave (11 s of playback saves)', async () => {
    const { store, client, calls } = make();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    for (let t = 0; t < 11_000; t += 40) {
      store.setUi((ui) => ({ playheadFrame: ui.playheadFrame + 1 }));
      await vi.advanceTimersByTimeAsync(40);
    }
    expect(calls).toHaveLength(1);
    expect(client.getState().status).toBe('saved');
  });

  it('eng D9: a click while a failed save waits for its backoff does not re-send it early', async () => {
    const { store, calls } = make([{ kind: 'error', error: new ApiError('boom', 503) }], { retryBaseMs: 5000 });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(1);
    store.setUi({ selection: ['c2'] });
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(4000);
    expect(calls).toHaveLength(2);
  });

  it('eng D10: pagehide flushes at once with keepalive', async () => {
    const { store, client, calls, callOptions } = make();
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    win.dispatchEvent(new Event('pagehide'));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);
    expect(callOptions[0]).toEqual({ keepalive: true });
  });

  it('pagehide while a normal save is in flight sends the newest document with keepalive on the in-flight base', async () => {
    const { store, client, calls, callOptions, held } = make([
      { kind: 'hold' }, // the normal autosave, still in flight when the page hides
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 102 }) }, // keepalive: the first one won (the fake answers 100 + calls)
    ]);
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(1);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });

    win.dispatchEvent(new Event('pagehide'));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(2); // sent NOW: a queued follow-up would die with the page
    expect(callOptions[1]).toEqual({ keepalive: true });
    expect(calls[1].baseRevision).toBe(7);
    expect(calls[1].ops).toHaveLength(2);
    expect(calls[1].document.tracks[0].items[0].name).toBe('b');

    // The page survived (bfcache): the first save lands, the keepalive's 409 is expected, not a conflict.
    held[0].resolve(undefined);
    await vi.advanceTimersByTimeAsync(1000);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().readOnly).toBe(false);
    expect(calls).toHaveLength(3);
    expect(calls[2].baseRevision).toBe(102);
    expect(calls[2].ops).toHaveLength(1);
    expect(store.getState().dirty).toBe(false);
  });

  it('pagehide keepalive that lands while the in-flight save fails counts as saved, with nothing re-sent', async () => {
    const { store, client, calls, held } = make([{ kind: 'hold' }, { kind: 'ok', revision: 8 }]);
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    win.dispatchEvent(new Event('pagehide'));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(2);

    held[0].reject(new TypeError('aborted'));
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(2);
    expect(client.getState().status).toBe('saved');
    expect(client.getState().revision).toBe(8);
    expect(store.getState().revision).toBe(8);
    expect(store.getState().dirty).toBe(false);
  });

  it('pagehide keepalive that overtakes the in-flight save: the in-flight 409 is not a conflict', async () => {
    const { store, client, calls, held } = make([{ kind: 'hold' }, { kind: 'ok', revision: 8 }]);
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    win.dispatchEvent(new Event('pagehide'));
    await vi.advanceTimersByTimeAsync(0);

    held[0].reject(new ApiError('conflict', 409, [], { currentRevision: 8 }));
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(2);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().revision).toBe(8);
    expect(store.getState().readOnly).toBe(false);
    expect(store.getState().dirty).toBe(false);
  });

  // T-073 / P3-SYNC-10: on a full navigation `pagehide` fires only once the next page has answered;
  // a keepalive PATCH first issued there was dropped ~1 run in 4 on mobile Chromium. `beforeunload`
  // fires while the page is still whole, so the save leaves there, once.
  it('beforeunload while dirty sends the save at once with keepalive; the pagehide after it sends nothing twice', async () => {
    const { store, client, calls, callOptions, held } = make([{ kind: 'hold' }]);
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    const unload = new Event('beforeunload', { cancelable: true });
    win.dispatchEvent(unload);
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);
    expect(callOptions[0]).toEqual({ keepalive: true });
    expect(unload.defaultPrevented).toBe(true); // the leave warning still shows while the save is out

    win.dispatchEvent(new Event('pagehide'));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);

    held[0].resolve(undefined);
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(1);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().dirty).toBe(false);
  });

  // C-319-1: the browser aborts a normal fetch at unload and the server honours the abort, so a
  // PATCH in flight at tab close (with no later edit) must go again, identically, with keepalive.
  it('pagehide while a normal save is in flight with no later edit re-sends that same body with keepalive', async () => {
    const { store, client, calls, callOptions, held } = make([
      { kind: 'hold' },
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 8 }) }, // the first one landed
    ]);
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(1);
    expect(callOptions[0]).toBeUndefined();

    win.dispatchEvent(new Event('pagehide'));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(2);
    expect(callOptions[1]).toEqual({ keepalive: true });
    expect(calls[1]).toEqual(calls[0]); // same base, same ops, same document

    // The page survived and the first request landed: the re-send's 409 is the same document, harmless.
    held[0].resolve(undefined);
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(2);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().readOnly).toBe(false);
    expect(store.getState().dirty).toBe(false);
  });

  it('the keepalive re-send saves the edit when the in-flight save is aborted by the unload', async () => {
    const { store, client, calls, held } = make([{ kind: 'hold' }, { kind: 'ok', revision: 8 }]);
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(500);
    const unload = new Event('beforeunload', { cancelable: true });
    win.dispatchEvent(unload);
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(2);
    expect(calls[1]).toEqual(calls[0]);

    held[0].reject(new TypeError('aborted'));
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(2);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().revision).toBe(8);
    expect(store.getState().dirty).toBe(false);
  });

  it('beforeunload with nothing to save sends nothing and does not warn', async () => {
    const { client, calls } = make();
    const { win } = fakeWindow();
    client.attachLifecycle(win);
    const unload = new Event('beforeunload', { cancelable: true });
    win.dispatchEvent(unload);
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toHaveLength(0);
    expect(unload.defaultPrevented).toBe(false);
  });

  it('eng D10: visibilitychange to hidden flushes at once with keepalive', async () => {
    const { store, client, calls, callOptions } = make();
    const { win, doc } = fakeWindow();
    client.attachLifecycle(win);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    doc.visibilityState = 'hidden';
    doc.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);
    expect(callOptions[0]).toEqual({ keepalive: true });
  });

  it('eng D10: dispose (route change) flushes the pending edit before it stops', async () => {
    const { store, client, calls } = make();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    client.dispose();
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);
    expect(calls[0].document.tracks[0].items[0].name).toBe('a');
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' });
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toHaveLength(1);
  });

  it('a 5xx whose retry lands on 409 at base+1 with the document this tab sent counts as saved', async () => {
    const { store, client, calls, api } = make([
      { kind: 'error', error: new ApiError('Bad gateway', 502) },
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 8 }) },
    ]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    const sent = structuredClone(store.getState().doc);
    vi.mocked(api.getEditTimeline).mockResolvedValue({ id: 't', projectId: 'p', name: 'n', revision: 8, document: sent, seedStepResultId: null, createdAt: '', updatedAt: '' });
    await vi.advanceTimersByTimeAsync(500);
    expect(client.getState().status).toBe('retrying');
    await vi.advanceTimersByTimeAsync(100);
    expect(calls).toHaveLength(2);
    expect(api.getEditTimeline).toHaveBeenCalledTimes(1);
    expect(client.getState().status).toBe('saved');
    expect(client.getState().revision).toBe(8);
    expect(store.getState().revision).toBe(8);
    expect(store.getState().dirty).toBe(false);
    expect(store.getState().readOnly).toBe(false);
  });

  it('a 5xx whose retry lands on 409 with a DIFFERENT server document is a conflict, and the store goes read-only', async () => {
    const { store, client, api } = make([
      { kind: 'error', error: new ApiError('Bad gateway', 502) },
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 8 }) },
    ]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    vi.mocked(api.getEditTimeline).mockResolvedValue({ id: 't', projectId: 'p', name: 'n', revision: 8, document: standardDoc(), seedStepResultId: null, createdAt: '', updatedAt: '' });
    await vi.advanceTimersByTimeAsync(600);
    expect(client.getState().status).toBe('conflict');
    expect(client.getState().conflictRevision).toBe(8);
    expect(store.getState().readOnly).toBe(true);
    expect(() => store.dispatch({ type: 'lift', itemId: 'c2' })).toThrow(/read-only/);
  });

  it('a 5xx that landed, then an edit during the backoff: the 409 is checked against the document SENT, not the current one', async () => {
    const { store, client, calls, api } = make([
      { kind: 'error', error: new ApiError('Bad gateway', 502) },
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 8 }) },
    ]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    const sent = structuredClone(store.getState().doc);
    vi.mocked(api.getEditTimeline).mockResolvedValue({ id: 't', projectId: 'p', name: 'n', revision: 8, document: sent, seedStepResultId: null, createdAt: '', updatedAt: '' });
    await vi.advanceTimersByTimeAsync(500); // PATCH at base 7 → 502 (but it landed)
    expect(client.getState().status).toBe('retrying');

    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'b' }); // edit during the backoff
    await vi.advanceTimersByTimeAsync(100); // retry at base 7 → 409 at 8, head holds the FIRST document
    expect(calls).toHaveLength(2);
    expect(calls[1].document.tracks[0].items[0].name).toBe('b');
    expect(client.getState().status).not.toBe('conflict');
    expect(store.getState().readOnly).toBe(false);
    expect(store.getState().revision).toBe(8);
    expect(store.getState().pendingOps).toHaveLength(1); // only the edit made during the backoff is left

    await vi.advanceTimersByTimeAsync(600); // the remaining edit goes on the new base
    expect(calls).toHaveLength(3);
    expect(calls[2].baseRevision).toBe(8);
    expect(calls[2].ops).toHaveLength(1);
    expect(calls[2].document.tracks[0].items[0].name).toBe('b');
    expect(client.getState().status).toBe('saved');
    expect(store.getState().dirty).toBe(false);
  });

  it('a failed head read after 5xx→409 is "unknown": keep retrying with a plain reason, never conflict', async () => {
    const { store, client, calls, api } = make([
      { kind: 'error', error: new ApiError('Bad gateway', 502) },
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 8 }) },
      { kind: 'error', error: new ApiError('conflict', 409, [], { currentRevision: 8 }) },
    ]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    const sent = structuredClone(store.getState().doc);
    vi.mocked(api.getEditTimeline)
      .mockRejectedValueOnce(new TypeError('fetch failed'))
      .mockResolvedValue({ id: 't', projectId: 'p', name: 'n', revision: 8, document: sent, seedStepResultId: null, createdAt: '', updatedAt: '' });
    await vi.advanceTimersByTimeAsync(500); // 502
    await vi.advanceTimersByTimeAsync(100); // 409 at 8, head read fails
    expect(calls).toHaveLength(2);
    expect(client.getState().status).toBe('retrying');
    expect(store.getState().readOnly).toBe(false);
    for (const reason of client.getState().reasons ?? []) expect(reason).not.toMatch(/409|fetch|conflict/i);
    expect(client.getState().reasons?.length).toBeGreaterThan(0);

    await vi.advanceTimersByTimeAsync(200); // retry → 409 at 8 again, head now readable and equal
    expect(calls).toHaveLength(3);
    expect(client.getState().status).toBe('saved');
    expect(store.getState().revision).toBe(8);
    expect(store.getState().readOnly).toBe(false);
  });

  it('eng D11: a rejected save carries plain-sentence reasons (no ids, no paths) and Retry re-sends', async () => {
    const violations = [
      { path: 'tracks[0].items[1].fadeIn', message: 'fadeIn must not exceed the clip duration' },
      { path: 'sequences[0].id', message: 'sequence id 3f2b1c9e-2d4a-4b7e-9a51-0c6f2e8d1a77 is duplicated' },
    ];
    const { store, client, calls } = make([{ kind: 'error', error: new ApiError('bad', 400, [], violations) }]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(600);
    const state = client.getState();
    expect(state.status).toBe('rejected');
    expect(state.reasons).toEqual([
      'Track 1, clip 2: fade in must not exceed the clip duration.',
      'Sequence 1: sequence id is duplicated.',
    ]);
    for (const reason of state.reasons ?? []) {
      expect(reason).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/i);
      expect(reason).not.toMatch(/\[\d+\]/);
    }

    await client.retry();
    expect(calls).toHaveLength(2);
    expect(client.getState().status).toBe('saved');
    expect(client.getState().reasons).toEqual([]);
  });

  it('eng D11: a { message, errors } body becomes the reason list', async () => {
    const { store, client } = make([{ kind: 'error', error: new ApiError('The request is invalid.', 400, ['baseRevision is required'], { message: 'The request is invalid.', errors: ['baseRevision is required'] }) }]);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'a' });
    await vi.advanceTimersByTimeAsync(600);
    expect(client.getState().reasons).toEqual(['Base revision is required.']);
  });

  it('unrecognised raw framework / JSON text from the server becomes a plain generic sentence', () => {
    const raw = [
      '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400}',
      'System.InvalidOperationException: Sequence contains no elements\n   at ReelBolt.Api.Controllers.EditTimelinesController.Patch()',
      '<!DOCTYPE html><html><body>Bad Request</body></html>',
    ];
    for (const message of raw) {
      expect(reasonsFor(new ApiError(message, 400))).toEqual(['The server refused the timeline.']);
    }
    // A real sentence still passes through, humanised.
    expect(reasonsFor(new ApiError('The document name is too long.', 400))).toEqual(['The document name is too long.']);
  });
});

// C-319-2: the bodies the QA review reproduced. An ApiError is built exactly as `apiFetch` builds it.
describe('reasonsFor — no framework text, type name or op name reaches a save reason (C-319-2)', () => {
  const asFetched = (status: number, body: unknown) =>
    new ApiError(extractErrorMessage(body) ?? 'Bad Request', status, extractErrorList(body), body);
  const problem = (errors: Record<string, string[]>) => ({
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'One or more validation errors occurred.',
    status: 400,
    errors,
  });
  const GENERIC = ['The server refused the timeline.'];

  it('System.Text.Json binding errors become the plain generic sentence', () => {
    const bodies = [
      problem({ '$.ops[0]': ['The JSON value could not be converted to ReelBolt.Shared.Editing.EditOp. Path: $. | LineNumber: 0 | BytePositionInLine: 1.'] }),
      problem({
        request: ['The request field is required.'],
        '$.document.tracks[0].items[0]': ['The JSON value could not be converted to ReelBolt.Shared.Editing.Item. Path: $.document.tracks[0].items[0] | LineNumber: 0 | BytePositionInLine: 212.'],
      }),
      problem({ '$.baseRevision': ['The JSON value could not be converted to System.Int32. Path: $.baseRevision | LineNumber: 0 | BytePositionInLine: 20.'] }),
      problem({ '$.document.tracks[0].items[0].type': ["Read unrecognized type discriminator id 'textOverlayX'. Path: $.document.tracks[0].items[0].type | LineNumber: 0 | BytePositionInLine: 88."] }),
      problem({ $: ["'s' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."] }),
      problem({ request: ['The request field is required.'] }),
    ];
    for (const body of bodies) expect(reasonsFor(asFetched(400, body))).toEqual(GENERIC);
  });

  it('op names never show, spelled out or not: a known op prefix is removed, a message naming an op is dropped', () => {
    const violations = [
      { path: 'ops[0]', message: 'setKeyframe: the keyframe is outside the clip' },
      { path: 'ops[1]', message: "op 'setTrackProperty' is not supported" },
      { path: 'ops[2]', message: 'moveKeyframe needs a keyframe at that frame' },
      { path: 'ops[3]', message: "unknown op type 'insert'" },
    ];
    const reasons = reasonsFor(asFetched(400, violations));
    expect(reasons).toEqual(['The keyframe is outside the clip.']);
    for (const reason of reasons) expect(reason).not.toMatch(/set ?keyframe|set ?track ?property|move ?keyframe|'insert'/i);
  });

  it('a real sentence with a spelled-out field name still passes', () => {
    expect(reasonsFor(asFetched(400, [{ path: 'tracks[0].items[1].fadeIn', message: 'fadeIn must not exceed the clip duration' }]))).toEqual([
      'Track 1, clip 2: fade in must not exceed the clip duration.',
    ]);
  });
});
