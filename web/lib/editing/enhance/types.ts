/** Video enhancement: the five choices, their options, and the fixed request / response contract. */

export type EnhanceChoice = 'upscale' | 'interpolate' | 'denoise' | 'motionBlur' | 'extend';
export type EnhanceOp = 'Upscale' | 'Interpolate' | 'Denoise' | 'MotionBlur' | 'Extend';
export type EnhanceEngine = 'Local' | 'Hosted';

export const ENHANCE_CHOICES: readonly EnhanceChoice[] = ['upscale', 'interpolate', 'denoise', 'motionBlur', 'extend'];

export const OP_OF: Record<EnhanceChoice, EnhanceOp> = { upscale: 'Upscale', interpolate: 'Interpolate', denoise: 'Denoise', motionBlur: 'MotionBlur', extend: 'Extend' };

/** Which choices have an AI (credit-using) path; the other two are Fast only. */
export const HAS_AI: Record<EnhanceChoice, boolean> = { upscale: true, interpolate: true, denoise: false, motionBlur: false, extend: true };

/** Choices with no Fast path: Extend always sends the picture to the AI model. */
export const HOSTED_ONLY: Record<EnhanceChoice, boolean> = { upscale: false, interpolate: false, denoise: false, motionBlur: false, extend: true };

export type ExtendAspect = '16:9' | '9:16' | '1:1' | '4:5';
export const EXTEND_ASPECTS: readonly ExtendAspect[] = ['16:9', '9:16', '1:1', '4:5'];
/** How the customer asks for the new frame: a shape, or an amount per side. */
export type ExtendMode = 'aspect' | 'sides';
/** The longest Extend description, in characters (the server refuses longer). */
export const EXTEND_PROMPT_MAX = 500;
/** The largest amount Extend adds on one side, as a fraction of that side. */
export const EXTEND_MARGIN_MAX = 0.5;

export type UpscaleOption = 'x2' | 'x4' | 'h1080' | 'h2160';
export const UPSCALE_OPTIONS: readonly UpscaleOption[] = ['x2', 'x4', 'h1080', 'h2160'];
export const UPSCALE_HEIGHT: Partial<Record<UpscaleOption, number>> = { h1080: 1080, h2160: 2160 };
export const UPSCALE_FACTOR: Partial<Record<UpscaleOption, 2 | 4>> = { x2: 2, x4: 4 };

export type FrameMultiplier = 2 | 4 | 8;
export const FRAME_MULTIPLIERS: readonly FrameMultiplier[] = [2, 4, 8];
/** The AI path stops at 4x. */
export const AI_MAX_MULTIPLIER: FrameMultiplier = 4;

export type DenoiseStrength = 'Light' | 'Strong';
export const DENOISE_STRENGTHS: readonly DenoiseStrength[] = ['Light', 'Strong'];

export type ShutterName = 'light' | 'natural' | 'strong';
export const SHUTTER_ANGLE: Record<ShutterName, number> = { light: 90, natural: 180, strong: 360 };
export const SHUTTERS: readonly ShutterName[] = ['light', 'natural', 'strong'];

/** What the customer picked. */
export interface EnhanceSelection {
  choice: EnhanceChoice;
  engine: EnhanceEngine;
  upscale: UpscaleOption;
  multiplier: FrameMultiplier;
  strength: DenoiseStrength;
  keepGrain: boolean;
  shutter: ShutterName;
  /** Extend: a shape, or an amount per side. */
  extendMode: ExtendMode;
  extendAspect: ExtendAspect;
  /** Extend, sides mode: fractions of each side to add (0..0.5). */
  marginLeft: number;
  marginRight: number;
  marginTop: number;
  marginBottom: number;
  /** Extend: what the new edges should show; blank sends nothing and the server uses a neutral default. */
  prompt: string;
}

export const DEFAULT_SELECTION: EnhanceSelection = {
  choice: 'upscale',
  engine: 'Local',
  upscale: 'x2',
  multiplier: 2,
  strength: 'Light',
  keepGrain: true,
  shutter: 'natural',
  extendMode: 'aspect',
  // 1:1 is reachable from every ordinary source (a side may grow by at most half): 9:16 and 4:5 are not from a landscape clip.
  extendAspect: '1:1',
  marginLeft: 0.2,
  marginRight: 0.2,
  marginTop: 0.2,
  marginBottom: 0.2,
  prompt: '',
};

