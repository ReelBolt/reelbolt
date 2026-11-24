// @vitest-environment jsdom
/**
 * The Figma section only appears on a server that has the import switched on. The server answers
 * `404 { code: "feature_disabled" }` to any Figma import when it is off and validates the file key
 * (a 400) when it is on, so one probe call with empty values tells the two apart without spending a
 * token. Each test gets a fresh SWR cache, so "asked once" is observable.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { SWRConfig } from 'swr';

vi.mock('@/lib/api/design-system', () => ({
  importDesign: vi.fn(),
  importFigmaVariables: vi.fn(),
  importBrandPdf: vi.fn(),
  importZip: vi.fn(),
  approveVersion: vi.fn(),
  rejectVersion: vi.fn(),
}));
vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: [], isLoading: false, error: null }),
}));

import { ApiError } from '@/lib/api/client';
import { importFigmaVariables } from '@/lib/api/design-system';
import { ImportProfileModal } from '../ImportProfileModal';
import { IMPORT_FIGMA_FILE_KEY_LABEL, IMPORT_FIGMA_TITLE } from '../copy';
import { en } from './en';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const disabled = () => new ApiError('the Figma variables import is disabled', 404, [], { code: 'feature_disabled' });
const invalidKey = () => new ApiError('fileKey must match', 400, [], { code: 'INVALID_FILE_KEY' });

function renderModal(cache = new Map()) {
  const ui = (
    <SWRConfig value={{ provider: () => cache }}>
      <MantineProvider>
        <Notifications />
        <ImportProfileModal projectId="p1" opened onClose={vi.fn()} onImported={vi.fn()} />
      </MantineProvider>
    </SWRConfig>
  );
  return { ...render(ui), cache, ui };
}

describe('Figma import availability', () => {
  it('hides the Figma section when the server says the feature is disabled', async () => {
    vi.mocked(importFigmaVariables).mockRejectedValue(disabled());
    renderModal();

    await waitFor(() => expect(screen.queryByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL))).toBeNull());
    expect(screen.queryByText(en(IMPORT_FIGMA_TITLE))).toBeNull();
    // The rest of the import dialog is untouched.
    expect(screen.getByTestId('import-submit')).toBeTruthy();
  });

  it('keeps the Figma section when the server only rejects the empty probe values', async () => {
    vi.mocked(importFigmaVariables).mockRejectedValue(invalidKey());
    renderModal();

    await waitFor(() => expect(importFigmaVariables).toHaveBeenCalled());
    expect(screen.getByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL))).toBeTruthy();
  });

  it('asks the server once and remembers the answer when the dialog is shown again', async () => {
    vi.mocked(importFigmaVariables).mockRejectedValue(disabled());
    const { cache, unmount } = renderModal();
    await waitFor(() => expect(screen.queryByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL))).toBeNull());
    unmount();

    renderModal(cache);
    await waitFor(() => expect(screen.queryByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL))).toBeNull());
    expect(importFigmaVariables).toHaveBeenCalledTimes(1);
  });

  it('hides the section if a real import is refused as disabled', async () => {
    vi.mocked(importFigmaVariables)
      .mockRejectedValueOnce(invalidKey()) // the probe
      .mockRejectedValueOnce(disabled()); // the user's own submit
    renderModal();

    await waitFor(() => expect(importFigmaVariables).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL)), { target: { value: 'Abbc123456' } });
    fireEvent.change(screen.getByLabelText('Figma access token'), { target: { value: 'figd_x' } });
    fireEvent.click(screen.getByTestId('figma-submit'));

    await waitFor(() => expect(screen.queryByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL))).toBeNull());
  });
});
