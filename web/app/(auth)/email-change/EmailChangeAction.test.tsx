// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({ apiFetch: vi.fn() }));

vi.mock('@/lib/api/client', async (orig) => {
  const real = await orig<typeof import('@/lib/api/client')>();
  return { ...real, apiFetch: (...a: unknown[]) => H.apiFetch(...a) };
});

import { ApiError } from '@/lib/api/client';
import { EmailChangeAction, tokenFromHash } from './EmailChangeAction';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

const preview = { currentEmailMasked: 'a***@example.com', newEmail: 'new@example.com', expiresAt: '2030-01-01T00:00:00Z' };

function renderAt(hash: string) {
  window.history.replaceState(null, '', '/email-change' + hash);
  return render(
    <MantineProvider>
      <EmailChangeAction />
    </MantineProvider>,
  );
}

function bodyOf(call: unknown[]): unknown {
  return JSON.parse((call[1] as { body: string }).body);
}

beforeEach(() => {
  H.apiFetch.mockReset();
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

describe('tokenFromHash', () => {
  it('reads the token from #token=...', () => {
    expect(tokenFromHash('#token=abc123')).toBe('abc123');
    expect(tokenFromHash('token=xyz')).toBe('xyz');
    expect(tokenFromHash('')).toBe('');
    expect(tokenFromHash('#other=1')).toBe('');
  });
});

describe('EmailChangeAction', () => {
  it('drops the hash, previews, then confirms with a POST body token', async () => {
    H.apiFetch.mockResolvedValueOnce(preview).mockResolvedValueOnce({});
    renderAt('#token=tok-1');
    expect(await screen.findByTestId('email-change-new')).toHaveProperty('textContent', 'new@example.com');
    expect(screen.getByTestId('email-change-current').textContent).toBe('a***@example.com');
    expect(window.location.hash).toBe('');
    expect(window.location.search).toBe('');
    expect(document.body.textContent).not.toContain('tok-1');

    const [path, init] = H.apiFetch.mock.calls[0] as [string, { method: string }];
    expect(path).toBe('/api/v1/auth/email-change/preview');
    expect(init.method).toBe('POST');
    expect(bodyOf(H.apiFetch.mock.calls[0])).toEqual({ token: 'tok-1' });

    fireEvent.click(screen.getByTestId('email-change-confirm'));
    await screen.findByTestId('email-change-confirmed');
    expect(H.apiFetch.mock.calls[1][0]).toBe('/api/v1/auth/email-change/confirm');
    expect(bodyOf(H.apiFetch.mock.calls[1])).toEqual({ token: 'tok-1' });
  });

  it('cancels with a POST', async () => {
    H.apiFetch.mockResolvedValueOnce(preview).mockResolvedValueOnce(undefined);
    renderAt('#token=tok-2');
    fireEvent.click(await screen.findByTestId('email-change-cancel'));
    await screen.findByTestId('email-change-cancelled');
    expect(H.apiFetch.mock.calls[1][0]).toBe('/api/v1/auth/email-change/cancel');
    expect((H.apiFetch.mock.calls[1][1] as { method: string }).method).toBe('POST');
  });

  it('shows the invalid state when preview refuses the token', async () => {
    H.apiFetch.mockRejectedValueOnce(new ApiError('bad', 400, [], { code: 'EMAIL_CHANGE_TOKEN_INVALID' }));
    renderAt('#token=bad');
    await screen.findByTestId('email-change-invalid');
  });

  it('shows the invalid state without calling the API when there is no token', async () => {
    renderAt('');
    await screen.findByTestId('email-change-invalid');
    expect(H.apiFetch).not.toHaveBeenCalled();
  });

  it('explains EMAIL_IN_USE on confirm and keeps the buttons', async () => {
    H.apiFetch
      .mockResolvedValueOnce(preview)
      .mockRejectedValueOnce(new ApiError('conflict', 409, [], { code: 'EMAIL_IN_USE' }));
    renderAt('#token=t3');
    fireEvent.click(await screen.findByTestId('email-change-confirm'));
    await waitFor(() => expect(screen.getByTestId('email-change-error').textContent).toMatch(/already uses/));
    expect(screen.getByTestId('email-change-cancel')).toBeTruthy();
  });
});
