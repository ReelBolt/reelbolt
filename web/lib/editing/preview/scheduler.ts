/**
 * The compositor's planning functions — pure, over the document.
 *
 *   - `activeLayersAt(doc, frame, flags)` — the video items on screen at a timeline frame, top track
 *     first (the document lists tracks in compositing order; the LAST video track is the top layer,
 *     matching "V tracks top→bottom" — so it is reversed here into draw order back-to-front).
 *   - `sourceTimeSec(clip, frame, rate)` — where in the SOURCE a timeline frame lands, honouring
 *     `sourceIn` and `playbackRate` (the latter is render-required for parity, but the scheduler
 *     still has to put SOME frame on screen, and the right frame is the retimed one).
 *   - `evaluateKeyframed(value, frame)` — a `Keyframed<number>` at a frame with the contract's
 *     interpolation kinds. Used for gain envelopes in P3 and for every numeric parameter in P5.
 *   - `activeAudioAt(doc, frame, flags)` — audio items and their gains, honouring mute/solo.
 */

import type { ClipCrop, EditTimeline, InterpKind, Item, Keyframed, MediaClip, Rational, Track, Transition } from '../types';
import { xfadeIndexOf } from './xfade';

export interface TrackFlags {
  enabled: boolean;
  mute: boolean;
  solo: boolean;
  locked: boolean;
}

export type FlagsFor = (track: Track) => TrackFlags;

export const documentFlags: FlagsFor = (track) => ({
  enabled: track.enabled,
  mute: track.mute,
  solo: track.solo,
  locked: track.locked,
});

export interface ActiveLayer {
  track: Track;
  item: Item;
  /** 0 = bottom-most layer in draw order. */
  layerIndex: number;
  /**
   * P5: the layer's opacity from its place in a transition (the incoming clip ramps 0 → 1; the
   * outgoing one stays at 1 underneath). 1 for every layer outside a transition. A clip's own
   * transform/fade opacity is separate — see {@link layerTransformAt}.
   */
  opacity: number;
  /**
   * T-008b: set on both legs of a transition whose word has an `xfade` kernel (every word but the plain `fade`, which the
   * opacity ramp already is). The compositor mixes the legs with the kernel; `t` is the progress 0..1 after easing.
   */
  xfade?: { word: string; t: number; role: 'from' | 'to' };
}

/** P5: the evaluated picture transform of a media clip at one frame, as the compositor draws it. */
export interface LayerTransform {
  /** Offset of the clip centre from the canvas centre, fractions of canvas width/height. */
  x: number;
  y: number;
  /** Scale relative to the fitted size (horizontal when `scaleY` is set, else uniform). */
  scale: number;
  /** Vertical scale; absent means the same as `scale`. */
  scaleY?: number;
  /** Degrees clockwise. */
  rotation: number;
  /** Transform opacity × fade-in ramp × fade-out ramp. */
  opacity: number;
  crop: Required<ClipCrop>;
}

export const IDENTITY_TRANSFORM: LayerTransform = Object.freeze({
  x: 0,
  y: 0,
  scale: 1,
  rotation: 0,
  opacity: 1,
  crop: Object.freeze({ left: 0, top: 0, right: 0, bottom: 0 }),
});

/** True when drawing the clip needs anything beyond the P3 full-canvas quad. */
export function hasTransform(t: LayerTransform): boolean {
  return t.x !== 0 || t.y !== 0 || t.scale !== 1 || (t.scaleY !== undefined && t.scaleY !== 1) || t.rotation !== 0 || t.crop.left !== 0 || t.crop.top !== 0 || t.crop.right !== 0 || t.crop.bottom !== 0;
}

/**
 * The transform of a media clip at `frame`: every keyframed channel evaluated (with the same
 * interpolation the renderer compiles), the crop clamped to a legal picture, and the clip's fades
 * folded into opacity. Identity for a clip with none of it.
 */
export function layerTransformAt(clip: MediaClip, frame: number): LayerTransform {
  const t = clip.transform ?? null;
  const fadeIn = typeof clip.fadeInFrames === 'number' && clip.fadeInFrames > 0 ? clip.fadeInFrames : 0;
  const fadeOut = typeof clip.fadeOutFrames === 'number' && clip.fadeOutFrames > 0 ? clip.fadeOutFrames : 0;
  if (!t && !clip.crop && fadeIn === 0 && fadeOut === 0) return IDENTITY_TRANSFORM;

  const scaleRaw = evaluateKeyframed(t?.scale ?? null, frame, 1);
  const scaleYRaw = t?.scaleY ? evaluateKeyframed(t.scaleY, frame, 1) : undefined;
  let opacity = clamp01(evaluateKeyframed(t?.opacity ?? null, frame, 1));
  if (fadeIn > 0) opacity *= clamp01((frame - clip.recordIn + 1) / fadeIn);
  if (fadeOut > 0) opacity *= clamp01((clip.recordOut - frame) / fadeOut);

  const crop = clip.crop ?? null;
  const edge = (v: number | undefined) => (typeof v === 'number' && Number.isFinite(v) && v > 0 && v < 1 ? v : 0);
  let left = edge(crop?.left);
  let right = edge(crop?.right);
  let top = edge(crop?.top);
  let bottom = edge(crop?.bottom);
  if (left + right >= 1) left = right = 0;
  if (top + bottom >= 1) top = bottom = 0;

  return {
    x: evaluateKeyframed(t?.positionX ?? null, frame, 0),
    y: evaluateKeyframed(t?.positionY ?? null, frame, 0),
    scale: scaleRaw > 0 ? scaleRaw : 1,
    ...(scaleYRaw !== undefined ? { scaleY: scaleYRaw > 0 ? scaleYRaw : scaleRaw > 0 ? scaleRaw : 1 } : {}),
    rotation: evaluateKeyframed(t?.rotation ?? null, frame, 0),
    opacity,
    crop: { left, top, right, bottom },
  };
}

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

