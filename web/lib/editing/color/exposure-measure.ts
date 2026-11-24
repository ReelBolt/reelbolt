/**
 * Fix exposure, the measuring driver and the write plan (shared by the Colour page and the phone Look
 * sheet). The maths is `exposure-fix.ts`; this file only
 *   - reads up to 5 frames of the clip from the viewer with the grade bypassed (the camera-profile input
 *     stage is NOT part of the bypass: `live-session` drops only the folded grade and still passes
 *     `inputProfile`, so log footage is measured after its camera conversion),
 *   - decides where the one "Auto fix" node goes (the clip stack, or the colour group's stack), and
 *   - runs the job in the background with an anchor, so a clip edited meanwhile is never written silently.
 * Dependencies are injected so the driver unit-tests without a DOM.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import type { EditOp } from '../engine';
import type { ColorGroup, EditTimeline, EditTimelineSettings, GradeStack, MediaClip } from '../types';
import { programPictureCanvas } from '../stills/stills';
import { fitFor } from '../preview/gl-compositor';
import { hasTransform, layerTransformAt, mediaRefOf } from '../preview/scheduler';
import type { MediaPoolItem } from '../media/types';
import { computeScopes, luma709, sampleCanvas, type PixelBuffer } from './scopes';
import { colorGroupOf } from './grade';
import {
  exposureFix, findAutoFixNode, histogramFromScopes, mergeHistograms, withAutoFixNode,
  type ExposureFixResult, type FixPrimaries, type LumaHistogram,
} from './exposure-fix';

export type FixSummary = Extract<ExposureFixResult, { kind: 'fix' }>['summary'];

export type FixPart = 'brighter' | 'darker' | 'deeper' | 'lifted' | 'highlightsUp' | 'highlightsDown';

/** The plain-word parts of a fix summary, in reading order (empty when nothing visible changed). */
export function fixSummaryParts(summary: FixSummary): FixPart[] {
  const parts: FixPart[] = [];
  if (Math.round(Math.abs(summary.stops) * 10) > 0) parts.push(summary.stops > 0 ? 'brighter' : 'darker');
  if (summary.shadows !== 'same') parts.push(summary.shadows === 'deeper' ? 'deeper' : 'lifted');
  if (summary.highlights !== 'same') parts.push(summary.highlights === 'brighter' ? 'highlightsUp' : 'highlightsDown');
  return parts;
}

export const MAX_MEASURE_FRAMES = 5;
const EVEN_FRAMES = 4;
const SETTLE_MS = 600;
const SAMPLE_WIDTH = 192;

export interface ClipRange {
  recordIn: number;
  recordOut: number;
}

/** The frames to read: the playhead (when inside the clip) plus up to 4 evenly spaced, never more than 5, no duplicates. */
export function sampleFrames(clip: ClipRange, playhead: number): number[] {
  const last = Math.max(clip.recordIn, clip.recordOut - 1);
  const span = Math.max(0, clip.recordOut - clip.recordIn);
  const frames: number[] = [];
  if (playhead >= clip.recordIn && playhead < clip.recordOut) frames.push(Math.trunc(playhead));
  for (let i = 0; i < EVEN_FRAMES; i++) {
    const f = Math.min(last, clip.recordIn + Math.floor((span * (i + 0.5)) / EVEN_FRAMES));
    if (!frames.includes(f)) frames.push(f);
  }
  return frames.slice(0, MAX_MEASURE_FRAMES);
}

/** A rectangle in canvas (or sampled-buffer) pixels. */
export interface PictureRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * The rectangle the clip's picture occupies on the program canvas, from the compositor's own
 * geometry: `fitFor` over the layer transform (crop included) and the timeline's aspect policy,
 * with the source's probed size. `canvasWidth`/`canvasHeight` are the sampled buffer's size, so
 * the fractions computed at the timeline settings size land on the sample. Null when the geometry
 * cannot be resolved (source size unknown, a rotated layer, no canvas): callers then trim bars.
 */
