using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>
/// The object-analysis gate/estimate/queue/status logic, shared by <c>ObjectAnalysisController</c> and the assistant. It answers
/// with <see cref="ObjectAnalysisResult{T}"/> (never an <c>IActionResult</c>); the response bodies and status codes are exactly the
/// controller's. Plan feature, estimate, credit hold, content cache and refunds are the hidden workflow's own. Every failure to
/// find the source is a 404 (never a 403), and no response carries a storage key or hash.
/// </summary>
public sealed class ObjectAnalysisService : IObjectAnalysisService
{
    /// <summary>Paid analyses per user per project per rolling hour (the paid bucket's default).</summary>
    public const int PaidRunsPerHour = 10;

    public const int MaxListed = 50;
    private static readonly string[] Effects = ["Blur", "Pixelate", "Highlight", "Spotlight", "ColorPop", "Outline"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".mkv"];

    private readonly InferenceApiDbContext _db;
    private readonly IWorkflowExecutionService _executions;
    private readonly IFileStorageService _storage;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly IWorkflowCostEstimator? _costEstimator;

    /// <summary>The container's constructor.</summary>
    public ObjectAnalysisService(
        InferenceApiDbContext db,
        IWorkflowExecutionService executions,
        IFileStorageService storage,
        ILogger<ObjectAnalysisService> logger,
        TimeProvider? clock = null,
        IOrganizationEntitlements? entitlements = null,
        IWorkflowCostEstimator? costEstimator = null,
        IConfiguration? configuration = null)
        : this(db, executions, storage, (ILogger)logger, clock, entitlements, costEstimator)
    {
        if (configuration?.GetValue<int?>(NotFoundTapsConfigKey) is { } configured && configured >= 0) MaxNotFoundTapsPerHour = configured;
    }

    /// <summary>Config key for <see cref="MaxNotFoundTapsPerHour"/>.</summary>
    public const string NotFoundTapsConfigKey = "ObjectAnalysis:MaxNotFoundTapsPerHour";

    /// <summary>Default cap on taps per user and project per rolling hour that found nothing (free to the user, paid by us).</summary>
    public const int DefaultMaxNotFoundTapsPerHour = 5;

    /// <summary>Taps that found nothing allowed per user and project per rolling hour; over it a seeded request is a 429.</summary>
    public int MaxNotFoundTapsPerHour { get; } = DefaultMaxNotFoundTapsPerHour;

    /// <summary>For a caller that already holds its own logger (the controller's fallback when none is injected).</summary>
    internal ObjectAnalysisService(
        InferenceApiDbContext db,
        IWorkflowExecutionService executions,
        IFileStorageService storage,
        ILogger logger,
        TimeProvider? clock,
        IOrganizationEntitlements? entitlements,
        IWorkflowCostEstimator? costEstimator)
    {
        _db = db;
        _executions = executions;
        _storage = storage;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _entitlements = entitlements;
        _costEstimator = costEstimator;
    }

    private bool BillingOn => _costEstimator is { BillingEnabled: true };

    internal sealed record Source(string Kind, Guid Id, double? DurationSec, double? Fps);

    // ------------------------------------------------------------------------------------------------ estimate

    public async Task<ObjectAnalysisResult<ObjectAnalysisEstimate>> EstimateAsync(
        ObjectAnalysisCaller caller, string mediaRef, ObjectAnalysisEstimateRequest? body, CancellationToken ct)
    {
        Project project = caller.Project;
        Guid projectId = project.Id;

        if (ValidateTarget(body?.Prompt, body?.Seed, out string prompt, out string label, out bool noWords) is { } badPrompt) return Refuse<ObjectAnalysisEstimate>(badPrompt);
        if (!TryScope(body?.MatchScope, out ObjectMatchScope scope)) return Refuse<ObjectAnalysisEstimate>(BadScope());
        if (body?.Seed is not null) scope = ObjectMatchScope.SingleTrack;

        (Source? source, ObjectAnalysisRefusal? sourceFailure) = await FindSourceAsync(projectId, mediaRef, ct);
        if (sourceFailure is not null) return Refuse<ObjectAnalysisEstimate>(sourceFailure);
        if (SeedTimeRefusal(body?.Seed, source!) is { } seedTime) return Refuse<ObjectAnalysisEstimate>(seedTime);

        // The cap is priced in 30-fps-equivalent seconds (a faster video has more frames); unknown length prices the maximum.
        int? cap = ObjectAnalysisConfig.MaxSecondsFor(source!.DurationSec, source.Fps);
        int maxSeconds = cap ?? ObjectAnalysisConfig.MaxSourceSeconds;
        bool upperBound = source.DurationSec is null;
        string? unavailable = await PlanRefusalAsync(project, ct) is { } violation ? violation.Message : null;
        if (unavailable is null && cap is null) unavailable = TooLongMessage(source);

        bool consented = project.ObjectEffectsConsentAt is not null;
        ObjectEffectRequest? cached = await FindReusableAsync(projectId, source, prompt, scope, succeededOnly: true, ct);
        int frames = SegmentationCreditPricing.PricedFramesFor(maxSeconds);

        if (!BillingOn)
        {
            return ObjectAnalysisResult<ObjectAnalysisEstimate>.Ok(new ObjectAnalysisEstimate(
                false, 0, frames, maxSeconds, 0, unavailable is null, cached is not null, consented, unavailable, upperBound));
        }

        // The balance is the real one even for a cached hit (which costs nothing).
        WorkflowPreflightResult price = await PreflightAsync(project, source, label, scope, maxSeconds, body?.Seed, noWords, ct);
        if (cached is not null)
        {
            return ObjectAnalysisResult<ObjectAnalysisEstimate>.Ok(new ObjectAnalysisEstimate(
                true, 0, frames, maxSeconds, price.BalanceMillis / 1000.0, unavailable is null, true, consented, unavailable, upperBound));
        }

        return ObjectAnalysisResult<ObjectAnalysisEstimate>.Ok(new ObjectAnalysisEstimate(
            true,
            price.Estimate.RequiredMillis / 1000.0,
            frames,
            maxSeconds,
            price.BalanceMillis / 1000.0,
            unavailable is null && !price.Refused,
            false,
            consented,
            unavailable,
            upperBound));
    }

