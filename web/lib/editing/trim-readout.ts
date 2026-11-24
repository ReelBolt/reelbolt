/**
 * T-019: what a trim drag is about to do, in a form a readout can say in plain words. Pure: no React,
 * no copy. The caller turns `key` + `values` and `limit` into catalogue strings, so a later readout (T-020's
 * snap labels and ripple ghosts) extends the union here without touching the component.
 */
import { apply, type EditOp, type TrimEdge, type TrimMode } from '@/lib/editing/engine';
import { jlCutOps, linkedOpsForTrim, linkIdOf, locate, type LinkOptions } from '@/lib/editing/link';
import type { EditTimeline, MediaClip, Rational } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';

/** Why the drag stopped short of what the pointer asked for. */
export type TrimLimit = 'sourceStart' | 'sourceEnd' | 'minLength' | 'neighbour' | 'noNeighbour' | 'noSource';

/** The engine's answer to a trim request, without applying it. */
export type TrimProbe = { effective: number } | { blocked: 'noNeighbour' | 'noSource' };

/** One clip a ripple will carry along: where it is now and where it will be. */
export interface ShiftedClip {
  itemId: string;
  trackId: string;
  from: number;
  to: number;
  length: number;
}

/** What a ripple will carry along, linked partners included (the engine moves them, so they are in the diff). */
export interface RippleShift {
  count: number;
  /** The largest whole-frame shift among them. */
  frames: number;
  clips: readonly ShiftedClip[];
}

const NO_SHIFT: RippleShift = { count: 0, frames: 0, clips: [] };

/** Clips whose start moved while their length did not: the ones a ripple carries, not the ones it trims. */
function diffShift(before: EditTimeline, after: EditTimeline): RippleShift {
  const was = new Map<string, { recordIn: number; length: number }>();
  for (const track of before.tracks) for (const item of track.items) was.set(item.id, { recordIn: item.recordIn, length: item.recordOut - item.recordIn });
  const clips: ShiftedClip[] = [];
  let frames = 0;
  for (const track of after.tracks) {
    for (const item of track.items) {
      const old = was.get(item.id);
      const length = item.recordOut - item.recordIn;
      if (!old || old.recordIn === item.recordIn || old.length !== length) continue;
      clips.push({ itemId: item.id, trackId: track.id, from: old.recordIn, to: item.recordIn, length });
      frames = Math.max(frames, Math.abs(item.recordIn - old.recordIn));
    }
  }
  return clips.length === 0 ? NO_SHIFT : { count: clips.length, frames, clips };
}

/** The source frames a clip reads, the server validator's own sum (`EditTimelineValidator`): only a mediaClip is checked. */
function requiredSourceFrames(clip: MediaClip): number {
  const length = clip.recordOut - clip.recordIn;
  const rate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const consumed = clip.hold ? 1 : Math.floor(length * rate);
  return (clip.sourceIn ?? 0) + Math.max(1, consumed);
}

/**
 * How far past its source's end the worst clip this edit changed now runs, in timeline frames; 0 when none does.
 * A clip already over (an old document) only counts when the edit makes it worse. Unknown length is not checked,
 * as on the server.
 */
function sourceOverrun(before: EditTimeline, after: EditTimeline, sourceFramesFor: (clip: MediaClip) => number | null): number {
  const was = new Map<string, EditTimeline['tracks'][number]['items'][number]>();
  for (const track of before.tracks) for (const item of track.items) was.set(item.id, item);
  let over = 0;
  for (const track of after.tracks) {
    for (const item of track.items) {
      if (item.type !== 'mediaClip' || was.get(item.id) === item) continue;
      const available = sourceFramesFor(item);
      if (available === null) continue;
      const required = requiredSourceFrames(item);
      if (required <= available) continue;
      const old = was.get(item.id);
      if (old?.type === 'mediaClip' && requiredSourceFrames(old) >= required) continue;
      const rate = !item.hold && typeof item.playbackRate === 'number' && item.playbackRate > 0 ? item.playbackRate : 1;
      over = Math.max(over, Math.ceil((required - available) / rate));
    }
  }
  return over;
}

/** One trim drag's request: what the pointer asked for, from either route (handle or body). */
export interface TrimDragRequest {
  itemId: string;
  edge: TrimEdge;
  mode: TrimMode;
  deltaFrames: number;
  /** Alt held or "Edit sound separately" on: this clip's edge alone. */
  separately?: boolean;
}

export interface TrimDragContext {
  linkOptions?: LinkOptions;
  /** The clip's source length in timeline frames, or null when unknown (then it is not clamped). */
  sourceFramesFor?: (clip: MediaClip) => number | null;
}

