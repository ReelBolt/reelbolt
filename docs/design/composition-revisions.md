# Composition revisions: a revision library for Remotion films

Status: shipped (first front-end: "Request changes" on a finished run). Owner request: "turn this
into a proper library that can be extended in the future. Today we do it from a single run;
tomorrow it's the user clicking on the v2 editor and asking for changes in real time."

Developer reference: [`../composition-revisions.md`](../composition-revisions.md). User guide:
[`../user-guide/workflows/request-changes.md`](../user-guide/workflows/request-changes.md).

## The problem composition revisions solve

A Remotion workflow (for example `quick-win-promo`: analyzers → translator → animation strategy →
scriptwriter → director → author → review) ends with the Author agent writing a Remotion project in
a per-execution sandbox and rendering it with `RenderVideoAndUploadToStorage`. Before this work only
the MP4 left the sandbox. The project itself lived in that one sandbox (stopped at the end of the
run, deleted after `SANDBOX_TTL`, wiped by any sandbox-executor restart), and a new execution got a
fresh, empty sandbox. An owner who wanted "better graphics in the intro and the stats section" had to
pay for the whole pipeline again and got a different film back.

## Decisions at a glance

| Question | Decision | Why |
|---|---|---|
| What is the source of truth for a film? | A **composition workspace**: an append-only chain of **versions**, each a tar.gz of the sandbox project in object storage plus a row | A render is an output; the project is what can be changed. Versions make every change reversible and comparable |
| Who writes the rows? | The **engine** captures; the **Inference API** owns the schema and creates requests | Capture happens where the sandbox is; the API already writes engine-owned `workflow_executions`, so the reverse write has precedent |
| How does a revision run? | As its **own execution**: of a hidden per-project system workflow with one `StepType.Revise` step when nothing depends on the film; otherwise of a per-revision replay snapshot (below) | Credits, leases, cancellation, SSE progress, history and refunds all come for free |
| New step type or Agent step? | **New `StepType.Revise`** | The restore must happen before the model runs (never left to the model), and success requires a render + a recorded version — pre/post-conditions the generic agent executor cannot express |
| New agent or Author with a new prompt? | **New built-in `AgentType.CompositionReviser`** | The Author's contract is "assemble the whole film" (RenderManifestOutput, Author-only output checks, whole-file writes) — exactly the rewrite a revision must not do |
| What else runs after the re-render? | **Every step of the original workflow that depends on the film, and only those** (dependency analysis over the step configs, "when in doubt it depends") | The owner ships the workflow's deliverable (the final compile), not the animation; re-running steps that do not read the film would only cost time and credits |
| Re-cut the film? | **No, by default ("Keep the same cut")**: an editorial decision is reused when the re-analysed film offers the same ids at the same places; "Re-edit the video too" re-makes it | Re-running the edit room re-cuts a film the owner already approved; an id-anchored decision is exactly as valid as its ids |
| Execution model of a replay | **A per-revision snapshot of the source workflow** (`CompositionReplay`, hidden), the film step replaced by `Revise`, upstream steps copied in | Every step-order reference resolves unchanged, credits/cache/leases/SSE/stop are the ordinary path, and the record survives later edits of the user's workflow (an edit cascade-deletes that workflow's step results) |
| Archive format | **tar.gz** | In Go's stdlib (`archive/tar`, `compress/gzip`) and .NET's BCL (`System.Formats.Tar`, `GZipStream`); no new dependency; zstd would need one on both sides |
| Archive key | `projects/{p}/agentFiles/workspaces/{workspaceId}/{versionId}.tar.gz` | Under the project's prefix (counted by storage metering, removed by account purge); `agentFiles` so it is never mistaken for a playable render; keyed by version **id** so concurrent appends can never overwrite each other |
| Bytes path | Streamed (`GET`/`PUT /workspace/archive`), never base64 | The `/files/raw` rule: a project with assets is tens to hundreds of MB |

## The library's layers

