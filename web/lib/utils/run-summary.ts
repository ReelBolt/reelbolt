/**
 * Plain-language summaries of a workflow run for the execution page and the Renders tab: a human
 * run name instead of a hash, an honest time-left estimate, the run's customer warnings (contract
 * C1) and a friendly render title instead of a GUID file name.
 */
import type { T } from '@/i18n/translator';
import { enT, labelOf, labelSlug } from '@/lib/i18n-default';
import type { WorkflowExecution, WorkflowStep, WorkflowStepResult } from '../types/workflow';

/** Contract C1: `{ code, message }` entries a step result's OutputJson may carry under `warnings`. */
export interface RunWarning {
  code: string;
  message: string;
}

const WARNING_CODES = [
  'design_system', 'narration_dropped', 'narration_mostly_dropped', 'no_program_audio', 'music_no_tracks',
  'sfx_no_clips', 'target_length_missed', 'voice_provider_unhealthy', 'voice_provider_fallback',
] as const;

/** English headline per known warning code (catalogue: `libLabels.runSummary.warnings`). */
export const WARNING_TITLES: Record<string, string> = Object.fromEntries(
  WARNING_CODES.map((c) => [c, enT(`libLabels.runSummary.warnings.${labelSlug(c)}`)]),
);

/** Short headline per known warning code; unknown codes fall back to a generic heading. */
export function warningTitle(code: string, t: T = enT): string {
  return labelOf(t, 'runSummary.warnings', code, t('libLabels.runSummary.warningFallback'));
}

