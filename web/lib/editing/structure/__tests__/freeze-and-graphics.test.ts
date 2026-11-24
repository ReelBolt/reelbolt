/** @vitest-environment node */
/** R07.5 freeze frame and R10.5 title / colour card builders. */
import { describe, expect, it } from 'vitest';
import { applyAll } from '../../engine';
import { EditorStore } from '../../store';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { freezeBlocker, freezeFrameOps } from '../freeze-frame';
import { buildGraphicOps, isHexColor } from '../graphics';

let n = 0;
const newId = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`;

describe('freezeFrameOps', () => {
  const linked = () =>
    doc([
      track(TRACKS.v1, 'video', [media('v', 0, 120, 10, { linkId: 'L' }), media('w', 120, 200)]),
      track(TRACKS.a1, 'audio', [media('s', 0, 120, 10, { linkId: 'L' })]),
    ]);

  it('splits, holds 60 frames, shifts the rest, silent, in one batch', () => {
    const d = linked();
    const plan = freezeFrameOps(d, 'v', 40, newId)!;
    const next = applyAll(d, plan.ops);
    const v = next.tracks[0].items;
    expect(v.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 40], [40, 100], [100, 180], [180, 260]]);
    expect(v[1]).toMatchObject({ hold: true, sourceIn: 50 });
    expect(next.tracks[1].items.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 40], [100, 180]]);
    const store = new EditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    store.dispatchBatch(plan.ops, 'Freeze frame');
    expect(store.undo()).toBe(true);
    expect(store.getState().doc).toEqual(d);
  });

  it('says why it cannot', () => {
    const d = linked();
    expect(freezeBlocker(d, null, 10)).toMatch(/select/i);
    expect(freezeBlocker(d, 'v', 0)).toMatch(/playhead/i);
    expect(freezeBlocker(d, 's', 10)).toMatch(/video/i);
  });
});

describe('buildGraphicOps', () => {
  const d = doc([track(TRACKS.v1, 'video', [media('a', 0, 100)]), track(TRACKS.v2, 'video', [])]);
  it('title keeps template, text and length', () => {
    const r = buildGraphicOps(d, { spec: { kind: 'title', text: 'Hi', template: 'lowerThird', seconds: 2 }, at: 30, newId });
    const item = applyAll(d, r.ops).tracks[1].items[0];
    expect(item).toMatchObject({ type: 'titleClip', text: 'Hi', template: 'lowerThird', recordIn: 30, recordOut: 90 });
    expect(r.landedAt).toMatchObject({ frame: 30, tc: '00:00:01:00' });
  });
  it('colour card stores the colour; the chosen track is honoured when free', () => {
    const r = buildGraphicOps(d, { spec: { kind: 'colorCard', color: '#1A73E8' }, at: 10, trackId: TRACKS.v2, newId });
    expect(applyAll(d, r.ops).tracks[1].items[0]).toMatchObject({ generatorType: 'color', generatorConfig: { color: '#1a73e8' } });
    const busy = buildGraphicOps(d, { spec: { kind: 'colorCard', color: '#000000' }, at: 10, trackId: TRACKS.v1, newId });
    expect(busy.ops[0]).toMatchObject({ type: 'placeOnTop', trackId: TRACKS.v2 });
  });
  it('validates hex', () => {
    expect(isHexColor('#12abEF')).toBe(true);
    expect(isHexColor('#12')).toBe(false);
  });
});
