/**
 * How many frames playback skipped in the last few seconds. The Simple shell has no status bar, so its viewer says
 * this on the picture while it is happening and says nothing when playback is smooth. Pure: samples in, a count out.
 */
export const RECENT_DROP_WINDOW_MS = 3000;

export interface RecentDrops {
  /** One reading of the session's running dropped-frame total; returns the frames dropped inside the window. */
  push(sample: { at: number; dropped: number }): number;
  reset(): void;
}

export function createRecentDrops(windowMs: number = RECENT_DROP_WINDOW_MS): RecentDrops {
  let samples: { at: number; dropped: number }[] = [];
  return {
    push(sample) {
      // A total that went down means a new session started counting from zero.
      if (samples.length > 0 && sample.dropped < samples[samples.length - 1].dropped) samples = [];
      samples.push(sample);
      // The baseline is the newest reading that is at least a window old (or the oldest one there is).
      while (samples.length > 1 && sample.at - samples[1].at >= windowMs) samples.shift();
      return Math.max(0, sample.dropped - samples[0].dropped);
    },
    reset() {
      samples = [];
    },
  };
}
