# Workflow execution optimization

Three independent mechanisms that make workflow executions — especially *repeated*
executions of the same workflow over an unchanged project — cheaper in tokens, wall-clock
time, and money:

1. **[Step-result caching](#1-step-result-caching)** — a repeated execution reuses a prior
   successful step's output instead of re-calling the agent or re-running ffmpeg.
2. **[The prompt-context budget](#2-the-prompt-context-budget)** — a deterministic,
   non-LLM bound on how much prior-step output is concatenated into an agent's prompt.
3. **[Sandbox edit tools](#3-sandbox-edit-tools)** — agents change an existing file with a
   targeted edit instead of re-emitting the whole file.

A fourth, **[compaction inside the tool loop](#5-compaction-inside-the-tool-loop)**, bounds
what an agent accumulates *after* it starts: the tool results it re-sends to the model on
every call.

All of them are **additive and independently switchable**. With every knob at its default
the platform behaves exactly as before apart from the intended savings; with all of them
disabled it is byte-for-byte the old behaviour.

Two further sections are not switchable and are not optional: **[§4](#4-engine-to-inference-api-authentication)**
documents how the engine authenticates to the Inference API, and
**[§6](#6-tool-output-hygiene-and-the-render-check-tools)** documents the bound on what a
sandbox command may return to a model, and the tools that let an agent check a render before
paying for it.

---

## 1. Step-result caching

### The problem

Every execution of a workflow re-runs every step from scratch. Running
`quick-win-promo` twice on a project whose files have not changed pays for the five
analysis agents twice, even though — given identical inputs, identical prompts and an
identical file inventory — the second run is asking the same question and (modulo
sampling) getting the same answer. The same is far worse for the video templates, where a
`VideoAnalyze` step can burn fifteen minutes of ffmpeg decode plus a full ASR pass before
a single token is spent.

### The cache key

A cache entry is looked up by `(ProjectId, CacheKey)`, where `CacheKey` is a SHA-256 over a
length-prefixed, field-named encoding of:

| Field | Why it is in the key |
|---|---|
| `SchemaVersion` | Lets a future change to the encoding invalidate every existing entry at once. Now `v10`: the resolved-provider fingerprint (`ResolvedProviderFingerprint`) gained an `AudioIsolation` line (hosted voice isolation, audio-cleanup), which changes every step's key, so every pre-v10 row orphans. An unresolved capability is `-` as before. |
| `AlgorithmRevision` | `DeterministicStepRevisions.For(stepType)` — a tag per deterministic step type, bumped whenever its executor would produce different output from the same config and inputs. The other fields cover what goes IN, not the code; without this a fixed `VideoAnalyze` tracker kept serving an identical re-run its pre-fix analysis. Agent steps need no tag: their system prompt is already hashed. |
| `ProjectId` | Entries are never shared across projects. |
| `StepType`, `AgentType`, `AgentDefinitionId` | Different step/agent → different answer. |
| `AgentSystemPrompt`, `AgentOutputSchemaName`, `AgentAssignedSkillsJson` | Editing an agent's prompt, its output schema or its skills must invalidate everything it ever produced. Hashing the prompt *text* (rather than a version counter) means this is true even for an edit made directly in the database. |
| `AgentInferenceProviderId` | Pointing an agent at a different model is a different agent. |
| The six step config blobs (`Extract`/`VideoAnalyze`/`VideoCompile`/`EditRoom`/`GraphicsRoom`/`ColorGradeRoom`), `AgentInputContextMode`, `SelectedPriorStepOrdersJson`, `InputMappingJson`, `ConditionExpression`, `MaxIterations`, `MinScore` | The step's own configuration. |
| **`ResolvedInput`** | The fully-built step input string. For an `Agent` step it transitively contains every upstream step's output the prompt reads. For a deterministic step it is only the PREVIOUS step's output — which is why `UpstreamDigest` exists. |
| **`UpstreamDigest`** (schema `v5`) | A SHA-256 over every earlier step this step reads, as the dependency analysis (`WorkflowStepDependencyGraph`, the same one a composition revision's replay is planned from) names them: each one's output text and its output/artifact storage keys. A compile reads its decision AND its analysis artifact AND the video that analysis read; with only the previous step's output in the key, a re-run whose decision was unchanged but whose analysis or film changed — exactly what a revision that keeps the cut produces — was served the OLD compile. Over-inclusion only costs a miss. See [composition-revisions.md](composition-revisions.md#replaying-the-steps-that-depend-on-the-film). |
| `UserRequest` | The free-text request is part of the prompt. |
| `RunStyle` (hashed only when present) | `RunStyleOverrides.CacheKeyComponent`: the run's editing style, format override, resolved target format and, only when the run chose one, `\|resolution={n}` (for example `style=1:-\|format=-\|target=-\|resolution=2160` for a run that chose only 4K). A run that chose none of them hashes exactly the fields it did before, so `SchemaVersion` stays `v5`. See [editing-styles.md](editing-styles.md#step-cache-key). |
| **`ProjectFileFingerprint`** | The one thing the resolved input does *not* cover. An agent holding `ToolGroup.ProjectRead` reads project files through tools, so that state never appears in its prompt. The fingerprint — a SHA-256 over every `ProjectFile` row's `(Id, StorageKey, SizeBytes, UploadedAt, SummaryStatus, IndexingStatus)`, ordered by id — closes that hole. Without it, uploading a new file would not invalidate an analysis agent's cached answer *about that project's files*. |

Fields are encoded name-prefixed and length-prefixed specifically so that no two different
field sets can collide through plain concatenation (`{A="ab", B="c"}` must not hash the
same as `{A="a", B="bc"}`); there is a unit test pinning exactly that.

### What is cacheable, and what is deliberately not

`StepCachePolicy` decides. A `WorkflowStep.CacheMode` of `Never` or `Always` overrides it;
`Default` (the default, stored as `null`) falls through to the policy:

| Step type | Cached by default | Why |
|---|---|---|
| `Extract` | yes | Deterministic and pure. |
| `VideoAnalyze` | yes | The single most expensive step in the platform. Deterministic given the same source. |
| `VideoCompile` | yes | Minutes of ffmpeg encode. Its `OutputStorageKey` is validated (below) before a hit is accepted. |
| `EditRoom`, `ColorGradeRoom` | yes | Many LLM calls per run; neither writes anything outside its own output. |
| `Agent`, where the agent's tool grant contains **none** of `ProjectWrite` / `SandboxAuthoring` / `SandboxRender` | yes | The output *is* the whole effect of the step. |
| `Agent`, where it does | **no** | These agents mutate state that lives **outside** the cached output — project files, the sandbox workspace, a rendered media object. Replaying the output would silently skip a side effect that downstream steps depend on: a cached `AuthorAgent` result would hand the next step a storage key for a render that this execution's sandbox never produced. This excludes `AuthorAgent`, `RemotionComponentTranslator`, `MotionGraphicsPlanner` and `MotionGraphicsDirector`. |
| `GraphicsRoom` | **no** | Same reason — its director may render an overlay asset during synthesis. |
| `ReviewLoop` | **no** | Its entire purpose is to judge *this* iteration's output afresh. A cached score would defeat the loop. |
| `Conditional`, `ForEach`, `Parallel` | no | Control flow; nothing worth caching. |

### Safety properties

- **The cache can never fail a workflow.** Every database and S3 operation inside the
  lookup and store paths is wrapped; any failure is logged at warning level and degrades to
  "no cache". A cache that is down is a cache that is slow, never a broken execution.
- **Artifact validation.** When `ValidateStorageArtifacts` is on (default), a hit carrying an
  `OutputStorageKey` or `ArtifactStorageKey` is only accepted after the object is confirmed
  to still exist in the object store (Garage). A stale row pointing at a swept object is deleted and treated as
  a miss, rather than handing a downstream step a key that 404s.
- **A compile above the run's output cap is a miss.** For a `VideoCompile` lookup the engine sets
  `StepCacheKeyInputs.MaxOutputShortSide` (not hashed) to the run's current effective cap
  (`RunStyleOverrides.OutputCap`: Auto is `min(plan, 1080)`, unlimited on an unlimited plan; a
  chosen resolution is `min(choice, plan)`). A cached compile whose encoded short side is above it is
  treated as a miss (not evicted; the re-run upserts its own result under the same key), so an org
  that moved to a smaller plan is never served a render made under the larger one. The encoded short
  side is the compile output's `output.encodedShortSide`, which the compile writes only when the plan
  cap scaled the canvas down, else the shorter of `output.width` / `output.height`. An output that
  says neither, or a run with no organization or entitlement service, is not checked.
- **Voiceover hits are validated on hit.** A hit does not skip the voice consent check: a Voiceover
  hit is served only while every voice the cached output used is still usable (a refused hit is a miss) (`IVoiceReuseGuard`,
  see `docs/voiceover.md` "Voices and consent"); otherwise it is a miss and the step runs live.
- **A hit is indistinguishable downstream.** A cache hit takes the same persistence and
  event-publishing path as a real result: the same `WorkflowStepResult` write, the same
  `WorkflowStepCompleted` / diagnostics publish, the same step-output-history append, the
  same `accumulatedOutput` update. Nothing after the executor needs to know.
- **No foreign keys.** `workflow_step_cache_entries` deliberately has no FK to
  `projects` or `workflow_definitions`, so editing a workflow definition does not cascade
  away entries that are still perfectly valid. Entries are reclaimed by TTL instead.

A hit is recorded on the step result as `FromCache = true` with `TokensUsed = 0` (this
execution genuinely spent nothing) and `CachedTokensSaved` carrying what the original run
cost, which is what the execution UI reports as savings.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `WorkflowEngine:StepCache:Enabled` | `true` | Master switch. |
| `WorkflowEngine:StepCache:TtlHours` | `168` | Entry lifetime (7 days). |
| `WorkflowEngine:StepCache:MaxEntryChars` | `2000000` | Outputs larger than this are not stored. |
| `WorkflowEngine:StepCache:ValidateStorageArtifacts` | `true` | HEAD the object before accepting a hit that carries a storage key. |

---

## 2. The prompt-context budget

### The problem

`StepExecutionContext.BuildFullWorkflowInput()` concatenates **every** prior step's full
output into the next agent's prompt. Across the eleven-step default pipeline that is
quadratic: step 11 pays for steps 1-10 in full, step 10 paid for 1-9, and so on. Most of
that is analysis output the production agents skim once and never refer to again.

### The mechanism

A deterministic, non-LLM budget (`AgentInputBudget`) applied **only** when the composed
input exceeds `MaxInputChars`:

1. The most recent `RecentStepsVerbatim` entries (by descending `StepOrder`) are protected
   and passed through untouched.
2. Every older entry is compacted by `JsonOutputDigest`: parsed as JSON and rewritten with
   long strings truncated (`"…(+N chars)"`), over-long arrays capped with a trailing
   `"…(+N more items omitted by context budget)"` element, and subtrees past a depth limit
   elided. **The digest is still valid JSON.** Output that is not JSON gets middle-out
   truncation with an explicit character count in the marker.
3. If it still does not fit, entries are dropped oldest-first, each replaced by a one-line
   marker that keeps its `## Step N: Label` header so the agent knows the step existed and
   can go re-read it with the project/file tools.
4. If even the protected entries alone exceed the budget, they are returned intact. We
   never corrupt a machine-consumed contract to hit a number.

### Safety contract

This is the part that matters, and it mirrors the contract already documented on
`SetPromptOutputOverride`:

- Budgeting is **prompt-only**. `StepOutputHistory` itself is untouched, the persisted
  `WorkflowStepResult.OutputJson` is untouched, and every deterministic by-`StepOrder`
  consumer — `VideoCompileStepExecutor` resolving a decision, a graphics plan, a music plan
  or an analysis artifact — keeps reading the raw output.
- `AgentInputContextMode.PreviousStepOnly` is **never** digested or truncated. The previous
  step's output is routinely a machine-consumed contract (a `{view, meta}` envelope, a
  structured decision) that the next agent must see whole.
- The most recent N entries in `FullWorkflow`/`SelectedPriorSteps` mode are protected for
  the same reason.
- `CustomMappedSubset` is never digested; the mapping already narrowed it deliberately.
- **When the input already fits, the result is byte-identical to the previous behaviour.**
  There is a regression test that builds the legacy concatenation by hand and asserts
  equality, rather than calling the production helper.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `WorkflowEngine:ContextBudget:Enabled` | `true` | Master switch. |
| `WorkflowEngine:ContextBudget:MaxInputChars` | `160000` | Overall cap; `<= 0` disables. |
| `WorkflowEngine:ContextBudget:MaxDigestedStepChars` | `12000` | Per-step cap once digesting engages. |
| `WorkflowEngine:ContextBudget:RecentStepsVerbatim` | `2` | How many trailing steps are protected. |
| `WorkflowEngine:ContextBudget:DigestMaxStringChars` | `600` | String truncation threshold inside the digest. |
| `WorkflowEngine:ContextBudget:DigestMaxArrayItems` | `25` | Array cap inside the digest. |
| `WorkflowEngine:ContextBudget:DigestMaxDepth` | `12` | Depth cap inside the digest. |

---

## 3. Sandbox edit tools

### The problem

An agent authoring Remotion code in the sandbox had exactly one way to change a file:
`WriteSandboxFile(path, content)` — re-emit the entire file. Changing three lines of a
400-line composition cost 400 lines of output tokens, and, worse, made long files
*unreliable*: a model re-emitting a file it did not write character-for-character silently
drops code it fully intended to keep. Most of the `ReviewLoop` iterations in the default
pipeline are spent recovering from exactly that.

### The tools

| Tool | Group | Purpose |
|---|---|---|
| `EditSandboxFile(path, oldText, newText, replaceAll)` | `SandboxAuthoring` | Replace one unique snippet. Preferred over `WriteSandboxFile` for any change to an existing file. |
| `ApplySandboxFileEdits(path, edits[])` | `SandboxAuthoring` | Several hunks in **one** read and **one** write. |
| `InsertSandboxFileLines(path, beforeLine, text)` | `SandboxAuthoring` | Insert lines at a position (`-1` appends) without changing any existing line: an import at the top, a component above another. Line numbers are `ReadSandboxFileLines`' own. |
| `ReadSandboxFileLines(path, startLine, lineCount)` | `SandboxBrowse` | Read part of a large file with line numbers, instead of paying for all of it. |
| `GetSandboxFileOutline(path)` | `SandboxBrowse` | Imports, exported symbols with line numbers, and `<Composition>` ids — so the agent can locate code before reading it. |

### Design decisions

- **All-or-nothing.** In a multi-edit call, edits are applied in order against the result of
  the previous one, entirely in memory; if any one fails, nothing is written. A partially
  applied edit list leaves the sandbox in a state neither the agent nor the caller can
  reason about.
- **Ambiguity is an error, not a guess.** Zero matches fails with an instruction to re-read
  and copy the exact text; more than one match fails asking for more surrounding context,
  unless `replaceAll` was set explicitly.
- **Failures return JSON, they do not throw.** A bad `oldText` comes back as
  `{"ok": false, "error": "..."}` so the model corrects itself on the next turn, rather than
  burning a whole step retry.
- **Line endings are normalised.** Models routinely emit `\r\n` against an `\n` file (or the
  reverse). Failing on that would push the agent straight back to the whole-file rewrite
  this feature exists to eliminate, so the editor reconciles the two before matching.
- **Matching is ordinal, never regex.** `oldText` containing `.*` or `$1` is literal text.
- **An insert lands on a line boundary.** `InsertSandboxFileLines` converts its text to the
  file's own line ending and terminates it, so it never merges into the line it lands in front
  of; a position outside the file fails with the valid range (`1..totalLines+1`, or `-1`).
- **Scoping.** The edit tools mutate, so they live in `ToolGroup.SandboxAuthoring`; the read
  tools are in `ToolGroup.SandboxBrowse`. Both are registered in `ToolGroupCatalog` and
  `AgentToolProvider`, which `ToolScopingDriftGuardTests` pins together.

The default system prompts for `AuthorAgent`, `RemotionComponentTranslator`,
`MotionGraphicsPlanner` and `MotionGraphicsDirector` now direct the model to prefer a
targeted edit, and to locate code with the outline + ranged read rather than reading whole
files. The tools are worthless if the model does not reach for them.

### Copying a project file into the sandbox

`StageProjectFile(fileReference, sandboxPath?)` (`SandboxAuthoring`) copies one project file — an
uploaded logo, screenshot, font, image, audio or video clip — into the sandbox by its bytes, so a
composition can use the project's own assets. The file is named the way `ListProjectFiles` lists it
(id, storage key or original file name, resolved with `ProjectFileWorkspace`'s precedence over the
project's own list), and moved with the same transport as `StageVoiceoverAudio` and
`StageGeneratedClips`: a presigned GET checked against `projects/{projectId}/`, or the engine's own
read when there is no presigner. The content never passes through the model, so binary files work.

- **Destination is `public/` only** — Remotion's static folder, which `staticFile()` reads. The
  default is `public/<original file name>` (unusual characters replaced); a requested path is
  forced under `public/` and must be plain file-name segments with no `..`. A staged file can
  therefore never overwrite source code, `package.json` or `node_modules`. The result carries the
  `staticFile` value to use in code (`public/brand/logo.png` → `staticFile('brand/logo.png')`).
- **Capped** at `Compute:Render:SourceMaxBytes` (256 MiB by default), checked from the file's
  recorded size before anything is copied.
- **Not guardrail-screened**, like the other `Stage*` tools, so it **refuses code and data files**
  (`.js` `.jsx` `.ts` `.tsx` `.mjs` `.cjs` `.mts` `.cts` `.json` `.wasm` `.html` `.htm`), judged on
  the source file's name and on the destination. Otherwise an agent could write a payload into an
  unscreened project file (`WriteProjectFile` is not screened), stage it, and leave the screen only
  a one-line import of it to judge. Code goes through `ReadProjectFile` + `WriteSandboxFile`, which
  is screened.

## 4. Engine to Inference API authentication

Tools that call the Inference API from the WorkflowEngine (for example `search_project_files` ->
`POST /api/v1/projects/{id}/files/search`) previously sent no `Authorization` header and always got 401.
They now use the named client `InferenceApi` (`InferenceApiClient.Name`), whose `InferenceApiAuthHandler`
reads the ambient `IWorkflowExecutionContextAccessor.Current`, looks up the execution's
`InitiatedByUserId` and that user's real email/`isAdmin`, and attaches a 5-minute token from
`OnBehalfOfTokenIssuer` (claims `sub`, `email`, `isAdmin`, `act=workflow-engine`, `executionId`; signed
with `Jwt:SigningKey`). With no execution context or initiator the request carries no token (401 as
before) and a warning is logged; there is deliberately no admin/service fallback. The Inference API
needs no change: these are ordinary JWTs and per-project ownership checks still apply.

---

## 5. Compaction inside the tool loop

### The problem

The prompt-context budget (section 2) bounds the **step input**: what is built from prior
steps, once, before the agent starts. It does not touch what grows *afterward*. An agent runs
as a tool-calling loop (`ChatClientAgent` over `FunctionInvokingChatClient`), and that client
keeps ONE accumulating message list and re-sends the whole list to the model on every
iteration. Every tool result therefore stays in the prompt for every later call.

Measured on execution `1aa8d6f8-c626-4de1-8f14-c04115ff763b`: the Author step's input was
62 KB, yet attempt 2 consumed 10.4 M input tokens over about 50 model calls. A single raw
`remotion render` result was 124-140 KB and was paid for again on each following call. The
input budget was never the bug.

Truncating tool output at its source is a separate, complementary fix. This is the generic
safety net, so no loop can grow without bound whichever tool produced the bytes.

### Where it sits

```
ChatClientAgent
  └─ FunctionInvokingChatClient      (the loop; owns the accumulating history)
       └─ ToolLoopCompactingChatClient   <- sees the WHOLE list on every iteration
            └─ provider client (OpenAI / Azure / Anthropic / ...)
```

The compactor has to sit **below** `FunctionInvokingChatClient`: that is the layer that calls
down once per iteration with the full history. Above it, a client sees a single call (the
agent's) and never the loop. `ChatClientAgent` adds its own `FunctionInvokingChatClient` only
when the client it is given does not already contain one (`WithDefaultAgentMiddleware` checks
`GetService<FunctionInvokingChatClient>()`), so `ToolLoopPipeline.Wrap` builds that client
itself, in order to put the compactor under it. As a side effect it is also the only place the
loop's limits can be set, because `AsAIAgent` exposes no setting for them.

The wrap is one line in `ReelBoltAgentBase.CreateAgentAsync`. Room participants
(`RoomStepExecutorBase.BuildInnerAgentAsync`) build their agent separately and are **not**
wrapped; their turns run read-only tools with bounded output and their history is bounded by
`MaxHistoryChars`. The room's synthesis call goes through `ReelBoltAgentBase` and is covered.

### What it does, per model call

`ToolLoopCompactor` measures the call (instructions + every message, in characters, the same
token proxy the input budget uses; `ChatClientAgent` passes the system prompt as
`ChatOptions.Instructions`, so that is counted too). At or under `MaxContextChars` the very same
list is forwarded: byte-identical, nothing allocated. Over it, working on a **copy**:

1. Tool results older than the last `RecentToolResultsVerbatim` are digested, oldest first,
   stopping the moment the call fits. The digested set therefore only grows as the loop
   advances, so the same old result is digested identically on every later call.
2. If that was not enough, the verbatim window shrinks, oldest of it first. The single latest
   result is never digested: it is the one the model is reacting to.
3. Last resort (`CompactCallArguments`): string arguments of older tool *calls* (a
   `WriteSandboxFile` carrying a whole TSX file) are digested.

A digest is a marker line `[compacted: N chars, M lines]` followed by either a structural
`JsonOutputDigest` rewrite (JSON results; the body stays valid JSON) or the first and last
lines with the middle dropped.

### Safety contract

- A tool call is **never** removed or separated from its result: contents are replaced in
  place with the same call id and name, so the OpenAI wire format never sees an orphaned call
  or an orphaned result.
- Instructions, the original prompt, all assistant text (including a final answer) and all
  other content are never touched.
- **Signed thinking blocks (Anthropic).** Anthropic binds a thinking block's signature to the
  conversation before it and answers `400 invalid_request_error ... Invalid signature in thinking
  block` when that earlier history reaches it changed. Compaction therefore never alters anything
  ahead of the latest signed thinking block (`TextReasoningContent` with `ProtectedData`): that
  prefix is sent exactly as it was on the call that produced the block, the thinking block itself is
  never edited or dropped, and only tool results after it are digested. Whole old call and result
  pairs are never dropped. The trade-off is less compaction while thinking is on: a long tool loop can stay above
  `MaxContextChars` where an OpenAI or Azure loop of the same size would have shrunk. A conversation
  with no signed thinking block is compacted as before (`ToolLoopCompactor.Compact`). The freeze
  applies only when the provider below the compactor reports `ChatClientMetadata.ProviderName`
  `anthropic` (the Anthropic SDK adapter does, through `AnthropicChatOptionsAdapter`). OpenAI's
  Responses adapter also fills `ProtectedData`, from `reasoning.encrypted_content`, but that state
  is not bound to the prefix, so OpenAI, Azure and every other provider compact exactly as before.
- **Hard ceiling (Anthropic).** Because the frozen prefix cannot shrink, a long Anthropic loop
  with one tool call per turn grows by every new result. Before each model call the compactor
  checks what it would send (the frozen prefix plus the pending results, after every compaction
  still allowed, instructions included) against `WorkflowEngine:ToolLoopBudget:HardMaxChars`
  (default 900000 chars). Over it, the call is not made: the agent step fails with
  `CONTEXT_TOO_LARGE` (`ContextTooLargeException`, failure class `PlatformInternal`, so the step
  is refunded) and the message "This step read more than the model can hold at once. Ask it to
  read files in slices (outline + line ranges) instead of whole files." This replaces the
  provider's own context-limit 400 a few calls later. It does not apply to other providers or
  when `Enabled` is false; `HardMaxChars` 0 or less switches it off.
- The caller's list is never mutated. `FunctionInvokingChatClient` builds the final response
  from its own raw history, so the persisted tool-call trace, reasoning capture and step output
  are unaffected. Only what the provider is sent shrinks. Modified messages are rebuilt without
  `RawRepresentation`, so a provider client cannot prefer a stale uncompacted original.
- `MaxContextChars` is a ceiling the compactor works toward, not a guarantee: a conversation
  made only of untouchable content can still exceed it.
- Compaction changes what the model can re-read, which is the point. An agent that needs an old
  tool result in full has to call the tool again.

### The loop's own limits

`ToolLoopPipeline` sets `FunctionInvokingChatClient.MaximumIterationsPerRequest` and
`MaximumConsecutiveErrorsPerRequest` from `WorkflowEngine:ToolLoop:*`. The defaults are the
Microsoft.Extensions.AI 10.10.0 defaults (40 and 3), so nothing changes unless configured.

When the iteration cap is reached the client makes one more call with the function
declarations stripped, forcing a text answer. That used to be indistinguishable from finishing.
Now `ToolLoopRunStats.IterationCapReached` is set (that extra call being made is the signal),
the engine logs a warning naming the agent and the call count, and the run's `Reasoning`
(persisted as `ReasoningJson` on the step result) gains a `[tool loop] Stopped at the iteration
cap ...` entry. `AgentRunResult` also carries `ModelCalls`, `PeakContextChars`,
`CharsCompacted` and `ToolLoopCapReached`.

### The log line

One line per agent run (also for failed and timed-out runs), logger
`ReelBolt.WorkflowEngine.ToolLoop` (the numbers below are illustrative):

```
Agent AuthorAgent tool loop completed: ModelCalls=41, InputTokens=2310000, OutputTokens=48000,
PeakContextChars=1480000, PeakForwardedChars=198000, CharsCompacted=1280000, CompactedCalls=33
```

`PeakContextChars` is what the loop assembled; `PeakForwardedChars` is what was sent.
`CharsCompacted` is the most removed from any one call, not a sum (the same old result is
digested again on every later call, so a sum would count it dozens of times). `InputTokens` is
what the provider reported, summed over every call.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `WorkflowEngine:ToolLoopBudget:Enabled` | `true` | Master switch. Off: every list is forwarded as built. |
| `WorkflowEngine:ToolLoopBudget:MaxContextChars` | `200000` | Soft ceiling per model call, instructions included. |
| `WorkflowEngine:ToolLoopBudget:RecentToolResultsVerbatim` | `4` | Trailing tool results kept whole while older ones are digested (floored at 1 in use). |
| `WorkflowEngine:ToolLoopBudget:DigestMaxChars` | `2000` | Size a digested result or argument is cut to. |
| `WorkflowEngine:ToolLoopBudget:CompactCallArguments` | `true` | Allow phase 3. |
| `WorkflowEngine:ToolLoopBudget:HardMaxChars` | `900000` | Anthropic only: a call over this, after compaction, fails the step with `CONTEXT_TOO_LARGE` instead of being sent (0 = off). |
| `WorkflowEngine:ToolLoop:MaxIterations` | `40` | `FunctionInvokingChatClient.MaximumIterationsPerRequest` (min 1). |
| `WorkflowEngine:ToolLoop:MaxConsecutiveErrors` | `3` | `FunctionInvokingChatClient.MaximumConsecutiveErrorsPerRequest` (min 0). |
## 6. Tool-output hygiene and the render-check tools

### The problem

An agent's tool results are replayed on every later turn of its loop, so a tool result costs
its size once per remaining turn. Execution `1aa8d6f8` (an Author step on `deepseek-flash`)
measured what that does when a result is large: a successful raw
`RunSandboxRemotionCommand("render", ...)` returned the command's whole stdout/stderr —
thousands of `Rendered 722/3555, time remaining …` and `Encoded 2174/2757` lines — at
**124–140 KB (35–45 K tokens) per render**. One attempt reached 10.4 M input tokens over 42
tool calls and 15 renders; the step used 22 M tokens over three attempts and ~76 minutes. The
run also spent most of those calls re-rendering and probing (`probe`, `probe3`, `small`,
`bench`, `bench2..4`, `clean-test`, …) because nothing cheaper told it whether a composition
worked, and because the first render's result did not say it was already the answer.

### Bounded command output

`SandboxOutputCompactor` bounds every sandbox command result — `RunSandboxRemotionCommand`,
`RunSandboxNpmScript`, `InstallNpmPackages`, `CheckLintAndTypeErrors`, and the failure path
(`outputTail`) of all of them:

| Rule | Value |
|---|---|
| Progress families (`Rendered n/N`, `Encoded n/N`, `Bundling n%`) | one summary line each with the **final** value, e.g. `[progress] Rendered 3555/3555 frames (3555 progress lines collapsed)` |
| Any other run of ≥ 4 lines that differ only in digits | first line, an `… (N similar lines omitted)` marker, last line |
| What remains | first `HeadLines` = 20 and last `TailLines` = 40 lines, with a `… [N lines omitted] …` marker |
| Characters | `MaxOutputChars` = 6,000 for stdout, `MaxErrorChars` = 2,000 for stderr; a single line is clipped at 300 |

A success now returns `{success, output, error?, outputLinesTotal, truncated, collapsedLines?,
progress?}`. `outputLinesTotal` is what the command really wrote; `truncated` is true only when
**real content** was dropped or clipped — collapsed progress does not set it, because the summary
line already carries the value; `progress` is the structured `{renderedFrames, encodedFrames,
totalFrames, bundlingPercent}`. ANSI colour codes and `\r`-redrawn progress are handled. The
class is pure and I/O-free; the 124–140 KB measurement is cited on `MaxOutputChars`.

### A finished upload is recorded first

`RenderVideoAndUploadToStorage` now sets `ctx.PendingOutputStorageKey` **immediately after the
upload succeeds**, before the scene sidecar and the usage metering — both best-effort. It used to
be set after them, so any slip in that post-work left an uploaded, finished render the step never
learned about. The success result also says so in so many words — `artifactRecorded: true` and a
`message` ("recorded as this step's output artifact; do not render again unless you change the
composition") — and reports `renderSeconds` and `framesRendered` from the render's own progress.

### The render-check tools (`ToolGroup.SandboxRender`, `ToolGroup.SandboxPreview`)

| Tool | What it answers | Why it exists |
|---|---|---|
| `GetCompositionInfo(compositionId?)` | Every registered composition as numbers: width, height, fps, `durationInFrames`, `durationSeconds`, `isStill`, plus `renderTimeoutSeconds`. | `remotion compositions` printed a table the model had to read; the master composition's real length is what decides whether a render will fit. Parsed by `RemotionCompositionTable` (the CLI's own table format). |
| `RenderPreviewStills(compositionId, frames?, scale?)` | Renders up to 6 low-resolution stills (default: first, last and three evenly spaced frames at scale 0.25) and reports per frame: did it render, PNG `sizeBytes`, `looksFlat`, and (with a Vision provider) `look`; plus `similarFrames`. Deletes its scratch; uploads nothing. | A missing asset, a throwing component or a `delayRender` timeout fails at one frame's cost instead of at the end of a 5-minute render, and a composition that draws nothing shows up as every frame being flat. |

`looksFlat` is a **heuristic** — a PNG under 0.01 bytes/pixel is a single colour. A fade-in's
first frame is legitimately flat. It stops at the first frame that fails (each frame is its own
bundle and browser launch) and names the frames it skipped.

**What the frames look like (`look`, `similarFrames`).** When every requested frame rendered, the
tool reads each PNG through the sandbox session *before* deleting the scratch (at most 6, each
capped at 2 MB; a larger one is left undescribed) and `PreviewFrameDescriber` sends them, in one
chat call, to the default Vision provider (`IInferenceProviderResolver.ResolveVisionAsync(null)`)
with one fixed layout-audit question: text overlapping or clipped at the frame edge, colliding
elements, an empty or flat frame, and a one-line description of the layout; then the pairs of
frames with near-identical layouts. Each frame gets `look` (one line, ≤ 400 chars) and the result
gets `similarFrames` (ordered pairs of frames it was sent) and `lookNote`. The call is made through
`IChatClientFactory`, so `MeteringChatClient` meters it against the step's usage scope like every
other model call; there is no new usage kind. Its deadline is 10 s per frame sent.

The frames are model-authored, so a description can carry injected text. It is **data**: control
and format characters stripped, whitespace collapsed, capped, frame numbers accepted only when
sent; `lookNote` says it is an observation of the picture, never an instruction. It lives only in
this tool result: it is never interpolated into a system or user prompt and never copied into the
review-feedback text. With no Vision provider, on a timeout, a provider error or an unreadable
answer, every `look` is `null` and `lookUnavailable` says why; the tool never fails because of
vision.

Both tools run Remotion and so sit with the render tools, with one exception: the ReviewAgent gets
the two of them alone through `ToolGroup.SandboxPreview` (read-only stills, no upload), so it can
see the frames it scores. Because it does not get `SandboxRender`, nothing keyed on that group
changes for it: it stays step-cacheable outside a ReviewLoop, its credit hold does not scale with
resolution, and the replay graph does not treat it as writing the sandbox.
`ToolScopingDriftGuardTests` pins "granted where `RenderVideoAndUploadToStorage` is, plus the
ReviewAgent, and nowhere else". They are
deliberately **not** guardrail-screened — `ScreenedToolNames` stays exactly six — because they
write no file and upload nothing, and the only code they execute went through the screened
`WriteSandboxFile`/`EditSandboxFile`/`ApplySandboxFileEdits`/`InsertSandboxFileLines`.

### Targeted project-file tools

Sandbox files had ranged reads and targeted edits (§3); project files had only whole-file
`ReadProjectFile`/`WriteProjectFile`. The twins:

| Tool | Group | Purpose |
|---|---|---|
| `ReadProjectFileLines(fileReference, startLine, lineCount)` | `ProjectRead` | Numbered slice + `totalLines`/`totalChars`/`truncated`. Same engine as `ReadSandboxFileLines` (`TextLineWindow`). |
| `GetProjectFileOutline(fileReference)` | `ProjectRead` | ts/tsx/js/jsx: imports and exported symbols (reuses `SandboxFileOutline`); markdown: heading tree with line numbers; anything else: size only. |
| `EditProjectFile(fileReference, oldText, newText, replaceAll)` | `ProjectWrite` | Ordinal, all-or-nothing replace through the **same** `SandboxTextEditor` as `EditSandboxFile`; failures are `{"ok": false, "error": …}`, never exceptions. |

`EditProjectFile` never mutates a stored object. Like `WriteProjectFile` it creates a **new**
`project_files` row (category `agentFiles`, same original path), so a user's upload is left intact
and `ProjectFileWorkspace`'s newest-first name lookup is what makes the edited version the one a
later read by name returns. Editing by name twice in a row therefore edits the latest version.

`ReadProjectFile` itself is now capped at `MaxReadProjectFileChars` = 60,000 characters (about
15 K tokens): a longer file is cut at a line boundary and the result says which line to continue
from with `ReadProjectFileLines`. Skill documents (`UseSkill`/`ReadSkillResource`) are static,
curated corpus files and were left whole.

The `AuthorAgent` prompt (and its byte-identical seeder copy) now tells the model to render once
after a `RenderPreviewStills` pass, to stop after a successful upload, and never to render
benchmark or probe files. As in §3, the tools are worthless if the model does not reach for them.

## 7. Per-agent reasoning effort

How hard an agent's model is asked to think is a field on the agent, not only a constant in code.

**Where it lives.** `agent_definitions.reasoning_effort` (`AgentDefinition.ReasoningEffort`, a nullable
string, migration `AddAgentReasoningEffort`; the WorkflowEngine maps the table read-only and carries an
empty migration of the same name only so its model snapshot stays current). The vocabulary is
`none`, `low`, `medium`, `xhigh` (`AgentReasoningEffort.Vocabulary`). **`high` is deliberately not in it:**
the deployed Qwen chat template raises on anything else, so every provider path must tolerate every
member, and a self-hosted Qwen endpoint does not tolerate `high`. DeepSeek and Anthropic tolerate all
four.

**Resolution order for a run:** the stored value of the definition driving the run (looked up by
definition id, then by built-in agent type, through the same 60-second resolver snapshot that serves the
provider override - `IInferenceProviderResolver.ResolveAgentReasoningEffortAsync`) -> the configuration
key `Agents:<name>:ReasoningEffort` -> the `AgentModelSettings` default in the agent's class. A NULL
column means "use the code default", so a row from before the column existed behaves as it always did.
The agent states the result provider-neutrally as `ChatOptions.Reasoning`; it is projected per provider
kind at the client boundary by `ReasoningEffortMapper`:

| Kind | `none` | `low` / `medium` / `xhigh` |
|---|---|---|
| Azure OpenAI, OpenAI-compatible (any host but api.openai.com), Gemini | `reasoning_effort: "none"` (unchanged) | sent as is (unchanged) |
| OpenAI-compatible at `api.openai.com` | `reasoning.effort: "none"` on the Responses API | `reasoning.effort` on the Responses API; `temperature`/`top_p`/`top_k` are not sent |
| Anthropic | floored to `low` (`thinking.type=disabled` is a 400 on an always-thinking model) | `output_config.effort` |
| DeepSeek | `thinking: {"type":"disabled"}`, no `reasoning_effort` | sent as is; DeepSeek maps medium/xhigh to `high` |

See [deepseek-provider.md](deepseek-provider.md#reasoning-effort-and-turning-thinking-off) and
[anthropic-provider.md](anthropic-provider.md) for the vendor facts behind the Anthropic and DeepSeek rows.

**OpenAI Responses routing.** An `OpenAICompatible` provider whose endpoint host is exactly `api.openai.com`
(case-insensitive, any scheme or path such as `/v1`; `api.openai.com.evil.example` or a path containing the
name do not match) is built on the OpenAI Responses API (`POST {endpoint}/responses`) instead of Chat
Completions, because OpenAI's Chat Completions endpoint refuses function tools combined with
`reasoning_effort` for its newer reasoning models, and every agent run sends both. The calls are stateless:
`store: false`, no `previous_response_id`, the full history is resent each turn exactly as on Chat
Completions. When the effort is not `none`, `include: ["reasoning.encrypted_content"]` is requested so each
reasoning item comes back with its encrypted state and is replayed between tool calls; a reasoning item in
the history that carries no encrypted state is dropped before sending, since the server cannot resolve it by
id without stored state. OpenAI also answers 400 to a `temperature` other than 1 or any `top_p` while
reasoning is on, so for this host (and only this host) `temperature`, `top_p` and `top_k` are not sent when
the effort is not `none`. Every other OpenAI-compatible endpoint (vLLM, Ollama, speaches, Gemini's
compatibility layer) stays on Chat Completions with both its sampling parameters and `reasoning_effort`,
byte-identical to before. To use an OpenAI model as an agent, add an `OpenAICompatible` `Chat` provider with
endpoint `https://api.openai.com/v1` and the model name; nothing else is needed.

**Seeded defaults.** `DatabaseSeeder.BuiltInReasoningEfforts` writes each built-in row's value when the
column is NULL and never over a value an admin set (unlike the prompt and schema, which a built-in always
mirrors). It must equal the agent class default; `AgentReasoningEffortTests` holds the two together.
Author, Director, RemotionComponentTranslator and AnimationStrategy ship at `low`, not the `xhigh` they
used to have: execution 1aa8d6f8-c626-4de1-8f14-c04115ff763b showed deliberation was not the bottleneck,
and `xhigh` reaches DeepSeek as `high` anyway. Clearing a built-in row to NULL through the API is allowed
but is refilled at the next Inference API start, with the same value the class default would have given.

**API and UI.** `reasoningEffort` is on the agent DTOs. `PUT /api/v1/agents/{id}/reasoning-effort`
(`{ "reasoningEffort": "low" | null }`, 400 outside the vocabulary) has the same authority as
`PUT /{id}/inference-provider`: platform admin for a built-in, the organization's Owner/Admin for its own
custom agent, 404 across organizations. The agent detail page has a select beside the provider override.
The engine reads a changed value within the resolver cache TTL (`Inference:ProviderCacheSeconds`).

---

## 8. Run snapshots, Resume and Retry

Not an optimization switch: this is how a run keeps its own definition and how a stopped run is
carried on. It sits beside the lease and recovery sweeper rules in `CLAUDE.md`, "Workflow Execution
Liveness".

### The snapshot (kind 5)

At run start the engine clones a `Standard` workflow and its steps into a hidden
`workflow_definitions` row, `Kind = ExecutionSnapshot` (5), carrying the plain source name, and
runs from it. Migration `AddExecutionSnapshots` (engine) adds:

| Table | Column | Meaning |
|---|---|---|
| `workflow_executions` | `snapshot_definition_id` | FK to the snapshot, `ON DELETE SET NULL` |
| `workflow_executions` | `agent_snapshot_json` | The agents the run uses, recorded at start |
| `workflow_executions` | `pending_resume` | Set by Resume or auto-resume; tells the engine to reuse results |
| `workflow_executions` | `resume_count`, `auto_resume_count` | Manual and automatic resumes (auto is at most 1) |
| `workflow_step_results` | `superseded_at` | Stamped by Retry, and by a resume at its first miss; the row is kept for audit and billing and hidden from views |
| `workflow_definitions` | `snapshot_of_execution_id` | The run a snapshot belongs to |

The agent snapshot records `Name`, `AgentType`, `IsBuiltIn`, `OrganizationId`, `SystemPrompt`,
`ConfigJson`, `OutputSchemaJson`, `OutputSchemaName`, `AvailableToolsJson`, `GeneratesOutput`,
`ContextMode`, `ContextWindowSize`, `InferenceProviderId`, `AssignedSkillsJson`, `ReasoningEffort`.
The snapshot covers every agent the steps name: the step's own agent, a `Parallel` step's extra agents
(`ParallelAgentIdsJson`) and the custom seat and director agents of a room (`StepAgentReferences`).
`ExecutionSnapshotter` takes only platform built-ins (`IsBuiltIn`) and agents of the run's own
organization; any other id is left out. Provider rows, organization overrides and built-in providers
still resolve live.

The same rule applies at save time. `StepProviderOwnershipValidator` checks each id in
`ParallelAgentIdsJson` and each room seat or director id on a workflow save, and refuses an id that is
neither a platform built-in nor an agent of the project's organization. The message is the same
whether the agent belongs to another organization or does not exist.

The step cache (section 1) and the cost estimates key on the **source** workflow, mapped by
`StepOrder`, so a snapshot run hits the same cache entries as its source. Deleting an execution
removes its snapshot.

### Edits and deletes

`WorkflowStepReplacer` handles `PUT`, the assistant's `ApplyWorkflow`, MCP apply and the assistant's
delete tool. A step row still referenced by a legacy result (a run from before snapshots) moves to a
kind-5 archive with `snapshot_of_execution_id` null. `DELETE` returns `409 WORKFLOW_RUN_ACTIVE` while
a run of the workflow is `Queued` or `Running`; otherwise past runs are re-pointed to their snapshot
and survive under the source name. `FOR UPDATE` locks on the definition and step rows close the races.

### Resume

`POST /api/v1/projects/{projectId}/workflows/{id}/executions/{executionId}/resume`. Gates, in order:

1. Email verified and Edit access (`404`/`403`).
2. The run is in scope.
3. The workflow is not a stale design-profile copy, else `409 PROFILE_WORKFLOW_OUTDATED` (the same check as Execute and Retry).
4. `Failed` or `Cancelled`, else `409 RUN_NOT_RESUMABLE`.
5. An engine-taken snapshot plus an agent snapshot, else `409 RESUME_UNAVAILABLE` (a legacy run; use Retry).
6. The lease has lapsed, else `409 RUN_STILL_STOPPING`.
7. The shared run-start gate (`RunStartGate.RefuseAsync`), in this order: the plan's resolution cap
   (`402 PLAN_NOT_ENTITLED`), the plan's concurrent-run cap (`429`), the profile-build estimate gate
   (`IDesignBuildEstimateGate`: for a run of the `product-profile-from-code` template, `409 ESTIMATE_OVER_CAP`,
   `409 ESTIMATE_NOT_ACCEPTED` unless the body's `acceptEstimateCredits` is at least the high estimate, or
   `503 ESTIMATE_UNAVAILABLE`; any other workflow is untouched) and the credit preflight
   (`402 INSUFFICIENT_CREDITS`), priced from the first incomplete step on. Execute, Retry and Resume all run this same sequence after their authority and the
   stale-profile check, so Retry and Resume get the `429` too, before the `402`.
8. A conditional `UPDATE` to `Queued` with `pending_resume`, `resume_count + 1`, `StartedAt` kept; `202`.
   On Postgres it runs in a transaction that first takes the workflow's definition row `FOR SHARE`, so a
   concurrent delete of the workflow (which holds that row `FOR UPDATE`) and the re-queue serialize.

If the `UPDATE` matches nothing, `ExecutionResumability.LostRequeueAsync` re-reads the row and answers
`409` with a message that says what happened:

| Row now | Code | Message |
|---|---|---|
| Gone | `RUN_NOT_RESUMABLE` | This run no longer exists. Reload the page. |
| Filed under another workflow (the workflow was deleted) | `RUN_NOT_RESUMABLE` | This run's workflow was deleted while you asked. Reload the run and try again. |
| `Queued` or `Running` | `RUN_NOT_RESUMABLE` | This run was already queued again. |
| Lease still live | `RUN_STILL_STOPPING` | This run is still stopping. Try again in a moment. |
| Anything else | `RUN_NOT_RESUMABLE` | This run changed while you asked. Reload it and try again. |

The run page shows the server's message for `RUN_NOT_RESUMABLE` and `RUN_CHANGED`.

**The 402 estimate is an approximation.** `ResumeEstimateFrontier` prices the steps from the first
incomplete step onward (the first non-superseded result that is not `Completed` or `Skipped`, else the
step after the last result). It does not model storage objects that are gone (a reused render that
re-runs), loop-backs to earlier steps, or a `Conditional` that skipped steps before the frontier. It is
advisory: the engine's per-step credit gate still enforces credits for every live step.

`RUN_STILL_STOPPING` is used only while the lease is live: a renewal that landed after the route's check.

**Cursor semantics.** For each step the engine reuses the k-th `Completed` or `Skipped` result until
the first miss, then runs live from there. A reused result whose storage object is gone re-runs.
Containers (`ForEach`, `Parallel`, rooms, `ReviewLoop` iterations) are reused only whole. Reused rows
are not charged, not written to the step cache and not re-published. Superseded results are ignored
by the `VideoCompile` source-agent label.

**Superseded rows.** On the first miss the engine stamps `superseded_at` on the old attempt's rows from
that point on (`ResumeCursor.SupersedeReplacedAsync`). The rows stay for audit and billing. A repeat
resume therefore never matches them, so it cannot charge the same step twice, and the run's view no longer
shows the failed attempt. Rows before the miss are untouched. Every content read skips superseded rows
(the cursor, the `VideoCompile` source lookup, the composition store and the sandbox tools filter on
`superseded_at IS NULL`).

### Retry

The same execution row, the run's **own** snapshot, from step 1; later edits are never picked up.
Old results get `superseded_at`, in the same transaction as the re-queue. `StartedAt` is reset. Retry
gets the same gate order as Execute and Resume (resolution `402`, concurrency `429`, profile-build estimate gate, credit
`402`). A `Queued` or `Running` row is refused first with `400 RUN_ALREADY_ACTIVE`; `RUN_STILL_STOPPING`
applies to the other statuses while the lease is live. A retried legacy run whose snapshot is an archive
keeps its archive pointer (`snapshot_definition_id`) through the claim; it is replaced only by the save
that commits the new snapshot, so a snapshot that fails leaves the run pointing at its archive. Like Resume, it takes the workflow's definition row
`FOR SHARE` on Postgres, so it serializes with a delete of the workflow.

When Retry's conditional `UPDATE` matches nothing, it answers like Resume (table above) through
`LostRequeueAsync`, but with `409 RUN_CHANGED` in place of `RUN_NOT_RESUMABLE`. `RUN_STILL_STOPPING`
stays reserved for a live lease. `409 RETRY_UNAVAILABLE` for a deleted workflow's run that only has a legacy archive. The
execution DTO carries `canResume`, `resumeCount` and `workflowName`.

### Auto-resume

`ExecutionRecoverySweeper` re-queues a run lost through a lapsed lease **once** (`auto_resume_count`
0, engine-taken snapshot) instead of failing it, unless the run has a `VideoGenerate` ledger row
`Reserved`, `Submitted` or `Running`. Those runs are `Failed` ("Interrupted: engine instance lost")
and Resume is offered, so a paid clip is never bought twice automatically.

A **graceful engine shutdown** (a deploy, a restart) takes the same path. `WorkflowExecutionRunner.StopAsync`
cancels in-flight runs with `ExecutionCancellationRegistry.TryCancelForShutdown`, which records the shutdown
reason and marks the run as shutdown-interrupted (`IsShutdown`). `WorkflowExecutorService.MarkCancelledAsync` then
writes **nothing** for such a run (no `Cancelled`, no failure event): the step that was running is already closed
`Failed` (platform class, hold refunded), the row stays `Running`, `RunAsync` ends its lease, and the sweeper claims
it on its next pass (this replica after the restart, or another) and applies the rule above. Before this, the run
was written `Cancelled`, a terminal state the sweeper never reads, so a deploy used to lose a run that
auto-resume should have saved. A Stop, a plan stop or a lease loss recorded for the run always outranks a shutdown:
`TryCancel` clears the mark and the run ends `Cancelled` as before. A run that does not unwind inside the host's
shutdown window is the hard-kill case: its lease lapses and the same sweeper rule applies. On a box that has not
adopted blue-green deploys, the operator release script additionally refuses to restart while runs are active (`ci.md`, "Deploys
and running jobs").

**Drain mode.** On an adopted box the engine is not stopped at a deploy. The old engine gets a drain signal
(the file `/tmp/reelbolt-drain` in its container), stops its message receive endpoints, keeps publishing and finishes
the runs it holds; its status is in `/tmp/reelbolt-drain-status.json`. It is stopped when it reports `drained` or
after `REELBOLT_ENGINE_DRAIN_MAX` (8 hours); only then does the shutdown path above apply to a run still going.
See the operator runbook.

### Liveness at run start and during a run

- **Claim, then snapshot.** The `Queued` to `Running` claim is a conditional `UPDATE` (`WHERE status =
  Queued`), so of two deliveries only one starts the run. The snapshot is taken only after that claim, so a
  delivery that lost it never writes `snapshot_definition_id`.
- **A failed snapshot ends the run.** If the snapshot cannot be taken, the run ends `Failed` with
  `WorkflowExecutorService.SnapshotFailedMessage`, a platform-class message ("Interrupted: the platform
  could not take this run's snapshot ..."). It is no longer silently re-queued. A stop that arrives while
  the snapshot is taken ends the run `Cancelled` with the recorded reason.
- **Lease ownership checks.** Before each step, before a credit reserve, before a credit settle and
  before a `VideoGenerate` submit, the engine checks `lease_owner = self`
  (`ExecutionLeaseService.TryHoldAsync`, which also pushes the lease out a full window). If the sweeper
  recovered the run or another instance claimed it, the run is marked lost, no longer renewed and cancelled
  with a platform-class reason (`LeaseLostReason`). A run that lost its lease does not write a terminal
  status: before writing one the engine calls `StillOwnsRunAsync`, and only a confirmed loss skips the
  write. If the lease check itself throws (a database error), `StillOwnsRunAsync` logs a warning and
  returns true, so the terminal status is written anyway.

### `VideoGenerate` on a new attempt

See [`video-generation.md`](./video-generation.md#attempts-and-resume): an earlier attempt's
`Reserved` take with no `RemoteJobId` is `Abandoned` and re-submitted under a fresh key.

### Render keys

A render uploads to `projects/{p}/outputFiles/{executionId}/{stepResultId:N}/{fileName}`, one object
per review-loop iteration, so an earlier iteration's result keeps its own video. A `Revise` step passes
its step result id too (`CompositionRevisionRequest.StepResultId`), so a retried revision does not
overwrite the superseded attempt's render. With no step result
id the old key `projects/{p}/outputFiles/{executionId}/{fileName}` is used. Both keep the
`outputFiles/{executionId}/` prefix that the output verifiers check.

### Review scores

`review_scores.step_result_id` (engine migration `AddReviewScoreStepResult`, FK `ON DELETE SET NULL`) is
the `ReviewLoop` step result whose dispatch produced the score. `ReviewScoreLineage.Current` hides the
scores of a superseded dispatch (the run DTOs and the execution list use it); nothing is deleted. A row
written before the column existed has no id and falls back to a timestamp link: the run's result with the
latest `CompletedAt` at or before the score's `CreatedAt`.

### Step agents and organizations (A8)

A step's own agent must be a built-in or one of the project's organization's agents. `StepAgentAuthorization`
applies the rule (message `Invalid or unauthorized agent`) on the REST create and update, the
assistant and MCP `ProposeWorkflow` and `ApplyWorkflow` tools, and both the propose and apply of the
assistant controller. At run time a `Parallel` step loads only built-in and own-organization agents; a
stored reference to another organization's agent fails the step.

### Follow-ups

- A hold-row "uncapped" flag, so the double-failure sweep also settles a metered job above its cap.
- A filter override for `run-postgres-tests.sh`.

## 9. Structured output is checked at the step

An agent that declares an `OutputSchemaType` must produce an answer that **deserializes** into it, not
merely a JSON object. `AgentStepExecutor` runs `StructuredOutputValidator` (extract the last JSON
object, deserialize with tolerant options: case-insensitive, string enums, numbers from strings,
trailing commas; per-type options through `StructuredOutputOptions.For`). It is provider-agnostic:
DeepSeek, Gemini and `OpenAICompatible` do not enforce `ChatResponseFormat.ForJsonSchema<T>()`, so the
step check is the only guarantee.

- Invalid answer: **one repair turn** (`IReelBoltAgent.RunRepairAsync`) to the same model, with no tools
  (a render or sandbox write is never repeated), carrying the original input, the invalid answer
  (cut at 30,000 characters) and the deserializer error. Its tokens are added to the step so settle
  matches metering. An agent without a no-tools mode gets no repair turn.
- Still invalid: the step fails with `AGENT_OUTPUT_SCHEMA_INVALID` (`PlatformProviderError`, refunded)
  naming the agent, schema and error. The outer retry loop still applies.
- Exempt (JSON-object check only, justified in `StructuredOutputValidator`): `DirectorOutput`,
  `ReviewOutput`, `VideoReviewOutput`, `AnimationStrategyOutput`, `RenderManifestOutput`,
  `RemotionProjectBuildOutput`.
- `ScriptScene.OnScreenText` accepts a string and splits it on line breaks
  (`ScriptwriterOutputJson.Options`; the converter lives in the options, not on the property, so the
  generated JSON schema still types it as an array of strings).
- A step can mark a failure non-retryable (`StepExecutionResult.Retryable = false`); the retry loop
  records the classification and stops. Voiceover marks its input and configuration errors this way.
- Step cache: the changed prompts are part of the cache key, so they miss by themselves; no
  `StepCacheKeyInputs.SchemaVersion` bump.
- Engine/API mapping drift: `EngineApiModelDriftGuardTests` compares the provider type and nullability
  of every column both EF contexts map. It exists because the engine mapped
  `project_files.moderation_status` as an integer while the column is text, which made every full
  `ProjectFile` read in the engine throw (`ReadProjectFile` answered "Function failed" to every
  analyzer, and step-cache lookups failed quietly).

## Provider fingerprint: video enhancement (SchemaVersion v11)

`ResolvedProviderFingerprint` hashes one line per capability a step can reach. `VideoUpscale` and
`VideoOutpaint` joined that set with the Extend op (SchemaVersion v12: the fingerprint gained a line for every step, so every cached key moved). `FrameInterpolation` joined that set with the video-enhance vendor layer, so repointing the default upscaler or
interpolator row invalidates a cached result, and `StepCacheKeyInputs.SchemaVersion` went `v9` to `v11`: two more
lines in the hashed fingerprint change EVERY step's key, so every pre-v11 row orphans rather than hits (v10 is the
audio-cleanup initiative's, which adds `AudioIsolation` on its own branch and is not merged). An unresolved capability
is the line `<Capability>:-:-`, as before. The golden keys in `StepCacheKeyBuilderTests`,
`RunBriefEngineTests`, `RunStyleCacheKeyGoldenTests` and `EditRoomShotJudgingGuidanceTests` were re-recorded for the
version string only.

## Run brief in the step-cache key

`StepCacheKeyInputs.RunBrief` carries only the brief fields a step reads (an analyzer none, a writer language, length and voice, `VideoCompile` the length, `Voiceover` language and voice, `VideoAnalyze` the source language and chosen files) and is appended only when non-empty. A run with no brief keeps its key and `SchemaVersion` is not bumped. `DeterministicStepRevisions` for `VideoCompile`, `Voiceover` and `VideoAnalyze` carry a `run-brief-*` tag.
