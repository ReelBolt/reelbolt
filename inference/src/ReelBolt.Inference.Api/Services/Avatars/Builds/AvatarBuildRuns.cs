using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>One step of a hidden avatar build run: its type and its config (the column its type reads).</summary>
public sealed record AvatarBuildRunStep(string Label, StepType Type, string ConfigJson);

/// <summary>What one step of a hidden run came to: its status, its result row and the code it failed with.</summary>
public sealed record AvatarBuildStepOutcome(StepStatus Status, Guid StepResultId, string? OutputStorageKey, string? FailureCode);

/// <summary>A hidden run as the build job reads it: its status (null = no such run yet) and each step's latest result, by step order.</summary>
public sealed record AvatarBuildRunState(ExecutionStatus? Status, IReadOnlyDictionary<int, AvatarBuildStepOutcome> Steps)
{
    public bool Ended => Status is ExecutionStatus.Passed or ExecutionStatus.Failed or ExecutionStatus.Cancelled;
}

/// <summary>
/// The hidden <see cref="WorkflowDefinitionKind.AvatarBuild"/> runs of an avatar build (docs/design/avatar-objects.md
/// "Avatar variants" stage 2, "Pulling an object out of a video" step 3): a workflow of one step per item (a free
/// <c>ExtractFrame</c> grab or cut, a <c>VideoGenerate</c> take), queued through the same
/// <see cref="IWorkflowExecutionService.QueueAsync"/> every hidden one-step kind uses, so the engine runs it with its
/// usual credit gate, budget gate, ledger, lease and recovery.
/// <para>
/// <b>Restart-safe.</b> The workflow and execution ids are given by the job (derived from it, so a job run again finds
/// them): a run whose workflow exists is not created twice, and one whose execution exists is not queued twice. A
/// workflow left without an execution (a refused or interrupted queue) is deleted here, or by the orphan reaper.
/// </para>
/// </summary>
public sealed class AvatarBuildRuns
{
    private readonly InferenceApiDbContext _db;
    private readonly ILogger<AvatarBuildRuns> _logger;
    private readonly IWorkflowExecutionService? _executions;
    private readonly IPublishEndpoint? _publish;

    public AvatarBuildRuns(
        InferenceApiDbContext db,
        ILogger<AvatarBuildRuns> logger,
        IWorkflowExecutionService? executions = null,
        IPublishEndpoint? publish = null)
    {
        _db = db;
        _logger = logger;
        _executions = executions;
        _publish = publish;
    }

    /// <summary>Whether hidden runs can be queued in this process.</summary>
    public bool Available => _executions is not null;

    /// <summary>
    /// Queues <paramref name="run"/>: its workflow (created now when it is not there yet) and its execution. Answers null
    /// when it is queued (or already was), else the plan limit that refused it now (the plan's concurrency cap: the
    /// caller waits and asks again).
    /// </summary>
    public async Task<PlanGateViolation?> QueueAsync(
        AvatarBuildJob job, AvatarBuildRun run, string name, IReadOnlyList<AvatarBuildRunStep> steps, CancellationToken ct)
    {
        if (_executions is null) throw new InvalidOperationException("Hidden avatar build runs cannot be queued here.");
        if (await _db.WorkflowExecutions.AsNoTracking().AnyAsync(e => e.Id == run.ExecutionId, ct)) return null;

        if (!await _db.WorkflowDefinitions.AsNoTracking().AnyAsync(w => w.Id == run.WorkflowId, ct))
        {
            Guid placeholder = await _db.AgentDefinitions.AsNoTracking()
                .Where(a => a.AgentType == AgentType.VideoTransform)
                .OrderByDescending(a => a.IsBuiltIn)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (placeholder == Guid.Empty)
                throw new InvalidOperationException("The VideoTransform placeholder agent is not seeded, so no avatar build step can be created.");

            DateTime now = DateTime.UtcNow;
            var workflow = new WorkflowDefinition
            {
                Id = run.WorkflowId,
                ProjectId = job.ProjectId,
                Name = name.Length <= 200 ? name : name[..200],
                Kind = WorkflowDefinitionKind.AvatarBuild,
                RequiresUserInput = false,
                CreatedAt = now,
                UpdatedAt = now
            };
            for (int i = 0; i < steps.Count; i++)
            {
                AvatarBuildRunStep step = steps[i];
                var row = new WorkflowStep
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = workflow.Id,
                    AgentDefinitionId = placeholder,
                    StepOrder = i + 1,
                    Label = step.Label,
                    StepType = step.Type
                };
                switch (step.Type)
                {
                    case StepType.ExtractFrame:
                        row.ExtractFrameConfigJson = step.ConfigJson;
                        break;
                    case StepType.VideoGenerate:
                        row.VideoGenerateConfigJson = step.ConfigJson;
                        break;
                    default:
                        throw new ArgumentException($"An avatar build run has no {step.Type} step.", nameof(steps));
                }

                workflow.Steps.Add(row);
            }

            _db.WorkflowDefinitions.Add(workflow);
            await _db.SaveChangesAsync(ct);
        }

