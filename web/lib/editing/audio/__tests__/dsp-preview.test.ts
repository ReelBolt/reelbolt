/** @vitest-environment node */
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  autoMakeupDb,
  BUS_FALLBACK_NOTE,
  clampCurve,
  compressorNodeParams,
  deesserWorkletParams,
  detectDspSupport,
  DSP_DEFAULTS,
  gateWorkletParams,
  HEARD_AFTER_RENDER,
  HEARD_IN_PREVIEW,
  limiterNodeParams,
  markWorkletUnavailable,
  planBusRouting,
  PREVIEW_DIFFERENCES,
  presentStages,
  resetWorkletRefusal,
  stageStatusText,
  trackBusId,
  unheardStages,
} from '../dsp-preview';
import { AUDIO_RANGES } from '../../engine/ops';

afterEach(() => {
  vi.unstubAllGlobals();
  resetWorkletRefusal();
});

describe('compressor mapping', () => {
  it('maps the C# defaults to the browser compressor with the node\'s own make-up taken out', () => {
    const p = compressorNodeParams(undefined);
    expect(p).toMatchObject({ thresholdDb: -18, ratio: 3, attackSec: 0.02, releaseSec: 0.25, kneeDb: 9 });
    // -18 dB, 3:1: the node adds 0.6 * 18 * (1 - 1/3) = 7.2 dB itself; the mapping subtracts it again.
    expect(autoMakeupDb(-18, 3, 9)).toBeCloseTo(7.2, 6);
    expect(20 * Math.log10(p.makeupGain)).toBeCloseTo(-7.2, 4);
  });
  it('adds the user\'s make-up on top', () => {
    expect(20 * Math.log10(compressorNodeParams({ makeupDb: 6 }).makeupGain)).toBeCloseTo(6 - 7.2, 4);
  });
  it('clamps every field to the validator range (min and max)', () => {
    const r = AUDIO_RANGES.compressor;
    const lo = compressorNodeParams({ thresholdDb: -999, ratio: 0, attackMs: 0, releaseMs: 0, makeupDb: -5 });
    expect(lo).toMatchObject({ thresholdDb: r.thresholdDb[0], ratio: r.ratio[0], attackSec: r.attackMs[0] / 1000, releaseSec: r.releaseMs[0] / 1000 });
    const hi = compressorNodeParams({ thresholdDb: 10, ratio: 99, attackMs: 99999, releaseMs: 99999, makeupDb: 99 });
    expect(hi).toMatchObject({ thresholdDb: 0, ratio: 20, attackSec: 1, releaseSec: 1 }); // browser caps times at 1 s
    expect(autoMakeupDb(0, 20, 9)).toBe(0); // threshold at 0 dB: the node adds nothing
    expect(Number.isFinite(hi.makeupGain)).toBe(true);
  });
  it('ignores non-finite values', () => {
    expect(compressorNodeParams({ ratio: Number.NaN }).ratio).toBe(DSP_DEFAULTS.compressor.ratio);
  });
});

describe('limiter mapping', () => {
  it('uses the default ceiling and a hard clip at it', () => {
    const p = limiterNodeParams(undefined);
    expect(p.compressor).toMatchObject({ thresholdDb: -1, ratio: 20, kneeDb: 0 });
    expect(p.ceilingLin).toBeCloseTo(10 ** (-1 / 20), 6);
  });
  it('covers the ceiling range -24..0', () => {
    expect(limiterNodeParams({ ceilingDb: -99 }).ceilingLin).toBeCloseTo(10 ** (-24 / 20), 6);
    expect(limiterNodeParams({ ceilingDb: 5 }).ceilingLin).toBe(1);
  });
  it('clampCurve is identity under the ceiling and flat over it, so no sample can pass', () => {
    const c = clampCurve(0.5, 5);
    expect(Array.from(c)).toEqual([-0.5, -0.5, 0, 0.5, 0.5]);
    const big = clampCurve(0.8);
    expect(Math.max(...big)).toBeCloseTo(0.8, 6);
    expect(Math.min(...big)).toBeCloseTo(-0.8, 6);
  });
});

