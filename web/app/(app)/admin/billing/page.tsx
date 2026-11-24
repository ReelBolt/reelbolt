'use client';

import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Loader,
  SegmentedControl,
  SimpleGrid,
  Stack,
  Table,
  Tabs,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconAdjustments,
  IconAlertTriangle,
  IconChartBar,
  IconCoin,
  IconInfoCircle,
  IconHourglass,
  IconPlus,
} from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { BillingSectionHeading } from '@/components/admin/billing/BillingSectionHeading';
import { ConsolidationReportPanel } from '@/components/admin/billing/ConsolidationReportPanel';
import { PriceCoveragePanel } from '@/components/admin/billing/PriceCoveragePanel';
import { PricingSettingsPanel } from '@/components/admin/billing/PricingSettingsPanel';
import { ReconcilePanel } from '@/components/admin/billing/ReconcilePanel';
import { RateMultiplierTable } from '@/components/admin/RateMultiplierTable';
import { RateMultiplierForm } from '@/components/admin/RateMultiplierForm';
import {
  useBillingAdminOverview,
  usePriceCoverage,
  usePricingSettings,
  useRateMultipliers,
} from '@/lib/hooks/use-admin-billing';
import { useConsolidationReport } from '@/lib/hooks/use-inference-providers';
import { useAuth } from '@/lib/hooks/use-auth';
import { useMerchantLabel } from '@/lib/hooks/use-billing';
import type {
  BillingCollectedCurrency,
  BillingCostBucket,
  BillingCostSliceKey,
  BillingExcludedProvider,
} from '@/lib/types/billing-admin';
import {
  formatCreditsFromMillis,
  formatMinorAmount,
  formatPercent,
  formatUsd,
} from '@/lib/utils/billing';

const BREADCRUMBS = [{ label: 'Admin', href: '/admin' }, { label: 'Billing' }];

const WINDOW_OPTIONS = [
  { value: '30', label: '30 days' },
  { value: '90', label: '90 days' },
  { value: '365', label: '365 days' },
];

/** The cost slices the API returns, in the order the tabs present them. */
const SLICES: { key: BillingCostSliceKey; label: string; keyHeader: string }[] = [
  { key: 'byProviderKind', label: 'Provider', keyHeader: 'Provider kind' },
  { key: 'byModel', label: 'Model', keyHeader: 'Model' },
  { key: 'byUsageKind', label: 'Usage kind', keyHeader: 'Usage kind' },
  { key: 'bySource', label: 'Source', keyHeader: 'Source' },
  { key: 'byBillingMode', label: 'Billing mode', keyHeader: 'Billing mode' },
  { key: 'byTemplate', label: 'Template', keyHeader: 'Template' },
];

/**
 * The three areas this page is read in. The window's totals and the caveats that qualify them stay
 * above the tabs, so a reader gets the money picture first and only then chooses an area.
 */
const OVERVIEW = 'overview';
const PRICING = 'pricing';
const WAITING_ON_PROVIDER = 'waiting-on-provider';
const MULTIPLIERS = 'multipliers';

/** Both bounds are sent, so the window the operator picked is the window the server reports on. */
function windowParams(days: number): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to.getTime() - days * 24 * 60 * 60 * 1000);
  return { from: from.toISOString(), to: to.toISOString() };
}

function formatEventCount(count: number): string {
  return new Intl.NumberFormat('en-US').format(count);
}

/** A tab label with the number of rows waiting behind it, when there are any to count. */
function withCount(label: string, count: number): string {
  return count > 0 ? `${label} (${count})` : label;
}

