import { describe, expect, it } from 'vitest';
import en from '@/messages/en/editor-timeline.json';
import it_ from '@/messages/it/editor-timeline.json';
import { applyAll } from '../engine';
import { jlCutOps, linkedOpsForTrim } from '../link';
import { bodyTrimRequest, describeTrim, limitFor, planTrimDrag, probeMove, probeTrim, rippleShift, type TrimDragContext } from '../trim-readout';
import { doc, media, title, track, TRACKS } from './fixtures';

const rate = { num: 30, den: 1 };
// A [0..60] B [60..120] (source from 30) C [120..180] (source from 30), as the trim e2e lays them out.
const three = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 60), media('b', 60, 120, 30), media('c', 120, 180, 30)])]);
const say = (mode: 'ripple' | 'roll' | 'slip' | 'slide', edge: 'in' | 'out', requested: number, itemId = 'b') =>
  describeTrim({ mode, edge, requested, probe: probeTrim(three(), itemId, edge, mode, requested), rate });

describe('trim readout', () => {
  it('slip: same length, and the direction is where the source offset really went', () => {
    const r = say('slip', 'in', -12);
    expect(r).toMatchObject({ key: 'slip', limit: null, values: { frames: 12, timecode: '00:00:00:12', direction: 'earlier' } });
    expect(bodyTrimRequest('slip', 12, true, true)).toEqual({ edge: 'in', deltaFrames: -12 });
    expect(say('slip', 'in', 12).values.direction).toBe('later');
  });

  it('roll names the cut and the moved frames', () => {
    expect(say('roll', 'out', 12)).toMatchObject({ key: 'roll', limit: null, values: { frames: 12, direction: 'later' } });
  });

  it('ripple and slide say what moved', () => {
    expect(say('ripple', 'out', -20)).toMatchObject({ key: 'ripple', values: { frames: 20, direction: 'earlier', edge: 'out' } });
    expect(say('slide', 'out', 10)).toMatchObject({ key: 'slide', values: { frames: 10 } });
  });

  it('slide picks the neighbour on the side it heads toward, and the only one there is', () => {
    expect(bodyTrimRequest('slide', 5, true, true).edge).toBe('out');
    expect(bodyTrimRequest('slide', -5, true, true).edge).toBe('in');
    expect(bodyTrimRequest('slide', 5, true, false).edge).toBe('in');
    expect(bodyTrimRequest('slide', -5, false, true).edge).toBe('out');
  });

  it('limits: the readout says what stopped a slip at the start of the source', () => {
    const r = say('slip', 'in', 100); // sourceIn 30: shifting the source offset by +100 is allowed, -100 is not
    expect(r.limit).toBeNull();
    const stopped = say('slip', 'in', -100);
    expect(stopped).toMatchObject({ limit: 'sourceStart', values: { frames: 30 } });
  });

  it('limits: a ripple that would shrink a clip below one frame, and a roll against a neighbour', () => {
    expect(say('ripple', 'out', -500).limit).toBe('minLength');
    expect(say('roll', 'out', 500).limit).toBe('neighbour');
  });

  it('limits: a missing neighbour or source is named, not silent', () => {
    expect(say('roll', 'in', 5, 'a')).toMatchObject({ key: 'none', limit: 'noNeighbour' });
    const d = doc([track(TRACKS.v1, 'video', [title('t', 0, 60)])]);
    expect(describeTrim({ mode: 'slip', edge: 'in', requested: 5, probe: probeTrim(d, 't', 'in', 'slip', 5), rate })).toMatchObject({ limit: 'noSource' });
  });

  it('limitFor reads the clamp direction per mode and edge', () => {
    expect(limitFor('ripple', 'out', 10, 4)).toBe('sourceEnd');
    expect(limitFor('ripple', 'in', -10, -4)).toBe('sourceStart');
    expect(limitFor('ripple', 'in', 10, 4)).toBe('minLength');
    expect(limitFor('slide', 'in', 10, 10)).toBeNull();
  });

  it('has every sentence in English and Italian', () => {
    for (const cat of [en, it_]) {
      const rd = cat.canvas.trimReadout as Record<string, unknown>;
      for (const k of ['slip', 'slide', 'ripple', 'roll', 'none']) expect(typeof rd[k]).toBe('string');
      for (const k of ['sourceStart', 'sourceEnd', 'minLength', 'neighbour', 'noNeighbour', 'noSource']) expect(typeof (rd.limit as Record<string, unknown>)[k]).toBe('string');
    }
  });
});

