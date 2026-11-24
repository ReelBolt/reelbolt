// @vitest-environment jsdom
/**
 * The archive import flow (P4-17, SPEC §16.4/§17): the "Design file or export
 * (.zip, .sketch, .penpot)" upload, the vendor label and read/skipped counts it renders, the
 * diagnostics list, and the blocked "could not find colors or fonts" message with next steps.
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
  importZip: vi.fn(),
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
import { importZip } from '@/lib/api/design-system';
import type {
  DesignVersionSummary,
  ImportDiagnostic,
  ImportReport,
  ImportSourceRef,
} from '@/lib/types/design-system';
import { ImportProfileModal } from '../ImportProfileModal';
import {
  IMPORT_PRODUCT_MAP_LINK,
  IMPORT_ZIP_NEXT_STEPS,
  IMPORT_ZIP_NOTHING_RECOGNIZED,
  IMPORT_ZIP_NOT_A_ZIP,
  IMPORT_ZIP_REJECTED,
  IMPORT_ZIP_TOO_LARGE,
  IMPORT_ZIP_UNAVAILABLE,
  importDropSentence,
} from '../copy';
import { en } from './en';

const version: DesignVersionSummary = {
  id: 'v1',
  designSystemId: 'd1',
  versionNumber: 1,
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

const sources: ImportSourceRef[] = [
  { path: 'tokens/colors.json', importer: 'dtcg', sha256: 'a', projectFileId: null },
  { path: 'theme.css', importer: 'css-theme', sha256: 'b', projectFileId: null },
];

const diagnostics: ImportDiagnostic[] = [
  { severity: 'Info', code: 'BUNDLE_VENDOR', path: '', message: 'Recognized the archive layout' },
  { severity: 'Warn', code: 'BUNDLE_FILE_DROPPED', path: 'src/app.tsx', message: 'Not a design file' },
  { severity: 'Warn', code: 'FILE_TOO_LARGE', path: 'huge.json', message: 'Too big' },
];

const report: ImportReport = {
  importerId: 'claude-design',
  vendor: 'claude-design',
  axes: [
    {
      axis: 'Tokens',
      support: 'Full',
      imported: 42,
      dropped: 2,
      reasons: [
        {
          code: 'UNSUPPORTED_VALUE',
          severity: 'Warn',
          count: 2,
          message: 'the value was not understood',
        },
      ],
    },
    {
      axis: 'Assets',
      support: 'Full',
      imported: 3,
      dropped: 0,
      reasons: [],
    },
  ],
};

const assets = [
  { role: 'logo', variant: null, name: 'logo.svg', projectFileId: 'a1' },
  { role: 'icon', variant: null, name: 'icon.svg', projectFileId: 'a2' },
  { role: 'preview', variant: null, name: 'preview.png', projectFileId: 'a3' },
];

const references = [
  { name: 'DESIGN.md', path: 'design/DESIGN.md', projectFileId: 'r1' },
  { name: 'README.md', path: 'README.md', projectFileId: 'r2' },
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
  vi.mocked(importZip).mockReset();
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

/** The archive FileInput's hidden file input: the Brand PDF upload renders first. */
function zipInput(): HTMLInputElement {
  const inputs = Array.from(document.querySelectorAll('input[type="file"]')) as HTMLInputElement[];
  return inputs[inputs.length - 1];
}

function chooseZip(name: string) {
  const file = new File(['PK\x03\x04'], name, { type: 'application/zip' });
  fireEvent.change(zipInput(), { target: { files: [file] } });
  return file;
}

