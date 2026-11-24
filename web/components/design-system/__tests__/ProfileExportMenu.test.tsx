// @vitest-environment jsdom
/**
 * P4-08 (SPEC §16.2): the profile export menu offers every design format, offers the product map
 * json only, and each choice fetches the effective document and triggers a blob download. The
 * fetcher is mocked so no network runs; the download itself is the real `downloadBlob`.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/api/design-system', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/design-system')>();
  return { ...actual, exportDesignDocument: vi.fn() };
});

import { exportDesignDocument } from '@/lib/api/design-system';
import type { DesignSystemKind } from '@/lib/types/design-system';
import { ProfileExportMenu } from '../ProfileExportMenu';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({
      matches: false,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
    }),
  });
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as never;
  Element.prototype.scrollIntoView = vi.fn();
  Object.defineProperty(URL, 'createObjectURL', { writable: true, value: vi.fn(() => 'blob:mock') });
  Object.defineProperty(URL, 'revokeObjectURL', { writable: true, value: vi.fn() });
  HTMLAnchorElement.prototype.click = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderMenu(kind: DesignSystemKind = 'design') {
  render(
    <MantineProvider>
      <ProfileExportMenu projectId="p1" kind={kind} />
    </MantineProvider>,
  );
}

/** Waits for Mantine's close transition to finish so no timer outlives the jsdom environment. */
async function settled() {
  await waitFor(() => expect(screen.queryAllByRole('menuitem')).toHaveLength(0));
}

describe('ProfileExportMenu', () => {
  it('offers every design format and triggers a blob download', async () => {
    vi.mocked(exportDesignDocument).mockResolvedValue(new Blob(['{}'], { type: 'application/json' }));
    renderMenu();

    fireEvent.click(screen.getByTestId('profile-export-menu'));
    expect(await screen.findAllByRole('menuitem')).toHaveLength(5);

    fireEvent.click(screen.getByTestId('export-dtcg'));

    await waitFor(() => expect(exportDesignDocument).toHaveBeenCalledWith('p1', 'design', 'dtcg'));
    await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalled());
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalled();
    await settled();
  });

  it('offers json only for a product map and downloads it', async () => {
    vi.mocked(exportDesignDocument).mockResolvedValue(new Blob(['{}'], { type: 'application/json' }));
    renderMenu('product_map');

    fireEvent.click(screen.getByTestId('profile-export-menu'));
    const items = await screen.findAllByRole('menuitem');
    expect(items).toHaveLength(1);
    expect(items[0].textContent).toBe('JSON');

    fireEvent.click(screen.getByTestId('export-json'));

    await waitFor(() => expect(exportDesignDocument).toHaveBeenCalledWith('p1', 'product_map', 'json'));
    await settled();
  });

  it('surfaces a refusal instead of downloading', async () => {
    vi.mocked(exportDesignDocument).mockRejectedValue(new Error('unsupported format'));
    renderMenu();

    fireEvent.click(screen.getByTestId('profile-export-menu'));
    fireEvent.click(await screen.findByTestId('export-css'));

    expect((await screen.findByRole('alert')).textContent).toBe('unsupported format');
    expect(URL.createObjectURL).not.toHaveBeenCalled();
    await settled();
  });
});

describe('ProfileExportMenu accessible names', () => {
  it('names each button by what it exports, so two Export buttons are distinguishable', () => {
    render(
      <MantineProvider>
        <ProfileExportMenu projectId="p1" kind="design" />
        <ProfileExportMenu projectId="p1" kind="product_map" />
      </MantineProvider>,
    );
    expect(screen.getByRole('button', { name: 'Export brand profile' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Export product profile' })).toBeTruthy();
  });
});
