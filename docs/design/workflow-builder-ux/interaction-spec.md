# Workflow builder — interaction spec

Source: the workflow builder UX audit (verdict: fail, 3 blockers).
This document is the literal contract Frontend implements — every rule below
is a requirement, not a suggestion. Where the audit flagged something as
"unverified" (out of its `reads`), this spec still states the required
behavior; Frontend confirms current DOM/binding reality against it while
implementing.

All 11 `StepType`s (`web/lib/types/workflow.ts:10`): `Agent`, `Conditional`,
`ForEach`, `ReviewLoop`, `Parallel`, `Extract`, `VideoAnalyze`,
`VideoCompile`, `EditRoom`, `GraphicsRoom`, `ColorGradeRoom`.

---

## 1. Desktop model (>=768px, `FlowchartBuilder.tsx`)

### 1.1 Canvas navigation

- **Pan**: click-drag on empty canvas background (React Flow default),
  two-finger trackpad scroll. No change to current binding.
- **Zoom**: scroll-wheel / trackpad pinch, plus explicit `+`/`-` buttons in
  the `Controls` panel (already present, `FlowchartBuilder.tsx:248`).
  Zoom range 0.25x-2x.
- **Fit to view**: a dedicated "Fit view" `ActionIcon` in the top-right
  `Panel`, next to "Add Step" / "Auto Layout" (`FlowchartBuilder.tsx:250-273`).
  Triggers React Flow's `fitView()`. Bound to keyboard shortcut `F` (below).
  This replaces relying on the `fitView` prop firing once on mount only
  (current: `FlowchartBuilder.tsx:244`) as the sole way back to an overview —
  today a workflow past ~3 steps has no way back to "see everything" short of
  manual scroll/zoom (audit blocker, `FlowchartBuilder.tsx:125,224-232`).
- **Minimap**: React Flow `<MiniMap />`, bottom-right corner, always
  mounted (not toggle-hidden) once a workflow has more than 5 steps; below
  5 steps it is present but visually optional (may be collapsed to a small
  toggle button) since a short workflow doesn't need it. Minimap node colors
  follow the same per-StepType accent used on the canvas nodes (§1.3).
- **Auto Layout is no longer single-column.** `handleAutoLayout`
  (`FlowchartBuilder.tsx:224-232`) and the node-position `useMemo`
  (`FlowchartBuilder.tsx:113-172`, line 125) both currently hardcode
  `x: 250, y: index * 180 + 50` — one vertical column, every render. Replace
  with:
  - A **layered/tree layout**: the main sequential spine runs top-to-bottom
    in one column; a `Conditional` step's two branches render as two
    side-by-side columns (true branch left-of-spine offset, false branch
    right-of-spine offset) that rejoin the spine at whichever step number is
    common to both branches' continuation, if any.
  - Node position is **not** recomputed every render. Position is stored in
    component state, seeded once from `steps` order on mount/step-count
    change, and only overwritten by "Auto Layout" or by React Flow's own
    drag (`onNodesChange`). It must never be silently reset by hovering or
    pinning a card (audit blocker: `expandedStepId`/`pinnedStepIds` are in
    the position-`useMemo`'s dependency array today, `FlowchartBuilder.tsx:172`,
    which discards any manual drag on the next hover).
  - The canvas container drops the fixed `height: '600px'`
    (`FlowchartBuilder.tsx:236`) in favor of filling available viewport
    height (`min-height: 480px`, grow via flex/`dvh`, never a hardcoded px
    block) so a workflow of 8+ steps isn't forced into a scroll-then-zoom
    pattern on first view.

### 1.2 Keyboard shortcut table

