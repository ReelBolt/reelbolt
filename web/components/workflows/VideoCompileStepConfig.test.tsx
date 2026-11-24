// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { useState } from 'react';
import { MantineProvider } from '@mantine/core';
import { VideoCompileStepConfig, createDefaultVideoCompileStepConfig, describeInsertAudioProblem } from './VideoCompileStepConfig';
import type { VideoCompileStepConfig as VideoCompileStepConfigValue, StepType } from '@/lib/types/workflow';
import type { Mock } from 'vitest';
import type { StepData } from './WorkflowStepList';

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: [], error: null, isLoading: false }),
}));

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

// Vitest runs without `globals`, so Testing Library's auto-cleanup never registers.
afterEach(() => {
  cleanup();
});

const renderWithMantine = (component: React.ReactElement) => {
  return render(<MantineProvider>{component}</MantineProvider>);
};

// Helper to create a minimal StepData for testing
function createTestStep(id: string, stepType: string = 'Agent'): StepData {
  return {
    id,
    label: `Step ${id}`,
    agentDefinitionId: 'agent-123',
    stepType: stepType as StepType,
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
    voiceoverConfig: null,
    videoGenerateConfig: null,
  };
}

describe('VideoCompileStepConfig', () => {
  it('routes an edited field through onChange while preserving unknown top-level keys', () => {
    const handleChange = vi.fn();

    const config = {
      ...createDefaultVideoCompileStepConfig(),
      unknownKey: 'test-value',
    } as VideoCompileStepConfigValue & { unknownKey: string };

    renderWithMantine(
      <VideoCompileStepConfig
        config={config}
        onChange={handleChange}
        projectId="test-project"
        currentStepIndex={1}
        allSteps={[{ ...createTestStep('step-1'), label: 'Analyze', stepType: 'VideoAnalyze' as StepType }]}
      />,
    );

    // Verify the form renders by checking for the Encoding section
    expect(screen.getByText('Mode')).toBeTruthy();

    // Find and interact with the CRF NumberInput inside the Encoding fieldset
    const crfInput = screen.getByDisplayValue(String(config.crf)) as HTMLInputElement;
    expect(crfInput).toBeTruthy();

    // Fire change event to update the CRF value
    fireEvent.change(crfInput, { target: { value: '25' } });

    // Verify onChange was called with the updated crf and the unknown key preserved
    expect(handleChange).toHaveBeenCalled();
    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;
    expect(callArg.crf).toBe(25);
    expect(callArg.unknownKey).toBe('test-value');
  });
});

/** Two earlier steps, so the generated-clips picker has a real step to reference. */
const GENERATED_CLIPS_STEPS: StepData[] = [
  { ...createTestStep('step-1'), label: 'Analyze', stepType: 'VideoAnalyze' as StepType },
  { ...createTestStep('step-2'), label: 'Generate', stepType: 'VideoGenerate' as StepType },
];

function lastConfig(handleChange: Mock): VideoCompileStepConfigValue {
  const calls = handleChange.mock.calls;
  return calls[calls.length - 1][0] as VideoCompileStepConfigValue;
}

/** Controlled harness: the component is stateless, so a round-trip needs real re-rendering. */
function renderControlled(
  initial: VideoCompileStepConfigValue,
  handleChange: Mock = vi.fn(),
): Mock {
  function Harness() {
    const [config, setConfig] = useState(initial);
    return (
      <VideoCompileStepConfig
        config={config}
        onChange={(next) => {
          handleChange(next);
          setConfig(next);
        }}
        projectId="test-project"
        currentStepIndex={2}
        allSteps={GENERATED_CLIPS_STEPS}
      />
    );
  }
  renderWithMantine(<Harness />);
  return handleChange;
}

