import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test } from 'node:test';
import { createFakeStripe } from './fake-stripe.mjs';
import { LIVE_KEY, TEST_KEY, run, tempDir } from './helpers.mjs';

test('a live key without --i-mean-live is refused before any request, in apply mode', async () => {
  const fake = createFakeStripe({ key: LIVE_KEY });
  const result = await run(['--apply'], { fake });
  assert.equal(result.code, 1);
  assert.match(result.err, /LIVE Stripe account without --i-mean-live/);
  assert.equal(fake.log.length, 0, 'no request at all');
});

test('a live key may dry-run without the flag, and applies with it', async () => {
  const fake = createFakeStripe({ key: LIVE_KEY });
  const dry = await run([], { fake });
  assert.equal(dry.code, 2);
  assert.match(dry.out, /LIVE mode, dry run/);
  assert.equal(fake.writes.length, 0);
  const applied = await run(['--apply', '--i-mean-live'], { fake });
  assert.equal(applied.code, 0, applied.out + applied.err);
  assert.match(applied.out, /LIVE mode, APPLY/);
  assert.equal(fake.db.products.size, 6);
});

test('the key is only taken from the environment and must be a recognised secret or restricted key', async () => {
  const none = await run([], { key: '' });
  assert.equal(none.code, 1);
  assert.match(none.err, /STRIPE_API_KEY is not set/);
  const publishable = await run([], { key: 'pk_test_abc123' });
  assert.equal(publishable.code, 1);
  assert.match(publishable.err, /Publishable keys/);
  assert.ok(!publishable.err.includes('pk_test_abc123'));
  const flag = await run(['--api-key', 'sk_test_x']);
  assert.equal(flag.code, 1);
  assert.match(flag.err, /unknown option --api-key/);
  assert.ok(!flag.err.includes('sk_test_x'));
});

test('a base URL that is not api.stripe.com or loopback is refused and nothing is sent', async () => {
  for (const base of [
    'https://evil.example.com',
    'http://api.stripe.com',
    'https://api.stripe.com.evil.com',
    'http://localhost.evil.com:80',
    'ftp://localhost',
    'not a url',
  ]) {
    const fake = createFakeStripe();
    const result = await run([], { fake, env: { STRIPE_API_BASE: base } });
    assert.equal(result.code, 1, base);
    assert.equal(fake.log.length, 0, `${base}: no request`);
    assert.ok(!result.err.includes(TEST_KEY));
  }
});

test('the default base is https://api.stripe.com and loopback is accepted', async () => {
  const fake = createFakeStripe();
  await run(['--skip-portal'], { fake, env: { STRIPE_API_BASE: '' } });
  assert.ok(fake.log.length > 0);
  assert.ok(fake.log.every((r) => r.host === 'api.stripe.com' && r.protocol === 'https:'));
  const local = createFakeStripe();
  await run(['--skip-portal'], { fake: local, env: { STRIPE_API_BASE: 'http://127.0.0.1:12111' } });
  assert.ok(local.log.every((r) => r.host === '127.0.0.1:12111'));
});

test('redirects are never followed, so the key cannot leak to another host', async () => {
  let init;
  const result = await run(['--skip-portal'], {
    extra: {
      fetch: async (_url, i) => {
        init = i;
        return new Response('{"error":{"message":"nope"}}', { status: 401 });
      },
    },
  });
  assert.equal(init.redirect, 'error');
  assert.equal(result.code, 1);
});

test('the key never appears in stdout, stderr, the report or the SQL file, on success or failure', async () => {
  const secret = 'sk_test_' + 'SUPERSECRETVALUE0123456789';
  const dir = await tempDir();
  const outputs = [];
  const scenarios = [
    ['--apply', '--out', join(dir, 'a.json'), '--sql-out', join(dir, 'a.sql')],
    ['--out', join(dir, 'b.json'), '--sql-out', join(dir, 'b.sql')],
    ['--apply', '--tax-behavior', 'inclusive'],
    ['--bogus'],
  ];
  for (const args of scenarios) {
    const fake = createFakeStripe({ key: secret });
    if (args.includes('--tax-behavior')) {
      await run(['--apply'], { fake });
    }
    const result = await run(args, { fake });
    outputs.push(result.out, result.err);
  }
  const bad = createFakeStripe({ key: secret, failures: Array(10).fill(500) });
  const failed = await run([], { fake: bad });
  outputs.push(failed.out, failed.err);
  const wrongKey = createFakeStripe({ key: 'sk_test_other' });
  const unauthorised = await run([], { fake: wrongKey, key: secret });
  outputs.push(unauthorised.out, unauthorised.err);
  assert.equal(unauthorised.code, 1);
  for (const file of ['a.json', 'a.sql', 'b.json', 'b.sql']) {
    outputs.push(await readFile(join(dir, file), 'utf8'));
  }
  for (const text of outputs) {
    assert.ok(!text.includes(secret), 'full key leaked');
    assert.ok(!text.includes('SUPERSECRETVALUE'), 'key body leaked');
  }
});

test('the dry-run SQL cannot be executed by accident while it holds placeholders', async () => {
  const fake = createFakeStripe();
  const { out } = await run([], { fake });
  assert.match(out, /BEGIN;\nDO \$\$ BEGIN RAISE EXCEPTION 'dry-run output contains placeholders/);
  const applied = await run(['--apply'], { fake });
  assert.ok(!applied.out.includes('RAISE EXCEPTION'));
});

test('catalog problems fail cleanly with exit 1 and no request', async () => {
  const dir = await tempDir();
  const { writeJson } = await import('./helpers.mjs');
  const bad = await writeJson(dir, 'bad.json', { currency: 'USD', plans: [{ id: 'x', name: 'X', kind: 'subscription', priceMonthly: 0.001 }] });
  const fake = createFakeStripe();
  const result = await run(['--catalog', bad], { fake });
  assert.equal(result.code, 1);
  assert.match(result.err, /not representable/);
  assert.equal(fake.log.length, 0);
  const missing = await run(['--catalog', join(dir, 'nope.json')], { fake });
  assert.equal(missing.code, 1);
});

test('--help prints usage and exits 0 without needing a key', async () => {
  const result = await run(['--help'], { key: '' });
  assert.equal(result.code, 0);
  assert.match(result.out, /Dry run by default/);
});
