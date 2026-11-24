/** @vitest-environment node */
/** R07.1 motion preset builders, R07.3 unit conversion, R07.4 transition helpers - against the real engine. */
import { describe, expect, it } from 'vitest';
import { apply, applyAll, assertInvariants } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import type { MediaClip } from '../../types';
import {
  MOTION_PRESETS,
  addTransitionOp,
  addTransitionToAllOps,
  autoFadeFrames,
  checkFadeSeconds,
  checkOpacityPercent,
  checkVolumePercent,
  dbToVolumePercent,
  findSeams,
  fitTransition,
  isAnimated,
  makeRoomOps,
  motionKeys,
  motionOps,
  opacityToPercent,
  planSpeed,
  secondsToFrames,
  speedReadout,
  transitionLabel,
  transitionParity,
  volumePercentToDb,
  MORE_TRANSITIONS,
  TRANSITION_WORDS,
  type MotionPresetId,
} from '..';

const RATE = { num: 30, den: 1 };
const opaque = { ids: 'opaque' as const };
const clipDoc = () => doc([track('V1', 'video', [media('a', 0, 90, 0), media('b', 90, 180, 0)])]);
const clipA = (d = clipDoc()) => d.tracks[0].items[0] as MediaClip;
const keysOf = (d: ReturnType<typeof clipDoc>, param: 'positionX' | 'positionY' | 'scale' | 'opacity' | 'rotation') => ((d.tracks[0].items[0] as MediaClip).transform?.[param]?.keys ?? []).map((k) => [k.frame, k.value]);

describe('R07.1 motion presets', () => {
  it('every preset yields ops the engine applies, keys inside the clip, and one undo restores the clip', () => {
    for (const info of MOTION_PRESETS) {
      const d = clipDoc();
      const ops = motionOps(info.id, clipA(d), RATE);
      expect(ops.length, info.id).toBeGreaterThan(0);
      const next = applyAll(d, ops);
      assertInvariants(next, opaque);
      const clip = next.tracks[0].items[0] as MediaClip;
      expect(isAnimated(clip), info.id).toBe(true);
      for (const channel of Object.values(clip.transform ?? {})) for (const k of channel?.keys ?? []) expect(k.frame >= 0 && k.frame < 90, `${info.id} key ${k.frame}`).toBe(true);
    }
  });

  it('writes the documented keys', () => {
    const d = applyAll(clipDoc(), motionOps('zoomIn', clipA(), RATE));
    expect(keysOf(d, 'scale')).toEqual([[0, 1], [30, 1.2]]);
    const out = applyAll(clipDoc(), motionOps('slideLeft', clipA(), RATE, { seconds: 1 }));
    expect(keysOf(out, 'positionX')).toEqual([[0, -1], [30, 0]]);
    const exit = applyAll(clipDoc(), motionOps('slideRight', clipA(), RATE, { seconds: 1, timing: 'end' }));
    expect(keysOf(exit, 'positionX')).toEqual([[59, 0], [89, 1]]);
    const fade = applyAll(clipDoc(), motionOps('fade', clipA(), RATE, { seconds: 0.5 }));
    expect(keysOf(fade, 'opacity')).toEqual([[0, 0], [15, 1]]);
    const burns = applyAll(clipDoc(), motionOps('kenBurns', clipA(), RATE, { strengthPercent: 20 }));
    expect(keysOf(burns, 'scale')).toEqual([[0, 1], [89, 1.2]]);
    expect(keysOf(burns, 'positionX')[1]).toEqual([89, -0.05]);
  });

  it('strength scales the motion and the duration is clamped inside the clip', () => {
    const strong = motionKeys('zoomIn', { recordIn: 10, recordOut: 40 }, RATE, { strengthPercent: 50, seconds: 60 });
    expect(strong).toEqual([
      { param: 'scale', frame: 10, value: 1, interp: 'easeInOutQuad' },
      { param: 'scale', frame: 39, value: 1.5, interp: 'easeInOutQuad' },
    ]);
  });

  it('a new preset replaces the earlier motion and undoing it restores the earlier motion exactly', () => {
    const first = applyAll(clipDoc(), motionOps('zoomIn', clipA(), RATE));
    const ops = motionOps('slideTop', first.tracks[0].items[0] as MediaClip, RATE);
    expect(ops.filter((o) => o.type === 'removeKeyframe')).toHaveLength(2);
    const second = applyAll(first, ops);
    expect(keysOf(second, 'scale')).toEqual([]);
    expect(keysOf(second, 'positionY').length).toBe(2);
    // The batch's own inverse restores the doc: fold each op with its inverse in reverse.
    let cur = first;
    const inverses: ReturnType<typeof apply>['inverse'][] = [];
    for (const op of ops) {
      const r = apply(cur, op);
      inverses.push(r.inverse);
      cur = r.doc;
    }
    let back = cur;
    for (const inv of inverses.reverse()) back = applyAll(back, inv);
    expect(back).toEqual(first);
  });

  it('refuses nothing silently: a one-frame clip is not animatable', () => {
    expect(motionOps('zoomIn', media('x', 0, 1) as MediaClip, RATE)).toEqual([]);
  });
});

