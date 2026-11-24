/**
 * The live preview session — §3.5's compositor, built on the spike (a) decisions:
 *
 *   - demux: mediabunny (`mediabunny-source.ts`)                 · decode: one lane per layer
 *   - look-ahead 12 / ceiling 18, invariant enforced (`decoder-pool.ts`)
 *   - composite: WebGL2 textured quads back-to-front (`gl-compositor.ts`), 2D overlay pass on top
 *   - audio: WebAudio graph, **audio clock is master** (`audio-graph.ts`)
 *   - a dropped-frame ledger the status bar shows (`drop-frame-ledger.ts`)
 *
 * ## Missed deadlines — exactly what happens (BUDGET.md §5)
 *
 * A deadline for frame `i` is missed when the render tick that owns it finds any layer without
 * frame `i` decoded, or when whole slots were skipped between two ticks. On a miss the stack is
 * NEVER degraded: the compositor re-draws each late layer's held texture (a repeat), audio is never
 * paused or re-timed, the ledger records the drop, and the pool keeps feeding so recovery is
 * automatic. The counter is real from day one so a slow GPU fails honestly.
 *
 * A repeat is only ever the same clip's picture. After a cut, a clip whose source is still opening
 * or whose first frame is not decoded has nothing to repeat: the layer is drawn empty and reported
 * as a missing layer (`notDecoded` — "Preparing preview…"), never as the previous clip's last frame.
 * While playing, the tick does not wait for a source to open; it starts the open and moves on.
 *
 * ## What the session reads
 *
 * `getDoc()` is called on every tick, so an edit shows on the very next frame without the session
 * being rebuilt. `flags(track)` supplies the EFFECTIVE track flags (document + UI overrides).
 * `proxyUrlFor(clip)` maps an asset to a decodable URL; a clip whose asset has no proxy yet is
 * shown as a labelled hole (the overlay says "proxy not ready"), never as a wrong frame.
 */

import type { EditTimeline, InsertClip, MediaClip, SegmentationMask } from '../types';
import { AudioGraph } from './audio-graph';
import { DEFAULT_POOL_CONFIG, DecoderPool, type DecoderPoolConfig, type FrameSource } from './decoder-pool';
import { DropFrameLedger } from './drop-frame-ledger';
import { RateGovernor } from './rate-governor';
import { fitRenderSize, ResolutionGovernor } from './resolution-governor';

/** Width (px) from which a decoded source counts as heavy (4K): the rate governor may then halve a 30 fps timeline. */
const HEAVY_SOURCE_WIDTH = 3000;
import type { ViewingLutSpec } from '../color/viewer-color';
import { overlayRunsOf, type OverlayRun } from './overlay-stack';
import { keyerParamsOf } from './keyer';
import { createGlCompositor, imageSize, previewEffectsOf, rendersPadded, isSoftwareRenderer, type Compositor, type CompositorLayer } from './gl-compositor';
import { hasTransform, layerTransformAt, sourceTimeSec } from './scheduler';
import { cutoutEffectOf, matteSourceKindFor, previewRegionEffectsOf, type MaskFrame, type PreviewRegionEffect } from './region-effects';
import { insertQuadAt, pinQuadOf, shapeParamsOf, type Quad } from './live-geometry';
import { maskBindsClip, maskPreviewUrl, matteFor, rotateMatte, type Matte, type SegmentationTracksLookup } from './segmentation-tracks';
import { rasterizeOverlays, type OverlayItem } from './overlay-raster';
import { activeLayersAt, fpsOf, sourceFrameFor, type FlagsFor } from './scheduler';
import type { ContextLoss, MissingLayer, PlaybackStats, PreviewSession } from './session';
import { REASON_CONTEXT_FAILED, REASON_CONTEXT_LOST } from './fault-reasons';
import { timelineEndFrame } from '../transport/navigation';
import { effectiveGrade } from '../color/grade';
import { previewGradeNodesOf } from '../color/preview-grade';
import { needsGradePass, richGradePlanOf, type GradeLutResolver } from './grade-pass-plan';

export interface LiveSessionOptions {
  canvas: HTMLCanvasElement;
  getDoc(): EditTimeline;
  flags: FlagsFor;
  proxyUrlFor(clip: MediaClip): string | null;
  /** The url the PICTURE decodes when it differs from `proxyUrlFor` (the stabilised proxy); the audio graph keeps `proxyUrlFor`. */
  pictureUrlFor?(clip: MediaClip): string | null;
  /** Injectable for tests; defaults to the mediabunny source. */
  openSource?: (url: string, fps: number, targetWidth: number, rotation?: number) => Promise<FrameSource | null>;
  poolConfig?: DecoderPoolConfig;
  playbackResolution?: 'full' | 'half' | 'quarter';
  /**
   * Auto quality: the picture is drawn no larger than the viewer shows it (`setDisplayLongSide`), with its long side
   * capped at `maxLongSide` device pixels. Absent = `playbackResolution` is an exact fraction of the timeline's size.
   */
  fitDisplay?: { maxLongSide: number };
  /**
   * While playing, draw at a smaller size when frames are being skipped (and go back to the full playback size on
   * pause). Every layer and effect stays on. Default true; false pins the size (tests, an explicit "Full" pick).
   */
  adaptive?: boolean;
  audioContext?: AudioContext | null;
  requestFrame?: (cb: (t: number) => void) => number;
  cancelFrame?: (handle: number) => void;
  now?: () => number;
  /** P7: when it returns true every grade is bypassed in the preview (the before/after toggle). */
  getGradeBypass?: () => boolean;
  /** The tracked boxes of a segmentation mask's analysis (the preview of the mask follows these boxes). */
  segmentationTracksFor?: SegmentationTracksLookup;
  /** The URL of a track's mask-preview video (T-006), or null when the host cannot serve one; absent = boxes only. */
  maskPreviewUrlFor?: (analysisStepResultId: string, trackId: string) => string | null;
  /**
   * The width in pixels of the clip's source AS THE PREVIEW SHOWS IT (after the clip's `sourceRotation`: a quarter turn
   * swaps the sides), when known. Scales the mask feather from source pixels to the decoded proxy.
   */
  sourceWidthFor?: (clip: MediaClip) => number | null | undefined;
  /**
   * The clip's camera conversion for the compositor (`inputStageOf`): the profile, and whether the proxy being played
   * already carries it. Null/absent = no input stage (no profile, an HDR clip, or the option is not wired).
   */
  inputStageFor?: (clip: MediaClip) => { profile: string; baked: boolean } | null;
  /** The viewing LUT (the last step of the live viewer only), read at every present; null = off. Never in a readback. */
  getViewingLut?: () => ViewingLutSpec | null;
  /**
   * L3: the parsed `.cube` of a grade node's LUT (null while it loads or when it is refused). A node whose LUT is not
   * here yet draws without it; the host re-presents when the file arrives. Absent = LUT nodes never preview.
   */
  gradeLutFor?: GradeLutResolver;
}

