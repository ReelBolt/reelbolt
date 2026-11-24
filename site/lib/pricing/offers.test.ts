import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import type { Plan, PlanCatalog } from './index.ts';
import {
  aggregateOfferNode,
  annualMonthlyEquivalent,
  annualMonthsFree,
  annualPlanOffer,
  annualPlanOffers,
  assistantAllowance,
  benefitLines,
  cardHighlights,
  creditsLabel,
  formatMoney,
  formatNumber,
  highestResolutionPlan,
  joinNames,
  normalizePlan,
  packLabels,
  planById,
  planCardLines,
  featureLines,
  planCtaHref,
  planCtaLabel,
  planMaxResolution,
  plansWith,
  plansWithMaxOutputHeight,
  pricedPlanOffer,
  pricedPlanOffers,
  resolutionLabel,
  sharedPaidLines,
  storageLabel,
  topExport,
} from './offers.ts';

// The catalog is read from disk rather than imported so this file stays runnable under
// `node --test` (no JSON import attributes there). The point of these tests is that every price,
// label and link on /pricing is derived from that one file — change a number there and the page,
// the Offer markup and these expectations move together.
const catalog = JSON.parse(
  readFileSync(new URL('./plans.catalog.json', import.meta.url), 'utf8'),
) as PlanCatalog;

const { currency } = catalog;
const plans = catalog.plans.map(normalizePlan);
const SITE = 'https://reelbolt.example';
const offers = pricedPlanOffers(plans, currency, SITE);

function plan(id: string): Plan {
  const found = plans.find((candidate) => candidate.id === id);
  assert.ok(found, `expected a "${id}" plan in the catalog`);
  return found;
}

test('every plan that publishes a price gets an offer, and contact plans get none', () => {
  const priced = plans.filter((candidate) => candidate.priceMonthly !== undefined).map((c) => c.id);
  const trials = plans.filter((candidate) => candidate.kind === 'trial').map((c) => c.id);
  assert.deepEqual(
    offers.map((offer) => offer.url.split('#')[1]),
    [...trials, ...priced],
    'offers must be the trial plus every priced plan, in catalog order',
  );
  assert.equal(pricedPlanOffer(plan('enterprise'), currency, SITE), undefined);
});

test('each offer carries the catalog price, in the catalog currency, verbatim', () => {
  for (const offer of offers) {
    assert.equal(offer.priceCurrency, currency);
    assert.equal(offer.priceCurrency, 'USD');
    // Acceptance for this WP: every price in the JSON-LD appears verbatim in the page text, and
    // the page prints it through this one formatter.
    assert.ok(
      formatMoney(currency, offer.price).includes(String(offer.price)),
      `"${formatMoney(currency, offer.price)}" must contain "${offer.price}"`,
    );
  }
});

test('a per-seat plan is priced per seat per month, a subscription per month, the trial once', () => {
  const team = pricedPlanOffer(plan('team'), currency, SITE);
  assert.equal(team?.price, plan('team').priceMonthly);
  assert.equal(team?.priceSpecification.unitText, 'seat per month');
  assert.equal(team?.priceSpecification.billingDuration, 'P1M');

  const pro = pricedPlanOffer(plan('pro'), currency, SITE);
  assert.equal(pro?.price, 79);
  assert.equal(pro?.priceSpecification.unitText, 'month');
  assert.equal(pro?.priceSpecification.billingDuration, 'P1M');

  const trial = pricedPlanOffer(plan('trial'), currency, SITE);
  assert.equal(trial?.price, 0);
  assert.equal(trial?.priceSpecification.billingDuration, undefined);
});

test('no price is hardcoded in this module: the offer follows the catalog', () => {
  const inflated: PlanCatalog = {
    ...catalog,
    plans: catalog.plans.map((candidate) =>
      candidate.id === 'creator' ? { ...candidate, priceMonthly: 12345 } : candidate,
    ),
  };
  const moved = pricedPlanOffers(inflated.plans.map(normalizePlan), inflated.currency, SITE);
  const creator = moved.find((offer) => offer.url === `${SITE}/pricing#creator`);
  assert.equal(creator?.price, 12345);
  assert.equal(creator?.priceSpecification.price, 12345);
});

