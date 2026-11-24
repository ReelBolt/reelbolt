using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.PinInsert</c> (T-206) — the "pinning step" with no agent. Reads the tracked screens a
/// <c>VideoAnalyze</c> step offered, resolves each pin's content reference on the server (a project file of this
/// project, an earlier step of this run, or a step result of this project — never a key a model typed) and emits a
/// <see cref="MotionGraphicsPlanOutput"/> with screen inserts only, so a <c>VideoCompile</c> with
/// <c>graphicsPlan: Step N</c> composites it unchanged. Deterministic and free: no provider, no media work.
/// <para>
/// Region choice: a pin naming a region claims it; the catch-all pin (no region) takes every other offered screen
/// (feature-tracked objects such as a licence plate are never a catch-all target, like the compile's own
/// deterministic insert content). <b>It never throws</b>: every failure is a coded <see cref="StepStatus.Failed"/>.
/// </para>
/// </summary>
public sealed class PinInsertStepExecutor : IStepExecutor
{
    /// <summary>The plan's <c>reason</c> prefix: the compile and the reviewer see the insert was pinned, not chosen.</summary>
    internal const string PinnedReasonPrefix = "Pinned by a PinInsert step";

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IProjectFileWorkspace _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<PinInsertStepExecutor> _logger;

    public PinInsertStepExecutor(
        IProjectFileWorkspace workspace,
        IServiceScopeFactory scopeFactory,
        IOptions<VideoEditingOptions> options,
        ILogger<PinInsertStepExecutor> logger)
    {
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public StepType StepType => StepType.PinInsert;

    /// <summary>What one pin's content resolved to: the plan's asset string, or why it did not resolve.</summary>
    internal sealed record ResolvedContent(string? Asset, string? Problem, string From);

    /// <summary>One region that got content.</summary>
    internal sealed record PinnedRegion(string RegionId, int PinIndex, string Asset, string From);

    /// <summary>One pin (or one pin's region) that could not be pinned, and why.</summary>
    internal sealed record SkippedPin(int PinIndex, string? RegionId, string Reason);

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        CancellationToken ct = context.CancellationToken;
        WorkflowStep step = context.Step;

        try
        {
            // ---- 1. Config (re-validated: templates, imports and the assistant bypass the save validator) ----
            if (string.IsNullOrWhiteSpace(step.PinInsertConfigJson))
                return Failure(context, sw, "CONFIG_INVALID", "The PinInsert step has no PinInsertConfigJson configured.", FailureClass.UserInput);

            PinInsertStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<PinInsertStepConfig>(step.PinInsertConfigJson, PinInsertStepConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"PinInsertConfigJson is not valid JSON: {ex.Message}", FailureClass.UserInput);
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "PinInsertConfigJson deserialized to null.", FailureClass.UserInput);

            List<string> errors = config.Validate();
            if (errors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", errors), FailureClass.UserInput);

            // ---- 2. The analysis and the screens it offered ----
            await context.ReportProgressAsync("Reading the tracked screens");
            StepOutputHistoryEntry? analysisEntry = config.Analysis.From == ExtractInputSource.Step
                ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == config.Analysis.StepOrder)
                : context.StepOutputHistory.LastOrDefault();
            if (analysisEntry is null || string.IsNullOrWhiteSpace(analysisEntry.ArtifactStorageKey))
            {
                string which = config.Analysis.From == ExtractInputSource.Step ? $"Step {config.Analysis.StepOrder}" : "The previous step";
                return Failure(context, sw, "ANALYSIS_NOT_FOUND",
                    $"{which} in this execution did not produce a VideoAnalyze artifact to pin screens from.", FailureClass.UserInput);
            }

            VideoAnalysisArtifact? artifact = await ReadArtifactAsync(context, analysisEntry.ArtifactStorageKey!, ct);
            if (artifact is null)
                return Failure(context, sw, "ANALYSIS_INVALID", $"The analysis artifact of step {analysisEntry.StepOrder} could not be read.");

            List<VideoInsertRegionTrack> offered = OfferedRegions(artifact);
            if (offered.Count == 0)
            {
                // The step panel shows ErrorDetails as "What went wrong", so it carries the plain sentence;
                // the operator detail (step number, detectInsertRegions) goes to the log.
                _logger.LogWarning(
                    "PinInsert step {StepOrder}: analysis step {AnalysisStep} offered no tracked screens to pin; turn on detectInsertRegions in that VideoAnalyze step and check the footage shows a green, blue or magenta screen",
                    step.StepOrder, analysisEntry.StepOrder);
                return Failure(context, sw, "PIN_INSERT_NO_REGIONS",
                    Services.Billing.FailureMessages.UserMessage("PIN_INSERT_NO_REGIONS"), FailureClass.UserInput);
            }

            // ---- 3. Content, resolved on the server ----
            await context.ReportProgressAsync("Resolving the content", 50);
            List<ResolvedContent> contents = [];
            foreach (PinInsertEntry pin in config.Pins)
                contents.Add(await ResolveContentAsync(context, pin.Content, ct));

            (List<PinnedRegion> pinned, List<SkippedPin> skipped) = Plan(config, offered, contents);

            if (config.RequireApplied && skipped.Count > 0)
            {
                return Failure(context, sw, "PIN_INSERT_UNRESOLVED",
                    "Not every pin could be applied: " + string.Join("; ", skipped.Select(Describe)) +
                    ". Fix the pins, or set requireApplied to false to pin what resolves.", FailureClass.UserInput);
            }

            // ---- 4. Output: a MotionGraphicsPlanOutput (inserts only) plus a report ----
            var plan = new JsonObject
            {
                ["overlays"] = new JsonArray(),
                ["inserts"] = new JsonArray(pinned.Select(p => (JsonNode)new JsonObject
                {
                    ["regionId"] = p.RegionId,
                    ["renderedAssetStorageKey"] = p.Asset,
                    ["reason"] = $"{PinnedReasonPrefix} (pin {p.PinIndex + 1}, content from {p.From})."
                }).ToArray()),
                ["planRationale"] = pinned.Count == 0
                    ? "No pin resolved, so no screen insert is planned."
                    : $"{pinned.Count} tracked screen(s) pinned deterministically from step {analysisEntry.StepOrder}'s analysis.",
                ["pinInsert"] = new JsonObject
                {
                    ["analysisStepOrder"] = analysisEntry.StepOrder,
                    ["offeredRegionIds"] = new JsonArray(offered.Select(r => (JsonNode)r.Id).ToArray()),
                    ["pinned"] = new JsonArray(pinned.Select(p => (JsonNode)new JsonObject
                    {
                        ["regionId"] = p.RegionId,
                        ["pin"] = p.PinIndex + 1,
                        ["from"] = p.From
                    }).ToArray()),
                    ["skipped"] = new JsonArray(skipped.Select(s => (JsonNode)new JsonObject
                    {
                        ["pin"] = s.PinIndex + 1,
                        ["regionId"] = s.RegionId,
                        ["reason"] = s.Reason
                    }).ToArray()),
                    ["requireApplied"] = config.RequireApplied
                }
            };

            _logger.LogInformation(
                "PinInsert step {StepOrder}: pinned {Pinned} region(s) from analysis step {AnalysisStep}, skipped {Skipped}",
                step.StepOrder, pinned.Count, analysisEntry.StepOrder, skipped.Count);

            return new StepExecutionResult
            {
                Output = plan.ToJsonString(EnvelopeJsonOptions),
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed,
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PinInsert step {StepOrder} failed unexpectedly", step.StepOrder);
            return Failure(context, sw, "PIN_INSERT_FAILED", $"Pinning the screen inserts failed unexpectedly: {ex.Message}");
        }
    }

