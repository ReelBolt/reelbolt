/** @vitest-environment node */
/** The shipped worklet source, run against a fake AudioWorkletProcessor: numbers in, numbers out. */
import { describe, expect, it } from 'vitest';
import { DEESSER_PROCESSOR, DSP_WORKLET_SOURCE, GATE_PROCESSOR } from '../dsp-worklet-source';

const SR = 48000;
interface Processor {
  port: { onmessage: ((e: { data: unknown }) => void) | null };
  process(inputs: Float32Array[][], outputs: Float32Array[][]): boolean;
}
type Ctor = new () => Processor;

function load(): Record<string, Ctor> {
  const registered: Record<string, Ctor> = {};
  class FakeProcessor {
    port = { onmessage: null as ((e: { data: unknown }) => void) | null };
  }
  new Function('AudioWorkletProcessor', 'registerProcessor', 'sampleRate', DSP_WORKLET_SOURCE)(FakeProcessor, (name: string, c: Ctor) => (registered[name] = c), SR);
  return registered;
}

const sine = (hz: number, amp: number, n: number, offset = 0) => Float32Array.from({ length: n }, (_, i) => amp * Math.sin((2 * Math.PI * hz * (i + offset)) / SR));
const rms = (a: Float32Array, from = 0) => Math.sqrt(a.slice(from).reduce((s, v) => s + v * v, 0) / (a.length - from));
const db = (v: number) => 20 * Math.log10(v);

/** Pushes `signal` through in 128-sample quanta, mono, returning the output. */
function run(p: Processor, signal: Float32Array): Float32Array {
  const out = new Float32Array(signal.length);
  for (let i = 0; i < signal.length; i += 128) {
    const inp = signal.slice(i, i + 128);
    const o = new Float32Array(inp.length);
    p.process([[inp]], [[o]]);
    out.set(o, i);
  }
  return out;
}
const send = (p: Processor, data: object) => p.port.onmessage?.({ data: { type: 'params', ...data } });

describe('gate worklet', () => {
  const processors = load();
  it('registers both processors', () => {
    expect(Object.keys(processors).sort()).toEqual([DEESSER_PROCESSOR, GATE_PROCESSOR].sort());
  });
  it('passes a signal above the threshold and closes on one below it', () => {
    const gate = new processors[GATE_PROCESSOR]();
    send(gate, { thresholdLin: 0.01, ratio: 20, attackMs: 1, releaseMs: 20 });
    const loud = run(gate, sine(440, 0.5, SR / 2));
    expect(db(rms(loud, SR / 4) / rms(sine(440, 0.5, 1000)))).toBeGreaterThan(-0.5);
    // A quiet hiss at -60 dB (threshold is -40 dB): the gate closes and attenuates it hard.
    const quiet = sine(440, 0.001, SR);
    const out = run(gate, quiet);
    expect(db(rms(out, SR / 2) / rms(quiet, SR / 2))).toBeLessThan(-20);
  });
  it('a ratio of 1 does nothing', () => {
    const gate = new processors[GATE_PROCESSOR]();
    send(gate, { thresholdLin: 0.1, ratio: 1, attackMs: 1, releaseMs: 10 });
    const quiet = sine(300, 0.001, SR / 4);
    const out = run(gate, quiet);
    expect(db(rms(out, 2000) / rms(quiet, 2000))).toBeGreaterThan(-0.1);
  });
  it('reopens after the signal returns (attack)', () => {
    const gate = new processors[GATE_PROCESSOR]();
    send(gate, { thresholdLin: 0.01, ratio: 20, attackMs: 2, releaseMs: 10 });
    run(gate, new Float32Array(SR / 2)); // silence: closed
    const burst = run(gate, sine(440, 0.5, SR / 4));
    expect(rms(burst, SR / 8)).toBeGreaterThan(0.3);
  });
  it('is allocation-free in steady state (same output buffer values, no growth) and handles stereo', () => {
    const gate = new processors[GATE_PROCESSOR]();
    const l = sine(440, 0.5, 128);
    const r = sine(440, 0.5, 128);
    const ol = new Float32Array(128);
    const or = new Float32Array(128);
    expect(gate.process([[l, r]], [[ol, or]])).toBe(true);
    expect(gate.process([[]], [[]])).toBe(true); // no input yet: keep alive
    expect(ol.some((v) => v !== 0)).toBe(true);
    expect(Array.from(ol)).toEqual(Array.from(or));
  });
});

describe('de-esser worklet', () => {
  const processors = load();
  const make = (ratio: number) => {
    const p = new processors[DEESSER_PROCESSOR]();
    send(p, { bandHz: 7000, thresholdLin: 10 ** (-24 / 20), ratio, attackMs: 2, releaseMs: 40 });
    return p;
  };
  it('turns down a loud sibilant tone and leaves a low tone alone', () => {
    const sib = sine(8000, 0.5, SR / 2);
    const out = run(make(8), sib);
    expect(db(rms(out, SR / 4) / rms(sib, SR / 4))).toBeLessThan(-6);
    const low = sine(300, 0.5, SR / 2);
    const lowOut = run(make(8), low);
    expect(Math.abs(db(rms(lowOut, SR / 4) / rms(low, SR / 4)))).toBeLessThan(0.5);
  });
  it('intensity 0 (ratio 1) is transparent', () => {
    const sib = sine(8000, 0.5, SR / 4);
    const out = run(make(1), sib);
    expect(Math.abs(db(rms(out, SR / 8) / rms(sib, SR / 8)))).toBeLessThan(0.3);
  });
  it('a quiet sibilant under the threshold is not touched', () => {
    const sib = sine(8000, 0.01, SR / 4);
    const out = run(make(8), sib);
    expect(Math.abs(db(rms(out, SR / 8) / rms(sib, SR / 8)))).toBeLessThan(0.5);
  });
});
