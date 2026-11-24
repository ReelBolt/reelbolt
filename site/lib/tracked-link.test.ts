import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  REQUEST_KIND_HEADERS,
  SITE_TOKEN_HEADER,
  TRACKED_LINK_TIMEOUT_MS,
  followTrackedLink,
  linkCode,
  relayLocation,
  trackedLinkFallback,
  trackedLinkHome,
  trackedLinkRequestHeaders,
  trackedLinkUpstream,
} from './tracked-link.ts';
import { onRequest } from '../functions/l/[code].ts';
import { onRequest as onOtherPath } from '../functions/l/[[path]].ts';

const API = 'https://api.test';
const LONG = '/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software';
const req = (path = '/l/hn-launch', init?: RequestInit) => new Request(`https://reelbolt.ai${path}`, init);

interface Call {
  url: string;
  init: RequestInit;
}

function upstream(respond: (call: Call) => Response | Promise<Response>) {
  const calls: Call[] = [];
  const fetchImpl = async (url: string, init?: RequestInit) => {
    const call = { url, init: init ?? {} };
    calls.push(call);
    return respond(call);
  };
  return { fetchImpl, calls };
}

const found = (location: string) => new Response(null, { status: 302, headers: { location, 'cache-control': 'no-store' } });

function assertHome(res: Response) {
  assert.equal(res.status, 302);
  assert.equal(res.headers.get('location'), '/');
  assert.equal(res.headers.get('cache-control'), 'no-store');
}

/** The home page with the link code attached: the click was not counted, the visit keeps its link. */
function assertLinkHome(res: Response, code = 'hn-launch') {
  assert.equal(res.status, 302);
  assert.equal(res.headers.get('location'), `/?utm_content=${code}`);
  assert.equal(res.headers.get('cache-control'), 'no-store');
  assert.equal(res.body, null);
}

test('linkCode accepts the contract pattern, lowercased, and nothing else', () => {
  for (const ok of ['hn-launch', 'ab', 'a1', '0x', 'a'.repeat(40), 'x-y-z']) assert.equal(linkCode(ok), ok);
  assert.equal(linkCode('HN-Launch'), 'hn-launch');
  assert.equal(linkCode(' hn-launch '), 'hn-launch');
  assert.equal(linkCode(['hn-launch']), 'hn-launch');
  for (const bad of ['a', '', '-ab', 'a_b', 'a.b', 'a b', 'a/b', 'a'.repeat(41), '../x', 'a%2fb', 'café', null, undefined, [], ['a', 'b']]) {
    assert.equal(linkCode(bad as string), null, JSON.stringify(bad));
  }
});

test('the upstream URL is {REELBOLT_API_URL}/api/v1/l/<code>, or nothing when the origin is unusable', () => {
  assert.equal(trackedLinkUpstream('https://reelbolt.ai', 'hn-launch'), 'https://reelbolt.ai/api/v1/l/hn-launch');
  assert.equal(trackedLinkUpstream('http://inference:8080/', 'ab'), 'http://inference:8080/api/v1/l/ab');
  for (const bad of [undefined, '', '  ', 'ftp://x', 'not a url']) assert.equal(trackedLinkUpstream(bad, 'ab'), null);
});

test('only a path on this host is relayed; anything else becomes the home page', () => {
  assert.equal(relayLocation(LONG), LONG);
  assert.equal(relayLocation('/'), '/');
  for (const bad of [
    'https://evil.test/',
    'http://reelbolt.ai/for/creators',
    '//evil.test/x',
    '/\\evil.test',
    '/ok\\path',
    'for/creators',
    'javascript:alert(1)',
    '/a b',
    '/a\r\nSet-Cookie: x=1',
    '/a\tb',
    `/${'a'.repeat(2048)}`,
    '',
    null,
    undefined,
  ]) {
    assert.equal(relayLocation(bad), '/', JSON.stringify(bad));
  }
});

