/** @vitest-environment node */
/**
 * The editor store: the spine P3 hangs off.
 *
 *   1. dispatch applies through the engine and records an inverse program; undo/redo restore exactly.
 *   2. dispatchBatch is atomic: a refusal partway through leaves the document byte-identical.
 *   3. jumpTo walks the history list to any point; dispatching after an undo drops the redo tail.
 *   4. UI changes never produce an op, a history entry or dirtiness.
 *   5. markSaved trims only the sent prefix of pendingOps and keeps history.
 *   6. A UI-locked track refuses ops in the engine's own error type, before the engine runs.
 */

import { describe, expect, it } from 'vitest';
import { createEditorStore } from '../editor-store';
import { EditEngineError } from '../../engine';
import { standardDoc, TRACKS } from '../../__tests__/fixtures';
import { compileCreateCompound } from '../../structure/compound';

const make = () => createEditorStore({ timelineId: 'timeline-1', projectId: 'project-1', doc: standardDoc(), revision: 7, now: () => 1 });

describe('EditorStore document edits', () => {
  it('dispatch applies the op and undo/redo restore the document exactly', () => {
    const store = make();
    const before = structuredClone(store.getState().doc);

    store.dispatch({ type: 'blade', itemId: 'c1', atFrame: 30, newItemId: 'c1b' });
    expect(store.getState().doc.tracks[0].items.map((i) => i.id)).toEqual(['c1', 'c1b', 'c2', 'c3']);
    expect(store.getState().history).toHaveLength(1);
    expect(store.getState().cursor).toBe(1);
    expect(store.getState().dirty).toBe(true);
    expect(store.getState().pendingOps).toHaveLength(1);

    store.undo();
    expect(store.getState().doc).toEqual(before);
    expect(store.getState().cursor).toBe(0);
    // Undo appends the inverse program to the pending log — the server gets an honest op history.
    expect(store.getState().pendingOps.map((o) => o.type)).toEqual(['blade', 'lift', 'overwrite']);

    store.redo();
    expect(store.getState().doc.tracks[0].items.map((i) => i.id)).toEqual(['c1', 'c1b', 'c2', 'c3']);
    expect(store.getState().cursor).toBe(1);
  });

  it('dispatchBatch is atomic: a refusal partway through leaves the document untouched and throws', () => {
    const store = make();
    const before = structuredClone(store.getState().doc);

    expect(() =>
      store.dispatchBatch(
        [
          { type: 'blade', itemId: 'c1', atFrame: 30, newItemId: 'x1' },
          { type: 'blade', itemId: 'c2', atFrame: 90, newItemId: 'x2' },
          // A refusal: the frame is not strictly inside c3.
          { type: 'blade', itemId: 'c3', atFrame: 150, newItemId: 'x3' },
        ],
        'Blade all',
      ),
    ).toThrow(EditEngineError);

    expect(store.getState().doc).toEqual(before);
    expect(store.getState().history).toHaveLength(0);
    expect(store.getState().pendingOps).toHaveLength(0);
    expect(store.getState().dirty).toBe(false);
  });

  it('a batch is ONE history entry whose inverse restores the document', () => {
    const store = make();
    const before = structuredClone(store.getState().doc);
    store.dispatchBatch(
      [
        { type: 'blade', itemId: 'c1', atFrame: 30, newItemId: 'x1' },
        { type: 'lift', itemId: 'x1' },
        { type: 'setProperty', itemId: 'c2', key: 'name', value: 'renamed' },
      ],
      'Composite',
    );
    expect(store.getState().history).toHaveLength(1);
    expect(store.getState().history[0].forward).toHaveLength(3);
    store.undo();
    expect(store.getState().doc).toEqual(before);
  });

  it('jumpTo moves the cursor through the history list; dispatch after undo drops the redo tail', () => {
    const store = make();
    const d0 = structuredClone(store.getState().doc);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'one' });
    const d1 = structuredClone(store.getState().doc);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'two' });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'three' });

    store.jumpTo(1);
    expect(store.getState().doc).toEqual(d1);
    store.jumpTo(0);
    expect(store.getState().doc).toEqual(d0);
    store.jumpTo(3);
    expect(store.getState().doc.tracks[0].items[0].name).toBe('three');

    store.jumpTo(1);
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'branch' });
    expect(store.getState().history.map((h) => h.label)).toEqual(['Set name', 'Set name']);
    expect(store.canRedo()).toBe(false);
  });

  it('markSaved trims only the sent prefix and leaves history alone', () => {
    const store = make();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'one' });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'two' });
    expect(store.getState().pendingOps).toHaveLength(2);

    store.markSaved(8, 1);
    expect(store.getState().revision).toBe(8);
    expect(store.getState().pendingOps).toHaveLength(1);
    expect(store.getState().dirty).toBe(true);
    expect(store.getState().history).toHaveLength(2);

    store.markSaved(9);
    expect(store.getState().pendingOps).toHaveLength(0);
    expect(store.getState().dirty).toBe(false);
    expect(store.canUndo()).toBe(true);
  });
});

