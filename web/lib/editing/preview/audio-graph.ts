/**
 * The WebAudio graph: per audio track `clip gain → track bus (fader → pan → EQ) → analyser →
 * master → splitter → two analysers → destination`, with clip buffers scheduled against the audio
 * clock. **Audio is the master clock** (`spike/BUDGET.md` §5): the session derives the playhead
 * from `AudioContext.currentTime`, never from the decoder.
 *
 * Decoding: a clip's proxy (or original) is fetched as an `ArrayBuffer` and decoded with
 * `decodeAudioData` once per URL, cached; a reversed clip (P5) plays a reversed copy of that
 * buffer, cached too. A clip whose buffer lands after its start time joins at the frame the audio
 * clock is at when it is scheduled (PA-5), so first-play sound is never late against the picture. The gain envelope (`gainDb` keyframes × the clip's fade ramps) is applied with
 * `setValueCurveAtTime` sampled per output frame over the clip's audible span, so an automation
 * curve is heard as drawn rather than as a staircase of block updates. The track fader (P5,
 * `track.gainDb`) takes the same treatment on the track bus; pan is a `StereoPannerNode`; the EQ
 * is a chain of `BiquadFilterNode`s rebuilt on every `play`.
 *
 * Meters (P5): one `AnalyserNode` per track bus and two on the master (left/right) feed
 * {@link AudioGraph.meters}. They are POLLED, so the picture is sampled rather than gap-free —
 * the render's `loudnorm` measurement is the authoritative number, and the mixer says so.
 *
 * Browser-only; nothing here is imported by a test. The scheduling math it relies on
 * (`activeAudioAt`, `evaluateKeyframed`, `dbToGain`, `clipFadeGain`) lives in `scheduler.ts` and
 * IS tested, as is the LUFS meter that consumes the master samples (`../audio/lufs.ts`).
 */

import type { AudioBus, ChannelMap, EditTimeline, Keyframed, MediaClip, Track } from '../types';
import { detectDspSupport, markWorkletUnavailable, planBusRouting, trackBusId, type DspSupport } from '../audio/dsp-preview';
import { buildDynamicsChain, ensureDspWorklet, type DspChain } from './dsp-graph';
import type { AudioMeterSnapshot } from './session';
import { activeAudioAt, clipFadeGain, dbToGain, evaluateKeyframed, fpsOf, type FlagsFor } from './scheduler';

export interface AudioGraphOptions {
  /** Resolves an asset to a URL whose bytes `decodeAudioData` can read (the proxy, else the original). */
  urlFor(clip: MediaClip): string | null;
  flags: FlagsFor;
  /** Overrides feature detection (tests). */
  dspSupport?: DspSupport;
}

interface TrackBus {
  /** Where clips connect. */
  input: GainNode;
  /** The fader (static or automated). */
  fader: GainNode;
  /** The mute/solo gate (1 or 0), separate from the fader so a toggle never fights the automation. */
  gate: GainNode;
  panner: StereoPannerNode | null;
  filters: BiquadFilterNode[];
  analyser: AnalyserNode | null;
  /** R08.6: the track's dynamics stages, when any can be heard. */
  dsp: DspChain | null;
}

/** R08.6: a submix bus: `input → fader → [eq] → [pan] → [dynamics] → parent bus or master`. */
interface BusNode {
  input: GainNode;
  fader: GainNode;
  panner: StereoPannerNode | null;
  filters: BiquadFilterNode[];
  dsp: DspChain | null;
}

const ANALYSER_SIZE = 2048;
/** A bus fader at or below this is silent. */
const BUS_SILENT_DB = -60;
/** The most the smoothed audio clock may lead the device's `currentTime` (a bit over one buffer step). */
export const MAX_CLOCK_LEAD_SEC = 0.04;
/** At most this many audio clips are decoded ahead of Play (decoded PCM is ~11 MB per minute of stereo 48 kHz). */
const PRELOAD_MAX_CLIPS = 24;
/** How far ahead of "now" a late-joining source is scheduled, so its start is never in the past. */
const SCHEDULE_LEAD_SEC = 0.02;

