// @vitest-environment jsdom
/**
 * The axis-tagged import report (T-102; SPEC §17): one row per axis with its support, imported and
 * dropped counts, a plain-language sentence per dropped reason code, "nothing was dropped" for a
 * clean axis, and no panel at all when the API sent no report.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ImportReport } from '@/lib/types/design-system';
import { ImportReportPanel } from '../ImportReportPanel';
import { IMPORT_REPORT_NOTHING_DROPPED, IMPORT_REPORT_TITLE, importDropSentence } from '../copy';
import { en } from './en';

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
      support: 'Partial',
      imported: 5,
      dropped: 0,
      reasons: [],
    },
    {
      axis: 'ReferenceDocuments',
      support: 'Full',
      imported: 3,
      dropped: 1,
      reasons: [
        {
          code: 'REFERENCE_CAP',
          severity: 'Info',
          count: 1,
          message: '1 file(s) were skipped (REFERENCE_CAP)',
        },
      ],
    },
  ],
};

function renderPanel(value: ImportReport | null | undefined) {
  render(
    <MantineProvider>
      <ImportReportPanel report={value} />
    </MantineProvider>,
  );
}

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
});

afterEach(cleanup);

describe('ImportReportPanel (T-102)', () => {
  it('renders one row per axis with the support word and imported/dropped counts', () => {
    renderPanel(report);

    expect(screen.getByText(en(IMPORT_REPORT_TITLE))).toBeTruthy();
    expect(screen.getAllByTestId(/^import-report-row-/)).toHaveLength(3);

    const tokens = screen.getByTestId('import-report-row-Tokens');
    expect(tokens.textContent).toContain('Colours and values');
    expect(tokens.textContent).toContain('Full');
    expect(screen.getByTestId('import-report-imported-Tokens').textContent).toBe('42');
    expect(screen.getByTestId('import-report-dropped-Tokens').textContent).toBe('2');
  });

  it('shows each reason code with its plain-language sentence', () => {
    renderPanel(report);

    const unsupported = screen.getByTestId('import-report-reason-Tokens-UNSUPPORTED_VALUE');
    expect(unsupported.textContent).toContain('UNSUPPORTED_VALUE');
    expect(unsupported.textContent).toContain(en(importDropSentence('UNSUPPORTED_VALUE', '')));

    const capped = screen.getByTestId('import-report-reason-ReferenceDocuments-REFERENCE_CAP');
    expect(capped.textContent).toContain('REFERENCE_CAP');
    expect(capped.textContent).toContain(en(importDropSentence('REFERENCE_CAP', '')));
  });

  it('falls back to the sanitized API message for a code it does not know', () => {
    renderPanel({
      ...report,
      axes: [
        {
          axis: 'Tokens',
          support: 'Full',
          imported: 1,
          dropped: 1,
          reasons: [
            {
              code: 'SOMETHING_NEW',
              severity: 'Warn',
              count: 1,
              message: 'A sanitized API sentence',
            },
          ],
        },
      ],
    });

    expect(
      screen.getByTestId('import-report-reason-Tokens-SOMETHING_NEW').textContent,
    ).toContain('A sanitized API sentence');
  });

  it('says nothing was dropped on an axis with no drops', () => {
    renderPanel(report);

    expect(screen.getByTestId('import-report-nothing-dropped-Assets').textContent).toBe(
      en(IMPORT_REPORT_NOTHING_DROPPED),
    );
    expect(screen.queryByTestId('import-report-reason-Assets-DROPPED_SCRIPT')).toBeNull();
  });

  it('renders nothing when the report is null or absent', () => {
    renderPanel(null);
    expect(screen.queryByTestId('import-report')).toBeNull();

    cleanup();
    renderPanel(undefined);
    expect(screen.queryByTestId('import-report')).toBeNull();
  });
});
