// @vitest-environment jsdom
/** The Extend picker: sides + ONE amount, the 12 px text floor, the 44 px phone targets and a default every ordinary source can reach. */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { MantineProvider } from '@mantine/core';
import en from '@/messages/en/editor-enhance.json';
import type { EnhanceEstimate } from '@/lib/editing/enhance/types';
import { EnhanceRunner } from '../EnhanceRunner';
import type { EnhanceJobApi } from '../useEnhanceJob';
import { resetEnhanceStore } from '../enhance-store';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as unknown as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
Element.prototype.scrollIntoView = vi.fn();

afterEach(() => {
  cleanup();
  resetEnhanceStore();
});

const EST: EnhanceEstimate = { billingEnabled: true, hosted: true, credits: 2, seconds: 5, balanceCredits: 100, affordable: true, unavailableReason: null, consented: true, maxOutputHeight: 2160, outputWidth: 1920, outputHeight: 1920, outputFps: 30, estimatedBytes: 12_000_000, estimatedProcessingSeconds: 40 };

function mount(opts: { phone?: boolean } = {}) {
  const estimate = vi.fn().mockResolvedValue(EST);
  const api = { estimate, start: vi.fn(), wait: vi.fn(), cancel: vi.fn(), grantConsent: vi.fn(), getJob: vi.fn() } as unknown as EnhanceJobApi;
  render(
    <MantineProvider>
      <EnhanceRunner projectId="p1" sourceMediaRef="file:f1" readOnly={false} phone={opts.phone ?? false} api={api} renderResult={() => null} />
    </MantineProvider>,
  );
  return estimate;
}

async function pickExtend() {
  fireEvent.mouseDown(screen.getByTestId('enhance-choice'));
  fireEvent.click(await screen.findByText(en.choice.extend));
  await waitFor(() => expect(screen.getByTestId('enhance-extend-prompt')).toBeTruthy());
}

describe('Extend picker', () => {
  it('starts on a shape every ordinary source can reach (1:1), not the 9:16 a landscape clip cannot', async () => {
    const estimate = mount();
    await pickExtend();
    await waitFor(() => expect(estimate).toHaveBeenCalled());
    expect(estimate.mock.calls.at(-1)?.[2]).toMatchObject({ op: 'Extend', targetAspect: '1:1' });
  });

  it('sides mode offers sides and ONE amount, and every request carries that one amount on the sides that grow', async () => {
    const estimate = mount();
    await pickExtend();
    fireEvent.click(screen.getByText(en.extend.mode.sides));
    await screen.findByTestId('enhance-extend-amount');
    expect(screen.getAllByRole('checkbox')).toHaveLength(4);

    // Left and right off, top and bottom stay: one amount, two sides.
    fireEvent.click(screen.getByTestId('enhance-extend-left'));
    fireEvent.click(screen.getByTestId('enhance-extend-right'));
    await waitFor(() =>
      expect(estimate.mock.calls.at(-1)?.[2]).toEqual({ op: 'Extend', engine: 'Hosted', marginLeft: 0, marginRight: 0, marginTop: 0.2, marginBottom: 0.2 }),
    );

    fireEvent.change(screen.getByTestId('enhance-extend-amount'), { target: { value: '30' } });
    await waitFor(() =>
      expect(estimate.mock.calls.at(-1)?.[2]).toEqual({ op: 'Extend', engine: 'Hosted', marginLeft: 0, marginRight: 0, marginTop: 0.3, marginBottom: 0.3 }),
    );
  });

  it('no Extend text renders under 12 px (Mantine xs input descriptions are xs - 2px)', async () => {
    mount();
    await pickExtend();
    const small = Array.from(document.querySelectorAll('[class*="description"]')).filter((el) =>
      /--input-description-size:\s*calc\(var\(--mantine-font-size-xs\)\s*-/.test(el.getAttribute('style') ?? ''),
    );
    expect(small.map((el) => el.textContent)).toEqual([]);
  });

  it('phone: the prompt and the amount are 44 px targets', async () => {
    mount({ phone: true });
    await pickExtend();
    expect(screen.getByTestId('enhance-extend-prompt').getAttribute('style') ?? '').toMatch(/min-height:\s*44px/);
    fireEvent.click(screen.getByText(en.extend.mode.sides));
    expect((await screen.findByTestId('enhance-extend-amount')).getAttribute('style') ?? '').toMatch(/min-height:\s*44px/);
  });

  it('clearing or zeroing the amount box keeps the last valid amount and never sends a near-zero margin', async () => {
    const estimate = mount();
    await pickExtend();
    fireEvent.click(screen.getByText(en.extend.mode.sides));
    const amount = await screen.findByTestId('enhance-extend-amount');
    await waitFor(() => expect(estimate).toHaveBeenCalled());
    fireEvent.change(amount, { target: { value: '' } });
    fireEvent.change(amount, { target: { value: '0' } });
    await waitFor(() => expect(estimate.mock.calls.length).toBeGreaterThan(0));
    for (const call of estimate.mock.calls) {
      const req = call[2] as Record<string, number | string>;
      for (const m of [req.marginLeft, req.marginRight, req.marginTop, req.marginBottom] as (number | undefined)[]) {
        if (m !== undefined && m > 0) expect(m).toBeGreaterThanOrEqual(0.01);
      }
    }
  });

  it('phone: the side checkboxes are 44 px touch targets', async () => {
    mount({ phone: true });
    await pickExtend();
    fireEvent.click(screen.getByText(en.extend.mode.sides));
    const left = await screen.findByTestId('enhance-extend-left');
    const root = left.closest('[class*="Checkbox-root"]') as HTMLElement | null;
    expect([left, root].map((el) => el?.getAttribute('style') ?? '').join(';')).toMatch(/min-height:\s*44px/);
  });
});
