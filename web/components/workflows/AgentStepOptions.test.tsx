// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { AgentStepOptions } from './AgentStepOptions';
import type { StepData } from './WorkflowStepList';

const runs = vi.hoisted(() => ({
  data: [
    { id: 'e2', workflowId: 'w1', workflowName: 'Launch film', status: 'Passed', startedAt: '2026-10-02T10:00:00Z' },
    { id: 'e1', workflowId: 'w1', workflowName: 'Launch film', status: 'Failed', startedAt: '2026-10-01T10:00:00Z' },
  ] as unknown[] | undefined,
  enabledSeen: [] as boolean[],
}));
vi.mock('@/lib/hooks/use-executions', () => ({
  useProjectExecutions: (_projectId: string | undefined, enabled: boolean) => {
    runs.enabledSeen.push(enabled);
    return { data: enabled ? runs.data : undefined, isLoading: false };
  },
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
afterEach(() => { cleanup(); runs.enabledSeen = []; });

function renderOptions(step: Pick<StepData, 'continueComposition' | 'registerOutputAsProjectFile'> = {}) {
  const onChange = vi.fn();
  render(
    <MantineProvider>
      <AgentStepOptions step={step} onChange={onChange} projectId="p1" />
    </MantineProvider>,
  );
  return onChange;
}

describe('AgentStepOptions', () => {
  it('is off by default and does not load the project\'s runs until continuation is switched on', () => {
    renderOptions();
    expect((screen.getByRole('switch', { name: /Continue from an earlier run/ }) as HTMLInputElement).checked).toBe(false);
    expect((screen.getByRole('switch', { name: /Save output as a project file/ }) as HTMLInputElement).checked).toBe(false);
    expect(screen.queryByRole('combobox', { name: /Run to continue from/ })).toBeNull();
    expect(runs.enabledSeen.every((e) => e === false)).toBe(true);
  });

  it('switching continuation on starts an empty choice (nothing is sent until a run is picked)', () => {
    const onChange = renderOptions();
    fireEvent.click(screen.getByRole('switch', { name: /Continue from an earlier run/ }));
    expect(onChange).toHaveBeenCalledWith({ continueComposition: { executionId: null } });
  });

  it('lists this project\'s runs and stores the picked execution', () => {
    const onChange = renderOptions({ continueComposition: { executionId: null } });
    fireEvent.click(screen.getByRole('combobox', { name: /Run to continue from/ }));
    const options = screen.getAllByRole('option');
    expect(options).toHaveLength(2);
    expect(options[0].textContent).toContain('Launch film');
    expect(options[0].textContent).toContain('Passed');
    fireEvent.click(options[1]);
    expect(onChange).toHaveBeenCalledWith({ continueComposition: { executionId: 'e1' } });
  });

  it('keeps a stored run that is no longer listed visible instead of blanking', () => {
    renderOptions({ continueComposition: { executionId: 'gone' } });
    expect((screen.getByRole('combobox', { name: /Run to continue from/ }) as HTMLInputElement).value).toBe('A run that is no longer listed');
  });

  it('explains a version-based continuation instead of offering a run', () => {
    renderOptions({ continueComposition: { versionId: 'v-7', executionId: null } });
    expect(screen.queryByRole('combobox', { name: /Run to continue from/ })).toBeNull();
    expect(screen.getByText(/saved composition version \(v-7\)/)).toBeTruthy();
  });

  it('switching continuation off clears it, and save-as-file toggles independently', () => {
    const onChange = renderOptions({ continueComposition: { executionId: 'e1' } });
    fireEvent.click(screen.getByRole('switch', { name: /Continue from an earlier run/ }));
    expect(onChange).toHaveBeenCalledWith({ continueComposition: null });
    fireEvent.click(screen.getByRole('switch', { name: /Save output as a project file/ }));
    expect(onChange).toHaveBeenCalledWith({ registerOutputAsProjectFile: true });
  });
});
