/**
 * The decoder pool — one decode lane per video layer, a bounded decoded-frame buffer per lane, and
 * look-ahead prefetch, exactly as `spike/BUDGET.md` §5 pinned it:
 *
 *   | decoders             | one per layer, never shared                                       |
 *   | look-ahead           | 12 frames (400 ms at 30 fps)                                      |
 *   | decoded ceiling      | 18 frames per lane                                                |
 *   | eviction             | oldest-first by source frame; the frame on screen is exempt      |
 *
 * ## The invariant, enforced at construction and on every reconfiguration
 *
 *     maxDecodedBufferedFrames >= lookAheadFrames + 2
 *
 * Spike (a) measured what happens without it: at look-ahead 24 over an 18-frame ceiling the pool
 * reported healthy decoding while presenting NOTHING (2292 frames decoded and evicted unseen),
 * because eviction closed exactly the frames the playhead was about to ask for. `+1` for the frame
 * on screen, `+1` for the one in flight. A violating configuration throws `RangeError` here rather
 * than producing a black preview that reports it is working.
 *
 * ## Headless by construction
 *
 * The pool knows nothing about WebCodecs or mediabunny. A lane is fed by a `FrameSource` — an
 * object that resolves a source frame index to a drawable — and the live session supplies a
 * mediabunny-backed one. Tests supply a fake, so the buffering, eviction and look-ahead logic is
 * fully exercised under Node.
 */

export interface DecodedFrame {
  /** Source frame index this drawable shows. */
  sourceFrame: number;
  /** Anything a 2D context or a GL texture can take. */
  image: CanvasImageSource;
  /** Releases the underlying resource (a `VideoFrame`, a pooled canvas). Idempotent. */
  close(): void;
}

export interface FrameSource {
  /** Resolves the frame at `sourceFrame` (at the source's own rate), or null past the end. */
  getFrame(sourceFrame: number): Promise<DecodedFrame | null>;
  dispose(): void;
}

export interface DecoderPoolConfig {
  lookAheadFrames: number;
  maxDecodedBufferedFrames: number;
}

export const DEFAULT_POOL_CONFIG: DecoderPoolConfig = Object.freeze({
  lookAheadFrames: 12,
  maxDecodedBufferedFrames: 18,
});

export function assertPoolInvariant(config: DecoderPoolConfig): void {
  if (!Number.isInteger(config.lookAheadFrames) || config.lookAheadFrames < 0) {
    throw new RangeError(`lookAheadFrames must be a non-negative integer, got ${config.lookAheadFrames}`);
  }
  if (!Number.isInteger(config.maxDecodedBufferedFrames) || config.maxDecodedBufferedFrames < 1) {
    throw new RangeError(`maxDecodedBufferedFrames must be a positive integer, got ${config.maxDecodedBufferedFrames}`);
  }
  if (config.maxDecodedBufferedFrames < config.lookAheadFrames + 2) {
    throw new RangeError(
      `decoder pool invariant violated: maxDecodedBufferedFrames (${config.maxDecodedBufferedFrames}) must be >= ` +
        `lookAheadFrames (${config.lookAheadFrames}) + 2 — see spike/BUDGET.md §5, the footgun`,
    );
  }
}

export interface LaneStats {
  buffered: number;
  evictedUndisplayed: number;
  decodedTotal: number;
  misses: number;
}

/** One decode lane: a bounded, oldest-first buffer over a `FrameSource`, with prefetch. */
export class DecodeLane {
  private readonly buffer = new Map<number, DecodedFrame>();
  private readonly inFlight = new Map<number, Promise<DecodedFrame | null>>();
  private onScreen: number | null = null;
  private evictedUndisplayed = 0;
  private decodedTotal = 0;
  private misses = 0;
  private disposed = false;
  private parked = false;

  constructor(
    private readonly source: FrameSource,
    private readonly config: DecoderPoolConfig,
  ) {
    assertPoolInvariant(config);
  }

  /** The decoded frame for `sourceFrame` if it is already buffered — synchronous, for the render tick. */
  peek(sourceFrame: number): DecodedFrame | null {
    const frame = this.buffer.get(sourceFrame) ?? null;
    if (frame) this.onScreen = sourceFrame;
    else this.misses += 1;
    return frame;
  }

