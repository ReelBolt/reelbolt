'use client';

import { useState } from 'react';
import { Alert, Anchor, Button, Checkbox, Stack, Text, Title } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { acceptTerms } from '@/lib/api/auth';
import { ApiError } from '@/lib/api/client';
import { isEmailNotVerified, updateMarketingConsent } from '@/lib/api/marketing';
import { LEGAL_PRIVACY_URL, LEGAL_TERMS_URL, legalVersionsFromRefusal } from '@/lib/auth/legal';
import { useMarketingConsent, useMarketingWording } from '@/lib/hooks/use-marketing';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { MarketingOptInCheckbox } from '@/components/marketing/MarketingOptInCheckbox';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/** What moved since this session last accepted (see `legalChangeFor` in lib/auth/legal.ts). */
export type LegalChange = 'terms' | 'privacy';

/**
 * The re-acceptance gate reached from web/proxy.ts when the readable `reelbolt_user` cookie
 * records a version behind the deployed one, and the page the OAuth callback sends a first login to
 * (C10).
 *
 * The acceptance is recorded SERVER-side by `POST /api/v1/auth/accept-terms`, which writes the
 * `legal_acceptances` rows and re-issues the session — so the cookie the proxy gate reads carries
 * the new version because the API set it, not because this component edited `document.cookie`.
 * Before that endpoint existed (C7-UI) the page could only write the cookie, which proved nothing;
 * that KNOWN GAP is closed here.
 *
 * F-7: the version this form sends is the one the SERVER reported when the page rendered, and the
 * API records its own — a submission naming anything else is refused with 409, which carries the
 * current versions. This form then shows those, unchecks the box and asks the visitor to accept
 * again, because a version published between the render and the submit is a different document to
 * agree to, not a retry.
 *
 * Two variants (saas-promotions compliance, "Forced re-acceptance is an acknowledgement"):
 * - `change="terms"`: the Terms moved, so the visitor ticks an explicit acceptance.
 * - `change="privacy"`: only the Privacy Policy moved. A privacy notice is information, not
 *   consent, so the page says what changed, links the policy and offers "Continue": no required
 *   checkbox, never "I agree". It posts `privacyAcknowledgement: true` with `acceptTerms: false`
 *   (S-23), and the API records ONLY a Privacy acknowledgement from it. The variant is chosen from
 *   the readable cookie, which the browser can edit, so the API is the authority: when the Terms
 *   version it has stored for this user is not current it answers 409, and this form then shows the
 *   full Terms variant instead.
 *
 * Both carry the optional marketing box: unticked, secondary, separate from the acceptance, and
 * posted on its own to the consent endpoint only AFTER the acceptance succeeded. Its failure never
 * holds the visitor on this page. It is hidden for an account whose email is not verified (the
 * consent endpoint answers 403 `EMAIL_NOT_VERIFIED` there, S-02), and while that is still unknown.
 */
