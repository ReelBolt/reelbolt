// @vitest-environment jsdom
/**
 * The side-by-side two-version diff (P3-04, SPEC §13.3): the counts summary and a before/after row
 * per changed path, colour values as swatch + hex, provenance shown as a caption.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { DesignDiff } from '@/lib/types/design-system';
import { VersionDiff } from './VersionDiff';

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

const diff: DesignDiff = {
  counts: { added: 0, removed: 0, changed: 1 },
  added: [],
  removed: [],
  changed: [
    {
      path: 'color.primary',
      before: { hex: '#6d28d9' },
      after: { hex: '#7c3aed' },
      prov: { method: 'static', importer: 'css-theme', sourceId: null, loc: 'app/globals.css:12' },
    },
  ],
};

describe('VersionDiff', () => {
  it('renders the changed count, both hex values and the provenance location', () => {
    render(
      <MantineProvider>
        <VersionDiff diff={diff} />
      </MantineProvider>,
    );

    expect(screen.getByTestId('diff-changed').textContent).toContain('1');
    expect(screen.getByText('#6d28d9')).toBeTruthy();
    expect(screen.getByText('#7c3aed')).toBeTruthy();
    expect(screen.getByText('app/globals.css:12')).toBeTruthy();
    expect(screen.getByText('color.primary')).toBeTruthy();
  });

  it('shows a font sample on both sides of a font-family change', () => {
    const fontDiff: DesignDiff = {
      counts: { added: 0, removed: 0, changed: 1 },
      added: [],
      removed: [],
      changed: [{ path: 'typography.family.heading', before: ['Inter', 'sans-serif'], after: ['Comic Neue'] }],
    };
    render(
      <MantineProvider>
        <VersionDiff diff={fontDiff} />
      </MantineProvider>,
    );

    const statuses = screen.getAllByTestId('type-specimen-sample').map((node) => node.getAttribute('data-font-status'));
    expect(statuses).toEqual(['available', 'unavailable']);
  });
});
