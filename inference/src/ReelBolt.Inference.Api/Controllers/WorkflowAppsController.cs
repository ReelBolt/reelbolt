using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>The last run of an app, as a card shows it.</summary>
public record WorkflowAppLastRun(string Status, DateTime? FinishedAt);

/// <summary>One of the project's workflows, presented as an app card.</summary>
public record WorkflowAppCard(
    Guid WorkflowId, string Title, string? Tagline, string Category, string CostClass, int InputsCount,
    WorkflowAppLastRun? LastRun, string Source);

/// <summary>One thing a recipe still needs from the project, with the action that fixes it.</summary>
public record WorkflowAppMissing(string Need, string Message, string Action, string? Detail = null);

/// <summary>A template the project can start from, with whether the project is ready for it.</summary>
/// <param name="ModelInputs">
/// How many model fields the page the recipe would be created with has (docs/design/app-inputs.md 28): the gallery offers
/// "Model choices" for those through <c>GET .../workflow-apps/recipes/{key}/model-options</c>.
/// </param>
public record WorkflowRecipeCard(
    string Key, string Title, string? Tagline, string Category, string CostClass, int InputsCount,
    string Fit, List<WorkflowAppMissing> Missing, int ModelInputs = 0);

/// <summary>The gallery in one response: the project's apps and the recipe list.</summary>
public record WorkflowAppsResponse(List<WorkflowAppCard> Apps, List<WorkflowRecipeCard> Recipes);

/// <summary>The effective app manifest of one workflow (the author's over the derived), with its cost class.</summary>
/// <remarks><see cref="BindableFields"/> is <see cref="StepCatalog.BindableFieldsOf"/> per step type that has any: the settings the
/// builder offers to fill from an app field (docs/design/app-inputs.md 13.1, 24).</remarks>
public record WorkflowAppResponse(
    Guid WorkflowId, string Name, AppManifest Manifest, bool HasExplicitManifest, string CostClass, string Source,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, IReadOnlyList<StepCardBindableField>>? BindableFields = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    WorkflowAppMediaEgress? MediaEgress = null);

/// <summary>
/// The permission to send media as the app page shows it (docs/design/app-inputs.md "Permission to send media as an app
/// field"): the field's id, whether a run needs it, whether every step already allows it, and the steps with the reason
/// codes the page words in the person's language. Absent when no step sends anything.
/// </summary>
/// <param name="Required">A run with the page's default choices cannot start without it.</param>
/// <param name="DependsOnChoices">
/// True when a field of the page chooses a branch that changes whether it is needed: the page then follows the pre-run
/// check for the current choices (WL015), and the run gate has the last word.
/// </param>
public record WorkflowAppMediaEgress(
    string InputId, bool Required, bool AlreadyAllowed, IReadOnlyList<WorkflowAppMediaEgressStep> Steps, bool DependsOnChoices = false)
{
    /// <summary><paramref name="summary"/> covers every step; <paramref name="asOpened"/> the steps reached with the default choices.</summary>
    public static WorkflowAppMediaEgress? From(MediaEgressSummary summary, MediaEgressSummary? asOpened = null) => !summary.Present
        ? null
        : new(MediaEgressPermission.InputId, (asOpened ?? summary).Required, summary.AlreadyAllowed,
            [.. summary.Steps.Select(s => new WorkflowAppMediaEgressStep(
                s.StepOrder, MediaEgressPermission.StepName(s), s.StepType.ToString(), s.Reason ?? string.Empty,
                s.Level == MediaEgressNeedLevel.Required ? "required" : "optional", s.StoredOn))],
            asOpened is not null && asOpened.Required != summary.Required);
}

/// <summary>One step that sends media: its name, why (<c>reason</c>, a closed code), and whether its own switch is already on.</summary>
public record WorkflowAppMediaEgressStep(int StepOrder, string StepName, string StepType, string Reason, string Need, bool AllowedInStep);

