// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { useState } from 'react';
import { MantineProvider } from '@mantine/core';
import { ThumbnailGenerateStepConfig, createDefaultThumbnailGenerateStepConfig } from './ThumbnailGenerateStepConfig';
import type { ThumbnailGenerateStepConfig as ThumbnailGenerateStepConfigValue, StepType } from '@/lib/types/workflow';
import type { Mock } from 'vitest';
import type { StepData } from './WorkflowStepList';

afterEach(() => cleanup());

beforeAll(() => {
  // Mock matchMedia
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(query => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });

  // Mock ResizeObserver
  // A `function`, not an arrow: vitest 4+ refuses `new` on an arrow-function mock.
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });

  // Mantine's Combobox scrolls the selected option into view; jsdom lacks it.
  Element.prototype.scrollIntoView = vi.fn();
});

const renderWithMantine = (component: React.ReactElement) => render(<MantineProvider>{component}</MantineProvider>);

/** Minimal StepData — the editor only reads the two fields that drive its step pickers. */
function step(id: string, stepType: StepType, label: string): StepData {
  return {
    id,
    label,
    agentDefinitionId: 'agent-123',
    stepType,
    conditionExpression: null,
    loopSourceExpression: null,
    loopTargetStepOrder: null,
    maxIterations: 3,
    minScore: null,
    inputMappingJson: null,
    agentInputContextMode: null,
    cacheMode: null,
    selectedPriorStepOrders: [],
    trueBranchStepOrder: null,
    falseBranchStepOrder: null,
    parallelAgentIds: [],
    extractConfig: null,
    videoAnalyzeConfig: null,
    videoCompileConfig: null,
    thumbnailGenerateConfig: null,
    videoGenerateConfig: null,
    voiceoverConfig: null,
  };
}

const ANALYZE_STEPS: StepData[] = [
  step('s1', 'VideoAnalyze', 'Watch the footage'),
  step('s2', 'ThumbnailGenerate', 'Make the cover'),
];

/** Controlled harness: the component is stateless, so a round-trip needs real re-rendering. */
function renderControlled(
  initial: ThumbnailGenerateStepConfigValue,
  allSteps: StepData[] = [],
  currentStepIndex = 0,
  handleChange: Mock = vi.fn(),
): Mock {
  function Harness() {
    const [config, setConfig] = useState(initial);
    return (
      <ThumbnailGenerateStepConfig
        config={config}
        onChange={(next) => {
          handleChange(next);
          setConfig(next);
        }}
        allSteps={allSteps}
        currentStepIndex={currentStepIndex}
      />
    );
  }
  renderWithMantine(<Harness />);
  return handleChange;
}

describe('createDefaultThumbnailGenerateStepConfig', () => {
  /**
   * The factory is one of three descriptions of the same record (the C# default parameter values,
   * the JSON schema's `default`s, and this). Asserted as the whole object rather than field by
   * field, so a member that drifts in EITHER direction fails here.
   */
  it("mirrors ThumbnailGenerateStepConfig.cs's declared defaults", () => {
    expect(createDefaultThumbnailGenerateStepConfig()).toEqual({
      version: 1,
      analysisStepOrder: 1,
      analysisStepResultId: null,
      selection: 'Auto',
      atTimeSec: 0,
      sourceIndex: 0,
      width: 1280,
      height: 720,
      fit: 'Cover',
      jpegQuality: 4,
      outputFileName: 'thumbnail.jpg',
      registerProjectFile: true,
      maxCandidates: 24,
      minShotSeconds: 0.4,
      preferBestTake: true,
      overlayText: null,
      overlayPosition: 'None',
      overlayFontSizePct: 9,
      overlayFontColor: 'white',
      overlayBoxColor: 'black@0.45',
      maxOverlayChars: 90,
    });
  });
});

