using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Restores a Standard workflow (contract section 8 step 5): the stored canonical document is turned
/// back into save requests and rebuilt through the same steps every editor save uses
/// (<see cref="IWorkflowEditDiffService.ValidateProposedSteps"/>, <see cref="StepProviderOwnershipValidator"/>,
/// <see cref="StepAgentAuthorization"/> and <c>BuildStep</c>), so a version can never reintroduce a step
/// the current rules refuse. A workflow that no longer passes is skipped whole — nothing is written.
/// <para>
/// A workflow deleted since the capture is recreated at its old id when that id is free, else at a new
/// one. A live workflow whose run is Queued/Running is refused (<c>WORKFLOW_RUN_ACTIVE</c>).
/// </para>
/// <para>
/// <c>allowSourceMediaEgress</c> is a customer consent, never an artefact of a version: a captured
/// step keeps it only where the live workflow has the same step (same order and type) with that same
/// config already granted. Everywhere else it is written false and the workflow reports
/// <c>EGRESS_CONSENT_RESET</c>.
/// </para>
/// </summary>
public sealed class WorkflowRestoreKindHandler : IProjectRestoreKindHandler
{
    /// <summary>The <c>*ConfigJson</c> properties a step's vendor-egress consent can live in.</summary>
    private static readonly PropertyInfo[] ConfigProperties = typeof(CreateWorkflowStepRequest)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string)
                    && p.Name.EndsWith("ConfigJson", StringComparison.Ordinal))
        .ToArray();

    private readonly InferenceApiDbContext _db;
    private readonly IWorkflowEditDiffService _diff;
    private readonly WorkflowStepReplacer _replacer;
    private readonly TimeProvider _clock;

    public WorkflowRestoreKindHandler(
        InferenceApiDbContext db, IWorkflowEditDiffService diff, TimeProvider? clock = null)
    {
        _db = db;
        _diff = diff;
        _replacer = new WorkflowStepReplacer(db);
        _clock = clock ?? TimeProvider.System;
    }

    public ProjectVersionItemKind Kind => ProjectVersionItemKind.Workflow;

    public async Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        if (!Guid.TryParse(item.ItemKey, out Guid workflowId))
            return Skip(item, ProjectHistoryRestoreReasons.ContentGone);

        string? json = await RestoreDocument.LoadAsync(_db, context.Project.Id, item.DocumentSha256, ct);
        if (RestoreDocument.Parse(json) is not JsonObject document)
            return Skip(item, ProjectHistoryRestoreReasons.ContentGone);

        JsonArray? steps = document["steps"] as JsonArray;
        if (steps is null)
            return Skip(item, ProjectHistoryRestoreReasons.WorkflowInvalid);

        string? name = RestoreDocument.GetString(document, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = item.Name;
        if (string.IsNullOrWhiteSpace(name))
            return Skip(item, ProjectHistoryRestoreReasons.WorkflowInvalid);

        // A run in flight must not have its workflow changed under it (contract section 8 step 2).
        if (await _replacer.HasActiveRunAsync(workflowId, ct))
            return Skip(item, ProjectHistoryRestoreReasons.WorkflowRunActive);

        // The row may be a live Standard workflow of this project, absent (a deleted one), or an id
        // already held by something else (a hidden system row or another project's workflow).
        WorkflowDefinition? occupant = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        bool isLiveStandard = occupant is not null
            && occupant.ProjectId == context.Project.Id
            && occupant.Kind == WorkflowDefinitionKind.Standard;

        List<CreateWorkflowStepRequest> proposed = new(steps.Count);
        foreach (JsonNode? node in steps)
        {
            if (node is not JsonObject step || ToRequest(step) is not { } request)
                return Skip(item, ProjectHistoryRestoreReasons.WorkflowInvalid);
            proposed.Add(request);
        }

        // Egress consent: only a live step at the same order and type may vouch for a captured true.
        List<WorkflowStep> liveSteps = isLiveStandard
            ? await _db.WorkflowSteps
                .AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == workflowId)
                .ToListAsync(ct)
            : [];
        bool egressReset = false;
        proposed = ApplyEgressConsent(proposed, liveSteps, out egressReset);

        // The same save-time rules every edit path runs, against the project as it is NOW.
        WorkflowValidationResult validation = _diff.ValidateProposedSteps(context.Project.Id, proposed);
        if (!validation.Valid)
            return Skip(item, ProjectHistoryRestoreReasons.WorkflowInvalid);

        if (!StepAgentAuthorization.AllUsable(_db, context.Project.OrganizationId, proposed))
            return Skip(item, ProjectHistoryRestoreReasons.WorkflowInvalid);

        List<string> ownership = [.. new StepProviderOwnershipValidator(_db)
            .Validate(context.Project.OrganizationId, context.Project.Id, proposed)];
        if (ownership.Count > 0)
            return Skip(item, ProjectHistoryRestoreReasons.WorkflowInvalid);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        string? notice = egressReset ? ProjectHistoryRestoreNotices.EgressConsentReset : null;

        if (isLiveStandard)
        {
            WorkflowDefinition workflow = occupant!;
            ApplyDefinition(workflow, document, name, now);
            List<WorkflowStep> built = proposed.Select(p => _diff.BuildStep(workflow.Id, p)).ToList();
            await _replacer.ReplaceStepsAsync(workflow, built, ct);
            return ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, name, notice);
        }

        // Deleted, or its id is taken: recreate at the old id when free, else at a new one.
        Guid id = occupant is null ? workflowId : Guid.NewGuid();
        WorkflowDefinition recreated = new()
        {
            Id = id,
            ProjectId = context.Project.Id,
            Kind = WorkflowDefinitionKind.Standard,
            CreatedAt = now
        };
        ApplyDefinition(recreated, document, name, now);
        foreach (CreateWorkflowStepRequest request in proposed)
            recreated.Steps.Add(_diff.BuildStep(id, request));

        _db.WorkflowDefinitions.Add(recreated);
        await _db.SaveChangesAsync(ct);
        return ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, name, notice);
    }

    public async Task<IReadOnlyList<ProjectRestoreOutcome>> RemoveAbsentAsync(
        ProjectRestoreWriteContext context, IReadOnlySet<string> presentKeys, CancellationToken ct)
    {
        // Hidden kinds (composition/design-graphic/system snapshots) are never a version's items and
        // are never removed here.
        List<WorkflowDefinition> live = await _db.WorkflowDefinitions
            .Where(w => w.ProjectId == context.Project.Id && w.Kind == WorkflowDefinitionKind.Standard)
            .ToListAsync(ct);

        List<ProjectRestoreOutcome> outcomes = new();
        foreach (WorkflowDefinition workflow in live)
        {
            if (presentKeys.Contains(workflow.Id.ToString("D")))
                continue;

            // The one delete path: a run in flight refuses, every other run is re-pointed to a snapshot.
            if (!await _replacer.DeleteAsync(workflow, ct))
            {
                outcomes.Add(ProjectRestoreOutcome.Skipped(
                    ProjectVersionItemKind.Workflow, workflow.Id.ToString("D"), workflow.Name,
                    ProjectHistoryRestoreReasons.WorkflowRunActive));
                continue;
            }

            outcomes.Add(ProjectRestoreOutcome.Removed(
                ProjectVersionItemKind.Workflow, workflow.Id.ToString("D"), workflow.Name));
        }

        return outcomes;
    }

    private static void ApplyDefinition(
        WorkflowDefinition workflow, JsonObject document, string name, DateTime now)
    {
        workflow.Name = name;
        workflow.RequiresUserInput = RestoreDocument.GetBool(document, "requiresUserInput") ?? false;

        string? createdVia = RestoreDocument.GetString(document, "createdVia");
        workflow.CreatedVia = WorkflowCreatedVia.IsValid(createdVia) ? createdVia : null;
        workflow.TemplateKey = RestoreDocument.GetString(document, "templateKey");
        workflow.AppManifestJson = document.TryGetPropertyValue("appManifest", out JsonNode? manifest)
                                   && manifest is not null
            ? manifest.ToJsonString()
            : null;
        workflow.UpdatedAt = now;
    }

    /// <summary>
    /// Copies the captured steps, forcing every <c>allowSourceMediaEgress</c> that the live workflow did
    /// not already grant (same order + type + config field) to false. Returns the steps to validate and
    /// write; <paramref name="reset"/> says whether any grant was dropped.
    /// </summary>
    private static List<CreateWorkflowStepRequest> ApplyEgressConsent(
        List<CreateWorkflowStepRequest> proposed, IReadOnlyList<WorkflowStep> liveSteps, out bool reset)
    {
        reset = false;
        List<CreateWorkflowStepRequest> result = new(proposed.Count);

        foreach (CreateWorkflowStepRequest request in proposed)
        {
            WorkflowStep? live = liveSteps.FirstOrDefault(s =>
                s.StepOrder == request.StepOrder
                && s.AgentDefinitionId == request.AgentDefinitionId
                && string.Equals(
                    s.StepType.ToString(),
                    request.StepType ?? nameof(StepType.Agent),
                    StringComparison.OrdinalIgnoreCase));

            bool unbacked = false;
            foreach (PropertyInfo property in ConfigProperties)
            {
                if (property.GetValue(request) is not string capturedJson
                    || string.IsNullOrWhiteSpace(capturedJson)
                    || AssistantEgressConsentGuard.HasConsent(capturedJson) != true)
                {
                    continue;
                }

                PropertyInfo? liveProperty = live is null ? null : typeof(WorkflowStep).GetProperty(property.Name);
                string? liveJson = liveProperty?.GetValue(live) as string;
                if (liveJson is null || AssistantEgressConsentGuard.HasConsent(liveJson) != true)
                    unbacked = true;
            }

            // The grant also needs the rest of the step to be what the customer allowed: the live step's
            // config, apart from the egress flag, must equal the captured one (as the assistant guard asks).
            if (!unbacked && live is not null && !ConfigMatchesApartFromFlag(request, live))
                unbacked = true;

            if (unbacked)
            {
                result.Add(AssistantEgressConsentGuard.ResetAll([request], out _)[0]);
                reset = true;
            }
            else
            {
                result.Add(request);
            }
        }

        return result;
    }

    private static bool ConfigMatchesApartFromFlag(CreateWorkflowStepRequest request, WorkflowStep live)
    {
        foreach (PropertyInfo property in ConfigProperties)
        {
            string? captured = property.GetValue(request) as string;
            string? current = typeof(WorkflowStep).GetProperty(property.Name)?.GetValue(live) as string;
            if (!string.Equals(WithoutFlag(captured), WithoutFlag(current), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string WithoutFlag(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            return Normalize(JsonNode.Parse(json))?.ToJsonString() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    // Key-sorted copy without any allowSourceMediaEgress property, at any depth.
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = new();
                foreach (KeyValuePair<string, JsonNode?> entry in obj
                             .Where(e => !string.Equals(e.Key, "allowSourceMediaEgress", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    sorted[entry.Key] = Normalize(entry.Value);
                }

                return sorted;
            case JsonArray array:
                JsonArray copy = new();
                foreach (JsonNode? item in array)
                    copy.Add(Normalize(item));
                return copy;
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Turns one canonical step node back into the request every save path understands.</summary>
    private static CreateWorkflowStepRequest? ToRequest(JsonObject step)
    {
        if (RestoreDocument.GetString(step, "agentId") is not { } agentRaw
            || !Guid.TryParse(agentRaw, out Guid agentId)
            || Int(step, "order") is not { } order)
        {
            return null;
        }

        return new CreateWorkflowStepRequest(
            agentId,
            order,
            Text(step, "edgeCondition"),
            Text(step, "label"),
            Text(step, "stepType"),
            Text(step, "conditionExpression"),
            Text(step, "loopSourceExpression"),
            Int(step, "loopTargetStepOrder"),
            Int(step, "maxIterations"),
            Int(step, "minScore"),
            Json(step, "inputMapping"),
            Text(step, "agentInputContextMode"),
            Json(step, "selectedPriorStepOrders"),
            Text(step, "trueBranchStepOrder"),
            Text(step, "falseBranchStepOrder"),
            Json(step, "parallelAgentIds"),
            Json(step, "extractConfig"),
            Json(step, "videoAnalyzeConfig"),
            Json(step, "videoCompileConfig"),
            Json(step, "editRoomConfig"),
            Json(step, "graphicsRoomConfig"),
            Json(step, "colorGradeRoomConfig"),
            Json(step, "conditionalConfig"),
            Json(step, "reviewLoopConfig"),
            Text(step, "cacheMode"),
            Json(step, "videoGenerateConfig"),
            Json(step, "voiceoverConfig"),
            Json(step, "thumbnailGenerateConfig"),
            Json(step, "dailiesRoomConfig"),
            Json(step, "timelineRenderConfig"),
            Json(step, "designSystemConfig"),
            Json(step, "continueComposition"),
            Bool(step, "registerOutputAsProjectFile"),
            Json(step, "extractFrameConfig"),
            Json(step, "audioGenerateConfig"),
            Json(step, "pinInsertConfig"));
    }

    private static string? Text(JsonObject obj, string key) => RestoreDocument.GetString(obj, key);

    private static int? Int(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue(out int number)
            ? number
            : null;

    private static bool Bool(JsonObject obj, string key) => RestoreDocument.GetBool(obj, key) ?? false;

    private static string? Json(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node) && node is not null
            ? node.ToJsonString()
            : null;

    private static ProjectRestoreOutcome Skip(ProjectVersionItem item, string reason) =>
        ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, reason);
}
