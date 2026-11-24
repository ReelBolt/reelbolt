import type { EditOp } from '@/lib/editing/engine';
import type { BlendMode, ClipTransform, EditTimeline, Keyframed, MediaClip, Rational } from '@/lib/editing/types';
import { checkFadeSeconds, checkOpacityPercent, framesToSeconds, opacityToPercent } from '@/lib/editing/clip-depth';
import {
  baseSize, fillValues, fitValues, padValues, POSITION_LIMIT, SCALE_MAX, SCALE_MIN, transformAt, transformOps, keyedChannels,
  type TransformValues,
} from '@/lib/editing/viewer-transform';
import { evaluateKeyframed } from '@/lib/editing/preview';
import { enhanceSubject } from '@/lib/editing/enhance/plan';
import type { BuildResult } from '@/lib/editing/timeline-edit/clipboard';

/**
 * R07.7: the pure side of the phone clip sheet. Each builder returns the ops of ONE undo step, composed from the
 * same helpers the desktop inspector and viewer use (transformOps, checkOpacityPercent, checkFadeSeconds), or a
 * plain sentence saying why not. A channel that carries keyframes is never written from the phone: it is shown
 * read-only as "animated on desktop".
 */

export type PhoneChannel = 'positionX' | 'positionY' | 'scale' | 'opacity';

export interface ClipSheetInfo {
  clip: MediaClip;
  onPictureTrack: boolean;
  /** Position, scale and rotation at the playhead. */
  values: TransformValues;
  /** Opacity as a percent (0 to 100). */
  opacityPercent: number;
  /** Which channels are animated (keyframed): read-only here. */
  animated: Record<PhoneChannel | 'rotation', boolean>;
  fadeInFrames: number;
  fadeOutFrames: number;
  durationFrames: number;
  /** Things on the clip the phone does not edit, in plain words. */
  readOnlyNotes: string[];
}

const hasKeys = (c: Keyframed<number> | null | undefined) => Array.isArray(c?.keys) && c!.keys!.length > 0;

export function clipSheetInfoOf(doc: EditTimeline, clip: MediaClip, playheadFrame: number): ClipSheetInfo {
  const onPictureTrack = doc.tracks.find((t) => t.items.some((i) => i.id === clip.id))?.kind === 'video';
  const values = transformAt(clip, playheadFrame);
  const keyed = keyedChannels(clip);
  const opacityChannel = clip.transform?.opacity;
  const opacity = evaluateKeyframed(opacityChannel ?? null, playheadFrame, typeof opacityChannel?.value === 'number' ? opacityChannel.value : 1);
  const notes: string[] = [];
  if (Math.abs(values.rotation) > 1e-6) notes.push(`Rotated ${Math.round(values.rotation)} degrees${keyed.rotation ? ' (animated on desktop)' : ''}. Change it on desktop.`);
  const crop = clip.crop;
  if (crop && Object.values(crop).some((v) => typeof v === 'number' && v > 0)) notes.push('Cropped. Change it on desktop.');
  if (clip.reverse) notes.push('Plays backwards. Change it on desktop.');
  if (clip.hold) notes.push('A freeze frame.');
  return {
    clip,
    onPictureTrack,
    values,
    opacityPercent: opacityToPercent(opacity),
    animated: { positionX: keyed.positionX, positionY: keyed.positionY, scale: keyed.scale, rotation: keyed.rotation, opacity: hasKeys(opacityChannel) },
    fadeInFrames: clip.fadeInFrames ?? 0,
    fadeOutFrames: clip.fadeOutFrames ?? 0,
    durationFrames: clip.recordOut - clip.recordIn,
    readOnlyNotes: notes,
  };
}

const ANIMATED = 'This is animated, so it can only be changed on desktop.';

export interface Canvas {
  width: number;
  height: number;
}

function guardAnimated(info: ClipSheetInfo, channels: readonly (PhoneChannel | 'rotation')[]): string | null {
  return channels.some((c) => info.animated[c]) ? ANIMATED : null;
}

