# End-to-end tests

Playwright specs live in `tests/`. Most of them run against whatever `BASE_URL` points at (by default
the developer's own stack on `http://localhost`). Billing specs, and anything else that needs a known
starting state, run against an **isolated stack** instead: a second, minimal copy of ReelBolt with its
own compose project, ports and volumes, billing on, signup on, and a Paddle mock in place of Paddle.

## Isolated stack

### Up

```bash
E2E_PROJECT=reelbolt-myinit APP_PORT=8112 HTTPS_PORT=9112 PADDLE_MOCK_PORT=28112 \
  e2e/scripts/up-isolated.sh
```

For a Stripe stack add `--provider stripe` (see [Stripe fake](#stripe-fake)):

```bash
E2E_PROJECT=reelbolt-stripe-e2e APP_PORT=8112 HTTPS_PORT=9112 e2e/scripts/up-isolated.sh --provider stripe
```

This writes `e2e/.stack/<project>/` (git-ignored): `secrets.env` (random JWT key, sandbox token,
object-store and database passwords, webhook secret, initial admin password; generated once, then
reused), `garage.toml`, the compose `.env`, `env.sh` and, after seeding, `users.json`. It then runs

```bash
docker compose -p <project> --env-file e2e/.stack/<project>/.env \
  -f docker-compose.yml -f docker-compose.e2e.yml up -d --build --wait
```

and finally `e2e/scripts/seed-billing.mjs`. Re-running it is safe: secrets are reused and the seed
converges on the same state.

| Variable | Default | Meaning |
|---|---|---|
| `E2E_PROJECT` | `reelbolt-e2e` | Compose project name. `reelforge`, `reelbolt`, the checkout's directory name and an empty name are refused. |
| `APP_PORT` | `8180` | nginx HTTP on `127.0.0.1` (`BASE_URL`) |
| `HTTPS_PORT` | `8543` | nginx HTTPS on `127.0.0.1` (self-signed) |
| `PADDLE_MOCK_PORT` | `APP_PORT + 20000` | Paddle mock on `127.0.0.1` (`PADDLE_MOCK_URL`) |
| `E2E_BILLING_PROVIDER` / `--provider` | `paddle` | `paddle` or `stripe`: which provider takes new checkouts. `--provider stripe` also starts the [Stripe fake](#stripe-fake) |
| `STRIPE_MOCK_PORT` | `APP_PORT + 21000` | Stripe fake on `127.0.0.1` (`STRIPE_MOCK_URL`); only with `--provider stripe` |
| `E2E_PROFILES` | empty | `e2e-site` (marketing site at `/`), `e2e-engine` (workflow engine), comma-separated |
| `E2E_BUILD` | `1` | `0` reuses the images already built for this project |
| `E2E_SEED` | `1` | `0` skips the seed |
| `E2E_BUILD_NETWORK` | `default` | `host` when image builds fail with `DNS: transient error` (BuildKit handed an IPv6-only resolver it cannot reach) |

Nothing else is published on the host: postgres, garage, rabbitmq, qdrant and the web container have no
host port, `container_name` is cleared, qdrant uses a named volume instead of `./data/qdrant`.

### Down

```bash
E2E_PROJECT=reelbolt-myinit e2e/scripts/down-isolated.sh            # containers, network, volumes, e2e/.stack/<project>
E2E_PROJECT=reelbolt-myinit e2e/scripts/down-isolated.sh --keep-env # keep secrets and users.json
```

`down -v --remove-orphans` on that project only. Same name refusals as `up`.

### What runs, and what never does

Default `up`: postgres, garage, rabbitmq, qdrant, inference, go-api, web (production build, no
inspector), nginx, paddle-mock. nginx resolves upstreams per request, so routes of services that are
not running (`/` without `e2e-site`, `/docs`, `/mcp`, `/api/v1/workflow-engine/*` without `e2e-engine`)
answer 502 and nothing else breaks. Without `e2e-engine`, workflow runs queue and never start, and
engine-owned tables (workflow executions, step results) do not exist.

**Never** in an e2e stack (profile `e2e-forbidden`, which the scripts refuse to enable):

- `sandbox-executor`: at startup it deletes every `rf-sbx-*` container and unknown directories under
  `/var/lib/reelbolt/sandboxes` on the host, and creates the global network `sandbox-net`. It would
  destroy the developer's own sandboxes.
- `autoheal`: it restarts any `autoheal`-labelled container on the host, whatever project it belongs to.

Also off (profile `e2e-off`): whisper, embeddings, mcp, docs, runner-gateway, decision, comfyui, caddy.

### Using it from a spec

```bash
source e2e/.stack/<project>/env.sh   # BASE_URL, PADDLE_MOCK_URL, E2E_STACK_DIR
cd e2e && npx playwright test tests/billing/stack-smoke.spec.ts --project=desktop
```

```ts
import { loginAs, mock, user } from '../../fixtures/stack';

await loginAs(page, 'creator');                       // POST /api/v1/auth/token in this context only
await page.goto('/app/settings/billing');
const calls = await mock.calls({ path: '/subscriptions/' });
await mock.advancePeriod(user('creator').subscriptionId!);
```

`loginAs` never shares a storage-state file, so specs do not leak sessions into each other.

Sign-ins go through nginx's public-auth limiter. `up-isolated.sh` raises its rate (`E2E_AUTH_RATE`,
default `100r/s`; production is `5r/m`), but its `burst=5` sits in `nginx/locations.conf`, which the
base compose file mounts unchanged, so a few sign-ins landing in the same instant can still get a
429. `loginAs`, `signIn` and `postToken` retry a 429 with backoff; use them rather than posting to
`/api/v1/auth/token` yourself.

The marketing form endpoints are raised the same way (`E2E_SITE_FORM_RATE`, default `100r/s`):
`site_forms` ships at `1r/m` with a burst of 4, and the site specs submit the footer newsletter from
`127.0.0.1` with no 429 retry. Its `burst=4` also sits in `nginx/locations.conf`, so the raise only
changes how fast the bucket refills. `site_promotions` (`100r/m`, burst 100) is left as it ships.

### The billing suite

```bash
source e2e/.stack/<project>/env.sh
cd e2e
npx playwright test tests/billing/ --project=desktop   # flows
npx playwright test tests/billing/ --project=mobile    # layout smoke of both pages (the rest skips)
```

| Spec | Covers |
|---|---|
| `lifecycle.spec.ts` | Choose Creator → Pay now → Upgrade to Pro (payment held: Pro shows, balance unchanged; released: prorated credits) → Cancel plan (Keep my plan in the dialog, then confirm) → banner Keep my plan → Cancel plan → period end → Trial → Choose Pro again |
| `downgrade.spec.ts` | Switch to Creator → "Moves to Creator on …" → Keep Pro → Switch again → period end → Creator with Creator's credits; no plan change while a cancel is scheduled (UI and `409 cancel_scheduled`) |
| `topup.spec.ts` | the three credit packs; 500 credits paid → balance +500; Trial has no packs |
| `seats.spec.ts` | Choose Team with seats; more seats now (credits only once the held payment is released); fewer seats confirmed and applied at period end; above the cap refused |
| `upgrade-payment.spec.ts` | the upgrade charge's shape; a failed payment never adds credits; a refund of the upgrade payment claws back its unspent grant |
| `portal.spec.ts` | Manage in Paddle, Update payment method, Cancel in Paddle; canceled → Trial still reaches the portal; Trial without a customer cannot |
| `roles.spec.ts` | a Member: read-only page, banners without buttons, every billing POST → 403 |
| `webhook-gap.spec.ts` | paid but webhooks held back (`complete {delayMs}`): a second plan checkout → `409 subscription_pending` on screen |
| `links.spec.ts` | "See plans and credits" → Plan & billing; no in-app anchor leaves the `/app` basePath |
| `errors.spec.ts` | the Retry button of both pages |
| `repro-walk.spec.ts` | the T-002 reproduction walk, every finding now asserted fixed; Usage controls and breadcrumbs; mobile layout |
| `stack-smoke.spec.ts` | the seeded state |

Every spec that changes billing state creates its own throwaway users (`Throwaways` in
`fixtures/stack.ts`: admin API, deleted in `afterAll`; `createTeamOrg` and `addMember` for a Team
workspace with a Member) and waits for webhook-driven state with `expectSummary` (polls
`GET /api/v1/billing/summary`) or the page's own Done notice, never with a fixed sleep. Seeded users
are only read. Each file runs serially; files run in parallel.

### Seeded users

All passwords are in `users.json` (derived from the stack's own secret, so they survive re-seeding).

| Label | Email | Org (role) | Billing state |
|---|---|---|---|
| `admin` | `admin@e2e.test` | Personal (Owner), platform admin | Trial |
| `trial` | `trial@e2e.test` | Personal (Owner) | Trial, trial credits granted |
| `creator` | `creator@e2e.test` | Personal (Owner) | Creator, Active, billing customer, via mock checkout |
| `team` | `team@e2e.test` | `E2E Team` (Owner) | Team, 3 seats, Active |
| `canceled` | `canceled@e2e.test` | Personal (Owner) | Subscribed to Creator, canceled immediately → Trial |
| `member` | `member@e2e.test` | `E2E Team` (Member) | Sees the Team plan; cannot manage billing |

Plans get Paddle price ids `pri_e2e_creator`, `pri_e2e_pro`, `pri_e2e_team` (SQL, as no endpoint sets
them); top-ups use `pri_e2e_topup_500/1000/3000`. The member's invite token is written by SQL because
the API only ever emails it.

## Paddle mock

`e2e/paddle-mock/server.mjs`, Node with no dependencies, in-memory state. The Inference API reaches it
at `http://paddle-mock:8080` (`Billing__Paddle__BaseUrl`), with `Billing__Paddle__ClientToken` empty so
the dashboard follows the hosted checkout URL instead of loading Paddle.js.

API (Bearer `e2e-mock-key`, `{data, meta}` envelopes, Paddle error shape `{error:{type,code,detail}}`):
`POST /transactions`, `GET /transactions/{id}`, `POST /customers` (409 `customer_already_exists` on a
known email), `GET /customers/{id}`, `POST /customers/{id}/portal-sessions`, `GET /subscriptions/{id}`,
`PATCH /subscriptions/{id}` (items + `proration_billing_mode`, or `scheduled_change: null`; items are
refused with 400 `subscription_locked_pending_changes` while a cancel is scheduled),
`POST /subscriptions/{id}/cancel` (`next_billing_period` schedules, `immediately` cancels),
`GET /transactions` (Paddle's list envelope `{data, meta: {pagination}}`; filters `subscription_id`,
`customer_id`, `id`, `status`, `origin` as comma lists, `billed_at` / `updated_at` exact or
`[LT|LTE|GT|GTE]`, `per_page` 1-200 (default 30) with `after` paging), the read the billing reconciler
uses to find a lost upgrade payment (`status=completed&origin=subscription_update&billed_at[GTE]=...`).

Subscriptions carry `updated_at`, moved on every change (PATCH, cancel, resume, renewal); the webhook
announcing the change has that same instant as `occurred_at`, as Paddle does. Transactions carry
`billed_at` and `updated_at`.

Browser pages: `GET /checkout/{txnId}` ("Pay now", `data-testid="mock-pay"`, then redirect to
`{app}/app/checkout?_ptxn={txnId}`), `GET /portal/{session}/overview` and
`/portal/{session}/subscriptions/{id}/{cancel|update-payment-method}` (heading "Paddle customer portal
(mock)", `data-testid="mock-portal-heading"`).

Completing a subscription checkout creates an active subscription for one month and sends signed
`transaction.completed` then `subscription.created` about 300 ms later; a top-up sends only
`transaction.completed`. Every state change sends `subscription.updated` or `subscription.canceled`.
`occurred_at` strictly increases. Webhooks go out in order and are retried on failure.

A `PATCH` whose items change with `proration_billing_mode: prorated_immediately` (what the Inference API
sends for an upgrade or a seat increase) is charged the way Paddle does: a transaction `txn_...` with
`origin: subscription_update`, the subscription and customer ids, the new items, each with
`proration {rate, billing_period {starts_at: the PATCH instant, ends_at: the period end}}`.
`subscription.updated` (whose `occurred_at` is that same instant) goes first, then a signed
`transaction.completed`. `full_next_billing_period` and `scheduled_change: null` charge nothing. Specs can
hold or fail that payment per subscription (below) to check that upgrade credits follow the payment.

Control API (no auth, `127.0.0.1` only):

| Route | Does |
|---|---|
| `GET /__mock/state` | customers, transactions, subscriptions, portal sessions, webhook deliveries |
| `GET /__mock/calls?path=&method=` | every API call the Inference API made, with body and status |
| `GET /__mock/webhooks` | webhook deliveries (each with an `id`, `dropped`, `redelivery_of`) and their attempts, plus the armed drop filters |
| `POST /__mock/webhooks/drop` | lose webhooks on purpose: `{event_type?, subscription_id?, transaction_id?, count = 1}`; the next `count` matching webhooks are recorded with `dropped: true` and never sent |
| `POST /__mock/webhooks/{deliveryId}/redeliver` | send a recorded delivery (dropped or not) again with its original `event_id` and `occurred_at`, freshly signed, never dropped (`{attempts}` optional, default 1); answers `{event, delivery, status}` with the receiver's status |
| `POST /__mock/reset` | forget everything, drop filters included (the database keeps its rows) |
| `POST /__mock/transactions/{id}/complete` | pay a checkout without the browser (`{delayMs}` optional); for a held proration charge, release it (`transaction.completed`) |
| `POST /__mock/subscriptions/{id}/payments` | `{mode: pay\|hold\|fail}` for the subscription's next proration charges (default `pay`) |
| `GET /__mock/subscriptions/{id}/transactions` | the subscription's proration charges, oldest first |
| `POST /__mock/transactions/{id}/fail` | a held proration charge fails: `past_due`, `transaction.payment_failed` (`{notify: false}` to send none) |
| `POST /__mock/transactions/{id}/refund` | `adjustment.created` for a completed transaction: `{action: refund\|chargeback, type: full\|partial, status: approved}` (defaults shown first) |
| `POST /__mock/subscriptions/{id}/advance-period` | renewal: next period, `subscription.updated`; a scheduled cancel becomes `subscription.canceled` |
| `POST /__mock/subscriptions/{id}/cancel` | cancel as Paddle would (`{effective_from}`) |
| `POST /__mock/webhooks/send` | send `{event_type, data}` or `{payload}` signed (`secret`/`signature` override for negative tests) |
| `POST /__mock/pricing-preview/mode` | fault knob for `POST /pricing-preview` (the plan-price lookup): `{mode: ok\|error\|tax}`. `error` answers every preview `500` with a Paddle-shaped error; `tax` adds 10% tax to each line and its totals; `ok` restores the normal preview. Cleared by `POST /__mock/reset` |

A drop filter is matched when the webhook is queued, so arm it before the call that causes the change
(`mock.dropWebhooks`), then find the lost delivery in `mock.webhooks()` and resend it with
`mock.redeliver(id)`; `mock.listTransactions(subscriptionId, filter)` reads `GET /transactions`. With
no filter armed nothing changes. The stack runs the reconcile sweep fast
(`Billing__Reconcile__StuckAfterSeconds` 8 via `E2E_RECONCILE_STUCK_AFTER_SECONDS`,
`SweepIntervalSeconds` 4 via `E2E_RECONCILE_SWEEP_SECONDS`, first sweep 5 s after start).

State is in memory: restarting the `paddle-mock` container forgets the seeded subscriptions while the
database still has them. Bring the stack down and up again to start clean.

### Promotions endpoints

Discounts and price previews, for the promotions specs. Undiscounted flows are unchanged: a
transaction without a `discount_id` returns and sends exactly the payload it did before.

- `POST /discounts` stores `{id: "dsc_" + 26 lowercase alnum, status: "active", ...body, times_used: 0}`
  and returns `{data, meta}` (201). It checks `description`, `type` (`percentage` | `flat` |
  `flat_per_seat`), `amount` (string; percent, or minor units for flat) and `currency_code` for flat.
- `GET /discounts/{id}`, and `PATCH /discounts/{id}`, which merges `status` (`active` | `archived`)
  and `expires_at` (`null` clears it) and nothing else. There is no delete, as in Paddle.
- `POST /transactions` with a `discount_id` refuses, with 400 and Paddle's code: a missing or
  archived discount (`transaction_discount_not_found`), an expired one (`discount_expired`), a used-up
  `usage_limit` (`discount_usage_limit_exceeded`), or a `restrict_to` that matches none of the items
  (`transaction_discount_not_eligible`). A discounted transaction's payload, and its
  `transaction.completed` webhook, carry a top-level `discount_id` and `details.totals`
  (`subtotal`, `discount`, `tax`, `total`, `grand_total` in cents, as strings). Completing it bumps
  the discount's `times_used`. A `recur: true` discount also sets the new subscription's `discount`.
- `POST /pricing-preview` takes `items[]` (`price_id`, `quantity`) and an optional `discount_id`
  (same checks as above), and answers like Paddle: `data.currency_code: "USD"` and
  `data.details.line_items[]` with `price.id`, `quantity`, `totals` (cents as strings, tax `"0"`) and
  `formatted_totals` (`{subtotal: "$79.00", discount: "-$15.80", tax: "$0.00", total: "$63.20"}`
  for Pro at 20% off). Unit prices, in cents: `pri_e2e_creator` 2900, `pri_e2e_pro` 7900,
  `pri_e2e_team` 4900 per seat, `pri_e2e_topup_500/1000/3000` 1800/3500/10500, from
  `plans.catalog.json`. `PADDLE_MOCK_PRICES='{"pri_x": 1234}'` adds or overrides prices. An unknown
  price is a 404. Percent discounts round to the cent per line; `flat` applies once, capped at the
  line's subtotal. This is the only place in the stack that does display maths.

Control API additions:

| Route | Does |
|---|---|
| `GET /__mock/discounts` | every discount, with `status`, `expires_at`, `restrict_to`, `custom_data` and `times_used` |
| `GET /__mock/transactions` | every transaction as Paddle shows it, plus `request`: the exact body the Inference API sent (`discount_id`, `custom_data.promotion_id`) |

`POST /__mock/reset` forgets the discounts too. `GET /__mock/calls` still records every API call and
its body, `POST /discounts`, `PATCH /discounts/{id}` and `POST /pricing-preview` included.

## Stripe fake

`e2e/stripe-mock/server.mjs`, Node with no dependencies, in-memory state: a stateful stand-in for the Stripe
API at the version the provider pins (`2026-09-30.endive`). It exists so the whole Stripe path (hosted
checkout, subscriptions, proration invoices, portal, refunds, signed webhooks) runs with no network, no
account and no key. Every amount comes from `plans.catalog.json` at start-up (mounted read-only); there is
no price literal in the file. `node --test e2e/stripe-mock/server.test.mjs` runs its own 53 offline tests
(no docker).

```bash
e2e/scripts/up-isolated.sh --provider stripe     # the `stripe` compose profile: stripe-mock, Billing__Provider=stripe
source e2e/.stack/<project>/env.sh               # BASE_URL, STRIPE_MOCK_URL, E2E_BILLING_PROVIDER=stripe, ...
```

`--provider stripe` generates a test key (`sk_test_e2e_mock_...`) and webhook secret (`whsec_e2e_...`) into
`secrets.env`, starts the fake on `127.0.0.1:$STRIPE_MOCK_PORT`, points the Inference API at it
(`Billing__Stripe__BaseUrl=http://stripe-mock:8080`, `ManagedPayments=${E2E_STRIPE_MANAGED_PAYMENTS:-true}`, the three top-up price ids, the seven `Billing__Stripe__Branding__*` values,
the dedicated portal configuration `bpc_e2e_portal`) and seeds with `seed-billing.mjs --provider stripe`:
the plans get `{"stripe": {"monthly": "price_e2e_creator"}}` merged into `provider_price_ids_json` (a
`paddle` key is kept), and the seeded Creator, Team and canceled users are Stripe subscriptions. The Paddle
mock still runs; the default (`paddle`) stack is unchanged. The fake also seeds a yearly price per plan that has
`priceAnnual` in the catalog (`price_e2e_<plan>_annual`, interval `year`, lookup key `rb:<plan>:year:usd`); a
yearly checkout or renewal advances the period by twelve months, and a monthly-to-yearly change restarts the cycle
now (unused month credited, yearly price charged in full). Add `"annual": "price_e2e_<plan>_annual"` next to
`monthly` in `provider_price_ids_json` to sell it (the seed script does not yet). `down-isolated.sh` removes the fake too.

API (`Authorization: Bearer <key>` only, form-encoded bodies in Stripe's nested syntax, Stripe-shaped errors
`{error: {type, code, message, param}}`; a wrong key is 401, a JSON body or an unknown parameter is 400, and
a removed parameter such as `payment_method_types` is refused like any unknown one):
customers (create, get), Checkout Sessions (create in `subscription` or `payment` mode, get with
`expand[]=line_items`, list by `payment_intent` / `subscription`, `expire`), subscriptions (get with
`expand[]=discounts`, list by `customer` and `status`, update with `items[n][id|price|quantity|deleted]`,
`cancel_at_period_end`, `proration_behavior` `always_invoice` | `create_prorations` | `none`, `discounts=`;
`DELETE` cancels now), billing portal sessions (with `flow_data`), invoices (get, list, `create_preview`),
`invoice_payments`, payment intents, charges, balance transactions (a fixed fake fee), coupons (create with
a custom id, `resource_already_exists`, get, delete), prices, products and refunds (full or partial). Any
`expand[]` path works (`data.payment.payment_intent.latest_charge.balance_transaction`). A POST with an
`Idempotency-Key` replays its first answer for the same parameters and is an `idempotency_error` for others
(a key over 255 characters is refused; a failed request is not stored, so a retry with the same key can succeed).

What the provider sends is accepted, and what Stripe refuses is refused:

- `payment_behavior=error_if_incomplete` on a subscription update with `always_invoice` (the app's immediate
  plan and seat changes): applied only if the proration invoice is paid. With the `fail` payment mode the call
  answers `402` `card_error` / `card_declined` (`decline_code: generic_decline`), with `hold`
  `invoice_payment_intent_requires_action`; either way the whole call is rolled back (items, status, discounts,
  invoices, payments) and no webhook is sent. Only `allow_incomplete` (the default) and `error_if_incomplete` exist.
- Checkout Session create with `managed_payments` off (`E2E_STRIPE_MANAGED_PAYMENTS=false`; the default stack runs Managed Payments, the shipped mode): `automatic_tax[enabled]=true`,
  `customer_update[address|name|shipping]=auto|never` (only with an existing `customer`, and Stripe Tax on an
  existing customer without an address needs `customer_update[address]=auto`; completing the session saves an
  address and name on the customer). With `managed_payments[enabled]=true` those parameters are refused as
  `parameter_unknown`.
- Checkout Session create with `branding_settings` (the stack passes the seven `Billing__Stripe__Branding__*` values, the
  production defaults `#0B0B12`, `#7C3AED`, `rectangular`, `chakra_petch`, `ReelBolt` and the two `https://reelbolt.ai/brand/stripe-*.png`
  URLs): `background_color` and `button_color` as `#RRGGBB`, `border_style` `pill|rectangular|rounded`, `font_family` a lower-case
  token, `display_name` 1 to 100 characters, `logo` and `icon` as `[type]=url` plus an `https` `[url]`, or `[type]=file` plus a
  `file_...` id. Invalid values are `invalid_request_error`, other keys `parameter_unknown`, and nothing is created. The object is
  stored on the session as `branding_settings` (echoed on read, `null` when absent) and the hosted page shows it:
  `mock-checkout-brand-name` (the display name, with the background colour as its style), `mock-checkout-brand-logo`, the button
  colour and radius.
- Lists: `GET /v1/checkout/sessions?subscription=` / `?payment_intent=` / `?customer=` with `limit`.

Shapes follow API versions from basil on: subscription periods live on the items, invoice lines carry
`pricing.price_details.price` and `parent.subscription_item_details`, invoices `parent.subscription_details`
(the subscription id and the metadata of `subscription_data[metadata]`). An upgrade with
`always_invoice` makes a `subscription_update` invoice: a negative credit line for the old price, positive
lines for the new one, `period.start` = the proration time (`proration_date`, else now). Completing a
subscription checkout sends `customer.subscription.created`, `invoice.paid`, `checkout.session.completed`;
a top-up sends `checkout.session.completed`; changes send `customer.subscription.updated|deleted`,
`invoice.paid`, `refund.created` (status `succeeded`) and `charge.refunded`.

Webhooks: `Stripe-Signature: t=<unix>,v1=<hmac>,v0=<dummy>` (HMAC-SHA256 of `"{t}.{body}"` with the stack's
secret), `api_version: 2026-09-30.endive`, `livemode: false`, sent to `WEBHOOK_URL` after
`WEBHOOK_DELAY_MS` (300), one at a time in order, signed at send time, retried up to 4 times on any
non-2xx.

Browser pages: `GET /pay/{sessionId}` (heading `mock-checkout-heading`, `mock-checkout-session`,
`mock-checkout-total`, `mock-checkout-coupon`, `mock-pay`; the POST completes the session and answers 303
to its `success_url` with `{CHECKOUT_SESSION_ID}` filled in), `GET /portal/{sessionId}` (`mock-portal-heading`,
`mock-portal-section` = the flow type or `overview`, `mock-portal-cancel` on a cancel flow,
`mock-portal-back`).

Control API (no auth, `127.0.0.1` only):

| Route | Does |
|---|---|
| `GET /__mock/health` | `{ok, apiVersion}` |
| `GET /__mock/state` | customers, sessions, subscriptions, invoices, invoice payments, payment intents, charges, refunds, disputes, coupons, discounts, portal sessions, webhook deliveries, armed drop filters |
| `GET /__mock/calls?path=&method=` | every `/v1` call the Inference API made, with the parsed form `body`, `rawBody`, `stripeVersion`, `idempotencyKey`, `status` and `replayed`; the API key is never recorded |
| `GET /__mock/webhooks` | deliveries (each with an `id`, `dropped`, `delivered`, `attempts`, `redelivery_of`) |
| `POST /__mock/webhooks/drop-next` | lose webhooks on purpose: `{event_type?, subscription_id?, object_id?, count = 1}`; the next matching ones are recorded with `dropped: true` and never sent. Arm it before the call that causes them |
| `POST /__mock/webhooks/{deliveryId}/redeliver` | send a recorded delivery (dropped or not) again with its original event id, freshly signed |
| `POST /__mock/webhooks/send` | `{type, object, secret?, signature?}`: an arbitrary event, optionally mis-signed (negative tests) |
| `POST /__mock/complete-checkout` | `{session_id}`: pay a Checkout Session without the browser; resolves after its webhooks were delivered |
| `POST /__mock/subscriptions/{id}/advance-period` | renewal: next period, a `subscription_cycle` invoice, `customer.subscription.updated` + `invoice.paid`; a scheduled cancel becomes `customer.subscription.deleted` instead |
| `POST /__mock/subscriptions/{id}/cancel-now` | `customer.subscription.deleted` |
| `POST /__mock/subscriptions/{id}/set-status` | `{status}`: `active`, `trialing`, `past_due`, `unpaid`, `paused`, `incomplete`, ... + `customer.subscription.updated` |
| `POST /__mock/subscriptions/{id}/payments` | `{mode: pay\|hold\|fail}` for the subscription's next upgrade and renewal invoices: paid (default), left open, or failed (subscription `past_due`, `invoice.payment_failed`). An update sent with `payment_behavior=error_if_incomplete` (what the app sends for an upgrade or a seat increase) is refused with `402` instead and changes nothing |
| `POST /__mock/invoices/{id}/pay` · `/fail` | pay or fail a held invoice |
| `POST /__mock/issue-refund` | `{payment_intent \| invoice \| session, amount?}`: `refund.created` then `charge.refunded` |
| `POST /__mock/disputes` | `{payment_intent, status?, amount?, reason?, emit_created?}`: a chargeback shaped like Stripe's Dispute (`dp_...`, `payment_intent`, `charge`, `amount`, `currency`, `status`, `reason`); sends `charge.dispute.created` (status `needs_response`), and when `status` is `won`, `lost`, `warning_closed` or `charge_refunded` also `charge.dispute.closed` with it (`emit_created: false` sends only the closed one). `amount` defaults to the whole charge. A lost dispute leaves the charge's `amount_refunded` at 0 |
| `POST /__mock/disputes/{id}/close` | `{status = lost}`: `charge.dispute.closed` for an open dispute |
| `POST /__mock/coupons` | create a coupon the way the catalog sync would (`{id, percent_off, duration, ...}`) |
| `POST /__mock/reset` | forget everything and the armed filters (prices come back from the catalog; the database keeps its rows) |

In a spec: `import { stripeMock, isStripeStack, subscribeViaStripeApi } from '../../fixtures/stack'`
(`stripeMock.completeCheckout`, `.advancePeriod`, `.refund`, `.dispute`, `.closeDispute`, `.cancelNow`, `.dropNextWebhook`, `.redeliver`,
`.setPayments`, `.payInvoice`, `.calls`, `.state`, `.liveSubscriptionOf`).

### The Stripe billing suite

`tests/billing-stripe/`, Playwright project `billing-stripe`; every spec skips itself unless the stack is a
`--provider stripe` one, and the `desktop` and `mobile` projects leave the folder out.

```bash
e2e/scripts/up-isolated.sh --provider stripe && source e2e/.stack/<project>/env.sh
cd e2e
npx playwright test --project=billing-stripe --workers=2
npx playwright test --project=billing-stripe-restart --no-deps --workers=1   # restarts the Inference API, runs alone
```

| Spec | Covers |
|---|---|
| `subscribe.spec.ts` | Choose Creator → hosted checkout → pay → Active, plan credits granted once (every webhook redelivered, nothing more); the Checkout Session the API sent (pinned version, org on session and subscription, idempotent customer) |
| `topup.spec.ts` | an abandoned pack charges nothing; 500 credits paid → balance +500; the same webhook redelivered twice grants nothing more |
| `seats.spec.ts` | Team 3 → 5 (`always_invoice` + `error_if_incomplete`, prorated invoice paid, credits on `invoice.paid`) → 4 at period end (`proration_behavior=none`, no invoice, renewal bills four) |
| `upgrade-payment-failed.spec.ts` | h: an upgrade whose card is declined (`fail` mode) → 402 `card_declined` from Stripe, `409 billing_provider_refused` in the page, plan and credits unchanged, no invoice and no webhook; with a working card the retry (a new idempotency key) upgrades |
| `chargeback.spec.ts` | i: a top-up whose dispute is opened (nothing happens) and then lost → the credits go back once (the closed event again changes nothing); a won dispute takes nothing |
| `cancel-resume.spec.ts` | cancel / keep from the dashboard, the portal links (`manage-in-stripe`, `update-payment-method`, `cancel-in-stripe`) on the dedicated configuration, a cancel made inside the portal, period end → Trial |
| `webhook-lost.spec.ts` | A: upgrade with its webhooks lost → `409 change_pending` → the sweep heals to Pro with one grant → late redelivery is a no-op. C: a lost top-up webhook is granted once on redelivery. Stripe has no `updated_at`, so "too recent" counts from the watch the upgrade request recorded: the spec clears the checkout's leftover watch first (`clearReconcileWatch`) |
| `webhook-lost-lazy.spec.ts` | B: a seat change whose webhook is lost is healed by the next seat change (sweep paused by restarting inference; project `billing-stripe-restart`; the checkout's leftover watch is cleared first, nothing else would clear it) |
| `refund.spec.ts` | a top-up refunded in full → `refund.created` resolved through Stripe → credits clawed back once |
| `founder-price.spec.ts` | the 50%-forever `rb_founder_alpha` coupon linked to a plan-discount promotion: checkout and renewal both pay half |

## Reusing it from another initiative

Pick your own project name and ports; nothing else changes. For example:

- render resolution: `E2E_PROJECT=reelbolt-saas-render-resolution APP_PORT=8113 HTTPS_PORT=9113`
- model catalog: `E2E_PROJECT=reelbolt-saas-model-catalog APP_PORT=8114 HTTPS_PORT=9114`
- promotions: `E2E_PROJECT=reelbolt-saas-promotions APP_PORT=8115 HTTPS_PORT=9115`
- estimate range: `E2E_PROJECT=reelbolt-saas-estimate-range APP_PORT=8116 HTTPS_PORT=9116 E2E_PROFILES=e2e-engine`
- marketing refresh: `E2E_PROJECT=reelbolt-saas-marketing-refresh APP_PORT=8117 HTTPS_PORT=9117 E2E_PROFILES=e2e-site`

## Soft-open rehearsal (signup switch)

`tests/soft-open/signup-to-checkout.spec.ts` rehearses what the operator flips in production (`SIGNUP_ENABLED=true` on go-api
AND `NEXT_PUBLIC_SIGNUP_ENABLED=true` at site build time): negative control, then CTA, signup, email verification
(link read from the go-api console log), the per-IP cap, the Terms gate, the free first video and a Stripe upgrade
against the fake. It needs a Stripe stack with the site and the engine (the Inference API reads workflow tables the
engine creates), and no paid provider is ever configured.

```bash
export E2E_PROJECT=reelbolt-soft-open
E2E_BUILD_NETWORK=host APP_PORT=8130 HTTPS_PORT=9130 E2E_PROFILES=e2e-site,e2e-engine e2e/scripts/up-isolated.sh --provider stripe
source e2e/.stack/$E2E_PROJECT/env.sh && cd e2e && npm ci   # the commands below run from e2e/

scripts/soft-open-switches.sh off            # go-api + web SIGNUP_ENABLED=false, a 2nd site build (NEXT_PUBLIC_SIGNUP_ENABLED=false) on :8131
SOFT_OPEN_PHASE=off npx playwright test -c playwright.soft-open.config.ts
scripts/soft-open-switches.sh on             # back to the stack default
SOFT_OPEN_PHASE=on  npx playwright test -c playwright.soft-open.config.ts

scripts/soft-open-switches.sh down           # remove the off-site container and image
scripts/down-isolated.sh                     # tear the stack down
```

Run the Playwright commands through the repository test governor with the e2e class.
Traces, screenshots and the JSON report go to `/tmp/launch-soft-open-rehearsal/` (`SOFT_OPEN_ARTIFACTS`).

What it does not prove: Turnstile (off here, on in production), real SMTP (mail goes to the console log, whose recipient is a
hashed ref), real Stripe (the fake), the engine's providers, and nginx's production auth limiter (5r/m; the stack raises it).
The main account uses a real mail domain (`SOFT_OPEN_MX_DOMAIN`, default `gmail.com`) because the first-video check does a live
MX lookup and `.test` has none; offline, that check fails open.

### Stale-image trap

If the stack's web image build fails at typecheck, `docker compose up` still starts the previous image, so a spec runs against old code and can pass or fail for the wrong reason. After building from a worktree, confirm the build exited 0 and that the running image is the new one before trusting a result.
