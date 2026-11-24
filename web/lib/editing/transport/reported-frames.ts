/**
 * Telling the session's own frames from a user seek while playing (PA-8).
 *
 * The viewer reports each frame the session presents to the store, and the store echoes it back as
 * the playhead. A playhead that is not one of those echoes is a user seek and must reach the
 * session. An echo is CONSUMED when it arrives (with every older report): a later seek back to a
 * frame the playhead already passed is the user's, not a late echo, and is no longer ignored.
 */

/** How many reported-but-not-yet-echoed frames are remembered. */
export const RECENT_REPORTED_FRAMES = 8;

/** The list after the session reported `frame`. */
export function rememberReported(recent: readonly number[], frame: number, max = RECENT_REPORTED_FRAMES): number[] {
  return [...recent.slice(-(max - 1)), frame];
}

/**
 * Reads a playhead change while playing: `echo` when it is a frame the session reported and that
 * has not been echoed yet; `rest` is what is left to match afterwards.
 */
export function readPlayhead(recent: readonly number[], playhead: number): { echo: boolean; rest: number[] } {
  const at = recent.indexOf(playhead);
  if (at < 0) return { echo: false, rest: [] };
  return { echo: true, rest: recent.slice(at + 1) };
}