| Key | Action | Scope |
|---|---|---|
| `+` / `=` | Zoom in | Canvas focused |
| `-` | Zoom out | Canvas focused |
| `F` | Fit to view | Canvas focused |
| `Tab` | Move focus to next node / toolbar control, in visual reading order (top-to-bottom spine, left-to-right within a branch pair) | Canvas focused |
| `Shift+Tab` | Move focus to previous node / toolbar control | Canvas focused |
| `Enter` / `Space` | Open the focused node's config Drawer (§1.3) | Node focused |
| `Escape` | Close the open Drawer, return focus to the node that opened it | Drawer open |
| `Delete` / `Backspace` | Remove the focused node (with an inline confirm, not a blocking `window.confirm`) | Node focused, not inside a text field |
| `Arrow keys` | Nudge the focused node's position by one grid unit (16px) when not inside a form field | Node focused |

The "Add Step" and "Auto Layout" / "Fit view" toolbar buttons sit in DOM tab
order **before** the node layer, matching their visual top-right position
(audit major finding: current DOM order is `Background`, `Controls`, `Panel`,
with the node layer mounted separately by React Flow with no guaranteed tab
position, `FlowchartBuilder.tsx:234-282`). Frontend must verify with an
actual tab-through and adjust `tabIndex`/DOM position if React Flow's
internal node-layer mount order doesn't already satisfy this.

### 1.3 Branch drawing — every StepType, by name

Edges are drawn from each step's actual data (`trueBranchStepOrder`,
`falseBranchStepOrder`, `loopTargetStepOrder`, `selectedPriorStepOrders`),
never as an unconditional `index-1 -> index` chain. Today every step,
regardless of type, gets exactly one incoming edge from the previous array
index and one outgoing edge to the next (`FlowchartBuilder.tsx:150-167`) —
this is why Conditional/ForEach/ReviewLoop/Parallel/Room branching is
invisible on canvas today; the rules below replace that.

- **Agent** — one incoming edge, one outgoing edge, drawn to the next
  sequential step. No branching.
- **Extract** — one incoming edge, one outgoing edge, sequential. No
  branching.
- **VideoAnalyze** — one incoming edge, one outgoing edge, sequential. No
  branching.
- **VideoCompile** — one incoming edge, one outgoing edge, sequential. No
  branching.
- **EditRoom**, **GraphicsRoom**, **ColorGradeRoom** — one incoming edge,
  one outgoing edge each, sequential (a room step is a black-box
  collaborative sub-session, not a branch point). All three share one
  accent-family stroke/marker color, distinct from the main spine's edge
  color, so the canvas reads "this is a room step" at a glance without
  reading the node body (matches the audit's note that room step types
  already share one accent family, `AddStepModal.tsx` import graph).
- **Conditional** — **two** outgoing edges, never one: a solid edge labeled
  "True" to the step at `trueBranchStepOrder`, and a solid edge labeled
  "False" to the step at `falseBranchStepOrder`, both with their own arrow
  marker. If either target is null/unset, that edge is omitted and the node
  shows an inline "not set" badge (not a silently missing edge that looks
  intentional). Edge labels are real text (not color-only), satisfying
  1.4.1 (use of color) and 1.1.1 for anyone who can't distinguish the two
  edge colors.
- **ForEach** — one incoming edge from its source; the step(s) it iterates
  (`selectedPriorStepOrders`) are drawn as a **dashed "reads from" edge**
  (not the solid sequential-flow edge) from each referenced prior step into
  the ForEach node, distinguishing "data dependency" from "execution order."
  One solid outgoing edge continues the spine. The node itself carries a
  small "×N" badge summarizing how many prior steps feed it.
- **ReviewLoop** — one incoming edge (solid, forward); a **looping edge**
  drawn from the ReviewLoop node back to the step at `loopTargetStepOrder`,
  rendered as a curved edge that visually arcs backward above/beside the
  spine (never overlapping straight through intervening nodes), labeled
  with `≤{maxIterations}` and, when `minScore` is set, `score >= {minScore}`.
  One solid outgoing edge continues forward past the loop.
- **Parallel** — one incoming edge, one outgoing edge on the main spine
  (the parallel fan-out is internal to the step — `parallelAgentIds` are
  agents run concurrently inside this one step, not separate graph nodes).
  The node body shows a "N agents in parallel" badge; no extra edges are
  drawn for this internal fan-out, so it is not confused with Conditional's
  external branching.

