/**
 * The first finished video (docs/design/new-user-funnel.md section 3, step 6): who sees the
 * "your first video is ready" moment, what the run itself says about before and after, and where
 * "seen" is remembered. Pure except for the two storage helpers at the end; nothing here throws.
 *
 * Every figure comes from the run's own step outputs. A field that is missing, not a number or not
 * believable is left out: nothing is worked out, rounded up or guessed.
 */
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { OnboardingMirror, OnboardingServerState } from '@/lib/onboarding/state';
import type { WorkflowExecution, WorkflowStepResult } from '@/lib/types/workflow';

// --------------------------------------------------------------------------- the facts of one run

export interface RunFacts {
  /** The total length of the footage the run was given, in seconds. */
  sourceSec: number | null;
  /** How many clips that footage was. Only set together with `sourceSec`. */
  sourceClips: number | null;
  /** The length of the finished video, in seconds. */
  outputSec: number | null;
  /** How many pieces of the footage the finished cut keeps. Only set together with `sourceSec`. */
  parts: number | null;
}

export const NO_FACTS: RunFacts = { sourceSec: null, sourceClips: null, outputSec: null, parts: null };

type FactSource = Pick<WorkflowStepResult, 'stepType' | 'status'> &
  Partial<Pick<WorkflowStepResult, 'outputJson' | 'output' | 'stepOrder' | 'iterationNumber' | 'completedAt' | 'executedAt'>>;

/** Longest length taken at face value: a day. Anything above is a broken probe, not footage. */
const MAX_SECONDS = 24 * 60 * 60;
const MAX_PARTS = 10_000;
/** Two lengths this close are the same length (container rounding). */
const SAME_LENGTH_SEC = 0.5;

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

function seconds(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 && value <= MAX_SECONDS ? value : null;
}

function parsed(result: FactSource): Record<string, unknown> | null {
  const raw = result.outputJson || result.output;
  if (!raw) return null;
  try {
    return record(JSON.parse(raw));
  } catch {
    return null;
  }
}

function finished(result: FactSource): boolean {
  return result.status === 'Completed';
}

function time(result: FactSource): number {
  const at = Date.parse(result.completedAt ?? result.executedAt ?? '');
  return Number.isFinite(at) ? at : 0;
}

/**
 * The footage the run analysed, from the analysis step's own `view`: one clip reads `view.media`,
 * several read `view.sources` (one entry per clip). Null when the step analysed anything that is
 * not the person's footage (a generated take, a rendered composition), or when a length is missing.
 */
function footageOf(view: Record<string, unknown>): { sec: number; clips: number } | null {
  const media = record(view['media']);
  if (!media || media['composition'] === true) return null;
  const sources = view['sources'];
  if (Array.isArray(sources) && sources.length > 0) {
    let total = 0;
    for (const entry of sources) {
      const source = record(entry);
      const sec = source ? seconds(source['durationSec']) : null;
      if (!source || sec === null || source['composition'] === true || typeof source['clipId'] === 'string') return null;
      total += sec;
    }
    return total <= MAX_SECONDS ? { sec: total, clips: sources.length } : null;
  }
  const sec = seconds(media['durationSec']);
  return sec === null ? null : { sec, clips: 1 };
}

/**
 * What a finished run's own step outputs say about before and after.
 *
 * - Before: the first footage analysis of the run (`VideoAnalyze`), its clips and their total length.
 * - After: the last compile that really encoded (`VideoCompile`, `status: "completed"`, not a
 *   plan-only pass): `outputDurationSec` and `segments` (the pieces of footage the cut keeps).
 *
 * `fallbackOutputSec` is the finished file's own probed length (the outputs list), used only when no
 * compile reported one. The footage is dropped when the finished video is longer than it: the run
 * added material of its own, so the two lengths do not compare.
 */
