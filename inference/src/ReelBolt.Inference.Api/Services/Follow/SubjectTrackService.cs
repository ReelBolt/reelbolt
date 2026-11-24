using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
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
using ReelBolt.Shared.Workflows.Follow;

namespace ReelBolt.Inference.Api.Services.Follow;

/// <summary>
/// "Follow this subject" tracking: queues a HIDDEN one-step <see cref="WorkflowDefinitionKind.ObjectAnalysis"/> run whose only step is a
/// <see cref="StepType.VideoAnalyze"/> with one placed <see cref="ObjectTrackMethod.Feature"/> target (the free local tracker: no vendor,
/// no model, nothing leaves the host), and reads its result back. There is no <see cref="ObjectEffectRequest"/> row: every sweeper and
/// finalizer of the kind is driven by those rows, so this run is invisible to them. The run is priced compute-only and counts against
/// the free run-rate bucket.
/// </summary>
public sealed class SubjectTrackService
{
    /// <summary>The longest source the local tracker is asked to follow, seconds.</summary>
    public const double MaxSourceSeconds = 3600;

    /// <summary>Most boxes a status answer carries.</summary>
    public const int MaxBoxes = 2000;

    /// <summary>The hidden workflow's name; the run-rate counts of follow runs and paid object searches are told apart by it.</summary>
    public const string WorkflowName = "Follow subject";

    public const string StatusQueued = "queued";
    public const string StatusRunning = "running";
    public const string StatusSucceeded = "succeeded";
    public const string StatusFailed = "failed";

    public const string NotFoundCode = "FOLLOW_SUBJECT_NOT_FOUND";
    public const string NotFoundMessage = "Could not follow that subject. Try a tighter box on a clear frame.";

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly InferenceApiDbContext _db;
    private readonly IWorkflowExecutionService _executions;
    private readonly IFileStorageService _storage;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly ObjectAnalysisService _sources;

    public SubjectTrackService(
        InferenceApiDbContext db,
        IWorkflowExecutionService executions,
        IFileStorageService storage,
        ILogger<SubjectTrackService> logger,
        TimeProvider? clock = null,
        IWorkflowCostEstimator? costEstimator = null)
    {
        _db = db;
        _executions = executions;
        _storage = storage;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _costEstimator = costEstimator;
        _sources = new ObjectAnalysisService(db, executions, storage, logger, _clock, null, costEstimator);
    }

    private bool BillingOn => _costEstimator is { BillingEnabled: true };

    // ------------------------------------------------------------------------------------------------ request

    public async Task<ObjectAnalysisResult<SubjectTrackQueuedResponse>> RequestAsync(
        ObjectAnalysisCaller caller, string mediaRef, SubjectTrackRequestBody? body, CancellationToken ct)
    {
        if (!caller.EmailVerified)
        {
            return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status403Forbidden,
                new { code = EmailVerificationGate.ErrorCode, message = EmailVerificationGate.Message });
        }

        Project project = caller.Project;
        Guid projectId = project.Id;

        if (ValidateBody(body) is { } invalid) return ObjectAnalysisResult<SubjectTrackQueuedResponse>.Refuse(invalid);

        (ObjectAnalysisService.Source? source, ObjectAnalysisRefusal? sourceFailure) = await _sources.FindSourceAsync(projectId, mediaRef, ct);
        if (sourceFailure is not null) return ObjectAnalysisResult<SubjectTrackQueuedResponse>.Refuse(sourceFailure);

        if (source!.DurationSec is { } duration)
        {
            if (duration > MaxSourceSeconds)
            {
                return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status422UnprocessableEntity, new
                {
                    code = "FOLLOW_SOURCE_TOO_LONG",
                    message = "This video is longer than 1 hour, which is more than we can follow a subject through. Use a shorter clip."
                });
            }

