using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The builder's draft app page and steps, to describe against what is stored (docs/design/app-inputs.md 29, the L6 narrowing).
/// <paramref name="AppManifest"/> is the page as Save would send it (null: the stored page); <paramref name="Steps"/> the draft
/// steps (null or empty: the stored steps).
/// </summary>
public sealed record AppPageChangesRequest(AppManifest? AppManifest = null, List<CreateWorkflowStepRequest>? Steps = null);

/// <summary>What saving the draft changes on the app page, one plain sentence per line (empty when nothing changes).</summary>
public sealed record AppPageChangesResponse(IReadOnlyList<string> Lines);

public sealed partial class WorkflowAppsController
{
    /// <summary>
    /// "What changes" before Save (docs/design/app-inputs.md 29): the same sentences the assistant's approval card shows
    /// (<see cref="AssistantAppInputs.DescribeChanges"/>): fields added, changed or removed, the title, the request text, and which
    /// settings start or stop using a field. Writes nothing. Edit permission; a workflow that is not a Standard one of this project
    /// is a 404.
    /// </summary>
    [HttpPost("workflows/{id:guid}/app/changes")]
    public async Task<ActionResult<AppPageChangesResponse>> DescribeAppChanges(
        Guid projectId, Guid id, [FromBody] AppPageChangesRequest? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow is null) return NotFound();

        AppManifest? stored = AppManifestJson.TryParse(workflow.AppManifestJson);
        AppManifest? after = body?.AppManifest ?? stored;
        IReadOnlyList<AppBindingStep> beforeSteps = StepConfigSaveValidator.BindingStepsOf(
            workflow.Steps.OrderBy(s => s.StepOrder).Select(AssistantStepShapes.FromStep));
        IReadOnlyList<AppBindingStep> afterSteps = body?.Steps is { Count: > 0 } draft
            ? StepConfigSaveValidator.BindingStepsOf(draft)
            : beforeSteps;
        IReadOnlyDictionary<Guid, string> names = await AssistantAppInputs.ModelNamesAsync(
            _db, AssistantAppInputs.ManifestModelIds(stored, after), ct);
        return Ok(new AppPageChangesResponse(AssistantAppInputs.DescribeChanges(stored, beforeSteps, after, afterSteps, names)));
    }
}
