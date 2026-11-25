// A stateful stand-in for the Stripe API (version 2026-09-30.endive) for the isolated e2e stack. Zero
// dependencies, in-memory state, one process. It speaks exactly the slice of Stripe the Inference API
// uses (inference/src/ReelBolt.Inference.Api/Services/Billing/Stripe/StripeBillingProvider.cs and
// StripeHttp.cs) and sends signed webhooks back the way Stripe does, so the dashboard's billing flows
// run end to end with no network access and no Stripe account. Test mode only: nothing here is a real
// payment, and the only "key" it knows is the one the stack generates (sk_test_e2e_mock...).
//
// Surfaces:
//   - the API under /v1 (Bearer auth, form-encoded bodies with Stripe's nested syntax, Stripe-shaped
//     errors, Idempotency-Key cache) that the Inference API calls;
//   - browser pages: the hosted checkout `GET|POST /pay/{sessionId}` and the portal `GET /portal/{id}`;
//   - a control API under /__mock/ for specs and the seed script (no auth: published on 127.0.0.1
//     only, it exists to drive the fake).
//
// It rejects any parameter the provider does not send (`parameter_unknown`) on purpose, so the allow lists below
// are the contract: change them together with StripeBillingProvider.cs. Notable behavior it reproduces:
// `payment_behavior=error_if_incomplete` (an update is applied only if its proration invoice is paid, else 402
// and nothing changes), Stripe Tax's `customer_update` rules and their absence under Managed Payments, and
// chargebacks (`POST /__mock/disputes`).
//
// Environment (only read when the file is run directly; tests call createMock(options)):
//   PORT                  listen port inside the container (default 8080)
//   STRIPE_API_KEY        the Bearer token /v1 routes require (empty = any non-empty token)
//   STRIPE_WEBHOOK_SECRET signing secret (whsec_...) for Stripe-Signature
//   WEBHOOK_URL           where webhooks go (http://inference:8080/api/v1/billing/webhooks/stripe)
//   PUBLIC_URL            this fake as the BROWSER reaches it (http://localhost:29201)
//   APP_PUBLIC_URL        the app as the browser reaches it (http://localhost:8180)
//   WEBHOOK_DELAY_MS      delay before each webhook is sent (default 300)
//   WEBHOOK_RETRY_DELAY_MS base delay between the 4 delivery attempts (default 1000, times the attempt)
//   CATALOG_PATH          plans.catalog.json (default: the repository's canonical copy). Every amount
//                         comes from it; there is no price literal in this file.
//   FAKE_FEE_MINOR        the processing fee every balance transaction reports (default 100)

import { createServer } from 'node:http';
import { createHmac, randomInt } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const API_VERSION = '2026-09-30.endive';

const HERE = dirname(fileURLToPath(import.meta.url));
const DEFAULT_CATALOG = resolve(HERE, '..', '..', 'inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json');
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);
const WEBHOOK_ATTEMPTS = 4;

// ------------------------------------------------------------------ form encoding (Stripe's syntax)

function decode(s) {
  return decodeURIComponent(s.replace(/\+/g, ' '));
}

function keyPath(key) {
  const m = /^([^[\]]+)((?:\[[^[\]]*\])*)$/.exec(key);
  if (!m) return [key];
  const path = [m[1]];
  for (const part of m[2].matchAll(/\[([^[\]]*)\]/g)) path.push(part[1]);
  return path;
}

function assign(root, path, value) {
  let node = root;
  for (let i = 0; i < path.length; i++) {
    const seg = path[i];
    if (FORBIDDEN_KEYS.has(seg)) return;
    const next = path[i + 1];
    if (next === undefined) {
      node[seg] = value;
      return;
    }
    if (next === '') {
      if (!Array.isArray(node[seg])) node[seg] = [];
      if (i + 2 >= path.length) {
        node[seg].push(value);
        return;
      }
      const child = {};
      node[seg].push(child);
      node = child;
      i++;
      continue;
    }
    if (typeof node[seg] !== 'object' || node[seg] === null || Array.isArray(node[seg])) node[seg] = {};
    node = node[seg];
  }
}

function normalize(v) {
  if (Array.isArray(v)) return v.map(normalize);
  if (v && typeof v === 'object') {
    const keys = Object.keys(v);
    if (keys.length > 0 && keys.every((k) => /^\d+$/.test(k))) {
      return keys.sort((a, b) => Number(a) - Number(b)).map((k) => normalize(v[k]));
    }
    for (const k of keys) v[k] = normalize(v[k]);
  }
  return v;
}

/**
 * Parses an application/x-www-form-urlencoded string the way Stripe does: `a[b][c]=1` nests, `x[]=1`
 * and `x[0]=1` make arrays, an empty value stays the empty string (`discounts=` clears), every scalar
 * stays a string.
 */
export function parseForm(text) {
  const root = {};
  if (!text) return root;
  for (const pair of text.split('&')) {
    if (!pair) continue;
    const eq = pair.indexOf('=');
    const key = decode(eq < 0 ? pair : pair.slice(0, eq));
    const value = decode(eq < 0 ? '' : pair.slice(eq + 1));
    assign(root, keyPath(key), value);
  }
  return normalize(root);
}

/** The webhook header: `t=<unix>,v1=<hex>,v0=<hex>` (v0 is the dummy scheme Stripe's test events carry). */
export function signPayload(rawBody, secret, timestamp = Math.floor(Date.now() / 1000)) {
  const v1 = createHmac('sha256', secret).update(`${timestamp}.${rawBody}`).digest('hex');
  const v0 = createHmac('sha256', 'v0-dummy-scheme').update(`${timestamp}.${rawBody}`).digest('hex');
  return `t=${timestamp},v1=${v1},v0=${v0}`;
}

// ------------------------------------------------------------------ small helpers

const ID_ALPHABET = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
function newId(prefix, length = 24) {
  let s = '';
  for (let i = 0; i < length; i++) s += ID_ALPHABET[randomInt(ID_ALPHABET.length)];
  return `${prefix}_${s}`;
}

function addMonths(sec, n) {
  const d = new Date(sec * 1000);
  d.setUTCMonth(d.getUTCMonth() + n);
  return Math.floor(d.getTime() / 1000);
}