    /// <summary>The regions the analysis OFFERED (offered is stricter than exists), in offer order.</summary>
    internal static List<VideoInsertRegionTrack> OfferedRegions(VideoAnalysisArtifact artifact)
    {
        HashSet<string> offeredIds = new(artifact.OfferedInsertRegionIds ?? [], StringComparer.Ordinal);
        return (artifact.InsertRegions ?? []).Where(r => offeredIds.Contains(r.Id)).ToList();
    }

    /// <summary>
    /// Pure region assignment: explicit pins first (an unoffered region is skipped), then the catch-all pin over
    /// every remaining offered screen. A pin whose content did not resolve is skipped with the reason.
    /// </summary>
    internal static (List<PinnedRegion> Pinned, List<SkippedPin> Skipped) Plan(
        PinInsertStepConfig config, IReadOnlyList<VideoInsertRegionTrack> offered, IReadOnlyList<ResolvedContent> contents)
    {
        List<PinnedRegion> pinned = [];
        List<SkippedPin> skipped = [];
        HashSet<string> claimed = new(StringComparer.Ordinal);
        Dictionary<string, VideoInsertRegionTrack> byId = offered.ToDictionary(r => r.Id, StringComparer.Ordinal);

        for (int i = 0; i < config.Pins.Count; i++)
        {
            string? regionId = config.Pins[i].RegionId;
            if (regionId is null)
                continue;

            claimed.Add(regionId);
            if (!byId.ContainsKey(regionId))
            {
                skipped.Add(new SkippedPin(i, regionId,
                    $"the analysis did not offer region {regionId} (it offered {string.Join(", ", offered.Select(r => r.Id))})"));
                continue;
            }

            if (contents[i].Asset is not { } asset)
            {
                skipped.Add(new SkippedPin(i, regionId, contents[i].Problem ?? "the content did not resolve"));
                continue;
            }

            pinned.Add(new PinnedRegion(regionId, i, asset, contents[i].From));
        }

        int catchAll = -1;
        for (int i = 0; i < config.Pins.Count; i++)
        {
            if (config.Pins[i].RegionId is null)
                catchAll = i;
        }

        if (catchAll >= 0)
        {
            List<VideoInsertRegionTrack> rest = offered
                .Where(r => r.Method != "feature" && !claimed.Contains(r.Id))
                .ToList();
            if (contents[catchAll].Asset is not { } asset)
                skipped.Add(new SkippedPin(catchAll, null, contents[catchAll].Problem ?? "the content did not resolve"));
            else if (rest.Count == 0)
                skipped.Add(new SkippedPin(catchAll, null, "no offered screen was left for the pin without a region"));
            else
                pinned.AddRange(rest.Select(r => new PinnedRegion(r.Id, catchAll, asset, contents[catchAll].From)));
        }

        // Plan order follows the analysis' own offer order, so a re-run emits byte-identical output.
        List<string> order = offered.Select(r => r.Id).ToList();
        pinned.Sort((a, b) => order.IndexOf(a.RegionId).CompareTo(order.IndexOf(b.RegionId)));
        return (pinned, skipped);
    }

