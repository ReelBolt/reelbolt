using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// One provider reference that a project transfer would strand: the step that names it and the name
/// of the row it names, so the 409 body can list exactly what the author must repoint.
/// </summary>
public sealed record DanglingProviderReference(
    Guid WorkflowId,
    string WorkflowName,
    int StepOrder,
    StepType StepType,
    string Field,
    Guid ProviderId,
    string ProviderName);

/// <summary>
/// A10: the save-time half of "no workflow can reference another organization's provider".
///
/// <para>
/// Every step config field that names an inference provider (see
/// <see cref="StepProviderReferences"/>) must name a row the PROJECT'S organization can actually
/// see — either a platform-managed row (<c>organization_id IS NULL</c>, shared by every tenant) or
/// one of that organization's own rows. Anything else is refused here, before the write.
/// </para>
///
/// <para>
/// <b>Decision D5 governs the message.</b> A provider id that is not visible to the project's
/// organization is reported as one thing regardless of WHY it is invisible — another organization's
/// row, a deleted row, a row the author typo'd. Splitting those into distinct errors would turn this
/// endpoint into an existence oracle for other tenants' provider ids, which is exactly what D5
/// forbids. The row is looked up with the tenant query filter DROPPED and an explicit
/// <c>OrganizationId</c> comparison instead: the answer must be a property of the project's
/// organization, never of whichever organization happens to be ambient on the request.
/// </para>
///
/// <para>
/// This is a SECOND, independent enforcement behind the resolver's own fail-closed behaviour
/// (A9a: an explicit id that is not in the resolving organization's snapshot is treated as missing
/// and counted as <c>reelbolt.inference.foreign_provider_ref</c>). Save-time refusal is what turns
/// that silent fall-through into a message the author can act on; it is not the bound, and it must
/// never be the only check.
/// </para>
///
/// <para>
/// Deliberately ownership-only: a VISIBLE row whose capability does not match the field is left to
/// the resolver, which already treats it as a silent miss and falls through to that capability's
/// default (a long-standing defense in depth). Refusing it here would tighten what already saves.
/// </para>
/// </summary>
public sealed class StepProviderOwnershipValidator
{
    private readonly InferenceApiDbContext _db;

    public StepProviderOwnershipValidator(InferenceApiDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The formatted errors for <paramref name="steps"/> saved into <paramref name="projectId"/> (a project
    /// of <paramref name="organizationId"/>); empty when every reference is usable.
    /// </summary>
    /// <remarks>
    /// Besides provider ownership this runs the two cross-run reference rules, both scoped to the
    /// PROJECT (T-014): a continue-composition source (<see cref="StepOptionsSaveValidator"/>) and a
    /// <c>StepResult</c> video source (<see cref="StepConfigSaveValidator.ValidateStepResultSourcesAsync"/>).
    /// There is deliberately no organization-only overload: a sibling project of the same organization is
    /// as foreign as another tenant, and a caller that does not know the project must not save at all.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ValidateAsync(
        Guid organizationId, Guid projectId, IReadOnlyList<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        HashSet<Guid> visible = [.. (await LoadVisibleAsync(organizationId, ct)).Keys];
        HashSet<Guid> lipSync = [.. await LipSyncRows(organizationId).ToListAsync(ct)];
        List<Guid> named = [.. AgentReferences(steps).SelectMany(s => s.References).Select(r => r.AgentDefinitionId).Distinct()];
        HashSet<Guid> visibleAgents = named.Count == 0
            ? []
            : [.. await VisibleAgents(organizationId, named).ToListAsync(ct)];
        return
        [
            .. Check(steps, visible, lipSync),
            .. CheckAgents(steps, visibleAgents),
            .. await StepOptionsSaveValidator.ValidateReferencesAsync(_db, organizationId, projectId, steps, ct),
            .. await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
                steps, (id, token) => StepConfigSaveValidator.StepResultSourceVerdictOfAsync(_db, projectId, id, token), ct),
            .. await VoiceoverVoiceConsentSaveValidator.ValidateAsync(_db, projectId, steps, ct),
            .. await VideoGenerateAvatarSaveValidator.ValidateAsync(_db, projectId, steps, ct)
        ];
    }

