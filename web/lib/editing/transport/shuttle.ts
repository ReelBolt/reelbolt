/**
 * JKL shuttle — the state machine behind the transport's J / K / L keys.
 *
 *   - `L` plays forward; repeated presses step the speed 1× → 2× → 4× → 8× (and stay at 8×).
 *   - `J` plays in reverse with the same ladder (−1× → −2× → −4× → −8×).
 *   - `K` pauses (speed 0). While `K` is held, `J`/`L` step ONE frame (`stepFrame` below).
 *   - Pressing the opposite direction while shuttling drops to that direction's 1×, not to 0 —
 *     the NLE convention, so a J tap during forward play means "back at 1×".
 *   - Space toggles between 0 and the last non-zero forward speed (default 1×).
 *
 * Pure: `(speed, key) → speed`. The clock that turns a speed into frame motion is `playback-clock.ts`.
 */

export const SHUTTLE_LADDER: readonly number[] = [1, 2, 4, 8];

export type ShuttleKey = 'J' | 'K' | 'L' | 'Space';

export function shuttle(speed: number, key: ShuttleKey): number {
  switch (key) {
    case 'K':
      return 0;
    case 'L':
      return speed <= 0 ? 1 : nextOnLadder(speed);
    case 'J':
      return speed >= 0 ? -1 : -nextOnLadder(-speed);
    case 'Space':
      return speed === 0 ? 1 : 0;
    default:
      return speed;
  }
}

function nextOnLadder(magnitude: number): number {
  for (const step of SHUTTLE_LADDER) {
    if (step > magnitude) return step;
  }
  return SHUTTLE_LADDER[SHUTTLE_LADDER.length - 1];
}

/**
 * Advances a fractional playhead by `speed × dtSec × fps` frames. The caller keeps `fractional`
 * between calls so that sub-frame remainders accumulate instead of being dropped — at 0.5× on a
 * 60 Hz display every other tick would otherwise move nothing and the next would move one frame.
 */
export function advancePlayhead(
  fractional: number,
  speed: number,
  dtSec: number,
  fps: number,
  maxFrame: number,
): { fractional: number; frame: number; hitEnd: boolean } {
  if (!Number.isFinite(dtSec) || dtSec < 0) dtSec = 0;
  const next = fractional + speed * dtSec * fps;
  if (next <= 0) return { fractional: 0, frame: 0, hitEnd: speed < 0 };
  if (next >= maxFrame) return { fractional: maxFrame, frame: Math.floor(maxFrame), hitEnd: speed > 0 };
  return { fractional: next, frame: Math.floor(next), hitEnd: false };
}
