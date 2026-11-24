import { useMemo } from 'react';
import type { EditTimeline, Track } from '@/lib/editing/types';
import { readTranscript, wordRecordFrame, type TranscriptWord } from '@/lib/editing/transcript/transcript';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import type { SourceMatcher } from '@/lib/editing/silence/remove-silence';

export interface TranscriptWords {
  isLoading: boolean;
  error: Error | undefined;
  fps: number;
  matches: SourceMatcher;
  mediaTracks: Track[];
  /** Every transcript word. */
  all: TranscriptWord[];
  /** The words that are on the given track, in order: the list the word picker shows. */
  onTrack: (trackId: string | null) => TranscriptWord[];
}

/** The transcript the timeline was cut from (or a per-file analysis), read the way the desktop panels read it. */
export function useTranscriptWords(doc: EditTimeline, projectId: string, seedStepResultId: string | null): TranscriptWords {
  const { data: analysis, isLoading, error } = useTimelineAnalysis(projectId, seedStepResultId, doc);
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;
  const matches = useMemo(() => sourceMatcher(analysis, doc), [analysis, doc]);
  const all = useMemo(() => readTranscript(analysis?.artifact).words, [analysis]);
  const mediaTracks = useMemo(() => doc.tracks.filter((t) => t.items.some((i) => i.type === 'mediaClip')), [doc.tracks]);
  const onTrack = (trackId: string | null): TranscriptWord[] => (trackId === null ? [] : all.filter((w) => wordRecordFrame(doc, trackId, w, fps, matches) !== null));
  return { isLoading, error, fps, matches, mediaTracks, all, onTrack };
}
