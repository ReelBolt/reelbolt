// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { VideoCompileReframeFields, parseReframesText } from './VideoCompileReframeFields';
import { FieldErrorProvider } from './config-kit';
import { createDefaultVideoCompileStepConfig } from './VideoCompileStepConfig';
import type { VideoCompileStepConfig as Cfg } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

const { FILES } = vi.hoisted(() => ({ FILES: { current: [] as { id: string; mimeType: string; originalFileName: string }[] } }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: FILES.current, error: null, isLoading: false }) }));

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

const steps = [{ label: 'Make the clip', stepType: 'VideoGenerate' }, { label: 'Compile', stepType: 'VideoCompile' }] as StepData[];

function renderFields(overrides: Partial<Cfg> = {}, patch: Mock = vi.fn(), errors: Map<string, string> | null = null) {
  render(
    <MantineProvider>
      <FieldErrorProvider errors={errors}>
        <VideoCompileReframeFields
          config={{ ...createDefaultVideoCompileStepConfig(), ...overrides }}
          patch={patch}
          allSteps={steps}
          currentStepIndex={1}
          projectId="p1"
        />
      </FieldErrorProvider>
    </MantineProvider>,
  );
  return patch;
}
const last = (m: Mock) => m.mock.calls.at(-1)![0] as Partial<Cfg>;

const REFRAME = [{ sourceIndex: 0, fromSec: 0, toSec: 6, keyframes: [{ atSec: 0, zoom: 1 }, { atSec: 6, zoom: 1.5, easing: 'EaseInOut' }] }];

describe('parseReframesText', () => {
  it('treats empty text and an empty list as no reframes', () => {
    expect(parseReframesText('  ')).toEqual({ value: null, error: null });
    expect(parseReframesText('[]')).toEqual({ value: null, error: null });
  });
  it('parses a list and leaves its shape to the server', () => {
    expect(parseReframesText(JSON.stringify(REFRAME))).toEqual({ value: REFRAME, error: null });
    expect(parseReframesText('[{"sourceIndex": -4}]').error).toBeNull();
  });
  it('rejects invalid JSON, a non-list and non-object entries', () => {
    expect(parseReframesText('[{').error).toMatch(/Not valid JSON/);
    expect(parseReframesText('{"a":1}').error).toMatch(/JSON list/);
    expect(parseReframesText('[1]').error).toMatch(/must be an object/);
  });
});

describe('VideoCompileReframeFields — reframes', () => {
  it('shows existing reframes as JSON', () => {
    renderFields({ reframes: REFRAME as Cfg['reframes'] });
    const box = screen.getByRole('textbox', { name: /Reframes \(JSON\)/ }) as HTMLTextAreaElement;
    expect(JSON.parse(box.value)).toEqual(REFRAME);
  });

  it('emits valid JSON and switches stream copy to re-encode; the reframes are what the user typed', () => {
    const patch = renderFields({ mode: 'StreamCopy' });
    fireEvent.change(screen.getByRole('textbox', { name: /Reframes \(JSON\)/ }), { target: { value: JSON.stringify(REFRAME) } });
    expect(last(patch)).toEqual({ reframes: REFRAME, mode: 'Reencode' });
  });

  it('shows the parse error and emits nothing while the JSON is broken', () => {
    const patch = renderFields();
    fireEvent.change(screen.getByRole('textbox', { name: /Reframes \(JSON\)/ }), { target: { value: '[{"sourceIndex":' } });
    expect(screen.getByText(/Not valid JSON/)).toBeTruthy();
    expect(patch).not.toHaveBeenCalled();
  });

  it('clearing the text clears the reframes', () => {
    const patch = renderFields({ reframes: REFRAME as Cfg['reframes'] });
    fireEvent.change(screen.getByRole('textbox', { name: /Reframes \(JSON\)/ }), { target: { value: '' } });
    expect(last(patch)).toEqual({ reframes: null });
  });

  it('marks the field with the server\'s 400 message', () => {
    renderFields({ reframes: REFRAME as Cfg['reframes'] }, vi.fn(), new Map([['config.reframes', 'REFRAME_OVERLAP: reframes 0 and 1 overlap']]));
    const marked = document.querySelector('[data-field-path="config.reframes"]');
    expect(marked?.getAttribute('data-field-error')).toBe('true');
    expect(marked?.getAttribute('data-diff-note')).toContain('REFRAME_OVERLAP');
  });

  it('warns when reframes are set with stream copy', () => {
    renderFields({ mode: 'StreamCopy', reframes: REFRAME as Cfg['reframes'] });
    expect(screen.getByText(/Reframes need Re-encode mode/)).toBeTruthy();
  });
});

describe('VideoCompileReframeFields — per-clip fits, safe area, reference', () => {
  it('adds, edits and removes a per-clip fit, keyed by the 0-based source index', () => {
    const patch = renderFields();
    fireEvent.click(screen.getByRole('button', { name: 'Fit a clip differently' }));
    expect(last(patch)).toEqual({ sourceFits: { '0': 'Pad' } });
    cleanup();

    const patch2 = renderFields({ sourceFits: { '2': 'Pad' } });
    expect((screen.getByRole('textbox', { name: /Clip number/ }) as HTMLInputElement).value).toBe('3');
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(last(patch2)).toEqual({ sourceFits: null });
  });

  it('clamps the safe margin to 0..0.25 and empties to null', () => {
    const patch = renderFields();
    const box = screen.getByRole('textbox', { name: /Safe margin around text/ });
    fireEvent.change(box, { target: { value: '0.9' } });
    expect(last(patch).subjectSafeArea).toBe(0.25);
    fireEvent.change(box, { target: { value: '' } });
    expect(last(patch).subjectSafeArea).toBeNull();
  });

  it('names insert content from an earlier step, a project file, or an earlier run\'s step result', () => {
    FILES.current = [{ id: 'f1', mimeType: 'image/png', originalFileName: 'logo.png' }, { id: 'a1', mimeType: 'audio/mp3', originalFileName: 'song.mp3' }];
    let patch = renderFields({ insertContentRef: { from: 'Step', stepOrder: null } });
    fireEvent.click(screen.getByRole('combobox', { name: /^Step/ }));
    fireEvent.click(screen.getByRole('option', { name: '1. Make the clip' }));
    expect(last(patch).insertContentRef).toEqual({ from: 'Step', projectFileId: null, stepOrder: 1, stepResultId: null });
    cleanup();

    patch = renderFields({ insertContentRef: { from: 'ProjectFile', projectFileId: null } });
    fireEvent.click(screen.getByRole('combobox', { name: /^File/ }));
    expect(screen.queryByRole('option', { name: 'song.mp3' })).toBeNull();
    fireEvent.click(screen.getByRole('option', { name: 'logo.png' }));
    expect(last(patch).insertContentRef).toEqual({ from: 'ProjectFile', projectFileId: 'f1', stepOrder: null, stepResultId: null });
    cleanup();

    patch = renderFields({ insertContentRef: { from: 'StepResult' } });
    fireEvent.change(screen.getByRole('textbox', { name: /Step result ID/ }), { target: { value: ' r-9 ' } });
    expect(last(patch).insertContentRef).toEqual({ from: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: 'r-9' });
  });
});