/** [from source channel, to output channel, gain] per channel map word (`AudioMixBuilder.ChannelMapFilter`). */
export const CHANNEL_ROUTES: Readonly<Record<Exclude<ChannelMap, 'stereo'>, ReadonlyArray<readonly [number, number, number]>>> = {
  left: [[0, 0, 1], [0, 1, 1]],
  right: [[1, 0, 1], [1, 1, 1]],
  mono: [[0, 0, 0.5], [1, 0, 0.5], [0, 1, 0.5], [1, 1, 0.5]],
  swap: [[1, 0, 1], [0, 1, 1]],
};

export class AudioGraph {
  private readonly context: AudioContext;
  private readonly master: GainNode;
  private readonly splitter: ChannelSplitterNode | null;
  private readonly masterAnalysers: AnalyserNode[];
  private readonly buses = new Map<string, TrackBus>();
  /** R08.6: submix buses, rebuilt with the track buses on every play. */
  private readonly busNodes = new Map<string, BusNode>();
  private support: DspSupport;
  /** A worklet node can only be made once its module has loaded, which the first play awaits. */
  private workletLoaded = false;
  private busFallback = false;
  private readonly buffers = new Map<string, Promise<AudioBuffer | null>>();
  private readonly playing: AudioBufferSourceNode[] = [];
  private startedAt = 0;
  private startFrame = 0;
  private speed = 0;
  /** Bumped by every play/stop, so a decode that lands after a newer play schedules nothing. */
  private playToken = 0;
  private clockOffset: number | null = null;

  constructor(
    context: AudioContext,
    private readonly options: AudioGraphOptions,
  ) {
    this.context = context;
    this.support = options.dspSupport ?? detectDspSupport();
    this.master = context.createGain();
    // Meters are optional equipment: a minimal context (tests, an exotic browser) without splitter
    // or analyser nodes still plays and still keeps the clock.
    this.splitter = typeof context.createChannelSplitter === 'function' ? context.createChannelSplitter(2) : null;
    this.masterAnalysers = this.splitter ? [0, 1].map(() => this.analyser()).filter((a): a is AnalyserNode => a !== null) : [];
    if (this.splitter && this.masterAnalysers.length === 2) {
      this.master.connect(this.splitter);
      this.splitter.connect(this.masterAnalysers[0], 0);
      this.splitter.connect(this.masterAnalysers[1], 1);
    }
    this.master.connect(context.destination);
    exposeTestHook(this);
  }

  /** What the preview can run right now (the worklet entry turns false when its module fails to load). */
  dspSupport(): DspSupport {
    return this.support;
  }

  /** True when the buses could not be set up and every track plays straight to the master. */
  usesBusFallback(): boolean {
    return this.busFallback;
  }

  private analyser(): AnalyserNode | null {
    if (typeof this.context.createAnalyser !== 'function') return null;
    const analyser = this.context.createAnalyser();
    analyser.fftSize = ANALYSER_SIZE;
    analyser.smoothingTimeConstant = 0;
    return analyser;
  }

  get currentTime(): number {
    return this.context.currentTime;
  }

  /**
   * The timeline frame the audio clock says we are at, or `null` when it cannot be the master: not
   * playing, or the context is not `running` (autoplay policy kept it suspended, or the browser has
   * no audio sink). A suspended context's `currentTime` does not advance, and a clock that never
   * advances would freeze the video — so the session falls back to the wall clock instead.
   */
  playheadFrame(fps: number): number | null {
    if (this.speed === 0 || this.context.state !== 'running') return null;
    // Sources are scheduled 50 ms ahead (`startedAt`), so for that long the raw clock reads BEFORE
    // the start frame. Clamp: a forward clock cannot be earlier than where playback began — an
    // unclamped negative frame read as "ran off the start" and paused playback on the first tick.
    const elapsed = Math.max(0, this.clockNow() - this.startedAt);
    return this.startFrame + elapsed * this.speed * fps;
  }

  /**
   * The audio clock in seconds, smoothed. `currentTime` advances in device-buffer steps (10.7 or 21.3 ms here); read once
   * per 16.7 ms animation frame it made `floor(frame)` skip a picture on a 60 fps timeline (profiled: a steady 4-40
   * percent "dropped" with an idle main thread). The true clock is `currentTime` plus up to one step, so the smoothed
   * clock follows the wall clock but is held to [currentTime, currentTime + MAX_CLOCK_LEAD_SEC]: it can never fall behind
   * what the device has played, and when the audio runs slow (an underrun) it cannot run ahead of the sound by more than
   * the lead; beyond it, it resyncs to the audio at once.
   */
  private clockNow(): number {
    const wall = performance.now() / 1000;
    const raw = this.context.currentTime;
    if (this.clockOffset === null) this.clockOffset = raw - wall;
    let lead = wall + this.clockOffset - raw;
    if (lead > MAX_CLOCK_LEAD_SEC) lead = MAX_CLOCK_LEAD_SEC;
    else if (lead < 0) lead = 0;
    this.clockOffset = raw - wall + lead;
    return wall + this.clockOffset;
  }