/// <summary>
/// The app view of workflows (docs/workflow-apps.md): the gallery of a project's apps plus recipes, and the effective
/// manifest of one workflow. View permission; another organization's project or workflow is a 404.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}")]
[Authorize]
public sealed partial class WorkflowAppsController : ControllerBase
{
    public const string SourceCustom = "custom";

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly WorkflowTemplateProvisioningService _templates;
    private readonly IProjectReadinessFacts _readiness;
    private readonly IOrganizationEntitlements? _entitlements;

    public WorkflowAppsController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        WorkflowTemplateProvisioningService templates,
        IProjectReadinessFacts readiness,
        IOrganizationEntitlements? entitlements = null)
    {
        _entitlements = entitlements;
        _db = db;
        _projectAccess = projectAccess;
        _templates = templates;
        _readiness = readiness;
    }

    [GeneratedRegex(@" v\d+$")]
    private static partial Regex VersionSuffix();

    /// <summary>One request for the whole gallery: apps with their last run, recipes with their fit. No per-card fan-out.</summary>
    [HttpGet("workflow-apps")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<WorkflowAppsResponse>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        List<WorkflowDefinition> workflows = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .Where(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard)
            .ToListAsync(ct);

        Dictionary<Guid, AgentType> agentTypes = await AgentTypesAsync(workflows.SelectMany(w => w.Steps.Select(s => s.AgentDefinitionId)), ct);
        Guid[] workflowIds = workflows.Select(w => w.Id).ToArray();
        // One row per workflow, chosen in the database: the newest run by when it was queued, so a queued run counts.
        var lastRuns = (await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.ProjectId == projectId && workflowIds.Contains(e.WorkflowDefinitionId))
            .GroupBy(e => e.WorkflowDefinitionId)
            .Select(g => g.OrderByDescending(e => e.QueuedAt ?? e.StartedAt)
                .Select(e => new { e.WorkflowDefinitionId, e.Status, e.StartedAt, e.CompletedAt, e.QueuedAt })
                .First())
            .ToListAsync(ct))
            .ToDictionary(r => r.WorkflowDefinitionId);

        IReadOnlyList<WorkflowTemplateDefinition> templates = await _templates.ListRecipesAsync(ct);

        List<(WorkflowAppCard Card, DateTime Recency)> apps = [];
        foreach (WorkflowDefinition workflow in workflows)
        {
            IReadOnlyList<AppStepFacts> facts = FactsOf(workflow.Steps, agentTypes);
            string source = SourceOf(workflow);
            AppManifest manifest = AppManifestDeriver.Effective(
                AppManifestDeriver.UpgradeStored(AppManifestJson.TryParse(workflow.AppManifestJson), workflow.TemplateKey), facts, workflow.RequiresUserInput,
                workflow.Name, TaglineFallback(source));
            WorkflowAppLastRun? last = lastRuns.TryGetValue(workflow.Id, out var run)
                ? new WorkflowAppLastRun(run.Status.ToString(), run.CompletedAt)
                : null;
            apps.Add((new WorkflowAppCard(
                workflow.Id, manifest.Title!, manifest.Tagline, manifest.Category!, AppCostClass.Derive(facts),
                manifest.Inputs?.Count ?? 0, last, source),
                lastRuns.TryGetValue(workflow.Id, out var r) ? r.QueuedAt ?? r.StartedAt ?? workflow.CreatedAt : workflow.CreatedAt));
        }

        ProjectReadinessSnapshot snapshot = await _readiness.LoadAsync(project, ct);
        OrganizationEntitlementSet? plan = _entitlements is null ? null : await _entitlements.GetAsync(project.OrganizationId, ct);
        List<WorkflowRecipeCard> recipes = [];
        foreach (WorkflowTemplateDefinition template in templates)
        {
            IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(template);
            AppManifest manifest = AppManifestDeriver.Effective(
                template.AppManifest, facts, template.RequiresUserInput, template.Name, FirstSentence(template.Description));
            List<WorkflowAppMissing> missing = ProjectReadinessFacts.Evaluate(facts, snapshot, RequiresProfile(template))
                .Select(m => new WorkflowAppMissing(m.Need, m.Message, m.Action, m.Detail)).ToList();
            // A recipe the plan would refuse is not ready, whatever the project holds (same facts as the lint).
            if (plan is not null)
                missing.AddRange(RecipeFit.PlanNeeds(template, plan).Select(m => new WorkflowAppMissing(m.Need, m.Message, m.Action)));
            recipes.Add(new WorkflowRecipeCard(
                template.Key, manifest.Title!, manifest.Tagline, manifest.Category!, AppCostClass.Derive(facts),
                manifest.Inputs?.Count ?? 0, missing.Count == 0 ? "ready" : "needs", missing,
                (manifest.Inputs ?? []).Count(i => i.Kind == AppManifestVocabulary.KindInput
                    && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model)));
        }

        return Ok(new WorkflowAppsResponse(
            apps.OrderByDescending(a => a.Recency).Select(a => a.Card).ToList(), recipes));
    }

    /// <summary>The effective manifest of one workflow: the author's members over the derived ones, defaults filled.</summary>
    [HttpGet("workflows/{id:guid}/app")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<WorkflowAppResponse>> Get(
        Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow is null) return NotFound();

        Dictionary<Guid, AgentType> agentTypes = await AgentTypesAsync(workflow.Steps.Select(s => s.AgentDefinitionId), ct);
        IReadOnlyList<AppStepFacts> facts = FactsOf(workflow.Steps, agentTypes);
        string source = SourceOf(workflow);
        AppManifest? stored = AppManifestDeriver.UpgradeStored(AppManifestJson.TryParse(workflow.AppManifestJson), workflow.TemplateKey);
        AppManifest effective = AppManifestDeriver.Effective(stored, facts, workflow.RequiresUserInput, workflow.Name, TaglineFallback(source));
        // The permission to send media is added to every page read, never stored: a workflow with no such step is untouched.
        IReadOnlyList<AppBindingStep> bindingSteps = [.. workflow.Steps.OrderBy(s => s.StepOrder).Select(AppBindingStep.From)];
        MediaEgressSummary egress = MediaEgressPermission.Describe(bindingSteps, MediaEgressPermission.AvatarOf(effective));
        effective = MediaEgressPermission.WithField(effective, bindingSteps)!;
        // Required as the page opens: only steps reached with the page's default choices count (a field may pick a branch).
        MediaEgressSummary asOpened = egress;
        if (egress.Present)
        {
            IReadOnlySet<int> reach = StaticBranchResolver.ReachableStepOrders(
                bindingSteps, AppInputValueChecker.Check(effective, bindingSteps, null).Values);
            asOpened = MediaEgressPermission.Describe(bindingSteps, MediaEgressPermission.AvatarOf(effective), reach);
            effective = effective with
            {
                Inputs = [.. (effective.Inputs ?? []).Select(i => MediaEgressPermission.IsField(i) ? i with { Required = asOpened.Required ? true : null } : i)]
            };
        }

        return Ok(new WorkflowAppResponse(workflow.Id, workflow.Name, effective, stored is not null, AppCostClass.Derive(facts), source,
            BindableFieldsByStepType(), WorkflowAppMediaEgress.From(egress, asOpened)));
    }

    /// <summary>The bindable settings of every step type that has any, keyed by the step type's name.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<StepCardBindableField>> BindableFieldsByStepType() =>
        Enum.GetValues<StepType>()
            .Select(t => (Type: t, Fields: StepCatalog.BindableFieldsOf(t)))
            .Where(x => x.Fields.Count > 0)
            .ToDictionary(x => x.Type.ToString(), x => x.Fields, StringComparer.Ordinal);

    /// <summary>
    /// The options of one <c>model</c> input (docs/design/app-inputs.md 8.2): the models a person may pick, recommended first,
    /// each with a relative price and whether the run would accept it. View permission; a workflow, input or project that is
    /// not there (another organization's included) is a 404.
    /// <paramref name="all"/> is the builder's view (Edit permission, docs/design/app-inputs.md 24): every model of the
    /// input's kind the workspace can select, the author's <c>allowed</c> list ignored, so "Allowed models" can widen it.
    /// </summary>
    [HttpGet("workflows/{id:guid}/app/inputs/{inputId}/options")]
    public async Task<ActionResult<AppInputModelOptionsResponse>> ModelOptions(
        Guid projectId, Guid id, string inputId, [FromServices] AppInputModelOptionsService options,
        CancellationToken ct, [FromQuery] bool all = false)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, all ? ProjectPermission.Edit : ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        AppInputModelOptionsResponse? response = await options.OptionsAsync(access.Project!.OrganizationId, projectId, id, inputId, ct, ignoreAllowed: all);
        return response is null ? NotFound() : Ok(response);
    }

    /// <summary>
    /// The model choices of a recipe the project has not created yet (docs/design/app-inputs.md 28, the gallery's recipe card):
    /// one entry per model field of the page it would be created with, judged and priced by the same options source as a created
    /// app (recommended first, a relative price, available or why not, a kind the plan lacks included). View permission; an
    /// unknown recipe or another organization's project is a 404; a recipe with no
    /// model field answers an empty list.
    /// </summary>
    [HttpGet("workflow-apps/recipes/{templateKey}/model-options")]
    public async Task<ActionResult<List<AppInputModelOptionsResponse>>> RecipeModelOptions(
        Guid projectId, string templateKey, [FromServices] AppInputModelOptionsService options, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (await _templates.BuildDryAppAsync(templateKey, ct) is not { } dry)
            return NotFound();
        IReadOnlyList<AppInputModelOptionsResponse> all = await options.AllOptionsAsync(access.Project!.OrganizationId, projectId, dry.Manifest, dry.Steps, ct);
        return Ok(all.ToList());
    }

    private async Task<Dictionary<Guid, AgentType>> AgentTypesAsync(IEnumerable<Guid> agentIds, CancellationToken ct)
    {
        Guid[] ids = agentIds.Distinct().ToArray();
        return await _db.AgentDefinitions.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AgentType, ct);
    }

    private static IReadOnlyList<AppStepFacts> FactsOf(IEnumerable<WorkflowStep> steps, IReadOnlyDictionary<Guid, AgentType> agentTypes) =>
        steps.OrderBy(s => s.StepOrder)
            .Select(s => AppStepFacts.From(s.StepType, agentTypes.TryGetValue(s.AgentDefinitionId, out AgentType t) ? t : null, s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson))
            .ToList();

    /// <summary>The template key a workflow was provisioned from (its name is "{template name} v{n}"), else "custom".</summary>
    private static string SourceOf(WorkflowDefinition workflow)
    {
        // Stored when the workflow is provisioned. Only rows that predate the column fall back to the name.
        if (workflow.TemplateKey is { } stored && WorkflowTemplateCatalog.GetByKey(stored) is not null)
            return stored;
        if (workflow.CreatedVia is not null)
            return SourceCustom;

        string baseName = VersionSuffix().Replace(workflow.Name.Trim(), string.Empty);
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.GetAll()
            .FirstOrDefault(t => string.Equals(t.Name, baseName, StringComparison.Ordinal));
        return template?.Key ?? SourceCustom;
    }

    private static string? TaglineFallback(string source) =>
        source == SourceCustom || WorkflowTemplateCatalog.GetByKey(source) is not { } template
            ? null
            : FirstSentence(template.Description);

    private static bool RequiresProfile(WorkflowTemplateDefinition template) =>
        template.Key is WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey or WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey;

    private static string? FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        string sentence = end > 0 ? text[..(end + 1)] : text;
        return sentence.Length <= AppManifestVocabulary.MaxTagline ? sentence : sentence[..(AppManifestVocabulary.MaxTagline - 1)].TrimEnd() + "…";
    }
}
