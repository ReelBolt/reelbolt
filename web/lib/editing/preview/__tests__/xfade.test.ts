import { describe, expect, it } from 'vitest';
import fixture from '../__fixtures__/xfade-frames.json';
import { TRANSITION_TYPES } from '../../engine';
import { activeLayersAt } from '../scheduler';
import { XFADE_PARITY, XFADE_WORDS, frand, xfadeIndexOf, xfadeParityOf, xfadePixel, type Sampler } from '../xfade';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { Transition } from '../../types';

const W = fixture.width;
const H = fixture.height;
const bytes = (b64: string) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
const sampler = (buf: Uint8Array): Sampler => (x, y) => {
  const i = (y * W + x) * 3;
  return [buf[i], buf[i + 1], buf[i + 2]];
};
const A = sampler(bytes(fixture.a));
const B = sampler(bytes(fixture.b));

describe('the xfade word table', () => {
  it('covers every transition word the document allows, once, and nothing else', () => {
    expect([...XFADE_WORDS].sort()).toEqual([...TRANSITION_TYPES].sort());
    expect(new Set(XFADE_WORDS).size).toBe(XFADE_WORDS.length);
    expect(xfadeIndexOf('wipeleft')).toBe(XFADE_WORDS.indexOf('wipeleft'));
    expect(xfadeIndexOf('nope')).toBe(-1);
  });

  it('marks only the words the viewer cannot reproduce exactly as approximate, each with a reason', () => {
    expect(Object.keys(XFADE_PARITY).sort()).toEqual(['dissolve', 'distance', 'fadegrays', 'hblur']);
    for (const entry of Object.values(XFADE_PARITY)) expect(entry.note?.length ?? 0).toBeGreaterThan(10);
    expect(xfadeParityOf('wiperight')).toBe('exact');
    expect(xfadeParityOf('hblur')).toBe('approximate');
  });
});

describe('xfadePixel against real ffmpeg xfade frames', () => {
  // The fixture frames come from ffmpeg 8 (gbrp inputs, duration 1, 8 fps): frame n is at t = n/8, kernel progress 1 - t.
  for (const word of XFADE_WORDS) {
    it(`${word} matches within one level per channel`, () => {
      for (const n of ['2', '4', '6']) {
        const expected = bytes((fixture.frames as Record<string, Record<string, string>>)[word][n]);
        const progress = 1 - Number(n) / 8;
        for (let y = 0; y < H; y += 1) {
          for (let x = 0; x < W; x += 1) {
            const p = xfadePixel(word, progress, x, y, W, H, A, B);
            for (let c = 0; c < 3; c += 1) {
              const got = Math.floor(p[c] + 1e-6);
              expect(Math.abs(got - expected[(y * W + x) * 3 + c]), `${word} frame ${n} at ${x},${y} channel ${c}`).toBeLessThanOrEqual(1);
            }
          }
        }
      }
    });
  }

  it('an unknown word shows the incoming picture', () => {
    expect(xfadePixel('nope', 0.5, 3, 3, W, H, A, B)).toEqual(B(3, 3));
  });

  it('frand is the speckle ffmpeg uses: in [0, 1) and stable', () => {
    expect(frand(3, 4)).toBeGreaterThanOrEqual(0);
    expect(frand(3, 4)).toBeLessThan(1);
    expect(frand(3, 4)).toBe(frand(3, 4));
  });
});

describe('transition legs in the scheduler', () => {
  const transition = (transitionType: string): Transition => ({ id: 'x', type: 'transition', name: 'x', recordIn: 30, recordOut: 40, transitionType, easing: 'linear', enabled: true, provenance: null });
  const make = (word: string) => doc([track(TRACKS.v1, 'video', [media('a', 0, 30, 0), transition(word), media('b', 40, 80, 50)])]);

  it('a word with a kernel marks both legs, keeps them opaque and carries the eased progress', () => {
    const legs = activeLayersAt(make('wipeleft'), 34);
    expect(legs.map((l) => [l.item.id, l.opacity, l.xfade?.role, l.xfade?.word])).toEqual([['a', 1, 'from', 'wipeleft'], ['b', 1, 'to', 'wipeleft']]);
    expect(legs[1].xfade?.t).toBeCloseTo(0.5);
  });

  it('the plain fade and an unknown word stay an opacity ramp', () => {
    for (const word of ['fade', 'not-a-word']) {
      const legs = activeLayersAt(make(word), 34);
      expect(legs.map((l) => [l.item.id, l.opacity, l.xfade])).toEqual([['a', 1, undefined], ['b', 0.5, undefined]]);
    }
  });
});
