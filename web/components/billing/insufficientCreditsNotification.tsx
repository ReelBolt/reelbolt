'use client';

import Link from 'next/link';
import { Anchor, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { insufficientCredits } from '@/lib/api/billing';
import type { InsufficientCredits } from '@/lib/types/billing';
import { useT } from '@/i18n';
import { formatCreditAmount } from '@/lib/utils/billing';
import { emitUpgradeEvent } from '@/lib/telemetry/upgrade';
import { useUpgradePromptShown } from '@/lib/telemetry/useUpgradePromptShown';
import { useCreditAmount } from './useCreditAmount';

/**
 * The plain sentence a refused AI tool or proposal gets, with the server's own figures.
 * @deprecated English-only; the notification itself uses the `billing.insufficientToast` catalogue messages.
 */
export function insufficientCreditsSentence(data: Pick<InsufficientCredits, 'required' | 'balance'>): string {
  return `Not enough credits for this. It needs about ${formatCreditAmount(data.required)} credits and you have ${formatCreditAmount(data.balance)}.`;
}

function ToastTitle() {
  const t = useT('billing.insufficientToast');
  return <>{t('title')}</>;
}

function ToastBody({ data }: { data: Pick<InsufficientCredits, 'required' | 'balance'> }) {
  const t = useT('billing.insufficientToast');
  const formatCreditAmount = useCreditAmount();
  useUpgradePromptShown('insufficient_credits_toast');
  return (
    <Stack gap={4}>
      <Text size="sm">{t('body', { required: formatCreditAmount(data.required), balance: formatCreditAmount(data.balance) })}</Text>
      <Anchor component={Link} href="/settings/billing" size="sm" onClick={() => emitUpgradeEvent('billing.upgrade_clicked', 'insufficient_credits_toast')}>
        {t('add')}
      </Anchor>
    </Stack>
  );
}

/**
 * Shows the "not enough credits" notification for a 402 `INSUFFICIENT_CREDITS` refusal (a picture
 * editor AI tool, a timeline AI proposal) and answers true, so the caller skips its generic error.
 * Anything else answers false and shows nothing. The refusal came before the vendor call, so nothing
 * was charged.
 */
export function notifyInsufficientCredits(error: unknown): boolean {
  const data = insufficientCredits(error);
  if (!data) return false;
  notifications.show({
    id: 'insufficient-credits',
    color: 'yellow',
    title: <ToastTitle />,
    autoClose: 10_000,
    message: <ToastBody data={data} />,
  });
  return true;
}
