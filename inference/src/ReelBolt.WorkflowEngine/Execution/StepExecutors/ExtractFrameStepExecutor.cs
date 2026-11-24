using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.ExtractFrame</c> (T-105) — deterministic, non-LLM and free: grabs ONE frame (at a time, the
/// first or the last) of ONE video with a single ffmpeg call, publishes it as an image under
/// <c>outputFiles/{execution}/step-{n}-frame.{ext}</c>, and optionally registers it as a project file. The
/// image is the step's <c>OutputStorageKey</c>, so a later <c>VideoGenerate.FirstFrame</c>/<c>LastFrame</c>
/// (or a human) can use it.
/// <para>
/// The source is a typed <see cref="VideoSourceRef"/>; a <see cref="VideoSourceKind.StepResult"/> source goes
/// through the one shared <see cref="StepResultSourceResolver"/>. Nothing a user typed reaches the argv as
/// text: <see cref="FfmpegArgvBuilder.BuildExtractFrameArgs"/> formats every number from a typed, validated
/// value. <b>It never throws</b>: every failure is a coded <see cref="StepStatus.Failed"/> with valid JSON.
/// </para>
/// </summary>
public sealed class ExtractFrameStepExecutor : IStepExecutor
{
    /// <summary>Largest image the step will accept back from ffmpeg: a runaway guard, not a budget.</summary>
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private const string FrameVirtualPath = "extract-frame-out";
    private const string SourceVirtualPath = "source";

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IVideoToolRunner _videoToolRunner;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<ExtractFrameStepExecutor> _logger;
    private readonly IMediaWorkspaceFactory? _mediaWorkspaces;
    private readonly IScratchDiskGuard? _scratchGuard;

    public ExtractFrameStepExecutor(
        IVideoToolRunner videoToolRunner,
        IProjectFileWorkspace workspace,
        IServiceScopeFactory scopeFactory,
        IOptions<VideoEditingOptions> options,
        ILogger<ExtractFrameStepExecutor> logger,
        IMediaWorkspaceFactory? mediaWorkspaces = null,
        IScratchDiskGuard? scratchGuard = null)
    {
        _scratchGuard = scratchGuard;
        _videoToolRunner = videoToolRunner;
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _mediaWorkspaces = mediaWorkspaces;
    }

    public StepType StepType => StepType.ExtractFrame;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        CancellationToken ct = context.CancellationToken;
        WorkflowStep step = context.Step;

        try
        {
            // ---- 1. Config ----
            if (string.IsNullOrWhiteSpace(step.ExtractFrameConfigJson))
                return Failure(context, sw, "CONFIG_INVALID", "The ExtractFrame step has no ExtractFrameConfigJson configured.", FailureClass.UserInput);

            ExtractFrameStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<ExtractFrameStepConfig>(step.ExtractFrameConfigJson, ExtractFrameStepConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"ExtractFrameConfigJson is not valid JSON: {ex.Message}", FailureClass.UserInput);
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "ExtractFrameConfigJson deserialized to null.", FailureClass.UserInput);

            // Re-validated here as well as at save time: a config can arrive from a template, an import or the
            // assistant's apply path, none of which pass through the save-time validator.
            List<string> errors = config.Validate();
            if (errors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", errors), FailureClass.UserInput);

            string format = config.NormalizedFormat!;
            string extension = format == "jpeg" ? "jpg" : "png";
            string contentType = format == "jpeg" ? "image/jpeg" : "image/png";

            // ---- 2. The source ----
            await context.ReportProgressAsync("Resolving the source video");
            (string? sourceKey, string? sourceError, string? sourceCode) = await ResolveSourceKeyAsync(context, config.Source);
            if (sourceKey is null)
                return Failure(context, sw, sourceCode ?? "SOURCE_UNRESOLVED", sourceError ?? "The source video could not be resolved.", FailureClass.UserInput);

            // ---- 3. Workspace + ffmpeg ----
            MediaWorkspaceOpenResult opened = await OpenMediaWorkspaceAsync(context, step, ct);
            if (opened.Refused)
                return Failure(context, sw, opened.FailureCode!, opened.Message!);

            await using MediaWorkspaceLease lease = opened.Lease!;
            IMediaWorkspace workspace = lease.Workspace;

            await context.ReportProgressAsync("Extracting the frame", 40);
            using ScratchLeases scratch = new();
            await scratch.ReserveAsync(_scratchGuard, _workspace, context.Execution.ProjectId, sourceKey, ct, workspace is LocalMediaWorkspace);
            await workspace.StageAsync(sourceKey, SourceVirtualPath, ct);
            string inputPath = await workspace.MaterializeAsync(SourceVirtualPath, ct).ConfigureAwait(false);
            string outputPath = await workspace.MaterializeAsync($"{FrameVirtualPath}.{extension}", ct).ConfigureAwait(false);

            string[] args = FfmpegArgvBuilder.BuildExtractFrameArgs(
                inputPath, outputPath, config.At, config.AtSec, format, config.MaxLongSide, config.Crop,
                await HdrToneMap.ResolveKindForFileAsync(workspace, inputPath, ct).ConfigureAwait(false));

            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(30, SourceTimeoutPolicy.AnalyzeSeconds(_options)));
            VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "ExtractFrame step {StepOrder}: ffmpeg failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                    step.StepOrder, result.ExitCode, result.TimedOut, Truncate(result.StdErr));
                return Failure(context, sw, "FRAME_EXTRACTION_FAILED",
                    $"ffmpeg could not extract the frame (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
            }

            string framePath = $"{FrameVirtualPath}.{extension}";
            MediaWorkspaceStat stat = await workspace.StatAsync(framePath, ct).ConfigureAwait(false);
            if (!stat.Exists || stat.Bytes <= 0)
            {
                // A seek past the end of the video decodes nothing: ffmpeg exits 0 and writes no image.
                return config.At == FrameAt.Time
                    ? Failure(context, sw, "FRAME_OUT_OF_RANGE",
                        $"No frame exists at {config.AtSec:0.###}s: the video ends before it.", FailureClass.UserInput)
                    : Failure(context, sw, "FRAME_EMPTY", "ffmpeg reported success but wrote no frame.");
            }

            if (stat.Bytes > MaxFrameBytes)
                return Failure(context, sw, "FRAME_TOO_LARGE", $"The frame is {stat.Bytes} bytes, over the {MaxFrameBytes}-byte cap for this step.");

            byte[] bytes = await workspace.ReadAsync(framePath, ct).ConfigureAwait(false);
            (int Width, int Height)? size = ImageDimensions.TryRead(bytes);

            // ---- 4. Publish ----
            await context.ReportProgressAsync("Publishing the frame", 80);
            string storageFileName = $"step-{step.StepOrder}-frame.{extension}";
            string storageKey = ProjectFilePath.BuildStorageKey(
                context.Execution.ProjectId,
                ThumbnailGenerateStepExecutor.PublishedCategory,
                directoryPath: context.Execution.Id.ToString("D"),
                storageFileName: storageFileName);

            string publishedKey = await workspace.PublishAsync(framePath, storageKey, contentType, ct).ConfigureAwait(false);

            Guid? projectFileId = null;
            if (config.RegisterProjectFile)
                projectFileId = await RegisterProjectFileAsync(context, publishedKey, storageFileName, contentType, stat.Bytes, ct);

            // ---- 5. Output ----
            double? atSec = config.At == FrameAt.Time ? Math.Round(config.AtSec!.Value, 3) : null;
            var output = new JsonObject
            {
                ["status"] = "completed",
                // QA-P1-14: no storage key in the output (the no-storage-key rule, as for
                // ThumbnailGenerate). The frame is addressed by this step's OutputStorageKey and,
                // when registered, its projectFileId.
                ["fileName"] = storageFileName,
                ["format"] = format,
                ["width"] = size?.Width,
                ["height"] = size?.Height,
                ["atSec"] = atSec,
                ["at"] = config.At.ToString(),
                ["sizeBytes"] = stat.Bytes,
                ["projectFileId"] = projectFileId?.ToString(),
                ["computeTarget"] = lease.Decision.Target.ToString()
            };

            _logger.LogInformation(
                "ExtractFrame step {StepOrder} produced '{FileName}' ({Bytes} bytes, {Width}x{Height}) at {At} on {Target}",
                step.StepOrder, storageFileName, stat.Bytes, size?.Width, size?.Height, config.At, lease.Decision.Target);

            return new StepExecutionResult
            {
                Output = output.ToJsonString(EnvelopeJsonOptions),
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed,
                DurationMs = (int)sw.ElapsedMilliseconds,
                // The image IS this step's output: a later step names it through its OutputStorageKey.
                OutputStorageKey = publishedKey,
                ComputeTarget = lease.Decision.Target
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractFrame step {StepOrder} failed unexpectedly", step.StepOrder);
            return Failure(context, sw, "EXTRACT_FRAME_FAILED", "The frame extraction failed unexpectedly. The detail is in the server log.");
        }
    }

    /// <summary>Resolves the one video to a storage key. Every refusal is a coded, user-input failure.</summary>
    private async Task<(string? Key, string? Error, string? Code)> ResolveSourceKeyAsync(
        StepExecutionContext context, VideoSourceRef source)
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
                    // LastOrDefault: after a ReviewLoop loop-back an order can have two entries, and an explicit
                    // step reference must resolve to that step's most recent output.
                    StepOutputHistoryEntry? entry = context.StepOutputHistory.LastOrDefault(h => h.StepOrder == source.StepOrder);
                    return source.StepOrder is null || entry is null || string.IsNullOrWhiteSpace(entry.OutputStorageKey)
                        ? (null, $"Step {source.StepOrder} did not produce a video OutputStorageKey.", "SOURCE_UNRESOLVED")
                        : (entry.OutputStorageKey, null, null);
                }

            case VideoSourceKind.ProjectFile:
                {
                    if (source.ProjectFileId is null)
                        return (null, "Source=ProjectFile requires ProjectFileId.", "SOURCE_UNRESOLVED");

                    IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.Execution.ProjectId, context.CancellationToken);
                    ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == source.ProjectFileId.Value);
                    return file is null
                        ? (null, $"ProjectFile '{source.ProjectFileId.Value}' was not found in this project.", "SOURCE_NOT_FOUND")
                        : (file.StorageKey, null, null);
                }

            case VideoSourceKind.StepResult:
                {
                    StepResultSourceResolution resolved = await StepResultSourceResolver.ResolveAsync(
                        _scopeFactory, context.Execution.ProjectId, source, allowImages: false, context.CancellationToken);
                    return resolved.StorageKey is { } key
                        ? (key, null, null)
                        : (null, resolved.Message, resolved.Code);
                }

            default:
                return (null, $"Source kind '{source.Kind}' is not supported by ExtractFrame.", "SOURCE_UNRESOLVED");
        }
    }

    /// <summary>Pinned to the local target, like ThumbnailGenerate: there is no runner job type for a frame grab.</summary>
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
                Purpose: $"extract.frame:{step.StepOrder}",
                RunnerEligible: false),
            ct).ConfigureAwait(false);
    }

    /// <summary>Best-effort, like the thumbnail's: the object is published and the output names it, so a failed registration only costs discoverability.</summary>
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
            _logger.LogError(ex, "ExtractFrame step {StepOrder}: could not register '{StorageKey}' as a project file", context.Step.StepOrder, storageKey);
            return null;
        }
    }

    private static string Truncate(string value, int max = 2000) => value.Length <= max ? value : value[^max..];

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
