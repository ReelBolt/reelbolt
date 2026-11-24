/**
 * Audio cleanup as a background tool. The cleaned file is a whole new source, so the anchor covers the whole
 * source and the result lands on EVERY clip that still plays it (split pieces included), as one undo step.
 * Lane B's `planApply` decides what changes on each clip; nothing of its contract is re-implemented here.
 */

import type { EditOp } from '../engine';
import { onBatchLanded } from '../store/editor-store';
import { getCleanupJob, isCleanupFailed } from '../cleanup/cleanup-api';
import { planApply, type CleanupRecord, type CleanupSubject } from '../cleanup/plan';
import type { CleanupJob, CleanupOutputKind } from '../cleanup/types';
import type { EditTimeline, MediaClip } from '../types';
import { allMediaClips, anchorForClips, rebaseAnchor } from './anchor';
import type { BackgroundEntry, ResultsStore } from './results-store';

/** What landing needs of an editor: the real store, or the panel's own `doc` + `onApply` (one undo step either way). */
export interface CleanupEditor {
  getState(): { doc: EditTimeline; readOnly: boolean };
  /** `false` when the edit guard held the batch (it lands later if the user goes on). */
  dispatchBatch(ops: EditOp[], label: string, options?: { exemptJobId?: string }): boolean;
}

export const AUDIO_CLEANUP_TOOL = 'audioCleanup';

export interface AudioCleanupIntent {
  outputKind: CleanupOutputKind;
  /** Set when the run landed: the cleaned file the clips switch to. */
  outputFileId?: string;
}

export const isAudioCleanupEntry = (entry: BackgroundEntry): boolean => entry.tool === AUDIO_CLEANUP_TOOL;

const intentOf = (entry: BackgroundEntry): AudioCleanupIntent => entry.intent as unknown as AudioCleanupIntent;

/** The background entry of a cleanup run just started for `clipId`. Null when the clip has no usable source. */
export function entryForCleanup(args: { doc: EditTimeline; clipId: string; executionId: string; outputKind: CleanupOutputKind; now?: () => string }): BackgroundEntry | null {
  const anchor = anchorForClips(args.doc, [args.clipId], { startSec: 0, endSec: null });
  if (anchor.assetKey === '') return null;
  const intent: AudioCleanupIntent = { outputKind: args.outputKind };
  return {
    id: `cleanup:${args.executionId}`,
    tool: AUDIO_CLEANUP_TOOL,
    requestId: args.executionId,
    executionId: args.executionId,
    anchor,
    intent: intent as unknown as Record<string, unknown>,
    state: 'running',
    createdAt: (args.now ?? (() => new Date().toISOString()))(),
  };
}

/** Reads the run the way the panel does (lane B's route); the watcher calls this. */
export const fetchCleanup = (projectId: string, entry: BackgroundEntry): Promise<CleanupJob> => getCleanupJob(projectId, entry.anchor.assetKey, entry.requestId);

/** One batch: lane B's `planApply` for every target clip; clips that already carry the cleaned file add nothing. */
export function cleanupApplyPlan(doc: EditTimeline, entry: BackgroundEntry, clipIds: readonly string[]): { ops: EditOp[]; label: string } | null {
  const outputFileId = intentOf(entry).outputFileId;
  if (!outputFileId) return null;
  const byId = new Map(allMediaClips(doc).map((c) => [c.id, c]));
  const ops: EditOp[] = [];
  let label = '';
  for (const id of clipIds) {
    const clip: MediaClip | undefined = byId.get(id);
    if (!clip) continue;
    const subject: CleanupSubject = { clip, mediaRef: entry.anchor.assetKey, outputKind: intentOf(entry).outputKind === 'VideoWithCleanAudio' ? 'VideoWithCleanAudio' : 'Audio', linked: false };
    const plan = planApply(doc, subject, outputFileId);
    if (!plan) continue;
    ops.push(...plan.ops);
    label = plan.label;
  }
  return ops.length > 0 ? { ops, label } : null;
}