/** The next source orientation in the 0 -> 90 -> 180 -> 270 -> 0 cycle. */
export function nextSourceRotation(current: number | null | undefined): 0 | 90 | 180 | 270 {
  return current === 90 ? 180 : current === 180 ? 270 : current === 270 ? 0 : 90;
}

/** The source size as the picture is seen after the source orientation: width and height swap at 90 and 270. */
export function orientedSourceSize(source: { width: number; height: number } | null, rotation: number | null | undefined): { width: number; height: number } | null {
  if (source === null) return null;
  return rotation === 90 || rotation === 270 ? { width: source.height, height: source.width } : source;
}

/** Rotate 90: one setProperty (0 clears the field), so one undo step. */
export function buildRotateOps(info: ClipSheetInfo): BuildResult<{ ops: EditOp[]; label: string }> {
  if (!info.onPictureTrack) return { ok: false, reason: 'Only a clip on a picture track can be rotated.' };
  const next = nextSourceRotation(info.clip.sourceRotation);
  return { ok: true, ops: [{ type: 'setProperty', itemId: info.clip.id, key: 'sourceRotation', value: next === 0 ? undefined : next }], label: 'Rotate 90°' };
}

/** Fit or Fill: the same presets as the viewer's menu (position back to centre, upright, scale to suit). */
export function buildFitFillOps(doc: EditTimeline, info: ClipSheetInfo, kind: 'fit' | 'fill', source: { width: number; height: number } | null, frame: number): BuildResult<{ ops: EditOp[]; label: string }> {
  if (!info.onPictureTrack) return { ok: false, reason: 'Only a clip on a picture track can be fitted.' };
  const blocked = guardAnimated(info, ['positionX', 'positionY', 'scale', 'rotation']);
  if (blocked) return { ok: false, reason: blocked };
  const canvas: Canvas = { width: doc.settings.width, height: doc.settings.height };
  const base = baseSize(canvas, orientedSourceSize(source, info.clip.sourceRotation), doc.settings.aspectPolicy, info.clip.crop ?? null);
  const next = kind === 'fit' ? fitValues(base, canvas) : fillValues(base, canvas);
  const ops = transformOps(info.clip, next, frame);
  if (ops.length === 0) return { ok: false, reason: kind === 'fit' ? 'The picture already fits.' : 'The picture already fills the frame.' };
  return { ok: true, ops, label: kind === 'fit' ? 'Fit picture' : 'Fill picture' };
}

/** A cell of the 3x3 pad (-1 left/top, 0 centre, 1 right/bottom). */
export function buildPadOps(doc: EditTimeline, info: ClipSheetInfo, col: -1 | 0 | 1, row: -1 | 0 | 1, source: { width: number; height: number } | null, frame: number): BuildResult<{ ops: EditOp[]; label: string }> {
  if (!info.onPictureTrack) return { ok: false, reason: 'Only a clip on a picture track can be moved.' };
  const blocked = guardAnimated(info, ['positionX', 'positionY']);
  if (blocked) return { ok: false, reason: blocked };
  const canvas: Canvas = { width: doc.settings.width, height: doc.settings.height };
  const base = baseSize(canvas, orientedSourceSize(source, info.clip.sourceRotation), doc.settings.aspectPolicy, info.clip.crop ?? null);
  const ops = transformOps(info.clip, padValues(info.values, base, canvas, col, row), frame);
  if (ops.length === 0) return { ok: false, reason: 'The picture is already there.' };
  return { ok: true, ops, label: 'Place picture' };
}

