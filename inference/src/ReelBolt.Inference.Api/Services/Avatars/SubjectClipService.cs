using System.Globalization;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>The codes a subject clip answers with, beside those of the subject reader and the plan gate.</summary>
public static class SubjectClipCodes
{
    /// <summary>The media consent is not recorded and this call may not allow sending media for itself.</summary>
    public const string ConsentRequired = "CONSENT_REQUIRED";

    /// <summary>
    /// The clip shows a person and this deployment lets nobody (or only platform admins) generate with a person
    /// (<c>Avatars:Generation</c>): the engine's own code, answered before anything is queued.
    /// </summary>
    public const string GenerationDisabled = "AVATAR_GENERATION_DISABLED";

    /// <summary>A setting of the request is not one the model takes (the body carries <c>errors</c>).</summary>
    public const string RequestInvalid = "SUBJECT_CLIP_REQUEST_INVALID";

    /// <summary>The model chosen (<c>capabilityId</c>) is not a video model of this workspace or of the platform.</summary>
    public const string ModelNotFound = "VIDEO_MODEL_NOT_FOUND";

    /// <summary>No video model is set up (no default one), or the one set up cannot be used here.</summary>
    public const string ModelMissing = "VIDEO_MODEL_MISSING";

    /// <summary>
    /// A talking clip (a speech file) needs a lip-sync model and none is set up: the code master's run start answers for
    /// a talking-head step (<see cref="ReelBolt.Inference.Api.Services.Workflows.WorkflowLintGate.LipSyncModelNotSetCode"/>).
    /// </summary>
    public const string LipSyncModelNotSet = ReelBolt.Inference.Api.Services.Workflows.WorkflowLintGate.LipSyncModelNotSetCode;

    /// <summary>Billing is on and the model has no price set up, so the clip is never started for free.</summary>
    public const string ModelUnpriced = "VIDEO_MODEL_UNPRICED";

    /// <summary>The model cannot show avatars or objects (the engine's own code; the body may carry <c>sibling</c>).</summary>
    public const string NotSupportedByModel = VideoReferenceInput.NotSupportedCode;

    /// <summary>The path needs a picture model to make the still, and none is set up (the engine's own code).</summary>
    public const string StillProviderMissing = "AVATAR_STILL_PROVIDER_MISSING";

    /// <summary>The picture model set up cannot make a still from several pictures (the engine's own code).</summary>
    public const string StillModelUnsupported = "AVATAR_STILL_MODEL_UNSUPPORTED";

    /// <summary>A talking clip (<c>speechFileId</c>) shows one person and nothing else.</summary>
    public const string SpeechNeedsOnePerson = "speech_needs_one_person";

    /// <summary>The speech is not a WAV or MP3 file of this project that may be used.</summary>
    public const string SpeechFileInvalid = "speech_file_invalid";

    /// <summary>A stop came after the clip ended.</summary>
    public const string AlreadyFinished = "SUBJECT_CLIP_ALREADY_FINISHED";

    /// <summary>A ready run whose clip is held, or whose files could not be found: nothing is served.</summary>
    public const string OutputUnavailable = "OUTPUT_UNAVAILABLE";

    /// <summary>The run-rate source the paid bucket records the clip under.</summary>
    public const string RunRateSource = "subject_clip";
}

/// <summary>The names a subject clip is filed under: its hidden workflow ("Avatar clip: Anna, Car") and its step label.</summary>
public static class SubjectClipNames
{
    /// <summary>What the hidden workflow's name starts with.</summary>
    public const string WorkflowPrefix = "Avatar clip: ";

    private const int MaxWorkflowName = 200;
    private const int MaxLabel = 60;

    /// <summary>"Avatar clip: " and the names of the subjects, in the order chosen.</summary>
    public static string WorkflowName(IEnumerable<string> names)
    {
        string name = WorkflowPrefix + string.Join(", ", names.Select(n => n.Trim()).Where(n => n.Length > 0));
        return name.Length <= MaxWorkflowName ? name : name[..MaxWorkflowName].TrimEnd();
    }

    /// <summary>The names a workflow name carries (what the activity tray titles the clip with), or null.</summary>
    public static string? TitleOf(string? workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName)) return null;
        string title = workflowName.StartsWith(WorkflowPrefix, StringComparison.Ordinal)
            ? workflowName[WorkflowPrefix.Length..]
            : workflowName;
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    /// <summary>
    /// The step's label: the first line of the prompt, at most 60 characters. The registered files are named after the
    /// workflow and this label, so the media pool says what each clip shows.
    /// </summary>
    public static string StepLabel(string prompt)
    {
        string first = prompt.Split('\n', 2)[0].Trim();
        if (first.Length == 0) return "Clip";
        return first.Length <= MaxLabel ? first : first[..MaxLabel].TrimEnd();
    }
}

/// <inheritdoc />
public sealed class SubjectClipService : ISubjectClipService
{
    /// <summary>The most a talking clip's speech file may weigh (the engine reads at most this much).</summary>
    public const long MaxSpeechBytes = 50L * 1024 * 1024;

    /// <summary>The length a clip gets when the request names none.</summary>
    public const int DefaultSeconds = 6;

    /// <summary>The most takes one request asks for.</summary>
    public const int MaxTakes = 2;

    /// <summary>The longest prompt.</summary>
    public const int MaxPromptLength = 2000;

    /// <summary>The size a clip is made at when the request names none and the model offers it.</summary>
    public const string PreferredResolution = "768P";

    /// <summary>The frame shape a clip is made in when the request names none.</summary>
    public const string DefaultRatio = "16:9";

    private const int MaxListed = 20;

    private static readonly string[] SpeechMimeTypes = ["audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave", "audio/mpeg", "audio/mp3"];

    private readonly InferenceApiDbContext _db;
    private readonly AvatarOptions _options;
    private readonly IProjectSubjectReader _subjects;
    private readonly ILogger<SubjectClipService> _logger;
    private readonly IWorkflowExecutionService? _executions;
    private readonly IInferenceProviderResolver? _providers;
    private readonly IVideoGenerationClientFactory? _videoClients;
    private readonly IModelPriceService? _prices;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly IAssistantExecutionGuard? _runRate;
    private readonly IPublishEndpoint? _publish;
    private readonly TimeProvider _clock;

