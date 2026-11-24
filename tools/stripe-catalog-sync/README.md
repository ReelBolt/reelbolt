# stripe-catalog-sync

Creates and maintains the Stripe side of the ReelBolt price catalog: products, prices, an
optional coupon per promotion, and one Billing Portal configuration. It reads the canonical
`inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json`, so no amount is written in
this tool. It is a standalone Node script (Node 22+, no npm dependencies) and it never touches
our database: it prints the SQL and environment lines for you to review and run.

## Quick start

```bash
cd tools/stripe-catalog-sync
export STRIPE_API_KEY=rk_test_...          # restricted key preferred; read from the environment only

node sync.mjs                              # dry run: reads Stripe, writes nothing, prints the diff
node sync.mjs --apply --out report.json    # perform the writes (test mode)
node --test                                # unit tests (fake Stripe, no network)
```

A dry run is the default. It sends `GET` requests only; the client itself refuses to send
anything else in that mode.

Exit codes: `0` in sync, `1` error (including any refused item), `2` differences found in a
dry run (or unresolved coupon drift after `--apply`).

## What it manages

| Stripe object | Identity | Source |
| --- | --- | --- |
| Product `rb_plan_<planId>` | custom product id | each `subscription` / `perSeat` plan ("ReelBolt Creator") |
| Product `rb_topup_<credits>` | custom product id | each `topUp.packs[]` ("ReelBolt 500 credits") |
| Price `rb:<plan>:month:<currency>` | `lookup_key` | `priceMonthly`; a per-seat plan is a per-unit price (quantity = seats), `minSeats` goes to metadata only |
| Price `rb:topup:<credits>:<currency>` | `lookup_key` | `topUp.packs[].price`, one-time |
| Coupon `rb_promo_<promotionId>` | custom coupon id | `--promotions` export |
| Billing Portal configuration | metadata `reelbolt_key=portal` | built in (see below) |

`trial` and `contact` (Enterprise) plans have no provider price and are listed as `skip`.
Identity is always the custom id or the `lookup_key`, never the name. `--lookup-prefix`
(default `rb`) changes the prefix of all of those.

Amounts convert from the catalog's major units to minor units with integer math. A value that
is not exactly representable (0.001 USD) is rejected, as are zero and negatives. Zero-decimal
currencies (JPY and friends) use exponent 0; three-decimal currencies are refused.

## Flags

| Flag | Meaning |
| --- | --- |
| `--apply` | perform the writes (default is a dry run) |
| `--i-mean-live` | required with `--apply` when the key is a live key |
| `--catalog <file>` | catalog path (default: the one in this repository) |
| `--promotions <file>` | promotions export, see below |
| `--include-inactive` | also sync `Paused` / `Archived` promotions |
| `--recreate-coupons` | delete and re-create our coupons whose definition drifted |
| `--tax-behavior exclusive\|inclusive\|unspecified` | default `exclusive` (the catalog says prices exclude VAT) |
| `--tax-code txcd_...` | product tax code, set on every product (default with Managed Payments: `txcd_10105002`, AI as a service, business use) |
| `--managed-payments` | the default and the chosen tax model: Stripe is the seller of record. Puts a tax code on every product and prints the reminder that Managed Payments must be activated and approved in the Dashboard |
| `--no-managed-payments` | NOT the chosen model: plain Stripe with Stripe Tax, you are the seller; tax code optional |
| `--founder` / `--no-founder` | manage the public-alpha founder coupon from `founder-offer.json` (default on) |
| `--founder-promotion-id <uuid>` | emit the UPDATE linking that promotion row to the coupon |
| `--site-url <https origin>` | default `https://reelbolt.ai`: the portal's privacy and terms links are `<site>/legal/privacy` and `<site>/legal/terms` |
| `--skip-portal` | do not manage the portal configuration |
| `--lookup-prefix <p>` | default `rb` |
| `--out <file>` | machine-readable report: ids and diff, no secrets |
| `--sql-out <file>` | write the SQL section to a file |

Environment: `STRIPE_API_KEY` (required), `STRIPE_API_BASE` (optional, see safety model).

## Safety model

- **Dry run by default.** Writes need `--apply`. A live key (`sk_live_` / `rk_live_`) additionally
  needs `--i-mean-live`. There are no prompts, so a script behaves the same way every time. Run
  the dry run against live first and read the table.
- **The key lives in the environment only.** It is never accepted as a flag, never printed, and is
  scrubbed from error messages. Only its prefix decides test or live mode. Publishable keys are
  refused.
- **Only Stripe or loopback receives the key.** `STRIPE_API_BASE` defaults to
  `https://api.stripe.com`; it may point to `localhost` / `127.0.0.1` (for stripe-mock) and
  nothing else. Redirects are not followed.
- **Ownership marker.** Everything the tool creates carries `metadata[reelbolt_catalog]=1` and
  `metadata[reelbolt_key]=<our id>`. An object found by id or `lookup_key` without the marker is
  reported `not-ours` (exit 1) and never modified, archived or deleted, `--recreate-coupons`
  included. Items that depend on it are skipped; independent items still sync.