function isEnabled(item: Item): boolean {
  const enabled = (item as { enabled?: boolean }).enabled;
  return enabled !== false;
}

/**
 * Video items on screen at `frame`, in back-to-front draw order (first video track at the bottom).
 *
 * P5: a transition between two enabled media clips contributes BOTH clips — the outgoing one
 * (continuing past its own end) underneath at full opacity and the incoming one (pulled back before
 * its start) on top, ramped by the transition's easing — so the preview shows a dissolve where the
 * render will apply the chosen `xfade`. Any other transition shows nothing, like the renderer skips it.
 */
export function activeLayersAt(doc: EditTimeline, frame: number, flags: FlagsFor = documentFlags): ActiveLayer[] {
  const layers: ActiveLayer[] = [];
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    if (!flags(track).enabled) continue;
    for (let i = 0; i < track.items.length; i++) {
      const item = track.items[i];
      if (!(item.recordIn <= frame && frame < item.recordOut) || !isEnabled(item)) continue;
      if (item.type === 'transition') {
        const from = track.items[i - 1];
        const to = track.items[i + 1];
        if (from?.type === 'mediaClip' && to?.type === 'mediaClip' && isEnabled(from) && isEnabled(to) && from.recordOut === item.recordIn && to.recordIn === item.recordOut) {
          const t = transitionProgress(item, frame);
          const word = String(item.transitionType);
          if (word !== 'fade' && xfadeIndexOf(word) >= 0) {
            layers.push({ track, item: from, layerIndex: layers.length, opacity: 1, xfade: { word, t, role: 'from' } });
            layers.push({ track, item: to, layerIndex: layers.length, opacity: 1, xfade: { word, t, role: 'to' } });
          } else {
            layers.push({ track, item: from, layerIndex: layers.length, opacity: 1 });
            layers.push({ track, item: to, layerIndex: layers.length, opacity: t });
          }
        }
        break;
      }
      layers.push({ track, item, layerIndex: layers.length, opacity: 1 });
      break; // items on a track never overlap, so at most one is active
    }
  }
  return layers;
}

/** The incoming clip's share of a transition at `frame`, through the transition's easing word. */
export function transitionProgress(transition: Transition, frame: number): number {
  const span = transition.recordOut - transition.recordIn;
  if (span <= 0) return 1;
  const u = (frame - transition.recordIn + 1) / span;
  const easing = (transition.easing ?? 'linear') as InterpKind;
  return easing === 'step' ? (u >= 1 ? 1 : 0) : ease(easing, u);
}

export function fpsOf(rate: Rational): number {
  if (!rate || rate.den === 0) return 30;
  const fps = rate.num / rate.den;
  return Number.isFinite(fps) && fps > 0 ? fps : 30;
}

/**
 * The source position (in source frames at the timeline rate, fractional) a clip reads at timeline
 * `frame` — the one formula the renderer's `SegmentPlanner.SourceFrameAt` mirrors: forwards from
 * `sourceIn`; reversed (P5), counted back from the clip's end so `sourceIn` stays the lowest frame
 * touched; held (P5), always `sourceIn`. Frames outside the clip extrapolate linearly (a transition
 * leg reads past the clip's own span), clamped at source frame 0.
 */
export function sourcePositionFor(clip: MediaClip, frame: number): number {
  const sourceIn = typeof clip.sourceIn === 'number' ? clip.sourceIn : 0;
  const playbackRate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  if (clip.hold === true) return sourceIn;
  const offset = clip.reverse === true ? (clip.recordOut - frame) * playbackRate : (frame - clip.recordIn) * playbackRate;
  return Math.max(0, sourceIn + offset);
}

/** Seconds into the source where timeline `frame` lands for a media clip. */
export function sourceTimeSec(clip: MediaClip, frame: number, rate: Rational): number {
  return sourcePositionFor(clip, frame) / fpsOf(rate);
}

/** The source frame index (integer) for a timeline frame. */
export function sourceFrameFor(clip: MediaClip, frame: number): number {
  return Math.floor(sourcePositionFor(clip, frame));
}

// ─────────────────────────────────────────────────────────────────────────────
// Keyframe evaluation
// ─────────────────────────────────────────────────────────────────────────────

