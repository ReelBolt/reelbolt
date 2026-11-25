# Site Design System

The public marketing site (`/site`) went through a 5-phase visual redesign to a bold, technical
"schematic" aesthetic — purple/white/near-black, two display/mono webfonts, decorative hairline
primitives, and a live launch film on the homepage hero — the product's own output, with a single
genuine WebGL viewfinder scene standing in when no film is configured. A 6th pass then inverted the
neutral ramp to a dark palette (near-black page, off-white text) — same structure, same accent
purple, same primitives, just recolored; see "The dark-palette pass" below for exactly what did and
didn't change. This doc is the reference for that system: the exact tokens, why they were chosen,
and the rules that keep the site's accessibility/performance invariants (see `docs/marketing-site.md`
and `docs/checklist-traceability.md`) intact as it evolves. It does not repeat the
SEO/legal/consent material those two docs already own.

## Palette

All tokens live in `site/app/globals.css`'s `@theme` block. There is no other place a color is
defined for use in Tailwind utility classes — a `bg-accent` or `text-ink-muted` class always
resolves back to one of these. Token *names* still follow the original light-theme metaphor
(`paper` = background, `ink` = foreground text) even though the palette is now dark — the role each
token plays didn't change, only which end of the lightness scale it sits at.

| Token | Hex | Role |
|---|---|---|
| `--color-paper` | `#0B0B12` | Page background, near-black (not pure black) |
| `--color-paper-2` | `#15131D` | Secondary/alternating section background |
| `--color-paper-3` | `#1D1927` | Tertiary surface (subtle recess) |
| `--color-ink` | `#F5F3F8` | Primary text, off-white (not pure white) |
| `--color-ink-2` | `#C7C2D3` | Secondary heading/emphasis text |
| `--color-ink-muted` | `#A79FB4` | Body copy on paper backgrounds |
| `--color-ink-faint` | `#8B8699` | De-emphasized text / decorative glyph color (lifted from `#6F6A7D` to clear 4.5:1 for small text) |
| `--color-line` | `#221E2C` | Default hairline border |
| `--color-line-strong` | `#6F6A7D` | Emphasized hairline (table rules, dividers that need to read) |
| `--color-accent` | `#A855F7` | Primary purple accent (unchanged) |
| `--color-accent-hover` | `#C084FC` | Hover state for accent surfaces |
| `--color-accent-strong` | `#7C3AED` | Accent on higher-contrast surfaces (buttons, focus rings; unchanged) |
| `--color-accent-ink` | `#C4B5FD` | Accent used as text color on paper (dark bg) |
| `--color-accent-deep` | `#2E1065` | Darkest accent tint, used as a panel background (unchanged) |
| `--color-accent-tint` | `#221A38` | Darkest accent tint, used as a subtle fill |
| `--color-danger` | `#F87171` | Error text |
| `--color-danger-border` | `#EF4444` | Error input border |
| `--color-danger-tint` | `#2A1215` | Error banner/background fill |

Phase 3 of the original redesign removed the old `--color-brand-*` shim entirely — there is no
legacy alias left pointing at these values under a different name. Confirm this before adding a new
token: `grep -n "color-brand" site/app/globals.css` should return nothing.

The ReelBolt logo (the R-with-bolt mark, its outlined Chakra Petch wordmark and the lockups) uses
only the violet tokens in this table — `accent-deep`, `accent-ink`, `accent` and `ink` — and adds
no colour of its own. Its masters, clear-space and minimum-size rules, and the sync script that
copies them into `/site`, `/web` and `/docs-site` are documented in [`docs/brand.md`](brand.md).

### Why these exact hex values — measured contrast

Every text/background and interactive-border pairing actually used in the UI was checked against
WCAG 2 contrast requirements before being adopted. These are the pairings that matter; re-run a
contrast check before changing any of the hexes above, since a "close enough" purple can silently
drop a pairing below its required ratio.

