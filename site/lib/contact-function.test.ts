import { test } from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

// Behavioural test of the Cloudflare Pages contact function (functions/api/contact.ts). The Pages
// sources use bundler-style imports (extensionless relative paths, a JSON import without an import
// attribute) that plain `node --test` cannot load, so these hooks resolve `./x` to `./x.ts` and load
// `.json` as JSON. They only touch site sources, never node_modules.
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier.startsWith('.') && !/\.[cm]?[jt]sx?$|\.json$/.test(specifier)) {
      try {
        return nextResolve(`${specifier}.ts`, context);
      } catch {
        // fall through to the default resolution
      }
    }
    return nextResolve(specifier, context);
  },
  load(url, context, nextLoad) {
    if (url.endsWith('.json') && !url.includes('/node_modules/')) {
      return nextLoad(url, { ...context, importAttributes: { type: 'json' } });
    }
    return nextLoad(url, context);
  },
});

const { onRequestPost } = await import('../functions/api/contact.ts');

// A parsed JSON value that is not an object must never reach the handler's field reads: each is a
// 400 with the shared malformed-body error, and nothing is sent.
for (const [label, raw] of [
  ['null', 'null'],
  ['an array', '[]'],
  ['a string', '"hello"'],
  ['a number', '42'],
] as const) {
  test(`the Pages contact function answers 400 to ${label} body`, async () => {
    const request = new Request('https://example.test/api/contact', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'cf-connecting-ip': `203.0.113.${raw.length}` },
      body: raw,
    });
    const response = await onRequestPost({ request, env: {}, waitUntil: () => {} } as never);
    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), {
      ok: false,
      errors: [{ field: '_form', message: 'Invalid request.' }],
    });
  });
}
