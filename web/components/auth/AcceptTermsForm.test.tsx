// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { AcceptTermsForm } from './AcceptTermsForm';
import { ApiError } from '@/lib/api/client';

const push = vi.fn();
const notifyAuthChange = vi.fn();
const acceptTerms = vi.fn();

vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: () => notifyAuthChange() }));
vi.mock('@/lib/api/auth', () => ({ acceptTerms: (data: unknown) => acceptTerms(data) }));

const WORDING = {
  version: '2026-10-marketing-v1',
  text: 'Email me ReelBolt product news, tips and offers. You can unsubscribe at any time from any email or in Settings. See our Privacy Policy.',
};
const M = vi.hoisted(() => ({
  updateConsent: vi.fn(),
  wording: { data: undefined as { version: string; text: string } | undefined },
  consent: { data: undefined as { status: string } | undefined, error: undefined as unknown },
}));
vi.mock('@/lib/hooks/use-marketing', () => ({ useMarketingWording: () => M.wording, useMarketingConsent: () => M.consent }));
vi.mock('@/lib/api/marketing', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/marketing')>()),
  updateMarketingConsent: (data: unknown) => M.updateConsent(data),
}));
const CONSENT_NONE = { data: { status: 'none' }, error: undefined };
const UNVERIFIED = { data: undefined, error: new ApiError('Forbidden', 403, [], { code: 'EMAIL_NOT_VERIFIED', message: 'Verify your email address to manage marketing emails.' }) };

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const renderForm = (termsVersion = '2026-10', privacyVersion = '2026-10', change: 'terms' | 'privacy' = 'terms') =>
  render(
    <MantineProvider>
      <AcceptTermsForm termsVersion={termsVersion} privacyVersion={privacyVersion} change={change} />
    </MantineProvider>,
  );

