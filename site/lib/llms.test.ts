import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { buildLlmsFullTxt, buildLlmsTxt, type LlmsInput, type LlmsPost } from './llms.ts';
import type { Plan, PlanCatalog } from './pricing/index.ts';
import { formatMoney } from './pricing/offers.ts';
import { HOME_FAQS, PRICING_FAQS } from './faqs.ts';
import { siteCtas } from './site-config.ts';
import { AUDIENCES } from './audiences/registry.ts';

// The real catalog, read the same way offers.test.ts reads it, so the plan facts asserted below
// are the ones the site ships with.
// The loader's `TopUp` drops `expiresAfterMonths`; the route reads it straight from the JSON, so
// the test types that one field itself.
type RawCatalog = Omit<PlanCatalog, 'topUp'> & { topUp?: { expiresAfterMonths: number } };
const catalog: RawCatalog = JSON.parse(
  readFileSync(new URL('./pricing/plans.catalog.json', import.meta.url), 'utf8'),
);

const ORIGIN = 'https://reelbolt.test';

// Fixture metas, not `allPosts()`: posts.ts imports TSX modules that node --test cannot load.
const POSTS: LlmsPost[] = [
  {
    slug: 'hello-reelbolt',
    title: 'What ReelBolt is',
    description: 'ReelBolt is an AI video team for product marketing teams.',
    tags: ['product'],
  },
  {
    slug: 'reelbolt-and-other-tools',
    title: 'ReelBolt and other video tools',
    description: 'How ReelBolt compares with other video tools for product teams.',
    tags: ['comparisons'],
  },
];

function input(overrides: Partial<LlmsInput> = {}): LlmsInput {
  return {
    siteUrl: ORIGIN,
    name: 'ReelBolt',
    shortDescription: 'ReelBolt is the AI video team for product marketing teams.',
    cta: { label: 'Start free', href: '/app/signup' },
    plans: catalog.plans as Plan[],
    currency: catalog.currency,
    taxNote: catalog.taxNote,
    topUpExpiresAfterMonths: catalog.topUp?.expiresAfterMonths,
    posts: POSTS,
    faqs: { home: HOME_FAQS, pricing: PRICING_FAQS },
    runnerAvailable: false,
    runnerPath: '/runner',
    ...overrides,
  };
}

const BANNED = [
  'ffmpeg',
  'remotion',
  'docker',
  'postgresql',
  'postgres',
  'rabbitmq',
  'qdrant',
  'workflow engine',
  'paddle',
  'minimax',
  'higgsfield',
  'fal.ai',
  'fish audio',
  'comfyui',
  'typesafe',
];

