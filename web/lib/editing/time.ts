/**
 * Frame <-> timecode conversion for the ReelBolt editing timeline.
 *
 * **Non-drop-frame only.** Every timecode this module produces or accepts is `HH:MM:SS:FF`
 * counted at a constant integer frame base — the `:` before the frame field is the conventional
 * mark of non-drop-frame. Drop-frame notation (`HH:MM:SS;FF`, which skips frame numbers to keep
 * NTSC timecode aligned with wall-clock time) is deliberately NOT supported: the timeline
 * contract counts frames, so a dropped-frame label would be a second, disagreeing source of
 * truth for the same value.
 *
 * The frame base is `Math.round(rate.num / rate.den)`, the same convention
 * `lib/utils/timecode.ts` already uses for its `fps` argument — so 30000/1001 (29.97) works on
 * a 30-frame base. That keeps `timecodeToFrames(framesToTimecode(n, rate), rate) === n` for
 * every integer frame count `n >= 0` at any rate, including fractional ones.
 *
 * Malformed input is rejected by clamping to zero, never by throwing — mirroring the tolerance
 * of `lib/utils/timecode.ts`. This module is intentionally standalone: it does not import from
 * `lib/utils/timecode.ts`, which is seconds-based and has no notion of the `Rational` contract.
 */

import type { Rational } from './types';

export type { Rational };

/** The conventional fallback when a rate is missing, zero, negative or not finite. */
const DEFAULT_TIMECODE_BASE = 30;

/** Whole-frame timecode base for a rate, clamped to at least one frame per second. */
function timecodeBase(rate: Rational | null | undefined): number {
  const fps = rate ? rate.num / rate.den : Number.NaN;
  if (!Number.isFinite(fps) || fps < 1) return DEFAULT_TIMECODE_BASE;
  return Math.round(fps);
}

const pad = (value: number): string => String(value).padStart(2, '0');

/**
 * Formats a frame count as `HH:MM:SS:FF` (non-drop-frame) at the given rate.
 *
 * A non-finite or negative frame count is treated as `0`; a fractional one is floored, since a
 * timecode label can only address whole frames.
 */
export function framesToTimecode(frames: number, rate: Rational): string {
  const safeFrames = Number.isFinite(frames) && frames > 0 ? Math.floor(frames) : 0;
  const base = timecodeBase(rate);

  const ff = safeFrames % base;
  const totalSeconds = Math.floor(safeFrames / base);
  const ss = totalSeconds % 60;
  const mm = Math.floor(totalSeconds / 60) % 60;
  const hh = Math.floor(totalSeconds / 3600);

  return `${pad(hh)}:${pad(mm)}:${pad(ss)}:${pad(ff)}`;
}

/**
 * Parses an `HH:MM:SS:FF` (non-drop-frame) timecode back into a frame count — the exact inverse
 * of {@link framesToTimecode} for any frame count that function produced.
 *
 * Anything malformed (a wrong segment count, a non-numeric segment, or a field outside
 * `00-59` / `00-(base-1)`) returns `0` rather than throwing or returning a partially parsed
 * value.
 */
export function timecodeToFrames(tc: string, rate: Rational): number {
  if (typeof tc !== 'string') return 0;

  const match = /^(\d+):(\d+):(\d+):(\d+)$/.exec(tc.trim());
  if (!match) return 0;

  const [hh, mm, ss, ff] = match.slice(1).map((part) => Number.parseInt(part, 10));
  const base = timecodeBase(rate);

  if (mm > 59 || ss > 59 || ff >= base) return 0;

  return ((hh * 60 + mm) * 60 + ss) * base + ff;
}

// ─────────────────────────────────────────────────────────────────────────────
// R01.5: typed timecode entry
// ─────────────────────────────────────────────────────────────────────────────

export type TimecodeParseResult =
  | { ok: true; frames: number; timecode: string }
  | { ok: false; reason: string };

/** The most digits a typed stream can hold: HHMMSSFF. */
export const TIMECODE_MAX_DIGITS = 8;

/** The longest text a timecode field accepts (`HH:MM:SS:FF`). */
export const TIMECODE_MAX_LENGTH = 11;

/**
 * Turns what the user typed in a timecode field into frames, or says in plain words why not.
 *
 * Two forms are understood:
 * - a digit stream with no colons: the digits fill the timecode from the right, so `1215` is
 *   `00:00:12:15`, `90` is `00:00:00:90` (refused: frames stop at the frame rate) and `100` is
 *   `00:00:01:00`.
 * - colon form, right-aligned too: `12:15` is `00:00:12:15`, `1:02:03:04` is `01:02:03:04`.
 *
 * Never throws; an invalid entry is `{ ok: false, reason }` so the caller can revert the field.
 */
