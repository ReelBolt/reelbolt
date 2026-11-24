# Design review: assistant chat panel + workflow diff preview

Scope: `AssistantChatPanel.tsx`, `AssistantProvider.tsx`, `WorkflowDiffPreview.tsx`, `FlowchartBuilder.tsx`.

## Ticket context bullets, verified

1. **Additions/removals/modifications use icon + label + color** (`WorkflowDiffPreview.tsx:71,90,109`) — PASS. Plus/Minus/Pencil are visually distinct shapes, not color-alone.
2. **`diffBadge` node registration** — ticket's premise is stale. `diffBadge: DiffBadgeNode` **is** registered (`FlowchartBuilder.tsx:87`), and `DiffBadgeNode` (lines 48-70) renders both `data.icon` and `data.label`. Removal (`IconMinus`) vs. modification (`IconPencil`) also differ by icon *shape*, not just border color (line 214-215), so the WCAG 1.4.1 use-of-color risk the ticket flagged is already mitigated. A real contrast defect exists in the same component instead — see blocker below.
3. **Apply/Discard keyboard + focus** (`WorkflowDiffPreview.tsx:119-135`) — PASS. Both are real Mantine `<Button>` (native `<button>`), not `onClick`-div; native tab order, Enter/Space activation apply. `theme.ts` is outside this ticket's `reads`, so a repo-wide `focusRing` override can't be ruled out from these files alone — flagged as an unverified assumption, not a finding.
4. **No `aria-live` for streaming assistant text** — CONFIRMED. `grep -rn "aria-live" AssistantChatPanel.tsx AssistantProvider.tsx` returns zero hits. WCAG 4.1.3 (Status Messages) fail.

## Findings

- blocker `web/components/assistant/AssistantChatPanel.tsx:7-15` streaming `MessagePrimitive.Content` has no status-message announcement (WCAG 4.1.3) -> add a Mantine visually-hidden `Text` with `aria-live="polite" aria-atomic="true"`, populated once from the completed message text when the run status transitions to `done` (not on every token delta, to avoid announcement spam).
- blocker `web/components/workflows/FlowchartBuilder.tsx:48-70,215,236,262` `DiffBadgeNode` label/icon color uses raw hex against a white card (`#fff`, line 57): red `#ef5350` ≈3.49:1, orange `#fb8500` ≈2.48:1, green `#66bb6a` ≈2.37:1 — all fail WCAG 1.4.3 (4.5:1, label text); orange and green also fail 1.4.11 (3:1, icon as non-text UI graphic). -> replace the three raw hex literals (lines 214, 215's `borderColor`, and 262) with Mantine `theme.colors.{red,orange,green}[8|9]` tokens (e.g. `var(--mantine-color-red-9)` ≈`#c92a2a`, which clears 4.5:1 on white); this is also a raw-hex-in-component taste violation, not just a contrast one.
- blocker `web/components/assistant/WorkflowDiffPreview.tsx:29,36` `c="dimmed"` (Mantine gray.6, `#868e96`) text sitting inside the colored Paper panels measures ≈3.05:1 (green.0), ≈3.11:1 (orange.0), ≈3.10:1 (red.0) against those backgrounds — fails WCAG 1.4.3 4.5:1 for this normal-weight `size="sm"`/`"xs"` text. -> drop `c="dimmed"` inside these panels; use the same per-panel `.9` shade already used for the heading (`green.9`/`red.9`/`orange.9`) or a fixed `c="dark.6"`.
- major `web/components/assistant/WorkflowDiffPreview.tsx:63,101` heading text `c="green.9"` on `bg="green.0"` ≈4.07:1 and `c="orange.9"` on `bg="orange.0"` ≈3.96:1 both fall short of the 4.5:1 normal-text threshold (red.9-on-red.0 ≈5.10:1 does pass — verified, not just the one bullet the ticket named). -> bump `fw` to 700 and treat as bold-large (3:1 threshold, both then pass), or swap to a darker on-surface token (e.g. `c="dark.8"`) for the three headings.
- major `web/components/assistant/AssistantChatPanel.tsx:34` empty-state instruction ("Ask the assistant...") uses `c="dimmed" size="sm"` directly on the panel's white background ≈3.32:1 — below 4.5:1 for text that is the page's only guidance in the empty state. -> use `c="gray.7"` (or Mantine `dark.4` in light theme) instead of `dimmed`.

## Verdict

fail (3 blockers, 2 major)

## Sign-off 2026-09-27

Independent re-verification of commit `a614cc93` against the current files (not the commit
message). Contrast recomputed from source hex/token values via the WCAG relative-luminance
formula, not eyeballed.

- Blocker 1 (`AssistantChatPanel.tsx:55-92`, `AssistantStatusAnnouncer`) -- **pass**. Renders
  `<VisuallyHidden aria-live="polite" aria-atomic="true">` (line 88). `useEffect` only calls
  `setStatusText` when `lastStatusRef.current === 'running' && messageData.statusType !== 'running'`
  (lines 80-82) -- i.e. exactly once, on the transition away from `running`, not per streamed
  token; `lastStatusRef` is updated unconditionally afterward so a second render at the same
  terminal status can't re-fire it.
- Blocker 2 (`FlowchartBuilder.tsx:214,262`, `DiffBadgeNode` hex vs. white card `#fff`) --
  **pass, all 3**.
  - `#c92a2a` (red, removal): relative luminance 0.1427 -> contrast **5.45:1** vs white (>=4.5:1 text, >=3:1 icon/border).
  - `#c2410c` (orange, modification): relative luminance 0.1525 -> contrast **5.19:1** vs white.
  - `#15803d` (green, addition ghost node): relative luminance 0.1594 -> contrast **5.02:1** vs white.
- Blocker 3 (`WorkflowDiffPreview.tsx:29,36`, `DiffEntry` label/step text now `c="gray.7"`
  `#495057`) -- **pass, all 3**. Recomputed gray.7 (luminance 0.0785) against each panel bg:
  green.0 `#ebfbee` -> **7.61:1**; orange.0 `#fff4e6` -> **7.52:1**; red.0 `#fff5f5` -> **7.64:1**.
  All comfortably clear 4.5:1 for this `size="sm"`/`"xs"` normal-weight text.
- Prior major 1 (heading contrast, `WorkflowDiffPreview.tsx:63,101`) -- **still open, not fixed**.
  Headings are untouched by this commit: `c="green.9"` (`#2b8a3e`) on `bg="green.0"` recomputes to
  **4.07:1**, `c="orange.9"` (`#d9480f`) on `bg="orange.0"` recomputes to **3.96:1** -- both still
  below the 4.5:1 normal-text threshold (red.9-on-red.0 still passes at ~5.10:1, unaffected).
  Non-gating per the ticket, but routes back to Frontend if a follow-up ticket is opened.
- Prior major 2 (empty-state dimmed text, `AssistantChatPanel.tsx:125`) -- **incidentally fixed**.
  The blocker-3 `c="dimmed"` -> `c="gray.7"` sweep also caught this string; recomputed gray.7
  (`#495057`) on the panel's white background: **8.17:1**, well above 4.5:1.

Overall: pass (0 blockers open)
