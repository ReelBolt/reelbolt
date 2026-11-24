import { describe, expect, it } from 'vitest';
import { doc as makeDoc, media, track } from '../../__tests__/fixtures';
import { MAX_GRADE_NODES } from '../../engine/ops';
import type { ColorGroup, EditTimeline, EditTimelineSettings, GradeStack, MediaClip } from '../../types';
import type { MediaPoolItem } from '../../media/types';
import { computeScopes } from '../scopes';
import type { PixelBuffer } from '../scopes';
import { exposureFix, histogramFromScopes, FIX_NODE_NAME, type FixPrimaries, type LumaHistogram } from '../exposure-fix';
import {
  buildFixWrite, cropPixelBuffer, fixSummaryParts, measureClip, pictureHistogram, pictureRectOf,
  readClipLumaHistogram, resolveFixTarget, sampleFrames, sourceSizeFromPool, targetKey, trimDarkBars,
  type MeasureDeps,
} from '../exposure-measure';

const primaries: FixPrimaries = { exposure: 0.8, offset: { r: 0, g: 0, b: 0, master: 0 }, gamma: { r: 0, g: 0, b: 0, master: 0 } };

function dark(): LumaHistogram {
  const bins = new Array<number>(256).fill(0);
  for (let i = 5; i < 60; i++) bins[i] = 100;
  return { bins, total: 5500 };
}

function fakes(overrides: Partial<MeasureDeps> = {}) {
  const log: string[] = [];
  const progress: Array<[number, number]> = [];
  const deps: MeasureDeps = {
    seek: (f) => log.push(`seek ${f}`),
    settle: async () => undefined,
    setBypass: (on) => log.push(`bypass ${on}`),
    playheadFrame: 7,
    bypass: false,
    read: dark,
    onProgress: (d, t) => progress.push([d, t]),
    ...overrides,
  };
  return { deps, log, progress };
}

describe('sampleFrames', () => {
  it('is the playhead (inside) plus 4 evenly spaced frames, at most 5, no duplicates', () => {
    const frames = sampleFrames({ recordIn: 0, recordOut: 100 }, 30);
    expect(frames).toEqual([30, 12, 37, 62, 87]);
    expect(frames.length).toBeLessThanOrEqual(5);
  });
  it('skips an outside playhead and dedupes on a short clip', () => {
    expect(sampleFrames({ recordIn: 10, recordOut: 110 }, 500)).toHaveLength(4);
    expect(sampleFrames({ recordIn: 10, recordOut: 12 }, 500).length).toBeLessThanOrEqual(2);
  });
});

describe('measureClip', () => {
  it('reads each frame with grades bypassed, then restores playhead and bypass', async () => {
    const { deps, log, progress } = fakes();
    const out = await measureClip(deps, { recordIn: 0, recordOut: 100 });
    expect(out.kind).toBe('result');
    if (out.kind === 'result') expect(out.result.kind).toBe('fix');
    expect(log[0]).toBe('bypass true');
    expect(log.filter((l) => l.startsWith('seek ')).length).toBe(6) // 5 reads (the playhead 7 is inside) and the restore;
    expect(log.slice(-2)).toEqual(['bypass false', 'seek 7']);
    expect(progress[0]).toEqual([0, 5]);
    expect(progress.at(-1)).toEqual([5, 5]);
  });

  it('cancel mid-way restores and reports cancelled (nothing to write)', async () => {
    const ac = new AbortController();
    let settles = 0;
    const { deps, log } = fakes({ signal: ac.signal, settle: async () => { settles += 1; if (settles === 2) ac.abort(); } });
    const out = await measureClip(deps, { recordIn: 0, recordOut: 100 });
    expect(out).toEqual({ kind: 'cancelled' });
    expect(log.slice(-2)).toEqual(['bypass false', 'seek 7']);
  });

  it('a null read is noPicture and still restores', async () => {
    let n = 0;
    const { deps, log } = fakes({ read: () => (++n === 3 ? null : dark()) });
    const out = await measureClip(deps, { recordIn: 0, recordOut: 100 });
    expect(out).toEqual({ kind: 'result', result: { kind: 'noPicture' } });
    expect(log.slice(-2)).toEqual(['bypass false', 'seek 7']);
  });

  it('restores a bypass that was already on', async () => {
    const { deps, log } = fakes({ bypass: true });
    await measureClip(deps, { recordIn: 0, recordOut: 100 });
    expect(log.at(-2)).toBe('bypass true');
  });
});

function docWith(clip: MediaClip, groups?: ColorGroup[]): EditTimeline {
  return { ...makeDoc([track('v1', 'video', [clip])]), colorGroups: groups ?? null };
}
const stackOf = (n: number): GradeStack => ({ enabled: true, nodes: Array.from({ length: n }, (_, i) => ({ id: `n${i}`, name: `Node ${i}`, enabled: true, primaries: undefined, curves: null, lut: null }) as never) });

