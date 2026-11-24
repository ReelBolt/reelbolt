/**
 * The preview parity contract — which document features the browser compositor shows faithfully,
 * which it approximates, and which it cannot show at all (render-required).
 *
 * This is the Review Focus of P3: "preview/render divergence is visible, not silent". The badge
 * the viewer shows and the per-item marks on the timeline canvas are both driven by this one
 * classification, so a feature can never be approximated quietly in one place and claimed faithful
 * in another. The deterministic `TimelineRender` step (P4) is authoritative for all three classes.
 *
 * Classes:
 *   - `faithful`        cuts, multi-track video stacking (top track wins per pixel), track
 *                       enable/mute/solo, static clip gain and gain envelopes (linear / eased /
 *                       stepped keyframes evaluated per frame).
 *   - `approximate`     titles and captions (rasterised with the browser's text engine, not
 *                       drawtext), non-colour generator clips (a flat colour; a colour generator is
 *                       faithful), insert clips (a perspective warp, chroma matte included), a speed change
 *                       (the timing is previewed, the pitch-preserving audio stretch and frame
 *                       blending are not; optical flow adds its own note), blur/pixelate/mono/vignette
 *                       effects on the GPU, a segmentation mask followed by its box, camera conversion,
 *                       curves and LUTs, extra grade nodes, transforms and crop, HDR footage.
 *   - `renderRequired`  anything the browser does not execute at all: effects it has no shader for,
 *                       shape masks, region effects, keys, reframe, corner pin, (stabilisation, when the
 *                       viewer has no stabilised proxy lookup; with one it is `approximate`),
 *                       secondary grades, adjustment clips.
 *
 * An item reports EVERY finding (`ItemParity.findings`), worst first; its `parity` and `reason` are the
 * worst finding's.
 */

import { richGradePlanOf } from './grade-pass-plan';
import { effectiveGrade } from '../color/grade';
import type { AssetRef, AudioBus, AudioDynamics, EditTimeline, GradeNode, Item, MediaClip, SegmentationMask, Track } from '../types';
import { DSP_STAGE_LABEL, detectDspSupport, PREVIEW_NOTE, planBusRouting, presentStages, unheardStages, type DspSupport } from '../audio/dsp-preview';
import { previewEffectsOf, unpreviewableEffectsOf } from './gl-compositor';
import { maskPreviewState, matteSourceKindFor } from './region-effects';
import { hasBoxData, maskBindsClip, type SegmentationTracksLookup } from './segmentation-tracks';
import { STABILIZE_FAILED_REASON, STABILIZE_READY_REASON, stabilizePreparingReason } from './stabilize-proxy';
import { xfadeIndexOf, xfadeParityOf } from './xfade';

/** The reason for each transition word that is approximate (the texts are catalogued in reason-tables.ts). */
const XFADE_APPROXIMATE_REASON: Readonly<Record<string, string>> = {
  dissolve: 'A dissolve transition is previewed with the render\'s transition maths; the grain pattern differs a little from the render because the GPU computes it slightly differently.',
  distance: 'A distance transition is previewed with the render\'s transition maths; the render measures colour distance in YUV and the viewer in RGB, so the edge differs a little.',
  fadegrays: 'A fadegrays transition is previewed with the render\'s transition maths; the render greys by luma and the viewer by an RGB average, so the grey differs a little.',
  hblur: 'An hblur transition is previewed with the render\'s transition maths; the viewer samples a very wide blur at 64 points, so the blur is slightly coarser than the render.',
};
import { HDR_PREVIEW_REASON, type HdrKindLookup } from '../media/hdr';

/** Plain reasons for a clip with a camera conversion (`inputProfile`). */
export const CAMERA_PLUS_LUT_HINT = 'This clip already has a camera conversion and a LUT; check it is not converted twice.';

/** The retime-quality note: optical flow is only blended by the render. */
export const OPTICAL_FLOW_REASON = 'Frames are not blended in the preview; the render blends them.';

/** What the viewer knows about a stabilised clip's picture: the stabilised proxy is ready, or still being made (percent, null = unknown). */
export type StabilizeState = { state: 'ready' } | { state: 'preparing'; percent: number | null } | { state: 'failed' };
export type StabilizeLookup = (clip: MediaClip) => StabilizeState | null;

export type ParityClass = 'faithful' | 'approximate' | 'renderRequired';

