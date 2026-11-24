// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { PinInsertStepConfig, createDefaultPinInsertStepConfig, isPinComplete, MAX_PINS } from './PinInsertStepConfig';
import type { PinInsertStepConfig as Cfg } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: [], error: null, isLoading: false }) }));
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

const steps = [
  { label: 'Analyze', stepType: 'VideoAnalyze' },
  { label: 'Author', stepType: 'Agent' },
  { label: 'Pin', stepType: 'PinInsert' },
] as StepData[];

function renderConfig(config: Cfg = createDefaultPinInsertStepConfig(), onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <PinInsertStepConfig config={config} onChange={onChange} allSteps={steps} currentStepIndex={2} />
    </MantineProvider>,
  );
  return onChange;
}
const last = (m: Mock) => m.mock.calls.at(-1)![0] as Cfg;

describe('createDefaultPinInsertStepConfig', () => {
  it('mirrors the C# record: the previous step\'s screens, one pin for every screen, require applied', () => {
    expect(createDefaultPinInsertStepConfig()).toEqual({
      analysis: { from: 'Previous', stepOrder: null },
      pins: [{ regionId: null, content: { from: 'Step', stepOrder: null } }],
      requireApplied: true,
    });
  });

  it('knows when a pin names its content', () => {
    expect(isPinComplete({ regionId: null, content: { from: 'Step', stepOrder: null } })).toBe(false);
    expect(isPinComplete({ regionId: 'r0', content: { from: 'Step', stepOrder: 2 } })).toBe(true);
    expect(isPinComplete({ regionId: null, content: { from: 'ProjectFile', projectFileId: 'f1' } })).toBe(true);
    expect(isPinComplete({ regionId: null, content: { from: 'StepResult', stepResultId: null } })).toBe(false);
  });
});

describe('PinInsertStepConfig', () => {
  it('names the screen of a pin and adds a second pin with its own screen', () => {
    const onChange = renderConfig();
    fireEvent.change(screen.getByLabelText('Screen'), { target: { value: ' r1 ' } });
    expect(last(onChange).pins[0].regionId).toBe('r1');

    cleanup();
    const onAdd = renderConfig();
    fireEvent.click(screen.getByText('Add content for another screen'));
    const pins = last(onAdd).pins;
    expect(pins).toHaveLength(2);
    expect(pins[1].regionId).toBe('r1'); // only one pin may cover every screen
  });

  it('shows a bad screen id, cannot remove the only pin, and turns requireApplied off', () => {
    const onChange = renderConfig({ ...createDefaultPinInsertStepConfig(), pins: [{ regionId: 'phone', content: { from: 'Step', stepOrder: 2 } }] });
    expect(screen.getByText('A screen id looks like r0, r1, ...')).toBeTruthy();
    expect((screen.getByLabelText('Remove pin 1') as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByLabelText(/Stop if something cannot be put on a screen/));
    expect(last(onChange).requireApplied).toBe(false);
  });

  it('stops adding pins at the server\'s limit', () => {
    const full = { ...createDefaultPinInsertStepConfig(), pins: Array.from({ length: MAX_PINS }, (_, i) => ({ regionId: `r${i}`, content: { from: 'Step' as const, stepOrder: 2 } })) };
    renderConfig(full);
    expect((screen.getByText('Add content for another screen').closest('button') as HTMLButtonElement).disabled).toBe(true);
  });
});
