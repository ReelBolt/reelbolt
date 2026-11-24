// @vitest-environment jsdom
/**
 * "You picked the Pro plan" on the dashboard: someone who pressed a plan's button on the pricing
 * page before they had an account. Signing up puts every account on the trial, so the choice used
 * to vanish at sign-in. The notice keeps it one click away and shows no price of its own.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { BillingSummary } from '@/lib/types/billing';

const env = vi.hoisted(() => ({ summary: { data: undefined as unknown, error: undefined as unknown } }));
vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: () => env.summary }));
vi.mock('next/link', () => ({
  default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a>,
}));

import { PickedPlanNotice, pickedPlanHref } from './PickedPlanNotice';

const plan = (key: string, name: string, current = false) => ({ key, name, kind: 'Subscription', priceMonthlyUsd: 79, minSeats: null, includedCredits: 1, priceId: `pri_${key}`, current });
const summaryWith = (plans: ReturnType<typeof plan>[]) => ({ plans }) as unknown as BillingSummary;
const onForget = vi.fn();
const mount = (planKey = 'pro') =>
  render(
    <MantineProvider>
      <PickedPlanNotice planKey={planKey} onForget={onForget} />
    </MantineProvider>,
  );

beforeEach(() => {
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  onForget.mockReset();
  env.summary = { data: summaryWith([plan('creator', 'Creator'), plan('pro', 'Pro')]), error: undefined };
});
afterEach(cleanup);

describe('PickedPlanNotice', () => {
  it('names the plan as the workspace\'s own billing summary names it, and links to its card', () => {
    mount();
    const notice = screen.getByTestId('picked-plan-notice');
    expect(notice.textContent).toContain('You picked the Pro plan');
    expect(notice.textContent).toContain('You can start without paying');
    expect(screen.getByTestId('picked-plan-open').getAttribute('href')).toBe('/settings/billing?plan=pro#plans');
    expect(pickedPlanHref('pro')).toBe('/settings/billing?plan=pro#plans');
    expect(onForget).not.toHaveBeenCalled();
  });

  // Prices have one source and are never typed into copy: the billing page shows the provider's own.
  it('shows no price, credit figure or time-limited offer of its own', () => {
    mount();
    const text = screen.getByTestId('picked-plan-notice').textContent ?? '';
    expect(text).not.toMatch(/[$€£]|\d/);
    expect(text).not.toMatch(/credit|discount|off\b|today|now only|hurry/i);
  });

  it('"Not now" forgets the choice', () => {
    mount();
    fireEvent.click(screen.getByTestId('picked-plan-dismiss'));
    expect(onForget).toHaveBeenCalledTimes(1);
  });

  it('shows nothing, and forgets, for a plan that is not on sale here or that the workspace is already on', async () => {
    mount('enterprise');
    expect(screen.queryByTestId('picked-plan-notice')).toBeNull();
    await waitFor(() => expect(onForget).toHaveBeenCalledTimes(1));
    cleanup();
    onForget.mockReset();
    env.summary = { data: summaryWith([plan('pro', 'Pro', true)]), error: undefined };
    mount('pro');
    expect(screen.queryByTestId('picked-plan-notice')).toBeNull();
    await waitFor(() => expect(onForget).toHaveBeenCalledTimes(1));
  });

  it('shows nothing, and forgets, where billing is switched off (the summary answers 404)', async () => {
    env.summary = { data: undefined, error: new ApiError('Not found', 404) };
    mount();
    expect(screen.queryByTestId('picked-plan-notice')).toBeNull();
    await waitFor(() => expect(onForget).toHaveBeenCalledTimes(1));
  });

  it('shows nothing while the summary loads or fails to load, and keeps the choice for the next visit', async () => {
    env.summary = { data: undefined, error: undefined };
    mount();
    expect(screen.queryByTestId('picked-plan-notice')).toBeNull();
    cleanup();
    env.summary = { data: undefined, error: new ApiError('Server error', 500) };
    mount();
    expect(screen.queryByTestId('picked-plan-notice')).toBeNull();
    await new Promise((r) => setTimeout(r, 20));
    expect(onForget).not.toHaveBeenCalled();
  });
});
