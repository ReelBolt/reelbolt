using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// Step option "continue from an earlier run's composition" (<c>WorkflowStep.ContinueCompositionJson</c>),
/// run by <c>AgentStepExecutor</c> just before the agent: resolve the source workspace version, prove it
/// belongs to THIS execution's project, restore it into this execution's sandbox, and tell the agent so.
///
/// <list type="bullet">
/// <item><c>versionId</c> → that version; <c>executionId</c> → the latest version of that run's origin
/// workspace (else the latest version that run captured into a revision lineage).</item>
/// <item>A source outside the execution's project — another project, another tenant, or nothing at all —
/// fails the step as <see cref="SourceNotFoundCode"/> (UserInput), with one message for all three, and
/// nothing is restored. The save path refuses the same thing; this is the independent run-time bound.</item>
/// <item>Lineage: no <see cref="CompositionLineage"/> is set, so this run's later render captures form
/// its OWN origin workspace (revisions on the new run work) and the source lineage is never written.
/// When this run already owns a captured version (a review-loop pass or a retry after a render), the
/// sandbox holds this run's own work and is NOT restored over again.</item>
/// <item>A restore that fails is <see cref="RestoreFailedCode"/> (PlatformInternal); the agent never runs
/// on a half-restored sandbox.</item>
/// </list>
/// </summary>
public sealed class CompositionContinuation
{
    public const string SourceNotFoundCode = "COMPOSITION_SOURCE_NOT_FOUND";
    public const string RestoreFailedCode = "COMPOSITION_RESTORE_FAILED";
    public const string RequiresAuthoringCode = "CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT";

    /// <summary>Prepended to the Author's input when its sandbox starts from an earlier composition.</summary>
    public const string Note =
        "[CONTINUING COMPOSITION] The sandbox already contains the composition from an earlier run. " +
        "Reuse its components and styles; edit rather than rewrite; render once.";

    private const string SourceNotFoundMessage =
        "The composition this step continues from is not available in this project.";

    private readonly ICompositionWorkspaceRestorer _restorer;
    private readonly ISandboxSessionFactory _sandboxes;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CompositionContinuation> _logger;

