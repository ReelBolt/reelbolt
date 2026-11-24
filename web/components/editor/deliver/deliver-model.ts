import type { EditTimelineRenderSummary, RenderEditTimelineRequest } from '@/lib/editing/sync/api';
import { msg, type Msg } from '@/i18n/msg';
import type { TimelineRenderEncoderSpeed, TimelineRenderPreset, TimelineRenderRateControl, TimelineRenderVideoCodec } from '@/lib/types/workflow';

/** The Deliver panel's plain-language model: format names, quality steps, the status line, the credit estimate. */

/** Each render format as a person names it, in the order the Format picker lists them. */
export const FORMAT_OPTIONS: { value: TimelineRenderPreset; label: string }[] = [
  { value: 'Hd1080p16x9', label: 'Full HD landscape' },
  { value: 'Uhd4k16x9', label: '4K landscape' },
  { value: 'Vertical9x16', label: 'Vertical for phones' },
  { value: 'Square1x1', label: 'Square' },
  { value: 'Portrait4x5', label: 'Portrait 4:5' },
  { value: 'Master', label: 'Master (large, for editing elsewhere)' },
  { value: 'AudioOnly', label: 'Audio only' },
];

/** The name of a format, from the preset the server reports (any unknown value is shown as it is, never blank). */
export function formatName(preset: string): string {
  return FORMAT_OPTIONS.find((o) => o.value === preset)?.label ?? preset;
}

/** Width over height of each fixed-shape format; `Master` follows the timeline and `AudioOnly` has no picture. */
const SHAPE: Partial<Record<TimelineRenderPreset, number>> = {
  Hd1080p16x9: 16 / 9,
  Uhd4k16x9: 16 / 9,
  Vertical9x16: 9 / 16,
  Square1x1: 1,
  Portrait4x5: 4 / 5,
};

const gcd = (a: number, b: number): number => (b === 0 ? a : gcd(b, a % b));

/** "16:9" for a 1920x1080 timeline; the plain ratio of the two sides. */
export function aspectLabel(width: number, height: number): string {
  const d = gcd(Math.round(width), Math.round(height)) || 1;
  const w = Math.round(width) / d;
  const h = Math.round(height) / d;
  // Odd sizes (1366x768) reduce to something unreadable: say it in one decimal instead.
  return w > 40 || h > 40 ? `${(width / height).toFixed(2)}:1` : `${w}:${h}`;
}

/**
 * The sentence for a shape that will not fit the timeline's picture, or null when it fits (or has no picture
 * shape to compare: Master renders at the timeline's own shape, Audio only has no picture).
 */
export function fitWarning(timeline: { width: number; height: number } | null, preset: TimelineRenderPreset): string | null {
  const shape = SHAPE[preset];
  if (!timeline || shape === undefined || timeline.width <= 0 || timeline.height <= 0) return null;
  const own = timeline.width / timeline.height;
  if (Math.abs(own - shape) / shape < 0.02) return null;
  return `This shape will crop or letterbox your picture: your timeline is ${aspectLabel(timeline.width, timeline.height)} and ${formatName(preset)} is not.`;
}

/**
 * The format a new Deliver panel starts on: the fixed shape that matches the timeline's own picture (9:16 ->
 * Vertical for phones, 1:1 -> Square, 4:5 -> Portrait; odd sizes take the nearest shape). Phones always start vertical;
 * an unknown size starts on Full HD landscape.
 */
export function defaultPresetFor(timeline: { width: number; height: number } | null, phone: boolean): TimelineRenderPreset {
  // The phone always starts vertical, as before.
  if (phone || !timeline || !(timeline.width > 0) || !(timeline.height > 0)) return phone ? 'Vertical9x16' : 'Hd1080p16x9';
  const own = Math.log(timeline.width / timeline.height);
  let best: TimelineRenderPreset = 'Hd1080p16x9';
  let bestDistance = Infinity;
  for (const preset of ['Hd1080p16x9', 'Vertical9x16', 'Square1x1', 'Portrait4x5'] as const) {
    const distance = Math.abs(own - Math.log(SHAPE[preset] as number));
    if (distance < bestDistance) {
      best = preset;
      bestDistance = distance;
    }
  }
  return best;
}

