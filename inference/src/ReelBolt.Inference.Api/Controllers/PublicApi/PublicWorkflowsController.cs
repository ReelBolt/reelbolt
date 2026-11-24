using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.PublicApi.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary>
/// <c>/api/public/v1/projects/{projectId}/workflows</c>: Standard workflows only (the hidden composition
/// and snapshot kinds do not exist here, exactly as in the dashboard list). Create applies a template through
/// the dashboard's own provisioning path, so the template's step defaults (egress consent off) are kept.
/// </summary>
[Route("api/public/v1/projects/{projectId:guid}/workflows")]
public sealed class PublicWorkflowsController : PublicResourceControllerBase
{
    private const int MaxNameLength = 200;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly WorkflowTemplateProvisioningService _templates;
    private readonly IProductEventEmitter? _productEvents;

    public PublicWorkflowsController(
        InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        WorkflowTemplateProvisioningService templates, IProductEventEmitter? productEvents = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _templates = templates;
        _productEvents = productEvents;
    }

    [RequireScope(ApiScopes.WorkflowsRead)]
    [HttpGet]
    [ProducesResponseType(typeof(PublicApiPage<PublicWorkflowView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> List(Guid projectId, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        int take = PublicApiPagination.NormalizeLimit(limit);
        IQueryable<WorkflowDefinition> query = _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!PublicApiPagination.TryDecodeCursor(cursor, out DateTime at, out Guid after))
                return InvalidRequest("The cursor is not valid.");
            query = query.Where(w => w.CreatedAt < at || (w.CreatedAt == at && w.Id.CompareTo(after) < 0));
        }

        List<WorkflowDefinition> rows = await query.Include(w => w.Steps)
            .OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id).Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        string? next = more ? PublicApiPagination.EncodeCursor(rows[^1].CreatedAt, rows[^1].Id) : null;

        Dictionary<Guid, AgentType> agentTypes = await AgentTypesAsync(rows, ct);
        return Ok(new PublicApiPage<PublicWorkflowView>([.. rows.Select(w => ToView(w, agentTypes))], next));
    }

    [RequireScope(ApiScopes.WorkflowsRead)]
    [HttpGet("{workflowId:guid}")]
    [ProducesResponseType(typeof(PublicWorkflowView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid projectId, Guid workflowId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        WorkflowDefinition? workflow = await LoadStandardAsync(projectId, workflowId, ct);
        if (workflow is null) return NotFoundError("The workflow was not found.");
        return Ok(ToView(workflow, await AgentTypesAsync([workflow], ct)));
    }

    [RequireScope(ApiScopes.WorkflowsWrite)]
    [Idempotent]
    [HttpPost]
    [ProducesResponseType(typeof(PublicWorkflowView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Create(Guid projectId, [FromBody] PublicCreateWorkflowRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        Project project = access.Project!;

        string templateKey = request?.TemplateKey?.Trim() ?? string.Empty;
        if (templateKey.Length == 0) return InvalidRequest("templateKey is required.");
        if (WorkflowTemplateCatalog.GetByKey(templateKey) is not { } chosen)
            return PublicError(StatusCodes.Status404NotFound, "template_not_found", "No workflow template has that key.");
        string? name = request!.Name?.Trim();
        if (name is { Length: > MaxNameLength }) return InvalidRequest($"name must be at most {MaxNameLength} characters.");

        WorkflowDefinition? workflow;
        try
        {
            workflow = await _templates.CreateFromTemplateAsync(projectId, templateKey, skipIfExists: false, ct);
        }
        catch (InvalidOperationException ex)
        {
            return PublicError(StatusCodes.Status409Conflict, "conflict", ex.Message);
        }

        if (workflow is null)
            return PublicError(StatusCodes.Status409Conflict, "conflict", "Template could not be applied.");

        workflow = await _db.WorkflowDefinitions.Include(w => w.Steps).FirstAsync(w => w.Id == workflow.Id, ct);
        if (!string.IsNullOrEmpty(name))
        {
            workflow.Name = name;
            workflow.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        // product-analytics: the template chosen, as the dashboard records it (the catalog's key, never caller text).
        if (_productEvents is not null)
            await _productEvents.AppendAsync(_currentUser.UserId, project.OrganizationId, ProductEventNames.TemplateChosen, chosen.Key, ct);

        return StatusCode(StatusCodes.Status201Created, ToView(workflow, await AgentTypesAsync([workflow], ct)));
    }

    [RequireScope(ApiScopes.WorkflowsWrite)]
    [HttpDelete("{workflowId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Delete(Guid projectId, Guid workflowId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.FirstOrDefaultAsync(
            w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow is null) return NotFoundError("The workflow was not found.");

        // Refused while a run is in flight; otherwise every past run is re-pointed to its own snapshot first.
        if (!await new WorkflowStepReplacer(_db).DeleteAsync(workflow, ct))
            return PublicRefusal(Conflict(new { code = WorkflowStepReplacer.RunActiveCode, message = WorkflowStepReplacer.RunActiveMessage }));
        return NoContent();
    }

    private Task<WorkflowDefinition?> LoadStandardAsync(Guid projectId, Guid workflowId, CancellationToken ct) =>
        _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);

    private async Task<Dictionary<Guid, AgentType>> AgentTypesAsync(IEnumerable<WorkflowDefinition> workflows, CancellationToken ct)
    {
        Guid[] ids = [.. workflows.SelectMany(w => w.Steps.Select(s => s.AgentDefinitionId)).Distinct()];
        return await _db.AgentDefinitions.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.AgentType, ct);
    }

    private static PublicWorkflowView ToView(WorkflowDefinition w, IReadOnlyDictionary<Guid, AgentType> agentTypes)
    {
        List<WorkflowStep> steps = [.. w.Steps.OrderBy(s => s.StepOrder)];
        IReadOnlyList<AppStepFacts> facts = [.. steps.Select(s => AppStepFacts.FromStep(
            s, agentTypes.TryGetValue(s.AgentDefinitionId, out AgentType t) ? t : null))];
        AppManifest manifest = AppManifestDeriver.Effective(
            AppManifestDeriver.UpgradeStored(AppManifestJson.TryParse(w.AppManifestJson), w.TemplateKey),
            facts, w.RequiresUserInput, w.Name, null);
        // The permission to send media is one of the app's fields wherever a step sends something (docs/public-api.md).
        manifest = MediaEgressPermission.WithField(manifest, [.. steps.Select(AppBindingStep.From)])!;

        return new PublicWorkflowView(
            Id(w.Id), w.Name, manifest.Tagline, w.TemplateKey,
            [.. steps.Select(s => new PublicWorkflowStepView(
                s.StepOrder, Camel(s.StepType.ToString()), string.IsNullOrWhiteSpace(s.Label) ? Camel(s.StepType.ToString()) : s.Label))],
            [.. (manifest.Inputs ?? []).Select(i => new PublicAppInputView(
                i.Kind, i.Field, i.Role, i.Label, i.Min, i.Max, i.Id, i.Type, i.Required,
                i.Options is { Count: > 0 } o ? [.. o.Select(x => new PublicAppInputOptionView(x.Value, x.Label))] : null, i.Unit,
                i.Default, i.Help, i.Example, i.MaxLength, i.Step))],
            AppCostClass.Derive(facts), Utc(w.CreatedAt), Utc(w.UpdatedAt),
            PublicAppInputSchema.Build(manifest, AppBindingAnalysis.Analyze(manifest, [.. steps.Select(AppBindingStep.From)])));
    }
}
