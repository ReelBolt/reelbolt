# Change requests on any step of a finished run

Status: slice 1 built, plus several targets in one request (see "Several targets") (shipped behaviour in [`../change-requests.md`](../change-requests.md)). First slice: the generic plumbing plus `Voiceover` line
re-synthesis. Next slices: `VideoGenerate` re-take, then `AudioGenerate`, `ThumbnailGenerate`, agent
(script) steps and plain retry. Builds on [`composition-revisions.md`](composition-revisions.md)
("Replaying what depends on the film"); it does not replace it.

Motivation: almost no run succeeds in every step on the first try. Change requests exist
only for the Remotion render. The owner must be able to ask for changes to the narration, to an
AI-generated clip, to the script and the rest, keeping everything else exactly as it was, spending as
few credits as possible and redoing only what did not come out right. It must be general: a future
step type gets change requests by declaring one capability. It must work from the run page, the chat
assistant and MCP.

## Decisions at a glance

| Question | Decision | Why |
|---|---|---|
| What is a change request? | A **row** (`step_change_requests`) naming a finished run, one **target step**, an optional **item scope**, an **instruction** and typed **overrides**. Its work is its **own hidden run** | Credits, leases, cancel, SSE, history and refunds come for free; same shape as a composition revision |
| How is the rest of the run kept? | The existing **replay machinery**: `WorkflowStepDependencyGraph` + `CompositionReplayPlanner` give each step `Reuse` / `Revise` / `Run` / `KeepIfCompatible` / `Skip`; the per-request snapshot workflow is `WorkflowDefinitionKind.CompositionReplay` | Already priced, already tested, already hidden from every list |
| What replaces the target step in the snapshot? | **The same step**, cloned, with `CacheMode = Never`. Its executor reads the request (by execution id) and applies it | A new step type per target would duplicate every executor. The Remotion film step keeps its `Revise` step; it is just one more capability entry |
| How does an executor learn about the request? | `IStepChangeRequestContext.FindAsync(executionId, stepOrder)`, the way `ReviseStepExecutor` reads `CompositionRevision` today. **No column on `workflow_steps`** | A saved user workflow can never carry a request; no 15-site column mirror |
| Where does "which step types support it" live? | One pure registry, `StepChangeCapabilities` (Shared): per `StepType` either `Supported(items, overrides)` or `NotSupported(reason)`. A **drift guard** fails the build when a `StepType` has no entry, when a `Supported` type has no executor that implements `IChangeRequestAwareStepExecutor`, or when a `NotSupported` entry has an empty reason | "Future steps get it by implementing one interface" |
| Who can ask, how often, what is charged? | Same as a revision: project **Edit** access, verified email, `RUN_NOT_FINISHED`, rate limit through the shared `revision` run-rate source (Paid bucket), `402 INSUFFICIENT_CREDITS` preflight over the charged steps, holds / settle / refund by `FailureClass` on the ordinary path | One rule set to explain |
| Can one request change several steps? | **Yes, up to 6**: one request = one replay run = one plan (`CompositionReplayPlanner.Plan` with a set of target orders) = one preflight and one rate-limit hit; the table keeps **one row per target** (unique `(execution_id, target_step_order)`), all rows sharing the run and the plan. The film can be one of the targets (it also writes a `composition_revisions` row of the same run); a reviser note names the steps re-made upstream | "Change the graphics and the voiceover with one review" without N runs; the single-target shape stays byte-identical |
| What stays free? | Every `Reuse` step (a copied row, no hold, no cache); inside the target step, every **unchanged item** (see "Items") | The goal of spending as few credits as possible |

## Product decisions

1. **A change makes a new linked run.** Never an edit of the finished record. The run page shows the
   lineage ("changed from run X, redid steps 3 and 7, cost N credits").
2. **Item granularity per step.** `Voiceover` = one narration line; `VideoGenerate` = one planned
   shot (all its takes, or one take); `AudioGenerate` = one take or the whole step;
   `ThumbnailGenerate` = whole step; **script (`ScriptwriterAgent`) = one scene**, or the whole step.
   A scene-level script edit still re-runs everything downstream of the script (narration, author,
   render), so the request form and the approval card label it as the most expensive option.
3. **Price display.** The same range the revision panel shows ("about X, usually A to B", `required` =
   what must be available), built from the replay plan; a cheaper exact figure for the target step
   where it can be computed (a voiceover line: bytes x the vendor rate).
4. **Downstream reviews.** A review of what the request re-made runs once (as in a replay); a review of
   only the changed step is skipped.
5. **Run-rate.** Change requests share the revision bucket (`Assistant:MaxPaidExecutionsPerHour`).

## Model

