# Realtime Workflow Execution (SSE)

This document describes the production-ready realtime execution updates flow across Go API, WorkflowEngine events, and the execution detail UI.

## Overview

- Transport: Server-Sent Events (SSE)
- Event source of truth: WorkflowEngine events published via RabbitMQ
- Go API role: read-only consumer of RabbitMQ workflow events and SSE fanout gateway
- Frontend role: execution page subscribes to per-execution SSE stream and updates step bubbles live

## Endpoints

### Per-execution stream (primary)

- `GET /api/v1/workflows/executions/{executionId}/events`
- Auth required (`middleware.Auth`)
- Stream content type: `text/event-stream`
- Keepalive: comment ping (`: ping`) every 25s

Security rules (organization-scoped, A5):

1. The execution must exist.
2. The caller's **active organization** must own it. Anyone with a project-capable role in that
   organization — Owner, Admin or Member — may watch every execution in it, whoever started it.
   Platform administrators get no extra reach: a platform-wide view lives under `/api/v1/admin/*`,
   never on this stream.
3. An execution belonging to **another organization, or an unknown id, or a caller with no active
   organization at all** returns `404` — never `403`, because existence must not leak
   (decision D5).
4. The only `403` is a caller inside the right organization whose role may not see executions:
   the **Billing** role, which covers billing pages only.

Legacy compatibility route (still available):

- `GET /api/v1/projects/{projectId}/workflows/{workflowId}/executions/{executionId}/events`
- Adds strict tuple validation (`executionId`, `projectId`, `workflowId`) before streaming

### Global stream (filtered)

- `GET /api/v1/workflows/events`
- Auth required
- The stream is subscribed to the caller's **active organization**, not to the caller alone: every
  member with a project-capable role sees the organization's executions whoever started them, and
  nobody sees another organization's.
- A caller with no active organization, or one holding the **Billing** role, gets `404` and no stream
  at all (the same "existence never leaks" rule as the per-execution route).
- Each event is still filtered per connection against the execution's organization, memoized for the
  life of that connection. A lookup error denies the event rather than forwarding it, so a transient
  database fault cannot leak one.
- Switching workspace re-issues the session and the client reconnects; an already-open stream keeps
  the organization it connected with, which is exactly the stale state to avoid.

## SSE operational guards

- Per-user stream cap enforced in Go API hub (`maxConnectionsPerUser = 3`)
- If cap exceeded, endpoint returns `429 Too Many Requests`
- Slow clients are non-blocking and may drop events (bounded channel strategy)

## Event contracts

Event contracts were extended with optional metadata fields for richer UI rendering while remaining backward-compatible.

### `WorkflowStepCompleted` optional fields

- `projectId`
- `workflowDefinitionId`
- `stepOrder`
- `stepLabel`
- `stepType`
- `iterationNumber`
- `agentType`

### `WorkflowExecutionCompleted` optional fields

- `workflowDefinitionId`
- `initiatedByUserId`

### `WorkflowExecutionFailed` optional fields

- `projectId`
- `workflowDefinitionId`
- `initiatedByUserId`

## Frontend execution page behavior

The execution detail page (`web/app/(app)/projects/[id]/workflows/[workflowId]/executions/[executionId]/page.tsx`) uses `useExecutionStream` and subscribes to the per-execution endpoint.

- Reconnect strategy: exponential backoff (1s to 8s)
- Stream state: `connecting | connected | reconnecting | closed` — shown only inside the page's **Details** disclosure (see below), never up front
- Every non-`step.progress` event triggers a REST snapshot refresh; `step.progress` only repaints the step diagram locally

## What the execution page shows customers

The page is written for someone waiting on a video, not for someone debugging an agent. Each
`##` rule below is load-bearing; the tests in `page.test.tsx` next to the page pin them.

- **Run name, not a hash.** The title is `Run N · 4 Oct, 14:02`: `N` is the run's ordinal among
  its workflow's executions ordered by `startedAt` (unstarted last, ties by id) — `runOrdinal` in
  `web/lib/utils/run-summary.ts`, the same rule `OutputsController` uses for `RunNumber`, so the run
  page and the Renders tab always agree.
- **Warnings first (contract C1).** Every step result's `outputJson` may carry a top-level
  `warnings: [{ code, message }]`. `collectRunWarnings` gathers them across the run, deduplicates by
  `(code, message)`, and `RunWarningsAlert` (`web/components/outputs/`) renders one yellow alert
  above the status card. Known codes get a headline (`WARNING_TITLES`); the server's `message` is
  shown verbatim, so it must be written for customers.
