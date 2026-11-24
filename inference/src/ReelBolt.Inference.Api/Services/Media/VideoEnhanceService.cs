using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Media;

/// <summary>Who is asking, resolved by the entry once (the project through <c>IProjectAccessService</c>).</summary>
public sealed record VideoEnhanceCaller(Guid UserId, Guid OrganizationId, Project Project, bool EmailVerified);

/// <summary>A refusal in transport-neutral form: the status the entry answers with and the exact body (null for a bare 404).</summary>
public sealed record VideoEnhanceRefusal(int StatusCode, object? Body);

/// <summary>A value or a refusal. <see cref="SuccessStatus"/> is 200, or 202 when a run was queued or a stop was requested.</summary>
public sealed record VideoEnhanceResult<T>(T? Value, VideoEnhanceRefusal? Refusal, int SuccessStatus = 200)
{
    public bool Succeeded => Refusal is null;

    public static VideoEnhanceResult<T> Ok(T value, int status = 200) => new(value, null, status);

    public static VideoEnhanceResult<T> Refuse(int status, object? body = null) => new(default, new VideoEnhanceRefusal(status, body));

    public static VideoEnhanceResult<T> Refuse(VideoEnhanceRefusal refusal) => new(default, refusal);
}

/// <summary>
/// "Enhance this video", for any entry (the editor's routes and, later, the assistant): the plan gate, the consent, the length and
/// price checks, the hidden one-step run, the status derived from it and the stop. Behaviour is the controller's, kept here so the
/// assistant shares it.
/// </summary>
public interface IVideoEnhanceService
{
    Task<VideoEnhanceResult<VideoEnhanceEstimate>> EstimateAsync(VideoEnhanceCaller caller, string mediaRef, VideoEnhanceRequestBody? body, CancellationToken ct);

    Task<VideoEnhanceResult<VideoEnhanceQueuedResponse>> RequestAsync(VideoEnhanceCaller caller, string mediaRef, VideoEnhanceRequestBody? body, CancellationToken ct);

    Task<VideoEnhanceResult<VideoEnhanceStatusResponse>> GetAsync(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct);

