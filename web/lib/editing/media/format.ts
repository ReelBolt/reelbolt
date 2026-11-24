/**
 * Display formatting for media metadata.
 *
 * Every function here is TOTAL and returns a non-empty, human-readable string for any input,
 * including `null`, `undefined`, `NaN`, `Infinity` and negatives. That is the point: a pool tile
 * must never render "NaN", "undefined" or an empty cell, and the cheapest way to guarantee that is
 * to make the formatting layer incapable of producing one, rather than to guard at each call site.
 *
 * These are deliberately local to the media feature: `web/lib/editing/time.ts` owns timecode and the
 * shared `Rational` contract, this module owns only the short labels a pool tile shows.
 */

import type { MediaProbe } from './types';

/** Shown wherever a value is genuinely unknown. Never an empty string. */
export const NOT_ANALYSED = 'Not analysed';

/**
 * Shown for a field that is null INSIDE a probe that exists.
 *
 * Deliberately distinct from {@link NOT_ANALYSED}: "no artifact mentioned this source" and "an
 * artifact mentioned it but never probed this field" are different facts, and a panel that
 * collapsed them would tell the user something nobody measured. The landed API emits `null` — never
 * 0, never an empty string — for exactly this reason, and this label is the UI's half of that
 * bargain. **No row may ever render a fabricated `0`, `0 kbps`, "unknown codec" or a blank cell in
 * place of this.**
 */
export const NOT_PROBED = 'Not probed';

const isUsableNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);

/** Rounds to `digits` decimals and drops trailing zeros, so `12.50` reads `12.5`. */
function trimNumber(value: number, digits: number): string {
  return String(Number(value.toFixed(digits)));
}

const UNITS = ['B', 'kB', 'MB', 'GB', 'TB'] as const;

/** `48112004` → `48.1 MB`. Always a non-empty string. */
export function formatBytes(bytes: number | null | undefined): string {
  if (!isUsableNumber(bytes) || bytes <= 0) return '0 B';

  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return unit === 0 ? `${Math.round(value)} B` : `${trimNumber(value, 1)} ${UNITS[unit]}`;
}

/**
 * `12.5` → `12.5 s`; `62.5` → `1:02.5`; null/negative/non-finite → {@link NOT_ANALYSED}.
 *
 * A null duration means "never analysed", which is a real state and must be stated, not rendered as
 * a zero — a clip that claims to be 0 s long is a wrong fact, not a missing one.
 */
export function formatDuration(seconds: number | null | undefined): string {
  if (!isUsableNumber(seconds) || seconds < 0) return NOT_ANALYSED;
  if (seconds < 60) return `${trimNumber(seconds, 1)} s`;

  const wholeMinutes = Math.floor(seconds / 60);
  const rest = seconds - wholeMinutes * 60;
  const paddedSeconds = rest < 10 ? `0${trimNumber(rest, 1)}` : trimNumber(rest, 1);
  return `${wholeMinutes}:${paddedSeconds} s`;
}

/** `1920 × 1080`, or {@link NOT_ANALYSED} when either side is unknown. */
export function formatResolution(width: number | null, height: number | null): string {
  if (!isUsableNumber(width) || !isUsableNumber(height) || width <= 0 || height <= 0) {
    return NOT_ANALYSED;
  }
  return `${Math.round(width)} × ${Math.round(height)}`;
}

/**
 * The probe's frame rate as a reduced rational AND its decimal — `30/1.001 (29.97 fps)`.
 *
 * The decimal is presentation only; nothing downstream computes from it. A non-positive denominator
 * is treated as unknown rather than dividing by it.
 */
export function formatFrameRate(fpsNum: number | null, fpsDen: number | null): string {
  if (!isUsableNumber(fpsNum) || !isUsableNumber(fpsDen) || fpsDen <= 0 || fpsNum <= 0) {
    return NOT_ANALYSED;
  }

  const divisor = greatestCommonDivisor(fpsNum, fpsDen);
  const num = fpsNum / divisor;
  const den = fpsDen / divisor;
  const decimal = trimNumber(fpsNum / fpsDen, 2);

  return den === 1 ? `${trimNumber(num, 0)} fps (${decimal} fps)` : `${trimNumber(num, 0)}/${trimNumber(den, 3)} (${decimal} fps)`;
}

/**
 * A one-line probe summary for a pool tile: resolution, frame rate and duration, in that order.
 * Returns {@link NOT_ANALYSED} when there is no probe at all.
 */
export function formatProbeSummary(probe: MediaProbe | null | undefined): string {
  if (!probe || typeof probe !== 'object') return NOT_ANALYSED;

  const parts = [
    formatResolution(probe.width, probe.height),
    formatFrameRate(probe.fpsNum, probe.fpsDen),
    formatDuration(probe.durationSec),
  ].filter((part) => part !== NOT_ANALYSED);

  return parts.length > 0 ? parts.join(' · ') : NOT_ANALYSED;
}

/** `N items` — phrased once, here, so every count in the workspace reads the same. */
export function formatItemCount(count: number | null | undefined): string {
  const value = isUsableNumber(count) && count > 0 ? Math.floor(count) : 0;
  return `${value} ${value === 1 ? 'item' : 'items'}`;
}

// ─────────────────────────────────────────────────────────────────────────────
// Probe fields the API used to have no field for at all
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A codec name, or {@link NOT_PROBED}.
 *
 * ffprobe returns a bare identifier (`h264`, `aac`, `prores`), which is already readable; this
 * function's whole job is to refuse to invent one. A blank or whitespace-only string is treated as
 * absent rather than rendered, because an empty cell is not an unknown — it is a broken row.
 */
export function formatCodec(codec: string | null | undefined): string {
  if (typeof codec !== 'string') return NOT_PROBED;
  const trimmed = codec.trim();
  return trimmed.length > 0 ? trimmed : NOT_PROBED;
}

