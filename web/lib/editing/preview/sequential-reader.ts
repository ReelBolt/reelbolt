/**
 * A forward-sequential sample reader over an async sample iterator (mediabunny's
 * `VideoSampleSink.samples`), so every packet of a clip is decoded ONCE while it plays.
 *
 * Why: the decoder pool asks for a window of consecutive frames at once. Resolving each with its own
 * random-access `getSample`/`getCanvas` call makes the demuxer seek to the preceding key frame and
 * re-decode the GOP for every call (profiled: ~30x the decode work of one pass on a GOP-30 source).
 * Here the reads are serialised on one iterator that only ever moves forward; a request that lands
 * behind the iterator, or far ahead of it, restarts the iterator at the requested time (a seek).
 *
 * `read(t, map)` runs `map` on the sample covering time `t` (the last sample with `timestamp <= t`,
 * or the first sample when `t` precedes it) INSIDE the serialised step, so the borrowed sample can
 * never be closed by the next read before `map` has taken what it needs (a `VideoFrame` copy).
 */

export interface TimedSample {
  timestamp: number;
  close(): void;
}

export interface SequentialReaderOptions {
  /** A request more than this many seconds past the iterator's lookahead restarts it instead of decoding through. */
  maxSkipSec?: number;
}

export const DEFAULT_MAX_SKIP_SEC = 0.75;
const PAST_END_RETRY_SEC = 2;

export class SequentialReader<S extends TimedSample> {
  private iterator: AsyncGenerator<S, void, unknown> | null = null;
  private held: S | null = null;
  private ahead: S | null = null;
  private tail: Promise<unknown> = Promise.resolve();
  private disposed = false;
  private restartCount = 0;
  private readonly maxSkipSec: number;

  constructor(
    private readonly open: (startTimestamp: number) => AsyncGenerator<S, void, unknown>,
    options: SequentialReaderOptions = {},
  ) {
    this.maxSkipSec = options.maxSkipSec ?? DEFAULT_MAX_SKIP_SEC;
  }

  /** How many times the iterator was (re)started: 1 for a clean forward play, more for seeks. */
  get restarts(): number {
    return this.restartCount;
  }

  read<R>(timestamp: number, map: (sample: S) => R): Promise<R | null> {
    const run = this.tail.then(() => this.step(timestamp, map));
    this.tail = run.catch(() => undefined);
    return run;
  }

  dispose(): void {
    this.disposed = true;
    this.tail = this.tail.then(() => this.release());
  }

  private async step<R>(t: number, map: (sample: S) => R): Promise<R | null> {
    if (this.disposed) return null;
    const behind = this.held !== null && t < this.held.timestamp;
    const farAhead = this.ahead !== null && t - this.ahead.timestamp > this.maxSkipSec;
    if (this.iterator === null || behind || farAhead) await this.start(t);
    while (this.ahead !== null && this.ahead.timestamp <= t) {
      this.held?.close();
      this.held = this.ahead;
      this.ahead = null;
      this.ahead = await this.pull();
      if (this.disposed) return null;
    }
    const sample = this.held ?? this.ahead;
    return sample ? map(sample) : null;
  }

  private async start(t: number): Promise<void> {
    await this.release();
    this.restartCount += 1;
    this.iterator = this.open(Math.max(0, t));
    this.ahead = await this.pull();
    if (this.ahead === null && t > 0) {
      // Past the clip's last frame (a transition leg reading beyond its source): an iterator started there
      // yields nothing. Walk in from a little earlier so the last frame is held, as a clamped read would.
      await this.release();
      this.iterator = this.open(Math.max(0, t - PAST_END_RETRY_SEC));
      this.ahead = await this.pull();
    }
  }

  private async pull(): Promise<S | null> {
    const it = this.iterator;
    if (!it) return null;
    const next = await it.next();
    return next.done ? null : next.value;
  }

  private async release(): Promise<void> {
    this.held?.close();
    this.ahead?.close();
    this.held = null;
    this.ahead = null;
    const it = this.iterator;
    this.iterator = null;
    if (it) await it.return(undefined).catch(() => undefined);
  }
}
