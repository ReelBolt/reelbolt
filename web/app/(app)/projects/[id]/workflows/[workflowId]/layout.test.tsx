// @vitest-environment jsdom
/**
 * The run page's "Resend the email" needs the Turnstile site key where the resend is protected. Only
 * the server can read it, so this layout hands it down: without it the button could never be used
 * on a deployment that has the challenge switched on.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/i18n/server', () => ({ getT: async () => (key: string) => key }));
vi.mock('@/lib/api/auth', () => ({ resendVerification: vi.fn() }));
vi.mock('@/components/auth/TurnstileWidget', () => ({
  TurnstileWidget: ({ siteKey }: { siteKey: string }) => <div data-testid="turnstile" data-key={siteKey} />,
}));

import WorkflowLayout from './layout';
import { ConfirmEmailNotice } from '@/components/workflow-apps/ConfirmEmailNotice';

const mount = () =>
  render(
    <MantineProvider>
      <WorkflowLayout>
        <ConfirmEmailNotice email="ada@example.com" />
      </WorkflowLayout>
    </MantineProvider>,
  );

afterEach(() => {
  cleanup();
  vi.unstubAllEnvs();
});

describe('workflow route layout', () => {
  it('hands the Turnstile site key to the run page, read on the server per request', () => {
    global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
    vi.stubEnv('TURNSTILE_SITE_KEY', 'site-key-9');
    mount();
    expect(screen.getByTestId('turnstile').getAttribute('data-key')).toBe('site-key-9');
    // The challenge fails closed: no token yet, no resend.
    expect((screen.getByRole('button', { name: 'Resend the email' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('adds no challenge where none is configured', () => {
    vi.stubEnv('TURNSTILE_SITE_KEY', '');
    vi.stubEnv('NEXT_PUBLIC_TURNSTILE_SITE_KEY', '');
    mount();
    expect(screen.queryByTestId('turnstile')).toBeNull();
    expect((screen.getByRole('button', { name: 'Resend the email' }) as HTMLButtonElement).disabled).toBe(false);
  });
});
