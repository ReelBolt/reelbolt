import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { planNamesFor, resolutionTiers, trialFacts } from './plan-gates.ts';
import { PAID_GATES } from './registry.ts';

// The same file the pricing page reads. The plan names an audience page prints are asserted
// against it, so "On Pro, Team and Enterprise" can only ever be what the catalog says.
const catalog = JSON.parse(readFileSync(new URL('../pricing/plans.catalog.json', import.meta.url), 'utf8')) as {
  plans: { id: string; name: string; kind: string; creditsOneTime?: number; limits: { maxResolution: string; watermark: boolean }; entitlements?: Record<string, unknown> }[];
};
const namesWhere = (test: (plan: (typeof catalog.plans)[number]) => boolean) => catalog.plans.filter(test).map((p) => p.name);

test('each paid gate names the plans whose catalog entitlement is set', () => {
  assert.deepEqual(planNamesFor('videoGen'), namesWhere((p) => p.entitlements?.videoGenAllowed === true));
  assert.deepEqual(planNamesFor('audioGen'), namesWhere((p) => p.entitlements?.audioGenAllowed === true));
  assert.deepEqual(planNamesFor('ownProvider'), namesWhere((p) => p.entitlements?.byoAllowed === true));
  assert.deepEqual(planNamesFor('api'), namesWhere((p) => p.entitlements?.apiAccess === true));
  assert.deepEqual(planNamesFor('noWatermark'), namesWhere((p) => p.limits.watermark === false));
});

test('a paid gate never includes the free trial, and always includes at least one plan', () => {
  const trial = catalog.plans.find((p) => p.kind === 'trial');
  assert.ok(trial, 'the catalog has a trial plan');
  for (const gate of PAID_GATES) {
    const names = planNamesFor(gate);
    assert.ok(names.length > 0, `${gate}: no plan includes it, so the page would promise nothing`);
    assert.ok(!names.includes(trial.name), `${gate} is marked paid but the trial includes it`);
  }
});

test('the trial facts the copy states come from the catalog', () => {
  const trial = catalog.plans.find((p) => p.kind === 'trial');
  assert.ok(trial);
  const facts = trialFacts();
  assert.equal(facts.credits, trial.creditsOneTime);
  assert.equal(facts.resolution, trial.limits.maxResolution);
  // The trust strip, the limits and the cost answer all say "with a watermark". If the catalog
  // ever drops the trial watermark, this fails and those strings must change with it.
  assert.equal(facts.watermark, true);
});

test('each plan without a watermark is listed once, with its OWN export ceiling, lowest first', () => {
  const tiers = resolutionTiers();
  const paid = catalog.plans.filter((p) => p.limits.watermark === false);
  // Every such plan is in exactly one tier, and that tier is the plan's own ceiling.
  assert.deepEqual(tiers.flatMap((tier) => tier.plans).sort(), paid.map((p) => p.name).sort());
  for (const plan of paid) {
    const tier = tiers.find((t) => t.plans.includes(plan.name));
    assert.ok(tier, plan.name);
    assert.equal(tier.resolution, plan.limits.maxResolution, `${plan.name} would be shown with another plan's resolution`);
  }
  // Lowest ceiling first, judged by the catalog's own numeric height where it has one.
  const heightOf = (resolution: string) =>
    Math.min(...paid.filter((p) => p.limits.maxResolution === resolution).map((p) => Number(p.entitlements?.maxOutputHeight ?? Infinity)));
  const heights = tiers.map((tier) => heightOf(tier.resolution));
  assert.deepEqual(heights, [...heights].sort((a, b) => a - b));
  // The trial is never in it.
  const trial = catalog.plans.find((p) => p.kind === 'trial');
  assert.ok(trial && !tiers.some((tier) => tier.plans.includes(trial.name)));
});

test('no plan named on the "no watermark" card is shown with a resolution above its own ceiling', () => {
  // The reported defect: the card is gated on every plan without a watermark, and one sentence
  // gave all of them the highest ceiling ("On Creator, Pro, Team and Enterprise: exports up to
  // 4K"), which Creator does not have. The card's plans and the tiers' plans are the same set, and
  // each tier's resolution is the ceiling of every plan in it.
  const gatePlans = planNamesFor('noWatermark');
  const tiers = resolutionTiers();
  assert.deepEqual(tiers.flatMap((tier) => tier.plans).sort(), [...gatePlans].sort());
  const rank = (plan: (typeof catalog.plans)[number]) => Number(plan.entitlements?.maxOutputHeight ?? Infinity);
  for (const tier of tiers) {
    const members = catalog.plans.filter((p) => tier.plans.includes(p.name));
    const capped = members.filter((p) => Number.isFinite(rank(p)));
    // All capped plans of one tier share one height: none sits below the resolution the tier prints.
    assert.equal(new Set(capped.map(rank)).size <= 1, true, `tier ${tier.resolution} mixes ceilings`);
  }
  // Today that is more than one tier. If the catalog ever gives every paid plan one ceiling the
  // sentence still reads correctly; this line only records why the split exists.
  const creator = catalog.plans.find((p) => p.id === 'creator');
  const pro = catalog.plans.find((p) => p.id === 'pro');
  assert.ok(creator && pro);
  if (creator.limits.maxResolution !== pro.limits.maxResolution) {
    assert.notEqual(
      tiers.find((t) => t.plans.includes(creator.name))?.resolution,
      tiers.find((t) => t.plans.includes(pro.name))?.resolution,
    );
  }
  // The card reads the tiers, not a single top figure, and its words leave both to the catalog.
  const card = readFileSync(new URL('../../components/audiences/CapabilityList.tsx', import.meta.url), 'utf8');
  assert.match(card, /resolutionTiers\(\)\.map\(/);
  for (const locale of ['en', 'it']) {
    const messages = JSON.parse(readFileSync(new URL(`../../messages/${locale}/audiences.json`, import.meta.url), 'utf8'));
    const copy = messages.capabilities.fullResolution;
    assert.match(copy.description, /\{tiers\}/, locale);
    assert.match(messages.page.make.resolutionTier, /\{resolution\}.*\{plans\}/, locale);
    assert.doesNotMatch(JSON.stringify(copy), /topRes/, locale);
  }
});

test('a catalog without the plan a line depends on fails loudly instead of printing a blank', () => {
  assert.throws(() => trialFacts([]), /no trial plan/);
  assert.throws(() => resolutionTiers([{ id: 't', name: 'T', kind: 'trial', limits: { maxResolution: '720p', watermark: true } }]), /no plan without a watermark/);
  assert.deepEqual(planNamesFor('videoGen', []), []);
});
