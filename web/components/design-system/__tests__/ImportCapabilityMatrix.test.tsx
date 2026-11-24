// @vitest-environment jsdom
/**
 * The importer capability matrix (T-101; SPEC §17): a mocked response renders one row per importer
 * and one column per axis, every cell says Full/Partial/None in words, the note is available on
 * expand (and as the cell's accessible name), and a failed fetch degrades to one short line.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/api/design-system', () => ({
  getImportCapabilities: vi.fn(),
}));

import { getImportCapabilities } from '@/lib/api/design-system';
import type { ImportCapabilitiesResponse, ImportCapabilitySupport } from '@/lib/types/design-system';
import { ImportCapabilityMatrix } from '../ImportCapabilityMatrix';
import { CAPABILITIES_UNAVAILABLE } from '../copy';
import { en } from './en';

const AXES = [
  'Tokens',
  'Modes',
  'Assets',
  'Fonts',
  'Components',
  'PreviewCards',
  'Prose',
  'Voice',
  'DerivedTokens',
  'ReferenceDocuments',
] as const;

/** The 13 importer ids the server promises: one row each. */
const IMPORTERS: { id: string; label: string }[] = [
  { id: 'dtcg', label: 'DTCG JSON' },
  { id: 'css-theme', label: 'CSS theme' },
  { id: 'design-md', label: 'DESIGN.md' },
  { id: 'tokens-studio', label: 'Tokens Studio' },
  { id: 'shadcn-registry', label: 'shadcn registry' },
  { id: 'storybook-index', label: 'Storybook index' },
  { id: 'style-dictionary', label: 'Style Dictionary' },
  { id: 'tailwind-v3', label: 'Tailwind v3 config' },
  { id: 'figma-variables', label: 'Figma variables' },
  { id: 'brand-pdf', label: 'Brand PDF' },
  { id: 'penpot', label: 'Penpot export' },
  { id: 'sketch', label: 'Sketch file' },
  { id: 'claude-design', label: 'Claude Design' },
];

const SUPPORTS: ImportCapabilitySupport[] = ['Full', 'Partial', 'None'];

const NOTE = 'Reads colours and other values from the file.';

/** A 13 × 10 response whose support levels cycle, so all three words appear. */
function capabilityResponse(): ImportCapabilitiesResponse {
  return {
    schema: 'reelbolt.import-capabilities/1',
    importers: IMPORTERS.map((importer, rowIndex) => ({
      ...importer,
      axes: AXES.map((axis, axisIndex) => ({
        axis,
        support: SUPPORTS[(rowIndex + axisIndex) % SUPPORTS.length],
        note: `${importer.label} · ${axis}: ${NOTE}`,
      })),
    })),
  };
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

beforeEach(() => {
  vi.mocked(getImportCapabilities).mockReset();
  vi.mocked(getImportCapabilities).mockResolvedValue(capabilityResponse());
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderMatrix() {
  render(
    <MantineProvider>
      <ImportCapabilityMatrix />
    </MantineProvider>,
  );
}

describe('ImportCapabilityMatrix', () => {
  it('renders one row per importer and one column per axis', async () => {
    renderMatrix();

    expect(await screen.findByTestId('capability-matrix')).toBeTruthy();
    expect(getImportCapabilities).toHaveBeenCalledTimes(1);
    expect(screen.getAllByTestId(/^capability-row-/)).toHaveLength(13);

    for (const importer of IMPORTERS) {
      expect(screen.getByTestId(`capability-row-${importer.id}`)).toBeTruthy();
      expect(screen.getByText(importer.label)).toBeTruthy();
    }

    // Every cell of every row is present — 13 rows × 10 axes.
    expect(screen.getAllByTestId(/^capability-cell-/)).toHaveLength(130);
  });

  it('shows Full, Partial and None as words, not colour alone', async () => {
    renderMatrix();

    await screen.findByTestId('capability-matrix');
    expect(screen.getAllByText('Full').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Partial').length).toBeGreaterThan(0);
    expect(screen.getAllByText('None').length).toBeGreaterThan(0);
  });

  it('carries the note in the cell label and shows it when the row is expanded', async () => {
    renderMatrix();

    await screen.findByTestId('capability-matrix');
    // The cell's accessible name names importer, axis, support and the reason.
    expect(screen.getByLabelText(/DTCG JSON — Colours and values: Full/)).toBeTruthy();

    const toggle = screen.getByTestId('capability-toggle-claude-design');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);

    expect(screen.getByTestId('capability-notes-claude-design')).toBeTruthy();
    expect(screen.getByText(`Claude Design · Tokens: ${NOTE}`)).toBeTruthy();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(toggle);
    expect(screen.queryByTestId('capability-notes-claude-design')).toBeNull();
  });

  it('degrades to a short unavailable line when the fetch fails', async () => {
    vi.mocked(getImportCapabilities).mockRejectedValueOnce(new Error('offline'));
    renderMatrix();

    expect(await screen.findByTestId('capability-unavailable')).toBeTruthy();
    expect(screen.getByText(en(CAPABILITIES_UNAVAILABLE))).toBeTruthy();
    expect(screen.queryByTestId('capability-matrix')).toBeNull();
  });
});
