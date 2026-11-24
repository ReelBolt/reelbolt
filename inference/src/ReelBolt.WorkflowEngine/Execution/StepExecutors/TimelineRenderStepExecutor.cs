using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Styles;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video.Quality;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.TimelineRender"/>: renders an editor-v2 document to a delivery file.
/// Deterministic, non-LLM, never throws (every failure is a <see cref="StepStatus.Failed"/> result
/// whose Output is valid JSON naming the reason — <c>output_json</c> is <c>jsonb</c>).
/// </summary>
/// <remarks>
/// <para>Order of operations, each a named failure:</para>
/// <list type="number">
/// <item><c>INVALID_CONFIG</c> — the config JSON is missing, unreadable or fails <see cref="TimelineRenderStepConfig.Validate"/>.</item>
/// <item><c>TIMELINE_NOT_FOUND</c> — no <c>edit_timelines</c> row with that id IN THIS PROJECT (a foreign timeline is indistinguishable from a missing one, on purpose).</item>
/// <item><c>REVISION_NOT_RESTORABLE</c> — a pinned revision that is neither current nor a stored snapshot. The revision actually rendered is always written to the output.</item>
/// <item><c>DOCUMENT_INVALID</c> — the stored document fails <c>EditTimelineValidator</c> when re-validated against the project's own files and step results. The row is never trusted. Also a document that asks for more than one render carries (more than 8 keyframed gains in the mix): refused before any ffmpeg call, with a plain sentence.</item>
/// <item><c>ASSET_UNRESOLVED</c> / <c>ASSET_OUTSIDE_PROJECT</c> — an <c>AssetRef</c> that does not resolve to an object under <c>projects/{projectId}/</c>.</item>
/// <item><c>RENDER_FAILED</c> — ffmpeg failed; the stderr tail is in the error.</item>
/// </list>
/// <para>
/// The render itself is <see cref="TimelineRenderer"/> over local files; this class owns the step
/// contract, the database reads, the asset downloads and the uploads: the delivered file (an
/// <c>outputFiles</c> project file when <c>RegisterProjectFile</c>, else an execution-scoped
/// <c>outputFiles</c> artifact) goes to <c>OutputStorageKey</c>, a v1 <c>TimelineManifest</c> of the
/// result to <c>TimelineStorageKey</c> (so a render reopens in the v1 viewer and can seed a new v2
/// document exactly like compile output), and a JSON render report to <c>ArtifactStorageKey</c>,
/// all three under the same conventions the compile step uses.
/// </para>
/// <para>
/// Registered as a singleton like every executor; it opens a DI scope for the
/// <see cref="WorkflowEngineDbContext"/> reads it needs, exactly as the compile executor does.
/// </para>
/// </remarks>
public sealed class TimelineRenderStepExecutor : IStepExecutor
{
    public static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions DocumentJsonOptions = EditTimelineJson.Options;
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IVideoToolRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly IProjectFileWorkspace _workspace;
    private readonly VideoEditingOptions _options;
    private readonly IScratchDiskGuard? _scratchGuard;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TimelineRenderStepExecutor> _logger;
    /// <summary>T-006: optional so existing tests/constructor calls compile; DI supplies the gate.</summary>
    private readonly ReelBolt.Shared.Moderation.IContentModerationGate? _moderation;
    private readonly int _moderationMaxChars;

    public TimelineRenderStepExecutor(
        IVideoToolRunner runner,
        IMediaProbe probe,
        IProjectFileWorkspace workspace,
        IOptions<VideoEditingOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<TimelineRenderStepExecutor> logger,
        ReelBolt.Shared.Moderation.IContentModerationGate? moderation = null,
        IOptions<ReelBolt.Shared.Moderation.ContentModerationOptions>? moderationOptions = null,
        IScratchDiskGuard? scratchGuard = null)
    {
        _scratchGuard = scratchGuard;
        _runner = runner;
        _probe = probe;
        _workspace = workspace;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _moderation = moderation;
        _moderationMaxChars = moderationOptions?.Value.MaxTextChars ?? 8000;
    }

    public StepType StepType => StepType.TimelineRender;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        Stopwatch sw = Stopwatch.StartNew();
        WorkflowStep step = context.Step;
        Guid projectId = context.Execution.ProjectId;
        CancellationToken ct = context.CancellationToken;
        VideoScratchSpace? scratch = null;

        try
        {
            // 1. Config
            TimelineRenderStepConfig? config = null;
            List<string> configErrors = [];
            if (string.IsNullOrWhiteSpace(step.TimelineRenderConfigJson))
            {
                configErrors.Add("TimelineRenderConfigJson is required.");
            }
            else
            {
                try
                {
                    config = JsonSerializer.Deserialize<TimelineRenderStepConfig>(step.TimelineRenderConfigJson, ConfigJsonOptions);
                    if (config is null) configErrors.Add("TimelineRenderConfigJson must be a JSON object.");
                    else configErrors.AddRange(config.Validate());
                }
                catch (JsonException ex)
                {
                    configErrors.Add($"TimelineRenderConfigJson is not valid JSON: {ex.Message}");
                }
            }

            if (config is null || configErrors.Count > 0)
                return Fail(context, sw, "INVALID_CONFIG", string.Join(" ", configErrors));

            await context.ReportProgressAsync("Loading timeline", 1);

            // 2-4. Document at the pinned revision, re-validated.
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            EditTimelineEntity? timeline = await db.EditTimelines.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == config.EditTimelineId && t.ProjectId == projectId, ct);
            if (timeline is null)
                return Fail(context, sw, "TIMELINE_NOT_FOUND", $"Edit timeline {config.EditTimelineId} does not exist in project {projectId}.");