export function pictureRectOf(
  settings: Pick<EditTimelineSettings, 'width' | 'height' | 'aspectPolicy'>,
  clip: MediaClip,
  source: { width: number; height: number } | null,
  frame: number,
  canvasWidth: number,
  canvasHeight: number,
): PictureRect | null {
  if (!source || !(source.width > 0) || !(source.height > 0)) return null;
  if (!(canvasWidth > 0) || !(canvasHeight > 0)) return null;
  if (!(settings.width > 0) || !(settings.height > 0)) return null;
  const t = layerTransformAt(clip, frame);
  // A rotated quad is not axis-aligned: no honest rectangle to crop to.
  if (!Number.isFinite(t.rotation) || Math.abs(t.rotation) > 1e-6) return null;
  const transformed = hasTransform(t);
  const layer = transformed ? { transform: t } : { aspectPolicy: settings.aspectPolicy ?? 'fit' };
  const [fitX, fitY] = fitFor(layer, source.width, source.height, settings.width, settings.height);
  const scale = transformed && Number.isFinite(t.scale) && t.scale > 0 ? t.scale : 1;
  const w = fitX * scale * canvasWidth;
  const h = fitY * scale * canvasHeight;
  if (!(w >= 1) || !(h >= 1)) return null;
  const cx = (0.5 + (transformed ? t.x : 0)) * canvasWidth;
  // Same convention as the compositor, the render (`y=(H-h)/2+H*y`) and the transform handles: +y moves down.
  const cy = (0.5 + (transformed ? t.y : 0)) * canvasHeight;
  const x0 = Math.max(0, Math.round(cx - w / 2));
  const y0 = Math.max(0, Math.round(cy - h / 2));
  const x1 = Math.min(canvasWidth, Math.round(cx + w / 2));
  const y1 = Math.min(canvasHeight, Math.round(cy + h / 2));
  if (!(x1 > x0) || !(y1 > y0)) return null;
  return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
}

const clampInt = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, Math.round(v)));

/** The `rect` window of a sampled frame (a copy: the scope builder must never see the whole canvas). */
export function cropPixelBuffer(buffer: PixelBuffer, rect: PictureRect): PixelBuffer | null {
  const x = clampInt(rect.x, 0, buffer.width - 1);
  const y = clampInt(rect.y, 0, buffer.height - 1);
  const w = Math.min(buffer.width - x, clampInt(rect.width, 0, buffer.width));
  const h = Math.min(buffer.height - y, clampInt(rect.height, 0, buffer.height));
  if (!(w > 0) || !(h > 0)) return null;
  const data = new Uint8ClampedArray(w * h * 4);
  for (let row = 0; row < h; row++) {
    const from = ((y + row) * buffer.width + x) * 4;
    data.set(buffer.data.subarray(from, from + w * 4), row * w * 4);
  }
  return { data, width: w, height: h };
}

const DARK_BAR_LUMA = 2;

const darkPixel = (data: PixelBuffer['data'], i: number): boolean => luma709(data[i], data[i + 1], data[i + 2]) < DARK_BAR_LUMA;

/**
 * Belt and braces for a fit clip whose picture rect could not be resolved: drop edge rows and
 * columns that are below 2/255 luma across their whole width/height (letterbox and pillarbox bars).
 * A frame that is entirely black is kept as it is (it is "no picture"/"flat", not a crop).
 */
export function trimDarkBars(buffer: PixelBuffer): PixelBuffer {
  const { data, width, height } = buffer;
  const rowDark = (y: number): boolean => {
    for (let x = 0; x < width; x++) if (!darkPixel(data, (y * width + x) * 4)) return false;
    return true;
  };
  const colDark = (x: number, top: number, bottom: number): boolean => {
    for (let y = top; y <= bottom; y++) if (!darkPixel(data, (y * width + x) * 4)) return false;
    return true;
  };
  let top = 0;
  let bottom = height - 1;
  let left = 0;
  let right = width - 1;
  while (top <= bottom && rowDark(top)) top++;
  while (bottom >= top && rowDark(bottom)) bottom--;
  if (top > bottom) return buffer;
  while (left <= right && colDark(left, top, bottom)) left++;
  while (right >= left && colDark(right, top, bottom)) right--;
  if (right < left || (top === 0 && left === 0 && bottom === height - 1 && right === width - 1)) return buffer;
  return cropPixelBuffer(buffer, { x: left, y: top, width: right - left + 1, height: bottom - top + 1 }) ?? buffer;
}

