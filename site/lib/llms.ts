// Plain-text builders for /llms.txt and /llms-full.txt. Pure on purpose: the routes gather the
// data (site config, the plan catalog, blog metas, FAQ arrays) and pass it in, so `node --test` can
// exercise the text without importing the TSX post modules or Next. Every number comes from the
// catalog through `formatMoney` / `creditsLabel`; nothing here is a price literal.
import type { Plan } from './pricing/index.ts';
import { creditsLabel, formatMoney } from './pricing/offers.ts';
import { AUDIENCES } from './audiences/registry.ts';
import type { LandingPersona } from './attribution-persona.ts';
import { productVersion, RELEASE_URL } from './site-config.ts';

export interface LlmsCta {
  label: string;
  /** A site path such as `/app/signup` or `/contact`; made absolute here. */
  href: string;
}

export interface LlmsPost {
  slug: string;
  title: string;
  description: string;
  tags: readonly string[];
}

export interface LlmsFaq {
  question: string;
  answer: string;
}

export interface LlmsInput {
  /** The absolute site origin, from `siteConfig.url`. */
  siteUrl: string;
  name: string;
  shortDescription: string;
  /** The primary CTA, from `siteConfig.primaryCta`. */
  cta: LlmsCta;
  plans: readonly Plan[];
  currency: string;
  /** The catalog's tax note, printed verbatim under the plan list. */
  taxNote: string;
  /** Credit pack expiry from the catalog. The pack sentence is left out when this is absent. */
  topUpExpiresAfterMonths?: number;
  /** Newest first, as `allPosts()` returns them. */
  posts: readonly LlmsPost[];
  faqs: { home: readonly LlmsFaq[]; pricing: readonly LlmsFaq[] };
  /** Whether `/runner` has a download. The same predicate the page and the sitemap use. */
  runnerAvailable: boolean;
  runnerPath: string;
  /** Whether the recipe gallery had recipes when the site was built. Left out of the page list otherwise. */
  recipesAvailable?: boolean;
}

function absolute(origin: string, path: string): string {
  return `${origin}${path}`;
}

