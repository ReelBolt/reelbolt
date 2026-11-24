# Assistant permission requests and auto mode

Status: implemented. Audience: developers. (`docs/design/**` is excluded from
the documentation site and from the assistant's documentation search.)

## Problem

The assistant changed things through a propose → apply pair, and the *human* half of "propose, then get
agreement" was the model writing the proposal out as prose and asking "shall I go ahead?". The user typed
"yes", and the model then called the apply half. That is slow (two turns), unreliable (the model decides
whether it asked), and unreadable (a wall of text, sometimes with ids).

## Design

Every tool that writes is gated **in the server**, not in the prompt.

```
model ──ProposeX──▶ tool ──▶ result (token, summary)        gate remembers it (the "ledger")
model ──ApplyX────▶ gate ──▶ [ask mode]  data-assistantAction state=pending ──▶ browser card
                                          user clicks Allow / Don't allow ──POST /approvals/{id}──▶ gate
                            [auto mode]  no card to answer; the action is still shown (done / failed)
                      └──▶ tool runs (only after Allow) ──▶ data-assistantAction state=done | failed
```

* The gate wraps **every `Apply*` tool and `ExecuteWorkflow`** (`AssistantApprovalCatalog` lists them). A new
  `Apply*` tool is gated by its name; `AssistantApprovalCatalogTests` fails when one is missing from the
  catalog.
* Authority is unchanged: the gate sits *in front of* the tool body, which still verifies the confirmation
  token, ownership, role and plan. Allowing a request never widens what the tool may do.
* `Propose*` tools are not gated (they write nothing).
* The MCP tool bridge (`AssistantToolBridgeController`) is **not** gated for the other tools: the external client (Claude Code)
  asks its own user. Only the in-app chat transport installs a gate. `ApplyDecideDesignChange` is the carve-out: the tool
  body refuses `approve` when the call came over MCP, when the user is in auto mode, or when the account row is missing,
  so approving a brand change always needs a click in the app. `reject` is allowed.
* A refusal writes nothing and tells the model, in a result it reads, that the user said no.

### Auto mode

`application_users.assistant_auto_approve` (boolean, default `false`, migration `AddAssistantAutoApprove`),
read **live on every gated call** — never cached for a turn — so switching it mid-turn applies to the very
next action, and so does answering a card with "Always allow". There are two modes, `ask` and `auto`, and no
exceptions in `auto` for deletions and the like: it skips those cards. The exceptions are cost (below) and the
always-ask set: `ApplyRestoreProjectVersion`, `ApplySchedulePost`, `ApplyCancelScheduledPost`,
`ApplyCreateInferenceProvider`, `ApplyUpdateInferenceProvider`, `ApplyDeleteInferenceProvider`,
`ApplyAssignAgentSkills` and `ApplyImportFileFromUrl` (`AssistantApprovalCatalog.AlwaysAsksUser`). Their card is
shown in auto mode whatever the cost, and "Always allow" on it does not switch auto mode on. The admin writes are
there because an injected instruction must not be able to change an AI service connection (its key and endpoint)
or an agent's skills without the user's own click;
the card of a workflow edit that turns a step's `allowSourceMediaEgress` on (`ApprovalCard.AllowsMediaEgress`, see below) is also always shown, because that click is the consent for sending the user's media; the link import because it makes the server fetch an address
the model chose. A run-wake turn shows the same card and it expires if nobody answers.
The web app states what auto mode includes before it switches the mode on. Each action is still reported to the chat (`mode: "auto"`,
`state: "done" | "failed"`), so the user can see what was done on their behalf.

#### Cost limit in auto mode

`Assistant:AutoApproveCostCardCredits` (credits, default **25**) is a ceiling for auto mode. When one gated
action's own card carries a credit figure above it, the card is shown and the action waits for the user exactly
as in ask mode (same broker, decline and expiry rules). The figure compared is the one the card builder already
computed, never a new estimator:

- `ExecuteWorkflow`: the preflight's **p90 required (hold) figure** (`RequiredMillis`, what the run must be able
  to hold, so it is the conservative number the card leads with).
- `ApplyReviseComposition`: its `RequiredCredits`.
- `ApplyDesignGraphic`: the fresh estimate's required credits.
- `ApplyRunPictureTool`: the one-picture quote.

