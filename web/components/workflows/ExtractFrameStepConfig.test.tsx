// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import {
  ExtractFrameStepConfig,
  createDefaultExtractFrameStepConfig,
  EXTRACT_FRAME_FIRST_STEP_SOURCE,
} from './ExtractFrameStepConfig';
import type { ExtractFrameStepConfig as Cfg } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

const { FILES } = vi.hoisted(() => ({ FILES: { current: [] as { id: string; mimeType: string; originalFileName: string }[] } }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: FILES.current, error: null, isLoading: false }) }));
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

const prior = (label: string): StepData => ({ label, stepType: 'VideoGenerate' } as StepData);

function renderConfig(overrides: Partial<Cfg> = {}, steps: StepData[] = [prior('Make the clip')], index = 1, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <ExtractFrameStepConfig
        config={{ ...createDefaultExtractFrameStepConfig(), ...overrides }}
        onChange={onChange}
        allSteps={steps}
        currentStepIndex={index}
        projectId="p1"
      />
    </MantineProvider>,
  );
  return onChange;
}

const last = (m: Mock) => m.mock.calls.at(-1)![0] as Cfg;

describe('createDefaultExtractFrameStepConfig', () => {
  it('mirrors the C# record defaults: last frame, png, registered as a project file, source the previous step', () => {
    expect(createDefaultExtractFrameStepConfig()).toEqual({
      source: { kind: 'PreviousStepOutput', projectFileId: null, stepOrder: null },
      at: 'Last',
      atSec: null,
      format: 'png',
      maxLongSide: null,
      registerProjectFile: true,
    });
    expect(EXTRACT_FRAME_FIRST_STEP_SOURCE.kind).toBe('ProjectFile');
  });
});

describe('ExtractFrameStepConfig', () => {
  it('offers the four source kinds and hides the step ones when nothing comes before the step', () => {
    renderConfig({ source: { ...EXTRACT_FRAME_FIRST_STEP_SOURCE } }, [], 0);
    fireEvent.click(screen.getByRole('combobox', { name: /Take the frame from/ }));
    expect(screen.getByRole('option', { name: 'An uploaded video file' })).toBeTruthy();
    expect(screen.getByRole('option', { name: /earlier run \(step result ID\)/ })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'The output of the previous step' })).toBeNull();
    expect(screen.queryByRole('option', { name: 'The output of a specific step' })).toBeNull();
  });

  it('picks a project video file', async () => {
    FILES.current = [
      { id: 'v1', mimeType: 'video/mp4', originalFileName: 'a.mp4' },
      { id: 'i1', mimeType: 'image/png', originalFileName: 'b.png' },
    ];
    const onChange = renderConfig({ source: { kind: 'ProjectFile', projectFileId: null, stepOrder: null } });
    fireEvent.click(screen.getByRole('button', { name: 'Choose' }));
    await screen.findByRole('radio', { name: 'a.mp4' });
    expect(screen.queryByRole('radio', { name: 'b.png' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'a.mp4' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(last(onChange).source).toEqual({ kind: 'ProjectFile', projectFileId: 'v1', stepOrder: null, stepResultId: null });
  });

  it('picks an earlier step', () => {
    const onChange = renderConfig({ source: { kind: 'StepOutput', projectFileId: null, stepOrder: null } });
    fireEvent.click(screen.getByRole('combobox', { name: /^Step/ }));
    fireEvent.click(screen.getByRole('option', { name: '1. Make the clip' }));
    expect(last(onChange).source).toMatchObject({ kind: 'StepOutput', stepOrder: 1 });
  });

  it('takes a step result id and trims it', () => {
    const onChange = renderConfig({ source: { kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: null } });
    fireEvent.change(screen.getByRole('textbox', { name: /Step result ID/ }), { target: { value: '  abc-123 ' } });
    expect(last(onChange).source).toEqual({ kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: 'abc-123' });
  });

  it('asks for a moment only with "A moment I choose" and drops it again for first/last (the server refuses atSec otherwise)', () => {
    let onChange = renderConfig();
    expect(screen.queryByRole('textbox', { name: /Moment \(seconds\)/ })).toBeNull();
    fireEvent.click(screen.getByRole('combobox', { name: /^Frame/ }));
    fireEvent.click(screen.getByRole('option', { name: 'A moment I choose' }));
    expect(last(onChange)).toMatchObject({ at: 'Time', atSec: 0 });
    cleanup();

    onChange = renderConfig({ at: 'Time', atSec: 2.5 });
    expect((screen.getByRole('textbox', { name: /Moment \(seconds\)/ }) as HTMLInputElement).value).toBe('2.5');
    fireEvent.click(screen.getByRole('combobox', { name: /^Frame/ }));
    fireEvent.click(screen.getByRole('option', { name: 'The first frame' }));
    expect(last(onChange)).toMatchObject({ at: 'First', atSec: null });
  });

  it('routes format, longest side (clamped to 16..8192, empty = null) and register', () => {
    const onChange = renderConfig();
    fireEvent.click(screen.getByRole('combobox', { name: /^Format/ }));
    fireEvent.click(screen.getByRole('option', { name: /JPEG/ }));
    expect(last(onChange).format).toBe('jpeg');

    const side = screen.getByRole('textbox', { name: /Longest side/ });
    fireEvent.change(side, { target: { value: '99999' } });
    expect(last(onChange).maxLongSide).toBe(8192);
    fireEvent.change(side, { target: { value: '' } });
    expect(last(onChange).maxLongSide).toBeNull();

    fireEvent.click(screen.getByRole('switch', { name: /Save to project files/ }));
    expect(last(onChange).registerProjectFile).toBe(false);
  });

  it('keeps a crop it does not edit', () => {
    const crop = { x: 0.1, y: 0.1, w: 0.5, h: 0.5 };
    const onChange = renderConfig({ crop });
    fireEvent.click(screen.getByRole('switch', { name: /Save to project files/ }));
    expect(last(onChange).crop).toEqual(crop);
  });
});