    Task<VideoEnhanceResult<object>> CancelAsync(Guid projectId, string mediaRef, Guid executionId, Guid userId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class VideoEnhanceService : IVideoEnhanceService
{
    /// <summary>Paid (hosted) enhancements per user per project per rolling hour.</summary>
    public const int PaidRunsPerHour = 10;

    /// <summary>Free (local) enhancements per user per project per rolling hour.</summary>
    public const int FreeRunsPerHour = 60;

    public const string ConsentRequiredCode = "CONSENT_REQUIRED";
    public const string SourceTooLongCode = "SOURCE_TOO_LONG";
    public const string SourceTooShortCode = "SOURCE_TOO_SHORT";
    public const string SourceNotMeasuredCode = "SOURCE_NOT_MEASURED";
    public const string UnavailableCode = "ENHANCE_UNAVAILABLE";
    public const string ResolutionAbovePlanCode = "RESOLUTION_ABOVE_PLAN";

    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".mkv"];

    private readonly InferenceApiDbContext _db;
    private readonly IWorkflowExecutionService _executions;
    private readonly IInferenceProviderResolver? _providers;
    private readonly IModelPriceService? _prices;
    private readonly IPublishEndpoint? _publish;
    private readonly ILogger<VideoEnhanceService> _logger;
    private readonly TimeProvider _clock;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly VideoEnhanceHostedSettings _settings;

    public VideoEnhanceService(
        InferenceApiDbContext db,
        IWorkflowExecutionService executions,
        ILogger<VideoEnhanceService> logger,
        IInferenceProviderResolver? providers = null,
        IModelPriceService? prices = null,
        IPublishEndpoint? publish = null,
        TimeProvider? clock = null,
        IOrganizationEntitlements? entitlements = null,
        IWorkflowCostEstimator? costEstimator = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _settings = VideoEnhanceHostedSettings.From(configuration);
        _db = db;
        _executions = executions;
        _logger = logger;
        _providers = providers;
        _prices = prices;
        _publish = publish;
        _clock = clock ?? TimeProvider.System;
        _entitlements = entitlements;
        _costEstimator = costEstimator;
    }

    private bool BillingOn => _costEstimator is { BillingEnabled: true };

    private sealed record Source(string Kind, Guid Id, MediaSourceProbe? Probe)
    {
        public double? DurationSec => Probe is { DurationSec: > 0 } p ? p.DurationSec : null;

        public double? Fps => Probe is { FpsNum: > 0, FpsDen: > 0 } p ? (double)p.FpsNum / p.FpsDen : null;

        public bool Measured => DurationSec is not null && Fps is not null && Probe is { Width: > 0, Height: > 0 };
    }

    /// <summary>Everything the gates decided: the config to queue (or the first reason it cannot be), and the facts the answer shows.</summary>
    private sealed record Plan(
        VideoEnhanceStepConfig Config,
        string ConfigJson,
        VideoEnhanceSizing.Output Output,
        int Seconds,
        int MaxSeconds,
        bool Consented,
        int MaxOutputHeight,
        VideoEnhanceRefusal? Refusal,
        string? Code,
        string? Reason);

    // ------------------------------------------------------------------------------------------------ estimate

    public async Task<VideoEnhanceResult<VideoEnhanceEstimate>> EstimateAsync(
        VideoEnhanceCaller caller, string mediaRef, VideoEnhanceRequestBody? body, CancellationToken ct)
    {
        if (ValidateShape(body) is { } bad) return VideoEnhanceResult<VideoEnhanceEstimate>.Refuse(bad);

        (Source? source, VideoEnhanceRefusal? sourceFailure) = await FindSourceAsync(caller.Project.Id, mediaRef, ct);
        if (sourceFailure is not null) return VideoEnhanceResult<VideoEnhanceEstimate>.Refuse(sourceFailure);

        Plan plan = await PlanAsync(caller.Project, source!, body!, ct);
        bool hosted = plan.Config.IsHosted;
        VideoEnhanceSizing.Output output = plan.Output;
        long bytes = VideoEnhanceSizing.EstimatedBytes(output, source!.DurationSec ?? 0);
        int processing = VideoEnhanceSizing.EstimatedProcessingSeconds(plan.Config, source.DurationSec ?? 0, output);

        double credits = 0;
        double balance = 0;
        bool affordable = plan.Refusal is null;
        string? reason = plan.Reason;
        string? code = plan.Code;
        if (BillingOn && plan.Refusal is null && hosted)
        {
            WorkflowPreflightResult price = await PreflightAsync(caller.Project, plan.ConfigJson, ct);
            credits = price.Estimate.RequiredMillis / 1000.0;
            balance = price.BalanceMillis / 1000.0;
            affordable = !price.Refused;
        }
        else if (BillingOn)
        {
            balance = (await PreflightAsync(caller.Project, plan.ConfigJson, ct)).BalanceMillis / 1000.0;
        }

        return VideoEnhanceResult<VideoEnhanceEstimate>.Ok(new VideoEnhanceEstimate(
            BillingOn, hosted, credits, plan.Seconds, balance, affordable && plan.Refusal is null, reason, code, plan.Consented, plan.MaxOutputHeight,
            output.Width, output.Height, Math.Round(output.Fps, 3), bytes, processing, plan.MaxSeconds));
    }

    // ------------------------------------------------------------------------------------------------ request

    public async Task<VideoEnhanceResult<VideoEnhanceQueuedResponse>> RequestAsync(
        VideoEnhanceCaller caller, string mediaRef, VideoEnhanceRequestBody? body, CancellationToken ct)
    {
        // A run like any other: an unverified address cannot start one.
        if (!caller.EmailVerified)
        {
            return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(new VideoEnhanceRefusal(
                StatusCodes.Status403Forbidden, new { code = EmailVerificationGate.ErrorCode, message = EmailVerificationGate.Message }));
        }

        Project project = caller.Project;

        // 400 shape -> 404 media -> 402 plan -> 409 consent -> 413 too long -> 409 unavailable (all inside the plan) ...
        if (ValidateShape(body) is { } bad) return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(bad);

        (Source? source, VideoEnhanceRefusal? sourceFailure) = await FindSourceAsync(project.Id, mediaRef, ct);
        if (sourceFailure is not null) return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(sourceFailure);

        Plan plan = await PlanAsync(project, source!, body!, ct);
        if (plan.Refusal is not null) return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(plan.Refusal);

        // The final config is the one the engine will validate and hold on; a failure here is ours, never the caller's.
        CreateWorkflowStepRequest check = new(Guid.Empty, 1, null, null, StepType: nameof(StepType.VideoEnhance), VideoEnhanceConfigJson: plan.ConfigJson);
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(check);
        if (errors.Count > 0)
        {
            _logger.LogError("The video-enhance step config failed validation: {Errors}", string.Join("; ", errors));
            return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(StatusCodes.Status500InternalServerError, new { message = "The enhancement could not be set up." });
        }

        // ... 402 credits ...
        double credits = 0;
        if (BillingOn && plan.Config.IsHosted)
        {
            WorkflowPreflightResult price = await PreflightAsync(project, plan.ConfigJson, ct);
            credits = price.Estimate.RequiredMillis / 1000.0;
            if (price.Refused)
            {
                return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(new VideoEnhanceRefusal(StatusCodes.Status402PaymentRequired, new
                {
                    code = "INSUFFICIENT_CREDITS",
                    message = CreditShortfallMessage.Build(price.Estimate.RequiredMillis, null, price.BalanceMillis),
                    missing = CreditShortfallMessage.MissingCredits(price.Estimate.RequiredMillis, price.BalanceMillis)
                }));
            }
        }

        // ... 429 run rate (and the plan's concurrency inside the queue) ...
        WorkflowStep probe = new() { StepOrder = 1, StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = plan.ConfigJson };
        RunRateBucket bucket = RunRateClassifier.Classify([probe]);
        if (await CheckRunRateAsync(project.Id, caller.UserId, bucket, ct) is { } limited)
            return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(limited);

        AgentDefinition? placeholder = await _db.AgentDefinitions
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .FirstOrDefaultAsync(ct);
        if (placeholder is null)
        {
            return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(StatusCodes.Status500InternalServerError, new
            {
                message = "The VideoTransform placeholder agent is not seeded, so no enhancement step can be created."
            });
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = $"Enhance video: {VideoEnhanceLabel(plan.Config)}",
            Kind = WorkflowDefinitionKind.VideoEnhance,
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
            Label = "Enhance video",
            StepType = StepType.VideoEnhance,
            VideoEnhanceConfigJson = plan.ConfigJson
        });

        Guid executionId = Guid.NewGuid();

        // One transaction for the hidden workflow and the execution: a refused or failed start (the plan's concurrency cap, a database
        // error) rolls both back. QueueAsync commits through beforePublish.
        await using (IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            bool committed = false;
            try
            {
                _db.WorkflowDefinitions.Add(workflow);
                await _db.SaveChangesAsync(ct);

                await _executions.QueueAsync(
                    project.Id, workflow.Id, caller.UserId, userRequest: null, ExecutionInitiator.Human, ct,
                    regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.VideoEnhance, executionId,
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
                    return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Refuse(new VideoEnhanceRefusal(limit.Violation.StatusCode, limit.Violation.ToBody()));
                throw;
            }
        }

        return VideoEnhanceResult<VideoEnhanceQueuedResponse>.Ok(
            new VideoEnhanceQueuedResponse(executionId, plan.Config.Op.ToString(), plan.Config.Engine.ToString(), plan.Config.IsHosted, "queued", credits),
            StatusCodes.Status202Accepted);
    }

    // ------------------------------------------------------------------------------------------------ the gates

    /// <summary>Parses and checks the body's own shape (400). The source, the provider and the consent are not the body's.</summary>
    private static VideoEnhanceRefusal? ValidateShape(VideoEnhanceRequestBody? body)
    {
        if (body is null) return Invalid("The request body is required.");
        if (TryBuild(body, new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid()), out VideoEnhanceStepConfig? config, out string? parseError) is false)
            return Invalid(parseError!);

        List<string> errors = config!.Validate();
        return errors.Count == 0 ? null : Invalid(string.Join(" ", errors));
    }