// The switch reads "Add generated clips" and the picker "Generated clips from" since the customer
// e2e findings plain-language pass (they were "Enable generated clips" / "Generated clips plan").
describe('VideoCompileStepConfig — generated clips', () => {
  it('renders the generated-clips switch inside its own Generated clips (optional) Fieldset', () => {
    renderWithMantine(
      <VideoCompileStepConfig
        config={createDefaultVideoCompileStepConfig()}
        onChange={vi.fn()}
        projectId="test-project"
        currentStepIndex={2}
        allSteps={GENERATED_CLIPS_STEPS}
      />,
    );

    const legend = screen.getByText('Generated clips (optional)');
    const fieldset = legend.closest('fieldset');
    expect(fieldset).toBeTruthy();

    const switchInput = screen.getByRole('switch', { name: /^Add generated clips/ });
    expect(switchInput).toBeTruthy();
    expect(fieldset!.contains(switchInput)).toBe(true);
  });

  it('shows the generated-clips picker only while the switch is on', () => {
    renderWithMantine(
      <VideoCompileStepConfig
        config={createDefaultVideoCompileStepConfig()}
        onChange={vi.fn()}
        projectId="test-project"
        currentStepIndex={2}
        allSteps={GENERATED_CLIPS_STEPS}
      />,
    );

    expect(screen.queryByRole('combobox', { name: 'Generated clips from' })).toBeNull();
    expect(screen.queryByLabelText('Max generated clips')).toBeNull();
  });

  it('renders the picker defaulting to Previous and the Max generated clips input when on', () => {
    renderWithMantine(
      <VideoCompileStepConfig
        config={{ ...createDefaultVideoCompileStepConfig(), enableGeneratedClips: true }}
        onChange={vi.fn()}
        projectId="test-project"
        currentStepIndex={2}
        allSteps={GENERATED_CLIPS_STEPS}
      />,
    );

    expect(screen.getByRole('combobox', { name: 'Generated clips from' })).toBeTruthy();
    const maxClips = screen.getByLabelText('Max generated clips') as HTMLInputElement;
    expect(maxClips.value).toBe('8');
  });

  it('preserves the picker value across an off/on round-trip and never clears it', () => {
    const handleChange = renderControlled({
      ...createDefaultVideoCompileStepConfig(),
      enableGeneratedClips: true,
      generatedClips: { from: 'Step', stepOrder: 2 },
    });

    const switchInput = screen.getByRole('switch', { name: /^Add generated clips/ }) as HTMLInputElement;
    expect(switchInput.checked).toBe(true);
    expect(screen.getByRole('combobox', { name: 'Generated clips from' })).toBeTruthy();

    // Off: the picker hides, but the ref it held must survive in the emitted config.
    fireEvent.click(switchInput);
    const offConfig = lastConfig(handleChange);
    expect(offConfig.enableGeneratedClips).toBe(false);
    expect(offConfig.generatedClips).toEqual({ from: 'Step', stepOrder: 2 });
    expect(screen.queryByRole('combobox', { name: 'Generated clips from' })).toBeNull();

    // Back on: the picker comes back with exactly the value it had.
    const onSwitch = screen.getByRole('switch', { name: /^Add generated clips/ }) as HTMLInputElement;
    expect(onSwitch.checked).toBe(false);
    fireEvent.click(onSwitch);

    const onConfig = lastConfig(handleChange);
    expect(onConfig.enableGeneratedClips).toBe(true);
    expect(onConfig.generatedClips).toEqual({ from: 'Step', stepOrder: 2 });
    // The ref survives, not just the plain object: the picker re-renders it as its own selection.
    // InputRefPicker's Select displays the option's label, so `from: 'Step'` reads as 'Specific step'.
    expect((screen.getByRole('combobox', { name: 'Generated clips from' }) as HTMLInputElement).value).toBe('Specific step');
    expect((screen.getByRole('combobox', { name: 'Step' }) as HTMLInputElement).value).toBe('2. Generate');
  });

  it('flips the mode to Reencode when the switch is turned on from StreamCopy', () => {
    const handleChange = renderControlled({
      ...createDefaultVideoCompileStepConfig(),
      mode: 'StreamCopy',
      enableGeneratedClips: false,
    });

    fireEvent.click(screen.getByRole('switch', { name: /^Add generated clips/ }));

    const callArg = lastConfig(handleChange);
    expect(callArg.enableGeneratedClips).toBe(true);
    expect(callArg.mode).toBe('Reencode');
  });

  it('emits the three new fields with the C# defaults from the factory', () => {
    const config = createDefaultVideoCompileStepConfig();
    expect(config.generatedClips).toBeNull();
    expect(config.enableGeneratedClips).toBe(false);
    expect(config.maxGeneratedClips).toBe(8);
  });

  it('renders a legacy config that predates the three fields without crashing', () => {
    const legacy = createDefaultVideoCompileStepConfig() as unknown as Record<string, unknown>;
    delete legacy.generatedClips;
    delete legacy.enableGeneratedClips;
    delete legacy.maxGeneratedClips;

    renderWithMantine(
      <VideoCompileStepConfig
        config={legacy as unknown as VideoCompileStepConfigValue}
        onChange={vi.fn()}
        projectId="test-project"
        currentStepIndex={2}
        allSteps={GENERATED_CLIPS_STEPS}
      />,
    );

    // The switch still renders (as off), with no picker offered.
    const switchInput = screen.getByRole('switch', { name: /^Add generated clips/ }) as HTMLInputElement;
    expect(switchInput.checked).toBe(false);
    expect(screen.queryByRole('combobox', { name: 'Generated clips from' })).toBeNull();
  });
});