/** A typed or slid position (percent of the frame, +right and +down) or scale (percent). */
export function buildChannelOps(info: ClipSheetInfo, channel: 'positionX' | 'positionY' | 'scale', percent: number, frame: number): BuildResult<{ ops: EditOp[]; label: string }> {
  if (!info.onPictureTrack) return { ok: false, reason: 'Only a clip on a picture track has a place in the frame.' };
  const blocked = guardAnimated(info, [channel]);
  if (blocked) return { ok: false, reason: blocked };
  if (!Number.isFinite(percent)) return { ok: false, reason: 'Type a number. The old value is kept.' };
  const value = percent / 100;
  if (channel === 'scale') {
    if (value < SCALE_MIN || value > SCALE_MAX) return { ok: false, reason: `Size is between ${Math.round(SCALE_MIN * 100)}% and ${Math.round(SCALE_MAX * 100)}%. The old value is kept.` };
  } else if (Math.abs(value) > POSITION_LIMIT) {
    return { ok: false, reason: `Position is between -${POSITION_LIMIT * 100}% and ${POSITION_LIMIT * 100}%. The old value is kept.` };
  }
  const ops = transformOps(info.clip, { ...info.values, [channel]: Number(value.toFixed(4)) }, frame);
  if (ops.length === 0) return { ok: false, reason: 'That is already the value.' };
  return { ok: true, ops, label: channel === 'scale' ? 'Picture size' : 'Move picture' };
}

/** Opacity percent: written as the inspector does (identity removes the channel). */
export function buildOpacityOps(info: ClipSheetInfo, percent: number): BuildResult<{ ops: EditOp[]; label: string }> {
  if (!info.onPictureTrack) return { ok: false, reason: 'Only a clip on a picture track has opacity.' };
  const blocked = guardAnimated(info, ['opacity']);
  if (blocked) return { ok: false, reason: blocked };
  const checked = checkOpacityPercent(percent);
  if (!checked.ok) return { ok: false, reason: checked.sentence };
  const transform: Record<string, unknown> = { ...(info.clip.transform ?? {}) };
  if (checked.value === 1) delete transform.opacity;
  else transform.opacity = { value: checked.value, keys: null };
  const cleaned = Object.fromEntries(Object.entries(transform).filter(([, v]) => v !== undefined));
  const value = Object.keys(cleaned).length === 0 ? undefined : (cleaned as ClipTransform);
  return { ok: true, ops: [{ type: 'setProperty', itemId: info.clip.id, key: 'transform', value }], label: 'Opacity' };
}

/** The blend word picked on the sheet: `normal` removes the field. Picture tracks only. */
export function buildBlendOps(info: ClipSheetInfo, mode: BlendMode): BuildResult<{ ops: EditOp[]; label: string }> {
  if (!info.onPictureTrack) return { ok: false, reason: 'Only a clip on a picture track can blend.' };
  if ((info.clip.blendMode ?? 'normal') === mode) return { ok: false, reason: 'That is already the blend.' };
  return { ok: true, ops: [{ type: 'setProperty', itemId: info.clip.id, key: 'blendMode', value: mode === 'normal' ? undefined : mode }], label: 'Blend mode' };
}

/** A fade length typed in seconds, checked against the clip and the other fade. */
export function buildFadeOps(info: ClipSheetInfo, which: 'in' | 'out', seconds: number, rate: Rational): BuildResult<{ ops: EditOp[]; label: string }> {
  const other = which === 'in' ? info.fadeOutFrames : info.fadeInFrames;
  const checked = checkFadeSeconds(which, seconds, rate, info.durationFrames, other);
  if (!checked.ok) return { ok: false, reason: checked.sentence };
  const current = which === 'in' ? info.fadeInFrames : info.fadeOutFrames;
  if (checked.value === current) return { ok: false, reason: 'That is already the fade.' };
  const key = which === 'in' ? 'fadeInFrames' : 'fadeOutFrames';
  return { ok: true, ops: [{ type: 'setProperty', itemId: info.clip.id, key, value: checked.value > 0 ? checked.value : undefined }], label: which === 'in' ? 'Fade in' : 'Fade out' };
}

export const fadeSeconds = (frames: number, rate: Rational): number => framesToSeconds(frames, rate);

/**
 * The phone Enhance section's gate, pure: 'ok' for a media clip on a picture track whose source is a project file
 * or a render; otherwise why not. No keymap action is added (TOUCH_PARITY: the section is reached by scrolling the sheet).
 */
export function enhanceSheetState(doc: EditTimeline, clipId: string): 'ok' | 'notPicture' {
  return enhanceSubject(doc, clipId) ? 'ok' : 'notPicture';
}
