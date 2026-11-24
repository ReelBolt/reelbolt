/**
 * R07.3: plain-unit clip controls. The document stores opacity as 0..1, fades as frames, speed as a
 * multiplier and gain in dB; the inspector shows percent, seconds, a multiplier and a volume
 * percent. These helpers convert both ways and check a typed value, answering a refusal with a plain
 * sentence (never a silent clamp). Pure, no React.
 */
import type { Rational } from '../types';

export type Checked<T> = { ok: true; value: T } | { ok: false; sentence: string };

/** The quietest gain the document accepts (a clip at this gain is, for every practical purpose, muted). */
export const MUTE_DB = -60;
/** The loudest volume the plain control offers (percent); 200% is +6 dB. */
export const MAX_VOLUME_PERCENT = 200;
export const SPEED_MIN = 0.1;
export const SPEED_MAX = 10;
/** The speed chips, in order. */
export const SPEED_CHIPS: readonly number[] = [0.25, 0.5, 1, 1.5, 2, 4];

const fps = (rate: Rational): number => rate.num / rate.den;

/** `0.4` shown as `40`. */
export function opacityToPercent(opacity: number): number {
  return Math.round(opacity * 100);
}

export function checkOpacityPercent(percent: number): Checked<number> {
  if (!Number.isFinite(percent) || percent < 0 || percent > 100) {
    return { ok: false, sentence: 'Opacity is between 0% and 100%. The old value is kept.' };
  }
  return { ok: true, value: Number((percent / 100).toFixed(4)) };
}

export function framesToSeconds(frames: number, rate: Rational): number {
  return Number((frames / fps(rate)).toFixed(2));
}

export function secondsToFrames(seconds: number, rate: Rational): number {
  return Math.round(seconds * fps(rate));
}

/** A length in seconds as a short phrase: `2 s`, `0.5 s`. */
export function formatSeconds(seconds: number): string {
  return `${Number(seconds.toFixed(2))} s`;
}

/** A fade typed in seconds, checked against the clip and the other fade (the two must fit inside the clip). */
export function checkFadeSeconds(which: 'in' | 'out', seconds: number, rate: Rational, durationFrames: number, otherFadeFrames: number): Checked<number> {
  if (!Number.isFinite(seconds) || seconds < 0) return { ok: false, sentence: 'A fade cannot be negative. The old value is kept.' };
  const frames = secondsToFrames(seconds, rate);
  const room = Math.max(0, durationFrames - otherFadeFrames);
  if (frames > room) {
    const other = which === 'in' ? 'fade out' : 'fade in';
    return { ok: false, sentence: `That fade is longer than the room left on this clip (${formatSeconds(framesToSeconds(room, rate))}, because the ${other} takes the rest). The old value is kept.` };
  }
  return { ok: true, value: frames };
}

/** The "Auto fade" length: half a second, but never more than a quarter of the clip. */
export function autoFadeFrames(rate: Rational, durationFrames: number): number {
  return Math.max(1, Math.min(secondsToFrames(0.5, rate), Math.floor(durationFrames / 4)));
}

export interface SpeedPlan {
  playbackRate: number;
  /** The clip's new length in frames (ripples what follows). */
  durationFrames: number;
  /** The fades after the change: they shrink to fit a shorter clip. */
  fadeInFrames: number;
  fadeOutFrames: number;
  fadesShrunk: boolean;
}

/** What a new speed does to a clip: its length, and the fades that no longer fit. */
export function planSpeed(speed: number, clip: { playbackRate?: number; recordIn: number; recordOut: number; fadeInFrames?: number; fadeOutFrames?: number }): Checked<SpeedPlan> {
  if (!Number.isFinite(speed) || speed < SPEED_MIN || speed > SPEED_MAX) {
    return { ok: false, sentence: `Speed is between ${SPEED_MIN}x and ${SPEED_MAX}x. The old speed is kept.` };
  }
  const oldRate = clip.playbackRate ?? 1;
  const duration = clip.recordOut - clip.recordIn;
  const durationFrames = Math.max(1, Math.round((duration * oldRate) / speed));
  let fadeIn = clip.fadeInFrames ?? 0;
  let fadeOut = clip.fadeOutFrames ?? 0;
  let fadesShrunk = false;
  if (fadeIn + fadeOut > durationFrames) {
    fadesShrunk = true;
    const total = fadeIn + fadeOut;
    fadeIn = Math.floor((fadeIn * durationFrames) / total);
    fadeOut = Math.floor(((clip.fadeOutFrames ?? 0) * durationFrames) / total);
  }
  return { ok: true, value: { playbackRate: speed, durationFrames, fadeInFrames: fadeIn, fadeOutFrames: fadeOut, fadesShrunk } };
}

/** The "clip is now N s" readout for a speed. */
export function speedReadout(speed: number, clip: { playbackRate?: number; recordIn: number; recordOut: number }, rate: Rational): string {
  const plan = planSpeed(speed, clip);
  if (!plan.ok) return '';
  return `Clip is now ${formatSeconds(framesToSeconds(plan.value.durationFrames, rate))}`;
}

/** The gain (dB) of a volume percent; 0% is the muted floor. */
export function volumePercentToDb(percent: number): number {
  if (percent <= 0) return MUTE_DB;
  return Math.max(MUTE_DB, Number((20 * Math.log10(percent / 100)).toFixed(2)));
}

export function dbToVolumePercent(db: number): number {
  if (db <= MUTE_DB) return 0;
  return Math.round(100 * 10 ** (db / 20));
}

export function checkVolumePercent(percent: number): Checked<number> {
  if (!Number.isFinite(percent) || percent < 0 || percent > MAX_VOLUME_PERCENT) {
    return { ok: false, sentence: `Volume is between 0% and ${MAX_VOLUME_PERCENT}%. The old volume is kept.` };
  }
  return { ok: true, value: volumePercentToDb(percent) };
}

export function isMutedDb(db: number): boolean {
  return db <= MUTE_DB;
}