export interface EnhanceRequest {
  op: EnhanceOp;
  engine: EnhanceEngine;
  upscaleFactor?: 2 | 4;
  targetHeight?: 720 | 1080 | 1440 | 2160;
  frameMultiplier?: FrameMultiplier;
  denoiseStrength?: DenoiseStrength;
  keepGrain?: boolean;
  shutterAngle?: number;
  /** Extend: exactly one of targetAspect and the four margins. */
  targetAspect?: ExtendAspect;
  marginLeft?: number;
  marginRight?: number;
  marginTop?: number;
  marginBottom?: number;
  /** Extend: what the new edges should show, at most {@link EXTEND_PROMPT_MAX} characters. */
  prompt?: string;
}

const clampMargin = (value: number): number => (Number.isFinite(value) ? Math.min(EXTEND_MARGIN_MAX, Math.max(0, value)) : 0);

/** True when sides mode would add nothing: the server refuses it, so the UI does not ask for a price. */
export function extendMarginsEmpty(sel: Pick<EnhanceSelection, 'marginLeft' | 'marginRight' | 'marginTop' | 'marginBottom'>): boolean {
  return clampMargin(sel.marginLeft) + clampMargin(sel.marginRight) + clampMargin(sel.marginTop) + clampMargin(sel.marginBottom) <= 0;
}

export type ExtendSide = 'left' | 'right' | 'top' | 'bottom';
export const EXTEND_SIDES: readonly ExtendSide[] = ['left', 'right', 'top', 'bottom'];
/** The one amount offered when a side is first switched on (a fraction of that side). */
export const EXTEND_DEFAULT_AMOUNT = 0.2;

interface MarginSet {
  marginLeft?: number | null;
  marginRight?: number | null;
  marginTop?: number | null;
  marginBottom?: number | null;
}

const MARGIN_KEY: Record<ExtendSide, keyof MarginSet> = { left: 'marginLeft', right: 'marginRight', top: 'marginTop', bottom: 'marginBottom' };

/** The sides that grow. Wan has ONE amount for every side it grows, so "sides + one amount" is the whole model. */
export function extendSidesOn(m: MarginSet): ExtendSide[] {
  return EXTEND_SIDES.filter((s) => clampMargin(m[MARGIN_KEY[s]] ?? 0) > 0);
}

/** The one amount (fraction) shown for the sides that grow: the largest margin, or the default when none grows. */
export function extendAmountOf(m: MarginSet): number {
  const amounts = EXTEND_SIDES.map((s) => clampMargin(m[MARGIN_KEY[s]] ?? 0));
  const largest = Math.max(...amounts);
  return largest > 0 ? largest : EXTEND_DEFAULT_AMOUNT;
}

/** The four margins for these sides at one amount: every side that grows gets exactly that amount, the others 0. */
export function extendMargins(sides: readonly ExtendSide[], amount: number): { marginLeft: number; marginRight: number; marginTop: number; marginBottom: number } {
  const a = clampMargin(amount);
  const on = (s: ExtendSide) => (sides.includes(s) ? a : 0);
  return { marginLeft: on('left'), marginRight: on('right'), marginTop: on('top'), marginBottom: on('bottom') };
}

/** The contract body for a selection. Noise and motion blur are always Fast; Extend is always AI. */
export function requestOf(sel: EnhanceSelection): EnhanceRequest {
  const engine: EnhanceEngine = HOSTED_ONLY[sel.choice] ? 'Hosted' : HAS_AI[sel.choice] ? sel.engine : 'Local';
  switch (sel.choice) {
    case 'upscale': {
      const factor = UPSCALE_FACTOR[sel.upscale];
      const height = UPSCALE_HEIGHT[sel.upscale];
      return { op: 'Upscale', engine, ...(factor ? { upscaleFactor: factor } : {}), ...(height ? { targetHeight: height as 1080 | 2160 } : {}) };
    }
    case 'interpolate':
      return { op: 'Interpolate', engine, frameMultiplier: engine === 'Hosted' ? (Math.min(sel.multiplier, AI_MAX_MULTIPLIER) as FrameMultiplier) : sel.multiplier };
    case 'denoise':
      return { op: 'Denoise', engine, denoiseStrength: sel.strength, keepGrain: sel.keepGrain };
    case 'motionBlur':
      return { op: 'MotionBlur', engine, shutterAngle: SHUTTER_ANGLE[sel.shutter] };
    case 'extend': {
      const prompt = sel.prompt.trim().slice(0, EXTEND_PROMPT_MAX);
      const text = prompt ? { prompt } : {};
      if (sel.extendMode === 'aspect') return { op: 'Extend', engine: 'Hosted', targetAspect: sel.extendAspect, ...text };
      // Sides + ONE amount, whatever the four stored numbers say: the server refuses uneven margins.
      return { op: 'Extend', engine: 'Hosted', ...extendMargins(extendSidesOn(sel), extendAmountOf(sel)), ...text };
    }
  }
}

