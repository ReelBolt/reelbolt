using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Media;

/// <summary>
/// A refusal of the cleanup start or estimate: <see cref="Result"/> is exactly what the REST route answers; <see cref="Code"/>
/// and <see cref="Message"/> are the same refusal in words, for a caller that is not an HTTP route (the assistant tools).
/// </summary>
public sealed record AudioCleanupFailure(IActionResult Result, string Code, string Message, int? RetryAfterSeconds = null)
{
    public static AudioCleanupFailure From(IActionResult result, int? retryAfterSeconds = null)
    {
        if (result is ObjectResult { Value: { } value })
        {
            Type type = value.GetType();
            string? code = type.GetProperty("code")?.GetValue(value) as string;
            string? message = type.GetProperty("message")?.GetValue(value) as string ?? type.GetProperty("Message")?.GetValue(value) as string;
            return new AudioCleanupFailure(result, code ?? "REFUSED", message ?? "This file can't be cleaned.", retryAfterSeconds);
        }

        return new AudioCleanupFailure(result, "NOT_FOUND", "That file or project was not found.", retryAfterSeconds);
    }
}

/// <summary>One answer of the service: either the value or the failure.</summary>
public sealed record AudioCleanupResult<T>(T? Value, AudioCleanupFailure? Failure) where T : class
{
    public static AudioCleanupResult<T> Ok(T value) => new(value, null);

    public static AudioCleanupResult<T> Fail(IActionResult result, int? retryAfterSeconds = null) =>
        new(null, AudioCleanupFailure.From(result, retryAfterSeconds));

    public static AudioCleanupResult<T> Fail(AudioCleanupFailure failure) => new(null, failure);
}