export function parseTimecodeInput(text: string, rate: Rational): TimecodeParseResult {
  const raw = typeof text === 'string' ? text.trim() : '';
  if (raw === '') return { ok: false, reason: 'Type a time, like 1215 for 00:00:12:15.' };
  if (raw.includes(';')) return { ok: false, reason: 'Drop-frame timecode is not supported. Use colons, like 00:00:12:15.' };
  if (!/^[0-9:]+$/.test(raw)) return { ok: false, reason: 'Use numbers only, like 00:00:12:15.' };

  const base = timecodeBase(rate);
  let parts: number[];
  if (raw.includes(':')) {
    const segments = raw.split(':');
    if (segments.length > 4 || segments.some((segment) => segment === '' || segment.length > 3)) {
      return { ok: false, reason: 'Use the form hours:minutes:seconds:frames, like 00:00:12:15.' };
    }
    parts = segments.map((segment) => Number.parseInt(segment, 10));
    while (parts.length < 4) parts.unshift(0);
  } else {
    if (raw.length > TIMECODE_MAX_DIGITS) return { ok: false, reason: 'That is too many digits. Use up to 8, like 00001215.' };
    const padded = raw.padStart(TIMECODE_MAX_DIGITS, '0');
    parts = [0, 2, 4, 6].map((at) => Number.parseInt(padded.slice(at, at + 2), 10));
  }

  const [hh, mm, ss, ff] = parts;
  if (mm > 59) return { ok: false, reason: 'Minutes go up to 59.' };
  if (ss > 59) return { ok: false, reason: 'Seconds go up to 59.' };
  if (ff >= base) return { ok: false, reason: `Frames go up to ${base - 1} at this frame rate.` };

  const frames = ((hh * 60 + mm) * 60 + ss) * base + ff;
  return { ok: true, frames, timecode: framesToTimecode(frames, rate) };
}

/**
 * What a digit stream reads as while it is being typed: the digits right-aligned into `HH:MM:SS:FF`
 * (so the field shows `00:00:12:15` as the user types 1, 2, 1, 5). Non-digits are ignored and extra
 * digits beyond eight keep only the last eight.
 */
export function digitStreamPreview(digits: string): string {
  const only = digits.replace(/\D/g, '').slice(-TIMECODE_MAX_DIGITS).padStart(TIMECODE_MAX_DIGITS, '0');
  return `${only.slice(0, 2)}:${only.slice(2, 4)}:${only.slice(4, 6)}:${only.slice(6, 8)}`;
}

// ─────────────────────────────────────────────────────────────────────────────
// R01.3: the formats and frame rates a person picks in plain words
// ─────────────────────────────────────────────────────────────────────────────

export interface TimelineFormat {
  key: 'landscape' | 'landscape4k' | 'vertical' | 'square' | 'portrait';
  label: string;
  width: number;
  height: number;
}

export const TIMELINE_FORMATS: readonly TimelineFormat[] = [
  { key: 'landscape', label: 'Landscape 16:9 1080p', width: 1920, height: 1080 },
  { key: 'landscape4k', label: '4K landscape', width: 3840, height: 2160 },
  { key: 'vertical', label: 'Vertical 9:16', width: 1080, height: 1920 },
  { key: 'square', label: 'Square 1:1', width: 1080, height: 1080 },
  { key: 'portrait', label: 'Portrait 4:5', width: 1080, height: 1350 },
];

export const FRAME_RATE_CHOICES: readonly number[] = [24, 25, 30, 50, 60];

/** The value of the "Match my first clip" entry in a frame-rate list. */
export const MATCH_FIRST_CLIP = 'match';

export const DEFAULT_FRAME_RATE: Rational = { num: 30, den: 1 };

/** The timeline format whose size is exactly `width` x `height`, if one of the named formats is. */
export function formatForSize(width: number, height: number): TimelineFormat | null {
  return TIMELINE_FORMATS.find((f) => f.width === width && f.height === height) ?? null;
}

/** "24", "29.97": a frame rate as a person says it. */
export function describeFrameRate(rate: Rational): string {
  const fps = rate.den === 0 ? 0 : rate.num / rate.den;
  return Number.isInteger(fps) ? String(fps) : String(Math.round(fps * 1000) / 1000);
}

/** The probe fields of a pool item this module needs; structural so it does not import the media types. */
export interface ProbedRate {
  kind: string;
  probe: { fpsNum: number | null; fpsDen: number | null } | null;
}

/**
 * "Match my first clip": the frame rate of the first video in the pool whose rate is known, or the
 * 30 fps default with `matched: false` so the caller can show the hint.
 */
export function matchFirstClipRate(pool: readonly ProbedRate[] | null | undefined): { rate: Rational; matched: boolean } {
  for (const item of pool ?? []) {
    const num = item.probe?.fpsNum;
    const den = item.probe?.fpsDen;
    if (item.kind === 'video' && num && den && num > 0 && den > 0 && num / den <= 120) {
      return { rate: { num, den }, matched: true };
    }
  }
  return { rate: DEFAULT_FRAME_RATE, matched: false };
}
