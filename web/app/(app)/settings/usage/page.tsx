'use client';

import { useMemo, useState } from 'react';
import {
  Alert, Button, Card, Center, Group, Loader, Progress, SegmentedControl, SimpleGrid, Stack, Text,
} from '@mantine/core';
import { IconAlertCircle, IconInfoCircle } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { UsageBreakdownTable } from '@/components/billing/UsageBreakdownTable';
import { AssistantAllowanceCard } from '@/components/billing/AssistantAllowanceCard';
import { VideoSpendLimitCard } from '@/components/billing/VideoSpendLimitCard';
import { isBillingUnavailable } from '@/lib/api/billing';
import { useBillingSummary, useBillingUsage } from '@/lib/hooks/use-billing';
import type { BillingSummary, BillingUsage, UsageBillingMode, UsageGroupBy } from '@/lib/types/billing';
import { formatCredits, usageModeLabel } from '@/lib/utils/usage-labels';
import { useT, useFormat } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

const RANGES = [{ value: '7' }, { value: '30' }, { value: '90' }] as const;

const AXIS_VALUES: UsageGroupBy[] = ['day', 'project', 'model', 'kind', 'mode', 'source'];

const DAY_MS = 86_400_000;

function windowStart(days: number, nowMs: number): string {
  return new Date(nowMs - days * DAY_MS).toISOString();
}

export default function UsagePage() {
  const t = useT('settings.usage');
  const tAll = useT();
  const rangeLabels = { '7': t('ranges.d7'), '30': t('ranges.d30'), '90': t('ranges.d90') };
  const axisLabels: Record<UsageGroupBy, string> = {
    day: t('axes.day'),
    project: t('axes.project'),
    model: t('axes.model'),
    kind: t('axes.kind'),
    mode: t('axes.mode'),
    source: t('axes.source'),
  };
  const axisPhrase: Record<UsageGroupBy, string> = {
    day: t('axisPhrase.day'),
    project: t('axisPhrase.project'),
    model: t('axisPhrase.model'),
    kind: t('axisPhrase.kind'),
    mode: t('axisPhrase.mode'),
    source: t('axisPhrase.source'),
  };
  const [axis, setAxis] = useState<UsageGroupBy>('day');
  const [days, setDays] = useState(30);
  // Sampled once into state rather than read during render, the same way the execution page samples
  // its clock: reading the wall clock mid-render would make the SWR key — and therefore the request —
  // change on every paint instead of only when the range does.
  const [nowMs] = useState(() => Date.now());

  const from = useMemo(() => windowStart(days, nowMs), [days, nowMs]);
  const usage = useBillingUsage({ from, groupBy: axis });
  const summary = useBillingSummary();

  const header = (
    <PageHeader
      title={t('title')}
      breadcrumbs={[{ label: t('breadcrumbDashboard'), href: '/dashboard' }, { label: t('title') }]}
    />
  );

  // `Billing:Enabled=false` answers 404 on every /api/v1/billing action. That is a property of
  // the deployment, not of the request, so it gets its own explanation instead of a retry button
  // that could never succeed.
  if (isBillingUnavailable(summary.error) || isBillingUnavailable(usage.error)) {
    return (
      <>
        {header}
        <Alert icon={<IconInfoCircle size={16} />} color="blue" title={t('noBilling.title')}>
          <Text size="sm">
            {t('noBilling.body')}
          </Text>
        </Alert>
      </>
    );
  }

  const failed = summary.error ?? usage.error;
  if (failed) {
    return (
      <>
        {header}
        <Alert icon={<IconAlertCircle size={16} />} color="red" title={t('loadFailed.title')}>
          <Group justify="space-between" gap="sm">
            <Text size="sm">
              {describeError(tAll, failed, t('loadFailed.fallback'))}
            </Text>
            <Button
              size="xs"
              variant="light"
              color="red"
              onClick={() => { void summary.mutate(); void usage.mutate(); }}
            >
              {t('loadFailed.retry')}
            </Button>
          </Group>
        </Alert>
      </>
    );
  }

  // Only the first load blanks the page; a range or axis change keeps the previous figures on screen
  // while the new ones arrive, so the page never flashes empty mid-interaction.
  if (usage.isLoading && !usage.data) {
    return (
      <>
        {header}
        <Center h={300}><Loader size="lg" aria-label={t('loadingAria')} /></Center>
      </>
    );
  }

  const totals = usage.data?.totals;
  const modes = usage.data?.byBillingMode ?? [];
  const groups = usage.data?.groups ?? [];

  return (
    <>
      {header}
      <Stack gap="lg">
        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>

        <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
          <CreditsCard summary={summary.data} loading={summary.isLoading} />
          <TotalsCard usage={usage.data} days={days} />
          <WhoPaysCard modes={modes} />
        </SimpleGrid>

        {summary.data?.assistantAllowance && (
          <AssistantAllowanceCard allowance={summary.data.assistantAllowance} />
        )}

        <VideoSpendLimitCard />

        <Card withBorder radius="lg" padding="lg">
          <Stack gap="md">
            <Group justify="space-between" gap="sm" wrap="wrap">
              <Text fw={600}>{t('breakdown.title')}</Text>
              <Group gap="xs" wrap="wrap">
                <SegmentedControl
                  size="xs"
                  value={String(days)}
                  onChange={(value) => setDays(Number(value))}
                  data={RANGES.map((r) => ({ label: rangeLabels[r.value], value: r.value }))}
                  aria-label={t('breakdown.periodAria')}
                />
                <SegmentedControl
                  size="xs"
                  value={axis}
                  onChange={(value) => setAxis(value as UsageGroupBy)}
                  data={AXIS_VALUES.map((value) => ({ label: axisLabels[value], value }))}
                  aria-label={t('breakdown.groupByAria')}
                />
              </Group>
            </Group>

            {groups.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('breakdown.empty', { days })}
              </Text>
            ) : (
              <>
                <Text size="sm" c="dimmed">
                  {t('breakdown.showing', { count: groups.length, axis: axisPhrase[axis] })}
                </Text>
                <UsageBreakdownTable
                  groups={groups}
                  groupBy={axis}
                  showCost={totals?.costUsd !== null && totals?.costUsd !== undefined}
                />
              </>
            )}
          </Stack>
        </Card>
      </Stack>
    </>
  );
}