  isPlaying(): boolean {
    return this.speed !== 0;
  }

  /** The track's bus, (re)built to the track's current fader/pan/EQ. */
  /** What a chain may build now: before the module has loaded (an edit before the first play) the worklet stages wait for the play's rebuild. */
  private chainSupport(): DspSupport {
    return this.workletLoaded ? this.support : { ...this.support, worklet: false };
  }

  private busFor(track: Track, rebuild: boolean): TrackBus {
    let bus = this.buses.get(track.id);
    if (bus && !rebuild) return bus;
    if (bus) {
      bus.input.disconnect();
      bus.fader.disconnect();
      bus.gate.disconnect();
      bus.panner?.disconnect();
      for (const f of bus.filters) f.disconnect();
      bus.analyser?.disconnect();
      bus.dsp?.dispose();
    }
    const input = this.context.createGain();
    const fader = this.context.createGain();
    const gate = this.context.createGain();
    const analyser = this.analyser();
    const filters: BiquadFilterNode[] = [];
    const eq = track.eq ?? null;
    if (eq && typeof this.context.createBiquadFilter === 'function') {
      if (typeof eq.lowCutHz === 'number') filters.push(this.biquad('highpass', eq.lowCutHz, 0, 0.707));
      if (typeof eq.highCutHz === 'number') filters.push(this.biquad('lowpass', eq.highCutHz, 0, 0.707));
      for (const band of (eq.bands ?? []).slice(0, 4)) {
        if (band.gainDb !== 0) filters.push(this.biquad('peaking', band.frequencyHz, band.gainDb, typeof band.q === 'number' && band.q > 0 ? band.q : 1));
      }
    }
    const pan = typeof track.pan === 'number' && track.pan !== 0 && typeof this.context.createStereoPanner === 'function' ? this.context.createStereoPanner() : null;
    if (pan) pan.pan.value = Math.max(-1, Math.min(1, track.pan as number));

    // input → fader → gate → [filters…] → [pan] → analyser → master
    let tail: AudioNode = gate;
    input.connect(fader);
    fader.connect(gate);
    for (const f of filters) {
      tail.connect(f);
      tail = f;
    }
    if (pan) {
      tail.connect(pan);
      tail = pan;
    }
    // R08.6: the track's dynamics come after EQ and pan, as in the render's chain.
    const dsp = buildDynamicsChain(this.context, track.dynamics, this.chainSupport());
    if (dsp) {
      tail.connect(dsp.input);
      tail = dsp.output;
    }
    if (analyser) {
      tail.connect(analyser);
      tail = analyser;
    }
    tail.connect(this.destinationFor(track));

    bus = { input, fader, gate, panner: pan, filters, analyser, dsp };
    this.buses.set(track.id, bus);
    return bus;
  }

  /** Where a track's output goes: its bus when the routing was built, else the master. */
  private destinationFor(track: Track): AudioNode {
    const busId = this.busFallback ? null : trackBusId(this.currentDoc ?? { buses: [] }, track.busId);
    return (busId ? this.busNodes.get(busId)?.input : undefined) ?? this.master;
  }

  private currentDoc: EditTimeline | null = null;

