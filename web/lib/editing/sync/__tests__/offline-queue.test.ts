/** @vitest-environment node */
/**
 * P9 offline op queue — the reconciliation that must never apply a queued op blind:
 *   - fast-forward when the server has not moved;
 *   - a rebase only when both logs replay, touch disjoint parts AND commute (fast-check property:
 *     every `rebased` outcome equals both application orders and satisfies the invariants; every
 *     overlapping pair is refused);
 *   - refusal for a restore, a tampered queue, overlapping edits, an unreadable server log;
 *   - the SyncClient end to end: offline → queued (persisted) → reconnect → rebased PATCH against
 *     the server's revision, or `conflict` with the queue kept and no PATCH at all.
 */

import { describe, expect, it, vi } from 'vitest';
import fc from 'fast-check';
import { apply, applyAll, assertInvariants, type EditOp } from '../../engine';
import { createEditorStore } from '../../store/editor-store';
import { queueKey, reconcileQueue, readQueue, sameDocument, touchedParts, type QueueStorage } from '../offline-queue';
import { SyncClient } from '../sync-client';
import { userScopeFromEmail } from '../user-scope';
import type { EditTimelineSyncApi, PatchEditTimelineRequest, WireOp } from '../api';
import { standardDoc, TRACKS } from '../../__tests__/fixtures';

const USER = userScopeFromEmail('me@x.test');
import type { EditTimeline } from '../../types';

// These fixtures name things `c1`/`timeline-1`, and the production code under test checks documents
// with the strict engine invariants. Only the Guid id shape is relaxed here (it is pinned in
// `engine/__tests__/apply.contract.test.ts`); every structural invariant still runs.
vi.mock('../../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../../engine')>();
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(doc, { ids: 'opaque' }) };
});

const base = standardDoc();
const run = (doc: EditTimeline, ops: EditOp[]) => applyAll(doc, ops);

/** A small menu of always-meaningful ops over the standard fixture, chosen by an index. */
function opMenu(doc: EditTimeline, pick: number, seq: number): EditOp | null {
  const items = doc.tracks.flatMap((t) => t.items.map((i) => ({ item: i, track: t })));
  const choice = pick % 7;
  const target = items[Math.floor(pick / 7) % Math.max(1, items.length)];
  switch (choice) {
    case 0:
      return target ? { type: 'setProperty', itemId: target.item.id, key: 'name', value: `n${seq}` } : null;
    case 1:
      return target ? { type: 'nudge', itemId: target.item.id, deltaFrames: 1 + (pick % 3) } : null;
    case 2:
      return { type: 'addMarker', marker: { id: `mk-${seq}-${pick}`, frame: pick % 200, name: 'm', color: null, note: null, durationFrames: null } };
    case 3:
      return { type: 'setTrackProperty', trackId: doc.tracks[pick % doc.tracks.length].id, key: 'solo', value: pick % 2 === 0 };
    case 4:
      return target && target.item.recordOut - target.item.recordIn > 2 ? { type: 'blade', itemId: target.item.id, atFrame: target.item.recordIn + 1, newItemId: `bl-${seq}-${pick}` } : null;
    case 5:
      return target ? { type: 'lift', itemId: target.item.id } : null;
    default:
      return { type: 'setInOut', inOut: { inFrame: pick % 50, outFrame: 60 + (pick % 50) } };
  }
}

/** Builds a valid op sequence from picks (refused picks are skipped). */
function sequence(doc: EditTimeline, picks: number[], tag: string): { ops: EditOp[]; doc: EditTimeline } {
  let current = doc;
  const ops: EditOp[] = [];
  picks.forEach((pick, i) => {
    const op = opMenu(current, pick, Number.parseInt(tag, 36) * 100 + i);
    if (!op) return;
    try {
      current = apply(current, op).doc;
      ops.push(op);
    } catch {
      // refused by the engine: skipped, like a user's refused edit
    }
  });
  return { ops, doc: current };
}

