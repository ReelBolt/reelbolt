import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

import { HOME_FAQS } from './faqs.ts';
import { SIGNUP_ONLY_TRUST_ITEMS, TRUST_ITEMS, trustItems } from './proof/trust.ts';
import { siteConfig } from './site-config.ts';

// The positioning and the claim rules of docs/marketing/copy-deck.md (sections 1 and "Claim
// rules"), held as tests so a later edit cannot drift back to a software-only message, invent a
// number or promise something the product does not do.

const read = (locale: string, ns: string) =>
  JSON.parse(readFileSync(new URL(`../messages/${locale}/${ns}.json`, import.meta.url), 'utf8'));
const source = (path: string) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');

function strings(node: unknown, prefix = ''): [string, string][] {
  if (typeof node === 'string') return [[prefix, node]];
  if (Array.isArray(node)) return node.flatMap((v, i) => strings(v, `${prefix}[${i}]`));
  return Object.entries(node as Record<string, unknown>).flatMap(([k, v]) => strings(v, prefix ? `${prefix}.${k}` : k));
}

/** The namespaces that carry the new message, in both languages. */
const MESSAGE_NAMESPACES = ['home', 'audiences', 'proof'];
const LOCALES = ['en', 'it'];

test('the hero says the new positioning, in both languages', () => {
  const en = read('en', 'home').hero;
  assert.equal(en.title, 'You have a story. ReelBolt makes the video.');
  assert.equal(en.eyebrow, 'AI video team · for anyone with a story to tell');
  assert.match(en.intro, /^Drop in the clips you already shot and say what the video is for\./);
  assert.match(en.intro, /reviews it before you see it/);
  assert.equal(en.support, 'No card required. Your first video is on us.');
  const it = read('it', 'home').hero;
  assert.equal(it.title, 'Tu hai una storia. ReelBolt ne fa un video.');
  assert.equal(it.eyebrow, 'Team video AI · per chi ha una storia da raccontare');
  for (const hero of [en, it]) {
    for (const text of [hero.title, hero.eyebrow, hero.intro]) assert.doesNotMatch(text, /software/i);
  }
});

test('the site description is one sentence in two places, and it is not about software only', () => {
  // `siteConfig.shortDescription` feeds the meta description, the manifest and llms.txt;
  // `seo.description` feeds the JSON-LD. They are the same words.
  assert.equal(read('en', 'seo').description, siteConfig.shortDescription);
  assert.match(siteConfig.shortDescription, /anyone with a story to tell/);
  assert.doesNotMatch(siteConfig.shortDescription, /software|product marketing|every release/i);
  assert.equal(siteConfig.tagline, 'Your story, made into a video.');
  for (const locale of LOCALES) {
    const seo = read(locale, 'seo');
    const { home } = seo.pages;
    for (const text of [seo.description, seo.ogTitle, seo.ogAlt, home.title, home.descriptionSignup, home.descriptionContact]) {
      assert.doesNotMatch(text, /software/i, `${locale}: ${text}`);
    }
  }
});

test('the home page renders its sections in the agreed order', () => {
  const page = source('components/pages/HomePage.tsx');
  const body = page.slice(page.indexOf('function HomePageBody'));
  const order = [
    '<Hero />',
    '<UseCaseStrip />',
    '<AudiencePicker id="who" />',
    '<BeforeAfter',
    '<HowItWorks',
    '<TrustStrip />',
    '<SampleGallery />',
    '<Testimonials />',
    '<FounderNote />',
    '<Section id="whats-inside"',
    '<Section id="faq"',
    '<Section id="get-started"',
  ];
  let at = -1;
  for (const marker of order) {
    const next = body.indexOf(marker, at + 1);
    assert.ok(next > at, `${marker} is missing or out of order`);
    at = next;
  }
  // The anchors the footer links to still exist (the hero's section rail was removed).
  assert.match(source('components/audiences/HowItWorks.tsx'), /id = 'how-it-works'/);
  assert.ok(body.includes('id="whats-inside"'));
  assert.ok(body.includes('id="get-started"'));
});

