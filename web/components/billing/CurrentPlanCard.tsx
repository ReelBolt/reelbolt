'use client';

import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  Divider,
  Group,
  SimpleGrid,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import { IconCalendarEvent, IconExternalLink, IconInfoCircle } from '@tabler/icons-react';
import type { BillingPlan, BillingSummary } from '@/lib/types/billing';
import { useT } from '@/i18n';
import { formatCredits } from '@/lib/utils/usage-labels';
import { BillingConfirmModal } from './BillingConfirmModal';
import { CreditGrantsList } from './CreditGrantsList';
import { SeatStepper } from './SeatStepper';
import { providerTestIdSuffix } from './subscription';
import { subscriptionStatusLabel, useLongDate, useMerchant } from './useBillingFormat';

/** Which portal page a button opens: the overview, or one of the subscription's deep links. */
export type PortalTarget = 'overview' | 'payment-method' | 'cancel';

/**
 * The current-plan card: plan, credits, renewal, seats, the scheduled cancel / plan-change banners,
 * and — for an Owner or Billing member on a live paid plan — cancel, seats and the provider's links.
 * Every action is the parent's: this card only asks, and confirms first where money or access moves.
 */
export function CurrentPlanCard({
  summary,
  currentPerSeat,
  livePaidPlan,
  canManage,
  pending,
  onOpenPortal,
  onCancel,
  onResume,
  onKeepCurrent,
  onChangeSeats,
}: {
  summary: BillingSummary;
  currentPerSeat: BillingPlan | null;
  /** The workspace is on a live paid plan (see `hasLivePaidPlan`). */
  livePaidPlan: boolean;
  canManage: boolean;
  /** The in-flight action's key, or null. Every action button is disabled while one runs. */
  pending: string | null;
  onOpenPortal: (target: PortalTarget) => void;
  onCancel: () => void;
  onResume: () => void;
  onKeepCurrent: () => void;
  onChangeSeats: (seats: number) => void;
}) {
  const { subscription, credits } = summary;
  const t = useT('billing.currentPlan');
  const root = useT();
  const { provider: merchant, providerStart } = useMerchant(summary);
  const longDate = useLongDate();
  const suffix = providerTestIdSuffix(summary);
  const minSeats = currentPerSeat?.minSeats ?? 1;
  const [seats, setSeats] = useState<number>(subscription?.seats ?? minSeats);
  const [confirming, setConfirming] = useState<'cancel' | 'fewer-seats' | null>(null);
  const busy = pending !== null;

  const creditsRenewAt = subscription?.creditsRenewAt ?? summary.creditsRenewAt ?? null;
  const cancelScheduled = subscription?.cancelAtPeriodEnd === true;
  const scheduled = subscription?.scheduledChange ?? null;
  const periodEndText = subscription?.periodEnd ? longDate(subscription.periodEnd) : null;
  const cancelDate = subscription?.cancelEffectiveAt ?? subscription?.periodEnd ?? null;
  const actions = canManage && livePaidPlan;

  return (
    <Card withBorder radius="lg" padding="lg" data-testid="current-plan-card">
      <Stack gap="md">
        <Group justify="space-between" gap="sm" wrap="wrap">
          <div>
            <Text size="sm" c="dimmed">{t('title')}</Text>
            <Group gap="xs" align="baseline">
              <Text fw={700} size="xl">{subscription?.planName ?? t('noPlan')}</Text>
              {subscription && <Badge variant="light">{subscriptionStatusLabel(root, subscription.status)}</Badge>}
            </Group>
          </div>

          {canManage && (
            <Tooltip
              label={t('manageUnavailable', { provider: merchant })}
              disabled={Boolean(subscription?.hasBillingCustomer)}
              events={{ hover: true, focus: true, touch: true }}
            >
              <span tabIndex={subscription?.hasBillingCustomer ? undefined : 0}>
                <Button
                  variant="default"
                  rightSection={<IconExternalLink size={16} />}
                  disabled={busy || !subscription?.hasBillingCustomer}
                  loading={pending === 'portal:overview'}
                  onClick={() => onOpenPortal('overview')}
                  data-testid={`manage-in-${suffix}`}
                >
                  {t('manageIn', { provider: merchant })}
                </Button>
              </span>
            </Tooltip>
          )}
        </Group>

        <SimpleGrid cols={{ base: 1, sm: 3 }} spacing="md">
          <Fact
            label={t('creditsAvailable')}
            value={formatCredits(credits.available)}
            hint={t('creditsHint', { balance: formatCredits(credits.balance), reserved: formatCredits(credits.reserved) })}
          />
          <Fact
            label={cancelScheduled ? t('accessEnds') : t('renews')}
            value={periodEndText ?? '—'}
            hint={
              (subscription?.periodEnd
                ? t('periodStarted', { date: subscription.periodStart ? longDate(subscription.periodStart) : '—' })
                : t('noPeriod')) +
              (creditsRenewAt && subscription?.interval === 'annual' ? ` ${t('creditsRenew', { date: longDate(creditsRenewAt) })}` : '')
            }
          />
          <Fact
            label={t('seats')}
            value={subscription ? String(subscription.seats) : '—'}
            hint={currentPerSeat ? t('seatsScale') : t('notPerSeat')}
          />
        </SimpleGrid>

        {credits.grants !== undefined && (
          <>
            <Divider />
            <CreditGrantsList grants={credits.grants} />
          </>
        )}

        {cancelScheduled && (
          <Alert
            icon={<IconInfoCircle size={16} />}
            color="yellow"
            title={cancelDate ? t('cancelsOn', { date: longDate(cancelDate) }) : t('cancelsAtEnd')}
            data-testid="cancel-banner"
          >
            <Stack gap="xs">
              <Text size="sm">
                {t('cancelBannerBody')}
              </Text>
              {actions && (
                <Group>
                  <Button
                    size="xs"
                    variant="white"
                    color="yellow"
                    disabled={busy}
                    loading={pending === 'resume'}
                    onClick={onResume}
                    data-testid="cancel-banner-keep"
                  >
                    {t('keepMyPlan')}
                  </Button>
                </Group>
              )}
            </Stack>
          </Alert>
        )}

        {scheduled && (
          <Alert
            icon={<IconCalendarEvent size={16} />}
            color="blue"
            title={t('movesTo', { plan: scheduled.planName, date: longDate(scheduled.effectiveAt) })}
            data-testid="scheduled-change-banner"
          >
            <Stack gap="xs">
              <Text size="sm">
                {scheduled.planKey === subscription?.planKey
                  ? t('scheduledSeats', { current: subscription.seats, next: scheduled.seats })
                  : subscription?.planName
                    ? t('scheduledKeepPlan', { plan: subscription.planName })
                    : t('scheduledKeepYourPlan')}
              </Text>
              {actions && subscription && (
                <Group>
                  <Button
                    size="xs"
                    variant="white"
                    disabled={busy}
                    loading={pending === 'keep'}
                    onClick={onKeepCurrent}
                    data-testid="scheduled-change-keep"
                  >
                    {t('keepPlan', { plan: subscription.planName })}
                  </Button>
                </Group>
              )}
            </Stack>
          </Alert>
        )}

        {subscription && !subscription.hasBillingCustomer && (
          <Text size="sm" c="dimmed">
            {t('notPurchased')}
          </Text>
        )}

        {actions && subscription && currentPerSeat && !cancelScheduled && !scheduled && (
          <>
            <Divider />
            <Group align="flex-end" gap="sm" wrap="wrap">
              <SeatStepper
                label={t('seats')}
                description={t('seatsMin', { min: minSeats })}
                value={seats}
                onChange={setSeats}
                min={minSeats}
              />
              <Button
                variant="light"
                disabled={busy || seats < minSeats || seats === subscription.seats}
                loading={pending === 'seats'}
                onClick={() => {
                  if (seats < subscription.seats) setConfirming('fewer-seats');
                  else onChangeSeats(seats);
                }}
                data-testid="update-seats"
              >
                {t('updateSeats')}
              </Button>
              <Text size="xs" c="dimmed" maw={340} style={{ fontSize: 12 }}>
                {t('seatsNote', { provider: providerStart })}
              </Text>
            </Group>
          </>
        )}

        {actions && (
          <>
            <Divider />
            <Group gap="sm" wrap="wrap">
              {!cancelScheduled && (
                <Button
                  variant="light"
                  color="red"
                  disabled={busy}
                  loading={pending === 'cancel'}
                  onClick={() => setConfirming('cancel')}
                  data-testid="cancel-plan"
                >
                  {t('cancelPlan')}
                </Button>
              )}
              {subscription?.hasBillingCustomer && (
                <>
                  <Button
                    variant="subtle"
                    rightSection={<IconExternalLink size={14} />}
                    disabled={busy}
                    loading={pending === 'portal:payment-method'}
                    onClick={() => onOpenPortal('payment-method')}
                    data-testid="update-payment-method"
                  >
                    {t('updatePayment')}
                  </Button>
                  <Button
                    variant="subtle"
                    color="gray"
                    rightSection={<IconExternalLink size={14} />}
                    disabled={busy}
                    loading={pending === 'portal:cancel'}
                    onClick={() => onOpenPortal('cancel')}
                    data-testid={`cancel-in-${suffix}`}
                  >
                    {t('cancelIn', { provider: merchant })}
                  </Button>
                  <Text size="xs" c="dimmed" maw={340} data-testid="portal-cancel-note">
                    {t('portalCancelNote', { provider: merchant })}
                  </Text>
                </>
              )}
            </Group>
          </>
        )}
      </Stack>

      <BillingConfirmModal
        opened={confirming === 'cancel'}
        title={t('cancelModal.title')}
        confirmLabel={t('cancelModal.confirm')}
        confirmColor="red"
        dismissLabel={t('keepMyPlan')}
        testId="cancel-plan-modal"
        onClose={() => setConfirming(null)}
        onConfirm={() => {
          setConfirming(null);
          onCancel();
        }}
      >
        {periodEndText ? t('cancelModal.untilDate', { date: periodEndText }) : t('cancelModal.untilEnd')}{' '}
        {t('cancelModal.after')}
      </BillingConfirmModal>

      <BillingConfirmModal
        opened={confirming === 'fewer-seats'}
        title={periodEndText ? t('fewerSeats.titleDate', { date: periodEndText }) : t('fewerSeats.titleNext')}
        confirmLabel={t('fewerSeats.confirm')}
        dismissLabel={t('fewerSeats.back')}
        testId="fewer-seats-modal"
        onClose={() => setConfirming(null)}
        onConfirm={() => {
          setConfirming(null);
          onChangeSeats(seats);
        }}
      >
        {t('fewerSeats.body', { current: subscription?.seats ?? seats, next: seats })}
      </BillingConfirmModal>
    </Card>
  );
}

function Fact({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div>
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{label}</Text>
      <Text fw={600} style={{ fontVariantNumeric: 'tabular-nums' }}>{value}</Text>
      <Text size="xs" c="dimmed">{hint}</Text>
    </div>
  );
}
