import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * The dashboard is served under basePath `/app`; the docs, blog, legal pages and the marketing site are
 * not. A Next `<Link href="/docs/...">` (or router.push) would be prefixed to `/app/docs/...`, a 404.
 * Those targets must be plain anchors (see `docsPageHref` in guide-links.ts).
 */
const ROOT = join(__dirname, '..', '..');
const SKIP = new Set(['node_modules', '.next', 'coverage', 'e2e']);
const OUTSIDE = '(docs|blog|legal|contact|pricing|features|about)';

function sources(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (SKIP.has(name)) continue;
    const full = join(dir, name);
    if (statSync(full).isDirectory()) sources(full, out);
    else if (/\.(tsx?)$/.test(name) && !/\.test\.tsx?$/.test(name)) out.push(full);
  }
  return out;
}

const LINK = new RegExp(`<Link\\b[^>]*\\bhref=\\{?\\s*["'\`]/${OUTSIDE}(?:[/"'\`#?]|$)`);
const COMPONENT_LINK = new RegExp(`component=\\{Link\\}[^>]*\\bhref=\\{?\\s*["'\`]/${OUTSIDE}(?:[/"'\`#?]|$)`);
// Non-literal hrefs: a docs helper/constant handed to next/link has the same basePath problem.
const DOCS_IDENT = '(?:docs\\w*|guideHref\\w*|\\w*GUIDE_URL|LEGAL_\\w+)';
const LINK_IDENT = new RegExp(`<Link\\b[^>]*\\bhref=\\{\\s*${DOCS_IDENT}\\b`);
const COMPONENT_LINK_IDENT = new RegExp(`component=\\{Link\\}[^>]*\\bhref=\\{\\s*${DOCS_IDENT}\\b`);
const HREF_FIRST_IDENT = new RegExp(`\\bhref=\\{\\s*${DOCS_IDENT}\\b[^>]*component=\\{Link\\}`);
const ROUTER = new RegExp(`router\\.(push|replace|prefetch)\\(\\s*["'\`]/${OUTSIDE}(?:[/"'\`#?]|$)`);

describe('dashboard links to routes outside the /app base path', () => {
  it('never use next/link or the router for /docs, /blog, /legal or marketing pages', () => {
    const offenders: string[] = [];
    for (const file of sources(ROOT)) {
      const text = readFileSync(file, 'utf8');
      if ([LINK, COMPONENT_LINK, LINK_IDENT, COMPONENT_LINK_IDENT, HREF_FIRST_IDENT, ROUTER].some((re) => re.test(text))) {
        offenders.push(relative(ROOT, file));
      }
    }
    expect(offenders).toEqual([]);
  });

  it('the guard recognises the broken pattern', () => {
    expect(LINK.test('<Link href="/docs/api/">x</Link>')).toBe(true);
    expect(LINK.test('<Link href="/dashboard">x</Link>')).toBe(false);
    expect(ROUTER.test("router.push('/docs/x')")).toBe(true);
    expect(COMPONENT_LINK_IDENT.test('<Button component={Link} href={DESKTOP_APP_GUIDE_URL}>')).toBe(true);
    expect(LINK_IDENT.test('<Link href={docsPageHref("a", l)}>')).toBe(true);
    expect(COMPONENT_LINK_IDENT.test('<Button component="a" href={docsPageHref("a", l)}>')).toBe(false);
  });
});