test('a rejected Location becomes the fallback the caller names, which is the link home for a well-formed code', () => {
  assert.equal(trackedLinkFallback('hn-launch'), '/?utm_content=hn-launch');
  assert.equal(trackedLinkFallback(null), '/');
  assert.equal(relayLocation(LONG, '/?utm_content=hn-launch'), LONG);
  for (const bad of ['https://evil.test/', '//evil.test/x', '/\\evil.test', '', null, undefined]) {
    assert.equal(relayLocation(bad, '/?utm_content=hn-launch'), '/?utm_content=hn-launch', JSON.stringify(bad));
  }
  // The fallback is itself a path this module would relay, and it reads back as the link's code.
  assert.equal(relayLocation(trackedLinkFallback('hn-launch')), '/?utm_content=hn-launch');
  assert.equal(new URLSearchParams(trackedLinkFallback('a1-b2').slice(2)).get('utm_content'), 'a1-b2');
});

test("only the caller's User-Agent goes upstream when it sent nothing else we forward: no cookie, no Authorization, no address", () => {
  const incoming = new Headers({
    'user-agent': 'Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)',
    cookie: 'reelbolt_token=secret; rb_ft=abc',
    authorization: 'Bearer secret',
    'cf-connecting-ip': '203.0.113.9',
    'x-forwarded-for': '203.0.113.9',
    referer: 'https://news.ycombinator.com/',
  });
  const out = trackedLinkRequestHeaders(incoming);
  assert.deepEqual([...out.keys()], ['user-agent']);
  assert.equal(out.get('user-agent'), 'Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)');
  assert.deepEqual([...trackedLinkRequestHeaders(new Headers()).keys()], []);
  assert.equal(trackedLinkRequestHeaders(new Headers({ 'user-agent': 'x'.repeat(5000) })).get('user-agent')?.length, 512);
});

test("the browser's request-kind headers go upstream next to the User-Agent, and nothing else of the caller's does", () => {
  assert.deepEqual([...REQUEST_KIND_HEADERS], ['sec-fetch-mode', 'sec-fetch-dest', 'sec-purpose', 'purpose', 'x-purpose', 'x-moz']);

  // A person's click in a browser.
  const click = trackedLinkRequestHeaders(
    new Headers({
      'user-agent': 'Mozilla/5.0',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Dest': 'document',
      'sec-fetch-site': 'cross-site',
      'sec-fetch-user': '?1',
      cookie: 'rb_ft=abc',
      authorization: 'Bearer secret',
      'cf-connecting-ip': '203.0.113.9',
      'x-forwarded-for': '203.0.113.9',
      'x-real-ip': '203.0.113.9',
      referer: 'https://news.ycombinator.com/',
      'accept-language': 'it',
    }),
  );
  assert.deepEqual([...click.keys()].sort(), ['sec-fetch-dest', 'sec-fetch-mode', 'user-agent']);
  assert.equal(click.get('sec-fetch-mode'), 'navigate');
  assert.equal(click.get('sec-fetch-dest'), 'document');

  // A speculative load, in each browser's spelling.
  const prefetch = trackedLinkRequestHeaders(
    new Headers({
      'user-agent': 'Mozilla/5.0',
      'sec-fetch-mode': 'no-cors',
      'sec-fetch-dest': 'empty',
      'sec-purpose': 'prefetch;prerender',
      purpose: 'prefetch',
      'x-purpose': 'preview',
      'x-moz': 'prefetch',
    }),
  );
  assert.deepEqual([...prefetch.keys()].sort(), ['purpose', 'sec-fetch-dest', 'sec-fetch-mode', 'sec-purpose', 'user-agent', 'x-moz', 'x-purpose']);
  assert.equal(prefetch.get('sec-purpose'), 'prefetch;prerender');
  assert.equal(prefetch.get('x-purpose'), 'preview');
  assert.equal(prefetch.get('x-moz'), 'prefetch');

  // Absent or blank stays absent; an implausibly long value is cut.
  assert.deepEqual([...trackedLinkRequestHeaders(new Headers({ purpose: '  ' })).keys()], []);
  assert.equal(trackedLinkRequestHeaders(new Headers({ 'sec-purpose': 'p'.repeat(500) })).get('sec-purpose')?.length, 64);
});