  /** Ensures `sourceFrame` and the next `lookAheadFrames` frames are being decoded. */
  prefetch(sourceFrame: number, direction: 1 | -1 = 1): void {
    if (this.disposed || this.parked) return;
    for (let i = 0; i <= this.config.lookAheadFrames; i++) {
      const target = sourceFrame + i * direction;
      if (target < 0) continue;
      if (this.buffer.has(target) || this.inFlight.has(target)) continue;
      // Never request past the ceiling: a prefetch that could only evict frames the playhead is about
      // to ask for is the exact failure the invariant guards against.
      if (this.buffer.size + this.inFlight.size >= this.config.maxDecodedBufferedFrames) break;
      const promise = this.source.getFrame(target).then(
        (frame) => {
          if (this.inFlight.get(target) === promise) this.inFlight.delete(target);
          if (this.disposed || this.parked) {
            frame?.close();
            return null;
          }
          if (frame) {
            this.decodedTotal += 1;
            this.store(target, frame);
          }
          return frame;
        },
        () => {
          if (this.inFlight.get(target) === promise) this.inFlight.delete(target);
          return null;
        },
      );
      this.inFlight.set(target, promise);
    }
  }

  /** Awaits the frame at `sourceFrame`, decoding it if needed (paused scrub path). */
  /** Resolves once the `count` frames from `sourceFrame` are buffered or have finished decoding (a pre-roll). */
  settleAhead(sourceFrame: number, count: number): Promise<void> | null {
    const pending: Promise<unknown>[] = [];
    for (let i = 0; i < count; i++) {
      const flying = this.inFlight.get(sourceFrame + i);
      if (flying && !this.buffer.has(sourceFrame + i)) pending.push(flying);
    }
    return pending.length === 0 ? null : Promise.all(pending).then(() => undefined);
  }

  async fetch(sourceFrame: number): Promise<DecodedFrame | null> {
    const buffered = this.buffer.get(sourceFrame);
    if (buffered) {
      this.onScreen = sourceFrame;
      return buffered;
    }
    let pending = this.inFlight.get(sourceFrame);
    if (!pending) {
      this.prefetch(sourceFrame, 1);
      pending = this.inFlight.get(sourceFrame);
    }
    if (!pending) {
      // The buffer was full of frames that will not be asked for again: make room explicitly.
      this.evictFor(sourceFrame);
      this.prefetch(sourceFrame, 1);
      pending = this.inFlight.get(sourceFrame);
    }
    const frame = pending ? await pending : null;
    if (frame) this.onScreen = sourceFrame;
    return frame;
  }

  /** Drops every buffered frame that is further than `lookAheadFrames` behind `sourceFrame`. */
  trimBehind(sourceFrame: number): void {
    for (const [index, frame] of this.buffer) {
      if (index < sourceFrame - 1 && index !== this.onScreen) {
        frame.close();
        this.buffer.delete(index);
      }
    }
  }

  private store(sourceFrame: number, frame: DecodedFrame): void {
    this.evictFor(sourceFrame);
    this.buffer.set(sourceFrame, frame);
  }

  /** Oldest-first eviction until there is room for one more; the on-screen frame is exempt. */
  private evictFor(incoming: number): void {
    while (this.buffer.size >= this.config.maxDecodedBufferedFrames) {
      let oldest: number | null = null;
      for (const index of this.buffer.keys()) {
        if (index === this.onScreen) continue;
        if (oldest === null || index < oldest) oldest = index;
      }
      if (oldest === null) break;
      const victim = this.buffer.get(oldest)!;
      victim.close();
      this.buffer.delete(oldest);
      if (oldest > (this.onScreen ?? -1) && oldest !== incoming) this.evictedUndisplayed += 1;
    }
  }

  stats(): LaneStats {
    return {
      buffered: this.buffer.size,
      evictedUndisplayed: this.evictedUndisplayed,
      decodedTotal: this.decodedTotal,
      misses: this.misses,
    };
  }

