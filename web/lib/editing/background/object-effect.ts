/**
 * Magic blur as a background tool: the same `regionEffects` entry the inspector has always built, the ops that put
 * it on a set of clips, the routes that adopt and settle server-launched results, and the landing step the editor's
 * watcher runs. Existing ops only (`setProperty` on a media clip's `regionEffects`).
 */

import { ApiError, apiFetch } from '@/lib/api/client';
import { MAX_REGION_EFFECTS, type ObjectRequest } from '@/lib/editing/api/objects';
import type { EditOp } from '../engine';
import { onBatchLanded, type EditorStore } from '../store/editor-store';
import type { EditTimeline, MediaClip, RegionClipEffect, RegionEffectType } from '../types';
import { allMediaClips, rebaseAnchor, type ResultAnchor } from './anchor';
import type { BackgroundEntry, ResultsStore } from './results-store';

export interface ObjectIntent {
  prompt: string;
  effect: RegionEffectType;
  strength: number;
  invert: boolean;
  featherPx: number;
  analysisStepResultId?: string;
  trackIds?: string[];
  /** Known only when the launch says which workflow ran the job; without it the job cannot be stopped from here. */
  workflowId?: string;
}

export const canTakeRegionEffect = (clip: MediaClip): boolean => (clip.regionEffects?.length ?? 0) < MAX_REGION_EFFECTS;

const intentOf = (entry: BackgroundEntry): ObjectIntent => entry.intent as unknown as ObjectIntent;

/** The entry written to a clip: exactly what the inspector built when it applied the result itself. */
export function regionEntryFor(intent: ObjectIntent): RegionClipEffect | null {
  if (!intent.analysisStepResultId || !intent.trackIds || intent.trackIds.length === 0) return null;
  return {
    effect: intent.effect,
    strength: intent.strength,
    mask: { kind: 'segmentation', analysisStepResultId: intent.analysisStepResultId, trackIds: intent.trackIds.slice(0, 8), invert: intent.invert, featherPx: intent.featherPx },
    enabled: true,
  };
}

const sameEntry = (a: RegionClipEffect, b: RegionClipEffect): boolean =>
  a.effect === b.effect &&
  a.strength === b.strength &&
  a.mask.analysisStepResultId === b.mask.analysisStepResultId &&
  a.mask.invert === b.mask.invert &&
  a.mask.featherPx === b.mask.featherPx &&
  a.mask.trackIds.length === b.mask.trackIds.length &&
  a.mask.trackIds.every((id, i) => id === b.mask.trackIds[i]);

/** One `setProperty` per clip that can take the entry and does not already carry an identical one. */
export function objectEffectOps(doc: EditTimeline, clipIds: readonly string[], entry: RegionClipEffect): EditOp[] {
  const byId = new Map(allMediaClips(doc).map((c) => [c.id, c]));
  const ops: EditOp[] = [];
  for (const id of clipIds) {
    const clip = byId.get(id);
    if (!clip) continue;
    const current = clip.regionEffects ?? [];
    if (current.some((e) => sameEntry(e, entry)) || current.length >= MAX_REGION_EFFECTS) continue;
    ops.push({ type: 'setProperty', itemId: id, key: 'regionEffects', value: [...current, entry] } as EditOp);
  }
  return ops;
}

// ── server routes (absent routes answer 404: the feature is not there, which is not an error) ──

export interface ServerObjectItem {
  requestId: string;
  executionId: string;
  workflowId?: string | null;
  mediaRef: string;
  clipIds: string[];
  prompt: string;
  effect: RegionEffectType;
  strength: number;
  invert: boolean;
  featherPx: number;
  coverageEndSec: number | null;
  analysisStepResultId: string;
  trackIds: string[];
  createdAt: string;
}

const enc = encodeURIComponent;

