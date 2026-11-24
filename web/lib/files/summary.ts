export interface FileSummary {
  /** The summary as prose. */
  text: string;
  /** Extra labelled paragraphs from a structured summary (e.g. "Video generation relevance"). */
  extras: Array<{ label: string; text: string }>;
}

function humanLabel(key: string): string {
  const words = key.replace(/[_-]+/g, ' ').trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/**
 * An agent summary is sometimes stored as a JSON object (`{"summary":"...","video_generation_relevance":"..."}`).
 * Returns prose for it; a plain-text summary is returned unchanged.
 */
export function parseFileSummary(raw: string): FileSummary {
  const trimmed = raw.trim();
  if (trimmed.startsWith('{') && trimmed.endsWith('}')) {
    try {
      const obj = JSON.parse(trimmed) as unknown;
      if (obj && typeof obj === 'object' && !Array.isArray(obj)) {
        const rec = obj as Record<string, unknown>;
        const main = typeof rec.summary === 'string' ? rec.summary.trim() : '';
        const extras = Object.entries(rec)
          .filter(([k, v]) => k !== 'summary' && typeof v === 'string' && v.trim() !== '')
          .map(([k, v]) => ({ label: humanLabel(k), text: (v as string).trim() }));
        if (main || extras.length > 0) return { text: main, extras };
      }
    } catch {
      // Not JSON after all: show it as written.
    }
  }
  return { text: raw, extras: [] };
}
