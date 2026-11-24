// @vitest-environment jsdom

/**
 * The one storage refusal, as the upload tray, the assistant's attach control and the folder-link
 * list render it: "Your workspace is out of storage (X of Y used). Delete files or upgrade your
 * plan." with a link to Billing — never the backend's own wording, and never shown for any other
 * error.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { isStorageQuotaError, storageQuotaInfo, storageQuotaMessage, uploadErrorMessage } from '@/lib/api/quota';
import { StorageQuotaNotice, lastErrorLooksLikeStorageFull } from './StorageQuotaNotice';

// Mantine's provider reads `matchMedia` for the colour scheme, which jsdom does not implement.
beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});

const render2 = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

/** The browser route's body: `code` + `limit`/`current` (from the plan gate). */
const browserRefusal = () =>
  new ApiError('storage', 413, [], {
    code: 'STORAGE_QUOTA_EXCEEDED',
    feature: 'storage',
    message: 'Your workspace is out of storage.',
    limit: 16_106_127_360,
    current: 14_820_833_280,
  });

/** The device/folder-sync body: `error` + `usedBytes`/`limitBytes`. */
const deviceRefusal = () =>
  new ApiError('storage', 413, [], {
    error: 'STORAGE_QUOTA_EXCEEDED',
    usedBytes: 14_820_833_280,
    limitBytes: 16_106_127_360,
  });

afterEach(cleanup);

describe('storageQuotaInfo', () => {
  it('reads the plan gate’s limit/current shape', () => {
    expect(storageQuotaInfo(browserRefusal())).toEqual({ usedBytes: 14_820_833_280, limitBytes: 16_106_127_360 });
  });

  it('reads the device route’s usedBytes/limitBytes shape', () => {
    expect(storageQuotaInfo(deviceRefusal())).toEqual({ usedBytes: 14_820_833_280, limitBytes: 16_106_127_360 });
  });

  it('is null for any other error, so nothing else is ever called a quota problem', () => {
    expect(storageQuotaInfo(new ApiError('nope', 409, [], { code: 'UPLOAD_SIZE_MISMATCH' }))).toBeNull();
    expect(storageQuotaInfo(new Error('network'))).toBeNull();
    expect(isStorageQuotaError(new Error('network'))).toBe(false);
    expect(isStorageQuotaError(browserRefusal())).toBe(true);
  });
});

describe('storageQuotaMessage', () => {
  it('uses the prescribed sentence with the server’s own figures', () => {
    expect(storageQuotaMessage(storageQuotaInfo(browserRefusal())))
      .toBe('Your workspace is out of storage (13.8 GB of 15 GB used). Delete files or upgrade your plan.');
  });

  it('drops the figures rather than guessing when the body omitted them', () => {
    expect(storageQuotaMessage({ usedBytes: null, limitBytes: null }))
      .toBe('Your workspace is out of storage. Delete files or upgrade your plan.');
  });
});

describe('uploadErrorMessage', () => {
  it('maps a quota error to the prescribed sentence and anything else to its own message', () => {
    expect(uploadErrorMessage(browserRefusal(), 'fallback')).toContain('out of storage');
    expect(uploadErrorMessage(new Error('Storage refused part 2 (HTTP 503).'), 'fallback')).toBe('Storage refused part 2 (HTTP 503).');
    expect(uploadErrorMessage('not an error', 'fallback')).toBe('fallback');
  });
});

describe('StorageQuotaNotice', () => {
  it('renders the sentence and a link to Billing for a quota refusal', () => {
    render2(<StorageQuotaNotice error={browserRefusal()} />);

    const notice = screen.getByTestId('storage-quota-notice');
    expect(notice.textContent).toContain('out of storage (13.8 GB of 15 GB used)');
    expect(screen.getByRole('link', { name: /See plans and storage/ }).getAttribute('href')).toBe('/settings/billing');
  });

  it('renders nothing at all for any other error', () => {
    render2(<StorageQuotaNotice error={new ApiError('nope', 500, [], { code: 'BOOM' })} />);
    expect(screen.queryByTestId('storage-quota-notice')).toBeNull();

    cleanup();
    render2(<StorageQuotaNotice error={undefined} />);
    expect(screen.queryByTestId('storage-quota-notice')).toBeNull();
  });
});

describe('lastErrorLooksLikeStorageFull', () => {
  it('recognizes the two ways a sync says the workspace is full', () => {
    expect(lastErrorLooksLikeStorageFull('Storage is full (15 GB of 15 GB used) — 3 files were not copied.')).toBe(true);
    expect(lastErrorLooksLikeStorageFull('Your workspace is out of storage.')).toBe(true);
    expect(lastErrorLooksLikeStorageFull('The folder could not be read.')).toBe(false);
    expect(lastErrorLooksLikeStorageFull(null)).toBe(false);
  });
});
