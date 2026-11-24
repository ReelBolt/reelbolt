using System.Diagnostics;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// One revision to run: what to change, where, from which version, and in which sandbox. Front-end
/// neutral — the execution-backed <c>ReviseStepExecutor</c> builds it from a <c>composition_revisions</c>
/// row; the editor-v2 live front-end will build it from a timeline selection on a warm sandbox.
/// </summary>
public sealed record CompositionRevisionRequest
{
    public required Guid ProjectId { get; init; }
    public required Guid OrganizationId { get; init; }

    /// <summary>The sandbox key: the revision runs in THIS execution's sandbox, never the source run's.</summary>
    public required Guid ExecutionId { get; init; }

    public required string CorrelationId { get; init; }
    public required Guid WorkspaceId { get; init; }

    /// <summary>The version to start from. Null only together with <see cref="RecoverFromExecutionId"/>.</summary>
    public Guid? BaseVersionId { get; init; }

    /// <summary>A pre-capture run whose still-existing sandbox is recovered as the workspace's first version.</summary>
    public Guid? RecoverFromExecutionId { get; init; }

    public required string Instruction { get; init; }
    public CompositionRevisionTargets? Targets { get; init; }
    public CompositionRevisionMode Mode { get; init; } = CompositionRevisionMode.Render;
    public Guid? RevisionId { get; init; }
    public Guid? RequestedByUserId { get; init; }

    /// <summary>The <c>agent_definitions</c> row driving the agent (per-agent provider override); null = the built-in default.</summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>What a previous attempt was told was wrong, verbatim.</summary>
    public string? RetryGuidance { get; init; }

    /// <summary>First-party note naming steps the same change request re-made upstream (no user text); null for a plain revision.</summary>
    public string? AlsoRemadeNote { get; init; }

    /// <summary>
    /// When true the sandbox already holds the base version (a warm sandbox in the live front-end) and
    /// the restore is skipped. The execution-backed front-end always restores.
    /// </summary>
    public bool SandboxAlreadyRestored { get; init; }

    /// <summary>
    /// The Revise step result this attempt belongs to, carried on the agent's execution scope so the
    /// render tool keys its upload per attempt (a retried Revise must not overwrite the superseded
    /// attempt's render). Null for the live front-end, which has no step result: the legacy key.
    /// </summary>
    public Guid? StepResultId { get; init; }
}

/// <summary>
/// Where a revision reports its stages. The execution-backed front-end forwards them to the step's
/// ephemeral progress events; a live front-end would stream them to the editor.
/// </summary>
public interface ICompositionRevisionObserver
{
    Task OnStageAsync(string stage, int? percentComplete, CancellationToken ct);
}

