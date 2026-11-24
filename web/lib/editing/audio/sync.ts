/**
 * P8: audio sync by waveform cross-correlation — the multicam angle offsets.
 *
 * Each angle's audio is reduced to an ENVELOPE (mean absolute amplitude per bucket, mono, at
 * `bucketsPerSecond`), the envelopes are mean-removed, and the lag maximising the normalised
 * cross-correlation within `±maxLagSeconds` is the offset. A clap or a word onset dominates the
 * envelope, which is why this works on scratch audio from different microphones. `confidence` is
 * the winning correlation coefficient (−1..1) together with how far it stands above the runner-up
 * outside its own neighbourhood; a flat or unrelated pair reads low and the UI says so rather than
 * applying a guess.
 *
 * Resolution is one bucket (10 ms at the default 100 Hz), finer than a frame at every supported
 * rate. The decode step (`decodeEnvelope`) uses WebAudio's `decodeAudioData` over the media's
 * proxy/original URL; the maths below is pure and tested on synthetic signals.
 */

export interface SyncResult {
  /** Seconds `other` must be ADVANCED by (its source read later) to line up with `reference`. */
  offsetSeconds: number;
  /** The same offset in envelope buckets. */
  lagBuckets: number;
  /** Normalised correlation at the winning lag, −1..1. */
  correlation: number;
  /** Winner minus the best lag outside ±(1 % of the window), 0..2 — near 0 means ambiguous. */
  margin: number;
}

/** Mean absolute amplitude per bucket of a mono PCM signal. */
export function envelope(samples: Float32Array | readonly number[], sampleRate: number, bucketsPerSecond = 100): Float32Array {
  const bucket = Math.max(1, Math.round(sampleRate / bucketsPerSecond));
  const count = Math.floor(samples.length / bucket);
  const out = new Float32Array(count);
  for (let b = 0; b < count; b++) {
    let sum = 0;
    for (let i = b * bucket; i < (b + 1) * bucket; i++) sum += Math.abs(samples[i]);
    out[b] = sum / bucket;
  }
  return out;
}

/**
 * The lag (in buckets) of `other` relative to `reference`: a positive lag means the same event
 * occurs `lag` buckets LATER in `other`, so `other`'s source must start `lag` buckets later.
 */
export function estimateSyncOffset(reference: Float32Array, other: Float32Array, bucketsPerSecond = 100, maxLagSeconds = 30): SyncResult {
  const a = centred(reference);
  const b = centred(other);
  const maxLag = Math.max(0, Math.min(Math.round(maxLagSeconds * bucketsPerSecond), Math.max(a.length, b.length) - 1));
  const scores = new Float64Array(2 * maxLag + 1);
  let bestLag = 0;
  let best = Number.NEGATIVE_INFINITY;
  for (let lag = -maxLag; lag <= maxLag; lag++) {
    // Correlate a[i] with b[i + lag] over the overlap.
    let dot = 0;
    let na = 0;
    let nb = 0;
    const from = Math.max(0, -lag);
    const to = Math.min(a.length, b.length - lag);
    for (let i = from; i < to; i++) {
      const x = a[i];
      const y = b[i + lag];
      dot += x * y;
      na += x * x;
      nb += y * y;
    }
    const score = na > 0 && nb > 0 && to - from >= Math.min(a.length, b.length) / 4 ? dot / Math.sqrt(na * nb) : Number.NEGATIVE_INFINITY;
    scores[lag + maxLag] = score;
    if (score > best) {
      best = score;
      bestLag = lag;
    }
  }
  const guard = Math.max(2, Math.round(scores.length / 100));
  let runnerUp = Number.NEGATIVE_INFINITY;
  for (let lag = -maxLag; lag <= maxLag; lag++) {
    if (Math.abs(lag - bestLag) <= guard) continue;
    runnerUp = Math.max(runnerUp, scores[lag + maxLag]);
  }
  const correlation = Number.isFinite(best) ? best : 0;
  return {
    offsetSeconds: bestLag / bucketsPerSecond,
    lagBuckets: bestLag,
    correlation,
    margin: Number.isFinite(runnerUp) ? Math.max(0, correlation - runnerUp) : correlation,
  };
}

/** Whether a sync result is trustworthy enough to apply without the user confirming it. */
export function isConfidentSync(result: SyncResult): boolean {
  return result.correlation >= 0.5 && result.margin >= 0.05;
}

/** Seconds → timeline frames at `rate`. */
export function offsetToFrames(offsetSeconds: number, rate: { num: number; den: number }): number {
  const fps = rate.den > 0 ? rate.num / rate.den : 30;
  return Math.round(offsetSeconds * fps);
}

function centred(values: Float32Array): Float64Array {
  let mean = 0;
  for (const v of values) mean += v;
  mean /= Math.max(1, values.length);
  const out = new Float64Array(values.length);
  for (let i = 0; i < values.length; i++) out[i] = values[i] - mean;
  return out;
}

/**
 * Decodes a media URL's audio (WebAudio) to a mono envelope. Browser-only; throws a readable error
 * when the platform cannot decode (no `AudioContext`, or a codec the browser lacks).
 */
export async function decodeEnvelope(url: string, bucketsPerSecond = 100, maxSeconds = 600): Promise<Float32Array> {
  const Ctx = typeof window === 'undefined' ? undefined : (window.OfflineAudioContext ?? (window as unknown as { webkitOfflineAudioContext?: typeof OfflineAudioContext }).webkitOfflineAudioContext);
  if (!Ctx) throw new Error('This browser cannot decode audio (no OfflineAudioContext).');
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) throw new Error(`Could not read the media (${response.status}).`);
  const bytes = await response.arrayBuffer();
  const context = new Ctx(1, 48000, 48000);
  const buffer = await context.decodeAudioData(bytes);
  const length = Math.min(buffer.length, Math.round(maxSeconds * buffer.sampleRate));
  const mono = new Float32Array(length);
  for (let c = 0; c < buffer.numberOfChannels; c++) {
    const data = buffer.getChannelData(c);
    for (let i = 0; i < length; i++) mono[i] += data[i] / buffer.numberOfChannels;
  }
  return envelope(mono, buffer.sampleRate, bucketsPerSecond);
}
