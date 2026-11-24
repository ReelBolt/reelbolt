'use client';

import { useEffect, useRef, useState } from 'react';
import { useSWRConfig } from 'swr';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Loader,
  SimpleGrid,
  Stack,
  Text,
} from '@mantine/core';
import { IconAlertCircle, IconCheck, IconClockHour4, IconInfoCircle } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { PageHeader } from '@/components/shared/PageHeader';
import { StorageUsageBar } from '@/components/storage/StorageUsageBar';
import { CurrentPlanCard, type PortalTarget } from '@/components/billing/CurrentPlanCard';
import { FirstVideoGrantCard } from '@/components/billing/FirstVideoGrantCard';
import { PlanCard } from '@/components/billing/PlanCard';
import { PlanIntervalSwitch } from '@/components/billing/PlanIntervalSwitch';
import { effectiveInterval, isAnnualSubscriber, sellsAnnual } from '@/components/billing/plan-interval';
import { saveCheckoutSnapshot, takeSnapshot, type CheckoutTarget } from '@/components/billing/checkout-snapshot';
import { PromoCodeField, type ApplyOutcome } from '@/components/billing/PromoCodeField';
import { useLongDate, useMerchant } from '@/components/billing/useBillingFormat';
import {
  cancelConfirmed,
  CHECKOUT_START_PARAM,
  CHECKOUT_START_TOKEN_KEY,
  hasLivePaidPlan,
  planChangeConfirmed,
  pollSummary,
  isNonPaddleProvider,
  pickedPlanCheckoutHref,
  resumeConfirmed,
  type SummaryCheck,
} from '@/components/billing/subscription';
import {
  billingActionMessage,
  cancelSubscription,
  changeBillingSeats,
  changeSubscriptionPlan,
  isBillingUnavailable,
  isSafeRedirectUrl,
  openBillingPortal,
  resumeSubscription,
  startPlanCheckout,
  startTopUpCheckout,
} from '@/lib/api/billing';
import { ApiError } from '@/lib/api/client';
import { sanitizePlan } from '@/lib/auth/signup-carry';
import { clearSignupIntent } from '@/lib/auth/signup-intent';
import { useAuth } from '@/lib/hooks/use-auth';
import { UnverifiedEmailBanner } from '@/components/auth/UnverifiedEmailBanner';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { useStorageUsage } from '@/lib/hooks/use-storage';
import { usePaddlePrices } from '@/lib/hooks/use-paddle-prices';
import { checkoutSuccessUrl, paddleClientOf, priceIdFor } from '@/lib/paddle/pricing';
import { loadPaddle, openOverlayCheckout } from '@/lib/paddle/sdk';
import type { CheckoutSession } from '@/lib/api/billing';
import { previewAmounts } from '@/lib/utils/billing';
import {
  PROMO_CODE_PATTERN,
  normalizePromoCode,
  previewCheckout,
  promoCodeMessage,
  promotionCoversPack,
  promotionCoversPlan,
  type ActivePromotion,
} from '@/lib/api/promotions';
import { checkoutPreviewKey, useActivePromotions, useCheckoutPreview } from '@/lib/hooks/use-promotions';
import { hasPriceDiscount, packPreviewRequest, planPreviewRequest, type PreviewTarget } from '@/components/billing/promo-preview';
import type { BillingInterval, BillingSummary, PlanChangeResult } from '@/lib/types/billing';
import { formatCredits } from '@/lib/utils/usage-labels';

/**
 * The page's one status line. `waiting`: a change was accepted and Paddle's webhook has not landed
 * yet; `done`: the summary shows it (or a checkout/portal is opening); `timeout`: no confirmation
 * within the poll window — never reported as success.
 */
type Notice = { tone: 'waiting' | 'done' | 'timeout'; text: string };

