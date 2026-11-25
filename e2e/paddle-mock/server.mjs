// A stand-in for the Paddle Billing API (v1) for the isolated e2e stack. Zero dependencies, in-memory
// state, one process. It speaks the slice of Paddle the Inference API uses
// (inference/src/ReelBolt.Inference.Api/Services/Billing/Paddle/PaddleBillingProvider.cs) and sends
// signed webhooks back the way Paddle does, so the dashboard's billing flows run end to end with no
// network access and no Paddle account.
//
// Three surfaces:
//   - the API (Bearer auth, `{data, meta}` envelopes, Paddle-shaped errors) the Inference API calls;
//   - browser pages: the hosted checkout `GET /checkout/{txnId}` and the customer portal `/portal/...`;
//   - a control API under `/__mock/` for tests and the seed script (no auth: it is published on
//     127.0.0.1 only and exists to drive the mock).
//
// Environment:
//   PORT                   listen port inside the container (default 8080)
//   PADDLE_API_KEY         the Bearer token API routes require (empty = any non-empty token)
//   PADDLE_WEBHOOK_SECRET  signing secret for Paddle-Signature (required for webhooks to verify)
//   WEBHOOK_URL            where webhooks go (http://inference:8080/api/v1/billing/webhooks/paddle)
//   PUBLIC_URL             this mock as the BROWSER reaches it (http://localhost:28112)
//   APP_PUBLIC_URL         the app as the browser reaches it (http://localhost:8112)
//   WEBHOOK_DELAY_MS       delay before webhooks of a completed checkout are sent (default 300)
//   PADDLE_MOCK_PRICES     optional JSON {"pri_…": <unit amount in USD cents>} merged over PRICE_AMOUNTS

import { createServer } from 'node:http';
import { createHmac, randomInt } from 'node:crypto';

const PORT = Number(process.env.PORT || 8080);
const API_KEY = process.env.PADDLE_API_KEY || '';
const WEBHOOK_SECRET = process.env.PADDLE_WEBHOOK_SECRET || '';
const WEBHOOK_URL = process.env.WEBHOOK_URL || 'http://inference:8080/api/v1/billing/webhooks/paddle';
const PUBLIC_URL = (process.env.PUBLIC_URL || `http://localhost:${PORT}`).replace(/\/+$/, '');
const APP_PUBLIC_URL = (process.env.APP_PUBLIC_URL || 'http://localhost').replace(/\/+$/, '');
const WEBHOOK_DELAY_MS = Number(process.env.WEBHOOK_DELAY_MS ?? 300);
const WEBHOOK_ATTEMPTS = 4;

// Unit amounts in USD cents for the price ids e2e/scripts/seed-billing.mjs and docker-compose.e2e.yml
// configure. They mirror the list prices in plans.catalog.json (Creator 29, Pro 79, Team 49 per seat;
// packs 500/1000/3000 credits at 18/35/105). Only POST /pricing-preview and the discount totals of a
// completed transaction use them: the display maths lives here, never in the app.
const PRICE_AMOUNTS = {
  pri_e2e_creator: 2900,
  pri_e2e_pro: 7900,
  pri_e2e_team: 4900,
  pri_e2e_topup_500: 1800,
  pri_e2e_topup_1000: 3500,
  pri_e2e_topup_3000: 10500,
  ...(process.env.PADDLE_MOCK_PRICES ? JSON.parse(process.env.PADDLE_MOCK_PRICES) : {}),
};

// ------------------------------------------------------------------ state

let state;
function resetState() {
  state = {
    customers: new Map(),
    transactions: new Map(),
    subscriptions: new Map(),
    portalSessions: new Map(),
    discounts: new Map(),
    calls: [],
    webhooks: [],
    // Armed by POST /__mock/webhooks/drop: each matching webhook is recorded as dropped, never sent.
    dropFilters: [],
    // Delivery id -> the exact event recorded, for POST /__mock/webhooks/{id}/redeliver.
    deliveryEvents: new Map(),
    // Fault knob for POST /pricing-preview, set with POST /__mock/pricing-preview/mode:
    //   'ok' (default), 'error' (every preview answers 500), 'tax' (10% tax on each line).
    pricingPreviewMode: 'ok',
  };
}
resetState();

// occurred_at must strictly increase per subscription (the processor drops anything not newer than
// the last applied event), so every generated event takes the next tick of one monotonic clock.
let lastOccurredMs = 0;
function nextOccurred() {
  lastOccurredMs = Math.max(Date.now(), lastOccurredMs + 1);
  return iso(new Date(lastOccurredMs));
}

// Paddle prints six fractional digits.
function iso(d) {
  return d.toISOString().replace(/\.(\d{3})Z$/, '.$1000Z');
}

// Epoch ms of an RFC 3339 datetime (NaN if it is not one). Fractions past milliseconds are cut, and a
// `+hh:mm` offset that arrived unencoded in a query string (`+` read as a space) is restored.
function parseTime(s) {
  const text = String(s).trim().replace(/ (\d{2}:\d{2})$/, '+$1').replace(/(\.\d{3})\d+/, '$1');
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$/i.test(text)) return NaN;
  return Date.parse(text);
}

function addMonth(d) {
  const n = new Date(d.getTime());
  n.setUTCMonth(n.getUTCMonth() + 1);
  return n;
}

const ID_ALPHABET = 'abcdefghjkmnpqrstvwxyz0123456789';
function newId(prefix) {
  let s = '';
  for (let i = 0; i < 24; i++) s += ID_ALPHABET[randomInt(ID_ALPHABET.length)];
  return `${prefix}_01${s}`;
}

function log(...args) {
  console.log(new Date().toISOString(), '[paddle-mock]', ...args);
}

// ------------------------------------------------------------------ http helpers

function send(res, status, body, headers = {}) {
  const text = typeof body === 'string' ? body : JSON.stringify(body);
  res.writeHead(status, {
    'content-type': typeof body === 'string' ? 'text/html; charset=utf-8' : 'application/json',
    ...headers,
  });
  res.end(text);
}

function ok(res, data, status = 200) {
  send(res, status, { data, meta: { request_id: newId('req') } });
}

// Paddle's list envelope. One page: the mock never holds more entities than a page.
function okList(res, req, items) {
  send(res, 200, {
    data: items,
    meta: {
      request_id: newId('req'),
      pagination: { per_page: Math.max(items.length, 50), next: `${PUBLIC_URL}${new URL(req.url, 'http://mock.local').pathname}`, has_more: false, estimated_total: items.length },
    },
  });
}

// A comma-list filter (`status=active,paused`); null when the parameter is absent or empty.
function listFilter(query, name) {
  const raw = query.get(name);
  if (raw === null || raw.trim() === '') return null;
  return raw.split(',').map((v) => v.trim()).filter(Boolean);
}

