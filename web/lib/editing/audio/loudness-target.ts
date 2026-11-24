/**
 * R08.4: loudness against a target. The chips name where the film will be heard; "Match target"
 * works out how many dB the whole mix must move and spreads it over the audio tracks' faders
 * (the document has no separate master gain; every fader keeps its own balance).
 */

import { msg, type Msg } from '@/i18n/msg';
import type { EditOp } from '../engine';
import type { EditTimeline, Keyframed } from '../types';
import { FADER_MAX_DB, FADER_MIN_DB } from './fader';

export interface LoudnessTarget {
  id: 'social' | 'podcast' | 'broadcast';
  label: string;
  lufs: number;
  hint: string;
}

export const LOUDNESS_TARGETS: readonly LoudnessTarget[] = [
  { id: 'social', label: 'Social', lufs: -14, hint: 'Instagram, TikTok, YouTube' },
  { id: 'podcast', label: 'Podcast', lufs: -16, hint: 'Podcasts and talks' },
  { id: 'broadcast', label: 'Broadcast', lufs: -23, hint: 'TV and radio' },
];

export const DEFAULT_TARGET: LoudnessTarget = LOUDNESS_TARGETS[0];
/** A measurement within this many LU of the target counts as on target. */
export const TARGET_TOLERANCE_LU = 1;

export const targetById = (id: string | null | undefined): LoudnessTarget => LOUDNESS_TARGETS.find((t) => t.id === id) ?? DEFAULT_TARGET;

export type TargetVerdict = { kind: 'unmeasurable' } | { kind: 'on'; delta: number } | { kind: 'loud'; delta: number } | { kind: 'quiet'; delta: number };

/** Where a measurement sits against the target; delta is target - measured (the dB the mix must move). */
export function judgeLoudness(measuredLufs: number | null | undefined, targetLufs: number): TargetVerdict {
  if (measuredLufs === null || measuredLufs === undefined || !Number.isFinite(measuredLufs) || measuredLufs <= -69) return { kind: 'unmeasurable' };
  const delta = Math.round((targetLufs - measuredLufs) * 10) / 10;
  if (Math.abs(delta) <= TARGET_TOLERANCE_LU) return { kind: 'on', delta };
  return delta < 0 ? { kind: 'loud', delta } : { kind: 'quiet', delta };
}

/** Same verdicts as `describeVerdict`, as catalogue messages for the UI (the English text lives in editor-audio.json). */
export function verdictMsg(v: TargetVerdict, targetLabel: string): Msg {
  switch (v.kind) {
    case 'unmeasurable':
      return msg('editor-audio.loudness.verdict.unmeasurable');
    case 'on':
      return msg('editor-audio.loudness.verdict.on', { target: targetLabel });
    case 'loud':
      return msg('editor-audio.loudness.verdict.loud', { db: Number(Math.abs(v.delta).toFixed(1)), target: targetLabel });
    case 'quiet':
      return msg('editor-audio.loudness.verdict.quiet', { db: Number(v.delta.toFixed(1)), target: targetLabel });
  }
}

export function describeVerdict(v: TargetVerdict, targetLabel: string): string {
  switch (v.kind) {
    case 'unmeasurable':
      return 'Nothing audible to measure yet.';
    case 'on':
      return `On target for ${targetLabel}.`;
    case 'loud':
      return `${Math.abs(v.delta).toFixed(1)} dB too loud for ${targetLabel}.`;
    case 'quiet':
      return `${v.delta.toFixed(1)} dB too quiet for ${targetLabel}.`;
  }
}

const clamp = (v: number) => Math.min(FADER_MAX_DB, Math.max(FADER_MIN_DB, Math.round(v * 100) / 100));

const shift = (g: Keyframed<number> | null | undefined, delta: number): Keyframed<number> => {
  const base = typeof g?.value === 'number' ? g.value : 0;
  return { value: clamp(base + delta), keys: g?.keys ? g.keys.map((k) => ({ ...k, value: clamp(k.value + delta) })) : null };
};

/** The ops that move every unmuted audio track's fader by `deltaDb` (one batch = one undo step). */
export function compileMatchTarget(doc: EditTimeline, deltaDb: number): EditOp[] {
  return doc.tracks
    .filter((t) => t.kind === 'audio' && !t.mute)
    .map((t): EditOp => ({ type: 'setTrackProperty', trackId: t.id, key: 'gainDb', value: shift(t.gainDb, deltaDb) }));
}
