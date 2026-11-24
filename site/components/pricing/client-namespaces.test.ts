import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// A client component's `useT('ns…')` only resolves if its namespace reached the browser: the chrome
// ones, or the `extra` the page hands PageMessages. A missing one renders the raw key (the live
// "plans.billing.monthly" bug), so every client island under components/pricing is checked here.
const CHROME = ['layout', 'consent', 'promo'];

function pricingExtra(): string[] {
  const src = readFileSync(join(import.meta.dirname, '../pages/PricingPage.tsx'), 'utf8');
  const m = /PRICING_CLIENT_NAMESPACES\s*=\s*\[([^\]]*)\]/.exec(src);
  assert.ok(m, 'PRICING_CLIENT_NAMESPACES is declared');
  return [...m[1].matchAll(/'([^']+)'/g)].map((x) => x[1]);
}

test('every namespace a pricing client component reads reaches the browser', () => {
  const allowed = new Set([...CHROME, ...pricingExtra()]);
  const dirs = [import.meta.dirname, join(import.meta.dirname, '../promo')];
  let checked = 0;
  for (const dir of dirs) {
    for (const f of readdirSync(dir).filter((n) => n.endsWith('.tsx'))) {
      const src = readFileSync(join(dir, f), 'utf8');
      if (!/^['"]use client['"]/.test(src)) continue;
      for (const m of src.matchAll(/useT\(\s*'([^']+)'/g)) {
        const root = m[1].split('.')[0];
        assert.ok(allowed.has(root), `${f} reads '${m[1]}' but '${root}' is not sent to the browser`);
        checked++;
      }
    }
  }
  assert.ok(checked > 0);
});

test('BillingSwitch labels exist in every locale', () => {
  for (const loc of readdirSync(join(import.meta.dirname, '../../messages'))) {
    const plans = JSON.parse(readFileSync(join(import.meta.dirname, `../../messages/${loc}/plans.json`), 'utf8'));
    for (const k of ['label', 'monthly', 'annual']) assert.ok(plans.billing?.[k], `${loc}: plans.billing.${k}`);
  }
});