```
            front-ends (thin)                               reusable core
 ┌─────────────────────────────────────┐   ┌────────────────────────────────────────────┐
 │ 1. Execution page "Request changes" │   │ ICompositionRevisionRunner  (layer 3)      │
 │    POST .../revisions → execution   │──▶│   restore → brief → agent → verify → record│
 │    ReviseStepExecutor (adapter)     │   │   CompositionRevisionBrief (pure)          │
 ├─────────────────────────────────────┤   ├────────────────────────────────────────────┤
 │ 2. (future) editor-v2 live mode     │──▶│ ICompositionWorkspaceCapture / Restorer    │
 │    warm sandbox, preview-only,      │   │   (layer 2: sandbox transfer)              │
 │    streamed progress                │   ├────────────────────────────────────────────┤
 └─────────────────────────────────────┘   │ ICompositionWorkspaceStore (layer 1)       │
                                           │   rows + archives, numbering, digests      │
                                           ├────────────────────────────────────────────┤
                                           │ ISandboxSession.Export/ImportWorkspaceAsync│
                                           │   (transport: local executor; runner TBD)  │
                                           └────────────────────────────────────────────┘
```

- **Layer 1 — store** (`ICompositionWorkspaceStore`, engine): creates a lineage's workspace, appends
  versions (object first, then row; the number is arbitrated by a unique index and retried),
  downloads an archive to scratch and verifies it against the recorded SHA-256, reads/links revision
  requests. Knows nothing about sandboxes or agents.
- **Layer 2 — transfer** (`ICompositionWorkspaceCapture`, `ICompositionWorkspaceRestorer`, engine):
  capture spools the sandbox's archive to scratch while hashing it (the digest is the engine's,
  never the sandbox's), dedupes an unchanged origin re-render, appends under the right lineage and
  advances it. Restore creates a fresh sandbox (its container copies the image's template with
  `node_modules`), streams the archive in, and installs packages only when the archive declares
  dependencies the template lacks — turning an impossible install (no egress) into a warning for the
  agent rather than a failure.
- **Layer 3 — runner** (`ICompositionRevisionRunner`, engine): takes a front-end-neutral
  `CompositionRevisionRequest` {workspace, base version or a run to recover, instruction, targets,
  mode, sandbox key}, reports stages to an `ICompositionRevisionObserver`, and returns a
  `CompositionRevisionOutcome` (never throws for an expected failure; every failure has a code).
- **Front-end 1 — execution-backed**: `POST /api/v1/projects/{p}/executions/{e}/revisions` writes a
  `composition_revisions` row and queues an execution of the project's hidden workflow;
  `ReviseStepExecutor` reads the row and drives the runner; the web execution page shows the panel and
  the version chain.

## Replaying what depends on the film

The first shipped version re-rendered only the animation. On staging that was workflow e410f6ae
("Animated film + edit room cut": translator → animation strategy → director → AUTHOR → analyze (the
film plus five photos) → edit room → compile → review): the revision produced `revision.mp4` and
version 2, but analyze, edit room, compile and review never ran again, so the cut the owner ships was
unchanged. A revision now ends with the workflow's real deliverable, for any shape.

### Which steps run again: a dependency analysis

