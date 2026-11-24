/**
 * R07.1: the "Animate" front door. A motion preset turns a plain choice (Zoom in, Slide in from the
 * left, Ken Burns...) into keyframes. A builder returns the EXISTING ops (removeKeyframe,
 * setKeyframe): the workspace dispatches them as ONE batch, so one undo restores the clip. No new op.
 * Keys are timeline frames inside the clip's span; each preset replaces any earlier motion on the
 * clip (it removes the keys of every transform channel first).
 */
import type { EditOp, KeyframeParam } from '../engine';
import type { MediaClip, Rational } from '../types';
import { secondsToFrames } from './units';

export type MotionPresetId =
  | 'zoomIn'
  | 'zoomOut'
  | 'pop'
  | 'slideLeft'
  | 'slideRight'
  | 'slideTop'
  | 'slideBottom'
  | 'fade'
  | 'kenBurns';

/** Whether a slide or fade plays as the clip arrives (start) or leaves (end). */
export type MotionTiming = 'start' | 'end';

export interface MotionPresetInfo {
  id: MotionPresetId;
  label: string;
  /** One plain sentence under the chip. */
  hint: string;
  /** The "how much" control: label, default and bounds (percent). Null = the preset has none. */
  strength: { label: string; defaultPercent: number; min: number; max: number } | null;
  /** The motion has a length the user can set (Ken Burns spans the whole clip). */
  hasDuration: boolean;
  /** An "arrives / leaves" choice applies. */
  hasTiming: boolean;
}

export const MOTION_PRESETS: readonly MotionPresetInfo[] = [
  { id: 'zoomIn', label: 'Zoom in', hint: 'Slowly pushes in.', strength: { label: 'How much', defaultPercent: 20, min: 5, max: 100 }, hasDuration: true, hasTiming: false },
  { id: 'zoomOut', label: 'Zoom out', hint: 'Starts close and pulls back.', strength: { label: 'How much', defaultPercent: 20, min: 5, max: 100 }, hasDuration: true, hasTiming: false },
  { id: 'pop', label: 'Pop', hint: 'Pops in with a small bounce.', strength: { label: 'How big', defaultPercent: 40, min: 10, max: 90 }, hasDuration: true, hasTiming: false },
  { id: 'slideLeft', label: 'Slide from left', hint: 'Slides in from the left edge.', strength: { label: 'Distance', defaultPercent: 100, min: 10, max: 200 }, hasDuration: true, hasTiming: true },
  { id: 'slideRight', label: 'Slide from right', hint: 'Slides in from the right edge.', strength: { label: 'Distance', defaultPercent: 100, min: 10, max: 200 }, hasDuration: true, hasTiming: true },
  { id: 'slideTop', label: 'Slide from top', hint: 'Slides in from the top edge.', strength: { label: 'Distance', defaultPercent: 100, min: 10, max: 200 }, hasDuration: true, hasTiming: true },
  { id: 'slideBottom', label: 'Slide from bottom', hint: 'Slides in from the bottom edge.', strength: { label: 'Distance', defaultPercent: 100, min: 10, max: 200 }, hasDuration: true, hasTiming: true },
  { id: 'fade', label: 'Fade', hint: 'Fades the picture in or out.', strength: null, hasDuration: true, hasTiming: true },
  { id: 'kenBurns', label: 'Ken Burns', hint: 'A slow zoom and drift across the whole clip.', strength: { label: 'How much', defaultPercent: 15, min: 5, max: 60 }, hasDuration: false, hasTiming: false },
];

export function presetInfo(id: MotionPresetId): MotionPresetInfo {
  return MOTION_PRESETS.find((p) => p.id === id) as MotionPresetInfo;
}

export interface MotionOptions {
  /** Percent, see {@link MotionPresetInfo.strength}. Defaults to the preset's own. */
  strengthPercent?: number;
  /** Seconds the motion takes (slides, zoom, pop, fade). Defaults to 1 s (0.6 s for slide, pop and fade). */
  seconds?: number;
  timing?: MotionTiming;
}

/** The five transform channels a preset may write; all of them are cleared first. */
const CHANNELS: readonly KeyframeParam[] = ['positionX', 'positionY', 'scale', 'rotation', 'opacity'];

interface Key {
  param: KeyframeParam;
  frame: number;
  value: number;
  interp: 'linear' | 'easeInQuad' | 'easeOutQuad' | 'easeInOutQuad' | 'easeOutCubic' | 'easeInOutCubic';
}

const clampValue = (param: KeyframeParam, value: number): number => {
  const bounds: Record<string, [number, number]> = { positionX: [-2, 2], positionY: [-2, 2], scale: [0.01, 8], rotation: [-360, 360], opacity: [0, 1] };
  const [lo, hi] = bounds[param] ?? [-Infinity, Infinity];
  return Math.min(hi, Math.max(lo, Number(value.toFixed(4))));
};