describe('AcceptTermsForm', () => {
  beforeEach(() => {
    push.mockReset();
    notifyAuthChange.mockReset();
    acceptTerms.mockReset();
    M.updateConsent.mockReset();
    M.updateConsent.mockResolvedValue({ status: 'optedIn' });
    M.wording = { data: WORDING };
    M.consent = CONSENT_NONE;
    acceptTerms.mockResolvedValue({ accessToken: 't', tokenType: 'Bearer', expiresIn: 86400, mustChangePassword: false });
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });

  afterEach(cleanup);

  it('will not continue until the updated terms are accepted, and records nothing', async () => {
    renderForm();
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(screen.getByText('Please accept the updated terms to continue.')).toBeTruthy());
    expect(acceptTerms).not.toHaveBeenCalled();
    expect(push).not.toHaveBeenCalled();
  });

  it('records the acceptance server-side and continues', async () => {
    renderForm('2026-11', '2026-11');
    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    // The two versions are the ones the page was rendered with, and the API writes the rows.
    expect(acceptTerms).toHaveBeenCalledWith({
      acceptTerms: true,
      termsVersion: '2026-11',
      privacyVersion: '2026-11',
    });
    expect(notifyAuthChange).toHaveBeenCalled();
  });

  // F-7. A 409 means the deployment published a new version between rendering this page and
  // submitting it. The refusal carries the versions that are current now, so the form shows THOSE
  // and asks the visitor to agree again — recording the stale ones is exactly the bug this fixes.
  it('adopts the versions a 409 refusal carries and makes the visitor accept again', async () => {
    acceptTerms.mockRejectedValueOnce(
      new ApiError('the terms have been updated since this page was loaded', 409, [], {
        currentTermsVersion: '2026-11',
        currentPrivacyVersion: '2026-11',
      }),
    );
    renderForm('2026-10', '2026-10');
    expect(screen.getByRole('checkbox', { name: /2026-10/ })).toBeTruthy();

    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(screen.getByText(/Please review version 2026-11/)).toBeTruthy());
    expect(push).not.toHaveBeenCalled();
    // The box is cleared: consent to a document is not carried over to a new one.
    expect((screen.getByRole('checkbox', { name: /2026-11/ }) as HTMLInputElement).checked).toBe(false);

    // Accepting again submits the version the server is actually serving.
    acceptTerms.mockResolvedValue({ accessToken: 't', tokenType: 'Bearer', expiresIn: 86400, mustChangePassword: false });
    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(acceptTerms).toHaveBeenLastCalledWith({
      acceptTerms: true,
      termsVersion: '2026-11',
      privacyVersion: '2026-11',
    });
  });

  it('renders without a version when the API could not be asked, and still submits', async () => {
    // The page could not read the served versions; the form must not invent one, and the API
    // refuses the submission rather than recording anything.
    renderForm('', '');
    expect(
      screen.getByRole('checkbox', { name: 'I accept the current Terms of Service and Privacy Policy' }),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(acceptTerms).toHaveBeenCalledWith({
      acceptTerms: true,
      termsVersion: '',
      privacyVersion: '',
    }));
  });

  it('shows the API error and does not navigate when the acceptance cannot be recorded', async () => {
    acceptTerms.mockRejectedValue(new Error('service unavailable'));
    renderForm();
    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(screen.getByText('service unavailable')).toBeTruthy());
    expect(push).not.toHaveBeenCalled();
  });

  it('the full Terms variant posts a plain acceptance with no privacy acknowledgement', async () => {
    renderForm('2026-11', '2026-11');
    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));
    await waitFor(() => expect(acceptTerms).toHaveBeenCalledTimes(1));
    expect(acceptTerms.mock.calls[0][0]).toStrictEqual({ acceptTerms: true, termsVersion: '2026-11', privacyVersion: '2026-11' });
  });

  it('hides the marketing box in the terms variant for an unverified account, and accepting still works', async () => {
    M.consent = UNVERIFIED;
    renderForm('2026-11', '2026-11');
    expect(screen.queryByTestId('marketing-optin-checkbox')).toBeNull();
    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
  });

  it('keeps the marketing box unticked and separate in the terms variant, and posts nothing when untouched', async () => {
    renderForm('2026-11', '2026-11');
    const box = screen.getByTestId('marketing-optin-checkbox') as HTMLInputElement;
    expect(box.checked).toBe(false);
    expect(screen.getByRole('checkbox', { name: /I accept/ })).not.toBe(box);

    fireEvent.click(screen.getByRole('checkbox', { name: /I accept/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(M.updateConsent).not.toHaveBeenCalled();
  });

  it('ticking the marketing box alone never stands in for accepting the terms', async () => {
    renderForm();
    fireEvent.click(screen.getByTestId('marketing-optin-checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));

    await waitFor(() => expect(screen.getByText('Please accept the updated terms to continue.')).toBeTruthy());
    expect(acceptTerms).not.toHaveBeenCalled();
    expect(M.updateConsent).not.toHaveBeenCalled();
  });
});

describe('AcceptTermsForm — privacy-only acknowledgement', () => {
  beforeEach(() => {
    push.mockReset();
    notifyAuthChange.mockReset();
    acceptTerms.mockReset();
    acceptTerms.mockResolvedValue({ accessToken: 't', tokenType: 'Bearer', expiresIn: 86400, mustChangePassword: false });
    M.updateConsent.mockReset();
    M.updateConsent.mockResolvedValue({ status: 'optedIn' });
    M.wording = { data: WORDING };
    M.consent = CONSENT_NONE;
  });

  afterEach(cleanup);

  it('is a notice with a Continue button and no required checkbox, never "I agree"', () => {
    renderForm('2026-10', '2026-11', 'privacy');
    expect(screen.getByRole('heading', { name: "We've updated our Privacy Policy" })).toBeTruthy();
    expect(
      screen.getByText('We have changed our Privacy Policy. Please read what changed before you continue.'),
    ).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Read the Privacy Policy' }).getAttribute('href')).toBe('/legal/privacy');
    expect(screen.getByTestId('privacy-continue-button').textContent).toContain('Continue');

    // The only checkbox is the optional marketing one, and it is unticked and not required.
    const boxes = screen.getAllByRole('checkbox') as HTMLInputElement[];
    expect(boxes).toHaveLength(1);
    expect(boxes[0]).toBe(screen.getByTestId('marketing-optin-checkbox'));
    expect(boxes[0].checked).toBe(false);
    expect(boxes[0].required).toBe(false);
    expect(document.body.textContent).not.toMatch(/I agree|I accept/i);
  });

  // S-23: the acknowledgement is not a Terms acceptance, so it never posts acceptTerms: true.
  it('Continue posts a privacy acknowledgement, never a Terms acceptance, and no consent', async () => {
    renderForm('2026-10', '2026-11', 'privacy');
    fireEvent.click(screen.getByTestId('privacy-continue-button'));

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(acceptTerms).toHaveBeenCalledTimes(1);
    expect(acceptTerms.mock.calls[0][0]).toStrictEqual({
      acceptTerms: false,
      privacyAcknowledgement: true,
      termsVersion: '2026-10',
      privacyVersion: '2026-11',
    });
    expect(M.updateConsent).not.toHaveBeenCalled();
  });

  // S-23: the variant comes from an editable cookie; the API answers 409 when the stored Terms
  // version is not current, and the full acceptance is then the only way on.
  it('shows the full Terms variant when the API refuses the acknowledgement with 409', async () => {
    acceptTerms.mockRejectedValueOnce(new ApiError('terms not current', 409, [], { error: 'terms_not_current' }));
    renderForm('2026-10', '2026-11', 'privacy');
    fireEvent.click(screen.getByTestId('privacy-continue-button'));

    const box = await screen.findByRole('checkbox', { name: /I accept version 2026-10/ });
    expect(screen.queryByTestId('privacy-continue-button')).toBeNull();
    expect(push).not.toHaveBeenCalled();

    fireEvent.click(box);
    fireEvent.click(screen.getByRole('button', { name: 'Accept and continue' }));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(acceptTerms.mock.calls[1][0]).toStrictEqual({
      acceptTerms: true,
      termsVersion: '2026-10',
      privacyVersion: '2026-11',
    });
  });

  it('hides the marketing box for an unverified account and still continues', async () => {
    M.consent = UNVERIFIED;
    renderForm('2026-10', '2026-11', 'privacy');
    expect(screen.queryByTestId('marketing-optin-checkbox')).toBeNull();
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    fireEvent.click(screen.getByTestId('privacy-continue-button'));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(M.updateConsent).not.toHaveBeenCalled();
  });

  it('posts the marketing consent separately, after the acceptance, only when ticked', async () => {
    const order: string[] = [];
    acceptTerms.mockImplementation(async () => {
      order.push('accept');
      return { accessToken: 't', tokenType: 'Bearer', expiresIn: 86400, mustChangePassword: false };
    });
    M.updateConsent.mockImplementation(async () => {
      order.push('consent');
      return { status: 'optedIn' };
    });
    renderForm('2026-10', '2026-11', 'privacy');
    fireEvent.click(screen.getByTestId('marketing-optin-checkbox'));
    fireEvent.click(screen.getByTestId('privacy-continue-button'));

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(M.updateConsent).toHaveBeenCalledWith({
      optIn: true,
      source: 'acceptTerms',
      wordingVersion: '2026-10-marketing-v1',
    });
    expect(order).toEqual(['accept', 'consent']);
  });

  it('does not post the consent when the acceptance fails', async () => {
    acceptTerms.mockRejectedValue(new Error('service unavailable'));
    renderForm('2026-10', '2026-11', 'privacy');
    fireEvent.click(screen.getByTestId('marketing-optin-checkbox'));
    fireEvent.click(screen.getByTestId('privacy-continue-button'));

    await waitFor(() => expect(screen.getByText('service unavailable')).toBeTruthy());
    expect(M.updateConsent).not.toHaveBeenCalled();
    expect(push).not.toHaveBeenCalled();
  });

  it('still continues when the consent request fails', async () => {
    M.updateConsent.mockRejectedValue(new Error('nope'));
    renderForm('2026-10', '2026-11', 'privacy');
    fireEvent.click(screen.getByTestId('marketing-optin-checkbox'));
    fireEvent.click(screen.getByTestId('privacy-continue-button'));

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
  });

  it('turns into the explicit acceptance when a 409 shows the Terms moved too', async () => {
    acceptTerms.mockRejectedValueOnce(
      new ApiError('stale', 409, [], { currentTermsVersion: '2026-12', currentPrivacyVersion: '2026-12' }),
    );
    renderForm('2026-10', '2026-11', 'privacy');
    fireEvent.click(screen.getByTestId('privacy-continue-button'));

    await waitFor(() => expect(screen.getByRole('checkbox', { name: /2026-12/ })).toBeTruthy());
    expect(screen.queryByTestId('privacy-continue-button')).toBeNull();
  });

  it('welcomes a first-time acceptance instead of saying the terms were updated (A26)', () => {
    render(
      <MantineProvider>
        <AcceptTermsForm termsVersion="2026-10" privacyVersion="2026-10" firstAcceptance />
      </MantineProvider>,
    );
    expect(screen.getByRole('heading', { name: 'Welcome to ReelBolt' })).toBeTruthy();
    expect(screen.queryByText(/We have updated our Terms/)).toBeNull();
  });

  it('still says the terms were updated when the session accepted an earlier version', () => {
    renderForm();
    expect(screen.getByRole('heading', { name: 'Updated terms' })).toBeTruthy();
  });
});
