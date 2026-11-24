using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.ThumbnailGenerate</c> — deterministic, non-LLM. Chooses one moment of a project's own
/// video and turns it into a cover image.
/// <para>
/// <b>It calls no model, hosts nothing and needs no GPU.</b> This is work package I1 of
/// <c>docs/thumbnails-and-image-editing.md</c>, and the point of starting here is that every hard
/// part already exists: <c>VideoAnalyze</c> has decoded the source, split it into shots and measured
/// take quality, sharpness, brightness, motion, stillness windows and near-duplicate retakes;
/// <see cref="IMediaWorkspace"/> is the seam that runs ffmpeg wherever the compute decision says;
/// <c>PublishAsync</c> is the verified upload; and <c>media_derivatives</c> is a table that was
/// designed for exactly this row and has had <b>no writer at all</b> until now. What this executor
/// adds is the selection rule (<see cref="ThumbnailCandidateSelector"/>), the frame extraction, the
/// optional text band, and the writer.
/// </para>
/// <para>
/// <b>It never throws.</b> Same contract as <c>VideoCompileStepExecutor</c>/<c>VideoGenerateStepExecutor</c>:
/// every failure path returns a <see cref="StepExecutionResult"/> with a coded
/// <see cref="StepStatus.Failed"/> and valid JSON in <see cref="StepExecutionResult.Output"/>,
/// because <c>workflow_step_results.output_json</c> is <c>jsonb</c> and a thrown exception would be
/// classified — and refunded — as a platform fault rather than the user-input or internal failure it
/// actually is.
/// </para>
/// </summary>
public sealed class ThumbnailGenerateStepExecutor : IStepExecutor, IChangeRequestAwareStepExecutor
{
    /// <summary>Config-value enums are persisted by NAME, exactly as every other step config does.</summary>
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly Dictionary<ThumbnailOverlayPosition, double> BandCenters = new()
    {
        [ThumbnailOverlayPosition.Center] = 0,
        [ThumbnailOverlayPosition.LowerThird] = 0.78,
        [ThumbnailOverlayPosition.UpperThird] = 0.18
    };

    /// <summary>Name of the session file the overlay TEXT is written to — never interpolated into a filter string.</summary>
    private const string OverlaySlotVirtualPath = "thumbnail-overlay.txt";

    /// <summary>Name of the freshly extracted frame before the text band is composed onto it.</summary>
    private const string RawFrameVirtualPath = "thumbnail-raw.jpg";

    /// <summary>Stage-1 output when a text overlay follows: the fitted frame, before the text is drawn.</summary>
    private const string ExtractedFrameVirtualPath = "thumbnail-frame.jpg";

    /// <summary>
    /// Object-store and <c>project_files</c> category a thumbnail lands under.
    /// <para>
    /// <b>Deliberately <c>outputFiles</c>, and this is the retention answer.</b> The plan flagged
    /// that <c>StorageRetentionService</c> records <c>media_derivatives</c> as "not swept" because it
    /// had no writer, so a writer would leave orphaned objects with nothing naming them. That is true
    /// of the derivative ROW, and it is already solved by the schema: <c>media_derivatives</c>
    /// cascades on <c>ProjectId</c> AND on <c>SourceProjectFileId</c>, so the row disappears with the
    /// project or with the project file it was made from. What the sweep must still reach is the
    /// OBJECT, and it reaches exactly the categories it already walks — <c>outputFiles</c> and
    /// <c>agentFiles</c>. Publishing a thumbnail under a brand-new category would put its object
    /// outside the only sweep that exists. Under this one, the daily job marks it, drops the
    /// <c>project_files</c> reference, and the derivative row cascades away with it.
    /// </para>
    /// </summary>
    public const string PublishedCategory = "outputFiles";

    /// <summary>Largest JPEG this executor will accept back from the encoder — a thumbnail is kilobytes, so this is a runaway guard, not a budget.</summary>
    private const int MaxThumbnailBytes = 32 * 1024 * 1024;

    private readonly IVideoToolRunner _videoToolRunner;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IScratchDiskGuard? _scratchGuard;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<ThumbnailGenerateStepExecutor> _logger;

    /// <summary>
    /// D11's factory. <b>Optional on purpose</b>, exactly as on <c>VideoCompileStepExecutor</c>: every
    /// test that builds this executor directly passes its established collaborators and nothing else,
    /// and a null factory reproduces precisely the local workspace this code would otherwise build
    /// inline. The real composition always supplies it.
    /// </summary>
    private readonly IMediaWorkspaceFactory? _mediaWorkspaces;

    /// <summary>
    /// T-006: optional so every pre-moderation construction site keeps compiling. The sanitized
    /// overlay text is screened once before the overlay ffmpeg call; a Block fails the step.
    /// </summary>
    private readonly IContentModerationGate? _moderation;
    private readonly int _moderationMaxChars;

    /// <summary>Optional: an ordinary run (and every older test) never has a change request.</summary>
    private readonly IStepChangeRequestContext? _changeRequests;

    public ThumbnailGenerateStepExecutor(
        IVideoToolRunner videoToolRunner,
        IProjectFileWorkspace workspace,
        IServiceScopeFactory scopeFactory,
        IOptions<VideoEditingOptions> options,
        ILogger<ThumbnailGenerateStepExecutor> logger,
        IMediaWorkspaceFactory? mediaWorkspaces = null,
        IContentModerationGate? moderation = null,
        IOptions<ContentModerationOptions>? moderationOptions = null,
        IStepChangeRequestContext? changeRequests = null,
        IScratchDiskGuard? scratchGuard = null)
    {
        _scratchGuard = scratchGuard;
        _changeRequests = changeRequests;
        _videoToolRunner = videoToolRunner;
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _mediaWorkspaces = mediaWorkspaces;
        _moderation = moderation;
        _moderationMaxChars = Math.Max(1, moderationOptions?.Value.MaxTextChars ?? 8000);
    }

    public StepType StepType => StepType.ThumbnailGenerate;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        CancellationToken ct = context.CancellationToken;
        WorkflowStep step = context.Step;