    private static VideoEnhanceRefusal Invalid(string message) =>
        new(StatusCodes.Status400BadRequest, new { code = "ENHANCE_REQUEST_INVALID", message });

    private static bool TryBuild(VideoEnhanceRequestBody body, VideoSourceRef source, out VideoEnhanceStepConfig? config, out string? error)
    {
        config = null;
        error = null;
        if (!Enum.TryParse(body.Op, ignoreCase: true, out VideoEnhanceOp op) || !Enum.IsDefined(op))
        {
            error = "op must be Upscale, Interpolate, Denoise, MotionBlur or Extend.";
            return false;
        }

        if (!Enum.TryParse(body.Engine, ignoreCase: true, out VideoEnhanceEngine engine) || !Enum.IsDefined(engine))
        {
            error = "engine must be Local or Hosted.";
            return false;
        }

        VideoEnhanceDenoiseStrength? strength = null;
        if (!string.IsNullOrWhiteSpace(body.DenoiseStrength))
        {
            if (!Enum.TryParse(body.DenoiseStrength, ignoreCase: true, out VideoEnhanceDenoiseStrength parsed) || !Enum.IsDefined(parsed))
            {
                error = "denoiseStrength must be Light or Strong.";
                return false;
            }

            strength = parsed;
        }

        config = new VideoEnhanceStepConfig(
            op, engine, source,
            UpscaleFactor: body.UpscaleFactor,
            TargetHeight: body.TargetHeight,
            FrameMultiplier: body.FrameMultiplier,
            DenoiseStrength: strength,
            KeepGrain: body.KeepGrain ?? false,
            ShutterAngle: body.ShutterAngle,
            TargetAspect: string.IsNullOrWhiteSpace(body.TargetAspect) ? null : body.TargetAspect.Trim(),
            MarginLeft: body.MarginLeft,
            MarginRight: body.MarginRight,
            MarginTop: body.MarginTop,
            MarginBottom: body.MarginBottom,
            Prompt: body.Prompt);
        return true;
    }