test('the relay token goes upstream as X-ReelBolt-Site-Token only when one is configured, and never the caller\'s own', () => {
  assert.equal(SITE_TOKEN_HEADER, 'x-reelbolt-site-token');
  const caller = new Headers({ 'user-agent': 'Mozilla/5.0', 'X-ReelBolt-Site-Token': 'forged-by-the-caller' });

  assert.equal(trackedLinkRequestHeaders(caller, ' relay-secret ').get(SITE_TOKEN_HEADER), 'relay-secret');
  // No token configured: the header is not sent at all, whatever the caller sent.
  for (const unset of [undefined, '', '   ']) {
    assert.deepEqual([...trackedLinkRequestHeaders(caller, unset).keys()], ['user-agent'], JSON.stringify(unset));
  }
});

test('an active link: one upstream GET, manual redirect, short timeout, and the Location relayed as a 302', async () => {
  const { fetchImpl, calls } = upstream(() => found(LONG));
  const res = await followTrackedLink({
    apiUrl: API,
    code: 'hn-launch',
    request: req('/l/hn-launch?junk=1', { headers: { 'user-agent': 'Mozilla/5.0', cookie: 'a=b', authorization: 'Bearer t' } }),
    fetchImpl,
  });
  assert.equal(res.status, 302);
  assert.equal(res.headers.get('location'), LONG);
  assert.equal(res.headers.get('cache-control'), 'no-store');
  assert.equal(res.body, null);

  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, `${API}/api/v1/l/hn-launch`, "the click's own query string is not forwarded");
  assert.equal(calls[0].init.method, 'GET');
  assert.equal(calls[0].init.redirect, 'manual');
  assert.equal(calls[0].init.credentials, undefined);
  assert.ok(calls[0].init.signal instanceof AbortSignal);
  const sent = calls[0].init.headers as Headers;
  assert.deepEqual([...sent.keys()], ['user-agent']);
  assert.equal(sent.get('user-agent'), 'Mozilla/5.0');
  assert.ok(TRACKED_LINK_TIMEOUT_MS <= 5000, 'a visitor is waiting on this redirect');
});

test('the upstream request carries the request-kind headers and the relay token, and still no cookie or Authorization', async () => {
  const { fetchImpl, calls } = upstream(() => found(LONG));
  const res = await followTrackedLink({
    apiUrl: API,
    siteToken: 'relay-secret',
    code: 'hn-launch',
    request: req('/l/hn-launch', {
      headers: {
        'user-agent': 'Mozilla/5.0',
        'sec-fetch-mode': 'navigate',
        'sec-fetch-dest': 'document',
        'sec-purpose': 'prefetch',
        cookie: 'reelbolt_token=secret',
        authorization: 'Bearer t',
        'x-reelbolt-site-token': 'forged',
      },
    }),
    fetchImpl,
  });
  assert.equal(res.headers.get('location'), LONG);
  const sent = calls[0].init.headers as Headers;
  assert.deepEqual([...sent.keys()].sort(), ['sec-fetch-dest', 'sec-fetch-mode', 'sec-purpose', 'user-agent', 'x-reelbolt-site-token']);
  assert.equal(sent.get('x-reelbolt-site-token'), 'relay-secret');
  assert.equal(sent.get('sec-fetch-mode'), 'navigate');
  assert.equal(calls[0].url, `${API}/api/v1/l/hn-launch`, 'the token is a header, never part of the address');
  // The token never comes back to the visitor.
  for (const [name, value] of res.headers) assert.ok(!`${name}${value}`.includes('relay-secret'), name);
});

test('a re-cased code reaches the API in lower case', async () => {
  const { fetchImpl, calls } = upstream(() => found('/pricing?utm_content=hn-launch'));
  const res = await followTrackedLink({ apiUrl: API, code: 'HN-Launch', request: req('/l/HN-Launch'), fetchImpl });
  assert.equal(calls[0].url, `${API}/api/v1/l/hn-launch`);
  assert.equal(res.headers.get('location'), '/pricing?utm_content=hn-launch');
});

