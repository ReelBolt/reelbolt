import { describe, expect, it } from 'vitest';
import { doc, media, track } from '../../__tests__/fixtures';
import { ANALYSIS_KINDS, KIND_LABEL, describeKind, eventTypeCounts, documentFileIds, documentMediaRefs, MAX_ANALYSIS_SOURCES, mergeFileAnalysis, shotMotion, type FileAnalysisData } from '../media-analysis';
import { readTranscript } from '../../transcript/transcript';
import { readShots } from '../../cut/cut';
import { sourceMatcher } from '../analysis';

const FILE_A = '00000000-0000-4000-8000-000000000001';
const FILE_B = '00000000-0000-4000-8000-000000000002';
const clipOf = (id: string, file: string, recordIn: number, recordOut: number) => media(id, recordIn, recordOut, 0, { assetRef: { kind: 'projectFile', projectFileId: file } });

describe('per-file analysis feeding the editing panels', () => {
  const d = doc([track('t-v', 'video', [clipOf('11111111-0000-4000-8000-000000000001', FILE_A, 0, 100), clipOf('11111111-0000-4000-8000-000000000002', FILE_B, 100, 200)])]);
  const files: FileAnalysisData[] = [
    { projectFileId: FILE_A, silence: [{ startSec: 1, endSec: 2 }], shots: [{ startSec: 0, endSec: 6 }], motion: [{ startSec: 0, endSec: 6, mean: 0.01, motionClass: 'still' }], transcript: [{ text: 'hello', startSec: 0.5, endSec: 0.9, estimated: false }, { text: 'world', startSec: 1, endSec: 1.4, estimated: true }] },
    { projectFileId: FILE_B, silence: [{ startSec: 0, endSec: 1 }], shots: null, motion: null, transcript: null },
  ];

  it('lists the distinct files in first-use order', () => {
    expect(documentFileIds(d)).toEqual([FILE_A, FILE_B]);
  });

  it('gives each file its own source index and keeps the estimated flag', () => {
    const merged = mergeFileAnalysis(null, files, d);
    expect(merged?.sources).toEqual([{ sourceIndex: 0, projectFileId: FILE_A }, { sourceIndex: 1, projectFileId: FILE_B }]);
    const spans = (merged?.artifact as { silenceSpans: Array<{ sourceIndex: number }> }).silenceSpans;
    expect(spans.map((s) => s.sourceIndex)).toEqual([0, 1]);
    const transcript = readTranscript(merged?.artifact);
    expect(transcript.words.map((w) => [w.text, w.estimated])).toEqual([['hello', false], ['world', true]]);
    expect(transcript.hasEstimatedWords).toBe(true);
    const shots = readShots(merged?.artifact);
    expect(shots[0].motionMean).toBeCloseTo(0.01);
  });

  it('never lets file A\'s seconds match file B\'s clip (two-source track)', () => {
    const merged = mergeFileAnalysis(null, files, d);
    const matches = sourceMatcher(merged, d);
    const clips = d.tracks[0].items.filter((i) => i.type === 'mediaClip');
    expect(matches(clips[0] as never, 0)).toBe(true);
    expect(matches(clips[1] as never, 0)).toBe(false);
    expect(matches(clips[1] as never, 1)).toBe(true);
  });

  it('keeps the seed analysis when it has data for the kind', () => {
    const seed = { stepResultId: 's', artifact: { silenceSpans: [{ id: 'g0', startSec: 0, endSec: 1 }] }, sources: [] };
    const merged = mergeFileAnalysis(seed, [{ ...files[0], transcript: null, shots: null }], d);
    expect(merged).toBe(seed);
  });

  it('weights motion by overlap and says things in plain words', () => {
    expect(shotMotion({ startSec: 0, endSec: 2 }, [{ startSec: 0, endSec: 1, mean: 0, motionClass: 'still' }, { startSec: 1, endSec: 2, mean: 0.2, motionClass: 'high' }])?.mean).toBeCloseTo(0.1);
    expect(describeKind('silence', { state: 'ready', reason: null, requestId: null, updatedAt: null, summary: { rangeCount: 3, totalSeconds: 4 } })).toBe('3 quiet stretches');
    expect(describeKind('transcript', { state: 'failed', reason: 'The audio could not be read.', requestId: null, updatedAt: null, summary: null })).toBe('The audio could not be read.');
    expect(describeKind('shots', { state: 'none', reason: null, requestId: null, updatedAt: null, summary: null })).toBe('Not done yet');
    expect(ANALYSIS_KINDS).toContain('events');
    expect(KIND_LABEL.events).toBe('Events');
    const ev = { state: 'ready' as const, reason: null, requestId: null, updatedAt: null, summary: { eventCount: 6, byType: { appear: 4, beat: 2 } } };
    expect(eventTypeCounts(ev.summary)).toBe('4 appear, 2 beat');
    expect(describeKind('events', ev)).toBe('6 events: 4 appear, 2 beat');
    expect(describeKind('beats', { state: 'ready', reason: null, requestId: null, updatedAt: null, summary: { bpm: 128.4, tempoWord: 'fast' } })).toBe('128 BPM, fast');
  });
});

