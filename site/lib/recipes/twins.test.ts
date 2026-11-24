import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { handleRecipes } from '../../functions/_recipes-proxy.ts';
import { recipeShellResponse, type AssetsBinding } from './shell.ts';
import registry from '../../i18n/locales.json' with { type: 'json' };

const ID = '3f2b8c1e-5d4a-4b7e-9c61-0a1b2c3d4e5f';
const req = (path: string, init?: RequestInit) => new Request(`https://reelbolt.ai${path}`, init);
const reg = { defaultLocale: registry.defaultLocale, codes: registry.locales.map((l) => l.code) };

test('Pages twin: Range passthrough, 405, empty list when unset', async () => {
  const f = async () => new Response('abc', { status: 206, headers: { 'content-type': 'video/mp4', 'content-range': 'bytes 0-2/9' } });
  const m = await handleRecipes('preview', req(`/api/recipes/${ID}/preview`, { headers: { range: 'bytes=0-2' } }), { REELBOLT_API_URL: 'https://a.test' }, ID, f);
  assert.equal(m.status, 206);
  assert.equal(m.headers.get('content-range'), 'bytes 0-2/9');
  assert.equal((await handleRecipes('list', req('/api/recipes', { method: 'POST' }), {})).status, 405);
  assert.deepEqual(await (await handleRecipes('list', req('/api/recipes'), {})).json(), { items: [], nextCursor: null });
});

const assets = (files: Record<string, string>): AssetsBinding => ({
  fetch: async (i) => {
    const p = new URL(typeof i === 'string' ? i : i.url).pathname;
    return p in files ? new Response(files[p]) : new Response('nf', { status: 404 });
  },
});

test('shell: prebuilt page as is, unknown id gets noindex placeholder, bad prefix passes on', async () => {
  const env = { ASSETS: assets({ [`/recipes/${ID}`]: 'built', '/recipes/_': 'shell', '/it/recipes/_': 'shell-it' }) };
  const next = async () => new Response('next');
  assert.equal(await (await recipeShellResponse({ request: req(`/recipes/${ID}`), env, next }, undefined, reg)).text(), 'built');
  const s = await recipeShellResponse({ request: req('/recipes/11111111-2222-4333-8444-555555555555'), env, next }, undefined, reg);
  assert.equal(await s.text(), 'shell');
  assert.match(s.headers.get('x-robots-tag') ?? '', /noindex/);
  assert.equal(await (await recipeShellResponse({ request: req('/it/recipes/x'), env, next }, 'it', reg)).text(), 'shell-it');
  assert.equal(await (await recipeShellResponse({ request: req('/api/recipes/x'), env, next }, 'api', reg)).text(), 'next');
  assert.equal((await recipeShellResponse({ request: req(`/recipes/${ID}`), env: {}, next }, undefined, reg)).status, 404);
});

test('_routes.json covers every language prefix', () => {
  const r = JSON.parse(readFileSync(new URL('../../public/_routes.json', import.meta.url), 'utf8')) as { include: string[] };
  for (const l of registry.locales) if (l.code !== registry.defaultLocale) assert.ok(r.include.includes(`/${l.code}/recipes/*`));
  assert.ok(r.include.includes('/recipes/*') && r.include.includes('/api/*'));
});