test('HEAD stays HEAD upstream (the API counts it as a link checker)', async () => {
  const { fetchImpl, calls } = upstream(() => found(LONG));
  const res = await followTrackedLink({ apiUrl: API, code: 'hn-launch', request: req('/l/hn-launch', { method: 'HEAD' }), fetchImpl });
  assert.equal(calls[0].init.method, 'HEAD');
  assert.equal(res.status, 302);
  assert.equal(res.headers.get('location'), LONG);
});

test('a malformed code goes to the bare home page without calling anything', async () => {
  const { fetchImpl, calls } = upstream(() => found(LONG));
  for (const code of ['a', '-bad', 'has_underscore', 'a'.repeat(41), '', undefined, ['a', 'b'], 'x&utm_content=other', 'a%26b']) {
    assertHome(await followTrackedLink({ apiUrl: API, siteToken: 'relay-secret', code: code as string, request: req(), fetchImpl }));
  }
  assert.equal(calls.length, 0);
});

test('an unset or unusable API origin sends a well-formed code home with the code, without calling anything', async () => {
  const { fetchImpl, calls } = upstream(() => found(LONG));
  for (const apiUrl of [undefined, '', 'ftp://x']) {
    assertLinkHome(await followTrackedLink({ apiUrl, code: 'hn-launch', request: req(), fetchImpl }));
  }
  // The code in the fallback is the lower-case one, as the API stores it.
  assertLinkHome(await followTrackedLink({ apiUrl: undefined, code: 'HN-Launch', request: req('/l/HN-Launch'), fetchImpl }));
  assert.equal(calls.length, 0);
});

test('a posted link never breaks: every upstream failure is a 302 to the home page that still names the link', async () => {
  const answers: (() => Response | Promise<Response>)[] = [
    () => new Response('nf', { status: 404 }), // tracked links switched off
    () => new Response('{"error":"x"}', { status: 500 }),
    () => new Response('busy', { status: 429 }),
    () => new Response('bad gateway', { status: 502 }),
    () => new Response('<html>home</html>', { status: 200 }), // a followed redirect, or a wrong host
    () => new Response(null, { status: 301, headers: { location: LONG } }), // not the API's answer
    () => new Response(null, { status: 302 }), // no Location
    () => found('https://evil.test/'),
    () => found('//evil.test/'),
    () => {
      throw new TypeError('fetch failed');
    },
    () => Promise.reject(new DOMException('The operation timed out.', 'TimeoutError')),
  ];
  for (const answer of answers) {
    const { fetchImpl } = upstream(answer);
    assertLinkHome(await followTrackedLink({ apiUrl: API, code: 'hn-launch', request: req(), fetchImpl }));
    // HEAD (a link checker) gets the same answer.
    assertLinkHome(await followTrackedLink({ apiUrl: API, code: 'hn-launch', request: req('/l/hn-launch', { method: 'HEAD' }), fetchImpl }));
  }
});

test('a slow API is cut off by the timeout and the visitor goes home', async () => {
  const fetchImpl = (_url: string, init?: RequestInit) =>
    new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(init.signal?.reason));
    });
  // AbortSignal.timeout does not keep Node's event loop alive on its own; a browser or a Worker has
  // no such concern. This timer only stops the test process from exiting under the pending fetch.
  const keepAlive = setTimeout(() => undefined, 5000);
  try {
    const started = Date.now();
    assertLinkHome(await followTrackedLink({ apiUrl: API, code: 'hn-launch', request: req(), fetchImpl, timeoutMs: 20 }));
    assert.ok(Date.now() - started < 2000);
  } finally {
    clearTimeout(keepAlive);
  }
});

test('the unknown-code answer of the API (302 to /) is relayed as it is', async () => {
  const { fetchImpl } = upstream(() => found('/'));
  assertHome(await followTrackedLink({ apiUrl: API, code: 'gone-link', request: req('/l/gone-link'), fetchImpl }));
});

test('other methods are refused without touching the API', async () => {
  const { fetchImpl, calls } = upstream(() => found(LONG));
  for (const method of ['POST', 'PUT', 'DELETE']) {
    const res = await followTrackedLink({ apiUrl: API, code: 'hn-launch', request: req('/l/hn-launch', { method }), fetchImpl });
    assert.equal(res.status, 405);
    assert.equal(res.headers.get('allow'), 'GET, HEAD');
  }
  assert.equal(calls.length, 0);
});

