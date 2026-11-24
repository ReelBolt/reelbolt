/**
 * Step config blobs are stored as the backend serialized them. Configs written by workflow
 * templates, the assistant or direct API calls arrive with PascalCase member names
 * (`{"Source": {"Kind": ...}, "Vision": "Optional"}`), while the editors read camelCase. Left
 * as-is every field silently renders as its default, and a save writes both spellings side by
 * side. This normalizes at the load boundary; the server binds case-insensitively, so camelCase
 * round-trips.
 *
 * Properties named in `DICTIONARY_KEYS` hold author-chosen keys (e.g. Extract's named `inputs`),
 * which must keep their exact spelling because other steps refer to them by name.
 */
const DICTIONARY_KEYS = new Set(['inputs', 'Inputs']);

const lowerFirst = (k: string) => (k.length > 0 ? k[0].toLowerCase() + k.slice(1) : k);

export function camelizeKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(camelizeKeys);
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      const key = lowerFirst(k);
      if (DICTIONARY_KEYS.has(k) && v && typeof v === 'object' && !Array.isArray(v)) {
        out[key] = Object.fromEntries(
          Object.entries(v as Record<string, unknown>).map(([dk, dv]) => [dk, camelizeKeys(dv)]),
        );
      } else {
        out[key] = camelizeKeys(v);
      }
    }
    return out;
  }
  return value;
}

/** Parses a config JSON string and camelizes its keys. Returns null for empty/invalid input. */
export function parseConfigJson<T = Record<string, unknown>>(raw: string | null | undefined): T | null {
  if (!raw) return null;
  try {
    return camelizeKeys(JSON.parse(raw)) as T;
  } catch {
    return null;
  }
}

/** Same, but returns a JSON string (for the room editors that carry their config as a raw string). */
export function camelizeConfigJsonString(raw: string | null | undefined): string | null {
  if (!raw) return null;
  const parsed = parseConfigJson(raw);
  return parsed === null ? raw : JSON.stringify(parsed);
}
