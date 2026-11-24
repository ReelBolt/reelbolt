# Platform Assistant + MCP Server

ReelBolt supplies a conversational platform assistant, served by the Inference API and mounted over the whole authenticated dashboard, plus a Model Context Protocol (MCP) server for external CLI clients such as Claude Code. The assistant manages projects, files, workflows, pictures in the picture editor, agents and inference providers through a closed, server-authorised tool set. The MCP server reimplements none of it: it is a thin proxy that lists the assistant's tools through the MCP projection (the collapsed surface, [below](#proposeapply-pairs-collapse-to-one-mcp-tool)) and forwards each call to the Inference API, so every tool has one implementation and one place that decides who may call it.

---

## Table of Contents

- [Architecture overview](#architecture-overview)
- [The platform assistant](#the-platform-assistant)
- [Chat transport](#chat-transport)
- [Conversation threads](#conversation-threads)
- [Unanswered turns and turns that outlive the browser](#unanswered-turns-and-turns-that-outlive-the-browser)
- [Background work never blocks an upload or the assistant](#background-work-never-blocks-an-upload-or-the-assistant)
- [The tool surface](#the-tool-surface)
- [Moving files and outputs in and out over the MCP](#moving-files-and-outputs-in-and-out-over-the-mcp)
- [Run results, waiting and credits](#run-results-waiting-and-credits)
- [Async run wake](#async-run-wake)
- [The footage-sharing consent on workflow edits](#the-footage-sharing-consent-on-workflow-edits)
- [Allowing media to be sent for one run](#allowing-media-to-be-sent-for-one-run)
- [App inputs: an app's own fields](#app-inputs-an-apps-own-fields)
- [Object effects: blur or mask an object by name](#object-effects-blur-or-mask-an-object-by-name)
- [Picture editor tools](#picture-editor-tools)
- [Per-call authority: platform and organization](#per-call-authority-platform-and-organization)
- [Confirmation tokens](#confirmation-tokens)
- [Permission cards and auto mode](#permission-cards-and-auto-mode)
- [Guardrails, names and the documentation boundary](#guardrails-names-and-the-documentation-boundary)
- [The web surface](#the-web-surface)
- [Assistant execution rate limiting](#assistant-execution-rate-limiting)
- [The MCP server](#the-mcp-server)
- [The MCP tool bridge](#the-mcp-tool-bridge)
- [Connecting Claude Code over OAuth](#connecting-claude-code-over-oauth)
- [MCP metering and the admin-tools setting](#mcp-metering-and-the-admin-tools-setting)
- [Step configuration schemas](#step-configuration-schemas)
- [Authentication](#authentication)
- [Deployment](#deployment)
- [MCP exposure through nginx](#mcp-exposure-through-nginx)

---

## Architecture overview

```
┌──────────────────────────────────────────────────────────────────────┐
│                            Inference API                             │
├──────────────────────────────────┬───────────────────────────────────┤
│  AssistantChatController         │  AssistantThreadsController       │
│  POST /api/v1/assistant/chat     │  /api/v1/assistant/threads        │
│  SSE stream of one reply         │  caller-scoped thread CRUD        │
│           ▲                      │           ▲                       │
│           │ runs                 │           │ persists              │
│  ┌────────┴──────────────────────┴───────────────────────────────┐   │
│  │ AssistantAgent                                                 │   │
│  │   └── AssistantToolProvider (153 chat tools; 101 over MCP)     │   │
│  │        ├── 8 core provider tools                               │   │
│  │        ├── AssistantProjectTools  (8)                          │   │
│  │        ├── AssistantFileTools     (5)                          │   │
│  │        ├── AssistantWorkflowTools (6)                          │   │
│  │        ├── AssistantKnowledgeTools(3)                          │   │
│  │        ├── AssistantStyleTools    (3)                          │   │
│  │        ├── AssistantDesignSystemTools (6)                      │   │
│  │        ├── AssistantPictureTools  (15, the picture editor)     │   │
│  │        ├── AssistantOutputTools   (4)                          │   │
│  │        ├── AssistantUploadTools   (4)                          │   │
│  │        ├── AssistantRunTools      (2)                          │   │
│  │        ├── AssistantRevisionTools (2)                          │   │
│  │        ├── AssistantChangeRequestTools (3)                     │   │
│  │        ├── AssistantWorkflowPatchTools (4)                     │   │
│  │        ├── AssistantPictureDocumentTools (2)                   │   │
│  │        ├── AssistantGreenScreenTools (2)                       │   │
│  │        ├── AssistantObjectEffectTools (2)                      │   │
│  │        ├── AssistantAudioCleanupTools (2)                      │   │
│  │        ├── AssistantVideoEnhanceTools (2)                      │   │
│  │        ├── AssistantRecipeTools   (4, recipe-first)            │   │
│  │        └── AssistantAdminTools    (13, gated: 10 platform + 3  │   │
│  │                                       organization)            │   │
│  └────────────────────────────────────────────────────────────────┘   │
│                          ▲                                            │
├──────────────────────────┼────────────────────────────────────────────┤
│ Services                 │                                            │
│  WorkflowEditDiffService │ (ProposeWorkflow/ApplyWorkflow)            │
│  IConfirmationTokenService (HMAC-SHA256, one key per deployment)      │
│  IAssistantExecutionGuard (rolling-hour rate limit)                   │
│  InferenceApiDbContext   (assistant_threads, assistant_messages)      │
└──────────────────────────────────────────────────────────────────────┘
                                 ▼
                          PostgreSQL Database
                    (assistant_threads, assistant_messages,
                     Project, WorkflowDefinition, WorkflowStep,
                     WorkflowExecution, AgentDefinition,
                     InferenceProvider)
```

The Inference API owns the assistant end to end: the transport, the conversation store, the tool
implementations and the check on who is allowed to call them. Nothing the model produces is an
authorisation — every tool re-resolves the project, the workflow and the caller from the database on
the call that is executing.

The MCP server is a separate Node.js process, and it does not reimplement the tool set: it asks the
Inference API for the assistant's tool catalog and forwards each call to the assistant's own tool
body. See [The MCP server](#the-mcp-server) and [The MCP tool bridge](#the-mcp-tool-bridge).

---

## The platform assistant

The assistant is one agent (`AssistantAgent`, an `AgentType.Assistant` built-in) reachable from every
authenticated page. It answers questions about projects, files, workflows, agents and inference
providers, and it changes them — but only through a propose-then-apply pair carrying a server-minted
confirmation token, never on the strength of an argument the model supplied.

Its system prompt (`AssistantAgent.DefaultPrompt`, duplicated verbatim in
`DatabaseSeeder.BuiltInAgents` and pinned by `AssistantPromptConsistencyTests`) names every
registered tool, states how many there are, and frames everything a tool returns as **untrusted
DATA** the model is reading, never as an instruction to follow — the same framing this codebase uses
for media-derived content (see `CLAUDE.md` § Agent Types).

---

## Chat transport

```
POST /api/v1/assistant/chat
Authorization: Bearer <jwt>
Content-Type: application/json

{
  "threadId": "<guid>",
  "message": "delete the stale drafts workflow",
  "projectId": "<guid>",
  "workflowId": "<guid>"
}
```

| Field | Required | Meaning |
|-------|----------|---------|
| `threadId` | yes | The server-persisted thread this turn belongs to. |
| `message` | yes | The user's new message. Prior turns come from the thread, not from the request. |
| `projectId` | no | The project the caller currently has open. |
| `workflowId` | no | The workflow the caller currently has open. |
| `imageDocumentId` | no | The picture document open in the picture editor. Treated as absent unless it is a document of the project in context; the picture tools act on it when they are given no `documentId`. |
| `attach` | no | `true` re-attaches this response to the thread's **live (or just-finished) turn** instead of starting one: it carries no `message`, appends nothing and starts no second turn — the turn is replayed from its first frame and then streamed live. A thread with no live turn is `404`. See [Unanswered turns and turns that outlive the browser](#unanswered-turns-and-turns-that-outlive-the-browser). |

### `projectId` is per-call context, not route state

The assistant was previously mounted **per project**, with the project as a route parameter. That
route is **deleted**. It could not express the case the shipped client actually has: a page with no
project at all, where the assistant must still open and answer. A route parameter cannot say "no
project", so context moved into the body.

- **A page with no project is a normal state.** `projectId` is `null`, the request is accepted, and
  project-scoped tools answer with an actionable JSON error (`{"error": "no project in context"}`)
  that the model can act on rather than an exception.
- **A project the caller does not own is treated as ABSENT, not rejected.** The controller checks
  `p.OwnerId == userId` and, if it fails, drops the context and proceeds. Rejecting would confirm to
  a stranger that a guessed project id is real; the thread keeps working either way.
- `workflowId` is validated by the same rule, and additionally must belong to the project in context.
  It is injected into the **first user turn** as a `[WORKFLOW CONTEXT]` block, so it stays visible
  across the whole multi-turn exchange.

### Responses

Before any frame is written:

- `404` with `{"error": "Thread not found"}` when the thread does not exist **or belongs to another
  user** — never `403`, which would confirm it exists.
- `400` with `{"error": "Message must not be empty"}` when `message` is blank.

The user's turn is persisted *before* the response stream starts, so a refused request records
nothing and a dropped stream still records what the user said.

On success the response is `text/event-stream` (`Cache-Control: no-cache`, `Connection: keep-alive`)
carrying the ui-message-stream protocol. Each frame is `data: <json>` followed by a blank line, and
the frames arrive in this order:

| Frame | Payload | When |
|-------|---------|------|
| `start` | `{"type":"start","messageId":"<guid>"}` | Always, first. |
| `text-start` | `{"type":"text-start","id":"<guid>"}` | Always, second. |
| `text-delta` | `{"type":"text-delta","textDelta":"<the reply>"}` | Always. The whole reply arrives in this one frame. |
| `text-end` | `{"type":"text-end"}` | Always. The assistant's turn is persisted *before* this frame, so a stream that dies afterwards still leaves a complete record. |
| `data-workflowDiff` | `{"type":"data-workflowDiff","data":{…WorkflowEditDiff…}}` | Only when a workflow was proposed during this turn. |
| `data-imageDocumentChanged` | `{"type":"data-imageDocumentChanged","data":{"changeId":"…","projectId":"…","documentId":"…","kind":"layersAdded","layerIds":["…"],"summary":"<plain words>","graphicId":null}}` | Zero or more, the moment a picture tool APPLIED a change (mid-turn, before `text-delta`). `kind` is `layersAdded`, `layersChanged`, `layersDeleted`, `documentChanged` or `graphicQueued`. The picture editor showing that document reloads it and points at the changed layers; see [Picture editor tools](#picture-editor-tools). |
| `data-toolStatus` | `{"type":"data-toolStatus","data":{"caption":"<plain words>"}}` | Zero or more, between `text-start` and `text-delta`: one when each tool starts ("Searching the documentation") and one when it returns ("Thinking", or "Docs index still building — answering without it"), so the panel's label always says what is true now. |
| `finish` | `{"type":"finish","finishReason":"<reason>","usage":{"inputTokens":<n>,"outputTokens":<n>}}` | Always. `<reason>` is the **provider's own** finish reason mapped onto the AI SDK vocabulary (`stop`, `length`, `content-filter`, `tool-calls`, `other`, or `stop` when the provider reported none) — never a hardcoded `stop`. |
| `[DONE]` | the literal `data: [DONE]` | Always, last. |

Payloads are serialized camelCase with nulls omitted. The frame set and its order are a published
contract — the client parses by `type`.

### Reply length, and why a truncated reply says so

An assistant turn is bounded twice, from opposite ends, and both bounds announce themselves.

**The model's own ceiling is set before the call.** `AssistantChatLimits.MaxOutputTokens` resolves one
number per turn and sends it as `ChatOptions.MaxOutputTokens`:

- On a provider that draws its reasoning from the **same** budget as the visible answer — Anthropic
  (`thinking.type=adaptive`) and Gemini (native `reasoning_effort` mapping) — the ceiling is the
  configured answer allowance **plus `ReasoningHeadroomTokens` (8,192)**. The configured value is a
  promise about the *answer*, not about the thinking that comes first, so headroom is added on top
  rather than taken out of it. Default answer allowance: 16,384, i.e. **24,576** on the wire.
- On every other kind — and when the provider cannot be resolved — **no ceiling is sent unless the
  deployment explicitly configures one**. An OpenAI-compatible row may point at a self-hosted vLLM
  server whose context window is whatever its operator passed to `--max-model-len`; a ceiling
  ReelBolt invented there could turn a working turn into a 400.
- Anthropic is the exception by necessity: the Messages API *requires* `max_tokens` on every
  request, so the only choice there is between the SDK's built-in number and ours.

When the provider answers with the `length` stop reason, the reply is marked
(`AssistantChatLimits.ModelTruncationMarker`) before it is persisted or streamed — a half sentence
streamed as a finished answer is the defect that marker exists to close. The marker names the
ceiling ReelBolt actually sent, or omits the number when it sent none.

**The character cap runs after the call** and behaves as before: an assistant reply over
`Assistant:MaxMessageChars` is truncated to the cap with its own marker, because it lands in
`assistant_messages` and is replayed into every later prompt of that thread. When both bounds bite,
the character marker is what the reader ends on — the bound that actually shortened the visible text
is the one that gets to say so.

### How much the assistant thinks before it answers

A reply's cost is not only what the model writes — it is also what the model deliberates first, and
that half is invisible: thinking never appears in the reply, so all a user sees is a slow, expensive
answer. Every assistant turn is therefore run with an explicit reasoning effort
(`ChatOptions.Reasoning`), resolved by `AssistantChatLimits.ReasoningEffortFor` from
`Assistant:ReasoningEffort`.

**The default is `none` — thinking off.** That is the house default for interactive surfaces, not a
new opinion: the three room step configs already default their participants' `ReasoningEffort` to
`"none"`, and `ReelBoltAgentBase`'s remarks record the measured reason (it makes the deployed
vLLM/Qwen chat template skip the thinking scaffold entirely — 0 reasoning tokens, and a shorter
rendered prompt). The chat assistant was the one interactive surface still running at whatever the
provider defaults to, which is the most deliberation a reasoning-capable backend will do.

The vocabulary is `none`, `low`, `medium`, `xhigh` — the same set
`AgentReasoningEffort.Vocabulary` restricts agent turns to, and the same set that chat
template accepts, so the generic enum's `high` is deliberately not accepted. Raise it where the
added deliberation earns its latency: the assistant does agentic work, and `low` or `medium` is a
defensible choice for a deployment that would rather wait than have it guess.
`Assistant:ReasoningEffort=auto` sends no reasoning configuration at all — the request shape every
turn had before this key existed — for a provider that rejects the field outright.

| Setting | `OpenAICompatible`, `AzureOpenAI`, `Gemini` | `DeepSeek` | `Anthropic` |
|---|---|---|---|
| *(unset)* | `"reasoning_effort":"none"` | `"thinking":{"type":"disabled"}` | `"output_config":{"effort":"low"},"thinking":{"type":"adaptive"}` |
| `none` | `"reasoning_effort":"none"` | `"thinking":{"type":"disabled"}` | raised to `low` — see below |
| `low` / `medium` / `xhigh` | sent verbatim | sent verbatim (DeepSeek maps medium/xhigh to `high`) | sent as `output_config.effort` |
| `auto` | nothing sent | nothing sent | nothing sent |

DeepSeek's thinking is on by default and `reasoning_effort` cannot turn it off, so `none` there is the
`thinking` body field instead - see [deepseek-provider.md](deepseek-provider.md#reasoning-effort-and-turning-thinking-off).

**Anthropic has a floor of `low`, never `none`.** There, `ReasoningEffort.None` maps to
`thinking.type=disabled`, which a model that always thinks rejects with a 400 — "stop thinking so
much" would become "the assistant stops answering". `low` is Anthropic's own way to ask for less
(`output_config.effort`, thinking left adaptive), so that is what the floor sends. See
[anthropic-provider.md](anthropic-provider.md).

An unparseable value falls back to the default rather than to "no configuration", the same rule every
other `AssistantChatLimits` bound applies: a typo must never silently restore the maximal thinking
this setting exists to remove. The wire shape of each setting — that `reasoning_effort` really is
the field the provider reads, on both request families — is pinned by `AssistantReasoningTests`
against a stub transport, not asserted from the option object.

---

## Conversation threads

Conversations are persisted server-side. The client no longer sends history: it sends one new turn,
and the server loads the thread's prior turns in sequence order.

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/assistant/threads` | The caller's non-archived threads, most recently updated first. |
| `POST` | `/api/v1/assistant/threads` | Creates a thread; body `{ projectId?, title? }`, both optional. Returns the new id. |
| `GET` | `/api/v1/assistant/threads/{threadId:guid}` | One thread with its full, ordered history. |
| `GET` | `/api/v1/assistant/threads/{threadId:guid}/turn` | The thread's most recent turn's **state** — `idle` / `running` / `awaiting_approval` / `completed` / `failed`, with `turnId`, `messageId` and `pendingApprovalId` (absent when there is none). What a reopening client reads before re-attaching; a foreign or unknown thread is `404`. Never a turn's content. |
| `PATCH` | `/api/v1/assistant/threads/{threadId:guid}` | Renames and/or archives; body `{ title?, archived? }`. A null field is left unchanged, so a rename never implicitly un-archives. |
| `DELETE` | `/api/v1/assistant/threads/{threadId:guid}` | Deletes the thread and its messages. |

The endpoints are **caller-scoped, not route-scoped** — the assistant is mounted globally, so there
is no project in the URL to hang a conversation off. Every lookup filters on `UserId` inside the LINQ
predicate, so the ownership filter is applied by the database before anything is materialized.

**A thread id that exists but belongs to another user is `404`, never `403`.** A `403` would confirm
the thread exists to a user with no right to know that. The same rule applies to the chat transport
above.

`POST /api/v1/assistant/threads` drops a project the caller does not own rather than rejecting it, so
a page handing the assistant a stale or foreign project id cannot make conversation history
unopenable.

### Storage

`AddAssistantThreads` (`Migrations/20261002142456_AddAssistantThreads.cs`) creates two tables:

- **`assistant_threads`** — `id`, `user_id` (FK to `application_users`, cascade delete), nullable
  `project_id`, `title`, `created_at`, `updated_at`, nullable `archived_at`.
- **`assistant_messages`** — `id`, `thread_id` (FK to `assistant_threads`, cascade delete), `role`
  (`user`/`assistant`/`system`/`tool`), `content`, `sequence`, nullable `metadata_json` (jsonb),
  `created_at`.

`sequence` is **1-based and unique per thread** (unique index
`IX_assistant_messages_thread_id_sequence`), assigned as `max(sequence) + 1` when a turn is appended.
The assistant's turn is written with `userTurn.Sequence + 1`, so the user and assistant halves of one
exchange always take consecutive numbers. Appending a turn also touches the thread's `updated_at`,
which is what orders the thread list by real activity.

---

## Unanswered turns and turns that outlive the browser

A thread whose newest message is the user's looks the same in the database whether its reply is still
being written or its request died. Customers saw the second case as a question that silently
disappeared after they navigated away. The server now tells the two apart and keeps answering when it
can:

- **A turn outlives its browser, and re-attaches.** The turn is owned by the **process**, not the SSE
  connection: `ChatStream` runs the model on its own cancellation token, never the request's, and every frame
  is logged through a process-wide `AssistantTurnHub` that the HTTP response is only a **subscriber** to — the
  same shape `CLAUDE.md` § Workflow Execution Liveness records for workflow runs. When the client disconnects
  (panel closed, navigation, reload, a phone screen switching off) the turn keeps running and still persists
  its reply; writes to the dead stream are skipped instead of thrown. Reopening the chat reads
  `GET …/threads/{threadId}/turn` for the state and then **re-attaches** with
  `POST /api/v1/assistant/chat` `{ "threadId": "…", "attach": true }` — the same stream path and frame
  contract as a new turn, replaying the turn from its first frame and then streaming what is still arriving.
  An attach appends no message and starts no second turn; a thread with no live turn answers `404` and the
  client shows the stored history.
- **A turn nobody is attached to is bounded, one that someone is attached to is not.** "Survives a disconnect"
  must not mean "keeps a workspace's turn-concurrency slot and its credit hold forever". A turn is cancelled when
  it has been **unattended** for `Assistant:DetachedTurnSeconds` (default 300): the original connection is gone,
  no re-attached screen is reading it, and it is not held on a permission card. The clock restarts whenever a
  screen attaches or leaves or a card is answered, and never runs while a re-attached subscriber is reading or a card
  is open (a card has its own `Assistant:ApprovalTimeoutSeconds`). `Assistant:MaxTurnSeconds` (default 1800) still
  bounds every turn, attached or not.
- **A cut turn leaves a record, not silence.** The reply only exists when the whole tool loop is done, so there is
  no half-written text to save. When a turn is cancelled (the unattended ceiling or the hard wall clock), or fails
  after an action ran or with nobody watching, the thread gets an assistant message built by
  `AssistantInterruptedTurn`: "This answer was interrupted…" followed by each action card and where it ended
  (done, didn't go through, declined, no answer, or allowed but unconfirmed), or "Nothing was changed". It is also
  streamed to any re-attached screen. It never says "timed out" for a dropped connection.
- **The hub is bounded too.** A subscriber that stops reading is **dropped** (bounded channel, never an
  unbounded buffer behind a dead connection); the replay log is capped by frame count and by bytes, and a
  replay that lost frames starts with a `data-turnTruncated` frame so a client is told it is seeing a partial
  turn rather than silently getting less; a finished turn's log is evicted after a retention window, and a
  turn past its own wall clock is reclaimed **even if its `finally` never ran**, so a leaked turn cannot wedge
  a later re-attach. An attach read is bounded by the turn's remaining wall clock, so it returns rather than
  hanging.
- **The live turn is process-local.** `AssistantTurnHub`, like `AssistantTurnRegistry` and
  `AssistantApprovalBroker`, is in-memory: an API restart forgets live turns, so a re-attach afterwards
  answers `404` and the client falls back to the thread's persisted messages. Re-attach is **not** durable
  across a deploy.
- **A permission card survives a disconnect.** The gate does not watch the connection. A card left pending when the
  browser goes away stays open until its own timeout (`Assistant:ApprovalTimeoutSeconds`); a screen that comes back
  re-attaches, sees the same card replayed with the same arguments, and its click on
  `POST /api/v1/assistant/approvals/{id}` runs the tool. The tool still runs only on an explicit `allow` (or the
  user's own auto mode), never because a connection dropped; if the turn is cancelled while the card is open the gate
  emits an `expired` frame so a later re-attach does not show a card nobody can answer. The web client re-attaches
  by itself when the page returns to the front, the network comes back, or a reply ends broken or with a card
  waiting (`AssistantResumeOnReturn`).
- **A turn has a hard wall clock and a spend cut-off.** `Assistant:MaxTurnSeconds` (default 1800) cancels a
  turn, connected or not, and an MCP tool call; a connected chat gets "This took too long…" as a normal reply.
  Every model round-trip and tool call (and an MCP call) is pre-claimed at its worst case before it runs
  (round-trip: the request's estimated input plus its output ceiling; a Platform-billed tool: its quote) against
  the per-turn ceiling and what the workspace can pay for; a stopped turn ends with a plain reply and a
  `data-credits` part (`TURN_LIMIT` / `INSUFFICIENT_CREDITS`), and no tool runs after it. A round-trip over
  `Assistant:MaxRoundTripInputTokens` is refused; tool calls past `Assistant:MaxToolCallsPerStep` in one model
  step get a plain refusal. A workspace with `Billing:NonWorkflow:MaxConcurrentTurnsPerOrg` (default 4) charged
  turns in flight gets 429 `TURN_CONCURRENCY_LIMIT` (chat and MCP); a turn on the workspace's own chat model
  (BYO) is counted from its first Platform-billed tool call (vision, picture tools, design graphic, docs search),
  and refused there when the workspace is full. See [metering-and-billing.md](metering-and-billing.md#charging-policy-work-outside-workflows).
- **In-flight turns are visible.** `AssistantTurnRegistry` (process-wide) marks a thread while a turn
  on it runs. `GET /api/v1/assistant/threads` adds `awaitingReply` (newest message is the user's and
  nothing is answering it) and `replyInProgress` to each summary; `GET …/threads/{id}` adds
  `replyInProgress`. Both are appended with defaults, so older clients are unaffected.
- **"Ask again" never duplicates the question.** When a turn arrives whose text equals a trailing,
  unanswered user message that is not in flight, the server reuses that message instead of storing
  the question a second time (and replaying it twice into every later prompt). A double-send while a
  turn is still running appends as before.
- **The panel says which.** The History control is a labelled button; rows read "No reply yet" or
  "Still answering…"; opening the assistant shows a "has no reply yet — Open" hint for a waiting thread
  other than the current one; and a restored thread ending in an unanswered message shows either
  "Still working on your last message" (with *Check again*) or "No reply yet" with *Ask again*, which
  starts a run whose parent is that same message.

---

## Background work never blocks an upload or the assistant

Uploading a 233-file repository took ~15 minutes, and the first assistant question after it waited
minutes too. Two shared resources were the cause, and both are now protected.

**Uploads never wait for summarization.** `IBackgroundTaskQueue<T>` has no awaiting enqueue any more:
the upload, content-update and assistant file-write paths persist `SummaryStatus.Pending` and offer the
file with the non-blocking `TryQueue`. A full queue leaves the row Pending. `FileSummarizationService`
runs a sweeper shortly after startup and then every `FileSummarization:SweepIntervalSeconds` that
re-offers Pending user files (oldest first, never video/audio, never engine-written agent/output files)
and resets rows a previous process left in `Processing` for longer than `StuckProcessingMinutes`. The
queue tracks every id from enqueue until the worker completes it, so a sweep can never queue or run the
same file twice.

**The person at the keyboard goes first.** Summarization runs `MaxConcurrency` workers (default 1) on
the chat provider, and each yields to an in-flight assistant turn for up to `InteractiveYieldSeconds`
before its LLM call. Per-file indexing and the docs indexer yield likewise on the embedding server (see
[platform-docs-search.md](platform-docs-search.md#the-search-tool-never-holds-a-turn)). Yields are
always bounded, so a long chat slows background work down but never stops it.

| Key (`FileSummarization:*`) | Default | Meaning |
|-----|---------|---------|
| `MaxConcurrency` | `1` | Summarization LLM calls at once (1..8). |
| `QueueCapacity` | `100` | In-memory queue size; a full queue never blocks anything. |
| `SweepIntervalSeconds` | `45` | How often Pending/stuck rows are re-offered. |
| `InitialSweepDelaySeconds` | `10` | First sweep after startup. |
| `StuckProcessingMinutes` | `15` | When a `Processing` row from an earlier process is re-queued. |
| `InteractiveYieldSeconds` | `60` | Longest wait for an assistant turn before one summarization call. |

---

## The tool surface

The assistant has **exactly 153 tools** in the chat; the MCP catalog publishes **101**, because the chat-only `WatchExecution` is not offered over MCP, the two social pairs (schedule and cancel a post) are not offered over MCP at all, and the MCP surface collapses the other Propose/Apply pairs (see [Propose/Apply pairs collapse to one MCP tool](#proposeapply-pairs-collapse-to-one-mcp-tool)). `AssistantToolProvider.GetTools` composes them from twenty-five
lists, and `AssistantPromptConsistencyTests` asserts the prompt against the registration itself —
both directions, and the count — so a tool that is registered but undescribed, or described but not
registered, fails the suite.

| Group | Count | Source |
|-------|-------|--------|
| Core provider tools | 8 | `AssistantToolProvider` |
| Project | 13 | `AssistantProjectTools` |
| File | 11 | `AssistantFileTools` |
| Workflow | 8 | `AssistantWorkflowTools` |
| Knowledge | 3 | `AssistantKnowledgeTools` |
| Styles | 3 | `AssistantStyleTools` |
| Design system | 6 | `AssistantDesignSystemTools` |
| Picture editor | 15 | `AssistantPictureTools` |
| Outputs | 5 | `AssistantOutputTools` |
| Uploads and imports | 4 | `AssistantUploadTools` |
| Runs | 3 | `AssistantRunTools` |
| Revisions | 2 | `AssistantRevisionTools` |
| Change requests | 3 | `AssistantChangeRequestTools` |
| Workflow patches and clones | 4 | `AssistantWorkflowPatchTools` |
| Picture documents | 2 | `AssistantPictureDocumentTools` |
| Green-screen insert | 2 | `AssistantGreenScreenTools` |
| Media overlays | 2 | `AssistantMediaOverlayTools` |
| Object effects | 2 | `AssistantObjectEffectTools` |
| Audio cleanup | 2 | `AssistantAudioCleanupTools` |
| Voices | 4 | `AssistantVoiceTools` |
| Video enhance | 2 | `AssistantVideoEnhanceTools` |
| Avatars and objects | 12 | `AssistantAvatarTools` |
| Recipe-first | 4 | `AssistantRecipeTools` |
| Project history | 6 | `AssistantHistoryTools` |
| Community recipes | 4 | `AssistantMarketplaceTools` |
| Post text | 1 | `AssistantPostTextTools` |
| Video events | 2 | `AssistantVideoEventsTools` |
| Social publishing | 7 | `AssistantSocialTools` |
| Admin (gated) | 13 | `AssistantAdminTools` |
| **Total** | **153** (101 over MCP) | |

### Video events (progressive context)

Two free, read-only tools read the events a video analysis stored (`AssistantVideoEventsTools`, same names over MCP,
`readOnlyHint`, not gated, closed world): `ListVideoEvents` (events by `section` such as `c2`, by `types` and a `limit`,
strongest first; `source` is the analysed video's number) and `DescribeVideoWindow` (every event, weak ones too, between two
event ids, a coarse motion curve in words and the speech lines `t{n}` of the window). They read the newest analysis of the
project that found events, or the run named by `executionId`. Project access is checked on every call (view permission;
another tenant's project is absent, not refused), a quarantined artifact is never read, and an answer carries ids and
words only: no time, no frame, no storage key, no run id. A call returns at most 2,000 tokens and a turn may make at most 6
reads (`VideoEventReadAllowance`, one per `GetTools` call); the seventh answers `read limit reached`. Speech lines are
fenced as untrusted data. The planning agents (`VideoStoryEditor`, `SoundDesigner`, `MotionGraphicsPlanner`) get the same two
tools through the tool group `EventsRead` (`VideoEventsAgentTools`, reading only their own run's analysis). How the
outline and windows are built: [`docs/video-editing.md`](video-editing.md) "Outline and windows".


### Social publishing tools

Seven tools, in `AssistantSocialTools`, work on the workspace's connected social accounts and its posts (see [social-publishing.md](social-publishing.md)). Every one runs the app's own code: the reads call `SocialController`, `SocialPostsController` and `SocialMetricsController`; a proposed schedule runs the controller's preflight and plan check, and the applied one its create route (plan gate, account in the ACTIVE organization, output of the project, text limits, time rules); a cancel runs its cancel route. The publisher then checks everything again when the post fires. Replies name accounts, projects and videos by name; the ids a read returns are only what a later call passes back.

| Tool | Kind | What it does |
|------|------|--------------|
| `ListSocialAccounts` | read | The connected accounts: name, platform, status. Never a token. |
| `ListScheduledPosts` | read | The queue and past posts, newest first (`status`, `inProject`, `onAccount`, `take` 1 to 50) |
| `GetPostPerformance` | read | Totals and per-post counts over the last 1 to 30 days, or one post's readings. A count that is null is "not available", never 0 |
| `ProposeSchedulePost` / `ApplySchedulePost` | pair | Schedules a finished video (render or video project file) as a post on one account at a future time (`scheduledAt` with a Z or offset, or `scheduledLocal` plus `timeZone`); text from `title`/`body`/`hashtags` or a saved `postTextId` |
| `ProposeCancelScheduledPost` / `ApplyCancelScheduledPost` | pair | Cancels a post that is still scheduled |

**Always asked.** `ApplySchedulePost` and `ApplyCancelScheduledPost` are in `AssistantApprovalCatalog`'s always-ask set (with `ApplyRestoreProjectVersion`): the card appears even in auto mode and whatever the credit limit, and "always allow" on such a card does not switch auto mode on. The card names the account, platform, project, video and time and shows the whole post text, never shortened and never with ids.

**Authority per call.** The first step of each gated body re-reads Edit on the project (for a cancel, on the project of the post, found in the ACTIVE organization); nothing is remembered. An account or post of another organization, or one that does not exist, is the same "not found".

**Not over MCP.** The two pairs are not in the MCP catalog (`AssistantMcpToolSurface.NotOverMcp`), the old names are refused as unknown, and the bodies themselves refuse an MCP initiator and an unattended wake turn. Over MCP only the three reads exist (read-only annotations, closed world).

**Cost.** None of the seven calls a model; the cost table lists them as free.

### Project history tools

Six tools, in `AssistantHistoryTools`, work on a project's versions (see [project-versioning.md](project-versioning.md)). They need a project in scope and name versions by number and name, never by id.

| Tool | Kind | What it does |
|------|------|--------------|
| `ListProjectVersions` | read | Lists the project's versions |
| `GetProjectChanges` | read | Names what changed between a version and now, or between two versions |
| `ProposeSaveProjectVersion` / `ApplySaveProjectVersion` | pair | Saves the current state as a named version (1 to 80 characters) |
| `ProposeRestoreProjectVersion` / `ApplyRestoreProjectVersion` | pair | Restores named items (`scope` `item`, 1 to 50 names) or the whole project (`scope` `project`); a safety version is saved first |

**Forced card.** `ApplyRestoreProjectVersion` is in `AssistantApprovalCatalog`'s always-ask set, so its approval card appears even when the user is in auto mode.

**MCP project-scope refusal.** Over MCP the collapsed `RestoreProjectVersion` accepts `scope` `item` and refuses `scope` `project` with: "Restoring a whole project needs your click in the in-app assistant card or the History page; over MCP only single items can be restored." The caller's project permission is checked again on every call.

### Propose/Apply pairs collapse to one MCP tool

**The in-app assistant is unchanged.** It keeps all 153 tools, every `Propose*` / `Apply*` pair,
its approval cards and auto mode. The collapse lives only in the MCP catalog and the MCP call path
(`AssistantMcpToolSurface`): an MCP client already asks its own user before a destructive call, so
the second in-product approval step is not repeated on that surface.

Of the 40 Propose/Apply pairs, 39 are published as one callable tool. The MCP call runs `Propose*`
with the caller's arguments, reads the `confirmationToken` out of its JSON and runs `Apply*` with
that token in the same request. The token is minted and consumed inside the server call: it is not
declared in the MCP schema, is never returned to the client, and a value the caller supplies is
overwritten by the server-minted one. If the proposal refuses (an error, a lint finding, an authority
refusal), its JSON is returned unchanged and nothing is applied.

| In-app pair | MCP tool |
|---|---|
| `ProposeCreateFromTemplate` / `ApplyCreateFromTemplate` | `CreateFromTemplate` |
| `ProposeWorkflow` / `ApplyWorkflow` | `SaveWorkflow` |
| `ProposeCreateProject` / `ApplyCreateProject` | `CreateProject` |
| `ProposeUpdateProject` / `ApplyUpdateProject` | `UpdateProject` |
| `ProposeDeleteProject` / `ApplyDeleteProject` | `DeleteProject` |
| `ProposeSavePronunciation` / `ApplySavePronunciation` | `SavePronunciation` |
| `ProposeWriteProjectFile` / `ApplyWriteProjectFile` | `WriteProjectFile` |
| `ProposeEditProjectFile` / `ApplyEditProjectFile` | `EditProjectFile` |
| `ProposeInsertProjectFileLines` / `ApplyInsertProjectFileLines` | `InsertProjectFileLines` |
| `ProposeDeleteProjectFile` / `ApplyDeleteProjectFile` | `DeleteProjectFile` |
| `ProposeDeleteWorkflow` / `ApplyDeleteWorkflow` | `DeleteWorkflow` |
| `ProposeStopExecution` / `ApplyStopExecution` | `StopExecution` |
| `ProposeCreateInferenceProvider` / `ApplyCreateInferenceProvider` | `CreateInferenceProvider` |
| `ProposeUpdateInferenceProvider` / `ApplyUpdateInferenceProvider` | `UpdateInferenceProvider` |
| `ProposeDeleteInferenceProvider` / `ApplyDeleteInferenceProvider` | `DeleteInferenceProvider` |
| `ProposeAssignAgentSkills` / `ApplyAssignAgentSkills` | `AssignAgentSkills` |
| `ProposeSaveStyle` / `ApplySaveStyle` | `SaveStyle` |
| `ProposeAddTextLayer` / `ApplyAddTextLayer` | `AddTextLayer` |
| `ProposeEditPictureLayers` / `ApplyEditPictureLayers` | `EditPictureLayers` |
| `ProposeDeletePictureLayers` / `ApplyDeletePictureLayers` | `DeletePictureLayers` |
| `ProposeChangePictureCanvas` / `ApplyChangePictureCanvas` | `ChangePictureCanvas` |
| `ProposeRunPictureTool` / `ApplyRunPictureTool` | `RunPictureTool` |
| `ProposeDesignGraphic` / `ApplyDesignGraphic` | `DesignGraphic` |
| `ProposeSaveDesignSystem` / `ApplySaveDesignSystem` | `SaveDesignSystem` |
| `ProposeDecideDesignChange` / `ApplyDecideDesignChange` | `DecideDesignChange` |
| `ProposeSaveOutputAsFile` / `ApplySaveOutputAsFile` | `SaveOutputAsFile` |
| `ProposeImportFileFromUrl` / `ApplyImportFileFromUrl` | `ImportFileFromUrl` |
| `ProposeReviseComposition` / `ApplyReviseComposition` | `ReviseComposition` |
| `ProposeStepChange` / `ApplyStepChange` | `StepChange` |
| `ProposePatchWorkflow` / `ApplyPatchWorkflow` | `PatchWorkflow` |
| `ProposeCloneWorkflow` / `ApplyCloneWorkflow` | `CloneWorkflow` |
| `ProposeCreatePictureDocument` / `ApplyCreatePictureDocument` | `CreatePictureDocument` |
| `ProposeGreenScreenInsert` / `ApplyGreenScreenInsert` | `GreenScreenInsert` |
| `ProposeSetMediaOverlay` / `ApplySetMediaOverlay` | `SetMediaOverlay` |
| `ProposeObjectEffect` / `ApplyObjectEffect` | `ObjectEffect` |
| `ProposeCleanAudio` / `ApplyCleanAudio` | `CleanAudio` |
| `ProposeAddLibraryVoice` / `ApplyAddLibraryVoice` | `AddLibraryVoice` |
| `ProposeAddSoundEffect` / `ApplyAddSoundEffect` | `AddSoundEffect` |
| `ProposeEnhanceVideo` / `ApplyEnhanceVideo` | `EnhanceVideo` |
| `ProposeCopyMarketplaceRecipe` / `ApplyCopyMarketplaceRecipe` | `CopyMarketplaceRecipe` |
| `ProposeCreateObjectAvatar` / `ApplyCreateObjectAvatar` | `CreateObjectAvatar` |
| `ProposeExtractObjectFromVideo` / `ApplyExtractObjectFromVideo` | `ExtractObjectFromVideo` |
| `ProposeCreateAvatarVariant` / `ApplyCreateAvatarVariant` | `CreateAvatarVariant` |
| `ProposeImportLibraryAvatar` / `ApplyImportLibraryAvatar` | `ImportLibraryAvatar` |
| `ProposeGenerateSubjectClip` / `ApplyGenerateSubjectClip` | `GenerateSubjectClip` |

**The upload pair is a protocol, not an approval.** `ProposeUploadFile` hands out a presigned `PUT`
(or part list) and `ApplyUploadFile` records the bytes the client uploaded between the two calls, so
collapsing them would make uploads impossible. MCP publishes them as `BeginUpload` (read-only,
records nothing) and `CompleteUpload` (destructive). They are the only pair kept as two MCP tools.

**The old names are gone from MCP.** `Propose*` and `Apply*` are not in the MCP catalog and are
refused as unknown over MCP; the in-app assistant still uses them. `ExecuteWorkflow` and every read
tool keep their names. The arithmetic is 49 reads (the three social reads included) + `TestInferenceProvider` + `WritePostText` + 47 collapsed tools +
`BeginUpload` + `CompleteUpload` + `ExecuteWorkflow` = **101**.

**Every catalog entry carries `title` and `annotations`.** `title` is a short human label (never
empty, at most 64 characters), repeated as `annotations.title`. `annotations` also carries
`readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint`. Every tool is exactly one of
read-only or destructive: read-only when it changes no stored data (the reads and `BeginUpload`;
49 tools), destructive when it creates, updates or deletes data or starts a run (the 42 collapsed
tools, `CompleteUpload`, `ExecuteWorkflow`, `WritePostText` and `TestInferenceProvider`, whose probe writes the
capability's last-test columns; 46 tools). `idempotentHint` is true for
the read-only tools and for the writes that can safely be repeated (`CompleteUpload` returns the same
file; `SaveWorkflow` with the same steps answers `alreadyApplied`). `openWorldHint` is true where a
tool reaches a model, a vendor, an arbitrary URL or a queued run (`ExecuteWorkflow`, `RunPictureTool`,
`DesignGraphic`, `ReviseComposition`, `StepChange`, `ImportFileFromUrl`, `TestInferenceProvider`, the
two look tools, and the provider create/update tools).

**The `brief` parameter is a typed object, not free-form JSON.** `ValidateWorkflow`,
`ExecuteWorkflow` and `CreateFromTemplate` (and the in-app recipe pair behind it) declare the run
brief as an object with `language`, `sourceLanguage`, `lengthSec` (5 to 600), `format`,
`resolution` (`Auto`, `720p`, `1080p`, `2160p`), `stylePreset`, `styleId`, `style`, `tone`,
`designSystem` (`project` or `none`), `voice`, `sourceFiles` and `notes`. Every field is optional and
unknown fields are refused. The schema matches `RunBriefValidator`, which still owns the one set of
parse and validation rules; the declaration changed, not the behaviour.

**Refusals and authority are unchanged.** The MCP bridge still installs no approval gate. Every
collapsed call still re-checks authority per call inside the Apply body, and the `runs.watch` scope
guard is untouched. `DecideDesignChange(approve)` is still refused over MCP inside the tool body
(the tool is the collapsed `ProposeDecideDesignChange`/`ApplyDecideDesignChange` pair); `reject` is
allowed and records the note `via assistant (MCP)`.

**Any project, from any page.** Every project-scoped tool takes an optional `projectId`. `AssistantProjectScope`
is the one place that decides which project a call is about: an explicit id wins, otherwise the project open on the
user's page, otherwise the call is refused with a hint to find the project by name with `ListProjects`. Whichever id
wins is re-authorized through `IProjectAccessService` on that call with the permission the tool needs, and an unknown,
foreign or role-forbidden id gives the same refusal (never a distinguishable "forbidden"). The page's workflow and open
picture are only defaults for a call on the page's own project. `ListWorkflowExecutions` with no project at all lists
recent runs across the workspace. The MCP bridge benefits too: an external client can name any project by id.

Every registered name in the chat surface, marked **read-only** or **Propose/Apply**. The
Propose/Apply pair names below are in-app only; the MCP catalog publishes the single names in the
table above:


**Core provider tools (7)**

| Tool | Kind |
|------|------|
| `ListAgents` | read-only — agents available to this project (built-in plus this owner's custom) |
| `ListStepSchemas` | read-only — JSON Schema for every step type's config |
| `GetStepCatalog` | read-only — the step catalog generated from code: per step type (or all) purpose, cost class (never a price), needs with the linter rule that checks each, what it produces, run-brief fields it reads, pairings and common mistakes; one step type also returns its config schema |
| `ListProjectFiles` | read-only — this project's files |
| `ValidateWorkflow` | read-only — validates proposed steps and runs the pre-run linter over them (optional `projectId` and `brief`), writes nothing. Returns `valid` (true only with no error), `errors` (save-time problems) and `findings` (`{ruleId, severity, stepOrder, message, messageKey, args, fix {kind, label, labelId, args, patchId}}`, the shape of the lint endpoints; `message` is the English sentence, `messageKey`+`args` the tokens a localising client renders) |
| `ProposeWorkflow` | Propose — diff + confirmationToken, writes nothing. The linter runs on the proposal: an error about the graph's shape (not about the project's state, and for an edit not one the stored workflow already had) is refused as `{error, code: WORKFLOW_LINT_ERRORS, findings}` with no token, so it never reaches a card (`AssistantLintPolicy`). MCP publishes this pair as `SaveWorkflow`. |
| `ApplyWorkflow` | Apply — the write half; see the token carve-out below |
| `ExecuteWorkflow` | writes — starts an execution, subject to the rolling-hour run rate limit (paid and free buckets, see [Assistant execution rate limiting](#assistant-execution-rate-limiting)); returns a compact receipt that does not echo the request; takes `brief` (the typed run brief: `language`, `sourceLanguage`, `lengthSec`, `format`, `resolution`, `stylePreset`/`styleId`, `tone`, `designSystem`, `voice`, `sourceFiles`, `notes`; unknown fields are refused; the approval card lists each set field and adds `Not used by this app: …` for a field no step of the workflow reads, the linter's WL011 test) and `regenerateVideoClips` (bool?) — for a workflow with a `VideoGenerate` step, omitting it makes the tool refuse and return `needsDecision` plus the Max-Spend upper bound so the assistant asks the user (reuse = free, regenerate = paid) before re-calling; optional `styleId` / `stylePresetKey` (one of them) and `outputFormat` run it in an editing style and shape, and optional `outputResolution` (`Auto`/`720p`/`1080p`/`2160p`, short side; also `720`/`1080`/`2160`/`4k`, case-insensitive; unknown value → `{error}`) sets its resolution, all resolved by `RunStyleResolver` before the credit preflight (which prices the run at that resolution), the paid-clips question and the rate-limit slot (see [editing-styles.md](editing-styles.md)); a resolution above the org's plan `MaxOutputHeight` is refused when the run is queued and returns `{error, planGate}` (the 402 `PLAN_NOT_ENTITLED` body, feature `resolution`) and starts nothing; the approval card adds a `Resolution: {720p \| 1080p \| 4K (2160p)}` line for a choice other than Auto; optional `acceptEstimateCredits` (number of credits, at least the estimate's high figure) is read only for the "Build brand and product profile" template: over MCP it is required, and without it the tool refuses with `ESTIMATE_NOT_ACCEPTED` and the estimate (over the cap: `ESTIMATE_OVER_CAP`; no estimate: `ESTIMATE_UNAVAILABLE`), while in chat the user's approval is the acceptance |

**Project (10)** — `ListProjects` (read-only; optional `nameContains`, a case-insensitive filter so a project the user names is found without scanning the whole list), optional `tag` and `folder` (names, case-insensitive; rows carry `tags` as names and `folder` as a name). `ProposeCreateProject`/`ProposeUpdateProject` (and their Apply) take optional `tags` (names, never ids; missing ones are created in the caller's workspace, at most 5 new per call, at most 20 per project; on update omit to leave unchanged, `[]` removes all) and `folder` (a name, created if missing; on update omit to leave unchanged, `""` clears). Tags and folder are bound into the confirmation token and shown on the approval card, one line per tag and one for the folder, each name quoted; `GetProject` (read-only) returns them too. `ProposeCreateProject` /
`ApplyCreateProject`, `ProposeUpdateProject` / `ApplyUpdateProject`, `ProposeDeleteProject` /
`ApplyDeleteProject` (three Propose/Apply pairs), and `ProposeSavePronunciation` / `ApplySavePronunciation`
(a fourth pair: teach the project's narration how to say one word; `term` plus `spelling`, the word written with its
stress accent, e.g. `Novàra`, and optional `caseSensitive`. It upserts one entry of the project's pronunciation
glossary through the same service as the REST endpoint, needs project `Edit`, and the token binds the project and
the normalized term, spelling and case flag. The approval card reads "Say Novara as Novàra in narration" and notes
that subtitles and on-screen text are unchanged. Removal is UI-only).

**File** — `ReadProjectFile`, `ReadProjectFileLines` and `GetProjectFileOutline` (read-only);
`ProposeWriteProjectFile` / `ApplyWriteProjectFile`, `ProposeEditProjectFile` / `ApplyEditProjectFile`,
`ProposeInsertProjectFileLines` / `ApplyInsertProjectFileLines` and
`ProposeDeleteProjectFile` / `ApplyDeleteProjectFile` (four Propose/Apply pairs). The targeted tools are
described in [Targeted file reads and edits](#targeted-file-reads-and-edits).
The three read tools also read a PDF: its text comes from the import-worker (`POST /v1/pdf-text`, up to
`IMPORT_WORKER_PDF_MAX_PAGES` pages, shared per-version cache in `ProjectFilePdfText`), `ReadProjectFile` caps it at 60,000 characters with
`truncated`, `source: "pdf-text"` and a `note` that names the pages left out ("only the first N of M pages") only when the worker says some were, `ReadProjectFileLines` slices it, and
`GetProjectFileOutline` reports `{ kind: "pdf", totalChars, totalLines }`. A PDF with no text, one over 20 MiB,
an unreadable one or a worker that is down answers `{ error, code }` with `PDF_NO_TEXT`, `PDF_TOO_LARGE`,
`NOT_A_PDF`, `PDF_UNREADABLE`, `PDF_READER_UNAVAILABLE`, `PDF_READER_BUSY` or `IMPORT_WORKER_BAD_RESPONSE` and
a plain sentence saying what to do. The write and edit tools still refuse a PDF. The text is untrusted file
content like any other. Reading is free.

**Workflow (6)** — `ListWorkflows`, `GetWorkflow` (a workflow's complete steps in the exact
`CreateWorkflowStepRequest` shape `ValidateWorkflow`/`ProposeWorkflow` accept, so it can copy a workflow
or change one setting), `ListWorkflowExecutions` and `GetWorkflowExecution` (run history; per-step
status, timings, credits charged, failure code and class, `outputId`, the full error up to 8000
characters, and an output preview truncated to 1500 characters; see
[Run results, waiting and credits](#run-results-waiting-and-credits)) — all read-only and ownership-checked like every project-scoped tool;
`ProposeDeleteWorkflow` / `ApplyDeleteWorkflow`; `ProposeStopExecution` / `ApplyStopExecution`, which stop a Queued or Running run through the same stop event as the REST `POST .../executions/{id}/stop` (leases, holds and events behave identically). It needs Edit on the run's project; a run of a project the caller cannot use, or a missing one, reads as "execution not found". A run that already finished answers `alreadyFinished` / `alreadyStopped`, not an error. The card names the workflow and project and says the run stops, work in progress is lost and credits already used are not refunded (a stopped step is classed `UserCancelled`, which keeps what was consumed; see [metering and billing](metering-and-billing.md)); it shows the credits used so far from the ledger. The MCP bridge installs no gate, as for every write; over MCP this pair is the single `StopExecution` tool.

**Knowledge (3)** — `SearchPlatformDocs` (semantic search over the shipped `/docs` tree, see
[platform-docs-search.md](platform-docs-search.md)), `ListWorkflowTemplates` and `GetWorkflowTemplate`
(the built-in recipe catalog). `ListWorkflowTemplates(goal?, projectId?)` returns, per recipe, its `title`, `tagline`,
`category`, `forGoals`, `needs`, `costClass`, `briefFieldsRead`, `suggestedStylePreset` and the steps, plus, when a
project is known (named, or open on the page), `satisfied` and `missing` computed from the same readiness facts the
gallery uses; `goal` puts the best matches first and sets `matchesGoal`. `GetWorkflowTemplate` returns ready-to-propose
steps with each agent type resolved to the built-in agent row's id, exactly as applying the template from the UI does,
plus the `appManifest`, the `briefBindings` (which steps read each run-brief field, and the fields none reads) and
`availablePatches` (each closed patch with whether it fits). All read-only and need no project in context (a project
the caller names but cannot use reads as `project not found`, like everywhere else).

**Recipe-first (4)** — `GetProjectReadiness`, `EstimateWorkflowRun` (both read-only and free: no model call, no hold),
and `ProposeCreateFromTemplate` / `ApplyCreateFromTemplate` (docs/design/workflow-apps.md section 6; see
[Recipe-first tools](#recipe-first-tools)). MCP publishes the pair as `CreateFromTemplate`.

**Styles (3)** — `ListStyles` (read-only: the built-in presets, the caller's saved styles usable in
the project in context, the output formats and the `outputResolutions` a run accepts; needs no project) and `ProposeSaveStyle` /
`ApplySaveStyle` (create a saved style, or replace one the caller owns; the confirmation payload binds
the target's id and **current** name plus the normalized name, description, project pin and sanitized
style, recomputed from the database on apply). Another user's style is reported as not found. See
[editing-styles.md](editing-styles.md) "Assistant tools for styles".

**Design system (6)** — `GetDesignSystem` (read-only: a project's brand design system and product
profile — the current approved version, its colours, fonts, tagline and written guidance (summary, tone, do and
don't rules, vocabulary), any draft and the recent version history) and `ListDesignChanges` (read-only: the pending changes, each with the `changeName`
the decision tools take). `ProposeSaveDesignSystem` / `ApplySaveDesignSystem` create a Draft from the
current approved document plus whichever of the colours (`#RRGGBB`), heading/body fonts, tagline,
written guidance (`guidanceSummary`, `tone`, `dos`, `donts`, `vocabulary`; a list replaces the whole list)
and brand assets (`logoProjectFileIds`, `iconProjectFileIds`, `previewProjectFileIds`) are passed, all
optional; the confirmation token binds the normalized values, the asset id arrays and the base version,
so a draft cannot be applied over a system that has moved on. An asset array lists project file ids
found with `ListProjectFiles`: the first is the primary file and the rest become named variants in
order, a non-empty array replaces the slot, an omitted one keeps it and an empty one clears it. Every
id must be a non-quarantined image file of the same project (png, jpeg, webp, gif or svg); a foreign,
missing or non-image file is refused by name. Library assets cannot be attached by the assistant yet:
a project linked to a brand library must attach those on the library page. `ProposeDecideDesignChange` / `ApplyDecideDesignChange` approve or reject
one pending change by its `changeName` (two changes that would read the same get a `(#n)` suffix,
oldest first); over MCP these two pairs are `SaveDesignSystem` and `DecideDesignChange`. The approve
path **refuses** while the user has auto mode on, when the call comes
through the MCP bridge, and when the account row cannot be read (D-7) — a brand change only ever
rides on the user's own click on the in-app approval card or the Design system page — while a
rejection is always allowed (it records the note `via assistant`, or `via assistant (MCP)` over MCP).
The approval cards for the two Apply tools are built from the database and the exact arguments.
See [design-system.md](design-system.md).

**Outputs (5)** — `GetOutput`, `LookAtOutput` and `GetMusicBeats` (all read-only; see [`GetMusicBeats`](#reading-a-music-files-beats-getmusicbeats)) and `ProposeSaveOutputAsFile` / `ApplySaveOutputAsFile`. **Uploads and imports (4)** — `ProposeUploadFile` / `ApplyUploadFile` and `ProposeImportFileFromUrl` / `ApplyImportFileFromUrl`. Over MCP these are `SaveOutputAsFile`, `BeginUpload` / `CompleteUpload` (the upload protocol kept as two tools) and `ImportFileFromUrl`. See [Moving files and outputs in and out over the MCP](#moving-files-and-outputs-in-and-out-over-the-mcp).

**Runs (3)** — `GetExecutionStatus` and `GetCreditBalance` (read-only) and the chat-only `WatchExecution`, which arms a wake-up (see [Async run wake](#async-run-wake)). **Revisions (2)** — `ProposeReviseComposition` / `ApplyReviseComposition`, which start a revision of a finished animated video (MCP: `ReviseComposition`; see [composition-revisions.md](composition-revisions.md#revisions-from-the-assistant-and-the-mcp)). **Change requests (3)** — `GetChangeOptions` (read-only), `ProposeStepChange` / `ApplyStepChange` (MCP: `StepChange`): redo narration lines of a finished run (new wording, another voice, speed, or how a word is said) (see [change-requests.md](design/change-requests.md)); the Apply half is approval-gated in the chat, counts in the paid bucket with source `revision`, and a film-only change goes through `ProposeReviseComposition` instead. Both tools also take an optional `changes` list (up to 6 entries of `{stepOrder or stepResultId, itemIds, instruction, overrides}`; a film entry carries only `stepOrder` and an `instruction`) to change several steps of the same run at once: in the chat, one review, one combined estimate, one approval card listing every change, one new version; over MCP the one `StepChange` call returns `{executionId, changeRequestId, status, rateLimit}`. Send either the single-step arguments or `changes`, never both. In the chat the confirmation token covers the whole list, so any edit between Propose and Apply is refused (see [change-requests.md](change-requests.md#several-changes-in-one-request)); over MCP the two halves run in the one `StepChange` call. See [Run results, waiting and credits](#run-results-waiting-and-credits).

The run page presents the film and the steps as one feature, the **Change this video** card, and the assistant does the same: `ProposeReviseComposition` changes the video alone, and `ProposeStepChange` with `changes` changes steps, optionally with the film as one entry. The tool counts are unchanged.

**Workflow patches and clones (4)** — `ProposePatchWorkflow` / `ApplyPatchWorkflow` (MCP: `PatchWorkflow`) edit a few settings of an existing workflow without resending it: up to 50 operations (`setStepConfig` takes a JSON merge patch of one step's config where `null` deletes a key, `renameStep`, and `setStepField` for an allowlist of scalar step fields). The patched steps go through exactly the validation and diff of `ApplyWorkflow`; the proposal returns only the changed steps and their changed fields, and the approval card lists those fields. The token is the organization-bound one and covers the complete stored steps, so any change since the proposal is "stale, re-propose". `ProposeCloneWorkflow` / `ApplyCloneWorkflow` (MCP: `CloneWorkflow`) copy a workflow into the same project or another project of the workspace the caller can edit; a workflow that continues a composition or reads step results of the source project cannot move to another project. **Picture documents (2)** — `ProposeCreatePictureDocument` / `ApplyCreatePictureDocument` (MCP: `CreatePictureDocument`) open an image output (a thumbnail or extracted frame) or an image project file as a new picture in the thumbnail studio and return `{ documentId, name, width, height }`; a video is refused with a pointer to an `ExtractFrame` step. **Green-screen insert (2)** — `ProposeGreenScreenInsert` / `ApplyGreenScreenInsert` (MCP: `GreenScreenInsert`) create (or update, by name) a workflow from the `green-screen-insert` template for a plate video and the content to show; it uses no AI model (only processing time is charged) and starts no run, so `ExecuteWorkflow` stays the run gate.

`ListProjectFiles` takes `pathPrefix`, `mimePrefix`, `since`, `nameContains`, `limit` (100 by default, 500 at most) and an opaque `cursor`, and answers `{ projectName, files, total, nextCursor?, truncatedSummaries }` with each summary cut to 300 characters (`ReadProjectFile` returns the whole one). `ExecuteWorkflow` accepts `briefFileId`, a text file of the same project (up to 64 KB) used as the run's brief; with `userRequest` the request follows it under "Notes for this run:".

**Documentation is also injected.** Besides the tool, every turn's system prompt carries the
documentation sections most similar to the user's latest message (`PlatformDocsContextBuilder`,
best-effort, bounded by a timeout and a character budget, never persisted to the thread). See
[platform-docs-search.md](platform-docs-search.md).

**Guardrails.** The prompt is a customer-support brief with non-negotiable boundaries — see [Guardrails, names and the documentation boundary](#guardrails-names-and-the-documentation-boundary).

**Admin (13, in two authorities)** — the tools registered by `AssistantAdminTools`. They do not share one "admin" bit: **ten are platform-scoped** and **three are organization-scoped**, and the two gates are independent (see [Per-call authority](#per-call-authority-platform-and-organization)). `TestInferenceProvider` runs the same connection test as the admin page's Test button (both go through `InferenceProviderTester`: same unsupported-combination and private-endpoint guards, same per-capability probe, same persisted `LastTestAt`/`LastTestOk`/`LastTestError`); it is not a Propose/Apply pair because it changes no configuration, only the last-test columns, and its result's error text is key-scrubbed. `ListInferenceProviders`, `ListAgentsAdmin` and `GetDecisionCalibration` are read-only (the last reads the WorkflowEngine's calibration report for the caller by forwarding the caller's own bearer token, so the engine's admin check applies to the same identity, and it only ever recommends — it has no write counterpart);
`ProposeCreateInferenceProvider` / `ApplyCreateInferenceProvider`,
`ProposeUpdateInferenceProvider` / `ApplyUpdateInferenceProvider`,
`ProposeDeleteInferenceProvider` / `ApplyDeleteInferenceProvider`,
`ProposeAssignAgentSkills` / `ApplyAssignAgentSkills` (four Propose/Apply pairs; over MCP they are
`CreateInferenceProvider`, `UpdateInferenceProvider`, `DeleteInferenceProvider` and
`AssignAgentSkills`, so the 13 registered tools publish as 9 MCP entries when enabled). `ListSkills` and the skill pair read and write one organization's own agents, so they are the three organization-scoped tools; the other ten are platform-scoped.

**Picture editor (15)** — `DescribePictureDocument`, `ListPictureTools` and `LookAtPicture`
(read-only; the last sends one layer's stored pixels to the deployment's Vision provider and reports
what the picture actually shows), and six
Propose/Apply pairs: `ProposeAddTextLayer` / `ApplyAddTextLayer`, `ProposeEditPictureLayers` /
`ApplyEditPictureLayers`, `ProposeDeletePictureLayers` / `ApplyDeletePictureLayers`,
`ProposeChangePictureCanvas` / `ApplyChangePictureCanvas`, `ProposeRunPictureTool` / `ApplyRunPictureTool`
and `ProposeDesignGraphic` / `ApplyDesignGraphic`. Over MCP the six pairs are `AddTextLayer`,
`EditPictureLayers`, `DeletePictureLayers`, `ChangePictureCanvas`, `RunPictureTool` and
`DesignGraphic`. See [Picture editor tools](#picture-editor-tools).

That is 49 read-only tools (the three social reads included), `TestInferenceProvider`, `WritePostText`, 50 Propose/Apply pairs (100 tools) and
`ExecuteWorkflow` in the chat — 153 with `WatchExecution`. The MCP catalog publishes 49 read tools,
`TestInferenceProvider`, `WritePostText`, the 47 collapsed pair tools, `BeginUpload` and `CompleteUpload`, and
`ExecuteWorkflow` — **101**; the two social pairs are not in it. Its 50 read-only entries and 51 destructive entries cover every tool.

No sandbox, shell, filesystem or render tool is ever granted to the assistant itself. (`ApplyDesignGraphic` in
the chat, `DesignGraphic` over MCP,
queues a run of the platform's own graphic designer, which works in ITS sandbox under the platform's rules; the
assistant never touches that sandbox.) The only tool that writes file content
(`WriteProjectFile` over MCP, `ApplyWriteProjectFile` in the chat) goes through `IFileStorageService.UploadAsync`, never the filesystem.

### Scope guard

`AssistantAdminTools` is also the boundary of what the assistant deliberately does **not** do:
user-management tools (create/update/delete user, password reset) are absent, blocked on a product decision
(the Inference-to-Go hop is closed), and workflow-service admin actions are absent because the WorkflowEngine exposes
read-only status only (`GET /api/v1/workflow-engine/status` and
`GET /api/v1/workflow-engine/skills/status`, both admin-only).

---

## Moving files and outputs in and out over the MCP

An MCP client has no browser session, so four families of tools move bytes without one. None of them ever returns a storage key, a bucket name or a presigned download URL; the only URLs that leave the server are the two below.

### Output links: `GetOutput`

`GetOutput(outputId, projectId?)` needs View on the project. `outputId` is either a step result id (a run's render, listed as `outputId` in `GetWorkflowExecution` and `GetExecutionStatus`) or a project file id. It returns:

```json
{
  "projectName": "...",
  "output": { "id": "...", "kind": "render", "name": "...", "mimeType": "video/mp4", "sizeBytes": 0,
              "durationSec": 0, "width": 0, "height": 0, "executionId": "...", "stepOrder": 0, "stepLabel": "..." },
  "link": { "url": "...", "expiresAt": "..." },
  "frames": { "status": "ready", "thumbnail": { "url": "..." },
              "contactSheets": [ { "url": "...", "cols": 0, "rows": 0, "densitySec": 0, "firstTimeSec": 0, "thumbCount": 0 } ] }
}
```

`kind` is `render` or `file`. Tile `i` of a contact sheet shows the time `firstTimeSec + i * densitySec`. `frames.status` is `ready`, `pending` (the frames did not exist, so the tool published `MediaDerivativesRequested`; call again in a minute) or `unavailable`.

`link.url` is an **anonymous capability URL**: `GET /api/v1/output-links/{token}` (`OutputLinksController`, `[AllowAnonymous]`). The token is `base64url(payload).base64url(signature)`; the payload carries only a version, the kind (`StepResult`, `ProjectFile` or `Derivative`), the ids, the project, the user, an optional sheet index and the expiry. The signature is `IConfirmationTokenService` under the operation `OutputLink.v1`, verified in constant time. The storage key is resolved on the server **at fetch time**, from the database row, after re-checking that the user still exists and is still a member of the project's organization with View. Consequences:

- Expired, tampered, replayed-after-removal, other-organization and key-outside-prefix all answer the same `404` body, never bytes.
- The route streams with `Range` support and `SafeContentPolicy`, with `Cache-Control: private, no-store`.
- The API never logs the token. It is a bearer secret in the path, so any proxy in front must not write the request URI of this route to its access log; the bundled nginx does not (see the nginx route below).
- Hand the link to the user or fetch it immediately; do not store it.

| Config key | Default | Meaning |
|---|---|---|
| `OutputLinks:DefaultTtlMinutes` | `15` | Lifetime of a link when the caller names none; clamped to 60 minutes |
| `OutputLinks:PublicBaseUrl` | *(empty)* | Origin put in front of the link. Empty falls back to `CloudStorage:PublicBaseUrl` (compose `PUBLIC_BASE_URL`), else the URL is relative to the API |

The maximum lifetime is fixed at 60 minutes (`OutputLinkService.MaxTtl`).

**nginx route.** `nginx/locations.conf` has a dedicated case-insensitive regex location `~* ^/api/v1/output-links/` (the API routes case-insensitively, so a case variant must not fall through to `/api/v1/`). It writes the access log with `output_links_log` (`nginx/nginx.conf`), which prints `/api/v1/output-links/-` and omits the URI, query and referer, so the token never reaches the access log (nginx's error log still quotes the request line on an upstream failure and cannot be redacted from there). It applies the `output_links` limit zone, per client IP at 120 requests a minute with `burst=60 nodelay` (a player issues many Range requests per view, which `auth_public`'s 5r/m would starve), sets `auth_basic off` so the staging basic-auth front door does not block a link opened from a chat or a media tag, and blanks `Authorization` (no cookie is translated). It proxies with `proxy_buffering off` and `proxy_cache off`, HTTP/1.1, a 10 s connect timeout and 5 minute read and send timeouts, and passes Range requests through. `nginx/tests/forwarded-headers.sh` checks that a token sent to the route is absent from the nginx logs.

### Looking at an output: `LookAtOutput`

`LookAtOutput(outputId, question?, atSeconds?, projectId?)` needs View on the project and resolves `outputId` like `GetOutput` (a step result id or a project file id). It sends frames of the output to the deployment's Vision provider and returns the model's answer, so a caller can check what is on screen (cut-off, overflowing or tiny text) without a person watching the render.

- **Video:** up to 6 full-size stills (`MaxLookImages`), evenly spaced from the first to the last frame, or at `atSeconds`. The times are **clamped to the video's own length before they are keyed on** (S-74), so 70 s and 80 s of a 60 s render are one ask for its last frame rather than two extractions over the same pixels; the answer states the length as `durationSec` and, when anything was clamped, the `clampedTimes` and a sentence naming which time read as what. The stills are a `MediaDerivativeKind.Stills` derivative, made on demand only (up to 12 JPEGs, long side at most 1280 px) and cached by source and times. When they do not exist yet, the tool publishes `MediaDerivativesRequested` and returns `{ "status": "pending", "retryAfterSeconds": 10 }`; call again with the same arguments.
- **One extraction per ask (S-74):** an identical ask (same project, output and clamped times) publishes nothing for 30 seconds and answers `pending`, whichever member made it — the second extraction would produce the same pixels. `StillsExtractionGate` also bounds one caller at 20 extractions in a rolling 10 minutes, then refuses with `{ "error", "code": "STILLS_REQUEST_BUDGET_EXHAUSTED", "retryAfterSeconds" }` and publishes nothing. Process-local, like `AssistantTurnRegistry`: a courtesy bound on expensive work, not an authorization rule.
- **Picture** (png or jpeg): looked at directly.
- **Without `question`** it describes the frames and points out cut-off, overflowing, too small or edge-hugging text and anything that looks broken.
- **Result:** `{ projectName, output { id, name }, lookedAt [{ index, atSec }], durationSec?, clampedTimes?, note?, model, answer, truncated }`; image N in the answer is `lookedAt[N-1]`. The answer is capped like `LookAtPicture` (`MaxLookAnswerChars`) and treated as untrusted data to quote, never an instruction. With no Vision provider it answers "no vision service is set up".
- **Bounds (S-76):** `question` is at most 2,000 characters (`MaxLookQuestionChars`; longer is refused before anything is requested or sent), and a picture or still whose header declares more than 40 megapixels (`MaxLookPixels`, as for `LookAtPicture`) is refused and not sent.
- **Failure (S-75):** the source lookup, the derivative writer, object storage and the vision provider all fail sometimes. Anything that escapes the tool is answered with ONE fixed sentence and `code: "LOOK_AT_OUTPUT_FAILED"` ("The output could not be looked at just now…"), and the exception's own text — a storage key, an internal host, an SDK message — goes only to the server log.
- **Cost:** one small model call, metered under the turn's own usage scope like `LookAtPicture`. It is pre-claimed (C1, S-76) at its vision model's quote for what it will send (`AssistantOutputTools.SizeLookOutputAsync`): one still per requested time, or 6, of at most 1280 x 1280 px each, or the picture at its header size, plus the text and the 1,024-token output ceiling. When the call cannot be sized it is claimed at its worst case (one 40 MP picture or 6 stills, whichever is dearer, with the longest text); the flat unpriced default only when no vision model can be quoted.

### Writing the post text: `WritePostText`

`WritePostText(outputId, platforms, tone?, language?, about?, hashtagSource?, projectId?)` needs Edit on the project and writes the text to post with a finished video, one version per platform (`tiktok`, `instagram_reels`, `youtube_shorts`, `linkedin`, `x`). It runs the app's `PostTextsController.Write` itself, so the plan, the credit hold and settle, the hourly cap and the moderation screen are the app's. It returns `{ httpStatus, result }` and saves nothing (a saved version goes through the app's save route); a refusal keeps the route's status (`402`, `409`, `429`, `503`, `424`, `422`). It is annotated non-read-only and open-world (it spends credits) and has no approval card, no workflow step and no Propose/Apply pair. It is text only: no `readVideo`, because the assistant has no price to show first and cannot start a transcript; "also read the video" is in the app's dialog. `hashtagSource: "web"` sends the project name and the about text as a search query to the workspace's web search provider. Hashtags are suggestions and are never called trending. Details: [`post-text.md`](post-text.md).

### Reading a music file's beats: `GetMusicBeats`

`GetMusicBeats(fileId, projectId?)` (View, free) answers "what is the tempo of this music?" for one project music or video file, using its id from `ListProjectFiles`. It reads the cached `beats` analysis of the per-file analysis API; `MediaAnalysisAccess` (`Services/Editing`) is shared with `MediaAnalysisController`, so the tool and the route use the same source lookup, held check and queueing.

- **States.** `ready` carries the grid: `fileName`, `bpm` (one decimal), `tempoWord`, `confidence` (two decimals), `steady`, `beatsPerBar`, `durationSec`, `analysedSec`, `dropSec`, `outroSec`, `barCount`, `barTimesSec` and `truncated`. `working` means the analysis is queued or running. `failed` carries a plain `reason`. `notAnalysed` means there is no analysis and the caller can only view. `started` means the tool just queued the analysis and the model should call again shortly.
- **Authority.** View reads a stored result. With no usable result, a caller with Edit starts the free beat analysis (no credits, no hold) and gets `started`; a caller with View only gets `notAnalysed` and nothing starts. The refusals are the media analysis route's: a file held for review answers with the output surface's held sentence, a quarantined file with the moderation sentence, an unknown file with "file not found", and a file that is not audio or video says it cannot be analysed.
- **Bounds.** `barTimesSec` lists the start times of the first 64 bars (two decimals); `barCount` and `truncated` say whether more exist. The per-beat list is never returned.
- **Annotations.** Read-only and free, in `AssistantPlatformTools` (Free) and `AssistantToolAnnotations`. The reply is marked untrusted data and holds no ids, only the file name and numbers.
- **Tests.** `AssistantMusicBeatsToolTests` covers the five states, View reading versus Edit starting, and the refusals.

### Saving a render as a file: `SaveOutputAsFile`

Copies a finished render (a step result with an output under `outputFiles`) into the project's `userFiles` as a new project file, so a later run can use it as a source, an insert or a music track. The copy is made on the server, so deleting either side never breaks the other. The tool binds the project, the step result, the target path and the size into a confirmation token that the server mints and consumes inside the call; it needs Edit, re-verifies the token, checks the storage quota (`IPlanGateService.CheckUploadAsync`) and writes nothing if the quota would be exceeded. It returns `{fileId, fileName, directoryPath, sizeBytes}`. In the chat this is the `ProposeSaveOutputAsFile` / `ApplySaveOutputAsFile` pair. For a step that should register its own output in place, use the step option in [video-editing.md](video-editing.md#registering-any-steps-output-as-a-project-file).

### Uploading bytes: `BeginUpload` / `CompleteUpload`

This is the one Propose/Apply pair MCP does not collapse, because the two calls are a binary protocol: the client uploads bytes between them.

`BeginUpload(fileName, sizeBytes, contentType?, directoryPath?, projectId?)` needs Edit and a verified email. It calls `IDirectUploadService.CreateAsync` exactly like the REST upload-url route (the same quota check, the same caller key) and returns `{projectName, uploadToken, method: "PUT", url or parts, partSizeBytes, partCount, headers, expiresAt, maxBytes, instructions}`. It writes nothing.

- Up to 256 MiB (`DirectUploadLimits.SinglePutMaxBytes`) is a single `PUT` to `url`.
- Larger files come back as `parts: [{partNumber, url}]`. All parts are presigned in one call, and a file that would need more than 100 parts (`DirectUploadService.MaxPartUrlsPerCall`) is refused with a clear message.
- The presigned `PUT` URL is the one sanctioned exception to the "no presigned URL" rule: it is write-only, short-lived and for a server-chosen key, and the key itself is not echoed.

The client then `PUT`s the bytes (each part, keeping each `ETag` response header) and calls `CompleteUpload(uploadToken, parts?: [{partNumber, etag}], projectId?)`. Completion uses the storage's own size (HEAD), re-checks the quota, runs the same post-complete side effects as the REST route (text indexing and summary, media derivatives, activity; one shared service) and is idempotent: repeating the call returns the same file. It returns `{fileId, fileName, directoryPath, sizeBytes, mimeType}`. In the chat these are `ProposeUploadFile` and `ApplyUploadFile`.

### Importing from a link: `ImportFileFromUrl`

For a file that is already on a public https URL. The tool validates only the shape (https, no `user:password`, a host that passes `GeneratedClipUrlGuard.EnsureAllowed`), then downloads synchronously and stores the file in `userFiles` (`IUrlImportService`). The token over the project, URL and target path is minted and consumed inside the call. In the chat this is the `ProposeImportFileFromUrl` / `ApplyImportFileFromUrl` pair. The REST twin is `POST /api/v1/projects/{id}/files/import-url` with `{url, fileName?, directoryPath?}` (Edit, verified email; `201` `ProjectFileResponse`).

| Rule | Behaviour |
|---|---|
| Scheme and host | https only; the host must be public. Private, loopback, link-local, CGNAT, documentation, Teredo, 6to4, NAT64 and IPv4-embedded IPv6 addresses are refused, checked on **every connect**, so a rebinding host or a redirect to a private host is refused too. `GeneratedClipUrlGuard` is the one implementation, deny by default |
| Redirects | At most 3 (`UrlImportService.MaxRedirects`); each target is re-validated |
| Size | `FileImport:MaxBytes` (default 536870912, 512 MiB). A larger declared `Content-Length` is refused before the body is read; a body that outgrows the cap is aborted and the partial object deleted |
| Time | `FileImport:TimeoutSeconds` (default 300) for the whole download |
| Quota | Checked against the declared size up front and the real bytes afterwards; nothing is stored on failure |
| Name and type | `fileName`, else the last segment of the URL path (sanitised; `..` refused); mime from the extension, else the response `Content-Type`, else `application/octet-stream` |
| Hygiene | No cookies or `Authorization` are sent; logs carry the scheme and host only, never the path or query (signed URLs carry secrets) |

Errors are `{error, code}` with `IMPORT_URL_REFUSED`, `IMPORT_TOO_LARGE`, `IMPORT_FAILED` (with `upstreamStatus`), `IMPORT_TIMEOUT` or `STORAGE_QUOTA_EXCEEDED`. The REST route answers `400`, `413` or `422` with `{message, code}` and never `502` or `504`, which Cloudflare would replace with its own page.

## Targeted file reads and edits

Internal background. The assistant changes part of a text file without re-sending the whole file. The tools
mirror the agents' `ReadProjectFileLines`, `GetProjectFileOutline` and `EditProjectFile` and the sandbox's
`ApplySandboxFileEdits` / `InsertSandboxFileLines`; one shared engine in `ReelBolt.Shared` does the matching, so
the three surfaces behave the same. The assistant prompt tells the model to prefer these over
`WriteProjectFile` for a partial change.

| Tool | Writes? | Arguments | Authority |
|---|---|---|---|
| `ReadProjectFileLines` | No | `fileId` or `path`, `startLine` (default 1), `lineCount` (default 400, clamped to 1..2000) | Project `View` |
| `GetProjectFileOutline` | No | `fileId` or `path` | Project `View` |
| `EditProjectFile` (chat: `ProposeEditProjectFile` / `ApplyEditProjectFile`) | Yes — one MCP call | `fileId` or `path`, `edits: [{oldText, newText, replaceAll?}]` | Project `Edit` |
| `InsertProjectFileLines` (chat: `ProposeInsertProjectFileLines` / `ApplyInsertProjectFileLines`) | Yes — one MCP call | `fileId` or `path`, `afterLine` (0 = top, -1 = append), `text` | Project `Edit` |

`ReadProjectFileLines` returns the slice with a numbered gutter. Authority is checked per call; a project or
file the caller cannot see is absent, not forbidden.

Edit rules:

- `oldText` is matched exactly and ordinally, never as a regular expression.
- Each `oldText` must occur once unless `replaceAll` is true.
- The edits are all-or-nothing: if one fails, none is applied and the result is `{"ok": false, "error": ...}`.
- Text files only, and the result must be at most 1 MiB.

Limits: at most 50 edits per call, each `oldText` and `newText` (and an inserted `text`) at most 64K characters, and the
file and the result at most 1 MiB. A replace-all whose result would exceed the limit is refused before anything is built.
`path` is the stored path; a bare file name works when only one file has it, and several matches give
`{"ok": false, "error": ...}` asking for the full path. Reads see the whole file up to 1 MiB, so `totalLines` is true;
the returned text is cut at 60,000 characters on a line boundary (`truncated`), and a file over 1 MiB is flagged.

In the chat the pair runs in two steps: Propose reads the current file, dry-runs the change and returns a confirmation token with counts (hunks,
lines removed and added, sizes); the diff itself is shown on the approval card, not returned by Propose.
The token binds the project, the file, the hash of the base content and the exact edits (or the exact insert).
Apply re-reads the file, re-verifies the token and refuses if the file changed since the proposal; the model must propose again.
Over MCP, `EditProjectFile` and `InsertProjectFileLines` run both halves in one call, so the token never reaches the client.
On success the tool edits the file **in place** through the same path as `WriteProjectFile`: the file row and its stored
object are overwritten, the previous contents are replaced and cannot be restored, so the chat card is shown as a destructive change.

Known limits. The check against a changed file and the save are two steps, not one transaction: `project_files` has no row
version, so a second writer with Edit who saves the same file in the instant between Apply's re-read and its save is
overwritten (over MCP both halves run inside one request, but the same window exists between them; closing it needs a
concurrency token and a migration on a shared table; it affects integrity only, and the
second writer is already an authorized editor). The line diff behind Propose and the chat card is bounded: when more than
`MaxDiffedLines` (20,000) lines differ the whole differing middle is one hunk without an edit script, and the card refuses
it as too large to review ("split it into smaller edits"). Outline patterns are non-backtracking (matching time is linear in the
line, so a file's whole outline costs time linear in its size), have a 100 ms match timeout as a second bound, and lines
longer than 2,000 characters are skipped. A line-ending change on one line of a file that stays mixed (some CRLF, some LF)
is not shown when the edit also has a visible change; a switch of the file's style, or an edit with no visible change at
all, is shown or refused.

The approval card for either chat Apply shows the real diff, built from the database file and the Apply arguments: the file
path, the project name, line numbers and the removed and added lines, bounded (see
[Permission cards and auto mode](#permission-cards-and-auto-mode)).

## Run results, waiting and credits

**`GetWorkflowExecution`** carries, per step: `stepOrder`, `label`, `stepType`, `status`, `durationMs`, `tokensUsed`, `creditsCharged` (credits, three decimals, `null` when none was recorded), `startedAt`, `completedAt`, `fromCache`, `producedVideo`, `outputId` (the step result id when the step produced a playable output; the id `GetOutput` accepts), `failureCode`, `failureClass` and `errorDetails` (up to 8000 characters). At the top level: `errorMessage` (up to 8000 characters), `totals {creditsCharged, creditsHeldOrUnsettled?, durationMs, steps, passed, failed}` and `warnings[]`. A warning is one plain sentence for each step whose `outputResolution` report says `honoured: false`, for example "Step 4 (Compile) produced 1344x768; the requested 2160p was not applied: ...". See [video-editing.md](video-editing.md#output-format). When the run has a `VideoGenerate` step, the top level also carries `videoBudget` for the run's project (`VideoBudgetSnapshot.ForRun`, a read-only view over `external_generation_jobs` that applies the same accounting as the engine's budget gate): `{unit: "credits", dailyCapCredits, usedTodayCredits, remainingTodayCredits, resetsAt, limitedBy}`, or `{unit: "gpuMinutes", dailyCapGpuMinutes, usedTodayGpuMinutes, remainingTodayGpuMinutes, resetsAt, limitedBy}` for a ComfyUI run. It reports the binding cap, the one with the least room left (`limitedBy`: `project`, `plan` or `platform`); `resetsAt` is the next 00:00 UTC. When the platform cap binds, the cap and used figures are left out and only the remaining room, the reset and `limitedBy` show. The field is absent for a run without video generation. A failed `VideoGenerate` step explains itself in `errorDetails`; see [video-generation.md](video-generation.md#the-budget-gate). The REST `StepResultResponse` carries the same new fields (`CreditsCharged`, `FailureCode`, `FailureClass`, `StepOrder`, `StepLabel`, `StepType`).

**`GetExecutionStatus(executionId, projectId?)`** answers at once and never waits. It needs View on the run's project and refuses another organization's run exactly as `GetWorkflowExecution` does. It returns `{executionId, status, terminal, progress {stepsTotal, stepsDone, currentStep {stepOrder, label, stepType}}, creditsCharged, creditsHeldOrUnsettled?, costNote, errorMessage?, outputs: [{outputId, stepOrder, label}]}`. While the run is going it also returns `retryAfterSeconds` and a `hint`; the caller should not poll in a loop. **Real cost after a run:** `creditsCharged` is the settled sum of the run's step charges (already net of refunds: a platform-failed step charges 0), never the estimate. `creditsHeldOrUnsettled` appears only when some step metered credits it could not yet settle, and then the figure is not final. `costNote` says which it is ("so far" while running, final once finished). With billing off `creditsCharged` and `costNote` are null and no cost is claimed. `GetWorkflowExecution` carries the same `costNote` and `totals.creditsHeldOrUnsettled`. The prompt tells the assistant to state these, not the proposal's estimate. `WaitForExecution` is removed from the catalog. `POST /api/v1/assistant/tool-call` still accepts the name as a thin alias that answers like `GetExecutionStatus` plus a `deprecated` field.

**`WatchExecution(executionId, projectId?)`** is chat-only (not offered over MCP). It records a watch in `assistant_run_watches` for the current thread so the assistant is woken when the run ends, and the assistant ends its turn. For a run that already finished it answers `{watching: false, terminal: true, ...}` and points at `GetWorkflowExecution`.

**`GetCreditBalance()`** reads the balance of the organization of the active token, from the same service as `GET /api/v1/billing/summary`, and any organization role may call it. With billing off it returns `{billingEnabled: false}`; otherwise `{billingEnabled: true, balance, reserved, available, plan, renewsAt?}` in whole credits.

**`INSUFFICIENT_CREDITS` refusals.** `ExecuteWorkflow`, `ReviseComposition` (`ApplyReviseComposition` in the chat) and the REST routes refuse an unaffordable run with `code: INSUFFICIENT_CREDITS`, a plain `message` ("This run needs up to 788 credits to start (it usually costs about 286); the workspace has 500 available, so 288 more are needed. Credits that aren't used are returned when the run finishes.") and `missing` (credits still needed) alongside `required`, `balance`, `low`, `high` and `typical`. `required` is the hold the engine takes, not the usual cost. See [the pre-run estimate](metering-and-billing.md#the-pre-run-estimate-what-is-shown-versus-what-is-held).

## Async run wake

When a run the assistant watches reaches `Passed`, `Failed` or `Cancelled`, the platform writes one user turn into the thread and the assistant answers it, whether or not the tab is open.

- **Files.** `Services/Background/AssistantRunWakeService.cs` runs a sweep every `Assistant:RunWake:SweepIntervalSeconds`. `Services/Assistant/AssistantRunWakeSweeper.cs` expires watches, releases lapsed claims, finds watches whose run is terminal and claims each. `AssistantWakeTurnRunner.cs` drives the real `AssistantChatController.ChatStream` on a synthetic `HttpContext` whose principal is rebuilt from the database. `AssistantRunWakeText.cs` builds the text.
- **Authority.** Everything the wake relies on (thread, membership, role, project access) is re-read from the database at wake time, never from the watch row. A watch whose authority no longer holds is cancelled.
- **Claim.** A single conditional `UPDATE` claims a watch, so a replica that races another cannot fire it twice; a claim lasts longer than the longest turn (`Assistant:MaxTurnSeconds` plus the approval wait plus two minutes, 42 minutes by default) before a dead replica is presumed gone. Every later write to the watch is guarded by the claim, so a stale replica cannot overwrite a re-claimed row. Wakes run in parallel, at most one per thread. A turn that ran but left no reply is run at most three times in all.
- **Text.** The wake turn is a fixed template with the status word and the run reference. No workflow name, error, step output or file content reaches the model through it. The workflow's display name and the wake marker live only in the stored message metadata.
- **Approvals.** The wake is a normal assistant turn, so it follows the user's approval setting: a tool that needs a permission card waits for the user, and with auto mode on it runs on its own within the usual per-request credit limit. The always-ask tools (provider create, update and delete, agent skill assignment, a link import, a restore, a scheduled or cancelled post) keep their card in a wake turn too, so they never run with nobody there.
- **Hub limits.** The turn hub (re-attach, who is writing) is process-local. A reply written by another replica appears when the thread is next loaded.

| Key | Default | Notes |
|-----|---------|-------|
| `Assistant:RunWake:Enabled` | `true` | `false` stops new watches and the sweep. |
| `Assistant:RunWake:SweepIntervalSeconds` | `5` | Seconds between sweeps; `0` switches the sweep off. |
| `Assistant:RunWake:WatchTtlHours` | `24` | A watch older than this expires without waking. |
| `Assistant:RunWake:BatchSize` | `10` | Watches handled per sweep. |
| `Assistant:RunWake:MaxConcurrentWakes` | `3` | Wake turns that may run at once on one replica (never two on the same thread). |
| `Assistant:RunWake:MaxPendingPerThread` / `MaxPendingPerOrganization` | `3` / `20` | Open watches one thread and one workspace may hold. |
| `Assistant:RunWake:MaxWakesPerThreadPerDay` | `20` | Wakes per thread per day. |
| `Assistant:RunWake:MaxConsecutiveWakes` | `3` | Wakes in a row with no user message between them. |
| `Assistant:RunWake:MaxAttempts` | `5` | Tries before a failing wake is given up. |

**Unread replies.** `assistant_threads.read_through_sequence` marks the newest message a user has seen. `GET /api/v1/assistant/activity` returns `writing` (this caller's live turns, from the process-local hub), `unread` (threads whose newest assistant message is above the marker, within 7 days, at most 20) and `pendingWatches`. It carries titles only, never message content, and is cached for 2 seconds per caller and workspace. `POST /api/v1/assistant/threads/{threadId}/read` advances the marker. The web app polls every 5 seconds while a turn is writing or a watch is pending and every 30 seconds otherwise (`web/components/assistant/useAssistantActivity.ts`). The header badge adds approvals and unread replies. `AssistantReplyToast.tsx` shows "Assistant is writing a reply...", "picking up ... now that your run finished" and "Assistant replied". `AssistantBackgroundNotifier.tsx` and `web/lib/assistant/browser-notify.ts` add an opt-in browser notification: off by default, permission asked only when the user switches it on, and it shows only the conversation title.

**Turn budget.** `Assistant:MaxToolIterations` defaults to 80 (the library default was 40), so a wake that reads a long run fits. `Billing:NonWorkflow:AssistantTurnCeilingCredits` defaults to 100; the hold (`AssistantTurnHoldCredits`) stays at 5. `TurnBudgetValidation` fails the boot when the hold exceeds the ceiling or `AssistantMinTurnCredits` exceeds the hold.

**MCP run notifications.** `mcp/src/runSubscriptions.ts` serves the resource `reelbolt://runs/{executionId}[?project={projectId}]`. A client that subscribes receives `notifications/resources/updated` when the run changes and, if it passed a progress token, `notifications/progress` with the steps done. Notifications are best effort: a client without subscription support calls `GetExecutionStatus` or runs a watch command: the Claude Code plugin's `reelbolt-watch` (below), or `reelbolt-mcp-watch` for stdio self-hosters. Bounds (HTTP mode): `MCP_MAX_SUBSCRIPTIONS_PER_SESSION` (5), `MCP_MAX_SUBSCRIPTIONS_PER_PRINCIPAL` (10), `MCP_MAX_SUBSCRIPTIONS` (100, whole process), `MCP_SUBSCRIPTION_TTL_MINUTES` (120) and `MCP_MAX_SESSIONS_PER_PRINCIPAL` (20).

`reelbolt-mcp-watch <executionId> [--project <id>] [--max-minutes 120]` (`mcp/src/watch.ts`) polls `GetExecutionStatus` and prints one plain line when the run changes. It needs `INFERENCE_API_URL` and `REELBOLT_MCP_TOKEN`. Stdout carries only the lines; diagnostics go to stderr and the token is never printed. Exit codes: `0` when the run finished, `1` for an authentication failure, lost contact, a missing variable or the time limit, `2` for bad arguments. It stays for stdio self-hosters; the plugin does not use it.

The Claude Code plugin ships its own watcher, `plugins/reelbolt/bin/reelbolt-watch.mjs` (dependency-free Node 22+ ESM; commands `login --server <mcp url>`, `logout`, `status` and `<executionId> --project <id> [--max-minutes N]`), started under Claude Code's Monitor. It does its own OAuth 2.1 sign-in against the same authorization server as the plugin's MCP connection (discovery, dynamic registration of a loopback public client, authorization code with PKCE S256, callback on `127.0.0.1`) and asks only for the scope `runs.watch`. A `runs.watch` token reaches `POST /api/v1/assistant/tool-call` and, in the bridge controller, only the tool `GetExecutionStatus`; the catalog, every other tool and every REST route answer `403 mcp_token_scope`. The credential is `watch-credentials.json` in `$REELBOLT_WATCH_HOME`, else `$XDG_CONFIG_HOME/reelbolt`, else `~/.config/reelbolt` (Windows `%APPDATA%\reelbolt`): directory `0700`, file `0600`, refused on read if group/other can read it. It holds the server origin, client id, refresh token and a short-lived access token; refresh rotation is serialized by a lock file because a reused refresh token revokes the grant. The token is never printed or put in argv, is sent only to the stored origin, and redirects are refused. `logout` calls `/oauth/revoke` and deletes the file. Exit codes: `0` finished, `1` problem or time limit, `2` bad arguments, `3` sign-in needed (`not signed in`, `sign-in expired`, `sign-in unusable`).

## Enhance a video: upscale, slow motion, denoise, motion blur, extend

`AssistantVideoEnhanceTools` (`Agents/Tools/AssistantVideoEnhanceTools.cs`) adds one Propose/Apply pair, `ProposeEnhanceVideo` / `ApplyEnhanceVideo`, published over MCP as `EnhanceVideo` (destructive, title "Enhance a video"). Both call the same `IVideoEnhanceService` as the editor's `/enhance` routes, so the plan feature, height cap, length cap and credit preflight answer the same codes.

- **Source.** Exactly one of `fileId` (a project file) or `outputId` (a finished render); an unknown or foreign one answers `not found`. Authority is Edit on the project, re-read on every call.
- **`ProposeEnhanceVideo`** reads the estimate, writes nothing and mints a confirmation token over `{operation, canonicalPayload, userId}`. The summary names the operation, the source and the project (never ids) and gives seconds, output size and frame rate, and the price in credits or "free".
- **Consent rule.** The assistant never records the project's video consent (`projects.video_enhance_consent_at`) and takes no consent argument. A hosted operation (AI upscale or slow motion) with no consent on file answers `NEEDS_CUSTOMER_OK` ("the customer must agree once in the editor"), mints no token and writes nothing. Local operations leave the host never, need no consent and are free.
- **Extend (hosted only).** Both tools accept op `Extend` with exactly one of `targetAspect` (`16:9`, `9:16`, `1:1`, `4:5`) or the four margins `marginLeft`, `marginRight`, `marginTop`, `marginBottom` (each 0 to 0.5), and an optional `prompt` (up to 500 characters); margins must be one amount on the sides that grow (uneven margins are refused). The approval card shows the sanitized, truncated prompt so the customer approves what is sent. The prompt is sanitized (control characters stripped, trimmed, length-checked with `INVALID_REQUEST`) before anything else; only the sanitized text is put in the request, the confirmation-token payload and the run, and it is not repeated in the summary. The approval card and summary name the new shape in plain words ("extend the picture to vertical 9:16", "wider on the left and right"). A source below the model's frame floor answers `SOURCE_TOO_SHORT` (422) and one over its ceiling `SOURCE_TOO_LONG` (413), exactly as the route does. The tool count is unchanged (the same pair); a local Extend is refused like the route refuses it.
- **`ApplyEnhanceVideo`** re-runs the gates, verifies the token (a changed argument answers `stale confirmationToken, re-propose and try again`) and starts the hidden `VideoEnhance` run. It returns the `executionId` and does not wait; use `GetExecutionStatus` or `WatchExecution`.
- **Approval card.** Built from the database and the exact arguments: the operation in plain words, the source name, seconds, output size and frame rate, and "Free" or the credits. A paid card carries `CostCredits`, so auto mode still asks above `Assistant:AutoApproveCostCardCredits`.
- **Progress captions.** "Checking the video and what enhancing it would produce" and "Starting the video enhancement" (`AssistantVideoEnhanceTools.Captions`).

## Allowing media to be sent for one run

The saved step switch is one thing; the permission for a single run is another. An app whose steps send the user's
media to an AI service has a field of its own, `allowMediaEgress` (listed by `GetWorkflow` with `permission` set and the
steps it is for). `ExecuteWorkflow` takes it in `brief.inputs` like any app field, and it applies to that run only: no
saved step is written and the field itself cannot be created, bound, defaulted or removed by any tool.

- **In chat** a run whose brief sets `allowMediaEgress: true` always gets an approval card, in auto mode and on a turn
  the platform woke as well. The card adds the line "Allows sending your media to AI services for this run: ..." with the
  steps and what each sends, built from the stored steps. Approving it is the consent; declined or expired, nothing runs.
- **Over MCP** (stdio token or OAuth token) it is taken only while the calling user has switched on "Let connected tools
  (MCP) allow sending my media to AI services" in Settings (`application_users.mcp_media_egress_enabled`, off by default,
  read on every call). Otherwise the tool answers `{"error": "media_egress_not_allowed", "message": "Allow connected
  tools to send your media in Settings > Assistant, or run this from the app page.", "inputId": "allowMediaEgress"}` and
  starts nothing. No OAuth scope is involved.
- A run that needs the permission and does not set it is refused with `APP_INPUT_MISSING` (`inputId:
  "allowMediaEgress"`) before anything is spent.
- The result of a run that set it carries `mediaEgress: { allowed: true, steps: [{ stepName, sends }] }`, and the run's
  record says who allowed it ("Approved in the assistant", "Allowed by a connected tool").

These are the rules of the one per-call media permission, `IMediaEgressCallPermission`
(`Services/Avatars/MediaEgressCallPermission.cs`), which every surface asks: workflow runs, picture operations, subject
clips, variants and object extraction, on REST and in the tools. A REST call counts as the app only for the person's
own session on that surface's own route (an API key needs `media.egress`; a scoped OAuth token, the engine and a
background worker never); the assistant runs a surface inside its chat request, so only an allowed card counts there,
and only on a context the chat controller built (the workflow-assistant route never).

The assistant must never say media was not sent for a run that allowed it. Design record:
[`design/app-inputs.md`](design/app-inputs.md) "Permission to send media as an app field".

## The footage-sharing consent on workflow edits

`allowSourceMediaEgress` on a `VideoAnalyze`, `VideoGenerate` or `AudioCleanup` step is the customer's
permission to send that step's footage (the source clip for segmentation, keyframe images, the audio
for hosted cleanup) to an outside service. The in-app assistant turns this saved switch on only when the user asks, and
only under an approval card the person allows (below); a connected tool never does; a single run can be given the permission another way, see "Allowing media to
be sent for one run". Every assistant and MCP path that
writes steps (`ProposeWorkflow`/`ApplyWorkflow`, `ProposePatchWorkflow`/`ApplyPatchWorkflow`, so MCP
`SaveWorkflow` and `PatchWorkflow`; checked again on the stored rows at Apply) runs
`AssistantEgressConsentGuard.Evaluate(proposed, stored)`:

- **A flip needs the user's consent.** A step proposing the flag on (any depth, any case, `"true"` included) where
  the stored step at that order had no top-level grant is a grant request; a new workflow has no stored grant,
  so every new step that proposes it is one. It is allowed only with consent, otherwise it is refused with
  `egress_consent_is_the_customers` and nothing is written:
  - **In chat**, only through an Apply approval card that names each such step and what it sends
    (`ApprovalCard.AllowsMediaEgress`). That card is always asked, in auto-approve mode too, and never auto.
    The consent is the ambient `AssistantMediaEgressApproval`, set only by the gate after a human click on that
    card. The confirmation token alone never grants, and `ChatApproved` alone is not consent.
  - **Over MCP never.** The user's MCP media setting (Settings > Assistant, `McpMediaEgressEnabled`) allows one run
    at a time (`allowMediaEgress` on `ExecuteWorkflow`), not a saved step permission; the surface `WorkflowEdit`
    refuses MCP whatever the setting, and the refusal says the permission is saved in the app or from the in-app
    assistant with its card.
  - At Apply every step the edit would grant must be one the approved card listed (the gate hands the card's step
    orders to the call, `AssistantMediaEgressApproval.ListedSteps`); a step the card did not list (the stored workflow
    changed after the card) is refused and nothing is written.
  - Text from files, tool results or fetched pages can never cause a grant: it can only make the assistant
    propose one, which reaches the user as a card. The tool counts are unchanged (153 chat / 101 MCP).
- **A grant is kept per step**, when nothing that decides what THAT step sends changed. Steps are
  compared in the form they would be stored (`WorkflowEditDiffService.ToStep`, so a default the
  caller left out equals the stored default), every field but the label and the top-level flag:
  1. the step itself, at the same `StepOrder`, same type and agent: its sources (project file, step
     reference, brief role), segmentation targets and prompts, provider ids, keyframe references,
     cleanup target, limits, every other config field;
  2. every step it reads, directly or transitively, by `WorkflowStepDependencyGraph` built on both
     the proposal and the stored steps ("when in doubt it depends": `PreviousStepOutput` and unreadable
     configs read every earlier step); the guard does not load agent definitions, so a model step
     (`Agent`, `Parallel`, `ForEach`, rooms, `ReviewLoop`) counts as reading every step before it;
  3. every control-flow step (`ReviewLoop`, `ForEach`, `Conditional`, any `LoopTargetStepOrder`)
     anywhere in the workflow, since a loop can run the step again.
- **Otherwise the grant is reset**, not refused (a context change without a card): that step's flag is written as false and the result
  carries a `needsBeforeRunning` entry `WL015` naming that step only. An insert or delete before the
  step or a reorder moves its order, so it resets. A step that keeps its grant is not reported.

So patching a compile step's framing keeps the grant of an analysis that reads its own file, while an
analysis that sends that compile's film is reset. A copy into another project (`ProposeCloneWorkflow`)
and a workflow the assistant creates from a recipe start with every grant off (`ResetAll`). Tests:
`AssistantEgressConsentGuardTests`.

## App inputs: an app's own fields

**F3 (docs/design/app-inputs.md 29).** `GetChangeOptions` also lists `runSettings` (the page's run settings: key, label, value in
words, supported or why not, what a change redoes); `ProposeStepChange` / `ApplyStepChange` (MCP `StepChange`) take `brief` (run
settings: language, length, voice, files, notes, design, card sound, avatar; shape, size, style and tone refused with
`BRIEF_FIELD_NOT_CHANGEABLE`) and accept `inputs` and `brief` together with `changes` (one review, one estimate, one run; the
token covers all three; `inputs` alone keeps the L9 token). The MCP collapsed descriptions of `SaveWorkflow` (`appManifest`),
`PatchWorkflow` (the four app-page operations), `StepChange` (`inputs`, `brief`) and `CreateFromTemplate` (`brief.inputs`) gain one
sentence each, only while the real tool takes the argument (`AssistantMcpToolSurface.AppInputNotes`). No tool added: 129 chat, 87 MCP.

Always on (design: [`docs/design/app-inputs.md`](design/app-inputs.md) 13 and 23; the switch `Workflows:AppInputs:Enabled` was removed 2026-11-10). No
tool was added for it; the counts stay 129 chat and 87 MCP.

**Filling the fields to change a run.** `GetWorkflow` adds `appInputs` for an app whose page declares any: each field's
id, type, label, help, example, whether it is required, default, min/max/unit, options, `underMoreOptions`, `showWhen`,
`usedBy` (where it is used, in words: `video prompt of step 1 "Make the clip"`, `the request sent with each run`, `chooses
the path at step 1`, `the model of step 2`) and, for a `model` field, `modelOptions` from the run page's own source
(`AppInputModelOptionsService`: each option's id, name, `recommended`, `price` in words from `costIndex` with the run page's
thresholds, `available`, `unavailableReason`). `ExecuteWorkflow` (chat and MCP) takes `brief.inputs` (`{id: value}`; a model
field takes an option id): the brief schema declares `inputs` only with the switch on. The run gate is REST's
(`RunBriefSplitter` with `AppInputRunStart`: `APP_INPUT_MISSING`, `APP_INPUT_INVALID`, `APP_INPUT_MODEL_UNAVAILABLE`
before anything is spent), and from there the tool gates and prices the **bound** steps on the **chosen** branch
(`RunBriefResolution.PricingOr`, the same `RunStartPricing` REST uses): the lint and footage gates get the bound steps and
the charged orders, the 402 preflight calls `IWorkflowExecutionPreflight.CheckStepsAsync` inside
`AppAgentModelAmbient.Enter(AgentModels)` (so a chosen agent model prices its cold-start chat), and the paid-clips question
only counts the clips of the chosen branch. The run card (`AssistantApprovalCardBuilder.RunAsync`) prices through
`PriceStepsAsync` with the same steps, lists each value by the field's label (a choice and a switch by their words, a model
by its name from `model_capabilities`, never an id) and says how many steps the chosen path leaves out.
`EstimateWorkflowRun` prices `brief.inputs` the same way (`CheckAsync(..., forEstimate: true)`, `EstimateChargedAsync`
over the reachable steps); a recipe estimate takes its defaults' branch.

**Setting up a request page.** `ProposeWorkflow`/`ApplyWorkflow` (MCP `SaveWorkflow`) and `ValidateWorkflow` take an
optional `appManifest` (the page, as REST takes it on create and update, read with `AppManifestJson.Options` so an unknown
member is refused). Every save path (`ValidateWorkflow`, `ProposeWorkflow`, `ApplyWorkflow`, the patch and clone tools,
the set-media-overlay tool, `ProposeCreateFromTemplate`, and the REST twin `WorkflowAssistantController`) validates with
`StepConfigSaveValidator.Validate(steps)` and `WorkflowAppSupport.CheckForSave(supplied, stored, facts,
bindingSteps)`, the REST rules: placeholders and `appInputs` maps only where the `[AppBindable]` allowlist allows them, every
input declared, the page checked against the steps it lives with. The legacy `ApplyWorkflow` token is extended, not
replaced: with no page it is byte-for-byte today's; with a page it is `SHA-256(token | digest(page) | digest(stored page))`
(`AssistantAppInputs.WorkflowToken`), so a page changed between Propose and Apply is stale. A page-bearing proposal is not
handed to the page's review tray (`LastProposedDiff` stays null), whose apply carries steps only.

`ProposePatchWorkflow`/`ApplyPatchWorkflow` (MCP `PatchWorkflow`) gain four operations
(`AssistantWorkflowPatchTools.AppInputs.cs`):

| Op | Members | Effect |
|---|---|---|
| `setAppInput` | `input` (one field object; `kind` defaults to `input`) | Adds or replaces the field by id; the page is written at version 2. The first page edit of a workflow without a stored page starts from its derived page, so the brief fields and file slots stay |
| `removeAppInput` | `id` | Removes the field, every `appInputs` entry naming it, each `{{input.id}}` (replaced by its text default or nothing), its request-template lines and other fields' `showWhen` on it. Refused while a Conditional reads it |
| `bindAppInput` | `stepOrder`, `field`, `inputId`, `mode` (`placeholder` / `value`, default from the allowlist), `template` | Placeholder: writes `template` (must contain `{{input.<inputId>}}`, default the bare placeholder) at the setting's path (`a.b[0].c`); value: sets `appInputs[field] = inputId` (`@model` for the model). A setting off the allowlist is refused naming the allowed ones |
| `setAppPage` | `title`, `tagline`, `order` (input ids), `requestTemplate` (`""` removes it) | Edits the page; `order` reorders the app inputs in their own slots |

The plan then runs the same save checks; a changed page is validated in full, an unchanged one re-checked against the
patched steps. The token payload is exactly today's when the page does not change; with a change it also binds the stored
page and the page to store. The proposal carries `appChanges`, plain sentences built by the tool from the stored page, the
new page and the steps' own labels (`AssistantAppInputs.DescribeChanges`: `Adds the field "What happens in the video"
(long text, required) to the app page.`, `Uses "Clip length" as the clip length of step 1 "Make the clip".`,
`Removes the field …`, the request template, the title); the approval cards for `ApplyWorkflow` and `ApplyPatchWorkflow`
list them first (`AppChangeLines`), and the no-proposal fallback has an arm per operation. The allowlist is the binder's only
vocabulary, so consent (`allowSourceMediaEgress`), spend caps and provider ids cannot be bound whatever the model writes;
`AssistantEgressConsentGuard` is unchanged.

`GetStepCatalog` adds each card's `bindableFields` with the switch on. WL028 (a required field with no value) is about a
run's values, not the graph, so it never blocks a proposal (`AssistantLintPolicy`).

**Recipes.** `ListWorkflowTemplates`, `GetWorkflowTemplate`, `EstimateWorkflowRun` (recipe path) and
`ProposeCreateFromTemplate`/`ApplyCreateFromTemplate` (and their card) read the edition the switch selects
(`ListRecipesAsync`, `AssistantRecipeTools.ResolveRecipe`): with it on, the merged `avatar-clip` is offered and the two old
avatar keys resolve to it with their own defaults (`ManifestForCreate`, Lite mode on for `talking-avatar-budget`); the
created workflow stores the page as provisioning resolves it (agent-model fields pointed at the built-in agent).
`ProposeCreateFromTemplate`'s `brief.inputs` become the page's defaults (an id the recipe's page does not have is refused).
`BuildDryStepsAsync` reads the edition too.

**Prompt.** `AssistantAgent.AppInputsSection` (verbatim in `DatabaseSeeder.AssistantAppInputsSection`) is appended by
`PromptFor` only with the switch on: fill the fields to change a run, never edit the steps; how to set up a request page;
a switch plus a Conditional for a cheaper path; what never becomes a field. KB: `docs/assistant-kb/app-inputs.md` (its
"Settings a field can fill" blocks are generated from the allowlist, `AppInputsKbDocDriftTests`). Tests:
`AssistantAppInputsTests`, `AssistantAppInputsSaveAndRecipeTests`.

## Object effects: blur or mask an object by name

`AssistantObjectEffectTools` (`Agents/Tools/AssistantObjectEffectTools.cs`) adds one Propose/Apply pair, `ProposeObjectEffect` / `ApplyObjectEffect`, published over MCP as `ObjectEffect`. It is a paid action (`AssistantActionRisk.Paid` in `AssistantApprovalCatalog`), so the pair is destructive, not read-only, and the in-app assistant shows its approval card.

- **Names, not ids.** Both halves resolve the timeline, the video file and the clips by name in the active project (at most 20 clips). The effect word is one of `blur`, `pixelate`, `highlight`, `spotlight`, `colorpop`, `outline`; `strength` defaults to 0.7; `everythingExceptObject` is the invert option.
- **`ProposeObjectEffect`** runs the gates the analysis service runs (see [The object-analysis routes](editor.md#object-analysis-and-the-object-effects-finalizer-magic-blur-editor)) through the estimate, creates nothing and mints a confirmation token over the resolved payload. A refusal (plan, length, credits, run rate) is a plain sentence and no token.
- **Consent rule.** The assistant never turns the project's footage-sharing consent (`ObjectEffectsConsentAt`) on. With none on file the proposal returns a plain notice that a person must agree once in the editor (Objects section), and mints no token.
- **`ApplyObjectEffect`** re-resolves, re-verifies the token (a mismatch answers `stale confirmationToken, re-propose and try again`), then calls the shared `RequestAsync` with a timeline intent (timeline id and clip ids) stored on the `object_effect_requests` row. Neither tool edits the timeline.
- **Finalizer.** The timeline write is done by `ObjectEffectApplySweeper` when the analysis passes. When the same analysis already exists, `ApplyObjectEffect` applies just that one request itself (`ObjectEffectApplier.ApplyOneAsync`, the same claim and write as the sweeper, never a sweep of other rows) and reports `added`; otherwise it reports `queued`. If an identical search for the same timeline is already running, it says so plainly (`already being looked for in that timeline`) and starts nothing.
- **`needs_review`.** When the timeline changed while the search ran, `ApplyObjectEffect` answers `status: "needs_review"` and changes nothing; the result waits in the editor for the user to apply or discard (see [background-activity.md](background-activity.md)).
- **No wake.** The pair does not register an async run watch, so the assistant is not woken when the search finishes. Its `queued` summary tells the model to point the user at the editor. See [Async run wake](#async-run-wake) for the watch that other runs use.
- **Progress captions.** "Checking the clips and what finding the object would cost" and "Starting the search for the object" (`AssistantObjectEffectTools.Captions`).

## Audio cleanup: clean the sound of a file

`AssistantAudioCleanupTools` (`Agents/Tools/AssistantAudioCleanupTools.cs`) adds one Propose/Apply pair, `ProposeCleanAudio` / `ApplyCleanAudio`, published over MCP as `CleanAudio`. Both start the same hidden `AudioCleanup` workflow the editor's cleanup route starts, through the shared `AudioCleanupService` (the REST route `POST .../media/{mediaRef}/cleanup` calls the same service).

- **Source and targets.** One of `fileId` (a project file) or `outputId` (a finished render), and a `target`: `VoiceBasic`, `Hum`, `Wind`, `Hiss` or `Echo` (free, done locally), or `IsolateVoice` / `RemoveMusic` (hosted AI).
- **Consent rule.** The cleanup route has no server-side consent record; it only takes the `allowSourceMediaEgress` flag the editor's dialog sends. The tools never set or accept that flag, so a hosted target answers `NEEDS_CUSTOMER_OK` ("needs the customer's OK in the editor") at propose, at apply and at the approval gate, and runs nothing.
- **Authority.** Edit permission on the project is re-read first on every call; media of another project, quarantined or unknown is absent. Plan and credit checks answer the same codes as the REST route.
- **`ProposeCleanAudio`** returns a `confirmationToken` over the target, source and output kind, the source name, its length in minutes and `credits: "Free"`; it writes nothing.
- **`ApplyCleanAudio`** re-verifies the token (a mismatch answers `stale confirmationToken, re-propose and try again`), starts the run and returns the `executionId` without waiting; follow it with `GetExecutionStatus`. The approval card names the target, the source, the minutes and says Free.
- **Progress captions.** "Checking the audio to clean" and "Starting the audio cleanup".

## Voices: list, browse the library, add a library voice

`AssistantVoiceTools` (`Agents/Tools/AssistantVoiceTools.cs`) gives the assistant the project's narration voices. It adds two reads and one Propose/Apply pair; over MCP they are `ListProjectVoices`, `SearchVoiceLibrary` and `AddLibraryVoice`. The library browse and lookup share one implementation with the REST route `GET /api/v1/projects/{p}/voices/library` (`ProjectVoiceLibraryService`, see [voiceover.md](voiceover.md)).

- **`ListProjectVoices`** (View) returns each voice's `id`, `name`, `description`, `kind` (`Library` or `Cloned`), `selectable` and `unselectableReason` (the `ProjectVoice.IsSelectable` rule). The `id` is what a run takes: `brief.voice.projectVoiceId` (with `mode: narrated`) on `ExecuteWorkflow`, or `projectVoiceId` in a `Voiceover` step's config; the tool descriptions say so.
- **`SearchVoiceLibrary`** (View; `q`, `language`, `page`) returns one page of the resolved SpeechSynthesis provider's public library: `voiceId`, `name`, `languages`, `tags`, `previewAvailable`, plus `total`, `page`, `pageSize` (24) and an `UNTRUSTED DATA` note. Same rules as the REST route: Fish Audio only (otherwise `VOICE_LIBRARY_NOT_SUPPORTED`), bounded strings, ten-minute cache, a voice cloned on the platform account is never listed, the vendor's error text is logged only (`VOICE_LIBRARY_FAILED`). No audio is proxied. The vendor listing has no description field.
- **`ProposeAddLibraryVoice` / `ApplyAddLibraryVoice`** (`vendorVoiceId`, optional `displayName` up to 200 characters). Edit on the project is re-read first on every call. The id must be a public library voice right now: `IVoiceLibraryClient.GetLibraryVoiceAsync` (Fish `GET /model/{id}`; a 404, a non-public `visibility` or a different `_id` is "not found") and never a voice cloned on the platform account (checked in the database before any vendor call); otherwise `NOT_A_LIBRARY_VOICE` and no token. A voice the project already has answers `alreadyAdded` with its id and mints nothing. The token covers the project, the vendor id and the final name (the display name, else the library's name). Apply looks the voice up again, verifies the token and writes the same row the Voices tab's "Add" writes (`Source = Library`, no consent, description = the voice's tags), and returns the new `id`.
- **Approval card.** `ApplyAddLibraryVoice` is in `AssistantApprovalCatalog` ("Add a library voice", Normal). The card is built from a fresh library lookup and the database: the library's name for the voice, the name it will get, its languages and the project's name, and says Free and that no consent is needed. An id that is not a library voice gets no card and nothing runs.
- **Cloning is not offered.** A cloned voice needs the recorded person's consent statement, attested verbatim by the user in the Voices tab; the assistant and MCP cannot clone.
- **Progress captions.** "Reading the project's voices", "Searching the voice library", "Checking the voice", "Adding the voice to the project".

## Sound effects: search the library, add a sound

Three tools in `AssistantProjectTools` give the assistant the built-in sound-effects library ([`docs/sfx-library.md`](sfx-library.md)): one read and one Propose/Apply pair. Over MCP they are `SearchSoundEffects` and `AddSoundEffect`.

- **`SearchSoundEffects`** (`query`, `category`, `maxDurationMs`, `limit` up to 50, default 20; free, needs no project in context). Returns each sound by **name** (`name`, `title`, `category`, `tags`, `durationMs`), the total and the categories with their counts. It is platform content, so it reads the library table directly (enabled items only, filtered in memory like the REST list) and is annotated read-only. Titles and tags are marked untrusted data.
- **`ProposeAddSoundEffect` / `ApplyAddSoundEffect`** (`name`, or `starterSet: true` for the ready-made set of about 24 sounds; exactly one of the two; `projectId` optional like every project tool). Edit on the project is re-read first on every call. The name is the one `SearchSoundEffects` returns (the title also resolves when it is unique). Apply calls `SfxLibraryProjectAdder`, the same class behind `POST /api/v1/projects/{projectId}/sfx-library/{id}/add` and `.../starter-set`, so the copy, the pre-cleared moderation status, the per-project cap and the idempotent key are not duplicated. The token covers the project and the resolved library name (or the starter set). Adding a sound that is already in the project answers `alreadyInProject: true` and writes nothing; a project at its cap answers an error. The result names the sound, the file name and path, never an id.
- **Free and quota-exempt.** No credits and no model call; the files are not counted against the storage quota (`SourceKind = sfx_library`). Both tools are `Free` in `AssistantPlatformTools`.
- **Approval card.** `ApplyAddSoundEffect` is in `AssistantApprovalCatalog` ("Add a sound effect", Normal). The card names the sound and the project, or the starter set and the project, read fresh from the library table and the project; a sound removed from the library since the proposal makes the card unreachable.
- **History.** `ApplyAddSoundEffect` is an assistant write tool for the project history auto-capture (it adds project files).
- **Not here.** Placing a sound on a timeline is done in the editor; the assistant has no timeline-editing tool. A workflow's `OfferSfxClips` step reads the added files like any other audio file.
- **Progress captions.** "Searching the sound effects", "Preparing the sound effect", "Adding the sound effect".

## Avatars and objects: list, make objects, variants, the library, clips

`AssistantAvatarTools` (`Agents/Tools/AssistantAvatarTools.cs` and its partials `.Objects`, `.Variants`, `.Library`,
`.Clips`) adds **twelve** tools: two reads, `ListProjectAvatars` and `ListAvatarLibrary` (the same names over MCP,
`readOnlyHint: true`, closed world, free, not gated), and five Propose/Apply pairs, collapsed over MCP to
`CreateObjectAvatar`, `ExtractObjectFromVideo`, `CreateAvatarVariant`, `ImportLibraryAvatar` and
`GenerateSubjectClip`. Each pair calls the service its REST route calls (`ObjectAvatarCreator`, `IAvatarBuildService`,
`AvatarLibraryService`, `ISubjectClipService`), so every check of the route applies. Design record:
[`design/avatar-objects.md`](design/avatar-objects.md) "Avatar tools for the assistant and MCP"; see also
[avatars.md](avatars.md).

- **`ListProjectAvatars`** (View, re-read per call): for each person or object `name`, `kind`, `description`,
  `selectable`, `unselectableReason` in plain words (one sentence per rule reason, never the machine code),
  `coversPictures`, `references` counts, the `roles` present, the linked `voice` name, `keepUntil`, `fromLibrary`, and
  variants (`variantOf`, `variantLabel`, `variantDescription` on a variant, `variants` on its original); `builds` (a
  variant made by the picture model or an object pulled out of a video, being made or failed in the last day: name,
  kind, state, stage, a plain message); `clips` (the newest clips with avatars: state, the avatars shown, a plain
  message, the new files' names once ready); `mediaConsent`. The reason comes from `ProjectAvatar.UnselectableReason`
  over references observed by the shared `AvatarReferenceObserver`, the same observation the engine's
  `IAvatarResolver` makes. It never returns an id, a storage key, a URL, a digest, media, or the consent subject's name.
- **`ListAvatarLibrary`**: the workspace library (names, kinds, status, counts, keep-until of a person, how many
  projects use each, `inThisProject` with a project); a variant item carries `variantOf` (its original's name), its
  label and description. Saving to the library, renaming, confirming again, withdrawing and deleting stay in the app.
- **`CreateObjectAvatar`** (free): an object from project files named by path (`files: [{path, role?}]`, roles
  `Front`/`Angle`/`Detail` for pictures, `Motion` for videos), with the person check of the create route.
  **`ExtractObjectFromVideo`** (billed when the frames are cut by the segmentation model, else free): a video by path,
  up to 6 moments (`atSeconds`), in the background; it shows under `builds`. **`CreateAvatarVariant`**: a variant of an
  OBJECT by its original's name and a `label` + `description`, from other `files` (free, at once) or an `instruction`
  (the picture model, `includeVideos` optional; paid, in the background); a person's variant is refused with
  `VARIANT_OF_PERSON_IN_APP` and where to do it. **`ImportLibraryAvatar`** (free): a library item by name; a project
  that holds it already answers `alreadyImported` and mints no token. **`GenerateSubjectClip`** (paid by the clip
  run's credit gate): one to three avatars or objects by name (at most one person), the video model by name, a speech
  file by path; it answers `started` and the run's `executionId` without waiting (`GetExecutionStatus`,
  `WatchExecution` in chat).
- **The object statement.** Making an object (create, extract, variant) is the member's own attestation. In chat the
  card shows the statement (`ApprovalCard.Statement`) and approving it is the tick: the gate marks the call
  (`AssistantStatementApproval`) only when that card was shown and allowed, and the tool refuses
  `object_statement_required` without it. Over MCP the caller passes `statement` verbatim and `locale` (`en`, `it`);
  without them the answer carries the text to show (`requiredStatement`) and nothing is made.
- **Always asks.** `ApplyCreateObjectAvatar`, `ApplyExtractObjectFromVideo`, `ApplyCreateAvatarVariant` and
  `ApplyImportLibraryAvatar` are in `AssistantApprovalCatalog`'s always-ask set (a card in auto mode too);
  `ApplyGenerateSubjectClip` follows the user's setting unless it allows sending media for that call. Each card is
  built from what the tool resolves (names, kinds, files by path, the statement, what is sent and to whom, the
  estimate); a call the tool would refuse gets no card.
- **Sending media for one call.** A clip, a variant by the picture model, an extraction cut by the segmentation model
  and a picture tool with `subjects` send avatar media to an AI service. Without the project's agreement
  (`mediaConsent: false`) they take `allowMediaEgress: true`, judged by the one per-call rule (see "Allowing media to
  be sent for one run"). The grant never records the project's agreement and never switches a stored step flag on.
- **Not here.** No tool creates, confirms again, withdraws or deletes a person's avatar (only the Avatars tab, by a
  person ticking the statement), deletes an object or a library item, or saves to the library. The prompt section
  "People and objects" says so; `AssistantPromptConsistencyTests` pins it in both prompt copies.
- **Switches.** `Avatars:Enabled` and `Avatars:Objects:Enabled`, read on every call; the tools stay registered (the
  counts never move). The lists answer `{ "available": false, ... }`; the pairs refuse `AVATARS_NOT_AVAILABLE`, after
  the project check, so a foreign project reads exactly like a missing one.
- **History.** The import, extraction, variant and clip applies are assistant write tools for the project history
  auto-capture (they register project files); creating an object from files already in the project is not.
- **Phase 2 (generation), no new tool.** The typed run brief (`AssistantToolSchemas.RunBriefSchema`, so the MCP
  `brief` object too) gains `avatar: { avatarId }`; the workflow tools may set `VideoGenerate.avatarId`. The approval
  cards of `ExecuteWorkflow`, `ApplyWorkflow` (save) and `ApplyPatchWorkflow` add one line per avatar the run or the
  change shows: `Shows: "<avatar name>" (avatar). Sends that person's pictures, videos and voice samples to the
  video provider.` (name from the database, never an id or the subject's name). The step's `allowSourceMediaEgress`
  follows the consent rule above: `AssistantEgressConsentGuard` fingerprints every config field, so pointing a consented
  step at a different avatar resets it with a `needsBeforeRunning` notice, and an off-to-on flip needs the card (chat only;
  never over MCP). See
  [avatars.md](avatars.md) "Generating a clip with an avatar".

## Picture editor tools

The 15 tools of `AssistantPictureTools` let the assistant read and change the picture open in the picture editor
(`/app/editor/thumbnails/{documentId}?project=…`) on the user's behalf. They follow the assistant's rules exactly:
reads need no token, every change is a Propose/Apply pair in the chat, and nothing is applied without the user's
agreement. Over MCP each of the six pairs is one tool (the table below names it).

**Looking at the picture (vision).** `LookAtPicture` answers a question about what the open picture actually
SHOWS — people, objects, setting, colours, text and roughly where they sit — by sending ONE layer's stored raster as
an image part to the deployment's Vision provider and returning the model's answer. There is no server-side
compositor, so what a model can be shown is a layer's own pixels: with no `layerId` it looks at the backmost layer
that has pixels (the photograph the rest was built on) and says which layer it looked at, and a caller that needs to
check a cut-out or a designed graphic passes that layer's id. It is read-only, costs one small model call, and bounded
like every other tool result (3,000 characters, replayed on later turns). With no Vision provider configured it answers
"no vision service is set up" rather than failing, so the layer list stays usable on a self-host install.

**Always registered, refusing cleanly elsewhere.** The tool set is closed and static, so these tools exist on every
page. The web app sends the open picture as `imageDocumentId`; the chat endpoint keeps it only when it is a document of
the project in context (otherwise it is ABSENT, like a foreign project id), puts it in the session context ("Open
picture … (verified)") and in the per-call `AssistantToolContext.ImageDocumentId`. A tool given no `documentId` and
running without an open picture answers `{"error":"no picture open"}` and does nothing.

**The picture editor states the pair; the route is only the fallback.** This is the one page whose project id lives in
its `?project=` query rather than in the path, and the app router writes the URL only *after* the render that already
reports the new pathname. A shell that derived the project from `window.location.search` therefore read the PREVIOUS
page's query, sent the document without its project, and had the pair dropped by the endpoint above — the assistant
answering "no picture open" to someone looking at one. The page now mounts `AssistantPageContext` with both ids (the
document from its path, the project from the query the router itself resolved), and `AssistantShellProvider` re-reads
the location after each commit so a route-derived project id cannot go stale for any other page either. When the page
reports the picture editor and no picture is verified, the session context says so in words, so the assistant asks the
user to reopen the picture or reload the page instead of denying that one is open.

**Authority per call.** The first thing every tool body does is re-resolve the project through `IProjectAccessService`
(view for the two reads, edit for every write, whether it is the chat Apply or the single MCP tool) and then the document inside that project. Another
organization's picture, another project's picture and a nonexistent one all answer `{"error":"picture not found"}` —
never a permission error that would confirm it exists. A refusal writes nothing (`AssistantPictureToolTests` counts
`SaveChanges` calls and the change tracker, not only the response).

**Writes go through the editor's own routes.** Every apply calls the SAME controller action the editor calls
(`IImageDocumentCommands`, implemented by `ControllerImageDocumentCommands`, which instantiates
`EditImageDocumentsController` / `EditImageOperationsController` in the request's scope): validation, the document
revision bump, retention marks, vendor dispatch, transparency carry-over, the 90-second bound, metering and each
action's own access check are the editor's, not a second copy that could drift.

| Tool (MCP name; the chat pair in brackets) | What it does | Confirmation payload binds |
|---|---|---|
| `DescribePictureDocument` | Canvas size and background; every layer back to front (id, name, kind, role, position, scale, size on the canvas, rotation, flips, visibility, lock, opacity, blend mode, a text layer's words and font, how it was made); graphics still being designed. Finished graphics are settled into layers first. | — |
| `ListPictureTools` | The AI picture tools, whether a provider is set up for each (same resolution as the editor, including the `ImageEdit` fallback), and the price of one run from the model-capability price the metering uses. | — |
| `AddTextLayer` (`ProposeAddTextLayer` / `ApplyAddTextLayer`) | A text layer: exact words, font family, size, weight, colour, alignment, outline, position. | document + the normalised text content and transform |
| `EditPictureLayers` (`ProposeEditPictureLayers` / `ApplyEditPictureLayers`) | Move, resize (scale or width), rotate, flip, show/hide, lock, opacity, blend mode, rename, restack, and a text layer's words/font/size/colour/outline. | document + each layer's id, CURRENT name and kind, and the resolved new values |
| `DeletePictureLayers` (`ProposeDeletePictureLayers` / `ApplyDeletePictureLayers`) | Delete layers (destructive; the prompt offers hiding first). | document + each layer's id and CURRENT name |
| `ChangePictureCanvas` (`ProposeChangePictureCanvas` / `ApplyChangePictureCanvas`) | Exact size, or a shape (16:9, 9:16, 1:1, 4:5, 4:3, 3:2, 2:3, 21:9) at a short side, and/or the background colour. | document + the current and the new size/background |
| `RunPictureTool` (`ProposeRunPictureTool` / `ApplyRunPictureTool`) | One paid AI tool: `erase`, `fill`, `expand`, `replaceBackground`, `relight`, `upscale`, `instruct`, `generate`, `removeBackground`, or `cutOut` (a subject named by a prompt, via `/subjects`). In the chat the proposal shows the estimated cost and is refused when no provider serves the tool; over MCP the call returns `{applied, summary, projectName, pictureName, layerIds, notice}`, so the client's own confirmation is the only checkpoint before the charge. | document + tool, target layer id and name, prompt, area, parameters |
| `DesignGraphic` (`ProposeDesignGraphic` / `ApplyDesignGraphic`) | Queue the graphic designer ([`editor.md`](editor.md#designed-graphics)), or change a graphic it made (`revisesLayerId`). In the chat the proposal returns the estimate; over MCP the call queues it and returns `{applied, summary, projectName, pictureName, graphicId, status}`. It needs a verified email, like every paid run. | document + description, placement, size, the revised layer's id, name and base version |

**Masks the assistant draws.** `erase` and `fill` act on a painted area in the editor. The assistant cannot paint, so it
names an `area` — a rectangle as fractions of the layer (`{left, top, right, bottom}`, 0..1) — and the tool draws a
white-on-black mask of exactly that rectangle at the layer's own pixel size (`PngAlpha.EncodeRectangleMask`; the size
read from the row or the image header), stores it at a key shaped exactly like one `assets/upload-url` issues for the
document (with the same 24-hour retention mark) and runs the editor's operation with it. Every other tool is maskless.

**The editor follows live.** Each write reports its change through `AssistantToolProvider.OnImageDocumentChanged`, which
the chat endpoint writes AT ONCE as a `data-imageDocumentChanged` frame. The web app announces it once per `changeId`
(`web/lib/editor/assistant-events.ts`); the picture editor with that document open flushes its own pending edits,
reloads the document, selects and reveals the changed layers, and says "The assistant: …". Added layers are one undo
step (undo hides them); any other remote change restarts the undo history. A `graphicQueued` change makes the editor
re-read its graphics list, so the work-in-progress effect appears where the graphic will land.

---

## Per-call authority: platform and organization

Authority is **per call and server-derived; it is never cached.**

- `AssistantToolProvider` holds the request-scoped `ICurrentUser` and reads it *inside the tool body
  that is running*. It does not hold a user id, an admin boolean, or any other derived authority in a
  field, a closure, a `Lazy` or a `static`.
- `AssistantToolContext` carries the acting `UserId` and the caller's page context (`ProjectId`,
  `WorkflowId`, `ImageDocumentId`) and deliberately carries **no authority** — no `IsAdmin`, no role, no permission set.
  An admin claim that travelled with the conversation would let a prompt-injected instruction inside
  an admin's session outlive the privilege that authorised it.
- The page context is not a grant either: every tool that uses `ProjectId` re-resolves the project
  from the database and re-checks `project.OwnerId == UserId` on every call.

### Two authorities, never one blurred "admin" bit

The assistant's **13 gated tools** are exactly the tools registered by `AssistantAdminTools`, and
they are **not** gated on one privilege. Which authority governs depends on what the tool acts on,
and the two are independent:

| Authority | Read as | Tools | What they act on |
|---|---|---|---|
| **Platform** | `ICurrentUser.IsPlatformAdmin` | 10 — the inference-provider tools (`ListInferenceProviders`, `TestInferenceProvider`, and the create/update/delete pairs), `ListAgentsAdmin` and `GetDecisionCalibration` | The **platform-managed** rows — `inference_providers.organization_id IS NULL` — the deployment's own resources, plus the platform-wide calibration report. |
| **Organization** | `OrgAuthority.IsOrganizationAdmin(ICurrentUser.OrgRole)` — Owner or Admin of the **active** organization | 3 — `ListSkills`, `ProposeAssignAgentSkills`, `ApplyAssignAgentSkills` | One organization's **own** custom agents and their skill assignment. |

Two consequences follow, and both are intentional:

- **Being an organization's Owner grants nothing on the platform tools.** The provider tools act on
  platform-managed rows only, so a workspace's own bring-your-own provider is as unreachable through
  them as a nonexistent one — the same answer `/api/v1/inference-providers` gives for it. Before
  A11 those tools read through the EF tenant filter, which also admits the caller's own
  organization's rows, so a platform admin could edit or delete a workspace's own provider and clear
  that workspace's default as a side effect.
- **Being a platform admin grants nothing on the organization tools.** A platform admin who is only
  a Member of their active workspace is refused on the skill tools exactly as any other Member is,
  and needs no platform claim at all if they are an Owner or Admin there.

Across both, existence never leaks: another organization's custom agent answers
`{"error":"agent not found"}` — the same answer a nonexistent id gets — whatever authority the
caller holds.

The gated subset is not maintained by hand: `AssistantToolProvider.GetAdminToolNames` filters the
provider's own registered list through the same `AssistantAdminTools.GetTools` list the registration
comes from, so a new gated tool is covered the moment it is registered and a tool that forgets its
gate fails the test rather than escaping it.

The gate itself:

- **The authority is re-read from the request scope inside every gated tool body**, and the gate is
  the *first executed statement* of every tool method. It is never memoized in a field, a `Lazy`, a
  closure or a static, and no tool infers authority from an earlier tool result or from the
  conversation — an authority claim that travelled with the conversation would let a prompt-injected
  instruction inside an admin's session outlive the privilege that authorised it. `ICurrentUser`
  reads `IsPlatformAdmin` (the `platformAdmin` claim, itself re-validated against the database on
  every request by the JWT bearer's membership validator) and `OrgRole` (the role in the **active**
  organization, likewise re-read from the membership row, never from the token). Two identities that
  differ only by authority get different outcomes from the same provider instance.
- **A refusal writes nothing.** The refusal is inert: the caller gets
  `{"error": "admin privileges required"}` and the database is untouched. `AssistantAdminToolTests`
  asserts the DbContext, not just the response string, because a tool that returns an error *after*
  writing is still a privilege-escalation bug.
- **Secrets never come back out.** A provider's API key may be an *input* (create/update — it is the
  admin's own write), but no result ever contains the ciphertext or a decrypted key: reads return
  only the redaction vocabulary `hasApiKey` / `apiKeyLastFour`.

---

## Confirmation tokens

Every mutating tool is backed by `IConfirmationTokenService`
(`Services/Assistant/ConfirmationTokenService.cs`), registered as a **singleton** — one key per
deployment, shared by every surface, because a per-scope instance would derive its own key and reject
tokens its sibling had just minted.

`Propose*` derives a canonical payload, mints a token with
`Compute(operation, canonicalPayload, userId)` and shows the model a summary. `Apply*` re-derives the
same payload from the same sources and asks
`Verify(operation, canonicalPayload, userId, presented)` to check it. A token that does not verify
produces a JSON error and writes nothing — **the payload, not the model's claim about it, is what
authorises the write.**

Over MCP the client never sees the token: the collapsed tool runs the `Propose*` half, reads the
token from its JSON and passes it to the `Apply*` half inside the same server call. The Apply body
still verifies it, so every in-app rule holds; the token simply cannot be replayed from outside.

### The MAC

`Compute` returns lowercase hex HMAC-SHA256 over a **length-prefixed** message:

```
<operation.Length>:<operation>\n
<canonicalPayload.Length>:<canonicalPayload>\n
<userId as "D">
```

The explicit lengths matter: without them the operation name and the payload could be re-split at
their separator, and a token minted for one pair would verify against another.

`Verify` recomputes the token and compares with `CryptographicOperations.FixedTimeEquals` — not string
equality — so a byte-by-byte probe of a token learns nothing from response timing. It returns `false`
for an empty or length-mismatched token rather than throwing, and fails closed on any exception in the
computation path.

### The key

Resolved lazily, on first use, so constructing the service (at DI validation time, or in a test) never
touches the filesystem. In order:

1. **`Assistant:ConfirmationTokenKey`** — an operator-supplied, Data-Protection-protected base64 key.
   If it is set but cannot be decrypted with the current key ring, the service logs a warning and
   falls back rather than silently keeping a second key: tokens minted under the configured key are
   rejected, which is recoverable by re-proposing, whereas a silent divergence would be invisible.
2. **`Assistant:ConfirmationTokenKeyFile`** — otherwise the persisted key is read from here.
3. **First run** — 32 random bytes are generated, protected with `ISecretProtector`, and persisted
   with a write-then-rename so a crash mid-write cannot leave a half-written key. The default path is
   `<DataProtection:KeysPath>/assistant-confirmation-token`, and `DataProtection:KeysPath` defaults
   to `/keys` — the shared `dpkeys` volume both services already mount — so every replica and every
   restart derives the same key. If the key cannot be persisted, the service degrades to a
   process-local key rather than refusing to start; the only consequence is that a token proposed
   before a restart is rejected after it.

The key is never hardcoded, never logged and never returned; only its role in the MAC is observable.

### What a canonical payload binds

- **Destructive operations bind the target's id AND its exact current name**, and the name is
  **re-read from the database at apply time**. A confirmation the user saw for a project called
  "Archive 2024" therefore cannot be honoured against a project that has since been renamed — the
  recomputed payload no longer matches and the apply is refused instead of destroying something the
  user was never shown. This applies to `ProposeDeleteProject`, `ProposeDeleteWorkflow`,
  `ProposeDeleteProjectFile`, `ProposeDeleteInferenceProvider` and `ProposeAssignAgentSkills`.
- **`ProposeWriteProjectFile` binds a hash of the content**, so any edit to the body between propose
  and apply invalidates the token.
- **`ProposeEditProjectFile` and `ProposeInsertProjectFileLines` bind the file's base content hash and the exact
  edits**, so a change to the file or to the edits between propose and apply invalidates the token.
- **A provider create/update payload carries a SHA-256 *fingerprint* of the submitted API key, not
  the key**, so the plaintext never enters a payload the model could be shown. An empty string means
  "clear the stored key" and a null means "leave it unchanged", and those two are distinct in the
  payload.

### The one unkeyed operation — an honest asymmetry

`ApplyWorkflow` is a **compatibility carve-out**, not a template. It predates this service, and its
token is pinned byte-for-byte, because a token proposed before a deploy has to stay acceptable after
it. The pin is documented where it is implemented — `ConfirmationTokenService`'s own class remarks
and the `ApplyWorkflowOperation` constant's doc comment, against the keyless digest
`WorkflowEditDiffService` has always produced — and no decision record states it.
`ConfirmationTokenService.Compute` therefore reproduces the legacy **unkeyed
SHA-256 digest of the canonical payload — no key, no user** — for that one operation name
(`ConfirmationTokenService.ApplyWorkflowOperation`) and for nothing else. Every other operation is
HMAC-SHA256 over a keyed, user-bound message. **New mutating tools must not copy it.**

---

## Permission cards and auto mode

The human check on a change is **a card the server holds the tool behind**, not a sentence the model may or may not
write. The full design and wire contract is [design/assistant-approvals.md](design/assistant-approvals.md); the shape
of it:

* `AssistantToolProvider` wraps every tool in `AssistantToolInterceptor` when the chat transport gives it an
  `IAssistantApprovalGate`. A tool is **gated by name** — every `Apply*` plus `ExecuteWorkflow` —
  (`AssistantApprovalCatalog.IsGated`); `Propose*` tools write nothing and are never gated, but their results are
  remembered so the matching apply can describe the real change.
* In ask mode the gate emits a `data-assistantAction` frame (`state: pending`) with a plain-language title, the
  changes, the project name and, for paid work, the cost, and then **waits**. Nothing runs while it waits. The
  user's click arrives at `POST /api/v1/assistant/approvals/{id}` and `AssistantApprovalBroker` releases the turn.
  A decline or an expiry (`Assistant:ApprovalTimeoutSeconds`) means the tool does not
  run (a browser that went away does not decide anything: the card stays answerable), and the model reads `{"declined": true, …}` and is told not to try again.
* The create card (`ApplyCreateFromTemplate`) reads "Create {name} from the {recipe} recipe" and lists the steps added by each patch, the brief's fields, the cost class and lint warnings; the run card (`ExecuteWorkflow`) lists each brief field that is set ahead of the price and adds `Not used by this app: …` for a field no step reads. Lint errors never reach a card: the tool refuses the proposal to the model with the findings, in auto mode as well, because the lint runs in the tools.
* The words on a card are written by `AssistantApprovalCardBuilder` from names it reads out of the database through
  the same access checks the tool will make. They are never copied from the model's arguments alone, and never contain
  an id. A call that names something the caller cannot reach produces no card — the tool refuses it itself and writes
  nothing.
* Authority is unchanged. The tool body still verifies its confirmation token, ownership, role and plan; allowing a
  card never widens what a tool may do.
* **Auto mode** (`application_users.assistant_auto_approve`, `GET`/`PUT /api/v1/assistant/preferences`) skips the card
  for every action — deletions included, a deliberate choice the web app states before it switches the
  mode on — except the always-ask set below and an action whose own cost estimate is above `Assistant:AutoApproveCostCardCredits` (default 25
  credits; `<= 0` or invalid falls back to 25; raise it with a larger positive value). That card is shown and must be
  approved, flagged with `autoNote` on the frame; the preferences response carries the read-only
  `autoApproveCostLimitCredits`. The MCP bridge is unchanged (no gate; see below). It still streams a `done`/`failed` frame for each action so the chat shows what was done on the user's
  behalf. The flag is read live on every gated call; "Always allow" on a card persists it before the action runs.
* **Always-ask set** (`AssistantApprovalCatalog.AlwaysAsksUser`): the card is shown even in auto mode, whatever the
  credit limit, and "always allow" on it does not switch auto mode on. It holds `ApplyRestoreProjectVersion`,
  `ApplySchedulePost`, `ApplyCancelScheduledPost`, the admin writes `ApplyCreateInferenceProvider`,
  `ApplyUpdateInferenceProvider`, `ApplyDeleteInferenceProvider` and `ApplyAssignAgentSkills` (an AI service
  connection holds a key and an endpoint, so content read during a turn must not be able to change one), and
  `ApplyImportFileFromUrl` (it makes the server fetch an address the model chose). In a run-wake turn these cards
  wait for the user like any other card and expire unanswered, so none of them runs unattended.
* The MCP bridge (`AssistantToolBridgeController`) builds the same tools with **no gate**: the external client asks its
  own user and there is no chat stream for a card. Each collapsed tool still runs both halves, so the mutation is
  authorised exactly as in the chat. The one carve-out is `DecideDesignChange`: over MCP it refuses `approve`
  by itself (inside the tool body), so a brand change is never approved without a click in the app; `reject` works.
* Approval requests are process-local, like `AssistantTurnRegistry`. An API restart, or a browser reload while a card
  is pending, abandons the request and nothing is changed.

### When a tool fails

`AssistantToolFailureReporter` wraps every tool (just outside the approval interceptor, so a throwing gated tool still fails
its card). Each failure, a thrown exception or a result with a top-level string `error`, is logged at **Warning** with the tool
name, thread id, transport (`chat` or `mcp`), error code and the error text (one line, capped, secrets redacted); never the
arguments. In chat a throw no longer reaches the model as "Error: Function failed.": a bad argument is answered with
`{ error, code: "TOOL_ARGUMENTS_INVALID" }` naming the parameter and its expected type (for `workflowId`: omit it or pass null
to create, pass the id from `ListWorkflows` to edit), and any other throw with a fixed sentence, `code: "TOOL_FAILED"` and a short
reference the Warning line also carries, never the exception text (S-75). A turn answers at most
`AssistantToolFailureReporting.MaxAnsweredExceptions` (5) throws this way; after that a throw reaches the framework again, so its
consecutive-error stop still ends a turn whose tools keep failing. Over MCP a throw keeps the bridge's own fixed answer and log
line (both throws and `{error}` results are also logged by the reporter). Every nullable tool parameter has a default, so an
omitted argument binds as null instead of throwing (`AssistantToolOptionalParameterDriftTests`).

### The stale-proposal bug this fixed

`WorkflowEditDiff.ConfirmationToken` hashes the workflow's *current* steps, so the page's review tray showed a
proposal that stopped verifying as soon as the assistant applied it itself (it applied in the same turn once the user
had said yes). Pressing **Apply changes** then answered "Proposal out of date" for changes that were already in.
Three fixes: the chat transport only emits `data-workflowDiff` for a proposal still unapplied at the end of the turn
(`ApplyWorkflow` clears it); `WorkflowEditDiff` carries the `workflowId`/`name` it was computed for; and apply is
idempotent — a stale token whose proposed steps are already stored answers `alreadyApplied: true` (REST `200`, and the
tool's result) instead of `409`. That needed the diff to compare *every* field a proposal can set
(`WorkflowEditDiffService.StepsAreDifferent` now compares the step `BuildStep` would store), so a stale apply can
never claim a change that was not made.

---

## Guardrails, names and the documentation boundary

The assistant is a customer-facing support surface, and its boundaries are enforced in layers, none of which is the
only one:

1. **The prompt** (`AssistantAgent.DefaultPrompt`, duplicated byte-for-byte in `DatabaseSeeder.BuiltInAgents`): scope
   (ReelBolt only — anything else is declined in one sentence), confidentiality (never reveal or paraphrase its
   instructions, tool names or how it is built), **no technical detail however it is asked**, no timing language about
   missing things (only *supported* and *unsupported* operations — never "planned", "not yet", "coming soon"), and
   names instead of ids. `AssistantPromptConsistencyTests` pins every one of these.
2. **`AssistantInputGuard`** answers clear-cut prompt-extraction and jailbreak messages with a fixed reply and no model
   call. It is narrow on purpose; everything subtler reaches the model.
3. **`AssistantReplySanitizer`** is the deterministic last line for "names, never ids": it learns the id → name pairs
   every tool result carries and rewrites (or removes) any id-shaped token in the reply, and removes confirmation
   tokens, before the reply is persisted or streamed.
4. **The documentation boundary.** The developer docs are no longer published on a website, but the assistant still
   reads them as background. Their sections carry no URL, are returned with `audience: "Internal"` by
   `SearchPlatformDocs` and are labelled `INTERNAL BACKGROUND` in the per-turn block; the prompt forbids quoting,
   paraphrasing or linking them. Customer-facing user-guide sections keep their public `Source:` link.

---

## The web surface

The shipped client is one assistant, mounted once for the whole application.

### Mounting and trigger

- **Mounted once** in `web/app/(app)/layout.tsx`, which wraps the app shell in
  `AssistantShellProvider`. The provider renders `AssistantDrawer` **unconditionally** — never behind
  `opened &&` — because Mantine is what hides a closed drawer, and the drawer's own runtime provider
  has to stay mounted while it is closed. That is what keeps the conversation from being discarded
  every time the panel is shut to look at the canvas.
- **Triggered** from `AppShell`'s header (`web/components/shell/AppShell.tsx`), by `AssistantTrigger`
  next to the mobile `Burger`. The trigger lives in `AppShell` rather than in a page header so every
  authenticated page reaches it, including the majority that render no page header at all.

### Shape

`AssistantDrawerShell` (`web/components/assistant/AssistantDrawer.shell.tsx`) follows the viewport:

| Viewport | Shape |
|----------|-------|
| Desktop | **Left drawer** — `position="left"`, `size={560}` |
| Below Mantine's `sm` breakpoint | **Bottom sheet** — `useMediaQuery('(max-width: 48em)', false)` → `position="bottom"`, `size="85%"` |

The set of confirmation tokens already handed to the page lives in the drawer shell for the lifetime
of the thread, and a token is forwarded at most once: reopening the drawer remounts every message,
each message's diff extractor re-emits its proposal, and a proposal that was applied, discarded or
superseded must not come back.

### There is exactly one assistant

The workflow page's **page-local assistant is deleted** — it no longer mounts a drawer of its own. The
page opens the shell's assistant and contributes to it:

- `<AssistantPageContext projectId? workflowId? />` tells the shell which project and workflow the
  page on screen is about. It renders nothing, and it clears the context on unmount so the next page
  never inherits this one's project.
- `useAssistantPageContext({ onWorkflowDiff, currentSteps, review })` hands over the handler that
  applies proposals, the steps a diff is drawn against, and the page's own review tray, for as long as
  the page is mounted. Proposals arrive in the `data-workflowDiff` SSE frame.

### Attachments

The composer's attach control does **not** send files through the chat stream. It uploads through the
project's existing file endpoint — `uploadFile` in `web/lib/api/files.ts` — so an attachment is an
ordinary `ProjectFile` the rest of the platform already understands, and then sends an ordinary user
turn naming the new project file id. The turn is persisted like any other. With no project in context
the control is disabled and says so out loud ("Open a project to attach files"), because there is
nowhere to put the file.

### Client API

`web/lib/api/assistant.ts` holds the transport helpers: `getAssistantChatStreamUrl()` returns
`/api/v1/assistant/chat` and takes **no arguments** (the old
`getAssistantChatStreamUrl(projectId)` threw on a non-GUID and could not be called from a
project-less page), alongside `listThreads`, `createThread`, `getThread`, `renameThread` and
`deleteThread`.

---

## Assistant execution rate limiting

The `IAssistantExecutionGuard` enforces a **rolling-hour cap per user + project**, in two buckets:

| Bucket | Default | Config key |
|---|---|---|
| `paid` | 10 per hour | `Assistant:MaxPaidExecutionsPerHour` (the legacy `Assistant:MaxExecutionsPerHour` is honoured when only it is set) |
| `free` | 60 per hour | `Assistant:MaxFreeExecutionsPerHour` |

- **Which bucket:** `RunRateClassifier` reads the workflow's steps. A workflow is `free` only when every step is provably deterministic and calls no model or vendor; anything else, an unknown step type, an unparsable config or an empty workflow is `paid`. A free run never uses the paid allowance. A revision started with `ApplyReviseComposition` always counts as `paid`. See [metering-and-billing.md](metering-and-billing.md), "Run rate limit buckets and run_rate_events".
- **Window:** the last 3600 seconds.
- **Counted:** rows of the API-owned table `run_rate_events` (`Source` `execute` or `revision`), written when the run is admitted. Human-started runs from the UI are not counted. Queued runs count from the moment they are asked for.

Both the assistant's `ExecuteWorkflow` (and so the MCP) and `POST /api/v1/projects/{projectId}/workflows/{workflowId}/assistant/execute` classify and count **inside the guard's lock** from the step list the run starts from, so the bucket that is counted is the bucket that was admitted.

**What a refusal says.** Every surface reports the same numbers. The assistant and MCP tool returns:

```json
{ "error": "run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 13 minutes",
  "code": "RUN_RATE_LIMITED", "bucket": "paid", "used": 10, "limit": 10, "windowMinutes": 60, "retryAfterSeconds": 754 }
```

The REST route answers `429` with `{message, code: "RUN_RATE_LIMITED", bucket, used, limit, windowMinutes, retryAfterSeconds}` and a `Retry-After` header. `retryAfterSeconds` is the time until the oldest run in the window leaves it (at least 1). When the guard cannot take its lock in time the sentence says the limiter is busy and to try again in a few seconds. The separate revision and graphic limits (`REVISION_RATE_LIMITED`, `GRAPHIC_RATE_LIMITED`) now carry `used`, `limit`, `windowMinutes` and `retryAfterSeconds` as well.

**`RUN_CLASS_CHANGED`.** The engine reads the workflow's steps again when the run executes. A run admitted as `free` is stamped on its row (`workflow_executions.admitted_run_rate_bucket`); if the steps the engine loads now classify as `paid`, the run fails before its first step with `RUN_CLASS_CHANGED` (failure class UserInput, nothing charged): "This workflow changed after the run started; start it again." A run with no stamp is not checked.

**`ExecuteWorkflow` receipt.** The tool no longer echoes the request. It returns `{executionId, status, workflowName, projectName, startedAt, styleName?, outputFormat?, outputResolution?, designSystemPin?, rateLimit: {bucket, used, limit}}`.

The cap holds across API replicas. The count check and the insert run under a lock: a process-local
semaphore keyed on `(userId, projectId)`, plus a PostgreSQL session advisory lock on the request's
own connection. The session lock is polled every 50 ms for up to
`Assistant:ExecutionGuardLockWaitMilliseconds` (integer 0 to 60000, default 5000). A request that
cannot take the lock in that time is refused the same way as a run over the cap. The lock is held
from the count to the insert, so two concurrent calls on any replica cannot both observe
`count < max` and both proceed.

The run row is committed before the run is published, and before the `201` is returned. Nothing is
announced for a row that could still roll back.

---

## The MCP server

The MCP server (`mcp/src/server.ts`, built in `mcp/Dockerfile`) is an **independent Node.js
service** that exposes the assistant's tools to external CLI clients such as Claude Code. It contains
**no tool logic**: `tools/list` proxies the Inference API's tool catalog and `tools/call` proxies the
invocation, so every schema, every validation and every authority check stays the C# assistant's (see
[The MCP tool bridge](#the-mcp-tool-bridge)).

It offers both HTTP and stdio transports:

- **Stdio mode** (the default): spawned by a CLI client as a child process. The caller's own bearer
  token is read once at startup from `REELBOLT_MCP_TOKEN` and forwarded verbatim on every call.
- **HTTP mode** (when `MCP_TRANSPORT=http`): a streamable-HTTP server on `PORT` (default `3000`; the
  compose service sets `PORT` from `MCP_PORT`, default `3002`), taking `Authorization: Bearer <token>`
  from each request and forwarding it verbatim. Over HTTP it is also the OAuth resource server — see
  [Connecting Claude Code over OAuth](#connecting-claude-code-over-oauth).

`INFERENCE_API_URL` is **required in both modes and has no default**. The server proxies and nothing
else, so a missing target is a clear startup error rather than a server that starts and then fails
every call against nothing.

### Sessions and expiry

In HTTP mode the server keeps its streamable-HTTP sessions in memory, so a restart forgets them. A request that carries an `mcp-session-id` the server does not know is answered according to what it is: an `initialize` request starts a new session as usual; any other request (a `POST` such as `tools/call`, a `GET` for the event stream or a `DELETE`) is answered **HTTP 404** with the JSON-RPC error `{"jsonrpc":"2.0","error":{"code":-32001,"message":"Session not found"},"id":null}`, which makes a client following the Streamable HTTP specification initialise again by itself. No transport is created for the unknown id. Authentication (the `401` challenge) still runs first, and a request without a session id behaves as before. This replaces the earlier `400 Server not initialized`, which clients did not read as an expired session.

### It never verifies the token

The server does **not** parse, decode or validate the bearer token, and it never reads
`JWT_SIGNING_KEY`. It forwards the token to the Inference API, which authenticates it with
`AddJwtBearer` and re-derives membership and authority from the database on every request. A `401`
from the API becomes the OAuth challenge described below; every other non-2xx — including a `403` —
is surfaced as a JSON-RPC error. The API is the only authority.

That is deliberate. The server used to verify the JWT itself with the platform's shared HMAC signing
key, which meant handing the platform's **token-signing secret** to anything running this server —
including a plugin installed on a user's own machine, where anyone holding that key could mint a
token for any account. Forwarding removes it: the server is given the caller's own token, and the API
alone decides what that token may do.

---

## The MCP tool bridge

Two Inference API endpoints are the whole bridge (`AssistantToolBridgeController`):

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/assistant/tool-catalog` | The MCP projection of the assistant's tools: `name`, `description`, `inputSchema`, `title` and `annotations` (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`). The schema is read from the `AIFunction` itself — never hand-written, never maintained in a second place. |
| `POST /api/v1/assistant/tool-call` | One invocation. Body: `{ name, arguments, context? }`. Result: the tool's own output. A collapsed name runs both halves in this one request. |

**The catalog is a projection, not a listing.** `AssistantMcpToolSurface.Project` takes
`AssistantToolProvider.GetTools(...)`, publishes the 47 collapsed pair names and the two upload names
instead of the 48 `Propose*` / `Apply*` pairs, drops `WatchExecution` (it is chat-only) and the four social pair
tools (`AssistantMcpToolSurface.NotOverMcp`), and applies the
admin-tools filter. The result is 101 tools. The in-app registration is still the one implementation,
and a drift test fails if a registered `Apply*` has no collapse entry or an entry has no pair.

**The old pair names are unknown names over MCP.** `POST /api/v1/assistant/tool-call` refuses a
`Propose*` or `Apply*` name exactly as it refuses any name not in the catalog; only the collapsed
names are callable. `confirmationToken` is not in any published schema, and a caller-supplied value is
overwritten by the token the server mints inside the call.

**Page context crosses in one optional `context` object** (`projectId`, `workflowId`,
`imageDocumentId`). The assistant's project-scoped tools take no `projectId` argument — they read it
from the per-call context — so the MCP layer supplies it. The server in `mcp/` injects this one
property into every bridged tool's `inputSchema`, strips it back out of `arguments`, and sends it as
the call's `context` field. One wrapper rather than three top-level properties, precisely because
real tools already declare `workflowId` (the chat's `ProposeWorkflow` / `ApplyWorkflow` and
`ExecuteWorkflow`; over MCP the same pair is `SaveWorkflow`).

**A refusal is a result, not a transport error.** An authority refusal, a plan-gate refusal, a
rate-limit refusal and an unknown workflow all come back as the tool's own JSON text with
`isError: false`, because the model is meant to read them and act on them. `isError: true` is reserved
for a genuine transport or execution failure.

**Authority is never carried in from the client.** The `context` names page context only. The acting
user comes from the verified JWT's `sub`, never from the request body, and each gated tool re-reads
its own authority from the request scope inside its body — see
[Per-call authority: platform and organization](#per-call-authority-platform-and-organization).

---

## Connecting Claude Code over OAuth

The hosted path needs no token handling by the user: Claude Code discovers and runs an **OAuth 2.1**
login, and the access token it receives is a normal ReelBolt JWT carrying a `scope` claim. There is
no new identity provider and no second permission model — it is the same token the Go API issues and
the Inference API already validates. The frozen contract is
[`design/mcp-oauth.md`](design/mcp-oauth.md).

| Piece | Lives in |
|---|---|
| Authorization server: `/.well-known/oauth-authorization-server`, `/oauth/register`, `/oauth/authorize`, `/oauth/token`, `/oauth/revoke` | the Go API |
| Resource server: `/.well-known/oauth-protected-resource` and the `/mcp` transport | the MCP server |
| Consent screen, and the scopes it grants (`assistant.tools`, `assistant.admin`) | the Go API's `/oauth/authorize` |

The MCP server's two public URLs come from configuration and are never a hardcoded host:
`MCP_PUBLIC_URL` (the `resource` value, e.g. `https://host/mcp`) and `OAUTH_ISSUER_URL` (the single
entry in `authorization_servers`, e.g. `https://host`). **Stdio mode needs neither.** When the HTTP
transport is asked to do anything without a bearer token — or the Inference API rejects the presented
token with `401` — the server answers `401` with a `WWW-Authenticate: Bearer resource_metadata=…`
challenge, so the client can discover the authorization server and start a login. Every other non-2xx
from the API keeps the JSON-RPC behaviour.

The user-facing steps are in [`user-guide/assistant/claude-code.md`](user-guide/assistant/claude-code.md).

---

## MCP metering and the admin-tools setting

**Every bridged call is recorded.** One `mcp_tool_calls` row is written per call — user, organization,
tool name, when it ran, whether it succeeded, its error code and its duration — whether the call
succeeded or was refused, so usage is reportable per user and abuse is visible. **A read tool's row is
a record, not a charge.**

**Credits are drawn only where the work already costs money.** The bridge deliberately does not write
to `usage_events`, which records what the platform buys from a provider: a tool call is not a provider
purchase, and zero-cost rows there would distort provider-cost reporting. Charging is therefore
exactly what it is on every other path — the existing credit preflight and ledger already charge
workflow runs and paid picture tools, and the MCP path inherits them (see
[metering-and-billing.md](metering-and-billing.md)). MCP-caused executions are labelled
`ExecutionInitiator.Mcp` rather than `Assistant`.

With the monthly allowance used up and the credit balance 0, tools that spend nothing (those `AssistantPlatformTools.Tools` classifies `Free`, such as `ListProjects`) keep working, and only tools that can spend money (`PlatformBilled`, or any name the table does not list) are refused with the plain credits message.

**Admin tools are off by default, per user.** `application_users.mcp_admin_tools_enabled` gates the
13 gated tools listed under [Per-call authority](#per-call-authority-platform-and-organization). Over
MCP they publish as 9 catalog entries, because the four pairs collapse; the setting still gates the
whole group, including both halves of each pair:

- **Off** — the catalog omits them entirely, so a caller with a narrow token is not even told they
  exist. This is a deliberate narrowing relative to the chat path, which registers them on every page
  and refuses inside the body.
- **On** — they are listed and callable, and each still passes its own per-call authority check as
  before. The setting is an **additional** condition, never a substitute for authority.
- A refusal carries the code `MCP_ADMIN_TOOLS_DISABLED` with a hint telling the user to enable it.
- The OAuth scope `assistant.admin` mirrors the setting at consent time for auditability. It does not
  form a second, independently-failing gate: **the setting is the authority**, applied identically to
  local and remote callers.

**Nothing in the shipped UI writes that column yet.** At the time of writing neither `api/` nor `web/`
has a route or a control that sets `mcp_admin_tools_enabled` — it is read by the bridge and by the
OAuth consent path, and written nowhere — so turning it on is currently a database operation. The
refusal hint names the portal's settings as its intended home; that control does not exist yet.

---

## Step configuration schemas

The `StepConfigSchemaExporter` (in `inference/src/ReelBolt.Shared/Workflows/StepConfigSchemaExporter.cs`) uses reflection to scan the `ReelBolt.Shared.Workflows` namespace for sealed records ending in `StepConfig`. For each `StepType` enum value, it reflectively generates a JSON Schema using .NET's `JsonSchemaExporter` and exposes it via:

- **Inference API:** `GET /api/v1/workflow-step-schemas` (list all) and `GET /api/v1/workflow-step-schemas/{stepType}` (single)
- **MCP:** `ListStepSchemas` is one of the bridged tools, so a CLI client reaches exactly the same schemas: the tool is in the catalog and its call is forwarded to the same C# tool body, with no second implementation.

The schema describes what a step's config JSON must look like. This contract is consumed by the frontend workflow builder component (`workflow-builder-ux`) to:

1. Validate user-built steps client-side before proposing them
2. Render dynamic forms for each step type's config fields
3. Display helpful errors when the user's config doesn't match the schema

---

## Authentication

Every surface uses the **same ReelBolt JWT** the Go API issues (HS256, carrying `sub` userId, `email`,
and the organization claims `org`, `orgRole` and `platformAdmin`; `isAdmin` is still emitted as an
alias of `platformAdmin` for one release). **No new IdP and no separate permission model** — the same
token that authenticates the Inference API's project, workflow and file endpoints authenticates the
assistant and the MCP server. The Inference API validates it with `AddJwtBearer` (issuer, audience,
`IssuerSigningKey`, lifetime) and then re-reads membership from the database; the Go API is the only
issuer and the two share `JWT_SIGNING_KEY`. **The MCP server shares neither**: it reads no signing key
and checks no token (see [It never verifies the token](#it-never-verifies-the-token)).

### Inference API (`/api/v1/assistant/*`)

Standard `[Authorize]` attribute on `AssistantChatController`, `AssistantThreadsController` and
`AssistantToolBridgeController` — JWT extracted from `Authorization: Bearer <token>` by ASP.NET Core's
authentication middleware. Authentication establishes *who* the caller is, never the whole
authorisation: ownership is checked per call (thread `UserId`, the project's owning organization, the
workflow's project), and the platform/organization authority is re-read per call inside each gated
tool body.

### MCP server

**Stdio mode:** `REELBOLT_MCP_TOKEN` is read once at startup and held for the life of the process. If
it is missing the server exits `1` rather than running unauthenticated. The token is **forwarded
verbatim** on every call and is not verified here.

**HTTP mode:** the token comes from the `Authorization: Bearer <token>` header of each request and is
forwarded verbatim. A request with no token — or one the Inference API rejects with `401` — gets the
OAuth challenge described in
[Connecting Claude Code over OAuth](#connecting-claude-code-over-oauth).

**OAuth (hosted):** over HTTP the caller normally presents an OAuth access token instead: a ReelBolt
JWT with a `scope` claim, obtained through the Go API's authorization server. The MCP server treats it
exactly like any other bearer token — it forwards it, and the Inference API validates it. The user's
own consent at `/oauth/authorize` is what granted the scopes.

**A scoped token is not a session.** A token with a `scope` claim may only call
`GET /api/v1/assistant/tool-catalog` and `POST /api/v1/assistant/tool-call` on the Inference API (plus
the engine's decision-calibration read for `assistant.admin`); every other route in the Inference API,
the Workflow Engine and every authenticated Go API route answers `403 mcp_token_scope`. So it cannot reach the REST routes
that the assistant's guards and approval cards stand in front of (workflow edits, egress consent,
billing, switch-org, settings). Stdio `REELBOLT_MCP_TOKEN` is the user's own unscoped session JWT and
stays unscoped. A token whose only scope is `runs.watch` (the plugin's run watcher) may call only `POST /api/v1/assistant/tool-call` with the tool `GetExecutionStatus`. See [`design/mcp-oauth.md`](design/mcp-oauth.md) section 3a.

### Organization scope of a token

A ReelBolt token is scoped to the organization it was minted in: the `org` claim names the active
organization and `orgRole` the caller's role there at issue time, while `platformAdmin` (alias
`isAdmin`) is platform-wide authority. **The MCP server reads none of these claims** — it has no typed
auth context and no opinion about token contents — so organization scoping is entirely the Inference
API's: it re-reads membership from the database and applies the caller's active organization to every
request. To work in a different organization from the CLI, mint a new token by switching organization
in the dashboard (or via the Go API's switch-org endpoint) and restart the stdio server with the new
`REELBOLT_MCP_TOKEN`, or present that new bearer token in HTTP mode; an existing token never changes
organization.

---

## Deployment

### In docker-compose

The MCP service is defined in `docker-compose.yml`. It is a thin proxy, so it is given **no signing
key and no JWT settings at all**:

```yaml
mcp:
  build:
    context: ./mcp
  restart: unless-stopped
  environment:
    INFERENCE_API_URL: http://inference:8080
    PORT: ${MCP_PORT:-3002}
    MCP_TRANSPORT: http
    # Public identities, used only to build the OAuth discovery documents.
    # Neither is read in stdio mode, which needs no OAuth at all.
    MCP_PUBLIC_URL: ${MCP_PUBLIC_URL:-http://localhost/mcp}
    OAUTH_ISSUER_URL: ${OAUTH_ISSUER_URL:-http://localhost}
  depends_on:
    inference:
      condition: service_healthy
  networks:
    - reelbolt
```

The MCP service:

- **Is published through nginx over HTTP**, at `/mcp`, together with its
  `/.well-known/oauth-protected-resource` document and the Go API's `/oauth/*` endpoints and
  `/.well-known/oauth-authorization-server`. nginx preserves the request URI, so the server sees
  `/mcp`. That is what lets Claude Code run an OAuth login — see
  [MCP exposure through nginx](#mcp-exposure-through-nginx).
- **Listens on `MCP_PORT` (default 3002) inside the compose network**, reached by nginx through the
  `mcp` upstream rather than by a published host port.
- **Depends on the Inference API** being healthy, since every MCP tool call proxies through it.
- **Carries no `JWT_SIGNING_KEY`, `JWT_ISSUER` or `JWT_AUDIENCE`.** Handing this container the
  platform's token-signing secret bought nothing — the API authenticates the forwarded token — and it
  leaked material the same image ships to plugin users.

### Inference API settings the assistant reads

| Config key | Default | Notes |
|------------|---------|-------|
| `Assistant:MaxPaidExecutionsPerHour` | `10` | Rolling-hour cap on assistant- and MCP-initiated runs that may hold model or vendor credits (and on revisions), per user + project. Queued runs count. Holds across replicas. |
| `Assistant:MaxFreeExecutionsPerHour` | `60` | The same cap for runs whose steps are all deterministic. |
| `Assistant:MaxExecutionsPerHour` | *(unset)* | Legacy single cap; used as the paid limit only when `Assistant:MaxPaidExecutionsPerHour` is not set. |
| `OutputLinks:DefaultTtlMinutes` / `OutputLinks:PublicBaseUrl` | `15` / empty | Output link lifetime (clamped to 60 minutes) and origin. See [Moving files and outputs in and out over the MCP](#moving-files-and-outputs-in-and-out-over-the-mcp). |
| `FileImport:MaxBytes` / `FileImport:TimeoutSeconds` | `536870912` / `300` | Largest import from a link and the time allowed for the whole download. |
| `Assistant:ExecutionGuardLockWaitMilliseconds` | `5000` | How long a run request waits for the per-user, per-project session lock (0 to 60000). On timeout the run is refused like an over-cap run. |
| `Assistant:MaxOutputTokens` | `16384` | Output-token allowance for one assistant turn's **visible answer**. `ReasoningHeadroomTokens` (8,192) is added on top on a provider that charges thinking to the same ceiling, so an Anthropic turn is sent 24,576 by default and a configured 4,096 is sent as 12,288. Only sent at all — on a kind other than Anthropic — when this key is set. |
| `Assistant:ReasoningEffort` | `none` | How much the model may think before it answers: `none`, `low`, `medium`, `xhigh`, or `auto` to send no reasoning configuration at all. `Anthropic` has a floor of `low` — it answers `none` with a 400. |
| `Assistant:MaxMessageChars` | `32000` | Both halves of a turn: a user message over it is refused with a `400`; an assistant reply over it is truncated with a visible marker. |
| `Assistant:MaxReplayMessages` | `40` | How many of a thread's newest persisted turns are replayed into the next prompt. |
| `Assistant:DetachedTurnSeconds` | `300` | How long a turn keeps running with nobody attached (original connection gone, no re-attached screen, no open permission card) before it is cancelled and its turn-concurrency slot and credit hold are released. Never runs while a screen is attached or a card is open. Separate from `MaxTurnSeconds`, which bounds every turn. |
| `Assistant:MaxRoundTripInputTokens` | `320000` | Most input tokens (estimated: characters / 4 × 1.5) one model round-trip of a charged turn may send; a larger one is refused before it runs (`TURN_LIMIT`). Default: the replay window (`MaxReplayMessages` × `MaxMessageChars` / 4). |
| `Assistant:MaxToolIterations` | `80` | Most model round-trips in one turn's tool loop (min 1); at the cap the model must answer in text. |
| `Assistant:MaxToolCallsPerStep` | `8` | Most tool calls one model step may make; the rest are answered with a plain refusal and not run. |
| `Assistant:ApprovalTimeoutSeconds` | `600` | How long a permission card waits for an answer (1–3600 s) before it expires and the tool is not run. |
| `Assistant:ConfirmationTokenKey` | *(unset)* | Operator override: a Data-Protection-protected base64 HMAC key. When unset, the key is generated and persisted. |
| `Assistant:ConfirmationTokenKeyFile` | `<DataProtection:KeysPath>/assistant-confirmation-token` | Where the generated key is persisted. |
| `DataProtection:KeysPath` | `/keys` | The shared `dpkeys` volume, mounted at `/keys` in both inference services so either can decrypt what the other wrote. |
| `Agents:Assistant:SystemPrompt` | *(unset)* | Overrides the assistant's built-in system prompt. |

### Environment variables (MCP)

| Variable | Default | Notes |
|----------|---------|-------|
| `INFERENCE_API_URL` | *(required)* | Base URL of the Inference API. **No default**: the server only proxies, so it exits `1` when this is unset rather than starting and failing every call. In compose it is `http://inference:8080`. |
| `REELBOLT_MCP_TOKEN` | *(required in stdio mode)* | The caller's own ReelBolt bearer token. Read once at startup, forwarded verbatim, never verified here. Unused in HTTP mode, where the token arrives per request. |
| `MCP_MAX_SUBSCRIPTIONS_PER_SESSION` / `_PER_PRINCIPAL` / `MCP_MAX_SUBSCRIPTIONS` | `5` / `10` / `100` | Run subscriptions one session, one caller and the whole process may hold. |
| `MCP_SUBSCRIPTION_TTL_MINUTES` | `120` | How long a run subscription lives. |
| `MCP_MAX_SESSIONS_PER_PRINCIPAL` | `20` | Open HTTP sessions one caller may hold. |
| `MCP_MAX_SESSIONS` / `MCP_MAX_SESSIONS_PER_IP` | `2000` / `200` | Open HTTP sessions in the whole process, and from one client address (the `X-Real-IP` nginx sends, an IPv6 address counted on its /64). These do not depend on the token, which the transport never verifies: past the per-address cap `initialize` answers `429`, past the process-wide one `503` with `Retry-After`. Raise the per-address value if many users reach the server through one shared egress address. |
| `MCP_MAX_BODY_BYTES` | `1048576` | Largest request body the HTTP transport buffers; a larger one is answered `413`. nginx caps `/mcp` at the same 1 MB and limits it to 600 requests a minute per address (burst 200). |
| `MCP_TRANSPORT` | `stdio` | Set to `http` to serve streamable HTTP instead. |
| `PORT` | `3000` | Port to listen on (HTTP mode only). Compose sets it from `MCP_PORT`, default `3002`. |
| `MCP_PUBLIC_URL` | *(required in HTTP mode)* | The public MCP URL, e.g. `https://host/mcp`. Becomes the `resource` value in the protected-resource metadata. |
| `OAUTH_ISSUER_URL` | *(required in HTTP mode)* | The public origin of the Go API, e.g. `https://host`. Becomes the single `authorization_servers` entry. |

`JWT_SIGNING_KEY`, `JWT_ISSUER` and `JWT_AUDIENCE` are deliberately **not read by this service**. The
two public URLs are required only in HTTP mode and refused when missing, because the server will not
guess a host.

---

## MCP exposure through nginx

nginx proxies the Inference API's public REST endpoints (`/api/v1/*`) so clients can reach them from
browsers and external networks with TLS termination and cookie↔header translation. The two MCP
transports are treated differently, and that split is the current design — an earlier version of this
page said the MCP server was not proxied at all, which is now only half true.

**Stdio is not proxied, and does not need to be.** In stdio mode the MCP server is not a network
service at all: a CLI client spawns it as a child process and they talk over stdin/stdout. There is no
port for nginx to forward, no TLS to terminate, and no browser involved:

- **Browsers cannot maintain stdio connections** — stdio is for CLI tools spawned as child processes
  only.
- **nginx's strength is authentication at the request boundary**; stdio's model is a bearer token
  supplied by the process environment once at startup, which is never a network request.

**HTTP is published through nginx, because OAuth needs it.** The hosted path is what lets Claude Code
connect at all: the client must fetch discovery documents over HTTPS, be sent to a browser to sign in,
and come back with a token. So nginx carries:

| Location | Forwards to |
|---|---|
| `/mcp` | the MCP server's streamable-HTTP transport (the request URI is preserved, so the server sees `/mcp`) |
| `/.well-known/oauth-protected-resource` | the MCP server (RFC 9728) |
| `/.well-known/oauth-authorization-server` | the Go API (RFC 8414) |
| `/oauth/token`, `/oauth/revoke`, `/oauth/register` | the Go API |
| `/oauth/authorize` | the Go API — a browser navigation with a human present, so it stays a page rather than a JSON API |

TLS terminates at nginx as it does for everything else, and the public origin the MCP server
advertises comes from `MCP_PUBLIC_URL` / `OAUTH_ISSUER_URL` rather than a hardcoded host. The
Inference API's assistant endpoints (`/api/v1/assistant/chat`, `/api/v1/assistant/threads`, and the
tool bridge under `/api/v1/assistant/`) still fall under nginx's `location /api/v1/` block.

A self-host install that would rather expose nothing can keep using stdio, which needs none of this —
see [The MCP server](#the-mcp-server).

## Page awareness, titles and docking (QA 2026-10)

- **Docking.** The drawer opens on the RIGHT (bottom sheet on phones); the header trigger sits in the right-hand header group.
- **Who and where.** Every chat turn carries `pageContext` (`path`, `kind`, `title`, `documentTitle`, `tab`, `entityIds`, optional `extra`), derived client-side from the route (`components/assistant/pageInfo.ts`) — with the pages that own ids stating them through `AssistantPageContext` instead (project detail, workflow editor, picture editor; the picture editor's project id is never taken from the route alone, see [Picture editor tools](#picture-editor-tools)). The server treats it as an untrusted hint: `AssistantSessionContext` bounds every field, allowlists ids/paths, strips control characters and brackets, caps `extra` at about 2 KB, and appends the resulting block to the agent's SYSTEM prompt for that turn only (never persisted, so it cannot go stale). Identity (id, email, display name, admin) is read server-side from `ICurrentUser` and the user row; admin authority is still re-checked inside each gated tool.
- **The editor kinds are kept apart** because the assistant's picture tools act in exactly one of them: `picture-editor` (the thumbnail editor — one layered picture, the only page where the picture tools work), `editor` (the video editor, the timeline, which no assistant tool edits), `editor-hub` (the Editor page's Videos list) and `pictures` (its Pictures list, chosen by `?view=pictures` through `Route.byQuery`). The prompt names the difference and the pages' titles carry it, so "make the title bigger" and "tighten this cut" are never answered from the wrong editor.
- **Welcome state** is page-aware (`welcomeFor(kind)`): a greeting plus 2 to 4 suggestion chips per page kind, generic fallback.
- **Thread titles.** The first message (truncated) names a thread immediately; after the first exchange a short separate model call (10 s budget, `AssistantThreadTitler`) replaces it, persisted on the thread and announced with a `data-threadTitle` stream part. The client reloads the history list when a reply settles. Failure keeps the truncated title.
- **History list.** Grouped Today / Yesterday / Earlier, active thread highlighted, relative times, search (shown above 6 threads), inline rename and confirm-delete, empty state.
- **Opening with a prompt.** `useAssistantShell().openWithPrompt(prompt, extraContext?, { send? })`.

## Step catalog and generated facts

`StepCatalog` (in `ReelBolt.Shared/Workflows`) is built from code: a `[StepCard(Purpose, Needs, Produces, CostClass)]` attribute on each step config record (or on the `StepType` member for a type without one) carries what code cannot infer; the config schemas, `StepCompatibilityRules` (the linter's rule table), `BriefFieldConsumers` and `AppCostClass` supply the rest. `StepCatalogTests` fails the build when a `StepType` has no card, when a card's cost class disagrees with the derived one, or when any price appears in a card. `GetStepCatalog(stepType?)` returns the cards; the same cards and one fact per recipe are indexed as `generated-fact` chunks of the documentation search (`docs/platform-docs-search.md`) at index time.

## Recipe-first tools

The assistant is a recipe selector and brief filler, not a graph author (docs/design/workflow-apps.md section 6.1). Four tools in `AssistantRecipeTools` carry it; every one answers in names (recipe titles, voice names, workflow names), never ids a reply could repeat, and re-resolves and re-authorizes its project on each call.

- **`GetProjectReadiness(projectId?)`** (View, free). Files by kind and count (video clips, audio, images, code and text, documents), the design system's status (`none`, `draft`, `approved` with its name and version number, or `linked` to the workspace library), the project's voices (library or cloned, usable now and why not), which AI capabilities the workspace has a provider for (`voice`, `videoGeneration`, `transcription`, `vision`, `musicGeneration`, `soundEffectGeneration`), the plan's limits (tier, tallest output, whether editing rooms and video generation are allowed) and the project's workflows with the recipe each came from. It reads the same `ProjectReadinessFacts` the app gallery uses, so the two cannot disagree. Audio files are one count: the platform does not tell music from sound effects.
- **`EstimateWorkflowRun(workflowId | templateKey, brief?, projectId?)`** (View, free). Exactly one of a saved workflow or a recipe the project does not have yet. A recipe is priced from a dry instance built by `WorkflowTemplateProvisioningService.BuildDryStepsAsync` (the steps provisioning would create, in memory, never saved). The same `IWorkflowCostEstimator` the estimate endpoint and the run card use gives the usual range, what the workspace needs available to start and the paid items (AI video clips, a narration voice); the brief's `resolution` changes the price. The answer carries the cost class (a class, never a vendor price) and a one-line `summary` to say before running. With billing off it says so and invents no numbers.
- **`ProposeCreateFromTemplate` / `ApplyCreateFromTemplate`** in the chat, **`CreateFromTemplate`** over MCP (Edit; the chat Apply is gated by name like every `Apply*`). `templateKey`, an optional `brief`, optional `patches` and an optional `name`. The server instantiates the recipe (`RecipeInstance`), binds the brief's settings that live in a step's own config (the narration language and project voice on a `Voiceover` step, the target length on the compile step, only where the recipe left them empty), applies the patches, runs the normal save validation, the provider-ownership check and the plan gate, then lints. A structural lint error is refused with the findings and no token. An error about the project's state (WL006, WL007, WL008, WL010, WL012) that the plain recipe already has is reported as `needsBeforeRunning` and does not block; a patch that newly causes one is refused. Warnings and notes come back as `warnings`. The workflow is named from the recipe title and made unique ("… (2)"); the chat proposal lists `alreadyFromThisRecipe` so the model sees it is about to duplicate one (the reuse rule: run that one instead). The confirmation token binds `{projectId, templateKey, normalized brief, patch ids, workflow name, hash of the final steps}` plus the user and the organization, recomputed from the arguments on Apply; over MCP it is minted and consumed inside the one call. The MCP result is the Apply half's `{workflowId, workflowName, projectId, projectName, recipe, patchesApplied, stepCount, costClass, brief, needsBeforeRunning, hint}`; the proposal's `summary`, `steps`, `warnings` and `alreadyFromThisRecipe` are not returned on that surface, so an MCP client cannot see them before or after the write. The new workflow gets `created_via = assistant`, the thread id, its `TemplateKey` and the recipe's curated app manifest. Neither tool starts a run. The chat create card (`AssistantApprovalCardBuilder.CreateFromTemplateAsync`) reads "Create {name} from the {recipe} recipe" and lists the recipe, the step count, each patch, the brief's fields, the cost class and the lint notes; creating costs nothing.

### The closed patch catalog

`RecipePatches` (`Services/Workflows`) is the whole vocabulary of changes to a recipe: `swapToRoom`, `addVoiceover`, `addCaptions`, `addMusic`, `addSfx`, `addColorGrade`, `addGraphics`, applied in that canonical order whatever order was asked (so a set of ids gives one graph and one token). Each is a pure transform of the recipe's steps: a planning or narration step is inserted before the compile step, the compile step gets its two members (`enable…` and the plan reference, in the position the built-in recipes use), the analyze step gets its flag (`offerMusicTracks`, `offerSfxClips`, `emitOverlayPlacements`), a compile reference that meant "the step before me" is pinned to the step it meant, and every later step reference is renumbered. `addVoiceover` writes `NarrationPlan` narration in the footage family and a `ScriptScenes` voiceover before the director in the promo family (the review loop's target moves with it); `addCaptions` needs the narration; `swapToRoom` replaces the single story editor with an edit room. A patch is **refused, never improvised**, with a plain reason, when the recipe has no cut to edit (the green-screen and region-effect recipes, the profile build), when the feature is already on, when the compile is a stream copy, when `addCaptions` has no narration, or when the project has no audio files for `addMusic` / `addSfx`. `RecipePatchesTests` pins each footage patch against the built-in recipe that already has the feature (the patched highlight edit equals `video-derush-edit-music`, `-sfx`, `-graphics`, `-grade-room`, `-room` and `-voiceover`).

### The "Choosing a recipe" prompt section and `Assistant:RecipeFirst`

The recipe-first procedure (docs/design/workflow-apps.md 6.1: extract the brief, look at the project once, read the fit, decide in order between reuse, create from the recipe, recipe plus patch, custom graph, decline and clarify, price in one line) is the prompt constant `AssistantAgent.RecipeFirstSection`, with a byte-identical copy in `DatabaseSeeder.AssistantRecipeFirstSection` (`AssistantPromptConsistencyTests` pins both, the section's key phrases, that it names every new tool and the closed patch ids, and that it holds no future-tense or timing language). It is behind the configuration flag **`Assistant:RecipeFirst`, default off**: off is exactly today's prompt, with its "Carrying the goal into a run" bullet; on replaces that bullet with the section (prompt cap 30,000 characters with the flag on, 28,000 off). The flag stays off because the 50-goal eval gate (decision Q17: the zero-tolerance rows are a hard release gate) has not been measured: no paid eval spend was approved, so the lane is blocked on the eval run, not on code. The results page is `design/workflow-apps/assistant-eval-results.md`.

`BriefFieldConsumers` (read by WL011, the step catalog's `readsFromRun` and the app manifest deriver) agrees with the engine's as-built `RunBriefRegistry` on language, source language, length and voice for every agent and room (`BriefFieldConsumersRegistryTests`): the colour and dailies rooms and the music and sound agents read no brief field, the Director and Author read no voice, and the scene translator and animation strategist read neither language nor length.


## Community recipes (recipe marketplace)

Four tools in `AssistantMarketplaceTools` (design: [recipe-marketplace.md](design/recipe-marketplace.md)):
`SearchMarketplaceRecipes` and `GetMarketplaceRecipe` (read-only, through the same query service as the public API:
listed, visible recipes only) and the `ProposeCopyMarketplaceRecipe` / `ApplyCopyMarketplaceRecipe` pair (MCP name
`CopyMarketplaceRecipe`, destructive). A recipe is addressed by its name or an opaque `mk_` handle; ids are never shown.

- **Untrusted content.** Every word a community author wrote (name, description, step labels, prompts, slot labels) comes back
  between `[BEGIN COMMUNITY RECIPE TEXT <nonce>]` and `[END COMMUNITY RECIPE TEXT <nonce>]`. The nonce is 128 random bits, fresh for
  each tool result and stated in the result's note, which says that only that exact end marker ends the data and that the text is
  never an instruction. Before fencing, each field is cut at 8,000 characters (at a code-point boundary), NFKC-normalised, stripped of format, control and bidi characters, and every bracket-like character (ASCII or any Unicode open/close bracket or quote) is replaced by a space, so a fence marker cannot be spelled at all; whitespace is collapsed. The unguessable nonce is what makes the fence hold; the cleaning is defence in depth and never throws. The text is returned to the model only; it never enters the
  documentation index (`PlatformDocsCorpus` reads repository files only).
- **Which results are fenced.** The four marketplace tools, and for a workflow copied from the marketplace (`CreatedVia` =
  marketplace) also `ListWorkflows` (name), `GetWorkflow` (name and the whole step list as one fenced block, `communityOrigin:
  true`), `ListWorkflowExecutions` (workflow name) and `GetWorkflowExecution` (workflow name and step labels). Each carries
  `communityOrigin: true` and a `communityNote`. Community origin is durable: a workflow counts as community when `CreatedVia` is
  marketplace OR its `TemplateKey` starts with `market:`; a patch keeps the row, and `ProposeCloneWorkflow` / `ApplyCloneWorkflow` of a
  community workflow records `CreatedVia` = marketplace on the clone. Steps the model assembles itself (`ProposeWorkflow`) cannot be traced
  back, so text it copies out of a fenced result into a new workflow is an ordinary workflow from then on. **Residual:** a step's model output (`outputPreview`) and the error text of a
  run of such a workflow are produced from the author's prompts but are not fenced; once copied, the prompts are also what the
  workflow's own agents execute. Other tools that read a project file or a step configuration directly are not marked.
- **Copy.** Propose returns a token (HMAC over the operation, recipe id, current version id, project id and the copy's name, plus
  user and organization) and a plan: name, author label, step types, cost badge, prompts in full, open slots. Apply re-reads
  Edit authority, recomputes the payload from the stored current version (a changed version, project or name is refused as a
  stale token) and calls `MarketplaceCopyService`, the service behind the REST copy: idempotent per recipe, project and name.
  It starts no run, charges nothing and leaves egress consent off. The approval card is built from the database and the exact
  arguments. The MCP bridge installs no approval gate, as for its siblings; the verified-email and Edit checks are the same.
- **Publishing is not an assistant or MCP action.** Consent and preview are a human act in the app.
- **Flag.** The four tools are always registered, so the closed set does not move with `Marketplace:Enabled`. With the flag off
  each answers the same fixed "Community recipes are not available." (`NOT_AVAILABLE`), which says nothing about any recipe.
