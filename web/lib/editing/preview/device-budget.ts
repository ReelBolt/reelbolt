/**
 * Device-aware preview (R04.5): which preview a device should START on, and when playback that is
 * already running should offer the simple preview.
 *
 * "Auto" looks at what the device is (a phone, a software-graphics browser, a few CPU cores or little
 * memory) and sizes the LIVE preview for it: the picture is drawn no larger than the viewer shows it, with
 * a lower ceiling on a low-budget device. Every device starts on the live preview, so masks, grades,
 * effects and layers are always on screen. Edits stay exact: the final render never depends on the preview.
 *
 * Pure: the facts are passed in (see {@link readDeviceFacts} for the browser reader), so the rules test
 * without a browser.
 */

import { deviceClassOf, type DeviceClass } from '../device-class';
import { probeGl } from './gl-probe';

/** What the user picks. `auto` is the default. */
export type PreviewQuality = 'auto' | 'full' | 'half' | 'quarter';
export type PlaybackResolution = 'full' | 'half' | 'quarter';

export const PREVIEW_QUALITY_OPTIONS: { value: PreviewQuality; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'full', label: 'Full' },
  { value: 'half', label: 'Half' },
  { value: 'quarter', label: 'Quarter' },
];

export interface DeviceFacts {
  deviceClass: DeviceClass;
  /** `navigator.hardwareConcurrency`, when the browser reports it. */
  cores: number | null;
  /** `navigator.deviceMemory` in GB, when the browser reports it (Chromium only). */
  memoryGb: number | null;
  /** The graphics context is a CPU rasteriser (SwiftShader, llvmpipe...). */
  softwareGraphics: boolean;
}

export const LOW_BUDGET_MAX_CORES = 4;
export const LOW_BUDGET_MAX_MEMORY_GB = 4;

/** Why a device counts as low-budget, structured so the UI can say it in any language (`autoPlanText`). */
export type BudgetReasonCode = { code: 'phone' } | { code: 'software' } | { code: 'cores'; n: number } | { code: 'memory'; n: number };

export interface BudgetVerdict {
  low: boolean;
  /** Plain English words for why (stable fallback for non-UI callers), empty when the device is not low-budget. */
  reasons: string[];
  /** The same reasons as codes plus counts, in the same order. */
  codes: BudgetReasonCode[];
}

export function budgetOf(facts: DeviceFacts): BudgetVerdict {
  const reasons: string[] = [];
  const codes: BudgetReasonCode[] = [];
  if (facts.deviceClass === 'phone') {
    reasons.push('this is a phone');
    codes.push({ code: 'phone' });
  }
  if (facts.softwareGraphics) {
    reasons.push('the browser draws with software graphics');
    codes.push({ code: 'software' });
  }
  if (facts.cores !== null && facts.cores <= LOW_BUDGET_MAX_CORES) {
    reasons.push(`it has ${facts.cores} processor cores`);
    codes.push({ code: 'cores', n: facts.cores });
  }
  if (facts.memoryGb !== null && facts.memoryGb <= LOW_BUDGET_MAX_MEMORY_GB) {
    reasons.push(`it has about ${facts.memoryGb} GB of memory`);
    codes.push({ code: 'memory', n: facts.memoryGb });
  }
  return { low: reasons.length > 0, reasons, codes };
}

/** Auto's long-side ceilings (device pixels): the picture is drawn no larger than the viewer shows it, and never past these. */
export const AUTO_MAX_LONG_SIDE = 1280;
export const AUTO_MAX_LONG_SIDE_LOW = 854;
export const AUTO_MAX_LONG_SIDE_PHONE = 640;
export const AUTO_MAX_LONG_SIDE_SOFTWARE = 480;

export interface PlaybackPlan {
  /** The size the session starts at (and stays at for an explicit pick): a fraction of the timeline's size. */
  resolution: PlaybackResolution;
  /** Start on the Simple preview (single clip, from its small copy). Auto never does: the live preview adapts instead. */
  simple: boolean;
  /**
   * Auto only: draw the picture no larger than the viewer shows it, with its long side capped at this many device
   * pixels. Null for an explicit pick (Full / Half / Quarter are exact fractions of the timeline).
   */
  fitMaxLongSide: number | null;
  /** Why Auto holds the size down on this device, one sentence. Null when it does not (or on a phone, whose screen is the reason). */
  autoReason: string | null;
  /** The same decision, structured for translation (`autoPlanText`); absent when `autoReason` is null. */
  autoWhy?: { mode: 'reduced'; codes: BudgetReasonCode[] };
}

/**
 * What the viewer plays with: the user's pick, or for `auto` what the device can carry.
 *
 * Auto ALWAYS starts on the live preview, on every device class, so every layer, mask, grade and effect is on
 * screen. A weak device gets it at a smaller size (and the session lowers the size further while playing if frames
 * are skipped, see `resolution-governor.ts`); paused, the picture is back at the full playback size. The Simple
 * preview is only the user's own switch, or the offer once the live preview has nothing left to give.
 */
