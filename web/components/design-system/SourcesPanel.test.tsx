// @vitest-environment jsdom
/**
 * "Check now" (P3-04, SPEC §9): it calls `POST /check` and reports success. The API, the sources
 * hook and the notifications module are mocked exactly like the other design-system tests.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/api/design-system', () => ({ checkForDrift: vi.fn() }));
vi.mock('@/lib/hooks/use-design-system', () => ({ useDesignSources: vi.fn() }));
const revalidate = vi.hoisted(() => vi.fn());
vi.mock('@/lib/hooks/use-revalidate-design-system', () => ({ useRevalidateDesignSystem: () => revalidate }));

import { notifications } from '@mantine/notifications';
import { checkForDrift } from '@/lib/api/design-system';
import { useDesignSources } from '@/lib/hooks/use-design-system';
import { SourcesPanel } from './SourcesPanel';
import { CHECK_DONE } from './copy';
import { en } from './__tests__/en';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('SourcesPanel check now', () => {
  it('queues a check and reports success', async () => {
    vi.mocked(useDesignSources).mockReturnValue(
      { data: [], error: undefined, isLoading: false, mutate: vi.fn() } as never,
    );
    vi.mocked(checkForDrift).mockResolvedValue(undefined);

    render(
      <MantineProvider>
        <SourcesPanel projectId="p1" canEdit />
      </MantineProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Check now' }));

    await waitFor(() => expect(checkForDrift).toHaveBeenCalledWith('p1'));
    await waitFor(() =>
      expect(notifications.show).toHaveBeenCalledWith(expect.objectContaining({ message: en(CHECK_DONE) })),
    );
  });
});

describe('SourcesPanel honesty and failure', () => {
  it('says the check was queued (not that changes were checked) and refreshes suggestions later', async () => {
    vi.mocked(useDesignSources).mockReturnValue(
      { data: [], error: undefined, isLoading: false, mutate: vi.fn() } as never,
    );
    vi.mocked(checkForDrift).mockResolvedValue(undefined);

    render(
      <MantineProvider>
        <SourcesPanel projectId="p1" canEdit refreshDelaysMs={[15]} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Check now' }));

    await waitFor(() =>
      expect(notifications.show).toHaveBeenCalledWith(
        expect.objectContaining({ message: expect.stringContaining('Check queued') }),
      ),
    );
    expect(vi.mocked(notifications.show).mock.calls.flat().map((c) => JSON.stringify(c)).join()).not.toContain('Changes checked');
    // The proposals the check creates arrive after the job runs, so the profile is revalidated later.
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it('says the sources could not be loaded, with a retry, instead of "No sources recorded"', () => {
    const mutate = vi.fn();
    vi.mocked(useDesignSources).mockReturnValue({ data: undefined, error: new Error('x'), isLoading: false, mutate } as never);
    render(<MantineProvider><SourcesPanel projectId="p1" canEdit /></MantineProvider>);

    expect(screen.queryByText('No sources recorded.')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(mutate).toHaveBeenCalled();
  });

  it('without edit access Check now is disabled and a visible sentence says why', () => {
    vi.mocked(useDesignSources).mockReturnValue({ data: [], error: undefined, isLoading: false, mutate: vi.fn() } as never);
    render(<MantineProvider><SourcesPanel projectId="p1" canEdit={false} /></MantineProvider>);

    const button = screen.getByRole('button', { name: 'Check now' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    const id = button.getAttribute('aria-describedby');
    expect(document.getElementById(id as string)?.textContent).toContain('edit access');
  });
});
