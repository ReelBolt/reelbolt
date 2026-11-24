// @vitest-environment jsdom
/**
 * Wave 5: "Export for other apps" on the Deliver page. The file type is chosen first, what it carries and leaves out
 * (the server's own lists) is read next, and the download of that type comes after that account. The hand-off card
 * draws no second title under the page's panel header.
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { EditTimelineExportFormat, ExportScope } from '@/lib/editing/sync/api';
import { ExportPanel } from '../ExportPanel';
import { HandOffCard } from '../HandOffCard';

const scopeCalls: EditTimelineExportFormat[] = [];
let scopeImpl: (format: EditTimelineExportFormat) => Promise<ExportScope>;
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  getExportScope: (_p: string, _t: string, format: EditTimelineExportFormat) => {
    scopeCalls.push(format);
    return scopeImpl(format);
  },
}));
vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => 'desktop',
}));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const SCOPES: Record<string, ExportScope> = {
  otio: { format: 'otio', carries: ['Every track and clip', 'Markers'], dropped: [], mediaMode: 'reelbolt-url' },
  edl: { format: 'edl', carries: ['The cuts of one video track'], dropped: ['Effects', 'Titles'], mediaMode: 'none' },
};

beforeEach(() => {
  scopeCalls.length = 0;
  scopeImpl = async (format) => SCOPES[format] ?? { format, carries: ['Captions'], dropped: [], mediaMode: 'none' };
});
afterEach(() => cleanup());

const doc = { tracks: [{ id: 'v1', name: 'Video 1', kind: 'video', items: [] }] };
const mount = (props: Partial<React.ComponentProps<typeof ExportPanel>> = {}) =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <ExportPanel projectId="p1" timelineId="t1" doc={doc} captionCount={2} dirty={false} layout="page" {...props} />
      </MantineProvider>
    </SWRConfig>,
  );

const follows = (first: Element, second: Element): boolean => (first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;

describe('ExportPanel on the Deliver page', () => {
  it('shows what the chosen file type carries and leaves out before its download', async () => {
    mount();
    expect(screen.getByTestId('export-scope-loading')).toBeTruthy();
    const carries = await screen.findByTestId('export-scope-carries');
    expect(Array.from(carries.querySelectorAll('li')).map((li) => li.textContent)).toEqual(['Every track and clip', 'Markers']);
    expect(screen.getByTestId('export-scope').textContent).toContain('Nothing from the timeline.');
    expect(screen.getByTestId('export-scope-media').textContent?.length).toBeGreaterThan(10);

    const download = screen.getByTestId('deliver-export-otio');
    expect(download.tagName).toBe('A');
    expect(download.getAttribute('data-primary')).toBe('true');
    expect(download.textContent).toBe('Download OpenTimelineIO');
    expect(download.getAttribute('href')).toBe('/api/v1/projects/p1/edit-timelines/t1/export?format=otio');
    expect(download.hasAttribute('download')).toBe(true);
    // The order on the page: the type, its account, then the download.
    expect(follows(screen.getByTestId('export-scope-format'), screen.getByTestId('export-scope-summary'))).toBe(true);
    expect(follows(carries, download)).toBe(true);
    expect(follows(screen.getByTestId('export-scope-media'), download)).toBe(true);
  });

  it('asks the server for the chosen type and moves the main download to it', async () => {
    mount();
    await screen.findByTestId('export-scope-carries');
    fireEvent.click(screen.getByTestId('export-scope-format'));
    fireEvent.click(screen.getByRole('option', { name: 'CMX3600 EDL', hidden: true }));
    await waitFor(() => expect(Array.from(screen.getByTestId('export-scope-dropped').querySelectorAll('li')).map((li) => li.textContent)).toEqual(['Effects', 'Titles']));
    expect(scopeCalls).toContain('edl');
    expect(screen.getByTestId('deliver-export-edl').getAttribute('data-primary')).toBe('true');
    expect(screen.getByTestId('deliver-export-otio').getAttribute('data-primary')).toBeNull();
    expect(screen.getByTestId('export-scope-summary').textContent).toContain('Cuts only');
  });

  it('keeps one link per file type, each a plain download, with no control drawn twice', async () => {
    mount();
    await screen.findByTestId('export-scope-carries');
    for (const format of ['otio', 'edl', 'srt', 'vtt', 'fcpxml']) {
      expect(screen.getAllByTestId(`deliver-export-${format}`)).toHaveLength(1);
      expect(screen.getByTestId(`deliver-export-${format}`).getAttribute('href')).toContain(`format=${format}`);
    }
    expect(screen.queryByTestId('handoff-card'), 'the hand-off package is its own section on the page').toBeNull();
  });

  it('offers no empty caption file when the timeline has no captions', async () => {
    mount({ captionCount: 0 });
    await screen.findByTestId('export-scope-carries');
    for (const format of ['srt', 'vtt']) {
      const el = screen.getByTestId(`deliver-export-${format}`);
      expect(el.tagName).not.toBe('A');
      expect(el.textContent).toContain('no captions yet');
    }
    expect(screen.getByTestId('deliver-export-otio').tagName).toBe('A');
  });

  it('says so when the account could not be loaded, and the download still works', async () => {
    scopeImpl = async () => {
      throw new Error('offline');
    };
    mount();
    expect((await screen.findByTestId('export-scope-error')).textContent).not.toBe('');
    expect(screen.getByTestId('deliver-export-otio').getAttribute('href')).toContain('format=otio');
  });

  it('the phone sheet keeps its layout: the five downloads first and the hand-off note under them', async () => {
    mount({ layout: 'compact' });
    await screen.findByTestId('export-scope-carries');
    expect(follows(screen.getByTestId('deliver-export-otio'), screen.getByTestId('export-scope-format'))).toBe(true);
    expect(screen.getByTestId('deliver-export-otio').textContent).toBe('OpenTimelineIO');
    expect(screen.getByTestId('handoff-card')).toBeTruthy();
  });
});

describe('HandOffCard under a panel header', () => {
  const card = (heading: boolean) =>
    render(
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MantineProvider>
          <HandOffCard projectId="p1" timelineId="t1" doc={doc} dirty={false} heading={heading} />
        </MantineProvider>
      </SWRConfig>,
    );

  it('draws its own title only when nothing above names it; the early-preview label stays either way', () => {
    const first = card(true);
    expect(screen.getByText('For another editor')).toBeTruthy();
    first.unmount();
    card(false);
    expect(screen.queryByText('For another editor')).toBeNull();
    expect(screen.getByTestId('handoff-early-line')).toBeTruthy();
    expect(screen.getByTestId('handoff-download')).toBeTruthy();
  });
});
