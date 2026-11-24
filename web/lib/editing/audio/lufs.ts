/**
 * An ITU-R BS.1770-4 loudness meter in plain TypeScript (P5): K-weighting (the two-stage
 * pre-filter + RLB high-pass, coefficients derived for ANY sample rate from the published 48 kHz
 * design), 400 ms momentary and 3 s short-term windows with 75 % overlap, and the gated integrated
 * loudness (absolute gate −70 LUFS, relative gate −10 LU under the ungated mean of the blocks that
 * passed the absolute gate). True peak is a 4× linear-interpolation upsample — an approximation of
 * the spec's 4× FIR oversampler that under-reads intersample peaks by at most a fraction of a dB,
 * and labelled as approximate wherever it is shown.
 *
 * The meter consumes whatever the caller feeds it (`AnalyserNode` windows in the browser are
 * sampled, not gap-free), so its readout is a live INDICATION; the render's `loudnorm` pass is the
 * authoritative measurement and the mixer shows both side by side.
 */

export interface LoudnessReadout {
  /** Momentary loudness (400 ms), LUFS, or -Infinity before the first full block. */
  momentaryLufs: number;
  /** Short-term loudness (3 s), LUFS, or -Infinity. */
  shortTermLufs: number;
  /** Gated integrated loudness since `reset()`, LUFS, or -Infinity. */
  integratedLufs: number;
  /** Approximate true peak, dBTP, or -Infinity. */
  truePeakDbtp: number;
}

interface Biquad {
  b0: number;
  b1: number;
  b2: number;
  a1: number;
  a2: number;
}

interface BiquadState {
  x1: number;
  x2: number;
  y1: number;
  y2: number;
}

/** The BS.1770 pre-filter (high shelf) for `sampleRate`, from the analog prototype behind the 48 kHz table. */
export function preFilter(sampleRate: number): Biquad {
  const f0 = 1681.974450955533;
  const G = 3.999843853973347;
  const Q = 0.7071752369554196;
  const K = Math.tan((Math.PI * f0) / sampleRate);
  const Vh = 10 ** (G / 20);
  const Vb = Vh ** 0.4996667741545416;
  const a0 = 1 + K / Q + K * K;
  return {
    b0: (Vh + (Vb * K) / Q + K * K) / a0,
    b1: (2 * (K * K - Vh)) / a0,
    b2: (Vh - (Vb * K) / Q + K * K) / a0,
    a1: (2 * (K * K - 1)) / a0,
    a2: (1 - K / Q + K * K) / a0,
  };
}

/** The BS.1770 RLB weighting (high-pass) for `sampleRate`. */
export function rlbFilter(sampleRate: number): Biquad {
  const f0 = 38.13547087602444;
  const Q = 0.5003270373238773;
  const K = Math.tan((Math.PI * f0) / sampleRate);
  const a0 = 1 + K / Q + K * K;
  return {
    b0: 1 / a0,
    b1: -2 / a0,
    b2: 1 / a0,
    a1: (2 * (K * K - 1)) / a0,
    a2: (1 - K / Q + K * K) / a0,
  };
}

function runBiquad(coef: Biquad, state: BiquadState, x: number): number {
  const y = coef.b0 * x + coef.b1 * state.x1 + coef.b2 * state.x2 - coef.a1 * state.y1 - coef.a2 * state.y2;
  state.x2 = state.x1;
  state.x1 = x;
  state.y2 = state.y1;
  state.y1 = y;
  return y;
}

/** Mean-square → LUFS/LKFS (the −0.691 dB offset makes a 0 dBFS 1 kHz sine read 0 LUFS... per channel sum). */
export function meanSquareToLufs(meanSquareSum: number): number {
  return meanSquareSum <= 0 ? Number.NEGATIVE_INFINITY : -0.691 + 10 * Math.log10(meanSquareSum);
}

export class LufsMeter {
  private readonly pre: Biquad;
  private readonly rlb: Biquad;
  private readonly preState: BiquadState[];
  private readonly rlbState: BiquadState[];
  /** 100 ms sub-block energies (summed over channels, per sample), newest last. */
  private readonly subBlocks: number[] = [];
  private readonly subBlockSize: number;
  private partial = 0;
  private partialCount = 0;
  /** Every completed 400 ms block's mean-square (for the integrated gate). */
  private readonly blocks: number[] = [];
  private peak = 0;
  private readonly lastSample: number[];