    public CompositionContinuation(
        ICompositionWorkspaceRestorer restorer,
        ISandboxSessionFactory sandboxes,
        IServiceScopeFactory scopes,
        ILogger<CompositionContinuation> logger)
    {
        _restorer = restorer;
        _sandboxes = sandboxes;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>What to hand the agent, or the failed result to return instead of running it.</summary>
    public sealed record Outcome(string Input, StepExecutionResult? Failure);

    /// <summary>
    /// Built from the container's existing registrations; null when any is missing (an embedding without
    /// composition workspaces), in which case a step that asks to continue fails instead of silently
    /// starting from scratch.
    /// </summary>
    public static CompositionContinuation? FromServices(IServiceProvider? services)
    {
        if (services?.GetService<ICompositionWorkspaceRestorer>() is not { } restorer
            || services.GetService<ISandboxSessionFactory>() is not { } sandboxes
            || services.GetService<IServiceScopeFactory>() is not { } scopes)
            return null;

        ILogger<CompositionContinuation> logger =
            services.GetService<ILoggerFactory>()?.CreateLogger<CompositionContinuation>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CompositionContinuation>.Instance;
        return new CompositionContinuation(restorer, sandboxes, scopes, logger);
    }

    /// <summary>A no-op for a step without the option; otherwise <see cref="ContinueAsync"/>.</summary>
    public static Task<Outcome> ApplyAsync(CompositionContinuation? continuation, StepExecutionContext context, string stepInput)
    {
        if (string.IsNullOrWhiteSpace(context.Step.ContinueCompositionJson))
            return Task.FromResult(new Outcome(stepInput, null));

        return continuation is null
            ? Task.FromResult(new Outcome(stepInput, Fail(context, RestoreFailedCode, FailureClass.PlatformInternal,
                "Continuing a composition is not available in this engine (composition workspaces are not configured).")))
            : continuation.ContinueAsync(context, stepInput);
    }

    public async Task<Outcome> ContinueAsync(StepExecutionContext context, string stepInput)
    {
        CancellationToken ct = context.CancellationToken;

        if (!ToolGroupCatalog.GroupsFor(context.Step.AgentDefinition.AgentType).Contains(ToolGroup.SandboxAuthoring))
        {
            return new Outcome(stepInput, Fail(context, RequiresAuthoringCode, FailureClass.UserInput,
                $"Agent {context.Step.AgentDefinition.AgentType} does not author compositions, so it cannot continue one."));
        }

        if (!ContinueCompositionConfig.TryParse(context.Step.ContinueCompositionJson, out ContinueCompositionConfig? config, out _))
            return new Outcome(stepInput, Fail(context, SourceNotFoundCode, FailureClass.UserInput, SourceNotFoundMessage));

        (CompositionWorkspaceVersion? version, bool alreadyContinued) = await ResolveAsync(context.Execution, config!, ct);
        if (version is null)
        {
            _logger.LogWarning(
                "Execution {ExecutionId} step {StepOrder}: continue-composition source {Source} is not in project {ProjectId}",
                context.Execution.Id, context.Step.StepOrder, context.Step.ContinueCompositionJson, context.Execution.ProjectId);
            return new Outcome(stepInput, Fail(context, SourceNotFoundCode, FailureClass.UserInput, SourceNotFoundMessage));
        }

        if (alreadyContinued)
        {
            _logger.LogInformation(
                "Execution {ExecutionId} step {StepOrder}: this run already captured its own composition; keeping its sandbox",
                context.Execution.Id, context.Step.StepOrder);
            return new Outcome(Prefix(stepInput), null);
        }

        if (await PredatesQuarantineAsync(version, ct))
            return new Outcome(stepInput, Fail(context, SourceNotFoundCode, FailureClass.UserInput, SourceNotFoundMessage));

        try
        {
            ISandboxSession session = _sandboxes.Create(context.Execution.Id);
            CompositionRestoreResult restored = await _restorer.RestoreAsync(session, version, ct);
            _logger.LogInformation(
                "Execution {ExecutionId} step {StepOrder}: restored composition workspace {WorkspaceId} v{Version} ({Files} files)",
                context.Execution.Id, context.Step.StepOrder, version.WorkspaceId, version.VersionNumber, restored.Files);
            string input = restored.Warnings.Count == 0
                ? Prefix(stepInput)
                : Prefix(string.Join("\n", restored.Warnings) + "\n\n" + stepInput);
            return new Outcome(input, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Execution {ExecutionId} step {StepOrder}: restoring composition version {VersionId} failed",
                context.Execution.Id, context.Step.StepOrder, version.Id);
            return new Outcome(stepInput, Fail(context, RestoreFailedCode, FailureClass.PlatformInternal,
                "The earlier composition could not be restored into this run's sandbox."));
        }
    }

    private async Task<bool> PredatesQuarantineAsync(CompositionWorkspaceVersion version, CancellationToken ct)
    {
        using IServiceScope scope = _scopes.CreateScope();
        return await CompositionVersionQuarantine.PredatesAsync(
            scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>(), version, ct);
    }

    /// <summary>
    /// The source version, or null when it is not in <paramref name="execution"/>'s project; plus whether
    /// this execution already owns a captured version (its sandbox then already holds the continuation).
    /// Every query compares project AND organization explicitly; nothing is trusted from one row alone.
    /// </summary>
    private async Task<(CompositionWorkspaceVersion? Version, bool AlreadyContinued)> ResolveAsync(
        WorkflowExecution execution, ContinueCompositionConfig config, CancellationToken ct)
    {
        using IServiceScope scope = _scopes.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Guid projectId = execution.ProjectId;
        Guid organizationId = execution.OrganizationId;

        IQueryable<CompositionWorkspaceVersion> inProject =
            from v in db.CompositionWorkspaceVersions.AsNoTracking()
            join w in db.CompositionWorkspaces.AsNoTracking() on v.WorkspaceId equals w.Id
            where v.ProjectId == projectId && w.ProjectId == projectId && w.OrganizationId == organizationId
            select v;

        CompositionWorkspaceVersion? version = null;
        if (config.VersionId is { } versionId)
        {
            version = await inProject.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        }
        else if (config.ExecutionId is { } sourceId && sourceId != execution.Id)
        {
            bool sourceInProject = await db.WorkflowExecutions.AsNoTracking()
                .AnyAsync(e => e.Id == sourceId && e.ProjectId == projectId && e.OrganizationId == organizationId, ct);
            if (sourceInProject)
            {
                version = await (
                        from v in inProject
                        join w in db.CompositionWorkspaces.AsNoTracking() on v.WorkspaceId equals w.Id
                        where w.OriginExecutionId == sourceId
                        orderby v.VersionNumber descending
                        select v)
                    .FirstOrDefaultAsync(ct)
                    // A revision run captures into its source's lineage, not an origin workspace of its own.
                    ?? await inProject
                        .Where(v => v.CreatedByExecutionId == sourceId)
                        .OrderByDescending(v => v.CreatedAt)
                        .FirstOrDefaultAsync(ct);
            }
        }

        if (version is null)
            return (null, false);

        bool alreadyContinued = await (
                from v in db.CompositionWorkspaceVersions.AsNoTracking()
                join w in db.CompositionWorkspaces.AsNoTracking() on v.WorkspaceId equals w.Id
                where w.OriginExecutionId == execution.Id && w.ProjectId == projectId
                select v.Id)
            .AnyAsync(ct);
        return (version, alreadyContinued);
    }

    private static string Prefix(string stepInput) => Note + "\n\n" + stepInput;

    private static StepExecutionResult Fail(StepExecutionContext context, string code, FailureClass failureClass, string message) => new()
    {
        Output = JsonSerializer.Serialize(new { error = new { code, message } }),
        NextStepIndex = context.CurrentStepIndex + 1,
        NewIterationCount = context.IterationCount,
        DurationMs = 0,
        TokensUsed = 0,
        Status = StepStatus.Failed,
        ErrorDetails = $"{code}: {message}",
        FailureClass = failureClass,
        FailureCode = code
    };
}
