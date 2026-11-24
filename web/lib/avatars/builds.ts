/**
 * Pure helpers of avatar builds (docs/design/avatar-objects.md "Avatar variants", "Pulling an object out of a
 * video"): background work that ends in a new avatar. No user-visible text lives here: every function answers a
 * value or a key suffix the components look up in `avatars.json` (`avatars.builds.*`).
 */
import type { AvatarBuild, AvatarBuildListResponse } from '../types/avatar-builds';

const FINISHED = new Set(['ready', 'failed', 'cancelled']);

/** The builds of a list answer, whether the server sent a bare array or an envelope; anything else is none. */
export function buildsOf(response: AvatarBuildListResponse | null | undefined): AvatarBuild[] {
  const list = Array.isArray(response) ? response : (response?.items ?? response?.builds);
  return Array.isArray(list) ? list.filter((b): b is AvatarBuild => typeof b?.jobId === 'string' && b.jobId.length > 0) : [];
}

const word = (value: unknown): string => (typeof value === 'string' ? value.trim().toLowerCase() : '');

/** True while the build is still working: any state that is not `ready`, `failed` or `cancelled`. */
export function isBuildRunning(build: Pick<AvatarBuild, 'state'>): boolean {
  return !FINISHED.has(word(build.state));
}

export function anyBuildRunning(builds: readonly Pick<AvatarBuild, 'state'>[]): boolean {
  return builds.some(isBuildRunning);
}

/**
 * Where a build is, as a key suffix under `avatars.builds.stage`. A finished build reads its state; a running one
 * reads the step the server names (`stage`), else its state, else `working` for a word this client does not know.
 */
export type BuildStage =
  | 'queued' | 'pictures' | 'videos' | 'finding' | 'cutting' | 'creating' | 'working' | 'ready' | 'failed' | 'cancelled';

const RUNNING_STAGES: readonly BuildStage[] = ['queued', 'pictures', 'videos', 'finding', 'cutting', 'creating'];

export function buildStage(build: Pick<AvatarBuild, 'state' | 'stage'>): BuildStage {
  const state = word(build.state);
  if (FINISHED.has(state)) return state as BuildStage;
  const stage = word(build.stage);
  const known = RUNNING_STAGES.find((s) => s === stage) ?? RUNNING_STAGES.find((s) => s === state);
  return known ?? 'working';
}

/** `k of n` of the current step, when the server counts it (whole numbers, n at least 1, k inside 0..n). */
export function buildCount(build: Pick<AvatarBuild, 'progress'>): { done: number; total: number } | null {
  const total = Math.floor(Number(build.progress?.total));
  const done = Math.floor(Number(build.progress?.done));
  if (!Number.isFinite(total) || total < 1 || !Number.isFinite(done)) return null;
  return { done: Math.min(total, Math.max(0, done)), total };
}

/** The share of the current step that is done (0..1), or null when the server does not count it. */
export function buildFraction(build: Pick<AvatarBuild, 'progress'>): number | null {
  const count = buildCount(build);
  return count ? count.done / count.total : null;
}

/**
 * How long to wait before asking again, in milliseconds: 2 s, then half as long again each time something is
 * still running and nothing changed, up to 15 s.
 */
export function buildPollDelayMs(attempt: number): number {
  const n = Number.isFinite(attempt) && attempt > 0 ? Math.floor(attempt) : 0;
  return Math.min(15_000, Math.round(2_000 * 1.5 ** Math.min(n, 12)));
}

/** A short text that changes whenever any build moves, so a poll can start its waiting times over. */
export function buildsSignature(builds: readonly AvatarBuild[]): string {
  return builds.map((b) => `${b.jobId}:${word(b.state)}:${word(b.stage)}:${b.progress?.done ?? ''}/${b.progress?.total ?? ''}`).join('|');
}

/**
 * The builds the "Being made" list shows: every running one; a failed one until the person dismisses it; a ready
 * or cancelled one only when this page saw it running (so an old finished build never reappears).
 */
export function visibleBuilds(builds: readonly AvatarBuild[], seenRunning: ReadonlySet<string>, dismissed: ReadonlySet<string>): AvatarBuild[] {
  return builds.filter((b) => {
    if (isBuildRunning(b)) return true;
    if (dismissed.has(b.jobId)) return false;
    return word(b.state) === 'failed' || seenRunning.has(b.jobId);
  });
}

/** The builds that just turned `ready` between two readings of the list (they were running before). */
export function newlyReady(before: ReadonlyMap<string, string>, builds: readonly AvatarBuild[]): AvatarBuild[] {
  return builds.filter((b) => {
    const was = before.get(b.jobId);
    return word(b.state) === 'ready' && was !== undefined && !FINISHED.has(was);
  });
}

/** The state of each build by id, for `newlyReady`. */
export function buildStates(builds: readonly AvatarBuild[]): Map<string, string> {
  return new Map(builds.map((b) => [b.jobId, word(b.state)]));
}

/** True when the list call answered "not found": an older server, or avatars switched off. */
export function isBuildsOff(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { status?: unknown }).status === 404;
}
