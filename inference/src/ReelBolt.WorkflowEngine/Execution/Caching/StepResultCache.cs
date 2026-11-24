using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// <inheritdoc cref="IStepResultCache"/>
///
/// <para>
/// <b>The one invariant every method here upholds:</b> a caching failure — a bad DB connection,
/// an S3 hiccup, a serialization edge case — must never fail the workflow step it is trying to
/// speed up. Every public method wraps its entire body in a try/catch that logs a warning and
/// degrades to "no cache" (a miss, or simply not storing). Callers in
/// <see cref="WorkflowExecutorService"/> never need their own defensive try/catch around a call
/// into this class for that reason.
/// </para>
/// <para>
/// <b>Quarantine.</b> A result may depend on a project file that is quarantined later, and neither
/// <see cref="StepCacheKeyInputs.ProjectFileFingerprint"/> (memoized for the run, and it can predate a file the
/// run used) nor the entry itself can prove otherwise. So <see cref="StepCacheKeyInputs.QuarantineState"/> is read
/// fresh here on every lookup and store: a quarantine moves every key of the project, so nothing stored before
/// it can be served after it, and a run that saw the state change stores nothing. Failing to read the state is
/// a miss and no store.
/// </para>
/// </summary>
public sealed class StepResultCache : IStepResultCache
{
    private readonly WorkflowEngineDbContext _db;
    private readonly IStepCacheKeyBuilder _keyBuilder;
    private readonly IStepCachePolicy _policy;
    private readonly StepCacheOptions _options;
    private readonly ILogger<StepResultCache> _logger;
    private readonly IAmazonS3? _s3Client;
    private readonly string _bucketName;

    /// <summary>What this instance (one per run scope) first saw, per project. A failed read stays failed.</summary>
    private readonly Dictionary<Guid, (bool Known, string? State)> _quarantineBaseline = new();

    /// <summary>
    /// <paramref name="policy"/> is NOT invoked by <see cref="TryGetAsync"/>: <see cref="StepCacheKeyInputs"/>
    /// deliberately carries no <see cref="WorkflowStep"/> reference (it must remain a flat, hashable
    /// value), so the policy question "is this STEP cacheable" is answered once, by
    /// <see cref="WorkflowExecutorService"/>, before it ever calls into this class.
    /// <para>
    /// <see cref="StoreAsync"/> DOES consult it — for <see cref="IStepCachePolicy.IsAdmissible"/> only,
    /// which is a question about the RESULT and is therefore answerable here (the executor's own
    /// pre-store check remains the primary gate; this is the same rule enforced at the boundary, so a
    /// future caller cannot store an inadmissible result by forgetting to ask). A D12
    /// <c>FromRunner</c> result is refused here as well as there.
    /// </para>
    /// <paramref name="s3Client"/> is optional: when null (e.g. a unit test harness with no S3
    /// wired), <see cref="StepCacheOptions.ValidateStorageArtifacts"/> is treated as a no-op
    /// rather than throwing — degrading to "trust the row" rather than failing the lookup.
    /// </summary>
    public StepResultCache(
        WorkflowEngineDbContext db,
        IStepCacheKeyBuilder keyBuilder,
        IStepCachePolicy policy,
        IOptions<StepCacheOptions> options,
        ILogger<StepResultCache> logger,
        IAmazonS3? s3Client = null,
        IConfiguration? configuration = null)
    {
        _db = db;
        _keyBuilder = keyBuilder;
        _policy = policy;
        _options = options.Value;
        _logger = logger;
        _s3Client = s3Client;
        _bucketName = configuration?["MinIO:BucketName"] ?? "reelforge";
    }

