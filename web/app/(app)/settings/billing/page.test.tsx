// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor, within, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import BillingPage from './page';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { usePaddlePrices } from '@/lib/hooks/use-paddle-prices';
import { loadPaddle, openOverlayCheckout } from '@/lib/paddle/sdk';
import type { Paddle } from '@paddle/paddle-js';
import {
  cancelSubscription,
  changeBillingSeats,
  changeSubscriptionPlan,
  openBillingPortal,
  resumeSubscription,
  startPlanCheckout,
  startTopUpCheckout,
} from '@/lib/api/billing';
import type { BillingPlan, BillingSummary } from '@/lib/types/billing';
import { previewCheckout, redeemPromoCode, type CheckoutPreview } from '@/lib/api/promotions';
import { checkoutPreviewKey } from '@/lib/hooks/use-promotions';

// Only the three calls that leave the browser are stubbed. `isBillingUnavailable`,
// `billingActionMessage` and `isSafeRedirectUrl` stay real, because the page's behaviour on
// refusals and on a provider URL is exactly what most of these tests are about.
vi.mock('@/lib/api/billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/billing')>();
  return {
    ...actual,
    startPlanCheckout: vi.fn(),
    startTopUpCheckout: vi.fn(),
    openBillingPortal: vi.fn(),
    changeBillingSeats: vi.fn(),
    cancelSubscription: vi.fn(),
    resumeSubscription: vi.fn(),
    changeSubscriptionPlan: vi.fn(),
  };
});

vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: vi.fn() }));

// The real hook, with the address's verified flag overridable (undefined: the real answer).
const AUTH = vi.hoisted(() => ({ emailVerified: undefined as boolean | undefined }));
vi.mock('@/lib/hooks/use-auth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-auth')>();
  return {
    ...actual,
    useAuth: () => {
      const real = actual.useAuth();
      return AUTH.emailVerified === undefined ? real : { ...real, user: { ...(real.user ?? {}), emailVerified: AUTH.emailVerified } as typeof real.user };
    },
  };
});

// The storage bar's own figures (`GET /api/v1/storage/usage`), stubbed so the page never fetches.
const STORAGE = vi.hoisted(() => ({
  result: { data: undefined, error: undefined, isLoading: false, mutate: vi.fn() } as Record<string, unknown>,
}));
vi.mock('@/lib/hooks/use-storage', () => ({
  useStorageUsage: () => STORAGE.result,
  useProjectFolderLinks: () => ({ data: [], error: undefined, isLoading: false, mutate: vi.fn() }),
}));

// Paddle.js is never loaded in a unit test: the package injects a <script> from cdn.paddle.com, and
// the loader and the preview each have their own tests. What these stubs make visible is the page's
// own decision — the embedded overlay when this deployment has a Paddle.js checkout, the provider's
// hosted page for the same transaction when it does not.
vi.mock('@/lib/paddle/sdk', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/paddle/sdk')>()),
  loadPaddle: vi.fn(),
  openOverlayCheckout: vi.fn(),
}));
vi.mock('@/lib/hooks/use-paddle-prices', () => ({ usePaddlePrices: vi.fn() }));

// Promotions: the automatic offers and the per-card quotes are served from these tables, keyed the
// way the page keys its requests, so a test states exactly what the server quoted.
const PROMO = vi.hoisted(() => ({
  active: [] as unknown[],
  previews: new Map<string, unknown>(),
  mode: 'ok' as 'ok' | 'loading' | 'error',
}));
vi.mock('@/lib/hooks/use-promotions', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-promotions')>();
  return {
    ...actual,
    useActivePromotions: () => ({ data: PROMO.active, error: undefined, isLoading: false }),
    useCheckoutPreview: (request: Parameters<typeof actual.checkoutPreviewKey>[0] | null) => ({
      data: request ? PROMO.previews.get(actual.checkoutPreviewKey(request)) : undefined,
      error: PROMO.mode === 'error' ? new Error('preview failed') : undefined,
      isLoading: PROMO.mode === 'loading',
    }),
  };
});
vi.mock('@/lib/api/promotions', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/promotions')>()),
  previewCheckout: vi.fn(),
  redeemPromoCode: vi.fn(),
}));

// Marketing consent: hidden unless a test states the buyer's record and the wording.
const MKT = vi.hoisted(() => ({
  consent: undefined as undefined | { status: string },
  consentError: undefined as unknown,
  wording: undefined as undefined | { version: string; text: string },
  update: vi.fn(),
}));
vi.mock('@/lib/hooks/use-marketing', () => ({
  useMarketingConsent: () => ({ data: MKT.consent, error: MKT.consentError, mutate: vi.fn() }),
  useMarketingWording: () => ({ data: MKT.wording, error: undefined, mutate: vi.fn() }),
}));
vi.mock('@/lib/api/marketing', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/marketing')>()),
  updateMarketingConsent: (...args: unknown[]) => MKT.update(...args),
}));


const summaryMock = vi.mocked(useBillingSummary);
const paddleLoadMock = vi.mocked(loadPaddle);
const overlayMock = vi.mocked(openOverlayCheckout);
const pricesMock = vi.mocked(usePaddlePrices);
const planCheckoutMock = vi.mocked(startPlanCheckout);
const topUpCheckoutMock = vi.mocked(startTopUpCheckout);
const portalMock = vi.mocked(openBillingPortal);
const seatsMock = vi.mocked(changeBillingSeats);
const cancelMock = vi.mocked(cancelSubscription);
const resumeMock = vi.mocked(resumeSubscription);
const changePlanMock = vi.mocked(changeSubscriptionPlan);
const previewMock = vi.mocked(previewCheckout);
const redeemMock = vi.mocked(redeemPromoCode);

/** A quote as the payment provider formats it. The page must print these strings, nothing else. */
const QUOTE: CheckoutPreview = {
  currencyCode: 'EUR',
  subtotal: '€27,00',
  discount: '€5,40',
  total: '€21,60',
  promotion: { headline: 'Spring sale: 20% off', kind: 'planDiscount' },
  bonusCredits: null,
};

type SummaryResult = ReturnType<typeof useBillingSummary>;

const summaryResult = (over: Partial<SummaryResult>) =>
  ({ data: undefined, error: undefined, isLoading: false, isValidating: false, mutate: vi.fn(), ...over }) as unknown as SummaryResult;

const plan = (over: Partial<BillingPlan> = {}): BillingPlan => ({
  key: 'creator',
  name: 'Creator',
  kind: 'Subscription',
  priceMonthlyUsd: 29,
  minSeats: null,
  includedCredits: 1000,
  priceId: 'pri_creator',
  current: false,
  ...over,
});

/** The three sellable plans the endpoint returns, priced by the API and never by this page. */
const PLANS: BillingPlan[] = [
  plan({ key: 'creator', name: 'Creator', priceMonthlyUsd: 29, includedCredits: 1000, priceId: 'pri_creator' }),
  plan({ key: 'pro', name: 'Pro', priceMonthlyUsd: 79, includedCredits: 3500, priceId: 'pri_pro', current: true }),
  plan({ key: 'team', name: 'Team', kind: 'PerSeat', priceMonthlyUsd: 49, minSeats: 3, includedCredits: 1500, priceId: 'pri_team' }),
];

const summary = (over: Partial<BillingSummary> = {}): BillingSummary => ({
  billingProvider: 'paddle',
  paddle: null,
  canManageBilling: true,
  subscription: {
    planKey: 'pro', planName: 'Pro', status: 'Active', seats: 1,
    periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
    cancelAtPeriodEnd: false, hasBillingCustomer: true,
  },
  credits: { balance: 120, reserved: 20, available: 100 },
  topUp: { available: true, packs: [500, 1000, 3000] },
  plans: PLANS,
  ...over,
});

/** A Trial workspace: free, unpurchased, and so not allowed top-ups (decision D9). */
const trial = (over: Partial<BillingSummary> = {}): BillingSummary =>
  summary({
    subscription: {
      planKey: 'trial', planName: 'Trial', status: 'Active', seats: 1,
      periodStart: null, periodEnd: null, cancelAtPeriodEnd: false, hasBillingCustomer: false,
    },
    credits: { balance: 100, reserved: 0, available: 100 },
    topUp: { available: false, packs: [] },
    ...over,
  });

