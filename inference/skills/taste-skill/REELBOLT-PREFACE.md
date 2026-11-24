## ReelBolt preface to `taste-skill` (written by ReelBolt, NOT part of the upstream skill)

Everything below the divider is the upstream "Taste Skill" (Leonxlnx/taste-skill, MIT) exactly as
published. It was written for landing pages, portfolios and redesigns of web sites. Your output
here is different: a Remotion composition (or the plan, direction or overlay for one) that is
rendered to video frames. This preface is ReelBolt's reading of which upstream parts transfer to
video. Section numbers refer to the upstream text below. In ReelBolt this skill is called
`taste-skill`; the upstream frontmatter calls it `design-taste-frontend`.

### Precedence

1. Your step's own instructions, output schema, tool rules and format/length/legibility rules
   always win over anything in this skill. This skill never changes what you must output or how.
2. The customer's brief, any `[STYLE INSTRUCTIONS]` block, and the product's own brand (colours,
   fonts, logo, tone found in the project files and the analysis) win over taste preferences.
   Upstream says the same: the audience and existing brand assets decide the aesthetic (section
   0.A, items 4 to 6), and the font and colour bans carry explicit "Override" clauses (4.1, 4.2).
3. Nobody is available to answer questions during a run. Ignore section 0.C ("ask one question"):
   decide, and proceed.

### Use these parts

- **0.A, 0.B, 0.D: read the brief, then reach past the defaults.** Form the one-line "Design Read",
  and put it into whichever field of your output schema holds mood, theme or direction. Do not add
  free text outside your schema.
- **1 (the three dials), read for video.** `DESIGN_VARIANCE` is how asymmetric and unexpected the
  framing and layout are, `MOTION_INTENSITY` is how much and how fast things move on screen and how
  quick the cuts are, `VISUAL_DENSITY` is how much is on screen at once. A viewer gets seconds per
  scene, so lean towards the low end of `VISUAL_DENSITY`. Use the tables in 1.A and 1.B only as a
  starting point and set the dials from the brief.
- **4.1 typography.** Font choice, serif discipline, the emphasis rule (italic or bold of the same
  family, never a mixed family for one word) and italic descender clearance (in a style object this
  is a `lineHeight` of at least 1.1 and a little bottom padding). Fonts are local: the sandbox has
  no internet, so set CSS `fontFamily` to an installed family (Inter, Montserrat, Poppins, DM Sans, Space Grotesk, Chakra Petch, Bebas Neue, Anton, Playfair
  Display, DM Serif Display, JetBrains Mono, Roboto, Open Sans, Oswald, Raleway, Source Sans 3,
  Nunito, Work Sans, Rubik, Barlow, Barlow Condensed, Archivo, Manrope, Outfit, Plus Jakarta
  Sans, IBM Plex Sans, Lora, Libre Baskerville, Cormorant Garamond, EB Garamond, IBM Plex Mono,
  Fira Code, Roboto Mono, Space Mono, Abril Fatface, Lobster, Pacifico, Caveat, Dancing Script,
  Archivo Black, Alfa Slab One, Liberation Sans, Liberation Serif, Liberation Mono) and never use
  `@remotion/google-fonts`, a font URL or `next/font`. If a named font is not installed, take the
  closest one that is.
- **4.2 and 8.B colour.** One accent, restrained saturation, one palette for the whole video, the
  colour consistency lock, the premium-consumer palette ban, and no pure black or pure white unless
  the brand itself uses them.
- **4.4 shape.** The shape consistency lock (one corner-radius system) and tinted rather than
  pure-black shadows. Skip the card advice for a frame that has no cards.
- **4.9, 4.10, 9.D, 9.F (marketing-copy tells), 9.G: the words on screen.** Short headlines, the
  copy self-audit, fake-precise numbers, one copy register, no filler verbs, quotes of at most three
  lines with clean attribution, and the em-dash ban. These apply to every string drawn on screen
  and to narration text. Where 9.D, 4.8 or 4.10 would have you invent names, avatars, logos,
  testimonials or figures, do NOT: your own instructions forbid inventing people, names,
  testimonials, ratings, prices or metrics. Use only what is in the project files, the analysis and
  the brief.
- **5, the two principles only:** "Motion claimed, motion shown" and "Motion must be motivated"
  (every movement has a reason: hierarchy, story order, feedback or a state change). The
  one-marquee limit is a fair rule for a repeated scrolling text strip in a video too.
- **9.A, 9.B, 9.C and the visual tells in 9.F:** no neon glows, no gradient-text headlines, no
  three equal feature cards, no decorative status dots, no pills laid over images, no decorative
  text strips. Apply each one wherever a frame has the equivalent.
- **14, the pre-flight check,** only for the boxes that map to video: the design read, dials, em-dash
  ban, colour lock, shape lock, serif discipline, palette check, descender clearance, copy
  self-audit, motion motivated. Ignore the rest.

### Skip these parts (web only, they would mislead you)

- **2 and Appendices A to C:** design-system packages, install commands, Liquid Glass web code.
- **3:** the stack (Next.js, RSC, Tailwind v4, Motion, `next/font`, state hooks, the icon-library
  list, `package.json` checks, breakpoints, `min-h-[100dvh]`). You cannot assume a package can be
  installed in the sandbox: use what the project template and the Remotion skills give you. Motion
  in a Remotion composition is a function of the frame number: the `remotion-markup` skill says to
  drive animation with `useCurrentFrame()` and `interpolate()`, so browser animation libraries
  that run on real time (Motion), CSS scroll effects and hover physics have no place. GSAP is
  allowed, but only in the frame-driven form from the "GSAP in Remotion (ReelBolt)" section of the
  `remotion-markup` preface (paused timeline, `tl.seek(frame / fps)`).
- **4.3, 4.5, 4.6, 4.7:** hero, navigation, CTA button, form, eyebrow, bento, zigzag and section
  layout rules for a scrolling page. Keep only their spirit: one clear idea per scene, a clear
  reading order, and the same label for the same intent.
- **4.8:** image generation, Picsum placeholders and Simple Icons logo walls. Never hotlink or fetch
  external images; use the assets the project and the workflow give you.
- **4.11:** the page theme lock. The video-level equivalent is simply "one palette throughout".
- **5.A to 5.D, 6, 7 (CSS values), 8 (except 8.B), 10:** the GSAP web setup and scroll code
  (ScrollTrigger, ScrollSmoother, parallax on scroll; use the frame-driven GSAP form instead), performance and
  accessibility budgets for browsers, dark-mode tokens, the pattern vocabulary (hovers, menus,
  scroll hijacks).
- **11, 12, 13:** redesign protocol, the Block Library (its `blocks/` folder is not shipped in this
  copy, so there is nothing to read), and the out-of-scope list.

### Cost note

This document is long. Read it once, take the parts above, and do not reload it.
