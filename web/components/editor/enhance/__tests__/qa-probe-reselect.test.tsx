// @vitest-environment jsdom
/**
 * QA probe (T-104): the panel must not lose its job after Apply re-keys the clip and the clip is re-selected.
 * InspectorPanel mounts EnhanceSection with key `enhance-${item.id}`, so selecting another clip and back remounts it.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { MantineProvider } from '@mantine/core';
import { assetRef, doc, media, track } from '@/lib/editing/__tests__/fixtures';
import { apply, type EditOp } from '@/lib/editing/engine';
import type { EnhanceEstimate, EnhanceJob } from '@/lib/editing/enhance/types';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { EnhanceSection } from '../EnhanceSection';
import type { EnhanceJobApi } from '../useEnhanceJob';
import { resetEnhanceStore } from '../enhance-store';
import { registerEnhanceHost } from '../enhance-host';

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

afterEach(() => {
  cleanup();
  resetEnhanceStore();
  registerEnhanceHost(null);
});

const EST: EnhanceEstimate = { billingEnabled: true, hosted: false, credits: 0, seconds: 10, balanceCredits: 10, affordable: true, unavailableReason: null, consented: false, maxOutputHeight: 2160, outputWidth: 1920, outputHeight: 1080, outputFps: 30, estimatedBytes: 12_000_000, estimatedProcessingSeconds: 40 };
const done: EnhanceJob = { executionId: 'e1', state: 'ready', op: 'Upscale', engine: 'Local', outputFileId: 'out-1', sourceMediaRef: `file:${assetRef(1).projectFileId}` };
const api: EnhanceJobApi = {
  estimate: vi.fn().mockResolvedValue(EST),
  start: vi.fn().mockResolvedValue({ executionId: 'e1', op: 'Upscale', engine: 'Local', hosted: false, state: 'queued', credits: 0 }),
  wait: vi.fn().mockResolvedValue(done),
  cancel: vi.fn().mockResolvedValue({}),
  grantConsent: vi.fn().mockResolvedValue({ consented: true }),
};

/** Mirrors InspectorPanel: one section for the selected clip, keyed by clip id, live doc, ONE batch per call. */
function Editor() {
  const [d, setD] = React.useState<EditTimeline>(() => doc([track('V1', 'video', [media('c1', 0, 100, 0), media('c2', 100, 200, 0, { assetRef: assetRef(2) })])]));
  const [sel, setSel] = React.useState('c1');
  const clip = d.tracks[0].items.find((i) => i.id === sel) as MediaClip;
  const onBatch = (ops: EditOp[]) => setD((cur) => ops.reduce((acc, op) => apply(acc, op).doc, cur));
  return (
    <>
      <button data-testid="sel-c1" onClick={() => setSel('c1')} />
      <button data-testid="sel-c2" onClick={() => setSel('c2')} />
      <EnhanceSection key={`enhance-${clip.id}`} clip={clip} projectId="p1" readOnly={false} doc={d} onBatch={onBatch} api={api} />
    </>
  );
}

describe('QA probe: Apply, select another clip, select back', () => {
  it('still offers "Use original" for the applied clip', async () => {
    render(<MantineProvider><Editor /></MantineProvider>);
    fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
    fireEvent.click(await screen.findByText('Sharper picture'));
    await waitFor(() => expect(screen.getByTestId('enhance-estimate')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-start'));
    await waitFor(() => expect(screen.getByTestId('enhance-done')).toBeTruthy(), { timeout: 5000 });
    fireEvent.click(screen.getByTestId('enhance-apply'));
    await waitFor(() => expect(screen.getByTestId('enhance-use-original')).toBeTruthy(), { timeout: 5000 });
    // Same mounted section: fine. Now the user selects another clip and comes back.
    fireEvent.click(screen.getByTestId('sel-c2'));
    fireEvent.click(screen.getByTestId('sel-c1'));
    expect(screen.queryByTestId('enhance-use-original')).not.toBeNull();
  }, 20000);
});