            int revision = config.Revision ?? timeline.Revision;
            string? documentJson = revision == timeline.Revision
                ? timeline.DocumentJson
                : await db.EditTimelineSnapshots.AsNoTracking()
                    .Where(s => s.TimelineId == timeline.Id && s.Revision == revision)
                    .Select(s => s.DocumentJson)
                    .FirstOrDefaultAsync(ct);
            if (documentJson is null)
                return Fail(context, sw, "REVISION_NOT_RESTORABLE", $"Revision {revision} of timeline {timeline.Id} has no stored document (current is {timeline.Revision}).", revision);

            EditTimeline? document;
            try
            {
                document = JsonSerializer.Deserialize<EditTimeline>(documentJson, DocumentJsonOptions);
            }
            catch (JsonException ex)
            {
                return Fail(context, sw, "DOCUMENT_UNREADABLE", ex.Message, revision);
            }

            if (document is null)
                return Fail(context, sw, "DOCUMENT_UNREADABLE", "The stored document is null.", revision);

            EditTimelineValidationContext validationContext = await BuildValidationContextAsync(db, projectId, document, ct);
            ValidationResult validation = EditTimelineValidator.Validate(document, validationContext);
            if (!validation.IsValid)
            {
                string violations = string.Join("; ", validation.Violations.Select(v => $"{v.Path}: {v.Message}"));
                // A deleted project file / pruned step result is simply absent from the ownership maps; when
                // that is the ONLY thing wrong the user's fix is "put the file back", not a structural error.
                if (validation.Violations.All(v => EditTimelineValidator.IsNotFoundInProject(v.Message)))
                    return Fail(context, sw, "ASSET_UNRESOLVED", violations, revision);
                return Fail(context, sw, "DOCUMENT_INVALID", violations, revision);
            }

            // P8: everything from here on works on the FLATTENED document — compound clips expanded
            // into their sequences' tracks, multicam clips into one media clip per angle run — so
            // the staged assets, the render and the manifest all describe what actually plays.
            // Validation above ran on the document as stored (nesting rules included).
            document = TimelineFlattener.Flatten(document);

            // 5. Assets: resolved server-side from this project's rows, prefix-checked, downloaded once.
            scratch = VideoScratchSpace.Create(_options, context.Execution.Id, step.Id, _logger);
            Dictionary<AssetRef, LocalAsset> assets = [];
            Dictionary<AssetRef, string> storageKeys = [];
            // P7: LUT files referenced by clip grades, colour-group grades and adjustment clips are
            // staged like any other asset (same ownership and prefix checks), but not probed.
            HashSet<AssetRef> lutReferences = TimelineAssetReferences.LutFiles(document).ToHashSet();
            // Media clips AND insert clips' assets (an insert is drawn into its quad), through the same ownership and prefix checks.
            List<AssetRef> references = TimelineAssetReferences.MediaSources(document)
                .Concat(lutReferences)
                .Distinct()
                .ToList();