export interface ItemParity {
  itemId: string;
  trackId: string;
  itemType: Item['type'];
  /** The item's own name (for "Clip 'Interview' ..." lines); may be empty. */
  itemName: string;
  parity: ParityClass;
  /** The worst finding's reason, in words the badge shows. */
  reason: string;
  /** Every finding for the item, worst class first (rule order within a class); empty when faithful. */
  findings: ParityFinding[];
}

export interface ParitySummary {
  items: ItemParity[];
  /** The worst class present anywhere in the document. */
  worst: ParityClass;
  renderRequiredCount: number;
  approximateCount: number;
}

const RANK: Record<ParityClass, number> = { faithful: 0, approximate: 1, renderRequired: 2 };

function trackKindOf(doc: EditTimeline, itemId: string): 'video' | 'audio' | null {
  for (const track of doc.tracks) if (track.items.some((i) => i.id === itemId)) return track.kind;
  return null;
}

/** A blending clip: the viewer ports the export's integer formulas; only the colour-conversion matrix and 8-bit rounding at partial opacity differ. */
export const BLEND_MODE_REASON = "The blend mode is previewed with the render's own formulas; colours can differ by one level where the clip is partly transparent.";
/** The feature a finding is about; the join key the timeline badges use to colour each badge. */
export type ParityFeature =
  | 'speed' | 'retime' | 'channelMap' | 'audioProcessing' | 'stabilize' | 'cornerPin' | 'crop' | 'shapeMask' | 'segmentationMask'
  | 'regionEffects' | 'keyer' | 'reframe' | 'effects' | 'secondaries' | 'hdr' | 'inputProfile' | 'curvesLut' | 'gradeNodes'
  | 'transform' | 'title' | 'caption' | 'generator' | 'insert' | 'adjustment' | 'transition' | 'compound' | 'multicam' | 'unknown';

/** One thing the preview does not show exactly about an item. */
export interface ParityFinding {
  feature: ParityFeature;
  parity: ParityClass;
  reason: string;
}

export interface ItemClassification {
  /** The worst finding's class (`faithful` when there is none). */
  parity: ParityClass;
  /** The worst finding's reason (the faithful sentence when there is none). */
  reason: string;
  /** Every applicable finding, in rule order. Empty for a faithful item. */
  findings: ParityFinding[];
}

/** Worst finding wins; the earlier one on a tie. */
function conclude(findings: ParityFinding[], faithfulReason: string): ItemClassification {
  let worstFinding: ParityFinding | null = null;
  for (const f of findings) if (f.parity !== 'faithful' && (!worstFinding || RANK[f.parity] > RANK[worstFinding.parity])) worstFinding = f;
  const real = findings.filter((f) => f.parity !== 'faithful');
  return worstFinding
    ? { parity: worstFinding.parity, reason: worstFinding.reason, findings: real.slice().sort((a, b) => RANK[b.parity] - RANK[a.parity]) }
    : { parity: 'faithful', reason: faithfulReason, findings: [] };
}

/** A mask's outline is exact when the mask preview video is ready for EVERY track it names (T-006 `maskPreview`). */
function outlineReady(mask: SegmentationMask, tracksFor: SegmentationTracksLookup | undefined): boolean {
  // The ONE predicate the draw path uses too: every track ready and few enough for the shader (the session always has the URL).
  return matteSourceKindFor(mask, tracksFor, true) === 'maskVideo';
}

const OUTLINE_CLOSE_REASON = "The outline comes from a small copy of the mask, so its edges are close to the export but not identical.";
const OUTLINE_PREPARING_REASON = 'Outline preview still preparing, showing a box.';
const BOX_ONLY_REASON = 'The preview follows a box around the object; the render follows its exact outline.';

/** The approximate reason for a mask whose outline is not ready: still being made (pending) or never available (none). */
function boxReasonFor(mask: SegmentationMask, tracksFor: SegmentationTracksLookup | undefined): string {
  const tracks = tracksFor?.(mask.analysisStepResultId) ?? [];
  return mask.trackIds.some((id) => maskPreviewState(tracks.find((t) => t.trackId === id)) === 'pending') ? OUTLINE_PREPARING_REASON : BOX_ONLY_REASON;
}

const single = (feature: ParityFeature, parity: ParityClass, reason: string): ItemClassification => conclude([{ feature, parity, reason }], reason);

/**
 * One item's parity. Pass the document so a clip graded through a colour group is judged by the
 * whole grade the render applies (the group's stack, then the clip's own), not just its own stack.
 * Every applicable finding is collected (worst first, rule order within a class), not just the first.
 */
