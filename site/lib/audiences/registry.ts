// The audience landing pages, `/for/<slug>`, in one typed registry: slug, persona token and the
// message keys each page reads. The closed set is the one in docs/design/new-user-funnel.md
// section 2; the slugs and tokens themselves live in `lib/attribution-persona` (the tracking side
// reads the same table), so this file adds only what the pages need on top of it.
//
// Pure on purpose (no React, no `@/` imports): `node --test`, `lib/llms`, `app/sitemap` and
// `i18n/config` all read it.
//
// What an audience page may claim is fixed here, not in the catalogue. Each page lists capability
// ids from a closed set; the words for an id are written once
// (`messages/<locale>/audiences.json`, `capabilities.<id>`), so two pages can
// never describe the same capability differently, and a capability that needs a paid plan carries
// its gate wherever it is shown.
import {
  LANDING_PERSONAS,
  PERSONA_LANDING_SLUGS,
  personaLandingPath,
  type LandingPersona,
} from '../attribution-persona.ts';

/**
 * How a capability is available today.
 * - `trial`: works on the free trial and uses credits. Every free-trial capability does: there is
 *   no "no credits" gate, because nothing a page lists is free of charge. The thumbnail recipe
 *   comes closest (no AI model runs), but its video analysis and frame grab are metered as
 *   processing time, so its card says it uses very few credits, not none.
 * - anything else: needs a paid plan. The plan names are read from the catalog by `lib/audiences/plan-gates`.
 */
export type CapabilityGate = 'trial' | 'videoGen' | 'audioGen' | 'ownProvider' | 'api' | 'noWatermark';

export const PAID_GATES = ['videoGen', 'audioGen', 'ownProvider', 'api', 'noWatermark'] as const satisfies readonly CapabilityGate[];
export type PaidGate = (typeof PAID_GATES)[number];

export function isPaidGate(gate: CapabilityGate): gate is PaidGate {
  return (PAID_GATES as readonly string[]).includes(gate);
}

/**
 * Everything an audience page may list under "What you can make today". Each entry is a capability
 * that ships today; the gate says who can use it. Adding one means adding its words to
 * `audiences.capabilities.<id>` in every language (the registry test checks both directions).
 */
export const CAPABILITIES = {
  highlightEdit: { gate: 'trial' },
  titledEdit: { gate: 'trial' },
  musicEdit: { gate: 'trial' },
  narratedStory: { gate: 'trial' },
  subtitles: { gate: 'trial' },
  translatedSubtitles: { gate: 'trial' },
  dub: { gate: 'trial' },
  testimonialReel: { gate: 'trial' },
  thumbnail: { gate: 'trial' },
  postText: { gate: 'trial' },
  codePromo: { gate: 'trial' },
  shapes: { gate: 'trial' },
  editor: { gate: 'trial' },
  brandKit: { gate: 'trial' },
  githubSync: { gate: 'trial' },
  claudeCode: { gate: 'trial' },
  reviewLinks: { gate: 'trial' },
  handoff: { gate: 'trial' },
  generatedClips: { gate: 'videoGen' },
  generatedAudio: { gate: 'audioGen' },
  ownProvider: { gate: 'ownProvider' },
  api: { gate: 'api' },
  fullResolution: { gate: 'noWatermark' },
} as const satisfies Record<string, { gate: CapabilityGate }>;

export type CapabilityId = keyof typeof CAPABILITIES;
export const CAPABILITY_IDS = Object.keys(CAPABILITIES) as CapabilityId[];

export interface Audience {
  /** The persona token shared with the app, the API and BI. */
  persona: LandingPersona;
  /** The page's slug: `/for/<slug>`. */
  slug: string;
  /** `/for/<slug>`, without a language prefix. */
  path: string;
  /** The key under `audiences.items` in `messages/<locale>/audiences.json`. Equal to the persona token. */
  messageKey: LandingPersona;
  /** The `location` tokens of the page's two primary calls to action (`site.cta_click` detail). */
  cta: { hero: string; final: string };
  /** "What you can make today", in display order. Free-trial entries first, paid ones last. */
  capabilities: readonly CapabilityId[];
  /** How many lines the catalogue holds under `items.<key>.before.points` and `.after.points`. */
  beforePoints: number;
  afterPoints: number;
  /** How many of the audience's own questions the catalogue holds under `items.<key>.faq`. */
  faqs: number;
}

