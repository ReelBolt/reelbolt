import type { TimelineRenderEncoderSpeed, TimelineRenderPreset, TimelineRenderRateControl, TimelineRenderVideoCodec } from '@/lib/types/workflow';
import { fitsOutputCap } from '@/components/styles/style-options';
import { newGuid } from '@/lib/editing/ids';
import type { RenderQuality } from './deliver-model';
import { MAX_AUDIO_KBPS, MAX_VIDEO_KBPS, MIN_AUDIO_KBPS, MIN_VIDEO_KBPS } from './deliver-platforms';

/**
 * The Deliver page's preset model: what each render preset produces, whether the plan allows it, the settings a
 * person can save as their own preset, and the settings this device remembers for the renders it started.
 * Nothing here knows a price or talks to the server.
 */

/** The picture each fixed preset makes; `Master` follows the timeline and `AudioOnly` has no picture. */
export const PRESET_SIZE: Partial<Record<TimelineRenderPreset, { width: number; height: number; shape: string }>> = {
  Hd1080p16x9: { width: 1920, height: 1080, shape: '16:9' },
  Uhd4k16x9: { width: 3840, height: 2160, shape: '16:9' },
  Vertical9x16: { width: 1080, height: 1920, shape: '9:16' },
  Square1x1: { width: 1080, height: 1080, shape: '1:1' },
  Portrait4x5: { width: 1080, height: 1350, shape: '4:5' },
};

/** The one preset that is an explicit size choice: above the plan it is refused, never scaled down. */
export const EXPLICIT_SIZE_PRESET: TimelineRenderPreset = 'Uhd4k16x9';

/** The short side a preset renders at; `Master` takes the timeline's own size; null = no picture or unknown. */
export function presetShortSide(preset: TimelineRenderPreset, masterShortSide: number | null): number | null {
  if (preset === 'Master') return masterShortSide;
  const size = PRESET_SIZE[preset];
  return size ? Math.min(size.width, size.height) : null;
}

/**
 * What the plan does with a preset: `ok` renders as chosen, `clamped` still renders but the engine scales it down to
 * the plan's largest size, `refused` is the explicit 4K choice above the plan (the server answers 402 for it).
 */
export type PresetAvailability = 'ok' | 'clamped' | 'refused';

export function presetAvailability(preset: TimelineRenderPreset, masterShortSide: number | null, maxOutputHeight: number | null): PresetAvailability {
  if (fitsOutputCap(presetShortSide(preset, masterShortSide), maxOutputHeight)) return 'ok';
  return preset === EXPLICIT_SIZE_PRESET ? 'refused' : 'clamped';
}

/** The picture a preset makes, for the list and the summary; null when it has none or the size is unknown. */
export function presetPicture(preset: TimelineRenderPreset, timelineSize: { width: number; height: number } | null): { width: number; height: number } | null {
  if (preset === 'AudioOnly') return null;
  if (preset === 'Master') return timelineSize && timelineSize.width > 0 && timelineSize.height > 0 ? { width: timelineSize.width, height: timelineSize.height } : null;
  const size = PRESET_SIZE[preset];
  return size ? { width: size.width, height: size.height } : null;
}

/* ---- The settings a preset of your own holds ---- */

/** Everything the settings form holds except the range and the file name, which belong to one render. */
export interface RenderFormSettings {
  preset: TimelineRenderPreset;
  quality: RenderQuality;
  custom: boolean;
  codec: TimelineRenderVideoCodec;
  crf: number;
  speed: TimelineRenderEncoderSpeed;
  rateControl: TimelineRenderRateControl;
  videoKbps: number;
  audioKbps: number;
  normalise: boolean;
  targetLufs: number;
  truePeak: number;
  loudnessRange: number;
}

/** What the form starts on, apart from the format (which follows the timeline's shape). */
export const DEFAULT_FORM_SETTINGS: Omit<RenderFormSettings, 'preset'> = Object.freeze({
  quality: 'good',
  custom: false,
  codec: 'H264',
  crf: 18,
  speed: 'Medium',
  rateControl: 'Quality',
  videoKbps: 8000,
  audioKbps: 192,
  normalise: false,
  targetLufs: -16,
  truePeak: -1.5,
  loudnessRange: 11,
});

const PRESETS: readonly TimelineRenderPreset[] = ['Hd1080p16x9', 'Uhd4k16x9', 'Vertical9x16', 'Square1x1', 'Portrait4x5', 'Master', 'AudioOnly'];
const QUALITIES: readonly RenderQuality[] = ['draft', 'good', 'best'];
const CODEC_VALUES: readonly TimelineRenderVideoCodec[] = ['H264', 'H265'];
const SPEED_VALUES: readonly TimelineRenderEncoderSpeed[] = ['Fast', 'Medium', 'Slow'];
const RATE_VALUES: readonly TimelineRenderRateControl[] = ['Quality', 'Capped', 'Average'];

