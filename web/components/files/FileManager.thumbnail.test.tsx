// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { ProjectFile } from '@/lib/types/project';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
const queue = { entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() };
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => queue }));
vi.mock('@/lib/hooks/use-file-drop', () => ({ useWindowFileDrop: () => false }));

import { FileManager } from './FileManager';

const file = (id: string, name: string, mimeType: string): ProjectFile => ({
  id, projectId: 'p', originalFileName: name, directoryPath: undefined, category: 'userFiles', mimeType,
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
});

beforeEach(() => {
  // matches: true = desktop, so the list view with per-file menus is the one rendered.
  global.matchMedia = ((q: string) => ({ matches: true, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function renderManager(files: ProjectFile[], onEditAsThumbnail?: (f: ProjectFile) => void) {
  render(
    <MantineProvider>
      <Notifications />
      <FileManager projectId="p" files={files} onChanged={vi.fn()} onEditAsThumbnail={onEditAsThumbnail} />
    </MantineProvider>,
  );
}

describe('Edit as thumbnail', () => {
  it('is offered on an image and hands that file to the page', async () => {
    const open = vi.fn();
    const image = file('i1', 'cover.png', 'image/png');
    renderManager([image, file('v1', 'clip.mp4', 'video/mp4')], open);
    fireEvent.click(screen.getAllByRole('button', { name: /Actions for cover\.png/ })[0]);
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Edit as picture' }));
    expect(open).toHaveBeenCalledWith(image);
  });

  it('is not offered on a video', async () => {
    renderManager([file('v1', 'clip.mp4', 'video/mp4')], vi.fn());
    fireEvent.click(screen.getAllByRole('button', { name: /Actions for clip\.mp4/ })[0]);
    await screen.findByRole('menuitem', { name: 'Download' });
    expect(screen.queryByRole('menuitem', { name: 'Edit as picture' })).toBeNull();
  });

  it('is not offered at all when the page gives no handler', async () => {
    renderManager([file('i1', 'cover.png', 'image/png')]);
    fireEvent.click(screen.getAllByRole('button', { name: /Actions for cover\.png/ })[0]);
    await screen.findByRole('menuitem', { name: 'Download' });
    expect(screen.queryByRole('menuitem', { name: 'Edit as picture' })).toBeNull();
  });
});
