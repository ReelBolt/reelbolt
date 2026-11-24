'use client';

/**
 * AI proposals as a background tool. A proposal is asked for a range; the server answers while the user keeps
 * editing. The request is registered here so the range's clips show busy and an edit that affects them asks first;
 * the answer is KEPT (persisted) when the panel is gone; and when it no longer fits the edited timeline the groups
 * that still fit can be applied as one undo step instead of the whole paid answer being refused and lost.
 */

import { useSyncExternalStore } from 'react';
import type { EditProposalResponse, ProposalAgent } from '../sync/api';
import { idsNamedBy, planProposal, userOwnedTouchedBy } from '../proposals/apply-proposal';
import { onBatchLanded, type EditorStore } from '../store/editor-store';
import type { EditTimeline, MediaClip } from '../types';
import { allMediaClips, anchorForClips } from './anchor';
import type { BackgroundEntry, ResultsStore } from './results-store';

export const PROPOSAL_TOOL = 'proposal';

export interface ProposalIntent {
  agent: ProposalAgent;
  brief: string | null;
  range: { inFrame: number; outFrame: number } | null;
  /** Where each touched clip sat in the record at launch (the answer's ops are written against these frames). */
  placements?: Placements;
  /** Every item on the main tracks at launch (id to placement) and the timeline end: what a group's target frames sat among. */
  surroundings?: { items: Placements; endFrame: number };
  /** The answer, once it landed (what is still waiting for the user's OK). */
  response?: EditProposalResponse;
}

export type Placements = Record<string, { recordIn: number; recordOut: number; sourceIn: number; rate: number }>;

const placementOf = (c: MediaClip): Placements[string] => ({ recordIn: c.recordIn, recordOut: c.recordOut, sourceIn: c.sourceIn ?? 0, rate: c.playbackRate && c.playbackRate > 0 ? c.playbackRate : 1 });

export const proposalIntentOf = (entry: BackgroundEntry): ProposalIntent => entry.intent as unknown as ProposalIntent;
export const isProposalEntry = (entry: BackgroundEntry): boolean => entry.tool === PROPOSAL_TOOL;

/** Proposals asked in THIS page session; a `running` one that is not here lost its request (a reload). */
const live = new Set<string>();

/** The media clips on the main tracks that the range touches (no range = the whole timeline). */
export function clipsInRange(doc: EditTimeline, range: ProposalIntent['range']): string[] {
  const clips = doc.tracks.flatMap((t) => t.items.filter((i): i is MediaClip => i.type === 'mediaClip'));
  return clips.filter((c) => range === null || (c.recordIn < range.outFrame && c.recordOut > range.inFrame)).map((c) => c.id);
}

/** The `running` entry for a request about to be sent. Coverage is the whole of each clip the range touches. */
export function proposalLaunchEntry(doc: EditTimeline, intent: ProposalIntent, id: string, now: string): BackgroundEntry {
  const clipIds = clipsInRange(doc, intent.range);
  const anchor = anchorForClips(doc, clipIds, { startSec: 0, endSec: null });
  live.add(id);
  const placements: Placements = {};
  for (const c of allMediaClips(doc)) if (clipIds.includes(c.id)) placements[c.id] = placementOf(c);
  const all = doc.tracks.flatMap((t) => t.items);
  const items: Placements = {};
  for (const i of all) items[i.id] = { recordIn: i.recordIn, recordOut: i.recordOut, sourceIn: (i as MediaClip).sourceIn ?? 0, rate: (i as MediaClip).playbackRate && (i as MediaClip).playbackRate! > 0 ? (i as MediaClip).playbackRate! : 1 };
  const surroundings = { items, endFrame: all.reduce((m, i) => Math.max(m, i.recordOut), 0) };
  return { id, tool: PROPOSAL_TOOL, requestId: id, anchor, intent: { ...intent, placements, surroundings } as unknown as Record<string, unknown>, state: 'running', createdAt: now };
}

/** The answer landed: keep it and wait for the user's OK (the busy mark ends here). */
export function landProposal(results: ResultsStore, id: string, response: EditProposalResponse): void {
  live.delete(id);
  const entry = results.getEntries().find((e) => e.id === id);
  if (!entry) return;
  results.update(id, { state: 'needsReview', intent: { ...entry.intent, response } as unknown as Record<string, unknown>, review: { applicable: [], reasons: [], lostCount: 0 } });
}

export function failProposal(results: ResultsStore, id: string, message: string | null): void {
  live.delete(id);
  results.update(id, { state: 'failed', failure: { code: null, message } });
}

