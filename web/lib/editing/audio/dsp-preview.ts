/**
 * R08.6: the pure mapping from the stored dynamics stages (track and bus) to Web Audio parameters,
 * plus the honest "preview versus render" table. Field names, units and defaults are the C#
 * `AudioMixBuilder.ProcessingChain` / `Structure.cs` ones (dB in the document, milliseconds for
 * times); the render turns them into ffmpeg `agate`, `deesser`, `acompressor` and `alimiter`.
 *
 * The preview graph that consumes these lives in `../preview/dsp-graph.ts`. Nothing here touches
 * the browser, so every row is unit-tested.
 *
 * Choices (written down so the next person need not rediscover them):
 *   - Compressor: `DynamicsCompressorNode` plus a make-up `GainNode`. The node adds an automatic
 *     make-up gain of its own that cannot be switched off, so the mapping subtracts it again
 *     ({@link autoMakeupDb}) and the user's make-up value is the only make-up.
 *   - Limiter: a fast compressor (ratio 20, no knee) followed by a `WaveShaper` that clamps at the
 *     ceiling. An `AudioWorklet` with lookahead would be closer, but the clamp guarantees the
 *     ceiling is never exceeded even without the worklet, and works wherever the node does.
 *   - Gate and de-esser need per-sample gain computing, so they run in an `AudioWorklet`
 *     (`dsp-worklet-source.ts`); without `AudioWorklet` they stay "heard after render only".
 */

import type { AudioCompressor, AudioDeesser, AudioDynamics, AudioGate, AudioLimiter, EditTimeline } from '../types';
import { AUDIO_RANGES } from '../engine/ops';

/** The four dynamics stages, in the order the render applies them (gate, de-esser, compressor, limiter). */
export type DspStage = 'gate' | 'deesser' | 'compressor' | 'limiter';
export const DSP_STAGE_ORDER: readonly DspStage[] = ['gate', 'deesser', 'compressor', 'limiter'];

export const DSP_STAGE_LABEL: Record<DspStage, string> = {
  gate: 'Noise gate',
  deesser: 'De-esser',
  compressor: 'Compressor',
  limiter: 'Limiter',
};

/** The C# record defaults (`Structure.cs`), also what the panel adds when a stage is switched on. */
export const DSP_DEFAULTS = {
  compressor: { thresholdDb: -18, ratio: 3, attackMs: 20, releaseMs: 250, makeupDb: 0 },
  limiter: { ceilingDb: -1 },
  gate: { thresholdDb: -40, ratio: 2, attackMs: 20, releaseMs: 250 },
  deesser: { intensity: 0.5 },
} as const;

/** What this browser can run. */
export interface DspSupport {
  /** `DynamicsCompressorNode`, `WaveShaperNode`, `GainNode`: the compressor and limiter. */
  nodes: boolean;
  /** `AudioWorklet`: the gate and the de-esser. */
  worklet: boolean;
}

export const NO_DSP_SUPPORT: DspSupport = { nodes: false, worklet: false };

let workletRefused = false;
/** Called by the graph when the worklet module failed to load, so the badge stops claiming the gate and de-esser are heard. */
export function markWorkletUnavailable(): void {
  workletRefused = true;
}
/** Test seam: forget a recorded worklet refusal. */
export function resetWorkletRefusal(): void {
  workletRefused = false;
}

/** Feature-detects the preview DSP. Safe on the server and in tests (returns no support). */
export function detectDspSupport(): DspSupport {
  const g = globalThis as { DynamicsCompressorNode?: unknown; WaveShaperNode?: unknown; AudioWorkletNode?: unknown; AudioContext?: { prototype?: object } };
  const nodes = typeof g.DynamicsCompressorNode === 'function' && typeof g.WaveShaperNode === 'function';
  const worklet = typeof g.AudioWorkletNode === 'function' && !!g.AudioContext?.prototype && 'audioWorklet' in g.AudioContext.prototype;
  return { nodes, worklet: worklet && !workletRefused };
}

export function stageHeard(stage: DspStage, support: DspSupport): boolean {
  return stage === 'gate' || stage === 'deesser' ? support.worklet : support.nodes;
}

export const HEARD_IN_PREVIEW = 'Heard in preview, approximately';
export const HEARD_AFTER_RENDER = 'Heard after render only';
export function stageStatusText(stage: DspStage, support: DspSupport): string {
  return stageHeard(stage, support) ? HEARD_IN_PREVIEW : HEARD_AFTER_RENDER;
}

/** The plain-language line shown in the mixer. */
export const PREVIEW_NOTE = 'Preview is close to the delivered sound; the render is exact.';

