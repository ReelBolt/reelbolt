// @vitest-environment jsdom
/** T-002 (R12.2): the "For another editor" card: states ok, over cap, busy/refused, phone note. */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { ExportScope } from '@/lib/editing/sync/api';
import { HandOffCard } from '../HandOffCard';

let device: 'phone' | 'desktop' = 'desktop';
let scope: ExportScope;
vi.mock('@/lib/editing/use-device-class', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/use-device-class')>()),
  useDeviceClass: () => device,
}));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  getExportScope: async () => scope,
}));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const scopeOf = (over: Partial<NonNullable<ExportScope['handOff']>>): ExportScope => ({
  format: 'otio',
  carries: ['Cuts'],
  dropped: [],
  mediaMode: 'reelbolt-url',
  handOff: { supported: true, mediaFiles: 12, mediaBytes: 1.4 * 1024 ** 3, wouldExceedCap: false, capBytes: 2 * 1024 ** 3, ...over },
});

const mount = () =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <HandOffCard projectId="p1" timelineId="t1" doc={{ tracks: [{ id: 'v1', name: 'Video 1', kind: 'video', items: [] }] }} dirty={false} />
      </MantineProvider>
    </SWRConfig>,
  );

beforeEach(() => {
  device = 'desktop';
  scope = scopeOf({});
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('HandOffCard', () => {
  it('shows the package size and links to the hand-off endpoint', async () => {
    mount();
    expect((await screen.findByTestId('handoff-size')).textContent).toContain('12 files, 1.4 GB');
    const link = screen.getByTestId('handoff-download');
    expect(link.getAttribute('href')).toBe('/api/v1/projects/p1/edit-timelines/t1/handoff?format=otio&media=zip');
    expect((screen.getByTestId('handoff-media-zip') as HTMLInputElement).disabled).toBe(false);
  });

  it('over the cap: files option disabled with the reason, list only chosen', async () => {
    scope = scopeOf({ wouldExceedCap: true, mediaBytes: 3 * 1024 ** 3 });
    mount();
    expect((await screen.findByTestId('handoff-over-cap')).textContent).toContain('3 GB');
    expect((screen.getByTestId('handoff-media-zip') as HTMLInputElement).disabled).toBe(true);
    await waitFor(() => expect(screen.getByTestId('handoff-download').getAttribute('href')).toContain('media=manifest'));
  });

  it('a busy answer reads as a sentence and nothing navigates', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ code: 'HANDOFF_BUSY', message: 'busy' }), { status: 429 })));
    mount();
    await screen.findByTestId('handoff-size');
    fireEvent.click(screen.getByTestId('handoff-download'));
    expect((await screen.findByTestId('handoff-problem')).textContent).toMatch(/already being prepared/);
  });

  it('format follows the editor and the how-to-open lines never claim a test', async () => {
    mount();
    await screen.findByTestId('handoff-size');
    fireEvent.click(screen.getByTestId('handoff-how-toggle'));
    expect((screen.getByTestId('handoff-how')).textContent).toContain('Should open in Premiere Pro. Tell us if it does not.');
    expect(screen.getByTestId('handoff-how').textContent).not.toMatch(/tested in/i);
  });

  it('phone: a plain note and no button', () => {
    device = 'phone';
    mount();
    expect((screen.getByTestId('handoff-phone-note')).textContent).toMatch(/desktop job/);
    expect(screen.queryByTestId('handoff-download')).toBeNull();
  });
});

describe('early preview label', () => {
  const LINE = 'Hand-off packages are an early preview: we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut Pro.';
  it('shows the label and the line on desktop', async () => {
    device = 'desktop';
    scope = scopeOf({});
    mount();
    expect(screen.getByTestId('early-preview-label')).toBeTruthy();
    expect(screen.getByTestId('handoff-early-line').textContent).toBe(LINE);
  });
  it('shows the label and the line on a phone', () => {
    device = 'phone';
    mount();
    expect(screen.getByTestId('early-preview-label')).toBeTruthy();
    expect(screen.getByTestId('handoff-early-line').textContent).toBe(LINE);
    device = 'desktop';
  });
});
