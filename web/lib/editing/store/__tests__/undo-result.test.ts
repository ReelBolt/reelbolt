/** @vitest-environment node */
/**
 * S13: Undo / Redo buttons report a refused step instead of looking like they did nothing.
 * (A refused inverse is provoked by wrapping `apply`, as editor-store-undo-refusal.test.ts does.)
 */
import { describe, expect, it, vi } from 'vitest';
import { createEditorStore } from '../editor-store';
import { redoStep, undoStep } from '../undo-result';
import { caption, standardDoc, TRACKS } from '../../__tests__/fixtures';

const controls = vi.hoisted(() => ({ refuse: new Set<string>() }));

vi.mock('../../engine', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../engine')>();
  return {
    ...actual,
    apply: (document: Parameters<typeof actual.apply>[0], op: Parameters<typeof actual.apply>[1], options?: Parameters<typeof actual.apply>[2]) => {
      if (controls.refuse.has(op.type)) throw new actual.EditEngineError(`refused ${op.type}`, op.type);
      return actual.apply(document, op, options);
    },
  };
});

const make = () => createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1, now: () => 1 });
const insertGapItem = { type: 'insert', trackId: TRACKS.v1, index: 2, item: caption('x1', 130, 140) } as const;

describe('undoStep / redoStep', () => {
  it('reports nothing when there is nothing to step', () => {
    const store = make();
    expect(undoStep(store)).toEqual({ status: 'nothing' });
    expect(redoStep(store)).toEqual({ status: 'nothing' });
  });

  it('steps and says done', () => {
    const store = make();
    store.dispatch(insertGapItem);
    expect(undoStep(store)).toEqual({ status: 'done' });
    expect(store.getState().cursor).toBe(0);
    expect(redoStep(store)).toEqual({ status: 'done' });
    expect(store.getState().cursor).toBe(1);
  });

  it('a refused inverse comes back as a sentence, with the document untouched', () => {
    const store = make();
    store.dispatch(insertGapItem);
    const before = structuredClone(store.getState().doc);
    controls.refuse.add('lift');
    const result = undoStep(store);
    controls.refuse.clear();
    expect(result.status).toBe('refused');
    if (result.status === 'refused') {
      expect(result.title).toBe('Could not undo');
      expect(result.message).toMatch(/Nothing was altered/);
    }
    expect(store.getState().doc).toStrictEqual(before);
    expect(undoStep(store)).toEqual({ status: 'done' });
  });
});