describe('T-020 ripple shift agrees with the engine', () => {
  const lanes = () =>
    doc([
      track(TRACKS.v1, 'video', [media('a', 0, 60), media('b', 60, 120, 30, { linkId: 'L' }), media('c', 120, 180, 30), media('d', 200, 260, 30)]),
      track(TRACKS.a1, 'audio', [media('x', 60, 120, 30, { linkId: 'L' }), media('y', 130, 190, 30)]),
    ]);

  it('counts exactly the clips the real result moved, linked partners included, by the same amount', () => {
    for (const delta of [-20, -1, 7, 15]) {
      const d = lanes();
      const shift = rippleShift(d, 'b', 'out', 'ripple', delta);
      // The oracle is what the release really dispatches, not one plain trim: y moves with its linked partner x.
      const real = applyAll(d, linkedOpsForTrim(d, 'b', 'out', 'ripple', delta));
      const moved = new Map(d.tracks.flatMap((t) => t.items).map((i) => [i.id, i.recordIn]));
      const expected = real.tracks.flatMap((t) => t.items).filter((i) => moved.get(i.id) !== i.recordIn && i.id !== 'b' && i.id !== 'x');
      expect(expected.map((i) => i.id)).toContain('y');
      expect(shift.clips.map((c) => c.itemId).sort()).toEqual(expected.map((i) => i.id).sort());
      for (const clip of shift.clips) expect(clip.to).toBe(expected.find((i) => i.id === clip.itemId)!.recordIn);
      expect(shift.count).toBe(expected.length);
      expect(shift.frames).toBe(Math.abs(delta));
    }
  });

  it('puts the line in the readout and is silent for other modes', () => {
    const d = lanes();
    const shift = rippleShift(d, 'b', 'out', 'ripple', 12);
    const r = describeTrim({ mode: 'ripple', edge: 'out', requested: 12, probe: probeTrim(d, 'b', 'out', 'ripple', 12), rate, shift });
    expect(r.shift).toEqual({ count: shift.count, timecode: '00:00:00:12' });
    expect(shift.clips.map((c) => c.itemId)).toContain('c');
    expect(shift.count).toBeGreaterThanOrEqual(2);
    expect(rippleShift(d, 'b', 'out', 'roll', 5).count).toBe(0);
    expect(describeTrim({ mode: 'roll', edge: 'out', requested: 5, probe: probeTrim(d, 'b', 'out', 'roll', 5), rate }).shift).toBeNull();
  });

  it('a move onto another clip is reported as the refusal it is, not as an overwrite', () => {
    const d = lanes();
    expect(probeMove(d, 'a', TRACKS.v1, 100)).toBe('overlap');
    expect(probeMove(d, 'a', TRACKS.v1, 190)).toBe('overlap');
    expect(probeMove(d, 'a', TRACKS.v1, 300)).toBe('ok');
  });

  it('stays cheap on a large document and is memoised per changed frame', () => {
    const items = Array.from({ length: 5000 }, (_, i) => media(`m${i}`, i * 60, i * 60 + 50, 10));
    const d = doc([track(TRACKS.v1, 'video', items)]);
    const first = performance.now();
    const shift = rippleShift(d, 'm0', 'out', 'ripple', -5);
    const cold = performance.now() - first;
    expect(shift.count).toBe(4999);
    const again = performance.now();
    for (let i = 0; i < 1000; i += 1) rippleShift(d, 'm0', 'out', 'ripple', -5);
    expect(performance.now() - again).toBeLessThan(50);
    // One engine run on 5000 clips; recorded so a regression shows as a number, not a feeling.
    console.info(`ripple preview on 5000 clips: ${cold.toFixed(1)} ms cold`);
    expect(cold).toBeLessThan(1500);
  });

  it('has the new sentences in English and Italian', () => {
    for (const cat of [en, it_]) {
      expect(typeof (cat.canvas.trimReadout as Record<string, unknown>).shift).toBe('string');
      for (const k of ['playhead', 'marker', 'in', 'out', 'clipEdge', 'start']) expect(typeof (cat.canvas.snapLabel as Record<string, unknown>)[k]).toBe('string');
      expect(typeof cat.canvas.moveOverlap).toBe('string');
    }
  });
});

