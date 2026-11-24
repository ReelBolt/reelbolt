using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

public partial class ReactRemotionSandboxTools
{
    [Description(
        "Stage generated video clips (background plates) from object storage into the sandbox. Copies the delivered clips of EVERY " +
        "completed VideoGenerate step of THIS execution (not other runs: for a file from an earlier run use StageProjectFile) into the " +
        "sandbox's public/ folder as generated/{clipId}.mp4 and returns, per clip, its path (what staticFile() takes, e.g. " +
        "staticFile('generated/v1.mp4')), purpose and the prompt it was generated from. Optional `clipIds` stages only those clips " +
        "(ids as in a VideoGenerate step's clips, e.g. ['v1','v3t2']); omit it to stage all. If two VideoGenerate steps both made a clip " +
        "with the same id, the later step's keeps generated/{clipId}.mp4 and the earlier one is staged as generated/s{stepOrder}-{clipId}.mp4. " +
        "When nothing can be staged the result is {\"ok\": false, \"error\": ...} naming the steps searched and what was found, never a bare empty list.")]
    public async Task<string> StageGeneratedClips(string[]? clipIds = null)
    {
        WorkflowExecutionContext ctx = RequireContext();
        string executionId = ctx.ExecutionId.ToString();

        _logger.LogInformation(
            "Tool call stage_generated_clips for execution {ExecutionId}",
            executionId);

        var staged = new List<object>();
        var failed = new List<object>();
        var searched = new List<string>();
        var requested = clipIds?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToHashSet(StringComparer.Ordinal);
        var requestedFound = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            // QA-P1-11 (T-113 item 4): a screen-content composition. When a LATER VideoCompile of
            // this run pins THIS step's own render as insert content (insertContentRef from=Step
            // naming it), that compile composites the render — never a generated clip staged here —
            // so staging plates is the footgun this guard exists to stop. The marker is reliable:
            // the compile's config is saved before the run, and the step is the persisted result
            // the agent runs under (WorkflowExecutorService writes the row before the agent starts).
            if (await ScreenContentCompositionRefusalAsync(db, ctx, CancellationToken.None) is { } refusal)
            {
                _logger.LogInformation(
                    "Tool result stage_generated_clips for execution {ExecutionId}: refused (screen-content composition)",
                    executionId);
                return JsonSerializer.Serialize(new { ok = false, error = refusal, staged = new List<object>(), failed = new List<object>() });
            }

            // Every Completed VideoGenerate step result of THIS execution that wrote a manifest — the
            // manifest (ArtifactStorageKey) is the ONLY place a generated clip's storage key is recorded
            // for a consumer; the step's Output JSON deliberately carries none (see VideoGenerateStepExecutor).
            // Taking only the latest step hid every earlier step's clips whenever a later generate step
            // delivered none (all skipped, or a re-plan), which surfaced as a silent empty list.
            var generateResults = await db.WorkflowStepResults
                .Include(r => r.WorkflowStep)
                .Where(r => r.WorkflowExecutionId == ctx.ExecutionId
                    && r.WorkflowStep.StepType == StepType.VideoGenerate
                    && r.Status == StepStatus.Completed
                    && r.SupersededAt == null
                    && r.ArtifactStorageKey != null)
                .OrderBy(r => r.WorkflowStep.StepOrder)
                .ToListAsync(CancellationToken.None);

            string allowedPrefix = $"projects/{ctx.ProjectId}/agentFiles/generated/";
            ISandboxSession? session = null;

            // Clip id -> the highest step order that DELIVERED it, so an earlier step's colliding id gets its own path.
            var latestOwner = new Dictionary<string, int>(StringComparer.Ordinal);
            var manifests = new List<(int Order, JsonElement Clips)>();

            foreach (var generateResult in generateResults)
            {
                int order = generateResult.WorkflowStep.StepOrder;
                JsonElement manifest;
                try
                {
                    var manifestResponse = await _s3Client.GetObjectAsync(_bucketName, generateResult.ArtifactStorageKey!, CancellationToken.None);
                    using var manifestStream = manifestResponse.ResponseStream;
                    manifest = await JsonSerializer.DeserializeAsync<JsonElement>(manifestStream, JsonOptions, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Generated-clip manifest {Key} could not be read for execution {ExecutionId}",
                        generateResult.ArtifactStorageKey, executionId);
                    failed.Add(new { clipId = (string?)null, step = order, reason = "manifest_unreadable" });
                    searched.Add($"step {order} (manifest unreadable)");
                    continue;
                }

                if (manifest.ValueKind != JsonValueKind.Object ||
                    !manifest.TryGetProperty("clips", out JsonElement clips) ||
                    clips.ValueKind != JsonValueKind.Array)
                {
                    failed.Add(new { clipId = (string?)null, step = order, reason = "manifest_invalid" });
                    searched.Add($"step {order} (manifest invalid)");
                    continue;
                }

                manifests.Add((order, clips));
                foreach (JsonElement clip in clips.EnumerateArray())
                {
                    string? id = clip.TryGetProperty("clipId", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
                    string? st = clip.TryGetProperty("status", out var stProp) && stProp.ValueKind == JsonValueKind.String ? stProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id) && string.Equals(st, "succeeded", StringComparison.OrdinalIgnoreCase))
                        latestOwner[id] = order;
                }
            }

