/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import {
  clipKindOf, clipLabel, filmstripCellSec, filmstripCount, fitText, formatClipDuration, paintClipContent, peakColumns, peaksBucketMs, shadeWidth, sourceSpanSec,
  peakLodsBySize, trimHandles, type ClipArt, type PeakData,
} from '../clipPaint';
import { DARK_THEME, paintTimeline, type PaintContext, type PaintUi, type Viewport } from '../paint';

describe('R03.4 clip kinds and labels', () => {
  it('kind and badge per item type', () => {
    expect(clipKindOf({ type: 'mediaClip' }, 'video')).toEqual({ kind: 'video', badge: 'V' });
    expect(clipKindOf({ type: 'mediaClip' }, 'audio')).toEqual({ kind: 'audio', badge: 'A' });
    expect(clipKindOf({ type: 'titleClip' }, 'video').kind).toBe('title');
    expect(clipKindOf({ type: 'generatorClip' }, 'video').kind).toBe('colour');
    expect(clipKindOf({ type: 'compoundClip' }, 'video')).toEqual({ kind: 'compound', badge: 'Compound' });
  });
  it('duration text', () => {
    expect(formatClipDuration(48, 24)).toBe('2.0s');
    expect(formatClipDuration(24 * 30, 24)).toBe('30s');
    expect(formatClipDuration(24 * 65, 24)).toBe('1:05');
  });
  it('fitText truncates with an ellipsis and never returns something wider than the room', () => {
    expect(fitText('abc', 100)).toBe('abc');
    const out = fitText('A very long clip name', 60);
    expect(out.endsWith('…')).toBe(true);
    expect(out.length * 6.6).toBeLessThanOrEqual(60);
    expect(fitText('abc', 3)).toBe('');
  });
  it('a narrow clip drops the duration, then the name, never shrinks the font', () => {
    const base = { name: 'Interview A', badge: 'V', durationText: '12s', showName: true };
    expect(clipLabel({ ...base, widthPx: 300 })).toEqual({ badge: 'V', name: 'Interview A', duration: '12s' });
    const mid = clipLabel({ ...base, widthPx: 90 });
    expect(mid.duration).toBe('');
    expect(mid.name.length).toBeGreaterThan(0);
    expect(clipLabel({ ...base, widthPx: 14 }).name).toBe('');
    expect(clipLabel({ ...base, widthPx: 300, showName: false }).name).toBe('');
  });
});

describe('R03.4 trimmed regions and filmstrip math', () => {
  const clip = { recordIn: 0, recordOut: 48, sourceIn: 24, playbackRate: 1 };
  it('span in source seconds follows sourceIn and speed', () => {
    expect(sourceSpanSec(clip, 24)).toEqual({ startSec: 1, endSec: 3 });
    expect(sourceSpanSec({ ...clip, playbackRate: 2 }, 24).endSec).toBe(5);
  });
  it('handles are the unused source on each side, in timeline pixels', () => {
    const h = trimHandles(clip, 24, 10, 2);
    expect(h.left).toBeCloseTo(48, 5); // 1 s = 24 frames * 2 px
    expect(h.right).toBeGreaterThan(300);
    expect(trimHandles({ recordIn: 0, recordOut: 240, sourceIn: 0 }, 24, 10, 2)).toEqual({ left: 0, right: 0 });
    expect(trimHandles(clip, 24, null, 2)).toEqual({ left: 0, right: 0 });
  });
  it('shade widths are 0 or 4..14 px', () => {
    expect(shadeWidth(0)).toBe(0);
    expect(shadeWidth(2)).toBe(4);
    expect(shadeWidth(500)).toBe(14);
  });
  it('filmstrip: poster only when narrow, evenly spread cells when wide', () => {
    expect(filmstripCount(60, 40)).toBe(0);
    expect(filmstripCount(200, 40)).toBe(5);
    expect(filmstripCellSec(0, 1, 2, 6)).toBe(2);
    expect(filmstripCellSec(0, 4, 0, 8)).toBe(1);
    expect(filmstripCellSec(3, 4, 0, 8)).toBe(7);
  });
});

