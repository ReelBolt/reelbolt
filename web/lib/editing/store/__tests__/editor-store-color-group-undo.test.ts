/** @vitest-environment node */
/**
 * T-112: the editor store's `undo()` across the colour-group sequence that crashed it.
 *
 * `undo()` replays an entry's inverse program. For [group a clip, remove the clip, remove the group]
 * the group's restore runs before the inverse that brings the clip back; on the pre-fix engine the
 * third undo threw an `EditEngineError` straight out of `undo()` (the crash path the ticket names).
 */

import { describe, expect, it } from 'vitest';
import { createEditorStore } from '../editor-store';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';

const make = () =>
  createEditorStore({
    timelineId: 'timeline-1',
    projectId: 'project-1',
    doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60), media('c2', 60, 120)])]),
    revision: 1,
    now: () => 1,
  });

describe('T-112: undo across [group a clip, remove the clip, remove the group]', () => {
  it('restores the document with no escaping EditEngineError', () => {
    const store = make();
    const before = structuredClone(store.getState().doc);

    store.dispatch({ type: 'setColorGroup', groupId: 'g1', group: { id: 'g1', name: 'G', clipIds: ['c1'], grade: null } });
    store.dispatch({ type: 'lift', itemId: 'c1' });
    store.dispatch({ type: 'setColorGroup', groupId: 'g1', group: undefined });
    expect(store.getState().cursor).toBe(3);

    expect(store.undo()).toBe(true);
    expect(store.undo()).toBe(true);
    expect(store.undo()).toBe(true);

    expect(store.getState().doc).toStrictEqual(before);
    expect(store.getState().cursor).toBe(0);
    expect(store.canUndo()).toBe(false);
  });

  /**
   * S-LFR-14 (Security): `dispatchBatch`'s rollback replays a batch's inverses when a later op in the
   * batch is refused. It used to replay them WITHOUT the inverse exemption, so a batch that replaced a
   * dangling colour group threw the restore error out of the rollback and masked the real refusal.
   */
  it('a batch rollback over a dangling colour group does not throw and leaves the document unchanged', () => {
    const store = make();
    store.dispatch({ type: 'setColorGroup', groupId: 'g1', group: { id: 'g1', name: 'G', clipIds: ['c1'], grade: null } });
    store.dispatch({ type: 'lift', itemId: 'c1' });
    const before = structuredClone(store.getState().doc);

    // A batch whose first op replaces the (now dangling) group with LIVE members only — so its inverse
    // is the op that restores a dangling member, and the document it is applied to no longer tolerates
    // it — and whose second op is refused. The REAL refusal must surface; without the exemption the
    // rollback threw its own "not a media clip" error and masked it.
    let thrown: unknown;
    try {
      store.dispatchBatch(
        [
          { type: 'setColorGroup', groupId: 'g1', group: { id: 'g1', name: 'G2', clipIds: ['c2'], grade: null } },
          { type: 'setColorGroup', groupId: 'no-such-group', group: undefined },
        ],
        'replace then refuse',
      );
    } catch (error) {
      thrown = error;
    }

    const message = String((thrown as Error | undefined)?.message ?? '');
    expect(message).toContain('no colour group');
    expect(message).not.toContain('is not a media clip');
    expect(store.getState().doc).toStrictEqual(before);

    expect(store.getState().doc).toStrictEqual(before);
  });
});
