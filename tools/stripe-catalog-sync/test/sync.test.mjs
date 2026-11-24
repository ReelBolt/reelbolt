import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test } from 'node:test';
import { main } from '../lib/cli.mjs';
import { createFakeStripe } from './fake-stripe.mjs';
import { REAL_CATALOG, editedCatalog, run, tempDir, writesTo } from './helpers.mjs';

test('dry run against the real catalog prints the expected plan and writes nothing', async () => {
  const fake = createFakeStripe();
  const { code, out } = await run([], { fake });
  assert.equal(code, 2, 'differences found');
  assert.equal(fake.writes.length, 0, 'no POST or DELETE in dry run');
  assert.deepEqual(fake.violations, []);
  assert.match(out, /dry run \(no writes\)/);
  for (const key of ['rb:creator:month:usd', 'rb:pro:month:usd', 'rb:team:month:usd', 'rb:topup:500:usd', 'rb:topup:1000:usd', 'rb:topup:3000:usd']) {
    assert.match(out, new RegExp(key));
  }
  assert.match(out, /2900 usd\/month/);
  assert.match(out, /7900 usd\/month/);
  assert.match(out, /4900 usd\/month/);
  assert.match(out, /1500 usd one-time/);
  assert.match(out, /2500 usd one-time/);
  assert.match(out, /6000 usd one-time/);
  for (const key of ['rb:creator:year:usd', 'rb:pro:year:usd', 'rb:team:year:usd']) {
    assert.match(out, new RegExp(key));
  }
  assert.match(out, /\/year/);
  assert.doesNotMatch(out, /sk_(test|live)_/);
  assert.match(out, /skip\s+plan\s+trial/);
  assert.match(out, /skip\s+plan\s+enterprise/);
  assert.match(out, /price_<would-create>/);
  assert.match(out, /STRIPE_TOPUP_PRICE_500=price_<would-create>/);
  assert.match(out, /STRIPE_PORTAL_CONFIGURATION_ID=bpc_<would-create>/);
});

test('first apply creates everything with the ownership marker, second run is a no-op with zero POSTs', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const first = await run(['--apply', '--out', join(dir, 'r.json'), '--sql-out', join(dir, 'x.sql')], { fake });
  assert.equal(first.code, 0, first.err + first.out);
  assert.deepEqual(fake.violations, []);

  assert.equal(fake.db.products.size, 6, 'creator, pro, team + 3 packs');
  assert.equal(fake.db.prices.size, 9, '3 monthly + 3 yearly + 3 packs');
  const catalogJson = JSON.parse(await readFile(REAL_CATALOG, 'utf8'));
  for (const plan of catalogJson.plans.filter((p) => p.priceAnnual)) {
    const year = [...fake.db.prices.values()].find((p) => p.lookup_key === `rb:${plan.id}:year:usd`);
    assert.equal(year.unit_amount, Math.round(plan.priceAnnual * 100));
    assert.deepEqual(year.recurring, { interval: 'year', interval_count: 1 });
    assert.equal(year.tax_behavior, 'exclusive');
    assert.equal(year.product, `rb_plan_${plan.id}`);
    assert.equal(fake.db.prices.get(year.id).active, true);
  }
  assert.equal(fake.db.portals.size, 1);
  for (const object of [...fake.db.products.values(), ...fake.db.prices.values(), ...fake.db.portals.values()]) {
    assert.equal(object.metadata.reelbolt_catalog, '1');
    assert.ok(object.metadata.reelbolt_key);
  }
  const team = [...fake.db.prices.values()].find((p) => p.lookup_key === 'rb:team:month:usd');
  assert.equal(team.unit_amount, 4900);
  assert.equal(team.metadata.min_seats, '3');
  assert.deepEqual(team.recurring, { interval: 'month', interval_count: 1 });
  assert.equal(team.tax_behavior, 'exclusive');
  const pack = [...fake.db.prices.values()].find((p) => p.lookup_key === 'rb:topup:3000:usd');
  assert.equal(pack.unit_amount, 6000);
  assert.equal(pack.recurring, null);
  assert.equal(fake.db.products.get('rb_plan_creator').name, 'ReelBolt Creator');
  assert.equal(fake.db.products.get('rb_topup_500').name, 'ReelBolt 500 credits');

  const report = JSON.parse(await readFile(join(dir, 'r.json'), 'utf8'));
  assert.equal(report.dryRun, false);
  assert.equal(report.ids.plans.find((p) => p.planId === 'team').monthly, team.id);
  assert.match(report.ids.portal, /^bpc_/);

  fake.reset();
  const second = await run(['--apply'], { fake });
  assert.equal(second.code, 0, second.out + second.err);
  assert.equal(fake.writes.length, 0, 'idempotent: zero writes on the second run');
  assert.match(second.out, /in sync with the catalog/);

  fake.reset();
  const dry = await run([], { fake });
  assert.equal(dry.code, 0, 'dry run reports in sync with exit 0');
});

