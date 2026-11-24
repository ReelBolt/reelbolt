// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { StepType } from '@/lib/types/workflow';
import { STEP_TYPE_LABELS, SYSTEM_STEP_TYPES, isAuthorableStepType } from '@/lib/utils/constants';
import { AddStepModal } from './AddStepModal';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

describe('AddStepModal', () => {
  it('offers every authorable step type and never the system "Revise" step', async () => {
    render(
      <MantineProvider>
        <AddStepModal opened onClose={vi.fn()} onAdd={vi.fn()} />
      </MantineProvider>,
    );
    await screen.findByText('Choose a step type to add to your workflow');

    expect(SYSTEM_STEP_TYPES.has('Revise')).toBe(true);
    expect(isAuthorableStepType('Revise')).toBe(false);
    for (const stepType of Object.keys(STEP_TYPE_LABELS) as StepType[]) {
      const offered = screen.queryAllByText(STEP_TYPE_LABELS[stepType], { selector: 'p' }).length > 0;
      expect(offered, stepType).toBe(isAuthorableStepType(stepType));
    }
    expect(screen.queryByText('Changes')).toBeNull();
  });
});

describe('AddStepModal — frame and audio steps', () => {
  it.each([
    ['ExtractFrame', 'Grab a frame'],
    ['AudioGenerate', 'Generate music or sound'],
    ['ImageGenerate', 'Generate or edit a picture'],
    ['AudioCleanup', 'Clean up audio'],
    ['VideoEnhance', 'Enhance video'],
    ['PinInsert', 'Put on screens'],
  ] as const)('adds a %s step from its plain-language entry', (stepType, label) => {
    const onAdd = vi.fn();
    render(
      <MantineProvider>
        <AddStepModal opened onClose={vi.fn()} onAdd={onAdd} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByText(label, { selector: 'p' }).closest('button')!);
    expect(onAdd).toHaveBeenCalledWith(stepType);
  });

  it('waits for the built-in agent list like the other deterministic steps', () => {
    render(
      <MantineProvider>
        <AddStepModal opened onClose={vi.fn()} onAdd={vi.fn()} nonLlmStepsDisabled />
      </MantineProvider>,
    );
    for (const label of ['Grab a frame', 'Generate music or sound', 'Put on screens']) {
      expect((screen.getByText(label, { selector: 'p' }).closest('button') as HTMLButtonElement).disabled).toBe(true);
    }
  });
});