/** Where the preview and the render differ, per stage (surfaced in docs/editor.md and as tooltips). */
export const PREVIEW_DIFFERENCES: Record<DspStage, string> = {
  compressor: 'The preview uses the browser compressor: a fixed soft knee, its own attack and release curves (times above one second are capped at one second), and no sidechain filter. Settings are mapped so the loudness change is close; the render is exact.',
  limiter: 'The preview uses a fast compressor and a hard clip at the ceiling, without the render\'s lookahead, so very fast peaks may sound slightly harsher.',
  gate: 'The preview gate follows the same threshold, ratio, attack and release, but its smoothing curve differs from the render\'s gate.',
  deesser: 'The preview de-esser turns down a band around 7 kHz when it gets loud. The render uses a different detector, so the amount of reduction is approximate.',
};

export function previewDifferenceNote(stage: DspStage): string {
  return PREVIEW_DIFFERENCES[stage];
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
const finite = (v: number | undefined, fallback: number) => (typeof v === 'number' && Number.isFinite(v) ? v : fallback);
export const dbToLin = (db: number) => Math.pow(10, db / 20);

/** The knee the render's `acompressor` uses by default (2.83 linear, about 9 dB). */
export const COMPRESSOR_KNEE_DB = 9;
/** `DynamicsCompressorNode` applies `pow(1 / gainAtFullScale, 0.6)` of make-up on its own. */
const AUTO_MAKEUP_EXPONENT = 0.6;

/** The automatic make-up (dB) the browser compressor adds for these settings; subtracted again by the mapping. */
export function autoMakeupDb(thresholdDb: number, ratio: number, kneeDb: number): number {
  if (thresholdDb + kneeDb >= 0 || ratio <= 1) return 0;
  const gainAtFullScaleDb = thresholdDb * (1 - 1 / ratio); // negative: what 0 dBFS in comes out lower by
  return -AUTO_MAKEUP_EXPONENT * gainAtFullScaleDb;
}

export interface CompressorNodeParams {
  thresholdDb: number;
  kneeDb: number;
  ratio: number;
  attackSec: number;
  releaseSec: number;
  /** The make-up `GainNode`'s linear gain: the user's make-up minus the node's own. */
  makeupGain: number;
}

export function compressorNodeParams(c: AudioCompressor | null | undefined): CompressorNodeParams {
  const d = DSP_DEFAULTS.compressor;
  const r = AUDIO_RANGES.compressor;
  const thresholdDb = clamp(finite(c?.thresholdDb, d.thresholdDb), r.thresholdDb[0], r.thresholdDb[1]);
  const ratio = clamp(finite(c?.ratio, d.ratio), r.ratio[0], r.ratio[1]);
  const makeupDb = clamp(finite(c?.makeupDb, d.makeupDb), r.makeupDb[0], r.makeupDb[1]);
  return {
    thresholdDb,
    kneeDb: COMPRESSOR_KNEE_DB,
    ratio,
    attackSec: Math.min(1, clamp(finite(c?.attackMs, d.attackMs), r.attackMs[0], r.attackMs[1]) / 1000),
    releaseSec: Math.min(1, clamp(finite(c?.releaseMs, d.releaseMs), r.releaseMs[0], r.releaseMs[1]) / 1000),
    makeupGain: dbToLin(makeupDb - autoMakeupDb(thresholdDb, ratio, COMPRESSOR_KNEE_DB)),
  };
}

export interface LimiterNodeParams {
  /** The fast compressor in front of the clamp. */
  compressor: CompressorNodeParams;
  /** The linear level the clamp holds the signal under. */
  ceilingLin: number;
}

export function limiterNodeParams(l: AudioLimiter | null | undefined): LimiterNodeParams {
  const r = AUDIO_RANGES.limiter;
  const ceilingDb = clamp(finite(l?.ceilingDb, DSP_DEFAULTS.limiter.ceilingDb), r.ceilingDb[0], r.ceilingDb[1]);
  const thresholdDb = ceilingDb;
  return {
    compressor: {
      thresholdDb,
      kneeDb: 0,
      ratio: 20,
      attackSec: 0.001,
      releaseSec: 0.05,
      makeupGain: dbToLin(-autoMakeupDb(thresholdDb, 20, 0)),
    },
    ceilingLin: dbToLin(ceilingDb),
  };
}

/** The `WaveShaper` curve for a hard ceiling: identity inside +/- ceiling, flat outside (inputs beyond +/-1 take the end values). */
export function clampCurve(ceilingLin: number, points = 2049): Float32Array<ArrayBuffer> {
  const n = Math.max(3, points | 1);
  const curve = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const x = (i / (n - 1)) * 2 - 1;
    curve[i] = clamp(x, -ceilingLin, ceilingLin);
  }
  return curve;
}