/** A 9:16 canvas frame with black bars (`bars` fraction of the height, split evenly) around a lit band. */
function pictureFrame(bars: number, lo: number, hi: number, width = 108, height = 192): PixelBuffer {
  const data = new Uint8ClampedArray(width * height * 4);
  const bar = Math.round((height * bars) / 2);
  for (let y = 0; y < height; y++) {
    const lit = y >= bar && y < height - bar;
    for (let x = 0; x < width; x++) {
      const v = lit ? lo + Math.round(((hi - lo) * x) / (width - 1)) : 0;
      const i = (y * width + x) * 4;
      data[i] = v; data[i + 1] = v; data[i + 2] = v; data[i + 3] = 255;
    }
  }
  return { data, width, height };
}

const rawHistogram = (buffer: PixelBuffer): LumaHistogram => histogramFromScopes(computeScopes(buffer, { columns: 32, levels: 64, vectorSize: 16 }));

const vertical: EditTimelineSettings = {
  width: 1080, height: 1920, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0,
};

describe('the picture rectangle (letterbox bars)', () => {
  it('is the compositor fit of a 16:9 clip in a 9:16 timeline: ~68% bars around the picture', () => {
    const rect = pictureRectOf(vertical, media('c', 0, 60, 0), { width: 1920, height: 1080 }, 10, 108, 192);
    expect(rect).not.toBeNull();
    expect(rect!.width).toBe(108);
    expect(rect!.height).toBeGreaterThan(50);
    expect(rect!.height).toBeLessThan(72);
    expect(1 - rect!.height / 192).toBeGreaterThan(0.6);
  });

  it('follows crop and transform, and gives up on a rotated layer', () => {
    const cropped = pictureRectOf(vertical, media('c', 0, 60, 0, { crop: { left: 0, top: 0.25, right: 0, bottom: 0 } }), { width: 1920, height: 1080 }, 10, 108, 192);
    expect(cropped).not.toBeNull();
    // Cropping the top quarter makes the source wider, so its fitted picture is shorter.
    expect(cropped!.height).toBeGreaterThan(30);
    expect(cropped!.height).toBeLessThan(55);
    const rotated = pictureRectOf(vertical, media('c', 0, 60, 0, { transform: { rotation: { value: 20 } } }), { width: 1920, height: 1080 }, 10, 108, 192);
    expect(rotated).toBeNull();
    expect(pictureRectOf(vertical, media('c', 0, 60, 0), null, 10, 108, 192)).toBeNull();
  });

  it('puts a moved clip where the compositor draws it: positive positionY is DOWN, positive positionX is RIGHT', () => {
    const moved = pictureRectOf(vertical, media('c', 0, 60, 0, { transform: { positionY: { value: 0.25 }, positionX: { value: 0.1 }, scale: { value: 0.5 } } }), { width: 1920, height: 1080 }, 10, 108, 192);
    expect(moved).not.toBeNull();
    // Centre at (0.5 + y) * H = 144 and (0.5 + x) * W = 64.8, as the render's overlay y=(H-h)/2+H*y.
    expect(moved!.y + moved!.height / 2).toBeCloseTo(144, 0);
    expect(moved!.x + moved!.width / 2).toBeCloseTo(64.8, 0);
  });

  it('keeps black rows inside a known picture (a night sky) and trims only when the picture is unknown', () => {
    // Dark rows at the top and bottom of a picture whose rect is known (the whole frame here) are content.
    const frame = pictureFrame(0.3, 8, 240);
    const whole = { x: 0, y: 0, width: frame.width, height: frame.height };
    const withRect = pictureHistogram(frame, whole);
    const trimmed = pictureHistogram(frame, null);
    expect(withRect.total).toBeGreaterThan(trimmed.total);
    expect(pictureHistogram(frame, null, false).total).toBe(withRect.total);
  });

  it('a 68% bars frame reads as a bright picture only when the picture, not the canvas, is measured', () => {
    const frame = pictureFrame(0.68, 8, 240);
    const wholeCanvas = exposureFix(rawHistogram(frame));
    expect(wholeCanvas.kind).toBe('fix');
    if (wholeCanvas.kind === 'fix') expect(wholeCanvas.primaries.exposure).toBeGreaterThan(0);
    const rect = pictureRectOf(vertical, media('c', 0, 60, 0), { width: 1920, height: 1080 }, 10, 108, 192);
    expect(exposureFix(pictureHistogram(frame, rect)).kind).toBe('fine');
    // Belt and braces: with no geometry, the bar trim alone still finds the well-exposed picture.
    expect(exposureFix(pictureHistogram(frame, null)).kind).toBe('fine');
  });

  it('a bright picture with 20% bars is darkened, not brightened', () => {
    const r = exposureFix(pictureHistogram(pictureFrame(0.2, 150, 245), null));
    expect(r.kind).toBe('fix');
    if (r.kind === 'fix') {
      expect(r.after.median).toBeLessThan(r.before.median);
      expect(r.summary.stops).toBeLessThan(0);
    }
  });

  it('crops and trims without touching a frame that has no bars', () => {
    const frame = pictureFrame(0, 20, 240);
    expect(pictureHistogram(frame, null).total).toBeGreaterThan(0);
    expect(trimDarkBars(frame).width).toBe(108);
    expect(trimDarkBars(frame).height).toBe(192);
    expect(cropPixelBuffer(frame, { x: -10, y: 5, width: 10000, height: 3 })).toMatchObject({ width: 108, height: 3 });
    expect(readClipLumaHistogram(() => null, () => null)).toBeNull();
  });

  it('source size comes from the pool probe for the clip asset', () => {
    const clip = media('c', 0, 60, 0);
    const ref = 'file:00000000-0000-4000-8000-000000000001';
    const pool = [{ mediaRef: ref, probe: { width: 1920, height: 1080 } }] as unknown as MediaPoolItem[];
    expect(sourceSizeFromPool(pool, clip)).toEqual({ width: 1920, height: 1080 });
    expect(sourceSizeFromPool([], clip)).toBeNull();
  });
});

