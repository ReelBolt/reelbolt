/**
 * (a) Subscribe through Stripe's hosted checkout: Choose Creator → the Stripe fake's hosted page → Pay now →
 * back in the app on /app/checkout?session_id=… → plan Creator, Active, with the plan's credits granted
 * exactly once even when every webhook of the purchase is delivered a second time. Also pins what the
 * Inference API sent: the pinned API version, the Checkout Session's shape (no payment_method_types, the
 * organization on the session and on the subscription) and the idempotent customer.
 *
 * Needs a stack started with `e2e/scripts/up-isolated.sh --provider stripe`:
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/subscribe.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, loadStack, planIncludedCredits, planPriceMinor, stripeMock, Throwaways, captureCheckout } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-sub');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

test('Choose Creator → hosted checkout → pay → Creator is Active, its credits granted once', async ({ page }) => {
  test.setTimeout(120_000);
  const t = await users.create('creator');
  await users.login(page, t);
  await page.goto('/app/settings/billing');
  const before = (await billingSummary(page)).credits.balance;

  const captured = await captureCheckout(page, () => page.getByTestId('plan-creator-choose').click());
  expect(captured.status).toBe(200);
  const checkout = captured.body;
  expect(checkout.transactionId, 'the Checkout Session id').toMatch(/^cs_test_/);
  expect(checkout.checkoutUrl).toBe(`${loadStack().stripeMockUrl}/pay/${checkout.transactionId}`);

  await test.step('the hosted page shows the session and the catalog price', async () => {
    await page.waitForURL(/\/pay\/cs_test_/);
    await expect(page.getByTestId('mock-checkout-session')).toHaveText(checkout.transactionId);
    await expect(page.getByTestId('mock-checkout-total')).toHaveText((planPriceMinor('creator') / 100).toFixed(2));
    await expect(page.getByTestId('mock-checkout-status')).toHaveText('open');
    await expect(page.getByTestId('mock-checkout-brand-name'), 'the display name from branding_settings').toHaveText('ReelBolt');
    await expect(page.getByTestId('mock-checkout-brand-name')).toHaveCSS('background-color', 'rgb(11, 11, 18)');
  });

  await test.step('Pay now → back in the app', async () => {
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?session_id=cs_test_/);
  });

  await test.step('what the Inference API sent to Stripe', async () => {
    const session = (await stripeMock.calls({ path: '/v1/checkout/sessions', method: 'POST' })).filter((c) => c.body?.client_reference_id === t.orgId).at(-1);
    expect(session, 'POST /v1/checkout/sessions for this organization').toBeTruthy();
    expect(session!.stripeVersion).toBe('2026-09-30.endive');
    expect(session!.status).toBe(200);
    expect(session!.body).toMatchObject({ mode: 'subscription', ui_mode: 'hosted_page', line_items: [{ price: 'price_e2e_creator', quantity: '1' }] });
    expect(session!.body.metadata.organization_id).toBe(t.orgId);
    expect(session!.body.subscription_data.metadata.organization_id).toBe(t.orgId);
    expect(session!.body, 'payment_method_types was removed in the pinned API version').not.toHaveProperty('payment_method_types');
    expect(session!.body.success_url).toContain('/app/checkout?session_id={CHECKOUT_SESSION_ID}');
    // Checkout branding (Billing__Stripe__Branding__*, the production defaults in docker-compose.e2e.yml). The form
    // body is parsed into nested keys by the fake, which refused anything Stripe would not accept.
    expect(session!.body.branding_settings).toEqual({
      background_color: '#0B0B12', button_color: '#7C3AED', border_style: 'rectangular', font_family: 'chakra_petch', display_name: 'ReelBolt',
      logo: { type: 'url', url: 'https://reelbolt.ai/brand/stripe-logo.png' },
      icon: { type: 'url', url: 'https://reelbolt.ai/brand/stripe-icon.png' },
    });
    // The shipped default is Stripe Managed Payments (E2E_STRIPE_MANAGED_PAYMENTS, default true): Stripe is the
    // seller, so the session carries managed_payments and none of the parameters Managed Payments forbids. With the
    // flag set to false the stack runs Stripe Tax mode instead: tax is calculated, and Checkout saves the address and
    // name it collects back to the customer, which Stripe Tax needs for any later session of this customer.
    const managedPayments = (process.env.E2E_STRIPE_MANAGED_PAYMENTS ?? 'true') !== 'false';
    if (managedPayments) {
      expect(session!.body.managed_payments).toEqual({ enabled: 'true' });
      for (const forbidden of ['automatic_tax', 'customer_update', 'tax_id_collection', 'invoice_creation']) {
        expect(session!.body, `${forbidden} is refused under Managed Payments`).not.toHaveProperty(forbidden);
      }
    } else {
      expect(session!.body).not.toHaveProperty('managed_payments');
      expect(session!.body.automatic_tax).toEqual({ enabled: 'true' });
      expect(session!.body.customer_update).toEqual({ address: 'auto', name: 'auto' });
    }

    const customer = (await stripeMock.calls({ path: '/v1/customers', method: 'POST' })).find((c) => c.body?.metadata?.organization_id === t.orgId);
    expect(customer?.idempotencyKey, 'one customer per organization and address, even on a retry').toMatch(new RegExp(`^customer:${t.orgId}:[0-9a-f]{16}$`));
    const saved = (await stripeMock.state()).customers.find((c) => c.metadata?.organization_id === t.orgId);
    if (!managedPayments) {
      expect(saved?.address, 'Checkout saved the collected address on the customer (customer_update[address]=auto)').toBeTruthy();
    }
  });

  await test.step('Creator is Active and its credits arrive exactly once', async () => {
    await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.status}`, 'creator/Active');
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { message: 'the period refill', timeout: 20_000 })
      .toBe(before + planIncludedCredits('creator'));
  });

  await test.step('every webhook of the purchase again → nothing more is granted', async () => {
    const sub = (await stripeMock.liveSubscriptionOf(t.orgId!))!;
    await expect.poll(async () => (await stripeMock.webhooks()).filter((w) => w.subscription_id === sub.id && w.delivered).map((w) => w.event_type).sort(),
      { message: 'the three webhooks of the purchase were delivered', timeout: 20_000 })
      .toEqual(['checkout.session.completed', 'customer.subscription.created', 'invoice.paid']);
    const settled = await billingSummary(page);
    for (const w of (await stripeMock.webhooks()).filter((x) => x.subscription_id === sub.id && x.delivered)) {
      const again = await stripeMock.redeliver(w.id);
      expect(again.status, `redelivery of ${w.event_type}`).toBe(200);
    }
    const after = await billingSummary(page);
    expect(after.credits.balance).toBe(settled.credits.balance);
    expect(after.subscription).toEqual(settled.subscription);
  });
});
