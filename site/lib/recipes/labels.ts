// Buyer-facing names for the machine keys the API sends (step types, categories, prompt fields) and the
// known shapes of a step's `limits` strings. Pure: components map a key to a catalogue entry and fall back to a
// neutral label, never the raw key.

/** "VideoAnalyze", "Video analyze" and "video_analyze" are the same key. */
export function normalizeKey(key: string): string {
  return key.toLowerCase().replace(/[^a-z0-9]/g, '');
}

export type LimitLabel =
  | { key: 'reviewRounds'; values: { count: number } }
  | { key: 'passMark'; values: { score: string } }
  | { key: 'spendCap'; values: { credits: string } };

const LIMIT_SHAPES: { pattern: RegExp; build: (m: RegExpExecArray) => LimitLabel }[] = [
  { pattern: /^Review loop, up to (\d{1,3}) rounds$/, build: (m) => ({ key: 'reviewRounds', values: { count: Number(m[1]) } }) },
  { pattern: /^Pass mark (\d{1,2}(?:\.\d{1,2})?) out of 10$/, build: (m) => ({ key: 'passMark', values: { score: m[1] } }) },
  { pattern: /^Spend limited to (\d{1,9}(?:\.\d{1,2})?) credits$/, build: (m) => ({ key: 'spendCap', values: { credits: m[1] } }) },
];

/** A known limit string as a translatable label, or null (the caller hides what it does not know). */
export function parseLimit(limit: string): LimitLabel | null {
  for (const { pattern, build } of LIMIT_SHAPES) {
    const m = pattern.exec(limit.trim());
    if (m) return build(m);
  }
  return null;
}
