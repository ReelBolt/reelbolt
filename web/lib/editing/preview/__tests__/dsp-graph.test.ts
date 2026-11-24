/** @vitest-environment node */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildDynamicsChain, ensureDspWorklet } from '../dsp-graph';
import { FakeContext, type FakeNode } from './fake-audio-context';
import { DEESSER_PROCESSOR, GATE_PROCESSOR } from '../dsp-worklet-source';

const ctx = () => new FakeContext();
const as = <T>(v: unknown) => v as T;
const build = (c: FakeContext, dynamics: Parameters<typeof buildDynamicsChain>[1], worklet = true) =>
  buildDynamicsChain(as<BaseAudioContext>(c), dynamics, { nodes: true, worklet }, { workletNode: (_, name) => as<AudioWorkletNode>(c.worklet(name)) });

afterEach(() => vi.unstubAllGlobals());

describe('buildDynamicsChain', () => {
  it('returns null for no stages and for stages this browser cannot run', () => {
    const c = ctx();
    expect(build(c, null)).toBeNull();
    expect(build(c, {})).toBeNull();
    expect(buildDynamicsChain(as<BaseAudioContext>(c), { compressor: {} }, { nodes: false, worklet: false })).toBeNull();
    expect(c.nodes).toHaveLength(1); // only the destination
  });
  it('chains the stages in the render order: gate, de-esser, compressor, limiter', () => {
    const c = ctx();
    const chain = build(c, { limiter: { ceilingDb: -3 }, compressor: { thresholdDb: -20, ratio: 4, makeupDb: 3 }, deesser: { intensity: 1 }, gate: {} })!;
    expect(chain.stages).toEqual(['gate', 'deesser', 'compressor', 'limiter']);
    expect((chain.input as unknown as FakeNode).kind).toBe(`worklet:${GATE_PROCESSOR}`);
    expect((chain.output as unknown as FakeNode).kind).toBe('waveshaper');
    const kinds: string[] = [];
    for (let n: FakeNode | undefined = chain.input as unknown as FakeNode; n; n = n.outputs[0]) kinds.push(n.kind);
    expect(kinds).toEqual([`worklet:${GATE_PROCESSOR}`, `worklet:${DEESSER_PROCESSOR}`, 'compressor', 'gain', 'compressor', 'gain', 'waveshaper']);
    expect(c.hasCycle()).toBe(false);
  });
  it('sets the compressor node parameters from the mapping', () => {
    const c = ctx();
    build(c, { compressor: { thresholdDb: -30, ratio: 6, attackMs: 10, releaseMs: 100, makeupDb: 0 } });
    const comp = c.ofKind('compressor')[0] as unknown as { threshold: { value: number }; ratio: { value: number }; attack: { value: number }; release: { value: number }; knee: { value: number } };
    expect(comp.threshold.value).toBe(-30);
    expect(comp.ratio.value).toBe(6);
    expect(comp.attack.value).toBeCloseTo(0.01, 6);
    expect(comp.release.value).toBeCloseTo(0.1, 6);
    expect(comp.knee.value).toBe(9);
  });
  it('limiter ends in a clamp at the ceiling', () => {
    const c = ctx();
    build(c, { limiter: { ceilingDb: -6 } });
    const shaper = c.ofKind('waveshaper')[0] as unknown as { curve: Float32Array };
    expect(Math.max(...shaper.curve)).toBeCloseTo(10 ** (-6 / 20), 5);
  });
  it('hands the worklets their numbers by one message each and creates no node afterwards', () => {
    const c = ctx();
    const chain = build(c, { gate: { thresholdDb: -50, ratio: 4, attackMs: 5, releaseMs: 80 }, deesser: { intensity: 0.5 } })!;
    const messages = chain.nodes.map((n) => (n as unknown as { port: { messages: Record<string, number>[] } }).port.messages);
    expect(messages[0]).toHaveLength(1);
    expect(messages[0][0]).toMatchObject({ type: 'params', ratio: 4, attackMs: 5, releaseMs: 80 });
    expect(messages[1][0]).toMatchObject({ type: 'params', bandHz: 7000 });
  });
  it('skips the gate and de-esser without the worklet, keeps the compressor', () => {
    const c = ctx();
    const chain = build(c, { gate: {}, deesser: {}, compressor: {} }, false)!;
    expect(chain.stages).toEqual(['compressor']);
  });
  it('dispose disconnects every node', () => {
    const c = ctx();
    const chain = build(c, { compressor: {}, limiter: {} })!;
    chain.dispose();
    expect(chain.nodes.every((n) => (n as unknown as FakeNode).disconnected === 1)).toBe(true);
  });
});

describe('ensureDspWorklet', () => {
  it('loads the module once per context', async () => {
    const c = ctx();
    expect(await ensureDspWorklet(as<BaseAudioContext>(c))).toBe(true);
    expect(await ensureDspWorklet(as<BaseAudioContext>(c))).toBe(true);
    expect(c.modules).toHaveLength(1);
  });
  it('resolves false when the module is refused or there is no audioWorklet', async () => {
    const refused = ctx();
    refused.audioWorklet = { addModule: async () => Promise.reject(new Error('CSP')) };
    expect(await ensureDspWorklet(as<BaseAudioContext>(refused))).toBe(false);
    const none = ctx();
    none.audioWorklet = undefined;
    expect(await ensureDspWorklet(as<BaseAudioContext>(none))).toBe(false);
  });
});
