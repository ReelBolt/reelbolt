// Offline unit tests for the Stripe fake: `node --test e2e/stripe-mock/server.test.mjs` (no docker, no network
// beyond loopback). They pin what the Inference API relies on: Stripe's form syntax, error shapes, signed
// webhooks (verified here with the same algorithm as StripeSignature.cs), the subscription / upgrade /
// downgrade / cancel flows, the idempotency cache and the refund events.

import test from 'node:test';
import assert from 'node:assert/strict';
import { createHmac, timingSafeEqual } from 'node:crypto';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { API_VERSION, createMock, parseForm, signPayload } from './server.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const catalog = JSON.parse(readFileSync(resolve(here, '../../inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json'), 'utf8'));
const minor = (units) => Math.round(units * 100);
const PRICE = Object.fromEntries(catalog.plans.filter((p) => p.priceMonthly > 0).map((p) => [p.id, minor(p.priceMonthly)]));
const PACK = Object.fromEntries(catalog.topUp.packs.map((p) => [p.credits, minor(p.price)]));

const KEY = 'sk_test_e2e_mock_unit';
const SECRET = 'whsec_e2e_unit_secret';
const ORG = '11111111-1111-1111-1111-111111111111';

// ------------------------------------------------------------------ the independent half of the contract

/** Encodes like StripeForm.cs: a[b][0][c], scalar lists as x[], booleans lower-case, null skipped, "" kept. */
function encodeForm(obj) {
  const pairs = [];
  const add = (key, v) => {
    if (v === null || v === undefined) return;
    if (Array.isArray(v)) {
      v.forEach((item, i) => add(typeof item === 'object' && item !== null ? `${key}[${i}]` : `${key}[]`, item));
    } else if (typeof v === 'object') {
      for (const [k, x] of Object.entries(v)) add(`${key}[${k}]`, x);
    } else {
      pairs.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(v))}`);
    }
  };
  for (const [k, v] of Object.entries(obj)) add(k, v);
  return pairs.join('&');
}

/** StripeSignature.Verify: signed payload "{t}.{raw}", HMAC-SHA256 hex, compare every v1, ignore other schemes. */
function verifySignature(raw, header, secrets, tolerance = 300, now = Math.floor(Date.now() / 1000)) {
  let t = null;
  const v1 = [];
  for (const part of String(header).split(',')) {
    const eq = part.indexOf('=');
    if (eq <= 0) continue;
    const k = part.slice(0, eq).trim();
    const v = part.slice(eq + 1).trim();
    if (k === 't') t = v;
    else if (k === 'v1') v1.push(v);
  }
  if (t === null || !/^\d+$/.test(t) || v1.length === 0) return false;
  if (Math.abs(now - Number(t)) > tolerance) return false;
  let match = false;
  for (const secret of secrets) {
    const expected = createHmac('sha256', secret).update(`${t}.${raw}`).digest();
    for (const candidate of v1) {
      const given = Buffer.from(candidate, 'hex');
      if (given.length === expected.length) match = timingSafeEqual(expected, given) || match;
    }
  }
  return match;
}

// ------------------------------------------------------------------ harness

async function harness(options = {}) {
  const received = [];
  let respondWith = 200;
  const receiver = createServer((req, res) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8');
      received.push({ raw, headers: req.headers, event: JSON.parse(raw), status: respondWith, valid: verifySignature(raw, req.headers['stripe-signature'], [SECRET]) });
      res.writeHead(respondWith);
      res.end('ok');
    });
  });
  await new Promise((ok) => receiver.listen(0, '127.0.0.1', ok));
  const receiverUrl = `http://127.0.0.1:${receiver.address().port}/api/v1/billing/webhooks/stripe`;

  const mock = createMock({
    apiKey: KEY, webhookSecret: SECRET, webhookUrl: receiverUrl, webhookDelayMs: 0, webhookRetryDelayMs: 1, quiet: true,
    publicUrl: 'http://pay.test', appPublicUrl: 'http://app.test', ...options,
  });
  const port = await mock.listen(0);
  const base = `http://127.0.0.1:${port}`;

  const api = async (method, path, form, { headers = {}, key = KEY, raw } = {}) => {
    const init = { method, headers: { ...headers } };
    if (key) init.headers.authorization = `Bearer ${key}`;
    let url = base + path;
    if (method === 'GET' || method === 'DELETE') {
      if (form) url += `?${encodeForm(form)}`;
    } else {
      init.headers['content-type'] ??= 'application/x-www-form-urlencoded';
      init.body = raw ?? encodeForm(form ?? {});
    }
    const res = await fetch(url, init);
    return { status: res.status, headers: res.headers, body: await res.json() };
  };
  const control = async (method, path, body) => {
    const res = await fetch(`${base}/__mock${path}`, { method, headers: body ? { 'content-type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
    return res.json();
  };
  const ok = async (method, path, form, extra) => {
    const r = await api(method, path, form, extra);
    assert.equal(r.status, 200, `${method} ${path} -> ${r.status} ${JSON.stringify(r.body)}`);
    return r.body;
  };
  const eventsOf = (type) => received.filter((r) => r.event.type === type).map((r) => r.event);

  return {
    mock, base, api, ok, control, received, eventsOf,
    setReceiverStatus: (s) => { respondWith = s; },
    settle: () => mock.idle(),
    async close() {
      await mock.idle();
      await mock.close();
      await new Promise((r) => receiver.close(r));
    },
  };
}

/**
 * The exact request body StripeBillingProvider.CreateCheckoutAsync sends: hosted page; with Managed Payments off
 * (the e2e stack) Stripe Tax plus customer_update so Checkout saves the collected address and name back to the
 * customer, with Managed Payments on only `managed_payments[enabled]` (it forbids the other three).
 */
function checkoutForm({ customer, price, quantity = 1, mode = 'subscription', metadata = {}, discount, managed = false }) {
  return {
    mode, ui_mode: 'hosted_page', integration_identifier: 'reelbolt-checkout-qxmvbtrk', customer, client_reference_id: ORG,
    line_items: [{ price, quantity }], metadata,
    [mode === 'payment' ? 'payment_intent_data' : 'subscription_data']: { metadata },
    success_url: 'http://app.test/app/checkout?session_id={CHECKOUT_SESSION_ID}', cancel_url: 'http://app.test/app/settings/billing',
    ...(discount ? { discounts: [{ coupon: discount }] } : {}),
    ...(managed ? { managed_payments: { enabled: true } } : { automatic_tax: { enabled: true }, customer_update: { address: 'auto', name: 'auto' } }),
  };
}

/** The body StripeBillingProvider.ChangePlanAsync sends for a one-item subscription (immediate = an upgrade or seat increase). */
function changeForm(item, price, { quantity = 1, immediate = true, clearDiscount = false, prorationDate } = {}) {
  return {
    items: [{ id: item.id, price, quantity }],
    proration_behavior: immediate ? 'always_invoice' : 'none',
    ...(immediate ? { payment_behavior: 'error_if_incomplete' } : {}),
    ...(clearDiscount ? { discounts: '' } : {}),
    ...(prorationDate ? { proration_date: prorationDate } : {}),
  };
}

async function subscribe(h, { plan = 'creator', quantity = 1, discount } = {}) {
  const customer = await h.ok('POST', '/v1/customers', { email: 'owner@e2e.test', metadata: { organization_id: ORG } }, { headers: { 'idempotency-key': `customer:${ORG}:${Math.random()}` } });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: `price_e2e_${plan}`, quantity, metadata: { organization_id: ORG, user_id: 'u1' }, discount }));
  const done = await h.control('POST', '/complete-checkout', { session_id: session.id });
  assert.ok(done.subscription, JSON.stringify(done));
  return { customer, session, sub: done.subscription };
}

// ------------------------------------------------------------------ form syntax

test('parseForm: Stripe nesting, arrays, empty strings, decoding, no prototype pollution', () => {
  assert.deepEqual(parseForm('a[b][c]=1&a[b][d]=2&a[e]=x'), { a: { b: { c: '1', d: '2' }, e: 'x' } });
  assert.deepEqual(parseForm('items[0][id]=si_1&items[0][price]=p&items[1][price]=q&expand[]=a&expand[]=b'), {
    items: [{ id: 'si_1', price: 'p' }, { price: 'q' }], expand: ['a', 'b'],
  });
  assert.deepEqual(parseForm('discounts='), { discounts: '' });
  assert.deepEqual(parseForm('metadata[note]=a%20b+c%26d'), { metadata: { note: 'a b c&d' } });
  assert.deepEqual(parseForm('created[gte]=10&payment[type]=payment_intent'), { created: { gte: '10' }, payment: { type: 'payment_intent' } });
  assert.deepEqual(parseForm('__proto__[x]=1&a[constructor][y]=2&z=3'), { a: {}, z: '3' });
  assert.equal({}.x, undefined);
});