export type RenderQuality = 'draft' | 'good' | 'best';

/** The quality steps in the order they are listed; their words live in `editor.deliver.renderSettings.qualitySteps`. */
export const QUALITY_STEPS: { value: RenderQuality }[] = [{ value: 'draft' }, { value: 'good' }, { value: 'best' }];

/** The encoder settings behind a quality step; `Good` is the format's own default (null = send nothing). */
export function qualityEncode(quality: RenderQuality): { codec: TimelineRenderVideoCodec; crf: number; speed: TimelineRenderEncoderSpeed } | null {
  if (quality === 'draft') return { codec: 'H264', crf: 28, speed: 'Fast' };
  if (quality === 'best') return { codec: 'H264', crf: 16, speed: 'Slow' };
  return null;
}

/**
 * The status line under the film card. `renderRevision` is the revision the latest render was made from;
 * `revision` the one on screen. Each saved change moves the revision by one, so the gap is the count of changes.
 */
export function renderStatusLine(renderRevision: number | null, revision: number): string {
  if (renderRevision === null) return 'Last render: your latest edit';
  const n = Math.max(0, revision - renderRevision);
  if (n === 0) return 'Last render: your latest edit';
  return `Last render: before your latest edits (${n} ${n === 1 ? 'change' : 'changes'} since)`;
}

/** What the estimate endpoint answers (EditTimelineRenderEstimateResponse). */
export interface RenderEstimate {
  billingEnabled: boolean;
  credits?: number | null;
  creditsLow?: number | null;
  creditsHigh?: number | null;
  estimateSeconds?: number | null;
  balanceCredits?: number | null;
  affordable: boolean;
  unavailableReason?: string | null;
  estimatedSizeBytes?: number | null;
  sizeIsApproximate?: boolean | null;
}

const trimNumber = (n: number): string => (Number.isInteger(n) ? String(n) : n.toFixed(1).replace(/\.0$/, ''));

/**
 * "About 12 credits, about 3 min", or null when there is nothing honest to say: billing off (never "0 credits"),
 * no figure computed. A render under a minute says "under 1 min".
 */
export function formatEstimate(estimate: RenderEstimate | null | undefined): string | null {
  if (!estimate || !estimate.billingEnabled || estimate.credits === null || estimate.credits === undefined) return null;
  const credits = estimate.credits;
  const creditsText = `About ${trimNumber(Math.max(0, Math.round(credits * 10) / 10))} ${credits === 1 ? 'credit' : 'credits'}`;
  const seconds = estimate.estimateSeconds;
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return creditsText;
  const minutes = Math.round(seconds / 60);
  return `${creditsText}, ${seconds < 60 ? 'under 1 min' : `about ${minutes} min`}`;
}

/** How many more credits the render needs than the balance holds; null when it is affordable or unknown. */
export function creditsMissing(estimate: RenderEstimate | null | undefined): number | null {
  if (!estimate || !estimate.billingEnabled || estimate.affordable) return null;
  if (estimate.credits === null || estimate.credits === undefined || estimate.balanceCredits === null || estimate.balanceCredits === undefined) return null;
  const missing = estimate.credits - estimate.balanceCredits;
  return missing > 0 ? Math.ceil(missing * 10) / 10 : null;
}

/** "You need 3 more credits" (or null). */
export function missingSentence(missing: number | null): string | null {
  if (missing === null || missing <= 0) return null;
  return `You need ${trimNumber(missing)} more ${missing === 1 ? 'credit' : 'credits'}`;
}

/** The plain word for each state a render can be in. */
export function statusWord(status: string): string {
  switch (status) {
    case 'Queued': return 'Waiting';
    case 'Running': return 'Rendering';
    case 'Passed': return 'Finished';
    case 'Failed': return 'Did not finish';
    case 'Cancelled': return 'Stopped';
    default: return status;
  }
}