describe('EditorStore UI state', () => {
  it('setUi never produces an op, a history entry or dirtiness', () => {
    const store = make();
    const before = store.getState().doc;
    store.setUi({ playheadFrame: 42, selection: ['c1', 'c2'], pixelsPerFrame: 4, tool: 'blade', snapping: false, workspace: 'audio' });
    store.setUi((ui) => ({ inOut: { ...ui.inOut, inFrame: 10 } }));
    store.setTrackOverride(TRACKS.a1, { mute: true });

    const s = store.getState();
    expect(s.doc).toBe(before);
    expect(s.history).toHaveLength(0);
    expect(s.pendingOps).toHaveLength(0);
    expect(s.dirty).toBe(false);
    expect(s.ui.playheadFrame).toBe(42);
    expect(s.ui.inOut.inFrame).toBe(10);
    expect(s.ui.workspace).toBe('audio');
    expect(store.effectiveTrackFlags(s.doc.tracks[2]).mute).toBe(true);
  });

  it('a UI-locked track refuses ops before the engine runs, in the engine error type', () => {
    const store = make();
    store.setTrackOverride(TRACKS.v1, { locked: true });
    expect(() => store.dispatch({ type: 'blade', itemId: 'c1', atFrame: 30, newItemId: 'x' })).toThrow(EditEngineError);
    expect(() => store.dispatch({ type: 'move', itemId: 't1', toTrackId: TRACKS.v1, toRecordIn: 300 })).toThrow(/locked in the editor/);
    expect(store.getState().history).toHaveLength(0);
    // Other tracks still edit.
    store.dispatch({ type: 'setProperty', itemId: 't1', key: 'text', value: 'ok' });
    expect(store.getState().history).toHaveLength(1);
  });

  it('notifies subscribers on every commit and stops after unsubscribe', () => {
    const store = make();
    let count = 0;
    const off = store.subscribe(() => {
      count += 1;
    });
    store.setUi({ playheadFrame: 1 });
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'n' });
    expect(count).toBe(2);
    off();
    store.setUi({ playheadFrame: 2 });
    expect(count).toBe(2);
  });

  it('replaceDocument clears history and pending ops', () => {
    const store = make();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'n' });
    const fresh = standardDoc();
    store.replaceDocument(fresh, 12);
    expect(store.getState().doc).toBe(fresh);
    expect(store.getState().revision).toBe(12);
    expect(store.getState().history).toHaveLength(0);
    expect(store.getState().dirty).toBe(false);
  });

  // T-073 / GUARD-COMPOUND: a selection naming an item the timeline no longer holds made "Split at
  // playhead" target nothing, so the edit after "Make compound" silently never happened or saved.
  it('drops selected ids the document no longer holds after an edit (Make compound, lift)', () => {
    const store = make();
    store.setUi({ selection: ['c1', 't1'] });
    const ids = { sequenceId: '9f0c1a52-3b7e-4d6a-8c21-5e4f3a2b1c0d', compoundId: '0b6f2e19-7c4d-4a85-9e13-2d5c6b7a8f90' };
    store.dispatchBatch(compileCreateCompound(store.getState().doc, ['c1'], ids), 'Make compound');
    expect(store.getState().ui.selection).toEqual(['t1']);

    store.dispatch({ type: 'lift', itemId: 't1' });
    expect(store.getState().ui.selection).toEqual([]);

    // Undo brings the item back but does not invent a selection; a pure view change keeps its ids as given.
    store.undo();
    expect(store.getState().ui.selection).toEqual([]);
    store.setUi({ selection: ['t1', 'c2'] });
    expect(store.getState().ui.selection).toEqual(['t1', 'c2']);
  });
});

describe('EditorStore read-only (eng D12, ui1 D7)', () => {
  it('while read-only, no document write path changes the document; view state still moves', () => {
    const store = make();
    store.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'before' });
    store.setReadOnly(true);
    const frozen = store.getState().doc;
    const cursor = store.getState().cursor;

    // Delete / Ctrl+B / Remove-silence apply all go through dispatch or dispatchBatch.
    expect(() => store.dispatch({ type: 'lift', itemId: 'c2' })).toThrow(EditEngineError);
    expect(() => store.dispatch({ type: 'blade', itemId: 'c1', atFrame: 30, newItemId: 'c1b' })).toThrow(/read-only/);
    expect(() => store.dispatchBatch([{ type: 'lift', itemId: 'c3' }], 'Remove silence')).toThrow(EditEngineError);
    // Ctrl+Z / Ctrl+Shift+Z / History panel jumps.
    expect(store.canUndo()).toBe(false);
    store.undo();
    store.redo();
    store.jumpTo(0);
    // History restore replaces the document wholesale.
    expect(() => store.replaceDocument(standardDoc(), 9, { markDirty: true })).toThrow(EditEngineError);

    expect(store.getState().doc).toBe(frozen);
    expect(store.getState().cursor).toBe(cursor);

    store.setUi({ playheadFrame: 12, selection: ['c1'] });
    expect(store.getState().ui.playheadFrame).toBe(12);

    store.setReadOnly(false);
    store.undo();
    expect(store.getState().doc).not.toBe(frozen);
  });
});