- **What is happening now.** For each running step: `stepProgressPhrase` (`web/lib/utils/agent-labels.ts`,
  which resolves any spelling of an agent type and delegates to the shared `agent-roles.ts` the
  workflow builder uses) plus the step's name and its latest `step.progress` stage.
- **Time left is an estimate, labelled as one.** `estimateRemainingMs`: the mean `durationMs` of
  the latest completed, non-cached result per step × the steps still to go, minus what the running
  step has already spent (never below 10% of the mean). Null — shown as "Estimating…" — until one
  step has really run.
- **Telemetry behind "Details".** Stream state, token counters, `Avg …ms/step`, iteration count,
  the execution id, each step's token tiles and raw Data tab, tool-call/reasoning events and every
  event's raw payload appear only when the disclosure is open. The activity feed and event cards
  name agents by role, never by their upper-cased enum.
- **No entrance animation on the summary card.** It used to fade in from `opacity: 0` through
  framer-motion; wherever animation frames are throttled (a background tab, a hidden preview pane)
  the card stayed part-way through the fade and rendered dimmed. Content must never depend on an
  animation finishing to become fully visible.
- **375px.** The four stat tiles are a `SimpleGrid` with `cols={{ base: 2, sm: 4 }}` and labels of
  at most 12 characters that never wrap (`white-space: nowrap`).

## Notes

- SSE payloads are treated as eventually consistent. UI performs snapshot refresh on new events to ensure final state correctness.
- Existing consumers remain compatible because new event fields are optional.

## The render list and what it tells the Renders tab

`GET /api/v1/projects/{projectId}/outputs` (`OutputsController.ListOutputs`) lists every step result
whose `OutputStorageKey` is a playable output of this project — under
`projects/{projectId}/outputFiles/` or, for a generated clip, under
`projects/{projectId}/agentFiles/generated/` (QA-P0-22) — newest first. Its response,
`OutputVideoResponse`, keeps its original six members and appends run context so the Renders tab
can name a render instead of showing its GUID file name:

| Field | Meaning |
|---|---|
| `workflowDefinitionId`, `workflowName` | The workflow that produced it (null if the workflow was deleted). |
| `runNumber`, `runStartedAt`, `executionStatus` | The run's ordinal among that workflow's runs (by `startedAt`), its start time and status. |
| `stepLabel`, `stepType`, `iterationNumber` | The producing step, and which review-loop round produced this attempt. |
| `durationSec`, `width`, `height` | Read tolerantly from the step's `OutputJson` (`outputDurationSec`/`durationSec`, `outputWidth`/`width`, or a nested `output`/`resolution`/`canvas` object); null when not recorded — the player then measures the video itself. |
| `isFinal` | True for exactly one render per run: the latest completed render of a run whose status is `Passed`. Every earlier render of that run (review-loop rejects) and every render of a run that did not pass is a draft. |
| `warnings` | Every C1 warning from **any** step of that run, deduplicated — a Voiceover step's warning explains what is wrong with the compile step's video. |

Context is resolved with separate lookups, never navigation projections, so a render whose step or
workflow has been deleted still lists with null context. Malformed `OutputJson` degrades to nulls,
never an error. The web maps these to a title (`renderTitle`: "60s product story — Run 2"), a
Final/Draft/In progress badge (`renderStage`) and a download file name (`renderDownloadName`), all in
`web/lib/utils/run-summary.ts`.

## Sharing a render: owner-only by design (follow-up: public links)

**Share** on a render shares the authenticated download URL
(`/api/v1/projects/{projectId}/outputs/{stepResultId}/download`). That endpoint is owner-scoped:
nginx turns the `reelbolt_token` cookie into a bearer token and the controller requires the caller
to own the project. So the link opens only for the signed-in project owner, and the UI says so
under every player ("Links open only for people signed in to this project").

The button never fails silently: the Web Share API when present (closing the sheet is not an
error), otherwise the clipboard with a "Link copied" notification, otherwise a legacy selection
copy, otherwise a red notification that shows the link to copy by hand (`shareRenderLink` in
`web/components/outputs/OutputPlayer.tsx`).

**Public or expiring share links are a deliberate follow-up, not implemented.** Doing it safely
needs pieces the current auth model does not have: an unauthenticated nginx location that does not
inject a bearer token, a signed, revocable, expiring capability token (an HMAC over
`{stepResultId, expiresAt}` with a per-deployment key, plus a revocation list or a stored share row)
verified by a dedicated anonymous endpoint that serves only that one object with Range support, and
UI to create, list and revoke links. Reusing the owner endpoint with a long-lived token in the URL
would leak a credential that grants the owner's whole account.
