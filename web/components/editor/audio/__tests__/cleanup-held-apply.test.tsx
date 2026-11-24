// @vitest-environment jsdom
/** The panel's Apply records "applied" only when the editor really took the batch (a held one keeps waiting). */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditOp } from '@/lib/editing/engine';
import type { CleanupEstimate, CleanupJob } from '@/lib/editing/cleanup/types';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { CleanupPanel } from '../CleanupPanel';
import type { CleanupJobApi } from '../useCleanupJob';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
Element.prototype.scrollIntoView = vi.fn();
window.HTMLMediaElement.prototype.pause = vi.fn();
window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);

const FREE: CleanupEstimate = { billingEnabled: true, hosted: false, credits: 0, affordable: true, cached: false };
const READY: CleanupJob = { executionId: 'e1', state: 'ready', target: 'Hum', applied: true, outputFileId: 'out-1' };
const d = doc([track('A1', 'audio', [media('a', 0, 100)])]);

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
  setActiveTimeline('t', () => d);
});
afterEach(() => {
  cleanup();
  setActiveTimeline(null);
});

describe('CleanupPanel Apply with the editor store open', () => {
  it('a held batch is not recorded as applied; a landed one is', async () => {
    const api: CleanupJobApi = { estimate: vi.fn().mockResolvedValue(FREE), start: vi.fn().mockResolvedValue({ executionId: 'e1', target: 'Hum', hosted: false }), wait: vi.fn().mockResolvedValue(READY) };
    const results = getResultsStore('t');
    let landed = false;
    const onApply = vi.fn((_ops: readonly EditOp[], _label: string) => landed);
    render(
      <MantineProvider>
        <CleanupPanel doc={d} selection={['a']} projectId="p1" readOnly={false} onApply={onApply} api={api} userId="u@x" orgId="o1" />
      </MantineProvider>,
    );
    fireEvent.click(await screen.findByTestId('cleanup-choice-hum'));
    fireEvent.click(await screen.findByTestId('cleanup-run'));
    fireEvent.click(await screen.findByTestId('cleanup-apply'));
    await waitFor(() => expect(onApply).toHaveBeenCalledTimes(1));
    const entry = () => results.getEntries().find((e) => e.id === 'cleanup:e1');
    expect(entry()?.state).toBe('needsReview');
    expect(screen.queryByTestId('cleanup-applied')).toBeNull();
    landed = true;
    fireEvent.click(screen.getByTestId('cleanup-apply'));
    await waitFor(() => expect(entry()?.state).toBe('applied'));
  });
});
