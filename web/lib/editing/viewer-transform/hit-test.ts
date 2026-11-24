/**
 * T-013: picking and text geometry behind the viewer's on-canvas transform. Pure functions, no DOM.
 *
 * Pictures (media clips) use the compositor's own geometry (`frameFor` over `baseSize`, incl. crop, scale and
 * rotation). Titles and captions use the preview's own text layout (`titleTemplateLayout` / `positionAnchor`),
 * so a box sits where the text is drawn. The text box width is an estimate (0.55 em per character), enough for
 * picking and a handle box; the text itself is always drawn by the compositor.
 */

import type { Caption, EditTimeline, Item, MediaClip, TextPosition, TitleClip } from '@/lib/editing/types';
import type { EditOp } from '@/lib/editing/engine';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { positionAnchor, titleTemplateLayout, TEXT_LINE_HEIGHT } from '@/lib/editing/preview/title-template';
import { SCALE_MAX, SCALE_MIN, baseSize, canvasToLocal, clamp, clampValues, frameFor, localToCanvas, rotateVec, scaleYOf, type Frame, type HandleSpec, type TransformValues, type Vec } from './geometry';
import { transformAt } from './draft';

export type PictureKind = 'mediaClip' | 'titleClip' | 'caption';

/** One thing on screen at a frame that the viewer can select. */
export type PictureTarget = { id: string; kind: PictureKind; frame: Frame };

type Canvas = { width: number; height: number };

/** Font size limits of a title (the validator's range for `fontSize`). */
export const FONT_SIZE_MIN = 8;
export const FONT_SIZE_MAX = 600;
const DEFAULT_FONT_SIZE = 48;
const CHAR_EM = 0.55;

export const TEXT_POSITIONS: readonly TextPosition[] = ['topLeft', 'topCenter', 'topRight', 'middleLeft', 'center', 'middleRight', 'bottomLeft', 'bottomCenter', 'bottomRight'];

const longestLine = (text: string): number => text.split('\n').reduce((m, l) => Math.max(m, l.length), 0);
const lineCount = (text: string): number => Math.max(1, text.split('\n').length);

/** The box a title occupies, in canvas pixels (`fontSize` overrides the stored size, for a live resize; `position` overrides the stored pad cell, for a live ghost). */
export function titleFrame(item: TitleClip, canvas: Canvas, overrides: { fontSize?: number; position?: TextPosition | null } = {}): Frame {
  const size = overrides.fontSize ?? (typeof item.fontSize === 'number' && item.fontSize > 0 ? item.fontSize : DEFAULT_FONT_SIZE);
  const fontPx = Math.max(8, Math.round((size / 1080) * canvas.height));
  const position = overrides.position === undefined ? item.position : overrides.position;
  const layout = titleTemplateLayout(item.template, canvas.width, canvas.height, fontPx, position);
  const w = Math.max(fontPx, Math.min(fontPx * CHAR_EM * longestLine(item.text), canvas.width * 0.9));
  const h = fontPx * TEXT_LINE_HEIGHT * lineCount(item.text);
  const left = layout.align === 'center' ? layout.x - w / 2 : layout.align === 'right' ? layout.x - w : layout.x;
  return { cx: left + w / 2, cy: layout.y + h / 2, w, h, rotation: 0 };
}

/** The box a caption occupies (`position` overrides the stored pad cell, for a live ghost). */
export function captionFrame(item: Caption, canvas: Canvas, overrides: { position?: TextPosition | null } = {}): Frame {
  const position = overrides.position === undefined ? item.position : overrides.position;
  const fontPx = Math.max(8, Math.round((36 / 1080) * canvas.height));
  const h = fontPx * TEXT_LINE_HEIGHT * lineCount(item.text) + fontPx * 0.3;
  const w = Math.max(fontPx, Math.min(fontPx * CHAR_EM * longestLine(item.text) + fontPx, canvas.width * 0.8));
  const a = positionAnchor(position ?? 'bottomCenter', canvas.width, canvas.height, h);
  const left = a.align === 'center' ? a.x - w / 2 : a.align === 'right' ? a.x - w : a.x;
  return { cx: left + w / 2, cy: a.y + h / 2, w, h, rotation: 0 };
}

/**
 * A title's box under its free transform. The render scales and rotates the whole canvas-size layer about the CANVAS
 * CENTRE, then offsets it by `positionX/Y` (canvas fractions), so the box centre is `C + R(S(c0 - C)) + offset`.
 */
