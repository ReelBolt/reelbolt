import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { cleanFirstName, contactOptInFirstName, forwardSubscribe, subscribeUrl, trustedForwardHeaders } from './subscribe.ts';
import { MARKETING_WORDING, currentWording, splitPrivacyLink } from './wording.ts';

const registryText = readFileSync(new URL('./marketing-consent.wording.json', import.meta.url), 'utf8');
const registry = JSON.parse(registryText);

interface Call {
  url: string;
  init: RequestInit;
}

function recordingFetch(respond: (call: Call) => Promise<Response>) {
  const calls: Call[] = [];
  const fn = (async (url: string | URL | Request, init?: RequestInit) => {
    const call = { url: String(url), init: init ?? {} };
    calls.push(call);
    return respond(call);
  }) as typeof fetch;
  return { fn, calls };
}

test('the vendored registry pins v1 to its published text', () => {
  assert.equal(registry.current, '2026-10-marketing-v1');
  assert.equal(
    registry.versions['2026-10-marketing-v1'],
    'Email me ReelBolt product news, tips and offers. You can unsubscribe at any time from any email or in Settings. See our Privacy Policy.',
  );
  assert.ok(registryText.endsWith('}\n'), 'file ends with a single trailing newline');
});

test('MARKETING_WORDING is the current registry entry', () => {
  assert.deepEqual(MARKETING_WORDING, {
    version: registry.current,
    text: registry.versions[registry.current],
  });
});

test('currentWording rejects a registry whose current version has no text', () => {
  assert.throws(() => currentWording({ current: 'missing', versions: {} }), /no text/);
});

test('splitPrivacyLink keeps every character of the wording', () => {
  const parts = splitPrivacyLink(MARKETING_WORDING.text);
  assert.equal(parts.link, 'Privacy Policy');
  assert.equal(parts.before + parts.link + parts.after, MARKETING_WORDING.text);
  assert.deepEqual(splitPrivacyLink('no link here'), { before: 'no link here', link: null, after: '' });
});

test('subscribeUrl joins the API origin without doubling slashes', () => {
  assert.equal(subscribeUrl('http://inference:8080'), 'http://inference:8080/api/v1/marketing/subscribe');
  assert.equal(subscribeUrl('https://reelbolt.ai/'), 'https://reelbolt.ai/api/v1/marketing/subscribe');
});

test('forwardSubscribe POSTs the email, current wording version and source as JSON', async () => {
  const { fn, calls } = recordingFetch(async () => new Response('{"status":"checkEmail"}', { status: 202 }));
  const logs: string[] = [];
  await forwardSubscribe(' ada@example.com ', 'siteContact', 'http://inference:8080', { wordingVersion: MARKETING_WORDING.version,
    fetch: fn,
    log: (m) => logs.push(m),
  });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'http://inference:8080/api/v1/marketing/subscribe');
  assert.equal(calls[0].init.method, 'POST');
  assert.deepEqual(calls[0].init.headers, { 'content-type': 'application/json' });
  assert.deepEqual(JSON.parse(String(calls[0].init.body)), {
    email: 'ada@example.com',
    wordingVersion: '2026-10-marketing-v1',
    source: 'siteContact',
  });
  assert.ok(calls[0].init.signal instanceof AbortSignal, 'request carries a timeout signal');
  assert.deepEqual(logs, []);
});

test('forwardSubscribe logs and resolves on a non-2xx response', async () => {
  const { fn } = recordingFetch(async () => new Response('', { status: 503 }));
  const logs: unknown[][] = [];
  await forwardSubscribe('ada@example.com', 'siteContact', 'http://api', { wordingVersion: MARKETING_WORDING.version,
    fetch: fn,
    log: (...args) => logs.push(args),
  });
  assert.deepEqual(logs, [['[contact] newsletter subscribe responded with', 503]]);
});