/** What a drag will really do. The preview reads it and the release dispatches `ops`, so the two cannot differ. */
export interface TrimPlan {
  ops: readonly EditOp[];
  probe: TrimProbe;
  shift: RippleShift;
  /** The request was shortened because a clip would have run past the end of its source. */
  stoppedBySource: boolean;
}

const NO_CONTEXT: TrimDragContext = {};
/** Tries before giving up on a delta: each step removes the measured overrun, so one or two are the norm. */
const MAX_FIT_STEPS = 6;

function dragOps(doc: EditTimeline, request: TrimDragRequest, deltaFrames: number, context: TrimDragContext): readonly EditOp[] {
  const { itemId, edge, mode, separately } = request;
  if (separately) {
    const found = locate(doc, itemId);
    if (found && linkIdOf(found.item) !== null && mode === 'ripple') return jlCutOps(doc, itemId, edge, deltaFrames);
    return [{ type: 'trim', itemId, edge, mode, deltaFrames }];
  }
  return linkedOpsForTrim(doc, itemId, edge, mode, deltaFrames, context.linkOptions);
}

function runOps(doc: EditTimeline, ops: readonly EditOp[], itemId: string): { after: EditTimeline; effective: number } {
  let after = doc;
  let effective = 0;
  for (const op of ops) {
    const result = apply(after, op);
    if (op.type === 'trim' && op.itemId === itemId) {
      const undo = result.inverse.find((inverse) => inverse.type === 'trim');
      effective = undo?.type === 'trim' ? (undo.deltaFrames === 0 ? 0 : -undo.deltaFrames) : op.deltaFrames;
    }
    after = result.doc;
  }
  return { after, effective };
}

function computePlan(doc: EditTimeline, request: TrimDragRequest, context: TrimDragContext): TrimPlan {
  const { itemId, edge, mode, deltaFrames } = request;
  const bare: EditOp = { type: 'trim', itemId, edge, mode, deltaFrames };
  try {
    // A drag that has not moved yet still says whether the mode can act on this clip at all.
    if (deltaFrames === 0) apply(doc, bare);
    let ops = dragOps(doc, request, deltaFrames, context);
    let { after, effective } = runOps(doc, ops, itemId);
    let stoppedBySource = false;
    const sourceFramesFor = context.sourceFramesFor;
    if (sourceFramesFor) {
      const sign = Math.sign(deltaFrames);
      for (let step = 0; ; step += 1) {
        const over = sourceOverrun(doc, after, sourceFramesFor);
        if (over === 0) break;
        stoppedBySource = true;
        const shorter = effective - sign * Math.max(1, over);
        if (step === MAX_FIT_STEPS || Math.sign(shorter) !== sign) {
          ops = [];
          after = doc;
          effective = 0;
          break;
        }
        ops = dragOps(doc, request, shorter, context);
        ({ after, effective } = runOps(doc, ops, itemId));
      }
    }
    return { ops, probe: { effective }, shift: mode === 'ripple' ? diffShift(doc, after) : NO_SHIFT, stoppedBySource };
  } catch (error) {
    const message = error instanceof Error ? error.message : '';
    // Kept as the bare op: the release then shows the engine's refusal, as it always has.
    return { ops: [bare], probe: { blocked: /carries none/.test(message) ? 'noSource' : 'noNeighbour' }, shift: NO_SHIFT, stoppedBySource: false };
  }
}

const lastPlan = new WeakMap<EditTimeline, { key: string; context: TrimDragContext; plan: TrimPlan }>();

/**
 * The one answer to "what does this trim drag do": the same op list for the preview and for the release, including
 * linked partners, the separately route and the stop at the source's end. Memoised on the last request per document,
 * so a drag that stays on one frame costs nothing after the first move, and nothing outlives the document.
 */
export function planTrimDrag(doc: EditTimeline, request: TrimDragRequest, context: TrimDragContext = NO_CONTEXT): TrimPlan {
  const key = `${request.itemId}|${request.edge}|${request.mode}|${request.deltaFrames}|${request.separately === true}`;
  const memo = lastPlan.get(doc);
  if (memo && memo.key === key && memo.context === context) return memo.plan;
  const plan = computePlan(doc, request, context);
  lastPlan.set(doc, { key, context, plan });
  return plan;
}

/** Whether a trim could act, and how far, for a clip on its own (no partners, no source length). */
export function probeTrim(doc: EditTimeline, itemId: string, edge: TrimEdge, mode: TrimMode, deltaFrames: number): TrimProbe {
  return planTrimDrag(doc, { itemId, edge, mode, deltaFrames }).probe;
}

/** T-020: the clips a ripple trim will shift, read from the engine's own result on the drag's own op list. */
export function rippleShift(doc: EditTimeline, itemId: string, edge: TrimEdge, mode: TrimMode, deltaFrames: number): RippleShift {
  return planTrimDrag(doc, { itemId, edge, mode, deltaFrames }).shift;
}