        try
        {
            // ---- 1. Config ----
            if (string.IsNullOrWhiteSpace(step.ThumbnailGenerateConfigJson))
            {
                return Failure(context, sw, "CONFIG_INVALID",
                    "The ThumbnailGenerate step has no ThumbnailGenerateConfigJson configured.",
                    FailureClass.UserInput);
            }

            ThumbnailGenerateStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<ThumbnailGenerateStepConfig>(
                    step.ThumbnailGenerateConfigJson, ConfigJsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID",
                    $"ThumbnailGenerateConfigJson is not valid JSON: {ex.Message}",
                    FailureClass.UserInput);
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "ThumbnailGenerateConfigJson deserialized to null.",
                    FailureClass.UserInput);

            // ---- 1b. Step change request (docs/design/change-requests.md): whole step, overrides applied to the config ----
            StepChangeRequestScope? changeScope = _changeRequests is null
                ? null
                : await _changeRequests.FindAsync(context.Execution, step, ct);
            List<string> changedKeys = [];
            if (changeScope is { Malformed: true })
                return Failure(context, sw, "CHANGE_NOT_APPLIED", StepChangeRequestScope.MalformedRefusal, FailureClass.UserInput);
            if (changeScope is not null)
            {
                (ThumbnailGenerateStepConfig Config, List<string> Changed, string? Refusal) applied = ApplyChange(config, changeScope.Payload);
                if (applied.Refusal is not null)
                    return Failure(context, sw, "CHANGE_NOT_APPLIED", applied.Refusal, FailureClass.UserInput);
                config = applied.Config;
                changedKeys = applied.Changed;
                List<string> invalid = ThumbnailGenerateStepConfig.Validate(config);
                if (invalid.Count > 0)
                    return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", invalid), FailureClass.UserInput);
            }

            // The executor CLAMPS rather than rejects (a config can arrive from a template, an import
            // or the assistant's apply path, none of which pass through the save-time validator), but
            // the two values that reach ffmpeg's filter STRING are allowlisted, not clamped.
            bool cover = config.Fit == ThumbnailFit.Cover;
            int width = Math.Clamp(config.Width, 16, 4096);
            int height = Math.Clamp(config.Height, 16, 4096);
            int quality = Math.Clamp(config.JpegQuality, 2, 31);
            int overlayFontSizePct = Math.Clamp(config.OverlayFontSizePct, 2, 25);
            int maxOverlayChars = Math.Clamp(config.MaxOverlayChars, 1, 400);
            int maxCandidates = Math.Clamp(config.MaxCandidates, 1, 500);
            double minShotSeconds = Math.Max(0, config.MinShotSeconds);

            string outputFileName = SanitizeFileName(config.OutputFileName, "thumbnail.jpg");

            string? overlayText = null;
            if (config.OverlayPosition != ThumbnailOverlayPosition.None &&
                !string.IsNullOrWhiteSpace(config.OverlayText))
            {
                // Qualified: the engine's Services.Video namespace also holds a thin forwarding shim
                // with this name, so the bare identifier is ambiguous here.
                overlayText = ReelBolt.Shared.Editing.OverlayTextSanitizer.Sanitize(config.OverlayText, maxOverlayChars);
                if (overlayText.Length == 0)
                {
                    _logger.LogInformation(
                        "ThumbnailGenerate step {StepOrder}: the configured overlay text sanitized to nothing; drawing no text",
                        step.StepOrder);
                }
            }

            if (config.OverlayPosition != ThumbnailOverlayPosition.None)
            {
                if (!ThumbnailGenerateStepConfig.IsAllowedFontColor(config.OverlayFontColor))
                {
                    return Failure(context, sw, "COLOR_NOT_ALLOWED",
                        $"OverlayFontColor '{config.OverlayFontColor}' is not in the allowlist (white/black/yellow/#RRGGBB).",
                        FailureClass.UserInput);
                }

                if (!ThumbnailGenerateStepConfig.IsAllowedBoxColor(config.OverlayBoxColor))
                {
                    return Failure(context, sw, "COLOR_NOT_ALLOWED",
                        $"OverlayBoxColor '{config.OverlayBoxColor}' is not in the allowlist (black@0.45/black@0.6/white@0.4/none).",
                        FailureClass.UserInput);
                }
            }

            // Every field the ffmpeg argv uses is checked here and named, so a bad value is a precise
            // CONFIG_INVALID rather than an ffmpeg failure. (The colours were checked above under their own code.)
            List<string> configErrors = ThumbnailGenerateStepConfig.Validate(config);
            if (configErrors.Count > 0)
            {
                return Failure(context, sw, "CONFIG_INVALID",
                    "The ThumbnailGenerate config is invalid: " + string.Join(" ", configErrors),
                    FailureClass.UserInput);
            }

            // ---- 1b. Content moderation (T-006): the sanitized overlay text is screened before
            // any analysis read or ffmpeg call. Review and Unavailable proceed (fails open); only
            // a Block fails the step.
            if (_moderation is not null && !string.IsNullOrWhiteSpace(overlayText))
            {
                if (!ModerationTextChunker.TryChunk(overlayText, _moderationMaxChars, out IReadOnlyList<string> chunks))
                {
                    return Failure(context, sw, "INPUT_TOO_LARGE",
                        "The thumbnail text is too long to check without leaving part of it unclassified.",
                        FailureClass.UserInput);
                }

                foreach (string chunk in chunks)
                {
                    string? blockedOverlay = await ScreenGeneratedTextAsync(context, chunk, ct);
                    if (blockedOverlay is not null)
                    {
                        return Failure(context, sw, "CONTENT_BLOCKED", blockedOverlay, FailureClass.UserInput);
                    }
                }
            }

