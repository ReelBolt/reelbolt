/**
 * T-088: every control in the Remove silence dialog is a >= 44 px touch target on a coarse pointer
 * or a phone width, and the neutral and destructive footer actions sit >= 8 px apart. jsdom does not
 * lay out, so this pins the inline sizes the dialog sets.
 */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { assetRef, doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { RemoveSilenceDialog } from '../panels/RemoveSilenceDialog';

let touch = false;
const matchMediaStub = (query: string): MediaQueryList =>
  ({
    matches: touch && /pointer:\s*coarse|max-width/.test(query),
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

const P = '11111111-1111-4111-8111-111111111111';
const SEED = '22222222-2222-4222-8222-222222222222';
const ANALYSIS = '33333333-3333-4333-8333-333333333333';
const EXEC = '44444444-4444-4444-8444-444444444444';
const KEY = `projects/${P}/agentFiles/video-analysis/${EXEC}/step-1-analysis.json`;

const routes: Record<string, unknown> = {
  [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: { version: 1, analysisArtifactStorageKey: KEY, analysisStepOrder: 1, analysisStepResultId: null, segments: [{ index: 0, snappedStartSec: 0, snappedEndSec: 10, sourceIndex: 0 }] },
  [`/api/v1/projects/${P}/executions/${EXEC}`]: { id: EXEC, stepResults: [{ id: ANALYSIS, artifactStorageKey: KEY }] },
  [`/api/v1/projects/${P}/step-results/${ANALYSIS}/artifact`]: {
    sources: [{ sourceIndex: 0, storageKey: 'a.mp4' }],
    silenceSpans: [{ id: 'g0', startSec: 3, endSec: 4.5, sourceIndex: 0 }],
  },
  [`/api/v1/projects/${P}/files`]: [{ id: assetRef(1).projectFileId, storageKey: 'a.mp4' }],
};
const fetchStub = vi.fn(async (url: string) => {
  if (!(url in routes)) return { ok: false, status: 404, statusText: 'Not Found', json: async () => ({}) };
  return { ok: true, status: 200, statusText: 'OK', json: async () => routes[url] };
});

beforeEach(() => {
  vi.stubGlobal('fetch', fetchStub);
  vi.stubGlobal('matchMedia', matchMediaStub);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  touch = false;
});

const renderDialog = () =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <RemoveSilenceDialog
          opened
          onClose={() => {}}
          doc={doc([track(TRACKS.v1, 'video', [media('a', 0, 300, 0, { assetRef: assetRef(1) })])])}
          projectId={P}
          seedStepResultId={SEED}
          newItemId={() => 'n'}
          onApply={() => {}}
        />
      </MantineProvider>
    </SWRConfig>,
  );

const minHeightPx = (el: HTMLElement | null) => parseFloat(el?.style.minHeight ?? '') || 0;

describe('RemoveSilenceDialog touch targets (T-088)', () => {
  it('makes every control at least 44 px and spaces Cancel and Remove at least 8 px apart on touch', async () => {
    touch = true;
    renderDialog();
    const span = await waitFor(() => screen.getByTestId('remove-silence-span-g0'));

    const apply = screen.getByTestId('remove-silence-apply');
    const cancel = screen.getByTestId('remove-silence-cancel');
    expect(minHeightPx(apply)).toBeGreaterThanOrEqual(44);
    expect(minHeightPx(cancel)).toBeGreaterThanOrEqual(44);
    expect(cancel.textContent).toBe('Cancel');
    expect(apply.textContent).toBe('Remove');

    // The footer group holding both actions.
    const footer = apply.parentElement as HTMLElement;
    expect(footer).toBe(cancel.parentElement);
    expect(footer.style.getPropertyValue('--group-gap')).toBe('var(--mantine-spacing-sm)');

    for (const id of ['remove-silence-track', 'remove-silence-pad', 'remove-silence-min']) {
      expect(minHeightPx(screen.getByTestId(id))).toBeGreaterThanOrEqual(44);
    }
    // The checkbox's label is its click target.
    expect(minHeightPx(span.closest('.mantine-Checkbox-root')?.querySelector('label') as HTMLElement)).toBeGreaterThanOrEqual(44);

    const close = screen.getByRole('button', { name: 'Close' });
    expect(close.style.getPropertyValue('--cb-size')).toMatch(/2\.75rem|44/);
  });

  it('keeps the compact desktop sizes on a fine pointer', async () => {
    renderDialog();
    await waitFor(() => screen.getByTestId('remove-silence-span-g0'));
    expect(minHeightPx(screen.getByTestId('remove-silence-apply'))).toBe(0);
    expect(minHeightPx(screen.getByTestId('remove-silence-cancel'))).toBe(0);
  });
});
