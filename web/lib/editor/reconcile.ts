/**
 * After a paid run fails or loses its connection the server may still have finished it (a layer
 * that landed late and was billed). The editor reads the document again and brings in whatever is
 * new, so nothing billed stays invisible and nothing is run twice.
 */

import { ApiError } from '@/lib/api/client';

/** A 424 (the AI service failed or timed out), a gateway answer, or no answer at all. */
export function isReconcilable(cause: unknown): boolean {
  if (cause instanceof ApiError) return cause.status === 424 || cause.status === 502 || cause.status === 503 || cause.status === 504;
  return cause instanceof TypeError; // fetch failed: the connection dropped
}

/** The layer ids in `current` that were not in `known`. */
export function newLayerIds(known: readonly string[], current: readonly { id: string }[]): string[] {
  const had = new Set(known);
  return current.filter((l) => !had.has(l.id)).map((l) => l.id);
}

export function reconciledText(title: string, found: number): string {
  return found > 0
    ? `${title} took longer than expected, but it finished. The result is on your picture, and you were charged once.`
    : `${title} did not finish, so nothing was added. Check your credits, then try again.`;
}