test('the first FAQ answer is for everyone, and the limits are stated', () => {
  assert.equal(HOME_FAQS[0].question, 'What does ReelBolt actually make?');
  assert.doesNotMatch(HOME_FAQS[0].answer, /for software products/i);
  assert.match(HOME_FAQS[0].answer, /for software teams/, 'software stays one audience among several');
  const who = HOME_FAQS.find((faq) => faq.question === 'Who is ReelBolt for?');
  assert.ok(who);
  for (const audience of ['creators', 'small businesses', 'educators', 'nonprofits', 'software teams', 'agencies']) {
    assert.ok(who.answer.includes(audience), `"Who is ReelBolt for?" must name ${audience}`);
  }
  // Never "type a prompt, get a video" for a free user.
  const footage = HOME_FAQS.find((faq) => /footage, or does it create/.test(faq.question));
  assert.ok(footage);
  assert.match(footage.answer, /On the free trial, no recipe makes a video from a text prompt alone or from photos alone\./);
  const limits = HOME_FAQS.find((faq) => faq.question === 'What can ReelBolt not do yet?');
  assert.ok(limits);
  assert.match(limits.answer, /public alpha/);
  // The home answers carry no figure (copy deck rule; the pricing answers are tested in faqs.test.ts).
  for (const faq of HOME_FAQS) assert.doesNotMatch(faq.answer, /\d/, `a number in "${faq.question}"`);
});

test('the trust facts keep their exact meaning', () => {
  const items = read('en', 'proof').trust.items;
  assert.deepEqual(Object.keys(items), ['noCard', 'firstVideo', 'reviewed', 'refund', 'speed', 'alpha']);
  assert.deepEqual(Object.keys(items), [...TRUST_ITEMS]);
  assert.equal(items.refund.body, 'If a step fails on our side, the credits held for it are refunded.');
  // The review fact is scoped to what is reviewed. The subtitle, thumbnail and post-text recipes
  // have no review step, and they are listed on the same pages, so "every draft" was too wide.
  assert.equal(
    items.reviewed.body,
    'Every edited cut is scored before you see it; a low score sends it back for another pass. Subtitles, thumbnails and post text are made in a single pass.',
  );
  assert.doesNotMatch(items.reviewed.title + items.reviewed.body, /every draft/i);
  assert.equal(items.speed.body, 'Minutes for a small job, sometimes half an hour for a big one.');
  // The first-video fact carries its conditions: the grant expires, is given once per person and
  // is refused for some connections and addresses. "No surprises" has to include those.
  assert.equal(
    items.firstVideo.body,
    '{credits} credits when you verify your email, on top of the {trialCredits} trial credits, to use within {days, plural, one {# day} other {# days}}. They are given once per person, and they are not available from some connections or with a temporary email address. Trial exports are {resolution} with a watermark.',
  );
  assert.match(items.alpha.body, /public alpha\. Expect rough edges/);
  // Never the stronger claims the register forbids.
  const all = strings(read('en', 'proof')).concat(strings(read('en', 'home')), strings(read('en', 'audiences')));
  for (const [key, text] of all) {
    assert.doesNotMatch(text, /never pay for (a )?fail/i, key);
    assert.doesNotMatch(text, /for life|locked in|guarantee/i, key);
    assert.doesNotMatch(text, /\bbeta\b/i, key);
  }
});

test('no figure for credits, resolution or a plan is typed into the new copy', () => {
  // These come from the plan catalog and the first-video constant at render time.
  for (const locale of LOCALES) {
    for (const ns of MESSAGE_NAMESPACES) {
      for (const [key, text] of strings(read(locale, ns))) {
        assert.doesNotMatch(text, /\b(750|100|850)\b/, `${locale}/${ns}:${key} types a credit amount`);
        assert.doesNotMatch(text, /\b(720p|1080p|4K)\b/, `${locale}/${ns}:${key} types a resolution`);
        // "Creator" alone is also the Italian word for the creator audience, and "Team" an ordinary
        // word, so those two are caught only when they are used as a plan.
        assert.doesNotMatch(text, /\b(Pro|Enterprise)\b/, `${locale}/${ns}:${key} types a plan name`);
        assert.doesNotMatch(
          text,
          /\b(Creator|Team) plans?\b|\bpian[oi] (Creator|Team)\b|\b(Creator|Team) (and|or|e|o) (Pro|Team|Enterprise)\b/,
          `${locale}/${ns}:${key} types a plan name`,
        );
      }
    }
  }
});

test('no invented number, customer or rating appears in the new copy', () => {
  const invented = [
    /\b\d+\s?x\b/i, // "10x faster"
    /\b\d+\s?%/, // "80% less time"
    /saves?\s+(you\s+)?\d/i,
    /\d+\s+(hours?|ore)\b/i,
    /\b(thousands|millions|hundreds)\s+of\b/i,
    /\bmigliaia di\b/i,
    /\btrusted by\b/i,
    /\b(customers|users|creators|teams)\s+(love|trust)\b/i,
    /★|⭐|\b\d(\.\d)?\s*\/\s*5\b/,
    /\bstars?\b|\brated\b|\b(customer|user) reviews?\b|\brecension[ei]\b/i,
  ];
  for (const locale of LOCALES) {
    for (const ns of MESSAGE_NAMESPACES) {
      for (const [key, text] of strings(read(locale, ns))) {
        for (const pattern of invented) assert.doesNotMatch(text, pattern, `${locale}/${ns}:${key}`);
      }
    }
  }
});

