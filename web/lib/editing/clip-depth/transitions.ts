/**
 * R07.4: transitions at the seam. The closed catalogue stays at 44 types; the picker offers eight
 * labelled words and the rest under "More". These helpers name the words, say how faithful the preview
 * is, work out the handles a transition needs and build the ops for one cut or for every cut. All ops
 * are the existing addTransition / removeTransition / trim / setProperty; the workspace dispatches
 * them as one batch.
 */
import { apply, TRANSITION_TYPES, type EditOp } from '../engine';
import { linkedOpsForTrim, type LinkOptions } from '../link';
import type { EditTimeline, MediaClip, Track, Transition } from '../types';

export interface TransitionWord {
  type: string;
  label: string;
}

/** The eight curated words, in picker order. */
export const TRANSITION_WORDS: readonly TransitionWord[] = [
  { type: 'dissolve', label: 'Dissolve' },
  { type: 'fadeblack', label: 'Dip to black' },
  { type: 'fadewhite', label: 'Dip to white' },
  { type: 'wipeleft', label: 'Wipe left' },
  { type: 'wiperight', label: 'Wipe right' },
  { type: 'slideleft', label: 'Slide left' },
  { type: 'slideright', label: 'Slide right' },
  { type: 'circleopen', label: 'Circle open' },
];

const WORD_PARTS: Readonly<Record<string, string>> = {
  tl: 'top left',
  tr: 'top right',
  bl: 'bottom left',
  br: 'bottom right',
};

/** A plain name for any of the 44 catalogue types (`wipetl` becomes "Wipe top left"). */
export function transitionLabel(type: string): string {
  const word = TRANSITION_WORDS.find((w) => w.type === type);
  if (word) return word.label;
  const match = /^(fade|wipe|slide|smooth|diag|circle|rect|vert|horz|hl|hr|vu|vd|squeeze)(.*)$/.exec(type);
  if (!match) return type.charAt(0).toUpperCase() + type.slice(1);
  const base: Record<string, string> = { fade: 'Fade', wipe: 'Wipe', slide: 'Slide', smooth: 'Smooth', diag: 'Diagonal', circle: 'Circle', rect: 'Rectangle', vert: 'Vertical', horz: 'Horizontal', hl: 'Slice left', hr: 'Slice right', vu: 'Slice up', vd: 'Slice down', squeeze: 'Squeeze' };
  const rest = match[2];
  const tail = WORD_PARTS[rest] ?? rest;
  return tail ? `${base[match[1]]} ${tail}` : base[match[1]];
}

/** Every catalogue type that is not one of the eight words, in catalogue order. */
export const MORE_TRANSITIONS: readonly TransitionWord[] = TRANSITION_TYPES.filter((t) => !TRANSITION_WORDS.some((w) => w.type === t)).map((type) => ({ type, label: transitionLabel(type) }));

export type TransitionParity = 'faithful' | 'approximate' | 'render-required';

/** How close the preview is to the export for a transition type; mirrors the preview's own classifier. */
export function transitionParity(type: string): { parity: TransitionParity; line: string } {
  if (type === 'dissolve' || type === 'fade') return { parity: 'faithful', line: 'Faithful: the preview matches the export.' };
  return { parity: 'approximate', line: `Approximate: the preview shows a dissolve; the export applies the real ${transitionLabel(type).toLowerCase()}.` };
}

/** The transition lengths the picker offers, in seconds. */
export const TRANSITION_SECONDS: readonly number[] = [0.25, 0.5, 1, 1.5];

export interface Seam {
  trackId: string;
  outgoing: MediaClip;
  incoming: MediaClip;
}

/** Every cut between two adjacent media clips on one track, with no transition yet. */
export function findSeams(doc: EditTimeline): Seam[] {
  const seams: Seam[] = [];
  for (const track of doc.tracks) {
    for (let i = 0; i + 1 < track.items.length; i++) {
      const a = track.items[i];
      const b = track.items[i + 1];
      if (a.type === 'mediaClip' && b.type === 'mediaClip' && a.recordOut === b.recordIn) seams.push({ trackId: track.id, outgoing: a, incoming: b });
    }
  }
  return seams;
}

/** The transitions already in the document, with the clips either side (for the seam's edit / remove menu). */
export function findTransitions(doc: EditTimeline): { trackId: string; transition: Transition; outgoing: MediaClip; incoming: MediaClip }[] {
  const out: { trackId: string; transition: Transition; outgoing: MediaClip; incoming: MediaClip }[] = [];
  for (const track of doc.tracks) {
    for (let i = 1; i + 1 < track.items.length; i++) {
      const t = track.items[i];
      const a = track.items[i - 1];
      const b = track.items[i + 1];
      if (t.type === 'transition' && a.type === 'mediaClip' && b.type === 'mediaClip') out.push({ trackId: track.id, transition: t, outgoing: a, incoming: b });
    }
  }
  return out;
}

/** The record frames of source the incoming clip has before its start (what a transition needs to reach back). */
export function incomingHandleFrames(incoming: MediaClip): number {
  if (incoming.reverse === true) return Number.POSITIVE_INFINITY;
  return Math.floor((incoming.sourceIn ?? 0) / (incoming.playbackRate ?? 1));
}

