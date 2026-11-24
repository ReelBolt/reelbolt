// @vitest-environment jsdom
/**
 * The change-password page's half of the forced-change return-path contract: the Go API sends a
 * mustChangePassword session here with ?next=<the consent URL it was trying to reach>
 * (api/handlers/oauth/oauth.go:132), and the page must hand the form only the validated value.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

// The page's half of the contract is the prop it hands the form; the form's own navigation is
// ChangePasswordForm.test.tsx's job. Stubbing the form exposes exactly that prop, so a page that
// forwarded the raw `?next=` (instead of the validated one) would render "//evil.com".
vi.mock('@/components/auth/ChangePasswordForm', () => ({
  ChangePasswordForm: ({ next }: { next?: string }) => (
    <div data-testid="change-password-form-next">{next ?? '(none)'}</div>
  ),
}));

import ChangePasswordPage from './page';

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

async function renderPage(params: Record<string, string | string[] | undefined> = {}) {
  const ui = await ChangePasswordPage({ searchParams: Promise.resolve(params) });
  return render(<MantineProvider>{ui}</MantineProvider>);
}

afterEach(cleanup);

describe('change-password page: the return path', () => {
  it('passes a validated root-level ?next through to the form unchanged', async () => {
    await renderPage({ next: '/oauth/authorize?client_id=app&scope=assistant.tools' });
    expect(screen.getByTestId('change-password-form-next').textContent).toBe(
      '/oauth/authorize?client_id=app&scope=assistant.tools',
    );
  });

  it('drops a hostile ?next rather than handing it to the form', async () => {
    await renderPage({ next: '//evil.com' });
    expect(screen.getByTestId('change-password-form-next').textContent).toBe('(none)');
  });
});