/// <summary>The result of a revision, success or failure — never an exception for an expected failure.</summary>
public sealed record CompositionRevisionOutcome
{
    public required bool Succeeded { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public CompositionWorkspaceVersion? BaseVersion { get; init; }
    public CompositionWorkspaceVersion? ResultVersion { get; init; }
    public string? RenderStorageKey { get; init; }
    public CompositionRevisionOutput? AgentAnswer { get; init; }
    public string? AgentOutput { get; init; }
    public int TokensUsed { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public Guid? PrimaryProviderId { get; init; }
    public string? PrimaryModelName { get; init; }
    public IReadOnlyList<AgentToolCallTrace> ToolCalls { get; init; } = [];
    public IReadOnlyList<string> Reasoning { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Brief { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// Layer 3 of the library: the revision engine. Restore → brief → agent → verify → record, with every
/// layer below it behind an interface (store, capture/restore, sandbox session, agent registry), so the
/// same runner serves a run started from the execution page today and a live editor session tomorrow.
/// </summary>
public interface ICompositionRevisionRunner
{
    Task<CompositionRevisionOutcome> RunAsync(
        CompositionRevisionRequest request, ICompositionRevisionObserver? observer, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CompositionRevisionRunner : ICompositionRevisionRunner
{
    public const string RequestMissing = "REVISION_REQUEST_MISSING";

    /// <summary>
    /// Retired by T-020: a Render-mode revision that changed the project and did not render is now
    /// rendered by the platform (<see cref="RenderFailed"/> when that render fails), and one that
    /// changed nothing is <see cref="NoChanges"/>. No longer emitted; kept, with its classifier row,
    /// so failures recorded before the change still read and classify the same.
    /// </summary>
    public const string NotRendered = "REVISION_NOT_RENDERED";

    /// <summary>The agent changed the project without rendering and the platform's own render failed.</summary>
    public const string RenderFailed = "REVISION_RENDER_FAILED";

    /// <summary>The agent neither changed the project nor rendered it: nothing to show for the instruction.</summary>
    public const string NoChanges = "REVISION_NO_CHANGES";
    public const string AgentFailed = "REVISION_AGENT_FAILED";
    public const string AgentUnavailable = "REVISION_AGENT_UNAVAILABLE";

    private static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web);

    private readonly ICompositionWorkspaceStore _store;
    private readonly ICompositionWorkspaceCapture _capture;
    private readonly ICompositionWorkspaceRestorer _restorer;
    private readonly ISandboxSessionFactory _sandboxes;
    private readonly IAgentRegistry _agents;
    private readonly IWorkflowExecutionContextAccessor _contextAccessor;
    private readonly IAmazonS3 _s3;
    private readonly ICompositionRevisionRenderer _renderer;
    private readonly string _bucket;
    private readonly ILogger<CompositionRevisionRunner> _logger;

    public CompositionRevisionRunner(
        ICompositionWorkspaceStore store,
        ICompositionWorkspaceCapture capture,
        ICompositionWorkspaceRestorer restorer,
        ISandboxSessionFactory sandboxes,
        IAgentRegistry agents,
        IWorkflowExecutionContextAccessor contextAccessor,
        IAmazonS3 s3,
        IConfiguration configuration,
        ILogger<CompositionRevisionRunner> logger,
        ReactRemotionSandboxTools renderTools)
        : this(store, capture, restorer, sandboxes, agents, contextAccessor, s3, configuration, logger,
            (ICompositionRevisionRenderer)renderTools)
    {
    }

    /// <summary>The seam constructor: any <see cref="ICompositionRevisionRenderer"/> (tests script one).</summary>
    internal CompositionRevisionRunner(
        ICompositionWorkspaceStore store,
        ICompositionWorkspaceCapture capture,
        ICompositionWorkspaceRestorer restorer,
        ISandboxSessionFactory sandboxes,
        IAgentRegistry agents,
        IWorkflowExecutionContextAccessor contextAccessor,
        IAmazonS3 s3,
        IConfiguration configuration,
        ILogger<CompositionRevisionRunner> logger,
        ICompositionRevisionRenderer renderer)
    {
        _renderer = renderer;
        _store = store;
        _capture = capture;
        _restorer = restorer;
        _sandboxes = sandboxes;
        _agents = agents;
        _contextAccessor = contextAccessor;
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    public async Task<CompositionRevisionOutcome> RunAsync(
        CompositionRevisionRequest request, ICompositionRevisionObserver? observer, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ISandboxSession session = _sandboxes.Create(request.ExecutionId);

        // 1. The version to start from — recovered from an old run's sandbox when none was captured.
        await ReportAsync(observer, "Preparing your project", 5, ct);
        CompositionWorkspaceVersion? baseVersion;
        try
        {
            baseVersion = await ResolveBaseVersionAsync(request, ct);
        }
        catch (CompositionWorkspaceException ex)
        {
            return Fail(ex.Code, ex.Message, clock);
        }

        if (baseVersion is null)
        {
            return Fail(
                CompositionWorkspaceException.Unavailable,
                "There is no saved copy of this video's project to change, and the run that made it can no longer be recovered.",
                clock);
        }

        // A version made by a run that began at or before the project's last quarantine may hold a copy of that file,
        // whether it is restored here or arrives in an already-warm sandbox. Same answer as a missing copy.
        if (await _store.PredatesQuarantineAsync(baseVersion, ct))
        {
            return Fail(
                CompositionWorkspaceException.Unavailable,
                "There is no saved copy of this video's project to change, and the run that made it can no longer be recovered.",
                clock);
        }

        // 2. Restore it into this execution's fresh sandbox.
        IReadOnlyList<string> warnings = [];
        if (!request.SandboxAlreadyRestored)
        {
            await ReportAsync(observer, "Restoring your project", 15, ct);
            try
            {
                CompositionRestoreResult restored = await _restorer.RestoreAsync(session, baseVersion, ct);
                warnings = restored.Warnings;
            }
            catch (CompositionWorkspaceException ex)
            {
                return Fail(ex.Code, ex.Message, clock, baseVersion);
            }
            catch (SandboxRequestFailedException ex)
            {
                return Fail(CompositionWorkspaceException.RestoreFailed, ex.Message, clock, baseVersion);
            }
        }

        // 3. The agent, in a scope whose lineage makes every render it captures a child of the base.
        IReelBoltAgent? agent = _agents.GetByType(AgentType.CompositionReviser);
        if (agent is null)
            return Fail(AgentUnavailable, "The revision agent is not registered in this engine.", clock, baseVersion);

        string brief = CompositionRevisionBrief.Build(
            request.Instruction, request.Targets, request.Mode, baseVersion, warnings, request.RetryGuidance, request.AlsoRemadeNote);

        CompositionLineage lineage = new()
        {
            WorkspaceId = baseVersion.WorkspaceId,
            ParentVersionId = baseVersion.Id,
            Instruction = request.Instruction,
            RevisionId = request.RevisionId,
            RequestedByUserId = request.RequestedByUserId
        };

        // What the project looked like before the agent, so a revision that rendered nothing can tell
        // "changed but not rendered" (the platform renders it) from "changed nothing". Null = unknown.
        bool renderMode = request.Mode == CompositionRevisionMode.Render;
        string? digestBefore = renderMode
            ? await ReactRemotionSandboxTools.TryDigestCompositionSourcesAsync(session, _logger, ct)
            : null;

        await ReportAsync(observer, "Making your changes", 30, ct);
        AgentRunResult result;
        string? pendingRender;
        string? abortReason;
        CompositionRevisionOutput? answer;
        string? renderFailure = null;
        bool nothingChanged = false;
        using (_contextAccessor.BeginScope(
            request.ExecutionId, request.ProjectId, request.CorrelationId, request.OrganizationId, stepResultId: request.StepResultId))
        {
            WorkflowExecutionContext scope = _contextAccessor.Current!;
            scope.CompositionLineage = lineage;
            result = await agent.RunAsync(brief, request.AgentDefinitionId, ct);
            pendingRender = scope.PendingOutputStorageKey;
            abortReason = scope.AbortReason;
            answer = ParseAnswer(result.Output);

            // A Render-mode revision always ends with a render or a clear failure: when the agent
            // finished without rendering, the platform renders ONCE itself, through the agent's own
            // render tool and inside this same scope (so the lineage captures it as the child version).
            if (renderMode && result.Success && abortReason is null && string.IsNullOrWhiteSpace(pendingRender))
            {
                string? digestAfter = await ReactRemotionSandboxTools.TryDigestCompositionSourcesAsync(session, _logger, ct);
                nothingChanged = digestBefore is not null && digestAfter is not null
                    && string.Equals(digestBefore, digestAfter, StringComparison.Ordinal);
                string? compositionId = NullIfEmpty(answer?.CompositionId) ?? NullIfEmpty(baseVersion.CompositionId);
                if (!nothingChanged && compositionId is null)
                {
                    renderFailure = "the film's composition id is not known, so it cannot be rendered.";
                }
                else if (!nothingChanged)
                {
                    _logger.LogInformation(
                        "Revision of workspace {WorkspaceId} changed the project without rendering; rendering {CompositionId} for it",
                        baseVersion.WorkspaceId, compositionId);
                    (pendingRender, renderFailure) = await PlatformRenderAsync(scope, compositionId, baseVersion, observer, ct);

                    // A render refused BEFORE it ran (HTML5 <Video>, a missing staticFile, a network font), or one
                    // that crashed on the composition's OWN code (a ReferenceError, an unresolved import, a React or
                    // Remotion API error: production run a2be36db), is a code fix the agent can make, not a platform
                    // failure: the agent gets ONE repair turn in this same scope and sandbox with the error quoted,
                    // then the platform renders once more if the agent changed something without rendering. Never a
                    // second repair turn; a timeout, a killed renderer or an unreadable video gets none.
                    AgentFixableRenderFailureKind fixable = AgentFixableRenderFailure.Classify(renderFailure);
                    if (renderFailure is not null && fixable != AgentFixableRenderFailureKind.None)
                    {
                        string refusal = renderFailure;
                        _logger.LogWarning(
                            "The platform render of revision workspace {WorkspaceId} failed on something the reviser can fix ({Kind}); giving it one repair turn: {Refusal}",
                            baseVersion.WorkspaceId, fixable, refusal);
                        await ReportAsync(observer, "Fixing what stopped the render", 85, ct);
                        string? digestBeforeRepair = await ReactRemotionSandboxTools.TryDigestCompositionSourcesAsync(session, _logger, ct);
                        AgentRunResult? repair = null;
                        try
                        {
                            repair = await agent.RunAsync(
                                CompositionRevisionBrief.BuildRenderRepair(request.Instruction, compositionId, refusal, fixable),
                                request.AgentDefinitionId, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger.LogWarning(ex, "The render-repair turn of revision workspace {WorkspaceId} failed", baseVersion.WorkspaceId);
                        }

                        if (repair is not null)
                        {
                            result = CombineRuns(result, repair);
                            answer ??= ParseAnswer(repair.Output);
                        }

                        pendingRender = scope.PendingOutputStorageKey;
                        if (!string.IsNullOrWhiteSpace(pendingRender))
                        {
                            renderFailure = null;
                        }
                        else if (repair is { Success: true })
                        {
                            string? digestAfterRepair = await ReactRemotionSandboxTools.TryDigestCompositionSourcesAsync(session, _logger, ct);
                            bool repairChangedNothing = digestBeforeRepair is not null && digestAfterRepair is not null
                                && string.Equals(digestBeforeRepair, digestAfterRepair, StringComparison.Ordinal);
                            if (!repairChangedNothing)
                                (pendingRender, renderFailure) = await PlatformRenderAsync(scope, compositionId, baseVersion, observer, ct);
                            else
                                renderFailure = refusal;
                        }
                        else
                        {
                            renderFailure = refusal;
                        }
                    }
                }
            }
        }

        CompositionRevisionOutcome Partial(bool ok, string? code, string? message, CompositionWorkspaceVersion? resultVersion) => new()
        {
            Succeeded = ok,
            FailureCode = code,
            FailureMessage = message,
            BaseVersion = baseVersion,
            ResultVersion = resultVersion,
            RenderStorageKey = pendingRender,
            AgentAnswer = answer,
            AgentOutput = result.Output,
            TokensUsed = result.TokensUsed,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            ToolCalls = result.ToolCalls,
            Reasoning = result.Reasoning,
            Warnings = warnings,
            Brief = brief,
            DurationMs = clock.ElapsedMilliseconds
        };

        if (!result.Success || abortReason is not null)
        {
            string message = abortReason ?? result.FailureReason ?? "The revision agent did not finish.";
            return Partial(false, AgentFailed, message, null);
        }

        // 4. Verify the deliverable and make sure it is recorded as a version.
        await ReportAsync(observer, "Saving the new version", 95, ct);
        CompositionWorkspaceVersion? resultVersion = lineage.LastCapturedVersionId is { } capturedId
            ? await _store.FindVersionAsync(capturedId, ct)
            : null;

        if (request.Mode == CompositionRevisionMode.Render)
        {
            if (nothingChanged)
            {
                return Partial(false, NoChanges,
                    "Nothing in the video was changed, so there is no new version to show. "
                    + "Please describe the change more specifically: which part of the video, and what it should look like.", null);
            }

            if (renderFailure is not null || string.IsNullOrWhiteSpace(pendingRender))
            {
                return Partial(false, RenderFailed,
                    "The changes were made but the new video could not be rendered: " + (renderFailure ?? "no video was produced."), null);
            }

            // The render tool's own capture is best effort; when it did not land, capture now so the
            // render is never left without a revisable source.
            resultVersion ??= (await _capture.TryCaptureAsync(session, new CompositionCaptureRequest
            {
                ProjectId = request.ProjectId,
                OrganizationId = request.OrganizationId,
                ExecutionId = request.ExecutionId,
                Lineage = lineage,
                Source = CompositionVersionSource.RenderCapture,
                CompositionId = NullIfEmpty(answer?.CompositionId) ?? baseVersion.CompositionId,
                Fps = baseVersion.Fps,
                Width = baseVersion.Width,
                Height = baseVersion.Height,
                DurationInFrames = baseVersion.DurationInFrames,
                RenderOutputStorageKey = pendingRender
            }, ct)).Version;
        }
        else
        {
            resultVersion ??= (await _capture.TryCaptureAsync(session, new CompositionCaptureRequest
            {
                ProjectId = request.ProjectId,
                OrganizationId = request.OrganizationId,
                ExecutionId = request.ExecutionId,
                Lineage = lineage,
                Source = CompositionVersionSource.PreviewSnapshot,
                CompositionId = NullIfEmpty(answer?.CompositionId) ?? baseVersion.CompositionId,
                Fps = baseVersion.Fps,
                Width = baseVersion.Width,
                Height = baseVersion.Height,
                DurationInFrames = baseVersion.DurationInFrames
            }, ct)).Version;
        }

        if (resultVersion is not null && request.RevisionId is { } revisionId)
        {
            try
            {
                await _store.LinkRevisionResultAsync(revisionId, resultVersion.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Revision {RevisionId} produced version {VersionId} but the link could not be saved", revisionId, resultVersion.Id);
            }
        }

        _logger.LogInformation(
            "Revision of workspace {WorkspaceId} from v{BaseVersion} finished in {ElapsedMs}ms: new version {ResultVersion}, render {RenderKey}",
            baseVersion.WorkspaceId, baseVersion.VersionNumber, clock.ElapsedMilliseconds,
            resultVersion?.VersionNumber, pendingRender);

        return Partial(true, null, null, resultVersion);
    }

    /// <summary>
    /// The platform's one render through the agent's own render tool, inside <paramref name="scope"/>: the
    /// recorded output key on success, else null and the render's own error text.
    /// </summary>
    private async Task<(string? PendingRender, string? Failure)> PlatformRenderAsync(
        WorkflowExecutionContext scope, string compositionId, CompositionWorkspaceVersion baseVersion,
        ICompositionRevisionObserver? observer, CancellationToken ct)
    {
        await ReportAsync(observer, "Rendering your video", 80, ct);
        string? failure = null;
        string? pending = null;
        try
        {
            CompositionRevisionRenderResult rendered = await _renderer.RenderAsync(
                compositionId, CompositionRevisionBrief.OutputFileName, ct);
            pending = scope.PendingOutputStorageKey;
            if (!rendered.Succeeded || string.IsNullOrWhiteSpace(pending))
                failure = rendered.Error ?? "The render did not produce a video.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The platform render of revision workspace {WorkspaceId} failed", baseVersion.WorkspaceId);
            failure = ex.Message;
        }

        return failure is null ? (pending, null) : (null, failure);
    }

    /// <summary>Whether a failed render's error text is a pre-render refusal of the render tool (see <see cref="AgentFixableRenderFailure"/>).</summary>
    internal static bool IsFixablePreRenderRefusal(string? error) =>
        AgentFixableRenderFailure.Classify(error) == AgentFixableRenderFailureKind.PreRenderRefusal;

    /// <summary>The first turn and the repair turn as one run: the repair's answer and status, the usage and traces of both.</summary>
    internal static AgentRunResult CombineRuns(AgentRunResult first, AgentRunResult repair) => new()
    {
        Output = repair.Success && !string.IsNullOrWhiteSpace(repair.Output) ? repair.Output : first.Output,
        Success = first.Success,
        FailureReason = first.FailureReason,
        TokensUsed = first.TokensUsed + repair.TokensUsed,
        InputTokens = first.InputTokens is null && repair.InputTokens is null ? null : (first.InputTokens ?? 0) + (repair.InputTokens ?? 0),
        OutputTokens = first.OutputTokens is null && repair.OutputTokens is null ? null : (first.OutputTokens ?? 0) + (repair.OutputTokens ?? 0),
        PrimaryProviderId = first.PrimaryProviderId ?? repair.PrimaryProviderId,
        PrimaryModelName = first.PrimaryModelName ?? repair.PrimaryModelName,
        ToolCalls = [.. first.ToolCalls, .. repair.ToolCalls],
        Reasoning = [.. first.Reasoning, .. repair.Reasoning],
        ModelCalls = first.ModelCalls is null && repair.ModelCalls is null ? null : (first.ModelCalls ?? 0) + (repair.ModelCalls ?? 0),
        PeakContextChars = first.PeakContextChars is null ? repair.PeakContextChars
            : repair.PeakContextChars is null ? first.PeakContextChars : Math.Max(first.PeakContextChars.Value, repair.PeakContextChars.Value),
        CharsCompacted = first.CharsCompacted is null ? repair.CharsCompacted
            : repair.CharsCompacted is null ? first.CharsCompacted : Math.Max(first.CharsCompacted.Value, repair.CharsCompacted.Value),
        ToolLoopCapReached = first.ToolLoopCapReached || repair.ToolLoopCapReached
    };

    /// <summary>
    /// The base version: the requested one (which must belong to the requested workspace and project),
    /// or — for a run made before capture existed — a recovery of that run's sandbox while it still
    /// exists (stopped containers keep their workspace until the sandbox TTL; an executor restart purges
    /// them all). Recovery runs at most once per workspace: a second request finds the recovered version.
    /// </summary>
    private async Task<CompositionWorkspaceVersion?> ResolveBaseVersionAsync(CompositionRevisionRequest request, CancellationToken ct)
    {
        if (request.BaseVersionId is { } baseId)
        {
            CompositionWorkspaceVersion? version = await _store.FindVersionAsync(baseId, ct);
            return version is not null && version.WorkspaceId == request.WorkspaceId && version.ProjectId == request.ProjectId
                ? version
                : null;
        }

        if (await _store.LatestVersionAsync(request.WorkspaceId, ct) is { } latest)
            return latest;

        if (request.RecoverFromExecutionId is not { } sourceExecutionId)
            return null;

        // The source run's sandbox may hold a file quarantined since when the run began at or before the marker; nothing
        // is captured from it. Same answer as a sandbox that is gone.
        if (await _store.RunPredatesQuarantineAsync(request.ProjectId, sourceExecutionId, ct))
        {
            throw new CompositionWorkspaceException(
                CompositionWorkspaceException.Unavailable,
                "There is no saved copy of this video's project to change, and the run that made it can no longer be recovered.");
        }

        string? renderKey = await _store.FindLatestRenderKeyAsync(sourceExecutionId, ct);
        string? manifestJson = renderKey is null ? null : await TryReadSidecarAsync(renderKey, ct);
        CompositionSceneManifest? manifest = CompositionRevisionBrief.ReadManifest(manifestJson);

        ISandboxSession sourceSession = _sandboxes.Create(sourceExecutionId);
        CompositionCaptureResult recovered;
        try
        {
            recovered = await _capture.CaptureAsync(sourceSession, new CompositionCaptureRequest
            {
                ProjectId = request.ProjectId,
                OrganizationId = request.OrganizationId,
                ExecutionId = sourceExecutionId,
                CreatedByExecutionId = sourceExecutionId,
                Lineage = new CompositionLineage { WorkspaceId = request.WorkspaceId },
                Source = CompositionVersionSource.SandboxRecovery,
                CompositionId = manifest?.CompositionId,
                Fps = manifest?.Fps > 0 ? manifest.Fps : null,
                DurationInFrames = manifest is { Scenes.Count: > 0 }
                    ? manifest.Scenes.Max(s => s.FromFrame + s.DurationInFrames)
                    : null,
                SceneManifestJson = manifestJson,
                RenderOutputStorageKey = renderKey
            }, ct);
        }
        catch (SandboxRequestFailedException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new CompositionWorkspaceException(
                CompositionWorkspaceException.Unavailable,
                "This video was made before changes were saved automatically, and its project is no longer available "
                + "(it is kept for about a week after the run). Run the workflow again to make a video that can be changed.", ex);
        }
        catch (SandboxOperationNotSupportedException ex)
        {
            throw new CompositionWorkspaceException(CompositionWorkspaceException.TransferNotSupported, ex.Message, ex);
        }

        return recovered.Version;
    }

    private async Task<string?> TryReadSidecarAsync(string renderKey, CancellationToken ct)
    {
        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(_bucket, CompositionSceneManifest.SidecarKeyFor(renderKey), ct);
            using StreamReader reader = new(response.ResponseStream);
            string json = await reader.ReadToEndAsync(ct);
            return json.Length <= 1_000_000 ? json : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "No scene manifest beside {RenderKey}", renderKey);
            return null;
        }
    }

    internal static CompositionRevisionOutput? ParseAnswer(string? output)
    {
        string? json = RobustJsonExtractor.ExtractLastJsonObject(output ?? string.Empty);
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<CompositionRevisionOutput>(json, AnswerJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task ReportAsync(ICompositionRevisionObserver? observer, string stage, int? percent, CancellationToken ct)
    {
        if (observer is null)
            return;
        try
        {
            await observer.OnStageAsync(stage, percent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Progress is UI signal; it never changes the outcome of a revision.
        }
    }

    private static CompositionRevisionOutcome Fail(
        string code, string message, Stopwatch clock, CompositionWorkspaceVersion? baseVersion = null) => new()
        {
            Succeeded = false,
            FailureCode = code,
            FailureMessage = message,
            BaseVersion = baseVersion,
            DurationMs = clock.ElapsedMilliseconds
        };
}
