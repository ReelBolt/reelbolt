// @vitest-environment jsdom
/**
 * The unsubscribe and confirm pages' button. What these tests protect: rendering never sends
 * anything (a link scanner's GET changes nothing), and the click POSTs exactly the token.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';

const H = vi.hoisted(() => ({ unsubscribe: vi.fn(), confirm: vi.fn() }));
vi.mock('@/lib/api/marketing', () => ({
  unsubscribeMarketing: (token: string) => H.unsubscribe(token),
  confirmMarketingSubscription: (token: string) => H.confirm(token),
}));

import { ConfirmSubscriptionAction, UnsubscribeAction } from './MarketingLinkAction';

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

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

beforeEach(() => {
  H.unsubscribe.mockReset();
  H.confirm.mockReset();
});
afterEach(cleanup);

describe('UnsubscribeAction', () => {
  it('sends nothing on render, and unsubscribes only on the click', async () => {
    H.unsubscribe.mockResolvedValue(undefined);
    wrap(<UnsubscribeAction token="u1.k1.abc.def" />);
    expect(H.unsubscribe).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTestId('unsubscribe-button'));

    await waitFor(() => expect(screen.getByTestId('unsubscribe-done')).toBeTruthy());
    expect(H.unsubscribe).toHaveBeenCalledWith('u1.k1.abc.def');
  });

  it('says an invalid link is invalid', async () => {
    H.unsubscribe.mockRejectedValue(new ApiError('invalid_token', 400, [], { error: 'invalid_token' }));
    wrap(<UnsubscribeAction token="bad" />);
    fireEvent.click(screen.getByTestId('unsubscribe-button'));

    await waitFor(() => expect(screen.getByText(/This link is not valid/)).toBeTruthy());
    expect(screen.queryByTestId('unsubscribe-done')).toBeNull();
  });

  it('offers no button without a token', () => {
    wrap(<UnsubscribeAction token="" />);
    expect(screen.queryByTestId('unsubscribe-button')).toBeNull();
  });
});

describe('ConfirmSubscriptionAction', () => {
  it('sends nothing on render, and confirms only on the click', async () => {
    H.confirm.mockResolvedValue(undefined);
    wrap(<ConfirmSubscriptionAction token="c1.k1.abc.1.def" />);
    expect(H.confirm).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTestId('confirm-subscription-button'));

    await waitFor(() => expect(screen.getByTestId('confirm-subscription-done')).toBeTruthy());
    expect(H.confirm).toHaveBeenCalledWith('c1.k1.abc.1.def');
  });

  it('says an expired link has expired', async () => {
    H.confirm.mockRejectedValue(new ApiError('token_expired', 410, [], { error: 'token_expired' }));
    wrap(<ConfirmSubscriptionAction token="c1.old" />);
    fireEvent.click(screen.getByTestId('confirm-subscription-button'));

    await waitFor(() => expect(screen.getByText(/has expired/)).toBeTruthy());
  });
});
