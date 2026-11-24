// @vitest-environment jsdom

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup, within, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { WorkflowDiffPreview } from './WorkflowDiffPreview';
import type { WorkflowEditDiff } from '../../lib/types/workflow-assistant';

const currentSteps = [
  { label: 'Analyze code structure', stepType: 'Agent' },
  { label: 'Write the script', stepType: 'Agent' },
  { label: 'Author the video', stepType: 'Agent' },
];

const fullDiff: WorkflowEditDiff = {
  confirmationToken: 'token123',
  additions: [{ stepOrder: 4, agentDefinitionId: 'agent-4', stepType: 'ReviewLoop', summary: 'Review' }],
  removals: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Analyze code structure' }],
  modifications: [{ stepOrder: 2, agentDefinitionId: 'agent-2', stepType: 'Agent', summary: 'Tightened prompt' }],
  proposedSteps: [
    { agentDefinitionId: 'agent-x', stepOrder: 1, label: 'Analyze code structure', stepType: 'Agent' },
    { agentDefinitionId: 'agent-2', stepOrder: 2, label: 'Write a punchy script', stepType: 'Agent' },
    { agentDefinitionId: 'agent-3', stepOrder: 3, label: 'Author the video', stepType: 'Agent' },
    { agentDefinitionId: 'agent-4', stepOrder: 4, label: 'Review', stepType: 'ReviewLoop' },
  ],
};

function renderPreview(diff: WorkflowEditDiff, props: Partial<Parameters<typeof WorkflowDiffPreview>[0]> = {}) {
  const onApply = vi.fn(() => Promise.resolve());
  const onDiscard = vi.fn();
  render(
    <MantineProvider>
      <WorkflowDiffPreview diff={diff} currentSteps={currentSteps} onApply={onApply} onDiscard={onDiscard} {...props} />
    </MantineProvider>,
  );
  return { onApply, onDiscard };
}

describe('WorkflowDiffPreview', () => {
  beforeEach(() => {
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('groups added, changed and removed steps under labelled lists, and never calls onApply before Apply is clicked', () => {
    const { onApply, onDiscard } = renderPreview(fullDiff);
    expect(onApply).not.toHaveBeenCalled();

    const added = screen.getByRole('list', { name: /added/i });
    const changed = screen.getByRole('list', { name: /changed/i });
    const removed = screen.getByRole('list', { name: /removed/i });

    // Additions are named from the proposal, removals from the canvas.
    expect(within(added).getByText('Review')).toBeDefined();
    expect(within(added).getByText(/review loop/i)).toBeDefined();
    expect(within(removed).getByText('Analyze code structure')).toBeDefined();
    // A change that renames the step shows before -> after, plus the server's summary.
    expect(within(changed).getByText('Write the script')).toBeDefined();
    expect(within(changed).getByText('Write a punchy script')).toBeDefined();
    expect(within(changed).getByRole('img', { name: 'becomes' })).toBeDefined();
    expect(within(changed).getByText(/tightened prompt/i)).toBeDefined();

    fireEvent.click(screen.getByTestId('apply-button'));
    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onDiscard).not.toHaveBeenCalled();
  });

  it('marks each kind with a word, not colour alone', () => {
    renderPreview(fullDiff);
    const region = screen.getByRole('region', { name: 'Proposed changes' });
    for (const word of ['Added', 'Changed', 'Removed']) {
      expect(within(region).getAllByText(word).length).toBeGreaterThan(0);
    }
  });

  it('calls onDiscard when Discard button is clicked', () => {
    const { onDiscard, onApply } = renderPreview({
      confirmationToken: 'token123',
      additions: [],
      removals: [],
      modifications: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Test modification' }],
    });

    fireEvent.click(screen.getByTestId('discard-button'));
    expect(onDiscard).toHaveBeenCalledTimes(1);
    expect(onApply).not.toHaveBeenCalled();
  });

  it('disables Apply and Discard while onApply is pending', async () => {
    let resolveApply: (() => void) | null = null;
    const onApply = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveApply = resolve;
        }),
    );
    renderPreview(fullDiff, { onApply });

    const applyButton = screen.getByTestId('apply-button') as HTMLButtonElement;
    const discardButton = screen.getByTestId('discard-button') as HTMLButtonElement;

    fireEvent.click(applyButton);
    expect(applyButton.disabled).toBe(true);
    expect(discardButton.disabled).toBe(true);

    await act(async () => {
      (resolveApply as unknown as () => void)();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(applyButton.disabled).toBe(false);
    expect(discardButton.disabled).toBe(false);
  });

  it('only renders groups that have content', () => {
    renderPreview({
      confirmationToken: 'token123',
      additions: [{ stepOrder: 1, agentDefinitionId: 'agent-1', stepType: 'Agent', summary: 'Add video processing step' }],
      removals: [],
      modifications: [],
    });

    expect(screen.getByRole('list', { name: /added/i })).toBeDefined();
    expect(screen.queryByRole('list', { name: /removed/i })).toBeNull();
    expect(screen.queryByRole('list', { name: /changed/i })).toBeNull();
    // Without proposedSteps the entry still gets a readable name (its summary).
    expect(screen.getByText('Add video processing step')).toBeDefined();
  });

  it('collapses and expands the step list from its header button', () => {
    renderPreview(fullDiff);
    const toggle = screen.getByRole('button', { name: /proposed changes/i });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    // Apply stays reachable while collapsed.
    expect(screen.getByTestId('apply-button')).toBeDefined();
  });

  it('announces the proposal to screen readers after mounting', () => {
    vi.useFakeTimers();
    renderPreview(fullDiff);
    act(() => {
      vi.advanceTimersByTime(200);
    });
    expect(screen.getByText(/proposed changes to 3 steps: 1 added, 1 changed, 1 removed/i)).toBeDefined();
  });
});
