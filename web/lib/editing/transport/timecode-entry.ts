/**
 * Timecode entry — what the transport's timecode field accepts, as a pure parser.
 *
 * Absolute forms (jump to):
 *   - `HH:MM:SS:FF`                non-drop-frame, the display format of `lib/editing/time.ts`
 *   - `HH:MM:SS;FF`                DROP-FRAME notation (`;` before the frame field). The timeline
 *                                  counts frames, so a drop-frame label is converted to the frame
 *                                  count it NAMES at 29.97/59.94 (two frame numbers skipped every
 *                                  minute except each tenth) and is never stored. At any other rate
 *                                  the `;` is read as `:`.
 *   - `MM:SS:FF`, `SS:FF`, `FF`    shorter forms, right-aligned (the rightmost field is frames)
 *   - `1215` with no separators    right-aligned field fill: 00:00:12:15 (an overfull field carries)
 *
 * Relative forms (nudge from the current frame):
 *   - `+15` / `-15` / `+15f`       frames
 *   - `+2s` / `-0.5s`              seconds, rounded to the nearest frame
 *   - `+1:00`                      a short timecode delta, right-aligned (here: one SECOND; `+1:00:00` is a minute)
 *
 * Returns `null` for anything else; never throws, never clamps silently to zero (the caller decides
 * what to do with a refused entry, which is to leave the playhead where it is).
 */

import type { Rational } from '../types';

export interface TimecodeEntryContext {
  rate: Rational;
  currentFrame: number;
  /** Exclusive upper bound; results are clamped to `[0, maxFrame]`. Omit for no upper bound. */
  maxFrame?: number;
}

const DROP_FRAME_BASES = new Set([30, 60]);

function timecodeBase(rate: Rational): number {
  const fps = rate.den === 0 ? Number.NaN : rate.num / rate.den;
  if (!Number.isFinite(fps) || fps < 1) return 30;
  return Math.round(fps);
}

/** True when the rate is one drop-frame notation exists for (29.97 or 59.94). */
export function isDropFrameRate(rate: Rational): boolean {
  if (rate.den === 0) return false;
  const fps = rate.num / rate.den;
  const base = Math.round(fps);
  return DROP_FRAME_BASES.has(base) && Math.abs(fps - base) > 1e-6;
}

/**
 * Converts drop-frame fields to the frame count they name. Standard SMPTE 12M: at a 30 base two
 * frame numbers (0 and 1) are skipped at the start of every minute except minutes divisible by 10;
 * at a 60 base, four.
 */
export function dropFrameToFrames(hh: number, mm: number, ss: number, ff: number, base: number): number {
  const dropPerMinute = base === 60 ? 4 : 2;
  const totalMinutes = hh * 60 + mm;
  const droppedFrames = dropPerMinute * (totalMinutes - Math.floor(totalMinutes / 10));
  return ((hh * 60 + mm) * 60 + ss) * base + ff - droppedFrames;
}

function clampResult(frame: number, context: TimecodeEntryContext): number {
  const lower = Math.max(0, Math.round(frame));
  return context.maxFrame === undefined ? lower : Math.min(lower, Math.max(0, context.maxFrame));
}

function parseFields(text: string, base: number): { fields: number[]; dropFrame: boolean } | null {
  const dropFrame = text.includes(';');
  const parts = text.split(/[:;.]/);
  if (parts.length === 0 || parts.length > 4) return null;
  const fields: number[] = [];
  for (const part of parts) {
    if (!/^\d{1,3}$/.test(part)) return null;
    fields.push(Number.parseInt(part, 10));
  }
  // Right-align to [hh, mm, ss, ff].
  while (fields.length < 4) fields.unshift(0);
  const [, mm, ss, ff] = fields;
  if (mm > 59 || ss > 59 || ff >= base) return null;
  return { fields, dropFrame };
}

function fieldsToFrames(fields: number[], dropFrame: boolean, rate: Rational): number {
  const base = timecodeBase(rate);
  const [hh, mm, ss, ff] = fields;
  if (dropFrame && isDropFrameRate(rate)) return dropFrameToFrames(hh, mm, ss, ff, base);
  return ((hh * 60 + mm) * 60 + ss) * base + ff;
}

export function parseTimecodeEntry(input: string, context: TimecodeEntryContext): number | null {
  if (typeof input !== 'string') return null;
  const text = input.trim().toLowerCase();
  if (text.length === 0) return null;

  const base = timecodeBase(context.rate);
  const fps = context.rate.den === 0 ? base : context.rate.num / context.rate.den;

  const relative = /^([+-])(.+)$/.exec(text);
  if (relative) {
    const sign = relative[1] === '-' ? -1 : 1;
    const body = relative[2].trim();

    const seconds = /^(\d+(?:\.\d+)?)s$/.exec(body);
    if (seconds) {
      const delta = Math.round(Number.parseFloat(seconds[1]) * fps);
      return clampResult(context.currentFrame + sign * delta, context);
    }

    const frames = /^(\d+)f?$/.exec(body);
    if (frames) {
      return clampResult(context.currentFrame + sign * Number.parseInt(frames[1], 10), context);
    }

    if (/[:;]/.test(body)) {
      const parsed = parseFields(body, base);
      if (!parsed) return null;
      const delta = fieldsToFrames(parsed.fields, parsed.dropFrame, context.rate);
      return clampResult(context.currentFrame + sign * delta, context);
    }

    return null;
  }

  if (/^\d+$/.test(text)) {
    // Right-aligned field fill (editor.md, Transport): the last two digits are frames, then
    // seconds, minutes, and whatever is left is hours — `1215` is 00:00:12:15. A field that is
    // too big for its unit carries into the next one (`95` at 30 fps is 3 s 05 f).
    const ff = Number.parseInt(text.slice(-2), 10);
    const ss = text.length > 2 ? Number.parseInt(text.slice(-4, -2), 10) : 0;
    const mm = text.length > 4 ? Number.parseInt(text.slice(-6, -4), 10) : 0;
    const hh = text.length > 6 ? Number.parseInt(text.slice(0, -6), 10) : 0;
    return clampResult(((hh * 60 + mm) * 60 + ss) * base + ff, context);
  }

  const parsed = parseFields(text, base);
  if (!parsed) return null;
  return clampResult(fieldsToFrames(parsed.fields, parsed.dropFrame, context.rate), context);
}