describe('ThumbnailGenerateStepConfig', () => {
  it('starts on Auto and explains that the frame comes from the footage analysis', () => {
    renderControlled(createDefaultThumbnailGenerateStepConfig(), ANALYZE_STEPS, 1);

    expect(screen.getByText('Which frame')).toBeTruthy();
    expect(screen.getByText(/chosen from what the Watch footage step measured/)).toBeTruthy();
    // The Auto-only scoring controls are the ones on screen; the exact-moment pair is not.
    expect(screen.getByLabelText('Moments to score')).toBeTruthy();
    expect(screen.queryByLabelText('Moment (seconds)')).toBeNull();
    // No warning: a Watch footage step comes before this one.
    expect(screen.queryByText(/No 'Watch footage' step comes before this one/)).toBeNull();
  });

  it('lists the Watch footage steps before it, and warns when there are none', () => {
    renderControlled(createDefaultThumbnailGenerateStepConfig(), ANALYZE_STEPS, 1);
    expect(screen.getByText('1. Watch the footage')).toBeTruthy();

    cleanup();

    // A first step has nothing to take a frame from, so the editor says so rather than offering a
    // step order the run would fail on.
    renderControlled(createDefaultThumbnailGenerateStepConfig(), [], 0);
    expect(screen.getByText(/No 'Watch footage' step comes before this one/)).toBeTruthy();
    expect(screen.queryByText('1. Watch the footage')).toBeNull();
  });

  it("stays quiet about a missing footage step when an earlier run's analysis is reused", () => {
    // The result-id override is the one way a step with no Watch footage step above it still has
    // candidates, so the notice must not fire on a runnable step.
    renderControlled({ ...createDefaultThumbnailGenerateStepConfig(), analysisStepResultId: 'result-1' }, [], 0);

    expect(screen.queryByText(/No 'Watch footage' step comes before this one/)).toBeNull();
  });

  it('swaps the Auto scoring controls for the exact moment when AtTime is chosen', () => {
    renderControlled({ ...createDefaultThumbnailGenerateStepConfig(), selection: 'AtTime', atTimeSec: 12.5 }, ANALYZE_STEPS, 1);

    expect(screen.getByText(/You give the exact moment/)).toBeTruthy();
    expect((screen.getByLabelText('Moment (seconds)') as HTMLInputElement).value).toBe('12.5');
    // Clip numbers are 1-based for the customer while SourceIndex is 0-based on the wire.
    expect((screen.getByLabelText('Clip number') as HTMLInputElement).value).toBe('1');
    expect(screen.queryByLabelText('Moments to score')).toBeNull();
    expect(screen.queryByLabelText('Prefer the best attempt')).toBeNull();
  });

  it('routes an edited size through onChange, clamped to the range the executor accepts', () => {
    const handleChange = renderControlled(createDefaultThumbnailGenerateStepConfig(), ANALYZE_STEPS, 1);

    fireEvent.change(screen.getByLabelText('Width (pixels)'), { target: { value: '800' } });
    expect((handleChange as Mock).mock.calls.at(-1)?.[0]).toMatchObject({ width: 800 });

    fireEvent.change(screen.getByLabelText('Width (pixels)'), { target: { value: '99999' } });
    expect((handleChange as Mock).mock.calls.at(-1)?.[0]).toMatchObject({ width: 4096 });

    fireEvent.change(screen.getByLabelText('Image quality'), { target: { value: '0' } });
    expect((handleChange as Mock).mock.calls.at(-1)?.[0]).toMatchObject({ jpegQuality: 2 });
  });

  it('keeps a bare file name: clearing the field restores the default rather than sending an empty one', () => {
    const handleChange = renderControlled(createDefaultThumbnailGenerateStepConfig(), ANALYZE_STEPS, 1);

    fireEvent.change(screen.getByLabelText('File name'), { target: { value: 'cover.jpg' } });
    expect((handleChange as Mock).mock.calls.at(-1)?.[0]).toMatchObject({ outputFileName: 'cover.jpg' });

    fireEvent.change(screen.getByLabelText('File name'), { target: { value: '' } });
    expect((handleChange as Mock).mock.calls.at(-1)?.[0]).toMatchObject({ outputFileName: 'thumbnail.jpg' });
  });

  it('shows the overlay controls only once text is chosen, and says when nothing will be drawn', () => {
    renderControlled({ ...createDefaultThumbnailGenerateStepConfig(), overlayPosition: 'LowerThird' }, ANALYZE_STEPS, 1);

    expect(screen.getByLabelText('Text on the image')).toBeTruthy();
    // Position chosen but no text yet: the step would burn nothing, which is worth saying out loud.
    expect(screen.getByText('Nothing will be drawn yet: type the text above.')).toBeTruthy();
    expect(screen.getByLabelText('Text size (% of image height)')).toBeTruthy();

    cleanup();

    const handleChange = renderControlled({
      ...createDefaultThumbnailGenerateStepConfig(),
      overlayPosition: 'LowerThird',
      overlayText: 'Watch the launch',
    }, ANALYZE_STEPS, 1);

    expect(screen.queryByText('Nothing will be drawn yet: type the text above.')).toBeNull();
    // A Select exposes its label twice in jsdom (the input and the portalled listbox), so it is
    // queried by the value it displays rather than by its accessible name.
    expect(screen.getByDisplayValue('Lower third of the image')).toBeTruthy();
    expect(screen.getByDisplayValue('White')).toBeTruthy();

    // Clearing the text writes null, not '', so the config carries "no overlay text".
    fireEvent.change(screen.getByLabelText('Text on the image'), { target: { value: '' } });
    expect((handleChange as Mock).mock.calls.at(-1)?.[0]).toMatchObject({ overlayText: null });
  });

  it('draws no overlay controls at all while the position is None', () => {
    renderControlled(createDefaultThumbnailGenerateStepConfig(), ANALYZE_STEPS, 1);

    expect(screen.getByDisplayValue('No text (default)')).toBeTruthy();
    expect(screen.queryByLabelText('Text size (% of image height)')).toBeNull();
    expect(screen.queryByDisplayValue('Dark panel (see-through)')).toBeNull();
  });

  it('keeps an overlay colour this build does not offer selectable rather than blanking the field', () => {
    // A hex value is admissible at execution time and may have been written by another editor or the
    // assistant; blanking the picker would hide what the step will actually draw.
    renderControlled({
      ...createDefaultThumbnailGenerateStepConfig(),
      overlayPosition: 'Center',
      overlayText: 'Hello',
      overlayFontColor: '#ff0000',
    }, ANALYZE_STEPS, 1);

    expect(screen.getByDisplayValue('#ff0000 (set elsewhere)')).toBeTruthy();
  });
});