describe('R03.4 peak LOD without a duration', () => {
  it('three rows rank by size; two by ratio; one is unknown', () => {
    expect(peakLodsBySize([80, 16, 2])).toEqual([20, 100, 1000]);
    expect(peakLodsBySize([2, 80, 16])).toEqual([1000, 20, 100]);
    expect(peakLodsBySize([800, 160])).toEqual([20, 100]);
    expect(peakLodsBySize([160, 16])).toEqual([100, 1000]);
    expect(peakLodsBySize([800, 16])).toEqual([20, 1000]);
    expect(peakLodsBySize([100])).toEqual([null]);
  });
});

describe('R03.4 waveform columns', () => {
  // 2 channels x 4 buckets of 1000 ms: [ch][bucket]{min,max}
  const data = new Int16Array([-100, 200, -32768, 32767, -50, 60, 0, 0, /* ch1 */ -10, 20, -500, 700, -1, 1, 0, 0]);
  const peaks: PeakData = { data, channels: 2, bucketMs: 1000 };
  it('merges channels and normalises', () => {
    const mm = peakColumns(peaks, 0, 4, 4);
    expect(mm).toHaveLength(8);
    expect(mm[0]).toBeCloseTo(-100 / 32768, 5);
    expect(mm[1]).toBeCloseTo(200 / 32767, 5);
    expect(mm[2]).toBe(-1);
    expect(mm[3]).toBe(1);
    expect(mm[6]).toBe(0);
  });
  it('fewer columns than buckets takes the extremes; out-of-range columns are flat', () => {
    const mm = peakColumns(peaks, 0, 4, 2);
    expect(mm[0]).toBe(-1);
    expect(mm[1]).toBe(1);
    const past = peakColumns(peaks, 10, 12, 2);
    expect(Array.from(past)).toEqual([0, 0, 0, 0]);
  });
  it('bucket size is the LOD nearest the source duration', () => {
    expect(peaksBucketMs(2 * 100 * 4, 2, 2)).toBe(20); // 100 buckets over 2 s
    expect(peaksBucketMs(2 * 20 * 4, 2, 2)).toBe(100);
    expect(peaksBucketMs(2 * 2 * 4, 2, 2)).toBe(1000);
    expect(peaksBucketMs(0, 2, 2)).toBeNull();
    expect(peaksBucketMs(100, 2, null)).toBeNull();
  });
});

function recorder() {
  const calls: Record<string, number> = {};
  const bump = (k: string) => (calls[k] = (calls[k] ?? 0) + 1);
  const texts: string[] = [];
  const fonts: string[] = [];
  let font = '';
  const ctx = {
    canvas: { width: 800, height: 300 },
    save: () => bump('save'), restore: () => bump('restore'), setTransform: () => undefined, clearRect: () => undefined,
    fillRect: () => bump('fillRect'), strokeRect: () => bump('strokeRect'),
    fillText: (t: string) => { texts.push(t); fonts.push(font); bump('fillText'); },
    beginPath: () => undefined, moveTo: () => undefined, lineTo: () => undefined, stroke: () => undefined, rect: () => undefined, clip: () => undefined,
    drawImage: () => bump('drawImage'),
    fillStyle: '', strokeStyle: '', lineWidth: 1, textAlign: 'left', textBaseline: 'top', globalAlpha: 1,
    get font() { return font; }, set font(v: string) { font = v; },
  } as unknown as PaintContext;
  return { ctx, calls, texts, fonts };
}

