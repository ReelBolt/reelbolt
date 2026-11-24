'use client';

import Link from 'next/link';
import { Alert, Anchor, Text } from '@mantine/core';
import { IconCoin } from '@tabler/icons-react';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { useT } from '@/i18n';
import { lowBalance } from '@/lib/utils/billing';
import { useCreditAmount } from './useCreditAmount';

/**
 * The dashboard-wide "running low" bar (B10c-UI): shown when the workspace's AVAILABLE credits fall
 * below {@link LOW_BALANCE_FRACTION} of what its plan grants for a period.
 *
 * It is deliberately quiet about what it cannot know. A deployment without billing 404s the summary
 * and shows nothing; so does a workspace whose plan allowance is not on the wire — the summary lists
 * only the plans it can sell, so Trial and Enterprise workspaces have no allowance to compare
 * against, and inventing one would be a warning pulled out of thin air.
 *
 * The threshold is on available credits (balance minus open holds), which is the same figure the
 * execute preflight refuses a run against — a workspace whose credits are all held by runs in flight
 * is genuinely unable to start another one, and the banner says so before the 402 does.
 */
export function LowBalanceBanner() {
  const { data, error } = useBillingSummary();
  const t = useT('billing.lowBalance');
  const formatCreditAmount = useCreditAmount();
  // A failed summary is not a low balance: it is a summary we could not read.
  const state = error ? null : lowBalance(data);
  if (!state?.low) return null;

  const percent = Math.max(0, Math.round((state.available / state.allowance) * 100));

  return (
    <Alert
      icon={<IconCoin size={16} />}
      color="yellow"
      variant="light"
      mb="md"
      role="status"
      title={t('title')}
      data-testid="low-balance-banner"
    >
      <Text size="sm">
        {t('body', { available: formatCreditAmount(state.available), allowance: formatCreditAmount(state.allowance), percent })}
      </Text>
      <Anchor component={Link} href="/settings/billing" size="sm" fw={500} mt={4} display="block">
        {t('topUp')}
      </Anchor>
    </Alert>
  );
}
