import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildListQuery, forwardRequestHeaders, passthroughResponseHeaders, proxyRecipes, recipesCacheKey, upstreamBase, upstreamUrl, type FetchLike } from './proxy.ts';

const API = 'https://api.example.test';
const ID = '3f2b8c1e-5d4a-4b7e-9c61-0a1b2c3d4e5f';
const card = { id: ID, name: 'Launch teaser', description: 'd', category: 'marketing', language: 'en', costClass: 'Uses credits', stepCount: 3, stepTypes: [], authorLabel: 'A', copyCount: 1, ratingAverage: null, ratingCount: 0, previewSeconds: 12, listedAt: '2026-01-01T00:00:00Z', version: 1 };

function fake(handler: (url: string, init: RequestInit) => Response) {
  const calls: { url: string; init: RequestInit }[] = [];
  const impl: FetchLike = async (url, init) => {
    calls.push({ url, init: init ?? {} });
    return handler(url, init ?? {});
  };
  return { impl, calls };
}
const req = (path: string, init?: RequestInit) => new Request(`https://reelbolt.ai${path}`, init);

test('only allowlisted query parameters go upstream, capped', () => {
  const q = buildListQuery(new URLSearchParams(`q=${'x'.repeat(300)}&category=marketing&cost=Uses%20credits&stepType=GenerateImage&language=it&sort=rated&cursor=abc_-123&limit=999&admin=1&token=s`));
  assert.equal(q.get('q')?.length, 100);
  assert.deepEqual([...q.keys()], ['q', 'category', 'cost', 'stepType', 'language', 'sort', 'cursor', 'limit']);
  assert.equal(q.get('limit'), '50');
});

test('invalid values are dropped; cost words keep their spaces', () => {
  assert.equal(buildListQuery(new URLSearchParams('sort=drop&category=a%20b&cursor=%3Cscript%3E&limit=abc&cost=a%3Cb')).toString(), '');
  assert.equal(buildListQuery(new URLSearchParams('cost=Uses%20credits%20and%20a%20voice')).get('cost'), 'Uses credits and a voice');
});

test('upstream URLs', () => {
  assert.equal(upstreamBase(undefined), null);
  assert.equal(upstreamBase('javascript:alert(1)'), null);
  assert.equal(upstreamUrl(API, 'detail', ID), `${API}/api/v1/public/marketplace/recipes/${ID}`);
  assert.equal(upstreamUrl(API, 'facets'), `${API}/api/v1/public/marketplace/facets`);
  assert.equal(upstreamUrl(API, 'detail', '../../x'), null);
});

test('cache key ignores unknown parameters', () => {
  assert.equal(recipesCacheKey('https://reelbolt.ai/api/recipes?q=a&z=1', 'list'), recipesCacheKey('https://reelbolt.ai/api/recipes?z=2&q=a', 'list'));
});

test('no cookies or Authorization go upstream; Range and If-* do for media', () => {
  const h = new Headers({ cookie: 's=1', authorization: 'Bearer x', range: 'bytes=0-9', 'if-none-match': '"e"' });
  const media = forwardRequestHeaders(h, true);
  assert.equal(media.get('range'), 'bytes=0-9');
  assert.equal(media.has('cookie') || media.has('authorization'), false);
  assert.deepEqual([...forwardRequestHeaders(h, false).keys()], ['accept']);
  const out = passthroughResponseHeaders(new Headers({ 'content-type': 'video/mp4', 'set-cookie': 'a=b', server: 'k' }));
  assert.equal(out.has('set-cookie') || out.has('server'), false);
  assert.equal(out.get('cache-control'), 'public, max-age=3600');
});

test('preview: 206 with Content-Range streams through, request carries Range only', async () => {
  const { impl, calls } = fake(() => new Response('0123456789', { status: 206, headers: { 'content-type': 'video/mp4', 'content-range': 'bytes 0-9/100', 'accept-ranges': 'bytes', 'cache-control': 'public, max-age=3600' } }));
  const { response } = await proxyRecipes({ apiUrl: API, kind: 'preview', id: ID, fetchImpl: impl, request: req(`/api/recipes/${ID}/preview`, { headers: { range: 'bytes=0-9', cookie: 's=1', authorization: 'B' } }) });
  assert.equal(response.status, 206);
  assert.equal(response.headers.get('content-range'), 'bytes 0-9/100');
  assert.equal(response.headers.get('cache-control'), 'public, max-age=3600');
  assert.equal(await response.text(), '0123456789');
  const sent = new Headers(calls[0].init.headers);
  assert.equal(sent.get('range'), 'bytes=0-9');
  assert.equal(sent.has('cookie') || sent.has('authorization'), false);
});

