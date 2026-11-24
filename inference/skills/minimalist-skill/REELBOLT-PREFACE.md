## ReelBolt preface to `minimalist-skill` (written by ReelBolt, NOT part of the upstream skill)

Everything below the divider is the upstream "minimalist-ui" skill (Leonxlnx/taste-skill, MIT)
exactly as published. It is a style recipe for web interfaces. Your output here is a Remotion
composition (or the plan, direction or overlay for one) rendered to video frames. Load this only
when the customer's brief asks for a minimalist, calm, editorial or document-style look; for any
other brief the core `taste-skill` is enough. When this look IS requested, its type and palette
choices take precedence over the core skill's defaults where the two differ. This preface is
ReelBolt's reading of which parts transfer to video. Section numbers refer to the upstream text.

Your step's own instructions, output schema, tool rules and format, length and legibility rules
always win over this skill, and so do the customer's brand colours and fonts.

### Use these parts

- **2 (banned elements), the parts about content:** no emojis, no generic placeholder names, no AI
  copywriting cliches. Your own instructions already forbid inventing people, names, testimonials,
  ratings, prices or metrics, so use only what the project files, the analysis and the brief give
  you. Also: no neon, no gradients, no large primary-colour fields.
- **3 (typography):** the contrast between a clean sans for body text and an editorial serif for
  headings, tight tracking and line-height on headings, a monospace for small metadata, off-black
  instead of pure black, muted grey for secondary text. Fonts are local: the sandbox has no
  internet, so set CSS `fontFamily` to an installed family (Inter, Montserrat, Poppins, DM Sans, Space Grotesk, Chakra Petch, Bebas Neue, Anton, Playfair
  Display, DM Serif Display, JetBrains Mono, Roboto, Open Sans, Oswald, Raleway, Source Sans 3,
  Nunito, Work Sans, Rubik, Barlow, Barlow Condensed, Archivo, Manrope, Outfit, Plus Jakarta
  Sans, IBM Plex Sans, Lora, Libre Baskerville, Cormorant Garamond, EB Garamond, IBM Plex Mono,
  Fira Code, Roboto Mono, Space Mono, Abril Fatface, Lobster, Pacifico, Caveat, Dancing Script,
  Archivo Black, Alfa Slab One, Liberation Sans, Liberation Serif, Liberation Mono) and never use
  `@remotion/google-fonts`, a font URL or `next/font`. Inter or DM Sans carry the body text,
  Playfair Display or DM Serif Display the headings and JetBrains Mono the metadata.
- **4 (colour):** the warm monochrome canvas, near-black text, and the desaturated pastels used
  sparingly for accents. Colour is scarce and carries meaning.
- **7 (motion), as a principle:** quiet, short fade-and-rise entrances, staggered rather than all at
  once, nothing flashy. Build it with `useCurrentFrame()`, `interpolate()` and an easing from the
  `remotion-markup` skill (where you have it), not with CSS transitions or `IntersectionObserver`.
  The easing curve and the roughly 600 ms duration are usable as given.

### Skip these parts (web only)

- **5:** bento cards, buttons, tags, accordions, `<kbd>` keys and faux-OS window chrome.
- **6:** the icon-library list, the Picsum placeholder URLs and the page-background layers. Never
  hotlink or fetch external images; use the assets the project and the workflow give you.
- **8:** the web execution protocol (Tailwind classes, `max-w-*`, scroll-entry on every block).
- The hover, scroll and shadow details in 7.
