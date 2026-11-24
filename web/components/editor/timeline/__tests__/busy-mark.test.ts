/** @vitest-environment node */
/**
 * What a background job shows ON its clip: the body dims, a strip along the foot shows progress (a bar when the job
 * reports it, a moving stripe when it does not, static hatching under reduced motion), and a chip names the state.
 * The old corner square is gone.
 */

import { describe, expect, it } from 'vitest';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { ClipJobPaint } from '@/lib/editing/background/job-marks';
import { DARK_THEME, hitTestJobChip, paintTimeline, type PaintContext, type PaintUi, type Viewport } from '../paint';
import { CLIP_FALLBACK, withClipColors } from '../editorTheme';
import { JOB_STRIP_HEIGHT, jobChipRect } from '../clipJobPaint';
import { FALLBACK_PAINT_LABELS } from '../paintLabels';

const theme = withClipColors(DARK_THEME, CLIP_FALLBACK.dark);

const job = (over: Partial<ClipJobPaint> = {}): ClipJobPaint => ({ state: 'running', label: 'Magic blur', jobKind: 'objectEffect', markId: 'r1', ...over });

function run(jobOf: PaintUi['jobOf'], extra: Partial<PaintUi> = {}) {
  const fills: Array<{ x: number; y: number; w: number; h: number; fill: string; alpha: number }> = [];
  const texts: string[] = [];
  let strokes = 0;
  const ctx: Record<string, unknown> & { fillStyle: string; globalAlpha: number } = {
    canvas: { width: 800, height: 300 },
    save: () => {}, restore: () => {}, setTransform: () => {}, clearRect: () => {}, strokeRect: () => {},
    fillText: (s: string) => { texts.push(s); },
    beginPath: () => {}, moveTo: () => {}, lineTo: () => {}, stroke: () => { strokes += 1; }, rect: () => {}, clip: () => {},
    fillStyle: '', strokeStyle: '', lineWidth: 1, font: '', textAlign: 'left', textBaseline: 'top', globalAlpha: 1,
    fillRect: (x: number, y: number, w: number, h: number) => { fills.push({ x, y, w, h, fill: ctx.fillStyle, alpha: ctx.globalAlpha }); },
  };
  const ui: PaintUi = {
    playheadFrame: 0, selection: new Set(), inFrame: null, outFrame: null, rationaleFrames: [], parityOf: () => 'faithful', jobOf, ...extra,
    trackFlags: (t) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo, collapsed: false }),
  };
  const viewport: Viewport = { width: 800, height: 300, scrollFrame: 0, pixelsPerFrame: 4, devicePixelRatio: 1 };
  const d = doc([track('v', 'video', [media('busy', 0, 100, 0), media('idle', 120, 220, 0)])]);
  paintTimeline(ctx as unknown as PaintContext, d, viewport, ui, theme);
  const strips = fills.filter((f) => f.h === JOB_STRIP_HEIGHT && f.fill === theme.item.transition);
  return { fills, texts, strokes: () => strokes, strips, d, ui, viewport };
}

describe('job state on a clip', () => {
  it('no job paints nothing extra and no corner square', () => {
    const r = run(undefined);
    expect(r.strips).toHaveLength(0);
    expect(r.texts).not.toContain('Processing');
    expect(r.fills.filter((f) => f.fill === theme.item.transition && f.w <= 10 && f.h <= 10)).toHaveLength(0);
  });
  it('running with progress: a bar as wide as the progress, on the busy clip only, plus the dimming and the job name', () => {
    const r = run((id) => (id === 'busy' ? job({ progress: 0.5 }) : undefined));
    expect(r.strips).toHaveLength(1);
    // busy spans 400 px; a half-done job fills about half of it
    expect(r.strips[0]!.w).toBeGreaterThan(190);
    expect(r.strips[0]!.w).toBeLessThan(210);
    expect(r.strips[0]!.x).toBeLessThan(400);
    expect(r.fills.some((f) => f.fill === theme.chip && f.alpha === 0.22)).toBe(true);
    expect(r.texts).toContain('Magic blur');
  });
  it('running with no progress: a moving block when a clock is given, hatching when it is not (reduced motion)', () => {
    const moving = run((id) => (id === 'busy' ? job() : undefined), { nowMs: 1000 });
    expect(moving.strips.length).toBeGreaterThan(0);
    expect(moving.strips[0]!.w).toBeLessThan(400 * 0.5);
    const first = moving.strips[0]!.x;
    const later = run((id) => (id === 'busy' ? job() : undefined), { nowMs: 2000 }).strips[0]!.x;
    expect(later).not.toBe(first);
    const still = run((id) => (id === 'busy' ? job() : undefined));
    expect(still.strips).toHaveLength(0);
    expect(still.strokes()).toBeGreaterThan(0);
  });
  it('an empty label falls back to the "Processing" word', () => {
    expect(run((id) => (id === 'busy' ? job({ label: '' }) : undefined)).texts).toContain('Processing');
  });
  it('needs review: a "Ready to review" chip in the accent colour, no dimming, no strip', () => {
    const r = run((id) => (id === 'busy' ? job({ state: 'needsReview' }) : undefined));
    expect(r.texts).toContain('Ready to review');
    expect(r.strips).toHaveLength(0);
    expect(r.fills.some((f) => f.alpha === 0.22)).toBe(false);
    expect(r.fills.some((f) => f.fill === theme.selected && f.h === 14)).toBe(true);
  });
  it('failed: an error-coloured chip', () => {
    const r = run((id) => (id === 'busy' ? job({ state: 'failed', message: 'No credits' }) : undefined));
    expect(r.texts).toContain('Did not finish');
    expect(r.fills.some((f) => f.fill === theme.renderRequired && f.h === 14)).toBe(true);
  });
  it('a caller that only knows "busy" still gets the running state', () => {
    const r = run(undefined, { busyClipIds: new Set(['busy']), nowMs: 1000 });
    expect(r.strips.length).toBeGreaterThan(0);
  });
  it('a clip too narrow for the chip paints the strip but no chip', () => {
    expect(jobChipRect(job(), FALLBACK_PAINT_LABELS, 0, 60, 4, 56)).toBeNull();
    expect(jobChipRect(job(), FALLBACK_PAINT_LABELS, 0, 300, 4, 56)).not.toBeNull();
  });
  it('the chip is found by the click through the same geometry', () => {
    const r = run((id) => (id === 'busy' ? job({ state: 'needsReview' }) : undefined));
    let hit = null as ReturnType<typeof hitTestJobChip>;
    for (let y = 0; y < 300 && !hit; y += 1) hit = hitTestJobChip(r.d, r.viewport, r.ui, 380, y);
    expect(hit?.item.id).toBe('busy');
    expect(hit?.job.state).toBe('needsReview');
    expect(hitTestJobChip(r.d, r.viewport, r.ui, 10, 5)).toBeNull();
  });
});
