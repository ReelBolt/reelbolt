/** A recording fake AudioContext: node kinds, the parameters set on them and every connection. */

export interface FakeParam {
  value: number;
}

export class FakeNode {
  readonly outputs: FakeNode[] = [];
  disconnected = 0;
  constructor(
    readonly kind: string,
    readonly ctx: FakeContext,
  ) {}
  /** Every connection with its output and input index (channel routing), beside `outputs`. */
  readonly routes: { dest: FakeNode; output: number; input: number }[] = [];
  connect(dest: FakeNode, output = 0, input = 0): FakeNode {
    this.outputs.push(dest);
    this.routes.push({ dest, output, input });
    return dest;
  }
  disconnect(): void {
    this.disconnected++;
    this.outputs.length = 0;
  }
}

const param = (v = 1): FakeParam => ({ value: v });

export class FakeContext {
  readonly nodes: FakeNode[] = [];
  currentTime = 0;
  state = 'running';
  sampleRate = 48000;
  readonly destination: FakeNode;
  readonly modules: string[] = [];
  audioWorklet: { addModule(url: string): Promise<void> } | undefined = {
    addModule: async (url: string) => {
      this.modules.push(url);
    },
  };

  constructor() {
    this.destination = this.make('destination');
  }

  private make<T extends object>(kind: string, extra?: T): FakeNode & T {
    const n = new FakeNode(kind, this) as FakeNode & T;
    if (extra) Object.assign(n, extra);
    this.nodes.push(n);
    return n;
  }

  createGain() {
    return this.make('gain', { gain: param(1) });
  }
  createStereoPanner() {
    return this.make('panner', { pan: param(0) });
  }
  createBiquadFilter() {
    return this.make('biquad', { type: 'lowpass', frequency: param(350), gain: param(0), Q: param(1) });
  }
  createDynamicsCompressor() {
    return this.make('compressor', { threshold: param(-24), knee: param(30), ratio: param(12), attack: param(0.003), release: param(0.25) });
  }
  createWaveShaper() {
    return this.make('waveshaper', { curve: null as Float32Array | null });
  }
  createAnalyser() {
    return this.make('analyser', { fftSize: 2048, smoothingTimeConstant: 0, getFloatTimeDomainData: () => undefined });
  }
  createChannelSplitter() {
    return this.make('splitter');
  }
  createChannelMerger() {
    return this.make('merger');
  }
  createBufferSource() {
    return this.make('source', { buffer: null, playbackRate: param(1), start: () => undefined, stop: () => undefined });
  }
  async resume() {
    this.state = 'running';
  }
  async close() {
    return undefined;
  }
  async decodeAudioData() {
    return null;
  }

  worklet(name: string): FakeNode & { port: { messages: unknown[]; postMessage(m: unknown): void } } {
    const messages: unknown[] = [];
    return this.make(`worklet:${name}`, { port: { messages, postMessage: (m: unknown) => void messages.push(m) } });
  }

  ofKind(kind: string): FakeNode[] {
    return this.nodes.filter((n) => n.kind === kind);
  }

  /** Whether `to` is reachable from `from` along connections. */
  reaches(from: FakeNode, to: FakeNode): boolean {
    const seen = new Set<FakeNode>();
    const walk = (n: FakeNode): boolean => {
      if (n === to) return true;
      if (seen.has(n)) return false;
      seen.add(n);
      return n.outputs.some(walk);
    };
    return walk(from);
  }

  /** True when any node can reach itself. */
  hasCycle(): boolean {
    const state = new Map<FakeNode, 1 | 2>();
    const visit = (n: FakeNode): boolean => {
      if (state.get(n) === 1) return true;
      if (state.get(n) === 2) return false;
      state.set(n, 1);
      for (const o of n.outputs) if (visit(o)) return true;
      state.set(n, 2);
      return false;
    };
    return this.nodes.some(visit);
  }
}