test('the voice rules hold: no hype words, no exclamation marks, no emoji', () => {
  const hype = /\b(revolutionary|seamless(ly)?|unlock|leverage|game-?changing|effortless(ly)?|supercharge|rivoluzionari[oa]|senza sforzo)\b/i;
  const emoji = /\p{Extended_Pictographic}/u;
  for (const locale of LOCALES) {
    for (const ns of [...MESSAGE_NAMESPACES, 'seo', 'faq']) {
      for (const [key, text] of strings(read(locale, ns))) {
        assert.doesNotMatch(text, hype, `${locale}/${ns}:${key}`);
        assert.doesNotMatch(text, /!/, `${locale}/${ns}:${key} has an exclamation mark`);
        assert.doesNotMatch(text, emoji, `${locale}/${ns}:${key} has an emoji`);
        assert.doesNotMatch(text, /—.*—/, `${locale}/${ns}:${key} stacks em dashes`);
      }
    }
  }
});

test('the editor is labelled an early preview wherever the new copy offers it, never beta', () => {
  const en = read('en', 'audiences').capabilities;
  for (const id of ['editor', 'reviewLinks', 'handoff']) assert.match(en[id].description, /early preview/i, id);
  assert.match(read('en', 'home').page.features.editor.description, /early preview/);
  const it = read('it', 'audiences').capabilities;
  for (const id of ['editor', 'reviewLinks', 'handoff']) assert.match(it[id].description, /anteprima anticipata/i, id);
  assert.match(read('it', 'home').page.features.editor.description, /anteprima anticipata/);
});

