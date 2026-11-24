'use client';

import { Link } from '@/i18n/link';
import { useBasePath } from '@/i18n/use-base-path';
import { useCurrentLocale, useT } from '@/i18n';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { isValidSubscribeEmail } from '@/lib/marketing/subscribe';
import { MARKETING_WORDING, splitPrivacyLink } from '@/lib/marketing/wording';
import { buttonClass } from '@/components/ui/button-styles';

// The opt-in label is the registered consent wording, character for character, with its
// "Privacy Policy" mention rendered as a link. Never edit the text here: change the registry.
const OPT_IN_LABEL = splitPrivacyLink(MARKETING_WORDING.text);

// Server error codes (POST /api/subscribe) mapped to the copy the visitor sees.
const SERVER_MESSAGE_KEYS: Record<string, string> = {
  stale_wording: 'staleWording',
  consent_required: 'consentRequired',
  invalid_email: 'invalidEmail',
};

type Status =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'sent' }
  | { kind: 'error'; message: string };

// The contact form carries its own marketing opt-in, so the footer sign-up is hidden on these
// pages: two identical consent boxes on one screen leave visitors unsure which one subscribes them.
const HIDDEN_ON_PATHS = ['/contact', '/contact/thank-you'];

/**
 * The footer band that holds the newsletter. A client leaf so SiteFooter can stay a server
 * component: the pathname decides whether the band renders at all.
 */
export function FooterNewsletter() {
  // Trailing slashes (/contact/) must match too, or the band reappears on the same page.
  const pathname = useBasePath();
  if (HIDDEN_ON_PATHS.includes(pathname)) return null;
  return (
    <div className="border-b border-line py-10">
      <NewsletterSignup />
    </div>
  );
}