describe('ImportProfileModal archive import (P4-17)', () => {
  it('uploads a .sketch export and shows the vendor, read/skipped counts and diagnostics', async () => {
    vi.mocked(importZip).mockResolvedValue({ version, diagnostics, sources, vendor: 'sketch' });
    renderModal();

    const file = chooseZip('brand.sketch');
    fireEvent.click(screen.getByTestId('zip-submit'));

    await waitFor(() => expect(importZip).toHaveBeenCalledWith('p1', file));

    expect(screen.getByTestId('zip-vendor').textContent).toBe('Export format: sketch');
    expect(screen.getByTestId('zip-recognized').textContent).toBe('Files read: 2');
    // Only the two dropped-entry notes count as skipped; the Info vendor note does not.
    expect(screen.getByTestId('zip-ignored').textContent).toBe('Entries skipped: 2');

    expect(within(screen.getByTestId('import-diagnostics')).getByText('Recognized the archive layout')).toBeTruthy();
    expect(screen.getByText('Not a design file')).toBeTruthy();
    expect(screen.getByTestId('draft-review-banner')).toBeTruthy();
  });

  it('shows the blocked message and next steps when nothing was recognized', async () => {
    vi.mocked(importZip).mockRejectedValueOnce(
      new ApiError('raw', 400, [], {
        code: 'IMPORT_NOTHING_RECOGNIZED',
        message: 'no registered importer recognized any entry of the archive',
      }),
    );
    renderModal();

    chooseZip('images.zip');
    fireEvent.click(screen.getByTestId('zip-submit'));

    expect(await screen.findByText(en(IMPORT_ZIP_NOTHING_RECOGNIZED))).toBeTruthy();
    expect(screen.getByTestId('zip-error-next-steps').textContent).toBe(en(IMPORT_ZIP_NEXT_STEPS));
  });

  it('surfaces the archive refusals in plain language', async () => {
    const cases: [number, string, string][] = [
      [415, 'NOT_A_ZIP', en(IMPORT_ZIP_NOT_A_ZIP)],
      [413, 'ZIP_TOO_LARGE', en(IMPORT_ZIP_TOO_LARGE)],
      [503, 'import_worker_unavailable', en(IMPORT_ZIP_UNAVAILABLE)],
      [400, 'IMPORT_REJECTED', en(IMPORT_ZIP_REJECTED)],
    ];

    for (const [status, code, message] of cases) {
      cleanup();
      vi.mocked(importZip).mockRejectedValueOnce(new ApiError('raw', status, [], { code }));
      renderModal();
      chooseZip('export.zip');
      fireEvent.click(screen.getByTestId('zip-submit'));
      expect(await screen.findByText(message)).toBeTruthy();
    }
  });

  describe('skipped files (T-016)', () => {
    const skipped = [
      { reason: 'DROPPED_SCRIPT', count: 4, examples: ['a1.js', 'a2.js', '<img src=x>'] },
      { reason: 'DROPPED_HTML', count: 1, examples: ['index.html'] },
      { reason: 'DOCUMENT_TOO_LARGE', count: 2, examples: ['big.json'] },
      { reason: 'PATH_NON_ASCII', count: 1, examples: ['caf?.json'] },
      { reason: 'SOMETHING_NEW', count: 3, examples: [] },
    ];

    it('lists a friendly line per reason with count and example names as text after a 201', async () => {
      vi.mocked(importZip).mockResolvedValue({ version, diagnostics, sources, vendor: 'sketch', skipped });
      renderModal();
      chooseZip('brand.zip');
      fireEvent.click(screen.getByTestId('zip-submit'));

      const list = await screen.findByTestId('zip-skipped');
      const rows = within(list).getAllByTestId('zip-skipped-row');
      expect(rows).toHaveLength(5);
      expect(rows[0].textContent).toContain('Scripts are never run');
      expect(rows[0].textContent).toContain('4 files');
      expect(rows[0].textContent).toContain('a1.js, a2.js, <img src=x>');
      expect(list.querySelector('img')).toBeNull();
      expect(rows[1].textContent).toContain('Web pages are never run');
      expect(rows[2].textContent).toContain('too large');
      // A worker from before T-021 may still answer PATH_NON_ASCII; accents are supported now, so it gets the generic line.
      expect(rows[3].textContent).toContain('1 file was skipped');
      expect(rows[3].textContent).not.toContain('plain letters');
      expect(rows[4].textContent).toContain('3 files were skipped');
    });

    it('shows nothing when no files were skipped', async () => {
      vi.mocked(importZip).mockResolvedValue({ version, diagnostics, sources, vendor: null, skipped: [] });
      renderModal();
      chooseZip('brand.zip');
      fireEvent.click(screen.getByTestId('zip-submit'));
      await screen.findByTestId('zip-result');
      expect(screen.queryByTestId('zip-skipped')).toBeNull();
    });

    it('also lists skipped files when the import is rejected with IMPORT_NOTHING_RECOGNIZED', async () => {
      vi.mocked(importZip).mockRejectedValueOnce(
        new ApiError('raw', 400, [], {
          code: 'IMPORT_NOTHING_RECOGNIZED',
          message: 'nothing',
          skipped: [{ reason: 'DROPPED_SCRIPT', count: 4, examples: ['a1.js'] }],
        }),
      );
      renderModal();
      chooseZip('code.zip');
      fireEvent.click(screen.getByTestId('zip-submit'));
      const error = await screen.findByTestId('zip-error');
      expect(within(error).getByTestId('zip-skipped').textContent).toContain('4 files');
    });
  });

  describe('import report (T-102)', () => {
    it('renders the report above the diagnostics with stored counts and the product-map label', async () => {
      vi.mocked(importZip).mockResolvedValue({
        version,
        diagnostics,
        sources,
        vendor: 'claude-design',
        assets,
        references,
        productMapVersion: version,
        report,
      });
      renderModal();
      chooseZip('brand.zip');
      fireEvent.click(screen.getByTestId('zip-submit'));

      const panel = await screen.findByTestId('import-report');
      const rawDiagnostics = screen.getByTestId('import-diagnostics');

      // The report is the summary; the raw diagnostics list stays below it for detail.
      expect(
        panel.compareDocumentPosition(rawDiagnostics) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy();
      expect(within(rawDiagnostics).getByText('Recognized the archive layout')).toBeTruthy();

      expect(screen.getByTestId('import-report-row-Tokens').textContent).toContain(
        'Colours and values',
      );
      expect(
        screen.getByTestId('import-report-reason-Tokens-UNSUPPORTED_VALUE').textContent,
      ).toContain(en(importDropSentence('UNSUPPORTED_VALUE', '')));
      expect(screen.getByTestId('import-report-nothing-dropped-Assets').textContent).toBe(
        'Nothing was dropped.',
      );

      expect(screen.getByTestId('import-assets-count').textContent).toBe('Files stored: 3');
      expect(screen.getByTestId('import-references-count').textContent).toBe(
        'Reference documents stored: 2',
      );
      expect(screen.getByTestId('import-product-map-link').textContent).toBe(en(IMPORT_PRODUCT_MAP_LINK));
    });

    it('renders no report panel or product-map label when the response carried none', async () => {
      vi.mocked(importZip).mockResolvedValue({ version, diagnostics, sources, vendor: 'sketch' });
      renderModal();
      chooseZip('brand.zip');
      fireEvent.click(screen.getByTestId('zip-submit'));

      await screen.findByTestId('zip-result');
      expect(screen.queryByTestId('import-report')).toBeNull();
      expect(screen.queryByTestId('import-product-map-link')).toBeNull();
    });
  });
});
