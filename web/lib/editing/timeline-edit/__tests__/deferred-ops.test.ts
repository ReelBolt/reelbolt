import { describe, expect, it } from 'vitest';
import { applyAll } from '../../engine';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import { fitToFillAvailability, fitToFillConfirmLine, joinAvailability, replaceAvailability, replaceSourceCheck } from '..';

describe('joinAvailability', () => {
  it('accepts two halves of one source that touch', () => {
    const d = applyAll(doc([track('v1', 'video', [media('a', 0, 60)])]), [{ type: 'blade', itemId: 'a', atFrame: 30, newItemId: '00000000-0000-4000-8000-0000000000b1' }]);
    const r = joinAvailability(d, 'a');
    expect(r.ok).toBe(true);
    if (r.ok) expect(applyAll(d, r.ops).tracks[0].items.map((i) => [i.id, i.recordIn, i.recordOut])).toEqual([['a', 0, 60]]);
  });
  it('says why when it is the last clip', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30)])]);
    expect(joinAvailability(d, 'a')).toEqual({ ok: false, reason: 'There is no clip after this one on its track.' });
  });
  it('says why when there is a gap', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30, 0), media('b', 40, 70, 30)])]);
    const r = joinAvailability(d, 'a');
    expect(!r.ok && r.reason).toMatch(/gap/);
  });
  it('says why when the files differ', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30, 0), media('b', 30, 60, 30, { assetRef: assetRef(2) })])]);
    const r = joinAvailability(d, 'a');
    expect(!r.ok && r.reason).toMatch(/different files/);
  });
  it('says why when the source does not continue', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30, 0), media('b', 30, 60, 100)])]);
    const r = joinAvailability(d, 'a');
    expect(!r.ok && r.reason).toMatch(/carry on/);
  });
  it('says select a clip for an unknown id', () => {
    expect(joinAvailability(doc([track('v1', 'video', [])]), 'zz').ok).toBe(false);
  });
});

describe('fitToFillAvailability', () => {
  it('reports the resulting speed', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 50), media('b', 60, 90)])]);
    const r = fitToFillAvailability(d, 'a');
    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.speedPercent).toBe(83);
      expect(fitToFillConfirmLine(r)).toBe('Speed becomes 83%');
      expect(applyAll(d, r.ops).tracks[0].items[0].recordOut).toBe(60);
    }
  });
  it('refuses with no gap', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30), media('b', 30, 60)])]);
    expect(fitToFillAvailability(d, 'a')).toEqual({ ok: false, reason: 'There is no gap after this clip to fill.' });
  });
  it('refuses for a frozen frame', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30, 0, { hold: true }), media('b', 60, 90)])]);
    const r = fitToFillAvailability(d, 'a');
    expect(!r.ok && r.reason).toMatch(/frozen/);
  });
});

describe('replace checks', () => {
  const clip = media('a', 0, 60);
  it('allows a media clip', () => {
    expect(replaceAvailability(doc([track('v1', 'video', [clip])]), 'a').ok).toBe(true);
  });
  it('refuses a missing clip', () => {
    expect(replaceAvailability(doc([track('v1', 'video', [])]), 'a').ok).toBe(false);
  });
  it('refuses a file shorter than the clip', () => {
    expect(replaceSourceCheck(clip, { frames: 30, isStill: false })).toEqual({ ok: false, reason: 'That file is shorter than the clip.' });
  });
  it('accepts a long enough file, a still, and an unknown length', () => {
    expect(replaceSourceCheck(clip, { frames: 60, isStill: false }).ok).toBe(true);
    expect(replaceSourceCheck(clip, { frames: 1, isStill: true }).ok).toBe(true);
    expect(replaceSourceCheck(clip, { frames: null, isStill: false }).ok).toBe(true);
  });
});
