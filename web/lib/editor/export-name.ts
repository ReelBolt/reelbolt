/**
 * The file name an export is saved under (in the project's files, or the browser's downloads). The
 * browser, not the server, picks it (`fileName` in `POST …/exports`), so it is made safe here: no
 * path separators or control characters, the extension of the chosen format, and a sensible default
 * when the box is empty. Any picture extension the user typed is replaced, so switching from PNG to
 * PSD never leaves a `cover.png.psd`.
 */

export const DEFAULT_EXPORT_BASE = 'thumbnail';
export const DEFAULT_EXPORT_NAME = `${DEFAULT_EXPORT_BASE}.png`;
const MAX_BASE_LENGTH = 100;

/** Every extension the editor can export, which a typed name may already carry. */
const KNOWN_EXTENSIONS = /\.(png|jpe?g|webp|avif|psd|ora)$/i;

/** The name without its picture extension, cleaned; empty when nothing usable is left. */
export function exportBaseName(raw: string): string {
  const cleaned = raw.replace(/[\\/:*?"<>|\u0000-\u001f]/g, ' ').replace(/\s+/g, ' ').trim();
  return cleaned.replace(KNOWN_EXTENSIONS, '').replace(/^[.\s]+/, '').trim().slice(0, MAX_BASE_LENGTH).trim();
}

/** `extension` includes the dot, e.g. `.psd`. */
export function exportFileName(raw: string, extension = '.png'): string {
  const base = exportBaseName(raw);
  return `${base.length === 0 ? DEFAULT_EXPORT_BASE : base}${extension}`;
}