            string projectPrefix = $"projects/{projectId:D}/";
            int assetIndex = 0;
            using ScratchLeases scratchLeases = new();
            using ScopeSet timeoutScopes = new();
            foreach (AssetRef reference in references)
            {
                ct.ThrowIfCancellationRequested();
                (string? storageKey, string? fingerprint) = await ResolveStorageKeyAsync(db, projectId, reference, ct);
                if (storageKey is null)
                    return Fail(context, sw, "ASSET_UNRESOLVED", $"Asset {Describe(reference)} does not resolve to a file or render in this project.", revision);
                if (!storageKey.StartsWith(projectPrefix, StringComparison.Ordinal))
                    return Fail(context, sw, "ASSET_OUTSIDE_PROJECT", $"Asset {Describe(reference)} resolves outside this project's storage prefix.", revision);

                await context.ReportProgressAsync($"Downloading source {assetIndex + 1}/{references.Count}", 2);
                // A LUT is read by the grade filter, which goes by its extension, and never as an ffmpeg input. Media is
                // staged under a media extension or the neutral one, never a playlist or list extension, and a file named
                // as one is refused before it is downloaded: it is not media, it names other files (security ENG-3).
                bool isLut = lutReferences.Contains(reference);
                if (!isLut && UserMediaInputFormats.IsListFile(storageKey))
                    return Fail(context, sw, UserMediaInputFormats.RefusedCode, UserMediaInputFormats.RefusedMessage, revision);
                string extension;
                if (isLut)
                {
                    extension = Path.GetExtension(storageKey);
                    if (string.IsNullOrEmpty(extension) || extension.Length > 8) extension = ".bin";
                }
                else
                {
                    extension = UserMediaInputFormats.SafeExtension(storageKey, ".bin");
                }

                string localPath = scratch.GetPath($"asset-{assetIndex:D3}{extension}");
                long assetBytes;
                try
                {
                    assetBytes = await scratchLeases.ReserveAsync(_scratchGuard, _workspace, projectId, storageKey, ct);
                }
                catch (SourceTooLargeException)
                {
                    return Fail(context, sw, ReelBolt.Shared.Workflows.VideoSourceLimits.TooLargeCode, "A source is larger than the platform can edit.", revision);
                }

                await _workspace.DownloadStorageKeyToFileAsync(projectId, storageKey, localPath, ct);
                if (lutReferences.Contains(reference))
                {
                    assets[reference] = new LocalAsset(localPath, $"{storageKey}|{fingerprint}", new MediaProbeResult(0, 0, 1, 0, 0, null, null, null));
                    storageKeys[reference] = storageKey;
                    assetIndex++;
                    continue;
                }

                MediaProbeResult probe;
                try
                {
                    probe = await _probe.ProbeAsync(localPath, ct);
                }
                catch (MediaFormatNotAllowedException refusal)
                {
                    // The probe refused the format (a playlist or list file under any name): the author's input.
                    return Fail(context, sw, UserMediaInputFormats.RefusedCode, UserMediaInputFormats.MessageFor(refusal), revision);
                }

                if (_scratchGuard is not null && probe is not null && probe.DurationSec > _scratchGuard.MaxSourceSeconds)
                    return Fail(context, sw, ReelBolt.Shared.Workflows.VideoSourceLimits.TooLongCode, "A source is longer than the platform can edit.", revision);
                if (probe is not null)
                    timeoutScopes.Add(SourceTimeoutPolicy.Begin(
                    probe.DurationSec, probe.Width, probe.Height, assetBytes, _scratchGuard?.MaxSourceSeconds ?? ReelBolt.Shared.Workflows.VideoSourceLimits.DefaultMaxSeconds,
                    SourceTimeoutPolicy.FpsOf(probe.FpsNum, probe.FpsDen)));
                assets[reference] = new LocalAsset(localPath, $"{storageKey}|{fingerprint}", probe);
                storageKeys[reference] = storageKey;
                assetIndex++;
            }

            // 5b. Segmentation masks (T-015): every mask a clip names is downloaded through the mask store's
            // scope, re-hashed against the analysis artifact and size-checked against its source. One that
            // cannot be verified refuses the render - never an unmasked picture.
            TimelineSegmentationMasks.Resolution segmentation = await TimelineSegmentationMasks.ResolveAsync(
                document, projectId, assets,
                (ids, token) => ReadAnalysisArtifactsAsync(db, projectId, ids, token),
                async (key, token) =>
                {
                    string artifactLocal = scratch.GetPath($"analysis-{Guid.NewGuid():N}.json");
                    await _workspace.DownloadStorageKeyToFileAsync(projectId, key, artifactLocal, SegmentationTrackIdLookup.MaxArtifactBytes, token);
                    await using FileStream stream = File.OpenRead(artifactLocal);
                    return await SegmentationTrackIdLookup.ParseAsync(stream, token);
                },
                (key, local, token) => _workspace.DownloadStorageKeyToFileAsync(projectId, key, local, token),
                (local, token) => _probe.ProbeAsync(local, token),
                n => scratch.GetPath($"segmentation-{n:D2}.mkv"),
                ct);
            if (segmentation.Refusal is { } maskRefusal)
                return Fail(context, sw, "DOCUMENT_INVALID", maskRefusal, revision);

            // 6. Output cap: a picture whose short side passes the cap is clamped to it
            // (aspect-preserving) by rewriting the config's encode size HERE, before either Resolve
            // call (this one and the renderer's), so the region hashes and the encode agree. An
            // editor export caps at the plan; a step of an ordinary run at the run's own rule
            // (OutputResolution.EffectiveMaxHeight, like VideoCompile and Remotion).
            int? planMaxOutputHeight = await ResolveOutputCapAsync(context, db, ct);
            bool planCapApplied = false;
            if (planMaxOutputHeight is { } cap)
                config = TimelineRenderPresets.CapToShortSide(config, document.Settings, cap, out planCapApplied);

            // 7. Render.
            ResolvedRenderTarget target = TimelineRenderPresets.Resolve(config, document.Settings);
            string outputFileName = Path.GetFileNameWithoutExtension(config.OutputFileName) + target.Extension;
            string outputPath = scratch.GetPath("delivery" + target.Extension);
            TimelineRenderEnvironment env = new(
                Runner: _runner,
                ScratchDirectory: scratch.GetPath("render"),
                FontFilePath: _options.FontFilePath,
                Timeout: TimeSpan.FromSeconds(Math.Max(30, SourceTimeoutPolicy.CompileSeconds(_options))),
                Threads: Math.Max(1, _options.FfmpegThreads),
                Cache: new ObjectStoreRegionCache(_workspace),
                ProjectId: projectId,
                Progress: (stage, percent) => context.ReportProgressAsync(stage, percent),
                Moderation: new ReelBolt.Shared.Moderation.ModerationAttribution(
                    context.Execution.OrganizationId,
                    context.Execution.ProjectId,
                    context.Execution.InitiatedByUserId,
                    context.Execution.Id,
                    context.StepResultId,
                    ReelBolt.Shared.Moderation.ModerationSurface.GeneratedText,
                    ReelBolt.Shared.Moderation.ModerationContentKind.Text),
                SegmentationMasks: segmentation.Masks.Count == 0 ? null : segmentation.Masks);

