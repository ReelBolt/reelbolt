/**
 * Loop and Play in → out as pure policy (PA-7).
 *
 * - Loop on, no marks: the whole timeline loops.
 * - Loop on with marks: playback wraps between In and Out (a missing mark is the timeline edge).
 * - Play in → out (Shift+Space): plays from In and stops on the last frame before Out.
 * - Plain play ignores the marks and runs to the end of the timeline.
 *
 * Marks set in the wrong order are read as the range they span. `outFrame` is exclusive.
 */

export interface PlayRange {
  inFrame: number;
  /** Exclusive. */
  outFrame: number;
  loop: boolean;
}

export type RangeAction = { kind: 'continue' } | { kind: 'loop'; frame: number } | { kind: 'stop'; frame: number };

export function effectivePlayRange(
  marks: { inFrame: number | null; outFrame: number | null },
  mode: { loop: boolean; inToOut: boolean; endFrame: number },
): PlayRange | null {
  const end = Math.max(0, mode.endFrame);
  const a = marks.inFrame ?? 0;
  const b = marks.outFrame ?? end;
  const inFrame = Math.max(0, Math.min(a, b));
  const outFrame = Math.max(inFrame + 1, Math.min(end > 0 ? end : Math.max(a, b), Math.max(a, b)));
  if (mode.loop) return { inFrame, outFrame, loop: true };
  if (mode.inToOut && marks.inFrame !== null && marks.outFrame !== null) return { inFrame, outFrame, loop: false };
  return null;
}

/** What to do when the session presents `frame` while playing at `speed`. */
export function rangeStep(frame: number, speed: number, range: PlayRange | null): RangeAction {
  if (!range || speed === 0) return { kind: 'continue' };
  if (speed > 0 && frame >= range.outFrame) {
    return range.loop ? { kind: 'loop', frame: range.inFrame } : { kind: 'stop', frame: range.outFrame - 1 };
  }
  if (speed < 0 && frame < range.inFrame) {
    return range.loop ? { kind: 'loop', frame: range.outFrame - 1 } : { kind: 'stop', frame: range.inFrame };
  }
  return { kind: 'continue' };
}

/** The session stopped by itself at a timeline edge: loop back, or stop (frame null = stay put). */
export function rangeEnded(range: PlayRange | null, speed: number): { kind: 'loop'; frame: number } | { kind: 'stop'; frame: number | null } {
  if (!range) return { kind: 'stop', frame: null };
  if (range.loop) return { kind: 'loop', frame: speed < 0 ? range.outFrame - 1 : range.inFrame };
  return { kind: 'stop', frame: speed < 0 ? range.inFrame : range.outFrame - 1 };
}