test('the aggregate range spans the recurring prices and counts every offer', () => {
  const aggregate = aggregateOfferNode(plans, offers, currency);
  const recurring = plans
    .map((candidate) => candidate.priceMonthly)
    .filter((price): price is number => price !== undefined);
  assert.equal(aggregate?.lowPrice, Math.min(...recurring));
  assert.equal(aggregate?.highPrice, Math.max(...recurring));
  assert.equal(aggregate?.priceCurrency, currency);
  assert.equal(aggregate?.offerCount, offers.length);
  assert.ok(
    (aggregate?.lowPrice ?? 0) > 0,
    'the free trial must not drag lowPrice down to zero — it is a one-time grant, not a price',
  );
});

test('a catalog with no priced plan produces no aggregate rather than a broken one', () => {
  const freeOnly: PlanCatalog = {
    ...catalog,
    plans: catalog.plans.filter((candidate) => candidate.priceMonthly === undefined),
  };
  const freeOffers = pricedPlanOffers(freeOnly.plans.map(normalizePlan), currency, SITE);
  assert.equal(aggregateOfferNode(freeOnly.plans.map(normalizePlan), freeOffers, currency), undefined);
});

test('plan links follow the D25 switch', () => {
  assert.equal(planCtaHref(plan('creator'), true), '/app/signup?plan=creator');
  for (const candidate of plans.filter((p) => p.cta === 'signup')) {
    assert.equal(planCtaHref(candidate, false), '/contact');
  }
  assert.equal(planCtaHref(plan('enterprise'), false), '/contact?topic=enterprise');
  assert.equal(planCtaHref(plan('enterprise'), true), '/contact?topic=enterprise');
});

test('with signup live, the trial starts free, a paid plan is chosen by name, Enterprise talks', () => {
  assert.equal(planCtaLabel(plan('trial'), true), 'Start free');
  assert.equal(planCtaLabel(plan('creator'), true), 'Choose Creator');
  assert.equal(planCtaLabel(plan('pro'), true), 'Choose Pro');
  assert.equal(planCtaLabel(plan('team'), true), 'Choose Team');
  assert.equal(planCtaLabel(plan('enterprise'), true), 'Talk to us');
});

test('with signup off, every plan says Talk to us', () => {
  for (const candidate of plans) {
    assert.equal(planCtaLabel(candidate, false), 'Talk to us', `${candidate.id} must not promise signup`);
  }
});

test('credits are described per plan kind, never as a monthly grant on the trial', () => {
  // Grouped through the same formatter the page uses, so "1,000" here is the rendered string.
  assert.equal(creditsLabel(plan('trial')), `${formatNumber(plan('trial').creditsOneTime ?? 0)} credits, one time`);
  assert.equal(creditsLabel(plan('creator')), `${formatNumber(plan('creator').includedCredits)} credits every month`);
  assert.match(creditsLabel(plan('team')), /credits per seat every month, pooled/);
  assert.equal(creditsLabel(plan('enterprise')), 'Volume and terms on arrangement');
  for (const candidate of plans) {
    const label = creditsLabel(candidate);
    assert.ok(!/\$\d/.test(label), `"${label}" must not invent a price`);
  }
});

test('a card never prints the credits line twice', () => {
  for (const candidate of plans) {
    const bullets = cardHighlights(candidate);
    assert.ok(
      !bullets.includes(creditsLabel(candidate)),
      `"${creditsLabel(candidate)}" must not be repeated as a bullet`,
    );
    assert.ok(bullets.length > 0, 'every plan must keep at least one bullet');
  }
  // The trial's bullet is different copy, not a repeat, so it stays.
  assert.ok(cardHighlights(plan('trial')).includes('100 credits to try ReelBolt'));
  assert.deepEqual(cardHighlights(plan('pro')), [
    '300 assistant credits a month',
    '4K exports',
    'Multi-agent editing rooms',
    'Generated b-roll clips',
    '50 GB of storage',
    'Priority rendering',
  ]);
});