/** The Team plan in force: the one plan whose billing_plan seats can be changed from this page. */
const teamSubscription = (over: Partial<BillingSummary> = {}): BillingSummary =>
  summary({
    plans: PLANS.map((p) => ({ ...p, current: p.key === 'team' })),
    subscription: {
      planKey: 'team', planName: 'Team', status: 'Active', seats: 3,
      periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
      cancelAtPeriodEnd: false, hasBillingCustomer: true,
    },
    ...over,
  });

const assignMock = vi.fn();
/** Opens the page at an address with a query (the suite replaces `window.location` with a stub). */
const openWithQuery = (search: string) =>
  Object.defineProperty(window, 'location', {
    configurable: true,
    value: { assign: assignMock, href: `http://localhost/app/settings/billing${search}#plans`, origin: 'http://localhost', search, pathname: '/app/settings/billing', hash: '#plans' },
  });

const renderPage = () =>
  render(
    <MantineProvider>
      <BillingPage />
    </MantineProvider>,
  );

describe('BillingPage', () => {
  beforeEach(() => {
    // Mantine's SegmentedControl measures its indicator with a ResizeObserver, which jsdom lacks.
    global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
    // jsdom's own `location.assign` refuses to navigate without a real document, so the page's one
    // navigation is replaced by a spy. If the page ever used `href = …` instead, that assignment
    // would not be recorded and the checkout tests would fail rather than pass vacuously.
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { assign: assignMock, href: 'http://localhost/app/settings/billing', origin: 'http://localhost' },
    });
    PROMO.mode = 'ok';
    summaryMock.mockReturnValue(summaryResult({ data: summary() }));
    STORAGE.result = { data: undefined, error: undefined, isLoading: false, mutate: vi.fn() };
    planCheckoutMock.mockResolvedValue({ checkoutUrl: 'https://checkout.paddle.test/txn_1', transactionId: 'txn_1' });
    topUpCheckoutMock.mockResolvedValue({ checkoutUrl: 'https://checkout.paddle.test/txn_2', transactionId: 'txn_2' });
    portalMock.mockResolvedValue({ url: 'https://portal.paddle.test/session' });
    // Default: a deployment whose summary carries no Paddle.js settings, and no localized prices, so
    // the page shows the catalogue's list price and follows the provider's hosted checkout URL. The
    // embedded-checkout cases below override both.
    paddleLoadMock.mockResolvedValue(null);
    pricesMock.mockReturnValue({ totals: new Map(), pending: false });
    seatsMock.mockResolvedValue({
      direction: 'upgrade', effective: 'now', effectiveAt: '2026-09-20T00:00:00Z',
      creditGrantMillis: 0, seats: 5, message: 'The change applies when the provider confirms it.',
    });
    PROMO.active = [];
    PROMO.previews = new Map();
    MKT.consent = undefined;
    MKT.consentError = undefined;
    MKT.wording = undefined;
    MKT.update.mockReset();
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('shows the current plan, when it renews and what is left to spend', () => {
    renderPage();

    const card = screen.getByTestId('current-plan-card');
    expect(card.textContent).toContain('Pro');
    expect(card.textContent).toContain('Active');
    expect(card.textContent).toContain('100');
    expect(card.textContent).toContain('120 in the account');
    expect(card.textContent).toContain('20.00 held for running work');
    expect(card.textContent).toContain('Renews');
    expect(card.textContent).toContain(new Date('2026-10-10T00:00:00Z').toLocaleDateString(undefined, {
      year: 'numeric', month: 'long', day: 'numeric',
    }));
  });

  it('renders one card per plan with the price the API supplied, never one of its own', () => {
    summaryMock.mockReturnValue(summaryResult({
      data: summary({ plans: PLANS.map((p) => ({ ...p, priceMonthlyUsd: 111 })) }),
    }));
    renderPage();

    expect(screen.getByTestId('plans-card')).toBeTruthy();
    expect(screen.getByTestId('plan-creator').textContent).toContain('$111.00');
    expect(screen.getByTestId('plan-pro').textContent).toContain('$111.00');
    expect(screen.queryByText('$29.00')).toBeNull();
    expect(screen.queryByText('$79.00')).toBeNull();
  });

  // Someone who pressed "Choose Pro" on the pricing page before they had an account arrives here
  // from the dashboard's notice with `?plan=pro`: that card is marked, nothing else changes.
  it('marks the plan picked on the pricing page, and forgets the remembered choice', async () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial({ plans: PLANS.map((p) => ({ ...p, current: false })) }) }));
    window.localStorage.setItem('rb-signup-intent:v1', JSON.stringify({ at: Date.now(), who: 'u1', plan: 'pro', next: '/app/projects/9' }));
    openWithQuery('?plan=Pro');
    renderPage();

    await waitFor(() => expect(screen.getByTestId('plan-pro').getAttribute('data-picked')).toBe('true'));
    expect(within(screen.getByTestId('plan-pro')).getByTestId('picked-badge').textContent).toBe('Your pick');
    expect(screen.getByTestId('plan-creator').getAttribute('data-picked')).toBeNull();
    // Arriving here is the offer made: the plan is dropped, the return path is not this page's to drop.
    const left = JSON.parse(window.localStorage.getItem('rb-signup-intent:v1') ?? '{}');
    expect(left.plan).toBeUndefined();
    expect(left.next).toBe('/app/projects/9');
    window.localStorage.clear();
  });

  describe('the checkout a picked plan opens straight after the first sign-in', () => {
    const free = () => trial({ plans: PLANS.map((p) => ({ ...p, current: false })) });
    let replaceState: ReturnType<typeof vi.spyOn>;
    beforeEach(() => {
      AUTH.emailVerified = undefined;
      window.sessionStorage.clear();
      summaryMock.mockReturnValue(summaryResult({ data: free() }));
      replaceState = vi.spyOn(window.history, 'replaceState').mockImplementation(() => undefined);
    });
    afterEach(() => {
      replaceState.mockRestore();
      window.sessionStorage.clear();
      AUTH.emailVerified = undefined;
    });

    it('starts the checkout of the plan once, by key alone, and takes start off the address', async () => {
      window.sessionStorage.setItem('rb-plan-start:v1', 'pro');
      openWithQuery('?plan=pro&start=1');
      renderPage();
      await waitFor(() => expect(planCheckoutMock).toHaveBeenCalledTimes(1));
      expect(window.sessionStorage.getItem('rb-plan-start:v1')).toBeNull();
      expect(planCheckoutMock).toHaveBeenCalledWith('pro');
      expect(replaceState).toHaveBeenCalledTimes(1);
      const url = String(replaceState.mock.calls[0][2]);
      expect(url).not.toContain('start');
      expect(url).toContain('plan=pro');
      await act(async () => {});
      expect(planCheckoutMock).toHaveBeenCalledTimes(1);
    });

    it('does not start for start=1 without the dashboard\'s token (a link made elsewhere), but marks the card', async () => {
      openWithQuery('?plan=pro&start=1');
      renderPage();
      await waitFor(() => expect(screen.getByTestId('plan-pro').getAttribute('data-picked')).toBe('true'));
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
      expect(String(replaceState.mock.calls[0][2])).not.toContain('start');
    });

    it('does not start when the token is for another plan', async () => {
      window.sessionStorage.setItem('rb-plan-start:v1', 'creator');
      openWithQuery('?plan=pro&start=1');
      renderPage();
      await waitFor(() => expect(screen.getByTestId('plan-pro').getAttribute('data-picked')).toBe('true'));
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
    });

    it('only marks the card when start is absent', async () => {
      openWithQuery('?plan=pro');
      renderPage();
      await waitFor(() => expect(screen.getByTestId('plan-pro').getAttribute('data-picked')).toBe('true'));
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
    });

    it('opens nothing for a per-seat plan, but still marks the card', async () => {
      window.sessionStorage.setItem('rb-plan-start:v1', 'team');
      openWithQuery('?plan=team&start=1');
      renderPage();
      await waitFor(() => expect(screen.getByTestId('plan-team').getAttribute('data-picked')).toBe('true'));
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
    });

    it('opens nothing for a member who cannot manage billing, but still marks the card', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: { ...free(), canManageBilling: false } }));
      window.sessionStorage.setItem('rb-plan-start:v1', 'pro');
      openWithQuery('?plan=pro&start=1');
      renderPage();
      await waitFor(() => expect(screen.getByTestId('plan-pro').getAttribute('data-picked')).toBe('true'));
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
    });

    it('opens nothing for an unverified address, but still marks the card', async () => {
      AUTH.emailVerified = false;
      window.sessionStorage.setItem('rb-plan-start:v1', 'pro');
      openWithQuery('?plan=pro&start=1');
      renderPage();
      await waitFor(() => expect(screen.getByTestId('plan-pro').getAttribute('data-picked')).toBe('true'));
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
    });

    it('opens nothing for a plan token it does not know', async () => {
      window.sessionStorage.setItem('rb-plan-start:v1', '<script>');
      openWithQuery('?plan=%3Cscript%3E&start=1');
      renderPage();
      await act(async () => {});
      expect(planCheckoutMock).not.toHaveBeenCalled();
    });
  });

  it('marks nothing for a plan token it does not know, or for the plan the workspace is already on', async () => {
    openWithQuery('?plan=%3Cscript%3E');
    renderPage();
    await new Promise((r) => setTimeout(r, 20));
    expect(document.querySelector('[data-picked]')).toBeNull();
    cleanup();
    summaryMock.mockReturnValue(summaryResult({ data: teamSubscription() }));
    openWithQuery('?plan=team');
    renderPage();
    await new Promise((r) => setTimeout(r, 20));
    expect(document.querySelector('[data-picked]')).toBeNull();
    expect(screen.queryByTestId('picked-badge')).toBeNull();
  });

  it('marks the plan the workspace is already on, and offers no checkout for it', () => {
    renderPage();

    const pro = screen.getByTestId('plan-pro');
    expect(pro.textContent).toContain('Current plan');
    expect(pro.textContent).toContain('This is the plan the workspace is on now.');
    expect(screen.queryByRole('button', { name: 'Choose Pro' })).toBeNull();
  });

  it('starts a checkout for a chosen plan and follows the URL the provider returned', async () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));

    await waitFor(() => expect(planCheckoutMock).toHaveBeenCalledWith('creator', undefined));
    await waitFor(() => expect(assignMock).toHaveBeenCalledWith('https://checkout.paddle.test/txn_1'));
    expect(topUpCheckoutMock).not.toHaveBeenCalled();
    // No Paddle.js settings on this summary, so there is nothing to initialize and nothing to open:
    // the buyer completes the very same transaction on the provider's own page.
    expect(paddleLoadMock).not.toHaveBeenCalled();
    expect(overlayMock).not.toHaveBeenCalled();
  });

  it('with Stripe, follows the hosted URL, never loads Paddle.js, and names Stripe in the copy and test ids', async () => {
    const stripe = summary({ billingProvider: 'stripe', billingProviderDisplayName: 'Stripe', paddle: null });
    summaryMock.mockReturnValue(summaryResult({ data: { ...trial(), billingProvider: 'stripe', billingProviderDisplayName: 'Stripe', paddle: null } }));
    planCheckoutMock.mockResolvedValue({ checkoutUrl: 'https://checkout.stripe.test/c/cs_1', transactionId: 'cs_1' });
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));

    await waitFor(() => expect(assignMock).toHaveBeenCalledWith('https://checkout.stripe.test/c/cs_1'));
    expect(paddleLoadMock).not.toHaveBeenCalled();
    expect(overlayMock).not.toHaveBeenCalled();
    expect(screen.getByText(/billed monthly through Stripe,/)).toBeTruthy();
    expect(document.body.textContent).not.toContain('Paddle');

    cleanup();
    summaryMock.mockReturnValue(summaryResult({ data: stripe }));
    renderPage();
    expect(screen.getByTestId('manage-in-stripe').textContent).toContain('Manage in Stripe');
    expect(screen.queryByTestId('manage-in-paddle')).toBeNull();
  });

  it("opens Paddle's embedded overlay for the transaction the API created, and navigates nowhere", async () => {
    const paddle = { Checkout: { open: vi.fn() } } as unknown as Paddle;
    paddleLoadMock.mockResolvedValue(paddle);
    summaryMock.mockReturnValue(summaryResult({
      data: trial({ paddle: { environment: 'sandbox', clientToken: 'test_87d07575c0b6730d5305e81cff12', customerId: null } }),
    }));
    // The signed-in address the server would also have used, read from the session cookie the Go API
    // wrote, so the buyer does not retype it.
    document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify({ email: 'buyer@example.com' }))}; path=/`;

    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));

    await waitFor(() => expect(overlayMock).toHaveBeenCalledTimes(1));
    expect(planCheckoutMock).toHaveBeenCalledWith('creator', undefined);
    // The transaction id is the API's answer, not something the page chose: the organization a
    // payment buys credits for rides in the transaction's custom data, which the server attached
    // from the caller's session. This page passes no custom data of its own.
    expect(overlayMock).toHaveBeenCalledWith({
      paddle,
      transactionId: 'txn_1',
      successUrl: 'http://localhost/app/checkout',
      email: 'buyer@example.com',
    });
    expect(assignMock).not.toHaveBeenCalled();

    document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  it('buys a top-up through the same overlay, not a second checkout path', async () => {
    const paddle = { Checkout: { open: vi.fn() } } as unknown as Paddle;
    paddleLoadMock.mockResolvedValue(paddle);
    summaryMock.mockReturnValue(summaryResult({
      data: summary({ paddle: { environment: 'production', clientToken: 'live_abc', customerId: 'ctm_01hbuyer' } }),
    }));

    renderPage();
    fireEvent.click(screen.getByRole('button', { name: '500 credits' }));

    await waitFor(() => expect(overlayMock).toHaveBeenCalledTimes(1));
    expect(topUpCheckoutMock).toHaveBeenCalledWith(500);
    expect(overlayMock).toHaveBeenCalledWith(expect.objectContaining({ transactionId: 'txn_2' }));
    expect(assignMock).not.toHaveBeenCalled();
  });

  it("shows Paddle's own localized price where it has one, and the labelled list price where it does not", () => {
    pricesMock.mockReturnValue({ totals: new Map([['pri_creator', 'EUR 31.90']]), pending: false });
    renderPage();

    const creator = screen.getByTestId('plan-creator');
    // Paddle's string, printed exactly as it came back: no parsing, no converting, no re-formatting.
    expect(creator.textContent).toContain('EUR 31.90');
    expect(creator.textContent).not.toContain('$29.00');
    expect(creator.textContent).toContain('priced for your country by the payment provider');

    // The plan Paddle did not price says so, and shows the catalogue price as the list price rather
    // than inventing a localized one.
    const team = screen.getByTestId('plan-team');
    expect(team.textContent).toContain('$49.00');
    expect(team.textContent).toContain('list price per month');
    expect(team.textContent).toContain('excluding tax');
  });

  it('sends the seat count it was given for a per-seat plan, not the minimum', async () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    renderPage();

    fireEvent.change(screen.getByLabelText('Seats on the Team plan'), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: 'Choose Team' }));

    await waitFor(() => expect(planCheckoutMock).toHaveBeenCalledWith('team', 7));
  });

  it('never starts a second subscription: a paying workspace changes plan here instead', () => {
    renderPage();

    expect(screen.getByText(/already has a plan/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Choose Creator' })).toBeNull();
    // Pro is $79; Creator ($29) is cheaper, Team at its 3-seat minimum ($147) dearer.
    expect(screen.getByRole('button', { name: 'Switch to Creator' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Upgrade to Team' })).toBeTruthy();
  });

  it('opens Paddle from the header button', async () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Manage in Paddle/ }));

    await waitFor(() => expect(portalMock).toHaveBeenCalled());
    await waitFor(() => expect(assignMock).toHaveBeenCalledWith('https://portal.paddle.test/session'));
  });

  it('changes seats on a per-seat subscription and reports it once the provider confirmed it', async () => {
    // The refreshed summary already carries the webhook's result, so the first poll confirms it.
    const mutate = vi.fn().mockResolvedValue(teamSubscription({
      subscription: { ...teamSubscription().subscription!, seats: 5 },
    }));
    summaryMock.mockReturnValue(summaryResult({
      data: teamSubscription(),
      mutate,
    }));

    renderPage();

    fireEvent.change(screen.getByLabelText('Seats on the current plan'), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Update seats' }));

    await waitFor(() => expect(seatsMock).toHaveBeenCalledWith(5));
    await waitFor(() => expect(screen.getByText(
      'You have 5 seats now. Extra credits for the rest of this billing period are added as soon as Paddle confirms the payment.',
    )).toBeTruthy());
    expect(mutate).toHaveBeenCalled();
  });

  it('names the provider whose charge will appear on the card statement', () => {
    renderPage();
    // The merchant name, not the provider key (F-09).
    expect(screen.getByText(/billed monthly through Paddle,/)).toBeTruthy();

    cleanup();
    summaryMock.mockReturnValue(summaryResult({ data: summary({ billingProvider: null }) }));
    renderPage();
    expect(screen.getByText(/billed monthly by our payment provider/)).toBeTruthy();
  });

  it('offers no seat control for a plan that is not billed per seat', () => {
    renderPage();

    expect(screen.queryByLabelText('Seats on the current plan')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Update seats' })).toBeNull();
  });

  it('explains that top-ups need a paid plan instead of offering a checkout that would be refused', () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    renderPage();

    const card = screen.getByTestId('topup-card');
    expect(card.textContent).toContain('Top-up credits are available on paid plans only.');
    expect(screen.queryByRole('button', { name: /credits$/ })).toBeNull();
    expect(topUpCheckoutMock).not.toHaveBeenCalled();
  });

  it('buys a top-up pack in credits', async () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: '1,000 credits' }));

    await waitFor(() => expect(topUpCheckoutMock).toHaveBeenCalledWith(1000));
    await waitFor(() => expect(assignMock).toHaveBeenCalledWith('https://checkout.paddle.test/txn_2'));
    expect(planCheckoutMock).not.toHaveBeenCalled();
  });

  it('turns a refusal code into something the person clicking can act on', async () => {
    topUpCheckoutMock.mockRejectedValue(new ApiError('topup_requires_paid_plan', 403));
    summaryMock.mockReturnValue(summaryResult({ data: summary() }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: '500 credits' }));

    await waitFor(() => expect(screen.getByText(/Top-up credits are available on paid plans only/)).toBeTruthy());
    expect(assignMock).not.toHaveBeenCalled();
  });

  it('says the workspace already has a subscription when the API refuses a second checkout', async () => {
    planCheckoutMock.mockRejectedValue(new ApiError('already_subscribed', 409));
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));

    await waitFor(() => expect(screen.getByText(/Change it from Plan & billing/)).toBeTruthy());
  });

  it('shows the verify-your-email step when checkout answers 403 EMAIL_NOT_VERIFIED, not a generic error', async () => {
    planCheckoutMock.mockRejectedValue(new ApiError('EMAIL_NOT_VERIFIED', 403, [], { code: 'EMAIL_NOT_VERIFIED', error: 'EMAIL_NOT_VERIFIED', message: 'x' }));
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));

    await waitFor(() => expect(screen.getByText('Confirm your email first')).toBeTruthy());
    expect(screen.queryByText('That did not go through')).toBeNull();
    expect(planCheckoutMock).toHaveBeenCalledTimes(1);
  });

  it('refuses to follow a link that is not http(s) rather than running it in our own origin', async () => {
    planCheckoutMock.mockResolvedValue({ checkoutUrl: 'javascript:alert(1)', transactionId: 'txn_1' });
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));

    await waitFor(() => expect(screen.getByText(/link this page will not follow/)).toBeTruthy());
    expect(assignMock).not.toHaveBeenCalled();
  });

  it('shows a plan but no purchase controls to a member who may not manage billing', () => {
    summaryMock.mockReturnValue(summaryResult({ data: summary({ canManageBilling: false }) }));
    renderPage();

    expect(screen.getByText(/limited to the workspace\s+Owner/)).toBeTruthy();
    expect(screen.getByTestId('plan-creator')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Choose Creator' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^(Upgrade|Switch) to/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Manage in Paddle/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Cancel plan' })).toBeNull();
    // F-06: no seat input whose value could go nowhere.
    expect(screen.queryByLabelText('Seats on the Team plan')).toBeNull();
    expect(screen.queryByRole('button', { name: '1,000 credits' })).toBeNull();
    expect(screen.getByText(/An Owner or a member with the Billing role buys credits/)).toBeTruthy();
  });

  it('explains a deployment without billing instead of showing a failure', () => {
    summaryMock.mockReturnValue(summaryResult({ error: new ApiError('Not Found', 404) }));
    renderPage();

    expect(screen.getByText('Billing is not enabled on this deployment')).toBeTruthy();
    expect(screen.queryByText('Could not load your plan')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(screen.queryByTestId('plans-card')).toBeNull();
  });

  it('offers a retry for a failure that is not a missing feature', () => {
    const mutate = vi.fn();
    summaryMock.mockReturnValue(summaryResult({ error: new Error('Network unreachable'), mutate }));
    renderPage();

    expect(screen.getByText('Could not load your plan')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(mutate).toHaveBeenCalled();
  });

  it('shows a loader, and claims no plan, until the summary arrives', () => {
    summaryMock.mockReturnValue(summaryResult({ isLoading: true }));
    const { container } = renderPage();

    expect(container.querySelector('.mantine-Loader-root')).not.toBeNull();
    expect(screen.queryByTestId('current-plan-card')).toBeNull();
  });

  it('warns when a subscription is ending rather than silently letting it lapse', () => {
    summaryMock.mockReturnValue(summaryResult({ data: summary({
      subscription: {
        planKey: 'pro', planName: 'Pro', status: 'Active', seats: 1,
        periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
        cancelAtPeriodEnd: true, hasBillingCustomer: true,
      },
    }) }));
    renderPage();

    expect(screen.getByTestId('cancel-banner').textContent).toContain(
      `Cancels on ${new Date('2026-10-10T00:00:00Z').toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' })}`,
    );
    expect(screen.getByRole('button', { name: 'Keep my plan' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Cancel plan' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^(Upgrade|Switch) to/ })).toBeNull();
    expect(screen.getAllByText('Keep your plan first to change it.')).toHaveLength(2);
    expect(screen.getByTestId('current-plan-card').textContent).toContain('Access ends');
  });

  it('shows a storage bar with the plan limit and the largest projects', () => {
    STORAGE.result = {
      data: {
        usedBytes: 4_500_000_000,
        limitBytes: 16_106_127_360,
        byProject: [
          { projectId: 'p1', name: 'Launch reel', usedBytes: 3_000_000_000 },
          { projectId: 'p2', name: 'B-roll', usedBytes: 1_500_000_000 },
        ],
      },
    };
    renderPage();

    const card = screen.getByTestId('storage-card');
    expect(card.textContent).toContain('4.2 GB of 15 GB used');
    expect(card.textContent).toContain('Largest projects');
    expect(card.textContent).toContain('Launch reel');
    expect(card.textContent).toContain('B-roll');
  });

  it('says Unlimited instead of a fraction when the plan has no storage limit', () => {
    STORAGE.result = { data: { usedBytes: 4_500_000_000, limitBytes: null, byProject: [] } };
    renderPage();

    const card = screen.getByTestId('storage-card');
    expect(card.textContent).toContain('Unlimited');
    expect(card.textContent).not.toContain(' of ');
  });

  // ------------------------------------------------------------- cancel, keep, change plan (T-004)

  const LONG = (iso: string) =>
    new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });

  /** A Pro workspace with something scheduled or changed, as the summary shows it after a webhook. */
  const pro = (sub: Partial<NonNullable<BillingSummary['subscription']>>, over: Partial<BillingSummary> = {}) =>
    summary({ subscription: { ...summary().subscription!, ...sub }, ...over });

  it('lets a canceled workspace, which keeps its Paddle customer, choose a plan again (F-01)', () => {
    summaryMock.mockReturnValue(summaryResult({
      data: trial({
        subscription: { ...trial().subscription!, hasBillingCustomer: true },
      }),
    }));
    renderPage();

    expect(screen.getByRole('button', { name: 'Choose Creator' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Choose Team' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^(Upgrade|Switch) to/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Cancel plan' })).toBeNull();
    // The canceled plan's invoices are still in Paddle.
    expect((screen.getByRole('button', { name: /Manage in Paddle/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('cancels only after the confirm step, and says done only once the summary shows it', async () => {
    const mutate = vi.fn().mockResolvedValue(pro({ cancelAtPeriodEnd: true, cancelEffectiveAt: '2026-10-10T00:00:00Z' }));
    summaryMock.mockReturnValue(summaryResult({ data: summary(), mutate }));
    cancelMock.mockResolvedValue({ status: 'cancel_requested', effectiveAt: '2026-10-10T00:00:00Z' });
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel plan' }));
    const dialog = await screen.findByRole('dialog', { name: 'Cancel your plan?' });
    expect(dialog.textContent).toContain(`Your plan stays active until ${LONG('2026-10-10T00:00:00Z')}.`);
    expect(cancelMock).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Yes, cancel at period end' }));

    await waitFor(() => expect(cancelMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId('billing-done-notice').textContent)
      .toContain(`Your plan cancels on ${LONG('2026-10-10T00:00:00Z')}`));
    expect(mutate).toHaveBeenCalled();
  });

  it('closes the cancel dialog with Keep my plan and changes nothing', async () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel plan' }));
    const dialog = await screen.findByRole('dialog', { name: 'Cancel your plan?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep my plan' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(cancelMock).not.toHaveBeenCalled();
  });

  it('keeps the plan from the cancel banner through resume', async () => {
    const mutate = vi.fn().mockResolvedValue(pro({ cancelAtPeriodEnd: false }));
    summaryMock.mockReturnValue(summaryResult({
      data: pro({ cancelAtPeriodEnd: true, cancelEffectiveAt: '2026-10-10T00:00:00Z' }),
      mutate,
    }));
    resumeMock.mockResolvedValue({ status: 'resume_requested' });
    renderPage();

    fireEvent.click(within(screen.getByTestId('cancel-banner')).getByRole('button', { name: 'Keep my plan' }));

    await waitFor(() => expect(resumeMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByText('You keep Pro. It renews as usual.')).toBeTruthy());
  });

  it('switches to a cheaper plan from the next billing period, after saying so', async () => {
    const mutate = vi.fn().mockResolvedValue(pro({
      scheduledChange: { planKey: 'creator', planName: 'Creator', seats: 1, effectiveAt: '2026-10-10T00:00:00Z' },
    }));
    summaryMock.mockReturnValue(summaryResult({ data: summary(), mutate }));
    changePlanMock.mockResolvedValue({
      direction: 'downgrade', effective: 'period_end', effectiveAt: '2026-10-10T00:00:00Z',
      creditGrantMillis: 0, seats: 1, message: 'Later.',
    });
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Switch to Creator' }));
    const dialog = await screen.findByRole('dialog', { name: 'Switch to Creator?' });
    expect(dialog.textContent).toContain(`You keep Pro until ${LONG('2026-10-10T00:00:00Z')}. Creator starts with your next billing period.`);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(changePlanMock).toHaveBeenCalledWith('creator', undefined));
    await waitFor(() => expect(screen.getByTestId('billing-done-notice').textContent)
      .toContain(`Creator starts on ${LONG('2026-10-10T00:00:00Z')}`));
  });

  it('upgrades now and, once Paddle confirmed, says the extra credits follow the payment', async () => {
    const team = PLANS.map((p) => ({ ...p, current: p.key === 'team' }));
    const mutate = vi.fn().mockResolvedValue(pro({ planKey: 'team', planName: 'Team', seats: 4 }, { plans: team }));
    summaryMock.mockReturnValue(summaryResult({ data: summary(), mutate }));
    changePlanMock.mockResolvedValue({
      direction: 'upgrade', effective: 'now', effectiveAt: '2026-09-20T00:00:00Z',
      creditGrantMillis: 1_234_000, seats: 4, message: 'Now.',
    });
    renderPage();

    fireEvent.change(screen.getByLabelText('Seats on the Team plan'), { target: { value: '4' } });
    fireEvent.click(screen.getByRole('button', { name: 'Upgrade to Team' }));
    const dialog = await screen.findByRole('dialog', { name: 'Upgrade to Team?' });
    expect(dialog.textContent).toContain('The new plan starts now.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(changePlanMock).toHaveBeenCalledWith('team', 4));
    await waitFor(() => expect(screen.getByTestId('billing-done-notice').textContent)
      .toContain("You're on Team now. Extra credits for the rest of this billing period are added as soon as Paddle confirms the payment."));
    // `creditGrantMillis` is only a request-time estimate: the notice never states a credit number.
    const notice = screen.getByTestId('billing-done-notice').textContent ?? '';
    expect(notice).not.toMatch(/\d/);
    expect(notice).not.toContain('1,234');
  });

  it('shows a scheduled change and keeps the current plan from its banner', async () => {
    const mutate = vi.fn().mockResolvedValue(pro({ scheduledChange: null }));
    summaryMock.mockReturnValue(summaryResult({
      data: pro({ scheduledChange: { planKey: 'creator', planName: 'Creator', seats: 1, effectiveAt: '2026-10-10T00:00:00Z' } }),
      mutate,
    }));
    changePlanMock.mockResolvedValue({
      direction: 'keep', effective: 'now', effectiveAt: null, creditGrantMillis: 0, seats: 1, message: 'Kept.',
    });
    renderPage();

    const banner = screen.getByTestId('scheduled-change-banner');
    expect(banner.textContent).toContain(`Moves to Creator on ${LONG('2026-10-10T00:00:00Z')}`);
    fireEvent.click(within(banner).getByRole('button', { name: 'Keep Pro' }));

    await waitFor(() => expect(changePlanMock).toHaveBeenCalledWith('pro', undefined));
    await waitFor(() => expect(screen.getByText('You keep Pro. The scheduled change is removed.')).toBeTruthy());
  });

  it('offers no other plan change while a change is scheduled, only keeping the current plan', () => {
    summaryMock.mockReturnValue(summaryResult({
      data: pro({ scheduledChange: { planKey: 'creator', planName: 'Creator', seats: 1, effectiveAt: '2026-10-10T00:00:00Z' } }),
    }));
    renderPage();

    expect(screen.queryByRole('button', { name: /^(Upgrade|Switch) to / })).toBeNull();
    expect(screen.getByTestId('plan-creator').textContent).toContain('Keep Pro first to change plans.');
    expect(screen.getByTestId('plan-team').textContent).toContain('Keep Pro first to change plans.');
    expect(screen.queryByLabelText('Seats on the Team plan')).toBeNull();
    expect(within(screen.getByTestId('scheduled-change-banner')).getByRole('button', { name: 'Keep Pro' })).toBeTruthy();
  });

  it('hides the seat control while fewer seats are scheduled, and keeps the Keep button', () => {
    summaryMock.mockReturnValue(summaryResult({
      data: teamSubscription({
        subscription: {
          ...teamSubscription().subscription!, seats: 5,
          scheduledChange: { planKey: 'team', planName: 'Team', seats: 3, effectiveAt: '2026-10-10T00:00:00Z' },
        },
      }),
    }));
    renderPage();

    expect(screen.queryByLabelText('Seats on the current plan')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Update seats' })).toBeNull();
    expect(screen.getByTestId('plan-pro').textContent).toContain('Keep Team first to change plans.');
    expect(within(screen.getByTestId('scheduled-change-banner')).getByRole('button', { name: 'Keep Team' })).toBeTruthy();
  });

  it('shows the banners, without their buttons, to a member who may not manage billing', () => {
    summaryMock.mockReturnValue(summaryResult({
      data: pro(
        {
          cancelAtPeriodEnd: true, cancelEffectiveAt: '2026-10-10T00:00:00Z',
          scheduledChange: { planKey: 'creator', planName: 'Creator', seats: 1, effectiveAt: '2026-10-10T00:00:00Z' },
        },
        { canManageBilling: false },
      ),
    }));
    renderPage();

    expect(screen.getByTestId('cancel-banner')).toBeTruthy();
    expect(screen.getByTestId('scheduled-change-banner')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Keep my plan' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Keep Pro' })).toBeNull();
  });

  it('asks before fewer seats, and says when they take effect', async () => {
    const mutate = vi.fn().mockResolvedValue(teamSubscription({
      subscription: {
        ...teamSubscription().subscription!, seats: 5,
        scheduledChange: { planKey: 'team', planName: 'Team', seats: 3, effectiveAt: '2026-10-10T00:00:00Z' },
      },
    }));
    summaryMock.mockReturnValue(summaryResult({
      data: teamSubscription({ subscription: { ...teamSubscription().subscription!, seats: 5 } }),
      mutate,
    }));
    seatsMock.mockResolvedValue({
      direction: 'downgrade', effective: 'period_end', effectiveAt: '2026-10-10T00:00:00Z',
      creditGrantMillis: 0, seats: 3, message: 'Later.',
    });
    renderPage();

    fireEvent.change(screen.getByLabelText('Seats on the current plan'), { target: { value: '3' } });
    fireEvent.click(screen.getByRole('button', { name: 'Update seats' }));
    const dialog = await screen.findByRole('dialog', { name: `Fewer seats from ${LONG('2026-10-10T00:00:00Z')}` });
    expect(dialog.textContent).toContain('You keep 5 seats until then.');
    expect(seatsMock).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(seatsMock).toHaveBeenCalledWith(3));
    await waitFor(() => expect(screen.getByTestId('billing-done-notice').textContent)
      .toContain(`The workspace moves to 3 seats on ${LONG('2026-10-10T00:00:00Z')}`));
  });

  it('adds seats now and says the extra credits follow the payment, with no credit number', async () => {
    const mutate = vi.fn().mockResolvedValue(teamSubscription({
      subscription: { ...teamSubscription().subscription!, seats: 6 },
    }));
    summaryMock.mockReturnValue(summaryResult({ data: teamSubscription(), mutate }));
    seatsMock.mockResolvedValue({
      direction: 'upgrade', effective: 'now', effectiveAt: '2026-09-20T00:00:00Z',
      creditGrantMillis: 2_500_000, seats: 6, message: 'Now.',
    });
    renderPage();

    fireEvent.change(screen.getByLabelText('Seats on the current plan'), { target: { value: '6' } });
    fireEvent.click(screen.getByRole('button', { name: 'Update seats' }));

    await waitFor(() => expect(seatsMock).toHaveBeenCalledWith(6));
    await waitFor(() => expect(screen.getByTestId('billing-done-notice').textContent)
      .toContain('You have 6 seats now. Extra credits for the rest of this billing period are added as soon as Paddle confirms the payment.'));
    expect(screen.getByTestId('billing-done-notice').textContent).not.toContain('2,500');
  });

  it('opens the payment-method page from a fresh portal session', async () => {
    portalMock.mockResolvedValue({
      url: 'https://portal.paddle.test/overview',
      updatePaymentMethodUrl: 'https://portal.paddle.test/payment',
      cancelUrl: 'https://portal.paddle.test/cancel',
    });
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Update payment method/ }));
    await waitFor(() => expect(assignMock).toHaveBeenCalledWith('https://portal.paddle.test/payment'));

    fireEvent.click(screen.getByRole('button', { name: /Cancel in Paddle/ }));
    await waitFor(() => expect(assignMock).toHaveBeenCalledWith('https://portal.paddle.test/cancel'));
    expect(portalMock).toHaveBeenCalledTimes(2);
  });

  it("shows the server's own sentence when Paddle refuses a change", async () => {
    changePlanMock.mockRejectedValue(new ApiError('Paddle says no.', 409, [], {
      error: 'billing_provider_refused', providerCode: 'subscription_not_active', message: 'Paddle says no.',
    }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Switch to Creator' }));
    const dialog = await screen.findByRole('dialog', { name: 'Switch to Creator?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    await waitFor(() => expect(screen.getByText('Paddle says no.')).toBeTruthy());
    expect(screen.queryByTestId('billing-done-notice')).toBeNull();
  });

  // ------------------------------------------------------- promo discount on plan change (T-048)

  describe('a recurring promo on the subscription', () => {
    /** Creator in force, with a cheaper non-target Starter plan to downgrade to. */
    const creatorWithPromo = (promoDiscount: NonNullable<BillingSummary['subscription']>['promoDiscount']) =>
      summary({
        plans: [
          plan({ key: 'starter', name: 'Starter', priceMonthlyUsd: 9, includedCredits: 300, priceId: 'pri_starter' }),
          ...PLANS.map((p) => ({ ...p, current: p.key === 'creator' })),
        ],
        subscription: { ...summary().subscription!, planKey: 'creator', planName: 'Creator', promoDiscount },
      });
    const PERCENT = { discountType: 'Percentage' as const, amount: '20', currencyCode: null, targetPriceIds: ['pri_creator', 'pri_pro'] };
    const FLAT = { discountType: 'Flat' as const, amount: '500', currencyCode: 'USD', targetPriceIds: ['pri_creator', 'pri_pro'] };

    const openModal = async (button: string, title: string) => {
      fireEvent.click(screen.getByRole('button', { name: button }));
      return screen.findByRole('dialog', { name: title });
    };

    it('says nothing about the promo when the new plan is one it covers', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: creatorWithPromo(PERCENT) }));
      renderPage();

      const dialog = await openModal('Upgrade to Pro', 'Upgrade to Pro?');
      expect(dialog.textContent).toContain('The new plan starts now.');
      expect(dialog.textContent).not.toContain('promo ends with this change');
    });

    it('says a percentage promo ends when the new plan is outside it', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: creatorWithPromo(PERCENT) }));
      renderPage();

      const dialog = await openModal('Upgrade to Team', 'Upgrade to Team?');
      expect(dialog.textContent).toContain('Your 20% promo ends with this change.');
      expect(dialog.textContent).not.toContain('does not bring it back');
    });

    it('formats a flat promo as money', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: creatorWithPromo(FLAT) }));
      renderPage();

      const dialog = await openModal('Upgrade to Team', 'Upgrade to Team?');
      expect(dialog.textContent).toContain('Your $5.00 promo ends with this change.');
    });

    it('on a downgrade, also says keeping the current plan later does not restore it', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: creatorWithPromo(PERCENT) }));
      renderPage();

      const dialog = await openModal('Switch to Starter', 'Switch to Starter?');
      expect(dialog.textContent).toContain('Starter starts with your next billing period.');
      expect(dialog.textContent).toContain(
        'Your 20% promo ends with this change. Keeping Creator later does not bring it back.',
      );
    });

    it('says nothing when the subscription has no promo', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: creatorWithPromo(null) }));
      renderPage();

      const dialog = await openModal('Upgrade to Team', 'Upgrade to Team?');
      expect(dialog.textContent).not.toContain('promo ends with this change');
    });
  });

  // ------------------------------------------------------------------------------ promotions

  it("applies a promo code: Paddle's total shown, its subtotal struck, and the code sent to checkout", async () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    // The code covers Creator only; the Team plan answers that it is not valid there.
    previewMock.mockImplementation(async (req) => {
      if (req.plan === 'creator') {
        PROMO.previews.set(checkoutPreviewKey(req), QUOTE);
        return QUOTE;
      }
      throw new ApiError('promo_code_invalid', 400, [], { error: 'promo_code_invalid' });
    });
    renderPage();

    fireEvent.change(screen.getByTestId('promo-code-input'), { target: { value: 'spring20' } });
    fireEvent.click(screen.getByTestId('promo-code-apply'));

    await waitFor(() => expect(screen.getByTestId('promo-code-card').textContent).toContain('SPRING20'));
    expect(previewMock).toHaveBeenCalledWith({ plan: 'creator', promoCode: 'SPRING20' });
    expect(previewMock).toHaveBeenCalledWith({ plan: 'team', seats: 3, promoCode: 'SPRING20' });

    const creator = screen.getByTestId('plan-creator');
    expect(creator.querySelector('[data-testid="promo-preview-total"]')?.textContent).toBe('€21,60');
    expect(creator.querySelector('[data-testid="promo-preview-subtotal"]')?.textContent).toBe('€27,00');
    expect(creator.textContent).toContain('Spring sale: 20% off');
    expect(screen.getByTestId('plan-team').querySelector('[data-testid="promo-preview-total"]')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));
    await waitFor(() => expect(planCheckoutMock).toHaveBeenCalledWith('creator', undefined, 'SPRING20'));

    fireEvent.click(screen.getByRole('button', { name: 'Choose Team' }));
    await waitFor(() => expect(planCheckoutMock).toHaveBeenLastCalledWith('team', 3));
  });

  it('says a code is not valid when it applies to nothing, and changes no price', async () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    previewMock.mockRejectedValue(new ApiError('promo_code_invalid', 400, [], { error: 'promo_code_invalid' }));
    redeemMock.mockRejectedValue(new ApiError('promo_code_invalid', 400, [], { error: 'promo_code_invalid' }));
    renderPage();

    fireEvent.change(screen.getByTestId('promo-code-input'), { target: { value: 'NOPE1234' } });
    fireEvent.click(screen.getByTestId('promo-code-apply'));

    expect(await screen.findByText("That code isn't valid.")).toBeTruthy();
    expect(screen.queryByTestId('promo-preview-total')).toBeNull();
  });

  it('says to try later when the code attempts are rate limited', async () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    previewMock.mockRejectedValue(new ApiError('promo_rate_limited', 429, [], { error: 'promo_rate_limited' }));
    renderPage();

    fireEvent.change(screen.getByTestId('promo-code-input'), { target: { value: 'GUESS1234' } });
    fireEvent.click(screen.getByTestId('promo-code-apply'));

    expect(await screen.findByText('Too many attempts. Try again later.')).toBeTruthy();
    expect(redeemMock).not.toHaveBeenCalled();
  });

  it('badges a plan an automatic offer covers and shows the quoted discount, with no code', () => {
    summaryMock.mockReturnValue(summaryResult({ data: trial() }));
    PROMO.active = [{ kind: 'planDiscount', headline: 'Launch week: 30% off', endsAt: null, planKeys: ['creator'] }];
    PROMO.previews.set(checkoutPreviewKey({ plan: 'creator' }), {
      ...QUOTE, promotion: { headline: 'Launch week: 30% off', kind: 'planDiscount' },
    });
    renderPage();

    const creator = screen.getByTestId('plan-creator');
    expect(creator.textContent).toContain('Launch week: 30% off');
    expect(creator.querySelector('[data-testid="promo-preview-total"]')?.textContent).toBe('€21,60');
    expect(screen.getByTestId('plan-team').textContent).not.toContain('Launch week');
  });

  it('shows a pack bonus as extra credits, not as a price change', () => {
    PROMO.active = [{ kind: 'packBonus', headline: 'Bonus credits week', endsAt: null, packCredits: [1000] }];
    PROMO.previews.set(checkoutPreviewKey({ topUpCredits: 1000 }), {
      ...QUOTE, subtotal: '€10,00', total: '€10,00',
      promotion: { headline: 'Bonus credits week', kind: 'packBonus' }, bonusCredits: 100,
    });
    renderPage();

    const card = screen.getByTestId('topup-card');
    expect(card.textContent).toContain('Bonus credits week');
    expect(card.textContent).toContain('+100 bonus credits');
    expect(card.querySelector('[data-testid="promo-preview-total"]')).toBeNull();
  });

  it('locks the discount in the overlay when the checkout carries one', async () => {
    const paddle = { Checkout: { open: vi.fn() } } as unknown as Paddle;
    paddleLoadMock.mockResolvedValue(paddle);
    topUpCheckoutMock.mockResolvedValue({
      checkoutUrl: 'https://checkout.paddle.test/txn_2', transactionId: 'txn_2', discount: { headline: 'Pack sale' },
    });
    summaryMock.mockReturnValue(summaryResult({
      data: summary({ paddle: { environment: 'production', clientToken: 'live_abc', customerId: 'ctm_01hbuyer' } }),
    }));
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: '500 credits' }));
    await waitFor(() => expect(overlayMock).toHaveBeenCalledTimes(1));
    expect(overlayMock).toHaveBeenCalledWith(expect.objectContaining({ transactionId: 'txn_2', discounted: true }));
  });

  it('redeems a credit code and states the credits, the expiry and the conditions', async () => {
    const mutate = vi.fn();
    summaryMock.mockReturnValue(summaryResult({ data: summary(), mutate }));
    previewMock.mockRejectedValue(new ApiError('promo_code_invalid', 400, [], { error: 'promo_code_invalid' }));
    redeemMock.mockResolvedValue({ credits: 500, expiresAt: '2027-01-10T00:00:00Z', headline: 'Welcome gift' });
    renderPage();

    fireEvent.change(screen.getByTestId('promo-code-input'), { target: { value: 'welcome50' } });
    fireEvent.click(screen.getByTestId('promo-code-apply'));

    const result = await screen.findByTestId('redeem-code-result');
    expect(redeemMock).toHaveBeenCalledWith('welcome50');
    expect(result.textContent).toContain('500 credits were added');
    expect(result.textContent).toContain(new Date('2027-01-10T00:00:00Z').toLocaleDateString(undefined, {
      year: 'numeric', month: 'long', day: 'numeric',
    }));
    expect(result.textContent).toContain("Promo credits have no cash value and can't be transferred.");
    expect(result.textContent).not.toMatch(/paddle|ledger|millis/i);
    expect(mutate).toHaveBeenCalled();
  });

  it('explains a refused redeem in plain words', async () => {
    previewMock.mockRejectedValue(new ApiError('promo_code_invalid', 400, [], { error: 'promo_code_invalid' }));
    redeemMock.mockRejectedValue(new ApiError('promo_already_redeemed', 409, [], { error: 'promo_already_redeemed' }));
    renderPage();

    fireEvent.change(screen.getByTestId('promo-code-input'), { target: { value: 'WELCOME50' } });
    fireEvent.click(screen.getByTestId('promo-code-apply'));

    expect(await screen.findByText('This workspace has already used that code.')).toBeTruthy();
  });

  it('lists the credit grants with their expiry, soonest first as served', () => {
    summaryMock.mockReturnValue(summaryResult({
      data: summary({
        credits: {
          balance: 120, reserved: 20, available: 100,
          grants: [
            { source: 'promo', remaining: 50, expiresAt: '2026-11-01T00:00:00Z' },
            { source: 'plan', remaining: 70, expiresAt: '2026-12-01T00:00:00Z' },
          ],
        },
      }),
    }));
    renderPage();

    const list = screen.getByTestId('credit-grants-list');
    expect(list.textContent).toContain('Promo credits');
    expect(list.textContent).toContain('Plan credits');
    const promoDate = new Date('2026-11-01T00:00:00Z').toLocaleDateString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
    });
    expect(list.textContent).toContain(`expires ${promoDate}`);
    expect(list.textContent!.indexOf('Promo credits')).toBeLessThan(list.textContent!.indexOf('Plan credits'));
  });

  it('offers no code or redeem controls to a member who may not manage billing', () => {
    summaryMock.mockReturnValue(summaryResult({ data: summary({ canManageBilling: false }) }));
    renderPage();

    expect(screen.queryByTestId('promo-code-input')).toBeNull();
    expect(screen.queryByTestId('redeem-code-input')).toBeNull();
  });

  describe('yearly billing', () => {
    const YEARLY_PLANS: BillingPlan[] = PLANS.map((p) => ({
      ...p,
      priceAnnualUsd: p.priceMonthlyUsd * 10,
      annualPriceId: `${p.priceId}_year`,
    }));
    const annualSub = (interval: 'monthly' | 'annual') => summary({
      plans: YEARLY_PLANS,
      subscription: {
        planKey: 'pro', planName: 'Pro', status: 'Active', seats: 1, interval,
        periodStart: '2026-09-10T00:00:00Z', periodEnd: interval === 'annual' ? '2027-09-10T00:00:00Z' : '2026-10-10T00:00:00Z',
        cancelAtPeriodEnd: false, hasBillingCustomer: true,
      },
    });

    it('shows no switch when no plan has a yearly price and id (monthly flows unchanged)', () => {
      renderPage();
      expect(screen.queryByTestId('interval-switch')).toBeNull();
    });

    it('shows the per-month equivalent, the yearly total and 2 months free in the yearly view', () => {
      summaryMock.mockReturnValue(summaryResult({ data: trial({ plans: YEARLY_PLANS }) }));
      renderPage();
      expect(screen.getByRole('radio', { name: 'Monthly' })).toBeTruthy();
      fireEvent.click(screen.getByRole('radio', { name: 'Annual' }));
      const card = screen.getByTestId('plan-creator');
      expect(card.textContent).toContain('$24.17');
      expect(screen.getByTestId('plan-creator-yearly-total').textContent).toContain('$290.00');
      expect(screen.getByTestId('plan-creator-yearly-total').textContent).toContain('2 months free.');
      expect(card.textContent).toContain('1,000 credits every month');
      expect(card.textContent).toContain('Credits arrive every month');
    });

    it('sends the yearly interval on checkout, and nothing extra on a monthly one', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: trial({ plans: YEARLY_PLANS }) }));
      renderPage();
      fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));
      await waitFor(() => expect(planCheckoutMock).toHaveBeenLastCalledWith('creator', undefined));
      fireEvent.click(screen.getByRole('radio', { name: 'Annual' }));
      fireEvent.click(screen.getByRole('button', { name: 'Choose Creator' }));
      await waitFor(() => expect(planCheckoutMock).toHaveBeenLastCalledWith('creator', undefined, undefined, 'annual'));
    });

    it('opens on Annual for a yearly subscriber, disables Monthly and upgrades keep yearly', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: annualSub('annual') }));
      changePlanMock.mockResolvedValue({
        direction: 'upgrade', effective: 'now', effectiveAt: '2027-09-10T00:00:00Z',
        creditGrantMillis: 0, seats: 4, message: 'Now.',
      });
      renderPage();
      expect((screen.getByRole('radio', { name: 'Monthly' }) as HTMLInputElement).disabled).toBe(true);
      expect((screen.getByRole('radio', { name: 'Annual' }) as HTMLInputElement).checked).toBe(true);
      expect(screen.getByTestId('interval-annual-note').textContent).toContain('cancel it at renewal');
      fireEvent.click(screen.getByRole('button', { name: 'Upgrade to Team' }));
      const dialog = await screen.findByRole('dialog');
      fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));
      await waitFor(() => expect(changePlanMock).toHaveBeenCalledWith('team', 3, 'annual'));
    });

    it('lets a monthly subscriber start the yearly plan after a confirm that says so', async () => {
      summaryMock.mockReturnValue(summaryResult({ data: annualSub('monthly') }));
      changePlanMock.mockResolvedValue({
        direction: 'upgrade', effective: 'now', effectiveAt: '2026-09-20T00:00:00Z',
        creditGrantMillis: 0, seats: 1, message: 'Now.',
      });
      renderPage();
      fireEvent.click(screen.getByRole('radio', { name: 'Annual' }));
      fireEvent.click(screen.getByRole('button', { name: 'Start Pro yearly' }));
      const dialog = await screen.findByRole('dialog', { name: 'Start the yearly plan for Pro?' });
      expect(dialog.textContent).toContain('starts a yearly plan for Pro now');
      expect(dialog.textContent).toContain('shows the exact amount');
      fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));
      await waitFor(() => expect(changePlanMock).toHaveBeenCalledWith('pro', undefined, 'annual'));
    });

    it('asks Paddle for the yearly price ids in the yearly view', () => {
      summaryMock.mockReturnValue(summaryResult({ data: trial({ plans: YEARLY_PLANS }) }));
      renderPage();
      expect(pricesMock).toHaveBeenLastCalledWith(null, YEARLY_PLANS, 'monthly');
      fireEvent.click(screen.getByRole('radio', { name: 'Annual' }));
      expect(pricesMock).toHaveBeenLastCalledWith(null, YEARLY_PLANS, 'annual');
    });
  });

  describe('marketing opt-in', () => {
    it('is not offered on the billing page (it lives under Account > Marketing emails)', () => {
      MKT.consent = { status: 'none' };
      MKT.wording = { version: '2026-10-marketing-v1', text: 'Email me ReelBolt product news.' };
      summaryMock.mockReturnValue(summaryResult({ data: trial() }));
      renderPage();

      expect(screen.queryByTestId('marketing-optin-checkbox')).toBeNull();
    });
  });
  describe('usability fixes (A12 A13 A14 A18)', () => {
    it('shows the Team total for the seat count and names the stepper buttons', () => {
      summaryMock.mockReturnValue(summaryResult({ data: trial() }));
      renderPage();

      const team = screen.getByTestId('plan-team');
      expect(screen.getByTestId('plan-team-seat-total').textContent).toContain('3 seats = $147');
      fireEvent.click(within(team).getByRole('button', { name: 'More seats on the Team plan' }));
      expect(screen.getByTestId('plan-team-seat-total').textContent).toContain('4 seats = $196');
      expect(within(team).getByRole('button', { name: 'Fewer seats on the Team plan' })).toBeTruthy();
      expect(within(team).getByTestId('plan-team-features').textContent).toContain('3 seats minimum');
    });

    it('hides the first-video card once a paid plan is bought, and keeps it on Trial', () => {
      const grant = { credits: 100, remaining: 100, expiresAt: '2027-03-04T00:00:00Z' } as unknown as BillingSummary['firstVideoGrant'];
      summaryMock.mockReturnValue(summaryResult({ data: summary({ firstVideoGrant: grant }) }));
      const paid = renderPage();
      expect(screen.queryByTestId('first-video-grant-card')).toBeNull();
      paid.unmount();

      summaryMock.mockReturnValue(summaryResult({ data: trial({ firstVideoGrant: grant }) }));
      renderPage();
      expect(screen.getByTestId('first-video-grant-card')).toBeTruthy();
    });

    it('prints the provider quote on every top-up pack, with no code applied', () => {
      PROMO.previews.set(checkoutPreviewKey({ topUpCredits: 1000 }), { ...QUOTE, subtotal: '$35.00', total: '$35.00', promotion: null });
      renderPage();

      expect(screen.getByTestId('topup-card').querySelector('[data-testid="pack-price"]')?.textContent).toContain('$35.00');
    });

    it('shows the workspace\'s own seats in the total on the current Team plan, not the 3-seat minimum', () => {
      summaryMock.mockReturnValue(summaryResult({
        data: teamSubscription({ subscription: { ...teamSubscription().subscription!, seats: 5 } }),
      }));
      renderPage();

      expect(screen.getByTestId('plan-team-seat-total').textContent).toContain('5 seats = $245');
    });

    it('shows the same seats for a member who cannot manage billing (no stepper)', () => {
      summaryMock.mockReturnValue(summaryResult({
        data: teamSubscription({ canManageBilling: false, subscription: { ...teamSubscription().subscription!, seats: 5 } }),
      }));
      renderPage();

      expect(screen.queryByRole('button', { name: 'More seats on the Team plan' })).toBeNull();
      expect(screen.getByTestId('plan-team-seat-total').textContent).toContain('5 seats = $245');
    });

    it('shows no Team total where the plan is neither current nor adjustable', () => {
      summaryMock.mockReturnValue(summaryResult({ data: summary({ canManageBilling: false }) }));
      renderPage();

      expect(screen.queryByTestId('plan-team-seat-total')).toBeNull();
    });

    it('shows a placeholder while a pack price loads and a quiet hint when it fails', () => {
      PROMO.mode = 'loading';
      const loading = renderPage();
      expect(screen.getAllByTestId('pack-price-loading')).toHaveLength(3);
      loading.unmount();

      PROMO.mode = 'error';
      renderPage();
      expect(screen.getAllByTestId('pack-price-unavailable')[0].textContent).toBe('Price shown at checkout');
    });

    it('explains the disabled Manage button on a workspace that has not bought anything', () => {
      summaryMock.mockReturnValue(summaryResult({ data: trial() }));
      renderPage();

      const manage = screen.getByTestId('manage-in-paddle') as HTMLButtonElement;
      expect(manage.disabled).toBe(true);
      expect(manage.closest('span')?.getAttribute('tabindex')).toBe('0');
    });

    it('offers one code field, not two', () => {
      renderPage();
      expect(screen.getAllByRole('textbox', { name: /code/i })).toHaveLength(1);
      expect(screen.queryByText('Redeem a code')).toBeNull();
    });
  });
});