/** Optional inputs of the classifiers that grow over time (one object instead of more positional parameters). */
export interface ParityOptions {
  /** Where a grade LUT file is: still loading, ready, or failed (the viewer then draws without it). Absent = every LUT counts as ready. */
  gradeLutStatus?: (file: AssetRef) => 'loading' | 'ready' | 'failed';
  /** A stabilised clip: its stabilised proxy is ready or being made. Absent = the preview cannot show stabilisation. */
  stabilizeFor?: StabilizeLookup;
  /** The HDR lookup (an HDR source previews approximately). */
  hdrKindFor?: HdrKindLookup;
  /** The audio processing the browser can run; absent = detected. */
  dsp?: DspSupport;
  /** The segmentation tracks (mask boxes and outline previews) by analysis id. */
  tracksFor?: SegmentationTracksLookup;
}

export function classifyItem(item: Item, doc?: EditTimeline, options: ParityOptions = {}): ItemClassification {
  const { hdrKindFor, tracksFor, gradeLutStatus, stabilizeFor } = options;
  const dsp = options.dsp ?? detectDspSupport();
  switch (item.type) {
    case 'mediaClip': {
      const found: ParityFinding[] = [];
      const add = (feature: ParityFeature, parity: ParityClass, reason: string) => { found.push({ feature, parity, reason }); };
      const rate = (item as { playbackRate?: number }).playbackRate;
      if (typeof rate === 'number' && rate !== 1) {
        // The scheduler maps record frames to source time through `playbackRate` and the audio graph plays at it,
        // so the preview does show the speed change; what it cannot show is the render's pitch-preserving audio
        // stretch and its frame blending. A non-positive rate is ignored by the scheduler (it plays at 1x).
        if (!(rate > 0)) add('speed', 'renderRequired', `A speed of ${rate}× is not previewed (the preview plays this clip at normal speed); the render applies it.`);
        else {
          add('speed', 'approximate', `${rate < 1 ? 'Slow' : 'Fast'} motion (${Number(rate.toFixed(3))}×): the preview shows the new timing, but the sound's pitch changes and frames are not blended. The final render is exact.`);
          if (item.retimeQuality === 'opticalFlow') add('retime', 'approximate', OPTICAL_FLOW_REASON);
        }
      }
      // The channel map is heard in the live preview: the audio graph routes the channels as the render's `pan` filter does.
      // R08.6: bus routing and the dynamics stages are heard when the preview graph implements them.
      const processing = doc ? audioProcessingParity(doc, item.id, dsp) : null;
      if (processing) add('audioProcessing', processing.parity, processing.reason);
      // P7: the picture features the compositor does not execute at all.
      if (item.stabilize) {
        // With a proxy lookup the preview does show it (a stabilised copy of the whole source); without one, it does not.
        const stab = stabilizeFor?.(item) ?? null;
        // Only a ready proxy is a stabilised picture (approximate); while it is made, or after it failed, the viewer shows the unstabilised one.
        if (stab?.state === 'ready') add('stabilize', 'approximate', STABILIZE_READY_REASON);
        else if (stab?.state === 'failed') add('stabilize', 'renderRequired', STABILIZE_FAILED_REASON);
        else if (stab) add('stabilize', 'renderRequired', stabilizePreparingReason(stab.percent));
        else add('stabilize', 'renderRequired', 'Stabilisation is a two-pass render effect; the preview shows the unstabilised picture.');
      }
      if (item.cornerPin) add('cornerPin', 'approximate', 'The corner pin is previewed as a true perspective warp; edges may differ by a pixel.');
      let maskApprox: ParityFinding | null = null;
      if (item.mask && item.mask.kind === 'segmentation') {
        // A rotated clip's mask turns with the picture (the render mattes at source geometry and rotates afterwards), so it previews like any other.
        // The render and the validator refuse a clip whose source span the mask does not cover (SegmentationMaskBinding.Check).
        const docRate = doc?.settings.rate;
        if (!hasBoxData(item.mask, tracksFor) && !outlineReady(item.mask, tracksFor)) add('segmentationMask', 'renderRequired', 'The object mask is not loaded yet, so the preview shows the unmasked picture dimmed; the effect applies in the render.');
        else if (docRate && maskBindsClip(item, item.mask, tracksFor, docRate) === false) {
          add('segmentationMask', 'renderRequired', 'The object mask does not cover this part of the clip; the render refuses it until the mask covers the whole clip.');
        } else maskApprox = { feature: 'segmentationMask', parity: 'approximate', reason: outlineReady(item.mask, tracksFor) ? OUTLINE_CLOSE_REASON : boxReasonFor(item.mask, tracksFor) };
      } else if (item.mask) {
        add('shapeMask', 'approximate', "The shape mask is previewed with the export's soft edge. On a rotated clip the export places it in the turned frame, so it sits in a different spot; with blur or pixelate its edges differ slightly.");
      }
      // Region effects draw through their matte: faithful with the mask video, a box while it is made, nothing without box data.
      const regionOn = item.regionEffects?.filter((e) => e.enabled) ?? [];
      if (regionOn.length > 0 && !regionOn.every((e) => hasBoxData(e.mask, tracksFor) || outlineReady(e.mask, tracksFor))) add('regionEffects', 'renderRequired', 'The object is not loaded yet, so the preview shows the clip without its effects; the export applies them.');
      else if (regionOn.length > 0) {
        // Never faithful: the blur runs on a reduced copy, the colour maths is RGB (the render's is YUV) and the outline is a 640 px mask.
        const waiting = regionOn.find((e) => !outlineReady(e.mask, tracksFor));
        if (waiting) add('regionEffects', 'approximate', boxReasonFor(waiting.mask, tracksFor));
        else add('regionEffects', 'approximate', 'The effect follows the object\'s outline from a small copy of the mask; blur and colours are close to the export but not identical.');
      }
      // The chroma key runs in the live grade pass (colorkey + despill, measured against ffmpeg): faithful on the live GL path, the default.
      if (item.reframe) add('reframe', 'approximate', 'The picture is cropped around the subject to fit the video shape, as in the export; edges may differ by a pixel.');
      const fx = previewEffectsOf(item.effects);
      if (unpreviewableEffectsOf(item.effects).length > 0) add('effects', 'renderRequired', 'Some effects are render-only; the preview shows the others.');
      const stacks = doc ? effectiveGrade(doc, item) : item.grade ? [item.grade] : [];
      const nodes: GradeNode[] = stacks.flatMap((stack) => (stack.enabled === false ? [] : stack.nodes ?? []));
      // Curves, LUTs and the HSL / power-window secondaries run in the live grade pass (maths ported from the render): faithful on the live GL
      // path, which is the default. The simple preview's own honesty lives in honesty.ts.
      // Camera conversion: the preview converts through the same table as the render and interpolates it tetrahedrally, as the render's
      // lut3d does, so the conversion alone is faithful. A clip with BOTH a camera conversion and a grade LUT gets the double-conversion
      // hint (a check for the editor, not a rendering difference; the findings model has no info class, so it reads approximate).
      // HDR footage ignores the profile (the render converts HDR itself), so the HDR note stays the headline and no conversion finding is added.
      const isHdr = Boolean(item.assetRef && hdrKindFor?.(item));
      if (item.inputProfile && !isHdr && nodes.some((n) => n.enabled !== false && n.lut)) {
        add('inputProfile', 'approximate', CAMERA_PLUS_LUT_HINT);
      }
      // The mask box and the GPU effects are listed before the grade/transform notes, so on a tie they stay the headline.
      if (maskApprox) found.push(maskApprox);
      if (fx.some((e) => e.type === 'blur' || e.type === 'pixelate' || e.type === 'mono' || e.type === 'vignette')) {
        add('effects', 'approximate', 'Blur, pixelate, mono and vignette preview on the GPU in the clip\'s on-canvas geometry at reduced resolution. Rotated clips, softer picture edges and a smaller export differ: the render\'s blur and pixel size are fixed in output pixels, so a smaller export looks stronger.');
      }
      if (fx.some((e) => e.type === 'sharpen')) add('effects', 'approximate', 'Sharpen previews at the render\'s strength; at a smaller preview size the edge detail is slightly softer.');
      if (fx.some((e) => e.type === 'grain')) add('effects', 'approximate', 'Film grain previews with the same strength but a different pattern.');
      // R10.7: HDR footage is converted to standard colours before grading in the render; the preview does not do that.
      if (isHdr) add('hdr', 'approximate', HDR_PREVIEW_REASON);
      // A LUT node is faithful once its file has loaded; until then the grade draws without it.
      if (gradeLutStatus) {
        const states = nodes.filter((n) => n.enabled !== false && n.lut && (n.lut.intensity ?? 1) > 0).map((n) => gradeLutStatus((n.lut as NonNullable<GradeNode['lut']>).file));
        if (states.includes('failed')) add('curvesLut', 'approximate', 'The LUT file could not be loaded, so the preview shows the clip without it.');
        else if (states.includes('loading')) add('curvesLut', 'approximate', 'A LUT file is still loading; the preview shows the grade without it until it arrives.');
      }
      // Every correction is ported (primaries, curves, LUT, secondaries); only more than 16 differ: the rest fold into one approximate step.
      const plan = richGradePlanOf(stacks, 0);
      if (plan.folded > 0 || plan.dropped > 0) {
        add('gradeNodes', 'approximate', 'More than 16 corrections: the preview grades the first 16 exactly and folds the rest into one approximate step. Corrections past 16 that use curves, a LUT or a secondary are not shown.');
      }
      const t = item.transform ?? null;
      const animated = t !== null && Object.values(t).some((channel) => channel && Array.isArray(channel.keys) && channel.keys.length > 1);
      if (animated) {
        add('transform', 'approximate', 'Keyframed transform: the curve is identical, but the preview and the render sample sub-pixel geometry differently.');
      } else if (t !== null) {
        add('transform', 'approximate', 'Transform/crop preview uses GPU bilinear sampling; the render uses swscale.');
      }
      // The crop is its own finding (its badge must not carry a keyframed transform's reason); the sentence is shared.
      if (item.crop) add('crop', 'approximate', 'Transform/crop preview uses GPU bilinear sampling; the render uses swscale.');
      return conclude(found, item.blendMode !== undefined && item.blendMode !== null && item.blendMode !== 'normal' ? BLEND_MODE_REASON : 'Cut, stacking, gain, fades, reverse and hold are previewed as rendered.');
    }
    case 'titleClip':
      return item.template && item.template !== 'plain'
        ? single('title', 'approximate', `The "${item.template}" template is previewed with its position, box and border; the text is rasterised by the browser, not by drawtext, so metrics may differ.`)
        : single('title', 'approximate', 'Title text is rasterised by the browser, not by drawtext; metrics may differ.');
    case 'caption':
      return single('caption', 'approximate', 'Caption text is rasterised by the browser; styling is approximate.');
    case 'generatorClip':
      return item.generatorType === 'color'
        ? single('generator', 'faithful', 'A colour generator previews as its colour, as rendered.')
        : item.generatorType === 'bars'
          ? single('generator', 'faithful', "The bars generator draws the render's HD colour bars; the grey ramp's steps can sit a pixel off.")
          : single('generator', 'approximate', 'Bars/test generators preview as a flat colour; the render draws the real pattern.');
    case 'insertClip':
      // The insert's chroma matte runs in the live grade pass with the keyer's own maths, before the warp, as in the export.
      return single('insert', 'approximate', 'The insert is previewed as a perspective warp; edges may differ by a pixel.');
    case 'adjustmentClip':
      return unpreviewableEffectsOf(item.effects).length > 0 ? single('adjustment', 'renderRequired', 'An adjustment clip grades the layers below it only in the render.') : single('adjustment', 'faithful', "An adjustment clip grades and applies its effects to everything below it, as rendered.");
    case 'transition':
      if (item.transitionType === 'fade') return single('transition', 'faithful', 'A dissolve is previewed as the same opacity ramp the render applies.');
      // T-008b: every word with an xfade kernel is previewed with the render's own maths; a word we do not know stays a dissolve.
      if (xfadeIndexOf(item.transitionType) < 0) return single('transition', 'approximate', `A ${item.transitionType} transition previews as a dissolve; the render applies the real wipe.`);
      return xfadeParityOf(item.transitionType) === 'exact'
        ? single('transition', 'faithful', `A ${item.transitionType} transition is previewed with the render's own transition maths.`)
        : single('transition', 'approximate', XFADE_APPROXIMATE_REASON[item.transitionType]);
    case 'compoundClip':
      return single('compound', 'approximate', 'A compound clip previews through the flattener (lib/editing/structure/flatten.ts, the renderer\'s own rules); its contents keep their own parity.');
    case 'multicamClip':
      return single('multicam', 'faithful', 'A multicam clip previews as its angle runs, cut where the render cuts, with the sound from the source chosen for it (the program, one angle, or none).');
    default:
      return single('unknown', 'renderRequired', 'Unknown item kind.');
  }
}

