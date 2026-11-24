/** @vitest-environment node */
/**
 * Wave 1 sync additions, on the state machine (fake API, fake IndexedDB, injected timers):
 *   R02.2 the journal is written on hide / shortly after an edit and cleared on a confirmed save; a journal
 *         newer than the server is offered (Restore rebases + saves, Discard forgets); a 401 keeps the edits,
 *         pauses saving, and `resumeAfterSignIn` flushes; "memory only" is told when no store works;
 *   R02.1 a 409 first tries an online rebase (disjoint edits merge, `mergedAt` stamped), overlapping edits become
 *         a conflict with a reason, and `saveAsCopy` writes the local document into a duplicate;
 *   R02.3 every failure class says its own plain sentence (no revision counters);
 *   R02.5 `canEdit: false` is frozen from the start and never PATCHes or journals.
 */

import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../../api/client';
import { applyAll, type EditOp } from '../../engine';
import { createEditorStore } from '../../store/editor-store';
import { standardDoc } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';
import type { EditTimelineResponse, EditTimelineSyncApi, PatchEditTimelineRequest, WireOp } from '../api';
import { ANONYMOUS_SCOPE, userScopeFromEmail, wipeEditorLocalData } from '../user-scope';
import { EditJournal, JOURNAL_VERSION, journalKey, type JournalEntry } from '../journal';
import { queueKey, type QueueStorage } from '../offline-queue';
import { describeSaveState, isReadOnlySave, MEMORY_ONLY, SIGN_IN_AGAIN, SyncClient, type SaveState } from '../sync-client';

const USER = userScopeFromEmail('me@x.test');
const OTHER = userScopeFromEmail('other@x.test');
const SOMEONE_ELSE = userScopeFromEmail('someone-else@x.test');
const USER_A = userScopeFromEmail('a@x.test');
const USER_B = userScopeFromEmail('b@x.test');

vi.mock('../../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../../engine')>();
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(doc, { ids: 'opaque' }) };
});

const base = standardDoc();
const run = (doc: EditTimeline, ops: EditOp[]) => applyAll(doc, ops);
const memoryStorage = (): QueueStorage & { data: Map<string, string> } => {
  const data = new Map<string, string>();
  return { data, get: (k) => data.get(k) ?? null, set: (k, v) => void data.set(k, v), remove: (k) => void data.delete(k) };
};
const response = (over: Partial<EditTimelineResponse> & { document: EditTimeline; revision: number }): EditTimelineResponse => ({ id: 't', projectId: 'p', name: 'n', seedStepResultId: null, createdAt: '', updatedAt: '', ...over });

function setup(opts: { serverRevision?: number; serverDoc?: EditTimeline; history?: Array<{ revision: number; ops: WireOp[] }>; canEdit?: boolean; journal?: EditJournal | null; userId?: () => string } = {}) {
  const server = { revision: opts.serverRevision ?? 7, doc: opts.serverDoc ?? base, history: opts.history ?? [] };
  const patches: Array<{ timelineId: string; request: PatchEditTimelineRequest }> = [];
  const failures: unknown[] = [];
  const duplicates: Array<{ timelineId: string; name: string | null | undefined }> = [];
  const api: EditTimelineSyncApi = {
    patchEditTimeline: vi.fn(async (_p, timelineId, request) => {
      patches.push({ timelineId, request: structuredClone(request) });
      const failure = failures.shift();
      if (failure) throw failure;
      if (timelineId === 't' && request.baseRevision !== server.revision) throw new ApiError('conflict', 409, [], { currentRevision: server.revision });
      if (timelineId === 't') {
        server.revision = request.baseRevision + 1;
        server.doc = request.document;
      }
      return response({ id: timelineId, revision: request.baseRevision + 1, document: request.document });
    }),
    getEditTimeline: vi.fn(async () => response({ revision: server.revision, document: server.doc })),
    getEditTimelineHistory: vi.fn(async () => ({ id: 't', currentRevision: server.revision, entries: [...server.history].reverse().map((h) => ({ ...h, createdAt: '', hasSnapshot: false })), restorableRevisions: [] })),
    duplicateEditTimeline: vi.fn(async (_p, timelineId, name) => {
      duplicates.push({ timelineId, name });
      return response({ id: 'copy-1', name: name ?? 'copy', revision: 1, document: { ...server.doc, id: 'copy-1' } });
    }),
  };
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: base, revision: 7 });
  const storage = memoryStorage();
  const journal = opts.journal === undefined ? new EditJournal({ idb: new IDBFactory(), storage }) : opts.journal;
  const timers: Array<() => void> = [];
  const sync = new SyncClient({
    store, api, debounceMs: 1, retryBaseMs: 1, maxRetries: 0, queueStorage: null, journal, canEdit: opts.canEdit, tabId: 'tab-test', userId: opts.userId ?? (() => USER),
    setTimeout: (fn) => { timers.push(fn); return timers.length; }, clearTimeout: () => {},
  });
  return { store, sync, api, server, patches, failures, duplicates, storage, journal, timers, fireTimers: () => timers.splice(0).forEach((t) => t()) };
}

