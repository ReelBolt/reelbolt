# Design review: workflow app run page (T-021)

Scope: `web/components/workflow-apps/` AppRunPage.tsx, AppRunPage.module.css, ReadinessStrip, FileSlot, RunBar, RunResult, run-model. Read, not run (no build, no browser); layout findings are from the CSS and markup.

## Contrast (computed, WCAG 2.1 ratios)

Every text pairing in AppRunPage.module.css passes in both themes. Lowest in light: ok on ok-bg 5.78, bad on pane 6.97, muted on pane 7.35. Lowest in dark: text on note-bg 11.73. All others are higher. Pairings checked: text, muted, bad, ok, warn-fg each on pane, body and its own tint. The blocked Run button at opacity .7 is an inactive control, so it is exempt. Medium confidence on dark: `light-dark()` needs `color-scheme` set by Mantine on the root, which I read but did not run.

## Findings

major AppRunPage.module.css:195-206 the sticky `.actions` bar lives inside `.inputs`; sticky is bounded by its parent, and on a phone the result sits in the next grid row, so the bar scrolls away as soon as the person scrolls to the progress list. Stop (and the price) are out of reach during a run -> on phone set `.inputs { display: contents }` and make the bar a direct child of `.layout`, or render a second fixed Stop bar while `running`.
major ReadinessStrip.tsx:52 the Fix button is `compact-sm` with `minHeight: 32`; on a phone this misses the 44px target the spec requires -> `minHeight: phone ? 44 : 32` (pass `compact` through) and give `<summary>` (line 83) `min-height: 44px; display: flex; align-items: center`.
major RunBar.tsx:~66 the price breakdown (`Collapse` with RunCostEstimate) opens inside the sticky bar, so on a phone it can fill the screen while `--wa-bar-h` stays 9rem (scroll-margin is then wrong, fields hide under the bar) -> render the breakdown in a bottom Drawer on phone, or cap `.actions` at `max-height: 40dvh; overflow:auto`.
major RunResult.tsx:102 the empty state says "Fill in the choices on the left"; on a phone the result is below, not to the left -> "Fill in the choices above and press Run." (works at both widths).
minor RunResult.tsx:96-135 the title status ("The last run did not finish") and the failure banner `role="alert"` both fire on failure, so screen readers hear two announcements -> drop `role="alert"` on the banner (the status region already announces) or keep the alert and make the title static text on Failed.
minor AppRunPage.tsx (run handler) after Run on a phone nothing scrolls to or focuses the result, so the "Result" area is off-screen below the inputs; the announcement is the only cue -> `scrollIntoView({ block: 'start' })` on the Result section when a run starts, without moving focus (focus stays on the Stop button).
minor RunResult.tsx:117-124 progress list: state is a muted "(Working)" suffix at normal weight; the current step is not distinguished except by the spinner -> bold the working row (`[data-state='working'] { font-weight: 600 }`); the list itself is not live, which is right (the title announces start and finish).
minor FileSlot.tsx:113 phone button is named "Choose clips" while the visible field label is "Footage"/"Music"; the label is a `<span>`, not tied to the button -> add `aria-labelledby="{inputId}-label {inputId}"` so the name starts with the visible label, and keep `aria-describedby` for the count.
minor FileSlot.tsx:129 desktop `div tabIndex={-1} aria-labelledby` has no role, so the name is ignored -> add `role="group"` (the inner list already has one; drop one of the two nested groups with the same name).
minor ReadinessStrip.tsx:40-47 the whole strip is one `aria-live="polite"` region containing Fix buttons; any change re-reads every row -> keep `role="status"` but add `aria-atomic="false"` and `aria-relevant="additions text"`, or announce only the worst row via VisuallyHidden.
minor AppRunPage.module.css:195 `z-index: 5` and :150 `background: #000` are raw values; `--wa-line` (rgba .14, ~1.3:1) is used for the pane and bar borders -> use the documented layer token and `var(--mantine-color-black)`; the borders are decorative containers, so this is not an AA failure.
minor RunBar.tsx:~61 `variant="gradient"` violet to purple is a second accent next to the editor's tokens, and white text on it is 4.6:1 at the violet end (passes, little margin) -> use the gallery's primary filled style so one accent runs through the flow; if kept, verify the lightest gradient stop stays above 4.5:1.
minor AppRunPage.module.css:7-16 hex colours instead of tokens (same deferred item as the gallery review) -> track with the stylist; not a regression.

## Checked and fine

Each readiness state has icon plus word plus tint (Fix needed, Check, Ready, Note), so no state is colour alone, and the four are visually distinct (fix also gets a red border). Plain marketer wording: no step, model or id names, no emoji, no em-dashes in visible strings. DOM and tab order run inputs, readiness strip, Run bar, result, history. A blocked press on desktop moves focus to the first invalid field (`focusId`), and on a phone the button reads the fix and performs it. The blocked Run button is `aria-disabled` (stays in the tab order) and linked to its reason by `aria-describedby`. 48px Run button, 44px segmented labels, file rows and result buttons. Safe-area bottom padding is present. `100dvh`. The single-column collapse is stated at 48em. Label above input, errors below. All icons are `aria-hidden` with a word beside them. The video has an accessible name.

Verdict: pass-with-fixes (0 blockers, 4 major, 9 minor). Confidence: high on contrast and markup, medium on the sticky-bar behaviour, which is read from the CSS containment rules rather than seen in a browser.

## Resolution (follow-up commit)

Fixed: all four majors (the bar is a page-level grid item ordered last on a phone, holds the strip, price, Run and Stop; the breakdown opens outside it; Fix button and summary are 44 px on a phone; the empty-result copy is width-neutral) and the minors for the double failure announcement, scroll to the result after Run, file slot name and role, polite updates limited to additions, the working step in bold, and the raw `#000` and `z-index`. Deferred: the hex colour tokens (tracked with the stylist, as in the gallery review) and the gradient Run style.

## Re-check (head f3f179630, read from CSS and markup)

Majors: all four resolved. The strip, notice and Run bar now share a page-level `.bar` that is the last grid item on a phone (sticky, so status and Stop stay on screen for the whole page); the Fix button and `<summary>` are 44px on a phone; the empty copy is correct at both widths (it still switches on `phone`, acceptable); the price breakdown has left the bar. Minors resolved: `aria-atomic`/`aria-relevant` on the strip, file-slot `role="group"` and label-in-name, working-row emphasis, scroll to the result, token z-index, banner alert duplication. New contrast: `--wa-line-strong` 3.4:1 on white and about 5.3:1 on dark body, passes the 3:1 border rule.

Remaining:
minor AppRunPage.module.css:17,40 `--wa-bar-h` is still 9rem but the bar now also holds the strip and notice (roughly 12rem or more), so a focused field can still end up under the bar -> raise to about 14rem or set it from a ResizeObserver on `.bar`.
minor AppRunPage.module.css:228-237 an expanded `<details>` in the strip, or several items, can grow the sticky bar without limit on a small phone -> `max-height: 45dvh; overflow: auto` on `.bar`.
minor AppRunPage.tsx:~785 "Why this price?" opens the breakdown in the results column, which sits above the bar in the phone order but may be off-screen from the tapped link -> scroll it into view on open, or open it in a Drawer on phone.
minor RunBar.tsx:~66 gradient violet/purple second accent and the raw hex palette are unchanged (deferred to the stylist).

Re-check verdict: pass-with-fixes, 0 blockers, 0 majors, 4 minors.
