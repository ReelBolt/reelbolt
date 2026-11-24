// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { ProjectFile } from '@/lib/types/project';
import { ApiError } from '@/lib/api/client';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
const queue = { entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() };
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => queue }));
vi.mock('@/lib/hooks/use-file-drop', () => ({ useWindowFileDrop: () => false }));
const api = vi.hoisted(() => ({ getFilePictureUsage: vi.fn(), deleteFile: vi.fn() }));
vi.mock('@/lib/api/files', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/files')>()),
  getFilePictureUsage: api.getFilePictureUsage,
  deleteFile: api.deleteFile,
}));

import { FileManager } from './FileManager';

const file = (id: string, name: string, mimeType: string): ProjectFile => ({
  id, projectId: 'p', originalFileName: name, directoryPath: undefined, category: 'userFiles', mimeType,
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
});

beforeEach(() => {
  api.getFilePictureUsage.mockReset();
  api.deleteFile.mockReset();
  global.matchMedia = ((q: string) => ({ matches: true, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

async function askToDelete(files: ProjectFile[], name: string) {
  render(
    <MantineProvider>
      <Notifications />
      <FileManager projectId="p" files={files} onChanged={vi.fn()} />
    </MantineProvider>,
  );
  fireEvent.click(screen.getAllByRole('button', { name: new RegExp(`Actions for ${name.replace('.', '\\.')}`) })[0]);
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
  return within(await screen.findByRole('dialog'));
}

describe('deleting a photo that pictures use (T-050/T-051)', () => {
  it('says how many pictures use the photo and that they keep their own copy', async () => {
    api.getFilePictureUsage.mockResolvedValue(3);
    const dialog = await askToDelete([file('i1', 'cover.png', 'image/png')], 'cover.png');
    expect(await dialog.findByText(/3 pictures use this photo\. They keep their own copy\./)).toBeTruthy();
    expect(api.getFilePictureUsage).toHaveBeenCalledWith('p', 'i1');
  });

  it('uses the singular for one picture', async () => {
    api.getFilePictureUsage.mockResolvedValue(1);
    const dialog = await askToDelete([file('i1', 'cover.png', 'image/png')], 'cover.png');
    expect(await dialog.findByText(/1 picture uses this photo\. It keeps its own copy\./)).toBeTruthy();
  });

  it('says nothing about pictures when none use it, or the count cannot be read', async () => {
    api.getFilePictureUsage.mockResolvedValue(0);
    const dialog = await askToDelete([file('i1', 'cover.png', 'image/png')], 'cover.png');
    await waitFor(() => expect(api.getFilePictureUsage).toHaveBeenCalled());
    expect(dialog.queryByText(/use this photo|uses this photo/)).toBeNull();
    cleanup();
    api.getFilePictureUsage.mockRejectedValue(new Error('offline'));
    const again = await askToDelete([file('i1', 'cover.png', 'image/png')], 'cover.png');
    await waitFor(() => expect(api.getFilePictureUsage).toHaveBeenCalledTimes(2));
    expect(again.getByRole('button', { name: /Delete/ })).toBeTruthy();
    expect(again.queryByText(/use this photo|uses this photo/)).toBeNull();
  });

  it('does not ask about a file that is not an image', async () => {
    await askToDelete([file('v1', 'clip.mp4', 'video/mp4')], 'clip.mp4');
    expect(api.getFilePictureUsage).not.toHaveBeenCalled();
  });
});

describe('a multi-select delete the server refuses (T-051)', () => {
  const sentence = 'This photo is used by 3 pictures. Keeping their copies needs more storage than your plan has left. Delete some pictures or free up space, then try again.';
  const refusal = () => new ApiError(sentence, 409, [], { code: 'PICTURE_COPIES_OVER_QUOTA', message: sentence, pictures: 3 });

  async function deleteSelected(files: ProjectFile[]) {
    api.getFilePictureUsage.mockResolvedValue(0);
    render(
      <MantineProvider>
        <Notifications />
        <FileManager projectId="p" files={files} onChanged={vi.fn()} />
      </MantineProvider>,
    );
    fireEvent.click(screen.getAllByRole('checkbox', { name: 'Select all files in this view' })[0]);
    fireEvent.click(within(screen.getByRole('region', { name: 'Selection' })).getByRole('button', { name: /Delete/ }));
    const dialog = within(await screen.findByRole('dialog'));
    fireEvent.click(dialog.getByRole('button', { name: /Delete/ }));
  }

  it("shows the server's sentence instead of only a count", async () => {
    api.deleteFile.mockImplementation((_p: string, id: string) => (id === 'i1' ? Promise.reject(refusal()) : Promise.resolve()));
    await deleteSelected([file('i1', 'cover.png', 'image/png'), file('v1', 'clip.mp4', 'video/mp4')]);
    expect(await screen.findByText(sentence)).toBeTruthy();
    expect(screen.queryByText(/1 of 2 failed/)).toBeNull();
    expect(screen.queryByText(/PICTURE_COPIES_OVER_QUOTA|i1/)).toBeNull();
  });

  it('says several refused photos in one sentence', async () => {
    api.deleteFile.mockImplementation(() => Promise.reject(refusal()));
    await deleteSelected([file('i1', 'a.png', 'image/png'), file('i2', 'b.png', 'image/png'), file('i3', 'c.png', 'image/png')]);
    expect(await screen.findByText(/3 photos were kept because their pictures' copies need more storage than your plan has left\./)).toBeTruthy();
  });

  it('keeps the count for failures without a reason', async () => {
    api.deleteFile.mockImplementation((_p: string, id: string) => (id === 'i1' ? Promise.reject(new Error('Failed to fetch')) : Promise.resolve()));
    await deleteSelected([file('i1', 'cover.png', 'image/png'), file('v1', 'clip.mp4', 'video/mp4')]);
    expect(await screen.findByText('1 of 2 failed.')).toBeTruthy();
  });
});
