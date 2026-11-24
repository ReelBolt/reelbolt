---
title: Composition revisions
description: How ReelBolt snapshots a Remotion film's project after every render and revises it later — storage, sandbox transfer, the revision runner, the Revise step and the API.
---

# Composition revisions

A finished Remotion film can be changed without re-running its workflow: the project that rendered
it is snapshotted as a **composition workspace version**, and a **revision** restores a version into
a fresh sandbox, lets the `CompositionReviser` agent make targeted edits, renders once and records a
new version. When other steps of the workflow depend on the film — an analysis, an edit, a final
compile — the revision runs those again too, so it ends with the workflow's real deliverable. The design, its alternatives and the editor-v2 roadmap are in
[`design/composition-revisions.md`](design/composition-revisions.md); the user-facing page is
[Request changes to a finished video](user-guide/workflows/request-changes.md).

Other steps of a finished run (today the narration, line by line) are changed through [change requests](change-requests.md), which reuse the replay machinery below; the film itself stays here.

On the run page the film and the steps are one card, **Change this video**. The film alone goes through the revisions API below, with scene choice, cut policy and start version. The film together with any other change goes through the change-requests API as one target (instruction only, builds on the latest version), and the card switches those three choices off with a note. The API is unchanged: the change-requests API still answers `409 USE_COMPOSITION_REVISION` for the film alone, and the card never sends it. The run's history merges revisions and change requests by execution id.

## Where the code for composition revisions lives

| Layer | Type | Project |
|---|---|---|
| Entities | `CompositionWorkspace`, `CompositionWorkspaceVersion`, `CompositionRevision`, `WorkflowDefinitionKind` | `ReelBolt.Shared/Data/Models/CompositionWorkspace.cs` |
| Targets and keys | `CompositionRevisionTargets`, `CompositionWorkspaceKeys` | `ReelBolt.Shared/Compositions/` |
| Store (rows + archives) | `ICompositionWorkspaceStore` | `ReelBolt.WorkflowEngine/Services/Compositions/CompositionWorkspaceStore.cs` |
| Capture / restore | `ICompositionWorkspaceCapture`, `ICompositionWorkspaceRestorer`, `CompositionLineage` | `.../Compositions/CompositionWorkspaceTransfer.cs` |
| Brief | `CompositionRevisionBrief` (pure) | `.../Compositions/CompositionRevisionBrief.cs` |
| Runner | `ICompositionRevisionRunner` | `.../Compositions/CompositionRevisionRunner.cs` |
| Step | `ReviseStepExecutor` (`StepType.Revise`) | `ReelBolt.WorkflowEngine/Execution/StepExecutors/` |
| Agent | `CompositionReviserAgent` (`AgentType.CompositionReviser`) | `ReelBolt.WorkflowEngine/Agents/Production/` |
| Sandbox transfer | `ISandboxSession.ExportWorkspaceAsync` / `ImportWorkspaceAsync` | `ReelBolt.WorkflowEngine/Services/Compute/` |
| API | `CompositionRevisionsController`, `ICompositionRevisionService` | `ReelBolt.Inference.Api` |
| Sandbox-executor | `GET`/`PUT /workspace/archive` | `sandbox/pkg/container/archive.go` ([sandbox-service.md](sandbox-service.md)) |
| Dependency analysis | `WorkflowStepDependencyGraph`, `AgentInputContextDefaults` | `ReelBolt.Shared/Workflows/Replay/` |
| Replay plan | `CompositionReplayPlanner`, `CompositionReplayPlan`, `CompositionCutPolicy`, `WorkflowStepCloner` | `ReelBolt.Shared/Workflows/Replay/` |
| "Keep the same cut" | `CutCompatibility` | `ReelBolt.Shared/Workflows/Replay/CutCompatibility.cs` |
| Replay source | `CompositionReplaySources` | `ReelBolt.Inference.Api/Services/Compositions/` |
| Replay execution | `CompositionReplaySession` (+ hooks in `WorkflowExecutorService`) | `ReelBolt.WorkflowEngine/Execution/Replay/` |
| Step-cache upstream digest | `StepUpstreamDigest` (schema `v5`) | `ReelBolt.WorkflowEngine/Execution/Caching/` |
| Web | `components/revisions/*`, `lib/api/revisions.ts`, `lib/hooks/use-revisions.ts` | `web/` |