/** The keys a preset writes, in time order per channel. Pure; exported so tests assert exact values. */
export function motionKeys(id: MotionPresetId, clip: Pick<MediaClip, 'recordIn' | 'recordOut'>, rate: Rational, options: MotionOptions = {}): Key[] {
  const info = presetInfo(id);
  const last = clip.recordOut - 1;
  const length = clip.recordOut - clip.recordIn;
  const strength = (options.strengthPercent ?? info.strength?.defaultPercent ?? 0) / 100;
  const defaultSeconds = id === 'zoomIn' || id === 'zoomOut' ? 1 : 0.6;
  const wanted = info.hasDuration ? secondsToFrames(options.seconds ?? defaultSeconds, rate) : length - 1;
  const span = Math.max(1, Math.min(wanted, length - 1));
  const timing: MotionTiming = info.hasTiming ? (options.timing ?? 'start') : 'start';
  const keys: Key[] = [];
  const key = (param: KeyframeParam, frame: number, value: number, interp: Key['interp']) => keys.push({ param, frame, value: clampValue(param, value), interp });
  /** An entrance runs from the clip's first frame; an exit ends on its last frame. */
  const window = timing === 'start' ? { from: clip.recordIn, to: clip.recordIn + span } : { from: last - span, to: last };
  /** `rest` at the near end, `away` at the far end of the motion; an exit reverses them. */
  const between = (param: KeyframeParam, away: number, rest: number, interp: Key['interp']) => {
    if (timing === 'start') {
      key(param, window.from, away, interp);
      key(param, window.to, rest, interp);
    } else {
      key(param, window.from, rest, interp);
      key(param, window.to, away, interp);
    }
  };
  switch (id) {
    case 'zoomIn':
      key('scale', clip.recordIn, 1, 'easeInOutQuad');
      key('scale', clip.recordIn + span, 1 + strength, 'easeInOutQuad');
      break;
    case 'zoomOut':
      key('scale', clip.recordIn, 1 + strength, 'easeInOutQuad');
      key('scale', clip.recordIn + span, 1, 'easeInOutQuad');
      break;
    case 'pop': {
      const mid = clip.recordIn + Math.max(1, Math.round(span * 0.7));
      key('scale', clip.recordIn, 1 - strength, 'easeOutCubic');
      key('scale', mid, 1 + strength * 0.25, 'easeInOutQuad');
      key('scale', Math.max(mid + 1, clip.recordIn + span), 1, 'easeOutQuad');
      key('opacity', clip.recordIn, 0, 'easeOutQuad');
      key('opacity', clip.recordIn + Math.max(1, Math.round(span * 0.4)), 1, 'easeOutQuad');
      break;
    }
    case 'slideLeft':
      between('positionX', -strength, 0, 'easeOutCubic');
      break;
    case 'slideRight':
      between('positionX', strength, 0, 'easeOutCubic');
      break;
    case 'slideTop':
      between('positionY', -strength, 0, 'easeOutCubic');
      break;
    case 'slideBottom':
      between('positionY', strength, 0, 'easeOutCubic');
      break;
    case 'fade':
      between('opacity', 0, 1, 'linear');
      break;
    case 'kenBurns':
      key('scale', clip.recordIn, 1, 'easeInOutQuad');
      key('scale', last, 1 + strength, 'easeInOutQuad');
      key('positionX', clip.recordIn, 0, 'easeInOutQuad');
      key('positionX', last, -strength * 0.25, 'easeInOutQuad');
      key('positionY', clip.recordIn, 0, 'easeInOutQuad');
      key('positionY', last, -strength * 0.1, 'easeInOutQuad');
      break;
  }
  // Keys land on distinct frames per channel (a one-frame clip cannot be animated).
  return keys.filter((k, i) => keys.findIndex((o) => o.param === k.param && o.frame === k.frame) === i);
}

/** True when the clip is long enough to animate (at least two frames). */
export function canAnimate(clip: Pick<MediaClip, 'recordIn' | 'recordOut'>): boolean {
  return clip.recordOut - clip.recordIn >= 2;
}

/** The keys a clip already carries, per transform channel. */
function existingKeys(clip: MediaClip): { param: KeyframeParam; frame: number }[] {
  const out: { param: KeyframeParam; frame: number }[] = [];
  for (const param of CHANNELS) {
    const channel = clip.transform?.[param as keyof NonNullable<MediaClip['transform']>];
    for (const k of channel?.keys ?? []) out.push({ param, frame: k.frame });
  }
  return out;
}

/** True when any transform channel of the clip has a key (the "animated" state). */
export function isAnimated(clip: MediaClip): boolean {
  return existingKeys(clip).length > 0;
}

/** The ops that remove every key on the clip's transform channels (empty when it is not animated). */
export function clearMotionOps(clip: MediaClip): EditOp[] {
  return existingKeys(clip).map((k): EditOp => ({ type: 'removeKeyframe', target: { itemId: clip.id }, param: k.param, frame: k.frame }));
}

/**
 * The batch that applies a preset: clear the old motion, then write the new keys. Apply it with
 * `dispatchBatch`, so it is one undo step. Empty when the clip is too short to animate.
 */
export function motionOps(id: MotionPresetId, clip: MediaClip, rate: Rational, options: MotionOptions = {}): EditOp[] {
  if (!canAnimate(clip)) return [];
  const writes = motionKeys(id, clip, rate, options).map((k): EditOp => ({ type: 'setKeyframe', target: { itemId: clip.id }, param: k.param, frame: k.frame, value: k.value, interp: k.interp }));
  return [...clearMotionOps(clip), ...writes];
}
