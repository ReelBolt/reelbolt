/**
 * R08.3: the one fader. Every strip fader (mixer, buses panel) reads and writes the track's `gainDb`
 * through these helpers so the range, the scale marks, the typed entry and the reset agree.
 * The bottom of the scale is "−∞" (silence for every practical purpose: -60 dB).
 */

export const FADER_MIN_DB = -60;
export const FADER_MAX_DB = 12;
export const FADER_RESET_DB = 0;
export const FADER_STEP_DB = 0.5;

/** The scale marks drawn under every fader. */
export const FADER_MARKS: readonly { value: number; label: string }[] = [
  { value: FADER_MIN_DB, label: '−∞' },
  { value: -24, label: '−24' },
  { value: -12, label: '−12' },
  { value: 0, label: '0' },
  { value: FADER_MAX_DB, label: '+12' },
];

export function clampFaderDb(db: number): number {
  if (!Number.isFinite(db)) return db === Number.POSITIVE_INFINITY ? FADER_MAX_DB : FADER_MIN_DB;
  return Math.min(FADER_MAX_DB, Math.max(FADER_MIN_DB, db));
}

/** The readout for a fader value: "−∞" at the bottom, one decimal otherwise ("-3.5", "+2.0", "0.0"). */
export function formatFaderDb(db: number): string {
  if (!Number.isFinite(db) || db <= FADER_MIN_DB) return '−∞';
  const rounded = Math.round(db * 10) / 10;
  return `${rounded > 0 ? '+' : ''}${rounded.toFixed(1)}`;
}

/**
 * Parses what a person types into the dB field: "-6", "+3.5", "3 dB", "-inf", "−∞", "off". Returns
 * the clamped value, or null when the text is not a gain (the field then snaps back).
 */
export function parseFaderDb(text: string): number | null {
  const t = text.trim().toLowerCase().replace(/db$/, '').replace(/−/g, '-').trim();
  if (t === '') return null;
  if (t === '-inf' || t === '-∞' || t === '-infinity' || t === 'off' || t === 'silent') return FADER_MIN_DB;
  if (!/^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(t)) return null;
  const n = Number(t);
  return Number.isFinite(n) ? clampFaderDb(n) : null;
}

/** Pan label: C, L35, R20. */
export function formatPan(pan: number | undefined): string {
  if (pan === undefined || Math.abs(pan) < 0.005) return 'C';
  return pan < 0 ? `L${Math.round(-pan * 100)}` : `R${Math.round(pan * 100)}`;
}
