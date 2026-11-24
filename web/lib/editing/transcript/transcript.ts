/**
 * Text-based editing (P6): the transcript as an editing surface.
 *
 * A `VideoAnalyze` artifact carries transcript segments (`t{n}`) and, when the ASR provider gave
 * them, words (`w{n}`), both in SOURCE seconds. This module turns that into something an editor can
 * act on — words grouped into paragraphs, a word range mapped onto the timeline's record frames,
 * filler candidates — and compiles the edits as programs of the engine's own primitive ops, so a
 * deletion in the transcript is a real ripple cut on the timeline and ONE undo step.
 *
 * ## The ripple engine, not a text widget
 *
 * Deleting words never edits text. The selected words name a source window; the window is mapped
 * through each media clip on the chosen track exactly as a silence span is
 * (`mapSourceSilenceToRecord`), and the record spans are cut with `compileRemoveSilence`'s
 * blade/blade/extract program. Exact frame ranges, rightmost first, one history entry. The
 * transcript panel re-reads the artifact against the NEW document, so words whose source frames
 * are no longer on the timeline simply stop being offered.
 *
 * ## Words without word timing
 *
 * A transcript may carry segments only. Then every segment is split into words whose timing is
 * ESTIMATED by character share of the segment's span. Those words are marked `estimated: true` and
 * the panel says so: a cut on an estimated word lands within the segment, not on the word's own
 * measured edge.
 */

import { apply, type EditOp, type Insert } from '../engine';
import { compileRemoveSilence, mapSourceSilenceToRecord, normalizeSpans, relinkOps, type RecordSpan, type RemoveSilencePlan, type SourceMatcher } from '../silence/remove-silence';
import type { EditTimeline, Item, MediaClip } from '../types';

export interface TranscriptWord {
  id: string;
  startSec: number;
  endSec: number;
  text: string;
  sourceIndex: number;
  /** The segment the word belongs to (by time), when one does. */
  segmentId: string | null;
  /** True when the timing was derived from the segment, not measured by the recogniser. */
  estimated: boolean;
}

export interface TranscriptSegment {
  id: string;
  startSec: number;
  endSec: number;
  text: string;
  sourceIndex: number;
}

export interface TranscriptParagraph {
  id: string;
  startSec: number;
  endSec: number;
  sourceIndex: number;
  segments: TranscriptSegment[];
  words: TranscriptWord[];
}

export interface Transcript {
  segments: TranscriptSegment[];
  words: TranscriptWord[];
  paragraphs: TranscriptParagraph[];
  /** True when at least one word's timing was estimated from its segment. */
  hasEstimatedWords: boolean;
}

const num = (e: Record<string, unknown>, a: string, b: string): number | null => {
  const v = e[a] ?? e[b];
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
};
const str = (e: Record<string, unknown>, a: string, b: string): string | null => {
  const v = e[a] ?? e[b];
  return typeof v === 'string' ? v : null;
};

function readSegments(artifact: Record<string, unknown>): TranscriptSegment[] {
  const raw = artifact.segments ?? artifact.Segments;
  if (!Array.isArray(raw)) return [];
  const out: TranscriptSegment[] = [];
  raw.forEach((entry, index) => {
    if (typeof entry !== 'object' || entry === null) return;
    const e = entry as Record<string, unknown>;
    const startSec = num(e, 'startSec', 'StartSec');
    const endSec = num(e, 'endSec', 'EndSec');
    const text = (str(e, 'text', 'Text') ?? '').trim();
    if (startSec === null || endSec === null || endSec <= startSec || text.length === 0) return;
    out.push({ id: str(e, 'id', 'Id') ?? `t${index}`, startSec, endSec, text, sourceIndex: num(e, 'sourceIndex', 'SourceIndex') ?? 0 });
  });
  return out.sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);
}

