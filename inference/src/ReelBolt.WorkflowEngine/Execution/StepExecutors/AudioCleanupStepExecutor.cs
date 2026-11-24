using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Cleanup;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.AudioCleanup</c> — deterministic, non-LLM: one audio file (or the audio of one video) in, one cleaned file
/// out under <c>outputFiles/{execution}/step-{n}-clean.{ext}</c>, optionally registered as a project file.
/// <para>
/// <b>Local targets</b> (hum, wind, hiss, echo, basic voice) run <see cref="ILocalAudioCleaner"/> on this host: free, no
/// network, no plan feature. <c>Hum</c> with no mains hum found completes with <c>applied=false</c> and the source passed
/// through untouched (never registered, never charged).
/// </para>
/// <para>
/// <b>Hosted targets</b> (<c>IsolateVoice</c>, <c>RemoveMusic</c>) send the source's audio to a hosted isolator, so they
/// refuse, in this order and before anything leaves the host: the plan feature <c>audio_cleanup</c>
/// (<c>PLAN_NOT_ENTITLED</c>), the customer's consent (<c>EGRESS_REFUSED</c>), a resolved provider
/// (<c>NO_AUDIO_ISOLATION_PROVIDER</c>), a source over the request cap (<c>AUDIO_SOURCE_TOO_LONG</c>) and an unpriced model
/// (<c>CONFIG_INVALID</c>). The credit hold is taken by the engine before the step, like every step's, from the price book
/// (<see cref="AudioCleanupCreditPricing"/>); the step then meters the PROBED started minutes
/// (<see cref="UsageKind.AudioCleanupMinutes"/>) only for a result that passed the 50 ms length check, so any earlier
/// failure (a refusal, a vendor failure, a length mismatch) records no usage and its class decides the refund.
/// The temporary upload lives under <c>agentFiles/audio-cleanup/{execution}/</c>, is presigned for 10 minutes and is deleted
/// in a <c>finally</c>. Nothing here logs a URL, a token or a storage key, and none is ever in the step output.
/// </para>
/// <b>It never throws</b> except to propagate a cancellation: every failure is a coded <see cref="StepStatus.Failed"/>.
/// </summary>
public sealed class AudioCleanupStepExecutor : IStepExecutor
{
    /// <summary>The longest a presigned GET for the source may live (the vendor reads it within seconds).</summary>
    public static readonly TimeSpan SourceUrlTtl = TimeSpan.FromMinutes(10);

    /// <summary>The folder under <c>agentFiles/</c> that holds a hosted step's temporary upload.</summary>
    public const string UploadFolder = "audio-cleanup";

    private const string SourceVirtualPath = "audio-cleanup-source";
    private const string ExtractVirtualPath = "audio-cleanup-extract.wav";
    private const string StemVirtualPath = "audio-cleanup-stem";
    private const string CleanVirtualPath = "audio-cleanup-clean.wav";
    private const string RemuxVirtualPath = "audio-cleanup-remux";

    private const int MaxStemBytes = 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static readonly IReadOnlySet<string> AudioOnlyExtensions = StepResultMediaExtensions.Audio;

    /// <summary>Codes that blame the vendor of the resolved row: on an organization's own row they are BYO errors.</summary>
    private static readonly HashSet<string> VendorCodes = new(StringComparer.Ordinal)
    {
        "AUDIO_PROVIDER_CONFIG", "AUDIO_VENDOR_QUOTA", "AUDIO_VENDOR_RETRYABLE", "AUDIO_VENDOR_FAILED",
    };

    private readonly IVideoToolRunner _videoToolRunner;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoEditingOptions _options;
    private readonly ILocalAudioCleaner _localCleaner;
    private readonly IInferenceProviderResolver _providers;
    private readonly IAudioIsolationClientFactory _isolationClients;
    private readonly ILogger<AudioCleanupStepExecutor> _logger;
    private readonly IModelPriceService? _prices;
    private readonly IUsageRecorder? _usageRecorder;
    private readonly IPresignedUrlService? _presignedUrls;
    private readonly IConfiguration? _configuration;
    private readonly IMediaWorkspaceFactory? _mediaWorkspaces;
    private readonly IScratchDiskGuard? _scratchGuard;

