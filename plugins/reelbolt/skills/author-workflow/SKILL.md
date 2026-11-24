---
description: Build or change a ReelBolt workflow through the ReelBolt MCP tools - prefer a built-in recipe (template) over a custom graph; validate, then save in one call after the user confirms, then run. Use when asked to build, edit, copy or run a ReelBolt workflow.
---

# Author and run a ReelBolt workflow

Build a workflow from a description, or edit an existing one, with the ReelBolt MCP tools. The
server is registered as `reelbolt`, so in this session its tools are named
`mcp__plugin_reelbolt_reelbolt__<ToolName>`.

Every call can carry the page context in a single optional `context` argument (see the
`inspect-run` skill). Populate at least `context.projectId`; project-scoped tools also accept a
`projectId` argument.

## Recipe first, custom graph last

Decide in this order and stop at the first that fits:

1. **Reuse.** `ListWorkflows` / `GetProjectReadiness` (its workflows list names the recipe each came
   from). If the project already has a workflow from the right recipe, run that one.
2. **Create from a recipe.** `ListWorkflowTemplates` with `goal` set to the user's goal (best
   matches first; each carries `forGoals`, `needs`, `costClass`, and `satisfied` / `missing` for the
   project). `GetWorkflowTemplate` shows its steps, `briefBindings` and `availablePatches`.
3. **Recipe plus patches.** Add only the closed patch ids (`swapToRoom`, `addVoiceover`,
   `addCaptions`, `addMusic`, `addSfx`, `addColorGrade`, `addGraphics`). A patch that does not fit
   is refused with a reason; do not work around it.
4. **Custom graph.** Only when no recipe plus patch can do it. Use the custom flow below.
5. **Decline or ask.** If the project lacks what the recipe needs, say so.

Before creating or running, price it in one line: `EstimateWorkflowRun` (`workflowId` or
`templateKey`, optional `brief`) is free and returns a cost class, a usual range and the paid items.

Create-from-recipe flow:

1. `GetProjectReadiness` once: files, design system, voices, available AI capabilities, plan limits.
2. Show the user the recipe, the patches, the brief fields you will set and the one-line price, and
   get an explicit yes.
3. `CreateFromTemplate` (`templateKey`, optional `brief`, `patches`, `name`). It creates the workflow
   in this one call and returns `workflowId`, `workflowName`, `patchesApplied`, `stepCount`,
   `costClass`, `brief`, `needsBeforeRunning` and a `hint`.
4. Relay everything in `needsBeforeRunning` (a missing provider, voice, file, design system, or a
   consent only they can give in the app); it does not block the save but the run needs it. It
   starts no run.

Read [`reference.md`](./reference.md) for argument lists, the brief fields, lint rules, patching and
cloning, and the custom-graph details.

## Rules that are not optional

1. **Confirm before every write.** MCP write tools are single calls (`CreateFromTemplate`,
   `SaveWorkflow`, `WriteProjectFile`, `CreateProject`, …). Never call one without showing the user
   what it will do and getting an explicit yes. There is no separate propose step to lean on.
2. **Never send a `confirmationToken`.** MCP does not accept one: the server mints and consumes it
   inside the one call. A write that refuses answers with a normal result (`isError:false`) saying
   why; fix the arguments rather than retrying the same call.
3. **Lint errors are final.** A save with workflow errors is refused with `findings`. Fix the graph
   or pick another recipe; do not retry the same call.
4. **Footage-sharing consent is the customer's.** Never set `allowSourceMediaEgress` yourself. See
   the `consent-and-budget` skill.
5. **Read back before running.** After a save, confirm with `GetWorkflow` that the stored steps are
   what you intended.
6. **Ask before running.** Confirm style (`styleId` or `stylePresetKey`, never both), `outputFormat`
   and `outputResolution` with the user. If the workflow has a `VideoGenerate` step, ask whether to
   reuse previous clips (free) or regenerate (paid) and pass `regenerateVideoClips`.
7. **Fill an app's fields, never edit its steps, to change one run.** Where the workspace has app
   inputs, `GetWorkflow` lists them under `appInputs` (each field's `id`, type, label, default, choices
   and, for a model field, `modelOptions`). Pass the values in `ExecuteWorkflow`'s `brief.inputs`
   (`{ "<id>": value }`; a model field takes a `modelOptions` id).
8. **Do not block on a run.** Follow the `run-and-watch` skill.

## Custom graphs and small edits

- Custom graph: `ListAgents`, then `GetStepCatalog` (one step type or all: purpose, cost class,
  needs, what it produces) and `ListStepSchemas` before writing any config. Then `ValidateWorkflow`
  (it also runs the linter) -> show the user -> `SaveWorkflow` once confirmed.
- One setting or a rename on an existing workflow: `PatchWorkflow` instead of resending every step.
- The request page of a custom app (where the workspace has app inputs): the fields a person fills in
  before each run. Make only the settings a person changes from run to run into fields, with a plain
  label and an example; `PatchWorkflow`'s `setAppInput`, `bindAppInput`, `setAppPage` and
  `removeAppInput` operations, or `SaveWorkflow`'s `appManifest`. See [`reference.md`](./reference.md).
- Copy a workflow, into this project or another: `CloneWorkflow`.

## Reporting

Say what changed in the user's terms: the workflow name, the steps added, removed or edited, and what
still needs doing before it can run. Relay validation and lint text as it came back.
