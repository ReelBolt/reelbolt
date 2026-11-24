using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>A frame window of the film, <c>[fromFrame, toFrame)</c>, as the assistant names it.</summary>
public sealed record RevisionFrameRangeArg(int FromFrame, int ToFrame);

/// <summary>
/// Composition revisions as assistant and MCP tools: <c>ProposeReviseComposition</c> reads the film's history and
/// prices one more revision, <c>ApplyReviseComposition</c> queues it through the same
/// <see cref="ICompositionRevisionService"/> the REST endpoint uses. A revision holds model credits, so it counts
/// in the guard's PAID bucket next to paid workflow runs.
/// </summary>
/// <remarks>Not registered in <c>AssistantToolProvider</c> here; the integration ticket composes it.</remarks>
public sealed class AssistantRevisionTools
{
    internal const string OpReviseComposition = "ReviseComposition";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";
    internal const string ExecutionNotFoundMessage = "execution not found";
    internal const string DefaultCutPolicy = nameof(CompositionCutPolicy.KeepCut);

    /// <summary>Same ceiling as the REST request.</summary>
    internal const int MaxInstructionChars = 4000;

    /// <summary>Progress captions, in <c>AssistantToolProgress</c>'s voice, for the integration ticket to merge.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeReviseComposition"] = "Checking what changing the video would involve",
        ["ApplyReviseComposition"] = "Starting the video changes",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICompositionRevisionService _revisions;
    private readonly IAssistantExecutionGuard _guard;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly ICurrentUser _currentUser;
    private readonly ICreditAccountProvisioner? _trialCredits;
    private readonly IWorkflowCostEstimator? _costEstimator;

    private readonly ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? _productEvents;

