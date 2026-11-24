import { describe, expect, it } from 'vitest';
import fc from 'fast-check';

import { LufsMeter, meanSquareToLufs, preFilter, rlbFilter } from '../lufs';
import { DEFAULT_DUCKING, deriveDucking, dialogueSpans } from '../ducking';
import { apply, assertInvariants as assertEngineInvariants } from '../../engine';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';

/** These fixtures name things `v1`/`track-video`: every invariant except the Guid id shape (pinned in `apply.contract.test.ts`). */
const assertInvariants = (doc: Parameters<typeof assertEngineInvariants>[0]): void => assertEngineInvariants(doc, { ids: 'opaque' });

function sine(sampleRate: number, seconds: number, hz: number, amplitude: number): Float32Array {
  const out = new Float32Array(Math.round(sampleRate * seconds));
  for (let i = 0; i < out.length; i++) out[i] = amplitude * Math.sin((2 * Math.PI * hz * i) / sampleRate);
  return out;
}

describe('LufsMeter', () => {
  it('reads a -20 dBFS 1 kHz stereo sine within 0.3 LU of -17 LUFS (two coherent channels sum +3 dB) at 48k and 44.1k', () => {
    for (const sampleRate of [48000, 44100]) {
      const meter = new LufsMeter(sampleRate, 2);
      const tone = sine(sampleRate, 5, 1000, 10 ** (-20 / 20));
      meter.process([tone, tone]);
      const r = meter.readout();
      // BS.1770: a 0 dBFS sine in ONE channel reads -3.01 LUFS; two identical channels add 3 dB.
      expect(r.integratedLufs).toBeCloseTo(-20 - 3.01 + 3.01, 0);
      expect(Math.abs(r.integratedLufs - -20.0)).toBeLessThan(0.3);
      expect(Math.abs(r.momentaryLufs - r.integratedLufs)).toBeLessThan(0.3);
      expect(Math.abs(r.shortTermLufs - r.integratedLufs)).toBeLessThan(0.3);
      expect(r.truePeakDbtp).toBeCloseTo(-20, 0);
    }
  });

  it('K-weighting is ~0 dB at 1 kHz and strongly attenuates 30 Hz', () => {
    const sampleRate = 48000;
    const run = (hz: number) => {
      const meter = new LufsMeter(sampleRate, 1);
      meter.process([sine(sampleRate, 3, hz, 0.5)]);
      return meter.readout().integratedLufs;
    };
    const ref = run(1000);
    expect(Math.abs(run(1000) - ref)).toBe(0);
    // K-weighting: -8.35 dB at 30 Hz and +4.0 dB at 8 kHz against +0.65 dB at 1 kHz (computed from
    // the filter design), so ~9 dB down and ~3.3 dB up relative to the reference tone.
    expect(run(30)).toBeLessThan(ref - 8);
    expect(run(8000)).toBeGreaterThan(ref + 2.5);
  });

  it('gates silence out of the integrated measurement and reports -Infinity for pure silence', () => {
    const sampleRate = 48000;
    const silent = new LufsMeter(sampleRate, 2);
    silent.process([new Float32Array(sampleRate), new Float32Array(sampleRate)]);
    expect(silent.readout().integratedLufs).toBe(Number.NEGATIVE_INFINITY);
    expect(silent.readout().truePeakDbtp).toBe(Number.NEGATIVE_INFINITY);

    const gated = new LufsMeter(sampleRate, 2);
    const tone = sine(sampleRate, 2, 1000, 10 ** (-20 / 20));
    gated.process([tone, tone]);
    gated.process([new Float32Array(sampleRate * 4), new Float32Array(sampleRate * 4)]);
    // Four seconds of silence after two of tone must not drag the gated integrated value down.
    expect(Math.abs(gated.readout().integratedLufs - -20.0)).toBeLessThan(0.5);
    expect(gated.readout().momentaryLufs).toBe(Number.NEGATIVE_INFINITY);
  });

  it('exposes stable filter designs and the LUFS offset', () => {
    const pre = preFilter(48000);
    expect(pre.b0).toBeCloseTo(1.53512485958697, 6);
    expect(pre.a1).toBeCloseTo(-1.69065929318241, 6);
    const rlb = rlbFilter(48000);
    expect(rlb.a1).toBeCloseTo(-1.99004745483398, 6);
    expect(meanSquareToLufs(1)).toBeCloseTo(-0.691, 6);
    expect(meanSquareToLufs(0)).toBe(Number.NEGATIVE_INFINITY);
  });
});

describe('deriveDucking', () => {
  const d = doc([
    track(TRACKS.a1, 'audio', [media('d1', 10, 40, 0), media('d2', 44, 60, 0), media('d3', 200, 230, 0)]),
    track(TRACKS.a2, 'audio', [media('music', 0, 300, 0)]),
  ]);

  it('merges dialogue closer than attack+release and writes a bed/duck envelope with strictly increasing frames', () => {
    const spans = dialogueSpans(d, [TRACKS.a1], 28);
    expect(spans).toEqual([[10, 60], [200, 230]]);
    const curve = deriveDucking(d, { ...DEFAULT_DUCKING, dialogueTrackIds: [TRACKS.a1] });
    expect(curve.value).toBe(0);
    expect(curve.keys!.map((k) => [k.frame, k.value])).toEqual([[2, 0], [10, -12], [60, -12], [80, 0], [192, 0], [200, -12], [230, -12], [250, 0]]);
    for (let i = 1; i < curve.keys!.length; i++) expect(curve.keys![i].frame).toBeGreaterThan(curve.keys![i - 1].frame);
  });

  it('clamps the first ramp at frame 0, respects the dB range, and yields a bed-only fader with no dialogue', () => {
    const early = doc([track(TRACKS.a1, 'audio', [media('d', 2, 20, 0)]), track(TRACKS.a2, 'audio', [])]);
    const curve = deriveDucking(early, { ...DEFAULT_DUCKING, dialogueTrackIds: [TRACKS.a1], bedDb: -3, depthDb: -100 });
    expect(curve.keys![0]).toEqual({ frame: 0, value: -3, interp: 'linear' });
    expect(curve.keys![1].value).toBe(-60);
    expect(deriveDucking(early, { ...DEFAULT_DUCKING, dialogueTrackIds: ['nope'] })).toEqual({ value: 0, keys: [] });
  });

  it('property: the derived curve is always accepted by setTrackProperty and keeps the invariants', () => {
    fc.assert(
      fc.property(
        fc.array(fc.tuple(fc.nat({ max: 400 }), fc.integer({ min: 1, max: 60 })), { minLength: 0, maxLength: 6 }),
        fc.integer({ min: 0, max: 30 }),
        fc.integer({ min: 0, max: 30 }),
        fc.integer({ min: -40, max: 0 }),
        (spans, attack, release, depth) => {
          let cursor = 0;
          const items = spans.map(([gap, len], i) => {
            const start = cursor + gap;
            cursor = start + len;
            return media(`d${i}`, start, cursor, 0);
          });
          const generated = doc([track(TRACKS.a1, 'audio', items), track(TRACKS.a2, 'audio', [media('music', 0, 1000, 0)])]);
          const curve = deriveDucking(generated, { dialogueTrackIds: [TRACKS.a1], bedDb: 0, depthDb: depth, attackFrames: attack, releaseFrames: release });
          const applied = apply(generated, { type: 'setTrackProperty', trackId: TRACKS.a2, key: 'gainDb', value: curve });
          assertInvariants(applied.doc);
          expect(applied.doc.tracks[1].gainDb).toEqual(curve);
        },
      ),
    );
  });
});