export interface EnhanceEstimate {
  billingEnabled: boolean;
  hosted: boolean;
  credits: number;
  seconds: number;
  balanceCredits: number;
  affordable: boolean;
  unavailableReason: string | null;
  /** PLAN_NOT_ENTITLED | SOURCE_TOO_LONG | SOURCE_NOT_MEASURED | ENHANCE_UNAVAILABLE | CONSENT_REQUIRED | RESOLUTION_ABOVE_PLAN. */
  unavailableCode?: string | null;
  /** The longest source the server accepts, sent with SOURCE_TOO_LONG. */
  maxSeconds?: number | null;
  consented: boolean;
  maxOutputHeight: number | null;
  outputWidth: number | null;
  outputHeight: number | null;
  outputFps: number | null;
  estimatedBytes: number | null;
  estimatedProcessingSeconds: number | null;
}

export interface EnhanceStart {
  executionId: string;
  op: EnhanceOp;
  engine: EnhanceEngine;
  hosted: boolean;
  state: EnhanceState;
  credits: number;
}

export type EnhanceState = 'queued' | 'running' | 'ready' | 'failed' | 'cancelled';

export interface EnhanceJob {
  executionId: string;
  state: EnhanceState;
  op: EnhanceOp;
  engine: EnhanceEngine;
  method?: string | null;
  durationSec?: number | null;
  outputFileId: string | null;
  sourceMediaRef: string;
  outputWidth?: number | null;
  outputHeight?: number | null;
  outputFps?: number | null;
  sizeBytes?: number | null;
  warnings?: string[];
  message?: string | null;
  failureCode?: string | null;
  /** The server's percent: 0 while queued, absent (null) while running, 100 when finished. */
  progress?: number | null;
}

export interface EnhanceConsent {
  consented: boolean;
  consentedAt: string | null;
}

/** Which options a plan or engine rules out, and why. `null` = available. */
export type EnhanceGate = null | 'plan' | 'engine';

/** An upscale option above the plan's output height is off (the plan reason is shown). `sourceHeight` makes the x2/x4 check exact when known. */
export function upscaleGate(option: UpscaleOption, maxOutputHeight: number | null, sourceHeight?: number | null): EnhanceGate {
  if (maxOutputHeight === null || maxOutputHeight <= 0) return null;
  const height = UPSCALE_HEIGHT[option];
  if (height !== undefined) return height > maxOutputHeight ? 'plan' : null;
  const factor = UPSCALE_FACTOR[option];
  if (factor !== undefined && sourceHeight && sourceHeight > 0) return sourceHeight * factor > maxOutputHeight ? 'plan' : null;
  return null;
}

/** Slow motion 8x is Fast only. */
export function multiplierGate(multiplier: FrameMultiplier, engine: EnhanceEngine): EnhanceGate {
  return engine === 'Hosted' && multiplier > AI_MAX_MULTIPLIER ? 'engine' : null;
}

/** The selection after an engine switch: AI cannot keep 8x. */
export function withEngine(sel: EnhanceSelection, engine: EnhanceEngine): EnhanceSelection {
  const next = { ...sel, engine };
  if (engine === 'Hosted' && next.multiplier > AI_MAX_MULTIPLIER) next.multiplier = AI_MAX_MULTIPLIER;
  return next;
}

/** The server checks the plan's cap on the output's SHORT side, so a portrait result is judged by its width. */
export function exceedsPlanHeight(estimate: EnhanceEstimate): boolean {
  const { maxOutputHeight: cap, outputWidth: w, outputHeight: h } = estimate;
  if (cap === null || cap <= 0 || h === null) return false;
  const short = w !== null && w > 0 ? Math.min(w, h) : h;
  return short > cap;
}

/**
 * Which sentence a job failure code gets (a key under `failure`). Every code the server can send is named; only a
 * truly unknown code falls back to the generic `failed`.
 */
