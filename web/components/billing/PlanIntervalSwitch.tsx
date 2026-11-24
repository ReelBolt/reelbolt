'use client';

import { SegmentedControl, Stack, Text } from '@mantine/core';
import type { BillingInterval } from '@/lib/types/billing';
import { useT } from '@/i18n';

/**
 * Monthly / Yearly choice above the plan cards. A yearly subscriber cannot go back to monthly here
 * (the server refuses it), so Monthly is disabled and one sentence says how to switch.
 */
export function PlanIntervalSwitch({
  value,
  onChange,
  annualSubscriber,
}: {
  value: BillingInterval;
  onChange: (next: BillingInterval) => void;
  annualSubscriber: boolean;
}) {
  const t = useT('plans.interval');
  return (
    <Stack gap={4} align="flex-start">
      <SegmentedControl
        data-testid="interval-switch"
        aria-label={t('label')}
        value={value}
        onChange={(next) => onChange(next === 'annual' ? 'annual' : 'monthly')}
        data={[
          { value: 'monthly', label: t('monthly'), disabled: annualSubscriber },
          { value: 'annual', label: t('annual') },
        ]}
      />
      {annualSubscriber && (
        <Text size="xs" c="dimmed" data-testid="interval-annual-note">
          {t('annualSubscriberNote')}
        </Text>
      )}
    </Stack>
  );
}
