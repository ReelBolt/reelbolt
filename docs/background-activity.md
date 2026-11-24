# Background activity

How slow work (renders, analyses, AI edits, uploads) shows up to the user while they keep working, and how a new job type or a tool that changes the document plugs in. Each `##` section stands alone.

## What the user sees

- **Header indicator** (`web/components/activity/ActivityIndicator.tsx`): always visible. Shows the number of active jobs, a spinner while one runs, and a dot when a job failed (and was not yet seen) or one is waiting for the user's OK.
- **Tray** (`ActivityTray.tsx`): opens from the indicator, on desktop and on the phone (including the phone editor). Sections, in this order: "Needs your OK" (`needsReview`), "Running now" (waiting and running), "Recently finished" (done, failed, cancelled). "Clear finished" dismisses the finished ones for this browser (`web/lib/activity/dismissals.ts`, per workspace, capped at 200 ids).
- **Row**: type label (plus the item's name when it has one), project, state, a progress bar when the job knows its progress ("Step N of M" for runs), cost, and the actions Open (the registry `href`), Cancel (only when the job is `cancellable`, after a confirm) and Dismiss.
- **Cost** (`JobCost.tsx`): "About N credits held" while a job runs, "N credits used" when it ends. Nothing is shown when the figure is unknown or a finished job charged nothing.
- **Failure** (`JobFailure.tsx`): a plain sentence per failure code (`failure.*` keys in `web/messages/{en,it}/activity.json`, `generic` for an unknown code) plus "Your credits were given back." when `failure.refunded` is true, or "Credits used up to the failure were kept." when it is false.
- **Notifications** (`web/lib/activity/notifier.ts`): when a job the user started finishes (or needs their OK) while they are NOT on the job's own page, a toast appears ("`{label}` is done.", "did not finish", "was cancelled", "finished and needs your OK", with "Your credits were returned." after a refunded failure). Only transitions seen in this page session notify; whatever was finished at first load is the baseline. A per-device switch "Tell me when a background job finishes" (`reelbolt.activity.toasts` in localStorage, default on) mutes toasts for successes and failures. When the tab is hidden, the browser notification of the assistant (`getNotifySwitch()` in `web/lib/assistant/browser-notify.ts`, opt-in, key `reelbolt.assistant.browserNotify`) also fires if the permission is granted. Media prep and media analysis do not announce a success (their failures still notify); a job type can mute itself with `notifyOnFinish: false`.
- **In place status**: the section that started the work shows `InlineJobStatus` (`web/components/activity`) for the matching job, found with `useJobFor`, so the user sees the same state in the tray and where they clicked.
- **Editor "AI tools" button** (`web/components/editor/shell/AiToolsLauncher.tsx`): one searchable list of every AI tool of the editor, grouped Picture, Sound, Edit, Check. Each row shows free or credits, a "running" chip while a job of that tool's `activityTypes` is active in the project, and a "select a clip first" hint when the selection does not meet `needs`. On the phone it is a bottom drawer opened from the phone header.

## Server read: GET /api/v1/activity

`ActivityController` (`api/v1/activity`, `[Authorize]`) returns `ActivityFeedDto { items, serverTime, activeCount }`, built by `ActivityFeedService` from existing tables only (no migration), read-only, `AsNoTracking`, a small fixed number of queries.

- **Item** (`ActivityItemDto`, camelCase on the wire; web type `ActivityItem` in `web/lib/activity/types.ts`): `id` (`<source>:<guid>`, stable across polls), `type`, `state` (`waiting|running|done|failed|cancelled|needsReview`), `projectId`, `projectName`, `title` (a name, never an id or storage key), `executionId`, `workflowId`, `subject` (`timelineId`, `documentId`, `mediaRef`, `sourceExecutionId`, `linkId`, `analysisKind`, `kindNumber`; only the applicable ones are set), `step` (`order`, `count`, `stepType`), `progress` (0..1 or null), `credits` (`heldCredits`, `chargedCredits`), `failure` (`code`, `refunded`), `startedByYou`, `cancellable`, `createdAt`, `startedAt`, `endedAt`.
- **Server types today**: `workflowRun`, `designSystemBuild`, `revision`, `changeRequest`, `graphic`, `render`, `preview`, `loudness`, `audition`, `objectAnalysis`, `mediaAnalysis`, `hiddenRun`. The server never sends `needsReview` today: that state comes only from client jobs (see "Tools that change the document while running").
- **Scope**: the caller's active organization. No workspace, or the Billing role (no project access, hidden from the event stream too), returns an empty list. Every other role needs `View`; there is no per-project filter beyond the organization. `cancellable` also depends on the `Edit` permission.
- **Windows**: queued or running jobs younger than 7 days (`ActiveWindow`); finished jobs for 24 hours (`FinishedWindow`). Scans are bounded (1000 active, 300 finished per source).
- **Ordering**: running (oldest first), then waiting (oldest first), then finished by `endedAt`, newest first.
- **Limit**: `?limit=` defaults to 50, clamped to 1..100 (`ClampLimit`). `activeCount` counts all running and waiting candidates, not only the page.
- **Credits**: `heldCredits` is set while billing is on for media analysis requests with an unsettled hold and for runs that hold credits (the tray shows "About N credits held" for a running run too); it is `null` when no hold is known. Both figures are CREDITS on the wire (the server divides millicredits by 1000), and `JobCost` and `trackJob`'s `creditsEstimate` take credits. `chargedCredits` comes from the step results.
- **Failure refund**: `ActivityFailureRefund.IsRefunded` is the same switch as the engine's `CreditGate.IsRefundable`; a test walks every `FailureClass` against the engine so the two cannot drift.
- **NOT listed, on purpose**: media prep (`media_derivatives` has no queued or running state, a row exists only once built; progress is live over the event stream only), cloud drive sync (`project_cloud_links` has no syncing state, only last-synced and last-error without a time), folder sync, and file indexing. Media prep and cloud sync still have job types so a client or stream-driven job renders correctly.
- **Client side**: `ActivityProvider` fetches `/api/v1/activity?limit=50` (a 404, a network error or a bad body is an empty list), refreshes every 4 s while something is active and every 30 s otherwise, and refreshes again (500 ms debounce) on stream frames.

## The shared event stream

`web/lib/activity/events.ts` holds ONE ref-counted `EventSource` to `/api/v1/workflows/events`. The Go API caps that stream at three connections per user, so every consumer (the activity provider, the dashboard, the workflow monitor, the media derivative progress) must subscribe through it and never open its own `EventSource`.

- `subscribeWorkflowEvents(types, handler)` returns an unsubscribe function; `useWorkflowEvents(types, handler)` is the hook; `useWorkflowEventsConnection()` reports `connecting|connected|reconnecting`.
- The stream opens when the first subscriber arrives and closes when the last leaves. It carries the cookie it was opened with, so a workspace switch must re-open it (`resetWorkflowEvents()`, done by the hook).
- A new consumer that needs another event type adds it to its `types` list; it does not need a connection.

## Add a job type

A job is a server item, a client-tracked job, or both. Checklist:

1. **Register the type** once at module scope (or in `web/lib/activity/builtins.ts`): `registerJobType({ type, icon, labelKey, href, cancel, notifyOnFinish })`. `icon` is a Tabler component, `labelKey` is under the `activity` namespace (`types.<type>`), `href` is the "Open" destination without the `/app` base path (`null` = nowhere), `cancel` stops the work (offered only when the job is cancellable). A type nobody registered renders with `GENERIC_JOB_TYPE`.
2. **i18n**: add `types.<type>` to `web/messages/en/activity.json` and `web/messages/it/activity.json` (plain words, no technical names).
3. **Server job**: add the type arm in `ActivityFeedService` (`BuildRun` switches on `WorkflowDefinitionKind`; `BuildAnalysis` handles analysis rows). Set `title` to a name, fill `subject`, and decide `cancellable`. Add the type to `ServerActivityType` in `web/lib/activity/types.ts`.
4. **Synchronous or client-only work** (an upload, a picture operation, a call with no server row): wrap it in `trackJob(meta, work)` from `useActivity()` / `useTrackedJob()`. `work` is a promise or `(signal, jobId) => promise`; `cancel` aborts the signal first. Set `executionId` when the work mirrors a server run so the run is listed once (server item wins while it runs, client job wins while it needs the OK). Move on with `updateTrackedJob(id, { state, progress, liveStage })`; `estimating: true` starts in the cost-estimate phase.
5. **In place status**: `const job = useJobFor((j) => ...)` and render `InlineJobStatus`, so the section shows the same state as the tray.
6. **Needs your OK**: a client job calls `markNeedsReview` with a `review` (`summaryKey`, `onApply`, `onDiscard`); the tray shows Apply and Discard.
7. Test the notifier case (finish while elsewhere) and the failure copy.

## Tools that change the document while running

A tool that starts slow work and then changes the user's document (the edit timeline or a picture) must not write blindly over a document the user kept editing. The rules:

1. **Anchor the result** to stable things, not to the revision at launch: the source media, the clip ids, and the source-time range analysed (`ResultAnchor` in `web/lib/editing/background/anchor.ts`: `assetKey`, `clipIds`, `assetClipIdsAtLaunch`, `coverage`, `launchWindows`). Build it with `anchorForClips`.
2. **Rebase when it lands** onto the CURRENT document with `rebaseAnchor(doc, anchor, canApply)`. It returns `{ kind: 'apply', clipIds }` or `{ kind: 'needsReview', applicable, reasons, lostCount }`.
3. **Ask when it does not fit** ("Finished, needs your OK"): the user applies what still fits, or discards. Nothing the job paid for is dropped silently.
4. **Warn before an edit that hits a running job**, and **apply as one undo step**.

### Rebase rules

- Tolerance is one frame (`1 / fps`; 30 fps when the rate is 0). Source windows that touch within the tolerance count as the same footage.
- A clip counts as a target when its id is in `clipIds` and it still plays the same asset. Trim, move and speed change which part of the source a clip plays; the target stays a target and the result is checked against `coverage`.
- **Split**: pieces made after launch (not in `assetClipIdsAtLaunch`) that share source time with a target, or with where a target played, join the targets, so the result lands on every piece. A split-and-deleted original is recognised through its pieces (`launchWindows`).
- **Reverse, speed (`playbackRate`) and hold** change the window `clipSourceWindow` computes (a hold plays one instant); the window, not the timeline position, decides.
- **Compound clips**: clips inside `doc.sequences` are real clips (`allMediaClips`), so a clip moved into a compound is still found.
- A target outside `coverage` is lost with reason `outsideCoverage`; a missing target is `targetGone` (or `assetChanged` when the id exists but plays other media); a clip that cannot take the effect (`canApply`, e.g. the region effect cap) is `limitReached`. No target at all is `targetGone`.
- Any reason at all means `needsReview` with `applicable` (the clips it still fits) and `lostCount`.

### Pieces in the editor

- **Results store** (`results-store.ts`): `ResultsStore` per timeline, outside React, saved to localStorage (`reelbolt.editor.bg.<userId>.<timelineId>`, capped at 50). A `BackgroundEntry` has `tool`, `requestId`, `executionId`, `anchor`, `intent` (tool-specific JSON), `state` (`running|needsReview|applied|discarded|failed`), `review`, `failure`, `stalled`. `getResultsStore(timelineId)`, `useBackgroundResults`.
- **Watcher** (`useBackgroundResultWatcher`): mounted ONCE by the editor workspace, never by a panel. Polls running entries, adopts server-launched results (every 30 s, from the list route below), rebases on landing and applies, or parks as needs-your-OK. Selecting something else or closing a panel drops nothing.
- **Edit guard** (`edit-guard.ts`, `useBusyEditGuard`): an edit that deletes, trims, moves, splits, slips, replaces or retimes a clip a running job works on (`affectedClipIds`; `setProperty` only for `playbackRate`, `sourceIn`, `reverse`, `hold`, `assetRef`) is held and the user chooses to go on (the result follows the edit), stop the job, or go back. "Go on" is remembered per job for the session. Undo, redo, remote changes and the watcher's own write never reach the guard.
- **One undo step**: the landing writes use `editor.dispatchBatch(ops, label)` (`object-effect.ts`, `proposal.ts`).
- **Bridge** (`useBackgroundActivityBridge`): mirrors the entries into the tray as client jobs (magic blur and AI proposals), so "needs your OK" shows in the tray too.
- **Proposals** (`proposal.ts`): an AI proposal is a background tool; its answer is kept when the panel is gone, and `applyWhatFits` applies the groups that still fit as one undo step. A proposal is checked against the current timeline: a group whose parts changed since it was made goes to needs-your-OK instead of landing. Proposals are not rebased by source time (no op rebasing); a changed part is the user's decision. When the edit warning holds an edit, `dispatchBatch` returns false, and Apply / Accept then leave the proposal waiting rather than marking it applied.

### Server side

- **Magic blur** (`ObjectEffectApplier`): when the timeline changed since the request (`TimelineChangedSinceAsync`) or a target clip is gone (`ApplyOutcome.NeedsReview`), the request goes to status `needs_review` instead of writing. Nothing on the timeline is changed. Routes in `ObjectAnalysisController`:
  - `GET /api/v1/projects/{projectId}/edit-timelines/{timelineId}/object-requests?state=needs_review` (View; any other `state` is 404; up to 50, oldest first) lists the waiting results with the clip ids, effect settings, `coverageEndSec`, `stepResultId`, `trackIds` and `workflowId`.
  - `POST /api/v1/projects/{projectId}/media/{mediaRef}/objects/{requestId}/resolve` with `{ "action": "applied" | "discarded", "revision" }` (Edit). Idempotent, race-safe against the sweeper's claim. Anything but those two actions is 400.
  - The assistant's `ApplyObjectEffect` answers `status: "needs_review"` in this case (see [platform-assistant-mcp.md](platform-assistant-mcp.md)).
- **Picture operations and graphics**: when the layer a result was made for was deleted while the job ran, the result is inserted as a HIDDEN layer and the response carries `needsReview: { reason: "layerGone", layerId }` (`ImageNeedsReviewDto`; `EditImageOperationsController` for operations, `GraphicDesignService` for graphics, where it is also present on re-read until the person shows the layer). `layerId` is the NEW hidden layer. The client offers "Show it" (layer PATCH `visible: true` with `baseRevision`) or "Discard" (layer DELETE). The paid result is never dropped.

### Audit: which tools comply

| Tool | State |
|---|---|
| Magic blur (object effects) | Complies: anchor, rebase, results store, guard, `needs_review`. |
| AI proposals (recut, graphics, grade, sound effects, review) | Complies: busy range, edit guard, kept answer, apply what fits. |
| Picture operations and graphic designer | Complies: busy layer mark, warning before editing it (delete, move, crop, merge), hidden insert with `needsReview`, kept across a reload. |
| Audio cleanup (`audio-cleanup.ts`) | Complies: the panel registers the run (`entryForCleanup`, whole-source coverage), the watcher polls `GET .../media/{mediaRef}/cleanup/{executionId}`, and on `ready` lane B's `planApply` runs for every target piece (split pieces included) as ONE `dispatchBatch`; a gone target goes to "needs your OK". The run cannot be stopped from here (no stop route), so the guard offers go on or back only. |

### Add a document-changing tool

Worked example: audio cleanup (`web/lib/editing/background/audio-cleanup.ts`). It builds the entry at launch (`entryForCleanup`), is polled by the watcher, lands through `landCleanupResult` / `settleCleanup` with the tool's own plan (`cleanupApplyPlan`), and has one arm each in the bridge, `PendingResults` and the guard's tool name. Server: `ActivityFeedService` maps the hidden workflow kind to `audioCleanup`.

1. Build the `ResultAnchor` at launch (`anchorForClips`), and a `BackgroundEntry` with a new `tool` name and its `intent`.
2. Register the entry in the results store; have the watcher poll it (or adopt it from a server list route).
3. On landing call `rebaseAnchor` with a tool-specific `canApply`; on `apply` write with ONE `dispatchBatch`, on `needsReview` park it with its `review`.
4. Extend `affectedClipIds` if the tool's own ops change which clips count as busy; keep the tool's own write out of the guard.
5. Server side: never blind-write the edit timeline over a changed document. Detect the change, move the request to a `needs_review` status, add a list route (View) and a resolve route (Edit, idempotent), and never drop a paid result. Pictures: hidden insert plus `needsReview`.
6. Add the tool to the audit table above.

## Add an editor AI tool

`registerEditorAiTool(def)` in `web/lib/editing/ai-tools.ts` adds a row to the editor's "AI tools" list; registering the same `id` again replaces it. Call it once at module load (built-ins are in `web/lib/editing/ai-tools.builtin.ts`). A definition has:

- `id` (unique), `icon`, `group` (`picture|sound|edit|check`).
- `labelKey`, `descriptionKey` under `editor-ai-tools` (`tools.<id>.label`, `tools.<id>.description`); add both to `web/messages/{en,it}/editor-ai-tools.json`. Plain words.
- `cost` (`free|credits`) and `needs` (`none|clip|videoClip|range`; `toolNeedMet` checks it against the selection).
- `activityTypes`: the job types that mean "this tool is running" (the chip).
- `open(ctx)`: opens the tool through the `EditorAiToolContext` the workspace gives it (`openPanel`, `openWorkspace`, `openRemoveSilence`, `openInspectorSection`, `openAssist`, `openDeliver`, phone `openSheet`). A tool never reaches into the workspace's state.

No change to the launcher is needed. If the tool starts slow work, also follow "Add a job type", and if it changes the document, "Add a document-changing tool".

## Picture review, storage and workspaces

- **Picture review persistence**: a picture result kept hidden because its layer was deleted is remembered per user and document in `reelbolt.editor.picreview.<userScope>.<documentId>` (layer ids, capped at 50, dropped once the layer is gone or shown; `picture-review-store.ts`). After a reload the review bar offers it again and the tray lists it again under the stable job id `picreview:layer:<documentId>:<layerId>` (a designed graphic uses `picreview:graphic:<graphicId>`), so reopening the editor never adds a second row. Keeping the result hidden is not an undo step: undo does not reveal it. Operations have no read route, so this device-side memory is what brings them back.
- **Sign-out** wipes the editor's per-user local data, including `reelbolt.editor.bg.` (results store), `reelbolt.editor.picreview.` (picture review) and the `reelbolt.activity.*` keys (dismissals and the seen-failures mark).
- **Client jobs are per workspace**: a client-tracked job (upload, picture operation, suggestion) carries the workspace it started in and is not shown after a switch to another workspace. `trackJob` takes an optional stable `id` (tracking it again re-attaches to the same row) and `quietTitle` (the title may hold the user's own text, so it shows in the tray row only, never in a toast or notification).