const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const oneOf = <T extends string>(v: unknown, allowed: readonly T[], fallback: T): T => (typeof v === 'string' && (allowed as readonly string[]).includes(v) ? (v as T) : fallback);
const num = (v: unknown, lo: number, hi: number, fallback: number): number => (typeof v === 'number' && Number.isFinite(v) ? Math.min(hi, Math.max(lo, v)) : fallback);

/**
 * Stored settings back into the form's shape: every word is checked against its allowlist and every number is put
 * back in the range the server accepts, so a hand-edited or older entry can never send a value the form could not.
 * A value that is not an object with a known format is nothing.
 */
export function sanitizeFormSettings(raw: unknown): RenderFormSettings | null {
  if (!isObj(raw) || typeof raw.preset !== 'string' || !(PRESETS as readonly string[]).includes(raw.preset)) return null;
  const d = DEFAULT_FORM_SETTINGS;
  return {
    preset: raw.preset as TimelineRenderPreset,
    quality: oneOf(raw.quality, QUALITIES, d.quality),
    custom: raw.custom === true,
    codec: oneOf(raw.codec, CODEC_VALUES, d.codec),
    crf: Math.round(num(raw.crf, 0, 51, d.crf)),
    speed: oneOf(raw.speed, SPEED_VALUES, d.speed),
    rateControl: oneOf(raw.rateControl, RATE_VALUES, d.rateControl),
    videoKbps: Math.round(num(raw.videoKbps, MIN_VIDEO_KBPS, MAX_VIDEO_KBPS, d.videoKbps)),
    audioKbps: Math.round(num(raw.audioKbps, MIN_AUDIO_KBPS, MAX_AUDIO_KBPS, d.audioKbps)),
    normalise: raw.normalise === true,
    targetLufs: num(raw.targetLufs, -36, -5, d.targetLufs),
    truePeak: num(raw.truePeak, -9, 0, d.truePeak),
    loudnessRange: num(raw.loudnessRange, 1, 30, d.loudnessRange),
  };
}

/** True when two sets of settings would send the same render. */
export const sameFormSettings = (a: RenderFormSettings, b: RenderFormSettings): boolean => JSON.stringify(sanitizeFormSettings(a)) === JSON.stringify(sanitizeFormSettings(b));

/* ---- "My presets": kept on this device ---- */

export interface MyRenderPreset {
  id: string;
  name: string;
  settings: RenderFormSettings;
}

export const MY_PRESETS_KEY = 'reelbolt:editor:deliver:my-presets';
export const MAX_MY_PRESETS = 20;
export const MY_PRESET_NAME_MAX = 40;

/** The storage the stores use: the browser's, or nothing when it is switched off or refuses to open. */
export type PresetStorage = Pick<Storage, 'getItem' | 'setItem'>;

function browserStorage(): PresetStorage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
}

/** A name as it is stored: trimmed, single spaces, no control characters, at most 40 characters; null when nothing is left. */
export function cleanPresetName(name: string): string | null {
  const cleaned = name.replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, MY_PRESET_NAME_MAX).trim();
  return cleaned === '' ? null : cleaned;
}

/** Why a name cannot be used for a new or renamed preset; null when it can. */
export type PresetNameProblem = 'empty' | 'taken' | 'full';

export function presetNameProblem(name: string, presets: readonly MyRenderPreset[], renamingId: string | null = null): PresetNameProblem | null {
  const cleaned = cleanPresetName(name);
  if (cleaned === null) return 'empty';
  if (presets.some((p) => p.id !== renamingId && p.name.toLowerCase() === cleaned.toLowerCase())) return 'taken';
  if (renamingId === null && presets.length >= MAX_MY_PRESETS) return 'full';
  return null;
}

/** The saved presets of this device. `available` is false when the browser does not let the page keep anything. */
export function readMyPresets(storage: PresetStorage | null = browserStorage()): { presets: MyRenderPreset[]; available: boolean } {
  if (!storage) return { presets: [], available: false };
  let raw: string | null;
  try {
    raw = storage.getItem(MY_PRESETS_KEY);
  } catch {
    return { presets: [], available: false };
  }
  // A value that cannot be read back is an empty list, not a reason to stop offering presets.
  let parsed: unknown = [];
  try {
    parsed = raw ? JSON.parse(raw) : [];
  } catch {
    parsed = [];
  }
  if (!Array.isArray(parsed)) return { presets: [], available: true };
  const presets: MyRenderPreset[] = [];
  for (const entry of parsed) {
    if (!isObj(entry) || typeof entry.id !== 'string' || typeof entry.name !== 'string') continue;
    const name = cleanPresetName(entry.name);
    const settings = sanitizeFormSettings(entry.settings);
    if (!name || !settings || presets.some((p) => p.id === entry.id)) continue;
    presets.push({ id: entry.id, name, settings });
    if (presets.length >= MAX_MY_PRESETS) break;
  }
  return { presets, available: true };
}

