// @vitest-environment jsdom
/**
 * The run's Resolution control: what the plan allows decides which choices are offered (read from
 * the real billing-summary hook, with only the network stubbed), a disabled choice is explained in
 * text with a way to upgrade, and the run carries `outputResolution` only when one was picked.
 */
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';

const { SUMMARY, READS } = vi.hoisted(() => ({
  SUMMARY: { current: (() => Promise.reject(new Error('unset'))) as () => Promise<unknown> },
  READS: { count: 0 },
}));
vi.mock('@/lib/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/client')>();
  return {
    ...actual,
    // Only the billing summary is read here (no project/workflow, so no estimate is fetched).
    fetcher: (url: string) =>
      url.includes('/billing/summary')
        ? (READS.count++, SUMMARY.current())
        : Promise.reject(new actual.ApiError('Not Found', 404)),
  };
});
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({ data: [] }),
  useEditingStyles: () => ({ data: [] }),
}));

import { RunWithInputsModal } from '@/components/workflow-apps/RunWithInputsModal';
import { RunStylePicker } from '@/components/styles/RunStylePicker';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});
afterEach(() => {
  cleanup();
  READS.count = 0;
});

/** Lets the summary read happen and SWR commit its answer, so "enabled" is not just "not loaded yet". */
async function summarySettled() {
  await waitFor(() => expect(READS.count).toBeGreaterThan(0));
  await new Promise((r) => setTimeout(r, 0));
}

/** A summary with only the field the picker reads; the rest of the shape is not this test's business. */
const summaryWithCap = (maxOutputHeight: number | null) => () => Promise.resolve({ entitlements: { maxOutputHeight } });

function renderModal(onConfirm = vi.fn()) {
  render(
    // A fresh cache per case, so one plan's answer never leaks into the next.
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <RunWithInputsModal projectId="p1" workflowId="w1" opened onClose={vi.fn()} onConfirm={onConfirm} />
      </MantineProvider>
    </SWRConfig>,
  );
  return onConfirm;
}

const radio = (name: string) => screen.getByRole('radio', { name }) as HTMLInputElement;

describe('RunStylePicker resolution', () => {
  it('labels the control and offers Auto, 720p, 1080p and 4K', async () => {
    SUMMARY.current = summaryWithCap(null);
    renderModal();
    expect(screen.getByRole('radiogroup', { name: 'Resolution' })).toBeTruthy();
    for (const name of ['Auto', '720p', '1080p', '4K']) expect(radio(name)).toBeTruthy();
    expect(radio('Auto').checked).toBe(true);
    expect(screen.getByText("Same as today: up to 1080p, or your plan's limit if lower")).toBeTruthy();
  });

  it('Creator (1080p cap): 4K is disabled and the reason is in text with a link to billing', async () => {
    SUMMARY.current = summaryWithCap(1080);
    renderModal();
    await waitFor(() => expect(radio('4K').disabled).toBe(true));
    expect(radio('1080p').disabled).toBe(false);
    expect(radio('720p').disabled).toBe(false);
    expect(radio('Auto').disabled).toBe(false);
    const hint = screen.getByTestId('run-resolution-cap');
    expect(hint.textContent).toContain('Your plan renders up to 1080p. Upgrade to render in 4K.');
    expect(screen.getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing');
  });

  it('Trial (720p cap): 1080p and 4K are disabled and the hint offers both', async () => {
    SUMMARY.current = summaryWithCap(720);
    renderModal();
    await waitFor(() => expect(radio('4K').disabled).toBe(true));
    expect(radio('1080p').disabled).toBe(true);
    expect(radio('720p').disabled).toBe(false);
    expect(screen.getByTestId('run-resolution-cap').textContent).toContain(
      'Your plan renders up to 720p. Upgrade to render at 1080p or 4K.',
    );
  });

  it('Pro (2160p cap): every choice is enabled and no upgrade hint is shown', async () => {
    SUMMARY.current = summaryWithCap(2160);
    renderModal();
    await summarySettled();
    for (const name of ['Auto', '720p', '1080p', '4K']) expect(radio(name).disabled).toBe(false);
    expect(screen.queryByTestId('run-resolution-cap')).toBeNull();
  });

  it('billing off (summary 404): every choice is enabled', async () => {
    SUMMARY.current = () => Promise.reject(new ApiError('Not Found', 404));
    renderModal();
    await summarySettled();
    for (const name of ['Auto', '720p', '1080p', '4K']) expect(radio(name).disabled).toBe(false);
    expect(screen.queryByTestId('run-resolution-cap')).toBeNull();
  });

  it('sends outputResolution only when a resolution other than Auto is picked', async () => {
    SUMMARY.current = summaryWithCap(2160);
    const onConfirm = renderModal();
    await summarySettled();
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({});

    fireEvent.click(screen.getByText('4K'));
    expect(screen.getByText('Ultra HD, 2160p. Uses about 4x the render credits of 1080p')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({ resolution: '2160p' });

    fireEvent.click(screen.getByText('Auto'));
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));
    expect(onConfirm).toHaveBeenLastCalledWith({});
  });
});

describe('RunStylePicker controls', () => {
  it('renders only the listed controls, with ids unique per instance', () => {
    render(
      <MantineProvider>
        <SWRConfig value={{ provider: () => new Map() }}>
          <RunStylePicker controls={['shape']} format="Original" onFormatChange={() => {}} />
          <RunStylePicker controls={['shape', 'resolution']} format="Original" onFormatChange={() => {}} resolution="Auto" onResolutionChange={() => {}} />
        </SWRConfig>
      </MantineProvider>,
    );
    expect(screen.queryByText('Editing style')).toBeNull();
    expect(screen.getAllByText('Video shape')).toHaveLength(2);
    expect(screen.getAllByText('Resolution')).toHaveLength(1);
    const ids = Array.from(document.querySelectorAll('[id$="-format-label"]')).map((e) => e.id);
    expect(new Set(ids).size).toBe(2);
  });
});
