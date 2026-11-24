/**
 * R09.9: repeated takes found in the transcript. When a line is said more than once ("the quick
 * brown fox" three times in a row while the speaker tries again), the repeats are takes of one
 * line. They are grouped, the best is suggested (fewest stumbles and rough-timed words, the later
 * one on a tie, since a speaker usually gets it right last), and the others can be set aside on a
 * switched-off holding track instead of being thrown away.
 */

import type { EditOp } from '../engine';
import { compileRemoveSilence, type RecordSpan, type RemoveSilencePlan, type SourceMatcher } from '../silence/remove-silence';
import { DEFAULT_FILLER_WORDS, sourceWindowToRecordSpans, type TranscriptWord } from '../transcript/transcript';
import type { EditTimeline } from '../types';

export interface SpokenTake {
  id: string;
  sourceIndex: number;
  startSec: number;
  endSec: number;
  text: string;
  /** Words that are fillers or stumbles. */
  stumbles: number;
  /** Words whose timing was only estimated. */
  roughWords: number;
  score: number;
}

export interface TakeGroup {
  id: string;
  /** The line, as the best take says it. */
  text: string;
  takes: SpokenTake[];
  bestId: string;
}

const norm = (t: string): string => t.toLowerCase().replace(/[^\p{L}\p{N}']/gu, '');
const endsSentence = (t: string): boolean => /[.!?…]["')\]]?$/.test(t);

/** Splits words into sentences by punctuation and by pauses. */
export function splitSentences(words: readonly TranscriptWord[], pauseSec = 0.9): TranscriptWord[][] {
  const sorted = [...words].sort((a, b) => a.sourceIndex - b.sourceIndex || a.startSec - b.startSec);
  const out: TranscriptWord[][] = [];
  let current: TranscriptWord[] = [];
  for (const word of sorted) {
    const last = current[current.length - 1];
    if (last && (last.sourceIndex !== word.sourceIndex || word.startSec - last.endSec > pauseSec || endsSentence(last.text) || current.length >= 40)) {
      out.push(current);
      current = [];
    }
    current.push(word);
  }
  if (current.length > 0) out.push(current);
  return out;
}

/** Dice similarity of two sentences' word sets, 0..1. */
export function sentenceSimilarity(a: readonly string[], b: readonly string[]): number {
  if (a.length === 0 || b.length === 0) return 0;
  const setB = new Set(b);
  const setA = new Set(a);
  let shared = 0;
  for (const t of setA) if (setB.has(t)) shared++;
  return (2 * shared) / (setA.size + setB.size);
}

const FILLERS = new Set(DEFAULT_FILLER_WORDS.map(norm));

function makeTake(id: string, sentence: readonly TranscriptWord[]): SpokenTake {
  const tokens = sentence.map((w) => norm(w.text)).filter((t) => t.length > 0);
  const stumbles = tokens.filter((t, i) => FILLERS.has(t) || (i > 0 && tokens[i - 1] === t && t.length > 1)).length;
  const roughWords = sentence.filter((w) => w.estimated).length;
  return {
    id,
    sourceIndex: sentence[0].sourceIndex,
    startSec: sentence[0].startSec,
    endSec: sentence[sentence.length - 1].endSec,
    text: sentence.map((w) => w.text).join(' '),
    stumbles,
    roughWords,
    score: 100 - 12 * stumbles - 4 * roughWords + sentence[0].startSec * 1e-3,
  };
}

/** Groups sentences of three or more words that repeat each other (similarity 0.7 or more) into take groups. */
export function findRepeatedTakes(words: readonly TranscriptWord[], options: { minWords?: number; minSimilarity?: number } = {}): TakeGroup[] {
  const minWords = options.minWords ?? 3;
  const minSimilarity = options.minSimilarity ?? 0.7;
  const sentences = splitSentences(words).filter((s) => s.filter((w) => norm(w.text).length > 0).length >= minWords);
  const tokens = sentences.map((s) => s.map((w) => norm(w.text)).filter((t) => t.length > 0));
  const used = new Set<number>();
  const groups: TakeGroup[] = [];
  for (let i = 0; i < sentences.length; i++) {
    if (used.has(i)) continue;
    const members = [i];
    for (let j = i + 1; j < sentences.length; j++) {
      if (used.has(j)) continue;
      const ratio = Math.min(tokens[i].length, tokens[j].length) / Math.max(tokens[i].length, tokens[j].length);
      if (ratio >= 0.5 && sentenceSimilarity(tokens[i], tokens[j]) >= minSimilarity) members.push(j);
    }
    if (members.length < 2) continue;
    for (const m of members) used.add(m);
    const id = `r${groups.length}`;
    const takes = members.map((m, k) => makeTake(`${id}.${k}`, sentences[m]));
    const best = [...takes].sort((a, b) => b.score - a.score)[0];
    groups.push({ id, text: best.text, takes, bestId: best.id });
  }
  return groups;
}

/** The record spans a take occupies on the track (one per clip it touches). */
export function takeRecordSpans(doc: EditTimeline, trackId: string, take: SpokenTake, fps: number, matches?: SourceMatcher): RecordSpan[] {
  return sourceWindowToRecordSpans(doc, trackId, { startSec: take.startSec, endSec: take.endSec, sourceIndex: take.sourceIndex }, fps, matches);
}

export interface SetAsidePlan extends RemoveSilencePlan {
  /** Takes that are not on this track and so were not touched. */
  absent: number;
}

/**
 * Keeps the chosen take of each group and sets the others aside (a ripple cut that parks the pieces
 * on a switched-off "Other takes" track). The linked sound is cut with the picture.
 */
export function compileKeepTake(doc: EditTimeline, options: { trackId: string; groups: ReadonlyArray<{ group: TakeGroup; keepId: string }>; fps: number; matches?: SourceMatcher; newItemId: () => string }): SetAsidePlan {
  const spans: RecordSpan[] = [];
  let absent = 0;
  for (const { group, keepId } of options.groups) {
    for (const take of group.takes) {
      if (take.id === keepId) continue;
      const on = takeRecordSpans(doc, options.trackId, take, options.fps, options.matches);
      if (on.length === 0) absent++;
      spans.push(...on);
    }
  }
  const plan = compileRemoveSilence(doc, { trackId: options.trackId, spans, padFrames: 0, minFrames: 1, newItemId: options.newItemId, setAside: { name: 'Other takes', newTrackId: options.newItemId } });
  return { ...plan, absent };
}

export type { EditOp };