/// <summary>
/// Audio cleanup start and estimate, shared by the REST route (<c>AudioCleanupController</c>) and the assistant tools.
/// The service probes the source length itself and writes <c>MaxSourceMinutes = ceil(minutes)</c> into the step config, so the
/// engine's credit hold, the estimate and the balance check are all <see cref="AudioCleanupCreditPricing.TryStepHoldMillisAsync"/>
/// of the SAME config. No answer carries a storage key, bucket or URL; every project or media failure is a 404.
/// </summary>
public class AudioCleanupService
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;
    private readonly IWorkflowExecutionService _executions;
    private readonly IAssistantExecutionGuard _guard;
    private readonly IInferenceProviderResolver _providers;
    private readonly IModelPriceService? _prices;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly INonWorkflowChargeService? _charges;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly TimeProvider _clock;
    private readonly IConfiguration? _configuration;

    public AudioCleanupService(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        ICurrentUser currentUser,
        IWorkflowExecutionService executions,
        IAssistantExecutionGuard guard,
        IInferenceProviderResolver providers,
        IModelPriceService? prices = null,
        IOrganizationEntitlements? entitlements = null,
        INonWorkflowChargeService? charges = null,
        IWorkflowCostEstimator? costEstimator = null,
        TimeProvider? clock = null,
        IConfiguration? configuration = null)
    {
        _configuration = configuration;
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
        _executions = executions;
        _guard = guard;
        _providers = providers;
        _prices = prices;
        _entitlements = entitlements;
        _charges = charges;
        _costEstimator = costEstimator;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The local source cap: <c>Cleanup:Local:MaxSourceMinutes</c> (default 60), never above the default.</summary>
    private int LocalMaxSourceMinutes => AudioCleanupCreditPricing.EffectiveMaxMinutes(
        stepMaxSourceMinutes: null,
        _configuration?.GetValue<int?>(AudioCleanupCreditPricing.LocalMaxSourceMinutesKey),
        AudioCleanupCreditPricing.DefaultLocalMaxSourceMinutes);

    private bool BillingOn => _costEstimator is { BillingEnabled: true } && _charges is not null;

    private static ObjectResult Code(int status, object body) => new(body) { StatusCode = status };

    private static BadRequestObjectResult Bad(object body) => new(body);

    /// <summary>Queue the cleanup. The value is the execution to poll.</summary>
    public async Task<AudioCleanupResult<AudioCleanupStartedResponse>> StartAsync(
        Guid projectId, string mediaRef, AudioCleanupRequestBody? body, CancellationToken ct, ProjectAccessResult? authorised = null)
    {
        ProjectAccessResult access = authorised ?? await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(denied);
        // A restore of this project is rewriting it: the hidden cleanup run waits for it like every run start.
        if (await RestoreRunGate.IsHeldAsync(_db, projectId, ct)) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(RestoreRunGate.Refusal());
        if (!TryReadBody(body, out AudioCleanupTarget target, out AudioCleanupOutputKind outputKind, out IActionResult? bad)) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(bad!);
        bool hosted = IsHosted(target);

        if (await IsHeldAsync(projectId, mediaRef, ct)) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable());
        MediaSource? source = await FindSourceAsync(projectId, mediaRef, ct);
        if (source is null) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(new NotFoundResult());
        if (!source.Analysable) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(Bad(new { code = "NOT_AUDIO_OR_VIDEO", message = "Only video and audio files can be cleaned." }));
        if (access.Project?.OrganizationId is not { } organizationId) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(new NotFoundResult());

        if (hosted)
        {
            if (EmailVerificationGate.Check(_currentUser) is { } unverified) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(unverified);
            if (await CheckPlanAsync(organizationId, ct) is { } notEntitled) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(notEntitled.ToResult());
            if (!body!.AllowSourceMediaEgress)
            {
                return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(Bad(new
                {
                    code = "EGRESS_REFUSED",
                    message = "This cleanup sends the audio to a hosted AI model. Confirm that the audio may leave this server and try again."
                }));
            }
        }

        if (!TryBuildConfig(source, target, outputKind, hosted, out AudioCleanupStepConfig? config, out IActionResult? invalid)) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(invalid!);
        string configJson = JsonSerializer.Serialize(config, AudioCleanupStepConfig.JsonOptions);

        long requiredMillis = 0;
        if (hosted && BillingOn)
        {
            long? price = await PriceAsync(configJson, ct);
            if (price is null)
                return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(new ConflictObjectResult(new { code = "CLEANUP_UNAVAILABLE", message = "Voice isolation isn't set up for this workspace yet." }));
            requiredMillis = price.Value;

            long balance = await _charges!.GetAvailableMillisAsync(organizationId, ct) ?? 0;
            if (requiredMillis > 0 && balance < requiredMillis)
            {
                return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(Code(StatusCodes.Status402PaymentRequired, new
                {
                    code = "INSUFFICIENT_CREDITS",
                    message = CreditShortfallMessage.Build(requiredMillis, null, balance),
                    missing = CreditShortfallMessage.MissingCredits(requiredMillis, balance)
                }));
            }
        }

        AgentDefinition? placeholder = await _db.AgentDefinitions
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .FirstOrDefaultAsync(ct);
        if (placeholder is null)
        {
            return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(Code(StatusCodes.Status500InternalServerError, new
            {
                message = "The VideoTransform placeholder agent is not seeded, so no deterministic step can be created."
            }));
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = $"Clean audio: {target}",
            Kind = WorkflowDefinitionKind.AudioCleanup,
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
            Label = $"Clean audio ({target})",
            StepType = StepType.AudioCleanup,
            AudioCleanupConfigJson = configJson,
            CacheMode = StepCacheMode.Never
        });

        // Hosted targets count in the paid hourly bucket, local ones in the free bucket (the classifier reads the config).
        Task<RunRateBucket> ClassifyUnderLock() => Task.FromResult(RunRateClassifier.Classify(workflow.Steps));

        WorkflowExecutionResponse? execution = null;
        PlanGateViolation? planRefusal = null;
        RunRateDecision decision = await _guard.TryAcquireAsync(_currentUser.UserId, projectId, ClassifyUnderLock, RunRateEvent.ExecuteSource, async () =>
        {
            // One transaction for the hidden workflow and its execution: a refused or failed start never leaves an orphan.
            await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct);
            bool committed = false;
            try
            {
                _db.WorkflowDefinitions.Add(workflow);
                await _db.SaveChangesAsync(ct);

                execution = await _executions.QueueAsync(
                    projectId, workflow.Id, _currentUser.UserId, userRequest: null, ExecutionInitiator.Human, ct,
                    regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.AudioCleanup, executionId: null,
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

                return execution.Id;
            }
            catch (PlanLimitException limit) when (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                planRefusal = limit.Violation;
                return null;
            }
            catch (Exception) when (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                throw;
            }
        }, ct);

        if (planRefusal is not null) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(planRefusal.ToResult());
        if (!decision.Allowed) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(RunRateLimited(decision));
        if (execution is null) return AudioCleanupResult<AudioCleanupStartedResponse>.Fail(Code(StatusCodes.Status500InternalServerError, new { message = "The cleanup could not be started." }));

        return AudioCleanupResult<AudioCleanupStartedResponse>.Ok(new AudioCleanupStartedResponse(
            execution.Id, target.ToString(), hosted, "queued", hosted && BillingOn ? requiredMillis / 1000.0 : hosted ? null : 0));
    }
    public async Task<AudioCleanupResult<AudioCleanupEstimateResponse>> EstimateAsync(
        Guid projectId, string mediaRef, AudioCleanupRequestBody? body, CancellationToken ct, ProjectAccessResult? authorised = null)
    {
        ProjectAccessResult access = authorised ?? await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return AudioCleanupResult<AudioCleanupEstimateResponse>.Fail(denied);
        if (!TryReadBody(body, out AudioCleanupTarget target, out AudioCleanupOutputKind outputKind, out IActionResult? bad)) return AudioCleanupResult<AudioCleanupEstimateResponse>.Fail(bad!);
        bool hosted = IsHosted(target);

        if (await IsHeldAsync(projectId, mediaRef, ct)) return AudioCleanupResult<AudioCleanupEstimateResponse>.Fail(ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable());
        MediaSource? source = await FindSourceAsync(projectId, mediaRef, ct);
        if (source is null) return AudioCleanupResult<AudioCleanupEstimateResponse>.Fail(new NotFoundResult());
        if (!source.Analysable) return AudioCleanupResult<AudioCleanupEstimateResponse>.Fail(Bad(new { code = "NOT_AUDIO_OR_VIDEO", message = "Only video and audio files can be cleaned." }));

        double? seconds = source.DurationSec is { } d ? Math.Round(d, 1) : null;
        if (!hosted && source.DurationSec is { } localSeconds && localSeconds > 0
            && !AudioCleanupCreditPricing.WithinCap(localSeconds, LocalMaxSourceMinutes))
        {
            // The start would refuse this source (413), so the estimate says so instead of promising a free run.
            return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(
                BillingOn, BillingOn ? 0 : null, seconds, null, false, false,
                $"Audio cleanup takes a source of up to {LocalMaxSourceMinutes} minutes. Trim the clip first.", false));
        }

        if (!BillingOn) return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(false, null, seconds, null, true, false, null, hosted));
        if (!hosted) return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(true, 0, seconds, null, true, false, null, false));
        if (access.Project?.OrganizationId is not { } organizationId) return AudioCleanupResult<AudioCleanupEstimateResponse>.Fail(new NotFoundResult());

        if (await CheckPlanAsync(organizationId, ct) is { } notEntitled)
            return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(true, null, seconds, null, false, false, notEntitled.Message, true));

        if (!TryBuildConfig(source, target, outputKind, hosted, out AudioCleanupStepConfig? config, out IActionResult? invalid))
            return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(true, null, seconds, null, false, false, AudioCleanupFailure.From(invalid!).Message, true));

        long? price = await PriceAsync(JsonSerializer.Serialize(config, AudioCleanupStepConfig.JsonOptions), ct);
        if (price is null)
            return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(true, null, seconds, null, false, false, "Voice isolation isn't set up for this workspace yet.", true));

        long balance = await _charges!.GetAvailableMillisAsync(organizationId, ct) ?? 0;
        return AudioCleanupResult<AudioCleanupEstimateResponse>.Ok(new AudioCleanupEstimateResponse(true, price.Value / 1000.0, seconds, balance / 1000.0, balance >= price.Value, false, null, true));
    }
    // ---------------------------------------------------------------------------------------------

    internal static bool IsHosted(AudioCleanupTarget target) => target is AudioCleanupTarget.IsolateVoice or AudioCleanupTarget.RemoveMusic;

    /// <summary>The ONE pricing call: the same function the engine's hold uses, over the same config JSON.</summary>
    private async Task<long?> PriceAsync(string configJson, CancellationToken ct) =>
        _prices is null ? null : await AudioCleanupCreditPricing.TryStepHoldMillisAsync(configJson, _providers, _prices, ct);

    private async Task<PlanGateViolation?> CheckPlanAsync(Guid organizationId, CancellationToken ct) =>
        _entitlements is null ? null : PlanGate.CheckAudioCleanup(await _entitlements.GetAsync(organizationId, ct));

    private static AudioCleanupFailure RunRateLimited(RunRateDecision decision)
    {
        ObjectResult result = new(new
        {
            message = RunRateRefusal.Sentence(decision),
            code = RunRateRefusal.Code,
            bucket = RunRateRefusal.BucketName(decision.Bucket),
            used = decision.Used,
            limit = decision.Limit,
            windowMinutes = decision.WindowSeconds / 60,
            retryAfterSeconds = decision.RetryAfterSeconds,
            busy = decision.Busy
        })
        { StatusCode = StatusCodes.Status429TooManyRequests };
        return AudioCleanupFailure.From(result, decision.RetryAfterSeconds);
    }

    internal static bool TryReadBody(
        AudioCleanupRequestBody? body, out AudioCleanupTarget target, out AudioCleanupOutputKind outputKind, out IActionResult? bad)
    {
        target = default;
        outputKind = AudioCleanupOutputKind.Auto;
        bad = null;
        if (body is null || string.IsNullOrWhiteSpace(body.Target)
            || !Enum.TryParse(body.Target.Trim(), ignoreCase: true, out target) || !Enum.IsDefined(target) || int.TryParse(body.Target.Trim(), out _))
        {
            bad = new BadRequestObjectResult(new
            {
                code = "INVALID_TARGET",
                message = "Name a target: VoiceBasic, Hum, Wind, Hiss, Echo, IsolateVoice or RemoveMusic."
            });
            return false;
        }

        if (!string.IsNullOrWhiteSpace(body.OutputKind)
            && (!Enum.TryParse(body.OutputKind.Trim(), ignoreCase: true, out outputKind) || !Enum.IsDefined(outputKind) || int.TryParse(body.OutputKind.Trim(), out _)))
        {
            bad = new BadRequestObjectResult(new { code = "INVALID_OUTPUT_KIND", message = "OutputKind must be Auto, Audio or VideoWithCleanAudio." });
            return false;
        }

        return true;
    }

    /// <summary>
    /// The step config the run executes. A hosted target carries <c>MaxSourceMinutes = ceil(minutes)</c> of the PROBED length so
    /// the hold is the real size; a source over the platform cap is refused here (413), before anything is queued or uploaded.
    /// </summary>
    private bool TryBuildConfig(
        MediaSource source, AudioCleanupTarget target, AudioCleanupOutputKind outputKind, bool hosted,
        out AudioCleanupStepConfig? config, out IActionResult? refusal)
    {
        config = null;
        refusal = null;
        int? maxMinutes = null;
        if (hosted)
        {
            if (source.DurationSec is not { } seconds || seconds <= 0)
            {
                refusal = new ConflictObjectResult(new
                {
                    code = "SOURCE_NOT_MEASURED",
                    message = "We haven't measured this file's length yet, so we can't say what the cleanup costs. Open it in the editor once and try again."
                });
                return false;
            }

            if (!AudioCleanupCreditPricing.WithinCap(seconds, AudioCleanupCreditPricing.DefaultMaxSourceMinutes))
            {
                refusal = new ObjectResult(new
                {
                    code = "SOURCE_TOO_LONG",
                    message = $"AI cleanup takes a source of up to {AudioCleanupCreditPricing.DefaultMaxSourceMinutes} minutes. Trim the clip first or use a free cleanup."
                })
                { StatusCode = StatusCodes.Status413PayloadTooLarge };
                return false;
            }

            maxMinutes = (int)AudioCleanupCreditPricing.BilledMinutes(seconds);
        }
        else if (source.DurationSec is { } localSeconds && localSeconds > 0
            && !AudioCleanupCreditPricing.WithinCap(localSeconds, LocalMaxSourceMinutes))
        {
            // A local cleanup is free, but it still copies and decodes the whole source on the worker:
            // the deployment's 60-minute default (Cleanup:Local:MaxSourceMinutes, only lowerable) bounds it.
            refusal = new ObjectResult(new
            {
                code = "AUDIO_SOURCE_TOO_LONG",
                message = $"Audio cleanup takes a source of up to {LocalMaxSourceMinutes} minutes. Trim the clip first."
            })
            { StatusCode = StatusCodes.Status413PayloadTooLarge };
            return false;
        }

        VideoSourceRef sourceRef = source.FileId is { } fileId
            ? new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileId)
            : new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: source.StepResultId);
        config = new AudioCleanupStepConfig(
            target, sourceRef, ProviderId: null, AllowSourceMediaEgress: hosted, RegisterProjectFile: true,
            OutputKind: outputKind, MaxSourceMinutes: maxMinutes);
        if (config.Validate() is { Count: > 0 } errors)
        {
            refusal = new BadRequestObjectResult(new { code = "INVALID_REQUEST", message = errors[0] });
            return false;
        }

        return true;
    }

    internal static AudioCleanupStepConfig? TryParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AudioCleanupStepConfig>(json, AudioCleanupStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static string? StringOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private async Task<bool> IsHeldAsync(Guid projectId, string mediaRef, CancellationToken ct)
    {
        string[] parts = mediaRef.Split(':', 2);
        return parts.Length == 2 && Guid.TryParse(parts[1], out Guid id)
            && await ReelBolt.Inference.Api.Services.Outputs.OutputGate.IsOutputIdHeldAsync(_db, projectId, id, ct);
    }

    /// <summary>The media a token names, for this project only (quarantined media is absent). Duration comes from the ingest probe.</summary>
    private async Task<MediaSource?> FindSourceAsync(Guid projectId, string mediaRef, CancellationToken ct)
    {
        string[] parts = mediaRef.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out Guid id)) return null;

        string? storageKey;
        string? mime;
        Guid? fileId = null;
        Guid? stepResultId = null;
        if (parts[0] == "file")
        {
            ProjectFile? file = await _db.ProjectFiles.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == projectId && f.ModerationStatus != FileModerationStatus.Quarantined, ct);
            if (file is null || file.CsamStatus == CsamScanStatus.Quarantined) return null;
            storageKey = file.StorageKey;
            mime = file.MimeType;
            fileId = file.Id;
        }
        else if (parts[0] == "step_result")
        {
            string prefix = $"projects/{projectId}/outputFiles/";
            storageKey = await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == id && r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null && r.OutputStorageKey.StartsWith(prefix))
                .Select(r => r.OutputStorageKey).FirstOrDefaultAsync(ct);
            if (storageKey is null) return null;
            stepResultId = id;
            mime = Path.GetExtension(storageKey).ToLowerInvariant() switch
            {
                ".mp4" or ".mov" or ".webm" or ".mkv" => "video/mp4",
                ".mp3" or ".wav" or ".m4a" or ".aac" => "audio/mpeg",
                _ => null
            };
        }
        else
        {
            return null;
        }

        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(_db, projectId, [storageKey], ct);
        double? duration = probes.TryGetValue(storageKey, out MediaSourceProbe? probe) && probe.DurationSec > 0 ? probe.DurationSec : null;
        bool analysable = mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
        return new MediaSource(fileId, stepResultId, analysable, duration);
    }

    private sealed record MediaSource(Guid? FileId, Guid? StepResultId, bool Analysable, double? DurationSec);
}