export interface GateWorkletParams {
  thresholdLin: number;
  ratio: number;
  attackMs: number;
  releaseMs: number;
}

export function gateWorkletParams(g: AudioGate | null | undefined): GateWorkletParams {
  const d = DSP_DEFAULTS.gate;
  const r = AUDIO_RANGES.gate;
  return {
    thresholdLin: dbToLin(clamp(finite(g?.thresholdDb, d.thresholdDb), r.thresholdDb[0], r.thresholdDb[1])),
    ratio: clamp(finite(g?.ratio, d.ratio), r.ratio[0], r.ratio[1]),
    attackMs: clamp(finite(g?.attackMs, d.attackMs), r.attackMs[0], r.attackMs[1]),
    releaseMs: clamp(finite(g?.releaseMs, d.releaseMs), r.releaseMs[0], r.releaseMs[1]),
  };
}

export interface DeesserWorkletParams {
  bandHz: number;
  thresholdLin: number;
  ratio: number;
  attackMs: number;
  releaseMs: number;
}

/** Intensity 0 does nothing (ratio 1); 1 is a strong 8:1 on the sibilance band. */
export function deesserWorkletParams(de: AudioDeesser | null | undefined): DeesserWorkletParams {
  const i = clamp(finite(de?.intensity, DSP_DEFAULTS.deesser.intensity), AUDIO_RANGES.deesser.intensity[0], AUDIO_RANGES.deesser.intensity[1]);
  return { bandHz: 7000, thresholdLin: dbToLin(-24), ratio: 1 + 7 * i, attackMs: 2, releaseMs: 40 };
}

/** The stages present in a dynamics section, in render order. */
export function presentStages(dynamics: AudioDynamics | null | undefined): DspStage[] {
  if (!dynamics) return [];
  return DSP_STAGE_ORDER.filter((s) => !!dynamics[s]);
}

/** The stages this browser cannot run, among those present. */
export function unheardStages(dynamics: AudioDynamics | null | undefined, support: DspSupport): DspStage[] {
  return presentStages(dynamics).filter((s) => !stageHeard(s, support));
}

export const MAX_BUS_DEPTH = 4;

export type BusPlan =
  | { ok: true; /** Bus ids, a bus after every bus that feeds it. */ order: string[]; parentOf: Map<string, string | null> }
  | { ok: false; reason: string };

export const BUS_FALLBACK_NOTE = 'The buses could not be set up for the preview, so every track plays straight to the master here. The render still uses the buses.';

/**
 * Routes the document's buses: track to bus to master. A missing output bus feeds the master (as the
 * render does); a loop or a chain deeper than four levels cannot be built, and the preview falls
 * back to direct-to-master with {@link BUS_FALLBACK_NOTE}.
 */
export function planBusRouting(doc: Pick<EditTimeline, 'buses'>): BusPlan {
  const buses = doc.buses ?? [];
  const byId = new Map(buses.map((b) => [b.id, b] as const));
  const parentOf = new Map<string, string | null>();
  for (const b of buses) parentOf.set(b.id, b.outputBusId && byId.has(b.outputBusId) && b.outputBusId !== b.id ? b.outputBusId : b.outputBusId === b.id ? b.id : null);
  const depthOf = new Map<string, number>();
  for (const b of buses) {
    const seen = new Set<string>();
    let cur: string | null = b.id;
    let depth = 0;
    while (cur) {
      if (seen.has(cur)) return { ok: false, reason: 'A bus feeds itself.' };
      seen.add(cur);
      depth++;
      cur = parentOf.get(cur) ?? null;
    }
    if (depth > MAX_BUS_DEPTH) return { ok: false, reason: 'Buses are nested too deeply.' };
    depthOf.set(b.id, depth);
  }
  // A bus connects to its parent's input, so parents (shallower chains) are built first.
  const order = [...byId.keys()].sort((a, b) => (depthOf.get(a) ?? 0) - (depthOf.get(b) ?? 0));
  return { ok: true, order, parentOf };
}

/** The bus that actually receives a track, or null for the master. */
export function trackBusId(doc: Pick<EditTimeline, 'buses'>, busId: string | null | undefined): string | null {
  if (!busId) return null;
  return (doc.buses ?? []).some((b) => b.id === busId) ? busId : null;
}