`WorkflowStepDependencyGraph` (Shared, pure) derives, from the step definitions alone, what each step
READS from earlier steps: its prompt context (`FullWorkflow`/`CustomMappedSubset` = everything before,
`PreviousStepOnly` = the previous step, `SelectedPriorSteps` = the list), the accumulated output
(`ForEach`, `Conditional`, a review's facts), typed references (`VideoSourceRef` `StepOutput n` and
`PreviousStepOutput` = every earlier step; `ExtractInputRef` `Previous`/`Step n`; `AnalysisStepOrder`
plus the video that analysis read, because a compile cuts those files; the compile's decision, graphics,
music, colour, SFX and generated-clip references and its voiceover steps; a voiceover's script/narration/
pickup step, or everything when pronunciation is `Auto`; a video generation's plan and analysis), and the
sandbox (an agent with any sandbox tool reads what every earlier sandbox-writing step left). The rule is
"when in doubt, it depends": an unreadable config, a custom mapping or a workflow with branches (a
branch can skip the previous step) falls back to "depends on everything before". Over-inclusion costs a
re-run; under-inclusion ships a stale deliverable.

The steps transitively downstream of the film step run again; every other step is reused. Two
exceptions: a branch is always re-evaluated, and a review whose only re-made input is the film itself
(the quick-win promo's code review after its Author) is skipped — it reviews the composition the owner
just asked to change, and a review can never loop back into a revision. A plan with nothing left to run
is not a replay at all: the revision stays the one-step run it always was.

### Keep the same cut

Re-running an editorial decision (the edit room, the story editor, the motion-graphics/music/SFX/
colour/shot/narration planners) would re-cut a film the owner approved. Decisions name only the ids the
analysis offered (`s3`, `g1`, `t7`, `p2` …), never a time, so a decision is reused — "Keep the same
cut", the default — when everything it was decided from is unchanged: steps reused or kept, any
re-run step with byte-equal output, and the film itself through its re-run analysis, which must offer
the SAME set of ids from the same sources with every boundary within max(0.25 s, 10 % of the item)
(`CutCompatibility`). Deliberately stricter than "the ids the decision names still exist": a scene added
to the film leaves the old ids resolvable while the kept cut silently drops the new scene. When anything
differs the decision is re-made, and every decision downstream of it is re-checked against it. The owner
can instead choose "Re-edit the video too", which re-makes every dependent decision. The compile always
runs: it resolves the kept ids against the NEW analysis artifact, so the new frames are cut at the new
boundaries. On the staging run the revision kept every scene's timing ("keep the timing" was part of the
request), so the kept cut applies as is.

### Review loops in a replay

A replayed review runs once, so the new deliverable has a score on record. It loops back only when its
window contains a model step this replay re-made (a re-made edit can respond to the critique), and it
never loops into or before the `Revise` step — redoing the owner's change with a critic's notes instead
of the owner's words is not what was asked; a loop that would is redirected to the first re-made step.
With the cut kept, the review therefore never loops: looping would re-run deterministic steps to the same
result, or override a choice the owner made.

### Execution model

Considered:

| Option | Verdict |
|---|---|
| Run the revision against the ORIGINAL workflow, with the film step dispatched to the Revise executor | Rejected: editing a workflow replaces its step rows and cascade-deletes every result of every run of it, so a revision's record (and the seed of a revision of a revision) would vanish with the next edit; step-type checks would see an `Agent` step running Revise code; and the original workflow's run list would mix runs and revisions |
| The shared hidden one-step workflow, with the replayed steps materialised per execution | Rejected: steps are rows of a workflow definition; a shared definition cannot hold one revision's copy of e410f6ae and another revision's copy of a different workflow at once |
| **A per-revision snapshot workflow** (`WorkflowDefinitionKind.CompositionReplay`) | **Chosen**: the source run's steps cloned at their own orders (`WorkflowStepCloner`, reflection over every scalar column so a new config column is never forgotten), the film step replaced by a `Revise` step with the built-in reviser. Every step-order reference resolves as it did; the executor runs it like any workflow; the revision run is a complete, self-contained record (copied rows for reused steps), so revising version N seeds from N's own run. Cost: one hidden workflow (a handful of step rows) per replaying revision |

The plan (`CompositionReplayPlan`, jsonb on the request row) is built by the Inference API when the
request is accepted, so the preflight and the panel price exactly what the engine will run, and is
executed by the engine's `CompositionReplaySession`, which never runs anything itself: a reused step is
a copied row (no credits, no cache, published like a finished step), a skip is a skipped row, everything
else goes down the ordinary executor path — plan re-check, step cache, per-step credit hold/settle/
refund by `FailureClass`, retries, progress/SSE, stop, leases. The replay execution carries the seed
run's ORIGINAL brief and style (the edit room, the compile's length target and the review read the
brief); the owner's instruction is on the request row, which is all the `Revise` step reads.