  /**
   * R08.6: (re)builds the submix buses of the document (parents first so a child connects to a
   * bus that exists), each `input → fader → eq → pan → dynamics`. A document whose buses loop or
   * nest too deeply, or a context that throws while building, falls back to direct-to-master.
   */
  private buildBuses(doc: EditTimeline, fromFrame: number, whenSec: number, fps: number, speed: number): void {
    this.disposeBuses();
    this.currentDoc = doc;
    this.busFallback = false;
    const buses = doc.buses ?? [];
    if (buses.length === 0) return;
    const plan = planBusRouting(doc);
    if (!plan.ok) {
      this.busFallback = true;
      return;
    }
    try {
      const byId = new Map(buses.map((b) => [b.id, b] as const));
      for (const id of plan.order) {
        const bus = byId.get(id)!;
        const node = this.createBusNode(bus);
        const parent = plan.parentOf.get(id);
        const parentNode = parent ? this.busNodes.get(parent) : undefined;
        const tail = node.dsp ? node.dsp.output : node.panner ?? node.filters[node.filters.length - 1] ?? node.fader;
        tail.connect(parentNode ? parentNode.input : this.master);
        this.applyFader(node, bus, fromFrame, whenSec, fps, speed, timelineEnd(doc));
        // A bus at its quietest setting is silent (buses have no mute field of their own).
        if (!bus.gainDb?.keys?.length && (bus.gainDb?.value ?? 0) <= BUS_SILENT_DB) node.fader.gain.value = 0;
        this.busNodes.set(id, node);
      }
    } catch {
      this.disposeBuses();
      this.busFallback = true;
    }
  }

  private createBusNode(bus: AudioBus): BusNode {
    const input = this.context.createGain();
    const fader = this.context.createGain();
    input.connect(fader);
    let tail: AudioNode = fader;
    const filters: BiquadFilterNode[] = [];
    const eq = bus.eq ?? null;
    if (eq && typeof this.context.createBiquadFilter === 'function') {
      if (typeof eq.lowCutHz === 'number') filters.push(this.biquad('highpass', eq.lowCutHz, 0, 0.707));
      if (typeof eq.highCutHz === 'number') filters.push(this.biquad('lowpass', eq.highCutHz, 0, 0.707));
      for (const band of (eq.bands ?? []).slice(0, 4)) {
        if (band.gainDb !== 0) filters.push(this.biquad('peaking', band.frequencyHz, band.gainDb, typeof band.q === 'number' && band.q > 0 ? band.q : 1));
      }
    }
    for (const f of filters) {
      tail.connect(f);
      tail = f;
    }
    let panner: StereoPannerNode | null = null;
    if (typeof bus.pan === 'number' && bus.pan !== 0 && typeof this.context.createStereoPanner === 'function') {
      panner = this.context.createStereoPanner();
      panner.pan.value = Math.max(-1, Math.min(1, bus.pan));
      tail.connect(panner);
      tail = panner;
    }
    const dsp = buildDynamicsChain(this.context, bus.dynamics, this.chainSupport());
    if (dsp) tail.connect(dsp.input);
    return { input, fader, panner, filters, dsp };
  }

  private disposeBuses(): void {
    for (const b of this.busNodes.values()) {
      b.input.disconnect();
      b.fader.disconnect();
      b.panner?.disconnect();
      for (const f of b.filters) f.disconnect();
      b.dsp?.dispose();
    }
    this.busNodes.clear();
  }

  private biquad(type: BiquadFilterType, frequency: number, gain: number, q: number): BiquadFilterNode {
    const node = this.context.createBiquadFilter();
    node.type = type;
    node.frequency.value = Math.max(20, Math.min(20000, frequency));
    node.gain.value = gain;
    node.Q.value = q;
    return node;
  }

  /**
   * The decoded buffer for a clip's CURRENT url, cached per url: a source with no url yet is not
   * cached as silence, and a proxy that arrives later is decoded on the next play.
   */
  private bufferFor(clip: MediaClip, reversed: boolean): Promise<AudioBuffer | null> {
    const url = this.options.urlFor(clip);
    if (!url) return Promise.resolve(null);
    const key = reversed ? `${url}#reversed` : url;
    let pending = this.buffers.get(key);
    if (!pending) {
      pending = reversed
        ? this.bufferFor(clip, false).then((forward) => (forward ? reverseBuffer(this.context, forward) : null))
        : fetch(url)
            .then((r) => (r.ok ? r.arrayBuffer() : Promise.reject(new Error(`HTTP ${r.status}`))))
            .then((bytes) => this.context.decodeAudioData(bytes))
            .catch(() => null);
      this.buffers.set(key, pending);
    }
    return pending;
  }

  /**
   * Fetches and decodes the audio of every audible clip in the background (cached per URL), so that
   * pressing Play finds the buffers ready. Without it the first play downloaded and decoded each
   * source (a 1080p original when no proxy exists) with the video already running, and the audio
   * joined seconds late. Idempotent: a URL already fetched or in flight costs a map lookup.
   */
  preload(doc: EditTimeline): void {
    let count = 0;
    for (const track of doc.tracks) {
      if (track.kind !== 'audio') continue;
      for (const item of track.items) {
        if (item.type !== 'mediaClip' || item.hold === true || (item as { enabled?: boolean }).enabled === false) continue;
        if (++count > PRELOAD_MAX_CLIPS) return;
        void this.bufferFor(item, false);
      }
    }
  }

