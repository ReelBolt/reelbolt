using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;

namespace ReelBolt.WorkflowEngine.Execution.Replay;

/// <summary>What a step executor needs to apply a change request to its step.</summary>
/// <param name="RequestId">The <c>step_change_requests</c> row.</param>
/// <param name="Payload">The validated request (instruction, item scope, overrides).</param>
/// <param name="PreviousOutputJson">The target step's output in the run being changed (null when it is no longer available).</param>
/// <param name="ArtifactStorageKey">The target step result's artifact (e.g. VideoGenerate's clips manifest, which alone holds the clips' storage keys); null unless it lies under this project's own storage prefix.</param>
public sealed record StepChangeRequestScope(Guid RequestId, ChangeRequestPayload Payload, string? PreviousOutputJson, string? ArtifactStorageKey = null)
{
    /// <summary>
    /// True when the stored request could not be read (its scope or overrides JSON is corrupt). Such a scope carries no
    /// usable payload: every executor refuses it with <c>CHANGE_NOT_APPLIED</c> before any side effect rather than redoing
    /// the whole step while the credit hold stays scaled to the request.
    /// </summary>
    public bool Malformed { get; init; }

    /// <summary>What a refused malformed request says.</summary>
    public const string MalformedRefusal = "The stored change request could not be read, so nothing was changed. Ask for the change again.";
}

/// <summary>
/// Finds the change request an execution is carrying out, the way <c>ReviseStepExecutor</c> finds its
/// composition revision (docs/design/change-requests.md "Model / Engine session"). A request applies only
/// when the execution's workflow is a <see cref="WorkflowDefinitionKind.CompositionReplay"/> snapshot, the
/// row belongs to the execution's project and the step is the row's target step — a malformed or foreign row
/// never applies, and an ordinary run always gets null.
/// </summary>
public interface IStepChangeRequestContext
{
    Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct);
}

/// <summary>
/// Reads through a fresh DI scope per call, so the singleton step executors can depend on it (the same way
/// the composition workspace store and the project-voice resolver do).
/// </summary>
public sealed class StepChangeRequestContext(IServiceScopeFactory scopeFactory) : IStepChangeRequestContext
{
    public async Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        // A change a review loop asked for inside this very run (docs/design/review-loop-adjustments.md): the executor
        // applies it exactly as it applies a customer request.
        if (ReviewStepChangeRegistry.Instance.Find(execution.Id, step.StepOrder) is { } reviewChange)
            return reviewChange;

        using IServiceScope scope = scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        WorkflowDefinitionKind? kind = execution.WorkflowDefinition?.Kind
            ?? await db.WorkflowDefinitions.AsNoTracking()
                .Where(d => d.Id == execution.WorkflowDefinitionId)
                .Select(d => (WorkflowDefinitionKind?)d.Kind)
                .FirstOrDefaultAsync(ct);
        if (kind != WorkflowDefinitionKind.CompositionReplay)
            return null;

        StepChangeRequest? row = await db.StepChangeRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ExecutionId == execution.Id && r.TargetStepOrder == step.StepOrder, ct);
        if (row is null || row.ProjectId != execution.ProjectId)
            return null;

        // An app-input change (docs/design/app-inputs.md 25) carries no step change: the step was re-bound with the new
        // values in the request's snapshot and runs from its own config, so no executor reads the row as overrides.
        if (AppInputChangeRows.IsAppInputRow(row.OverridesJson))
            return null;

        bool idsOk = TryParseIds(row.ScopeJson, out IReadOnlyList<string>? ids);
        bool overridesOk = TryParseOverrides(row.OverridesJson, out IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>>? overrides);
        bool malformed = !idsOk || !overridesOk;
        ChangeRequestPayload payload = new(row.Instruction, ids, overrides);

        Guid? sourceResultId = CompositionReplayPlan.TryParse(row.ReplayPlanJson)?.For(step.StepOrder)?.SourceStepResultId
            ?? row.TargetStepResultId;
        string? previous = null;
        string? artifactKey = null;
        if (sourceResultId is Guid id)
        {
            WorkflowStepResult? source = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == id && r.WorkflowExecution.ProjectId == execution.ProjectId)
                .FirstOrDefaultAsync(ct);
            // What is carried over free comes from this result, so one that may predate the project's last quarantine
            // gives none, as a source that is no longer available does. A failed read counts as predating.
            DateTime? sourceRunStartedAt = source is null ? null : await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.Id == source.WorkflowExecutionId)
                .Select(e => e.FirstStartedAt)
                .FirstOrDefaultAsync(ct);
            if (source is not null
                && !await ProjectQuarantineState.ResultPredatesAsync(db, execution.ProjectId, source.ExecutedAt, sourceRunStartedAt, ct))
            {
                previous = CompositionReplaySession.OriginalOutput(source);
                // Project-scoped: a key outside this project's own prefix is never handed to an executor.
                artifactKey = !string.IsNullOrWhiteSpace(source.ArtifactStorageKey)
                    && source.ArtifactStorageKey.StartsWith($"projects/{execution.ProjectId}/agentFiles/", StringComparison.Ordinal)
                    && !source.ArtifactStorageKey.Contains("..", StringComparison.Ordinal)
                        ? source.ArtifactStorageKey
                        : null;
            }
        }

        return new StepChangeRequestScope(row.Id, payload, string.IsNullOrWhiteSpace(previous) ? null : previous, artifactKey) { Malformed = malformed };
    }

    private static bool TryParseIds(string? json, out IReadOnlyList<string>? ids)
    {
        ids = null;
        if (string.IsNullOrWhiteSpace(json))
            return true;
        try
        {
            ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryParseOverrides(string? json, out IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>>? overrides)
    {
        overrides = null;
        if (string.IsNullOrWhiteSpace(json))
            return true;
        try
        {
            overrides = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, System.Text.Json.JsonElement>>>(json)?
                .ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, System.Text.Json.JsonElement>)p.Value);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