test('signPayload carries t, v1 and the dummy v0, and v1 verifies like StripeSignature.Verify', () => {
  const header = signPayload('{"a":1}', SECRET, 1_700_000_000);
  assert.match(header, /^t=1700000000,v1=[0-9a-f]{64},v0=[0-9a-f]{64}$/);
  assert.ok(verifySignature('{"a":1}', header, [SECRET], 300, 1_700_000_100));
  assert.ok(!verifySignature('{"a":2}', header, [SECRET], 300, 1_700_000_100), 'tampered body');
  assert.ok(!verifySignature('{"a":1}', header, ['whsec_other'], 300, 1_700_000_100), 'wrong secret');
  assert.ok(!verifySignature('{"a":1}', header, [SECRET], 300, 1_700_001_000), 'stale timestamp');
  assert.ok(!verifySignature('{"a":1}', `t=1700000000,v0=${header.split('v0=')[1]}`, [SECRET], 300, 1_700_000_100), 'v0 alone is not a signature');
});

// ------------------------------------------------------------------ API surface

test('auth, content type and unknown parameters are refused with Stripe-shaped errors', async (t) => {
  const h = await harness();
  t.after(() => h.close());

  const noKey = await h.api('GET', '/v1/prices/price_e2e_creator', undefined, { key: null });
  assert.equal(noKey.status, 401);
  assert.equal(noKey.body.error.type, 'invalid_request_error');
  assert.match(noKey.body.error.message, /did not provide an API key/);

  const wrong = await h.api('GET', '/v1/prices/price_e2e_creator', undefined, { key: 'sk_test_other' });
  assert.equal(wrong.status, 401);
  assert.doesNotMatch(JSON.stringify(wrong.body), /sk_test_other/, 'the rejected key is masked');

  const basic = await fetch(`${h.base}/v1/prices/price_e2e_creator`, { headers: { authorization: `Basic ${Buffer.from(`${KEY}:`).toString('base64')}` } });
  assert.equal(basic.status, 401, 'only Bearer is accepted: the provider sends Bearer');

  const json = await h.api('POST', '/v1/customers', undefined, { headers: { 'content-type': 'application/json' }, raw: '{"email":"a@b.c"}' });
  assert.equal(json.status, 400);
  assert.match(json.body.error.message, /x-www-form-urlencoded/);

  const unknown = await h.api('POST', '/v1/customers', { email: 'a@b.c', bogus: '1' });
  assert.equal(unknown.status, 400);
  assert.equal(unknown.body.error.code, 'parameter_unknown');
  assert.equal(unknown.body.error.param, 'bogus');

  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const removed = await h.api('POST', '/v1/checkout/sessions', { ...checkoutForm({ customer: customer.id, price: 'price_e2e_creator' }), payment_method_types: ['card'] });
  assert.equal(removed.status, 400, 'payment_method_types was removed in Endive');
  assert.equal(removed.body.error.param, 'payment_method_types');

  const missing = await h.api('GET', '/v1/prices/price_nope');
  assert.equal(missing.status, 404);
  assert.equal(missing.body.error.code, 'resource_missing');
  assert.equal(missing.body.error.param, 'id');
});

test('the calls log records version, idempotency key and the parsed form, never the key', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await h.ok('POST', '/v1/customers', { email: 'a@b.c', metadata: { organization_id: ORG } }, { headers: { 'stripe-version': API_VERSION, 'idempotency-key': 'customer:x' } });
  const { calls } = await h.control('GET', '/calls?path=/v1/customers&method=POST');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].stripeVersion, API_VERSION);
  assert.equal(calls[0].idempotencyKey, 'customer:x');
  assert.deepEqual(calls[0].body, { email: 'a@b.c', metadata: { organization_id: ORG } });
  assert.equal(calls[0].status, 200);
  assert.doesNotMatch(JSON.stringify(calls), new RegExp(KEY));
});

test('prices come from plans.catalog.json: no amount is typed in the fake', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  for (const [plan, cents] of Object.entries(PRICE)) {
    const price = await h.ok('GET', `/v1/prices/price_e2e_${plan}`);
    assert.equal(price.unit_amount, cents);
    assert.equal(price.currency, catalog.currency.toLowerCase());
    assert.deepEqual(price.recurring, { interval: 'month', interval_count: 1 });
    assert.equal(price.product, `prod_e2e_${plan}`);
  }
  for (const [credits, cents] of Object.entries(PACK)) {
    const price = await h.ok('GET', `/v1/prices/price_e2e_topup_${credits}`);
    assert.equal(price.unit_amount, cents);
    assert.equal(price.recurring, null);
  }
});

// ------------------------------------------------------------------ checkout and subscriptions

test('subscription checkout: hosted page, 303 to success_url, then signed subscription.created, invoice.paid, session.completed', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'owner@e2e.test', metadata: { organization_id: ORG } });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_creator', metadata: { organization_id: ORG, user_id: 'u1' } }));
  assert.match(session.id, /^cs_test_/);
  assert.equal(session.url, `http://pay.test/pay/${session.id}`);
  assert.equal(session.status, 'open');
  assert.equal(session.amount_total, PRICE.creator);

  const pageRes = await fetch(`${h.base}/pay/${session.id}`);
  const html = await pageRes.text();
  assert.equal(pageRes.status, 200);
  assert.match(html, /data-testid="mock-pay"/);

  const pay = await fetch(`${h.base}/pay/${session.id}`, { method: 'POST', redirect: 'manual' });
  assert.equal(pay.status, 303);
  assert.equal(pay.headers.get('location'), `http://app.test/app/checkout?session_id=${session.id}`);
  await h.settle();

  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.created', 'invoice.paid', 'checkout.session.completed']);
  for (const r of h.received) {
    assert.ok(r.valid, `${r.event.type} carries a valid signature`);
    assert.match(r.headers['stripe-signature'], /v0=/);
    assert.equal(r.event.api_version, API_VERSION);
    assert.equal(r.event.livemode, false);
    assert.match(r.event.id, /^evt_/);
    assert.equal(r.event.object, 'event');
  }

  const sub = h.received[0].event.data.object;
  assert.equal(sub.status, 'active');
  assert.equal(sub.customer, customer.id);
  assert.deepEqual(sub.metadata, { organization_id: ORG, user_id: 'u1' }, 'subscription_data[metadata], not the session metadata');
  assert.equal(sub.current_period_start, undefined, 'basil+: no period on the subscription');
  const item = sub.items.data[0];
  assert.equal(item.price.id, 'price_e2e_creator');
  assert.equal(item.quantity, 1);
  assert.ok(item.current_period_end > item.current_period_start, 'periods live on the item');

  const inv = h.received[1].event.data.object;
  assert.equal(inv.billing_reason, 'subscription_create');
  assert.equal(inv.status, 'paid');
  assert.equal(inv.amount_paid, PRICE.creator);
  assert.equal(inv.parent.subscription_details.subscription, sub.id);
  assert.equal(inv.parent.subscription_details.metadata.organization_id, ORG);
  assert.equal(inv.lines.data[0].pricing.price_details.price, 'price_e2e_creator');
  assert.equal(inv.lines.data[0].parent.subscription_item_details.proration, false);

  const completed = h.received[2].event.data.object;
  assert.equal(completed.mode, 'subscription');
  assert.equal(completed.subscription, sub.id);
  assert.equal(completed.client_reference_id, ORG);

  const read = await h.ok('GET', `/v1/checkout/sessions/${session.id}`);
  assert.equal(read.status, 'complete');
  assert.equal(read.payment_status, 'paid');
});

test('expire an open session; a second expire and paying it are refused', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_creator' }));
  const expired = await h.ok('POST', `/v1/checkout/sessions/${session.id}/expire`, {});
  assert.equal(expired.status, 'expired');
  const again = await h.api('POST', `/v1/checkout/sessions/${session.id}/expire`, {});
  assert.equal(again.status, 400);
  const pay = await fetch(`${h.base}/pay/${session.id}`, { method: 'POST', redirect: 'manual' });
  assert.equal(pay.status, 400);
  assert.match(await pay.text(), /mock-checkout-error/);
});

test('Stripe Tax mode (Managed Payments off) accepts automatic_tax and customer_update and saves the collected address back', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  assert.equal(customer.address, null);
  const tax = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_creator' }));
  assert.equal(tax.managed_payments, null);
  assert.deepEqual(tax.automatic_tax, { enabled: true, status: null });
  assert.deepEqual(tax.customer_update, { address: 'auto', name: 'auto' });

  await h.control('POST', '/complete-checkout', { session_id: tax.id });
  const saved = await h.ok('GET', `/v1/customers/${customer.id}`);
  assert.equal(saved.address.country, 'IT', 'customer_update[address]=auto stored what Checkout collected');
  assert.equal(saved.name, 'Mock Buyer', 'customer_update[name]=auto too');

  // With an address on file, a later session needs no customer_update.
  const later = await h.api('POST', '/v1/checkout/sessions', { ...checkoutForm({ customer: customer.id, price: 'price_e2e_creator' }), customer_update: undefined });
  assert.equal(later.status, 200, JSON.stringify(later.body));
});

