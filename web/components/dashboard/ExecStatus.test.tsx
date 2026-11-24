// @vitest-environment jsdom
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ExecStatus } from './sections';

const attention = { reason: 'review_below_minimum', stepOrder: 4, score: 5, minScore: 8, iterations: 3, message: 'The review stopped.' };

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

function renderBadge(props: Parameters<typeof ExecStatus>[0]) {
  return render(<MantineProvider><ExecStatus {...props} /></MantineProvider>);
}

describe('ExecStatus on the dashboard run lists', () => {
  it('shows the amber Needs attention badge, filled so its text stays readable, for a Passed run with a runAttention value', () => {
    renderBadge({ status: 'Passed', needsAttention: attention });
    const badge = screen.getByText('Needs attention').closest('.mantine-Badge-root');
    expect(badge?.getAttribute('data-variant')).toBe('filled');
  });

  it('keeps a plain Passed badge for a run without attention', () => {
    renderBadge({ status: 'Passed', needsAttention: null });
    expect(screen.getByText('Passed')).toBeTruthy();
    expect(screen.queryByText('Needs attention')).toBeNull();
  });
});