describe('gate and de-esser mapping', () => {
  it('maps gate defaults and bounds', () => {
    const d = gateWorkletParams(undefined);
    expect(d.thresholdLin).toBeCloseTo(0.01, 6); // -40 dB
    expect(d).toMatchObject({ ratio: 2, attackMs: 20, releaseMs: 250 });
    expect(gateWorkletParams({ thresholdDb: -200, ratio: 0, attackMs: 0, releaseMs: 0 })).toMatchObject({ ratio: 1, attackMs: 1, releaseMs: 1 });
    expect(gateWorkletParams({ thresholdDb: 5, ratio: 99, attackMs: 9e9, releaseMs: 9e9 })).toMatchObject({ thresholdLin: 1, ratio: 20, attackMs: 2000, releaseMs: 2000 });
  });
  it('maps de-esser intensity 0..1 to ratio 1..8', () => {
    expect(deesserWorkletParams({ intensity: 0 }).ratio).toBe(1);
    expect(deesserWorkletParams(undefined).ratio).toBeCloseTo(4.5, 6);
    expect(deesserWorkletParams({ intensity: 1 }).ratio).toBe(8);
    expect(deesserWorkletParams({ intensity: 7 }).ratio).toBe(8);
    expect(deesserWorkletParams({ intensity: 1 }).bandHz).toBe(7000);
  });
});

describe('feature detect and status', () => {
  it('reports no support without the browser classes', () => {
    expect(detectDspSupport()).toEqual({ nodes: false, worklet: false });
  });
  it('detects nodes and worklet', () => {
    vi.stubGlobal('DynamicsCompressorNode', function () {});
    vi.stubGlobal('WaveShaperNode', function () {});
    expect(detectDspSupport()).toEqual({ nodes: true, worklet: false });
    vi.stubGlobal('AudioWorkletNode', function () {});
    vi.stubGlobal('AudioContext', class { get audioWorklet() { return {}; } });
    expect(detectDspSupport()).toEqual({ nodes: true, worklet: true });
    markWorkletUnavailable();
    expect(detectDspSupport().worklet).toBe(false);
  });
  it('says what is heard per stage', () => {
    const nodesOnly = { nodes: true, worklet: false };
    expect(stageStatusText('compressor', nodesOnly)).toBe(HEARD_IN_PREVIEW);
    expect(stageStatusText('limiter', nodesOnly)).toBe(HEARD_IN_PREVIEW);
    expect(stageStatusText('gate', nodesOnly)).toBe(HEARD_AFTER_RENDER);
    expect(stageStatusText('deesser', { nodes: true, worklet: true })).toBe(HEARD_IN_PREVIEW);
    expect(unheardStages({ gate: {}, compressor: {} }, nodesOnly)).toEqual(['gate']);
    expect(presentStages({ limiter: {}, gate: {} })).toEqual(['gate', 'limiter']); // render order
  });
  it('has a plain-language difference for every stage, with no em dash', () => {
    for (const text of Object.values(PREVIEW_DIFFERENCES)) expect(text).not.toContain('—');
    expect(Object.keys(PREVIEW_DIFFERENCES).sort()).toEqual(['compressor', 'deesser', 'gate', 'limiter']);
  });
});

describe('bus routing plan', () => {
  const bus = (id: string, outputBusId?: string | null) => ({ id, name: id, outputBusId });
  it('orders parents before children', () => {
    const plan = planBusRouting({ buses: [bus('child', 'mid'), bus('mid', 'root'), bus('root')] });
    expect(plan.ok && plan.order).toEqual(['root', 'mid', 'child']);
    expect(plan.ok && plan.parentOf.get('child')).toBe('mid');
    expect(plan.ok && plan.parentOf.get('root')).toBeNull();
  });
  it('sends an unknown output to the master like the render', () => {
    const plan = planBusRouting({ buses: [bus('a', 'ghost')] });
    expect(plan.ok && plan.parentOf.get('a')).toBeNull();
  });
  it('refuses loops, self-loops and chains deeper than four', () => {
    expect(planBusRouting({ buses: [bus('a', 'b'), bus('b', 'a')] }).ok).toBe(false);
    expect(planBusRouting({ buses: [bus('a', 'a')] }).ok).toBe(false);
    expect(planBusRouting({ buses: [bus('a', 'b'), bus('b', 'c'), bus('c', 'd'), bus('d', 'e'), bus('e')] }).ok).toBe(false);
    expect(planBusRouting({ buses: [bus('a', 'b'), bus('b', 'c'), bus('c', 'd'), bus('d')] }).ok).toBe(true);
    expect(BUS_FALLBACK_NOTE).toMatch(/straight to the master/);
  });
  it('resolves a track\'s bus only when it exists', () => {
    expect(trackBusId({ buses: [bus('a')] }, 'a')).toBe('a');
    expect(trackBusId({ buses: [bus('a')] }, 'zzz')).toBeNull();
    expect(trackBusId({}, 'a')).toBeNull();
    expect(trackBusId({ buses: [bus('a')] }, null)).toBeNull();
  });
});