        try
        {
            await _executions.QueueAsync(
                job.ProjectId, run.WorkflowId, job.RequestedByUserId, userRequest: null, ExecutionInitiator.Human, ct,
                regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.AvatarBuild, run.ExecutionId);
            return null;
        }
        catch (PlanLimitException limit)
        {
            // Nothing runs: the workflow goes too (it is made again with the same id when the job asks again).
            await DeleteWorkflowQuietlyAsync(run.WorkflowId);
            return limit.Violation;
        }
    }

    /// <summary>The run's status and the latest result of each of its steps (superseded attempts left out).</summary>
    public async Task<AvatarBuildRunState> ReadAsync(Guid projectId, Guid executionId, CancellationToken ct)
    {
        var execution = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ProjectId == projectId)
            .Select(e => new { e.Status, e.WorkflowDefinitionId })
            .FirstOrDefaultAsync(ct);
        if (execution is null) return new AvatarBuildRunState(null, new Dictionary<int, AvatarBuildStepOutcome>());

        Dictionary<Guid, int> orders = await _db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == execution.WorkflowDefinitionId)
            .ToDictionaryAsync(s => s.Id, s => s.StepOrder, ct);
        var results = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId && r.SupersededAt == null)
            .Select(r => new { r.Id, r.WorkflowStepId, r.Status, r.OutputStorageKey, r.FailureCode, r.ExecutedAt })
            .ToListAsync(ct);

        Dictionary<int, AvatarBuildStepOutcome> steps = [];
        foreach (var result in results.OrderBy(r => r.ExecutedAt))
        {
            if (!orders.TryGetValue(result.WorkflowStepId, out int order)) continue;
            steps[order] = new AvatarBuildStepOutcome(result.Status, result.Id, result.OutputStorageKey, result.FailureCode);
        }

        return new AvatarBuildRunState(execution.Status, steps);
    }

    /// <summary>Asks the engine to stop a run (the same event the workflow stop route publishes). Best effort.</summary>
    public async Task StopAsync(Guid executionId, Guid userId)
    {
        if (_publish is null) return;
        try
        {
            await _publish.Publish(new WorkflowExecutionStopRequested { ExecutionId = executionId, RequestedByUserId = userId }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask the engine to stop avatar build run {ExecutionId}", executionId);
        }
    }

    /// <summary>
    /// Deletes a workflow nothing ran. Only its own rows are touched: the caller's unit of work (the job it is saving)
    /// stays as it is.
    /// </summary>
    private async Task DeleteWorkflowQuietlyAsync(Guid workflowId)
    {
        WorkflowDefinition? workflow = null;
        try
        {
            workflow = await _db.WorkflowDefinitions.Include(w => w.Steps)
                .FirstOrDefaultAsync(w => w.Id == workflowId, CancellationToken.None);
            if (workflow is null) return;
            _db.WorkflowSteps.RemoveRange(workflow.Steps);
            _db.WorkflowDefinitions.Remove(workflow);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The orphan reaper deletes a hidden avatar build workflow nothing ran.
            _logger.LogInformation(ex, "Could not delete the unqueued avatar build workflow {WorkflowId}; the orphan reaper will", workflowId);
            if (workflow is not null)
            {
                foreach (WorkflowStep step in workflow.Steps) _db.Entry(step).State = EntityState.Detached;
                _db.Entry(workflow).State = EntityState.Detached;
            }
        }
    }
}
