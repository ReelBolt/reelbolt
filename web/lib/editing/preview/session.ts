/**
 * The preview-session contract the viewer talks to, and the one implementation that is always
 * available: `unavailablePreviewSession`.
 *
 * Three kinds exist:
 *   - `live`        WebCodecs decode (mediabunny demux) + WebGL2 composite + WebAudio mix — the
 *                   §3.5 compositor. Requires `VideoDecoder`, WebGL2 and `AudioContext`.
 *   - `fallback`    the §3.5 degraded path: a single `<video>` over the proxy of the clip under the
 *                   playhead, seeked per frame. Confirmed in spike (a) at 29.87 fps. Labelled as
 *                   degraded in the UI; cannot stack layers or mix tracks.
 *   - `unavailable` no decodable media reachable (no proxies yet, or an empty timeline). The viewer
 *                   shows WHY instead of a black box.
 *
 * Every kind exposes the same `PlaybackStats` ledger, so the status bar's dropped-frame counter is
 * never a number invented by the UI — it is whatever the session measured (0 for `unavailable`).
 */

import type { SvgGradeFilter } from '../color/svg-grade';

export type PreviewSessionKind = 'live' | 'fallback' | 'unavailable';

export interface PlaybackStats {
  /** Deadlines that were missed (frame not presented on time). The user-visible counter. */
  droppedFrames: number;
  /** Frames actually presented. */
  presentedFrames: number;
  /** Presented frames in which some layer showed a held picture (late or undecoded) instead of a NEW decoded frame. `presentedFrames - repeatedFrames` are fresh. */
  repeatedFrames: number;
  /** Deadlines seen so far (presented + dropped). */
  deadlines: number;
  /** p50 lateness of presented frames, ms, over the last window. */
  latenessP50Ms: number;
  /** Achieved fps over the last window. */
  achievedFps: number;
  /** True when the last presented frame was a repeat (a late layer re-showed its held frame). */
  lastFrameWasRepeat: boolean;
}

export const ZERO_STATS: PlaybackStats = Object.freeze({
  droppedFrames: 0,
  presentedFrames: 0,
  repeatedFrames: 0,
  deadlines: 0,
  latenessP50Ms: 0,
  achievedFps: 0,
  lastFrameWasRepeat: false,
});

/** P5: what the live audio graph exposes to the mixer, once per poll. */
export interface AudioMeterSnapshot {
  sampleRate: number;
  /** Per audio track, the peak sample magnitude (0..1+) in the last analyser window. */
  tracks: { trackId: string; peak: number }[];
  /** The master bus's most recent time-domain window per channel (left, right). */
  master: Float32Array[];
}

/**
 * A layer the session could not draw at the frame it last showed, and why:
 *   - `noUrl`       the source has no decodable URL yet (lookup pending, or nothing to read);
 *   - `openFailed`  the URL could not be opened for decoding;
 *   - `noFrame`     the source opened but has no picture at that point (paused frames only — a late
 *                   frame during playback is a repeat of the SAME clip, counted by the ledger);
 *   - `notDecoded`  during playback, the clip's source is still opening or its first picture is not
 *                   decoded yet, and the layer holds no picture of this clip to repeat (never the
 *                   previous clip's).
 */
export interface MissingLayer {
  itemId: string;
  trackId: string;
  cause: 'noUrl' | 'openFailed' | 'noFrame' | 'notDecoded';
}

/**
 * Why a session is not the live compositor, one cause each (the viewer words it and picks the action):
 *   - `userChoice`        the Simple preview was chosen (by the user, or by Auto on a phone);
 *   - `noWebGl2`          this browser cannot make a WebGL2 context (graphics acceleration is off or missing);
 *   - `noWebCodecs`       this browser has no WebCodecs;
 *   - `compositorFailed`  WebGL2 and WebCodecs exist but the compositor failed to start.
 */
export type PreviewFault = 'userChoice' | 'noWebGl2' | 'noWebCodecs' | 'compositorFailed';

/** A live session whose graphics context is gone: `lost` while the browser may still restore it, `failed` when the rebuild did not work. */
export interface ContextLoss {
  state: 'lost' | 'failed';
  message: string;
}

export interface PreviewSession {
  readonly kind: PreviewSessionKind;
  /** For `fallback`/`unavailable`: the one cause behind it. */
  readonly fault?: PreviewFault | null;
  /** For `fallback`: the live preview could run on this browser (WebCodecs and WebGL2 work), so "Use live preview" is honest. */
  readonly liveAvailable?: boolean;
  /** For `live`: the graphics context was lost (or could not be rebuilt); null while it is fine. */
  readonly contextLoss?: ContextLoss | null;
  /** For `fallback`: the colour grade of the clip on screen as an SVG filter, or null (no grade, bypassed). */
  gradeFilter?(): SvgGradeFilter | null;
  /** Why the session is what it is — shown to the user for `fallback`/`unavailable`. */
  readonly reason: string | null;
  /** A live session that is deliberately running below full quality (software graphics): one sentence for the viewer, else null. */
  readonly reducedQuality?: string | null;
  /**
   * For `live`: the adaptive picture size right now. `scale` is the factor of the full playback size being drawn while
   * playing (1 when paused or not reduced); `atFloor` is true once no smaller size is left to try.
   */
  readonly adaptiveQuality?: { scale: number; atFloor: boolean; width: number; height: number };
  /**
   * For `live` with Auto quality: the long side, in device pixels, the viewer shows the picture at (null = unknown).
   * The session then draws no larger than that. A no-op for a session with a fixed size.
   */
  setDisplayLongSide?(px: number | null): void;
  /** Shows the given timeline frame (paused scrub). */
  seek(frame: number): void;
  /** Starts the clock at `speed` (frames advance against the audio clock). 0 pauses. */
  play(speed: number): void;
  pause(): void;
  /** The frame the session is currently showing. */
  currentFrame(): number;
  /** True while the session's clock is running (it pauses itself at the end of the timeline). */
  isPlaying(): boolean;
  stats(): PlaybackStats;
  /**
   * P5: the audio meters of the live graph (per-track peaks, the master's recent samples for the
   * LUFS meter), or `null` for a session without a running audio graph. Sampled, not gap-free —
   * the authoritative loudness number is the render's `loudnorm` measurement.
   */
  meters?(): AudioMeterSnapshot | null;
  /** PA-13: the layers missing from the frame last shown (empty when everything was drawn). */
  missingLayers?(): readonly MissingLayer[];
  /** Fires after every presented or dropped frame and on every stats change. */
  subscribe(listener: () => void): () => void;
  /** The document changed; the session re-plans what is active at each frame. */
  invalidate(): void;
  dispose(): void;
}

export function unavailablePreviewSession(reason: string): PreviewSession {
  let frame = 0;
  const listeners = new Set<() => void>();
  const notify = () => {
    for (const l of listeners) l();
  };
  return {
    kind: 'unavailable',
    reason,
    seek(next) {
      frame = Math.max(0, Math.trunc(next));
      notify();
    },
    play() {
      /* nothing to drive */
    },
    pause() {
      /* nothing to drive */
    },
    currentFrame: () => frame,
    isPlaying: () => false,
    stats: () => ZERO_STATS,
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    invalidate() {
      notify();
    },
    dispose() {
      listeners.clear();
    },
  };
}