export function failureKey(code: string | null | undefined): string {
  switch (code) {
    case 'ENHANCE_DURATION_MISMATCH': return 'ENHANCE_DURATION_MISMATCH';
    case 'ENHANCE_SOURCE_CHANGED': return 'ENHANCE_SOURCE_CHANGED';
    case 'timeout': return 'timeout';
    case 'ENHANCE_SOURCE_TOO_LONG': return 'tooLong';
    case 'ENHANCE_SOURCE_TOO_SHORT': return 'tooShort';
    case 'ENHANCE_SUBMIT_OUTCOME_UNKNOWN':
    case 'SUBMIT_OUTCOME_UNKNOWN': return 'submitUnknown';
    case 'CONTENT_BLOCKED': return 'contentBlocked';
    case 'CONFIG_INVALID': return 'configInvalid';
    case 'ENHANCE_SOURCE_NO_VIDEO': return 'noVideo';
    case 'ENHANCE_OUTPUT_TOO_LARGE': return 'outputTooLarge';
    case 'ENHANCE_FPS_TOO_HIGH': return 'fpsTooHigh';
    case 'ENHANCE_NOT_LARGER': return 'notLarger';
    case 'ENHANCE_INVALID_REQUEST':
    case 'ENHANCE_REQUEST_INVALID': return 'invalid';
    case 'ENHANCE_PROBE_FAILED':
    case 'ENHANCE_PICTURE_INVALID':
    case 'ENHANCE_FFMPEG_FAILED': return 'processing';
    case 'ENHANCE_VENDOR_QUOTA':
    case 'ENHANCE_VENDOR_RETRYABLE':
    case 'ENHANCE_VENDOR_FAILED':
    case 'ENHANCE_VENDOR_REFUSED':
    case 'ENHANCE_PROVIDER':
    case 'ENHANCE_PROVIDER_CONFIG':
    case 'ENHANCE_EGRESS_UNAVAILABLE':
    case 'ENHANCE_UPLOAD_FAILED': return 'service';
    case 'ENHANCE_STILL_RUNNING': return 'stillRunning';
    case 'OUTPUT_UNAVAILABLE': return 'outputUnavailable';
    default: return 'failed';
  }
}

/** The choice and options a request stands for, so a remounted section shows what is running. */
export function selectionOf(req: EnhanceRequest): EnhanceSelection {
  const choice: EnhanceChoice = req.op === 'Upscale' ? 'upscale' : req.op === 'Interpolate' ? 'interpolate' : req.op === 'Denoise' ? 'denoise' : req.op === 'Extend' ? 'extend' : 'motionBlur';
  const upscale: UpscaleOption = req.upscaleFactor === 4 ? 'x4' : req.targetHeight === 1080 ? 'h1080' : req.targetHeight === 2160 ? 'h2160' : 'x2';
  const shutter: ShutterName = req.shutterAngle === 90 ? 'light' : req.shutterAngle === 360 ? 'strong' : 'natural';
  const extendAspect: ExtendAspect = req.targetAspect && EXTEND_ASPECTS.includes(req.targetAspect) ? req.targetAspect : DEFAULT_SELECTION.extendAspect;
  return {
    choice,
    engine: req.engine,
    upscale,
    multiplier: req.frameMultiplier ?? 2,
    strength: req.denoiseStrength ?? 'Light',
    keepGrain: req.keepGrain ?? true,
    shutter,
    extendMode: req.op === 'Extend' && !req.targetAspect ? 'sides' : 'aspect',
    extendAspect,
    marginLeft: req.marginLeft ?? DEFAULT_SELECTION.marginLeft,
    marginRight: req.marginRight ?? DEFAULT_SELECTION.marginRight,
    marginTop: req.marginTop ?? DEFAULT_SELECTION.marginTop,
    marginBottom: req.marginBottom ?? DEFAULT_SELECTION.marginBottom,
    prompt: req.prompt ?? '',
  };
}

/**
 * The percent to show for a job still in progress, or null when it is unknown (indeterminate). The server sends 0
 * (queued), null (running) or 100 (finished); a run in progress never reads 100%.
 */
export function progressPercent(progress: number | null | undefined): number | null {
  if (typeof progress !== 'number' || !Number.isFinite(progress)) return null;
  return Math.min(99, Math.max(0, Math.round(progress)));
}
