/** A render's sound check, as the API sends it (camelCase; numbers may be null). `detail` is English and is never displayed. */
export type RenderCheckKind = 'loudness_off_target' | 'loudness_range_wide' | 'true_peak_high' | 'clipping' | 'long_silence' | 'no_sound';
export type RenderCheckSeverity = 'high' | 'medium' | 'low';

export interface RenderCheck {
  kind: RenderCheckKind;
  severity: RenderCheckSeverity;
  startSec?: number | null;
  endSec?: number | null;
  measured?: number | null;
  limit?: number | null;
  detail?: string | null;
}

export interface RenderCheckMessage {
  /** Key under `editor-audio.checks`. */
  key: string;
  values: Record<string, string>;
}

const one = (n: number): string => (Math.round(n * 10) / 10).toFixed(1);

/** The i18n key and numbers for one check; an unknown kind or missing numbers fall back to a calm generic sentence. */
export function renderCheckMessage(check: RenderCheck): RenderCheckMessage {
  const { measured, limit, startSec, endSec } = check;
  const both = typeof measured === 'number' && typeof limit === 'number';
  const values: Record<string, string> = both ? { measured: one(measured), limit: one(limit) } : {};
  switch (check.kind) {
    case 'loudness_off_target':
      return both ? { key: measured < limit ? 'loudnessQuiet' : 'loudnessLoud', values } : { key: 'loudnessGeneric', values };
    case 'loudness_range_wide':
      return { key: both ? 'rangeWide' : 'rangeWideGeneric', values };
    case 'true_peak_high':
      return { key: both ? 'peakHigh' : 'peakHighGeneric', values };
    case 'clipping':
      return { key: 'clipping', values: {} };
    case 'long_silence':
      return typeof startSec === 'number' && typeof endSec === 'number'
        ? { key: 'silence', values: { from: one(startSec), to: one(endSec) } }
        : { key: 'silenceGeneric', values: {} };
    case 'no_sound':
      return { key: 'noSound', values: {} };
    default:
      return { key: 'other', values: {} };
  }
}

const rank: Record<RenderCheckSeverity, number> = { high: 0, medium: 1, low: 2 };
export const sortChecks = (checks: readonly RenderCheck[]): RenderCheck[] =>
  [...checks].sort((a, b) => (rank[a.severity] ?? 3) - (rank[b.severity] ?? 3));