| Foreground | Background | Ratio | Requirement met |
|---|---|---|---|
| `#0B0B12` (paper) | `#A855F7` (accent) | 4.96:1 | AA, normal text (button text on `bg-accent`; `text-ink` measured 3.59:1 and was replaced) |
| `#0B0B12` (paper) | `#C084FC` (accent-hover) | 7.42:1 | AAA, normal text (button text on `bg-accent-hover`) |
| `#FFFFFF` | `#7C3AED` (accent-strong) | 5.70:1 | AA, normal text |
| `#C4B5FD` (accent-ink) | `#0B0B12` (paper) | 10.62:1 | AAA, normal text |
| `#F5F3F8` (ink) | `#0B0B12` (paper) | 17.80:1 | AAA, normal text |
| `#0B0B12` (paper) | `#F5F3F8` (ink) | 17.80:1 | AAA, normal text (dark button text on `bg-ink`; replaces `text-white`, which measured 1.10:1) |
| `#A79FB4` (ink-muted) | `#0B0B12` / `#15131D` | 7.71 / 7.24:1 | AAA, normal text |
| `#8B8699` (ink-faint) | `#0B0B12` / `#15131D` / `#1D1927` | 5.57 / 5.22 / 4.89:1 | AA, normal text on every paper surface — the small-print spots (footer copyright, legal-page footnote, ContactForm "(optional)" labels) and the decorative glyph colour all clear 4.5:1 |
| `#6F6A7D` (line-strong) | `#0B0B12` / `#15131D` | 3.77 / 3.53:1 | ≥3:1 only — used for interactive control **borders** (WCAG 1.4.11 non-text contrast); not used for text |
| `#F87171` (danger) | `#0B0B12` / `#2A1215` (danger-tint) | 7.09 / 6.35:1 | AAA, normal text |
| `#FFFFFF` | `#2E1065` (accent-deep) | 15.24:1 | AAA, normal text |

`ink-faint` clears 4.5:1 on all three paper surfaces, so it is valid for normal-size text. The old
`#6F6A7D` measured 3.30–3.77:1 and failed AA for text. Before using `ink-faint` on a new background
(or changing its hex), re-run the contrast check against whatever surface it lands on; any value
below 4.5:1 fails normal-text AA.

### The dark-palette pass

The 6th redesign pass flipped the site from a white-paper/near-black-ink palette to a near-black
paper/off-white-ink one, keeping the accent purple family, the type system, the decorative
primitives, and every layout untouched. It was **not** a blind swap of the two hex values, because
several tokens are used for more than one role:

- **`ink` is also a background color**, not just the default text color — `Panel`'s `tone="ink"`
  and `Section`'s `tone="ink"` (used for the homepage/features closing CTA bands and the
  hero's film/viewfinder panel) render `bg-ink`. Those call sites previously hardcoded
  `text-white`/`border-white` for the text and corner-bracket color sitting on that background,
  because `ink` was dark. Now that `ink` is the light color, that on-`ink` content was repointed to
  `text-paper`/`border-paper` (`paper` — dark — is exactly the color those spots need). See
  `components/ui/Panel.tsx`, `components/ui/CornerBrackets.tsx` (new `paper` tone),
  `components/layout/Section.tsx`, `components/home/Hero.tsx`, `app/page.tsx`, and
  `app/features/page.tsx`.
- **`accent-strong` (#7C3AED) cannot serve as both a button/icon background *and* body text on the
  new dark `paper`** — it clears AA (5.70:1) as a background with white text, but as *text* on
  `#0B0B12` it only reaches 3.44:1, short of the 4.5:1 normal-text requirement, and that pairing
  (`text-accent-strong` on `bg-paper`/`bg-paper-2`) was the site's primary "accent-colored link/
  heading/icon" treatment across nearly every page. Rather than compromise the button-background
  role, every former `text-accent-strong` call site was repointed to `text-accent-ink` (10.62:1 on
  paper), which is exactly what that token's role — "accent used as text color on paper" — already
  meant; only the axis of "paper" flipped. Former `hover:text-accent-ink` pairs became
  `hover:text-accent` (4.96:1), so hovering still visibly shifts the color without dropping below
  AA. `accent-strong` itself keeps its original hex and is now used **only** for backgrounds/fills/
  focus rings/icon fills, never body text.
- **`accent-tint`** (the "subtle fill" role) flipped from a near-white lavender to a near-black
  lavender (`#221A38`) rather than just inverting lightness on the same hue at the same distance
  from white — it was tuned so `accent`/`accent-ink` text sitting on it (e.g. the numbered step
  badges on the homepage) still clears AA.
- **`danger`/`danger-border`/`danger-tint`** all moved to brighter/darker-on-dark equivalents
  (`#F87171`/`#EF4444`/`#2A1215`) rather than reusing the light-theme reds verbatim, since the
  original dark reds (`#B91C1C`/`#DC2626`) don't clear 4.5:1 against a near-black background.
- **`app/manifest.ts`'s `background_color`** was updated from `#ffffff` to `#0B0B12` to match (PWA
  splash-screen background); `theme_color` (`#7C3AED`, the browser-chrome color) was left unchanged
  — it was never a page-background value.