/** `A, B and C`. */
function joinNames(names: readonly string[]): string {
  if (names.length <= 1) return names.join('');
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

/** One plan's line: price, credits and export ceiling, all from the catalog entry. */
function planLine(plan: Plan, currency: string): string {
  const exports = `Exports up to ${plan.limits.maxResolution}`;
  if (plan.kind === 'contact') return `- ${plan.name}: priced on request. Contact us. ${exports}.`;
  const price =
    plan.priceMonthly === undefined
      ? undefined
      : `${formatMoney(currency, plan.priceMonthly)}${plan.kind === 'perSeat' ? ' per seat' : ''} a month`;
  // `creditsLabel` already says "one time" for Trial, so Trial never reads as a monthly grant.
  const yearly =
    plan.priceAnnual === undefined
      ? undefined
      : `${formatMoney(currency, plan.priceAnnual)}${plan.kind === 'perSeat' ? ' per seat' : ''} billed yearly, credits still arrive every month`;
  const parts = [price, yearly, creditsLabel(plan), exports].filter((part): part is string => part !== undefined);
  return `- ${plan.name}: ${parts.join('. ')}.`;
}

function faqBlock(faq: LlmsFaq): string {
  return `### ${faq.question}\n\n${faq.answer}`;
}

function section(title: string, lines: readonly string[]): string {
  return `## ${title}\n\n${lines.join('\n')}`;
}

function summaryParagraphs(name: string): string[] {
  return [
    `${name} is an AI video team for anyone who has a story to tell and no time to tell it. You drop in the clips you already shot and say what the video is for, and a group of specialized AI agents find the story and cut the footage; captions, titles, narration and sound are added when the recipe or the person asks for them. A reviewer agent scores every edited cut and sends weak ones back, so what reaches you is a reviewed cut you can still change. Subtitles, thumbnails and post text are made in a single pass. Software teams can also start from their product's source code instead of footage.`,
    `Every finished video also opens as an editable timeline, so a person can change anything the agents did. ${name} is in public alpha. It runs as a hosted service with a free trial and monthly plans, and the code is open source under AGPL-3.0. Self-hosting is free and the hosted service is the easy path; a commercial licence is available on request.`,
  ];
}

// One line per audience page, in English like the rest of this file. The record is keyed by the
// persona token, so a seventh audience in the registry fails the type check until it has a line.
const AUDIENCE_LINES: Record<LandingPersona, { name: string; who: string; gets: string }> = {
  creator: {
    name: 'Creators',
    who: 'Vloggers, streamers and podcasters who film and post.',
    gets: 'a first cut with the dead air gone, subtitles, a thumbnail and post text.',
  },
  business: {
    name: 'Small businesses',
    who: 'Shops, studios, restaurants, local services and solo marketers.',
    gets: 'a short video for the shop, the launch or the offer from clips filmed on a phone.',
  },
  educator: {
    name: 'Educators',
    who: 'Coaches, teachers and course makers.',
    gets: 'short, captioned lesson clips, with translated subtitles or a dub in another language.',
  },
  nonprofit: {
    name: 'Nonprofits',
    who: 'Charities, community groups and campaigns.',
    gets: 'a narrated story with captions or a testimonial reel from the clips they already have.',
  },
  software: {
    name: 'Software teams',
    who: 'Founders and product teams.',
    gets: "a promo video built from the app's source code and real screens, from GitHub or Claude Code.",
  },
  agency: {
    name: 'Agencies',
    who: 'Studios and freelancers who edit for clients.',
    gets: 'a reviewed first cut, guest review links and a hand-off package for their own editor.',
  },
};

function whoItIsFor(abs: (path: string) => string): string[] {
  return AUDIENCES.map((audience) => {
    const line = AUDIENCE_LINES[audience.persona];
    return `- [${line.name}](${abs(audience.path)}): ${line.who} They get ${line.gets}`;
  });
}

// What a person can make today. Every line is a recipe that ships; a line that needs a paid plan
// says so, and no line promises a video from a text prompt alone.
const USED_FOR = [
  '- Highlight edits from raw footage: dead air, false starts and retakes removed, cuts on full sentences.',
  '- Footage edits with titles and callouts, or with music and sound effects from files the customer uploads.',
  '- Narrated stories from clips and a short brief, with captions.',
  '- Subtitles burned in, with SRT and VTT files, and subtitles translated into another language.',
  '- Dubs in another language. Mouth movement on camera is not changed.',
  '- Testimonial reels from a talking-head clip and a screenshot.',
  '- Thumbnails with cover text, and post text (a caption and hashtags) for a video.',
  "- Promo videos built from an app's source code, for software teams.",
  '- AI-generated clips and b-roll, on the paid plans whose catalog entry allows video generation.',
];

const KEY_FEATURES = [
  '- Automatic editing of raw recordings: pauses, stumbles and weaker takes removed, cuts on full sentences, best take kept.',
  '- A timeline editor (early preview) for every finished video: trim, re-cut from the transcript, grade, mix, subtitles, version history, review comments, and export to other editors (OTIO, EDL, Final Cut XML, SRT, VTT).',
  '- A layered picture editor for thumbnails and stills: background removal and replacement, erase and fill, edit with words, expand, upscale, relight.',
  '- Designed graphics: describe a title, badge, price tag or arrow and an AI designer draws it with exact text in brand colors and fonts.',
  '- A brand kit per project and a shared brand library, imported from design tokens, CSS, Tailwind, DESIGN.md, design-tool exports or a brand PDF, with drift checks you approve.',
  '- AI narration written to fit the cut, stock or consented cloned voices, captions, dubs that translate the narration, and pickups that fix a flubbed line. Mouth movement on camera is not changed.',
  '- Generated b-roll (cutaways, cold opens, end cards) planned from the edit, under a spending cap, with several takes per shot. Included on the plans whose catalog entry allows video generation.',
  '- Editing rooms where several AI editors and a director decide the cut, the graphics or the color look. Included on the plans whose catalog entry allows rooms.',
  '- Tracked screen inserts on green, blue or magenta screens, and object tracking with blur, pixelate or fill for private details.',
  '- A chat assistant across the dashboard with approval cards that show each change and its cost, also available in Claude Code and other MCP clients.',
  '- Sync from GitHub, a folder on a paired computer (the desktop app download has not been released yet), Google Drive, OneDrive, SharePoint and Dropbox, read-only.',
  '- Bring your own AI provider and choose which model each agent uses.',
  '- Rendering on a Windows or Linux machine the customer pairs with the workspace, at no credit cost. The self-serve download has not been released yet.',
  "- Output resolution chosen per run, up to the plan's limit, in landscape, vertical, square, portrait or audio-only.",
  '- Team workspaces with roles, invitations and a shared credit pool.',
];

function pricingLines(input: LlmsInput): string[] {
  const lines = [
    `- Plans: ${joinNames(input.plans.map((plan) => plan.name))}. Prices are monthly or yearly, in ${input.currency} on the pricing page. ${input.taxNote}`,
    ...input.plans.map((plan) => planLine(plan, input.currency)),
    '- Each paid plan includes monthly credits. Credits pay for the AI and rendering ReelBolt supplies: workflow steps, picture AI tools, timeline suggestions, designed graphics, generated video, and assistant chat beyond the monthly allowance.',
    "- Free: file summaries, indexing and search, storage within the plan, brand imports and exports, work done with the customer's own AI provider, and work done on the customer's own paired machine.",
    '- Before each run, an estimate shows about how many credits it will use and usually a range. Generated video is shown as an "up to" amount.',
    "- If a run fails because of a problem on ReelBolt's side, the credits for the failed step are refunded automatically.",
    "- Higher output resolution uses more render credits. Each plan's maximum resolution is listed on the pricing page.",
  ];
  if (input.topUpExpiresAfterMonths !== undefined) {
    lines.push(
      `- Paid plans can buy credit packs; pack credits expire after ${input.topUpExpiresAfterMonths} months.`,
    );
  }
  lines.push(
    '- Plans are managed in the workspace: cancel at the end of the period, upgrades take effect after payment, downgrades at the next period. Promo codes are entered on the Plan and billing page.',
  );
  return lines;
}

function nextLines(input: LlmsInput, abs: (path: string) => string): string[] {
  const lines = [`- [${input.cta.label}](${abs(input.cta.href)}): try ReelBolt on your own footage.`];
  if (input.cta.href !== '/contact') {
    lines.push(`- [Talk to the team](${abs('/contact')}): questions about plans, what you want to make or your team.`);
  }
  lines.push(
    `- [Comparisons with other video tools](${abs('/blog/tag/comparisons')}): how ReelBolt compares with the other tools people use.`,
  );
  return lines;
}

function pageLines(input: LlmsInput, abs: (path: string) => string): string[] {
  const { name } = input;
  const lines = [
    `- [Home](${abs('/')}): what ${name} does, who it is for, how it works, and answers to common questions.`,
        `- [Example](${abs('/examples')}): the ReelBolt launch film, showing the product at work, to watch before you sign up.`,
    ...AUDIENCES.map(
      (audience) =>
        `- [${name} for ${AUDIENCE_LINES[audience.persona].name.toLowerCase()}](${abs(audience.path)}): what this audience can make today and what needs a paid plan.`,
    ),
    `- [Features](${abs('/features')}): editing, the timeline and picture editors, designed graphics, brand kit, narration, dubs, generated b-roll, rooms, screen inserts, privacy, the assistant, sync, your own machine, output and control, in detail.`,
    `- [Pricing](${abs('/pricing')}): the plans, what a credit is, and what ${name} does and does not charge for.`,
    `- [Open source](${abs('/open-source')}): the code, self-hosting versus the hosted service, the licence and how to contribute.`,
    `- [About](${abs('/about')}): why ${name} exists and the principles it is built on.`,
    `- [Contact](${abs('/contact')}): talk to us or reach the team.`,
  ];
  if (input.recipesAvailable) {
    lines.push(`- [Recipe gallery](${abs('/recipes')}): video recipes shared by the community, each with a preview, its steps and prompts, ready to copy to your workspace.`);
  }
  if (input.runnerAvailable) {
    lines.push(`- [Desktop runner](${abs(input.runnerPath)}): render on a computer you own.`);
  }
  lines.push(
    `- [Documentation](${abs('/docs/')}): the user guide, served by the platform itself.`,
    `- [Privacy policy](${abs('/legal/privacy')})`,
    `- [Terms of service](${abs('/legal/terms')})`,
    `- [Acceptable use policy](${abs('/legal/acceptable-use')})`,
    `- [Refund and cancellation policy](${abs('/legal/refunds')})`,
  );
  return lines;
}

function openSourceLines(input: LlmsInput, abs: (path: string) => string): string[] {
  const { name } = input;
  return [
    `${name} is open source under AGPL-3.0. The code is at https://github.com/ReelBolt/reelbolt, and you can read it, change it, or run it yourself for free.`,
    `- Current release: ${productVersion}, at ${RELEASE_URL}.`,
    `- Self-hosting is free; you pay for your own server and the AI services you connect. It is an early alpha, and support for self-hosted copies is community support through GitHub issues.`,
    `- The hosted service is the easy path: no setup, and support by plan. See [Pricing](${abs('/pricing')}).`,
    `- A commercial licence is available on request for companies that do not want the AGPL obligations: legal@reelbolt.ai.`,
    `- The ${name} name and logo are trademarks of the project owner and are reserved; a fork needs its own name.`,
    `- [Open source page](${abs('/open-source')}): what you can run yourself, hosted versus self-hosted, and how to start.`,
  ];
}

function blogLines(posts: readonly LlmsPost[], abs: (path: string) => string): string[] {
  const lines = [`- [Blog](${abs('/blog')}): guides, product news and comparisons with other video tools.`];
  for (const post of posts) {
    const kind = post.tags.includes('comparisons') ? 'Comparison. ' : '';
    lines.push(`- [${post.title}](${abs(`/blog/${post.slug}`)}): ${kind}${post.description}`);
  }
  return lines;
}

/** The body of `/llms.txt`. Its FAQ blocks are the ones `scripts/check-faq-schema.mjs` compares. */
export function buildLlmsTxt(input: LlmsInput): string {
  const origin = input.siteUrl.replace(/\/+$/, '');
  const abs = (path: string) => absolute(origin, path);
  const blocks = [
    `# ${input.name}`,
    `> ${input.shortDescription}`,
    summaryParagraphs(input.name).join('\n\n'),
    section('Who it is for', whoItIsFor(abs)),
    section('What it is used for', USED_FOR),
    section('Key features', KEY_FEATURES),
    section('How pricing works', pricingLines(input)),
    section('Where to go next', nextLines(input, abs)),
    section('Open source', openSourceLines(input, abs)),
    section('Pages', pageLines(input, abs)),
    section('Blog', blogLines(input.posts, abs)),
    `## Frequently asked questions\n\n${input.faqs.home.map(faqBlock).join('\n\n')}`,
    `## Pricing, credits and your data\n\n${input.faqs.pricing.map(faqBlock).join('\n\n')}`,
  ];
  return `${blocks.join('\n\n')}\n`;
}

/**
 * `/llms-full.txt`: the whole of `/llms.txt` (which already carries every home and pricing FAQ,
 * so they are not repeated), then each blog post's title, URL and description. Post headings are
 * not in the post metadata, so they are not listed.
 */
export function buildLlmsFullTxt(input: LlmsInput): string {
  const base = buildLlmsTxt(input);
  if (input.posts.length === 0) return base;
  const origin = input.siteUrl.replace(/\/+$/, '');
  const posts = input.posts.map(
    (post) => `### ${post.title}\n\nURL: ${absolute(origin, `/blog/${post.slug}`)}\n\n${post.description}`,
  );
  return `${base.trimEnd()}\n\n## Blog posts in full\n\n${posts.join('\n\n')}\n`;
}