            foreach ((int order, JsonElement clips) in manifests)
            {
                int total = 0, notDelivered = 0, rejected = 0, stagedHere = 0;

                foreach (JsonElement clip in clips.EnumerateArray())
                {
                    total++;
                    string? clipId = clip.TryGetProperty("clipId", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
                    string? status = clip.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String ? statusProp.GetString() : null;
                    string? storageKey = clip.TryGetProperty("storageKey", out var keyProp) && keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : null;

                    if (string.IsNullOrWhiteSpace(clipId) || !GeneratedClipIdPattern.IsMatch(clipId))
                    {
                        rejected++;
                        continue;
                    }
                    if (requested is not null && !requested.Contains(clipId))
                        continue;
                    if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(storageKey))
                    {
                        notDelivered++;
                        continue;
                    }
                    requestedFound.Add(clipId);
                    if (!storageKey.StartsWith(allowedPrefix, StringComparison.Ordinal) || storageKey.Contains("..", StringComparison.Ordinal))
                    {
                        failed.Add(new { clipId, step = order, reason = "key_outside_generated_prefix" });
                        continue;
                    }

                    // Under public/, where staticFile() reads; the clip's reported path is the staticFile() one.
                    // The latest step keeps the plain id; an earlier step's colliding id is prefixed so it cannot overwrite.
                    string staticPath = latestOwner[clipId] == order
                        ? $"generated/{clipId}.mp4"
                        : $"generated/s{order}-{clipId}.mp4";
                    string sandboxPath = StaticFileDirectory + staticPath;
                    try
                    {
                        session ??= _sandboxSessions.Create(ctx.ExecutionId);
                        // D8: stage from a presigned GET of the clip the VideoGenerate step uploaded (or,
                        // without a presigner, the engine's own read of it) — the same transport the
                        // voiceover WAVs use, capped like a source clip.
                        // An oversized clip is staged as its delivery copy under the same path (ReactRemotionSandboxTools.VideoProxy.cs).
                        long clipBytes = clip.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out long b) ? b : 0;
                        StagedVideoFacts? clipFacts = await StageVideoAsync(session, ctx, storageKey, clipBytes, sandboxPath);

                        staged.Add(new
                        {
                            video = clipFacts is null ? null : DescribeStagedVideo(clipFacts),
                            clipId,
                            step = order,
                            path = staticPath,
                            purpose = clip.TryGetProperty("purpose", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                            prompt = clip.TryGetProperty("prompt", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null,
                            durationSeconds = clip.TryGetProperty("durationSeconds", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : (double?)null
                        });
                        stagedHere++;
                        _logger.LogInformation(
                            "Staged generated clip {ClipId} for execution {ExecutionId} at {Path}",
                            clipId, executionId, sandboxPath);
                    }
                    catch (Exception ex) when (IsObjectMissing(ex))
                    {
                        _logger.LogWarning(ex,
                            "Generated clip object not found for execution {ExecutionId} clip {ClipId} at key {ObjectKey}",
                            executionId, clipId, storageKey);
                        failed.Add(new { clipId, step = order, reason = "object_not_found" });
                    }
                    catch (SandboxRequestFailedException ex)
                    {
                        _logger.LogWarning(
                            "Failed to stage generated clip {ClipId} for execution {ExecutionId}: {StatusCode}",
                            clipId, executionId, ex.StatusCode);
                        failed.Add(new { clipId, step = order, reason = "sandbox_put_failed" });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error staging generated clip {ClipId} for execution {ExecutionId}",
                            clipId, executionId);
                        failed.Add(new { clipId, step = order, reason = "unexpected_error" });
                    }
                }

                searched.Add($"step {order} ({total} clip(s): {stagedHere} staged, {notDelivered} failed/skipped/pruned" +
                    (rejected > 0 ? $", {rejected} with an unusable id" : "") + ")");
            }

            _logger.LogInformation(
                "Tool result stage_generated_clips for execution {ExecutionId}: staged {Count} clip(s), failed {FailedCount}",
                executionId, staged.Count, failed.Count);

            if (staged.Count > 0)
                return JsonSerializer.Serialize(new { ok = true, staged, failed });

            string where = searched.Count == 0
                ? "this execution has no completed VideoGenerate step with a clip manifest"
                : $"searched this execution's completed VideoGenerate steps: {string.Join("; ", searched)}";
            string missing = requested is { Count: > 0 } && requested.Except(requestedFound).Any()
                ? $" Requested clipIds with no deliverable clip: {string.Join(", ", requested.Except(requestedFound).OrderBy(x => x, StringComparer.Ordinal))}."
                : "";
            string error = $"No generated clip could be staged: {where}.{missing}" +
                (failed.Count > 0 ? $" {failed.Count} clip(s) failed while staging (see `failed`)." : "") +
                " Only clips generated by VideoGenerate steps of this execution are stageable. To use a file from another run or an " +
                "uploaded file, stage it with StageProjectFile (after SaveOutputAsFile for a run output).";
            return JsonSerializer.Serialize(new { ok = false, error, staged, failed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in stage_generated_clips for execution {ExecutionId}", executionId);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "StageGeneratedClips failed unexpectedly; stage a project file with StageProjectFile instead.",
                staged = new List<object>(),
                failed = new List<object>()
            });
        }
    }

    /// <summary>Compile-config JSON options, mirroring <c>VideoCompileStepExecutor.ConfigJsonOptions</c> (enum names, camelCase).</summary>
    private static readonly JsonSerializerOptions CompileConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The refusal message when a LATER <c>VideoCompile</c> of this execution pins THIS step's own
    /// render as insert content (<c>insertContentRef</c> from=Step naming its step order) — the
    /// screen-content composition case (QA-P1-11 / T-113 item 4). Null when no later compile does,
    /// when this scope has no persisted step result to name, or when the execution has no definition
    /// to read. Never throws: a config that will not deserialize simply contributes nothing.
    /// </summary>
    private static async Task<string?> ScreenContentCompositionRefusalAsync(
        WorkflowEngineDbContext db, WorkflowExecutionContext ctx, CancellationToken ct)
    {
        if (ctx.StepResultId is not { } stepResultId)
            return null;

        int? currentOrder = await db.WorkflowStepResults
            .Where(r => r.Id == stepResultId && r.WorkflowExecutionId == ctx.ExecutionId)
            .Select(r => (int?)r.WorkflowStep.StepOrder)
            .FirstOrDefaultAsync(ct);
        if (currentOrder is not { } order)
            return null;

        Guid? definitionId = await db.WorkflowExecutions
            .Where(e => e.Id == ctx.ExecutionId)
            .Select(e => (Guid?)e.WorkflowDefinitionId)
            .FirstOrDefaultAsync(ct);
        if (definitionId is not { } definition)
            return null;

        List<string> configs = await db.WorkflowSteps
            .Where(s => s.WorkflowDefinitionId == definition
                && s.StepType == StepType.VideoCompile
                && s.StepOrder > order
                && s.VideoCompileConfigJson != null)
            .Select(s => s.VideoCompileConfigJson!)
            .ToListAsync(ct);

        foreach (string json in configs)
        {
            VideoCompileStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, CompileConfigJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (config is null)
                continue;

            IEnumerable<InsertContentRef?> refs =
                [config.InsertContentRef, .. (config.InsertContents ?? []).Select(a => a?.InsertContentRef)];
            if (!refs.Any(r => r is { Kind: InsertContentRef.StepKind } && r.StepOrder == order))
                continue;

            return
                $"No generated clip can be staged: this composition's render is pinned as insert content — a later " +
                $"VideoCompile step names step {order}'s output with insertContentRef from=Step. That compile composites the " +
                "RENDER onto the tracked screen, not a generated clip, so anything staged here would never appear. Build the " +
                "composition as the screen content itself; if the render is really meant to be a background plate instead, " +
                "remove that VideoCompile step's insertContentRef (or point it at a different step) and call this tool again.";
        }

        return null;
    }
}