/** Results the assistant started that wait for the user's OK; `null` when the route is not there. */
export async function fetchPendingServerResults(projectId: string, timelineId: string): Promise<ServerObjectItem[] | null> {
  try {
    const res = await apiFetch<{ items: ServerObjectItem[] }>(`/api/v1/projects/${enc(projectId)}/edit-timelines/${enc(timelineId)}/object-requests?state=needs_review`);
    return res.items ?? [];
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

/** Tells the server what became of a result. A 404 (feature absent) is ignored. */
export async function resolveObjectOnServer(projectId: string, mediaRef: string, requestId: string, action: 'applied' | 'discarded', revision?: number): Promise<void> {
  try {
    await apiFetch(`/api/v1/projects/${enc(projectId)}/media/${enc(mediaRef)}/objects/${enc(requestId)}/resolve`, { method: 'POST', body: JSON.stringify(revision === undefined ? { action } : { action, revision }) });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return;
    throw error;
  }
}

/** Stops a running job. Only possible when the launch recorded the workflow. */
export const stopObjectJob = (projectId: string, workflowId: string, executionId: string): Promise<unknown> =>
  apiFetch(`/api/v1/projects/${enc(projectId)}/workflows/${enc(workflowId)}/executions/${enc(executionId)}/stop`, { method: 'POST' });

export function entryFromServerItem(doc: EditTimeline, item: ServerObjectItem): BackgroundEntry {
  const anchor: ResultAnchor = {
    assetKey: item.mediaRef,
    clipIds: [...item.clipIds],
    // The assistant targeted exactly these clips at launch; a clip split during the run left pieces that are not in
    // the list, and they join by source-time overlap (the adoption-time document must not decide what was there).
    assetClipIdsAtLaunch: [...item.clipIds],
    coverage: { startSec: 0, endSec: item.coverageEndSec },
  };
  const intent: ObjectIntent = {
    prompt: item.prompt,
    effect: item.effect,
    strength: item.strength,
    invert: item.invert,
    featherPx: item.featherPx,
    analysisStepResultId: item.analysisStepResultId,
    trackIds: item.trackIds,
    ...(item.workflowId ? { workflowId: item.workflowId } : {}),
  };
  const outcome = rebaseAnchor(doc, anchor, canTakeRegionEffect);
  const review = outcome.kind === 'apply' ? { applicable: outcome.clipIds, reasons: [], lostCount: 0 } : { applicable: outcome.applicable, reasons: outcome.reasons, lostCount: outcome.lostCount };
  return { id: `srv:${item.requestId}`, tool: 'objectEffect', requestId: item.requestId, executionId: item.executionId, anchor, intent: intent as unknown as Record<string, unknown>, state: 'needsReview', review, createdAt: item.createdAt };
}

const refusalText = (error: unknown): string => (error instanceof Error && error.message ? error.message : 'The editor did not accept the change.');

/**
 * A finished request landed. Rebases its anchor onto the CURRENT document and, when nothing is lost, applies it as
 * ONE undo step through the editor's normal path. Anything else becomes "needs your OK": never a silent drop.
 */
export function landObjectResult(deps: { results: ResultsStore; editor: EditorStore; entry: BackgroundEntry; request: ObjectRequest; label: string; onApplied?: (entry: BackgroundEntry) => void }): void {
  const { results, editor, entry, request, label } = deps;
  if (request.status === 'failed') {
    results.update(entry.id, { state: 'failed', failure: { code: request.failureCode, message: request.message } });
    return;
  }
  const intent: ObjectIntent = { ...intentOf(entry), analysisStepResultId: request.analysisStepResultId ?? undefined, trackIds: request.trackIds ?? undefined };
  const regionEntry = regionEntryFor(intent);
  if (!regionEntry) {
    results.update(entry.id, { state: 'failed', failure: { code: 'RESULT_UNAVAILABLE', message: null } });
    return;
  }
  const anchor: ResultAnchor = request.coverageEndSec !== null ? { ...entry.anchor, coverage: { startSec: entry.anchor.coverage.startSec, endSec: request.coverageEndSec } } : entry.anchor;
  const landed: BackgroundEntry = { ...entry, anchor, intent: intent as unknown as Record<string, unknown> };
  results.update(entry.id, { anchor, intent: landed.intent });
  settleLanded({ results, editor, entry: landed, label, onApplied: deps.onApplied, autoApply: true });
}

/** The rebase-and-apply step shared by a landing and by "Apply to what is left". */
export function settleLanded(deps: { results: ResultsStore; editor: EditorStore; entry: BackgroundEntry; label: string; autoApply: boolean; onApplied?: (entry: BackgroundEntry) => void }): void {
  const { results, editor, entry, label } = deps;
  const regionEntry = regionEntryFor(intentOf(entry));
  if (!regionEntry) {
    results.update(entry.id, { state: 'failed', failure: { code: 'RESULT_UNAVAILABLE', message: null } });
    return;
  }
  const outcome = rebaseAnchor(editor.getState().doc, entry.anchor, canTakeRegionEffect);
  const clipIds = outcome.kind === 'apply' ? outcome.clipIds : outcome.applicable;
  const review = outcome.kind === 'apply' ? { applicable: clipIds, reasons: [], lostCount: 0 } : { applicable: outcome.applicable, reasons: outcome.reasons, lostCount: outcome.lostCount };
  if ((deps.autoApply && outcome.kind !== 'apply') || clipIds.length === 0) {
    results.update(entry.id, { state: 'needsReview', review });
    return;
  }
  if (editor.getState().readOnly) {
    results.update(entry.id, { state: 'needsReview', review: { ...review, blocked: 'readOnly' } });
    return;
  }
  const ops = objectEffectOps(editor.getState().doc, clipIds, regionEntry);
  const markApplied = () => {
    results.resolve(entry.id, 'applied');
    deps.onApplied?.(entry);
  };
  try {
    if (ops.length > 0) {
      // Applied only when the batch really landed. The edit guard may hold it (the user can press Back): then the
      // result waits for the user's OK, and "Go on" lands it later and records it then.
      onBatchLanded(ops, markApplied);
      if (!editor.dispatchBatch(ops, label, { exemptJobId: entry.id })) {
        results.update(entry.id, { state: 'needsReview', review });
        return;
      }
      return;
    }
  } catch (error) {
    results.update(entry.id, { state: 'needsReview', review: { ...review, blocked: 'refused', refusal: refusalText(error) } });
    return;
  }
  markApplied();
}