```
finished run R ──► POST /api/v1/projects/{p}/executions/{R}/change-requests
                    { stepOrder | stepResultId, itemIds?, instruction?, overrides? }
                      │  validate against StepChangeCapabilities (closed keys, ranges)
                      │  plan = CompositionReplayPlanner.Plan(R's own snapshot steps, R's results, target)
                      │  price = estimator over Revise + Run (+ KeepIfCompatible high end)
                      ▼
          step_change_requests row  +  hidden CompositionReplay snapshot workflow
          (copies of R's steps; target step cloned with CacheMode=Never)
                      ▼
          engine: CompositionReplaySession decides per step
             Reuse  → copy the result row (reused_from_step_result_id), free
             Revise → run the target executor with the request applied
             Run    → run (cache, holds, settle) because it reads the target's output
             Skip   → skipped row
```

* **Seed run.** The run the owner points at, executed from its own engine snapshot
  (`snapshot_definition_id`), so a later edit of the user's workflow never breaks it. A change request
  of a change request seeds from the replay run (a complete record of copied rows); any previous
  request context is irrelevant to the clone because the request lives in the table, not the step.
* **Plan and table.** `step_change_requests` is Inference API owned (API migration; the engine maps it
  `ExcludeFromMigrations()`, as `composition_revisions`). Columns: `id`, `project_id`,
  `organization_id` (denormalized, never a guard), `source_execution_id`, `target_step_order`,
  `target_step_result_id`, `step_type`, `instruction`, `scope_json`, `overrides_json`, `execution_id`
  (unique), `workflow_definition_id`, `replay_plan_json` (a `CompositionReplayPlan`), `requested_by_user_id`,
  `created_at`. No FK to engine tables; no tenant query filter (a child of a project, guarded at the
  project like `composition_revisions`; registered in `TenantBoundaryInventoryTests`); purged with the
  account. Status is read from the execution, never copied.
* **Engine session.** `CompositionReplaySession.LoadAsync` finds its plan in `composition_revisions` or
  in `step_change_requests`. The planner is parameterised only by `revisedStepOrder`; its reason
  strings become generic ("the step this request changes"). `ReplayStepDisposition.Revise` means "the
  target step": for a `Revise` step it still runs the Remotion reviser, for any other step type it
  runs that type's own executor with the request applied.
* **Cut policy.** `KeepCut` for every non-film target. A downstream editorial decision is kept only when
  `CutCompatibility`/`EvaluateKeep` says everything it was decided from is unchanged; otherwise it is
  re-made, exactly as in a composition replay.
* **Film step.** The Remotion revision stays at `/revisions` and keeps its richer lineage. The
  capability entry for the film step (an `Agent` step with sandbox tools, found by the existing
  `CompositionReplaySources`) names `route: composition-revision`, so the run page, the assistant and
  MCP show **one** "Request changes" for every step and send a film request to the revision service.

## Capability registry and the drift guard

```csharp
// ReelBolt.Shared/Workflows/ChangeRequests/StepChangeCapabilities.cs
StepChangeCapability For(StepType type);        // never throws; every member has an entry
record StepChangeCapability(
    bool Supported, string? NotSupportedReason,
    StepItemKind ItemKind,                      // None | Line | Shot | Take | Scene
    IReadOnlyList<ChangeOverrideSpec> Overrides,  // closed catalogue: key, type, range, enum
    bool AllowsPlainRetry);
IReadOnlyList<StepItem> ListItems(StepType type, string? outputJson);   // pure, from the stored output
```

* A request carries `{ instruction?, itemIds?, overrides: { "<itemId>" | "*": { key: value } } }`.
  Unknown keys, out-of-range numbers and an unknown item are refused before anything is written
  (`ChangeRequestValidator`, the same "closed catalogue, named violation" style as the step-config
  validators). Free text never reaches a provider unsanitised: each type's executor runs the text it
  accepts through that type's existing sanitiser.
* `ListItems` reads only the step result's stored output, so the run page, the assistant and MCP all show
  the same items with the same ids. Item ids are the step's own (`vo{n}` for a narration line,
  `v{n}` / `v{n}t{k}` for a shot/take), valid because the replay reuses the plan the ids came from.
* **Drift guard** (`StepChangeCapabilitiesDriftTests`, copying `RunRateClassifierTests`):
  every `StepType` has an entry; `Supported` implies an `IChangeRequestAwareStepExecutor` registered
  for that type and at least one validator test; `NotSupported` has a reason.