    /// <summary>
    /// The synchronous twin of <see cref="ValidateAsync"/>, for
    /// <see cref="WorkflowEditDiffService.ValidateProposedSteps"/>, whose step/agent lookups are
    /// already synchronous against the same request-scoped <see cref="InferenceApiDbContext"/>.
    /// </summary>
    public IReadOnlyList<string> Validate(Guid organizationId, Guid projectId, IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        HashSet<Guid> visible = [.. LoadVisible(organizationId).Keys];
        HashSet<Guid> lipSync = [.. LipSyncRows(organizationId).ToList()];
        List<Guid> named = [.. AgentReferences(steps).SelectMany(s => s.References).Select(r => r.AgentDefinitionId).Distinct()];
        HashSet<Guid> visibleAgents = named.Count == 0
            ? []
            : [.. VisibleAgents(organizationId, named).ToList()];
        return
        [
            .. Check(steps, visible, lipSync),
            .. CheckAgents(steps, visibleAgents),
            .. StepOptionsSaveValidator.ValidateReferences(_db, organizationId, projectId, steps),
            .. StepConfigSaveValidator.ValidateStepResultSources(steps, id => StepConfigSaveValidator.StepResultSourceVerdictOf(_db, projectId, id)),
            .. VoiceoverVoiceConsentSaveValidator.Validate(_db, projectId, steps),
            .. VideoGenerateAvatarSaveValidator.Validate(_db, projectId, steps)
        ];
    }

    /// <summary>
    /// SQFU-S3: the agents a step's CONFIG names (a Parallel step's extra agents, a room's seat and
    /// director agents; <see cref="StepAgentReferences"/>) obey the same rule as providers — a platform
    /// built-in or one of the project's organization's own agents. Same D5 discipline: tenant filter
    /// dropped, explicit organization comparison, and one message whether the row is another tenant's
    /// or does not exist. (The step's own <c>AgentDefinitionId</c> is checked by the callers' A8 check.)
    /// </summary>
    private IQueryable<Guid> VisibleAgents(Guid organizationId, List<Guid> ids) =>
        _db.AgentDefinitions
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(a => ids.Contains(a.Id) && (a.IsBuiltIn || a.OrganizationId == organizationId))
            .Select(a => a.Id);

    private static IEnumerable<(CreateWorkflowStepRequest Step, StepType StepType, IReadOnlyList<StepAgentReference> References)> AgentReferences(
        IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        foreach (CreateWorkflowStepRequest step in steps)
        {
            if (step.StepType is null || !Enum.TryParse<StepType>(step.StepType, out StepType stepType))
                continue;

            yield return (step, stepType, StepAgentReferences.Extract(
                stepType,
                step.ParallelAgentIdsJson,
                step.EditRoomConfigJson,
                step.GraphicsRoomConfigJson,
                step.ColorGradeRoomConfigJson,
                step.DailiesRoomConfigJson));
        }
    }

    private static IEnumerable<string> CheckAgents(IReadOnlyList<CreateWorkflowStepRequest> steps, HashSet<Guid> visibleAgents)
    {
        foreach ((CreateWorkflowStepRequest step, StepType stepType, IReadOnlyList<StepAgentReference> references) in AgentReferences(steps))
        {
            foreach (StepAgentReference reference in references)
            {
                if (!visibleAgents.Contains(reference.AgentDefinitionId))
                    yield return $"Step {step.StepOrder} ({stepType}): {reference.Field} references an agent that is not available to this organization.";
            }
        }
    }

    /// <summary>
    /// The provider references in <paramref name="projectId"/>'s stored workflows that moving the
    /// project to <paramref name="targetOrganizationId"/> would strand — i.e. those naming a row that
    /// belongs to neither the platform nor the target organization. Empty means the transfer can
    /// proceed without repointing anything.
    /// </summary>
    /// <remarks>
    /// A reference to a row the TARGET organization owns is not dangling: after the move that row is
    /// the project's own. A reference to a PLATFORM row is never dangling: platform rows are visible
    /// to every organization, including the target's. One row named by several steps is reported once,
    /// against the first step that names it, because that is the step an operator would edit.
    /// </remarks>
    public async Task<IReadOnlyList<DanglingProviderReference>> FindDanglingAsync(
        Guid projectId, Guid targetOrganizationId, CancellationToken ct)
    {
        List<WorkflowStep> steps = await _db.WorkflowSteps
            .AsNoTracking()
            .Include(s => s.WorkflowDefinition)
            .Where(s => s.WorkflowDefinition.ProjectId == projectId)
            .OrderBy(s => s.WorkflowDefinitionId)
            .ThenBy(s => s.StepOrder)
            .ToListAsync(ct);

        Dictionary<Guid, string> stillUsable = await LoadVisibleAsync(targetOrganizationId, ct);
        Dictionary<Guid, string> everyProvider = await LoadEveryProviderAsync(ct);

        List<DanglingProviderReference> dangling = [];
        HashSet<Guid> reported = [];

        foreach (WorkflowStep step in steps)
        {
            foreach (StepProviderReference reference in References(step))
            {
                if (stillUsable.ContainsKey(reference.ProviderId) || !reported.Add(reference.ProviderId))
                    continue;

                dangling.Add(new DanglingProviderReference(
                    step.WorkflowDefinitionId,
                    step.WorkflowDefinition?.Name ?? string.Empty,
                    step.StepOrder,
                    step.StepType,
                    reference.Field,
                    reference.ProviderId,
                    everyProvider.GetValueOrDefault(reference.ProviderId, string.Empty)));
            }
        }

        return dangling;
    }

