# Marketing Site

`/site` is a public marketing brochure — home, features, pricing, about, open source, contact, legal, the blog, and the
desktop runner's page — served at the
root domain. `/web` is the authenticated dashboard. They are two separate Next.js projects, not
one project with two route groups.

## Visual design system

`/site` went through a 5-phase visual redesign (purple/white/near-black palette, Chakra Petch +
JetBrains Mono type system, decorative hairline primitives, and a WebGL 3D hero) after this file
was first written. See [`docs/site-design-system.md`](site-design-system.md) for the palette
tokens and their measured WCAG contrast ratios, the font/decoration/3D rationale, the SSR-isolation
and reduced-motion rules for the 3D scenes, and the bundle code-split contract that keeps them off
every non-homepage route. Nothing in that redesign changed the routing split, domain contract,
consent behavior, or legal/SEO plumbing described below — those remain exactly as documented here.

## Why a separate project instead of a route group in `/web`

- **Different audience, different trust boundary.** Every page in `/site` is meant to be crawled,
  indexed, and viewed by anonymous visitors with no session. Every page in `/web` requires a
  cookie and is deliberately kept out of search engines (see `robots.ts`/`sitemap.ts` in
  `/site` — neither exists in `/web`, whose middleware guards every route instead). Folding both
  into one Next app would mean threading "is this route public" through the same middleware that
  currently does one job: enforce auth.
- **Different tech needs.** `/web` is built on Mantine v9 for the component-heavy authenticated
  UI (tables, forms, drag-and-drop workflow builder). A marketing site doesn't need a component
  library at that scale — it needs fast static pages, so `/site` uses Tailwind CSS v4 directly and
  ships no Mantine at all. Pulling Mantine into pages that exist purely to be crawled and to load
  fast would add bundle weight, and pulling marketing-site concerns (JSON-LD, OG image generation,
  cookie consent) into `/web` would add weight in the other direction.
- **Independent deploy/build lifecycle.** `/site` has no backend dependency (no `depends_on` in
  `docker-compose.yml`) and must come up even if the entire backend stack is down — a marketing
  page returning an error because Postgres is unreachable is a worse failure mode than the whole
  platform being down. `/web` depends on `inference` and `go-api`. Two Next projects means each
  one's healthcheck, `depends_on`, and rebuild only touches what it actually needs.

## The routing split

Nginx is still the single entry point. The split happens in `nginx/locations.conf`, at the very
end of the file, using the two upstream variables declared per-scheme just above the routing table
(`nginx/http-server-dev.conf` for `:80`, `nginx/https-server.conf.template` for `:443`):

```nginx
set $web_app  http://web:3000;
set $site_app http://site:3000;
```

and the two trailing location blocks:

```nginx
# Dashboard (Next.js `web`, basePath '/app').
location ^~ /app {
    proxy_pass $web_app;
    ...
}

# Public marketing site.
location / {
    proxy_pass $site_app;
    ...
}
```

