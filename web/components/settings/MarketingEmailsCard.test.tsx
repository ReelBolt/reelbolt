// @vitest-environment jsdom
/**
 * "Marketing emails" on the account page.
 *
 * What these tests protect:
 *   - the switch reflects the person's own record and shows the date they agreed;
 *   - withdrawing is one click: no dialog, one PUT with optIn false;
 *   - turning it on sends the wording version shown next to the switch, from source "settings";
 *   - a 409 (the wording moved) re-reads the wording and changes nothing.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';

const H = vi.hoisted(() => ({
  getConsent: vi.fn(),
  getWording: vi.fn(),
  update: vi.fn(),
}));

vi.mock('@/lib/api/marketing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/marketing')>();
  return {
    ...actual,
    getMarketingConsent: (...args: unknown[]) => H.getConsent(...args),
    getMarketingWording: (...args: unknown[]) => H.getWording(...args),
    updateMarketingConsent: (...args: unknown[]) => H.update(...args),
  };
});

import { MarketingEmailsCard } from './MarketingEmailsCard';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

const WORDING = {
  version: '2026-10-marketing-v1',
  text: 'Email me ReelBolt product news, tips and offers. You can unsubscribe at any time from any email or in Settings. See our Privacy Policy.',
};

const OPTED_IN = {
  status: 'optedIn',
  wordingVersion: '2026-10-marketing-v1',
  consentedAt: '2026-10-01T10:00:00Z',
  confirmedAt: '2026-10-01T10:00:00Z',
  withdrawnAt: null,
} as const;

const NONE = { status: 'none', wordingVersion: null, consentedAt: null, confirmedAt: null, withdrawnAt: null } as const;

const renderCard = () =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <MarketingEmailsCard />
      </SWRConfig>
    </MantineProvider>,
  );

const theSwitch = async () => (await screen.findByTestId('marketing-emails-switch')) as HTMLInputElement;

beforeEach(() => {
  H.getConsent.mockReset();
  H.getWording.mockReset();
  H.update.mockReset();
  H.getWording.mockResolvedValue(WORDING);
});

afterEach(cleanup);

describe('MarketingEmailsCard', () => {
  it('shows an opted-in record as on, with the date of consent', async () => {
    H.getConsent.mockResolvedValue(OPTED_IN);
    renderCard();

    expect((await theSwitch()).checked).toBe(true);
    const expected = new Date(OPTED_IN.consentedAt).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'long',
      day: 'numeric',
    });
    expect(screen.getByTestId('marketing-emails-status').textContent).toContain(expected);
  });

  it('withdraws in one click, with no confirmation step', async () => {
    H.getConsent.mockResolvedValue(OPTED_IN);
    H.update.mockResolvedValue({ ...OPTED_IN, status: 'withdrawn', withdrawnAt: '2026-10-12T09:00:00Z' });
    renderCard();

    fireEvent.click(await theSwitch());

    await waitFor(() => expect(H.update).toHaveBeenCalledTimes(1));
    expect(H.update.mock.calls[0][0]).toMatchObject({ optIn: false, source: 'settings' });
    await waitFor(() => expect((screen.getByTestId('marketing-emails-switch') as HTMLInputElement).checked).toBe(false));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByTestId('marketing-emails-status').textContent).toMatch(/unsubscribed/i);
  });

  it('starts off for a person who never opted in, and shows the wording it would record', async () => {
    H.getConsent.mockResolvedValue(NONE);
    renderCard();

    expect((await theSwitch()).checked).toBe(false);
    await waitFor(() => expect(screen.getByText(/Email me ReelBolt product news/)).toBeTruthy());
  });

  it('opts in with the wording version it shows, from settings', async () => {
    H.getConsent.mockResolvedValue(NONE);
    H.update.mockResolvedValue({ ...OPTED_IN, consentedAt: '2026-10-12T09:00:00Z' });
    renderCard();
    await waitFor(() => expect(screen.getByText(/Email me ReelBolt product news/)).toBeTruthy());

    fireEvent.click(await theSwitch());

    await waitFor(() =>
      expect(H.update).toHaveBeenCalledWith({ optIn: true, source: 'settings', wordingVersion: '2026-10-marketing-v1' }),
    );
    await waitFor(() => expect((screen.getByTestId('marketing-emails-switch') as HTMLInputElement).checked).toBe(true));
  });

  // S-02: an unverified account may not read or change the record its address keys.
  it('explains that the email must be verified, with the switch disabled, on 403 EMAIL_NOT_VERIFIED', async () => {
    H.getConsent.mockRejectedValue(new ApiError('Forbidden', 403, [], { code: 'EMAIL_NOT_VERIFIED', message: 'Verify your email address to manage marketing emails.' }));
    renderCard();

    const sw = await theSwitch();
    expect(sw.checked).toBe(false);
    expect(sw.disabled).toBe(true);
    expect(screen.getByTestId('marketing-emails-status').textContent).toBe(
      'Verify your email address to manage marketing emails.',
    );
    fireEvent.click(sw);
    expect(H.update).not.toHaveBeenCalled();
  });

  it('shows a pending website sign-up as off and waiting, and switching on opts in from settings', async () => {
    const PENDING = { ...NONE, status: 'pending', wordingVersion: '2026-10-marketing-v1', consentedAt: '2026-10-11T10:00:00Z' };
    H.getConsent.mockResolvedValue(PENDING);
    H.update.mockResolvedValue({ ...OPTED_IN, consentedAt: '2026-10-12T09:00:00Z' });
    renderCard();

    const sw = await theSwitch();
    expect(sw.checked).toBe(false);
    expect(screen.getByTestId('marketing-emails-status').textContent).toContain(
      'Waiting for you to confirm from your inbox',
    );
    await waitFor(() => expect(sw.disabled).toBe(false));

    fireEvent.click(sw);
    await waitFor(() =>
      expect(H.update).toHaveBeenCalledWith({ optIn: true, source: 'settings', wordingVersion: '2026-10-marketing-v1' }),
    );
    await waitFor(() => expect((screen.getByTestId('marketing-emails-switch') as HTMLInputElement).checked).toBe(true));
  });

  it('re-reads the wording and stays off when the API says the version went stale', async () => {
    H.getConsent.mockResolvedValue(NONE);
    H.update.mockRejectedValue(new ApiError('stale', 409, [], { currentWordingVersion: '2026-11-marketing-v2' }));
    renderCard();
    await waitFor(() => expect(screen.getByText(/Email me ReelBolt product news/)).toBeTruthy());
    expect(H.getWording).toHaveBeenCalledTimes(1);

    fireEvent.click(await theSwitch());

    await waitFor(() => expect(screen.getByTestId('marketing-emails-error').textContent).toMatch(/wording/));
    expect((screen.getByTestId('marketing-emails-switch') as HTMLInputElement).checked).toBe(false);
    await waitFor(() => expect(H.getWording).toHaveBeenCalledTimes(2));
  });
});