/** The switch reads "Mix the sound from the inserted clip"; the volume sits directly under it. */
describe('VideoCompileStepConfig — sound from the inserted clip', () => {
  const renderInserts = (overrides: Partial<VideoCompileStepConfigValue> = {}, handleChange: Mock = vi.fn()) => {
    renderWithMantine(
      <VideoCompileStepConfig
        config={{ ...createDefaultVideoCompileStepConfig(), enableInserts: true, ...overrides }}
        onChange={handleChange}
        projectId="test-project"
        currentStepIndex={2}
        allSteps={GENERATED_CLIPS_STEPS}
      />,
    );
    return handleChange;
  };

  it('leaves both new fields out of the factory, so an untouched step is saved exactly as before', () => {
    const config = createDefaultVideoCompileStepConfig() as unknown as Record<string, unknown>;
    expect('mixInsertAudio' in config).toBe(false);
    expect('insertAudioGainDb' in config).toBe(false);
  });

  it('puts the mix switch inside the Screen inserts section, off by default', () => {
    renderInserts();

    const fieldset = screen.getByText('Screen inserts (optional)').closest('fieldset');
    expect(fieldset).toBeTruthy();

    const switchInput = screen.getByRole('switch', { name: /^Mix the sound from the inserted clip/ }) as HTMLInputElement;
    expect(switchInput.checked).toBe(false);
    expect(fieldset!.contains(switchInput)).toBe(true);
  });

  it('offers neither the switch nor the volume while screen inserts are off', () => {
    renderInserts({ enableInserts: false });

    expect(screen.queryByRole('switch', { name: /^Mix the sound/ })).toBeNull();
    expect(screen.queryByLabelText(/^Insert sound volume/)).toBeNull();
  });

  // Every interaction re-renders the whole 1100-line form, so the two interaction-heavy round
  // trips get an explicit budget: the default 5s is tight under a fully parallel run.
  it('reveals the volume only while the switch is on, starting at 0 dB', () => {
    const handleChange = renderControlled({
      ...createDefaultVideoCompileStepConfig(),
      enableInserts: true,
    });

    expect(screen.queryByLabelText(/^Insert sound volume/)).toBeNull();

    fireEvent.click(screen.getByRole('switch', { name: /^Mix the sound/ }));
    expect(lastConfig(handleChange).mixInsertAudio).toBe(true);
    expect((screen.getByLabelText(/^Insert sound volume/) as HTMLInputElement).value).toBe('0');
  }, 20000);

  it('clamps the volume to −24..12 dB and keeps it across an off/on round trip', () => {
    const handleChange = renderControlled({
      ...createDefaultVideoCompileStepConfig(),
      enableInserts: true,
      mixInsertAudio: true,
      insertAudioGainDb: -6,
    });
    const volume = () => screen.getByLabelText(/^Insert sound volume/) as HTMLInputElement;
    expect(volume().value).toBe('-6');

    fireEvent.change(volume(), { target: { value: '-40' } });
    expect(lastConfig(handleChange).insertAudioGainDb).toBe(-24);
    fireEvent.change(volume(), { target: { value: '20' } });
    expect(lastConfig(handleChange).insertAudioGainDb).toBe(12);

    // Off: the volume hides, but the value it held survives in the emitted config.
    fireEvent.click(screen.getByRole('switch', { name: /^Mix the sound/ }));
    const off = lastConfig(handleChange);
    expect(off.mixInsertAudio).toBe(false);
    expect(off.insertAudioGainDb).toBe(12);
    expect(screen.queryByLabelText(/^Insert sound volume/)).toBeNull();

    // Back on: the volume returns with the same value.
    fireEvent.click(screen.getByRole('switch', { name: /^Mix the sound/ }));
    const on = lastConfig(handleChange);
    expect(on.mixInsertAudio).toBe(true);
    expect(on.insertAudioGainDb).toBe(12);
    expect(volume().value).toBe('12');
  }, 20000);

  it('warns before the run when the sound is on but screen inserts are off', () => {
    renderInserts({ enableInserts: false, mixInsertAudio: true });

    expect(screen.getByText('Screen inserts are switched off')).toBeTruthy();
    // Names the switch the customer has to turn on — and does not collide with that switch's own label.
    expect(screen.getByText(/can only be heard when screen inserts are on/)).toBeTruthy();
  });

  it('warns before the run when the program audio is copied instead of re-encoded', () => {
    renderInserts({ audioCodec: 'copy', mixInsertAudio: true });

    expect(screen.getByText('This sound cannot be mixed in')).toBeTruthy();
    expect(screen.getByText(/under Advanced below/)).toBeTruthy();
  });

  it('says nothing while the switch is off, or when the settings agree', () => {
    renderInserts({ audioCodec: 'copy' });
    expect(screen.queryByText('Screen inserts are switched off')).toBeNull();
    expect(screen.queryByText('This sound cannot be mixed in')).toBeNull();

    cleanup();
    renderInserts({ mixInsertAudio: true });
    expect(screen.queryByText('Screen inserts are switched off')).toBeNull();
    expect(screen.queryByText('This sound cannot be mixed in')).toBeNull();
  });

  it('describes only the two ways the compile step would reject the run', () => {
    const base = { ...createDefaultVideoCompileStepConfig(), enableInserts: true, audioCodec: 'aac' };
    // Quiet on: the switch absent, switched back off, inserts off on their own, or nothing disagreeing.
    expect(describeInsertAudioProblem(base)).toBeNull();
    expect(describeInsertAudioProblem({ ...base, mixInsertAudio: false })).toBeNull();
    expect(describeInsertAudioProblem({ ...base, audioCodec: 'copy' })).toBeNull();
    expect(describeInsertAudioProblem({ ...base, mixInsertAudio: true })).toBeNull();
    expect(describeInsertAudioProblem({ ...base, mixInsertAudio: true, enableInserts: false })?.title)
      .toBe('Screen inserts are switched off');
    expect(describeInsertAudioProblem({ ...base, mixInsertAudio: true, audioCodec: 'copy' })?.title)
      .toBe('This sound cannot be mixed in');
  });
});