function trackWith(doc: EditTimeline, itemId: string): Track | null {
  return doc.tracks.find((t) => t.items.some((i) => i.id === itemId)) ?? null;
}

/** The track's own bus chain, nearest first (bounded, loop-safe). */
function busChain(doc: EditTimeline, track: Track): AudioBus[] {
  const byId = new Map((doc.buses ?? []).map((b) => [b.id, b] as const));
  const chain: AudioBus[] = [];
  let next = track.busId ? byId.get(track.busId) : undefined;
  while (next && chain.length < 6 && !chain.includes(next)) {
    chain.push(next);
    next = next.outputBusId ? byId.get(next.outputBusId) : undefined;
  }
  return chain;
}

const busHasProcessing = (b: AudioBus) =>
  presentStages(b.dynamics).length > 0 || (b.gainDb?.value ?? 0) !== 0 || (b.gainDb?.keys?.length ?? 0) > 0 || (b.pan ?? 0) !== 0 || !!b.eq;

/**
 * R08.6: the parity of a sound clip's track and bus processing. Null when there is none. The dynamics
 * stages and the bus chain are `approximate` only when the preview graph implements them (the gate and
 * the de-esser need `AudioWorklet`); a stage this browser cannot run stays `renderRequired`. Denoise and
 * the leveler are not previewed at all.
 */