/**
 * How long a lost graphics context may stay "Reconnecting..." before the viewer gives up and offers Try live preview
 * again. Browsers restore a recoverable context within a second or two; 5 s is well past that yet short enough that
 * nobody stares at a spinner that will never end.
 */
export const CONTEXT_RESTORE_WAIT_MS = 5000;

export const REDUCED_QUALITY_SOFTWARE = 'This browser draws with software graphics, so the preview is scaled down to keep up.';
export const REDUCED_QUALITY_ADAPTIVE = 'Playing at a smaller size so every effect keeps up. Pause to see the full picture.';

const RESOLUTION_SCALE: Record<'full' | 'half' | 'quarter', number> = { full: 1, half: 0.5, quarter: 0.25 };

export function createLivePreviewSession(options: LiveSessionOptions): PreviewSession | null {
  // The graphics context state the viewer shows over the frame; null while it is fine.
  let contextLoss: ContextLoss | null = null;
  let restoreTimer: ReturnType<typeof setTimeout> | null = null;
  const clearRestoreTimer = () => {
    if (restoreTimer !== null) clearTimeout(restoreTimer);
    restoreTimer = null;
  };
  const compositor: Compositor | null = createGlCompositor(options.canvas, {
    onContextLost: () => {
      if (disposed) return;
      contextLoss = { state: 'lost', message: REASON_CONTEXT_LOST };
      clearRestoreTimer();
      restoreTimer = setTimeout(() => {
        restoreTimer = null;
        if (disposed || contextLoss?.state !== 'lost') return;
        contextLoss = { state: 'failed', message: REASON_CONTEXT_FAILED };
        notify();
      }, CONTEXT_RESTORE_WAIT_MS);
      notify();
    },
    onContextRestored: (ok) => {
      clearRestoreTimer();
      if (disposed) return;
      if (!ok) {
        contextLoss = { state: 'failed', message: REASON_CONTEXT_FAILED };
        notify();
        return;
      }
      contextLoss = null;
      notify();
      // The held overlay textures died with the context: the compositor re-uploads each overlay layer's canvas, then the frame repaints.
      void presentFrame(frame, true);
    },
  });
  if (!compositor) return null;

  // A CPU rasteriser cannot upload and blend several 1080p/4K layers 30 times a second; decode scaled down to the
  // playback width instead (and say so) rather than drop frames silently.
  const software = isSoftwareRenderer(compositor.renderer);
  const now = options.now ?? (() => performance.now());
  const requestFrame = options.requestFrame ?? ((cb) => requestAnimationFrame(cb));
  const cancelFrame = options.cancelFrame ?? ((h) => cancelAnimationFrame(h));

  const doc0 = options.getDoc();
  const scale = RESOLUTION_SCALE[options.playbackResolution ?? 'half'];
  // The FULL playback size: what a paused frame is drawn at. An exact fraction of the timeline for a pick; for Auto
  // (`fitDisplay`) it follows the viewer's own size once the host reports it (`setDisplayLongSide`).
  let displayLongSide: number | null = null;
  const fullSizeOf = (doc: EditTimeline): { width: number; height: number } => {
    if (options.fitDisplay && displayLongSide !== null) return fitRenderSize(doc.settings.width, doc.settings.height, displayLongSide, options.fitDisplay.maxLongSide);
    const fixed = { width: Math.max(160, Math.round(doc.settings.width * scale)), height: Math.max(90, Math.round(doc.settings.height * scale)) };
    if (!options.fitDisplay) return fixed;
    // Auto before the viewer is measured: the starting fraction, held under the device's ceiling.
    const capped = fitRenderSize(doc.settings.width, doc.settings.height, null, options.fitDisplay.maxLongSide);
    return capped.width < fixed.width ? capped : fixed;
  };
  let fullSize = fullSizeOf(doc0);
  // The decode target (software graphics only scale in the decoder) stays the size the session started at.
  const targetWidth = fullSize.width;
  const targetHeight = fullSize.height;
  // What the canvas is right now: the full size, or a smaller adaptive size while playing.
  let renderWidth = targetWidth;
  let renderHeight = targetHeight;
  compositor.resize(renderWidth, renderHeight);

  const overlayCanvas = document.createElement('canvas');
  overlayCanvas.width = renderWidth;
  overlayCanvas.height = renderHeight;
  const overlayCtx = overlayCanvas.getContext('2d');

  const openSource =
    options.openSource ??
    (async (url: string, fps: number, width: number, rotation = 0) => {
      const { openMediabunnySource } = await import('./mediabunny-source');
      return openMediabunnySource({ url, fps, targetWidth: width, scaleDown: software, rotation });
    });


  const pool = new DecoderPool(async (assetKey) => {
    const [url, fpsText, rotation] = splitAssetKey(assetKey);
    return openSource(url, Number.parseFloat(fpsText), targetWidth, rotation);
  }, options.poolConfig ?? DEFAULT_POOL_CONFIG, { now });

  const audioContext =
    options.audioContext === undefined
      ? typeof AudioContext !== 'undefined'
        ? new AudioContext()
        : null
      : options.audioContext;
  const audio = audioContext
    ? new AudioGraph(audioContext, { urlFor: options.proxyUrlFor, flags: options.flags })
    : null;

  audio?.preload(doc0);
  const ledger = new DropFrameLedger();
  // A source 3840 px wide or more is on screen (4K): the rate governor may then halve a 30 fps timeline too.
  let heavySource = false;
  const governor = new RateGovernor(
    () => fpsOf(options.getDoc().settings.rate),
    () => heavySource,
  );
  // Skipped frames first make the picture smaller (every effect stays on); the frame-rate governor is what is left after that.
  const adaptive = options.adaptive !== false;
  const sizeGovernor = new ResolutionGovernor(
    () => fpsOf(options.getDoc().settings.rate),
    () => Math.max(fullSize.width, fullSize.height),
  );
  const listeners = new Set<() => void>();
  const notify = () => {
    for (const l of listeners) l();
  };

  let frame = 0;
  let speed = 0;
  let wallStart = 0;
  let wallStartFrame = 0;
  let rafHandle: number | null = null;
  let lastDeadline = -1;
  let disposed = false;

  // Boot a few decode workers now, while paused: booting one at a cut mid-play is a visible stall.
  const warmWorkers = () => {
    if (!options.openSource && !disposed) void import('./mediabunny-source').then((m) => m.warmDecodeWorkers());
  };
  warmWorkers();
  let seekToken = 0;
  let missing: MissingLayer[] = [];
  // Audio-clock stall detection: a context that reports `running` but whose `currentTime` does not
  // advance (headless browsers, a missing output device) would otherwise freeze playback forever.
  let lastAudioFrame: number | null = null;
  let lastAudioAdvanceWall = 0;
  let audioStalled = false;
  const AUDIO_STALL_MS = 300;

  // One decode lane per CLIP (not per track): a cut or a transition then never re-points a live lane at a
  // different asset, and the next clip's lane can be opened and filled while the current one still plays.
  const laneIdOf = (trackId: string, itemId: string) => `layer:${trackId}:${itemId}`;
  const PREWARM_HORIZONS_SEC = [0.4, 0.8, 1.2];
  const PREROLL_FRAMES = 6;
  const PREROLL_MAX_MS = 750;
  // Lanes opened ahead of their clip: `laneId\0assetKey`, so the first frames are requested once.
  const prewarmed = new Set<string>();
  let playToken = 0;
  // Titles/captions that come on screen mid-play are rasterised once, off screen, before the clock starts: the first
  // raster of a font and size costs 8-60 ms of main thread (profiled), which at 60 fps is a skipped slot at the cut-in.
  let scratchCtx: CanvasRenderingContext2D | null | undefined;
  function warmOverlays(fromFrame: number): void {
    if (!overlayCtx || disposed) return;
    const items: OverlayItem[] = [];
    for (const [layerIndex, track] of options.getDoc().tracks.entries()) {
      for (const item of track.items) {
        if (item.type === 'mediaClip' || item.type === 'transition' || item.recordOut <= fromFrame) continue;
        if (items.length < 6) items.push({ item, layerIndex });
      }
    }
    if (items.length === 0) return;
    if (scratchCtx === undefined) {
      const scratch = document.createElement('canvas');
      scratch.width = renderWidth;
      scratch.height = renderHeight;
      scratchCtx = scratch.getContext('2d');
    }
    if (!scratchCtx) return;
    for (const entry of items) rasterizeOverlays(scratchCtx as unknown as Parameters<typeof rasterizeOverlays>[0], [entry]);
  }

  // Titles, captions and generators sit at their own track's place in the stack: each run of consecutive overlay
  // layers is one picture layer with its own canvas (slot), re-rasterised only when the run's content changes.
  const overlaySlots: { canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D | null; signature: string | null }[] = [
    { canvas: overlayCanvas, ctx: overlayCtx, signature: null },
  ];
  function overlayLayerFor(run: OverlayRun): CompositorLayer | null {
    let slot = overlaySlots[run.slot];
    if (!slot) {
      const canvas = document.createElement('canvas');
      canvas.width = renderWidth;
      canvas.height = renderHeight;
      slot = { canvas, ctx: canvas.getContext('2d'), signature: null };
      overlaySlots[run.slot] = slot;
    }
    if (!slot.ctx) return null;
    if (slot.signature !== run.signature) {
      const drawn = rasterizeOverlays(slot.ctx as unknown as Parameters<typeof rasterizeOverlays>[0], run.items);
      slot.signature = drawn > 0 ? run.signature : null;
      if (drawn === 0) return null;
    }
    return { layerId: `overlay:${run.slot}`, contentKey: run.signature, image: slot.canvas, opacity: 1 };
  }

  /**
   * Sizes the canvas (and the title/caption canvases, which are rasterised at the canvas size) for `factor` of the full
   * playback size. The compositor repaints the held picture at once, so the change never shows a cleared canvas.
   */
  function applyRenderSize(factor: number): boolean {
    const even = (n: number) => Math.max(2, Math.round(n / 2) * 2);
    const width = factor >= 1 ? fullSize.width : even(fullSize.width * factor);
    const height = factor >= 1 ? fullSize.height : even(fullSize.height * factor);
    if (width === renderWidth && height === renderHeight) return false;
    renderWidth = width;
    renderHeight = height;
    for (const slot of overlaySlots) {
      if (!slot) continue;
      slot.canvas.width = width;
      slot.canvas.height = height;
      slot.signature = null;
    }
    scratchCtx = undefined;
    compositor!.resize(width, height);
    return true;
  }
  /** The size while playing follows the governor; paused it is always the full playback size. */
  const playingScale = () => (adaptive && speed !== 0 ? sizeGovernor.scale : 1);

  // A source that failed to open is retried by the pool on a backoff. Playing, every tick asks for
  // it again; paused, nothing would, so the frame is re-presented when the next retry is due.
  let retryTimer: ReturnType<typeof setTimeout> | null = null;
  function scheduleRetry(holes: readonly MissingLayer[]): void {
    if (retryTimer !== null) clearTimeout(retryTimer);
    retryTimer = null;
    if (disposed || !holes.some((hole) => hole.cause === 'openFailed')) return;
    const at = pool.nextRetryAt();
    if (at === null) return;
    retryTimer = setTimeout(() => {
      retryTimer = null;
      if (!disposed && speed === 0) void presentFrame(frame, true);
    }, Math.max(0, at - now()) + 1);
  }

  /** Lane ids of the mask-preview videos a clip's region effects may read (kept open while the clip is on screen). */
  function maskLaneIdsOf(clip: MediaClip): string[] {
    const ids: string[] = [];
    for (const [index, e] of (clip.regionEffects ?? []).entries()) if (e.enabled) for (const trackId of e.mask.trackIds) ids.push(`mask:${clip.id}:${index}:${trackId}`);
    if (clip.mask && clip.mask.kind === 'segmentation') for (const trackId of clip.mask.trackIds) ids.push(`mask:${clip.id}:-1:${trackId}`);
    return ids;
  }

  /**
   * The decoded mask-preview frames (one per named track) for a region effect entry at `sourceSec`, or null when the
   * entry has to use boxes: the preview is not ready for every track, the host gave no URL, or a frame is not decoded yet.
   * Frame k = round(sourceSec * fps) of the track, luma as red, full range (T-006).
   */
  async function maskFramesOf(clip: MediaClip, index: number, mask: SegmentationMask, sourceSec: number, forScrub: boolean): Promise<MaskFrame[] | null> {
    // The same predicate parity uses (matteSourceKindFor); the URL is always known to a live session (its document's project).
    const urlFor = options.maskPreviewUrlFor ?? ((analysis: string, trackId: string) => maskPreviewUrl(options.getDoc().projectId, analysis, trackId));
    const tracks = options.segmentationTracksFor?.(mask.analysisStepResultId);
    if (matteSourceKindFor(mask, options.segmentationTracksFor, true) !== 'maskVideo' || !tracks) return null;
    const frames: MaskFrame[] = [];
    for (const trackId of mask.trackIds) {
      const track = tracks.find((t) => t.trackId === trackId);
      const url = urlFor(mask.analysisStepResultId, trackId);
      if (!track || !url || !(track.fps > 0)) return null;
      const laneId = `mask:${clip.id}:${index}:${trackId}`;
      // The mask video spans the unrotated source frame: it is decoded with the clip's own rotation, so it turns with the picture.
      const assetKey = joinAssetKey(url, track.fps, clip.sourceRotation);
      const lane = forScrub ? await pool.lane(laneId, assetKey) : pool.ready(laneId, assetKey);
      if (!lane) return null;
      const k = Math.max(0, Math.round(sourceSec * track.fps));
      const decoded = forScrub ? await lane.fetch(k) : lane.peek(k);
      lane.prefetch(k + 1, 1);
      lane.trimBehind(k);
      if (!decoded) return null;
      frames.push({ image: decoded.image });
    }
    return frames;
  }

  async function presentFrame(target: number, forScrub: boolean): Promise<void> {
    const doc = options.getDoc();
    const fps = fpsOf(doc.settings.rate);
    const layers = activeLayersAt(doc, target, options.flags);
    const laneKeyFor = (layer: (typeof layers)[number]) => laneIdOf(layer.track.id, layer.item.id);
    const liveLayerIds = new Set(layers.map(laneKeyFor));
    // Playing forward: the lanes of clips about to come on screen stay open (and fill) too.
    const upcoming = !forScrub && speed > 0 ? prewarmUpcoming(doc, target, fps, liveLayerIds) : [];
    for (const id of upcoming) liveLayerIds.add(id);
    for (const layer of layers) if (layer.item.type === 'mediaClip') for (const id of maskLaneIdsOf(layer.item)) liveLayerIds.add(id);
    pool.retainOnly(liveLayerIds);

    const draws: CompositorLayer[] = [];
    const holes: MissingLayer[] = [];
    const pendingFrames = new Map<string, MissingLayer>();
    let anyMissing = false;
    const token = ++seekToken;

    const bypass = options.getGradeBypass?.() === true;
    const bypassGrade = bypass;
    let sawHeavy = false;
    const overlayRuns = overlayRunsOf(layers, target);
    for (const layer of layers) {
      if (layer.item.type === 'adjustmentClip') {
        const adj = layer.item;
        const stack = bypassGrade ? [] : [adj.grade ?? null];
        const stages = stack.length > 0 ? richGradePlanOf(stack, target, options.gradeLutFor).stages : [];
        const adjFx = previewEffectsOf(adj.effects);
        draws.push({ layerId: `adjustment:${adj.id}`, image: null, opacity: 1, adjustment: true, richGrade: stages.length > 0 ? stages : undefined, effects: adjFx && adjFx.length > 0 ? adjFx : undefined, fxSeed: target });
        continue;
      }
      // An insert with media is decoded like a layer and warped into its keyframed quad (it has no transform, crop or fades).
      const insert = layer.item.type === 'insertClip' && layer.item.assetRef ? layer.item : null;
      if (layer.item.type !== 'mediaClip' && !insert) {
        const run = overlayRuns.get(layer);
        const overlayLayer = run && overlayCtx ? overlayLayerFor(run) : null;
        if (overlayLayer) draws.push(overlayLayer);
        continue;
      }
      const clip: MediaClip = insert ? insertAsMedia(insert) : (layer.item as MediaClip);
      const insertQuad: Quad | null = insert ? insertQuadAt(insert, target, doc.settings.width, doc.settings.height) : null;
      // An insert with no usable quad for this frame draws nothing (and, having media, no placeholder box either).
      if (insert && !insertQuad) continue;
      const laneId = laneKeyFor(layer);
      const transform = layerTransformAt(clip, target);
      const opacity = layer.opacity * transform.opacity;
      // A corner-pinned clip is padded to the canvas by the render and pinned from there: the transform's position is not applied.
      const pinned = !insert && clip.cornerPin ? true : false;
      const geometry = hasTransform(transform) ? (pinned ? { ...transform, x: 0, y: 0 } : transform) : undefined;
      // Picture features the compositor now draws: reframe (source crop), a shape mask and the corner pin / insert quad.
      const reframe = clip.reframe ?? undefined;
      const shape = shapeParamsOf(clip.mask) ?? undefined;
      const pin: Quad | null | undefined = insert ? insertQuad : pinned ? pinQuadOf(clip.cornerPin) : undefined;
      const aspectPolicy = insert ? 'stretch' : (doc.settings.aspectPolicy ?? 'fit');
      // P7: the clip's grade nodes (group then own, applied in order), or none when bypassed / at identity.
      const stacks = bypass ? [] : effectiveGrade(doc, clip);
      // L3: a keyer always runs in the grade pass, so the grade follows it. Curves, LUT nodes, qualifier/window secondaries (or more nodes than the single-draw shader holds) go through the grade pass.
      const rich = stacks.length > 0 && (needsGradePass(stacks) || !!clip.keyer) ? richGradePlanOf(stacks, target, options.gradeLutFor).stages : [];
      const richGrade = rich.length > 0 ? rich : undefined;
      const nodes = richGrade || stacks.length === 0 ? [] : previewGradeNodesOf(stacks);
      const grade = nodes.length > 0 ? nodes : undefined;
      // Effects the compositor runs: the ones it can, in the renderer's order (an unknown word is skipped alone).
      const fx = previewEffectsOf(clip.effects);
      const effects = fx.length > 0 ? fx : undefined;
      // An insert's matte is the same keyer, keyed out of its picture before the warp (InsertLayerBuilder.Chain).
      const keyer = keyerParamsOf(insert ? insert.matte : clip.keyer) ?? undefined;
      const padded = rendersPadded(clip);
      const inputStage = options.inputStageFor?.(clip) ?? null;
      const inputProfile = inputStage?.profile;
      const inputProfileBaked = inputStage?.baked;
      const blendMode = clip.blendMode !== undefined && clip.blendMode !== null && clip.blendMode !== 'normal' ? clip.blendMode : undefined;
      // Object masks and region effects are in source geometry: on a rotated clip their boxes (and the decoded mask video) turn with the picture.
      const boxMatte = clip.mask && clip.mask.kind === 'segmentation' ? matteFor(clip.mask, options.segmentationTracksFor, sourceTimeSec(clip, target, doc.settings.rate)) : null;
      const matte: Matte | undefined = boxMatte ? rotateMatte(boxMatte, clip.sourceRotation) : undefined;
      if (matte) matte.sourceWidth = options.sourceWidthFor?.(clip) ?? undefined;
      // A clip the render refuses to bind to its object mask (the mask ends before the clip's source span) draws no picture; the badge says why.
      // While the tracks are not loaded (or carry no box) the picture is shown UNMASKED and dimmed, without its clip effects: a
      // blur of the whole frame would mislead, a hidden layer would look like a bug. The badge says the mask is not shown yet.
      const segMask = clip.mask && clip.mask.kind === 'segmentation' ? clip.mask : null;
      // The decoded mask video is the preferred matte; boxes are only the fallback (so a ready video needs no box data).
      const maskFrames = segMask ? await maskFramesOf(clip, -1, segMask, sourceTimeSec(clip, target, doc.settings.rate), forScrub) : null;
      if (token !== seekToken && forScrub) return;
      if (segMask && (matte !== undefined || maskFrames) && maskBindsClip(clip, segMask, options.segmentationTracksFor, doc.settings.rate) === false) continue;
      const matteMissing = segMask !== null && matte === undefined && !maskFrames;
      const shownOpacity = matteMissing ? opacity * 0.5 : opacity;
      const shownEffects = matteMissing ? undefined : effects;
      // Region effects (magic blur and friends): per entry a decoded mask video when its preview is ready, else tracked boxes.
      // An entry with no matte yet is left out (the clip shows without it); loading the box data starts from the document's analyses.
      let regionEffects: readonly PreviewRegionEffect[] | undefined;
      if (clip.regionEffects?.some((e) => e.enabled)) {
        const sourceSec = sourceTimeSec(clip, target, doc.settings.rate);
        const frameMap = new Map<number, MaskFrame[]>();
        for (const [index, e] of clip.regionEffects.entries()) {
          if (!e.enabled) continue;
          const frames = await maskFramesOf(clip, index, e.mask, sourceSec, forScrub);
          if (token !== seekToken && forScrub) return;
          if (frames) frameMap.set(index, frames);
        }
        const planned = previewRegionEffectsOf(clip.regionEffects, options.segmentationTracksFor, sourceSec, (i) => frameMap.get(i) ?? null);
        for (const d of planned.drawn) {
          if (d.matte.kind === 'boxes') {
            d.matte.matte = rotateMatte(d.matte.matte, clip.sourceRotation);
            d.matte.matte.sourceWidth = options.sourceWidthFor?.(clip) ?? undefined;
          } else d.matte.sourceWidth = options.sourceWidthFor?.(clip) ?? undefined;
        }
        regionEffects = planned.drawn.length > 0 ? planned.drawn : undefined;
      }
      // The clip's own object mask: the decoded mask video when its preview is ready (the exact outline), else the box matte.
      let layerMatte = matte;
      if (segMask && maskFrames) {
        const cut = cutoutEffectOf(segMask, maskFrames);
        if (cut.matte.kind === 'maskVideo') cut.matte.sourceWidth = options.sourceWidthFor?.(clip) ?? undefined;
        regionEffects = [...(regionEffects ?? []), cut];
        layerMatte = undefined;
      }
      const url = (options.pictureUrlFor ?? options.proxyUrlFor)(clip);
      if (!url) {
        draws.push({ layerId: laneId, image: null, opacity: shownOpacity, transform: geometry, grade, effects: shownEffects, reframe, shape, pin, xfade: layer.xfade, blendMode });
        holes.push({ itemId: clip.id, trackId: layer.track.id, cause: 'noUrl' });
        anyMissing = true;
        continue;
      }
      const assetKey = joinAssetKey(url, fps, clip.sourceRotation);
      const contentKey = `${clip.id}\u0000${assetKey}`;
      // Playing: never stall the tick on an open (this starts it, or takes back a parked lane at
      // once), and never show another clip meanwhile.
      const opening = pool.lane(laneId, assetKey);
      const lane = forScrub ? await opening : pool.ready(laneId, assetKey);
      if (token !== seekToken && forScrub) return;
      if (!lane && !forScrub && !pool.failed(laneId, assetKey)) {
        holes.push({ itemId: clip.id, trackId: layer.track.id, cause: 'notDecoded' });
        anyMissing = true;
        continue;
      }
      if (!lane) {
        holes.push({ itemId: clip.id, trackId: layer.track.id, cause: 'openFailed' });
        anyMissing = true;
        continue;
      }
      const sourceFrame = sourceFrameFor(clip, target);
      const decoded = forScrub ? await lane.fetch(sourceFrame) : lane.peek(sourceFrame);
      if (token !== seekToken && forScrub) return;
      const direction = (speed < 0) !== (clip.reverse === true) ? -1 : 1;
      lane.prefetch(sourceFrame + direction, direction);
      lane.trimBehind(sourceFrame);
      if (decoded) {
        if (imageSize(decoded.image)[0] >= HEAVY_SOURCE_WIDTH) sawHeavy = true;
        draws.push({ layerId: laneId, contentKey, image: decoded.image, opacity: shownOpacity, transform: geometry, grade, richGrade, keyer, fxSeed: target, effects: shownEffects, padded, matte: layerMatte, aspectPolicy, inputProfile, inputProfileBaked, reframe, shape, pin, regionEffects, xfade: layer.xfade, blendMode });
      } else {
        draws.push({ layerId: laneId, contentKey, image: null, opacity: shownOpacity, transform: geometry, grade, richGrade, keyer, fxSeed: target, effects: shownEffects, padded, matte: layerMatte, aspectPolicy, inputProfile, inputProfileBaked, reframe, shape, pin, regionEffects, xfade: layer.xfade, blendMode });
        // While playing a late frame is a repeat of THIS clip (the ledger counts it); paused, or with
        // no picture of this clip held yet, it is a hole.
        if (forScrub) holes.push({ itemId: clip.id, trackId: layer.track.id, cause: 'noFrame' });
        else pendingFrames.set(laneId, { itemId: clip.id, trackId: layer.track.id, cause: 'notDecoded' });
        anyMissing = true;
      }
    }

    if (sawHeavy) heavySource = true;
    compositor!.setViewingLut(options.getViewingLut?.() ?? null);
    const { repeats, blank } = compositor!.draw(draws);
    for (const layerId of blank) {
      const hole = pendingFrames.get(layerId);
      if (hole) holes.push(hole);
    }
    if (!forScrub) {
      const deadlineMs = wallStart + ((target - wallStartFrame) / (speed * fps)) * 1000;
      const lateness = now() - deadlineMs;
      if (anyMissing || repeats > 0) ledger.recordPresented(lateness, now(), true);
      else ledger.recordPresented(lateness, now(), false);
    }
    frame = target;
    missing = holes;
    if (forScrub) scheduleRetry(holes);
    notify();
  }

  /**
   * Opens (and starts filling) the decode lanes of clips that come on screen within the next ~1.2 s, so a
   * cut or a transition finds its picture decoded instead of opening a source mid-play. Returns the ids
   * of every upcoming lane so the pool keeps them. Only a clip's FIRST visible frame is prefetched.
   */
  function prewarmUpcoming(doc: EditTimeline, target: number, fps: number, current: ReadonlySet<string>): string[] {
    const ids: string[] = [];
    for (const horizonSec of PREWARM_HORIZONS_SEC) {
      const horizon = target + Math.round(horizonSec * fps);
      for (const layer of activeLayersAt(doc, horizon, options.flags)) {
        if (layer.item.type !== 'mediaClip') continue;
        const laneId = laneIdOf(layer.track.id, layer.item.id);
        if (current.has(laneId) || ids.includes(laneId)) continue;
        ids.push(laneId);
        const clip = layer.item;
        const url = (options.pictureUrlFor ?? options.proxyUrlFor)(clip);
        if (!url) continue;
        const assetKey = joinAssetKey(url, fps, clip.sourceRotation);
        const warmKey = `${laneId}\u0000${assetKey}`;
        if (prewarmed.has(warmKey)) continue;
        prewarmed.add(warmKey);
        // The first frame at which this clip is on screen (a transition's incoming leg shows before its recordIn).
        let first = horizon;
        for (let f = target + 1; f < horizon; f++) {
          if (activeLayersAt(doc, f, options.flags).some((l) => l.item.id === clip.id)) {
            first = f;
            break;
          }
        }
        void pool.lane(laneId, assetKey).then((lane) => lane?.prefetch(sourceFrameFor(clip, first), 1));
      }
    }
    if (prewarmed.size > 64) prewarmed.clear();
    return ids;
  }

  /** Waits (bounded) until every on-screen layer has its first look-ahead frames decoded; null when nothing is pending. */
  function preroll(at: number): Promise<void> | null {
    const doc = options.getDoc();
    const fps = fpsOf(doc.settings.rate);
    const waits: Promise<unknown>[] = [];
    for (const layer of activeLayersAt(doc, at, options.flags)) {
      if (layer.item.type !== 'mediaClip') continue;
      const clip = layer.item;
      const url = (options.pictureUrlFor ?? options.proxyUrlFor)(clip);
      if (!url) continue;
      const assetKey = joinAssetKey(url, fps, clip.sourceRotation);
      const laneId = laneIdOf(layer.track.id, clip.id);
      const sourceFrame = sourceFrameFor(clip, at);
      const settle = (lane: ReturnType<typeof pool.ready>) => {
        if (!lane) return null;
        lane.prefetch(sourceFrame, clip.reverse === true ? -1 : 1);
        return lane.settleAhead(sourceFrame, PREROLL_FRAMES);
      };
      const ready = pool.ready(laneId, assetKey);
      const pending = ready ? settle(ready) : pool.lane(laneId, assetKey).then((lane) => settle(lane));
      if (pending) waits.push(pending);
    }
    if (waits.length === 0) return null;
    return Promise.race([Promise.all(waits).then(() => undefined), new Promise<void>((resolve) => setTimeout(resolve, PREROLL_MAX_MS))]);
  }

  function tick(): void {
    if (disposed || speed === 0) return;
    const doc = options.getDoc();
    const fps = fpsOf(doc.settings.rate);
    const end = timelineEndFrame(doc);

    // Audio is the master clock when it is running; the wall clock otherwise (reverse shuttle, a
    // suspended context, or an audio clock that stopped advancing — see `audioStalled`).
    let audioFrame = audio && speed > 0 && !audioStalled ? audio.playheadFrame(fps) : null;
    if (audioFrame !== null) {
      if (lastAudioFrame === null || audioFrame !== lastAudioFrame) {
        lastAudioFrame = audioFrame;
        lastAudioAdvanceWall = now();
      } else if (now() - lastAudioAdvanceWall > AUDIO_STALL_MS) {
        // Hand the clock to the wall from HERE, so the stall is not replayed as a burst of drops.
        audioStalled = true;
        wallStart = now();
        wallStartFrame = frame;
        audioFrame = null;
      }
    }
    const elapsedSec = (now() - wallStart) / 1000;
    const clockFrame = audioFrame ?? wallStartFrame + elapsedSec * speed * fps;
    const step = governor.frameStep;
    const target = Math.floor(clockFrame / step) * step;

    if (target < 0 || (end > 0 && target >= end)) {
      pause();
      frame = Math.max(0, Math.min(end > 0 ? end - 1 : 0, target));
      void presentFrame(frame, true);
      return;
    }

    if (lastDeadline >= 0) {
      const skipped = Math.abs(target - lastDeadline) / step - 1;
      if (skipped > 0) ledger.recordDropped(skipped);
      if (target !== lastDeadline) {
        let changed = false;
        if (adaptive && sizeGovernor.record(Math.max(0, skipped))) changed = applyRenderSize(playingScale()) || changed;
        if (governor.record(Math.max(0, skipped))) changed = true;
        if (changed) notify();
      }
    }
    if (target !== lastDeadline) {
      lastDeadline = target;
      void presentFrame(target, false);
    }
    rafHandle = requestFrame(tick);
  }

  function play(nextSpeed: number): void {
    if (disposed) return;
    if (nextSpeed === 0) {
      pause();
      return;
    }
    speed = nextSpeed;
    const token = ++playToken;
    if (rafHandle !== null) cancelFrame(rafHandle);
    rafHandle = null;
    // The clock (and the audio, which is its master) start once the look-ahead is primed, so the first
    // frames are not all late; a source that is slow to open cannot hold the start longer than PREROLL_MAX_MS.
    const begin = () => {
      if (disposed || token !== playToken || speed === 0) return;
      governor.reset();
      sizeGovernor.beginPlay();
      applyRenderSize(playingScale());
      heavySource = false;
      wallStart = now();
      wallStartFrame = frame;
      lastDeadline = -1;
      lastAudioFrame = null;
      lastAudioAdvanceWall = now();
      audioStalled = false;
      if (audio) void audio.play(options.getDoc(), frame, speed);
      rafHandle = requestFrame(tick);
    };
    if (nextSpeed > 0) warmOverlays(frame);
    const waiting = nextSpeed > 0 ? preroll(frame) : null;
    if (waiting) void waiting.then(begin);
    else begin();
    notify();
  }

  /** `resuming`: a seek during playback pauses only to restart at once, so the adaptive size is left as it is (no two resizes per loop wrap). */
  function pause(resuming = false): void {
    const wasPlaying = speed !== 0;
    speed = 0;
    playToken += 1;
    warmWorkers();
    if (rafHandle !== null) cancelFrame(rafHandle);
    rafHandle = null;
    audio?.stop();
    // Back at the full playback size: the held picture is repainted at once, then the frame is drawn afresh (titles re-rasterised sharp).
    if (!disposed && !resuming && applyRenderSize(1) && wasPlaying) void presentFrame(frame, true);
    notify();
  }

  return {
    kind: 'live',
    reason: null,
    get contextLoss() {
      return contextLoss;
    },
    get reducedQuality() {
      if (governor.frameStep > 1) return `Reduced frame rate: this machine could not present ${Math.round(fpsOf(options.getDoc().settings.rate))} fps, so every ${governor.frameStep === 2 ? 'second' : 'few'} frame is shown. The dropped-frame counter counts the reduced rate.`;
      if (speed !== 0 && adaptive && sizeGovernor.scale < 1) return REDUCED_QUALITY_ADAPTIVE;
      return software ? REDUCED_QUALITY_SOFTWARE : null;
    },
    get adaptiveQuality() {
      return { scale: playingScale(), atFloor: !adaptive || sizeGovernor.atFloor, width: renderWidth, height: renderHeight };
    },
    setDisplayLongSide(px) {
      if (disposed || !options.fitDisplay) return;
      const next = px !== null && px > 0 ? px : null;
      if (next === displayLongSide) return;
      displayLongSide = next;
      const size = fullSizeOf(options.getDoc());
      if (size.width === fullSize.width && size.height === fullSize.height) return;
      fullSize = size;
      // Paused: the frame is drawn afresh at the new size. Playing: the next tick draws it; the held picture covers the gap.
      if (applyRenderSize(playingScale()) && speed === 0) void presentFrame(frame, true);
      notify();
    },
    seek(next) {
      // PA-23: keep the shuttle speed (and direction) across a seek during playback.
      const playingAt = speed;
      if (playingAt !== 0) pause(true);
      frame = Math.max(0, Math.trunc(next));
      void presentFrame(frame, true);
      if (playingAt !== 0) play(playingAt);
    },
    missingLayers: () => missing,
    play,
    pause: () => pause(),
    currentFrame: () => frame,
    isPlaying: () => speed !== 0,
    stats: (): PlaybackStats => ledger.snapshot(),
    meters: () => audio?.meters() ?? null,
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    invalidate() {
      audio?.preload(options.getDoc());
      audio?.applyTrackFlags(options.getDoc());
      if (speed === 0) void presentFrame(frame, true);
    },
    dispose() {
      disposed = true;
      clearRestoreTimer();
      if (retryTimer !== null) clearTimeout(retryTimer);
      pause();
      pool.dispose();
      audio?.dispose();
      compositor.dispose();
      listeners.clear();
    },
  };
}

/** `fps|url`; a clip with a source rotation adds `@deg` to the fps part so a rotation edit opens a fresh lane. */
export function joinAssetKey(url: string, fps: number, rotation?: number | null): string {
  return rotation ? `${fps}@${rotation}|${url}` : `${fps}|${url}`;
}

/**
 * An insert's media as the clip the decode lane and the proxy lookup read: it plays from the start of its asset at the
 * timeline rate (an insert has no source offset, speed, crop or fades). Only used to decode its picture; the insert
 * itself is what the document keeps.
 */
export function insertAsMedia(insert: InsertClip): MediaClip {
  return { id: insert.id, name: insert.name, type: 'mediaClip', recordIn: insert.recordIn, recordOut: insert.recordOut, assetRef: insert.assetRef as NonNullable<InsertClip['assetRef']>, sourceIn: 0, enabled: insert.enabled };
}

export function splitAssetKey(key: string): [string, string, number] {
  const separator = key.indexOf('|');
  const head = key.slice(0, separator);
  const at = head.indexOf('@');
  return at < 0 ? [key.slice(separator + 1), head, 0] : [key.slice(separator + 1), head.slice(0, at), Number.parseInt(head.slice(at + 1), 10) || 0];
}