    public AssistantRevisionTools(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        ICompositionRevisionService revisions,
        IAssistantExecutionGuard guard,
        IConfirmationTokenService confirmationTokens,
        ICurrentUser currentUser,
        ICreditAccountProvisioner? trialCredits = null,
        IWorkflowCostEstimator? costEstimator = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _costEstimator = costEstimator;
        _trialCredits = trialCredits;
        _db = db;
        _projectAccess = projectAccess;
        _revisions = revisions;
        _guard = guard;
        _confirmationTokens = confirmationTokens;
        _currentUser = currentUser;
    }

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string targetsNote =
            " scenes are scene names from availableScenes; frameRanges are [fromFrame, toFrame) windows; cutPolicy is KeepCut (default: keep the approved edit) or ReEdit (re-make editorial decisions too); fromVersionId revises an earlier version instead of the latest.";
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (Guid executionId, string instruction, string[]? scenes = null, RevisionFrameRangeArg[]? frameRanges = null, Guid? fromVersionId = null, string? cutPolicy = null, Guid? projectId = null)
                    => ProposeAsync(context, executionId, instruction, scenes, frameRanges, fromVersionId, cutPolicy, projectId),
                name: "ProposeReviseComposition",
                description: "Proposes CHANGING a finished animated video (a run's Remotion film) and returns a confirmationToken plus what the user is agreeing to: the targetable scenes (availableScenes), any scene names that do not exist (unknownScenes), and the expected credit cost (estimate: billingEnabled, low, high, required, balance, affordable), WITHOUT starting anything. Refuses with the reason (code RUN_NOT_FINISHED, WORKSPACE_UNAVAILABLE...) when the run cannot be changed. instruction is at most 4000 characters, scenes at most 50, frameRanges at most 20;" + targetsNote + " " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyReviseComposition."),
            AIFunctionFactory.Create(
                (Guid executionId, string instruction, string confirmationToken, string[]? scenes = null, RevisionFrameRangeArg[]? frameRanges = null, Guid? fromVersionId = null, string? cutPolicy = null, Guid? projectId = null)
                    => ApplyAsync(context, executionId, instruction, confirmationToken, scenes, frameRanges, fromVersionId, cutPolicy, projectId),
                name: "ApplyReviseComposition",
                description: "STARTS a revision of a finished animated video: queues a new run that applies the instruction (it spends credits and counts as one paid run toward the hourly limit). confirmationToken is verified against the proposal; a fabricated or stale token, or a changed executionId, instruction or target, is refused and nothing starts." + targetsNote + " " + AssistantProjectScope.ProjectIdNote + " Returns revisionId, executionId (the NEW run to follow with GetWorkflowExecution), status and rateLimit; or {error, code, ...} such as INSUFFICIENT_CREDITS, PLAN_NOT_ENTITLED, REVISION_RATE_LIMITED or RUN_RATE_LIMITED (each with its numbers and retryAfterSeconds where it has one)."),
        };
    }

    private async Task<string> ProposeAsync(
        AssistantToolContext context, Guid executionId, string? instruction, string[]? scenes,
        RevisionFrameRangeArg[]? frameRanges, Guid? fromVersionId, string? cutPolicy, Guid? projectId)
    {
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.View, projectId, out string scopeError);
        if (project is null)
            return scopeError;

        if (!TryBuildRequest(instruction, scenes, frameRanges, fromVersionId, cutPolicy, out CreateCompositionRevisionRequest? body, out string? invalid))
            return Error(invalid!, "INVALID_REQUEST");

        if (_trialCredits is not null)
            await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, CancellationToken.None);

        CompositionRevisionHistoryResponse? history = await _revisions.GetHistoryAsync(project, executionId, _currentUser.OrganizationId, CancellationToken.None, body!.FromVersionId);
        if (history is null)
            return Error(ExecutionNotFoundMessage, "NOT_FOUND");

        if (!history.CanRevise)
        {
            // Available-but-not-canRevise is the unfinished run; unavailable is the workspace.
            string code = history.Availability == CompositionRevisionService.AvailabilityUnavailable ? "WORKSPACE_UNAVAILABLE" : "RUN_NOT_FINISHED";
            return Error(history.UnavailableReason ?? "This video cannot be changed.", code);
        }

        string workflowName = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ProjectId == project.Id)
            .Join(_db.WorkflowDefinitions, e => e.WorkflowDefinitionId, w => w.Id, (_, w) => w.Name)
            .FirstOrDefaultAsync() ?? string.Empty;

        HashSet<string> known = history.Scenes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        string[] unknown = (body!.Targets?.Scenes ?? []).Where(s => !known.Contains(s)).Distinct(StringComparer.Ordinal).ToArray();

        RevisionCostEstimateResponse? estimate = PickEstimate(history, body.CutPolicy!);
        string token = _confirmationTokens.Compute(OpReviseComposition, Payload(project.Id, executionId, body), context.UserId, _currentUser.OrganizationId);

        return JsonSerializer.Serialize(new
        {
            operation = OpReviseComposition,
            confirmationToken = token,
            summary = Summary(project.Name, workflowName, body, estimate),
            projectName = project.Name,
            workflowName,
            availableScenes = history.Scenes.Select(s => new { name = s.Name, fromFrame = s.FromFrame, durationInFrames = s.DurationInFrames, description = s.Description }),
            unknownScenes = unknown,
            estimate = EstimateJson(estimate),
            cutPolicy = body.CutPolicy,
            replay = history.Replay is { Available: true } replay
                ? new { available = true, hasEditDecisions = replay.HasEditDecisions, steps = replay.Steps.Select(s => s.Label) }
                : null,
        }, Json);
    }

    private async Task<string> ApplyAsync(
        AssistantToolContext context, Guid executionId, string? instruction, string? confirmationToken, string[]? scenes,
        RevisionFrameRangeArg[]? frameRanges, Guid? fromVersionId, string? cutPolicy, Guid? projectId)
    {
        if (!_currentUser.EmailVerified)
            return Error(EmailVerificationGate.Message, EmailVerificationGate.ErrorCode);

        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string scopeError);
        if (project is null)
            return scopeError;

        if (!TryBuildRequest(instruction, scenes, frameRanges, fromVersionId, cutPolicy, out CreateCompositionRevisionRequest? body, out string? invalid))
            return Error(invalid!, "INVALID_REQUEST");

        if (!_confirmationTokens.Verify(OpReviseComposition, Payload(project.Id, executionId, body!), context.UserId, _currentUser.OrganizationId, confirmationToken ?? string.Empty))
            return Error(StaleTokenMessage);

        // The same lazy trial grant the REST revision route runs before its credit preflight, so a new
        // workspace is not refused INSUFFICIENT_CREDITS for credits it is about to be given.
        if (_trialCredits is not null)
            await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, CancellationToken.None);

        ExecutionInitiator initiator = context.Initiator == ExecutionInitiator.Mcp ? ExecutionInitiator.Mcp : ExecutionInitiator.Assistant;
        CompositionRevisionRequestResult? refused = null;
        PlanGateViolation? planRefusal = null;
        CompositionRevisionResponse? created = null;

        RunRateDecision decision = await _guard.TryAcquireAsync(
            context.UserId, project.Id, RunRateBucket.Paid, RunRateEvent.RevisionSource, async () =>
            {
                try
                {
                    CompositionRevisionRequestResult result = await _revisions.RequestAsync(
                        project, executionId, context.UserId, _currentUser.OrganizationId, body!, CancellationToken.None, initiator);
                    if (result.Created is null)
                    {
                        refused = result;
                        return null;
                    }

                    created = result.Created;
                    return created.ExecutionId;
                }
                catch (PlanLimitException limit)
                {
                    planRefusal = limit.Violation;
                    return null;
                }
            }, CancellationToken.None);

        if (planRefusal is not null)
        {
            return JsonSerializer.Serialize(new { error = planRefusal.Message, code = planRefusal.Code, planGate = planRefusal.ToBody() }, Json);
        }

        if (!decision.Allowed)
            return AssistantToolProvider.RunRateRefusalJson(decision);

        if (refused is not null)
            return Refusal(refused);

        if (created is null)
            return Error("the revision could not be started");

        // product-analytics: same event as CompositionRevisionsController.Create. Best-effort.
        if (_productEvents is not null)
        {
            await _productEvents.AppendAsync(
                context.UserId, _currentUser.OrganizationId,
                ReelBolt.Inference.Api.Services.Telemetry.ProductEventNames.ChangeRequested, null, CancellationToken.None);
        }

        return JsonSerializer.Serialize(new
        {
            revisionId = created.Id,
            executionId = created.ExecutionId,
            status = created.Status,
            rateLimit = new { bucket = RunRateRefusal.BucketName(decision.Bucket), used = decision.Used, limit = decision.Limit },
        }, Json);
    }

    /// <summary>The service's own refusal, keeping its code and every extra field (required/balance, used/limit/retryAfterSeconds...).</summary>
    private static string Refusal(CompositionRevisionRequestResult result)
    {
        Dictionary<string, object?> body = new()
        {
            ["error"] = string.IsNullOrWhiteSpace(result.Message) ? "the revision could not be started" : result.Message,
            ["code"] = result.Code ?? result.StatusCode switch
            {
                400 => "INVALID_REQUEST",
                404 => "NOT_FOUND",
                409 => "CONFLICT",
                _ => "REVISION_REFUSED",
            },
        };
        foreach ((string key, object? value) in result.Extra ?? new Dictionary<string, object?>())
            body.TryAdd(key, value);
        return JsonSerializer.Serialize(body, Json);
    }

    private static bool TryBuildRequest(
        string? instruction, string[]? scenes, RevisionFrameRangeArg[]? frameRanges, Guid? fromVersionId, string? cutPolicy,
        out CreateCompositionRevisionRequest? body, out string? error)
    {
        body = null;
        string text = instruction?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "instruction is required: say what should change in the video.";
            return false;
        }

        if (text.Length > MaxInstructionChars)
        {
            error = $"instruction may be at most {MaxInstructionChars} characters.";
            return false;
        }

        string policy = DefaultCutPolicy;
        if (!string.IsNullOrWhiteSpace(cutPolicy))
        {
            if (!Enum.TryParse(cutPolicy.Trim(), ignoreCase: true, out CompositionCutPolicy parsed) || !Enum.IsDefined(parsed))
            {
                error = $"cutPolicy must be one of: {string.Join(", ", Enum.GetNames<CompositionCutPolicy>())}.";
                return false;
            }

            policy = parsed.ToString();
        }

        CompositionRevisionTargets targets = new()
        {
            Scenes = scenes is { Length: > 0 } ? scenes : null,
            FrameRanges = frameRanges is { Length: > 0 } ? frameRanges.Select(r => new CompositionFrameRange(r.FromFrame, r.ToFrame)).ToArray() : null,
        };
        IReadOnlyList<string> problems = targets.Validate();
        if (problems.Count > 0)
        {
            error = string.Join(" ", problems);
            return false;
        }

        body = new CreateCompositionRevisionRequest(text, targets.IsEmpty ? null : targets, fromVersionId, null, policy);
        error = null;
        return true;
    }

    /// <summary>The canonical payload the token is bound to: project, run, instruction, targets, base version and cut policy.</summary>
    private static string Payload(Guid projectId, Guid executionId, CreateCompositionRevisionRequest body) =>
        JsonSerializer.Serialize(new
        {
            projectId,
            executionId,
            instruction = body.Instruction,
            scenes = body.Targets?.Scenes ?? [],
            frameRanges = (body.Targets?.FrameRanges ?? []).Select(r => new[] { r.FromFrame, r.ToFrame }),
            fromVersionId = body.FromVersionId,
            cutPolicy = body.CutPolicy,
        });

    internal static RevisionCostEstimateResponse? PickEstimate(CompositionRevisionHistoryResponse history, string cutPolicy) =>
        history.Replay is { Available: true } replay && cutPolicy == nameof(CompositionCutPolicy.ReEdit)
            ? replay.ReEditEstimate ?? history.Estimate
            : history.Estimate;

    /// <summary>
    /// The estimate as the tool reports it. Billing on but no evaluable estimate is NOT "billing off": it is
    /// reported as <c>billingEnabled:true, estimateUnavailable:true</c> with no figures, and still counts as paid.
    /// </summary>
    private object EstimateJson(RevisionCostEstimateResponse? estimate)
    {
        if (estimate is null && _costEstimator is { BillingEnabled: true })
        {
            return new
            {
                billingEnabled = true,
                estimateUnavailable = true,
                low = (double?)null,
                high = (double?)null,
                required = (double?)null,
                balance = (double?)null,
                affordable = (bool?)null,
            };
        }

        return new
        {
            billingEnabled = estimate?.BillingEnabled ?? false,
            estimateUnavailable = false,
            low = (double?)(estimate?.LowCredits ?? 0),
            high = (double?)(estimate?.HighCredits ?? 0),
            required = (double?)(estimate?.RequiredCredits ?? 0),
            balance = estimate?.BalanceCredits,
            affordable = (bool?)(estimate?.Affordable ?? true),
        };
    }

    private string Summary(string projectName, string workflowName, CreateCompositionRevisionRequest body, RevisionCostEstimateResponse? estimate)
    {
        string instruction = body.Instruction!.Length > 200 ? body.Instruction[..200] + "…" : body.Instruction;
        string cost = estimate is { BillingEnabled: true }
            ? $" Estimated cost: {estimate.LowCredits:0.##}–{estimate.HighCredits:0.##} credits{(estimate.Affordable ? string.Empty : " (more than the current balance)")}."
            : estimate is null && _costEstimator is { BillingEnabled: true }
                ? " The cost could not be worked out in advance; it is charged as a paid run."
                : string.Empty;
        string where = body.Targets is null ? "the whole film" : "the chosen scenes/frames";
        return $"Change the video from workflow \"{workflowName}\" in project \"{projectName}\" ({where}): \"{instruction}\". "
            + $"This starts a new run and counts as one paid run toward the hourly limit.{cost}";
    }

    private static string Error(string message, string? code = null) =>
        code is null
            ? JsonSerializer.Serialize(new { error = message })
            : JsonSerializer.Serialize(new { error = message, code });
}