test('Stripe Tax on an existing customer without an address needs customer_update[address]=auto; customer_update needs a customer', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const base = checkoutForm({ customer: customer.id, price: 'price_e2e_creator' });

  const noUpdate = await h.api('POST', '/v1/checkout/sessions', { ...base, customer_update: undefined });
  assert.equal(noUpdate.status, 400);
  assert.equal(noUpdate.body.error.param, 'automatic_tax');
  assert.match(noUpdate.body.error.message, /customer_update\[address\]/);

  const nameOnly = await h.api('POST', '/v1/checkout/sessions', { ...base, customer_update: { name: 'auto' } });
  assert.equal(nameOnly.status, 400, 'a name alone gives Stripe Tax no location');

  const noCustomer = await h.api('POST', '/v1/checkout/sessions', { ...base, customer: undefined, customer_email: 'a@b.c' });
  assert.equal(noCustomer.status, 400);
  assert.equal(noCustomer.body.error.param, 'customer_update');
  const emailOnly = await h.api('POST', '/v1/checkout/sessions', { ...base, customer: undefined, customer_email: 'a@b.c', customer_update: undefined });
  assert.equal(emailOnly.status, 200, 'a new customer is created by Checkout with its address');

  const badValue = await h.api('POST', '/v1/checkout/sessions', { ...base, customer_update: { address: 'always' } });
  assert.equal(badValue.status, 400);
  assert.equal(badValue.body.error.param, 'customer_update[address]');
  const badKey = await h.api('POST', '/v1/checkout/sessions', { ...base, customer_update: { address: 'auto', phone: 'auto' } });
  assert.equal(badKey.status, 400);
  assert.equal(badKey.body.error.code, 'parameter_unknown');
});

test('Managed Payments forbids automatic_tax, customer_update[address] and customer_update[name] (parameter_unknown)', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const mpBase = checkoutForm({ customer: customer.id, price: 'price_e2e_creator', managed: true });
  const mp = await h.ok('POST', '/v1/checkout/sessions', mpBase);
  assert.deepEqual(mp.managed_payments, { enabled: true });
  assert.equal(mp.automatic_tax, undefined);
  assert.equal(mp.customer_update, undefined);

  for (const [what, extra, param] of [
    ['automatic_tax', { automatic_tax: { enabled: true } }, 'automatic_tax'],
    ['customer_update[address]', { customer_update: { address: 'auto' } }, 'customer_update'],
    ['customer_update[name]', { customer_update: { name: 'auto' } }, 'customer_update'],
    ['the full Stripe Tax body the provider sends without Managed Payments', { automatic_tax: { enabled: true }, customer_update: { address: 'auto', name: 'auto' } }, 'automatic_tax'],
  ]) {
    const bad = await h.api('POST', '/v1/checkout/sessions', { ...mpBase, ...extra });
    assert.equal(bad.status, 400, what);
    assert.equal(bad.body.error.code, 'parameter_unknown', what);
    assert.equal(bad.body.error.param, param, what);
  }
  const unknownKey = await h.api('POST', '/v1/checkout/sessions', { ...mpBase, managed_payments: { enabled: true, mode: 'x' } });
  assert.equal(unknownKey.status, 400);
});

const BRANDING = {
  background_color: '#0B0B12', button_color: '#7C3AED', border_style: 'rectangular', font_family: 'chakra_petch', display_name: 'ReelBolt',
  logo: { type: 'url', url: 'https://reelbolt.ai/brand/stripe-logo.png' }, icon: { type: 'url', url: 'https://reelbolt.ai/brand/stripe-icon.png' },
};

test('branding_settings: the full object the provider sends is accepted, echoed on the session and shown on the hosted page', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const session = await h.ok('POST', '/v1/checkout/sessions', { ...checkoutForm({ customer: customer.id, price: 'price_e2e_creator', managed: true }), branding_settings: BRANDING });
  assert.deepEqual(session.branding_settings, BRANDING);
  const read = await h.ok('GET', `/v1/checkout/sessions/${session.id}`);
  assert.deepEqual(read.branding_settings, BRANDING);

  const html = await (await fetch(`${h.base}/pay/${session.id}`)).text();
  assert.match(html, /data-testid="mock-checkout-brand-name" style="background:#0B0B12">ReelBolt</);
  assert.match(html, /button:not\(\.secondary\)\{background:#7C3AED\}/);
  assert.match(html, /data-testid="mock-checkout-brand-logo"[^>]*src="https:\/\/reelbolt\.ai\/brand\/stripe-logo\.png"/);
});

test('branding_settings: every field is optional, a file logo is accepted, and a session without it has null and no brand on the page', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const base = checkoutForm({ customer: customer.id, price: 'price_e2e_creator', managed: true });
  const partial = await h.ok('POST', '/v1/checkout/sessions', { ...base, branding_settings: { display_name: 'Only a name', icon: { type: 'file', file: 'file_1Abc' } } });
  assert.deepEqual(partial.branding_settings, { display_name: 'Only a name', icon: { type: 'file', file: 'file_1Abc' } });
  const plain = await h.ok('POST', '/v1/checkout/sessions', base);
  assert.equal(plain.branding_settings, null);
  const html = await (await fetch(`${h.base}/pay/${plain.id}`)).text();
  assert.doesNotMatch(html, /mock-checkout-brand-name|mock-checkout-brand-logo/);
});

test('branding_settings: invalid values are refused as invalid_request_error, unknown keys as parameter_unknown, and nothing is created', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const base = checkoutForm({ customer: customer.id, price: 'price_e2e_creator', managed: true });
  const before = (await h.control('GET', '/state')).sessions.length;
  const cases = [
    ['background_color without #', { background_color: '0B0B12' }, 'invalid_request_error', 'branding_settings[background_color]'],
    ['background_color short form', { background_color: '#fff' }, 'invalid_request_error', 'branding_settings[background_color]'],
    ['button_color not hex', { button_color: '#GGGGGG' }, 'invalid_request_error', 'branding_settings[button_color]'],
    ['border_style unknown', { border_style: 'square' }, 'invalid_request_error', 'branding_settings[border_style]'],
    ['font_family with spaces', { font_family: 'Chakra Petch' }, 'invalid_request_error', 'branding_settings[font_family]'],
    ['display_name empty', { display_name: '' }, 'invalid_request_error', 'branding_settings[display_name]'],
    ['display_name over 100', { display_name: 'x'.repeat(101) }, 'invalid_request_error', 'branding_settings[display_name]'],
    ['logo type unknown', { logo: { type: 'data', url: 'https://x.test/a.png' } }, 'invalid_request_error', 'branding_settings[logo][type]'],
    ['logo type missing', { logo: { url: 'https://x.test/a.png' } }, 'invalid_request_error', 'branding_settings[logo][type]'],
    ['logo url not https', { logo: { type: 'url', url: 'http://x.test/a.png' } }, 'invalid_request_error', 'branding_settings[logo][url]'],
    ['logo url missing', { logo: { type: 'url' } }, 'invalid_request_error', 'branding_settings[logo][url]'],
    ['icon url garbage', { icon: { type: 'url', url: 'not a url' } }, 'invalid_request_error', 'branding_settings[icon][url]'],
    ['icon file with url', { icon: { type: 'file', file: 'file_1Abc', url: 'https://x.test/a.png' } }, 'invalid_request_error', 'branding_settings[icon][url]'],
    ['icon file not an id', { icon: { type: 'file', file: 'a.png' } }, 'invalid_request_error', 'branding_settings[icon][file]'],
    ['unknown key', { text_color: '#000000' }, 'invalid_request_error', 'branding_settings[text_color]'],
    ['unknown logo key', { logo: { type: 'url', url: 'https://x.test/a.png', alt: 'x' } }, 'invalid_request_error', 'branding_settings[logo][alt]'],
  ];
  for (const [what, branding, type, param] of cases) {
    const res = await h.api('POST', '/v1/checkout/sessions', { ...base, branding_settings: { ...BRANDING, ...branding } });
    assert.equal(res.status, 400, what);
    assert.equal(res.body.error.type, type, what);
    assert.equal(res.body.error.param, param, what);
    if (what.startsWith('unknown')) assert.equal(res.body.error.code, 'parameter_unknown', what);
  }
  const notHash = await h.api('POST', '/v1/checkout/sessions', { ...base, branding_settings: 'ReelBolt' });
  assert.equal(notHash.status, 400);
  const after = (await h.control('GET', '/state')).sessions.length;
  assert.equal(after, before, 'a refused request creates no session');
});

test('a recurring price in payment mode, and subscription_data in payment mode, are refused', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const wrongPrice = await h.api('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_creator', mode: 'payment' }));
  assert.equal(wrongPrice.status, 400);
  const wrongData = await h.api('POST', '/v1/checkout/sessions', { ...checkoutForm({ customer: customer.id, price: 'price_e2e_topup_500', mode: 'payment' }), subscription_data: { metadata: { a: '1' } } });
  assert.equal(wrongData.status, 400);
  assert.equal(wrongData.body.error.param, 'subscription_data');
});

