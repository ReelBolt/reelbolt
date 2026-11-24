/**
 * Helpers for showing a font sample next to a diff value. A diff entry carries only a dot path and
 * raw before/after values (no token `$type`), so a font-family entry is recognised by its path
 * (`typography.family.<role>`, `typography.<role>.fontFamily`) and its value may be a string or a
 * `$value` array of family names.
 */

/** True when the path names a font-family token: a `family` or `fontFamily` segment. */
export function isFontFamilyPath(path: string): boolean {
  return /(^|\.)(family|fontFamily)(\.|$)/i.test(path);
}

/** A font stack as text ("Inter, sans-serif") from a string or an array of family names; else null. */
export function fontStackText(value: unknown): string | null {
  if (typeof value === 'string') return value.trim() || null;
  if (Array.isArray(value)) {
    const names = value.filter((entry): entry is string => typeof entry === 'string' && entry.trim().length > 0);
    return names.length > 0 ? names.join(', ') : null;
  }
  return null;
}
