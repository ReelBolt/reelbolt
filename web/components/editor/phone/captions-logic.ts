import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { buildCues, compileAddCaptionAt, compileGenerateCaptions, defaultCaptionStyle, listCaptions, type TimedWord } from '@/lib/editing/subtitles/subtitles';
import { compileRippleCut, sourceWindowToRecordSpans, wordsToRecordSpans, type TranscriptWord } from '@/lib/editing/transcript/transcript';
import type { SourceMatcher } from '@/lib/editing/silence/remove-silence';

export interface CaptionRow {
  id: string;
  text: string;
  startFrame: number;
  endFrame: number;
  timecode: string;
  enabled: boolean;
  mergeable: boolean;
  splittable: boolean;
}

/** The caption list the sheet shows (the Subtitles panel's data), with what each row can do at this playhead. */
export function captionRows(doc: EditTimeline, playheadFrame: number): CaptionRow[] {
  const all = listCaptions(doc);
  return all.map(({ caption, track }, index) => {
    const next = all[index + 1];
    return {
      id: caption.id,
      text: caption.text,
      startFrame: caption.recordIn,
      endFrame: caption.recordOut,
      timecode: framesToTimecode(caption.recordIn, doc.settings.rate),
      enabled: caption.enabled !== false,
      mergeable: next !== undefined && next.track.id === track.id && track.items.indexOf(next.caption) === track.items.indexOf(caption) + 1,
      splittable: playheadFrame > caption.recordIn && playheadFrame < caption.recordOut && caption.text.split(/\s+/).filter(Boolean).length >= 2,
    };
  });
}

/** Delete a caption: lift it and leave the gap, so the captions after it keep their timing. */
export const compileDeleteCaption = (captionId: string): EditOp[] => [{ type: 'lift', itemId: captionId }];

export function compileNewCaption(doc: EditTimeline, playheadFrame: number, text: string, newItemId: () => string, newTrackId: () => string): { ops: EditOp[]; refusal: string | null } {
  const style = defaultCaptionStyle(doc.settings.width, doc.settings.height);
  const plan = compileAddCaptionAt(doc, { atFrame: playheadFrame, text, trackId: null, language: 'en', look: style.look, position: style.position, newItemId, newTrackId });
  return { ops: plan.ops, refusal: plan.refusal };
}

/** Captions from the transcript with the Subtitles panel's defaults. */
export function planGenerate(
  doc: EditTimeline,
  words: readonly TranscriptWord[],
  trackId: string,
  fps: number,
  matches: SourceMatcher,
  newItemId: () => string,
  newTrackId: () => string,
): { ops: EditOp[]; message: string } {
  const timed: TimedWord[] = [];
  for (const w of words) {
    const spans = sourceWindowToRecordSpans(doc, trackId, { startSec: w.startSec, endSec: Math.max(w.endSec, w.startSec + 1 / fps), sourceIndex: w.sourceIndex }, fps, matches);
    if (spans.length > 0) timed.push({ startFrame: spans[0].startFrame, endFrame: spans[0].endFrame, text: w.text });
  }
  const cues = buildCues(timed, fps, { maxCharsPerLine: 42, maxLines: 2 });
  const style = defaultCaptionStyle(doc.settings.width, doc.settings.height);
  const plan = compileGenerateCaptions(doc, { cues, trackId: null, trackName: 'Subtitles', language: 'en', look: style.look, position: style.position, newItemId, newTrackId });
  if (plan.ops.length === 0) {
    return { ops: [], message: plan.skipped > 0 ? 'The captions are already there.' : 'No spoken words were found on this track.' };
  }
  return { ops: plan.ops, message: `${plan.inserted} caption${plan.inserted === 1 ? '' : 's'} added.` };
}

/** A selection of words as indices into the list on screen (both ends included). */
export interface WordRange {
  start: number;
  end: number;
}

export const orderedRange = (a: number, b: number): WordRange => (a <= b ? { start: a, end: b } : { start: b, end: a });

/** Move one end of the range by a word. The start never passes the end and both stay on the list. */
export function stepRange(range: WordRange, edge: 'start' | 'end', direction: -1 | 1, count: number): WordRange {
  const clamp = (n: number) => Math.max(0, Math.min(count - 1, n));
  if (edge === 'start') return { start: Math.min(range.end, clamp(range.start + direction)), end: range.end };
  return { start: range.start, end: Math.max(range.start, clamp(range.end + direction)) };
}

/** Drag a handle onto a word: the same rule as `stepRange`, for any distance. */
export function moveEdgeTo(range: WordRange, edge: 'start' | 'end', index: number, count: number): WordRange {
  const clamp = (n: number) => Math.max(0, Math.min(count - 1, n));
  const i = clamp(index);
  return edge === 'start' ? { start: Math.min(i, range.end), end: range.end } : { start: range.start, end: Math.max(i, range.start) };
}

export interface WordDeletePlan {
  ops: EditOp[];
  label: string;
  cuts: number;
  removedFrames: number;
  /** Words whose timing was only estimated: the cut may clip a neighbour, so the person is asked first. */
  estimated: number;
}

/** Delete the selected words with the transcript panel's own ripple cut (one undo step). */
export function planWordDelete(doc: EditTimeline, trackId: string, selected: readonly TranscriptWord[], fps: number, matches: SourceMatcher, newItemId: () => string): WordDeletePlan {
  const spans = wordsToRecordSpans(doc, trackId, [...selected], fps, matches);
  const plan = compileRippleCut(doc, { trackId, spans, newItemId });
  return {
    ops: plan.ops,
    label: `Delete ${selected.length} word${selected.length === 1 ? '' : 's'}`,
    cuts: plan.cuts,
    removedFrames: plan.removedFrames,
    estimated: selected.filter((w) => w.estimated).length,
  };
}

/** Panels that need the desktop's room (take comparison): not offered under the Captions sheet's "More tools" on a phone (x1). */
export const PHONE_HIDDEN_TOOL_TABS: readonly string[] = ['takes', 'compare'];

/** The tabs a phone shows under "More tools": the left panels without the desktop-only ones. */
export function phoneMoreToolTabs<T extends { value: string }>(tabs: readonly T[]): T[] {
  return tabs.filter((t) => !PHONE_HIDDEN_TOOL_TABS.includes(t.value));
}