function CreditsCard({ summary, loading }: { summary?: BillingSummary; loading: boolean }) {
  const t2 = useT('settings.usage.credits');
  const f = useFormat();
  return (
    <Card withBorder radius="lg" padding="lg" data-testid="credits-card">
      <Stack gap="xs">
        <Text size="sm" c="dimmed">{t2('available')}</Text>
        {loading && !summary ? (
          <Loader size="sm" aria-label={t2('loadingAria')} />
        ) : (
          <>
            <Text size="32px" fw={700} style={{ fontVariantNumeric: 'tabular-nums' }}>
              {formatCredits(summary?.credits.available ?? 0, f.locale)}
            </Text>
            <Text size="xs" c="dimmed">
              {t2('heldLine', {
                balance: formatCredits(summary?.credits.balance ?? 0, f.locale),
                reserved: formatCredits(summary?.credits.reserved ?? 0, f.locale),
              })}
            </Text>
            {summary?.subscription && (
              <Text size="xs" c="dimmed">
                {summary.subscription.periodEnd
                  ? t2('renews', {
                      plan: summary.subscription.planName,
                      date: new Date(summary.subscription.periodEnd).toLocaleDateString(f.locale),
                    })
                  : summary.subscription.planName}
              </Text>
            )}
          </>
        )}
      </Stack>
    </Card>
  );
}

function TotalsCard({ usage, days }: { usage?: BillingUsage; days: number }) {
  const t3 = useT('settings.usage.totals');
  const f = useFormat();
  return (
    <Card withBorder radius="lg" padding="lg" data-testid="totals-card">
      <Stack gap="xs">
        <Text size="sm" c="dimmed">{t3('used')}</Text>
        <Text size="32px" fw={700} style={{ fontVariantNumeric: 'tabular-nums' }}>
          {formatCredits(usage?.totals.credits ?? 0, f.locale)}
        </Text>
        <Text size="xs" c="dimmed">
          {t3('calls', { events: usage?.totals.events ?? 0, days })}
        </Text>
        {/* Our own provider cost (COGS). The backend sends it only to a platform admin. */}
        {usage?.totals.costUsd != null && (
          <Text size="xs" c="dimmed">{t3('providerCost', {
            cost: f.currency(usage.totals.costUsd, 'USD', { minimumFractionDigits: 4, maximumFractionDigits: 4 }),
          })}</Text>
        )}
      </Stack>
    </Card>
  );
}

function WhoPaysCard({ modes }: { modes: UsageBillingMode[] }) {
  const t4 = useT('settings.usage.whoPays');
  const tr = useT();
  const f = useFormat();
  const total = modes.reduce((sum, m) => sum + m.credits, 0);
  return (
    <Card withBorder radius="lg" padding="lg" data-testid="who-pays-card">
      <Stack gap="xs">
        <Text size="sm" c="dimmed">{t4('title')}</Text>
        {modes.length === 0 ? (
          <Text size="sm" c="dimmed">{t4('empty')}</Text>
        ) : (
          modes.map((mode) => (
            <div key={mode.mode}>
              <Group justify="space-between" gap="xs">
                <Text size="sm">{usageModeLabel(mode.mode, tr)}</Text>
                <Text size="sm" fw={600} style={{ fontVariantNumeric: 'tabular-nums' }}>
                  {formatCredits(mode.credits, f.locale)}
                </Text>
              </Group>
              <Progress
                value={total > 0 ? (mode.credits / total) * 100 : 0}
                size={4}
                radius="xl"
                mt={4}
                aria-label={t4('shareAria', { mode: usageModeLabel(mode.mode, tr) })}
              />
            </div>
          ))
        )}
        <Text size="xs" c="dimmed">
          {t4('note')}
        </Text>
      </Stack>
    </Card>
  );
}
