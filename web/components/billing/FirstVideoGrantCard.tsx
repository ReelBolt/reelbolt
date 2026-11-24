'use client';

import { Card, Group, Text, ThemeIcon } from '@mantine/core';
import { IconGift } from '@tabler/icons-react';
import Link from 'next/link';
import type { FirstVideoGrant, FirstVideoGrantStatus } from '@/lib/types/billing';
import { useFormat, useT } from '@/i18n';
import { useCreditAmount } from './useCreditAmount';

/** "March 4, 2027" for an ISO timestamp, or null when it does not parse. */
export function useGrantExpiry(): (iso: string) => string | null {
  const f = useFormat();
  return (iso) => {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return null;
    return f.date(date, 'medium');
  };
}


/** Plain note shown when the free first video was refused; links to the plans on the billing page. */
export function FirstVideoUnavailableNote({ testId }: { testId: string }) {
  const t = useT('billing.firstVideo');
  return (
    <Text size="sm" data-testid={testId}>
      {t('unavailable')}{' '}
      <Link href="/settings/billing#plans" className="touch-link">{t('seePlans')}</Link>
    </Text>
  );
}

/** Billing page card for the one-time free-first-video credits. Renders nothing without a grant. */
export function FirstVideoGrantCard({ grant, status }: { grant: FirstVideoGrant | null | undefined; status?: FirstVideoGrantStatus }) {
  const t = useT('billing.firstVideo');
  const formatCreditAmount = useCreditAmount();
  const formatGrantExpiry = useGrantExpiry();
  if (status === 'unavailable') {
    return (
      <Card withBorder padding="md" data-testid="first-video-grant-card-unavailable">
        <FirstVideoUnavailableNote testId="first-video-unavailable-note" />
      </Card>
    );
  }
  if (!grant) return null;
  const expires = formatGrantExpiry(grant.expiresAt);
  return (
    <Card withBorder padding="md" data-testid="first-video-grant-card">
      <Group wrap="nowrap" align="flex-start">
        <ThemeIcon variant="light" size="lg" radius="md"><IconGift size={18} /></ThemeIcon>
        <div>
          <Text fw={600}>{t('title')}</Text>
          <Text size="sm">
            {t('left', { remaining: formatCreditAmount(grant.remaining), total: formatCreditAmount(grant.credits) })}
            {expires ? ` ${t('useBy', { date: expires })}` : ''}
          </Text>
          <Text size="sm" c="dimmed">{t('watermark')}</Text>
        </div>
      </Group>
    </Card>
  );
}
