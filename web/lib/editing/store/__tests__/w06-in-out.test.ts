/** @vitest-environment node */
/**
 * W0.6 (P4-OPS-12): in/out marks persist to the document through `setInOut`, so a reload keeps them
 * and the Deliver range reads them. A half-set pair (only In) stays view state until it is complete.
 */

import { describe, expect, it } from 'vitest';
import { createEditorStore, marksOf } from '../editor-store';
import { standardDoc } from '../../__tests__/fixtures';

const make = (doc = standardDoc()) => createEditorStore({ timelineId: 'timeline-1', projectId: 'project-1', doc, revision: 1, now: () => 1 });

describe('in/out marks persist to the document', () => {
  it('a complete pair dispatches one setInOut op; a half pair stays view-only', () => {
    const store = make();
    store.setInOutMarks({ inFrame: 10, outFrame: null });
    expect(store.getState().doc.inOut ?? null).toBeNull();
    expect(store.getState().pendingOps).toHaveLength(0);
    expect(store.getState().ui.inOut).toEqual({ inFrame: 10, outFrame: null });

    store.setInOutMarks({ inFrame: 10, outFrame: 40 });
    expect(store.getState().doc.inOut).toEqual({ inFrame: 10, outFrame: 40 });
    expect(store.getState().pendingOps.map((o) => o.type)).toEqual(['setInOut']);
    expect(store.getState().history).toHaveLength(1);

    // Same marks again: no second op.
    store.setInOutMarks({ inFrame: 10, outFrame: 40 });
    expect(store.getState().pendingOps).toHaveLength(1);
  });

  it('clearing the marks clears the stored range; undo brings the marks back on screen', () => {
    const store = make();
    store.setInOutMarks({ inFrame: 5, outFrame: 25 });
    store.setInOutMarks({ inFrame: null, outFrame: null });
    expect(store.getState().doc.inOut ?? null).toBeNull();
    store.undo();
    expect(store.getState().doc.inOut).toEqual({ inFrame: 5, outFrame: 25 });
    expect(store.getState().ui.inOut).toEqual({ inFrame: 5, outFrame: 25 });
  });

  it('a reloaded document opens with its stored marks', () => {
    const doc = { ...standardDoc(), inOut: { inFrame: 12, outFrame: 48 } };
    const store = make(doc);
    expect(store.getState().ui.inOut).toEqual({ inFrame: 12, outFrame: 48 });
    expect(marksOf(doc)).toEqual({ inFrame: 12, outFrame: 48 });
    store.replaceDocument({ ...standardDoc(), inOut: null }, 2);
    expect(store.getState().ui.inOut).toEqual({ inFrame: null, outFrame: null });
  });

  it('persist:false (read-only) keeps the marks on screen without an op', () => {
    const store = make();
    store.setInOutMarks({ inFrame: 1, outFrame: 9 }, { persist: false });
    expect(store.getState().pendingOps).toHaveLength(0);
    expect(store.getState().ui.inOut).toEqual({ inFrame: 1, outFrame: 9 });
  });
});