describe('R03.4 paintClipContent', () => {
  const item = { ...media('c1', 0, 240), name: 'Interview' };
  const art = (over: Partial<ClipArt> = {}): ClipArt => ({
    sourceDurationSec: () => 20,
    thumb: () => ({ image: {} as CanvasImageSource, sx: 0, sy: 0, sw: 160, sh: 90 }),
    thumbAspect: () => 16 / 9,
    peaks: () => ({ data: new Int16Array([-100, 100, -200, 200, -100, 100, -200, 200]), channels: 2, bucketMs: 1000 }),
    offline: () => false,
    ...over,
  });
  const args = (ctx: PaintContext, a?: ClipArt, over: Record<string, unknown> = {}) => ({
    item, trackKind: 'video' as const, x0: 10, y: 0, w: 400, height: 96, fps: 24, pixelsPerFrame: 2, viewportWidth: 800, muted: false, linked: false, art: a,
    view: { waveforms: true, thumbnails: true, names: true }, theme: DARK_THEME, ...over,
  });
  it('draws a filmstrip and a waveform for a tall video clip with sound', () => {
    const r = recorder();
    const out = paintClipContent(r.ctx, args(r.ctx, art()));
    expect(out.thumbs).toBeGreaterThan(1);
    expect(out.waveform).toBe(true);
    expect(r.texts).toContain('Interview');
    for (const f of r.fonts) expect(f).toMatch(/12px/);
  });
  it('the view toggles switch each layer off', () => {
    const r = recorder();
    const out = paintClipContent(r.ctx, args(r.ctx, art(), { view: { waveforms: false, thumbnails: false, names: false } }));
    expect(out.thumbs).toBe(0);
    expect(out.waveform).toBe(false);
    expect(r.texts).not.toContain('Interview');
  });
  it('a short clip draws only a poster; no art when the source has none', () => {
    const r = recorder();
    expect(paintClipContent(r.ctx, args(r.ctx, art(), { w: 40 })).thumbs).toBe(1);
    const r2 = recorder();
    const none = paintClipContent(r2.ctx, args(r2.ctx, art({ thumb: () => null, peaks: () => null })));
    expect(none).toEqual({ thumbs: 0, waveform: false, offline: false, unchecked: false });
  });
  it('T-023 a source with an unknown length shows the small "Length not checked" note, a known one does not', () => {
    const r = recorder();
    const out = paintClipContent(r.ctx, args(r.ctx, art({ lengthUnchecked: () => true })));
    expect(out.unchecked).toBe(true);
    expect(r.texts).toContain('Length not checked');
    const known = recorder();
    expect(paintClipContent(known.ctx, args(known.ctx, art({ lengthUnchecked: () => false }))).unchecked).toBe(false);
    expect(known.texts).not.toContain('Length not checked');
  });

  it('an offline source says so and draws no art', () => {
    const r = recorder();
    const out = paintClipContent(r.ctx, args(r.ctx, art({ offline: () => true })));
    expect(out.offline).toBe(true);
    expect(out.thumbs).toBe(0);
    expect(r.texts).toContain('Offline');
  });
  it('audio clips draw the waveform and no thumbnails', () => {
    const r = recorder();
    const out = paintClipContent(r.ctx, args(r.ctx, art(), { trackKind: 'audio' }));
    expect(out.thumbs).toBe(0);
    expect(out.waveform).toBe(true);
  });
  it('a linked clip gets its foot stripe', () => {
    const a = recorder();
    paintClipContent(a.ctx, args(a.ctx, art()));
    const b = recorder();
    paintClipContent(b.ctx, args(b.ctx, art(), { linked: true }));
    expect(b.calls.fillRect).toBe((a.calls.fillRect ?? 0) + 1);
  });
});

describe('R03.4 paint perf: 5 tracks x 60 clips', () => {
  it('visits only the visible clips and paints inside a frame budget', () => {
    const tracks = Array.from({ length: 5 }, (_, t) =>
      track(`t${t}`, t % 2 ? 'audio' : 'video', Array.from({ length: 60 }, (_, i) => media(`c${t}-${i}`, i * 100, i * 100 + 90))),
    );
    const d = doc(tracks);
    const r = recorder();
    const vp: Viewport = { width: 1200, height: 400, scrollFrame: 2000, pixelsPerFrame: 1, devicePixelRatio: 1 };
    const ui: PaintUi = {
      playheadFrame: 0, selection: new Set(), inFrame: null, outFrame: null, parityOf: () => 'faithful', rationaleFrames: [],
      trackFlags: (tk) => ({ locked: tk.locked, enabled: tk.enabled, mute: tk.mute, solo: tk.solo, collapsed: false }),
    };
    const t0 = performance.now();
    let stats = paintTimeline(r.ctx, d, vp, ui);
    for (let i = 0; i < 49; i++) stats = paintTimeline(r.ctx, d, { ...vp, scrollFrame: 2000 + i * 7 }, ui);
    const perFrameMs = (performance.now() - t0) / 50;
    expect(stats.visitedItems).toBeLessThanOrEqual(5 * 15);
    console.info(`5x60 clips: ${perFrameMs.toFixed(2)} ms per frame (no art)`);
    expect(perFrameMs).toBeLessThan(8);
  });
});