test('llms.txt opens with the llmstxt.org shape: H1, blockquote summary, then sections', () => {
  const out = buildLlmsTxt(input());
  assert.ok(out.startsWith('# ReelBolt\n\n> ReelBolt is the AI video team'), out.slice(0, 120));
  assert.match(out, /\n## Key features\n/);
  assert.match(out, /\n## How pricing works\n/);
  assert.match(out, /\n## Where to go next\n/);
});

test('every link in llms.txt and llms-full.txt is an absolute URL on the site origin', () => {
  for (const out of [buildLlmsTxt(input()), buildLlmsFullTxt(input())]) {
    const links = [...out.matchAll(/\]\(([^)\s]+)\)/g)].map((match) => match[1]);
    assert.ok(links.length > 10, `expected many links, found ${links.length}`);
    for (const link of links) {
      assert.ok(link.startsWith(`${ORIGIN}/`), `relative or foreign link: ${link}`);
    }
    assert.doesNotMatch(out, /href="\//, 'an HTML relative href leaked into the text');
  }
});

test('llms.txt links the required pages, every post and the docs', () => {
  const out = buildLlmsTxt(input());
  for (const path of ['/', '/pricing', '/features', '/about', '/contact', '/blog', '/docs/', '/legal/privacy', '/legal/terms', '/legal/acceptable-use', '/legal/refunds', '/blog/tag/comparisons', ...AUDIENCES.map((a) => a.path)]) {
    assert.ok(out.includes(`](${ORIGIN}${path})`), `missing link to ${path}`);
  }
  for (const post of POSTS) {
    assert.ok(out.includes(`](${ORIGIN}/blog/${post.slug})`), `missing post ${post.slug}`);
  }
});

test('llms.txt names the six audiences, links their pages', () => {
  const out = buildLlmsTxt(input());
  for (const audience of AUDIENCES) {
    // Once under "Who it is for" and once in the page list.
    assert.equal(out.split(`](${ORIGIN}${audience.path})`).length - 1, 2, audience.path);
  }
  assert.match(out, /\n## Who it is for\n/);
  assert.doesNotMatch(out, /cannot do yet/);
  // The summary is the new positioning; software teams are one audience, not the frame.
  assert.match(out, /an AI video team for anyone who has a story to tell and no time to tell it/);
  assert.doesNotMatch(out, /AI video team for software companies/);
  assert.ok(out.includes('[Software teams]'), 'software teams stay listed');
});

test('the plan names, prices, credits and export limits come from the catalog', () => {
  const out = buildLlmsTxt(input());
  for (const plan of catalog.plans) {
    assert.ok(out.includes(plan.name), `missing plan name ${plan.name}`);
    assert.ok(out.includes(`Exports up to ${plan.limits.maxResolution}`), `missing export label for ${plan.name}`);
    if (plan.priceMonthly !== undefined) {
      assert.ok(out.includes(formatMoney(catalog.currency, plan.priceMonthly)), `missing price for ${plan.name}`);
    }
  }
  const trialCredits = catalog.plans.find((plan) => plan.kind === 'trial')?.creditsOneTime;
  assert.ok(trialCredits !== undefined, 'the catalog has a trial with one-time credits');
  assert.ok(out.includes(`${trialCredits} credits, one time`), 'trial must show its one-time credits');
  assert.doesNotMatch(out, /Trial: \$/, 'trial must not show a monthly price');
  assert.ok(out.includes('Enterprise: priced on request. Contact us.'), 'enterprise must read as contact us');
});

test('the pack-expiry sentence comes from the catalog and is dropped when absent', () => {
  assert.ok(buildLlmsTxt(input()).includes(`pack credits expire after ${catalog.topUp?.expiresAfterMonths} months`));
  assert.doesNotMatch(buildLlmsTxt(input({ topUpExpiresAfterMonths: undefined })), /credit packs/);
});

test('no banned term appears in llms.txt or llms-full.txt', () => {
  for (const out of [buildLlmsTxt(input()), buildLlmsFullTxt(input())]) {
    const lower = out.toLowerCase();
    for (const term of BANNED) {
      assert.ok(!lower.includes(term), `banned term "${term}" found`);
    }
  }
});

test('the primary CTA follows signupEnabled: Start free to /app/signup when on, Talk to us to /contact when off', () => {
  const on = buildLlmsTxt(input({ cta: siteCtas({ NEXT_PUBLIC_SIGNUP_ENABLED: 'true' }).primary }));
  assert.ok(on.includes(`[Start free](${ORIGIN}/app/signup)`), 'signup on: Start free to /app/signup');
  assert.ok(!on.includes('Talk to us'), 'signup on: no Talk to us link');

  const off = buildLlmsTxt(input({ cta: siteCtas({ NEXT_PUBLIC_SIGNUP_ENABLED: 'false' }).primary }));
  assert.ok(off.includes(`[Talk to us](${ORIGIN}/contact)`), 'signup off: Talk to us to /contact');
  assert.ok(!off.includes('/app/signup'), 'signup off: nothing points at /app/signup');
  assert.ok(!off.includes('Start free'), 'signup off: no Start free');
});

test('the FAQ section repeats the home and pricing FAQs exactly, in order', () => {
  const out = buildLlmsTxt(input());
  let at = 0;
  for (const faq of [...HOME_FAQS, ...PRICING_FAQS]) {
    const next = out.indexOf(`### ${faq.question}\n\n${faq.answer}`, at);
    assert.ok(next !== -1, `FAQ missing or out of order: ${faq.question}`);
    at = next;
  }
});

test('llms-full.txt starts with the whole of llms.txt and then lists every post with its URL and description', () => {
  const short = buildLlmsTxt(input());
  const full = buildLlmsFullTxt(input());
  assert.ok(full.startsWith(short.trimEnd()), 'llms-full must contain llms.txt verbatim first');
  for (const post of POSTS) {
    assert.ok(full.includes(`### ${post.title}\n\nURL: ${ORIGIN}/blog/${post.slug}\n\n${post.description}`), post.slug);
  }
});

test('the desktop runner is listed only when it is available', () => {
  assert.doesNotMatch(buildLlmsTxt(input({ runnerAvailable: false })), /Desktop runner/);
  assert.ok(buildLlmsTxt(input({ runnerAvailable: true })).includes(`](${ORIGIN}/runner)`));
});

test('the siteUrl trailing slash is normalised, so no URL has a double slash', () => {
  const out = buildLlmsFullTxt(input({ siteUrl: `${ORIGIN}/` }));
  assert.doesNotMatch(out, /reelbolt\.test\/\//);
  assert.ok(out.includes(`](${ORIGIN}/pricing)`));
});