- The three.js viewfinder scene colors (`components/three/ViewfinderScene.tsx`) and the OG/Twitter
  card gradient (`app/opengraph-image.tsx`) were **not** changed — both were already
  dark-purple-on-transparent or dark-purple-gradient and read correctly against the new dark page
  background without modification.

Re-verify the rename held with:

```bash
grep -rn "text-accent-strong" site/app site/components --include='*.tsx'
```

which should return nothing — every accent-as-text usage goes through `accent-ink` now.

## Typography

Two Google Fonts, loaded via `next/font/google` in `site/app/layout.tsx`:

- **`--font-display`** (Chakra Petch) — headings, CTAs, anything meant to read as bold/technical
  signage. Headings and card titles are set in **sentence case** with default tracking; `uppercase`
  and tight tracking are reserved for small functional labels (buttons, nav, table column heads),
  never headings.
- **`--font-mono`** (JetBrains Mono) — nav items, inputs, numbers, code, captions, small meta labels
  — the "readout" register of the design. **Paragraph copy is never mono**: hero and section
  intros, CTA bodies and plan credit lines use `font-sans`.
- **`--font-sans`** — the original zero-download system font stack, kept (not replaced) for
  long-form prose: legal pages and feature/about body copy. Long paragraphs set in a display or
  mono face hurt readability at body-copy sizes and line lengths; system-sans is the more legible
  choice for a multi-paragraph privacy policy or terms page. This is a one-line-reversible decision
  (swap the `font-sans` utility for `font-mono`/`font-display` on those pages) if the site owner
  later wants the technical faces used everywhere, including prose.

## Decorative primitive catalogue

All in `site/components/ui/`:

| Primitive | Purpose |
|---|---|
| `GridOverlay` | Absolute-fill hairline column/row grid — the schematic backdrop behind hero/section content. |
| `CornerBrackets` | Four L-shaped corner brackets around a panel, viewfinder/technical-readout styling. |
| `Eyebrow` | Small mono label with a leading accent dot. Use sparingly (about eight on the whole site: hero tagline, blog tags, the pricing offer); never above every heading. |
| `Button` | The site's one button/link component — every CTA, everywhere, goes through this. |
| `Panel` | Content container, borderless `paper-2` fill by default (`bordered` opts in), optional tone and corner brackets. At most one bordered card per view. |

### The aria-hidden / non-interactive contract

`GridOverlay` and `CornerBrackets` are **purely decorative** —
each renders `aria-hidden="true"` on its root element, carries no `tabIndex`, no `onClick`, and no
interactive ARIA role, and this must never regress. A screen reader and a keyboard-only user should
be able to ignore both completely; a design change that adds a click handler or a real link
inside one of these components breaks that contract and must move the interactive affordance out
to a real, focusable element instead (usually a `Button` or a plain `<a>`).

`Eyebrow` is the one partial exception worth calling out: the label *text* is real, meaningful
content (not `aria-hidden`) — only its small leading accent square is a decorative
`aria-hidden="true"` dot. `Button` is intentionally interactive (it's the CTA primitive, not
decoration). `Panel` is a plain layout container with no ARIA semantics of its own; it only
becomes non-interactive-by-construction because nothing inside it is added except through other,
already-compliant primitives.

Re-verify the contract with `grep -rn "tabIndex\|onClick" site/components/ui/GridOverlay.tsx site/components/ui/CornerBrackets.tsx` — it should return nothing.

## The hero — a live film, not a fabricated render