describe('reconcileQueue', () => {
  it('fast-forwards when the server is still at the base revision', () => {
    const local = sequence(base, [0, 9], 'a');
    expect(reconcileQueue({ baseRevision: 5, baseDoc: base, localDoc: local.doc, ops: local.ops }, { revision: 5, doc: base }, null)).toEqual({ kind: 'fastForward' });
  });

  it('rebases disjoint edits that commute, and the result is both orders at once', () => {
    const localOps: EditOp[] = [{ type: 'setProperty', itemId: 'c1', key: 'name', value: 'Hook' }];
    const remoteOps: EditOp[] = [{ type: 'nudge', itemId: 't1', deltaFrames: 5 }, { type: 'addMarker', marker: { id: 'r1', frame: 10, name: 'r', color: null, note: null, durationFrames: null } }];
    const out = reconcileQueue({ baseRevision: 5, baseDoc: base, localDoc: run(base, localOps), ops: localOps }, { revision: 7, doc: run(base, remoteOps) }, remoteOps);
    expect(out.kind).toBe('rebased');
    if (out.kind !== 'rebased') return;
    expect(sameDocument(out.doc, run(run(base, remoteOps), localOps))).toBe(true);
    expect(sameDocument(out.doc, run(run(base, localOps), remoteOps))).toBe(true);
  });

  it('refuses overlapping edits, a server restore, a tampered queue and an unreadable server log', () => {
    const localOps: EditOp[] = [{ type: 'nudge', itemId: 'c3', deltaFrames: 2 }];
    const localDoc = run(base, localOps);
    const remoteOps: EditOp[] = [{ type: 'lift', itemId: 'c2' }];
    const remoteDoc = run(base, remoteOps);
    const entry = { baseRevision: 5, baseDoc: base, localDoc, ops: localOps as WireOp[] };
    expect(reconcileQueue(entry, { revision: 6, doc: remoteDoc }, remoteOps)).toMatchObject({ kind: 'refused', reason: expect.stringContaining('same track') });
    expect(reconcileQueue(entry, { revision: 6, doc: remoteDoc }, [{ type: 'restore', revision: 2 }])).toMatchObject({ kind: 'refused', reason: expect.stringContaining('restored') });
    expect(reconcileQueue({ ...entry, localDoc: base }, { revision: 6, doc: remoteDoc }, remoteOps)).toMatchObject({ kind: 'refused', reason: expect.stringContaining('no longer reproduce') });
    expect(reconcileQueue(entry, { revision: 6, doc: base }, remoteOps)).toMatchObject({ kind: 'refused', reason: expect.stringContaining('do not reproduce') });
    expect(reconcileQueue(entry, { revision: 6, doc: remoteDoc }, null)).toMatchObject({ kind: 'refused' });
    expect(reconcileQueue(entry, { revision: 4, doc: remoteDoc }, remoteOps)).toMatchObject({ kind: 'refused' });
  });

  it('claims a whole track for a ripple and only the item for a property write', () => {
    expect([...touchedParts({ type: 'extract', itemId: 'c2' }, [base])]).toEqual(expect.arrayContaining([`track:${TRACKS.v1}`, 'item:c2']));
    expect([...touchedParts({ type: 'setProperty', itemId: 'c2', key: 'name', value: 'x' }, [base])]).toEqual(['item:c2']);
    expect([...touchedParts({ type: 'lift', itemId: 'ghost' }, [base])]).toContain('*');
  });

  it('property: never applies blind — a rebase equals both orders and is valid; an overlap is always refused', () => {
    const seen = { rebased: 0, refused: 0 };
    fc.assert(
      fc.property(fc.array(fc.nat(400), { minLength: 1, maxLength: 5 }), fc.array(fc.nat(400), { minLength: 1, maxLength: 5 }), (localPicks, remotePicks) => {
        const local = sequence(base, localPicks, 'l');
        const remote = sequence(base, remotePicks, 'r');
        const out = reconcileQueue({ baseRevision: 3, baseDoc: base, localDoc: local.doc, ops: local.ops }, { revision: 3 + Math.max(1, remote.ops.length), doc: remote.doc }, remote.ops);
        if (out.kind === 'rebased') {
          seen.rebased++;
          assertInvariants(out.doc);
          expect(sameDocument(out.doc, run(remote.doc, local.ops))).toBe(true);
          expect(sameDocument(out.doc, run(local.doc, remote.ops))).toBe(true);
          const docs = [base, local.doc, remote.doc];
          const l = new Set(local.ops.flatMap((o) => [...touchedParts(o, docs)]));
          const r = new Set(remote.ops.flatMap((o) => [...touchedParts(o, docs)]));
          expect([...l].some((p) => r.has(p))).toBe(false);
        } else if (out.kind === 'refused') {
          seen.refused++;
          expect(out.reason.length).toBeGreaterThan(0);
        }
      }),
      { numRuns: 300 },
    );
    // Both branches are genuinely exercised, so neither half of the property is vacuous.
    expect(seen.rebased).toBeGreaterThan(10);
    expect(seen.refused).toBeGreaterThan(10);
  });
});