    /// <summary>
    /// The decisions after the media is found, in the documented order: plan feature and output height (402), consent (409), source
    /// length (413), availability (409). The first refusal wins; the plan carries its machine code and sentence for the estimate.
    /// </summary>
    private async Task<Plan> PlanAsync(Project project, Source source, VideoEnhanceRequestBody body, CancellationToken ct)
    {
        VideoSourceRef sourceRef = source.Kind == "file"
            ? new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: source.Id)
            : new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: source.Id);
        TryBuild(body, sourceRef, out VideoEnhanceStepConfig? bare, out _);
        VideoEnhanceStepConfig config = bare!;
        bool hosted = config.IsHosted;
        bool consented = project.VideoEnhanceConsentAt is not null;
        OrganizationEntitlementSet? plan = _entitlements is null ? null : await _entitlements.GetAsync(project.OrganizationId, ct);
        int maxHeight = plan?.MaxOutputHeight ?? 2160;

        // What the result would be, from the measured source (or the source's own unknowns as zeros: the figures are then not shown).
        MediaSourceProbe? probe = source.Probe;
        int sw = probe?.Width ?? 0;
        int sh = probe?.Height ?? 0;
        double fps = source.Fps ?? 30;
        VideoEnhanceSizing.Output output = sw > 0 && sh > 0
            ? VideoEnhanceSizing.OutputFor(config, sw, sh, fps)
            : new VideoEnhanceSizing.Output(0, 0, 0);
        int seconds = source.DurationSec is { } d ? (int)Math.Ceiling(d) : 0;

        VideoEnhanceStepConfig finalConfig = config with
        {
            MaxSourceSeconds = seconds > 0 ? seconds : null,
            AllowSourceMediaEgress = hosted && consented,
            SourceWidth = hosted && source.Measured ? sw : null,
            SourceHeight = hosted && source.Measured ? sh : null,
            SourceFps = hosted && source.Measured ? Math.Round(fps, 3) : null
        };
        string json = JsonSerializer.Serialize(finalConfig, VideoEnhanceStepConfig.JsonOptions);

        // The cap that applies to this operation, from the configuration the executor reads (a deployment can only lower it).
        int capSeconds = config.EffectiveMaxSourceSeconds(hosted ? _settings.HostedMaxSourceSeconds : _settings.LocalMaxSourceSeconds);

        Plan Refused(int status, string code, string message, object? extra = null) =>
            new(finalConfig, json, output, seconds, capSeconds, consented, maxHeight, new VideoEnhanceRefusal(status, ErrorBody(code, message, extra)), code, message);

        // 402: the plan feature (hosted only), then the output height (an upscale above the plan's).
        if (hosted && plan is not null && PlanGate.CheckVideoEnhance(plan) is { } violation)
        {
            return new Plan(finalConfig, json, output, seconds, capSeconds, consented, maxHeight,
                new VideoEnhanceRefusal(violation.StatusCode, violation.ToBody()), PlanGate.NotEntitledCode, violation.Message);
        }

        int outShort = output.Width > 0 && output.Height > 0 ? Math.Min(output.Width, output.Height) : (config.TargetHeight ?? 0);
        // EVERY operation: a denoise, motion blur or interpolation of a source above the plan writes a file at that height too.
        if (plan is not null && outShort > 0 && PlanGate.CheckResolution(outShort, plan) is { } tooTall)
        {
            return new Plan(finalConfig, json, output, seconds, capSeconds, consented, maxHeight,
                new VideoEnhanceRefusal(tooTall.StatusCode, tooTall.ToBody()), ResolutionAbovePlanCode, tooTall.Message);
        }

        // 409: a hosted operation needs the project's recorded consent (the body can never grant it).
        if (hosted && !consented)
        {
            return Refused(StatusCodes.Status409Conflict, ConsentRequiredCode,
                "Before we can enhance this video with AI, you need to allow this project's video to be sent to an outside AI service. You can agree once in the editor.");
        }

        // 413: the source's length against the cap of its engine (hosted 120 s, local 600 s).
        int cap = capSeconds;
        if (source.DurationSec is { } length && length > cap)
        {
            string shown = $"{(int)Math.Ceiling(length)} seconds";
            return Refused(StatusCodes.Status413PayloadTooLarge, SourceTooLongCode,
                hosted
                    ? $"This video is {shown} long, over the {cap}-second limit for an AI enhancement. Trim it, or use a free local operation."
                    : $"This video is {shown} long, over the {cap}-second limit for one enhancement. Trim the clip first.",
                new { maxSeconds = cap });
        }

        // A hosted operation needs the source measured (its size and rate price the hold) and a priced provider.
        if (hosted)
        {
            if (!source.Measured)
            {
                return Refused(StatusCodes.Status409Conflict, SourceNotMeasuredCode,
                    "This video has not been measured yet, so the AI enhancement cannot be priced. Try again in a moment.");
            }

            if (config.Op == VideoEnhanceOp.Extend)
            {
                if (config.ExtendCanvas(sw, sh) is not { } canvas || canvas is { Left: 0, Right: 0, Top: 0, Bottom: 0 })
                {
                    return Refused(StatusCodes.Status400BadRequest, "ENHANCE_REQUEST_INVALID",
                        "That extension is not possible for this video: it already has that shape, or a side would need to grow by more than half.");
                }

                // The model's own frame ceiling, in seconds at this source's rate (Wan VACE: 241 frames).
                ResolvedAudioGenerationProvider? outpaint = _providers is null
                    ? null
                    : await _providers.ResolveVideoOutpaintAsync(null, ct);
                VideoEnhanceEndpoint? endpoint = outpaint is null
                    ? null
                    : VideoEnhanceModelCatalog.Instance.Find(InferenceProviderCapability.VideoOutpaint, outpaint.ModelName);
                if (endpoint is not null && source.DurationSec is { } shortSeconds
                    && !VideoEnhanceCreditPricing.AtLeastMinFrames(endpoint, (long)Math.Ceiling(shortSeconds * fps)))
                {
                    int minSeconds = VideoEnhanceCreditPricing.MinFrameSeconds(endpoint, fps) ?? 1;
                    return Refused(StatusCodes.Status422UnprocessableEntity, SourceTooShortCode,
                        $"This video is {shortSeconds:0.#} seconds long, under the {minSeconds}-second minimum for extending a video at this frame rate. Use a longer clip.",
                        new { minSeconds });
                }

                if (endpoint is not null && source.DurationSec is { } clipSeconds
                    && !VideoEnhanceCreditPricing.WithinFrameCap(endpoint, (long)Math.Ceiling(clipSeconds * fps)))
                {
                    int frameCapSeconds = (int)Math.Floor(VideoEnhanceCreditPricing.FrameCapSeconds(endpoint, fps) ?? 0d);
                    return Refused(StatusCodes.Status413PayloadTooLarge, SourceTooLongCode,
                        $"This video is {(int)Math.Ceiling(clipSeconds)} seconds long, over the {frameCapSeconds}-second limit for extending a video at this frame rate. Trim it first.",
                        new { maxSeconds = frameCapSeconds });
                }
            }

            long? hold = _providers is null || _prices is null
                ? null
                : await VideoEnhanceStepPricing.TryStepHoldMillisAsync(json, _providers, _prices, ct, _settings);
            if (hold is null)
            {
                return Refused(StatusCodes.Status409Conflict, UnavailableCode,
                    "AI video enhancement is not available right now (no provider is set up, or it cannot be priced). Use a free local operation.");
            }
        }

        return new Plan(finalConfig, json, output, seconds, capSeconds, consented, maxHeight, null, null, null);
    }

    private static object ErrorBody(string code, string message, object? extra)
    {
        Dictionary<string, object?> body = new() { ["code"] = code, ["message"] = message };
        if (extra is not null)
        {
            foreach (System.Reflection.PropertyInfo p in extra.GetType().GetProperties())
                body[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        }

        return body;
    }

    private async Task<WorkflowPreflightResult> PreflightAsync(Project project, string configJson, CancellationToken ct)
    {
        AgentDefinition? placeholder = await _db.AgentDefinitions
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .FirstOrDefaultAsync(ct);
        WorkflowStep step = new()
        {
            StepOrder = 1,
            StepType = StepType.VideoEnhance,
            VideoEnhanceConfigJson = configJson,
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = placeholder?.Id ?? Guid.Empty
        };
        return await _costEstimator!.PreflightAsync(project.OrganizationId, [step], ct);
    }

    private async Task<VideoEnhanceRefusal?> CheckRunRateAsync(Guid projectId, Guid userId, RunRateBucket bucket, CancellationToken ct)
    {
        int limit = bucket == RunRateBucket.Free ? FreeRunsPerHour : PaidRunsPerHour;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime since = now.AddHours(-1);
        List<DateTime> recent = (await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.InitiatedByUserId == userId
                    && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.VideoEnhance)
                .Select(e => e.QueuedAt)
                .ToListAsync(ct))
            .Where(q => q is not null && q >= since)
            .Select(q => q!.Value)
            .ToList();
        if (recent.Count < limit) return null;

        int retryAfter = Math.Max(1, (int)Math.Ceiling((recent.Min().AddHours(1) - now).TotalSeconds));
        return new VideoEnhanceRefusal(StatusCodes.Status429TooManyRequests, new
        {
            code = RunRateRefusal.Code,
            message = $"Run limit reached: {recent.Count} of {limit} video enhancements in this project in the last hour. Try again in {RunRateRefusal.Wait(retryAfter)}.",
            used = recent.Count,
            limit,
            windowMinutes = 60,
            retryAfterSeconds = retryAfter
        });
    }

    private static string VideoEnhanceLabel(VideoEnhanceStepConfig config) => config.Op switch
    {
        VideoEnhanceOp.Upscale => "sharper",
        VideoEnhanceOp.Interpolate => "smoother",
        VideoEnhanceOp.Denoise => "cleaner",
        VideoEnhanceOp.Extend => "extended",
        _ => "motion blur"
    };

    // ------------------------------------------------------------------------------------------------ status and stop

    public async Task<VideoEnhanceResult<VideoEnhanceStatusResponse>> GetAsync(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        Loaded? run = await LoadAsync(projectId, mediaRef, executionId, ct);
        if (run is null) return VideoEnhanceResult<VideoEnhanceStatusResponse>.Refuse(StatusCodes.Status404NotFound);

        string state = run.Execution.Status switch
        {
            ExecutionStatus.Queued => "queued",
            ExecutionStatus.Running => "running",
            ExecutionStatus.Passed => "ready",
            ExecutionStatus.Cancelled => "cancelled",
            _ => "failed"
        };

        WorkflowStepResult? result = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId && r.WorkflowStep.StepType == StepType.VideoEnhance)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

        JsonElement output = default;
        bool hasOutput = false;
        if (result?.OutputJson is { Length: > 0 } json)
        {
            try
            {
                output = JsonDocument.Parse(json).RootElement.Clone();
                hasOutput = output.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                hasOutput = false;
            }
        }

        string? failureCode = null;
        string? message = null;
        if (state == "ready" && result is not null && await OutputGate.IsResultHeldAsync(_db, result.Id, ct))
        {
            // A result that has since been held by moderation is not served.
            state = "failed";
            failureCode = "OUTPUT_UNAVAILABLE";
            message = "This result is not available.";
        }
        else if (state == "ready" && (result is null || result.Status == StepStatus.Failed || !hasOutput))
        {
            state = "failed";
        }

        if (state is "failed" or "cancelled")
        {
            failureCode = failureCode ?? result?.FailureCode ?? (hasOutput ? Text(output, "errorCode") : null);
            message = message ?? (hasOutput ? Text(output, "message") : null) ?? run.Execution.ErrorMessage;
        }

        bool ready = state == "ready";
        List<string> warnings = [];
        if (ready && hasOutput && output.TryGetProperty("warnings", out JsonElement w) && w.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in w.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) warnings.Add(s);
            }
        }

        VideoEnhanceStatusResponse response = new(
            executionId,
            state,
            run.Config.Op.ToString(),
            run.Config.Engine.ToString(),
            ready ? Text(output, "method") : null,
            ready ? Number(output, "durationSec") : null,
            ready && Guid.TryParse(Text(output, "outputFileId"), out Guid fileId) ? fileId : null,
            mediaRef,
            ready ? (int?)Number(output, "outputWidth") : null,
            ready ? (int?)Number(output, "outputHeight") : null,
            ready ? Number(output, "outputFps") : null,
            ready ? (long?)Number(output, "sizeBytes") : null,
            warnings,
            message,
            failureCode,
            state switch { "queued" => 0, "running" => null, _ => 100 });
        return VideoEnhanceResult<VideoEnhanceStatusResponse>.Ok(response);
    }

    public async Task<VideoEnhanceResult<object>> CancelAsync(Guid projectId, string mediaRef, Guid executionId, Guid userId, CancellationToken ct)
    {
        Loaded? run = await LoadAsync(projectId, mediaRef, executionId, ct);
        if (run is null) return VideoEnhanceResult<object>.Refuse(StatusCodes.Status404NotFound);

        if (run.Execution.Status is not (ExecutionStatus.Queued or ExecutionStatus.Running))
        {
            return VideoEnhanceResult<object>.Refuse(StatusCodes.Status409Conflict, new
            {
                code = "ENHANCE_ALREADY_FINISHED",
                message = "This enhancement has already finished."
            });
        }

        if (_publish is null)
            return VideoEnhanceResult<object>.Refuse(StatusCodes.Status500InternalServerError, new { message = "Stopping is not available." });

        // The same event the workflow stop endpoint publishes, so the engine's stop path (hold release included) is exactly the same.
        await _publish.Publish(new WorkflowExecutionStopRequested { ExecutionId = executionId, RequestedByUserId = userId }, ct);
        return VideoEnhanceResult<object>.Ok(new { executionId, state = "stopping" }, StatusCodes.Status202Accepted);
    }

    private sealed record Loaded(WorkflowExecution Execution, VideoEnhanceStepConfig Config);

    /// <summary>
    /// The execution of this project's hidden VideoEnhance workflow that enhances the media of the path, or null: another project's,
    /// another kind's and another media's runs are all the same 404.
    /// </summary>
    private async Task<Loaded?> LoadAsync(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        if (!TryParseRef(mediaRef, out string kind, out Guid id)) return null;

        WorkflowExecution? execution = await _db.WorkflowExecutions.AsNoTracking()
            .Include(e => e.WorkflowDefinition).ThenInclude(w => w.Steps)
            .FirstOrDefaultAsync(e => e.Id == executionId && e.ProjectId == projectId
                && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.VideoEnhance, ct);
        string? configJson = execution?.WorkflowDefinition.Steps.FirstOrDefault(s => s.StepType == StepType.VideoEnhance)?.VideoEnhanceConfigJson;
        if (execution is null || string.IsNullOrWhiteSpace(configJson)) return null;

        VideoEnhanceStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(configJson, VideoEnhanceStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        bool sameMedia = kind == "file"
            ? config?.Source is { Kind: VideoSourceKind.ProjectFile } f && f.ProjectFileId == id
            : config?.Source is { Kind: VideoSourceKind.StepResult } s && s.StepResultId == id;
        return config is not null && sameMedia ? new Loaded(execution, config) : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // ------------------------------------------------------------------------------------------------ the media

    private static bool TryParseRef(string mediaRef, out string kind, out Guid id)
    {
        kind = string.Empty;
        id = Guid.Empty;
        string[] parts = mediaRef.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out id)) return false;
        if (parts[0] is "file" or "step_result")
        {
            kind = parts[0];
            return true;
        }

        return false;
    }

    /// <summary>The video a token names in this project: 404 when absent, foreign, quarantined or held; 422 when it is not a video.</summary>
    private async Task<(Source?, VideoEnhanceRefusal?)> FindSourceAsync(Guid projectId, string mediaRef, CancellationToken ct)
    {
        VideoEnhanceRefusal notFound = new(StatusCodes.Status404NotFound, null);
        if (!TryParseRef(mediaRef, out string kind, out Guid id)) return (null, notFound);
        if (await OutputGate.IsOutputIdHeldAsync(_db, projectId, id, ct))
        {
            Microsoft.AspNetCore.Mvc.ObjectResult held = OutputGate.Unavailable();
            return (null, new VideoEnhanceRefusal(held.StatusCode ?? StatusCodes.Status404NotFound, held.Value));
        }

        string storageKey;
        bool isVideo;
        if (kind == "file")
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
            return (null, new VideoEnhanceRefusal(
                StatusCodes.Status422UnprocessableEntity, new { code = "ENHANCE_SOURCE_NOT_VIDEO", message = "Only a video can be enhanced." }));
        }

        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(_db, projectId, [storageKey], ct);
        probes.TryGetValue(storageKey, out MediaSourceProbe? probe);
        return (new Source(kind, id, probe), null);
    }
}