            TimelineRenderResult result;
            try
            {
                result = await new TimelineRenderer(_logger, _moderation, _moderationMaxChars).RenderAsync(document, config, assets, outputPath, env, ct);
            }
            catch (TimelineContentBlockedException ex)
            {
                return Fail(context, sw, "CONTENT_BLOCKED", ex.Message, revision);
            }
            catch (RenderLutException ex)
            {
                return FailLut(context, sw, ex, revision);
            }
            catch (TimelineRenderLimitException ex)
            {
                return Fail(context, sw, "DOCUMENT_INVALID", ex.Message, revision);
            }
            catch (TimelineRenderException ex)
            {
                return Fail(context, sw, "RENDER_FAILED", ex.Message, revision);
            }

            // 8. Upload the delivery, the manifest and the report.
            await context.ReportProgressAsync("Uploading render", 96);
            string outputStorageKey;
            if (config.RegisterProjectFile)
            {
                ProjectWorkspaceFile uploaded = await _workspace.UploadBinaryFileAsync(
                    projectId, outputPath, outputFileName, target.MimeType,
                    SummaryStatus.Done, FileIndexingStatus.NotIndexed, ct, category: "outputFiles");
                outputStorageKey = uploaded.StorageKey;
            }
            else
            {
                outputStorageKey = await _workspace.UploadArtifactAsync(
                    projectId, outputPath, $"{context.Execution.Id:D}/{outputFileName}", target.MimeType, ct, category: "outputFiles");
            }

            double fps = target.Fps;
            TimelineManifest manifest = BuildManifest(document, result, assets, storageKeys, outputStorageKey, projectId, context.Execution.Id, fps);
            string manifestLocal = scratch.GetPath("timeline.json");
            await File.WriteAllTextAsync(manifestLocal, JsonSerializer.Serialize(manifest, OutputJsonOptions), ct);
            string timelineStorageKey = await _workspace.UploadArtifactAsync(
                projectId, manifestLocal, $"video-analysis/{context.Execution.Id:D}/step-{step.StepOrder}-timeline.json", "application/json", ct);

            JsonObject report = BuildOutput(config, timeline.Id, revision, target, result, outputStorageKey, timelineStorageKey, artifactStorageKey: null, fps);
            if (config.WriteCaptionSidecars)
            {
                // R09.7: the SRT/VTT cue list next to the film (artifacts under the run, named like the film).
                IReadOnlyList<CaptionCue> cues = CaptionSidecar.Cues(ReelBolt.Shared.Editing.TimelineFlattener.Flatten(document), config.Range);
                string stem = Path.GetFileNameWithoutExtension(outputFileName);
                string srtLocal = scratch.GetPath("captions.srt");
                string vttLocal = scratch.GetPath("captions.vtt");
                await File.WriteAllTextAsync(srtLocal, CaptionSidecar.Srt(cues), ct);
                await File.WriteAllTextAsync(vttLocal, CaptionSidecar.Vtt(cues), ct);
                async Task<string> UploadSidecar(string local, string name, string mime)
                {
                    // Registered as project files like the film, so the user downloads them through the files path.
                    if (config.RegisterProjectFile)
                    {
                        ProjectWorkspaceFile f = await _workspace.UploadBinaryFileAsync(
                            projectId, local, name, mime, SummaryStatus.Done, FileIndexingStatus.NotIndexed, ct, category: "outputFiles");
                        return f.StorageKey;
                    }

                    return await _workspace.UploadArtifactAsync(projectId, local, $"{context.Execution.Id:D}/{name}", mime, ct, category: "outputFiles");
                }

                string srtKey = await UploadSidecar(srtLocal, stem + ".srt", "application/x-subrip");
                string vttKey = await UploadSidecar(vttLocal, stem + ".vtt", "text/vtt");
                report["captionSidecars"] = new JsonObject { ["srtStorageKey"] = srtKey, ["vttStorageKey"] = vttKey, ["cueCount"] = cues.Count };
            }

            if (planMaxOutputHeight is { } recordedCap)
                report["planCap"] = new JsonObject { ["maxOutputHeight"] = recordedCap, ["applied"] = planCapApplied };
            if (OutputResolutionReport.ForTimelineRender(RunStyleOverrides.OutputResolutionOf(context.Execution), target.Width, target.Height, planCapApplied) is { } resolutionNode)
                report["outputResolution"] = resolutionNode;
            string reportLocal = scratch.GetPath("render-report.json");
            await File.WriteAllTextAsync(reportLocal, report.ToJsonString(OutputJsonOptions), ct);
            string artifactStorageKey = await _workspace.UploadArtifactAsync(
                projectId, reportLocal, $"video-analysis/{context.Execution.Id:D}/step-{step.StepOrder}-render.json", "application/json", ct);
            report["artifactStorageKey"] = artifactStorageKey;