describe('SyncClient offline', () => {
  const memoryStorage = (): QueueStorage & { data: Map<string, string> } => {
    const data = new Map<string, string>();
    return { data, get: (k) => data.get(k) ?? null, set: (k, v) => void data.set(k, v), remove: (k) => void data.delete(k) };
  };

  function setup(server: { revision: number; doc: EditTimeline; history: Array<{ revision: number; ops: WireOp[] }> }) {
    let online = true;
    const patches: PatchEditTimelineRequest[] = [];
    const api: EditTimelineSyncApi = {
      patchEditTimeline: vi.fn(async (_p, _t, request) => {
        if (!online) throw new TypeError('Failed to fetch');
        patches.push(structuredClone(request));
        return { id: 't', projectId: 'p', name: 'n', revision: request.baseRevision + 1, document: request.document, seedStepResultId: null, createdAt: '', updatedAt: '' };
      }),
      getEditTimeline: vi.fn(async () => ({ id: 't', projectId: 'p', name: 'n', revision: server.revision, document: server.doc, seedStepResultId: null, createdAt: '', updatedAt: '' })),
      getEditTimelineHistory: vi.fn(async () => ({ id: 't', currentRevision: server.revision, entries: [...server.history].reverse().map((h) => ({ ...h, createdAt: '', hasSnapshot: false })), restorableRevisions: [] })),
    };
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: base, revision: 7 });
    const storage = memoryStorage();
    const sync = new SyncClient({ store, api, debounceMs: 1, queueStorage: storage, userId: () => USER, isOnline: () => online, setTimeout: () => null, clearTimeout: () => {} });
    return { store, sync, storage, patches, goOffline: () => { online = false; }, goOnline: () => { online = true; } };
  }

  it('queues while offline, then rebases onto a moved server and saves against ITS revision', async () => {
    const remoteOps: EditOp[] = [{ type: 'nudge', itemId: 't1', deltaFrames: 3 }];
    const t = setup({ revision: 8, doc: run(base, remoteOps), history: [{ revision: 8, ops: remoteOps }] });
    t.goOffline();
    t.store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'Offline name' });
    await t.sync.flush();
    expect(t.sync.getState().status).toBe('offline');
    t.store.dispatch({ type: 'addMarker', marker: { id: 'off', frame: 5, name: 'later', color: null, note: null, durationFrames: null } });
    expect(readQueue(t.storage, USER, 't')?.ops).toHaveLength(2);
    expect(t.sync.getState().queuedOps).toBe(2);

    t.goOnline();
    await t.sync.reconnect();
    expect(t.patches).toHaveLength(1);
    expect(t.patches[0].baseRevision).toBe(8);
    expect(t.patches[0].ops.map((o) => o.type)).toEqual(['setProperty', 'addMarker']);
    const saved = t.patches[0].document;
    expect(saved.tracks[1].items[0].recordIn).toBe(33);
    expect(saved.tracks[0].items[0].name).toBe('Offline name');
    expect(t.sync.getState().status).toBe('saved');
    expect(readQueue(t.storage, USER, 't')).toBeNull();
  });

  it('refuses to rebase overlapping edits: read-only, no PATCH, the queue kept for download', async () => {
    const remoteOps: EditOp[] = [{ type: 'lift', itemId: 'c2' }];
    const t = setup({ revision: 8, doc: run(base, remoteOps), history: [{ revision: 8, ops: remoteOps }] });
    t.goOffline();
    t.store.dispatch({ type: 'nudge', itemId: 'c3', deltaFrames: 2 });
    await t.sync.flush();
    t.goOnline();
    await t.sync.reconnect();
    expect(t.patches).toHaveLength(0);
    expect(t.sync.getState()).toMatchObject({ status: 'conflict', offlineRefused: true });
    expect(t.sync.getQueuedEntry()?.ops).toHaveLength(1);
  });

  it('restores a queue left by an earlier session and fast-forwards it when the server has not moved', async () => {
    const t = setup({ revision: 7, doc: base, history: [] });
    const ops: EditOp[] = [{ type: 'setProperty', itemId: 'c2', key: 'name', value: 'From last session' }];
    t.storage.set(queueKey(USER, 't'), JSON.stringify({ version: 1, projectId: 'p', timelineId: 't', baseRevision: 7, baseDoc: base, localDoc: run(base, ops), ops, savedAt: 1, userId: USER }));
    expect(await t.sync.restoreQueued()).toBe('sent');
    expect(t.patches[0]).toMatchObject({ baseRevision: 7, ops });
  });
});