describe('write plan', () => {
  it('writes one setProperty grade on an ungrouped clip', () => {
    const t = resolveFixTarget(docWith(media('c', 0, 60, 0)), 'c');
    expect(t?.group).toBeNull();
    const plan = buildFixWrite(t!, primaries, () => 'new1');
    expect(plan.ok && plan.ops).toHaveLength(1);
    expect(plan.ok && plan.ops[0]).toMatchObject({ type: 'setProperty', itemId: 'c', key: 'grade' });
  });

  it('writes into the group stack when the group has a grade; a group without one uses the clip', () => {
    const graded: ColorGroup = { id: 'g1', name: 'G', clipIds: ['c'], grade: stackOf(1) };
    const t = resolveFixTarget(docWith(media('c', 0, 60, 0), [graded]), 'c');
    expect(t?.group?.id).toBe('g1');
    const plan = buildFixWrite(t!, primaries, () => 'new1');
    expect(plan.ok && plan.group).toBe(true);
    expect(plan.ok && plan.ops[0]).toMatchObject({ type: 'setColorGroup', groupId: 'g1' });
    const empty = resolveFixTarget(docWith(media('c', 0, 60, 0), [{ ...graded, grade: null }]), 'c');
    expect(empty?.group).toBeNull();
  });

  it('replaces the Auto fix node in place and refuses past the cap', () => {
    const base = media('c', 0, 60, 0);
    const once = buildFixWrite(resolveFixTarget(docWith(base), 'c')!, primaries, () => 'a');
    if (!once.ok || once.ops[0].type !== 'setProperty') throw new Error('expected a grade write');
    const stack = once.ops[0].value as GradeStack;
    const again = buildFixWrite(resolveFixTarget(docWith({ ...base, grade: stack }), 'c')!, { ...primaries, exposure: 1 }, () => 'b');
    const next = again.ok && again.ops[0].type === 'setProperty' ? (again.ops[0].value as GradeStack) : null;
    expect(next?.nodes?.filter((n) => n.name === FIX_NODE_NAME)).toHaveLength(1);
    expect(next?.nodes?.[0].primaries?.exposure).toBe(1);
    const full = resolveFixTarget(docWith({ ...base, grade: stackOf(MAX_GRADE_NODES) }), 'c')!;
    expect(buildFixWrite(full, primaries, () => 'c')).toEqual({ ok: false, reason: 'tooManyNodes' });
  });

  it('a gone clip resolves to null; a changed stack changes the key', () => {
    expect(resolveFixTarget(docWith(media('c', 0, 60, 0)), 'nope')).toBeNull();
    const a = resolveFixTarget(docWith(media('c', 0, 60, 0)), 'c')!;
    const b = resolveFixTarget(docWith({ ...media('c', 0, 60, 0), grade: stackOf(1) }), 'c')!;
    expect(targetKey(a)).not.toBe(targetKey(b));
  });

  it('summarises in plain parts', () => {
    expect(fixSummaryParts({ stops: 0.8, shadows: 'deeper', highlights: 'same' })).toEqual(['brighter', 'deeper']);
    expect(fixSummaryParts({ stops: 0, shadows: 'same', highlights: 'same' })).toEqual([]);
  });
});