test('idempotency keys are deterministic across runs and unique per operation', async () => {
  const a = createFakeStripe();
  const b = createFakeStripe();
  await run(['--apply'], { fake: a });
  await run(['--apply'], { fake: b });
  const keys = (f) => f.writes.map((w) => w.idempotencyKey);
  assert.deepEqual(keys(a), keys(b));
  assert.equal(new Set(keys(a)).size, a.writes.length);
  assert.ok(keys(a).every((k) => /^rbsync-[0-9a-f]{64}$/.test(k)));
});

test('SQL keeps the paddle key and merges only the stripe key; env snippet lists the packs', async () => {
  const fake = createFakeStripe();
  const dir = await tempDir();
  const { out } = await run(['--apply', '--sql-out', join(dir, 'x.sql')], { fake });
  const sql = await readFile(join(dir, 'x.sql'), 'utf8');
  const creator = [...fake.db.prices.values()].find((p) => p.lookup_key === 'rb:creator:month:usd');
  const creatorYear = [...fake.db.prices.values()].find((p) => p.lookup_key === 'rb:creator:year:usd');
  assert.ok(
    sql.includes(
      `UPDATE plans SET provider_price_ids_json = jsonb_set(COALESCE(provider_price_ids_json,'{}'::jsonb), '{stripe}', '{"monthly":"${creator.id}","annual":"${creatorYear.id}"}'::jsonb, true) WHERE key = 'creator';`,
    ),
  );
  assert.ok(!/SET provider_price_ids_json = '/.test(sql), 'never overwrites the whole column');
  assert.ok(!/paddle/i.test(sql.replace(/^--.*$/gm, '')), 'no statement mentions paddle ids');
  assert.match(sql, /^-- .*\n-- The "stripe" key is merged into provider_price_ids_json; other providers \(paddle\) are kept\./m);
  assert.equal((sql.match(/UPDATE plans/g) ?? []).length, 3);
  assert.ok(!sql.includes('trial') && !sql.includes('enterprise'));
  for (const credits of [500, 1000, 3000]) {
    const price = [...fake.db.prices.values()].find((p) => p.lookup_key === `rb:topup:${credits}:usd`);
    assert.ok(out.includes(`STRIPE_TOPUP_PRICE_${credits}=${price.id}`));
  }
  const portal = [...fake.db.portals.values()][0];
  assert.ok(out.includes(`STRIPE_PORTAL_CONFIGURATION_ID=${portal.id}`));
});

test('a changed catalog price replaces the price: transfer_lookup_key, old one archived', async () => {
  const fake = createFakeStripe();
  await run(['--apply'], { fake });
  const old = [...fake.db.prices.values()].find((p) => p.lookup_key === 'rb:creator:month:usd');
  const dir = await tempDir();
  const catalog = await editedCatalog(dir, (c) => {
    c.plans.find((p) => p.id === 'creator').priceMonthly = 39;
  });

  fake.reset();
  const dry = await run(['--catalog', catalog], { fake });
  assert.equal(dry.code, 2);
  assert.equal(fake.writes.length, 0);
  assert.match(dry.out, /replace-price\s+price\s+rb:creator:month:usd/);
  assert.match(dry.out, new RegExp(`2900 usd -> 3900 usd/month; old ${old.id} is archived`));

  const applied = await run(['--catalog', catalog, '--apply'], { fake });
  assert.equal(applied.code, 0, applied.out + applied.err);
  const creates = writesTo(fake, 'POST', '/v1/prices').filter((w) => w.path === '/v1/prices');
  assert.equal(creates.length, 1);
  assert.equal(creates[0].body.transfer_lookup_key, 'true');
  assert.equal(creates[0].body.unit_amount, '3900');
  assert.equal(fake.db.prices.get(old.id).active, false, 'old price archived');
  const current = [...fake.db.prices.values()].filter((p) => p.lookup_key === 'rb:creator:month:usd');
  assert.equal(current.length, 1);
  assert.equal(current[0].unit_amount, 3900);
  assert.equal(current[0].active, true);

  fake.reset();
  assert.equal((await run([], { fake, extra: {} })).code, 2, 'default catalog now differs from the edited one');
  assert.equal((await run(['--catalog', catalog], { fake })).code, 0, 'edited catalog is in sync');
  assert.equal(fake.writes.length, 0);
});

test('an interrupted replace leaves a stale active price that the next run archives', async () => {
  const fake = createFakeStripe();
  await run(['--apply'], { fake });
  const current = [...fake.db.prices.values()].find((p) => p.lookup_key === 'rb:pro:month:usd');
  const stale = fake.seedPrice({ product: 'rb_plan_pro', currency: 'usd', unit_amount: 6900, type: 'recurring', recurring: { interval: 'month', interval_count: 1 }, lookup_key: null, metadata: { reelbolt_catalog: '1', reelbolt_key: 'rb:pro:month:usd' } });
  fake.reset();
  const result = await run(['--apply'], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.match(result.out, /archive\s+price\s+rb:pro:month:usd/);
  assert.equal(fake.db.prices.get(stale.id).active, false);
  assert.equal(fake.db.prices.get(current.id).active, true);
});

test('objects without the ownership marker are never touched: not-ours, error exit, no write to them', async () => {
  const fake = createFakeStripe();
  fake.seedProduct({ id: 'rb_plan_creator', name: 'Somebody elses product' });
  fake.seedProduct({ id: 'rb_topup_500', name: 'Mine', metadata: { reelbolt_catalog: '1' } });
  const foreignPrice = fake.seedPrice({ product: 'rb_topup_500', currency: 'usd', unit_amount: 999, type: 'one_time', recurring: null, lookup_key: 'rb:topup:500:usd', metadata: {} });
  fake.seedPortal({ metadata: { reelbolt_key: 'portal' } });

  const result = await run(['--apply'], { fake });
  assert.equal(result.code, 1);
  assert.match(result.out, /not-ours\s+product\s+rb_plan_creator/);
  assert.match(result.out, /not-ours\s+price\s+rb:topup:500:usd/);
  assert.match(result.out, /not-ours\s+portal/);
  assert.match(result.out, /skip\s+price\s+rb:creator:month:usd\s+its product is not ours/);
  const touched = fake.writes.filter((w) => /rb_plan_creator|\/v1\/billing_portal/.test(w.path) || w.path === `/v1/prices/${foreignPrice.id}`);
  assert.deepEqual(touched, [], 'nothing written to the foreign objects');
  assert.equal(fake.db.products.get('rb_plan_creator').name, 'Somebody elses product');
  assert.equal(fake.db.prices.get(foreignPrice.id).active, true);
  assert.equal([...fake.db.prices.values()].filter((p) => p.product === 'rb_plan_creator').length, 0);
  // The other, healthy items were still synced.
  assert.ok([...fake.db.prices.values()].some((p) => p.lookup_key === 'rb:pro:month:usd'));
});

test('dry run makes zero POST/DELETE even when everything differs', async () => {
  const fake = createFakeStripe();
  await run(['--apply'], { fake });
  const dir = await tempDir();
  const catalog = await editedCatalog(dir, (c) => {
    c.plans.find((p) => p.id === 'pro').priceMonthly = 99;
    c.topUp.packs[0].price = 20;
  });
  fake.reset();
  const dry = await run(['--catalog', catalog], { fake });
  assert.equal(dry.code, 2);
  assert.ok(fake.log.length > 0);
  assert.ok(fake.log.every((r) => r.method === 'GET'));
  assert.equal(fake.writes.length, 0);
});

test('tax_behavior is a one-way door: a different value than an existing price is refused before any write', async () => {
  const fake = createFakeStripe();
  await run(['--apply'], { fake });
  fake.reset();
  const result = await run(['--apply', '--tax-behavior', 'inclusive'], { fake });
  assert.equal(result.code, 1);
  assert.match(result.err, /tax_behavior=exclusive, which can never be changed/);
  assert.equal(fake.writes.length, 0);
});

test('an existing price with unspecified tax behaviour gets the chosen value without replacing it', async () => {
  const fake = createFakeStripe();
  fake.seedProduct({ id: 'rb_topup_500', name: 'ReelBolt 500 credits', metadata: { reelbolt_catalog: '1', reelbolt_key: 'rb_topup_500', catalog_kind: 'topup', credits: '500' } });
  const price = fake.seedPrice({ product: 'rb_topup_500', currency: 'usd', unit_amount: 1500, type: 'one_time', recurring: null, lookup_key: 'rb:topup:500:usd', tax_behavior: 'unspecified', metadata: { reelbolt_catalog: '1', reelbolt_key: 'rb:topup:500:usd', credits: '500' } });
  const result = await run(['--apply', '--skip-portal'], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.equal(fake.db.prices.get(price.id).tax_behavior, 'exclusive');
  assert.ok(fake.db.prices.get(price.id).active);
});

test('managed payments is the default: tax code txcd_10105002 on every product, opt out with --no-managed-payments', async () => {
  const fake = createFakeStripe();
  const bad = await run(['--tax-code', 'nonsense'], { fake });
  assert.equal(bad.code, 1);
  const both = await run(['--managed-payments', '--no-managed-payments'], { fake });
  assert.equal(both.code, 1);
  assert.match(both.err, /cannot be combined/);

  const ok = await run(['--apply', '--managed-payments'], { fake });
  assert.equal(ok.code, 0, ok.out + ok.err);
  assert.match(ok.out, /Managed Payments \(default\): tax_code txcd_10105002 is set on every product/);
  assert.ok([...fake.db.products.values()].every((p) => p.tax_code === 'txcd_10105002'));
});

test('without any flag the run is Managed Payments; a custom tax code replaces the default', async () => {
  const fake = createFakeStripe();
  const defaults = await run(['--tax-code', 'txcd_10000000', '--apply'], { fake });
  assert.equal(defaults.code, 0, defaults.out + defaults.err);
  assert.ok([...fake.db.products.values()].every((p) => p.tax_code === 'txcd_10000000'));

  const rawFake = createFakeStripe();
  let out = '';
  const code = await main(['--no-founder'], { env: { STRIPE_API_KEY: rawFake.key, STRIPE_API_BASE: 'http://localhost:12111' }, stdout: (t) => (out += t), stderr: () => {}, fetch: rawFake.fetch, sleep: async () => {}, now: new Date('2026-10-26T12:00:00Z') });
  assert.equal(code, 2);
  assert.match(out, /Managed Payments \(default\): tax_code txcd_10105002/);
  assert.ok(out.includes('txcd_10105002'));
});

test('--no-managed-payments is reported as not the chosen model and sets no tax code', async () => {
  const fake = createFakeStripe();
  const result = await run(['--apply', '--no-managed-payments'], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.match(result.out, /Managed Payments is OFF/);
  assert.ok([...fake.db.products.values()].every((p) => !p.tax_code));
});

test('a --lookup-prefix changes keys and ids but not the ownership marker', async () => {
  const fake = createFakeStripe();
  const result = await run(['--apply', '--lookup-prefix', 'rbx', '--skip-portal'], { fake });
  assert.equal(result.code, 0, result.out + result.err);
  assert.ok(fake.db.products.has('rbx_plan_creator'));
  assert.ok([...fake.db.prices.values()].some((p) => p.lookup_key === 'rbx:creator:month:usd'));
  assert.equal(fake.db.portals.size, 0);
});

test('portal: created with the safe feature set; drift is repaired; promotion codes are not enabled', async () => {
  const fake = createFakeStripe();
  await run(['--apply'], { fake });
  const [portal] = [...fake.db.portals.values()];
  assert.deepEqual(portal.features.subscription_cancel, { enabled: true, mode: 'at_period_end', proration_behavior: 'none' });
  assert.equal(portal.features.subscription_update.enabled, false);
  assert.equal(portal.features.invoice_history.enabled, true);
  assert.equal(portal.features.payment_method_update.enabled, true);
  assert.deepEqual([...portal.features.customer_update.allowed_updates].sort(), ['address', 'email', 'name']);
  assert.ok(!JSON.stringify(portal).includes('promotion'));

  portal.features.subscription_update.enabled = true;
  fake.reset();
  const dry = await run([], { fake });
  assert.equal(dry.code, 2);
  assert.match(dry.out, /update\s+portal\s+portal\s+subscription_update/);
  const fixed = await run(['--apply'], { fake });
  assert.equal(fixed.code, 0);
  assert.equal(portal.features.subscription_update.enabled, false);
  assert.equal(fake.db.portals.size, 1, 'updated in place, not duplicated');
});

test('transient 429 and 5xx are retried with backoff and then succeed', async () => {
  const fake = createFakeStripe({ failures: [429, 500, 503] });
  const result = await run(['--skip-portal'], { fake });
  assert.equal(result.code, 2, result.out + result.err);
  assert.ok(fake.log.length > 3);
});

test('a persistent outage fails after bounded retries with exit 1', async () => {
  const fake = createFakeStripe({ failures: Array(50).fill(500) });
  const result = await run(['--skip-portal'], { fake });
  assert.equal(result.code, 1);
  assert.equal(fake.log.length, 5, '1 try + 4 retries');
  assert.match(result.err, /HTTP 500/);
});

test('the default catalog path points at the real catalog', async () => {
  const fake = createFakeStripe();
  const withDefault = await run(['--skip-portal'], { fake });
  const explicit = await run(['--skip-portal', '--catalog', REAL_CATALOG], { fake });
  assert.equal(withDefault.out, explicit.out);
});

test('no price literal lives in the tool: every amount comes from the catalog', async () => {
  const { readdir } = await import('node:fs/promises');
  const dir = new URL('../lib/', import.meta.url);
  for (const file of await readdir(dir)) {
    const source = await readFile(new URL(file, dir), 'utf8');
    for (const literal of ['2900', '7900', '4900', '1500', '2500', '6000', '1800', '3500', '10500']) {
      assert.ok(!new RegExp(`\\b${literal}\\b`).test(source), `${file} contains ${literal}`);
    }
  }
});

test('portal: headline and the privacy and terms links come from --site-url; a changed link is repaired', async () => {
  const fake = createFakeStripe();
  const first = await run(['--apply'], { fake });
  assert.equal(first.code, 0, first.out + first.err);
  const portal = [...fake.db.portals.values()][0];
  assert.deepEqual(portal.business_profile, {
    headline: 'Manage your ReelBolt plan, payment method and invoices',
    privacy_policy_url: 'https://reelbolt.ai/legal/privacy',
    terms_of_service_url: 'https://reelbolt.ai/legal/terms',
  });

  portal.business_profile.terms_of_service_url = 'https://example.com/old-terms';
  const dry = await run([], { fake });
  assert.match(dry.out, /update\s+portal\s+portal\s+business_profile/);
  const fixed = await run(['--apply'], { fake });
  assert.equal(fixed.code, 0, fixed.out + fixed.err);
  assert.equal(portal.business_profile.terms_of_service_url, 'https://reelbolt.ai/legal/terms');

  const other = createFakeStripe();
  await run(['--apply', '--site-url', 'https://staging.example.com/'], { fake: other });
  assert.equal([...other.db.portals.values()][0].business_profile.privacy_policy_url, 'https://staging.example.com/legal/privacy');
});

test('--site-url must be an https origin', async () => {
  const fake = createFakeStripe();
  for (const bad of ['http://reelbolt.ai', 'https://user:pw@reelbolt.ai', 'https://reelbolt.ai/path', 'reelbolt.ai']) {
    const result = await run(['--site-url', bad], { fake });
    assert.equal(result.code, 1, bad);
    assert.match(result.err, /--site-url must be an https origin/);
  }
  assert.equal(fake.writes.length, 0);
});

test('yearly prices survive the stale-price archiver and monthly rows stay unchanged on re-run', async () => {
  const fake = createFakeStripe();
  await run(['--apply'], { fake });
  const before = [...fake.db.prices.values()].filter((p) => p.lookup_key?.includes(':month:')).map((p) => `${p.id}:${p.unit_amount}`);
  fake.reset();
  const again = await run(['--apply'], { fake });
  assert.equal(again.code, 0, again.out + again.err);
  assert.equal(writesTo(fake, 'POST', '/v1/prices').length, 0);
  assert.ok([...fake.db.prices.values()].filter((p) => p.lookup_key?.includes(':year:')).every((p) => p.active));
  assert.deepEqual([...fake.db.prices.values()].filter((p) => p.lookup_key?.includes(':month:')).map((p) => `${p.id}:${p.unit_amount}`), before);
});
