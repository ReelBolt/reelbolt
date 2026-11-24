using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>A finished, shareable render of one timeline: server-side only, the key never leaves the API.</summary>
public sealed record ReviewRender(
    Guid ExecutionId,
    Guid StepResultId,
    string StorageKey,
    string FileName,
    int Revision,
    DateTime CreatedAt,
    double? DurationSec);

/// <summary>
/// Finds the render a review link shows. A shareable render is a PASSED full-timeline
/// <c>TimelineRender</c> execution of this timeline (no range, not the short preview preset, not an audio
/// job) that has a completed, non-superseded step result with an output file under the project's own
/// output prefixes. Anything else is "no render" and the caller answers the uniform 404 / 409.
/// </summary>
/// <remarks>
/// The public routes are anonymous, so every query names the project explicitly and ignores only the
/// named <c>Tenant</c> filter (soft-delete filters still apply).
/// </remarks>
public sealed class ReviewRenderResolver(InferenceApiDbContext db)
{
    private static readonly string[] Tenant = [InferenceApiDbContext.TenantFilterName];

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The newest shareable render of the timeline, or null.</summary>
    public async Task<ReviewRender?> NewestAsync(Guid projectId, Guid timelineId, CancellationToken ct)
    {
        List<ReviewRender> all = await LoadAsync(projectId, timelineId, onlyExecution: null, ct);
        return all.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.ExecutionId).FirstOrDefault();
    }

    /// <summary>The shareable render of <paramref name="executionId"/>, or null when it is not one of this timeline.</summary>
    public async Task<ReviewRender?> ForExecutionAsync(Guid projectId, Guid timelineId, Guid executionId, CancellationToken ct) =>
        (await LoadAsync(projectId, timelineId, executionId, ct)).FirstOrDefault();

    private async Task<List<ReviewRender>> LoadAsync(Guid projectId, Guid timelineId, Guid? onlyExecution, CancellationToken ct)
    {
        IQueryable<WorkflowExecution> executions = db.WorkflowExecutions.IgnoreQueryFilters(Tenant).AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Status == ExecutionStatus.Passed
                        && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.TimelineRender
                        && e.WorkflowDefinition.ProjectId == projectId);
        if (onlyExecution is { } only) executions = executions.Where(e => e.Id == only);

        // Newest first and bounded: a timeline's recent history is all a share ever needs.
        List<WorkflowExecution> rows = await executions
            .Include(e => e.StepResults)
            .Include(e => e.WorkflowDefinition).ThenInclude(w => w.Steps)
            .OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
            .Take(onlyExecution is null ? 200 : 1)
            .ToListAsync(ct);

        List<ReviewRender> renders = [];
        ReelBolt.Shared.Storage.ProjectOutputHoldState hold =
            await ReelBolt.Shared.Storage.ProjectOutputHold.ReadAsync(db, projectId, ct);
        foreach (WorkflowExecution e in rows)
        {
            WorkflowStep? step = e.WorkflowDefinition.Steps.FirstOrDefault(s => s.StepType == StepType.TimelineRender);
            TimelineRenderStepConfig? config = Parse(step?.TimelineRenderConfigJson);
            if (step is null || config is null) continue;
            if (config.EditTimelineId != timelineId || config.Range is not null || config.AudioJobKind is not null
                || config.Preset == TimelineRenderPreset.Preview)
                continue;

            WorkflowStepResult? result = e.StepResults
                .Where(r => r.WorkflowStepId == step.Id && r.Status == StepStatus.Completed && r.SupersededAt == null)
                .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
                .FirstOrDefault();
            if (result is null || !ProjectOutputKeys.IsAddressableOutput(projectId, result.OutputStorageKey)) continue;
            if (hold.IsActive && hold.HoldsOutput(result.ExecutedAt, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(e.FirstStartedAt, e.StartedAt))) continue;

            (int? revision, double? duration) = ReadOutput(result.OutputJson);
            renders.Add(new ReviewRender(
                e.Id, result.Id, result.OutputStorageKey!, Path.GetFileName(result.OutputStorageKey!),
                revision ?? config.Revision ?? 0,
                e.QueuedAt ?? e.StartedAt ?? e.CompletedAt ?? DateTime.UnixEpoch,
                duration));
        }

        return renders;
    }

    private static TimelineRenderStepConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TimelineRenderStepConfig>(json, ConfigJson); }
        catch (JsonException) { return null; }
    }

    private static (int? Revision, double? DurationSec) ReadOutput(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return (null, null);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            int? revision = root.TryGetProperty("revision", out JsonElement r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out int rv)
                ? rv
                : null;
            double? duration = root.TryGetProperty("durationSec", out JsonElement d) && d.ValueKind == JsonValueKind.Number
                && d.TryGetDouble(out double dv) && double.IsFinite(dv) && dv >= 0
                ? dv
                : null;
            return (revision, duration);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
