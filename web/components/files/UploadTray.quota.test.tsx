// @vitest-environment jsdom

/**
 * The upload tray's treatment of a full workspace (docs/design/local-folder-sync-and-storage.md
 * Part 2): the QUEUE turns a `STORAGE_QUOTA_EXCEEDED` refusal into the prescribed sentence and marks
 * it as a quota failure, and the TRAY renders that once, with a link to Billing, instead of a Retry
 * button that cannot help.
 */

import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { UploadTray } from './UploadTray';
import { useUploadQueue } from '@/lib/hooks/use-upload-queue';
import { joinPath } from '@/lib/files/model';

vi.mock('@/lib/api/files', () => ({ uploadFileWithProgress: vi.fn() }));

import { uploadFileWithProgress } from '@/lib/api/files';

const uploadMock = vi.mocked(uploadFileWithProgress);

const quotaRefusal = () => new ApiError('storage', 413, [], {
  code: 'STORAGE_QUOTA_EXCEEDED',
  feature: 'storage',
  message: 'Your workspace is out of storage.',
  limit: 16_106_127_360,
  current: 14_820_833_280,
});

function makeFile(name = 'clip.mp4', size = 1024): File {
  return new File([new Uint8Array(size)], name, { type: 'video/mp4' });
}

/** The real queue, rendered through the real tray: one drop of one file. */
function QueueHarness() {
  const { entries, enqueue } = useUploadQueue('p1', () => {});
  const [started, setStarted] = useState(false);
  const drop = () => {
    if (started) return;
    setStarted(true);
    enqueue([{ file: makeFile(), relativePath: 'clip.mp4' }], '');
  };
  return (
    <>
      <button onClick={drop}>drop</button>
      <UploadTray entries={entries} onRetry={() => {}} onClear={() => {}} />
    </>
  );
}

beforeEach(() => {
  uploadMock.mockReset();
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});

afterEach(cleanup);

describe('the upload tray and a full workspace', () => {
  it('shows the prescribed sentence once, with a link to Billing and no Retry on that file', async () => {
    uploadMock.mockRejectedValue(quotaRefusal());
    render(<MantineProvider><QueueHarness /></MantineProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'drop' }));

    const notice = await screen.findByTestId('upload-quota-notice');
    expect(notice.textContent).toContain('out of storage (13.8 GB of 15 GB used)');
    expect(notice.textContent).toContain('Delete files or upgrade your plan');
    expect(within(notice).getByRole('link', { name: /See plans and storage/ }).getAttribute('href')).toBe('/settings/billing');
    // The entry itself says the same sentence, and offers no Retry: retrying cannot free space.
    const tray = screen.getByRole('complementary', { name: 'Uploads' });
    expect(tray.textContent).toContain('13.8 GB of 15 GB used');
    expect(within(tray).queryByRole('button', { name: /Retry/ })).toBeNull();
  });

  it('keeps the ordinary sentence and the Retry button for any other failure', async () => {
    uploadMock.mockRejectedValue(new Error('Storage refused part 2 (HTTP 503).'));
    render(<MantineProvider><QueueHarness /></MantineProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'drop' }));

    const tray = await screen.findByRole('complementary', { name: 'Uploads' });
    await waitFor(() => expect(tray.textContent).toContain('Storage refused part 2 (HTTP 503).'));
    expect(screen.queryByTestId('upload-quota-notice')).toBeNull();
    expect(within(tray).getByRole('button', { name: /Retry/ })).toBeTruthy();
  });

  it('joins the destination into the entry, so a folder upload shows where it lands', async () => {
    uploadMock.mockResolvedValue({ id: 'f1' } as never);
    render(<MantineProvider><QueueHarness /></MantineProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'drop' }));

    const tray = await screen.findByRole('complementary', { name: 'Uploads' });
    await waitFor(() => expect(tray.textContent).toContain('clip.mp4'));
    expect(joinPath('', 'clip.mp4')).toBe('clip.mp4');
  });
});
