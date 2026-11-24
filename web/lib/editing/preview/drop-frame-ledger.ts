/**
 * The dropped-frame ledger — the user-visible counter, as a pure accumulator.
 *
 * A **deadline** is one output frame slot the clock passed. It is **presented** when a composite
 * was shown for it, **dropped** otherwise (the composite was not ready, or whole slots were skipped
 * between two render ticks). The ledger never estimates: it only records what the session reports.
 */

import type { PlaybackStats } from './session';

const WINDOW = 120;

export class DropFrameLedger {
  private dropped = 0;
  private presented = 0;
  private repeated = 0;
  private deadlines = 0;
  private repeat = false;
  private readonly lateness: number[] = [];
  private readonly presentTimes: number[] = [];

  /** Records `count` deadline slots that were skipped outright. */
  recordDropped(count = 1): void {
    if (count <= 0) return;
    this.dropped += count;
    this.deadlines += count;
  }

  /** Records a presented frame, with how late it was and whether it was a repeat of a held frame. */
  recordPresented(latenessMs: number, nowMs: number, wasRepeat = false): void {
    this.presented += 1;
    this.deadlines += 1;
    this.repeat = wasRepeat;
    if (wasRepeat) this.repeated += 1;
    this.lateness.push(Math.max(0, latenessMs));
    if (this.lateness.length > WINDOW) this.lateness.shift();
    this.presentTimes.push(nowMs);
    if (this.presentTimes.length > WINDOW) this.presentTimes.shift();
  }

  reset(): void {
    this.dropped = 0;
    this.presented = 0;
    this.repeated = 0;
    this.deadlines = 0;
    this.repeat = false;
    this.lateness.length = 0;
    this.presentTimes.length = 0;
  }

  snapshot(): PlaybackStats {
    const sorted = [...this.lateness].sort((a, b) => a - b);
    const p50 = sorted.length === 0 ? 0 : sorted[Math.floor(sorted.length / 2)];
    let fps = 0;
    if (this.presentTimes.length >= 2) {
      const span = this.presentTimes[this.presentTimes.length - 1] - this.presentTimes[0];
      if (span > 0) fps = ((this.presentTimes.length - 1) * 1000) / span;
    }
    return {
      droppedFrames: this.dropped,
      presentedFrames: this.presented,
      repeatedFrames: this.repeated,
      deadlines: this.deadlines,
      latenessP50Ms: p50,
      achievedFps: fps,
      lastFrameWasRepeat: this.repeat,
    };
  }
}