test('an audience page carries one primary CTA at the top and one at the bottom, both with its persona', () => {
  const page = source('components/pages/AudiencePage.tsx');
  const ctas = page.match(/<PrimaryCta [^>]*\/>/g) ?? [];
  assert.equal(ctas.length, 2);
  assert.match(ctas[0], /location=\{audience\.cta\.hero\} persona=\{audience\.persona\}/);
  assert.match(ctas[1], /location=\{audience\.cta\.final\} persona=\{audience\.persona\}/);
  // The header must not gain a nav item for the audience pages (e2e measures its width).
  assert.doesNotMatch(JSON.stringify(siteConfig.nav), /\/for\//);
  // They are reachable from the home picker and the footer.
  assert.match(source('components/layout/SiteFooter.tsx'), /AUDIENCES\.map\(\(audience\) => \(/);
  assert.match(source('components/audiences/AudiencePicker.tsx'), /href=\{audience\.path\}/);
});

test('the first-video grant is never stated with its figure and without its conditions', () => {
  // Wherever the amount of the grant is printed (`{credits}`), its lifetime and the fact that it is
  // not given to everyone are printed with it, in both languages.
  for (const locale of LOCALES) {
    const stated = [
      ['proof:trust.items.firstVideo.body', read(locale, 'proof').trust.items.firstVideo.body],
      ['audiences:costFaq.a', read(locale, 'audiences').costFaq.a],
    ] as const;
    for (const [key, text] of stated) {
      assert.match(text, /\{credits\}/, `${locale}/${key}`);
      assert.match(text, /\{days, plural,/, `${locale}/${key} does not say when the grant expires`);
      assert.match(text, /once per person|una volta per persona/, `${locale}/${key}`);
      assert.match(text, /not available from some connections|non sono disponibili da alcune connessioni/, `${locale}/${key}`);
      assert.match(text, /\{resolution\}/, `${locale}/${key} does not state the trial export limit`);
    }
  }
  // The lifetime is the constant the Terms print, passed as a number so each language words it.
  for (const file of ['components/proof/TrustStrip.tsx', 'components/pages/AudiencePage.tsx']) {
    assert.match(source(file), /days: FIRST_VIDEO_GRANT_EXPIRY_DAYS,/, file);
  }
  assert.match(source('lib/legal-placeholders.ts'), /FIRST_VIDEO_GRANT_EXPIRY = `\$\{FIRST_VIDEO_GRANT_EXPIRY_DAYS\} days`;/);
});

test('a free start is promised only on a build where the button starts a free account', () => {
  // `NEXT_PUBLIC_SIGNUP_ENABLED` defaults to false: the only button is "Talk to us" and the signup
  // API answers 404. Every string that tells a visitor to start free, or that no card is needed,
  // must sit behind `siteConfig.signupEnabled`. These are the ones that do, by catalogue key:
  const gated = new Set<string>([
    'home:hero.support',
    'home:page.start.bodySignup',
    'audiences:page.support',
    'audiences:page.final.bodySignup',
    'audiences:costFaq.q',
    'audiences:costFaq.a',
    'seo:pages.home.descriptionSignup',
    ...SIGNUP_ONLY_TRUST_ITEMS.flatMap((item) => [`proof:trust.items.${item}.title`, `proof:trust.items.${item}.body`]),
  ]);
  const promise = /start free|no card|first video is on us|free trial comes with|inizia gratis|senza carta|nessuna carta|non serve una carta|primo video è offerto/i;
  for (const locale of LOCALES) {
    for (const ns of [...MESSAGE_NAMESPACES, 'seo']) {
      for (const [key, text] of strings(read(locale, ns))) {
        if (gated.has(`${ns}:${key}`)) continue;
        assert.doesNotMatch(text, promise, `${locale}/${ns}:${key} promises a free start outside the signup switch`);
      }
    }
    // And the gated ones really are the strings that make the promise (the list holds no dead key).
    const all = new Set([...MESSAGE_NAMESPACES, 'seo'].flatMap((ns) => strings(read(locale, ns)).map(([k]): string => `${ns}:${k}`)));
    for (const key of gated) assert.ok(all.has(key), `${locale}: ${key} is not in the catalogue`);
  }

  // Each gated string is read behind the switch, in the component that renders it.
  const home = source('components/pages/HomePage.tsx');
  assert.match(home, /description: siteConfig\.signupEnabled \? t\('pages\.home\.descriptionSignup'\) : t\('pages\.home\.descriptionContact'\)/);
  assert.match(home, /siteConfig\.signupEnabled\s*\? t\('start\.bodySignup'\)\s*: t\('start\.bodyContact'\)/);
  assert.match(source('components/home/Hero.tsx'), /\{siteConfig\.signupEnabled && \(\s*<p[^>]*>\{t\('support'\)\}<\/p>/);
  const audience = source('components/pages/AudiencePage.tsx');
  assert.match(audience, /\{siteConfig\.signupEnabled && \(\s*<p[^>]*>\{t\('page\.support'\)\}<\/p>/);
  assert.match(audience, /siteConfig\.signupEnabled \? t\('page\.final\.bodySignup'\) : t\('page\.final\.bodyContact'\)/);
  assert.match(audience, /\.\.\.\(siteConfig\.signupEnabled \? \[\{ question: t\('costFaq\.q', values\), answer: t\('costFaq\.a', values\) \}\] : \[\]\)/);
  assert.equal(audience.match(/costFaq/g)?.length, 2, 'the cost answer is read in one place, behind the switch');
  assert.match(source('components/proof/TrustStrip.tsx'), /const items = trustItems\(siteConfig\.signupEnabled\);/);

  // The trust strip itself: the two facts about starting free are dropped, the other four stay.
  assert.deepEqual([...trustItems(true)], [...TRUST_ITEMS]);
  assert.deepEqual([...trustItems(false)], ['reviewed', 'refund', 'speed', 'alpha']);
  assert.deepEqual([...SIGNUP_ONLY_TRUST_ITEMS], ['noCard', 'firstVideo']);
});

test('captions are offered as a choice, never stated as part of every cut', () => {
  // The recipe a new account starts on (the highlight edit) ships with its Captions switch off,
  // and so do the titled edit and the testimonial reel. A person who presses Run with the defaults
  // gets no captions, so no sentence may say the team "captions it".
  const en = [...strings(read('en', 'home')), ...strings(read('en', 'audiences')), ...strings(read('en', 'seo'))];
  for (const [key, text] of en) {
    assert.doesNotMatch(text, /captions it\b|cuts, captions|cut, captioned|adds titles and captions|the titles and the captions|captions in place/i, `en:${key}`);
  }
  const it = [...strings(read('it', 'home')), ...strings(read('it', 'audiences')), ...strings(read('it', 'seo'))];
  for (const [key, text] of it) {
    assert.doesNotMatch(text, /lo monta, aggiunge i sottotitoli|lo sottotitola|sottotitolato e rivisto|aggiunge titoli e sottotitoli|dei titoli e dei sottotitoli|sottotitoli al loro posto/i, `it:${key}`);
  }
  // What the hero says instead, in both languages.
  assert.match(read('en', 'home').hero.intro, /cuts it and reviews it before you see it, and adds captions when you ask\./);
  assert.match(read('it', 'home').hero.intro, /lo monta e lo rivede prima che tu lo veda, e aggiunge i sottotitoli quando li chiedi\./);
  assert.equal(read('en', 'seo').ogSubtitle, 'Cut and reviewed by an AI video team. Ready to publish.');
});

test('the review claim is not made for the recipes that have no review step', () => {
  // The educator page leads with subtitles and translated subtitles, which are made in one pass.
  for (const [locale, wide] of [['en', /every draft is reviewed/i], ['it', /ogni bozza viene rivista/i]] as const) {
    const hero = read(locale, 'audiences').items.educator.hero.intro;
    assert.doesNotMatch(hero, wide, locale);
  }
  assert.match(read('en', 'audiences').items.educator.hero.intro, /and scores the cut before you see it\. It can also burn in subtitles/);
  assert.match(source('lib/llms.ts'), /scores every edited cut/);
  assert.doesNotMatch(source('lib/llms.ts'), /scores every draft/);
});

test('Claude Code is not said to show the app\u2019s confirmations', () => {
  // In the app a change waits on a card the server builds; over Claude Code the asking is done by
  // the person's own client. The copy says who asks, in both languages.
  for (const locale of LOCALES) {
    for (const ns of ['audiences', 'features']) {
      for (const [key, text] of strings(read(locale, ns))) {
        assert.doesNotMatch(text, /same confirmations|stesse conferme/i, `${locale}/${ns}:${key}`);
        if (/Claude Code/.test(text)) assert.doesNotMatch(text, /the same tools|gli stessi strumenti/i, `${locale}/${ns}:${key}`);
      }
    }
  }
  assert.match(read('en', 'audiences').capabilities.claudeCode.description, /Claude Code asks you before it changes or spends anything\./);
});

test('nothing a page lists is labelled as costing no credits', () => {
  // The thumbnail recipe runs no AI model, but its video analysis and frame grab are metered as
  // processing time, so "no credits" was not true. It says "very few" instead.
  for (const locale of LOCALES) {
    const audiences = read(locale, 'audiences');
    assert.deepEqual(Object.keys(audiences.page.make.gate).sort(), ['paid', 'trial'], locale);
    assert.doesNotMatch(audiences.page.make.intro, /unless they say otherwise|salvo dove è scritto il contrario/, locale);
    assert.match(audiences.capabilities.thumbnail.description, /very few credits|pochissimi crediti/, locale);
  }
});

test('no blog post says the thumbnail recipe costs no credits', () => {
  // Same truth as the audience pages: the cover picture is metered as processing time, so the posts
  // say "very few credits". A competitor's own "costs no credits" claim is not about this recipe.
  const dir = new URL('../content/blog/', import.meta.url);
  for (const name of readdirSync(dir).filter((n) => n.endsWith('.tsx'))) {
    const text = readFileSync(new URL(name, dir), 'utf8').replace(/\s+/g, ' ');
    assert.doesNotMatch(text, /cover picture[^.]{0,60}(no credit|costs no credits)|This recipe uses no credits|It uses no credits\. The picture/i, name);
  }
});

test('on a phone the audience hero shows its call to action before the intro paragraph', () => {
  // The consent bar is fixed to the bottom of a phone screen and the sticky CTA stays hidden until
  // the visitor answers it, so the hero CTA must come first below `md` (measured: with the
  // paragraph first it sat under the bar at 390x844 on all twelve pages). Same flip as the home hero.
  const page = source('components/pages/AudiencePage.tsx');
  const hero = page.slice(page.indexOf('<h1'), page.indexOf('<div className="lg:col-span-5">'));
  assert.match(hero, /<p className="order-2 [^"]*md:order-1[^"]*">\{t\(`\$\{item\}\.hero\.intro`\)\}<\/p>/);
  assert.match(hero, /<div className="order-1 [^"]*md:order-2[^"]*">\s*<PrimaryCta location=\{audience\.cta\.hero\}/);
  assert.match(page, /<div className="flex flex-col lg:col-span-7">/, '`order` needs a flex column');
  const homeHero = source('components/home/Hero.tsx');
  assert.match(homeHero, /order-2 [^"]*md:order-1/);
  assert.match(homeHero, /order-1 md:order-2/);
});