export function titleBoxFor(base: Frame, v: TransformValues, canvas: Canvas): Frame {
  const sx = v.scale;
  const sy = scaleYOf(v);
  const off = rotateVec({ x: (base.cx - canvas.width / 2) * sx, y: (base.cy - canvas.height / 2) * sy }, v.rotation);
  return { cx: canvas.width / 2 + off.x + v.positionX * canvas.width, cy: canvas.height / 2 + off.y + v.positionY * canvas.height, w: base.w * sx, h: base.h * sy, rotation: v.rotation };
}

/** The inverse of {@link titleBoxFor}: the stored transform values that put a title's box at `box`. `scaleY` stays absent for a uniform box unless `previous` has one. */
export function titleValuesForBox(base: Frame, box: Frame, previous: TransformValues, canvas: Canvas): TransformValues {
  const sx = box.w / base.w;
  const sy = box.h / base.h;
  const off = rotateVec({ x: (base.cx - canvas.width / 2) * sx, y: (base.cy - canvas.height / 2) * sy }, box.rotation);
  const uniform = previous.scaleY === undefined && Math.abs(sx - sy) < 1e-9;
  return clampValues({
    positionX: (box.cx - canvas.width / 2 - off.x) / canvas.width,
    positionY: (box.cy - canvas.height / 2 - off.y) / canvas.height,
    scale: sx,
    ...(uniform ? {} : { scaleY: sy }),
    rotation: box.rotation,
  });
}

/** The values a box has in the media-clip convention (centre = canvas centre + position), so the shared drag math can run on it. */
export function boxAsValues(box: Frame, base: { w: number; h: number }, canvas: Canvas, withScaleY: boolean): TransformValues {
  const sx = box.w / base.w;
  const sy = box.h / base.h;
  return { positionX: (box.cx - canvas.width / 2) / canvas.width, positionY: (box.cy - canvas.height / 2) / canvas.height, scale: sx, ...(withScaleY || Math.abs(sx - sy) >= 1e-9 ? { scaleY: sy } : {}), rotation: box.rotation };
}

/**
 * The magnet for a moving title: when the box centre is within `threshold` canvas pixels of where one of the nine
 * pad cells would put the text, the pixel correction that lands it exactly there; zeros otherwise.
 */
export function padMagnet(item: TitleClip, box: Frame, canvas: Canvas, threshold: number): { dx: number; dy: number; position: TextPosition | null } {
  let best: { dx: number; dy: number; position: TextPosition | null; d: number } = { dx: 0, dy: 0, position: null, d: threshold };
  for (const position of TEXT_POSITIONS) {
    const target = titleFrame(item, canvas, { position });
    const dx = target.cx - box.cx;
    const dy = target.cy - box.cy;
    const d = Math.hypot(dx, dy);
    if (d <= best.d) best = { dx, dy, position, d };
  }
  return { dx: best.dx, dy: best.dy, position: best.position };
}

const onScreen = (item: Item, frame: number) => frame >= item.recordIn && frame < item.recordOut && (item as { enabled?: boolean }).enabled !== false;

/**
 * Every selectable item on screen at `frame`, TOPMOST FIRST. Text (drawn on the overlay layer above the pictures)
 * comes before pictures; within each, a later track is above an earlier one (the document lists tracks in compositing order).
 */
export function pictureTargetsAt(doc: EditTimeline, frame: number, sourceSizeFor: (clip: MediaClip) => { width: number; height: number } | null): PictureTarget[] {
  const canvas = { width: doc.settings.width, height: doc.settings.height };
  const text: PictureTarget[] = [];
  const pictures: PictureTarget[] = [];
  const videoTracks = doc.tracks.filter((t) => t.kind === 'video');
  for (let ti = videoTracks.length - 1; ti >= 0; ti -= 1) {
    for (const item of videoTracks[ti].items) {
      if (!onScreen(item, frame)) continue;
      if (item.type === 'titleClip') {
        const title = item as TitleClip;
        text.push({ id: item.id, kind: 'titleClip', frame: title.transform ? titleBoxFor(titleFrame(title, canvas), transformAt(title, frame), canvas) : titleFrame(title, canvas) });
      }
      else if (item.type === 'caption') text.push({ id: item.id, kind: 'caption', frame: captionFrame(item as Caption, canvas) });
      else if (item.type === 'mediaClip') {
        const clip = item as MediaClip;
        pictures.push({ id: clip.id, kind: 'mediaClip', frame: frameFor(transformAt(clip, frame), baseSize(canvas, sourceSizeFor(clip), doc.settings.aspectPolicy, clip.crop ?? null), canvas) });
      }
    }
  }
  return [...text, ...pictures];
}

