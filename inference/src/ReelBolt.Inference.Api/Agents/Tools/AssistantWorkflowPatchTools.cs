using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// One edit of <c>ProposePatchWorkflow</c>/<c>ApplyPatchWorkflow</c>. <c>Op</c> is <c>setStepConfig</c>
/// (<c>Patch</c>: an RFC 7396 merge-patch for that step's per-type config), <c>renameStep</c>
/// (<c>Label</c>) or <c>setStepField</c> (<c>Field</c> from the allowlist, <c>Value</c>). A step is named by
/// its <c>StepOrder</c>.
/// </summary>
/// <remarks>
/// The app-page operations (docs/design/app-inputs.md 13.2, appended members): <c>setAppInput</c> (<c>Input</c>),
/// <c>removeAppInput</c> (<c>Id</c>), <c>bindAppInput</c> (<c>StepOrder</c>, <c>Field</c>, <c>InputId</c>, <c>Mode</c>,
/// <c>Template</c>) and <c>setAppPage</c> (<c>Title</c>, <c>Tagline</c>, <c>Order</c>, <c>RequestTemplate</c>).
/// </remarks>
public sealed record WorkflowPatchOperation(
    string? Op = null,
    int StepOrder = 0,
    JsonElement? Patch = null,
    string? Label = null,
    string? Field = null,
    JsonElement? Value = null,
    JsonElement? Input = null,
    string? Id = null,
    string? InputId = null,
    string? Mode = null,
    string? Template = null,
    string? Title = null,
    string? Tagline = null,
    IReadOnlyList<string>? Order = null,
    string? RequestTemplate = null);

/// <summary>
/// Editing one setting of one step used to cost the whole workflow twice (<c>ProposeWorkflow</c> and
/// <c>ApplyWorkflow</c>, each with every config as an escaped string). These tools take the edit instead:
/// <c>ProposePatchWorkflow</c>/<c>ApplyPatchWorkflow</c> patch steps in place, and
/// <c>ProposeCloneWorkflow</c>/<c>ApplyCloneWorkflow</c> copy a workflow.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same validation as ApplyWorkflow.</b> The patched step list is built from the stored rows, then goes
/// through exactly what <c>ApplyWorkflow</c> runs: <see cref="StepConfigSaveValidator"/>, the
/// project-scoped <see cref="StepProviderOwnershipValidator"/> (provider ownership, continueComposition and
/// StepResult sources), and the plan gate; the diff comes from <see cref="IWorkflowEditDiffService"/>.
/// A refusal writes nothing.
/// </para>
/// <para>
/// <b>Token.</b> These use <see cref="IConfirmationTokenService"/> (user- and organization-bound), not the
/// legacy <c>ApplyWorkflow</c> diff token. That token hashes only each stored step's order, agent, type and
/// label, so a config-only change made after the proposal would not invalidate it, and it binds no user:
/// both are exactly what a patch tool (which edits configs) cannot accept. The payload here hashes the
/// COMPLETE stored steps and the complete patched steps, recomputed from the live rows on apply, so any
/// change to the workflow since the proposal (or to the operations) is "stale, re-propose".
/// </para>
/// <para>Not registered in <c>AssistantToolProvider</c> here; the registration, prompt and approval-card
/// wiring are separate.</para>
/// </remarks>
public sealed partial class AssistantWorkflowPatchTools
{
    internal const string OpPatchWorkflow = "PatchWorkflow";
    internal const string OpCloneWorkflow = "CloneWorkflow";

    /// <summary>Upper bound on the operations of one patch.</summary>
    public const int MaxOperations = 50;

    internal const string WorkflowNotFoundMessage = "workflow not found";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";

    private const int MaxValueChars = 200;
    private const int MaxNameChars = 200;

    /// <summary>The status line shown while each tool runs.</summary>
    public static IReadOnlyDictionary<string, string> Captions { get; } = new Dictionary<string, string>
    {
        ["ProposePatchWorkflow"] = "Preparing the workflow change",
        ["ApplyPatchWorkflow"] = "Saving the workflow change",
        ["ProposeCloneWorkflow"] = "Preparing a copy of the workflow",
        ["ApplyCloneWorkflow"] = "Copying the workflow"
    };

    /// <summary>The scalar fields <c>setStepField</c> may set, as the model must name them.</summary>
    public static IReadOnlyList<string> AllowedFields { get; } =
    [
        "label", "maxIterations", "minScore", "cacheMode", "agentDefinitionId", "registerOutputAsProjectFile",
        "continueCompositionJson", "inputMappingJson", "edgeConditionJson", "conditionExpression",
        "loopSourceExpression", "loopTargetStepOrder", "agentInputContextMode"
    ];