    public async Task CaptureQuarantineBaselineAsync(Guid projectId, CancellationToken ct)
    {
        if (!_options.Enabled || _quarantineBaseline.ContainsKey(projectId))
            return;

        try
        {
            _quarantineBaseline[projectId] = (true, await ProjectQuarantineState.ReadAsync(_db, projectId, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read project {ProjectId}'s file state at run start; this run stores nothing in the step cache", projectId);
            _quarantineBaseline[projectId] = (false, null);
        }
    }

    public async Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct)
    {
        if (!_options.Enabled)
            return StepCacheLookupResult.Miss;

        try
        {
            await CaptureQuarantineBaselineAsync(inputs.ProjectId, ct);
            inputs = inputs with { QuarantineState = await ProjectQuarantineState.ReadAsync(_db, inputs.ProjectId, ct) };
            inputs = await WithStepResultSourcesAsync(inputs, ct);
            string cacheKey = _keyBuilder.Build(inputs);

            WorkflowStepCacheEntry? entry = await _db.WorkflowStepCacheEntries
                .FirstOrDefaultAsync(e => e.ProjectId == inputs.ProjectId && e.CacheKey == cacheKey, ct);

            if (entry is null)
                return StepCacheLookupResult.Miss;

            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= DateTime.UtcNow)
            {
                _logger.LogInformation(
                    "Step cache entry {CacheKeyPrefix}... for project {ProjectId} expired at {ExpiresAt}; evicting",
                    cacheKey[..Math.Min(12, cacheKey.Length)], inputs.ProjectId, entry.ExpiresAt);
                _db.WorkflowStepCacheEntries.Remove(entry);
                await _db.SaveChangesAsync(ct);
                return StepCacheLookupResult.Miss;
            }

            if (_options.ValidateStorageArtifacts && !await StorageArtifactsStillExistAsync(entry, ct))
            {
                _logger.LogInformation(
                    "Step cache entry {CacheKeyPrefix}... for project {ProjectId} references a storage object that no longer exists; evicting",
                    cacheKey[..Math.Min(12, cacheKey.Length)], inputs.ProjectId);
                _db.WorkflowStepCacheEntries.Remove(entry);
                await _db.SaveChangesAsync(ct);
                return StepCacheLookupResult.Miss;
            }

            if (inputs.StepType == StepType.VideoCompile
                && inputs.MaxOutputShortSide is { } cap
                && CachedCompileShortSide(entry.Output) is { } cachedShortSide
                && cachedShortSide > cap)
            {
                // Not evicted: the re-run stores its own result under this same key (an upsert).
                _logger.LogInformation(
                    "Step cache entry {CacheKeyPrefix}... for project {ProjectId} is a {ShortSide}p render above this run's {Cap}p output cap; treating as a miss",
                    cacheKey[..Math.Min(12, cacheKey.Length)], inputs.ProjectId, cachedShortSide, cap);
                return StepCacheLookupResult.Miss;
            }

            entry.HitCount++;
            entry.LastHitAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Step cache HIT {CacheKeyPrefix}... for project {ProjectId} (hit #{HitCount}, originally spent {TokensUsed} tokens)",
                cacheKey[..Math.Min(12, cacheKey.Length)], inputs.ProjectId, entry.HitCount, entry.TokensUsed);

            return new StepCacheLookupResult(true, entry);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // See the class doc comment: a cache failure must never fail the step it is trying to
            // speed up, so any unexpected exception here degrades to an ordinary miss.
            _logger.LogWarning(ex, "Step cache lookup failed for project {ProjectId}; treating as a miss", inputs.ProjectId);
            return StepCacheLookupResult.Miss;
        }
    }

    /// <summary>
    /// The short side a cached <c>VideoCompile</c> output was encoded at: its <c>output.encodedShortSide</c>
    /// when the plan cap scaled it down, else the shorter of <c>output.width</c>/<c>output.height</c>.
    /// Null when the output does not say (an older or unreadable row) — the caller then serves the hit
    /// exactly as before, since nothing proves the render is above the cap.
    /// </summary>
    internal static int? CachedCompileShortSide(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("output", out JsonElement node)
                || node.ValueKind != JsonValueKind.Object)
                return null;

            if (node.TryGetProperty("encodedShortSide", out JsonElement encoded)
                && encoded.ValueKind == JsonValueKind.Number && encoded.TryGetInt32(out int encodedShortSide) && encodedShortSide > 0)
                return encodedShortSide;