test('top-up: payment mode makes a PaymentIntent with payment_intent_data metadata and one checkout.session.completed', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_topup_500', mode: 'payment', metadata: { organization_id: ORG, topup_credits: '500' } }));
  assert.equal(session.amount_total, PACK[500]);
  await h.control('POST', '/complete-checkout', { session_id: session.id });
  await h.settle();
  assert.deepEqual(h.received.map((r) => r.event.type), ['checkout.session.completed']);
  const s = h.received[0].event.data.object;
  assert.equal(s.mode, 'payment');
  assert.equal(s.payment_status, 'paid');
  assert.equal(s.amount_total, PACK[500]);
  assert.equal(s.metadata.topup_credits, '500');
  assert.match(s.payment_intent, /^pi_/);

  const pi = await h.ok('GET', `/v1/payment_intents/${s.payment_intent}`, { expand: ['latest_charge.balance_transaction'] });
  assert.equal(pi.status, 'succeeded');
  assert.deepEqual(pi.metadata, { organization_id: ORG, topup_credits: '500' });
  assert.equal(pi.latest_charge.amount, PACK[500]);
  assert.equal(pi.latest_charge.balance_transaction.fee, 100);

  const found = await h.ok('GET', '/v1/checkout/sessions', { payment_intent: s.payment_intent });
  assert.deepEqual(found.data.map((x) => x.id), [session.id]);
});

test('upgrade: always_invoice makes a paid subscription_update invoice with a negative credit line and period.start = proration time', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  const item = sub.items.data[0];
  const at = item.current_period_start + 10 * 24 * 3600; // 10 days in
  await h.ok('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_pro', { prorationDate: at }), { headers: { 'idempotency-key': 'change:1' } });
  await h.settle();

  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated', 'invoice.paid']);
  const updated = h.received[0].event.data;
  assert.equal(updated.object.items.data[0].price.id, 'price_e2e_pro');
  assert.ok(updated.previous_attributes.items, 'previous_attributes names the changed items');

  const inv = h.received[1].event.data.object;
  assert.equal(inv.billing_reason, 'subscription_update');
  const [credit, charge] = inv.lines.data;
  assert.ok(credit.amount < 0 && charge.amount > 0);
  assert.equal(credit.pricing.price_details.price, 'price_e2e_creator');
  assert.equal(charge.pricing.price_details.price, 'price_e2e_pro');
  assert.equal(charge.parent.subscription_item_details.proration, true);
  assert.equal(charge.period.start, at, 'period.start is the proration time');
  assert.equal(charge.period.end, item.current_period_end);
  const span = item.current_period_end - item.current_period_start;
  const frac = (item.current_period_end - at) / span;
  assert.equal(charge.amount, Math.round(PRICE.pro * frac));
  assert.equal(credit.amount, -Math.round(PRICE.creator * frac));
  assert.equal(inv.amount_paid, charge.amount + credit.amount);
  assert.ok(inv.amount_paid > 0);
  assert.equal(inv.parent.subscription_details.subscription, sub.id);
});

test('downgrade with proration_behavior=none changes the items at once and invoices nothing', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'pro' });
  await h.settle();
  h.received.length = 0;
  const before = (await h.control('GET', '/state')).invoices.length;
  const item = sub.items.data[0];
  const out = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_creator', quantity: 1 }], proration_behavior: 'none' });
  await h.settle();
  assert.equal(out.items.data[0].price.id, 'price_e2e_creator');
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated']);
  assert.equal((await h.control('GET', '/state')).invoices.length, before, 'no invoice');
  // The next renewal bills the new price in full.
  const renewal = await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  assert.equal(renewal.invoice.amount_paid, PRICE.creator);
});

test('seats: price change resets quantity unless resent; increase is invoiced, decrease is not', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'team', quantity: 3 });
  await h.settle();
  h.received.length = 0;
  const item = sub.items.data[0];
  assert.equal(item.quantity, 3);

  await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_team', quantity: 5 }], proration_behavior: 'always_invoice' });
  await h.settle();
  const inv = h.eventsOf('invoice.paid')[0].data.object;
  assert.equal(inv.billing_reason, 'subscription_update');
  assert.equal(inv.lines.data.find((l) => l.amount > 0).quantity, 5);

  h.received.length = 0;
  const down = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_team', quantity: 4 }], proration_behavior: 'none' });
  await h.settle();
  assert.equal(down.items.data[0].quantity, 4);
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated']);

  const reset = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_creator' }], proration_behavior: 'none' });
  assert.equal(reset.items.data[0].quantity, 1, 'changing the price without a quantity resets it to 1');
  const unknownItem = await h.api('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: 'si_nope', quantity: 2 }] });
  assert.equal(unknownItem.status, 400);
  assert.equal(unknownItem.body.error.code, 'resource_missing');
});

test('cancel at period end, resume, then cancel and renew: customer.subscription.deleted at the period end', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;

  const canceled = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { cancel_at_period_end: true });
  assert.equal(canceled.cancel_at_period_end, true);
  assert.equal(canceled.cancel_at, sub.items.data[0].current_period_end);
  const resumed = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { cancel_at_period_end: false });
  assert.equal(resumed.cancel_at_period_end, false);
  assert.equal(resumed.cancel_at, null);
  await h.ok('POST', `/v1/subscriptions/${sub.id}`, { cancel_at_period_end: true });
  await h.settle();
  assert.deepEqual(h.received.map((r) => r.event.type), Array(3).fill('customer.subscription.updated'));
  assert.equal(h.received[0].event.data.previous_attributes.cancel_at_period_end, false);

  h.received.length = 0;
  await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.deleted']);
  assert.equal(h.received[0].event.data.object.status, 'canceled');
  const gone = await h.api('POST', `/v1/subscriptions/${sub.id}`, { cancel_at_period_end: false });
  assert.equal(gone.status, 400, 'a canceled subscription cannot be updated');
});

test('renewal: next period, a subscription_cycle invoice, subscription.updated then invoice.paid', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  const end = sub.items.data[0].current_period_end;
  const out = await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  assert.equal(out.subscription.items.data[0].current_period_start, end);
  assert.ok(out.subscription.items.data[0].current_period_end > end);
  assert.equal(out.invoice.billing_reason, 'subscription_cycle');
  assert.equal(out.invoice.amount_paid, PRICE.creator);
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated', 'invoice.paid']);
});

test('pending payment: hold keeps the invoice open with no invoice.paid until it is paid; fail marks the subscription past_due', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  const item = sub.items.data[0];

  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'hold' });
  await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_pro', quantity: 1 }], proration_behavior: 'always_invoice' });
  await h.settle();
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated'], 'the items moved, nothing was paid');
  const held = (await h.control('GET', '/state')).invoices.find((i) => i.billing_reason === 'subscription_update');
  assert.equal(held.status, 'open');
  assert.equal(held.amount_paid, 0);
  assert.ok(held.amount_due > 0);

  await h.control('POST', `/invoices/${held.id}/pay`, {});
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated', 'invoice.paid']);
  assert.equal(h.received[1].event.data.object.amount_paid, held.amount_due);

  h.received.length = 0;
  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'fail' });
  await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated', 'invoice.payment_failed']);
  assert.equal(h.received[0].event.data.object.status, 'past_due');
});

test('set-status and cancel-now send subscription events', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  await h.control('POST', `/subscriptions/${sub.id}/set-status`, { status: 'past_due' });
  await h.control('POST', `/subscriptions/${sub.id}/cancel-now`, {});
  assert.deepEqual(h.received.map((r) => [r.event.type, r.event.data.object.status]), [
    ['customer.subscription.updated', 'past_due'], ['customer.subscription.deleted', 'canceled'],
  ]);
  const listed = await h.ok('GET', '/v1/subscriptions', { customer: sub.customer, status: 'all', limit: 100 });
  assert.equal(listed.data.length, 1);
  const live = await h.ok('GET', '/v1/subscriptions', { customer: sub.customer });
  assert.equal(live.data.length, 0, 'canceled subscriptions are left out of the default listing');
});

test('DELETE cancels now', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  const gone = await h.ok('DELETE', `/v1/subscriptions/${sub.id}`);
  assert.equal(gone.status, 'canceled');
});

// ------------------------------------------------------------------ idempotency