            if (body!.AtSec > duration)
            {
                return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status400BadRequest, new
                {
                    code = "FOLLOW_TIME_INVALID",
                    message = "The chosen moment is after the end of the video."
                });
            }
        }

        string configJson = Serialize(BuildConfig(ObjectAnalysisService.SourceRef(source), body!));
        CreateWorkflowStepRequest step = new(Guid.Empty, 1, null, null, StepType: nameof(StepType.VideoAnalyze), VideoAnalyzeConfigJson: configJson);
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(step);
        if (errors.Count > 0)
        {
            _logger.LogError("The follow-subject step config failed validation: {Errors}", string.Join("; ", errors));
            return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status500InternalServerError, new { message = "The subject could not be set up for following." });
        }

        // A restore of this project is rewriting it: no run starts until it is done.
        if (await RestoreRunGate.IsHeldAsync(_db, projectId, ct))
        {
            return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status409Conflict, new
            {
                code = ProjectHistoryRestoreCodes.RestoreInProgress,
                message = RestoreRunGate.Message
            });
        }

        // The same request already in flight: answer it, no new run.
        if (await FindInFlightAsync(projectId, configJson, ct) is { } existing)
        {
            return ObjectAnalysisResult<SubjectTrackQueuedResponse>.Ok(
                new SubjectTrackQueuedResponse(existing.Id, existing.Status == ExecutionStatus.Running ? StatusRunning : StatusQueued),
                StatusCodes.Status202Accepted);
        }

        WorkflowStep probe = new() { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = configJson };

        if (BillingOn)
        {
            WorkflowPreflightResult price = await PreflightAsync(project, probe, ct);
            if (price.Refused)
            {
                return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status402PaymentRequired, new
                {
                    code = "INSUFFICIENT_CREDITS",
                    message = CreditShortfallMessage.Build(price.Estimate.RequiredMillis, null, price.BalanceMillis),
                    missing = CreditShortfallMessage.MissingCredits(price.Estimate.RequiredMillis, price.BalanceMillis)
                });
            }
        }

        RunRateBucket bucket = RunRateClassifier.Classify([probe]);
        if (await CheckRunRateAsync(projectId, caller.UserId, bucket, ct) is { } limited)
            return ObjectAnalysisResult<SubjectTrackQueuedResponse>.Refuse(limited);

        AgentDefinition? placeholder = await _sources.FindPlaceholderAsync(ct);
        if (placeholder is null)
        {
            return Refuse<SubjectTrackQueuedResponse>(StatusCodes.Status500InternalServerError, new
            {
                message = "The VideoTransform placeholder agent is not seeded, so no tracking step can be created."
            });
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = WorkflowName,
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
            Label = "Follow subject",
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = configJson
        });

        Guid executionId = Guid.NewGuid();

        // One transaction for the hidden workflow and the execution: a refused start (the plan's concurrency cap) rolls both back.
        await using (IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            bool committed = false;
            try
            {
                _db.WorkflowDefinitions.Add(workflow);
                await _db.SaveChangesAsync(ct);

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
                if (ex is PlanLimitException limit)
                    return Refuse<SubjectTrackQueuedResponse>(limit.Violation.StatusCode, limit.Violation.ToBody());
                throw;
            }
        }

        return ObjectAnalysisResult<SubjectTrackQueuedResponse>.Ok(
            new SubjectTrackQueuedResponse(executionId, StatusQueued), StatusCodes.Status202Accepted);
    }

    /// <summary>The step config of a follow run: everything off but one placed feature target, and no egress.</summary>
    public static VideoAnalyzeStepConfig BuildConfig(VideoSourceRef source, SubjectTrackRequestBody body)
    {
        SubjectTrackBoxBody b = body.Box!;
        // The engine's placed Region order is TL, TR, BL, BR (like every insert quad).
        VideoInsertSurfaceQuad region = new(b.X, b.Y, b.X + b.W, b.Y, b.X, b.Y + b.H, b.X + b.W, b.Y + b.H);
        return new VideoAnalyzeStepConfig(
            Version: 1,
            Source: source,
            DetectSilence: false,
            DetectShots: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: false,
            AnalyzeAudioLevels: false,
            DetectLetterbox: false,
            DetectNearDuplicates: false,
            Vision: VideoVisionMode.Off,
            DerushPrePass: VideoDerushPrePassMode.Off,
            ScreenInstructionLikeSpans: false,
            LocateSubjects: false,
            TrackObjects: [new ObjectTrackTarget("subject", AtSec: body.AtSec, Region: region, Method: ObjectTrackMethod.Feature)],
            AllowSourceMediaEgress: false);
    }

    public static string Serialize(VideoAnalyzeStepConfig config) => JsonSerializer.Serialize(config, ConfigJson);

    private static ObjectAnalysisRefusal? ValidateBody(SubjectTrackRequestBody? body)
    {
        if (body is null || body.Box is null)
        {
            return new ObjectAnalysisRefusal(StatusCodes.Status400BadRequest, new { code = "FOLLOW_BOX_INVALID", message = "Draw a box around the subject to follow." });
        }

        if (!double.IsFinite(body.AtSec) || body.AtSec < 0)
        {
            return new ObjectAnalysisRefusal(StatusCodes.Status400BadRequest,
                new { code = "FOLLOW_TIME_INVALID", message = "The chosen moment must be zero or later." });
        }

        SubjectTrackBoxBody b = body.Box;
        const double eps = 1e-9;
        bool finite = double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.W) && double.IsFinite(b.H);
        if (!finite || b.W < 0.01 || b.H < 0.01 || b.X < 0 || b.Y < 0 || b.X + b.W > 1 + eps || b.Y + b.H > 1 + eps)
        {
            return new ObjectAnalysisRefusal(StatusCodes.Status400BadRequest, new
            {
                code = "FOLLOW_BOX_INVALID",
                message = "The box must be inside the picture and at least 1% wide and tall."
            });
        }

        return null;
    }

    private static ObjectAnalysisResult<T> Refuse<T>(int status, object body) => ObjectAnalysisResult<T>.Refuse(status, body);

    private async Task<WorkflowExecution?> FindInFlightAsync(Guid projectId, string configJson, CancellationToken ct) =>
        await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.ProjectId == projectId
                && (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running)
                && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.ObjectAnalysis
                && e.WorkflowDefinition.Steps.Any(s => s.VideoAnalyzeConfigJson == configJson))
            .OrderBy(e => e.QueuedAt)
            .FirstOrDefaultAsync(ct);

    private async Task<WorkflowPreflightResult> PreflightAsync(Project project, WorkflowStep probe, CancellationToken ct)
    {
        AgentDefinition? placeholder = await _sources.FindPlaceholderAsync(ct);
        // The probe must price what the engine reserves for the saved step: same agent, and a definition id that resolves to this
        // project so the source-footage add-on applies (the hint stands in for the workflow row that does not exist yet).
        Guid probeDefinition = Guid.NewGuid();
        WorkflowStep step = new()
        {
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = probe.VideoAnalyzeConfigJson,
            WorkflowDefinitionId = probeDefinition,
            AgentDefinitionId = placeholder?.Id ?? Guid.Empty
        };
        using IDisposable hint = SourceFootageDefinitions.Enter(probeDefinition, project.Id);
        return await _costEstimator!.PreflightAsync(project.OrganizationId, [step], ct);
    }

    private async Task<ObjectAnalysisRefusal?> CheckRunRateAsync(Guid projectId, Guid userId, RunRateBucket bucket, CancellationToken ct)
    {
        int limit = bucket == RunRateBucket.Free ? 60 : ObjectAnalysisService.PaidRunsPerHour;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime since = now.AddHours(-1);
        List<DateTime> recent = (await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.InitiatedByUserId == userId
                    && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.ObjectAnalysis
                    && e.WorkflowDefinition.Name == WorkflowName)
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
            message = $"Run limit reached: {recent.Count} of {limit} subject searches in this project in the last hour. Try again in {RunRateRefusal.Wait(retryAfter)}.",
            used = recent.Count,
            limit,
            windowMinutes = 60,
            retryAfterSeconds = retryAfter
        });
    }

    // ------------------------------------------------------------------------------------------------ status

    public async Task<ObjectAnalysisResult<SubjectTrackStatusResponse>> GetAsync(
        Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        ObjectAnalysisResult<SubjectTrackStatusResponse> notFound = ObjectAnalysisResult<SubjectTrackStatusResponse>.Refuse(StatusCodes.Status404NotFound);
        if (!ObjectAnalysisService.TryParseRef(mediaRef, out string kind, out Guid sourceId)) return notFound;

        var run = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ProjectId == projectId && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.ObjectAnalysis)
            .Select(e => new
            {
                e.Status,
                Config = e.WorkflowDefinition.Steps
                    .Where(s => s.StepType == StepType.VideoAnalyze)
                    .Select(s => s.VideoAnalyzeConfigJson)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);
        if (run is null || !IsFollowConfigFor(run.Config, kind, sourceId)) return notFound;

        switch (run.Status)
        {
            case ExecutionStatus.Queued:
                return Status(executionId, StatusQueued);
            case ExecutionStatus.Running:
                return Status(executionId, StatusRunning);
            case ExecutionStatus.Cancelled:
                return Failed(executionId, "RUN_CANCELLED", "The tracking was cancelled.", null);
        }

        var result = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId && r.WorkflowStep.StepType == StepType.VideoAnalyze)
            .OrderByDescending(r => r.CompletedAt)
            .Select(r => new { r.Id, r.Status, r.FailureCode, r.OutputJson, r.ArtifactStorageKey })
            .FirstOrDefaultAsync(ct);

        if (run.Status == ExecutionStatus.Failed || result is null || result.Status == StepStatus.Failed)
        {
            string? code = result?.FailureCode ?? ObjectAnalysisService.ErrorCodeOf(result?.OutputJson);
            return Failed(executionId, code ?? "TRACKING_FAILED", "We could not follow that subject. Try again in a few minutes.", result?.Id);
        }

        if (await OutputGate.IsResultHeldAsync(_db, result.Id, ct))
            return Failed(executionId, "RESULT_UNAVAILABLE", "The result of this tracking is not available. Try following the subject again.", result.Id);

        (VideoAnalysisArtifact? artifact, bool readFailed) = await ReadArtifactAsync(projectId, result.ArtifactStorageKey, ct);
        // A transient read error is not a verdict: stay running so the next read retries.
        if (readFailed) return Status(executionId, StatusRunning);

        VideoInsertRegionTrack? best = (artifact?.InsertRegions ?? [])
            .Where(t => string.Equals(t.Method, "feature", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(t.Id) && double.IsFinite(t.StartSec) && double.IsFinite(t.EndSec))
            .OrderByDescending(t => t.Keyframes?.Count ?? 0)
            .FirstOrDefault();
        List<SubjectSample> present = best is null
            ? []
            : SubjectPathAdapters.FromInsertTrack(best).Where(s => s.Present).ToList();
        if (best is null || present.Count == 0) return Failed(executionId, NotFoundCode, NotFoundMessage, result.Id);

        List<SubjectTrackBoxResponse> boxes = Decimate(present, MaxBoxes)
            .Select(s => new SubjectTrackBoxResponse(s.TimeSec, s.X, s.Y, s.Width, s.Height))
            .ToList();
        return ObjectAnalysisResult<SubjectTrackStatusResponse>.Ok(new SubjectTrackStatusResponse(
            executionId, StatusSucceeded, null, null, result.Id, new SubjectTrackResponse(best.Id, best.StartSec, best.EndSec, boxes)));
    }

    /// <summary>At most <paramref name="max"/> items, evenly spaced, first and last kept.</summary>
    public static IReadOnlyList<T> Decimate<T>(IReadOnlyList<T> items, int max)
    {
        if (items.Count <= max) return items;
        List<T> kept = new(max);
        for (int i = 0; i < max; i++) kept.Add(items[(int)((long)i * (items.Count - 1) / (max - 1))]);
        return kept;
    }

    private static ObjectAnalysisResult<SubjectTrackStatusResponse> Status(Guid executionId, string status) =>
        ObjectAnalysisResult<SubjectTrackStatusResponse>.Ok(new SubjectTrackStatusResponse(executionId, status, null, null, null, null));

    private static ObjectAnalysisResult<SubjectTrackStatusResponse> Failed(Guid executionId, string code, string message, Guid? stepResultId) =>
        ObjectAnalysisResult<SubjectTrackStatusResponse>.Ok(new SubjectTrackStatusResponse(executionId, StatusFailed, code, message, stepResultId, null));

    /// <summary>True when the step config is a follow config (one placed feature target) over the media the route names.</summary>
    private static bool IsFollowConfigFor(string? json, string kind, Guid sourceId)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            VideoAnalyzeStepConfig? config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(json, ConfigJson);
            if (config?.TrackObjects is not { Count: 1 } targets) return false;
            ObjectTrackTarget target = targets[0];
            if (target is null || target.Method != ObjectTrackMethod.Feature || target.Region is null) return false;
            return kind == ObjectEffectRequest.SourceFile
                ? config.Source.Kind == VideoSourceKind.ProjectFile && config.Source.ProjectFileId == sourceId
                : config.Source.Kind == VideoSourceKind.StepResult && config.Source.StepResultId == sourceId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(VideoAnalysisArtifact? Artifact, bool ReadFailed)> ReadArtifactAsync(Guid projectId, string? key, CancellationToken ct)
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
            return (await SegmentationTrackIdLookup.ParseAsync(stream, ct), false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the video-analysis artifact of project {ProjectId}", projectId);
            return (null, true);
        }
    }
}
