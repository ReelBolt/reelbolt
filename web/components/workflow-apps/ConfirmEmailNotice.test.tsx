// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';

const env = vi.hoisted(() => ({ resend: vi.fn(), giveToken: null as ((token: string) => void) | null }));
vi.mock('@/lib/api/auth', () => ({ resendVerification: env.resend }));
vi.mock('@/components/auth/TurnstileWidget', () => ({
  TurnstileWidget: ({ siteKey, onToken }: { siteKey: string; onToken: (t: string) => void }) => {
    env.giveToken = onToken;
    return <div data-testid="turnstile" data-key={siteKey} />;
  },
}));

import {
  CONFIRM_EMAIL_NOTICE_ID,
  CONFIRM_EMAIL_RESEND_ID,
  ConfirmEmailNotice,
  RunPageConfigProvider,
  focusConfirmEmailNotice,
  isEmailNotVerified,
} from './ConfirmEmailNotice';

function mount(props: { refused?: boolean; siteKey?: string } = {}) {
  const notice = <ConfirmEmailNotice email="ada@example.com" {...(props.refused ? { refused: true } : {})} />;
  return render(
    <MantineProvider>
      {props.siteKey ? <RunPageConfigProvider turnstileSiteKey={props.siteKey}>{notice}</RunPageConfigProvider> : notice}
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  env.resend.mockReset();
  env.resend.mockResolvedValue({ message: 'ok' });
  env.giveToken = null;
});
afterEach(cleanup);

describe('ConfirmEmailNotice', () => {
  it('says, before Run is pressed, that a run needs a confirmed address, naming the person\'s own', () => {
    mount();
    const notice = screen.getByTestId('confirm-email-notice');
    expect(notice.getAttribute('role')).toBe('status');
    expect(notice.textContent).toContain('Confirm your email before you run');
    expect(notice.textContent).toContain('A run starts only after ada@example.com is confirmed.');
    expect(notice.textContent).not.toContain('That run did not start');
  });

  it('resends with the existing call and says it was sent', async () => {
    mount();
    const button = screen.getByRole('button', { name: 'Resend the email' });
    expect(button.id).toBe(CONFIRM_EMAIL_RESEND_ID);
    fireEvent.click(button);
    await waitFor(() => expect(env.resend).toHaveBeenCalledWith({ email: 'ada@example.com' }));
    expect(await screen.findByText('Sent. Check your inbox.')).toBeTruthy();
    // It can be sent again.
    fireEvent.click(screen.getByRole('button', { name: 'Send it again' }));
    await waitFor(() => expect(env.resend).toHaveBeenCalledTimes(2));
  });

  it('says so when the email could not be sent', async () => {
    env.resend.mockRejectedValue(new Error('429'));
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Resend the email' }));
    expect(await screen.findByText('We could not send it just now. Please try again later.')).toBeTruthy();
  });

  it('a refused run is announced at once, in words', () => {
    mount({ refused: true });
    const notice = screen.getByTestId('confirm-email-notice');
    expect(notice.getAttribute('role')).toBe('alert');
    expect(notice.textContent).toContain('That run did not start, because your email address is not confirmed yet.');
  });

  it('with a Turnstile key, the resend waits for the challenge and sends its token (fails closed)', async () => {
    mount({ siteKey: 'site-key-1' });
    expect(screen.getByTestId('turnstile').getAttribute('data-key')).toBe('site-key-1');
    const button = screen.getByRole('button', { name: 'Resend the email' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    act(() => env.giveToken!('tok-1'));
    expect(button.disabled).toBe(false);
    fireEvent.click(button);
    await waitFor(() => expect(env.resend).toHaveBeenCalledWith({ email: 'ada@example.com', turnstileToken: 'tok-1' }));
    // A token is single-use: the next send needs a fresh one.
    await waitFor(() => expect((screen.getByRole('button', { name: 'Send it again' }) as HTMLButtonElement).disabled).toBe(true));
  });

  it('without a key there is no challenge and no token', () => {
    mount();
    expect(screen.queryByTestId('turnstile')).toBeNull();
    expect((screen.getByRole('button', { name: 'Resend the email' }) as HTMLButtonElement).disabled).toBe(false);
  });

  /**
   * A refused run, and the readiness strip's "Resend the email" fix, move focus to this notice.
   * With a challenge configured the resend button is disabled until the challenge resolves, and a
   * disabled button cannot take focus: focus stayed on Run and the fix did nothing a keyboard user
   * could see. The notice itself takes focus then.
   */
  describe('where focus lands', () => {
    it('on "Resend the email" when the button can be pressed', () => {
      mount({ refused: true });
      focusConfirmEmailNotice();
      expect(document.activeElement?.id).toBe(CONFIRM_EMAIL_RESEND_ID);
    });

    it('on the notice itself while the challenge keeps the button disabled, and on the button once it resolves', () => {
      mount({ refused: true, siteKey: 'site-key-1' });
      const button = screen.getByRole('button', { name: 'Resend the email' }) as HTMLButtonElement;
      expect(button.disabled).toBe(true);
      focusConfirmEmailNotice();
      const notice = screen.getByTestId('confirm-email-notice');
      expect(document.activeElement).toBe(notice);
      expect(notice.id).toBe(CONFIRM_EMAIL_NOTICE_ID);
      // Reachable from script, not a tab stop of its own.
      expect(notice.getAttribute('tabindex')).toBe('-1');
      act(() => env.giveToken!('tok-1'));
      focusConfirmEmailNotice();
      expect(document.activeElement).toBe(button);
    });

    it('does nothing, and does not throw, when the notice is not on the page', () => {
      expect(() => focusConfirmEmailNotice()).not.toThrow();
    });
  });

  it('recognises only the run gate\'s own refusal', () => {
    expect(isEmailNotVerified(new ApiError('Confirm your email', 403, [], { code: 'EMAIL_NOT_VERIFIED' }))).toBe(true);
    expect(isEmailNotVerified(new ApiError('Forbidden', 403, [], { code: 'OTHER' }))).toBe(false);
    expect(isEmailNotVerified(new ApiError('x', 402, [], { code: 'EMAIL_NOT_VERIFIED' }))).toBe(false);
    expect(isEmailNotVerified(new Error('EMAIL_NOT_VERIFIED'))).toBe(false);
  });
});