export function AcceptTermsForm({
  termsVersion,
  privacyVersion,
  change = 'terms',
  firstAcceptance = false,
}: {
  termsVersion: string;
  privacyVersion: string;
  change?: LegalChange;
  /**
   * This session has never accepted any version (an account created by an administrator, or a first
   * OAuth login). Wording only (A26): "Welcome, please accept" rather than "we updated". Recording the
   * acceptance earlier is a product decision and not done here.
   */
  firstAcceptance?: boolean;
}) {
  const [accepted, setAccepted] = useState(false);
  const [marketingOptIn, setMarketingOptIn] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  // Server-reported at render time; replaced by whatever a 409 refusal carries.
  const [versions, setVersions] = useState({ termsVersion, privacyVersion });
  // A 409 can reveal that the Terms moved too; an acknowledgement then becomes an acceptance.
  const [variant, setVariant] = useState<LegalChange>(change);
  const wording = useMarketingWording();
  // Read only to learn whether this account may hold a marketing record at all.
  const consent = useMarketingConsent();
  const consentSettled = consent.data !== undefined || consent.error !== undefined;
  const offerMarketing = wording.data !== undefined && consentSettled && !isEmailNotVerified(consent.error);
  const router = useRouter();
  const t = useT('auth.terms');
  const tAll = useT();

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (variant === 'terms' && !accepted) {
      setError(t('pleaseAccept'));
      return;
    }
    setError('');
    setLoading(true);
    try {
      await acceptTerms(
        variant === 'privacy'
          ? {
              acceptTerms: false,
              privacyAcknowledgement: true,
              termsVersion: versions.termsVersion,
              privacyVersion: versions.privacyVersion,
            }
          : {
              acceptTerms: true,
              termsVersion: versions.termsVersion,
              privacyVersion: versions.privacyVersion,
            },
      );
    } catch (err: unknown) {
      setLoading(false);
      if (variant === 'privacy' && err instanceof ApiError && err.status === 409) {
        // An acknowledgement cannot stand in for the Terms: either the Terms this user accepted are
        // not the current ones, or the documents moved under us. Either way, ask for the full
        // acceptance, of whatever versions the refusal names (or the ones already shown).
        const named = legalVersionsFromRefusal(err.body);
        if (named) setVersions(named);
        setAccepted(false);
        setVariant('terms');
        setError(t('alsoUpdated'));
        return;
      }
      const current = legalVersionsFromRefusal(err instanceof ApiError ? err.body : undefined);
      if (current) {
        // The documents were published under us. Show what is current now and make the visitor
        // accept again rather than silently recording consent to something they did not read.
        const termsMoved = current.termsVersion !== versions.termsVersion;
        setVersions(current);
        setAccepted(false);
        if (variant === 'terms' || termsMoved) {
          setVariant('terms');
          setError(t('termsUpdated', { version: current.termsVersion }));
        } else {
          setError(t('privacyUpdatedAgain'));
        }
        return;
      }
      setError(describeError(tAll, err, t('recordFailed')));
      return;
    }

    // The acceptance is recorded; the marketing choice is a separate request with its own record.
    if (marketingOptIn && offerMarketing && wording.data) {
      try {
        await updateMarketingConsent({ optIn: true, source: 'acceptTerms', wordingVersion: wording.data.version });
      } catch {
        // Never holds the visitor here: the choice can be made again from Settings at any time.
      }
    }
    notifyAuthChange();
    router.push('/dashboard');
  };

  const marketingBox = offerMarketing && wording.data ? (
    <MarketingOptInCheckbox wording={wording.data} checked={marketingOptIn} onChange={setMarketingOptIn} />
  ) : null;

  const errorAlert = error ? (
    <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
      {error}
    </Alert>
  ) : null;

  if (variant === 'privacy') {
    return (
      <form onSubmit={handleSubmit}>
        <Stack>
          <Title order={2} ta="center">
            {t('privacyTitle')}
          </Title>
          {errorAlert}
          <Text size="sm">
            {t('privacyBody')}
          </Text>
          <Text size="sm">
            <Anchor href={LEGAL_PRIVACY_URL} target="_blank" rel="noreferrer">
              {t('readPrivacy')}
            </Anchor>
          </Text>
          {marketingBox}
          <Button type="submit" fullWidth loading={loading} data-testid="privacy-continue-button">
            {t('continue')}
          </Button>
        </Stack>
      </form>
    );
  }

  return (
    <form onSubmit={handleSubmit}>
      <Stack>
        <Title order={2} ta="center">
          {firstAcceptance ? t('welcome') : t('updatedTitle')}
        </Title>
        {versions.termsVersion && (
          <Text size="sm" ta="center" c="dimmed">
            {t('version', { version: versions.termsVersion })}
          </Text>
        )}
        {errorAlert}
        <Text size="sm">
          {firstAcceptance
            ? t('introFirst')
            : t('introUpdated')}
        </Text>
        <Text size="sm">
          <Anchor href={LEGAL_TERMS_URL} target="_blank" rel="noreferrer">
            {t('termsLink')}
          </Anchor>
          {' · '}
          <Anchor href={LEGAL_PRIVACY_URL} target="_blank" rel="noreferrer">
            {t('privacyLink')}
          </Anchor>
        </Text>
        <Checkbox
          checked={accepted}
          onChange={(event) => setAccepted(event.currentTarget.checked)}
          label={
            versions.termsVersion
              ? t('acceptVersion', { version: versions.termsVersion })
              : t('acceptCurrent')
          }
        />
        {marketingBox}
        <Button type="submit" fullWidth loading={loading}>
          {t('submit')}
        </Button>
      </Stack>
    </form>
  );
}