/** A `running` proposal whose request died with the page cannot land any more: stop marking its clips busy. */
export function sweepInterruptedProposals(results: ResultsStore): void {
  for (const e of results.getEntries()) {
    if (isProposalEntry(e) && e.state === 'running' && !live.has(e.id)) results.update(e.id, { state: 'failed', failure: { code: 'INTERRUPTED', message: null } });
  }
}

/** Replaces the groups still waiting (after a partial accept or a skip), or finishes the entry when none are left. */
export function keepRemainingGroups(results: ResultsStore, id: string, remaining: EditProposalResponse | null, finished: 'applied' | 'discarded'): void {
  const entry = results.getEntries().find((e) => e.id === id);
  if (!entry) return;
  if (remaining === null || remaining.groups.length === 0) results.resolve(id, finished);
  else results.update(id, { intent: { ...entry.intent, response: remaining } as unknown as Record<string, unknown> });
}

export interface GroupFit {
  /** Groups that apply together on `doc`, in proposal order. */
  fits: number[];
  /** Groups the document no longer lets through (a clip they name was deleted or the ops refuse). */
  stale: number[];
  /** Groups written against a part of the video that was moved, trimmed or re-timed since the suggestions were asked for. */
  changed: number[];
  /** Groups that would change a clip the user made: they keep the ghost-diff acknowledgement flow, not this one. */
  needsAck: number[];
}

/** True when a clip a group names no longer sits where it did at launch. Unknown launch placements verify nothing. */
type Surroundings = NonNullable<ProposalIntent['surroundings']>;

/** The record frames a group's ops aim at: placed items' spans, split/gap frames, or the timeline end for append. */
function targetFrames(ops: Parameters<typeof idsNamedBy>[0][], endFrame: number): Array<[number, number]> {
  const out: Array<[number, number]> = [];
  for (const op of ops) {
    const o = op as unknown as { type: string; item?: { recordIn: number; recordOut: number }; atFrame?: number };
    if (op.type === 'append') out.push([endFrame, endFrame]);
    else if (op.type === 'insert' || op.type === 'placeOnTop') {
      if (o.item) out.push([o.item.recordIn, o.item.recordOut]);
    } else if (op.type === 'deleteGap' && typeof o.atFrame === 'number') out.push([o.atFrame, o.atFrame]);
  }
  return out;
}

/** True when anything around the group's target frames moved, was trimmed, was deleted, or appeared since launch. */
function surroundingsChanged(doc: EditTimeline, ops: Parameters<typeof idsNamedBy>[0][], surroundings: Surroundings | undefined): boolean {
  const frames = targetFrames(ops, surroundings?.endFrame ?? 0);
  if (frames.length === 0) return false;
  if (!surroundings) return true;
  const now = doc.tracks.flatMap((t) => t.items);
  const end = now.reduce((m, i) => Math.max(m, i.recordOut), 0);
  return frames.some(([lo, hi]) => {
    if (lo === hi && lo === surroundings.endFrame && end !== surroundings.endFrame) return true;
    const near = (a: number, b: number) => a <= hi && b >= lo;
    const was = Object.entries(surroundings.items).filter(([, p]) => near(p.recordIn, p.recordOut));
    const is = now.filter((i) => near(i.recordIn, i.recordOut));
    if (was.length !== is.length) return true;
    return was.some(([id, p]) => {
      const i = now.find((x) => x.id === id);
      return !i || i.recordIn !== p.recordIn || i.recordOut !== p.recordOut;
    });
  });
}

function movedSinceLaunch(doc: EditTimeline, group: { ops: Parameters<typeof idsNamedBy>[0][] }, placements: Placements | undefined, surroundings: Surroundings | undefined): boolean {
  if (!placements) return true;
  if (surroundingsChanged(doc, group.ops, surroundings)) return true;
  const now = new Map(allMediaClips(doc).map((c) => [c.id, c]));
  for (const id of new Set(group.ops.flatMap(idsNamedBy))) {
    const was = placements[id];
    const clip = now.get(id);
    if (!was || !clip) continue;
    const is = placementOf(clip);
    if (is.recordIn !== was.recordIn || is.recordOut !== was.recordOut || is.sourceIn !== was.sourceIn || is.rate !== was.rate) return true;
  }
  return false;
}

/**
 * Which of the groups still apply to the CURRENT document, which no longer do, and which were written for a part
 * that changed. Never mutates `doc`. (Proposal ops are record-frame based; they are not re-based by source time.)
 */