/** The newest render that finished with a file, or null. Input order is the server's (newest first); the date decides regardless. */
export function latestFinished(renders: readonly EditTimelineRenderSummary[] | undefined): EditTimelineRenderSummary | null {
  let best: EditTimelineRenderSummary | null = null;
  for (const r of renders ?? []) {
    if (r.status !== 'Passed' || !r.hasOutput || !r.stepResultId) continue;
    const when = (x: EditTimelineRenderSummary) => Date.parse(x.completedAt ?? x.createdAt ?? x.startedAt ?? '') || 0;
    if (!best || when(r) > when(best)) best = r;
  }
  return best;
}

/** "1:23" or "0:07" for a length in seconds. */
export function formatLength(seconds: number | null | undefined): string | null {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) return null;
  const total = Math.round(seconds);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}

/** The settings the form holds, as the render request. */
export interface RenderFormState {
  revision: number;
  preset: TimelineRenderPreset;
  range: { inFrame: number; outFrame: number } | null;
  fileName: string;
  quality: RenderQuality;
  advanced: { codec: TimelineRenderVideoCodec; crf: number; speed: TimelineRenderEncoderSpeed; audioKbps: number } | null;
  /** Size and quality: how the video bitrate is held; `Quality` (or absent) sends nothing new. */
  rate?: { control: TimelineRenderRateControl; videoKbps: number } | null;
  /** Size and quality: the audio bitrate; absent or 192 is the default. */
  audioKbps?: number;
  loudness: { targetLufs: number; truePeakDbtp: number; loudnessRange: number } | null;
}

/** The encoder values a format starts from when only the bitrate changes (`Master` is the one that differs). */
function baseEncode(preset: TimelineRenderPreset): { codec: TimelineRenderVideoCodec; crf: number; speed: TimelineRenderEncoderSpeed } {
  return preset === 'Master' ? { codec: 'H264', crf: 14, speed: 'Slow' } : { codec: 'H264', crf: 18, speed: 'Medium' };
}

export function buildRenderRequest(form: RenderFormState): RenderEditTimelineRequest {
  const audioOnly = form.preset === 'AudioOnly';
  const audioKbps = form.advanced?.audioKbps ?? form.audioKbps ?? 192;
  const rate = !audioOnly && form.rate && form.rate.control !== 'Quality' ? form.rate : null;
  const picked = audioOnly ? null : form.advanced ?? qualityEncode(form.quality) ?? (rate || audioKbps !== 192 ? baseEncode(form.preset) : null);
  return {
    revision: form.revision,
    preset: form.preset,
    range: form.range,
    outputFileName: form.fileName.trim() === '' ? null : form.fileName.trim(),
    registerProjectFile: true,
    loudness: form.loudness,
    encode: picked
      ? {
          width: null, height: null, codec: picked.codec, crf: picked.crf, speed: picked.speed, audioBitrateKbps: audioKbps,
          ...(rate ? { rateControl: rate.control, videoBitrateKbps: rate.videoKbps } : {}),
        }
      : null,
  };
}

/** The size line ("About 12.5 MB"), or null when the estimate has no size. Every size is approximate, so it always says "about". */
export function formatSizeMsg(estimate: RenderEstimate | null | undefined): Msg | null {
  const bytes = estimate?.estimatedSizeBytes;
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes <= 0) return null;
  const gb = bytes >= 1e9;
  const value = new Intl.NumberFormat(undefined, { maximumFractionDigits: gb || bytes < 1e7 ? 2 : 1 }).format(bytes / (gb ? 1e9 : 1e6));
  if (gb) return msg(`${H}.sizeLine.aboutGb`, { size: value });
  return msg(`${H}.sizeLine.aboutMb`, { size: value });
}

/** The part of the sync client's state the "Save first" sentence reads. */
export interface SaveFailureSource {
  status: string;
  message: string | null;
  reasons?: readonly string[];
}