export default function BillingPage() {
  const t = useT('billing.page');
  const tAll = useT();
  const longDate = useLongDate();
  const summary = useBillingSummary();
  // The plan's storage limit and what is used are on the summary; the project breakdown is not, and
  // comes from `/storage/usage` (any member may read it, even with billing off).
  const storage = useStorageUsage();

  // The plan someone picked on the pricing page before they had an account (`?plan=<key>`, written
  // by the dashboard's notice): its card is marked. Read after mount, so the server markup matches.
  // Arriving here is the offer made, so the remembered choice is dropped.
  const [pickedPlan, setPickedPlan] = useState<string | null>(null);
  const [wantsCheckout, setWantsCheckout] = useState(false);
  useEffect(() => {
    const picked = sanitizePlan(new URLSearchParams(window.location.search).get('plan') ?? undefined);
    if (!picked) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- read from the address after mount to match the server markup
    setPickedPlan(picked);
    clearSignupIntent('plan');
    // `start=1` (from the dashboard, right after a first sign-in) asks for the checkout of that
    // plan to open now. It is taken off the address at once so a reload never opens it again.
    const params = new URLSearchParams(window.location.search);
    if (params.get(CHECKOUT_START_PARAM) === '1') {
      // Only an arrival the dashboard sent (it leaves the plan in sessionStorage, used once): a
      // link made elsewhere with `start=1` just marks the card.
      let mine = false;
      try {
        mine = window.sessionStorage.getItem(CHECKOUT_START_TOKEN_KEY) === picked;
        window.sessionStorage.removeItem(CHECKOUT_START_TOKEN_KEY);
      } catch {
        mine = false;
      }
      if (mine) setWantsCheckout(true);
      params.delete(CHECKOUT_START_PARAM);
      const query = params.toString();
      window.history.replaceState(null, '', `${window.location.pathname}${query ? `?${query}` : ''}${window.location.hash}`);
    }
  }, []);

  const [pending, setPending] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  // The checkout refused an unverified address (403 EMAIL_NOT_VERIFIED): show the verify step, not a retry.
  const [needsVerify, setNeedsVerify] = useState(false);
  // Stops a poll that is still running when the page is left.
  const unmounted = useRef(false);
  useEffect(() => {
    unmounted.current = false;
    return () => {
      unmounted.current = true;
    };
  }, []);

  // The embedded checkout's two inputs, both served by the summary itself: the Paddle.js settings,
  // and Paddle's own prices for the plans on sale. Every hook runs before the early returns below,
  // and both are inert when this deployment has no Paddle.js checkout.
  const { user } = useAuth();
  const paddleConfig = paddleClientOf(summary.data);
  const [pickedInterval, setPickedInterval] = useState<BillingInterval | null>(null);
  const interval = effectiveInterval(
    pickedInterval,
    summary.data && hasLivePaidPlan(summary.data) ? summary.data.subscription : null,
  );
  const prices = usePaddlePrices(paddleConfig, summary.data?.plans ?? [], interval);
  // The provider named in every sentence below: the summary's own display name, else a neutral phrase.
  const { provider, providerStart } = useMerchant(summary.data);
  const header = (
    <PageHeader
      title={t('title')}
      breadcrumbs={[{ label: t('breadcrumbDashboard'), href: '/dashboard' }, { label: t('title') }]}
    />
  );

  // Automatic offers running now (no code needed), and the promo code the buyer applied with the
  // targets it turned out to be valid for. A checkout carries the code only for those targets.
  const activePromotions = useActivePromotions(summary.data !== undefined);
  const { mutate: primeCache } = useSWRConfig();
  const [appliedCode, setAppliedCode] = useState<{ code: string; targets: PreviewTarget[] } | null>(null);

  /**
   * Creates the transaction on the server, then opens it: as Paddle's embedded one-page overlay when
   * this deployment has a Paddle.js checkout, and on Paddle's hosted page for the same transaction
   * when it does not (no publishable token configured, or Paddle.js could not be loaded).
   *
   * There is one checkout path here, not two. The transaction — with the organization taken from the
   * session, which is what the webhook reads to decide whose credits a payment buys — is created by
   * `POST /api/v1/billing/checkout` in both cases.
   */
  async function beginCheckout(start: () => Promise<CheckoutSession>, target: CheckoutTarget): Promise<string> {
    const session = await start();
    // The baseline the checkout return page compares against, saved only once a checkout exists.
    if (data) saveCheckoutSnapshot(takeSnapshot(data, target));
    const paddle = paddleConfig ? await loadPaddle(paddleConfig) : null;
    if (!paddle) {
      follow(session.checkoutUrl);
      return t('openingCheckout');
    }

    openOverlayCheckout({
      paddle,
      transactionId: session.transactionId,
      successUrl: checkoutSuccessUrl(window.location.origin),
      email: user?.email,
      ...(session.discount ? { discounted: true } : {}),
    });
    return t('openingCheckout');
  }

  /**
   * Sends the browser to a link the payment provider built. The URL is still untrusted input, so
   * a scheme this page will not follow is refused (through the same error path as a failed
   * request) rather than navigated to.
   */
  function follow(url: string): void {
    if (!isSafeRedirectUrl(url)) {
      throw new Error(t('unsafeLink'));
    }
    window.location.assign(url);
  }

  /**
   * Runs one billing action, keeping a single in-flight key (every action button is disabled while
   * it is set, polling included, so nothing is submitted twice) and one error slot.
   */
  async function run(key: string, action: () => Promise<string | Notice>) {
    setActionError(null);
    setNeedsVerify(false);
    setNotice(null);
    setPending(key);
    try {
      const outcome = await action();
      if (!unmounted.current) setNotice(typeof outcome === 'string' ? { tone: 'done', text: outcome } : outcome);
    } catch (error) {
      if (!unmounted.current) {
        if (error instanceof ApiError && error.status === 403 && error.code === 'EMAIL_NOT_VERIFIED') setNeedsVerify(true);
        else setActionError(billingActionMessage(error));
      }
    } finally {
      if (!unmounted.current) setPending(null);
    }
  }

  /**
   * After a 2xx from cancel / resume / change plan / seats: the provider applies the change and tells us
   * by webhook, so the page polls the summary until it shows the change, and only then says it is
   * done — with the outcome read from the confirmed summary.
   */
  async function awaitProvider(isConfirmed: SummaryCheck, describe: (fresh: BillingSummary) => string): Promise<Notice> {
    setNotice({ tone: 'waiting', text: t('waiting', { provider }) });
    const fresh = await pollSummary(() => summary.mutate(), isConfirmed, {
      isCancelled: () => unmounted.current,
    });
    return fresh ? { tone: 'done', text: describe(fresh) } : { tone: 'timeout', text: t('timeout', { provider: providerStart }) };
  }

  /** Opens a fresh portal session (its links are short-lived) at the page the button names. */
  function openPortal(target: PortalTarget) {
    void run(`portal:${target}`, async () => {
      const portal = await openBillingPortal();
      const url = target === 'overview'
        ? portal.url
        : target === 'payment-method' ? portal.updatePaymentMethodUrl : portal.cancelUrl;
      if (!url) {
        throw new Error(t('portalMissing', { providerStart, provider }));
      }
      follow(url);
      return t('openingProvider', { provider });
    });
  }

  /**
   * The Done sentence for a confirmed plan or seat change, from the server's own 202 and the new summary.
   * An upgrade never states a credit number: `creditGrantMillis` is a request-time estimate, and the
   * backend grants the credits only once Paddle's proration payment succeeds, which can come later.
   */
  function describeChange(result: PlanChangeResult, fresh: BillingSummary, kind: 'plan' | 'seats'): string {
    const sub = fresh.subscription;
    if (result.direction === 'keep') {
      return t('describe.keep', { plan: sub?.planName ?? t('describe.yourPlan') });
    }
    if (result.direction === 'downgrade') {
      const change = sub?.scheduledChange;
      const date = change ? longDate(change.effectiveAt) : null;
      const current = sub?.planName ?? t('describe.yourPlan');
      if (change && change.planKey === sub?.planKey) {
        return date
          ? t('describe.downgradeSeatsDate', { seats: change.seats, date, current: sub.seats })
          : t('describe.downgradeSeatsNext', { seats: change.seats, current: sub.seats });
      }
      const next = change?.planName ?? t('describe.newPlanStart');
      return date
        ? t('describe.downgradePlanDate', { plan: next, date, current })
        : t('describe.downgradePlanNext', { plan: next, current });
    }
    const credits = t('describe.extraCredits', { provider });
    return kind === 'seats'
      ? `${t('describe.seatsNow', { seats: sub?.seats ?? result.seats })} ${credits}`
      : `${sub?.planName ? t('describe.onPlan', { plan: sub.planName }) : t('describe.onNewPlan')} ${credits}`;
  }

  function changePlan(planKey: string, seats: number | undefined, key: string, planInterval?: BillingInterval) {
    void run(key, async () => {
      const result = planInterval === 'annual'
        ? await changeSubscriptionPlan(planKey, seats, planInterval)
        : await changeSubscriptionPlan(planKey, seats);
      return awaitProvider(planChangeConfirmed(planKey, result), (fresh) => describeChange(result, fresh, 'plan'));
    });
  }

  // A plan chosen before the account existed opens its checkout once, as soon as the summary says
  // this workspace may buy it (the same rule the dashboard used to send the person here). When it
  // may not, the card stays marked and nothing opens. The server creates the transaction from the
  // plan key alone; the page only asks, and only for a verified address.
  const autoCheckoutDone = useRef(false);
  useEffect(() => {
    if (!wantsCheckout || autoCheckoutDone.current || !summary.data || !pickedPlan) return;
    autoCheckoutDone.current = true;
    if (pickedPlanCheckoutHref(pickedPlan, summary.data, user?.emailVerified) === null) return;
    const key = pickedPlan;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- starting the checkout marks the card busy; it runs once per arrival
    void run(`plan:${key}`, () => beginCheckout(() => startPlanCheckout(key), { kind: 'plan', planKey: key }));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- fires once per arrival; run/beginCheckout are re-created every render
  }, [wantsCheckout, summary.data, pickedPlan, user?.emailVerified]);
  // `Billing:Enabled=false` answers 404 on every /api/v1/billing/* action. That is a property of the
  // deployment, not of the request, so it gets its own explanation instead of a retry button that
  // could never succeed.
  if (isBillingUnavailable(summary.error)) {
    return (
      <>
        {header}
        <Alert icon={<IconInfoCircle size={16} />} color="blue" title={t('disabled.title')}>
          <Text size="sm">
            {t('disabled.body')}
          </Text>
        </Alert>
      </>
    );
  }

  if (summary.error) {
    return (
      <>
        {header}
        <Alert icon={<IconAlertCircle size={16} />} color="red" title={t('loadError.title')}>
          <Group justify="space-between" gap="sm">
            <Text size="sm">
              {describeError(tAll, summary.error, t('loadError.fallback'))}
            </Text>
            <Button size="xs" variant="light" color="red" onClick={() => { void summary.mutate(); }}>
              {t('loadError.retry')}
            </Button>
          </Group>
        </Alert>
      </>
    );
  }

  if (summary.isLoading && !summary.data) {
    return (
      <>
        {header}
        <Center h={300}><Loader size="lg" aria-label={t('loading')} /></Center>
      </>
    );
  }

  const data = summary.data;
  if (!data) return header;

  const { subscription, plans, topUp } = data;
  // A live paid plan changes through cancel / change plan on this page; a workspace without one
  // (Trial, canceled, or an abandoned checkout — which all keep their Paddle customer) chooses a
  // plan through a checkout. `hasBillingCustomer` only says whether there are invoices to open.
  const livePaidPlan = hasLivePaidPlan(data);
  const currentPlan = plans.find((p) => p.current) ?? null;
  const currentPerSeat = currentPlan?.kind === 'PerSeat' ? currentPlan : null;
  const canManage = data.canManageBilling;
  const merchant = data.billingProvider || data.billingProviderDisplayName ? provider : null;
  const offers: ActivePromotion[] = activePromotions.data ?? [];

  // What a code can be tried against: the plans a checkout is offered for (none while a paid plan is
  // live: those change through change-plan, which takes no code), and the packs.
  const previewTargets: PreviewTarget[] = canManage
    ? [
        ...(livePaidPlan
          ? []
          : plans
              .filter((p) => !p.current)
              .map((p) => ({
                id: `plan:${p.key}`,
                label: p.name,
                request: planPreviewRequest(p, p.kind === 'PerSeat' ? (p.minSeats ?? 1) : undefined, null),
              }))),
        ...(topUp.available
          ? topUp.packs.map((pack) => ({
              id: `topup:${pack}`,
              label: t('packCredits', { credits: formatCredits(pack) }),
              request: packPreviewRequest(pack, null),
            }))
          : []),
      ]
    : [];

  /** The applied code, for one target, or null when the code does not cover it. */
  const codeFor = (targetId: string): string | null =>
    appliedCode?.targets.some((t) => t.id === targetId) ? appliedCode.code : null;

  /**
   * Checks a code against every target at once. The server answers each one: where the code
   * applies, the quote is put straight into the cache the card reads, so the card shows it without
   * asking again. Answers an error sentence, or null when it applied to something.
   */
  async function applyPromoCode(raw: string): Promise<ApplyOutcome> {
    const code = normalizePromoCode(raw);
    if (!PROMO_CODE_PATTERN.test(code)) return { message: t('promoInvalid'), rateLimited: false };

    const results = await Promise.allSettled(
      previewTargets.map((t) => previewCheckout({ ...t.request, promoCode: code })),
    );
    const valid: PreviewTarget[] = [];
    let refusal: unknown = null;
    results.forEach((result, i) => {
      const target = previewTargets[i];
      if (result.status === 'fulfilled') {
        if (!result.value.promotion && result.value.bonusCredits === null) return;
        valid.push(target);
        void primeCache(checkoutPreviewKey({ ...target.request, promoCode: code }), result.value, { revalidate: false });
      } else if (refusal === null || isRateLimited(result.reason)) {
        refusal = result.reason;
      }
    });

    if (valid.length === 0) {
      return refusal === null
        ? { message: t('promoInvalid'), rateLimited: false }
        : { message: promoCodeMessage(refusal), rateLimited: isRateLimited(refusal) };
    }
    setAppliedCode({ code, targets: valid });
    return null;
  }

  return (
    <>
      {header}
      <Stack gap="lg">
        <Text size="sm" c="dimmed">
          {merchant
            // The provider is named because it also appears on the card statement and on the
            // invoice — the two places you would go looking for a charge you do not recognise.
            ? t('intro', { provider: merchant })
            : t('introGeneric')}{' '}
          {t('introCredits')}
        </Text>

        {!canManage && (
          <Alert icon={<IconInfoCircle size={16} />} color="gray" title={t('readOnly.title')}>
            <Text size="sm">
              {t('readOnly.body', { provider })}
            </Text>
          </Alert>
        )}

        {needsVerify && (
          <>
            <Alert icon={<IconInfoCircle size={16} />} color="yellow" title={t('verifyEmail.title')}>
              <Text size="sm">{t('verifyEmail.body')}</Text>
            </Alert>
            <UnverifiedEmailBanner />
          </>
        )}

        {actionError && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" title={t('actionError')}>
            <Text size="sm">{actionError}</Text>
          </Alert>
        )}

        {notice?.tone === 'waiting' && (
          <Alert
            icon={<Loader size={14} />}
            color="gray"
            title={notice.text}
            data-testid="billing-waiting-notice"
            role="status"
          >
            <Text size="sm">{t('waitingBody', { provider: providerStart })}</Text>
          </Alert>
        )}

        {notice?.tone === 'timeout' && (
          <Alert
            icon={<IconClockHour4 size={16} />}
            color="yellow"
            title={t('notConfirmed')}
            data-testid="billing-timeout-notice"
            role="status"
          >
            <Text size="sm">{notice.text}</Text>
          </Alert>
        )}

        {notice?.tone === 'done' && (
          <Alert icon={<IconCheck size={16} />} color="green" title={t('done')} data-testid="billing-done-notice" role="status">
            <Text size="sm">{notice.text}</Text>
          </Alert>
        )}

        {!livePaidPlan && (
          <FirstVideoGrantCard grant={data?.firstVideoGrant} status={data?.firstVideoGrantStatus} />
        )}

        <CurrentPlanCard
          summary={data}
          currentPerSeat={currentPerSeat}
          livePaidPlan={livePaidPlan}
          pending={pending}
          canManage={canManage}
          onOpenPortal={openPortal}
          onCancel={() => {
            void run('cancel', async () => {
              await cancelSubscription();
              return awaitProvider(cancelConfirmed, (fresh) => {
                const at = fresh.subscription?.cancelEffectiveAt ?? fresh.subscription?.periodEnd;
                return at
                  ? t('cancelDoneDate', { date: longDate(at) })
                  : t('cancelDoneEnd');
              });
            });
          }}
          onResume={() => {
            void run('resume', async () => {
              await resumeSubscription();
              return awaitProvider(resumeConfirmed, (fresh) =>
                t('resumeDone', { plan: fresh.subscription?.planName ?? t('describe.yourPlan') }));
            });
          }}
          onKeepCurrent={() => {
            if (subscription) changePlan(subscription.planKey, undefined, 'keep');
          }}
          onChangeSeats={(seats) => {
            void run('seats', async () => {
              const result = await changeBillingSeats(seats);
              return awaitProvider(planChangeConfirmed(subscription?.planKey ?? '', result), (fresh) =>
                describeChange(result, fresh, 'seats'));
            });
          }}
        />

        <Card withBorder radius="lg" padding="lg" data-testid="storage-card">
          <Stack gap="md">
            <div>
              <Text fw={600}>{t('storage.title')}</Text>
              <Text size="sm" c="dimmed">
                {t('storage.description')}
              </Text>
            </div>
            {storage.data ? (
              <StorageUsageBar usage={storage.data} withProjects />
            ) : storage.error ? (
              <Text size="sm" c="dimmed">
                {t('storage.error')}
              </Text>
            ) : (
              <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t('storage.loading')}</Text></Group>
            )}
          </Stack>
        </Card>

        <PromoCodeField
          canManage={canManage}
          canApply={previewTargets.length > 0}
          appliedCode={appliedCode?.code ?? null}
          appliedTo={appliedCode?.targets.map((t) => t.label) ?? []}
          disabled={pending !== null}
          onApply={applyPromoCode}
          onRemove={() => setAppliedCode(null)}
          onRedeemed={() => { void summary.mutate(); }}
        />

        <Card id="plans" withBorder radius="lg" padding="lg" data-testid="plans-card">
          <Stack gap="md">
            <div>
              <Text fw={600}>{t('plans.title')}</Text>
              <Text size="sm" c="dimmed">
                {livePaidPlan
                  ? t('plans.descriptionLive')
                  : t('plans.description')}
              </Text>
            </div>

            {plans.some(sellsAnnual) && (
              <PlanIntervalSwitch value={interval} onChange={setPickedInterval} annualSubscriber={livePaidPlan && isAnnualSubscriber(subscription)} />
            )}

            {plans.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('plans.none')}
              </Text>
            ) : (
              <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="lg">
                {plans.map((plan) => (
                  <PlanCard
                    key={plan.key}
                    plan={plan}
                    picked={pickedPlan === plan.key}
                    canManage={canManage}
                    subscription={subscription}
                    currentPlan={currentPlan}
                    livePaidPlan={livePaidPlan}
                    pending={pending}
                    formattedTotal={prices.totals.get(priceIdFor(plan, interval) ?? '') ?? null}
                    interval={interval}
                    merchant={provider}
                    beforeTax={isNonPaddleProvider(data)}
                    promoCode={codeFor(`plan:${plan.key}`)}
                    offer={offers.find((o) => promotionCoversPlan(o, plan.key)) ?? null}
                    onCheckout={(seats) => {
                      const code = codeFor(`plan:${plan.key}`);
                      void run(`plan:${plan.key}`, () =>
                        beginCheckout(() =>
                          interval === 'annual'
                            ? startPlanCheckout(plan.key, seats, code ?? undefined, interval)
                            : code ? startPlanCheckout(plan.key, seats, code) : startPlanCheckout(plan.key, seats),
                          { kind: 'plan', planKey: plan.key }));
                    }}
                    onChangePlan={(seats) =>
                      (interval === 'annual'
                      ? changePlan(plan.key, seats, `change:${plan.key}`, interval)
                      : changePlan(plan.key, seats, `change:${plan.key}`))}
                  />
                ))}
              </SimpleGrid>
            )}
          </Stack>
        </Card>

        <Card withBorder radius="lg" padding="lg" data-testid="topup-card">
          <Stack gap="md">
            <div>
              <Text fw={600}>{t('topUp.title')}</Text>
              <Text size="sm" c="dimmed">
                {t('topUp.description')}
              </Text>
            </div>

            {!canManage ? (
              <Text size="sm" c="dimmed">
                {t('topUp.needsOwner')}
              </Text>
            ) : !topUp.available ? (
              <Text size="sm" c="dimmed">
                {t('topUp.paidOnly')}
              </Text>
            ) : topUp.packs.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('topUp.noPacks')}
              </Text>
            ) : (
              <>
                <Group gap="md" wrap="wrap" align="flex-start">
                  {topUp.packs.map((pack) => (
                    <PackOption
                      key={pack}
                      pack={pack}
                      pending={pending}
                      beforeTax={isNonPaddleProvider(data)}
                      promoCode={codeFor(`topup:${pack}`)}
                      offer={offers.find((o) => promotionCoversPack(o, pack)) ?? null}
                      onBuy={() => {
                        const code = codeFor(`topup:${pack}`);
                        void run(`topup:${pack}`, () =>
                          beginCheckout(() => (code ? startTopUpCheckout(pack, code) : startTopUpCheckout(pack)), { kind: 'topUp', credits: pack }));
                      }}
                    />
                  ))}
                </Group>
                <Text size="xs" c="dimmed">
                  {t('topUp.exactCharge')}
                </Text>
              </>
            )}
          </Stack>
        </Card>
      </Stack>
    </>
  );
}

