/** The one path self-serve signup lives at — a route in `/web`, reached through nginx as `/app/signup`. */
export const SIGNUP_PATH = '/app/signup';
export const LOGIN_PATH = '/app/login';

/**
 * The product version this build ships, in one place. Every surface that shows a version — the
 * alpha notice, the footer — reads it from here rather than typing the string again, so the site
 * cannot drift from the repo-root `VERSION` file that `infra/scripts/check-version.sh` asserts
 * against.
 */
export const productVersion = '0.4.0-alpha';

/**
 * The alpha notice, verbatim as the launch plan pins it, built from `productVersion` so the
 * sentence and the version cannot drift apart. `AlphaBanner.tsx` renders this constant rather than
 * repeating the text, which is what lets `site-config.test.ts` guard the notice: the component's
 * own `.tsx` test could not run under the site's `node --test` glob, which only reaches `lib`.
 */
export const ALPHA_NOTICE = `ReelBolt ${productVersion} — public alpha. Expect rough edges; things may change.`;

/** A call to action: the words, where they go, and the GA event a click fires. */
export interface Cta {
  label: string;
  /** Key of the translated label in `messages/<locale>/layout.json` (`cta.<msgKey>`); `label` is the English text. */
  msgKey: 'startFree' | 'talkToUs';
  href: string;
  /**
   * GA4 event name, distinct per CTA so the pre-launch and post-launch states stay directly
   * comparable in reports rather than being merged into one series.
   */
  event: string;
}

export interface SiteCtas {
  /** Whether this build sends visitors into the self-serve signup flow at all. */
  signupEnabled: boolean;
  /** The main action on a page. */
  primary: Cta;
  /** The human path, always available. */
  sales: Cta;
}

const SALES_CTA: Cta = { label: 'Talk to us', msgKey: 'talkToUs', href: '/contact', event: 'cta_talk_to_us' };

/**
 * Which CTAs this build shows.
 *
 * Decision D25: "Start free" is the primary CTA **only once self-serve signup is deployed**. Until
 * then the coming-soon state stays — the primary action is the contact path, exactly as it was
 * before this config existed. `NEXT_PUBLIC_SIGNUP_ENABLED=true` is the switch that flips it, so
 * one source tree ships both the self-host build (signup off) and the cloud
 * build (signup on). It mirrors `SIGNUP_ENABLED` on the Go API and `web/lib/auth/config.ts`,
 * which accepts either spelling; here the `NEXT_PUBLIC_` form is the only one that can reach a
 * statically exported page.
 *
 * This is a *display* switch, never an authorization decision: the Go handler answers 404 to
 * `POST /api/v1/auth/signup` unless the API itself has `SIGNUP_ENABLED`, whatever this returns.
 *
 * Build-time, not runtime: `NEXT_PUBLIC_*` values are inlined into the bundle at image build
 * time, so changing it needs a rebuild of the `site` image.
 *
 * The env value is read HERE, through the literal `process.env.NEXT_PUBLIC_SIGNUP_ENABLED`
 * expression, and handed to the function as its default. That is not a style choice — it is what
 * makes the switch work in the browser at all. Next inlines `NEXT_PUBLIC_*` by matching that
 * exact member expression; reading it as a property of a passed-in object compiles, renders
 * correctly on the server, and then silently evaluates to `undefined` in the client bundle, where
 * `process.env` is the browser shim. Before this indirection was removed, a
 * `NEXT_PUBLIC_SIGNUP_ENABLED=true` build shipped "Sign in"/"Start free" in the HTML and then
 * re-rendered every client component (the header, the sticky mobile CTA, every `PrimaryCta`)
 * back to "Talk to us" on hydration — measured in headless Chrome, not inferred. The parameter
 * stays for the tests in `site-config.test.ts`, which pass their own env.
 */
const SIGNUP_ENABLED_FROM_ENV = process.env.NEXT_PUBLIC_SIGNUP_ENABLED;