// Paddle's error shape: https://developer.paddle.com/errors/overview
function paddleError(res, status, code, detail, type = 'request_error') {
  send(res, status, {
    error: { type, code, detail, documentation_url: `https://developer.paddle.com/errors/shared/${code}` },
    meta: { request_id: newId('req') },
  });
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function parseJson(text) {
  if (!text) return {};
  return JSON.parse(text);
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

function page(title, body) {
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)}</title>
<style>
  body{font-family:system-ui,sans-serif;max-width:640px;margin:40px auto;padding:0 16px;color:#1d1d1f;background:#fff}
  .badge{display:inline-block;font-size:12px;padding:2px 8px;border-radius:999px;background:#fde68a;color:#78350f}
  table{border-collapse:collapse;width:100%;margin:16px 0}td,th{border-bottom:1px solid #ddd;padding:6px;text-align:left}
  button{font-size:16px;padding:10px 18px;border-radius:8px;border:0;background:#6d28d9;color:#fff;cursor:pointer}
  button.secondary{background:#e5e7eb;color:#111}
  code{background:#f3f4f6;padding:1px 4px;border-radius:4px}
</style></head><body>
<p><span class="badge">Paddle mock — no real payment</span></p>
${body}
</body></html>`;
}

// ------------------------------------------------------------------ domain helpers

function expandItems(items) {
  return items.map((i) => ({
    price_id: i.price_id,
    quantity: i.quantity,
    price: { id: i.price_id, product_id: `pro_${i.price_id.replace(/^pri_/, '')}`, description: i.price_id },
  }));
}

function subscriptionItems(items, now) {
  return items.map((i) => ({
    status: 'active',
    quantity: i.quantity,
    recurring: true,
    created_at: now,
    updated_at: now,
    previously_billed_at: now,
    next_billed_at: null,
    price: { id: i.price_id, product_id: `pro_${i.price_id.replace(/^pri_/, '')}`, description: i.price_id },
  }));
}

function plainItems(sub) {
  return sub.items.map((i) => ({ price_id: i.price.id, quantity: i.quantity }));
}

function validateItems(items) {
  if (!Array.isArray(items) || items.length === 0) return 'items must be a non-empty array';
  for (const i of items) {
    if (!i || typeof i.price_id !== 'string' || !/^pri_[a-z0-9_]+$/i.test(i.price_id)) return 'each item needs a price_id like pri_…';
    if (i.quantity !== undefined && (!Number.isInteger(i.quantity) || i.quantity < 1)) return 'quantity must be a positive integer';
  }
  return null;
}

// ------------------------------------------------------------------ discounts and totals

/**
 * Why a discount cannot be used on these items right now, as a Paddle error code, or null.
 * Codes are the ones Paddle documents for POST /transactions with a discount_id.
 */
function discountProblem(discountId, items) {
  const d = state.discounts.get(discountId);
  if (!d || d.status !== 'active') return { status: 400, code: 'transaction_discount_not_found', detail: `Discount ${discountId} not found or not active.` };
  if (d.expires_at && Date.parse(d.expires_at) <= Date.now()) return { status: 400, code: 'discount_expired', detail: `Discount ${discountId} expired at ${d.expires_at}.` };
  if (Number.isInteger(d.usage_limit) && d.times_used >= d.usage_limit) {
    return { status: 400, code: 'discount_usage_limit_exceeded', detail: `Discount ${discountId} reached its usage limit of ${d.usage_limit}.` };
  }
  if (Array.isArray(d.restrict_to) && d.restrict_to.length > 0 && !items.some((i) => d.restrict_to.includes(i.price_id))) {
    return { status: 400, code: 'transaction_discount_not_eligible', detail: `Discount ${discountId} does not apply to any of these prices.` };
  }
  return null;
}

// Minimum quantity per price, as Paddle's price.quantity.minimum: Team is sold from 3 seats (its
// MinSeats), so a one-seat transaction or preview is refused like Paddle refuses it.
const PRICE_MIN_QUANTITY = {
  pri_e2e_team: 3,
};

/** The Paddle-style refusal for a quantity below a price's minimum, or null. */
function quantityProblem(items) {
  for (const i of items) {
    const min = PRICE_MIN_QUANTITY[i.price_id];
    if (min !== undefined && (i.quantity ?? 1) < min) {
      return `Quantity ${i.quantity ?? 1} for ${i.price_id} is below the price minimum ${min}.`;
    }
  }
  return null;
}

function unitAmount(priceId) {
  return Object.prototype.hasOwnProperty.call(PRICE_AMOUNTS, priceId) ? PRICE_AMOUNTS[priceId] : null;
}

/**
 * Per-line totals in USD cents. A percentage discount applies to every eligible line; a flat discount
 * (minor units) applies once, to the first eligible line, capped at its subtotal; flat_per_seat applies
 * per unit. Eligible = every line when restrict_to is empty, else the lines whose price is listed.
 */
function lineTotals(items, discount) {
  let flatLeft = discount?.type === 'flat' ? Number(discount.amount) || 0 : 0;
  return items.map((i) => {
    const quantity = i.quantity ?? 1;
    const subtotal = (unitAmount(i.price_id) ?? 0) * quantity;
    let off = 0;
    const eligible = discount && (!Array.isArray(discount.restrict_to) || discount.restrict_to.length === 0 || discount.restrict_to.includes(i.price_id));
    if (eligible) {
      if (discount.type === 'percentage') off = Math.round((subtotal * Number(discount.amount)) / 100);
      else if (discount.type === 'flat_per_seat') off = (Number(discount.amount) || 0) * quantity;
      else if (discount.type === 'flat') {
        off = Math.min(flatLeft, subtotal);
        flatLeft -= off;
      }
      off = Math.max(0, Math.min(off, subtotal));
    }
    return { price_id: i.price_id, quantity, subtotal, discount: off, tax: 0, total: subtotal - off };
  });
}

function unitTotals(l) {
  const q = l.quantity || 1;
  return { subtotal: l.subtotal / q, discount: l.discount / q, tax: l.tax / q, total: l.total / q };
}

function sumTotals(lines) {
  const t = { subtotal: 0, discount: 0, tax: 0, total: 0 };
  for (const l of lines) for (const k of Object.keys(t)) t[k] += l[k];
  return t;
}

function money(cents) {
  return `$${(cents / 100).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

function formattedTotals(t) {
  return {
    subtotal: money(t.subtotal),
    discount: t.discount > 0 ? `-${money(t.discount)}` : money(0),
    tax: money(t.tax),
    total: money(t.total),
  };
}

function stringTotals(t) {
  return { subtotal: String(t.subtotal), discount: String(t.discount), tax: String(t.tax), total: String(t.total) };
}

function customerForTransaction(txn) {
  if (txn.customer_id && state.customers.has(txn.customer_id)) return txn.customer_id;
  // A transaction created without a customer (the pre-create failed): the buyer would type an email on
  // Paddle's page. Reuse the organization's customer if one exists, else make one up.
  const org = txn.custom_data?.organization_id;
  for (const c of state.customers.values()) {
    if (org && c.custom_data?.organization_id === org) return c.id;
  }
  const now = iso(new Date());
  const c = {
    id: newId('ctm'),
    email: `buyer+${(org || 'unknown').slice(0, 8)}@paddle-mock.test`,
    name: null,
    status: 'active',
    custom_data: org ? { organization_id: org } : null,
    created_at: now,
    updated_at: now,
  };
  state.customers.set(c.id, c);
  return c.id;
}

function managementUrls(sub) {
  return {
    update_payment_method: `${PUBLIC_URL}/portal/direct/subscriptions/${sub.id}/update-payment-method`,
    cancel: `${PUBLIC_URL}/portal/direct/subscriptions/${sub.id}/cancel`,
  };
}

function subscriptionView(sub) {
  return { ...sub, management_urls: managementUrls(sub) };
}

/** Completes a transaction: creates the subscription for a subscription purchase, then queues webhooks. */
function completeTransaction(txn, { delayMs = WEBHOOK_DELAY_MS } = {}) {
  if (txn.status === 'completed') return { transaction: txn, subscription: txn.subscription_id ? state.subscriptions.get(txn.subscription_id) : null, alreadyCompleted: true };
  // One tick for the payment, the next for the subscription it creates: each webhook carries its
  // entity's timestamp as occurred_at, still in the order they are sent.
  const txnAt = nextOccurred();
  txn.customer_id = customerForTransaction(txn);
  txn.status = 'completed';
  txn.billed_at = txnAt;
  txn.updated_at = txnAt;

  let sub = null;
  const kind = txn.custom_data?.kind;
  if (kind !== 'topup') {
    const nowIso = nextOccurred();
    const now = new Date(nowIso);
    sub = {
      id: newId('sub'),
      status: 'active',
      customer_id: txn.customer_id,
      address_id: newId('add'),
      business_id: null,
      currency_code: 'USD',
      collection_mode: 'automatic',
      created_at: nowIso,
      updated_at: nowIso,
      started_at: nowIso,
      first_billed_at: nowIso,
      next_billed_at: iso(addMonth(now)),
      paused_at: null,
      canceled_at: null,
      current_billing_period: { starts_at: nowIso, ends_at: iso(addMonth(now)) },
      billing_cycle: { frequency: 1, interval: 'month' },
      scheduled_change: null,
      items: subscriptionItems(txn.items.map((i) => ({ price_id: i.price_id, quantity: i.quantity })), nowIso),
      custom_data: txn.custom_data ?? null,
      discount: null,
    };
    // A recurring discount carries over to the subscription, as Paddle does with recur: true.
    const d = txn.discount_id ? state.discounts.get(txn.discount_id) : null;
    if (d?.recur) sub.discount = { id: d.id, starts_at: nowIso, ends_at: null };
    state.subscriptions.set(sub.id, sub);
    txn.subscription_id = sub.id;
  }
  if (txn.discount_id && state.discounts.has(txn.discount_id)) {
    const d = state.discounts.get(txn.discount_id);
    d.times_used += 1;
    d.updated_at = txnAt;
  }

  const txnSnapshot = JSON.parse(JSON.stringify(txn));
  const subSnapshot = sub ? JSON.parse(JSON.stringify(sub)) : null;
  setTimeout(() => {
    enqueueWebhook('transaction.completed', transactionPayload(txnSnapshot), { occurredAt: txnSnapshot.updated_at });
    if (subSnapshot) enqueueWebhook('subscription.created', subscriptionView(subSnapshot), { occurredAt: subSnapshot.updated_at });
  }, Math.min(60_000, Math.max(0, Number(delayMs) || 0)));
  log(`completed ${txn.id}${sub ? ` -> ${sub.id}` : ' (top-up)'}`);
  return { transaction: txn, subscription: sub, alreadyCompleted: false };
}

function transactionPayload(txn) {
  // Only a discounted transaction gains discount_id and details.totals, so the payloads of every
  // undiscounted flow stay exactly what they were before discounts existed in the mock.
  // The totals use the discount as it was when the transaction was created.
  let discounted = {};
  if (txn.discount_id) {
    const totals = sumTotals(lineTotals(txn.items, txn.discount_snapshot));
    discounted = {
      discount_id: txn.discount_id,
      details: { totals: { ...stringTotals(totals), grand_total: String(totals.total), currency_code: 'USD' } },
    };
  }
  const items = expandItems(txn.items);
  if (txn.proration) items.forEach((i) => { i.proration = txn.proration; });
  return {
    id: txn.id,
    status: txn.status,
    customer_id: txn.customer_id,
    address_id: null,
    business_id: null,
    subscription_id: txn.subscription_id ?? null,
    invoice_id: txn.status === 'completed' ? newId('inv') : null,
    origin: txn.origin ?? 'web',
    collection_mode: 'automatic',
    currency_code: 'USD',
    created_at: txn.created_at,
    updated_at: txn.updated_at,
    billed_at: txn.billed_at ?? null,
    items,
    details: txn.details ?? null,
    custom_data: txn.custom_data ?? null,
    checkout: txn.checkout ?? null,
    ...discounted,
  };
}

// ------------------------------------------------------------------ upgrade (proration) payments

// What happens to the next proration charge of a subscription: 'pay' (default: transaction.completed
// follows subscription.updated), 'hold' (stays billed until /__mock/transactions/{id}/complete or /fail),
// 'fail' (transaction.payment_failed, never completed). Set with POST /__mock/subscriptions/{id}/payments.
const paymentModes = new Map();

// The mock knows no price amounts, so a proration charges a nominal full-period difference (minor
// units, USD) scaled by the share of the period left. The Inference API grants upgrade credits only
// for a charge above 0; a change in the very last instant of a period would charge 0, as Paddle does.
const PRORATION_FULL_PERIOD_MINOR = 5000;

/**
 * Paddle-shaped `details.line_items` of a proration: each removed (previous) item is credited with a
 * negative total and each new item is charged a positive one, so the line totals add up to `charged`.
 * The Inference API pairs the payment with its upgrade on the positively charged lines (price id and
 * quantity, Security R-2); a charge of 0 has no positive line and never pairs.
 */
function prorationLineItems(previousItems, items, charged, proration) {
  const credit = Number(charged);
  const split = (amount, list) => list.map((_, idx) => Math.floor(amount / list.length) + (idx === 0 ? amount % list.length : 0));
  const credits = split(credit, previousItems);
  const charges = split(Number(charged) + (previousItems.length > 0 ? credit : 0), items);
  const line = (i, amount) => ({
    id: newId('txnitm'),
    price_id: i.price_id,
    quantity: i.quantity,
    totals: { subtotal: String(amount), tax: '0', discount: '0', total: String(amount) },
    product: { id: `pro_${i.price_id.replace(/^pri_/, '')}` },
    proration,
  });
  return [...previousItems.map((i, idx) => line(i, -credits[idx])), ...items.map((i, idx) => line(i, charges[idx]))];
}

/** The charge Paddle creates for a prorated_immediately change, prorated from `changedAt` to the period end. */
function createProrationTransaction(sub, items, changedAt, previousItems = []) {
  const startsAt = changedAt;
  const endsAt = sub.current_billing_period?.ends_at ?? iso(addMonth(new Date(changedAt)));
  const periodStart = sub.current_billing_period?.starts_at ?? startsAt;
  const total = new Date(endsAt).getTime() - new Date(periodStart).getTime();
  const left = new Date(endsAt).getTime() - new Date(startsAt).getTime();
  const rate = total > 0 ? Math.min(1, Math.max(0, left / total)) : 1;
  const charged = String(Math.round(PRORATION_FULL_PERIOD_MINOR * rate));
  const txn = {
    id: newId('txn'),
    status: 'billed',
    origin: 'subscription_update',
    customer_id: sub.customer_id,
    subscription_id: sub.id,
    items: items.map((i) => ({ price_id: i.price_id, quantity: i.quantity })),
    proration: { rate: rate.toFixed(5), billing_period: { starts_at: startsAt, ends_at: endsAt } },
    // Paddle's totals are strings of minor units; grand_total is what the customer is charged.
    details: {
      totals: { subtotal: charged, tax: '0', total: charged, grand_total: charged, currency_code: 'USD' },
      line_items: prorationLineItems(previousItems, items, charged, { rate: rate.toFixed(5), billing_period: { starts_at: startsAt, ends_at: endsAt } }),
    },
    custom_data: sub.custom_data ?? null,
    collection_mode: 'automatic',
    created_at: changedAt,
    updated_at: changedAt,
    billed_at: changedAt,
    checkout: null,
  };
  state.transactions.set(txn.id, txn);
  log(`proration ${txn.id} for ${sub.id} (rate ${txn.proration.rate}, mode ${paymentModes.get(sub.id) ?? 'pay'})`);
  return txn;
}

/** Applies the subscription's payment mode to a fresh proration charge (queued after subscription.updated). */
function settleProration(txn) {
  const mode = paymentModes.get(txn.subscription_id) ?? 'pay';
  if (mode === 'pay') return payProration(txn);
  if (mode === 'fail') return failProration(txn);
  return null; // held
}

function payProration(txn) {
  if (txn.status === 'completed') return null;
  txn.status = 'completed';
  txn.updated_at = nextOccurred();
  return enqueueWebhook('transaction.completed', transactionPayload(JSON.parse(JSON.stringify(txn))), { occurredAt: txn.updated_at });
}

function failProration(txn, { notify = true } = {}) {
  if (txn.status === 'completed') return null;
  txn.status = 'past_due';
  txn.updated_at = nextOccurred();
  return notify ? enqueueWebhook('transaction.payment_failed', transactionPayload(JSON.parse(JSON.stringify(txn))), { occurredAt: txn.updated_at }) : null;
}

/** A refund (or chargeback) of a paid transaction: adjustment.created, approved, full unless told otherwise. */
function refundTransaction(txn, { action = 'refund', type = 'full', status = 'approved' } = {}) {
  const now = iso(new Date());
  const adjustment = {
    id: newId('adj'),
    action,
    type,
    status,
    transaction_id: txn.id,
    subscription_id: txn.subscription_id ?? null,
    customer_id: txn.customer_id ?? null,
    reason: 'e2e refund',
    currency_code: 'USD',
    items: txn.items.map((i) => ({ item_id: newId('txnitm'), type: type === 'full' ? 'full' : 'partial', amount: '0', proration: txn.proration ?? null })),
    created_at: now,
    updated_at: now,
  };
  return { adjustment, delivery: enqueueWebhook('adjustment.created', adjustment) };
}

// Every mutation of a subscription takes the next tick of the event clock as its updated_at, and the
// webhook announcing it carries that same instant as occurred_at (as Paddle does): a reconciler that
// reads GET /subscriptions/{id} sees the time of the change the lost webhook would have carried.
function touch(sub) {
  sub.updated_at = nextOccurred();
  return sub.updated_at;
}

/** Queues a subscription webhook whose occurred_at is the subscription's updated_at (call touch first). */
function subscriptionWebhook(eventType, sub) {
  return enqueueWebhook(eventType, subscriptionView(JSON.parse(JSON.stringify(sub))), { occurredAt: sub.updated_at });
}

// ------------------------------------------------------------------ webhooks

// Deliveries go out one at a time, in the order they were queued, like Paddle's per-subscription
// ordering in practice. Each delivery is retried a few times on failure (Paddle retries for days).
let deliveryChain = Promise.resolve();

function buildEvent(eventType, data, { eventId, occurredAt } = {}) {
  return {
    event_id: eventId || newId('evt'),
    event_type: eventType,
    occurred_at: occurredAt || nextOccurred(),
    notification_id: newId('ntf'),
    data,
  };
}

function sign(rawBody, secret = WEBHOOK_SECRET, ts = Math.floor(Date.now() / 1000)) {
  const h1 = createHmac('sha256', secret).update(`${ts}:${rawBody}`).digest('hex');
  return `ts=${ts};h1=${h1}`;
}

function eventSubscriptionId(event) {
  return event.data?.subscription_id ?? (String(event.event_type).startsWith('subscription.') ? event.data?.id ?? null : null);
}

function eventTransactionId(event) {
  return String(event.event_type).startsWith('transaction.') ? event.data?.id ?? null : event.data?.transaction_id ?? null;
}

/**
 * Consumes one use of the first armed drop filter the event matches (every given field must match;
 * an absent field matches anything) and returns it, or null: the event is then sent as usual.
 */
function takeDropFilter(event) {
  const filter = state.dropFilters.find((f) => f.remaining > 0
    && (!f.event_type || f.event_type === event.event_type)
    && (!f.subscription_id || f.subscription_id === eventSubscriptionId(event))
    && (!f.transaction_id || f.transaction_id === eventTransactionId(event)));
  if (!filter) return null;
  filter.remaining -= 1;
  filter.dropped += 1;
  return filter;
}

/**
 * Sends one event (or records it as lost when `drop` is a consumed filter). Every delivery is recorded
 * in state.webhooks with an id; the event itself is kept for /__mock/webhooks/{id}/redeliver.
 */
async function deliver(event, { secret, signature, attempts = WEBHOOK_ATTEMPTS, drop = null, redeliveryOf = null } = {}) {
  const raw = JSON.stringify(event);
  const record = { id: newId('dlv'), event_id: event.event_id, event_type: event.event_type, occurred_at: event.occurred_at, subscription_id: eventSubscriptionId(event), transaction_id: eventTransactionId(event), attempts: [], delivered: false, dropped: false };
  if (redeliveryOf) record.redelivery_of = redeliveryOf;
  state.webhooks.push(record);
  state.deliveryEvents.set(record.id, event);
  if (drop) {
    // Lost on purpose: Paddle "sent" it, the receiver never saw it.
    record.dropped = true;
    record.drop_filter = drop.id;
    log(`webhook ${event.event_type} ${event.event_id} DROPPED (filter ${drop.id}, ${drop.remaining} left) as ${record.id}`);
    return record;
  }
  for (let attempt = 1; attempt <= attempts; attempt++) {
    // Signed at send time: the receiver's tolerance window is 5 seconds.
    const header = signature || sign(raw, secret ?? WEBHOOK_SECRET);
    let status = 0;
    let body = '';
    try {
      const res = await fetch(WEBHOOK_URL, {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'Paddle-Signature': header },
        body: raw,
        signal: AbortSignal.timeout(10_000),
      });
      status = res.status;
      body = (await res.text()).slice(0, 500);
    } catch (err) {
      body = String(err?.message || err);
    }
    record.attempts.push({ at: new Date().toISOString(), status, body });
    log(`webhook ${event.event_type} ${event.event_id} attempt ${attempt} -> ${status || 'error'} ${body}`);
    if (status >= 200 && status < 300) {
      record.delivered = true;
      record.response = body;
      return record;
    }
    // A 4xx other than 404/429 will not get better on retry (bad signature, malformed body).
    if (status >= 400 && status < 500 && status !== 404 && status !== 429) break;
    if (attempt < attempts) await new Promise((r) => setTimeout(r, 1000 * attempt));
  }
  return record;
}

// Drop filters are matched when the webhook is queued, i.e. by the call that caused it, so a filter
// armed afterwards never catches an event already on its way.
function enqueueWebhook(eventType, data, options = {}) {
  const event = buildEvent(eventType, data, options);
  const drop = takeDropFilter(event);
  const p = deliveryChain.then(() => deliver(event, { ...options, drop }));
  deliveryChain = p.catch(() => {});
  return p;
}

// ------------------------------------------------------------------ API routes

function requireBearer(req, res) {
  const auth = req.headers['authorization'] || '';
  const m = /^Bearer\s+(\S.*)$/i.exec(auth);
  if (!m) {
    paddleError(res, 401, 'authentication_missing', 'Authentication header missing or malformed. Expected `Authorization: Bearer <api key>`.');
    return false;
  }
  if (API_KEY && m[1].trim() !== API_KEY) {
    paddleError(res, 403, 'forbidden', 'The API key used is not valid for this mock.');
    return false;
  }
  return true;
}

async function handleApi(req, res, method, parts, body) {
  // POST /transactions
  if (method === 'POST' && parts.length === 1 && parts[0] === 'transactions') {
    const err = validateItems(body.items);
    if (err) return paddleError(res, 400, 'bad_request', err);
    const quantityErr = quantityProblem(body.items);
    if (quantityErr) return paddleError(res, 400, 'transaction_item_quantity_out_of_range', quantityErr);
    if (body.customer_id && !state.customers.has(body.customer_id)) {
      return paddleError(res, 404, 'entity_not_found', `Customer ${body.customer_id} not found.`);
    }
    if (body.discount_id !== undefined && body.discount_id !== null) {
      const problem = discountProblem(body.discount_id, body.items);
      if (problem) return paddleError(res, problem.status, problem.code, problem.detail);
    }
    const now = iso(new Date());
    const id = newId('txn');
    const txn = {
      id,
      status: body.customer_id ? 'ready' : 'draft',
      customer_id: body.customer_id ?? null,
      subscription_id: null,
      items: body.items.map((i) => ({ price_id: i.price_id, quantity: i.quantity ?? 1 })),
      custom_data: body.custom_data ?? null,
      collection_mode: body.collection_mode ?? 'automatic',
      created_at: now,
      updated_at: now,
      checkout: { url: `${PUBLIC_URL}/checkout/${id}` },
    };
    if (body.discount_id) {
      txn.discount_id = body.discount_id;
      txn.discount_snapshot = JSON.parse(JSON.stringify(state.discounts.get(body.discount_id)));
    }
    // The request exactly as received, for GET /__mock/transactions (never part of a payload).
    txn.request_body = JSON.parse(JSON.stringify(body));
    state.transactions.set(id, txn);
    return ok(res, transactionPayload(txn), 201);
  }

  // POST /discounts
  if (method === 'POST' && parts.length === 1 && parts[0] === 'discounts') {
    if (typeof body.description !== 'string' || !body.description.trim()) return paddleError(res, 400, 'bad_request', 'description is required.');
    if (!['percentage', 'flat', 'flat_per_seat'].includes(body.type)) return paddleError(res, 400, 'bad_request', 'type must be percentage, flat or flat_per_seat.');
    if (typeof body.amount !== 'string' || !/^\d+(\.\d+)?$/.test(body.amount)) return paddleError(res, 400, 'bad_request', 'amount must be a numeric string.');
    if (body.type === 'percentage' && !(Number(body.amount) > 0 && Number(body.amount) <= 100)) return paddleError(res, 400, 'bad_request', 'a percentage amount must be between 0.01 and 100.');
    if (body.type !== 'percentage' && !/^\d+$/.test(body.amount)) return paddleError(res, 400, 'bad_request', 'a flat amount is in minor units (an integer string).');
    if (body.type !== 'percentage' && typeof body.currency_code !== 'string') return paddleError(res, 400, 'bad_request', 'currency_code is required for a flat discount.');
    if (body.restrict_to !== undefined && body.restrict_to !== null && !Array.isArray(body.restrict_to)) return paddleError(res, 400, 'bad_request', 'restrict_to must be an array of ids.');
    const now = iso(new Date());
    // Defaults, then the request body, then the fields Paddle owns (id = dsc_ + 26 lowercase alnum).
    const d = {
      code: null,
      enabled_for_checkout: false,
      currency_code: null,
      recur: false,
      maximum_recurring_intervals: null,
      usage_limit: null,
      restrict_to: null,
      expires_at: null,
      custom_data: null,
      ...body,
    };
    const { id: _id, status: _status, times_used: _used, created_at: _c, updated_at: _u, ...fields } = d;
    const created = { id: newId('dsc'), status: 'active', ...fields, times_used: 0, created_at: now, updated_at: now };
    state.discounts.set(created.id, created);
    return ok(res, created, 201);
  }

  // GET /discounts/{id}
  if (method === 'GET' && parts.length === 2 && parts[0] === 'discounts') {
    const d = state.discounts.get(parts[1]);
    return d ? ok(res, d) : paddleError(res, 404, 'entity_not_found', `Discount ${parts[1]} not found.`);
  }

  // PATCH /discounts/{id}  — status (active|archived) and expires_at (null clears it); no delete exists.
  if (method === 'PATCH' && parts.length === 2 && parts[0] === 'discounts') {
    const d = state.discounts.get(parts[1]);
    if (!d) return paddleError(res, 404, 'entity_not_found', `Discount ${parts[1]} not found.`);
    const has = (k) => Object.prototype.hasOwnProperty.call(body, k);
    if (!has('status') && !has('expires_at')) return paddleError(res, 400, 'bad_request', 'The mock updates status and expires_at only.');
    if (has('status') && !['active', 'archived'].includes(body.status)) return paddleError(res, 400, 'bad_request', 'status must be active or archived.');
    if (has('expires_at') && body.expires_at !== null && Number.isNaN(Date.parse(body.expires_at))) return paddleError(res, 400, 'bad_request', 'expires_at must be RFC 3339 or null.');
    if (has('status')) d.status = body.status;
    if (has('expires_at')) d.expires_at = body.expires_at;
    d.updated_at = iso(new Date());
    return ok(res, d);
  }

  // POST /pricing-preview  — items[] {price_id, quantity}, optional discount_id; no customer needed.
  if (method === 'POST' && parts.length === 1 && parts[0] === 'pricing-preview') {
    if (state.pricingPreviewMode === 'error') {
      return paddleError(res, 500, 'internal_error', 'Injected failure (mode error on /__mock/pricing-preview/mode).', 'api_error');
    }
    const err = validateItems(body.items);
    if (err) return paddleError(res, 400, 'bad_request', err);
    const quantityErr = quantityProblem(body.items);
    if (quantityErr) return paddleError(res, 400, 'transaction_item_quantity_out_of_range', quantityErr);
    const unknown = body.items.find((i) => unitAmount(i.price_id) === null);
    if (unknown) return paddleError(res, 404, 'entity_not_found', `Price ${unknown.price_id} not found (the mock knows ${Object.keys(PRICE_AMOUNTS).join(', ')}).`);
    let discount = null;
    if (body.discount_id !== undefined && body.discount_id !== null) {
      const problem = discountProblem(body.discount_id, body.items);
      if (problem) return paddleError(res, problem.status, problem.code, problem.detail);
      discount = state.discounts.get(body.discount_id);
    }
    // Mode tax: 10% of each line's subtotal, added to the line and the totals (the site must drop such a price).
    const lines = lineTotals(body.items, discount).map((l) => {
      if (state.pricingPreviewMode !== 'tax') return l;
      const tax = Math.round(l.subtotal * 0.1);
      return { ...l, tax, total: l.total + tax };
    });
    const totals = sumTotals(lines);
    return ok(res, {
      customer_id: null,
      address_id: null,
      business_id: null,
      currency_code: 'USD',
      discount_id: discount?.id ?? null,
      items: body.items.map((i) => ({ price_id: i.price_id, quantity: i.quantity ?? 1 })),
      details: {
        line_items: lines.map((l) => ({
          price: { id: l.price_id, product_id: `pro_${l.price_id.replace(/^pri_/, '')}`, description: l.price_id },
          quantity: l.quantity,
          // One unit of the line, discount and tax included, as Paddle reports it (the app shows these
          // for a per-seat plan; it never divides a line total itself).
          unit_totals: stringTotals(unitTotals(l)),
          formatted_unit_totals: formattedTotals(unitTotals(l)),
          totals: stringTotals(l),
          formatted_totals: formattedTotals(l),
        })),
        totals: stringTotals(totals),
        formatted_totals: formattedTotals(totals),
      },
    });
  }

  // GET /transactions?subscription_id=sub_1&status=completed&origin=subscription_update
  //   &billed_at[GTE]=2026-01-01T00:00:00Z&per_page=50&after=txn_1 — list, filtered by every given
  //   parameter (id, customer_id, subscription_id, status, origin: comma lists; billed_at / updated_at:
  //   exact or [LT|LTE|GT|GTE]), oldest first, paginated with `after` as Paddle does.
  if (method === 'GET' && parts.length === 1 && parts[0] === 'transactions') {
    const query = new URL(req.url, 'http://mock.local').searchParams;
    const filters = {
      id: listFilter(query, 'id'),
      customer_id: listFilter(query, 'customer_id'),
      subscription_id: listFilter(query, 'subscription_id'),
      status: listFilter(query, 'status'),
      origin: listFilter(query, 'origin'),
    };
    const ranges = [];
    for (const field of ['billed_at', 'updated_at']) {
      for (const [suffix, test] of [['', (a, b) => a === b], ['[LT]', (a, b) => a < b], ['[LTE]', (a, b) => a <= b], ['[GT]', (a, b) => a > b], ['[GTE]', (a, b) => a >= b]]) {
        const raw = query.get(`${field}${suffix}`);
        if (raw === null || raw.trim() === '') continue;
        const bound = parseTime(raw);
        if (Number.isNaN(bound)) return paddleError(res, 400, 'bad_request', `${field}${suffix} must be an RFC 3339 datetime.`);
        ranges.push({ field, test, bound });
      }
    }
    const perPageRaw = query.get('per_page');
    const perPage = perPageRaw === null || perPageRaw === '' ? 30 : Number(perPageRaw);
    if (!Number.isInteger(perPage) || perPage < 1 || perPage > 200) return paddleError(res, 400, 'bad_request', 'per_page must be an integer from 1 to 200.');
    let items = [...state.transactions.values()].map(transactionPayload).filter((t) =>
      Object.entries(filters).every(([k, allowed]) => !allowed || allowed.includes(t[k]))
      && ranges.every(({ field, test, bound }) => t[field] != null && test(parseTime(t[field]), bound)));
    const total = items.length;
    const after = query.get('after');
    if (after) {
      const at = items.findIndex((t) => t.id === after);
      items = at < 0 ? [] : items.slice(at + 1);
    }
    const pageItems = items.slice(0, perPage);
    const hasMore = items.length > perPage;
    const next = new URL(req.url, PUBLIC_URL);
    if (pageItems.length > 0) next.searchParams.set('after', pageItems.at(-1).id);
    return send(res, 200, {
      data: pageItems,
      meta: {
        request_id: newId('req'),
        pagination: { per_page: perPage, next: `${PUBLIC_URL}${next.pathname}${next.search}`, has_more: hasMore, estimated_total: total },
      },
    });
  }

  // GET /transactions/{id}
  if (method === 'GET' && parts.length === 2 && parts[0] === 'transactions') {
    const txn = state.transactions.get(parts[1]);
    return txn ? ok(res, transactionPayload(txn)) : paddleError(res, 404, 'entity_not_found', `Transaction ${parts[1]} not found.`);
  }

  // PATCH /transactions/{id}  — only {"status":"canceled"} (what the Inference API sends to cancel a
  // superseded or expired promotion checkout). Paddle cancels a transaction that can still be paid
  // (draft or ready); any other one answers transaction_immutable and is left alone.
  if (method === 'PATCH' && parts.length === 2 && parts[0] === 'transactions') {
    const txn = state.transactions.get(parts[1]);
    if (!txn) return paddleError(res, 404, 'not_found', `Transaction ${parts[1]} not found.`);
    const keys = body && typeof body === 'object' ? Object.keys(body) : [];
    if (keys.length !== 1 || body.status !== 'canceled') {
      return paddleError(res, 400, 'invalid_field', 'The mock accepts only { "status": "canceled" } on PATCH /transactions/{id}.');
    }
    if (txn.status !== 'draft' && txn.status !== 'ready') {
      return paddleError(res, 400, 'transaction_immutable', `Transaction ${txn.id} is ${txn.status} and cannot be changed.`);
    }
    txn.status = 'canceled';
    txn.updated_at = iso(new Date());
    log(`canceled ${txn.id}`);
    return ok(res, transactionPayload(txn));
  }

  // POST /customers
  if (method === 'POST' && parts.length === 1 && parts[0] === 'customers') {
    const email = typeof body.email === 'string' ? body.email.trim().toLowerCase() : '';
    if (!email || !email.includes('@')) return paddleError(res, 400, 'bad_request', 'email is required.');
    for (const c of state.customers.values()) {
      if (c.email === email) {
        // Paddle answers 409 with the existing id in the detail.
        return paddleError(res, 409, 'customer_already_exists', `customer email conflicts with customer of id ${c.id}`);
      }
    }
    const now = iso(new Date());
    const c = { id: newId('ctm'), email, name: body.name ?? null, status: 'active', custom_data: body.custom_data ?? null, created_at: now, updated_at: now };
    state.customers.set(c.id, c);
    return ok(res, c, 201);
  }

  // GET /customers?email=a@b.c,d@e.f — list, filtered by email (comma list, case-insensitive).
  if (method === 'GET' && parts.length === 1 && parts[0] === 'customers') {
    const query = new URL(req.url, 'http://mock.local').searchParams;
    const emails = listFilter(query, 'email')?.map((e) => e.toLowerCase());
    const ids = listFilter(query, 'id');
    const items = [...state.customers.values()].filter((c) => (!emails || emails.includes(c.email)) && (!ids || ids.includes(c.id)));
    return okList(res, req, items);
  }

  // GET /customers/{id}
  if (method === 'GET' && parts.length === 2 && parts[0] === 'customers') {
    const c = state.customers.get(parts[1]);
    return c ? ok(res, c) : paddleError(res, 404, 'entity_not_found', `Customer ${parts[1]} not found.`);
  }

  // POST /customers/{id}/portal-sessions
  if (method === 'POST' && parts.length === 3 && parts[0] === 'customers' && parts[2] === 'portal-sessions') {
    const c = state.customers.get(parts[1]);
    if (!c) return paddleError(res, 404, 'entity_not_found', `Customer ${parts[1]} not found.`);
    const subIds = body.subscription_ids ?? [];
    if (!Array.isArray(subIds)) return paddleError(res, 400, 'bad_request', 'subscription_ids must be an array.');
    for (const s of subIds) {
      const sub = state.subscriptions.get(s);
      if (!sub || sub.customer_id !== c.id) return paddleError(res, 400, 'bad_request', `Subscription ${s} does not belong to customer ${c.id}.`);
    }
    const id = newId('cpls');
    const base = `${PUBLIC_URL}/portal/${id}`;
    const session = {
      id,
      customer_id: c.id,
      urls: {
        general: { overview: `${base}/overview` },
        subscriptions: subIds.map((s) => ({
          id: s,
          cancel_subscription: `${base}/subscriptions/${s}/cancel`,
          update_subscription_payment_method: `${base}/subscriptions/${s}/update-payment-method`,
        })),
      },
      created_at: iso(new Date()),
    };
    state.portalSessions.set(id, session);
    return ok(res, session, 201);
  }

  // GET /subscriptions?customer_id=ctm_1&status=active,trialing — list, filtered (each a comma list).
  if (method === 'GET' && parts.length === 1 && parts[0] === 'subscriptions') {
    const query = new URL(req.url, 'http://mock.local').searchParams;
    const customers = listFilter(query, 'customer_id');
    const statuses = listFilter(query, 'status');
    const ids = listFilter(query, 'id');
    const items = [...state.subscriptions.values()]
      .filter((sub) => (!customers || customers.includes(sub.customer_id)) && (!statuses || statuses.includes(sub.status)) && (!ids || ids.includes(sub.id)))
      .map(subscriptionView);
    return okList(res, req, items);
  }

  // GET /subscriptions/{id}
  if (method === 'GET' && parts.length === 2 && parts[0] === 'subscriptions') {
    const sub = state.subscriptions.get(parts[1]);
    return sub ? ok(res, subscriptionView(sub)) : paddleError(res, 404, 'entity_not_found', `Subscription ${parts[1]} not found.`);
  }

  // PATCH /subscriptions/{id}
  if (method === 'PATCH' && parts.length === 2 && parts[0] === 'subscriptions') {
    const sub = state.subscriptions.get(parts[1]);
    if (!sub) return paddleError(res, 404, 'entity_not_found', `Subscription ${parts[1]} not found.`);
    if (sub.status === 'canceled') {
      return paddleError(res, 400, 'subscription_update_when_canceled', 'Subscription is canceled and cannot be updated.');
    }
    const hasItems = Object.prototype.hasOwnProperty.call(body, 'items');
    const clearsSchedule = Object.prototype.hasOwnProperty.call(body, 'scheduled_change') && body.scheduled_change === null;
    if (Object.prototype.hasOwnProperty.call(body, 'scheduled_change') && body.scheduled_change !== null) {
      return paddleError(res, 400, 'bad_request', 'scheduled_change can only be set to null (use the cancel or pause operations to schedule a change).');
    }
    // `discount` as Paddle takes it: absent = unchanged, null = removed, {id, effective_from} = applied.
    const hasDiscount = Object.prototype.hasOwnProperty.call(body, 'discount');
    if (hasDiscount && body.discount !== null) {
      const d = body.discount;
      if (typeof d !== 'object' || typeof d.id !== 'string' || !['immediately', 'next_billing_period'].includes(d.effective_from)) {
        return paddleError(res, 400, 'bad_request', 'discount must be null or { id, effective_from: immediately|next_billing_period }.');
      }
      const known = state.discounts.get(d.id);
      if (!known || known.status !== 'active') {
        return paddleError(res, 400, 'subscription_discount_not_valid', `Discount ${d.id} not found or not active.`);
      }
    }
    if (!hasItems && !clearsSchedule && !hasDiscount) {
      return paddleError(res, 400, 'bad_request', 'Nothing to update: send items (with proration_billing_mode), scheduled_change: null or discount.');
    }
    if (hasItems) {
      const err = validateItems(body.items);
      if (err) return paddleError(res, 400, 'bad_request', err);
      const modes = ['prorated_immediately', 'prorated_next_billing_period', 'full_immediately', 'full_next_billing_period', 'do_not_bill'];
      if (!modes.includes(body.proration_billing_mode)) {
        return paddleError(res, 400, 'bad_request', `proration_billing_mode is required when items change; one of ${modes.join(', ')}.`);
      }
      if (sub.scheduled_change && !clearsSchedule) {
        return paddleError(res, 400, 'subscription_locked_pending_changes',
          `Subscription has a scheduled ${sub.scheduled_change.action} and its items cannot change; remove the scheduled change first.`);
      }
    }
    // One instant for the whole change: subscription.updated's occurred_at and the proration's
    // billing_period.starts_at (the backend pairs a payment with its upgrade on the charged items, and
    // only within 10 min of these two).
    const changedAt = nextOccurred();
    if (clearsSchedule) sub.scheduled_change = null;
    if (hasDiscount) {
      sub.discount = body.discount === null ? null : { id: body.discount.id, starts_at: changedAt, ends_at: null };
    }
    let proration = null;
    if (hasItems) {
      const nextItems = body.items.map((i) => ({ price_id: i.price_id, quantity: i.quantity ?? 1 }));
      const previousItems = plainItems(sub);
      const itemsChanged = JSON.stringify(nextItems) !== JSON.stringify(previousItems);
      sub.items = subscriptionItems(nextItems, changedAt);
      sub.last_proration_billing_mode = body.proration_billing_mode;
      // Paddle bills a prorated_immediately change at once: a transaction with origin
      // subscription_update. The Inference API sends that mode only for upgrades (a downgrade is
      // full_next_billing_period), so the mock charges whenever the items change in that mode.
      if (body.proration_billing_mode === 'prorated_immediately' && itemsChanged) {
        proration = createProrationTransaction(sub, nextItems, changedAt, previousItems);
      }
    }
    sub.updated_at = changedAt;
    enqueueWebhook('subscription.updated', subscriptionView(JSON.parse(JSON.stringify(sub))), { occurredAt: changedAt });
    if (proration) settleProration(proration);
    return ok(res, subscriptionView(sub));
  }

  // POST /subscriptions/{id}/cancel
  if (method === 'POST' && parts.length === 3 && parts[0] === 'subscriptions' && parts[2] === 'cancel') {
    const sub = state.subscriptions.get(parts[1]);
    if (!sub) return paddleError(res, 404, 'entity_not_found', `Subscription ${parts[1]} not found.`);
    if (sub.status === 'canceled') return paddleError(res, 400, 'subscription_is_canceled_action_invalid', 'Subscription is already canceled.');
    const effective = body.effective_from ?? 'next_billing_period';
    if (effective === 'immediately') {
      cancelNow(sub);
      return ok(res, subscriptionView(sub));
    }
    if (effective !== 'next_billing_period') {
      return paddleError(res, 400, 'bad_request', 'effective_from must be next_billing_period or immediately.');
    }
    if (sub.scheduled_change?.action === 'cancel') {
      return paddleError(res, 400, 'subscription_locked_pending_changes', 'Subscription already has a scheduled cancellation.');
    }
    sub.scheduled_change = { action: 'cancel', effective_at: sub.current_billing_period?.ends_at ?? iso(new Date()), resume_at: null };
    touch(sub);
    subscriptionWebhook('subscription.updated', sub);
    return ok(res, subscriptionView(sub));
  }

  return paddleError(res, 404, 'not_found', `No route for ${method} /${parts.join('/')} in the Paddle mock.`);
}

function cancelNow(sub) {
  sub.status = 'canceled';
  sub.scheduled_change = null;
  sub.current_billing_period = null;
  sub.next_billed_at = null;
  sub.canceled_at = touch(sub);
  return subscriptionWebhook('subscription.canceled', sub);
}

// ------------------------------------------------------------------ browser pages

function checkoutPage(res, txn) {
  if (!txn) return send(res, 404, page('Checkout not found', '<h1>Checkout not found</h1><p>This transaction does not exist in the mock (was it reset?).</p>'));
  const rows = txn.items.map((i) => `<tr><td><code>${escapeHtml(i.price_id)}</code></td><td>${i.quantity}</td></tr>`).join('');
  const kind = txn.custom_data?.kind === 'topup' ? `Top-up: ${escapeHtml(txn.custom_data?.credits)} credits` : `Subscription: ${escapeHtml(txn.custom_data?.plan_key ?? '?')}`;
  const done = txn.status === 'completed';
  const body = `
<h1 data-testid="mock-checkout-heading">Paddle checkout (mock)</h1>
<p data-testid="mock-checkout-kind">${kind}</p>
<p>Transaction <code data-testid="mock-checkout-txn">${escapeHtml(txn.id)}</code> — status <strong data-testid="mock-checkout-status">${escapeHtml(txn.status)}</strong></p>
<table><thead><tr><th>Price</th><th>Quantity</th></tr></thead><tbody>${rows}</tbody></table>
${done ? '<p>This transaction is already paid.</p>' : ''}${txn.status === 'canceled' ? '<p data-testid="mock-checkout-canceled">This transaction was canceled and cannot be paid.</p>' : ''}
<form method="post" action="/checkout/${encodeURIComponent(txn.id)}/pay" style="display:inline">
  <button type="submit" data-testid="mock-pay">${done ? 'Return to the app' : 'Pay now'}</button>
</form>
<a href="${escapeHtml(APP_PUBLIC_URL)}/app/settings/billing" data-testid="mock-checkout-cancel"><button type="button" class="secondary">Cancel</button></a>`;
  return send(res, 200, page('Paddle checkout (mock)', body));
}

function portalPage(res, parts) {
  // /portal/{sessionId}/overview
  // /portal/{sessionId}/subscriptions/{subId}/(cancel|update-payment-method)
  const [sessionId, section, subId, action] = parts;
  const session = state.portalSessions.get(sessionId);
  const known = sessionId === 'direct' || session;
  if (!known) return send(res, 404, page('Portal session not found', '<h1>Portal session not found</h1>'));
  let title = 'Overview';
  let extra = '';
  if (section === 'subscriptions' && subId) {
    const sub = state.subscriptions.get(subId);
    if (!sub) return send(res, 404, page('Subscription not found', '<h1>Subscription not found</h1>'));
    if (action === 'cancel') {
      title = 'Cancel subscription';
      extra = `<p>Subscription <code>${escapeHtml(sub.id)}</code> — status ${escapeHtml(sub.status)}${sub.scheduled_change ? `, scheduled ${escapeHtml(sub.scheduled_change.action)} at ${escapeHtml(sub.scheduled_change.effective_at)}` : ''}</p>
<form method="post" action="/portal/${encodeURIComponent(sessionId)}/subscriptions/${encodeURIComponent(sub.id)}/cancel">
  <button type="submit" data-testid="mock-portal-cancel">Cancel at the end of the billing period</button>
</form>`;
    } else if (action === 'update-payment-method') {
      title = 'Update payment method';
      extra = `<p>Subscription <code>${escapeHtml(sub.id)}</code>. The mock has no card to update.</p>`;
    } else {
      return send(res, 404, page('Not found', '<h1>Unknown portal page</h1>'));
    }
  } else if (section !== 'overview') {
    return send(res, 404, page('Not found', '<h1>Unknown portal page</h1>'));
  } else {
    const customerId = session?.customer_id;
    const subs = [...state.subscriptions.values()].filter((s) => s.customer_id === customerId);
    extra = `<p>Customer <code data-testid="mock-portal-customer">${escapeHtml(customerId ?? '?')}</code></p>
<ul>${subs.map((s) => `<li><code>${escapeHtml(s.id)}</code> — ${escapeHtml(s.status)} — ${s.items.map((i) => `${escapeHtml(i.price.id)} × ${i.quantity}`).join(', ')}</li>`).join('') || '<li>No subscriptions</li>'}</ul>`;
  }
  const body = `
<h1 data-testid="mock-portal-heading">Paddle customer portal (mock)</h1>
<h2 data-testid="mock-portal-section">${escapeHtml(title)}</h2>
${extra}
<p><a href="${escapeHtml(APP_PUBLIC_URL)}/app/settings/billing" data-testid="mock-portal-back">Back to ReelBolt</a></p>`;
  return send(res, 200, page(`Paddle customer portal (mock) — ${title}`, body));
}

// ------------------------------------------------------------------ control API

async function handleControl(req, res, method, parts, body, url) {
  if (method === 'GET' && parts[0] === 'health') return send(res, 200, { ok: true });

  if (method === 'GET' && parts[0] === 'state') {
    return send(res, 200, {
      customers: [...state.customers.values()],
      transactions: [...state.transactions.values()].map(transactionPayload),
      subscriptions: [...state.subscriptions.values()].map(subscriptionView),
      portalSessions: [...state.portalSessions.values()],
      webhooks: state.webhooks,
      dropFilters: state.dropFilters,
    });
  }

  if (method === 'GET' && parts[0] === 'calls') {
    const filter = url.searchParams.get('path');
    const m = url.searchParams.get('method');
    let calls = state.calls;
    if (filter) calls = calls.filter((c) => c.path.includes(filter));
    if (m) calls = calls.filter((c) => c.method === m.toUpperCase());
    return send(res, 200, { calls });
  }

  if (method === 'GET' && parts[0] === 'webhooks' && parts.length === 1) return send(res, 200, { webhooks: state.webhooks, dropFilters: state.dropFilters });

  // POST /__mock/webhooks/drop  body: { event_type?, subscription_id?, transaction_id?, count? = 1 } —
  // the next `count` webhooks matching every given field are lost: recorded with dropped: true, never
  // sent. Cleared by /__mock/reset.
  if (method === 'POST' && parts[0] === 'webhooks' && parts.length === 2 && parts[1] === 'drop') {
    for (const k of ['event_type', 'subscription_id', 'transaction_id']) {
      if (body[k] !== undefined && body[k] !== null && (typeof body[k] !== 'string' || body[k].trim() === '')) {
        return paddleError(res, 400, 'bad_request', `${k} must be a non-empty string when given.`);
      }
    }
    const count = body.count ?? 1;
    if (!Number.isInteger(count) || count < 1) return paddleError(res, 400, 'bad_request', 'count must be a positive integer.');
    const filter = {
      id: newId('drp'),
      event_type: body.event_type ?? null,
      subscription_id: body.subscription_id ?? null,
      transaction_id: body.transaction_id ?? null,
      count,
      remaining: count,
      dropped: 0,
    };
    state.dropFilters.push(filter);
    log(`drop filter ${filter.id}: ${JSON.stringify({ event_type: filter.event_type, subscription_id: filter.subscription_id, transaction_id: filter.transaction_id, count })}`);
    return send(res, 200, { filter });
  }

  // POST /__mock/webhooks/{deliveryId}/redeliver  body: { attempts? = 1 } — sends a recorded delivery
  // (dropped or not) again with its original event_id and occurred_at, freshly signed, never dropped;
  // recorded as a new delivery with redelivery_of. Answers the receiver's status.
  if (method === 'POST' && parts[0] === 'webhooks' && parts.length === 3 && parts[2] === 'redeliver') {
    const event = state.deliveryEvents.get(parts[1]);
    if (!event) return paddleError(res, 404, 'entity_not_found', `Webhook delivery ${parts[1]} not found.`);
    const attempts = body.attempts ?? 1;
    if (!Number.isInteger(attempts) || attempts < 1) return paddleError(res, 400, 'bad_request', 'attempts must be a positive integer.');
    const p = deliveryChain.then(() => deliver(event, { attempts, redeliveryOf: parts[1] }));
    deliveryChain = p.catch(() => {});
    const record = await p;
    return send(res, 200, { event, delivery: record, status: record.attempts.at(-1)?.status ?? 0 });
  }

  // GET /__mock/discounts — every discount the Inference API created, with status, expiry and times_used.
  if (method === 'GET' && parts[0] === 'discounts' && parts.length === 1) {
    return send(res, 200, { discounts: [...state.discounts.values()] });
  }

  // GET /__mock/transactions — each transaction as Paddle shows it plus `request`, the exact body the
  // Inference API sent (so a spec can assert discount_id and custom_data.promotion_id).
  if (method === 'GET' && parts[0] === 'transactions' && parts.length === 1) {
    return send(res, 200, { transactions: [...state.transactions.values()].map((t) => ({ ...transactionPayload(t), request: t.request_body ?? null })) });
  }

  // POST /__mock/pricing-preview/mode  body: { mode: 'ok' | 'error' | 'tax' } — the fault knob for
  //   POST /pricing-preview: 'error' answers every preview 500, 'tax' adds 10% tax to each line, 'ok'
  //   restores the normal preview. Cleared by /__mock/reset.
  if (method === 'POST' && parts[0] === 'pricing-preview' && parts[1] === 'mode' && parts.length === 2) {
    if (!['ok', 'error', 'tax'].includes(body.mode)) return paddleError(res, 400, 'bad_request', "mode must be 'ok', 'error' or 'tax'.");
    state.pricingPreviewMode = body.mode;
    log('pricing-preview mode', body.mode);
    return send(res, 200, { mode: body.mode });
  }

  if (method === 'POST' && parts[0] === 'reset') {
    resetState();
    paymentModes.clear();
    log('state reset');
    return send(res, 200, { ok: true });
  }

  // POST /__mock/transactions/{id}/complete  body: { delayMs? }
  if (method === 'POST' && parts[0] === 'transactions' && parts.length === 3 && parts[2] === 'complete') {
    const txn = state.transactions.get(parts[1]);
    if (!txn) return paddleError(res, 404, 'entity_not_found', `Transaction ${parts[1]} not found.`);
    if (txn.status === 'canceled') return paddleError(res, 400, 'transaction_immutable', `Transaction ${txn.id} is canceled and cannot be paid.`);
    if (txn.origin === 'subscription_update') {
      // Releases a held (or failed, then retried) proration charge: transaction.completed, awaited.
      const alreadyCompleted = txn.status === 'completed';
      const delivery = await payProration(txn);
      return send(res, 200, { transaction: transactionPayload(txn), subscription: null, alreadyCompleted, webhook: delivery });
    }
    const r = completeTransaction(txn, { delayMs: body.delayMs ?? WEBHOOK_DELAY_MS });
    return send(res, 200, { transaction: transactionPayload(r.transaction), subscription: r.subscription ? subscriptionView(r.subscription) : null, alreadyCompleted: r.alreadyCompleted });
  }

  // POST /__mock/transactions/{id}/fail  body: { notify? = true } — a held proration charge fails:
  // transaction.payment_failed (unless notify is false); never completed.
  if (method === 'POST' && parts[0] === 'transactions' && parts.length === 3 && parts[2] === 'fail') {
    const txn = state.transactions.get(parts[1]);
    if (!txn) return paddleError(res, 404, 'entity_not_found', `Transaction ${parts[1]} not found.`);
    if (txn.status === 'completed') return paddleError(res, 400, 'transaction_immutable', `Transaction ${txn.id} is already completed.`);
    const delivery = await failProration(txn, { notify: body.notify !== false });
    return send(res, 200, { transaction: transactionPayload(txn), webhook: delivery });
  }

  // POST /__mock/transactions/{id}/refund  body: { action? = refund|chargeback, type? = full|partial,
  // status? = approved } — adjustment.created for a completed transaction, awaited.
  if (method === 'POST' && parts[0] === 'transactions' && parts.length === 3 && parts[2] === 'refund') {
    const txn = state.transactions.get(parts[1]);
    if (!txn) return paddleError(res, 404, 'entity_not_found', `Transaction ${parts[1]} not found.`);
    if (txn.status !== 'completed') return paddleError(res, 400, 'transaction_not_completed', `Transaction ${txn.id} is ${txn.status}; only a completed one can be refunded.`);
    const { adjustment, delivery } = refundTransaction(txn, { action: body.action, type: body.type, status: body.status });
    return send(res, 200, { adjustment, webhook: await delivery });
  }

  // POST /__mock/subscriptions/{id}/payments  body: { mode: pay|hold|fail } — what the subscription's
  // next proration charges do (default pay).
  if (method === 'POST' && parts[0] === 'subscriptions' && parts.length === 3 && parts[2] === 'payments') {
    if (!state.subscriptions.has(parts[1])) return paddleError(res, 404, 'entity_not_found', `Subscription ${parts[1]} not found.`);
    if (!['pay', 'hold', 'fail'].includes(body.mode)) return paddleError(res, 400, 'bad_request', 'mode must be pay, hold or fail.');
    paymentModes.set(parts[1], body.mode);
    return send(res, 200, { subscription_id: parts[1], mode: body.mode });
  }

  // GET /__mock/subscriptions/{id}/transactions — the subscription's proration charges, oldest first.
  if (method === 'GET' && parts[0] === 'subscriptions' && parts.length === 3 && parts[2] === 'transactions') {
    const txns = [...state.transactions.values()].filter((t) => t.subscription_id === parts[1] && t.origin === 'subscription_update');
    return send(res, 200, { transactions: txns.map(transactionPayload) });
  }

  // POST /__mock/subscriptions/{id}/advance-period — the renewal at period end.
  if (method === 'POST' && parts[0] === 'subscriptions' && parts.length === 3 && parts[2] === 'advance-period') {
    const sub = state.subscriptions.get(parts[1]);
    if (!sub) return paddleError(res, 404, 'entity_not_found', `Subscription ${parts[1]} not found.`);
    if (sub.status === 'canceled') return paddleError(res, 400, 'subscription_is_canceled_action_invalid', 'Subscription is canceled.');
    let delivery;
    if (sub.scheduled_change?.action === 'cancel') {
      delivery = await cancelNow(sub);
    } else {
      const start = sub.current_billing_period ? new Date(sub.current_billing_period.ends_at) : new Date();
      const end = addMonth(start);
      sub.current_billing_period = { starts_at: iso(start), ends_at: iso(end) };
      sub.next_billed_at = iso(end);
      for (const i of sub.items) i.previously_billed_at = iso(start);
      touch(sub);
      delivery = await subscriptionWebhook('subscription.updated', sub);
    }
    return send(res, 200, { subscription: subscriptionView(sub), webhook: delivery });
  }

  // POST /__mock/subscriptions/{id}/cancel  body: { effective_from? } — same as the API, without auth.
  if (method === 'POST' && parts[0] === 'subscriptions' && parts.length === 3 && parts[2] === 'cancel') {
    return handleApi(req, res, 'POST', ['subscriptions', parts[1], 'cancel'], body);
  }

  // POST /__mock/webhooks/send  body: { event_type, data, event_id?, occurred_at?, secret?, signature? }
  //   or { payload: <full event object>, secret?, signature? } — sent synchronously, result returned.
  if (method === 'POST' && parts[0] === 'webhooks' && parts[1] === 'send') {
    let event;
    if (body.payload && typeof body.payload === 'object') {
      event = body.payload;
    } else if (typeof body.event_type === 'string' && body.data && typeof body.data === 'object') {
      event = buildEvent(body.event_type, body.data, { eventId: body.event_id, occurredAt: body.occurred_at });
    } else {
      return paddleError(res, 400, 'bad_request', 'Send { event_type, data } or { payload }.');
    }
    const p = deliveryChain.then(() => deliver(event, { secret: body.secret, signature: body.signature, attempts: body.attempts ?? 1 }));
    deliveryChain = p.catch(() => {});
    const record = await p;
    return send(res, 200, { event, delivery: record });
  }

  return paddleError(res, 404, 'not_found', `No mock control route for ${method} /__mock/${parts.join('/')}.`);
}

// ------------------------------------------------------------------ server

const server = createServer(async (req, res) => {
  const url = new URL(req.url, 'http://mock.local');
  const method = req.method.toUpperCase();
  const parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
  let raw = '';
  try {
    raw = await readBody(req);
  } catch {
    return paddleError(res, 400, 'bad_request', 'Could not read the request body.');
  }

  // Browser form posts arrive form-encoded (or empty); the API speaks JSON.
  let body = {};
  const contentType = req.headers['content-type'] || '';
  if (raw && contentType.includes('application/json')) {
    try {
      body = parseJson(raw);
    } catch {
      return paddleError(res, 400, 'invalid_json', 'Request body is not valid JSON.');
    }
  }

  try {
    if (parts[0] === '__mock') return await handleControl(req, res, method, parts.slice(1), body, url);

    if (parts[0] === 'checkout' && parts.length >= 2) {
      const txn = state.transactions.get(parts[1]);
      if (method === 'GET' && parts.length === 2) return checkoutPage(res, txn);
      if (method === 'POST' && parts.length === 3 && parts[2] === 'pay') {
        if (!txn || txn.status === 'canceled') return checkoutPage(res, txn);
        completeTransaction(txn);
        // Paddle's hosted checkout returns the buyer to the default payment link with ?_ptxn=.
        res.writeHead(303, { location: `${APP_PUBLIC_URL}/app/checkout?_ptxn=${encodeURIComponent(txn.id)}` });
        return res.end();
      }
    }

    if (parts[0] === 'portal') {
      if (method === 'GET') return portalPage(res, parts.slice(1));
      // The portal's own cancel button: schedules a cancel at period end, as Paddle's portal does.
      if (method === 'POST' && parts.length === 5 && parts[2] === 'subscriptions' && parts[4] === 'cancel') {
        const sub = state.subscriptions.get(parts[3]);
        if (sub && sub.status !== 'canceled' && !sub.scheduled_change) {
          sub.scheduled_change = { action: 'cancel', effective_at: sub.current_billing_period?.ends_at ?? iso(new Date()), resume_at: null };
          touch(sub);
          subscriptionWebhook('subscription.updated', sub);
        }
        res.writeHead(303, { location: `${PUBLIC_URL}/portal/${encodeURIComponent(parts[1])}/subscriptions/${encodeURIComponent(parts[3])}/cancel` });
        return res.end();
      }
    }

    // Everything else is the Paddle API.
    const call = { at: new Date().toISOString(), method, path: url.pathname, query: url.search || undefined, body: raw ? safeJson(raw) : null, paddleVersion: req.headers['paddle-version'] ?? null, status: 0 };
    state.calls.push(call);
    const origEnd = res.end.bind(res);
    res.end = (...args) => {
      call.status = res.statusCode;
      return origEnd(...args);
    };
    if (!requireBearer(req, res)) return;
    if (raw && !contentType.includes('application/json')) {
      return paddleError(res, 415, 'unsupported_media_type', 'Send application/json.');
    }
    return await handleApi(req, res, method, parts, body);
  } catch (err) {
    log('handler error', err);
    if (!res.headersSent) return paddleError(res, 500, 'internal_error', 'Internal mock error.', 'api_error');
  }
});

function safeJson(raw) {
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

server.listen(PORT, '0.0.0.0', () => {
  log(`listening on :${PORT}; public ${PUBLIC_URL}; app ${APP_PUBLIC_URL}; webhooks -> ${WEBHOOK_URL}${WEBHOOK_SECRET ? '' : ' (NO SECRET: deliveries will be rejected)'}`);
});

for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    log(`${sig}, shutting down`);
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
  });
}