/** Footer newsletter: an email, the verbatim consent checkbox (unticked) and a honeypot. */
export function NewsletterSignup() {
  const t = useT('promo.newsletter');
  const locale = useCurrentLocale();
  const [email, setEmail] = useState('');
  const [firstName, setFirstName] = useState('');
  const [consent, setConsent] = useState(false);
  const [status, setStatus] = useState<Status>({ kind: 'idle' });
  const [fieldError, setFieldError] = useState<string | null>(null);
  const honeypotRef = useRef<HTMLInputElement>(null);
  const startedAtRef = useRef<number | null>(null);

  // Set after mount, like the contact form, so the server's fill-time check measures real typing time.
  useEffect(() => {
    startedAtRef.current = Date.now();
  }, []);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (status.kind === 'submitting') return;

    const trimmed = email.trim();
    if (!isValidSubscribeEmail(trimmed)) {
      setFieldError(t('invalidEmail'));
      return;
    }
    if (!consent) {
      setFieldError(t('consentRequired'));
      return;
    }
    setFieldError(null);
    setStatus({ kind: 'submitting' });

    try {
      const res = await fetch('/api/subscribe', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          email: trimmed,
          // Optional; the server drops it when blank or invalid.
          ...(firstName.trim() ? { firstName: firstName.trim() } : {}),
          consent: true,
          wordingVersion: MARKETING_WORDING.version,
          startedAt: startedAtRef.current,
          website: honeypotRef.current?.value ?? '',
          // The visitor's language, so the confirmation email arrives in it (allowlisted server side).
          locale,
        }),
      });
      if (res.status === 202) {
        setStatus({ kind: 'sent' });
        return;
      }
      if (res.status === 429) {
        setStatus({ kind: 'error', message: t('rateLimited') });
        return;
      }
      const body: unknown = await res.json().catch(() => null);
      const code = typeof body === 'object' && body !== null ? (body as { error?: unknown }).error : undefined;
      setStatus({
        kind: 'error',
        message: (typeof code === 'string' && SERVER_MESSAGE_KEYS[code] ? t(SERVER_MESSAGE_KEYS[code]) : t('error')),
      });
    } catch {
      setStatus({ kind: 'error', message: t('error') });
    }
  }

  const errorText = fieldError ?? (status.kind === 'error' ? status.message : null);

  return (
    <div>
      <p id="footer-newsletter" className="font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">
        {t('heading')}
      </p>
      <p className="mt-2 text-sm text-ink-muted">{t('helper')}</p>

      {status.kind === 'sent' ? (
        <p role="status" className="mt-4 text-sm text-ink">
          {t('checkInbox')}
        </p>
      ) : (
        <form
          onSubmit={onSubmit}
          noValidate
          aria-labelledby="footer-newsletter"
          className="relative mt-4 grid gap-4 md:grid-cols-[minmax(0,1fr)_auto] md:items-end"
        >
          <div>
            <label
              htmlFor="footer-newsletter-email"
              className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted"
            >
              {t('emailLabel')}
            </label>
            <input
              id="footer-newsletter-email"
              name="email"
              type="email"
              autoComplete="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              aria-invalid={fieldError ? 'true' : undefined}
              aria-describedby={errorText ? 'footer-newsletter-error' : undefined}
              className="mt-2 block min-h-11 w-full rounded-none border border-line-strong bg-paper px-3 py-2 font-mono text-sm text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
            />
          </div>

          <div className="md:col-span-2">
            <label
              htmlFor="footer-newsletter-first-name"
              className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted"
            >
              {t('firstNameLabel')}
            </label>
            <input
              id="footer-newsletter-first-name"
              name="firstName"
              type="text"
              autoComplete="given-name"
              maxLength={40}
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              className="mt-2 block min-h-11 w-full rounded-none border border-line-strong bg-paper px-3 py-2 font-mono text-sm text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
            />
          </div>

          <button
            type="submit"
            disabled={status.kind === 'submitting'}
            className={`${buttonClass({ variant: 'accent', size: 'md' })} disabled:opacity-60`}
          >
            {status.kind === 'submitting' ? t('submitting') : t('submit')}
          </button>

          <div className="flex min-h-11 items-center gap-3 md:col-span-2">
            <span className="relative h-5 w-5 shrink-0">
              <input
                id="footer-newsletter-consent"
                name="marketingOptIn"
                type="checkbox"
                checked={consent}
                onChange={(e) => setConsent(e.target.checked)}
                className="peer absolute inset-0 h-5 w-5 cursor-brand-pointer appearance-none border border-line-strong bg-paper checked:border-accent-strong checked:bg-accent-strong focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
              />
              <span
                aria-hidden="true"
                className="pointer-events-none absolute left-[7px] top-[3px] hidden h-2.5 w-1.5 rotate-45 border-b-2 border-r-2 border-ink peer-checked:block"
              />
            </span>
            <label htmlFor="footer-newsletter-consent" className="font-sans text-sm text-ink-muted">
              {OPT_IN_LABEL.before}
              {OPT_IN_LABEL.link && (
                <Link href="/legal/privacy" className="text-accent-ink underline hover:text-accent">
                  {OPT_IN_LABEL.link}
                </Link>
              )}
              {OPT_IN_LABEL.after}
            </label>
          </div>

          {/* Honeypot: hidden from people, left empty by bots that fill every field. */}
          <div aria-hidden="true" className="absolute left-[-9999px] top-auto h-px w-px overflow-hidden">
            <label htmlFor="footer-website">{t('honeypotLabel')}</label>
            <input
              ref={honeypotRef}
              id="footer-website"
              name="website"
              type="text"
              tabIndex={-1}
              autoComplete="off"
            />
          </div>

          {/* Two lines reserved, so an error appearing after submit does not move the footer. */}
          <div className="min-h-10 md:col-span-2">
            {errorText && (
              <p id="footer-newsletter-error" role="alert" className="text-sm text-danger">
                {errorText}
              </p>
            )}
          </div>
        </form>
      )}
    </div>
  );
}