test('media: 304 passes; other statuses and wrong content types are a bare 404', async () => {
  const run = async (status: number, type = 'video/mp4') => (await proxyRecipes({ apiUrl: API, kind: 'preview', id: ID, fetchImpl: fake(() => new Response(null, { status, headers: { 'content-type': type } })).impl, request: req(`/api/recipes/${ID}/preview`) })).response;
  assert.equal((await run(304)).status, 304);
  for (const s of [404, 403, 416, 500]) assert.equal((await run(s)).status, 404);
  assert.equal((await run(200, 'text/html')).status, 404);
});

test('list: re-serialised through the parser, leaks dropped, cached', async () => {
  const { impl, calls } = fake(() => Response.json({ items: [{ ...card, authorUserId: 'leak' }, { nope: 1 }], nextCursor: 'c1' }));
  const { response, cacheable } = await proxyRecipes({ apiUrl: API, kind: 'list', fetchImpl: impl, request: req('/api/recipes?q=hi&bogus=1&limit=500') });
  const body = (await response.json()) as { items: Record<string, unknown>[]; nextCursor: string };
  assert.equal(cacheable, true);
  assert.equal(response.headers.get('cache-control'), 'public, max-age=60');
  assert.equal(body.items.length, 1);
  assert.equal('authorUserId' in body.items[0], false);
  assert.equal(calls[0].url, `${API}/api/v1/public/marketplace/recipes?q=hi&limit=50`);
});

test('failures: empty list/facets, bare 404 otherwise, no upstream URL; bad cursor 400 is an empty list', async () => {
  const boom = fake(() => { throw new Error(`ECONNREFUSED ${API}`); }).impl;
  const list = await proxyRecipes({ apiUrl: API, kind: 'list', fetchImpl: boom, request: req('/api/recipes') });
  assert.deepEqual(await list.response.json(), { items: [], nextCursor: null });
  const facets = await proxyRecipes({ apiUrl: API, kind: 'facets', fetchImpl: boom, request: req('/api/recipes/facets') });
  assert.deepEqual(await facets.response.json(), { costClasses: [], categories: [], stepTypes: [], languages: [] });
  const detail = await proxyRecipes({ apiUrl: API, kind: 'detail', id: ID, fetchImpl: boom, request: req(`/api/recipes/${ID}`) });
  assert.equal(detail.response.status, 404);
  assert.equal(await detail.response.text(), '');
  const bad = await proxyRecipes({ apiUrl: API, kind: 'list', fetchImpl: fake(() => new Response(`bad at ${API}`, { status: 400 })).impl, request: req('/api/recipes?cursor=zzz') });
  const text = await bad.response.text();
  assert.equal(text, JSON.stringify({ items: [], nextCursor: null }));
});

test('unset API: nothing called; bad id never reaches the API; only GET/HEAD', async () => {
  const { impl, calls } = fake(() => Response.json(card));
  assert.equal((await proxyRecipes({ apiUrl: undefined, kind: 'preview', id: ID, fetchImpl: impl, request: req('/x') })).response.status, 404);
  assert.equal((await proxyRecipes({ apiUrl: API, kind: 'detail', id: 'nope', fetchImpl: impl, request: req('/x') })).response.status, 404);
  assert.equal((await proxyRecipes({ apiUrl: API, kind: 'list', fetchImpl: impl, request: req('/x', { method: 'POST' }) })).response.status, 405);
  assert.equal(calls.length, 0);
  const head = await proxyRecipes({ apiUrl: API, kind: 'poster', id: ID, fetchImpl: fake(() => new Response('abc', { headers: { 'content-type': 'image/jpeg' } })).impl, request: req('/x', { method: 'HEAD' }) });
  assert.equal(await head.response.text(), '');
});
