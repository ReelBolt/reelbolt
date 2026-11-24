'use client';

import { useEffect, useRef, useState, type FocusEventHandler, type FormEvent } from 'react';
import { Link } from '@/i18n/link';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  CONTACT_TOPICS,
  isContactTopic,
  topicFromParam,
  validateContact,
  type ContactTopic,
  type ValidationError,
} from '@/lib/validation';
import { useT } from '@/i18n';
import { trackEvent } from '@/lib/gtag';
import { SUPPORT_EMAIL } from '@/lib/legal-placeholders';
import { MARKETING_WORDING, splitPrivacyLink } from '@/lib/marketing/wording';
import { Spinner } from '@/components/ui/Spinner';
import { buttonClass } from '@/components/ui/button-styles';

type FieldName = 'name' | 'email' | 'company' | 'message';
type TopicFieldName = FieldName | 'topic';

interface FormValues {
  name: string;
  email: string;
  company: string;
  topic: ContactTopic;
  message: string;
}

const EMPTY_VALUES: FormValues = { name: '', email: '', company: '', topic: 'general', message: '' };

// Things we sell by arrangement rather than ship as self-serve. Buyer language only.
const ENTERPRISE_ITEMS = ['sso', 'capacity', 'selfHosted'] as const;

// The validation module returns English sentences (the API route shares it); the form maps each
// known one to a catalogue key and shows anything else (a server-only message) as received.
const VALIDATION_KEYS: Record<string, string> = {
  'Please enter your name.': 'nameRequired',
  'Name must be between 2 and 100 characters.': 'nameLength',
  'Please enter your email address.': 'emailRequired',
  'Please enter a valid email address.': 'emailInvalid',
  'Company name must be 100 characters or fewer.': 'companyLength',
  'Please choose a topic from the list.': 'topicInvalid',
  'Please enter a message.': 'messageRequired',
  'Message must be between 20 and 5000 characters.': 'messageLength',
  'Invalid submission.': 'invalid',
};

// The opt-in label is the registered consent wording, character for character, with its
// "Privacy Policy" mention rendered as a link.
const OPT_IN_LABEL = splitPrivacyLink(MARKETING_WORDING.text);

type FormBannerError =
  | { kind: 'network' }
  | { kind: 'validation'; messages: string[] }
  | { kind: 'server' };

function errorsToMap(
  errors: ValidationError[],
  localize: (message: string) => string,
): Partial<Record<FieldName, string>> {
  const map: Partial<Record<FieldName, string>> = {};
  for (const err of errors) {
    if (err.field === 'name' || err.field === 'email' || err.field === 'company' || err.field === 'message') {
      // Keep the first message per field.
      if (!map[err.field]) {
        map[err.field] = localize(err.message);
      }
    }
  }
  return map;
}