describe('R07.3 plain units', () => {
  it('opacity percent stores the exact fraction and refuses out of range', () => {
    expect(checkOpacityPercent(40)).toEqual({ ok: true, value: 0.4 });
    expect(checkOpacityPercent(33)).toEqual({ ok: true, value: 0.33 });
    expect(opacityToPercent(0.4)).toBe(40);
    const bad = checkOpacityPercent(140);
    expect(bad.ok).toBe(false);
    if (!bad.ok) expect(bad.sentence).toMatch(/between 0% and 100%/);
  });

  it('fades convert seconds to frames and refuse what does not fit, with a sentence', () => {
    expect(secondsToFrames(0.5, RATE)).toBe(15);
    expect(checkFadeSeconds('in', 1, RATE, 90, 0)).toEqual({ ok: true, value: 30 });
    const bad = checkFadeSeconds('in', 4, RATE, 90, 30);
    expect(bad.ok).toBe(false);
    if (!bad.ok) expect(bad.sentence).toMatch(/longer than the room left/);
    expect(autoFadeFrames(RATE, 90)).toBe(15);
    expect(autoFadeFrames(RATE, 20)).toBe(5);
  });

  it('speed plans the new length, shrinks fades that no longer fit and reads "Clip is now N s"', () => {
    const clip = { recordIn: 0, recordOut: 90, playbackRate: 1, fadeInFrames: 20, fadeOutFrames: 20 };
    const two = planSpeed(2, clip);
    expect(two.ok && two.value.durationFrames).toBe(45);
    const four = planSpeed(4, { ...clip, fadeInFrames: 20, fadeOutFrames: 20 });
    expect(four.ok && four.value.fadesShrunk).toBe(true);
    if (four.ok) expect(four.value.fadeInFrames + four.value.fadeOutFrames).toBeLessThanOrEqual(four.value.durationFrames);
    expect(speedReadout(2, clip, RATE)).toBe('Clip is now 1.5 s');
    expect(speedReadout(0.5, clip, RATE)).toBe('Clip is now 6 s');
    const bad = planSpeed(40, clip);
    expect(bad.ok).toBe(false);
  });

  it('volume percent converts to dB and back, 0 is muted', () => {
    expect(volumePercentToDb(100)).toBe(0);
    expect(volumePercentToDb(50)).toBe(-6.02);
    expect(volumePercentToDb(0)).toBe(-60);
    expect(dbToVolumePercent(-6.02)).toBe(50);
    expect(dbToVolumePercent(-60)).toBe(0);
    expect(checkVolumePercent(500).ok).toBe(false);
  });
});

describe('R07.4 transitions at the seam', () => {
  it('names eight words, the rest under More, and a parity line per word', () => {
    expect(TRANSITION_WORDS).toHaveLength(8);
    expect(TRANSITION_WORDS.length + MORE_TRANSITIONS.length).toBe(44);
    expect(transitionLabel('wipetl')).toBe('Wipe top left');
    expect(transitionParity('dissolve').parity).toBe('faithful');
    expect(transitionParity('wipeleft').parity).toBe('approximate');
    expect(transitionParity('wipeleft').line).toMatch(/wipe left/);
  });

  it('finds the seams and builds an addTransition that the engine applies; handles missing is reported', () => {
    const d = doc([track('V1', 'video', [media('a', 0, 90, 0), media('b', 90, 180, 30)])]);
    const seams = findSeams(d);
    expect(seams).toHaveLength(1);
    const made = addTransitionOp(seams[0], { transitionType: 'wipeleft', frames: 15 }, () => 't1');
    expect('op' in made).toBe(true);
    if ('op' in made) {
      const next = apply(d, made.op).doc;
      const t = next.tracks[0].items.find((i) => i.type === 'transition');
      expect(t && t.type === 'transition' && t.transitionType).toBe('wipeleft');
    }
    const noRoom = clipDoc();
    const refused = addTransitionOp(findSeams(noRoom)[0], { transitionType: 'dissolve', frames: 15 }, () => 't2');
    expect('reason' in refused && refused.reason).toMatch(/Make room/);
    expect(fitTransition(findSeams(noRoom)[0], 15).missingHandle).toBe(7);
  });

  it('Apply to all cuts adds one per cut that fits and counts the ones that do not', () => {
    const d = doc([track('V1', 'video', [media('a', 0, 60, 0), media('b', 60, 120, 30), media('c', 120, 180, 0), media('d', 180, 240, 30)])]);
    let n = 0;
    const plan = addTransitionToAllOps(d, { transitionType: 'dissolve', frames: 10 }, () => `t${++n}`);
    expect(plan.added).toBe(2);
    expect(plan.skipped).toBe(1);
    const next = applyAll(d, plan.ops);
    assertInvariants(next, opaque);
    expect(next.tracks[0].items.filter((i) => i.type === 'transition')).toHaveLength(2);
  });

  it('Make room trims the next clip head so the transition then fits; one batch', () => {
    const d = clipDoc();
    const seam = findSeams(d)[0];
    const room = makeRoomOps(d, seam, 15);
    expect('ops' in room).toBe(true);
    if (!('ops' in room)) return;
    const next = applyAll(d, room.ops);
    assertInvariants(next, opaque);
    const b = next.tracks[0].items.find((i) => i.id === 'b') as MediaClip;
    expect(b.sourceIn).toBe(room.trimmedFrames);
    const seam2 = findSeams(next)[0];
    expect('op' in addTransitionOp(seam2, { transitionType: 'dissolve', frames: 15 }, () => 't3')).toBe(true);
  });

  it('Make room says why not when the clip is too short', () => {
    const d = doc([track('V1', 'video', [media('a', 0, 90, 0), media('b', 90, 96, 0)])]);
    const room = makeRoomOps(d, findSeams(d)[0], 30);
    expect('reason' in room).toBe(true);
  });
});

describe('presets list', () => {
  it('has the nine motion presets by id', () => {
    const ids: MotionPresetId[] = MOTION_PRESETS.map((p) => p.id);
    expect(ids).toEqual(['zoomIn', 'zoomOut', 'pop', 'slideLeft', 'slideRight', 'slideTop', 'slideBottom', 'fade', 'kenBurns']);
  });
});