test('the Pages Function hands the route parameter, REELBOLT_API_URL and SITE_FORWARD_TOKEN to the shared logic', async () => {
  const real = globalThis.fetch;
  const seen: { url: string; headers: Headers }[] = [];
  globalThis.fetch = (async (url: string | URL | Request, init?: RequestInit) => {
    seen.push({ url: String(url), headers: new Headers(init?.headers) });
    return found(LONG);
  }) as typeof fetch;
  try {
    const browser = { headers: { 'user-agent': 'Mozilla/5.0', 'sec-fetch-mode': 'navigate', 'sec-fetch-dest': 'document' } };
    const res = await onRequest({ request: req('/l/hn-launch', browser), env: { REELBOLT_API_URL: API }, params: { code: 'hn-launch' } });
    assert.equal(res.status, 302);
    assert.equal(res.headers.get('location'), LONG);
    assert.deepEqual(seen.map((call) => call.url), [`${API}/api/v1/l/hn-launch`]);
    assert.equal(seen[0].headers.get('sec-fetch-mode'), 'navigate');
    assert.equal(seen[0].headers.get('sec-fetch-dest'), 'document');
    assert.equal(seen[0].headers.has('x-reelbolt-site-token'), false, 'no token configured, no header');

    // With the token in the environment, the upstream request carries it.
    await onRequest({ request: req('/l/hn-launch', browser), env: { REELBOLT_API_URL: API, SITE_FORWARD_TOKEN: 'relay-secret' }, params: { code: 'hn-launch' } });
    assert.equal(seen[1].headers.get('x-reelbolt-site-token'), 'relay-secret');

    // No API origin configured: the home page with the code, and no request at all.
    assertLinkHome(await onRequest({ request: req('/l/hn-launch'), env: { SITE_FORWARD_TOKEN: 'relay-secret' }, params: { code: 'hn-launch' } }));
    // A code that cannot exist: the bare home page.
    assertHome(await onRequest({ request: req('/l/x'), env: { REELBOLT_API_URL: API }, params: { code: 'x' } }));
    assert.equal(seen.length, 2);
  } finally {
    globalThis.fetch = real;
  }
});

test('the token is never logged: the relay and its Function write nothing to the console', () => {
  for (const file of ['./tracked-link.ts', '../functions/l/[code].ts', '../functions/l/[[path]].ts']) {
    assert.doesNotMatch(readFileSync(new URL(file, import.meta.url), 'utf8'), /console\.|\bprint\(/, file);
  }
});

test('every other path under /l/ goes home', () => {
  assertHome(trackedLinkHome());
  assertHome(onOtherPath());
});

test('_routes.json sends /l/* to the Functions and keeps the routes it had', () => {
  const routes = JSON.parse(readFileSync(new URL('../public/_routes.json', import.meta.url), 'utf8')) as { include: string[]; exclude: string[] };
  assert.ok(routes.include.includes('/l/*'));
  for (const kept of ['/', '/api/*', '/recipes/*', '/it/recipes/*']) assert.ok(routes.include.includes(kept), kept);
  assert.deepEqual(routes.exclude, []);
});

test('nothing reachable from the function uses a JSON import attribute, and no Node route twin exists', () => {
  // wrangler's bundler rejects `with { type: 'json' }` (npm run check:functions is the real check).
  for (const file of ['./tracked-link.ts', './recipes/proxy.ts', './recipes/types.ts', '../functions/l/[code].ts', '../functions/l/[[path]].ts']) {
    assert.doesNotMatch(readFileSync(new URL(file, import.meta.url), 'utf8'), /with\s*\{\s*type:/, file);
  }
  // The static export cannot hold a dynamic route handler, and nginx serves /l/ on the Node stack.
  assert.throws(() => readFileSync(new URL('../app/l/[code]/route.ts', import.meta.url)), /ENOENT/);
});
