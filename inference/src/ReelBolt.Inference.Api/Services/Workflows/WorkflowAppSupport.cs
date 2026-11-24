using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// The glue between stored or requested workflow steps and the pure app logic in the shared project: building
/// <see cref="AppStepFacts"/> (which needs the agent types a step refers to) and the save-time manifest checks every
/// write path shares, so REST, the builder and the assistant refuse the same manifests with the same words.
/// </summary>
public static class WorkflowAppSupport
{
    public const string InvalidCode = "APP_MANIFEST_INVALID";

    /// <summary>The facts of stored steps, in step order. Reads agent types through the tenant-filtered agent table.</summary>
    public static async Task<IReadOnlyList<AppStepFacts>> FactsForStepsAsync(
        InferenceApiDbContext db, IEnumerable<WorkflowStep> steps, CancellationToken ct)
    {
        List<WorkflowStep> ordered = steps.OrderBy(s => s.StepOrder).ToList();
        Dictionary<Guid, AgentType> types = await AgentTypesAsync(db, ordered.Select(s => s.AgentDefinitionId), ct);
        return ordered.Select(s => AppStepFacts.From(
            s.StepType, types.TryGetValue(s.AgentDefinitionId, out AgentType t) ? t : null, s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson)).ToList();
    }

    /// <summary>The facts of the steps of a create or update request.</summary>
    public static async Task<IReadOnlyList<AppStepFacts>> FactsForRequestAsync(
        InferenceApiDbContext db, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        List<CreateWorkflowStepRequest> ordered = steps.OrderBy(s => s.StepOrder).ToList();
        Dictionary<Guid, AgentType> types = await AgentTypesAsync(db, ordered.Select(s => s.AgentDefinitionId), ct);
        return ordered.Select(s => AppStepFacts.From(
            Enum.TryParse(s.StepType, out StepType stepType) ? stepType : StepType.Agent,
            types.TryGetValue(s.AgentDefinitionId, out AgentType t) ? t : null,
            s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson)).ToList();
    }

    /// <summary>
    /// The save-time check. A supplied manifest is sanitized and fully validated against the steps it will live with;
    /// with none supplied, the manifest already stored is re-checked for exposed inputs the new steps would orphan.
    /// Returns the manifest to store (null = leave the stored one alone) and the refusal, if any.
    /// </summary>
    /// <param name="bindingSteps">
    /// The steps in the app-input shape (docs/design/app-inputs.md 11.1); null refuses every
    /// app-input member. A stored manifest with app inputs is re-checked
    /// against the new steps (an edit that removes the last consumer of an input is refused, the WL018 rule extended to
    /// inputs), and steps that bind inputs with no manifest at all are checked against an empty one (every input they
    /// name is undeclared).
    /// </param>
    public static (AppManifest? Sanitized, IReadOnlyList<string> Errors) CheckForSave(
        AppManifest? supplied, string? storedManifestJson, IReadOnlyList<AppStepFacts> steps,
        IReadOnlyList<AppBindingStep>? bindingSteps = null)
    {
        if (supplied is not null)
        {
            AppManifest clean = AppManifestValidator.Sanitize(supplied);
            return (clean, AppManifestValidator.Validate(clean, steps, bindingSteps));
        }

        AppManifest? stored = AppManifestJson.TryParse(storedManifestJson);
        if (bindingSteps is not null)
        {
            if (stored is not null && AppInputValueValidator.UsesAppInputs(stored, []))
                return (null, AppManifestValidator.Validate(stored, steps, bindingSteps));
            if (stored is null && AppInputValueValidator.UsesAppInputs(null, bindingSteps))
                return (null, AppManifestValidator.Validate(new AppManifest(), steps, bindingSteps));
        }

        List<string> orphans = AppManifestValidator.FindOrphans(stored, steps).Select(o => o.Message).ToList();
        return (null, orphans);
    }

    /// <summary>
    /// The WL018 check for assistant propose paths (synchronous like the rest of the propose tools): the stored manifest
    /// of <paramref name="workflowId"/>, if any, against the proposed steps. Empty when nothing would be orphaned.
    /// </summary>
    public static IReadOnlyList<string> OrphanErrors(InferenceApiDbContext db, Guid workflowId, IEnumerable<CreateWorkflowStepRequest> steps)
    {
        string? stored = db.WorkflowDefinitions.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.AppManifestJson).FirstOrDefault();
        if (stored is null)
            return [];

        List<CreateWorkflowStepRequest> ordered = steps.OrderBy(s => s.StepOrder).ToList();
        Guid[] ids = ordered.Select(s => s.AgentDefinitionId).Distinct().ToArray();
        Dictionary<Guid, AgentType> types = db.AgentDefinitions.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionary(a => a.Id, a => a.AgentType);
        List<AppStepFacts> facts = ordered.Select(s => AppStepFacts.From(
            Enum.TryParse(s.StepType, out StepType stepType) ? stepType : StepType.Agent,
            types.TryGetValue(s.AgentDefinitionId, out AgentType t) ? t : null,
            s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson)).ToList();
        return AppManifestValidator.FindOrphans(AppManifestJson.TryParse(stored), facts).Select(o => o.Message).ToList();
    }

    /// <summary>The refusal body for a failed manifest check.</summary>
    public static object ErrorBody(IReadOnlyList<string> errors) => new
    {
        code = InvalidCode,
        message = string.Join(" ", errors),
        errors
    };

    private static async Task<Dictionary<Guid, AgentType>> AgentTypesAsync(
        InferenceApiDbContext db, IEnumerable<Guid> agentIds, CancellationToken ct)
    {
        Guid[] ids = agentIds.Distinct().ToArray();
        return await db.AgentDefinitions.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AgentType, ct);
    }
}