test('idempotency: same key and body replays the first answer; same key and another body is an idempotency_error', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const first = await h.api('POST', '/v1/customers', { email: 'a@b.c', metadata: { organization_id: ORG } }, { headers: { 'idempotency-key': 'customer:1' } });
  const again = await h.api('POST', '/v1/customers', { email: 'a@b.c', metadata: { organization_id: ORG } }, { headers: { 'idempotency-key': 'customer:1' } });
  assert.equal(first.status, 200);
  assert.equal(again.status, 200);
  assert.equal(again.body.id, first.body.id, 'the same customer, not a second one');
  assert.equal(again.headers.get('idempotent-replayed'), 'true');
  assert.equal((await h.control('GET', '/state')).customers.length, 1);

  const other = await h.api('POST', '/v1/customers', { email: 'other@b.c' }, { headers: { 'idempotency-key': 'customer:1' } });
  assert.equal(other.status, 400);
  assert.equal(other.body.error.type, 'idempotency_error');

  // Parameter order does not change the fingerprint.
  const a = await h.api('POST', '/v1/customers', undefined, { headers: { 'idempotency-key': 'k2' }, raw: 'email=x%40y.z&name=N' });
  const b = await h.api('POST', '/v1/customers', undefined, { headers: { 'idempotency-key': 'k2' }, raw: 'name=N&email=x%40y.z' });
  assert.equal(b.body.id, a.body.id);

  // A failed request is not stored: the retry can succeed with the same key.
  const fail = await h.api('POST', '/v1/customers', { bogus: '1' }, { headers: { 'idempotency-key': 'k3' } });
  assert.equal(fail.status, 400);
});

// ------------------------------------------------------------------ refunds

test('refund of a top-up: refund.created (succeeded, with the payment intent) then charge.refunded; cumulative amounts', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_topup_1000', mode: 'payment', metadata: { organization_id: ORG, topup_credits: '1000' } }));
  const done = await h.control('POST', '/complete-checkout', { session_id: session.id });
  const pi = done.session.payment_intent;
  await h.settle();
  h.received.length = 0;

  const partial = await h.ok('POST', '/v1/refunds', { payment_intent: pi, amount: 500, reason: 'requested_by_customer', metadata: { reelbolt_reason: 'goodwill' } }, { headers: { 'idempotency-key': 'refund:1' } });
  assert.equal(partial.status, 'succeeded');
  assert.equal(partial.amount, 500);
  await h.settle();
  assert.deepEqual(h.received.map((r) => r.event.type), ['refund.created', 'charge.refunded']);
  const created = h.received[0].event.data.object;
  assert.equal(created.id, partial.id);
  assert.equal(created.status, 'succeeded');
  assert.equal(created.payment_intent, pi);
  assert.equal(created.currency, catalog.currency.toLowerCase());
  const charge = h.received[1].event.data.object;
  assert.equal(charge.amount_refunded, 500);
  assert.equal(charge.refunded, false);

  const tooMuch = await h.api('POST', '/v1/refunds', { payment_intent: pi, amount: PACK[1000] });
  assert.equal(tooMuch.status, 400);

  h.received.length = 0;
  const rest = await h.control('POST', '/issue-refund', { payment_intent: pi });
  assert.equal(rest.refund.amount, PACK[1000] - 500);
  assert.equal(rest.charge.refunded, true);
  assert.equal(rest.charge.amount_refunded, PACK[1000]);
  assert.deepEqual(h.received.map((r) => r.event.type), ['refund.created', 'charge.refunded']);

  const none = await h.api('POST', '/v1/refunds', { payment_intent: pi });
  assert.equal(none.body.error.code, 'charge_already_refunded');
});

test('refund of a subscription payment resolves through invoice_payments to the invoice and the subscription', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  const state = await h.control('GET', '/state');
  const inv = state.invoices[0];
  const payments = await h.ok('GET', '/v1/invoice_payments', { 'payment': { type: 'payment_intent', payment_intent: state.paymentIntents[0].id }, status: 'paid' });
  assert.equal(payments.data.length, 1);
  assert.equal(payments.data[0].invoice, inv.id);
  const invRead = await h.ok('GET', `/v1/invoices/${inv.id}`);
  assert.equal(invRead.parent.subscription_details.subscription, sub.id);

  const deep = await h.ok('GET', '/v1/invoice_payments', { invoice: inv.id, expand: ['data.payment.payment_intent.latest_charge.balance_transaction'] });
  assert.equal(deep.data[0].payment.payment_intent.latest_charge.balance_transaction.fee, 100);

  const refund = await h.control('POST', '/issue-refund', { invoice: inv.id });
  assert.equal(refund.refund.amount, PRICE.creator);
});

// ------------------------------------------------------------------ coupons

test('coupons: custom id, duplicate refused, a 50% forever coupon discounts checkout and every renewal', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const made = await h.ok('POST', '/v1/coupons', { id: 'rb_founder_alpha', name: 'Founder', percent_off: '50', duration: 'forever', metadata: { reelbolt_catalog: '1' } });
  assert.equal(made.percent_off, 50);
  assert.equal(made.valid, true);
  const dup = await h.api('POST', '/v1/coupons', { id: 'rb_founder_alpha', percent_off: '50', duration: 'forever' });
  assert.equal(dup.status, 400);
  assert.equal(dup.body.error.code, 'resource_already_exists');
  assert.equal((await h.ok('GET', '/v1/coupons/rb_founder_alpha')).metadata.reelbolt_catalog, '1');
  const missing = await h.api('GET', '/v1/coupons/nope');
  assert.equal(missing.body.error.code, 'resource_missing');

  const { session, sub } = await subscribe(h, { plan: 'creator', discount: 'rb_founder_alpha' });
  assert.equal(session.amount_total, PRICE.creator / 2);
  await h.settle();
  const first = h.eventsOf('invoice.paid')[0].data.object;
  assert.equal(first.amount_paid, PRICE.creator / 2);
  assert.equal(first.total_discount_amounts[0].amount, PRICE.creator / 2);
  assert.match(first.discounts[0], /^di_/);

  const expanded = await h.ok('GET', `/v1/invoices/${first.id}`, { expand: ['discounts'] });
  assert.equal(expanded.discounts[0].source.coupon, 'rb_founder_alpha');
  const withDiscounts = await h.ok('GET', `/v1/subscriptions/${sub.id}`, { expand: ['discounts'] });
  assert.equal(withDiscounts.discounts[0].source.coupon, 'rb_founder_alpha');
  assert.equal(withDiscounts.discounts[0].end, null, 'forever');

  for (let i = 0; i < 2; i++) {
    const renewal = await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
    assert.equal(renewal.invoice.amount_paid, PRICE.creator / 2, `renewal ${i + 1} stays discounted`);
    assert.equal(renewal.invoice.total_discount_amounts[0].amount, PRICE.creator / 2);
  }
});

test('a 100% forever coupon: the $0 first invoice and every $0 renewal still send invoice.paid, as Stripe does', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await h.ok('POST', '/v1/coupons', { id: 'free100', percent_off: '100', duration: 'forever' });
  const { session, sub } = await subscribe(h, { plan: 'pro', discount: 'free100' });
  assert.equal(session.amount_total, 0);
  await h.settle();
  const first = h.eventsOf('invoice.paid')[0]?.data.object;
  assert.ok(first, 'invoice.paid is sent for the $0 first invoice');
  assert.equal(first.billing_reason, 'subscription_create');
  assert.equal(first.amount_paid, 0);
  assert.equal(first.subtotal, PRICE.pro);
  assert.equal(first.total_discount_amounts[0].amount, PRICE.pro);

  await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  await h.settle();
  const renewals = h.eventsOf('invoice.paid').map((e) => e.data.object).filter((i) => i.billing_reason === 'subscription_cycle');
  assert.equal(renewals.length, 1, 'invoice.paid is sent for the $0 renewal');
  assert.equal(renewals[0].amount_paid, 0);
});

test('a once coupon discounts the first invoice only; an unknown or expired coupon is refused at checkout', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await h.ok('POST', '/v1/coupons', { id: 'once10', percent_off: '10', duration: 'once' });
  const { sub } = await subscribe(h, { plan: 'creator', discount: 'once10' });
  const renewal = await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  assert.equal(renewal.invoice.amount_paid, PRICE.creator, 'the second invoice is full price');
  assert.deepEqual((await h.ok('GET', `/v1/subscriptions/${sub.id}`)).discounts, []);

  const customer = await h.ok('POST', '/v1/customers', { email: 'a@b.c' });
  const unknown = await h.api('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_creator', discount: 'nope' }));
  assert.equal(unknown.body.error.code, 'resource_missing');
  await h.ok('POST', '/v1/coupons', { id: 'old', percent_off: '10', duration: 'once', redeem_by: '1' });
  const expired = await h.api('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_creator', discount: 'old' }));
  assert.equal(expired.body.error.code, 'coupon_expired');
});

test('discounts= clears the subscription discount, and a price change in the same call is prorated without it', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await h.ok('POST', '/v1/coupons', { id: 'half', percent_off: '50', duration: 'forever' });
  const { sub } = await subscribe(h, { plan: 'creator', discount: 'half' });
  const item = sub.items.data[0];
  const at = item.current_period_start + 15 * 24 * 3600;
  const out = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_pro', quantity: 1 }], proration_behavior: 'always_invoice', proration_date: at, discounts: '' });
  assert.deepEqual(out.discounts, []);
  const inv = (await h.control('GET', '/state')).invoices.find((i) => i.billing_reason === 'subscription_update');
  const frac = (item.current_period_end - at) / (item.current_period_end - item.current_period_start);
  assert.equal(inv.lines.data.find((l) => l.amount > 0).amount, Math.round(PRICE.pro * frac), 'prorated from the updated (removed) discount');
  const renewal = await h.control('POST', `/subscriptions/${sub.id}/advance-period`, {});
  assert.equal(renewal.invoice.amount_paid, PRICE.pro, 'the next invoice is full price');
});