            // ---- 2. The analysis artifact (not needed for AtTime on an explicit Source) ----
            bool explicitAtTime = config.Source is not null && config.Selection == ThumbnailSelection.AtTime;
            string? artifactKey = null;
            if (!explicitAtTime)
            {
                await context.ReportProgressAsync("Resolving analysis artifact");
                (artifactKey, string? artifactError) = await ResolveAnalysisArtifactKeyAsync(context, config, ct);
                if (artifactKey is null)
                    return Failure(context, sw, "ANALYSIS_NOT_FOUND", artifactError ?? "Could not resolve the VideoAnalyze artifact.");
            }

            // ---- 3. Workspace ----
            MediaWorkspaceOpenResult opened = await OpenMediaWorkspaceAsync(context, step, ct);
            if (opened.Refused)
                return Failure(context, sw, opened.FailureCode!, opened.Message!);

            await using MediaWorkspaceLease lease = opened.Lease!;
            IMediaWorkspace workspace = lease.Workspace;

            VideoAnalysisArtifact? artifact = null;
            if (artifactKey is not null)
            {
                const string AnalysisVirtualPath = "analysis.json";
                if (!await AnalysisArtifactStaging.TryStageAsync(workspace, artifactKey, AnalysisVirtualPath, _logger, ct))
                    return Failure(context, sw, AnalysisArtifactStaging.UnavailableCode, AnalysisArtifactStaging.ReadMessage);

                try
                {
                    byte[] artifactBytes = await workspace.ReadAsync(AnalysisVirtualPath, ct).ConfigureAwait(false);
                    artifact = JsonSerializer.Deserialize<VideoAnalysisArtifact>(
                        TrimBom(System.Text.Encoding.UTF8.GetString(artifactBytes)), ArtifactJsonOptions);
                }
                catch (JsonException ex)
                {
                    return Failure(context, sw, "ANALYSIS_INVALID", $"Analysis artifact is not valid JSON: {ex.Message}");
                }

                if (artifact is null)
                    return Failure(context, sw, "ANALYSIS_INVALID", "Analysis artifact deserialized to null.");
            }

            // ---- 3b. An explicit Source (a file, a step output, an assembled film) ----
            string? explicitSourceKey = null;
            if (config.Source is { } explicitSource)
            {
                (explicitSourceKey, string? sourceError, string? sourceCode) = await ResolveExplicitSourceKeyAsync(context, explicitSource, ct);
                if (explicitSourceKey is null)
                    return Failure(context, sw, sourceCode ?? "SOURCE_UNRESOLVED", sourceError ?? "The Source could not be resolved.", FailureClass.UserInput);
            }

            // ---- 4. Which moment ----
            await context.ReportProgressAsync("Selecting the thumbnail moment");

            int? onlySourceIndex = null;
            if (explicitSourceKey is not null && artifact is not null)
            {
                // Auto on a given file ranks the shots of THAT analysed source only (not per clip, not the whole project).
                VideoAnalysisSourceInfo? analysed = (artifact.Sources ?? []).FirstOrDefault(
                    s => string.Equals(s.StorageKey, explicitSourceKey, StringComparison.Ordinal));
                if (analysed is null)
                {
                    return Failure(context, sw, "SOURCE_NOT_ANALYSED",
                        "The Source file is not one of the sources the VideoAnalyze step analysed, so Auto has no shots to rank for it. Use Selection=AtTime, or point Source at an analysed file.",
                        FailureClass.UserInput);
                }

                onlySourceIndex = analysed.SourceIndex;
            }

            IReadOnlyList<ThumbnailCandidate> candidates = config.Selection == ThumbnailSelection.Auto
                ? ThumbnailCandidateSelector.Rank(artifact!, maxCandidates, minShotSeconds, config.PreferBestTake, onlySourceIndex)
                : [];

            ThumbnailChoice choice;
            if (config.Selection == ThumbnailSelection.AtTime)
            {
                // A config also reaches the executor from a template, an import or the assistant's apply
                // path, none of which pass through Validate(): clamp the same window here, so a
                // non-finite or absurd moment is a coded frame at 0 / the ceiling, never an exception.
                double atSec = double.IsFinite(config.AtTimeSec)
                    ? Math.Clamp(config.AtTimeSec, 0, VideoSourceRef.MaxWindowSec)
                    : 0;
                if (config.Source is { } timed && config.AtTimeBasis == ThumbnailAtTimeBasis.Output)
                {
                    // Output basis: second 0 is the reference's window start; a bounded window refuses a time past its end.
                    atSec += timed.InSec ?? 0;
                    if (timed.OutSec is { } windowEnd && atSec > windowEnd)
                    {
                        return Failure(context, sw, "AT_TIME_OUT_OF_RANGE",
                            $"AtTimeSec {config.AtTimeSec:0.###} is past the end of the Source window (it ends at {windowEnd - (timed.InSec ?? 0):0.###}s on the output timeline).",
                            FailureClass.UserInput);
                    }
                }

                choice = new ThumbnailChoice(
                    atSec,
                    Math.Max(0, config.SourceIndex),
                    ShotId: null,
                    Score: null,
                    Reason: config.Source is not null ? $"explicit AtTimeSec on Source ({config.AtTimeBasis} basis)" : "explicit AtTimeSec",
                    Scores: null);
            }
            else
            {
                ThumbnailCandidate? best = candidates.FirstOrDefault();
                if (best is null)
                {
                    return Failure(context, sw, "NO_CANDIDATE_SHOT",
                        $"The analysis artifact has no shot at least {minShotSeconds:0.###}s long to choose a thumbnail from.",
                        FailureClass.UserInput);
                }

                choice = new ThumbnailChoice(
                    best.AtSec, best.SourceIndex, best.ShotId, best.Score, best.Reason, best.Scores);
            }

            // ---- 5. Which source file ----
            VideoAnalysisSourceInfo? source;
            if (explicitSourceKey is not null)
            {
                source = new VideoAnalysisSourceInfo(
                    onlySourceIndex ?? 0, explicitSourceKey, artifact?.Media ?? new VideoAnalysisMedia(0, 0, 0, 0, 0));
            }
            else
            {
                IReadOnlyList<VideoAnalysisSourceInfo> sources = artifact!.Sources ?? [];
                source = sources.FirstOrDefault(s => s.SourceIndex == choice.SourceIndex)
                    ?? sources.FirstOrDefault();
            }