/** Is the canvas point inside the (rotated) frame? */
export function pointInFrame(frame: Frame, p: Vec): boolean {
  const l = canvasToLocal(frame, p);
  return Math.abs(l.x) <= frame.w / 2 && Math.abs(l.y) <= frame.h / 2;
}

/**
 * The item a click picks: the topmost target under the point. With `cycleFrom` (Alt-click, the id of the item
 * selected now) it is the next one BELOW it under the point, wrapping to the topmost after the last; when
 * `cycleFrom` is not under the point it is the topmost. Null is empty canvas.
 */
export function hitTest(targets: readonly PictureTarget[], p: Vec, cycleFrom: string | null = null): PictureTarget | null {
  const under = targets.filter((t) => pointInFrame(t.frame, p));
  if (under.length === 0) return null;
  if (cycleFrom) {
    const at = under.findIndex((t) => t.id === cycleFrom);
    if (at >= 0) return under[(at + 1) % under.length];
  }
  return under[0];
}

// ── the single selected item (media, title or caption) ──────────────────────────

export type SelectedPicture = { item: MediaClip | TitleClip | Caption; kind: PictureKind };

/** The single selected media clip, title or caption that is on screen at the playhead, else null. */
export function selectedPicture(doc: EditTimeline, selection: readonly string[], frame: number): SelectedPicture | null {
  if (selection.length !== 1) return null;
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.id !== selection[0] || !onScreen(item, frame)) continue;
      if (item.type === 'mediaClip' || item.type === 'titleClip' || item.type === 'caption') return { item: item as SelectedPicture['item'], kind: item.type };
    }
  }
  return null;
}

// ── text: the 3x3 pad and the font size ─────────────────────────────────────────

/** The nearest of the nine pad cells to a canvas point (thirds of the canvas). */
export function nearestTextPosition(p: Vec, canvas: Canvas): TextPosition {
  const col = clamp(Math.floor((p.x / canvas.width) * 3), 0, 2);
  const row = clamp(Math.floor((p.y / canvas.height) * 3), 0, 2);
  return TEXT_POSITIONS[row * 3 + col];
}

/** A title's font size after dragging a corner handle: uniform about the opposite corner, clamped to the validator's range. */
export function titleFontSizeFromDrag(startFontSize: number, startFrame: Frame, corner: HandleSpec, pointer: Vec): number {
  const anchor = localToCanvas(startFrame, { x: -corner.local.x, y: -corner.local.y });
  const cornerVec = { x: corner.local.x * 2 * startFrame.w, y: corner.local.y * 2 * startFrame.h };
  const denom = cornerVec.x * cornerVec.x + cornerVec.y * cornerVec.y;
  if (denom === 0) return startFontSize;
  const p = canvasToLocal({ ...startFrame, cx: anchor.x, cy: anchor.y }, pointer);
  const factor = clamp((p.x * cornerVec.x + p.y * cornerVec.y) / denom, SCALE_MIN, SCALE_MAX);
  return clamp(Math.round(startFontSize * factor), FONT_SIZE_MIN, FONT_SIZE_MAX);
}

export const titleFontSize = (item: TitleClip): number => (typeof item.fontSize === 'number' && item.fontSize > 0 ? item.fontSize : DEFAULT_FONT_SIZE);

/** The ops for a title/caption pad move; none when the cell did not change. */
export function textPositionOps(item: TitleClip | Caption, position: TextPosition): EditOp[] {
  const current: TextPosition = item.position ?? (item.type === 'caption' ? 'bottomCenter' : 'center');
  return current === position ? [] : [{ type: 'setProperty', itemId: item.id, key: 'position', value: position }];
}

/** The ops for a title font-size resize; none when unchanged. */
export function titleFontSizeOps(item: TitleClip, fontSize: number): EditOp[] {
  return fontSize === titleFontSize(item) ? [] : [{ type: 'setProperty', itemId: item.id, key: 'fontSize', value: fontSize }];
}

/** Commit text ops as ONE undo step. False when nothing changed. */
export function commitTextOps(store: EditorStore, ops: EditOp[], label: string): boolean {
  if (ops.length === 0) return false;
  store.dispatchBatch(ops, label);
  return true;
}