`location ^~ /app` wins over every regex `location` above it (SSE, `/stop`, etc.) without further
regex evaluation, and `web` itself runs with Next's `basePath: '/app'`, so `proxy_pass $web_app`
(a bare variable, no URI segment) forwards `/app/...` through unchanged — exactly what a
basePath'd app expects to receive. Everything that isn't `/api/v1/*`, `/health`, or `/app/*` falls
through to the trailing `location /`, which proxies to `site`. Every location block *before* these
two (auth, admin, workflows, the SSE regex, the `/stop` regex, health, workflow-engine, the
sandbox-executor's deliberate non-block, the generic `/api/v1/` catch-all) is unrelated to this
split and untouched by it.

## Languages

English is unprefixed (`/pricing`), every other registered language lives under its code (`/it/pricing`). The registry is `i18n/locales.json` (vendored into `site/i18n/`); the contract and the "add a language" steps are in [`i18n.md`](i18n.md). A language is a catalogue directory (`site/messages/<code>/<namespace>.json`) plus one registry line; no route file changes.

- **Routes.** No Next `middleware`; the one request-time redirect (`GET /`) is nginx njs. There is no top-level `app/layout.tsx`: `app/(en)/` and `app/[locale]/` are two root layouts that render the same `components/layout/RootShell.tsx`, and every page body is a locale-taking component in `components/pages/*Page.tsx` (thin route files in both trees; `[locale]` uses `generateStaticParams` over the non-default locales and `dynamicParams = false`). The locale always comes from the route (`setRequestLocale`), never from a header. An unmatched URL renders `app/global-not-found.tsx` (English).
- **Pages.** Home, the six audience pages (`/for/<slug>`, see "Audience pages"), features, example, pricing, about, open source, contact, thank-you, runner and the recipe gallery (`/recipes`, `/recipes/<id>`) exist in every language. Legal (Terms, Privacy, Refunds, Acceptable use, Runner EULA) and the blog exist only at their English URLs: in Italian chrome their link labels are translated and point to the English page (the blog link says "in inglese"). Buyer-copy rules apply to every language (`lib/buyer-copy.test.ts` scans all catalogues).
- **Code.** `useT('layout.footer')` / `await getT(...)`, `useFormat()`, `msg()` from `@/i18n` and `@/i18n/server`; `Link` from `@/i18n/link` keeps a visitor in their language (links to untranslated pages pass through). Prices are never typed: Offers still derive from `plans.catalog.json`, in every language.
- **Choosing a language.** Only `GET /` redirects: the `rb_locale` cookie (set by the footer switcher, one year, `Path=/`, `SameSite=Lax`, functional) wins, else `Accept-Language`, else English; 302, `Cache-Control: private, no-store`, `Vary: Accept-Language, Cookie`, query string kept. `GET /?for=<persona>` (trimmed, lowercased; one of the six, `other` and unknown values ignored) also 302s, with the same headers, to `/for/<slug>` (`/<code>/for/<slug>` for a non-default pick) keeping the whole query (`for` and `utm_*`), so links shared as `/?for=creator&utm_…` reach the audience page; the njs copy of the slug map is in `nginx/i18n.js`, the source is `PERSONA_LANDING_SLUGS`. Deep links are never redirected, so a loop is impossible. One ES5 picker (`i18n/pick-locale.js`, vendored by `node i18n/sync.mjs` into `site/functions/_pick-locale.js` and `nginx/pick-locale.js`) serves both runtimes: the Pages Function `site/functions/_middleware.ts` (`public/_routes.json` keeps it off static assets) and nginx njs (`nginx/i18n.js`, `location = /`). Tests: `i18n/pick-locale.test.mjs` (shared vectors), `site/lib/home-redirect.test.ts`, `nginx/tests/i18n-home.sh` (live, throwaway containers).
- **SEO.** `<html lang>`, a canonical that is the page's own URL, `alternates.languages` (every language plus `x-default` = English) on each translated page, `og:locale` plus alternates, a localized Open Graph card (`app/[locale]/opengraph-image.tsx`), localized JSON-LD (`inLanguage`, strings from `messages/<locale>/seo.json`) and a sitemap listing each language with alternates. `npm run test:build` checks the built output.
- **Footer newsletter.** The visitor's language travels as `locale` (allowlisted) to the API so the confirmation mail can arrive in it. The consent sentence itself is the registered wording and stays English until a translated wording version is registered.
- **Gates.** `i18n/catalog.test.ts` (same files, keys and ICU shape in every language, plural `other`, no empty value, no markup or typed price), `i18n/unused-keys.test.ts`, `lib/buyer-copy.test.ts`, `i18n/config.test.ts`, and an ESLint `i18next/no-literal-string` ratchet over the files listed in `site/i18n/migrated.json` (the list only grows).

## Why `web`'s healthcheck probes `/app/login`, not `/`

`web` runs with `basePath: '/app'`, so a request for `/` inside that container 404s — Next simply
doesn't have a route there. `docker-compose.yml`'s original healthcheck (`wget .../` ) predates the
basePath change and would now report `web` unhealthy forever. That matters beyond a red status
dot: `nginx`'s `depends_on` on `web` uses `condition: service_healthy`, so an unhealthy `web`
would mean nginx itself never starts, taking the entire stack down over a healthcheck that was
probing a route that doesn't exist. The healthcheck was moved to a real, unauthenticated route
under the basePath — `/app/login` — which 200s regardless of auth state.

## The single-swap domain contract

Every place in `/site` that needs the real, absolute domain — canonical URLs, `sitemap.xml`,
`robots.txt`'s `sitemap`/`host` fields, Open Graph and Twitter card URLs, JSON-LD `url` fields —
reads it from exactly one place: `siteConfig.url` in `site/lib/site-config.ts`, which in turn reads
`process.env.NEXT_PUBLIC_SITE_URL` (falling back to `https://reelbolt.ai` if unset). `buildMetadata()`
in `site/lib/seo.ts` is the only place page-level metadata is constructed, and it always resolves
URLs through `siteConfig.url`. There is deliberately no second place that hardcodes a domain — swap
`NEXT_PUBLIC_SITE_URL` once and every page, feed, and structured-data block follows.

## Same-origin routes: promotions and the footer newsletter

The browser calls three routes on the marketing site's own origin, so it never calls the API domain
directly. These paths sit outside nginx's `/api/v1/*` rules, so they fall through to the site.

- **`GET /api/promotions`** returns the anonymous promotions list from the billing API
  (`GET /api/v1/billing/promotions/active`, read from `REELBOLT_API_URL`). The answer carries
  `Cache-Control: public, max-age=60`, so a cache keeps it for a minute. With `REELBOLT_API_URL`
  unset it returns an empty list. The route is read at request time and never prerendered.
  The same body also carries `planPrices`, the Paddle-priced plan discounts from
  `GET /api/v1/billing/promotions/plan-prices`, fetched in parallel with the promotions and each side
  empty on its own failure; the legacy Pages Function (unused, see below) caches the merged body for 60 seconds with the Cache API
  (`caches.default`, keyed on the request URL), and structured data stays on catalog prices, because the
  pre-JavaScript price is the catalog price and a temporary promo price is never advertised in Offers.
- **`POST /api/subscribe`** takes the footer newsletter address and forwards it to
  `POST /api/v1/marketing/subscribe` with `source: "siteFooter"`. The API sends the confirmation
  email (double opt-in), so a valid submit answers `202 { "status": "checkEmail" }`. In the legacy Pages Function, an
  unset `REELBOLT_API_URL` accepts the opt-in without forwarding it. The footer form and the contact form have an optional first-name field, forwarded as `firstName`. The contact form shows it only while the newsletter box is ticked. The site omits it when blank or invalid (`cleanFirstName`: the same strict rule as the API, mirrored by a code port, so the two copies must be kept in step: at most 40 characters, one script, no digits or symbols; the API stays authoritative). The API stores it only when that request creates the row and keeps it provisional until the confirm link is redeemed. The consent wording and its registry are unchanged. See [Contact sync and names](marketing-consent.md#contact-sync-and-names).

Both routes exist twice: `site/app/api/promotions/route.ts` and `site/app/api/subscribe/route.ts`
for the `site` container, which is what serves production. `site/functions/api/promotions.ts` and
`site/functions/api/subscribe.ts` are a legacy, unused Cloudflare Pages path, kept in the tree pending
removal; nothing deploys them. Change the `site/app/api` routes; the Functions copies need no upkeep. Inside compose the
Node subscribe route forwards to `http://inference:8080` by default; set `REELBOLT_API_URL` to
override it.

**Promo banner kill switch.** Set `NEXT_PUBLIC_PROMO_BANNER=off` to turn the promo banner off. With
it set, the banner fetches nothing and renders nothing. The value is inlined at build time, so
change it and rebuild the site.

## Recipe gallery

`/recipes` and `/it/recipes` list the recipes the community shared (the marketplace, behind `Marketplace:Enabled`
on the API). `/recipes/<id>` and `/it/recipes/<id>` is one recipe: preview, steps, "what you will need", the
prompts as plain text, copies and rating, and a button to `/app/community/<id>` (the dashboard does the sign-in
and the copy). Contract: [`design/recipe-marketplace.md`](design/recipe-marketplace.md). Facts and limits:

- **User text is inert.** Names, descriptions, labels and prompts are never translated and never rendered as
  HTML, markdown or links: React text nodes only (`lib/recipes/types.ts` also removes control characters).
  A rating average is shown as visible text only from 5 ratings, never invented, and is never put in structured
  data (no `aggregateRating`, per the ban under Structured data); there are no prices anywhere on these pages.
- **Same-origin routes, written twice** (change one, change both). The browser calls `GET /api/recipes`
  (list and search), `/api/recipes/facets`, `/api/recipes/<id>`, `/api/recipes/<id>/preview` and
  `/api/recipes/<id>/poster`. Node route handlers under `site/app/api/recipes/**` serve the `site` container (production); the legacy, unused Pages
  Functions under `site/functions/api/recipes/**` are the twin that nothing deploys. Both call `proxyRecipes` in
  `site/lib/recipes/proxy.ts`, which forwards to `${REELBOLT_API_URL}/api/v1/public/marketplace/...`: GET and
  HEAD only (405 otherwise); only `q`, `category`, `cost`, `stepType`, `language`, `sort`, `cursor` and `limit`
  are forwarded (`q` at most 100 characters, `limit` at most 50, every value checked); no cookies and no
  `Authorization`; `Range` and `If-*` pass through for media, which is streamed (200, 206, 304; `Content-Range`,
  `Accept-Ranges`, `Content-Type`, `Cache-Control`); any upstream failure or unset `REELBOLT_API_URL` is an
  empty list (list, facets) or a bare 404, never a stack or the upstream URL. List, facets and detail answers
  are cached for 60 s (the legacy Pages twin uses the Cache API keyed on the sanitised query).
- **Build-time fetch.** When `REELBOLT_API_URL` is set **at build time**, the build reads the listed recipes
  (cap 500, 50 per page): it prerenders the first page of the gallery with real content, a static page for
  every recipe, and puts the gallery and each recipe (with its own listing date, hreflang alternates) in
  `sitemap.xml`, plus one line in `llms.txt`. With the variable unset, or an API that errors or lists nothing,
  the build still succeeds: the gallery is built empty and `noindex`, it is left out of the sitemap, and it
  fills itself from `/api/recipes` in the browser.
- **Refresh.** The static pages and the sitemap refresh on the next site deploy. A recipe listed after the
  build has no static page: in the legacy Pages bundle `functions/recipes/[id].ts` and `functions/[locale]/recipes/[id].ts`
  serve the prebuilt file when it exists, else the placeholder page `/recipes/_` (status 200, `noindex` meta and
  `X-Robots-Tag`), which reads the id from the URL and fetches `/api/recipes/<id>` in the browser. A removed or
  unknown recipe shows a friendly "no longer available" state, `noindex`, never a 5xx. The Node container
  renders unknown ids on demand (cached 60 s). `public/_routes.json` lists `/recipes/*` and `/<code>/recipes/*`
  for the shell Function: **adding a language needs its `/<code>/recipes/*` line there** (a test fails
  otherwise). The `site` container renders them on demand, so no scheduled redeploy is needed.
- **Previews** play only on hover (cards) or on pressing play (detail page): muted, never on load, and no hover
  playback under `prefers-reduced-motion`.
- **Gates.** `lib/recipes/*.test.ts` (proxy, allowlist, Range passthrough, failure behaviour, both twins, the
  shell fallback, structured data, sitemap, an XSS fixture), `i18n/catalog.test.ts` and
  `lib/buyer-copy.test.ts` on `messages/<code>/recipes.json`, and `npm run check:functions`.

## Home page and audience pages

The message is the one in [`design/new-user-funnel.md`](design/new-user-funnel.md) section 2:
ReelBolt is for anyone who has a story to tell and no time to tell it. Software teams are one
audience among six. The shipped words are in `marketing/copy-deck.md`.

**Home (`components/pages/HomePage.tsx`), in order:**

| # | Section | Anchor | Component |
|---|---|---|---|
| 1 | Hero: headline, intro, primary CTA, "No card required. Your first video is on us." (signup builds only), the launch film | - | `components/home/Hero.tsx` |
| 2 | "Make today": the kinds of video a person can make on the free trial, as plain text | - | `components/home/UseCaseStrip.tsx` |
| 3 | Audience picker "What is your story?": six cards, each a link to its audience page | `#who` | `components/audiences/AudiencePicker.tsx` |
| 4 | Before and after, in words (a real pair when the proof registry holds one) | `#before-after` | `components/proof/BeforeAfter.tsx` |
| 5 | How it works, three steps | `#how-it-works` | `components/audiences/HowItWorks.tsx` |
| 6 | Trust strip "What is true today" | `#trust` | `components/proof/TrustStrip.tsx` |
| 7 | Proof slots: sample gallery, testimonials, founder note. Each renders nothing while its registry is empty | `#made-with-reelbolt`, `#in-their-words`, `#founder-note` | `components/proof/*` |
| 8 | Feature grid | `#whats-inside` | in the page |
| 9 | FAQ (`HOME_FAQS`) | `#faq` | in the page |
| 10 | Final CTA | `#get-started` | in the page |

`lib/site-copy.test.ts` pins this order, the hero wording and the claim rules below.

**Audience pages.** `/for/creators`, `/for/small-business`, `/for/educators`, `/for/nonprofits`,
`/for/software-teams`, `/for/agencies`, each also under `/it/`. The closed set is
`lib/audiences/registry.ts` (slug, persona token, message key, CTA locations, the capability
ids the page may show); it is built on `lib/attribution-persona.ts`, the table the tracking
side reads, and `lib/audiences/registry.test.ts` holds it equal to the design's set.

- Routes: `app/(en)/for/[audience]/page.tsx` and `app/[locale]/for/[audience]/page.tsx`, both with
  `generateStaticParams` from the registry and `dynamicParams = false` (any other slug is a 404).
  Body: `components/pages/AudiencePage.tsx`. Text: `messages/<locale>/audiences.json`.
- Sections: hero in that audience's words, before/after, "What you can make today", three steps, trust strip,
  sample gallery (empty today), FAQ (three of the
  audience's own questions plus the shared cost question), the other five audiences, final CTA.
- One primary CTA at the top and one at the bottom, `PrimaryCta` with `persona=<token>` and the
  locations `for-<slug>-hero` and `for-<slug>-final`. The signup link then carries `?for=<token>`.
- **"What you can make today" is a closed list.** A page names capability ids from
  `CAPABILITIES` in the registry; the words for an id are written once
  (`audiences.capabilities.<id>`), so two pages cannot describe one capability differently. Each
  card shows its gate: "Free trial", "Free trial · no credits", or "On <plans>". The plan names
  come from the plan catalog through `lib/audiences/plan-gates.ts` (`videoGenAllowed`,
  `audioGenAllowed`, `byoAllowed`, `apiAccess`, `limits.watermark`), never typed. Free-trial
  capabilities are listed before paid ones (test).
- Every credit figure and resolution in this copy is a placeholder filled at render time from the
  catalog or `FIRST_VIDEO_GRANT_CREDITS`; `lib/site-copy.test.ts` fails on a typed one.
- They are linked from the home picker and the footer ("Made for"). **There is no header nav item
  for them**: `e2e/tests/site/header-spacing.spec.ts` measures the header's width per language.
- They are in `app/sitemap.ts` (once per language, with hreflang), in `llms.txt` ("Who it is for"
  and the page list) and in `i18n/build-output.test.ts`. `isTranslatedPath` accepts exactly the
  six paths.
- To add an audience: it must first exist in the design's persona set and in
  `lib/attribution-persona.ts` (the app, the API and BI share the tokens). Then add its content
  block to `CONTENT` in the registry, `audiences.items.<token>` in every language, a line in
  `AUDIENCE_LINES` in `lib/llms.ts`, and update the closed-set test.

## Proof registry

`site/lib/proof/registry.ts` holds the only proof assets the site may show: sample outputs "made
with ReelBolt" (`SAMPLE_OUTPUTS`), customer testimonials (`TESTIMONIALS`) and a founder note
(`FOUNDER_NOTE`). **All three ship empty**, because no real, signed-off sample, quote or note
exists today. `components/proof/SampleGallery.tsx`, `Testimonials.tsx` and `FounderNote.tsx`
render nothing while their registry is empty (no placeholder, no "coming soon"), and
`BeforeAfter.tsx` shows its words only. The trust strip is the only proof visible today.

To add an entry:

1. For an output, run `marketing/launch-2026/public-outputs-checklist.md`
   on it and fill its sign-off row. For a testimonial, get the person's written permission to
   publish their words, name and role, and file it. A founder note is written and approved by the
   founder; nobody drafts it for them.
2. Put the video and its poster on the CDN (`https://`), or a poster under `site/public`
   (rasters stay under 200 KB; video never goes in `public`).
3. Add the entry with its provenance: `signedOffBy` and `signedOffOn` (a sample), `permissionRef`
   and `date` (a testimonial; the reference says where the permission is kept and is never
   shown), `approvedBy` and `approvedOn` (the note). A sample may carry `before` (the raw footage,
   which makes the before/after sections show the real pair) and `facts` (`sourceSeconds`,
   `outputSeconds`, `cuts`: the measured numbers of that run, shown as measured).
4. Update the "ships empty" test in `lib/proof/registry.test.ts` in the same commit.

The shape of a sample, with placeholders where the real values go (do not commit an entry with
placeholder or made-up values; the registry is for real assets only):

```ts
// site/lib/proof/registry.ts
export const SAMPLE_OUTPUTS: readonly SampleOutput[] = [
  {
    id: '<kebab-case-id>',                 // unique
    title: '<what the video is>',
    recipe: 'Highlight edit from footage', // the recipe's name as the product shows it
    persona: '<creator|business|educator|nonprofit|software|agency|other>',
    videoSrc: 'https://<cdn>/<file>.mp4',
    posterSrc: 'https://<cdn>/<file>.webp',
    before: { videoSrc: 'https://<cdn>/<raw>.mp4', posterSrc: 'https://<cdn>/<raw>.webp' }, // optional
    facts: { sourceSeconds: 0, outputSeconds: 0, cuts: 0 }, // optional; the measured numbers of that run
    signedOffBy: '<name or role of the person who watched it in full>',
    signedOffOn: '<YYYY-MM-DD>',
  },
];
```

Where an entry shows: every sample in the home gallery; on an audience page, the samples of that
page's persona; a sample with `before` also fills the before/after section with the real pair
and, when it has `facts`, the numbers of that run. Those numbers are the only figures the
before/after sections may ever show.

`proofProblems` (`lib/proof/validate.ts`) runs when the registry module loads, so an entry with a
missing or future sign-off date, no signer, no permission reference, an `http://` address or a
made-up persona fails `next build` and `npm test`. Entries are data, like blog posts: they are
shown as written in every language, and a testimonial is never translated or edited. No rating or
review markup is emitted for them.

## Visitor tracking, tracked links and the signup link

What the site does so that a person who arrives through a posted link can be followed to signup.
The contract every service shares is [`design/new-user-funnel.md`](design/new-user-funnel.md); the
operator's view (how to post a link, how to turn the counting on, how to read it) is
an operator document. This section is the site's half only.

**The landing is held in memory.** `components/analytics/landing.ts` reads the address and the
referrer of the first page into `landingMemory` (`lib/attribution-landing.ts`) once per document.
Memory only: no cookie, no local or session storage. So a visitor who accepts cookies on their
third page still has the real landing recorded, as long as they moved between pages inside the
site. A full page load starts a new landing from that page's own address.

**Three first-party events** go to `POST /api/v1/telemetry/visit` on the site's own origin
(`lib/site-events.ts`, mounted through `components/analytics/AttributionTracker.tsx`). They are
not Google Analytics events and touch nothing in `lib/gtag.ts`.

| Event | When | `detail` |
|---|---|---|
| `site.visit` | Once per visitor per tab session | not sent (the API works out the source label) |
| `site.page_view` | Once per page key per page life, on client-side navigation too. The page the document opened on gets one as well, even when Accept came pages later | the page key |
| `site.cta_click` | A click on a primary call to action | where the button sits |

- **Consent gate.** No cookie or stored value is read or written, and nothing is sent, until the
  stored cookie choice is `granted` (the choice itself is the one thing read). The sender re-reads
  the choice at the moment of each call and needs the visitor id that only exists after Accept;
  it never creates one. Reject or withdraw removes `rb_ft`, `rf-visitor-id` and `rf-visit-sent`.
  While the visitor is undecided, nothing is stored, sent or removed.
- **Which visitor.** The first-touch cookie and the visitor id are one fact (`planIdentity` in
  `lib/attribution-capture.ts`). A readable `rb_ft` (it decodes and names a visitor id) names the
  visitor: its first touch is never rewritten and `rf-visitor-id` follows it. With no readable
  `rb_ft` (never set, ended after its 30 days, cleared by the API at signup) a fresh one is written
  together with a **new** visitor id, and when the browser still held an earlier id the new
  visitor's `site.visit` goes out even if the tab already sent one. So a person who returns after the cookie ended, or after signing up, counts as
  a new consented visitor. When the cookie cannot be written (cookies blocked) the stored id is
  kept. The dashboard's signup page follows the same rule
  (`web/lib/attribution/visitor-events.ts`, `planSignupIdentity`).
- **Server switch.** The API answers `404` until `TELEMETRY_VISIT_INGEST_ENABLED` is on, and the
  beacon fails silently. A lost beacon is a lost count, never an error the visitor sees.
- **Page keys** (`pageKeyForPath`, the path without the language prefix, so `/it/pricing` is
  `pricing`): `home`, `features`, `pricing`, `about`, `contact`, `recipes` (the gallery and every
  recipe page), `runner`, `for-<slug>` for the six audience pages, `blog` (the index and tag
  pages) and `blog:<slug>` (cut to 64 characters). Legal pages, the contact thank-you page, a 404
  and an unknown `/for/` slug have no key and send no page view. A page key is never the full
  address.
- **Call-to-action locations in use today:** `hero`, `header`, `header-mobile`, `sticky-mobile`,
  `footer`, `home-get-started`, `features`, `pricing`, `pricing-<plan id>` on each plan card
  (`PlanCtaButton`), `not-found`, `recipe_detail`, `blog-article-cta`, and `for-<slug>-hero` and
  `for-<slug>-final` on each audience page. A location is a lower-case token matching
  `^[a-z0-9][a-z0-9:._-]{0,63}$`; anything else sends no event. The click is sent whether the
  button leads to signup or, with signup switched off, to the contact page.
- **Every event carries the landing** (campaign labels, landing path, referrer host), so whichever
  event reaches the API first records the same first touch. `source` in the body is the
  `utm_source` only; the API derives the label (`utm_source`, else the referrer host, else
  `direct`).
- One beacon per visitor, event and detail per page life; the API drops repeats for 30 minutes on
  top of that. Sent with `keepalive`, so a click that leaves the page still arrives.
- The beacon reaches the API through nginx on the control box (`/api/v1/*`, see
  [`tls.md`](tls.md)); the site has no Function for it.

**The "Start free" link carries the audience, the campaign and the landing path.** In the browser,
every link to `/app/signup` gets `?for=<persona>` and the landing's `utm_source`, `utm_medium`,
`utm_campaign`, `utm_content` and `utm_term` appended, and last, only when the link has a campaign
parameter, `lp=<landing path>`: the path of the page the visit began on
(`lib/attribution-signup-link.ts`, used through `useSignupHref` in `PrimaryCta`, `PlanCtaButton`,
`SignupLink` and `SignInLink`). So a visit that began on
`/for/creators?utm_source=…&utm_content=hn-launch&for=creator` leads to
`/app/signup?for=creator&utm_source=…&utm_content=hn-launch&lp=%2Ffor%2Fcreators`, and a visit with
no campaign leads to `/app/signup?for=creator` with no `lp`.

- **`lp` is a path and nothing more.** It goes through `sanitizeLandingPath`: no query, no
  fragment, no host, and never the referring site. The dashboard reads it under the same name and
  rule (`web/lib/auth/signup-carry.ts`, `LANDING_PARAM`) and sends it as the landing page of the
  signup; the Go API accepts it on the OAuth start address too. A test compares the name, the rule
  and the path pattern with the web files.

- The server-rendered address is the plain one (`/app/signup`, or `/app/signup?plan=creator`).
  The site is a static export and cannot know the visitor's link; the decorated address arrives
  right after hydration.
- An existing query is kept byte for byte and a parameter already present is never overwritten,
  so decorating twice changes nothing. Every value goes through the same sanitizers as the
  first-touch cookie; an invalid value is dropped. With nothing to carry, the address is returned
  unchanged.
- **Which audience.** `GET /?for=<persona>` is redirected by the edge to `/for/<slug>` with the query intact (see "Languages"); the `?for=` on the link the visitor arrived through wins, for as long as
  they do not open a different audience page. Otherwise the button's own audience (an audience
  page, or a post written for one audience), otherwise the audience page they opened last. Only
  the six personas with a landing page are carried; `other` never is.
- **The sign-in link is decorated too** (`/app/login`), because a person who taps "Sign in" and
  continues with Google or GitHub gets a new account when none exists.
- **Links that open a new document carry the same values**, `lp` included: the language switcher,
  and a link from a translated page to an English-only one (the blog, the legal pages). Those are
  different root layouts, so the browser does a full load and the memory would start empty
  (`useCarryHref`, `@/i18n/link`). Links that stay inside one layout are not decorated, so their
  addresses stay clean.
- **The new document reads `lp` back as the landing path of the visit**
  (`lib/attribution-landing.ts`), so it does not take itself for the landing. It believes `lp`
  only when all three hold: the referrer is this host (it was one of our own links, not something
  typed into a posted campaign link), the address has a campaign parameter, and the value is a
  plain path. Otherwise the landing path is the document's own. One gap follows: a visitor whose
  browser hides referrers, and who has not accepted cookies, loses the original landing path on a
  language switch; the link then names the second page.
- **This happens whatever the cookie choice.** Nothing is stored for it: the values come from
  memory and travel in the address the person clicks. The audience is always saved on the new
  account as a suggestion. The campaign labels and the landing path are saved on the account
  without a cookie only when `ATTRIBUTION_URL_CAPTURE_ENABLED` is on (off by default; a privacy
  switch described in the Privacy Policy section "What a sign-up link carries"). While it is off
  they also stay out of a Google or GitHub sign-in.
- With signup switched off the primary button leads to `/contact` and is never decorated.

**Tracked links, `/l/<code>`.** A short address that counts the click and redirects to a landing
page with the campaign labels (`utm_content` is the link code). The count is made by the API
(`GET /api/v1/l/{code}`); the site only gets the request there.

- **On the boxes (production, staging and self-host):** nginx serves `/l/<code>` itself (`nginx/locations.conf`), GET and
  HEAD only, with no cookie or session passed on. There is no Node route twin under `site/app`: a
  dynamic route handler would break the static export, and nginx answers `/l/` before the `site`
  container (a test asserts the file does not exist). When the API cannot answer or the address
  is over the limit, nginx redirects to `/?utm_content=<code>`: the click is lost, the campaign is
  not.
- **Legacy Pages Function (unused; nothing deploys it, kept pending removal):** `site/functions/l/[code].ts` calls `followTrackedLink`
  (`lib/tracked-link.ts`). It lowercases and checks the code, asks
  `{REELBOLT_API_URL}/api/v1/l/{code}` with a 3-second timeout, and relays a `302` whose `Location`
  is a path on this host, with `Cache-Control: no-store`. `HEAD` stays `HEAD`; other methods get
  `405`. `site/functions/l/[[path]].ts` sends everything else under `/l/` to `/`, as nginx does.
  - **What it sends upstream** is a closed list: the caller's `User-Agent` (512 characters at
    most) and, when present, the browser's six request-kind headers (`Sec-Fetch-Mode`,
    `Sec-Fetch-Dest`, `Sec-Purpose`, `Purpose`, `X-Purpose`, `X-Moz`; 64 characters each), which
    is how the API tells a preview, a crawler or a prefetch from a person's click. Never a cookie,
    `Authorization` or an address header, and a caller's own `X-ReelBolt-Site-Token` is dropped.
  - **The relay token.** With `SITE_FORWARD_TOKEN` set, the request carries
    it as `X-ReelBolt-Site-Token`. The origin's nginx compares it with its own copy and then
    limits the relay in its own zone (`link_relay`, 1200 a minute) instead of the per-address one
    (`link_redirect`, 120 a minute), which every click relayed from Cloudflare would otherwise
    share. The token is never logged and never part of an answer.
  - **When the click cannot be counted** (`REELBOLT_API_URL` unset or unusable, a timeout, a
    network error, any answer that is not a `302`, a `Location` that is not a plain own-origin
    path), a well-formed code goes to `/?utm_content=<code>`, exactly what nginx answers in the
    same case: the click is lost, the visit still belongs to the link. A malformed code goes to
    `/`. The API's own answers are relayed as they are, including its `302 /` for an unknown or
    archived code.