Every branch/loop edge above must clear the same 3:1 non-text contrast floor
against the canvas background as the existing spine edge
(`FlowchartBuilder.tsx:160,163`, `#8b5cf6` on white measured at ~4.8:1 in the
audit — reuse tokens verified at that ratio, do not introduce new raw hex
per branch type without checking contrast first, per taste-checklist §1).

---

## 2. Mobile model (<768px, `WorkflowStepList.tsx` / `StepCard.tsx`)

The sub-768px list (`FlowchartBuilderWrapper`, `FlowchartBuilder.tsx:287-305`)
stays first-class: it is not a degraded fallback view, it is the canonical
mobile editing surface, mounted instead of React Flow (never mounted-hidden
behind it).

- **Step order is a flat, numbered list** (`WorkflowStepList.tsx:123-145`),
  one `StepCard` per step, in `stepOrder` sequence.
- **Tap-to-move reorder is retained as-is**: up/down arrow buttons per card
  (`StepCard.tsx:104,115`, `aria-label="Move step up"` / `"Move step down"`),
  not drag-and-drop. This is an explicit accessible-pattern win the audit
  called out (`WorkflowStepList.tsx:72-77`); do not replace it with
  touch drag-and-drop.
- **Branch/loop visibility on mobile**: since there is no canvas to
  cross-reference, every field that names another step by number
  (`trueBranchStepOrder`, `falseBranchStepOrder` for Conditional;
  `loopTargetStepOrder` for ReviewLoop; `selectedPriorStepOrders` for
  ForEach and Parallel's input mapping) renders that target step's own
  label directly next to the number, e.g. "3 — Compile final cut", using
  data already available via `allSteps`/`previousSteps`
  (`ConditionalStepConfig.tsx:40-55`, `WorkflowStepList.tsx:130`). A bare
  numeric input with no label preview is not acceptable on mobile (audit
  major finding, mobile nav).
- **Config opens in a full-height bottom sheet**, never a modal dialog and
  never the current inline-expand-in-card pattern. Tapping a `StepCard`
  opens its config as a bottom sheet that slides up from the screen edge,
  covers the full viewport height minus a visible dismiss affordance at the
  top (drag handle + explicit "Done" button — the sheet must be dismissable
  without relying on a swipe gesture alone), and does not shift or resize
  the page behind it. This mirrors the desktop Drawer (§3) — same field
  grouping, same validation timing, same copy — so a user moving between
  phone and desktop mid-task sees the identical config surface, just a
  different container.
- **Touch targets**: every tappable control on mobile — move-up/down,
  delete, "Add Step", the row itself as the sheet-open trigger, every
  control inside the bottom sheet — is at least 44x44 CSS px (WCAG 2.5.5),
  including hit-area padding beyond a visually smaller icon glyph.
- All 11 StepTypes render through the same `StepCard` + bottom-sheet path
  on mobile; `Conditional`, `ForEach`, `ReviewLoop`, and `Parallel` are not
  given any special canvas-only treatment here since there is no canvas —
  their branch/loop fields are ordinary form fields inside the sheet,
  grouped per §3.

---

## 3. Config-surface UX contract (desktop Drawer + mobile bottom sheet)

Applies identically on both surfaces — same grouping, same validation
timing, same copy tone, same accessible structure. The container differs
(side Drawer on desktop, full-height bottom sheet on mobile); the form
inside it does not.

- **Opening**: click-to-open only. Desktop: clicking a canvas node opens a
  right-side Drawer (React-Flow-adjacent panel, not an inline-expanding
  card body). This replaces the current hover-preview / click-to-pin
  pattern entirely (`FlowchartBuilder.tsx:88-98`, `expandedStepId`/
  `pinnedStepIds`) — **no hover-only functionality anywhere**: hovering a
  node may show a lightweight tooltip (label + step type) but must never be
  the only way to reveal an affordance or content a keyboard/touch user
  can't otherwise reach. The node's own body always shows label + step
  type + a visible "Configure" affordance (a real, always-tabbable
  `<button>`), never a hover-revealed overlay (closes the audit's
  keyboard-reachability gap at `FlowchartBuilder.tsx:88-98`).
- **Field grouping**: every step type with more than 3 fields is broken
  into named `<Fieldset legend="...">` groups (or an Accordion of such
  groups), never a flat list of controls separated only by a visual
  `<Divider label>` (audit major finding,
  `VideoAnalyzeStepConfig.tsx:264-608`, 8 sections / ~49 controls announced
  today as one flat list to a screen reader). Concretely:
  - **Agent**: single group, no fieldset needed (label, agent, input
    mapping, cache mode — 4 fields).
  - **Conditional**: single group "Condition" (condition expression, true
    branch target, false branch target).
  - **ForEach**: single group "Loop source" (loop source expression, prior
    steps selected).
  - **ReviewLoop**: single group "Review loop" (loop target step, max
    iterations, min score).
  - **Parallel**: single group "Parallel agents" (ordered agent list).
  - **Extract**: grouped by `ExtractStepConfig`'s own sections (inputs,
    id-prefix/skip/unknown-id handling, output shaping) — 3 named groups,
    not one flat form.
  - **VideoAnalyze**: the 8 existing `<Divider>`-labelled sections become 8
    named `<Fieldset legend>` groups (silence detection, shot detection,
    transcription, etc.), collapsed by default with a "basic vs advanced"
    split — advanced groups (chunk sizing, char limits) start collapsed,
    core groups (source, detection toggles) start open.
  - **VideoCompile**: same fieldset-per-concern treatment as VideoAnalyze,
    grouped by its own named sub-sections (ducking, transitions, output).
  - **EditRoom**, **GraphicsRoom**, **ColorGradeRoom**: single group per
    room type named for that room ("Edit room", "Graphics room", "Color
    grade room"), exposing only the handful of fields each editor surfaces
    today, config JSON passthrough unaffected.
- **Validation timing**: on-blur, not on-keystroke and not only on submit.
  A field validates when it loses focus; an error already shown clears
  immediately on the next keystroke that would fix it (re-validate on
  change only when the field is already in an error state — never
  interrupt a first-pass typing user).
- **Error copy tone**: plain and specific, never "Oops!" or a generic
  "Invalid input." States what's wrong and how to fix it, e.g. "Step order
  must reference an earlier step in the workflow" rather than "Invalid step
  order." Errors render inline, directly below the field they belong to —
  never as a top-level alert banner.
- **Label position**: label above the input, always visibly present — never
  placeholder-text standing in for a label.
- **Loading/empty states**: the Drawer/sheet shows a designed skeleton or
  labelled loading state while agent/step data resolves (e.g. while
  `agentsLoading` is true), not a bare spinner; if a step type has no
  configurable fields yet (e.g. agent list still loading for Extract/Video/
  Room defaults), the surface says so explicitly rather than rendering
  blank.
- **Transitions**: Drawer slide-in and bottom-sheet slide-up both animate
  `transform`/`opacity` only, 200-300ms, and are skipped entirely under
  `prefers-reduced-motion: reduce` (snap to open/closed state instead).

---

## 4. Accessibility requirements

- **Contrast**: all body text and error/warning text >= 4.5:1 against its
  background; all non-text UI (edges, node borders, focus rings, icon-only
  button glyphs) >= 3:1. The audit's confirmed-likely failure — the
  cache-mode advisory rendered as `Text c="orange" size="xs"`
  (`StepCard.tsx:270-272`, Mantine `orange-6` ~2.6:1 on white) — must be
  replaced with a token verified >= 4.5:1 on every theme this tenant ships
  (light and dark, if both exist), not just the default theme. Verify
  against the tenant's actual computed color, not the Mantine default
  swatch, before shipping.
- **Focus order**: matches visual reading order on every surface — top
  toolbar before canvas nodes on desktop (§1.2), top-to-bottom card order
  on mobile (already correct per audit, `StepCard.tsx:93-287`,
  `WorkflowStepList.tsx:125-140` — do not regress this). Drawer/bottom-sheet
  opening moves focus to the first field inside it; closing returns focus
  to the control that opened it (`Escape`, §1.2).
- **Visible focus ring**: every interactive element — node "Configure"
  button, canvas toolbar icons, Drawer/sheet form fields, mobile move-up/
  down/delete buttons, minimap toggle — shows a visible focus ring on
  keyboard focus. Nothing removes the default outline without substituting
  an equally visible replacement.
- **ARIA roles, canvas nodes**: each React Flow node's outer element is a
  real, always-tabbable `<button>` or has `role="button"` with a
  descriptive `aria-label` combining step number, step type, and label
  (e.g. `aria-label="Step 3, Conditional: Check editorial score"`) — not a
  bare `<div>` relying on a click handler. Branch/loop edges carry their
  "True"/"False"/iteration-count labels as real text nodes in the SVG
  (readable by assistive tech that inspects the DOM), not as color- or
  position-only signals.
- **ARIA roles, mobile list cards**: each `StepCard` row that opens the
  bottom sheet is a real `<button>` (or a focusable element with
  `role="button"`) with an `aria-label` matching the desktop node's
  pattern above (parity between the two surfaces' assistive-tech
  announcements is required, not incidental).
- **No-hover-only rule (explicit)**: nothing in this builder — desktop or
  mobile — reveals a control, a piece of content, or a state change
  *exclusively* on `:hover`. A hover state may add a visual affordance
  (elevation, tooltip) on top of something that is already reachable and
  operable via keyboard focus and tap; it must never be the only path.
  This directly supersedes the current `expandedStepId` hover-preview
  behavior (`FlowchartBuilder.tsx:88-93,96-98`).
- **Reduced motion**: React Flow's animated edge dash (`animated: true`,
  `FlowchartBuilder.tsx:157`) and all Drawer/sheet/branch-edge transitions
  respect `prefers-reduced-motion: reduce` — animation stops or reduces to
  a non-animated state change; this includes verifying `@xyflow/react`'s
  own default CSS honors the media query and overriding it if it does not
  (audit flagged this as not assessed from the granted `reads`).

