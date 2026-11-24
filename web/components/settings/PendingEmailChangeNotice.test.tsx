// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useEffect, useState } from 'react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  data: undefined as unknown,
  cancel: vi.fn(),
  bump: () => {},
}));

vi.mock('@/lib/hooks/use-auth-settings', () => ({
  useAuthSettings: () => ({
    data: H.data,
    mutate: async (val: unknown) => {
      H.data = val;
      H.bump();
      return val;
    },
  }),
}));
vi.mock('@/lib/api/auth-settings', () => ({
  cancelOwnEmailChange: (...a: unknown[]) => H.cancel(...a),
}));

import { PendingEmailChangeNotice } from './PendingEmailChangeNotice';

function Rerenderable({ children }: { children: React.ReactNode }) {
  const [, force] = useState(0);
  useEffect(() => {
    H.bump = () => force((n) => n + 1);
  }, []);
  return <>{children}</>;
}

const renderNotice = () =>
  render(
    <MantineProvider>
      <Rerenderable>
        <PendingEmailChangeNotice />
      </Rerenderable>
    </MantineProvider>,
  );

beforeEach(() => {
  H.cancel = vi.fn().mockResolvedValue(undefined);
  H.data = {
    mcpAdminToolsEnabled: false,
    displayName: 'Me',
    pendingEmailChange: { newEmail: 'next@example.com', expiresAt: '2030-01-02T10:00:00Z' },
  };
  window.matchMedia = ((q: string) => ({
    matches: false,
    media: q,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

describe('PendingEmailChangeNotice', () => {
  it('renders nothing when no change is pending', () => {
    H.data = { mcpAdminToolsEnabled: false, displayName: 'Me', pendingEmailChange: null };
    renderNotice();
    expect(screen.queryByTestId('pending-email-change')).toBeNull();
  });

  it('shows the pending address and its expiry', () => {
    renderNotice();
    const text = screen.getByTestId('pending-email-change').textContent ?? '';
    expect(text).toContain('next@example.com');
    expect(text).toMatch(/2030/);
  });

  it('Cancel calls DELETE and hides the notice', async () => {
    renderNotice();
    fireEvent.click(screen.getByTestId('pending-email-change-cancel'));
    await waitFor(() => expect(H.cancel).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByTestId('pending-email-change')).toBeNull());
  });

  it('keeps the notice and says so when the cancel fails', async () => {
    H.cancel = vi.fn().mockRejectedValue(new Error('boom'));
    renderNotice();
    fireEvent.click(screen.getByTestId('pending-email-change-cancel'));
    await screen.findByText('Could not cancel the change. Please try again.');
    expect(screen.getByTestId('pending-email-change')).toBeTruthy();
  });
});