            return node.TryGetProperty("width", out JsonElement w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out int width)
                && node.TryGetProperty("height", out JsonElement h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out int height)
                && width > 0 && height > 0
                ? Math.Min(width, height)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a deterministic step's <c>{view, meta}</c> output reports that one of its stages
    /// DEGRADED — <c>meta.{stage}.degraded: true</c>, the convention every optional stage uses
    /// (transcription, vision, insert and object tracking, an events detection that lost a part...). A degradation is a provider outage,
    /// a timeout, a missing configuration: transient by nature. Caching it would replay the
    /// failure on every identical re-run long after the cause was fixed — which is exactly what a
    /// censor run with a then-broken Vision provider would otherwise have done.
    /// </summary>
    private static readonly HashSet<string> TransientStages = new(StringComparer.Ordinal)
    {
        "objectTracking", "vision", "insertTracking", "events"
    };

    internal static bool ReportsDegradation(string output)
    {
        if (!output.Contains("\"degraded\"", StringComparison.Ordinal))
            return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("meta", out JsonElement meta) ||
                meta.ValueKind != JsonValueKind.Object)
                return false;

            foreach (JsonProperty stage in meta.EnumerateObject())
            {
                // Only stages whose degradation is a transient provider failure. Transcription
                // also reports "degraded" for a source with no audio stream or an install with no
                // speech-to-text provider — permanent facts of the input, and caching around them
                // would turn every such analysis into a full re-run forever.
                if (!TransientStages.Contains(stage.Name))
                    continue;
                if (stage.Value.ValueKind == JsonValueKind.Object &&
                    stage.Value.TryGetProperty("degraded", out JsonElement degraded) &&
                    degraded.ValueKind == JsonValueKind.True)
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// v7 (T-023, S-17): fills <see cref="StepCacheKeyInputs.StepResultSources"/> with what every
    /// <c>StepResult</c> source of the step's VideoAnalyze/VideoCompile/ExtractFrame config resolves to NOW, through
    /// the same project-scoped resolver the executors use (a resolved key, or <c>\0missing</c> for anything it
    /// refuses), so a source pruned, deleted or re-pointed since an entry was stored misses. S-64 / QA-P1-4: a
    /// compile's insert content named by <c>{"from":"StepResult"}</c> (the fallback and each per-plate entry) is a
    /// source too, resolved as the insert loop resolves it (stills allowed), and so is ExtractFrame's source. A
    /// config naming no such source (or one that does not parse) is returned unchanged.
    /// </summary>
    private async Task<StepCacheKeyInputs> WithStepResultSourcesAsync(StepCacheKeyInputs inputs, CancellationToken ct)
    {
        (string? json, bool allowImages) = inputs.StepType switch
        {
            StepType.VideoAnalyze => (inputs.VideoAnalyzeConfigJson, true),
            StepType.VideoCompile => (inputs.VideoCompileConfigJson, false),
            StepType.ExtractFrame => (inputs.ExtractFrameConfigJson, false),
            StepType.AudioCleanup => (inputs.AudioCleanupConfigJson, false),
            StepType.VideoEnhance => (inputs.VideoEnhanceConfigJson, false),
            StepType.ImageGenerate => (inputs.ImageGenerateConfigJson, true),
            _ => (null, false)
        };
        // AudioCleanup takes an audio file as well as a video: its StepResult source resolves audio outputs too.
        bool allowAudio = inputs.StepType == StepType.AudioCleanup;

        List<(Guid Id, bool AllowImages, string Label)> sources = [];
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                List<Guid> ids = [];
                CollectStepResultIds(JsonNode.Parse(json), ids);
                sources.AddRange(ids.Select(id => (id, allowImages, "")));
            }
            catch (JsonException)
            {
                return inputs;
            }

            if (inputs.StepType == StepType.VideoCompile)
            {
                foreach (Guid id in InsertStepResultIds(json))
                    sources.Add((id, true, "insert:"));
            }
        }

        if (sources.Count == 0)
            return inputs;