/** months one billing period of a recurring price spans: 12 for a yearly price, `interval_count` months otherwise */
function monthsOfPrice(price) {
  const r = price?.recurring;
  if (!r) return 1;
  return (r.interval === 'year' ? 12 : 1) * (Number(r.interval_count) || 1);
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
  .badge{display:inline-block;font-size:12px;padding:2px 8px;border-radius:999px;background:#e0e7ff;color:#312e81}
  table{border-collapse:collapse;width:100%;margin:16px 0}td,th{border-bottom:1px solid #ddd;padding:6px;text-align:left}
  button{font-size:16px;padding:10px 18px;border-radius:8px;border:0;background:#635bff;color:#fff;cursor:pointer}
  button.secondary{background:#e5e7eb;color:#111}
  code{background:#f3f4f6;padding:1px 4px;border-radius:4px}
</style></head><body>
<p><span class="badge">Stripe mock - no real payment</span></p>
${body}
</body></html>`;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const isTrue = (v) => v === true || v === 'true';
const view = (o) => JSON.parse(JSON.stringify(o, (k, v) => (k.startsWith('_') ? undefined : v)));
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** A Stripe API error: `{error: {type, code, message, param}}` with an HTTP status. */
export class ApiError extends Error {
  constructor(status, { type = 'invalid_request_error', code, message, param, declineCode } = {}) {
    super(message);
    this.status = status;
    this.type = type;
    this.code = code;
    this.param = param;
    this.declineCode = declineCode;
  }
}

const invalid = (message, param, code) => new ApiError(400, { message, param, code });
const missingResource = (what, id, param) => new ApiError(400, { code: 'resource_missing', message: `No such ${what}: '${id}'`, param });
/** A resource addressed in the URL path: Stripe answers 404. */
const notFound = (what, id) => new ApiError(404, { code: 'resource_missing', message: `No such ${what}: '${id}'`, param: 'id' });
const missingParam = (param) => new ApiError(400, { code: 'parameter_missing', message: `Missing required param: ${param}.`, param });

// ------------------------------------------------------------------ the fake

/**
 * Builds a fake Stripe. `options` (all optional): apiKey, webhookSecret, webhookUrl, publicUrl,
 * appPublicUrl, webhookDelayMs, webhookRetryDelayMs, catalogPath, feeMinor, now (() => ms).
 * Returns {server, listen, close, reset, state}.
 */
export function createMock(options = {}) {
  const cfg = {
    apiKey: options.apiKey ?? '',
    webhookSecret: options.webhookSecret ?? '',
    webhookUrl: options.webhookUrl ?? 'http://inference:8080/api/v1/billing/webhooks/stripe',
    publicUrl: (options.publicUrl ?? 'http://localhost:8080').replace(/\/+$/, ''),
    appPublicUrl: (options.appPublicUrl ?? 'http://localhost').replace(/\/+$/, ''),
    webhookDelayMs: options.webhookDelayMs ?? 300,
    webhookRetryDelayMs: options.webhookRetryDelayMs ?? 1000,
    catalogPath: options.catalogPath ?? DEFAULT_CATALOG,
    feeMinor: options.feeMinor ?? 100,
    now: options.now ?? (() => Date.now()),
  };
  const nowSec = () => Math.floor(cfg.now() / 1000);
  const log = (...args) => {
    if (!options.quiet) console.log(new Date().toISOString(), '[stripe-mock]', ...args);
  };

  const catalog = JSON.parse(readFileSync(cfg.catalogPath, 'utf8'));
  const currency = String(catalog.currency).toLowerCase();

  /** price/product seeds derived from the catalog, never typed here */
  function catalogObjects() {
    const objects = [];
    const minor = (units) => Math.round(Number(units) * 100);
    for (const plan of catalog.plans ?? []) {
      if (!(plan.priceMonthly > 0)) continue;
      objects.push({ id: `prod_e2e_${plan.id}`, object: 'product', name: plan.name, active: true, metadata: { reelbolt_catalog: '1', plan: plan.id }, created: 1_700_000_000 });
      objects.push({
        id: `price_e2e_${plan.id}`, object: 'price', active: true, currency, product: `prod_e2e_${plan.id}`, type: 'recurring',
        unit_amount: minor(plan.priceMonthly), billing_scheme: 'per_unit', recurring: { interval: 'month', interval_count: 1 },
        lookup_key: `rb:${plan.id}:month:${currency}`, nickname: plan.name, tax_behavior: 'exclusive', metadata: { reelbolt_catalog: '1', plan: plan.id }, created: 1_700_000_000, livemode: false,
      });
      if (plan.priceAnnual > 0) {
        objects.push({
          id: `price_e2e_${plan.id}_annual`, object: 'price', active: true, currency, product: `prod_e2e_${plan.id}`, type: 'recurring',
          unit_amount: minor(plan.priceAnnual), billing_scheme: 'per_unit', recurring: { interval: 'year', interval_count: 1 },
          lookup_key: `rb:${plan.id}:year:${currency}`, nickname: `${plan.name} (annual)`, tax_behavior: 'exclusive', metadata: { reelbolt_catalog: '1', plan: plan.id }, created: 1_700_000_000, livemode: false,
        });
      }
    }
    const packs = catalog.topUp?.packs ?? [];
    if (packs.length > 0) {
      objects.push({ id: 'prod_e2e_topup', object: 'product', name: 'Top-up credits', active: true, metadata: { reelbolt_catalog: '1' }, created: 1_700_000_000 });
    }
    for (const pack of packs) {
      objects.push({
        id: `price_e2e_topup_${pack.credits}`, object: 'price', active: true, currency, product: 'prod_e2e_topup', type: 'one_time',
        unit_amount: minor(pack.price), billing_scheme: 'per_unit', recurring: null,
        lookup_key: `rb:topup:${pack.credits}:${currency}`, nickname: `${pack.credits} credits`, tax_behavior: 'exclusive', metadata: { reelbolt_catalog: '1', credits: String(pack.credits) }, created: 1_700_000_000, livemode: false,
      });
    }
    return objects;
  }

  // ---------------------------------------------------------------- state

  let state;
  let seq = 0;
  let deliveryChain = Promise.resolve();

  function resetState() {
    state = {
      store: new Map(), // every id'd object: customers, sessions, subscriptions, invoices, prices ...
      calls: [],
      webhooks: [],
      dropFilters: [],
      deliveryEvents: new Map(),
      idempotency: new Map(),
    };
    for (const o of catalogObjects()) state.store.set(o.id, { ...o, _seq: ++seq });
  }
  resetState();

  const lookup = (id) => state.store.get(id);
  const all = (type) => [...state.store.values()].filter((o) => o.object === type);

  function register(obj) {
    obj.created ??= nowSec();
    obj.livemode = false;
    obj._seq = ++seq;
    state.store.set(obj.id, obj);
    return obj;
  }

  function metaOf(m, param = 'metadata') {
    if (m === undefined || m === null || m === '') return {};
    if (typeof m !== 'object' || Array.isArray(m)) throw invalid(`Invalid ${param}: must be a hash of strings.`, param);
    const keys = Object.keys(m);
    if (keys.length > 50) throw invalid('You have passed too many keys in metadata (50 max).', param);
    for (const k of keys) {
      if (k.length > 40) throw invalid(`Metadata key "${k.slice(0, 20)}..." is longer than 40 characters.`, param);
      if (typeof m[k] !== 'string' || m[k].length > 500) throw invalid(`Metadata values must be strings of at most 500 characters (key "${k}").`, param);
    }
    return { ...m };
  }

  /** A nested hash parameter that may only carry `allowed` keys; anything else is `parameter_unknown`, like Stripe. */
  function onlyKeys(value, allowed, param) {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) throw invalid(`Invalid ${param}: must be a hash.`, param);
    for (const k of Object.keys(value)) {
      if (!allowed.includes(k)) throw new ApiError(400, { code: 'parameter_unknown', message: `Received unknown parameter: ${param}[${k}]`, param: `${param}[${k}]` });
    }
    return value;
  }

  /**
   * Checkout `branding_settings` as Stripe validates it: colours `#RRGGBB`, `border_style` pill|rectangular|rounded,
   * `font_family` a lower-case token (Stripe's own list, e.g. chakra_petch), `display_name` at most 100 characters,
   * `logo`/`icon` `{type: url|file, url (https) | file (a file id)}`. Anything else is `parameter_unknown` or
   * `invalid_request_error`. Returns the object Stripe echoes on the session (only what was sent).
   */
  function parseBranding(value) {
    const P = 'branding_settings';
    onlyKeys(value, ['background_color', 'button_color', 'border_style', 'font_family', 'display_name', 'logo', 'icon'], P);
    const out = {};
    for (const k of ['background_color', 'button_color']) {
      if (value[k] === undefined) continue;
      if (typeof value[k] !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(value[k])) throw invalid(`Invalid ${P}[${k}]: must be a hex colour like #RRGGBB.`, `${P}[${k}]`);
      out[k] = value[k];
    }
    if (value.border_style !== undefined) {
      if (!['pill', 'rectangular', 'rounded'].includes(value.border_style)) throw invalid(`Invalid ${P}[border_style]: must be one of pill, rectangular or rounded.`, `${P}[border_style]`);
      out.border_style = value.border_style;
    }
    if (value.font_family !== undefined) {
      if (typeof value.font_family !== 'string' || !/^[a-z][a-z0-9_]*$/.test(value.font_family)) throw invalid(`Invalid ${P}[font_family]: must be a font family token such as chakra_petch.`, `${P}[font_family]`);
      out.font_family = value.font_family;
    }
    if (value.display_name !== undefined) {
      if (typeof value.display_name !== 'string' || value.display_name.trim() === '' || value.display_name.length > 100) throw invalid(`Invalid ${P}[display_name]: must be between 1 and 100 characters.`, `${P}[display_name]`);
      out.display_name = value.display_name;
    }
    for (const k of ['logo', 'icon']) {
      if (value[k] === undefined) continue;
      const param = `${P}[${k}]`;
      onlyKeys(value[k], ['type', 'url', 'file'], param);
      const { type, url, file } = value[k];
      if (type === undefined) throw missingParam(`${param}[type]`);
      if (!['url', 'file'].includes(type)) throw invalid(`Invalid ${param}[type]: must be one of url or file.`, `${param}[type]`);
      if (type === 'url') {
        if (file !== undefined) throw invalid(`${param}[file] cannot be set when ${param}[type] is url.`, `${param}[file]`);
        if (url === undefined) throw missingParam(`${param}[url]`);
        let ok = false;
        try { ok = typeof url === 'string' && new URL(url).protocol === 'https:'; } catch { /* not a URL */ }
        if (!ok) throw invalid(`Invalid ${param}[url]: must be a valid https URL.`, `${param}[url]`);
        out[k] = { type, url };
      } else {
        if (url !== undefined) throw invalid(`${param}[url] cannot be set when ${param}[type] is file.`, `${param}[url]`);
        if (file === undefined) throw missingParam(`${param}[file]`);
        if (typeof file !== 'string' || !/^file_[A-Za-z0-9]+$/.test(file)) throw invalid(`Invalid ${param}[file]: must be a file id.`, `${param}[file]`);
        out[k] = { type, file };
      }
    }
    return out;
  }

  function int(v, param) {
    if (v === undefined || v === null || v === '') return undefined;
    if (!/^-?\d+$/.test(String(v))) throw invalid(`Invalid integer: ${v}`, param);
    return Number(v);
  }

  // ---------------------------------------------------------------- catalog helpers

  function needPrice(id, param) {
    const p = lookup(id);
    if (!p || p.object !== 'price') throw missingResource('price', id, param);
    if (!p.active) throw invalid(`The price specified is inactive. This field only accepts active prices.`, param);
    return p;
  }

  // ---------------------------------------------------------------- coupons and quotes

  function couponProblem(coupon) {
    if (!coupon.valid) return 'That coupon has expired.';
    if (coupon.redeem_by && coupon.redeem_by < nowSec()) return 'That coupon has expired.';
    if (coupon.max_redemptions && coupon.times_redeemed >= coupon.max_redemptions) return 'That coupon has been redeemed the maximum number of times.';
    return null;
  }

  function needCoupon(id, param) {
    const c = lookup(id);
    if (!c || c.object !== 'coupon') throw missingResource('coupon', id, param);
    const problem = couponProblem(c);
    if (problem) throw new ApiError(400, { code: 'coupon_expired', message: problem, param });
    return c;
  }

  function parseDiscounts(list, param = 'discounts') {
    if (list === undefined || list === '' || (Array.isArray(list) && list.length === 0)) return null;
    if (!Array.isArray(list)) throw invalid(`Invalid array for ${param}.`, param);
    if (list.length > 1) throw invalid('Checkout accepts only one discount.', param);
    const d = list[0];
    if (d.promotion_code) throw missingResource('promotion code', d.promotion_code, `${param}[0][promotion_code]`);
    if (!d.coupon) throw missingParam(`${param}[0][coupon]`);
    return needCoupon(d.coupon, `${param}[0][coupon]`);
  }

  /**
   * lines: [{product, amount, discountable}] -> per-line discount and the totals. A percent coupon takes
   * its share of every discountable line it applies to; a fixed one is taken once, from the first.
   */
  function quote(lines, coupon) {
    const restrict = coupon?.applies_to?.products ?? [];
    const applies = (l) => l.discountable !== false && (restrict.length === 0 || restrict.includes(l.product));
    let remaining = coupon?.amount_off && coupon.currency === currency ? coupon.amount_off : 0;
    const out = lines.map((l) => {
      let discount = 0;
      if (coupon && applies(l)) {
        if (coupon.percent_off) discount = Math.round((l.amount * coupon.percent_off) / 100);
        else if (remaining > 0 && l.amount > 0) {
          discount = Math.min(remaining, l.amount);
          remaining -= discount;
        }
      }
      return { ...l, discount };
    });
    const subtotal = out.reduce((a, l) => a + l.amount, 0);
    const discount = out.reduce((a, l) => a + l.discount, 0);
    return { lines: out, subtotal, discount, total: subtotal - discount };
  }

  function priceLines(items) {
    return items.map(({ price, quantity }) => ({ price: price.id, product: price.product, quantity, amount: price.unit_amount * quantity, discountable: true }));
  }

  // ---------------------------------------------------------------- webhooks

  const subscriptionOfEvent = (o) => {
    if (o.object === 'subscription') return o.id;
    if (o.object === 'invoice') return o.parent?.subscription_details?.subscription ?? null;
    if (o.object === 'checkout.session') return o.subscription ?? null;
    if (o.object === 'dispute') {
      const invoice = lookup(lookup(o.charge)?.invoice);
      return invoice?.parent?.subscription_details?.subscription ?? null;
    }
    return null;
  };

  function buildEvent(type, obj, previous) {
    return {
      id: newId('evt'),
      object: 'event',
      api_version: API_VERSION,
      created: nowSec(),
      livemode: false,
      type,
      pending_webhooks: 1,
      request: { id: null, idempotency_key: null },
      data: { object: view(obj), ...(previous ? { previous_attributes: previous } : {}) },
    };
  }

  function takeDropFilter(event) {
    const obj = event.data.object;
    const filter = state.dropFilters.find((f) => f.remaining > 0
      && (!f.event_type || f.event_type === event.type)
      && (!f.subscription_id || f.subscription_id === subscriptionOfEvent(obj))
      && (!f.object_id || f.object_id === obj.id));
    if (!filter) return null;
    filter.remaining -= 1;
    filter.dropped += 1;
    return filter;
  }

  async function deliver(event, { secret, signature, attempts = WEBHOOK_ATTEMPTS, drop = null, redeliveryOf = null } = {}) {
    const raw = JSON.stringify(event);
    const obj = event.data.object;
    const record = {
      id: newId('dlv'), event_id: event.id, event_type: event.type, created: event.created,
      object_id: obj.id ?? null, subscription_id: subscriptionOfEvent(obj), attempts: [], delivered: false, dropped: false,
    };
    if (redeliveryOf) record.redelivery_of = redeliveryOf;
    state.webhooks.push(record);
    state.deliveryEvents.set(record.id, event);
    if (drop) {
      record.dropped = true;
      record.drop_filter = drop.id;
      log(`webhook ${event.type} ${event.id} DROPPED (filter ${drop.id}, ${drop.remaining} left) as ${record.id}`);
      return record;
    }
    for (let attempt = 1; attempt <= attempts; attempt++) {
      // Signed at send time: the receiver's tolerance window is five minutes, retries must still verify.
      const header = signature || signPayload(raw, secret ?? cfg.webhookSecret);
      let status = 0;
      let body = '';
      try {
        const res = await fetch(cfg.webhookUrl, {
          method: 'POST',
          headers: { 'content-type': 'application/json; charset=utf-8', 'Stripe-Signature': header, 'user-agent': 'Stripe/1.0 (+https://stripe.com/docs/webhooks)' },
          body: raw,
          signal: AbortSignal.timeout(10_000),
        });
        status = res.status;
        body = (await res.text()).slice(0, 500);
      } catch (err) {
        body = String(err?.message || err);
      }
      record.attempts.push({ at: new Date(cfg.now()).toISOString(), status, body });
      log(`webhook ${event.type} ${event.id} attempt ${attempt} -> ${status || 'error'} ${body}`);
      if (status >= 200 && status < 300) {
        record.delivered = true;
        record.response = body;
        return record;
      }
      // Stripe retries on every non-2xx.
      if (attempt < attempts) await sleep(cfg.webhookRetryDelayMs * attempt);
    }
    return record;
  }

  /** Queues an event; drop filters are matched now, so a filter armed afterwards never catches it. */
  function enqueue(type, obj, previous) {
    const event = buildEvent(type, obj, previous);
    const drop = takeDropFilter(event);
    const p = deliveryChain.then(() => (drop ? null : sleep(cfg.webhookDelayMs))).then(() => deliver(event, { drop }));
    deliveryChain = p.catch(() => {});
    return p;
  }

  // ---------------------------------------------------------------- payments

  function createPayment({ amount, customer, metadata = {}, invoice = null }) {
    const bt = register({
      id: newId('txn'), object: 'balance_transaction', amount, currency, fee: cfg.feeMinor,
      fee_details: [{ amount: cfg.feeMinor, currency, type: 'stripe_fee', description: 'Stripe processing fees', application: null }],
      net: amount - cfg.feeMinor, type: 'charge', status: 'available',
    });
    const charge = register({
      id: newId('ch'), object: 'charge', amount, amount_captured: amount, amount_refunded: 0, refunded: false, captured: true, paid: true,
      status: 'succeeded', currency, customer, metadata: { ...metadata }, balance_transaction: bt.id, payment_intent: null, invoice,
      disputed: false, dispute: null,
    });
    const pi = register({
      id: newId('pi'), object: 'payment_intent', amount, amount_received: amount, currency, customer, status: 'succeeded',
      latest_charge: charge.id, metadata: { ...metadata }, invoice, capture_method: 'automatic',
    });
    charge.payment_intent = pi.id;
    bt.source = charge.id;
    return { pi, charge, bt };
  }

  // ---------------------------------------------------------------- subscriptions and invoices

  function periodOf(sub) {
    const item = sub.items.data[0];
    return { start: item?.current_period_start ?? sub.start_date, end: item?.current_period_end ?? addMonths(sub.start_date, monthsOfPrice(item?.price)) };
  }

  function activeDiscount(sub) {
    for (const id of sub.discounts) {
      const d = lookup(id);
      if (d && d._remaining !== 0) return d;
    }
    return null;
  }

  function consumeDiscount(sub, discount) {
    if (!discount || discount._remaining === null) return;
    discount._remaining -= 1;
    if (discount._remaining <= 0) sub.discounts = sub.discounts.filter((id) => id !== discount.id);
  }

  function makeItem(sub, price, quantity, period) {
    return {
      id: newId('si'), object: 'subscription_item', created: nowSec(), current_period_start: period.start, current_period_end: period.end,
      price: view(price), quantity, subscription: sub.id, discounts: [], metadata: {}, tax_rates: [],
    };
  }

  function newDiscount(sub, coupon, sessionId = null) {
    const months = coupon.duration === 'repeating' ? coupon.duration_in_months : null;
    const d = register({
      id: newId('di'), object: 'discount', source: { type: 'coupon', coupon: coupon.id }, start: nowSec(),
      end: months ? addMonths(nowSec(), months) : null, subscription: sub.id, customer: sub.customer, checkout_session: sessionId,
      _remaining: coupon.duration === 'once' ? 1 : months ?? null,
    });
    sub.discounts.push(d.id);
    coupon.times_redeemed += 1;
    if (coupon.max_redemptions && coupon.times_redeemed >= coupon.max_redemptions) coupon.valid = false;
    return d;
  }

  /**
   * One invoice for `sub`. lines: [{price, product, quantity, amount, period, proration?, description,
   * discountable, itemId}]. `mode` pay | hold | fail decides what happens to the money. No events here.
   */
  function makeInvoice({ sub, reason, lines, discount, mode }) {
    const coupon = discount ? lookup(discount.source.coupon) : null;
    const q = quote(lines.map((l) => ({ ...l, discountable: l.discountable !== false })), coupon);
    const invId = newId('in');
    const lineViews = q.lines.map((l) => ({
      id: newId('il'), object: 'line_item', amount: l.amount, currency, description: l.description, discountable: l.discountable,
      discount_amounts: l.discount > 0 ? [{ amount: l.discount, discount: discount.id }] : [], period: { start: l.period.start, end: l.period.end },
      quantity: l.quantity,
      pricing: { type: 'price_details', price_details: { price: l.price, product: l.product }, unit_amount_decimal: String(Math.round(l.amount / Math.max(l.quantity, 1))) },
      parent: {
        type: 'subscription_item_details',
        subscription_item_details: { subscription: sub.id, subscription_item: l.itemId ?? null, proration: Boolean(l.proration), proration_details: l.proration ? { credited_items: null } : null },
      },
    }));
    const period = periodOf(sub);
    const total = q.total;
    const due = Math.max(total, 0);
    const inv = register({
      id: invId, object: 'invoice', number: `E2E-${String(seq).padStart(6, '0')}`, customer: sub.customer, currency, billing_reason: reason,
      status: 'open', collection_method: 'charge_automatically', attempt_count: 0, period_start: period.start, period_end: period.end,
      subtotal: q.subtotal, total, amount_due: due, amount_paid: 0, amount_remaining: due,
      total_discount_amounts: q.discount > 0 ? [{ amount: q.discount, discount: discount.id }] : [],
      discounts: q.discount > 0 ? [discount.id] : [], total_taxes: [],
      lines: { object: 'list', data: lineViews, has_more: false, url: `/v1/invoices/${invId}/lines` },
      parent: { type: 'subscription_details', subscription_details: { subscription: sub.id, metadata: { ...sub.metadata } } },
      hosted_invoice_url: `${cfg.publicUrl}/invoice/${invId}`, status_transitions: { finalized_at: nowSec(), paid_at: null, voided_at: null },
      payment_settings: {},
    });
    if (q.discount > 0) consumeDiscount(sub, discount);
    sub.latest_invoice = inv.id;
    if (due === 0) markPaid(inv);
    else if (mode === 'pay') markPaid(inv);
    else if (mode === 'fail') {
      inv.attempt_count = 1;
      sub.status = 'past_due';
    }
    return inv;
  }

  /** Charges an open invoice: PaymentIntent, Charge, InvoicePayment, status paid. */
  function markPaid(inv) {
    inv.status = 'paid';
    inv.amount_paid = inv.amount_due;
    inv.amount_remaining = 0;
    inv.attempt_count = Math.max(inv.attempt_count, 1);
    inv.status_transitions.paid_at = nowSec();
    if (inv.amount_paid > 0) {
      const { pi } = createPayment({ amount: inv.amount_paid, customer: inv.customer, invoice: inv.id });
      register({
        id: newId('inpay'), object: 'invoice_payment', invoice: inv.id, status: 'paid', amount_paid: inv.amount_paid,
        amount_requested: inv.amount_due, currency, is_default: true, payment: { type: 'payment_intent', payment_intent: pi.id },
      });
    }
  }

  function renewalLines(sub, period) {
    const lines = sub.items.data.map((item) => ({
      price: item.price.id, product: item.price.product, quantity: item.quantity, amount: item.price.unit_amount * item.quantity,
      period, itemId: item.id, description: `${item.quantity} x ${item.price.nickname ?? item.price.id} (at ${(item.price.unit_amount / 100).toFixed(2)} / ${item.price.recurring?.interval ?? 'month'})`,
    }));
    return lines;
  }

  function createSubscription({ customer, items, metadata, coupon, session }) {
    const start = nowSec();
    const period = { start, end: addMonths(start, monthsOfPrice(items[0]?.price)) };
    const sub = register({
      id: newId('sub'), object: 'subscription', customer, status: 'active', metadata, cancel_at_period_end: false, cancel_at: null,
      canceled_at: null, ended_at: null, start_date: start, billing_cycle_anchor: start, collection_method: 'charge_automatically', currency,
      discounts: [], items: { object: 'list', data: [], has_more: false, url: '/v1/subscription_items' }, latest_invoice: null,
      billing_mode: { type: 'flexible' }, managed_payments: session?.managed_payments ?? null, default_payment_method: null,
      description: null, trial_end: null, _payments: 'pay', _pending: [],
    });
    sub.items.data = items.map(({ price, quantity }) => makeItem(sub, price, quantity, period));
    const discount = coupon ? newDiscount(sub, coupon, session?.id) : null;
    const inv = makeInvoice({ sub, reason: 'subscription_create', lines: renewalLines(sub, period), discount, mode: 'pay' });
    return { sub, inv };
  }

  function cancelNow(sub, out) {
    const previous = { status: sub.status };
    sub.status = 'canceled';
    sub.canceled_at = nowSec();
    sub.ended_at = sub.canceled_at;
    out.push(enqueue('customer.subscription.deleted', sub, previous));
  }

  function advancePeriod(sub, out) {
    if (sub.status === 'canceled') throw invalid('A canceled subscription cannot renew.');
    if (sub.cancel_at_period_end || (sub.cancel_at && sub.cancel_at <= periodOf(sub).end)) {
      cancelNow(sub, out);
      return null;
    }
    const before = view(sub);
    const oldEnd = periodOf(sub).end;
    const period = { start: oldEnd, end: addMonths(oldEnd, monthsOfPrice(sub.items.data[0]?.price)) };
    for (const item of sub.items.data) {
      item.current_period_start = period.start;
      item.current_period_end = period.end;
    }
    const lines = [...renewalLines(sub, period), ...sub._pending.map((l) => ({ ...l }))];
    sub._pending = [];
    const inv = makeInvoice({ sub, reason: 'subscription_cycle', lines, discount: activeDiscount(sub), mode: sub._payments });
    out.push(enqueue('customer.subscription.updated', sub, diffPrevious(before, view(sub))));
    if (inv.status === 'paid') out.push(enqueue('invoice.paid', inv)); // Stripe marks a $0 invoice paid and sends invoice.paid for it too
    else if (inv.status === 'open' && sub._payments === 'fail') out.push(enqueue('invoice.payment_failed', inv));
    return inv;
  }

  function diffPrevious(before, after) {
    const prev = {};
    for (const key of Object.keys(after)) {
      if (!same(before[key], after[key])) prev[key] = before[key] ?? null;
    }
    return prev;
  }

  function prorationLines(sub, changes, at, discount, reset = null) {
    const period = periodOf(sub);
    const span = Math.max(period.end - period.start, 1);
    const frac = Math.min(Math.max((period.end - at) / span, 0), 1);
    const coupon = discount ? lookup(discount.source.coupon) : null;
    const factorFor = (price) => {
      const restrict = coupon?.applies_to?.products ?? [];
      return coupon?.percent_off && (restrict.length === 0 || restrict.includes(price.product)) ? 1 - coupon.percent_off / 100 : 1;
    };
    const lines = [];
    const proration = { start: at, end: period.end };
    const fresh = reset ? { start: reset.start, end: reset.end } : proration;
    for (const { old, nu } of changes) {
      if (old) {
        lines.push({
          price: old.price.id, product: old.price.product, quantity: old.quantity, period: proration, proration: true, discountable: false, itemId: old.id,
          amount: -Math.round(old.price.unit_amount * old.quantity * frac * factorFor(old.price)),
          description: `Unused time on ${old.quantity} x ${old.price.nickname ?? old.price.id} after ${new Date(at * 1000).toISOString().slice(0, 10)}`,
        });
      }
      if (nu) {
        lines.push({
          price: nu.price.id, product: nu.price.product, quantity: nu.quantity, period: fresh, proration: true, discountable: false, itemId: nu.id,
          amount: Math.round(nu.price.unit_amount * nu.quantity * (reset ? 1 : frac) * factorFor(nu.price)),
          description: `Remaining time on ${nu.quantity} x ${nu.price.nickname ?? nu.price.id} after ${new Date(at * 1000).toISOString().slice(0, 10)}`,
        });
      }
    }
    return lines;
  }

  function applyItems(sub, raw) {
    if (!Array.isArray(raw)) throw invalid('Invalid array for items.', 'items');
    const changes = [];
    const period = periodOf(sub);
    let data = sub.items.data.map((i) => ({ ...i }));
    raw.forEach((r, i) => {
      const where = `items[${i}]`;
      if (r.id) {
        const idx = data.findIndex((x) => x.id === r.id);
        if (idx < 0) throw missingResource('subscription_item', r.id, `${where}[id]`);
        const old = data[idx];
        if (isTrue(r.deleted)) {
          if (data.length === 1) throw invalid('A subscription must keep at least one item.', `${where}[deleted]`);
          data.splice(idx, 1);
          changes.push({ old, nu: null });
          return;
        }
        const price = r.price ? needPrice(r.price, `${where}[price]`) : null;
        const priceChanged = price !== null && price.id !== old.price.id;
        const quantity = r.quantity !== undefined ? int(r.quantity, `${where}[quantity]`) : priceChanged ? 1 : old.quantity;
        if (quantity < 0) throw invalid('Quantity must be zero or more.', `${where}[quantity]`);
        if (price && price.type !== 'recurring') throw invalid('A subscription item needs a recurring price.', `${where}[price]`);
        if (!priceChanged && quantity === old.quantity) return;
        const nu = { ...old, price: price ? view(price) : old.price, quantity };
        data[idx] = nu;
        changes.push({ old, nu });
        return;
      }
      if (!r.price) throw missingParam(`${where}[price]`);
      const price = needPrice(r.price, `${where}[price]`);
      if (price.type !== 'recurring') throw invalid('A subscription item needs a recurring price.', `${where}[price]`);
      if (data.some((x) => x.price.id === price.id)) throw invalid(`Duplicate price: ${price.id}. Update the existing item instead.`, `${where}[price]`);
      const nu = makeItem(sub, price, int(r.quantity, `${where}[quantity]`) ?? 1, period);
      data.push(nu);
      changes.push({ old: null, nu });
    });
    sub.items.data = data;
    return changes;
  }

  // ---------------------------------------------------------------- refunds

  function createRefund({ charge, amount, reason, metadata }) {
    const remaining = charge.amount - charge.amount_refunded;
    if (remaining <= 0) throw new ApiError(400, { code: 'charge_already_refunded', message: `Charge ${charge.id} has already been refunded.` });
    const value = amount ?? remaining;
    if (!(value > 0)) throw invalid('Invalid positive integer', 'amount');
    if (value > remaining) {
      throw invalid(`Refund amount (${value}) is greater than unrefunded amount on charge (${remaining}).`, 'amount');
    }
    const bt = register({
      id: newId('txn'), object: 'balance_transaction', amount: -value, currency, fee: 0, fee_details: [], net: -value, type: 'refund', status: 'available', source: null,
    });
    const refund = register({
      id: newId('re'), object: 'refund', amount: value, currency, charge: charge.id, payment_intent: charge.payment_intent, status: 'succeeded',
      reason: reason ?? null, metadata: metaOf(metadata), balance_transaction: bt.id, failure_reason: null,
    });
    bt.source = refund.id;
    charge.amount_refunded += value;
    charge.refunded = charge.amount_refunded >= charge.amount;
    return refund;
  }

  function emitRefund(refund, out) {
    out.push(enqueue('refund.created', refund));
    out.push(enqueue('charge.refunded', lookup(refund.charge)));
  }

  // ---------------------------------------------------------------- disputes

  const DISPUTE_REASONS = [
    'bank_cannot_process', 'check_returned', 'credit_not_processed', 'customer_initiated', 'debit_not_authorized', 'duplicate', 'fraudulent', 'general',
    'incorrect_account_details', 'insufficient_funds', 'product_not_received', 'product_unacceptable', 'subscription_canceled', 'unrecognized',
  ];
  const DISPUTE_OPEN = ['needs_response', 'under_review', 'warning_needs_response', 'warning_under_review'];
  const DISPUTE_CLOSED = ['won', 'lost', 'warning_closed', 'charge_refunded'];

  /** A dispute on the payment's charge, in an open status (`charge.dispute.created` is the caller's to send). */
  function createDispute({ paymentIntent, amount, reason, status }) {
    const pi = lookup(paymentIntent);
    if (pi?.object !== 'payment_intent') throw missingResource('payment_intent', paymentIntent, 'payment_intent');
    const charge = lookup(pi.latest_charge);
    if (charge?.disputed) throw invalid(`Charge ${charge.id} already has a dispute (${charge.dispute}).`, 'payment_intent');
    const value = amount ?? charge.amount;
    if (!(value > 0) || value > charge.amount) throw invalid(`The dispute amount (${value}) must be between 1 and the charge amount (${charge.amount}).`, 'amount');
    if (!DISPUTE_REASONS.includes(reason)) throw invalid(`Invalid reason: must be one of ${DISPUTE_REASONS.join(', ')}.`, 'reason');
    if (!DISPUTE_OPEN.includes(status)) throw invalid(`A dispute is created in one of ${DISPUTE_OPEN.join(', ')}.`, 'status');
    const dispute = register({
      id: newId('dp'), object: 'dispute', amount: value, currency, charge: charge.id, payment_intent: pi.id, status, reason,
      balance_transactions: [], evidence: {}, evidence_details: { due_by: nowSec() + 7 * 24 * 3600, has_evidence: false, past_due: false, submission_count: 0 },
      is_charge_refundable: false, metadata: {}, network_reason_code: null,
    });
    charge.disputed = true;
    charge.dispute = dispute.id;
    return dispute;
  }

  /** Closes an open dispute. A lost one keeps the money with the cardholder: the charge's amount_refunded stays 0. */
  function closeDispute(dispute, status) {
    if (!DISPUTE_CLOSED.includes(status)) throw invalid(`A dispute is closed as one of ${DISPUTE_CLOSED.join(', ')}.`, 'status');
    if (DISPUTE_CLOSED.includes(dispute.status)) throw invalid(`Dispute ${dispute.id} is already closed (${dispute.status}).`, 'status');
    dispute.status = status;
    dispute.evidence_details = { ...dispute.evidence_details, past_due: false };
    return dispute;
  }

  // ---------------------------------------------------------------- checkout

  function completeSession(s, out) {
    if (s.status === 'complete') return s;
    if (s.status === 'expired' || s.expires_at < nowSec()) {
      s.status = 'expired';
      throw invalid('This Checkout Session has expired.');
    }
    const coupon = s._couponId ? lookup(s._couponId) : null;
    if (coupon) {
      const problem = couponProblem(coupon);
      if (problem) throw new ApiError(400, { code: 'coupon_expired', message: problem });
    }
    let customer = s.customer;
    if (!customer) {
      customer = register({ id: newId('cus'), object: 'customer', email: s.customer_email ?? null, name: null, description: null, address: null, metadata: {}, balance: 0, delinquent: false }).id;
      s.customer = customer;
    }
    // customer_update[address|name]=auto saves what Checkout collected back to the customer (the fake
    // "collects" a fixed buyer), which is what lets Stripe Tax price a later session for the same customer.
    const buyer = lookup(customer);
    if (s._customerUpdate.address === 'auto') buyer.address = { country: 'IT', line1: 'Via Mock 1', city: 'Roma', postal_code: '00100', state: null, line2: null };
    if (s._customerUpdate.name === 'auto') buyer.name = 'Mock Buyer';
    if (s.mode === 'subscription') {
      const items = s._lines.map((l) => ({ price: lookup(l.price), quantity: l.quantity }));
      const { sub, inv } = createSubscription({ customer, items, metadata: s._subscriptionMetadata, coupon, session: s });
      s.subscription = sub.id;
      s.invoice = inv.id;
      s.payment_status = inv.amount_due === 0 ? 'no_payment_required' : 'paid';
      s.status = 'complete';
      out.push(enqueue('customer.subscription.created', sub));
      if (inv.status === 'paid') out.push(enqueue('invoice.paid', inv)); // Stripe marks a $0 invoice paid and sends invoice.paid for it too
      out.push(enqueue('checkout.session.completed', s));
      return s;
    }
    // payment mode (top-up)
    if (coupon) {
      coupon.times_redeemed += 1;
      if (coupon.max_redemptions && coupon.times_redeemed >= coupon.max_redemptions) coupon.valid = false;
    }
    if (s.amount_total > 0) {
      const { pi } = createPayment({ amount: s.amount_total, customer, metadata: s._paymentIntentMetadata });
      s.payment_intent = pi.id;
      s.payment_status = 'paid';
    } else {
      s.payment_status = 'no_payment_required';
    }
    s.status = 'complete';
    out.push(enqueue('checkout.session.completed', s));
    return s;
  }

  function sessionView(s, expand) {
    const v = view(s);
    if (expand?.includes('line_items')) {
      v.line_items = {
        object: 'list', has_more: false, url: `/v1/checkout/sessions/${s.id}/line_items`,
        data: s._lines.map((l) => ({ id: newId('li'), object: 'item', amount_subtotal: l.amount, amount_total: l.amount - l.discount, currency, quantity: l.quantity, price: view(lookup(l.price)) })),
      };
    }
    return v;
  }

  // ---------------------------------------------------------------- routes

  const routes = [];
  function route(method, pattern, spec, handler) {
    const names = [];
    const re = new RegExp(`^${pattern.replace(/:([a-z_]+)/g, (_, n) => (names.push(n), '([^/]+)'))}$`);
    routes.push({ method, re, names, allow: spec.allow ?? null, handler });
  }

  const MODES = ['pay', 'hold', 'fail'];

  /** GET /v1/<things>/:id for a plain stored object. */
  const byId = (type, label) => ({ params }) => {
    const o = lookup(params.id);
    if (o?.object !== type) throw notFound(label, params.id);
    return o;
  };

  // customers
  route('POST', '/v1/customers', { allow: ['email', 'name', 'metadata', 'description'] }, ({ input }) =>
    register({ id: newId('cus'), object: 'customer', email: input.email ?? null, name: input.name ?? null, description: input.description ?? null, address: null, metadata: metaOf(input.metadata), balance: 0, delinquent: false }));
  route('GET', '/v1/customers/:id', {}, byId('customer', 'customer'));

  // products, prices
  route('GET', '/v1/prices/:id', {}, byId('price', 'price'));
  route('GET', '/v1/products/:id', {}, byId('product', 'product'));
  route('GET', '/v1/prices', { allow: ['lookup_keys', 'active', 'limit', 'starting_after', 'product', 'type'] }, ({ input }) => {
    let list = all('price');
    if (input.lookup_keys) list = list.filter((p) => input.lookup_keys.includes(p.lookup_key));
    if (input.active !== undefined) list = list.filter((p) => String(p.active) === input.active);
    if (input.product) list = list.filter((p) => p.product === input.product);
    return listOf('/v1/prices', list, input);
  });

  // coupons
  function createCoupon(input) {
    const id = input.id ?? newId('cpn', 8);
    if (lookup(id)) throw new ApiError(400, { code: 'resource_already_exists', message: `Coupon ${id} already exists.`, param: 'id' });
    const percent = input.percent_off !== undefined ? Number(input.percent_off) : null;
    const amountOff = int(input.amount_off, 'amount_off') ?? null;
    if ((percent === null) === (amountOff === null)) throw invalid('Specify exactly one of percent_off or amount_off.', 'percent_off');
    if (percent !== null && !(percent > 0 && percent <= 100)) throw invalid('Invalid percent_off: must be greater than 0 and at most 100.', 'percent_off');
    if (amountOff !== null && (!(amountOff > 0) || !input.currency)) throw invalid('amount_off needs a positive amount and a currency.', 'amount_off');
    const duration = input.duration ?? 'once';
    if (!['once', 'repeating', 'forever'].includes(duration)) throw invalid('Invalid duration: must be one of forever, once, or repeating.', 'duration');
    const months = int(input.duration_in_months, 'duration_in_months') ?? null;
    if (duration === 'repeating' && !(months > 0)) throw missingParam('duration_in_months');
    if (duration !== 'repeating' && months !== null) throw invalid('duration_in_months is only valid with duration=repeating.', 'duration_in_months');
    const products = input.applies_to?.products ?? [];
    for (const p of products) if (lookup(p)?.object !== 'product') throw missingResource('product', p, 'applies_to[products]');
    return register({
      id, object: 'coupon', name: input.name ?? null, valid: true, percent_off: percent, amount_off: amountOff, currency: amountOff !== null ? String(input.currency).toLowerCase() : null,
      duration, duration_in_months: months, max_redemptions: int(input.max_redemptions, 'max_redemptions') ?? null, times_redeemed: 0,
      redeem_by: int(input.redeem_by, 'redeem_by') ?? null, applies_to: products.length > 0 ? { products } : undefined, metadata: metaOf(input.metadata),
    });
  }
  route('POST', '/v1/coupons', { allow: ['id', 'name', 'percent_off', 'amount_off', 'currency', 'duration', 'duration_in_months', 'max_redemptions', 'redeem_by', 'applies_to', 'metadata'] }, ({ input }) => createCoupon(input));
  route('GET', '/v1/coupons/:id', {}, byId('coupon', 'coupon'));
  route('DELETE', '/v1/coupons/:id', {}, ({ params }) => {
    const c = lookup(params.id);
    if (c?.object !== 'coupon') throw notFound('coupon', params.id);
    state.store.delete(c.id);
    return { id: c.id, object: 'coupon', deleted: true };
  });

  // checkout sessions
  const SESSION_PARAMS = [
    'mode', 'ui_mode', 'integration_identifier', 'customer', 'customer_email', 'customer_creation', 'client_reference_id', 'line_items', 'metadata',
    'payment_intent_data', 'subscription_data', 'success_url', 'cancel_url', 'discounts', 'managed_payments', 'automatic_tax', 'allow_promotion_codes',
    'expires_at', 'locale', 'billing_address_collection', 'tax_id_collection', 'customer_update', 'payment_method_collection', 'invoice_creation',
    'branding_settings',
  ];
  route('POST', '/v1/checkout/sessions', { allow: SESSION_PARAMS }, ({ input }) => {
    const mode = input.mode;
    if (!mode) throw missingParam('mode');
    if (!['subscription', 'payment'].includes(mode)) throw invalid('Invalid mode: the mock supports payment and subscription.', 'mode');
    const uiMode = input.ui_mode ?? 'hosted_page';
    if (uiMode !== 'hosted_page') throw invalid('The mock supports only ui_mode=hosted_page.', 'ui_mode');
    if (!input.success_url) throw missingParam('success_url');
    if (!Array.isArray(input.line_items) || input.line_items.length === 0) throw missingParam('line_items');
    const items = input.line_items.map((li, i) => {
      const price = needPrice(li.price, `line_items[${i}][price]`);
      if (mode === 'subscription' && price.type !== 'recurring') throw invalid('You must provide a recurring price in subscription mode when using prices.', `line_items[${i}][price]`);
      if (mode === 'payment' && price.type === 'recurring') throw invalid('You specified payment mode but passed a recurring price. Either switch to subscription mode or use only one-time prices.', `line_items[${i}][price]`);
      const quantity = int(li.quantity, `line_items[${i}][quantity]`) ?? 1;
      if (!(quantity >= 1)) throw invalid('Quantity must be at least 1.', `line_items[${i}][quantity]`);
      return { price, quantity };
    });
    if (mode === 'subscription' && input.payment_intent_data) throw invalid('You can not pass `payment_intent_data` in `subscription` mode.', 'payment_intent_data');
    if (mode === 'payment' && input.subscription_data) throw invalid('You can not pass `subscription_data` in `payment` mode.', 'subscription_data');
    const mp = isTrue(input.managed_payments?.enabled);
    if (input.managed_payments !== undefined) onlyKeys(input.managed_payments, ['enabled'], 'managed_payments');
    if (mp) {
      // Managed Payments makes Stripe the merchant of record: tax, tax ids and the customer's saved address
      // are Stripe's business, so a request that sets any of them is refused outright.
      for (const k of ['automatic_tax', 'tax_id_collection', 'customer_update', 'invoice_creation']) {
        if (input[k] !== undefined) throw new ApiError(400, { code: 'parameter_unknown', message: `Received unknown parameter: ${k}. It cannot be set when managed_payments[enabled]=true.`, param: k });
      }
    }
    if (input.automatic_tax !== undefined) onlyKeys(input.automatic_tax, ['enabled'], 'automatic_tax');
    const customerUpdate = input.customer_update === undefined ? {} : onlyKeys(input.customer_update, ['address', 'name', 'shipping'], 'customer_update');
    for (const [k, v] of Object.entries(customerUpdate)) {
      if (!['auto', 'never'].includes(v)) throw invalid(`Invalid customer_update[${k}]: must be one of auto or never.`, `customer_update[${k}]`);
    }
    if (input.customer && lookup(input.customer)?.object !== 'customer') throw missingResource('customer', input.customer, 'customer');
    if (input.customer && input.customer_email) throw invalid('You may only specify one of these parameters: customer, customer_email.', 'customer_email');
    if (Object.keys(customerUpdate).length > 0 && !input.customer) {
      throw invalid('`customer_update` can only be used with an existing `customer`.', 'customer_update');
    }
    if (isTrue(input.automatic_tax?.enabled) && input.customer) {
      // Stripe Tax needs the customer's location. An existing customer with no address must have Checkout
      // save the one it collects back (customer_update[address]=auto, or shipping), or the session is refused.
      const known = lookup(input.customer).address;
      if (!known && customerUpdate.address !== 'auto' && customerUpdate.shipping !== 'auto') {
        throw invalid('Automatic tax calculation in Checkout requires a valid address on the Customer. Add a valid address to the Customer or set either `customer_update[address]` to \'auto\' or `customer_update[shipping]` to \'auto\' to save the address entered in Checkout to the Customer.', 'automatic_tax');
      }
    }
    const branding = input.branding_settings === undefined ? null : parseBranding(input.branding_settings);
    const coupon = parseDiscounts(input.discounts);
    const q = quote(priceLines(items), coupon);
    const id = newId('cs_test');
    const expiresAt = int(input.expires_at, 'expires_at') ?? nowSec() + 24 * 3600;
    const s = register({
      id, object: 'checkout.session', mode, status: 'open', payment_status: 'unpaid', ui_mode: 'hosted_page', url: `${cfg.publicUrl}/pay/${id}`,
      success_url: input.success_url, cancel_url: input.cancel_url ?? null, client_reference_id: input.client_reference_id ?? null,
      customer: input.customer ?? null, customer_email: input.customer_email ?? null, metadata: metaOf(input.metadata), subscription: null, payment_intent: null, invoice: null,
      amount_subtotal: q.subtotal, amount_total: q.total, currency, total_details: { amount_discount: q.discount, amount_tax: 0, amount_shipping: 0 },
      discounts: coupon ? [{ coupon: coupon.id, promotion_code: null }] : [], expires_at: expiresAt, managed_payments: mp ? { enabled: true } : null,
      automatic_tax: mp ? undefined : { enabled: isTrue(input.automatic_tax?.enabled), status: null }, integration_identifier: input.integration_identifier ?? null,
      branding_settings: branding,
      customer_update: mp || input.customer_update === undefined ? undefined : { ...customerUpdate },
      _customerUpdate: { ...customerUpdate },
      _lines: q.lines.map((l, i) => ({ price: items[i].price.id, quantity: items[i].quantity, amount: l.amount, discount: l.discount })),
      _subscriptionMetadata: metaOf(input.subscription_data?.metadata, 'subscription_data[metadata]'),
      _paymentIntentMetadata: metaOf(input.payment_intent_data?.metadata, 'payment_intent_data[metadata]'),
      _couponId: coupon?.id ?? null,
    });
    return s;
  });
  route('GET', '/v1/checkout/sessions/:id', { allow: [] }, ({ params, input }) => {
    const s = lookup(params.id);
    if (s?.object !== 'checkout.session') throw notFound('checkout.session', params.id);
    return sessionView(s, input.expand);
  });
  route('GET', '/v1/checkout/sessions', { allow: ['payment_intent', 'subscription', 'customer', 'limit', 'starting_after'] }, ({ input }) => {
    let list = all('checkout.session');
    if (input.payment_intent) list = list.filter((s) => s.payment_intent === input.payment_intent);
    if (input.subscription) list = list.filter((s) => s.subscription === input.subscription);
    if (input.customer) list = list.filter((s) => s.customer === input.customer);
    return listOf('/v1/checkout/sessions', list, input);
  });
  route('POST', '/v1/checkout/sessions/:id/expire', { allow: [] }, ({ params }, out) => {
    const s = lookup(params.id);
    if (s?.object !== 'checkout.session') throw notFound('checkout.session', params.id);
    if (s.status !== 'open') throw invalid('Only Checkout Sessions with a status in open can be expired.');
    s.status = 'expired';
    out.push(enqueue('checkout.session.expired', s));
    return s;
  });

  // subscriptions
  const PAYMENT_BEHAVIORS = ['allow_incomplete', 'error_if_incomplete'];
  const SUB_UPDATE_PARAMS = ['items', 'cancel_at_period_end', 'cancel_at', 'proration_behavior', 'proration_date', 'discounts', 'metadata', 'payment_behavior'];
  const needSub = (id) => {
    const s = lookup(id);
    if (s?.object !== 'subscription') throw notFound('subscription', id);
    return s;
  };
  route('GET', '/v1/subscriptions/:id', {}, ({ params }) => needSub(params.id));
  route('GET', '/v1/subscriptions', { allow: ['customer', 'status', 'price', 'limit', 'starting_after'] }, ({ input }) => {
    let list = all('subscription');
    if (input.customer) list = list.filter((s) => s.customer === input.customer);
    const status = input.status ?? 'active_or_past';
    if (status === 'all') {
      // everything
    } else if (status === 'active_or_past') {
      list = list.filter((s) => s.status !== 'canceled' && s.status !== 'incomplete_expired');
    } else {
      list = list.filter((s) => s.status === status);
    }
    if (input.price) list = list.filter((s) => s.items.data.some((i) => i.price.id === input.price));
    return listOf('/v1/subscriptions', list, input);
  });
  route('POST', '/v1/subscriptions/:id', { allow: SUB_UPDATE_PARAMS }, ({ params, input }, out) => {
    const sub = needSub(params.id);
    if (sub.status === 'canceled') throw invalid('A canceled subscription can only update its cancellation_details and metadata.');
    const behavior = input.proration_behavior ?? 'create_prorations';
    if (!['always_invoice', 'create_prorations', 'none'].includes(behavior)) throw invalid('Invalid proration_behavior: must be one of always_invoice, create_prorations, or none.', 'proration_behavior');
    const paymentBehavior = input.payment_behavior ?? 'allow_incomplete';
    if (!PAYMENT_BEHAVIORS.includes(paymentBehavior)) {
      throw invalid(`The mock implements payment_behavior=${PAYMENT_BEHAVIORS.join(' and ')} only (got ${paymentBehavior}).`, 'payment_behavior');
    }
    // error_if_incomplete: the update is applied only if the proration invoice is paid; otherwise nothing at all
    // changes and the call answers 402. Everything the call touches is restored from this copy.
    const strict = paymentBehavior === 'error_if_incomplete';
    const undo = strict ? structuredClone(state.store) : null;
    const before = view(sub);
    const at = int(input.proration_date, 'proration_date') ?? nowSec();

    const discountAtChange = activeDiscount(sub);
    let changes = [];
    const monthsBefore = monthsOfPrice(sub.items.data[0]?.price);
    if (input.items !== undefined) changes = applyItems(sub, input.items);
    // An interval change (monthly to yearly) restarts the billing cycle now, as Stripe does: the unused
    // old time is credited pro rata, the new price is charged in full for a fresh period.
    const monthsAfter = monthsOfPrice(sub.items.data[0]?.price);
    let reset = null;
    if (changes.length > 0 && monthsAfter !== monthsBefore) {
      reset = { start: at, end: addMonths(at, monthsAfter) };
    }
    const restartCycle = () => {
      if (!reset) return;
      sub.billing_cycle_anchor = reset.start;
      for (const item of sub.items.data) {
        item.current_period_start = reset.start;
        item.current_period_end = reset.end;
      }
    };
    if (input.cancel_at_period_end !== undefined) {
      const on = isTrue(input.cancel_at_period_end);
      sub.cancel_at_period_end = on;
      sub.cancel_at = on ? periodOf(sub).end : null;
      sub.canceled_at = on ? at : null;
    }
    if (input.cancel_at !== undefined) sub.cancel_at = input.cancel_at === '' ? null : int(input.cancel_at, 'cancel_at');
    if (input.discounts !== undefined) {
      if (input.discounts === '') sub.discounts = [];
      else {
        const coupon = parseDiscounts(input.discounts);
        sub.discounts = [];
        if (coupon) newDiscount(sub, coupon);
      }
    }
    if (input.metadata !== undefined) sub.metadata = { ...sub.metadata, ...metaOf(input.metadata) };

    let invoice = null;
    if (changes.length > 0 && behavior !== 'none') {
      // Prorations use the discount as updated by this same call (Stripe: "the proration is computed
      // with the updated discount"), so a `discounts=` that clears it also takes it off the proration.
      const lines = prorationLines(sub, changes, at, input.discounts === '' ? null : discountAtChange, reset);
      restartCycle();
      if (behavior === 'always_invoice') invoice = makeInvoice({ sub, reason: 'subscription_update', lines, discount: null, mode: sub._payments });
      else sub._pending.push(...lines);
    }
    if (changes.length > 0 && behavior === 'none') restartCycle();
    if (strict && invoice && invoice.amount_due > 0 && invoice.status !== 'paid') {
      const declined = sub._payments === 'fail';
      state.store = undo;
      throw declined
        ? new ApiError(402, { type: 'card_error', code: 'card_declined', declineCode: 'generic_decline', message: 'Your card was declined.' })
        : new ApiError(402, { type: 'card_error', code: 'invoice_payment_intent_requires_action', message: 'This payment requires additional user action before it can be completed successfully. Payment can be completed using the PaymentIntent associated with the invoice.' });
    }

    const after = view(sub);
    if (!same(before, after)) {
      out.push(enqueue('customer.subscription.updated', sub, diffPrevious(before, after)));
    }
    if (invoice) {
      if (invoice.status === 'paid') out.push(enqueue('invoice.paid', invoice)); // $0 invoices included, as Stripe does
      else if (invoice.status === 'open' && sub._payments === 'fail') out.push(enqueue('invoice.payment_failed', invoice));
    }
    return sub;
  });
  route('DELETE', '/v1/subscriptions/:id', { allow: [] }, ({ params }, out) => {
    const sub = needSub(params.id);
    if (sub.status === 'canceled') throw invalid('This subscription is already canceled.');
    cancelNow(sub, out);
    return sub;
  });

  // invoices
  const needInvoice = (id) => {
    const i = lookup(id);
    if (i?.object !== 'invoice') throw notFound('invoice', id);
    return i;
  };
  route('GET', '/v1/invoices/:id', {}, ({ params }) => needInvoice(params.id));
  route('GET', '/v1/invoices', { allow: ['subscription', 'customer', 'status', 'created', 'limit', 'starting_after'] }, ({ input }) => {
    let list = all('invoice');
    if (input.subscription) list = list.filter((i) => i.parent?.subscription_details?.subscription === input.subscription);
    if (input.customer) list = list.filter((i) => i.customer === input.customer);
    if (input.status) list = list.filter((i) => i.status === input.status);
    if (input.created) {
      const c = input.created;
      if (c.gte !== undefined) list = list.filter((i) => i.created >= int(c.gte, 'created[gte]'));
      if (c.gt !== undefined) list = list.filter((i) => i.created > int(c.gt, 'created[gt]'));
      if (c.lte !== undefined) list = list.filter((i) => i.created <= int(c.lte, 'created[lte]'));
      if (c.lt !== undefined) list = list.filter((i) => i.created < int(c.lt, 'created[lt]'));
    }
    return listOf('/v1/invoices', list, input);
  });
  route('POST', '/v1/invoices/create_preview', { allow: ['currency', 'customer', 'subscription', 'subscription_details', 'discounts', 'customer_details'] }, ({ input }) => {
    const raw = input.subscription_details?.items;
    if (!Array.isArray(raw) || raw.length === 0) throw missingParam('subscription_details[items]');
    const items = raw.map((r, i) => {
      const price = needPrice(r.price, `subscription_details[items][${i}][price]`);
      return { price, quantity: int(r.quantity, `subscription_details[items][${i}][quantity]`) ?? 1 };
    });
    const coupon = parseDiscounts(input.discounts);
    const q = quote(priceLines(items), coupon);
    const cur = (input.currency ?? currency).toLowerCase();
    return {
      id: null, object: 'invoice', livemode: false, currency: cur, billing_reason: 'upcoming', status: 'draft',
      subtotal: q.subtotal, total: q.total, total_excluding_tax: q.total, amount_due: q.total,
      total_discount_amounts: q.discount > 0 ? [{ amount: q.discount, discount: null }] : [], total_taxes: [],
      lines: {
        object: 'list', has_more: false, url: '/v1/invoices/preview/lines',
        data: q.lines.map((l, i) => ({
          id: newId('il'), object: 'line_item', amount: l.amount, currency: cur, quantity: items[i].quantity, discountable: true,
          discount_amounts: l.discount > 0 ? [{ amount: l.discount, discount: null }] : [], pricing: { type: 'price_details', price_details: { price: l.price, product: l.product } },
        })),
      },
    };
  });
  route('GET', '/v1/invoice_payments', { allow: ['invoice', 'payment', 'status', 'limit', 'starting_after'] }, ({ input }) => {
    let list = all('invoice_payment');
    if (input.invoice) list = list.filter((p) => p.invoice === input.invoice);
    if (input.status) list = list.filter((p) => p.status === input.status);
    if (input.payment?.type) list = list.filter((p) => p.payment.type === input.payment.type);
    if (input.payment?.payment_intent) list = list.filter((p) => p.payment.payment_intent === input.payment.payment_intent);
    return listOf('/v1/invoice_payments', list, input);
  });

  // payments, charges, balance transactions
  for (const [path, type, label] of [['payment_intents', 'payment_intent', 'payment_intent'], ['charges', 'charge', 'charge'], ['balance_transactions', 'balance_transaction', 'balance_transaction'], ['refunds', 'refund', 'refund'], ['disputes', 'dispute', 'dispute']]) {
    route('GET', `/v1/${path}/:id`, {}, byId(type, label));
  }
  route('POST', '/v1/refunds', { allow: ['payment_intent', 'charge', 'amount', 'reason', 'metadata'] }, ({ input }, out) => {
    let charge;
    if (input.charge) {
      charge = lookup(input.charge);
      if (charge?.object !== 'charge') throw missingResource('charge', input.charge, 'charge');
    } else if (input.payment_intent) {
      const pi = lookup(input.payment_intent);
      if (pi?.object !== 'payment_intent') throw missingResource('payment_intent', input.payment_intent, 'payment_intent');
      charge = lookup(pi.latest_charge);
    } else {
      throw invalid('Either payment_intent or charge must be provided.', 'payment_intent');
    }
    if (input.reason && !['duplicate', 'fraudulent', 'requested_by_customer'].includes(input.reason)) throw invalid('Invalid reason: must be one of duplicate, fraudulent, or requested_by_customer', 'reason');
    const refund = createRefund({ charge, amount: int(input.amount, 'amount'), reason: input.reason, metadata: input.metadata });
    emitRefund(refund, out);
    return refund;
  });

  // billing portal
  route('POST', '/v1/billing_portal/sessions', { allow: ['customer', 'configuration', 'return_url', 'flow_data', 'locale'] }, ({ input }) => {
    if (!input.customer) throw missingParam('customer');
    if (lookup(input.customer)?.object !== 'customer') throw missingResource('customer', input.customer, 'customer');
    const flow = input.flow_data ?? null;
    if (flow) {
      if (!['subscription_cancel', 'payment_method_update', 'subscription_update', 'subscription_update_confirm', 'customer_update'].includes(flow.type)) throw invalid(`Invalid flow_data[type]: ${flow.type}`, 'flow_data[type]');
      if (flow.type === 'subscription_cancel') {
        const subId = flow.subscription_cancel?.subscription;
        if (!subId) throw missingParam('flow_data[subscription_cancel][subscription]');
        const sub = needSub(subId);
        if (sub.customer !== input.customer) throw invalid('That subscription belongs to another customer.', 'flow_data[subscription_cancel][subscription]');
      }
    }
    const id = newId('bps');
    return register({
      id, object: 'billing_portal.session', customer: input.customer, configuration: input.configuration ?? 'bpc_default', return_url: input.return_url ?? null,
      locale: input.locale ?? null, flow: flow ? { type: flow.type, subscription_cancel: flow.subscription_cancel ? { subscription: flow.subscription_cancel.subscription } : undefined } : null,
      url: `${cfg.publicUrl}/portal/${id}`,
    });
  });

  // ---------------------------------------------------------------- listing and expansion

  function listOf(url, items, input) {
    const limit = Math.min(Math.max(int(input.limit, 'limit') ?? 10, 1), 100);
    const ordered = [...items].sort((a, b) => b.created - a.created || b._seq - a._seq);
    let start = 0;
    if (input.starting_after) {
      const idx = ordered.findIndex((o) => o.id === input.starting_after);
      if (idx < 0) throw invalid(`No such object: '${input.starting_after}'`, 'starting_after');
      start = idx + 1;
    }
    const page = ordered.slice(start, start + limit);
    return { object: 'list', url, has_more: start + limit < ordered.length, data: page };
  }

  function expandPath(node, segs) {
    if (segs.length === 0 || node === null || node === undefined) return;
    if (Array.isArray(node)) {
      node.forEach((_, i) => {
        if (typeof node[i] === 'string') {
          const found = lookup(node[i]);
          if (found) node[i] = view(found);
        }
        expandPath(node[i], segs.slice(1));
      });
      return;
    }
    if (typeof node !== 'object') return;
    const [head, ...rest] = segs;
    let v = node[head];
    if (typeof v === 'string') {
      const found = lookup(v);
      if (found) v = node[head] = view(found);
    }
    if (Array.isArray(v)) {
      v.forEach((el, i) => {
        if (typeof el === 'string' && lookup(el)) v[i] = view(lookup(el));
        expandPath(v[i], rest);
      });
    } else if (v && typeof v === 'object') {
      expandPath(v, rest);
    }
  }

  function respond(result, input) {
    const out = view(result);
    const expand = Array.isArray(input.expand) ? input.expand : [];
    for (const path of expand) expandPath(out, String(path).split('.'));
    return out;
  }

  // ---------------------------------------------------------------- the HTTP surface

  const errorBody = (e) => ({ error: { ...(e.type ? { type: e.type } : {}), ...(e.code ? { code: e.code } : {}), message: e.message, ...(e.param ? { param: e.param } : {}), ...(e.declineCode ? { decline_code: e.declineCode } : {}), doc_url: e.code ? `https://stripe.com/docs/error-codes/${e.code.replaceAll('_', '-')}` : undefined } });

  function send(res, status, body, headers = {}) {
    const isText = typeof body === 'string';
    res.writeHead(status, {
      'content-type': isText ? 'text/html; charset=utf-8' : 'application/json',
      'request-id': newId('req', 14),
      'stripe-version': API_VERSION,
      ...headers,
    });
    res.end(isText ? body : JSON.stringify(body));
  }

  function readBody(req) {
    return new Promise((resolveBody, reject) => {
      const chunks = [];
      let size = 0;
      req.on('data', (c) => {
        size += c.length;
        if (size > 1_000_000) {
          reject(new Error('body too large'));
          req.destroy();
          return;
        }
        chunks.push(c);
      });
      req.on('end', () => resolveBody(Buffer.concat(chunks).toString('utf8')));
      req.on('error', reject);
    });
  }

  function authorize(req) {
    const m = /^Bearer\s+(\S.*)$/i.exec(req.headers.authorization || '');
    if (!m) {
      return new ApiError(401, { message: "You did not provide an API key. You need to provide your API key in the Authorization header, using Bearer auth (e.g. 'Authorization: Bearer YOUR_SECRET_KEY'). See https://stripe.com/docs/api#authentication for details, or we can help at https://support.stripe.com/." });
    }
    const key = m[1].trim();
    if (cfg.apiKey && key !== cfg.apiKey) {
      return new ApiError(401, { code: 'api_key_invalid', message: `Invalid API Key provided: ${key.slice(0, 8)}${'*'.repeat(8)}${key.slice(-4)}` });
    }
    return null;
  }

  async function handleApi(req, res, method, pathname, query, rawBody, call) {
    const authError = authorize(req);
    if (authError) return fail(res, authError);

    const hit = routes.find((r) => r.method === method && r.re.test(pathname));
    if (!hit) {
      const known = routes.some((r) => r.re.test(pathname));
      return fail(res, new ApiError(known ? 405 : 404, { message: known ? `Method ${method} is not allowed for ${pathname}.` : `Unrecognized request URL (${method}: ${pathname}). The mock implements only what ReelBolt uses.` }));
    }
    const params = {};
    const m = hit.re.exec(pathname);
    hit.names.forEach((n, i) => (params[n] = decodeURIComponent(m[i + 1])));

    const isWrite = method === 'POST';
    if (isWrite && rawBody) {
      const type = String(req.headers['content-type'] || '');
      if (!type.includes('application/x-www-form-urlencoded')) {
        return fail(res, new ApiError(400, { message: 'Stripe expects application/x-www-form-urlencoded bodies; this request sent something else.' }));
      }
    }
    const input = isWrite ? { ...parseForm(rawBody), ...parseForm(query) } : parseForm(query);
    call.body = input;

    if (hit.allow) {
      for (const k of Object.keys(input)) {
        if (k === 'expand' || hit.allow.includes(k)) continue;
        return fail(res, new ApiError(400, { code: 'parameter_unknown', message: `Received unknown parameter: ${k}`, param: k }));
      }
    }

    // Idempotency-Key: POST only; same key + same parameters replays the first answer.
    const idemKey = isWrite ? req.headers['idempotency-key'] : undefined;
    const fingerprint = `${pathname}?${rawBody.split('&').sort().join('&')}`;
    if (idemKey && idemKey.length > 255) {
      return fail(res, new ApiError(400, { message: `Keys for idempotent requests cannot be longer than 255 characters, but this key is ${idemKey.length} characters long.` }));
    }
    if (idemKey) {
      const stored = state.idempotency.get(idemKey);
      if (stored) {
        if (stored.fingerprint !== fingerprint) {
          return fail(res, new ApiError(400, { type: 'idempotency_error', message: `Keys for idempotent requests can only be used with the same parameters they were first used with. Try using a key other than '${idemKey}' if you meant to execute a different request.` }));
        }
        call.replayed = true;
        return send(res, stored.status, stored.body, { 'idempotent-replayed': 'true' });
      }
    }

    const out = [];
    try {
      const result = hit.handler({ params, input, req }, out);
      const body = respond(result, input);
      if (idemKey) state.idempotency.set(idemKey, { fingerprint, status: 200, body });
      return send(res, 200, body);
    } catch (err) {
      if (err instanceof ApiError) return fail(res, err);
      throw err;
    }
  }

  function fail(res, err) {
    return send(res, err.status, errorBody(err));
  }

  // ---------------------------------------------------------------- browser pages

  function checkoutPage(res, s) {
    if (s?.object !== 'checkout.session') return send(res, 404, page('Checkout not found', '<h1 data-testid="mock-checkout-heading">Checkout not found</h1><p>This session does not exist in the mock (was it reset?).</p>'));
    const rows = s._lines.map((l) => `<tr><td><code>${escapeHtml(l.price)}</code></td><td>${l.quantity}</td><td>${(l.amount / 100).toFixed(2)}</td></tr>`).join('');
    const kind = s.metadata.topup_credits ? `Top-up: ${escapeHtml(s.metadata.topup_credits)} credits` : `Subscription: ${escapeHtml(s._lines.map((l) => l.price).join(', '))}`;
    const done = s.status === 'complete';
    const expired = s.status === 'expired';
    const brand = s.branding_settings ?? {};
    const bg = brand.background_color;
    const btn = brand.button_color;
    const radius = { pill: '999px', rounded: '8px', rectangular: '0' }[brand.border_style];
    // The values were validated on create (hex colours, enum border style), so they are safe inside the style block.
    const brandStyle = `${bg ? `body{background:${bg};color:#f5f5f7}` : ''}${btn ? `button:not(.secondary){background:${btn}}` : ''}${radius !== undefined ? `button{border-radius:${radius}}` : ''}`;
    const body = `${brandStyle ? `<style>${brandStyle}</style>` : ''}
${brand.display_name ? `<p data-testid="mock-checkout-brand-name" ${bg ? `style="background:${bg}"` : ''}>${escapeHtml(brand.display_name)}</p>` : ''}
${brand.logo?.url ? `<img data-testid="mock-checkout-brand-logo" alt="" src="${escapeHtml(brand.logo.url)}" height="32">` : ''}
<h1 data-testid="mock-checkout-heading">Stripe checkout (mock)</h1>
<p data-testid="mock-checkout-kind">${kind}</p>
<p>Session <code data-testid="mock-checkout-session">${escapeHtml(s.id)}</code> - status <strong data-testid="mock-checkout-status">${escapeHtml(s.status)}</strong></p>
<table><thead><tr><th>Price</th><th>Quantity</th><th>Amount (${escapeHtml(currency.toUpperCase())})</th></tr></thead><tbody>${rows}</tbody></table>
<p>Discount ${(s.total_details.amount_discount / 100).toFixed(2)} - total <strong data-testid="mock-checkout-total">${(s.amount_total / 100).toFixed(2)}</strong></p>
${s.discounts.length ? `<p data-testid="mock-checkout-coupon">Coupon <code>${escapeHtml(s.discounts[0].coupon)}</code></p>` : ''}
${expired ? '<p data-testid="mock-checkout-expired">This session expired and cannot be paid.</p>' : ''}
<form method="post" action="/pay/${encodeURIComponent(s.id)}" style="display:inline">
  <button type="submit" data-testid="mock-pay" ${expired ? 'disabled' : ''}>${done ? 'Return to the app' : 'Pay now'}</button>
</form>
<a href="${escapeHtml(s.cancel_url || `${cfg.appPublicUrl}/app/settings/billing`)}" data-testid="mock-checkout-cancel"><button type="button" class="secondary">Cancel</button></a>`;
    return send(res, 200, page('Stripe checkout (mock)', body));
  }

  function portalPage(res, id) {
    const session = lookup(id);
    if (session?.object !== 'billing_portal.session') return send(res, 404, page('Portal session not found', '<h1 data-testid="mock-portal-heading">Portal session not found</h1>'));
    const subs = all('subscription').filter((s) => s.customer === session.customer);
    const flow = session.flow?.type ?? 'overview';
    let extra = '';
    if (flow === 'subscription_cancel') {
      const sub = lookup(session.flow.subscription_cancel.subscription);
      extra = `<p>Subscription <code>${escapeHtml(sub?.id)}</code> - status ${escapeHtml(sub?.status)}${sub?.cancel_at_period_end ? ' (cancels at period end)' : ''}</p>
<form method="post" action="/portal/${encodeURIComponent(id)}/cancel"><button type="submit" data-testid="mock-portal-cancel">Cancel at the end of the billing period</button></form>`;
    } else if (flow === 'payment_method_update') {
      extra = '<p>The mock has no card to update.</p>';
    }
    const body = `
<h1 data-testid="mock-portal-heading">Stripe customer portal (mock)</h1>
<h2 data-testid="mock-portal-section">${escapeHtml(flow)}</h2>
<p>Customer <code data-testid="mock-portal-customer">${escapeHtml(session.customer)}</code> - configuration <code>${escapeHtml(session.configuration)}</code></p>
<ul>${subs.map((s) => `<li><code>${escapeHtml(s.id)}</code> - ${escapeHtml(s.status)} - ${s.items.data.map((i) => `${escapeHtml(i.price.id)} x ${i.quantity}`).join(', ')}</li>`).join('') || '<li>No subscriptions</li>'}</ul>
${extra}
<p><a href="${escapeHtml(session.return_url || `${cfg.appPublicUrl}/app/settings/billing`)}" data-testid="mock-portal-back">Back to ReelBolt</a></p>`;
    return send(res, 200, page('Stripe customer portal (mock)', body));
  }

  // ---------------------------------------------------------------- control API

  async function handleControl(req, res, method, parts, body, url) {
    const [head, a, b] = parts;
    const json = (data, status = 200) => send(res, status, data);
    const bad = (message, status = 400) => json({ error: message }, status);
    const settle = async (out) => Promise.all(out);

    if (method === 'GET' && head === 'health') return json({ ok: true, apiVersion: API_VERSION });

    if (method === 'GET' && head === 'state') {
      const list = (type) => all(type).sort((x, y) => x._seq - y._seq).map(view);
      return json({
        customers: list('customer'), sessions: list('checkout.session'), subscriptions: list('subscription'), invoices: list('invoice'),
        invoicePayments: list('invoice_payment'), paymentIntents: list('payment_intent'), charges: list('charge'), refunds: list('refund'), disputes: list('dispute'), coupons: list('coupon'),
        discounts: list('discount'), portalSessions: list('billing_portal.session'), webhooks: state.webhooks, dropFilters: state.dropFilters,
      });
    }

    if (method === 'GET' && head === 'calls') {
      const path = url.searchParams.get('path');
      const m = url.searchParams.get('method');
      let calls = state.calls;
      if (path) calls = calls.filter((c) => c.path.includes(path));
      if (m) calls = calls.filter((c) => c.method === m.toUpperCase());
      return json({ calls });
    }

    if (head === 'webhooks') {
      if (method === 'GET' && !a) return json({ webhooks: state.webhooks, dropFilters: state.dropFilters });
      if (method === 'POST' && (a === 'drop-next' || a === 'drop')) {
        const count = Math.max(1, Number(body.count ?? 1));
        const filter = {
          id: newId('drop', 8), event_type: body.event_type ?? null, subscription_id: body.subscription_id ?? null, object_id: body.object_id ?? null,
          count, remaining: count, dropped: 0,
        };
        state.dropFilters.push(filter);
        return json({ filter });
      }
      if (method === 'POST' && a === 'send') {
        if (!body.type || body.object === undefined) return bad('type and object are required');
        const event = buildEvent(body.type, { id: null, ...body.object });
        if (body.id) event.id = body.id;
        const record = await deliver(event, { secret: body.secret, signature: body.signature, attempts: body.attempts ?? 1 });
        return json({ event, delivery: record });
      }
      if (method === 'POST' && a && b === 'redeliver') {
        const event = state.deliveryEvents.get(a);
        if (!event) return bad(`no delivery ${a}`, 404);
        const record = await deliver(event, { attempts: Number(body.attempts ?? 1), redeliveryOf: a });
        return json({ event, delivery: record, status: record.attempts.at(-1)?.status ?? 0 });
      }
    }

    if (method === 'POST' && head === 'reset') {
      resetState();
      return json({ ok: true });
    }

    if (method === 'POST' && head === 'coupons') {
      try {
        const coupon = createCoupon(body);
        return json({ coupon: view(coupon) });
      } catch (err) {
        if (err instanceof ApiError) return bad(err.message, err.status);
        throw err;
      }
    }

    const out = [];
    try {
      // complete a checkout without the browser
      if (method === 'POST' && ((head === 'complete-checkout' && !a) || (head === 'checkout-sessions' && b === 'complete'))) {
        const id = head === 'complete-checkout' ? body.session_id : a;
        const s = lookup(id);
        if (s?.object !== 'checkout.session') return bad(`no checkout session ${id}`, 404);
        completeSession(s, out);
        if (body.wait !== false) await settle(out);
        return json({ session: view(s), subscription: s.subscription ? view(lookup(s.subscription)) : null, webhooks: state.webhooks.slice(-out.length) });
      }

      if (head === 'subscriptions' && a && method === 'POST') {
        const sub = lookup(a);
        if (sub?.object !== 'subscription') return bad(`no subscription ${a}`, 404);
        if (b === 'advance-period') {
          const inv = advancePeriod(sub, out);
          await settle(out);
          return json({ subscription: view(sub), invoice: inv ? view(inv) : null });
        }
        if (b === 'cancel-now') {
          if (sub.status === 'canceled') return bad('already canceled');
          cancelNow(sub, out);
          await settle(out);
          return json({ subscription: view(sub) });
        }
        if (b === 'set-status') {
          const allowed = ['active', 'trialing', 'past_due', 'unpaid', 'paused', 'incomplete', 'incomplete_expired'];
          if (!allowed.includes(body.status)) return bad(`status must be one of ${allowed.join(', ')}`);
          const before = view(sub);
          sub.status = body.status;
          out.push(enqueue('customer.subscription.updated', sub, diffPrevious(before, view(sub))));
          await settle(out);
          return json({ subscription: view(sub) });
        }
        if (b === 'payments') {
          if (!MODES.includes(body.mode)) return bad(`mode must be one of ${MODES.join(', ')}`);
          sub._payments = body.mode;
          return json({ mode: sub._payments });
        }
      }

      if (head === 'invoices' && a && method === 'POST' && (b === 'pay' || b === 'fail')) {
        const inv = lookup(a);
        if (inv?.object !== 'invoice') return bad(`no invoice ${a}`, 404);
        if (inv.status !== 'open') return bad(`invoice is ${inv.status}`);
        const subId = inv.parent?.subscription_details?.subscription;
        const sub = subId ? lookup(subId) : null;
        if (b === 'pay') {
          const before = sub ? view(sub) : null;
          markPaid(inv);
          if (sub && sub.status === 'past_due') {
            sub.status = 'active';
            out.push(enqueue('customer.subscription.updated', sub, diffPrevious(before, view(sub))));
          }
          out.push(enqueue('invoice.paid', inv));
        } else {
          inv.attempt_count += 1;
          if (sub) {
            const before = view(sub);
            sub.status = 'past_due';
            out.push(enqueue('customer.subscription.updated', sub, diffPrevious(before, view(sub))));
          }
          out.push(enqueue('invoice.payment_failed', inv));
        }
        await settle(out);
        return json({ invoice: view(inv) });
      }

      if (head === 'disputes' && method === 'POST') {
        if (a && b === 'close') {
          const existing = lookup(a);
          if (existing?.object !== 'dispute') return bad(`no dispute ${a}`, 404);
          closeDispute(existing, body.status ?? 'lost');
          out.push(enqueue('charge.dispute.closed', existing));
          await settle(out);
          return json({ dispute: view(existing), charge: view(lookup(existing.charge)) });
        }
        if (!a) {
          // status: an open one leaves the dispute open (created only); won / lost / warning_closed also close it.
          const wanted = body.status ?? 'needs_response';
          const closing = DISPUTE_CLOSED.includes(wanted);
          const dispute = createDispute({
            paymentIntent: body.payment_intent ?? lookup(body.charge)?.payment_intent,
            amount: body.amount === undefined ? undefined : Number(body.amount), reason: body.reason ?? 'fraudulent', status: closing ? 'needs_response' : wanted,
          });
          if (body.emit_created !== false) out.push(enqueue('charge.dispute.created', dispute));
          if (closing) {
            closeDispute(dispute, wanted);
            out.push(enqueue('charge.dispute.closed', dispute));
          }
          await settle(out);
          return json({ dispute: view(dispute), charge: view(lookup(dispute.charge)) });
        }
      }

      if (method === 'POST' && head === 'issue-refund') {
        let paymentIntent = body.payment_intent;
        if (!paymentIntent && body.invoice) {
          paymentIntent = all('invoice_payment').find((p) => p.invoice === body.invoice && p.status === 'paid')?.payment.payment_intent;
        }
        if (!paymentIntent && body.session) paymentIntent = lookup(body.session)?.payment_intent;
        const pi = lookup(paymentIntent);
        if (pi?.object !== 'payment_intent') return bad('no payment to refund: pass payment_intent, invoice or session');
        const refund = createRefund({ charge: lookup(pi.latest_charge), amount: body.amount === undefined ? undefined : Number(body.amount), reason: body.reason, metadata: {} });
        emitRefund(refund, out);
        await settle(out);
        return json({ refund: view(refund), charge: view(lookup(refund.charge)) });
      }
    } catch (err) {
      if (err instanceof ApiError) return bad(err.message, err.status);
      throw err;
    }

    return bad(`unknown control route ${method} /__mock/${parts.join('/')}`, 404);
  }

  // ---------------------------------------------------------------- server

  const server = createServer(async (req, res) => {
    const url = new URL(req.url, 'http://mock.local');
    const method = req.method || 'GET';
    const parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
    const call = {
      at: new Date(cfg.now()).toISOString(), method, path: url.pathname, query: url.search.replace(/^\?/, ''), body: null, rawBody: '',
      stripeVersion: req.headers['stripe-version'] ?? null, idempotencyKey: req.headers['idempotency-key'] ?? null, status: 0,
    };
    try {
      const rawBody = ['GET', 'HEAD'].includes(method) ? '' : await readBody(req);
      call.rawBody = rawBody;

      if (parts[0] === '__mock') {
        const body = rawBody ? safeJson(rawBody) : {};
        return await handleControl(req, res, method, parts.slice(1), body, url);
      }

      if (parts[0] === 'pay' && parts[1]) {
        const s = lookup(parts[1]);
        if (method === 'GET') return checkoutPage(res, s);
        if (method === 'POST') {
          if (s?.object !== 'checkout.session') return checkoutPage(res, null);
          try {
            completeSession(s, []);
          } catch (err) {
            if (!(err instanceof ApiError)) throw err;
            return send(res, 400, page('Checkout failed', `<h1 data-testid="mock-checkout-heading">Checkout failed</h1><p data-testid="mock-checkout-error">${escapeHtml(err.message)}</p>`));
          }
          res.writeHead(303, { location: s.success_url.replaceAll('{CHECKOUT_SESSION_ID}', s.id) });
          return res.end();
        }
      }

      if (parts[0] === 'portal' && parts[1]) {
        if (method === 'GET' && parts.length === 2) return portalPage(res, parts[1]);
        if (method === 'POST' && parts[2] === 'cancel') {
          const session = lookup(parts[1]);
          const sub = session?.flow?.subscription_cancel ? lookup(session.flow.subscription_cancel.subscription) : null;
          if (!sub || sub.status === 'canceled') return send(res, 404, page('Not found', '<h1>Nothing to cancel</h1>'));
          const before = view(sub);
          sub.cancel_at_period_end = true;
          sub.cancel_at = periodOf(sub).end;
          sub.canceled_at = nowSec();
          enqueue('customer.subscription.updated', sub, diffPrevious(before, view(sub)));
          res.writeHead(303, { location: session.return_url || `${cfg.appPublicUrl}/app/settings/billing` });
          return res.end();
        }
      }

      if (parts[0] === 'invoice' && parts[1] && method === 'GET') {
        const inv = lookup(parts[1]);
        return send(res, inv ? 200 : 404, page('Invoice (mock)', `<h1>Invoice ${escapeHtml(parts[1])}</h1><p>${inv ? `${escapeHtml(inv.billing_reason)} - total ${(inv.total / 100).toFixed(2)}` : 'unknown'}</p>`));
      }

      if (parts[0] === 'v1') {
        const response = await handleApi(req, res, method, url.pathname, url.search.replace(/^\?/, ''), rawBody, call);
        return response;
      }

      return send(res, 404, { error: { message: `Unknown path ${url.pathname}` } });
    } catch (err) {
      log('handler error', err);
      if (!res.headersSent) return send(res, 500, { error: { type: 'api_error', message: 'Internal mock error.' } });
    } finally {
      call.status = res.statusCode;
      if (parts[0] === 'v1') state.calls.push(call);
    }
  });

  return {
    server,
    state: () => state,
    reset: resetState,
    /** Resolves once every queued webhook has been delivered (or given up on). */
    idle: () => deliveryChain.then(() => undefined),
    listen: (port = 0, host = '127.0.0.1') => new Promise((ok) => server.listen(port, host, () => ok(server.address().port))),
    close: () => new Promise((ok) => server.close(() => ok())),
    config: cfg,
  };
}

function safeJson(raw) {
  try {
    return JSON.parse(raw);
  } catch {
    return {};
  }
}

// ------------------------------------------------------------------ run directly

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const port = Number(process.env.PORT || 8080);
  const secret = process.env.STRIPE_WEBHOOK_SECRET || '';
  const mock = createMock({
    apiKey: process.env.STRIPE_API_KEY || '',
    webhookSecret: secret,
    webhookUrl: process.env.WEBHOOK_URL || undefined,
    publicUrl: process.env.PUBLIC_URL || `http://localhost:${port}`,
    appPublicUrl: process.env.APP_PUBLIC_URL || undefined,
    webhookDelayMs: process.env.WEBHOOK_DELAY_MS === undefined ? undefined : Number(process.env.WEBHOOK_DELAY_MS),
    webhookRetryDelayMs: process.env.WEBHOOK_RETRY_DELAY_MS === undefined ? undefined : Number(process.env.WEBHOOK_RETRY_DELAY_MS),
    catalogPath: process.env.CATALOG_PATH || undefined,
    feeMinor: process.env.FAKE_FEE_MINOR === undefined ? undefined : Number(process.env.FAKE_FEE_MINOR),
  });
  await mock.listen(port, '0.0.0.0');
  console.log(new Date().toISOString(), '[stripe-mock]', `listening on :${port}; public ${mock.config.publicUrl}; app ${mock.config.appPublicUrl}; webhooks -> ${mock.config.webhookUrl}${secret ? '' : ' (NO SECRET: deliveries will be rejected)'}`);
  for (const sig of ['SIGTERM', 'SIGINT']) {
    process.on(sig, () => {
      console.log(new Date().toISOString(), '[stripe-mock]', `${sig}, shutting down`);
      mock.server.close(() => process.exit(0));
      setTimeout(() => process.exit(0), 2000).unref();
    });
  }
}