function PackOption({
  pack,
  pending,
  beforeTax,
  promoCode,
  offer,
  onBuy,
}: {
  pack: number;
  pending: string | null;
  /** The provider's previews can omit tax (non-Paddle): the discounted quote says "before tax". */
  beforeTax: boolean;
  /** The applied promo code, when it covers this pack. */
  promoCode: string | null;
  /** The automatic discount or bonus running on this pack, if any. */
  offer: ActivePromotion | null;
  onBuy: () => void;
}) {
  const t = useT('billing.page');
  // Always asked for: the payment provider's own quote is where a pack's price comes from, so the
  // buyer sees what each pack costs before pressing it (no price is written into this page).
  const preview = useCheckoutPreview(packPreviewRequest(pack, promoCode));
  const quote = preview.data;
  const discounted = hasPriceDiscount(quote) ? quote : null;
  const amounts = discounted ? previewAmounts(discounted) : null;
  const bonus = quote?.bonusCredits ?? null;
  const headline = quote?.promotion?.headline || offer?.headline || null;

  return (
    <Stack gap={4} align="flex-start">
      <Button
        variant="light"
        disabled={pending !== null}
        loading={pending === `topup:${pack}`}
        onClick={onBuy}
      >
        {t('packCredits', { credits: formatCredits(pack) })}
      </Button>
      {headline && <Badge color="violet" variant="light" size="sm" data-testid="promo-badge">{headline}</Badge>}
      {!quote && preview.isLoading && (
        <Text size="sm" c="dimmed" data-testid="pack-price-loading">{t('priceLoading')}</Text>
      )}
      {!quote && !preview.isLoading && preview.error && (
        <Text size="xs" c="dimmed" data-testid="pack-price-unavailable">{t('priceAtCheckout')}</Text>
      )}
      {quote && !amounts && (
        <Group gap={6} align="baseline">
          <Text size="sm" fw={700} data-testid="pack-price">{quote.total}</Text>
          {beforeTax && <Text size="xs" c="dimmed">{t('beforeTax')}</Text>}
        </Group>
      )}
      {amounts && (
        <Group gap={6} align="baseline">
          <Text size="xs" c="dimmed" td="line-through">{amounts.subtotal}</Text>
          <Text size="sm" fw={700} data-testid="promo-preview-total">{amounts.total}</Text>
          {beforeTax && <Text size="xs" c="dimmed">{t('beforeTax')}</Text>}
        </Group>
      )}
      {bonus !== null && bonus > 0 && (
        <Text size="xs" c="grape">{t('bonus', { credits: formatCredits(bonus) })}</Text>
      )}
    </Stack>
  );
}

function isRateLimited(error: unknown): boolean {
  return error instanceof ApiError && error.status === 429;
}
