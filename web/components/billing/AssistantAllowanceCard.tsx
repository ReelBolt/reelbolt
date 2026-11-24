'use client';

import { Card, Group, Progress, Stack, Text } from '@mantine/core';
import type { AssistantAllowance } from '@/lib/types/billing';
import { useFormat, useT } from '@/i18n';
import { useCreditAmount } from './useCreditAmount';

/**
 * The monthly assistant allowance (`summary.assistantAllowance`): how much of the assistant and
 * Claude Code use the plan includes has gone this period. The caller renders nothing at all when
 * billing is off or the backend does not send an allowance.
 */
export function AssistantAllowanceCard({ allowance }: { allowance: AssistantAllowance }) {
  const { includedCredits, usedCredits, periodEnd } = allowance;
  const unlimited = includedCredits === null;
  // A zero allowance is used up from the first credit, so it reads as a full bar rather than 0/0.
  const percent =
    includedCredits === null ? 0
      : includedCredits <= 0 ? 100
        : Math.min(100, (usedCredits / includedCredits) * 100);
  const t = useT('billing.allowance');
  const f = useFormat();
  const formatCreditAmount = useCreditAmount();
  const endDate = new Date(periodEnd);
  const ends = Number.isNaN(endDate.getTime()) ? null : new Intl.DateTimeFormat(f.locale).format(endDate);

  return (
    <Card withBorder radius="lg" padding="lg" data-testid="assistant-allowance-card">
      <Stack gap="xs">
        <Group justify="space-between" gap="xs">
          <Text size="sm" c="dimmed">{t('title')}</Text>
          {ends && <Text size="xs" c="dimmed">{t('periodEnds', { date: ends })}</Text>}
        </Group>
        {unlimited ? (
          <Text fw={600}>
            {t('unlimitedUsed', { count: usedCredits, amount: formatCreditAmount(usedCredits) })}
          </Text>
        ) : (
          <>
            <Text fw={600}>
              {t('usedOfIncluded', { used: formatCreditAmount(usedCredits), included: formatCreditAmount(includedCredits) })}
            </Text>
            <Progress
              value={percent}
              size="sm"
              radius="sm"
              color={percent >= 100 ? 'yellow' : undefined}
              aria-label={t('progressLabel')}
            />
          </>
        )}
        <Text size="xs" c="dimmed">
          {t('afterAllowance')}
        </Text>
      </Stack>
    </Card>
  );
}