    public AudioCleanupStepExecutor(
        IVideoToolRunner videoToolRunner,
        IProjectFileWorkspace workspace,
        IServiceScopeFactory scopeFactory,
        IOptions<VideoEditingOptions> options,
        ILocalAudioCleaner localCleaner,
        IInferenceProviderResolver providers,
        IAudioIsolationClientFactory isolationClients,
        ILogger<AudioCleanupStepExecutor> logger,
        IModelPriceService? prices = null,
        IUsageRecorder? usageRecorder = null,
        IPresignedUrlService? presignedUrls = null,
        IConfiguration? configuration = null,
        IMediaWorkspaceFactory? mediaWorkspaces = null,
        IScratchDiskGuard? scratchGuard = null)
    {
        _videoToolRunner = videoToolRunner;
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _localCleaner = localCleaner;
        _providers = providers;
        _isolationClients = isolationClients;
        _logger = logger;
        _prices = prices;
        _usageRecorder = usageRecorder;
        _presignedUrls = presignedUrls;
        _configuration = configuration;
        _mediaWorkspaces = mediaWorkspaces;
        _scratchGuard = scratchGuard;
    }

    public StepType StepType => StepType.AudioCleanup;

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
            if (string.IsNullOrWhiteSpace(step.AudioCleanupConfigJson))
                return Failure(context, sw, "CONFIG_INVALID", "The AudioCleanup step has no AudioCleanupConfigJson configured.", FailureClass.UserInput);

            AudioCleanupStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<AudioCleanupStepConfig>(step.AudioCleanupConfigJson, AudioCleanupStepConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"AudioCleanupConfigJson is not valid JSON: {ex.Message}", FailureClass.UserInput);
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "AudioCleanupConfigJson deserialized to null.", FailureClass.UserInput);

            // Re-validated at run time as well as at save time: a template, an import or the assistant's apply path skips the save validator.
            List<string> errors = config.Validate();
            if (errors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", errors), FailureClass.UserInput);

            // ---- 2. Hosted refusals that need no I/O, in the documented order ----
            ResolvedAudioGenerationProvider? provider = null;
            AudioIsolationEndpoint? endpoint = null;
            if (config.IsHosted)
            {
                if (await PlanViolationAsync(context.Execution.OrganizationId, ct) is { } violation)
                    return Failure(context, sw, PlanGate.NotEntitledCode, violation, FailureClass.EntitlementDenied);

                if (!config.AllowSourceMediaEgress)
                {
                    return Failure(context, sw, GeneratedShotEgressPolicy.EgressRefusedCode,
                        $"{HostedTargetLabel(config.Target)} sends the source's audio to a hosted AI service, so it needs your consent. "
                        + "allowSourceMediaEgress is false on this step; turn it on to accept that the audio leaves this host. Nothing was uploaded.",
                        FailureClass.UserInput);
                }

                provider = await _providers.ResolveAudioIsolationAsync(config.ProviderId, ct).ConfigureAwait(false);
                if (provider is null)
                {
                    return Failure(context, sw, "NO_AUDIO_ISOLATION_PROVIDER",
                        config.ProviderId is null
                            ? "No audio isolation provider is set as the default. Connect one, or choose a free target (hum, wind, hiss, echo)."
                            : "The audio isolation provider chosen on this step is not available to your organization.",
                        FailureClass.UserInput);
                }

                endpoint = AudioIsolationModelCatalog.Instance.Find(provider.Kind, provider.ModelName);
                if (endpoint is null)
                {
                    return Failure(context, sw, "CONFIG_INVALID",
                        "The audio isolation model of the chosen provider is not one the platform can read or price, so it cannot be used.",
                        FailureClass.UserInput);
                }
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
            // The lease deliberately does not dispose a LOCAL scratch directory: the executor owns that object
            // (MediaWorkspaceLease remarks) and deletes it in the outer finally on every exit, so a customer's
            // source copy, cleaned audio, vendor stem and remuxed video never stay on the worker disk.
            localScratch = lease.Scratch;
            IMediaWorkspace workspace = lease.Workspace;

            using ScratchLeases scratch = new();
            await scratch.ReserveAsync(_scratchGuard, _workspace, context.Execution.ProjectId, sourceKey, ct, workspace is LocalMediaWorkspace);
            await workspace.StageAsync(sourceKey, SourceVirtualPath, ct);
            string inputPath = await workspace.MaterializeAsync(SourceVirtualPath, ct).ConfigureAwait(false);

            MediaProbeResult probed;
            try
            {
                probed = await ProbeAsync(workspace, MediaDerivativeInputFormats.BuildSourceProbeArgs(await workspace.MaterializeAsync(SourceVirtualPath, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "AudioCleanup step {StepOrder}: the source could not be probed", step.StepOrder);
                return Failure(context, sw, CleanupErrorCodes.ProbeFailed, "The source could not be read as audio or video.");
            }

            if (string.IsNullOrWhiteSpace(probed.AudioCodec))
                return Failure(context, sw, "AUDIO_SOURCE_NO_AUDIO", "The source has no audio to clean.", FailureClass.UserInput);
            if (!(probed.DurationSec > 0) || double.IsInfinity(probed.DurationSec))
                return Failure(context, sw, CleanupErrorCodes.ProbeFailed, "The source's length could not be read.");

            bool hasPicture = !string.IsNullOrWhiteSpace(probed.VideoCodec)
                && !AudioOnlyExtensions.Contains(Path.GetExtension(sourceKey));
            bool videoOut = config.OutputKind switch
            {
                AudioCleanupOutputKind.Audio => false,
                AudioCleanupOutputKind.VideoWithCleanAudio => true,
                _ => hasPicture
            };
            if (videoOut && !hasPicture)
            {
                return Failure(context, sw, "AUDIO_OUTPUT_KIND_UNSUPPORTED",
                    "OutputKind=VideoWithCleanAudio needs a source with a picture, and this source is audio only. Use Auto or Audio.",
                    FailureClass.UserInput);
            }

            double durationSec = probed.DurationSec;
            int sampleRate = probed.AudioSampleRate is > 0 ? probed.AudioSampleRate.Value : 48_000;
            int channels = probed.AudioChannels is > 0 ? Math.Min(probed.AudioChannels.Value, 2) : 2;

            // ---- 4. The source cap (local or hosted), still before any cleanup work ----
            // A LOCAL target is free and offline but still bounded: the deployment's 60-minute default, which the step can
            // only lower with maxSourceMinutes. A HOSTED target keeps the 30-minute vendor-request cap.
            int maxMinutes = config.IsHosted ? EffectiveMaxMinutes(config) : EffectiveLocalMaxMinutes(config);
            if (!AudioCleanupCreditPricing.WithinCap(durationSec, maxMinutes))
            {
                return Failure(context, sw, "AUDIO_SOURCE_TOO_LONG",
                    config.IsHosted
                        ? $"The source is {durationSec / 60d:0.#} minutes long, over the {maxMinutes}-minute limit for one hosted cleanup. Trim it, or use a free target."
                        : $"The source is {durationSec / 60d:0.#} minutes long, over the {maxMinutes}-minute limit for one cleanup. Trim the clip first.",
                    FailureClass.UserInput);
            }

            if (config.IsHosted)
            {
                long? priced = _prices is null
                    ? null
                    : await AudioCleanupCreditPricing.TryHoldMillisAsync(durationSec, config.ProviderId, _providers, _prices, maxMinutes, ct).ConfigureAwait(false);
                long? held = _prices is null ? null
                    : await AudioCleanupCreditPricing.TryStepHoldMillisAsync(step.AudioCleanupConfigJson, _providers, _prices, ct).ConfigureAwait(false);
                if (priced is not null && held is { } taken && priced > taken)
                {
                    return Failure(context, sw, "AUDIO_SOURCE_TOO_LONG",
                        "The source costs more than the credits held for this step. Set maxSourceMinutes to cover it, or trim the source. Nothing was uploaded.",
                        FailureClass.UserInput);
                }

                if (priced is null)
                {
                    return Failure(context, sw, "CONFIG_INVALID",
                        "The audio isolation model has no price on this deployment, so it cannot be offered. Nothing was uploaded.",
                        FailureClass.UserInput);
                }
            }

            // ---- 5. Clean ----
            string cleanPath = await workspace.MaterializeAsync(CleanVirtualPath, ct).ConfigureAwait(false);
            JsonArray warnings = [];
            string method;
            bool applied = true;
            string? reason = null;
            long? billedSeconds = null;
            string? vendorRequestId = null;

            if (!config.IsHosted)
            {
                LocalCleanupTarget local = config.ToLocalTarget()!.Value;
                await context.ReportProgressAsync("Cleaning the audio", 30);
                LocalCleanupResult cleaned;
                try
                {
                    cleaned = await _localCleaner.CleanAsync(inputPath, cleanPath, local, ct).ConfigureAwait(false);
                }
                catch (CleanupException ex)
                {
                    _logger.LogWarning("AudioCleanup step {StepOrder}: local cleanup failed with {Code}", step.StepOrder, ex.Code);
                    return Failure(context, sw, ex.Code, ex.Message, FailureClassifier.ForCode(ex.Code));
                }

                method = "local:" + LocalMethodName(local);
                if (!cleaned.Applied)
                {
                    // Nothing to do (no mains hum was found): a successful step that changed nothing. The source is carried on
                    // untouched, never registered as a new file, never charged.
                    applied = false;
                    reason = cleaned.Reason ?? "not_applied";
                    return Completed(context, sw, config, method, applied, reason, durationSec, null, null, warnings, sourceKey, lease, fileName: null, format: null, sizeBytes: null);
                }
            }
            else
            {
                (HostedOutcome? hosted, StepExecutionResult? refusal) = await RunHostedAsync(
                    context, sw, config, provider!, endpoint!, workspace, inputPath, cleanPath, sourceKey,
                    durationSec, sampleRate, channels, key => uploadedKey = key, ct);
                if (refusal is not null)
                    return refusal;

                method = hosted!.Method;
                billedSeconds = (long)Math.Ceiling(hosted.BilledSeconds);
                vendorRequestId = hosted.VendorRequestId;
                if (hosted.Warning is not null)
                    warnings.Add(hosted.Warning);
            }

            // ---- 6. The deliverable: the cleaned audio, or the picture copied with it ----
            string extension = videoOut ? (IsMov(sourceKey) ? "mov" : "mp4") : "wav";
            string contentType = videoOut ? (extension == "mov" ? "video/quicktime" : "video/mp4") : "audio/wav";
            string deliverVirtual = CleanVirtualPath;
            if (videoOut)
            {
                await context.ReportProgressAsync("Putting the cleaned audio back under the picture", 80);
                string remuxPath = await workspace.MaterializeAsync($"{RemuxVirtualPath}.{extension}", ct).ConfigureAwait(false);
                VideoToolResult remux = await workspace.RunFfmpegAsync(
                    AudioCleanupArgv.BuildRemuxArgs(inputPath, cleanPath, remuxPath, extension == "mov"),
                    TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
                if (!remux.Succeeded)
                {
                    _logger.LogWarning("AudioCleanup step {StepOrder}: the remux failed (exitCode={ExitCode})", step.StepOrder, remux.ExitCode);
                    return Failure(context, sw, "AUDIO_CLEANUP_FAILED", "The cleaned audio could not be put back under the picture.");
                }

                deliverVirtual = $"{RemuxVirtualPath}.{extension}";
            }

            MediaWorkspaceStat stat = await workspace.StatAsync(deliverVirtual, ct).ConfigureAwait(false);
            if (!stat.Exists || stat.Bytes <= 0)
                return Failure(context, sw, "AUDIO_CLEANUP_FAILED", "The cleanup wrote no output.");

            // ---- 7. Publish ----
            await context.ReportProgressAsync("Publishing the result", 90);
            string storageFileName = $"step-{step.StepOrder}-clean.{extension}";
            string storageKey = ProjectFilePath.BuildStorageKey(
                context.Execution.ProjectId,
                ThumbnailGenerateStepExecutor.PublishedCategory,
                directoryPath: context.Execution.Id.ToString("D"),
                storageFileName: storageFileName);
            string publishedKey = await workspace.PublishAsync(deliverVirtual, storageKey, contentType, ct).ConfigureAwait(false);

            Guid? projectFileId = null;
            if (config.RegisterProjectFile)
            {
                projectFileId = await RegisterProjectFileAsync(
                    context, publishedKey, RegisteredName(sourceName, config.Target, extension), contentType, stat.Bytes, ct);
            }

            // ---- 8. Meter (hosted): the probed started minutes, only for a delivered result ----
            if (config.IsHosted)
                await RecordUsageAsync(context, provider!, endpoint!, billedSeconds ?? (long)Math.Ceiling(durationSec));

            _logger.LogInformation(
                "AudioCleanup step {StepOrder} cleaned {Duration:0.###}s with {Method} ({Bytes} bytes) on {Target}",
                step.StepOrder, durationSec, method, stat.Bytes, lease.Decision.Target);

            return Completed(context, sw, config, method, applied, reason, durationSec, projectFileId, billedSeconds, warnings,
                publishedKey, lease, storageFileName, extension, stat.Bytes, vendorRequestId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCleanup step {StepOrder} failed unexpectedly", step.StepOrder);
            return Failure(context, sw, "AUDIO_CLEANUP_FAILED", "The audio cleanup failed unexpectedly. The detail is in the server log.");
        }
        finally
        {
            localScratch?.Dispose();
            if (uploadedKey is not null)
                await DeleteTemporaryUploadAsync(context.Execution.ProjectId, uploadedKey, step.StepOrder);
        }
    }

    /// <summary>ffprobe through the workspace with the caller's demuxer-whitelisted argv.</summary>
    private static async Task<MediaProbeResult> ProbeAsync(IMediaWorkspace workspace, string[] argv, CancellationToken ct)
    {
        VideoToolResult r = await workspace.RunFfprobeAsync(argv, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!r.Succeeded)
            throw new InvalidOperationException("ffprobe failed");
        return FfprobeOutputParser.Parse(r.StdOut);
    }

    private sealed record HostedOutcome(string Method, double BilledSeconds, string? VendorRequestId, string? Warning);

    /// <summary>
    /// One hosted isolation: extract the audio, upload it, presign it for ten minutes, call the isolator, conform the
    /// stem to the source's format and exact length (50 ms check) and leave the cleaned WAV at <paramref name="cleanPath"/>.
    /// The key of the temporary upload is handed back through <paramref name="onUploaded"/> the moment it exists, so the
    /// caller's <c>finally</c> deletes it on every exit.
    /// </summary>
    private async Task<(HostedOutcome? Outcome, StepExecutionResult? Failure)> RunHostedAsync(
        StepExecutionContext context, Stopwatch sw, AudioCleanupStepConfig config, ResolvedAudioGenerationProvider provider,
        AudioIsolationEndpoint endpoint, IMediaWorkspace workspace, string inputPath, string cleanPath, string sourceKey,
        double durationSec, int sampleRate, int channels, Action<string> onUploaded, CancellationToken ct)
    {
        WorkflowStep step = context.Step;
        if (_presignedUrls is null)
        {
            return (null, Failure(context, sw, "AUDIO_EGRESS_UNAVAILABLE",
                "This deployment cannot mint a temporary download URL (no presigner is configured, as with local-disk storage), so a hosted model cannot read the audio. Use a free target."));
        }

        await context.ReportProgressAsync("Preparing the audio", 20);
        string extractPath = await workspace.MaterializeAsync(ExtractVirtualPath, ct).ConfigureAwait(false);
        VideoToolResult extracted = await workspace.RunFfmpegAsync(
            AudioCleanupArgv.BuildExtractAudioArgs(inputPath, extractPath, channels), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (!extracted.Succeeded)
        {
            _logger.LogWarning("AudioCleanup step {StepOrder}: audio extraction failed (exitCode={ExitCode})", step.StepOrder, extracted.ExitCode);
            return (null, Failure(context, sw, "AUDIO_CLEANUP_FAILED", "The audio could not be extracted from the source."));
        }

        // The vendor reads this exact object for ten minutes and nothing else of the project.
        string uploadName = $"{UploadFolder}/{context.Execution.Id:D}/source-{step.StepOrder}.wav";
        string key;
        string url;
        try
        {
            await context.ReportProgressAsync("Uploading the audio for cleaning", 30);
            // Through the media session: on a remote session extractPath is session-relative, not a
            // file on this host. A local session uploads from the same resolved path to the same key.
            key = await workspace.UploadArtifactAsync(
                ExtractVirtualPath,
                ProjectFilePath.BuildStorageKey(context.Execution.ProjectId, "agentFiles", directoryPath: null, storageFileName: uploadName),
                "audio/wav", ct).ConfigureAwait(false);
            onUploaded(key);
            url = await _presignedUrls.CreateGetAsync(context.Execution.ProjectId, key, SourceUrlTtl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AudioCleanup step {StepOrder}: the temporary upload failed", step.StepOrder);
            return (null, Failure(context, sw, "AUDIO_CLEANUP_UPLOAD_FAILED", "The audio could not be prepared for the hosted service."));
        }

        AudioIsolationResult result;
        try
        {
            await context.ReportProgressAsync("Cleaning the audio", 45);
            IAudioIsolationClient client = _isolationClients.Get(provider);
            result = await client.IsolateAsync(
                new AudioIsolationRequest(url, durationSec, provider.ModelName), ct).ConfigureAwait(false);
        }
        catch (AudioIsolationException ex)
        {
            // The message is bounded and never carries the key or a URL (see AudioIsolationException).
            _logger.LogWarning("AudioCleanup step {StepOrder}: the isolator answered {Code} (vendor status {Status})",
                step.StepOrder, ex.Code, ex.VendorStatus);
            FailureClass failureClass = FailureClassifier.ForCode(ex.Code);
            if (provider.OrganizationId is not null && VendorCodes.Contains(ex.Code))
                failureClass = FailureClass.ByoProviderError;
            return (null, Failure(context, sw, ex.Code, ex.Message, failureClass));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transport, wrong-origin, JSON and timeout errors from the vendor path can escape the client unwrapped (security
            // verdict, layer-2 condition): their text is never shown or logged. The step answers one fixed sentence and logs
            // only the exception TYPE, so no vendor body or signed URL can reach a tenant or a log line.
            _logger.LogWarning("AudioCleanup step {StepOrder}: the isolator call failed ({ErrorType})", step.StepOrder, ex.GetType().Name);
            return (null, Failure(context, sw, "AUDIO_VENDOR_FAILED", "The hosted audio service could not finish the job.", FailureClass.PlatformProviderError));
        }

        // The vendor's stem, written beside the source and decoded by THIS host: the cleaned file is conformed to the source's own
        // sample rate, channel count and exact length, and the stem's own length must agree within 50 ms or the result is refused.
        byte[] stem = result.VoiceStem;
        if (stem.Length == 0 || stem.Length > MaxStemBytes)
            return (null, Failure(context, sw, "AUDIO_VENDOR_FAILED", "The isolator returned no usable audio.", FailureClass.PlatformProviderError));

        string stemName = StemVirtualPath + SafeExtension(result.Extension);
        await workspace.WriteBytesAsync(stemName, stem, ct).ConfigureAwait(false);
        string stemPath = await workspace.MaterializeAsync(stemName, ct).ConfigureAwait(false);

        MediaProbeResult stemProbe;
        try
        {
            stemProbe = await ProbeAsync(workspace, AudioCleanupArgv.BuildStemProbeArgs(stemPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AudioCleanup step {StepOrder}: the isolator's audio could not be probed", step.StepOrder);
            return (null, Failure(context, sw, "AUDIO_VENDOR_FAILED", "The isolator returned audio that could not be read.", FailureClass.PlatformProviderError));
        }

        if (Math.Abs(stemProbe.DurationSec - durationSec) > LocalAudioCleaner.MaxDurationDeltaSec)
        {
            return (null, Failure(context, sw, CleanupErrorCodes.DurationMismatch,
                $"The cleaned audio is {stemProbe.DurationSec:0.###}s but the source is {durationSec:0.###}s: more than 50 ms apart, so it was not used.",
                FailureClassifier.ForCode(CleanupErrorCodes.DurationMismatch)));
        }

        VideoToolResult conformed = await workspace.RunFfmpegAsync(
            AudioCleanupArgv.BuildConformArgs(stemPath, cleanPath, durationSec, sampleRate, channels), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (!conformed.Succeeded)
        {
            _logger.LogWarning("AudioCleanup step {StepOrder}: conforming the isolator's audio failed (exitCode={ExitCode})", step.StepOrder, conformed.ExitCode);
            return (null, Failure(context, sw, "AUDIO_CLEANUP_FAILED", "The isolator's audio could not be converted."));
        }

        // RemoveMusic: everything but the voice is what the model removes, so the voice stem is the answer; a model that offers a
        // separate background stem returns the music itself, which is the opposite of what this target asks for, so it is not used.
        string stemLabel = config.Target == AudioCleanupTarget.RemoveMusic
            ? "voice-stem (everything but the voice, music included, is removed)"
            : "voice-stem";
        string method = $"fal:{endpoint.Id}:{stemLabel}";

        // The probed length (verified within 50 ms of the stem) is the ONLY metered quantity; the vendor's figure is a warning at most.
        string? warning = Math.Abs(result.BilledSeconds - durationSec) > 1.0 ? "billed_seconds_differ_from_source" : null;
        return (new HostedOutcome(method, durationSec, result.VendorRequestId, warning), null);
    }

    /// <summary>
    /// Records the delivered result's started minutes (keyed per run and step so a replay never charges twice). A failure to
    /// record is logged loudly and never fails a step that already delivered; the engine's settle then charges the hold floor.
    /// </summary>
    private async Task RecordUsageAsync(
        StepExecutionContext context, ResolvedAudioGenerationProvider provider, AudioIsolationEndpoint endpoint, long billedSeconds)
    {
        if (_usageRecorder is null || billedSeconds <= 0)
            return;

        try
        {
            await _usageRecorder.RecordAsync(new UsageRecordRequest(
                endpoint.Unit,
                AudioCleanupCreditPricing.BilledMinutes(billedSeconds),
                provider.ProviderId,
                provider.Kind,
                endpoint.Id,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                IdempotencyKey: $"audiocleanup:{context.Execution.Id:N}:{context.Step.StepOrder}",
                Capability: InferenceProviderCapability.AudioIsolation,
                AccountId: provider.AccountId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metering AudioCleanup step {StepOrder} failed", context.Step.StepOrder);
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
            _logger.LogWarning(ex, "AudioCleanup step {StepOrder}: the temporary upload could not be deleted", stepOrder);
        }
    }

    private int EffectiveMaxMinutes(AudioCleanupStepConfig config) =>
        AudioCleanupCreditPricing.EffectiveMaxMinutes(
            config.MaxSourceMinutes,
            _configuration?.GetValue<int?>(AudioCleanupCreditPricing.MaxSourceMinutesKey),
            AudioCleanupCreditPricing.DefaultMaxSourceMinutes);

    private int EffectiveLocalMaxMinutes(AudioCleanupStepConfig config) =>
        AudioCleanupCreditPricing.EffectiveMaxMinutes(
            config.MaxSourceMinutes,
            _configuration?.GetValue<int?>(AudioCleanupCreditPricing.LocalMaxSourceMinutesKey),
            AudioCleanupCreditPricing.DefaultLocalMaxSourceMinutes);

    /// <summary>The plan's <c>audio_cleanup</c> flag as a sentence, or null when the plan allows it. A lookup that fails refuses: this gate protects money.</summary>
    private async Task<string?> PlanViolationAsync(Guid organizationId, CancellationToken ct)
    {
        if (organizationId == Guid.Empty)
            return null;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(IOrganizationEntitlements)) is not IOrganizationEntitlements entitlements)
                return null;

            OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct).ConfigureAwait(false);
            return PlanGate.CheckAudioCleanup(set)?.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AudioCleanup: the plan entitlement lookup for {OrganizationId} failed", organizationId);
            return "The plan that covers this workspace could not be read, so AI audio cleanup cannot start.";
        }
    }

    /// <summary>Resolves the one audio or video to a storage key and a display name. Every refusal is a coded, user-input failure.</summary>
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
                        : (entry.OutputStorageKey, NameOfKey(entry.OutputStorageKey), null, null);
                }

            case VideoSourceKind.StepOutput:
                {
                    StepOutputHistoryEntry? entry = context.StepOutputHistory.LastOrDefault(h => h.StepOrder == source.StepOrder);
                    return source.StepOrder is null || entry is null || string.IsNullOrWhiteSpace(entry.OutputStorageKey)
                        ? (null, null, $"Step {source.StepOrder} did not produce an output file.", "SOURCE_UNRESOLVED")
                        : (entry.OutputStorageKey, NameOfKey(entry.OutputStorageKey), null, null);
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
                        _scopeFactory, context.Execution.ProjectId, source, allowImages: false, context.CancellationToken, allowAudio: true);
                    return resolved.StorageKey is { } key
                        ? (key, NameOfKey(key), null, null)
                        : (null, null, resolved.Message, resolved.Code);
                }

            default:
                return (null, null, $"Source kind '{source.Kind}' is not supported by AudioCleanup.", "SOURCE_UNRESOLVED");
        }
    }

    private static string NameOfKey(string key) => Path.GetFileNameWithoutExtension(key);

    /// <summary>Pinned to the local target, like ExtractFrame: there is no runner job type for audio cleanup.</summary>
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
                Purpose: $"audio.cleanup:{step.StepOrder}",
                RunnerEligible: false),
            ct).ConfigureAwait(false);
    }

    /// <summary>Best-effort, like ExtractFrame's: the object is published and the output names it, so a failed registration only costs discoverability.</summary>
    private async Task<Guid?> RegisterProjectFileAsync(
        StepExecutionContext context, string storageKey, string fileName, string contentType, long sizeBytes, CancellationToken ct)
    {
        try
        {
            ProjectWorkspaceFile file = await _workspace.RegisterStoredObjectAsync(
                context.Execution.ProjectId, storageKey, fileName, contentType, sizeBytes,
                SummaryStatus.Done, FileIndexingStatus.NotIndexed, ct,
                category: ThumbnailGenerateStepExecutor.PublishedCategory).ConfigureAwait(false);
            return file.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCleanup step {StepOrder}: could not register the result as a project file", context.Step.StepOrder);
            return null;
        }
    }

    private static StepExecutionResult Completed(
        StepExecutionContext context, Stopwatch sw, AudioCleanupStepConfig config, string method, bool applied, string? reason,
        double durationSec, Guid? projectFileId, long? billedSeconds, JsonArray warnings, string outputKey, MediaWorkspaceLease lease,
        string? fileName, string? format, long? sizeBytes, string? vendorRequestId = null)
    {
        // No storage key in the output (the no-storage-key rule): the result is addressed by this step's OutputStorageKey and,
        // when registered, its project file id.
        var output = new JsonObject
        {
            ["status"] = "completed",
            ["target"] = config.Target.ToString(),
            ["method"] = method,
            ["applied"] = applied,
            ["durationSec"] = Math.Round(durationSec, 3),
            ["outputFileId"] = projectFileId?.ToString(),
            ["billedSeconds"] = billedSeconds,
            ["warnings"] = warnings,
            ["meta"] = new JsonObject
            {
                ["applied"] = applied,
                ["reason"] = reason,
                ["method"] = method
            },
            ["fileName"] = fileName,
            ["format"] = format,
            ["sizeBytes"] = sizeBytes,
            ["vendorRequestId"] = vendorRequestId,
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

    private static string HostedTargetLabel(AudioCleanupTarget target) => target == AudioCleanupTarget.RemoveMusic ? "Removing the music" : "Isolating the voice";

    private static string LocalMethodName(LocalCleanupTarget target) => target switch
    {
        LocalCleanupTarget.VoiceBasic => "voice-basic",
        LocalCleanupTarget.Hum => "hum",
        LocalCleanupTarget.Wind => "wind",
        LocalCleanupTarget.Hiss => "hiss",
        _ => "echo"
    };

    private static bool IsMov(string key) =>
        string.Equals(Path.GetExtension(key), ".mov", StringComparison.OrdinalIgnoreCase);

    private static string SafeExtension(string? extension)
    {
        string e = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return e is ".mp3" or ".wav" or ".aac" or ".flac" or ".ogg" or ".m4a" or ".mp4" ? e : ".mp3";
    }

    /// <summary>"&lt;source name&gt; (clean voice).wav" (or the mp4/mov variant): a short, sanitised, human name.</summary>
    internal static string RegisteredName(string? sourceName, AudioCleanupTarget target, string extension)
    {
        StringBuilder clean = new();
        foreach (char c in sourceName ?? string.Empty)
        {
            if (!char.IsControl(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                clean.Append(c);
        }

        string baseName = clean.ToString().Trim();
        if (baseName.Length == 0)
            baseName = "Audio";
        if (baseName.Length > 100)
            baseName = baseName[..100].TrimEnd();

        string suffix = target switch
        {
            AudioCleanupTarget.VoiceBasic or AudioCleanupTarget.IsolateVoice => "clean voice",
            AudioCleanupTarget.RemoveMusic => "no music",
            AudioCleanupTarget.Hum => "hum removed",
            AudioCleanupTarget.Wind => "wind reduced",
            AudioCleanupTarget.Hiss => "hiss reduced",
            _ => "echo reduced"
        };
        return string.Create(CultureInfo.InvariantCulture, $"{baseName} ({suffix}).{extension}");
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