## Composition revision tables and migrations

The Inference API owns `composition_workspaces`, `composition_workspace_versions` and
`composition_revisions` (migration `AddCompositionWorkspaces`). The WorkflowEngine maps all three
with `ExcludeFromMigrations()` and writes versions and result links. The engine migration
`AddWorkflowDefinitionKind` adds `workflow_definitions.kind` (`0` = `Standard`, `1` =
`CompositionRevision`, `2` = `GraphicDesign`, `3` = `CompositionReplay` — an int column, so `3` needed no
migration). The API migration `AddCompositionRevisionReplay` adds `composition_revisions.cut_policy`
(`KeepCut`/`ReEdit` by name) and `replay_plan_json` (jsonb); the engine migration
`AddStepResultReusedFrom` adds `workflow_step_results.reused_from_step_result_id`. All three are nullable:
every earlier row has none of them. None of the three tables carries the tenant query filter: every read is
scoped by a project the caller was authorized for.

Archives live at `projects/{projectId}/agentFiles/workspaces/{workspaceId}/{versionId}.tar.gz` — a
gzip-compressed tar of the sandbox project without `node_modules`, `out/`, `build/`, `dist/` or
caches. `sha256` and `size_bytes` on the row are computed by the engine while it spools the archive.

## When a composition workspace is captured

`RenderVideoAndUploadToStorage` captures after every successful **video** render (a still is not
captured), after the render is recorded as the step's output. The capture is best effort: a failure
is logged and counted on `reelbolt.composition_workspace.captures{outcome="failed"}` and never fails
the render. Inside a revision the ambient `WorkflowExecutionContext.CompositionLineage` makes the new
version a child of the revision's base; elsewhere the capture continues the execution's own lineage
and skips an unchanged re-render (same archive digest). Set `CompositionWorkspaces:CaptureEnabled`
to `false` to turn capture off.

## How a revision runs

There are two shapes. When nothing in the workflow depends on the film (the film is the deliverable,
like `quick-win-promo`), a revision is the one-step run below. When something does, the same Revise
step runs inside a replay of the workflow — see
[Replaying the steps that depend on the film](#replaying-the-steps-that-depend-on-the-film).

1. `POST /api/v1/projects/{projectId}/executions/{executionId}/revisions` validates the request,
   resolves the run's lineage, rate-limits per user and project, runs the credit preflight, creates
   the project's hidden **"Video revisions"** workflow on first use (one `Revise` step,
   `CompositionReviser` agent), writes a `composition_revisions` row and queues an execution whose id
   the row already carries.
2. `ReviseStepExecutor` reads the row and calls `ICompositionRevisionRunner.RunAsync`.
3. The runner resolves the base version (or recovers version 1 from an older run's sandbox),
   restores it into the execution's fresh sandbox (an oversized video under `public/` that a workspace
   captured before staged-video copies existed still holds is replaced by the engine-made same-fps copy
   and recorded, like a fresh staging; best effort, see docs/sandbox-service.md "Oversized staged
   videos"), builds the brief (`CompositionRevisionBrief`: the
   owner's text fenced as data, targets as frame windows, the composition and scene list, restore
   warnings, retry guidance) and runs the agent under the revision's lineage.
