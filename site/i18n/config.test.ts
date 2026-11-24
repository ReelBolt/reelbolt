import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  alternateLanguages,
  defaultLocale,
  docsHref,
  isLocale,
  isTranslatedPath,
  leavesLocaleLayout,
  localeList,
  localizePath,
  locales,
  prefixedLocales,
  splitLocalePath,
} from './config.ts';
import { AUDIENCE_PATHS } from '../lib/audiences/registry.ts';

test('the default locale is first and unprefixed; every other locale is prefixed', () => {
  assert.equal(locales[0], defaultLocale);
  assert.deepEqual(prefixedLocales, locales.filter((l) => l !== defaultLocale));
  assert.equal(localizePath(defaultLocale, '/pricing'), '/pricing');
  for (const l of prefixedLocales) assert.equal(localizePath(l, '/pricing'), `/${l}/pricing`);
});

test('the home page is /<code> with no trailing slash (the form Next serves without a redirect)', () => {
  for (const l of prefixedLocales) assert.equal(localizePath(l, '/'), `/${l}`);
  assert.equal(localizePath(prefixedLocales[0], '/#faq'), `/${prefixedLocales[0]}#faq`);
  assert.equal(localizePath(prefixedLocales[0], '/features#teams'), `/${prefixedLocales[0]}/features#teams`);
  assert.equal(localizePath(prefixedLocales[0], '/contact?x=1'), `/${prefixedLocales[0]}/contact?x=1`);
});

test('legal pages, the blog, the dashboard, API routes and external links keep their one URL', () => {
  const it = prefixedLocales[0];
  for (const path of [
    '/legal/terms', '/legal/privacy', '/legal/refunds', '/legal/acceptable-use', '/legal/runner-eula',
    '/blog', '/blog/hello-reelbolt', '/blog/tag/product', '/blog/rss.xml', '/app/login', '/app/signup?plan=creator',
    '/docs/', '/api/subscribe', 'https://example.com/x', 'mailto:a@b.c',
  ]) {
    assert.equal(localizePath(it, path), path, path);
    assert.equal(isTranslatedPath(path), false, path);
  }
});

test('isLocale is a strict allowlist: no traversal, no case folding, no default-prefixed form', () => {
  assert.equal(isLocale('it'), true);
  for (const bad of ['IT', '../it', 'it/', 'it\u0000', '', 'xx', undefined, null, 5, ['it']]) {
    assert.equal(isLocale(bad), false, JSON.stringify(bad));
  }
});

test('splitLocalePath undoes localizePath, and does not read /en/ or unregistered prefixes', () => {
  for (const l of prefixedLocales) {
    for (const p of ['/', '/pricing', '/contact/thank-you']) {
      assert.deepEqual(splitLocalePath(localizePath(l, p)), { locale: l, path: p });
    }
  }
  assert.deepEqual(splitLocalePath('/pricing'), { locale: defaultLocale, path: '/pricing' });
  assert.deepEqual(splitLocalePath('/xx/pricing'), { locale: defaultLocale, path: '/xx/pricing' });
  assert.deepEqual(splitLocalePath(`/${defaultLocale}/pricing`), { locale: defaultLocale, path: `/${defaultLocale}/pricing` });
});

test('hreflang: every translated page lists every language plus x-default (English), reciprocally', () => {
  for (const path of ['/', '/features', '/pricing', '/about', '/contact', '/runner']) {
    assert.equal(isTranslatedPath(path), true, path);
    const alt = alternateLanguages(path);
    assert.deepEqual(Object.keys(alt).sort(), [...localeList.map((l) => l.htmlLang), 'x-default'].sort());
    assert.equal(alt['x-default'], alt[localeList.find((l) => l.code === defaultLocale)!.htmlLang]);
    for (const l of localeList) assert.equal(alt[l.htmlLang], localizePath(l.code, path));
    // Reciprocity: the page of every alternate lists the same set.
    for (const target of Object.values(alt)) {
      const { path: base } = splitLocalePath(target);
      assert.deepEqual(alternateLanguages(base), alt);
    }
  }
});

test('the six audience pages are translated, with reciprocal hreflang; no other /for/ path is', () => {
  const it = prefixedLocales[0];
  for (const path of AUDIENCE_PATHS) {
    assert.equal(isTranslatedPath(path), true, path);
    assert.equal(localizePath(it, path), `/${it}${path}`);
    assert.equal(localizePath(it, `${path}?utm_source=x#make`), `/${it}${path}?utm_source=x#make`);
    assert.deepEqual(splitLocalePath(`/${it}${path}`), { locale: it, path });
    const alt = alternateLanguages(path);
    assert.deepEqual(Object.keys(alt).sort(), [...localeList.map((l) => l.htmlLang), 'x-default'].sort());
    assert.equal(alt['x-default'], path);
  }
  for (const path of ['/for', '/for/other', '/for/creator', '/for/creators/extra']) {
    assert.equal(isTranslatedPath(path), false, path);
    assert.equal(localizePath(it, path), path);
  }
});

test('docsHref points at the guide in the visitor language, never at a /<locale>/docs/ page of this app', () => {
  assert.equal(docsHref(defaultLocale), '/docs/');
  assert.equal(docsHref(defaultLocale, 'runner/'), '/docs/runner/');
  for (const code of prefixedLocales) {
    assert.equal(docsHref(code), `/docs/${code}/`);
    assert.equal(docsHref(code, 'runner/'), `/docs/${code}/runner/`);
  }
  assert.equal(docsHref('not-a-locale'), '/docs/');
});

test('leavesLocaleLayout: only a link from a prefixed language to an English-only page of this site', () => {
  for (const locale of prefixedLocales) {
    for (const path of ['/blog', '/blog/hello-reelbolt', '/legal/privacy', '/legal/terms#credits', '/blog?x=1']) {
      assert.equal(leavesLocaleLayout(locale, path), true, `${locale} ${path}`);
    }
    // A translated page stays in the language's layout; other apps and external links are not ours to decorate.
    for (const path of ['/', '/pricing', '/for/creators', '/recipes/abc', '/app/signup', '/app', '/api/contact', '/docs/it/', '/l/hn-launch', 'https://example.org/blog', '//example.org/blog', 'mailto:hello@reelbolt.ai', '#faq']) {
      assert.equal(leavesLocaleLayout(locale, path), false, `${locale} ${path}`);
    }
  }
  // English pages and the English-only pages share one root layout: client-side navigation, nothing to carry.
  for (const path of ['/blog', '/legal/privacy', '/pricing']) assert.equal(leavesLocaleLayout(defaultLocale, path), false, path);
  assert.equal(leavesLocaleLayout('zz', '/blog'), false);
});