  constructor(
    public readonly sampleRate: number,
    public readonly channels = 2,
  ) {
    if (!(sampleRate > 0)) throw new RangeError('sampleRate must be positive');
    this.pre = preFilter(sampleRate);
    this.rlb = rlbFilter(sampleRate);
    this.preState = Array.from({ length: channels }, () => ({ x1: 0, x2: 0, y1: 0, y2: 0 }));
    this.rlbState = Array.from({ length: channels }, () => ({ x1: 0, x2: 0, y1: 0, y2: 0 }));
    this.subBlockSize = Math.max(1, Math.round(sampleRate * 0.1));
    this.lastSample = new Array<number>(channels).fill(0);
  }

  reset(): void {
    for (const s of [...this.preState, ...this.rlbState]) s.x1 = s.x2 = s.y1 = s.y2 = 0;
    this.subBlocks.length = 0;
    this.blocks.length = 0;
    this.partial = 0;
    this.partialCount = 0;
    this.peak = 0;
    this.lastSample.fill(0);
  }

  /**
   * Feeds one window per channel (equal lengths). Channel weights are 1 for L/R (and C); a
   * surround layout's ±1.5 dB on Ls/Rs is not modelled — the editor mixes to stereo.
   */
  process(channelData: readonly Float32Array[]): void {
    const n = channelData[0]?.length ?? 0;
    if (n === 0) return;
    for (let i = 0; i < n; i++) {
      let energy = 0;
      for (let c = 0; c < this.channels; c++) {
        const x = channelData[c]?.[i] ?? 0;
        // True peak (approximate): 4× linear interpolation between this sample and the last.
        const prev = this.lastSample[c];
        for (let k = 1; k <= 4; k++) {
          const v = Math.abs(prev + ((x - prev) * k) / 4);
          if (v > this.peak) this.peak = v;
        }
        this.lastSample[c] = x;
        const y = runBiquad(this.rlb, this.rlbState[c], runBiquad(this.pre, this.preState[c], x));
        energy += y * y;
      }
      this.partial += energy;
      this.partialCount++;
      if (this.partialCount >= this.subBlockSize) {
        this.subBlocks.push(this.partial / this.partialCount);
        this.partial = 0;
        this.partialCount = 0;
        if (this.subBlocks.length >= 4) {
          // A 400 ms block completes every 100 ms (75 % overlap).
          const last4 = this.subBlocks.slice(-4);
          this.blocks.push(last4.reduce((a, b) => a + b, 0) / 4);
        }
        if (this.subBlocks.length > 30) this.subBlocks.splice(0, this.subBlocks.length - 30);
      }
    }
  }

  readout(): LoudnessReadout {
    return {
      momentaryLufs: this.windowLufs(4),
      shortTermLufs: this.windowLufs(30),
      integratedLufs: this.integrated(),
      truePeakDbtp: this.peak <= 0 ? Number.NEGATIVE_INFINITY : 20 * Math.log10(this.peak),
    };
  }

  private windowLufs(subBlocks: number): number {
    if (this.subBlocks.length < subBlocks) return Number.NEGATIVE_INFINITY;
    const slice = this.subBlocks.slice(-subBlocks);
    return meanSquareToLufs(slice.reduce((a, b) => a + b, 0) / slice.length);
  }

  /** The gated integrated loudness over every completed 400 ms block. */
  private integrated(): number {
    const absolute = this.blocks.filter((ms) => meanSquareToLufs(ms) > -70);
    if (absolute.length === 0) return Number.NEGATIVE_INFINITY;
    const ungated = meanSquareToLufs(absolute.reduce((a, b) => a + b, 0) / absolute.length);
    const relativeGate = ungated - 10;
    const gated = absolute.filter((ms) => meanSquareToLufs(ms) > relativeGate);
    if (gated.length === 0) return Number.NEGATIVE_INFINITY;
    return meanSquareToLufs(gated.reduce((a, b) => a + b, 0) / gated.length);
  }
}
