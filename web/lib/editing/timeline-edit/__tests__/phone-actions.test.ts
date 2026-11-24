/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { apply } from '../../engine';
import { createEditorStore } from '../../store';
import { doc, media, standardDoc, track, TRACKS } from '../../__tests__/fixtures';
import type { EditOp } from '../../engine';
import type { EditTimeline, MediaClip } from '../../types';
import { buildDeleteOps, buildDuplicateOps, buildLiftOps, buildMoveInOrderOps, buildSpeedOps, buildTrimStepOps, buildVolumeOps, foldHistorySince } from '..';
import { linkedOpsForDelete, linkedOpsForNudge } from '../../link';
import { newGuid } from '../../ids';

const run = (d: EditTimeline, ops: readonly EditOp[]) => ops.reduce((acc, op) => apply(acc, op).doc, d);
const clip = (d: EditTimeline, id: string) => d.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip | undefined;

describe('RM.5 phone actions: resulting document', () => {
  it('ripple delete closes the gap', () => {
    const d = standardDoc();
    const after = run(d, buildDeleteOps(d, ['c1']).ops);
    expect(clip(after, 'c1')).toBeUndefined();
    expect(clip(after, 'c2')!.recordIn).toBe(0);
  });
  it('lift leaves the gap', () => {
    const d = standardDoc();
    const after = run(d, buildLiftOps(d, ['c2']));
    expect(clip(after, 'c2')).toBeUndefined();
    expect(clip(after, 'c3')!.recordIn).toBe(150);
  });
  it('duplicate adds a copy', () => {
    const d = standardDoc();
    const r = buildDuplicateOps(d, ['c1'], newGuid);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = run(d, r.ops);
    expect(after.tracks.flatMap((t) => t.items).length).toBe(d.tracks.flatMap((t) => t.items).length + 1);
    expect(clip(after, r.newItemIds[0])).toBeDefined();
  });
  it('nudge moves the clip one frame', () => {
    const d = standardDoc();
    const after = run(d, linkedOpsForNudge(d, ['c3'], 1));
    expect(clip(after, 'c3')!.recordIn).toBe(151);
  });
  it('group delete removes a linked pair together', () => {
    const d = doc([track('v1', 'video', [media('v', 0, 60, 0, { linkId: 'L' })]), track('a1', 'audio', [media('a', 0, 60, 0, { linkId: 'L' })])]);
    const after = run(d, linkedOpsForDelete(d, ['v'], 'ripple').ops);
    expect(clip(after, 'v')).toBeUndefined();
    expect(clip(after, 'a')).toBeUndefined();
  });
});

describe('RM.5 move earlier / later', () => {
  it('later swaps with the next clip on the track', () => {
    const d = standardDoc();
    const r = buildMoveInOrderOps(d, ['c1'], 1);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = run(d, r.ops);
    expect([clip(after, 'c2')!.recordIn, clip(after, 'c2')!.recordOut]).toEqual([0, 60]);
    expect([clip(after, 'c1')!.recordIn, clip(after, 'c1')!.recordOut]).toEqual([60, 120]);
    expect(clip(after, 'c3')!.recordIn).toBe(150);
  });
  it('earlier keeps the gap between the two clips', () => {
    const d = standardDoc();
    const r = buildMoveInOrderOps(d, ['c3'], -1);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = run(d, r.ops);
    expect(clip(after, 'c3')!.recordIn).toBe(60);
    expect(clip(after, 'c2')!.recordIn).toBe(60 + 90 + 30);
  });
  it('refuses at the ends in plain words, and on a locked track', () => {
    const d = standardDoc();
    expect(buildMoveInOrderOps(d, ['c1'], -1)).toEqual({ ok: false, reason: 'This clip is already first on its track.' });
    expect(buildMoveInOrderOps(d, ['c3'], 1)).toEqual({ ok: false, reason: 'This clip is already last on its track.' });
    expect(buildMoveInOrderOps(d, ['c2'], 1, () => true).ok).toBe(false);
  });
});

describe('RM.5 trim sheet', () => {
  it('a frame stepper trims the end and a linked partner follows', () => {
    const d = doc([track('v1', 'video', [media('v', 0, 60, 0, { linkId: 'L' })]), track('a1', 'audio', [media('a', 0, 60, 0, { linkId: 'L' })])]);
    const r = buildTrimStepOps(d, 'v', 'out', 'ripple', -10);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = run(d, r.ops);
    expect(clip(after, 'v')!.recordOut).toBe(50);
    expect(clip(after, 'a')!.recordOut).toBe(50);
  });
  it('the in stepper moves the source in-point', () => {
    const d = standardDoc();
    const r = buildTrimStepOps(d, 'c2', 'in', 'ripple', 10);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(clip(run(d, r.ops), 'c2')!.sourceIn).toBe(110);
  });
  it('says plainly when the edge cannot move', () => {
    const d = standardDoc();
    const r = buildTrimStepOps(d, 'c1', 'in', 'ripple', -10); // already at source start
    expect(r.ok).toBe(false);
  });
  it('a session of presses folds into ONE undo step that restores the original', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });
    const before = structuredClone(store.getState().doc);
    const start = store.getState().cursor;
    for (const delta of [-1, -1, -10]) {
      const r = buildTrimStepOps(store.getState().doc, 'c2', 'out', 'ripple', delta);
      expect(r.ok).toBe(true);
      if (r.ok) store.dispatchBatch(r.ops, r.label);
    }
    expect(store.getState().cursor - start).toBe(3);
    const trimmed = structuredClone(store.getState().doc);
    expect(foldHistorySince(store, start, 'Trim')).toBe(3);
    expect(store.getState().cursor - start).toBe(1);
    expect(store.getState().doc).toEqual(trimmed);
    store.undo();
    expect(store.getState().doc).toEqual(before);
  });
});

describe('RM.5 speed and volume chips', () => {
  it('2x halves the clip in one step', () => {
    const d = standardDoc();
    const r = buildSpeedOps(d, 'c1', 2);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = clip(run(d, r.ops), 'c1')!;
    expect(after.playbackRate).toBe(2);
    expect(after.recordOut - after.recordIn).toBe(30);
  });
  it('refuses the speed the clip already has', () => {
    expect(buildSpeedOps(standardDoc(), 'c1', 1).ok).toBe(false);
  });
  it('volume 100% writes 0 dB on a sound clip, and a picture clip is told why not', () => {
    const d = standardDoc();
    const r = buildVolumeOps(d, 'a1', 100);
    expect(r.ok).toBe(true);
    if (r.ok) expect(clip(run(d, r.ops), 'a1')!.gainDb?.value).toBe(0);
    const bad = buildVolumeOps(d, 'c1', 100);
    expect(bad.ok).toBe(false);
    expect(buildVolumeOps(d, 'a1', 250).ok).toBe(false);
  });
});
void TRACKS;
