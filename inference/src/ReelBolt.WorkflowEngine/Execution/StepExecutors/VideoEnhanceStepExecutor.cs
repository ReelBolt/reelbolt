using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.VideoEnhance</c> — deterministic, non-LLM: one video in, one enhanced MP4 of the same length out under
/// <c>outputFiles/{execution}/step-{n}-enhanced.mp4</c>, registered as a project file named
/// <c>"&lt;source name&gt; (&lt;label&gt;).mp4"</c>. The output's audio is ALWAYS the source's own, copied back locally; no
/// vendor ever touches the sound.
/// <para>
/// <b>Local operations</b> (denoise, motion blur, and the engine=Local upscale and interpolate) run
/// <see cref="ILocalVideoEnhancer"/> on this host: free, no network, no plan feature, no credit hold.
/// </para>
/// <para>
/// <b>Hosted operations</b> (AI upscale, AI frame interpolation) send the source file to a hosted model (the whole file as it
/// is; the vendor only needs the picture, but the file is not re-cut), so they refuse in this order and before anything leaves
/// the host: the plan feature <c>video_enhance</c> (<c>PLAN_NOT_ENTITLED</c>), the customer's consent
/// (<c>EGRESS_REFUSED</c>, nothing uploaded), a resolved provider (<c>NO_VIDEO_ENHANCE_PROVIDER</c>), a source over the 120 s
/// request cap (<c>ENHANCE_SOURCE_TOO_LONG</c>, checked from the probe BEFORE upload), then the ledger reservation (the credit
/// hold itself is taken by the engine before the step). Only then is the source uploaded under
/// <c>agentFiles/video-enhance/{execution}/</c>, presigned for 10 minutes and submitted with a ledger row
/// (<c>external_generation_jobs</c>) that holds the vendor's handle, so an attempt resumed after a restart POLLS the existing
/// request and never submits again. The result is conformed and remuxed with the ORIGINAL audio
/// (<see cref="EnhancedAudioRemux"/>), its length is checked, and the step is settled by the billed output seconds (upscale) or
/// the compute-second hold (interpolation). A result that fails the length check is not metered and is refunded by its class.
/// The temporary upload is deleted in a <c>finally</c>. Nothing here logs a URL, a token or a storage key, and none is ever in
/// the step output.
/// </para>
/// <b>It never throws</b> except to propagate a cancellation: every failure is a coded <see cref="StepStatus.Failed"/>.
/// </summary>
public sealed class VideoEnhanceStepExecutor : IStepExecutor
{
    /// <summary>The longest a presigned GET for the source may live (the vendor reads it within seconds).</summary>
    public static readonly TimeSpan SourceUrlTtl = TimeSpan.FromMinutes(10);

    /// <summary>The folder under <c>agentFiles/</c> that holds a hosted step's temporary upload.</summary>
    public const string UploadFolder = "video-enhance";

    /// <summary>The configuration key of the local source cap (the same section <see cref="LocalEnhanceOptions"/> binds).</summary>
    public const string HostedMaxSourceSecondsKey = VideoEnhanceCreditPricing.MaxSourceSecondsKey;

    private const string SourceVirtualPath = "video-enhance-source";
    private const string OutputVirtualPath = "video-enhance-output.mp4";
    private const string CopyVirtualPath = "video-enhance-upload";
    private const string VendorVirtualPath = "video-enhance-vendor";

    private const int MaxVendorBytes = 512 * 1024 * 1024;

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Codes that blame the vendor of the resolved row: on an organization's own row they are BYO errors.</summary>
    private static readonly HashSet<string> VendorCodes = new(StringComparer.Ordinal)
    {
        "ENHANCE_PROVIDER_CONFIG", "ENHANCE_VENDOR_QUOTA", "ENHANCE_VENDOR_RETRYABLE", "ENHANCE_VENDOR_FAILED",
    };

    private readonly IVideoToolRunner _videoToolRunner;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoEditingOptions _options;
    private readonly LocalEnhanceOptions _localOptions;
    private readonly ILocalVideoEnhancer _localEnhancer;
    private readonly IEnhancedAudioRemux _remux;
    private readonly IInferenceProviderResolver _providers;
    private readonly IVideoUpscaleClientFactory _upscaleClients;
    private readonly IFrameInterpolationClientFactory _interpolationClients;
    private readonly IVideoGenerationBudgetGate _budgetGate;
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbContextFactory;
    private readonly ILogger<VideoEnhanceStepExecutor> _logger;
    private readonly IModelPriceService? _prices;
    private readonly IUsageRecorder? _usageRecorder;
    private readonly IPresignedUrlService? _presignedUrls;
    private readonly IConfiguration? _configuration;
    private readonly IMediaWorkspaceFactory? _mediaWorkspaces;
    private readonly IScratchDiskGuard? _scratchGuard;
    private readonly IVideoOutpaintClientFactory? _outpaintClients;
    private readonly IContentModerationGate? _moderation;

    public VideoEnhanceStepExecutor(
        IVideoToolRunner videoToolRunner,
        IProjectFileWorkspace workspace,
        IServiceScopeFactory scopeFactory,
        IOptions<VideoEditingOptions> options,
        IOptions<LocalEnhanceOptions> localOptions,
        ILocalVideoEnhancer localEnhancer,
        IEnhancedAudioRemux remux,
        IInferenceProviderResolver providers,
        IVideoUpscaleClientFactory upscaleClients,
        IFrameInterpolationClientFactory interpolationClients,
        IVideoGenerationBudgetGate budgetGate,
        IDbContextFactory<WorkflowEngineDbContext> dbContextFactory,
        ILogger<VideoEnhanceStepExecutor> logger,
        IModelPriceService? prices = null,
        IUsageRecorder? usageRecorder = null,
        IPresignedUrlService? presignedUrls = null,
        IConfiguration? configuration = null,
        IMediaWorkspaceFactory? mediaWorkspaces = null,
        IScratchDiskGuard? scratchGuard = null,
        IVideoOutpaintClientFactory? outpaintClients = null,
        IContentModerationGate? moderation = null)
    {
        _videoToolRunner = videoToolRunner;
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _localOptions = localOptions.Value;
        _localEnhancer = localEnhancer;
        _remux = remux;
        _providers = providers;
        _upscaleClients = upscaleClients;
        _interpolationClients = interpolationClients;
        _budgetGate = budgetGate;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _prices = prices;
        _usageRecorder = usageRecorder;
        _presignedUrls = presignedUrls;
        _configuration = configuration;
        _mediaWorkspaces = mediaWorkspaces;
        _scratchGuard = scratchGuard;
        _outpaintClients = outpaintClients;
        _moderation = moderation;
    }

    public StepType StepType => StepType.VideoEnhance;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        CancellationToken ct = context.CancellationToken;
        WorkflowStep step = context.Step;
        string? uploadedKey = null;
        VideoScratchSpace? localScratch = null;

        try
        {
            // ---- 1. Config ----
            if (string.IsNullOrWhiteSpace(step.VideoEnhanceConfigJson))
                return Failure(context, sw, "CONFIG_INVALID", "The VideoEnhance step has no VideoEnhanceConfigJson configured.", FailureClass.UserInput);

            VideoEnhanceStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(step.VideoEnhanceConfigJson, VideoEnhanceStepConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"VideoEnhanceConfigJson is not valid JSON: {ex.Message}", FailureClass.UserInput);
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "VideoEnhanceConfigJson deserialized to null.", FailureClass.UserInput);