/** T-020: whether a move would be accepted. The engine neither overwrites nor pushes; it refuses an overlap. */
export function probeMove(doc: EditTimeline, itemId: string, toTrackId: string, toRecordIn: number): 'ok' | 'overlap' {
  const key = `${itemId}|${toTrackId}|${toRecordIn}`;
  const memo = lastMove.get(doc);
  if (memo && memo.key === key) return memo.answer;
  let answer: 'ok' | 'overlap' = 'ok';
  try {
    apply(doc, { type: 'move', itemId, toTrackId, toRecordIn } satisfies EditOp);
  } catch {
    answer = 'overlap';
  }
  lastMove.set(doc, { key, answer });
  return answer;
}

const lastMove = new WeakMap<EditTimeline, { key: string; answer: 'ok' | 'overlap' }>();

/** Which limit stopped a clamped trim; null when nothing was clamped. */
export function limitFor(mode: TrimMode, edge: TrimEdge, requested: number, effective: number): TrimLimit | null {
  if (requested === effective) return null;
  const shortOfPositive = requested > effective;
  switch (mode) {
    case 'slip':
      // The engine only stops a slip at frame 0; the end of the source is `stoppedBySource`'s stop, and a linked partner's.
      return shortOfPositive ? 'sourceEnd' : 'sourceStart';
    case 'ripple':
      if (edge === 'out') return shortOfPositive ? 'sourceEnd' : 'minLength';
      return shortOfPositive ? 'minLength' : 'sourceStart';
    default:
      return 'neighbour';
  }
}

export interface TrimReadoutValues {
  /** Whole frames the drag moves, always positive. */
  frames: number;
  timecode: string;
  /** Which way the thing that moves goes. */
  direction: 'earlier' | 'later';
  edge: TrimEdge;
}

/** The extra line a ripple adds: how many clips shift and by how much. Null when nothing shifts. */
export interface ShiftValues {
  count: number;
  timecode: string;
}

export interface TrimReadout {
  /** The catalogue key under `trimReadout.`. */
  key: TrimMode | 'none';
  values: TrimReadoutValues;
  limit: TrimLimit | null;
  shift: ShiftValues | null;
}

export interface DescribeTrimInput {
  mode: TrimMode;
  edge: TrimEdge;
  /** What the pointer asked for, in timeline frames. */
  requested: number;
  probe: TrimProbe;
  rate: Rational;
  shift?: RippleShift;
  /** The plan stopped this drag at the end of a source. */
  stoppedBySource?: boolean;
}

/** Words for one trim drag: the mode's effect on the effective delta, plus the limit if it was hit. */
export function describeTrim(input: DescribeTrimInput): TrimReadout {
  const { mode, edge, requested, probe, rate, shift, stoppedBySource = false } = input;
  if ('blocked' in probe) {
    return { key: 'none', values: valuesFor(0, edge, rate), limit: probe.blocked, shift: null };
  }
  const effective = probe.effective;
  const limit = stoppedBySource ? 'sourceEnd' : limitFor(mode, edge, requested, effective);
  return { key: effective === 0 ? 'none' : mode, values: valuesFor(effective, edge, rate), limit, shift: shift && shift.count > 0 ? { count: shift.count, timecode: framesToTimecode(shift.frames, rate) } : null };
}

function valuesFor(effective: number, edge: TrimEdge, rate: Rational): TrimReadoutValues {
  const frames = Math.abs(effective);
  // With the sign for every mode: a slip's delta IS the change to the source offset, so + shows later source frames.
  const later = effective > 0;
  return { frames, timecode: framesToTimecode(frames, rate), direction: later ? 'later' : 'earlier', edge };
}

/**
 * The slip or slide a body drag of `dxFrames` timeline frames means, as the same `trim` request the handle
 * route sends. Slip carries the picture with the pointer, so the source offset moves the other way; slide
 * lets the neighbour on the side it is heading toward absorb the move (the engine needs that neighbour).
 */
export function bodyTrimRequest(mode: 'slip' | 'slide', dxFrames: number, hasPrevious: boolean, hasFollowing: boolean): { edge: TrimEdge; deltaFrames: number } {
  if (mode === 'slip') return { edge: 'in', deltaFrames: dxFrames === 0 ? 0 : -dxFrames };
  const wantOut = dxFrames >= 0;
  const edge: TrimEdge = wantOut ? (hasFollowing || !hasPrevious ? 'out' : 'in') : hasPrevious || !hasFollowing ? 'in' : 'out';
  return { edge, deltaFrames: dxFrames };
}
