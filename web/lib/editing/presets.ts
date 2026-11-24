/**
 * P9: the presets that ship with the editor. Mastering targets are the three integrated-loudness
 * conventions the plan names — social (−14 LUFS, the level the large streaming/social platforms
 * normalise to), podcast (−16 LUFS, the common spoken-word target) and broadcast (−23 LUFS, EBU R128)
 * — each with a −1 dBTP true-peak ceiling. They feed the render's two-pass `loudnorm` through the
 * same `loudness` block the Deliver form already sends, so no new renderer path exists for them.
 */

import type { MasteringPresetPayload } from './api/editor-p9';

export const MASTERING_PRESETS: readonly { id: string; name: string; payload: MasteringPresetPayload }[] = Object.freeze([
  { id: 'social', name: 'Social / streaming (−14 LUFS)', payload: { targetLufs: -14, truePeakDbtp: -1, loudnessRange: 11 } },
  { id: 'podcast', name: 'Podcast (−16 LUFS)', payload: { targetLufs: -16, truePeakDbtp: -1, loudnessRange: 11 } },
  { id: 'broadcast', name: 'Broadcast EBU R128 (−23 LUFS)', payload: { targetLufs: -23, truePeakDbtp: -1, loudnessRange: 15 } },
]);

/** Clamps a mastering payload into the render config's ranges (−36..−5 LUFS, −9..0 dBTP, 1..30 LU). */
export function clampMastering(p: MasteringPresetPayload): MasteringPresetPayload {
  const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
  return { targetLufs: clamp(p.targetLufs, -36, -5), truePeakDbtp: clamp(p.truePeakDbtp, -9, 0), loudnessRange: clamp(p.loudnessRange ?? 11, 1, 30) };
}
