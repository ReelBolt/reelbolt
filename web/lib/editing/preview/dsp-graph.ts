/**
 * R08.6: builds the preview's dynamics chain (gate, de-esser, compressor, limiter, in the render's
 * order) from a track's or bus's stored stages, using the pure mapping in `../audio/dsp-preview.ts`.
 *
 * Nodes are created once, when a chain is built (on play or when the stages change), never per
 * tick; worklets are told their numbers by one message. A stage this browser cannot run is left out
 * (the parity badge and the stage status say so), never faked.
 */

import type { AudioDynamics } from '../types';
import {
  clampCurve,
  compressorNodeParams,
  deesserWorkletParams,
  gateWorkletParams,
  limiterNodeParams,
  stageHeard,
  type CompressorNodeParams,
  type DspStage,
  type DspSupport,
  DSP_STAGE_ORDER,
} from '../audio/dsp-preview';
import { DEESSER_PROCESSOR, DSP_WORKLET_SOURCE, GATE_PROCESSOR } from './dsp-worklet-source';

export interface DspChain {
  input: AudioNode;
  output: AudioNode;
  /** Every node created, for disposal and for tests. */
  nodes: AudioNode[];
  /** The stages that were built. */
  stages: DspStage[];
  dispose(): void;
}

export interface DspGraphDeps {
  /** Creates a worklet node; defaults to `new AudioWorkletNode`. */
  workletNode?: (context: BaseAudioContext, name: string) => AudioWorkletNode;
}

const loaded = new WeakMap<object, Promise<boolean>>();

/** Loads the gate/de-esser worklet module into the context once; resolves false when it cannot. */
export function ensureDspWorklet(context: BaseAudioContext): Promise<boolean> {
  let pending = loaded.get(context);
  if (!pending) {
    pending = (async () => {
      try {
        if (!context.audioWorklet) return false;
        const url = URL.createObjectURL(new Blob([DSP_WORKLET_SOURCE], { type: 'text/javascript' }));
        try {
          await context.audioWorklet.addModule(url);
        } finally {
          URL.revokeObjectURL(url);
        }
        return true;
      } catch {
        return false;
      }
    })();
    loaded.set(context, pending);
  }
  return pending;
}

function applyCompressor(node: DynamicsCompressorNode, p: CompressorNodeParams): void {
  node.threshold.value = p.thresholdDb;
  node.knee.value = p.kneeDb;
  node.ratio.value = p.ratio;
  node.attack.value = p.attackSec;
  node.release.value = p.releaseSec;
}

/** The chain for these stages, or null when none of them can be heard (pass straight through). */
export function buildDynamicsChain(
  context: BaseAudioContext,
  dynamics: AudioDynamics | null | undefined,
  support: DspSupport,
  deps: DspGraphDeps = {},
): DspChain | null {
  if (!dynamics) return null;
  const nodes: AudioNode[] = [];
  const stages: DspStage[] = [];
  let head: AudioNode | null = null;
  let tail: AudioNode | null = null;
  const append = (first: AudioNode, last: AudioNode = first) => {
    nodes.push(first);
    if (last !== first) nodes.push(last);
    if (tail) tail.connect(first);
    else head = first;
    tail = last;
  };
  const makeWorklet = (name: string): AudioWorkletNode =>
    deps.workletNode ? deps.workletNode(context, name) : new AudioWorkletNode(context, name, { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2] });

  for (const stage of DSP_STAGE_ORDER) {
    const stored = dynamics[stage];
    if (!stored || !stageHeard(stage, support)) continue;
    if (stage === 'gate') {
      const node = makeWorklet(GATE_PROCESSOR);
      node.port.postMessage({ type: 'params', ...gateWorkletParams(dynamics.gate) });
      append(node);
    } else if (stage === 'deesser') {
      const node = makeWorklet(DEESSER_PROCESSOR);
      node.port.postMessage({ type: 'params', ...deesserWorkletParams(dynamics.deesser) });
      append(node);
    } else if (stage === 'compressor') {
      const p = compressorNodeParams(dynamics.compressor);
      const comp = context.createDynamicsCompressor();
      applyCompressor(comp, p);
      const makeup = context.createGain();
      makeup.gain.value = p.makeupGain;
      comp.connect(makeup);
      nodes.push(comp);
      if (tail) tail.connect(comp);
      else head = comp;
      nodes.push(makeup);
      tail = makeup;
    } else {
      const p = limiterNodeParams(dynamics.limiter);
      const comp = context.createDynamicsCompressor();
      applyCompressor(comp, p.compressor);
      const makeup = context.createGain();
      makeup.gain.value = p.compressor.makeupGain;
      const clip = context.createWaveShaper();
      clip.curve = clampCurve(p.ceilingLin);
      comp.connect(makeup);
      makeup.connect(clip);
      nodes.push(comp, makeup, clip);
      if (tail) tail.connect(comp);
      else head = comp;
      tail = clip;
    }
    stages.push(stage);
  }
  if (!head || !tail) return null;
  const input: AudioNode = head;
  const output: AudioNode = tail;
  return {
    input,
    output,
    nodes,
    stages,
    dispose() {
      for (const n of nodes) {
        try {
          n.disconnect();
        } catch {
          /* already disconnected */
        }
      }
    },
  };
}