describe('render-output (stepResult) clips', () => {
  const STEP = '00000000-0000-4000-8000-0000000000aa';
  const stepClip = (id: string, a: number, b: number) => media(id, a, b, 0, { assetRef: { kind: 'stepResult', stepResultId: STEP } });
  const d = doc([track('t-v', 'video', [clipOf('11111111-0000-4000-8000-000000000001', FILE_A, 0, 100), stepClip('11111111-0000-4000-8000-000000000003', 100, 200)])]);

  it('lists render outputs as step_result refs and keeps documentFileIds file-only', () => {
    expect(documentMediaRefs(d)).toEqual([`file:${FILE_A}`, `step_result:${STEP}`]);
    expect(documentFileIds(d)).toEqual([FILE_A]);
  });

  it('caps the sources read per document', () => {
    const clips = Array.from({ length: 12 }, (_, i) => media(`11111111-0000-4000-8000-0000000002${String(i).padStart(2, '0')}`, i * 10, i * 10 + 10, 0, { assetRef: { kind: 'stepResult', stepResultId: `00000000-0000-4000-8000-0000000003${String(i).padStart(2, '0')}` } }));
    const many = doc([track('t-v', 'video', clips)]);
    expect(documentMediaRefs(many)).toHaveLength(MAX_ANALYSIS_SOURCES);
  });

  it('merges a render output\'s words onto its own clips only', () => {
    const files: FileAnalysisData[] = [
      { mediaRef: `file:${FILE_A}`, projectFileId: FILE_A, silence: null, shots: null, motion: null, transcript: null },
      { mediaRef: `step_result:${STEP}`, projectFileId: null, silence: null, shots: null, motion: null, transcript: [{ text: 'hi', startSec: 0, endSec: 1, estimated: false }] },
    ];
    const merged = mergeFileAnalysis(null, files, d);
    expect(merged?.sources).toEqual([{ sourceIndex: 0, projectFileId: null, mediaRef: `step_result:${STEP}` }]);
    expect(readTranscript(merged?.artifact).words.map((w) => w.text)).toEqual(['hi']);
    const matches = sourceMatcher(merged, d);
    const clips = d.tracks[0].items.filter((i) => i.type === 'mediaClip');
    expect(matches(clips[0] as never, 0)).toBe(false);
    expect(matches(clips[1] as never, 0)).toBe(true);
  });

  it('does not map a sourceless legacy seed onto a render-output-only timeline', () => {
    const only = doc([track('t-v', 'video', [stepClip('11111111-0000-4000-8000-000000000003', 0, 100)])]);
    const seed = { stepResultId: 's', artifact: {}, sources: [] };
    const merged = mergeFileAnalysis(seed, [{ mediaRef: `step_result:${STEP}`, projectFileId: null, silence: null, shots: null, motion: null, transcript: [{ text: 'hi', startSec: 0, endSec: 1, estimated: false }] }], only);
    expect(merged?.sources).toEqual([{ sourceIndex: 0, projectFileId: null, mediaRef: `step_result:${STEP}` }]);
  });
});