/** Why the save before a render did not land, as one plain sentence (the panel puts "Save first: " in front). */
export function saveFailureSentence(state: SaveFailureSource): string {
  switch (state.status) {
    case 'conflict':
      return 'This timeline was changed somewhere else, so your edits here are not saved.';
    case 'offline':
    case 'reconnecting':
      return 'You are offline, so your latest edits are not saved yet.';
    case 'saving':
    case 'dirty':
    case 'retrying':
      return 'Your latest edits are still saving. Try again in a moment.';
    default: {
      const reason = state.reasons?.find((r) => r.trim() !== '') ?? state.message;
      if (!reason || !reason.trim()) return 'Your latest edits could not be saved.';
      const t = reason.trim();
      return /[.!?]$/.test(t) ? t : `${t}.`;
    }
  }
}

/* ---- Msg counterparts: the same sentences as catalogue keys, resolved with `t.msg(...)` at display time. ---- */

const H = 'editor-helpers-1.deliver';

/** {@link formatName} as a Msg (an unknown preset is shown as it is, never blank). */
export function formatNameMsg(preset: string): Msg | string {
  return FORMAT_OPTIONS.some((o) => o.value === preset) ? msg(`editor.deliver.renderSettings.formats.${preset}`) : preset;
}

/** {@link fitWarning} as a Msg. */
export function fitWarningMsg(timeline: { width: number; height: number } | null, preset: TimelineRenderPreset): Msg | null {
  if (fitWarning(timeline, preset) === null || !timeline) return null;
  return msg(`${H}.fitWarning`, { aspect: aspectLabel(timeline.width, timeline.height), format: formatNameMsg(preset) });
}

/** {@link renderStatusLine} as a Msg. */
export function renderStatusLineMsg(renderRevision: number | null, revision: number): Msg {
  const n = renderRevision === null ? 0 : Math.max(0, revision - renderRevision);
  return n === 0 ? msg(`${H}.statusLine.latest`) : msg(`${H}.statusLine.behind`, { count: n });
}

/** {@link formatEstimate} as a Msg, or null when there is nothing honest to say. */
export function formatEstimateMsg(estimate: RenderEstimate | null | undefined): Msg | null {
  if (!estimate || !estimate.billingEnabled || estimate.credits === null || estimate.credits === undefined) return null;
  const credits = msg(`${H}.estimate.credits`, { credits: Math.max(0, Math.round(estimate.credits * 10) / 10) });
  const seconds = estimate.estimateSeconds;
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return credits;
  return seconds < 60 ? msg(`${H}.estimate.underMinute`, { credits }) : msg(`${H}.estimate.withMinutes`, { credits, minutes: Math.round(seconds / 60) });
}

/** {@link missingSentence} as a Msg. */
export function missingMsg(missing: number | null): Msg | null {
  if (missing === null || missing <= 0) return null;
  return msg(`${H}.missing`, { missing: Math.round(missing * 10) / 10 });
}

/** {@link statusWord} as a Msg (an unknown status is shown as it is). */
export function statusWordMsg(status: string): Msg | string {
  return ['Queued', 'Running', 'Passed', 'Failed', 'Cancelled'].includes(status) ? msg(`${H}.statusWord.${status}`) : status;
}

/** {@link saveFailureSentence} as a Msg; a reason the sync client gave is its own sentence and stays a string. */
export function saveFailureMsg(state: SaveFailureSource): Msg | string {
  switch (state.status) {
    case 'conflict':
      return msg(`${H}.saveFailure.conflict`);
    case 'offline':
    case 'reconnecting':
      return msg(`${H}.saveFailure.offline`);
    case 'saving':
    case 'dirty':
    case 'retrying':
      return msg(`${H}.saveFailure.saving`);
    default: {
      const reason = state.reasons?.find((r) => r.trim() !== '') ?? state.message;
      return !reason || !reason.trim() ? msg(`${H}.saveFailure.generic`) : saveFailureSentence(state);
    }
  }
}