test('money formatting groups thousands and keeps the amount findable', () => {
  assert.equal(formatMoney('USD', 29), '$29');
  assert.equal(formatMoney('USD', 1000), '$1,000');
  assert.equal(formatMoney('EUR', 29), 'EUR 29');
});

test('a frame height reads as the label a buyer sees', () => {
  assert.equal(resolutionLabel(720), '720p');
  assert.equal(resolutionLabel(1080), '1080p');
  assert.equal(resolutionLabel(2160), '4K');
  assert.equal(resolutionLabel(4320), '8K');
});

test('names join with commas and a final "and", with no Oxford comma', () => {
  assert.equal(joinNames([]), '');
  assert.equal(joinNames(['Pro']), 'Pro');
  assert.equal(joinNames(['Pro', 'Team']), 'Pro and Team');
  assert.equal(joinNames(['Pro', 'Team', 'Enterprise']), 'Pro, Team and Enterprise');
});

test('entitlements are normalised so a missing figure reads as null, never undefined', () => {
  const enterprise = plan('enterprise').entitlements;
  assert.equal(enterprise.maxOutputHeight, null);
  assert.equal(enterprise.storageGb, null);
  assert.equal(enterprise.assistantMonthlyCredits, null);
  assert.equal(enterprise.assistantUnlimited, true);
  assert.equal(plan('trial').entitlements.storageGb, 1);
  assert.equal(plan('trial').entitlements.byoAllowed, false);
  assert.equal(plan('trial').entitlements.runnerAllowed, true);
  assert.equal(plan('team').entitlements.assistantMonthlyCredits, null);
  assert.equal(plan('team').entitlements.assistantMonthlyCreditsPerSeat, 100);
  assert.equal(plan('trial').topUpAllowed, false);
  assert.equal(plan('pro').topUpAllowed, true);
});

test('plan lists for the copy name the plans that carry each entitlement', () => {
  assert.equal(plansWith(plans, 'roomsAllowed'), 'Pro, Team and Enterprise');
  assert.equal(plansWith(plans, 'videoGenAllowed'), 'Pro, Team and Enterprise');
  assert.equal(plansWith(plans, 'byoAllowed'), 'Creator, Pro, Team and Enterprise');
  assert.equal(plansWithMaxOutputHeight(plans, 2160), 'Pro and Team');
});

test('the highest export is the plan with the largest height, first in catalog order on a tie', () => {
  const highest = highestResolutionPlan(plans);
  assert.equal(highest?.id, 'pro');
  assert.equal(highest?.limits.maxResolution, '4K');
  assert.equal(planMaxResolution(plan('trial')), '720p');
  assert.equal(planMaxResolution(plan('creator')), '1080p');
  assert.equal(planMaxResolution(plan('enterprise')), '4K');
});

test('assistant allowances read as a number, a per-seat figure or unlimited', () => {
  assert.equal(assistantAllowance(plan('trial')), '20');
  assert.equal(assistantAllowance(plan('creator')), '100');
  assert.equal(assistantAllowance(plan('pro')), '300');
  assert.equal(assistantAllowance(plan('team')), '100 per seat');
  assert.equal(assistantAllowance(plan('enterprise')), 'Unlimited');
});

test('storage shows only where the catalog sets a figure, and says shared on a seat plan', () => {
  assert.equal(storageLabel(plan('trial')), '1 GB');
  assert.equal(storageLabel(plan('team')), '100 GB shared');
  assert.equal(storageLabel(plan('enterprise')), undefined);
});

