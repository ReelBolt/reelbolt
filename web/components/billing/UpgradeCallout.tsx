'use client';

import Link from 'next/link';
import { Alert, Button, Group, Text } from '@mantine/core';
import { IconLock } from '@tabler/icons-react';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { planFeatureMsg } from '@/lib/utils/plan-refusal';
import { useT } from '@/i18n';
import { emitUpgradeEvent, useUpgradePromptShown } from '@/lib/telemetry';
import { pickedPlanHref } from './PickedPlanNotice';

export interface UpgradeCalloutProps {
  /** The plan-gate feature key the refusal named (`video_generation`, `resolution`, ...); null reads as "This feature". */
  feature: string | null | undefined;
  /** The plan the refusal itself named (`unlockPlan`, `unlockPlanName`); wins over the billing summary's answer. */
  plan?: { key: string | null; name: string | null } | null;
  /** One line inside a banner that already says what happened: no title, just where it is available and the way to it. */
  compact?: boolean;
  testId?: string;
}

/**
 * The next step for a refusal that is the plan's doing: what is not included, the lowest plan that includes it, and the way
 * to the plans page. Shown wherever a PLAN_NOT_ENTITLED refusal is (a failed run, a refused start, the editor).
 *
 * Display only. The plan name comes from the billing summary's catalogue (`entitlements.unlocks`) or the refusal body, never
 * from this file, and no price is shown. It renders nothing while the summary is unread or when billing is off on this
 * deployment (the summary 404s), so a self-hosted install never sees it. Only a member who can manage billing gets the
 * button; anyone else is told to ask the workspace owner, because the button would land on a page that refuses them.
 */
export function UpgradeCallout({ feature, plan, compact = false, testId = 'upgrade-callout' }: UpgradeCalloutProps) {
  const t = useT('billing.upgrade');
  const { data } = useBillingSummary();
  useUpgradePromptShown('plan_gate', !!data);
  if (!data) return null;

  const fromSummary = feature ? data.entitlements?.unlocks?.find((u) => u.feature === feature) : undefined;
  const planName = plan?.name ?? fromSummary?.planName ?? null;
  const planKey = plan?.key ?? fromSummary?.planKey ?? null;
  const canManage = data.canManageBilling;

  const availability = <Text size="sm">{planName ? t('availableOn', { plan: planName }) : t('upgradeToUnlock')}</Text>;
  const action = canManage ? (
    <Button
      component={Link}
      href={planKey ? pickedPlanHref(planKey) : '/settings/billing#plans'}
      size="compact-sm"
      style={{ minHeight: 44 }}
      data-testid={`${testId}-button`}
      onClick={() => emitUpgradeEvent('billing.upgrade_clicked', 'plan_gate')}
    >
      {planName ? t('upgradeTo', { plan: planName }) : t('seePlans')}
    </Button>
  ) : (
    <Text size="sm" fw={500} data-testid={`${testId}-ask-owner`}>{t('askOwner')}</Text>
  );

  if (compact) {
    return (
      <Group gap="sm" align="center" data-testid={testId} role="note">
        {availability}
        {action}
      </Group>
    );
  }
  return (
    <Alert color="violet" variant="light" icon={<IconLock size={16} />} title={t('title')} role="note" data-testid={testId}>
      <Text size="sm" fw={500} data-testid={`${testId}-what`}>{t.msg(planFeatureMsg(feature))}</Text>
      <Group gap="sm" align="center" mt={4}>
        {availability}
        {action}
      </Group>
    </Alert>
  );
}