        List<string> parts = [];
        foreach ((Guid id, bool images, string label) in sources)
        {
            StepResultSourceResolution resolution = await StepResultSourceResolver.ResolveAsync(
                _db, inputs.ProjectId, new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: id), images, ct, allowAudio);
            parts.Add($"{label}{id:D}={resolution.StorageKey ?? "\0missing"}");
        }

        return inputs with { StepResultSources = string.Join('|', parts) };
    }

    /// <summary>
    /// S-64: the <c>StepResult</c> ids of a compile's insert references, bound with the executor's own config options
    /// so every spelling the engine resolves (member case, padded kind) is fingerprinted. A config that does not bind
    /// names none (the executor refuses it too).
    /// </summary>
    private static List<Guid> InsertStepResultIds(string json)
    {
        VideoCompileStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, VideoAnalyzeStepExecutor.ConfigJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return [];
        }

        if (config is null)
            return [];
        IEnumerable<InsertContentRef?> refs = [config.InsertContentRef, .. (config.InsertContents ?? []).Select(a => a?.InsertContentRef)];
        return [.. refs
            .Where(r => r is { Kind: InsertContentRef.StepResultKind, StepResultId: { } id } && id != Guid.Empty)
            .Select(r => r!.StepResultId!.Value)];
    }

    /// <summary>
    /// Every object anywhere in <paramref name="node"/> that the engine binds as a <see cref="VideoSourceKind.StepResult"/>
    /// source, in document order. S-17: each candidate (an object with a <c>kind</c> property in any case) is read with
    /// the executor's own config options (<see cref="VideoAnalyzeStepExecutor.ConfigJsonOptions"/>: web defaults plus
    /// the string-enum converter), so a numeric <c>4</c>, a <c>"4"</c>, a padded or differently cased kind fingerprints
    /// its source exactly when the executor would resolve it (VideoCompile's options are identical). An object that does
    /// not bind is skipped.
    /// </summary>
    private static void CollectStepResultIds(JsonNode? node, List<Guid> ids)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Any(p => string.Equals(p.Key, "kind", StringComparison.OrdinalIgnoreCase))
                    && TryBindSource(obj) is { Kind: VideoSourceKind.StepResult, StepResultId: { } found })
                {
                    ids.Add(found);
                }

                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    if (property.Value is JsonObject or JsonArray)
                        CollectStepResultIds(property.Value, ids);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                    CollectStepResultIds(item, ids);
                break;
        }
    }

    private static VideoSourceRef? TryBindSource(JsonObject obj)
    {
        try
        {
            return obj.Deserialize<VideoSourceRef>(VideoAnalyzeStepExecutor.ConfigJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct)
    {
        if (!_options.Enabled)
            return;
        if (result.Status != StepStatus.Completed)
            return;
        if (string.IsNullOrWhiteSpace(result.Output))
            return;
        if (result.Output.Length > _options.MaxEntryChars)
            return;
        if (ReportsDegradation(result.Output))
            return;
        // D12: a result produced by the ORGANIZATION'S OWN runner is never stored. The executor
        // checks this too, before calling in; this is the same rule at the cache boundary, so no
        // caller can bypass it. See IStepCachePolicy.IsAdmissible for why an OrgRunner result is
        // excluded while a CloudPool one is not.
        if (!_policy.IsAdmissible(result))
        {
            _logger.LogInformation(
                "Step cache store skipped for project {ProjectId}: the result came from the organization's own runner (compute target {ComputeTarget}), which is never cached",
                inputs.ProjectId, result.ComputeTarget);
            return;
        }

        try
        {
            await CaptureQuarantineBaselineAsync(inputs.ProjectId, ct);
            string? quarantineState = await ProjectQuarantineState.ReadAsync(_db, inputs.ProjectId, ct);
            (bool known, string? baseline) = _quarantineBaseline[inputs.ProjectId];
            if (!known || baseline != quarantineState)
            {
                _logger.LogInformation(
                    "Step cache store skipped for project {ProjectId}: its file state changed during this run or could not be read",
                    inputs.ProjectId);
                return;
            }

            inputs = inputs with { QuarantineState = quarantineState };
            inputs = await WithStepResultSourcesAsync(inputs, ct);
            string cacheKey = _keyBuilder.Build(inputs);
            DateTime now = DateTime.UtcNow;
            DateTime? expiresAt = _options.TtlHours > 0 ? now.AddHours(_options.TtlHours) : null;

            WorkflowStepCacheEntry? existing = await _db.WorkflowStepCacheEntries
                .FirstOrDefaultAsync(e => e.ProjectId == inputs.ProjectId && e.CacheKey == cacheKey, ct);

            if (existing is null)
            {
                _db.WorkflowStepCacheEntries.Add(new WorkflowStepCacheEntry
                {
                    Id = Guid.NewGuid(),
                    ProjectId = inputs.ProjectId,
                    CacheKey = cacheKey,
                    WorkflowDefinitionId = inputs.WorkflowDefinitionId,
                    StepType = inputs.StepType,
                    AgentType = inputs.AgentType,
                    Output = result.Output,
                    OutputStorageKey = result.OutputStorageKey,
                    ArtifactStorageKey = result.ArtifactStorageKey,
                    TimelineStorageKey = result.TimelineStorageKey,
                    ChatTranscriptJson = result.ChatTranscriptJson,
                    TokensUsed = result.TokensUsed,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens,
                    DurationMs = result.DurationMs,
                    CreatedAt = now,
                    LastHitAt = null,
                    HitCount = 0,
                    ExpiresAt = expiresAt
                });
            }
            else
            {
                // Upsert: a fresh execution's result replaces the stored one wholesale — including
                // its TTL (refreshed from `now`, so a step that keeps naturally re-running never
                // actually expires) — but HitCount/LastHitAt are left untouched, since writing a
                // fresh result is not itself a cache HIT.
                existing.WorkflowDefinitionId = inputs.WorkflowDefinitionId;
                existing.StepType = inputs.StepType;
                existing.AgentType = inputs.AgentType;
                existing.Output = result.Output;
                existing.OutputStorageKey = result.OutputStorageKey;
                existing.ArtifactStorageKey = result.ArtifactStorageKey;
                existing.TimelineStorageKey = result.TimelineStorageKey;
                existing.ChatTranscriptJson = result.ChatTranscriptJson;
                existing.TokensUsed = result.TokensUsed;
                existing.InputTokens = result.InputTokens;
                existing.OutputTokens = result.OutputTokens;
                existing.DurationMs = result.DurationMs;
                existing.CreatedAt = now;
                existing.ExpiresAt = expiresAt;
            }

            // Opportunistic pruning: every store is a natural, cheap moment to sweep this
            // project's own expired rows, rather than running a separate background job just for
            // TTL eviction.
            List<WorkflowStepCacheEntry> stale = await _db.WorkflowStepCacheEntries
                .Where(e => e.ProjectId == inputs.ProjectId && e.ExpiresAt != null && e.ExpiresAt < now)
                .ToListAsync(ct);
            if (stale.Count > 0)
                _db.WorkflowStepCacheEntries.RemoveRange(stale);

            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // See the class doc comment: never let a cache-write failure fail the step whose
            // (already-successful) result it was trying to persist for next time.
            _logger.LogWarning(ex, "Step cache store failed for project {ProjectId}; the step's own result is unaffected", inputs.ProjectId);
        }
    }

    /// <summary>
    /// HEADs <paramref name="entry"/>'s <see cref="WorkflowStepCacheEntry.OutputStorageKey"/>,
    /// <see cref="WorkflowStepCacheEntry.ArtifactStorageKey"/>, and
    /// <see cref="WorkflowStepCacheEntry.TimelineStorageKey"/> (whichever are set) against S3, so
    /// a hit is never served for an object a bucket reset or manual cleanup already deleted. When
    /// no S3 client is wired (see the constructor's doc comment) this degrades to "trust the row"
    /// rather than failing the lookup.
    /// </summary>
    private async Task<bool> StorageArtifactsStillExistAsync(WorkflowStepCacheEntry entry, CancellationToken ct)
    {
        if (_s3Client is null)
            return true;

        foreach (string? key in new[] { entry.OutputStorageKey, entry.ArtifactStorageKey, entry.TimelineStorageKey })
        {
            if (string.IsNullOrEmpty(key))
                continue;

            if (!await ObjectExistsAsync(key, ct))
                return false;
        }

        return true;
    }

    private async Task<bool> ObjectExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _s3Client!.GetObjectMetadataAsync(_bucketName, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