test('benefit lines appear only where their condition holds', () => {
  const trial = benefitLines(plan('trial'));
  assert.ok(trial.includes('Export up to 720p with a watermark'));
  assert.ok(trial.includes('1 GB of storage for your footage and exports'));
  assert.ok(!trial.includes('Cancel anytime and manage your plan yourself'));
  assert.ok(!trial.includes('Top up with credit packs when you need more'));

  const pro = benefitLines(plan('pro'));
  assert.ok(pro.includes('Export in 4K'), 'the top tier replaces "Export up to 4K"');
  assert.ok(!pro.includes('Export up to 4K'));
  assert.ok(pro.includes('Top up with credit packs when you need more'));
  assert.ok(pro.includes('Cancel anytime and manage your plan yourself'));

  const team = benefitLines(plan('team'));
  assert.ok(team.includes('100 GB of shared storage for your footage and exports'));
  assert.ok(team.includes('100 assistant credits per seat a month included, in the dashboard and in Claude Code'));

  const enterprise = benefitLines(plan('enterprise'));
  assert.ok(enterprise.includes('Unlimited assistant use'));
  assert.ok(enterprise.includes('Export up to 4K'));
  assert.ok(!enterprise.includes('Cancel anytime and manage your plan yourself'));
  assert.ok(!enterprise.includes('Top up with credit packs when you need more'));
});

test('the pro card never states the same number or resolution label twice', () => {
  const lines = planCardLines(plan('pro'));
  assert.ok(lines.includes('Export in 4K'), 'the benefit wording is the one kept');
  const numbers = new Map<string, string>();
  const resolutions = new Map<string, string>();
  for (const line of lines) {
    for (const [token] of line.matchAll(/(?<![\w.])\d[\d,]*(?!\w)/g)) {
      const key = token.replace(/,/g, '');
      assert.ok(!numbers.has(key), `"${line}" repeats the number ${key} from "${numbers.get(key)}"`);
      numbers.set(key, line);
    }
    for (const [label] of line.matchAll(/\b(?:\d{3,4}p|\d+K)\b/g)) {
      assert.ok(!resolutions.has(label), `"${line}" repeats ${label} from "${resolutions.get(label)}"`);
      resolutions.set(label, line);
    }
  }
});

test('the cancel and top-up lines that every paid card repeats are printed once, under the grid', () => {
  assert.deepEqual(sharedPaidLines(plans), [
    'Cancel anytime and manage your plan yourself',
    'Top up with credit packs when you need more',
  ]);
  const shared = sharedPaidLines(plans);
  for (const candidate of ['creator', 'pro', 'team']) {
    for (const line of shared) {
      assert.ok(!planCardLines(plan(candidate), shared).includes(line), `${candidate} repeats "${line}"`);
    }
  }
});

test('the Creator card has no two lines about top-ups', () => {
  const topUps = planCardLines(plan('creator'), sharedPaidLines(plans)).filter((l) => /top.?up/i.test(l));
  assert.ok(topUps.length <= 1, `Creator prints ${topUps.length} top-up lines: ${topUps.join(' | ')}`);
});

test('the Team card repeats no Pro line except its own export ceiling, and does not restate priority', () => {
  const shared = sharedPaidLines(plans);
  const pro = planCardLines(plan('pro'), shared);
  const team = planCardLines(plan('team'), shared);
  assert.ok(team.includes('Everything in Pro'), 'the inheritance line stays');
  for (const line of team) {
    if (/^Export /.test(line)) continue;
    assert.ok(!pro.includes(line), `Team repeats "${line}" from Pro`);
  }
  assert.ok(!team.includes('Priority rendering'));
});

test('every card states its own export ceiling; Pro and Team show 4K (spec G: Team lists 4K)', () => {
  const shared = sharedPaidLines(plans);
  for (const id of ['pro', 'team']) {
    const lines = planCardLines(plan(id), shared);
    assert.ok(lines.some((l) => /\b4K\b/.test(l)), `${id} card has no 4K line: ${lines.join(' | ')}`);
  }
  assert.ok(!planCardLines(plan('creator'), shared).some((l) => /\b4K\b/.test(l)), 'Creator never shows 4K');
});

test('a card prints a benefit line once, even when the catalog already says it', () => {
  for (const candidate of plans) {
    const lines = planCardLines(candidate);
    assert.equal(new Set(lines).size, lines.length, `duplicate bullet on ${candidate.id}`);
  }
  assert.equal(planCardLines(plan('enterprise')).filter((l) => l === 'Unlimited assistant use').length, 1);
});