/**
 * The luma histogram of a sampled frame: cropped to the picture rect when it is known, otherwise
 * bar-trimmed. The trim runs only without a rect, so a night shot's black sky inside a known picture
 * still counts; `trimBars: false` skips it too (the raw video frame of the fallback viewer has no bars).
 */
export function pictureHistogram(buffer: PixelBuffer, rect: PictureRect | null, trimBars = rect === null): LumaHistogram {
  const cropped = rect ? cropPixelBuffer(buffer, rect) ?? buffer : buffer;
  return histogramFromScopes(computeScopes(trimBars ? trimDarkBars(cropped) : cropped, { columns: 32, levels: 64, vectorSize: 16 }));
}

/** What the reader needs to know where the clip's picture sits: the doc, its clip, the frame and the probed source size. */
export interface PictureGeometry {
  doc: EditTimeline;
  clip: MediaClip;
  frame: number;
  sourceSize: { width: number; height: number } | null;
}

/**
 * The luma histogram of the clip's PICTURE on the program canvas, or null while it holds none. The
 * whole program canvas includes the black bars of a `fit` clip (a 16:9 clip in a 9:16 timeline is
 * ~68% bars), which would drag the median down and make the fix brighten a well-exposed clip.
 */
export function readClipLumaHistogram(
  getCanvas: () => HTMLCanvasElement | null,
  geometry: () => PictureGeometry | null,
): LumaHistogram | null {
  const program = getCanvas();
  const canvas = programPictureCanvas(program);
  const buffer = canvas ? sampleCanvas(canvas, SAMPLE_WIDTH) : null;
  if (!buffer) return null;
  // The fallback viewer (no WebGL) hands back a copy of the raw video frame: it is all picture, with no
  // program-canvas layout, so neither the rect nor the bar trim applies.
  if (canvas !== program) return pictureHistogram(buffer, null, false);
  const g = geometry();
  const rect = g ? pictureRectOf(g.doc.settings, g.clip, g.sourceSize, g.frame, buffer.width, buffer.height) : null;
  return pictureHistogram(buffer, rect);
}

/** The clip's source size from the project's media pool (the same probe `sourceSizeFor` uses), when known. */
export function sourceSizeFromPool(pool: readonly MediaPoolItem[] | undefined, clip: MediaClip): { width: number; height: number } | null {
  const ref = mediaRefOf(clip.assetRef);
  const probe = ref ? pool?.find((p) => p.mediaRef === ref)?.probe : null;
  const width = probe?.width ?? 0;
  const height = probe?.height ?? 0;
  return width > 0 && height > 0 ? { width, height } : null;
}

export interface MeasureDeps {
  seek: (frame: number) => void;
  settle: (ms: number) => Promise<void>;
  setBypass: (on: boolean) => void;
  /** Where the playhead and bypass were when the job started; both are restored. */
  playheadFrame: number;
  bypass: boolean;
  /** The histogram of what the viewer shows now (default: the program canvas). */
  read: () => LumaHistogram | null;
  signal?: AbortSignal;
  onProgress?: (done: number, total: number) => void;
}

export type MeasureOutcome = { kind: 'cancelled' } | { kind: 'result'; result: ExposureFixResult };