- **Idempotent.** A second run changes nothing. Every `POST` carries a deterministic
  `Idempotency-Key` (sha256 of the operation, identity and desired state). `429` and `5xx`
  responses are retried with backoff, at most 4 times.
- **Price changes never edit a price.** A changed amount creates a new price with
  `transfer_lookup_key=true` and then sets the old one `active=false` (reported
  `replace-price`); existing subscribers keep the old price. If a run dies between those two
  calls, the next run finds the still-active price that no lookup key points at and archives it.
- **`tax_behavior` is a one-way door.** Once a price has `inclusive` or `exclusive` it can never
  change. The tool refuses to run when `--tax-behavior` differs from an existing price's value,
  before any write. Decide it before the first apply.
- **Coupons are immutable** except for their name. A coupon whose definition differs from the
  promotion is reported `drift` and left alone (exit 2). Only `--recreate-coupons` deletes it,
  and only if it carries our marker. Deleting stops new applications; existing discounts keep
  running. Whether Stripe allows re-using the id of a deleted coupon has not been verified:
  try it in test mode before relying on it.
- **Restricted key.** The key needs write access to Products, Prices, Coupons and Customer portal
  configurations (read is implied). Derive the minimum from the test-mode request log.

## Billing Portal configuration

One dedicated configuration (found by `metadata[reelbolt_key]=portal`, created or updated in
place): invoice history on, payment method update on, customer update on (email, address,
name), cancellation on with `mode=at_period_end` and `proration_behavior=none`, plan switching
off. The platform stays the authority for plan and seat changes. Promotion codes in the portal
are off by default in Stripe and this tool does not enable them. The id is printed as
`STRIPE_PORTAL_CONFIGURATION_ID`.

## Promotions

`--promotions promotions.json` is an array exported from our `promotions` table. Only
`PlanDiscount` and `PackDiscount` rows that are `Active` become coupons; `CreditCode` and
`PackBonus` have no provider discount and are listed as `skip`.

```bash
psql "$DATABASE_URL" -At -c "
SELECT COALESCE(json_agg(json_build_object(
  'id', id, 'kind', kind, 'status', status, 'name', name,
  'discountType', discount_type, 'discountAmount', discount_amount,
  'currencyCode', currency_code, 'durationKind', duration_kind,
  'durationMonths', duration_months, 'targetPlanKeys', target_plan_keys_json,
  'targetPackCredits', target_pack_credits_json, 'endsAt', ends_at,
  'maxRedemptions', max_redemptions)), '[]'::json)
FROM promotions WHERE kind IN ('PlanDiscount', 'PackDiscount')" > promotions.json
```

`\copy (SELECT json_agg(...)) TO 'promotions.json'` works with the same query, but text-format
`\copy` doubles backslashes, which corrupts a name that contains a quote or a backslash. Prefer
the `-At -c` form above.

Mapping: `Percentage` becomes `percent_off`; `Flat` becomes `amount_off` + `currency`
(`discount_amount` is already whole minor units in our table, so it is passed through; the
currency must equal the catalog currency). `Once` is `duration=once`, `Months` is `repeating`
with `duration_in_months`, `Forever` is `forever`. `maxRedemptions` is `max_redemptions`,
`endsAt` is `redeem_by` (in the future, at most 5 years). `targetPlanKeys` /
`targetPackCredits` become `applies_to[products]` using the product ids above; an empty list
means every plan (or every pack) and is written out explicitly. A target that is not a synced
plan or pack skips the promotion with a reason. Names are cut to 40 characters.

The export `id` must be a UUID (the same rule as `--founder-promotion-id`); any other row is
skipped with "id is not a UUID", so a dot-segment or quote can never reach `/v1/coupons/{id}` or
the SQL.

### Promotion targets (`target_price_ids_json`)

A promotion only applies when the checkout's price id is in its `target_price_ids_json` (a jsonb
array of provider price ids, read by `PromotionService` and `PromotionPlanPriceService`). Rows
created under Paddle hold Paddle ids, so after the switch they would silently stop applying. For
every exported promotion that becomes a coupon, the SQL therefore also carries

```sql
UPDATE promotions SET target_price_ids_json = (SELECT COALESCE(jsonb_agg(DISTINCT v ORDER BY v), '[]'::jsonb)
  FROM jsonb_array_elements_text((CASE WHEN jsonb_typeof(target_price_ids_json) = 'array' THEN target_price_ids_json ELSE '[]'::jsonb END) || '["price_..."]'::jsonb) AS t(v)) WHERE id = '<uuid>';
```

It is a union: the existing ids (Paddle) stay, the Stripe ids are added, duplicates collapse, and
running it twice changes nothing. The Stripe ids come from `targetPlanKeys` (empty = every synced
plan price) or `targetPackCredits` (empty = every synced pack price) and from the price ids of this
run (`price_<would-create>` in a dry run). If a promotion has no usable Stripe price (its plan or
pack was skipped or failed) the SQL carries a `-- NOTE:` instead and leaves the column alone.

