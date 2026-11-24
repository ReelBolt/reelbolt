# Billing

Developer and operator reference for ReelBolt's hosted billing (work packages B9a and B9b): the
provider boundary, the Paddle and Stripe implementations, how a webhook turns into subscription state and
credits, the endpoints, and what an operator must configure. The credit ledger itself (grants,
reservations, refunds) is in the code under `ReelBolt.Shared/Billing/CreditLedger.cs`; the pricing
rationale is in `design/saas-pricing-and-billing.md`.

## Switching billing on

Billing is off by default, which is the self-host behaviour. With `Billing__Enabled=false` (the
`.env` variable is `BILLING_ENABLED`) every `/api/v1/billing/*` endpoint answers `404`, no provider
is contacted, and nothing here runs. Hosted deployments set `BILLING_ENABLED=true`, choose a provider
with `BILLING_PROVIDER` (`Paddle`, the default, or `Stripe`; see [Stripe](#stripe)) and set that
provider's values. The Paddle values are:

| `.env` variable | Config key | Secret | Meaning |
|---|---|---|---|
| `BILLING_ENABLED` | `Billing__Enabled` | no | `false` (default) = billing endpoints 404. |
| `BILLING_PROVIDER` | `Billing__Provider` | no | `Paddle` (default, also when blank) or `Stripe`: the provider new checkouts, previews and promotions use. Existing subscriptions keep the provider they were bought with. The `STRIPE_*` variables are in [Stripe configuration](#stripe-configuration-stripe). |
| `PADDLE_API_KEY` | `Billing__Paddle__ApiKey` | yes | Server API key, `pdl_sdbx_apikey_...` (sandbox) or `pdl_live_apikey_...`. |
| `PADDLE_CLIENT_TOKEN` | `Billing__Paddle__ClientToken` | **no** | Client-side (publishable) token for the embedded checkout, `test_...` or `live_...`. The API serves it to the browser, so it is not a secret and must never be a server key. Blank = no embedded checkout (the buyer uses Paddle's hosted page). |
| `PADDLE_WEBHOOK_SECRET` | `Billing__Paddle__WebhookSecret` | yes | The notification destination's secret key. |
| `PADDLE_ENVIRONMENT` | `Billing__Paddle__Environment` | no | `sandbox` (default) or `live`. Selects the API base URL. A sandbox key only works against sandbox. |
| `PADDLE_TOPUP_PRICE_500`, `_1000`, `_3000` | `Billing__Paddle__TopUpPriceIds__{credits}` | no | Paddle price ids (`pri_...`) of the three top-up packs. A blank one means that pack is not sold. |
| none | `Billing__Paddle__ToleranceSeconds` | no | Largest accepted age of a webhook's signed timestamp. Default `5`. |
| none | `Billing__Paddle__MaxSelfServeSeats` | no | Largest seat count a per-seat subscription can be set to from the dashboard (`Billing:Paddle:MaxSelfServeSeats`). Default `200`. Above it `/seats` and `/subscription/change-plan` answer `400 too_many_seats`; bigger teams go through sales. |
| none | `Billing__Paddle__DiscountsEnabledForCheckout` | no | Sent as `enabled_for_checkout` on every discount a promotion creates. Default `false`. See [Promotions and discounts](#promotions-and-discounts). |
| none | `Billing__Promotions__CheckoutReservationMinutes` | no | How long an unpaid checkout holds its promotions. Default `60`. See [Checkout reservations](#checkout-reservations). |
| none | `Billing__Promotions__CheckoutsPerUserPerHour` | no | Checkouts carrying a promotion one user may make per hour. Only checkouts that reserve count. Default `10`. See [Checkout reservations](#checkout-reservations). |
| none | `Billing__Promotions__CheckoutSweepIntervalMinutes` | no | How often the checkout sweeper cancels the Paddle transactions of expired and superseded reservations. Default `5`; `0` or less switches it off. See [Cancelling stale checkouts](#cancelling-stale-checkouts). |

`host.sh check-env` requires both Paddle secrets on the control host when `BILLING_ENABLED=true` and
`BILLING_PROVIDER` is `Paddle` or unset; with `BILLING_PROVIDER=Stripe` it requires the Stripe values
instead (see [Stripe configuration](#stripe-configuration-stripe)). Secret handling and rotation are in
[`secrets.md`](secrets.md). Moving a production deployment from Paddle to Stripe is a runbook of its own:
`billing-stripe-switch-over.md`.

Lost-webhook recovery (see [When a Paddle webhook is lost](#when-a-paddle-webhook-is-lost)) is on
whenever billing is on. Its keys sit under `Billing:Reconcile` (environment form
`Billing__Reconcile__<Key>`), have no `.env` variable, and every number must be greater than 0 (the
Inference API refuses to start otherwise):

| Config key | Default | Meaning |
|---|---|---|
| `Billing:Reconcile:Enabled` | `true` | Master switch for the lazy heal, the sweep and the admin resync. No-op while `Billing:Enabled` is false. |
| `Billing:Reconcile:StuckAfterSeconds` | `600` | How long after Paddle's change a missing webhook counts as lost. |
| `Billing:Reconcile:SweepIntervalSeconds` | `300` | How often the sweep runs. |
| `Billing:Reconcile:StartupDelaySeconds` | `60` | Wait before the first sweep after start. |
| `Billing:Reconcile:MaxPerSweep` | `50` | Most organizations one sweep reconciles. |
| `Billing:Reconcile:RecheckAfterMinutes` | `360` | How often the sweep re-reads an overdue renewal or cancellation it already checked and found in order. A check that leaves it due is retried on the next sweep instead. |
| `Billing:Reconcile:CheckoutRecheckMinutes` | `30` | How often an unconfirmed checkout is re-read from Paddle. |
| `Billing:Reconcile:CheckoutWatchHours` | `48` | How long a started plan checkout is watched for its `subscription.created`. |

## The provider boundary

Everything provider specific sits behind `IBillingProvider`
(`ReelBolt.Inference.Api/Services/Billing/IBillingProvider.cs`), so changing provider is one new
class (decision D11). The operations are `CreateCheckoutAsync` (a plan or a top-up, optionally
carrying a discount id; returns the provider customer it used), `CreatePortalSessionAsync` (returns a
`PortalSession` with the overview link and the per-subscription deep links), `VerifyAndParseWebhook`,
`CancelSubscriptionAsync`, `ResumeSubscriptionAsync`, `ChangePlanAsync` (items, a
`PlanChangeTiming` and a `PlanChangeDiscount`; it carries plan changes and seat changes alike, so a
seat decrease cannot be billed differently from a plan downgrade; `PlanChangeDiscount.Unchanged`
leaves the subscription's discount as the provider holds it, `PlanChangeDiscount.Remove` removes it
in the same request), the configured top-up packs, and for promotions
`CreateDiscountAsync`, `UpdateDiscountAsync`, `PreviewPriceAsync` and `CancelTransactionAsync`. A typed provider refusal surfaces as
`BillingProviderException` (an `HttpRequestException` carrying the provider's error code). Two members
were added for Stripe: `ResolveAsync(BillingEvent)`, called after a webhook is verified so a provider can
complete the event with a few reads of its own API (the default returns the event unchanged), and
`RefundTransactionAsync`, which refunds a paid transaction (the default answers "not supported"). Two
providers are registered at once: `Billing:Provider` picks the one new purchases use, and an existing
subscription keeps the provider named on its row; see [Stripe](#stripe). A webhook is verified and parsed into a neutral `BillingEvent` with one of
six kinds: `SubscriptionChanged`, `TopUpPaid`, `PaymentRefunded`, `UpgradePaid` (the paid proration
transaction of a same-period upgrade; see "Cancel and plan changes"), `TransactionCompleted` (a
subscription payment that carried a discount, surfaced only so the promotion's use is recorded) or
`Ignored`. `BillingEventProcessor` only ever sees that neutral shape, never a provider payload.

Provider price ids are data, not code. They live on the plan row in
`plans.provider_price_ids_json` as `{ "paddle": { "monthly": "pri_..." } }` and are environment
specific (sandbox ids differ from live ids), so the plan catalog upsert never touches them. An
operator sets them once per environment after creating the products in Paddle. The summary lists
only the plans the active provider can actually sell, each with the `priceId` its checkout will use;
a plan with no price id for that provider is **omitted from the payload** rather than listed with a
blank one, and `POST /checkout` refuses it with `400 plan_not_purchasable`.

## Endpoints

All routes are on the Inference API under `/api/v1/billing` and 404 when billing is disabled.

| Method | Path | Who | What |
|---|---|---|---|
| `GET` | `/summary` | Any member of the active organization | Current plan, status, seats, renewal date, credit balance, purchasable plans (with their `priceId`), available top-up packs, `canManageBilling`, and the `paddle` block the browser needs for the embedded checkout. `subscription` also carries `cancelAtPeriodEnd`, `cancelEffectiveAt` (null unless a cancel is scheduled) and `scheduledChange` (`{ planKey, planName, seats, effectiveAt }` while a downgrade is held, else null). `credits.grants` lists the grants with credits left and not expired, as `{ source, remaining, expiresAt }`, soonest expiry first and never-expiring last. `subscription.promoDiscount` is the recurring promo discount the subscription still carries, as `{ discountType, amount, currencyCode, targetPriceIds }`, or null: `discountType` is `Percentage` or `Flat`, `amount` is the promotion's Paddle amount string (a percentage, or minor units for flat), `currencyCode` is set for a flat discount and null otherwise. It is read from the local redemption row, not from Paddle; see [Promo discounts across a plan change](metering-and-billing.md#promo-discounts-across-a-plan-change). |
| `POST` | `/checkout` | Owner or Billing role | Body `{ "plan": "creator" }` or `{ "plan": "team", "seats": 5 }` or `{ "topUpCredits": 1000 }`, plus an optional `promoCode`. Returns `{ checkoutUrl, transactionId, discount }`, where `discount` is `{ headline }` or `null`. Grants nothing, but stores the Paddle customer the checkout was created for in `billing_customers` right away, so `hasBillingCustomer` is true and the portal works before the first webhook. |
| `POST` | `/checkout/confirm` | Owner or Billing role | Body `{ "sessionId": "cs_..." }`. Confirms a finished Stripe checkout on the buyer's return so the plan and credits show at once; see [Confirming a checkout on return](#confirming-a-checkout-on-return). Answers `200 { status: confirmed \| pending \| abandoned, kind: subscription \| topUp }`, `404 not_found`, `400 invalid_session`, `409 confirm_unsupported` (a Paddle id), `429 rate_limited`, `503 provider_unavailable`. |
| `POST` | `/checkout/preview` | Owner or Billing role | Body like `/checkout`. Returns `{ currencyCode, subtotal, discount, total, promotion, bonusCredits }`: the money strings are Paddle's `formatted_totals`, verbatim. `promotion` is `{ headline, kind }` or `null`. |
| `POST` | `/promotions/redeem` | Owner or Billing role | Body `{ "code": "WELCOME50" }`. Redeems a credit code. Returns `{ credits, expiresAt, headline }`. |
| `GET` | `/promotions/active` | Anonymous | The active automatic promotions, for the billing page and the marketing site. Never carries a code or an id. |
| `GET`, `POST` | `/admin/promotions` | Platform admin | List, or create a promotion. |
| `GET`, `PATCH` | `/admin/promotions/{id}` | Platform admin | Read, or edit `name`, `publicHeadline`, `status`, `endsAt` or `clearEndsAt`. |
| `GET` | `/admin/promotions/{id}/redemptions` | Platform admin | The newest 500 redemptions. Each item carries `overshoot` (a paid use honoured past a limit), `outcome` (`granted` or `notGranted`) and `notGrantedReason` (`null` for a granted use) and `discountRemovedAt` (when a plan change outside the promotion's targets removed the subscription's discount, else `null`). See [Refused uses](#refused-uses). |
| `GET` | `/admin/orgs?search=` | Platform admin | Up to 50 organizations whose name or owner email contains the term. Never the Platform organization. |
| `GET` | `/admin/users/{userId}/orgs` | Platform admin | The user's organizations, in the same shape. |
| `GET`, `POST` | `/admin/orgs/{orgId}/credit-grants` | Platform admin | List, or grant promo credits by hand. |
| `POST` | `/portal` | Owner or Billing role | Returns `{ url, cancelUrl, updatePaymentMethodUrl }`: a short-lived authenticated customer-portal overview link plus the current subscription's cancel and update-payment-method deep links (each `null` when Paddle returned none). `409 no_billing_customer` before the first purchase. |
| `POST` | `/seats` | Owner or Billing role | Body `{ "seats": 7 }`. Sets the seat count of a per-seat subscription through the same path as change-plan, with the same `202` body. An increase takes effect now (prorated charge, prorated credit grant); a decrease takes effect at period end. `409 not_a_per_seat_subscription` otherwise. See "Cancel and plan changes". |
| `POST` | `/subscription/cancel` | Owner or Billing role | No body. Schedules the cancel at period end. `202 { status: "cancel_requested", effectiveAt }` (the current period end); `200 { status: "cancel_scheduled", effectiveAt }` when one is already scheduled. |
| `POST` | `/subscription/resume` | Owner or Billing role | No body. Removes a scheduled cancel ("Keep my plan"). `202 { status: "resume_requested" }`; `200 { status: "active" }` when nothing is scheduled. |
| `POST` | `/subscription/change-plan` | Owner or Billing role | Body `{ "plan": "pro" }` or `{ "plan": "team", "seats": 5 }`. `202 { direction, effective, effectiveAt, creditGrantMillis, seats, promoDiscountRemoved, message }`: `direction` is `upgrade`, `downgrade` or `keep`; `effective` is `now` or `period_end`; `creditGrantMillis` is the prorated upgrade grant estimated at request time (`0` for a downgrade or keep); `promoDiscountRemoved` is true when the change removed the subscription's promo discount (always false for `keep` and for a seat change on the same plan); `message` says when the change takes effect. See "Cancel and plan changes". |
| `POST` | `/webhooks/{provider}` | Anonymous, signature only | The provider's notification receiver. |
| `GET` | `/admin/reconcile` | Platform admin | Organizations waiting on Paddle: `{ stuckAfterSeconds, items }`, at most 200 items, oldest `waitingSince` first, read from the database only. Each item has `organizationId`, `organizationName`, `planKey`, `status`, `reason` (`change_pending`, `renewal_overdue`, `cancel_overdue`, `checkout_unconfirmed`, `reconcile_failing`), `waitingSince`, `lastCheckedAt`, `lastOutcome`, `lastError`. See "When a Paddle webhook is lost". |
| `POST` | `/admin/organizations/{orgId}/resync` | Platform admin | Optional body `{ "reason": "..." }` (at most 500 characters). Reads the organization's subscription from Paddle and applies it now. `200 { outcome, drift, subscriptionEventsApplied, transactionsApplied }`; `409 change_in_progress`; `502 billing_provider_error`; `404` for an unknown organization. Audited in `billing_admin_actions`. See "When a Paddle webhook is lost". |

Errors shared by seats, cancel, resume and change-plan: `404` when billing is off, `403` for Member
and Admin, `503 billing_not_configured`, `409 billing_provider_refused { providerCode, message }`
when Paddle answers a 4xx other than 401/403, and `502 billing_provider_error` for any other provider
failure. `message` is always our own fixed text for the `providerCode` (the table is
`ProviderRefusalMessages` in `BillingController`: locked by pending changes, processing or renewal;
canceled; not active; past due), with the fallback "Paddle refused this change. Try again later or
open Manage in Paddle."; Paddle's own `detail` is only logged, never returned. Cancel, resume and
change-plan answer `409 no_subscription` when the organization has no live provider subscription
(Trial, canceled, none). Change-plan adds `400 unknown_plan` and `400 plan_not_self_serve` (Trial,
Enterprise, a plan without a list price, or no price id for the provider). Seats and change-plan
both add `400 below_min_seats { minSeats }`, `400 too_many_seats { maxSeats, message }` (above
`Billing:Paddle:MaxSelfServeSeats`), `409 cancel_scheduled` (keep the plan first) and
`409 no_change` (already on that plan and seat count with no downgrade held), `409 change_pending`
(Paddle's live items or billing period differ from the row: the last change, renewal or
reactivation is unconfirmed) and
`409 downgrade_scheduled` (a downgrade is held; only "keep" is allowed).

Checkout rules: send exactly one of `plan` or `topUpCredits` (`400 choose_plan_or_topup`). A plan
must be a purchasable subscription with a price id (`400 plan_not_purchasable`). The Team plan has a
three-seat minimum (`400 below_min_seats`), and seats default to the minimum. Top-ups are for paying
customers only (decision D9): without an active paid subscription the answer is
`403 topup_requires_paid_plan`, and the pack must be one of the configured sizes
(`400 unknown_topup_pack`). An organization that already has a live subscription gets
`409 already_subscribed` ("This workspace already has a subscription. Change plan from the Plan &
billing page instead of starting a second checkout.") instead of a second subscription; plan changes
on a live subscription go through `POST /subscription/change-plan`. Between payment and the first
webhook there is no local subscription yet, so a plan checkout for an organization that already has a
Paddle customer first asks Paddle (`GET /subscriptions?customer_id=…&status=active,trialing,past_due,paused`)
for a live subscription carrying this organization's `custom_data.organization_id` (or none): if one
exists the answer is `409 subscription_pending` ("This workspace's new plan is still being confirmed.
Refresh this page in a minute."), and if Paddle cannot be asked it is `502 billing_provider_error`, so
a second subscription is never started blind. Top-ups never ask. When `POST /customers` answers
`409 customer_already_exists`, checkout looks the customer up by email and reuses it only if its
`custom_data.organization_id` is this organization. A caller in the right organization with the wrong role (Member, Admin) gets
`403`; a caller with no active organization gets `404`. A `promoCode` that does not apply is
refused with `400 promo_code_invalid` and never silently dropped; the rules are in
[Promotions and discounts](#promotions-and-discounts).

## The embedded checkout

The dashboard opens Paddle's **one-page overlay** — `Paddle.Checkout.open({ transactionId, settings:
{ displayMode: 'overlay', variant: 'one-page', successUrl } })` — rather than sending the browser to
Paddle's hosted page. Both are the same transaction and the same webhook; the overlay is a display
choice, not a second payment path.

**The transaction is always created by the server, and the browser is only ever handed its id.**
`Paddle.Checkout.open({ items, customData })` is deliberately never used: a payment is attributed to
an organization through `custom_data.organization_id`, which `BillingEventProcessor` reads to decide
whose credits a payment buys (see "Subscription state machine" above), and a value supplied by the
browser is one the buyer can rewrite — they could direct their credits, or someone else's, at an
organization that is not theirs. `PaddleBillingProvider.CreateCheckoutAsync` attaches
`custom_data.organization_id` (and `user_id`, `kind`, `plan_key`) from the caller's session, and the
client opens the result with `transactionId`. The dashboard has no way to express an organization.

**What the summary gives the browser, and why it is not a `NEXT_PUBLIC_*` value.** `GET
/api/v1/billing/summary` carries a `paddle` block — `{ environment, clientToken }` — next to the
prices. `environment` is already in Paddle.js's own vocabulary (`sandbox` listed as `sandbox`,
`live` as `production`), translated once on the server, so the browser cannot be initialized against
a different Paddle account than the one this API creates transactions in. The token is the
**client-side, publishable** one (`test_...`/`live_...`, `Billing__Paddle__ClientToken`): Paddle.js
is designed to run with it and it grants nothing on its own. It is served at runtime rather than
baked into the web image because the web app is built in Docker — a build-time value could not be
rotated without a rebuild, and it would be free to disagree with the server's `PADDLE_ENVIRONMENT`.
The block is `null` when the configured provider is not Paddle; `clientToken` is omitted when the
operator has not set one, which is the deployment's answer to "is there an embedded checkout?".

**Prices are Paddle's.** The page asks `Paddle.PricePreview({ items })` for the price ids the summary
served and prints the `formattedTotals` strings unchanged — no conversion, no rounding, no currency
symbol of this platform's own, and no arithmetic on the frontend at all. When the preview is
unavailable (no token, no Paddle.js, a refused call) the card falls back to the catalogue's own
`$` list price and *says* that is what it is. A number this app computed would be a second, worse
price next to the one Paddle actually charges.

**Prefill.** The signed-in address is passed as `customer: { email }` — `customer` is a top-level
member of `CheckoutOpenOptions` in `@paddle/paddle-js` 1.6.5 (there is no `settings.customer`). It is
belt-and-braces: the server also attaches `customer_id`, creating the Paddle customer from the same
session address when the organization has never bought (see the commit note on
`ResolveCustomerIdAsync`).

**Afterwards.** `settings.successUrl` is the app's own `/app/checkout` page, absolute and built from
the origin the dashboard is served on. Paddle returns the buyer there with `_ptxn`; that page claims
nothing about the payment, because only the webhook knows. Nothing in this flow grants credits.

The client half is `web/lib/paddle/` (`sdk.ts` for the calls, `pricing.ts` for the pure mappings) and
`web/lib/hooks/use-paddle-prices.ts` for the display prices.
## Webhooks are authoritative

A checkout redirect, a success page or any client call never grants credits or changes a
subscription. Only a verified webhook does. The browser can send the buyer back to a "thank you"
page, but the balance changes when Paddle's notification is processed.

> **Where the Paddle notification destination lands.** The destination points at
> `https://reelbolt.ai/api/v1/billing/webhooks/paddle`. `reelbolt.ai` is proxied through Cloudflare to
> the control box's nginx, which serves the whole stack (the marketing site is the `site` container
> there, not Cloudflare Pages), so the path reaches the receiver. Probed 2026-11-24: an unsigned `POST`
> answers **400**, the receiver rejecting the body.
>
> **A green "destination created" in the Paddle dashboard means the endpoint exists in Paddle — it does
> not mean deliveries are landing.** A paying customer's subscription never activates if the path stops
> reaching the origin.
>
> **The pass test is 400 from the origin, not "any 4xx".** **405 or 404 means the request did not reach
> the receiver**, and a check that accepts any 4xx passes on the very failure it exists to catch. This
> is the production-routing check (an operator checklist); it gates the operator checklist rows A5
> and A7.

The receiver (`POST /api/v1/billing/webhooks/paddle`) is `[AllowAnonymous]`: the signature over the
exact raw body is its only credential. It reads the body as raw bytes before any JSON parsing,
because the signature covers the exact bytes. Responses: `400` for a bad, missing or stale
signature or a malformed body (nothing is stored); `404` when billing is disabled or the provider is
unknown; `200` for an applied, duplicate, stale or unresolved event; `500` when processing throws,
so the provider retries. A body over 1 MB is rejected before it is read.

nginx routes the path with its own `location ~* ^/api/v1/billing/webhooks/paddle(/|$)` in
`nginx/locations.conf` (a case-insensitive regex that also takes a trailing slash, so no spelling
of the path the API accepts skips the block or its optional source-IP allowlist). That block does not use the cookie-to-`Authorization` translation (there is
no session on a server-to-server call), blanks any client-supplied `Authorization` header, and caps
the body at 1 MB.

## Exactly-once processing

`BillingEventProcessor.ProcessAsync` inserts a `billing_webhook_events` row, applies the effect and
marks the row processed inside one database transaction. The table has a unique index on
`(provider, event_id)`, so a redelivery finds the row and is a no-op (outcome `Duplicate`). A crash
or exception rolls the whole transaction back, including the event row, so the provider's retry
runs the event again from scratch. Two concurrent deliveries of the same event are resolved by the
unique index: the loser rolls back and reports `Duplicate`.

Credit effects carry their own ledger idempotency keys as a second line of defence, so a
differently numbered event that describes the same payment or period cannot grant twice:

| Effect | Ledger key |
|---|---|
| Plan refill | `plan:{provider}:{subscriptionId}:{periodStart UTC}` |
| Upgrade difference | `plan-upgrade:{provider}:{transactionId}` (the paid proration transaction) |
| Top-up grant | `topup:{provider}:{transactionId}` |
| Refund clawback | `clawback:{provider}:{transactionId}` |
| Pack bonus (a `Promo` grant) | `topup-bonus:{provider}:{transactionId}` |
| Pack bonus clawback on a full top-up refund | `clawback-bonus:{provider}:{transactionId}` |

The clawback keys name the transaction, not the refund adjustment, so a refund followed by a
chargeback of the same payment claws back once.

The promotion use a webhook records (`DiscountApplied`, `BonusGranted`) is also idempotent: a
second row for the same promotion and transaction is never written (a unique index on
`(promotion_id, provider_transaction_id)`). A subscription's discount is recorded once per
promotion and subscription (a unique index on `(promotion_id, provider_subscription_id)`), so the
renewals a recurring discount also reduces neither add rows nor count against the limits again.

## The money we actually took

`billing_payments` records cash received: one row per provider transaction, the amount the provider
actually took. Its grain is `(provider, provider_transaction_id)` — one transaction, one row — so
"how much money did we take" is a plain SQL question. Before this table the paid amount existed only
inside the raw webhook payload (`billing_webhook_events.payload_json`, a `jsonb`), where no query
could reach it.

| Column | Holds |
|---|---|
| `provider`, `provider_transaction_id` | The provider and its transaction id. Together they are the unique key. |
| `provider_subscription_id` | The subscription the transaction paid for; null for a stand-alone top-up. |
| `organization_id` | The organization our own rows attribute the payment to; null when none corroborates. No foreign key to `organizations`. |
| `amount_minor` | What the provider charged, in integer minor units of `currency_code`. |
| `currency_code` | ISO-4217 code as the provider sent it; null when it sent none. |
| `fee_minor` | The provider's own fee, in minor units; null when it reported none. Never 0 for "unknown". |
| `occurred_at` | When the provider says the payment happened, not when the webhook arrived. |
| `created_at` | When the row was written. |

**Amounts are integer minor units exactly as the provider sent them.** Paddle sends `"1450"`, and
that is what `amount_minor` holds — $14.50 for a two-decimal currency. Nothing in the database
converts it: the conversion to a display currency happens at the edge, in the code that reads the
row, and never here. A zero-decimal currency such as JPY is stored the same way, untouched.

**What writes it.** `BillingEventProcessor` records the payment for every completed transaction — a
top-up, an upgrade and an ordinary subscription payment alike — inside the same database transaction
that writes the `billing_webhook_events` row, and before anything touches credits. A completed
transaction whose amount the provider did not state is **not** recorded, so a row never claims an
amount the provider never gave. The money is written even when the processor cannot act on the event,
for the reason the backfill's `Unresolved` note below gives. The webhook pipeline is in
[metering-and-billing.md](metering-and-billing.md#the-webhook-pipeline-and-its-idempotency-keys).

**A replay cannot double-count.** Two structural defences. The processor holds the transaction's
advisory lock while it works, so two deliveries of the same payment serialise; and the table's unique
key on `(provider, provider_transaction_id)` rejects the second row outright. A redelivery of the
same event, or a differently-numbered event describing the same payment, therefore leaves exactly one
row.

**Attribution is corroborated against our own rows, never taken from the payload's word.** The
organization is resolved in this order: the matching `subscriptions` row (`provider` plus the
payload's `data.subscription_id`); else the payload's `custom_data.organization_id` claim **only
when an `organizations` row with that id exists**; else the matching `billing_customers` row; else
null. The payload can name an organization that does not exist, or contradict our own records, so it
is never trusted alone — where a `subscriptions` row disagrees with the payload, the subscription
wins. A payment whose owner cannot be resolved is still recorded, unattributed (`organization_id`
null): money is never dropped for want of a name. For per-organization revenue on those rows, join
`provider_subscription_id` to `subscriptions`.

### The backfill, and what it cannot cover

The migration backfills from the retained payloads: every `transaction.completed` payload still in
`billing_webhook_events` becomes a `billing_payments` row. It is idempotent — `ON CONFLICT DO
NOTHING` against the unique key — so it is safe to run again, and it never overwrites a row the
processor already wrote. The statement is the `Backfill` constant in
`inference/src/ReelBolt.Inference.Api/Migrations/BillingPaymentsMigrationSql.cs`; to re-run it by
hand, execute that SQL against the database with `psql`.

It states its own limits, and they are real:

- Only what is still stored. A transaction whose payload was pruned, or whose `transaction.completed`
  event never arrived, is not covered.
- A transaction described only by a `transaction.paid` event, with no matching `completed`, is not
  covered.
- A row the payload did not attribute (no matching `subscriptions`, no existing organization in the
  payload's `custom_data`, no matching `billing_customers`) stays unattributed; join
  `provider_subscription_id` to `subscriptions` for per-organization revenue on those.
- A payload with no parseable amount, or one whose promoted values do not fit their columns, is
  skipped whole rather than recorded as 0.

It reports its own totals and every skip as a `WARNING` in the migration's output — how many rows it
promoted, how many it skipped, how many had no parseable amount, and how many promoted rows came from
events the processor recorded but could not act on. It deliberately does **not** exclude those
`outcome = 'Unresolved'` events: Paddle captured that money whether or not we could attribute or
apply it, so dropping them would hide real revenue. It counts them in its closing warning instead.

### No card detail is stored

The payload contains `method_details.card` — card type, last 4, expiry and cardholder name — and
`billing_payments` promotes **none** of it. There is no customer id and no address either. Only the
amount, its currency and the provider's fee are stored, and the table has no foreign key to
`organizations`, so the revenue trail outlives an account purge, for the same reason
`billing_webhook_events` has none.

Because it carries no personal data, `billing_payments` is deliberately absent from the "Tables
holding personal data" map in
[`design/saas-accounts.md`](design/saas-accounts.md#tables-holding-personal-data) and from the
account export list (`AccountExportSections`). Adding it to that map without adding it to the export
fails `AccountExportSectionsTests.Exported_tables_are_exactly_the_C0_list`, and it belongs in
neither: there is nothing personal in it to export or erase. Do not "fix" the omission.

### The admin revenue overview

The admin revenue overview (`GET /api/v1/billing/admin/overview`) now reports
`revenue.topUpUsdRecorded` as `true`. It is true because it is now true: the amount a top-up took is
stored in `billing_payments`, so the claim is verifiable.

Revenue is what was charged, never the plan's list price:

- `revenue.collected[]` is one row per currency (never summed across currencies) of the payments and
  refunds whose `occurred_at` falls in the window: gross (`grand_total`), discount, tax, fee where
  known, refunds and the net excluding tax and refunds. A 100%-discounted payment is a payment of 0.
- `billing_payments` also keeps Paddle's `details.totals` `subtotal`, `discount`, `tax`, `total` and
  `earnings` (null = the provider did not send it, never 0). Approved refunds and chargebacks are
  recorded one row per provider adjustment in `billing_refunds` (no personal data, no FK). Both were
  backfilled from the retained webhook payloads by the `AddBillingRevenueDiscount` migration.
- `revenue.committedMonthlyUsd` is the list price times the charged fraction
  `(subtotal - discount) / subtotal` of each subscription's *latest* recorded payment (a once-only
  discount therefore stops counting at the first full-price renewal; a payment without totals falls
  back to 0 when it charged 0, else full price). A subscription with no recorded payment counts at
  list price and is reported in `revenue.subscriptionsWithoutPayment`. The old pre-discount figure is
  kept as `revenue.listPriceMonthlyUsd`. Internal grants (no provider) stay excluded.
- Stripe: every `invoice.paid` for a subscription invoice (create, cycle, update) writes a
  `billing_payments` row, including `amount_paid` 0 (a 100%-off coupon or promotion code). A zero
  payment records the revenue fact but never emits the first-payment funnel row, and credit,
  promotion-redemption and upgrade behaviour is unchanged. The `BackfillStripeRevenue` migration fills
  totals, inserts the never-recorded zero invoices and promotes Stripe refunds and lost disputes
  (tax null) from the retained `billing_webhook_events`.
- Only the active billing provider (`Billing:Provider`) is revenue: `revenue.activeProvider` names it, and
  payments, paying subscriptions and plan margins of any other provider are reported under
  `revenue.excluded[]` (never summed in). With no active provider every provider counts, as before.
- A Stripe payment whose `invoice.paid` arrived before `customer.subscription.created` and named no
  organization is attributed when the subscription row lands, and then emits the first-payment funnel row.
- Plan margin rows commit only paying provider subscriptions; internal grants and trials still count as
  organizations and carry cost.

## When a Paddle webhook is lost

Paddle retries a webhook that does not get a `200`, but a delivery can still be lost for good. Until
it arrives, the `subscriptions` row keeps the old state while Paddle already bills the new one. The
Inference API detects this and heals it from Paddle's live state. The code is in
`ReelBolt.Inference.Api/Services/Billing/Reconcile/`.

### Symptoms

What the customer sees depends on which webhook was lost:

- **A plan or seat change (upgrade or downgrade).** Paddle's items differ from the row, so the next
  plan, seat or cancel request (or resume, while a cancellation is scheduled) answers
  `409 change_pending` ("Your last change is still being
  confirmed. Refresh this page in a minute, then try again.") until it is healed (see
  [What the customer sees](#what-the-customer-sees-and-how-long-it-takes)).
- **A cancel.** The plan page shows no scheduled cancellation, although Paddle will end the
  subscription at the period end. Cancelling again answers `200 cancel_scheduled` (nothing is sent
  twice).
- **A resume ("Keep my plan" on a scheduled cancellation).** The plan page still shows the plan as set
  to cancel. Resuming again answers `202 resume_requested` with nothing sent.
- **A "keep" of a held downgrade.** The row still holds the downgrade, so the plan page still shows the
  scheduled lower plan, and every plan or seat change answers `409 downgrade_scheduled` ("Keep your
  current plan first, then change it."). Choosing "keep" again answers `202` and sends the same items
  to Paddle again. Paddle's items already match the row, so this is never `409 change_pending`.
- **A checkout.** A paid checkout does not show the new plan, and a second checkout answers
  `409 subscription_pending` ("This workspace's new plan is still being confirmed. Refresh this page
  in a minute.").
- **A renewal or a cancellation taking effect.** The billing period never moves, or a cancellation
  past its effective date is still not `Canceled`.

While Paddle cannot be read, a change request answers `502 billing_provider_error` and nothing is
sent: every change reads Paddle's live subscription first.

### How the self-heal works

`BillingReconciler` is a webhook substitute, not a second write path:

1. It reads the organization's subscription from Paddle (`GET /subscriptions/{id}`; with no local
   row, the customer's live subscription) and compares it with the row. The drift fields are
   `plan`, `seats`, `scheduled_change`, `period`, `status`, `cancel_at_period_end`, or
   `subscription_missing` when Paddle has a subscription the platform has no row for. A held
   downgrade that Paddle already bills (Paddle's items are the held plan and seats) is in sync.
   `scheduled_change` means the row holds a downgrade but Paddle's items are the row's own plan and
   seats again: a "keep" whose webhook was lost. Feeding that state to the processor releases the
   hold, exactly as the keep webhook would. `scheduled_change` never makes a change request answer
   `409 change_pending`. An organization with no subscription row and no `billing_customers` row
   cannot be looked up at Paddle: the outcome is `NothingToReconcile` with the error `no_customer`.
2. A difference counts as a lost webhook only once Paddle's `updated_at` is older than
   `Billing:Reconcile:StuckAfterSeconds` (default 600 s). A younger difference is left to its webhook
   (outcome `TooRecent`). A Paddle subscription returned without `updated_at` is not reconciled at all:
   the outcome is `ProviderUnavailable` with the error `provider returned no updated_at`, and nothing
   is applied. Stripe is the exception: its subscriptions have no `updated_at`, and the reconciler
   accepts that for a provider that says so (see [Reconcile (Stripe)](#reconcile-stripe)).
3. It reads the subscription's completed upgrade (proration) transactions since the period start
   minus one day. Only a transaction whose own `status` is `completed` and whose `subscription_id`
   is that subscription is used, whatever the listing returned; any other one (a `billed` or
   `past_due` one, another subscription's) is skipped and logged as a warning with its id and status.
4. Every Paddle read happens before any write. Any read failure (network, timeout, 5xx, any 4xx
   including 404, a body that cannot be parsed, a `2xx` list whose `data` is missing or not an array)
   ends as `ProviderUnavailable` and applies nothing: it fails closed. A plan checkout whose listing of
   the customer's subscriptions comes back that way answers `502 billing_provider_error`.
5. It feeds the subscription, as a synthetic `subscription.updated`, and each upgrade payment not
   already recorded, as a synthetic `transaction.completed`, into the same
   `BillingEventProcessor.ProcessAsync` a webhook uses. Idempotency, out-of-order protection and
   upgrade pairing are the webhook's own. The synthetic event ids are described in
   [metering-and-billing.md](metering-and-billing.md#the-webhook-pipeline-and-its-idempotency-keys).
6. It records the check on the organization's `billing_reconcile_watches` row, its only own write.
   It clears only the change it started from: a change or checkout recorded while it ran stays due.
   A change recorded while an older one is still due keeps the older `change_requested_at` (the kind
   and the member are the newer request's), so repeating a request never pushes the stuck threshold
   back.

The reconciler never grants credits by itself. A credit it causes is the one the lost webhook would
have granted, under the same ledger key (see [Exactly-once processing](#exactly-once-processing)).
An upgrade's credit difference is granted only when the paid proration transaction is found and
pairs with the upgrade; an upgrade with no paid transaction at Paddle grants nothing. When the
processor does not apply the subscription event (stale, duplicate or unresolved), the outcome is
`Stale` and the log says "Review manually".

It runs on three triggers:

- **Lazy.** A seat, change-plan, cancel or resume request that would answer `409 change_pending`
  because Paddle's live state differs from the row reconciles once, under the same per-organization
  change claim. If that healed the row, the change runs again in the same request; any other outcome
  keeps the `409`. A plan checkout that would answer `409 subscription_pending` does the same and,
  when healed, answers `409 already_subscribed`. A cancel that Paddle already has scheduled
  (`200 cancel_scheduled`) records a `cancel` watch. That cancel, and a resume that Paddle already
  applied (`202`, nothing sent), also reconcile once under the claim. The answer stays the same
  whatever the outcome.
- **Sweep.** `BillingReconcileSweeper` runs its first pass `StartupDelaySeconds` (60) after start.
  After each pass ends it waits `SweepIntervalSeconds` (300) before the next, and each pass
  reconciles at most `MaxPerSweep` (50) organizations: those whose
  last check did not fail first, then oldest first. Checkout watches are selected by their own query
  and take at most a quarter of `MaxPerSweep` (at least one) per sweep. However many abandoned
  checkouts there are, they never crowd out other changes. A watch is opened when Paddle accepts a change (kinds `cancel`, `resume`,
  `change_plan`, `seats`, `keep`) or a plan checkout is created (kind `checkout`). A change still due
  after a `TooRecent` check is retried on the next sweep. A failing one (`ProviderUnavailable`,
  `Stale`) backs off: it is retried `SweepIntervalSeconds` × 2^(failures − 1) after its last check,
  at most 1 hour (with the defaults 5, 10, 20, 40 minutes, then hourly), so organizations whose
  reconcile keeps failing cannot take every slot. A new change recorded after the failed check is due
  again at once. A checkout whose last check found no live subscription at Paddle is rechecked every
  `CheckoutRecheckMinutes` (30) for `CheckoutWatchHours` (48). A checkout whose last check was
  `TooRecent`, `ProviderUnavailable` or `Stale` is retried like a change: next sweep, or with the
  failure backoff. A renewal whose
  period ended, or a scheduled cancellation past its date, more than `StuckAfterSeconds` ago, with no
  change of that organization due, is checked by the next sweep; when that check finds it in order it
  is rechecked every `RecheckAfterMinutes` (360), and when it leaves it due it is retried like a
  change. Each organization takes its change claim, so a second replica gets `Busy`
  and applies nothing.
- **Admin.** A platform admin's resync (below) applies Paddle's state at once, without waiting for
  the threshold.

All keys and defaults are in [Switching billing on](#switching-billing-on).

### What the customer sees and how long it takes

For the first `StuckAfterSeconds` (10 minutes) after Paddle's change the customer sees the symptom
listed above (`409 change_pending`, `409 subscription_pending`, a stale cancel banner, or
`409 downgrade_scheduled` after a lost keep). The webhook may only be late. After that, the customer's
next change request that would be `409 change_pending` heals the row and runs the change in the same
request; so does a repeated cancel or resume. Without such a request, the next sweep heals it. A lost
keep is healed only by the sweep: its `keep` watch is due from the first keep. With the defaults a lost
change webhook is healed about 10 to 15 minutes after Paddle's change, and the plan page shows the new
state on its next load.
An unconfirmed checkout is re-read every 30 minutes for 48 hours after it started, so a lost
`subscription.created` is healed by the first of those checks after the threshold. An abandoned
checkout (its last check asked Paddle and found no live subscription) is still rechecked on that cadence but is
not counted as stuck, logs no `billing.change_stuck`, is not listed in "Waiting on Paddle" and does not
raise the daily report's `billing_webhooks` alert. A checkout of an organization with no
`billing_customers` row (outcome `NothingToReconcile`, error `no_customer`) is not known to be
abandoned and still counts as stuck. An overdue
renewal or cancellation is healed by the first sweep after it becomes overdue; one that Paddle
confirms is in order is looked at again every 6 hours. While Paddle cannot be reached, nothing
changes: every change request answers `502 billing_provider_error` with nothing sent, and the sweep
retries after 5, 10, 20 and 40 minutes, then every hour. A heal after an outage can therefore take up
to an hour longer. Once Paddle answers, a customer's own change request still reconciles at once.

### How to see it

- **Metrics:** the meter `ReelBolt.Inference.Billing.Webhooks`. Instruments and suggested alerts are
  in [observability.md](observability.md#billing-webhooks-and-reconcile).
- **Logs:** EventIds `7101 billing.reconcile_healed`, `7102 billing.reconcile_failed`,
  `7103 billing.change_stuck`, `7104 billing.webhook_signature_failed`,
  `7105 billing.webhook_processing_failed`, `7106 billing.webhook_unresolved`,
  `7107 billing.webhook_rejected` (a correctly signed webhook whose `event_id` starts with the
  reserved `reconcile:` or `confirm:` prefix: answered `400 invalid_webhook`, never processed, not
  counted as a signature failure), `7108 billing.webhook_failures_alert` (Warning: the sweep pushed a
  signature/processing-failure alert, see [Alerts](#alerts)), `7109 billing.drift_detected` (Warning:
  the sweep left residual Paddle-vs-ledger drift, see
  [the reconciliation drift report](#the-reconciliation-drift-report-and-runbook)) and
  `7110 billing.usage_unattributed` (Warning: the ledger holds `usage_events` with no attributable
  organization or user).
- **Daily report:** with `Billing:SpendReport:Enabled` on, every report logs
  `Billing webhooks over {Hours}h: {Received} received, {Unresolved} unresolved, {Reconciled} reconciled, {Stuck} waiting past {StuckAfterMinutes} min, {Failing} failing to reconcile; {SignatureFailures} signature failures, {ProcessingFailures} processing failures since the last report (this instance)`.
  `Reconciled` counts the synthetic events that ended `Applied` (lost webhooks healed). The two
  failure counts are this API instance's own, since its previous report or its start. They are kept in
  memory, not in the database, so with several replicas each reports its own. See
  [the daily spend report](platform-kill-switches.md#the-daily-spend-report).
- **Admin panel:** `/app/admin/billing`, tab "Waiting on Paddle". It lists organizations whose
  change, renewal, cancellation or checkout is past the threshold, or whose reconcile keeps
  failing, with the reason, the last check, its outcome and error. It reads the database only
  (`GET /api/v1/billing/admin/reconcile`) and never calls Paddle.
- **Database:** `billing_reconcile_watches` (one row per organization: `change_kind`,
  `change_requested_at`, `last_checked_at`, `last_outcome`, `last_error`, `consecutive_failures`)
  and `billing_webhook_events.outcome` (`Applied`, `Duplicate`, `Stale`, `Unresolved`).

### How to resync

In the admin panel, use "Resync from Paddle" on a row, or "Resync a workspace by ID" for an
organization that is not listed. The confirmation takes an optional reason. The API call is:

```http
POST /api/v1/billing/admin/organizations/{orgId}/resync
Content-Type: application/json

{ "reason": "Support ticket 1234: plan change not showing" }
```

It needs a platform admin. The answer is `200 { outcome, drift, subscriptionEventsApplied,
transactionsApplied }`, with `outcome` one of `Healed`, `InSync`, `NothingToReconcile` or `Stale`;
`409 change_in_progress` when another request holds the organization's change claim (retry);
`502 billing_provider_error` when Paddle cannot be read (nothing applied). Every call that reaches
the reconciler writes a `billing_admin_actions` row (`action = 'resync'`, the actor, the reason, the
outcome and a `detail_json` with the drift and counts), whatever its outcome, including errors. The
row is written even when the admin's browser goes away mid-request: outcome `Cancelled` when the
run itself was cancelled.

### Replaying a webhook from the Paddle dashboard

In the Paddle dashboard, open Developer tools → Notifications, open the event, and choose Replay
(check the current Paddle dashboard labels). The replay carries the original `event_id`. If that
event never reached the platform, it is processed normally. If it was already processed, it is a
`Duplicate`. If the reconciler already healed the subscription, the replay is a no-op: the row's
last event time is already Paddle's `updated_at`, so the real event ends `Stale` or applies the same
state again, and the ledger keys stop any second grant.

### Top-ups are not reconciled

A top-up creates no subscription, so its checkout opens no watch, and the reconciler reads only the
subscription and its upgrade payments. A lost top-up `transaction.completed` is never healed
automatically. Find the transaction's `transaction.completed` event in Paddle's notifications and
replay it as above; the `topup:{provider}:{transactionId}` ledger key keeps a repeat from granting
twice.

### Alerts

- Daily report alert, scope `billing_webhooks`, raised when any change is waiting past the
  threshold, any webhook of the window is unresolved, any reconcile keeps failing, or this instance
  rejected a webhook's signature or failed to process one since its previous report. It is POSTed
  to `Billing:SpendReport:AlertWebhookUrl` when one is set.
- Sweep alert, scope `billing_webhooks`. After a sweep, if this instance's webhook signature failures
  or processing failures grew since its previous alert (or since it started), the sweep sends one
  alert. It sends at most one per hour, and its `detail` gives the counts since the previous alert.
  It goes through the same sender as the daily report, so it is POSTed to
  `Billing:SpendReport:AlertWebhookUrl` when one is set, even with the daily report itself off. It is
  logged as a Warning with EventId `7108 billing.webhook_failures_alert`. It runs only while the sweep
  runs (`Billing:Enabled` and `Billing:Reconcile:Enabled`). Failures that grow within the hour go into
  the next alert.
- Sweep alert, scope `billing_drift` (B12). After a sweep, when the comparison left residual drift or
  the ledger holds unattributed `usage_events`, the sweep sends one alert with the drift classes and
  the unattributed count in its `detail` (see
  [the reconciliation drift report](#the-reconciliation-drift-report-and-runbook)). Like the alert
  above it goes through the same sender (so it reaches `Billing:SpendReport:AlertWebhookUrl` when one
  is set), is logged as a Warning with EventId `7109 billing.drift_detected`, and is sent at most once
  an hour per process. The unattributed count alone is logged as `7110 billing.usage_unattributed`.
- Metric alerts, set up as in [observability.md](observability.md#billing-webhooks-and-reconcile):
  signature failures above 0 over 15 minutes, any processing failure, `stuck_watches` above 0 for
  30 minutes, `unresolved_24h` above 0, and reconcile runs with outcome `providerunavailable` or
  `stale`.

## Subscription state machine

A subscription event carries the provider's status, billing period, items and any scheduled
cancellation. The processor resolves the organization from the `organization_id` the platform put
into the checkout's custom data, falling back to an existing subscription row and then the stored
billing customer. An event that names no known organization is stored with a note and
acknowledged, not retried forever.

- **Ordering.** Providers do not deliver in order. Every applied change stamps
  `subscriptions.last_event_at` with the event's `occurred_at`; an event not newer than that is
  recorded as `Stale` and not applied. A late `active` update therefore never resurrects a
  subscription that a newer event canceled.
- **Status.** The provider status maps to `Trialing`, `Active`, `PastDue`, `Paused` or `Canceled`.
  A scheduled cancel sets `CancelAtPeriodEnd` and `CancelEffectiveAt` (Paddle's
  `scheduled_change.effective_at`, else the period end) and leaves the subscription active until it
  ends; an event without a scheduled cancel clears both.
- **Plan and seats.** The plan is found by matching the subscription item price id against
  `plans.provider_price_ids_json`. A per-seat plan takes its seat count from the item quantity and
  never goes below the plan minimum (Team: 3). Inside the same period an upgrade applies at once and
  a downgrade is held; see "Cancel and plan changes".
- **New subscription.** A first paid subscription replaces any other non-canceled subscription of
  the organization (the internal Trial row).
- **Refill.** An `Active` or `Trialing` subscription with a known period start grants the plan's
  monthly credits (times seats for a per-seat plan), expiring at the period end. Because the key
  includes the period start, the created, updated and renewal events for one period grant once, and a
  renewal (a new period start) grants again. `PastDue`, `Paused` and `Canceled` grant nothing, and
  credits already granted stay until their own expiry. A same-period upgrade (plan or seats) grants
  only the difference prorated over the rest of the period, and only once its proration payment has
  completed (see "Cancel and plan changes"); a downgrade grants nothing until the next refill. The
  refill is always the entitlement the period **started** with: when the first `Active` event of a
  period is itself an upgrade (its renewal or reactivation webhook lagged, or the period was past due
  until then), the refill is granted at the pre-upgrade plan first and the upgrade's paid difference
  is measured from that refill, so a period never totals more than
  `refill + prorated(new − refill)`.
- **Top-up.** A completed stand-alone payment for a configured top-up price grants the pack's
  credits as a `TopUp` bucket expiring after 12 months. The pack size comes from the configured
  price id that was actually paid, never from custom data.
- **Refund.** An approved refund adjustment of a top-up transaction claws the credits back
  (a negative ledger adjustment, so the balance may go negative if they were already spent). An
  approved full refund **or chargeback** (Paddle adjustment action `refund` or `chargeback`) of an
  upgrade's proration transaction claws back the unspent part of that upgrade's grant:
  `min(grant remaining, amount granted)`, once per transaction, under the same `refund:` key. A
  full refund or chargeback of a transaction the processor has **not seen yet** (its
  `transaction.completed` is still on its way) is remembered as a tombstone: a
  `billing_pending_upgrade_grants` row with only the transaction id and `clawed_back_at` set (0
  taken). When that top-up or upgrade payment arrives later it grants nothing (the webhook row's note
  says it was refunded first) and the ledger is untouched; another adjustment for the same
  transaction is a no-op. The tombstone needs the organization (from the subscription, custom data or
  the stored customer); a refund that resolves none is recorded `Unresolved` with a warning for a
  human. A tombstone for a refund that names a known subscription may be of a renewal or initial
  subscription payment (never clawed back automatically), so its webhook row's note also says
  `review manually (subscription payment?)` and a `Warning` is logged. When the refunded upgrade
  payment finally arrives, its upgrade is retired if it is the one candidate (see "Pairing is on
  the charged items"). A partial refund (of a top-up or an upgrade payment), or a refund of any other
  subscription payment, is recorded with a note on the webhook row and needs a human decision. A
  partial refund of an upgrade payment that has **not granted yet** (waiting for `Active`, or not
  paired yet) also closes that row with 0 and logs a `Warning`, so a later `Active` or pairing never
  grants the full difference for a payment that was partly returned (Security G-2); a human can
  grant by hand. A partial refund of a transaction not seen yet is still only noted: its late payment
  pairs and grants normally (accepted: no tombstone is kept for a partial amount). A
  top-up payment and its refund are serialised per transaction (see "Serialised per subscription").
  A full top-up refund also claws back that top-up's pack bonus, if one was granted. A second refund
  or chargeback of the same transaction changes nothing.
- **Promotions.** A top-up whose server-recorded checkout reservation carried a pack bonus gets the
  bonus as a `Promo` grant, and any payment whose `discount_id` belongs to a promotion gets a
  `DiscountApplied` row. See [Promotions and discounts](#promotions-and-discounts).

## Cancel and plan changes

The dashboard cancels, resumes and changes plan through the three `/subscription/*` endpoints, and
changes seats through `POST /seats` (`BillingController`). Seats and change-plan share one code path
(`RequestChangeAsync`), so a seat change is a plan change with the same plan. Each one only asks
Paddle and answers `202`: **webhooks stay authoritative**,
and the `subscriptions` row changes only when Paddle's `subscription.updated` (or
`subscription.canceled`) is processed. The dashboard polls the summary until the change shows.
All four take a per-organization claim (`IBillingChangeLock`: a session `pg_try_advisory_lock` on
Postgres) from before the live Paddle read until the whole change returns. That includes a lazy heal
and the rerun it triggers, under the same claim. Cancel and resume lazy-heal under the held claim
too. The claim never waits for another request on the same organization: a second change for that
organization while one is in flight answers `409 change_pending`.

The claim also needs one of the replica's lock connections (`Billing:ChangeLock:MaxConnections`,
default 4). When no connection frees within `Billing:ChangeLock:SlotWaitMilliseconds` (default
2000), the change answers `503 billing_busy` with `Retry-After: 5` and the body
`{"error":"billing_busy","message":"Billing is busy right now. Try again in a few seconds."}`. Nothing
is sent to Paddle in that case, so the client can retry the same request after the delay. See
[database connections](database-connections.md#connection-budget-arithmetic) for how the cap is
counted.
Under that claim each of them first reads Paddle's live subscription and refuses with
`409 change_pending` while Paddle's items differ from the row (a plan or seat change whose webhook
has not landed), unless the difference is a held downgrade, and while Paddle's live
`current_billing_period` (start or end) differs from the row's (a renewal or reactivation whose
webhook has not landed: the row's plan, seats and refill are still last period's). So one change is
in flight per subscription until its webhook lands, including a cancel or resume sent right after a
plan change.
A cancel Paddle already has, or a resume it already applied, is answered without a second call. No
idempotency key is sent to Paddle; the claim and the live-state check are the guard against a
double submit.

**Cancel at period end.** `POST /subscription/cancel` calls Paddle
`POST /subscriptions/{id}/cancel` with `effective_from: next_billing_period`, a scheduled and
undoable cancel. The webhook sets `cancel_at_period_end` and `cancel_effective_at`; the subscription
stays `Active` with its plan and credits until then. When the period ends Paddle sends
`subscription.canceled`, the row becomes `Canceled`, and the summary and entitlements fall back to
the Trial plan (`Billing:TrialPlanKey`). Credits already granted stay until their own expiry.

**Resume ("Keep my plan" for a cancel).** `POST /subscription/resume` calls Paddle
`PATCH /subscriptions/{id}` with an explicit `"scheduled_change": null`, which removes the scheduled
cancel. The next webhook carries no scheduled cancel and clears both columns.

**Direction.** `BillingEventProcessor.IsDowngrade` compares `PriceMonthlyUsd × seats` (seats count
only on a per-seat plan). A lower total is a downgrade; the same or a higher total is an upgrade. The
endpoint picks Paddle's billing mode with it and the webhook classifies the resulting update with
it, so the two cannot disagree. A plan or seat change while a cancel is scheduled is refused with
`409 cancel_scheduled`: resume first. A seat count above `Billing:Paddle:MaxSelfServeSeats`
(default `200`) is refused with `400 too_many_seats`. Before deciding, seats and change-plan read
Paddle's live subscription (`GET /subscriptions/{id}`; if Paddle cannot answer, `502
billing_provider_error` and nothing is sent): a scheduled cancel there is `409 cancel_scheduled`, and
while its items do not match the row's plan and seats with no downgrade held, the last change's
webhook has not landed and the request is `409 change_pending` ("Your last change is still being
confirmed. Refresh this page in a minute, then try again."), so the direction is always judged
against what Paddle actually bills. While a downgrade is held, anything but "keep" is
`409 downgrade_scheduled` ("Keep your current plan first, then change it.").

**Upgrade: now, prorated charge, prorated credit difference once paid.** The endpoint sends the new
items with `proration_billing_mode: prorated_immediately`, so Paddle charges the prorated difference
now, in a transaction with `origin: subscription_update`. When the subscription webhook arrives in the
**same period** (`PeriodStart` unchanged), or is the first event of a **new** period and already
shows a higher price than the row (the renewal webhook lagged), the processor applies the new plan
and seats at once but grants **nothing yet** beyond the period's refill at the pre-upgrade plan: it records a pending upgrade (`billing_pending_upgrade_grants`: organization,
subscription, period, old and new entitlement, the event's `occurred_at`). The credits arrive with the
**paid proration transaction**: Paddle's `transaction.completed` with `origin: subscription_update`
(parsed as `BillingEventKind.UpgradePaid`) grants the pending upgrade **it belongs to**, as a `Plan`
grant expiring at the period end, under the key `plan-upgrade:{provider}:{transactionId}`. A failed charge (`transaction.payment_failed`) grants
nothing, and an upgrade whose payment never completes never grants. The two webhooks may arrive in
either order: a payment that lands first is remembered (paid, unmatched) and the upgrade event grants
at once when it arrives; each order grants exactly once, and redeliveries of either change nothing.
The payment is not a subscription event, so it never moves `last_event_at`.

**Two gates on every upgrade grant.** Once an upgrade and its payment meet on one row, the
difference is granted only when **both** hold:

1. **The payment charged more than 0.** The amount is the transaction's `details.totals.grand_total`
   (minor units, a string; `BillingEvent.AmountMinor`). Missing, unparseable or negative reads as 0
   (fail closed). A $0 payment still pairs with its upgrade and **closes** it with 0 granted
   (`granted_millis = 0`, `granted_at` set; a $0 payment that lands first is stored already closed),
   so that upgrade, holding a transaction, can never absorb another payment.
2. **The subscription row is `Active` in the upgrade's own period at grant time.** Paired while
   `PastDue` or `Paused` in that period, the row **waits** (transaction set, `granted_at` null) and is
   granted once, by the subscription event that makes it `Active` again in the same period, or at
   pairing if that event came first. Paired while `Trialing` or `Canceled`, or once the subscription
   is in a later period, it is closed with 0: a trial's upgrade never grants (the trial ends with a
   new period, refilled at the new plan). A full refund of a waiting row's transaction retires it with
   0, so a later `Active` grants nothing. A waiting row is also closed with 0 (with a `Warning`) by the
   first event that moves the subscription to a later period or to `Canceled` (Security G-1), so a
   late `Active` event that still names the old period grants nothing. A scheduled cancel keeps the
   status `Active`, so a completed charge above 0 still grants (credits expire at the period end, the
   cancel date). The status is Paddle's `data.status`; anything but the five documented words
   (`active`, `trialing`, `past_due`, `paused`, `canceled`) — unknown, empty or missing — is read as
   `PastDue`, never `Active` (Security G-4): the plan keeps working as in dunning, but there is no
   refill and a paid upgrade waits until a real `active` arrives.

Only `origin: subscription_update` is an upgrade payment: a renewal (`subscription_recurring`), a
one-time charge (`subscription_charge`) and a top-up never pay for a pending upgrade.

**Pairing is on the charged items, never on time alone (Security R-2).** Each upgrade row stores its
**target items** (`target_items`: the subscription event's items as `priceId:quantity`, sorted and
comma separated) and each payment row its **charged items** (`charged_items`, same form): the lines of
the transaction's `details.line_items` that Paddle charged above 0 (`totals.total` > 0, a `price_id` and
a quantity of at least 1; the credit lines for the removed items are 0 or less and are skipped). A
payment and an upgrade meet only when they are the same subscription and these two lists are
**exactly equal**. Time only narrows those exact matches: the payment's instant must lie inside the
upgrade's period and within `BillingEventProcessor.UpgradePairingTolerance` (**10 minutes**, either
side) of the upgrade event's `occurred_at`. The payment's instant is its proration start
(`items[].proration.billing_period.starts_at`) and, only when the transaction carries none, its
completion time. Time never pairs on its own and never picks between exact matches: pairing must be
unambiguous **both ways**, the arriving payment (or upgrade) has exactly one exact candidate in the
window, and that candidate has no other unmatched payment (or unpaid upgrade) for the same items in the
window either. Several exact matches (two upgrades to identical items minutes apart): nothing pairs, a
`Warning` names every transaction and row id for manual review, 0 is granted. No exact match, or no
items on either side, never pairs (fail closed): the payment is stored unmatched, with a `Warning` when
it carries no usable items or when unpaid upgrades in the window target other items; an upgrade that
finds only payments for other items in the window logs a `Warning` too. Rows written before this rule
(both columns null) never pair. So two upgrades to different plans or seat counts, minutes apart, each
pair with their own payment whichever order the four webhooks arrive in, and a refund of one payment
claws back only its own upgrade's grant.

A refunded payment never grants, but once its `transaction.completed` has arrived (it then carries its
charged items) it still pairs by the same rule (and counts as a rival): the upgrade it pairs with is
**retired**, closed as paid-and-clawed-back with 0 granted, so it cannot absorb another payment, and its
entitlement counts toward the period's cap as for a payment refunded after its grant. Every retire logs
a `Warning` naming the upgrade row and the transaction (Security N-5). This holds whichever of upgrade,
refund and payment comes first. **Neighbour residual (N-6).** A refunded payment can retire only an
upgrade to exactly its own items within the tolerance, so the old case (a refund retiring a neighbouring
upgrade to a different plan whose real payment was still in flight) is gone. What remains is two
upgrades to identical items inside the tolerance with one refunded payment arriving between them: it
can retire the wrong one of the two; the other payment then finds only that retired upgrade, stays
unmatched, and logs a `Warning` naming it ("matches only upgrade rows … already retired"). That is an
under-grant only, flagged for review. Anything else stays unmatched and grants nothing: an early upgrade
whose update was dropped as stale cannot lend its start-of-period payment to a last-day upgrade, and a
last-day upgrade never inherits a day-1 payment.

**Shape assumption to confirm against a real Paddle sandbox payload.** The charged items come from
`details.line_items[]` of the `transaction.completed` with `origin: subscription_update`: each line's
`price_id`, `quantity` and `totals.total` (an integer string, minor units), with the new item(s) charged
above 0 and the removed item(s) credited at 0 or less, as in Paddle's transaction docs
(<https://developer.paddle.com/api-reference/transactions/overview>). Not yet verified on a sandbox
proration; until it is, a payload without such lines simply never pairs (0 granted, `Warning`), and the
e2e Paddle mock (`e2e/paddle-mock/server.mjs`) emits this shape. A seat change on the same price is
assumed to appear as a credit for the old quantity and a positive line for the new quantity (target
`pri_team:5` for 3 → 5 seats); a subscription with extra items that do not change would not match
(its target lists every item, the charge only the changed ones) and would under-grant with a `Warning`.

**Serialised per subscription.** `ProcessAsync` takes `pg_advisory_xact_lock(key)` inside the
per-event transaction, before it reads the subscription or any pending upgrade row, for every
subscription event, upgrade payment and refund that names a subscription
(`key = BillingEventProcessor.SubscriptionLockKey(provider, subscriptionId)`, the first 8 bytes of
SHA-256 of `billing-subscription:{provider}:{subscriptionId}`). A `subscription.updated` and the
`transaction.completed` of the same upgrade delivered at the same moment therefore run one after the
other, and the second sees the first's row instead of both inserting an unpaired one. Paddle refunds
(`adjustment.*`) carry the adjustment's `subscription_id`; a refund that names none takes the lock on
its matched upgrade row's subscription and re-reads that row. A top-up payment and every refund first
take a second lock, `BillingEventProcessor.TransactionLockKey(provider, transactionId)` (SHA-256 of
`billing-transaction:{provider}:{transactionId}`), before any subscription lock, so a top-up and its
refund never both commit (a grant and a tombstone) and the two locks never form a cycle. The locks are released at commit or
rollback, and it is a no-op on non-Postgres providers (the Sqlite/InMemory tests apply events one at
a time).
`BillingEventProcessor.ProratedUpgradeGrantMillis` computes the grant:

```text
difference = new entitlement − old entitlement      (MonthlyCreditsMillis × seats; nothing unless > 0)
remaining  = clamp(periodEnd − at, 0, periodEnd − periodStart)
grant      = floor(difference × remaining ÷ (periodEnd − periodStart))   millicredits
```

The grant uses the upgrade event's period and, as `at`, `max(the instant Paddle prorated from (the
paid transaction's `items[].proration.billing_period.starts_at`), the upgrade event's `occurred_at`)`
— never earlier than the upgrade itself, whatever the payment says;
an upgrade without a period start or end grants nothing (and logs a warning). An upgrade at the start
of the period grants the full difference, at mid-period half of it, with one day of a 30-day period
left 1/30 of it, in the last hour about 1/720 of it.

**Capped per period.** The "old entitlement" of a paid upgrade is a high-water mark:
`max(entitlement the period started with (its refill), highest new entitlement already granted for an
upgrade in this period)`. Upgrade, downgrade (held), keep and upgrade again, or a price-up that lowers
the credits followed by another upgrade, therefore never add up to more than the highest plan's
prorated difference in one period; Pro → Team after a paid Creator → Pro only adds the prorated
Team − Pro difference.

The endpoint's `creditGrantMillis` is the same function with the subscription row's period and the
request time, returned for display; the grant itself happens only when the payment completes, so it
can be slightly smaller. A seat increase through `POST /seats` follows the same rule.

**Downgrade: next period.** Paddle has no scheduled item change: `full_next_billing_period` switches
the subscription's items **now** and bills the new price from the next period. The endpoint sends
that mode, and when the same-period webhook shows the lower plan, the processor keeps `plan_id` and
`seats` (the customer paid for them) and records the lower plan in `scheduled_plan_id`,
`scheduled_seats` and `scheduled_change_at` (the period end). The summary shows it as
`scheduledChange`. The first event with a **new** `PeriodStart` applies the event's plan (by then
Paddle's items already are the lower ones), clears the hold, and refills at the lower plan. A seat
decrease through `POST /seats` is a downgrade: it is sent with `full_next_billing_period` too, so
Paddle refunds nothing for the seats removed, and the webhook holds it the same way. Adding seats
and removing them again inside one period therefore cannot turn a short proration into credits.

**Keep my plan (for a downgrade).** `change-plan` with the current plan and seat count while a
downgrade is held sends the current items back with `full_next_billing_period` (this period is
already paid) and answers `202 { direction: "keep" }`. The same-period webhook that lands back on the
row's own plan clears the hold. With nothing held, the same request is `409 no_change`.

Duplicated or out-of-order webhooks cannot grant twice or apply a hold twice: events not newer than
`last_event_at` are dropped as `Stale`, a proration transaction is stored once per provider, and
the upgrade grant key is idempotent. A refund or chargeback claws back the grant made for **its own**
transaction (found by transaction id on the paired row), never another upgrade's; refunding a payment
that never paired takes nothing and closes it, and refunding one that has not arrived yet leaves a
tombstone so it never grants (see "Subscription state machine").

## Annual plans

A subscriber can pay once a year (ten times the monthly price; Team per seat) and still gets credits every month.
There is **no migration** and no stored interval column.

- **Interval derivation.** A subscription is annual when its period (`PeriodEnd - PeriodStart`) is longer than 40
  days (`SubscriptionInterval.Of` in `ReelBolt.Shared.Billing`); otherwise, or when a bound is unknown, it is monthly.
  Plans carry the yearly provider price id as an `annual` key next to `monthly` in `provider_price_ids_json`; the
  price a webhook names also tells the interval. `subscription.periodEnd` in the billing summary stays the yearly
  billing end; `subscription.creditsRenewAt` is the next monthly credit renewal (the next anniversary on annual).
- **Monthly grants.** The renewal (or first) webhook grants month 0 as before under `plan:{provider}:{sub}:{periodStart}`,
  but the grant expires one month after the period start, not at the yearly end. `AnnualCreditGrantSweeper` (hosted
  service, `Billing:AnnualGrants:Enabled` default `true`, `SweepIntervalMinutes` default 60, `StartupDelaySeconds`,
  `MaxPerSweep`, `MaxPassSeconds`) grants months 1..11 for Active/Trialing annual subscriptions under
  `plan:{provider}:{sub}:{periodStart}:m{n}` at the current plan and seats, expiring at the next anniversary (the
  last month at the term end). It grants only the month whose window is open now: a month already past (a replica
  was down) is lost, not granted born-expired, and one ahead is never granted early. It skips a cancelled-effective
  subscription, any other status, a term whose payment was fully refunded (see the refund rule), and a term the
  renewal webhook has not opened. A pass handles at most `MaxPerSweep` subscriptions and stops taking new ones after
  `MaxPassSeconds`; it resumes after the last one it handled (a cursor kept between passes, wrapping round), so a
  large due set is covered across passes and never the same first rows. Every replica runs it; the subscription
  advisory lock and the ledger keys make a second pass a replay.
  second pass a replay. Unspent credits expire at the month end like monthly ones.
- **Upgrades inside a term.** The prorated credit difference (and a seat increase) uses the current month
  (anniversary) window, never the whole year, and expires at that month's end: a grant never exceeds one month's
  difference. The assistant allowance and the "credits renew" date use the same monthly window.
- **Switch rules.** Same-interval upgrades are immediate and prorated, downgrades are held to the term end.
  Monthly to annual is immediate (the provider prorates); the new term's first refill is reduced by the plan refill
  already granted for that subscription in the month before the term start, so the month is never granted twice and
  never goes negative. Annual to monthly in the app is refused (`409 interval_change_not_supported`): cancel at renewal
  and subscribe monthly.
- **Refund rule.** The term's payments are every positive paid payment of the subscription from the term's
  `period_start` on that has no upgrade row of its own (the last monthly payment before a switch is never part of the
  term). `subscriptions.term_opening_transaction_id` (the one additive column of this feature) only identifies the
  opening payment: the webhook processor sets it once per term and only on an explicit provider signal (a Stripe
  `subscription_create` / `subscription_cycle` invoice whose billing period starts at the term start, a Paddle `transaction.completed` whose `billing_period.starts_at` is the term start (within minutes), (within
  5 minutes; the provider stamps the period just before the payment), or an upgrade payment billed from exactly the
  term start to the second, no tolerance (the monthly-to-annual switch; a seat change is prorated from a later instant and
  never pairs), clears it when the period start moves, and
  never guesses from time, amount or an upgrade row. Both the sweeper and the clawback read one helper
  (`AnnualTermPayments`). Fail closed: no term payment, or several with no opening id, is ambiguous: grants are
  skipped and `Review manually` is logged, but ambiguity never skips a clawback: a full refund or chargeback of any
  candidate payment claws the term back. The stored opening payment is always loaded by id, whatever its timestamp. A full refund or chargeback
  of any term payment stops grants; of one within `BillingRefundPolicy.MoneyBackDays` (14) days of THAT payment's date
  it also claws back, under the org credit lock, the unspent plan credits of the term (month 0 and every monthly
  grant) under one `clawback:{provider}:{transaction}` key, so a refund then a chargeback takes back once. A refunded
  upgrade or seat payment with its own row reverts to the pre-upgrade entitlement (upgrade rules); an unpaired one
  stops grants without a clawback. A partial refund, one later than 14 days, and every monthly refund change no
  credits and stay a human decision. The 14 days is one named constant here and `MONEY_BACK_DAYS` in
  `site/lib/launch-offer.ts`.
- **Grants after a refund (sweeper rule).** For any full refund or chargeback of a payment of the term the sweeper
  never grants more than was paid for: it grants `min(current plan amount, the pre-upgrade entitlement of every
  upgrade or seat payment that is unpaired, ungranted or refunded)`. A refunded or charged-back upgrade or seat
  payment therefore reverts further grants to the amount before it (what was granted that month is clawed back by the
  upgrade rules); a fully refunded term payment, or any other fully refunded payment of the term that is not an
  upgrade's own and cannot be tied to one, skips the term (fail closed, logged for review).
- **Promotions.** A plan-discount promotion targets the monthly price ids only. It targets the annual ones as well only
  when it says so explicitly (`includeAnnual: true`), and never with a months duration (creating one answers 400).
- **Monthly offers do not follow a move to annual.** The yearly price shares the monthly price's provider product, so
  a product-scoped coupon would discount the whole annual invoice (every year for a forever offer, the entire
  invoice for a months offer). When a plan change moves a monthly subscription to an annual price (same plan or
  another), a plan discount that does not target that yearly price (no `includeAnnual`), and every months-duration
  discount, is removed in the same provider call (the existing remove path, recorded on the redemption), and the
  response says plainly that the monthly offer does not carry over to the yearly price. A promotion created with
  `includeAnnual` keeps working. Monthly to monthly and annual changes are unchanged.
- **Accepted residuals.**
  - A Paddle opening payment processed before `subscription.created` (id null) with a timestamp before the term start is
    only a candidate by its timestamp, so a refund of it is not recognised as a term payment until the id exists; it is
    flagged by the missing/ambiguous term rather than clawed. Manual review.
  - Without an explicit signal (a Paddle payment without `billing_period`; Stripe's payment can land before the subscription update) the
    id stays null: one plain payment is the term payment, several are ambiguous and skip grants for review. A refund
    of a small plain payment stops the remaining grants; it needs manual review.
  - A zero-amount term, or a term whose webhook was lost, gets no grants (a warning is logged).

## The first-video grant

**What.** A new account gets a one-time 750-credit "first video on us" grant on top of the 100-credit trial. It is a `Promo` credit grant with ledger note `first-video` and idempotency key `firstvideo:{orgId:N}`.

**Who.** It is written by `CreditAccountProvisioner` in the same transaction as the trial grant, and only when the `trial_claims` insert is NEW: a verified Owner of a Personal workspace whose normalised email never claimed a trial. Existing accounts (they already hold a claim), a second workspace of the same person, unverified users and billing-off installs get nothing. A replay for the same org is a no-op.

**Config.**

| Key | Default | Meaning |
|---|---|---|
| `Billing:FirstVideoGrant:Enabled` | `true` | Kill switch. |
| `Billing:FirstVideoGrant:Credits` | `750` | Whole credits. |
| `Billing:FirstVideoGrant:ExpiryDays` | `30` | Days until the grant expires. |

**Summary.** `GET /api/v1/billing/summary` carries an additive `firstVideoGrant` block `{ credits, expiresAt, remaining }`, or `null` once it is spent, expired or never granted. It is derived from the Promo grant behind the `firstvideo:` ledger entry, not from a new table.

**Refusals.** The grant is refused (the account and the 100-credit trial still work) when the new account looks abusive. The reason is decided before the grant is written, stored in `trial_claims.first_video_denied_reason` in the same transaction as the claim, and logged as reason plus organization id only. The raw sign-up IP is never stored in that row or logged.

| Reason | Rule |
|---|---|
| `disposable_email` | The email domain (or a parent domain) is on the vendored disposable list, or the domain has no MX and no A/AAAA record (DNS lookup, 2 s timeout). Any DNS failure is not a refusal. |
| `tor_exit` | `application_users.signup_ip` is on the public Tor bulk exit list, refreshed by a background service. |
| `vpn_proxy_hosting` | ipinfo.io privacy detection reports `vpn`, `proxy`, `tor` or `hosting` for the sign-up IP. `relay` alone is not refused. |

Everything fails open: no stored sign-up IP skips the IP checks; no `IPINFO_TOKEN`, a timeout or an error grants normally and increments the `reelbolt.billing.first_video_ipinfo_failures` counter (not counted when no token is set); a failed Tor list refresh keeps the old list. ipinfo answers are cached per IP in memory for 24 hours (bounded).

| Key | Default | Meaning |
|---|---|---|
| `Billing:FirstVideoGrant:Abuse:TorListUrl` | `https://check.torproject.org/torbulkexportlist` | Tor exit list source. |
| `Billing:FirstVideoGrant:Abuse:RefreshHours` | `6` | Tor list refresh interval. |
| `Billing:FirstVideoGrant:Abuse:IpInfoToken` | empty | ipinfo token; set it with the `IPINFO_TOKEN` env var (secret, see `docs/secrets.md`). Empty disables the VPN/proxy/hosting check. |
| `Billing:FirstVideoGrant:Abuse:MxCheck` | `true` | Turn the no-mail-server check off. |

The summary also carries `firstVideoGrantStatus`: `granted` once the grant was written, `unavailable` when a refusal reason is recorded for the organization's claim, otherwise `none`.

**Updating the disposable list.** The .NET copy is `inference/src/ReelBolt.Inference.Api/Services/Billing/FirstVideoAbuse/disposable_domains.txt`, an embedded resource seeded from `api/services/disposable_domains.txt`. To update, refresh the Go file as its header says, copy it over the .NET one unchanged, and rebuild. A test fails when the two have a different number of domains.

**Farming limit.** The one-grant-per-person rule rests on email normalisation only (lowercase, `+tag` stripped, Gmail dots folded). A catch-all or alias domain can still earn more than one grant. Switch the grant off with `Billing:FirstVideoGrant:Enabled=false` if abused, and keep `Billing:FirstVideoGrant:Abuse:MxCheck` on (it is on by default; the Go sign-up check `ABUSE_MX_CHECK` is a separate setting and does not affect this grant).

**Why additive.** The 100-credit trial stays as is so a first video can finish without eating the trial; the Trial plan is unchanged (720p output, watermark).

## Founder price

The launch offer: 50% off every plan for new subscribers, open to NEW subscribers until 2027-01-31 23:59 Europe/Rome (= 2027-01-31T22:59:00Z). It is an ordinary `PlanDiscount` promotion (percentage 50, duration `forever`, no code, no targets = every sellable plan, `endsAt` set), so it works with Paddle now and with any provider that honours the contract below. No billing code is special-cased.

### Operator recipe

1. Open `/app/admin/promotions`, create a promotion and prefill it from the preset served by `GET /api/v1/billing/admin/promotions/presets` (`founderPrice`; platform admin only, 404 with billing off); the form then creates it with `POST /api/v1/billing/admin/promotions`. The code constant is `PromotionPresets.FounderPrice`.
2. The exact fields: kind `planDiscount`; name `Founder price`; public headline `Founder price: 50% off`; discount type `percentage`; amount `50`; duration `forever`; code empty; plans none selected (= every sellable plan); starts empty; ends `2027-01-31T22:59:00Z`; max redemptions empty.
3. Save. The provider discount is created from the same body; check the discount in the provider dashboard shows 50%, recurring with no maximum, and the expiry.

### What "locked" means

- A subscription that carries the discount keeps it. The summary's `promoDiscount` is read from the redemption row, not from `endsAt`, and a plan change or seat change keeps it because the promotion targets every plan (`promoDiscountRemoved=false`).
- Retention does not depend on the price list: a plan promotion created with no explicit plans keeps its discount on a change to ANY plan, including one sold after the promotion was created. (Such a later plan is not discounted for NEW checkouts or by the provider discount, which is restricted to the price ids that existed at creation; recreate or extend the promotion if a plan should get the founder price.)
- `endsAt` only closes the offer for NEW checkouts. A customer who cancels and re-subscribes after `endsAt` pays full price.
- Paddle: the discount is created with `recur=true` and no `maximum_recurring_intervals`, so it applies on every renewal.
- Stripe: a coupon with `duration=forever`, applied through `discounts[]`, with `redeem_by = endsAt`. Plan changes do not edit or remove it.

### Unverified

Paddle: does a forever (recurring, unlimited) discount that has an `expires_at` keep applying to an EXISTING subscription after that date passes? The code assumes yes (expiry limits new use only). Verify in the sandbox before launch: create the discount with a near expiry, subscribe, wait past it, and confirm the next renewal is still discounted.

### Provider contract for forever discounts

For any `IBillingProvider`:

1. `CreateDiscountAsync` with `Recur=true` and `MaximumRecurringIntervals=null` must produce a discount that applies on every renewal for as long as the subscription is active.
2. `ExpiresAt` limits who may START using the discount (new checkouts, new attachments). It must never remove the discount from a subscription that already carries it.
3. A discount with no code is attached automatically at checkout for the targeted prices (`RestrictToPriceIds`, here every sellable price).
4. A plan or seat change passes `PlanChangeDiscount.Unchanged` and must keep the discount; it is only removed (`Remove`) when the new price is not targeted.
5. `ProviderSubscriptionState.Discount` reports the live discount id and, when it has one, its end; an end in the past means the discount is gone.

### The 14-day money-back path

There is no in-app refund button. Operations: refund in the provider dashboard, the provider sends the refund event, `PaymentRefunded` is processed by the webhook handler, and the credits from that payment are clawed back by the existing flow.

## Promotions and discounts

Platform admins run promotions from `/app/admin/promotions`. The code is
`Services/Billing/Promotions/PromotionService.cs`, the controllers are `PromotionsController`,
`Admin/PromotionsAdminController` and `Admin/OrgCreditsAdminController`, and every route answers
`404` when billing is off. The four kinds:

| Kind | What it does | Paddle side |
|---|---|---|
| `CreditCode` | A code a workspace redeems for free credits: a `Promo` grant of `credits` (1 to 100,000) that expires `creditExpiryDays` later (1 to 730, default 90). A code is required. | None. |
| `PlanDiscount` | A discount on a plan subscription: percentage or flat, for the first payment, N months or forever. | A Paddle discount. |
| `PackDiscount` | A discount on a top-up pack, always for one payment. | A Paddle discount. |
| `PackBonus` | Extra credits on a bought pack: `bonusPercent` (1 to 100) of the pack size, granted as `Promo` credits that expire 12 months later. | None. |

A promotion with no code is **automatic**: the server applies it to every matching checkout. A code
is normalized (whitespace removed, uppercase) and must match `^[A-Z0-9]{4,32}$`. Codes exist only on
our side.

**Discounts are Paddle discounts, applied by id.** Creating a `PlanDiscount` or `PackDiscount` calls
`POST /discounts` with no `code`, `custom_data.promotion_id`, `usage_limit` = the global maximum,
`expires_at` = the end date, and `restrict_to` = the targets resolved to explicit price ids
(an empty target list means every plan or pack on sale; a target with no price id is
`400 target_not_purchasable`). Duration maps as: once → `recur: false`; N months →
`recur: true, maximum_recurring_intervals: N`; forever → `recur: true` with no maximum. Only then
is the local row saved, with `provider_discount_id`; a Paddle error answers `502
billing_provider_error` and saves nothing. The checkout passes `discount_id` on `POST
/transactions`, so a price is never computed or written by hand, and the preview shows Paddle's
own `formatted_totals`.

**Editing.** The value fields (kind, code, amounts, targets, duration, bonus) cannot change after
create. `PATCH` accepts `name`, `publicHeadline`, `status` (`active`, `paused`, `archived`),
`endsAt` or `clearEndsAt`; anything else is `400`. Status and end date go to Paddle first (paused
or archived → Paddle `archived`), and the row is saved only when Paddle accepts.

**Verified email only.** Only an account whose email is verified can reserve or redeem a
promotion, so throwaway signups cannot farm codes. The check reads `application_users`, never the
token. A code from an unverified account, on redeem, checkout or preview, is
`403 { code: "EMAIL_NOT_VERIFIED", error: "EMAIL_NOT_VERIFIED", message }`; an automatic promotion is
simply not applied to that account's checkout.

**Resolving a checkout.** With a code, the promotion must be active, inside its window, of a kind
the target takes, target the price id, have a Paddle discount (a bonus needs none), and be under
every limit; otherwise the answer is `400 promo_code_invalid`. With no code, the oldest active
automatic promotion of the matching kind that passes the same checks applies. A pack bonus is
resolved on its own: a bonus code supplied as the code, or else the automatic bonus. So a bonus
code stacks with an automatic pack discount. An automatic promotion over a limit is skipped, not
refused. The limits:

- **Global** (`maxRedemptions`): the completed uses plus the open reservations of other buyers
  must leave room for one more. The caller's own open reservations do not count, because this
  checkout supersedes them (see [Checkout reservations](#checkout-reservations)).
- **Per organization and per user** (`maxRedemptionsPerOrg`, default 1): the organization's
  completed uses of that kind, and the user's across every organization, must each stay under it.
  A person who creates a second organization does not get a second use.

**Automatic overlap.** Creating or reactivating an automatic promotion whose window overlaps, and
whose targets intersect, another active automatic promotion of the same kind is
`409 automatic_promotion_overlap`. A duplicate code is `409 promo_code_taken`.

### Checkout reservations

A checkout that carries a promotion reserves it before Paddle is asked for a transaction, so the
limits hold while buyers sit in the payment overlay. The code is
`PromotionService.ReserveCheckoutAsync`, and the reservation is a `promotion_checkouts` row.

1. **Rate limit.** One user may reserve `Billing:Promotions:CheckoutsPerUserPerHour` (default 10)
   checkouts that carry a promotion per hour, counted in the shared `mail_rate_counters` table
   (bucket `promo:checkout:user:{userId}`). Only a checkout that reserves is counted. A checkout
   without a promotion is not counted, and neither is one answered `checkout_busy`, or one whose
   promotion no longer applies (step 4). A user already over the limit is refused with
   `429 promo_checkout_rate_limited` before the promotion gates and locks are taken. The count is
   checked once more under the locks, in the checkout's own transaction, just before the row is
   inserted. A checkout that loses the last hour to a concurrent one rolls back and gets the same 429.
2. **Lock.** One transaction takes `pg_advisory_xact_lock(hashtext('promo:' || promotion_id))` for
   the discount and the bonus, in ordinal key order, so two checkouts side by side cannot both take
   the last use.
3. **Supersede.** The caller's own open reservations of the same promotions, by the same user or
   the same organization, are marked superseded. There is at most one open reservation per
   (promotion, user) and per (promotion, organization). Their Paddle transactions are cancelled
   once the new checkout exists; see [Cancelling stale checkouts](#cancelling-stale-checkouts).
4. **Re-check and insert.** Every limit is checked again under the lock. A code that no longer
   applies is `400 promo_code_invalid`; an automatic promotion that no longer applies is dropped.
   The row is inserted with `within_caps_at_reservation = true` and committed, and only then is
   Paddle called. If Paddle fails, the row is deleted.

A reservation is open until its payment webhook completes it, it is superseded, or
`Billing:Promotions:CheckoutReservationMinutes` (default 60, Paddle's checkout lifetime) passes.
An expired reservation never counts against a limit.

#### Cancelling stale checkouts

A superseded or expired reservation reserves nothing, so its Paddle transaction must not stay
payable with the discount or bonus. `IBillingProvider.CancelTransactionAsync` cancels it: Paddle
gets `PATCH /transactions/{id}` with `{"status":"canceled"}`.

- **What Paddle cancels.** Only a transaction that can still be paid (`draft` or `ready`). Paddle
  answers a 4xx for one that is paid, billed, completed, already canceled or unknown; that
  transaction is left alone and marked settled. A 401, 403, 408, 429 or 5xx answer, a network error
  or a timeout is a failure, and the cancel is tried again later. Only the HTTP status and Paddle's
  `error.code` are logged, because the error detail can quote the buyer.
- **At supersede time.** `POST /checkout` cancels the reservations it superseded right after the new
  Paddle transaction exists (`PromotionService.CancelSupersededAsync`). It is best effort: a failure
  is logged as a Warning and never fails the new checkout.
- **The sweeper.** `PromotionCheckoutSweeper` runs every
  `Billing:Promotions:CheckoutSweepIntervalMinutes` (default 5), starting two minutes after the API
  starts. Each run takes up to 200 unpaid reservations that have a Paddle transaction id and no
  `provider_cancelled_at`, and that are superseded or older than
  `Billing:Promotions:CheckoutReservationMinutes`. It retries a row on every run until Paddle
  cancels it or refuses for good. It does nothing while no billing provider is configured. Each reservation is cancelled through the provider that owns its transaction id (`txn_` Paddle, `cs_` Stripe; see [Payment identity (Stripe)](#payment-identity-stripe)).
- **Columns.** `promotion_checkouts.provider_cancelled_at` is set once the transaction is cancelled
  or found past cancelling. `provider_cancel_attempts` counts the attempts.

#### Paying

The webhook grants a pack bonus only from the reservation this server stored, never from the
checkout's client-visible custom data: a payment without a server-recorded reservation gets no
bonus. A discount Paddle applied is matched to the paid checkout's reservation when that
reservation belongs to the paying organization and names the discount. Under the
promotion's lock, each paid use is decided in this order:

1. **Within every limit** (global, per organization, per user; granted uses only): granted. This
   holds for a superseded or expired reservation too.
2. **Past a limit:** honoured only when a reservation backs the payment, and that reservation is
   live and was within every limit when it was created (`within_caps_at_reservation`). Live means
   not superseded, and not expired at the time Paddle reports the payment: the event's
   `OccurredAt`, or now if that is later or missing. Reservations created before migration
   `AddPromotionNotGrantedAndProviderCancel` have `within_caps_at_reservation = false`.
3. **Once per buyer.** Each user, and each organization, gets at most one use past a limit per
   promotion.
4. **Bounded per promotion.** A promotion is honoured past its limits at most
   `min(5, max(1, MaxRedemptions / 100))` times, in integer division. With no global maximum, the
   bound is 5, because the per-user and per-organization limits can still be passed.

A use honoured past a limit is saved with `overshoot = true` and logs a Warning. Every other use
past a limit is refused, and so is a bonus whose payment came from another organization or bought
another price than the reservation names.

A paid reservation supersedes the buyer's other open reservations of the same promotions.

#### Refused uses

A refused grant or count is still written to `promotion_redemptions`, with `outcome = NotGranted`
and a `not_granted_reason`, and logs one Warning (event `41701` `PromotionNotGranted`, with ids and
the reason only). `GET /admin/promotions/{id}/redemptions` returns it as `outcome: "notGranted"` and
`notGrantedReason`, so support can make the buyer whole. A `NotGranted` row never counts against
any limit and is not in `redemption_count` or the admin list's redemption count. For a discount, it
means Paddle charged the discounted price but the use was not counted; for a bonus, it means no
credits were granted.

| `notGrantedReason` | Meaning |
|---|---|
| `superseded` | A limit is reached and the reservation was superseded by a newer checkout or another payment. |
| `expired` | A limit is reached and the reservation had expired when the payment happened. |
| `reservedOverCap` | A limit is reached and the reservation was not within every limit when it was created. |
| `notReserved` | A limit is reached and no reservation of this server backs the payment. |
| `userCapReached` | The user is at the limit and already had their one use past it. |
| `orgCapReached` | The organization is at the limit and already had its one use past it. |
| `overshootBoundReached` | The promotion's bound on uses past its limits is used up. |
| `checkoutMismatch` | A bonus was paid by another organization, or for another price, than the reservation names. |

**Redeeming a credit code.**

1. The account's email must be verified, else `403 EMAIL_NOT_VERIFIED`.
2. The code must name an active `CreditCode` inside its window, else `400 promo_code_invalid`.
3. In one transaction, under the advisory lock of `promo:{promotionId}:user:{userId}`: if the
   organization, or the user across all organizations, has reached the per-organization maximum
   (default 1), the answer is `409 promo_already_redeemed`. Concurrent redeems by one user from
   several organizations wait on that lock, so the second sees the first.
4. The global maximum is claimed by one conditional `UPDATE ... WHERE redemption_count <
   max_redemptions`. Zero rows affected is `400 promo_code_invalid`, the same answer as an unknown
   code.
5. The grant's ledger key is `promo:{promotionId}:{orgId}:{n}`, where `n` is the organization's
   prior redemption count plus one. Two concurrent redeems by one organization derive the same
   key, so the ledger grants once and the loser rolls back with `409 promo_already_redeemed`.

**Counting uses.** `redemption_count` counts every granted use: a redeemed code, a recorded
discount and a granted bonus, including a use honoured past the maximum (`overshoot`). A
`NotGranted` row is never counted, and the per-organization and per-user limits count granted rows
only.

**Guessing codes.** Unknown, paused, expired, not-started and wrong-target codes all answer the
same `400 promo_code_invalid` body. `PromotionCodeAttemptLimiter` allows 10 failures per user per
hour across redeem, checkout and preview, then answers `429 promo_rate_limited`. It is in memory
and per process, so a restart or a second replica resets it: it slows guessing, it is not a lock.

**Admin credit grants.** `POST /admin/orgs/{orgId}/credit-grants` takes `{ requestId, credits,
expiresAt, reason }`: 1 to 1,000,000 credits, an expiry between 1 day and 3 years ahead, and a
reason of 1 to 500 characters. It writes a `Promo` grant keyed `admin-grant:{requestId}` and an
`admin_credit_grants` row in one transaction. A replayed `requestId` returns the same row with
`200`; the same `requestId` for another organization is `409 request_id_conflict`.

**Error bodies** are `{ error, message }`, and a validation failure is `{ message, errors }`.

| Status | `error` |
|---|---|
| `400` | `promo_code_invalid`, `target_not_purchasable`, or a validation failure |
| `403` | `EMAIL_NOT_VERIFIED` (the body also carries it as `code`) |
| `409` | `promo_already_redeemed`, `promo_code_taken`, `automatic_promotion_overlap`, `request_id_conflict` |
| `429` | `promo_rate_limited`, `promo_checkout_rate_limited`, `checkout_busy` |
| `502` | `billing_provider_error` |
| `503` | `billing_not_configured` (a discount kind with no configured provider) |

**Checkout lock wait.** A checkout with a promotion waits for the promotion's locks: first the
in-process gate, then the database lock under `lock_timeout`. `Billing:Promotions:CheckoutLockWaitMilliseconds`
(default `5000`) bounds each wait separately: first the hourly-limit probe, then the in-process gate and the database locks together. The worst case is therefore about twice the setting. It must be between 1 and 60000, or startup fails. When a
lock is not free in time, `POST /api/v1/billing/checkout` answers `429 checkout_busy` with
`Retry-After: 2` and the body `{"error":"checkout_busy","message":"Many checkouts with this promotion are in progress. Try again in a moment."}`.
Nothing is reserved or superseded, so the client should retry the request. The busy checkout is not
counted against the hourly limit either. The rate-limit probe that runs before the locks has its own
short transaction, bounded by the same wait, so a busy counter lock is also answered `checkout_busy`.

**Unverified: the discount inside Paddle's overlay.** Discounts are created with
`enabled_for_checkout` = `Billing:Paddle:DiscountsEnabledForCheckout` (default `false`), so a buyer
cannot type our discount into Paddle's checkout. Whether a discount with `enabled_for_checkout:
false` survives `Paddle.Checkout.open({ transactionId })` has **not** been tested in a real Paddle
sandbox. An operator must check it before launch (see the setup checklist below). If the overlay
drops the discount, set `Billing__Paddle__DiscountsEnabledForCheckout=true`; it applies to
discounts created after the change. When a checkout carries a discount, the web client opens the
overlay with `allowDiscountRemoval: false` and `showAddDiscounts: false`.

### Public plan prices (`GET /api/v1/billing/promotions/plan-prices`)

Anonymous. For the marketing site: each purchasable plan (Subscription or PerSeat, active, with a price id) that an automatic `PlanDiscount` promotion applies to, using checkout's own automatic selection for that price id but with no per-org caps ("what a fresh buyer gets"). Each plan is priced with the provider's pricing preview. A flat plan is previewed at quantity 1 and shows the line's formatted subtotal (`original`) and total (`discounted`). A per-seat plan is previewed at its minimum seats (`max(1, MinSeats)`, since the Paddle price refuses fewer) and shows the line's `formatted_unit_totals` subtotal and total, the per-seat figures, with `perSeat: true`; a per-seat line without unit totals is left out. These are Paddle's strings, verbatim; the API never computes or formats a price. The response carries `planKey`, `perSeat`, `currencyCode`, `original`, `discounted`, `headline`, `durationKind`, `durationMonths` and `endsAt`, and never a code or an id.

- **Cache:** `IMemoryCache` key `promotions:plan-prices`, 60 s absolute (an empty result too), behind a single-flight lock so concurrent misses make one round of previews. Previews run in parallel with a 2.5 s timeout each, under the site proxy's 3 s upstream timeout. The response sends `Cache-Control: public, max-age=60`.
- **Fail quiet:** an item is dropped (never a 5xx) when its preview throws or times out, the raw `totals.tax` is not `"0"` (the site shows pre-tax prices), the raw `totals.discount` is `"0"` or missing, or any string is empty. Each drop is logged at Warning with the plan key and a reason only, never an amount. With no provider configured the answer is `{ "items": [] }`; with `Billing:Enabled=false` it is 404.


## Plan priority

Pro, Team and Enterprise runs are dequeued ahead of everyone else's. There is exactly ONE source for
that: `entitlements.priority` in `plans.catalog.json` (`0` on Trial and Creator, `1` on Pro and Team,
`2` on Enterprise). It is read through the same plan entitlement every other runtime decision uses
(`OrganizationEntitlementSet.Priority`, mapped by `ReelBolt.Shared/Billing/RunPriority.cs`), so there
is no second "important customer" flag anywhere and a plan change applies to the next run with no
priority state to keep in sync. With `Billing__Enabled=false` every org is unlimited and therefore
standard (`0`), and a run is published byte-identically to how it was before this existed.

One number orders two queues:

| Queue | Field it orders by | Ordered by whom |
|---|---|---|
| RabbitMQ `workflow-execution` | the AMQP message priority | the broker, because the engine declares the queue with `x-max-priority` (`WorkflowEngine:QueuePriority:MaxPriority`, default `10`) |
| `runner_jobs` (Go runner gateway) | `runner_jobs.priority` | the gateway's claim: `priority DESC, created_at ASC, id` |

A standard run is published with NO priority property at all, so nothing about it depends on this
feature. The engine also resolves the run's class once when the execution starts and publishes it as
`WorkflowExecutionContext.RunPriority`, so one run keeps one class for its whole lifetime. The value
is clamped into `[0, 255]` (AMQP priority is one byte) so no catalog edit can produce a value the
publish path cannot carry.

**The second queue is not fed yet.** `RunnerJobRequest.Priority` is accepted by the engine's gateway
client and written to `runner_jobs.priority` verbatim, but **no production code constructs a
`RunnerJobRequest`** — the executors that would are D8 (render and staging) and D11 (media
sessions), and neither is in the tree. `WorkflowExecutionContext.RunPriority` therefore has no
reader, and nothing writes `runner_jobs.priority` today. The hop is a contract with both ends built,
not a wired path. See [compute-fabric.md](compute-fabric.md).

**Operator note — the queue argument is a one-time migration.** `x-max-priority` is a queue
argument, and RabbitMQ refuses to redeclare an existing queue with different arguments
(`PRECONDITION_FAILED`). An engine upgrading onto a broker that already holds the argument-less
`workflow-execution` queue fails to start its bus until that queue is deleted — drain it first, its
messages are not migrated. Set `WORKFLOW_QUEUE_MAX_PRIORITY=0`
(`WorkflowEngine__QueuePriority__MaxPriority`) to keep the old declaration and turn the ordering off;
it is the only value that does.

Priority is a queueing decision only: it never changes what a run costs, whether it is admitted
(that is the plan's concurrent-execution cap, `CONCURRENCY_LIMIT_REACHED`) or what it may include.

## Paddle specifics

Verified against Paddle's developer documentation on 2026-10-06:

- **Signature.** The header is `Paddle-Signature: ts=<unix seconds>;h1=<hex>`. The signed payload
  is `"{ts}:{raw body}"`, HMAC-SHA256 keyed with the destination's secret key, hex encoded, compared
  in constant time. More than one `h1` is sent while a secret is being rotated, and any match is
  accepted. Paddle's SDKs reject a timestamp more than 5 seconds old; this implementation does the
  same by default (`ToleranceSeconds`). Source:
  <https://developer.paddle.com/webhooks/signature-verification>.
- **Envelope.** `event_id`, `event_type`, `occurred_at`, `notification_id`, `data`. The processor
  keys idempotency on `event_id` and ordering on `occurred_at`. Source:
  <https://developer.paddle.com/webhooks/overview>.
- **Event types used.** `subscription.created`, `.activated`, `.updated`, `.canceled`, `.past_due`,
  `.paused`, `.resumed`, `.trialing` (all mapped from `data.status`), `transaction.completed` (top-ups,
  and subscription payments that carried a promotion's discount)
  and `adjustment.created` / `adjustment.updated` (refunds). Everything else, such as
  `customer.created`, is stored and ignored. Same source.
- **Response and retries.** Paddle expects `200` within 5 seconds. Sandbox retries 3 times in 15
  minutes; live retries 60 times over 3 days. Webhooks are not guaranteed in order and may repeat.
  Source: <https://developer.paddle.com/webhooks/respond-to-webhooks>.
- **Base URLs.** Live `https://api.paddle.com`, sandbox `https://sandbox-api.paddle.com`; sandbox keys
  work only on sandbox and live keys only on live. Requests send `Authorization: Bearer <key>` and
  `Paddle-Version: 1`. Source: <https://developer.paddle.com/api-reference/about>.
- **Checkout.** `POST /transactions` with `items` (`price_id`, `quantity`), `custom_data` and an
  optional `customer_id`. The response's `data.checkout.url` is the hosted checkout; it needs a
  default payment link set in Paddle's checkout settings, and the call fails loudly without one.
  The response `id` is the `transactionId` the browser opens in Paddle.js's overlay (and it is also
  what Paddle appends to the success URL as `?_ptxn=`). Source:
  <https://developer.paddle.com/api-reference/transactions/create-transaction>.
- **Portal.** `POST /customers/{customer_id}/portal-sessions`, optionally with `subscription_ids`;
  the link is `data.urls.general.overview`. The deep links are read from the
  `data.urls.subscriptions[]` entry whose `id` matches the subscription: `cancel_subscription` and
  `update_subscription_payment_method`. Sessions are temporary and must not be cached. Source:
  <https://developer.paddle.com/api-reference/customer-portals/create-customer-portal-session>.
- **Seats.** `PATCH /subscriptions/{id}` with `items: [{ price_id, quantity }]` and
  `proration_billing_mode`, through the same `ChangePlanAsync` call as a plan change:
  `prorated_immediately` for an increase, `full_next_billing_period` for a decrease. Source:
  <https://developer.paddle.com/build/subscriptions/add-remove-products-prices-addons>.
- **Cancel, resume, change plan.** `POST /subscriptions/{id}/cancel` with
  `effective_from: next_billing_period`; `PATCH /subscriptions/{id}` with `"scheduled_change": null`;
  `PATCH /subscriptions/{id}` with the full `items` list and `prorated_immediately` (upgrade) or
  `full_next_billing_period` (downgrade). With `PlanChangeDiscount.Unchanged` the body has no
  `discount` key, which leaves the subscription's discount as it is; with `PlanChangeDiscount.Remove`
  the same `PATCH` carries `"discount": null`, which removes it. A scheduled cancel is read from `data.scheduled_change`
  with `action: "cancel"` and its `effective_at`. Paddle's error envelope
  `{ "error": { "type", "code", "detail" } }` is surfaced as `409 billing_provider_refused` with
  `providerCode` and our own `message` for that code; `detail` is logged only. These were added after the date above and are checked only against the local mock.

- **Discounts** (checked 2026-10-12). `POST /discounts` takes `description`, `type`
  (`percentage` | `flat`), `amount` (a string: `"20"` is 20%; flat amounts are minor units, with
  `currency_code`), `enabled_for_checkout`, `recur`, `maximum_recurring_intervals` (null means
  forever), `usage_limit`, `restrict_to`, `expires_at` and `custom_data`, and returns `data.id`
  (`dsc_...`). There is no delete: `PATCH /discounts/{id}` sets `status` or `expires_at`. A plan
  discount needs `recur: true` to carry over to the subscription. Paddle has no automatic
  discount; for us "automatic" means our server passes `discount_id` itself.
- **Pricing preview.** `POST /pricing-preview` with `items[]` and an optional `discount_id` needs no
  customer and returns `details.line_items[].formatted_totals { subtotal, discount, tax, total }`.
  The preview endpoint returns the first line item's totals.
- **Discounted payments.** `transaction.completed` carries a top-level `discount_id` and
  `details.totals.discount`.
- **`customer.marketing_consent`** is Paddle's own checkbox and Paddle's consent. It is never
  imported; our consent store is [`marketing-consent.md`](marketing-consent.md).

The portal-session page itself returned 404 from the documentation fetcher on the date above, so the
endpoint path and response field come from Paddle's own search-indexed documentation snippets and
changelog rather than a fully rendered reference page. Re-check them in the sandbox before launch.

## Stripe

Stripe is the second billing provider. `StripeBillingProvider`
(`ReelBolt.Inference.Api/Services/Billing/Stripe/`) implements the same `IBillingProvider` contract as
Paddle, over a plain `HttpClient` and `System.Text.Json` (no Stripe SDK). Paddle stays the default.
The step-by-step production cutover is in
`billing-stripe-switch-over.md`; this section is the reference for
how the provider behaves. Stripe documentation facts below were checked against docs.stripe.com on
2026-10-26; the ones that could not be confirmed without a Stripe account are collected in
[Stripe wire assumptions: the sandbox pass](#stripe-wire-assumptions-the-sandbox-pass).

### Choosing the provider (Stripe)

`Billing:Provider` (`.env`: `BILLING_PROVIDER`) is `Paddle` (default; blank also means Paddle) or
`Stripe`, case-insensitive. Any other value stops the Inference API at startup. Both providers stay
registered at the same time, so a deployment can sell through one and still serve the other's
subscribers:

- **Active provider.** New checkouts, price previews, promotion discounts and the checkout sweeper use
  the provider named by `Billing:Provider`.
- **Row-bound provider.** Everything on an existing subscription (cancel, resume, seats, plan change,
  portal, reconcile) uses the provider named on that `subscriptions` row. A Stripe subscription keeps
  working after `BILLING_PROVIDER` is flipped back to `Paddle`, and the other way round, as long as the
  bound provider is still configured. If the bound provider is not configured, the active one is used.
- **Webhooks route by name.** `POST /api/v1/billing/webhooks/paddle` and
  `POST /api/v1/billing/webhooks/stripe` both answer when their provider is configured, whichever one
  is active.

The selection logic is `BillingProviderSelector`.

**Known gap: dashboard copy.** The dashboard's billing wording (for example "Manage billing in Stripe")
names the active provider, not necessarily the provider of the organization's existing subscription. It
matters only for a customer who is still subscribed through the other provider after a switch; the
actions themselves still go to the provider on the subscription row.

### Stripe configuration (Stripe)

| `.env` variable | Config key | Secret | Meaning |
|---|---|---|---|
| `BILLING_PROVIDER` | `Billing__Provider` | no | `Paddle` (default) or `Stripe`. |
| `STRIPE_API_KEY` | `Billing__Stripe__ApiKey` | yes | Restricted key (`rk_test_...`, `rk_live_...`) preferred; `sk_test_...` and `sk_live_...` are accepted. The prefix decides test or live mode. |
| `STRIPE_WEBHOOK_SECRET` | `Billing__Stripe__WebhookSecret` | yes | The webhook endpoint's signing secret, `whsec_...`. Sandbox and live endpoints have different secrets. |
| `STRIPE_WEBHOOK_SECRET_PREVIOUS` | `Billing__Stripe__WebhookSecretPrevious` | yes | Optional. The previous signing secret, accepted next to the current one during a rotation. |
| `STRIPE_MANAGED_PAYMENTS` | `Billing__Stripe__ManagedPayments` | no | `true` (default, the chosen model) = Stripe Managed Payments; `false` = Stripe Tax (not chosen). See [Managed Payments or Stripe Tax](#managed-payments-or-stripe-tax). |
| `STRIPE_PORTAL_CONFIGURATION_ID` | `Billing__Stripe__PortalConfigurationId` | no | The dedicated Customer portal configuration, `bpc_...`. See [Customer portal (Stripe)](#customer-portal-stripe). |
| `STRIPE_RETURN_BASE_URL` | `Billing__Stripe__ReturnBaseUrl` | no | Public https origin that checkout and the portal return to. It must be an absolute https URL without credentials; `http` is accepted only for `localhost` and `127.0.0.1`. In `docker-compose.yml` it falls back to `PUBLIC_BASE_URL` when blank. When it is missing at request time, checkout and the portal answer `503 billing_not_configured`. |
| `STRIPE_TOPUP_PRICE_500`, `_1000`, `_3000` | `Billing__Stripe__TopUpPriceIds__{credits}` | no | Stripe price ids (`price_...`) of the top-up packs. A blank one means that pack is not sold. |
| `STRIPE_API_BASE` | `Billing__Stripe__BaseUrl` | no | API base URL. Default `https://api.stripe.com`; change it only to point at a mock. With a live key (`sk_live_`, `rk_live_`) it must be exactly `https://api.stripe.com`. |
| none | `Billing__Stripe__ToleranceSeconds` | no | Largest accepted age of a webhook's signed timestamp, in seconds. Default `300`; allowed range 30 to 900. |
| none | `Billing__Stripe__MaxSelfServeSeats` | no | Largest seat count settable from the dashboard. Default `200`. |

Plan prices are not in the environment: they live in `plans.provider_price_ids_json` under a `stripe`
key (`{ "stripe": { "monthly": "price_..." } }`), next to the existing `paddle` key. The
[`tools/stripe-catalog-sync`](../tools/stripe-catalog-sync/README.md) tool creates the Stripe objects and
prints the SQL and the top-up lines.

The Inference API validates the section at startup when `Billing:Provider` is `Stripe` or an API key is
set: the key must start `sk_test_`, `sk_live_`, `rk_test_` or `rk_live_`, the webhook secret (and the
previous one, if set) must start `whsec_`, every `TopUpPriceIds` key must be a positive whole
number of credits, `ToleranceSeconds` must be 30 to 900, `ReturnBaseUrl` must be an absolute https URL
(http only for `localhost` or `127.0.0.1`, no credentials), and a live key requires `BaseUrl` to be
exactly `https://api.stripe.com`. The error text never contains a key. With `BILLING_ENABLED=true` and
`BILLING_PROVIDER=Stripe` (any letter case), `host.sh check-env` requires `STRIPE_API_KEY`,
`STRIPE_WEBHOOK_SECRET`, `STRIPE_RETURN_BASE_URL` (https, no credentials or whitespace),
`STRIPE_PORTAL_CONFIGURATION_ID` (`bpc_`) and the three `STRIPE_TOPUP_PRICE_*` values (`price_`), refuses
`CHANGE_ME` placeholders, and with a live key refuses a `STRIPE_API_BASE` other than
`https://api.stripe.com`. The Paddle variables are then not required.

Every request carries `Stripe-Version: 2026-09-30.endive` and `Authorization: Bearer <key>`. The
version is a constant (`StripeOptions.ApiVersion`), not a setting. Do not rely on the account's default
API version.

### How each provider operation maps to Stripe

| `IBillingProvider` member | What `StripeBillingProvider` does |
|---|---|
| `CreateCheckoutAsync` | Creates a Customer when the organization has none (`POST /v1/customers`, idempotency key `customer:{organizationId}`, metadata `organization_id`). Then `POST /v1/checkout/sessions`: `mode=subscription` (plan) or `payment` (top-up), `ui_mode=hosted_page`, one line item (price, quantity = seats for a per-seat plan, 1 otherwise), `client_reference_id` = organization id, metadata on the session and on `subscription_data` / `payment_intent_data`, `success_url` `…/app/checkout?session_id={CHECKOUT_SESSION_ID}`, `cancel_url` `…/app/settings/billing`. A discount id becomes `discounts[0][coupon]`, or `discounts[0][promotion_code]` when it starts `promo_`. Returns the session URL and the session id as the transaction id. |
| `HasLiveSubscriptionAsync`, `FindLiveSubscriptionIdAsync` | `GET /v1/subscriptions?customer=…&status=all`, paged. Live statuses: `active`, `trialing`, `past_due`, `paused`, `incomplete`. Prefers the subscription whose metadata names the organization; otherwise the first one with no organization metadata. |
| `CreatePortalSessionAsync` | `POST /v1/billing_portal/sessions` for the overview link, plus two more sessions with `flow_data` (`subscription_cancel`, `payment_method_update`) when a subscription id is given. All pass the configured `configuration`. A deep link that fails to mint is logged and left out; the overview link still works. |
| `VerifyAndParseWebhook` | Verifies `Stripe-Signature`, then maps the event. See [Stripe webhooks](#stripe-webhooks). |
| `ResolveAsync` | Completes a verified event with Stripe reads. See [Stripe webhooks](#stripe-webhooks). |
| `CancelSubscriptionAsync` | `POST /v1/subscriptions/{id}` with `cancel_at_period_end=true`. |
| `ResumeSubscriptionAsync` | `POST /v1/subscriptions/{id}` with `cancel_at_period_end=false`. |
| `ChangePlanAsync` | Reads the subscription, then `POST /v1/subscriptions/{id}` with `items[i][id|price|quantity]` and `proration_behavior`. See [Plan changes (Stripe)](#plan-changes-stripe). |
| `GetSubscriptionAsync` | `GET /v1/subscriptions/{id}?expand[]=discounts`: status, items, cancel flag, period, first discount (coupon id and `end`). |
| `SnapshotSubscriptionAsync` | `GET /v1/subscriptions/{id}`, mapped like a webhook. See [Reconcile (Stripe)](#reconcile-stripe). |
| `CompletedUpgradeTransactionsAsync` | `GET /v1/invoices?subscription=…&status=paid&created[gte]=…`, keeping invoices with `billing_reason=subscription_update` and `amount_paid > 0`. |
| `TopUpPriceId`, `TopUpPacks` | From `Billing:Stripe:TopUpPriceIds`. |
| `CreateDiscountAsync` | `POST /v1/coupons` with id `rb_promo_<promotionId>`. See [Discounts and the founder price (Stripe)](#discounts-and-the-founder-price-stripe). |
| `UpdateDiscountAsync` | A logged no-op. See the same section. |
| `PreviewPriceAsync` | `GET /v1/prices/{id}` for the currency, then `POST /v1/invoices/create_preview` with `subscription_details[items]` and the discount. See [Price display (Stripe)](#price-display-stripe). |
| `CancelTransactionAsync` | `POST /v1/checkout/sessions/{id}/expire`. A 4xx answer other than 401, 403 or 429 is `NotCancellable` (already completed, expired or gone); anything else throws and is retried by the caller. |
| `RefundTransactionAsync` | `POST /v1/refunds`. See [Refunds (Stripe)](#refunds-stripe). |

HTTP behaviour (`StripeHttp`): form-encoded bodies in Stripe's nested syntax. `429` and a
`lock_timeout` error mean the request was not processed and are always retried, up to 3 times with
exponential backoff and jitter. A `5xx` answer and a network failure may have been processed, so they
are retried only for a `GET` or a `POST` that carries an `Idempotency-Key`; otherwise the first one
fails the call. A 4xx answer becomes a
`BillingProviderException` whose code is Stripe's `error.code` (or `error.type`); the controller turns
it into `409 billing_provider_refused` with our own wording for the code. Exhausted retries throw
`HttpRequestException`, which the controller answers with `502 billing_provider_error`. Idempotency
keys are sent for customer creation, plan changes and refunds. Stripe keeps a key for 24 hours and
rejects the same key with different parameters
(<https://docs.stripe.com/api/idempotent_requests>). The plan-change key is a fresh random nonce per
call: the update is declarative, so applying it twice is a no-op, and the key only has to make the
retries inside one call safe. A repeated transition (upgrade, downgrade, upgrade) can therefore never be
answered from Stripe's cache of an earlier one.

### Managed Payments or Stripe Tax

`Billing:Stripe:ManagedPayments` is a runtime flag on one provider class, not a second provider.
Managed Payments is the chosen model: `STRIPE_MANAGED_PAYMENTS=true` is the default in `.env.example` and
both compose files, and `host.sh check-env` warns when it is `false`. Stripe Tax mode is available as a
fallback but is not chosen.

| | `ManagedPayments=false` (Stripe Tax, not chosen) | `ManagedPayments=true` (default, chosen) |
|---|---|---|
| Checkout parameter | `automatic_tax[enabled]=true` | `managed_payments[enabled]=true` |
| Merchant of record | You | Stripe (the customer sees "Sold through Link") |
| Tax | You register where required; Stripe Tax calculates | Stripe calculates, collects, files and remits where it covers the country |
| Product `tax_code` | Recommended | Required, from Stripe's eligible list |
| Receipts and invoices | Yours (Stripe invoices and receipts) | Sent by Stripe from Link |
| Refunds and disputes | You | Customers can also ask Stripe; Stripe may refund without you (see [Refunds (Stripe)](#refunds-stripe)) |

The provider never sends `payment_method_types`; Stripe removed it from Checkout Sessions in the
`2026-09-30.endive` API version. With Managed Payments on, it sends none of the parameters Managed Payments forbids (`customer_update`,
`tax_id_collection`, `invoice_creation`, shipping and the rest of the list in
<https://docs.stripe.com/payments/managed-payments/update-checkout>). Managed Payments needs an
eligibility review of the Stripe account and activation in the Dashboard first; see
<https://docs.stripe.com/payments/managed-payments/eligibility> and the switch-over runbook. Because
Managed Payments only covers subscriptions created through Checkout, and this provider creates
subscriptions only that way, nothing else changes in the code path. With Stripe Tax (Managed Payments
off) the Checkout Session carries `automatic_tax[enabled]=true` together with
`customer_update[address]=auto` and `customer_update[name]=auto`, so the address Checkout collects is
saved to an existing customer, which automatic tax needs. Stripe's own Link site lets those
customers change or cancel a subscription without going through us, so the webhook processor accepts
changes it did not start.

### Payment identity (Stripe)

The neutral `BillingEvent` carries these Stripe ids:

| Payment | `TransactionId` | Other |
|---|---|---|
| Top-up | PaymentIntent id (`pi_...`) | `CheckoutId` = Checkout Session id (`cs_...`) |
| Subscription and upgrade payments | Invoice id (`in_...`) | `SubscriptionId` from the invoice's `parent.subscription_details` |
| Refund | Starts as the PaymentIntent id; `ResolveAsync` swaps in the invoice id only when the invoice is a subscription's (a top-up's one-time invoice under Managed Payments stays under the PaymentIntent) | `AdjustmentId` = refund id (`re_...`) |
| Lost dispute | The dispute's PaymentIntent id, resolved like a refund | `AdjustmentId` = dispute id (`du_...` in a real account; recognised by the event type, not the prefix) |

`CreateCheckoutAsync` returns the Checkout Session id as the checkout's provider transaction id, so a
promotion reservation (`PromotionCheckout.ProviderTransactionId`) is found again from a completed
session through `BillingEvent.CheckoutId`, and the checkout sweeper expires an abandoned one with
`CancelTransactionAsync`. A subscription checkout completes through its first invoice
(`billing_reason=subscription_create`), which carries no session id, so `ResolveAsync` looks the session
up with `GET /v1/checkout/sessions?subscription={id}&limit=1` and sets `CheckoutId`. If that read fails
the failure is logged and the reservation is left to the sweeper.

**Which provider cancels.** The sweeper and a supersede cancel each reservation through the provider
that owns its transaction id (`IBillingProvider.OwnsTransaction`): `cs_` is Stripe, `txn_` is Paddle. A
Stripe reservation made before a switch back to Paddle is still expired at Stripe. An id nobody claims
goes to the active provider. A non-`cs_` id handed to Stripe is refused and retried later like any other
cancel failure.

### Stripe webhooks

**Endpoint.** `https://<your domain>/api/v1/billing/webhooks/stripe`. nginx has a dedicated location
for it (`nginx/locations.conf`): no session cookie, any `Authorization` header blanked, no basic-auth
challenge, raw body untouched. The signature is the only credential.

**API version.** Create the endpoint with `api_version` `2026-09-30.endive`, the version the parser
reads. An event whose `api_version` differs is refused with `400 invalid_webhook` (the log says to
recreate the endpoint). An event whose `livemode` does not match the configured key's mode is refused
the same way, so a live endpoint cannot feed a test-key deployment. To move to a newer API version,
create a second endpoint with the new version, change the pinned constant in a release, then retire
the old endpoint (<https://docs.stripe.com/webhooks/versioning>).

**Events to subscribe to.**

| Event | What the provider does |
|---|---|
| `checkout.session.completed`, `checkout.session.async_payment_succeeded` | `mode=payment` with `payment_status=paid` is a `TopUpPaid`; a subscription-mode session is ignored (the subscription events carry the lifecycle). A pack that is not in `TopUpPriceIds` is ignored. |
| `customer.subscription.created`, `.updated`, `.deleted`, `.paused`, `.resumed` | `SubscriptionChanged`. Status map: `active` Active; `trialing` Trialing; `past_due` PastDue; `paused`, `unpaid` and `incomplete` Paused (no entitlements); `canceled` and `incomplete_expired` Canceled. A scheduled cancel (`cancel_at_period_end` or `cancel_at`) sets the cancel flag. `unpaid` follows Stripe's guidance to revoke access once retries are exhausted. |
| `invoice.paid` | `billing_reason` `subscription_update` with `amount_paid > 0` is an `UpgradePaid` (the lines charged above 0 are the new items; the credit line for unused time is never an item). `subscription_create` and `subscription_cycle` are a `TransactionCompleted`. A zero-amount invoice is ignored, except a zero-amount `subscription_create` invoice (a 100%-off first period): it is a `TransactionCompleted` that is not a paid transaction, so it writes no payment row and grants nothing, but it completes the checkout's promotion reservation and records the discount. |
| `refund.created`, `refund.updated` | A refund with `status=succeeded` is a `PaymentRefunded`; `pending`, `requires_action`, `failed` and `canceled` are ignored, and `refund.updated` delivers the later transition to `succeeded`. |
| `charge.dispute.closed` | A dispute with `status=lost` is a `PaymentRefunded` with `AdjustmentId` = the dispute id: the credits are clawed back like a refund (full when the dispute amount covers the charge). Any other closing status (won, for example) is ignored. |
| `charge.dispute.created` | Ignored, with a Warning log line carrying the event and dispute ids. No automatic action; answer the dispute in the Stripe Dashboard. |

Other events can be selected without harm: `charge.refunded` is ignored in favour of `refund.created`,
`invoice.payment_failed` and `invoice.payment_action_required` leave the state to the subscription
event (a failed renewal turns the subscription `past_due`, which `customer.subscription.updated`
already reports), so the endpoint does not need `invoice.payment_failed`.
`checkout.session.async_payment_failed` and `checkout.session.expired` are ignored, and any other
`charge.dispute.*` event is ignored. Ignored events are stored with their reason and answer `200`.

**Signature.** The header is `Stripe-Signature: t=<unix>,v1=<hex>[,v1=<hex>][,v0=<hex>]`. The signed
payload is `"{t}.{raw body}"`, HMAC-SHA256 keyed with the endpoint secret (`whsec_...`), hex encoded,
compared in constant time against every `v1`; other schemes are ignored. The timestamp must be within
`ToleranceSeconds` of now (default 300, Stripe's library default). Source:
<https://docs.stripe.com/webhooks>.

**Rotating the secret.** Roll the signing secret in the Dashboard (Stripe signs with both for up to 24
hours), put the old value in `STRIPE_WEBHOOK_SECRET_PREVIOUS`, the new one in `STRIPE_WEBHOOK_SECRET`,
deploy, and clear `STRIPE_WEBHOOK_SECRET_PREVIOUS` afterwards. The verifier accepts a signature made
with either. Storage and rotation of secrets generally: [`secrets.md`](secrets.md).

**Why `ResolveAsync` reads Stripe.** A Stripe event cannot carry everything the processor needs, so
after verification `ResolveAsync` makes a few reads and returns the completed event:

- `SubscriptionChanged`: re-reads `GET /v1/subscriptions/{id}` and rebuilds the event from the fresh
  object, stamped with the fetch time. Stripe has no per-object `updated_at` and says not to order by
  `event.created`, so a delayed older event carries the latest state and cannot move a subscription
  backwards. The event's `OccurredAt` becomes the fetch time (taken before the read; it is the clock of
  the stale guard), while the webhook's own `created` time is kept in `BillingEvent.ChangedAt` (see
  [Plan changes (Stripe)](#plan-changes-stripe)). A `404` on a deleted or canceled subscription keeps the event as received.
- `UpgradePaid`, `TransactionCompleted`: a discount id that is a `di_...` Discount object is replaced by
  its coupon id (`GET /v1/invoices/{id}?expand[]=discounts`), and the processing fee is read from the
  payment's balance transaction (best effort; `FeeMinor` stays null if the read fails).
- `TopUpPaid`: the fee from the PaymentIntent's latest charge balance transaction, same best effort.
- `PaymentRefunded`: finds the invoice and subscription behind the PaymentIntent
  (`GET /v1/invoice_payments?payment[type]=payment_intent&…`), and whether the refund covers the whole
  charge.

**When Stripe is down.** A failed read in `ResolveAsync` throws before anything is recorded, so the
webhook answers `500` and Stripe retries (up to 3 days in live mode with exponential backoff,
<https://docs.stripe.com/webhooks>). A failure inside the processor is a `500` for the same reason.
Stripe delays automatic collection of renewal invoices while an endpoint keeps failing, so do not leave
a failing endpoint unattended.

### Confirming a checkout on return

The webhook stays the authority, but the buyer should not stare at an unchanged page until it lands (or, if a
webhook never arrives, forever). When Stripe sends the buyer back from a finished checkout the web app calls
`POST /api/v1/billing/checkout/confirm` with the `cs_...` session id from the return URL.

- **What the server does.** `StripeBillingProvider.ConfirmCheckoutAsync` reads `GET /v1/checkout/sessions/{id}`. The session
  must belong to the caller's active organization, compared with the server-set `metadata.organization_id`
  (only that key: a Payment Link lets a buyer set `client_reference_id`, so it is never trusted) and never with anything in the request body; another organization's
  session is `404 not_found`, byte-identical to an unknown id. `status=expired` is `abandoned`; `status=open`, or
  `payment_status=unpaid`, is `pending` and processes nothing (the webhook will land). `status=complete` with
  `payment_status` `paid` or `no_payment_required` is processed: a $0 subscription checkout (a 100% coupon) is
  `no_payment_required` and confirms like any other subscription. A $0 one-time top-up is still ignored by the event mapping
  (the same as its webhook), so it answers `pending`.
- **Same events as the webhooks.** A subscription checkout also reads `GET /v1/subscriptions/{id}` and the checkout's invoice
  (`GET /v1/invoices/{id}`): the session's own `invoice`, else the subscription's latest invoice only while its
  `billing_reason` is `subscription_create`, and only when its `status` is `paid` (a $0 first invoice is paid);
  otherwise no invoice event. Counting the follow-up reads made while resolving the events (coupon, checkout session of the
  subscription), a subscription confirm costs about 5-7 Stripe reads; a top-up uses the session itself. After
  resolving, an event for another organization turns the whole confirmation into `404`.
  The answer is `confirmed` only when at least one event was applied (or was already processed): an all-ignored or unresolved
  confirmation (for example a $0 top-up of a pack that is not configured) answers `pending`. The `confirm:` event-id
  prefix is reserved like `reconcile:` (a signed webhook using it is `400`), and those rows are not counted as real webhooks in the daily report or the backlog gauges. Each fetched object is wrapped in a synthetic Stripe
  envelope (`customer.subscription.updated`, `invoice.paid`, `checkout.session.completed`; `api_version` the pinned
  version, `created` now) with the deterministic id `confirm:{objectId}[:state hash]`, parsed by the webhook mapping
  without the signature step (`StripeEventParser.ParseVerified`), resolved like a webhook, and handed to
  `BillingEventProcessor.ProcessAsync` in order: subscription first, then the invoice (carrying the session id so the promotion
  reservation completes). The rows land in `billing_webhook_events` with provider `stripe`.
- **Idempotent.** A second confirm, or the real webhooks before or after, change nothing. The double-grant protection
  is the plan-refill, payment and redemption keys (and the event id for a repeated confirm); the stale guard only orders
  subscription state. The deterministic ids are why a repeat dedupes.
- **Limits.** At most 10 calls per organization and 10 per user per minute, and 120 per minute for the whole process
  (`429 rate_limited`, in-process sliding windows, idle entries evicted), because each call is several Stripe reads.
  A `confirmed` or `abandoned` answer is cached for 60 seconds per (organization, session) and replayed with no Stripe
  call and no limiter cost; `pending`, `404` and `503` are never cached. A Stripe outage is `503 provider_unavailable`; Stripe's error bodies are never echoed.
- **Paddle** keeps the default (unsupported): a Paddle `txn_` id is `409 confirm_unsupported`.

### Plan changes (Stripe)

`ChangePlanAsync` reads the subscription, matches items by position (an existing item is updated by id,
a surplus one is deleted), and sets `proration_behavior`:

| Change | `PlanChangeTiming` | `proration_behavior` |
|---|---|---|
| Upgrade, seat increase | `Immediately` | `always_invoice`: prorations are invoiced and charged now |
| Downgrade, seat decrease | `NextBillingPeriod` | `none`: the item changes now, no credit is issued, the new price is billed from the next invoice |

`PlanChangeDiscount.Remove` adds `discounts=""`, which clears the subscription's discounts in the same
request. An `Immediately` change also sends `payment_behavior=error_if_incomplete`, so an upgrade whose
payment does not go through is refused by Stripe and changes nothing: the subscription stays on the
lower plan (Stripe's default, `allow_incomplete`, would have moved it to the higher plan and left the
invoice unpaid). The refusal is a 4xx, so the controller answers `409 billing_provider_refused` with a
message that tells the customer to update the payment method or confirm the payment under Manage
billing; the row and the credits are untouched. A `NextBillingPeriod` change charges nothing now and
sends no `payment_behavior`. The credit grant for an upgrade waits for the paid invoice. The invoice's `invoice.paid` event with `billing_reason=subscription_update`
becomes an `UpgradePaid` and pairs with the pending grant described in
[Cancel and plan changes](#cancel-and-plan-changes): `Items` are the invoice lines charged above 0,
`ProrationStartsAt` is the earliest `period.start` of those lines, the amount is `amount_paid`.

**Idempotency.** The `Idempotency-Key` of a plan change is a fresh nonce per call, not derived from the
subscription's items. A declarative update applied twice is a no-op, and a per-state key would let a
later A, B, A, B sequence replay a cached answer.

**`ChangedAt` and `OccurredAt`.** For Stripe, `ResolveAsync` re-reads the subscription, so the event's
`OccurredAt` is the time of that read. A webhook delivered late would then look newer than the upgrade
payment it belongs to. `BillingEvent.ChangedAt` therefore carries the webhook's own `created` time (null
for Paddle). The processor pairs an `UpgradePaid` with its subscription change, and stamps the upgrade's
effective time, on `ChangedAt ?? OccurredAt`, so a late delivery still pairs the upgrade. The stale
guard stays on `OccurredAt`.

If a sandbox shows Managed Payments blocking `always_invoice` (see the checklist below), the fallback
is to bill an upgrade at the next renewal. That fallback is not built.

Stripe documentation: <https://docs.stripe.com/billing/subscriptions/prorations>.

### Customer portal (Stripe)

The platform stays the authority for plan and seat changes, so the portal must not offer them. Create
one dedicated configuration and pass it on every session (`STRIPE_PORTAL_CONFIGURATION_ID`; the
catalog sync tool creates and updates it, found by `metadata[reelbolt_key]=portal`):

- plan switching: off; quantity updates: off;
- payment method update: on; invoice history: on; customer update: on (email, address, name);
- cancellation: on, `mode=at_period_end`, `proration_behavior=none`;
- promotion codes: left at Stripe's default (off).

Without the id the provider logs a warning once and Stripe uses the account's default configuration,
which can allow plan switching. Whatever the customer does in the portal arrives as webhooks, so the
cancel, resume and payment-method flows stay webhook-authoritative. Stripe documentation:
<https://docs.stripe.com/customer-management/configure-portal>.

### Discounts and the founder price (Stripe)

- **One coupon per promotion.** `CreateDiscountAsync` creates the coupon with the custom id
  `rb_promo_<promotionId>` and metadata `promotion_id` and `reelbolt_catalog=1`. `percent_off` or
  `amount_off` + `currency`; `duration` is `once` (no recurrence), `repeating` with
  `duration_in_months` (a limited recurrence) or `forever`; the usage limit is `max_redemptions`, the
  end date `redeem_by`; the name is cut to 40 characters; a restriction to plans becomes
  `applies_to[products]`, resolved through each price's product (one product per plan). If the coupon
  already exists and carries our marker, its id is returned, so a retry is safe.
- **Months, not intervals.** Stripe counts `duration_in_months`; Paddle counts billing intervals. A
  four-interval limit on a yearly plan does not translate one to one.
- **Applied at checkout** with `discounts[0][coupon]` (Checkout accepts one discount). Customers never
  type a Stripe promotion code: the platform validates its own codes and hands the provider a discount id.
- **Immutable.** Stripe only lets you edit a coupon's name, and a promotion code cannot change its
  expiry. `UpdateDiscountAsync` is therefore a logged no-op on Stripe. The platform already checks the
  promotion's status and `EndsAt` before it gives a discount id to checkout, so a paused or ended
  promotion stops being offered without touching Stripe; `redeem_by` is only a second guard. Existing
  subscribers keep their coupon either way. Source: <https://docs.stripe.com/billing/subscriptions/coupons>.
- **Founder price.** The public-alpha founder price is a coupon with the id `rb_founder_alpha` (50% off,
  `duration=forever`, `redeem_by` 31 January 2027 23:59 Europe/Rome, plan products only), managed by the
  catalog sync tool from `founder-offer.json` and linked to the platform's `PlanDiscount` promotion
  through `promotions.provider_discount_id`. `redeem_by` closes the offer to new customers only;
  subscriptions that already carry the coupon keep the discount. See the tool's
  [README](../tools/stripe-catalog-sync/README.md#founder-price-public-alpha). This is the same promotion
  described in [Founder price](#founder-price): when its row is in the promotions export and
  `--founder-promotion-id` names it, the tool creates only `rb_founder_alpha` for it (no second
  `rb_promo_<id>` coupon). A promotion with no explicit plans is bound to the price ids that existed when it
  was created, so Stripe's `applies_to` lists those plans' products: a plan added later on a new product
  keeps a subscriber's coupon attached but is not discounted by it; recreate the promotion and re-run the
  sync when a plan should get the founder price.

### Refunds (Stripe)

`IBillingProvider.RefundTransactionAsync(providerTransactionId, amountMinor, reason, idempotencyKey)` is
a member the interface added for the 14-day money-back promise. The default implementation answers
"not supported" (Paddle keeps it); Stripe issues `POST /v1/refunds` for the PaymentIntent behind the
given session, PaymentIntent or invoice id (`amountMinor` null refunds the whole payment), with
`reason=requested_by_customer` and our reason in metadata, and returns the refund's id, amount,
currency and status. The provider only refunds. Whether a purchase is still inside the 14 days, and
whether it is eligible at all, is decided by the caller. The credit claw-back is not done there: it
follows from the `refund.created` webhook through the usual processor path, so a refund started in the
Dashboard, or by Stripe on its own under Managed Payments, is handled the same way. The claw-back is
idempotent per payment, not per refund event: a top-up is clawed back once, keyed on its transaction
(`clawback:{provider}:{transactionId}`), and an upgrade payment once, through the claw-back marker on
its pending-upgrade row. A lost dispute (`charge.dispute.closed` with `status=lost`) takes the same
path with the dispute id as the adjustment id; the dispute opening (`charge.dispute.created`) is only
logged. Stripe does not return the processing fee of the original charge
(<https://docs.stripe.com/refunds>).

### Reconcile (Stripe)

The lost-webhook sweeper described in [When a Paddle webhook is lost](#when-a-paddle-webhook-is-lost)
works for Stripe subscriptions through `SnapshotSubscriptionAsync` and
`CompletedUpgradeTransactionsAsync`. Stripe has no `updated_at` on a subscription, and the reconciler still works for it: the provider
declares `ReconcilesWithoutUpdatedAt`, so a missing `updated_at` is not `ProviderUnavailable`. The synthetic
event id is `reconcile:sub:{subscriptionId}:{12 hex of a hash of status, items, period end and cancel
flags}:{fetch ticks}` and `reconcile:txn:{invoiceId}` for an upgrade invoice. The ticks make every
snapshot id unique, so an A to B to A change with lost webhooks cannot collide with an id that was
already applied; a snapshot is only processed when the local row has drifted, so this adds at most one
webhook row per drifted subscription per sweep. The snapshot's `ChangedAt` is the proration start of the
newest paid `subscription_update` invoice whose lines equal the subscription's current items (null when
none matches), so an upgrade healed hours after its payment still pairs with that payment. `ProviderUpdatedAt` is null for
Stripe snapshots, so the "is it old enough to be a lost webhook" age is measured from the time the
reconcile watch recorded the change request (`Billing:Reconcile:StuckAfterSeconds` applies as usual).
With no watch time recorded, the difference is not treated as too recent. The synthetic event is stamped
with the fetch time. The reconciler uses the provider named on the subscription row, and with no row the
active one (`BillingProviderSelector`). Top-ups are not reconciled, as with Paddle.

### Price display (Stripe)

Stripe returns integer amounts in a currency's smallest unit. `PreviewPriceAsync` carries Stripe's own
`subtotal`, `total` and discount totals straight through and never computes a price. The
`PricePreview` strings come from `StripeMoneyFormatter`, a pure shift of the decimal point by the
currency's exponent: 0 for the zero-decimal list (BIF, CLP, DJF, GNF, JPY, KMF, KRW, MGA, PYG, RWF,
VND, VUV, XAF, XOF, XPF), 2 otherwise (Stripe sends ISK and UGX with two decimals as well), with `$`,
`€` and `£` for USD, EUR and GBP and the currency code otherwise. The .NET API runs with invariant
globalization, so it cannot produce locale-correct symbols. The preview therefore also returns the raw
`SubtotalMinor`, `DiscountMinor`, `TotalMinor`, unit amounts and `MinorUnitExponent`, and the web app
formats those with `Intl.NumberFormat` (`formatPreviewMoney` in `web/lib/utils/billing.ts`).

The preview may leave tax out: a preview made before checkout does not know the buyer's location, and
whether `create_preview` includes Managed Payments tax is unconfirmed. The dashboard therefore says
"before tax" next to a discounted quote when Stripe is the provider. The price the buyer pays is always
the one Stripe's hosted checkout shows. With Adaptive Pricing (always on under Managed Payments) the
buyer may pay in a local currency; the preview is labelled in the price currency. Stripe documentation:
<https://docs.stripe.com/currencies>.

### Tax and invoices (Stripe)

- **Managed Payments.** Stripe is the merchant of record: it calculates, collects, files and remits
  indirect tax in the countries it covers and sends the customer's receipts and invoices. In countries
  it does not cover you stay responsible. Source:
  <https://docs.stripe.com/payments/managed-payments/tax-compliance>.
- **Stripe Tax (own seller).** You are the seller and the merchant of record. Stripe Tax calculates and
  tracks registration thresholds; registration and filing are yours (Stripe points to partners). The
  `tax_behavior` of a price (inclusive or exclusive) cannot be changed once set, and the catalog sync
  tool defaults to `exclusive`.
- **Questions for your accountant.** Stripe's documentation does not settle these, so they are left
  open here: who issues the legally required invoice for sales to consumers and businesses when
  Managed Payments is on; how an Italian electronic invoice (SdI) obligation applies, and the treatment
  of income through a foreign merchant of record under the flat-rate regime; VAT one-stop-shop (OSS)
  registration when you are the seller; how Stripe's own fees are invoiced to you. Stripe's pages on the
  Italian cases are <https://support.stripe.com/questions/faq-vat-e-invoicing-requirements-in-italy>
  and <https://stripe.com/gb/resources/more/e-invoicing-for-flat-rate-tax-regime-italy>.
- **Hosted invoice links.** `hosted_invoice_url` is available on Stripe invoices, including Managed
  Payments ones (Stripe's Managed Payments changelog, 2026-02-09).

### API keys (Stripe)

Use restricted keys (`rk_`), one for the running service and a separate one for the catalog sync tool,
so a leaked runtime key cannot create prices. Stripe's Dashboard shows each permission as None, Read
or Write; Write includes Read (<https://docs.stripe.com/keys/restricted-api-keys>). Derive the final
list from the request log of a sandbox run; the starting point below follows the calls the code makes.
The exact permission labels in the Dashboard are unconfirmed.

| Key | Permissions to start from |
|---|---|
| Runtime (`STRIPE_API_KEY`) | Checkout Sessions: write. Customers: write. Subscriptions: write. Invoices: write (`create_preview` is a `POST`). Invoice payments, PaymentIntents, Charges, Balance transactions, Prices: read. Customer portal (sessions): write. Coupons: write. Refunds: write. |
| Catalog sync (`STRIPE_API_KEY` of the tool's shell) | Products, Prices, Coupons: write. Customer portal configurations: write. |
| Operator only (never in the service) | Webhook endpoints: write, used by a person in the Dashboard. |

Never print a key or a signing secret, never commit one, and never send one in a ticket or a chat.
Keys are shown once in live mode. Rolling a key keeps the old one valid for a while, so roll, deploy,
then confirm the old key stopped being used. Secret handling for the whole stack is in
[`secrets.md`](secrets.md).

### Testing the Stripe provider

| Layer | What it proves | How to run it |
|---|---|---|
| Hand-signed fixtures | Signature scheme (two `v1`, stale `t`, `v0` only, tampered body), event mapping, processor and controller integration with Stripe-shaped payloads. | `dotnet test --filter "FullyQualifiedName~Billing.Stripe"` from `inference/`. |
| Fake `HttpMessageHandler` | The exact form bodies, `Stripe-Version`, `Idempotency-Key`, retries, error mapping. | Same command (`StripeBillingProviderTests`, `StripeHttpTests`, `StripeResolveTests`). |
| `stripe-mock` lint | Request shape only: wrong or removed parameter names and types. It is stateless and cannot judge flows, proration, statuses or webhooks. | `docker run --rm -p 12111:12111 stripe/stripe-mock:latest`, then `STRIPE_MOCK_URL=http://localhost:12111` and the same `dotnet test` command; without the variable the lint tests are skipped. Not part of the default gate. |
| End-to-end fake | The whole path through the browser: hosted checkout, signed webhooks, cancel and resume, seats, refund, lost webhooks. | `e2e/scripts/up-isolated.sh --provider stripe`; see "Stripe fake" in [`e2e/README.md`](../e2e/README.md#stripe-fake). |
| Catalog sync tests | Desired state, diff, safety rules, against a fake Stripe. | `cd tools/stripe-catalog-sync && node --test` (CI job `stripe-catalog-sync`). |
| Real sandbox | The assumptions below. Nothing above talks to Stripe. | The rehearsal in `billing-stripe-switch-over.md`. |

The fixtures are shaped after Stripe's documentation, not recorded from a sandbox, and the end-to-end
fake follows the same documented shapes; it is not Stripe.

### Branding (Stripe)

Where each Stripe surface takes its look from:

| Surface | Source |
|---|---|
| Checkout page | Per-session `branding_settings` that the provider sends (`Billing:Stripe:Branding:*`, env `STRIPE_BRANDING_*`); a value left blank falls back to the Dashboard branding |
| Invoices, receipts, emails | Account branding in the Dashboard (the API cannot set it for your own account: "you may only use it on connected accounts") |
| Customer portal | Account branding for logo and colours; headline, privacy and terms links from the portal configuration (`business_profile`, set by the catalog sync tool) |

The ReelBolt values and where they come from are in [`brand.md`](brand.md#stripe-checkout-and-the-customer-portal):
background `#0B0B12`, button `#7C3AED`, `rectangular` corners, font `chakra_petch`, name `ReelBolt`, and a logo and
icon fetched from `https://reelbolt.ai/brand/stripe-logo.png` and `stripe-icon.png`. The logo and icon must be
reachable on the site before the first checkout that uses them. The provider sends `branding_settings` in both
tax modes; the sandbox accepted it together with `managed_payments[enabled]=true`.

What to expect under Managed Payments: the page is Link's. The left (brand) panel follows `branding_settings`
(background, logo, font, name); the right pay panel keeps Link's own styling (its green pay button), so
`button_color` does not recolour it. A light background hides the full-colour lockup, so keep the dark `paper`
background. The portal and the receipts take the account branding, not the per-session values.

### Stripe wire assumptions: the sandbox pass

The assumptions the provider makes about Stripe were checked once against a real Stripe sandbox on
2026-10-26 (API version `2026-09-30.endive`, Managed Payments on, the stack running the provider through
local staging with the Stripe CLI forwarding webhooks, a 4242 card, the catalog sync applied). Result per line;
anything that differed was fixed in the code, and the live cutover repeats the same pass once with the live key.

1. **Bearer authentication: confirmed.** `Authorization: Bearer <key>` is accepted (HTTP Basic works too).
2. **Managed Payments and `always_invoice`: confirmed.** An upgrade produced a paid invoice with
   `billing_reason=subscription_update`; the prorated credits were granted once.
3. **Managed Payments with coupons and quantities: confirmed.** Seats (quantity 3) with a coupon in `discounts`
   are accepted; the discount applies to the net price and tax is added on top.
4. **Discount object fields: confirmed.** A subscription's Discount has `source: {type: "coupon", coupon: "<id>"}`,
   `start` and `end` (`null` for a `forever` coupon).
5. **`invoice_payments`: filters confirmed, the deep expansion was wrong.** `invoice`, `payment[type]`,
   `payment[payment_intent]` and `status` work. Stripe refuses an expansion deeper than four levels
   (`data.payment.payment_intent.latest_charge.balance_transaction`): the fee is now read in two shallow requests.
6. **`create_preview`: confirmed.** It returns the fields the preview reads; the preview total did not include tax.
7. **`checkout.session.expired`: confirmed** as the event name (the provider ignores it).
8. **Fees under Managed Payments: differed, fixed.** `balance_transaction.fee` includes the tax Stripe withholds
   (a `withheld_tax` entry in `fee_details`: a 1,769 payment had `fee` 393 = 75 + 32 + 286 withheld tax). `FeeMinor`
   now excludes `withheld_tax` entries.
9. **Portal deep links: confirmed.** The cancel flow and payment-method flow open on the dedicated
   configuration (cancellation enabled on it).
10. **Expire status code: confirmed.** Expiring a completed or expired session answers `400`
    `invalid_request_error` with no `code`; an open session becomes `expired`.
11. **`resource_already_exists`: confirmed** for a coupon whose custom id exists.
12. **Refund effect on subscriptions: confirmed.** A refund leaves the subscription active; credits are clawed
    back once from `refund.created` (the later `refund.updated` is a no-op).
13. **`api_version` on delivered events: confirmed.** Events forwarded by `stripe listen --latest` carry
    `2026-09-30.endive` and are accepted; without `--latest` the account default version applies and would be refused.
14. **Idempotency on `POST /v1/refunds`: not exercised.**
15. **`error_if_incomplete` with Managed Payments: accepted** on a paid upgrade; the declined-card refusal
    was not exercised against Stripe (covered by the unit tests and the e2e fake).
16. **Dispute payload: differed, fixed.** Dispute ids start with `du_` (not `dp_`), which hid the lost-dispute
    claw-back until fixed; `charge.dispute.created` and `.closed` carry `status`, `amount`, `payment_intent`.
    A lost dispute (`4000000000000259`, then `evidence[uncategorized_text]=losing_evidence`) claws the top-up back.
17. **Checkout Session by subscription: confirmed**; the promotion reservation of a subscription checkout is completed.
18. **`customer_update` with `automatic_tax`: confirmed.** Without Managed Payments a session for an existing
    customer accepts `customer_update[address|name]=auto` next to `automatic_tax`.

Also learned in the pass: the sandbox has Managed Payments on by default and refuses a product without a tax
code; a coupon's `applies_to` is only returned when expanded (the catalog sync reads it with `expand[]=applies_to`);
Managed Payments creates an invoice (`billing_reason=manual`) for a one-time top-up, which the platform ignores,
so a top-up refund stays under its PaymentIntent; amounts the platform records include the tax charged; and
webhook order is not fixed (`invoice.paid` before `checkout.session.completed`, `charge.dispute.created` before the
payment's own events).

## Testing without a Paddle account

Automated tests (`ReelBolt.WorkflowEngine.Tests/Billing`) cover the processor on Sqlite, the
controller over a fake provider, the Paddle provider over a stubbed `HttpClient`, and signed
payloads end to end through the real controller. The payloads are synthetic, shaped after
Paddle's documentation, not recorded from a live sandbox.

Checkout, portal, cancel, resume, upgrade and downgrade are also exercised end to end, through the
browser, against a local Paddle mock that sends signed webhooks back: the isolated e2e stack
(`docker-compose.e2e.yml`, `e2e/scripts/up-isolated.sh`), documented in
[`e2e/README.md`](../e2e/README.md). The mock follows Paddle's documented shapes; it is not Paddle.

Lost webhooks can be reproduced on the same stack: `POST /__mock/webhooks/drop` loses the next
matching webhooks on purpose, and `POST /__mock/webhooks/{deliveryId}/redeliver` sends a recorded
delivery again with its original `event_id`, which is what a Paddle dashboard replay does. The stack
runs the reconcile sweep fast (`Billing__Reconcile__StuckAfterSeconds` 8,
`SweepIntervalSeconds` 4). The routes are in the "Paddle mock" section of
[`e2e/README.md`](../e2e/README.md#paddle-mock).

**Still not run against the real Paddle sandbox.** That needs a Paddle sandbox account, a
notification destination pointing at a reachable `/api/v1/billing/webhooks/paddle`, and the price
ids below. Verify there before launch:

- the portal deep-link field names (`data.urls.subscriptions[].cancel_subscription` and
  `.update_subscription_payment_method`), and that they come back when `subscription_ids` is sent;
- the error code Paddle returns for an items change while a cancel is scheduled (the mock answers
  `400 subscription_locked_pending_changes`; the endpoint already refuses that case with
  `409 cancel_scheduled` before calling Paddle);
- that a `full_next_billing_period` downgrade arrives as a same-period `subscription.updated` and the
  renewal as an event with a new `current_billing_period.starts_at`.

## Migrating from sandbox to live

This is the one-time move of the account from the sandbox Paddle account to the live one, for the
operator who holds the `paddle-live` MCP tools. Run it top to bottom, once. Every step ends in a
value you can check. It is not a code change — the code is already environment-driven, so the switch
is data and configuration (see [Switching billing on](#switching-billing-on)). The general,
provider-agnostic setup is in [Operator setup checklist](#operator-setup-checklist); this section is
the live-specific runbook and does not repeat it.

### Before you start

These guardrails are binding. The live account is real money and real customers, and most mistakes
here cannot be undone by deleting something.

- **Additive only.** Create live entities; do not change one that already exists.
- **Never delete, archive, deactivate or recreate a live entity** — product, price, discount, token,
  customer or notification destination.
- **Never recreate a notification destination.** Removing and re-adding one rotates its
  `endpoint_secret_key` and silently breaks every future webhook delivery. Reuse the existing
  destination and read its secret.
- **Prices are immutable once used.** A price that has been charged cannot be edited. Fix a wrong
  price by creating a new one and pointing the code at it, never by editing the old one.
- **Never modify the sandbox account.** The sandbox ids below are reference only. Do not create,
  change or delete anything in sandbox while you do this.

### The mapping

The sandbox catalog is the shape that was copied into live. The sandbox ids are reference only; the
live ids that were created from them are in [What already exists](#what-already-exists-2026-10-18).

Products — four, all `active`, `standard`:

| Product | Sandbox id |
|---|---|
| ReelBolt Creator | `pro_REDACTED` |
| ReelBolt Pro | `pro_REDACTED` |
| ReelBolt Team | `pro_REDACTED` |
| ReelBolt Top-up Credits | `pro_REDACTED` |

Prices — six, all `active`, `standard`, USD:

| Price | Product | Amount (minor units) | Cycle | Quantity |
|---|---|---|---|---|
| `pri_REDACTED` | Creator | `2900` | month ×1 | 1–100 |
| `pri_REDACTED` | Pro | `7900` | month ×1 | 1–100 |
| `pri_REDACTED` | Team | `4900` | month ×1 | **3**–1000 |
| `pri_REDACTED` | Top-up 500 credits | `1800` | none | 1–100 |
| `pri_REDACTED` | Top-up 1,000 credits | `3500` | none | 1–100 |
| `pri_REDACTED` | Top-up 3,000 credits | `10500` | none | 1–100 |

Skip the one sandbox discount, `dsc_REDACTED` (description `test`,
`enabled_for_checkout: false`). It is junk; do not recreate it. The live launch promo is separate
work.

### What already exists (2026-10-18)

The live catalog was created once, through the `paddle-live` MCP, on 2026-10-18. Every
entity is new; nothing existing was touched. **These exist — do not re-run the create snippets
below; doing so would create a second set of products and prices.** The snippets stay as the record
of how it was done.

| Entity | Live id |
|---|---|
| ReelBolt Creator (product) | `pro_REDACTED` |
| ReelBolt Pro (product) | `pro_REDACTED` |
| ReelBolt Team (product) | `pro_REDACTED` |
| ReelBolt Top-up Credits (product) | `pro_REDACTED` |
| Creator monthly price | `pri_REDACTED` |
| Pro monthly price | `pri_REDACTED` |
| Team monthly price (per seat, minimum 3) | `pri_REDACTED` |
| Top-up 500 credits price | `pri_REDACTED` |
| Top-up 1,000 credits price | `pri_REDACTED` |
| Top-up 3,000 credits price | `pri_REDACTED` |
| Client-side token ("ReelBolt Checkout") | `live_ad207fef033e40d64e5ccd8ad38` |
| Notification destination | `ntfset_REDACTED` |

The client-side token is publishable and safe to hand to the browser. The notification destination's
signing secret is not — see [The notification destination](#the-notification-destination). The junk
`test` discount was **not** copied: the live account has no discounts.

### Create the live catalog — how it was done

This is the procedure that created the live catalog above, run once on 2026-10-18. Do not run it
again. Each snippet is a paste-ready `execute` argument: an async arrow function that receives the
API client and returns the ids. **The ids they returned are new live ids.** They have nothing to do
with the sandbox ids in the mapping table — the sandbox ids are there only so you can match name,
amount, cycle and quantity minimum.

**Creator — product and its monthly price**

```js
async (paddle) => {
  const { data: product } = await paddle.products.create({
    name: "ReelBolt Creator",
    tax_category: "standard"
  });
  const { data: price } = await paddle.prices.create({
    product_id: product.id,
    description: "ReelBolt Creator monthly",
    unit_price: { amount: "2900", currency_code: "USD" },
    billing_cycle: { interval: "month", frequency: 1 },
    quantity: { minimum: 1, maximum: 100 }
  });
  return { product: product.id, monthly: price.id };
}
```

**Pro — product and its monthly price**

```js
async (paddle) => {
  const { data: product } = await paddle.products.create({
    name: "ReelBolt Pro",
    tax_category: "standard"
  });
  const { data: price } = await paddle.prices.create({
    product_id: product.id,
    description: "ReelBolt Pro monthly",
    unit_price: { amount: "7900", currency_code: "USD" },
    billing_cycle: { interval: "month", frequency: 1 },
    quantity: { minimum: 1, maximum: 100 }
  });
  return { product: product.id, monthly: price.id };
}
```

**Team — product and its monthly price, minimum three seats**

```js
async (paddle) => {
  const { data: product } = await paddle.products.create({
    name: "ReelBolt Team",
    tax_category: "standard"
  });
  const { data: price } = await paddle.prices.create({
    product_id: product.id,
    description: "ReelBolt Team monthly per seat",
    unit_price: { amount: "4900", currency_code: "USD" },
    billing_cycle: { interval: "month", frequency: 1 },
    quantity: { minimum: 3, maximum: 1000 }
  });
  return { product: product.id, monthly: price.id };
}
```

**Top-up Credits — product and its three one-time prices**

```js
async (paddle) => {
  const { data: product } = await paddle.products.create({
    name: "ReelBolt Top-up Credits",
    tax_category: "standard"
  });
  const { data: p500 } = await paddle.prices.create({
    product_id: product.id,
    description: "ReelBolt Top-up 500 credits",
    unit_price: { amount: "1800", currency_code: "USD" },
    quantity: { minimum: 1, maximum: 100 }
  });
  const { data: p1000 } = await paddle.prices.create({
    product_id: product.id,
    description: "ReelBolt Top-up 1,000 credits",
    unit_price: { amount: "3500", currency_code: "USD" },
    quantity: { minimum: 1, maximum: 100 }
  });
  const { data: p3000 } = await paddle.prices.create({
    product_id: product.id,
    description: "ReelBolt Top-up 3,000 credits",
    unit_price: { amount: "10500", currency_code: "USD" },
    quantity: { minimum: 1, maximum: 100 }
  });
  return { product: product.id, p500: p500.id, p1000: p1000.id, p3000: p3000.id };
}
```

**Client token — one `live_` token**

```js
async (paddle) => {
  const { data: token } = await paddle.clientTokens.create({
    name: "ReelBolt Checkout",
    description: "Embedded checkout and customer portal for the ReelBolt web app"
  });
  return token.token;
}
```

The token create returns the token in `data.token`, not `data.id`.

### The notification destination

**Decided (2026-10-18).** The Paddle dashboard showed that the
live account had **no** notification destination, so exactly one was created at
`https://reelbolt.ai/api/v1/billing/webhooks/paddle`, subscribed to the same events the existing
setup checklist names (`subscription.*`, `transaction.completed`, `adjustment.*`). Because none
existed, the guardrail against recreating one is not engaged. It still governs every future change:
never recreate a destination — recreating rotates `endpoint_secret_key` and silently breaks every
future delivery.

**It now exists — do not create another.** The live destination is
`ntfset_REDACTED` (`traffic_source: platform`, 56 events). Its signing secret is
held **outside the repository** — the operator holds it in a private env file (mode 600, on the operator's workstation) — and it is visible in the Paddle dashboard. **Never copy the secret
value into the repo, a report, a PR or a chat message; reference where it lives only.**

**The write scope landed.** The `paddle-live` OAuth connection was read-only (`product.write` not
granted) until the write scopes were granted on 2026-10-18; with them granted, the catalog and
this destination were created and nothing existing was touched.

**The destination host must serve the route — a green create is not a green webhook.** A read-only check
on 2026-10-18 found that `reelbolt.ai` was then answering as a marketing-only static host (superseded: on 2026-11-24 the apex is served by the control box's nginx and the route answers `400`). The 2026-10-18 reading was:
`/app` answers `302` (to `/contact`), `/health` and `/api/v1/health` answer `404`, and a `POST` to
`/api/v1/billing/webhooks/paddle` answers `405` from that static host, not from the app. `www.reelbolt.ai`
answers `522` (Cloudflare cannot reach an origin), and `app.reelbolt.ai` and `api.reelbolt.ai` do not
resolve in DNS. The only host serving
the app and the API is the staging host, whose webhook route answers `400` with
`{"error":"invalid_webhook"}` — a signature rejection, which is the correct answer to an unsigned
request. **No production host serves the route today.** The destination was created anyway — it is
additive and the right long-term target — but it **receives nothing until a production host serves
the route**, and a green "destination created" is not a green webhook. Verify the host and require a
signature rejection — never a `404`, `405` or `302`:

```sh
curl -sS -o /dev/null -w '%{http_code}\n' -X POST -H 'Content-Type: application/json' \
  -d '{}' https://<live-host>/api/v1/billing/webhooks/paddle
```

Make a production host serve the route in one of two ways, so the destination actually receives
deliveries:

- **(a) Give the production deployment its own hostname** and serve the whole stack there, and point
  the destination at that host.
- **(b) Point `reelbolt.ai` itself at the control box** (a proxied DNS record to its nginx, which also
  serves the marketing site). `reelbolt.ai` then serves the route. This is the current setup.

**The checkout success URL must resolve too.** `checkoutSuccessUrl(origin)` returns
`{origin}/app/checkout`, so a checkout opened from `reelbolt.ai` today returns the buyer to
`reelbolt.ai/app/checkout`, and `/app` redirects to `/contact`. The app paths (`/app` and the API
under `/api`) must resolve on the live domain before a live checkout is opened.

The create call that was used (already done — do not run it again):

```js
async (paddle) => {
  const { data } = await paddle.notificationSettings.create({
    description: "ReelBolt production webhooks",
    type: "url",
    destination: "https://reelbolt.ai/api/v1/billing/webhooks/paddle",
    subscribed_events: [
      "subscription.created",
      "subscription.activated",
      "subscription.updated",
      "subscription.canceled",
      "subscription.past_due",
      "subscription.paused",
      "subscription.resumed",
      "subscription.trialing",
      "transaction.completed",
      "adjustment.created",
      "adjustment.updated"
    ]
  });
  return data.endpoint_secret_key;
}
```

`endpoint_secret_key` is returned **once**, in that create response, and cannot be retrieved again —
capture it from that response and nowhere else. That value is what goes into `PADDLE_WEBHOOK_SECRET`.
The webhook route, its signature and its state machine are in
[Webhooks are authoritative](#webhooks-are-authoritative) and
[Subscription state machine](#subscription-state-machine); this section does not repeat them.

### Wire the ids into the deployment

Use the live ids from [What already exists](#what-already-exists-2026-10-18).

- **Plan prices** — into `plans.provider_price_ids_json`, one row each:
  - `creator` → `pri_REDACTED`
  - `pro` → `pri_REDACTED`
  - `team` → `pri_REDACTED`
- **Top-up prices** — `PADDLE_TOPUP_PRICE_500` → `pri_REDACTED`,
  `PADDLE_TOPUP_PRICE_1000` → `pri_REDACTED`, `PADDLE_TOPUP_PRICE_3000` →
  `pri_REDACTED`.
- **Client token** — `PADDLE_CLIENT_TOKEN` → `live_ad207fef033e40d64e5ccd8ad38`.
- **Webhook secret** — the destination's `endpoint_secret_key` into `PADDLE_WEBHOOK_SECRET`; the
  value is held outside the repository, not in this file (see
  [The notification destination](#the-notification-destination)).
- **Environment** — `PADDLE_ENVIRONMENT=live`.

The plan-price update, run once per plan with that plan's id (this is the `creator` row):

```sql
UPDATE plans SET provider_price_ids_json = '{"paddle":{"monthly":"pri_REDACTED"}}' WHERE key = 'creator';
```

Then check that the control host and the API agree:

```sh
infra/compose/host.sh check-env
docker compose exec inference printenv Billing__Paddle__Environment
```

The first refuses to start with `BILLING_ENABLED=true` when a Paddle secret is blank or still
`CHANGE_ME`; the second must print `live`. The variables themselves are in
[Switching billing on](#switching-billing-on).

### Dashboard-only steps the operator owns

Four things live only in the Paddle dashboard and cannot be done from the API:

| Step | Where it lives |
|---|---|
| Payment methods | Checkout > Checkout configuration |
| Default payment link | Checkout > Checkout configuration — must be a real, approved domain, not localhost |
| Submit domains for approval | My account > Settings > Website approval — do it now, so it processes during verification |
| Payout bank details | Business account > Payouts > Payout settings |

### Webhook IP allowlist

The webhook route can additionally restrict callers to Paddle's published source IPs. It is off by
default; switch it on with `PADDLE_WEBHOOK_IP_ALLOWLIST=true`.

- It fetches `https://api.paddle.com/ips` and uses the `data.ipv4_cidrs` list. The list is **never
  hard-coded** — it is fetched and refreshed on the nginx reload loop, so a rotated Paddle range is
  picked up without a restart.
- It **fails open**: if the fetch fails, no allowlist is written, the allowlist stays empty and the
  signature HMAC keeps guarding the route. A network blip must not stop live payments.
- It is only meaningful when `REAL_IP_FROM_CLOUDFLARE=true` restores the real client address.
  Without it, nginx sees Cloudflare's address and the allowlist would reject Paddle. The signature
  check is the credential either way; the allowlist is a second layer.

### What this does not do

This section makes the account ready. It does **not** start Paddle's verification and it does **not**
take a payment. Live payments cannot be taken until verification passes and the domains are approved.
Do not open the live build to real customers until then.

### Rollback

To go back, point `PADDLE_ENVIRONMENT` at `sandbox` and restore the sandbox price ids in
`plans.provider_price_ids_json` and the `PADDLE_TOPUP_PRICE_*` variables. A live entity is **never**
deleted, archived or deactivated to roll back — rollback is a configuration change only, and the
live entities stay in place for the next attempt.

## Operator setup checklist

This checklist is for Paddle. For Stripe see [Stripe](#stripe) and
`billing-stripe-switch-over.md`.

1. Create a Paddle account (live) and a sandbox account (separate; they share nothing).
2. In each, create a product and a monthly price for Creator, Pro and Team (Team is per seat, quantity
   minimum 3), and one one-time price per top-up pack (500, 1,000 and 3,000 credits).
3. Set a default payment link in Checkout settings and approve your domain.
4. Create an API key and a notification destination at
   `https://<your domain>/api/v1/billing/webhooks/paddle` subscribed to the `subscription.*`,
   `transaction.completed` and `adjustment.*` events. Copy its secret key.
5. Put the price ids into `plans.provider_price_ids_json` per environment, for example
   `UPDATE plans SET provider_price_ids_json = '{"paddle":{"monthly":"pri_..."}}' WHERE key = 'creator'`,
   and set the three `PADDLE_TOPUP_PRICE_*` variables.
6. Set `BILLING_ENABLED=true`, `PADDLE_API_KEY`, `PADDLE_WEBHOOK_SECRET`, `PADDLE_ENVIRONMENT` and
   `PADDLE_CLIENT_TOKEN` (the client-side token of the **same** account; blank is allowed and leaves
   the hosted checkout working without the embedded one).
7. Run a sandbox purchase and watch `billing_webhook_events` and the credit ledger.
8. Create a 20% `PlanDiscount` code on `/app/admin/promotions`, apply it on the billing page and
   complete a sandbox purchase through the overlay. Confirm that Paddle charged the discounted
   amount and that the redemptions list shows a `DiscountApplied` row. If the overlay showed the
   full price, set `Billing__Paddle__DiscountsEnabledForCheckout=true`, create the promotion again
   and repeat.

Without a sandbox, the shared isolated e2e stack's Paddle mock answers the discount, pricing-preview
and discounted-transaction calls and records what the Inference API sent; see
[`e2e/README.md`](../e2e/README.md#promotions-endpoints). The mock does not implement
`PATCH /transactions/{id}` yet: it answers `404 not_found`, which the API treats as a transaction
past cancelling, so the e2e stack marks stale checkouts settled without cancelling them.

## The reconciliation drift report and runbook

B12. The self-heal above answers "was this one webhook lost?"; the drift report answers "does the
ledger still disagree with Paddle anywhere, and is anyone looking?" Every reconcile the sweep runs is
classified into a **named** finding, so a difference is never silent. The code is in
`ReelBolt.Inference.Api/Services/Billing/Reconcile/` (`BillingDriftReport`, `BillingReconcileSweeper`).

### How to read the report

After each sweep (see [Sweep](#how-the-self-heal-works)) the sweep builds a `BillingDriftReport` from
the results of the organizations it checked, plus the ledger's unattributed `usage_events`:

- **A named finding per residual difference.** A difference the reconcile *resolved* is not a
  finding: a `Healed` run (a lost webhook was replayed), an `InSync` run, a change still too young to
  judge (`TooRecent`), an organization with nothing to reconcile, or one another replica owns
  (`Busy`) all leave no finding. A finding is raised for:
  - `subscription_missing` — Paddle has a live subscription the ledger has no row for, and the
    processor did not apply it (a lost `subscription.created` that did not heal);
  - `ledger_only` — the ledger holds a provider subscription Paddle answers `404` for (the reverse);
  - `transaction_unapplied` — Paddle holds a completed upgrade payment the processor did not apply;
  - `plan`, `seats`, `scheduled_change`, `period`, `status`, `cancel_at_period_end` — a drift the
    webhook processor would not apply (outcome `Stale`, "Review manually");
  - `unreadable` — Paddle could not be read, so the comparison could not be made at all. This is
    **not** evidence of drift; it is the honest "we could not compare".
- **Where an operator sees it.** A Warning log with EventId `7109 billing.drift_detected` (and
  `7110 billing.usage_unattributed` for the count alone), the `billing_drift` alert POSTed to
  `Billing:SpendReport:AlertWebhookUrl` at most once an hour (see [Alerts](#alerts)), and the gauges
  `reelbolt.billing.reconcile.drift_findings` and `reelbolt.billing.usage.unattributed` (see
  [observability.md](observability.md#billing-webhooks-and-reconcile)). The per-organization view is
  the admin panel's "Waiting on Paddle" list ([How to see it](#how-to-see-it)).
- **The unattributed count.** `usage_events` rows with no attributable organization or user
  (`source = Unattributed`, on the platform org) are a metering bug: a metered call must always have
  a usage scope. The report counts them over the last 24 hours; the same figures, sliced by source and
  by missing org/user, are available from `BillingUsageAttribution.QueryAsync`.

### What to do with each drift class

| Class | What it means | What to do |
|---|---|---|
| `subscription_missing` | Paddle bills a subscription the ledger has no row for | Resync the organization ([How to resync](#how-to-resync)). If it stays, the `billing_customers` row is missing or names the wrong customer; find the `subscription.created` event in Paddle and replay it. |
| `ledger_only` | The ledger holds a provider subscription Paddle answers `404` for | Check the row's `provider_subscription_id` and whether the organization has a second, live subscription. Resync. If Paddle genuinely has no such subscription, the row is stale — the ledger may need a compensating correction (never an update in place). |
| `transaction_unapplied` | Paddle holds a completed upgrade payment the ledger did not record | Look for the `transaction.completed` event in Paddle's notifications and replay it. The `upgrade:…` ledger key keeps a repeat from granting twice. Check `billing_pending_upgrade_grants` for a grant the pairing left behind. |
| `plan` / `seats` / `period` / `status` / `cancel_at_period_end` / `scheduled_change` | Paddle's live subscription still differs and the processor would not apply it (stale, duplicate or unresolved) | Resync the organization. If it persists, the synthetic event is being refused as stale: check `billing_webhook_events` for the organization and read the "Review manually" `7102` log line. |
| `unreadable` | Paddle could not be read; the comparison did not happen | Check Paddle reachability and `PADDLE_API_KEY`. Not drift; it clears itself on the next sweep that can read Paddle. |

The unattributed-usage count is chased the same way: find the metered call that ran without a usage
scope (the recorder logs a Warning with the kind, provider and model when it writes such a row) and
give it one.

### What the reconciler deliberately does not do

- It **grants no credits of its own.** A credit it causes is the one the lost webhook would have
  granted, under the same ledger key; an upgrade whose paid proration transaction Paddle does not
  hold grants nothing.
- **Top-ups are not reconciled.** A top-up creates no subscription and no watch, so a lost top-up
  `transaction.completed` is never healed automatically — replay it from the dashboard.
- It **never writes to Paddle** and never PATCHes a subscription or its discount: it reads, then
  feeds the provider's own state through the webhook processor.
- It does **not list all of a customer's transactions**, only the subscription's completed upgrade
  payments since the period start minus one day. So the reverse of `transaction_unapplied` — a
  ledger grant with no Paddle transaction behind it — is not detected here.
- It **never rolls a schema back**: migrations are forward-only (expand/contract).
- It does not pause, block or refuse anything. The report is reporting only; the operator's brake is
  `compute_paused` and the automatic one is the credit gate.