A tool whose card has no credit figure behaves as before. The limit is **per action**: several small actions in
one turn each run without a card, and the limit says nothing about their sum. Exactly the limit does not trigger
a card (`>` not `>=`). A value that is missing, not a number or `<= 0` falls back to 25 (the check is never
silently disabled); to raise it set a larger positive number, e.g. `Assistant__AutoApproveCostCardCredits=100`.

Billing off means no cost is known and no card. With billing on, any catalog-Paid card (`ApplyRunPictureTool`,
`ApplyDesignGraphic`, `ApplyReviseComposition`, `ExecuteWorkflow`) that has no numeric cost, or whose card could
not be built, is `CostUnestimated` and fails closed: a card is shown saying the cost could not be estimated.
The frame carries `autoNote: { limitCredits, costCredits? }` (absent `costCredits` = could not be estimated) and
`mode: "ask"` so the UI says "Shown because this costs about X credits (your auto-approve limit is Y)".
`GET`/`PUT /api/v1/assistant/preferences` also return the read-only `autoApproveCostLimitCredits`. MCP is
unchanged: no gate is installed there (`ExecuteWorkflow` over MCP still needs `acceptEstimateCredits` for
profile builds).

### Wire contract

#### Chat stream frame (`data-assistantAction`)

One frame type, upserted by `id`; the newest frame for an id replaces the earlier ones.

```jsonc
{
  "type": "data-assistantAction",
  "data": {
    "id": "guid",
    "state": "pending | allowed | declined | expired | done | failed",
    "mode": "ask | auto",
    "kind": "workflow | project | file | style | picture | run | connection | agent",
    "risk": "normal | destructive | paid",
    "title": "Rename the project “Launch Teaser”",   // plain language, never an id
    "summary": "…",                                    // optional, one or two plain sentences, no ids
    "changes": [ { "kind": "add | remove | change | info", "text": "…" } ],   // optional
    "projectName": "Launch Teaser",                    // optional: which project it happens in
    "cost": "usually 8–12 credits (needs 16 available to start; can cost more)",   // optional, paid actions only
    "outcome": "…",                                    // done/failed: short plain-language result or reason
    "expiresAt": "2026-10-11T10:00:00Z",              // pending only
    "refresh": { "projectId": "guid", "workflowId": "guid", "documentId": "guid" }  // done only; for the app, never shown
  }
}
```

State machine: `pending → allowed → done | failed`, `pending → declined`, `pending → expired`. In auto mode only
`done` / `failed` are emitted, except a card shown because of the cost limit (`autoNote`), which goes through
`pending` like an ask-mode card. `expired` means nobody answered within `Assistant:ApprovalTimeoutSeconds`, or the turn
itself was cancelled while the card was open. **It does not mean the browser went away**: a dropped connection (a phone
screen switching off, a navigation) leaves the card `pending` and answerable, because the turn belongs to the process
(`AssistantTurnHub`), not to the connection. A screen that re-attaches (`POST /api/v1/assistant/chat` with `attach`)
gets the same `pending` frame replayed and answers it through `POST /api/v1/assistant/approvals/{id}`;
`GET /api/v1/assistant/threads/{threadId}/turn` reports `awaiting_approval` with the card's `pendingApprovalId`. A
`pending` frame is always followed by exactly one terminal frame (`done`, `failed`, `declined` or `expired`) unless the
process is lost.

`title` is an imperative sentence the user can say "yes" to ("Delete the file “notes.txt”"), built by the
server from names it just read from the database — never from the model's words alone and never containing a
GUID. `refresh` tells the app what to reload; it is the only place ids travel.