    private static readonly JsonSerializerOptions HashOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly PropertyInfo[] RequestProperties = typeof(CreateWorkflowStepRequest)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name != "EqualityContract")
        .ToArray();

    private static readonly string WorkflowNotFoundErrorJson = JsonSerializer.Serialize(new { error = WorkflowNotFoundMessage });
    private static readonly string StaleTokenErrorJson = JsonSerializer.Serialize(new { error = StaleTokenMessage });

    private readonly InferenceApiDbContext _db;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly IProjectAccessService _projectAccess;
    private readonly IWorkflowEditDiffService _diffService;
    private readonly StepProviderOwnershipValidator _providerOwnership;
    private readonly IPlanGateService? _planGate;
    private readonly IWorkflowLinter? _linter;
    private readonly IMediaEgressCallPermission _mediaEgress;

    public AssistantWorkflowPatchTools(
        InferenceApiDbContext db,
        IConfirmationTokenService confirmationTokens,
        IProjectAccessService projectAccess,
        IWorkflowEditDiffService diffService,
        StepProviderOwnershipValidator providerOwnership,
        IPlanGateService? planGate = null,
        IWorkflowLinter? linter = null,
        IMediaEgressCallPermission? mediaEgress = null)
    {
        _linter = linter;
        _mediaEgress = mediaEgress ?? new MediaEgressCallPermission(db);
        _db = db;
        _confirmationTokens = confirmationTokens;
        _projectAccess = projectAccess;
        _diffService = diffService;
        _providerOwnership = providerOwnership;
        _planGate = planGate;
    }

    /// <param name="context">This invocation's caller and page context.</param>
    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string fields = string.Join(", ", AllowedFields);
        string opsNote =
            $"operations (at most {MaxOperations}, applied in order): {{op:\"setStepConfig\", stepOrder, patch:<JSON object>}} merge-patches that step's per-type config (RFC 7396: a key set to null is deleted, an object merges, anything else replaces; the config is the JSON object itself, not a string); "
            + "{op:\"renameStep\", stepOrder, label}; "
            + $"{{op:\"setStepField\", stepOrder, field, value}} for one of: {fields} (the Json ones accept an object or a string; null clears). stepOrder names the step as GetWorkflow shows it."
            + AppOpsNote();

        return
        [
            AIFunctionFactory.Create(
                (IReadOnlyList<WorkflowPatchOperation> operations, Guid? workflowId = null, Guid? projectId = null) =>
                    ProposePatchWorkflowAsync(context, workflowId, operations, projectId),
                name: "ProposePatchWorkflow",
                description: "Proposes a SMALL edit to existing steps of a workflow — change one setting, rename a step — without re-sending the workflow. Returns the diff (only the changed steps and their changed fields) and a confirmationToken, WITHOUT writing anything. The patched workflow is checked exactly as ApplyWorkflow checks a save; a refusal lists the errors. " + opsNote + " The proposal's confirmationToken is accepted by ApplyPatchWorkflow. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (IReadOnlyList<WorkflowPatchOperation> operations, string confirmationToken, Guid? workflowId = null, Guid? projectId = null) =>
                    ApplyPatchWorkflowAsync(context, workflowId, operations, confirmationToken, projectId),
                name: "ApplyPatchWorkflow",
                description: "Applies a patch previously proposed with ProposePatchWorkflow. confirmationToken is verified against the workflow as it is stored now; if the workflow changed since the proposal, or the operations differ, the call is refused and nothing is written. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (Guid? workflowId = null, string? name = null, Guid? targetProjectId = null, Guid? projectId = null) =>
                    ProposeCloneWorkflowAsync(context, workflowId, name, targetProjectId, projectId),
                name: "ProposeCloneWorkflow",
                description: "Proposes COPYING a workflow (all steps, with every setting) into a new workflow, WITHOUT writing anything. name defaults to \"<name> (copy)\". By default the copy goes in the same project; targetProjectId (from ListProjects) copies into another project of the workspace — a workflow whose steps continue a composition or read step results of a run cannot move to another project, because those runs do not exist there. The proposal's confirmationToken is accepted by ApplyCloneWorkflow. " + AssistantProjectScope.ProjectIdNote),
            AIFunctionFactory.Create(
                (string confirmationToken, Guid? workflowId = null, string? name = null, Guid? targetProjectId = null, Guid? projectId = null) =>
                    ApplyCloneWorkflowAsync(context, workflowId, name, targetProjectId, confirmationToken, projectId),
                name: "ApplyCloneWorkflow",
                description: "Creates the copy proposed by ProposeCloneWorkflow. The workflowId, name, targetProjectId and confirmationToken are verified against the source workflow as stored now; a fabricated or stale token, or a changed argument, is refused and nothing is written. " + AssistantProjectScope.ProjectIdNote)
        ];
    }

    // ------------------------------------------------------------------
    // Patch
    // ------------------------------------------------------------------

    private async Task<string> ProposePatchWorkflowAsync(
        AssistantToolContext context, Guid? workflowId, IReadOnlyList<WorkflowPatchOperation>? operations, Guid? projectId)
    {
        PatchPlan plan = await PlanPatchAsync(context, workflowId, operations, projectId, tracked: false);
        if (plan.Error is not null)
            return CommunityRefusal(plan);

        WorkflowEditDiff diff = plan.Diff!;
        JsonObject result = new()
        {
            ["workflowId"] = plan.Workflow.Id,
            ["workflowName"] = plan.Workflow.Name,
            ["projectId"] = plan.Project!.Id,
            ["projectName"] = plan.Project.Name,
            ["operationCount"] = operations!.Count,
            ["changesNothing"] = diff.ChangesNothing && !plan.ManifestChanged,
            ["additions"] = EntriesToJson(diff.Additions),
            ["removals"] = EntriesToJson(diff.Removals),
            ["modifications"] = EntriesToJson(diff.Modifications),
            ["changedSteps"] = ChangedSteps(plan.Before!, plan.After!, diff.NormalizationChanges),
            ["normalization"] = NormalizationToJson(diff.NormalizationChanges),
            ["confirmationToken"] = _confirmationTokens.Compute(
                OpPatchWorkflow, PatchPayload(plan), context.UserId, plan.Project.OrganizationId)
        };
        if (plan.AppChanges is { Count: > 0 } appChanges)
            result["appChanges"] = new JsonArray([.. appChanges.Select(line => (JsonNode)JsonValue.Create(line)!)]);
        if (plan.Needs is { Count: > 0 })
            result["needsBeforeRunning"] = JsonSerializer.SerializeToNode(plan.Needs);
        if (plan.EgressGrants is { Count: > 0 } grants)
            result["egressGrants"] = grants.DeepClone();
        // MKT-01: a copied recipe's name, labels, changed values and app-page sentences reach the model fenced; the
        // customer's card keeps reading the plain copy.
        if (AssistantMarketplaceTools.IsCommunity(plan.Workflow.CreatedVia, plan.Workflow.TemplateKey))
        {
            AssistantCardCopy.Offer(result.ToJsonString());
            AssistantMarketplaceTools.FenceProposal(
                result, "workflowName", "additions", "removals", "modifications", "changedSteps", "normalization", "appChanges",
                "needsBeforeRunning", "egressGrants");
        }

        return result.ToJsonString();
    }

    private async Task<string> ApplyPatchWorkflowAsync(
        AssistantToolContext context, Guid? workflowId, IReadOnlyList<WorkflowPatchOperation>? operations, string? confirmationToken, Guid? projectId)
    {
        // Recomputed from the LIVE rows: nothing the model re-sends is trusted beyond the operations,
        // whose effect on those rows the token covers.
        PatchPlan plan = await PlanPatchAsync(context, workflowId, operations, projectId, tracked: true);
        if (plan.Error is not null)
            return CommunityRefusal(plan);

        Project project = plan.Project!;
        WorkflowDefinition workflow = plan.Workflow!;
        string payload = PatchPayload(plan);
        WorkflowEditDiff diff = plan.Diff!;
        if (!_confirmationTokens.Verify(OpPatchWorkflow, payload, context.UserId, project.OrganizationId, confirmationToken ?? string.Empty))
        {
            // The stored steps (and app page) already ARE the patched ones (this patch was applied): nothing to write.
            if (diff.ChangesNothing && !plan.ManifestChanged)
            {
                return FenceIfCommunity(workflow, JsonSerializer.Serialize(new
                {
                    workflowId = workflow.Id,
                    workflowName = workflow.Name,
                    projectId = project.Id,
                    projectName = project.Name,
                    stepCount = diff.ProposedSteps.Count,
                    alreadyApplied = true
                }));
            }

            return StaleTokenErrorJson;
        }

        // Save the NORMALIZED proposal ComputeDiff produced: a patch that edits a review loop's target can
        // still produce a loop that changes nothing on a repeat, and the stored graph must be the one the
        // diff (and the assistant's card) describes.
        IReadOnlyList<CreateWorkflowStepRequest> steps = plan.After!;
        workflow.UpdatedAt = DateTime.UtcNow;
        // The app page the operations produced (docs/design/app-inputs.md 13.2), saved with the steps.
        if (plan.ManifestChanged)
            workflow.AppManifestJson = plan.ManifestAfter is null ? null : AppManifestJson.Serialize(plan.ManifestAfter);

        // Compare against the STORED rows, not the normalized list against itself: when normalization
        // removed a loop the orders differ, so the rows must be replaced rather than updated in place.
        if (steps.Select(step => step.StepOrder).SequenceEqual(plan.Current!.Select(step => step.StepOrder)))
        {
            // The normalization only retargeted a loop (no step removed): the patch still edits the same
            // rows in place, so every step keeps its id and a past run's results keep pointing at it.
            foreach (WorkflowStep stored in workflow.Steps)
            {
                WorkflowStep built = _diffService.BuildStep(workflow.Id, steps.First(step => step.StepOrder == stored.StepOrder));
                built.Id = stored.Id;
                _db.Entry(stored).CurrentValues.SetValues(built);
            }

            _db.SaveChanges();
        }
        else
        {
            // A loop was removed and the numbering closed up: rows cannot be updated in place, so replace
            // them exactly as ApplyWorkflow does (steps a past run's results reference are archived first).
            foreach (var tracked in _db.ChangeTracker.Entries<WorkflowStep>()
                .Where(entry => entry.Entity.WorkflowDefinitionId == workflow.Id)
                .ToList())
            {
                _db.Entry(tracked.Entity).State = EntityState.Detached;
            }

            workflow.Steps.Clear();
            await new WorkflowStepReplacer(_db).ReplaceStepsAsync(
                workflow, steps.Select(step => _diffService.BuildStep(workflow.Id, step)), CancellationToken.None);
        }

        return FenceIfCommunity(workflow, JsonSerializer.Serialize(new
        {
            workflowId = workflow.Id,
            workflowName = workflow.Name,
            projectId = project.Id,
            projectName = project.Name,
            stepCount = steps.Count,
            changedSteps = ChangedSteps(plan.Before!, plan.After!, diff.NormalizationChanges).Count
        }));
    }

    /// <summary>
    /// MKT-01: a refusal about a copied community recipe can quote the publisher's step labels and field labels (the save
    /// validator, the app page check, the linter, the footage-sharing guard), so it reaches the model fenced too.
    /// </summary>
    private static string CommunityRefusal(PatchPlan plan) => FenceIfCommunity(plan.Workflow, plan.Error!);

    /// <summary>
    /// MKT-01: a refusal or an Apply result about a copied community recipe (its name, a label in an error sentence)
    /// reaches the model fenced: a refusal through <see cref="AssistantMarketplaceTools.FenceRefusal"/> (code kept), an
    /// Apply result's <c>workflowName</c> through <see cref="AssistantMarketplaceTools.FenceProposal"/>. Anything else, as it was.
    /// </summary>
    private static string FenceIfCommunity(WorkflowDefinition? workflow, string json)
    {
        if (workflow is null || !AssistantMarketplaceTools.IsCommunity(workflow.CreatedVia, workflow.TemplateKey))
            return json;
        if (JsonNode.Parse(json) is not JsonObject node)
            return json;
        if (node.ContainsKey("error"))
            return AssistantMarketplaceTools.FenceRefusal(json);
        AssistantMarketplaceTools.FenceProposal(node, "workflowName");
        return node.ToJsonString();
    }

    private sealed record PatchPlan(
        string? Error = null,
        Project? Project = null,
        WorkflowDefinition? Workflow = null,
        List<WorkflowStep>? Current = null,
        List<CreateWorkflowStepRequest>? Before = null,
        List<CreateWorkflowStepRequest>? After = null,
        List<object>? Needs = null,
        WorkflowEditDiff? Diff = null)
    {
        /// <summary>True when the operations change the stored app page.</summary>
        public bool ManifestChanged { get; init; }

        /// <summary>The app page to store when <see cref="ManifestChanged"/>.</summary>
        public AppManifest? ManifestAfter { get; init; }

        /// <summary>The stored page's JSON the plan started from (bound by the token when the page changes).</summary>
        public string? ManifestBeforeJson { get; init; }

        /// <summary>What the patch changes about the app page, in plain sentences, for the approval card.</summary>
        public List<string>? AppChanges { get; init; }

        /// <summary>The steps whose footage-sharing consent the patch switches on under a grantor (card and Apply read it).</summary>
        public JsonArray? EgressGrants { get; init; }
    }

    private async Task<PatchPlan> PlanPatchAsync(
        AssistantToolContext context, Guid? workflowId, IReadOnlyList<WorkflowPatchOperation>? operations, Guid? projectId, bool tracked)
    {
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string projectError);
        if (project is null)
            return new PatchPlan(Error: projectError);

        Guid? id = workflowId is { } named && named != Guid.Empty ? named : AssistantProjectScope.PageWorkflowFor(context, project);
        if (id is null || id == Guid.Empty)
            return new PatchPlan(Error: Refusal("workflowId is required (no workflow is open on this page for this project; find it with ListWorkflows)"));

        IQueryable<WorkflowDefinition> query = _db.WorkflowDefinitions.Include(w => w.Steps);
        if (!tracked)
            query = query.AsNoTracking();
        WorkflowDefinition? workflow = query.FirstOrDefault(w => w.Id == id && w.ProjectId == project.Id && w.Kind == WorkflowDefinitionKind.Standard);
        if (workflow is null)
            return new PatchPlan(Error: WorkflowNotFoundErrorJson);

        List<WorkflowStep> current = [.. workflow.Steps.OrderBy(s => s.StepOrder)];
        if (current.Select(s => s.StepOrder).Distinct().Count() != current.Count)
            return new PatchPlan(Error: Refusal("this workflow has two steps with the same stepOrder, so a step cannot be addressed; fix it with ProposeWorkflow"), Workflow: workflow);

        List<CreateWorkflowStepRequest> before = [.. current.Select(AssistantStepShapes.FromStep)];
        AppManifest? storedManifest = AppManifestJson.TryParse(workflow.AppManifestJson);
        // The page an app-page operation starts from: the stored page, else the page the app shows today (derived), so
        // adding a field keeps the brief fields and file slots the page already asks for.
        IReadOnlyList<AppStepFacts> beforeFacts = await WorkflowAppSupport.FactsForStepsAsync(_db, current, CancellationToken.None);
        AppPageDraft page = new(() => storedManifest
            ?? new AppManifest(Inputs: AppManifestDeriver.Derive(beforeFacts, workflow.RequiresUserInput).Inputs));
        string? opError = ApplyOperations(before, operations, out List<CreateWorkflowStepRequest> after, page);
        if (opError is not null)
            return new PatchPlan(Error: Refusal(opError), Workflow: workflow);

        // The footage-sharing consent is the customer's, given in the app. Checked on the live rows at propose AND apply;
        // a clone copies what the customer already set, so it is not asked.
        // Only a grantor may keep a flip to on (the card the person allowed in the in-app chat; never over MCP); a token alone never does.
        bool mayGrant = context.Initiator != ExecutionInitiator.Mcp
            && (tracked
                ? await _mediaEgress.GrantorAsync(MediaEgressCall.FromTool(MediaEgressSurface.WorkflowEdit, context, project.Id), CancellationToken.None) is not null
                : context.ChatApproved);
        AssistantEgressConsentGuard.Outcome egress = AssistantEgressConsentGuard.Evaluate(after, before, mayGrant);
        if (egress.Refusal is not null)
            return new PatchPlan(Error: context.Initiator == ExecutionInitiator.Mcp ? AssistantEgressConsentGuard.McpRefusal(egress.Refusal) : egress.Refusal, Workflow: workflow);
        // At apply a grant must be a step the approved card listed (the stored workflow may have changed since it was shown).
        if (tracked && AssistantEgressConsentGuard.UnlistedGrantRefusal(egress, AssistantMediaEgressApproval.ListedSteps) is { } unlisted)
            return new PatchPlan(Error: unlisted, Workflow: workflow);
        after = egress.Steps;
        JsonArray egressGrants = AssistantEgressConsentGuard.GrantEntries(after, egress.Granted);

        // The graph that will be written is the diff's normalized proposal, so normalize BEFORE the
        // gates: the save validator, orphan check and linter must see exactly the steps Apply writes,
        // and the token must bind that same graph.
        WorkflowEditDiff diff = _diffService.ComputeDiff(workflow.Id, current, after);
        after = [.. diff.ProposedSteps];

        string? invalid = await ValidateAsync(project, before, after);
        if (invalid is not null)
            return new PatchPlan(Error: invalid, Workflow: workflow);

        AppManifest? manifestAfter = storedManifest;
        bool manifestChanged = false;
        {
            // The app page and the steps' bindings as a save checks them (docs/design/app-inputs.md 11.1): a changed page is
            // validated in full, an unchanged one re-checked against the patched steps (WL018 included, so a patch that pins
            // the analyze file or swaps an agent cannot orphan an exposed app input).
            (AppManifest? checkedManifest, IReadOnlyList<string> manifestErrors) = WorkflowAppSupport.CheckForSave(
                page.Touched ? page.Manifest : null, workflow.AppManifestJson,
                await WorkflowAppSupport.FactsForRequestAsync(_db, after, CancellationToken.None), StepConfigSaveValidator.BindingStepsOf(after));
            if (manifestErrors.Count > 0)
                return new PatchPlan(Error: JsonSerializer.Serialize(new { error = string.Join(" ", manifestErrors), errors = manifestErrors, code = WorkflowAppSupport.InvalidCode }), Workflow: workflow);
            if (page.Touched && checkedManifest is not null)
            {
                manifestAfter = checkedManifest;
                manifestChanged = AssistantAppInputs.Digest(checkedManifest) != AssistantAppInputs.Digest(workflow.AppManifestJson);
            }
        }

        // The pre-run linter, as ProposeWorkflow runs it: refuse structural errors the stored workflow did not already have.
        if (_linter is not null
            && await AssistantLintRefusal.LintDraftAsync(_linter, _db, project, after, null, CancellationToken.None, manifestAfter) is { } draft
            && AssistantLintRefusal.Evaluate(
                draft, await AssistantLintRefusal.LintStoredAsync(_linter, _db, project, workflow.Id, current, CancellationToken.None)) is { } lintRefusal)
        {
            return new PatchPlan(Error: JsonSerializer.Serialize(lintRefusal, HashOptions), Workflow: workflow);
        }

        IReadOnlyDictionary<Guid, string> modelNames = await AssistantAppInputs.ModelNamesAsync(
            _db, AssistantAppInputs.ManifestModelIds(storedManifest, manifestAfter), CancellationToken.None);
        List<string>? appChanges = AssistantAppInputs.DescribeChanges(
            storedManifest, StepConfigSaveValidator.BindingStepsOf(before), manifestAfter, StepConfigSaveValidator.BindingStepsOf(after), modelNames);

        return new PatchPlan(null, project, workflow, current, before, after, [.. egress.NeedsBeforeRunning], diff)
        {
            ManifestChanged = manifestChanged,
            ManifestAfter = manifestAfter,
            ManifestBeforeJson = workflow.AppManifestJson,
            AppChanges = appChanges,
            EgressGrants = egressGrants,
        };
    }

    /// <summary>
    /// The save checks <c>ApplyWorkflow</c> runs on the whole step list, plus the agent rule of
    /// <c>ValidateWorkflow</c> for an agent a patch swapped in. Null when the steps are savable.
    /// </summary>
    private async Task<string?> ValidateAsync(
        Project project, IReadOnlyList<CreateWorkflowStepRequest> before, IReadOnlyList<CreateWorkflowStepRequest> after)
    {
        List<string> errors = [.. StepConfigSaveValidator.Validate(after)];
        // Project-scoped (T-014): a sibling project's run, version or step result is refused.
        errors.AddRange(await _providerOwnership.ValidateAsync(project.OrganizationId, project.Id, after, CancellationToken.None));

        foreach (CreateWorkflowStepRequest step in after)
        {
            CreateWorkflowStepRequest? old = before.FirstOrDefault(b => b.StepOrder == step.StepOrder);
            if (old is not null && old.AgentDefinitionId == step.AgentDefinitionId)
                continue;
            AgentDefinition? agent = _db.AgentDefinitions.AsNoTracking().FirstOrDefault(a => a.Id == step.AgentDefinitionId);
            if (agent is null || !(agent.IsBuiltIn || agent.OrganizationId == project.OrganizationId))
                errors.Add($"Step {step.StepOrder}: agent {step.AgentDefinitionId} does not exist or is not usable in this project.");
        }

        if (errors.Count > 0)
            return JsonSerializer.Serialize(new { error = StepConfigSaveValidator.FormatMessage(errors), errors });

        if (_planGate is not null && await _planGate.CheckStepsAsync(project.OrganizationId, after, CancellationToken.None) is { } planRefused)
            return JsonSerializer.Serialize(new { error = planRefused.Message, planGate = planRefused.ToBody() });

        return null;
    }

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    /// <param name="page">The app page while it is patched.</param>
    private static string? ApplyOperations(
        List<CreateWorkflowStepRequest> before, IReadOnlyList<WorkflowPatchOperation>? operations, out List<CreateWorkflowStepRequest> after,
        AppPageDraft page)
    {
        after = [.. before];
        if (operations is null || operations.Count == 0)
            return "operations must contain at least one operation";
        if (operations.Count > MaxOperations)
            return $"at most {MaxOperations} operations per patch (got {operations.Count}); split it into several patches";

        for (int i = 0; i < operations.Count; i++)
        {
            WorkflowPatchOperation op = operations[i];
            string at = $"operation {i + 1}";
            string name = (op.Op ?? string.Empty).ToLowerInvariant();
            if (IsPageOperation(name))
            {
                if (ApplyPageOperation(name, op, after, page) is { } pageError)
                    return $"{at} ({op.Op}): {pageError}";
                continue;
            }

            int index = after.FindIndex(s => s.StepOrder == op.StepOrder);
            if (index < 0)
                return $"{at}: this workflow has no step {op.StepOrder}";

            string? error = name switch
            {
                "setstepconfig" => SetStepConfig(after, index, op),
                "renamestep" => op.Label is null ? "renameStep needs a label" : SetField(after, index, "label", JsonSerializer.SerializeToElement(op.Label)),
                "setstepfield" => string.IsNullOrWhiteSpace(op.Field) ? "setStepField needs a field" : SetField(after, index, op.Field!, op.Value),
                OpBindAppInput => BindAppInput(after, index, op, page),
                _ => $"unknown op '{op.Op}' (use setStepConfig, renameStep, setStepField, setAppInput, removeAppInput, bindAppInput or setAppPage)"
            };
            if (error is not null)
                return $"{at} (step {op.StepOrder}): {error}";
        }

        return null;
    }

    private static string? SetStepConfig(List<CreateWorkflowStepRequest> steps, int index, WorkflowPatchOperation op)
    {
        CreateWorkflowStepRequest step = steps[index];
        if (op.Patch is not { } patchElement)
            return "setStepConfig needs a patch (a JSON object)";

        JsonNode? patch;
        try
        {
            // A model that escapes the object into a string still means the object.
            patch = patchElement.ValueKind == JsonValueKind.String
                ? JsonNode.Parse(patchElement.GetString() ?? string.Empty)
                : JsonNode.Parse(patchElement.GetRawText());
        }
        catch (JsonException)
        {
            return "patch is not valid JSON";
        }

        if (patch is not JsonObject patchObject)
            return "patch must be a JSON object";

        string type = step.StepType ?? nameof(StepType.Agent);
        if (!Enum.TryParse(type, out StepType stepType) || ConfigOf(step, stepType) is not { } current)
            return $"a {type} step has no per-type config to patch (use setStepField for its other settings)";

        JsonObject merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(current.Json) ? new JsonObject() : (JsonNode.Parse(current.Json) as JsonObject ?? new JsonObject());
        }
        catch (JsonException)
        {
            merged = new JsonObject();
        }

        MergePatch(merged, patchObject);
        steps[index] = WithConfig(step, stepType, merged.ToJsonString());
        return null;
    }

    /// <summary>RFC 7396 into <paramref name="target"/>: null deletes, an object merges, anything else replaces.</summary>
    internal static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach ((string key, JsonNode? value) in patch.ToList())
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (value is JsonObject nested)
            {
                JsonObject child = target[key] as JsonObject ?? new JsonObject();
                MergePatch(child, nested);
                target[key] = child;
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static string? SetField(List<CreateWorkflowStepRequest> steps, int index, string field, JsonElement? value)
    {
        CreateWorkflowStepRequest step = steps[index];
        string key = field.Trim().ToLowerInvariant();
        string? error = null;
        switch (key)
        {
            case "label": steps[index] = step with { Label = Text(value, ref error) }; break;
            case "maxiterations": steps[index] = step with { MaxIterations = Int(value, ref error) }; break;
            case "minscore": steps[index] = step with { MinScore = Int(value, ref error) }; break;
            case "looptargetsteporder": steps[index] = step with { LoopTargetStepOrder = Int(value, ref error) }; break;
            case "conditionexpression": steps[index] = step with { ConditionExpression = Text(value, ref error) }; break;
            case "loopsourceexpression": steps[index] = step with { LoopSourceExpression = Text(value, ref error) }; break;
            case "registeroutputasprojectfile": steps[index] = step with { RegisterOutputAsProjectFile = Bool(value, ref error) }; break;
            case "continuecompositionjson": steps[index] = step with { ContinueCompositionJson = JsonText(value, ref error) }; break;
            case "inputmappingjson": steps[index] = step with { InputMappingJson = JsonText(value, ref error) }; break;
            case "edgeconditionjson": steps[index] = step with { EdgeConditionJson = JsonText(value, ref error) }; break;
            case "cachemode":
                string? cache = Text(value, ref error);
                if (error is null && cache is not null)
                {
                    if (!Enum.TryParse(cache, out StepCacheMode mode) || !Enum.IsDefined(mode))
                        error = $"cacheMode must be one of {string.Join(", ", Enum.GetNames<StepCacheMode>())}";
                    else
                        cache = mode.ToString();
                }

                steps[index] = step with { CacheMode = cache };
                break;
            case "agentinputcontextmode":
                string? contextMode = Text(value, ref error);
                if (error is null && contextMode is not null)
                {
                    if (!Enum.TryParse(contextMode, out AgentInputContextMode mode) || !Enum.IsDefined(mode))
                        error = $"agentInputContextMode must be one of {string.Join(", ", Enum.GetNames<AgentInputContextMode>())}";
                    else
                        contextMode = mode.ToString();
                }

                steps[index] = step with { AgentInputContextMode = contextMode };
                break;
            case "agentdefinitionid":
                string? agent = Text(value, ref error);
                if (error is null && (!Guid.TryParse(agent, out Guid agentId) || agentId == Guid.Empty))
                    error = "agentDefinitionId must be an agent id (GUID)";
                else if (error is null)
                    steps[index] = step with { AgentDefinitionId = Guid.Parse(agent!) };
                break;
            default:
                return $"'{field}' is not an allowed field (allowed: {string.Join(", ", AllowedFields)}; use setStepConfig for the per-type config)";
        }

        return error is null ? null : $"{field}: {error}";
    }

    private static string? Text(JsonElement? value, ref string? error)
    {
        if (value is not { ValueKind: not JsonValueKind.Null } v)
            return null;
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString();
        error = "must be a string (or null to clear)";
        return null;
    }

    private static int? Int(JsonElement? value, ref string? error)
    {
        if (value is not { ValueKind: not JsonValueKind.Null } v)
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int number))
            return number;
        error = "must be a whole number (or null to clear)";
        return null;
    }

    private static bool Bool(JsonElement? value, ref string? error)
    {
        if (value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v)
            return v.GetBoolean();
        error = "must be true or false";
        return false;
    }

    /// <summary>A JSON-text field: a string is kept as written, an object or array is compacted to text, null clears.</summary>
    private static string? JsonText(JsonElement? value, ref string? error)
    {
        if (value is not { ValueKind: not JsonValueKind.Null } v)
            return null;
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString();
        if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return JsonNode.Parse(v.GetRawText())!.ToJsonString();
        error = "must be a JSON object or a string (or null to clear)";
        return null;
    }

    private static (string? Json, string Property)? ConfigOf(CreateWorkflowStepRequest s, StepType type) => type switch
    {
        StepType.Extract => (s.ExtractConfigJson, nameof(s.ExtractConfigJson)),
        StepType.VideoAnalyze => (s.VideoAnalyzeConfigJson, nameof(s.VideoAnalyzeConfigJson)),
        StepType.VideoCompile => (s.VideoCompileConfigJson, nameof(s.VideoCompileConfigJson)),
        StepType.EditRoom => (s.EditRoomConfigJson, nameof(s.EditRoomConfigJson)),
        StepType.GraphicsRoom => (s.GraphicsRoomConfigJson, nameof(s.GraphicsRoomConfigJson)),
        StepType.ColorGradeRoom => (s.ColorGradeRoomConfigJson, nameof(s.ColorGradeRoomConfigJson)),
        StepType.Conditional => (s.ConditionalConfigJson, nameof(s.ConditionalConfigJson)),
        StepType.ReviewLoop => (s.ReviewLoopConfigJson, nameof(s.ReviewLoopConfigJson)),
        StepType.VideoGenerate => (s.VideoGenerateConfigJson, nameof(s.VideoGenerateConfigJson)),
        StepType.Voiceover => (s.VoiceoverConfigJson, nameof(s.VoiceoverConfigJson)),
        StepType.ThumbnailGenerate => (s.ThumbnailGenerateConfigJson, nameof(s.ThumbnailGenerateConfigJson)),
        StepType.DailiesRoom => (s.DailiesRoomConfigJson, nameof(s.DailiesRoomConfigJson)),
        StepType.TimelineRender => (s.TimelineRenderConfigJson, nameof(s.TimelineRenderConfigJson)),
        StepType.DesignSystem => (s.DesignSystemConfigJson, nameof(s.DesignSystemConfigJson)),
        StepType.ExtractFrame => (s.ExtractFrameConfigJson, nameof(s.ExtractFrameConfigJson)),
        StepType.AudioGenerate => (s.AudioGenerateConfigJson, nameof(s.AudioGenerateConfigJson)),
        StepType.PinInsert => (s.PinInsertConfigJson, nameof(s.PinInsertConfigJson)),
        StepType.AudioCleanup => (s.AudioCleanupConfigJson, nameof(s.AudioCleanupConfigJson)),
        StepType.VideoEnhance => (s.VideoEnhanceConfigJson, nameof(s.VideoEnhanceConfigJson)),
        StepType.ImageGenerate => (s.ImageGenerateConfigJson, nameof(s.ImageGenerateConfigJson)),
        _ => null
    };

    private static CreateWorkflowStepRequest WithConfig(CreateWorkflowStepRequest s, StepType type, string json) => type switch
    {
        StepType.Extract => s with { ExtractConfigJson = json },
        StepType.VideoAnalyze => s with { VideoAnalyzeConfigJson = json },
        StepType.VideoCompile => s with { VideoCompileConfigJson = json },
        StepType.EditRoom => s with { EditRoomConfigJson = json },
        StepType.GraphicsRoom => s with { GraphicsRoomConfigJson = json },
        StepType.ColorGradeRoom => s with { ColorGradeRoomConfigJson = json },
        StepType.Conditional => s with { ConditionalConfigJson = json },
        StepType.ReviewLoop => s with { ReviewLoopConfigJson = json },
        StepType.VideoGenerate => s with { VideoGenerateConfigJson = json },
        StepType.Voiceover => s with { VoiceoverConfigJson = json },
        StepType.ThumbnailGenerate => s with { ThumbnailGenerateConfigJson = json },
        StepType.DailiesRoom => s with { DailiesRoomConfigJson = json },
        StepType.TimelineRender => s with { TimelineRenderConfigJson = json },
        StepType.DesignSystem => s with { DesignSystemConfigJson = json },
        StepType.ExtractFrame => s with { ExtractFrameConfigJson = json },
        StepType.AudioGenerate => s with { AudioGenerateConfigJson = json },
        StepType.PinInsert => s with { PinInsertConfigJson = json },
        StepType.AudioCleanup => s with { AudioCleanupConfigJson = json },
        StepType.VideoEnhance => s with { VideoEnhanceConfigJson = json },
        StepType.ImageGenerate => s with { ImageGenerateConfigJson = json },
        _ => s
    };

    // ------------------------------------------------------------------
    // Diff rendering
    // ------------------------------------------------------------------

    private static JsonArray EntriesToJson(IReadOnlyList<WorkflowStepDiffEntry> entries) =>
        new([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["stepOrder"] = e.StepOrder,
            ["agentDefinitionId"] = e.AgentDefinitionId,
            ["stepType"] = e.StepType,
            ["label"] = e.Summary
        })]);

    /// <summary>
    /// Only the steps that differ, each with only the fields that differ (config fields down to the changed
    /// key). A step whose order moved because normalization removed a loop above it is compared at its WRITTEN
    /// order and carries <c>was</c>; a step normalization removed is listed once with <c>removed: true</c>; a
    /// written order no stored step maps to is listed with <c>added: true</c>.
    /// </summary>
    private static JsonArray ChangedSteps(
        IReadOnlyList<CreateWorkflowStepRequest> before,
        IReadOnlyList<CreateWorkflowStepRequest> after,
        IReadOnlyList<WorkflowNormalizationChange> normalizationChanges)
    {
        int[] removedOrders = [.. normalizationChanges
            .Where(change => change.Kind == WorkflowNormalizationChangeKind.Removed)
            .Select(change => change.StepOrder)];

        JsonArray steps = [];
        HashSet<int> written = [];
        foreach (CreateWorkflowStepRequest b in before)
        {
            if (removedOrders.Contains(b.StepOrder))
            {
                steps.Add(RemovedStep(b));
                continue;
            }

            // Every removed loop below this order moves the step up one; normalization only removes loops.
            int order = b.StepOrder - removedOrders.Count(removed => removed < b.StepOrder);
            if (after.FirstOrDefault(s => s.StepOrder == order) is not { } a)
            {
                steps.Add(RemovedStep(b));
                continue;
            }

            written.Add(order);
            JsonArray fields = [];
            foreach (PropertyInfo property in RequestProperties)
            {
                // The written order is reported as `was`, not as a field edit.
                if (property.Name == nameof(CreateWorkflowStepRequest.StepOrder))
                    continue;
                foreach (JsonObject change in FieldChanges(ToCamel(property.Name), property.GetValue(b), property.GetValue(a)))
                    fields.Add(change);
            }

            if (fields.Count > 0)
            {
                JsonObject entry = new()
                {
                    ["stepOrder"] = a.StepOrder,
                    ["label"] = a.Label,
                    ["stepType"] = a.StepType ?? nameof(StepType.Agent),
                    ["fields"] = fields
                };
                if (a.StepOrder != b.StepOrder)
                    entry["was"] = b.StepOrder;
                steps.Add(entry);
            }
        }

        foreach (CreateWorkflowStepRequest a in after)
        {
            if (written.Contains(a.StepOrder))
                continue;
            steps.Add(new JsonObject
            {
                ["stepOrder"] = a.StepOrder,
                ["label"] = a.Label,
                ["stepType"] = a.StepType ?? nameof(StepType.Agent),
                ["added"] = true
            });
        }

        return steps;

        static JsonObject RemovedStep(CreateWorkflowStepRequest b) => new()
        {
            ["stepOrder"] = b.StepOrder,
            ["label"] = b.Label,
            ["stepType"] = b.StepType ?? nameof(StepType.Agent),
            ["removed"] = true
        };
    }

    /// <summary>
    /// The normalizer's report as the proposal JSON the card reads. It is taken from the diff, never recomputed
    /// here; <c>fromStepOrder</c>/<c>toStepOrder</c> appear only when the normalizer reported them.
    /// </summary>
    private static JsonArray NormalizationToJson(IReadOnlyList<WorkflowNormalizationChange> changes)
    {
        JsonArray entries = [];
        foreach (WorkflowNormalizationChange change in changes)
        {
            JsonObject entry = new()
            {
                ["change"] = change.Kind == WorkflowNormalizationChangeKind.Removed ? "removed" : "retargeted",
                ["stepOrder"] = change.StepOrder,
                ["label"] = change.Label,
                ["stepType"] = change.StepType
            };
            if (change.FromTarget is { } from)
                entry["fromStepOrder"] = from;
            if (change.ToTarget is { } to)
                entry["toStepOrder"] = to;
            entries.Add(entry);
        }

        return entries;
    }

    private static IEnumerable<JsonObject> FieldChanges(string field, object? before, object? after)
    {
        if (Equals(before, after))
            yield break;

        if (before is string or null && after is string or null
            && field.EndsWith("Json", StringComparison.Ordinal)
            && TryLeaves(before as string, out Dictionary<string, JsonNode?>? was)
            && TryLeaves(after as string, out Dictionary<string, JsonNode?>? now))
        {
            foreach (string path in was.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
            {
                was.TryGetValue(path, out JsonNode? b);
                now.TryGetValue(path, out JsonNode? a);
                if (!(was.ContainsKey(path) && now.ContainsKey(path) && JsonNode.DeepEquals(b, a)))
                    yield return Change(path.Length == 0 ? field : $"{field}.{path}", b, a);
            }

            yield break;
        }

        yield return Change(field, before is null ? null : JsonValue.Create(before.ToString()), after is null ? null : JsonValue.Create(after.ToString()));
    }

    private static JsonObject Change(string field, JsonNode? before, JsonNode? after) => new()
    {
        ["field"] = field,
        ["before"] = Shorten(before),
        ["after"] = Shorten(after)
    };

    private static JsonNode? Shorten(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text) && text is not null && text.Length > MaxValueChars)
            return JsonValue.Create(text[..MaxValueChars] + $"… [truncated, {text.Length - MaxValueChars} more chars]");
        return node?.DeepClone();
    }

    /// <summary>The leaf values of a JSON-text field by dotted path; an empty field has no leaves; non-JSON text is one leaf.</summary>
    private static bool TryLeaves(string? json, out Dictionary<string, JsonNode?> leaves)
    {
        leaves = [];
        if (string.IsNullOrWhiteSpace(json))
            return true;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            leaves[string.Empty] = JsonValue.Create(json);
            return true;
        }

        Flatten(string.Empty, root, leaves);
        return true;
    }

    private static void Flatten(string path, JsonNode? node, Dictionary<string, JsonNode?> into)
    {
        if (node is JsonObject { Count: > 0 } obj)
        {
            foreach ((string key, JsonNode? child) in obj)
                Flatten(path.Length == 0 ? key : $"{path}.{key}", child, into);
        }
        else
        {
            into[path] = node;
        }
    }

    private static string ToCamel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    // ------------------------------------------------------------------
    // Clone
    // ------------------------------------------------------------------

    private sealed record ClonePlan(
        string? Error = null,
        Project? Source = null,
        Project? Target = null,
        WorkflowDefinition? Workflow = null,
        List<CreateWorkflowStepRequest>? Steps = null,
        string? Name = null,
        int ExistingCopies = 0,
        bool EgressReset = false);

    private async Task<ClonePlan> PlanCloneAsync(
        AssistantToolContext context, Guid? workflowId, string? name, Guid? targetProjectId, Guid? projectId)
    {
        Project? source = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.View, projectId, out string sourceError);
        if (source is null)
            return new ClonePlan(Error: sourceError);

        Project target = source;
        if (AssistantProjectScope.IsExplicit(targetProjectId) && targetProjectId != source.Id)
        {
            Project? resolved = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, targetProjectId, out string targetError);
            if (resolved is null)
                return new ClonePlan(Error: targetError);
            if (resolved.OrganizationId != source.OrganizationId)
                return new ClonePlan(Error: AssistantProjectScope.ProjectNotFoundErrorJson);
            target = resolved;
        }
        else if (AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, source.Id, out string editError) is null)
        {
            return new ClonePlan(Error: editError);
        }

        Guid? id = workflowId is { } named && named != Guid.Empty ? named : AssistantProjectScope.PageWorkflowFor(context, source);
        if (id is null || id == Guid.Empty)
            return new ClonePlan(Error: Refusal("workflowId is required (no workflow is open on this page for this project; find it with ListWorkflows)"));

        WorkflowDefinition? workflow = _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps)
            .FirstOrDefault(w => w.Id == id && w.ProjectId == source.Id && w.Kind == WorkflowDefinitionKind.Standard);
        if (workflow is null)
            return new ClonePlan(Error: WorkflowNotFoundErrorJson);

        string cloneName = string.IsNullOrWhiteSpace(name) ? $"{workflow.Name} (copy)" : name.Trim();
        if (cloneName.Length > MaxNameChars)
            return new ClonePlan(Error: Refusal($"name is too long (at most {MaxNameChars} characters)"), Workflow: workflow);

        List<CreateWorkflowStepRequest> steps = [.. workflow.Steps.OrderBy(s => s.StepOrder).Select(AssistantStepShapes.FromStep)];

        bool egressReset = false;
        if (target.Id != source.Id)
        {
            // The footage-sharing permission is the customer's, given per project in the app: a copy into another
            // project starts with it off.
            steps = AssistantEgressConsentGuard.ResetAll(steps, out egressReset);

            // A continued composition and a StepResult source name rows of the SOURCE project's runs; in
            // another project they would be refused one by one (and could never run), so say it once.
            List<int> bound = [.. steps
                .Where(s => !string.IsNullOrWhiteSpace(s.ContinueCompositionJson))
                .Select(s => s.StepOrder)
                .Union(StepConfigSaveValidator.StepResultBoundStepOrders(steps))
                .Order()];
            if (bound.Count > 0)
            {
                return new ClonePlan(Error: Refusal(
                    $"Steps {string.Join(", ", bound)} continue a composition or read step results of a run of the source project, and those runs do not exist in the target project. Clone into the same project, or remove those references from the source workflow first."), Workflow: workflow);
            }
        }

        string? invalid = await ValidateAsync(target, [], steps);
        if (invalid is not null)
            return new ClonePlan(Error: invalid, Workflow: workflow);

        // Lint the copy against the target project; the source's own findings (in its project) are the "before".
        if (_linter is not null
            && await AssistantLintRefusal.LintDraftAsync(_linter, _db, target, steps, null, CancellationToken.None) is { } draft
            && AssistantLintRefusal.Evaluate(
                draft, await AssistantLintRefusal.LintStoredAsync(_linter, _db, source, workflow.Id, [.. workflow.Steps.OrderBy(s => s.StepOrder)], CancellationToken.None)) is { } lintRefusal)
        {
            return new ClonePlan(Error: JsonSerializer.Serialize(lintRefusal, HashOptions), Workflow: workflow);
        }

        // QA-P2-12: the token binds how many workflows of that name the target already has, so applying it
        // moves the count and a replay (a retried call) is stale instead of writing another copy.
        int existing = _db.WorkflowDefinitions.AsNoTracking().Count(w => w.ProjectId == target.Id && w.Name == cloneName);
        return new ClonePlan(null, source, target, workflow, steps, cloneName, existing, egressReset);
    }

    private string ClonePayload(ClonePlan plan) => JsonSerializer.Serialize(new
    {
        op = OpCloneWorkflow,
        sourceProjectId = plan.Source!.Id,
        targetProjectId = plan.Target!.Id,
        workflowId = plan.Workflow!.Id,
        name = plan.Name,
        existingCopies = plan.ExistingCopies,
        requiresUserInput = plan.Workflow.RequiresUserInput,
        // The apply copies the source's app manifest, so the token binds exactly the manifest that was proposed.
        appManifest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(plan.Workflow.AppManifestJson ?? string.Empty))),
        steps = Hash(plan.Steps!)
    });

    private async Task<string> ProposeCloneWorkflowAsync(
        AssistantToolContext context, Guid? workflowId, string? name, Guid? targetProjectId, Guid? projectId)
    {
        ClonePlan plan = await PlanCloneAsync(context, workflowId, name, targetProjectId, projectId);
        if (plan.Error is not null)
            return FenceIfCommunity(plan.Workflow, plan.Error);

        JsonObject result = new()
        {
            ["sourceWorkflowId"] = plan.Workflow!.Id,
            ["sourceWorkflowName"] = plan.Workflow.Name,
            ["sourceProjectName"] = plan.Source!.Name,
            ["projectId"] = plan.Target!.Id,
            ["projectName"] = plan.Target.Name,
            ["name"] = plan.Name,
            ["crossProject"] = plan.Target.Id != plan.Source.Id,
            ["footageSharingPermissionTurnedOff"] = plan.EgressReset,
            ["note"] = plan.EgressReset
                ? "The copy starts with the permission to send the footage to an outside service turned off; the customer turns it on in the app (open the step in the app and switch on the option that allows sending your footage)."
                : null,
            ["stepCount"] = plan.Steps!.Count,
            ["steps"] = new JsonArray([.. plan.Steps.Select(s => (JsonNode)new JsonObject
            {
                ["stepOrder"] = s.StepOrder,
                ["label"] = s.Label,
                ["stepType"] = s.StepType ?? nameof(StepType.Agent)
            })]),
            ["confirmationToken"] = _confirmationTokens.Compute(OpCloneWorkflow, ClonePayload(plan), context.UserId, plan.Target.OrganizationId)
        };
        // MKT-01: the copied recipe's name and step labels reach the model fenced; the card keeps reading the plain copy.
        if (AssistantMarketplaceTools.IsCommunity(plan.Workflow.CreatedVia, plan.Workflow.TemplateKey))
        {
            AssistantCardCopy.Offer(result.ToJsonString());
            AssistantMarketplaceTools.FenceProposal(result, "sourceWorkflowName", "name", "steps");
        }

        return result.ToJsonString();
    }

    private async Task<string> ApplyCloneWorkflowAsync(
        AssistantToolContext context, Guid? workflowId, string? name, Guid? targetProjectId, string? confirmationToken, Guid? projectId)
    {
        ClonePlan plan = await PlanCloneAsync(context, workflowId, name, targetProjectId, projectId);
        if (plan.Error is not null)
            return FenceIfCommunity(plan.Workflow, plan.Error);

        Project target = plan.Target!;
        if (!_confirmationTokens.Verify(OpCloneWorkflow, ClonePayload(plan), context.UserId, target.OrganizationId, confirmationToken ?? string.Empty))
            return StaleTokenErrorJson;

        var clone = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = plan.Name!,
            ProjectId = target.Id,
            RequiresUserInput = plan.Workflow!.RequiresUserInput,
            // The clone keeps the source's app manifest; the assistant made the copy, in this thread.
            AppManifestJson = plan.Workflow.AppManifestJson,
            // A clone of a community copy stays community (its prompts are still another user's words): the assistant's read tools
            // keep fencing it. Anything else is the assistant's own.
            CreatedVia = AssistantMarketplaceTools.CreatedViaForClone(plan.Workflow),
            CreatedInThreadId = context.ThreadId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        foreach (CreateWorkflowStepRequest step in plan.Steps!)
            clone.Steps.Add(_diffService.BuildStep(clone.Id, step));
        _db.WorkflowDefinitions.Add(clone);
        _db.SaveChanges();

        return FenceIfCommunity(plan.Workflow, JsonSerializer.Serialize(new
        {
            workflowId = clone.Id,
            workflowName = clone.Name,
            projectId = target.Id,
            projectName = target.Name,
            stepCount = plan.Steps.Count,
            clonedFromWorkflowId = plan.Workflow.Id,
            footageSharingPermissionTurnedOff = plan.EgressReset
        }));
    }

    // ------------------------------------------------------------------
    // Token payloads
    // ------------------------------------------------------------------

    private static string PatchPayload(
        Guid projectId, Guid workflowId, IReadOnlyList<CreateWorkflowStepRequest> before, IReadOnlyList<CreateWorkflowStepRequest> after) =>
        JsonSerializer.Serialize(new { op = OpPatchWorkflow, projectId, workflowId, before = Hash(before), after = Hash(after) });

    /// <summary>
    /// The payload of a plan: exactly the steps payload when the app page does not change ; with a page change, the page stored now and the page to store are bound too.
    /// </summary>
    private static string PatchPayload(PatchPlan plan)
    {
        string steps = PatchPayload(plan.Project!.Id, plan.Workflow!.Id, plan.Before!, plan.After!);
        return plan.ManifestChanged
            ? JsonSerializer.Serialize(new { steps, pageBefore = AssistantAppInputs.Digest(plan.ManifestBeforeJson), pageAfter = AssistantAppInputs.Digest(plan.ManifestAfter) })
            : steps;
    }

    /// <summary>A digest of the COMPLETE step list (every field, every config), so any change to any of it moves the token.</summary>
    private static string Hash(IReadOnlyList<CreateWorkflowStepRequest> steps) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(steps, HashOptions)))).ToLowerInvariant();

    private static string Refusal(string message) => JsonSerializer.Serialize(new { error = message });
}