  /**
   * Idles the lane: its source stays open (re-opening is the expensive part — a GET and a demux),
   * but every decoded frame is released and nothing more is decoded until {@link resume}.
   */
  park(): void {
    this.parked = true;
    for (const frame of this.buffer.values()) frame.close();
    this.buffer.clear();
    this.inFlight.clear();
    this.onScreen = null;
  }

  resume(): void {
    this.parked = false;
  }

  dispose(): void {
    this.disposed = true;
    for (const frame of this.buffer.values()) frame.close();
    this.buffer.clear();
    this.inFlight.clear();
    this.source.dispose();
  }
}

/**
 * Lanes keyed by `(layerId, assetKey)` — one per video layer, never shared across layers, and never
 * reused for a different asset: a layer that cuts from file A to file B gets B's lane (A's is
 * parked, see below), so B's frames can never be drawn from A's decoder.
 *
 * Opening is memoised per key: every caller asking for the same `(layer, asset)` while the open is
 * in flight awaits the SAME promise, so the render tick (which presents un-awaited every deadline)
 * opens a source once rather than once per frame. A source that cannot be opened is remembered as
 * failed and tried again after a backoff that doubles from {@link RETRY_FIRST_MS} up to
 * {@link RETRY_MAX_MS} — never once per frame, never given up on while its layer stays on screen
 * ({@link DecoderPool.retainOnly} forgets the failure when the layer leaves).
 *
 * A lane the layer no longer shows is PARKED rather than closed: its source stays open with no
 * decoded frames held, so an A/B/A/B montage returns to A's open lane instead of fetching and
 * demuxing A again at every cut. Parking is bounded by count (`parkedLanes`, oldest closed first)
 * and by time (`parkMs`); a parked lane is handed back only for its own asset, to one layer at a time.
 */
/** The first wait before a source that failed to open is tried again, ms. */
export const RETRY_FIRST_MS = 2_000;
/** The longest wait between two attempts, ms. */
export const RETRY_MAX_MS = 30_000;

interface OpenFailure {
  layerId: string;
  attempts: number;
  retryAt: number;
}

export interface DecoderPoolOptions {
  /** How many idle lanes stay open. Default 4. */
  parkedLanes?: number;
  /** How long an idle lane stays open, ms. Default 10 s. */
  parkMs?: number;
  now?: () => number;
}

export class DecoderPool {
  private readonly lanes = new Map<string, { layerId: string; assetKey: string; lane: DecodeLane }>();
  private readonly parked: Array<{ assetKey: string; lane: DecodeLane; at: number }> = [];
  private readonly maxParked: number;
  private readonly parkMs: number;
  private readonly now: () => number;
  private readonly opening = new Map<string, { layerId: string; promise: Promise<DecodeLane | null> }>();
  private readonly failures = new Map<string, OpenFailure>();

  constructor(
    private readonly openSource: (assetKey: string) => Promise<FrameSource | null>,
    private readonly config: DecoderPoolConfig = DEFAULT_POOL_CONFIG,
    options: DecoderPoolOptions = {},
  ) {
    assertPoolInvariant(config);
    this.maxParked = Math.max(0, options.parkedLanes ?? 4);
    this.parkMs = options.parkMs ?? 10_000;
    this.now = options.now ?? (() => Date.now());
  }

  /** The lane for a layer showing `assetKey`, opening its source on first use. `null` when it cannot be opened. */
  lane(layerId: string, assetKey: string): Promise<DecodeLane | null> {
    const key = laneKey(layerId, assetKey);
    const existing = this.lanes.get(key);
    if (existing) return Promise.resolve(existing.lane);
    const inFlight = this.opening.get(key);
    if (inFlight) return inFlight.promise;
    const failure = this.failures.get(key);
    if (failure && this.now() < failure.retryAt) return Promise.resolve(null);

    // The layer now shows another asset: park the lanes it held for the previous one.
    this.releaseLayer(layerId, key);
    this.expireParked();
    const parkedAt = this.parked.findIndex((entry) => entry.assetKey === assetKey);
    if (parkedAt >= 0) {
      const [{ lane }] = this.parked.splice(parkedAt, 1);
      lane.resume();
      this.lanes.set(key, { layerId, assetKey, lane });
      return Promise.resolve(lane);
    }
    const promise = this.openSource(assetKey)
      .catch(() => null)
      .then((source) => {
        const still = this.opening.get(key);
        if (still?.promise === promise) this.opening.delete(key);
        if (!source) {
          if (still?.promise === promise) {
            const attempts = (this.failures.get(key)?.attempts ?? 0) + 1;
            const wait = Math.min(RETRY_MAX_MS, RETRY_FIRST_MS * 2 ** (attempts - 1));
            this.failures.set(key, { layerId, attempts, retryAt: this.now() + wait });
          }
          return null;
        }
        this.failures.delete(key);
        // Dropped while opening (the layer left the screen or moved on): do not keep the source.
        if (still?.promise !== promise) {
          source.dispose();
          return null;
        }
        const lane = new DecodeLane(source, this.config);
        this.lanes.set(key, { layerId, assetKey, lane });
        return lane;
      });
    this.opening.set(key, { layerId, promise });
    return promise;
  }