---

## 5. Self-review against taste-checklist

Reviewed against the design checklist.

| Section | Score | Rationale |
|---|---|---|
| 1. Tokens and color | Meets | Every color rule above (branch edges, focus rings, error text) is specified as "reuse an existing token verified at X:1" or "a token meant for small text," never a new raw hex; the one exception flagged (the orange-6 advisory) is explicitly named for replacement with a proper token, not left as-is. |
| 2. Type, shape, space | Meets | Field grouping standardizes on one system (`<Fieldset legend>`/Accordion) across every step type instead of the current ad hoc `<Divider label>`; no new one-off spacing or radius introduced by this spec. |
| 3. Layout (marketing pages) | N/A | App builder surface, not scored. |
| 4. Copy (UX writing) | Meets | Error-copy rule explicitly bans "Oops!"/generic messages and top-level banners; edge labels ("True"/"False", "≤N") are concrete, no invented precise numbers; no em-dashes used as separators anywhere in this document's prose rules meant to become user-visible copy. |
| 5. Accessibility and states | Meets | Every WCAG item the audit flagged (contrast, keyboard reachability, focus order, structure) has an explicit, testable rule above; no-hover-only stated as an absolute rule, not a preference; loading/empty states required, not left implicit. |
| 6. Redesign invariants | Meets | Nothing here changes route slugs, nav labels, form field names/order (`StepData` field names are reused verbatim throughout), analytics ids, or the existing accessible tap-to-move pattern, which is explicitly preserved rather than replaced. |

No blockers in this self-review; the desktop Auto Layout / position-reset
and edge-drawing rules above are the direct fixes for the audit's three
blockers and are stated as literal requirements, not options.