            await context.ReportProgressAsync("Done", 100);
            sw.Stop();
            return new StepExecutionResult
            {
                Output = report.ToJsonString(OutputJsonOptions),
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed,
                DurationMs = sw.ElapsedMilliseconds,
                OutputStorageKey = outputStorageKey,
                ArtifactStorageKey = artifactStorageKey,
                TimelineStorageKey = timelineStorageKey
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TimelineRender step {StepOrder} failed unexpectedly.", step.StepOrder);
            return Fail(context, sw, "UNEXPECTED", ex.Message);
        }
        finally
        {
            scratch?.Dispose();
        }
    }

    /// <summary>
    /// The tallest short side this step may render, read the way
    /// <c>VideoCompileStepExecutor.ResolveOutputPolicyAsync</c> reads it.
    /// <list type="bullet">
    /// <item>An <b>editor export</b> — the hidden one-step workflow
    /// (<see cref="WorkflowDefinitionKind.TimelineRender"/>) <c>POST …/edit-timelines/{id}/render</c>
    /// creates — is capped at the plan's raw <see cref="OrganizationEntitlementSet.MaxOutputHeight"/>:
    /// its size is the export the person picked, not a run's Auto choice.</item>
    /// <item>A step of an <b>ordinary workflow run</b> is capped by
    /// <see cref="RunStyleOverrides.OutputCap"/> (<c>OutputResolution.EffectiveMaxHeight</c>): under
    /// Auto the plan but never above 1080, with a choice the choice clamped to the plan — the same rule
    /// as VideoCompile and Remotion, so a 4K timeline in an Auto run does not out-render its run.</item>
    /// </list>
    /// Null — no cap, render as asked — for a run with no organization (<see cref="Guid.Empty"/>,
    /// self-host), an unlimited cap, a scope without an entitlement service, or a lookup that throws.
    /// </summary>
    private async Task<int?> ResolveOutputCapAsync(StepExecutionContext context, WorkflowEngineDbContext db, CancellationToken ct)
    {
        try
        {
            Guid organizationId = context.Execution.OrganizationId;
            if (organizationId == Guid.Empty) return null;

            IServiceScope? scope = _scopeFactory.CreateScope();
            if (scope is null) return null;

            OrganizationEntitlementSet set;
            using (scope)
            {
                IOrganizationEntitlements? entitlements = scope.ServiceProvider.GetService<IOrganizationEntitlements>();
                if (entitlements is null) return null;
                set = await entitlements.GetAsync(organizationId, ct);
            }

            WorkflowDefinitionKind? kind = context.Execution.WorkflowDefinition?.Kind
                ?? await db.WorkflowDefinitions.AsNoTracking()
                    .Where(w => w.Id == context.Execution.WorkflowDefinitionId)
                    .Select(w => (WorkflowDefinitionKind?)w.Kind)
                    .FirstOrDefaultAsync(ct);

            int cap = kind == WorkflowDefinitionKind.TimelineRender
                ? set.MaxOutputHeight
                : RunStyleOverrides.OutputCap(set, context.Execution);

            return cap == OrganizationEntitlementSet.UnlimitedInt ? null : cap;
        }
        catch (Exception ex)
        {
            // Fail open, deliberately and consistently with the other output policies (VideoCompile's
            // ResolveOutputPolicyAsync, the Remotion render): a lookup failure renders UNCAPPED rather
            // than failing the render. Nothing upstream bounds that size — Master and shape presets,
            // and the TimelineRender config of a workflow step, are not API-gated (only an explicit
            // 4K preset or encode size on the editor's render endpoint is refused before queueing) —
            // so the failure is logged as a warning for operators to see. Cancellation is still
            // honoured by the next awaited call, which takes the same token.
            _logger.LogWarning(
                ex, "TimelineRender step {StepOrder}: the plan output cap could not be resolved; rendering without it",
                context.Step.StepOrder);
            return null;
        }
    }

    /// <summary>Codes that are the same on every retry (a bad request or document, not a flaky host): never retried.</summary>
    public static bool IsDeterministicUserCode(string code) =>
        code is "INVALID_CONFIG" or "TIMELINE_NOT_FOUND" or "REVISION_NOT_RESTORABLE" or "DOCUMENT_INVALID" or "ASSET_UNRESOLVED"
            or "ASSET_OUTSIDE_PROJECT" or "CONTENT_BLOCKED" or "LUT_INVALID" or "LUT_UNSUPPORTED" or UserMediaInputFormats.RefusedCode
            or ReelBolt.Shared.Workflows.VideoSourceLimits.TooLargeCode or ReelBolt.Shared.Workflows.VideoSourceLimits.TooLongCode;

    private static StepExecutionResult FailLut(StepExecutionContext context, Stopwatch sw, RenderLutException ex, int? revision)
    {
        CubeLutException reason = ex.Reason;
        string code = reason.IsUnsupported ? "LUT_UNSUPPORTED" : "LUT_INVALID";
        JsonObject lut = new() { ["reason"] = reason.Code };
        Guid? fileId = ex.File.ProjectFileId;
        if (fileId is { } id) lut["fileId"] = id.ToString("D");
        if (reason.Expected is { } expected) lut["expected"] = expected;
        if (reason.Found is { } found) lut["found"] = found;
        if (reason.Keyword is { } keyword) lut["keyword"] = keyword;
        // The reason code and its numbers ride in the message too ("(wrong_row_count, expected 35937, found 1000)"):
        // the render queue only sees the error text, and the web maps that token to the localised sentence.
        System.Text.StringBuilder detail = new(reason.Code);
        if (reason.Expected is { } e) detail.Append(", expected ").Append(e.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (reason.Found is { } f) detail.Append(", found ").Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (reason.Keyword is { } k) detail.Append(", keyword ").Append(k);
        return Fail(context, sw, code, $"The LUT file cannot be used ({detail}): {reason.Message}.", revision, lut);
    }

    private static StepExecutionResult Fail(StepExecutionContext context, Stopwatch sw, string code, string message, int? revision = null, JsonObject? lut = null)
    {
        sw.Stop();
        JsonObject output = new()
        {
            ["status"] = "failed",
            ["error"] = code,
            ["message"] = message
        };
        if (revision is { } r) output["revision"] = r;
        if (lut is not null) output["lut"] = lut;
        return new StepExecutionResult
        {
            Output = output.ToJsonString(OutputJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Failed,
            ErrorDetails = $"{code}: {message}",
            DurationMs = sw.ElapsedMilliseconds,
            Retryable = !IsDeterministicUserCode(code)
        };
    }

    private async Task<EditTimelineValidationContext> BuildValidationContextAsync(
        WorkflowEngineDbContext db, Guid projectId, EditTimeline document, CancellationToken ct)
    {
        HashSet<Guid> fileIds = [];
        HashSet<Guid> stepResultIds = [];
        // P8: root tracks, every sequence's tracks and every multicam angle — what the flattened
        // document will read — plus LUT files, so ownership is proven for all of them.
        foreach (AssetRef? reference in TimelineAssetReferences.MediaSources(document).Concat(TimelineAssetReferences.LutFiles(document)))
        {
            if (reference?.ProjectFileId is { } fileId) fileIds.Add(fileId);
            if (reference?.StepResultId is { } stepResultId) stepResultIds.Add(stepResultId);
        }

        // Provenance step results resolve through the same map (the validator checks them), exactly
        // as the API's PATCH validation does; without them a timeline seeded from a run never renders.
        stepResultIds.UnionWith(TimelineAssetReferences.ProvenanceStepResultIds(document));

        Dictionary<Guid, Guid> fileOwners = fileIds.Count == 0
            ? []
            : await db.ProjectFiles.AsNoTracking()
                .Where(f => fileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.ProjectId })
                .ToDictionaryAsync(f => f.Id, f => f.ProjectId, ct);

        Dictionary<Guid, Guid> stepResultOwners = stepResultIds.Count == 0
            ? []
            : await db.WorkflowStepResults.AsNoTracking()
                .Where(r => stepResultIds.Contains(r.Id))
                .Select(r => new { r.Id, r.WorkflowExecution.ProjectId })
                .ToDictionaryAsync(r => r.Id, r => r.ProjectId, ct);

        // Durations deliberately empty: the probe below is the authority on length, and the
        // validator treats a missing duration as unvalidated, not as a violation.
        // Segmentation masks: the same shared lookup the API's PATCH validation uses, so both see the same map.
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> segmentationTracks = await SegmentationTrackIdLookup.ResolveAsync(
            document,
            projectId,
            async (ids, token) => (await db.WorkflowStepResults.AsNoTracking()
                .Where(r => ids.Contains(r.Id)
                    && r.WorkflowExecution.ProjectId == projectId
                    && r.WorkflowStep.StepType == StepType.VideoAnalyze)
                .Select(r => new { r.Id, r.ArtifactStorageKey })
                .ToListAsync(token)).Select(r => (r.Id, r.ArtifactStorageKey)).ToList(),
            async (key, token) =>
            {
                string local = Path.Combine(Path.GetTempPath(), $"seg-{Guid.NewGuid():N}.json");
                try
                {
                    await _workspace.DownloadStorageKeyToFileAsync(projectId, key, local, SegmentationTrackIdLookup.MaxArtifactBytes, token);
                    await using FileStream stream = File.OpenRead(local);
                    return await SegmentationTrackIdLookup.ParseAsync(stream, token);
                }
                finally
                {
                    try { File.Delete(local); } catch (IOException) { }
                }
            },
            ct);

        return new EditTimelineValidationContext(projectId, new Dictionary<AssetRef, long>(), fileOwners, stepResultOwners, SegmentationTrackIds: segmentationTracks);
    }

    /// <summary>
    /// The analysis artifacts a mask names, by result id. A named analysis artifact is a stored result like any other:
    /// one that may predate the project's quarantine marker (or was superseded since) is left out, which refuses the
    /// render as an unverifiable mask. Only a VideoAnalyze result of this project counts.
    /// </summary>
    internal static async Task<IReadOnlyList<(Guid Id, string? ArtifactKey)>> ReadAnalysisArtifactsAsync(
        WorkflowEngineDbContext db, Guid projectId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => ids.Contains(r.Id)
                && r.WorkflowExecution.ProjectId == projectId
                && r.WorkflowStep.StepType == StepType.VideoAnalyze)
            .Select(r => new { r.Id, r.ArtifactStorageKey, r.ExecutedAt, r.SupersededAt, r.WorkflowExecution.FirstStartedAt })
            .ToListAsync(ct);
        List<(Guid Id, string? ArtifactKey)> usable = [];
        foreach (var r in rows)
        {
            if (!await ProjectQuarantineState.ResultPredatesAsync(db, projectId, r.ExecutedAt, r.FirstStartedAt, ct, r.SupersededAt))
                usable.Add((r.Id, r.ArtifactStorageKey));
        }

        return usable;
    }

    internal static async Task<(string? StorageKey, string? Fingerprint)> ResolveStorageKeyAsync(
        WorkflowEngineDbContext db, Guid projectId, AssetRef reference, CancellationToken ct)
    {
        if (reference.Kind == AssetRefKind.ProjectFile && reference.ProjectFileId is { } fileId)
        {
            var file = await db.ProjectFiles.AsNoTracking()
                .Where(f => f.Id == fileId && f.ProjectId == projectId)
                .Select(f => new { f.StorageKey, f.SizeBytes, f.UploadedAt })
                .FirstOrDefaultAsync(ct);
            return file is null ? (null, null) : (file.StorageKey, $"{file.SizeBytes}|{file.UploadedAt.Ticks}");
        }

        if (reference.Kind == AssetRefKind.StepResult && reference.StepResultId is { } stepResultId)
        {
            var result = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null)
                .Select(r => new { r.OutputStorageKey, r.CompletedAt, r.ExecutedAt, r.SupersededAt, r.WorkflowExecution.FirstStartedAt })
                .FirstOrDefaultAsync(ct);
            if (result?.OutputStorageKey is not null && await ProjectQuarantineState.ResultPredatesAsync(db, projectId, result.ExecutedAt, result.FirstStartedAt, ct, result.SupersededAt))
                return (null, null);
            return result?.OutputStorageKey is null
                ? (null, null)
                : (result.OutputStorageKey, $"{(result.CompletedAt ?? result.ExecutedAt).Ticks}");
        }

        return (null, null);
    }

    private static string Describe(AssetRef reference) =>
        $"{reference.Kind}:{reference.ProjectFileId ?? reference.StepResultId}";

    private static TimelineManifest BuildManifest(
        EditTimeline document,
        TimelineRenderResult result,
        IReadOnlyDictionary<AssetRef, LocalAsset> assets,
        IReadOnlyDictionary<AssetRef, string> storageKeys,
        string outputStorageKey,
        Guid projectId,
        Guid executionId,
        double fps)
    {
        // P7: LUT files are staged assets but not sources of the manifest.
        HashSet<AssetRef> lutFiles = TimelineAssetReferences.LutFiles(document).ToHashSet();
        List<AssetRef> order = assets.Keys.Where(k => !lutFiles.Contains(k)).ToList();
        List<TimelineSource> sources = order.Select((reference, index) =>
        {
            LocalAsset asset = assets[reference];
            return new TimelineSource(index, storageKeys[reference], asset.Probe.DurationSec, asset.Probe.Width, asset.Probe.Height,
                asset.Probe.FpsDen > 0 ? asset.Probe.FpsNum / (double)asset.Probe.FpsDen : fps,
                ProjectFileId: reference.Kind == AssetRefKind.ProjectFile ? reference.ProjectFileId : null,
                StepResultId: reference.Kind == AssetRefKind.StepResult ? reference.StepResultId : null);
        }).ToList();

        long rangeStart = result.Plan.RangeStartFrame;
        List<TimelineTrack> tracks = [];
        int trackIndex = 0;
        foreach (Track track in document.Tracks)
        {
            string type = track.Kind == TrackKind.Video ? "video" : "music";
            List<TimelineItem> items = [];
            foreach (Item item in track.Items)
            {
                if (item.RecordOut <= result.Plan.RangeStartFrame || item.RecordIn >= result.Plan.RangeEndFrame) continue;
                double start = (Math.Max(item.RecordIn, result.Plan.RangeStartFrame) - rangeStart) / fps;
                double end = (Math.Min(item.RecordOut, result.Plan.RangeEndFrame) - rangeStart) / fps;
                switch (item)
                {
                    case MediaClip clip when track.Kind == TrackKind.Video:
                        {
                            double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
                            double sourceStart = (clip.SourceIn + (Math.Max(item.RecordIn, rangeStart) - clip.RecordIn) * rate) / fps;
                            items.Add(new TimelineItem(
                                Id: clip.Id.ToString("D"), TimelineStartSec: start, TimelineEndSec: end,
                                SourceIndex: order.IndexOf(clip.AssetRef), SourceStartSec: sourceStart,
                                SourceEndSec: sourceStart + (end - start) * rate, Label: clip.Name,
                                AgentRationale: clip.Provenance?.Rationale, AgentName: clip.Provenance?.AgentName,
                                ProviderName: clip.Provenance?.ProviderName));
                            break;
                        }
                    case MediaClip bed:
                        items.Add(new TimelineItem(
                            Id: bed.Id.ToString("D"), TimelineStartSec: start, TimelineEndSec: end,
                            TrackId: track.Id.ToString("D"), TrackName: bed.Name, BedDbfs: bed.GainDb?.Value,
                            SourceIndex: order.IndexOf(bed.AssetRef), SourceStartSec: bed.SourceIn / fps));
                        break;
                    case TitleClip title:
                        items.Add(new TimelineItem(Id: title.Id.ToString("D"), TimelineStartSec: start, TimelineEndSec: end, Text: title.Text, Style: "title", Label: title.Name));
                        break;
                    case Caption caption:
                        items.Add(new TimelineItem(Id: caption.Id.ToString("D"), TimelineStartSec: start, TimelineEndSec: end, Text: caption.Text, Style: "caption", Label: caption.Name));
                        break;
                }
            }

            tracks.Add(new TimelineTrack(track.Id.ToString("D"), type, track.Name, trackIndex, items));
            trackIndex++;
        }

        return new TimelineManifest(
            Version: 1,
            ProjectId: projectId,
            ExecutionId: executionId,
            OutputVideo: new TimelineOutputVideo(result.Target.Width, result.Target.Height, fps, result.Plan.DurationFrames / fps, outputStorageKey),
            Sources: sources,
            Tracks: tracks);
    }

    internal static JsonObject BuildOutput(
        TimelineRenderStepConfig config, Guid timelineId, int revision, ResolvedRenderTarget target,
        TimelineRenderResult result, string outputStorageKey, string timelineStorageKey, string? artifactStorageKey, double fps)
    {
        JsonArray regions = [];
        foreach (RenderedRegion region in result.Regions)
        {
            regions.Add(new JsonObject
            {
                ["startFrame"] = region.StartFrame,
                ["endFrame"] = region.EndFrame,
                ["cached"] = region.FromCache,
                ["hash"] = region.RecipeHash,
                ["layers"] = region.LayerCount
            });
        }

        JsonArray skipped = [];
        foreach (SkippedItem item in result.Plan.SkippedItems)
            skipped.Add(new JsonObject { ["itemId"] = item.ItemId, ["type"] = item.Type, ["reason"] = item.Reason, ["message"] = item.Message });

        // Notes that are NOT about one item. A skipped item is reported once, in skippedItems (with
        // its plain message); copying it here showed every skipped sentence twice in Deliver (C-328-1).
        JsonArray warnings = [];
        // warnings[] stays an array of plain sentences (the Outputs surface reads strings); the stable codes sit beside it.
        JsonArray warningCodes = [];
        foreach (RenderWarning warning in result.Warnings ?? [])
        {
            warnings.Add(warning.Message);
            warningCodes.Add(warning.Code);
        }

        // R10.7: sources converted from HDR to SDR before grading (clip names and kind only).
        JsonArray hdrToneMapped = [];
        foreach (HdrToneMapped mapped in result.HdrToneMapped ?? [])
            hdrToneMapped.Add(new JsonObject { ["name"] = mapped.Name, ["kind"] = mapped.Kind });

        return new JsonObject
        {
            ["status"] = "completed",
            ["outputStorageKey"] = outputStorageKey,
            ["timelineStorageKey"] = timelineStorageKey,
            ["artifactStorageKey"] = artifactStorageKey,
            ["timelineId"] = timelineId,
            ["revision"] = revision,
            ["preset"] = config.Preset.ToString(),
            ["width"] = target.Width,
            ["height"] = target.Height,
            ["fps"] = fps,
            ["videoEnabled"] = target.VideoEnabled,
            ["rangeStartFrame"] = result.Plan.RangeStartFrame,
            ["rangeEndFrame"] = result.Plan.RangeEndFrame,
            ["durationFrames"] = result.Plan.DurationFrames,
            ["durationSec"] = result.Plan.DurationFrames / fps,
            ["regions"] = new JsonObject
            {
                ["total"] = result.Regions.Count,
                ["cached"] = result.CachedRegionCount,
                ["rendered"] = result.Regions.Count - result.CachedRegionCount,
                ["list"] = regions
            },
            ["loudness"] = result.MeasuredLoudness is { } m
                ? new JsonObject
                {
                    ["integratedLufs"] = m.InputIntegratedLufs,
                    ["truePeakDbtp"] = m.InputTruePeakDbtp,
                    ["loudnessRange"] = m.InputLoudnessRange,
                    ["normalised"] = result.LoudnessNormalised,
                    ["checks"] = LoudnessChecksNode(result.LoudnessChecks)
                }
                : null,
            ["skippedItems"] = skipped,
            ["warnings"] = warnings,
            ["warningCodes"] = warningCodes,
            ["hdrToneMapped"] = hdrToneMapped,
            ["invocationCount"] = result.Invocations.Count
        };
    }

    /// <summary>The additive <c>loudness.checks</c> array: one entry per finding, empty when clean.</summary>
    internal static JsonArray LoudnessChecksNode(IReadOnlyList<QualityFinding>? findings)
    {
        JsonArray array = [];
        foreach (QualityFinding f in findings ?? [])
        {
            JsonObject o = new() { ["kind"] = f.Kind, ["severity"] = f.Severity };
            if (f.StartSec is { } a) o["startSec"] = a;
            if (f.EndSec is { } b) o["endSec"] = b;
            if (f.Measured is { } m) o["measured"] = m;
            if (f.Limit is { } l) o["limit"] = l;
            o["detail"] = f.Detail;
            array.Add(o);
        }

        return array;
    }
}
