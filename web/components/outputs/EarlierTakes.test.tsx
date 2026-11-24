// @vitest-environment jsdom
/**
 * Takes: the Renders tab shows each video's latest take, with earlier versions (earlier runs, review-loop
 * passes, retries) collapsed under "N earlier versions", still playable, and deletable only after a
 * confirmation. "Delete earlier versions" keeps the latest take and every pinned one.
 */
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach, beforeEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { OutputVideo } from '@/lib/api/outputs';
import { ProjectRendersTab } from '@/components/projects/ProjectSections';
import type { Project } from '@/lib/types/project';

const api = vi.hoisted(() => ({
  deleteOutputTake: vi.fn().mockResolvedValue(undefined),
  pruneOutputTakes: vi.fn().mockResolvedValue({ deleted: 1, kept: 2 }),
  setOutputTakePinned: vi.fn().mockResolvedValue({ pinned: true }),
}));
vi.mock('@/lib/api/outputs', async (importOriginal) => ({ ...(await importOriginal<object>()), ...api }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

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
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
  HTMLMediaElement.prototype.load = vi.fn();
});
beforeEach(() => Object.values(api).forEach((m) => m.mockClear()));
afterEach(() => cleanup());

const project = { id: 'p1', name: 'Reel' } as Project;

function take(id: string, at: string, extra: Partial<OutputVideo> = {}): OutputVideo {
  return {
    stepResultId: id, workflowExecutionId: `e-${id}`, workflowStepId: 'compile', storageKey: `k-${id}`, fileName: `${id}.mp4`,
    producedAt: at, workflowDefinitionId: 'wf', workflowName: '15s reel', takeGroupKey: 'wf:3:VideoCompile', ...extra,
  };
}

const outputs = [
  take('latest', '2026-10-05T11:00:00Z', { runNumber: 3, isLatestTake: true, takeIndex: 1, takeCount: 3 }),
  take('pass1', '2026-10-05T10:50:00Z', { runNumber: 3, isIntermediate: true, isLatestTake: false, takeIndex: 2 }),
  take('run1', '2026-10-05T09:00:00Z', { runNumber: 1, isLatestTake: false, isPinned: true, takeIndex: 3 }),
];

function renderTab(onChanged = vi.fn()) {
  render(<MantineProvider><ProjectRendersTab project={project} outputs={outputs} onChanged={onChanged} /></MantineProvider>);
  return onChanged;
}

describe('Renders — takes', () => {
  it('lists one video (its latest take) and folds the rest under "2 earlier versions"', () => {
    renderTab();
    expect(screen.getAllByRole('listitem').filter((el) => el.tagName === 'BUTTON')).toHaveLength(1);
    expect(screen.getByText(/1 video · 2 earlier versions/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '2 earlier versions' }));
    expect(screen.getByText('Version in progress')).toBeTruthy();
    expect(screen.getByText('Pinned')).toBeTruthy();
  });

  it('plays an earlier version when asked', () => {
    renderTab();
    fireEvent.click(screen.getByRole('button', { name: '2 earlier versions' }));
    fireEvent.click(screen.getByRole('button', { name: /^Play Run 1/ }));
    expect(screen.getByText('You are watching an earlier version of this video.')).toBeTruthy();
  });

  it('deletes earlier versions only after confirming, and refreshes', async () => {
    const onChanged = renderTab();
    fireEvent.click(screen.getByRole('button', { name: 'Delete earlier versions' }));
    expect(api.pruneOutputTakes).not.toHaveBeenCalled();
    expect(await screen.findByText(/deletes 1 earlier version of this video\. The latest version and pinned versions stay\./)).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(api.pruneOutputTakes).toHaveBeenCalledWith('p1', 'wf:3:VideoCompile'));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('offers no delete for a pinned take but lets it be unpinned', async () => {
    renderTab();
    fireEvent.click(screen.getByRole('button', { name: '2 earlier versions' }));
    expect(screen.queryByRole('button', { name: /^Delete Run 1/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /^Unpin Run 1/ }));
    await waitFor(() => expect(api.setOutputTakePinned).toHaveBeenCalledWith('p1', 'run1', false));
  });
});
