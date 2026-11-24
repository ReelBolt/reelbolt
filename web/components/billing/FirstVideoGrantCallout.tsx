'use client';

import { Alert, Text } from '@mantine/core';
import { IconGift } from '@tabler/icons-react';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { useT } from '@/i18n';
import { FirstVideoUnavailableNote, useGrantExpiry } from './FirstVideoGrantCard';
import { useCreditAmount } from './useCreditAmount';

/** Dashboard callout: shown only while free first-video credits are unspent. Not dismissible. */
export function FirstVideoGrantCallout() {
  const { data, error } = useBillingSummary();
  const t = useT('billing.firstVideo');
  const formatCreditAmount = useCreditAmount();
  const formatGrantExpiry = useGrantExpiry();
  if (!error && data?.firstVideoGrantStatus === 'unavailable') {
    return (
      <Alert color="gray" data-testid="first-video-grant-callout-unavailable">
        <FirstVideoUnavailableNote testId="first-video-unavailable-note" />
      </Alert>
    );
  }
  const grant = error ? null : data?.firstVideoGrant;
  if (!grant || grant.remaining <= 0) return null;
  const expires = formatGrantExpiry(grant.expiresAt);
  return (
    <Alert icon={<IconGift size={16} />} color="green" title={t('calloutTitle')} data-testid="first-video-grant-callout">
      <Text size="sm">
        {t('calloutBody', { amount: formatCreditAmount(grant.remaining) })}
        {expires ? ` ${t('expiresOn', { date: expires })}` : ''} {t('watermark')}
      </Text>
    </Alert>
  );
}
