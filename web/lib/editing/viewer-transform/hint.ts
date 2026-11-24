/**
 * T-013: the one-line "how to use the handles" hint shows once per browser session. The flag lives in
 * sessionStorage (and in memory when storage is blocked), never in the document.
 */

const KEY = 'reelbolt.editor.transformHintSeen';
let seenInMemory = false;

export function transformHintSeen(): boolean {
  if (seenInMemory) return true;
  try {
    return window.sessionStorage.getItem(KEY) === '1';
  } catch {
    return false;
  }
}

export function markTransformHintSeen(): void {
  seenInMemory = true;
  try {
    window.sessionStorage.setItem(KEY, '1');
  } catch {
    /* storage blocked: the in-memory flag still holds for this page load */
  }
}

/** Test seam: forget that the hint was shown. */
export function resetTransformHint(): void {
  seenInMemory = false;
  try {
    window.sessionStorage.removeItem(KEY);
  } catch {
    /* nothing to clear */
  }
}