export interface TransitionFit {
  framesBefore: number;
  framesAfter: number;
  /** Frames of handle still missing on the incoming clip (0 = it fits). */
  missingHandle: number;
  /** The longest transition (frames) the two clips can hold without eating either one. */
  maxFrames: number;
}

/** Splits a length half and half around the cut and says whether the clips can hold it. */
export function fitTransition(seam: Pick<Seam, 'outgoing' | 'incoming'>, frames: number): TransitionFit {
  const { outgoing, incoming } = seam;
  const outLen = outgoing.recordOut - outgoing.recordIn;
  const inLen = incoming.recordOut - incoming.recordIn;
  const handle = incomingHandleFrames(incoming);
  const cap = Math.max(0, Math.min(outLen - 1, inLen - 1) * 2);
  const wanted = Math.max(1, Math.min(frames, cap));
  const framesBefore = Math.floor(wanted / 2);
  const framesAfter = wanted - framesBefore;
  return { framesBefore, framesAfter, missingHandle: Math.max(0, framesBefore - (Number.isFinite(handle) ? handle : Number.POSITIVE_INFINITY)), maxFrames: cap };
}

export interface TransitionRequest {
  transitionType: string;
  frames: number;
  easing?: string;
}

/** The op that puts one transition on one cut, or null with the plain reason it cannot. */
export function addTransitionOp(seam: Seam, request: TransitionRequest, newId: () => string): { op: EditOp } | { reason: string } {
  const fit = fitTransition(seam, request.frames);
  if (fit.maxFrames < 1) return { reason: 'One of the two clips is too short to hold a transition.' };
  if (fit.missingHandle > 0) return { reason: 'The next clip has no spare footage before its start for a blend. Use Make room to trim its start.' };
  return {
    op: {
      type: 'addTransition',
      afterItemId: seam.outgoing.id,
      transition: { id: newId(), type: 'transition', name: transitionLabel(request.transitionType), transitionType: request.transitionType, easing: request.easing ?? 'linear', enabled: true, provenance: null },
      framesBefore: fit.framesBefore,
      framesAfter: fit.framesAfter,
    },
  };
}

/**
 * "Apply to all cuts": one addTransition per cut that can hold the transition, in one batch.
 * Cuts that cannot hold it (no spare footage, clip too short) are counted in `skipped`, never silently dropped.
 * Cuts are processed right to left, so an earlier cut's trim never moves a later cut's clips out from under its op.
 */
export function addTransitionToAllOps(doc: EditTimeline, request: TransitionRequest, newId: () => string): { ops: EditOp[]; added: number; skipped: number } {
  const seams = findSeams(doc).reverse();
  const ops: EditOp[] = [];
  let skipped = 0;
  for (const seam of seams) {
    const result = addTransitionOp(seam, request, newId);
    if ('op' in result) ops.push(result.op);
    else skipped += 1;
  }
  return { ops, added: ops.length, skipped };
}

/**
 * "Make room": gives the incoming clip the spare footage a transition of `frames` needs, by trimming
 * its start (a ripple trim, so what follows moves up and nothing is left behind; a linked sound
 * follows). Returns the ops, or the plain reason it cannot be done. One batch, one undo.
 */
export function makeRoomOps(doc: EditTimeline, seam: Seam, frames: number, options?: LinkOptions): { ops: EditOp[]; trimmedFrames: number } | { reason: string } {
  const fit = fitTransition(seam, frames);
  if (fit.missingHandle <= 0) return { reason: 'This cut already has room for that transition.' };
  const { incoming } = seam;
  if (incoming.hold === true) return { reason: 'A frozen clip has no footage before its start to make room from.' };
  // Trim the head until the source frames before sourceIn cover the half that reaches back.
  const length = incoming.recordOut - incoming.recordIn;
  const rate = incoming.playbackRate ?? 1;
  const sourceIn = incoming.sourceIn ?? 0;
  let trim = 1;
  while (Math.floor((sourceIn + Math.round(trim * rate)) / rate) < fit.framesBefore && trim < length) trim += 1;
  // After the head trim the clip must still hold the transition's second half and at least a frame.
  if (trim + fit.framesAfter >= length) {
    return { reason: `The next clip is only ${length} frames long; trimming ${trim} off its start would leave too little. Pick a shorter transition.` };
  }
  const ops = linkedOpsForTrim(doc, incoming.id, 'in', 'ripple', trim, options);
  if (ops.length === 0) return { reason: 'The next clip cannot be trimmed from its start here.' };
  // The group may trim by less than asked (a partner with less room); the real handle is checked on the result.
  try {
    let probe = doc;
    for (const op of ops) probe = apply(probe, op).doc;
    const after = locateClip(probe, incoming.id);
    if (!after || Math.floor((after.sourceIn ?? 0) / (after.playbackRate ?? 1)) < fit.framesBefore) {
      return { reason: 'The next clip does not have enough footage to trim that far.' };
    }
  } catch {
    return { reason: 'The next clip cannot be trimmed that far.' };
  }
  return { ops, trimmedFrames: trim };
}

function locateClip(doc: EditTimeline, id: string): MediaClip | null {
  for (const track of doc.tracks as readonly Track[]) {
    const found = track.items.find((i) => i.id === id);
    if (found && found.type === 'mediaClip') return found;
  }
  return null;
}
