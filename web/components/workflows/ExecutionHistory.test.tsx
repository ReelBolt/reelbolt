// @vitest-environment jsdom
/**
 * The run card's two layouts (workflow-builder-and-approval-ux T-001): the full-width row the page
 * always had, and the stacked card the 380px desktop rail needs so its badges and date cannot force
 * a horizontal scrollbar.
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import type { WorkflowExecution } from '@/lib/types/workflow';

const EXECUTION = {
  id: 'e1',
  status: 'Completed',
  startedAt: '2026-10-20T10:00:00Z',
  completedAt: '2026-10-20T10:02:00Z',
  userRequest: 'A vertical teaser for the launch',
  iterationCount: 1,
  styleName: 'Fast social reel',
  outputFormat: 'Vertical',
  outputResolution: '1080p',
  steps: [],
} as unknown as WorkflowExecution;

vi.mock('@/lib/hooks/use-executions', () => ({
  useExecutions: () => ({ data: [EXECUTION], isLoading: false }),
}));

// The card is a Link in the real app; an anchor keeps this test out of the app-router harness.
vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={href} {...rest}>{children}</a>
  ),
}));

import { ExecutionHistory } from './ExecutionHistory';

// Mantine's provider and Tooltip read these in jsdom.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => false,
  }),
});
vi.stubGlobal('ResizeObserver', class {
  observe() {}
  unobserve() {}
  disconnect() {}
});

function renderHistory(compact: boolean) {
  return render(
    <MantineProvider>
      <ExecutionHistory projectId="p1" workflowId="w1" compact={compact} />
    </MantineProvider>,
  );
}

describe('ExecutionHistory layouts', () => {
  it('renders the stacked compact card for the narrow rail', () => {
    renderHistory(true);

    const card = screen.getByText('Run 1').closest('a');
    expect(card?.getAttribute('data-layout')).toBe('compact');
    // The compact card keeps the facts that matter and wraps instead of truncating them away.
    expect(screen.getByText('Fast social reel')).toBeTruthy();
    expect(screen.getByText('A vertical teaser for the launch')).toBeTruthy();
    expect(screen.getByText('1 review round')).toBeTruthy();
  });

  it('keeps the wide row for the full-width page', () => {
    renderHistory(false);

    const card = screen.getByText('Run 1').closest('a');
    expect(card?.getAttribute('data-layout')).toBe('wide');
  });

  it('shows an amber Needs attention badge instead of Passed when the run needs attention', () => {
    const saved = { status: EXECUTION.status };
    (EXECUTION as { status: string }).status = 'Passed';
    (EXECUTION as { needsAttention?: unknown }).needsAttention = { reason: 'x', stepOrder: 3, score: 6, minScore: 8, iterations: 3, message: 'm' };
    try {
      renderHistory(false);
      expect(screen.getByText('Needs attention')).toBeTruthy();
      expect(screen.queryByText('Passed')).toBeNull();
    } finally {
      (EXECUTION as { status: string }).status = saved.status;
      delete (EXECUTION as { needsAttention?: unknown }).needsAttention;
    }
  });
});
