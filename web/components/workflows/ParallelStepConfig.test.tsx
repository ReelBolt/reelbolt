// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { ParallelStepConfig } from './ParallelStepConfig';
import React from 'react';
import type { Mock } from 'vitest';

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
});

// Mock the useAgents hook
vi.mock('@/lib/hooks/use-agents', () => ({
  useAgents: () => ({
    data: [
      { id: 'agent-1', name: 'Agent One' },
      { id: 'agent-2', name: 'Agent Two' },
      { id: 'agent-3', name: 'Agent Three' },
    ],
  }),
}));

// Mock AgentPicker to a simple test double that calls onChange on button click
vi.mock('./AgentPicker', () => ({
  AgentPicker: ({ onChange, placeholder }: { onChange: (id: string) => void; placeholder?: string }) =>
    React.createElement(
      'button',
      {
        onClick: () => onChange('agent-1'),
        'data-testid': 'agent-picker-button',
      },
      placeholder || 'Select agent',
    ),
}));

const renderWithMantine = (component: React.ReactElement) => {
  return render(<MantineProvider>{component}</MantineProvider>);
};

describe('ParallelStepConfig', () => {
  it('emits parallelAgentIds and agentDefinitionId patch on agent selection via real addAgent function', () => {
    const handleChange = vi.fn();

    renderWithMantine(
      <ParallelStepConfig
        parallelAgentIds={[]}
        onChange={handleChange}
      />,
    );

    // Verify initial render
    expect(screen.getByText(/Agents run in parallel/)).toBeTruthy();

    // Find and click the mocked AgentPicker button
    // This triggers the mocked onChange which calls the real addAgent function
    const pickerButton = screen.getByTestId('agent-picker-button');
    expect(pickerButton).toBeTruthy();

    fireEvent.click(pickerButton);

    // Verify that handleChange was called by the real addAgent function
    expect(handleChange).toHaveBeenCalled();

    // Get the actual argument passed to onChange from the real addAgent call
    const callArg = (handleChange as Mock).mock.calls[0][0] as Record<string, unknown>;

    // Verify the patch contains exactly parallelAgentIds and agentDefinitionId
    expect(callArg).toEqual({
      parallelAgentIds: ['agent-1'],
      agentDefinitionId: 'agent-1',
    });

    // Verify that no other unrelated StepData keys are included
    expect(Object.keys(callArg).sort()).toEqual(['agentDefinitionId', 'parallelAgentIds']);
  });
});
