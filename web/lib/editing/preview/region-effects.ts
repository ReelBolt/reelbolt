/**
 * Live preview of prompted region effects (magic blur and friends): the render's parameters, mirrored from
 * `RegionEffectFilterBuilder.cs` (strengths are fractions of the SOURCE short side), plus the pluggable matte source.
 * Pure, so the compositor, the session and the tests share one reading of each rule.
 */

import type { RegionClipEffect, RegionEffectType, SegmentationMask } from '../types';
import { matteFor, type Matte, type SegmentationTrack, type SegmentationTracksLookup } from './segmentation-tracks';

/** `EditTimelineValidator.MaxRegionEffects`. */
export const MAX_REGION_EFFECTS = 4;
/** The mask-video path decodes one video per track; more tracks than this fall back to boxes. */
export const MAX_MASK_VIDEO_TRACKS = 4;

// RegionEffectFilterBuilder constants (RegionEffectFilterBuilder.cs:46-83).
export const BLUR_SIGMA_FRACTION = 0.015;
export const PIXELATE_BLOCK_FRACTION = 0.02;
export const HIGHLIGHT_GAIN = 0.1;
export const HIGHLIGHT_CONTRAST = 0.1;
export const HIGHLIGHT_DIM = 0.15;
export const SPOTLIGHT_DIM = 0.6;
export const OUTLINE_BAND_FRACTION = 0.004;
export const MAX_OUTLINE_BAND_PX = 16;
export const MIN_PIXELATE_BLOCK_PX = 2;
export const MIN_BLUR_SIGMA = 0.1;

/** .NET `Math.Round(double)`: halves go to the even neighbour. */
export function roundHalfEven(v: number): number {
  const f = Math.floor(v);
  const d = v - f;
  if (d < 0.5) return f;
  if (d > 0.5) return f + 1;
  return f % 2 === 0 ? f : f + 1;
}

/** The render clamps a strength to 0..2 (a non-finite one is 1). */
export function regionStrength(s: number): number {
  return Number.isFinite(s) ? Math.min(2, Math.max(0, s)) : 1;
}

/** Blur sigma in source pixels (`BlurSigma`): 1.5% of the short side at strength 1, never below 0.1, 2 decimals. */
export function blurSigmaPx(shortSide: number, strength: number): number {
  return Math.round(Math.max(MIN_BLUR_SIGMA, shortSide * BLUR_SIGMA_FRACTION * regionStrength(strength)) * 100) / 100;
}

/** Mosaic block edge in source pixels (`PixelBlock`): 2% of the short side, clamped to [2, short/2]. */
export function pixelBlockPx(shortSide: number, strength: number): number {
  const hi = Math.max(MIN_PIXELATE_BLOCK_PX, Math.floor(shortSide / 2));
  return Math.min(hi, Math.max(MIN_PIXELATE_BLOCK_PX, roundHalfEven(shortSide * PIXELATE_BLOCK_FRACTION * regionStrength(strength))));
}

/** Outline ring width in source pixels (`OutlineBand`): 0.4% of the short side, 1..16. */
export function outlineBandPx(shortSide: number, strength: number): number {
  return Math.min(MAX_OUTLINE_BAND_PX, Math.max(1, roundHalfEven(shortSide * OUTLINE_BAND_FRACTION * regionStrength(strength))));
}

/** The brightness shifts a highlight/spotlight applies (eq filter values, `Math.Round(.., 4)`). */
export function dimmingOf(effect: string, strength: number): { gain: number; contrast: number; dim: number } {
  const s = regionStrength(strength);
  const r4 = (v: number) => Math.round(v * 1e4) / 1e4;
  if (effect === 'highlight') return { gain: r4(HIGHLIGHT_GAIN * s), contrast: r4(1 + HIGHLIGHT_CONTRAST * s), dim: r4(HIGHLIGHT_DIM * s) };
  if (effect === 'spotlight') return { gain: 0, contrast: 1, dim: r4(SPOTLIGHT_DIM * s) };
  return { gain: 0, contrast: 1, dim: 0 };
}

/** Where the blur runs: the sigma the 12-tap kernel can hold is <= 4 working pixels, so big blurs run on a smaller copy. */
export const REGION_BLUR_MAX_SIGMA_PX = 4;
export const REGION_BLUR_MIN_SHORT = 64;

export type RegionBlurPlan = { width: number; height: number; sigma: number };

/** The working size and sigma (in working pixels) of a region blur on a `srcW` x `srcH` picture. */
export function planRegionBlur(srcW: number, srcH: number, strength: number): RegionBlurPlan {
  const short = Math.max(1, Math.min(srcW, srcH));
  const s = Math.max(regionStrength(strength), 1e-6);
  const wantShort = Math.max(REGION_BLUR_MIN_SHORT, Math.round(REGION_BLUR_MAX_SIGMA_PX / (BLUR_SIGMA_FRACTION * s)));
  const scale = Math.min(1, wantShort / short);
  const width = Math.max(1, Math.round(srcW * scale));
  const height = Math.max(1, Math.round(srcH * scale));
  const sigma = Math.max(MIN_BLUR_SIGMA * scale, Math.min(width, height) * BLUR_SIGMA_FRACTION * s);
  return { width, height, sigma };
}

