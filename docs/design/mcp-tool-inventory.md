# Assistant tool inventory, authority map, and MCP duplication audit

**Type:** read-only audit. **Deliverable:** this file. No source file was modified.
**Audited revision:** `git rev-parse HEAD` → `9f6f9e0caeeda990e075fac523d52d875e9bc82c`
(commit `9f6f9e0ca`, 2026-10-11 03:30:04 +0200, *"feat: email notifications for job and file activity"*).
`git status --porcelain inference/` was **empty** at audit time: the whole `inference/` tree matched
HEAD, so every `file:line` below is stable at that commit.

**Current count.** This audit is a snapshot of the 59 tools registered at that commit and its
inventory below is not updated. The tools added after the snapshot (such as `ProposeObjectEffect` / `ApplyObjectEffect`, a paid pair published as `ObjectEffect`, and `ProposeEnhanceVideo` / `ApplyEnhanceVideo`, published as `EnhanceVideo`, and `GetMusicBeats`, a free read-only tool that returns a music file's beat grid) are described in [the platform assistant page](../platform-assistant-mcp.md#object-effects-blur-or-mask-an-object-by-name). The assistant now registers **122** tools in the chat and the MCP
catalog publishes **82**. The MCP surface is a projection of the registration: `WatchExecution` is
chat-only; the 40 `Propose*` / `Apply*` pairs collapse to 39 single MCP tools (for example
`ProposeWorkflow` / `ApplyWorkflow` become `SaveWorkflow`), and the upload pair stays two calls
because it is a binary protocol (`BeginUpload` read-only, `CompleteUpload` destructive). The 13
admin-gated registered tools publish as 9 MCP entries when enabled. Every catalog entry carries a
`title` and MCP `annotations` (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`,
`openWorldHint`), and every tool is exactly read-only or destructive. The in-app assistant keeps
every pair, its approval cards and auto mode; the old pair names are refused as unknown over MCP.
Tool history: `GetProjectReadiness`, `EstimateWorkflowRun` and `ProposeCreateFromTemplate` /
`ApplyCreateFromTemplate` are older; `ProposeStopExecution` / `ApplyStopExecution` and
`ProposeSavePronunciation` / `ApplySavePronunciation` are newer; `GetChangeOptions` /
`ProposeStepChange` / `ApplyStepChange` are the newest. `docs/platform-assistant-mcp.md` is current.
At the 77-tool point it registered: the 59 here, plus
the six design-system tools and one more picture tool (66), plus eleven for outputs (3), uploads and
imports (4), runs (2) and revisions (2). The current grouping and per-tool behaviour live in
[`../platform-assistant-mcp.md`](../platform-assistant-mcp.md#the-tool-surface).

**Concurrent work noted.** A teammate is rewriting `mcp/**`. At audit time `mcp/src/tools/*.ts` was
**unmodified from HEAD** (working-tree clean; `sha256` recorded in §4.1) while `mcp/package.json` and
`mcp/package-lock.json` had gained `@modelcontextprotocol/sdk@^1.32.1` (`git diff --stat mcp/` →
`package.json | 1 +`, `package-lock.json | 1201 ++++`). The MCP findings in §4 describe the
**HEAD revision** of `mcp/src/tools/`; re-check them if that directory changes.

---

## 1. The count, derived mechanically

### Command run

```bash
cd ~/src/reelbolt
for f in inference/src/ReelBolt.Inference.Api/Agents/Tools/*.cs; do
  n=$(grep -c 'AIFunctionFactory\.Create(' "$f")
  printf '%4d  %s\n' "$n" "$f"
done
echo "--- total ---"
grep -rho 'AIFunctionFactory\.Create(' inference/src/ReelBolt.Inference.Api/Agents/Tools/*.cs | wc -l
```

### Output (real, unedited)

```
  13  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantAdminTools.cs
   5  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantFileTools.cs
   3  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantKnowledgeTools.cs
  14  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantPictureTools.cs
   8  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantProjectTools.cs
   0  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantStepShapes.cs
   3  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantStyleTools.cs
   0  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantToolContext.cs
   0  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantToolProgress.cs
   7  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantToolProvider.cs
   6  inference/src/ReelBolt.Inference.Api/Agents/Tools/AssistantWorkflowTools.cs
--- total ---
59
```

Three independent corroborations that 59 is the registered count and that each name is unique:

| Check | Command | Result |
|---|---|---|
| Every registration has a `name:` | `grep -rn 'name: "' inference/src/ReelBolt.Inference.Api/Agents/Tools/*.cs \| wc -l` | `59` |
| No duplicate names | `grep -rho 'name: "[A-Za-z]*"' …/*.cs \| sort \| uniq -d` | *no output* |
| Unique names | `grep -rho 'name: "[A-Za-z]*"' …/*.cs \| sed 's/name: "//;s/"//' \| sort -u \| wc -l` | `59` |
| Runtime assertion in the suite | `inference/tests/ReelBolt.WorkflowEngine.Tests/Controllers/AssistantAuthorityTests.cs:403` | `registered.Count.Should().Be(59, "the provider registers exactly 59 tools at this commit")` |
| Runtime assertion for the gated subset | `AssistantAuthorityTests.cs:404` | `gated.Count.Should().Be(13, "exactly 13 registered tools are admin-gated at this commit")` |
| Closed-set name assertion | `Controllers/…/Agents/AssistantAgentToolScopeTests.cs:77-131` | `BeEquivalentTo` over exactly the same 59 names |

`AIFunctionFactory.Create` appears **nowhere else** in `inference/` except tests:
`grep -rn 'AIFunctionFactory\.Create(' inference/ --include=*.cs | grep -v '/Tools/'` returns only
`GraphicsRoomStepExecutorTests.cs:608-612` (5 fake tools) and two throwaway tools in
`ToolLoopCompactingChatClientTests.cs`. `AssistantToolProgress.WithProgress`
(`AssistantToolProgress.cs:114-126`) wraps each function in a `DelegatingAIFunction`; it creates no
new tools, so the count is not inflated or deflated by progress reporting.

### **Verdict on CLAUDE.md's "59"**

**CLAUDE.md is right: the assistant registers exactly 59 tools.** My count agrees with the CLAUDE.md
prose, with the frozen bridge contract (`docs/design/mcp-tool-bridge.md:17`), and with the two test
tripwires. There is no discrepancy to resolve. Note the composition CLAUDE.md states is also exact:
7 core + 8 project + 5 file + 6 workflow + 3 knowledge + 3 style + 14 picture + 13 gated = 59.

---

## 2. Complete inventory, by area

**Area = the surface that registers the tool** (`AssistantToolProvider.GetTools` inline block plus
the six per-area `GetTools` implementations plus `AssistantAdminTools`). The 13 admin-registered
tools split into **10 platform-gated** (`ICurrentUser.IsPlatformAdmin`) and **3
organization-gated** (`OrgAuthority.IsOrganizationAdmin` over the live `ICurrentUser.OrgRole`).

The gate is the **first executed statement** of each gated body
(`AssistantAdminTools.cs:42-51`); the line in the "Authority" column is that gate.

### 2.1 Core — 7 tools (`AssistantToolProvider.cs`)

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 1 | `ListAgents` | `AssistantToolProvider.cs:167` | No | Project-in-context `View` via `ResolveOwnedProject` (`:259-267`, called `:275`) |
| 2 | `ListStepSchemas` | `AssistantToolProvider.cs:171` | No | Authenticated only (JWT `[Authorize]`); no project, no role (`:312-316`) |
| 3 | `ListProjectFiles` | `AssistantToolProvider.cs:175` | No | Project `View` (`:288`) |
| 4 | `ValidateWorkflow` | `AssistantToolProvider.cs:179` | No | Project `View` (`:320`) + advisory plan gate (`:326`) |
| 5 | `ProposeWorkflow` | `AssistantToolProvider.cs:184` | No (mints a diff/token only) | Project `Edit` (`:333`) + workflow must belong to that project (`:343`) |
| 6 | `ApplyWorkflow` | `AssistantToolProvider.cs:189` | **Yes** (insert/update `workflow_definitions` + `workflow_steps`, `:399`, `:403-409`) | Project `Edit` (`:355`) + legacy unkeyed confirmation-token equality (`:370`) + `StepConfigSaveValidator` (`:373`) + `StepProviderOwnershipValidator` A10 (`:376`) + plan gate (`:380`) |
| 7 | `ExecuteWorkflow` | `AssistantToolProvider.cs:194` | **Yes** — creates the `workflow_execution` row and publishes the run (`:510`); the run it starts then spends credits step by step in the engine | `EmailVerified` (`:418`) + Project `Edit` (`:421`) + credit preflight B7 (`:430-451`) + style/format resolution (`:456`) + paid-clips decision (`:461-495`) + rolling-hour rate limit `IAssistantExecutionGuard.TryAcquireAsync` (`:503`) + plan gate (`:512-519`) |

### 2.2 Project — 8 tools (`AssistantProjectTools.cs`)

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 8 | `ListProjects` | `AssistantProjectTools.cs:83` | No | Live `_currentUser.OrganizationId` + `AuthorizeOrganization(View)` (`:140-142`) |
| 9 | `GetProject` | `AssistantProjectTools.cs:87` | No | Project `View` via `ResolveProject` (`:163`, helper `:375-391`) |
| 10 | `ProposeCreateProject` | `AssistantProjectTools.cs:91` | No | `name` non-blank only (`:182`); user id from `AssistantToolContext` |
| 11 | `ApplyCreateProject` | `AssistantProjectTools.cs:96` | **Yes** (inserts `projects`, may ensure the `application_users` row, provisions templates; `:239-249`) | Keyed confirmation token (`:209`) + `ActiveOrganization.Require(_currentUser)` (`:233`) |
| 12 | `ProposeUpdateProject` | `AssistantProjectTools.cs:100` | No | Project `Edit` (`:268`) |
| 13 | `ApplyUpdateProject` | `AssistantProjectTools.cs:105` | **Yes** (`:307-310`) | Project `Edit` (`:296`) + token (`:304`) |
| 14 | `ProposeDeleteProject` | `AssistantProjectTools.cs:109` | No | Project `Delete` (`:323`) |
| +2 | `ProposeSavePronunciation` / `ApplySavePronunciation` | `AssistantProjectTools.cs` | Apply only (upserts `project_pronunciation_entries`) | Project `Edit` + token bound to project, normalized term, spelling, case flag; 200-entry cap |
| 15 | `ApplyDeleteProject` | `AssistantProjectTools.cs:113` | **Yes**, destructive (`:358-359`) | Project `Delete` (`:346`) + token bound to the row's **current** id **and name** (`:354-355`) |

### 2.3 File — 5 tools (`AssistantFileTools.cs`)

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 16 | `ReadProjectFile` | `AssistantFileTools.cs:99` | No | Project `View` (`:158`) + text-mime predicate (`:169`) |
| 17 | `ProposeWriteProjectFile` | `AssistantFileTools.cs:104` | No | Project `Edit` (`:207`) |
| 18 | `ApplyWriteProjectFile` | `AssistantFileTools.cs:109` | **Yes** (object-storage upload + `project_files` row + indexing publish + summarization enqueue; `:276-324`) | Project `Edit` (`:246`) + token over the content SHA-256 (`:258`, payload `:128-143`) |
| 19 | `ProposeDeleteProjectFile` | `AssistantFileTools.cs:113` | No | Project `Edit` (`:340`) |
| 20 | `ApplyDeleteProjectFile` | `AssistantFileTools.cs:118` | **Yes**, destructive (storage delete + row delete; `:382-384`) | Project `Edit` (`:368`) + token over id + current name (`:378-379`) |

### 2.4 Workflow — 6 tools (`AssistantWorkflowTools.cs`)

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 21 | `ListWorkflows` | `AssistantWorkflowTools.cs:64` | No | Project `View` (`:99`) |
| 22 | `GetWorkflow` | `AssistantWorkflowTools.cs:68` | No | Project `View` (`:122`) + workflow must be `Standard` kind and belong to the project (`:133`) |
| 23 | `ListWorkflowExecutions` | `AssistantWorkflowTools.cs:72` | No | Project `View` (`:165`) |
| 24 | `GetWorkflowExecution` | `AssistantWorkflowTools.cs:76` | No | Project `View` (`:198`) + execution must belong to the project (`:207`) |
| 25 | `ProposeDeleteWorkflow` | `AssistantWorkflowTools.cs:80` | No | Project `Edit` (`:246`) |
| 26 | `ApplyDeleteWorkflow` | `AssistantWorkflowTools.cs:84` | **Yes**, destructive (`:286-287`) | Project `Edit` (`:271`) + token over id + current name (`:282-283`) |

### 2.5 Knowledge — 3 tools (`AssistantKnowledgeTools.cs`) — all read-only

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 27 | `SearchPlatformDocs` | `AssistantKnowledgeTools.cs:95` | No | Authenticated only; no project needed (`:108`) |
| 28 | `ListWorkflowTemplates` | `AssistantKnowledgeTools.cs:99` | No | Authenticated only (`:165`) |
| 29 | `GetWorkflowTemplate` | `AssistantKnowledgeTools.cs:103` | No | Authenticated only; reads built-in agent rows (`:185-217`) |

### 2.6 Style — 3 tools (`AssistantStyleTools.cs`)

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 30 | `ListStyles` | `AssistantStyleTools.cs:60` | No | Owner-scoped: `EditingStyles.OwnerId == context.UserId` (`:100`); a project pin is checked `View` (`:95`) |
| 31 | `ProposeSaveStyle` | `AssistantStyleTools.cs:65` | No | Owner-scoped (editing an existing row: `OwnerId == context.UserId`, `:207`); `View` when pinning (`:219`) |
| 32 | `ApplySaveStyle` | `AssistantStyleTools.cs:70` | **Yes** (`:180-188`) | Same ownership re-check + token (`:165`) + `EditingStyleRules.MaxStylesPerUser` cap (`:176`) |

### 2.7 Picture — 14 tools (`AssistantPictureTools.cs`)

Always registered (the set is closed and static); each answers `no picture open` with no
`AssistantToolContext.ImageDocumentId`. Every body's **first statement** is `ResolveAsync`
(`:264-283`), which re-resolves the project through `IProjectAccessService` and then the document
inside it; `Edit` on a `View`-only project answers `you can view this picture but not change it`
(`:272-276`). All applies delegate to `IImageDocumentCommands` — the editor's own controller
actions — so validation, revisions and metering stay the editor's.

| # | Tool | `name:` line | Writes? | Authority checked |
|---|---|---|---|---|
| 33 | `DescribePictureDocument` | `AssistantPictureTools.cs:168` | **No, but see note** — `_graphics.SettleAsync` (`:295`) can promote a *completed background* graphic into a layer. Read-only from the caller's side. | Project `View` (`:289`) |
| 34 | `ListPictureTools` | `AssistantPictureTools.cs:172` | No | Project `View` (`:391`) |
| 35 | `ProposeAddTextLayer` | `AssistantPictureTools.cs:178` | No | Project `Edit` (`:429`) |
| 36 | `ApplyAddTextLayer` | `AssistantPictureTools.cs:183` | **Yes** via `_commands.CreateTextLayerAsync` (`:460`) | Project `Edit` (`:449`) + token |
| 37 | `ProposeEditPictureLayers` | `AssistantPictureTools.cs:188` | No | Project `Edit` (`:507`) |
| 38 | `ApplyEditPictureLayers` | `AssistantPictureTools.cs:193` | **Yes** via `_commands.UpdateLayerAsync` (`:543`) | Project `Edit` (`:529`) + token binding each layer id + current name |
| 39 | `ProposeDeletePictureLayers` | `AssistantPictureTools.cs:198` | No | Project `Edit` (`:710`) |
| 40 | `ApplyDeletePictureLayers` | `AssistantPictureTools.cs:203` | **Yes**, destructive, via `_commands.DeleteLayerAsync` (`:746`) | Project `Edit` (`:732`) + token |
| 41 | `ProposeChangePictureCanvas` | `AssistantPictureTools.cs:209` | No | Project `Edit` (`:785`) |
| 42 | `ApplyChangePictureCanvas` | `AssistantPictureTools.cs:214` | **Yes** via `_commands.UpdateDocumentAsync` (`:816`) | Project `Edit` (`:805`) + token |
| 43 | `ProposeRunPictureTool` | `AssistantPictureTools.cs:220` | No (returns estimated cost) | Project `Edit` (`:1080`) |
| 44 | `ApplyRunPictureTool` | `AssistantPictureTools.cs:225` | **Yes — paid** (spends credits; `_commands.SegmentSubjectAsync`/`StoreMaskAsync`/`RunOperationAsync`, `:1121-1145`) | Project `Edit` (`:1105`) + token |
| 45 | `ProposeDesignGraphic` | `AssistantPictureTools.cs:231` | No (plans + estimates; `_graphics.PlanAsync`, `:1208`) | Project `Edit` (`:1201`) |
| 46 | `ApplyDesignGraphic` | `AssistantPictureTools.cs:236` | **Yes — paid**, background (`_graphics.RequestAsync`, `:1247`) | Project `Edit` (`:1230`) + token |

### 2.8 Admin — 13 tools (`AssistantAdminTools.cs`): 10 platform-gated + 3 organization-gated

| # | Tool | `name:` line | Gate | Gate line | Writes? | Authority checked |
|---|---|---|---|---|---|---|
| 47 | `ListInferenceProviders` | `AssistantAdminTools.cs:153` | Platform | `:395` | No | `ICurrentUser.IsPlatformAdmin`; platform-managed rows only (`PlatformProviders`, `:238-239`) |
| 48 | `TestInferenceProvider` | `AssistantAdminTools.cs:158` | Platform | `:357` | **Yes — `lastTest*` columns only** (`InferenceProviderTester.cs:727-730`), and sends one small real provider request | `IsPlatformAdmin`; platform-managed rows only (`:368`) |
| 49 | `ProposeCreateInferenceProvider` | `AssistantAdminTools.cs:165` | Platform | `:434` | No | `IsPlatformAdmin` |
| 50 | `ApplyCreateInferenceProvider` | `AssistantAdminTools.cs:173` | Platform | `:477` | **Yes** (`:517-518`) | `IsPlatformAdmin` + keyed token; key fingerprinted, never echoed (`:251-260`) |
| 51 | `ProposeUpdateInferenceProvider` | `AssistantAdminTools.cs:180` | Platform | `:539` | No | `IsPlatformAdmin` |
| 52 | `ApplyUpdateInferenceProvider` | `AssistantAdminTools.cs:188` | Platform | `:585` | **Yes** (`:663`) | `IsPlatformAdmin` + token |
| 53 | `ProposeDeleteInferenceProvider` | `AssistantAdminTools.cs:193` | Platform | `:682` | No | `IsPlatformAdmin`; default-for-capability cannot be deleted |
| 54 | `ApplyDeleteInferenceProvider` | `AssistantAdminTools.cs:198` | Platform | `:710` | **Yes**, destructive (`:729`) | `IsPlatformAdmin` + token over id + current name |
| 55 | `ListSkills` | `AssistantAdminTools.cs:203` | **Organization** | `:749` | No | `OrganizationId is not null && OrgAuthority.IsOrganizationAdmin(OrgRole)` |
| 56 | `ProposeAssignAgentSkills` | `AssistantAdminTools.cs:208` | **Organization** | `:779-780` | No | Org admin; built-in agents refused; another org's agent → `agent not found` (`:788`) |
| 57 | `ApplyAssignAgentSkills` | `AssistantAdminTools.cs:214` | **Organization** | `:824` | **Yes** (`:851`) | Org admin + token bound to the workspace that proposed it |
| 58 | `ListAgentsAdmin` | `AssistantAdminTools.cs:219` | Platform | `:880` | No | `IsPlatformAdmin` |
| 59 | `GetDecisionCalibration` | `AssistantAdminTools.cs:225` | Platform | `:323` | No | `IsPlatformAdmin` |

**Totals:** 59 tools. **20** can touch the database or spend money (7 core→2, project→3, file→2,
workflow→1, style→1, picture→6, admin→5), of which 19 are true state mutations behind a
propose/apply token or the execute guard, and one (`TestInferenceProvider`) writes only the
`lastTest*` audit columns. **39** are pure reads.

---

## 3. The duplication check (the user's explicit question)

**Question:** does the assistant's own path call the MCP server, call the
`/api/v1/projects/{projectId}/workflows/assistant/{validate,propose,apply}` routes, or otherwise
share a tool implementation with `mcp/**`?

### 3.1 Evidence

**(a) The assistant builds its tools in-process; it never makes an HTTP call to anything.**

```bash
grep -n 'GetTools\|AssistantToolProvider\|new AssistantToolContext\|HttpClient' \
  inference/src/ReelBolt.Inference.Api/Controllers/AssistantChatController.cs
```
```
58:    /// The assistant is backed by <see cref="AssistantToolProvider"/>, which composes the shipped
320:        AssistantToolProvider toolProvider = CreateToolProvider(diffService, executionService, executionGuard);
325:        var toolContext = new AssistantToolContext(userId, projectId, workflowId, imageDocumentId);
642:    protected virtual AssistantToolProvider CreateToolProvider(
```

`AssistantChatController.cs:315-325` resolves `IWorkflowEditDiffService`, `IWorkflowExecutionService`
and `IAssistantExecutionGuard` from the **request's own DI scope** and hands them to a
`new`-constructed `AssistantToolProvider`; it never visits `localhost`, the MCP URL, or its own
controllers. The only `HttpClient` anywhere in the assistant services is
`Services/Assistant/DecisionCalibrationClient.cs:26-52` (the `workflow-engine-calibration` client
behind the read-only `GetDecisionCalibration` tool) — unrelated to MCP.

**(b) No `mcp` code reference exists in `inference/**`.**

```bash
grep -rin 'mcp' inference/ --include=*.cs --include=*.json --include=*.csproj
```

Every hit is a comment, a test comment, a doc-string, a pronunciation-dictionary entry (`["mcp"] =
"ɛm siː piː"`), the Anthropic SDK's unrelated `GlobalMcpToolCallApprovalPolicy` in a `.csproj`
comment, or the enum member `ExecutionInitiator.Mcp`
(`ReelBolt.Shared/Data/Models/ExecutionInitiator.cs:15`). **There is no MCP client, no MCP URL, no
`REELBOLT_MCP_TOKEN`, and no MCP config key** anywhere in `inference/**`
(`grep -rin 'mcp' …/appsettings*.json` → no matches).

**(c) `ExecutionInitiator.Mcp` is dead code.** `grep -rn 'ExecutionInitiator.Mcp' inference/
--include=*.cs` → **no matches**. So nothing in the codebase even records a run as MCP-initiated,
let alone routes through the MCP server.

**(d) The assistant's `/assistant/{validate,propose,apply}` routes are declared but never called
from the assistant path.**

```bash
grep -rn 'workflows/assistant/' inference/ --include=*.{cs,ts,tsx}   # → no matches
```

The routes are declared in `Controllers/WorkflowAssistantController.cs:63,79,105` (plus
`execute` at `:234`). The only consumers outside `mcp/**` are the **dashboard UI**:
`web/lib/api/workflow-assistant.ts:18,32,55` (and its type file `web/lib/types/workflow-assistant.ts:31`).
The assistant's C# path calls none of them.

**(e) The MCP TS tools call REST routes, not the assistant's in-process tools.**

`mcp/src/server.ts:32-40` registers exactly 7 TS tools. Their targets:

| MCP tool | Transport today | Route (file:line) |
|---|---|---|
| `list_agents` | `GET` | `/api/v1/agents` → `AgentsController.cs:15,37` |
| `list_step_schemas` | `GET` | `/api/v1/workflow-step-schemas` → `WorkflowStepSchemasController.cs:14,32` |
| `list_project_files` | `GET` | `/api/v1/projects/{projectId}/files` → `ProjectFilesController.cs:25,115` |
| `validate_workflow` | `POST` | `…/workflows/assistant/validate` → `WorkflowAssistantController.cs:63` (`workflowTools.ts:66`) |
| `propose_workflow` | `POST` | `…/workflows/assistant/propose` → `WorkflowAssistantController.cs:79` (`workflowTools.ts:109`) |
| `apply_workflow` | `POST` | `…/workflows/assistant/apply` → `WorkflowAssistantController.cs:105` (`workflowTools.ts:152`) |
| `execute_workflow` | `POST` | `…/workflows/{workflowId}/assistant/execute` → `WorkflowAssistantController.cs:234` (`workflowTools.ts:197`) |

### 3.2 What *is* actually shared (the honest nuance)

The assistant tool bodies and the routes those 7 TS tools call **do share the underlying domain
services**, so the business logic is not forked today:

| Behaviour | Assistant in-process tool | REST route the MCP tool calls | Shared implementation |
|---|---|---|---|
| Workflow validate/propose/apply | `AssistantToolProvider.cs:318-411` | `WorkflowAssistantController.cs:73,102,131` | `IWorkflowEditDiffService` (both construct/DI the same interface) |
| Assistant run rate limit | `AssistantToolProvider.cs:503` | `WorkflowAssistantController.cs:288` | `IAssistantExecutionGuard` |
| Step schemas | `AssistantToolProvider.cs:314` (`SchemaExporter.GetAllSchemas()`) | `WorkflowStepSchemasController.cs:34` (`_exporter.GetAllSchemas()`) | `StepConfigSchemaExporter` (registered singleton) |
| Agent listing | `AssistantToolProvider.cs:279-281` (inline LINQ) | `AgentsController.cs:47-51` (inline LINQ) | **None** — the predicate `IsBuiltIn \|\| OrganizationId == …` is written twice |
| Project file listing | `AssistantToolProvider.cs:292-308` (inline LINQ + anonymous type) | `ProjectFilesController.cs:116+` (`ProjectFileResponse`) | **None** — the same 10 fields are projected twice (and a third time in `discoverTools.ts:129-140`) |

So the exact duplication that exists is:
1. **Between `mcp/**` and the REST controllers** — the TS tools re-implement the *client* of three
   read routes and four workflow routes, and two of those behaviours (`list_agents`,
   `list_project_files`) are also re-implemented a third time inside the C# assistant tools.
2. **Inside `inference/`** — `ListAgents`/`ListProjectFiles` are hand-written twice (once as an
   assistant tool body, once as a controller action). That is **pre-existing drift risk inside the
   API**, not a consequence of MCP, and it is not "the same tool maintained twice" in the sense the
   user asked about: both copies live in C# behind the same `IProjectAccessService`.

### 3.3 Verdict

> **The assistant is already internal-only. It does not call the MCP server, does not call the
> `/assistant/{validate,propose,apply}` routes, and shares no tool implementation with `mcp/**`.**
> There is **no doubling of the assistant's tools**: the assistant has one C# implementation of each
> of its 59 tools, and `mcp/**` has 7 separate TS tools that call REST routes. The only duplication
> in the repository is `mcp/**`'s 7 TS tools versus the REST routes they call — which is exactly the
> duplication the bridge removes — plus a pre-existing in-API copy of two read queries.

**Does the assistant need migrating to internal-only tooling?** No. It is already internal-only
(`AssistantChatController.cs:315-325`): no transport, no route, no second implementation. The
migration the frozen contract describes is on the **MCP** side (replace 7 TS implementations with
`tool-catalog`/`tool-call` proxies), and the constraint that follows for the bridge is that
**`GET /api/v1/assistant/tool-catalog` / `POST /api/v1/assistant/tool-call` must serve the same
`AssistantToolProvider.GetTools(...)` list, not a new one** — otherwise the bridge would *create*
the duplication it exists to remove. Bridge §3 already states this ("The set is exactly what
`AssistantToolProvider.GetTools(...)` registers"); this audit is the evidence that the C# side is
the single source of truth it can be pointed at.

---

## 4. The 7 MCP tools: routes today, and what the bridge requires

### 4.1 Revision audited

```bash
git rev-parse HEAD                    # 9f6f9e0caeeda990e075fac523d52d875e9bc82c
git status --porcelain mcp/           #  M mcp/package-lock.json ; M mcp/package.json
stat -c '%y %n' mcp/src/tools/*.ts
sha256sum mcp/src/tools/*.ts mcp/src/server.ts
```
```
2026-10-10 00:43:55 mcp/src/tools/discoverTools.test.ts
2026-10-04 02:05:47 mcp/src/tools/discoverTools.ts
2026-10-10 00:43:55 mcp/src/tools/workflowTools.test.ts
2026-10-04 02:05:47 mcp/src/tools/workflowTools.ts
f3adc899…  mcp/src/tools/discoverTools.ts
e672ae18…  mcp/src/tools/workflowTools.ts
28a4ddcd…  mcp/src/server.ts
```
`mcp/src/tools/*.ts` was **unmodified from HEAD** during this audit.

### 4.2 Per-tool migration

| # | MCP tool (name) | REST route(s) called today | Bridge target tool → `arguments` | `context` the bridge must carry | What the move requires |
|---|---|---|---|---|---|
| 1 | `list_agents` (`discoverTools.ts:20`) | `GET /api/v1/agents` (`discoverTools.ts:31`) | `ListAgents` → `{}` | `projectId` (**new requirement**) | The C# tool scopes agents by *the project in context* (`AssistantToolProvider.cs:279-281`); the REST route scopes by the JWT's active org. The MCP schema must gain the bridge `context` object so the caller can name the project; today `list_agents` has `"properties": {}` (`discoverTools.ts:22-25`). |
| 2 | `list_step_schemas` (`discoverTools.ts:52`) | `GET /api/v1/workflow-step-schemas` (`discoverTools.ts:63-64`) | `ListStepSchemas` → `{}` | none | Drop the dedicated route call; both paths already use the same `StepConfigSchemaExporter`. Payload differs in shape (`WorkflowStepSchemaDto` vs the raw exporter dictionary) — the bridge returns the assistant tool's own JSON, a deliberate, documented behaviour change for MCP clients. |
| 3 | `list_project_files` (`discoverTools.ts:97`) | `GET /api/v1/projects/{projectId}/files` (`discoverTools.ts:113-114`) | `ListProjectFiles` → `{}` | `projectId` | Move `projectId` from a required top-level argument into `context.projectId` (bridge §4). Follow-up: the bridge's `ListProjectFiles` also does **not** return `ProjectFileResponse`'s extra fields, so the TS field-by-field mapping at `discoverTools.ts:129-140` becomes unnecessary. |
| 4 | `validate_workflow` (`workflowTools.ts:47`) | `POST …/workflows/assistant/validate` (`workflowTools.ts:66`) | `ValidateWorkflow` → `{ "steps": [...] }` | `projectId` | **Argument rename**: the REST `ProposeWorkflowRequest`-style wrapper is gone; the C# tool's parameter is `steps` (`AssistantToolProvider.cs:178`), so `stepConfigs` → `steps`. See the body-shape defect in §5.1. Also: a validation failure is a **result**, so HTTP-status-based error handling must go. |
| 5 | `propose_workflow` (`workflowTools.ts:90`) | `POST …/assistant/propose` (`workflowTools.ts:109`) | `ProposeWorkflow` → `{ "workflowId", "name", "steps" }` | `projectId` | Flatten the single `request` object (`workflowTools.ts:92-98`) into the tool's own three argument names; drop `projectId` from `arguments`. |
| 6 | `apply_workflow` (`workflowTools.ts:133`) | `POST …/assistant/apply` (`workflowTools.ts:152`) | `ApplyWorkflow` → `{ "workflowId", "name", "steps", "confirmationToken" }` | `projectId` | Flatten `request`; **error-model change**: a stale token is HTTP `409` today (throwing at `workflowTools.ts:163-165`) but becomes a tool-level JSON result with `isError:false` (bridge §3). The MCP handler must stop throwing on non-2xx once it no longer speaks HTTP to the assistant. |
| 7 | `execute_workflow` (`workflowTools.ts:176`) | `POST …/workflows/{workflowId}/assistant/execute` (`workflowTools.ts:197`) | `ExecuteWorkflow` → `{ "workflowId", "userRequest", "regenerateVideoClips", "styleId", "stylePresetKey", "outputFormat" }` | `projectId` | Flatten `request` into the six named arguments (`AssistantToolProvider.cs:191-195`). Today `201 Created`→ JSON; the bridge returns `content[].text`. A `429` rate-limit refusal and a plan-gate refusal likewise become results, not errors. |

### 4.3 Cross-cutting requirements for all 7

1. Every bridged tool needs the single optional `context` wrapper property from bridge §4, and the
   guard test **"no bridged tool declares a property named `context`"** must pass. Verified safe at
   HEAD: none of the 59 C# registrations has a parameter named `context` — the only `context`
   identifiers are the `AssistantToolContext` parameter of `GetTools`, not a tool argument
   (`grep -rn 'name: "context"' …/Tools/*.cs` → no matches).
2. `projectId` disappears from `arguments` and moves into `context` for tools 1, 3, 4, 5, 6, 7.
   Tool 2 needs no context.
3. The status-code-driven error handling in all 7 handlers (`if (!response.ok) throw …`) is
   incompatible with bridge §3's "tool-level refusals are results". The bridge's `tool-call`
   returns `isError:false` for a refusal and reserves `isError:true` for transport failure, so the
   MCP layer has nothing to throw on except a genuine API/transport failure.
4. `list_agents` and `list_project_files` today return the REST DTO shape; the bridge returns the
   assistant tool's shape. Both are semantically equivalent for `list_project_files` (same 10
   fields) but **not** for `list_agents` (the assistant returns only `{Id, Name, Description,
   AgentType}` — `AssistantToolProvider.cs:282` — versus `AgentDefinitionResponse` with provider
   and skill detail). This is a client-visible contract change and must be called out in the MCP
   tool descriptions.

---

## 5. Discrepancies, risks and what this audit could not determine

### 5.1 Confirmed static defect: `validate_workflow`'s request body does not match the route

The MCP handler sends `JSON.stringify({ stepConfigs })` (`mcp/src/tools/workflowTools.ts:73`), but
the route binds `[FromBody] IReadOnlyList<CreateWorkflowStepRequest> steps`
(`Controllers/WorkflowAssistantController.cs:64-67`, DTO at `Controllers/Dto/ProjectDtos.cs:198`), i.e.
a **bare JSON array**. A JSON object cannot deserialize into `IReadOnlyList<T>`, so this call should
return `400` against a live API. The unit test does not catch it because it asserts only the URL
(`mcp/src/tools/workflowTools.test.ts:119-120`) with a stubbed `fetch`. **I did not run the API**, so
this is a static-reading finding, not a reproduced failure — but the mismatch is unambiguous in the
source. The bridge removes it: the argument is named `steps` and forwarded verbatim.

### 5.2 `DescribePictureDocument` is labelled read-only but can write

`AssistantPictureTools.cs:295` calls `_graphics.SettleAsync(...)`, which promotes a *completed*
background graphic into a layer (`Services/Graphics/GraphicDesignService.cs:388-425`). It writes
only the deterministic result of a design the caller already started, and it is the same write the
editor's own list performs — but a catalogue that publishes a per-tool "writes" flag over the bridge
should not label this one a pure read.

### 5.3 Areas where the bridge must not become a second source of truth

The bridge's `tool-catalog` must be generated from `AssistantToolProvider.GetTools(...)`
descriptions and schemas at call time (bridge §3). If it is ever materialised into a checked-in
list, this audit's 59-name inventory becomes a *third* copy alongside
`AssistantAgentToolScopeTests.cs:77-131` and `AssistantAuthorityTests.cs:403-404`, and the
duplication the user asked us to rule out would reappear one layer up.

### 5.4 Not determined

- **Runtime behaviour of every tool** — this is a static audit. No API, database, or assistant
  session was started; the write/read column is derived from the presence of `SaveChanges*`/
  `ExecuteDelete`/command-service calls in each tool body, not from observed side effects.
- **The teammate's in-flight rewrite of `mcp/src/tools/`** — the §4 mapping describes HEAD. If that
  directory changes, the route-to-tool table must be re-derived against the new revision.
- **The `validate_workflow` 400** — reasoned from source, not reproduced (no live API available to
  this read-only audit).
