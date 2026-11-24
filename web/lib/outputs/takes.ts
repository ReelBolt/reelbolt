import type { OutputVideo } from '../api/outputs';

/** One video and its takes: the latest render, and every earlier one (newest first). */
export interface TakeGroup {
  key: string;
  latest: OutputVideo;
  earlier: OutputVideo[];
}

/**
 * The key a render's takes share. The server sends it (`takeGroupKey`: workflow + step place + type);
 * an older server that does not falls back to workflow + step id, so takes still group.
 */
export function takeKey(o: OutputVideo): string {
  return o.takeGroupKey ?? `${o.workflowDefinitionId ?? o.workflowExecutionId}:${o.workflowStepId}`;
}

/** A render whose run is still going: never a video's latest take (it may still be replaced). */
export function isInProgress(o: OutputVideo): boolean {
  return o.executionStatus === 'Running' || o.executionStatus === 'Queued';
}

/**
 * Groups renders into videos, latest take first within each and videos ordered by their latest
 * take, newest first. The latest take is the newest render of a run that has ended (a run still in
 * progress only counts when nothing else exists); everything else is an earlier take, newest first.
 */
export function groupTakes(outputs: OutputVideo[]): TakeGroup[] {
  const byKey = new Map<string, OutputVideo[]>();
  for (const o of outputs) {
    const k = takeKey(o);
    const list = byKey.get(k);
    if (list) list.push(o);
    else byKey.set(k, [o]);
  }
  const newestFirst = (a: OutputVideo, b: OutputVideo) => Date.parse(b.producedAt) - Date.parse(a.producedAt);
  return [...byKey.entries()]
    .map(([key, list]) => {
      const sorted = [...list].sort(newestFirst);
      const latest = sorted.find((o) => !isInProgress(o)) ?? sorted[0];
      return { key, latest, earlier: sorted.filter((o) => o !== latest) };
    })
    .sort((a, b) => newestFirst(a.latest, b.latest));
}

/** Storage keys of every render that is NOT the latest take of its video — what Files hides by default. */
export function earlierTakeStorageKeys(outputs: OutputVideo[]): Set<string> {
  const keys = new Set<string>();
  for (const g of groupTakes(outputs)) for (const o of g.earlier) keys.add(o.storageKey);
  // A key the latest take still plays from (a reused render) is never hidden.
  for (const g of groupTakes(outputs)) keys.delete(g.latest.storageKey);
  return keys;
}