/** Seek, settle and read each frame with grades bypassed; restore playhead and bypass whatever happens. */
export async function measureClip(deps: MeasureDeps, clip: ClipRange): Promise<MeasureOutcome> {
  const frames = sampleFrames(clip, deps.playheadFrame);
  const total = frames.length;
  const histograms: LumaHistogram[] = [];
  deps.onProgress?.(0, total);
  try {
    if (deps.signal?.aborted) return { kind: 'cancelled' };
    deps.setBypass(true);
    for (let i = 0; i < total; i++) {
      deps.seek(frames[i]);
      await deps.settle(SETTLE_MS);
      if (deps.signal?.aborted) return { kind: 'cancelled' };
      const h = deps.read();
      if (!h) return { kind: 'result', result: { kind: 'noPicture' } };
      histograms.push(h);
      deps.onProgress?.(i + 1, total);
    }
    return { kind: 'result', result: exposureFix(mergeHistograms(histograms)) };
  } finally {
    deps.setBypass(deps.bypass);
    deps.seek(deps.playheadFrame);
  }
}

// --- where the node goes ---------------------------------------------------------------------

export type FixTarget = {
  clipId: string;
  range: ClipRange;
  /** The group whose stack takes the node (only a group with a non-empty grade), else null: the clip's own stack. */
  group: ColorGroup | null;
  stack: GradeStack | null;
};

type Item = EditTimeline['tracks'][number]['items'][number];

function findItem(doc: EditTimeline, id: string): Item | null {
  for (const track of doc.tracks) for (const item of track.items) if (item.id === id) return item;
  return null;
}

/** Null when the clip is gone (or is not a graded kind of clip). */
export function resolveFixTarget(doc: EditTimeline, clipId: string): FixTarget | null {
  const item = findItem(doc, clipId);
  if (!item || (item.type !== 'mediaClip' && item.type !== 'adjustmentClip')) return null;
  const found = colorGroupOf(doc, clipId);
  const group = found && (found.grade?.nodes?.length ?? 0) > 0 ? found : null;
  return { clipId, range: { recordIn: item.recordIn, recordOut: item.recordOut }, group, stack: group ? (group.grade ?? null) : (item.grade ?? null) };
}

/** A stable key of the stack being fixed (and where it lives), to see whether it changed meanwhile. */
export function targetKey(target: FixTarget): string {
  return JSON.stringify([target.group?.id ?? null, target.stack]);
}

export const hasAutoFix = (target: FixTarget | null): boolean => !!target && findAutoFixNode(target.stack) !== null;

export type FixWrite = { ok: true; ops: EditOp[]; group: boolean } | { ok: false; reason: 'tooManyNodes' };

/** The ops of ONE undo step that put the Auto fix node on the target (replacing a previous one in place). */
export function buildFixWrite(target: FixTarget, primaries: FixPrimaries, newId: () => string): FixWrite {
  const written = withAutoFixNode(target.stack, primaries, newId());
  if (!written.ok) return { ok: false, reason: written.reason };
  if (target.group) {
    return { ok: true, group: true, ops: [{ type: 'setColorGroup', groupId: target.group.id, group: { ...target.group, grade: written.stack } }] };
  }
  return { ok: true, group: false, ops: [{ type: 'setProperty', itemId: target.clipId, key: 'grade', value: written.stack }] };
}

// --- the background job ----------------------------------------------------------------------

/** The small, explicit job shape the activity tray can adopt later. */
export interface FixJob {
  label: string;
  targetItemId: string;
  progress: { done: number; total: number };
  cancel: () => void;
}

export type FixState =
  | { phase: 'idle' }
  | { phase: 'measuring'; job: FixJob }
  | { phase: 'fixed'; summary: FixSummary; group: boolean }
  | { phase: 'nothing'; reason: 'fine' | 'flat' | 'noPicture' }
  | { phase: 'tooManyNodes' }
  | { phase: 'changed'; primaries: FixPrimaries; summary: FixSummary }
  | { phase: 'gone' };