test('create_preview returns integer totals from the price table, with the coupon', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await h.ok('POST', '/v1/coupons', { id: 'p20', percent_off: '20', duration: 'once' });
  const plain = await h.ok('POST', '/v1/invoices/create_preview', { currency: 'usd', subscription_details: { items: [{ price: 'price_e2e_team', quantity: 4 }] } });
  assert.equal(plain.subtotal, PRICE.team * 4);
  assert.equal(plain.total, PRICE.team * 4);
  assert.equal(plain.lines.data[0].quantity, 4);
  assert.equal(plain.lines.data[0].amount, PRICE.team * 4);
  const off = await h.ok('POST', '/v1/invoices/create_preview', { currency: 'usd', subscription_details: { items: [{ price: 'price_e2e_pro', quantity: 1 }] }, discounts: [{ coupon: 'p20' }] });
  assert.equal(off.total_discount_amounts[0].amount, Math.round(PRICE.pro * 0.2));
  assert.equal(off.total, PRICE.pro - Math.round(PRICE.pro * 0.2));
});

// ------------------------------------------------------------------ portal

test('portal sessions: overview, cancel flow and payment-method flow; the cancel page schedules the cancel and sends a webhook', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub, customer } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;

  const overview = await h.ok('POST', '/v1/billing_portal/sessions', { customer: customer.id, configuration: 'bpc_e2e', return_url: 'http://app.test/app/settings/billing' });
  assert.match(overview.url, /^http:\/\/pay\.test\/portal\/bps_/);
  const cancel = await h.ok('POST', '/v1/billing_portal/sessions', { customer: customer.id, configuration: 'bpc_e2e', return_url: 'http://app.test/app/settings/billing', flow_data: { type: 'subscription_cancel', subscription_cancel: { subscription: sub.id } } });
  await h.ok('POST', '/v1/billing_portal/sessions', { customer: customer.id, flow_data: { type: 'payment_method_update' } });

  const html = await (await fetch(`${h.base}/portal/${overview.id}`)).text();
  assert.match(html, /data-testid="mock-portal-heading"/);
  assert.match(html, /data-testid="mock-portal-back"/);
  const cancelHtml = await (await fetch(`${h.base}/portal/${cancel.id}`)).text();
  assert.match(cancelHtml, /data-testid="mock-portal-cancel"/);

  const post = await fetch(`${h.base}/portal/${cancel.id}/cancel`, { method: 'POST', redirect: 'manual' });
  assert.equal(post.status, 303);
  assert.equal(post.headers.get('location'), 'http://app.test/app/settings/billing');
  await h.settle();
  assert.equal(h.received[0].event.type, 'customer.subscription.updated');
  assert.equal(h.received[0].event.data.object.cancel_at_period_end, true);

  const other = await h.api('POST', '/v1/billing_portal/sessions', { customer: 'cus_nope' });
  assert.equal(other.body.error.code, 'resource_missing');
});

// ------------------------------------------------------------------ webhooks

test('webhooks: drop-next loses one, redeliver sends it again with its event id and a fresh valid signature', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;

  await h.control('POST', '/webhooks/drop-next', { event_type: 'customer.subscription.updated', subscription_id: sub.id });
  await h.ok('POST', `/v1/subscriptions/${sub.id}`, { cancel_at_period_end: true });
  await h.settle();
  assert.equal(h.received.length, 0, 'the update webhook was lost');
  const dropped = (await h.control('GET', '/webhooks')).webhooks.filter((w) => w.dropped);
  assert.equal(dropped.length, 1);
  assert.equal(dropped[0].event_type, 'customer.subscription.updated');
  assert.equal(dropped[0].subscription_id, sub.id);
  assert.deepEqual(dropped[0].attempts, []);

  // The filter is spent: the next one goes out.
  await h.ok('POST', `/v1/subscriptions/${sub.id}`, { cancel_at_period_end: false });
  await h.settle();
  assert.equal(h.received.length, 1);

  const redo = await h.control('POST', `/webhooks/${dropped[0].id}/redeliver`, {});
  assert.equal(redo.status, 200);
  assert.equal(redo.delivery.redelivery_of, dropped[0].id);
  const last = h.received.at(-1);
  assert.equal(last.event.id, dropped[0].event_id);
  assert.ok(last.valid);
});

test('webhooks: a non-2xx answer is retried up to four times, then given up', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  h.setReceiverStatus(500);
  const { customer } = await subscribe(h, { plan: 'creator' });
  assert.ok(customer);
  await h.settle();
  const first = h.mock.state().webhooks[0];
  assert.equal(first.attempts.length, 4);
  assert.equal(first.delivered, false);
  assert.ok(h.received.slice(0, 4).every((r) => r.valid && r.event.id === first.event_id));
});

test('webhooks: a retry that succeeds stops retrying; an unconfigured secret yields signatures the receiver rejects', async (t) => {
  const h = await harness({ webhookSecret: 'whsec_wrong_one' });
  t.after(() => h.close());
  await subscribe(h, { plan: 'creator' });
  await h.settle();
  assert.ok(h.received.length > 0);
  assert.ok(h.received.every((r) => !r.valid), 'signed with another secret: the receiver cannot verify it');
});

test('the send route can sign with a wrong secret on purpose (negative tests)', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const out = await h.control('POST', '/webhooks/send', { type: 'invoice.paid', object: { object: 'invoice', id: 'in_x' }, secret: 'whsec_other' });
  assert.equal(out.delivery.delivered, true);
  assert.equal(h.received.at(-1).valid, false);
});

test('state and reset: reset forgets customers, subscriptions and drop filters but keeps the catalog prices', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await subscribe(h, { plan: 'creator' });
  await h.control('POST', '/webhooks/drop-next', {});
  const before = await h.control('GET', '/state');
  assert.equal(before.subscriptions.length, 1);
  assert.equal(before.dropFilters.length, 1);
  await h.control('POST', '/reset', {});
  const after = await h.control('GET', '/state');
  assert.equal(after.subscriptions.length, 0);
  assert.equal(after.customers.length, 0);
  assert.equal(after.dropFilters.length, 0);
  assert.equal((await h.ok('GET', '/v1/prices/price_e2e_pro')).unit_amount, PRICE.pro);
});

// ------------------------------------------------------------------ payment_behavior=error_if_incomplete

/** Everything a subscription update could have changed, for "nothing changed" assertions. */
async function footprint(h, subId) {
  const state = await h.control('GET', '/state');
  const sub = state.subscriptions.find((x) => x.id === subId);
  return {
    sub: JSON.stringify(sub),
    invoices: state.invoices.length,
    payments: state.paymentIntents.length,
    charges: state.charges.length,
    discounts: state.discounts.length,
    coupons: JSON.stringify(state.coupons),
  };
}

test('error_if_incomplete with a payment that succeeds: the items move, the proration invoice is paid, the usual events follow', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  const item = sub.items.data[0];
  const out = await h.ok('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_pro'));
  await h.settle();
  assert.equal(out.items.data[0].price.id, 'price_e2e_pro');
  assert.equal(out.status, 'active');
  assert.deepEqual(h.received.map((r) => r.event.type), ['customer.subscription.updated', 'invoice.paid']);
  assert.equal(h.received[1].event.data.object.billing_reason, 'subscription_update');
});

test('error_if_incomplete with the "fail" payment mode: 402 card_error / card_declined and the subscription is exactly as before', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  const item = sub.items.data[0];
  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'fail' });
  const before = await footprint(h, sub.id);

  const refused = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_pro'), { headers: { 'idempotency-key': `change:${sub.id}:nonce1` } });
  assert.equal(refused.status, 402);
  assert.equal(refused.body.error.type, 'card_error');
  assert.equal(refused.body.error.code, 'card_declined');
  assert.equal(refused.body.error.decline_code, 'generic_decline');
  assert.equal(refused.body.error.message, 'Your card was declined.');

  await h.settle();
  assert.deepEqual(await footprint(h, sub.id), before, 'no item change, no invoice, no payment, status still active');
  assert.deepEqual(h.received, [], 'no webhook for an update that never happened');
  const live = await h.ok('GET', `/v1/subscriptions/${sub.id}`);
  assert.equal(live.items.data[0].price.id, 'price_e2e_creator');
  assert.equal(live.items.data[0].id, item.id);
  assert.equal(live.status, 'active', 'not past_due: the failed attempt is rolled back whole');

  // The failure was not cached under the key: a retry once the card works succeeds with the same key.
  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'pay' });
  const retried = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_pro'), { headers: { 'idempotency-key': `change:${sub.id}:nonce1` } });
  assert.equal(retried.status, 200, JSON.stringify(retried.body));
  assert.equal(retried.body.items.data[0].price.id, 'price_e2e_pro');
});