test('forwardSubscribe swallows network errors and never logs the address', async () => {
  const { fn } = recordingFetch(async () => {
    throw new TypeError('fetch failed');
  });
  const logs: unknown[][] = [];
  await assert.doesNotReject(
    forwardSubscribe('ada@example.com', 'siteContact', 'http://api', { wordingVersion: MARKETING_WORDING.version, fetch: fn, log: (...a) => logs.push(a) }),
  );
  assert.equal(logs.length, 1);
  assert.ok(!JSON.stringify(logs).includes('ada@example.com'));
});

test('forwardSubscribe gives up after the timeout instead of hanging', async () => {
  const { fn } = recordingFetch(
    (call) =>
      new Promise<Response>((_resolve, reject) => {
        call.init.signal?.addEventListener('abort', () => reject(call.init.signal?.reason));
      }),
  );
  const logs: unknown[][] = [];
  const started = Date.now();
  // `AbortSignal.timeout` uses an unref'd timer. While this test waits on it nothing else is
  // pending, so without a ref'd timer of our own the process would simply exit here and node
  // would report this test and every test after it in the file as cancelled.
  const keepAlive = setInterval(() => undefined, 1000);
  try {
    await forwardSubscribe('ada@example.com', 'siteContact', 'http://api', { wordingVersion: MARKETING_WORDING.version,
      fetch: fn,
      timeoutMs: 50,
      log: (...a) => logs.push(a),
    });
  } finally {
    clearInterval(keepAlive);
  }
  assert.ok(Date.now() - started < 2000);
  assert.deepEqual(logs, [['[contact] newsletter subscribe failed', 'TimeoutError']]);
});

test('the default timeout is 5 seconds', async () => {
  const { SUBSCRIBE_TIMEOUT_MS } = await import('./subscribe.ts');
  assert.equal(SUBSCRIBE_TIMEOUT_MS, 5000);
});

const okFetch = () => recordingFetch(async () => new Response(null, { status: 202 }));
const sentHeaders = (call: Call) => new Headers(call.init.headers);

test('with a site token, forwardSubscribe sends the token and the client IP', async () => {
  const { fn, calls } = okFetch();
  await forwardSubscribe('ada@example.com', 'siteContact', 'http://api', { wordingVersion: MARKETING_WORDING.version,
    fetch: fn,
    siteToken: 's3cret',
    clientIp: '203.0.113.7',
  });
  const headers = sentHeaders(calls[0]);
  assert.equal(headers.get('x-reelbolt-site-token'), 's3cret');
  assert.equal(headers.get('x-reelbolt-client-ip'), '203.0.113.7');
  assert.equal(headers.get('content-type'), 'application/json');
});

test('without a site token, forwardSubscribe sends neither trusted header', async () => {
  for (const siteToken of [undefined, '', '   ']) {
    const { fn, calls } = okFetch();
    await forwardSubscribe('ada@example.com', 'siteContact', 'http://api', { wordingVersion: MARKETING_WORDING.version,
      fetch: fn,
      siteToken,
      clientIp: '203.0.113.7',
    });
    const headers = sentHeaders(calls[0]);
    assert.equal(headers.get('x-reelbolt-site-token'), null, `token ${JSON.stringify(siteToken)}`);
    assert.equal(headers.get('x-reelbolt-client-ip'), null, `token ${JSON.stringify(siteToken)}`);
  }
});

test('with a token but no client IP, only the token header is sent', async () => {
  for (const clientIp of [undefined, '', ' ']) {
    const { fn, calls } = okFetch();
    await forwardSubscribe('ada@example.com', 'siteContact', 'http://api', { wordingVersion: MARKETING_WORDING.version, fetch: fn, siteToken: 's3cret', clientIp });
    const headers = sentHeaders(calls[0]);
    assert.equal(headers.get('x-reelbolt-site-token'), 's3cret');
    assert.equal(headers.get('x-reelbolt-client-ip'), null);
  }
});

test('trustedForwardHeaders uses the contract header names', () => {
  assert.deepEqual(trustedForwardHeaders('t', '198.51.100.4'), {
    'X-ReelBolt-Site-Token': 't',
    'X-ReelBolt-Client-IP': '198.51.100.4',
  });
  assert.deepEqual(trustedForwardHeaders(undefined, '198.51.100.4'), {});
});

