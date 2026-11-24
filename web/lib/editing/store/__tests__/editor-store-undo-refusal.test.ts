/** @vitest-environment node */
/**
 * T-112: a refused inverse program is a FAILED UNDO, never an `EditEngineError` escaping `undo()`.
 *
 * The engine composes its inverses so a replay restores the exact document it was built from; a
 * refusal means the document was changed underneath the stack. `undo()` must surface that as a
 * `false` return with the document, cursor, history and pending ops left byte-identical — the entry
 * stays applied and a later retry still works.
 *
 * `apply` is wrapped here so the refusal can be provoked deterministically (the colour-group case
 * that used to trigger it is now fixed, so no reachable sequence refuses an inverse any more).
 */

import { describe, expect, it, vi } from 'vitest';
import { createEditorStore } from '../editor-store';
import { caption, standardDoc, TRACKS } from '../../__tests__/fixtures';

const controls = vi.hoisted(() => ({ refuse: new Set<string>() }));

vi.mock('../../engine', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../engine')>();
  return {
    ...actual,
    apply: (document: Parameters<typeof actual.apply>[0], op: Parameters<typeof actual.apply>[1]) => {
      if (controls.refuse.has(op.type)) throw new actual.EditEngineError(`refused ${op.type}`, op.type);
      return actual.apply(document, op);
    },
  };
});

const make = () =>
  createEditorStore({
    timelineId: 'timeline-1',
    projectId: 'project-1',
    doc: standardDoc(),
    revision: 7,
    now: () => 1,
  });

/** Inserts into the V1 gap at 120..150; its inverse is a single `lift`. */
const insertGapItem = { type: 'insert', trackId: TRACKS.v1, index: 2, item: caption('x1', 130, 140) } as const;

describe('T-112: a refused inverse program surfaces as a failed undo', () => {
  it('returns false and leaves the document, cursor, history and pending ops intact', () => {
    const store = make();
    store.dispatch(insertGapItem);
    expect(store.getState().cursor).toBe(1);

    controls.refuse.add('lift'); // the inverse program is [{ lift, itemId: 'x1' }]
    const afterDispatch = structuredClone(store.getState().doc);

    expect(() => store.undo()).not.toThrow();
    expect(store.undo()).toBe(false);

    expect(store.getState().doc).toStrictEqual(afterDispatch);
    expect(store.getState().cursor).toBe(1);
    expect(store.getState().history).toHaveLength(1);
    expect(store.getState().pendingOps.map((o) => o.type)).toEqual(['insert']);

    // The entry is still applied, so a later retry succeeds once the refusal is gone.
    controls.refuse.clear();
    expect(store.undo()).toBe(true);
    expect(store.getState().cursor).toBe(0);
    expect(store.getState().doc.tracks[0].items.map((i) => i.id)).toEqual(['c1', 'c2', 'c3']);
  });

  it('jumpTo stops at the first refused step instead of looping or throwing', () => {
    const store = make();
    store.dispatch(insertGapItem);
    store.dispatch({ type: 'setProperty', itemId: 'c2', key: 'name', value: 'renamed' });
    expect(store.getState().cursor).toBe(2);

    controls.refuse.add('setProperty'); // the inverse of the second entry
    expect(() => store.jumpTo(0)).not.toThrow();
    expect(store.getState().cursor).toBe(2);
  });
});
