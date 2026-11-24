/** @vitest-environment node */
/** R08.6: the AudioGraph against a recording fake context: bus routing, dynamics chains, fallback. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AudioGraph } from '../audio-graph';
import { FakeContext, type FakeNode } from './fake-audio-context';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { AudioBus, EditTimeline, Track } from '../../types';

afterEach(() => vi.unstubAllGlobals());

const flags = () => ({ enabled: true, mute: false, solo: false, locked: false });
const bus = (id: string, extra: Partial<AudioBus> = {}): AudioBus => ({ id, name: id, ...extra });
const withBuses = (tracks: Track[], buses: AudioBus[]): EditTimeline => ({ ...doc(tracks), buses });

async function playing(d: EditTimeline, worklet = false) {
  const ctx = new FakeContext();
  vi.stubGlobal('AudioWorkletNode', function (c: FakeContext, name: string) {
    return c.worklet(name);
  });
  const graph = new AudioGraph(ctx as unknown as AudioContext, { urlFor: () => null, flags, dspSupport: { nodes: true, worklet } });
  await graph.play(d, 0, 1);
  return { ctx, graph };
}
const master = (ctx: FakeContext) => ctx.destination.ctx.nodes.find((n) => n.kind === 'gain' && n.outputs.includes(ctx.destination))!;
const aud = (id: string, extra: Partial<Track> = {}): Track => ({ ...track(id, 'audio', [media(`${id}-c`, 0, 30)]), ...extra });

describe('AudioGraph routing (R08.6)', () => {
  it('a gate switched on before the worklet module has loaded does not throw, and is heard from the next play', async () => {
    const ctx = new FakeContext();
    let loaded = false;
    const add = ctx.audioWorklet!.addModule.bind(ctx.audioWorklet);
    ctx.audioWorklet!.addModule = async (url: string) => {
      await add(url);
      loaded = true;
    };
    vi.stubGlobal('AudioWorkletNode', function (c: FakeContext, name: string) {
      if (!loaded) throw new DOMException('AudioWorklet does not have a valid AudioWorkletGlobalScope.', 'InvalidStateError');
      return c.worklet(name);
    });
    const d = doc([aud(TRACKS.a1, { dynamics: { gate: {} } })]);
    const graph = new AudioGraph(ctx as unknown as AudioContext, { urlFor: () => null, flags, dspSupport: { nodes: true, worklet: true } });
    expect(() => graph.applyTrackFlags(d)).not.toThrow();
    expect(ctx.nodes.some((n) => n.kind.startsWith('worklet'))).toBe(false);
    await graph.play(d, 0, 1);
    expect(ctx.nodes.some((n) => n.kind.startsWith('worklet'))).toBe(true);
  });
  it('routes track -> bus -> master with the bus fader and no cycle', async () => {
    const d = withBuses([aud(TRACKS.a1, { busId: 'b1' })], [bus('b1', { gainDb: { value: -6 } })]);
    const { ctx } = await playing(d);
    // 2 gains for the bus (input, fader) + master + track (input, fader, gate).
    const [trackIn] = ctx.ofKind('gain').filter((n) => n.outputs.length > 0);
    expect(trackIn).toBeDefined();
    const faders = ctx.ofKind('gain').map((n) => n as unknown as FakeNode & { gain: { value: number } });
    expect(faders.some((g) => Math.abs(g.gain.value - 10 ** (-6 / 20)) < 1e-6)).toBe(true);
    expect(ctx.reaches(trackIn, master(ctx))).toBe(true);
    expect(ctx.hasCycle()).toBe(false);
  });
  it('does not send a routed track straight to the master', async () => {
    const d = withBuses([aud(TRACKS.a1, { busId: 'b1' })], [bus('b1', { dynamics: { compressor: {} } })]);
    const { ctx } = await playing(d);
    const m = master(ctx);
    // Everything reaching the master passes the bus compressor: no node outside the bus chain feeds it directly except the bus tail.
    const feeders = ctx.nodes.filter((n) => n.outputs.includes(m));
    expect(feeders).toHaveLength(1);
    expect(feeders[0].kind).toBe('gain'); // the compressor's make-up gain, which ends the bus chain
    expect(ctx.ofKind('compressor')).toHaveLength(1);
  });
  it('chains bus -> bus -> master', async () => {
    const d = withBuses([aud(TRACKS.a1, { busId: 'child' })], [bus('child', { outputBusId: 'parent' }), bus('parent', { pan: 0.5 })]);
    const { ctx } = await playing(d);
    expect(ctx.hasCycle()).toBe(false);
    expect(ctx.ofKind('panner')).toHaveLength(1);
    const start = ctx.ofKind('gain')[1]; // first gain after master is the bus input of the first built bus
    expect(ctx.reaches(start, master(ctx))).toBe(true);
  });
  it('a bus fader at its quietest setting is silent (bus mute)', async () => {
    const d = withBuses([aud(TRACKS.a1, { busId: 'b1' })], [bus('b1', { gainDb: { value: -60 } })]);
    const { ctx } = await playing(d);
    const faders = ctx.ofKind('gain').map((n) => (n as unknown as { gain: { value: number } }).gain.value);
    expect(faders).toContain(0);
  });
  it('falls back to direct-to-master when the buses loop', async () => {
    const d = withBuses([aud(TRACKS.a1, { busId: 'a' })], [bus('a', { outputBusId: 'b' }), bus('b', { outputBusId: 'a' })]);
    const { ctx, graph } = await playing(d);
    expect(graph.usesBusFallback()).toBe(true);
    expect(ctx.hasCycle()).toBe(false);
    expect(ctx.ofKind('analyser').some((n) => n.outputs.includes(master(ctx)))).toBe(true); // the track's meter feeds the master directly
  });
  it('an unrouted document has no bus nodes and no fallback', async () => {
    const { graph } = await playing(doc([aud(TRACKS.a1)]));
    expect(graph.usesBusFallback()).toBe(false);
  });
  it('puts the track dynamics after the EQ/pan and before the meter', async () => {
    const d = doc([aud(TRACKS.a1, { dynamics: { compressor: {}, limiter: {} }, pan: 0.2 })]);
    const { ctx } = await playing(d);
    expect(ctx.ofKind('compressor')).toHaveLength(2);
    const pan = ctx.ofKind('panner')[0];
    const analyser = ctx.ofKind('analyser').find((a) => ctx.reaches(pan, a))!;
    expect(ctx.reaches(pan, ctx.ofKind('compressor')[0])).toBe(true);
    expect(ctx.reaches(ctx.ofKind('compressor')[0], analyser)).toBe(true);
  });
  it('builds gate and de-esser worklets only when AudioWorklet is available', async () => {
    const d = doc([aud(TRACKS.a1, { dynamics: { gate: {}, deesser: {} } })]);
    const without = await playing(d, false);
    expect(without.ctx.nodes.filter((n) => n.kind.startsWith('worklet:'))).toHaveLength(0);
    const withWorklet = await playing(d, true);
    expect(withWorklet.ctx.nodes.filter((n) => n.kind.startsWith('worklet:')).map((n) => n.kind)).toEqual(['worklet:rb-gate', 'worklet:rb-deesser']);
    expect(withWorklet.graph.dspSupport().worklet).toBe(true);
  });
  it('turns the worklet off when its module is refused', async () => {
    const ctx = new FakeContext();
    ctx.audioWorklet = { addModule: async () => Promise.reject(new Error('refused')) };
    const graph = new AudioGraph(ctx as unknown as AudioContext, { urlFor: () => null, flags, dspSupport: { nodes: true, worklet: true } });
    await graph.play(doc([aud(TRACKS.a1, { dynamics: { gate: {}, compressor: {} } })]), 0, 1);
    expect(graph.dspSupport()).toEqual({ nodes: true, worklet: false });
    expect(ctx.ofKind('compressor')).toHaveLength(1);
  });
  it('creates no nodes between plays (graph is rebuilt per play, not per tick)', async () => {
    const d = withBuses([aud(TRACKS.a1, { busId: 'b1', dynamics: { compressor: {} } })], [bus('b1', { dynamics: { limiter: {} } })]);
    const { ctx, graph } = await playing(d);
    const count = ctx.nodes.length;
    graph.applyTrackFlags(d);
    graph.applyTrackFlags(d);
    graph.meters();
    expect(ctx.nodes.length).toBe(count);
  });
  it('mixer basics unchanged: mute still gates the track', async () => {
    const d = doc([aud(TRACKS.a1)]);
    const ctx = new FakeContext();
    let mute = false;
    const graph = new AudioGraph(ctx as unknown as AudioContext, { urlFor: () => null, flags: () => ({ enabled: true, mute, solo: false, locked: false }), dspSupport: { nodes: true, worklet: false } });
    await graph.play(d, 0, 1);
    const gates = () => ctx.ofKind('gain').map((n) => (n as unknown as { gain: { value: number } }).gain.value);
    expect(gates().filter((v) => v === 0)).toHaveLength(0);
    mute = true;
    graph.applyTrackFlags(d);
    expect(gates()).toContain(0);
  });
  it('publishes the test hook only when asked', async () => {
    const g = globalThis as { __RB_TEST_HOOKS__?: boolean; __rbAudioGraph?: unknown };
    delete g.__rbAudioGraph;
    await playing(doc([aud(TRACKS.a1)]));
    expect(g.__rbAudioGraph).toBeUndefined();
    g.__RB_TEST_HOOKS__ = true;
    await playing(doc([aud(TRACKS.a1)]));
    expect(g.__rbAudioGraph).toBeDefined();
    delete g.__RB_TEST_HOOKS__;
    delete g.__rbAudioGraph;
  });
});

describe('the clip channel map is heard in the preview (the render\'s pan filter, as channel routes)', () => {
  /** Plays one mapped clip with a decodable sound and returns the routes of its splitter into its merger. */
  async function routesFor(channelMap: 'stereo' | 'left' | 'right' | 'mono' | 'swap' | undefined) {
    const ctx = new FakeContext();
    (ctx as unknown as { decodeAudioData: () => Promise<unknown> }).decodeAudioData = async () => ({ duration: 10, numberOfChannels: 2, sampleRate: 48000, length: 480000 });
    vi.stubGlobal('fetch', async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(8) }));
    const d = doc([track(TRACKS.a1, 'audio', [{ ...media('c1', 0, 30), channelMap }])]);
    const graph = new AudioGraph(ctx as unknown as AudioContext, { urlFor: () => '/sound', flags, dspSupport: { nodes: true, worklet: false } });
    await graph.play(d, 0, 1);
    const source = ctx.ofKind('source')[0] as FakeNode;
    const mergers = ctx.ofKind('merger');
    // Track meters use a splitter too: the map's splitter is the one fed (through the stereo up-mix) by the source.
    const mapSplitter = ctx.ofKind('splitter').find((s) => source.outputs.some((up) => up.outputs.includes(s)));
    const routes: string[] = [];
    if (mapSplitter && mergers[0]) {
      for (const r of mapSplitter.routes) {
        if (r.dest === mergers[0]) routes.push(`${r.output}->${r.input}@1`);
        else for (const out of r.dest.routes) if (out.dest === mergers[0]) routes.push(`${r.output}->${out.input}@${(r.dest as unknown as { gain: { value: number } }).gain.value}`);
      }
    }
    return { ctx, source, mergers, routes: routes.sort() };
  }

  it('stereo (or no map) adds no node: the source feeds the clip gain directly', async () => {
    for (const map of ['stereo', undefined] as const) {
      const { mergers, source } = await routesFor(map);
      expect(mergers).toHaveLength(0);
      expect(source.outputs.map((n) => n.kind)).toEqual(['gain']);
    }
  });
  it('left: the left channel on both sides', async () => {
    expect((await routesFor('left')).routes).toEqual(['0->0@1', '0->1@1']);
  });
  it('right: the right channel on both sides', async () => {
    expect((await routesFor('right')).routes).toEqual(['1->0@1', '1->1@1']);
  });
  it('swap: the sides exchanged', async () => {
    expect((await routesFor('swap')).routes).toEqual(['0->1@1', '1->0@1']);
  });
  it('mono: half of each channel on both sides', async () => {
    expect((await routesFor('mono')).routes).toEqual(['0->0@0.5', '0->1@0.5', '1->0@0.5', '1->1@0.5']);
  });
  it('a mapped clip still reaches the master, with a stereo up-mix before the split (a mono file is on both sides first)', async () => {
    const { ctx, source } = await routesFor('left');
    const upmix = source.outputs[0] as FakeNode & { channelCount?: number; channelInterpretation?: string };
    expect(upmix.kind).toBe('gain');
    expect(upmix.channelCount).toBe(2);
    expect(upmix.channelInterpretation).toBe('speakers');
    expect(ctx.reaches(source, ctx.destination)).toBe(true);
  });
});