function readWords(artifact: Record<string, unknown>): Omit<TranscriptWord, 'segmentId'>[] {
  const raw = artifact.words ?? artifact.Words;
  if (!Array.isArray(raw)) return [];
  const out: Omit<TranscriptWord, 'segmentId'>[] = [];
  raw.forEach((entry, index) => {
    if (typeof entry !== 'object' || entry === null) return;
    const e = entry as Record<string, unknown>;
    const startSec = num(e, 'startSec', 'StartSec');
    const endSec = num(e, 'endSec', 'EndSec');
    const text = (str(e, 'text', 'Text') ?? '').trim();
    if (startSec === null || endSec === null || endSec < startSec || text.length === 0) return;
    out.push({ id: str(e, 'id', 'Id') ?? `w${index}`, startSec, endSec, text, sourceIndex: num(e, 'sourceIndex', 'SourceIndex') ?? 0, estimated: (e.estimated ?? e.Estimated) === true });
  });
  return out.sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);
}

/** Splits a segment into words with timing estimated by character share. */
export function estimateWords(segment: TranscriptSegment): TranscriptWord[] {
  const tokens = segment.text.split(/\s+/).filter((t) => t.length > 0);
  const totalChars = tokens.reduce((n, t) => n + t.length, 0) + Math.max(0, tokens.length - 1);
  const span = segment.endSec - segment.startSec;
  let cursor = 0;
  return tokens.map((text, index) => {
    const share = totalChars > 0 ? (text.length + (index < tokens.length - 1 ? 1 : 0)) / totalChars : 1 / tokens.length;
    const startSec = segment.startSec + span * cursor;
    cursor += share;
    const endSec = index === tokens.length - 1 ? segment.endSec : segment.startSec + span * cursor;
    return { id: `${segment.id}.${index}`, startSec, endSec, text, sourceIndex: segment.sourceIndex, segmentId: segment.id, estimated: true };
  });
}

/** Groups consecutive segments of one source separated by less than `gapSec` into paragraphs. */
export function groupParagraphs(segments: readonly TranscriptSegment[], words: readonly TranscriptWord[], gapSec = 1.5): TranscriptParagraph[] {
  const paragraphs: TranscriptParagraph[] = [];
  let current: TranscriptParagraph | null = null;
  for (const segment of segments) {
    const breaks = current === null || current.sourceIndex !== segment.sourceIndex || segment.startSec - current.endSec >= gapSec;
    if (breaks) {
      current = { id: `p${paragraphs.length}`, startSec: segment.startSec, endSec: segment.endSec, sourceIndex: segment.sourceIndex, segments: [], words: [] };
      paragraphs.push(current);
    }
    const paragraph = current as TranscriptParagraph;
    paragraph.segments.push(segment);
    paragraph.endSec = Math.max(paragraph.endSec, segment.endSec);
    for (const word of words) if (word.segmentId === segment.id) paragraph.words.push(word);
  }
  // Words that belong to no segment (a word-only transcript) form paragraphs of their own by gap.
  const orphans = words.filter((w) => w.segmentId === null);
  let orphanParagraph: TranscriptParagraph | null = null;
  for (const word of orphans) {
    const breaks = orphanParagraph === null || orphanParagraph.sourceIndex !== word.sourceIndex || word.startSec - orphanParagraph.endSec >= gapSec;
    if (breaks) {
      orphanParagraph = { id: `p${paragraphs.length}`, startSec: word.startSec, endSec: word.endSec, sourceIndex: word.sourceIndex, segments: [], words: [] };
      paragraphs.push(orphanParagraph);
    }
    const paragraph = orphanParagraph as TranscriptParagraph;
    paragraph.words.push(word);
    paragraph.endSec = Math.max(paragraph.endSec, word.endSec);
  }
  return paragraphs.sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);
}

