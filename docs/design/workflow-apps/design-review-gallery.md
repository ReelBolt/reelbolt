# Design review: app gallery (T-020)

Scope: `web/components/workflow-apps/AppsGallery.tsx` and `.module.css`, project page tab. Spec: workflow-apps.md 5.1/5.3, wireframes.html. Static read only (no browser run); no code changed.

## Contrast (computed with a script, WCAG relative luminance)

Light surface #ffffff, dark surface #101218 (`--rb-ed-surface`).

| Pairing | Light | Dark |
|---|---|---|
| text on surface | 15.43 | 15.28 |
| muted on surface | 8.18 | 10.73 |
| chip fg on chip bg | 11.50 | 11.73 |
| selected-chip text on chip bg | 13.37 | 11.73 |
| amber fg on amber bg | 9.08 | 9.73 |
| ok on surface | 6.56 | 12.25 |
| bad on surface | 7.75 | 9.82 |
| focus ring violet-6 on surface (needs 3:1) | 4.95 | 3.78 |

All text and ring pairings pass. Card and button borders (`--rb-ed-line-strong`, about 1.4:1) are below 3:1 but every control also has a text label, so 1.4.11 is not triggered; see minor finding.

## Findings

blocker AppsGallery.tsx:139 `aria-label="Use the {title} recipe"` does not contain the visible text "Use this recipe" (WCAG 2.5.3 Label in Name; voice-control users saying "Use this recipe" cannot target it) -> drop the aria-label and use a visually hidden suffix inside the button: `Use this recipe<span className={styles.srOnly}> for {recipe.title}</span>`, or set `aria-label={\`Use this recipe: ${recipe.title}\`}`.
major AppsGallery.tsx:138 and .module.css `.openLink[aria-disabled]` only sets `cursor: progress`; every other recipe button looks active while one is provisioning, so presses silently do nothing -> add `.openLink[aria-disabled='true'] { opacity: .6 }` (keep text at 4.5:1 or use a dashed border) and put "Setting up another recipe" in the shared live region.
major AppsGallery.tsx:98-109 while busy the `<h3 id={titleId}>` is not rendered, so `aria-labelledby` points at nothing and the card has no name -> render the title always (hide only the body) or set `aria-label` on the busy article.
minor .module.css:134-146 `.openLink` sets `font-weight: 600` then `font: inherit` (shorthand resets weight), so Open / Use this recipe render at normal weight -> move `font: inherit` above `font-weight`.
minor AppsGallery.tsx:44 cost explanation lives only in `title=`, unreachable on touch and keyboard -> move the hint into a visible line (one sentence under the badge on amber classes) or a Mantine Tooltip/Popover with a focusable trigger. The label itself already states the cost, so this is not a blocker.
minor AppsGallery.tsx:237-248 selected chip is shown by weight and border colour only (selected bg is 1.1:1 against the surface) -> add a leading check icon on the selected chip so selection is not carried by subtle styling; also arrow-key roving for `role="radio"` is not implemented (each chip is a tab stop), so either use `role="tab"`-free plain toggle buttons with `aria-pressed`, or add roving tabindex.
minor AppsGallery.tsx:292 `aria-label="Custom workflow"` differs from the heading "Custom workflow (advanced)" -> use `aria-labelledby` like the other cards; "workflow" is the only jargon word here, acceptable for an explicitly advanced card.
minor AppsGallery.tsx:212-220 error state says what happened but not what to do -> "We could not load your apps. Check your connection and try again." (keeps the retry button).
minor AppsGallery.tsx:200-208 loading skeleton is three full-width blocks, not the 1/2/3-column grid, so content shifts on load -> render 3 card-shaped skeletons inside `.grid`.
minor .module.css:7-16 raw hex colours (the file comment explains the muted-grey reason) instead of tokens -> acceptable now; ask the stylist to add `--rb-ed-text-muted` / status tokens so the editor and gallery share one source.
minor .module.css:51-53, 134-146 button and chip borders use `--rb-ed-line-strong` (about 1.4:1) -> use `--mantine-color-gray-6` (light) / `dark-3` for control outlines so buttons read as buttons at 3:1.
minor .module.css:195-203 `.srOnly` lacks `margin:-1px; padding:0; border:0` and a positioned ancestor -> use Mantine's `VisuallyHidden`.
minor AppsGallery.tsx:110 "Suggested for your project" is 12px muted text with no icon or emphasis, easy to miss as the leading signal in an empty project -> use a small badge with an icon (IconSparkles) and the existing `.badge` style.
minor wireframe gap: no "+ Start from a recipe" button in the header (5.1), no thumbnails (declared; no API field), no Needs line on existing apps (declared) -> acceptable for this lane; track the header button as a follow-up (the anchor `#apps-recipes` already exists).

## Checked and fine

Icon plus word on every status and cost badge (never colour alone); plain marketer wording, no step, model or id names; no emoji or em-dashes in visible strings; 44px minimum on chips, search input, links and buttons; single column below 48em, 2 and 3 columns above; focus ring on chips and links (Mantine styles the input and the card buttons share `.openLink`, which is covered by the shared `:focus-visible` rule); empty (no apps, no matches), loading and error states all exist; no motion beyond Mantine's skeleton pulse; results count announced via `role="status"`.

Verdict: fail (1 blocker: 2.5.3 label in name on the recipe button; fix is a one-line change, then pass-with-fixes). Confidence: high on contrast and markup findings; medium on dark-mode `light-dark()` rendering, which was read, not run.

## Resolution (follow-up commit)

| Finding | Status |
|---|---|
| blocker: recipe button label in name | fixed: no aria-label; visible text "Use this recipe" plus a visually hidden " for {title}"; tested |
| major: other recipe buttons while one is set up | fixed: dashed border, reduced opacity, aria-disabled, shared live region says the others are unavailable; tested |
| major: busy card lost its heading | fixed: heading always rendered; tested by the card's accessible name |
| minor: font-weight reset by `font: inherit` | fixed (shorthand moved above the weight) |
| minor: cost hint only in title | fixed: visible line under the badge |
| minor: chip selection and arrow keys | fixed: check icon on the selected chip, roving tabindex, arrows/Home/End; tested |
| minor: custom card aria-label | fixed (aria-labelledby) |
| minor: error state wording | fixed ("Check your connection and try again."); a failed refresh keeps loaded cards |
| minor: skeleton shape | fixed (three card-shaped skeletons in the grid) |
| minor: suggested badge | fixed (badge with sparkles icon) |
| minor: srOnly | fixed (Mantine VisuallyHidden) |
| minor: raw hex colours | deferred (needs shared tokens from the stylist) |
| minor: control border contrast | deferred (token change; labels carry identity) |
| minor/wireframe gaps: header "Start from a recipe" button, thumbnails, Needs on existing apps | deferred |