export function groupFit(doc: EditTimeline, proposal: Pick<EditProposalResponse, 'groups'>, placements: Placements | undefined, which: Iterable<number> = proposal.groups.map((_, i) => i), surroundings?: Surroundings): GroupFit {
  const out: GroupFit = { fits: [], stale: [], changed: [], needsAck: [] };
  for (const index of [...which].sort((a, b) => a - b)) {
    const group = proposal.groups[index];
    if (!group) continue;
    if (movedSinceLaunch(doc, group, placements, surroundings)) {
      out.changed.push(index);
      continue;
    }
    if (userOwnedTouchedBy(doc, group).length > 0) {
      out.needsAck.push(index);
      continue;
    }
    try {
      planProposal(doc, proposal, new Set([...out.fits, index]));
      out.fits.push(index);
    } catch {
      out.stale.push(index);
    }
  }
  return out;
}

export type ProposalReviewKind = 'ready' | 'someStale' | 'allStale';

/** What the "needs your OK" list says about a landed proposal right now. */
export function reviewKind(doc: EditTimeline, proposal: Pick<EditProposalResponse, 'groups'>, placements?: Placements, surroundings?: Surroundings): ProposalReviewKind {
  const fit = groupFit(doc, proposal, placements, undefined, surroundings);
  if (fit.stale.length === 0 && fit.changed.length === 0) return 'ready';
  return fit.fits.length + fit.needsAck.length > 0 ? 'someStale' : 'allStale';
}

export type ApplyFitsOutcome = 'applied' | 'nothing' | 'readOnly' | 'refused' | 'held';

/**
 * "Apply what still fits": ONE batch of the groups that still apply (one undo step), the `beforeProposal` version
 * taken first. Groups that need acknowledgement stay waiting; stale ones are dropped with this choice.
 */
export async function applyWhatFits(deps: { results: ResultsStore; editor: EditorStore; entry: BackgroundEntry; label: string; beforeApply?: () => Promise<unknown> }): Promise<ApplyFitsOutcome> {
  const { results, editor, entry } = deps;
  const intent = proposalIntentOf(entry);
  const response = intent.response;
  if (!response) return 'nothing';
  if (editor.getState().readOnly) return 'readOnly';
  if (groupFit(editor.getState().doc, response, intent.placements, undefined, intent.surroundings).fits.length === 0) return 'nothing';
  await deps.beforeApply?.().catch(() => undefined);
  const fit = groupFit(editor.getState().doc, response, intent.placements, undefined, intent.surroundings);
  if (fit.fits.length === 0) return 'nothing';
  let landed = false;
  try {
    const plan = planProposal(editor.getState().doc, response, new Set(fit.fits));
    if (plan.ops.length > 0) {
      const waiting = [...fit.needsAck, ...fit.changed].sort((x, y) => x - y).map((i) => response.groups[i]);
      // Recorded as applied only when the batch really landed; if the edit guard holds it, the paid answer stays as it
      // was and "Go on" lands (and records) it later.
      onBatchLanded(plan.ops, () => {
        landed = true;
        keepRemainingGroups(results, entry.id, waiting.length > 0 ? { ...response, groups: waiting } : null, 'applied');
      });
      if (!editor.dispatchBatch(plan.ops, deps.label, { exemptJobId: entry.id })) return 'held';
    }
  } catch {
    return 'refused';
  }
  return landed ? 'applied' : 'nothing';
}

// ── "Review" from the needs-your-OK list: the assist panel / sheet picks the entry up when it mounts ──
type Listener = () => void;
let requested: string | null = null;
const reviewListeners = new Set<Listener>();
const emit = () => {
  for (const l of reviewListeners) l();
};
export function requestProposalReview(entryId: string): void {
  requested = entryId;
  emit();
}
/** The entry a review was requested for, without taking it. */
export const peekProposalReview = (): string | null => requested;
export function clearProposalReview(): void {
  if (requested === null) return;
  requested = null;
  emit();
}
const subscribeReview = (l: Listener): (() => void) => {
  reviewListeners.add(l);
  return () => {
    reviewListeners.delete(l);
  };
};
/** The entry the user asked to review (null when none is waiting to be picked up). */
export const useProposalReviewRequest = (): string | null => useSyncExternalStore(subscribeReview, () => requested, () => null);
/** Calls `fn` whenever a review is requested (a shell opens the assist panel / sheet from it). */
export const onProposalReviewRequested = (fn: () => void): (() => void) =>
  subscribeReview(() => {
    if (requested !== null) fn();
  });