test('a pack shows its credits with a thousands separator and its price through formatMoney', () => {
  const packs = [
    { credits: 500, price: 15 },
    { credits: 3000, price: 60 },
  ];
  assert.deepEqual(packLabels(currency, packs[0]), { credits: '500 credits', price: '$15' });
  assert.deepEqual(packLabels(currency, packs[1]), { credits: '3,000 credits', price: '$60' });
});

test('the top export names its label and every plan that reaches it', () => {
  assert.deepEqual(topExport(plans), { label: '4K', plans: 'Pro and Team' });
});

test('planById returns the plan, and throws for an id the catalog does not have', () => {
  assert.equal(planById(plans, 'creator').name, 'Creator');
  assert.throws(() => planById(plans, 'starter'), /no plan with id "starter"/);
});

test('cards state the capabilities the entitlements switch on, in plain words', () => {
  const shared = sharedPaidLines(plans);
  const creator = planCardLines(plan('creator'), shared);
  for (const line of [
    'No watermark on your exports',
    'Cut people and objects out of your video',
    'Create music and sound effects',
    'Clean up noisy audio',
    'Use your own AI provider',
    'Up to 2 video jobs at the same time',
    '15 GB of storage for your footage and exports',
  ]) assert.ok(creator.includes(line), `Creator is missing "${line}"`);
  assert.equal(creator.filter((l) => /own AI provider/i.test(l)).length, 1);
  const trial = planCardLines(plan('trial'));
  assert.ok(!trial.includes('Cut people and objects out of your video'));
  assert.ok(trial.includes('One video job at a time'));
  assert.ok(planCardLines(plan('enterprise')).includes('Unlimited storage'));
  assert.ok(planCardLines(plan('pro')).includes('50 GB of storage for your footage and exports'));
  assert.ok(planCardLines(plan('team')).includes('100 GB of shared storage for your footage and exports'));
});

test('card feature lines name no implementation tooling', () => {
  for (const p of plans) {
    for (const line of featureLines(p)) assert.ok(!/ffmpeg|remotion|docker|postgres|temporal|workflow/i.test(line), line);
  }
});

test('assistant lines: Team is per seat and never unlimited, Enterprise is unlimited', () => {
  const team = benefitLines(plan('team')).filter((l) => /assistant/i.test(l));
  assert.deepEqual(team, ['100 assistant credits per seat a month included, in the dashboard and in Claude Code']);
  const ent = benefitLines(plan('enterprise')).filter((l) => /assistant/i.test(l));
  assert.deepEqual(ent, ['Unlimited assistant use']);
});

test('annual offers derive from priceAnnual, bill P1Y, and skip trial and Enterprise', () => {
  const annual = annualPlanOffers(plans, currency, SITE);
  const expected = plans.filter((c) => c.priceAnnual !== undefined).map((c) => c.id);
  assert.deepEqual(annual.map((o) => o.url.split('#')[1]), expected);
  assert.equal(annualPlanOffer(plan('enterprise'), currency, SITE), undefined);
  for (const o of annual) {
    assert.equal(o.priceSpecification.billingDuration, 'P1Y');
    assert.equal(o.priceSpecification.referenceQuantity?.unitCode, 'ANN');
    assert.equal(o.price, o.priceSpecification.price);
  }
  const team = annualPlanOffer(plan('team'), currency, SITE);
  assert.equal(team?.price, plan('team').priceAnnual);
  assert.match(team?.priceSpecification.unitText ?? '', /seat/);
});

test('monthly offers and the aggregate range ignore the yearly prices', () => {
  assert.ok(offers.every((o) => o.priceSpecification.billingDuration !== 'P1Y'));
  const aggregate = aggregateOfferNode(plans, offers, currency);
  assert.equal(aggregate?.highPrice, Math.max(...plans.map((c) => c.priceMonthly ?? 0)));
});

test('the per-month equivalent and months free come from the two catalog prices', () => {
  const pro = plan('pro');
  assert.equal(annualMonthlyEquivalent(pro), Math.round(((pro.priceAnnual as number) / 12) * 100) / 100);
  assert.equal(annualMonthsFree(pro), 2);
  assert.equal(annualMonthlyEquivalent(plan('enterprise')), undefined);
  assert.equal(annualMonthsFree(plan('trial')), undefined);
});
