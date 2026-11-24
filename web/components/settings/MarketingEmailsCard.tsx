'use client';

import { useState } from 'react';
import { Alert, Loader, Group, Switch, Text } from '@mantine/core';
import {
  isEmailNotVerified,
  staleWordingVersionOf,
  updateMarketingConsent,
  type MarketingConsent,
} from '@/lib/api/marketing';
import { useMarketingConsent, useMarketingWording } from '@/lib/hooks/use-marketing';
import { MarketingWordingText } from '@/components/marketing/MarketingOptInCheckbox';
import { SettingsCard } from './SettingsCard';
import { useT, useFormat, type Format, type T } from '@/i18n';

function formatDate(iso: string | null, f: Format): string | null {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  return f.date(date, 'medium');
}

/** One line saying where the record stands, with its date. */
function statusLine(consent: MarketingConsent, t: T, f: Format): string {
  const consented = formatDate(consent.consentedAt, f);
  const withdrawn = formatDate(consent.withdrawnAt, f);
  switch (consent.status) {
    case 'optedIn':
      return consented ? t('status.optedInOn', { date: consented }) : t('status.optedIn');
    case 'pending':
      return t('status.pending');
    case 'withdrawn':
      return withdrawn ? t('status.withdrawnOn', { date: withdrawn }) : t('status.withdrawn');
    default:
      return t('status.none');
  }
}

/**
 * "Marketing emails" on the account page: the person's own consent, never a workspace's.
 *
 * Withdrawing is as easy as agreeing (GDPR 7(3)): one click on the switch, no confirmation dialog.
 * Turning it on sends the registry's current wording version with the grant, and that wording is
 * shown next to the switch so the person sees exactly what they agree to.
 *
 * A pending record (a website sign-up awaiting its confirmation click) is NOT consent yet, so the
 * switch shows it as off and stays enabled: turning it on here is a signed-in opt-in from settings.
 *
 * An account whose email is not verified gets 403 `EMAIL_NOT_VERIFIED` from both reads and writes
 * (S-02): the record that address keys may be someone else's until the address is proven. The card
 * then says so and keeps the switch disabled.
 */
export function MarketingEmailsCard() {
  const t = useT('settings.marketing');
  const f = useFormat();
  const consent = useMarketingConsent();
  const wording = useMarketingWording();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const on = consent.data?.status === 'optedIn';
  const unverified = isEmailNotVerified(consent.error);

  async function change(next: boolean) {
    setError(null);
    if (next && !wording.data) {
      setError(t('errors.unavailable'));
      return;
    }
    setSaving(true);
    try {
      const updated = await updateMarketingConsent({
        optIn: next,
        source: 'settings',
        wordingVersion: wording.data?.version ?? consent.data?.wordingVersion ?? '',
      });
      await consent.mutate(updated, { revalidate: false });
    } catch (err) {
      if (staleWordingVersionOf(err) !== null) {
        void wording.mutate();
        setError(t('errors.wordingChanged'));
      } else {
        setError(next ? t('errors.subscribeFailed') : t('errors.unsubscribeFailed'));
      }
    } finally {
      setSaving(false);
    }
  }

  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="marketing-emails-card"
    >
      {consent.data ? (
        <>
          <Switch
            data-testid="marketing-emails-switch"
            checked={on}
            disabled={saving || (!on && !wording.data)}
            onChange={(event) => void change(event.currentTarget.checked)}
            label={on ? t('subscribed') : t('notSubscribed')}
            description={wording.data ? <MarketingWordingText text={wording.data.text} /> : undefined}
          />
          <Text size="sm" c="dimmed" data-testid="marketing-emails-status">
            {statusLine(consent.data, t, f)}
          </Text>
        </>
      ) : unverified ? (
        <>
          <Switch
            data-testid="marketing-emails-switch"
            checked={false}
            disabled
            label={t('notSubscribed')}
            onChange={() => undefined}
          />
          <Text size="sm" c="dimmed" data-testid="marketing-emails-status">
            {t('verifyEmail')}
          </Text>
        </>
      ) : consent.error ? (
        <Text size="sm" c="dimmed">
          {t('readFailed')}
        </Text>
      ) : (
        <Group gap="xs">
          <Loader size="sm" />
          <Text size="sm" c="dimmed">
            {t('reading')}
          </Text>
        </Group>
      )}
      {error && (
        <Alert color="red" variant="light" data-testid="marketing-emails-error">
          {error}
        </Alert>
      )}
    </SettingsCard>
  );
}
