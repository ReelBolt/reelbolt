/** @vitest-environment node */
/**
 * The live session against a recording WebGL2 fake: what the compositor actually draws when a
 * track cuts between two sources and the second one is not decoded yet. The picture of the clip
 * BEFORE the cut must never stand in for the clip after it, and the badge must not say "faithful".
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createLivePreviewSession } from '../live-session';
import type { DecodedFrame, FrameSource } from '../decoder-pool';
import { previewBadge, viewerNotice, PREPARING_PREVIEW } from '../honesty';
import { parityAtFrame } from '../parity';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { MediaClip } from '../../types';

interface Recorder {
  events: string[];
}

/** A WebGL2 context that records clears and which uploaded picture each quad drew. */
function fakeGl(recorder: Recorder): WebGL2RenderingContext {
  let nextId = 0;
  let bound: { id: number } | null = null;
  const uploaded = new Map<{ id: number }, string>();
  const handlers: Record<string, (...args: unknown[]) => unknown> = {
    createTexture: () => ({ id: ++nextId }),
    bindTexture: (_target, texture) => {
      bound = (texture as { id: number } | null) ?? null;
    },
    texImage2D: (...args) => {
      const image = args[5] as { tag?: string };
      if (bound) uploaded.set(bound, image.tag ?? '?');
    },
    clear: () => {
      recorder.events.push('clear');
    },
    drawArrays: () => {
      recorder.events.push(`draw:${bound ? (uploaded.get(bound) ?? 'empty') : 'none'}`);
    },
    getShaderParameter: () => true,
    getProgramParameter: () => true,
    getAttribLocation: () => 0,
    getUniformLocation: (_program, name) => name,
    uniform1f: (name, value) => {
      if (name === 'uMatteFeather') recorder.events.push(`feather:${value}`);
    },
    uniform3f: (name, r, g, b) => {
      if (name === 'uKeyColor') recorder.events.push(`key:${r},${g},${b}`);
    },
  };
  return new Proxy({} as WebGL2RenderingContext, {
    get(_target, prop: string) {
      return handlers[prop] ?? (() => ({}));
    },
  });
}

function fakeCanvas(recorder: Recorder): HTMLCanvasElement {
  const gl = fakeGl(recorder);
  return { width: 320, height: 180, getContext: () => gl } as unknown as HTMLCanvasElement;
}

/** A source whose frames carry `tag`; `ready: false` = the decoder never delivers a frame. */
function taggedSource(tag: string, ready = true): FrameSource {
  return {
    getFrame(sourceFrame: number): Promise<DecodedFrame | null> {
      if (!ready) return new Promise(() => undefined);
      return Promise.resolve({ sourceFrame, image: { tag } as unknown as CanvasImageSource, close: () => undefined });
    },
    dispose: () => undefined,
  };
}

const flush = async () => {
  for (let i = 0; i < 10; i++) await new Promise((resolve) => setTimeout(resolve, 0));
};

const montage = doc([
  track('v1', 'video', [
    media('clipA', 0, 30, 0, { assetRef: assetRef(1) }),
    media('clipB', 30, 60, 0, { assetRef: assetRef(2) }),
  ]),
]);
const urlFor = (clip: MediaClip) => `/proxy/${clip.id}`;
const flags = (t: { locked: boolean; enabled: boolean; mute: boolean; solo: boolean }) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo });

function playingSession(openB: () => Promise<FrameSource | null>) {
  const recorder: Recorder = { events: [] };
  let clock = 0;
  let pending: ((t: number) => void) | null = null;
  const session = createLivePreviewSession({
    canvas: fakeCanvas(recorder),
    getDoc: () => montage,
    flags,
    proxyUrlFor: urlFor,
    openSource: (url) => (url.endsWith('clipA') ? Promise.resolve(taggedSource('A')) : openB()),
    audioContext: null,
    requestFrame: (cb) => {
      pending = cb;
      return 1;
    },
    cancelFrame: () => {
      pending = null;
    },
    now: () => clock,
  })!;
  return {
    session,
    recorder,
    /** Advances the wall clock to `frame` (30 fps, playing from `from`) and runs one render tick. */
    tickTo(frame: number, from: number) {
      clock = ((frame - from) / 30) * 1000 + 0.5;
      const cb = pending;
      pending = null;
      cb?.(clock);
    },
  };
}