test('modules reachable from site/functions carry no JSON import attributes (Pages bundler rejects them)', () => {
  // wrangler 3's esbuild fails on `with { type: 'json' }` / `assert { type: 'json' }`.
  // Keep wording.ts (which needs the attribute for node --test) out of the functions' import graph.
  const reachable = [
    'functions/api/contact.ts',
    'functions/api/subscribe.ts',
    'functions/api/promotions.ts',
    'lib/marketing/subscribe.ts',
    'lib/marketing/function-wording.ts',
    'lib/marketing/wording-core.ts',
    'lib/validation.ts',
    'lib/bot-checks.ts',
    'lib/contact-body.ts',
    'lib/rate-limit.ts',
    'lib/promotions/active.ts',
    'lib/promotions/plan-prices.ts',
    'components/promo/copy.ts',
  ];
  for (const file of reachable) {
    const src = readFileSync(new URL(`../../${file}`, import.meta.url), 'utf8');
    assert.doesNotMatch(src, /['\"]\s*(with|assert)\s*\{\s*type\s*:/, `${file} has a JSON import attribute`);
    assert.doesNotMatch(src, /from\s+['"][^'"]*\/wording(\.ts)?['"]/, `${file} imports wording.ts`);
  }
});

test('the functions wording version equals the Next one', async () => {
  const { currentWording } = await import('./wording-core.ts');
  const raw = JSON.parse(readFileSync(new URL('./marketing-consent.wording.json', import.meta.url), 'utf8'));
  assert.equal(currentWording(raw).version, MARKETING_WORDING.version);
});

// ── Visitor language (i18n) ────────────────────────────────────────────────

test('allowedLocale keeps a registered code and drops everything else', async () => {
  const { allowedLocale } = await import('./subscribe.ts');
  assert.equal(allowedLocale('it', ['en', 'it']), 'it');
  for (const bad of ['fr', 'IT', '../it', '', undefined, null, 5, ['it']]) {
    assert.equal(allowedLocale(bad, ['en', 'it']), undefined, JSON.stringify(bad));
  }
  assert.equal(allowedLocale('it', undefined), undefined);
});

test('forwardSubscribe sends the locale when given, and omits the key otherwise', async () => {
  const withLocale = okFetch();
  await forwardSubscribe('ada@example.com', 'siteFooter', 'http://api', { wordingVersion: MARKETING_WORDING.version, fetch: withLocale.fn, locale: 'it' });
  assert.equal(JSON.parse(String(withLocale.calls[0].init.body)).locale, 'it');
  const without = okFetch();
  await forwardSubscribe('ada@example.com', 'siteFooter', 'http://api', { wordingVersion: MARKETING_WORDING.version, fetch: without.fn });
  assert.equal('locale' in JSON.parse(String(without.calls[0].init.body)), false);
});

test('handleFooterSubscribe forwards only an allowlisted locale', async () => {
  const { handleFooterSubscribe } = await import('./subscribe.ts');
  const seen: (string | undefined)[] = [];
  const forward = async (_e: string, _s: string, _u: string, deps: { locale?: string }) => {
    seen.push(deps.locale);
  };
  const base = { email: 'ada@example.com', consent: true, wordingVersion: MARKETING_WORDING.version, startedAt: 0, website: '' };
  for (const locale of ['it', 'xx', undefined]) {
    handleFooterSubscribe(
      { body: { ...base, locale }, now: 60_000, wordingVersion: MARKETING_WORDING.version, apiUrl: 'http://api', locales: ['en', 'it'] },
      forward as never,
    );
  }
  assert.deepEqual(seen, ['it', undefined, undefined]);
});

test('forwardSubscribe sends a trimmed firstName and omits the key when blank or invalid', async () => {
  const named = okFetch();
  await forwardSubscribe('ada@example.com', 'siteFooter', 'http://api', { wordingVersion: MARKETING_WORDING.version, fetch: named.fn, firstName: '  Ada ' });
  assert.equal(JSON.parse(String(named.calls[0].init.body)).firstName, 'Ada');
  for (const firstName of [undefined, '', '   ', 'x'.repeat(101), 'Ad\u0000a', 'Ad​a']) {
    const r = okFetch();
    await forwardSubscribe('ada@example.com', 'siteFooter', 'http://api', { wordingVersion: MARKETING_WORDING.version, fetch: r.fn, firstName });
    assert.equal('firstName' in JSON.parse(String(r.calls[0].init.body)), false);
  }
});

test('handleFooterSubscribe forwards a cleaned firstName and still accepts when it is invalid', async () => {
  const { handleFooterSubscribe } = await import('./subscribe.ts');
  const seen: (string | undefined)[] = [];
  const forward = async (_e: string, _s: string, _u: string, deps: { firstName?: string }) => {
    seen.push(deps.firstName);
  };
  const base = { email: 'ada@example.com', consent: true, wordingVersion: MARKETING_WORDING.version, startedAt: 0, website: '' };
  for (const firstName of [' Ada ', '', 'x'.repeat(101), 'Ad.a', 42, undefined]) {
    const out = handleFooterSubscribe(
      { body: { ...base, firstName }, now: 60_000, wordingVersion: MARKETING_WORDING.version, apiUrl: 'http://api' },
      forward as never,
    );
    assert.equal(out.status, 202);
  }
  assert.deepEqual(seen, ['Ada', undefined, undefined, undefined, undefined, undefined]);
});

test('cleanFirstName accepts letters, marks, spaces, apostrophes and hyphens only', () => {
  const accepted: [string, string][] = [
    ['Zoë', 'Zoë'],
    ["Mary-Jane O'Neil", "Mary-Jane O'Neil"],
    ['  Ada   Lovelace ', 'Ada Lovelace'],
    ['D’Arcy', 'D’Arcy'],
    ['Zoë', 'Zoë'],
    ['x'.repeat(40), 'x'.repeat(40)],
    ['Павел', 'Павел'],
    ['Γιώργος', 'Γιώργος'],
    ['محمد', 'محمد'],
    ['राहुल', 'राहुल'],
    ['민준', '민준'],
    ['李雷', '李雷'],
    ['田中さん', '田中さん'],
    ['สมชาย', 'สมชาย'],
    ['เอก', 'เอก'],
    ['ジョージ', 'ジョージ'],
    ['ב\u05B0\u05B7\u05BC', 'ב\u05B0\u05B7\u05BC'],
    ['ଅମିତ', 'ଅମିତ'],
  ];
  for (const [input, want] of accepted) assert.equal(cleanFirstName(input), want, input);
  for (const bad of ['evil.com', 'http://x', 'a@b', 'J.R.', '<b>', '12', '-Ann', "'Ann", 'A1', '', '   ', 'x'.repeat(41), 7, null, undefined,
    'ꓸꓸꓸ', 'ᐃᐧ', 'P\u0430ul', 'Ada\u02B0', 'A\u0316\u0317\u0318\u0319', '\u0301\u0302', 'ᏣᎳᎩ', 'Αda', 'A\u3164', 'A\u115F', 'A\u1160', 'A\uFFA0', 'A\u0337', 'A\u0338', 'A\u20DD', 'A\uFE0F', 'Aー', 'ＡＢＣ', '𝐀bc', '\u0E01\u0E50', 'ก๐']) {
    assert.equal(cleanFirstName(bad), undefined, String(bad));
  }
});

test('contactOptInFirstName returns the cleaned name only when marketingOptIn is exactly true', () => {
  assert.equal(contactOptInFirstName({ marketingOptIn: true, firstName: '  Ada   Lovelace ' }), 'Ada Lovelace');
  for (const marketingOptIn of [false, undefined, null, 'true', 1, {}]) {
    assert.equal(contactOptInFirstName({ marketingOptIn, firstName: 'Ada' }), undefined, String(marketingOptIn));
  }
  assert.equal(contactOptInFirstName({ firstName: 'Ada' }), undefined);
  for (const firstName of ['evil.com', '12', '', '   ', undefined, null, 5, {}, ['Ada']]) {
    assert.equal(contactOptInFirstName({ marketingOptIn: true, firstName }), undefined, String(firstName));
  }
});
