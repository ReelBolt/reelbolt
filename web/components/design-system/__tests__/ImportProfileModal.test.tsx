// @vitest-environment jsdom
/**
 * The extended Import flow (P3-14, SPEC §16.4/§17): the Figma variables form (one-shot token that
 * is cleared on submit), the Brand PDF multipart upload and the diagnostics list after an import.
 * The api module is mocked, so the assertions are about the request the modal would send.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
    data: [{ id: 'f1', originalFileName: 'brief.txt' }],
    isLoading: false,
    error: null,
  }),
}));

import { ApiError } from '@/lib/api/client';
import { importBrandPdf, importFigmaVariables } from '@/lib/api/design-system';
import type { DesignVersionSummary, ImportDiagnostic } from '@/lib/types/design-system';
import { ImportProfileModal } from '../ImportProfileModal';
import {
  IMPORT_FIGMA_FILE_KEY_LABEL,
  IMPORT_FIGMA_TOKEN_LABEL,
  IMPORT_PDF_NOT_A_PDF,
  IMPORT_PDF_TOO_LARGE,
  IMPORT_PDF_UNAVAILABLE,
} from '../copy';
import { en } from './en';

const version: DesignVersionSummary = {
  id: 'v1',
  designSystemId: 'd1',
  versionNumber: 3,
  state: 'Draft',
  origin: 'Import',
  documentDigest: 'abc',
  baseVersionId: null,
  workflowExecutionId: null,
  createdByUserId: 'u1',
  approvedByUserId: null,
  approvedAt: null,
  createdAt: '2026-01-01T00:00:00Z',
};

const diagnostics: ImportDiagnostic[] = [
  { severity: 'Info', code: 'DROPPED_CODE', path: '', message: 'Code was skipped' },
  { severity: 'Warn', code: 'UNSUPPORTED_VALUE', path: 'color.brand', message: 'Kept as-is' },
  { severity: 'Error', code: 'IMPORT_TOO_LARGE', path: 'tokens.json', message: 'Too big' },
];

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

beforeEach(() => {
  vi.mocked(importFigmaVariables).mockReset();
  vi.mocked(importBrandPdf).mockReset();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderModal() {
  render(
    <MantineProvider>
      <Notifications />
      <ImportProfileModal projectId="p1" opened onClose={vi.fn()} onImported={vi.fn()} />
    </MantineProvider>,
  );
}

function submitFigma(fileKey: string, token: string) {
  fireEvent.change(screen.getByLabelText(en(IMPORT_FIGMA_FILE_KEY_LABEL)), {
    target: { value: fileKey },
  });
  fireEvent.change(screen.getByLabelText(en(IMPORT_FIGMA_TOKEN_LABEL)), {
    target: { value: token },
  });
  fireEvent.click(screen.getByTestId('figma-submit'));
}

function choosePdf(name: string) {
  const file = new File(['%PDF-1.7'], name, { type: 'application/pdf' });
  const input = document.querySelector('input[type="file"]') as HTMLInputElement;
  fireEvent.change(input, { target: { files: [file] } });
  return file;
}

describe('ImportProfileModal extended import (P3-14)', () => {
  it('keeps the Figma token field a password and clears it on submit', async () => {
    vi.mocked(importFigmaVariables).mockResolvedValue({ version, diagnostics: [] });
    renderModal();

    const tokenInput = screen.getByLabelText(en(IMPORT_FIGMA_TOKEN_LABEL)) as HTMLInputElement;
    expect(tokenInput.type).toBe('password');

    submitFigma('Abbc123456', 'figd_secret');
    // Clearing happens before the request resolves, so the token never survives the submit.
    expect(tokenInput.value).toBe('');

    await waitFor(() =>
      expect(importFigmaVariables).toHaveBeenCalledWith('p1', 'Abbc123456', 'figd_secret'),
    );
    expect((screen.getByLabelText(en(IMPORT_FIGMA_TOKEN_LABEL)) as HTMLInputElement).value).toBe('');
    expect(screen.getByTestId('draft-review-banner')).toBeTruthy();
  });

  it('renders an Info, a Warn and an Error diagnostic with their badges', async () => {
    vi.mocked(importFigmaVariables).mockResolvedValue({ version, diagnostics });
    renderModal();

    submitFigma('Abbc123456', 'figd_secret');

    await screen.findByTestId('import-diagnostics');
    expect(within(screen.getByTestId('import-diagnostic-Info')).getByText('Info')).toBeTruthy();
    expect(within(screen.getByTestId('import-diagnostic-Warn')).getByText('Warning')).toBeTruthy();
    expect(within(screen.getByTestId('import-diagnostic-Error')).getByText('Error')).toBeTruthy();
    expect(screen.getByText('UNSUPPORTED_VALUE · color.brand')).toBeTruthy();
  });

  it('uploads a Brand PDF and shows the draft banner on success', async () => {
    vi.mocked(importBrandPdf).mockResolvedValue({ version, diagnostics: [] });
    renderModal();

    const file = choosePdf('brand.pdf');
    fireEvent.click(screen.getByTestId('pdf-submit'));

    await waitFor(() => expect(importBrandPdf).toHaveBeenCalledWith('p1', file));
    expect(screen.getByTestId('draft-review-banner')).toBeTruthy();
  });

  it('surfaces the PDF refusals in plain language', async () => {
    const cases: [number, string][] = [
      [415, en(IMPORT_PDF_NOT_A_PDF)],
      [413, en(IMPORT_PDF_TOO_LARGE)],
      [503, en(IMPORT_PDF_UNAVAILABLE)],
    ];

    for (const [status, message] of cases) {
      cleanup();
      vi.mocked(importBrandPdf).mockRejectedValueOnce(
        new ApiError('raw', status, [], { code: `HTTP_${status}` }),
      );
      renderModal();
      choosePdf('brand.pdf');
      fireEvent.click(screen.getByTestId('pdf-submit'));
      expect(await screen.findByText(message)).toBeTruthy();
    }
  });
});