beforeEach(() => {
  vi.stubGlobal('document', { createElement: () => ({ width: 0, height: 0, getContext: () => null }) });
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('live session across a cut to another source', () => {
  for (const [name, openB] of [
    ['B still opening', () => new Promise<FrameSource | null>(() => undefined)],
    ['B open, no frame decoded yet', () => Promise.resolve(taggedSource('B', false))],
  ] as const) {
    it(`never draws clip A's picture for clip B and is not "faithful" (${name})`, async () => {
      const { session, recorder, tickTo } = playingSession(openB);
      session.seek(25);
      await flush();
      expect(recorder.events).toContain('draw:A');

      session.play(1);
      await flush();
      tickTo(35, 25);
      await flush();

      const lastClear = recorder.events.lastIndexOf('clear');
      const sinceCut = recorder.events.slice(lastClear + 1);
      expect(lastClear).toBeGreaterThan(recorder.events.indexOf('draw:A'));
      expect(sinceCut).not.toContain('draw:A');

      const missing = session.missingLayers!();
      expect(missing.map((m) => m.itemId)).toEqual(['clipB']);
      const badge = previewBadge(parityAtFrame(montage, 35), { sessionKind: 'live', sessionReason: null, missing, notReady: [] });
      expect(badge.kind).not.toBe('faithful');
      expect(viewerNotice(montage, missing, () => ({ state: 'proxy', url: '/p' }))).toBe(PREPARING_PREVIEW);
      session.dispose();
    });
  }
});

describe('live session over an A/B/A/B montage on one track', () => {
  it('opens each source once however often the track cuts back to it', async () => {
    const abab = doc([
      track('v1', 'video', [
        media('a1', 0, 30, 0, { assetRef: assetRef(1) }),
        media('b1', 30, 60, 0, { assetRef: assetRef(2) }),
        media('a2', 60, 90, 30, { assetRef: assetRef(1) }),
        media('b2', 90, 120, 30, { assetRef: assetRef(2) }),
      ]),
    ]);
    const opens: string[] = [];
    const recorder: Recorder = { events: [] };
    const session = createLivePreviewSession({
      canvas: fakeCanvas(recorder),
      getDoc: () => abab,
      flags,
      proxyUrlFor: (clip) => `/proxy/${clip.assetRef.kind === 'projectFile' ? clip.assetRef.projectFileId : 'x'}`,
      openSource: (url) => {
        opens.push(url);
        return Promise.resolve(taggedSource(url));
      },
      audioContext: null,
      requestFrame: () => 1,
      cancelFrame: () => undefined,
      now: () => 0,
    })!;
    for (const frame of [5, 35, 65, 95, 10, 40]) {
      session.seek(frame);
      await flush();
    }
    expect(opens.length).toBe(2);
    expect(new Set(opens).size).toBe(2);
    session.dispose();
  });
});

describe('live session with a source that fails to open', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('says why in plain words, then retries on its own and shows the clip once it opens', async () => {
    vi.useFakeTimers();
    let clock = 0;
    let failOnce = true;
    const one = doc([track('v1', 'video', [media('clipA', 0, 30, 0, { assetRef: assetRef(1) })])]);
    const recorder: Recorder = { events: [] };
    const session = createLivePreviewSession({
      canvas: fakeCanvas(recorder),
      getDoc: () => one,
      flags,
      proxyUrlFor: urlFor,
      openSource: () => {
        if (failOnce) {
          failOnce = false;
          return Promise.resolve(null);
        }
        return Promise.resolve(taggedSource('A'));
      },
      audioContext: null,
      requestFrame: () => 1,
      cancelFrame: () => undefined,
      now: () => clock,
    })!;
    session.seek(5);
    await vi.advanceTimersByTimeAsync(0);
    const missing = session.missingLayers!();
    expect(missing.map((m) => m.cause)).toEqual(['openFailed']);
    expect(viewerNotice(one, missing, () => ({ state: 'proxy', url: '/p' }))).toMatch(/could not be opened/);
    expect(recorder.events).not.toContain('draw:A');

    clock = 60_000;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(session.missingLayers!()).toEqual([]);
    expect(recorder.events).toContain('draw:A');
    session.dispose();
  });
});

describe('live session with an object mask the render would refuse', () => {
  const run = async (coverageEndSec: number) => {
    const masked = doc([track('v1', 'video', [{ ...media('m', 0, 60, 0, { assetRef: assetRef(1) }), mask: { kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1'], invert: false, featherPx: 0 } } as unknown as MediaClip])]);
    const tracks = [{ trackId: 'g1', label: 'p', firstFrame: 0, lastFrame: 20, fps: 30, coverageEndSec, boxKeyframes: [{ t: 0, x: 0.1, y: 0.1, w: 0.5, h: 0.5 }] }];
    const recorder: Recorder = { events: [] };
    const session = createLivePreviewSession({
      canvas: fakeCanvas(recorder),
      getDoc: () => masked,
      flags,
      proxyUrlFor: urlFor,
      openSource: () => Promise.resolve(taggedSource('M')),
      audioContext: null,
      segmentationTracksFor: (id) => (id === 'a1' ? tracks : undefined),
    })!;
    session.seek(10);
    await flush();
    session.dispose();
    return recorder.events;
  };
  it('draws the clip when the mask covers its whole source span (60 frames = 2 s)', async () => {
    expect(await run(2)).toContain('draw:M');
  });
  it('draws no picture for the clip when the mask ends before its span does', async () => {
    const events = await run(1);
    expect(events.some((e) => e.startsWith('draw:'))).toBe(false);
  });
});

describe('the source width reaches the mask feather', () => {
  const feather = async (sourceWidthFor?: (clip: MediaClip) => number | null) => {
    const masked = doc([track('v1', 'video', [{ ...media('m', 0, 60, 0, { assetRef: assetRef(1) }), mask: { kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1'], invert: false, featherPx: 10 } } as unknown as MediaClip])]);
    const tracks = [{ trackId: 'g1', label: 'p', firstFrame: 0, lastFrame: 59, fps: 30, coverageEndSec: 2, boxKeyframes: [{ t: 0, x: 0.1, y: 0.1, w: 0.5, h: 0.5 }] }];
    const recorder: Recorder = { events: [] };
    const session = createLivePreviewSession({
      canvas: fakeCanvas(recorder),
      getDoc: () => masked,
      flags,
      proxyUrlFor: urlFor,
      openSource: () => Promise.resolve({ getFrame: (sourceFrame: number) => Promise.resolve({ sourceFrame, image: { width: 960, height: 540 } as unknown as CanvasImageSource, close: () => undefined }), dispose: () => undefined }),
      audioContext: null,
      segmentationTracksFor: (id) => (id === 'a1' ? tracks : undefined),
      sourceWidthFor,
    })!;
    session.seek(10);
    await flush();
    session.dispose();
    return recorder.events.filter((e) => e.startsWith('feather:')).map((e) => Number(e.slice(8)));
  };
  it('scales the feather ramp from source pixels to the decoded proxy (960 of 1920 = half)', async () => {
    expect(await feather(() => 1920)).toContain(16); // 3.2 x 10 x 960/1920
  });
  it('leaves it unscaled when the probe width is unknown', async () => {
    expect(await feather(() => null)).toContain(32);
    expect(await feather()).toContain(32);
  });
});

describe('a segmentation-masked layer without a usable matte is shown unmasked (dimmed), not hidden', () => {
  const draws = async (lookup: (id: string) => unknown) => {
    const masked = doc([track('v1', 'video', [{ ...media('m', 0, 60, 0, { assetRef: assetRef(1) }), mask: { kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1'], invert: false, featherPx: 0 } } as unknown as MediaClip])]);
    const recorder: Recorder = { events: [] };
    const session = createLivePreviewSession({
      canvas: fakeCanvas(recorder),
      getDoc: () => masked,
      flags,
      proxyUrlFor: urlFor,
      openSource: () => Promise.resolve(taggedSource('M')),
      audioContext: null,
      segmentationTracksFor: lookup as never,
    })!;
    session.seek(10);
    await flush();
    session.dispose();
    return recorder.events.some((e) => e.startsWith('draw:'));
  };
  const track1 = (boxKeyframes: unknown[]) => [{ trackId: 'g1', label: 'p', firstFrame: 0, lastFrame: 59, fps: 30, coverageEndSec: 2, boxKeyframes }];
  const box = { t: 0, x: 0.1, y: 0.1, w: 0.5, h: 0.5 };

  it('still draws the picture while the tracks are loading (or the fetch failed)', async () => {
    expect(await draws(() => undefined)).toBe(true);
  });
  it('still draws the picture when the tracks carry no box keyframes', async () => {
    expect(await draws((id) => (id === 'a1' ? track1([]) : undefined))).toBe(true);
  });
  it('still draws the layer when the binding is unknown (no coverage served)', async () => {
    expect(await draws((id) => (id === 'a1' ? [{ ...track1([box])[0], coverageEndSec: undefined }] : undefined))).toBe(true);
  });
  it('draws the layer once the tracked boxes are loaded', async () => {
    expect(await draws((id) => (id === 'a1' ? track1([box]) : undefined))).toBe(true);
  });
});

describe('source rotation reaches the decoder', () => {
  it('keys assets by rotation only when non-zero', async () => {
    const { joinAssetKey, splitAssetKey } = await import('../live-session');
    expect(joinAssetKey('/u', 30)).toBe('30|/u');
    expect(joinAssetKey('/u', 30, 0)).toBe('30|/u');
    expect(joinAssetKey('/u', 30, null)).toBe('30|/u');
    expect(joinAssetKey('/u', 30, 90)).toBe('30@90|/u');
    expect(splitAssetKey('30|/u')).toEqual(['/u', '30', 0]);
    expect(splitAssetKey('29.97@270|/a|b')).toEqual(['/a|b', '29.97', 270]);
  });

  const rotationsOpened = async (sourceRotation?: 90) => {
    const clip = { ...media('m', 0, 60, 0, { assetRef: assetRef(1) }), ...(sourceRotation ? { sourceRotation } : {}) } as MediaClip;
    const d = doc([track('v1', 'video', [clip])]);
    const opened: Array<number | undefined> = [];
    const session = createLivePreviewSession({
      canvas: fakeCanvas({ events: [] }),
      getDoc: () => d,
      flags,
      proxyUrlFor: urlFor,
      openSource: (_url, _fps, _w, rotation) => {
        opened.push(rotation);
        return Promise.resolve(taggedSource('M'));
      },
      audioContext: null,
    })!;
    session.seek(10);
    await flush();
    session.dispose();
    return opened;
  };
  it('opens the decoder with the clip rotation', async () => {
    expect(await rotationsOpened(90)).toContain(90);
  });
  it('opens with rotation 0 when the clip has none', async () => {
    const opened = await rotationsOpened();
    expect(opened.length).toBeGreaterThan(0);
    expect(opened.every((r) => r === 0)).toBe(true);
  });
});

describe('adaptive picture size (every effect stays on; the picture gets smaller, then full size again on pause)', () => {
  const longDoc = doc([track('v1', 'video', [media('clipA', 0, 3000, 0, { assetRef: assetRef(1) })])]);

  function adaptiveSession(overrides: Partial<Parameters<typeof createLivePreviewSession>[0]> = {}) {
    const recorder: Recorder = { events: [] };
    const canvas = fakeCanvas(recorder);
    let clock = 0;
    let pending: ((t: number) => void) | null = null;
    const session = createLivePreviewSession({
      canvas,
      getDoc: () => longDoc,
      flags,
      proxyUrlFor: urlFor,
      openSource: () => Promise.resolve(taggedSource('A')),
      audioContext: null,
      playbackResolution: 'half',
      requestFrame: (cb) => {
        pending = cb;
        return 1;
      },
      cancelFrame: () => {
        pending = null;
      },
      now: () => clock,
      ...overrides,
    })!;
    let frame = 0;
    return {
      session,
      canvas,
      /** Presents the next frame, `skip` slots after the last one (skip > 0 = that many slots were missed). */
      async step(skip = 0) {
        frame += 1 + skip;
        clock = (frame / 30) * 1000 + 0.5;
        const cb = pending;
        pending = null;
        cb?.(clock);
        await Promise.resolve();
      },
    };
  }

  it('a fixed pick is an exact fraction of the timeline', async () => {
    const { canvas } = adaptiveSession();
    expect([canvas.width, canvas.height]).toEqual([960, 540]);
  });

  it('steps the canvas down while frames are skipped, says so, and is back at full size on pause', async () => {
    const { session, canvas, step } = adaptiveSession();
    session.seek(0);
    await flush();
    session.play(1);
    await flush();
    expect(session.adaptiveQuality).toMatchObject({ scale: 1, atFloor: false });
    // Sustained skips: every presented frame follows two missed slots.
    for (let i = 0; i < 80 && canvas.width === 960; i++) await step(2);
    expect(canvas.width).toBe(720);
    expect(canvas.height).toBe(406);
    expect(session.adaptiveQuality?.scale).toBe(0.75);
    expect(session.reducedQuality).toMatch(/smaller size/);

    session.pause();
    await flush();
    expect([canvas.width, canvas.height]).toEqual([960, 540]);
    expect(session.adaptiveQuality?.scale).toBe(1);
    expect(session.reducedQuality).toBeNull();

    // The next play starts at the size the device needed, without stuttering through the same seconds again.
    session.play(1);
    await flush();
    expect(canvas.width).toBe(720);
  });

  it('keeps the full size while playback keeps up', async () => {
    const { session, canvas, step } = adaptiveSession();
    session.seek(0);
    await flush();
    session.play(1);
    await flush();
    for (let i = 0; i < 300; i++) await step(0);
    expect(canvas.width).toBe(960);
    expect(session.reducedQuality).toBeNull();
  });

  it('adaptive: false pins the size', async () => {
    const { session, canvas, step } = adaptiveSession({ adaptive: false });
    session.seek(0);
    await flush();
    session.play(1);
    await flush();
    for (let i = 0; i < 200; i++) await step(2);
    expect(canvas.width).toBe(960);
    expect(session.adaptiveQuality).toMatchObject({ scale: 1, atFloor: true });
  });

  it('Auto draws no larger than the viewer shows the picture, under the device ceiling', async () => {
    const { session, canvas } = adaptiveSession({ playbackResolution: 'quarter', fitDisplay: { maxLongSide: 854 } });
    // Before the viewer is measured: the starting fraction.
    expect([canvas.width, canvas.height]).toEqual([480, 270]);
    session.setDisplayLongSide?.(700);
    await flush();
    expect([canvas.width, canvas.height]).toEqual([800, 450]);
    session.setDisplayLongSide?.(3000);
    await flush();
    expect([canvas.width, canvas.height]).toEqual([854, 480]);
    session.setDisplayLongSide?.(200);
    await flush();
    expect([canvas.width, canvas.height]).toEqual([320, 180]);
  });

  it('a fixed pick ignores the viewer size', async () => {
    const { session, canvas } = adaptiveSession();
    session.setDisplayLongSide?.(300);
    await flush();
    expect(canvas.width).toBe(960);
  });
});

describe('an insert with a chroma matte', () => {
  const quad = { topLeft: { x: 0.1, y: 0.1 }, topRight: { x: 0.9, y: 0.1 }, bottomRight: { x: 0.9, y: 0.9 }, bottomLeft: { x: 0.1, y: 0.9 } };
  async function keyEventsFor(matte: { color: string } | null): Promise<string[]> {
    const insert = { id: 'i1', name: 'Screen', type: 'insertClip', recordIn: 0, recordOut: 30, assetRef: assetRef(1), enabled: true, quad: { value: quad }, matte };
    const withInsert = doc([track('v1', 'video', [insert as never])]);
    const recorder: Recorder = { events: [] };
    const session = createLivePreviewSession({
      canvas: fakeCanvas(recorder),
      getDoc: () => withInsert,
      flags,
      proxyUrlFor: () => '/proxy/insert',
      // A sized picture: the grade pass (where the keyer runs) needs the source's size.
      openSource: () => Promise.resolve({ getFrame: (sourceFrame: number) => Promise.resolve({ sourceFrame, image: { tag: 'I', width: 960, height: 540 } as unknown as CanvasImageSource, close: () => undefined }), dispose: () => undefined }),
      audioContext: null,
      requestFrame: () => 1,
      cancelFrame: () => undefined,
      now: () => 0,
    })!;
    session.seek(5);
    await flush();
    session.dispose();
    return recorder.events.filter((e) => e.startsWith('key:'));
  }

  it('is keyed in the preview with the matte colour, before the warp (as the export does)', async () => {
    expect(await keyEventsFor({ color: '#00ff00' })).toContain('key:0,1,0');
  });
  it('an opaque insert runs no keyer', async () => {
    expect(await keyEventsFor(null)).toEqual([]);
  });
});

describe('a seek while playing keeps the adaptive size (a loop wrap must not resize twice)', () => {
  it('stays at the reduced size across the seek and is full size only on a real pause', async () => {
    const longDoc = doc([track('v1', 'video', [media('clipA', 0, 3000, 0, { assetRef: assetRef(1) })])]);
    const recorder: Recorder = { events: [] };
    const canvas = fakeCanvas(recorder);
    let clock = 0;
    let pending: ((t: number) => void) | null = null;
    const session = createLivePreviewSession({
      canvas,
      getDoc: () => longDoc,
      flags,
      proxyUrlFor: urlFor,
      openSource: () => Promise.resolve(taggedSource('A')),
      audioContext: null,
      playbackResolution: 'half',
      requestFrame: (cb) => {
        pending = cb;
        return 1;
      },
      cancelFrame: () => {
        pending = null;
      },
      now: () => clock,
    })!;
    session.seek(0);
    await flush();
    session.play(1);
    await flush();
    let frame = 0;
    for (let i = 0; i < 80 && canvas.width === 960; i++) {
      frame += 3;
      clock = (frame / 30) * 1000 + 0.5;
      const cb = pending;
      pending = null;
      (cb as ((t: number) => void) | null)?.(clock);
      await Promise.resolve();
    }
    expect(canvas.width).toBe(720);
    const widths: number[] = [];
    const watch = Object.getOwnPropertyDescriptor(canvas, 'width');
    let width = canvas.width;
    Object.defineProperty(canvas, 'width', { configurable: true, get: () => width, set: (v: number) => { width = v; widths.push(v); } });
    session.seek(10);
    await flush();
    expect(widths, 'the seek did not go to full size and back').toEqual([]);
    expect(session.isPlaying()).toBe(true);
    session.pause();
    await flush();
    expect(width).toBe(960);
    if (watch) Object.defineProperty(canvas, 'width', watch);
  });
});
