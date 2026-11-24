import { describe, expect, it } from 'vitest';
import { applyAll, KEYFRAME_PARAM_RANGE, type EditOp } from '../../engine';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';
import { availableEventActions, EVENT_ACTION_ORDER, HIDDEN_EVENT_ACTIONS, planEventAction, seededNoise, type EventActionId } from '../event-actions';
import type { LaneEvent } from '../events-data';

const FILE = assetRef(1).projectFileId;
const mk = (frame: number, extra: Partial<LaneEvent> = {}): LaneEvent => ({ key: `${FILE}:e3:${frame}`, fileId: FILE, eventId: 'e3', clipId: 'c1', trackId: 'v1', type: 'hit', frame, sourceSec: frame / 30, strength: 0.8, region: { x: 0.6, y: 0.4, w: 0.2, h: 0.2 }, ...extra });
const ids = () => { let n = 0; return () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`; };
const base = (): EditTimeline => doc([track('v1', 'video', [media('c1', 0, 300, 0)]), track('a1', 'audio', [media('a1c', 0, 300, 0)])]);
const ops = (id: EventActionId, d = base(), e = mk(90)) => {
  const plan = planEventAction(id, { doc: d, event: e, newId: ids() });
  if (plan.kind !== 'ops') throw new Error(`no ops for ${id}: ${plan.kind}`);
  return plan.ops;
};
const types = (list: EditOp[]) => list.map((o) => o.type);

describe('Act on this event: golden programs over existing ops', () => {
  it('Zoom here: scale keys up to 1.25x on the event, centred on its region, held then eased back', () => {
    const list = ops('zoom');
    expect(list.filter((o) => o.type === 'setKeyframe' && o.param === 'scale').map((o) => (o as { frame: number; value: number }).frame)).toEqual([87, 90, 114, 126]);
    const scale = list.filter((o): o is Extract<EditOp, { type: 'setKeyframe' }> => o.type === 'setKeyframe' && o.param === 'scale');
    expect(scale.map((k) => k.value)).toEqual([1, 1.25, 1.25, 1]);
    const px = list.filter((o): o is Extract<EditOp, { type: 'setKeyframe' }> => o.type === 'setKeyframe' && o.param === 'positionX');
    expect(px.map((k) => k.value)).toEqual([0, -0.125, -0.125, 0]);
    expect(new Set(types(list))).toEqual(new Set(['setKeyframe']));
  });
  it('Zoom here without a region only scales', () => {
    const list = ops('zoom', base(), mk(90, { region: null }));
    expect(list.every((o) => o.type === 'setKeyframe' && o.param === 'scale')).toBe(true);
  });
  it('Pan here: keys for a pan over one second with the headroom a pan needs', () => {
    const list = ops('pan', base(), mk(90, { region: null }));
    const x = list.filter((o): o is Extract<EditOp, { type: 'setKeyframe' }> => o.type === 'setKeyframe' && o.param === 'positionX');
    expect(x.map((k) => [k.frame, k.value])).toEqual([[90, 0], [120, -0.08]]);
    expect(list.some((o) => o.type === 'setKeyframe' && o.param === 'scale' && o.value === 1.2)).toBe(true);
  });
  it('Shake: deterministic for the same event, different for another, always inside the channel ranges', () => {
    const a = ops('shake');
    expect(JSON.stringify(ops('shake'))).toBe(JSON.stringify(a));
    expect(JSON.stringify(ops('shake', base(), mk(90, { key: 'other' })))).not.toBe(JSON.stringify(a));
    for (const o of a) {
      if (o.type !== 'setKeyframe') throw new Error('shake writes keyframes only');
      const range = KEYFRAME_PARAM_RANGE[o.param];
      expect(o.value).toBeGreaterThanOrEqual(range.min);
      expect(o.value).toBeLessThanOrEqual(range.max);
    }
    expect(seededNoise('x')()).toBe(seededNoise('x')());
  });
  it('Flash: a white colour card of a few frames on a free video track above', () => {
    const list = ops('flash', doc([track('v1', 'video', [media('c1', 0, 300, 0)])]));
    expect(list).toHaveLength(1);
    expect(list[0]).toMatchObject({ type: 'addTrack', index: 1, track: { kind: 'video', items: [{ type: 'generatorClip', generatorType: 'color', recordIn: 90, recordOut: 93, generatorConfig: { color: '#ffffff' } }] } });
  });
  it('Add title here: the same program as Add title at the playhead, at the event frame', () => {
    const list = ops('addTitle');
    expect(list[0]).toMatchObject({ type: 'addTrack', track: { items: [{ type: 'titleClip', recordIn: 90, recordOut: 180 }] } });
  });
  it('Cut here: splits the clip and its linked partner at the event frame', () => {
    const linked = doc([track('v1', 'video', [media('c1', 0, 300, 0, { linkId: 'L' })]), track('a1', 'audio', [media('a1c', 0, 300, 0, { linkId: 'L' })])]);
    const list = ops('cut', linked);
    expect(list.filter((o) => o.type === 'blade')).toHaveLength(2);
    expect(list.every((o) => o.type !== 'blade' || o.atFrame === 90)).toBe(true);
    const out = applyAll(linked, list);
    expect(out.tracks[0].items.map((i) => [i.recordIn, i.recordOut])).toEqual([[0, 90], [90, 300]]);
  });
  it('Speed ramp: isolates half a second from the event, slows it to half speed and ripples the rest', () => {
    const d = base();
    const list = ops('speedRamp', d);
    expect(new Set(types(list))).toEqual(new Set(['blade', 'retime']));
    const out = applyAll(d, list);
    expect(out.tracks[0].items.map((i) => [i.recordIn, i.recordOut, (i as { playbackRate?: number }).playbackRate])).toEqual([[0, 90, 1], [90, 120, 0.5], [120, 315, 1]]);
  });
  it('Add sound here: no op; the playhead goes to the event frame and the sound picker opens', () => {
    expect(planEventAction('addSound', { doc: base(), event: mk(90), newId: ids() })).toEqual({ kind: 'sound', id: 'addSound', frame: 90 });
  });
  it('every program the planner makes applies to the document without the engine refusing it', () => {
    for (const id of EVENT_ACTION_ORDER) {
      const plan = planEventAction(id, { doc: base(), event: mk(90), newId: ids() });
      if (plan.kind === 'ops') expect(() => applyAll(base(), plan.ops), id).not.toThrow();
    }
  });
  it('near a clip edge the keys stay inside the clip and a one-frame clip is refused plainly', () => {
    const edge = doc([track('v1', 'video', [media('c1', 0, 100, 0)])]);
    const list = ops('zoom', edge, mk(98));
    for (const o of list) if (o.type === 'setKeyframe') expect(o.frame).toBeLessThanOrEqual(99);
    const tiny = doc([track('v1', 'video', [media('c1', 90, 91, 0)])]);
    expect(planEventAction('zoom', { doc: tiny, event: mk(90), newId: ids() }).kind).toBe('none');
  });
});

describe('availability', () => {
  const opts = { readOnly: false, canOpenSounds: true };
  it('offers all eight in order, and never glow (no existing op can express it)', () => {
    expect(availableEventActions(base(), mk(90), opts)).toEqual(['addSound', 'addTitle', 'zoom', 'pan', 'shake', 'flash', 'speedRamp', 'cut']);
    expect(HIDDEN_EVENT_ACTIONS).toContain('glow');
    expect(availableEventActions(base(), mk(90), opts)).not.toContain('glow' as EventActionId);
  });
  it('hides what cannot apply instead of disabling it', () => {
    expect(availableEventActions(base(), mk(90), { ...opts, canOpenSounds: false })).not.toContain('addSound');
    expect(availableEventActions(base(), mk(0), opts)).not.toContain('cut');
    expect(availableEventActions(doc([track('v1', 'video', [media('c1', 0, 300, 0)], { locked: true })]), mk(90), opts)).toEqual(['addSound', 'addTitle', 'flash']);
    expect(availableEventActions(base(), mk(90, { clipId: 'gone' }), opts)).toEqual(['addSound', 'addTitle', 'flash']);
    expect(availableEventActions(base(), mk(90), { ...opts, readOnly: true })).toEqual([]);
  });
});
