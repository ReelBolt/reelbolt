// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { ArchiveUnpack } from '@/lib/api/files';
import type { ProjectFile } from '@/lib/types/project';
import { UnpackStatus, failureKey } from './UnpackStatus';

const fetcherMock = vi.hoisted(() => vi.fn());
const unpackMock = vi.hoisted(() => vi.fn());
const usage = vi.hoisted(() => ({ data: undefined as unknown }));
vi.mock('@/lib/api/client', async (orig) => ({ ...(await orig<typeof import('@/lib/api/client')>()), fetcher: fetcherMock }));
vi.mock('@/lib/api/files', () => ({ unpackArchive: unpackMock }));
// The advertised zip limit (the figure the API serves), stubbed so the note never fetches it.
vi.mock('@/lib/hooks/use-storage', () => ({ useStorageUsage: () => usage }));

beforeEach(() => {
  usage.data = { zipUnpackLimits: { maxMb: 64 } };
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(() => { cleanup(); fetcherMock.mockReset(); unpackMock.mockReset(); });

const row = (o: Partial<ArchiveUnpack>): ArchiveUnpack => ({
  id: 'u1', archiveName: 'site.zip', status: 'Running', failureCode: null, filesCreated: 0, bytesCreated: 0,
  targetDirectory: null, skipped: null, createdAt: new Date().toISOString(), finishedAt: null, ...o,
});
const zip = { id: 'f1', originalFileName: 'site.zip' } as ProjectFile;

function renderIt(onChanged = vi.fn()) {
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider><UnpackStatus projectId="p" files={[zip]} onChanged={onChanged} /></MantineProvider>
    </SWRConfig>,
  );
  return onChanged;
}

describe('UnpackStatus', () => {
  it('maps codes with a generic default', () => {
    expect(failureKey('TOO_MANY_FILES')).toBe('TOO_MANY_FILES');
    expect(failureKey('WHAT')).toBe('default');
    expect(failureKey(null)).toBe('default');
  });

  it('shows the unpacking state', async () => {
    fetcherMock.mockResolvedValue([row({ status: 'Pending' })]);
    renderIt();
    expect(await screen.findByText(/Unpacking site\.zip/)).toBeTruthy();
  });

  it('shows count and folder when done', async () => {
    fetcherMock.mockResolvedValue([row({ status: 'Done', filesCreated: 3, targetDirectory: 'site', finishedAt: new Date().toISOString() })]);
    renderIt();
    expect(await screen.findByText(/3 files in the folder site/)).toBeTruthy();
  });

  it('shows the reason and retries a failed unpack', async () => {
    fetcherMock.mockResolvedValue([row({ status: 'Failed', failureCode: 'ARCHIVE_TOO_LARGE' })]);
    unpackMock.mockResolvedValue(row({ status: 'Pending' }));
    renderIt();
    expect(await screen.findByText(/over 64 MB/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(unpackMock).toHaveBeenCalledWith('p', 'f1'));
  });

  it('falls back to a limit-free sentence when the API serves no limit', async () => {
    usage.data = {};
    fetcherMock.mockResolvedValue([row({ status: 'Failed', failureCode: 'ARCHIVE_TOO_LARGE' })]);
    renderIt();
    expect(await screen.findByText(/The zip is too big to unpack\./)).toBeTruthy();
    expect(screen.queryByText(/over \d+ MB/)).toBeNull();
  });

  it('refreshes the file list when an active unpack turns Done', async () => {
    fetcherMock.mockResolvedValueOnce([row({ status: 'Running' })])
      .mockResolvedValue([row({ status: 'Done', filesCreated: 2, targetDirectory: 'site', finishedAt: new Date().toISOString() })]);
    const onChanged = renderIt();
    await screen.findByText(/Unpacking/);
    await waitFor(() => expect(onChanged).toHaveBeenCalled(), { timeout: 6000 });
  }, 10000);

  it('refreshes the file list when a small zip is already Done at the first re-check after upload', async () => {
    fetcherMock.mockResolvedValueOnce([])
      .mockResolvedValue([row({ status: 'Done', filesCreated: 2, targetDirectory: 'site', finishedAt: new Date().toISOString() })]);
    const onChanged = vi.fn();
    const tree = (files: ProjectFile[]) => (
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider><UnpackStatus projectId="p" files={files} onChanged={onChanged} /></MantineProvider>
      </SWRConfig>
    );
    const { rerender } = render(tree([]));
    await waitFor(() => expect(fetcherMock).toHaveBeenCalled());
    rerender(tree([zip]));
    await waitFor(() => expect(onChanged).toHaveBeenCalled(), { timeout: 6000 });
  }, 10000);

  it('shows a neutral message and no Retry for a blocked zip', async () => {
    fetcherMock.mockResolvedValue([row({ status: 'Failed', failureCode: 'UNPACK_BLOCKED' })]);
    renderIt();
    expect(await screen.findByText(/can.t be unpacked/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('hides an older Failed row beside a newer Running attempt and offers no Retry', async () => {
    const old = new Date(Date.now() - 60000).toISOString();
    fetcherMock.mockResolvedValue([
      row({ id: 'new', status: 'Running', createdAt: new Date().toISOString() }),
      row({ id: 'old', status: 'Failed', failureCode: 'WRITE_FAILED', createdAt: old }),
    ]);
    renderIt();
    expect(await screen.findByText(/Unpacking site\.zip/)).toBeTruthy();
    expect(screen.queryByText(/could not be unpacked/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('hides an older Failed row beside a newer Done attempt', async () => {
    fetcherMock.mockResolvedValue([
      row({ id: 'old', status: 'Failed', createdAt: new Date(Date.now() - 60000).toISOString() }),
      row({ id: 'new', status: 'Done', filesCreated: 2, targetDirectory: 'site', finishedAt: new Date().toISOString() }),
    ]);
    renderIt();
    expect(await screen.findByText(/2 files in the folder site/)).toBeTruthy();
    expect(screen.queryByText(/could not be unpacked/)).toBeNull();
  });

  it('gives each reachable failure code its own message', () => {
    for (const c of ['TOO_MANY_ENTRIES', 'ARCHIVE_INVALID', 'SOURCE_GONE', 'WRITE_FAILED', 'UNPACK_INTERRUPTED', 'WORKER_BAD_RESPONSE', 'MANIFEST_TOO_LARGE', 'PASSWORD_PROTECTED', 'UNPACK_BLOCKED']) {
      expect(failureKey(c)).toBe(c);
    }
    expect(failureKey('ARCHIVE_PASSWORD_PROTECTED')).toBe('PASSWORD_PROTECTED');
    expect(failureKey('ZIP_PASSWORD_PROTECTED')).toBe('PASSWORD_PROTECTED');
  });

  it('re-checks the unpack list when a new zip shows up in the file list', async () => {
    fetcherMock.mockResolvedValue([]);
    const ui = (files: ProjectFile[]) => (
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider><UnpackStatus projectId="p" files={files} onChanged={vi.fn()} /></MantineProvider>
      </SWRConfig>
    );
    const { rerender } = render(ui([]));
    await waitFor(() => expect(fetcherMock).toHaveBeenCalled());
    fetcherMock.mockResolvedValue([row({ status: 'Pending' })]);
    rerender(ui([zip]));
    expect(await screen.findByText(/Unpacking site\.zip/, undefined, { timeout: 3000 })).toBeTruthy();
  });
});