function audioProcessingParity(doc: EditTimeline, itemId: string, dsp: DspSupport): { parity: ParityClass; reason: string } | null {
  const track = trackWith(doc, itemId);
  if (!track || track.kind !== 'audio') return null;
  const buses = busChain(doc, track);
  const dynamicsOf = (d: AudioDynamics | null | undefined) => d ?? null;
  const all = [dynamicsOf(track.dynamics), ...buses.map((b) => dynamicsOf(b.dynamics))];
  if (track.denoise || track.leveler || buses.some((b) => b.denoise || b.leveler)) {
    return { parity: 'renderRequired', reason: 'Denoise and the leveler are not previewed; the render applies them.' };
  }
  if (track.busId && buses.length > 0 && !planBusRouting(doc).ok) {
    return { parity: 'renderRequired', reason: 'These buses cannot be set up in the preview, so the tracks play straight to the master here; the render uses the buses.' };
  }
  const missing = [...new Set(all.flatMap((d) => unheardStages(d, dsp)))];
  if (missing.length > 0) {
    return { parity: 'renderRequired', reason: `${missing.map((s) => DSP_STAGE_LABEL[s]).join(', ')} ${missing.length === 1 ? 'is' : 'are'} heard after the render only in this browser.` };
  }
  const hasStages = all.some((d) => presentStages(d).length > 0);
  if (hasStages || buses.some(busHasProcessing)) {
    return { parity: 'approximate', reason: `${hasStages ? 'Compressor, gate, limiter and de-esser' : 'Bus routing and gain'} are heard in the preview approximately. ${PREVIEW_NOTE}` };
  }
  return null;
}

