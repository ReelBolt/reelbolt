'use client';

import Link from 'next/link';
import { Alert, Button, Group, Text } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import type { InsufficientCredits } from '@/lib/types/billing';
import { useT } from '@/i18n';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { emitUpgradeEvent } from '@/lib/telemetry/upgrade';
import { useUpgradePromptShown } from '@/lib/telemetry/useUpgradePromptShown';
import { useCreditAmount, useCreditRange } from './useCreditAmount';

interface InsufficientCreditsAlertProps {
  /** The parsed 402 body (see `insufficientCredits`), never a bare error: the shortfall is the point. */
  data: InsufficientCredits;
  onDismiss?: () => void;
}

/**
 * The state a refused run leaves behind (B10c-UI): the server answered 402 `INSUFFICIENT_CREDITS`
 * before anything was queued, so this is not a failure to report but a decision to explain — how much
 * the run needs, how much is there, and the shortest way to close the gap.
 *
 * The figures are the server's own, read from the refusal body rather than re-derived here: the
 * balance the engine compared is exactly the balance shown.
 */
export function InsufficientCreditsAlert({ data, onDismiss }: InsufficientCreditsAlertProps) {
  const t = useT('billing.insufficient');
  const formatCreditAmount = useCreditAmount();
  const creditRange = useCreditRange();
  // The server's own answer to "may this workspace buy credits?": a Trial workspace may not, so the
  // way forward for it is a plan, and a "Top up" button would land on a page that says "paid plans only".
  const summary = useBillingSummary();
  useUpgradePromptShown('insufficient_credits_alert');
  const needsPlan = summary.data?.topUp?.available === false;
  return (
    <Alert
      color="yellow"
      variant="light"
      icon={<IconAlertTriangle size={16} />}
      title={t('title')}
      role="alert"
      mb="md"
      data-testid="insufficient-credits"
    >
      <Text size="sm">
        {data.message ?? (
          data.shortfall > 0
            ? t('needsShort', { required: formatCreditAmount(data.required), balance: formatCreditAmount(data.balance), shortfall: formatCreditAmount(data.shortfall) })
            : t('needs', { required: formatCreditAmount(data.required), balance: formatCreditAmount(data.balance) })
        )}{' '}
        {t('nothingCharged')}
      </Text>

      {data.low !== null && data.high !== null && (
        <Text size="xs" c="dimmed" mt={4}>
          {t('estimated', { range: creditRange(data.low, data.high) })}
        </Text>
      )}

      {!data.hasHistory && (
        <Text size="xs" c="dimmed" mt={4}>
          {t('noHistory')}
        </Text>
      )}

      <Group gap="sm" mt="xs">
        <Button
          component={Link}
          href={needsPlan ? '/settings/billing#plans' : '/settings/billing'}
          size="compact-sm"
          variant="light"
          data-testid="insufficient-credits-cta"
          onClick={() => emitUpgradeEvent('billing.upgrade_clicked', 'insufficient_credits_alert')}
        >
          {needsPlan ? t('choosePlan') : t('topUp')}
        </Button>
        {onDismiss && (
          <Button size="compact-sm" variant="subtle" color="gray" onClick={onDismiss}>
            {t('dismiss')}
          </Button>
        )}
      </Group>
    </Alert>
  );
}