export function siteCtas(
  env: Record<string, string | undefined> = { NEXT_PUBLIC_SIGNUP_ENABLED: SIGNUP_ENABLED_FROM_ENV },
): SiteCtas {
  const signupEnabled = env.NEXT_PUBLIC_SIGNUP_ENABLED === 'true';
  return {
    signupEnabled,
    primary: signupEnabled
      ? { label: 'Start free', msgKey: 'startFree', href: SIGNUP_PATH, event: 'cta_start_free' }
      : SALES_CTA,
    sales: SALES_CTA,
  };
}

const ctas = siteCtas();

/** The public source repository. A plain static link: it is live from the first push. */
export const REPO_URL = 'https://github.com/ReelBolt/reelbolt';
/** The GitHub release for the version this build ships. */
export const RELEASE_URL = `${REPO_URL}/releases/tag/v${productVersion}`;

export const siteConfig = {
  repoUrl: REPO_URL,
  name: 'ReelBolt',
  // The short tagline of the positioning (docs/marketing/copy-deck.md section 1). The lockup under
  // the logo keeps the two-word `layout.brand.tagline` ("AI video team"): the header's width is
  // measured per language by e2e/tests/site/header-spacing.spec.ts.
  tagline: 'Your story, made into a video.',
  // Mirrored word for word by `seo.description` in messages/en/seo.json (the JSON-LD description);
  // `lib/site-copy.test.ts` holds the two together.
  shortDescription:
    'ReelBolt is an AI video team for anyone with a story to tell and no time to tell it. Drop in the clips you already shot, say what the video is for, and get a reviewed cut you can change.',
  // `||`, not `??`: the Docker build declares `ARG NEXT_PUBLIC_SITE_URL` with no default and then
  // `ENV NEXT_PUBLIC_SITE_URL=$NEXT_PUBLIC_SITE_URL`, so a build that passes no build-arg sets this
  // to the EMPTY STRING rather than leaving it unset. `??` only falls back on null/undefined, so an
  // empty value flowed straight through to `new URL(siteConfig.url)` in app/layout.tsx's
  // metadataBase and failed the production build with ERR_INVALID_URL. Every other consumer of this
  // variable already treats empty as absent — .env.example documents leaving it blank.
  url: process.env.NEXT_PUBLIC_SITE_URL || 'https://reelbolt.ai',
  dashboardPath: '/app',
  loginPath: LOGIN_PATH,
  signupPath: SIGNUP_PATH,
  /** The desktop runner's page. Whether it is *live* is `lib/runner-downloads`'s call, not this one. */
  runnerPath: '/runner',
  /** See `siteCtas` — the D25 launch switch, read once at build time. */
  signupEnabled: ctas.signupEnabled,
  primaryCta: ctas.primary,
  salesCta: ctas.sales,
  // `desktop: false` keeps an item in the mobile menu and the footer but out of the desktop bar:
  // the labels, the GitHub button and the sign-in cluster do not share one line at 1280px in
  // Italian. Contact is also the header's sales CTA; Teams is a section of Features; About sits in the footer.
  // One array feeds the desktop nav and the mobile menu, so `/pricing` is listed once and
  // appears in both.
  nav: [
    { key: 'howItWorks', label: 'How it works', href: '/#how-it-works' },
    { key: 'features', label: 'Features', href: '/features' },
    { key: 'openSource', label: 'Open source', href: '/open-source' },
    { key: 'teams', label: 'Teams', href: '/features#teams', desktop: false },
    { key: 'pricing', label: 'Pricing', href: '/pricing' },
    { key: 'recipes', label: 'Recipes', href: '/recipes' },
    { key: 'blog', label: 'Blog', href: '/blog' },
    { key: 'docs', label: 'Docs', href: '/docs/' },
    { key: 'about', label: 'About', href: '/about', desktop: false },
    { key: 'contact', label: 'Contact', href: '/contact', desktop: false },
  ],
} as const;