  /** The open lane for `(layer, asset)` right now, without opening anything — for the render tick. */
  ready(layerId: string, assetKey: string): DecodeLane | null {
    return this.lanes.get(laneKey(layerId, assetKey))?.lane ?? null;
  }

  /** The lane a layer currently holds (whatever its asset), or null. */
  peekLane(layerId: string): DecodeLane | null {
    for (const entry of this.lanes.values()) if (entry.layerId === layerId) return entry.lane;
    return null;
  }

  /** True when the source for this `(layer, asset)` could not be opened while the layer stayed live. */
  failed(layerId: string, assetKey: string): boolean {
    return this.failures.has(laneKey(layerId, assetKey));
  }

  /** When a failed source will next be tried (the pool's clock), or null when it has not failed. */
  retryAt(layerId: string, assetKey: string): number | null {
    return this.failures.get(laneKey(layerId, assetKey))?.retryAt ?? null;
  }

  /** The earliest pending retry of any failed source, or null. */
  nextRetryAt(): number | null {
    let next: number | null = null;
    for (const failure of this.failures.values()) if (next === null || failure.retryAt < next) next = failure.retryAt;
    return next;
  }

  /** Drops lanes (and remembered failures) for layers that are no longer in the document. */
  retainOnly(layerIds: Set<string>): void {
    for (const [key, entry] of this.lanes) {
      if (!layerIds.has(entry.layerId)) {
        this.lanes.delete(key);
        this.park(entry.assetKey, entry.lane);
      }
    }
    this.expireParked();
    for (const [key, entry] of this.opening) if (!layerIds.has(entry.layerId)) this.opening.delete(key);
    for (const [key, failure] of this.failures) if (!layerIds.has(failure.layerId)) this.failures.delete(key);
  }

  dispose(): void {
    for (const entry of this.lanes.values()) entry.lane.dispose();
    this.lanes.clear();
    for (const entry of this.parked) entry.lane.dispose();
    this.parked.length = 0;
    this.opening.clear();
    this.failures.clear();
  }

  private releaseLayer(layerId: string, keep: string): void {
    for (const [key, entry] of this.lanes) {
      if (entry.layerId === layerId && key !== keep) {
        this.lanes.delete(key);
        this.park(entry.assetKey, entry.lane);
      }
    }
    for (const [key, entry] of this.opening) if (entry.layerId === layerId && key !== keep) this.opening.delete(key);
    for (const [key, failure] of this.failures) if (failure.layerId === layerId && key !== keep) this.failures.delete(key);
  }

  private park(assetKey: string, lane: DecodeLane): void {
    if (this.maxParked === 0) {
      lane.dispose();
      return;
    }
    lane.park();
    this.parked.push({ assetKey, lane, at: this.now() });
    while (this.parked.length > this.maxParked) this.parked.shift()!.lane.dispose();
  }

  private expireParked(): void {
    const cutoff = this.now() - this.parkMs;
    for (let i = this.parked.length - 1; i >= 0; i--) {
      if (this.parked[i].at < cutoff) {
        this.parked[i].lane.dispose();
        this.parked.splice(i, 1);
      }
    }
  }
}

function laneKey(layerId: string, assetKey: string): string {
  return `${layerId}\u0000${assetKey}`;
}
