// @vitest-environment jsdom

/**
 * The 402 state (B10c-UI): a run the server refused before queueing anything. Everything shown has
 * to come from the refusal body, so this tests that each figure reaches the screen — and that the
 * state is presented as something to fix rather than as a failure.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InsufficientCreditsAlert } from './InsufficientCreditsAlert';

const billing = vi.hoisted(() => ({ topUpAvailable: true as boolean | undefined }));
vi.mock('@/lib/hooks/use-billing', () => ({
  useBillingSummary: () => ({
    data: billing.topUpAvailable === undefined ? undefined : { topUp: { available: billing.topUpAvailable, packs: [] } },
  }),
}));
import type { InsufficientCredits } from '@/lib/types/billing';

const refusal = (over: Partial<InsufficientCredits> = {}): InsufficientCredits => ({
  required: 52,
  balance: 12,
  shortfall: 40,
  low: 30,
  high: 52,
  hasHistory: true,
  message: null,
  ...over,
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

afterEach(() => { cleanup(); billing.topUpAvailable = true; });

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('InsufficientCreditsAlert', () => {
  it('states the requirement, the balance and the shortfall, and sends the reader to the top-up page', () => {
    wrap(<InsufficientCreditsAlert data={refusal()} />);

    expect(screen.getByText(/Not enough credits to start this run/)).toBeTruthy();
    expect(screen.getByText(/52 credits up front/)).toBeTruthy();
    expect(screen.getByText(/12 available/)).toBeTruthy();
    expect(screen.getByText(/40 credits short/)).toBeTruthy();
    // The refusal happens before anything is queued: say so rather than leaving people wondering.
    expect(screen.getByText(/Nothing was started and nothing was charged/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /Top up credits/ }).getAttribute('href')).toBe('/settings/billing');
  });

  it('explains the gap between the estimate and what has to be available up front', () => {
    wrap(<InsufficientCreditsAlert data={refusal()} />);

    expect(screen.getByText(/estimated at ≈ 30–52 credits/)).toBeTruthy();
    expect(screen.getByText(/has to be available before it starts/)).toBeTruthy();
  });

  it('says when the figure behind the refusal is a typical one rather than a measurement', () => {
    wrap(<InsufficientCreditsAlert data={refusal({ hasHistory: false })} />);

    expect(screen.getByText(/no finished runs of its own to measure yet/)).toBeTruthy();
  });

  it('omits the range when the body did not carry one, instead of printing zeroes', () => {
    wrap(<InsufficientCreditsAlert data={refusal({ low: null, high: null })} />);

    expect(screen.getByText(/52 credits up front/)).toBeTruthy();
    expect(screen.queryByText(/estimated at/)).toBeNull();
  });

  it('can be dismissed, and reports the dismissal', () => {
    const onDismiss = vi.fn();
    wrap(<InsufficientCreditsAlert data={refusal()} onDismiss={onDismiss} />);

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(onDismiss).toHaveBeenCalled();
  });

  it('offers no dismiss control when the caller does not handle one', () => {
    wrap(<InsufficientCreditsAlert data={refusal()} />);

    expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();
  });

  it('sends a Trial workspace to the plans, not to a top-up page that says "paid plans only"', () => {
    billing.topUpAvailable = false;
    wrap(<InsufficientCreditsAlert data={refusal()} />);

    const cta = screen.getByTestId('insufficient-credits-cta');
    expect(cta.textContent).toBe('Choose a plan');
    expect(cta.getAttribute('href')).toBe('/settings/billing#plans');
    expect(screen.queryByText('Top up credits')).toBeNull();
  });
});

describe('InsufficientCreditsAlert upgrade events', () => {
  it('reports shown on mount and clicked on the CTA', async () => {
    const { emitUpgradeEvent } = await import('@/lib/telemetry/upgrade');
    const spy = vi.spyOn(await import('@/lib/telemetry/upgrade'), 'emitUpgradeEvent');
    wrap(<InsufficientCreditsAlert data={refusal()} />);
    expect(spy).toHaveBeenCalledWith('billing.upgrade_prompt_shown', 'insufficient_credits_alert');
    fireEvent.click(screen.getByTestId('insufficient-credits-cta'));
    expect(spy).toHaveBeenCalledWith('billing.upgrade_clicked', 'insufficient_credits_alert');
    expect(emitUpgradeEvent).toBeTypeOf('function');
    spy.mockRestore();
  });
});