export function classifyParity(doc: EditTimeline, options: ParityOptions = {}): ParitySummary {
  const dsp = options.dsp ?? detectDspSupport();
  const items: ItemParity[] = [];
  let worst: ParityClass = 'faithful';
  let renderRequiredCount = 0;
  let approximateCount = 0;

  for (const track of doc.tracks) {
    for (const item of track.items) {
      const { parity, reason, findings } = classifyItem(item, doc, { ...options, dsp });
      items.push({ itemId: item.id, trackId: track.id, itemType: item.type, itemName: item.name ?? '', parity, reason, findings });
      if (RANK[parity] > RANK[worst]) worst = parity;
      if (parity === 'renderRequired') renderRequiredCount++;
      if (parity === 'approximate') approximateCount++;
    }
  }

  return { items, worst, renderRequiredCount, approximateCount };
}

/** The parity of what is on screen at one frame — the badge reads this, not the whole document. */
export function parityAtFrame(doc: EditTimeline, frame: number, options: ParityOptions = {}): ParitySummary {
  const dsp = options.dsp ?? detectDspSupport();
  const items: ItemParity[] = [];
  let worst: ParityClass = 'faithful';
  let renderRequiredCount = 0;
  let approximateCount = 0;
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.recordIn > frame || item.recordOut <= frame) continue;
      const { parity, reason, findings } = classifyItem(item, doc, { ...options, dsp });
      items.push({ itemId: item.id, trackId: track.id, itemType: item.type, itemName: item.name ?? '', parity, reason, findings });
      if (RANK[parity] > RANK[worst]) worst = parity;
      if (parity === 'renderRequired') renderRequiredCount++;
      if (parity === 'approximate') approximateCount++;
    }
  }
  return { items, worst, renderRequiredCount, approximateCount };
}
