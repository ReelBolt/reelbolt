// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { ProjectFile } from '@/lib/types/project';
import type { PendingUpload } from '@/lib/files/drop';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const MIB = 1024 * 1024;
const hoisted = vi.hoisted(() => ({
  usage: { data: undefined as unknown, error: undefined as unknown },
  drop: { current: null as null | ((items: unknown[]) => void) },
}));

vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
const queue = { entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() };
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => queue }));
vi.mock('@/lib/hooks/use-file-drop', () => ({
  useWindowFileDrop: (cb: (items: unknown[]) => void) => { hoisted.drop.current = cb; return false; },
}));
vi.mock('@/lib/hooks/use-storage', () => ({ useStorageUsage: () => hoisted.usage }));

import { FileManager } from './FileManager';

const existing: ProjectFile = {
  id: 'f1', projectId: 'p', originalFileName: 'dup.txt', directoryPath: undefined, category: 'userFiles', mimeType: 'text/plain',
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
};

function mk(name: string, mb: number, type = 'application/octet-stream'): File {
  const f = new File(['x'], name, { type });
  Object.defineProperty(f, 'size', { value: mb * MIB });
  return f;
}
const pending = (...files: File[]): PendingUpload[] => files.map((file) => ({ file, relativePath: file.name }));

beforeEach(() => {
  hoisted.usage = { data: { zipUnpackLimits: { maxMb: 64 } }, error: undefined };
  queue.enqueue.mockReset();
  global.matchMedia = ((q: string) => ({ matches: true, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function renderManager() {
  return render(
    <MantineProvider>
      <Notifications />
      <FileManager projectId="p" files={[existing]} onChanged={vi.fn()} />
    </MantineProvider>,
  );
}

/** The three ways a person starts an upload on the Files page. */
const entryPoints: Record<string, (c: HTMLElement, files: File[]) => void> = {
  'the file picker': (c, files) => {
    fireEvent.change(c.querySelectorAll('input[type=file]')[0], { target: { files } });
  },
  'the folder picker': (c, files) => {
    fireEvent.change(c.querySelectorAll('input[type=file]')[1], { target: { files } });
  },
  'drag and drop': (_c, files) => {
    act(() => hoisted.drop.current?.(pending(...files)));
  },
};

describe.each(Object.keys(entryPoints))('zip size warning via %s', (via) => {
  const start = entryPoints[via];

  it('opens the modal before anything uploads for a zip over the limit', async () => {
    const { container } = renderManager();
    start(container, [mk('code.zip', 173, 'application/zip')]);
    const modal = await screen.findByRole('dialog');
    expect(within(modal).getByText(/code\.zip/)).toBeTruthy();
    expect(within(modal).getByText(/64 MB/)).toBeTruthy();
    expect(queue.enqueue).not.toHaveBeenCalled();
  });

  it('Upload anyway continues, and the duplicate-name check still runs', async () => {
    const { container } = renderManager();
    start(container, [mk('code.zip', 173), mk('dup.txt', 1)]);
    fireEvent.click(await screen.findByRole('button', { name: 'Upload anyway' }));
    // dup.txt already exists, so the duplicate prompt follows instead of an immediate upload.
    expect(await screen.findByRole('button', { name: 'Skip duplicates' })).toBeTruthy();
    expect(queue.enqueue).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Keep both' }));
    await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
    expect(queue.enqueue.mock.calls[0][0]).toHaveLength(2);
  });

  it('Upload anyway uploads straight away when nothing is duplicated', async () => {
    const { container } = renderManager();
    start(container, [mk('code.zip', 173)]);
    fireEvent.click(await screen.findByRole('button', { name: 'Upload anyway' }));
    await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
  });

  it('Cancel upload sends nothing', async () => {
    const { container } = renderManager();
    start(container, [mk('code.zip', 173), mk('notes.md', 1)]);
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel upload' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(queue.enqueue).not.toHaveBeenCalled();
  });

  it('can upload the other files without the oversized zip', async () => {
    const { container } = renderManager();
    start(container, [mk('code.zip', 173), mk('notes.md', 1)]);
    fireEvent.click(await screen.findByRole('button', { name: 'Upload without the zip' }));
    await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
    expect(queue.enqueue.mock.calls[0][0].map((i: PendingUpload) => i.file.name)).toEqual(['notes.md']);
  });

  it('offers no "without the zip" button when the zip is the only file', async () => {
    const { container } = renderManager();
    start(container, [mk('code.zip', 173)]);
    await screen.findByRole('dialog');
    expect(screen.queryByRole('button', { name: /Upload without the zip/ })).toBeNull();
  });

  it('opens no modal for a zip of exactly the limit', async () => {
    const { container } = renderManager();
    start(container, [mk('edge.zip', 64)]);
    await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('opens no modal when the limit is missing or its lookup failed', async () => {
    for (const usage of [{ data: {}, error: undefined }, { data: undefined, error: new Error('down') }]) {
      hoisted.usage = usage;
      queue.enqueue.mockReset();
      const { container, unmount } = renderManager();
      start(container, [mk('huge.zip', 900)]);
      await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
      expect(screen.queryByRole('dialog')).toBeNull();
      unmount();
    }
  });
});
