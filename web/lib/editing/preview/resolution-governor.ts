/**
 * Adaptive picture size during playback. Every effect stays on: when the device cannot keep up, the viewer draws the
 * SAME composite (every layer, mask, grade and effect) at a smaller size instead of skipping frames or dropping work.
 * Paused, the viewer always shows the full-size picture again, so a still is never soft.
 *
 * The compositor's cost follows the canvas size (the fill of every pass, and the working size of the effect chain,
 * which is planned from the canvas), so a smaller canvas is the one knob that makes every stage cheaper at once.
 *
 * Decision (per play): the first `WARMUP_SEC` of slots are not judged. After that, over windows of `WINDOW_SEC`, a
 * window is bad when more than `DROP_FRACTION` of its slots were skipped in at least `MIN_DROP_EVENTS` separate skips
 * (one isolated stall is not a slow device). `STRIKES` bad windows in a row step the size down one level. Within a
 * play the size never steps back up (no oscillation). What was learned is kept for the next play: a device that had to
 * go to half size starts the next play at half size instead of stuttering through the same seconds again. After
 * `CLEAN_WINDOWS_TO_RECOVER` clean windows in a row at a reduced size, the NEXT play starts one level higher.
 *
 * Pure: slots in, a scale out. The session resizes the compositor.
 */

/** The size levels, as a factor of the session's full playback size. */
export const SCALE_STEPS: readonly number[] = [1, 0.75, 0.5, 0.375];
export const WINDOW_SEC = 1.5;
export const WARMUP_SEC = 1;
export const DROP_FRACTION = 0.1;
export const MIN_DROP_EVENTS = 2;
export const STRIKES = 2;
/** A clean window: at most this share of slots skipped. */
export const CLEAN_FRACTION = 0.01;
/** This many clean windows in a row (30 s) at a reduced size let the next play try one level higher. */
export const CLEAN_WINDOWS_TO_RECOVER = 20;
/** The picture is never drawn with a long side under this many pixels. */
export const MIN_LONG_SIDE = 256;

export class ResolutionGovernor {
  private level = 0;
  private slots = 0;
  private dropped = 0;
  private events = 0;
  private strikes = 0;
  private clean = 0;
  private recover = false;
  private warmupLeft = -1;

  /**
   * `fps`: the timeline rate (sizes the windows). `fullLongSide`: the long side of the full playback size, so the
   * levels stop where the picture would get smaller than {@link MIN_LONG_SIDE}.
   */
  constructor(
    private readonly fps: () => number,
    private readonly fullLongSide: () => number,
  ) {}

  /** The factor to draw at while playing (1 = the full playback size). */
  get scale(): number {
    return SCALE_STEPS[this.level];
  }

  /** True when no smaller level is left (the frame-rate governor and the Simple preview offer are what remain). */
  get atFloor(): boolean {
    return !this.canStepDown();
  }

  private canStepDown(): boolean {
    const next = this.level + 1;
    return next < SCALE_STEPS.length && this.fullLongSide() * SCALE_STEPS[next] >= MIN_LONG_SIDE;
  }

  /** A play starts: windows restart; a long clean run in the last play lets this one try one level higher. */
  beginPlay(): void {
    if (this.recover && this.level > 0) this.level -= 1;
    this.recover = false;
    this.slots = 0;
    this.dropped = 0;
    this.events = 0;
    this.strikes = 0;
    this.clean = 0;
    this.warmupLeft = -1;
    // The full size changed (a smaller viewer, another quality pick): a level that is now under the floor is lifted.
    while (this.level > 0 && this.fullLongSide() * SCALE_STEPS[this.level] < MIN_LONG_SIDE) this.level -= 1;
  }

  /** Forget what was learned (the user changed the quality, or the session's size changed for good). */
  reset(): void {
    this.level = 0;
    this.recover = false;
    this.beginPlay();
  }

  /** One presented slot (`droppedSlots` = 0) or a run of skipped slots before it. Returns true when the scale just changed. */
  record(droppedSlots: number): boolean {
    const fps = Math.max(1, this.fps());
    if (this.warmupLeft < 0) this.warmupLeft = Math.ceil(fps * WARMUP_SEC);
    if (this.warmupLeft > 0) {
      this.warmupLeft = Math.max(0, this.warmupLeft - 1 - droppedSlots);
      return false;
    }
    this.slots += 1 + droppedSlots;
    this.dropped += droppedSlots;
    if (droppedSlots > 0) this.events += 1;
    if (this.slots < Math.ceil(fps * WINDOW_SEC)) return false;
    const fraction = this.dropped / this.slots;
    const bad = this.events >= MIN_DROP_EVENTS && fraction > DROP_FRACTION;
    const clean = fraction <= CLEAN_FRACTION;
    this.slots = 0;
    this.dropped = 0;
    this.events = 0;
    this.strikes = bad ? this.strikes + 1 : 0;
    this.clean = clean ? this.clean + 1 : 0;
    if (this.level > 0 && this.clean >= CLEAN_WINDOWS_TO_RECOVER) this.recover = true;
    if (bad) this.recover = false;
    if (this.strikes < STRIKES || !this.canStepDown()) return false;
    this.level += 1;
    this.strikes = 0;
    this.clean = 0;
    return true;
  }
}

/** A size's long side snapped UP to a step, so a viewer being resized does not reallocate the render targets on every pixel. */
export const DISPLAY_STEP_PX = 160;
export function quantizeLongSide(px: number): number {
  return Math.max(DISPLAY_STEP_PX, Math.ceil(px / DISPLAY_STEP_PX) * DISPLAY_STEP_PX);
}

/**
 * The full playback size for "fit the viewer": the timeline's shape, with its long side no larger than the viewer
 * shows it (`displayLongSide`, device pixels), than `maxLongSide` (the device's budget) or than the timeline itself.
 * Even dimensions, at least {@link MIN_LONG_SIDE} on the long side.
 */
export function fitRenderSize(docWidth: number, docHeight: number, displayLongSide: number | null, maxLongSide: number): { width: number; height: number } {
  const docLong = Math.max(docWidth, docHeight, 1);
  const wanted = displayLongSide !== null && displayLongSide > 0 ? quantizeLongSide(displayLongSide) : maxLongSide;
  const long = Math.max(Math.min(MIN_LONG_SIDE, docLong), Math.min(docLong, maxLongSide, wanted));
  const factor = long / docLong;
  const even = (n: number) => Math.max(2, Math.round(n / 2) * 2);
  return { width: even(docWidth * factor), height: even(docHeight * factor) };
}