/** Reads a VideoAnalyze artifact defensively; anything unreadable is simply not offered. */
export function readTranscript(artifact: unknown, gapSec = 1.5): Transcript {
  if (typeof artifact !== 'object' || artifact === null) return { segments: [], words: [], paragraphs: [], hasEstimatedWords: false };
  const a = artifact as Record<string, unknown>;
  const segments = readSegments(a);
  const measured = readWords(a);

  const words: TranscriptWord[] = [];
  let hasEstimatedWords = false;
  for (const segment of segments) {
    const inside = measured.filter((w) => w.sourceIndex === segment.sourceIndex && w.startSec >= segment.startSec - 1e-6 && w.startSec < segment.endSec + 1e-6);
    if (inside.length > 0) {
      for (const w of inside) words.push({ ...w, segmentId: segment.id });
    } else {
      hasEstimatedWords = true;
      words.push(...estimateWords(segment));
    }
  }
  const covered = new Set(words.map((w) => w.id));
  for (const w of measured) if (!covered.has(w.id) && !segments.some((s) => s.sourceIndex === w.sourceIndex && w.startSec >= s.startSec - 1e-6 && w.startSec < s.endSec + 1e-6)) words.push({ ...w, segmentId: null });
  words.sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);

  return { segments, words, paragraphs: groupParagraphs(segments, words, gapSec), hasEstimatedWords: hasEstimatedWords || words.some((w) => w.estimated) };
}

// ─────────────────────────────────────────────────────────────────────────────
// Mapping words onto the timeline
// ─────────────────────────────────────────────────────────────────────────────

/** The source window a contiguous word selection covers (first start to last end), or null. */
export function wordWindow(words: readonly TranscriptWord[]): { startSec: number; endSec: number } | null {
  if (words.length === 0) return null;
  let startSec = Number.POSITIVE_INFINITY;
  let endSec = Number.NEGATIVE_INFINITY;
  for (const w of words) {
    startSec = Math.min(startSec, w.startSec);
    endSec = Math.max(endSec, w.endSec);
  }
  return endSec > startSec ? { startSec, endSec } : { startSec, endSec: startSec + 1e-3 };
}

/**
 * Every record span on `trackId` the source window lands in (one per clip it touches), merged.
 * With `matches`, only clips showing the window's source (`sourceIndex`, default 0) are considered.
 */
export function sourceWindowToRecordSpans(doc: EditTimeline, trackId: string, window: { startSec: number; endSec: number; sourceIndex?: number }, fps: number, matches?: SourceMatcher): RecordSpan[] {
  const track = doc.tracks.find((t) => t.id === trackId);
  if (!track) return [];
  const spans: RecordSpan[] = [];
  for (const item of track.items) {
    if (item.type !== 'mediaClip') continue;
    const span = mapSourceSilenceToRecord(item, { id: '', ...window }, fps, matches);
    if (span) spans.push(span);
  }
  return normalizeSpans(spans);
}

/** The longest pause two selected words may have between them and still be cut as one piece. */
export const RUN_GAP_SEC = 0.35;

/** The record spans a word selection occupies on the track — what a ripple cut removes. */
export function wordsToRecordSpans(doc: EditTimeline, trackId: string, words: readonly TranscriptWord[], fps: number, matches?: SourceMatcher): RecordSpan[] {
  // Each run of consecutive words forms one window — the pause BETWEEN two selected words goes
  // with them, since a viewer deleting "the the" wants the stumble gone, not two slivers around a
  // 100 ms breath — while a selection with a real hole (a pause over `RUN_GAP_SEC`) cuts two pieces.
  // Words of different sources never share a window: their seconds are in different files.
  const sorted = [...words].sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);
  const windows: { startSec: number; endSec: number; sourceIndex: number }[] = [];
  for (const w of sorted) {
    const last = windows[windows.length - 1];
    if (last && last.sourceIndex === w.sourceIndex && w.startSec <= last.endSec + RUN_GAP_SEC) last.endSec = Math.max(last.endSec, w.endSec);
    else windows.push({ startSec: w.startSec, endSec: w.endSec, sourceIndex: w.sourceIndex });
  }
  return normalizeSpans(windows.flatMap((window) => sourceWindowToRecordSpans(doc, trackId, window, fps, matches)));
}

/** True when some media clip on the track still shows this word's source time. */
export function wordIsOnTrack(doc: EditTimeline, trackId: string, word: TranscriptWord, fps: number, matches?: SourceMatcher): boolean {
  return sourceWindowToRecordSpans(doc, trackId, { startSec: word.startSec, endSec: Math.max(word.endSec, word.startSec + 1 / fps), sourceIndex: word.sourceIndex }, fps, matches).length > 0;
}