export function runFacts(results: readonly FactSource[] | null | undefined, fallbackOutputSec?: number | null): RunFacts {
  const done = (results ?? []).filter(finished);

  const analyses = done
    .filter((r) => r.stepType === 'VideoAnalyze')
    .map((r, i) => ({ r, i }))
    .sort((a, b) => (a.r.stepOrder ?? 0) - (b.r.stepOrder ?? 0) || time(a.r) - time(b.r) || a.i - b.i);
  let footage: { sec: number; clips: number } | null = null;
  for (const { r } of analyses) {
    const view = record(parsed(r)?.['view']);
    if (!view) continue;
    footage = footageOf(view);
    // The first analysis that produced a view is the one that read the person's material.
    break;
  }

  const compiles = done
    .filter((r) => r.stepType === 'VideoCompile')
    .map((r, i) => ({ r, i }))
    .sort((a, b) => (b.r.stepOrder ?? 0) - (a.r.stepOrder ?? 0) || (b.r.iterationNumber ?? 0) - (a.r.iterationNumber ?? 0) || time(b.r) - time(a.r) || b.i - a.i);
  let outputSec: number | null = null;
  let parts: number | null = null;
  for (const { r } of compiles) {
    const out = parsed(r);
    if (!out || out['status'] !== 'completed' || out['planOnly'] === true || out['encoded'] === false) continue;
    outputSec = seconds(out['outputDurationSec']);
    const segments = out['segments'];
    parts = typeof segments === 'number' && Number.isInteger(segments) && segments >= 1 && segments <= MAX_PARTS ? segments : null;
    break;
  }
  outputSec ??= seconds(fallbackOutputSec);

  // Footage shorter than the finished video is not a before to that after.
  if (footage && outputSec !== null && outputSec > footage.sec + SAME_LENGTH_SEC) footage = null;

  return {
    sourceSec: footage?.sec ?? null,
    sourceClips: footage?.clips ?? null,
    outputSec,
    // "Pieces of your footage" only means something next to the footage itself.
    parts: footage ? parts : null,
  };
}

export function hasFacts(facts: RunFacts): boolean {
  return facts.sourceSec !== null || facts.outputSec !== null;
}

// --------------------------------------------------------------------------- whose first result it is

export interface FirstResultInput {
  /** The run on screen. */
  execution: Pick<WorkflowExecution, 'id' | 'status'> | null | undefined;
  /** Every run of this app the page knows. */
  executions: readonly Pick<WorkflowExecution, 'id' | 'status'>[] | null | undefined;
  /**
   * Whether the person had opened a finished result before this page was opened (see
   * {@link openedAResultBefore}); `null` when that is not known.
   */
  openedBefore: boolean | null;
  /**
   * The workspace's recent runs, its newest outputs and its output count; `undefined` while loading
   * or when it could not be read.
   */
  overview: Pick<DashboardOverview, 'recentExecutions' | 'recentOutputs' | 'totals'> | null | undefined;
  /**
   * How many workspaces the person belongs to (their Personal one included); `null` while that is
   * not known. The overview only sees the active workspace, so a second one is a place an earlier
   * video could be that nothing here can check.
   */
  workspaceCount: number | null;
}

/**
 * Had this person opened a finished result before? Yes when the API recorded it or this browser
 * remembers it. `null` when the API did not answer: without its word nobody is told "your first".
 *
 * The page asks this once, when it first knows the onboarding state, and keeps that answer: the
 * shell records "opened a result" while a finished run's page is open, and that must not turn the
 * very visit being recorded into "not the first".
 */
export function openedAResultBefore(server: OnboardingServerState | null, mirror: Pick<OnboardingMirror, 'done'>): boolean | null {
  if (!server) return null;
  return server.checklist.items.some((i) => i.key === 'open_result' && i.done) || (mirror.done ?? []).includes('open_result');
}

/**
 * Could the run on screen be the person's first finished video, as far as this app's own runs and
 * the person's onboarding state say? True only when it is known that they had never opened a
 * finished result, and this app has no other finished run. Cheap: needs no extra request.
 */
export function mayBeFirstResult({ execution, executions, openedBefore }: Pick<FirstResultInput, 'execution' | 'executions' | 'openedBefore'>): boolean {
  if (!execution || execution.status !== 'Passed') return false;
  if (openedBefore !== false) return false;
  return !(executions ?? []).some((e) => e.status === 'Passed' && e.id !== execution.id);
}

/**
 * Is the run on screen the person's first finished video? {@link mayBeFirstResult}, and nothing the
 * workspace holds says otherwise. Every doubt answers no: a moment missed is nothing, "your first
 * video" said to someone on their tenth is false.
 *
 * The account's "opened a result" mark is not enough by itself: before the app run page recorded
 * it, the mark was only set on the run-detail page, so an account can have finished videos and
 * still read "never opened one". The workspace is therefore asked directly:
 *
 * - its recent runs (which must have loaded) hold no other finished run;
 * - every output it has ever made is listed (`totals.renders` is not more than the newest outputs
 *   the overview carries) and none of them was produced before this run started. A finished video
 *   older than the recent-runs window, or made by another app, is caught here;
 * - the person belongs to this one workspace only. A video made in another workspace (Personal
 *   next to a team) is invisible from here, so a second workspace is a doubt.
 */