/** Everything `cleanupApplyPlan` would change, as one record, so "Use original" reverts every piece. */
export function cleanupRecordFor(doc: EditTimeline, entry: BackgroundEntry, clipIds: readonly string[]): CleanupRecord | null {
  const outputFileId = intentOf(entry).outputFileId;
  if (!outputFileId) return null;
  const byId = new Map(allMediaClips(doc).map((c) => [c.id, c]));
  let record: CleanupRecord | null = null;
  for (const id of clipIds) {
    const clip = byId.get(id);
    if (!clip) continue;
    const plan = planApply(doc, { clip, mediaRef: entry.anchor.assetKey, outputKind: 'Audio', linked: false }, outputFileId);
    if (!plan) continue;
    record = record ? { cleaned: record.cleaned, items: [...record.items, ...plan.record.items] } : plan.record;
  }
  return record;
}

const refusalText = (error: unknown): string => (error instanceof Error && error.message ? error.message : 'The editor did not accept the change.');

/** A cleanup that landed and waits for the user's OK with nothing lost: "Clean audio ready". */
export const isCleanupReady = (entry: BackgroundEntry): boolean => isAudioCleanupEntry(entry) && entry.state === 'needsReview' && (entry.review?.reasons.length ?? 0) === 0 && (entry.review?.lostCount ?? 0) === 0 && !entry.review?.blocked;

/** A finished (or failed) cleanup run: records the cleaned file and parks it as ready (or needs-your-OK). Never applies. */
export function landCleanupResult(deps: { results: ResultsStore; editor: CleanupEditor; entry: BackgroundEntry; job: CleanupJob }): void {
  const { results, editor, entry, job } = deps;
  if (isCleanupFailed(job)) {
    results.update(entry.id, { state: 'failed', failure: { code: job.failureCode ?? null, message: job.message ?? null } });
    return;
  }
  if (job.applied === false || !job.outputFileId) {
    // Nothing to remove (no hum found, ...): there is nothing to apply, and nothing is lost.
    if (job.applied === false) results.resolve(entry.id, 'applied');
    else results.update(entry.id, { state: 'failed', failure: { code: 'RESULT_UNAVAILABLE', message: null } });
    return;
  }
  const intent: AudioCleanupIntent = { ...intentOf(entry), outputFileId: job.outputFileId };
  const landed: BackgroundEntry = { ...entry, intent: intent as unknown as Record<string, unknown> };
  results.update(entry.id, { intent: landed.intent });
  const outcome = rebaseAnchor(editor.getState().doc, landed.anchor);
  const review = outcome.kind === 'apply' ? { applicable: outcome.clipIds, reasons: [], lostCount: 0 } : { applicable: outcome.applicable, reasons: outcome.reasons, lostCount: outcome.lostCount };
  results.update(entry.id, { state: 'needsReview', review });
}

/** The rebase-and-apply step shared by a landing and by "Apply to what is left". */
export function settleCleanup(deps: { results: ResultsStore; editor: CleanupEditor; entry: BackgroundEntry; autoApply: boolean; onApplied?: (entry: BackgroundEntry) => void }): void {
  const { results, editor, entry } = deps;
  const outcome = rebaseAnchor(editor.getState().doc, entry.anchor);
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
  const plan = cleanupApplyPlan(editor.getState().doc, entry, clipIds);
  const markApplied = () => {
    results.resolve(entry.id, 'applied');
    deps.onApplied?.(entry);
  };
  try {
    if (plan) {
      // Applied only when the batch really landed; a held batch keeps waiting and records itself on "Go on".
      onBatchLanded(plan.ops, markApplied);
      if (!editor.dispatchBatch(plan.ops, plan.label, { exemptJobId: entry.id })) {
        results.update(entry.id, { state: 'needsReview', review });
        return;
      }
      // A caller that cannot report the landing (the panel) returns true without calling back: record it here.
      if (results.getEntries().find((e) => e.id === entry.id)?.state === 'needsReview') markApplied();
      return;
    }
  } catch (error) {
    results.update(entry.id, { state: 'needsReview', review: { ...review, blocked: 'refused', refusal: refusalText(error) } });
    return;
  }
  markApplied();
}