/** Stores the list; false when the browser refused (private mode, full storage), so the caller can say so. */
export function writeMyPresets(presets: readonly MyRenderPreset[], storage: PresetStorage | null = browserStorage()): boolean {
  if (!storage) return false;
  try {
    storage.setItem(MY_PRESETS_KEY, JSON.stringify(presets.slice(0, MAX_MY_PRESETS)));
    return true;
  } catch {
    return false;
  }
}

export function addMyPreset(presets: readonly MyRenderPreset[], name: string, settings: RenderFormSettings): MyRenderPreset[] {
  const cleaned = cleanPresetName(name);
  const safe = sanitizeFormSettings(settings);
  if (!cleaned || !safe || presetNameProblem(cleaned, presets) !== null) return [...presets];
  return [...presets, { id: newGuid(), name: cleaned, settings: safe }];
}

export function renameMyPreset(presets: readonly MyRenderPreset[], id: string, name: string): MyRenderPreset[] {
  const cleaned = cleanPresetName(name);
  if (!cleaned || presetNameProblem(cleaned, presets, id) !== null) return [...presets];
  return presets.map((p) => (p.id === id ? { ...p, name: cleaned } : p));
}

export const removeMyPreset = (presets: readonly MyRenderPreset[], id: string): MyRenderPreset[] => presets.filter((p) => p.id !== id);

/* ---- The settings of the renders this device started, for "Render again" ---- */

/** Where the captions of a render went, as the Deliver page names the three choices. */
export type SentCaptions = 'burn' | 'sidecar' | 'both';

/** What a render was started with. The server's queue row carries only the format, so this is the only full record. */
export interface SentRender {
  settings: RenderFormSettings;
  range: { inFrame: number; outFrame: number } | null;
  fileName: string;
  captions: SentCaptions | null;
}

export const SENT_RENDERS_KEY = 'reelbolt:editor:deliver:sent-renders';
export const MAX_SENT_RENDERS = 40;

function sanitizeSent(raw: unknown): SentRender | null {
  if (!isObj(raw)) return null;
  const settings = sanitizeFormSettings(raw.settings);
  if (!settings) return null;
  const r = raw.range;
  const range =
    isObj(r) && typeof r.inFrame === 'number' && typeof r.outFrame === 'number' && Number.isInteger(r.inFrame) && Number.isInteger(r.outFrame) && r.inFrame >= 0 && r.outFrame > r.inFrame
      ? { inFrame: r.inFrame, outFrame: r.outFrame }
      : null;
  const fileName = typeof raw.fileName === 'string' && /^[A-Za-z0-9][A-Za-z0-9._ -]{0,119}$/.test(raw.fileName) ? raw.fileName : '';
  const captions = raw.captions === 'burn' || raw.captions === 'sidecar' || raw.captions === 'both' ? raw.captions : null;
  return { settings, range, fileName, captions };
}

/** Render id → what it was started with, newest last. Unreadable storage reads as "nothing remembered". */
export function readSentRenders(storage: PresetStorage | null = browserStorage()): Map<string, SentRender> {
  const out = new Map<string, SentRender>();
  if (!storage) return out;
  try {
    const raw = storage.getItem(SENT_RENDERS_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(parsed)) return out;
    for (const entry of parsed) {
      if (!Array.isArray(entry) || typeof entry[0] !== 'string') continue;
      const sent = sanitizeSent(entry[1]);
      if (sent) out.set(entry[0], sent);
    }
  } catch {
    /* nothing remembered */
  }
  return out;
}

/** Remembers one started render (keeping the newest 40). A refused write only means "Render again" is not offered for it later. */
export function rememberSentRender(executionId: string, sent: SentRender, storage: PresetStorage | null = browserStorage()): Map<string, SentRender> {
  const all = readSentRenders(storage);
  const safe = sanitizeSent(sent);
  if (!safe) return all;
  all.delete(executionId);
  all.set(executionId, safe);
  const kept = new Map([...all].slice(-MAX_SENT_RENDERS));
  try {
    storage?.setItem(SENT_RENDERS_KEY, JSON.stringify([...kept]));
  } catch {
    /* kept for this visit only */
  }
  return kept;
}