    private static string Describe(SkippedPin s) =>
        s.RegionId is null ? $"pin {s.PinIndex + 1}: {s.Reason}" : $"pin {s.PinIndex + 1} ({s.RegionId}): {s.Reason}";

    /// <summary>
    /// Resolves one content reference to the string the compile accepts as a plan insert's asset: a project file
    /// by its id (the compile re-reads it from this project's own file list), or the stored output of an earlier
    /// step of this run or of a step result of this project (the T-106 resolver: foreign ids never resolve).
    /// </summary>
    private async Task<ResolvedContent> ResolveContentAsync(StepExecutionContext context, InsertContentRef reference, CancellationToken ct)
    {
        string kind = reference.Kind ?? reference.From;
        if (reference.Kind == InsertContentRef.ProjectFileKind)
        {
            IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.Execution.ProjectId, ct) ?? [];
            ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == reference.ProjectFileId);
            if (file is null)
                return new(null, $"project file {reference.ProjectFileId} is not in this project", kind);
            return VideoCompileStepExecutor.IsInsertableMime(file.MimeType)
                ? new(file.Id.ToString("D"), null, kind)
                : new(null, $"project file '{file.OriginalFileName}' is not a video or an image", kind);
        }

        if (reference.Kind == InsertContentRef.StepKind && reference.StepOrder >= context.Step.StepOrder)
            return new(null, $"step {reference.StepOrder} is not an earlier step", kind);

        InsertContentRefResolver.Resolution resolution = await InsertContentRefResolver.ResolveDetailedAsync(
            reference, context.Execution.ProjectId, context.StepOutputHistory, _scopeFactory, ct);
        if (resolution.File is not { } resolved)
        {
            string what = reference.Kind == InsertContentRef.StepKind ? $"step {reference.StepOrder}" : $"step result {reference.StepResultId}";
            return new(null, resolution.FailureCode == StepResultSourceResolver.NotPlayableCode
                ? $"{what} has no finished video or image"
                : $"{what} has no output in this project", kind);
        }

        return VideoCompileStepExecutor.IsInsertableMime(resolved.MimeType)
            ? new(resolved.StorageKey, null, kind)
            : new(null, $"the output of {(reference.Kind == InsertContentRef.StepKind ? $"step {reference.StepOrder}" : "that step result")} is not a video or an image", kind);
    }

    private async Task<VideoAnalysisArtifact?> ReadArtifactAsync(StepExecutionContext context, string artifactKey, CancellationToken ct)
    {
        using VideoScratchSpace scratch = VideoScratchSpace.Create(_options, context.Execution.Id, context.Step.Id, _logger);
        string localPath = scratch.GetPath("analysis-for-pins.json");
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(context.Execution.ProjectId, artifactKey, localPath, ct);
            string json = await File.ReadAllTextAsync(localPath, ct);
            return JsonSerializer.Deserialize<VideoAnalysisArtifact>(json.TrimStart('﻿'), ArtifactJsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "PinInsert step {StepOrder}: the analysis artifact {Key} could not be read", context.Step.StepOrder, artifactKey);
            return null;
        }
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
