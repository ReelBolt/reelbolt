// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { buildFolderTree } from '@/lib/files/model';
import type { ProjectFile } from '@/lib/types/project';
import { FolderTree, INTERNAL_DRAG_TYPE } from './FolderTree';
import { UploadTray } from './UploadTray';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const file = (dir: string): ProjectFile => ({
  id: dir, projectId: 'p', originalFileName: 'a.txt', directoryPath: dir, category: 'userFiles', mimeType: 'text/plain',
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
});

const wrap = (ui: React.ReactElement) => <MantineProvider>{ui}</MantineProvider>;

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('FolderTree', () => {
  const root = buildFolderTree([file('footage'), file('footage/day1')]);

  it('selects a folder and marks the current one', () => {
    const onSelect = vi.fn();
    render(wrap(<FolderTree root={root} current="footage" onSelect={onSelect} onFolderAction={vi.fn()} onMoveDrop={vi.fn()} />));
    expect(screen.getByText('footage').closest('button')?.getAttribute('aria-current')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: /All files/ }));
    expect(onSelect).toHaveBeenCalledWith('');
  });

  it('reveals children of the open folder and accepts internal file drops', () => {
    const onMoveDrop = vi.fn();
    render(wrap(<FolderTree root={root} current="footage" onSelect={vi.fn()} onFolderAction={vi.fn()} onMoveDrop={onMoveDrop} />));
    const day1 = screen.getByText('day1').closest('button')!;
    fireEvent.drop(day1, { dataTransfer: { types: [INTERNAL_DRAG_TYPE], getData: () => JSON.stringify(['f1', 'f2']) } });
    expect(onMoveDrop).toHaveBeenCalledWith('footage/day1', ['f1', 'f2']);
  });
});

describe('UploadTray', () => {
  it('renders nothing without uploads and offers retry for failures', () => {
    const { container, rerender } = render(wrap(<UploadTray entries={[]} onRetry={vi.fn()} onClear={vi.fn()} />));
    expect(container.querySelector('aside')).toBeNull();
    const onRetry = vi.fn();
    rerender(wrap(<UploadTray entries={[{ id: 'u1', name: 'clip.mp4', destination: 'raw', size: 2048, progress: 0, status: 'error', error: 'Network error' }]} onRetry={onRetry} onClear={vi.fn()} />));
    expect(screen.getByText('Network error')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Retry/ }));
    expect(onRetry).toHaveBeenCalledWith('u1');
  });
});