    // ------------------------------------------------------------------------------------------------ request

    public async Task<ObjectAnalysisResult<ObjectAnalysisQueuedResponse>> RequestAsync(
        ObjectAnalysisCaller caller, string mediaRef, ObjectAnalysisRequestBody? body, ObjectAnalysisIntent? intent, CancellationToken ct)
    {
        // A run like any other: an unverified address cannot start one.
        if (!caller.EmailVerified)
        {
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(
                StatusCodes.Status403Forbidden, new { code = EmailVerificationGate.ErrorCode, message = EmailVerificationGate.Message }));
        }

        Project project = caller.Project;
        Guid projectId = project.Id;

        // A restore of this project is rewriting it: no run starts until it is done.
        if (await RestoreRunGate.IsHeldAsync(_db, projectId, ct))
        {
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(
                StatusCodes.Status409Conflict, new { code = ProjectHistoryRestoreCodes.RestoreInProgress, message = RestoreRunGate.Message }));
        }

        if (ValidateTarget(body?.Prompt, body?.Seed, out string prompt, out string label, out bool noWords) is { } badPrompt) return Refuse<ObjectAnalysisQueuedResponse>(badPrompt);
        if (!TryScope(body?.MatchScope, out ObjectMatchScope scope)) return Refuse<ObjectAnalysisQueuedResponse>(BadScope());
        if (body?.Seed is not null) scope = ObjectMatchScope.SingleTrack;
        if (ValidateIntent(body, out string effect, out double strength, out int feather) is { } badIntent) return Refuse<ObjectAnalysisQueuedResponse>(badIntent);
        if (intent is not null && (intent.TimelineId == Guid.Empty || intent.ItemIds.Count == 0))
        {
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(StatusCodes.Status400BadRequest,
                new { code = "OBJECT_INTENT_INVALID", message = "Name the timeline and at least one clip to apply the effect to." }));
        }

        (Source? source, ObjectAnalysisRefusal? sourceFailure) = await FindSourceAsync(projectId, mediaRef, ct);
        if (sourceFailure is not null) return Refuse<ObjectAnalysisQueuedResponse>(sourceFailure);
        if (SeedTimeRefusal(body?.Seed, source!) is { } seedTime) return Refuse<ObjectAnalysisQueuedResponse>(seedTime);

        if (await PlanRefusalAsync(project, ct) is { } violation)
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(violation.StatusCode, violation.ToBody()));

        if (project.ObjectEffectsConsentAt is null)
        {
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(StatusCodes.Status409Conflict, new
            {
                code = ObjectAnalysisMessages.ConsentRequired,
                message = "Before we can find an object, you need to allow this project's video frames to be sent to our object-detection service. You can agree once in the editor.",
                needsBeforeRunning = ObjectAnalysisMessages.ConsentNeeds
            }));
        }

        int? cap = ObjectAnalysisConfig.MaxSecondsFor(source!.DurationSec, source.Fps);
        if (cap is null)
        {
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(
                StatusCodes.Status422UnprocessableEntity, new { code = ObjectAnalysisMessages.SourceTooLong, message = TooLongMessage(source) }));
        }

        // The same analysis already done or in flight: no run, no charge.
        ObjectEffectRequest? existing = await FindReusableAsync(projectId, source, prompt, scope, succeededOnly: false, ct);
        if (existing is not null)
        {
            ObjectEffectRequest answered = existing;
            if (intent is not null)
            {
                // The analysis is shared, but the intent belongs to this request: it gets its own row on the same run, so the
                // finalizer has something to apply for it.
                // One in-flight request per (source, prompt, scope, timeline): a second attach for the same timeline while the
                // first is queued is answered with the first (its intent is the one the finalizer applies), never an error.
                ObjectEffectRequest? sameTimeline = await FindInFlightForTimelineAsync(projectId, source, prompt, scope, intent.TimelineId, ct);
                if (sameTimeline is not null) return await AlreadyInProgressAsync(sameTimeline, ct);

                answered = NewRow(caller, source, prompt, scope, effect, strength, feather, body, intent, existing.ExecutionId, _clock.GetUtcNow().UtcDateTime);
                answered.Status = existing.Status == ObjectEffectRequest.StatusQueued
                    ? ObjectEffectRequest.StatusQueued
                    : ObjectEffectRequest.StatusSucceeded;
                _db.ObjectEffectRequests.Add(answered);
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ObjectAnalysisRowWriter.IsUniqueViolation(ex))
                {
                    // Lost a race with an identical attach: the in-flight identity index let the other one in.
                    _db.Entry(answered).State = EntityState.Detached;
                    if (await FindInFlightForTimelineAsync(projectId, source, prompt, scope, intent.TimelineId, ct) is { } winner)
                        return await AlreadyInProgressAsync(winner, ct);
                    throw;
                }
            }

            bool done = existing.Status != ObjectEffectRequest.StatusQueued && existing.Status != ObjectEffectRequest.StatusFailed;
            ObjectAnalysisQueuedResponse reused = new(answered.Id, answered.ExecutionId, ApiStatus(answered.Status), WorkflowId: await WorkflowIdOfAsync(answered.ExecutionId, ct));
            return ObjectAnalysisResult<ObjectAnalysisQueuedResponse>.Ok(reused, done ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }

        int maxSeconds = cap.Value;
        VideoAnalyzeStepConfig config = ObjectAnalysisConfig.Build(SourceRef(source), label, scope, maxSeconds, body?.Seed, noWords);
        string configJson = ObjectAnalysisConfig.Serialize(config);
        if (Validate(configJson) is { } invalid) return Refuse<ObjectAnalysisQueuedResponse>(invalid);

        if (BillingOn)
        {
            WorkflowPreflightResult price = await PreflightAsync(project, source, label, scope, maxSeconds, body?.Seed, noWords, ct);
            if (price.Refused)
            {
                return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(StatusCodes.Status402PaymentRequired, new
                {
                    code = "INSUFFICIENT_CREDITS",
                    message = CreditShortfallMessage.Build(price.Estimate.RequiredMillis, null, price.BalanceMillis),
                    missing = CreditShortfallMessage.MissingCredits(price.Estimate.RequiredMillis, price.BalanceMillis)
                }));
            }
        }

        WorkflowStep probe = new() { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = configJson };
        RunRateBucket bucket = RunRateClassifier.Classify([probe]);
        if (await CheckRunRateAsync(projectId, caller.UserId, bucket, ct) is { } limited) return Refuse<ObjectAnalysisQueuedResponse>(limited);
        if (body?.Seed is not null && await CheckNotFoundTapsAsync(projectId, caller.UserId, includesOwn: false, ct) is { } tapLimited)
            return Refuse<ObjectAnalysisQueuedResponse>(tapLimited);

        AgentDefinition? placeholder = await FindPlaceholderAsync(ct);
        if (placeholder is null)
        {
            return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(StatusCodes.Status500InternalServerError, new
            {
                message = "The VideoTransform placeholder agent is not seeded, so no analysis step can be created."
            }));
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = body?.Seed is null ? $"Find object: {prompt}" : "Find object: selected by tap",
            Kind = WorkflowDefinitionKind.ObjectAnalysis,
            RequiresUserInput = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = placeholder.Id,
            StepOrder = 1,
            Label = "Find object",
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = configJson
        });

        Guid executionId = Guid.NewGuid();
        ObjectEffectRequest row = NewRow(caller, source, prompt, scope, effect, strength, feather, body, intent, executionId, now);

        // One transaction for the hidden workflow, the request row and the execution: a refused or failed start (the plan's
        // concurrency cap, a database error) rolls all three back. QueueAsync commits through beforePublish.
        await using (IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            bool committed = false;
            try
            {
                _db.WorkflowDefinitions.Add(workflow);
                _db.ObjectEffectRequests.Add(row);
                await _db.SaveChangesAsync(ct);

                // Counted again WITH this row in, inside the transaction, so taps racing past the first check are bounded too.
                if (body?.Seed is not null && await CheckNotFoundTapsAsync(projectId, caller.UserId, includesOwn: true, ct) is { } overCap)
                    throw new TapCapExceededException(overCap);

                await _executions.QueueAsync(
                    projectId, workflow.Id, caller.UserId, userRequest: null, ExecutionInitiator.Human, ct,
                    regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.ObjectAnalysis, executionId,
                    beforePublish: async commitCt =>
                    {
                        await transaction.CommitAsync(commitCt);
                        committed = true;
                    });
                if (!committed)
                {
                    await transaction.CommitAsync(ct);
                    committed = true;
                }
            }
            catch (Exception ex) when (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                if (ex is TapCapExceededException capped) return Refuse<ObjectAnalysisQueuedResponse>(capped.Refusal);
                if (ex is PlanLimitException limit)
                    return Refuse<ObjectAnalysisQueuedResponse>(new ObjectAnalysisRefusal(limit.Violation.StatusCode, limit.Violation.ToBody()));

                // Two identical requests raced past the reuse check: the partial unique index let one in. Answer with that one.
                if (ex is DbUpdateException dbEx && ObjectAnalysisRowWriter.IsUniqueViolation(dbEx)
                    && await FindReusableAsync(projectId, source, prompt, scope, succeededOnly: false, ct) is { } winner)
                {
                    bool finished = winner.Status != ObjectEffectRequest.StatusQueued && winner.Status != ObjectEffectRequest.StatusFailed;
                    return ObjectAnalysisResult<ObjectAnalysisQueuedResponse>.Ok(
                        new ObjectAnalysisQueuedResponse(winner.Id, winner.ExecutionId, ApiStatus(winner.Status), WorkflowId: await WorkflowIdOfAsync(winner.ExecutionId, ct)),
                        finished ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
                }

                throw;
            }
        }

        return ObjectAnalysisResult<ObjectAnalysisQueuedResponse>.Ok(
            new ObjectAnalysisQueuedResponse(row.Id, executionId, ObjectAnalysisStatus.Queued, WorkflowId: workflow.Id), StatusCodes.Status202Accepted);
    }

    public const string AlreadyInProgressMessage =
        "This is already being looked for in that timeline; the effect will appear when it finishes.";

    private async Task<ObjectAnalysisResult<ObjectAnalysisQueuedResponse>> AlreadyInProgressAsync(ObjectEffectRequest row, CancellationToken ct) =>
        ObjectAnalysisResult<ObjectAnalysisQueuedResponse>.Ok(
            new ObjectAnalysisQueuedResponse(row.Id, row.ExecutionId, ObjectAnalysisStatus.Queued, AlreadyInProgressMessage, await WorkflowIdOfAsync(row.ExecutionId, ct)),
            StatusCodes.Status202Accepted);

    /// <summary>The hidden workflow a run belongs to (the stop route needs it); null when the run is gone.</summary>
    public async Task<Guid?> WorkflowIdOfAsync(Guid executionId, CancellationToken ct) =>
        await _db.WorkflowExecutions.AsNoTracking().Where(e => e.Id == executionId).Select(e => (Guid?)e.WorkflowDefinitionId).FirstOrDefaultAsync(ct);

    private Task<ObjectEffectRequest?> FindInFlightForTimelineAsync(
        Guid projectId, Source source, string prompt, ObjectMatchScope scope, Guid timelineId, CancellationToken ct)
    {
        string sourceId = source.Id.ToString("D");
        string scopeName = scope.ToString();
        return _db.ObjectEffectRequests.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.SourceKind == source.Kind && r.SourceId == sourceId
                && r.Prompt == prompt && r.MatchScope == scopeName
                && r.Status == ObjectEffectRequest.StatusQueued && r.TimelineId == timelineId)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static ObjectEffectRequest NewRow(
        ObjectAnalysisCaller caller, Source source, string prompt, ObjectMatchScope scope, string effect, double strength, int feather,
        ObjectAnalysisRequestBody? body, ObjectAnalysisIntent? intent, Guid executionId, DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = caller.Project.OrganizationId,
            ProjectId = caller.Project.Id,
            ExecutionId = executionId,
            SourceKind = source.Kind,
            SourceId = source.Id.ToString("D"),
            Prompt = prompt,
            MatchScope = scope.ToString(),
            Effect = effect,
            Strength = strength,
            Invert = body?.Invert ?? false,
            FeatherPx = feather,
            TimelineId = intent?.TimelineId,
            TimelineItemIdsJson = intent is null ? null : JsonSerializer.Serialize(intent.ItemIds.Select(i => i.ToString("D"))),
            Status = ObjectEffectRequest.StatusQueued,
            CreatedByUserId = caller.UserId,
            CreatedAt = now
        };

    // ------------------------------------------------------------------------------------------------ reads

    public async Task<ObjectAnalysisResult<ObjectAnalysisResponse>> GetAsync(Guid projectId, string mediaRef, Guid requestId, CancellationToken ct)
    {
        if (!TryParseRef(mediaRef, out string kind, out Guid id)) return ObjectAnalysisResult<ObjectAnalysisResponse>.Refuse(StatusCodes.Status404NotFound);

        ObjectEffectRequest? row = await _db.ObjectEffectRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ProjectId == projectId
                && r.SourceKind == kind && r.SourceId == id.ToString("D"), ct);
        if (row is null) return ObjectAnalysisResult<ObjectAnalysisResponse>.Refuse(StatusCodes.Status404NotFound);
        return ObjectAnalysisResult<ObjectAnalysisResponse>.Ok(await ToResponseAsync(row, ct));
    }

    public async Task<ObjectAnalysisResult<IReadOnlyList<ObjectAnalysisResponse>>> ListAsync(Guid projectId, string mediaRef, CancellationToken ct)
    {
        if (!TryParseRef(mediaRef, out string kind, out Guid id))
            return ObjectAnalysisResult<IReadOnlyList<ObjectAnalysisResponse>>.Refuse(StatusCodes.Status404NotFound);

        string sourceId = id.ToString("D");
        List<ObjectEffectRequest> rows = (await _db.ObjectEffectRequests
                .Where(r => r.ProjectId == projectId && r.SourceKind == kind && r.SourceId == sourceId)
                .ToListAsync(ct))
            .OrderByDescending(r => r.CreatedAt)
            .Take(MaxListed)
            .ToList();

        List<ObjectAnalysisResponse> list = [];
        foreach (ObjectEffectRequest row in rows) list.Add(await ToResponseAsync(row, ct));
        return ObjectAnalysisResult<IReadOnlyList<ObjectAnalysisResponse>>.Ok(list);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static ObjectAnalysisResult<T> Refuse<T>(ObjectAnalysisRefusal refusal) => ObjectAnalysisResult<T>.Refuse(refusal);

    private async Task<ObjectAnalysisResponse> ToResponseAsync(ObjectEffectRequest row, CancellationToken ct)
    {
        ObjectAnalysisDerived derived = await DeriveAsync(row, ct);
        return new ObjectAnalysisResponse(
            row.Id, row.ExecutionId, derived.Status, row.Prompt, row.MatchScope, row.Effect, row.Strength, row.Invert, row.FeatherPx,
            derived.StepResultId, derived.TrackIds, derived.CoverageEndSec,
            derived.FailureCode, derived.Message, row.CreatedAt,
            // The apply step of a request that carries a timeline intent: "applied" (the note says what was skipped or changed) or "apply_failed".
            row.Status is ObjectEffectRequest.StatusApplied or ObjectEffectRequest.StatusApplyFailed or ObjectEffectRequest.StatusNeedsReview ? row.Status : null,
            row.Status is ObjectEffectRequest.StatusApplied or ObjectEffectRequest.StatusApplyFailed or ObjectEffectRequest.StatusNeedsReview ? row.FailureMessage : null,
            await WorkflowIdOfAsync(row.ExecutionId, ct));
    }

    /// <inheritdoc />
    public async Task<ObjectAnalysisDerived> DeriveAsync(ObjectEffectRequest row, CancellationToken ct)
    {
        if (row.Status == ObjectEffectRequest.StatusFailed)
        {
            return new(ObjectAnalysisStatus.Failed, row.FailureCode, ObjectAnalysisMessages.For(row.FailureCode), null, null, null);
        }

        var run = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == row.ExecutionId && e.ProjectId == row.ProjectId)
            .Select(e => new { e.Status, e.ErrorMessage })
            .FirstOrDefaultAsync(ct);

        ObjectAnalysisStepFacts? step = null;
        IReadOnlyList<VideoSegmentationTrack>? tracks = null;
        bool held = false;
        bool readFailed = false;
        if (run is not null && run.Status is ExecutionStatus.Passed or ExecutionStatus.Failed)
        {
            var result = await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.WorkflowExecutionId == row.ExecutionId && r.WorkflowStep.StepType == StepType.VideoAnalyze)
                .OrderByDescending(r => r.CompletedAt)
                .Select(r => new { r.Id, r.Status, r.FailureCode, r.OutputJson, r.ArtifactStorageKey })
                .FirstOrDefaultAsync(ct);
            if (result is not null)
            {
                step = new ObjectAnalysisStepFacts(result.Id, result.Status, result.FailureCode, ErrorCodeOf(result.OutputJson));
                if (run.Status == ExecutionStatus.Passed && result.Status != StepStatus.Failed)
                {
                    held = await OutputGate.IsResultHeldAsync(_db, result.Id, ct);
                    if (!held) (tracks, readFailed) = await ReadTracksAsync(row.ProjectId, result.ArtifactStorageKey, ct);
                }
            }
        }

        ObjectAnalysisDerived derived = ObjectAnalysisStatus.Derive(run?.Status, run?.ErrorMessage, step, tracks, held, readFailed);

        if (derived.Terminal && row.Status == ObjectEffectRequest.StatusQueued)
        {
            try
            {
                await ObjectAnalysisRowWriter.TryFinalizeAsync(
                    _db, row,
                    derived.Status == ObjectAnalysisStatus.Succeeded ? ObjectEffectRequest.StatusSucceeded : ObjectEffectRequest.StatusFailed,
                    derived.FailureCode, derived.Message, _clock.GetUtcNow().UtcDateTime, ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Could not persist the status of object analysis {RequestId}", row.Id);
            }
        }

        return derived;
    }

    /// <summary>The artifact's tracks; <c>ReadFailed</c> is true for a transient read error (no terminal state may be written for it).</summary>
    private async Task<(IReadOnlyList<VideoSegmentationTrack>? Tracks, bool ReadFailed)> ReadTracksAsync(Guid projectId, string? key, CancellationToken ct)
    {
        if (key is null
            || !key.StartsWith(SegmentationTrackIdLookup.ArtifactPrefix(projectId), StringComparison.Ordinal)
            || key.Contains("..", StringComparison.Ordinal))
        {
            return (null, false);
        }

        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, key, ct);
            VideoAnalysisArtifact? artifact = await SegmentationTrackIdLookup.ParseAsync(stream, ct);
            return (artifact?.SegmentationTracks, false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the video-analysis artifact of project {ProjectId}", projectId);
            return (null, true);
        }
    }

    internal static string? ErrorCodeOf(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The newest request for the same (source, prompt, scope) that is succeeded (or, unless <paramref name="succeededOnly"/>, in flight).</summary>
    private async Task<ObjectEffectRequest?> FindReusableAsync(
        Guid projectId, Source source, string prompt, ObjectMatchScope scope, bool succeededOnly, CancellationToken ct)
    {
        string sourceId = source.Id.ToString("D");
        string scopeName = scope.ToString();
        List<ObjectEffectRequest> candidates = (await _db.ObjectEffectRequests
                .Where(r => r.ProjectId == projectId && r.SourceKind == source.Kind && r.SourceId == sourceId
                    && r.Prompt == prompt && r.MatchScope == scopeName)
                .ToListAsync(ct))
            .OrderByDescending(r => r.CreatedAt)
            .Take(10)
            .ToList();

        foreach (ObjectEffectRequest candidate in candidates)
        {
            if (candidate.Status == ObjectEffectRequest.StatusQueued) await DeriveAsync(candidate, ct);
        }

        foreach (ObjectEffectRequest done in candidates.Where(r => r.Status is ObjectEffectRequest.StatusSucceeded
                     or ObjectEffectRequest.StatusApplied or ObjectEffectRequest.StatusApplyFailed
                     or ObjectEffectRequest.StatusNeedsReview or ObjectEffectRequest.StatusDiscarded))
        {
            // A result that has since been moderation-held is never reused.
            Guid? resultId = await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.WorkflowExecutionId == done.ExecutionId && r.WorkflowStep.StepType == StepType.VideoAnalyze)
                .OrderByDescending(r => r.CompletedAt)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (resultId is { } id && await OutputGate.IsResultHeldAsync(_db, id, ct)) continue;
            return done;
        }

        return succeededOnly ? null : candidates.FirstOrDefault(r => r.Status == ObjectEffectRequest.StatusQueued);
    }

    internal Task<AgentDefinition?> FindPlaceholderAsync(CancellationToken ct) =>
        _db.AgentDefinitions
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .FirstOrDefaultAsync(ct);

    private static string ApiStatus(string rowStatus) => rowStatus switch
    {
        ObjectEffectRequest.StatusQueued => ObjectAnalysisStatus.Queued,
        ObjectEffectRequest.StatusFailed => ObjectAnalysisStatus.Failed,
        _ => ObjectAnalysisStatus.Succeeded
    };

    private async Task<PlanGateViolation?> PlanRefusalAsync(Project project, CancellationToken ct)
    {
        if (_entitlements is null) return null;
        OrganizationEntitlementSet plan = await _entitlements.GetAsync(project.OrganizationId, ct);
        return PlanGate.CheckSegmentation(plan);
    }

    private async Task<WorkflowPreflightResult> PreflightAsync(
        Project project, Source source, string label, ObjectMatchScope scope, int maxSeconds, ObjectSeed? seed, bool noWords, CancellationToken ct)
    {
        string json = ObjectAnalysisConfig.Serialize(ObjectAnalysisConfig.Build(SourceRef(source), label, scope, maxSeconds, seed, noWords));
        AgentDefinition? placeholder = await FindPlaceholderAsync(ct);

        // The probe must price what the engine reserves for the saved step: same agent, and a definition id that resolves to this
        // project so the source-footage add-on applies (the hint stands in for the workflow row that does not exist yet).
        Guid probeDefinition = Guid.NewGuid();
        WorkflowStep step = new()
        {
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = json,
            WorkflowDefinitionId = probeDefinition,
            AgentDefinitionId = placeholder?.Id ?? Guid.Empty
        };
        using IDisposable hint = SourceFootageDefinitions.Enter(probeDefinition, project.Id);
        return await _costEstimator!.PreflightAsync(project.OrganizationId, [step], ct);
    }

    public Task<ObjectAnalysisRefusal?> PeekRunRateAsync(ObjectAnalysisCaller caller, CancellationToken ct) =>
        CheckRunRateAsync(caller.Project.Id, caller.UserId, RunRateBucket.Paid, ct);

    /// <summary>
    /// Taps that found nothing are refunded, so they cost us a vendor call and the user nothing: cap them per user and project per
    /// rolling hour, before any vendor call. They still count toward the paid-runs gate (they are ordinary analysis runs).
    /// </summary>
    private sealed class TapCapExceededException(ObjectAnalysisRefusal refusal) : Exception
    {
        public ObjectAnalysisRefusal Refusal { get; } = refusal;
    }

    /// <summary>
    /// The only shape a seeded request's stored prompt can have: the label, a space, '#' and 8 lower-case hex digits. A typed prompt of
    /// this shape is refused, so typed words can never equal, reuse or be counted as a seeded row.
    /// </summary>
    internal static readonly System.Text.RegularExpressions.Regex SeededPromptShape = new(@" #[0-9a-f]{8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private async Task<ObjectAnalysisRefusal?> CheckNotFoundTapsAsync(Guid projectId, Guid userId, bool includesOwn, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime since = now.AddHours(-1);
        List<ObjectEffectRequest> recent = (await _db.ObjectEffectRequests
                .Where(r => r.ProjectId == projectId && r.CreatedByUserId == userId && (r.FailureCode == ObjectAnalysisMessages.ObjectNotFoundAtPoint || r.Status == ObjectEffectRequest.StatusQueued))
                .ToListAsync(ct))
            .Where(r => r.CreatedAt >= since && SeededPromptShape.IsMatch(r.Prompt))
            .ToList();
        foreach (ObjectEffectRequest row in recent.Where(r => r.Status == ObjectEffectRequest.StatusQueued)) await DeriveAsync(row, ct);

        // Taps that found nothing, plus taps still in flight (any of them may be a miss), so a burst cannot outrun the count.
        List<DateTime> misses = recent
            .Where(r => r.FailureCode == ObjectAnalysisMessages.ObjectNotFoundAtPoint || r.Status == ObjectEffectRequest.StatusQueued)
            .Select(r => r.CreatedAt)
            .ToList();
        if (misses.Count < MaxNotFoundTapsPerHour + (includesOwn ? 1 : 0)) return null;

        int retryAfter = Math.Max(1, (int)Math.Ceiling((misses.Min().AddHours(1) - now).TotalSeconds));
        return new ObjectAnalysisRefusal(StatusCodes.Status429TooManyRequests, new
        {
            code = RunRateRefusal.Code,
            message = $"Too many taps that found nothing: {misses.Count} of {MaxNotFoundTapsPerHour} in the last hour. Try again in {RunRateRefusal.Wait(retryAfter)}.",
            used = misses.Count,
            limit = MaxNotFoundTapsPerHour,
            windowMinutes = 60,
            retryAfterSeconds = retryAfter
        });
    }

    private async Task<ObjectAnalysisRefusal?> CheckRunRateAsync(Guid projectId, Guid userId, RunRateBucket bucket, CancellationToken ct)
    {
        int limit = bucket == RunRateBucket.Free ? 60 : PaidRunsPerHour;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime since = now.AddHours(-1);
        List<DateTime> recent = (await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.InitiatedByUserId == userId
                    && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.ObjectAnalysis
                    && e.WorkflowDefinition.Name != Follow.SubjectTrackService.WorkflowName)
                .Select(e => e.QueuedAt)
                .ToListAsync(ct))
            .Where(q => q is not null && q >= since)
            .Select(q => q!.Value)
            .ToList();
        if (recent.Count < limit) return null;

        int retryAfter = Math.Max(1, (int)Math.Ceiling((recent.Min().AddHours(1) - now).TotalSeconds));
        return new ObjectAnalysisRefusal(StatusCodes.Status429TooManyRequests, new
        {
            code = RunRateRefusal.Code,
            message = $"Run limit reached: {recent.Count} of {limit} object searches in this project in the last hour. Try again in {RunRateRefusal.Wait(retryAfter)}.",
            used = recent.Count,
            limit,
            windowMinutes = 60,
            retryAfterSeconds = retryAfter
        });
    }

    private ObjectAnalysisRefusal? Validate(string configJson)
    {
        CreateWorkflowStepRequest step = new(Guid.Empty, 1, null, null, StepType: nameof(StepType.VideoAnalyze), VideoAnalyzeConfigJson: configJson);
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(step, allowSeededTargets: true);
        if (errors.Count == 0) return null;
        _logger.LogError("The object-analysis step config failed validation: {Errors}", string.Join("; ", errors));
        return new ObjectAnalysisRefusal(StatusCodes.Status500InternalServerError, new { message = "The analysis could not be set up." });
    }

    internal static VideoSourceRef SourceRef(Source source) => source.Kind == ObjectEffectRequest.SourceFile
        ? new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: source.Id)
        : new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: source.Id);

    private static string TooLongMessage(Source source)
    {
        double fps = source.Fps is { } f && f > 30 ? f : 30;
        int length = (int)Math.Ceiling(source.DurationSec ?? 0);
        string shown = length >= 60 ? $"{length / 60} min {length % 60} s" : $"{length} s";
        int limitAtFps = (int)Math.Floor(ObjectAnalysisConfig.MaxSourceSeconds * 30.0 / fps);
        return $"This video is {shown} at {fps:0.##} frames per second, which is more than we can search for an object. "
            + $"At that frame rate the most is about {limitAtFps / 60} min {limitAtFps % 60} s (10 minutes at 30 fps). Use a shorter clip or a lower frame rate.";
    }

    /// <summary>The display name of an object named by a tap rather than by words.</summary>
    public const string SelectedObjectLabel = "Selected object";

    /// <summary>
    /// The words, or the seed instead of them. With a seed the words are optional (none = the model is sent no words and the
    /// label is only a display name); without one they are required exactly as before. A seeded request's stored prompt carries
    /// a short tag of the seed so two different taps in one video are never mistaken for the same search.
    /// </summary>
    private static ObjectAnalysisRefusal? ValidateTarget(string? raw, ObjectSeed? seed, out string rowPrompt, out string label, out bool noWords)
    {
        rowPrompt = label = string.Empty;
        noWords = false;
        if (seed is null)
        {
            ObjectAnalysisRefusal? refusal = ValidatePrompt(raw, out string prompt);
            rowPrompt = label = prompt;
            return refusal;
        }

        if (ObjectSeed.Validate(seed, null) is { } why) return BadRequest(new { code = ObjectSeed.InvalidCode, message = why });
        if (string.IsNullOrWhiteSpace(raw))
        {
            label = SelectedObjectLabel;
            noWords = true;
        }
        else if (ValidatePrompt(raw, out label) is { } badWords)
        {
            return badWords;
        }

        string tag = SeedTag(seed);
        string head = label.Length > ObjectEffectRequest.MaxPromptLength - tag.Length - 2
            ? label[..(ObjectEffectRequest.MaxPromptLength - tag.Length - 2)]
            : label;
        rowPrompt = $"{head} #{tag}";
        return null;
    }

    private static string SeedTag(ObjectSeed seed)
    {
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
        string text = string.Create(c, $"{seed.AtSec:0.###}|{seed.Point!.X:0.####},{seed.Point.Y:0.####}")
            + (seed.Box is { } b ? string.Create(c, $"|{b.X:0.####},{b.Y:0.####},{b.W:0.####},{b.H:0.####}") : string.Empty);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
    }

    /// <summary>A seed whose time is past the end of the video (the shape was checked before the video was looked up).</summary>
    private static ObjectAnalysisRefusal? SeedTimeRefusal(ObjectSeed? seed, Source source) =>
        seed is not null && ObjectSeed.Validate(seed, source.DurationSec) is { } why
            ? BadRequest(new { code = ObjectSeed.InvalidCode, message = why })
            : null;

    private static ObjectAnalysisRefusal? ValidatePrompt(string? raw, out string prompt)
    {
        prompt = string.Empty;
        string trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > ObjectEffectRequest.MaxPromptLength)
        {
            return BadRequest(new { code = "OBJECT_PROMPT_INVALID", message = $"Describe the object in 1 to {ObjectEffectRequest.MaxPromptLength} characters." });
        }

        if (trimmed.Any(char.IsControl))
        {
            return BadRequest(new { code = "OBJECT_PROMPT_INVALID", message = "The object description cannot contain control characters." });
        }

        prompt = SegmentationPrompt.Sanitize(trimmed);
        if (SeededPromptShape.IsMatch(prompt))
        {
            return BadRequest(new { code = "OBJECT_PROMPT_INVALID", message = "The object description cannot end with a '#' and eight hex digits." });
        }

        return prompt.Length == 0
            ? BadRequest(new { code = "OBJECT_PROMPT_INVALID", message = $"Describe the object in 1 to {ObjectEffectRequest.MaxPromptLength} characters." })
            : null;
    }

    private static ObjectAnalysisRefusal BadRequest(object body) => new(StatusCodes.Status400BadRequest, body);

    private static bool TryScope(string? raw, out ObjectMatchScope scope)
    {
        scope = ObjectMatchScope.AllInstances;
        return string.IsNullOrWhiteSpace(raw) || Enum.TryParse(raw, ignoreCase: true, out scope) && Enum.IsDefined(scope);
    }

    private static ObjectAnalysisRefusal BadScope() =>
        BadRequest(new { code = "OBJECT_SCOPE_INVALID", message = "matchScope must be AllInstances or SingleTrack." });

    private static ObjectAnalysisRefusal? ValidateIntent(ObjectAnalysisRequestBody? body, out string effect, out double strength, out int feather)
    {
        effect = Effects.FirstOrDefault(e => string.Equals(e, body?.Effect?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        strength = body?.Strength ?? 0;
        feather = body?.FeatherPx ?? 0;
        if (effect.Length == 0)
            return BadRequest(new { code = "OBJECT_EFFECT_INVALID", message = $"effect must be one of: {string.Join(", ", Effects)}." });
        if (!double.IsFinite(strength) || strength < 0 || strength > 1)
            return BadRequest(new { code = "OBJECT_EFFECT_INVALID", message = "strength must be between 0 and 1." });
        if (feather < 0 || feather > 100)
            return BadRequest(new { code = "OBJECT_EFFECT_INVALID", message = "featherPx must be between 0 and 100." });
        return null;
    }

    internal static bool TryParseRef(string mediaRef, out string kind, out Guid id)
    {
        kind = string.Empty;
        id = Guid.Empty;
        string[] parts = mediaRef.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out id)) return false;
        if (parts[0] == "file") kind = ObjectEffectRequest.SourceFile;
        else if (parts[0] == "step_result") kind = ObjectEffectRequest.SourceStepResult;
        else return false;
        return true;
    }

    /// <summary>The video a token names in this project: 404 when absent, foreign, quarantined or held; 422 when it is not a video.</summary>
    internal async Task<(Source?, ObjectAnalysisRefusal?)> FindSourceAsync(Guid projectId, string mediaRef, CancellationToken ct)
    {
        ObjectAnalysisRefusal notFound = new(StatusCodes.Status404NotFound, null);
        if (!TryParseRef(mediaRef, out string kind, out Guid id)) return (null, notFound);
        if (await OutputGate.IsOutputIdHeldAsync(_db, projectId, id, ct))
        {
            Microsoft.AspNetCore.Mvc.ObjectResult held = OutputGate.Unavailable();
            return (null, new ObjectAnalysisRefusal(held.StatusCode ?? StatusCodes.Status404NotFound, held.Value));
        }

        string storageKey;
        bool isVideo;
        if (kind == ObjectEffectRequest.SourceFile)
        {
            ProjectFile? file = await _db.ProjectFiles.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == projectId && f.ModerationStatus != FileModerationStatus.Quarantined, ct);
            if (file is null || file.CsamStatus == CsamScanStatus.Quarantined) return (null, notFound);
            storageKey = file.StorageKey;
            isVideo = file.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
        }
        else
        {
            string prefix = $"projects/{projectId}/outputFiles/";
            string? key = await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == id && r.WorkflowExecution.ProjectId == projectId
                    && r.OutputStorageKey != null && r.OutputStorageKey.StartsWith(prefix))
                .Select(r => r.OutputStorageKey).FirstOrDefaultAsync(ct);
            if (key is null) return (null, notFound);
            storageKey = key;
            isVideo = VideoExtensions.Contains(Path.GetExtension(key).ToLowerInvariant());
        }

        if (!isVideo)
        {
            return (null, new ObjectAnalysisRefusal(
                StatusCodes.Status422UnprocessableEntity, new { code = "OBJECT_SOURCE_NOT_VIDEO", message = "Objects can only be found in a video." }));
        }

        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(_db, projectId, [storageKey], ct);
        double? duration = probes.TryGetValue(storageKey, out MediaSourceProbe? probe) && probe.DurationSec > 0 ? probe.DurationSec : null;
        double? fps = probe is { FpsNum: > 0, FpsDen: > 0 } ? (double)probe.FpsNum / probe.FpsDen : null;
        return (new Source(kind, id, duration, fps), null);
    }
}