/** The record frame of a word's start on the track (the first clip showing it), or null. */
export function wordRecordFrame(doc: EditTimeline, trackId: string, word: TranscriptWord, fps: number, matches?: SourceMatcher): number | null {
  const spans = sourceWindowToRecordSpans(doc, trackId, { startSec: word.startSec, endSec: Math.max(word.endSec, word.startSec + 1 / fps), sourceIndex: word.sourceIndex }, fps, matches);
  return spans.length > 0 ? spans[0].startFrame : null;
}

export interface RippleCutOptions {
  trackId: string;
  spans: readonly RecordSpan[];
  newItemId: () => string;
}

/**
 * The ripple cut: `compileRemoveSilence`'s blade/blade/extract program, with no padding and no
 * minimum length — a word is never too short to delete.
 */
export function compileRippleCut(doc: EditTimeline, options: RippleCutOptions): RemoveSilencePlan {
  return compileRemoveSilence(doc, { trackId: options.trackId, spans: options.spans, padFrames: 0, minFrames: 1, newItemId: options.newItemId });
}

// ─────────────────────────────────────────────────────────────────────────────
// Filler words
// ─────────────────────────────────────────────────────────────────────────────

/** The default filler vocabulary. Deliberately short: a word like "like" is too often real speech. */
export const DEFAULT_FILLER_WORDS: readonly string[] = ['um', 'uh', 'uhm', 'umm', 'erm', 'er', 'ah', 'hmm', 'mm', 'mhm'];

export interface FillerCandidate {
  id: string;
  kind: 'filler' | 'repeat';
  words: TranscriptWord[];
  /** What the viewer reads: the filler, or the repeated word with its repeat. */
  text: string;
  startSec: number;
  endSec: number;
}

