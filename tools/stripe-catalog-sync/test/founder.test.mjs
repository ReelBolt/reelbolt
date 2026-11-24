import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test } from 'node:test';
import { founderEntry } from '../lib/catalog.mjs';
import { createFakeStripe } from './fake-stripe.mjs';
import { REAL_CATALOG, run, tempDir } from './helpers.mjs';

const OFFER = JSON.parse(await readFile(new URL('../founder-offer.json', import.meta.url), 'utf8'));

test('founder offer file: forever, 50 percent, closes 2027-01-31 23:59 Europe/Rome (22:59Z)', () => {
  const entry = founderEntry(OFFER, { plans: [{ productId: 'rb_plan_creator' }, { productId: 'rb_plan_pro' }] }, { now: new Date('2026-10-26T12:00:00Z') });
  assert.equal(entry.couponId, 'rb_founder_alpha');
  assert.equal(entry.coupon.percent_off, 50);
  assert.equal(entry.coupon.duration, 'forever');
  assert.equal(entry.coupon.redeem_by, 1801436340);
  assert.equal(new Date(entry.coupon.redeem_by * 1000).toISOString(), '2027-01-31T22:59:00.000Z');
  assert.deepEqual(entry.coupon.applies_to.products, ['rb_plan_creator', 'rb_plan_pro']);
});

test('founder offer: rejects a missing offset, a closed window and a bad promotion uuid', () => {
  const desired = { plans: [{ productId: 'rb_plan_creator' }] };
  assert.throws(() => founderEntry({ ...OFFER, redeemBy: '2027-01-31T23:59:00' }, desired), /explicit offset/);
  assert.throws(() => founderEntry(OFFER, desired, { promotionId: 'not-a-uuid' }), /UUID/);
  assert.throws(() => founderEntry({ ...OFFER, duration: 'once' }, desired), /forever/);
});

test('founder offer: after the window closes it returns a skip, while config errors stay fatal', () => {
  const desired = { plans: [{ productId: 'rb_plan_creator', planId: 'creator' }] };
  const closed = new Date('2027-02-01T00:00:00Z');
  assert.deepEqual(founderEntry(OFFER, desired, { now: closed }), {
    skip: true,
    couponId: 'rb_founder_alpha',
    reason: 'founder window closed (redeem_by passed); existing coupon left untouched',
  });
  // Still fatal after closing: a broken offer file or a bad uuid must not be hidden by the skip.
  assert.throws(() => founderEntry({ ...OFFER, redeemBy: '2027-01-31T23:59:00' }, desired, { now: closed }), /explicit offset/);
  assert.throws(() => founderEntry({ ...OFFER, duration: 'once' }, desired, { now: closed }), /forever/);
  assert.throws(() => founderEntry({ ...OFFER, appliesTo: 'all' }, desired, { now: closed }), /subscriptionPlans/);
  assert.throws(() => founderEntry(OFFER, desired, { now: closed, promotionId: 'nope' }), /UUID/);
});

test('after the window closes the tool still runs: the founder coupon is a skip, exit code 0, nothing written for it', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const sql = join(dir, 'f.sql');
  const result = await run(['--founder', '--apply', '--sql-out', sql], { fake, extra: { now: new Date('2027-02-01T00:00:00Z') } });
  assert.equal(result.code, 0, result.out + result.err);
  assert.match(result.out, /skip\s+coupon\s+rb_founder_alpha\s+founder window closed \(redeem_by passed\); existing coupon left untouched/);
  assert.equal(fake.writes.filter((w) => w.path.startsWith('/v1/coupons')).length, 0);
  assert.ok(!(await readFile(sql, 'utf8')).includes('rb_founder_alpha'));
});

test('founder price targets: the SQL unions every subscription and per-seat plan price into the promotion', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const sql = join(dir, 'f.sql');
  const id = '0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b';
  const result = await run(['--founder', '--founder-promotion-id', id, '--apply', '--sql-out', sql], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  const text = await readFile(sql, 'utf8');
  const update = text.split('\n').find((l) => l.startsWith('UPDATE promotions SET target_price_ids_json'));
  assert.ok(update, text);
  assert.ok(update.endsWith(`WHERE id = '${id}';`));
  const added = JSON.parse(/\|\| '(\[.*?\])'::jsonb\)/.exec(update)[1]);
  const planPrices = [...fake.db.prices.values()].filter((p) => p.lookup_key?.startsWith('rb:') && p.lookup_key.endsWith(':month:usd')).map((p) => p.id);
  assert.equal(planPrices.length, 3);
  assert.deepEqual([...added].sort(), planPrices.sort());
});