The homepage hero is dominated by the **live launch film** — ReelBolt's own product output, shown
as what it is: no fabricated product render, no mascot, no stock asset passed off as bespoke.
`site/components/home/Hero.tsx` is a server component; it calls `launchFilm(process.env)`
(`site/lib/launch-film.ts`) at build time, and when a film is configured it renders
`site/components/home/LaunchFilm.tsx` in a `lg:col-span-8 lg:col-start-5` column — eight of twelve
columns on `lg`, bled to the viewport edge below `lg` by `-mx-4 sm:-mx-6 lg:mx-0`. The film
autoplays muted once an `IntersectionObserver` reports it at least 25% visible
(`FILM_AUTOPLAY_THRESHOLD` and `shouldAutoplay` in `site/lib/film-playback.ts`), pauses when it
leaves the viewport, never restarts a pause the visitor chose, and never plays sound on any path,
including the play button. It preloads `metadata` only; until playback starts the panel shows the
poster (when one is configured) and a play button carrying the film's title.
`NEXT_PUBLIC_LAUNCH_FILM_BASE=''` switches the film off entirely, which is what selects the
fallback below.

When no film is configured, the same eight-column slot renders a `Panel tone="ink"` around
`ViewfinderMount`: a wireframe/solid octahedron pair in the accent purple. That is **genuine,
live-rendered WebGL geometry** — abstract generative geometry rendered honestly as what it is, not
a fabricated product image — and it is the site's only remaining 3D scene. Both halves of the hero
obey the same rule, which mirrors the site's broader "no invented facts" discipline (see
`docs/marketing-site.md`) applied to visuals rather than copy: the film is real product output, and
the viewfinder is real geometry.

The extruded-bolt hero scene a previous pass mounted here — an `ExtrudeGeometry` copy of the brand
mark's bolt, inside a wireframe cage with an orbit ring — was deleted (PR #435). It is not part of
the hero any more; do not reintroduce it or describe it as current.

## SSR isolation rule

`three` and `@react-three/fiber` are imported **only** in one file:

- `site/components/three/ViewfinderScene.tsx`

Every other file reaches the scene only through its mount wrapper,
`site/components/three/ViewfinderMount.tsx`, which loads the real scene component via
`next/dynamic(() => import('./ViewfinderScene'), { ssr: false })`. This is required, not stylistic:
Next.js prerenders/statically generates this site's pages on the server, and WebGL has no
server-side implementation — a top-level `import * as THREE from 'three'` reached during SSR either
no-ops uselessly or throws, depending on what it touches (`document`/`window`/canvas context).
`next/dynamic(..., { ssr: false })` is also only legal inside a Client Component, which is why the
mount file is `'use client'` separately from the scene file and from `site/components/home/Hero.tsx`,
the server component that renders the mount.

Verify the isolation holds with:

```bash
grep -rln "from 'three'\|from '@react-three/fiber'" site/app site/components site/lib
```

This should list exactly `site/components/three/ViewfinderScene.tsx` and nothing else.

## Reduced-motion rule

`site/lib/use-reduced-motion.ts` exports `useReducedMotion()`, which **defaults to `true`** (assume
reduced motion) until a `matchMedia('(prefers-reduced-motion: reduce)')` check proves otherwise on
mount. Every motion-bearing piece of the site is built on top of this one hook, and all of them
must keep failing safe toward "do nothing that moves":

- **`ViewfinderMount`** (`site/components/three/`) — when `reduced` is true, the component returns
  its static fallback (`ViewfinderStatic`, a static SVG) **without ever calling `next/dynamic`'s
  import** — the three.js/`@react-three/fiber` chunk is not requested at all, not even prefetched,
  for a reduced-motion visitor.
  The same mount also applies a device gate from `site/lib/scene-gate.ts` (`shouldMountScene`): narrow
  (<1024px), low-core (<4 hardware threads, `navigator.hardwareConcurrency`) and Save-Data
  (`navigator.connection.saveData`) clients get the same static fallback and never request the chunk.
  An eligible client mounts the scene only after the visitor's **first interaction**:
  `whenFirstInteraction` in `site/lib/scene-gate.ts` waits for the first `pointermove`,
  `pointerdown`, `keydown`, `touchstart`, `wheel` or `scroll` on `window` (passive listeners, each
  fired once, all removed on cleanup). Until then `ViewfinderStatic` is what renders, and it is the
  LCP. So on capable desktops the scene loads after the visitor first interacts, not on idle; the
  scene's parse/compile cost never lands on first paint or on a visitor who never touches the page.
  Narrow, low-core, Save-Data and reduced-motion clients never reach this listener at all: the device
  gate and the reduced-motion check run first and short-circuit it, so they never request the chunk.
