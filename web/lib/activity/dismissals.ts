/** Dismissed finished items, per workspace, in this browser. Capped so storage never grows. */
export const DISMISSED_CAP = 200;

export const dismissedKey = (orgId: string): string => `reelbolt.activity.dismissed.${orgId}`;
export const seenKey = (orgId: string): string => `reelbolt.activity.seen.${orgId}`;

export function loadDismissed(orgId: string | null): string[] {
  if (!orgId) return [];
  try {
    const raw = window.localStorage.getItem(dismissedKey(orgId));
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string').slice(-DISMISSED_CAP) : [];
  } catch {
    return [];
  }
}

export function saveDismissed(orgId: string | null, ids: string[]): void {
  if (!orgId) return;
  try {
    window.localStorage.setItem(dismissedKey(orgId), JSON.stringify(ids.slice(-DISMISSED_CAP)));
  } catch {
    // Storage blocked: dismissals last for the page only.
  }
}

export function loadSeenAt(orgId: string | null): number {
  if (!orgId) return 0;
  try {
    return Number(window.localStorage.getItem(seenKey(orgId))) || 0;
  } catch {
    return 0;
  }
}

export function saveSeenAt(orgId: string | null, at: number): void {
  if (!orgId) return;
  try {
    window.localStorage.setItem(seenKey(orgId), String(at));
  } catch {
    // Best effort.
  }
}