  /**
   * Starts playback from `fromFrame` at `speed` (forward only — WebAudio cannot play a buffer in
   * reverse without resampling, so reverse shuttling is video-only and silent, by design; a clip
   * marked `reverse` plays a reversed copy of its buffer and is therefore heard).
   */
  async play(doc: EditTimeline, fromFrame: number, speed: number): Promise<void> {
    this.stop();
    if (speed <= 0) return;
    if (this.context.state === 'suspended') {
      try {
        await this.context.resume();
      } catch {
        /* autoplay policy: the graph stays silent, the clock still runs */
      }
    }

    // R08.6: load the gate/de-esser worklet once; when it cannot load, those stages stay render-only.
    if (this.support.worklet) {
      if (await ensureDspWorklet(this.context)) {
        this.workletLoaded = true;
      } else {
        this.support = { ...this.support, worklet: false };
        markWorkletUnavailable();
      }
    }

    const fps = fpsOf(doc.settings.rate);
    const now = this.context.currentTime + 0.05;
    this.startedAt = now;
    this.clockOffset = null;
    this.startFrame = fromFrame;
    this.speed = speed;
    const token = ++this.playToken;

    // Start every fetch/decode at once (cached ones resolve immediately); scheduling below still goes in order.
    const buffers = new Map<string, Promise<AudioBuffer | null>>();
    for (const track of doc.tracks) {
      if (track.kind !== 'audio') continue;
      for (const item of track.items) {
        if (item.type !== 'mediaClip' || item.recordOut <= fromFrame || item.hold === true) continue;
        if ((item as { enabled?: boolean }).enabled === false) continue;
        buffers.set(item.id, this.bufferFor(item, item.reverse === true));
      }
    }
    this.buildBuses(doc, fromFrame, now, fps, speed);
    for (const track of doc.tracks) {
      if (track.kind !== 'audio') continue;
      const bus = this.busFor(track, true);
      this.applyFader(bus, track, fromFrame, now, fps, speed, timelineEnd(doc));
      for (const item of track.items) {
        if (item.type !== 'mediaClip') continue;
        if (item.recordOut <= fromFrame) continue;
        if ((item as { enabled?: boolean }).enabled === false) continue;
        if (item.hold === true) continue; // a freeze frame is silent
        const reversed = item.reverse === true;
        const buffer = await buffers.get(item.id)!;
        if (!buffer || token !== this.playToken) continue;

        // PA-5: the decode above may have taken seconds of audio-clock time. Schedule against a
        // FRESH reading of the clock: a clip whose start has already passed joins at the frame the
        // clock is at now (offset advanced to match), never from its first sample, late.
        const nominalStartFrame = Math.max(item.recordIn, fromFrame);
        const nominalWhen = now + ((nominalStartFrame - fromFrame) / fps) / speed;
        const lateSec = Math.max(0, this.context.currentTime + SCHEDULE_LEAD_SEC - nominalWhen);
        const clipStartFrame = nominalStartFrame + lateSec * speed * fps;
        if (clipStartFrame >= item.recordOut) continue;
        const whenSec = nominalWhen + lateSec;
        const sourceIn = typeof item.sourceIn === 'number' ? item.sourceIn : 0;
        const playbackRate = typeof item.playbackRate === 'number' && item.playbackRate > 0 ? item.playbackRate : 1;
        const durationSec = ((item.recordOut - clipStartFrame) * playbackRate) / fps;
        // Forward: read from the source position of the first audible frame. Reversed: the
        // reversed buffer's position for source time t is (D - t), and the span starts at the clip's
        // HIGHEST source frame, which is sourceIn + (recordOut - clipStartFrame) × rate.
        const offsetSec = reversed
          ? buffer.duration - (sourceIn + (item.recordOut - clipStartFrame) * playbackRate) / fps
          : (sourceIn + (clipStartFrame - item.recordIn) * playbackRate) / fps;
        if (durationSec <= 0 || offsetSec >= buffer.duration || offsetSec < 0) continue;

        const source = this.context.createBufferSource();
        source.buffer = buffer;
        source.playbackRate.value = playbackRate * speed;

        const clipGain = this.context.createGain();
        this.applyEnvelope(clipGain, item, clipStartFrame, item.recordOut, whenSec, fps, speed);
        this.channelMapped(source, item.channelMap).connect(clipGain);
        clipGain.connect(bus.input);

        try {
          source.start(whenSec, offsetSec, Math.min(durationSec, buffer.duration - offsetSec));
          this.playing.push(source);
        } catch {
          /* a bad schedule is a silent clip, never a thrown frame */
        }
      }
    }
    this.applyTrackFlags(doc);
  }

