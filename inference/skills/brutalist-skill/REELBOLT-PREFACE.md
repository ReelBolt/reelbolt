## ReelBolt preface to `brutalist-skill` (written by ReelBolt, NOT part of the upstream skill)

Everything below the divider is the upstream "industrial-brutalist-ui" skill (Leonxlnx/taste-skill,
MIT, marked Beta upstream) exactly as published. It is a style recipe for web interfaces. Your
output here is a Remotion composition (or the plan, direction or overlay for one) rendered to video
frames. Load this only when the customer's brief asks for a brutalist, Swiss, industrial,
blueprint or terminal look; for any other brief the core `taste-skill` is enough. When this look
IS requested, its type and palette choices take precedence over the core skill's defaults where the
two differ. This preface is ReelBolt's reading of which parts transfer to video. Section numbers
refer to the upstream text.

Your step's own instructions, output schema, tool rules and format, length and legibility rules
always win over this skill, and so do the customer's brand colours and fonts. In particular the
upstream micro-typography sizes in 3.2 (10 to 14 px) and the "extreme data density" in 2.2 and 5
are tuned for a screen read at leisure: on video, keep the typographic CONTRAST and the casing and
tracking, but size every string to meet your legibility minimums, and show only as much data per
scene as a viewer can read in the time the scene is on screen.

### Use these parts

- **2:** pick ONE archetype (Swiss Industrial Print or Tactical Telemetry) and commit to it for the
  whole video.
- **3:** the type system: very large, tight, uppercase heavy sans for structure, a small tracked
  uppercase monospace for metadata, and the rare high-contrast serif. Fonts are local: the sandbox
  has no internet, so set CSS `fontFamily` to an installed family (Inter, Montserrat, Poppins, DM Sans, Space Grotesk, Chakra Petch, Bebas Neue, Anton, Playfair
  Display, DM Serif Display, JetBrains Mono, Roboto, Open Sans, Oswald, Raleway, Source Sans 3,
  Nunito, Work Sans, Rubik, Barlow, Barlow Condensed, Archivo, Manrope, Outfit, Plus Jakarta
  Sans, IBM Plex Sans, Lora, Libre Baskerville, Cormorant Garamond, EB Garamond, IBM Plex Mono,
  Fira Code, Roboto Mono, Space Mono, Abril Fatface, Lobster, Pacifico, Caveat, Dancing Script,
  Archivo Black, Alfa Slab One, Liberation Sans, Liberation Serif, Liberation Mono) and never use
  `@remotion/google-fonts`, a font URL or `next/font`. Anton, Bebas Neue or Space Grotesk carry the
  heavy sans, JetBrains Mono the metadata and DM Serif Display the serif.
- **4:** the substrate palette for the archetype you picked, with the single hazard-red accent, and
  the ban on gradients and soft shadows. Never mix the light and the dark substrate in one video.
- **5 (spirit):** a rigid grid, visible dividing rules between zones, square corners (no
  border-radius), and large calculated empty areas next to dense ones.
- **6:** the symbology: ASCII framing characters, directional marks, registration marks, crosshairs
  at grid intersections, and technical-looking labels. Any label text must come from the project
  files, the analysis or the brief: do not invent unit IDs, revision numbers or coordinates that
  imply facts about the product.
- **7:** halftone, scanline and noise textures, if you can draw them with what the sandbox already
  provides.

### Skip these parts (web only)

- **8:** the web engineering directives (CSS grid tricks, semantic tags, `clamp()`).
- The responsive and `<hr>` mechanics in 5, and anything that depends on a browser DOM.