const rename = (name: string): EditOp => ({ type: 'setProperty', itemId: 'c1', key: 'name', value: name });

// The journal expires entries by wall clock; these fixtures use tiny savedAt values, so the clock sits just after them.
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(5000);
});
afterEach(() => vi.useRealTimers());

describe('T-023 sign-out in another tab', () => {
  const rigged = (userId: () => string = () => USER) => {
    let signOut: () => void = () => undefined;
    const wiped: string[] = [];
    const storage = memoryStorage();
    const journal = new EditJournal({ idb: new IDBFactory(), storage });
    const t = setup({ journal, userId });
    // re-create with the sign-out hook injected
    const sync = new SyncClient({
      store: t.store, api: t.api, debounceMs: 1, retryBaseMs: 1, maxRetries: 0, queueStorage: null, journal, tabId: 'tab-test', userId,
      onSignOut: (h) => { signOut = h; return () => undefined; },
      wipeLocalData: async () => { wiped.push('wiped'); storage.data.clear(); },
      setTimeout: () => 1, clearTimeout: () => {},
    });
    return { ...t, sync, storage, wiped, signOut: () => signOut() };
  };

  it('stops journalling and wipes the per-user entries when another tab signs out', async () => {
    const t = rigged();
    t.store.dispatch(rename('Edited'));
    (t.sync as unknown as { writeJournal(): void }).writeJournal();
    expect(t.storage.data.size).toBe(1);
    t.signOut();
    expect(t.wiped).toEqual(['wiped']);
    t.store.dispatch(rename('Edited again'));
    (t.sync as unknown as { writeJournal(): void }).writeJournal();
    expect(t.storage.data.size).toBe(0);
    expect(t.sync.getState().status).toBe('authExpired');
  });

  it('refuses its next write after a sign-out it never heard (durable record); an expiry alone keeps journalling', () => {
    const ls = new Map<string, string>();
    vi.stubGlobal('window', { addEventListener: () => undefined, removeEventListener: () => undefined, localStorage: { getItem: (k: string) => ls.get(k) ?? null, setItem: (k: string, v: string) => void ls.set(k, v), removeItem: (k: string) => void ls.delete(k), length: 0, key: () => null } });
    try {
      const expired = rigged(() => ANONYMOUS_SCOPE);
      expired.store.dispatch(rename('Edited'));
      (expired.sync as unknown as { writeJournal(): void }).writeJournal();
      expect(expired.storage.data.size).toBe(1); // 401 / expiry: no sign-out recorded, the journal is kept
      const t = rigged(() => ANONYMOUS_SCOPE);
      ls.set('reelbolt:editor:signed-out-at', String(Date.now() + 1)); // another tab signed out; this one never heard it
      t.store.dispatch(rename('Edited'));
      (t.sync as unknown as { writeJournal(): void }).writeJournal();
      expect(t.storage.data.size).toBe(0);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('a sign-out closes this tab\'s IndexedDB connection so the database delete is not blocked', async () => {
    const idb = new IDBFactory();
    const journal = new EditJournal({ idb, storage: null });
    await journal.warm();
    let hear: () => void = () => undefined;
    const t = setup({ journal });
    new SyncClient({ store: t.store, api: t.api, queueStorage: null, journal, userId: () => USER, onSignOut: (h) => { hear = h; return () => undefined; }, wipeLocalData: async () => undefined, setTimeout: () => 1, clearTimeout: () => {} });
    hear();
    const outcome = await new Promise<string>((resolve) => { const r = idb.deleteDatabase('reelbolt-editor'); r.onsuccess = () => resolve('deleted'); r.onblocked = () => resolve('blocked'); });
    expect(outcome).toBe('deleted');
  });

  it('a tab whose session cookie user differs refuses to journal', () => {
    let user = USER;
    const t = rigged(() => user);
    user = OTHER;
    t.store.dispatch(rename('Edited'));
    (t.sync as unknown as { writeJournal(): void }).writeJournal();
    expect(t.storage.data.size).toBe(0);
  });

  it('an expired session (cookie gone) still journals, so nothing is lost', () => {
    let user = USER;
    const t = rigged(() => user);
    user = ANONYMOUS_SCOPE;
    t.store.dispatch(rename('Edited'));
    (t.sync as unknown as { writeJournal(): void }).writeJournal();
    expect(t.storage.data.has(journalKey(USER, 't'))).toBe(true);
  });
});

describe('R02.2 the journal', () => {
  it('writes the unsaved document on hide (synchronously to localStorage) and clears it when the server confirms', async () => {
    const t = setup();
    const target = new EventTarget() as unknown as Window & { document: { visibilityState: string; addEventListener: EventTarget['addEventListener']; removeEventListener: EventTarget['removeEventListener'] } };
    const doc = new EventTarget();
    Object.assign(target, { document: Object.assign(doc, { visibilityState: 'hidden' }) });
    t.sync.attachLifecycle(target);
    t.store.dispatch(rename('Edited'));
    (target as unknown as EventTarget).dispatchEvent(new Event('pagehide'));
    const stored = JSON.parse(t.storage.data.get(journalKey(USER, 't')) ?? 'null') as { kind: string; entry: JournalEntry };
    expect(stored.kind).toBe('entry');
    expect(stored.entry).toMatchObject({ baseRevision: 7, tabId: 'tab-test' });
    expect(stored.entry.localDoc.tracks[0].items[0].name).toBe('Edited');
    await t.sync.flush();
    await vi.waitFor(async () => expect(await t.journal?.read(USER, 't')).toBeNull());
    expect(t.sync.getState().status).toBe('saved');
  });

  it('writes shortly after an edit even when no hide event ever comes (a killed tab)', async () => {
    const t = setup();
    t.store.dispatch(rename('Killed'));
    expect(t.storage.data.has(journalKey(USER, 't'))).toBe(false);
    t.fireTimers(); // the journal timer (and the debounce) fire
    expect(JSON.parse(t.storage.data.get(journalKey(USER, 't')) ?? 'null')).toMatchObject({ kind: 'entry' });
  });

  it('offers a journal newer than the server; Restore rebases onto the server and saves, Discard forgets', async () => {
    const journal = new EditJournal({ idb: new IDBFactory(), storage: null });
    const local = run(base, [rename('Recovered')]);
    await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Recovered')], savedAt: 123, tabId: 'old-tab', userId: USER });
    const t = setup({ journal });
    expect(await t.sync.checkRecovery()).toBe('offered');
    expect(t.sync.getState().recovery).toEqual({ savedAt: 123 });
    expect(await t.sync.restoreRecovered()).toBe('sent');
    expect(t.patches).toHaveLength(1);
    expect(t.patches[0].request.baseRevision).toBe(7);
    expect(t.server.revision).toBe(8);
    expect(t.server.doc.tracks[0].items[0].name).toBe('Recovered');
    expect(t.sync.getState()).toMatchObject({ status: 'saved', recovery: null });
    await vi.waitFor(async () => expect(await journal.read(USER, 't')).toBeNull());

    const journal2 = new EditJournal({ idb: new IDBFactory(), storage: null });
    await journal2.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Recovered')], savedAt: 5, tabId: 'old-tab', userId: USER });
    const u = setup({ journal: journal2 });
    await u.sync.checkRecovery();
    u.sync.discardRecovered();
    expect(u.sync.getState().recovery).toBeNull();
    await vi.waitFor(async () => expect(await journal2.read(USER, 't')).toBeNull());
    expect(u.patches).toHaveLength(0);
  });

  it('rebases a recovered journal when the server moved on (disjoint edits)', async () => {
    const journal = new EditJournal({ idb: new IDBFactory(), storage: null });
    const mine: EditOp[] = [rename('Mine')];
    await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: run(base, mine), ops: mine, savedAt: 1, tabId: 'old-tab', userId: USER });
    const theirs: EditOp[] = [{ type: 'nudge', itemId: 't1', deltaFrames: 3 }];
    const t = setup({ journal, serverRevision: 8, serverDoc: run(base, theirs), history: [{ revision: 8, ops: theirs }] });
    t.store.replaceDocument(t.server.doc, 8);
    expect(await t.sync.checkRecovery()).toBe('offered');
    expect(await t.sync.restoreRecovered()).toBe('rebased');
    const saved = t.patches[0].request.document;
    expect(saved.tracks[0].items[0].name).toBe('Mine');
    expect(saved.tracks[1].items[0].recordIn).toBe(33);
  });

  it('drops a journal the server already holds (the save landed just before the tab died)', async () => {
    const journal = new EditJournal({ idb: new IDBFactory(), storage: null });
    const local = run(base, [rename('Landed')]);
    await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Landed')], savedAt: 1, tabId: 'old-tab', userId: USER });
    const t = setup({ journal, serverRevision: 8, serverDoc: local });
    t.store.replaceDocument(local, 8);
    expect(await t.sync.checkRecovery()).toBe('none');
    expect(await journal.read(USER, 't')).toBeNull();
  });

  it('never reads another user\'s journal, and deletes a pre-per-user (legacy key) entry', async () => {
    const local = run(base, [rename('Theirs')]);
    const storage = memoryStorage();
    const journal = new EditJournal({ idb: new IDBFactory(), storage });
    await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Theirs')], savedAt: 1, tabId: 'old-tab', userId: SOMEONE_ELSE });
    storage.set('reelbolt:editor:journal:t', JSON.stringify({ kind: 'entry', entry: { version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Old')], savedAt: 1, tabId: 'old-tab' } }));
    const t = setup({ journal });
    expect(await t.sync.checkRecovery()).toBe('none');
    expect(t.sync.getState().recovery ?? null).toBeNull();
    expect(storage.data.has('reelbolt:editor:journal:t')).toBe(false);
    expect(await journal.read(USER, 't')).toBeNull();
    expect((await journal.read(SOMEONE_ELSE, 't'))?.ops).toHaveLength(1); // still theirs, untouched
  });

  it('never reads another user\'s offline queue and does not send it; a legacy-key queue is deleted', async () => {
    const storage = memoryStorage();
    const local = run(base, [rename('Theirs')]);
    const theirs = JSON.stringify({ version: 1, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Theirs')], savedAt: 1, userId: SOMEONE_ELSE });
    storage.set(queueKey(SOMEONE_ELSE, 't'), theirs);
    storage.set('reelbolt:editor:offline-queue:t', theirs);
    const t = setup();
    const client = new SyncClient({ store: t.store, api: t.api, queueStorage: storage, journal: null, userId: () => USER });
    expect(await client.restoreQueued()).toBe('none');
    expect(storage.data.has('reelbolt:editor:offline-queue:t')).toBe(false);
    expect(storage.data.has(queueKey(SOMEONE_ELSE, 't'))).toBe(true);
    expect(t.patches).toHaveLength(0);
  });

  it('keeps the journal for the same user across a 401 and tags entries with the user', async () => {
    const t = setup({ userId: () => USER });
    t.failures.push(new ApiError('Unauthorized', 401));
    t.store.dispatch(rename('Mine'));
    await t.sync.flush();
    expect(JSON.parse(t.storage.data.get(journalKey(USER, 't')) ?? 'null').entry.userId).toBe(USER);
    await t.sync.resumeAfterSignIn();
    expect(t.patches).toHaveLength(2);
  });

  it('signing in as a different account does NOT flush the pending edits and says whose they are', async () => {
    let user = USER;
    const t = setup({ userId: () => user });
    t.failures.push(new ApiError('Unauthorized', 401));
    t.store.dispatch(rename('Not yours to send'));
    await t.sync.flush();
    expect(t.sync.getState().status).toBe('authExpired');
    user = OTHER;
    await t.sync.resumeAfterSignIn();
    expect(t.patches).toHaveLength(1);
    expect(t.sync.getState().accountChanged).toEqual({ previous: USER });
    t.sync.discardLocalEdits();
    expect(t.storage.data.has(journalKey(USER, 't'))).toBe(false);
  });

  it('a failed IndexedDB put flips durability to memory', async () => {
    const idb = new IDBFactory();
    const journal = new EditJournal({ idb, storage: null });
    await journal.warm();
    const t = setup({ journal });
    expect(await journal.probe()).toBe('device');
    const db = (journal as unknown as { opened: IDBDatabase }).opened;
    vi.spyOn(db, 'transaction').mockImplementation(() => {
      throw new Error('QuotaExceededError');
    });
    t.store.dispatch(rename('Too big'));
    t.fireTimers();
    await vi.waitFor(() => expect(t.sync.getState().durability).toBe('memory'));
    expect(await journal.probe()).toBe('memory');
  });

  it('logout wipes journal and queue keys and the IndexedDB database', async () => {
    const data = new Map<string, string>();
    const idb = new IDBFactory();
    vi.stubGlobal('indexedDB', idb);
    vi.stubGlobal('window', { localStorage: { get length() { return data.size; }, key: (i: number) => [...data.keys()][i] ?? null, removeItem: (k: string) => void data.delete(k), getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) } });
    try {
      data.set(journalKey(USER, 't'), '{}');
      data.set(queueKey(USER, 't'), '{}');
      data.set('unrelated', 'keep');
      const journal = new EditJournal({ idb, storage: null });
      await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: base, ops: [], savedAt: 1, tabId: 'x', userId: USER });
      (journal as unknown as { opened: IDBDatabase }).opened.close();
      await wipeEditorLocalData();
      expect([...data.keys()].sort()).toEqual(['reelbolt:editor:signed-out-at', 'unrelated']);
      const names = (await idb.databases()).map((d) => d.name);
      expect(names).not.toContain('reelbolt-editor');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('a 401 keeps the edit, pauses saving, and resumeAfterSignIn flushes it without a reload', async () => {
    const t = setup();
    t.failures.push(new ApiError('Unauthorized', 401));
    t.store.dispatch(rename('Behind a login'));
    await t.sync.flush();
    expect(t.sync.getState().status).toBe('authExpired');
    expect(describeSaveState(t.sync.getState())).toBe(SIGN_IN_AGAIN);
    expect(t.store.getState().dirty).toBe(true);
    expect(t.storage.data.has(journalKey(USER, 't'))).toBe(true);
    await t.sync.flush(); // paused: nothing is sent while the session is gone
    expect(t.patches).toHaveLength(1);
    await t.sync.resumeAfterSignIn();
    expect(t.patches).toHaveLength(2);
    expect(t.sync.getState().status).toBe('saved');
    expect(t.server.doc.tracks[0].items[0].name).toBe('Behind a login');
  });

  it('tells the truth when neither store works: edits are held in memory only', async () => {
    const t = setup({ journal: new EditJournal({ idb: null, storage: null }) });
    await vi.waitFor(() => expect(t.sync.getState().durability).toBe('memory'));
    t.store.dispatch(rename('x'));
    expect(describeSaveState(t.sync.getState())).toContain(MEMORY_ONLY);
  });
});

describe('R02.1 conflict recovery', () => {
  it('merges disjoint edits online after a 409 and stamps mergedAt for the toast', async () => {
    const theirs: EditOp[] = [{ type: 'nudge', itemId: 't1', deltaFrames: 3 }];
    const t = setup({ serverRevision: 8, serverDoc: run(base, theirs), history: [{ revision: 8, ops: theirs }] });
    t.store.dispatch(rename('Mine'));
    await t.sync.flush();
    expect(t.patches.map((p) => p.request.baseRevision)).toEqual([7, 8]);
    expect(t.sync.getState().status).toBe('saved');
    expect(t.sync.getState().mergedAt).toBeTruthy();
    expect(t.server.revision).toBe(9);
    const saved = t.server.doc;
    expect(saved.tracks[0].items[0].name).toBe('Mine');
    expect(saved.tracks[1].items[0].recordIn).toBe(33);
  });

  it('an overlap becomes a conflict with a reason, keeps the edits, and sends nothing more', async () => {
    const theirs: EditOp[] = [{ type: 'setProperty', itemId: 'c1', key: 'name', value: 'Theirs' }];
    const t = setup({ serverRevision: 8, serverDoc: run(base, theirs), history: [{ revision: 8, ops: theirs }] });
    t.store.dispatch(rename('Mine'));
    await t.sync.flush();
    const state = t.sync.getState();
    expect(state.status).toBe('conflict');
    expect(state.rebaseReason).toMatch(/same/);
    expect(isReadOnlySave(state)).toBe(true);
    expect(t.patches).toHaveLength(1);
    expect(t.storage.data.has(journalKey(USER, 't'))).toBe(true);
    expect(t.sync.getLocalEntry().localDoc.tracks[0].items[0].name).toBe('Mine');
  });

  it('"Keep my copy" duplicates as "<name> (my edits)" and writes the local document into the copy', async () => {
    const theirs: EditOp[] = [{ type: 'setProperty', itemId: 'c1', key: 'name', value: 'Theirs' }];
    const t = setup({ serverRevision: 8, serverDoc: run(base, theirs), history: [{ revision: 8, ops: theirs }] });
    t.store.dispatch(rename('Mine'));
    await t.sync.flush();
    const made = await t.sync.saveAsCopy();
    expect(made.id).toBe('copy-1');
    expect(made.name).toMatch(/ \(my edits\)$/);
    expect(t.duplicates[0].name).toBe(made.name);
    const copyPatch = t.patches.find((p) => p.timelineId === 'copy-1');
    expect(copyPatch?.request.baseRevision).toBe(1);
    expect(copyPatch?.request.document.id).toBe('copy-1');
    expect(copyPatch?.request.document.tracks[0].items[0].name).toBe('Mine');
    // The original timeline was never written by the copy.
    expect(t.server.doc.tracks[0].items[0].name).toBe('Theirs');
  });

  it('edits made while the server is being read are folded into the rebase, not lost', async () => {
    const theirs: EditOp[] = [{ type: 'nudge', itemId: 't1', deltaFrames: 3 }];
    const t = setup({ serverRevision: 8, serverDoc: run(base, theirs), history: [{ revision: 8, ops: theirs }] });
    const original = t.api.getEditTimeline;
    t.api.getEditTimeline = vi.fn(async (...args: Parameters<typeof original>) => {
      t.store.dispatch({ type: 'addMarker', marker: { id: 'mid', frame: 9, name: 'during', color: null, note: null, durationFrames: null } });
      return original(...args);
    });
    t.store.dispatch(rename('Mine'));
    await t.sync.flush();
    await t.sync.flush();
    expect(t.sync.getState().status).toBe('saved');
    expect(t.server.doc.markers.some((m) => m.id === 'mid')).toBe(true);
    expect(t.server.doc.tracks[0].items[0].name).toBe('Mine');
  });
});

describe('R02.3 the chip says each failure in plain words', () => {
  const chip = async (failure: unknown, extra?: () => void) => {
    const t = setup();
    t.failures.push(failure);
    extra?.();
    t.store.dispatch(rename('x'));
    await t.sync.flush();
    return t.sync.getState();
  };

  it('server error (after the retries): a sentence and Retry-able, no counters', async () => {
    const state = await chip(new ApiError('boom', 503));
    expect(state).toMatchObject({ status: 'error', problem: 'serverError' });
    expect(describeSaveState(state)).toBe('Not saved. The server had a problem.');
  });
  it('too large (413)', async () => {
    const state = await chip(new ApiError('big', 413));
    expect(state).toMatchObject({ status: 'rejected', problem: 'tooLarge' });
    expect(describeSaveState(state)).toMatch(/^Not saved\. This timeline is too large to save\./);
  });
  it('no permission (403)', async () => {
    const state = await chip(new ApiError('nope', 403));
    expect(describeSaveState(state)).toBe('Not saved. You no longer have permission to change this timeline.');
  });
  it('timeline deleted (404)', async () => {
    const state = await chip(new ApiError('gone', 404));
    expect(describeSaveState(state)).toBe('Not saved. This timeline was deleted.');
  });
  it('rejected document: the plain reason follows the sentence', async () => {
    const state = await chip(new ApiError('bad', 400, [], [{ path: 'tracks[0].items[1].recordIn', message: 'overlaps the previous clip' }]));
    expect(state.status).toBe('rejected');
    expect(describeSaveState(state)).toBe('Not saved. The server refused this change. Track 1, clip 2: overlaps the previous clip.');
  });
  it('session expired (401)', async () => {
    expect(describeSaveState(await chip(new ApiError('no', 401)))).toBe('Sign in again to keep saving');
  });
  it('offline', () => {
    const state = { status: 'offline', queuedOps: 2 } as SaveState;
    expect(describeSaveState(state)).toBe('You are offline. 2 changes will save when you are back');
    expect(describeSaveState({ ...state, queuedOps: 1 })).toBe('You are offline. 1 change will save when you are back');
  });
  it('conflict, saved, saving, unsaved, retrying, other tab, view only never show a revision counter', () => {
    const states: SaveState[] = (['saved', 'dirty', 'saving', 'retrying', 'conflict', 'otherTab', 'takenOver', 'reconnecting'] as const).map((status) => ({ status, revision: 41, attempt: 3 } as SaveState));
    for (const state of states) expect(describeSaveState(state)).not.toMatch(/\d|rev/i);
    expect(describeSaveState(states[0])).toBe('Saved');
    expect(describeSaveState(states[4])).toBe('Not saved. This timeline was changed somewhere else.');
    expect(describeSaveState({ status: 'saved', revision: 1, viewOnly: true } as SaveState)).toBe('View only');
  });
});

describe('R02.5 view-only open', () => {
  it('is frozen from the start and never PATCHes or journals', async () => {
    const t = setup({ canEdit: false });
    expect(t.store.getState().readOnly).toBe(true);
    expect(isReadOnlySave(t.sync.getState())).toBe(true);
    expect(() => t.store.dispatch(rename('nope'))).toThrow(/read-only/);
    t.sync.attachOps([{ type: 'restore', revision: 1 }]);
    await t.sync.flush();
    t.fireTimers();
    expect(t.patches).toHaveLength(0);
    expect(t.storage.data.size).toBe(0);
    expect(await t.sync.checkRecovery()).toBe('none');
    expect(await t.sync.restoreQueued()).toBe('none');
  });

  describe('T-014 account guard on every send path', () => {
    const switchTo = (t: ReturnType<typeof setup>, ref: { user: string }, user: string) => {
      ref.user = user;
      return t;
    };

    it('flush: a different account refuses to PATCH, sets accountChanged and writes nothing for the new user', async () => {
      const ref = { user: USER };
      const t = setup({ userId: () => ref.user });
      t.store.dispatch(rename('Mine'));
      switchTo(t, ref, OTHER);
      await t.sync.flush();
      expect(t.patches).toHaveLength(0);
      expect(t.sync.getState().accountChanged).toEqual({ previous: USER });
      expect(t.sync.getState().status).toBe('authExpired');
      expect([...t.storage.data.keys()].some((k) => k.includes(OTHER))).toBe(false);
    });

    it('retry timer: a retry after the account changed is not sent', async () => {
      const ref = { user: USER };
      const t = setup({ userId: () => ref.user });
      t.failures.push(new ApiError('boom', 503));
      t.store.dispatch(rename('Mine'));
      await t.sync.flush();
      ref.user = OTHER;
      await t.sync.flush();
      expect(t.patches).toHaveLength(1);
    });

    it('keepalive / unload flush: no fetch at all under another account', async () => {
      const ref = { user: USER };
      const t = setup({ userId: () => ref.user });
      t.store.dispatch(rename('Mine'));
      ref.user = OTHER;
      const target = Object.assign(new EventTarget(), { document: new EventTarget() });
      t.sync.attachLifecycle(target as unknown as Window);
      target.dispatchEvent(new Event('beforeunload'));
      target.dispatchEvent(new Event('pagehide'));
      await t.sync.flush();
      expect(t.patches).toHaveLength(0);
      expect(t.api.patchEditTimeline).not.toHaveBeenCalled();
    });

    it('reconnect: the offline queue is not replayed under another account', async () => {
      const ref = { user: USER };
      const storage = memoryStorage();
      const online = false;
      const t = setup();
      const client = new SyncClient({ store: t.store, api: t.api, queueStorage: storage, journal: null, userId: () => ref.user, isOnline: () => online, debounceMs: 1, setTimeout: () => null, clearTimeout: () => {} });
      t.failures.push(new TypeError('Failed to fetch'));
      t.store.dispatch(rename('Offline edit'));
      await client.flush();
      expect(client.getState().status).toBe('offline');
      const sentBefore = t.patches.length; // the one failed attempt that made it go offline
      ref.user = OTHER;
      await client.reconnect();
      expect(t.patches).toHaveLength(sentBefore);
      expect(t.api.getEditTimeline).not.toHaveBeenCalled();
      expect(client.getState().accountChanged).toEqual({ previous: USER });
    });

    it('offline-queue replay at startup (restoreQueued) is refused under another account', async () => {
      const storage = memoryStorage();
      const ref = { user: USER };
      const local = run(base, [rename('Queued')]);
      storage.set(queueKey(USER, 't'), JSON.stringify({ version: 1, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: local, ops: [rename('Queued')], savedAt: 1, userId: USER }));
      const t = setup();
      const client = new SyncClient({ store: t.store, api: t.api, queueStorage: storage, journal: null, userId: () => ref.user });
      ref.user = OTHER;
      expect(await client.restoreQueued()).toBe('refused');
      expect(t.patches).toHaveLength(0);
      expect(client.getState().accountChanged).toEqual({ previous: USER });
    });

    it('journal replay (restoreRecovered) is refused under another account', async () => {
      const ref = { user: USER };
      const journal = new EditJournal({ idb: new IDBFactory(), storage: null });
      await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: run(base, [rename('Recovered')]), ops: [rename('Recovered')], savedAt: 1, tabId: 'old-tab', userId: USER });
      const t = setup({ journal, userId: () => ref.user });
      expect(await t.sync.checkRecovery()).toBe('offered');
      ref.user = OTHER;
      expect(await t.sync.restoreRecovered()).toBe('refused');
      expect(t.patches).toHaveLength(0);
    });

    it('saveAsCopy: no duplicate and no PATCH under another account', async () => {
      const ref = { user: USER };
      const t = setup({ userId: () => ref.user });
      t.store.dispatch(rename('Mine'));
      ref.user = OTHER;
      await expect(t.sync.saveAsCopy()).rejects.toThrow(/different account/);
      expect(t.duplicates).toHaveLength(0);
      expect(t.patches).toHaveLength(0);
    });

    it('anonymous never matches anonymous: nothing is sent, the state is "sign in again"', async () => {
      const t = setup({ userId: () => ANONYMOUS_SCOPE });
      t.store.dispatch(rename('Mine'));
      await t.sync.flush();
      expect(t.patches).toHaveLength(0);
      expect(t.sync.getState().status).toBe('authExpired');
      expect(describeSaveState(t.sync.getState())).toBe(SIGN_IN_AGAIN);
    });

    it('anonymous now (cookie lost) with a real session user is refused too', async () => {
      const ref = { user: USER };
      const t = setup({ userId: () => ref.user });
      t.store.dispatch(rename('Mine'));
      ref.user = ANONYMOUS_SCOPE;
      await t.sync.flush();
      expect(t.patches).toHaveLength(0);
      expect(t.sync.getState().status).toBe('authExpired');
    });

    it('resume as a different account with NOTHING pending rebuilds the editor for the new identity', async () => {
      const ref = { user: USER };
      const rebuilt = vi.fn();
      const t = setup({ userId: () => ref.user });
      const client = new SyncClient({ store: t.store, api: t.api, queueStorage: null, journal: null, userId: () => ref.user, onIdentityChanged: rebuilt });
      (client as unknown as { setState: (p: object) => void }).setState({ status: 'authExpired' });
      ref.user = OTHER;
      await client.resumeAfterSignIn();
      expect(rebuilt).toHaveBeenCalledTimes(1);
      expect(t.patches).toHaveLength(0);
    });

    it('per-user keys: B never reaches A\'s journal entry by key or by read', async () => {
      const storage = memoryStorage();
      const journal = new EditJournal({ idb: new IDBFactory(), storage });
      await journal.write({ version: JOURNAL_VERSION, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: base, ops: [], savedAt: 1, tabId: 'x', userId: USER_A });
      expect(storage.data.has(journalKey(USER_A, 't'))).toBe(true);
      expect(await journal.read(USER_B, 't')).toBeNull();
      expect((await journal.read(USER_A, 't'))?.userId).toBe(USER_A);
      // even a forged entry planted under B's key but tagged A is rejected
      storage.set(journalKey(USER_B, 't'), storage.data.get(journalKey(USER_A, 't')) ?? '');
      expect(await journal.read(USER_B, 't')).toBeNull();
    });
  });
});