    public SubjectClipService(
        InferenceApiDbContext db,
        IOptions<AvatarOptions> options,
        IProjectSubjectReader subjects,
        ILogger<SubjectClipService> logger,
        IWorkflowExecutionService? executions = null,
        IInferenceProviderResolver? providers = null,
        IVideoGenerationClientFactory? videoClients = null,
        IModelPriceService? prices = null,
        IWorkflowCostEstimator? costEstimator = null,
        IOrganizationEntitlements? entitlements = null,
        IAssistantExecutionGuard? runRate = null,
        IPublishEndpoint? publish = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _options = options.Value;
        _subjects = subjects;
        _logger = logger;
        _executions = executions;
        _providers = providers;
        _videoClients = videoClients;
        _prices = prices;
        _costEstimator = costEstimator;
        _entitlements = entitlements;
        _runRate = runRate;
        _publish = publish;
        _clock = clock ?? TimeProvider.System;
    }

    private bool BillingOn => _costEstimator is { BillingEnabled: true };

    /// <summary>
    /// Everything the checks decided: the step to run (or, for an estimate, why it cannot run yet), and the facts the
    /// answer shows. <see cref="UnavailableCode"/> is set when the model stage found something only an administrator (or
    /// the one-time consent) can change; the estimate then answers it beside what it knows.
    /// </summary>
    private sealed record ClipPlan(
        ProjectSubjectLookup Found,
        VideoSubjectPath? Path,
        VideoGenerateStepConfig? Config,
        string? ConfigJson,
        long? HoldMillis,
        string? UnavailableCode,
        string? UnavailableReason);

    // ------------------------------------------------------------------------------------------------ estimate

    public async Task<SubjectClipOutcome<SubjectClipEstimate>> EstimateAsync(
        Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, CancellationToken ct)
    {
        (ClipPlan? plan, AvatarRefusal? refusal) = await PlanAsync(project, actor, request, mediaAllowed, forStart: false, ct);
        if (refusal is not null) return SubjectClipOutcome<SubjectClipEstimate>.Refused(refusal);

        ClipPlan planned = plan!;
        string? path = planned.Path?.ToString();
        bool still = planned.Path is VideoSubjectPath.FirstFrame or VideoSubjectPath.TalkingHead;
        bool consented = project.SubjectMediaConsentAt is not null || mediaAllowed;

        if (planned.ConfigJson is null)
        {
            // The model stage stopped: no figure, not affordable, and the code a start answers.
            return SubjectClipOutcome<SubjectClipEstimate>.Of(new SubjectClipEstimate(
                BillingOn, null, null, null, false, path, still, planned.UnavailableReason, planned.UnavailableCode));
        }

        string? consentReason = consented ? null : ConsentRequiredMessage;
        string? consentCode = consented ? null : SubjectClipCodes.ConsentRequired;
        if (!BillingOn)
        {
            return SubjectClipOutcome<SubjectClipEstimate>.Of(new SubjectClipEstimate(
                false, null, null, null, true, path, still, consentReason, consentCode));
        }

        WorkflowPreflightResult price = await PreflightAsync(project, planned.ConfigJson, ct);
        long held = price.Evaluated ? price.Estimate.RequiredMillis : planned.HoldMillis ?? 0;
        long high = Math.Max(held, price.Evaluated ? price.Estimate.FixedMaxMillis : 0);
        return SubjectClipOutcome<SubjectClipEstimate>.Of(new SubjectClipEstimate(
            true,
            Credits(held),
            Credits(high),
            price.Evaluated ? Credits(price.BalanceMillis) : null,
            !price.Refused,
            path,
            still,
            consentReason,
            consentCode));
    }

    // ------------------------------------------------------------------------------------------------ start

