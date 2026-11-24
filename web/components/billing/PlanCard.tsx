'use client';

import { useState } from 'react';
import { Badge, Button, Card, Group, List, Stack, Text } from '@mantine/core';
import type { ActivePromotion } from '@/lib/api/promotions';
import { useCheckoutPreview } from '@/lib/hooks/use-promotions';
import type { BillingInterval, BillingPlan, BillingSubscriptionSummary } from '@/lib/types/billing';
import { useFormat, useT } from '@/i18n';
import { formatCredits } from '@/lib/utils/usage-labels';
import { BillingConfirmModal } from './BillingConfirmModal';
import { DiscountedPrice } from './DiscountedPrice';
import { SeatStepper } from './SeatStepper';
import { planFeatureLines } from './plan-features';
import { promoEndsMsg, promoEndsWith } from './promo-discount';
import { hasPriceDiscount, planPreviewRequest } from './promo-preview';
import { annualPerMonth, isAnnualSubscriber, monthsFree, sellsAnnual } from './plan-interval';
import { planDirection, sentenceStart } from './subscription';
import { useLongDate } from './useBillingFormat';

/**
 * One plan on sale. What it offers depends on the workspace:
 * - no live paid plan (Trial, canceled): "Choose {Plan}" starts a checkout;
 * - a live paid plan: "Upgrade to {Plan}" / "Switch to {Plan}", confirmed first, through change-plan;
 * - a cancellation or a plan/seat change scheduled: nothing until the current plan is kept (the
 *   server refuses any other change while a downgrade is held: `downgrade_scheduled`);
 * - read-only member: a sentence saying who can act.
 * The seat input appears only where its value is sent somewhere (F-06).
 */
