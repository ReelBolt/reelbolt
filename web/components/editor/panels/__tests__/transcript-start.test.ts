import { describe, expect, it } from 'vitest';
import { transcriptStartState } from '../TranscriptStart';
import { analysisNeedOf } from '@/lib/editing/proposals/labels-msg';
import { NO_KIND, type MediaAnalysisStatus, type KindStatus } from '@/lib/editing/api/media-analysis';

const G = '00000000-0000-4000-8000-0000000000aa';
const status = (ref: string, transcript: Partial<KindStatus>): MediaAnalysisStatus => ({
  mediaRef: ref,
  kinds: { silence: NO_KIND, shots: NO_KIND, motion: NO_KIND, transcript: { ...NO_KIND, ...transcript }, beats: NO_KIND, events: NO_KIND },
});

describe('transcriptStartState', () => {
  const refs = [`step_result:${G}`];
  it('offers the words when nothing was analysed', () => {
    expect(transcriptStartState(refs, [status(refs[0], {})])).toEqual({ kind: 'notAnalysed', refs });
  });
  it('shows progress while queued or running', () => {
    expect(transcriptStartState(refs, [status(refs[0], { state: 'running' })]).kind).toBe('running');
    expect(transcriptStartState(refs, [status(refs[0], { state: 'queued' })]).kind).toBe('running');
  });
  it('shows the reason and offers a retry when it failed', () => {
    expect(transcriptStartState(refs, [status(refs[0], { state: 'failed', reason: 'No speech found.' })])).toEqual({ kind: 'failed', reason: 'No speech found.', refs });
  });
  it('is loading until statuses arrive, and empty without media', () => {
    expect(transcriptStartState(refs, undefined).kind).toBe('loading');
    expect(transcriptStartState([], []).kind).toBe('none');
    expect(transcriptStartState(refs, [status(refs[0], { state: 'ready' })]).kind).toBe('none');
  });
});

describe('analysisNeedOf with render outputs', () => {
  it('keeps step_result refs and reads the availability hint shape', () => {
    expect(analysisNeedOf({ kinds: ['transcript'], mediaRefs: [`step_result:${G}`, 'bogus'] })).toEqual({ kinds: ['transcript'], mediaRefs: [`step_result:${G}`] });
    expect(analysisNeedOf({ kinds: ['transcript'], missingRefs: [`file:${G}`] })).toEqual({ kinds: ['transcript'], mediaRefs: [`file:${G}`] });
  });
});
