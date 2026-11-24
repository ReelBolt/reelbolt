'use client';

import { Alert } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { TRIAL_PLAN_KEY } from '@/components/billing/subscription';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { useT } from '@/i18n';

/**
 * On the Trial plan a key can read through the API but cannot run apps or upload (the server
 * answers 402 `API_PLAN_READ_ONLY`). The plan comes from the billing summary the key form already uses.
 */
export function TrialReadOnlyNotice() {
  const t = useT('org.apiKeys');
  const { data: billing } = useBillingSummary(true);
  if (billing?.subscription?.planKey !== TRIAL_PLAN_KEY) return null;
  return (
    <Alert icon={<IconInfoCircle size={16} />} color="blue" variant="light" data-testid="api-key-trial-notice">
      {t('capGuard.trialNotice')}
    </Alert>
  );
}