            if (source is null || string.IsNullOrWhiteSpace(source.StorageKey))
            {
                // Every artifact the current analyzer produces carries at least one source entry, so
                // this is a legacy artifact or a truncated one. Refusing is the honest answer: the
                // only alternative would be to re-derive the source from the analyze step's config,
                // which is a second implementation of a rule that already exists.
                return Failure(context, sw, "ANALYSIS_SOURCE_UNRESOLVED",
                    "The analysis artifact carries no source entry for the selected shot, so the source video cannot be resolved.");
            }

            // ---- 6. Extract the frame (and compose the text band) ----
            await context.ReportProgressAsync("Extracting the frame", 40);

            const string SourceVirtualPath = "source";
            using ScratchLeases scratch = new();
            await scratch.ReserveAsync(_scratchGuard, _workspace, context.Execution.ProjectId, source.StorageKey, ct, workspace is LocalMediaWorkspace);
            await workspace.StageAsync(source.StorageKey, SourceVirtualPath, ct);

            string inputPath = await workspace.MaterializeAsync(SourceVirtualPath, ct).ConfigureAwait(false);
            string rawFramePath = await workspace.MaterializeAsync(RawFrameVirtualPath, ct).ConfigureAwait(false);

            ThumbnailOverlay? overlay = null;
            if (overlayText is not null)
            {
                // The TEXT goes into its own session file and is referenced with textfile= +
                // expansion=none; it is NEVER interpolated into the filter string. This is the same
                // two-layer defence DrawtextFilterBuilder documents for overlays and captions, and it
                // is why the sanitizer above is a second layer rather than the only one.
                await workspace.WriteTextAsync(
                    OverlaySlotVirtualPath, overlayText, MediaWorkspaceTexts.Utf8NoBom, ct).ConfigureAwait(false);
                string slotPath = await workspace.MaterializeAsync(OverlaySlotVirtualPath, ct).ConfigureAwait(false);

                // A remote (runner) session cannot read the engine host's font: no fontfile=, drawtext uses fontconfig's
                // default. A brand font below is staged into the session, so it is passed as before.
                string? fontPath = DrawtextFilterBuilder.EngineFontFor(workspace, _options.FontFilePath);
                if (config.FontProjectFileId is { } fontFileId)
                {
                    (string? stagedFont, string? fontCode, string? fontError) =
                        await StageBrandFontAsync(context, workspace, fontFileId, ct);
                    if (stagedFont is null)
                        return Failure(context, sw, fontCode!, fontError!, FailureClass.UserInput);
                    fontPath = stagedFont;
                }

                overlay = new ThumbnailOverlay(
                    slotPath,
                    fontPath,
                    Math.Max(4, (int)Math.Round(height * overlayFontSizePct / 100.0)),
                    config.OverlayFontColor,
                    config.OverlayBoxColor,
                    BandCenters.TryGetValue(config.OverlayPosition, out double band) ? band : 0.78);
            }

            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(30, SourceTimeoutPolicy.AnalyzeSeconds(_options)));

            // Two stages, so a failure names the one that broke. Stage 1 extracts and fits the frame (no text).
            // With an overlay it writes a best-quality intermediate; stage 2 burns the text band on it and
            // encodes at the requested quality. Without an overlay stage 1 writes the final JPEG directly.
            string extractTarget = overlay is null ? rawFramePath : await workspace
                .MaterializeAsync(ExtractedFrameVirtualPath, ct).ConfigureAwait(false);
            string[] args = FfmpegArgvBuilder.BuildThumbnailArgs(
                inputPath, extractTarget, choice.AtSec, width, height, cover, overlay is null ? quality : 2,
                hdrKind: await HdrToneMap.ResolveKindForFileAsync(workspace, inputPath, ct).ConfigureAwait(false));

            VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "ThumbnailGenerate step {StepOrder}: ffmpeg frame extraction failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                    step.StepOrder, result.ExitCode, result.TimedOut, Truncate(result.StdErr));
                return Failure(context, sw, "FRAME_EXTRACTION_FAILED",
                    $"ffmpeg could not extract a frame at {choice.AtSec:0.###}s (exitCode={result.ExitCode}, timedOut={result.TimedOut}). " +
                    $"Config fields used by this stage: Width={width}, Height={height}, Fit={config.Fit}, JpegQuality={quality}, " +
                    $"Selection={config.Selection}, AtTimeSec, SourceIndex. ffmpeg said: {FfmpegStderrSummary.Tail(result.StdErr)}");
            }

            if (overlay is not null)
            {
                string[] overlayArgs = FfmpegArgvBuilder.BuildThumbnailOverlayArgs(extractTarget, rawFramePath, quality, overlay);
                VideoToolResult overlayResult = await workspace.RunFfmpegAsync(overlayArgs, timeout, ct).ConfigureAwait(false);
                if (!overlayResult.Succeeded)
                {
                    _logger.LogWarning(
                        "ThumbnailGenerate step {StepOrder}: ffmpeg text overlay failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                        step.StepOrder, overlayResult.ExitCode, overlayResult.TimedOut, Truncate(overlayResult.StdErr));
                    return Failure(context, sw, "THUMBNAIL_OVERLAY_FAILED",
                        $"ffmpeg could not draw the text overlay (exitCode={overlayResult.ExitCode}, timedOut={overlayResult.TimedOut}). " +
                        $"Config fields used by this stage: OverlayPosition={config.OverlayPosition}, OverlayFontSizePct={overlayFontSizePct}, " +
                        $"OverlayFontColor={config.OverlayFontColor}, OverlayBoxColor={config.OverlayBoxColor}, OverlayText. " +
                        $"ffmpeg said: {FfmpegStderrSummary.Tail(overlayResult.StdErr)}");
                }
            }

            MediaWorkspaceStat stat = await workspace.StatAsync(RawFrameVirtualPath, ct).ConfigureAwait(false);
            if (!stat.Exists || stat.Bytes <= 0)
                return Failure(context, sw, "FRAME_EMPTY", "ffmpeg reported success but wrote no thumbnail bytes.");
            if (stat.Bytes > MaxThumbnailBytes)
                return Failure(context, sw, "FRAME_TOO_LARGE",
                    $"The encoded thumbnail is {stat.Bytes} bytes, over the {MaxThumbnailBytes}-byte cap for this step.");

            // ---- 7. Publish ----
            await context.ReportProgressAsync("Publishing the thumbnail", 80);

            string storageKey = ProjectFilePath.BuildStorageKey(
                context.Execution.ProjectId,
                PublishedCategory,
                directoryPath: context.Execution.Id.ToString("D"),
                storageFileName: outputFileName);

            string publishedKey = await workspace
                .PublishAsync(RawFrameVirtualPath, storageKey, "image/jpeg", ct)
                .ConfigureAwait(false);

            // ---- 8. The media_derivatives row — the first writer this table has ever had ----
            Guid? derivativeId = await WriteDerivativeRowAsync(
                context, source, publishedKey, stat.Bytes, width, height, choice, ct);

            // ---- 9. Optionally surface it in the project's own file list ----
            Guid? projectFileId = null;
            if (config.RegisterProjectFile)
            {
                projectFileId = await RegisterProjectFileAsync(
                    context, publishedKey, outputFileName, stat.Bytes, ct);
            }

            // ---- 10. Output ----
            var output = new JsonObject
            {
                ["status"] = "completed",
                ["thumbnailStorageKey"] = publishedKey,
                ["thumbnailFileName"] = outputFileName,
                ["derivativeId"] = derivativeId?.ToString(),
                ["projectFileId"] = projectFileId?.ToString(),
                ["width"] = width,
                ["height"] = height,
                ["sizeBytes"] = stat.Bytes,
                ["selection"] = config.Selection.ToString(),
                ["atSec"] = Math.Round(choice.AtSec, 3),
                ["sourceIndex"] = source.SourceIndex,
                ["sourceKind"] = config.Source?.Kind.ToString(),
                ["fontProjectFileId"] = overlay is not null ? config.FontProjectFileId?.ToString() : null,
                ["shotId"] = choice.ShotId,
                ["changeRequest"] = changeScope is null
                    ? null
                    : new JsonObject
                    {
                        ["requestId"] = changeScope.RequestId.ToString("D"),
                        ["changed"] = new JsonArray([.. changedKeys.Select(k => (JsonNode?)k)])
                    },
                ["score"] = choice.Score,
                ["reason"] = choice.Reason,
                ["scoreTerms"] = choice.Scores is null
                    ? null
                    : new JsonObject(choice.Scores.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
                ["overlay"] = overlay is null
                    ? null
                    : new JsonObject
                    {
                        ["text"] = overlayText,
                        ["position"] = config.OverlayPosition.ToString(),
                        ["fontSizePx"] = overlay!.FontSizePx
                    },
                ["candidatesConsidered"] = candidates.Count,
                ["candidates"] = new JsonArray(candidates
                    .Take(5)
                    .Select(c => (JsonNode)new JsonObject
                    {
                        ["shotId"] = c.ShotId,
                        ["atSec"] = Math.Round(c.AtSec, 3),
                        ["score"] = c.Score,
                        ["sourceIndex"] = c.SourceIndex
                    })
                    .ToArray()),
                ["computeTarget"] = lease.Decision.Target.ToString()
            };

            int durationMs = (int)sw.ElapsedMilliseconds;
            _logger.LogInformation(
                "ThumbnailGenerate step {StepOrder} produced {Width}x{Height} '{FileName}' ({Bytes} bytes) at {AtSec}s from shot {ShotId} on {Target}",
                step.StepOrder, width, height, outputFileName, stat.Bytes, choice.AtSec, choice.ShotId ?? "n/a", lease.Decision.Target);

            return new StepExecutionResult
            {
                Output = output.ToJsonString(EnvelopeJsonOptions),
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed,
                DurationMs = durationMs,
                // The image itself is an image, not a playable video program, so it is deliberately
                // NOT reported as OutputStorageKey: OutputsController treats an outputFiles-prefixed
                // key as a playable video, and publishing a JPEG there would make the takes view
                // offer a thumbnail as something to play. The key rides in the JSON envelope and on
                // the media_derivatives row instead, which is the index built to carry it.
                ComputeTarget = lease.Decision.Target
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ThumbnailGenerate step {StepOrder} failed unexpectedly", step.StepOrder);
            return Failure(context, sw, "THUMBNAIL_FAILED",
                $"The thumbnail step failed unexpectedly: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the analysis artifact's object key — the cross-execution override first, then this
    /// execution's own step history. Mirrors <c>VideoCompileStepExecutor.ResolveAnalysisArtifactKeyAsync</c>
    /// including its project-scope check, which is an authorization boundary rather than a lookup: a
    /// cross-execution reference must never read another project's artifact.
    /// </summary>
    private async Task<(string? Key, string? Error)> ResolveAnalysisArtifactKeyAsync(
        StepExecutionContext context, ThumbnailGenerateStepConfig config, CancellationToken ct)
    {
        if (config.AnalysisStepResultId.HasValue)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            WorkflowStepResult? result = await db.WorkflowStepResults
                .Include(r => r.WorkflowExecution)
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == config.AnalysisStepResultId.Value, ct)
                .ConfigureAwait(false);

            if (result is null)
                return (null, $"AnalysisStepResultId '{config.AnalysisStepResultId.Value}' was not found.");

            if (result.WorkflowExecution.ProjectId != context.Execution.ProjectId)
            {
                _logger.LogWarning(
                    "ThumbnailGenerate step {StepOrder}: AnalysisStepResultId {ResultId} belongs to project {OtherProject}, not this execution's project {ProjectId}; refusing to resolve",
                    context.Step.StepOrder, config.AnalysisStepResultId.Value,
                    result.WorkflowExecution.ProjectId, context.Execution.ProjectId);
                return (null, $"AnalysisStepResultId '{config.AnalysisStepResultId.Value}' does not belong to this project.");
            }

            if (await ProjectQuarantineState.ResultPredatesAsync(
                    db, context.Execution.ProjectId, result.ExecutedAt, result.WorkflowExecution.FirstStartedAt, ct, result.SupersededAt).ConfigureAwait(false))
                return (null, $"AnalysisStepResultId '{config.AnalysisStepResultId.Value}' was not found.");

            return string.IsNullOrWhiteSpace(result.ArtifactStorageKey)
                ? (null, $"AnalysisStepResultId '{config.AnalysisStepResultId.Value}' has no ArtifactStorageKey.")
                : (result.ArtifactStorageKey, null);
        }

        // LastOrDefault, not FirstOrDefault: after a ReviewLoop loop-back the history can hold two
        // entries for one order — the stale first-iteration one and the fresh one — and an explicit
        // step-order reference must always resolve to that step's MOST RECENT output.
        StepOutputHistoryEntry? entry = context.StepOutputHistory
            .LastOrDefault(h => h.StepOrder == config.AnalysisStepOrder);

        return entry is null || string.IsNullOrWhiteSpace(entry.ArtifactStorageKey)
            ? (null, $"Step {config.AnalysisStepOrder} in this execution did not produce an ArtifactStorageKey.")
            : (entry.ArtifactStorageKey, null);
    }

    /// <summary>
    /// Opens this attempt's media workspace. <b>The thumbnail step pins itself to the local target in
    /// v1</b> (<c>RunnerEligible: false</c>), and that is a deliberate, stated limitation rather than
    /// an oversight: the media session's job types are a closed set the worker's own argv validator is
    /// written against, and there is no <c>thumbnail.generate</c> job type or validator arm yet. The
    /// selector answers <c>Local</c> before it reads anything when <c>RunnerEligible</c> is false, so
    /// the <c>JobType</c> below is genuinely inert — it is passed only because the request record
    /// requires one, and it is named here so a reader is not misled into thinking a compile job is
    /// created.
    /// </summary>
    private async Task<MediaWorkspaceOpenResult> OpenMediaWorkspaceAsync(
        StepExecutionContext context, WorkflowStep step, CancellationToken ct)
    {
        if (_mediaWorkspaces is null)
        {
            VideoScratchSpace localScratch = VideoScratchSpace.Create(_options, context.Execution.Id, step.Id, _logger);
            LocalMediaWorkspace local = LocalMediaWorkspace.Create(
                localScratch, _videoToolRunner, _workspace, context.Execution.ProjectId, _logger);
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
                Purpose: $"thumbnail.generate:{step.StepOrder}",
                RunnerEligible: false),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the <see cref="MediaDerivative"/> row — the index that tells a client where the image
    /// actually is. Best-effort in the sense that a failure here does NOT fail the step: the object is
    /// already published and the step's own JSON envelope names it, so losing the index degrades a
    /// client's ability to discover the thumbnail rather than losing the thumbnail. It is logged
    /// loudly because an unwritten row is exactly the kind of silent gap this feature exists to close.
    /// </summary>
    private async Task<Guid?> WriteDerivativeRowAsync(
        StepExecutionContext context,
        VideoAnalysisSourceInfo source,
        string storageKey,
        long sizeBytes,
        int width,
        int height,
        ThumbnailChoice choice,
        CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            // SourceProjectFileId is a real foreign key with a cascade, so it is resolved rather than
            // guessed: an artifact's source key may name a project file (the ordinary case) or may be
            // an unregistered object. Only a key that really is a project_files row is recorded; a
            // fabricated id would violate the constraint and lose the whole row.
            Guid? sourceFileId = await db.ProjectFiles
                .AsNoTracking()
                .Where(f => f.ProjectId == context.Execution.ProjectId && f.StorageKey == source.StorageKey)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (sourceFileId is null)
            {
                _logger.LogWarning(
                    "ThumbnailGenerate step {StepOrder}: no project_files row matches the analysed source key '{SourceKey}', so no media_derivatives row was written for '{StorageKey}'",
                    context.Step.StepOrder, source.StorageKey, storageKey);
                return null;
            }

            var derivative = new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = context.Execution.ProjectId,
                SourceProjectFileId = sourceFileId.Value,
                Kind = MediaDerivativeKind.Thumbnail,
                StorageKey = storageKey,
                StorageBucket = string.Empty,
                MimeType = "image/jpeg",
                SizeBytes = sizeBytes,
                Width = width,
                Height = height,
                CreatedAt = DateTime.UtcNow
            };

            db.MediaDerivatives.Add(derivative);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "ThumbnailGenerate step {StepOrder}: wrote media_derivatives row {DerivativeId} for '{StorageKey}' (shot {ShotId})",
                context.Step.StepOrder, derivative.Id, storageKey, choice.ShotId ?? "n/a");

            return derivative.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ThumbnailGenerate step {StepOrder}: could not write the media_derivatives row for '{StorageKey}'; the object is published and the step output still names it",
                context.Step.StepOrder, storageKey);
            return null;
        }
    }

    /// <summary>
    /// Registers the published object as an ordinary project file so the thumbnail appears in the
    /// project's own Files list with the existing preview and download plumbing, rather than needing a
    /// bespoke viewer. Best-effort for the same reason as the derivative row.
    /// </summary>
    private async Task<Guid?> RegisterProjectFileAsync(
        StepExecutionContext context,
        string storageKey,
        string fileName,
        long sizeBytes,
        CancellationToken ct)
    {
        try
        {
            ProjectWorkspaceFile file = await _workspace.RegisterStoredObjectAsync(
                context.Execution.ProjectId,
                storageKey,
                fileName,
                "image/jpeg",
                sizeBytes,
                SummaryStatus.Done,
                FileIndexingStatus.NotIndexed,
                ct,
                category: PublishedCategory).ConfigureAwait(false);

            return file.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ThumbnailGenerate step {StepOrder}: could not register '{StorageKey}' as a project file",
                context.Step.StepOrder, storageKey);
            return null;
        }
    }

    /// <summary>The virtual name a staged brand font gets in the media session.</summary>
    private const string BrandFontVirtualPath = "brand-font";

    /// <summary>
    /// Resolves a brand font: the file must belong to THIS run's project, be a .ttf/.otf at most
    /// <see cref="ThumbnailGenerateStepConfig.MaxFontBytes"/> long, and start with a TrueType/OpenType magic
    /// number. It is staged into the session and the session path (server-chosen, never user text) is what
    /// drawtext's <c>fontfile=</c> gets. Every refusal is a coded user-input failure - never a silent fallback.
    /// </summary>
    private async Task<(string? Path, string? Code, string? Error)> StageBrandFontAsync(
        StepExecutionContext context, IMediaWorkspace workspace, Guid fontFileId, CancellationToken ct)
    {
        ProjectFile? file;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            file = await db.ProjectFiles.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fontFileId && f.ProjectId == context.Execution.ProjectId, ct)
                .ConfigureAwait(false);
        }

        if (file is null)
            return (null, "FONT_NOT_FOUND", "FontProjectFileId does not name a file in this project.");

        string ext = Path.GetExtension(file.OriginalFileName ?? string.Empty);
        if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            return (null, "FONT_INVALID", "FontProjectFileId must name a .ttf or .otf file.");

        if (file.SizeBytes <= 0 || file.SizeBytes > ThumbnailGenerateStepConfig.MaxFontBytes)
            return (null, "FONT_INVALID", "The brand font file must be between 1 byte and 10 MiB.");

        // The cap is enforced by the stage itself while it copies (the row's SizeBytes is only a pre-check).
        try
        {
            await workspace.StageAsync(file.StorageKey, BrandFontVirtualPath, ThumbnailGenerateStepConfig.MaxFontBytes, ct).ConfigureAwait(false);
        }
        catch (MediaStageLimitExceededException)
        {
            return (null, "FONT_INVALID", "The brand font file must be between 1 byte and 10 MiB.");
        }

        MediaWorkspaceStat stat = await workspace.StatAsync(BrandFontVirtualPath, ct).ConfigureAwait(false);
        if (!stat.Exists || stat.Bytes <= 0 || stat.Bytes > ThumbnailGenerateStepConfig.MaxFontBytes)
            return (null, "FONT_INVALID", "The brand font file must be between 1 byte and 10 MiB.");

        byte[] head = await workspace.ReadAsync(BrandFontVirtualPath, (int)ThumbnailGenerateStepConfig.MaxFontBytes, ct).ConfigureAwait(false);
        if (!HasFontMagic(head))
            return (null, "FONT_INVALID", "The file does not look like a TrueType/OpenType font (its first bytes are not a font header).");

        return (await workspace.MaterializeAsync(BrandFontVirtualPath, ct).ConfigureAwait(false), null, null);
    }

    /// <summary>TrueType (<c>00 01 00 00</c> or <c>true</c>) or OpenType/CFF (<c>OTTO</c>) sfnt header.</summary>
    private static bool HasFontMagic(byte[] bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) ||
         (bytes[0] == (byte)'t' && bytes[1] == (byte)'r' && bytes[2] == (byte)'u' && bytes[3] == (byte)'e') ||
         (bytes[0] == (byte)'O' && bytes[1] == (byte)'T' && bytes[2] == (byte)'T' && bytes[3] == (byte)'O'));

    /// <summary>
    /// Resolves an explicit <c>Source</c> to a storage key. Same rules as <c>ExtractFrame</c>; a project file is
    /// looked up in THIS run's project only. Every refusal is a coded, user-input failure.
    /// </summary>
    private async Task<(string? Key, string? Error, string? Code)> ResolveExplicitSourceKeyAsync(
        StepExecutionContext context, VideoSourceRef source, CancellationToken ct)
    {
        switch (source.Kind)
        {
            case VideoSourceKind.PreviousStepOutput:
                {
                    StepOutputHistoryEntry? entry = context.StepOutputHistory.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.OutputStorageKey));
                    return entry is null
                        ? (null, "Source=PreviousStepOutput but no prior step in this execution produced a video OutputStorageKey.", "SOURCE_UNRESOLVED")
                        : (entry.OutputStorageKey, null, null);
                }

            case VideoSourceKind.StepOutput:
                {
                    // LastOrDefault: after a ReviewLoop loop-back an order can have two entries; the latest wins.
                    StepOutputHistoryEntry? entry = context.StepOutputHistory.LastOrDefault(h => h.StepOrder == source.StepOrder);
                    return source.StepOrder is null || entry is null || string.IsNullOrWhiteSpace(entry.OutputStorageKey)
                        ? (null, $"Step {source.StepOrder} did not produce a video OutputStorageKey.", "SOURCE_UNRESOLVED")
                        : (entry.OutputStorageKey, null, null);
                }

            case VideoSourceKind.ProjectFile:
                {
                    if (source.ProjectFileId is null)
                        return (null, "Source=ProjectFile requires ProjectFileId.", "SOURCE_UNRESOLVED");

                    using IServiceScope scope = _scopeFactory.CreateScope();
                    WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                    string? key = await db.ProjectFiles.AsNoTracking()
                        .Where(f => f.Id == source.ProjectFileId.Value && f.ProjectId == context.Execution.ProjectId)
                        .Select(f => f.StorageKey)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    return key is null
                        ? (null, $"ProjectFile '{source.ProjectFileId.Value}' was not found in this project.", "SOURCE_NOT_FOUND")
                        : (key, null, null);
                }

            case VideoSourceKind.StepResult:
                {
                    // The P0 resolver contract: same-project addressable outputs only; a failed, unfinished or
                    // non-video row answers SOURCE_NOT_PLAYABLE, anything foreign or missing SOURCE_NOT_FOUND.
                    StepResultSourceResolution resolution = await StepResultSourceResolver.ResolveAsync(
                        _scopeFactory, context.Execution.ProjectId, source, allowImages: false, ct).ConfigureAwait(false);
                    return resolution.StorageKey is { } resultKey
                        ? (resultKey, null, null)
                        : (null, resolution.Message ?? StepResultSourceResolver.NotFoundMessage, resolution.Code ?? StepResultSourceResolver.NotFoundCode);
                }

            default:
                return (null, $"Source kind '{source.Kind}' is not supported by ThumbnailGenerate.", "SOURCE_UNRESOLVED");
        }
    }

    /// <summary>
    /// A bare, safe file name: the last path segment, with anything that is not a letter, digit, dot,
    /// dash or underscore replaced by a dash. Never trusted verbatim, because it becomes the final
    /// segment of an object key.
    /// </summary>
    private static string SanitizeFileName(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        string name = Path.GetFileName(raw.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return fallback;

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');

        string cleaned = sb.ToString().Trim('.', '-', '_');
        if (cleaned.Length == 0)
            return fallback;

        if (!cleaned.Contains('.'))
            cleaned += ".jpg";

        return cleaned.Length > 128 ? cleaned[..128] : cleaned;
    }

    private static string TrimBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    private static string Truncate(string value, int max = 2000) =>
        value.Length <= max ? value : value[^max..];

    /// <summary>
    /// T-006: screens the sanitized overlay text before it reaches drawtext. Returns the plain user
    /// message on a Block, or null to proceed (Allow, Review, or an unavailable call — fails open).
    /// </summary>
    private async Task<string?> ScreenGeneratedTextAsync(
        StepExecutionContext context, string? text, CancellationToken ct)
    {
        if (_moderation is null || string.IsNullOrWhiteSpace(text))
            return null;

        var attribution = new ModerationAttribution(
            context.Execution.OrganizationId,
            context.Execution.ProjectId,
            context.Execution.InitiatedByUserId,
            context.Execution.Id,
            context.StepResultId,
            ModerationSurface.GeneratedText,
            ModerationContentKind.Text);

        ModerationDecision decision = await _moderation.ModerateAsync(
            new ModerationInput(text, [], ModerationContentKind.Text), attribution, ct);

        if (decision.Action == ModerationAction.Block)
        {
            return string.IsNullOrWhiteSpace(decision.UserMessage)
                ? "This overlay text cannot be rendered."
                : decision.UserMessage;
        }

        if (!decision.Available)
        {
            _logger.LogWarning(
                "Content moderation was unavailable for the thumbnail overlay at step {StepOrder}; proceeding because generated text fails open",
                context.Step.StepOrder);
        }

        return null;
    }

    /// <summary>
    /// Applies a request's overrides (item <c>thumbnail</c> or <c>*</c>) to the config. The text lands in the config
    /// like any text (the ordinary sanitiser and the ffmpeg textfile path take it from there); an overlay text with no
    /// position set makes the position LowerThird, and an <c>atTimeSec</c> switches the selection to AtTime.
    /// </summary>
    internal static (ThumbnailGenerateStepConfig Config, List<string> Changed, string? Refusal) ApplyChange(
        ThumbnailGenerateStepConfig config, ChangeRequestPayload payload)
    {
        Dictionary<string, JsonElement> merged = new(StringComparer.Ordinal);
        foreach (string item in new[] { ChangeRequestValidator.AllItems, StepChangeCapabilities.ThumbnailItemId })
        {
            if (payload.Overrides is not null && payload.Overrides.TryGetValue(item, out IReadOnlyDictionary<string, JsonElement>? keys) && keys is not null)
            {
                foreach ((string k, JsonElement v) in keys)
                    merged[k] = v;
            }
        }

        List<string> changed = [];
        ThumbnailGenerateStepConfig result = config;
        foreach ((string key, JsonElement value) in merged.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            switch (key)
            {
                case "overlayText" when value.ValueKind == JsonValueKind.String:
                    result = result with { OverlayText = value.GetString() };
                    if (!merged.ContainsKey("overlayPosition") && result.OverlayPosition == ThumbnailOverlayPosition.None)
                        result = result with { OverlayPosition = ThumbnailOverlayPosition.LowerThird };
                    break;
                case "atTimeSec" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double at):
                    result = result with { Selection = ThumbnailSelection.AtTime, AtTimeSec = at };
                    break;
                case "overlayPosition" when value.ValueKind == JsonValueKind.String
                    && Enum.TryParse(value.GetString(), ignoreCase: true, out ThumbnailOverlayPosition position) && Enum.IsDefined(position):
                    result = result with { OverlayPosition = position };
                    break;
                case "overlayFontColor" when value.ValueKind == JsonValueKind.String
                    && ThumbnailGenerateStepConfig.IsAllowedFontColor(value.GetString()?.ToLowerInvariant()):
                    result = result with { OverlayFontColor = value.GetString()!.ToLowerInvariant() };
                    break;
                case "overlayBoxColor" when value.ValueKind == JsonValueKind.String
                    && ThumbnailGenerateStepConfig.IsAllowedBoxColor(value.GetString()):
                    result = result with { OverlayBoxColor = value.GetString()!.ToLowerInvariant() };
                    break;
                default:
                    continue;
            }

            changed.Add(key);
        }

        return changed.Count == 0
            ? (config, changed, "The change request carries no override this step accepts, so nothing was changed. Set overlayText, atTimeSec, overlayPosition, overlayFontColor or overlayBoxColor and ask again.")
            : (result, changed, null);
    }

    private static StepExecutionResult Failure(
        StepExecutionContext context, Stopwatch sw, string code, string message, FailureClass failureClass = FailureClass.PlatformInternal)
    {
        var output = new JsonObject
        {
            ["status"] = "failed",
            ["errorCode"] = code,
            ["message"] = message
        };

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

    /// <summary>The resolved moment, plus everything the step output needs to explain choosing it.</summary>
    private sealed record ThumbnailChoice(
        double AtSec,
        int SourceIndex,
        string? ShotId,
        double? Score,
        string Reason,
        IReadOnlyDictionary<string, double>? Scores);
}