// ─────────────────────────────────────────────────────────────────────────────
// Matte source: boxes | maskVideo
// ─────────────────────────────────────────────────────────────────────────────

export type MatteSourceKind = 'boxes' | 'maskVideo';

/** One decoded luma frame of a track's mask preview video (white = the object, FULL range), spanning the whole source frame. */
export type MaskFrame = { image: CanvasImageSource };

/**
 * What the compositor reads one entry's matte from this frame. `boxes` is the tracked-box matte that always exists;
 * `maskVideo` carries one decoded luma frame per named track (the union is taken in the shader).
 */
export type RegionMatteSource =
  | { kind: 'boxes'; matte: Matte }
  | { kind: 'maskVideo'; frames: readonly MaskFrame[]; invert: boolean; featherPx: number; sourceWidth?: number };

/** One region effect as the compositor draws it. */
/** `cutout` is not a render effect: the clip's own object mask (`clip.mask`) drawn as an alpha matte through the same sources. */
export type PreviewRegionEffect = { effect: RegionEffectType | 'cutout'; strength: number; matte: RegionMatteSource; /** Identity of the frames/boxes for caching. */ key: string };

/** The clip's object mask as a cutout through a decoded mask video (the box matte keeps the other case). */
export function cutoutEffectOf(mask: SegmentationMask, frames: readonly MaskFrame[]): PreviewRegionEffect {
  return { effect: 'cutout', strength: 1, key: 'cut', matte: { kind: 'maskVideo', frames, invert: mask.invert === true, featherPx: Math.min(64, Math.max(0, Number.isFinite(mask.featherPx) ? mask.featherPx : 0)) } };
}

/** The mask-preview wire state of a track (`maskPreview.state`; absent from an older API = none). */
export function maskPreviewState(track: SegmentationTrack | undefined): 'ready' | 'pending' | 'none' {
  const s = track?.maskPreview?.state;
  return s === 'ready' || s === 'pending' ? s : 'none';
}

/** The tracks `mask` names, from the lookup, in the mask's order. */
function namedTracks(mask: SegmentationMask, lookup: SegmentationTracksLookup | undefined): SegmentationTrack[] | null {
  const tracks = lookup?.(mask.analysisStepResultId);
  if (!tracks) return null;
  const out: SegmentationTrack[] = [];
  for (const id of mask.trackIds) {
    const t = tracks.find((x) => x.trackId === id);
    if (!t) return null;
    out.push(t);
  }
  return out;
}

/** The kind of matte a mask can use now: `maskVideo` only when EVERY named track's preview is ready (and few enough), else `boxes`. */
export function matteSourceKindFor(mask: SegmentationMask, lookup: SegmentationTracksLookup | undefined, hasUrl: boolean): MatteSourceKind {
  const tracks = namedTracks(mask, lookup);
  if (!hasUrl || !tracks || tracks.length === 0 || tracks.length > MAX_MASK_VIDEO_TRACKS) return 'boxes';
  return tracks.every((t) => maskPreviewState(t) === 'ready') ? 'maskVideo' : 'boxes';
}

/** The tracks whose mask preview is still being made (the session polls these), for entries/masks on screen. */
export function pendingPreviewTracks(mask: SegmentationMask, lookup: SegmentationTracksLookup | undefined): string[] {
  const tracks = namedTracks(mask, lookup) ?? [];
  return tracks.filter((t) => maskPreviewState(t) === 'pending').map((t) => t.trackId);
}

/**
 * The region effects to draw for a clip at `sourceSec`: the enabled entries (first 4, document order), each with the best
 * matte it has - `maskFramesFor` supplies decoded frames (null while not decoded or no mask video), and an entry without
 * them uses boxes. An entry with neither has no matte yet: it is listed in `unready` and not drawn.
 */
export function previewRegionEffectsOf(
  effects: readonly RegionClipEffect[] | null | undefined,
  lookup: SegmentationTracksLookup | undefined,
  sourceSec: number,
  maskFramesFor?: (entryIndex: number, mask: SegmentationMask) => readonly MaskFrame[] | null,
): { drawn: PreviewRegionEffect[]; unready: number } {
  const drawn: PreviewRegionEffect[] = [];
  let unready = 0;
  let index = -1;
  for (const e of effects ?? []) {
    index += 1;
    if (!e || e.enabled !== true) continue;
    if (drawn.length + unready >= MAX_REGION_EFFECTS) break;
    const strength = regionStrength(e.strength);
    const frames = maskFramesFor?.(index, e.mask) ?? null;
    if (frames && frames.length > 0) {
      drawn.push({ effect: e.effect, strength, key: `v${index}`, matte: { kind: 'maskVideo', frames, invert: e.mask.invert === true, featherPx: Math.min(64, Math.max(0, Number.isFinite(e.mask.featherPx) ? e.mask.featherPx : 0)) } });
      continue;
    }
    const matte = matteFor(e.mask, lookup, sourceSec);
    if (matte) drawn.push({ effect: e.effect, strength, key: `b${index}`, matte: { kind: 'boxes', matte } });
    else unready += 1;
  }
  return { drawn, unready };
}
