'use client';

import { useState } from 'react';
import { Alert, Button, Card, Group, Stack, Text, TextInput } from '@mantine/core';
import { IconAlertCircle, IconDiscount, IconGift } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { promoCodeMessage, redeemPromoCode, type RedeemResult } from '@/lib/api/promotions';
import { useT } from '@/i18n';
import { formatCredits } from '@/lib/utils/usage-labels';
import { useLongDate } from './useBillingFormat';

/** An example code shown in the empty field: a code, not copy. */
const CODE_EXAMPLE = 'SPRING20';

/** Why a code did not apply as a discount; `rateLimited` comes from the status code, not the wording. */
export interface ApplyOutcomeFailure { message: string; rateLimited: boolean }
/** Null when the code applied to at least one thing. */
export type ApplyOutcome = ApplyOutcomeFailure | null;

/**
 * The one "Have a code?" field on the billing page. A code is either a discount (the plan and pack
 * cards then show the discounted price the payment provider quoted, and the checkout carries the
 * code) or a gift of free credits (added to the balance at once). The buyer does not have to know
 * which: the code is tried as a discount first, then redeemed, and only one error is shown.
 *
 * The field never decides validity itself: `onApply` asks the server and answers with an error
 * sentence, or null when the code applied to at least one thing; `redeemPromoCode` asks the server
 * too.
 */
export function PromoCodeField({
  canManage,
  canApply,
  appliedCode,
  appliedTo,
  disabled,
  onApply,
  onRemove,
  onRedeemed,
}: {
  /** Owner or Billing member: anyone else is told who can use a code. */
  canManage: boolean;
  /** Something is on sale that a discount code can be tried against. */
  canApply: boolean;
  appliedCode: string | null;
  /** What the applied code discounts, as the buyer sees it ("Creator", "1,000 credits"). */
  appliedTo: string[];
  disabled: boolean;
  onApply: (code: string) => Promise<ApplyOutcome>;
  onRemove: () => void;
  /** Called after a successful redeem, so the balance and the grants list refresh. */
  onRedeemed: () => void;
}) {
  const t = useT('billing.promoField');
  const longDate = useLongDate();
  const [value, setValue] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<RedeemResult | null>(null);

  async function submit() {
    const code = value.trim();
    if (!code) return;
    setPending(true);
    setError(null);
    setResult(null);
    try {
      let applyError: string | null = null;
      if (canApply) {
        const outcome = await onApply(code);
        if (outcome === null) {
          setValue('');
          return;
        }
        applyError = outcome.message;
        // Too many attempts is final: a second request would only use up another one.
        if (outcome.rateLimited) {
          setError(applyError);
          return;
        }
      }
      try {
        const redeemed = await redeemPromoCode(code);
        setResult(redeemed);
        setValue('');
        onRedeemed();
      } catch (e) {
        const redeemError = promoCodeMessage(e);
        const notAGift = e instanceof ApiError && e.status !== 409 && e.status !== 429;
        setError(applyError && notAGift ? applyError : redeemError);
      }
    } finally {
      setPending(false);
    }
  }

  return (
    <Card withBorder radius="lg" padding="lg" data-testid="promo-code-card">
      <Stack gap="sm">
        <div>
          <Text fw={600}>{t('title')}</Text>
          <Text size="sm" c="dimmed">
            {t('description')}
          </Text>
        </div>

        {!canManage ? (
          <Text size="sm" c="dimmed">
            {t('needsOwner')}
          </Text>
        ) : appliedCode ? (
          <Group justify="space-between" gap="sm" wrap="wrap">
            <Group gap="xs">
              <IconDiscount size={18} />
              <Text size="sm">
                {t.rich('applied', { code: appliedCode, targets: appliedTo.join(', '), strong: (chunks) => <Text span fw={600}>{chunks}</Text> })}
              </Text>
            </Group>
            <Button variant="subtle" size="xs" onClick={onRemove} disabled={disabled}>
              {t('remove')}
            </Button>
          </Group>
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              void submit();
            }}
          >
            <Group align="flex-end" gap="sm" wrap="wrap">
              <TextInput
                label={t('label')}
                placeholder={CODE_EXAMPLE}
                value={value}
                onChange={(event) => setValue(event.currentTarget.value)}
                autoComplete="off"
                maxLength={40}
                w={220}
                disabled={disabled}
                data-testid="promo-code-input"
              />
              <Button
                type="submit"
                variant="light"
                loading={pending}
                disabled={disabled || value.trim() === ''}
                data-testid="promo-code-apply"
              >
                {t('apply')}
              </Button>
            </Group>
          </form>
        )}

        {error && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" role="alert">
            <Text size="sm">{error}</Text>
          </Alert>
        )}

        {result && (
          <Alert
            icon={<IconGift size={16} />}
            color="green"
            variant="light"
            title={result.headline || t('redeemed')}
            data-testid="redeem-code-result"
          >
            <Stack gap={4}>
              <Text size="sm">
                {t('added', { credits: formatCredits(result.credits), date: longDate(result.expiresAt) })}
              </Text>
              <Text size="sm">{t('noCashValue')}</Text>
            </Stack>
          </Alert>
        )}
      </Stack>
    </Card>
  );
}