    public async Task<SubjectClipOutcome<SubjectClipStarted>> StartAsync(
        Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, CancellationToken ct)
    {
        (ClipPlan? plan, AvatarRefusal? refusal) = await PlanAsync(project, actor, request, mediaAllowed, forStart: true, ct);
        if (refusal is not null) return SubjectClipOutcome<SubjectClipStarted>.Refused(refusal);
        ClipPlan planned = plan!;
        if (planned.ConfigJson is null || planned.Config is null)
        {
            return SubjectClipOutcome<SubjectClipStarted>.Refused(AvatarRefusal.Conflict(
                planned.UnavailableCode ?? SubjectClipCodes.ModelMissing, planned.UnavailableReason ?? "This clip cannot be made right now."));
        }

        // ---- 402: credits (the hold the engine reserves for the step) ----
        double? credits = null;
        if (BillingOn)
        {
            WorkflowPreflightResult price = await PreflightAsync(project, planned.ConfigJson, ct);
            long required = price.Evaluated ? price.Estimate.RequiredMillis : planned.HoldMillis ?? 0;
            credits = Credits(required);
            if (price.Refused)
            {
                return SubjectClipOutcome<SubjectClipStarted>.Refused(new AvatarRefusal(StatusCodes.Status402PaymentRequired,
                    NonWorkflowChargeCodes.InsufficientCredits,
                    CreditShortfallMessage.Build(price.Estimate.RequiredMillis, null, price.BalanceMillis),
                    new Dictionary<string, object?>
                    {
                        ["required"] = Credits(price.Estimate.RequiredMillis),
                        ["balance"] = Credits(Math.Max(0, price.BalanceMillis)),
                        ["missing"] = CreditShortfallMessage.MissingCredits(price.Estimate.RequiredMillis, price.BalanceMillis)
                    }));
            }
        }

        if (_executions is null)
        {
            return SubjectClipOutcome<SubjectClipStarted>.Refused(new AvatarRefusal(StatusCodes.Status503ServiceUnavailable,
                SubjectClipCodes.ModelMissing, "Clips cannot be started on this server."));
        }

        Guid? placeholder = await _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (placeholder is null)
        {
            _logger.LogError("The VideoTransform placeholder agent is not seeded, so no subject clip step can be created");
            return SubjectClipOutcome<SubjectClipStarted>.Refused(new AvatarRefusal(StatusCodes.Status500InternalServerError,
                "SUBJECT_CLIP_NOT_SET_UP", "The clip could not be set up. Nothing was started or charged."));
        }

        Guid executionId = Guid.NewGuid();
        string name = SubjectClipNames.WorkflowName(planned.Found.Avatars.Select(a => a.Name));
        string label = SubjectClipNames.StepLabel(planned.Config.InlinePrompt ?? string.Empty);

        // ---- 429: the paid run rate, hit once, with the run written inside it (a refused request writes nothing) ----
        PlanGateViolation? planLimit = null;
        async Task<Guid?> QueueAsync()
        {
            planLimit = await QueueRunAsync(project, actor, planned.ConfigJson, name, label, placeholder.Value, executionId, ct);
            return planLimit is null ? executionId : null;
        }

        if (_runRate is null)
        {
            await QueueAsync();
        }
        else
        {
            RunRateDecision decision = await _runRate.TryAcquireAsync(
                actor.UserId, project.Id, RunRateBucket.Paid, SubjectClipCodes.RunRateSource, QueueAsync, ct);
            if (!decision.Allowed && planLimit is null)
            {
                return SubjectClipOutcome<SubjectClipStarted>.Refused(new AvatarRefusal(StatusCodes.Status429TooManyRequests,
                    RunRateRefusal.Code,
                    decision.Busy
                        ? "The run limiter is busy right now. Try again in a few seconds."
                        : $"Run limit reached: {decision.Used} of {decision.Limit} paid runs in this project in the last hour. " +
                          $"Try again in {RunRateRefusal.Wait(decision.RetryAfterSeconds)}.",
                    new Dictionary<string, object?>
                    {
                        ["bucket"] = RunRateRefusal.BucketName(decision.Bucket),
                        ["used"] = decision.Used,
                        ["limit"] = decision.Limit,
                        ["windowMinutes"] = decision.WindowSeconds / 60,
                        ["retryAfterSeconds"] = decision.RetryAfterSeconds,
                        ["busy"] = decision.Busy
                    }));
            }
        }

        // The plan's concurrency cap, inside the queue: nothing was kept.
        if (planLimit is not null) return SubjectClipOutcome<SubjectClipStarted>.Refused(FromPlan(planLimit));

        _logger.LogInformation(
            "Queued subject clip {ExecutionId} in project {ProjectId}: {Subjects} subject(s), path {Path}, media allowed by {AllowedBy}",
            executionId, project.Id, planned.Found.Avatars.Count, planned.Path,
            project.SubjectMediaConsentAt is not null ? "project_consent" : "call");
        return SubjectClipOutcome<SubjectClipStarted>.Of(new SubjectClipStarted(executionId, "queued", credits));
    }