**Which run seeds the replay.** The run that produced the base VERSION — not the run the owner clicked,
since "Start from" can pick any version. A one-step revision made before replays existed holds only its
render, so the seed steps back through its request row to the run it revised; the film step is that
run's result whose render is the base version's (or an ancestor's). A seed run whose workflow was edited
since (its results are gone) cannot be replayed; the revision then re-renders the animation only and the
panel says why.

**The step cache.** A replayed deterministic step whose inputs did not change is a free hit — but only
because the key now covers what a step reads by step order (`StepUpstreamDigest`, schema `v5`). Before
it, a compile's key held only its previous step's output: behind a KEPT decision, the replay's compile
hashed exactly like the original's and would have been served the original cut — the bug this work
fixes, reintroduced by the cache.

**Credits.** The preflight (402) and the estimate cover the `Revise` step, every `Run` step and every
`KeepIfCompatible` step (it may be re-made, and the B7 promise is that the preflight never asks for less
than the engine may hold); "Keep the same cut" is shown as a range whose low end assumes the cut is
kept. Reused steps hold nothing.

**Outputs.** A version links to its run's FINAL video(s): the plan's `FinalVideoStepOrders` (every
compile no later step re-analyses; the film when there is none) resolved to that run's latest render of
each, primary last; the animation stays reachable as the secondary video. In the Renders tab a replay's
renders are takes of the ORIGINAL workflow's steps (the revision's cut is the newest take of the
compile, its film the newest take of the film step), and copied rows are never takes of their own.

## Data model

Inference API owns all three tables (migration `AddCompositionWorkspaces`); the engine maps them with
`ExcludeFromMigrations()` and writes versions/links. No FK to `workflow_executions` (engine-owned, and
absent when the API migrates first). No tenant query filter: they hang off a project, and every read
path resolves the project through `IProjectAccessService` first (pinned in `TenantBoundaryInventoryTests`
as child tables, the `ProjectVoice` rule).

- `composition_workspaces` — one per lineage: `project_id` (FK, cascade), `organization_id`
  (denormalized, never a guard), `origin_execution_id` (unique), `origin_workflow_definition_id`,
  `latest_version_number`.
- `composition_workspace_versions` — append-only: `workspace_id` (FK, cascade), `project_id`,
  `version_number` (unique per workspace), `parent_version_id`, `storage_key`, `sha256`, `size_bytes`,
  `file_count`, `source` (`RenderCapture` / `SandboxRecovery` / `PreviewSnapshot`), `composition_id`,
  `fps`, `width`, `height`, `duration_in_frames`, `scene_manifest_json` (the render's
  `CompositionSceneManifest`), `render_output_storage_key`, `created_by_execution_id`,
  `created_by_user_id`, `revision_id`, `instruction`.