    /// <summary>
    /// Ids and names of the MODEL CAPABILITIES visible to <paramref name="organizationId"/> (a step's
    /// "provider id" names one, docs/design/model-catalog.md §3) — platform-managed rows plus that
    /// organization's own, named by their model's display name. The tenant query filter is dropped so the
    /// answer depends only on the organization being asked about, and the explicit comparison is the
    /// enforcement.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadVisibleAsync(Guid organizationId, CancellationToken ct) =>
        await _db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(c => c.OrganizationId == null || c.OrganizationId == organizationId)
            .Select(c => new { c.Id, c.Model.DisplayName })
            .ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);

    private Dictionary<Guid, string> LoadVisible(Guid organizationId) =>
        _db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(c => c.OrganizationId == null || c.OrganizationId == organizationId)
            .Select(c => new { c.Id, c.Model.DisplayName })
            .ToDictionary(c => c.Id, c => c.DisplayName);

    /// <summary>
    /// Every model capability, name only — used to name the stranded row in a transfer refusal. The name
    /// of a row the caller cannot use as a provider is not a secret: it is already the operator's own
    /// project configuration, echoed back to them, and the id alone would be unactionable.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadEveryProviderAsync(CancellationToken ct) =>
        await _db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Select(c => new { c.Id, c.Model.DisplayName })
            .ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);

    /// <summary>The ids of the Lip-sync model capabilities visible to the organization: a talking head's provider must be one of them.</summary>
    private IQueryable<Guid> LipSyncRows(Guid organizationId) =>
        _db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(c => c.Capability == InferenceProviderCapability.LipSync && (c.OrganizationId == null || c.OrganizationId == organizationId))
            .Select(c => c.Id);

    private static IReadOnlyList<string> Check(IReadOnlyList<CreateWorkflowStepRequest> steps, HashSet<Guid> visible, HashSet<Guid> lipSync)
    {
        List<string> errors = [];

        foreach (CreateWorkflowStepRequest step in steps)
        {
            if (step.StepType is null || !Enum.TryParse<StepType>(step.StepType, out StepType stepType))
                continue;

            foreach (StepProviderReference reference in StepProviderReferences.Extract(
                stepType,
                step.ConditionalConfigJson,
                step.ReviewLoopConfigJson,
                step.VideoAnalyzeConfigJson,
                step.VideoGenerateConfigJson,
                step.VoiceoverConfigJson,
                step.EditRoomConfigJson,
                step.GraphicsRoomConfigJson,
                step.ColorGradeRoomConfigJson,
                step.AudioGenerateConfigJson,
                step.AudioCleanupConfigJson,
                step.VideoEnhanceConfigJson,
                step.ImageGenerateConfigJson))
            {
                // A talking head's provider is a Lip-sync row: a video-generation row (or any other) is refused under the same message.
                if (visible.Contains(reference.ProviderId)
                    && (reference.Capability != InferenceProviderCapability.LipSync || lipSync.Contains(reference.ProviderId)))
                    continue;

                errors.Add(
                    $"Step {step.StepOrder} ({stepType}): {reference.Field} references an inference provider " +
                    "that is not available to this organization.");
            }
        }

        return errors;
    }

    private static IReadOnlyList<StepProviderReference> References(WorkflowStep step) =>
        StepProviderReferences.Extract(
            step.StepType,
            step.ConditionalConfigJson,
            step.ReviewLoopConfigJson,
            step.VideoAnalyzeConfigJson,
            step.VideoGenerateConfigJson,
            step.VoiceoverConfigJson,
            step.EditRoomConfigJson,
            step.GraphicsRoomConfigJson,
            step.ColorGradeRoomConfigJson,
            step.AudioGenerateConfigJson,
            step.AudioCleanupConfigJson,
            step.VideoEnhanceConfigJson,
            step.ImageGenerateConfigJson);
}
