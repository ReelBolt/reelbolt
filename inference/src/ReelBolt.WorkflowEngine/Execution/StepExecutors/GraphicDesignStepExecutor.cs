using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Graphics;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <see cref="StepType.DesignGraphic"/>: the execution-backed front-end of graphic design for the picture
/// editor (docs/editor.md "Designed graphics"). A thin adapter, exactly like <see cref="ReviseStepExecutor"/>:
/// it reads the request the Inference API recorded for this execution (<c>edit_image_graphic_requests</c>,
/// by execution id), hands it to <see cref="IGraphicDesignRunner"/>, forwards the runner's stages as step
/// progress, and maps the outcome onto a step result.
///
/// <para>
/// <b>Why an execution and not a synchronous editor operation.</b> A design is an agent tool loop plus a
/// headless-browser render — minutes, not the 90 seconds the editor's synchronous operations are bounded
/// to — and running it as a step is what gives it, for free, the credit reserve/settle/refund by failure
/// class, the lease and recovery, cancellation, the SSE progress stream and the run history.
/// </para>
/// <para>
/// Never cached (<c>StepCachePolicy</c>: it writes a sandbox, an object and a workspace version), and never
/// retried by the outer loop (a retry would design a second, different graphic; a failed design is
/// refunded by its class and the person simply asks again).
/// </para>
/// </summary>
public sealed class GraphicDesignStepExecutor : IStepExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IGraphicDesignRequestStore _requests;
    private readonly IGraphicDesignRunner _runner;
    private readonly ILogger<GraphicDesignStepExecutor> _logger;

    public GraphicDesignStepExecutor(
        IGraphicDesignRequestStore requests,
        IGraphicDesignRunner runner,
        ILogger<GraphicDesignStepExecutor> logger)
    {
        _requests = requests;
        _runner = runner;
        _logger = logger;
    }

    public StepType StepType => StepType.DesignGraphic;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        EditImageGraphicRequest? row = await _requests.FindByExecutionAsync(context.Execution.Id, context.CancellationToken);
        if (row is null || row.ProjectId != context.Execution.ProjectId)
        {
            return Failure(context, GraphicDesignRunner.RequestMissing,
                "This run has no graphic request attached. Ask for a graphic from the picture editor.");
        }

        GraphicDesignRequest request = new()
        {
            GraphicId = row.Id,
            ProjectId = row.ProjectId,
            OrganizationId = context.Execution.OrganizationId,
            DocumentId = row.DocumentId,
            ExecutionId = context.Execution.Id,
            CorrelationId = context.CorrelationId,
            Description = row.Description,
            Placement = GraphicDesignRules.NormalisePlacement(row.Placement) ?? "center",
            FrameWidth = row.FrameWidth,
            FrameHeight = row.FrameHeight,
            Sized = row.Sized,
            BaseVersionId = row.BaseVersionId,
            RequestedByUserId = row.RequestedByUserId,
            AgentDefinitionId = context.Step.AgentDefinitionId,
            RetryGuidance = string.IsNullOrWhiteSpace(context.RetryGuidance) ? null : context.RetryGuidance
        };

        // What the step consumed, as JSON (InputJson is jsonb): the request, quoted once.
        context.RecordResolvedInput(JsonSerializer.Serialize(new
        {
            graphicId = row.Id,
            documentId = row.DocumentId,
            description = row.Description,
            placement = request.Placement,
            frameWidth = row.FrameWidth,
            frameHeight = row.FrameHeight,
            revisesLayerId = row.RevisesLayerId,
            baseVersionId = row.BaseVersionId
        }, Json));

        GraphicDesignOutcome outcome = await _runner.RunAsync(request, new StepObserver(context), context.CancellationToken);

        string output = JsonSerializer.Serialize(new
        {
            graphicId = row.Id,
            succeeded = outcome.Succeeded,
            width = outcome.Result?.Width,
            height = outcome.Result?.Height,
            versionNumber = outcome.Result?.VersionNumber,
            summary = outcome.AgentAnswer?.Summary,
            onScreenText = outcome.AgentAnswer?.OnScreenText is { Count: > 0 } words ? words : null,
            notes = string.IsNullOrWhiteSpace(outcome.AgentAnswer?.Notes) ? null : outcome.AgentAnswer!.Notes,
            warnings = outcome.Warnings.Count > 0 ? outcome.Warnings : null,
            failureCode = outcome.FailureCode,
            failure = outcome.FailureMessage
        }, Json);

        if (!outcome.Succeeded)
        {
            _logger.LogWarning(
                "DesignGraphic step of execution {ExecutionId} failed: {Code} {Message}",
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
            Status = outcome.Succeeded ? StepStatus.Completed : StepStatus.Failed,
            ErrorDetails = outcome.Succeeded ? null : outcome.FailureMessage,
            FailureClass = outcome.Succeeded ? FailureClass.None : FailureClassifier.ForCode(outcome.FailureCode),
            FailureCode = outcome.FailureCode
        };
    }

    private static StepExecutionResult Failure(StepExecutionContext context, string code, string message)
    {
        context.RecordFailure(FailureClassifier.ForCode(code), code);
        return new StepExecutionResult
        {
            Output = JsonSerializer.Serialize(new { succeeded = false, failureCode = code, failure = message }, Json),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            Status = StepStatus.Failed,
            ErrorDetails = message,
            FailureClass = FailureClassifier.ForCode(code),
            FailureCode = code
        };
    }

    /// <summary>Forwards the runner's stages to the step's ephemeral progress events (never fails the step).</summary>
    private sealed class StepObserver(StepExecutionContext context) : IGraphicDesignObserver
    {
        public Task OnStageAsync(string stage, int? percentComplete, CancellationToken ct) =>
            context.ReportProgressAsync(stage, percentComplete);
    }
}
