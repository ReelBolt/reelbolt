using System.Text.Json;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compositions;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <see cref="StepType.Revise"/>: the execution-backed front-end of the composition-revision library
/// (docs/design/composition-revisions.md). A thin adapter — it reads the request the Inference API
/// recorded for this execution (<c>composition_revisions</c>, by execution id), hands it to
/// <see cref="ICompositionRevisionRunner"/>, forwards the runner's stages as step progress, and maps the
/// outcome onto a step result. Everything a revision IS lives in the runner, so a second front-end
/// (the editor-v2 live mode) reuses it without this class.
///
/// <para>
/// A step of its own rather than an <c>Agent</c> step because the revision has deterministic pre- and
/// post-conditions the generic agent executor cannot express: the workspace is restored BEFORE the
/// model runs (never left to the model to remember), and the step only succeeds when a render was
/// uploaded and a version recorded. Never cached (<c>StepCachePolicy</c>) and never retried by the
/// outer loop (a retry would restore and re-render from scratch; a failed revision is refunded by its
/// class and the owner simply asks again).
/// </para>
/// </summary>
public sealed class ReviseStepExecutor : IStepExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ICompositionWorkspaceStore _store;
    private readonly ICompositionRevisionRunner _runner;
    private readonly ILogger<ReviseStepExecutor> _logger;
    private readonly IAlsoRemadeNoteSource? _alsoRemade;

    public ReviseStepExecutor(
        ICompositionWorkspaceStore store,
        ICompositionRevisionRunner runner,
        ILogger<ReviseStepExecutor> logger,
        IAlsoRemadeNoteSource? alsoRemade = null)
    {
        _store = store;
        _runner = runner;
        _logger = logger;
        _alsoRemade = alsoRemade;
    }

    public StepType StepType => StepType.Revise;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        CompositionRevision? revision = await _store.FindRevisionByExecutionAsync(context.Execution.Id, context.CancellationToken);
        if (revision is null || revision.ProjectId != context.Execution.ProjectId)
        {
            return Failure(context, CompositionRevisionRunner.RequestMissing,
                "This run has no change request attached. Ask for changes from the finished video's page.", null);
        }

        CompositionRevisionTargets? targets = CompositionRevisionTargets.Deserialize(revision.TargetsJson);
        string? alsoRemade = _alsoRemade is null
            ? null
            : await _alsoRemade.FindAsync(context.Execution.Id, revision.ProjectId, context.Step.StepOrder, context.CancellationToken);
        CompositionRevisionRequest request = new()
        {
            ProjectId = revision.ProjectId,
            OrganizationId = context.Execution.OrganizationId,
            ExecutionId = context.Execution.Id,
            StepResultId = context.StepResultId,
            CorrelationId = context.CorrelationId,
            WorkspaceId = revision.WorkspaceId,
            BaseVersionId = revision.BaseVersionId,
            RecoverFromExecutionId = revision.BaseVersionId is null ? revision.SourceExecutionId : null,
            Instruction = revision.Instruction,
            Targets = targets,
            Mode = revision.Mode,
            RevisionId = revision.Id,
            RequestedByUserId = revision.RequestedByUserId,
            AgentDefinitionId = context.Step.AgentDefinitionId,
            RetryGuidance = string.IsNullOrWhiteSpace(context.RetryGuidance) ? null : context.RetryGuidance,
            AlsoRemadeNote = alsoRemade
        };

        // What the step consumed, as JSON (InputJson is persisted as jsonb): the request, not the brief —
        // the brief is rebuilt from it deterministically and carries the owner's text only once.
        context.RecordResolvedInput(JsonSerializer.Serialize(new
        {
            revisionId = revision.Id,
            workspaceId = revision.WorkspaceId,
            baseVersionId = revision.BaseVersionId,
            sourceExecutionId = revision.SourceExecutionId,
            mode = revision.Mode.ToString(),
            instruction = revision.Instruction,
            targets
        }, Json));

        CompositionRevisionOutcome outcome = await _runner.RunAsync(request, new StepObserver(context), context.CancellationToken);

        string output = JsonSerializer.Serialize(new
        {
            revisionId = revision.Id,
            succeeded = outcome.Succeeded,
            baseVersion = outcome.BaseVersion is { } b ? new { id = b.Id, number = b.VersionNumber } : null,
            resultVersion = outcome.ResultVersion is { } r ? new { id = r.Id, number = r.VersionNumber } : null,
            renderStorageKey = outcome.RenderStorageKey,
            summary = outcome.AgentAnswer?.Summary,
            changedFiles = outcome.AgentAnswer?.ChangedFiles,
            changedScenes = outcome.AgentAnswer?.ChangedScenes,
            notes = NullIfEmpty(outcome.AgentAnswer?.Notes),
            warnings = outcome.Warnings.Count > 0 ? outcome.Warnings : null,
            failureCode = outcome.FailureCode,
            failure = outcome.FailureMessage
        }, Json);

        if (!outcome.Succeeded)
        {
            _logger.LogWarning(
                "Revise step of execution {ExecutionId} failed: {Code} {Message}",
                context.Execution.Id, outcome.FailureCode, outcome.FailureMessage);
            context.RecordFailure(FailureClassifier.ForCode(outcome.FailureCode), outcome.FailureCode);
        }

        return new StepExecutionResult
        {
            Output = output,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = outcome.DurationMs,
            TokensUsed = outcome.TokensUsed,
            InputTokens = outcome.InputTokens,
            OutputTokens = outcome.OutputTokens,
            PrimaryProviderId = outcome.PrimaryProviderId,
            PrimaryModelName = outcome.PrimaryModelName,
            ToolCalls = outcome.ToolCalls,
            Reasoning = outcome.Reasoning,
            OutputStorageKey = outcome.RenderStorageKey,
            Status = outcome.Succeeded ? StepStatus.Completed : StepStatus.Failed,
            ErrorDetails = outcome.Succeeded ? null : outcome.FailureMessage,
            FailureClass = outcome.Succeeded ? FailureClass.None : FailureClassifier.ForCode(outcome.FailureCode),
            FailureCode = outcome.FailureCode
        };
    }

    private static StepExecutionResult Failure(StepExecutionContext context, string code, string message, string? output)
    {
        context.RecordFailure(FailureClassifier.ForCode(code), code);
        return new StepExecutionResult
        {
            Output = output ?? JsonSerializer.Serialize(new { succeeded = false, failureCode = code, failure = message }, Json),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            Status = StepStatus.Failed,
            ErrorDetails = message,
            FailureClass = FailureClassifier.ForCode(code),
            FailureCode = code
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Forwards the runner's stages to the step's ephemeral progress events (never fails the step).</summary>
    private sealed class StepObserver(StepExecutionContext context) : ICompositionRevisionObserver
    {
        public Task OnStageAsync(string stage, int? percentComplete, CancellationToken ct) =>
            context.ReportProgressAsync(stage, percentComplete);
    }
}
