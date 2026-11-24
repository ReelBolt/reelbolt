// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import type { ProjectFile } from '@/lib/types/project';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
const queue = { entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() };
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => queue }));
vi.mock('@/lib/hooks/use-file-drop', () => ({ useWindowFileDrop: () => false }));
const renameFile = vi.fn();
vi.mock('@/lib/api/files', async (orig) => ({ ...(await orig<typeof import('@/lib/api/files')>()), renameFile: (...a: unknown[]) => renameFile(...a) }));

import { FileManager } from './FileManager';
import { renameErrorMessage, validateFileName as validateMsg } from './RenameFileDialog';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import type { Msg } from '@/i18n/msg';

const tEn = createTranslator({ locale: 'en', messages: loadMessages('en') }) as unknown as (k: string, v?: Record<string, unknown>) => string;
const en = (m: Msg | null): string | null => (m ? tEn(m.$msg, m.values as Record<string, unknown>) : null);
const validateFileName = (n: string) => en(validateMsg(n));
const renameMessage = (e: unknown, n: string) => en(renameErrorMessage(e, n)) ?? '';

const file = (id: string, name: string, dir: string | undefined): ProjectFile => ({
  id, projectId: 'p', originalFileName: name, directoryPath: dir, category: 'userFiles', mimeType: 'video/mp4',
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
});

beforeEach(() => {
  renameFile.mockReset();
  global.matchMedia = ((q: string) => ({ matches: true, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function renderManager(files: ProjectFile[], onChanged = vi.fn()) {
  render(
    <MantineProvider>
      <Notifications />
      <FileManager projectId="p" files={files} onChanged={onChanged} />
    </MantineProvider>,
  );
  return onChanged;
}

describe('file counts', () => {
  it('pluralises correctly', () => {
    expect(tEn('files.manager.count', { count: 1 })).toBe('1 file');
    expect(tEn('files.manager.count', { count: 0 })).toBe('0 files');
    expect(tEn('files.manager.count', { count: 3 })).toBe('3 files');
  });

  it('asks about "the file" (not "the 1 files") when deleting a folder with one file', async () => {
    renderManager([file('f1', 'a.mp4', 'probe'), file('f2', 'b.mp4', undefined)]);
    const folderMenu = screen.getAllByRole('button', { name: 'Actions for folder probe' }).at(-1)!;
    fireEvent.click(folderMenu);
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    expect(await screen.findByText('Delete "probe" and the file inside it? This cannot be undone.')).toBeTruthy();
    expect(screen.queryByText(/the 1 files/)).toBeNull();
  });
});

describe('rename validation', () => {
  it('rejects empty names, path separators and over-long names before calling the server', () => {
    expect(validateFileName('  ')).toMatch(/Enter a name/);
    expect(validateFileName('a/b.mp4')).toMatch(/cannot contain/);
    expect(validateFileName('a\\b.mp4')).toMatch(/cannot contain/);
    expect(validateFileName('x'.repeat(256))).toMatch(/limit is 255/);
    expect(validateFileName('intro.mp4')).toBeNull();
  });

  it('turns 409 and 400 into plain messages and leaves other errors to a notification', () => {
    expect(renameMessage(new ApiError('conflict', 409), 'a.mp4')).toBe('Another file in this folder is already called "a.mp4". Pick a different name.');
    expect(renameMessage(new ApiError('Bad name', 400, ['File name is too long.']), 'a')).toBe('That name is not allowed: File name is too long.');
    expect(renameMessage(new ApiError('boom', 500), 'a')).toBe('');
  });
});

describe('renaming a file', () => {
  async function openRename(name = 'clip.mp4') {
    fireEvent.click(screen.getByRole('button', { name: `Actions for ${name}` }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    return (await screen.findByLabelText('New name')) as HTMLInputElement;
  }

  it('renames through the API and refreshes the list', async () => {
    renameFile.mockResolvedValue(file('f1', 'opening shot.mp4', undefined));
    const onChanged = renderManager([file('f1', 'clip.mp4', undefined)]);
    const input = await openRename();
    expect(input.value).toBe('clip.mp4');
    fireEvent.change(input, { target: { value: 'opening shot.mp4' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Rename' })); });
    expect(renameFile).toHaveBeenCalledWith('p', 'f1', 'opening shot.mp4');
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('shows a clash inline (409) and keeps the dialog open', async () => {
    renameFile.mockRejectedValue(new ApiError('A file with that name already exists.', 409));
    renderManager([file('f1', 'clip.mp4', undefined), file('f2', 'intro.mp4', undefined)]);
    const input = await openRename();
    fireEvent.change(input, { target: { value: 'intro.mp4' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Rename' })); });
    expect(await screen.findByText('Another file in this folder is already called "intro.mp4". Pick a different name.')).toBeTruthy();
    expect(screen.getByLabelText('New name')).toBeTruthy();
  });

  it('shows a client-side validation message without calling the server', async () => {
    renderManager([file('f1', 'clip.mp4', undefined)]);
    const input = await openRename();
    fireEvent.change(input, { target: { value: 'footage/clip.mp4' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Rename' })); });
    expect(await screen.findByText(/cannot contain/)).toBeTruthy();
    expect(renameFile).not.toHaveBeenCalled();
  });
});