            // Re-validated at run time as well as at save time: a template, an import or the assistant's apply path skips the save validator.
            List<string> errors = config.Validate();
            if (errors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", errors), FailureClass.UserInput);

            // ---- 2. Refusals that need no I/O, in the documented order ----
            OrganizationEntitlementSet? entitlements = await LoadEntitlementsAsync(context.Execution.OrganizationId, config.IsHosted, ct);
            if (config.IsHosted && entitlements is null && context.Execution.OrganizationId != Guid.Empty)
            {
                return Failure(context, sw, PlanGate.NotEntitledCode,
                    "The plan that covers this workspace could not be read, so AI video enhancement cannot start.", FailureClass.EntitlementDenied);
            }

            ResolvedAudioGenerationProvider? provider = null;
            VideoEnhanceEndpoint? endpoint = null;
            if (config.IsHosted)
            {
                if (entitlements is not null && PlanGate.CheckVideoEnhance(entitlements) is { } violation)
                    return Failure(context, sw, PlanGate.NotEntitledCode, violation.Message, FailureClass.EntitlementDenied);

                if (!config.AllowSourceMediaEgress)
                {
                    return Failure(context, sw, GeneratedShotEgressPolicy.EgressRefusedCode,
                        $"{HostedOpLabel(config.Op)} sends the source video to a hosted AI service, so it needs your consent. "
                        + "allowSourceMediaEgress is false on this step; turn it on to accept that the video file leaves this host. Nothing was uploaded.",
                        FailureClass.UserInput);
                }

                // The step flag alone is not the customer's consent: a hosted run of ANY workflow kind also needs the project's recorded consent,
                // read fresh here (before anything is uploaded), so a withdrawal that landed after the run was queued still stops it.
                if (!await ProjectConsentedAsync(context.Execution.ProjectId, ct))
                {
                    return Failure(context, sw, GeneratedShotEgressPolicy.EgressRefusedCode,
                        $"{HostedOpLabel(config.Op)} sends the source video to a hosted AI service, and this project has not allowed that (or the permission was withdrawn). "
                        + "Allow it in the editor, then run it again. Nothing was uploaded.",
                        FailureClass.UserInput);
                }

                InferenceProviderCapability capability = config.HostedCapability!.Value;
                provider = capability switch
                {
                    InferenceProviderCapability.VideoUpscale => await _providers.ResolveVideoUpscaleAsync(config.ProviderId, ct).ConfigureAwait(false),
                    InferenceProviderCapability.VideoOutpaint => await _providers.ResolveVideoOutpaintAsync(config.ProviderId, ct).ConfigureAwait(false),
                    _ => await _providers.ResolveFrameInterpolationAsync(config.ProviderId, ct).ConfigureAwait(false)
                };
                if (provider is null)
                {
                    return Failure(context, sw, "NO_VIDEO_ENHANCE_PROVIDER",
                        config.ProviderId is null
                            ? "No video enhancement provider is set as the default. Connect one, or use a free local operation."
                            : "The video enhancement provider chosen on this step is not available to your organization.",
                        FailureClass.UserInput);
                }

                endpoint = VideoEnhanceModelCatalog.Instance.Find(capability, provider.ModelName);
                if (endpoint is null || provider.Kind != InferenceProviderKind.FalAi)
                {
                    return Failure(context, sw, "CONFIG_INVALID",
                        "The video enhancement model of the chosen provider is not one the platform can read or price, so it cannot be used.",
                        FailureClass.UserInput);
                }
            }

            // The plan's output height, before any work, for an upscale whose target is known without the source.
            if (config is { Op: VideoEnhanceOp.Upscale, UpscaleFactor: null, TargetHeight: { } wanted }
                && entitlements is not null && PlanGate.CheckResolution(wanted, entitlements) is { } heightViolation)
            {
                return Failure(context, sw, PlanGate.NotEntitledCode, heightViolation.Message, FailureClass.EntitlementDenied);
            }

            // ---- 3. The source ----
            await context.ReportProgressAsync("Resolving the source");
            (string? sourceKey, string? sourceName, string? sourceError, string? sourceCode) = await ResolveSourceAsync(context, config.Source);
            if (sourceKey is null)
                return Failure(context, sw, sourceCode ?? "SOURCE_UNRESOLVED", sourceError ?? "The source could not be resolved.", FailureClass.UserInput);

            MediaWorkspaceOpenResult opened = await OpenMediaWorkspaceAsync(context, step, ct);
            if (opened.Refused)
                return Failure(context, sw, opened.FailureCode!, opened.Message!);

            await using MediaWorkspaceLease lease = opened.Lease!;
            // The lease deliberately does not dispose a LOCAL scratch directory: the executor owns that object and deletes it in
            // the outer finally on every exit, so a customer's source copy, vendor result and remuxed video never stay on disk.
            localScratch = lease.Scratch;
            IMediaWorkspace workspace = lease.Workspace;
            using ScratchLeases scratch = new();
            await scratch.ReserveAsync(_scratchGuard, _workspace, context.Execution.ProjectId, sourceKey, ct, workspace is LocalMediaWorkspace);
            await workspace.StageAsync(sourceKey, SourceVirtualPath, ct);
            string inputPath = await workspace.MaterializeAsync(SourceVirtualPath, ct).ConfigureAwait(false);

            MediaProbeResult probed;
            StreamFacts? stream;
            try
            {
                VideoToolResult probeResult = await workspace.RunFfprobeAsync(
                    MediaDerivativeInputFormats.BuildSourceProbeArgs(inputPath), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                if (!probeResult.Succeeded)
                    throw new InvalidOperationException("ffprobe failed");
                probed = FfprobeOutputParser.Parse(probeResult.StdOut);
                // The video stream's own figures, from the same whitelisted probe: a container may under-report its length.
                stream = EnhanceInputGuards.ParseStream(probeResult.StdOut);
                if (stream is null)
                    throw new InvalidOperationException("the stream figures could not be read");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "VideoEnhance step {StepOrder}: the source could not be probed", step.StepOrder);
                return Failure(context, sw, EnhanceErrorCodes.ProbeFailed, "The source could not be read as a video.");
            }

            if (string.IsNullOrWhiteSpace(probed.VideoCodec) || probed.Width <= 0 || probed.Height <= 0)
                return Failure(context, sw, "ENHANCE_SOURCE_NO_VIDEO", "The source has no picture to enhance.", FailureClass.UserInput);
            if (!(probed.DurationSec > 0) || double.IsInfinity(probed.DurationSec))
                return Failure(context, sw, EnhanceErrorCodes.ProbeFailed, "The source's length could not be read.");

            // The cap, the hints, the hold and the run bounds all use the LARGEST of the container and video-stream length and the stream's
            // own frame count, so a container that under-reports cannot get a longer source through than the one that was checked or priced.
            double checkedSeconds = Math.Max(probed.DurationSec, stream.DurationSec ?? 0);
            probed = probed with { DurationSec = checkedSeconds };
            long streamFrames = stream.FrameCount ?? 0;

            // ---- 4a. A hosted source the route measured: the hold was priced on that size, so a source that is now bigger or longer is not the
            // one that was priced. A distinct, refunded code (never SOURCE_TOO_LONG, which means the user's file is over the limit). ----
            if (config.IsHosted && config.HasSourceHints && SourceDiffersFromHints(config, probed))
            {
                _logger.LogWarning("VideoEnhance step {StepOrder}: the source no longer matches the size the hold was priced on", step.StepOrder);
                return Failure(context, sw, "ENHANCE_SOURCE_CHANGED",
                    "The video is not the one this enhancement was priced for (it is larger, longer or faster now). Nothing was uploaded and nothing was charged; start the enhancement again.",
                    FailureClass.PlatformInternal);
            }

            // ---- 4. The source cap (local or hosted), from the probe, before any enhancement or upload ----
            int maxSeconds = config.IsHosted ? EffectiveHostedMaxSeconds(config) : EffectiveLocalMaxSeconds(config);
            if (probed.DurationSec > maxSeconds)
            {
                return Failure(context, sw, EnhanceErrorCodes.SourceTooLong,
                    config.IsHosted
                        ? $"The source is {probed.DurationSec:0.#} seconds long, over the {maxSeconds}-second limit for one hosted enhancement. Trim it, or use a free local operation."
                        : $"The source is {probed.DurationSec:0.#} seconds long, over the {maxSeconds}-second limit for one enhancement. Trim the clip first.",
                    FailureClass.UserInput);
            }

            SourceFacts facts = SourceFacts.FromProbe(probed);
            facts = facts with { FrameCount = (int)Math.Min(int.MaxValue, Math.Max(facts.FrameCount, streamFrames)) };
            RemuxTarget target = ExpectedTarget(config, facts);

            // The plan's output height for EVERY operation, now that the output size is known (a factor, or a source already above the
            // plan: a denoise, motion blur or interpolation of a 4K source on a 1080 plan writes a 4K file too).
            if (entitlements is not null
                && PlanGate.CheckResolution(Math.Min(target.Width, target.Height), entitlements) is { } outViolation)
            {
                return Failure(context, sw, PlanGate.NotEntitledCode, outViolation.Message, FailureClass.EntitlementDenied);
            }

            string outputPath = await workspace.MaterializeAsync(OutputVirtualPath, ct).ConfigureAwait(false);
            JsonArray warnings = [];
            string method;
            long? billedSeconds = null;
            LocalEnhanceResult produced;

            if (!config.IsHosted)
            {
                // ---- 5a. Local: free ----
                await context.ReportProgressAsync("Enhancing the video", 30);
                try
                {
                    produced = await _localEnhancer.EnhanceAsync(
                        inputPath, outputPath, ToLocalRequest(config.ToLocalRequest()!), progress: null, ct).ConfigureAwait(false);
                }
                catch (EnhanceException ex)
                {
                    _logger.LogWarning("VideoEnhance step {StepOrder}: local enhancement failed with {Code}", step.StepOrder, ex.Code);
                    return Failure(context, sw, ex.Code, ex.Message, FailureClassifier.ForCode(ex.Code));
                }

                method = "local:" + produced.Method;
            }
            else
            {
                // ---- 5b. Hosted ----
                (HostedOutcome? hosted, StepExecutionResult? refusal) = await RunHostedAsync(
                    context, sw, config, provider!, endpoint!, workspace, inputPath, outputPath, sourceKey, probed, facts, target,
                    key => uploadedKey = key, ct);
                if (refusal is not null)
                    return refusal;

                produced = hosted!.Result;
                method = hosted.Method;
                billedSeconds = hosted.BilledSeconds;
                foreach (string warning in hosted.Warnings)
                    warnings.Add(warning);
            }

            foreach (string warning in produced.Warnings)
            {
                if (!warnings.Any(w => w?.GetValue<string>() == warning))
                    warnings.Add(warning);
            }

            MediaWorkspaceStat stat = await workspace.StatAsync(OutputVirtualPath, ct).ConfigureAwait(false);
            if (!stat.Exists || stat.Bytes <= 0)
                return Failure(context, sw, "ENHANCE_FAILED", "The enhancement wrote no output.");

            // ---- 6. Publish and register ----
            await context.ReportProgressAsync("Publishing the result", 90);
            string storageFileName = $"step-{step.StepOrder}-enhanced.mp4";
            string storageKey = ProjectFilePath.BuildStorageKey(
                context.Execution.ProjectId,
                ThumbnailGenerateStepExecutor.PublishedCategory,
                directoryPath: context.Execution.Id.ToString("D"),
                storageFileName: storageFileName);
            string publishedKey = await workspace.PublishAsync(OutputVirtualPath, storageKey, "video/mp4", ct).ConfigureAwait(false);

            Guid? projectFileId = null;
            if (config.RegisterProjectFile)
            {
                (projectFileId, bool overQuota) = await StepOutputRegistration.TryRegisterNamedAsync(
                    _scopeFactory, context.Execution, step, publishedKey, RegisteredName(sourceName, config), stat.Bytes, _logger, ct);
                if (overQuota)
                    warnings.Add("over_storage_quota");
            }

            _logger.LogInformation(
                "VideoEnhance step {StepOrder} produced {Duration:0.###}s with {Method} ({Bytes} bytes) on {Target}",
                step.StepOrder, produced.DurationSec, method, stat.Bytes, lease.Decision.Target);

            return Completed(context, sw, config, method, produced, projectFileId, billedSeconds, warnings, publishedKey, lease, stat.Bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Type only: a storage or timeout exception can carry a URL, a key or a vendor body in its text.
            _logger.LogError("VideoEnhance step {StepOrder} failed unexpectedly ({ErrorType})", step.StepOrder, ex.GetType().Name);
            return Failure(context, sw, "ENHANCE_FAILED", "The video enhancement failed unexpectedly. The detail is in the server log.");
        }
        finally
        {
            localScratch?.Dispose();
            if (uploadedKey is not null)
                await DeleteTemporaryUploadAsync(context.Execution.ProjectId, uploadedKey, step.StepOrder);
        }
    }