export interface FixEnv {
  clipId: string | null;
  readOnly: boolean;
  label: string;
  /** The live timeline (read at the end of the job, not captured). */
  getDoc: () => EditTimeline;
  getPlayhead: () => number;
  getBypass: () => boolean;
  setBypass: (on: boolean) => void;
  seek: (frame: number) => void;
  getCanvas: () => HTMLCanvasElement | null;
  /** The clip's source size from the media probe (null when unknown: the reader trims bars instead). */
  getSourceSize?: (clip: MediaClip) => { width: number; height: number } | null;
  /** One undo step. */
  apply: (ops: readonly EditOp[], label: string) => void;
  newId: () => string;
  settle?: (ms: number) => Promise<void>;
  read?: () => LumaHistogram | null;
}

const defaultSettle = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export function useFixExposure(env: FixEnv) {
  const [state, setState] = useState<FixState>({ phase: 'idle' });
  const live = useRef(env);
  useEffect(() => {
    live.current = env;
  });
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);

  const commit = useCallback((target: FixTarget, primaries: FixPrimaries, summary: FixSummary) => {
    const e = live.current;
    const plan = buildFixWrite(target, primaries, e.newId);
    if (!plan.ok) { setState({ phase: 'tooManyNodes' }); return; }
    e.apply(plan.ops, e.label);
    setState({ phase: 'fixed', summary, group: plan.group });
  }, []);

  const start = useCallback(async () => {
    const e = live.current;
    if (e.readOnly || !e.clipId || abort.current) return;
    const clipId = e.clipId;
    const anchor = resolveFixTarget(e.getDoc(), clipId);
    if (!anchor) { setState({ phase: 'gone' }); return; }
    const anchorKey = targetKey(anchor);
    const ac = new AbortController();
    abort.current = ac;
    const job: FixJob = { label: e.label, targetItemId: clipId, progress: { done: 0, total: sampleFrames(anchor.range, e.getPlayhead()).length }, cancel: () => ac.abort() };
    setState({ phase: 'measuring', job });
    let wrote = false;
    try {
      const outcome = await measureClip({
        seek: e.seek,
        settle: e.settle ?? defaultSettle,
        setBypass: e.setBypass,
        playheadFrame: e.getPlayhead(),
        bypass: e.getBypass(),
        read: e.read ?? (() => readClipLumaHistogram(e.getCanvas, () => {
          const doc = live.current.getDoc();
          const item = findItem(doc, clipId);
          if (!item || item.type !== 'mediaClip') return null;
          const geo = live.current;
          return { doc, clip: item, frame: geo.getPlayhead(), sourceSize: geo.getSourceSize?.(item) ?? null };
        })),
        signal: ac.signal,
        onProgress: (done, total) => setState({ phase: 'measuring', job: { ...job, progress: { done, total } } }),
      }, anchor.range);
      if (outcome.kind === 'cancelled') { setState({ phase: 'idle' }); return; }
      const r = outcome.result;
      if (r.kind !== 'fix') { setState({ phase: 'nothing', reason: r.kind }); return; }
      const now = resolveFixTarget(live.current.getDoc(), clipId);
      if (!now) { setState({ phase: 'gone' }); return; }
      if (targetKey(now) !== anchorKey) { setState({ phase: 'changed', primaries: r.primaries, summary: r.summary }); return; }
      commit(now, r.primaries, r.summary);
      wrote = true;
    } catch {
      // A throw while seeking/settling/reading must never strand the sheet in "measuring" with a
      // dead Cancel: show the plain "could not read the picture" result (measureClip already
      // restored the playhead and bypass in its own finally).
      if (!wrote) setState({ phase: 'nothing', reason: 'noPicture' });
    } finally {
      abort.current = null;
    }
  }, [commit]);

  const cancel = useCallback(() => abort.current?.abort(), []);

  /** After "The clip changed while measuring": write onto whatever the target is now. */
  const applyAnyway = useCallback(() => {
    const e = live.current;
    if (state.phase !== 'changed' || !e.clipId || e.readOnly) return;
    const now = resolveFixTarget(e.getDoc(), e.clipId);
    if (!now) { setState({ phase: 'gone' }); return; }
    commit(now, state.primaries, state.summary);
  }, [commit, state]);

  return { state, start: () => void start(), cancel, applyAnyway, measureAgain: () => void start() };
}