export function ease(kind: InterpKind, t: number): number {
  const u = Math.min(1, Math.max(0, t));
  switch (kind) {
    case 'linear':
      return u;
    case 'easeInQuad':
      return u * u;
    case 'easeOutQuad':
      return 1 - (1 - u) * (1 - u);
    case 'easeInOutQuad':
      return u < 0.5 ? 2 * u * u : 1 - (-2 * u + 2) ** 2 / 2;
    case 'easeInCubic':
      return u * u * u;
    case 'easeOutCubic':
      return 1 - (1 - u) ** 3;
    case 'easeInOutCubic':
      return u < 0.5 ? 4 * u * u * u : 1 - (-2 * u + 2) ** 3 / 2;
    case 'step':
      return 0;
    default:
      return u;
  }
}

/**
 * The value of a `Keyframed<number>` at `frame`: the base `value` before the first key (or when
 * there are no keys), the last key's value after the last key, and the interpolation of the
 * surrounding pair in between using the EARLIER key's `interp`. Keys are assumed frame-sorted.
 */
export function evaluateKeyframed(value: Keyframed<number> | null | undefined, frame: number, fallback = 0): number {
  if (!value) return fallback;
  const base = typeof value.value === 'number' ? value.value : fallback;
  const keys = value.keys;
  if (!keys || keys.length === 0) return base;
  if (frame < keys[0].frame) return base;
  for (let i = 0; i < keys.length - 1; i++) {
    const a = keys[i];
    const b = keys[i + 1];
    if (frame >= a.frame && frame < b.frame) {
      const span = b.frame - a.frame;
      const t = span <= 0 ? 1 : (frame - a.frame) / span;
      return a.value + (b.value - a.value) * ease(a.interp, t);
    }
  }
  return keys[keys.length - 1].value;
}

export function dbToGain(db: number): number {
  return 10 ** (db / 20);
}

export interface ActiveAudio {
  track: Track;
  clip: MediaClip;
  /** Linear gain after the clip's gain envelope and the track's mute/solo. */
  gain: number;
}

/** Audio clips sounding at `frame` with their effective linear gain (mute/solo applied). */
export function activeAudioAt(doc: EditTimeline, frame: number, flags: FlagsFor = documentFlags): ActiveAudio[] {
  const audioTracks = doc.tracks.filter((t) => t.kind === 'audio');
  const anySolo = audioTracks.some((t) => flags(t).solo);
  const out: ActiveAudio[] = [];
  for (const track of audioTracks) {
    const f = flags(track);
    const audible = f.enabled && !f.mute && (!anySolo || f.solo);
    const trackGain = dbToGain(evaluateKeyframed(track.gainDb ?? null, frame, 0));
    for (const item of track.items) {
      if (item.type !== 'mediaClip') continue;
      if (!(item.recordIn <= frame && frame < item.recordOut) || !isEnabled(item)) continue;
      if (item.hold === true) continue; // a freeze frame is silent
      const clipGainDb = evaluateKeyframed(item.gainDb ?? null, frame, 0);
      out.push({ track, clip: item, gain: audible ? dbToGain(clipGainDb) * clipFadeGain(item, frame) * trackGain : 0 });
    }
  }
  return out;
}

/** P5: the clip's fade-in × fade-out ramp at `frame` (1 outside both ramps). */
export function clipFadeGain(clip: MediaClip, frame: number): number {
  let gain = 1;
  const fadeIn = typeof clip.fadeInFrames === 'number' && clip.fadeInFrames > 0 ? clip.fadeInFrames : 0;
  const fadeOut = typeof clip.fadeOutFrames === 'number' && clip.fadeOutFrames > 0 ? clip.fadeOutFrames : 0;
  if (fadeIn > 0) gain *= clamp01((frame - clip.recordIn + 1) / fadeIn);
  if (fadeOut > 0) gain *= clamp01((clip.recordOut - frame) / fadeOut);
  return gain;
}

/** Every distinct asset the document references — what the preview needs proxies for. */
export function referencedAssets(doc: EditTimeline): { kind: string; id: string }[] {
  const seen = new Set<string>();
  const out: { kind: string; id: string }[] = [];
  for (const track of doc.tracks) {
    for (const item of track.items) {
      const ref = (item as { assetRef?: { kind: string; projectFileId?: string | null; stepResultId?: string | null } | null }).assetRef;
      if (!ref) continue;
      const id = ref.kind === 'projectFile' ? ref.projectFileId : ref.stepResultId;
      if (!id) continue;
      const key = `${ref.kind}:${id}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ kind: ref.kind, id });
    }
  }
  return out;
}

/** The `mediaRef` token the media API addresses an asset by. */
export function mediaRefOf(ref: { kind: string; projectFileId?: string | null; stepResultId?: string | null }): string | null {
  if (ref.kind === 'projectFile' && ref.projectFileId) return `file:${ref.projectFileId}`;
  if (ref.kind === 'stepResult' && ref.stepResultId) return `step_result:${ref.stepResultId}`;
  return null;
}
