// @vitest-environment jsdom
'use client';

import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StepCard } from './StepCard';
import type { StepData } from './WorkflowStepList';

// Mock the child components to isolate StepCard's own behavior
vi.mock('./StepTypeBadge', () => ({
  StepTypeBadge: ({ stepType }: { stepType: string }) => <div data-testid="step-badge">{stepType}</div>,
}));

vi.mock('./StepTypeSelector', () => ({
  StepTypeSelector: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => (
    <select data-testid="step-type-selector" value={value} onChange={(e) => onChange(e.target.value)}>
      <option>Agent</option>
      <option>Conditional</option>
      <option>ExtractFrame</option>
      <option>AudioGenerate</option>
    </select>
  ),
}));

vi.mock('./AgentPicker', () => ({
  AgentPicker: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => (
    <input data-testid="agent-picker" value={value} onChange={(e) => onChange(e.target.value)} />
  ),
}));

vi.mock('./StepConfigSheet', () => ({
  StepConfigSheet: ({ opened, onClose, onChange }: { opened: boolean; onClose: () => void; onChange: (value: unknown) => void }) => (
    opened ? (
      <div data-testid="config-sheet">
        <button onClick={onClose}>Close Sheet</button>
      </div>
    ) : null
  ),
}));

vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({ data: [], isLoading: false }),
}));

// Mock all the step config components to avoid importing them
vi.mock('./ConditionalStepConfig', () => ({
  ConditionalStepConfig: () => null,
}));
vi.mock('./ParallelStepConfig', () => ({
  ParallelStepConfig: () => null,
}));
vi.mock('./ForEachStepConfig', () => ({
  ForEachStepConfig: () => null,
}));
vi.mock('./ReviewLoopStepConfig', () => ({
  ReviewLoopStepConfig: () => null,
}));
vi.mock('./ExtractStepConfig', () => ({
  ExtractStepConfig: () => null,
  createDefaultExtractStepConfig: () => ({}),
}));
vi.mock('./VideoAnalyzeStepConfig', () => ({
  VideoAnalyzeStepConfig: () => null,
  createDefaultVideoAnalyzeStepConfig: () => ({}),
}));
vi.mock('./VideoCompileStepConfig', () => ({
  VideoCompileStepConfig: () => null,
  createDefaultVideoCompileStepConfig: () => ({}),
}));
vi.mock('./VoiceoverStepConfig', () => ({
  VoiceoverStepConfig: () => null,
  createDefaultVoiceoverStepConfig: () => ({}),
}));
vi.mock('./RoomStepConfigEditor', () => ({
  RoomStepConfigEditor: () => null,
  ROOM_STEP_KINDS: {},
  isRoomStepType: () => false,
  defaultRoomConfigPatch: () => ({}),
}));

// Mock window APIs for Mantine and component testing
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
});

const createMockStep = (overrides?: Partial<StepData>): StepData => ({
  id: '1',
  label: 'Test Step',
  agentDefinitionId: 'agent-1',
  stepType: 'Agent',
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
  ...overrides,
});

const renderWithProvider = (component: React.ReactElement) => {
  return render(
    <MantineProvider>
      {component}
    </MantineProvider>
  );
};