function BucketTable({ buckets, keyHeader }: { buckets: BillingCostBucket[]; keyHeader: string }) {
  if (buckets.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        Nothing was metered in this window for this slice.
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={560}>
      <Table withTableBorder>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{keyHeader}</Table.Th>
            <Table.Th>Cost (USD)</Table.Th>
            <Table.Th>Credits</Table.Th>
            <Table.Th>Events</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {buckets.map((bucket) => (
            <Table.Tr key={bucket.key}>
              <Table.Td>{bucket.key}</Table.Td>
              <Table.Td>{formatUsd(bucket.costUsd)}</Table.Td>
              <Table.Td>{formatCreditsFromMillis(bucket.creditsMillis)}</Table.Td>
              <Table.Td>{formatEventCount(bucket.events)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function Tile({
  label,
  value,
  hint,
  badge,
  tooltip,
}: {
  label: string;
  value: string;
  hint: string;
  badge?: string;
  tooltip?: string;
}) {
  return (
    <Card withBorder padding="md" radius="md">
      <Group gap={6} wrap="nowrap">
        <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
          {label}
        </Text>
        {badge ? (
          <Badge size="xs" variant="light" color="violet">
            {badge}
          </Badge>
        ) : null}
        {tooltip ? (
          <Tooltip label={tooltip} multiline w={320} withArrow>
            <IconInfoCircle size={14} aria-label={label} />
          </Tooltip>
        ) : null}
      </Group>
      <Text size="xl" fw={700}>
        {value}
      </Text>
      <Text size="xs" c="dimmed">
        {hint}
      </Text>
    </Card>
  );
}

function paymentsText(count: number, zero: number): string {
  const base = `${count} payment${count === 1 ? '' : 's'}`;
  return zero > 0 ? `${base}, ${zero} charged 0 (fully discounted or covered by credit)` : base;
}

/** One currency's collected money: net up front, the rest of the breakdown in the hint. */
function collectedTile(row: BillingCollectedCurrency) {
  const money = (minor: number) => formatMinorAmount(minor, row.minorUnitExponent, row.currencyCode);
  const parts = [
    paymentsText(row.payments, row.zeroAmountPayments),
    `gross ${money(row.grossMinor)}`,
    `discounts ${money(row.discountMinor)}`,
    `tax ${money(row.taxMinor)}`,
    row.feeMinor === null ? 'fee not reported' : `fee ${money(row.feeMinor)}`,
    `${row.refunds} refund${row.refunds === 1 ? '' : 's'} ${money(row.refundedMinor)}`,
  ];
  if (row.currencyCode === null) parts.push('currency not stated');
  if (row.paymentsWithUnknownTax > 0) parts.push(`${row.paymentsWithUnknownTax} with tax unknown`);
  return (
    <Tile
      key={row.currencyCode ?? 'none'}
      label={`Collected in window${row.currencyCode ? ` (${row.currencyCode})` : ''}`}
      value={money(row.netMinor)}
      hint={parts.join(' · ')}
      tooltip={`What the provider actually charged after discounts, net of tax and refunds. Subscriptions ${money(row.subscriptionGrossMinor)} gross, top-ups ${money(row.topUpGrossMinor)} gross.`}
    />
  );
}

function providerName(key: string): string {
  return key.length > 0 ? key.charAt(0).toUpperCase() + key.slice(1) : key;
}

/** Other or test providers: visible, but explicitly not revenue. */
function ExcludedProviders({ excluded }: { excluded: BillingExcludedProvider[] }) {
  if (excluded.length === 0) return null;
  return (
    <Alert color="gray" variant="light" icon={<IconInfoCircle size={16} />} title="Not counted as revenue">
      <Stack gap={4}>
        {excluded.map((entry) => {
          const payments = entry.collected.reduce((n, row) => n + row.payments, 0);
          const amounts = entry.collected
            .map((row) =>
              `${row.currencyCode ?? 'currency not stated'} ${formatMinorAmount(row.netMinor, row.minorUnitExponent, row.currencyCode)} net`,
            )
            .join(', ');
          return (
            <Text size="sm" key={entry.provider}>
              <b>{providerName(entry.provider)}</b> - {payments} payment{payments === 1 ? '' : 's'}
              {amounts ? ` (${amounts})` : ''}, {entry.payingSubscriptions} paying subscription
              {entry.payingSubscriptions === 1 ? '' : 's'}: not counted (other or test provider)
            </Text>
          );
        })}
      </Stack>
    </Alert>
  );
}

/**
 * Platform-admin view of the money side of the ledger: the pricing rules (markup, multipliers, which
 * capabilities have no price) and the revenue/COGS picture those prices produced.
 *
 * Prices themselves live on each provider's page, per model capability; this page holds what applies
 * to all of them.
 */
export default function AdminBillingPage() {
  const tAll = useT();
  const provider = useMerchantLabel();
  const { isPlatformAdmin } = useAuth();
  const [windowValue, setWindowValue] = useState('30');
  const [multiplierFormOpened, setMultiplierFormOpened] = useState(false);

  const windowDays = Number(windowValue);
  const params = useMemo(() => windowParams(windowDays), [windowDays]);
  const overview = useBillingAdminOverview(params, isPlatformAdmin);
  const settings = usePricingSettings(isPlatformAdmin);
  const multipliers = useRateMultipliers(isPlatformAdmin);
  const coverage = usePriceCoverage(isPlatformAdmin);
  const consolidation = useConsolidationReport(isPlatformAdmin);

  const now = new Date();
  const allMultipliers = multipliers.data ?? [];
  const gapCount = coverage.data?.length ?? 0;
  const report = overview.data;
  const collectedNetUsd = report?.revenue.collectedNetUsd ?? null;
  const marginUsd =
    report && collectedNetUsd !== null ? collectedNetUsd - report.cost.totalUsd : null;
  const marginFlagged = report
    ? report.revenue.collectedNetUsd === null || report.revenue.otherCurrencyPayments > 0
    : false;

  useEffect(() => {
    if (!overview.error) return;
    notifications.show({
      title: 'Error',
      message:
        describeError(tAll, overview.error, 'Failed to load the revenue and cost overview'),
      color: 'red',
    });
  }, [overview.error, tAll]);

  if (!isPlatformAdmin) {
    return (
      <>
        <PageHeader title="Billing" breadcrumbs={BREADCRUMBS} />
        <Alert color="red" variant="light" title="Platform administrators only">
          This view shows platform-wide revenue, cost and prices. It is restricted to platform
          administrators, and the API answers 403 for anyone else.
        </Alert>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Billing & Pricing" breadcrumbs={BREADCRUMBS}>
        <SegmentedControl
          size="xs"
          data={WINDOW_OPTIONS}
          value={windowValue}
          onChange={setWindowValue}
          aria-label="Reporting window"
        />
        {overview.isValidating ? <Loader size="xs" /> : null}
      </PageHeader>

      <Stack gap="lg">
        {/*
          The caveats and the five headline figures stay above the area tabs deliberately: what the
          ledger does not know has to be read before the numbers it qualifies, not behind a click.
        */}
        {gapCount > 0 ? (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title="Some capabilities have no price">
            <Text size="sm">
              {gapCount} enabled platform capabilit{gapCount === 1 ? 'y has' : 'ies have'} a unit with no price, so that
              usage is charged nothing. The Pricing tab lists them.
            </Text>
          </Alert>
        ) : null}

        <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} title="What the ledger does and does not know about money in">
          <Stack gap={4}>
            <Text size="sm">
              <b>Collected</b> is what the billing provider actually charged in the window: after
              discounts, net of tax and refunds, shown per currency and never added across currencies.
              The <b>run rate</b> is the list price of paying subscriptions after each one&apos;s latest
              recorded discount. Internal grants and trials are never revenue.
            </Text>
            <Text size="sm" c="dimmed">
              The margin compares USD collected with COGS; payments in other currencies are not
              converted and are flagged instead.
            </Text>
          </Stack>
        </Alert>

        {report && (report.cost.unpricedPlatformEvents > 0 || report.cost.unattributedEvents > 0) ? (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title="Spend that the totals below do not fully contain">
            <Stack gap={4}>
              {report.cost.unpricedPlatformEvents > 0 ? (
                <Text size="sm">
                  {formatEventCount(report.cost.unpricedPlatformEvents)} platform usage row(s) had no price and
                  were charged <b>zero</b> by the fail-closed path. That spend is real and is
                  invisible in every cost total on this page.
                </Text>
              ) : null}
              {report.cost.unattributedEvents > 0 ? (
                <Text size="sm">
                  {formatEventCount(report.cost.unattributedEvents)} metered call(s) ran outside any usage
                  scope and were charged to the platform organization as <b>unattributed</b>.
                </Text>
              ) : null}
            </Stack>
          </Alert>
        ) : null}

        {report ? (
          <Text size="sm" c="dimmed">
            {report.revenue.activeProvider
              ? `Figures below count ${providerName(report.revenue.activeProvider)} only.`
              : 'Billing provider not configured: figures below count every provider.'}
          </Text>
        ) : null}

        {report ? (
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="md">
            {report.revenue.collected.length === 0 ? (
              <Tile
                label="Collected in window"
                value={formatUsd(0)}
                hint="No payments in this window"
                tooltip="What the provider actually charged after discounts, net of tax and refunds."
              />
            ) : (
              report.revenue.collected.map(collectedTile)
            )}
            <Tile
              label="Run rate after discounts / month"
              value={formatUsd(report.revenue.committedMonthlyUsd)}
              hint={`list price ${formatUsd(report.revenue.listPriceMonthlyUsd)} · ${report.revenue.discountedSubscriptions} discounted · ${report.revenue.fullyDiscountedSubscriptions} fully discounted · ${report.revenue.subscriptionsWithoutPayment} without payment · ${report.revenue.payingSubscriptions} paying · ${report.revenue.trialingSubscriptions} trialing · ${report.revenue.internalTrialOrganizations} internal trial`}
              tooltip="List price of paying subscriptions times the charged fraction of each one's latest payment. Subscriptions with no payment recorded count at list price."
            />
            <Tile
              label="Cost (COGS)"
              value={formatUsd(report.cost.totalUsd)}
              hint={`${formatEventCount(report.cost.events)} usage events, ${report.windowDays} days`}
              tooltip="usage_events.cost_usd: what the providers charged us, as priced at write time by each model capability's price."
            />
            <Tile
              label="Collected − COGS"
              value={marginUsd === null ? 'n/a' : formatUsd(marginUsd)}
              badge={marginFlagged ? 'USD ONLY' : undefined}
              hint={
                collectedNetUsd === null
                  ? 'No USD payments in this window'
                  : marginFlagged
                    ? `${report.revenue.otherCurrencyPayments} payment(s) in other currencies are not included`
                    : 'USD collected net minus COGS'
              }
              tooltip="USD net collected (after discounts, tax and refunds) minus COGS. Other currencies are never converted or summed."
            />
            <Tile
              label="Top-up credits granted"
              value={formatEventCount(report.revenue.topUpCreditsInWindow)}
              hint="Credits granted; the money paid is in the collected figure"
            />
          </SimpleGrid>
        ) : null}

        {report ? <ExcludedProviders excluded={report.revenue.excluded} /> : null}

        <Tabs defaultValue={OVERVIEW} keepMounted={false} variant="outline" radius="md">
          <Tabs.List>
            <Tabs.Tab value={OVERVIEW} leftSection={<IconChartBar size={16} />} style={{ minHeight: 44 }}>
              Overview
            </Tabs.Tab>
            <Tabs.Tab value={WAITING_ON_PROVIDER} leftSection={<IconHourglass size={16} />} style={{ minHeight: 44 }}>
              Waiting on {provider}
            </Tabs.Tab>
            <Tabs.Tab value={PRICING} leftSection={<IconCoin size={16} />} style={{ minHeight: 44 }}>
              {withCount('Pricing', gapCount)}
            </Tabs.Tab>
            <Tabs.Tab value={MULTIPLIERS} leftSection={<IconAdjustments size={16} />} style={{ minHeight: 44 }}>
              {withCount('Multipliers', allMultipliers.length)}
            </Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value={OVERVIEW} pt="md">
            {!report ? (
              <Center h={200}>
                {overview.isLoading ? (
                  <Loader />
                ) : (
                  <Text c="dimmed">Could not load the revenue and cost overview.</Text>
                )}
              </Center>
            ) : (
              <Stack gap="lg">
                <Stack gap="sm">
                  <Title order={4}>Margin per plan</Title>
                  <Text size="sm" c="dimmed">
                    Committed run rate after discounts against the cost that plan&apos;s organizations actually incurred in the
                    window. Usage from an organization with no subscription is listed separately rather than
                    folded into a plan.
                  </Text>
                  {report.plans.length === 0 ? (
                    <EmptyState
                      title="No subscriptions and no metered usage"
                      description="Nothing was metered in this window and no organization holds a subscription, so there is no margin to report."
                    />
                  ) : (
                    <Table.ScrollContainer minWidth={860}>
                      <Table withTableBorder highlightOnHover>
                        <Table.Thead>
                          <Table.Tr>
                            <Table.Th>Plan</Table.Th>
                            <Table.Th>Organizations</Table.Th>
                            <Table.Th>Committed / month (after discounts)</Table.Th>
                            <Table.Th>List price / month</Table.Th>
                            <Table.Th>Committed in window</Table.Th>
                            <Table.Th>Cost</Table.Th>
                            <Table.Th>Committed − COGS</Table.Th>
                            <Table.Th>Margin</Table.Th>
                          </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                          {report.plans.map((plan) => (
                            <Table.Tr key={plan.planKey}>
                              <Table.Td>{plan.planName}</Table.Td>
                              <Table.Td>{plan.organizations}</Table.Td>
                              <Table.Td>{formatUsd(plan.committedMonthlyUsd)}</Table.Td>
                              <Table.Td>{formatUsd(plan.listPriceMonthlyUsd)}</Table.Td>
                              <Table.Td>{formatUsd(plan.windowCommittedUsd)}</Table.Td>
                              <Table.Td>{formatUsd(plan.costUsd)}</Table.Td>
                              <Table.Td>{formatUsd(plan.marginUsd)}</Table.Td>
                              <Table.Td>
                                {plan.marginPercent === null ? (
                                  <Text span c="dimmed" size="sm">
                                    no committed revenue
                                  </Text>
                                ) : (
                                  formatPercent(plan.marginPercent)
                                )}
                              </Table.Td>
                            </Table.Tr>
                          ))}
                        </Table.Tbody>
                      </Table>
                    </Table.ScrollContainer>
                  )}
                </Stack>

                <Stack gap="sm">
                  <Title order={4}>Cost slices</Title>
                  <Tabs defaultValue="byProviderKind" keepMounted={false}>
                    <Tabs.List>
                      {SLICES.map((slice) => (
                        <Tabs.Tab key={slice.key} value={slice.key}>
                          {slice.label}
                        </Tabs.Tab>
                      ))}
                    </Tabs.List>
                    {SLICES.map((slice) => (
                      <Tabs.Panel key={slice.key} value={slice.key} pt="sm">
                        <BucketTable buckets={report.cost[slice.key]} keyHeader={slice.keyHeader} />
                      </Tabs.Panel>
                    ))}
                  </Tabs>
                </Stack>

                <Stack gap="sm">
                  <Title order={4}>Top organizations by cost</Title>
                  {report.cost.topOrganizations.length === 0 ? (
                    <Text size="sm" c="dimmed">
                      Nothing was metered in this window.
                    </Text>
                  ) : (
                    <>
                      <Table.ScrollContainer minWidth={640}>
                        <Table withTableBorder highlightOnHover>
                          <Table.Thead>
                            <Table.Tr>
                              <Table.Th>Organization</Table.Th>
                              <Table.Th>Cost (USD)</Table.Th>
                              <Table.Th>Credits</Table.Th>
                              <Table.Th>Events</Table.Th>
                            </Table.Tr>
                          </Table.Thead>
                          <Table.Tbody>
                            {report.cost.topOrganizations.map((org) => (
                              <Table.Tr key={org.organizationId}>
                                <Table.Td>{org.name}</Table.Td>
                                <Table.Td>{formatUsd(org.costUsd)}</Table.Td>
                                <Table.Td>{formatCreditsFromMillis(org.creditsMillis)}</Table.Td>
                                <Table.Td>{formatEventCount(org.events)}</Table.Td>
                              </Table.Tr>
                            ))}
                          </Table.Tbody>
                        </Table>
                      </Table.ScrollContainer>
                      {report.cost.topOrganizationsTruncated ? (
                        <Text size="xs" c="dimmed">
                          The list is capped; smaller organizations are not shown.
                        </Text>
                      ) : null}
                    </>
                  )}
                </Stack>
              </Stack>
            )}
          </Tabs.Panel>

          {/* Mounted only while selected (keepMounted={false}), so its list is fetched on demand. */}
          <Tabs.Panel value={WAITING_ON_PROVIDER} pt="md">
            <ReconcilePanel />
          </Tabs.Panel>

          <Tabs.Panel value={PRICING} pt="md">
            <Stack gap="md">
              <BillingSectionHeading
                title="Pricing"
                description="What a unit of platform usage is charged. Each price is set on its model's capability, on the provider's page; the markup below turns a cost into credits for every price that does not set its own."
              />
              {settings.data ? (
                <PricingSettingsPanel
                  key={settings.data.updatedAt}
                  settings={settings.data}
                  onSaved={() => void settings.mutate()}
                />
              ) : settings.isLoading ? (
                <Center h={80}>
                  <Loader />
                </Center>
              ) : (
                <Text size="sm" c="dimmed">Could not load the pricing settings.</Text>
              )}
              <PriceCoveragePanel gaps={coverage.data} />
              <ConsolidationReportPanel report={consolidation.data} />
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value={MULTIPLIERS} pt="md">
            <Stack gap="sm">
              <BillingSectionHeading
                title="Rate multipliers"
                description="Scales the credits a priced event costs by billing mode. A mode's row with no usage kind is its default; a kind-specific row beats it."
                action={
                  <Button
                    variant="default"
                    leftSection={<IconPlus size={16} />}
                    onClick={() => setMultiplierFormOpened(true)}
                  >
                    New multiplier
                  </Button>
                }
              />

              {multipliers.isLoading ? (
                <Center h={120}>
                  <Loader />
                </Center>
              ) : allMultipliers.length === 0 ? (
                <EmptyState
                  title="No rate multipliers"
                  description="Without a Platform multiplier of 1, priced events are scaled by the fallback rather than by an explicit row."
                />
              ) : (
                <RateMultiplierTable multipliers={allMultipliers} now={now} />
              )}
            </Stack>
          </Tabs.Panel>
        </Tabs>
      </Stack>

      <RateMultiplierForm
        opened={multiplierFormOpened}
        onClose={() => setMultiplierFormOpened(false)}
        onSuccess={() => multipliers.mutate()}
      />
    </>
  );
}