    private sealed record HostedOutcome(string Method, LocalEnhanceResult Result, long BilledSeconds, IReadOnlyList<string> Warnings);

    /// <summary>
    /// One hosted enhancement: price it, reserve the ledger row, upload and presign (a first attempt only), submit and poll (or
    /// poll the stored request after a restart), conform the vendor's picture with the source's own audio, check the length,
    /// then meter. The key of the temporary upload is handed back through <paramref name="onUploaded"/> the moment it exists.
    /// </summary>
    private async Task<(HostedOutcome? Outcome, StepExecutionResult? Failure)> RunHostedAsync(
        StepExecutionContext context, Stopwatch sw, VideoEnhanceStepConfig config, ResolvedAudioGenerationProvider provider,
        VideoEnhanceEndpoint endpoint, IMediaWorkspace workspace, string inputPath, string outputPath, string sourceKey,
        MediaProbeResult probed, SourceFacts facts, RemuxTarget target, Action<string> onUploaded, CancellationToken ct)
    {
        WorkflowStep step = context.Step;
        if (_presignedUrls is null)
        {
            return (null, Failure(context, sw, "ENHANCE_EGRESS_UNAVAILABLE",
                "This deployment cannot mint a temporary download URL (no presigner is configured, as with local-disk storage), so a hosted model cannot read the video. Use a free local operation."));
        }

        // ---- price: the real figure must fit the hold the engine took ----
        long sourceFrames = Math.Max(1, Math.Max(facts.FrameCount, (long)Math.Ceiling(probed.DurationSec * facts.Fps)));
        double factor = UpscaleFactorOf(config, facts);
        int outputShortSide = VideoEnhanceCreditPricing.OutputShortSide(facts.Width, facts.Height, factor);

        VideoOutpaintCanvas? canvas = null;
        string? extendPrompt = null;
        if (config.Op == VideoEnhanceOp.Extend)
        {
            // The frame ceiling first, with the cap in seconds: a source over it is the user's file being too long.
            if (!VideoEnhanceCreditPricing.WithinFrameCap(endpoint, sourceFrames))
            {
                double capSeconds = VideoEnhanceCreditPricing.FrameCapSeconds(endpoint, facts.Fps) ?? 0d;
                return (null, Failure(context, sw, EnhanceErrorCodes.SourceTooLong,
                    string.Create(CultureInfo.InvariantCulture,
                        $"The source is {probed.DurationSec:0.#} seconds long, over the {Math.Floor(capSeconds):0}-second limit for extending a video at {facts.Fps:0.##} fps. Trim it first. Nothing was uploaded."),
                    FailureClass.UserInput));
            }

            // The frame floor next: the vendor needs at least this many frames, so a shorter source is refused before upload.
            if (!VideoEnhanceCreditPricing.AtLeastMinFrames(endpoint, sourceFrames))
            {
                int minSeconds = VideoEnhanceCreditPricing.MinFrameSeconds(endpoint, facts.Fps) ?? 1;
                return (null, Failure(context, sw, EnhanceErrorCodes.SourceTooShort,
                    string.Create(CultureInfo.InvariantCulture,
                        $"The source is {probed.DurationSec:0.#} seconds long, under the {minSeconds}-second minimum for extending a video at {facts.Fps:0.##} fps. Use a longer clip. Nothing was uploaded."),
                    FailureClass.UserInput));
            }

            canvas = config.ExtendCanvas(facts.Width, facts.Height);
            bool noMargin = canvas is { Left: 0, Right: 0, Top: 0, Bottom: 0 };
            bool lumaCannot = endpoint.Id == VideoEnhanceModelCatalog.LumaReframeId && !VideoOutpaintGeometry.LumaSupports(config.TargetAspect);
            if (canvas is null || noMargin || lumaCannot)
            {
                return (null, Failure(context, sw, "CONFIG_INVALID",
                    noMargin
                        ? "The picture already has that shape, so there is nothing to extend. Nothing was uploaded."
                        : "That extension is not possible for this video with the chosen model (a side would need to grow by more than half). Nothing was uploaded.",
                    FailureClass.UserInput));
            }

            SanitizedPrompt sanitized = GeneratedPromptSanitizer.Sanitize(
                config.EffectiveExtendPrompt(), VideoEnhanceStepConfig.MaxPromptLength);
            if (sanitized.ExceedsMaxLength)
            {
                return (null, Failure(context, sw, "CONFIG_INVALID",
                    $"The prompt is over {VideoEnhanceStepConfig.MaxPromptLength} characters. Nothing was uploaded.", FailureClass.UserInput));
            }

            extendPrompt = sanitized.IsEmpty ? VideoEnhanceStepConfig.DefaultExtendPrompt : sanitized.Text;

            // Screen a customer-written prompt BEFORE anything is reserved or uploaded, like every generation prompt; the neutral default is ours.
            if (_moderation is not null && !string.IsNullOrWhiteSpace(config.Prompt))
            {
                ModerationAttribution attribution = new(
                    context.Execution.OrganizationId, context.Execution.ProjectId, context.Execution.InitiatedByUserId,
                    context.Execution.Id, context.StepResultId, ModerationSurface.GenerationPrompt, ModerationContentKind.Text);
                ModerationDecision decision = await _moderation.ModerateAsync(
                    new ModerationInput(extendPrompt, [], ModerationContentKind.Text), attribution, ct).ConfigureAwait(false);
                if (decision.Action == ModerationAction.Block)
                    return (null, Failure(context, sw, "CONTENT_BLOCKED", decision.UserMessage));
                if (!decision.Available)
                    _logger.LogWarning("Content moderation was unavailable for the Extend prompt at step {StepOrder}; proceeding because generation fails open", step.StepOrder);
            }
        }

        decimal? usd = config.Op switch
        {
            VideoEnhanceOp.Upscale => VideoEnhanceCreditPricing.EstimateUpscaleUsd(
                endpoint, facts.Width, facts.Height, facts.Fps, probed.DurationSec, factor, EffectiveHostedMaxSeconds(config)),
            VideoEnhanceOp.Extend => VideoEnhanceCreditPricing.EstimateOutpaintUsd(
                endpoint, canvas!.Width, canvas.Height, sourceFrames, probed.DurationSec, EffectiveHostedMaxSeconds(config)),
            _ => VideoEnhanceCreditPricing.EstimateInterpolationUsd(
                endpoint, facts.Width, facts.Height, sourceFrames, config.FrameMultiplier ?? 2, probed.DurationSec,
                ComputeSecondsPerFrame(), EffectiveHostedMaxSeconds(config))
        };
        if (usd is null)
        {
            return (null, Failure(context, sw, "CONFIG_INVALID",
                "The video enhancement model has no price for this source on this deployment, so it cannot be offered. Nothing was uploaded.",
                FailureClass.UserInput));
        }

        if (_prices is not null)
        {
            long? priced = config.Op switch
            {
                VideoEnhanceOp.Upscale => await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(
                    facts.Width, facts.Height, facts.Fps, probed.DurationSec, factor, config.ProviderId, _providers, _prices,
                    EffectiveHostedMaxSeconds(config), ct).ConfigureAwait(false),
                VideoEnhanceOp.Extend => await VideoEnhanceCreditPricing.TryHoldOutpaintMillisAsync(
                    canvas!.Width, canvas.Height, sourceFrames, probed.DurationSec, config.ProviderId, _providers, _prices,
                    EffectiveHostedMaxSeconds(config), ct).ConfigureAwait(false),
                _ => await VideoEnhanceCreditPricing.TryHoldInterpolationMillisAsync(
                    facts.Width, facts.Height, sourceFrames, config.FrameMultiplier ?? 2, probed.DurationSec, config.ProviderId,
                    _providers, _prices, ComputeSecondsPerFrame(), EffectiveHostedMaxSeconds(config), ct).ConfigureAwait(false)
            };
            long? held = await VideoEnhanceStepPricing.TryStepHoldMillisAsync(
                step.VideoEnhanceConfigJson, _providers, _prices, ct, Settings).ConfigureAwait(false);
            if (priced is null)
            {
                return (null, Failure(context, sw, "CONFIG_INVALID",
                    "The video enhancement model has no price on this deployment, so it cannot be offered. Nothing was uploaded.",
                    FailureClass.UserInput));
            }

            if (held is { } taken && priced > taken && config.HasSourceHints)
            {
                // The hold already priced the measured source: a higher price now means the source changed, not that it is too long.
                return (null, Failure(context, sw, "ENHANCE_SOURCE_CHANGED",
                    "The video is not the one this enhancement was priced for. Nothing was uploaded and nothing was charged; start the enhancement again.",
                    FailureClass.PlatformInternal));
            }

            if (held is { } taken2 && priced > taken2)
            {
                return (null, Failure(context, sw, EnhanceErrorCodes.SourceTooLong,
                    "This source costs more than the credits held for this step. Set maxSourceSeconds to cover it, or trim or downscale the source. Nothing was uploaded.",
                    FailureClass.UserInput));
            }
        }

        // ---- the bounded stream copy that is uploaded instead of the original ----
        string uploadExtension = EnhanceBoundedCopy.ExtensionFor(probed);
        string copyVirtual = CopyVirtualPath + uploadExtension;
        string copyPath = await workspace.MaterializeAsync(copyVirtual, ct).ConfigureAwait(false);
        long? checkedFrames = facts.FrameCount > 0 ? facts.FrameCount : null;
        VideoToolResult copied = await workspace.RunFfmpegAsync(
            EnhanceBoundedCopy.BuildArgs(inputPath, copyPath, probed.DurationSec, checkedFrames, uploadExtension == ".mp4"),
            TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (!copied.Succeeded)
        {
            _logger.LogWarning("VideoEnhance step {StepOrder}: the bounded copy failed (exitCode={ExitCode})", step.StepOrder, copied.ExitCode);
            return (null, Failure(context, sw, EnhanceErrorCodes.FfmpegFailed, "The video could not be prepared for the hosted service."));
        }

        double copySeconds;
        try
        {
            VideoToolResult measured = await workspace.RunFfprobeAsync(
                MediaDerivativeInputFormats.BuildSourceProbeArgs(copyPath), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!measured.Succeeded)
                throw new InvalidOperationException("ffprobe failed");
            MediaProbeResult copyProbe = FfprobeOutputParser.Parse(measured.StdOut);
            StreamFacts? copyStream = EnhanceInputGuards.ParseStream(measured.StdOut);
            copySeconds = Math.Max(copyProbe.DurationSec, copyStream?.DurationSec ?? 0);
            // Fail closed: a copy that measures as 0, negative, NaN or unreadable proves nothing about its length, so it is never uploaded.
            if (copyStream is null || !copyStream.HasVideo || !double.IsFinite(copySeconds) || copySeconds <= 0)
                throw new InvalidOperationException("the copy's length could not be measured");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("VideoEnhance step {StepOrder}: the bounded copy could not be measured ({ErrorType})", step.StepOrder, ex.GetType().Name);
            return (null, Failure(context, sw, EnhanceErrorCodes.ProbeFailed, "The video could not be prepared for the hosted service."));
        }

        if (EnhanceBoundedCopy.ExceedsChecked(copySeconds, probed.DurationSec, facts.Fps))
        {
            // The copy is longer than the length that was checked and priced: not the source that was measured. Nothing was uploaded.
            return (null, Failure(context, sw, "ENHANCE_SOURCE_CHANGED",
                "The video is not the one this enhancement was priced for (it is longer than it reported). Nothing was uploaded and nothing was charged; start the enhancement again.",
                FailureClass.PlatformInternal));
        }

        // ---- ledger row (idempotent per execution and request) ----
        string clipId = VideoBudgetAccounting.EnhanceClipId(config.Op.ToString());
        string requestHash = RequestHash(config, sourceKey, endpoint.Id, facts);
        decimal credits = CreditCurrency.CreditsFromUsd(usd.Value);
        VideoGenerationReservation reservation = await _budgetGate.ReserveAsync(
            new VideoGenerationReservationRequest(
                context.Execution.ProjectId, context.Execution.Id, context.StepResultId, provider.ProviderId ?? Guid.Empty, credits,
                [new VideoGenerationTakeRequest(clipId, requestHash, credits)],
                Scope: VideoGenerationBudgetScope.Enhance),
            ct).ConfigureAwait(false);
        if (!reservation.Allowed || reservation.Takes.Count != 1)
            return (null, Denied(context, sw, reservation.DenyReason));

        ReservedTake reserved = reservation.Takes[0];
        await using WorkflowEngineDbContext db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        ExternalGenerationJob job = await db.ExternalGenerationJobs.FirstOrDefaultAsync(j => j.Id == reserved.JobId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Reserved ledger row {reserved.JobId} was not found.");

        VideoEnhanceJobHandle? handle = reserved.Existing ? VideoEnhanceHandleCodec.Decode(job.RemoteJobId) : null;
        if (reserved.Existing && handle is null)
        {
            // An earlier attempt reserved this request and may have submitted it; with no handle there is nothing to poll, and
            // submitting again could pay twice. The row stays exactly as it is.
            _logger.LogWarning("VideoEnhance step {StepOrder}: ledger row {JobId} is in flight with no stored handle", step.StepOrder, job.Id);
            return (null, Failure(context, sw, "RESERVATION_ORPHANED",
                "A prior attempt of this step reserved this enhancement and its outcome is unknown. It was not re-submitted, to avoid double billing; "
                + "check the provider console for a job near that time.", FailureClass.PlatformInternal));
        }

        if (ct.IsCancellationRequested && handle is null)
        {
            await SaveAsync(db, job, ExternalGenerationJobStatus.Cancelled);
            ct.ThrowIfCancellationRequested();
        }

        string model = provider.ModelName;
        VideoEnhanceResult vendor;
        try
        {
            if (handle is null)
            {
                await context.ReportProgressAsync("Uploading the video for enhancement", 25);
                string url;
                try
                {
                    // The bounded stream copy is what leaves the host, never the customer's original.
                    string uploadName = $"{UploadFolder}/{context.Execution.Id:D}/source-{step.StepOrder}{uploadExtension}";
                    string key = await _workspace.UploadArtifactAsync(
                        context.Execution.ProjectId, copyPath, uploadName, EnhanceBoundedCopy.ContentTypeFor(uploadExtension), ct, category: "agentFiles").ConfigureAwait(false);
                    onUploaded(key);
                    url = await _presignedUrls.CreateGetAsync(context.Execution.ProjectId, key, SourceUrlTtl).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("VideoEnhance step {StepOrder}: the temporary upload failed ({ErrorType})", step.StepOrder, ex.GetType().Name);
                    await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
                    return (null, Failure(context, sw, "ENHANCE_UPLOAD_FAILED", "The video could not be prepared for the hosted service."));
                }

                await context.ReportProgressAsync("Enhancing the video", 40);
                handle = await SubmitAsync(config, provider, model, url, probed, facts, sourceFrames, factor, requestHash, extendPrompt, ct).ConfigureAwait(false);
                string? stored = VideoEnhanceHandleCodec.Encode(handle);
                if (stored is null)
                {
                    // The vendor accepted it but the handle cannot be kept: it may bill and cannot be resumed.
                    _logger.LogError("VideoEnhance step {StepOrder}: the vendor's job handle could not be stored on the ledger row {JobId}", step.StepOrder, job.Id);
                    await SaveAsync(db, job, null);
                    return (null, Failure(context, sw, "ENHANCE_VENDOR_FAILED",
                        "The hosted service accepted the job but it could not be tracked; the reservation stays charged and the job is not re-submitted.",
                        FailureClass.PlatformProviderError));
                }

                job.RemoteJobId = stored;
                await SaveAsync(db, job, ExternalGenerationJobStatus.Submitted);
            }
            else
            {
                await context.ReportProgressAsync("Resuming the enhancement", 40);
                _logger.LogInformation("VideoEnhance step {StepOrder}: resuming vendor job on ledger row {JobId}", step.StepOrder, job.Id);
            }

            vendor = config.Op switch
            {
                VideoEnhanceOp.Upscale => await _upscaleClients.Get(provider).ResumeAsync(handle, model, ct).ConfigureAwait(false),
                VideoEnhanceOp.Extend => await OutpaintClients().Get(provider).ResumeAsync(handle, model, ct).ConfigureAwait(false),
                _ => await _interpolationClients.Get(provider).ResumeAsync(handle, model, ct).ConfigureAwait(false)
            };
        }
        catch (VideoEnhanceException ex)
        {
            // The message is bounded and never carries the key or a URL (see VideoEnhanceException).
            _logger.LogWarning("VideoEnhance step {StepOrder}: the vendor answered {Code} (vendor status {Status})", step.StepOrder, ex.Code, ex.VendorStatus);
            bool definite = ex.Kind is VideoEnhanceErrorKind.Configuration or VideoEnhanceErrorKind.VendorQuota
                or VideoEnhanceErrorKind.RequestInvalid or VideoEnhanceErrorKind.Refused
                || (ex.Kind == VideoEnhanceErrorKind.Retryable && ex.VendorStatus == 429);
            // A definite rejection released the reservation; anything else may have been generated and billed, so it stays charged.
            if (definite && job.RemoteJobId is null)
                await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
            else
                await SaveAsync(db, job, null);

            FailureClass failureClass = FailureClassifier.ForCode(ex.Code);
            if (provider.OrganizationId is not null && VendorCodes.Contains(ex.Code))
                failureClass = FailureClass.ByoProviderError;
            string message = provider.OrganizationId is null && ex.Kind is VideoEnhanceErrorKind.Configuration or VideoEnhanceErrorKind.VendorQuota
                ? "The platform's video enhancement provider is temporarily unavailable. Try again later, and contact support if it persists."
                : ex.Message;
            return (null, Failure(context, sw, ex.Code, message, failureClass));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop during the poll: the row stays Submitted so a retry polls it again; the engine's cancel rules decide the charge.
            throw;
        }
        catch (Exception ex)
        {
            // Transport, wrong-origin, JSON and timeout errors escape the client unwrapped (a TaskCanceledException from an HttpClient timeout
            // is one): their text is never shown or logged. One fixed sentence, and only the exception TYPE in the log.
            _logger.LogWarning("VideoEnhance step {StepOrder}: the vendor call failed ({ErrorType})", step.StepOrder, ex.GetType().Name);
            await SaveAsync(db, job, null);
            return (null, Failure(context, sw, "SUBMIT_OUTCOME_UNKNOWN",
                "The hosted enhancement's outcome is unknown (the call timed out or the connection failed); the reservation stays charged.",
                FailureClass.PlatformProviderError));
        }

        // ---- the vendor's picture, conformed with the source's own audio ----
        if (vendor.Video.Length == 0 || vendor.Video.Length > MaxVendorBytes)
        {
            await SaveAsync(db, job, null);
            return (null, Failure(context, sw, "ENHANCE_VENDOR_FAILED", "The hosted service returned no usable video.", FailureClass.PlatformProviderError));
        }

        await context.ReportProgressAsync("Putting the original sound back", 75);
        string vendorName = VendorVirtualPath + SafeVendorExtension(vendor.Extension);
        await workspace.WriteBytesAsync(vendorName, vendor.Video, ct).ConfigureAwait(false);
        string vendorPath = await workspace.MaterializeAsync(vendorName, ct).ConfigureAwait(false);

        LocalEnhanceResult remuxed;
        try
        {
            remuxed = await _remux.RemuxAsync(inputPath, vendorPath, outputPath, target, ct).ConfigureAwait(false);
        }
        catch (EnhanceException ex)
        {
            _logger.LogWarning("VideoEnhance step {StepOrder}: conforming the hosted result failed with {Code}", step.StepOrder, ex.Code);
            // The result was not delivered: nothing is metered, and the code's class refunds the hold. The row stays charged against the ledger.
            await SaveAsync(db, job, null);
            return (null, Failure(context, sw, ex.Code, ex.Message, FailureClassifier.ForCode(ex.Code)));
        }

        // The settle never exceeds what this host delivered: the vendor's own figure is capped at the remuxed output's length, and that at the
        // length the hold was priced for, so the charge cannot exceed the shown estimate.
        double outputSeconds = Math.Min(
            vendor.VendorDurationSec is { } reported && reported > 0 ? reported : remuxed.DurationSec,
            remuxed.DurationSec);
        outputSeconds = Math.Min(outputSeconds, Math.Max(1, EffectiveHostedMaxSeconds(config)));
        long billed = (long)Math.Ceiling(outputSeconds);
        decimal units;
        string? variant = null;
        if (config.Op == VideoEnhanceOp.Upscale)
        {
            units = VideoEnhanceCreditPricing.BilledSeconds(outputSeconds);
            variant = VideoEnhanceCreditPricing.UpscaleTierFor(endpoint, outputShortSide, facts.Fps);
        }
        else if (config.Op == VideoEnhanceOp.Extend)
        {
            // Billed on the clip it returns: the source's frames (the model keeps them), at the model's own seconds rule.
            units = VideoEnhanceCreditPricing.OutpaintBilledSeconds(endpoint, sourceFrames, probed.DurationSec);
            variant = VideoEnhanceCreditPricing.OutpaintRateFor(endpoint, Math.Min(target.Width, target.Height))?.Variant;
            billed = (long)Math.Ceiling(units);
        }
        else
        {
            // No vendor figure exists for compute time: the hold is the settlement.
            units = VideoEnhanceCreditPricing.ComputeSecondsHold(
                VideoEnhanceCreditPricing.OutputFrames(sourceFrames, config.FrameMultiplier ?? 2), facts.Width, facts.Height, ComputeSecondsPerFrame());
            billed = (long)units;
        }

        job.ActualCost = CreditCurrency.CreditsFromUsd(config.Op switch
        {
            VideoEnhanceOp.Upscale => units * (endpoint.UsdPerSecondByTier is { } tiers && variant is not null && tiers.TryGetValue(variant, out decimal perSecond) ? perSecond : 0m),
            VideoEnhanceOp.Extend => units * (VideoEnhanceCreditPricing.OutpaintRateFor(endpoint, Math.Min(target.Width, target.Height))?.UsdPerSecond ?? 0m),
            _ => units * (endpoint.UsdPerComputeSecond ?? 0m)
        });
        job.StorageKey = null;
        await SaveAsync(db, job, ExternalGenerationJobStatus.Succeeded);
        await RecordUsageAsync(context, config, provider, endpoint, units, variant, job);

        List<string> hostedWarnings = [];
        if (vendor.VendorDurationSec is { } vendorDuration && Math.Abs(vendorDuration - remuxed.DurationSec) > 1.0)
            hostedWarnings.Add("billed_seconds_differ_from_output");

        return (new HostedOutcome($"fal:{endpoint.Id}", remuxed, billed, hostedWarnings), null);
    }

    private async Task<VideoEnhanceJobHandle> SubmitAsync(
        VideoEnhanceStepConfig config, ResolvedAudioGenerationProvider provider, string model, string url, MediaProbeResult probed,
        SourceFacts facts, long sourceFrames, double factor, string requestHash, string? extendPrompt, CancellationToken ct)
    {
        string correlation = requestHash[..16];
        if (config.Op == VideoEnhanceOp.Extend)
        {
            return await OutpaintClients().Get(provider).SubmitAsync(
                new VideoOutpaintRequest(
                    url, facts.Width, facts.Height, facts.Fps, probed.DurationSec, sourceFrames,
                    string.IsNullOrWhiteSpace(config.TargetAspect) ? null : config.TargetAspect.Trim(),
                    config.MarginLeft ?? 0, config.MarginRight ?? 0, config.MarginTop ?? 0, config.MarginBottom ?? 0,
                    extendPrompt ?? VideoEnhanceStepConfig.DefaultExtendPrompt, model, correlation), ct)
                .ConfigureAwait(false);
        }

        if (config.Op == VideoEnhanceOp.Upscale)
        {
            return await _upscaleClients.Get(provider).SubmitAsync(
                new VideoUpscaleRequest(url, facts.Width, facts.Height, facts.Fps, probed.DurationSec, sourceFrames, factor, model, correlation), ct)
                .ConfigureAwait(false);
        }

        return await _interpolationClients.Get(provider).SubmitAsync(
            new FrameInterpolationRequest(
                url, facts.Width, facts.Height, facts.Fps, probed.DurationSec, sourceFrames, config.FrameMultiplier ?? 2, model, correlation), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records the delivered result's billed units (keyed on the ledger row, so a replay never charges twice). A failure to record is
    /// logged loudly and never fails a step that already delivered; the engine's settle then charges the hold floor.
    /// </summary>
    private async Task RecordUsageAsync(
        StepExecutionContext context, VideoEnhanceStepConfig config, ResolvedAudioGenerationProvider provider,
        VideoEnhanceEndpoint endpoint, decimal units, string? variant, ExternalGenerationJob job)
    {
        if (_usageRecorder is null || units <= 0m)
            return;

        try
        {
            await _usageRecorder.RecordAsync(new UsageRecordRequest(
                endpoint.Unit,
                units,
                provider.ProviderId,
                provider.Kind,
                endpoint.Id,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                IdempotencyKey: $"videoenhance:{job.Id:N}",
                Capability: config.HostedCapability,
                Variant: variant,
                AccountId: provider.AccountId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metering VideoEnhance step {StepOrder} failed ({ErrorType})", context.Step.StepOrder, ex.GetType().Name);
        }
    }

    private async Task DeleteTemporaryUploadAsync(Guid projectId, string key, int stepOrder)
    {
        try
        {
            await _workspace.DeleteArtifactAsync(projectId, key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The presigned URL has already expired by the time anything can retry this; the object is under a per-run folder.
            _logger.LogWarning("VideoEnhance step {StepOrder}: the temporary upload could not be deleted ({ErrorType})", stepOrder, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Whether the project has a recorded hosted-enhancement consent right now. A lookup that fails refuses: this gate protects the
    /// customer's footage, so "could not read it" is never "allowed".
    /// </summary>
    private async Task<bool> ProjectConsentedAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(WorkflowEngineDbContext)) is not WorkflowEngineDbContext db)
                return false;

            DateTime? at = await db.Projects.AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => p.VideoEnhanceConsentAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return at is not null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("VideoEnhance: the project consent lookup failed ({ErrorType})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>True when the measured source is larger, faster or longer than the hints the route priced the hold on (a small tolerance for rounding).</summary>
    internal static bool SourceDiffersFromHints(VideoEnhanceStepConfig config, MediaProbeResult probed)
    {
        double hintFps = config.SourceFps!.Value;
        double fps = probed.FpsDen > 0 ? probed.FpsNum / (double)probed.FpsDen : hintFps;
        long hintPixels = (long)config.SourceWidth!.Value * config.SourceHeight!.Value;
        long pixels = (long)probed.Width * probed.Height;
        return pixels > hintPixels
            || fps > hintFps + 0.01
            || (config.MaxSourceSeconds is { } bound && probed.DurationSec > bound);
    }

    private VideoEnhanceHostedSettings Settings => VideoEnhanceHostedSettings.From(_configuration);
    private IVideoOutpaintClientFactory OutpaintClients() =>
        _outpaintClients ?? throw new InvalidOperationException("No video outpaint client factory is registered.");

    private int EffectiveHostedMaxSeconds(VideoEnhanceStepConfig config) =>
        config.EffectiveMaxSourceSeconds(Settings.HostedMaxSourceSeconds);

    private int EffectiveLocalMaxSeconds(VideoEnhanceStepConfig config) =>
        config.EffectiveMaxSourceSeconds(_localOptions.MaxSourceSeconds > 0 ? _localOptions.MaxSourceSeconds : VideoEnhanceStepConfig.MaxSourceSecondsLimit);

    private decimal ComputeSecondsPerFrame() => Settings.ComputeSecondsPerOutputFrame;

    /// <summary>The org's plan, or null when it cannot be read (a hosted operation then refuses: this gate protects money).</summary>
    private async Task<OrganizationEntitlementSet?> LoadEntitlementsAsync(Guid organizationId, bool hosted, CancellationToken ct)
    {
        if (organizationId == Guid.Empty)
            return null;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(IOrganizationEntitlements)) is not IOrganizationEntitlements entitlements)
                return null;

            return await entitlements.GetAsync(organizationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("VideoEnhance: the plan entitlement lookup failed ({ErrorType}, hosted={Hosted})", ex.GetType().Name, hosted);
            return null;
        }
    }

    /// <summary>Resolves the one video to a storage key and a display name. Every refusal is a coded, user-input failure.</summary>
    private async Task<(string? Key, string? Name, string? Error, string? Code)> ResolveSourceAsync(
        StepExecutionContext context, VideoSourceRef source)
    {
        switch (source.Kind)
        {
            case VideoSourceKind.PreviousStepOutput:
                {
                    StepOutputHistoryEntry? entry = context.StepOutputHistory.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.OutputStorageKey));
                    return entry is null
                        ? (null, null, "Source=PreviousStepOutput but no prior step in this execution produced an output file.", "SOURCE_UNRESOLVED")
                        : (entry.OutputStorageKey, NameOfKey(entry.OutputStorageKey!), null, null);
                }

            case VideoSourceKind.StepOutput:
                {
                    StepOutputHistoryEntry? entry = context.StepOutputHistory.LastOrDefault(h => h.StepOrder == source.StepOrder);
                    return source.StepOrder is null || entry is null || string.IsNullOrWhiteSpace(entry.OutputStorageKey)
                        ? (null, null, $"Step {source.StepOrder} did not produce an output file.", "SOURCE_UNRESOLVED")
                        : (entry.OutputStorageKey, NameOfKey(entry.OutputStorageKey!), null, null);
                }

            case VideoSourceKind.ProjectFile:
                {
                    if (source.ProjectFileId is null)
                        return (null, null, "Source=ProjectFile requires ProjectFileId.", "SOURCE_UNRESOLVED");

                    // ListFilesAsync never returns a quarantined file (CSAM or moderation), so a held source is "not found".
                    IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.Execution.ProjectId, context.CancellationToken);
                    ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == source.ProjectFileId.Value);
                    return file is null
                        ? (null, null, $"ProjectFile '{source.ProjectFileId.Value}' was not found in this project.", "SOURCE_NOT_FOUND")
                        : (file.StorageKey, Path.GetFileNameWithoutExtension(file.OriginalFileName), null, null);
                }

            case VideoSourceKind.StepResult:
                {
                    StepResultSourceResolution resolved = await StepResultSourceResolver.ResolveAsync(
                        _scopeFactory, context.Execution.ProjectId, source, allowImages: false, context.CancellationToken);
                    return resolved.StorageKey is { } key
                        ? (key, NameOfKey(key), null, null)
                        : (null, null, resolved.Message, resolved.Code);
                }

            default:
                return (null, null, $"Source kind '{source.Kind}' is not supported by VideoEnhance.", "SOURCE_UNRESOLVED");
        }
    }

    private static string NameOfKey(string key) => Path.GetFileNameWithoutExtension(key);

    /// <summary>Pinned to the local target, like ExtractFrame: the local engines and the remux run on this host.</summary>
    private async Task<MediaWorkspaceOpenResult> OpenMediaWorkspaceAsync(StepExecutionContext context, WorkflowStep step, CancellationToken ct)
    {
        if (_mediaWorkspaces is null)
        {
            VideoScratchSpace localScratch = VideoScratchSpace.Create(_options, context.Execution.Id, step.Id, _logger);
            LocalMediaWorkspace local = LocalMediaWorkspace.Create(localScratch, _videoToolRunner, _workspace, context.Execution.ProjectId, _logger);
            return MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                local, localScratch, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, context.Execution.Id, step.StepOrder, _logger));
        }

        return await _mediaWorkspaces.OpenAsync(
            new MediaWorkspaceRequest(
                ExecutionId: context.Execution.Id,
                OrganizationId: context.Execution.OrganizationId,
                ProjectId: context.Execution.ProjectId,
                StepId: step.Id,
                StepOrder: step.StepOrder,
                JobType: RunnerJobType.VideoCompile,
                Purpose: $"video.enhance:{step.StepOrder}",
                RunnerEligible: false),
            ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ geometry and names

    private static int EvenAtLeast2(double value) => Math.Max(2, (int)Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2);

    /// <summary>The factor a hosted upscale sends: the configured one, or the target height over the source's short side.</summary>
    internal static double UpscaleFactorOf(VideoEnhanceStepConfig config, SourceFacts facts) =>
        config.UpscaleFactor is { } factor ? factor
            : config.TargetHeight is { } height && facts.ShortSide > 0 ? (double)height / facts.ShortSide
            : 1d;

    /// <summary>The geometry a hosted result is conformed to, from the request and the probe (never from the vendor).</summary>
    internal static RemuxTarget ExpectedTarget(VideoEnhanceStepConfig config, SourceFacts facts)
    {
        switch (config.Op)
        {
            case VideoEnhanceOp.Upscale:
                {
                    double factor = UpscaleFactorOf(config, facts);
                    return new RemuxTarget(EvenAtLeast2(facts.Width * factor), EvenAtLeast2(facts.Height * factor), facts.FpsNum, facts.FpsDen);
                }

            case VideoEnhanceOp.Interpolate:
                {
                    int multiplier = config.FrameMultiplier ?? 2;
                    return new RemuxTarget(facts.Width, facts.Height, facts.FpsNum * multiplier, facts.FpsDen);
                }

            case VideoEnhanceOp.Extend:
                {
                    VideoOutpaintCanvas? canvas = config.ExtendCanvas(facts.Width, facts.Height);
                    return canvas is null
                        ? new RemuxTarget(facts.Width, facts.Height, facts.FpsNum, facts.FpsDen)
                        : new RemuxTarget(canvas.Width, canvas.Height, facts.FpsNum, facts.FpsDen);
                }

            default:
                return new RemuxTarget(facts.Width, facts.Height, facts.FpsNum, facts.FpsDen);
        }
    }

    private static LocalEnhanceRequest ToLocalRequest(VideoEnhanceLocalRequest request) => new()
    {
        Op = request.Op,
        UpscaleFactor = request.UpscaleFactor,
        TargetHeight = request.TargetHeight,
        FrameMultiplier = request.FrameMultiplier,
        DenoiseStrength = request.DenoiseStrength switch
        {
            VideoEnhanceDenoiseStrength.Light => DenoiseStrength.Light,
            VideoEnhanceDenoiseStrength.Strong => DenoiseStrength.Strong,
            _ => null
        },
        KeepGrain = request.KeepGrain,
        ShutterAngle = request.ShutterAngle
    };

    /// <summary>Hash of everything that makes two requests the same job: the operation, its parameters, the source and the model.</summary>
    private static string RequestHash(VideoEnhanceStepConfig config, string sourceKey, string endpointId, SourceFacts facts)
    {
        string canonical = string.Create(CultureInfo.InvariantCulture,
            $"{config.Op}|{config.UpscaleFactor}|{config.TargetHeight}|{config.FrameMultiplier}|{config.TargetAspect}|{config.MarginLeft}|{config.MarginRight}|{config.MarginTop}|{config.MarginBottom}|{config.EffectiveExtendPrompt()}|{sourceKey}|{endpointId}|{facts.Width}x{facts.Height}@{facts.FpsNum}/{facts.FpsDen}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SafeVendorExtension(string? extension)
    {
        string e = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return e is ".mp4" or ".mov" or ".webm" or ".mkv" ? e : ".mp4";
    }

    private static string HostedOpLabel(VideoEnhanceOp op) => op switch
    {
        VideoEnhanceOp.Interpolate => "AI smooth slow motion",
        VideoEnhanceOp.Extend => "AI extending the picture",
        _ => "AI upscaling"
    };

    /// <summary>The plain-English label each operation and setting puts in the registered file's name, kept in this one table.</summary>
    internal static string LabelFor(VideoEnhanceStepConfig config) => config.Op switch
    {
        VideoEnhanceOp.Upscale when config.UpscaleFactor is { } factor => string.Create(CultureInfo.InvariantCulture, $"sharper {factor}x"),
        VideoEnhanceOp.Upscale => config.TargetHeight switch
        {
            2160 => "4K",
            1440 => "1440p",
            1080 => "1080p",
            _ => "720p"
        },
        VideoEnhanceOp.Interpolate => string.Create(CultureInfo.InvariantCulture, $"smooth {config.FrameMultiplier ?? 2}x"),
        VideoEnhanceOp.Denoise => "clean",
        VideoEnhanceOp.Extend => string.IsNullOrWhiteSpace(config.TargetAspect) ? "extended" : "extended " + config.TargetAspect!.Trim().Replace(':', '-'),
        _ => "motion blur"
    };

    /// <summary>"&lt;source name&gt; (&lt;label&gt;).mp4": a short, sanitised, human name.</summary>
    internal static string RegisteredName(string? sourceName, VideoEnhanceStepConfig config)
    {
        StringBuilder clean = new();
        foreach (char c in sourceName ?? string.Empty)
        {
            if (!char.IsControl(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                clean.Append(c);
        }

        string baseName = clean.ToString().Trim();
        if (baseName.Length == 0)
            baseName = "Video";
        if (baseName.Length > 100)
            baseName = baseName[..100].TrimEnd();

        return string.Create(CultureInfo.InvariantCulture, $"{baseName} ({LabelFor(config)}).mp4");
    }

    // ------------------------------------------------------------------ results

    private static StepExecutionResult Completed(
        StepExecutionContext context, Stopwatch sw, VideoEnhanceStepConfig config, string method, LocalEnhanceResult produced,
        Guid? projectFileId, long? billedSeconds, JsonArray warnings, string outputKey, MediaWorkspaceLease lease, long sizeBytes)
    {
        // No storage key in the output (the no-storage-key rule): the result is addressed by this step's OutputStorageKey and,
        // when registered, its project file id.
        var output = new JsonObject
        {
            ["status"] = "completed",
            ["op"] = config.Op.ToString(),
            ["engine"] = config.Engine.ToString(),
            ["method"] = method,
            ["durationSec"] = Math.Round(produced.DurationSec, 3),
            ["outputFileId"] = projectFileId?.ToString(),
            ["outputWidth"] = produced.OutWidth,
            ["outputHeight"] = produced.OutHeight,
            ["outputFps"] = Math.Round(produced.OutFps, 3),
            ["sizeBytes"] = sizeBytes,
            ["billedSeconds"] = billedSeconds,
            ["warnings"] = warnings,
            ["computeTarget"] = lease.Decision.Target.ToString()
        };

        return new StepExecutionResult
        {
            Output = output.ToJsonString(EnvelopeJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Completed,
            DurationMs = (int)sw.ElapsedMilliseconds,
            OutputStorageKey = outputKey,
            ComputeTarget = lease.Decision.Target
        };
    }

    private static StepExecutionResult Denied(StepExecutionContext context, Stopwatch sw, string? denyReason)
    {
        string reason = denyReason ?? VideoBudgetMessages.Code + ":";
        string code = reason.StartsWith("CONFIG_INVALID:", StringComparison.Ordinal) ? "CONFIG_INVALID"
            : reason.StartsWith("PLAN_NOT_ENTITLED:", StringComparison.Ordinal) ? "PLAN_NOT_ENTITLED"
            : VideoBudgetMessages.Code;
        string text = reason.StartsWith(code + ":", StringComparison.Ordinal) ? reason[(code.Length + 1)..].Trim() : reason.Trim();
        if (text.Length == 0)
            text = code == VideoBudgetMessages.Code ? VideoBudgetMessages.GenericSentence : $"Video enhancement was refused ({code}).";
        return Failure(context, sw, code, text, code == "PLAN_NOT_ENTITLED" ? FailureClass.EntitlementDenied : FailureClassifier.ForCode(code));
    }

    private static async Task SaveAsync(WorkflowEngineDbContext db, ExternalGenerationJob job, ExternalGenerationJobStatus? status)
    {
        if (status is { } s)
            job.Status = s;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static StepExecutionResult Failure(
        StepExecutionContext context, Stopwatch sw, string code, string message, FailureClass failureClass = FailureClass.PlatformInternal)
    {
        var output = new JsonObject { ["status"] = "failed", ["errorCode"] = code, ["message"] = message };
        return new StepExecutionResult
        {
            Output = output.ToJsonString(EnvelopeJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Failed,
            ErrorDetails = $"{code}: {message}",
            DurationMs = (int)sw.ElapsedMilliseconds,
            FailureClass = failureClass,
            FailureCode = code
        };
    }
}
