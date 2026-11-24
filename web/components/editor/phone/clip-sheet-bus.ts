import type { ClipSheetSection } from './ClipSheet';

/**
 * R07.7: "open the clip sheet" from anywhere on the phone (the strip's Clip chip, the action bar). The sheet itself
 * lives in the action bar; this keeps the strip from needing a prop path through the editor workspace.
 */
const listeners = new Set<(section: ClipSheetSection) => void>();

export function requestClipSheet(section: ClipSheetSection = 'picture'): void {
  for (const l of [...listeners]) l(section);
}

export function onClipSheetRequest(listener: (section: ClipSheetSection) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
