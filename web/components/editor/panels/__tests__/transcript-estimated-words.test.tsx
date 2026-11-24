// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { mergeFileAnalysis, type FileAnalysisData } from '@/lib/editing/api/media-analysis';
import { readTranscript } from '@/lib/editing/transcript/transcript';

const FILE = '00000000-0000-4000-8000-000000000001';
// The prod shape: a long transcript whose words are all estimated, with the language null on the words and set on the result.
const WORDS = Array.from({ length: 685 }, (_, i) => ({ text: `w${i}`, startSec: i * 0.4, endSec: i * 0.4 + 0.35, estimated: true, language: null as string | null }));
const files: FileAnalysisData[] = [{ mediaRef: `file:${FILE}`, projectFileId: FILE, silence: null, shots: null, motion: null, transcript: WORDS }];
const d = doc([track(TRACKS.v1, 'video', [media('c1', 0, 8000, 0, { assetRef: { kind: 'projectFile', projectFileId: FILE } })])]);

vi.mock('@/lib/editing/api/media-analysis', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/media-analysis')>()),
  useDocumentMediaAnalysis: () => ({ data: files, isLoading: false, error: undefined }),
  useDocumentSourceStatuses: () => ({ data: undefined, mutate: vi.fn() }),
}));

import { TranscriptPanel } from '../TranscriptPanel';

afterEach(cleanup);

describe('estimated-only transcript (prod shape)', () => {
  it('merges and reads every word, flagged estimated', () => {
    const merged = mergeFileAnalysis(null, files, d);
    const t = readTranscript(merged?.artifact);
    expect(t.words).toHaveLength(685);
    expect(t.hasEstimatedWords).toBe(true);
  });

  it('shows the words in the panel instead of the empty state', () => {
    global.matchMedia = (q: string) => ({ matches: false, media: q, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
    render(<MantineProvider><TranscriptPanel doc={d} projectId="p" seedStepResultId={null} readOnly={false} newItemId={() => 'n'} onApply={() => {}} onSeek={() => {}} /></MantineProvider>);
    expect(screen.queryByTestId('transcript-empty')).toBeNull();
    expect(screen.getByTestId('transcript-word-w0')).toBeTruthy();
    expect(screen.getAllByTestId(/^transcript-word-/).length).toBe(685);
  });
});
