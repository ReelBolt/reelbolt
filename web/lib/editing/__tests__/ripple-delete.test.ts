import { describe, expect, it } from 'vitest';
import { apply } from '../engine';
import { rippleDeletePlan } from '../ripple-delete';
import type { EditTimeline, Item } from '../types';

const clip = (id: string, recordIn: number, recordOut: number, file = 'f1', sourceIn = 0): Item =>
  ({ type: 'mediaClip', id, name: id, recordIn, recordOut, assetRef: { kind: 'projectFile', projectFileId: file, stepResultId: null }, sourceIn, playbackRate: 1, enabled: true }) as unknown as Item;

const track = (id: string, kind: 'video' | 'audio', items: Item[], locked = false) => ({ id, kind, name: id, color: null, height: 64, locked, enabled: true, mute: false, solo: false, busId: null, items });

const doc = (tracks: ReturnType<typeof track>[]): EditTimeline =>
  ({ schemaVersion: 2, id: 't', projectId: 'p', name: 't', settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 } }, tracks, markers: [] }) as unknown as EditTimeline;

const run = (d: EditTimeline, ops: ReturnType<typeof rippleDeletePlan>['ops']) => ops.reduce((acc, op) => apply(acc, op).doc, d);
const starts = (d: EditTimeline) => d.tracks.map((t) => t.items.map((i) => [i.id, i.recordIn]));

describe('rippleDeletePlan', () => {
  const paired = () =>
    doc([
      track('v1', 'video', [clip('a', 0, 60), clip('b', 60, 120, 'f2')]),
      track('a1', 'audio', [clip('a-snd', 0, 60), clip('b-snd', 60, 120, 'f2')]),
    ]);

  it('takes the paired sound with the picture, so both tracks close up and stay in sync', () => {
    const d = paired();
    const plan = rippleDeletePlan(d, ['a'], () => false);
    expect(plan.itemCount).toBe(2);
    expect(plan.ops.map((o) => (o as { itemId: string }).itemId).sort()).toEqual(['a', 'a-snd']);
    expect(starts(run(d, plan.ops))).toEqual([[['b', 0]], [['b-snd', 0]]]);
  });

  it('leaves a clip of another source or another span alone', () => {
    const d = doc([
      track('v1', 'video', [clip('a', 0, 60), clip('b', 60, 120)]),
      track('a1', 'audio', [clip('music', 0, 120, 'song'), clip('other', 0, 60, 'f1', 30)]),
    ]);
    const plan = rippleDeletePlan(d, ['a'], () => false);
    expect(plan.ops).toHaveLength(1);
  });

  it('does not touch a paired clip on a locked track and says so', () => {
    const d = paired();
    const plan = rippleDeletePlan(d, ['a'], (id) => id === 'a1');
    expect(plan.ops).toHaveLength(1);
    expect(plan.skippedLocked).toBe(1);
  });

  it('does not extract a companion twice when both are selected, and orders right to left', () => {
    const d = paired();
    const plan = rippleDeletePlan(d, ['a', 'a-snd', 'b'], () => false);
    const ids = plan.ops.map((o) => (o as { itemId: string }).itemId);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toHaveLength(4);
    expect(starts(run(d, plan.ops))).toEqual([[], []]);
  });

  it('an unpaired selection is a plain ripple delete', () => {
    const d = doc([track('v1', 'video', [clip('a', 0, 60), clip('b', 60, 120, 'f2')])]);
    expect(starts(run(d, rippleDeletePlan(d, ['a'], () => false).ops))).toEqual([[['b', 0]]]);
  });
});
