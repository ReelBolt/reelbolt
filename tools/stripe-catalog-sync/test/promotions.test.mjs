import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test } from 'node:test';
import { createFakeStripe } from './fake-stripe.mjs';
import { run, tempDir, writeJson } from './helpers.mjs';

const future = '2027-03-01T00:00:00Z';
// Promotion ids are UUIDs (anything else is skipped); U[n] / C[n] are row n's id and coupon id.
const U = Object.fromEntries(Array.from({ length: 11 }, (_, n) => [n, `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`]));
const C = Object.fromEntries(Object.entries(U).map(([n, id]) => [n, `rb_promo_${id}`]));
const rows = () => [
  { id: U[1], kind: 'PlanDiscount', status: 'Active', name: 'Launch 20', discountType: 'Percentage', discountAmount: 20, currencyCode: null, durationKind: 'Once', durationMonths: null, targetPlanKeys: ['creator'], targetPackCredits: [], endsAt: future, maxRedemptions: 100 },
  { id: U[2], kind: 'PlanDiscount', status: 'Active', name: 'Five off for three months, a very long promotion name that goes past forty characters', discountType: 'Flat', discountAmount: '500', currencyCode: 'USD', durationKind: 'Months', durationMonths: 3, targetPlanKeys: [], targetPackCredits: [], endsAt: null, maxRedemptions: null },
  { id: U[3], kind: 'PackDiscount', status: 'Active', name: 'Packs 10', discountType: 'Percentage', discountAmount: 10, currencyCode: null, durationKind: 'Forever', durationMonths: null, targetPlanKeys: [], targetPackCredits: [500], endsAt: null, maxRedemptions: null },
  { id: U[4], kind: 'CreditCode', status: 'Active', name: 'Credits', discountType: 'Flat', discountAmount: 1, currencyCode: 'USD', durationKind: 'Once' },
  { id: U[5], kind: 'PlanDiscount', status: 'Paused', name: 'Paused', discountType: 'Percentage', discountAmount: 15, durationKind: 'Once', targetPlanKeys: ['pro'] },
  { id: U[6], kind: 'PlanDiscount', status: 'Active', name: 'Bad target', discountType: 'Percentage', discountAmount: 15, durationKind: 'Once', targetPlanKeys: ['nope'] },
  { id: U[7], kind: 'PlanDiscount', status: 'Active', name: 'Euro', discountType: 'Flat', discountAmount: '500', currencyCode: 'EUR', durationKind: 'Once', targetPlanKeys: ['pro'] },
  { id: U[8], kind: 'PackDiscount', status: 'Active', name: 'Ended', discountType: 'Percentage', discountAmount: 5, durationKind: 'Once', targetPackCredits: [1000], endsAt: '2026-01-01T00:00:00Z' },
  { id: U[10], kind: 'PlanDiscount', status: 'Active', name: 'Fractional minor units', discountType: 'Flat', discountAmount: '5.5', currencyCode: 'USD', durationKind: 'Once', targetPlanKeys: ['pro'] },
  { id: U[9], kind: 'PlanDiscount', status: 'Active', name: 'Free', discountType: 'Percentage', discountAmount: '100.5', durationKind: 'Once', targetPlanKeys: ['pro'] },
];

async function setup(extraArgs = [], fake = createFakeStripe()) {
  const dir = await tempDir();
  const file = await writeJson(dir, 'promotions.json', rows());
  const sqlFile = join(dir, 'p.sql');
  const result = await run(['--apply', '--promotions', file, '--sql-out', sqlFile, ...extraArgs], { fake });
  return { fake, dir, file, result, sql: await readFile(sqlFile, 'utf8') };
}

test('promotions map to coupons: percentage, flat, once, months, forever, applies_to, redeem_by', async () => {
  const { fake, result, sql } = await setup();
  assert.equal(result.code, 0, result.out + result.err);
  const bodies = Object.fromEntries(fake.writes.filter((w) => w.path === '/v1/coupons').map((w) => [w.body.id, w.body]));
  assert.deepEqual(Object.keys(bodies).sort(), [C[1], C[2], C[3]]);

  assert.deepEqual(bodies[C[1]], {
    id: C[1],
    name: 'Launch 20',
    metadata: { reelbolt_catalog: '1', reelbolt_key: C[1], promotion_id: U[1] },
    percent_off: '20',
    duration: 'once',
    max_redemptions: '100',
    redeem_by: String(Math.floor(Date.parse(future) / 1000)),
    applies_to: { products: ['rb_plan_creator'] },
  });
  assert.equal(bodies[C[2]].amount_off, '500');
  assert.equal(bodies[C[2]].currency, 'usd');
  assert.equal(bodies[C[2]].duration, 'repeating');
  assert.equal(bodies[C[2]].duration_in_months, '3');
  assert.deepEqual(bodies[C[2]].applies_to.products, ['rb_plan_creator', 'rb_plan_pro', 'rb_plan_team']);
  assert.equal(bodies[C[2]].name.length, 40);
  assert.equal(bodies[C[3]].duration, 'forever');
  assert.equal(bodies[C[3]].percent_off, '10');
  assert.deepEqual(bodies[C[3]].applies_to.products, ['rb_topup_500']);
  assert.equal(bodies[C[3]].redeem_by, undefined);

  for (const id of [U[1], U[2], U[3]]) {
    assert.ok(sql.includes(`UPDATE promotions SET provider_discount_id = 'rb_promo_${id}' WHERE id = '${id}';`), id);
  }
  assert.ok(!/WHERE id = '00000000-0000-4000-8000-00000000000[4-9]'/.test(sql));
});

