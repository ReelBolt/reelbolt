import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import type { RawPlan } from './index.ts';

const raw = JSON.parse(readFileSync(new URL('./plans.catalog.json', import.meta.url), 'utf8')) as { plans: RawPlan[] };

test('priceAnnual is 10 x priceMonthly on every paid plan and absent on trial and enterprise', () => {
  for (const p of raw.plans) {
    if (p.kind === 'subscription' || p.kind === 'perSeat') {
      assert.equal(p.priceAnnual, (p.priceMonthly as number) * 10, p.id);
    } else {
      assert.equal(p.priceAnnual, undefined, p.id);
    }
  }
});

test('the vendored copy is byte-identical to the API catalog', () => {
  const api = readFileSync(new URL('../../../inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json', import.meta.url));
  assert.ok(api.equals(readFileSync(new URL('./plans.catalog.json', import.meta.url))));
});