- **`LaunchFilm`** (`site/components/home/LaunchFilm.tsx`, a client component) — its
  `IntersectionObserver` effect returns immediately when `reduced` is true, so the film never
  autoplays for a reduced-motion visitor. The poster and the play button still render; a visitor who
  chooses to start the film with the play control can, with sound still off. The pure predicate it
  consults, `shouldAutoplay` in `site/lib/film-playback.ts`, likewise treats `reduced` as an absolute
  opt-out.
- **`Reveal`** (`site/components/motion/Reveal.tsx`, a server component) — does not use
  `useReducedMotion` at all. It renders `<Tag class="reveal">`, and the entrance animation exists only
  inside `@media (prefers-reduced-motion: no-preference)` and `@supports (animation-timeline: view())`
  in `site/app/globals.css`. Reduced-motion visitors and browsers without `animation-timeline` get
  the content fully visible with no animation: `.reveal` sets no opacity outside that block, so
  nothing can be stuck hidden. The `delay` prop is accepted and ignored.
  The keyframes animate `transform` only (`translateY(1rem)` to `none`), never `opacity`: an
  opacity fade left text mid-animation at low contrast, which axe flags. The element is always
  fully opaque; only its position eases in.
- **`useScrollParallax`** (`site/lib/use-scroll-parallax.ts`) — when `reduced` is true, the effect
  returns immediately; no scroll/resize listeners are attached and the element's transform is never
  touched.

Re-confirm this hasn't regressed by reading all five files together
(`lib/use-reduced-motion.ts`, `components/three/ViewfinderMount.tsx`,
`components/home/LaunchFilm.tsx`, `components/motion/Reveal.tsx`,
`lib/use-scroll-parallax.ts`) — the three hook users branch on `useReducedMotion`, `Reveal` branches
in CSS on the same preference via its media query, and each must choose "render the static/no-op
path, don't just animate faster" as its reduced-motion behavior.

## Bundle code-split contract

- Only `ViewfinderScene.tsx` may import `three` or `@react-three/fiber` (see "SSR isolation rule"
  above — the same rule doubles as the bundle boundary).
- Only `ViewfinderMount.tsx` may import that scene file, and only via
  `next/dynamic(..., { ssr: false })` — never a static `import` of `ViewfinderScene` from anywhere
  else, which would pull the three.js chunk back into a shared/eagerly-loaded bundle.
- `site/components/home/Hero.tsx` (the only page that renders the mount) imports the *mount*
  component and the film (`components/home/LaunchFilm.tsx`), never the scene component directly.
- `Hero.tsx` is a **server component** (no `'use client'`). Its scroll parallax on the grid lives in
  the client leaf `site/components/home/HeroGridParallax.tsx`, which wraps the grid in the
  `absolute inset-0` div that carries `useScrollParallax`. Keep new client-only code in leaves like
  this one, not in `Hero.tsx`, so the hero's markup stays in the server bundle.
- The mount also gates on the device check in `site/lib/scene-gate.ts` (see "Reduced-motion rule").
  Narrow, low-core and Save-Data clients never reach `next/dynamic`'s import, so the three.js chunk
  is not requested for them either. The gate is a pure function (`shouldMountScene`) with node tests
  in `site/lib/scene-gate.test.ts`; do not inline a second copy of the threshold in a component.
- Eligible clients then wait for `whenFirstInteraction` (see "Reduced-motion rule") before the
  mount sets its state and renders the `next/dynamic` scene, so the chunk is requested only after
  the visitor's first interaction on a capable desktop.
- `Hero.tsx` only renders `ViewfinderMount` when `launchFilm()` returned no film. With a film
  configured the mount is never rendered, so the three.js chunk is not requested from the homepage
  at all: the chunk is requested only when the viewfinder actually renders — no film configured, a
  client that passes the device gate and the reduced-motion check, and after first interaction.

