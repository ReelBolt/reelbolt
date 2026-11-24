'use client';

import { useEffect } from 'react';
import Link from 'next/link';
import { Alert, Button, Group, Text } from '@mantine/core';
import { IconTag } from '@tabler/icons-react';
import { isBillingUnavailable } from '@/lib/api/billing';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { useT } from '@/i18n';

/** The billing page with one plan picked out: `?plan=` marks the card, `#plans` scrolls to it. */
export function pickedPlanHref(planKey: string): string {
  return `/settings/billing?plan=${encodeURIComponent(planKey)}#plans`;
}

/**
 * "You picked the Pro plan": shown on the dashboard to someone who pressed a plan's button on the
 * pricing page before they had an account. Signing up puts every account on the trial, so the
 * choice used to vanish at sign-in; this keeps it one click away without getting between a new
 * person and their first video.
 *
 * It names a plan only when the workspace's own billing summary lists that plan for sale and the
 * workspace is not already on it. Billing switched off, an unknown plan token, the trial itself or
 * the current plan all render nothing and forget the choice (`onForget`). No price is shown here:
 * the billing page shows the provider's own.
 */
export function PickedPlanNotice({ planKey, onForget }: { planKey: string; onForget: () => void }) {
  const t = useT('billing.pickedPlan');
  const { data, error } = useBillingSummary();
  const plan = data?.plans?.find((candidate) => candidate.key === planKey) ?? null;
  // Known not to apply: billing is switched off here, or the summary answered and there is nothing
  // to offer. A summary that merely failed to load forgets nothing: the next visit asks again.
  const pointless = isBillingUnavailable(error) || (!error && data !== undefined && (plan === null || plan.current));

  useEffect(() => {
    if (pointless) onForget();
  }, [pointless, onForget]);

  if (!plan || plan.current) return null;
  return (
    <Alert icon={<IconTag size={16} />} color="violet" variant="light" title={t('title', { plan: plan.name })} mb="md" data-testid="picked-plan-notice">
      <Text size="sm">{t('body')}</Text>
      <Group gap="sm" mt="sm">
        <Button component={Link} href={pickedPlanHref(plan.key)} size="compact-sm" style={{ minHeight: 44 }} data-testid="picked-plan-open">
          {t('open', { plan: plan.name })}
        </Button>
        <Button variant="subtle" color="gray" size="compact-sm" style={{ minHeight: 44 }} onClick={onForget} data-testid="picked-plan-dismiss">
          {t('dismiss')}
        </Button>
      </Group>
    </Alert>
  );
}
