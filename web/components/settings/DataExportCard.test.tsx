// @vitest-environment jsdom
/**
 * "Download your data".
 *
 * What these tests protect:
 *   - a request that was accepted says the file is being prepared, as the status endpoint reports;
 *   - the newest copy is named with its date, size and expiry;
 *   - the once-a-day limit is said in plain words with how long to wait, and disables the button;
 *   - a ready copy downloads through a link fetched at the moment of the click;
 *   - a server with exports switched off says so instead of offering a button that cannot work.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

const H = vi.hoisted(() => ({
  request: vi.fn(),
  link: vi.fn(),
  status: vi.fn(),
}));

vi.mock('@/lib/api/account', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/account')>();
  return {
    ...actual,
    requestDataExport: (...args: unknown[]) => H.request(...args),
    getDataExportLink: (...args: unknown[]) => H.link(...args),
    getDataExportStatus: (...args: unknown[]) => H.status(...args),
  };
});

import { DataExportCard } from './DataExportCard';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

const NEW = 'https://s3.example/exports/u/new.zip?sig=2';

const renderCard = () =>
  render(
    <MantineProvider>
      {/* A fresh cache per test, so one test's answer is never another's. */}
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <DataExportCard />
      </SWRConfig>
    </MantineProvider>,
  );

beforeEach(() => {
  H.request.mockReset();
  H.link.mockReset();
  H.status.mockReset();
  Object.defineProperty(window, 'location', { configurable: true, value: { ...window.location, assign: vi.fn() } });
});

afterEach(cleanup);

const STATUS_NONE = { kind: 'ok', preparing: false, latest: null, nextRequestAt: null } as const;
const STATUS_READY = {
  kind: 'ok',
  preparing: false,
  latest: { createdAt: '2026-10-10T09:00:00Z', sizeBytes: 3 * 1024 * 1024, expiresAt: '2026-10-17T09:00:00Z' },
  nextRequestAt: null,
} as const;

describe('DataExportCard', () => {
  it('offers a first copy when there is none, and says it is being prepared once asked', async () => {
    H.status.mockResolvedValueOnce(STATUS_NONE);
    H.status.mockResolvedValue({ ...STATUS_NONE, preparing: true, nextRequestAt: new Date(Date.now() + 86_000_000).toISOString() });
    H.request.mockResolvedValue({ kind: 'accepted' });
    renderCard();

    fireEvent.click(await screen.findByRole('button', { name: 'Request a copy' }));
    expect(await screen.findByTestId('data-export-preparing')).toBeTruthy();
    expect(screen.getByText(/email you a link/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Request/ })).toBeNull();
    expect(H.request).toHaveBeenCalledTimes(1);
  });

  it('names the newest copy, its size and until when it can be downloaded', async () => {
    H.status.mockResolvedValue(STATUS_READY);
    renderCard();

    const ready = await screen.findByTestId('data-export-ready');
    expect(ready.textContent).toContain('was made on');
    expect(ready.textContent).toContain('MB');
    expect(ready.textContent).toContain('until');
    expect(screen.getByRole('button', { name: 'Request a new copy' })).toBeTruthy();
  });

  it('downloads through a link fetched at the moment of the click', async () => {
    H.status.mockResolvedValue(STATUS_READY);
    H.link.mockResolvedValueOnce({ kind: 'ready', url: NEW, expiresInSeconds: 900 });
    renderCard();

    fireEvent.click(await screen.findByRole('button', { name: 'Download your data (zip)' }));
    await waitFor(() => expect(window.location.assign).toHaveBeenCalledWith(NEW));
  });

  it('says a copy has expired when the download finds none', async () => {
    H.status.mockResolvedValue(STATUS_READY);
    H.link.mockResolvedValueOnce({ kind: 'none' });
    renderCard();

    fireEvent.click(await screen.findByRole('button', { name: 'Download your data (zip)' }));
    expect(await screen.findByTestId('data-export-expired')).toBeTruthy();
  });

  it('says how long to wait when a copy was asked for in the last day, and disables the button', async () => {
    H.status.mockResolvedValue({ ...STATUS_READY, nextRequestAt: new Date(Date.now() + (17 * 3600 - 30) * 1000).toISOString() });
    renderCard();

    const notice = await screen.findByTestId('data-export-too-soon');
    expect(notice.textContent).toContain('in 17 hours');
    expect((screen.getByRole('button', { name: 'Request a new copy' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('says exports are not available when the server has them switched off', async () => {
    H.status.mockResolvedValue({ kind: 'unavailable' });
    renderCard();

    expect(await screen.findByTestId('data-export-unavailable')).toBeTruthy();
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('keeps the request button when the first check fails, and reports a failed request in words', async () => {
    H.status.mockRejectedValue(new Error('network'));
    H.request.mockRejectedValue(new Error('boom'));
    renderCard();

    expect(await screen.findByTestId('data-export-unknown')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Request a copy' }));
    expect((await screen.findByTestId('data-export-error')).textContent).toContain("couldn't start your copy");
  });
});
