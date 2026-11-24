// SEO checks on a BUILT site: <html lang>, canonical, hreflang (reciprocal, x-default = English),
// legal and blog carrying none, and the sitemap listing each language with alternates.
//
// Opt-in because it needs a build first: `npm run build && npm run test:build` (CI does this after
// the Build step). I18N_BUILD_DIR points at a different output, e.g. `out` for the static export.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { alternateLanguages, defaultLocale, localeList, localizePath, prefixedLocales } from './config.ts';
import { siteRoot } from './catalog-fixtures.ts';
import { AUDIENCE_PATHS } from '../lib/audiences/registry.ts';

const dir = join(siteRoot, process.env.I18N_BUILD_DIR ?? '.next/server/app');
const built = existsSync(dir);
const ORIGIN = process.env.NEXT_PUBLIC_SITE_URL || 'https://reelbolt.ai';
// The fixed pages and the six audience pages (`/for/<slug>`), which exist in every language too.
const PAGES = ['/', '/features', '/pricing', '/about', '/contact', ...AUDIENCE_PATHS];

function htmlOf(path: string): string {
  const file = path === '/' ? 'index.html' : `${path.replace(/^\//, '')}.html`;
  const candidates = [join(dir, file), join(dir, path === '/' ? 'index.html' : file)];
  // Next's server output names the home page `index.html`; a locale home is `<code>.html`.
  const hit = candidates.find((c) => existsSync(c));
  assert.ok(hit, `no built HTML for ${path} in ${dir}`);
  return readFileSync(hit, 'utf8');
}

const links = (html: string, rel: string) =>
  [...html.matchAll(/<link\b[^>]*>/g)]
    .map((m) => m[0])
    .filter((tag) => new RegExp(`rel="${rel}"`).test(tag))
    .map((tag) => ({
      hreflang: /hrefLang="([^"]+)"/i.exec(tag)?.[1],
      href: /href="([^"]+)"/.exec(tag)?.[1] as string,
    }));
const abs = (path: string) => new URL(path, ORIGIN).toString().replace(/\/$/, '');
const strip = (url: string) => url.replace(/\/$/, '');

const opts = { skip: built ? false : `no build at ${dir}` };

for (const locale of localeList) {
  for (const path of PAGES) {
    const url = localizePath(locale.code, path);
    test(`${url}: lang, canonical and hreflang`, opts, () => {
      const html = locale.code === defaultLocale ? htmlOf(path) : htmlOf(url);
      assert.match(html, new RegExp(`<html lang="${locale.htmlLang}"`));
      assert.deepEqual(links(html, 'canonical').map((l) => strip(l.href)), [abs(url)], 'canonical is the page itself');
      const want = Object.fromEntries(
        Object.entries(alternateLanguages(path)).map(([lang, p]) => [lang, abs(p)]),
      );
      const got = Object.fromEntries(links(html, 'alternate').filter((l) => l.hreflang).map((l) => [l.hreflang, strip(l.href)]));
      assert.deepEqual(got, want);
      assert.match(html, new RegExp(`property="og:locale" content="${locale.ogLocale}"`));
    });
  }
}

test('legal pages are English only: no hreflang, no prefixed copy', opts, () => {
  for (const path of ['/legal/terms', '/legal/privacy']) {
    const html = htmlOf(path);
    assert.match(html, /<html lang="en"/);
    assert.deepEqual(links(html, 'alternate').filter((l) => l.hreflang), [], path);
    for (const l of prefixedLocales) assert.equal(existsSync(join(dir, l, `${path.replace(/^\//, '')}.html`)), false);
  }
  for (const l of prefixedLocales) assert.equal(existsSync(join(dir, l, 'blog.html')), false);
});

test('the sitemap lists every translated page in every language with alternates, and legal once', opts, () => {
  const file = [join(dir, 'sitemap.xml.body'), join(dir, 'sitemap.xml')].find((f) => existsSync(f));
  assert.ok(file, 'no sitemap output');
  const xml = readFileSync(file, 'utf8');
  for (const path of PAGES) {
    for (const l of localeList) {
      assert.ok(xml.includes(`<loc>${abs(localizePath(l.code, path))}`), `sitemap lacks ${localizePath(l.code, path)}`);
    }
  }
  assert.match(xml, /<xhtml:link rel="alternate" hreflang="it"/);
  assert.match(xml, /<xhtml:link rel="alternate" hreflang="x-default"/);
  for (const l of prefixedLocales) {
    assert.ok(!xml.includes(`/${l}/legal`) && !xml.includes(`/${l}/blog`), 'no prefixed legal or blog URLs');
    assert.ok(!xml.includes(`/${l}/contact/thank-you`), 'the thank-you page is not listed');
  }
});
