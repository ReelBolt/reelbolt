/** @vitest-environment node */
/**
 * The headless half of the compositor: planning, parity, the decoder pool's invariant and
 * buffering, the dropped-frame ledger, and the overlay rasteriser against a recording context.
 * Nothing here needs WebCodecs, WebGL or a DOM — those are the browser's and the e2e spec's.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { __resetGlProbeForTests, hasWebGl2 } from '../index';
import { aspectScale, COMPOSITOR_CONTEXT_ATTRIBUTES, FRAGMENT_SHADER, imageSize, VERTEX_SHADER } from '../gl-compositor';
import { activeAudioAt, activeLayersAt, dbToGain, evaluateKeyframed, hasTransform, IDENTITY_TRANSFORM, layerTransformAt, mediaRefOf, referencedAssets, sourceFrameFor, sourceTimeSec } from '../scheduler';
import { classifyItem, classifyParity, parityAtFrame } from '../parity';
import { assertPoolInvariant, DecodeLane, DecoderPool, DEFAULT_POOL_CONFIG, type DecodedFrame, type FrameSource } from '../decoder-pool';
import { DropFrameLedger } from '../drop-frame-ledger';
import { rasterizeOverlays, sanitizeFontFamily } from '../overlay-raster';
import { unavailablePreviewSession } from '../session';
import { MAX_CLOCK_LEAD_SEC } from '../audio-graph';
import { AudioGraph } from '../audio-graph';
import { doc, standardDoc, TRACKS, media, track } from '../../__tests__/fixtures';
import type { MediaClip, TitleClip, Transition } from '../../types';

const d = standardDoc();
const flags = (track: { locked: boolean; enabled: boolean; mute: boolean; solo: boolean }) => ({
  locked: track.locked,
  enabled: track.enabled,
  mute: track.mute,
  solo: track.solo,
});

describe('scheduler', () => {
  it('returns the active video layers back-to-front and honours track enable', () => {
    const at45 = activeLayersAt(d, 45, flags);
    expect(at45.map((l) => l.item.id)).toEqual(['c1', 't1']);
    expect(at45[1].layerIndex).toBe(1);
    expect(activeLayersAt(d, 130, flags)).toEqual([]); // the gap on V1, title ended at 90
    const disabled = activeLayersAt(d, 45, (t) => ({ ...flags(t), enabled: t.id !== TRACKS.v2 }));
    expect(disabled.map((l) => l.item.id)).toEqual(['c1']);
  });

  it('maps timeline frames to source time honouring sourceIn and playbackRate', () => {
    const clip = media('x', 100, 160, 300, { playbackRate: 2 });
    expect(sourceFrameFor(clip, 100)).toBe(300);
    expect(sourceFrameFor(clip, 110)).toBe(320);
    expect(sourceTimeSec(clip, 115, { num: 30, den: 1 })).toBeCloseTo(330 / 30, 9);
  });

  it('evaluates keyframed numbers with the contract interpolations', () => {
    const kf = { value: 5, keys: [{ frame: 0, value: 0, interp: 'linear' as const }, { frame: 10, value: 10, interp: 'step' as const }, { frame: 20, value: 20, interp: 'linear' as const }] };
    expect(evaluateKeyframed(kf, -1)).toBe(5);
    expect(evaluateKeyframed(kf, 5)).toBe(5);
    expect(evaluateKeyframed(kf, 15)).toBe(10); // step holds
    expect(evaluateKeyframed(kf, 20)).toBe(20);
    expect(evaluateKeyframed(kf, 99)).toBe(20);
    expect(evaluateKeyframed(null, 3, 7)).toBe(7);
    expect(evaluateKeyframed({ value: 3, keys: null }, 3)).toBe(3);
    const eased = { value: 0, keys: [{ frame: 0, value: 0, interp: 'easeInQuad' as const }, { frame: 10, value: 100, interp: 'linear' as const }] };
    expect(evaluateKeyframed(eased, 5)).toBe(25);
    expect(dbToGain(-6)).toBeCloseTo(0.501, 2);
    expect(dbToGain(0)).toBe(1);
  });

  it('computes audible gains honouring mute and solo', () => {
    expect(activeAudioAt(d, 30, flags).map((a) => [a.clip.id, Math.round(a.gain * 1000) / 1000])).toEqual([
      ['a1', 0.501],
      ['a2', Math.round(dbToGain(-6) * 1000) / 1000], // a2's envelope: -12 → 0 over 60 frames, linear: -6 at 30
    ]);
    const muted = activeAudioAt(d, 30, (t) => ({ ...flags(t), mute: t.id === TRACKS.a1 }));
    expect(muted.find((a) => a.clip.id === 'a1')!.gain).toBe(0);
    const solo = activeAudioAt(d, 30, (t) => ({ ...flags(t), solo: t.id === TRACKS.a2 }));
    expect(solo.find((a) => a.clip.id === 'a1')!.gain).toBe(0);
    expect(solo.find((a) => a.clip.id === 'a2')!.gain).toBeGreaterThan(0);
  });

  it('lists referenced assets once and builds mediaRef tokens', () => {
    expect(referencedAssets(d)).toEqual([{ kind: 'projectFile', id: '00000000-0000-4000-8000-000000000001' }]);
    expect(mediaRefOf({ kind: 'projectFile', projectFileId: 'abc' })).toBe('file:abc');
    expect(mediaRefOf({ kind: 'stepResult', stepResultId: 'xyz' })).toBe('step_result:xyz');
    expect(mediaRefOf({ kind: 'projectFile' })).toBeNull();
  });
});

describe('parity', () => {
  it('classifies the fixture: cuts faithful, title approximate; and the badge reads the current frame', () => {
    const summary = classifyParity(d);
    expect(summary.worst).toBe('approximate');
    expect(summary.approximateCount).toBe(1);
    expect(parityAtFrame(d, 200).worst).toBe('faithful');
    expect(parityAtFrame(d, 45).worst).toBe('approximate');
  });

  it('a retimed clip is approximate (the timing is previewed, the audio stretch is not); a dissolve is faithful, a wipe approximate', () => {
    const d2 = structuredClone(d);
    (d2.tracks[0].items[0] as { playbackRate?: number }).playbackRate = 2;
    expect(classifyParity(d2).worst).toBe('approximate');
    expect(classifyParity(d2).items.find((i) => i.itemId === 'c1')!.reason).toMatch(/Fast motion.*final render is exact/);
    (d2.tracks[0].items[0] as { playbackRate?: number }).playbackRate = 0.5;
    expect(classifyParity(d2).items.find((i) => i.itemId === 'c1')!.reason).toMatch(/Slow motion/);
    const wipe: Transition = { id: 'x', type: 'transition', name: 'x', recordIn: 60, recordOut: 60, transitionType: 'wipeleft', easing: 'linear', enabled: true, provenance: null };
    // T-008b: a word with an xfade kernel is previewed with the render's maths; four need a caveat; an unknown word stays a dissolve.
    expect(classifyItem(wipe).parity).toBe('faithful');
    expect(classifyItem(wipe).reason).toMatch(/render's own transition maths/);
    expect(classifyItem({ ...wipe, transitionType: 'fade' }).parity).toBe('faithful');
    for (const word of ['dissolve', 'distance', 'fadegrays', 'hblur']) expect(classifyItem({ ...wipe, transitionType: word }).parity).toBe('approximate');
    const unknown = classifyItem({ ...wipe, transitionType: 'cover' });
    expect(unknown.parity).toBe('approximate');
    expect(unknown.reason).toMatch(/previews as a dissolve/);
    const keyed = media('k', 0, 10, 0, { transform: { scale: { value: null, keys: [{ frame: 0, value: 1, interp: 'linear' }, { frame: 10, value: 2, interp: 'linear' }] } } });
    expect(classifyItem(keyed).parity).toBe('approximate');
    expect(classifyItem(media('f', 0, 10, 0, { fadeInFrames: 3, reverse: true })).parity).toBe('faithful');
  });

  it('P5: transforms, fades, reverse/hold and transition legs are planned as the renderer plans them', () => {
    const clip = media('t', 100, 160, 300, { transform: { positionX: { value: 0.25 }, scale: { value: null, keys: [{ frame: 100, value: 1, interp: 'linear' }, { frame: 160, value: 2, interp: 'linear' }] } }, fadeInFrames: 10, crop: { left: 0.1, right: 0.2 } });
    const at130 = layerTransformAt(clip, 130);
    expect(at130.x).toBe(0.25);
    expect(at130.scale).toBeCloseTo(1.5, 6);
    expect(at130.opacity).toBe(1);
    expect(layerTransformAt(clip, 104).opacity).toBeCloseTo(0.5, 6);
    expect(at130.crop).toEqual({ left: 0.1, top: 0, right: 0.2, bottom: 0 });
    expect(hasTransform(at130)).toBe(true);
    expect(layerTransformAt(media('p', 0, 10), 5)).toBe(IDENTITY_TRANSFORM);

    const reversed = media('r', 100, 160, 300, { reverse: true });
    expect(sourceFrameFor(reversed, 100)).toBe(360);
    expect(sourceFrameFor(reversed, 159)).toBe(301);
    expect(sourceFrameFor(media('h', 100, 160, 300, { hold: true }), 150)).toBe(300);

    const transDoc = doc([
      track(TRACKS.v1, 'video', [
        media('a', 0, 30, 0),
        { id: 'x', type: 'transition', name: 'x', recordIn: 30, recordOut: 40, transitionType: 'fade', easing: 'linear', enabled: true, provenance: null },
        media('b', 40, 80, 50),
      ]),
    ]);
    const legs = activeLayersAt(transDoc, 34, flags);
    expect(legs.map((l) => [l.item.id, l.opacity])).toEqual([['a', 1], ['b', 0.5]]);
    expect(sourceFrameFor(legs[1].item as MediaClip, 34)).toBe(44); // pulled back before its own start
    expect(activeLayersAt(transDoc, 20, flags).map((l) => l.item.id)).toEqual(['a']);
    expect(activeAudioAt(doc([track(TRACKS.a1, 'audio', [media('q', 0, 20, 0, { fadeInFrames: 10, hold: false })], { gainDb: { value: -6 } })]), 4, flags)[0].gain).toBeCloseTo(dbToGain(-6) * 0.5, 6);
  });
});

describe('decoder pool', () => {
  it('enforces the measured invariant ceiling >= lookAhead + 2', () => {
    expect(() => assertPoolInvariant(DEFAULT_POOL_CONFIG)).not.toThrow();
    expect(() => assertPoolInvariant({ lookAheadFrames: 24, maxDecodedBufferedFrames: 18 })).toThrow(RangeError);
    expect(() => assertPoolInvariant({ lookAheadFrames: 16, maxDecodedBufferedFrames: 18 })).not.toThrow();
    expect(() => assertPoolInvariant({ lookAheadFrames: 17, maxDecodedBufferedFrames: 18 })).toThrow(/invariant/);
    expect(() => new DecoderPool(async () => null, { lookAheadFrames: 24, maxDecodedBufferedFrames: 18 })).toThrow(RangeError);
  });

  function fakeSource(): FrameSource & { requested: number[]; closed: number[] } {
    const requested: number[] = [];
    const closed: number[] = [];
    return {
      requested,
      closed,
      async getFrame(sourceFrame: number): Promise<DecodedFrame | null> {
        requested.push(sourceFrame);
        if (sourceFrame > 1000) return null;
        return { sourceFrame, image: {} as CanvasImageSource, close: () => closed.push(sourceFrame) };
      },
      dispose: vi.fn(),
    };
  }

  it('prefetches the look-ahead window and never exceeds the ceiling', async () => {
    const source = fakeSource();
    const lane = new DecodeLane(source, { lookAheadFrames: 4, maxDecodedBufferedFrames: 6 });
    lane.prefetch(10);
    await Promise.resolve();
    await new Promise((r) => setTimeout(r, 0));
    expect(source.requested).toEqual([10, 11, 12, 13, 14]);
    expect(lane.stats().buffered).toBe(5);
    expect(lane.peek(10)).not.toBeNull();
    expect(lane.peek(99)).toBeNull();
    expect(lane.stats().misses).toBe(1);

    // Buffer is at 5 of 6; prefetching 10 more may only add until the ceiling.
    lane.prefetch(15);
    await new Promise((r) => setTimeout(r, 0));
    expect(lane.stats().buffered).toBeLessThanOrEqual(6);
  });

  it('evicts oldest-first, never the frame on screen, and counts evicted-undisplayed frames', async () => {
    const source = fakeSource();
    const lane = new DecodeLane(source, { lookAheadFrames: 2, maxDecodedBufferedFrames: 4 });
    await lane.fetch(0); // on screen = 0
    await new Promise((r) => setTimeout(r, 0));
    for (const f of [1, 2, 3, 4, 5]) await lane.fetch(f);
    await new Promise((r) => setTimeout(r, 0));
    // Frame 0 was on screen until frame 1 was fetched; afterwards the oldest frames go first.
    expect(lane.stats().buffered).toBeLessThanOrEqual(4);
    expect(lane.peek(5)).not.toBeNull();
    expect(source.closed.length).toBeGreaterThan(0);
    lane.dispose();
    expect(source.dispose).toHaveBeenCalled();
  });

  it('trimBehind releases frames behind the playhead', async () => {
    const source = fakeSource();
    const lane = new DecodeLane(source, { lookAheadFrames: 3, maxDecodedBufferedFrames: 8 });
    lane.prefetch(0);
    await new Promise((r) => setTimeout(r, 0));
    expect(lane.stats().buffered).toBe(4);
    lane.peek(3);
    lane.trimBehind(3);
    expect(lane.stats().buffered).toBe(2); // frames 2 and 3 remain (keep one behind)
  });

  it('opens one lane per layer and retains only live layers', async () => {
    const opened: string[] = [];
    const pool = new DecoderPool(async (key) => {
      opened.push(key);
      return fakeSource();
    });
    const a = await pool.lane('layer:v1', 'asset-a');
    const b = await pool.lane('layer:v2', 'asset-a');
    expect(a).not.toBe(b);
    expect(opened).toEqual(['asset-a', 'asset-a']);
    expect(await pool.lane('layer:v1', 'asset-a')).toBe(a);
    pool.retainOnly(new Set(['layer:v2']));
    expect(pool.peekLane('layer:v1')).toBeNull();
    expect(pool.peekLane('layer:v2')).toBe(b);
    const none = new DecoderPool(async () => null);
    expect(await none.lane('x', 'y')).toBeNull();
  });

  it('PA-3: a layer that cuts to another asset gets that asset\'s lane, never the previous one', async () => {
    const opened: string[] = [];
    const pool = new DecoderPool(async (key) => {
      opened.push(key);
      return fakeSource();
    });
    const a = await pool.lane('layer:v1', 'asset-a');
    const b = await pool.lane('layer:v1', 'asset-b');
    expect(b).not.toBeNull();
    expect(b).not.toBe(a);
    expect(opened).toEqual(['asset-a', 'asset-b']);
    // The layer now shows asset B: A's lane is released, B's is the one a peek finds.
    expect(pool.peekLane('layer:v1')).toBe(b);
  });

  it('an A/B/A/B montage on one layer opens each source once (recent lanes stay parked)', async () => {
    const opened: string[] = [];
    const sources: Array<ReturnType<typeof fakeSource>> = [];
    const pool = new DecoderPool(async (key) => {
      opened.push(key);
      const source = fakeSource();
      sources.push(source);
      return source;
    });
    const a = await pool.lane('layer:v1', 'asset-a');
    const b = await pool.lane('layer:v1', 'asset-b');
    expect(await pool.lane('layer:v1', 'asset-a')).toBe(a);
    expect(await pool.lane('layer:v1', 'asset-b')).toBe(b);
    expect(await pool.lane('layer:v1', 'asset-a')).toBe(a);
    expect(opened).toEqual(['asset-a', 'asset-b']);
    // Only the asset on screen is the layer's lane; the parked one is not drawn from.
    expect(pool.peekLane('layer:v1')).toBe(a);
    expect(sources.every((s) => (s.dispose as ReturnType<typeof vi.fn>).mock.calls.length === 0)).toBe(true);
  });

  it('parked lanes are bounded by count and by a grace period, and hold no decoded frames', async () => {
    let clock = 0;
    const sources = new Map<string, ReturnType<typeof fakeSource>>();
    const pool = new DecoderPool(
      async (key) => {
        const source = fakeSource();
        sources.set(key, source);
        return source;
      },
      DEFAULT_POOL_CONFIG,
      { parkedLanes: 2, parkMs: 10_000, now: () => clock },
    );
    const a = (await pool.lane('layer:v1', 'asset-a'))!;
    await a.fetch(0);
    expect(a.stats().buffered).toBeGreaterThan(0);
    await pool.lane('layer:v1', 'asset-b'); // parks A
    expect(a.stats().buffered).toBe(0);
    await pool.lane('layer:v1', 'asset-c'); // parks B
    await pool.lane('layer:v1', 'asset-d'); // parks C: A is the oldest beyond the count, disposed
    expect(sources.get('asset-a')!.dispose).toHaveBeenCalled();
    expect(sources.get('asset-b')!.dispose).not.toHaveBeenCalled();
    clock = 10_001;
    pool.retainOnly(new Set(['layer:v1'])); // any later call expires lanes parked too long
    expect(sources.get('asset-b')!.dispose).toHaveBeenCalled();
    expect(sources.get('asset-c')!.dispose).toHaveBeenCalled();
    expect(sources.get('asset-d')!.dispose).not.toHaveBeenCalled();
    pool.dispose();
    expect(sources.get('asset-d')!.dispose).toHaveBeenCalled();
  });

  it('PA-4: concurrent requests for one (layer, asset) open the source exactly once', async () => {
    let opens = 0;
    let release: (() => void) | null = null;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const pool = new DecoderPool(async () => {
      opens += 1;
      await gate;
      return fakeSource();
    });
    // The render tick fires an un-awaited present every deadline while the first open is pending.
    const pending = Array.from({ length: 30 }, () => pool.lane('layer:v1', 'asset-a'));
    release!();
    const lanes = await Promise.all(pending);
    expect(opens).toBe(1);
    expect(new Set(lanes).size).toBe(1);
    expect(lanes[0]).not.toBeNull();
  });

  it('PA-4: a source that cannot be opened is not re-requested on every frame', async () => {
    let opens = 0;
    const pool = new DecoderPool(async () => {
      opens += 1;
      return null;
    });
    for (let i = 0; i < 10; i++) expect(await pool.lane('layer:v1', 'asset-a')).toBeNull();
    expect(opens).toBe(1);
    expect(pool.failed('layer:v1', 'asset-a')).toBe(true);
    // Leaving the screen forgets the failure, so a later visit tries again.
    pool.retainOnly(new Set());
    expect(pool.failed('layer:v1', 'asset-a')).toBe(false);
  });

  it('retries a source that failed to open with a growing, capped backoff while its layer stays on screen', async () => {
    let clock = 0;
    let opens = 0;
    let failuresLeft = 2;
    const pool = new DecoderPool(
      async () => {
        opens += 1;
        if (failuresLeft > 0) {
          failuresLeft -= 1;
          return null;
        }
        return fakeSource();
      },
      DEFAULT_POOL_CONFIG,
      { now: () => clock },
    );
    expect(await pool.lane('layer:v1', 'asset-a')).toBeNull();
    const firstRetry = pool.retryAt('layer:v1', 'asset-a')!;
    expect(firstRetry).toBeGreaterThan(0);
    // Not before the backoff has elapsed.
    expect(await pool.lane('layer:v1', 'asset-a')).toBeNull();
    expect(opens).toBe(1);

    clock = firstRetry;
    expect(await pool.lane('layer:v1', 'asset-a')).toBeNull();
    expect(opens).toBe(2);
    expect(pool.failed('layer:v1', 'asset-a')).toBe(true);
    const secondRetry = pool.retryAt('layer:v1', 'asset-a')!;
    expect(secondRetry - clock).toBeGreaterThan(firstRetry);

    clock = secondRetry;
    expect(await pool.lane('layer:v1', 'asset-a')).not.toBeNull();
    expect(opens).toBe(3);
    expect(pool.failed('layer:v1', 'asset-a')).toBe(false);
    expect(pool.retryAt('layer:v1', 'asset-a')).toBeNull();

    // However long it keeps failing, the wait is capped.
    const stubborn = new DecoderPool(async () => null, DEFAULT_POOL_CONFIG, { now: () => clock });
    for (let i = 0; i < 12; i++) {
      await stubborn.lane('layer:v1', 'asset-z');
      clock = stubborn.retryAt('layer:v1', 'asset-z')!;
    }
    await stubborn.lane('layer:v1', 'asset-z');
    expect(stubborn.retryAt('layer:v1', 'asset-z')! - clock).toBeLessThanOrEqual(30_000);
  });
});

describe('drop-frame ledger', () => {
  it('records presented and dropped deadlines, lateness p50 and fps from timestamps', () => {
    const ledger = new DropFrameLedger();
    ledger.recordPresented(5, 1000);
    ledger.recordPresented(15, 1033);
    ledger.recordPresented(25, 1066, true);
    ledger.recordDropped(2);
    const s = ledger.snapshot();
    expect(s.presentedFrames).toBe(3);
    expect(s.droppedFrames).toBe(2);
    expect(s.deadlines).toBe(5);
    expect(s.latenessP50Ms).toBe(15);
    expect(s.achievedFps).toBeCloseTo(2000 / 66, 1);
    expect(s.lastFrameWasRepeat).toBe(true);
    ledger.reset();
    expect(ledger.snapshot().deadlines).toBe(0);
  });
});

describe('overlay rasteriser', () => {
  function recordingContext() {
    const calls: string[] = [];
    return {
      calls,
      ctx: {
        canvas: { width: 1920, height: 1080 },
        clearRect: () => calls.push('clear'),
        fillRect: () => calls.push('fillRect'),
        strokeRect: () => calls.push('strokeRect'),
        fillText: (t: string) => calls.push(`text:${t}`),
        fillStyle: '',
        strokeStyle: '',
        lineWidth: 1,
        font: '',
        textAlign: 'left' as CanvasTextAlign,
        textBaseline: 'top' as CanvasTextBaseline,
        globalAlpha: 1,
      },
    };
  }

  it('draws titles, captions, generators and insert quads; skips disabled items and media clips', () => {
    const { ctx, calls } = recordingContext();
    const drawn = rasterizeOverlays(ctx, [
      { item: d.tracks[1].items[0], layerIndex: 1 },
      { item: { ...(d.tracks[1].items[0] as TitleClip), id: 'off', enabled: false }, layerIndex: 2 },
      { item: d.tracks[0].items[0], layerIndex: 0 },
      { item: { id: 'g', type: 'generatorClip', name: 'g', recordIn: 0, recordOut: 10, generatorType: 'color', generatorConfig: { color: '#ff0000' } }, layerIndex: 3 },
      {
        item: {
          id: 'ins',
          type: 'insertClip',
          name: 'ins',
          recordIn: 0,
          recordOut: 10,
          quad: { value: { topLeft: { x: 0.1, y: 0.1 }, topRight: { x: 0.5, y: 0.1 }, bottomRight: { x: 0.5, y: 0.5 }, bottomLeft: { x: 0.1, y: 0.5 } }, keys: null },
        },
        layerIndex: 4,
      },
    ]);
    expect(drawn).toBe(3);
    expect(calls[0]).toBe('clear');
    expect(calls).toContain('text:Lower third');
    expect(calls).toContain('strokeRect');
    expect(ctx.fillStyle).toBe('#4C6EF5');
  });

  it('sanitises font families', () => {
    expect(sanitizeFontFamily('Inter')).toBe('Inter');
    expect(sanitizeFontFamily('Inter; url(x)')).toBe('sans-serif');
    expect(sanitizeFontFamily(undefined)).toBe('sans-serif');
  });
});

describe('unavailable session', () => {
  it('seeks, reports zero stats and never plays', () => {
    const session = unavailablePreviewSession('no proxies');
    let notified = 0;
    session.subscribe(() => notified++);
    session.seek(12);
    session.play(1);
    expect(session.currentFrame()).toBe(12);
    expect(session.isPlaying()).toBe(false);
    expect(session.stats().droppedFrames).toBe(0);
    expect(notified).toBe(1);
    expect(session.reason).toBe('no proxies');
  });
});

describe('AudioGraph clock', () => {
  function fakeContext(state: AudioContextState) {
    const node = { connect: vi.fn(), disconnect: vi.fn(), gain: { value: 1, setValueAtTime: vi.fn(), linearRampToValueAtTime: vi.fn() } };
    return {
      state,
      currentTime: 10,
      destination: {},
      createGain: () => node,
      resume: vi.fn(async () => undefined),
      close: vi.fn(async () => undefined),
    } as unknown as AudioContext;
  }
  const flags = () => ({ enabled: true, mute: false, solo: false, locked: false });

  it('is not the master clock while the context is not running', async () => {
    const context = fakeContext('suspended');
    const graph = new AudioGraph(context, { urlFor: () => null, flags });
    await graph.play(standardDoc(), 0, 1);
    // A suspended context's currentTime does not advance; a master clock that never moves would
    // freeze the video, so the graph declines and the session uses the wall clock.
    expect(graph.playheadFrame(30)).toBeNull();
  });

  it('never reads before the start frame during the 50 ms scheduling lead', async () => {
    const context = fakeContext('running');
    const graph = new AudioGraph(context, { urlFor: () => null, flags });
    await graph.play(standardDoc(), 0, 1);
    // currentTime has not moved yet: the raw clock would be -1.5 frames, which the session would
    // read as "before the timeline" and pause. The clock reports the start frame instead.
    expect(graph.playheadFrame(30)).toBe(0);
  });

  function decodingContext(decodeSeconds: number) {
    const context = fakeContext('running') as unknown as { currentTime: number } & Record<string, unknown>;
    const starts: { when: number; offset: number; duration: number }[] = [];
    const param = () => ({ value: 1, setValueCurveAtTime: vi.fn(), setValueAtTime: vi.fn() });
    Object.assign(context, {
      createBufferSource: () => ({
        buffer: null,
        playbackRate: { value: 1 },
        connect: vi.fn(),
        disconnect: vi.fn(),
        stop: vi.fn(),
        start: (when: number, offset: number, duration: number) => starts.push({ when, offset, duration }),
      }),
      createGain: () => ({ connect: vi.fn(), disconnect: vi.fn(), gain: param() }),
      // Fetch + decode of the whole file takes `decodeSeconds` of audio-clock time.
      decodeAudioData: async () => {
        context.currentTime += decodeSeconds;
        return { duration: 60, numberOfChannels: 2, length: 60 * 48000, sampleRate: 48000 };
      },
    });
    return { context: context as unknown as AudioContext, starts };
  }

  it('PA-5: a clip whose buffer decodes late joins in sync instead of starting late from its first sample', async () => {
    vi.stubGlobal('fetch', async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(8) }));
    try {
      const { context, starts } = decodingContext(2);
      const one = doc([track('a', 'audio', [media('a1', 0, 900)])]);
      const graph = new AudioGraph(context, { urlFor: () => '/a.wav', flags });
      await graph.play(one, 0, 1);
      expect(starts).toHaveLength(1);
      const [start] = starts;
      // The clock has run 2 s while decoding; the source must start no earlier than "now" and read
      // from the position the clock is at when it starts, so picture and sound agree.
      expect(start.when).toBeGreaterThanOrEqual(12);
      const clockFrameAtStart = graph.playheadFrame(30)! + (start.when - context.currentTime) * 30;
      expect(start.offset * 30).toBeCloseTo(clockFrameAtStart, 3);
      expect(start.offset).toBeGreaterThan(1.9);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('a source with no URL yet is asked again on the next play (no cached silence)', async () => {
    const fetches: string[] = [];
    vi.stubGlobal('fetch', async (url: string) => {
      fetches.push(url);
      return { ok: true, arrayBuffer: async () => new ArrayBuffer(8) };
    });
    try {
      const { context, starts } = decodingContext(0);
      let url: string | null = null;
      const one = doc([track('a', 'audio', [media('a1', 0, 900)])]);
      const graph = new AudioGraph(context, { urlFor: () => url, flags });
      await graph.play(one, 0, 1);
      expect(starts).toHaveLength(0);
      url = '/a.wav';
      await graph.play(one, 0, 1);
      expect(fetches).toEqual(['/a.wav']);
      expect(starts).toHaveLength(1);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('reports the frame from currentTime once running', async () => {
    const context = fakeContext('running');
    const graph = new AudioGraph(context, { urlFor: () => null, flags });
    await graph.play(standardDoc(), 30, 1);
    (context as unknown as { currentTime: number }).currentTime = 11.05;
    expect(graph.playheadFrame(30)).toBeCloseTo(60, 5);
  });
});

describe('AudioGraph smoothed clock', () => {
  const flags = () => ({ enabled: true, mute: false, solo: false, locked: false });
  function ctx() {
    const node = { connect: vi.fn(), disconnect: vi.fn(), gain: { value: 1, setValueAtTime: vi.fn(), linearRampToValueAtTime: vi.fn() } };
    return { state: 'running', currentTime: 10, destination: {}, createGain: () => node, resume: vi.fn(async () => undefined), close: vi.fn(async () => undefined) } as unknown as AudioContext & { currentTime: number };
  }
  afterEach(() => vi.restoreAllMocks());

  it('is continuous between buffer steps (a 21.3 ms step does not skip a 60 fps picture)', async () => {
    let wall = 1000;
    vi.spyOn(performance, 'now').mockImplementation(() => wall);
    const context = ctx();
    const graph = new AudioGraph(context, { urlFor: () => null, flags });
    await graph.play(standardDoc(), 0, 1);
    const frames: number[] = [];
    for (let i = 0; i < 60; i++) {
      wall += 16.667;
      context.currentTime = 10.05 + Math.floor((i * 16.667) / 21.333) * 0.021333; // device steps
      frames.push(Math.floor(graph.playheadFrame(60)!));
    }
    const steps = frames.slice(1).map((f, i) => f - frames[i]);
    expect(Math.max(...steps)).toBeLessThanOrEqual(1);
  });

  it('does not run ahead of a slow audio clock by more than the lead', async () => {
    let wall = 1000;
    vi.spyOn(performance, 'now').mockImplementation(() => wall);
    const context = ctx();
    const graph = new AudioGraph(context, { urlFor: () => null, flags });
    await graph.play(standardDoc(), 0, 1);
    // The device underruns: the audio clock advances at half speed for 4 s of wall time.
    for (let i = 0; i < 240; i++) {
      wall += 16.667;
      context.currentTime += 0.016667 / 2;
    }
    const clockFrame = (context.currentTime - 10.05) * 30;
    const shown = graph.playheadFrame(30)!;
    expect(shown - clockFrame).toBeLessThanOrEqual(MAX_CLOCK_LEAD_SEC * 30 + 1e-6);
    expect(shown - clockFrame).toBeGreaterThanOrEqual(0);
  });
});

describe('hasWebGl2 capability probe', () => {
  afterEach(() => vi.unstubAllGlobals());

  function stubProbe(webgl2Available: boolean): { created: () => number; lastAttrs: () => unknown } {
    __resetGlProbeForTests();
    let created = 0;
    let lastAttrs: unknown;
    vi.stubGlobal('document', {
      createElement: () => {
        created += 1;
        return {
          getContext: (kind: string, attrs?: unknown) => {
            lastAttrs = attrs;
            return kind === 'webgl2' && webgl2Available ? {} : null;
          },
        };
      },
    });
    return { created: () => created, lastAttrs: () => lastAttrs };
  }

  it('probes with the compositor\'s exact attributes and reuses one canvas (no per-call leak)', () => {
    const { created, lastAttrs } = stubProbe(true);
    expect(hasWebGl2(null)).toBe(true);
    expect(created()).toBe(1); // one shared canvas, never a fresh throwaway per call
    expect(hasWebGl2(null)).toBe(true); // reused
    expect(created()).toBe(1); // still one canvas
    expect(lastAttrs()).toEqual(COMPOSITOR_CONTEXT_ATTRIBUTES);
  });

  it('reports unavailable (never throws) when that context cannot be created', () => {
    stubProbe(false);
    expect(hasWebGl2(null)).toBe(false);
  });
});

// GLSL ES 3.00 is strict: a uniform name must carry the SAME type in the vertex and
// fragment stages or the program fails to link (the compositor then falls back). This
// static check is the stand-in for a link we cannot run headless — it caught a
// vertex `vec2 uOffset` / fragment `vec3 uOffset` collision that broke the compositor
// on every browser.
function shaderUniformTypes(source: string): Record<string, string[]> {
  const map: Record<string, string[]> = {};
  const re = /^uniform\s+([A-Za-z0-9_]+)\s+([A-Za-z0-9_]+)\s*;/gm;
  let m: RegExpExecArray | null;
  while ((m = re.exec(source))) (map[m[2]] ||= []).push(m[1]);
  return map;
}

describe('compositor shader uniform compatibility', () => {
  it('no uniform is declared with a different type in the vertex vs fragment stage', () => {
    const vertex = shaderUniformTypes(VERTEX_SHADER);
    const fragment = shaderUniformTypes(FRAGMENT_SHADER);
    const collisions = Object.keys(vertex)
      .filter((name) => name in fragment)
      .filter((name) => !vertex[name].some((t) => fragment[name].includes(t)));
    expect(collisions).toEqual([]);
  });
});

describe('compositor picture geometry', () => {
  it('PA-9: the context keeps its drawing buffer, so stills and scopes can read the picture', () => {
    expect(COMPOSITOR_CONTEXT_ATTRIBUTES.preserveDrawingBuffer).toBe(true);
    expect(COMPOSITOR_CONTEXT_ATTRIBUTES).toMatchObject({ alpha: false, premultipliedAlpha: false, antialias: false });
  });

  it('PA-11: fit letterboxes a source whose aspect differs from the timeline, as the render does', () => {
    // A 9:16 phone clip in a 16:9 timeline: full height, narrow width (pillarbox).
    const [sx, sy] = aspectScale(1080, 1920, 1920, 1080, 'fit');
    expect(sy).toBe(1);
    expect(sx).toBeCloseTo((1080 / 1920) / (1920 / 1080), 6);
    // A 2.39:1 clip in 16:9: full width, bars top and bottom.
    const [wx, wy] = aspectScale(2390, 1000, 1920, 1080, 'fit');
    expect(wx).toBe(1);
    expect(wy).toBeCloseTo((1920 / 1080) / 2.39, 6);
  });

  it('fill covers the canvas (one axis overflows), stretch and matching aspects are untouched', () => {
    const [sx, sy] = aspectScale(1080, 1920, 1920, 1080, 'fill');
    expect(sx).toBe(1);
    expect(sy).toBeCloseTo((1920 / 1080) / (1080 / 1920), 6);
    expect(aspectScale(1080, 1920, 1920, 1080, 'stretch')).toEqual([1, 1]);
    expect(aspectScale(1280, 720, 1920, 1080, 'fit')).toEqual([1, 1]);
    expect(aspectScale(0, 0, 1920, 1080, 'fit')).toEqual([1, 1]);
  });

  it('reads the size of every drawable the decoder hands over', () => {
    expect(imageSize({ displayWidth: 640, displayHeight: 360 } as unknown as CanvasImageSource)).toEqual([640, 360]);
    expect(imageSize({ videoWidth: 320, videoHeight: 240 } as unknown as CanvasImageSource)).toEqual([320, 240]);
    expect(imageSize({ width: 100, height: 50 } as unknown as CanvasImageSource)).toEqual([100, 50]);
    expect(imageSize({} as CanvasImageSource)).toEqual([0, 0]);
  });
});