test('error_if_incomplete with a payment that stays pending ("hold") is refused the same way and changes nothing', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'hold' });
  const before = await footprint(h, sub.id);
  const refused = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(sub.items.data[0], 'price_e2e_pro'));
  assert.equal(refused.status, 402);
  assert.equal(refused.body.error.type, 'card_error');
  assert.equal(refused.body.error.code, 'invoice_payment_intent_requires_action');
  await h.settle();
  assert.deepEqual(await footprint(h, sub.id), before);
  assert.deepEqual(h.received, []);
});

test('error_if_incomplete rolls back a discount change and a seat increase too; without it the same failure is only a past_due subscription', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  await h.control('POST', '/coupons', { id: 'half', percent_off: 50, duration: 'forever' });
  const { sub } = await subscribe(h, { plan: 'team', quantity: 3, discount: 'half' });
  await h.settle();
  const item = sub.items.data[0];
  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'fail' });
  const before = await footprint(h, sub.id);

  const seats = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_team', { quantity: 5, clearDiscount: true }));
  assert.equal(seats.status, 402);
  assert.deepEqual(await footprint(h, sub.id), before, 'the quantity, the cleared discount and the coupon counters are all restored');

  // allow_incomplete (the default) keeps the pre-fix behavior: the items move and the subscription goes past_due.
  const lenient = await h.ok('POST', `/v1/subscriptions/${sub.id}`, { items: [{ id: item.id, price: 'price_e2e_team', quantity: 5 }], proration_behavior: 'always_invoice' });
  assert.equal(lenient.items.data[0].quantity, 5);
  assert.equal(lenient.status, 'past_due');
});

test('error_if_incomplete needs no payment when nothing is invoiced (a downgrade with proration none); unknown behaviors are refused', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'pro' });
  await h.control('POST', `/subscriptions/${sub.id}/payments`, { mode: 'fail' });
  const item = sub.items.data[0];
  const ok = await h.api('POST', `/v1/subscriptions/${sub.id}`, { ...changeForm(item, 'price_e2e_creator', { immediate: false }), payment_behavior: 'error_if_incomplete' });
  assert.equal(ok.status, 200, JSON.stringify(ok.body));
  assert.equal(ok.body.items.data[0].price.id, 'price_e2e_creator');

  const odd = await h.api('POST', `/v1/subscriptions/${sub.id}`, { ...changeForm(ok.body.items.data[0], 'price_e2e_pro'), payment_behavior: 'pending_if_incomplete' });
  assert.equal(odd.status, 400);
  assert.equal(odd.body.error.param, 'payment_behavior');
});

// ------------------------------------------------------------------ Checkout Session lists

test('GET /v1/checkout/sessions?subscription=&limit=1 finds the session that created the subscription; ?payment_intent= the top-up one', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { session, sub } = await subscribe(h, { plan: 'creator' });
  const other = await subscribe(h, { plan: 'pro' });

  const bySub = await h.ok('GET', '/v1/checkout/sessions', { subscription: sub.id, limit: 1 });
  assert.equal(bySub.object, 'list');
  assert.deepEqual(bySub.data.map((x) => x.id), [session.id]);
  assert.equal(bySub.has_more, false);
  assert.equal(bySub.data[0].client_reference_id, ORG);

  const otherSub = await h.ok('GET', '/v1/checkout/sessions', { subscription: other.sub.id, limit: 1 });
  assert.deepEqual(otherSub.data.map((x) => x.id), [other.session.id]);
  const none = await h.ok('GET', '/v1/checkout/sessions', { subscription: 'sub_unknown', limit: 1 });
  assert.deepEqual(none.data, []);

  const topUp = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: session.customer, price: 'price_e2e_topup_500', mode: 'payment', metadata: { organization_id: ORG, topup_credits: '500' } }));
  const done = await h.control('POST', '/complete-checkout', { session_id: topUp.id });
  const byPi = await h.ok('GET', '/v1/checkout/sessions', { payment_intent: done.session.payment_intent, limit: 1 });
  assert.deepEqual(byPi.data.map((x) => x.id), [topUp.id]);

  const sameCustomer = await h.ok('GET', '/v1/checkout/sessions', { customer: session.customer, limit: 1 });
  assert.equal(sameCustomer.data.length, 1);
  assert.equal(sameCustomer.has_more, true, 'limit=1 pages');
  const bad = await h.api('GET', '/v1/checkout/sessions', { bogus: '1' });
  assert.equal(bad.status, 400);
  assert.equal(bad.body.error.code, 'parameter_unknown');
});

// ------------------------------------------------------------------ unpaid status and disputes

test('set-status accepts unpaid and sends customer.subscription.updated with previous_attributes.status', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  h.received.length = 0;
  const r = await h.control('POST', `/subscriptions/${sub.id}/set-status`, { status: 'unpaid' });
  assert.equal(r.subscription.status, 'unpaid');
  assert.deepEqual(h.received.map((x) => [x.event.type, x.event.data.object.status, x.event.data.previous_attributes.status]), [['customer.subscription.updated', 'unpaid', 'active']]);
  assert.equal((await h.ok('GET', `/v1/subscriptions/${sub.id}`)).status, 'unpaid');
  const refused = await h.control('POST', `/subscriptions/${sub.id}/set-status`, { status: 'canceled' });
  assert.match(refused.error, /status must be one of/);
});

async function paidTopUp(h, credits = 500) {
  const customer = await h.ok('POST', '/v1/customers', { email: 'owner@e2e.test' });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: `price_e2e_topup_${credits}`, mode: 'payment', metadata: { organization_id: ORG, topup_credits: String(credits) } }));
  const done = await h.control('POST', '/complete-checkout', { session_id: session.id });
  await h.settle();
  h.received.length = 0;
  return { customer, session, paymentIntent: done.session.payment_intent, amount: PACK[credits] };
}

test('disputes: a lost chargeback on a top-up sends charge.dispute.created then charge.dispute.closed (lost) shaped like Stripe\'s Dispute', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { paymentIntent, amount } = await paidTopUp(h);
  const pi = await h.ok('GET', `/v1/payment_intents/${paymentIntent}`);
  const out = await h.control('POST', '/disputes', { payment_intent: paymentIntent, status: 'lost' });

  assert.deepEqual(h.received.map((r) => r.event.type), ['charge.dispute.created', 'charge.dispute.closed']);
  const [created, closed] = h.received.map((r) => r.event.data.object);
  for (const d of [created, closed]) {
    assert.match(d.id, /^dp_/);
    assert.equal(d.object, 'dispute');
    assert.equal(d.payment_intent, paymentIntent);
    assert.equal(d.charge, pi.latest_charge);
    assert.equal(d.amount, amount, 'defaults to the whole charge');
    assert.equal(d.currency, catalog.currency.toLowerCase());
    assert.equal(d.reason, 'fraudulent');
    assert.ok(d.created > 0);
  }
  assert.equal(created.status, 'needs_response', 'the created event carries the status at the time');
  assert.equal(closed.status, 'lost');
  assert.equal(closed.id, created.id);
  assert.equal(out.dispute.id, closed.id);
  assert.ok(h.received.every((r) => r.valid), 'both webhooks are signed');

  const charge = await h.ok('GET', `/v1/charges/${pi.latest_charge}`);
  assert.equal(charge.disputed, true);
  assert.equal(charge.dispute, closed.id);
  assert.equal(charge.amount_refunded, 0, 'a lost dispute is not a refund: amount_refunded stays 0');
  assert.equal((await h.ok('GET', `/v1/disputes/${closed.id}`)).status, 'lost');
  assert.deepEqual((await h.control('GET', '/state')).disputes.map((d) => d.id), [closed.id]);
});

test('disputes: created alone, then closed later; a partial amount; only the closed event with emit_created=false', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { paymentIntent, amount } = await paidTopUp(h, 1000);

  const opened = await h.control('POST', '/disputes', { payment_intent: paymentIntent, amount: 1000, reason: 'product_not_received' });
  assert.deepEqual(h.received.map((r) => r.event.type), ['charge.dispute.created']);
  assert.equal(opened.dispute.status, 'needs_response');
  assert.equal(opened.dispute.amount, 1000);
  assert.equal(opened.dispute.reason, 'product_not_received');
  assert.ok(opened.dispute.amount < amount);

  h.received.length = 0;
  const won = await h.control('POST', `/disputes/${opened.dispute.id}/close`, { status: 'won' });
  assert.equal(won.dispute.status, 'won');
  assert.deepEqual(h.received.map((r) => [r.event.type, r.event.data.object.status]), [['charge.dispute.closed', 'won']]);
  const again = await h.control('POST', `/disputes/${opened.dispute.id}/close`, { status: 'lost' });
  assert.match(again.error, /already closed/);

  // A second dispute on the same charge is refused; a new payment can lose without a created event.
  const dup = await h.control('POST', '/disputes', { payment_intent: paymentIntent });
  assert.match(dup.error, /already has a dispute/);
  const second = await paidTopUp(h, 500);
  await h.control('POST', '/disputes', { payment_intent: second.paymentIntent, status: 'lost', emit_created: false });
  assert.deepEqual(h.received.map((r) => r.event.type), ['charge.dispute.closed']);
});