- **In the legacy Pages Function, `REELBOLT_API_URL` must be set at run time.** Unset, every tracked link
  lands on `/?utm_content=<code>`: no click is counted, the link's landing page and audience are
  lost, and it still looks as if it works.
- The Function keeps no limit of its own. In production nginx limits `/l/*` and the Cloudflare
  rate-limit rule on `/l/*` sits in front (see "Launch must-dos for the `site` container", row 7).
- A Node route twin of this relay must not be added: Node's own `fetch` overwrites
  `Sec-Fetch-Mode` with `cors`, which the API would read as "not a page being opened" and count
  every click as a bot hit. The Workers runtime passes the header through as given.

**`public/_routes.json`** (legacy, unused: Pages-only) lists the paths that run a Pages Function. Everything else is served as
a static file. Today:

| Path | Function |
|---|---|
| `/` | `functions/_middleware.ts`: the language redirect on the home page |
| `/api/*` | contact, subscribe, promotions and the recipe gallery proxies |
| `/l/*` | tracked links |
| `/recipes/*`, `/it/recipes/*` | the recipe page shell for a recipe listed after the build |

Adding a language needs its `/<code>/recipes/*` line (a test fails otherwise). Adding a Function
under a new path needs its line here, or the Function never runs. `lib/tracked-link.test.ts` pins
the `/l/*` entry.

**`out/_redirects` (legacy, unused Pages bundle)** is written by the build from `scripts/pages-redirects.mjs`. With
signup off it sends `/app` and `/app/*` to `/contact`. With signup on it writes no `/app` rule,
because "Start free" must reach the dashboard and never the contact page; where Pages itself
still answers `/app/*`, the visitor then sees the 404 page, which is a routing fault to fix.

**Tests.** `lib/attribution-capture.test.ts`, `attribution-landing.test.ts`,
`attribution-persona.test.ts`, `attribution-signup-link.test.ts`, `site-events.test.ts`,
`tracked-link.test.ts`, `pages-redirects.test.ts` and `lib/audiences/blog-posts.test.ts`.
`lib/ga-event-inventory.test.ts` is unchanged: no Google Analytics event was added.