describe('a trim drag has one plan: the preview is what the release dispatches', () => {
  const linked = (partnerSourceIn = 30) =>
    doc([
      track(TRACKS.v1, 'video', [media('a', 0, 60), media('b', 60, 120, 30, { linkId: 'L' }), media('c', 120, 180, 30), media('d', 200, 260, 30)]),
      track(TRACKS.a1, 'audio', [media('x', 60, 120, partnerSourceIn, { linkId: 'L' }), media('y', 130, 190, 30)]),
    ]);

  it('runs the same ops as the linked route', () => {
    const d = linked();
    expect(planTrimDrag(d, { itemId: 'b', edge: 'out', mode: 'ripple', deltaFrames: 7 }).ops).toEqual(linkedOpsForTrim(d, 'b', 'out', 'ripple', 7));
    expect(planTrimDrag(d, { itemId: 'b', edge: 'out', mode: 'ripple', deltaFrames: 7 }).shift.clips.map((c) => c.itemId).sort()).toEqual(['c', 'd', 'y']);
  });

  it('edit separately: nothing else shifts, and the ops are the J/L cut', () => {
    const d = linked();
    const plan = planTrimDrag(d, { itemId: 'b', edge: 'out', mode: 'ripple', deltaFrames: 7, separately: true });
    expect(plan.ops).toEqual(jlCutOps(d, 'b', 'out', 7));
    expect(plan.shift.count).toBe(0);
  });

  it('a linked partner with less room limits the slip; one with none stops it', () => {
    const roomy = planTrimDrag(linked(5), { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: -20 });
    expect(roomy.probe).toEqual({ effective: -5 });
    const stuck = planTrimDrag(linked(0), { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: -20 });
    expect(stuck.ops).toEqual([]);
    expect(describeTrim({ mode: 'slip', edge: 'in', requested: -20, probe: stuck.probe, rate })).toMatchObject({ key: 'none', limit: 'sourceStart' });
  });

  it('a refused request keeps the bare op, so the release still shows the refusal', () => {
    const plan = planTrimDrag(linked(), { itemId: 'a', edge: 'in', mode: 'roll', deltaFrames: 5 });
    expect(plan.probe).toEqual({ blocked: 'noNeighbour' });
    expect(plan.ops).toHaveLength(1);
  });
});

describe('a trim drag stops at the end of the source', () => {
  const clips = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 60), media('b', 60, 120, 30), media('c', 400, 460, 0)])]);
  const known = (frames: number | null): TrimDragContext => ({ sourceFramesFor: () => frames });
  const sourceIn = (d: ReturnType<typeof clips>, id: string) => d.tracks[0].items.find((i) => i.id === id) as { sourceIn?: number; recordOut: number };

  it('clamps a slip so the window ends on the last frame, and says so', () => {
    const d = clips();
    const plan = planTrimDrag(d, { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: 500 }, known(240));
    expect(plan.probe).toEqual({ effective: 150 });
    expect(plan.stoppedBySource).toBe(true);
    expect(sourceIn(applyAll(d, plan.ops), 'b').sourceIn).toBe(180);
    expect(describeTrim({ mode: 'slip', edge: 'in', requested: 500, probe: plan.probe, rate, stoppedBySource: plan.stoppedBySource }).limit).toBe('sourceEnd');
  });

  it('clamps a ripple out edge at the end of its source', () => {
    const d = clips();
    const plan = planTrimDrag(d, { itemId: 'b', edge: 'out', mode: 'ripple', deltaFrames: 500 }, known(100));
    expect(plan.probe).toEqual({ effective: 10 });
    expect(sourceIn(applyAll(d, plan.ops), 'b').recordOut).toBe(130);
  });

  it('counts source frames at the clip\'s rate', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('r', 0, 60, 0, { playbackRate: 2 })])]);
    const plan = planTrimDrag(d, { itemId: 'r', edge: 'in', mode: 'slip', deltaFrames: 200 }, known(240));
    const slipped = applyAll(d, plan.ops).tracks[0].items[0] as { sourceIn?: number };
    expect((slipped.sourceIn ?? 0) + 120).toBeLessThanOrEqual(240);
    expect(plan.stoppedBySource).toBe(true);
  });

  it('an unknown length is not clamped; a move away from the end is', () => {
    expect(planTrimDrag(clips(), { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: 500 }, known(null)).probe).toEqual({ effective: 500 });
    expect(planTrimDrag(clips(), { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: -10 }, known(240)).stoppedBySource).toBe(false);
  });

  it('a clip already past the end is not made worse, and shortening it is allowed', () => {
    const plan = planTrimDrag(clips(), { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: 5 }, known(50));
    expect(plan.ops).toEqual([]);
    expect(plan.stoppedBySource).toBe(true);
    expect(planTrimDrag(clips(), { itemId: 'b', edge: 'in', mode: 'slip', deltaFrames: -5 }, known(50)).stoppedBySource).toBe(false);
  });

  it('the sentences end with a stop, so they read apart', () => {
    for (const cat of [en, it_]) {
      const rd = cat.canvas.trimReadout as Record<string, unknown>;
      for (const k of ['slip', 'slide', 'ripple', 'roll', 'shift', 'none']) expect(rd[k]).toMatch(/\.$/);
      expect(Object.keys(rd.fallback as object).sort()).toEqual(['noNeighbour', 'noSource']);
    }
  });
});