/** Reads `warnings` from one OutputJson string; tolerant of anything malformed. */
export function readWarnings(outputJson: string | null | undefined): RunWarning[] {
  if (!outputJson || !outputJson.includes('warnings')) return [];
  try {
    const parsed: unknown = JSON.parse(outputJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return [];
    const list = (parsed as { warnings?: unknown }).warnings;
    if (!Array.isArray(list)) return [];
    return list.flatMap((w) => {
      if (!w || typeof w !== 'object') return [];
      const { code, message } = w as { code?: unknown; message?: unknown };
      if (typeof message !== 'string' || !message.trim()) return [];
      return [{ code: typeof code === 'string' && code ? code : 'warning', message }];
    });
  } catch {
    return [];
  }
}

/** Every warning across a run's step results, deduplicated by code + message, in step order. */
export function collectRunWarnings(stepResults: Pick<WorkflowStepResult, 'outputJson'>[]): RunWarning[] {
  const seen = new Set<string>();
  const out: RunWarning[] = [];
  for (const r of stepResults) {
    for (const w of readWarnings(r.outputJson)) {
      const key = `${w.code}\u0000${w.message}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(w);
    }
  }
  return out;
}

/** "4 Oct, 14:02" — compact, unambiguous, local time. */
export function formatRunDate(iso: string | null | undefined, locale: string = 'en'): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  // `locale` is the active UI language (`useFormat().locale`); English keeps the en-GB day-month order.
  const intl = locale === 'en' || locale === 'en-XA' ? 'en-GB' : locale;
  const date = d.toLocaleDateString(intl, { day: 'numeric', month: 'short' });
  const time = d.toLocaleTimeString(intl, { hour: '2-digit', minute: '2-digit', hour12: false });
  return `${date}, ${time}`;
}

/**
 * 1-based ordinal of a run among its workflow's runs, by start time (unstarted runs last). Mirrors
 * the server's `RunNumber` on the render list so both screens agree.
 */
export function runOrdinal(executionId: string, executions: Pick<WorkflowExecution, 'id' | 'startedAt'>[] | null | undefined): number | null {
  if (!executions?.length) return null;
  const sorted = [...executions].sort((a, b) => {
    const ta = a.startedAt ? new Date(a.startedAt).getTime() : Number.POSITIVE_INFINITY;
    const tb = b.startedAt ? new Date(b.startedAt).getTime() : Number.POSITIVE_INFINITY;
    if (ta !== tb) return ta - tb;
    return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  });
  const i = sorted.findIndex((e) => e.id === executionId);
  return i >= 0 ? i + 1 : null;
}

/** "Run 3 · 4 Oct, 14:02", degrading to "Run 3", "Run · 4 Oct, 14:02" or just "Run". */
export function runName(runNumber: number | null | undefined, startedAt: string | null | undefined, t: T = enT, locale: string = 'en'): string {
  const date = formatRunDate(startedAt, locale);
  const head = runNumber ? t('libLabels.runSummary.runNamed', { number: runNumber }) : t('libLabels.runSummary.runUnnamed');
  return date ? t('libLabels.runSummary.runDated', { head, date }) : head;
}

/** Latest result per workflow step (results are in execution order, so the last one wins). */
function latestResultByStep(results: WorkflowStepResult[]): Map<string, WorkflowStepResult> {
  const map = new Map<string, WorkflowStepResult>();
  for (const r of results) map.set(r.workflowStepId, r);
  return map;
}

/**
 * Estimated time left for a running run: the average duration of the steps that really ran (cache
 * hits cost ~0 and would skew it) times the steps still to go, less what the current step has
 * already spent. Null when there is nothing to base it on, or the run is not running. It is an
 * estimate — steps differ wildly (rendering takes far longer than reading) — and is labelled so.
 */
export function estimateRemainingMs(
  steps: Pick<WorkflowStep, 'id'>[],
  execution: Pick<WorkflowExecution, 'status' | 'stepResults'>,
  now: number,
): number | null {
  if (execution.status !== 'Running') return null;
  const latest = latestResultByStep(execution.stepResults);
  const timed = [...latest.values()].filter((r) => r.status === 'Completed' && !r.fromCache && r.durationMs > 0);
  if (timed.length === 0) return null;
  const avg = timed.reduce((sum, r) => sum + r.durationMs, 0) / timed.length;
  const done = steps.filter((s) => latest.get(s.id)?.status === 'Completed' || latest.get(s.id)?.status === 'Skipped').length;
  const remaining = steps.length - done;
  if (remaining <= 0) return null;
  const running = [...latest.values()].find((r) => r.status === 'Running');
  const spent = running ? Math.max(0, now - new Date(running.executedAt).getTime()) : 0;
  // The running step is assumed to take at least a little longer, never "0 left".
  const currentLeft = running ? Math.max(avg - spent, avg * 0.1) : avg;
  return Math.round(currentLeft + Math.max(0, remaining - 1) * avg);
}

/** "About 12 min left", "About 1 h 5 min left", "Less than a minute left". */
export function formatTimeLeft(ms: number, t: T = enT): string {
  const minutes = Math.round(ms / 60000);
  if (minutes < 1) return t('libLabels.runSummary.timeLeftLess');
  if (minutes < 60) return t('libLabels.runSummary.timeLeftMinutes', { minutes });
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? t('libLabels.runSummary.timeLeftHoursMinutes', { hours, minutes: rest }) : t('libLabels.runSummary.timeLeftHours', { hours });
}

/** "1:05" / "12:30" / "1:02:03" for a video length in seconds. */
export function formatVideoLength(sec: number | null | undefined): string | null {
  if (sec == null || !Number.isFinite(sec) || sec <= 0) return null;
  const total = Math.round(sec);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number) => String(n).padStart(2, '0');
  return h ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

/** "Vertical 9:16 · 1080×1920" — the shape name customers use, plus the exact size. */
export function formatFrameSize(width: number | null | undefined, height: number | null | undefined, t: T = enT): string | null {
  if (!width || !height) return null;
  const ratio = width / height;
  const key =
    Math.abs(ratio - 9 / 16) < 0.02 ? 'shapeVertical'
      : Math.abs(ratio - 1) < 0.02 ? 'shapeSquare'
        : Math.abs(ratio - 16 / 9) < 0.02 ? 'shapeLandscape169'
          : Math.abs(ratio - 4 / 5) < 0.02 ? 'shapePortrait45'
            : ratio < 1 ? 'shapePortrait' : 'shapeLandscape';
  return t('libLabels.runSummary.frameSize', { shape: t(`libLabels.runSummary.${key}`), width, height });
}

/** Final / draft / in-progress classification of one render, from the render list's context. */
export type RenderStage = 'final' | 'draft' | 'inProgress';

export interface RenderContext {
  fileName: string;
  workflowName?: string | null;
  runNumber?: number | null;
  runStartedAt?: string | null;
  executionStatus?: string | null;
  isFinal?: boolean;
}

export function renderStage(o: RenderContext): RenderStage | null {
  if (o.isFinal) return 'final';
  if (o.executionStatus === 'Running' || o.executionStatus === 'Queued') return 'inProgress';
  // Older servers send no run context at all: say nothing rather than guess "Draft".
  return o.executionStatus ? 'draft' : null;
}

/**
 * English stage labels; components should use `renderStageLabel(t, stage)`.
 * @deprecated Use `renderStageLabel(t, stage)` (translator-taking) so the text follows the active language.
 */
export const RENDER_STAGE_LABEL: Record<RenderStage, string> = {
  final: enT('libLabels.runSummary.stage.final'),
  draft: enT('libLabels.runSummary.stage.draft'),
  inProgress: enT('libLabels.runSummary.stage.inProgress'),
};

export function renderStageLabel(t: T, stage: RenderStage): string {
  return t(`libLabels.runSummary.stage.${stage}`);
}

/** "60 s product story — Run 2" instead of "acf96fd3-….mp4"; the file name only as a last resort. */
export function renderTitle(o: RenderContext, t: T = enT): string {
  if (!o.workflowName) return o.fileName;
  return o.runNumber ? t('libLabels.runSummary.renderTitleRun', { name: o.workflowName, number: o.runNumber }) : o.workflowName;
}

/** A download name a customer can recognise in their Downloads folder. */
export function renderDownloadName(o: RenderContext, stage: RenderStage | null, t: T = enT): string {
  if (!o.workflowName) return o.fileName;
  const ext = /\.[a-z0-9]{2,5}$/i.exec(o.fileName)?.[0] ?? '.mp4';
  const base = `${renderTitle(o, t)}${stage === 'draft' ? t('libLabels.runSummary.draftSuffix') : ''}`
    .replace(/[\\/:*?"<>|\u0000-\u001f]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return `${base || t('libLabels.runSummary.downloadFallback')}${ext}`;
}