- `composition_revisions` — one per request: `workspace_id`, `source_execution_id` (the run the owner
  clicked), `base_version_id` (null ⇒ recover), `execution_id` (unique — the revision's own run),
  `workflow_definition_id` (the hidden workflow), `instruction`, `targets_json`, `mode`,
  `requested_by_user_id`, `result_version_id`, `cut_policy` and `replay_plan_json` (both null when nothing
  depends on the film; migration `AddCompositionRevisionReplay`). Status is read from the execution, never copied.

Engine migration `AddWorkflowDefinitionKind` adds `workflow_definitions.kind` (`Standard` = 0,
`CompositionRevision` = 1). The hidden workflow is excluded from every listing, edit, delete and
ordinary execute path (REST, assistant, MCP); its executions stay visible, so the execution page,
SSE, stop and retry work unchanged.

`CompositionRevisionTargets` (Shared) is the target type every front-end shares: `scenes` (names from
the manifest — today's chips), `frameRanges` (composition frames), `region` (seconds — the shape of a
timeline selection in the editor). The brief resolves all three into frame windows.

## Capture and lineage

`RenderVideoAndUploadToStorage` captures after every successful **video** render, after the render
is recorded as the step's output and after its scene sidecar is written, through
`TryCaptureAsync` — logged and counted (`reelbolt.composition_workspace.captures{outcome,source}`),
never thrown. The lineage is ambient: `WorkflowExecutionContext.CompositionLineage`, set by the
runner inside a revision (workspace + parent + instruction + revision id), null everywhere else (the
execution's own lineage, created on first capture). Each capture advances the lineage's parent, so a
second render inside one revision chains onto the first. Outside a revision an unchanged re-render is
deduplicated by digest (the sandbox's archive is deterministic: sorted entries, zero mtimes, zero
ids); a revision always records its own version because it carries an instruction and a new render.
`CompositionWorkspaces:CaptureEnabled=false` turns capture off.

## Archive handling and security

The sandbox's threat model is unchanged: code inside it is hostile, the control plane holds the
Docker socket, and archives are exactly the kind of input that has historically broken that boundary
(traversal, symlink and hard-link tricks, device nodes, zip bombs). The sandbox-executor
(`sandbox/pkg/container/archive.go`) therefore:

- walks and writes **only through `os.Root`**, opening every path `O_NONBLOCK`, `fstat`-ing the
  handle, refusing anything but a regular file (or a directory where one is expected) and checking it
  is the same object the pre-pass `Lstat` saw — so a FIFO, device or a swap between stat and open
  cannot block the service or redirect it;
- **exports** regular files and directories only (links, devices, FIFOs, sockets are skipped and
  counted), never `node_modules` or `out/`, `build/`, `dist/`, `.cache/`, `.remotion/`, `.git/`,
  `.npm/`, `tmp/`, with a pre-pass that enforces entry/size caps before a byte is streamed, and an
  aborted response (never a valid-looking short archive) if something fails mid-stream;
- **imports** only regular files and directories with clean, relative names (no `..`, no absolute
  paths, no backslashes, no excluded names) — links, hard links, devices and FIFOs are refused — under
  entry, per-file, total-uncompressed (counted on bytes actually written) and compressed caps;
- pins the sandbox (`inFlight`) without restarting its container, so the janitor neither stops nor
  deletes it during a transfer.

On the engine side the store refuses an archive key outside the project's prefix, verifies the
SHA-256 it recorded before a restore, and caps what it spools (`CompositionWorkspaces:MaxArchiveBytes`).
The owner's instruction reaches a code-executing agent; the brief quotes it as data in a fenced block
whose fences it cannot close, the prompt says it never changes the rules, and — as for the Author —
the sandbox's containment (read-only rootfs, no egress, no socket) is the actual bound.

## Restoring runs made before capture

A run finished before this shipped has no version. Its sandbox may still exist: `/complete` only
stops the container, the janitor deletes it after `SANDBOX_TTL` (7 days), and a sandbox-executor
restart purges every sandbox. The API marks such a run **Recoverable** when it has an agent-made
film render and finished within `CompositionRevisions:SandboxRecoveryWindowDays` (7). The request row
then carries no base version, and the engine — the only service that can reach the sandbox — exports
the old run's workspace as version 1 (`SandboxRecovery`, linked to that run's render and scene
sidecar) before revising it. If the sandbox is gone the revision fails with `WORKSPACE_UNAVAILABLE`
(refunded) and a plain explanation.

## Failure codes and billing

Every code is in `FailureClassifier`: `WORKSPACE_ARCHIVE_TOO_LARGE`, `WORKSPACE_ARCHIVE_CORRUPT`,
`WORKSPACE_ARCHIVE_MISSING`, `WORKSPACE_UNAVAILABLE`, `WORKSPACE_RESTORE_FAILED`,
`WORKSPACE_TRANSFER_NOT_SUPPORTED`, `REVISION_REQUEST_MISSING`, `REVISION_AGENT_UNAVAILABLE` →
`REVISION_RENDER_FAILED` → `PlatformInternal`; `REVISION_AGENT_FAILED` and the legacy
`REVISION_NOT_RENDERED` → `PlatformProviderError`; `REVISION_NO_CHANGES` → `UserInput`. All
refund except `REVISION_NO_CHANGES`. A `Revise` step is never cached (`StepCachePolicy`) and never retried by the outer loop; it
holds the Agent row of `StepCreditEstimation` until the organization has its own history, and the
`POST` runs the same B7 preflight (402) as every other execute path.

## The Fabric path

`ISandboxSession` gained `ExportWorkspaceAsync`/`ImportWorkspaceAsync` as default members that throw
the typed `SandboxOperationNotSupportedException`. `LocalSandboxSession` implements them;
`RemoteSandboxSession` delegates when it bound locally and otherwise refuses — a revision on a
runner-backed session fails with `WORKSPACE_TRANSFER_NOT_SUPPORTED`, and a capture there is counted as
`unsupported`. The intended runner design mirrors D8: two ops on presigned URLs — `exportWorkspace`
(the runner tars its workspace with the same exclusions and determinism and PUTs it into
`runner-staging/{jobId}/{n}`, D12 verifies size and digest, and the store promotes it to the archive
key) and `importWorkspace` (a presigned GET the runner untars with the same validation rules). The
seam's stream-shaped signatures already fit that: a remote implementation publishes then streams back
exactly as `OpenRawFileAsync` does.

## Roadmap: editor-v2 live revisions

The intended second front-end: the owner selects a region on the editor's timeline and asks for a
change while watching.

- **Warm sandbox**: the editor opens a session keyed by an editor-session id, restores the current
  version once, and keeps it warm; each request runs the same runner with
  `SandboxAlreadyRestored = true`.
- **Preview-only by default**: `CompositionRevisionMode.PreviewOnly` already exists end to end — the
  agent edits and checks with `RenderPreviewStills` but does not render, and the runner records a
  `PreviewSnapshot` version. A "Render this" action then runs a `Render` revision from that version.
- **Streaming progress**: `ICompositionRevisionObserver` is the seam; the live front-end forwards
  stages (and, later, preview stills) over SSE instead of step progress events.
- **Targets**: the timeline selection is a `CompositionTimeRegion`; a selected element can become a
  `CompositionFrameRange` plus a scene name.
- **What has to be added**: an endpoint that runs the runner outside an execution (with its own
  credit hold via `CreditGate`, since there is no step), a session registry for warm sandboxes, and a
  preview-still transport (presigned GETs of the stills the agent rendered).

## Platform assistant

Not added for films. A `ProposeRequestChanges`/`ApplyRequestChanges` pair fits the assistant's
propose-then-apply-with-token pattern, but the gated tools' per-call authority rules need their own
review; it is a follow-up. (The assistant CAN start a picture graphic or "Change this graphic" through
`ProposeDesignGraphic`/`ApplyDesignGraphic`, which reuse this library's store and restore.)

## Second front-end: designed graphics for the picture editor

The picture editor's "Design a graphic" reuses layers 1 and 2 of this library rather than copying them
([`../editor.md`](../editor.md) "Designed graphics"): a designed graphic's source is captured as a version with
`CompositionVersionSource.GraphicDesign` (the PNG as its render), and "Change this graphic" restores that version into a
fresh sandbox before the `GraphicDesigner` agent edits it. Its runner (`GraphicDesignRunner`) is a sibling of the revision
runner, not a mode of it, because its deliverable is different: the platform renders a transparent still itself and
verifies and crops it. A lineage holding a `GraphicDesign` version is refused by the film revision front-end
(`Unavailable`: "change it from the picture editor"), so a graphic is never re-rendered as a film.

## What this does not do yet

- A replay inherits two existing behaviours unchanged: a downstream step that renders (a
  motion-graphics planner's overlay asset) captures into the replay run's own lineage, as it would in
  any run; and a source run deleted between the request and the run fails the replay with
  `REPLAY_SOURCE_MISSING` (refunded).
- "Re-make the finished video" without a change (for a version made by a one-step revision before
  replays existed) needs an instruction today.

- The runner (Fabric) transfer ops (see above).
- Diffing two versions, or restoring an older version without revising it.
- Retention: archives live until the project is deleted; nothing prunes old versions.
- The editor-v2 live front-end.