  /**
   * The clip's channel map, as the render's `pan` filter applies it right after `aformat=channel_layouts=stereo`
   * (`AudioMixBuilder.ChannelMapFilter`): `left` = c0 on both sides, `right` = c1 on both, `mono` = half of each on
   * both, `swap` = sides exchanged. Returns the node to connect onward (the source itself for `stereo`).
   */
  private channelMapped(source: AudioNode, map: ChannelMap | null | undefined): AudioNode {
    if (!map || map === 'stereo') return source;
    const ctx = this.context;
    if (typeof ctx.createChannelSplitter !== 'function' || typeof ctx.createChannelMerger !== 'function') return source;
    // The render forces stereo first, so a mono file is on both sides before the map: the same up-mix here.
    const stereo = ctx.createGain();
    stereo.channelCount = 2;
    stereo.channelCountMode = 'explicit';
    stereo.channelInterpretation = 'speakers';
    const splitter = ctx.createChannelSplitter(2);
    const merger = ctx.createChannelMerger(2);
    source.connect(stereo);
    stereo.connect(splitter);
    const route = (from: number, to: number, gain = 1) => {
      if (gain === 1) {
        splitter.connect(merger, from, to);
        return;
      }
      const g = ctx.createGain();
      g.gain.value = gain;
      splitter.connect(g, from);
      g.connect(merger, 0, to);
    };
    for (const [from, to, gain] of CHANNEL_ROUTES[map]) route(from, to, gain);
    return merger;
  }

  /** Re-reads mute/solo/enabled from the flags provider — called on every toggle. */
  applyTrackFlags(doc: EditTimeline): void {
    const audioTracks = doc.tracks.filter((t) => t.kind === 'audio');
    const anySolo = audioTracks.some((t) => this.options.flags(t).solo);
    for (const track of audioTracks) {
      const f = this.options.flags(track);
      const audible = f.enabled && !f.mute && (!anySolo || f.solo);
      this.busFor(track, false).gate.gain.value = audible ? 1 : 0;
    }
  }

  /** The track fader: a constant, or the automation curve sampled over the play span (P5). */
  private applyFader(bus: { fader: GainNode }, track: { gainDb?: Keyframed<number> | null }, fromFrame: number, whenSec: number, fps: number, speed: number, endFrame: number): void {
    const gainDb = track.gainDb ?? null;
    if (!gainDb || !gainDb.keys || gainDb.keys.length === 0) {
      bus.fader.gain.value = dbToGain(evaluateKeyframed(gainDb, fromFrame, 0));
      return;
    }
    const toFrame = Math.max(fromFrame + 1, endFrame);
    const frames = Math.max(2, toFrame - fromFrame);
    const step = Math.max(1, Math.ceil(frames / 2048));
    const samples: number[] = [];
    for (let f = fromFrame; f <= toFrame; f += step) samples.push(dbToGain(evaluateKeyframed(gainDb, f, 0)));
    if (samples.length < 2) samples.push(samples[0] ?? 1);
    try {
      bus.fader.gain.setValueCurveAtTime(new Float32Array(samples), whenSec, Math.max(0.001, frames / fps / speed));
    } catch {
      bus.fader.gain.value = samples[0];
    }
  }

