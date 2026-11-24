import { test } from 'node:test';
import assert from 'node:assert/strict';
import { homeResponse, VARY } from '../functions/_home.ts';

const CODES = ['en', 'it'];
const page = () => Promise.resolve(new Response('home', { status: 200, headers: { 'cache-control': 'public' } }));
const get = (path: string, headers: Record<string, string> = {}, method = 'GET') =>
  homeResponse(new Request(`https://reelbolt.ai${path}`, { method, headers }), CODES, page);

test('an Italian browser on / is sent to /it with a private, uncacheable 302', async () => {
  const res = await get('/', { 'accept-language': 'it-IT,it;q=0.9,en;q=0.8' });
  assert.equal(res.status, 302);
  assert.equal(res.headers.get('location'), '/it');
  assert.equal(res.headers.get('cache-control'), 'private, no-store');
  assert.equal(res.headers.get('vary'), VARY);
});

test('campaign parameters survive the redirect', async () => {
  const res = await get('/?utm_source=x&utm_campaign=y', { 'accept-language': 'it' });
  assert.equal(res.headers.get('location'), '/it?utm_source=x&utm_campaign=y');
});

test('the rb_locale cookie beats Accept-Language in both directions', async () => {
  assert.equal((await get('/', { cookie: 'rb_locale=it', 'accept-language': 'en-US' })).status, 302);
  assert.equal((await get('/', { cookie: 'rb_locale=en', 'accept-language': 'it' })).status, 200);
});

test('English visitors get the page itself, with Vary', async () => {
  const res = await get('/', { 'accept-language': 'en-US,en;q=0.9' });
  assert.equal(res.status, 200);
  assert.equal(await res.text(), 'home');
  assert.match(res.headers.get('vary') ?? '', /Accept-Language/);
  assert.equal(res.headers.get('cache-control'), 'public', 'the page keeps its own cache policy');
});

test('a crawler with no headers gets English', async () => {
  assert.equal((await get('/')).status, 200);
});

test('deep links and other methods are never redirected, so there is no loop', async () => {
  const headers = { 'accept-language': 'it', cookie: 'rb_locale=it' };
  for (const path of ['/it', '/it/', '/it/pricing', '/pricing', '/features', '/blog', '/legal/terms', '/api/subscribe']) {
    assert.equal((await get(path, headers)).status, 200, path);
  }
  assert.equal((await get('/', headers, 'POST')).status, 200);
});

test('?for=<persona> on / goes to the audience page with the whole query kept', async () => {
  const res = await get('/?for=creator&utm_source=x&utm_content=hn');
  assert.equal(res.status, 302);
  assert.equal(res.headers.get('location'), '/for/creators?for=creator&utm_source=x&utm_content=hn');
  assert.equal(res.headers.get('cache-control'), 'private, no-store');
  assert.equal(res.headers.get('vary'), VARY);
  assert.equal((await get('/?for=%20Business%20')).headers.get('location'), '/for/small-business?for=%20Business%20');
});

test('a non-default language pick prefixes the audience page', async () => {
  const res = await get('/?for=agency&utm_source=x', { 'accept-language': 'it' });
  assert.equal(res.headers.get('location'), '/it/for/agencies?for=agency&utm_source=x');
});

test('other and unknown personas are ignored', async () => {
  for (const q of ['?for=other', '?for=nope', '?for=', '?utm_source=x']) assert.equal((await get(`/${q}`)).status, 200, q);
  assert.equal((await get('/?for=other', { 'accept-language': 'it' })).headers.get('location'), '/it?for=other');
});

test('audience pages and deep links with ?for are never redirected', async () => {
  for (const path of ['/for/creators?for=creator', '/it/for/creators?for=creator', '/pricing?for=creator']) {
    assert.equal((await get(path, { 'accept-language': 'it' })).status, 200, path);
  }
});