## Public alpha label, founder strip and legal facts

- The footer shows the public alpha label as one small line (`ALPHA_NOTICE` wording; the footer's `alpha` message) with a "Privacy and telemetry" link to the Privacy Policy, and the hero shows it too. The notice bar that used to sit above the header is gone (no `AlphaBanner`); the cookie bar's first layer shows what the cookies are for, a privacy link, Details, and Reject and Accept side by side, and Reject loads no Google tag; the telemetry disclosure lives in the Privacy Policy. The reference wording is "ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change.".
- The founder strip (forever 50% off, ends 2027-01-31) replaced the 3-months launch strip; it is static copy (no price written) with a client-side end-date check that hides it after 2027-01-31 23:59 Europe/Rome. A money-back line sits beside it on pricing.
- Legal identity comes from `site/lib/legal-placeholders.ts` by name: controller Tommaso Celano, "P.IVA IT02862070030" (renders only when set), courts of Novara for business users, mailboxes hello@/support@/privacy@. No address literal is written in docs.
- Terms and Privacy are both at version 2026-11-17 (avatars of objects, pictures and the workspace avatar library: Terms 9.1 and 11, the providers table, the avatar paragraphs of the Privacy Policy and Acceptable Use 2; migration `AvatarLibraryLegalVersions`; the Terms bump asked every user to re-accept; published by the owner's decision, no lawyer review claimed). `LAST_UPDATED` reads November 17th, 2026; `TERMS_LAST_UPDATED` reads November 24th, 2026, because the Terms gained one non-material AGPL clarification (12.1: the visible date moves, the stored version stays 2026-11-17, nobody re-accepts). Before that, the Privacy Policy was at version 2026-11-11 (the new-user funnel: the sections "Short links", "What a sign-up link carries" (audience, campaign labels and the landing path `lp`), "Welcome questions" and "First-step records", and in its storage table the `rf-visit-sent` row and four functional rows: `rb_locale`, the alpha notice dismissal, the offer bar dismissal and the app's `rb-signup-intent:v1`). `site/lib/legal-disclosure.test.ts` pins those sentences and keys against the code, and fails when a new site file writes to browser storage without a table row. It is a Privacy-only change, so signed-in people acknowledge it and nobody re-accepts the Terms. The Terms stay at 2026-11-10 (recipes: Terms 10 has the gallery licence, unpublishing and takedown, Acceptable Use 5.1 the recipe rules, the Privacy Policy the recipe data and its retention); that Terms bump asked existing users to re-accept. The step before, 2026-11-05, was avatars (Terms 9.1 is a consent-based likeness rule, the Privacy Policy describes avatar data). Those dates read November 11th and 10th, 2026 then.
- **Trademark.** ReelBolt™ (Italian application for the word mark REELBOLT, filed 2026-10-27; pending, not registered) is owned by Tommaso Celano. Use ™, never ®, until a registration is granted. The symbol lives in `site/lib/trademark.ts` (a leaf module, so the client header can import it) and the sentence in `TRADEMARK_STATEMENT` (`legal-placeholders.ts`); it shows on the header wordmark, in the footer and in Terms section 12.1. Names in metadata and schema.org stay plain. The dashboard's signed-out frame (`web/components/shell/TrademarkNotice.tsx`), the docs-site footer and the exported `TRADEMARK.md`/`COMMERCIAL.md` carry the same statement. `trademark-notice.test.ts` pins it, and fails on a registered-mark symbol. No filing number, receipt or address goes on any surface. The Terms "Last updated" date and version were not bumped by this clause.

## Blog

Blog posts are TSX files in `site/content/blog/`, one file per post. `site/lib/blog/posts.ts` is the
registry: a post is built only when it is listed there, and listings read from the same registry.
Each post has a `date` (`YYYY-MM-DD`), an optional `updated`, `tags`, `readingMinutes`, and optional
`hero` and `faqs`. Metadata is checked in `site/lib/blog/validate.ts`.

Set `unlisted: true` to build a post at its URL without listing it. It is served with `noindex` and
left out of the index, tag pages, related posts, sitemap, RSS and the llms files.

Routes: `/blog` (index), `/blog/[slug]`, `/blog/tag/[tag]`, `/blog/rss.xml` (RSS of the listed
posts), and `/blog/[slug]/opengraph-image` (each post's own social image).

What the 2026-11 repositioning changed here:

- **The pillar post.** The index features one post above the grid: `LEAD_SLUG` in
  `app/(en)/blog/page.tsx`. It was the software-promo roundup (`product-promo-video-tools-2026`);
  it is now `ai-video-editors-for-people-who-do-not-edit-2026`, the roundup for people who do not
  edit. The old roundup stays published and listed. If the lead slug is not a listed post, the
  index shows the plain grid.
- **The index and tag pages speak to every audience.** Their title, description and intro no
  longer say "for software teams": the blog is "guides to making videos from the clips you
  already have, ReelBolt news, and sourced comparisons with other video tools".
- **The block under every article** (`app/(en)/blog/[slug]/page.tsx`) reads "See it on your
  footage / Turn the clips you already shot into a finished video" and holds one `PrimaryCta`
  with the location `blog-article-cta`.
- **A post written for one audience sends that audience to signup.** `lib/audiences/blog-posts.ts`
  maps four slugs to a persona (`stop-editing-on-weekends` creator, `video-for-your-small-business`
  business, `lessons-into-short-clips` educator, `tell-your-cause` nonprofit); the article's button
  then carries `?for=<persona>`. Comparisons, release notes and posts for everyone carry none.
  `lib/audiences/blog-posts.test.ts` checks every slug against the files in `content/blog`, so a
  renamed post fails the test instead of losing its audience.
- **Plan figures in a post** come from `catalog()` in `lib/blog/catalog.ts` (it throws on an
  unknown path, so a placeholder cannot render empty). A post never states a competitor's price.
  The fact record for the comparison posts is `docs/marketing/blog-drafts/FACTCHECK.md`.
- The blog stays English only. A visitor who opens a post from a translated page keeps their
  campaign and audience in the link (see "Visitor tracking, tracked links and the signup link").

## Example page

`/examples` (`components/pages/ExamplesPage.tsx`, messages `examples.json`) is for visitors who
arrive from an AI assistant or have no footage yet. It plays the launch film the homepage hero
plays (`launchFilm()`, `LaunchFilm`, same build-time env switch) and says plainly that it is a
film by the ReelBolt team, not a customer result. Everything it states about the film comes from
`docs/marketing/launch-2026/assets.md`. Add a customer example only when a real, consented one
exists. The primary CTA is `PrimaryCta`; the secondary link goes to `/features`, and the closing block links to `/recipes`.

## Crawlers, llms.txt and response headers

- **Robots.** `site/app/robots.ts` gives `*` and each named AI crawler the same rules: allow `/`,
  disallow `/api/` and `/contact/thank-you`. The named AI crawlers are the `AI_CRAWLERS` list in
  that file. Bytespider is left off on purpose.
- **llms.txt and llms-full.txt.** `site/app/llms.txt/route.ts` and `site/app/llms-full.txt/route.ts`
  only gather inputs. The text is built in `site/lib/llms.ts`, so the two files cannot disagree.
  Edit the copy there.
- **`public/_headers`.** Only Cloudflare Pages reads this file, and nothing deploys to Pages, so it is legacy and unused. It sets security headers on every path
  (`nosniff`, `Referrer-Policy`, `X-Frame-Options: DENY`, `Permissions-Policy`) and cache rules for
  `/_next/static/*` (one year, immutable), `/brand/*`, the favicons, `/icon.svg` and
  `/apple-touch-icon.png` (one day). The `site` container does not read it; nginx and the Next server set the live headers.

## Site gates, including the legacy Pages Functions bundle

Production serves the `site` container (Next standalone server) behind nginx, with Cloudflare in front.
Run these in `site/` before every PR that touches the site:

```bash
npm run lint && npm test && npm run build
```

The Pages gates below guard a legacy, unused path, kept pending removal; nothing deploys it. Run them
only if you touch `site/functions/` or `site/scripts/build-pages.mjs`:

```bash
NEXT_PUBLIC_SITE_URL=https://reelbolt.ai npm run build:pages && node scripts/check-faq-schema.mjs
npm run check:functions   # npx wrangler@3.114.17 pages functions build, the bundler Cloudflare Pages runs
```

For the legacy path, `npm run build:pages` passing is not enough. After the static export, Pages would bundle
`site/functions/` with its own wrangler (3.114.17), whose esbuild rejects JSON import attributes
(`import x from './x.json' with { type: 'json' }`). `node --test` requires that attribute, so the two
cannot share one module. Anything reachable from `site/functions` must therefore stay
attribute-free:
- the functions read the consent wording version from `lib/marketing/function-wording.ts`, which
  imports the JSON plainly and uses the pure helpers in `lib/marketing/wording-core.ts`;
- `lib/marketing/subscribe.ts` takes the wording version as an argument and never imports it;
- `lib/marketing/wording.ts`, which keeps the attribute for Next and the tests, is never imported
  from `site/functions`.

A test in `lib/marketing/subscribe.test.ts` fails if a module on that path gains an attribute or
imports `wording.ts`. `check:functions` is the real-bundler check.

## Launch checklist

**Launch must-dos.** The site runs as the `site` container on the control box, behind nginx, with Cloudflare in front. Work through this before pointing real traffic (and real search
engines) at the site.

1. **Set the real domain.** `NEXT_PUBLIC_SITE_URL` in `.env` — see "single-swap domain contract"
   above. This is a build-time arg (`site/Dockerfile` passes it to `next build`), so the `site`
   image must be rebuilt after changing it, not just restarted.
2. **Legal facts live in `site/lib/legal-placeholders.ts`** (filled 2026-10-26; the controller is a
   natural person, Tommaso Celano, sole trader). Constants to keep current:
   - `CONTROLLER_NAME`, `COMPANY_LEGAL_NAME` (the brand "ReelBolt")
   - `REGISTERED_ADDRESS` - personal data: lives ONLY in that file and the marketing-email footer;
     never in tests, docs, logs or fixtures (tests assert shape through the import). It is rendered only on the legal pages (via `OPERATOR_STATEMENT`) and in the email footer; the site footer, contact, about and thank-you pages never show it (pinned by `legal-disclosure.test.ts`)
   - `PARTITA_IVA` / `PARTITA_IVA_LABEL` - shown as "P.IVA ..." wherever the legal identity appears
   - `GENERAL_EMAIL`, `SUPPORT_EMAIL`, `PRIVACY_EMAIL`
   - `DPO_CONTACT` — data protection officer contact, if one is required/appointed
   - `GOVERNING_LAW` — the jurisdiction whose law governs the terms
   - `COURTS_JURISDICTION` — which courts have jurisdiction over disputes
   - `LAST_UPDATED` — the date the legal pages were last revised
   - `PHONE_NUMBER` — business phone number
   - `SUPERVISORY_AUTHORITY` — the data-protection authority visitors can complain to
   - `CONTACT_RETENTION_PERIOD` — how long contact-form submissions are retained
   - `LOG_RETENTION_PERIOD` — how long server/access logs are retained
   - `LIABILITY_CAP` — the liability limitation figure/formula in the terms
   - `SERVICE_AGREEMENT_REFERENCE` — reference to any separate master service agreement
   - `TRANSFER_MECHANISM` — the mechanism (e.g. SCCs) governing any international data transfer
   - `RESPONSE_TIME_SLA` — the response-time commitment shown on the contact page

   The remaining facts the site needs were supplied by the owner during H7 (subscriptions and the
   desktop runner) and are already filled in the file, which is why they are intentionally absent
   from the list above: `HOSTING_PROVIDER` (Hetzner, Falkenstein, Germany), `EMAIL_PROVIDER`
   (Cloudflare Email Sending for transactional and contact-form mail; campaigns go through the Brevo API, env `BREVO_CAMPAIGN_API_KEY`; the list send is off by default, `Marketing:Campaigns:ListSendEnabled`, so dry-run and test send only until it is enabled), `STORAGE_PROVIDER`
   (Cloudflare R2), `PAYMENT_PROVIDER` / `PAYMENT_PROVIDER_STATEMENT` (Stripe (Link) seller of record under Managed Payments, Paddle for existing subscriptions), `IP_REPUTATION_PROVIDER` (ipinfo.io), `MODEL_PROVIDERS`
   (DeepSeek primary chat today, plus OpenAI, Azure OpenAI, Anthropic, MiniMax, Higgsfield and Fish
   Audio), `REFUND_POLICY`, `CREDIT_EXPIRY_PERIOD` (1 year), `CONTENT_RETENTION_PERIOD`
   (3 years) and `RUNNER_EULA_LAST_UPDATED` (October 8th, 2026).
3. **Analytics.** Set `NEXT_PUBLIC_GA_MEASUREMENT_ID` to a real GA4 measurement ID
   (`G-XXXXXXXXXX`), or leave it empty to ship with no analytics at all — no Google tag is injected.
   With an ID the tag loads on every page under Google Consent Mode v2, with all storage denied
   until the visitor accepts the cookie banner (see below).
   The value is inlined at build time, so it must be a **build** argument: for the self-hosted
   `site` image that is the compose `build.args` entry and the CI image matrix `build_args`
   (repository variable `NEXT_PUBLIC_GA_MEASUREMENT_ID`); the legacy Pages deploy job (unused) would read it from its build environment. A runtime env var on a running container does not change a
   production bundle. a private operator test pins this for `site` and
   `web`.
4. **Contact form delivery.** Enquiries are sent by **Cloudflare Email Sending**, the same provider
   go-api uses. Set `CLOUDFLARE_EMAIL_ACCOUNT_ID`, `CLOUDFLARE_EMAIL_TOKEN` (a secret) and
   `SMTP_FROM`; both handlers send through one shared module (`site/lib/marketing/mailer.ts`), so
   there is no webhook URL and no second copy of the request to keep in step. With any of the three
   blank the form answers "Failed to deliver message" rather than accepting the enquiry into the
   void. See [Contact form delivery](#contact-form-delivery) below.
5. **Switch the build to production.** Set `SITE_BUILD_TARGET=production` and
   `SITE_NODE_ENV=production` in `.env`, and comment out or remove the dev-only volume mounts
   under `docker-compose.yml`'s `site` service (`./site:/app`, `/app/node_modules`, `/app/.next`)
   — those mounts exist for Turbopack hot reload and would shadow the precompiled standalone build
   the production Dockerfile target produces.
6. **Submit to search engines.** Once the real domain is live and serving real content, submit
   `https://<domain>/sitemap.xml` to Google Search Console (and any other search console you use). The
   site also serves `https://<domain>/llms.txt` (`site/app/llms.txt/route.ts`), a plain-text
   summary for AI assistants; it is generated from the same `siteConfig`/`HOME_FAQS` sources as
   the pages, so it needs no separate upkeep.

7. **Turn on "Start free", once self-serve signup is live (decision D25).** Every primary CTA on
   the site reads `siteConfig.primaryCta` (`site/lib/site-config.ts`, rendered through
   `site/components/ui/PrimaryCta.tsx`). With `NEXT_PUBLIC_SIGNUP_ENABLED` unset or anything but
   `true` — the default, and what a plain `docker build` produces — it
   is "Talk to us" pointing at `/contact`, and the header/footer keep their "App · Coming soon"
   state. Set it to `true` and the same source ships "Start free" pointing at `/app/signup`, with
   a "Sign in" link to `/app/login` where the coming-soon marker was. It is **build-time**
   (`NEXT_PUBLIC_*` is inlined, and the browser bundle is built once), so changing it needs a
   rebuild — `site` is rebuilt by the Docker build arg of the same name. Flip it only when
   `/app/signup` actually answers: the flag is a display switch, and `POST /api/v1/auth/signup`
   still 404s unless the Go API itself has `SIGNUP_ENABLED`. The value is read through the literal
   `process.env.NEXT_PUBLIC_SIGNUP_ENABLED` member expression in `site/lib/site-config.ts` on
   purpose: read as a property of a passed-in object it compiles and renders correctly on the
   server, then silently evaluates to `undefined` in the browser bundle, so every client component
   (the header, the sticky mobile CTA, every `PrimaryCta`) re-renders the old state on hydration
   while the server components keep the new one. H6 found and fixed that; the comment there is the
   warning.

8. **The desktop runner turns itself on from the published `latest.json`.** `/runner` ships as its
   "not available yet" state (`noindex`, absent from `sitemap.xml` and `llms.txt`) unless the build
   can fetch and strictly validate `https://downloads.reelbolt.ai/runner/stable/latest.json`.
   `site/scripts/fetch-runner-latest.mjs` (run by `npm run build`) does the fetch
   and writes `site/lib/runner-latest.generated.json`; nothing is filled by hand. Only
   `kind: "installer"` entries are used (platform windows-x64/linux-x64/linux-arm64, https URL on
   `downloads.reelbolt.ai`, 64-hex SHA-256, label, size, version). A fetch failure or invalid data
   leaves the empty state; set `RUNNER_DOWNLOADS_REQUIRED=true` on the build **after launch** so a
   broken fetch fails the deploy instead of silently shipping the empty page. `RUNNER_PUBLISHER_NAME`
   (optional) names the Windows signer in the page note; `RUNNER_LATEST_URL` may point at a
   `http://localhost` fixture for testing. Because the value is baked in at build time, a new release needs
   a site rebuild to show up. `site/lib/runner-downloads.test.ts` holds the fail-closed contract.

9. **Rate-limit the three `/api` endpoints at Cloudflare.** `/api/contact` and `/api/subscribe` are
   POST route handlers and `/api/promotions` is a GET one, all in the `site` container
   (`site/app/api/*/route.ts`) behind nginx. nginx carries the authoritative per-address limits (see
   the re-check below); the Cloudflare edge rules add a first layer on the proxied `reelbolt.ai`
   hostname. The zone's
   `http_ratelimit` entrypoint ruleset is the authority, and **creating it is an operator action**:
   this repository has no Wrangler configuration file, so Cloudflare is managed with the `cf` CLI
   (or the dashboard), never as repo code. **The rules are NOT APPLIED yet (2026-10-18)** — verified
   empirically: 7 consecutive POSTs to `https://reelbolt.ai/api/contact` all answered `200` (a
   5/600 s rule would have blocked the 6th).

   A zone has **one** `http_ratelimit` entrypoint ruleset, so only one owner may write it, and
   `cf rulesets entrypoint update` **replaces the whole entrypoint** — both rules must be sent
   together, and any rule already in that phase but absent from `rules.json` is dropped. Read the
   current entrypoint **first** (it is empty today), then apply. Write the rules to `rules.json`:

   ```bash
   cf auth login                       # or set CLOUDFLARE_API_TOKEN for an unattended run
   cf rulesets entrypoint get http_ratelimit --zone reelbolt.ai     # pre-flight: what is there now
   cf rulesets entrypoint update http_ratelimit --zone reelbolt.ai \
     --name "reelbolt site api rate limit" \
     --description "Rate limit the site contact, newsletter and promotions endpoints, which carry no limit of their own" \
     --rules @rules.json
   ```

   `CLOUDFLARE_API_TOKEN` needs **Zone WAF → Edit** (the token UI also calls this Rate Limiting →
   Edit) on the `reelbolt.ai` zone, plus **Zone Read** — the zone-scoped token the rest of the
   Cloudflare work uses.

   ```json
   [
     {
       "action": "block",
       "enabled": true,
       "description": "POST /api/contact or /api/subscribe",
       "expression": "(http.request.method eq \"POST\" and (http.request.uri.path eq \"/api/contact\" or http.request.uri.path eq \"/api/subscribe\"))",
       "ratelimit": {
         "characteristics": ["ip.src"],
         "period": 600,
         "requests_per_period": 5,
         "mitigation_timeout": 600
       }
     },
     {
       "action": "block",
       "enabled": true,
       "description": "GET /api/promotions",
       "expression": "(http.request.method eq \"GET\" and http.request.uri.path eq \"/api/promotions\")",
       "ratelimit": {
         "characteristics": ["ip.src"],
         "period": 60,
         "requests_per_period": 100,
         "mitigation_timeout": 60
       }
     }
   ]
   ```

   Preview without sending with `--dry-run`, then re-read the ruleset with
   `cf rulesets entrypoint get http_ratelimit --zone reelbolt.ai` to confirm both rules landed. The
   dashboard equivalent is Security, WAF, Rate limiting rules.

   The rules match the **path alone, never the hostname**. A `http.host` predicate would be
   needless: the three paths are served only by the `site` container (the dashboard/API origin serves
   `/api/v1/*`), so a path-only rule cannot meter the wrong origin and keeps working if another
   proxied hostname is ever pointed at the same origin.

   *Rule 1 — the two POST endpoints.*
   - **Expression:** `(http.request.method eq "POST" and (http.request.uri.path eq "/api/contact" or http.request.uri.path eq "/api/subscribe"))`
   - **Counting characteristic:** `ip.src`
   - **Rate:** 5 requests per 600 s, matching the `site` route's in-memory limit
   - **Action:** `block` (configurable to `managed_challenge`)
   - **Not covered by that rule: `GET /api/promotions`** (`site/app/api/promotions/route.ts`). It is a
     read-only proxy with a 60 s cache header, so it needs a **generous** limit, not the contact form's —
     but it currently has none, and this checklist has named it as needing one since before this
     release. Tracked as an alpha follow-up rather than silently dropped.

   5/600 s is deliberately tight because the abuse is a scripted form-spam burst, not a person: the
   `site` route already answers a sixth request in the window with `429`, and matching it
   keeps the edge and origin behaviour the same. **The trade-off to judge:** a shared corporate NAT
   where several people fill the contact form in one afternoon can trip it, and each of them then
   sees the form's server-error banner. That is the price of the limit; raise
   `requests_per_period` if the team would rather absorb more spam.

   *Rule 2 — the read-only proxy.*
   - **Expression:** `(http.request.method eq "GET" and http.request.uri.path eq "/api/promotions")`
   - **Counting characteristic:** `ip.src`
   - **Rate:** 100 requests per 60 s
   - **Action:** `block`

   The abuse here is scraping and origin load, not spam, so the numbers are generous:
   a real pricing-page view is one GET, and the endpoint's own 60 s cache TTL means a visitor who
   respects the response cache never approaches it. The 60 s window matches that TTL, and 100 per
   window leaves a busy shared NAT two orders of magnitude of headroom.

   Plan limits for the rate-limit rules differ (Free 1 rule/10 s, Pro 2 rules without a method field):
   an operator document lists a rules file per plan.

   The legacy, unused Pages Functions (`site/functions/api/contact.ts`) carry a best-effort
   per-isolate counter. It is kept in the tree pending removal; nothing deploys it, so it plays no part
   in production limits. The nginx zones and the zone rules above are the authority.

   **Re-check against the host set .** `reelbolt.ai/app/*` and
   `reelbolt.ai/api/*` are routed to the control box's nginx (`docs/tls.md` § "Serving `reelbolt.ai`
   from the two boxes"); the path-only expressions above still fire on every hostname — which is what
   we want — and no rule starts metering the wrong origin:
   - `/api/contact` and `/api/subscribe` on `reelbolt.ai` reach the **origin** (the `site`
     container) through nginx. The origin carries its own real per-address limits:
     `nginx/locations.conf` has three new locations — `location ~* ^/api/contact(/|$)`,
     `location ~* ^/api/subscribe(/|$)` and `location ~* ^/api/promotions(/|$)` — each with
     `limit_req`, proxying to the `site` container and answering `429` (`limit_req_status 429`). The
     two POST endpoints use the `site_forms` zone (`rate=1r/m`, `burst=4`) and the read-only
     promotions read uses `site_promotions` (`rate=100r/m`); both are keyed on `$client_ip_key`,
     built from `$binary_remote_addr` after `realip.conf`, so behind Cloudflare it is the visitor's
     address and a direct client cannot forge it (IPv6 clients key on the /64). The `site` container's
     in-memory limit remains as a second layer. The edge rule matches the path on the proxied hostname. **No rule matches traffic it should not, and none
     fails to match what it should.**
   - `/api/v1/*` — including the Paddle webhook `/api/v1/billing/webhooks/paddle` — is deliberately
     **not** matched by any of these rules: it is the app API, and the webhook must stay
     unauthenticated and uncapped so a delivery is never throttled.

   The origin rate is not literally 5 per 600 s, and nginx cannot express that: `limit_req_zone`
   `rate=` accepts only an integer `r/s` or `r/m` (verified on `nginx:alpine` — `5r/10m`, `5r/h`,
   `0.5r/m`, `0.5r/s` and `1r/10s` are all `invalid rate`). The slowest rate nginx can express is
   `1r/m`, already looser than 5 per 600 s (= 1 per 120 s), so the rule has no exact nginx form;
   `1r/m burst=4` is the closest — the same five requests pass and the sixth is `429`, the outcome
   an operator checklist measured ("a 5/600 s rule would have blocked the
   sixth"). Do not "fix" this back to a literal 5-per-600 s: nginx has no such rate. The read-only
   `GET /api/promotions` is deliberately generous (`site_promotions`, `100r/m`): it is a proxy with a
   60 s cache, fetched once per page load by the promo banner (`site/app/layout.tsx`), and
   `site/components/promo/usePromotions.ts` resolves it to `null` on any failure — a tight limit
   would silently empty the banner and the pricing badges for ordinary visitors behind one address.

   Deploy: nginx runs only on the control box, so this change ships with
   `REELBOLT_ROLE=control infra/scripts/release.sh deploy` run on that box from `/opt/reelbolt`; the
   media box is untouched.

   **`/api/v1/auth/*` — judgement (explicit): no new edge rule.** Login and signup become reachable
   on the production host for the first time, but the origin already enforces them: the per-IP signup
   limiter and Turnstile (go-live rows 3.5) and the Go API's own `auth_public` nginx limit (5 r/m,
   `nginx/locations.conf`). An edge rule on `/api/v1/auth/*` would double-count legitimate login
   retries behind a shared egress IP and risk locking out real users for abuse the origin already
   caps. **Decision: leave `/api/v1/auth/*` to the origin; revisit only if the origin limiter proves
   insufficient.**

   The periods on offer depend on the Cloudflare plan. If 600 s (or 60 s) is not available, lower
   `period` in the `rules.json` above to the longest period offered and reduce
   `requests_per_period` proportionally. The form submits with `fetch`, which cannot solve
   a Managed Challenge, so with either action a limited visitor sees the form's server-error
   banner. See [Contact form abuse protection](#contact-form-abuse-protection).

10. **Set `SITE_FORWARD_TOKEN` on both sides.** The token lets the API count newsletter opt-ins per
    visitor IP instead of per Cloudflare egress address. Generate one value:

    ```bash
    openssl rand -hex 32
    ```

    Put it in the server `.env` as `SITE_FORWARD_TOKEN` (compose passes it to `inference` as
    `Marketing__SiteForwardToken`, to `site`, and to `nginx`, which checks it on a
    tracked-link click the site relays: see row 7 below). There is no second place to set it: all three
    read the same `/opt/reelbolt/.env` on the control box. nginx reads it only when its
    container is created, so after setting or changing it recreate nginx once
    (`docker compose up -d nginx`; on a box on deploy slots the operator release script, a
    short outage). Redeploy with
    `REELBOLT_ROLE=control bash infra/scripts/release.sh deploy <sha>` so the containers pick it up.
    Leave it unset on either side and the
    API ignores the forwarded IP: opt-ins still work, but the per-IP cap counts the egress address,
    and while the API's domain or global alarm threshold is passed no opt-in gets a confirmation
    mail (only a request carrying the matching token counts as bot-checked).
    See [`marketing-consent.md`](marketing-consent.md#sending-limits).

## Contact form delivery

Both handlers send the enquiry by **Cloudflare Email Sending**, through one shared module —
`site/lib/marketing/mailer.ts` — which mirrors the Go API's transport
(`api/services/cloudflare_mailer.go`): the same endpoint
(`POST /client/v4/accounts/<account>/email/sending/send`), the same JSON body with `to`, `from`,
`subject`, `html` and `text` as separate fields (never a MIME message), the same 15 s request
timeout, and the same 8 KiB cap on a logged error body. The sender is `SMTP_FROM`
(`welcome@reelbolt.ai`) and the recipient is `support@reelbolt.ai`.

It refuses to send when `CLOUDFLARE_EMAIL_TOKEN`, `CLOUDFLARE_EMAIL_ACCOUNT_ID` or `SMTP_FROM` is
missing, so a delivery failure is the visitor's "Failed to deliver message" rather than a false
success. There is no `CONTACT_WEBHOOK_URL` any more: two copies of the same request is the drift
this one module removes.

## Launch must-dos for the `site` container

The marketing site is served by the `site` container on the control box (Cloudflare proxy, then
nginx, then the Next standalone server). Cloudflare Pages is not used; the Pages code in the tree is a
legacy, unused path kept pending removal. The settings below live in `/opt/reelbolt/.env` on the
control box or in the Cloudflare zone. Each has a way to verify it, because "set" is not the same as
"working".

| # | Must-do | Kind | How to verify |
|---|---|---|---|
| 1 | The rate-limit rules on `/api/contact`, `/api/subscribe` and `/api/promotions` | **Operator** — `cf rulesets entrypoint update http_ratelimit` (launch checklist step 9) | `cf rulesets entrypoint get http_ratelimit --zone reelbolt.ai` shows both rules; Security → WAF → Rate limiting rules shows them; a burst of six POSTs to `/api/contact` from one IP gets the block response, not `200`. |
| 2 | `REELBOLT_API_URL` set | Operator | Present in `/opt/reelbolt/.env` (compose defaults it to `http://inference:8080`). Verify by submitting the form with the newsletter box ticked and confirming a `marketing_consents` row appears. Unset outside compose, the opt-in is accepted but **not forwarded** — silent lead loss. |
| 3 | `SITE_FORWARD_TOKEN` matching the inference API's `Marketing__SiteForwardToken` and nginx's own `SITE_FORWARD_TOKEN` | Operator | One value (`openssl rand -hex 32`) in `/opt/reelbolt/.env`; compose passes it to `site`, `inference` and `nginx`; recreate nginx once so it reads it. Verify the forward carries `X-ReelBolt-Site-Token` and the API counts the visitor IP, not the Cloudflare egress address. For tracked links the same token moves the site's relay from the per-address limit to its own (`link_relay`). |
| 4 | `CLOUDFLARE_EMAIL_ACCOUNT_ID`, `CLOUDFLARE_EMAIL_TOKEN` and `SMTP_FROM` | Operator (the token is a **secret the operator sets**) | All three present in `/opt/reelbolt/.env`. Verify by submitting a real enquiry and confirming `support@reelbolt.ai` receives it. The token is never in the repo or a committed file. |
| 6 | The rate-limit rule on `/api/recipes*` (the gallery's list, detail and media routes) | **Operator** — `cf rulesets entrypoint update http_ratelimit`, one rule for media (`/api/recipes/<id>/preview` and `/poster`, 120 per minute per IP) and one for the rest (list, facets, detail, 60 per minute per IP) | `cf rulesets entrypoint get http_ratelimit --zone reelbolt.ai` shows both rules; a burst of 70 `GET /api/recipes?q=a` from one IP gets the block response, not `200`. Rule files: `infra/cloudflare/rate-limit.recipes.free.rules.json` (Free: one rule in total, 10 s windows) and `rate-limit.recipes.pro.rules.json` (two rules, 60 s window); `entrypoint update` replaces the whole phase, so keep any rule already there. Rate-limit characteristics must include `cf.colo.id`. Also set `REELBOLT_API_URL` at **build** time (the prerender reads it) and schedule a daily deploy so the static pages stay fresh. |
| 7 | Tracked links `/l/*`: `REELBOLT_API_URL` set at **run time** (row 2), `SITE_FORWARD_TOKEN` on both sides (row 3), and the rate-limit rule on `/l/*` | **Operator**. Apply the operator `links` rule file for your plan (`free`, `pro` or `business`) **instead of** the plain `rate-limit.<plan>.rules.json` of row 1: `entrypoint update` replaces the whole phase, so each `links` file is a complete list that keeps the contact, newsletter and promotions rules and adds `/l/` (Free: one rule for all of them, 20 per 10 s; Pro: promotions and `/l/` share 120 a minute; Business: `/l/` has its own rule, 120 a minute). Commands: `release/soft-open-runbook.md` (private) section 4.2. On Free with the recipe gallery on, no shipped file covers the forms, `/api/recipes` and `/l/` in one rule; merge by hand. The files are checked for shape by `infra/scripts/tests/cloudflare-rules.test.sh` and have not been applied to a real zone | Create a test link on `/app/admin/links`, then `curl -sI https://reelbolt.ai/l/<code>`: `302` with a `location:` that is the link's landing page with `utm_source`, `utm_medium`, `utm_campaign` and `utm_content=<code>`. A `location:` of `/?utm_content=<code>` alone means the click was not counted (the variable is missing, or the API did not answer); a bare `/` means the code is unknown, archived or malformed. Open the link once in a browser and check the Links page shows one more click (`curl` counts as a bot hit). Also, only if the legacy Pages bundle were ever deployed, confirm the Pages project has the `nodejs_compat` compatibility flag: on the local Workers runtime the Functions bundle did not start without it, and nothing in the repository sets it. On production nginx on the control box serves `/l/*`, so neither Pages setting applies. |

The sender must be a **verified** address on the zone (e.g. `contact@reelbolt.ai` or
`welcome@reelbolt.ai`); Email Routing must be on; `support@reelbolt.ai` must be on the allowlist if
recipients are restricted. These are operator items on the Cloudflare account, not code.

**Past submissions are gone.** Enquiries submitted
while the sender was unconfigured were refused and not stored, so they cannot be recovered. Size the loss from GA4 `page_view` on `/contact/thank-you` and from `marketing_consents`
rows — an operator task for the launch gate, not a code step.

## Contact form abuse protection

The contact form (`/api/contact`) is served by `site/app/api/contact/route.ts` in the `site`
container. A second handler, `site/functions/api/contact.ts`, is a legacy, unused Pages path kept
pending removal; nothing deploys it. If you touch both, they must stay behaviourally identical, so they share the code that
would otherwise drift — `site/lib/bot-checks.ts` (bot checks), `site/lib/validation.ts` (input
validation), `site/lib/marketing/subscribe.ts` (the newsletter forward) and, since 2026-10-18,
`site/lib/marketing/mailer.ts` (the one module that sends the mail). A fix applied to one handler
and not the other leaves exactly one deployment broken at a time, which is why the delivery itself
lives in a shared module rather than in two copies of the request.

**Bot checks.** A submission is treated as a bot when either of these holds:

- **Honeypot.** The hidden `website` field holds anything but whitespace.
- **Fill time.** The form records `startedAt` (milliseconds since the epoch) when it mounts. The
  submission is a bot when `startedAt` is missing or not a number, when it is less than 3 seconds
  before the server's clock, or when it is more than 60 seconds ahead of the server's clock. Up to
  60 seconds ahead is accepted as a visitor clock running fast.

A bot submission gets `200 { ok: true }`, the same answer as a real one, so it learns nothing. The
enquiry is not validated, sent by email or logged, and the newsletter opt-in is not forwarded to
the API.

**Rate limits.**

| Handler | Limit | Answer |
|---|---|---|
| `site` route (production) | nginx limits the path per client address first (`site_forms`, 1 request a minute with a burst of 4, keyed on the real client address, so five requests pass and the sixth is refused — launch checklist step 9), then the route's own limiter: 5 requests per 10 minutes per client IP, in memory, checked before anything else. The IP is `X-Real-IP` (set by nginx), else the rightmost `X-Forwarded-For` entry. The in-memory half resets on restart and is not shared between replicas. | `429` |
| Legacy Pages function (unused, kept pending removal) | A best-effort per-isolate counter. Nothing deploys it, so it sets no production limit. | `429` |

**Footer newsletter limit.** `/api/subscribe` is limited by the same `site_forms` nginx zone as
`/api/contact` — one bucket per client address, so five form submissions of either kind in a burst
and one a minute after that, then `429` (launch checklist step 9) — and then by the same in-memory
limit as the contact route: 5 requests per 10 minutes per client IP, answered `429`, checked before
the address is validated. The Cloudflare rule in launch checklist step 9 adds an edge limit on the proxied hostname.

**Newsletter opt-in forward.** When the visitor ticks the newsletter box, the route forwards the
address to `POST /api/v1/marketing/subscribe` after the enquiry has gone out. With
`SITE_FORWARD_TOKEN` set, the forward carries `X-ReelBolt-Site-Token` and `X-ReelBolt-Client-IP`
(the route's client IP from `X-Real-IP`), so the API's per-IP cap counts
the visitor. The API's own mail caps are in
[`marketing-consent.md`](marketing-consent.md#sending-limits).

## Who the copy is written for

The marketing pages address a **buyer**: a person with a story to tell and no time to tell it,
deciding whether to use ReelBolt. That is a creator, a small-business owner, an educator, a
nonprofit, a software founder or an agency (the six audience pages), not an engineer evaluating
the stack. That is a deliberate split:

- **Marketing pages say what you get.** "Upload the raw take, get back the finished cut."
  Implementation names (ffmpeg, Remotion, Docker, PostgreSQL, RabbitMQ, Qdrant, Docusaurus, the
  workflow engine, the agent-room architecture) are *out of scope* for `/`, `/features` and
  `/about`. A buyer does not choose a video tool on which encoder it shells out to, and the words
  cost comprehension.
- **So are third-party vendor names.** The speech, video-generation and decision-model services a
  deployment connects (Fish Audio, MiniMax, TypeSafe, Higgsfield, ComfyUI, and any later one) are
  described by role — "the speech service you connect", "a video-generation service" — never by
  name, on the same three pages. They are the customer's vendor relationship, not ReelBolt's
  feature, and naming one would imply an endorsement or a bundled licence that does not exist.
  `grep -niE 'ffmpeg|remotion|docker|postgres|rabbitmq|fish audio|minimax|typesafe|higgsfield|comfyui' site/app/page.tsx site/app/features/page.tsx site/app/about/page.tsx site/lib/faqs.ts site/lib/structured-data.ts site/app/llms.txt/route.ts site/components/home/*.tsx`
  must print nothing. (The trailing `site/components/home/*.tsx` — the Hero and the other homepage
  components — was added by the H8 copy refresh: the components sit outside the three page files
  but feed them, and the H8 acceptance grep covers them.)
- **Claims track what is on `master`, not what is in a PR.** The 2026-10 feature refresh (issue
  #159) is the worked example: narration is "the script spoken word for word in a voice you choose"
  because that is what shipped — no voice cloning, no dubbing, no caption output, so none of those
  appear; the studio view is "a window onto the edit, not a replacement for your editor" because
  the editable timeline is still unmerged, so the site never says "edit"; generated footage is
  "a handful of short clips under a hard spending cap" because that is the shipped planner +
  budget gate, and no second generation vendor is implied. When a feature PR lands, the copy may
  grow; until then it must not.
- **Blog comparison posts name competitors by name only.** A comparison post refers to a competitor
  by name, with no logos or lookalike branding. It quotes no competitor prices, and every factual
  claim about a competitor carries a source and a date.
- **`CLAUDE.md` and `docs/` say how it works.** That is where the stack belongs, and it stays
  exhaustive there.
- **One deliberate exception:** the open-source attribution clause in
  `site/app/legal/terms/page.tsx` still names Remotion and ffmpeg. That is a licensing
  acknowledgement, not marketing copy, and it must stay.
- **A second exception, granted and not yet exercised: `/runner` may name a container app.** The
  desktop runner renders in a container it manages (decision D15: Docker Desktop or Podman, and
  without one the runner advertises ffmpeg-only capabilities), so when that page carries install
  instructions the buyer has to be told which container app to install, and "a container runtime"
  is the jargon instead of the plain word. The permission is narrow: name the container app only
  where the buyer must install one, never as an architecture boast, and never on `/`,
  `/features` or `/about`. **It is not exercised today** — `/runner` currently ships its
  "not available yet" state, which has nothing to install, so it names no vendor at all. The
  `/runner` page is outside the three-page rule above, but the rule's `grep` does not cover it
  either; keep the check honest by adding `site/app/runner/page.tsx` to the next copy sweep
  rather than by loosening the rule.

- **A third exception, in use: `/open-source` and the open-source launch post may name Docker and
  Remotion.** The page and the post speak to people who will run ReelBolt themselves, and they must
  be told what to install and which licence they take on (Remotion is source-available under its own
  terms, and a self-hoster is the licensee for their installation). The permission is narrow: name
  Docker as the thing to install and Remotion in the licence paragraph, never as an architecture
  boast; ffmpeg, PostgreSQL and the workflow engine stay out. `/`, `/features` and `/about` carry
  only buyer language about the open-source release ("the code is open source; read it or run it
  yourself"), and the grep above must still print nothing for them. Add
  `site/components/pages/OpenSourcePage.tsx` (text in `site/messages/*/openSource.json`; the buyer-copy test skips that namespace) to the next copy sweep rather than loosening the rule.

When adding a page or a section, write the benefit first and check it against `CLAUDE.md` second.
If a sentence cannot be traced to a real capability there, it does not ship.

## Structured data

`site/lib/structured-data.ts` emits, per page:

| Schema type | Where | Source |
|---|---|---|
| `Organization`, `WebSite` | every page (root layout) | `siteConfig` |
| `SoftwareApplication` | `/` | `siteConfig` + a `featureList` of real capabilities (editing, graphics, grading/music/SFX, narration, budget-capped generated b-roll, object tracking and blurring, review loop, team workspaces, optional own-computer rendering, BYO provider/self-hosting) — every entry maps to a section on `/features`. `operatingSystem` stays `'Web-based'` deliberately: the desktop runner's signed installers are not released, so the runner platforms are not claimed yet; the value grows in the same commit as `site/lib/runner-downloads` gains artifacts |
| `FAQPage` | `/` | `site/lib/faqs.ts` — **the same array the page renders** |
| `AggregateOffer` | `/` | added to the `SoftwareApplication` by `lib/pricing/offers.ts` — `lowPrice`/`highPrice` are the cheapest and dearest **recurring** plan prices, and `offerCount` counts the offers actually emitted |
| `OfferCatalog` + one `Offer` per priced plan | `/pricing` | `pricingOffersSchema()`, from the same catalog; deliberately not a `Product` (Google treats Product with offers as a product snippet and asks for `review`/`aggregateRating`, which are never invented); each `Offer.url` is `/pricing#<plan id>`, the anchor on that plan's card |
| `FAQPage` | `/pricing` | `site/lib/faqs.ts` `PRICING_FAQS` — **the same array the page renders** |
| `BreadcrumbList` | `/features`, `/about`, `/pricing`, `/for/<slug>` | the trail passed by each page |
| `FAQPage` | `/for/<slug>` | the audience's questions in `messages/<locale>/audiences.json`: **the same array the page renders**, with its figures filled from the catalog first |

The `FAQPage` markup and the visible FAQ section read from one array on purpose: Google treats
FAQ markup whose answers are not visible on the page as a violation, so they must not be allowed
to drift. The pricing FAQs carry no numbers at all — every amount a reader sees is derived from
`site/lib/pricing` at render time, and a price typed into an answer would be a second copy.

**Prices are single-sourced, and that is the rule that replaced the old ban on `offers`.** There
are real published prices now (decision D24), so `/pricing` emits an `Offer` per priced plan and
`/` emits the `AggregateOffer` range. Every one of them — the price, the currency and the billing
unit — is generated from `site/lib/pricing` (`plans.catalog.json`, the vendored copy of
`inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json` that CI diffs and fails on), which
is also what the plan cards and the comparison table render from. **Never hand-write a price in markup or in copy**: a literal in a JSON-LD blob, a
`$29` typed into a sentence, or a hardcoded top-up rate is a second source of truth that Google
will eventually index after the catalog has moved on. A plan with no published price (Enterprise)
gets no `Offer` at all rather than a guessed one — decision D7 keeps it "Contact us" only.
`aggregateRating` remains banned outright: still no real reviews, and fabricated review markup is
a manual-action-level violation rather than a growth tactic.

## Google Consent Mode v2 — GA4 is on the page until the visitor rejects

`CookieConsentProvider` (`site/components/consent/CookieConsentProvider.tsx`) tracks one of three
states — no choice yet, granted, denied — in `localStorage`. `GoogleAnalytics`
(`site/components/analytics/GoogleAnalytics.tsx`) puts the Google tag in the document (init script,
then `gtag.js`, injected from an effect that reads `localStorage` itself rather than trusting the
first render's server snapshot) whenever `NEXT_PUBLIC_GA_MEASUREMENT_ID` is a GA4 ID and **no
rejection is on record**. Empty or malformed ID = no tag at all. What consent gates is storage,
through Google Consent Mode v2 (`site/lib/ga-consent.ts` builds the inline init script):

1. `gtag('consent','default',{ad_storage, ad_user_data, ad_personalization, analytics_storage:
   'denied', wait_for_update: 500})` is queued **before** `config`: the state before any choice.
2. A visitor whose stored choice is already `granted` gets `analytics_storage: 'granted'` queued
   right after, still before `config`, so their first hit of the visit is a full one.
3. `config` carries `allow_google_signals: false` and `allow_ad_personalization_signals: false`
   (the site runs no ads; same as the dashboard's tag).
4. Accept → `gtag('consent','update',{analytics_storage:'granted'})`; the ads purposes are never
   granted.

**Reject ends everything (operator decision, option B).** Cookieless pings exist only while the
visitor is undecided:

- A stored rejection loads no Google script and makes no Google request on later visits.
- A rejection made on the page, or "Cookie preferences" then Reject after accepting, sets Google's
  `ga-disable-<ID>` flag and updates consent to denied. gtag.js can still send a hit it had already
  queued while the visitor was granted (a trailing `user_engagement` hit with the `_ga` client id,
  a few seconds later, which the flag does not stop), so `installHitGuard` wraps `fetch` and
  `sendBeacon` before `gtag.js` loads and drops Google measurement requests while the flag is true.
- Reopening the banner without choosing does not bring the tag back on that page after a rejection;
  accepting does (and clears the flag).
- The page the visitor was on when they rejected may already have sent its first cookieless hit.

While undecided the tag writes and reads no cookie and uses no local or session storage; it sends
cookieless pings to Google (page URL and title, referrer, language, screen size, browser and OS
details, the request's IP address, and a client id that is random per page load and not stored).
Google uses them for modelled data. **Enhanced measurement stays on** (operator decision): while the
visitor is undecided the tag also sends the automatic interaction events (time on page, scrolls, outbound
clicks, site search, file downloads, video engagement, form start and submit), still cookieless,
and the privacy policy names them in Section 3. It is a property setting that `gtag`
configuration does not control; turning it off in GA4 would make the policy over-disclose, not
under-disclose, so update Section 3 if it is ever changed.

Proof: `e2e/tests/site-consent/ga-consent-mode.spec.ts` (real `gtag.js`, collection requests
answered locally) asserts the queue order, `gcs=G100` and no cookie while undecided, zero Google
requests after a stored or on-page Reject, zero requests after accept-then-withdraw (the trailing
hit), `gcs=G101` and `_ga*` after Accept, and the way back after a stored rejection.

The consequence: GA4 counts visits from undecided visitors cookielessly and models the rest, but
per-user measures cover accepters only, and rejecters are not counted at all. `trackEvent`
(`site/lib/gtag.ts`) is unchanged: custom events are sent only while the stored choice is
`granted`. The privacy policy (Sections 3, 4, 5, 6) and the banner describe the cookieless signal
and that Reject ends it; the pre-choice signal's stated basis is legitimate interest (Art. 6(1)(f)).

### Cookie and consent: what this site actually sets (H6 audit)

Audited from the source, not from the design's intention. **The items below exist (`rb_ft` is a cookie, consent-gated). Re-audited 2026-11-11 for the new-user funnel, which added `rf-visit-sent` and the two events `site.page_view` and `site.cta_click`, and again for the functional entries (the language cookie, the two dismissals, the app's sign-up entry):**

| What | Set by | When | Why |
|---|---|---|---|
| `localStorage['rf-cookie-consent']` (`'granted'` / `'denied'`) | `CookieConsentProvider`, key in `site/lib/consent.ts` | only when the visitor clicks Accept or Reject; removed again by the footer's "Cookie preferences" | it *is* the choice — first-party, no identifier, never sent anywhere, so it needs no consent of its own |
| `rb_ft` cookie (first-touch campaign labels `utm_source`, `utm_medium`, `utm_campaign`, `utm_content`, `utm_term`, referrer host, landing path, visitor id; 30 days, `Path=/`, `SameSite=Lax`, `Secure` on https) | `AttributionTracker`, codec in `site/lib/attribution.ts` | only while consent is `'granted'`, and only when no readable one exists (first touch wins; a cookie that cannot be read is replaced); deleted on reject/withdraw | attribute signups to a source and a tracked link; read by the API at signup, which clears it |
| `localStorage['rf-visitor-id']` (random UUID) | `AttributionTracker` | only while consent is `'granted'`. A fresh `rb_ft` always comes with a fresh id; a readable `rb_ft` names the id and this entry follows it; deleted on reject/withdraw | the anonymous id on the visitor events (`site.visit`, `site.page_view`, `site.cta_click`, and the app's `signup.started` and `signup.submitted`) |
| `sessionStorage['rf-visit-sent']` (the value `1`) | `AttributionTracker` | only while consent is `'granted'`, when the visit beacon is sent; deleted on reject/withdraw; gone when the tab closes | sends `site.visit` once per tab session; holds no identifier |
| `rb_locale` cookie (a language code; one year, `Path=/`, `SameSite=Lax`) | the footer language switcher (and the app's language menu) | only when the visitor picks a language | remembers the chosen language for the home-page redirect, the app, and emails sent while no language is saved on the account; functional, no identifier, so it needs no consent |
| `sessionStorage['reelbolt:promo-banner-dismissed']` (a short description of the offer: kind, end date, headline, discount) | `components/promo/PromoBanner.tsx` | only when the visitor closes the offer bar; gone when the tab closes | keeps that offer bar closed in this tab; functional, nothing about the visitor |
| `_ga`, `_ga_<container>` | Google Analytics, mounted by `GoogleAnalytics` | only while `analytics_storage` is granted, i.e. consent is `'granted'` (denied by default, so no cookie before a choice; no tag at all after Reject) | page-view measurement |
| — nothing else | — | — | `app/api/contact/route.ts` and `functions/api/contact.ts` set no cookie and keep no session; the rest of the site is static |

Three things are **not** storage and need no row: the landing held in memory until the page is
left (`lib/attribution-landing.ts`), the audience, campaign labels and landing path appended to
the signup link (they travel in the address), and the click count of a tracked link (a counter on
the server; no identifier, IP address or browser name is kept, and nothing is written to or read
from the device).

The dashboard's signup and sign-in pages share this origin, and two things they write belong in
this audit:

- **`rb_ft` as a session cookie.** With consent `'granted'`, no readable `rb_ft` and campaign
  labels on its own address, the signup page writes `rb_ft` as a **session** cookie holding only
  those labels and a new visitor id, so they survive the trip to Google or GitHub and back
  (`web/components/analytics/SignupTracker.tsx`). It does this when the page opens, never on
  submit; it never overwrites a readable cookie and writes nothing without consent.
- **`localStorage['rb-signup-intent:v1']`** (`web/lib/auth/signup-intent.ts`). Functional, not
  measurement, so it does not wait for the banner: the plan and the return path the person's link
  named, written only when they send the signup form or press a Google or GitHub button, read once
  after sign-in in the same browser, never sent anywhere. For an email sign-up it also holds a
  short code computed from the address (not the address). Removed when used; otherwise it lapses
  after three days (30 minutes for a provider sign-in).

The Privacy Policy's storage table (Section 5) lists `rb_ft`, `rf-visitor-id`, `rf-visit-sent`,
`rb_locale`, the two dismissals and `rb-signup-intent:v1`; keep that table and this one in step.
`site/lib/legal-disclosure.test.ts` pins the keys against the code. The table does not list the
signed-in app's own cookies (`reelbolt_token`, `reelbolt_user`) or its other local entries.

**No checkout script runs on `/site`, or on `/app` either — checked, not assumed.**
`grep -rniE "paddle|stripe" site/` prints nothing, no `cdn.paddle…`/`js.stripe…` appears anywhere
in the repository, and `/web`'s billing page does not host a checkout: it calls the API, gets a
`checkoutUrl`, and redirects the browser to the provider's own hosted page
(`web/app/(app)/settings/billing/page.tsx`). So **the banner needs no new cookie category**, and
H6's "flag it to B" resolves to *no action*: `/web` needs no consent banner, because the
payment provider's script never loads in either app. The only cookies on `/app` are the auth
cookies the Go API sets — `reelbolt_token` (httpOnly, nginx) and `reelbolt_user` (readable) —
which are strictly necessary for a session the visitor asked for by signing in, and are exempt on
that basis. Worth re-checking if a provider ever moves to an embedded checkout (Paddle.js /
Stripe.js `inline`), which is the one change that would put a third-party script on `/app`.

**What the gate still does not do.** A visitor who rejects *after* accepting keeps the `_ga`
cookies already written: they are not deleted, they expire on Google's schedule, and nothing
reads or refreshes them afterwards. The privacy policy says so. The banner's copy stays true:
nothing *new* is set after a rejection.

## No invented facts

Two categories of content went into this site, and they were held to different rules:

- **Product capabilities** (what ReelBolt does, its architecture, its workflow pipeline) were
  written from `CLAUDE.md` and the actual codebase — real, verifiable facts about a real system.
- **Business/legal facts** that only the business owner can supply — legal entity name, registered
  address, registration/VAT numbers, DPO contact, governing law, liability caps, retention
  periods, response-time SLAs — were never invented, guessed, or filled with a plausible-looking
  placeholder. Every one of them is a literal `[BRACKETED_TOKEN]` in
  `site/lib/legal-placeholders.ts`, imported everywhere it's needed (footer, contact page, privacy
  policy, terms of service) so there is exactly one place to fill in real values, and no
  page silently shipped with a fabricated address or a made-up company name that could be mistaken
  for real. See the launch checklist above for the full list of tokens to replace before launch.
- The same discipline extended to the visual redesign: the homepage strip
  (`site/components/home/UseCaseStrip.tsx`) lists the kinds of video a person can make today
  ("Highlight edits", "Narrated stories", …) as plain text, never captioned as "partners" or
  "customers" — no partner logo or customer name was invented. No fabricated social-media links
  were added either; the reference design's social-icon rail became real in-page section anchors.
  See `docs/site-design-system.md`.
- The copy rewrite kept the same rule. Every capability claim on the marketing pages maps to a
  real one in `CLAUDE.md` — automatic silence/bad-take removal, best-take selection, overlays and
  tracked screen inserts, colour grading, music and sound effects, the scoring review loop,
  bring-your-own provider, self-hosting — and the 2026-10 refresh added only what `docs/*.md`
  on `master` describe as shipped: text-to-speech narration (`docs/voiceover.md`, phase 1),
  planned b-roll under the budget gate (`docs/video-generation.md`, phases 1–2), object tracking
  and censoring (`docs/video-editing.md`), confidence-gated decision models plus the calibration
  view (`docs/decision-models.md`), the read-only studio timeline view (`docs/editor.md` — "no
  editor UI ships"), the platform assistant (`docs/platform-assistant-mcp.md`), semantic file
  search (`docs/embeddings.md`) and the public `/docs/` site (`docs/docs-site.md`). The homepage
  FAQ and `llms.txt` grew by the same facts and no others. What changed is the vocabulary, not the claim. No
  customer count, rating or time saving was asserted, and the structured data carries no
  `aggregateRating` for the same reason (see `site/lib/structured-data.ts`). Pricing is asserted
  now, but only from the one catalog the billing service also reads — every price on `/`,
  `/pricing` and in their `Offer` markup comes from `site/lib/pricing`, with no figure typed into
  a page, a component or a FAQ answer. The H8 (saas-launch) refresh added the same discipline to
  the new capabilities: team workspaces are described only as shipped (shared projects and run
  history, roles, invitations, a shared credit pool — `docs/multi-tenancy.md`, `docs/user-guide/
  workspaces.md`), bring-your-own provider carries its plan gate rather than an unconditional
  promise, and the desktop runner is described as *being built* — no download is claimed while
  `site/lib/runner-downloads` is empty, and the runner-only preference is described as failing
  rather than falling back to paid capacity, because that is the engine's actual behaviour
  (`docs/compute-fabric.md`). The compute fabric defaults to local and self-hosting is unchanged,
  so nothing on the site promises the cloud pool.
- The 2026-11 repositioning (for anyone with a story to tell) kept the rule and made it testable.
  Social proof is a registry of real, signed-off assets that renders nothing while it is empty
  (see "Proof registry"); the only proof on the site today is the trust strip, six facts with a
  fixed meaning (no card; the first-video credits, with their end date and the cases where they
  are not given; every edited cut scored before you see it, while subtitles, thumbnails and post
  text are made in one pass; credits refunded when a step fails on our side; minutes for a small
  job, sometimes half an hour for a big one; public alpha). The words are in
  `messages/<locale>/proof.json`. Before/after is qualitative: no number appears unless it is a measured
  fact of a signed-off sample. The first-person line on each audience card is that audience's
  situation, printed without quotation marks or a name, because it is not a customer quote. Each
  audience page lists only shipped capabilities with their plan gate. `lib/site-copy.test.ts` fails on a typed credit amount, resolution or plan name,
  on an invented multiplier, percentage, count or rating, and on the forbidden stronger claims.