export function PlanCard({
  plan,
  picked = false,
  canManage,
  subscription,
  currentPlan,
  livePaidPlan,
  pending,
  formattedTotal,
  merchant,
  beforeTax,
  promoCode,
  offer,
  interval = 'monthly',
  onCheckout,
  onChangePlan,
}: {
  plan: BillingPlan;
  /** The plan the person picked on the pricing page before signing up: marked, nothing else changes. */
  picked?: boolean;
  canManage: boolean;
  subscription: BillingSubscriptionSummary | null;
  /** The plan the workspace is on, when it is one of the plans on sale. */
  currentPlan: BillingPlan | null;
  livePaidPlan: boolean;
  pending: string | null;
  /** Paddle's own formatted total for this plan, or null while it is unknown/unavailable. */
  formattedTotal: string | null;
  /** The provider's display name (or a neutral phrase) for the copy. */
  merchant: string;
  /** The provider's previews can omit tax (non-Paddle), so the discounted quote says "before tax". */
  beforeTax: boolean;
  /** The applied promo code, when it covers this plan. */
  promoCode: string | null;
  /** The automatic offer running on this plan, if any. */
  offer: ActivePromotion | null;
  /** The interval this card is for (the switch, or yearly for a yearly subscriber). */
  interval?: BillingInterval;
  onCheckout: (seats: number | undefined) => void;
  onChangePlan: (seats: number | undefined) => void;
}) {
  const t = useT('plans.card');
  const tr = useT();
  const f = useFormat();
  const longDate = useLongDate();
  const minSeats = plan.minSeats ?? 1;
  const [seats, setSeats] = useState<number>(minSeats);
  const [confirming, setConfirming] = useState(false);
  const perSeat = plan.kind === 'PerSeat';
  const busy = pending !== null;

  const cancelScheduled = subscription?.cancelAtPeriodEnd === true;
  const changeScheduled = subscription?.scheduledChange != null;
  const annual = interval === 'annual';
  const annualPlan = sellsAnnual(plan);
  // A yearly view of a plan with no yearly price offers nothing; a monthly workspace looking at the
  // yearly view of its own plan can start the yearly plan.
  const yearlyUnavailable = annual && !annualPlan;
  const startingYearly = annual && annualPlan && livePaidPlan && !isAnnualSubscriber(subscription);
  const canChange =
    canManage && livePaidPlan && (!plan.current || startingYearly) && !cancelScheduled && !changeScheduled &&
    currentPlan !== null && !yearlyUnavailable;
  const canCheckout = canManage && !livePaidPlan && !plan.current && !yearlyUnavailable;
  const unitPrice = annual && annualPlan ? (annualPerMonth(plan) ?? plan.priceMonthlyUsd) : plan.priceMonthlyUsd;
  const freeMonths = annual ? monthsFree(plan) : null;
  const yearlyTotal = annual && annualPlan && plan.priceAnnualUsd != null
    ? (formattedTotal ?? f.currency(plan.priceAnnualUsd, 'USD'))
    : null;
  const showSeats = perSeat && (canCheckout || canChange);
  // The seat count the total is for: the stepper's value where there is a stepper, the workspace's
  // own seats on the plan it is on, and no total at all where neither applies.
  const totalSeats = showSeats ? seats : plan.current && subscription ? subscription.seats : null;
  const direction = canChange && currentPlan && subscription
    ? startingYearly ? 'upgrade' : planDirection(currentPlan, subscription.seats, plan, perSeat ? seats : 1)
    : null;
  const periodEndText = subscription?.periodEnd ? longDate(subscription.periodEnd) : null;
  // A recurring promo the move ends: said in the confirm step, before anything is sent.
  const promo = subscription?.promoDiscount ?? null;
  const promoLine = canChange && promo && promoEndsWith(plan, promo)
    ? tr.msg(promoEndsMsg(promo, direction === 'downgrade', subscription?.planName ?? currentPlan?.name ?? t('yourPlan')))
    : null;
  // The sentence before an upgrade names the plan's own price and credits (for the seats chosen) and the provider's difference charge.
  const upgradeSeats = perSeat ? seats : 1;
  const upgradeArgs = {
    plan: plan.name,
    price: f.currency(plan.priceMonthlyUsd * upgradeSeats, 'USD'),
    yearly: f.currency((plan.priceAnnualUsd ?? 0) * upgradeSeats, 'USD'),
    credits: formatCredits(plan.includedCredits * upgradeSeats),
    provider: sentenceStart(merchant),
    date: periodEndText ?? '',
  };
  const changeLabel = startingYearly ? t('startYearly', { plan: plan.name }) : direction === 'downgrade' ? t('switchTo', { plan: plan.name }) : t('upgradeTo', { plan: plan.name });

  // The discounted quote, asked for only where a checkout is offered and something applies. A plan
  // change (live paid plan) takes no code, so it never shows one.
  const preview = useCheckoutPreview(
    canCheckout && (promoCode !== null || offer !== null)
      ? planPreviewRequest(plan, perSeat ? seats : undefined, promoCode, interval)
      : null,
  );
  const discounted = hasPriceDiscount(preview.data) ? preview.data : null;
  const badge = discounted?.promotion?.headline || (!livePaidPlan && !plan.current ? offer?.headline : null) || null;

  return (
    <Card
      withBorder
      radius="lg"
      padding="lg"
      data-testid={`plan-${plan.key}`}
      data-picked={picked && !plan.current ? 'true' : undefined}
      style={picked && !plan.current ? { borderColor: 'var(--mantine-primary-color-filled)', borderWidth: 2 } : undefined}
    >
      <Stack gap="sm" h="100%">
        <Group justify="space-between" gap="xs">
          <Text fw={600}>{plan.name}</Text>
          {plan.current && <Badge color="green" variant="light">{t('current')}</Badge>}
          {picked && !plan.current && <Badge variant="light" data-testid="picked-badge">{t('picked')}</Badge>}
          {badge && <Badge color="violet" variant="light" data-testid="promo-badge">{badge}</Badge>}
        </Group>

        <div>
          {discounted ? (
            // The payment provider's quote with the discount applied, printed as it came back.
            <DiscountedPrice
              preview={discounted}
              note={perSeat
                ? t('discountNoteSeats', { seats, tax: beforeTax ? 'before' : 'incl' })
                : t('discountNote', { tax: beforeTax ? 'before' : 'incl' })}
            />
          ) : yearlyTotal ? (
            // Yearly: the per-month equivalent of the yearly list price, then the yearly total (the
            // provider's own string when it gave one) and, only when it is exact, the months saved.
            <>
              <Text size="xl" fw={700} data-testid={`plan-${plan.key}-price`}>{f.currency(unitPrice, 'USD')}</Text>
              <Text size="xs" c="dimmed">{t('annualPerMonthNote', { perSeat: perSeat ? 'yes' : 'no' })}</Text>
              <Text size="sm" data-testid={`plan-${plan.key}-yearly-total`}>
                {t('annualTotal', { total: yearlyTotal, perSeat: perSeat ? 'yes' : 'no' })}
                {freeMonths !== null && ` ${t('monthsFree', { months: freeMonths })}`}
              </Text>
            </>
          ) : yearlyUnavailable ? (
            <Text size="sm" c="dimmed">{t('noYearly')}</Text>
          ) : formattedTotal ? (
            // Paddle's string, printed exactly as it came back: localized, formatted and priced for
            // the buyer's country. Nothing here parses, converts or re-formats it.
            <>
              <Text size="xl" fw={700}>{formattedTotal}</Text>
              <Text size="xs" c="dimmed">
                {t('priceNote', { perSeat: perSeat ? 'yes' : 'no' })}
              </Text>
            </>
          ) : (
            <>
              <Text size="xl" fw={700}>{f.currency(plan.priceMonthlyUsd, 'USD')}</Text>
              <Text size="xs" c="dimmed">
                {t('listNote', { perSeat: perSeat ? 'yes' : 'no' })}
              </Text>
            </>
          )}
        </div>

        <List size="sm" spacing={4} data-testid={`plan-${plan.key}-features`} aria-label={t('featuresAria', { plan: plan.name })}>
          <List.Item>
            {perSeat
              ? t('creditsPerSeat', { credits: formatCredits(plan.includedCredits) })
              : t('credits', { credits: formatCredits(plan.includedCredits) })}
          </List.Item>
          {annual && annualPlan && (
            <List.Item>{t('creditsYearly')}</List.Item>
          )}
          {perSeat && (
            <List.Item>{t('seatsMinAddMore', { min: minSeats })}</List.Item>
          )}
          {planFeatureLines(plan).map((line) => (
            <List.Item key={line.key}>{t(line.key, line.values)}</List.Item>
          ))}
          {(plan.highlights ?? []).map((line) => (
            <List.Item key={line}>{line}</List.Item>
          ))}
        </List>

        {showSeats && (
          <SeatStepper
            label={t('seats')}
            description={t('seatsMin', { min: minSeats })}
            value={seats}
            onChange={setSeats}
            min={minSeats}
            planName={plan.name}
          />
        )}

        {perSeat && !discounted && !yearlyUnavailable && totalSeats !== null && (
          <Text size="sm" fw={600} data-testid={`plan-${plan.key}-seat-total`}>
            {t('seatTotal', { seats: totalSeats, total: f.currency(unitPrice * totalSeats, 'USD') })}
          </Text>
        )}

        <div style={{ marginTop: 'auto' }}>
          {plan.current && !canChange ? (
            <Text size="xs" c="dimmed">
              {t('isCurrent')}
            </Text>
          ) : !canManage ? (
            // No disabled button: it could never be enabled by the person looking at it.
            <Text size="xs" c="dimmed">
              {t('needsOwner')}
            </Text>
          ) : yearlyUnavailable ? (
            <Text size="xs" c="dimmed">{t('noYearly')}</Text>
          ) : livePaidPlan && cancelScheduled ? (
            <Text size="xs" c="dimmed">
              {t('keepFirst')}
            </Text>
          ) : livePaidPlan && changeScheduled ? (
            <Text size="xs" c="dimmed" data-testid={`plan-${plan.key}-keep-first`}>
              {subscription?.planName ? t('keepPlanFirst', { plan: subscription.planName }) : t('keepCurrentFirst')}
            </Text>
          ) : livePaidPlan && !currentPlan ? (
            <Text size="xs" c="dimmed">
              {t('arranged')}
            </Text>
          ) : canChange ? (
            <Button
              variant={direction === 'downgrade' ? 'light' : 'filled'}
              disabled={busy || (perSeat && seats < minSeats)}
              loading={pending === `change:${plan.key}`}
              onClick={() => setConfirming(true)}
              fullWidth
              data-testid={`plan-${plan.key}-change`}
            >
              {changeLabel}
            </Button>
          ) : (
            <Button
              variant="filled"
              disabled={busy}
              loading={pending === `plan:${plan.key}`}
              onClick={() => onCheckout(perSeat ? seats : undefined)}
              fullWidth
              data-testid={`plan-${plan.key}-choose`}
            >
              {t('choose', { plan: plan.name })}
            </Button>
          )}
        </div>
      </Stack>

      {canChange && (
        <BillingConfirmModal
          opened={confirming}
          title={startingYearly ? t('startYearlyTitle', { plan: plan.name }) : direction === 'downgrade' ? t('switchModalTitle', { plan: plan.name }) : t('upgradeModalTitle', { plan: plan.name })}
          confirmLabel={t('confirm')}
          dismissLabel={t('back')}
          testId="change-plan-modal"
          onClose={() => setConfirming(false)}
          onConfirm={() => {
            setConfirming(false);
            onChangePlan(perSeat ? seats : undefined);
          }}
        >
          {startingYearly
            ? t('startYearlyBody', { plan: plan.name, yearly: upgradeArgs.yearly, provider: upgradeArgs.provider })
            : direction === 'downgrade'
            ? periodEndText
              ? t('downgradeUntilDate', { current: subscription?.planName ?? t('yourPlan'), date: periodEndText, plan: plan.name })
              : t('downgradeUntilEnd', { current: subscription?.planName ?? t('yourPlan'), plan: plan.name })
            : periodEndText
              ? t('upgradeUntilDate', upgradeArgs)
              : t('upgradeUntilEnd', upgradeArgs)}
          {promoLine && (
            // A span, not a paragraph: the modal already wraps its body in one <p>.
            <Text component="span" size="sm" display="block" mt="xs" data-testid="change-plan-promo-ends">
              {promoLine}
            </Text>
          )}
        </BillingConfirmModal>
      )}
    </Card>
  );
}