  private applyEnvelope(
    node: GainNode,
    clip: MediaClip,
    fromFrame: number,
    toFrame: number,
    whenSec: number,
    fps: number,
    speed: number,
  ): void {
    const gainDb = clip.gainDb ?? null;
    const hasKeys = !!gainDb && !!gainDb.keys && gainDb.keys.length > 0;
    const hasFades = (clip.fadeInFrames ?? 0) > 0 || (clip.fadeOutFrames ?? 0) > 0;
    if (!hasKeys && !hasFades) {
      node.gain.value = dbToGain(evaluateKeyframed(gainDb, fromFrame, 0));
      return;
    }
    const frames = Math.max(2, toFrame - fromFrame);
    const step = Math.max(1, Math.ceil(frames / 2048));
    const samples: number[] = [];
    for (let f = fromFrame; f <= toFrame; f += step) samples.push(dbToGain(evaluateKeyframed(gainDb, f, 0)) * clipFadeGain(clip, f));
    if (samples.length < 2) samples.push(samples[0] ?? 1);
    const durationSec = frames / fps / speed;
    try {
      node.gain.setValueCurveAtTime(new Float32Array(samples), whenSec, Math.max(0.001, durationSec));
    } catch {
      node.gain.value = samples[0];
    }
  }

  stop(): void {
    for (const source of this.playing) {
      try {
        source.stop();
      } catch {
        /* already stopped */
      }
      source.disconnect();
    }
    this.playing.length = 0;
    this.speed = 0;
    this.playToken += 1;
  }

  /** The gains the ledger/UI may want to show: per-track linear gain at a frame (0 when inaudible). */
  gainsAt(doc: EditTimeline, frame: number): { trackId: string; gain: number }[] {
    return activeAudioAt(doc, frame, this.options.flags).map((a) => ({ trackId: a.track.id, gain: a.gain }));
  }

  /** P5: one sampled look at every meter. Cheap enough to call per animation frame. */
  meters(): AudioMeterSnapshot {
    const tracks: { trackId: string; peak: number }[] = [];
    const scratch = new Float32Array(ANALYSER_SIZE);
    for (const [trackId, bus] of this.buses) {
      if (!bus.analyser) continue;
      bus.analyser.getFloatTimeDomainData(scratch);
      let peak = 0;
      for (let i = 0; i < scratch.length; i++) {
        const v = Math.abs(scratch[i]);
        if (v > peak) peak = v;
      }
      tracks.push({ trackId, peak });
    }
    const master = this.masterAnalysers.map((analyser) => {
      const out = new Float32Array(ANALYSER_SIZE);
      analyser.getFloatTimeDomainData(out);
      return out;
    });
    return { sampleRate: this.context.sampleRate, tracks, master };
  }

  dispose(): void {
    this.stop();
    for (const bus of this.buses.values()) {
      bus.input.disconnect();
      bus.fader.disconnect();
      bus.gate.disconnect();
      bus.panner?.disconnect();
      for (const f of bus.filters) f.disconnect();
      bus.analyser?.disconnect();
      bus.dsp?.dispose();
    }
    this.buses.clear();
    this.disposeBuses();
    this.master.disconnect();
    void this.context.close().catch(() => undefined);
  }
}

function timelineEnd(doc: EditTimeline): number {
  let end = 0;
  for (const track of doc.tracks) for (const item of track.items) if (item.recordOut > end) end = item.recordOut;
  return end;
}

/** A sample-reversed copy of a decoded buffer (P5 reverse playback). */
function reverseBuffer(context: AudioContext, forward: AudioBuffer): AudioBuffer {
  const out = context.createBuffer(forward.numberOfChannels, forward.length, forward.sampleRate);
  for (let c = 0; c < forward.numberOfChannels; c++) {
    const src = forward.getChannelData(c);
    const dst = out.getChannelData(c);
    for (let i = 0, j = src.length - 1; i < src.length; i++, j--) dst[i] = src[j];
  }
  return out;
}

/**
 * Test-only: when an e2e run sets `window.__RB_TEST_HOOKS__ = true` before the app loads, the graph
 * publishes `window.__rbAudioGraph` so a spec can read the live peak telemetry (the same sampled
 * meters the mixer draws) and the DSP support. It has no effect, and costs nothing, otherwise.
 */
export interface AudioGraphTestHook {
  meters(): AudioMeterSnapshot;
  dspSupport(): DspSupport;
  usesBusFallback(): boolean;
}

function exposeTestHook(graph: AudioGraph): void {
  const w = globalThis as { __RB_TEST_HOOKS__?: boolean; __rbAudioGraph?: AudioGraphTestHook };
  if (w.__RB_TEST_HOOKS__ !== true) return;
  w.__rbAudioGraph = { meters: () => graph.meters(), dspSupport: () => graph.dspSupport(), usesBusFallback: () => graph.usesBusFallback() };
}