## Founder price (public alpha)

The public-alpha founder price is 50% off, locked for as long as the subscription stays active
(a Stripe coupon with `duration=forever`), open to new subscribers until 31 January 2027 23:59
Europe/Rome. Its numbers live in `founder-offer.json` and nowhere else. The tool manages it by
default (`--no-founder` to skip):

- Coupon id `rb_founder_alpha`, `percent_off` 50, `duration` forever, `redeem_by` 1801436340
  (`2027-01-31T22:59:00Z`), `applies_to` every subscription and per-seat plan product (not top-up
  packs). `redeem_by` closes the offer to NEW customers only; subscriptions that already carry the
  coupon keep the discount.
- The platform models the offer as a `PlanDiscount` promotion (50%, forever, all plans, ends at the
  same instant). Link the promotion row to the coupon with `--founder-promotion-id <uuid>`, which adds
  `UPDATE promotions SET provider_discount_id = 'rb_founder_alpha' WHERE id = '<uuid>';` to the SQL.
  Without the flag the SQL carries a NOTE instead. With the flag it also adds the target UPDATE
  above, covering every subscription and per-seat plan price, so the promotion matches Stripe
  checkouts.
- Coupons are immutable. To change the offer, edit `founder-offer.json` and run with
  `--recreate-coupons` (only ever deletes a coupon that carries our marker). After the window closes the
  tool does not fail: the coupon shows as `skip` ("founder window closed (redeem_by passed);
  existing coupon left untouched"), nothing is created, recreated or linked, and the exit code is
  unaffected, so a forgotten flag cannot recreate it and routine runs keep working. A broken offer
  file (for example `redeemBy` without an explicit offset) is still a fatal error.
- Dry run, drift reporting, `not-ours` protection and idempotency work exactly as for promotions.

## Output

After the diff table the tool prints, to stdout only:

1. SQL that merges the Stripe ids into the database. The `paddle` key already in
   `provider_price_ids_json` is preserved:

   ```sql
   UPDATE plans SET provider_price_ids_json = jsonb_set(COALESCE(provider_price_ids_json,'{}'::jsonb), '{stripe}', '{"monthly":"price_..."}'::jsonb, true) WHERE key = 'creator';
   UPDATE promotions SET provider_discount_id = 'rb_promo_<id>' WHERE id = '<id>';
   UPDATE promotions SET target_price_ids_json = ... WHERE id = '<id>';  -- union, see "Promotion targets"
   ```

2. Environment lines: `STRIPE_TOPUP_PRICE_500=price_..`, `_1000`, `_3000` (one per pack) and
   `STRIPE_PORTAL_CONFIGURATION_ID=bpc_..`.
3. With `--out`, a JSON report (ids and diff, no secrets).

In a dry run the ids that do not exist yet show as `price_<would-create>` / `bpc_<would-create>`
so the shape is reviewable. That SQL contains a statement that raises an error, so piping a dry
run into `psql` rolls back instead of writing placeholders. Review the SQL from an `--apply` run,
then execute it yourself.

## Running against live

1. Test mode first: `node sync.mjs`, read the table, `node sync.mjs --apply`, run it again and
   confirm exit 0 with no writes.
2. Decide `--tax-behavior` (the tax code defaults to `txcd_10105002` with Managed Payments, the default) before the first
   live price exists.
3. Live dry run: `STRIPE_API_KEY=rk_live_... node sync.mjs --out live-plan.json`. Review it.
4. Apply: `node sync.mjs --apply --i-mean-live --out live-report.json`.
5. Run the printed SQL against our database, add the env lines to the deployment configuration.

Stripe API version is pinned per request: `Stripe-Version: 2026-09-30.endive`.

## Yearly prices

Each plan in the catalog with a `priceAnnual` (creator, pro, team; Team is per seat) also gets a yearly
Stripe price on the same product: lookup key `<prefix>:<planId>:year:<currency>`, `interval=year`,
`interval_count=1`, the same `tax_behavior` as the monthly price, amount taken from `priceAnnual`
(the tool carries no price literal). The dry-run table shows the yearly rows as `/year`.

Stripe prices are immutable, so a first run creates new yearly price ids; the existing monthly prices
(and any old ids) stay as they are. The stale-price archiver only archives prices of ours with the same
interval as the spec it is checking and never one that another spec's lookup key claims, so the yearly
prices are never archived by the monthly pass and nothing we did not create is touched.

Outputs: the report `ids.plans[]` carries `{planId, monthly, annual}`; the SQL writes
`{"monthly":"price_..","annual":"price_.."}` under the `stripe` key via the same idempotent, merge-safe
`jsonb_set`; the env snippet adds `STRIPE_PLAN_<ID>_ANNUAL_PRICE`. Promotion targets keep using the
monthly price ids.
