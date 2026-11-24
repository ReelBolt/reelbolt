using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.DesignSystems;

namespace ReelBolt.WorkflowEngine.Services.Graphics;

/// <summary>One graphic to design, built from an <see cref="EditImageGraphicRequest"/> row.</summary>
public sealed record GraphicDesignRequest
{
    public required Guid GraphicId { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid OrganizationId { get; init; }
    public required Guid DocumentId { get; init; }

    /// <summary>The sandbox key: the graphic is designed in THIS execution's sandbox.</summary>
    public required Guid ExecutionId { get; init; }

    public required string CorrelationId { get; init; }
    public required string Description { get; init; }
    public required string Placement { get; init; }
    public required int FrameWidth { get; init; }
    public required int FrameHeight { get; init; }

    /// <summary>True when the frame is the graphic's own box rather than the whole picture.</summary>
    public bool Sized { get; init; }

    /// <summary>"Change this graphic": the version to restore. Null designs a new graphic from the scaffold.</summary>
    public Guid? BaseVersionId { get; init; }

    public Guid? RequestedByUserId { get; init; }
    public Guid? AgentDefinitionId { get; init; }
    public string? RetryGuidance { get; init; }
}

/// <summary>The result of a graphic design, success or failure — never an exception for an expected failure.</summary>
public sealed record GraphicDesignOutcome
{
    public required bool Succeeded { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public GraphicDesignResult? Result { get; init; }
    public GraphicDesignOutput? AgentAnswer { get; init; }
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

/// <summary>Where a graphic design reports its stages (the step's ephemeral progress events).</summary>
public interface IGraphicDesignObserver
{
    Task OnStageAsync(string stage, int? percentComplete, CancellationToken ct);
}

/// <summary>
/// Designs one still graphic: prepare the sandbox (scaffold or restore) → brief → GraphicDesigner agent →
/// render the still ITSELF → verify and crop → upload → record the source as a workspace version → record
/// the result. Every layer below it is behind an interface (store, capture/restore, sandbox session,
/// agent registry), the composition-revision library's own layers reused rather than copied.
/// </summary>
public interface IGraphicDesignRunner
{
    Task<GraphicDesignOutcome> RunAsync(GraphicDesignRequest request, IGraphicDesignObserver? observer, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GraphicDesignRunner : IGraphicDesignRunner
{
    public const string RequestMissing = "GRAPHIC_REQUEST_MISSING";
    public const string AgentUnavailable = "GRAPHIC_AGENT_UNAVAILABLE";
    public const string AgentFailed = "GRAPHIC_AGENT_FAILED";
    public const string BaseUnavailable = "GRAPHIC_BASE_UNAVAILABLE";
    public const string CompositionMissing = "GRAPHIC_COMPOSITION_MISSING";
    public const string NotRendered = "GRAPHIC_NOT_RENDERED";
    public const string UploadFailed = "GRAPHIC_UPLOAD_FAILED";

    /// <summary>Where the runner renders the still inside the sandbox (never a path the agent is told about).</summary>
    internal const string StillPath = "out/reelbolt-graphic/graphic.png";

    /// <summary>Server-side deadline for the still render and for reading it back.</summary>
    internal const int StillTimeoutSeconds = 300;

    private static readonly Regex CompositionIdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web);

    private readonly ICompositionWorkspaceStore _workspaces;
    private readonly ICompositionWorkspaceCapture _capture;
    private readonly ICompositionWorkspaceRestorer _restorer;
    private readonly IGraphicDesignRequestStore _requests;
    private readonly ISandboxSessionFactory _sandboxes;
    private readonly IAgentRegistry _agents;
    private readonly IWorkflowExecutionContextAccessor _contextAccessor;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ILogger<GraphicDesignRunner> _logger;

    public GraphicDesignRunner(
        ICompositionWorkspaceStore workspaces,
        ICompositionWorkspaceCapture capture,
        ICompositionWorkspaceRestorer restorer,
        IGraphicDesignRequestStore requests,
        ISandboxSessionFactory sandboxes,
        IAgentRegistry agents,
        IWorkflowExecutionContextAccessor contextAccessor,
        IAmazonS3 s3,
        IConfiguration configuration,
        ILogger<GraphicDesignRunner> logger)
    {
        _workspaces = workspaces;
        _capture = capture;
        _restorer = restorer;
        _requests = requests;
        _sandboxes = sandboxes;
        _agents = agents;
        _contextAccessor = contextAccessor;
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    public async Task<GraphicDesignOutcome> RunAsync(GraphicDesignRequest request, IGraphicDesignObserver? observer, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ISandboxSession session = _sandboxes.Create(request.ExecutionId);
        List<string> warnings = [];

        // 1. The sandbox: a restored version for "Change this graphic", a transparent scaffold otherwise.
        await ReportAsync(observer, "Preparing the canvas", 5, ct);
        CompositionWorkspaceVersion? baseVersion = null;
        string expectedComposition = GraphicDesignRules.ScaffoldCompositionId;
        string? previousRequest = null;
        if (request.BaseVersionId is { } baseId)
        {
            baseVersion = await _workspaces.FindVersionAsync(baseId, ct);
            if (baseVersion is null || baseVersion.ProjectId != request.ProjectId || baseVersion.Source != CompositionVersionSource.GraphicDesign)
            {
                return Fail(BaseUnavailable,
                    "The saved design of this graphic is no longer available, so it cannot be changed. Design a new graphic instead.", clock);
            }

            // A saved design from a run that began at or before the project's last quarantine may hold a copy of that
            // file: the same answer as a design that is gone (refused before any work, so the charge is refunded).
            if (await _workspaces.PredatesQuarantineAsync(baseVersion, ct))
            {
                return Fail(BaseUnavailable,
                    "The saved design of this graphic is no longer available, so it cannot be changed. Design a new graphic instead.", clock);
            }

            try
            {
                CompositionRestoreResult restored = await _restorer.RestoreAsync(session, baseVersion, ct);
                warnings.AddRange(restored.Warnings);
            }
            catch (CompositionWorkspaceException ex)
            {
                return Fail(ex.Code, ex.Message, clock);
            }
            catch (SandboxRequestFailedException ex)
            {
                return Fail(CompositionWorkspaceException.RestoreFailed, ex.Message, clock);
            }

            expectedComposition = string.IsNullOrWhiteSpace(baseVersion.CompositionId) ? expectedComposition : baseVersion.CompositionId;
            previousRequest = baseVersion.Instruction;
        }
        else
        {
            try
            {
                await session.EnsureAsync(ct);
                foreach ((string path, string content) in GraphicDesignBrief.Scaffold(request.FrameWidth, request.FrameHeight))
                    await session.WriteTextAsync(path, content, ct);
            }
            catch (SandboxRequestFailedException ex)
            {
                return Fail(CompositionWorkspaceException.RestoreFailed, "The design sandbox could not be prepared: " + ex.Message, clock);
            }
        }

        // 2. The agent.
        IReelBoltAgent? agent = _agents.GetByType(AgentType.GraphicDesigner);
        if (agent is null)
            return Fail(AgentUnavailable, "The graphic designer is not available on this installation.", clock);

        // The run's pinned brand, when it has one: the GraphicsPalette block carries the colours,
        // the sandbox font note and the logo's project file NAME (never its bytes or id). No pin,
        // or a pin with no design document, passes null so the brief stays byte-identical.
        DesignSystemRunState? brandState = DesignSystemAmbient.Current;
        string? brandBlock = brandState?.RenderDesignView(DesignView.GraphicsPalette);

        string brief = GraphicDesignBrief.Build(
            request.Description, request.FrameWidth, request.FrameHeight, request.Placement, request.Sized,
            expectedComposition, baseVersion is not null, previousRequest, warnings, request.RetryGuidance,
            brandBlock);

        // The sandbox has only its installed fonts (SandboxFonts), so report a brand font it lacks,
        // and the installed face drawn instead, in the outcome's notes (after the brief is built, so
        // it never becomes a restore warning).
        if (brandState?.Profile.Design is { } brandDesign)
        {
            IReadOnlyList<string> families = BrandFontMatcher.RequestedFamilies(brandDesign);
            if (families.Count > 0 && BrandFontMatcher.NearestSandboxFont(families).Warning is { } fontWarning)
                warnings.Add(fontWarning);
        }

        await ReportAsync(observer, "Designing your graphic", 20, ct);
        AgentRunResult result;
        string? abortReason;
        using (_contextAccessor.BeginScope(request.ExecutionId, request.ProjectId, request.CorrelationId, request.OrganizationId))
        {
            result = await agent.RunAsync(brief, request.AgentDefinitionId, ct);
            abortReason = _contextAccessor.Current?.AbortReason;
        }

        GraphicDesignOutput? answer = ParseAnswer(result.Output);
        GraphicDesignOutcome Done(bool ok, string? code, string? message, GraphicDesignResult? delivered) => new()
        {
            Succeeded = ok,
            FailureCode = code,
            FailureMessage = message,
            Result = delivered,
            AgentAnswer = answer,
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
            return Done(false, AgentFailed, abortReason ?? result.FailureReason ?? "The graphic designer did not finish.", null);

        // 3. Which composition draws it: the agent's answer when it names a registered one, else the
        //    scaffold's (or the restored version's). Read from the CLI, never from the answer alone.
        await ReportAsync(observer, "Rendering the picture", 80, ct);

        // The sandbox has no internet: a composition that loads a font from the network would fail
        // inside `remotion compositions`/`still`, so refuse it before either runs.
        if (await NetworkFontPreflight.CheckAsync(session, ct) is { } networkFont)
            return Done(false, NotRendered, networkFont, null);

        SandboxExecResult listing = await session.ExecAsync("npx", ["remotion", "compositions"], StillTimeoutSeconds, ct);
        IReadOnlyList<CompositionInfo> compositions = listing.TransportError is null
            ? RemotionCompositionTable.Parse($"{listing.Payload.Output}\n{listing.Payload.Error}")
            : [];
        string? claimed = answer?.CompositionId is { } id && CompositionIdPattern.IsMatch(id) ? id : null;
        CompositionInfo? composition =
            compositions.FirstOrDefault(c => string.Equals(c.Id, claimed, StringComparison.Ordinal))
            ?? compositions.FirstOrDefault(c => string.Equals(c.Id, expectedComposition, StringComparison.Ordinal));
        if (composition is null)
        {
            return Done(false, CompositionMissing,
                "The designer finished without a composition the platform could find, so there was nothing to render.", null);
        }

        if (composition.Width > GraphicDesignRules.MaxSide || composition.Height > GraphicDesignRules.MaxSide
            || (long)composition.Width * composition.Height > GraphicDesignRules.MaxPixels)
        {
            return Done(false, GraphicPngVerifier.InvalidOutput,
                $"The graphic's frame is {composition.Width}x{composition.Height} pixels, larger than a graphic may be.", null);
        }

        // 4. The platform renders the still itself: one frame, PNG, transparency kept.
        SandboxExecResult still = await session.ExecAsync(
            "npx",
            [
                "remotion", "still", composition.Id, StillPath, "--frame=0", "--image-format=png",
                "--chromium-executable=" + ReactRemotionSandboxTools.HeadlessShellPath
            ],
            StillTimeoutSeconds,
            ct);
        if (!still.Succeeded)
        {
            string detail = still.TransportError?.Message
                ?? SandboxOutputCompactor.Compact($"{still.Payload.Output}\n{still.Payload.Error}\n{still.RawError}").Text;
            return Done(false, NotRendered, "The graphic could not be rendered: " + Bound(detail, 600), null);
        }

        // 5. Read it back under a byte ceiling and verify it as hostile input.
        await ReportAsync(observer, "Checking the picture", 90, ct);
        byte[]? png;
        try
        {
            png = await ReadBoundedAsync(session, ct);
        }
        catch (SandboxRequestFailedException ex)
        {
            return Done(false, NotRendered, "The rendered graphic could not be read back: " + ex.Message, null);
        }

        if (png is null)
        {
            return Done(false, GraphicPngVerifier.InvalidOutput,
                $"The rendered graphic is larger than {GraphicPngVerifier.MaxBytes / (1024 * 1024)} MiB.", null);
        }

        GraphicPngVerifier.Result verdict = GraphicPngVerifier.Verify(png, composition.Width, composition.Height);
        if (!verdict.Ok)
            return Done(false, verdict.Code, verdict.Message, null);

        // 6. Upload beside the document, under the editor's own per-document prefix.
        await ReportAsync(observer, "Saving the graphic", 95, ct);
        string key = GraphicDesignRules.GraphicKey(request.ProjectId, request.DocumentId, Guid.NewGuid());
        try
        {
            using MemoryStream body = new(verdict.Png!, writable: false);
            PutObjectRequest put = new()
            {
                BucketName = _bucket,
                Key = key,
                InputStream = body,
                ContentType = "image/png",
                AutoCloseStream = false
            };
            put.Metadata["category"] = "agentFiles";
            put.Metadata["reelbolt-kind"] = "image-editor-designGraphic";
            await _s3.PutObjectAsync(put, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Designed graphic of execution {ExecutionId} could not be uploaded", request.ExecutionId);
            return Done(false, UploadFailed, "The graphic was designed but could not be saved. Please try again.", null);
        }

        // 7. The graphic's source becomes a composition workspace version, so "Change this graphic" can
        //    restore it later. Best effort: a graphic whose source could not be kept is still delivered.
        //    A new graphic starts its own lineage (the workspace whose origin is this execution); a
        //    revision continues the base version's. Either way the version records what was asked.
        CompositionCaptureResult captured;
        try
        {
            Guid workspaceId = baseVersion?.WorkspaceId
                ?? (await _workspaces.GetOrCreateOriginWorkspaceAsync(
                    request.ProjectId, request.OrganizationId, request.ExecutionId, null, ct)).Id;
            captured = await _capture.TryCaptureAsync(session, new CompositionCaptureRequest
            {
                ProjectId = request.ProjectId,
                OrganizationId = request.OrganizationId,
                ExecutionId = request.ExecutionId,
                Lineage = new CompositionLineage
                {
                    WorkspaceId = workspaceId,
                    ParentVersionId = baseVersion?.Id,
                    Instruction = Bound(request.Description, 4000),
                    RequestedByUserId = request.RequestedByUserId
                },
                Source = CompositionVersionSource.GraphicDesign,
                CompositionId = composition.Id,
                Width = composition.Width,
                Height = composition.Height,
                DurationInFrames = 1,
                RenderOutputStorageKey = key
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The source of graphic {GraphicId} could not be recorded", request.GraphicId);
            captured = CompositionCaptureResult.Failed("failed", ex.Message);
        }

        if (!captured.Captured || captured.Version is null)
        {
            warnings.Add("The graphic's design could not be saved for later changes, so \"Change this graphic\" will not be available for it.");
        }

        GraphicDesignResult delivered = new(
            key, verdict.Width, verdict.Height, verdict.CropX, verdict.CropY,
            captured.Version?.WorkspaceId, captured.Version?.Id, captured.Version?.VersionNumber,
            composition.Id, NullIfEmpty(answer?.Summary), result.PrimaryModelName);

        try
        {
            await _requests.RecordResultAsync(request.GraphicId, delivered, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Designed graphic {Key} of request {GraphicId} could not be recorded", key, request.GraphicId);
            return Done(false, UploadFailed, "The graphic was designed but could not be recorded. Please try again.", null);
        }

        _logger.LogInformation(
            "Graphic {GraphicId} designed in {ElapsedMs}ms: {Width}x{Height} cropped from {FrameWidth}x{FrameHeight} at ({X},{Y}), source v{Version}",
            request.GraphicId, clock.ElapsedMilliseconds, verdict.Width, verdict.Height, verdict.FrameWidth, verdict.FrameHeight,
            verdict.CropX, verdict.CropY, captured.Version?.VersionNumber);

        return Done(true, null, null, delivered);
    }

    /// <summary>The still's bytes, or null when it is larger than the verifier would accept.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(ISandboxSession session, CancellationToken ct)
    {
        await using SandboxRawFile raw = await session.OpenRawFileAsync(StillPath, StillTimeoutSeconds, ct);
        if (raw.ContentLength is > GraphicPngVerifier.MaxBytes)
            return null;

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await raw.Content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > GraphicPngVerifier.MaxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    internal static GraphicDesignOutput? ParseAnswer(string? output)
    {
        string? json = RobustJsonExtractor.ExtractLastJsonObject(output ?? string.Empty);
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<GraphicDesignOutput>(json, AnswerJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max] + "...";

    private static async Task ReportAsync(IGraphicDesignObserver? observer, string stage, int? percent, CancellationToken ct)
    {
        if (observer is null)
            return;
        try
        {
            await observer.OnStageAsync(stage, percent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Progress is UI signal; it never changes the outcome.
        }
    }

    private static GraphicDesignOutcome Fail(string code, string message, Stopwatch clock) => new()
    {
        Succeeded = false,
        FailureCode = code,
        FailureMessage = message,
        DurationMs = clock.ElapsedMilliseconds
    };
}