test('first run creates the founder coupon with exact params on every plan product and no pack product', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const sql = join(dir, 'f.sql');
  const result = await run(['--founder', '--apply', '--sql-out', sql], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  const coupons = fake.writes.filter((w) => w.path === '/v1/coupons');
  assert.equal(coupons.length, 1);
  const body = coupons[0].body;
  assert.equal(body.id, 'rb_founder_alpha');
  assert.equal(String(body.percent_off), '50');
  assert.equal(body.duration, 'forever');
  assert.equal(String(body.redeem_by), '1801436340');
  assert.equal(body.metadata.reelbolt_catalog, '1');
  assert.equal(body.metadata.reelbolt_key, 'founder');
  const products = body.applies_to.products;
  assert.deepEqual([...products].sort(), ['rb_plan_creator', 'rb_plan_pro', 'rb_plan_team']);
  assert.ok(!products.some((p) => p.startsWith('rb_topup_')));
  const text = await readFile(sql, 'utf8');
  assert.match(text, /NOTE: the founder price coupon is rb_founder_alpha/);
  assert.ok(!text.split('\n').some((l) => l.startsWith('UPDATE promotions')));
});

test('second run is a no-op (idempotent)', async () => {
  const fake = createFakeStripe();
  assert.equal((await run(['--founder', '--apply'], { fake })).code, 0);
  const before = fake.writes.length;
  const again = await run(['--founder', '--apply'], { fake });
  assert.equal(again.code, 0, again.out + again.err);
  assert.equal(fake.writes.length, before);
});

test('--founder-promotion-id emits the UPDATE that links the promotion row', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const sql = join(dir, 'f.sql');
  const id = '0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b';
  const result = await run(['--founder', '--founder-promotion-id', id, '--apply', '--sql-out', sql], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.match(await readFile(sql, 'utf8'), new RegExp(`UPDATE promotions SET provider_discount_id = 'rb_founder_alpha' WHERE id = '${id}';`));
});

test('bad promotion uuid, and --no-founder with a promotion id, are config errors with no request', async () => {
  const fake = createFakeStripe();
  const bad = await run(['--founder-promotion-id', 'nope'], { fake });
  assert.equal(bad.code, 1);
  assert.match(bad.err, /UUID/);
  const conflict = await run(['--no-founder', '--founder-promotion-id', '0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b'], { fake });
  assert.equal(conflict.code, 1);
  assert.equal(fake.writes.length, 0);
});

test('an edited founder coupon in Stripe is reported as drift and not touched', async () => {
  const fake = createFakeStripe();
  assert.equal((await run(['--founder', '--apply'], { fake })).code, 0);
  const coupon = fake.db.coupons.get('rb_founder_alpha');
  assert.ok(coupon, 'fake exposes the coupon store');
  coupon.percent_off = 25;
  const writes = fake.writes.length;
  const result = await run(['--founder', '--apply'], { fake });
  assert.equal(result.code, 2, result.out);
  assert.match(result.out, /drift/);
  assert.equal(fake.writes.length, writes);
});

test('--no-founder leaves the coupon out entirely', async () => {
  const fake = createFakeStripe();
  const result = await run(['--no-founder', '--apply'], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.equal(fake.writes.filter((w) => w.path === '/v1/coupons').length, 0);
  assert.ok(REAL_CATALOG);
});

test('the founder promotion row in the export gets rb_founder_alpha only, no second coupon of its own', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const id = '0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b';
  const file = join(dir, 'promotions.json');
  const { writeFile } = await import('node:fs/promises');
  await writeFile(file, JSON.stringify([{ id, kind: 'PlanDiscount', status: 'Active', name: 'Founder price', discountType: 'Percentage', discountAmount: 50, durationKind: 'Forever', targetPlanKeys: [], targetPackCredits: [], endsAt: '2027-01-31T22:59:00Z' }]));
  const sql = join(dir, 'f.sql');
  const result = await run(['--founder', '--founder-promotion-id', id, '--promotions', file, '--apply', '--sql-out', sql], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  const coupons = fake.writes.filter((w) => w.path === '/v1/coupons').map((w) => w.body.id);
  assert.deepEqual(coupons, ['rb_founder_alpha']);
  assert.match(result.out, /this is the founder promotion/);
  const text = await readFile(sql, 'utf8');
  assert.match(text, new RegExp(`provider_discount_id = 'rb_founder_alpha' WHERE id = '${id}'`));
  assert.ok(!text.includes(`rb_promo_${id}`));
});
