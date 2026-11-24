/**
 * One rule for how often a screen re-reads something that changes while a run is in flight:
 * not at all when nothing can change (a hidden tab is paused by SWR itself, which keeps its timer alive), quickly at first, then slower once the
 * status has sat unchanged for a while.
 */
export interface PollRule {
  /** Whether the thing being watched can still change (e.g. a run is Queued or Running). */
  active: boolean;
  /** Milliseconds since the status last changed. */
  unchangedForMs: number;
  fastMs?: number;
  slowMs?: number;
  backoffAfterMs?: number;
}

export function pollInterval({
  active,
  unchangedForMs,
  fastMs = 4000,
  slowMs = 8000,
  backoffAfterMs = 60_000,
}: PollRule): number {
  if (!active) return 0;
  return unchangedForMs >= backoffAfterMs ? slowMs : fastMs;
}