type AudienceContent = Pick<Audience, 'capabilities' | 'beforePoints' | 'afterPoints' | 'faqs'>;

const CONTENT: Record<LandingPersona, AudienceContent> = {
  creator: {
    capabilities: [
      'highlightEdit',
      'musicEdit',
      'subtitles',
      'translatedSubtitles',
      'thumbnail',
      'postText',
      'shapes',
      'editor',
      'generatedClips',
      'generatedAudio',
      'fullResolution',
    ],
    beforePoints: 3,
    afterPoints: 4,
    faqs: 3,
  },
  business: {
    capabilities: [
      'titledEdit',
      'narratedStory',
      'testimonialReel',
      'brandKit',
      'subtitles',
      'thumbnail',
      'postText',
      'shapes',
      'generatedClips',
      'fullResolution',
    ],
    beforePoints: 3,
    afterPoints: 4,
    faqs: 3,
  },
  educator: {
    capabilities: [
      'highlightEdit',
      'subtitles',
      'translatedSubtitles',
      'dub',
      'narratedStory',
      'thumbnail',
      'shapes',
      'editor',
      'fullResolution',
    ],
    beforePoints: 3,
    afterPoints: 3,
    faqs: 3,
  },
  nonprofit: {
    capabilities: [
      'narratedStory',
      'testimonialReel',
      'highlightEdit',
      'subtitles',
      'translatedSubtitles',
      'brandKit',
      'thumbnail',
      'postText',
      'shapes',
      'fullResolution',
    ],
    beforePoints: 3,
    afterPoints: 3,
    faqs: 3,
  },
  software: {
    capabilities: [
      'codePromo',
      'highlightEdit',
      'githubSync',
      'claudeCode',
      'brandKit',
      'subtitles',
      'shapes',
      'editor',
      'ownProvider',
      'generatedClips',
      'api',
      'fullResolution',
    ],
    beforePoints: 3,
    afterPoints: 3,
    faqs: 3,
  },
  agency: {
    capabilities: [
      'highlightEdit',
      'titledEdit',
      'testimonialReel',
      'brandKit',
      'reviewLinks',
      'handoff',
      'subtitles',
      'shapes',
      'generatedClips',
      'api',
      'fullResolution',
    ],
    beforePoints: 3,
    afterPoints: 4,
    faqs: 3,
  },
};

/** The six audience pages, in the order the home page and the footer list them. */
export const AUDIENCES: readonly Audience[] = LANDING_PERSONAS.map((persona) => {
  const slug = PERSONA_LANDING_SLUGS[persona];
  return {
    persona,
    slug,
    path: personaLandingPath(persona),
    messageKey: persona,
    cta: { hero: `for-${slug}-hero`, final: `for-${slug}-final` },
    ...CONTENT[persona],
  };
});

export const AUDIENCE_SLUGS: readonly string[] = AUDIENCES.map((audience) => audience.slug);
export const AUDIENCE_PATHS: readonly string[] = AUDIENCES.map((audience) => audience.path);

/** The audience behind a `/for/<slug>` route segment, or undefined for anything else. */
export function audienceForSlug(slug: string | null | undefined): Audience | undefined {
  return slug ? AUDIENCES.find((audience) => audience.slug === slug) : undefined;
}

/** True for exactly the six audience paths (no language prefix; a trailing slash is tolerated). */
export function isAudiencePath(path: string): boolean {
  const clean = path.length > 1 ? path.replace(/\/+$/, '') : path;
  return AUDIENCE_PATHS.includes(clean);
}

/** The step keys of the three-step "how it works", shared by the home page and every audience page. */
export const HOW_STEPS = ['drop', 'say', 'review'] as const;
export type HowStep = (typeof HOW_STEPS)[number];