test('disputes: unknown payment, bad amount, reason and status are refused and send nothing', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { paymentIntent, amount } = await paidTopUp(h);
  for (const [body, pattern] of [
    [{ payment_intent: 'pi_unknown' }, /No such payment_intent/],
    [{ payment_intent: paymentIntent, amount: amount + 1 }, /must be between 1 and the charge amount/],
    [{ payment_intent: paymentIntent, amount: 0 }, /must be between 1 and the charge amount/],
    [{ payment_intent: paymentIntent, reason: 'because' }, /Invalid reason/],
    [{ payment_intent: paymentIntent, status: 'pending' }, /created in one of/],
  ]) {
    assert.match((await h.control('POST', '/disputes', body)).error, pattern, JSON.stringify(body));
  }
  assert.match((await h.control('POST', '/disputes/dp_unknown/close', { status: 'lost' })).error, /no dispute/);
  assert.deepEqual(h.received, []);
  assert.equal((await h.control('GET', '/state')).disputes.length, 0);
});

test('disputes: on a subscription payment the event is attributed to the subscription (drop filters can match it)', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  const invoice = (await h.control('GET', '/state')).invoices.find((i) => i.billing_reason === 'subscription_create');
  const inpay = (await h.control('GET', '/state')).invoicePayments.find((p) => p.invoice === invoice.id);
  h.received.length = 0;
  await h.control('POST', '/webhooks/drop-next', { event_type: 'charge.dispute.closed', subscription_id: sub.id });
  await h.control('POST', '/disputes', { payment_intent: inpay.payment.payment_intent, status: 'lost' });
  assert.deepEqual(h.received.map((r) => r.event.type), ['charge.dispute.created'], 'the closed event matched the drop filter');
  const dropped = (await h.control('GET', '/webhooks')).webhooks.find((w) => w.dropped);
  assert.equal(dropped.event_type, 'charge.dispute.closed');
  assert.equal(dropped.subscription_id, sub.id);
});

// ------------------------------------------------------------------ idempotency of plan changes

test('plan changes with a fresh nonce per call: A -> B -> A -> B round trip is never blocked or replayed', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  await h.settle();
  const item = sub.items.data[0];
  const steps = ['price_e2e_pro', 'price_e2e_creator', 'price_e2e_pro', 'price_e2e_creator', 'price_e2e_pro'];
  for (const [i, price] of steps.entries()) {
    const r = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, price), { headers: { 'idempotency-key': `change:${sub.id}:${i.toString(16).padStart(32, '0')}` } });
    assert.equal(r.status, 200, `step ${i}: ${JSON.stringify(r.body)}`);
    assert.equal(r.headers.get('idempotent-replayed'), null, `step ${i} was applied, not replayed`);
    assert.equal(r.body.items.data[0].price.id, price, `step ${i}`);
  }
  await h.settle();
  const updates = (await h.control('GET', '/state')).invoices.filter((i) => i.billing_reason === 'subscription_update');
  assert.equal(updates.length, steps.length, 'one proration invoice per change');
  const calls = (await h.control('GET', `/calls?path=${encodeURIComponent(`/v1/subscriptions/${sub.id}`)}&method=POST`)).calls;
  assert.equal(new Set(calls.map((c) => c.idempotencyKey)).size, steps.length);
  assert.ok(calls.every((c) => !c.replayed));
});

test('a key reused for a later change (the old fingerprint-derived key) is replayed and applies nothing - the hazard the nonce removes', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  const item = sub.items.data[0];
  const key = { headers: { 'idempotency-key': `change:${sub.id}:fixed` } };
  const toPro = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_pro'), key);
  assert.equal(toPro.status, 200);
  await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_creator'), { headers: { 'idempotency-key': `change:${sub.id}:other` } });
  const stale = await h.api('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_pro'), key);
  assert.equal(stale.status, 200);
  assert.equal(stale.headers.get('idempotent-replayed'), 'true');
  assert.equal(stale.body.items.data[0].price.id, 'price_e2e_pro', 'the first answer comes back verbatim');
  assert.equal((await h.ok('GET', `/v1/subscriptions/${sub.id}`)).items.data[0].price.id, 'price_e2e_creator', 'and the subscription was not touched');

  const tooLong = await h.api('POST', '/v1/customers', { email: 'a@b.c' }, { headers: { 'idempotency-key': 'k'.repeat(256) } });
  assert.equal(tooLong.status, 400);
  assert.match(tooLong.body.error.message, /255 characters/);
  const longest = await h.api('POST', '/v1/customers', { email: 'a@b.c' }, { headers: { 'idempotency-key': 'k'.repeat(255) } });
  assert.equal(longest.status, 200);
});

// ------------------------------------------------------------------ yearly prices and periods

const ANNUAL = Object.fromEntries(catalog.plans.filter((p) => p.priceAnnual > 0).map((p) => [p.id, minor(p.priceAnnual)]));
const periodMonths = (item) => {
  const a = new Date(item.current_period_start * 1000);
  const b = new Date(item.current_period_end * 1000);
  return (b.getUTCFullYear() - a.getUTCFullYear()) * 12 + (b.getUTCMonth() - a.getUTCMonth());
};

test('yearly prices come from priceAnnual: interval year, lookup key rb:<plan>:year:<currency>, none for plans without one', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  assert.deepEqual(Object.keys(ANNUAL).sort(), ['creator', 'pro', 'team']);
  for (const [plan, cents] of Object.entries(ANNUAL)) {
    const price = await h.ok('GET', `/v1/prices/price_e2e_${plan}_annual`);
    assert.equal(price.unit_amount, cents);
    assert.equal(price.unit_amount, PRICE[plan] * 10, 'annual is 10 x monthly');
    assert.deepEqual(price.recurring, { interval: 'year', interval_count: 1 });
    assert.equal(price.lookup_key, `rb:${plan}:year:${catalog.currency.toLowerCase()}`);
    assert.equal(price.product, `prod_e2e_${plan}`);
  }
  const missing = await h.api('GET', '/v1/prices/price_e2e_enterprise_annual');
  assert.equal(missing.status, 404);
});

test('yearly checkout bills the yearly price for a one-year period and a renewal advances twelve months', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  // monthly baseline: one month
  assert.equal(periodMonths(sub.items.data[0]), 1);

  const customer = await h.ok('POST', '/v1/customers', { email: 'y@e2e.test', metadata: { organization_id: ORG } });
  const session = await h.ok('POST', '/v1/checkout/sessions', checkoutForm({ customer: customer.id, price: 'price_e2e_pro_annual', metadata: { organization_id: ORG, user_id: 'u1' } }));
  assert.equal(session.amount_total, ANNUAL.pro);
  const done = await h.control('POST', '/complete-checkout', { session_id: session.id });
  const yearly = done.subscription;
  assert.equal(yearly.items.data[0].price.id, 'price_e2e_pro_annual');
  assert.equal(periodMonths(yearly.items.data[0]), 12);
  await h.settle();
  const first = h.eventsOf('invoice.paid').map((e) => e.data.object).find((i) => i.subscription === undefined && i.parent.subscription_details.subscription === yearly.id);
  assert.equal(first.amount_paid, ANNUAL.pro);

  const renewal = await h.control('POST', `/subscriptions/${yearly.id}/advance-period`, {});
  assert.equal(renewal.invoice.billing_reason, 'subscription_cycle');
  assert.equal(renewal.invoice.amount_paid, ANNUAL.pro);
  const item = renewal.subscription.items.data[0];
  assert.equal(periodMonths(item), 12);
  assert.equal(item.current_period_start, yearly.items.data[0].current_period_end, 'the next year starts where the last ended');
});

test('monthly to yearly: the cycle restarts now, the unused month is credited and the yearly price charged in full', async (t) => {
  const h = await harness();
  t.after(() => h.close());
  const { sub } = await subscribe(h, { plan: 'creator' });
  const item = sub.items.data[0];
  assert.equal(periodMonths(item), 1);

  const updated = await h.ok('POST', `/v1/subscriptions/${sub.id}`, changeForm(item, 'price_e2e_creator_annual'));
  const after = updated.items.data[0];
  assert.equal(after.price.id, 'price_e2e_creator_annual');
  assert.equal(periodMonths(after), 12);
  assert.ok(after.current_period_start >= item.current_period_start, 'a fresh period starts at the change');

  await h.settle();
  const inv = h.eventsOf('invoice.paid').map((e) => e.data.object).find((i) => i.billing_reason === 'subscription_update');
  assert.ok(inv, 'a proration invoice is paid');
  const lines = inv.lines.data;
  const credit = lines.find((l) => l.amount < 0);
  const charge = lines.find((l) => l.amount > 0);
  assert.equal(charge.amount, ANNUAL.creator, 'the yearly price is charged in full');
  assert.ok(credit.amount < 0 && -credit.amount <= PRICE.creator, 'the unused monthly time is credited pro rata');
  assert.equal(inv.amount_paid, charge.amount + credit.amount);
});