test('ineligible promotions are skipped with a reason, never guessed', async () => {
  const { result } = await setup();
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000004\s+kind CreditCode has no provider discount/);
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000005\s+status Paused/);
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000006\s+target "nope" is not a synced plan/);
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000007\s+flat discount currency eur differs/);
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000008\s+endsAt is in the past/);
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000010\s+flat discountAmount must be a positive whole number of minor units/);
  assert.match(result.out, /skip\s+coupon\s+00000000-0000-4000-8000-000000000009\s+percentage must be above 0 and at most 100/);
});

test('--include-inactive also syncs paused promotions', async () => {
  const { fake } = await setup(['--include-inactive']);
  assert.ok(fake.db.coupons.has(C[5]));
  assert.deepEqual(fake.db.coupons.get(C[5]).applies_to.products, ['rb_plan_pro']);
});

test('a second run with promotions is a no-op', async () => {
  const { fake, file } = await setup();
  fake.reset();
  const again = await run(['--apply', '--promotions', file], { fake });
  assert.equal(again.code, 0, again.out + again.err);
  assert.equal(fake.writes.length, 0);
});

test('a coupon whose definition drifted is reported, never auto-deleted; its id stays out of the SQL', async () => {
  const { fake, file } = await setup();
  fake.db.coupons.get(C[1]).percent_off = 25;
  fake.reset();
  const dry = await run(['--promotions', file], { fake });
  assert.equal(dry.code, 2);
  assert.match(dry.out, /drift\s+coupon\s+rb_promo_00000000-0000-4000-8000-000000000001\s+definition differs \(percent_off\)/);
  assert.ok(!dry.out.includes(`WHERE id = '${U[1]}'`));
  const applied = await run(['--apply', '--promotions', file], { fake });
  assert.equal(applied.code, 2, 'drift remains');
  assert.equal(fake.writes.length, 0);
  assert.equal(fake.db.coupons.get(C[1]).percent_off, 25);
});

test('--recreate-coupons deletes and re-creates only our coupons', async () => {
  const { fake, file } = await setup();
  fake.db.coupons.get(C[1]).percent_off = 25;
  fake.reset();
  const result = await run(['--apply', '--recreate-coupons', '--promotions', file], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.deepEqual(
    fake.writes.map((w) => `${w.method} ${w.path}`),
    [`DELETE /v1/coupons/${C[1]}`, 'POST /v1/coupons'],
  );
  assert.equal(fake.db.coupons.get(C[1]).percent_off, 20);
});

test('a coupon without our marker is never modified or deleted, even with --recreate-coupons', async () => {
  const fake = createFakeStripe();
  fake.seedCoupon({ id: C[1], name: 'Theirs', percent_off: 99, duration: 'once' });
  const { result } = await setup(['--recreate-coupons'], fake);
  assert.equal(result.code, 1);
  assert.match(result.out, /not-ours\s+coupon\s+rb_promo_00000000-0000-4000-8000-000000000001/);
  assert.ok(!fake.writes.some((w) => w.path === `/v1/coupons/${C[1]}`));
  assert.equal(fake.db.coupons.get(C[1]).percent_off, 99);
});

test('a name-only difference is updated in place', async () => {
  const { fake, file } = await setup();
  fake.db.coupons.get(C[3]).name = 'Old name';
  fake.reset();
  const result = await run(['--apply', '--promotions', file], { fake });
  assert.equal(result.code, 0);
  assert.deepEqual(fake.writes.map((w) => `${w.method} ${w.path}`), [`POST /v1/coupons/${C[3]}`]);
  assert.equal(fake.db.coupons.get(C[3]).name, 'Packs 10');
});

test('dry run with promotions writes nothing', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const file = await writeJson(dir, 'promotions.json', rows());
  const result = await run(['--promotions', file], { fake });
  assert.equal(result.code, 2);
  assert.equal(fake.writes.length, 0);
  assert.match(result.out, /create\s+coupon\s+rb_promo_00000000-0000-4000-8000-000000000001\s+20% off, once/);
  assert.ok(result.out.includes(`UPDATE promotions SET provider_discount_id = '${C[1]}' WHERE id = '${U[1]}';`));
});

test('a promotion export that is not an array is rejected', async () => {
  const dir = await tempDir();
  const file = await writeJson(dir, 'promotions.json', { id: 'x' });
  const result = await run(['--promotions', file], { fake: createFakeStripe() });
  assert.equal(result.code, 1);
  assert.match(result.err, /must be a JSON array/);
});