export function ContactForm() {
  const t = useT('contact.form');
  const localize = (message: string) => {
    const key = VALIDATION_KEYS[message];
    return key ? t(`validation.${key}`) : message;
  };
  const router = useRouter();
  const searchParams = useSearchParams();
  const [values, setValues] = useState<FormValues>(() => ({
    ...EMPTY_VALUES,
    topic: topicFromParam(searchParams.get('topic')),
  }));
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<FieldName, string>>>({});
  const [submitting, setSubmitting] = useState(false);
  const [bannerError, setBannerError] = useState<FormBannerError | null>(null);
  // Separate from the enquiry and unticked by default; sending the message never depends on it.
  const [marketingOptIn, setMarketingOptIn] = useState(false);
  // Optional first name for the newsletter; sent only while the opt-in is ticked.
  const [firstName, setFirstName] = useState('');

  const nameRef = useRef<HTMLInputElement>(null);
  const emailRef = useRef<HTMLInputElement>(null);
  const companyRef = useRef<HTMLInputElement>(null);
  const messageRef = useRef<HTMLTextAreaElement>(null);
  const bannerRef = useRef<HTMLDivElement>(null);
  const honeypotRef = useRef<HTMLInputElement>(null);
  // When the form mounted (ms since the epoch). The server ignores a form sent within 3 s of this.
  const startedAtRef = useRef<number | null>(null);
  useEffect(() => {
    startedAtRef.current = Date.now();
  }, []);

  const fieldRefs: Record<FieldName, React.RefObject<HTMLInputElement | HTMLTextAreaElement | null>> = {
    name: nameRef,
    email: emailRef,
    company: companyRef,
    message: messageRef,
  };

  function updateValue(field: TopicFieldName, value: string) {
    setValues((prev) => ({ ...prev, [field]: value }));
  }

  function handleBlur(field: FieldName): FocusEventHandler<HTMLInputElement | HTMLTextAreaElement> {
    return () => {
      const errors = validateContact({ ...values, website: '' });
      const map = errorsToMap(errors, localize);
      setFieldErrors((prev) => ({ ...prev, [field]: map[field] }));
    };
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBannerError(null);

    const website = honeypotRef.current?.value ?? '';
    const errors = validateContact({ ...values, website });
    const map = errorsToMap(errors, localize);

    if (Object.keys(map).length > 0) {
      setFieldErrors(map);
      const firstInvalid = (['name', 'email', 'company', 'message'] as FieldName[]).find(
        (field) => map[field],
      );
      if (firstInvalid) {
        fieldRefs[firstInvalid].current?.focus();
      } else {
        bannerRef.current?.focus();
      }
      return;
    }

    setFieldErrors({});
    setSubmitting(true);

    const payload = {
      ...values,
      website,
      marketingOptIn,
      ...(marketingOptIn && firstName.trim() ? { firstName: firstName.trim() } : {}),
      startedAt: startedAtRef.current,
    };

    try {
      const response = await fetch('/api/contact', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(payload),
      });

      if (response.ok) {
        // A honeypot-filtered submission also returns `{ ok: true }` — it's
        // invisible to the real visitor and should look identical to a
        // genuine success from their point of view.
        trackEvent('generate_lead', { form: 'contact' });
        router.push(marketingOptIn ? '/contact/thank-you?subscribe=check' : '/contact/thank-you');
        return;
      }

      if (response.status === 400) {
        let serverMessages: string[] = [];
        try {
          const body = await response.json();
          if (Array.isArray(body?.errors)) {
            const serverErrors: ValidationError[] = body.errors;
            setFieldErrors((prev) => ({ ...prev, ...errorsToMap(serverErrors, localize) }));
            serverMessages = serverErrors.map((e: ValidationError) => localize(e.message));
          }
        } catch {
          // Fall through to the generic validation message below.
        }
        setBannerError({
          kind: 'validation',
          messages: serverMessages.length > 0 ? serverMessages : [t('fixFields')],
        });
        bannerRef.current?.focus();
        return;
      }

      console.error('[contact] submission failed', response.status);
      setBannerError({ kind: 'server' });
      bannerRef.current?.focus();
    } catch (err) {
      console.error('[contact] submission failed', err);
      setBannerError({ kind: 'network' });
      bannerRef.current?.focus();
    } finally {
      setSubmitting(false);
    }
  }

  const inputClass = (field: FieldName) =>
    `mt-2 block min-h-11 w-full rounded-none border bg-paper px-3 py-2 font-mono text-sm text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong ${
      fieldErrors[field]
        ? 'border-danger-border text-danger'
        : 'border-line-strong'
    }`;

  return (
    <form onSubmit={handleSubmit} noValidate className="space-y-6">
      {bannerError && (
        <div
          ref={bannerRef}
          role="alert"
          aria-live="assertive"
          tabIndex={-1}
          className="border border-danger-border bg-danger-tint p-4 font-mono text-sm text-danger focus:outline-none"
        >
          {bannerError.kind === 'network' && (
            <p>{t('network')}</p>
          )}
          {bannerError.kind === 'validation' && (
            <>
              <p>{t('fixFields')}</p>
              {bannerError.messages.length > 0 && (
                <ul className="mt-2 list-inside list-disc">
                  {bannerError.messages.map((message) => (
                    <li key={message}>{message}</li>
                  ))}
                </ul>
              )}
            </>
          )}
          {bannerError.kind === 'server' && (
            <p>
              {t.rich('server', {
                email: SUPPORT_EMAIL,
                link: (chunks) => (
                  <a href={`mailto:${SUPPORT_EMAIL}`} className="font-medium underline">
                    {chunks}
                  </a>
                ),
              })}
            </p>
          )}
        </div>
      )}

      <div>
        <label htmlFor="name" className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {t('name')}
        </label>
        <input
          ref={nameRef}
          id="name"
          name="name"
          type="text"
          required
          autoComplete="name"
          value={values.name}
          onChange={(e) => updateValue('name', e.target.value)}
          onBlur={handleBlur('name')}
          aria-invalid={fieldErrors.name ? 'true' : undefined}
          aria-describedby={fieldErrors.name ? 'name-error' : undefined}
          className={inputClass('name')}
        />
        {fieldErrors.name && (
          <p id="name-error" role="alert" className="mt-1 text-sm text-danger">
            {fieldErrors.name}
          </p>
        )}
      </div>

      <div>
        <label htmlFor="email" className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {t('email')}
        </label>
        <input
          ref={emailRef}
          id="email"
          name="email"
          type="email"
          required
          autoComplete="email"
          value={values.email}
          onChange={(e) => updateValue('email', e.target.value)}
          onBlur={handleBlur('email')}
          aria-invalid={fieldErrors.email ? 'true' : undefined}
          aria-describedby={fieldErrors.email ? 'email-error' : undefined}
          className={inputClass('email')}
        />
        {fieldErrors.email && (
          <p id="email-error" role="alert" className="mt-1 text-sm text-danger">
            {fieldErrors.email}
          </p>
        )}
      </div>

      <div>
        <label htmlFor="company" className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {t('company')} <span className="text-ink-faint">({t('optional')})</span>
        </label>
        <input
          ref={companyRef}
          id="company"
          name="company"
          type="text"
          autoComplete="organization"
          value={values.company}
          onChange={(e) => updateValue('company', e.target.value)}
          onBlur={handleBlur('company')}
          aria-invalid={fieldErrors.company ? 'true' : undefined}
          aria-describedby={fieldErrors.company ? 'company-error' : undefined}
          className={inputClass('company')}
        />
        {fieldErrors.company && (
          <p id="company-error" role="alert" className="mt-1 text-sm text-danger">
            {fieldErrors.company}
          </p>
        )}
      </div>

      <div>
        <label htmlFor="topic" className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {t('topic')}
        </label>
        <select
          id="topic"
          name="topic"
          value={values.topic}
          onChange={(e) => {
            if (isContactTopic(e.target.value)) updateValue('topic', e.target.value);
          }}
          className="mt-2 block min-h-11 w-full rounded-none border border-line-strong bg-paper px-3 py-2 font-mono text-sm text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
        >
          {CONTACT_TOPICS.map((topic) => (
            <option key={topic} value={topic}>
              {t(`topics.${topic}`)}
            </option>
          ))}
        </select>
        {values.topic === 'enterprise' && (
          <div className="mt-3 border border-line-strong bg-paper-2 p-4 font-mono text-sm text-ink-muted">
            <p>
              {t('enterpriseIntro')}
            </p>
            <ul className="mt-2 list-inside list-disc text-ink">
              {ENTERPRISE_ITEMS.map((item) => (
                <li key={item}>
                  {t(`enterpriseItems.${item}`)} <span className="text-ink-faint">({t('byArrangement')})</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      <div>
        <label htmlFor="message" className="block font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {t('message')}
        </label>
        <textarea
          ref={messageRef}
          id="message"
          name="message"
          required
          rows={5}
          autoComplete="off"
          value={values.message}
          onChange={(e) => updateValue('message', e.target.value)}
          onBlur={handleBlur('message')}
          aria-invalid={fieldErrors.message ? 'true' : undefined}
          aria-describedby={fieldErrors.message ? 'message-error' : undefined}
          className={inputClass('message')}
        />
        {fieldErrors.message && (
          <p id="message-error" role="alert" className="mt-1 text-sm text-danger">
            {fieldErrors.message}
          </p>
        )}
      </div>

      <div className="flex min-h-11 items-center gap-3">
        <input
          id="marketingOptIn"
          name="marketingOptIn"
          type="checkbox"
          checked={marketingOptIn}
          onChange={(e) => {
            setMarketingOptIn(e.target.checked);
            if (!e.target.checked) setFirstName('');
          }}
          className="h-5 w-5 shrink-0 accent-accent-strong"
        />
        <label htmlFor="marketingOptIn" className="font-sans text-sm text-ink-muted">
          {OPT_IN_LABEL.before}
          {OPT_IN_LABEL.link && (
            <Link href="/legal/privacy" className="text-accent-ink underline hover:text-accent">
              {OPT_IN_LABEL.link}
            </Link>
          )}
          {OPT_IN_LABEL.after}
        </label>
      </div>
      {marketingOptIn && (
        <div>
          <label htmlFor="firstName" className="block font-sans text-sm font-medium text-ink">
            {t('firstName')}
          </label>
          <input
            id="firstName"
            name="firstName"
            type="text"
            autoComplete="given-name"
            maxLength={40}
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
            className="mt-1 block min-h-11 w-full rounded-none border border-line-strong bg-paper px-3 py-2 font-sans text-base text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
          />
        </div>
      )}

      {/* Honeypot field — hidden from real users, left empty by bots that fill every field. */}
      <div className="absolute left-[-9999px] top-auto h-px w-px overflow-hidden">
        <label htmlFor="website">{t('website')}</label>
        <input
          ref={honeypotRef}
          id="website"
          name="website"
          type="text"
          tabIndex={-1}
          autoComplete="off"
          aria-hidden="true"
        />
      </div>

      <button
        type="submit"
        disabled={submitting}
        aria-busy={submitting}
        className={`${buttonClass({ variant: 'accent', size: 'lg' })} w-full gap-2 disabled:opacity-70 sm:w-auto`}
      >
        {submitting ? (
          <>
            <Spinner className="h-4 w-4" />
            {t('sending')}
          </>
        ) : (
          t('submit')
        )}
      </button>
    </form>
  );
}
