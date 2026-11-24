/**
 * Honest frame-rate degradation. When a timeline of 50 fps or more cannot be presented (sustained skipped slots), the
 * viewer presents every second frame instead of stuttering: still the audio-clock picture, at half the rate, labelled
 * "Reduced frame rate", with the dropped-frame counter counting slots of the REDUCED rate (so it stays honest).
 *
 * A 4K source on screen (`heavy`) lets a 30 fps timeline halve too, and any timeline halve once more (never below 15 fps).
 *
 * Decision: the first `WARMUP_SEC` of slots after a Play (a seek during play restarts Play) are not judged, so a
 * start-up stall cannot halve the rate. After that, over each window of `WINDOW_SLOTS` slots, a window is bad when
 * more than `DROP_FRACTION` were dropped AND the drops came from at least `MIN_DROP_EVENTS` separate skips (one isolated
 * stall, however long, is not a sustained drop). `STRIKES` bad windows in a row halve the rate. It never speeds back up
 * within a play (no oscillation); `reset()` on every Play. The governor only decides the rate: the dropped-frame
 * ledger counts every skipped slot, grace period included.
 */

export const WINDOW_SLOTS = 90;
export const DROP_FRACTION = 0.08;
/** With a heavy source on screen the bar is lower: 4K at 60 fps dropping 3-8 % is shown as a label, not left as a silent stutter. */
export const DROP_FRACTION_HEAVY = 0.03;
export const STRIKES = 3;
/** Slots of the first this-many seconds after Play are not judged (decoder first frames, audio start, look-ahead refill). */
export const WARMUP_SEC = 1.5;
/** A bad window needs at least this many separate skips: a single stall is not a sustained drop. */
export const MIN_DROP_EVENTS = 2;
export const MIN_FPS_TO_HALVE = 50;
/** With a heavy (4K) source on screen even a 30 fps timeline may be halved, and a second time, never below this rate. */
export const MIN_FPS_HEAVY = 15;

export class RateGovernor {
  private step = 1;
  private slots = 0;
  private dropped = 0;
  private strikes = 0;
  private events = 0;
  private warmupLeft = -1;

  /** `heavy`: a source too large to decode and upload at the timeline rate on every machine (3840 px wide or more) is on screen. */
  constructor(
    private readonly timelineFps: () => number,
    private readonly heavy: () => boolean = () => false,
  ) {}

  /** 1 = every frame, 2 = every second frame. */
  get frameStep(): number {
    return this.step;
  }

  reset(): void {
    this.step = 1;
    this.slots = 0;
    this.dropped = 0;
    this.strikes = 0;
    this.events = 0;
    this.warmupLeft = -1;
  }

  /** One presented slot (dropped = 0) or a run of skipped slots; returns true when the step just changed. */
  record(droppedSlots: number): boolean {
    if (this.warmupLeft < 0) this.warmupLeft = Math.ceil(this.timelineFps() * WARMUP_SEC);
    if (this.warmupLeft > 0) {
      this.warmupLeft = Math.max(0, this.warmupLeft - 1 - droppedSlots);
      return false;
    }
    this.slots += 1 + droppedSlots;
    this.dropped += droppedSlots;
    if (droppedSlots > 0) this.events += 1;
    if (this.slots < WINDOW_SLOTS) return false;
    const bad = this.events >= MIN_DROP_EVENTS && this.dropped / this.slots > (this.heavy() ? DROP_FRACTION_HEAVY : DROP_FRACTION);
    this.slots = 0;
    this.dropped = 0;
    this.events = 0;
    this.strikes = bad ? this.strikes + 1 : 0;
    if (this.strikes < STRIKES) return false;
    const fps = this.timelineFps();
    const heavy = this.heavy();
    const canHalve = this.step === 1 ? fps >= MIN_FPS_TO_HALVE || (heavy && fps / 2 >= MIN_FPS_HEAVY) : this.step === 2 && heavy && fps / 4 >= MIN_FPS_HEAVY;
    if (!canHalve) return false;
    this.step *= 2;
    this.strikes = 0;
    return true;
  }
}