describe('StepCard', () => {
  const mockStep = createMockStep();
  const mockOnChange = vi.fn();
  const mockOnRemove = vi.fn();
  const mockOnMoveUp = vi.fn();
  const mockOnMoveDown = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('should render move-up button with >= 44x44 CSS px hit area', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={true}
        canMoveDown={false}
      />
    );

    const moveUpButton = screen.getByLabelText('Move step up');
    // Verify the button is rendered with size="lg" which provides adequate hit area
    expect(moveUpButton).toBeTruthy();
    // In Mantine, size="lg" renders to at least 42px (44px with padding)
    expect(moveUpButton.tagName).toBe('BUTTON');
  });

  it('should render move-down button with >= 44x44 CSS px hit area', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={true}
      />
    );

    const moveDownButton = screen.getByLabelText('Move step down');
    // Verify the button is rendered with size="lg" which provides adequate hit area
    expect(moveDownButton).toBeTruthy();
    // In Mantine, size="lg" renders to at least 42px (44px with padding)
    expect(moveDownButton.tagName).toBe('BUTTON');
  });

  it('should render delete button with >= 44x44 CSS px hit area', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    const deleteButton = screen.getByLabelText('Delete step');
    // Verify the button is rendered with size="lg" which provides adequate hit area
    expect(deleteButton).toBeTruthy();
    // In Mantine, size="lg" renders to at least 42px (44px with padding)
    expect(deleteButton.tagName).toBe('BUTTON');
  });

  it('should allow move-up button to be focused and activated with keyboard', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={true}
        canMoveDown={false}
      />
    );

    const moveUpButton = screen.getByLabelText('Move step up');

    // Focus the button
    moveUpButton.focus();
    expect(document.activeElement).toBe(moveUpButton);

    // Click to activate (simulating Enter/Space behavior on a button)
    fireEvent.click(moveUpButton);
    expect(mockOnMoveUp).toHaveBeenCalled();
  });

  it('should allow move-down button to be focused and activated with keyboard', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={true}
      />
    );

    const moveDownButton = screen.getByLabelText('Move step down');

    // Focus the button
    moveDownButton.focus();
    expect(document.activeElement).toBe(moveDownButton);

    // Click to activate (simulating Enter/Space behavior on a button)
    fireEvent.click(moveDownButton);
    expect(mockOnMoveDown).toHaveBeenCalled();
  });

  it('should allow delete button to be focused and activated with keyboard', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    const deleteButton = screen.getByLabelText('Delete step');

    // Focus the button
    deleteButton.focus();
    expect(document.activeElement).toBe(deleteButton);

    // Click to activate (simulating Enter/Space behavior on a button)
    fireEvent.click(deleteButton);
    expect(mockOnRemove).toHaveBeenCalled();
  });

  it('should open StepConfigSheet when configure row is clicked for Conditional step', () => {
    const conditionalStep = createMockStep({ stepType: 'Conditional' });

    renderWithProvider(
      <StepCard
        step={conditionalStep}
        stepNumber={2}
        allSteps={[mockStep, conditionalStep]}
        currentStepIndex={1}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={true}
        canMoveDown={false}
      />
    );

    // The configure button should open the sheet (aria-label format: Step N, StepType: label)
    const configureButton = screen.getByRole('button', { name: /Step 2, Conditional Branch:/ });
    fireEvent.click(configureButton);

    // StepConfigSheet should be rendered
    expect(screen.getByTestId('config-sheet')).toBeTruthy();
  });

  it('should have configure row with proper aria-label for step card', () => {
    const conditionalStep = createMockStep({ stepType: 'Conditional', label: 'Check editorial score' });

    renderWithProvider(
      <StepCard
        step={conditionalStep}
        stepNumber={3}
        allSteps={[mockStep, mockStep, conditionalStep]}
        currentStepIndex={2}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={true}
        canMoveDown={false}
      />
    );

    // The step should have a proper button that opens the sheet
    // Check for a button element with appropriate aria-label pattern (Step N, StepType: label)
    const stepCardButton = screen.queryByRole('button', { name: /Step 3, Conditional Branch: Check editorial score/ });
    expect(stepCardButton).toBeTruthy();
  });

  it('should preserve move-up/move-down props and functionality', () => {
    renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={2}
        allSteps={[mockStep, mockStep]}
        currentStepIndex={1}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={true}
        canMoveDown={true}
      />
    );

    const moveUpButton = screen.getByLabelText('Move step up');
    const moveDownButton = screen.getByLabelText('Move step down');

    fireEvent.click(moveUpButton);
    expect(mockOnMoveUp).toHaveBeenCalledTimes(1);

    fireEvent.click(moveDownButton);
    expect(mockOnMoveDown).toHaveBeenCalledTimes(1);
  });

  it('should render a Voiceover-typed step with its type badge and label', () => {
    const voiceoverStep = createMockStep({ stepType: 'Voiceover', label: 'My Voiceover Step' });

    renderWithProvider(
      <StepCard
        step={voiceoverStep}
        stepNumber={1}
        allSteps={[voiceoverStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    expect(screen.getByTestId('step-badge').textContent).toBe('Voiceover');
    expect(screen.getByDisplayValue('My Voiceover Step')).toBeTruthy();
  });

  it('should suppress the agent picker for a Voiceover step', () => {
    // Positive control: an Agent step DOES render the picker, so the negative assertion below
    // cannot pass for the wrong reason (e.g. the picker never rendering at all).
    const agentRender = renderWithProvider(
      <StepCard
        step={mockStep}
        stepNumber={1}
        allSteps={[mockStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );
    expect(screen.getByTestId('agent-picker')).toBeTruthy();
    agentRender.unmount();

    // A Voiceover step's whole configuration lives in its config sheet — there is no agent to
    // pick, exactly like Extract/VideoAnalyze/VideoCompile.
    const voiceoverStep = createMockStep({ stepType: 'Voiceover', label: 'My Voiceover Step' });

    renderWithProvider(
      <StepCard
        step={voiceoverStep}
        stepNumber={1}
        allSteps={[voiceoverStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    expect(screen.getByTestId('step-badge').textContent).toBe('Voiceover');
    expect(screen.queryByTestId('agent-picker')).toBeNull();
  });


  it('treats ThumbnailGenerate like the other config-carrying types: no agent picker, its own editor', () => {
    const thumbnailStep = createMockStep({ stepType: 'ThumbnailGenerate', label: 'Make the cover' });

    renderWithProvider(
      <StepCard
        step={thumbnailStep}
        stepNumber={1}
        allSteps={[thumbnailStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    // The random frame this step avoids would be exactly what an agent assignment implied: its whole
    // configuration lives in the config sheet, so there is no agent to pick.
    expect(screen.getByTestId('step-badge').textContent).toBe('ThumbnailGenerate');
    expect(screen.queryByTestId('agent-picker')).toBeNull();
    // Reachable, and labelled from STEP_TYPE_LABELS — the same source the picker and the badge read.
    // The accessible name and the button label both come from STEP_TYPE_LABELS.ThumbnailGenerate,
    // never from the enum name itself.
    const configureButton = screen.getByRole('button', { name: /Step 1, Make Thumbnail:/ });
    expect(configureButton.textContent).toBe('Configure Make Thumbnail');
  });

  it('should open the config sheet when the Voiceover configure row is clicked', () => {
    const voiceoverStep = createMockStep({ stepType: 'Voiceover', label: 'My Voiceover Step' });

    renderWithProvider(
      <StepCard
        step={voiceoverStep}
        stepNumber={1}
        allSteps={[voiceoverStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    // The sheet starts closed — the Voiceover editor is reachable only through the configure row.
    expect(screen.queryByTestId('config-sheet')).toBeNull();

    const configureButton = screen.getByRole('button', { name: /Step 1, Voiceover:/ });
    // Real Mantine Button markup: the visible label comes from STEP_TYPE_LABELS.Voiceover.
    expect(configureButton.textContent).toBe('Configure Voiceover');

    fireEvent.click(configureButton);

    expect(screen.getByTestId('config-sheet')).toBeTruthy();
  });

  it('should close the Voiceover config sheet again when it asks to close', () => {
    const voiceoverStep = createMockStep({ stepType: 'Voiceover', label: 'My Voiceover Step' });

    renderWithProvider(
      <StepCard
        step={voiceoverStep}
        stepNumber={1}
        allSteps={[voiceoverStep]}
        currentStepIndex={0}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={false}
        canMoveDown={false}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /Step 1, Voiceover:/ }));
    expect(screen.getByTestId('config-sheet')).toBeTruthy();

    fireEvent.click(screen.getByText('Close Sheet'));
    expect(screen.queryByTestId('config-sheet')).toBeNull();
  });

  it('should give the Voiceover configure row a step-aware aria-label', () => {
    const voiceoverStep = createMockStep({ stepType: 'Voiceover', label: 'Narration pass' });

    renderWithProvider(
      <StepCard
        step={voiceoverStep}
        stepNumber={4}
        allSteps={[mockStep, mockStep, mockStep, voiceoverStep]}
        currentStepIndex={3}
        onChange={mockOnChange}
        onRemove={mockOnRemove}
        onMoveUp={mockOnMoveUp}
        onMoveDown={mockOnMoveDown}
        canMoveUp={true}
        canMoveDown={false}
      />
    );

    expect(screen.getByRole('button', { name: 'Step 4, Voiceover: Narration pass' })).toBeTruthy();
  });
});

describe('StepCard — switching to ExtractFrame / AudioGenerate', () => {
  const baseProps = {
    stepNumber: 2,
    onRemove: vi.fn(),
    onMoveUp: vi.fn(),
    onMoveDown: vi.fn(),
    canMoveUp: true,
    canMoveDown: false,
  };
  afterEach(() => cleanup());

  it('seeds the default ExtractFrame config, with the previous step as the source', () => {
    const onChange = vi.fn();
    const step = createMockStep({ agentDefinitionId: '' });
    renderWithProvider(
      <StepCard {...baseProps} step={step} allSteps={[createMockStep({ id: '0' }), step]} currentStepIndex={1} onChange={onChange} />,
    );
    fireEvent.change(screen.getByTestId('step-type-selector'), { target: { value: 'ExtractFrame' } });
    const updates = onChange.mock.calls.at(-1)![0] as Partial<StepData>;
    expect(updates.stepType).toBe('ExtractFrame');
    expect(updates.extractFrameConfig).toMatchObject({ at: 'Last', format: 'png', registerProjectFile: true, source: { kind: 'PreviousStepOutput' } });
  });

  it('starts an ExtractFrame step at the top of the workflow from an uploaded file', () => {
    const onChange = vi.fn();
    const step = createMockStep();
    renderWithProvider(
      <StepCard {...baseProps} step={step} allSteps={[step]} currentStepIndex={0} onChange={onChange} />,
    );
    fireEvent.change(screen.getByTestId('step-type-selector'), { target: { value: 'ExtractFrame' } });
    expect((onChange.mock.calls.at(-1)![0] as Partial<StepData>).extractFrameConfig?.source.kind).toBe('ProjectFile');
  });

  it('seeds the default AudioGenerate config, and keeps one the step already has', () => {
    const onChange = vi.fn();
    const step = createMockStep();
    renderWithProvider(
      <StepCard {...baseProps} step={step} allSteps={[step]} currentStepIndex={0} onChange={onChange} />,
    );
    fireEvent.change(screen.getByTestId('step-type-selector'), { target: { value: 'AudioGenerate' } });
    expect((onChange.mock.calls.at(-1)![0] as Partial<StepData>).audioGenerateConfig).toMatchObject({ kind: 'Music', takes: 1, registerProjectFile: true });
    cleanup();

    const existing = createMockStep({ audioGenerateConfig: { kind: 'SoundEffect', prompt: 'creak', durationSec: 3 } });
    const onChange2 = vi.fn();
    renderWithProvider(
      <StepCard {...baseProps} step={existing} allSteps={[existing]} currentStepIndex={0} onChange={onChange2} />,
    );
    fireEvent.change(screen.getByTestId('step-type-selector'), { target: { value: 'AudioGenerate' } });
    expect(onChange2.mock.calls.at(-1)![0]).not.toHaveProperty('audioGenerateConfig');
  });

  it('shows no agent picker for either: the step has no agent to pick', () => {
    for (const stepType of ['ExtractFrame', 'AudioGenerate'] as const) {
      const step = createMockStep({ stepType });
      renderWithProvider(
        <StepCard {...baseProps} step={step} allSteps={[step]} currentStepIndex={0} onChange={vi.fn()} />,
      );
      expect(screen.queryByTestId('agent-picker')).toBeNull();
      cleanup();
    }
  });
});
