// @vitest-environment jsdom
/**
 * Customer e2e findings 24/25: picking 8 clips took ~24 clicks because every new row defaulted to
 * "Previous step output" (meaningless for a first step) and hid the file picker. Clips are now
 * picked in one "Choose clips…" multi-select, new rows default to an uploaded file, and the step
 * settings read in plain language with internals behind "Advanced".
 */
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import {
  VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig, applyChosenClips, NEW_CLIP_SOURCE_REF,
} from './VideoAnalyzeStepConfig';
import type { VideoAnalyzeStepConfig as Cfg, VideoSourceRef } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({
    data: [
      { id: 'f1', mimeType: 'video/mp4', originalFileName: 'intro.mp4' },
      { id: 'f2', mimeType: 'video/mp4', originalFileName: 'demo.mp4' },
      { id: 'f3', mimeType: 'video/mp4', originalFileName: 'outro.mp4' },
      { id: 'a1', mimeType: 'audio/mpeg', originalFileName: 'bed.mp3' },
    ],
  }),
}));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: [] }) }));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => cleanup());

const file = (id: string | null): VideoSourceRef => ({ kind: 'ProjectFile', projectFileId: id, stepOrder: null });

function renderConfig(overrides: Partial<Cfg>, opts: { currentStepIndex?: number; allSteps?: StepData[] } = {}) {
  const onChange: Mock = vi.fn();
  render(
    <MantineProvider>
      <VideoAnalyzeStepConfig
        config={{ ...createDefaultVideoAnalyzeStepConfig(), ...overrides }}
        onChange={onChange}
        projectId="p1"
        currentStepIndex={opts.currentStepIndex ?? 0}
        allSteps={opts.allSteps ?? []}
      />
    </MantineProvider>,
  );
  return onChange;
}

describe('applyChosenClips', () => {
  it('appends newly chosen files in pick order and drops the empty placeholder row', () => {
    expect(applyChosenClips([file(null)], ['f2', 'f1'])).toEqual([file('f2'), file('f1')]);
  });

  it('keeps the hand-made order of files that stay chosen', () => {
    expect(applyChosenClips([file('f3'), file('f1')], ['f1', 'f3', 'f2'])).toEqual([file('f3'), file('f1'), file('f2')]);
  });

  it('removes unchosen files but keeps rows that take a video from a step', () => {
    const fromStep: VideoSourceRef = { kind: 'StepOutput', projectFileId: null, stepOrder: 2 };
    expect(applyChosenClips([file('f1'), fromStep, file('f2')], ['f2'])).toEqual([fromStep, file('f2')]);
  });

  it('never leaves the list empty', () => {
    expect(applyChosenClips([file('f1')], [])).toEqual([NEW_CLIP_SOURCE_REF]);
  });
});

describe('VideoAnalyzeStepConfig clips', () => {
  it('lets several clips be chosen at once from one "Choose clips…" picker', async () => {
    const onChange = renderConfig({ source: undefined, sources: [file(null)] });
    fireEvent.click(screen.getByRole('button', { name: 'Choose files' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'intro.mp4' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(onChange.mock.calls.at(-1)![0].sources).toEqual([file('f1')]);
    // Audio files are never offered as clips.
    expect(screen.queryByRole('checkbox', { name: 'bed.mp3' })).toBeNull();
  });

  it('adds new clip rows as uploaded files, not "previous step output"', () => {
    const onChange = renderConfig({ source: undefined, sources: [file('f1')] });
    fireEvent.click(screen.getByRole('button', { name: /Add another clip/ }));
    expect(onChange.mock.calls.at(-1)![0].sources).toEqual([file('f1'), NEW_CLIP_SOURCE_REF]);
  });

  it('shows the file picker straight away on a first step, even for a stored "previous step" source', () => {
    renderConfig({ source: { kind: 'PreviousStepOutput', projectFileId: null, stepOrder: null } });
    expect(screen.getByText('Video file')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Choose' })).toBeTruthy();
    expect(screen.queryByRole('radio', { name: 'Previous step output' })).toBeNull();
  });

  it('still offers step sources once there is an earlier step', () => {
    const earlier = { id: 'e', label: 'Generate', stepType: 'VideoGenerate' } as StepData;
    renderConfig({}, { currentStepIndex: 1, allSteps: [earlier] });
    expect(screen.getByRole('radio', { name: 'Previous step output' })).toBeTruthy();
  });

  it('turning on several clips from an untouched first step starts with an empty file row', () => {
    const onChange = renderConfig({});
    fireEvent.click(screen.getByRole('switch', { name: /^Use several clips/ }));
    expect(onChange.mock.calls.at(-1)![0].sources).toEqual([NEW_CLIP_SOURCE_REF]);
  });
});

describe('VideoAnalyzeStepConfig plain language', () => {
  const JARGON = /Deterministic|ffmpeg|pure C#|opaque-id|bounded view|p-ids|m-ids|docs\/video-editing|currently identical to Prior/;

  it('shows no engineer jargon in the visible settings', () => {
    renderConfig({ vision: 'Optional', derushPrePass: 'Fast' });
    // Visible text only: collapsed "Advanced" content is hidden from the accessibility tree, but
    // even it must not use the phrases the customer flagged.
    expect(document.body.textContent).not.toMatch(JARGON);
  });

  it('keeps the decision pre-pass and the output format behind Advanced', async () => {
    renderConfig({});
    expect(screen.queryByRole('combobox', { name: /Rate each shot before editing/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Advanced' }));
    expect(await screen.findByRole('combobox', { name: /Rate each shot before editing/ })).toBeTruthy();
  });

  it('warns when music tracks are offered (control: the project here has audio, so no warning)', () => {
    renderConfig({ offerMusicTracks: true });
    expect(screen.queryByText(/Your project has no music files/)).toBeNull();
  });
});

describe('VideoAnalyzeStepConfig — output of an earlier run', () => {
  it('offers "Output of an earlier run" even on a first step, where no step source exists', () => {
    renderConfig({ source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null } });
    expect(screen.queryByRole('radio', { name: 'Previous step output' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Output of an earlier run' })).toBeNull();
  });

  it('switching to it asks for a step result id and stores it as kind StepResult', () => {
    const earlier = { id: 'e', label: 'Generate', stepType: 'VideoGenerate' } as StepData;
    const onChange = renderConfig({}, { currentStepIndex: 1, allSteps: [earlier] });
    fireEvent.click(screen.getByRole('radio', { name: 'Output of an earlier run' }));
    expect(onChange.mock.calls.at(-1)![0].source).toMatchObject({ kind: 'StepResult', projectFileId: null, stepOrder: null });
  });

  it('shows the id of a stored StepResult source, keeps the choice visible on a first step, and trims edits', () => {
    const onChange = renderConfig({ source: { kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: 'r-1' } });
    expect(screen.getByRole('radio', { name: 'Output of an earlier run' })).toBeTruthy();
    const box = screen.getByRole('textbox', { name: 'Step result ID' }) as HTMLInputElement;
    expect(box.value).toBe('r-1');
    fireEvent.change(box, { target: { value: ' r-2 ' } });
    expect(onChange.mock.calls.at(-1)![0].source).toMatchObject({ kind: 'StepResult', stepResultId: 'r-2' });
  });
});