4. In `Render` mode a revision always ends with a render or a clear failure. When the agent finishes
   without rendering, the runner compares the project's digest with the one taken before the agent
   ran. The digest covers the code under `src/` and the staged assets under `public/` (narration, footage,
   music, pictures; media by size and modification time), so a revision that only re-stages an asset, such
   as slower narration under the same file names, counts as a change. If it changed, the platform renders exactly once through the agent's own render tool, inside the
   same lineage scope, so the new version is captured as normal. If that render throws or produces no video,
   the step fails `REVISION_RENDER_FAILED` (`PlatformInternal`, refunded) with the render error in
   `ErrorDetails`: the tool's error plus, when Remotion could not read a video, the video(s) and their
   size (`... exit status 1. Remotion could not read a frame of public/ad-pour-warm-light.mp4 (2160x3840).`),
   else Remotion's last error line after a bare exit status (links removed, one line, at most 2,000
   characters). If nothing changed and nothing rendered, it fails `REVISION_NO_CHANGES` (`UserInput`,
   not refunded) asking for a more specific instruction. An agent that rendered itself gets no extra render.
   **Render repair turn** (production runs 22f79d02 and a2be36db): when the platform's render fails on
   something the agent can fix in its own code (`AgentFixableRenderFailure.Classify` over the render error
   text, Remotion's own last error line included after "Remotion said:"), the reviser gets ONE more turn in
   the same lineage scope and the same sandbox. Two kinds qualify: a **pre-render refusal** by one of the
   render tool's code checks (`HTML5_VIDEO_IN_RENDER`, `MISSING_STATIC_FILE`, `NETWORK_FONT`; the error text
   starts with the code), and a **code error** that crashed the bundle or the render (V8 `ReferenceError`/
   `TypeError`/`SyntaxError`/`RangeError` and their messages such as "is not defined", "is not a function",
   "Cannot read properties of"; esbuild/webpack "Module not found", "Could not resolve", "Build failed with";
   React render errors; Remotion's own validation such as "Could not find composition", `inputRange`/
   `outputRange`, "prop of the <Sequence>", "must be a positive integer"). Platform failures are checked
   first and always win, whatever else the text says: killed/`SIGKILL`/out of memory, a timeout or deadline,
   a video frame that could not be read (`Could not extract frame`, `offthread-video-server`, `/proxy?src=`,
   `delayRender`), `ENOSPC`, a browser that died, a sandbox that is gone, and the failed-render cap; so is an
   error with no recognisable code marker (a bare "exit status 1"). Its input
   (`CompositionRevisionBrief.BuildRenderRepair`) is first-party: the owner's request and "Your changes could
   not be rendered: <error>", each fenced as data, bounded to 2,000 characters, control characters dropped,
   then for a refusal "fix exactly that, keep every other change, render once", for a code error "find the
   code the error points at in src/ and fix it, check it with RenderPreviewStills on the scene that failed,
   then render once". If the agent renders, that render is the
   output; if it changed the project without rendering, the platform renders once more; anything else
   (nothing changed, the turn failed, the second render failed) ends `REVISION_RENDER_FAILED` with the
   error (or the second render's error). One repair turn per revision step and at most two platform
   renders, never a loop; a platform failure gets none. The turn is an ordinary
   agent run: its own `AgentRunTimeoutSeconds` budget, its model calls metered on the same step result and
   settled with the step's one hold, its tokens and tool calls added to the step result. A refused render
   records neither a full render nor a failed one, so the render-once guard and the failed-render cap do not
   block the repair render. The Author step needs no such turn (it calls the render tool itself and reads the
   refusal); its one `[FINAL RENDER MISSING]` reminder now names `HTML5_VIDEO_IN_RENDER`/`NETWORK_FONT` and
   code errors too, and quotes its last render call's error when the same classifier calls it agent-fixable
   (`AgentStepExecutor.LastFixableRenderRefusal`, the text `RenderErrorText` makes, so Remotion's line is in
   it); for a code error it says to fix the code and check the failing scene with RenderPreviewStills.
   `REVISION_NOT_RENDERED` is the legacy code of "changed but not rendered"; the runner no longer ends that
   way, but it stays classified (`PlatformProviderError`) for older rows. If the render tool's capture did
   not land, the runner captures itself. In `PreviewOnly` mode the runner records a
   `PreviewSnapshot` version without a render. The resulting version is linked to the request.

A `Revise` step is never cached and never retried by the outer loop; its failure codes
(`WORKSPACE_*`, `REVISION_*`) all refund, except `REVISION_NO_CHANGES`. It holds the Agent row of `StepCreditEstimation`. A replaying
revision's preflight (and its estimate) prices the `Revise` step, every `Run` step and every
`KeepIfCompatible` step (it may be re-made) — never a reused one; "Keep the same cut" is shown as a range
whose low end assumes the cut is kept.

## Replaying the steps that depend on the film

A revision of a film that feeds a larger workflow (animated film → analyze → edit room → compile →
review, for example) must end with that workflow's deliverable, not only the re-rendered animation.

1. **The seed run.** `CompositionReplaySources` takes the run that produced the BASE version (the one
   being changed); a pre-replay one-step revision run holds only a render, so it steps back through its
   `composition_revisions.source_execution_id` to the run that holds the whole pipeline. The film step is
   the seed run's result whose `output_storage_key` is the base version's render (or an ancestor's).
2. **The plan.** `WorkflowStepDependencyGraph` reads what each step reads — prompt context mode, previous
   step, `VideoSourceRef`/`ExtractInputRef`/`AnalysisStepOrder` (and the video that analysis read), the
   compile's decision/plan/voiceover references, voiceover source steps, the sandbox — and
   `CompositionReplayPlanner` assigns every step one disposition: `Reuse` (before the film, or not
   depending on it — copied, never re-run), `Revise` (the film step), `Run`, `KeepIfCompatible` (an
   editorial decision under "Keep the same cut") or `Skip` (did not run in the seed run; or a review whose
   only re-made input is the film itself). Branches are always re-evaluated. An unreadable config or a
   workflow with branches falls back to "depends on everything before".
3. **The run.** The API saves a per-revision snapshot of the seed run's workflow
   (`WorkflowDefinitionKind.CompositionReplay`, hidden, "Changes to {workflow}") with the film step
   replaced by a `Revise` step, writes the plan to the request row and queues an execution that carries
   the seed run's ORIGINAL brief (`user_request`) and style/format — the replayed steps read those exactly
   as before; the owner's instruction is on the request row, which is all the `Revise` step reads.
4. **The engine.** `CompositionReplaySession` copies a reused step's result into the run
   (`reused_from_step_result_id`; history entry, accumulated output and storage keys as if it had run; no
   credits, no cache, published like any finished step), records a skip, and lets every other step down
   the ordinary path (plan re-check, step cache, credit hold/settle/refund, retries, progress, stop).
5. **Keeping the cut.** A `KeepIfCompatible` step is decided when it is reached: it is copied when every
   step it reads is unchanged — reused, kept, a re-run `VideoAnalyze` whose offered ids are the same set
   from the same sources within max(0.25 s, 10 % of the item) (`CutCompatibility`), or any other re-run
   step with byte-equal JSON — and otherwise re-made. The film itself counts as unchanged only through
   its re-checked analysis.
6. **Review loops.** A replayed review runs once for the record. It loops back only when its window
   holds a model step this run re-made, and never into or before the `Revise` step (it is redirected to
   the first re-made step). Under "Keep the same cut" with the cut kept, the review never loops: looping
   would either re-run deterministic steps to the same result or override the owner's choice.

The step cache serves a replayed deterministic step whose inputs did not change, because schema `v5`
hashes what a step reads by step order (`StepUpstreamDigest`) — without it a compile behind a kept
decision would have been served the original cut. Failure codes: `REPLAY_PLAN_MISSING` and
`REPLAY_SOURCE_MISSING` (both `PlatformInternal`, refunded).

## Revisions from the assistant and the MCP

`ProposeReviseComposition` and `ApplyReviseComposition` (`AssistantRevisionTools`) start a revision from chat or from an MCP client such as Claude Code, through the same `ICompositionRevisionService.RequestAsync` the REST route uses.

- **Propose** needs View on the run's project, reads the run's revision history and refuses with the service's own code when `canRevise` is false (`RUN_NOT_FINISHED`, `WORKSPACE_UNAVAILABLE`). It returns `operation`, `confirmationToken`, `summary`, `availableScenes` (name, `fromFrame`, `durationInFrames`, description), `unknownScenes`, `estimate` (`billingEnabled`, `low`, `high`, `required`, `balance`, `affordable`), `cutPolicy` and, for a film with dependent steps, `replay`. The token binds the project, run, instruction, targets, `fromVersionId` and `cutPolicy`; changing any of them makes Apply refuse. Limits match the REST request: instruction up to 4000 characters, up to 50 scenes, up to 20 frame ranges.
- **Apply** needs Edit and a verified email. It takes a slot in the **paid** bucket of the run rate limit (a revision always counts as paid) and then calls `RequestAsync` with initiator `Assistant` (or `Mcp` when the call came in over MCP), so the new run is attributed correctly. It returns `{revisionId, executionId, status, rateLimit}`; `executionId` is the new run to follow with `GetExecutionStatus`. Refusals come back as `{error, code, ...}` with the service's fields intact: `INSUFFICIENT_CREDITS` (`required`, `balance`), `PLAN_NOT_ENTITLED`, `REVISION_RATE_LIMITED` (`used`, `limit`, `windowMinutes`, `retryAfterSeconds`) and `RUN_RATE_LIMITED`. A refusal queues nothing.

The two limits are independent: `REVISION_RATE_LIMITED` (`CompositionRevisions:MaxPerHourPerUserProject`, below) counts revisions only, and the run rate limit counts the revision as one paid run too. See [Assistant execution rate limiting](platform-assistant-mcp.md#assistant-execution-rate-limiting).

## Continuing an earlier composition from a new run

**Which version a request builds on.** On the revisions route, with no `fromVersionId`, a revision builds on the newest version of the video (the workspace head), whichever run of the lineage it is sent from, and its replay plan is read from the run that created that version, so reused results and the video agree. Basing it on the run's own older version would silently drop every change made since. An explicit `fromVersionId` means exactly that version. A **combined change request** (several targets in one request, the film among them) builds on the same newest version (`ResolveFilmContextAsync`: `WorkspaceHeadAsync ?? CurrentVersion`; before production run 22f79d02 it used the run's own version and so dropped a later revision's fix), while every other target's items still come from the run the request is opened from; its estimate, `201` and approval card say which version (`filmBase`, by number). The `201` response carries `baseVersionId` and `baseVersionNumber`; the history carries `headVersionId` and `headVersionNumber`, and the run page says "Based on version N, the latest." when the page's run is not the head.

Separate from a revision, an **Agent** step can start a new run from an earlier run's composition with the step option `continueCompositionJson` (`{"executionId":"<guid>"}` for that run's latest captured version, or `{"versionId":"<guid>"}` for a specific one; exactly one of the two). Before the agent runs, the engine restores that workspace version into the new run's sandbox and prepends `[CONTINUING COMPOSITION] The sandbox already contains the composition from an earlier run. Reuse its components and styles; edit rather than rewrite; render once.` to the step input. The new run's later captures form its own lineage (revisions on the new run work); the source run's lineage is never modified.

- Allowed only on an Agent step whose agent holds `SandboxAuthoring`; otherwise the save is refused with `CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT`.
- The source must belong to the same project. A foreign or missing execution or version is refused on save with `CONTINUE_COMPOSITION_NOT_FOUND` (the same wording for both) on every save path, and is checked again at run time before anything is downloaded: the run fails `COMPOSITION_SOURCE_NOT_FOUND` (UserInput, nothing charged).
- A restore that fails fails the run `COMPOSITION_RESTORE_FAILED` (platform internal).
- The option round-trips through `GetWorkflow` and `ProposeWorkflow` and is exported with the account data.

Resume of a revision run (or of a change-request run) answers `409 REVISION_RESUME_NOT_SUPPORTED`; the recovery sweeper never
auto-resumes it and `canResume` is false. Ask for the revision again.

## Composition revision API

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/projects/{projectId}/executions/{executionId}/revisions` | Project view | The lineage of a run (original or revision): `availability` (`Snapshot`/`Recoverable`/`Unavailable`), `canRevise`, `currentVersionId`, `scenes`, `versions` (with `renderStepResultId` — the animation — and `deliverables` — the run's final video(s), primary last — for `GET .../outputs/{id}/download`, plus `runStatus`, `finalVideoExpected`, `cutPolicy`, `cutKept`), `revisions` (with `cutPolicy`), `estimate`, and `replay` (`available`, `reason`, `hasEditDecisions`, the steps that run again with their disposition under each cut choice, `keepCutEstimate`/`reEditEstimate`) |
| `POST` | `/api/v1/projects/{projectId}/executions/{executionId}/revisions` | Project edit, verified email | `{ instruction, targets?, fromVersionId?, mode?, cutPolicy? }` (`cutPolicy`: `KeepCut` default, `ReEdit`) → `201` with the queued revision (`executionId`, `workflowId`, `cutPolicy`). `400` validation, `402 INSUFFICIENT_CREDITS`, `404`, `409 RUN_NOT_FINISHED` / `WORKSPACE_UNAVAILABLE` / `RUN_CLOSED` (the run first started at or before the project's content marker; the history reports `canRevise: false` for it), `429 REVISION_RATE_LIMITED` (+ `retryAfterSeconds`, `Retry-After`), `503 REVISION_AGENT_UNAVAILABLE` |

`targets` is `{ scenes?: string[], frameRanges?: [{fromFrame, toFrame, compositionId?}], region?: {fromSeconds, toSeconds} }`.

## Composition revision configuration

| Key | Default | Service | Meaning |
|---|---|---|---|
| `CompositionWorkspaces:CaptureEnabled` | `true` | Engine | Snapshot after each video render |
| `CompositionWorkspaces:ScratchPath` | `VideoEditing:ScratchPath`/workspaces | Engine | Where archives are spooled |
| `CompositionWorkspaces:MaxArchiveBytes` | 1 GiB | Engine | Largest archive spooled or restored |
| `CompositionWorkspaces:TransferTimeoutSeconds` | `600` | Engine | Deadline of one archive transfer |
| `CompositionRevisions:MaxPerHourPerUserProject` | `10` | API | Rolling-hour request limit |
| `CompositionRevisions:SandboxRecoveryWindowDays` | `7` | API | How long a pre-capture run is offered as recoverable (mirror `SANDBOX_TTL`) |

The sandbox-executor's own caps (`SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES`, `SANDBOX_WORKSPACE_MAX_*`,
`SANDBOX_TEMPLATE_DIR`) are in [sandbox-service.md](sandbox-service.md).

## Revising runs made before capture existed

A run with an agent-made film but no version is **Recoverable** for
`SandboxRecoveryWindowDays` after it finished: the engine exports the old run's sandbox (stopped
sandboxes keep their workspace until `SANDBOX_TTL`) as version 1 (`SandboxRecovery`) before revising.
A sandbox-executor restart purges every sandbox, so recovery can fail earlier; the revision then
fails with `WORKSPACE_UNAVAILABLE` and is refunded.

## Graphic lineages

Designed graphics for the picture editor ([`editor.md`](editor.md#designed-graphics)) store their source in the same
tables: a version with `source = GraphicDesign` whose `render_output_storage_key` is the graphic's PNG and whose
`instruction` is the request that produced it. A run whose lineage holds such a version answers `Unavailable` on
`GET …/revisions` ("change it from the picture editor") and cannot be revised as a film.

## Composition revisions on the compute fabric

`ExportWorkspaceAsync`/`ImportWorkspaceAsync` are implemented for the local sandbox-executor only. A
`RemoteSandboxSession` that bound locally delegates; one bound to a runner throws
`SandboxOperationNotSupportedException`, so captures there are counted `unsupported` and a revision
fails with `WORKSPACE_TRANSFER_NOT_SUPPORTED`. The runner ops are a documented follow-up.
