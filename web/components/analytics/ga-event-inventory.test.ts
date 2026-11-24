// The GA4 events the dashboard can send, pinned. the soft-open funnel document lists exactly this:
// one manual page_view on non-blocked routes, with a redacted path. No sign_up, begin_checkout or
// purchase is sent from /app, so GA4 cannot see a payment; the server-side tables can (see the doc).
// Adding a GA event means changing this test and that page on purpose, with the description of what
// Google receives (docs/provider-data-handling.md) in view.

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

const webRoot = join(__dirname, '..', '..');

function sources(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === '.next' || name.startsWith('.')) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) sources(path, out);
    else if (/\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name) && !name.endsWith('.d.ts')) out.push(path);
  }
  return out;
}

const files = ['app', 'components', 'lib'].flatMap((d) => sources(join(webRoot, d))).map((path) => ({
  path: path.replace(webRoot, ''),
  text: readFileSync(path, 'utf8'),
}));

describe('GA4 events sent from the dashboard', () => {
  it('sends only page_view, from the one GoogleAnalytics component', () => {
    const callers = files.filter(({ text }) => /gtag(\?\.)?\(\s*'event'/.test(text));
    expect(callers.map((f) => f.path)).toEqual(['/components/analytics/GoogleAnalytics.tsx']);
    const names = [...callers[0].text.matchAll(/gtag\(\s*'event',\s*'([a-z_]+)'/g)].map((m) => m[1]);
    expect(names).toEqual(['page_view']);
  });

  it('has no trackEvent helper and no commerce or sign-up event anywhere', () => {
    for (const { path, text } of files) {
      expect(text, path).not.toMatch(/trackEvent\(/);
      expect(text, path).not.toMatch(/['"](begin_checkout|add_payment_info|sign_up)['"]/);
      // 'purchase' is a plain word in billing copy and types, so only a GA event shape is refused.
      expect(text, path).not.toMatch(/gtag\([^)]*['"]purchase['"]/);
    }
  });
});
