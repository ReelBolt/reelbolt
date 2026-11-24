/**
 * R08.6: the AudioWorklet module for the gate and the de-esser, as source text. It is loaded through
 * a Blob URL (`context.audioWorklet.addModule`), because a worklet module cannot go through the
 * bundler the way `new Worker(new URL(...))` does. If the Blob load is refused (a strict CSP) the
 * graph reports no worklet support and the stages stay "heard after render only".
 *
 * The text defines two processors. Tests run this same text against a fake `AudioWorkletProcessor`
 * and call `process` directly, so what is shipped is what is tested.
 *
 * Parameters arrive by message (`{ type: 'params', ... }`), never per sample; `process` allocates
 * nothing once the channel state exists.
 */

export const GATE_PROCESSOR = 'rb-gate';
export const DEESSER_PROCESSOR = 'rb-deesser';

export const DSP_WORKLET_SOURCE = `
class RbGate extends AudioWorkletProcessor {
  constructor() {
    super();
    this.threshold = 0.01;
    this.ratio = 2;
    this.attackCoef = 0;
    this.releaseCoef = 0;
    this.env = 0;
    this.gain = 1;
    this.setTimes(20, 250);
    this.port.onmessage = (e) => {
      const m = e.data;
      if (!m || m.type !== 'params') return;
      this.threshold = Math.max(1e-6, m.thresholdLin);
      this.ratio = Math.max(1, m.ratio);
      this.setTimes(m.attackMs, m.releaseMs);
    };
  }
  setTimes(attackMs, releaseMs) {
    this.attackCoef = Math.exp(-1 / (Math.max(0.1, attackMs) * 0.001 * sampleRate));
    this.releaseCoef = Math.exp(-1 / (Math.max(0.1, releaseMs) * 0.001 * sampleRate));
  }
  process(inputs, outputs) {
    const input = inputs[0];
    const output = outputs[0];
    if (!input || input.length === 0) return true;
    const frames = input[0].length;
    const channels = input.length;
    for (let i = 0; i < frames; i++) {
      let level = 0;
      for (let c = 0; c < channels; c++) {
        const a = Math.abs(input[c][i]);
        if (a > level) level = a;
      }
      // Peak follower: instant up, release down.
      this.env = level > this.env ? level : level + (this.env - level) * this.releaseCoef;
      // Downward expander: below the threshold the level falls ratio times as fast.
      const target = this.env >= this.threshold ? 1 : Math.pow(this.env / this.threshold, this.ratio - 1);
      // Opening (gain rising) uses the attack time, closing uses the release time.
      const coef = target > this.gain ? this.attackCoef : this.releaseCoef;
      this.gain = target + (this.gain - target) * coef;
      for (let c = 0; c < channels; c++) output[c][i] = input[c][i] * this.gain;
    }
    return true;
  }
}

class RbDeesser extends AudioWorkletProcessor {
  constructor() {
    super();
    this.threshold = 0.063;
    this.ratio = 4.5;
    this.attackCoef = 0;
    this.releaseCoef = 0;
    this.env = 0;
    this.gain = 1;
    // Bandpass biquad coefficients (RBJ, constant 0 dB peak), one state pair per channel (grown on demand, then reused).
    this.b0 = 0; this.b1 = 0; this.b2 = 0; this.a1 = 0; this.a2 = 0;
    this.z1 = new Float64Array(2);
    this.z2 = new Float64Array(2);
    this.configure(7000, 2, 40);
    this.port.onmessage = (e) => {
      const m = e.data;
      if (!m || m.type !== 'params') return;
      this.threshold = Math.max(1e-6, m.thresholdLin);
      this.ratio = Math.max(1, m.ratio);
      this.configure(m.bandHz, m.attackMs, m.releaseMs);
    };
  }
  configure(bandHz, attackMs, releaseMs) {
    this.attackCoef = Math.exp(-1 / (Math.max(0.1, attackMs) * 0.001 * sampleRate));
    this.releaseCoef = Math.exp(-1 / (Math.max(0.1, releaseMs) * 0.001 * sampleRate));
    const w0 = 2 * Math.PI * Math.min(bandHz, sampleRate * 0.45) / sampleRate;
    const cos = Math.cos(w0);
    const alpha = Math.sin(w0) / (2 * 1.0);
    const a0 = 1 + alpha;
    this.b0 = alpha / a0;
    this.b1 = 0;
    this.b2 = -alpha / a0;
    this.a1 = (-2 * cos) / a0;
    this.a2 = (1 - alpha) / a0;
  }
  process(inputs, outputs) {
    const input = inputs[0];
    const output = outputs[0];
    if (!input || input.length === 0) return true;
    const frames = input[0].length;
    const channels = input.length;
    if (this.z1.length < channels) { this.z1 = new Float64Array(channels); this.z2 = new Float64Array(channels); }
    for (let i = 0; i < frames; i++) {
      // Dynamic cut: band = bandpass(x) is detected and turned down; the rest passes untouched. The bandpass has zero phase at its centre, so removing (1 - gain) of it cuts the centre by exactly gain.
      let level = 0;
      for (let c = 0; c < channels; c++) {
        const x = input[c][i];
        const y = this.b0 * x + this.z1[c];
        this.z1[c] = this.b1 * x - this.a1 * y + this.z2[c];
        this.z2[c] = this.b2 * x - this.a2 * y;
        output[c][i] = y; // band, scaled below
        const a = Math.abs(y);
        if (a > level) level = a;
      }
      this.env = level > this.env ? this.env + (level - this.env) * (1 - this.attackCoef) : level + (this.env - level) * this.releaseCoef;
      const target = this.env > this.threshold ? Math.pow(this.threshold / this.env, 1 - 1 / this.ratio) : 1;
      this.gain = target + (this.gain - target) * (target < this.gain ? this.attackCoef : this.releaseCoef);
      for (let c = 0; c < channels; c++) {
        const band = output[c][i];
        output[c][i] = input[c][i] - band * (1 - this.gain);
      }
    }
    return true;
  }
}

registerProcessor('${GATE_PROCESSOR}', RbGate);
registerProcessor('${DEESSER_PROCESSOR}', RbDeesser);
`;