Net effect, confirmed at every phase of the redesign: the three.js/`@react-three/fiber` code
(~130KB+ gzipped) ships in its own lazy chunk and is requested only when the hero renders the
viewfinder on a client that does *not* have reduced motion set, passes the device gate (wide
≥1024px, ≥4 cores, not Save-Data — see "Reduced-motion rule"), and the visitor has interacted with
the page (`whenFirstInteraction`); with the default configuration a film is configured, so the
homepage never requests it. Every non-homepage route's First Load JS stays within about 1KB of the
pre-redesign baseline. Last recorded `npm run build` route table (measured before the hero became a
film; not re-measured since): homepage First Load JS is 113KB, every other route is 103–108KB, and
the three.js chunk does not appear in any route's First Load JS figure at all (it's a route-level
lazy chunk fetched at runtime, not part of the initial bundle Next.js's build output attributes to
a route). The reduced-motion half of that contract was re-checked by loading the production build
in headless Chromium twice, with and without `--force-prefers-reduced-motion`: the reduced run
requested 10 of the 16 chunk URLs the normal run requested and none that the normal run did not,
and the three.js chunk is among the 6 it never asks for.

## The `@react-three/fiber@9.8.1` pin

`package.json` pins `@react-three/fiber` to exactly `9.8.1` — no caret — because its peer
dependency on React is bounded (`react: ">=19 <19.4"`) while this project's declared React is
`^19.3.1`, which resolves to `19.3.1` in the lockfile: the range still admits that version today,
but an unpinned bump would let a future React 19.4+ resolution break `npm ci` (used by the
production Dockerfile's `deps` stage, and by this phase's full regression rebuild). `9.8.1`
declares `react: ">=19 <19.4"`, which resolves cleanly against React 19.3.1. Do not bump
`@react-three/fiber` past `9.8.1` without first confirming its peer-dependency range against
whatever React version is installed at the time.

`three` itself is declared with a caret (`^0.186.0`) and currently resolves to `0.186.0` — it has
no equivalent peer-dependency conflict with React, so it wasn't pinned exactly.

No `@react-three/drei` and no `framer-motion` were added anywhere in the redesign: the viewfinder
scene is built from three.js core geometries/materials only. Scroll-driven motion is hand-rolled, not a
motion library: `Reveal` is pure CSS (a `.reveal` class driven by `animation-timeline: view()` in
`globals.css`, no JS and no `IntersectionObserver`), and `useScrollParallax` is a small
`IntersectionObserver`/scroll-listener implementation (~1KB).

## No invented facts, applied to visuals

- The reference design's "partner logos" strip became `site/components/home/UseCaseStrip.tsx`, a
  "Made for" strip of the video jobs ReelBolt produces ("Product launches", "Feature demos",
  "Sales outreach", …) rendered as plain text — never as logos, and never captioned as "partners"
  or "customers". It previously listed stack technologies under a "Runs on" heading; that was
  accurate but spoke to engineers rather than buyers, so the slot now carries use cases (which
  also earn their keep as keyword coverage). The no-invented-logos rule is unchanged.
- The reference design's social-icon rail, the hero chrome widgets, registration marks, `01/02/03`
  step badges and "Part n of 6" numbering were removed (AI-slop audit, 2026-11): they were
  ornament with no information. Do not reintroduce them. Sections are separated by alternating
  `paper`/`paper-2` backgrounds, not by `border-b` dividers.

## How to extend this system

- Reuse `Reveal`, `GridOverlay`, `Eyebrow`, `Button`, and `Panel` for new sections rather than
  hand-rolling new decorative markup or a new button/card implementation. If a new decorative shape
  doesn't fit the existing primitives, add a new one to `site/components/ui/` following the same
  `aria-hidden`/non-interactive contract described above, rather than inlining `aria-hidden` divs
  ad hoc across pages.
- New hardcoded hex colors in `.tsx` files are not allowed outside `opengraph-image.tsx`,
  `twitter-image.tsx`, and `components/three/*` — those three exceptions exist because `next/og`'s
  `ImageResponse` and three.js material colors aren't CSS and can't reference the `@theme` tokens.
  Everywhere else, use the Tailwind utility classes backed by the tokens in the Palette section
  above (`bg-accent`, `text-ink-muted`, etc.), so a future palette change stays a one-file edit.
  Verify with:
  ```bash
  grep -rln "#[0-9a-fA-F]\{6\}" site/app site/components --include='*.tsx' \
    | grep -v opengraph-image | grep -v twitter-image | grep -v components/three
  ```
  which should return nothing.
- Before changing any palette hex, re-run a contrast check for every pairing in the "Why these
  exact hex values" table above — a value swapped for a "close enough" purple can silently drop a
  pairing below the WCAG ratio it currently clears.