* Entries: `Voiceover`, `VideoGenerate`, `AudioGenerate`, `ThumbnailGenerate` are Supported, and an `Agent` step is decided
  by the agent it hosts (`ForAgent`: the built-in `ScriptwriterAgent` by scene); the rooms and the other agents are
  NotSupported with the reason "not built yet" (the guard forces an entry to flip together with its executor). Types that have no output a
  person would ask to change (`Conditional`, `ForEach`, `Parallel`, `Extract`, `ExtractFrame`,
  `PinInsert`, `DesignSystem`, `ReviewLoop`) are NotSupported with a stated reason ("a routing /
  bookkeeping step"; their change is a change to what feeds them). `Revise` points at the revision route.

## Slice 1: Voiceover line re-synthesis

Items are the step's lines (`vo{n}`). Overrides per line (or `*`): `text`, `voiceId`, `speed`
(0.5..2.0, Fish prosody), `pronunciation: [{term, spelling}]` (a written-accent respelling such as `Novara` -> `Novàra`, applied to
that line's spoken text only, same rules as the project pronunciation glossary: the one mechanism measured to work
for Italian on Fish; IPA is not offered). The instruction is advisory text shown in the lineage and, for the assistant,
turned into overrides by the model before Propose.

Executor behaviour (`VoiceoverStepExecutor`, the only place line audio is made):

1. Resolve lines exactly as a normal run does (the upstream plan is a `Reuse` step, so line ids match).
2. Apply the request's overrides by line id.
3. For a line **not in scope and unchanged**, copy its previous result entry (audio key, duration,
   words, alignment, fit) from the target's source result: no object-store probe, no Fish call, no
   alignment (ASR) charge. A line in scope whose sanitized text, voice, model and pronunciation entries
   hash to an existing `voiceover/lines/{sha256}.wav` is also free (the content-addressed store).
4. Re-run the deterministic fit solver over the new durations; a line that now overflows its declared
   window is reported in `meta.fit` (a manual override bypasses the narration-fit review cap, because the
   owner asked for it).
5. Every new Fish byte is metered as usual; the per-line content key gains the speed and pronunciation
   override material only when present, so old audio stays addressable.

Downstream: `VideoCompile` reads the voiceover step's output, so the planner marks it and everything that
reads its output `Run`; the step cache hashes the new upstream output (`StepUpstreamDigest`), so the
compile re-mixes with the new audio and nothing else about the cut changes.

## Several targets

`CreateStepChangeRequest.targets[]` (max 6, distinct steps) replaces the top-level target fields. The planner takes the set: targets
are `Revise`, the downstream union runs, `Reuse` before the first target or off every target's cone, a review skipped when every
replayed input it reads is a target. `CompositionReplayPlan.RevisedStepOrders` is additive (omitted for one target; `RevisedStepOrder`
is the smallest). The engine finds a target's row by `(execution_id, step_order)` and the plan on the lowest row. The film
target adds a `CompositionRevision` row (same `ExecutionId`, combined plan) and the film step becomes the `Revise` step; the reviser
gets a first-party note (step type and validated item ids only). Pricing is one preflight with per-target scales; the rate window
counts distinct runs. Single-target requests, and a film asked alone (`409 USE_COMPOSITION_REVISION`), are unchanged.

## Surfaces

* **API**: `POST/GET /api/v1/projects/{p}/executions/{e}/change-requests` (create / list with each
  request's own run status), `GET .../executions/{e}/change-capabilities` (per step result: supported,
  reason, items, override catalogue, route). 404 across tenants, 403 for same-org View-only.
* **Run page**: a "Request changes" button on `StepResultPanel` for steps whose capability is supported;
  a small form generated from the override catalogue; the price range and shortfall before confirming;
  lineage list of requests (what was redone, what it cost) next to the existing revision history.
* **Assistant**: `ProposeStepChange` / `ApplyStepChange` (Propose / Apply pair, `IConfirmationTokenService`
  token over `{operation, canonicalPayload, userId, orgId}`), an approval card built from the DB
  (`AssistantApprovalCardBuilder`) with the price and what is redone / reused; the Apply is gated by name
  in `AssistantApprovalCatalog`. Tool count and the MCP fixture move in lockstep with the other
  initiatives (see "Coordination").
* **MCP**: the same tools through the assistant tool catalog; no approval card over MCP (as for
  revisions), so Apply over MCP refuses without the figure the Propose returned.

## Coordination

The assistant tool count and `mcp/test-fixtures/assistant-tool-names.json` move with whichever PR merges first; this
feature adds three tools (`GetChangeOptions`, `ProposeStepChange`, `ApplyStepChange`).

## Failure codes and billing

`CHANGE_REQUEST_*` codes follow the revision rules: `CHANGE_NOT_APPLIED` (override matched nothing) is
`UserInput`, no refund; provider failures keep their class (`PlatformProviderError` refunds); a failed
target step makes every `Run` step downstream `Skipped`, never run on stale input. Holds are planned once
per run by `RunCreditHolds`; the target voiceover step holds like any voiceover step and settles to what
Fish actually billed, so a one-line change settles to one line.

## What this does not do yet

In-place versions, scene-level script edits, per-token audio editing, and re-mixing without re-encoding
the whole film (the compile re-runs; its cost is the render tier of the original run).