export function isFirstResult(input: FirstResultInput): boolean {
  if (!mayBeFirstResult(input) || !input.overview || !input.execution) return false;
  if (input.workspaceCount !== 1) return false;
  const { overview } = input;
  const id = input.execution.id;
  if (overview.recentExecutions.some((e) => e.status === 'Passed' && e.id !== id)) return false;
  const started = Date.parse(overview.recentExecutions.find((e) => e.id === id)?.startedAt ?? '');
  if (!Number.isFinite(started)) return false;
  if (overview.totals.renders > overview.recentOutputs.length) return false;
  return overview.recentOutputs.every((output) => Date.parse(output.producedAt) >= started);
}

/**
 * Is the failed run on screen the person's first run that ended, with no finished video before it?
 * Same caution as above, from what the page already holds: it must be known that the person never
 * opened a finished result, and this app must have no finished run.
 */
export function isFirstRunFailed({ execution, executions, openedBefore }: Pick<FirstResultInput, 'execution' | 'executions' | 'openedBefore'>): boolean {
  if (!execution || execution.status !== 'Failed') return false;
  if (openedBefore !== false) return false;
  return !(executions ?? []).some((e) => e.status === 'Passed');
}

// --------------------------------------------------------------------------- whose side a failure was on

export type FailureSide = 'ours' | 'yours' | 'unknown';

/** The failure classes the platform refunds (`FailureClass` on the engine; refunds follow it). */
const OUR_SIDE = new Set(['PlatformInternal', 'PlatformProviderError']);
/** The classes that keep what was used: the input, a stop, the plan, the balance, the person's own provider or machine. */
const YOUR_SIDE = new Set(['UserInput', 'UserCancelled', 'ByoProviderError', 'InsufficientCredits', 'EntitlementDenied', 'Runner']);

/**
 * Whose side the failed step was on, from the class the engine recorded on it. Anything this build
 * does not know is `unknown`, never assumed to be ours: a refund is only stated where the class says so.
 */
export function failureSide(execution: Pick<WorkflowExecution, 'stepResults'> | null | undefined): FailureSide {
  const failed = (execution?.stepResults ?? []).filter((r) => r.status === 'Failed');
  const classes = failed.map((r) => r.failureClass ?? '').filter((c) => c !== '' && c !== 'None');
  if (classes.length === 0) return 'unknown';
  if (classes.every((c) => OUR_SIDE.has(c))) return 'ours';
  if (classes.every((c) => YOUR_SIDE.has(c))) return 'yours';
  return 'unknown';
}

// --------------------------------------------------------------------------- what comes third

/** The recipe "Add subtitles" sets up: subtitles burned into a clip, plus SRT and VTT files. */
export const SUBTITLES_RECIPE_KEY = 'video-subtitles';

/**
 * The third next step, from what this app really has: a footage app works from clips the subtitles
 * recipe can read, and an app with a Shape choice can be run again in another shape. With neither,
 * the step is the project's other recipes.
 */
export type ThirdStep = 'subtitlesOrShape' | 'subtitles' | 'shape' | 'recipes';

export function thirdStepFor({ hasClips, hasShape }: { hasClips: boolean; hasShape: boolean }): ThirdStep {
  if (hasClips && hasShape) return 'subtitlesOrShape';
  if (hasClips) return 'subtitles';
  if (hasShape) return 'shape';
  return 'recipes';
}

// --------------------------------------------------------------------------- remembered per person

/** What this browser remembers about the moment: which run it was shown for, and whether it was closed. */
export interface FirstResultMark {
  id: string;
  dismissed: boolean;
}

const KEY_PREFIX = 'rb-first-result:v1:';

export function firstResultStorageKey(userKey: string): string {
  return `${KEY_PREFIX}${userKey}`;
}

export function readFirstResultMark(userKey: string): FirstResultMark | null {
  if (typeof window === 'undefined' || !userKey) return null;
  try {
    const raw = window.localStorage.getItem(firstResultStorageKey(userKey));
    if (!raw) return null;
    const value = record(JSON.parse(raw));
    const id = value?.['id'];
    return typeof id === 'string' && id ? { id, dismissed: value?.['dismissed'] === true } : null;
  } catch {
    return null;
  }
}

/** Remembers the mark; false when the browser's storage refused it (then nothing may rely on it). */
export function writeFirstResultMark(userKey: string, mark: FirstResultMark): boolean {
  if (typeof window === 'undefined' || !userKey) return false;
  try {
    window.localStorage.setItem(firstResultStorageKey(userKey), JSON.stringify(mark));
    return true;
  } catch {
    return false;
  }
}