    /// <summary>
    /// The hidden workflow, then its execution through the same <see cref="IWorkflowExecutionService.QueueAsync"/> every
    /// hidden one-step kind uses (the avatar build runs' shape), so the engine runs it with its usual credit gate, budget
    /// gate, ledger, lease and recovery. A start the plan's concurrency cap refuses deletes the workflow again and answers
    /// that limit; a workflow left without an execution by any other failure is deleted by the orphan reaper.
    /// </summary>
    private async Task<PlanGateViolation?> QueueRunAsync(
        Project project, SubjectClipActor actor, string configJson, string name, string label, Guid placeholder, Guid executionId,
        CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = name,
            Kind = WorkflowDefinitionKind.SubjectClip,
            RequiresUserInput = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = placeholder,
            StepOrder = 1,
            Label = label,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson,
            // The takes become ordinary project video files, which is what the editor's media pool shows.
            RegisterOutputAsProjectFile = true,
            // A new clip is asked for every time: the same request is never answered with an earlier run's takes.
            CacheMode = StepCacheMode.Never
        });

        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _executions!.QueueAsync(
                project.Id, workflow.Id, actor.UserId, userRequest: null, actor.Initiator, ct,
                regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.SubjectClip, executionId);
            return null;
        }
        catch (PlanLimitException limit)
        {
            // Nothing runs: the workflow goes too.
            await DeleteUnqueuedAsync(workflow);
            return limit.Violation;
        }
    }

    /// <summary>Deletes a hidden workflow nothing ran; a failure here leaves it to the orphan reaper.</summary>
    private async Task DeleteUnqueuedAsync(WorkflowDefinition workflow)
    {
        try
        {
            _db.WorkflowSteps.RemoveRange(workflow.Steps);
            _db.WorkflowDefinitions.Remove(workflow);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not delete the unqueued subject clip workflow {WorkflowId}; the orphan reaper will", workflow.Id);
            foreach (WorkflowStep step in workflow.Steps) _db.Entry(step).State = EntityState.Detached;
            _db.Entry(workflow).State = EntityState.Detached;
        }
    }

    // ------------------------------------------------------------------------------------------------ the checks

    /// <summary>
    /// Every check of a request, in the design's order, up to and including the settings. A start (<paramref name="forStart"/>)
    /// is refused without the media permission; an estimate goes on and the caller reports it.
    /// </summary>
    private async Task<(ClipPlan? Plan, AvatarRefusal? Refusal)> PlanAsync(
        Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, bool forStart, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        // ---- 404: neither kind is switched on (no avatar exists as far as any caller can tell) ----
        if (!_options.AnyEnabled) return (null, NotFound());

        // ---- 403: an unverified address starts no run ----
        if (!actor.EmailVerified) return (null, EmailNotVerified());

        // ---- 404 / 400: which avatars the ids name (a kind switched off is absent), how many, one person at most ----
        ProjectSubjectLookup found = await _subjects.FindAsync(project.Id, request.SubjectIds, ct);
        if (found.Refusal is { } notFound) return (null, notFound);

        // ---- 409: who may generate with a person here (Avatars:Generation): the engine refuses such a clip with
        // AVATAR_GENERATION_DISABLED before anything is sent, so it is refused first and never queued (an estimate
        // answers the code beside no price). Objects need only their own switch. ----
        if (found.HasPerson && await PersonGenerationRefusalAsync(actor, ct) is { } personOff)
            return (Unavailable(found, null, SubjectClipCodes.GenerationDisabled, personOff), null);

        // ---- 402: the plan: video generation, and each kind named ----
        if (_entitlements is not null)
        {
            OrganizationEntitlementSet set = await _entitlements.GetAsync(project.OrganizationId, ct);
            if (PlanGate.CheckStep(StepType.VideoGenerate, set) is { } noVideo) return (null, FromPlan(noVideo));
            foreach (AvatarSubjectKind kind in found.Kinds)
            {
                PlanGateViolation? violation = kind == AvatarSubjectKind.Object ? PlanGate.CheckAvatarObjects(set) : PlanGate.CheckAvatars(set);
                if (violation is not null) return (null, FromPlan(violation));
            }
        }

        // ---- 409: the permission to send their media to the AI provider ----
        bool consented = project.SubjectMediaConsentAt is not null || mediaAllowed;
        if (!consented && forStart) return (null, AvatarRefusal.Conflict(SubjectClipCodes.ConsentRequired, ConsentRequiredMessage,
            new Dictionary<string, object?> { ["consent"] = "subject_media" }));

        // ---- 409: the one rule over every subject (its files re-hashed against the attested digests) ----
        ProjectSubjectRead read = await _subjects.ReadAsync(found, ProjectSubjectPurpose.Video, ProjectSubjectPictureBudget.None, ct);
        if (read.Refusal is { } unusable) return (null, unusable);

        // ---- 400: a talking clip's speech: one person and nothing else, a WAV or MP3 of this project ----
        (ProjectFile? speech, double? speechSeconds, AvatarRefusal? speechRefusal) = await SpeechAsync(project, found, request.SpeechFileId, ct);
        if (speechRefusal is not null) return (null, speechRefusal);

        // ---- the model, and how the subjects reach it ----
        if (!TryParseMode(request.SubjectMode, out VideoSubjectMode mode))
        {
            return (null, Invalid(["subjectMode must be Auto, References or FirstFrame."]));
        }

        // A clip with a speech file is a talking head: it resolves and prices through the LipSync capability (as master's
        // talking-head VideoGenerate steps do), never VideoGeneration. Without speech it stays VideoGeneration.
        (ResolvedVideoGenerationProvider? provider, AvatarRefusal? modelRefusal, string? missing) = await ModelAsync(
            project, request.CapabilityId, lipSync: speech is not null, ct);
        if (modelRefusal is not null) return (null, modelRefusal);
        if (provider is null)
        {
            return (Unavailable(found, null,
                speech is not null && request.CapabilityId is null ? SubjectClipCodes.LipSyncModelNotSet : SubjectClipCodes.ModelMissing, missing), null);
        }

        string model = provider.ModelName.Trim();
        IVideoGenerationClient client;
        VideoGenerationCapabilities capabilities;
        VideoSubjectPathDecision decision;
        try
        {
            client = _videoClients!.Get(provider);
            capabilities = client.Describe(model);
            decision = VideoSubjectPaths.Decide(client, provider.Kind, model, mode, hasDrivingAudio: speech is not null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _logger.LogInformation(ex, "The video model {Model} cannot make a subject clip here", model);
            return (Unavailable(found, null, SubjectClipCodes.ModelMissing,
                $"The video model \"{model}\" cannot make a clip with avatars here. Choose another model, or ask an administrator to set one up."), null);
        }

        // ---- 409: a model that cannot show them, naming the one of the same family that can ----
        if (decision.Refusal is { } cannot)
        {
            Dictionary<string, object?> extra = new(StringComparer.Ordinal) { ["model"] = model };
            if (cannot.Sibling is { } sibling) extra["sibling"] = sibling;
            return (null, AvatarRefusal.Conflict(cannot.Code, cannot.Message, extra));
        }

        VideoSubjectPath path = decision.Path!.Value;

        // The still a composed first frame or a talking head starts from: a fal.ai picture-edit model that takes several pictures.
        if (path is VideoSubjectPath.FirstFrame or VideoSubjectPath.TalkingHead)
        {
            if (await StillRefusalAsync(ct) is { } stillMissing)
                return (Unavailable(found, path, stillMissing.Code, stillMissing.Message), null);
        }

        // ---- 400: the settings, against what the model offers ----
        (VideoGenerateStepConfig? config, IReadOnlyList<string> errors) = Settings(
            request, found, provider, model, capabilities, client, path, mode, speech, speechSeconds, consented || forStart);
        if (errors.Count > 0) return (null, Invalid(errors));

        // ---- the price: the hold the engine will reserve, and the step's own spend cap ----
        long? hold = await HoldAsync(config!, ct);
        if (BillingOn && hold is null)
        {
            return (Unavailable(found, path, SubjectClipCodes.ModelUnpriced,
                $"The video model \"{model}\" has no price set up here, so a clip cannot be made with it. Choose another model, or ask an administrator to price it."), null);
        }

        if (!SetSpendCap(config!, provider, capabilities, client, path, hold))
        {
            return (Unavailable(found, path, SubjectClipCodes.ModelUnpriced,
                $"The video model \"{model}\" cannot be priced here, so a clip cannot be made with it. Choose another model."), null);
        }

        List<string> configErrors = [.. config!.Validate(capabilities), .. config.ValidateForKind(provider.Kind)];
        if (configErrors.Count > 0)
        {
            _logger.LogWarning("A subject clip config failed its own validation: {Errors}", string.Join("; ", configErrors));
            return (null, Invalid(configErrors));
        }

        string json = JsonSerializer.Serialize(config, VideoGenerateStepConfig.JsonOptions);
        return (new ClipPlan(found, path, config, json, hold, null, null), null);
    }

    /// <summary>
    /// Why a clip that shows a person cannot be generated in this deployment, or null when it can: the engine's own rule
    /// (<c>VideoGenerateStepExecutor</c>), so what the editor is told and what the run would do agree. <c>Off</c> refuses
    /// everyone; <c>PlatformAdminOnly</c> lets only a platform admin through, read from the database on every call (the
    /// engine reads the run's initiator the same way), and a failed read refuses.
    /// </summary>
    private async Task<string?> PersonGenerationRefusalAsync(SubjectClipActor actor, CancellationToken ct)
    {
        switch (_options.Generation)
        {
            case AvatarPersonGeneration.On:
                return null;
            case AvatarPersonGeneration.PlatformAdminOnly:
                bool admin;
                try
                {
                    admin = await _db.ApplicationUsers.AsNoTracking()
                        .Where(u => u.Id == actor.UserId)
                        .Select(u => u.IsAdmin)
                        .FirstOrDefaultAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not read whether user {UserId} is a platform admin; a clip with a person is refused", actor.UserId);
                    admin = false;
                }

                return admin
                    ? null
                    : "Clips that show a person are open to platform administrators only while they are being tested. A clip of objects only can be made.";
            default:
                return "Clips that show a person are not available in this workspace yet. A clip of objects only can be made.";
        }
    }

    private const string ConsentRequiredMessage =
        "Before an avatar or object can be shown in a clip, this project has to allow its files to be sent to the AI video " +
        "service. You can allow it once for this project.";

    private static ClipPlan Unavailable(ProjectSubjectLookup found, VideoSubjectPath? path, string code, string? message) =>
        new(found, path, null, null, null, code, message ?? "This clip cannot be made right now.");

    /// <summary>
    /// The speech of a talking clip: only with exactly one subject, a person; a WAV or MP3 of this project that is not
    /// quarantined, held or too large. Its length (from the stored probe) sizes the clip.
    /// </summary>
    private async Task<(ProjectFile? File, double? Seconds, AvatarRefusal? Refusal)> SpeechAsync(
        Project project, ProjectSubjectLookup found, Guid? speechFileId, CancellationToken ct)
    {
        if (speechFileId is not { } id) return (null, null, null);

        if (found.Avatars.Count != 1 || !found.HasPerson)
        {
            return (null, null, AvatarRefusal.BadRequest(SubjectClipCodes.SpeechNeedsOnePerson,
                "A talking clip shows one person saying the words, and nothing else. Choose one person, or leave the speech out."));
        }

        AvatarRefusal invalid = AvatarRefusal.BadRequest(SubjectClipCodes.SpeechFileInvalid,
            "The speech must be a WAV or MP3 file of this project.");
        if (id == Guid.Empty) return (null, null, invalid);

        ProjectFile? file = await _db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == project.Id, ct);
        if (file is null
            || file.ModerationStatus == FileModerationStatus.Quarantined
            || file.CsamStatus == CsamScanStatus.Quarantined
            || string.IsNullOrWhiteSpace(file.StorageKey)
            || !file.StorageKey.StartsWith($"projects/{project.Id}/", StringComparison.Ordinal)
            || !SpeechMimeTypes.Contains(file.MimeType?.Trim().ToLowerInvariant() ?? string.Empty, StringComparer.Ordinal)
            || file.SizeBytes > MaxSpeechBytes
            || await OutputGate.IsFileHeldAsync(_db, file, ct))
        {
            return (null, null, invalid);
        }

        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(_db, project.Id, [file.StorageKey], ct);
        double? seconds = probes.TryGetValue(file.StorageKey, out MediaSourceProbe? probe) && probe.DurationSec > 0 ? probe.DurationSec : null;
        return (file, seconds, null);
    }

    /// <summary>
    /// The video model: the one named (it must be a <c>VideoGeneration</c> model (a <c>LipSync</c> one for a talking clip, <paramref name="lipSync"/>) of the project's workspace or of the
    /// platform: anything else is the same <c>400 VIDEO_MODEL_NOT_FOUND</c>, whatever the reason), else the default one.
    /// No model at all answers no provider and the sentence the estimate shows.
    /// </summary>
    private async Task<(ResolvedVideoGenerationProvider? Provider, AvatarRefusal? Refusal, string? Missing)> ModelAsync(
        Project project, Guid? capabilityId, bool lipSync, CancellationToken ct)
    {
        InferenceProviderCapability capability = lipSync ? InferenceProviderCapability.LipSync : InferenceProviderCapability.VideoGeneration;
        AvatarRefusal notFound = AvatarRefusal.BadRequest(SubjectClipCodes.ModelNotFound, "The video model chosen is not available in this workspace.");
        if (capabilityId is { } id)
        {
            if (id == Guid.Empty) return (null, notFound, null);
            Guid organizationId = project.OrganizationId;
            bool visible = await _db.ModelCapabilities.AsNoTracking()
                .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                .AnyAsync(c => c.Id == id
                    && c.Capability == capability
                    && c.IsEnabled
                    && (c.OrganizationId == null || c.OrganizationId == organizationId), ct);
            if (!visible) return (null, notFound, null);
        }

        if (_providers is null || _videoClients is null)
            return (null, null, "Clips with avatars cannot be made on this server.");

        ResolvedVideoGenerationProvider? provider;
        try
        {
            provider = lipSync
                ? await _providers.ResolveLipSyncAsync(capabilityId, ct)
                : await _providers.ResolveVideoGenerationAsync(capabilityId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolving the video model for a subject clip failed");
            provider = null;
        }

        // An explicit model the resolver could not resolve falls through to the default there; here it is refused.
        if (capabilityId is { } asked && provider is not null && provider.ProviderId != asked) return (null, notFound, null);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ModelName))
        {
            return (null, null, capabilityId is null
                ? lipSync
                    ? LipSyncStepResolution.NoModelMessage
                    : "No video model is set up for this workspace. Ask an administrator to add one."
                : "The video model chosen cannot be used right now.");
        }

        return (provider, null, null);
    }

    /// <summary>
    /// The engine's still maker needs a fal.ai instruction-edit model that takes several pictures (the default <c>ImageEdit</c>
    /// model). Checked here so a clip that would fail on it is never started; the engine checks it again.
    /// </summary>
    private async Task<AvatarRefusal?> StillRefusalAsync(CancellationToken ct)
    {
        ResolvedImageProvider? still = null;
        try
        {
            still = _providers is null ? null : await _providers.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolving the picture model for a subject clip's still failed");
        }

        if (still is null || still.Kind != InferenceProviderKind.FalAi)
        {
            return AvatarRefusal.Conflict(SubjectClipCodes.StillProviderMissing,
                "This video model shows avatars through one picture made first, and no picture model that can make it is set up. " +
                "Choose a model that takes reference pictures, or ask an administrator to set up a picture-edit model.");
        }

        string stillModel = string.IsNullOrWhiteSpace(still.ModelName)
            ? FalImageCatalog.Instance.DefaultFor(FalImageJob.Instruct).Id
            : still.ModelName.Trim();
        FalImageEndpoint? endpoint = FalImageCatalog.Instance.Find(stillModel);
        if (endpoint is null || endpoint.Job != FalImageJob.Instruct || endpoint.ImageUrlsField is null)
        {
            return AvatarRefusal.Conflict(SubjectClipCodes.StillModelUnsupported,
                $"The picture model \"{stillModel}\" cannot make a picture from several pictures of the avatars, which this video model needs. " +
                "Choose a model that takes reference pictures, or ask an administrator to set up another picture-edit model.");
        }

        return null;
    }

    /// <summary>
    /// The step config from the request and the model's own offer, or the list of what to fix. A talking clip is as long as
    /// its speech (rounded up), within what the model makes.
    /// </summary>
    private static (VideoGenerateStepConfig? Config, IReadOnlyList<string> Errors) Settings(
        SubjectClipRequest request,
        ProjectSubjectLookup found,
        ResolvedVideoGenerationProvider provider,
        string model,
        VideoGenerationCapabilities capabilities,
        IVideoGenerationClient client,
        VideoSubjectPath path,
        VideoSubjectMode mode,
        ProjectFile? speech,
        double? speechSeconds,
        bool egress)
    {
        List<string> errors = [];

        string prompt = request.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0) errors.Add("Say what happens in the clip.");
        else if (prompt.Length > MaxPromptLength) errors.Add($"Describe the clip in at most {MaxPromptLength} characters.");
        else if (prompt.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) errors.Add("The description of the clip contains characters that cannot be used.");

        int takes = request.Takes ?? 1;
        if (takes < 1 || takes > MaxTakes) errors.Add($"Make one or {MaxTakes} takes.");

        string ratio = string.IsNullOrWhiteSpace(request.Ratio) ? DefaultRatio : request.Ratio.Trim();
        if (!capabilities.AllowedRatios.Contains(ratio, StringComparer.Ordinal))
            errors.Add($"This model makes clips in these shapes: {string.Join(", ", capabilities.AllowedRatios)}.");

        string resolution = string.IsNullOrWhiteSpace(request.Resolution) ? DefaultResolution(capabilities) : request.Resolution.Trim();
        if (!capabilities.PricePerSecondByResolution.ContainsKey(resolution))
            errors.Add($"This model makes clips at these sizes: {string.Join(", ", capabilities.PricePerSecondByResolution.Keys)}.");

        int min = Math.Max(1, capabilities.MinDurationSeconds);
        int max = Math.Max(min, capabilities.MaxDurationSeconds);
        int duration = request.DurationSeconds ?? DefaultSeconds;
        if (path == VideoSubjectPath.TalkingHead && speechSeconds is { } said)
        {
            VideoReferenceInputSupport support = client.DeclaredReferenceInputSupport(model);
            int ms = (int)Math.Round(said * 1000, MidpointRounding.AwayFromZero);
            string shown = said.ToString("0.#", CultureInfo.InvariantCulture);
            int needed = (int)Math.Ceiling(said);
            if (support.MinAudioMs > 0 && ms < support.MinAudioMs)
                errors.Add($"The speech lasts {shown} s; this model needs at least {support.MinAudioMs / 1000} s of speech.");
            else if ((support.MaxAudioTotalMs > 0 && ms > support.MaxAudioTotalMs) || needed > max)
                errors.Add($"The speech lasts {shown} s; this model makes talking clips of at most {Math.Min(max, support.MaxAudioTotalMs > 0 ? support.MaxAudioTotalMs / 1000 : max)} s.");
            else
                duration = Math.Clamp(needed, min, max);
        }

        if (duration < min || duration > max)
            errors.Add(min == max ? $"This model makes clips of exactly {min} seconds." : $"The length must be {min} to {max} seconds for this model.");

        if (errors.Count > 0) return (null, errors);

        VideoGenerateStepConfig config = new()
        {
            PlanSource = VideoGeneratePlanSource.Inline,
            InlinePrompt = prompt,
            Model = model,
            Resolution = resolution,
            DurationSeconds = duration,
            Ratio = ratio,
            ProviderId = provider.ProviderId,
            Takes = takes,
            MaxClips = takes,
            // Set by the server only, and only because the media permission was established (the project's recorded
            // consent, or this one call's word from a caller the safe rule lets give it). An estimate without it prices the
            // same step and answers CONSENT_REQUIRED beside the price.
            AllowSourceMediaEgress = egress,
            Subjects = [.. found.Avatars.Select(a => new VideoGenerateSubject(a.Id))],
            SubjectMode = mode,
            AvatarAudioProjectFileId = speech?.Id
        };
        return (config, []);
    }

    /// <summary>768P when the model offers it, else its cheapest size (the variant videos' rule).</summary>
    private static string DefaultResolution(VideoGenerationCapabilities capabilities) =>
        capabilities.PricePerSecondByResolution.ContainsKey(PreferredResolution) || capabilities.PricePerSecondByResolution.Count == 0
            ? PreferredResolution
            : capabilities.PricePerSecondByResolution.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;

    /// <summary>The engine's own hold for the step (<see cref="VideoGenerateCreditPricing.TryHoldMillisAsync"/>); null when it cannot be priced.</summary>
    private async Task<long?> HoldAsync(VideoGenerateStepConfig config, CancellationToken ct)
    {
        if (_providers is null || _videoClients is null || _prices is null) return null;
        // The hold does not read the spend cap; a placeholder keeps the config valid while it is priced.
        decimal cap = config.MaxSpendCredits;
        config.MaxSpendCredits = Math.Max(cap, 0.01m);
        try
        {
            return await VideoGenerateCreditPricing.TryHoldMillisAsync(
                JsonSerializer.Serialize(config, VideoGenerateStepConfig.JsonOptions), _providers, _videoClients, _prices, ct);
        }
        finally
        {
            config.MaxSpendCredits = cap;
        }
    }

    /// <summary>
    /// The step's own spend cap (the budget gate's step limit): the estimate's high figure in credits, rounded up to the
    /// cent, and never below what the vendor itself asks for the takes (so a bring-your-own model, held at nothing, still has
    /// a positive cap). A self-hosted ComfyUI model is budgeted in GPU minutes instead. False when nothing could be priced.
    /// </summary>
    private bool SetSpendCap(
        VideoGenerateStepConfig config, ResolvedVideoGenerationProvider provider, VideoGenerationCapabilities capabilities,
        IVideoGenerationClient client, VideoSubjectPath path, long? holdMillis)
    {
        if (!capabilities.PricePerSecondByResolution.TryGetValue(config.Resolution, out decimal perSecond)) return false;
        decimal takes = perSecond * config.DurationSeconds * config.Takes;
        decimal held = holdMillis is > 0 ? holdMillis.Value / 1000m : 0m;

        if (provider.Kind == InferenceProviderKind.ComfyUI)
        {
            // The vendor figure is GPU-seconds; credits never count.
            config.MaxGpuMinutes = UpToCent(takes / 60m);
            config.MaxSpendCredits = held > 0 ? UpToCent(held) : 0m;
            return config.MaxGpuMinutes > 0;
        }

        decimal vendorUsd = takes;
        if (path == VideoSubjectPath.References)
        {
            try
            {
                vendorUsd += VideoGenerateCreditPricing.MaxInputMaterialUsd(client, config.Model, config.Resolution, config.EffectiveSubjects.Count) * config.Takes;
            }
            catch (NotSupportedException ex)
            {
                _logger.LogInformation(ex, "The input material of {Model} cannot be priced for a subject clip", config.Model);
                return false;
            }
        }

        decimal cap = Math.Max(held, CreditCurrency.CreditsFromUsd(vendorUsd));
        config.MaxSpendCredits = Math.Max(0.01m, UpToCent(cap));
        return true;
    }

    private static decimal UpToCent(decimal value) => Math.Ceiling(value * 100m) / 100m;

    private async Task<WorkflowPreflightResult> PreflightAsync(Project project, string configJson, CancellationToken ct)
    {
        Guid placeholder = await _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.AgentType == AgentType.VideoTransform)
            .OrderByDescending(a => a.IsBuiltIn)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);
        WorkflowStep step = new()
        {
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson,
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = placeholder,
            RegisterOutputAsProjectFile = true,
            CacheMode = StepCacheMode.Never
        };
        return await _costEstimator!.PreflightAsync(project.OrganizationId, [step], ct);
    }

    private static bool TryParseMode(string? text, out VideoSubjectMode mode)
    {
        mode = VideoSubjectMode.Auto;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return Enum.TryParse(text.Trim(), ignoreCase: true, out mode) && Enum.IsDefined(mode) && !int.TryParse(text, out _);
    }

    private static double Credits(long millis) => Math.Round(millis / 1000.0, 3);

    private static AvatarRefusal Invalid(IReadOnlyList<string> errors) =>
        AvatarRefusal.BadRequest(SubjectClipCodes.RequestInvalid, "Check the clip's settings.",
            new Dictionary<string, object?> { ["errors"] = errors });

    private static AvatarRefusal FromPlan(PlanGateViolation violation) =>
        new(violation.StatusCode, violation.Code, violation.Message, new Dictionary<string, object?>
        {
            ["feature"] = violation.Feature,
            ["limit"] = violation.Limit,
            ["current"] = violation.Current,
            ["incoming"] = violation.Incoming,
            ["plan"] = violation.Plan
        });

    /// <summary>The refusal every route answers as a bare <c>404</c>.</summary>
    internal static AvatarRefusal NotFound() => new(StatusCodes.Status404NotFound, "not_found", "Not found.");

    private static AvatarRefusal EmailNotVerified() =>
        new(StatusCodes.Status403Forbidden, EmailVerificationGate.ErrorCode, EmailVerificationGate.Message);

    // ------------------------------------------------------------------------------------------------ status, list and stop

    public async Task<SubjectClipStatus?> GetAsync(Guid projectId, Guid executionId, CancellationToken ct)
    {
        WorkflowExecution? execution = await Runs(projectId)
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);
        return execution is null ? null : await StatusOfAsync(execution, ct);
    }

    public async Task<IReadOnlyList<SubjectClipStatus>> ListAsync(Guid projectId, bool activeOnly, CancellationToken ct)
    {
        IQueryable<WorkflowExecution> runs = Runs(projectId);
        if (activeOnly) runs = runs.Where(e => e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running);
        List<WorkflowExecution> rows = await runs
            .OrderByDescending(e => e.QueuedAt)
            .ThenByDescending(e => e.Id)
            .Take(MaxListed)
            .ToListAsync(ct);

        List<SubjectClipStatus> statuses = new(rows.Count);
        foreach (WorkflowExecution row in rows) statuses.Add(await StatusOfAsync(row, ct));
        return statuses;
    }

    public async Task<SubjectClipOutcome<SubjectClipStopped>> CancelAsync(Guid projectId, Guid executionId, Guid userId, CancellationToken ct)
    {
        WorkflowExecution? execution = await Runs(projectId).FirstOrDefaultAsync(e => e.Id == executionId, ct);
        if (execution is null) return SubjectClipOutcome<SubjectClipStopped>.Refused(NotFound());

        if (execution.Status is not (ExecutionStatus.Queued or ExecutionStatus.Running))
        {
            return SubjectClipOutcome<SubjectClipStopped>.Refused(AvatarRefusal.Conflict(SubjectClipCodes.AlreadyFinished,
                "This clip has already finished."));
        }

        if (_publish is null)
        {
            return SubjectClipOutcome<SubjectClipStopped>.Refused(new AvatarRefusal(StatusCodes.Status503ServiceUnavailable,
                "STOP_UNAVAILABLE", "Stopping is not available right now."));
        }

        // The same event the workflow stop route publishes, so the engine's stop path (the hold released) is the same.
        await _publish.Publish(new WorkflowExecutionStopRequested { ExecutionId = executionId, RequestedByUserId = userId }, ct);
        return SubjectClipOutcome<SubjectClipStopped>.Of(new SubjectClipStopped(executionId, "stopping"));
    }

    /// <summary>The project's subject clip runs: another project's run, another kind's and a missing one are all absent.</summary>
    private IQueryable<WorkflowExecution> Runs(Guid projectId) =>
        _db.WorkflowExecutions.AsNoTracking()
            .Include(e => e.WorkflowDefinition).ThenInclude(w => w.Steps)
            .Where(e => e.ProjectId == projectId && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.SubjectClip);

    /// <summary>
    /// A run as the editor reads it: the state, the subjects by name, and once ready the project files its takes became
    /// (a held result, or one whose files cannot be found, is <c>failed</c> with <c>OUTPUT_UNAVAILABLE</c>: nothing is served).
    /// </summary>
    private async Task<SubjectClipStatus> StatusOfAsync(WorkflowExecution execution, CancellationToken ct)
    {
        WorkflowStep? step = execution.WorkflowDefinition.Steps.FirstOrDefault(s => s.StepType == StepType.VideoGenerate);
        IReadOnlyList<SubjectClipSubjectName> subjects = await SubjectsOfAsync(execution.ProjectId, step, ct);

        string state = execution.Status switch
        {
            ExecutionStatus.Queued => "queued",
            ExecutionStatus.Running => "running",
            ExecutionStatus.Passed => "ready",
            ExecutionStatus.Cancelled => "cancelled",
            _ => "failed"
        };

        WorkflowStepResult? result = step is null
            ? null
            : await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.WorkflowExecutionId == execution.Id && r.WorkflowStepId == step.Id && r.SupersededAt == null)
                .OrderByDescending(r => r.ExecutedAt)
                .FirstOrDefaultAsync(ct);

        if (state == "ready")
        {
            if (result is null || result.Status != StepStatus.Completed || await OutputGate.IsResultHeldAsync(_db, result.Id, ct))
                return Unserved(execution.Id, subjects);

            List<Guid> files = await OutputFilesAsync(execution, result, ct);
            return files.Count == 0
                ? Unserved(execution.Id, subjects)
                : new SubjectClipStatus(execution.Id, "ready", files, null, null, subjects);
        }

        if (state is "failed" or "cancelled")
        {
            (string? failed, string? error) = MetaOf(result?.OutputJson);
            string? code = result?.FailureCode ?? failed;
            string? message = error ?? result?.ErrorDetails ?? execution.ErrorMessage;
            return new SubjectClipStatus(execution.Id, state, [], NullIfBlank(message), NullIfBlank(code), subjects);
        }

        return new SubjectClipStatus(execution.Id, state, [], null, null, subjects);
    }

    private static SubjectClipStatus Unserved(Guid executionId, IReadOnlyList<SubjectClipSubjectName> subjects) =>
        new(executionId, "failed", [], "The clip is not available.", SubjectClipCodes.OutputUnavailable, subjects);

    /// <summary>
    /// The project files the engine registered for the run's delivered takes, in take order: the objects under the run's
    /// own <c>agentFiles/generated/{executionId}/</c> folder (the step is never served from the cache, so its takes live
    /// there), and the step's first take. Quarantined and held files are left out.
    /// </summary>
    private async Task<List<Guid>> OutputFilesAsync(WorkflowExecution execution, WorkflowStepResult result, CancellationToken ct)
    {
        Guid projectId = execution.ProjectId;
        string prefix = ProjectOutputKeys.GeneratedPrefix(projectId) + execution.Id.ToString("D") + "/";
        string? first = result.OutputStorageKey;
        List<ProjectFile> files = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId
                && (f.StorageKey.StartsWith(prefix) || (first != null && f.StorageKey == first))
                && f.ModerationStatus != FileModerationStatus.Quarantined
                && f.CsamStatus != CsamScanStatus.Quarantined)
            .ToListAsync(ct);
        if (files.Count == 0) return [];

        HashSet<Guid> held = await OutputGate.HeldFileIdsAsync(_db, projectId, files, ct);
        return [.. files
            .Where(f => !held.Contains(f.Id))
            .OrderBy(f => TakeOrder(f.StorageKey))
            .ThenBy(f => f.StorageKey, StringComparer.Ordinal)
            .Select(f => f.Id)];
    }

    /// <summary>The take number of a generated clip key (<c>.../step-1-v{n}.mp4</c>); unknown shapes sort last.</summary>
    private static int TakeOrder(string key)
    {
        string name = Path.GetFileNameWithoutExtension(key);
        int v = name.LastIndexOf("-v", StringComparison.Ordinal);
        return v >= 0 && int.TryParse(name[(v + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
    }

    /// <summary>The subjects the run's step names, by name and kind, in order; deleted ones and kinds switched off are left out.</summary>
    private async Task<IReadOnlyList<SubjectClipSubjectName>> SubjectsOfAsync(Guid projectId, WorkflowStep? step, CancellationToken ct)
    {
        if (step?.VideoGenerateConfigJson is not { Length: > 0 } json) return [];
        VideoGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, VideoGenerateStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        Guid[] ids = [.. config?.EffectiveSubjects.Select(s => s.AvatarId) ?? []];
        if (ids.Length == 0) return [];
        var rows = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.SubjectKind })
            .ToListAsync(ct);
        List<SubjectClipSubjectName> names = [];
        foreach (Guid id in ids)
        {
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row is not null && _options.IsEnabled(row.SubjectKind)) names.Add(new SubjectClipSubjectName(row.Name, row.SubjectKind.ToString()));
        }

        return names;
    }

    /// <summary>The <c>meta.failed</c> code and <c>meta.error</c> sentence of a failed VideoGenerate output.</summary>
    private static (string? Failed, string? Error) MetaOf(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return (null, null);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object)
                return (null, null);
            string? failed = meta.TryGetProperty("failed", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            string? error = meta.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return (failed, error);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
