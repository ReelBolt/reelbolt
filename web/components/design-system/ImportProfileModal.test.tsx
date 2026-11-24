// @vitest-environment jsdom
/**
 * The Import flow (P2-22, SPEC §9/§17): the file choice and the profile kinds decide the exact
 * request the server receives; nothing about the draft itself is decided here.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';

vi.mock('@/lib/api/design-system', () => ({
  importDesign: vi.fn(),
  importFigmaVariables: vi.fn(),
  importBrandPdf: vi.fn(),
  approveVersion: vi.fn(),
  rejectVersion: vi.fn(),
}));
vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({
    data: [
      { id: 'f1', originalFileName: 'brief.txt' },
      { id: 'f2', originalFileName: 'logo.svg' },
    ],
    isLoading: false,
    error: null,
  }),
}));

import { importDesign } from '@/lib/api/design-system';
import { ImportProfileModal } from './ImportProfileModal';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

beforeEach(() => {
  vi.mocked(importDesign).mockResolvedValue({ versions: [], diagnostics: [] });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderModal(onImported = vi.fn()) {
  render(
    <MantineProvider>
      <Notifications />
      <ImportProfileModal projectId="p1" opened onClose={vi.fn()} onImported={onImported} />
    </MantineProvider>,
  );
  return onImported;
}

async function selectFile(name: string) {
  fireEvent.click(screen.getByRole('combobox'));
  const option = await screen.findByRole('option', { name });
  fireEvent.click(option);
}

describe('ImportProfileModal', () => {
  it('imports one selected file as the default design kind', async () => {
    renderModal();
    await selectFile('brief.txt');

    fireEvent.click(screen.getByTestId('import-submit'));
    await waitFor(() => expect(importDesign).toHaveBeenCalledWith('p1', ['f1'], ['design']));
  });

  it('checks product map too and sends both kinds', async () => {
    renderModal();
    await selectFile('brief.txt');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Product profile' }));

    fireEvent.click(screen.getByTestId('import-submit'));
    await waitFor(() => expect(importDesign).toHaveBeenCalledWith('p1', ['f1'], ['design', 'product_map']));
  });

  it('keeps submit disabled without a file', () => {
    renderModal();
    expect((screen.getByTestId('import-submit') as HTMLButtonElement).disabled).toBe(true);
  });

  it('keeps submit disabled with no kind checked', async () => {
    renderModal();
    await selectFile('brief.txt');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Brand profile' }));
    expect((screen.getByTestId('import-submit') as HTMLButtonElement).disabled).toBe(true);
  });
});
