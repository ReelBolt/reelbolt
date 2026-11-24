import { getUserFromCookie } from '@/lib/hooks/use-auth';
import { PICREVIEW_PREFIX, userScopeFromEmail } from '@/lib/editing/sync/user-scope';

/** At most this many layer ids are remembered per document. */
export const PICREVIEW_CAP = 50;

const keyFor = (documentId: string): string =>
  `${PICREVIEW_PREFIX}${userScopeFromEmail(getUserFromCookie()?.email)}.${documentId}`;

/**
 * The layers a picture operation left hidden because the layer it was made for was deleted, per user and
 * document, so the review bar offers them again after a reload. Sign-out wipes the prefix.
 */
export function loadPictureReview(documentId: string): string[] {
  try {
    if (typeof window === 'undefined') return [];
    const parsed: unknown = JSON.parse(window.localStorage.getItem(keyFor(documentId)) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string').slice(-PICREVIEW_CAP) : [];
  } catch {
    return [];
  }
}

export function savePictureReview(documentId: string, ids: readonly string[]): void {
  try {
    if (typeof window === 'undefined') return;
    const key = keyFor(documentId);
    if (ids.length === 0) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, JSON.stringify(ids.slice(-PICREVIEW_CAP)));
  } catch {
    // blocked storage: the bar is then offered for this session only
  }
}