const normalizeToken = (text: string): string => text.toLowerCase().replace(/[^\p{L}\p{N}']/gu, '');

/**
 * Finds filler words and immediate word repeats ("the the"). Each candidate is independently
 * acceptable or rejectable by the user; nothing here cuts anything.
 */
export function findFillerCandidates(words: readonly TranscriptWord[], options: { fillers?: readonly string[]; includeRepeats?: boolean } = {}): FillerCandidate[] {
  const fillers = new Set((options.fillers ?? DEFAULT_FILLER_WORDS).map(normalizeToken));
  const includeRepeats = options.includeRepeats ?? true;
  const out: FillerCandidate[] = [];
  const sorted = [...words].sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);
  for (let i = 0; i < sorted.length; i++) {
    const word = sorted[i];
    const token = normalizeToken(word.text);
    if (token.length === 0) continue;
    if (fillers.has(token)) {
      out.push({ id: `filler:${word.id}`, kind: 'filler', words: [word], text: word.text, startSec: word.startSec, endSec: word.endSec });
      continue;
    }
    if (includeRepeats && i > 0) {
      const previous = sorted[i - 1];
      if (previous.sourceIndex === word.sourceIndex && normalizeToken(previous.text) === token && token.length > 1 && word.startSec - previous.endSec < 0.6) {
        // The FIRST utterance is the one removed, so the sentence keeps its natural ending word.
        out.push({ id: `repeat:${previous.id}`, kind: 'repeat', words: [previous], text: `${previous.text} ${word.text}`, startSec: previous.startSec, endSec: previous.endSec });
      }
    }
  }
  return out;
}

// ─────────────────────────────────────────────────────────────────────────────
// Paragraph reorder
// ─────────────────────────────────────────────────────────────────────────────

export interface MoveSpanOptions {
  trackId: string;
  /** The record span to move, `[startFrame, endFrame)`. */
  from: RecordSpan;
  /** Where its start lands: a record frame outside `from` (a frame inside it is refused). */
  toFrame: number;
  newItemId: () => string;
}

export interface MoveSpanPlan {
  ops: EditOp[];
  preview: EditTimeline;
  /** Why nothing was compiled, when `ops` is empty. */
  refusal: string | null;
  /** The record frame the moved material starts at after the move. */
  landedAt: number | null;
}

const clipsOn = (doc: EditTimeline, trackId: string): MediaClip[] =>
  (doc.tracks.find((t) => t.id === trackId)?.items ?? []).filter((i): i is MediaClip => i.type === 'mediaClip');

/**
 * Moves the material in a record span to another place on the same track, as a program of blades,
 * extracts and inserts — the "drag a paragraph" edit. The pieces are bladed free, ripple-extracted
 * (rightmost first), then inserted in order at the target edit point, which is re-read from the
 * live document after the extracts so the ripple is accounted for exactly. Only media clips move;
 * a title or caption inside the span stays where it is.
 */
export function compileMoveSpan(doc: EditTimeline, options: MoveSpanOptions): MoveSpanPlan {
  const { trackId, from, toFrame } = options;
  if (from.endFrame <= from.startFrame) return { ops: [], preview: doc, refusal: 'Nothing to move.', landedAt: null };
  if (toFrame > from.startFrame && toFrame < from.endFrame) return { ops: [], preview: doc, refusal: 'The target is inside the material being moved.', landedAt: null };
  if (toFrame === from.startFrame || toFrame === from.endFrame) return { ops: [], preview: doc, refusal: 'The material is already there.', landedAt: null };
  if (!doc.tracks.some((t) => t.id === trackId)) return { ops: [], preview: doc, refusal: 'Unknown track.', landedAt: null };

  const ops: EditOp[] = [];
  let current = doc;
  const run = (op: EditOp) => {
    current = apply(current, op).doc;
    ops.push(op);
  };

  // 1. Make every needed boundary an edit point: the span's edges and the target.
  const bladeAt = (frame: number) => {
    const clip = clipsOn(current, trackId).find((c) => c.recordIn < frame && frame < c.recordOut);
    if (clip) run({ type: 'blade', itemId: clip.id, atFrame: frame, newItemId: options.newItemId() });
  };
  bladeAt(from.endFrame);
  bladeAt(from.startFrame);
  bladeAt(toFrame);

  // 2. The anchor the pieces land before (null = the end of the track), read as an ITEM so the
  //    extracts' ripple cannot invalidate it.
  const anchor = (current.tracks.find((t) => t.id === trackId) as { items: Item[] }).items.find((i) => i.recordIn >= toFrame && !(i.recordIn >= from.startFrame && i.recordOut <= from.endFrame));
  const anchorId = anchor ? anchor.id : null;

  // 3. Extract the pieces, rightmost first.
  const pieces = clipsOn(current, trackId)
    .filter((c) => c.recordIn >= from.startFrame && c.recordOut <= from.endFrame)
    .sort((a, b) => a.recordIn - b.recordIn);
  if (pieces.length === 0) return { ops: [], preview: doc, refusal: 'No media clip lies inside that span on this track.', landedAt: null };
  for (const piece of [...pieces].reverse()) run({ type: 'extract', itemId: piece.id });

  // 4. Insert them, in order, at the anchor's current start (or the track's end).
  const liveItems = () => (current.tracks.find((t) => t.id === trackId) as { items: Item[] }).items;
  let at: number;
  if (anchorId) {
    const anchorNow = liveItems().find((i) => i.id === anchorId);
    at = anchorNow ? anchorNow.recordIn : liveItems().reduce((end, i) => Math.max(end, i.recordOut), 0);
  } else {
    at = liveItems().reduce((end, i) => Math.max(end, i.recordOut), 0);
  }
  const landedAt = at;
  for (const piece of pieces) {
    const duration = piece.recordOut - piece.recordIn;
    const item: MediaClip = { ...piece, recordIn: at, recordOut: at + duration };
    const index = liveItems().filter((i) => i.recordIn < at).length;
    const insert: Insert = { type: 'insert', trackId, index, item };
    run(insert);
    at += duration;
  }

  // Pieces of linked clips no longer line up with their partners: re-pair what still does, unlink the rest.
  const linked = new Set<string>();
  for (const t of doc.tracks) for (const i of t.items) if (i.type === 'mediaClip' && typeof i.linkId === 'string') linked.add(i.linkId);
  for (const op of relinkOps(current, linked, options.newItemId)) run(op);

  return { ops, preview: current, refusal: null, landedAt };
}