export function planPlayback(quality: PreviewQuality, facts: DeviceFacts): PlaybackPlan {
  if (quality !== 'auto') return { resolution: quality, simple: false, fitMaxLongSide: null, autoReason: null };
  const verdict = budgetOf(facts);
  if (!verdict.low) return { resolution: 'half', simple: false, fitMaxLongSide: AUTO_MAX_LONG_SIDE, autoReason: null };
  const fitMaxLongSide = facts.softwareGraphics ? AUTO_MAX_LONG_SIDE_SOFTWARE : facts.deviceClass === 'phone' ? AUTO_MAX_LONG_SIDE_PHONE : AUTO_MAX_LONG_SIDE_LOW;
  // A phone's own screen is why its picture is small: only the other reasons are worth a sentence.
  const codes = verdict.codes.filter((c) => c.code !== 'phone');
  const reasons = verdict.reasons.filter((r) => r !== 'this is a phone');
  if (codes.length === 0) return { resolution: 'quarter', simple: false, fitMaxLongSide, autoReason: null };
  return {
    resolution: 'quarter',
    simple: false,
    fitMaxLongSide,
    autoReason: `The preview is drawn at a smaller size because ${reasons.join(' and ')}.`,
    autoWhy: { mode: 'reduced', codes },
  };
}

const NO_FACTS: DeviceFacts = { deviceClass: 'desktop', cores: null, memoryGb: null, softwareGraphics: false };

/** Whether the graphics context is a CPU rasteriser, from the page's one shared probe (see gl-probe.ts). */
function probeSoftwareGraphics(): boolean {
  return probeGl().software;
}

/** Reads this browser's facts. Safe on the server (returns neutral desktop facts). */
export function readDeviceFacts(): DeviceFacts {
  if (typeof window === 'undefined' || typeof navigator === 'undefined') return NO_FACTS;
  const nav = navigator as Navigator & { deviceMemory?: number };
  const coarse = typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
  return {
    deviceClass: deviceClassOf({ width: window.innerWidth, height: window.innerHeight, coarsePointer: coarse }),
    cores: typeof nav.hardwareConcurrency === 'number' && nav.hardwareConcurrency > 0 ? nav.hardwareConcurrency : null,
    memoryGb: typeof nav.deviceMemory === 'number' && nav.deviceMemory > 0 ? nav.deviceMemory : null,
    softwareGraphics: probeSoftwareGraphics(),
  };
}

// ── sustained dropped frames ─────────────────────────────────────────────────

/** A sample of the ledger: deadlines seen and frames dropped so far, at a time (ms). */
export interface DropSample {
  at: number;
  deadlines: number;
  dropped: number;
}

export const DROP_WINDOW_MS = 3000;
export const DROP_MIN_DEADLINES = 30;
/** More than this share of the window's frames dropped counts as "sustained". */
export const DROP_SUSTAINED_RATIO = 0.25;

export interface DropWatch {
  /** Adds the latest ledger reading. Returns true while drops are sustained. */
  push(sample: DropSample): boolean;
  reset(): void;
}

/**
 * Watches the ledger and says when the last {@link DROP_WINDOW_MS} of playback dropped more than
 * {@link DROP_SUSTAINED_RATIO} of its frames. A counter that goes backwards (a new play) starts over.
 */
export function createDropWatch(): DropWatch {
  let samples: DropSample[] = [];
  return {
    push(sample) {
      const last = samples[samples.length - 1];
      if (last && (sample.deadlines < last.deadlines || sample.at < last.at)) samples = [];
      samples.push(sample);
      while (samples.length > 2 && sample.at - samples[1].at >= DROP_WINDOW_MS) samples.shift();
      const first = samples[0];
      if (sample.at - first.at < DROP_WINDOW_MS * 0.6) return false;
      const deadlines = sample.deadlines - first.deadlines;
      if (deadlines < DROP_MIN_DEADLINES) return false;
      return (sample.dropped - first.dropped) / deadlines > DROP_SUSTAINED_RATIO;
    },
    reset() {
      samples = [];
    },
  };
}

// ── the remembered pick ──────────────────────────────────────────────────────

export const PREVIEW_QUALITY_STORAGE_KEY = 'reelbolt:editor:preview-quality';

/** The quality the user last picked on this device (`auto` when they never did, or storage is off). */
export function readStoredQuality(): PreviewQuality {
  try {
    const raw = typeof localStorage === 'undefined' ? null : localStorage.getItem(PREVIEW_QUALITY_STORAGE_KEY);
    return raw === 'full' || raw === 'half' || raw === 'quarter' || raw === 'auto' ? raw : 'auto';
  } catch {
    return 'auto';
  }
}

export function storeQuality(quality: PreviewQuality): void {
  try {
    localStorage.setItem(PREVIEW_QUALITY_STORAGE_KEY, quality);
  } catch {
    /* storage unavailable: the pick lasts for this visit only */
  }
}