A file-edit card (`ApplyEditProjectFile`, `ApplyInsertProjectFileLines`) shows the real diff rather than a summary: the
file path, the project name, and per change the line number, a little unchanged context and the removed and added lines.
It is built from the file as stored in the database right now plus the exact arguments of the Apply call (the edit is
dry-run again), not from the earlier proposal; if the edit no longer applies, or the file is not the caller's, the call is
refused as unreachable. Apply itself also refuses when the file changed since the proposal. The card is bounded: at most 60
changed lines and 100 rows in all. Every change keeps its header and its first lines, with "…and N more changed lines in this
change" when it is cut, and an edit with too many separate changes to review in one card is refused ("split it into smaller
edits"). A long changed line that replaces another is shown as a window around the first difference (through the last, with `⟦+N chars⟧` markers and the character offset); a long added or removed line with no counterpart is shown in full as consecutive rows of at most 200 characters (never splitting a surrogate pair), each row but the last ending in `⟦line continues⟧` and each row but the first starting with `⟦continued⟧`, with spaces at a row's edges shown as `\s` (trailing spaces of every changed line, and a trailing carriage return as `\u000D`, are shown too, so a whitespace-only change is visible); the card is refused if that does not fit the row budget. Characters a reader cannot see or could mistake for others (Unicode categories control, format, line and paragraph separator, private use, non-ASCII space, unpaired surrogate; the default-ignorable code points; combining marks with no visible base; U+061C; and the card's own marker brackets) are shown escaped as `\uXXXX` (`\u{XXXXX}` above U+FFFF), a tab as `\t` and a backslash as `\\`, so typed text cannot pose as an escape. A switch of the file's line-ending style (CRLF, LF, mixed) adds a row "Line endings change: X → Y."; an edit whose only effect is invisible characters is refused, and so is one whose line diff exceeds `MaxDiffedLines` (20,000). More than four combining marks on one base are escaped, and the card text keeps its spaces (`white-space: pre-wrap`). The edit is made in place and the previous contents are replaced, so the card carries the destructive
risk, like an overwrite (see [Targeted file reads and edits](../platform-assistant-mcp.md#targeted-file-reads-and-edits)).

Also sent: a `data-toolStatus` caption `"Waiting for your OK"` while a card is pending.

#### Answering a card

`POST /api/v1/assistant/approvals/{id}` — body `{ "decision": "allow" | "deny", "alwaysAllow": false }`

| Status | Meaning |
|--------|---------|
| `204` | recorded |
| `400` | unknown `decision` |
| `404` | no such request for this caller — unknown id, another user's, or already finished (never `403`: existence is not confirmed) |
| `409` | already decided |

`alwaysAllow: true` together with `allow` also switches the caller to auto mode (persisted) *before* the
action runs.

#### Preference

`GET /api/v1/assistant/preferences` → `{ "approvalMode": "ask" | "auto" }`.
`PUT /api/v1/assistant/preferences` with the same body → `200` with the stored value (read back from the row).

#### What the model reads

* Allowed → the tool's own normal result.
* Declined → `{"declined": true, "message": "The user chose not to allow this, so nothing was changed. Do not try again; say so briefly and ask what they would like instead."}`
* Expired → `{"declined": true, "expired": true, "message": "The user did not answer in time, so nothing was changed. …"}`

The prompt tells the model to call the apply half **right after** the propose half, in the same turn, and
never to ask in prose whether to go ahead — the card does that.

### Workflow edits and the stale-proposal bug

`WorkflowEditDiff.ConfirmationToken` hashes the workflow's *current* steps as well as the proposed ones, so a
proposal stops verifying the moment anything else changes the workflow — including the assistant applying it
itself earlier in the same turn. The page's review tray was still showing that proposal, so pressing **Apply
changes** answered "Proposal out of date" for changes that were already in.

Fixes:

1. The chat transport only emits `data-workflowDiff` for a proposal that is **still unapplied** at the end of
   the turn (`AssistantToolProvider` clears `LastProposedDiff` when `ApplyWorkflow` succeeds).
2. `WorkflowEditDiff` carries `workflowId` (the target; null for "create a new workflow"), so the page applies a
   proposal to the workflow it was computed for.
3. Apply is **idempotent** (REST `POST …/workflows/assistant/apply` and the `ApplyWorkflow` tool): a token that no
   longer verifies, where recomputing the diff of the proposed steps against the stored steps yields *no*
   additions, removals or modifications, answers `200 { …, "alreadyApplied": true }` instead of `409`.
4. The web page treats a settled `done` action of kind `workflow` as consuming the tray's proposal.

### Turning on a step's media permission

`ApplyWorkflow` and `ApplyPatchWorkflow` may carry a step's `allowSourceMediaEgress` from false to true when the user
asked for it. The card is built from the database and the exact steps, and adds one line per step naming it and what
it sends (the same words as the run card). `ApprovalCard.AllowsMediaEgress` forces Ask, so the card is shown in auto
mode and on a run-wake turn, and "Always allow" on it does not switch auto mode on. Approving it is the consent: the
gate sets the ambient `AssistantMediaEgressApproval` for that one call, and `AssistantEgressConsentGuard` reads it.
The confirmation token alone never grants, and `ChatApproved` alone is not consent. Declined or expired, nothing is written (the workflow stays as it was). Over MCP there is no card and no
grant: the user's MCP media setting allows one run at a time, never a saved step switch. At Apply each granted step must be one
the card listed (`AssistantMediaEgressApproval.ListedSteps`).
A grant that reaches the model only through file text, tool output or a fetched page still has to pass the card.

### Names, not ids

Everything the user reads names things the way the product does. The prompt says so; tool results carry names
next to every id; the server builds each card's text from the database; and `AssistantReplySanitizer` is a
deterministic last line — it replaces any id the turn's tools returned with that entity's name, and removes any
other GUID-shaped token from the reply before it is persisted or streamed.

### Implementation notes

* **Where the code is.** `Agents/Tools/AssistantToolInterceptor.cs` (the wrapper), `Services/Assistant/AssistantApprovalGate.cs`
  (one per turn), `AssistantApprovalBroker.cs` (the rendezvous), `AssistantApprovalCatalog.cs` (which tools, how presented),
  `AssistantApprovalCardBuilder.cs` (the card's words), `AssistantActionFrame.cs` (the wire vocabulary),
  `Controllers/AssistantApprovalsController.cs` (answer a card, preference). The web side is `components/assistant/ActionCard.tsx`,
  `lib/assistant/actions.ts` (the reducer over the frames), `lib/hooks/use-assistant-preferences.ts` and the switch in
  `AssistantDrawer.shell.tsx`.
* **The card and the tool read ONE argument dictionary, by exact name.** `AssistantToolInterceptor` hands both a canonical copy that keeps
  only the function's own parameter names (matched ordinally, exactly as the function binder does), and `ToolArgs` reads it ordinally.
  A differently-cased or stray key therefore means nothing to either of them. (The first version read case-insensitively while the binder
  did not: one mis-cased key could make the card describe one action while the tool performed another, or hide the target so no card was
  shown at all. `AssistantApprovalHardeningTests` reproduces each case; a tripwire test fails if the binder ever becomes case-insensitive.)
* **A card is built from names the server reads**, through the same access service the tool uses — and always at the *weakest*
  permission (`View`) — and from the very arguments the tool will receive (a provider card shows the endpoint host, default flag and
  what happens to the stored key; a workflow edit lists a rename and each step's real kind, e.g. "Review (video generate)"; a run shows
  its credits, see below).
* **The cost line never shows less than the run needs to start.** A run card resolves the style, format and resolution with the
  same `RunStyleResolver` call `ExecuteWorkflow` makes, and applies the same plan check
  (`IPlanGateService.CheckResolutionAsync` with the project's organization and the resolved resolution). A choice the tool would
  refuse, or a resolution the plan does not render, is shown as "This run will not start: …", with no cost and no resolution line. It then prices the run through `IWorkflowExecutionPreflight.PriceAsync` — the same workflow load and the same estimator preflight
  the tool's check makes, for the same organization and resolution — and words it with `CreditCostWording.RunCost`, like the run
  modal: "usually A–B credits (needs C available to start; can cost more)", plus ", plus up to F for generated video" when the run
  generates clips. The range is rounded to nearest; C (the preflight's `RequiredMillis`) and F are rounded UP at the shown precision
  (one decimal below 10, whole credits from 10), so 12.4 reads 13. A designed-graphic card prices the same way, fresh through
  `IGraphicDesignService.EstimateAsync` for the access-checked project only (the project's hidden workflow, or the step it will have:
  the GraphicDesigner agent's). It never falls back to an earlier proposal's figures, which may be stale. An AI picture tool has no
  hold (it is metered afterwards from a one-picture rate-card quote), so its card says "about X credits per picture; larger pictures
  or several results can cost more", X rounded up.
* **An unknown cost is not "free".** With billing on, the preflight can fail open: the estimate or the balance read throws, the run
  is still allowed (`WorkflowPreflightResult.Evaluated` is false) and is still charged. The run card then stays paid ("Allow and use
  credits"): it shows the estimate's figures when only the balance read failed, else "the credits this run uses could not be worked
  out in advance". A picture-tool or designed-graphic card whose quote or fresh estimate is unknown likewise stays paid and says "the
  credits this uses could not be worked out in advance". Only an explicit "not charged" makes such a card normal: billing off on the
  installation, or a picture-tool quote of 0 credits because the workspace's own AI service bills it. A run whose steps' agent types
  could not be read is also not evaluated (the figure could miss a render step's resolution scaling), so its card says the cost is
  unknown too.
* **A card that could not be built** (the builder threw) is replaced by `AssistantApprovalCardBuilder.FallbackCard`: the catalog's
  generic card, except that `ExecuteWorkflow` and every Paid tool (`ApplyRunPictureTool`, `ApplyDesignGraphic`) stay paid with the
  "could not be worked out in advance" line. They are normal with no cost only when the cost estimator says billing is off.
* **"No card" never means "no check".** The builder returns an `ApprovalVerdict`: a card to ask with, `QuestionOnly` (the one call that
  legitimately has nothing to approve — an `ExecuteWorkflow` of a paid-clips workflow that has not yet been told reuse-or-regenerate, which
  only returns that question), or a refusal. A call the builder cannot place (an unknown project, a foreign file, an admin tool without the
  admin right, no picture open) is **refused by the gate and the tool never runs**; the model reads `{"error": "…not found"}`.
* **Ledger.** Each `Propose*` result is remembered by its confirmation token, so the matching `Apply*` card can list the real
  changes (a workflow edit's added/removed/changed steps). An `Apply*` with no remembered proposal still asks, with the generic card.
* **Failure text** on a `failed` frame is mapped to plain words by the gate (`stale` → "out of date", `not found`,
  `INSUFFICIENT_CREDITS`, …); a technical error string never reaches a card.
* **Config.** `Assistant:ApprovalTimeoutSeconds` (default 600, clamped 1–3600).
* **Tests.** `AssistantApprovalGateTests` (ask/deny/expiry/card-survives-a-disconnect/auto/always-allow/card words), `AssistantApprovalsControllerTests`
  (broker, endpoint, preference, catalog coverage), `AssistantApprovalFlowTests` (the real chat endpoint and function-calling loop,
  including the stale-proposal regression), `AssistantApplyWorkflowIdempotencyTests`, and on the web `ActionCard.test.tsx`,
  `AssistantActionCards.integration.test.tsx`, `AssistantModeSwitch.test.tsx` and the workflow page's apply tests.

### Stopping a run

`ApplyStopExecution` (kind `run`, risk `destructive`) has a catalog entry like every `Apply*`. Its card is titled "Stop the run of
“<workflow>”", names the project, and says the run stops, work in progress is lost, and credits already used are not refunded
(steps that have not started are not charged); when the ledger has settled steps for the run it adds "Credits used so far". A run of a
project the caller cannot edit, or a missing one, is `Unreachable` (absent, never revealing existence). The tool publishes the same
`WorkflowExecutionStopRequested` event as the REST stop, so the engine's cancellation registry, leases, holds and SSE events behave
identically; a run that already finished is answered as already stopped, not an error. Tests: `AssistantStopExecutionTests`.

### What is deliberately not gated

Only tools that change a person's data or start work they will be charged for are gated. A few reads are not: `SearchPlatformDocs`
(one embedding call), `LookAtPicture` (one small vision call, metered), `TestInferenceProvider` (the admin page's Test button — one tiny
request and the last-test result) and `DescribePictureDocument`, which delivers a designed graphic the user already approved. Each is
metered or an explicit diagnostic; a card for each would only teach people to click through cards.

### Not covered

* Approval requests live in process memory (like `AssistantTurnRegistry`). An API restart abandons the request — the
  turn is gone and nothing was changed. A browser reload or a dropped connection does not: the card stays open and the
  reopened screen re-attaches to it (see the state machine above).
* The MCP bridge has no card; the MCP client's own approval step is the human check there, and the per-user auto-mode flag is not
  consulted on that path (except that `ApplyDecideDesignChange(approve)` is refused over MCP outright) (a prompt-injected MCP client is the MCP client's user's to catch — a per-user opt-in like
  `mcp_admin_tools_enabled` would be the next step).
* Approval requests are not shared between API replicas: with more than one, an answer routed to another replica is a 404 and the
  turn waits out `Assistant:ApprovalTimeoutSeconds` and expires (fail closed, but it looks like a hang). A single replica, or sticky
  routing for `/api/v1/assistant/*`, avoids it.
