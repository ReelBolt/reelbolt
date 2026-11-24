import type { WorkflowExecution } from '../types/workflow';

/** Shortest-to-read duration for an estimate: "about 4 minutes", "about 1 hour 10 minutes". */
export function formatEstimate(ms: number): string {
  const minutes = Math.max(1, Math.round(ms / 60000));
  if (minutes < 60) return `about ${minutes} minute${minutes === 1 ? '' : 's'}`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  const h = `${hours} hour${hours === 1 ? '' : 's'}`;
  return rest >= 5 ? `about ${h} ${rest} minutes` : `about ${h}`;
}

/**
 * How long a run of this workflow will roughly take, from its own past finished runs (the median,
 * so one stuck run does not skew it). Without history, a long workflow still gets an honest
 * warning rather than nothing; a short one gets no number at all, since we would be guessing.
 */
export function estimateRunDuration(executions: WorkflowExecution[] | undefined, stepCount: number): string | null {
  const durations = (executions ?? [])
    .filter((e) => e.status === 'Passed' && e.startedAt && e.completedAt)
    .map((e) => new Date(e.completedAt!).getTime() - new Date(e.startedAt!).getTime())
    .filter((d) => Number.isFinite(d) && d > 0)
    .sort((a, b) => a - b);
  if (durations.length > 0) {
    const median = durations[Math.floor(durations.length / 2)];
    return `Usually takes ${formatEstimate(median)}, based on ${durations.length === 1 ? 'your last run' : `your last ${durations.length} runs`}.`;
  }
  if (stepCount >= 6) return 'Workflows with this many steps can take up to an hour. You can leave this page; the run keeps going.';
  return null;
}