/** An earlier 'Watch footage' step that will follow one object with each engine. */
const ANALYZE_OBJECTS_STEPS: StepData[] = [
  {
    ...createTestStep('step-1'),
    label: 'Watch footage',
    stepType: 'VideoAnalyze' as StepType,
    videoAnalyzeConfig: {
      trackObjects: [
        { label: 'speedometer' },
        { label: 'speedometer', method: 'Segmentation' },
        { label: 'licence plate', method: 'Feature' },
      ],
    } as unknown as StepData['videoAnalyzeConfig'],
  },
];

/**
 * The ONE list that replaced the separate "Hide objects" and "Effect on one object" sections: the
 * JSON keeps both shapes (the free tracker's `censorLabels` and the paid engine's `regionEffects`),
 * so a saved step renders as rows and nothing is rewritten until the user edits it.
 */
describe('VideoCompileStepConfig — effects on objects', () => {
  const renderEffects = (
    overrides: Partial<VideoCompileStepConfigValue> = {},
    allSteps: StepData[] = ANALYZE_OBJECTS_STEPS,
    handleChange: Mock = vi.fn(),
  ) => {
    renderWithMantine(
      <VideoCompileStepConfig
        config={{ ...createDefaultVideoCompileStepConfig(), ...overrides }}
        onChange={handleChange}
        projectId="test-project"
        currentStepIndex={1}
        allSteps={allSteps}
      />,
    );
    return handleChange;
  };

  it('renders a saved config\'s censorLabels as tracked rows and drops the old Hide objects section', () => {
    const handleChange = renderEffects({ censorLabels: ['licence plate', '*'], censorStyle: 'Pixelate' });

    // The two old sections are gone for good, not hidden.
    expect(screen.queryByText(/Hide objects/)).toBeNull();
    expect(screen.queryByText(/Effect on one object/)).toBeNull();
    const fieldset = screen.getByText('Effects on objects (optional)').closest('fieldset');
    expect(fieldset).toBeTruthy();

    const objects = screen.getAllByRole('combobox', { name: 'Object' }) as HTMLInputElement[];
    expect(objects.map((input) => input.value)).toEqual(['licence plate', '*']);
    objects.forEach((input) => expect(fieldset!.contains(input)).toBe(true));
    const effects = screen.getAllByRole('combobox', { name: 'Effect' }) as HTMLInputElement[];
    expect(effects.map((input) => input.value)).toEqual(['Pixelate', 'Pixelate']);
    // The free tracker's own extras, once per row, and none of the paid engine's.
    expect(screen.getAllByLabelText('Padding')).toHaveLength(2);
    expect(screen.queryByLabelText('Strength')).toBeNull();
    // Rendering a saved config rewrites nothing: the first edit is the only write.
    expect(handleChange).not.toHaveBeenCalled();
  });

  it('offers the objects the earlier Watch footage step follows, each labelled with its engine', () => {
    renderEffects();

    fireEvent.click(screen.getByRole('button', { name: /^Add an effect/ }));
    fireEvent.click(screen.getByRole('combobox', { name: 'Object' }));

    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      'speedometer — tracked',
      'speedometer — segmentation (paid)',
      'licence plate — tracked',
    ]);
  });

  it('keeps typing as an escape hatch, offering the typed words once per engine', () => {
    renderEffects();

    fireEvent.click(screen.getByRole('button', { name: /^Add an effect/ }));
    const object = screen.getByRole('combobox', { name: 'Object' }) as HTMLInputElement;
    fireEvent.click(object);
    fireEvent.change(object, { target: { value: 'the tabby cat' } });

    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      'the tabby cat — tracked',
      'the tabby cat — segmentation (paid)',
    ]);
  });
});