/** The video and audio codecs of one probe, as one line, or {@link NOT_PROBED} when both are absent. */
export function formatCodecs(
  videoCodec: string | null | undefined,
  audioCodec: string | null | undefined,
): string {
  const parts: string[] = [];
  if (formatCodec(videoCodec) !== NOT_PROBED) parts.push(`${formatCodec(videoCodec)} video`);
  if (formatCodec(audioCodec) !== NOT_PROBED) parts.push(`${formatCodec(audioCodec)} audio`);
  return parts.length > 0 ? parts.join(' · ') : NOT_PROBED;
}

/**
 * Display labels for the single-token `format_name`s the writer actually sees. Presentation only —
 * the wire value is never rewritten.
 */
const SINGLE_CONTAINER_LABELS: Record<string, string> = {
  matroska: 'Matroska',
  webm: 'WebM',
  wav: 'WAV',
  mp3: 'MP3',
  flac: 'FLAC',
  ogg: 'Ogg',
  aiff: 'AIFF',
  image2: 'Still image',
  gif: 'GIF',
  png_pipe: 'PNG',
  mjpeg: 'Motion JPEG',
};

/**
 * Probe lists ffprobe emits as ONE `format_name`, with the readable family they denote. A sniffed
 * MP4 is the common case and is exactly why this table exists rather than a naive split.
 */
const MULTI_CONTAINER_FAMILIES: { tokens: readonly string[]; label: string }[] = [
  { tokens: ['mov', 'mp4', 'm4a', '3gp', '3g2', 'mj2'], label: 'MP4' },
  { tokens: ['matroska', 'webm'], label: 'Matroska / WebM' },
  { tokens: ['ogg'], label: 'Ogg' },
];

/**
 * A container, made readable **without pretending it is one clean name**.
 *
 * `container` is ffprobe's `format_name` verbatim, and for a sniffed MP4 that is the comma-separated
 * probe list `mov,mp4,m4a,3gp,3g2,mj2` — six demuxers that all claimed the header. Rendering that
 * as "mp4" would state a canonical container the file may not have, and rendering it raw would be
 * unreadable, so:
 *
 *   - one token → its display label, falling back to the token itself;
 *   - several tokens matching a known family → `MP4 (mov, mp4, m4a, 3gp, 3g2, mj2)`, so the family
 *     is legible AND the raw list is still visible;
 *   - several tokens matching nothing → the raw list plus `(probe list)`, which claims no family.
 *
 * A null, blank or token-less value is {@link NOT_PROBED}.
 */
export function formatContainer(formatName: string | null | undefined): string {
  if (typeof formatName !== 'string') return NOT_PROBED;
  const trimmed = formatName.trim();
  if (trimmed.length === 0) return NOT_PROBED;

  const tokens = trimmed
    .split(',')
    .map((token) => token.trim())
    .filter((token) => token.length > 0);
  if (tokens.length === 0) return NOT_PROBED;

  if (tokens.length === 1) {
    return SINGLE_CONTAINER_LABELS[tokens[0]] ?? tokens[0];
  }

  const family = MULTI_CONTAINER_FAMILIES.find((candidate) =>
    tokens.every((token) => candidate.tokens.includes(token)),
  );
  const listed = tokens.join(', ');
  return family ? `${family.label} (${listed})` : `${listed} (probe list)`;
}

/** `2` → `2 channels`; a null, negative or non-finite value → {@link NOT_PROBED}, never `0 channels`. */
export function formatChannels(channels: number | null | undefined): string {
  if (!isUsableNumber(channels) || channels <= 0) return NOT_PROBED;
  const value = Math.floor(channels);
  return `${value} ${value === 1 ? 'channel' : 'channels'}`;
}

/** `48000` → `48 kHz`; a null, zero or negative rate → {@link NOT_PROBED}, never `0 Hz`. */
export function formatSampleRate(hz: number | null | undefined): string {
  if (!isUsableNumber(hz) || hz <= 0) return NOT_PROBED;
  return hz % 1000 === 0 ? `${hz / 1000} kHz` : `${trimNumber(hz / 1000, 1)} kHz`;
}

/** `48112004` → `48.1 Mbps`; a null, zero or negative rate → {@link NOT_PROBED}, never `0 bps`. */
export function formatBitrate(bitsPerSecond: number | null | undefined): string {
  if (!isUsableNumber(bitsPerSecond) || bitsPerSecond <= 0) return NOT_PROBED;

  if (bitsPerSecond >= 1_000_000) return `${trimNumber(bitsPerSecond / 1_000_000, 2)} Mbps`;
  if (bitsPerSecond >= 1_000) return `${trimNumber(bitsPerSecond / 1_000, 1)} kbps`;
  return `${Math.round(bitsPerSecond)} bps`;
}

/**
 * The bitrate as `bits per second` AND its human form — `48112004 bps (48.11 Mbps)`.
 *
 * Both are shown because the two answer different questions: the raw figure is what the probe
 * recorded (and what a user comparing against ffprobe will recognise), the human form is what a
 * person reads. Neither is derived from the other's rounding.
 */
export function formatBitrateVerbose(bitsPerSecond: number | null | undefined): string {
  if (!isUsableNumber(bitsPerSecond) || bitsPerSecond <= 0) return NOT_PROBED;
  return `${Math.round(bitsPerSecond)} bps (${formatBitrate(bitsPerSecond)})`;
}

function greatestCommonDivisor(a: number, b: number): number {
  let x = Math.abs(Math.round(a));
  let y = Math.abs(Math.round(b));
  while (y > 0) {
    const next = x % y;
    x = y;
    y = next;
  }
  return x || 1;
}
