// @vitest-environment jsdom
/**
 * "Trimming inside a shot": each clip can be used only in part (from/until), and long shots can be
 * split into sub-shots the editor keeps by id.
 */
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { VideoAnalyzeStepConfig, createDefaultVideoAnalyzeStepConfig } from './VideoAnalyzeStepConfig';
import type { VideoAnalyzeStepConfig as Cfg } from '@/lib/types/workflow';
import { STEP_CONFIG_SCHEMAS } from '@/lib/schemas/workflow-step-configs';

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({
    data: [
      { id: 'f1', mimeType: 'video/mp4', originalFileName: 'intro.mp4' },
      { id: 'm1', mimeType: 'audio/mpeg', originalFileName: 'the-last-point.mp3' },
    ],
  }),
}));

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

function renderConfig(overrides: Partial<Cfg>) {
  const onChange: Mock = vi.fn();
  render(
    <MantineProvider>
      <VideoAnalyzeStepConfig config={{ ...createDefaultVideoAnalyzeStepConfig(), ...overrides }} onChange={onChange} projectId="p1" />
    </MantineProvider>,
  );
  return onChange;
}

describe('VideoAnalyzeStepConfig — the music\'s beat', () => {
  it('measures the beat on request and picks the track the video is cut to', () => {
    const onChange = renderConfig({});
    expect(screen.queryByRole('combobox', { name: /^Music the video is cut to/ })).toBeNull();
    fireEvent.click(screen.getByRole('switch', { name: /^Measure the music's beat/ }));
    expect(onChange.mock.calls.at(-1)![0].analyzeMusicBeats).toBe(true);

    cleanup();
    const onChange2 = renderConfig({ analyzeMusicBeats: true });
    fireEvent.click(screen.getByRole('combobox', { name: /^Music the video is cut to/ }));
    fireEvent.click(screen.getByRole('option', { name: 'the-last-point.mp3' }));
    expect(onChange2.mock.calls.at(-1)![0].musicBeatTrackProjectFileId).toBe('m1');

    const props = STEP_CONFIG_SCHEMAS.VideoAnalyze.properties as Record<string, Record<string, unknown>>;
    expect(props.analyzeMusicBeats.type).toEqual(['boolean', 'null']);
    expect(props.maxBeatAnalyzedTracks.maximum).toBe(20);
    const compile = STEP_CONFIG_SCHEMAS.VideoCompile.properties as Record<string, Record<string, unknown>>;
    expect(compile.beatSync.enum).toEqual(['Off', 'Beat', 'Bar', null]);
  });
});

describe('VideoAnalyzeStepConfig — using part of a clip', () => {
  it('routes from/until seconds onto the clip', () => {
    const onChange = renderConfig({ source: { kind: 'ProjectFile', projectFileId: 'f1', stepOrder: null } });
    fireEvent.change(screen.getByRole('textbox', { name: /^Use from \(seconds\)/ }), { target: { value: '2.5' } });
    expect(onChange.mock.calls.at(-1)![0].source).toEqual({ kind: 'ProjectFile', projectFileId: 'f1', stepOrder: null, inSec: 2.5 });
  });

  it('flags an end that is not after the start', () => {
    renderConfig({ source: { kind: 'ProjectFile', projectFileId: 'f1', stepOrder: null, inSec: 5, outSec: 3 } });
    expect(screen.getByText('Must be after the start')).toBeTruthy();
  });

  it('offers a window on every clip of a multi-clip step', () => {
    renderConfig({ source: undefined, sources: [{ kind: 'ProjectFile', projectFileId: 'f1' }, { kind: 'ProjectFile', projectFileId: null }] });
    expect(screen.getAllByRole('textbox', { name: /^Use until \(seconds\)/ })).toHaveLength(2);
  });

  it('routes the sub-shot length and mirrors the new members in the schema', () => {
    const onChange = renderConfig({});
    fireEvent.change(screen.getByRole('textbox', { name: /^Split shots longer than/ }), { target: { value: '4' } });
    expect(onChange.mock.calls.at(-1)![0].maxShotSec).toBe(4);

    const props = STEP_CONFIG_SCHEMAS.VideoAnalyze.properties as Record<string, Record<string, unknown>>;
    expect(props.maxShotSec.type).toEqual(['number', 'null']);
    const ref = (STEP_CONFIG_SCHEMAS.VideoAnalyze as { definitions: Record<string, { properties: Record<string, unknown>; required: string[] }> })
      .definitions.VideoSourceRef;
    expect(Object.keys(ref.properties)).toEqual(['kind', 'projectFileId', 'stepResultId', 'stepOrder', 'inSec', 'outSec']);
    expect(ref.required).toEqual(['kind']);
  });
});