function targetUpdate(sql, id) {
  return sql.split('\n').find((l) => l.startsWith('UPDATE promotions SET target_price_ids_json') && l.endsWith(`WHERE id = '${id}';`));
}

function addedIds(update) {
  return JSON.parse(/\|\| '(\[.*?\])'::jsonb\)/.exec(update)[1]);
}

function priceIdOf(fake, lookupKey) {
  return [...fake.db.prices.values()].find((p) => p.lookup_key === lookupKey)?.id;
}

test('promotion targets: the SQL UNIONS the Stripe price ids into target_price_ids_json, keeping the old provider ids', async () => {
  const { fake, sql } = await setup();
  const creator = priceIdOf(fake, 'rb:creator:month:usd');
  const allPlans = ['creator', 'pro', 'team'].map((k) => priceIdOf(fake, `rb:${k}:month:usd`));
  const pack500 = priceIdOf(fake, 'rb:topup:500:usd');
  assert.ok(creator && pack500 && allPlans.every(Boolean));

  // targetPlanKeys ['creator'] -> that plan's price only; [] -> every synced plan price; pack 500 -> that pack's price.
  assert.deepEqual(addedIds(targetUpdate(sql, U[1])), [creator]);
  assert.deepEqual(addedIds(targetUpdate(sql, U[2])).sort(), [...allPlans].sort());
  assert.deepEqual(addedIds(targetUpdate(sql, U[3])), [pack500]);

  // The paddle-id-preserved shape: the new value is (existing array || stripe ids), de-duplicated,
  // never a bare assignment of the stripe ids; a non-array jsonb value counts as empty.
  const update = targetUpdate(sql, U[1]);
  assert.ok(
    update.startsWith(
      "UPDATE promotions SET target_price_ids_json = (SELECT COALESCE(jsonb_agg(DISTINCT v ORDER BY v), '[]'::jsonb) FROM jsonb_array_elements_text((CASE WHEN jsonb_typeof(target_price_ids_json) = 'array' THEN target_price_ids_json ELSE '[]'::jsonb END) || '[",
    ),
    update,
  );
  assert.ok(!/SET target_price_ids_json = '/.test(sql), 'never overwrites the column with a literal');
  assert.ok(!addedIds(update).some((v) => v.startsWith('pri_')), 'only stripe ids are added; the old ids stay because the column is merged, not replaced');
  // Skipped promotions get no UPDATE at all.
  for (const n of [4, 5, 6, 7, 8, 9, 10]) {
    assert.ok(!sql.includes(`WHERE id = '${U[n]}'`), `row ${n}`);
  }
});

test('promotion targets in a dry run use the would-create placeholder', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const file = await writeJson(dir, 'promotions.json', rows());
  const dry = await run(['--promotions', file], { fake });
  assert.equal(dry.code, 2);
  assert.match(targetUpdate(dry.out, U[1]), /\|\| '\["price_<would-create>"\]'::jsonb\)/);
});

test('a promotion with no resolvable Stripe price gets a NOTE and no target UPDATE', async () => {
  const { collectIds, renderSql } = await import('../lib/outputs.mjs');
  const desired = { plans: [{ planId: 'creator', lookupKey: 'rb:creator:month:usd' }], packs: [] };
  const items = [{ kind: 'price', key: 'rb:creator:month:usd', action: 'error', error: 'boom' }, { kind: 'coupon', key: 'rb_promo_x', action: 'create', id: 'rb_promo_x' }];
  const promotions = { eligible: [{ promotionId: U[1], couponId: 'rb_promo_x', coupon: {}, targets: { kind: 'plan', planKeys: ['creator'] } }] };
  const ids = collectIds(items, desired, promotions);
  const sql = renderSql(ids, { dryRun: true });
  assert.match(sql, new RegExp(`-- NOTE: promotion ${U[1]} has no resolvable Stripe price`));
  assert.ok(!sql.includes('SET target_price_ids_json'));
});

test('promotion ids must be UUIDs: dot-segments, slashes and quotes are skipped before any request', async () => {
  const bad = ['..', '../customers', 'a/b', "x'; DROP TABLE promotions; --", '%2e%2e', 'p1', '00000000-0000-4000-8000-00000000000g'];
  const base = rows()[0];
  const dir = await tempDir();
  const file = await writeJson(dir, 'promotions.json', bad.map((id) => ({ ...base, id })));
  const fake = createFakeStripe();
  const result = await run(['--apply', '--promotions', file], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.equal(fake.writes.filter((w) => w.path.startsWith('/v1/coupons')).length, 0);
  assert.equal(fake.log.length > 0, true, 'the catalog itself still synced');
  assert.ok(!fake.log.some((r) => JSON.stringify(r).includes('..')), 'no request carried a dot-segment');
  assert.equal((result.out.match(/id is not a UUID/g) ?? []).length, bad.length);
  assert.ok(!result.out.includes('UPDATE promotions'));
});
