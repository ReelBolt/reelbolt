using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Styles;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.VideoGenerate"/> steps: budget-gated, crash-resumable
/// submit/poll/download, normalize, and upload. Never throws.
/// </summary>
/// <remarks>
/// Two plan sources, one money path. <see cref="VideoGeneratePlanSource.Inline"/> is phase 1
/// unchanged: one prompt, <c>Takes</c> clips. <see cref="VideoGeneratePlanSource.StepRef"/> walks a
/// <c>ShotDirector</c> plan and generates one clip per planned shot under the same
/// <c>MaxClips</c>/<c>MaxSpendCredits</c>/ledger rules — see <see cref="ExecutePlanAsync"/> for the
/// pre-scan order (egress, then keyframes, then the cap trim, then pricing) and why each one runs
/// before any row can exist.
/// </remarks>
public class VideoGenerateStepExecutor : IStepExecutor, IChangeRequestAwareStepExecutor
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Read/write options for the two JSON documents this executor consumes and produces besides
    /// the step config: a planner step's <c>GeneratedShotPlanOutput</c> and a <c>VideoAnalyze</c>
    /// step's <see cref="VideoAnalysisArtifact"/>, plus the manifest it writes back. Same Web
    /// defaults (+ string enums) every other video executor uses for those artifacts.
    /// </summary>
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string StatusSucceeded = "succeeded";
    private const string StatusFailed = "failed";
    private const string StatusSkipped = "skipped";

    /// <summary>Why a planned clip was never submitted: the plan offers more clips than the cap.</summary>
    private const string SkipMaxClipsExceeded = "MAX_CLIPS_EXCEEDED";

    /// <summary><c>meta.note</c> for a plan that legitimately offers no shots at all.</summary>
    private const string NoteEmptyPlan = "EMPTY_PLAN";

    private readonly IVideoGenerationBudgetGate _budgetGate;
    private readonly IInferenceProviderResolver _providerResolver;
    private readonly IVideoGenerationClientFactory _clientFactory;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbContextFactory;
    private readonly GeneratedClipNormalizer _normalizer;
    private readonly IMediaWorkspaceFactory _mediaWorkspaces;
    private readonly IKeyframeExtractor _keyframeExtractor;
    private readonly ILogger<VideoGenerateStepExecutor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ExecutionRunFlags _runFlags;
    private readonly IUsageRecorder? _usageRecorder;
    private readonly Services.ExecutionLeaseService? _leases;
    /// <summary>T-005: optional so the unit tests that do not care about moderation construct as before.</summary>
    private readonly IContentModerationGate? _moderation;
    private readonly IModerationMediaSampler? _sampler;
    private readonly IStepChangeRequestContext? _changeRequests;

    // Avatars phase 2 (docs/design/avatars.md "Engine and generation"). Optional so every existing construction keeps
    // compiling; a step that names an avatar on a host without them fails AVATAR_GENERATION_DISABLED, never "no avatar".
    private readonly IAvatarResolver? _avatars;
    private readonly AvatarGenerationOptions _avatarOptions;
    private readonly IGeneratedClipProvenanceSigner _provenance;

    // Avatars phase 3, the still-then-animate path: the speech that drives a talking head and the still it animates.
    // Optional like the rest; a talking-head step on a host without them fails before anything is reserved.
    private readonly IAvatarSpeechTrackSource? _avatarSpeech;
    private readonly IAvatarStillMaker? _avatarStills;

    // Per-provider concurrency limiters (VideoGeneration:MaxConcurrentPerProvider), keyed by
    // ProviderId ?? Guid.Empty. Instance-level on purpose, not static: the executor is registered
    // as a DI singleton (AddVideoGeneration), so this is already process-wide in production, while
    // a static would leak slots between executor instances (e.g. between tests).
    private readonly ISubjectFidelityCheck? _fidelity;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _providerSlots = new();
    private readonly int _slotsPerProvider;

    public VideoGenerateStepExecutor(
        IVideoGenerationBudgetGate budgetGate,
        IInferenceProviderResolver providerResolver,
        IVideoGenerationClientFactory clientFactory,
        IProjectFileWorkspace workspace,
        IDbContextFactory<WorkflowEngineDbContext> dbContextFactory,
        GeneratedClipNormalizer normalizer,
        IOptions<VideoGenerationOptions> videoGenerationOptions,
        ILogger<VideoGenerateStepExecutor> logger,
        IMediaWorkspaceFactory mediaWorkspaces,
        IKeyframeExtractor keyframeExtractor,
        TimeProvider? timeProvider = null,
        ExecutionRunFlags? runFlags = null,
        IUsageRecorder? usageRecorder = null,
        Services.ExecutionLeaseService? leases = null,
        IContentModerationGate? moderation = null,
        IModerationMediaSampler? sampler = null,
        IStepChangeRequestContext? changeRequests = null,
        IAvatarResolver? avatars = null,
        IOptions<AvatarGenerationOptions>? avatarOptions = null,
        IGeneratedClipProvenanceSigner? provenance = null,
        IAvatarSpeechTrackSource? avatarSpeech = null,
        IAvatarStillMaker? avatarStills = null,
        ISubjectFidelityCheck? fidelity = null)
    {
        _fidelity = fidelity;
        _avatarSpeech = avatarSpeech;
        _avatarStills = avatarStills;
        _avatars = avatars;
        _avatarOptions = avatarOptions?.Value ?? new AvatarGenerationOptions();
        _provenance = provenance ?? new NoProvenanceSigner();
        _changeRequests = changeRequests;
        _moderation = moderation;
        _sampler = sampler;
        _usageRecorder = usageRecorder;
        _leases = leases;
        _runFlags = runFlags ?? new ExecutionRunFlags();
        _budgetGate = budgetGate;
        _providerResolver = providerResolver;
        _clientFactory = clientFactory;
        _workspace = workspace;
        _dbContextFactory = dbContextFactory;
        _normalizer = normalizer;
        _mediaWorkspaces = mediaWorkspaces;
        _keyframeExtractor = keyframeExtractor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _slotsPerProvider = Math.Max(1, videoGenerationOptions.Value.MaxConcurrentPerProvider);
    }

    private SemaphoreSlim ProviderSlot(Guid providerKey) =>
        _providerSlots.GetOrAdd(providerKey, _ => new SemaphoreSlim(_slotsPerProvider, _slotsPerProvider));

    public StepType StepType => StepType.VideoGenerate;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var step = context.Step;
            var execution = context.Execution;

            // Parse config
            VideoGenerateStepConfig? config;
            try
            {
                if (string.IsNullOrWhiteSpace(step.VideoGenerateConfigJson))
                {
                    return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", "No VideoGenerateConfigJson configured.", context.CurrentStepIndex, context.IterationCount);
                }

                config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
                    step.VideoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions);

                if (config is null)
                {
                    return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", "VideoGenerateConfigJson deserialized to null.", context.CurrentStepIndex, context.IterationCount);
                }

                // Avatars phase 2: the run brief's avatar fills an inline step that names none (never the egress flag).
                config = Services.Briefs.RunBriefOverrides.ApplyToVideoGenerate(config, Services.Briefs.RunBriefAmbient.Current);

                var validationErrors = config.Validate();
                if (validationErrors.Count > 0)
                {
                    return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", string.Join("; ", validationErrors), context.CurrentStepIndex, context.IterationCount);
                }
            }
            catch (JsonException ex)
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", $"VideoGenerateConfigJson is not valid JSON: {ex.Message}", context.CurrentStepIndex, context.IterationCount);
            }

            // Resolve provider and get client. This runs BEFORE any prompt hygiene now, because the
            // prompt's length cap is per provider KIND (GeneratedPromptSanitizer.MaxPromptLengthFor)
            // and the kind is only known once the provider row has been resolved.
            // A talking-head step (avatar audio) resolves through the LipSync capability and never falls back to
            // VideoGeneration; every other step resolves exactly as before.
            var provider = await LipSyncStepResolution.ResolveAsync(_providerResolver, config, context.CancellationToken);

            if (provider is null)
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "PROVIDER_NOT_FOUND",
                    LipSyncStepResolution.IsLipSync(config)
                        ? LipSyncStepResolution.NoModelMessage
                        : "No video generation provider configured.",
                    context.CurrentStepIndex, context.IterationCount);
            }

            // Everything below reads config.Model/Resolution/Ratio (submit, price, ledger, capability checks): make them the effective ones.
            // This local copy is never written back to the step.
            LipSyncStepResolution.Apply(config, provider);

            // Exactly one cap is meaningful for the resolved KIND — credits for a priced vendor,
            // GPU-minutes for a self-hosted ComfyUI — and pairing the wrong one is refused here,
            // before any pricing or ledger work, rather than silently read as "no cap".
            var kindErrors = config.ValidateForKind(provider.Kind);
            if (kindErrors.Count > 0)
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", string.Join("; ", kindErrors), context.CurrentStepIndex, context.IterationCount);
            }

            var client = _clientFactory.Get(provider);

            // Resolve what the model actually offers AND what this exact configuration costs, BEFORE
            // any ledger row can exist. For a provider that publishes a price table this is a pure,
            // no-network lookup; for a provider that publishes none (Higgsfield) it is the async
            // cached price oracle, asked ONCE for the one configuration this step will submit. Both
            // arms fail closed: an unpriceable model throws rather than being offered at $0.
            VideoGenerationPricingBasis pricing;
            try
            {
                pricing = await VideoGenerationPricingBasis.ResolveAsync(
                    client,
                    new VideoGenerationPriceQuery(config.Model, config.Resolution, config.Ratio, config.DurationSeconds),
                    context.CancellationToken);
            }
            catch (VideoGenerationPriceUnavailableException ex)
            {
                // The vendor or the network could not price it right now: nothing the person did. A platform code, so the
                // step stays retryable (CONFIG_INVALID is UserInput and would stop at once). Nothing has been written.
                _logger.LogWarning(ex, "Video price lookup unavailable (Model={Model})", config.Model);
                return CreateFailedResult(execution.Id, step.StepOrder, "VIDEO_PRICE_UNAVAILABLE",
                    "The video provider's price for this model could not be fetched right now. Nothing was submitted.",
                    context.CurrentStepIndex, context.IterationCount);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                // NotSupportedException is what Describe has always thrown for an unpriceable model,
                // and what the oracle throws when it could not fetch a price — the model stays
                // UNOFFERABLE and the reason is surfaced. ArgumentException is the oracle's own
                // documented response to a malformed query (blank model/resolution/ratio,
                // non-positive duration), which is a config fault and must not escape to the outer
                // catch as UNEXPECTED_ERROR. Neither path has written anything: no ledger row exists.
                return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", ex.Message, context.CurrentStepIndex, context.IterationCount);
            }

            var capabilityErrors = config.Validate(pricing.Capabilities);
            if (capabilityErrors.Count > 0)
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", string.Join("; ", capabilityErrors), context.CurrentStepIndex, context.IterationCount);
            }

            // Change request (docs/change-requests.md): found only on a replay run of this very step. Resolved
            // after config/provider/pricing so a refused config still costs nothing, and before any plan work.
            VideoClipChangePlan? change = null;
            StepChangeRequestScope? changeScope = _changeRequests is null
                ? null
                : await _changeRequests.FindAsync(execution, step, context.CancellationToken);
            if (changeScope is { Malformed: true })
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "CHANGE_NOT_APPLIED",
                    StepChangeRequestScope.MalformedRefusal, context.CurrentStepIndex, context.IterationCount);
            }
            if (changeScope is not null)
            {
                (IReadOnlyList<JsonObject>? previousClips, string? previousError) =
                    await LoadPreviousClipsAsync(execution.ProjectId, changeScope.ArtifactStorageKey, context.CancellationToken);
                if (previousClips is null)
                {
                    return CreateFailedResult(execution.Id, step.StepOrder, "CHANGE_NOT_APPLIED",
                        previousError ?? "The clips of the finished step are not available.", context.CurrentStepIndex, context.IterationCount);
                }

                change = new VideoClipChangePlan(changeScope.Payload, previousClips);
                if (change.Matched == 0)
                {
                    return CreateFailedResult(execution.Id, step.StepOrder, "CHANGE_NOT_APPLIED",
                        "The change request matched none of the step's clips; nothing was made.", context.CurrentStepIndex, context.IterationCount);
                }
            }

            ChangeRun? changeRun = change is null ? null : new ChangeRun(changeScope!.RequestId, change);

            return config.PlanSource is VideoGeneratePlanSource.StepRef or VideoGeneratePlanSource.ProjectFile
                ? await ExecutePlanAsync(context, config, provider, client, pricing, sw, changeRun)
                : await ExecuteInlineAsync(context, config, provider, client, pricing, sw, changeRun);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in VideoGenerateStepExecutor");
            sw.Stop();

            return new StepExecutionResult
            {
                Status = StepStatus.Failed,
                // S-13: tenants read this output; the exception text (hosts, paths) stays in the log line above.
                Output = CreateErrorOutput(context.Execution.Id, context.Step.StepOrder, "UNEXPECTED_ERROR", UnexpectedStepSentence),
                DurationMs = (int)sw.ElapsedMilliseconds,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount
            };
        }
    }

    // ---------------------------------------------------------------------
    // Inline plan source (phase 1, unchanged apart from the shared sanitizer)
    // ---------------------------------------------------------------------

    private async Task<StepExecutionResult> ExecuteInlineAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        IVideoGenerationClient client,
        VideoGenerationPricingBasis pricing,
        System.Diagnostics.Stopwatch sw,
        ChangeRun? changeRun = null)
    {
        var execution = context.Execution;
        var step = context.Step;

        // The ONE shared prompt-hygiene pass (control characters stripped, then measured against
        // the resolved provider kind's cap). Control characters are stripped FIRST so the emptiness
        // check below sees the post-sanitization result: a prompt made only of control characters
        // must be rejected as empty, never submitted as an empty string.
        var sanitized = GeneratedPromptSanitizer.Sanitize(config.InlinePrompt, provider.Kind);

        if (sanitized.IsEmpty)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "INVALID_PROMPT", "InlinePrompt is empty or whitespace.", context.CurrentStepIndex, context.IterationCount);
        }

        if (sanitized.ExceedsMaxLength)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "INVALID_PROMPT", $"InlinePrompt exceeds {sanitized.MaxLength} characters.", context.CurrentStepIndex, context.IterationCount);
        }

        // T-105: the first/last frame images, resolved to bytes BEFORE anything is reserved or submitted. The
        // egress consent and the provider's image capability are checked first, so a refusal costs nothing.
        IReadOnlyList<VideoGenerationImageInput>? frameImages = null;
        var inlineMeta = new JsonObject();

        // Avatars phase 3: a step that names the avatar's speech but no avatar (the run brief should have filled it)
        // is refused, never run as a clip of nobody with the speech silently dropped.
        if (!config.HasAvatar && config.HasAvatarAudio)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID",
                "This step makes an avatar say its narration, and no avatar was chosen. Choose the avatar for the run " +
                "(or set it on the step). Nothing was submitted.",
                context.CurrentStepIndex, context.IterationCount, userFacing: true);
        }

        // Avatars phase 2: the avatar's reference material, resolved and read BEFORE anything is reserved. Every refusal
        // here (switch off, egress off, model cannot carry it, consent gone, a file changed) costs nothing. Several
        // subjects per clip (docs/design/avatar-objects.md): the one legacy avatar or the step's Subjects, the same way.
        AvatarInputs? avatar = null;
        if (config.HasAvatar)
        {
            var (prepared, avatarRefusal) = await PrepareSubjectsAsync(
                context, config, provider, client, config.EffectiveSubjects, config.NamesSubjects, inlineMeta);
            if (avatarRefusal is not null)
            {
                return avatarRefusal;
            }

            avatar = prepared;

            // The identity facts of the objects shown join the prompt as a first-party constraint (docs/design/avatar-objects.md
            // "Object fidelity"); a talking head takes no prompt.
            if (avatar is { AnimatesStill: false } && SubjectIdentityPrompt.ClauseFor(avatar.Resolved) is { } inlineClause)
            {
                sanitized = SubjectIdentityPrompt.Apply(config.InlinePrompt, inlineClause, provider.Kind, out _);
            }
        }
        if (config.HasFrameInputs)
        {
            var (resolvedImages, refusal) = await ResolveInlineFrameImagesAsync(context, config, provider, client, inlineMeta);
            if (refusal is not null)
            {
                return refusal;
            }

            frameImages = resolvedImages;
        }

        // T-005: the prompt and its downscaled reference images are screened BEFORE any budget is
        // reserved, so a blocked prompt can never leak spend. Media sampling failures fail open: the
        // text is still screened and the image is simply skipped.
        // A change request moderates each distinct prompt of the clips it actually makes (below), so a
        // carried-over clip's old prompt is never re-screened.
        if (changeRun is null)
        {
            StepExecutionResult? moderationRefusal =
                await ModeratePromptAsync(context, sanitized.Text, frameImages, context.CancellationToken);
            if (moderationRefusal is not null)
            {
                return moderationRefusal;
            }
        }

        // Avatars phase 3: a talking-head model animates a still of the avatar. It is made (or found) only now, after
        // the prompt was screened, and before anything is reserved; the take then lasts as long as the speech.
        if (avatar is { AnimatesStill: true })
        {
            var (withStill, stillRefusal) = await MakeAvatarStillAsync(context, config, avatar, sanitized.Text, inlineMeta);
            if (stillRefusal is not null)
            {
                return stillRefusal;
            }

            avatar = withStill!;
        }
        else if (avatar is { Path: VideoSubjectPath.FirstFrame })
        {
            // The FirstFrame path: ONE composed still that shows every subject in the scene of the prompt, made (or
            // found) only now, after the prompt was screened and before anything is reserved.
            var (withScene, sceneRefusal) = await MakeSubjectStillAsync(context, config, avatar, sanitized.Text, config.Ratio, inlineMeta);
            if (sceneRefusal is not null)
            {
                return sceneRefusal;
            }

            avatar = withScene!;
        }

        var request = avatar is { AnimatesStill: true, Still: { } still }
            ? new VideoGenerationRequest(
                config.Model,
                avatar.TakesPrompt ? sanitized.Text : string.Empty,
                avatar.SpeechSeconds,
                config.Resolution,
                config.Ratio,
                [new VideoGenerationImageInput(still.ContentType, still.Bytes)],
                References: avatar.References)
            { NoVendorRetention = true }
            : avatar is { Path: VideoSubjectPath.FirstFrame, Still: { } scene }
            // The composed still IS the first frame; no reference item is sent. A person's still is not kept by the vendor.
            ? new VideoGenerationRequest(
                config.Model,
                sanitized.Text,
                config.DurationSeconds,
                config.Resolution,
                config.Ratio,
                [new VideoGenerationImageInput(scene.ContentType, scene.Bytes)])
            { NoVendorRetention = avatar.ShowsPerson }
            : new VideoGenerationRequest(
                config.Model,
                sanitized.Text,
                config.DurationSeconds,
                config.Resolution,
                config.Ratio,
                frameImages,
                References: avatar?.References);

        var slots = new List<ClipSlot>(config.Takes);
        var moderatedTexts = new HashSet<string>(StringComparer.Ordinal);
        int dispatchOrdinal = 0;
        for (int clipIndex = 1; clipIndex <= config.Takes; clipIndex++)
        {
            string inlineClipId = $"clip-{clipIndex}";
            VideoGenerationRequest takeRequest = request;
            bool retake = false;
            if (changeRun is not null)
            {
                if (!changeRun.Plan.InScope(inlineClipId))
                {
                    var (carried, refusal) = TryCarry(context, changeRun, inlineClipId, null, clipIndex, null, KeyframeState.NotRequested);
                    if (refusal is not null)
                    {
                        return refusal;
                    }

                    // A kept clip of a person is new use of their likeness: it is kept only if it was made with the
                    // avatar exactly as it is now (same consent, same references). Otherwise nothing is made.
                    if (avatar is not null
                        && !string.Equals(
                            carried!.PreResolvedClip![avatar.ClipFingerprintKey]?.GetValue<string>(),
                            avatar.Fingerprint, StringComparison.Ordinal))
                    {
                        return CreateFailedResult(execution.Id, step.StepOrder, AvatarFailureCodes.ConsentMissing,
                            $"Clip '{inlineClipId}' was made with the avatar as it was before its consent or references changed, " +
                            "so it cannot be kept. Redo every clip of this step. Nothing was made.",
                            context.CurrentStepIndex, context.IterationCount);
                    }

                    if (VideoClipChangePlan.WasDispatched(carried!.PreResolvedClip!))
                    {
                        dispatchOrdinal++;
                    }

                    slots.Add(carried);
                    continue;
                }

                retake = changeRun.Plan.RetakeFor(inlineClipId);
                if (changeRun.Plan.PromptFor(inlineClipId) is string && avatar is { AnimatesStill: true, TakesPrompt: false })
                {
                    // The words of a talking-head clip are its speech and its look is its still: a new prompt has no
                    // field to go to on this model, and silently dropping it would remake the same clip at a price.
                    return CreateFailedResult(execution.Id, step.StepOrder, "CHANGE_NOT_APPLIED",
                        $"'{config.Model}' takes no prompt (the clip is the avatar's still saying the narration), so a new " +
                        "prompt cannot change it. Change the narration, or ask for a retake. Nothing was made.",
                        context.CurrentStepIndex, context.IterationCount);
                }

                if (changeRun.Plan.PromptFor(inlineClipId) is string overridePrompt)
                {
                    var overrideSanitized = SubjectIdentityPrompt.Apply(
                        overridePrompt, avatar is null ? null : SubjectIdentityPrompt.ClauseFor(avatar.Resolved), provider.Kind, out _);
                    if (overrideSanitized.IsEmpty || overrideSanitized.ExceedsMaxLength)
                    {
                        return CreateFailedResult(execution.Id, step.StepOrder, "INVALID_PROMPT",
                            overrideSanitized.IsEmpty ? "The new prompt is empty or whitespace." : $"The new prompt exceeds {overrideSanitized.MaxLength} characters.",
                            context.CurrentStepIndex, context.IterationCount);
                    }

                    takeRequest = request with { PromptText = overrideSanitized.Text };
                }

                if (moderatedTexts.Add(takeRequest.PromptText))
                {
                    StepExecutionResult? changeRefusal =
                        await ModeratePromptAsync(context, takeRequest.PromptText, frameImages, context.CancellationToken);
                    if (changeRefusal is not null)
                    {
                        return changeRefusal;
                    }
                }
            }

            dispatchOrdinal++;
            slots.Add(new ClipSlot(
                inlineClipId,
                ShotIndex: null,
                Take: clipIndex,
                Shot: null,
                DurationSeconds: avatar is { AnimatesStill: true } ? avatar.SpeechSeconds : config.DurationSeconds,
                Request: takeRequest,
                Keyframe: frameImages is null ? KeyframeState.NotRequested : KeyframeState.AppliedTo(),
                PreResolvedClip: null,
                HashOrdinal: changeRun is null ? null : dispatchOrdinal,
                Retake: retake));
        }

        return await ReserveAndDispatchAsync(
            context,
            config,
            provider,
            client,
            pricing,
            new DispatchPlan(slots, inlineMeta, IsPlanPath: false, PlanStepOrder: null, AnalysisStepOrder: null, ShotsPlanned: 0, Change: changeRun, Avatar: avatar),
            sw);
    }

    // ---------------------------------------------------------------------
    // Content moderation (content-moderation T-005)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Screens one generation prompt (plus its downscaled reference images) before any budget is
    /// reserved. Returns a failed result only on a high-confidence Block; an unavailable call or the
    /// mid band proceeds (generation fails open), with the gap logged.
    /// </summary>
    private async Task<StepExecutionResult?> ModeratePromptAsync(
        StepExecutionContext context,
        string text,
        IReadOnlyList<VideoGenerationImageInput>? images,
        CancellationToken ct)
    {
        if (_moderation is null)
        {
            return null;
        }

        IReadOnlyList<ModerationImageInput> sampled =
            await SampleForModerationAsync(images, ct);
        var attribution = new ModerationAttribution(
            context.Execution.OrganizationId,
            context.Execution.ProjectId,
            context.Execution.InitiatedByUserId,
            context.Execution.Id,
            context.StepResultId,
            ModerationSurface.GenerationPrompt,
            ModerationContentKind.Text);

        ModerationDecision decision = await _moderation.ModerateAsync(
            new ModerationInput(text, sampled, ModerationContentKind.Text), attribution, ct);

        if (decision.Action == ModerationAction.Block)
        {
            return CreateFailedResult(
                context.Execution.Id, context.Step.StepOrder, "CONTENT_BLOCKED", decision.UserMessage,
                context.CurrentStepIndex, context.IterationCount);
        }

        if (!decision.Available)
        {
            _logger.LogWarning(
                "Content moderation was unavailable for the VideoGenerate prompt at step {StepOrder}; proceeding because generation fails open",
                context.Step.StepOrder);
        }

        return null;
    }

    /// <summary>
    /// Downscales reference images through the moderation sampler so only derived JPEG frames reach
    /// the model. A sampling failure or an empty sample skips that image — the media never fails a
    /// generation step open surface.
    /// </summary>
    private async Task<IReadOnlyList<ModerationImageInput>> SampleForModerationAsync(
        IReadOnlyList<VideoGenerationImageInput>? images,
        CancellationToken ct)
    {
        if (images is not { Count: > 0 } || _sampler is null)
        {
            return [];
        }

        var sampled = new List<ModerationImageInput>(images.Count);
        foreach (VideoGenerationImageInput image in images)
        {
            try
            {
                using var stream = new MemoryStream(image.Bytes);
                ModerationMediaSample sample = await _sampler.SampleAsync(stream, image.ContentType, ct);
                foreach (byte[] jpeg in sample.Images)
                {
                    sampled.Add(new ModerationImageInput("image/jpeg", jpeg));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not sample a reference image for content moderation; skipping that image");
            }
        }

        return sampled;
    }

    // ---------------------------------------------------------------------
    // Inline first/last frame images (T-105)
    // ---------------------------------------------------------------------

    /// <summary>Largest first/last frame image the step will read into memory and send to a provider.</summary>
    private const long MaxFrameInputBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Resolves <see cref="VideoGenerateStepConfig.FirstFrame"/>/<see cref="VideoGenerateStepConfig.LastFrame"/> (or their
    /// project-file forms) to the request's <c>Images[0]</c>/<c>Images[1]</c>. The order is the whole point: the
    /// EGRESS consent (<see cref="GeneratedShotEgressPolicy"/>, fail-closed: an image of the author's own footage
    /// leaves the building only on an explicit <c>AllowSourceMediaEgress</c>) and the provider's image capability are
    /// both decided BEFORE any byte is read, and all of it before the budget reservation, so a refusal buys nothing.
    /// </summary>
    private async Task<(IReadOnlyList<VideoGenerationImageInput>? Images, StepExecutionResult? Refusal)> ResolveInlineFrameImagesAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        IVideoGenerationClient client,
        JsonObject meta)
    {
        var execution = context.Execution;
        var step = context.Step;
        StepExecutionResult Refuse(string code, string message) =>
            CreateFailedResult(execution.Id, step.StepOrder, code, message, context.CurrentStepIndex, context.IterationCount);

        var wanted = new List<(string Label, ExtractInputRef? Ref, Guid? FileId)>(2);
        if (config.FirstFrame is not null || config.FirstFrameProjectFileId is not null)
        {
            wanted.Add((DescribeFrameInput(config.FirstFrame, config.FirstFrameProjectFileId), config.FirstFrame, config.FirstFrameProjectFileId));
        }

        if (config.LastFrame is not null || config.LastFrameProjectFileId is not null)
        {
            wanted.Add((DescribeFrameInput(config.LastFrame, config.LastFrameProjectFileId), config.LastFrame, config.LastFrameProjectFileId));
        }

        if (wanted.Count == 0)
        {
            return (null, null);
        }

        // Evaluate treats the first label as firstFrame and the second as lastFrame; a config with only a
        // last frame never reaches here (Validate refuses it), so wanted[1] is always the last frame.
        var decision = GeneratedShotEgressPolicy.Evaluate(
            wanted[0].Label, wanted.Count > 1 ? wanted[1].Label : null, config.AllowSourceMediaEgress);
        if (!decision.Allowed)
        {
            return (null, Refuse(
                decision.Code ?? GeneratedShotEgressPolicy.EgressRefusedCode,
                decision.Reason ?? "Source-media egress was refused for this step's first/last frame."));
        }

        if (!VideoGenerationImageSupport.CanAcceptInputImages(provider.Kind))
        {
            return (null, Refuse(
                VideoGenerationImageSupport.UnsupportedCode,
                $"The step requests {wanted.Count} first/last frame image(s), and the resolved provider kind '{provider.Kind}' "
                + "cannot accept an input image for video generation. No clip was submitted."));
        }

        try
        {
            client.EnsureAccepts(config.Model, wanted.Count);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            // The raw text can name the vendor and list its endpoints: it stays in the log, the run page gets a fixed sentence.
            _logger.LogWarning(ex, "Avatar reference images refused (Model={Model})", config.Model);
            return (null, Refuse(VideoGenerationImageSupport.UnsupportedCode,
                "The model chosen for this step cannot take the avatar's pictures as reference images. Choose another model in the app. Nothing was submitted."));
        }

        var images = new List<VideoGenerationImageInput>(wanted.Count);
        var used = new JsonArray();
        string scratch = Path.Combine(Path.GetTempPath(), "reelbolt-frame-inputs-" + Guid.NewGuid().ToString("N"));
        string role = "firstFrame";
        try
        {
            Directory.CreateDirectory(scratch);
            for (int i = 0; i < wanted.Count; i++)
            {
                role = i == 0 ? "firstFrame" : "lastFrame";
                (string? key, string? keyError) = await ResolveFrameInputKeyAsync(context, wanted[i].Ref, wanted[i].FileId);
                if (key is null)
                {
                    return (null, Refuse("FRAME_INPUT_NOT_FOUND", $"{role}: {keyError}"));
                }

                string local = Path.Combine(scratch, $"{role}.bin");

                // S-67: the cap is enforced BY the download, not after it — the object is refused from its own
                // length before a byte is read, and the copy stops at the bound if that length lied. A 20 MB cap
                // that only runs once the file is on disk has already paid for the bytes it refuses.
                await _workspace.DownloadStorageKeyToFileAsync(
                    execution.ProjectId, key, local, MaxFrameInputBytes, context.CancellationToken);

                var info = new FileInfo(local);
                if (!info.Exists || info.Length == 0)
                {
                    return (null, Refuse("FRAME_INPUT_INVALID", $"{role}: the image is empty."));
                }

                byte[] bytes = await File.ReadAllBytesAsync(local, context.CancellationToken);
                string? contentType = ImageDimensions.ContentTypeOf(bytes);
                if (contentType is null)
                {
                    return (null, Refuse("FRAME_INPUT_INVALID", $"{role}: the referenced object is not a PNG or JPEG image."));
                }

                images.Add(new VideoGenerationImageInput(contentType, bytes));

                // QA-P1-1: what the request sent, never where it is stored. The role, the type the bytes were
                // sniffed as and their size are the author's own facts; the storage key is neither (the step
                // output is tenant-visible and carries no key — the same rule the rest of this output follows).
                used.Add(new JsonObject { ["role"] = role, ["contentType"] = contentType, ["bytes"] = bytes.Length });
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw; // the run is being stopped: not a frame problem
        }
        catch (StorageObjectTooLargeException tooLarge)
        {
            _logger.LogWarning(
                tooLarge, "A {Role} image for step {StepOrder} exceeds the {MaxBytes}-byte frame cap.", role, step.StepOrder, MaxFrameInputBytes);
            return (null, Refuse("FRAME_INPUT_INVALID", $"{role}: the image is larger than {MaxFrameInputBytes} bytes."));
        }
        catch (Exception ex)
        {
            // S-67: a download can fail for a hundred storage reasons whose text names keys, hosts and SDK
            // internals. The step answers one fixed sentence; the detail stays in the server log.
            _logger.LogWarning(
                ex, "Reading a {Role} image for step {StepOrder} failed; no clip was submitted.", role, step.StepOrder);
            return (null, Refuse(
                "FRAME_INPUT_UNAVAILABLE",
                $"{role}: the image could not be read from storage. Nothing was submitted; try the run again."));
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the frame-input scratch directory {Scratch}", scratch);
            }
        }

        meta["frameInputs"] = used;
        return (images, null);
    }

    private static string DescribeFrameInput(ExtractInputRef? reference, Guid? projectFileId) =>
        projectFileId is { } id
            ? $"project file {id}"
            : reference is { From: ExtractInputSource.Step } r ? $"step {r.StepOrder}" : "previous step";

    /// <summary>
    /// The storage key a frame input names, authorised to THIS project: a step reference resolves through the
    /// run's own output history, a project file through the project's own file list, and either must sit under
    /// <c>projects/{projectId}/</c>.
    /// </summary>
    private async Task<(string? Key, string? Error)> ResolveFrameInputKeyAsync(
        StepExecutionContext context, ExtractInputRef? reference, Guid? projectFileId)
    {
        Guid projectId = context.Execution.ProjectId;
        string? key;
        if (projectFileId is { } fileId)
        {
            var files = await _workspace.ListFilesAsync(projectId, context.CancellationToken);
            key = files.FirstOrDefault(f => f.Id == fileId)?.StorageKey;
            if (key is null)
            {
                return (null, $"project file '{fileId}' was not found in this project.");
            }
        }
        else
        {
            StepOutputHistoryEntry? entry = reference!.From == ExtractInputSource.Step
                ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == reference.StepOrder)
                : context.StepOutputHistory.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.OutputStorageKey));
            key = entry?.OutputStorageKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return (null, reference.From == ExtractInputSource.Step
                    ? $"step {reference.StepOrder} produced no image output."
                    : "no previous step produced an image output.");
            }
        }

        string prefix = $"projects/{projectId}/";
        return key.StartsWith(prefix, StringComparison.Ordinal) && !key.Contains("..", StringComparison.Ordinal)
            ? (key, null)
            : (null, "the referenced object is outside this project.");
    }

    // ---------------------------------------------------------------------
    // Avatars (phase 2, docs/design/avatars.md "Engine and generation")
    // ---------------------------------------------------------------------

    /// <summary>
    /// What one avatar step carries into dispatch: the avatar, its fingerprint as it is now, the references this model
    /// will receive (already read and digest-checked), the vendor's input-material charge per take, and the key that
    /// joins every take's ledger request hash.
    /// </summary>
    private sealed record AvatarInputs(
        Guid AvatarId,
        string Fingerprint,
        string RequestKey,
        IReadOnlyList<VideoGenerationReference> References,
        decimal InputMaterialUsd,
        int InputVideoSeconds,
        AvatarDisclosure Disclosure)
    {
        /// <summary>A person (the default) or an object (docs/design/avatar-objects.md): it decides how the clip is marked.</summary>
        public AvatarSubjectKind SubjectKind { get; init; } = AvatarSubjectKind.Person;

        /// <summary>Phase 3: the model animates a still of the avatar driven by <see cref="Speech"/> (see <see cref="VideoReferenceInputSupport.AnimatesStill"/>).</summary>
        public bool AnimatesStill { get; init; }

        /// <summary>Phase 3: the model also takes the step's prompt as a motion prompt.</summary>
        public bool TakesPrompt { get; init; }

        /// <summary>Phase 3: the avatar pictures (digest-checked) the still is made from.</summary>
        public IReadOnlyList<AvatarStillPicture>? StillPictures { get; init; }

        /// <summary>Phase 3: the speech track, also the one reference in <see cref="References"/>.</summary>
        public AvatarSpeechTrack? Speech { get; init; }

        /// <summary>Phase 3: the still, once made (or found) after the prompt was screened.</summary>
        public AvatarStill? Still { get; init; }

        /// <summary>Phase 3: the whole seconds of the speech, which the clip lasts and the take is reserved and billed at.</summary>
        public int SpeechSeconds => Speech is null ? 0 : Math.Max(1, (int)Math.Ceiling(Speech.DurationMs / 1000.0));

        // -- Several subjects per clip (docs/design/avatar-objects.md "Subjects in a video generation step"). For the
        //    one legacy avatar on a reference model every member below is at its default and nothing changes.

        /// <summary>How the subjects reach the model (see <see cref="VideoSubjectPaths"/>).</summary>
        public VideoSubjectPath Path { get; init; } = VideoSubjectPath.References;

        /// <summary>
        /// Every subject of the clip with its own fingerprint, for the re-check before each take. Empty for the one
        /// legacy avatar, which is re-checked as (<see cref="AvatarId"/>, <see cref="Fingerprint"/>).
        /// </summary>
        public IReadOnlyList<(Guid AvatarId, string Fingerprint)> Checks { get; init; } = [];

        /// <summary>
        /// The ids the ledger records in <c>subject_ids_json</c> (a step that names <c>Subjects</c>); null for the one
        /// legacy avatar, whose ledger row stays exactly what it was.
        /// </summary>
        public IReadOnlyList<Guid>? SubjectIds { get; init; }

        /// <summary>
        /// True when <see cref="Fingerprint"/> is the fingerprint of the SET (<see cref="SubjectsFingerprint"/>): the
        /// facts are then recorded under <c>meta.subjectFacts</c> and each clip carries <c>subjectsFingerprint</c>.
        /// False for the one legacy avatar and for a talking head (<c>meta.avatar</c>, <c>avatarFingerprint</c>).
        /// </summary>
        public bool SubjectsOnClips { get; init; }

        /// <summary>The FirstFrame path: the subjects and their pictures (digest-checked) the scene still is made from.</summary>
        public IReadOnlyList<SubjectStillSubject>? SceneSubjects { get; init; }

        /// <summary>A person is among the subjects: the clip is marked as that person's avatar asks, and their media is not kept by the vendor.</summary>
        public bool ShowsPerson => SubjectKind == AvatarSubjectKind.Person;

        /// <summary>
        /// Who and what the clip shows, by name (cleaned) and kind, in the order shown: written on each manifest clip as
        /// <c>subjects</c> (docs/design/avatar-objects.md), which the dailies room reads through the analysis view.
        /// </summary>
        public IReadOnlyList<ShownSubject> Shown { get; init; } = [];

        /// <summary>The subjects themselves (their identity facts join the prompt and the take check); empty for a talking head.</summary>
        public IReadOnlyList<ResolvedAvatar> Resolved { get; init; } = [];

        /// <summary>The per-clip key that holds <see cref="Fingerprint"/>.</summary>
        public string ClipFingerprintKey => SubjectsOnClips ? SubjectOutputFacts.ClipFingerprintKey : AvatarOutputFacts.ClipFingerprintKey;

        /// <summary>The <c>meta</c> key of the step-level facts (marking, c2pa, the still).</summary>
        public string FactsKey => SubjectsOnClips ? SubjectFactsKey : AvatarOutputFacts.MetaKey;
    }

    /// <summary><c>meta.subjectFacts</c>: the step-level facts of a step that names <c>Subjects</c> (what <c>meta.avatar</c> holds for the one legacy avatar).</summary>
    private const string SubjectFactsKey = "subjectFacts";

    /// <summary>The largest reference medium of each kind the step will read into memory (the avatar's own limits).</summary>
    private static long MaxReferenceBytes(AvatarReferenceKind kind) => kind switch
    {
        AvatarReferenceKind.Image => ProjectAvatarLimits.MaxImageBytes,
        AvatarReferenceKind.Video => ProjectAvatarLimits.MaxVideoBytes,
        _ => ProjectAvatarLimits.MaxAudioBytes,
    };

    /// <summary>
    /// Resolves and reads the subjects a clip shows (the one legacy avatar, or a step's <c>Subjects</c>: one to three
    /// project avatars of people and objects, at most one of them a person), in an order where every refusal costs
    /// nothing: the deployment's switch PER KIND (<c>Avatars:Enabled</c> + <c>Avatars:Generation</c> for a person,
    /// platform admins only during the smoke run; <c>Avatars:Objects:Enabled</c> for an object), the one-person rule,
    /// the step's egress permission (given only in the app, checked once), the PATH the resolved model carries
    /// subjects on (<see cref="VideoSubjectPaths"/>: a talking head, the model's own reference mode, or one composed
    /// first frame; a text-only route is refused with its sibling's name), every subject through the ONE rule (fails
    /// closed: never "no avatar"), then what the path needs: the references shared out fairly from the stored sizes
    /// and the bytes of what was chosen, or the pictures the scene still is made from, each re-hashed against the
    /// attested digest. Nothing is reserved or submitted before this returns.
    /// <para>
    /// Reusable per clip: it reads nothing of the step but <paramref name="config"/>'s model, mode, permission and
    /// speech, so a plan-sourced step can call it once per planned shot with that shot's subjects (and
    /// <see cref="MakeSubjectStillAsync"/> with that shot's prompt once the prompt was screened).
    /// </para>
    /// </summary>
    /// <param name="subjects">The subjects to show, in order.</param>
    /// <param name="namesSubjects">
    /// False for the one legacy <c>AvatarId</c>: its facts are recorded as <c>meta.avatar</c> and every hash is the one
    /// it always was. True for <c>Subjects</c>: <c>meta.subjects[]</c>, <c>meta.subjectPath</c> and
    /// <c>meta.subjectFacts</c>, and the fingerprint of the set.
    /// </param>
    /// <param name="meta">Where the facts are recorded (never a subject's storage key or a URL).</param>
    private async Task<(AvatarInputs? Inputs, StepExecutionResult? Refusal)> PrepareSubjectsAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        IVideoGenerationClient client,
        IReadOnlyList<VideoGenerateSubject> subjects,
        bool namesSubjects,
        JsonObject meta)
    {
        var execution = context.Execution;
        var step = context.Step;
        StepExecutionResult Refuse(string code, string message) =>
            CreateFailedResult(execution.Id, step.StepOrder, code, message, context.CurrentStepIndex, context.IterationCount, userFacing: true);
        bool one = subjects.Count == 1;

        // (1) The deployment's switches, per kind: a person needs Avatars:Enabled and a Generation mode, and the smoke
        // run is platform admins only; an object needs only Avatars:Objects:Enabled. With every switch off nothing
        // about an avatar is read at all; otherwise each row's kind alone is read first. An id this project does not
        // hold is judged as a person and refused by the resolver below.
        if (_avatars is null
            || (!_avatarOptions.AllowsGeneration(AvatarSubjectKind.Person) && !_avatarOptions.AllowsGeneration(AvatarSubjectKind.Object)))
        {
            return (null, Refuse(AvatarFailureCodes.GenerationDisabled,
                "Generating video with an avatar is not available in this workspace yet. Nothing was submitted."));
        }

        var kinds = new List<AvatarSubjectKind>(subjects.Count);
        foreach (VideoGenerateSubject subject in subjects)
        {
            AvatarSubjectKind kind =
                await _avatars.SubjectKindAsync(execution.ProjectId, subject.AvatarId, context.CancellationToken) ?? AvatarSubjectKind.Person;
            if (!_avatarOptions.AllowsGeneration(kind))
            {
                return (null, Refuse(AvatarFailureCodes.GenerationDisabled,
                    "Generating video with an avatar is not available in this workspace yet. Nothing was submitted."));
            }

            kinds.Add(kind);
        }

        bool anyPerson = kinds.Contains(AvatarSubjectKind.Person);
        if (anyPerson
            && _avatarOptions.Generation == AvatarGenerationMode.PlatformAdminOnly
            && !await InitiatedByPlatformAdminAsync(execution, context.CancellationToken))
        {
            return (null, Refuse(AvatarFailureCodes.GenerationDisabled,
                "Generating video with an avatar is open to platform administrators only while it is being tested. Nothing was submitted."));
        }

        if (kinds.Count(k => k == AvatarSubjectKind.Person) > 1)
        {
            return (null, Refuse("CONFIG_INVALID", OnePersonRule));
        }

        // (2) The step's own egress permission, once for every subject: their media leave only when the customer
        // allowed it in the app.
        if (!config.AllowSourceMediaEgress)
        {
            return (null, Refuse(GeneratedShotEgressPolicy.EgressRefusedCode,
                !one
                    ? "This step shows avatars, which sends their pictures, videos and voice samples to the video " +
                      "provider, but the step's permission to send media is off. Turn it on for this step in the app. Nothing was submitted."
                    : !anyPerson
                    ? "This step shows an object avatar, which sends that object's pictures and videos to the video " +
                      "provider, but the step's permission to send media is off. Turn it on for this step in the app. Nothing was submitted."
                    : "This step shows an avatar, which sends that person's pictures, videos and voice samples to the video " +
                      "provider, but the step's permission to send media is off. Turn it on for this step in the app. Nothing was submitted."));
        }

        // (3) How the resolved model carries subjects, before any byte is read.
        VideoReferenceInputSupport support;
        VideoSubjectPathDecision decision;
        try
        {
            support = client.DeclaredReferenceInputSupport(config.Model);
            decision = VideoSubjectPaths.Decide(
                provider.Kind, config.Model, support, client.DeclaredImageInputSupport(config.Model),
                config.SubjectMode, config.HasAvatarAudio, config.HasFrameInputs);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Avatar reference support refused (Model={Model})", config.Model);
            return (null, Refuse(VideoReferenceInput.NotSupportedCode,
                "The model chosen for this step cannot carry the avatar. Choose another model in the app. Nothing was submitted."));
        }

        if (decision.Refusal is { } unsupported)
        {
            return (null, Refuse(unsupported.Code, NothingSubmitted(unsupported.Message)));
        }

        VideoSubjectPath path = decision.Path!.Value;

        // (4) Every subject, through the one rule. Unusable fails the step (UserInput) with its own code.
        var resolved = new List<ResolvedAvatar>(subjects.Count);
        foreach (VideoGenerateSubject subject in subjects)
        {
            try
            {
                resolved.Add(await _avatars.ResolveRequiredAsync(execution.ProjectId, subject.AvatarId, context.CancellationToken));
            }
            catch (AvatarUnusableException unusable)
            {
                return (null, Refuse(unusable.Code, one
                    ? $"The avatar this step shows cannot be used ({unusable.Reason}). Nothing was submitted."
                    : $"One of the avatars this step shows cannot be used ({unusable.Reason}). Nothing was submitted."));
            }
        }

        // The kinds as resolved are the ones that count (the row read above only chose the switch to ask).
        if (resolved.Count(r => r.SubjectKind == AvatarSubjectKind.Person) > 1)
        {
            return (null, Refuse("CONFIG_INVALID", OnePersonRule));
        }

        // Phase 3: a talking-head model takes no reference pictures of its own; it animates a still made from them.
        if (path == VideoSubjectPath.TalkingHead)
        {
            // A talking head is ONE person saying words: an object, or a second subject, is never sent down that path.
            if (resolved.Count != 1)
            {
                return (null, Refuse("CONFIG_INVALID",
                    $"'{config.Model}' makes one person speak, and this step shows {resolved.Count} avatars. Keep one, a person. Nothing was submitted."));
            }

            if (resolved[0].SubjectKind == AvatarSubjectKind.Object)
            {
                return (null, Refuse(VideoReferenceInput.NotSupportedCode,
                    $"'{config.Model}' makes a person speak, and this step shows an object avatar. Choose a model that takes reference pictures. Nothing was submitted."));
            }

            var (talkingHead, talkingHeadRefusal) =
                await PrepareStillPathAsync(context, config, support, resolved[0], subjects[0].Roles, meta, Refuse);
            if (talkingHead is null)
            {
                return (null, talkingHeadRefusal);
            }

            talkingHead = talkingHead with { Shown = [ShownSubject.Of(resolved[0])] };
            if (!namesSubjects)
            {
                return (talkingHead, null);
            }

            // Named through Subjects: the facts stay where a talking head records them (meta.avatar) and the step
            // also says which subjects it showed, for the ledger and for the reuse guard.
            meta[SubjectOutputFacts.MetaKey] = new JsonArray(SubjectEntry(resolved[0], talkingHead.Fingerprint,
                meta[AvatarOutputFacts.MetaKey]?["referencesSent"]?.DeepClone(), meta[AvatarOutputFacts.MetaKey]?["referencesSkipped"]?.DeepClone()));
            meta[SubjectOutputFacts.PathKey] = SubjectOutputFacts.PathWord(path);
            return (talkingHead with { SubjectIds = [resolved[0].Id] }, null);
        }

        // What every later check and record needs of the set: each subject's fingerprint as it is now, the one
        // identity of the clip (the avatar's own for the legacy avatar, the set's for Subjects), who is shown first
        // (the person when there is one) and how the clip is marked.
        List<string> fingerprints = [.. resolved.Select(AvatarFingerprint.Compute)];
        string identity = namesSubjects ? SubjectsFingerprint.Compute(fingerprints) : fingerprints[0];
        ResolvedAvatar primary = resolved.FirstOrDefault(r => r.SubjectKind == AvatarSubjectKind.Person) ?? resolved[0];
        bool showsPerson = primary.SubjectKind == AvatarSubjectKind.Person;
        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(execution.ProjectId, context.CancellationToken);

        if (path == VideoSubjectPath.FirstFrame)
        {
            return await PrepareFirstFramePathAsync(
                context, config, subjects, resolved, fingerprints, identity, primary, files, namesSubjects, meta, Refuse);
        }

        // (5) The References path. Selection from the STORED sizes, shared out between the subjects in turns (every
        // subject gets one picture before any gets a second), so only what will be sent is read.
        var candidates = new List<List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>>(resolved.Count);
        var skipped = new List<JsonArray>(resolved.Count);
        for (int s = 0; s < resolved.Count; s++)
        {
            HashSet<AvatarReferenceRole>? roles = subjects[s].Roles is { } wanted ? [.. wanted] : null;
            var own = new List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>();
            var ownSkipped = new JsonArray();
            foreach (ResolvedAvatarReference r in resolved[s].References.OrderBy(r => r.Ordinal).ThenBy(r => r.Id))
            {
                if (roles is not null && !roles.Contains(r.Role))
                {
                    ownSkipped.Add(SkippedEntry(r, VideoReferenceInput.SkipRoleNotRequested));
                    continue;
                }

                ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == r.ProjectFileId);
                if (file is null)
                {
                    // The resolver saw it a moment ago; a file gone or hidden since is missing, never skipped.
                    return (null, Refuse(AvatarFailureCodes.ConsentMissing,
                        $"The avatar this step shows cannot be used ({ProjectAvatar.ReasonReferenceMissing}). Nothing was submitted."));
                }

                own.Add((r, file));
            }

            candidates.Add(own);
            skipped.Add(ownSkipped);
        }

        long maxBody = provider.Kind switch
        {
            InferenceProviderKind.MiniMax => MiniMaxVideoGenerationClient.MaxRequestBodyBytes,
            InferenceProviderKind.FalAi => FalAiVideoGenerationClient.MaxReferenceRequestBytes,
            _ => long.MaxValue,
        };
        List<IReadOnlyList<VideoGenerationReference>> shapes = [.. candidates.Select(own => (IReadOnlyList<VideoGenerationReference>)
            [.. own.Select(c => new VideoGenerationReference(c.Ref.Kind, c.Ref.Role, c.Ref.MimeType, [], c.Ref.DurationMs))])];
        IReadOnlyList<VideoReferenceSelection> selections = VideoReferenceInput.SelectShared(
            support, shapes, maxBody, reservedBodyBytes: 64 * 1024, sizeOf: (s, i) => candidates[s][i].File.SizeBytes);

        var chosen = new List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>();
        var chosenSubject = new List<int>();
        for (int s = 0; s < resolved.Count; s++)
        {
            foreach ((int index, string reason) in selections[s].Skipped)
            {
                skipped[s].Add(SkippedEntry(candidates[s][index].Ref, reason));
            }

            if (!selections[s].Chosen.Any(i => candidates[s][i].Ref.Kind == AvatarReferenceKind.Image))
            {
                return (null, Refuse(VideoReferenceInput.NotSupportedCode, one
                    ? $"None of the avatar's pictures can be sent to '{config.Model}' (the roles chosen, the model's limits or the " +
                      "request size leave none), and an avatar is shown from its pictures. Nothing was submitted."
                    : $"None of the pictures of one of the avatars can be sent to '{config.Model}' (the roles chosen, the model's " +
                      "limits or the request size leave none), and an avatar is shown from its pictures. Show fewer avatars or " +
                      "choose a model that takes more reference pictures. Nothing was submitted."));
            }

            foreach (int i in selections[s].Chosen)
            {
                chosen.Add(candidates[s][i]);
                chosenSubject.Add(s);
            }
        }

        // (6) The chosen bytes, each re-hashed: a file changed since the consent was given is not the person who agreed.
        var (read, readRefusal) = await ReadReferenceBytesAsync(context, chosen, Refuse);
        if (readRefusal is not null)
        {
            return (null, readRefusal);
        }

        var references = new List<VideoGenerationReference>(read!.Count);
        var sent = new List<ResolvedAvatarReference>(read.Count);
        foreach ((ResolvedAvatarReference r, byte[] bytes) in read)
        {
            references.Add(new VideoGenerationReference(r.Kind, r.Role, r.MimeType.Trim().ToLowerInvariant(), bytes, r.DurationMs));
            sent.Add(r);
        }

        // (7) The vendor's input-material charge for one take, priced from the model's own declaration.
        List<VideoReferenceMaterial> materials = [.. references.Select(r => new VideoReferenceMaterial(r.Kind, r.DurationMs))];
        decimal inputMaterialUsd;
        try
        {
            inputMaterialUsd = client.DeclaredInputMaterialCostUsd(config.Model, config.Resolution, materials);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Avatar input-material pricing refused (Model={Model})", config.Model);
            return (null, Refuse("CONFIG_INVALID",
                "The model chosen for this step cannot price the avatar's reference material. Choose another model in the app. Nothing was submitted."));
        }

        int inputVideoSeconds = references
            .Where(r => r.Kind == AvatarReferenceKind.Video)
            .Sum(r => (int)Math.Ceiling((r.DurationMs ?? 0) / 1000.0));
        string sentIds = string.Join(",", sent.Select(r => r.Id.ToString("N")));
        decimal inputMaterialCredits = IsGpuBudgeted(provider) ? 0m : CreditCurrency.CreditsFromUsd(inputMaterialUsd);

        JsonObject SentCounts(int? subject) => new()
        {
            ["images"] = Enumerable.Range(0, sent.Count).Count(i => (subject is null || chosenSubject[i] == subject) && sent[i].Kind == AvatarReferenceKind.Image),
            ["videos"] = Enumerable.Range(0, sent.Count).Count(i => (subject is null || chosenSubject[i] == subject) && sent[i].Kind == AvatarReferenceKind.Video),
            ["audio"] = Enumerable.Range(0, sent.Count).Count(i => (subject is null || chosenSubject[i] == subject) && sent[i].Kind == AvatarReferenceKind.Audio),
        };

        if (!namesSubjects)
        {
            // What the step output and the manifest record (never the subject's name, a storage key or a URL). The
            // avatar's display name is the user's own label. c2pa is settled per delivered clip (see
            // IGeneratedClipProvenanceSigner).
            meta[AvatarOutputFacts.MetaKey] = new JsonObject
            {
                ["avatarId"] = primary.Id.ToString("D"),
                ["name"] = primary.Name,
                ["fingerprint"] = identity,
                ["aiGenerated"] = true,
                ["disclosure"] = primary.Disclosure.ToString(),
                ["marking"] = primary.Disclosure == AvatarDisclosure.Visible ? "metadata+visibleLabel" : "metadata",
                ["c2pa"] = "not_available",
                ["referencesSent"] = SentCounts(null),
                ["referencesSkipped"] = skipped[0],
                ["inputVideoSeconds"] = inputVideoSeconds,
                ["inputMaterialCostPerTake"] = inputMaterialCredits,
            };
            // Written for an object only, so a person's recorded facts stay exactly what they were.
            if (primary.SubjectKind == AvatarSubjectKind.Object && meta[AvatarOutputFacts.MetaKey] is JsonObject objectMeta)
            {
                objectMeta["kind"] = "object";
            }

            return (new AvatarInputs(primary.Id, identity, identity + ":" + sentIds, references, inputMaterialUsd, inputVideoSeconds, primary.Disclosure)
            {
                SubjectKind = primary.SubjectKind,
                Shown = [.. resolved.Select(ShownSubject.Of)],
                Resolved = resolved,
            }, null);
        }

        meta[SubjectOutputFacts.MetaKey] = new JsonArray(
            [.. resolved.Select((r, s) => (JsonNode)SubjectEntry(r, fingerprints[s], SentCounts(s), skipped[s]))]);
        meta[SubjectOutputFacts.PathKey] = SubjectOutputFacts.PathWord(path);
        meta[SubjectFactsKey] = SubjectFacts(identity, primary, showsPerson, inputVideoSeconds, inputMaterialCredits);

        return (new AvatarInputs(
            primary.Id, identity, "subjects:" + identity + ":" + sentIds, references, inputMaterialUsd, inputVideoSeconds, primary.Disclosure)
        {
            SubjectKind = primary.SubjectKind,
            Path = path,
            Checks = [.. resolved.Select((r, s) => (r.Id, fingerprints[s]))],
            SubjectIds = [.. resolved.Select(r => r.Id)],
            SubjectsOnClips = true,
            Shown = [.. resolved.Select(ShownSubject.Of)],
            Resolved = resolved,
        }, null);
    }

    /// <summary>The rule a second person breaks, as the step says it.</summary>
    private const string OnePersonRule =
        "A clip shows at most one person: this step names more than one avatar of a person. Keep one of them (objects are " +
        "not limited to one). Nothing was submitted.";

    /// <summary>The <c>meta.avatar.path</c> / <c>meta.subjectPath</c> word of a clip that starts from one composed still.</summary>
    private const string FirstFramePathWord = "firstFrame";

    /// <summary>A refusal sentence that also says what did not happen (unless it already does).</summary>
    private static string NothingSubmitted(string message) =>
        message.EndsWith("Nothing was submitted.", StringComparison.Ordinal) ? message : message + " Nothing was submitted.";

    /// <summary>One entry of <c>meta.subjects[]</c>: who was shown (the owner's label, never a key or a URL) and what of them was sent.</summary>
    private static JsonObject SubjectEntry(ResolvedAvatar subject, string fingerprint, JsonNode? sent, JsonNode? skippedReferences) => new()
    {
        ["avatarId"] = subject.Id.ToString("D"),
        ["name"] = subject.Name,
        ["kind"] = SubjectOutputFacts.KindWord(subject.SubjectKind),
        ["fingerprint"] = fingerprint,
        ["referencesSent"] = sent,
        ["referencesSkipped"] = skippedReferences,
    };

    /// <summary>
    /// <c>meta.subjectFacts</c>: the step-level facts of a clip that shows a set of subjects. With a person among them
    /// the clip is marked as that person's avatar asks; with objects only it gets the neutral machine-readable marking
    /// and no label.
    /// </summary>
    private static JsonObject SubjectFacts(
        string identity, ResolvedAvatar primary, bool showsPerson, int inputVideoSeconds, decimal inputMaterialCredits) => new()
        {
            ["fingerprint"] = identity,
            ["aiGenerated"] = true,
            ["showsPerson"] = showsPerson,
            ["disclosure"] = (showsPerson ? primary.Disclosure : AvatarDisclosure.MetadataOnly).ToString(),
            ["marking"] = showsPerson && primary.Disclosure == AvatarDisclosure.Visible ? "metadata+visibleLabel" : "metadata",
            ["c2pa"] = "not_available",
            ["inputVideoSeconds"] = inputVideoSeconds,
            ["inputMaterialCostPerTake"] = inputMaterialCredits,
        };

    /// <summary>
    /// The FirstFrame path: the pictures of every subject the scene still is made from (picture kinds only, the roles
    /// chosen, shared out in turns up to <see cref="SubjectSceneStill.MaxPicturesPerSubject"/> each and
    /// <see cref="SubjectSceneStill.MaxPictures"/> in all), each read and re-hashed against its attested digest. The
    /// still itself is made later, once the prompt has been screened (<see cref="MakeSubjectStillAsync"/>). Nothing
    /// here costs anything, and no reference item is sent to the video model.
    /// </summary>
    private async Task<(AvatarInputs? Inputs, StepExecutionResult? Refusal)> PrepareFirstFramePathAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        IReadOnlyList<VideoGenerateSubject> subjects,
        IReadOnlyList<ResolvedAvatar> resolved,
        IReadOnlyList<string> fingerprints,
        string identity,
        ResolvedAvatar primary,
        IReadOnlyList<ProjectWorkspaceFile> files,
        bool namesSubjects,
        JsonObject meta,
        Func<string, string, StepExecutionResult> refuse)
    {
        if (_avatarStills is not ISubjectStillMaker)
        {
            return (null, refuse(AvatarFailureCodes.GenerationDisabled,
                $"'{config.Model}' is shown an avatar through one composed first frame, and this engine cannot make one. Nothing was submitted."));
        }

        bool one = resolved.Count == 1;
        var pictures = new List<List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>>(resolved.Count);
        var skipped = new List<JsonArray>(resolved.Count);
        for (int s = 0; s < resolved.Count; s++)
        {
            HashSet<AvatarReferenceRole>? roles = subjects[s].Roles is { } wanted ? [.. wanted] : null;
            var own = new List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>();
            var ownSkipped = new JsonArray();
            foreach (ResolvedAvatarReference r in resolved[s].References.OrderBy(r => r.Ordinal).ThenBy(r => r.Id))
            {
                if (r.Kind != AvatarReferenceKind.Image)
                {
                    ownSkipped.Add(SkippedEntry(r, StillPathSkipNotAPicture));
                    continue;
                }

                if (roles is not null && !roles.Contains(r.Role))
                {
                    ownSkipped.Add(SkippedEntry(r, VideoReferenceInput.SkipRoleNotRequested));
                    continue;
                }

                ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == r.ProjectFileId);
                if (file is null)
                {
                    return (null, refuse(AvatarFailureCodes.ConsentMissing,
                        $"The avatar this step shows cannot be used ({ProjectAvatar.ReasonReferenceMissing}). Nothing was submitted."));
                }

                own.Add((r, file));
            }

            if (own.Count == 0)
            {
                return (null, refuse(VideoReferenceInput.NotSupportedCode, one
                    ? $"None of the avatar's pictures can be used for the first frame '{config.Model}' starts from (the roles " +
                      "chosen leave none). Nothing was submitted."
                    : $"None of the pictures of one of the avatars can be used for the first frame '{config.Model}' starts from " +
                      "(the roles chosen leave none). Nothing was submitted."));
            }

            pictures.Add(own);
            skipped.Add(ownSkipped);
        }

        IReadOnlyList<int> share = SubjectSceneStill.SharePictures([.. pictures.Select(own => own.Count)]);
        var chosen = new List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>();
        for (int s = 0; s < pictures.Count; s++)
        {
            chosen.AddRange(pictures[s].Take(share[s]));
            foreach ((ResolvedAvatarReference r, _) in pictures[s].Skip(share[s]))
            {
                skipped[s].Add(SkippedEntry(r, VideoReferenceInput.SkipKindLimit));
            }
        }

        var (read, readRefusal) = await ReadReferenceBytesAsync(context, chosen, refuse);
        if (readRefusal is not null)
        {
            return (null, readRefusal);
        }

        var sceneSubjects = new List<SubjectStillSubject>(resolved.Count);
        int offset = 0;
        for (int s = 0; s < resolved.Count; s++)
        {
            List<AvatarStillPicture> own = [.. read!.Skip(offset).Take(share[s]).Select(x =>
                new AvatarStillPicture(x.Ref.Id, x.Ref.MimeType.Trim().ToLowerInvariant(), x.Bytes))];
            offset += share[s];
            sceneSubjects.Add(new SubjectStillSubject(resolved[s].Id, resolved[s].SubjectKind, fingerprints[s], resolved[s].Description, own, resolved[s].IdentityFacts));
        }

        bool showsPerson = primary.SubjectKind == AvatarSubjectKind.Person;
        string pictureIds = string.Join(",", read!.Select(x => x.Ref.Id.ToString("N")));
        JsonObject Sent(int s) => new() { ["images"] = share[s], ["videos"] = 0, ["audio"] = 0 };

        if (!namesSubjects)
        {
            // The one legacy avatar on a model with no reference mode: meta.avatar, as a talking head records it, with
            // the path word that says how it was shown. What reached a vendor: the pictures go to the still's image
            // model; the video model receives the still alone.
            var legacy = new JsonObject
            {
                ["avatarId"] = primary.Id.ToString("D"),
                ["name"] = primary.Name,
                ["fingerprint"] = identity,
                ["aiGenerated"] = true,
                ["disclosure"] = primary.Disclosure.ToString(),
                ["marking"] = primary.Disclosure == AvatarDisclosure.Visible ? "metadata+visibleLabel" : "metadata",
                ["c2pa"] = "not_available",
                ["path"] = FirstFramePathWord,
                ["referencesSent"] = Sent(0),
                ["referencesSkipped"] = skipped[0],
                ["inputVideoSeconds"] = 0,
                ["inputMaterialCostPerTake"] = 0m,
            };
            if (primary.SubjectKind == AvatarSubjectKind.Object)
            {
                legacy["kind"] = "object";
            }

            meta[AvatarOutputFacts.MetaKey] = legacy;
            return (new AvatarInputs(primary.Id, identity, identity + ":scene:" + pictureIds, [], 0m, 0, primary.Disclosure)
            {
                SubjectKind = primary.SubjectKind,
                Path = VideoSubjectPath.FirstFrame,
                SceneSubjects = sceneSubjects,
                Shown = [.. resolved.Select(ShownSubject.Of)],
                Resolved = resolved,
            }, null);
        }

        meta[SubjectOutputFacts.MetaKey] = new JsonArray(
            [.. resolved.Select((r, s) => (JsonNode)SubjectEntry(r, fingerprints[s], Sent(s), skipped[s]))]);
        meta[SubjectOutputFacts.PathKey] = SubjectOutputFacts.PathWord(VideoSubjectPath.FirstFrame);
        meta[SubjectFactsKey] = SubjectFacts(identity, primary, showsPerson, 0, 0m);

        return (new AvatarInputs(primary.Id, identity, "subjects:" + identity + ":scene:" + pictureIds, [], 0m, 0, primary.Disclosure)
        {
            SubjectKind = primary.SubjectKind,
            Path = VideoSubjectPath.FirstFrame,
            Checks = [.. resolved.Select((r, s) => (r.Id, fingerprints[s]))],
            SubjectIds = [.. resolved.Select(r => r.Id)],
            SubjectsOnClips = true,
            SceneSubjects = sceneSubjects,
            Shown = [.. resolved.Select(ShownSubject.Of)],
            Resolved = resolved,
        }, null);
    }

    /// <summary>
    /// Reads the chosen references' bytes, each re-hashed against the attested digest (a file changed since the consent
    /// was given is not the person who agreed). A storage failure is <c>AVATAR_REFERENCE_UNAVAILABLE</c> (ours, refunded).
    /// </summary>
    private async Task<(List<(ResolvedAvatarReference Ref, byte[] Bytes)>? Read, StepExecutionResult? Refusal)> ReadReferenceBytesAsync(
        StepExecutionContext context,
        IReadOnlyList<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)> chosen,
        Func<string, string, StepExecutionResult> refuse)
    {
        var read = new List<(ResolvedAvatarReference, byte[])>(chosen.Count);
        string scratch = Path.Combine(Path.GetTempPath(), "reelbolt-avatar-refs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            for (int index = 0; index < chosen.Count; index++)
            {
                ResolvedAvatarReference r = chosen[index].Ref;
                string local = Path.Combine(scratch, $"{index}.bin");
                await _workspace.DownloadStorageKeyToFileAsync(
                    context.Execution.ProjectId, r.StorageKey, local, MaxReferenceBytes(r.Kind), context.CancellationToken);
                byte[] bytes = await File.ReadAllBytesAsync(local, context.CancellationToken);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), r.ContentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, refuse(AvatarFailureCodes.ConsentMissing,
                        $"The avatar this step shows cannot be used ({ProjectAvatar.ReasonReferenceMissing}): a reference file " +
                        "changed after the consent was given. Re-attest the avatar in the app. Nothing was submitted."));
                }

                read.Add((r, bytes));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // S-67: a storage failure's text names keys and hosts; the step answers one fixed sentence.
            _logger.LogWarning(ex, "Reading an avatar reference for step {StepOrder} failed; no clip was submitted.", context.Step.StepOrder);
            return (null, refuse(AvatarFailureCodes.ReferenceUnavailable,
                "A reference file of the avatar could not be read from storage. Nothing was submitted; try the run again."));
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the avatar-reference scratch directory {Scratch}", scratch);
            }
        }

        return (read, null);
    }

    /// <summary>
    /// Phase 3, the still-then-animate path (docs/avatars.md "The still-then-animate path"): the avatar's pictures the
    /// still is made from (picture roles only, in the avatar's order, at most <see cref="AvatarStillStorage.MaxPictures"/>,
    /// digest-checked) and the speech that drives the mouth, its length checked against the model and against the step's
    /// <c>DurationSeconds</c> (the most a take is priced at). The still itself is made later, once the prompt has been
    /// screened. Nothing here costs anything.
    /// </summary>
    private async Task<(AvatarInputs? Inputs, StepExecutionResult? Refusal)> PrepareStillPathAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        VideoReferenceInputSupport support,
        ResolvedAvatar resolved,
        IReadOnlyList<AvatarReferenceRole>? wantedRoles,
        JsonObject meta,
        Func<string, string, StepExecutionResult> refuse)
    {
        if (_avatarSpeech is null || _avatarStills is null)
        {
            return (null, refuse(AvatarFailureCodes.GenerationDisabled,
                $"'{config.Model}' animates a still of the avatar, and this engine cannot make one. Nothing was submitted."));
        }

        HashSet<AvatarReferenceRole>? roles = wantedRoles is { } wanted ? [.. wanted] : null;
        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.Execution.ProjectId, context.CancellationToken);
        var chosen = new List<(ResolvedAvatarReference Ref, ProjectWorkspaceFile File)>();
        var skipped = new JsonArray();
        foreach (ResolvedAvatarReference r in resolved.References.OrderBy(r => r.Ordinal).ThenBy(r => r.Id))
        {
            if (r.Kind != AvatarReferenceKind.Image)
            {
                skipped.Add(SkippedEntry(r, StillPathSkipNotAPicture));
                continue;
            }

            if (roles is not null && !roles.Contains(r.Role))
            {
                skipped.Add(SkippedEntry(r, VideoReferenceInput.SkipRoleNotRequested));
                continue;
            }

            if (chosen.Count >= AvatarStillStorage.MaxPictures)
            {
                skipped.Add(SkippedEntry(r, VideoReferenceInput.SkipKindLimit));
                continue;
            }

            ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == r.ProjectFileId);
            if (file is null)
            {
                return (null, refuse(AvatarFailureCodes.ConsentMissing,
                    $"The avatar this step shows cannot be used ({ProjectAvatar.ReasonReferenceMissing}). Nothing was submitted."));
            }

            chosen.Add((r, file));
        }

        if (chosen.Count == 0)
        {
            return (null, refuse(VideoReferenceInput.NotSupportedCode,
                $"None of the avatar's pictures can be used for the still '{config.Model}' animates (the roles chosen leave " +
                "none). Nothing was submitted."));
        }

        var (read, readRefusal) = await ReadReferenceBytesAsync(context, chosen, refuse);
        if (readRefusal is not null)
        {
            return (null, readRefusal);
        }

        // The speech: read, joined and measured before anything is reserved or sent.
        AvatarSpeechTrackOutcome speechOutcome = await _avatarSpeech.ReadAsync(context, config, context.CancellationToken);
        if (speechOutcome.Track is not { } speech)
        {
            return (null, refuse(speechOutcome.Code ?? AvatarStillCodes.AudioUnavailable, speechOutcome.Message ?? "The avatar has nothing to say."));
        }

        double seconds = speech.DurationMs / 1000.0;
        string said = seconds.ToString("0.#", CultureInfo.InvariantCulture);
        if (support.MinAudioMs > 0 && speech.DurationMs < support.MinAudioMs)
        {
            return (null, refuse(AvatarStillCodes.AudioLength,
                $"The avatar's speech lasts {said} s; '{config.Model}' needs at least {support.MinAudioMs / 1000} s. Write a longer " +
                "narration. Nothing was submitted."));
        }

        if (speech.DurationMs > support.MaxAudioTotalMs || speech.DurationMs > config.DurationSeconds * 1000L)
        {
            int most = Math.Min(support.MaxAudioTotalMs / 1000, config.DurationSeconds);
            return (null, refuse(AvatarStillCodes.AudioLength,
                $"The avatar's speech lasts {said} s; this step makes clips of at most {most} s (the model's limit and the " +
                "step's duration, which is what each take is priced at). Shorten the narration. Nothing was submitted."));
        }

        List<AvatarStillPicture> pictures = [.. read!.Select(x =>
            new AvatarStillPicture(x.Ref.Id, x.Ref.MimeType.Trim().ToLowerInvariant(), x.Bytes))];
        string fingerprint = AvatarFingerprint.Compute(resolved);
        string requestKey = fingerprint + ":" + string.Join(",", read.Select(x => x.Ref.Id.ToString("N"))) + ":speech:" + speech.Sha256;

        meta[AvatarOutputFacts.MetaKey] = new JsonObject
        {
            ["avatarId"] = resolved.Id.ToString("D"),
            ["name"] = resolved.Name,
            ["fingerprint"] = fingerprint,
            ["aiGenerated"] = true,
            ["disclosure"] = resolved.Disclosure.ToString(),
            ["marking"] = resolved.Disclosure == AvatarDisclosure.Visible ? "metadata+visibleLabel" : "metadata",
            ["c2pa"] = "not_available",
            ["path"] = StillThenAnimatePath,
            ["referencesSent"] = new JsonObject
            {
                // What reached a vendor: the pictures go to the still's image model, the speech to the video model.
                ["images"] = pictures.Count,
                ["videos"] = 0,
                ["audio"] = 1,
            },
            ["referencesSkipped"] = skipped,
            ["speechSeconds"] = Math.Round(seconds, 3),
            ["speechLines"] = speech.Lines,
            ["inputVideoSeconds"] = 0,
            ["inputMaterialCostPerTake"] = 0m,
        };

        VideoGenerationReference speechReference = new(
            AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, speech.ContentType, speech.Bytes, speech.DurationMs);
        return (new AvatarInputs(resolved.Id, fingerprint, requestKey, [speechReference], 0m, 0, resolved.Disclosure)
        {
            Path = VideoSubjectPath.TalkingHead,
            AnimatesStill = true,
            TakesPrompt = support.TakesPrompt,
            StillPictures = pictures,
            Speech = speech,
        }, null);
    }

    /// <summary>The <c>meta.avatar.path</c> word of a clip made by animating a still.</summary>
    private const string StillThenAnimatePath = "stillThenAnimate";

    /// <summary>Reason word: the still is made from pictures only, so a video or audio reference is not sent.</summary>
    private const string StillPathSkipNotAPicture = "not_a_picture";

    /// <summary>
    /// Makes (or finds) the still after the prompt was screened, then re-resolves the avatar: a revoke or a change while
    /// the still was being made removes a still made now and fails the step with the avatar's code. Records what was
    /// used in <c>meta.avatar.still</c> (never a key or a URL).
    /// </summary>
    private async Task<(AvatarInputs? Inputs, StepExecutionResult? Refusal)> MakeAvatarStillAsync(
        StepExecutionContext context, VideoGenerateStepConfig config, AvatarInputs avatar, string prompt, JsonObject meta)
    {
        StepExecutionResult Refuse(string code, string message) =>
            CreateFailedResult(context.Execution.Id, context.Step.StepOrder, code, message, context.CurrentStepIndex, context.IterationCount, userFacing: true);

        AvatarStillOutcome outcome = await _avatarStills!.MakeAsync(
            new AvatarStillRequest(context.Execution.ProjectId, avatar.AvatarId, avatar.Fingerprint, avatar.StillPictures ?? [], prompt, config.Ratio, config.StillProviderId),
            context.CancellationToken);
        if (outcome.Still is not { } still)
        {
            return (null, Refuse(outcome.Code ?? AvatarStillCodes.StillFailed, outcome.Message ?? "The still of the avatar could not be made."));
        }

        if (await RecheckAvatarAsync(context.Execution.ProjectId, avatar, context.CancellationToken) is { } stopped)
        {
            if (!still.Reused)
            {
                try
                {
                    await _workspace.DeleteArtifactAsync(context.Execution.ProjectId, still.StorageKey, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // The storage sweep's orphan pass removes a still whose avatar is gone; a changed avatar's old still
                    // goes with the avatar.
                    _logger.LogWarning(ex, "Could not remove the still of avatar {AvatarId} after its consent changed", avatar.AvatarId);
                }
            }

            return (null, Refuse(stopped.Code ?? AvatarFailureCodes.ConsentMissing,
                "The avatar's consent or references changed while its still was being made; no video was submitted."));
        }

        if (meta[AvatarOutputFacts.MetaKey] is JsonObject avatarMeta)
        {
            avatarMeta["still"] = new JsonObject
            {
                ["made"] = !still.Reused,
                ["model"] = still.Model,
                ["sha256"] = still.Sha256,
            };
        }

        return (avatar with { Still = still, RequestKey = avatar.RequestKey + ":still:" + still.Sha256 }, null);
    }

    /// <summary>
    /// The FirstFrame path, after the prompt was screened: makes (or finds) the ONE composed still that shows every
    /// subject in the scene of <paramref name="prompt"/>, then re-resolves every subject: a revoke or a change while
    /// the still was being made removes a still made now and fails the step with the avatar's code. Records what was
    /// used under the step's facts (never a key or a URL). Reusable per clip, like <see cref="PrepareSubjectsAsync"/>.
    /// </summary>
    private async Task<(AvatarInputs? Inputs, StepExecutionResult? Refusal)> MakeSubjectStillAsync(
        StepExecutionContext context, VideoGenerateStepConfig config, AvatarInputs subjects, string prompt, string ratio, JsonObject meta)
    {
        StepExecutionResult Refuse(string code, string message) =>
            CreateFailedResult(context.Execution.Id, context.Step.StepOrder, code, message, context.CurrentStepIndex, context.IterationCount);

        if (_avatarStills is not ISubjectStillMaker stills || subjects.SceneSubjects is not { Count: > 0 } sceneSubjects)
        {
            return (null, Refuse(AvatarFailureCodes.GenerationDisabled,
                $"'{config.Model}' is shown an avatar through one composed first frame, and this engine cannot make one. Nothing was submitted."));
        }

        AvatarStillOutcome outcome = await stills.MakeSceneAsync(
            new SubjectSceneStillRequest(context.Execution.ProjectId, sceneSubjects, prompt, ratio, config.StillProviderId),
            context.CancellationToken);
        if (outcome.Still is not { } still)
        {
            return (null, Refuse(outcome.Code ?? AvatarStillCodes.StillFailed, outcome.Message ?? "The first frame that shows the avatar could not be made."));
        }

        if (await RecheckAvatarAsync(context.Execution.ProjectId, subjects, context.CancellationToken) is { } stopped)
        {
            if (!still.Reused)
            {
                try
                {
                    await _workspace.DeleteArtifactAsync(context.Execution.ProjectId, still.StorageKey, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // The storage sweep's orphan pass removes a still whose avatar is gone; a changed avatar's old still
                    // goes with the avatar.
                    _logger.LogWarning(ex, "Could not remove the scene still of avatar {AvatarId} after a subject changed", subjects.AvatarId);
                }
            }

            return (null, Refuse(stopped.Code ?? AvatarFailureCodes.ConsentMissing,
                "An avatar's consent or references changed while the first frame was being made; no video was submitted."));
        }

        if (meta[subjects.FactsKey] is JsonObject facts)
        {
            facts["still"] = new JsonObject
            {
                ["made"] = !still.Reused,
                ["model"] = still.Model,
                ["sha256"] = still.Sha256,
            };
        }

        return (subjects with { Still = still, RequestKey = subjects.RequestKey + ":still:" + still.Sha256 }, null);
    }

    private static JsonObject SkippedEntry(ResolvedAvatarReference r, string reason) => new()
    {
        ["kind"] = r.Kind.ToString(),
        ["role"] = r.Role.ToString(),
        ["reason"] = reason,
    };

    /// <summary>
    /// The per-take consent re-check (design: a running step re-resolves EVERY subject immediately before each take's
    /// submit, as it re-checks the lease). Null when each is still usable and unchanged; otherwise the take's failure,
    /// with the avatar's own code. A read failure refuses too (fail closed): nothing is sent unchecked.
    /// </summary>
    private async Task<TakeOutcome?> RecheckAvatarAsync(Guid projectId, AvatarInputs avatar, CancellationToken ct)
    {
        if (_avatars is null)
        {
            return TakeOutcome.Failed(AvatarFailureCodes.GenerationDisabled, "The avatar could not be re-checked; this take was not submitted.");
        }

        // The one legacy avatar is its own single check; a set is checked subject by subject, each against its own
        // fingerprint (the clip's identity is the fingerprint of the set, which no single avatar produces).
        IReadOnlyList<(Guid AvatarId, string Fingerprint)> checks =
            avatar.Checks.Count > 0 ? avatar.Checks : [(avatar.AvatarId, avatar.Fingerprint)];
        foreach ((Guid avatarId, string fingerprint) in checks)
        {
            try
            {
                ResolvedAvatar now = await _avatars.ResolveAsync(projectId, avatarId, ct);
                if (!now.Usable)
                {
                    return TakeOutcome.Failed(AvatarFailureCodes.For(now.Reason),
                        $"The avatar stopped being usable during the run ({now.Reason}); this take was not submitted and its reservation was released.");
                }

                if (!string.Equals(AvatarFingerprint.Compute(now), fingerprint, StringComparison.Ordinal))
                {
                    return TakeOutcome.Failed(AvatarFailureCodes.ConsentMissing,
                        "The avatar's consent or references changed during the run; this take was not submitted and its reservation was released.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-checking avatar {AvatarId} before a take failed; the take is not submitted.", avatarId);
                return TakeOutcome.Failed(AvatarFailureCodes.ReferenceUnavailable,
                    "The avatar could not be re-checked before this take; it was not submitted and its reservation was released.");
            }
        }

        return null;
    }

    /// <summary>Whether the run was started by a platform admin (the smoke-run switch). Unknown or no initiator = no.</summary>
    private async Task<bool> InitiatedByPlatformAdminAsync(WorkflowExecution execution, CancellationToken ct)
    {
        if (execution.InitiatedByUserId is not Guid userId)
        {
            return false;
        }

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            return await db.ApplicationUsers.AsNoTracking().Where(u => u.Id == userId).Select(u => u.IsAdmin).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read whether the initiator of execution {ExecutionId} is a platform admin.", execution.Id);
            return false;
        }
    }

    /// <summary>
    /// The credit-side record of an avatar take's INPUT MATERIAL (extra pictures, input video), next to the output
    /// seconds <see cref="RecordVideoGenUsageAsync"/> records: priced from the model's declared USD figure at the
    /// platform markup (the oracle path), keyed <c>videogen-input:{job}</c> so a replayed settle charges once. The
    /// quantity is the input-video seconds the vendor meters (at least 1, since a row needs a positive quantity).
    /// </summary>
    private async Task RecordInputMaterialUsageAsync(
        ResolvedVideoGenerationProvider? provider, VideoGenerationRequest request, ExternalGenerationJob job, AvatarInputs avatar,
        InferenceProviderCapability capability)
    {
        if (_usageRecorder is null || provider is null || avatar.InputMaterialUsd <= 0m) return;

        try
        {
            await _usageRecorder.RecordAsync(new UsageRecordRequest(
                UsageKind.VideoGenSeconds, Math.Max(1, avatar.InputVideoSeconds), provider.ProviderId, provider.Kind,
                string.IsNullOrWhiteSpace(request.Model) ? provider.ModelName : request.Model,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                IdempotencyKey: $"videogen-input:{job.Id:N}",
                Capability: capability,
                Variant: "input-material",
                AccountId: provider.AccountId,
                QuotedCostUsd: avatar.InputMaterialUsd), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metering the input material of video generation job {JobId} failed", job.Id);
        }
    }

    // ---------------------------------------------------------------------
    // Plan-sourced path
    // ---------------------------------------------------------------------

    /// <summary>
    /// Generates one clip per planned shot. The order below is the whole point of this method and
    /// is not interchangeable:
    /// plan resolution → EGRESS PRE-SCAN → PROVIDER IMAGE-CAPABILITY PRE-SCAN → keyframe resolution →
    /// the <c>MaxClips</c> trim (which DEFINES the effective plan, and which is why only the clips that
    /// survive it have a frame cut for them) → whole-plan and effective-plan pricing → the step-cap
    /// pre-check → the single budget reservation → dispatch. Every refusal that must cost nothing
    /// happens before the reservation, because a reservation is a ledger row and a ledger row is
    /// money.
    /// </summary>
    private async Task<StepExecutionResult> ExecutePlanAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        IVideoGenerationClient client,
        VideoGenerationPricingBasis pricing,
        System.Diagnostics.Stopwatch sw,
        ChangeRun? changeRun = null)
    {
        var execution = context.Execution;
        var step = context.Step;

        // Frame transport, one pair of objects for this attempt. The capture remembers which object
        // each source was staged as (so two frames of one shot cost one staging); the session opens
        // LAZILY, because a plan that names no frame — or whose frames were all trimmed away — must
        // not pay for a media session, and it is disposed on every exit from here, including the
        // refusals below.
        var keyframeCapture = new ShotKeyframeCapture(_keyframeExtractor, _logger);
        await using var keyframeMedia = new KeyframeMediaSession(_mediaWorkspaces, context, _logger);

        // ((5)) Resolve the planner step's raw output — or the approved shot list saved as a
        // project file, which is the same JSON and takes the identical path from here on.
        string? planJson;
        StepOutputHistoryEntry? planEntry = null;
        string? planError = null;
        if (config.PlanSource == VideoGeneratePlanSource.ProjectFile)
        {
            try
            {
                planJson = await _workspace.ReadFileAsync(execution.ProjectId, config.PlanProjectFileId!.Value.ToString(), context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // S-39: tenants read this output; the storage exception (hosts, buckets) stays in the log.
                _logger.LogWarning(ex, "Plan project file {PlanProjectFileId} could not be read for execution {ExecutionId}",
                    config.PlanProjectFileId, execution.Id);
                planJson = null;
                planError = $"The plan project file could not be read (file {config.PlanProjectFileId}); the details are in the server log.";
            }
        }
        else
        {
            (planJson, planEntry, planError) = ResolveStepJson(context, config.Plan!, "Plan");
        }
        if (planJson is null)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", planError ?? "Plan input could not be resolved.", context.CurrentStepIndex, context.IterationCount);
        }

        GeneratedShotPlanOutput? plan;
        try
        {
            plan = JsonSerializer.Deserialize<GeneratedShotPlanOutput>(planJson, ArtifactJsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", $"Plan output is not a valid GeneratedShotPlanOutput: {ex.Message}", context.CurrentStepIndex, context.IterationCount);
        }

        if (plan is null)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", "Plan output deserialized to null.", context.CurrentStepIndex, context.IterationCount);
        }

        var shots = plan.Shots ?? new List<ShotPlan>();

        // ((5b)) Avatars and objects in planned shots (docs/design/avatar-objects.md): the offer ids each shot names,
        // resolved against the project's usable subjects before anything is read or bought. Null when no shot names
        // one, and then everything below is exactly what it was.
        var (shotSubjects, subjectsRefusal) = await ResolvePlannedSubjectsAsync(context, config, shots);
        if (subjectsRefusal is not null)
        {
            return subjectsRefusal;
        }

        // A shot that shows subjects never also takes a planned first or last frame (the subjects win): its frames are
        // left out of every keyframe rule below (the egress and provider pre-scans, the analysis lookup, the cut), and
        // each of its clips reports the frame as not applied, for first_frame_dropped_for_subjects.
        IReadOnlyList<ShotPlan?> frameShots = shots;
        if (shotSubjects is not null)
        {
            frameShots = [.. shots.Select(ShotPlan? (s, i) => s is not null && shotSubjects[i] is { FramesDropped: true } ? WithoutFrames(s) : s)];
        }

        // ((6)) EGRESS PRE-SCAN over EVERY shot, before anything is bought. A partially generated
        // plan is a half-edit the operator cannot use, so one refused shot refuses the whole step —
        // and it does so here, where nothing has been submitted and no ledger row exists.
        for (int i = 0; i < shots.Count; i++)
        {
            ShotPlan? candidate = frameShots[i];
            var decision = GeneratedShotEgressPolicy.Evaluate(candidate?.FirstFrame, candidate?.LastFrame, config.AllowSourceMediaEgress);
            if (!decision.Allowed)
            {
                return CreateFailedResult(
                    execution.Id, step.StepOrder,
                    decision.Code ?? GeneratedShotEgressPolicy.EgressRefusedCode,
                    decision.Reason ?? "Source-media egress was refused for this plan.",
                    context.CurrentStepIndex, context.IterationCount);
            }
        }

        // ((7)) Keyframes are touched ONLY when a shot actually names one.
        var keyframeShots = new List<(int ShotIndex, ShotPlan Shot)>();
        for (int i = 0; i < shots.Count; i++)
        {
            var shot = frameShots[i];
            if (shot is not null && (!string.IsNullOrWhiteSpace(shot.FirstFrame) || !string.IsNullOrWhiteSpace(shot.LastFrame)))
            {
                keyframeShots.Add((i + 1, shot));
            }
        }

        // ((6b)) PROVIDER IMAGE-CAPABILITY PRE-SCAN — the second all-or-nothing rule, and the one
        // place where the resolved provider, the resolved CLIENT, the resolved model and the plan's
        // frame requests are all in hand at once. It is upstream of the pricing, the step-cap check
        // and the single budget reservation — so a refusal here costs nothing.
        //
        // Two checks, both all-or-nothing. The KIND check is the coarse one: a provider kind with no
        // image input at all cannot honour any frame, so the plan is refused before a frame is cut.
        // The CLIENT check is the fine one: the resolved client declares, per model, whether it reads
        // a first frame, a first and a last frame, or no image at all, and a model that cannot carry
        // what a shot's request will carry refuses here — before ReserveAsync, so no ledger row exists.
        if (keyframeShots.Count > 0)
        {
            int plannedFrames = keyframeShots.Sum(k => RequestedFrameCount(k.Shot));

            if (!VideoGenerationImageSupport.CanAcceptInputImages(provider.Kind))
            {
                var offenders = string.Join(", ", keyframeShots.Select(k =>
                    $"shot {k.ShotIndex} ({DescribeRequestedFrames(k.Shot)})"));
                return CreateFailedResult(
                    execution.Id, step.StepOrder, VideoGenerationImageSupport.UnsupportedCode,
                    $"The plan requests {plannedFrames} source-media keyframe(s) ({offenders}), and the resolved "
                    + $"provider kind '{provider.Kind}' cannot accept an input image for video generation. "
                    + "No clip was submitted.",
                    context.CurrentStepIndex, context.IterationCount);
            }

            // The per-request count is at most 2 (first and last frame), so the single model question
            // is the MAXIMUM any one shot's request will carry: a plan whose shots each ask for one
            // frame passes a first-frame-only model, and one shot asking for two needs a model that
            // documents both. EnsureAccepts throws rather than returning a verdict, so a refusal takes
            // the same catch-to-CreateFailedResult shape and the same stable code as the kind check.
            try
            {
                int maxImagesPerClip = keyframeShots.Max(k => RequestedFrameCount(k.Shot));
                client.EnsureAccepts(config.Model, maxImagesPerClip);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                return CreateFailedResult(
                    execution.Id, step.StepOrder, VideoGenerationImageSupport.UnsupportedCode,
                    ex.Message, context.CurrentStepIndex, context.IterationCount);
            }
        }

        HashSet<string>? offeredIds = null;
        int? analysisStepOrder = null;
        VideoAnalysisArtifact? analysisArtifact = null;
        // Phase 4: look matching and camera continuity read the analysis step's MEASURED per-shot
        // descriptors, so they need the artifact whenever they are on — even for a plan that names
        // no keyframe. Validate() already requires the Analysis reference in that case.
        bool wantsMeasuredShots = config.LookMatching || config.CameraContinuity;
        if (keyframeShots.Count > 0 || wantsMeasuredShots)
        {
            if (config.Analysis is null)
            {
                var offenders = string.Join(", ", keyframeShots.Select(k =>
                    $"shot {k.ShotIndex} ({DescribeRequestedFrames(k.Shot)})"));
                return CreateFailedResult(
                    execution.Id, step.StepOrder, "CONFIG_INVALID",
                    keyframeShots.Count > 0
                        ? $"Analysis is required when a planned shot requests a keyframe: {offenders} named a frame but no Analysis step reference is configured."
                        : "Analysis is required when LookMatching or CameraContinuity is enabled, but no Analysis step reference is configured.",
                    context.CurrentStepIndex, context.IterationCount);
            }

            var (loadedArtifact, analysisEntry, analysisError) = await LoadAnalysisArtifactAsync(context, config.Analysis);
            if (loadedArtifact is null)
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", analysisError ?? "The analysis artifact could not be resolved.", context.CurrentStepIndex, context.IterationCount);
            }

            analysisArtifact = loadedArtifact;
            offeredIds = new HashSet<string>(loadedArtifact.OfferedIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            analysisStepOrder = analysisEntry?.StepOrder;
        }

        // ((11)/(12)/(13)) FIRST the cap trim, because it DEFINES the effective plan: walk the plan
        // in shot order, then takes within a shot, reserving at most MaxClips clips for the whole
        // step. A clip that never reaches a ledger row is recorded here with its reason (the cap,
        // or a prompt that sanitizes to nothing) rather than dropped — and, because it never
        // reaches the vendor, such a clip consumes no MaxClips slot either, so the effective plan
        // is exactly the set of clips that will really be dispatched.
        var slots = new List<ClipSlot>();
        var effectivePlanShots = new List<ShotPlan>();
        // Change request: one entry per clip the request redoes (priced one take each), and the hash ordinal the
        // original run gave each dispatched clip, so an unchanged re-request still finds its earlier clip.
        var changeShots = changeRun is null ? null : new List<ShotPlan>();
        int dispatchOrdinal = 0;
        int reservedClips = 0;
        // Planned subjects: one preparation (pictures read and re-hashed, path chosen) per distinct set of subjects,
        // shared by every shot that shows exactly that set.
        var preparedSubjectSets = new Dictionary<string, (AvatarInputs Inputs, JsonObject Facts)>(StringComparer.Ordinal);
        for (int i = 0; i < shots.Count; i++)
        {
            ShotPlan? shot = shots[i];

            // An unreadable shot is a config fault, never a clip that quietly disappears. This is
            // the same refusal the whole-plan estimator issues for a null entry; it lives here now
            // that the trim — and therefore this loop — runs BEFORE the pricing call.
            if (shot is null)
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID",
                    $"The plan's shot at index {i} is null; an unreadable shot cannot be priced.",
                    context.CurrentStepIndex, context.IterationCount);
            }

            var shotNumber = i + 1;

            // ((8)) Where this shot's frames live, decided from the artifact's OWN shots and sources —
            // an opaque offered id becomes one object and one moment, never a time the plan supplied.
            ShotPlan frameShot = frameShots[i]!;
            ShotKeyframeResolution keyframe = ShotKeyframeResolver.Resolve(
                analysisArtifact, offeredIds, frameShot.FirstFrame, frameShot.LastFrame);

            // This shot's own subjects (null when it names none): the ones it shows, what it named and did not get,
            // and its facts object in meta.plannedSubjects. Prepared below only for a take that is really bought.
            PlannedShotSubjects? subjectState = shotSubjects?[i];
            AvatarInputs? shotInputs = null;
            var stillsByPrompt = new Dictionary<string, AvatarInputs>(StringComparer.Ordinal);

            // The state every clip of this shot carries unless it is actually dispatched WITH its
            // frames: a clip that never reaches a ledger row has no frame to apply, and an id that
            // did not resolve never had one. A frame the subjects displaced is said as such.
            KeyframeState undispatchedKeyframe = subjectState is { FramesDropped: true }
                ? KeyframeState.NotApplied(PlannedSubjects.FirstFrameDroppedForSubjects)
                : UndispatchedKeyframe(keyframe);

            // The frames cut for this shot, at most once however many takes follow, and only once a
            // take has been decided to actually be bought.
            ShotKeyframeCaptureResult? capturedFrames = null;

            // Phase 4 finishing, both deterministic and both OFF by default (byte-identical prompt
            // and camera word when off). The anchor's measured real shot supplies the look words
            // (first-party table, never the model's) and, when the planner left `camera` blank, a
            // continuity camera word. Neither ever overrides a word the planner chose.
            VideoAnalysisShotVisual? anchorVisual = wantsMeasuredShots
                ? FindAnchorShotVisual(analysisArtifact, shot.Anchor)
                : null;
            string? lookWords = config.LookMatching ? LookMatchingPromptWords.Describe(anchorVisual) : null;
            string? continuityCamera = config.CameraContinuity && string.IsNullOrWhiteSpace(shot.Camera)
                ? LookMatchingPromptWords.ContinuityCameraWord(anchorVisual)
                : null;
            ShotPlan effectiveShot = continuityCamera is null
                ? shot
                : new ShotPlan
                {
                    Purpose = shot.Purpose,
                    Anchor = shot.Anchor,
                    FirstFrame = shot.FirstFrame,
                    LastFrame = shot.LastFrame,
                    Camera = continuityCamera,
                    Duration = shot.Duration,
                    Prompt = shot.Prompt,
                    Reason = shot.Reason,
                    Subjects = shot.Subjects
                };
            string? rawPrompt = lookWords is null
                ? shot.Prompt
                : string.IsNullOrWhiteSpace(shot.Prompt) ? shot.Prompt : $"{shot.Prompt} {lookWords}";

            // The identity facts of the objects this shot shows, as a first-party constraint on its prompt
            // (docs/design/avatar-objects.md "Object fidelity"); null for a shot with no object that has facts.
            string? identityClause = subjectState is { Kept.Count: > 0 } ? SubjectIdentityPrompt.ClauseFor(subjectState.Kept) : null;

            // T-005: the shot is screened once (not once per take), after its keyframes are cut and
            // before the shared reservation. A blocked shot fails the step with nothing bought. A change
            // request may give each take its own prompt, so the screened text is remembered instead of a flag.
            string? moderatedText = null;

            for (int take = 1; take <= config.Takes; take++)
            {
                var clipId = config.Takes == 1 ? $"v{shotNumber}" : $"v{shotNumber}t{take}";

                bool retake = false;
                string? promptOverride = null;
                if (changeRun is not null)
                {
                    if (!changeRun.Plan.InScope(clipId))
                    {
                        var (carried, carryRefusal) = TryCarry(context, changeRun, clipId, shotNumber, take, shot, undispatchedKeyframe);
                        if (carryRefusal is not null)
                        {
                            return carryRefusal;
                        }

                        // A kept clip of a planned shot that shows subjects is new use of them: kept only if it was made
                        // with exactly the subjects the shot shows now, each as it is now (as an inline step's kept clip).
                        string? madeWith = carried!.PreResolvedClip![SubjectOutputFacts.ClipFingerprintKey] is JsonValue madeValue
                            && madeValue.TryGetValue(out string? made) ? made : null;
                        string? showsNow = subjectState is { Kept.Count: > 0 } ? SubjectsFingerprint.Compute(subjectState.Kept) : null;
                        if ((madeWith is not null || showsNow is not null) && !string.Equals(madeWith, showsNow, StringComparison.Ordinal))
                        {
                            return CreateFailedResult(execution.Id, step.StepOrder, AvatarFailureCodes.ConsentMissing,
                                $"Clip '{clipId}' was made with the avatars as they were before their consent or references changed, " +
                                "so it cannot be kept. Redo every clip of this shot. Nothing was made.",
                                context.CurrentStepIndex, context.IterationCount);
                        }

                        if (VideoClipChangePlan.WasDispatched(carried!.PreResolvedClip!))
                        {
                            dispatchOrdinal++;
                        }

                        slots.Add(carried);
                        continue;
                    }

                    changeShots!.Add(shot);
                    retake = changeRun.Plan.RetakeFor(clipId);
                    promptOverride = changeRun.Plan.PromptFor(clipId);
                }

                if (reservedClips >= config.MaxClips)
                {
                    slots.Add(new ClipSlot(
                        clipId, shotNumber, take, shot, null, null, undispatchedKeyframe,
                        BuildClip(new ClipFacts
                        {
                            ClipId = clipId,
                            ShotIndex = shotNumber,
                            Take = take,
                            Shot = shot,
                            Status = StatusSkipped,
                            Cost = 0m,
                            CostBasis = CostBasisReleased,
                            Keyframe = undispatchedKeyframe,
                            SkipReason = SkipMaxClipsExceeded,
                        })));
                    continue;
                }

                // ((13)) Per-shot prompt hygiene. An empty or over-length prompt degrades THAT clip
                // only — never the step — and, because it never reaches the vendor, it consumes no
                // MaxClips slot and gets no ledger row.
                var sanitized = SubjectIdentityPrompt.Apply(promptOverride ?? rawPrompt, identityClause, provider.Kind, out _);
                if (promptOverride is not null && (sanitized.IsEmpty || sanitized.ExceedsMaxLength))
                {
                    // The owner's own words are never degraded to a failed clip that would replace a good one.
                    return CreateFailedResult(execution.Id, step.StepOrder, "INVALID_PROMPT",
                        sanitized.IsEmpty ? "The new prompt is empty or whitespace." : $"The new prompt exceeds {sanitized.MaxLength} characters.",
                        context.CurrentStepIndex, context.IterationCount);
                }

                if (sanitized.IsEmpty || sanitized.ExceedsMaxLength)
                {
                    var detail = sanitized.IsEmpty
                        ? "The shot's prompt is empty or whitespace after sanitization."
                        : $"The shot's prompt exceeds {sanitized.MaxLength} characters.";
                    slots.Add(new ClipSlot(
                        clipId, shotNumber, take, shot, null, null, undispatchedKeyframe,
                        BuildClip(new ClipFacts
                        {
                            ClipId = clipId,
                            ShotIndex = shotNumber,
                            Take = take,
                            Shot = shot,
                            Status = StatusFailed,
                            Cost = 0m,
                            CostBasis = CostBasisReleased,
                            Keyframe = undispatchedKeyframe,
                            FailureReason = "INVALID_PROMPT",
                            Detail = detail,
                        })));
                    continue;
                }

                // The ONLY request builder on this path. Duration and camera phrasing are resolved
                // to provider parameters here, from first-party tables — the plan never carried a
                // number or a vendor directive.
                var request = ShotPlanParameterMapper.BuildRequest(
                    pricing.Capabilities, config.Model, config.Resolution, config.Ratio, effectiveShot, sanitized.Text);

                // ((8b)) The frame itself, cut once per shot and only for a take that is really being
                // bought — the cap trim above and the prompt check above have both already had their
                // say. This is where an id that resolved becomes bytes on the request.
                KeyframeState clipKeyframe = undispatchedKeyframe;
                if (keyframe.Resolved && capturedFrames is null)
                {
                    IMediaWorkspace? frameWorkspace = await keyframeMedia.WorkspaceAsync(context.CancellationToken);
                    if (frameWorkspace is null)
                    {
                        // The media session could not be opened at all (an unreachable fabric, a runner
                        // that is not online). That is not this clip's problem: the step fails with the
                        // session's own code BEFORE the reservation, so nothing is bought.
                        return CreateFailedResult(
                            execution.Id, step.StepOrder,
                            keyframeMedia.RefusalCode ?? "KEYFRAME_MEDIA_UNAVAILABLE",
                            keyframeMedia.RefusalMessage ?? "The media session the plan's frames are cut in could not be opened.",
                            context.CurrentStepIndex, context.IterationCount);
                    }

                    capturedFrames = await keyframeCapture.CaptureAsync(
                        frameWorkspace, keyframe, context.CancellationToken);
                }

                if (keyframe.Resolved)
                {
                    if (capturedFrames!.Images is { Count: > 0 } frames)
                    {
                        request = request with { Images = frames };
                        clipKeyframe = KeyframeState.AppliedTo();
                    }
                    else
                    {
                        clipKeyframe = KeyframeState.NotApplied(
                            capturedFrames.ClipReason ?? ShotKeyframeReasons.ExtractionFailed);
                    }
                }

                // The shot's own subjects (docs/design/avatar-objects.md "Avatars and objects in planned shots"): prepared
                // once per shot, for a take that is really being bought, before anything is reserved; the same path
                // table and the same refusals as an inline step (an unusable subject fails the step with its code).
                if (subjectState is { Kept.Count: > 0 } && shotInputs is null)
                {
                    var (prepared, preparedRefusal) = await PrepareShotSubjectsAsync(
                        context, config, provider, client, subjectState, preparedSubjectSets);
                    if (preparedRefusal is not null)
                    {
                        return preparedRefusal;
                    }

                    shotInputs = prepared;
                }

                if (!string.Equals(moderatedText, sanitized.Text, StringComparison.Ordinal))
                {
                    StepExecutionResult? refusal = await ModeratePromptAsync(
                        context, sanitized.Text, request.Images, context.CancellationToken);
                    if (refusal is not null)
                    {
                        return refusal;
                    }

                    moderatedText = sanitized.Text;
                }

                // How the subjects reach the model: their reference items, or ONE composed still of them in the scene of
                // this prompt as the first frame, made (or found) only now that the prompt was screened.
                AvatarInputs? takeInputs = shotInputs;
                if (shotInputs is { Path: VideoSubjectPath.FirstFrame })
                {
                    if (!stillsByPrompt.TryGetValue(sanitized.Text, out takeInputs))
                    {
                        var (withScene, sceneRefusal) = await MakeSubjectStillAsync(
                            context, config, shotInputs, sanitized.Text, config.Ratio, subjectState!.Entry);
                        if (sceneRefusal is not null)
                        {
                            return sceneRefusal;
                        }

                        takeInputs = withScene!;
                        stillsByPrompt[sanitized.Text] = takeInputs;
                    }
                }

                if (takeInputs is not null)
                {
                    request = takeInputs is { Path: VideoSubjectPath.FirstFrame, Still: { } sceneStill }
                        ? request with
                        {
                            Images = [new VideoGenerationImageInput(sceneStill.ContentType, sceneStill.Bytes)],
                            NoVendorRetention = takeInputs.ShowsPerson,
                        }
                        : request with { References = takeInputs.References };
                }

                // The manifest's `description` is the prompt the clip was made from: the owner's new one when given.
                ShotPlan slotShot = promptOverride is null
                    ? effectiveShot
                    : new ShotPlan
                    {
                        Purpose = effectiveShot.Purpose,
                        Anchor = effectiveShot.Anchor,
                        FirstFrame = effectiveShot.FirstFrame,
                        LastFrame = effectiveShot.LastFrame,
                        Camera = effectiveShot.Camera,
                        Duration = effectiveShot.Duration,
                        Prompt = GeneratedPromptSanitizer.Sanitize(promptOverride, provider.Kind).Text,
                        Reason = effectiveShot.Reason,
                        Subjects = effectiveShot.Subjects
                    };
                dispatchOrdinal++;
                slots.Add(new ClipSlot(
                    clipId, shotNumber, take, slotShot, request.DurationSeconds, request, clipKeyframe, null,
                    LookWords: promptOverride is null ? lookWords : null, CameraDefaulted: continuityCamera is not null,
                    HashOrdinal: changeRun is null ? null : dispatchOrdinal, Retake: retake,
                    Avatar: takeInputs, SubjectMeta: takeInputs is null ? null : subjectState!.Entry));
                effectivePlanShots.Add(effectiveShot);
                reservedClips++;
            }
        }

        // ((9)) Price BOTH the whole plan and the effective plan, fail-closed, before a ledger row
        // can exist. Each throws rather than returning <= 0 for an unpriceable duration word or
        // resolution, because a zero estimate passes every cap and would credit the daily ledger
        // with spend that never happened. An EMPTY plan is the one legitimate zero for both — there
        // is genuinely nothing to buy — and the pre-check below can never refuse it, since
        // MaxSpendCredits is validated strictly positive.
        //
        // WHY two numbers. `wholePlanEstimate` is the plan as the planner wrote it, every planned shot config.Takes times over;
        // it is only what a change request (which redoes exact clips) is checked against. `effectivePlanEstimate` is the plan
        // trimmed to MaxClips that will really be dispatched, and it is what the step cap is checked against for a normal run
        // and what `meta.planEstimated` means: the USD sum converted once to credits. A clip past MaxClips is skipped, never
        // submitted or reserved, so the person's "most clips" and "most credits" agree with the run gate, which prices
        // MaxClips clips. The atomic reservation below still enforces the step, daily and global caps on what is reserved.
        //
        // WHY the effective estimate is one entry per DISPATCHED CLIP at ONE take rather than the
        // distinct effective shots at config.Takes: the estimator multiplies every entry by
        // `takes`, and the cap trim is clip-granular, so a shot whose final take(s) the trim
        // dropped would otherwise be priced for takes that are never bought — over-stating this
        // number and breaking the meta.planEstimated == whole-plan USD sum converted once invariant.
        decimal wholePlanEstimate;
        decimal effectivePlanEstimate;
        try
        {
            // A change request prices only the clips it redoes (one entry per clip, one take each).
            wholePlanEstimate = changeShots is null
                ? BudgetUnit(pricing.EstimateWholePlanUsd(config.Resolution, config.Ratio, shots, config.Takes), provider)
                : BudgetUnit(pricing.EstimateWholePlanUsd(config.Resolution, config.Ratio, changeShots, takes: 1), provider);

            effectivePlanEstimate = BudgetUnit(
                pricing.EstimateWholePlanUsd(config.Resolution, config.Ratio, effectivePlanShots, takes: 1), provider);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", ex.Message, context.CurrentStepIndex, context.IterationCount);
        }

        // ((10)) The step-cap pre-check, deliberately HERE — before _budgetGate.ReserveAsync, which
        // owns the project/global daily budgets and its own denial mapping and is still called
        // below exactly as it was. An estimate over the cap ends the step with zero submits and zero
        // reservations, whatever the gate's implementation does: ReserveAsync is not reached on this
        // path, so no ledger row can exist.
        // The cap is checked against what this step will actually buy: the plan trimmed to MaxClips (a take counts as a clip,
        // and a clip past MaxClips is skipped, never submitted or reserved), so the person's "most clips" and "most credits"
        // agree with the run gate, which prices MaxClips clips. A change request keeps pricing exactly the clips it redoes.
        // The atomic reservation below still enforces the step cap, the daily and the global caps on what is reserved.
        decimal planStepCap = StepCap(config, provider);
        decimal capChecked = changeShots is null ? effectivePlanEstimate : wholePlanEstimate;
        if (capChecked > planStepCap)
        {
            return CreateFailedResult(
                execution.Id, step.StepOrder, "BUDGET_EXCEEDED",
                VideoBudgetMessages.Describe(
                    new BudgetRefusal(
                        BudgetCapKind.StepLimit,
                        IsGpuBudgeted(provider) ? VideoGenerationCostUnit.GpuSeconds : VideoGenerationCostUnit.Credits,
                        planStepCap, 0m, capChecked, null),
                    DateTime.UtcNow),
                context.CurrentStepIndex, context.IterationCount);
        }

        var meta = new JsonObject { { "planEstimated", effectivePlanEstimate } };
        if (shots.Count == 0)
        {
            meta["note"] = NoteEmptyPlan;
        }

        // Planned subjects: what each shot named, showed and did not get (meta.plannedSubjects), and every subject any
        // clip shows (meta.subjects, read by the reuse guard). Absent when no shot named one.
        if (shotSubjects is not null)
        {
            WritePlannedSubjectFacts(meta, shotSubjects);
        }

        return await ReserveAndDispatchAsync(
            context,
            config,
            provider,
            client,
            pricing,
            new DispatchPlan(slots, meta, IsPlanPath: true, PlanStepOrder: planEntry?.StepOrder, AnalysisStepOrder: analysisStepOrder, ShotsPlanned: shots.Count, Change: changeRun),
            sw);
    }

    // ---------------------------------------------------------------------
    // Planned subjects (docs/design/avatar-objects.md "Avatars and objects in planned shots")
    // ---------------------------------------------------------------------

    /// <summary>
    /// One planned shot's subjects: the ones it shows (resolved and allowed, at most three, at most one person),
    /// whether its planned frames were dropped for them, and its entry of <c>meta.plannedSubjects.shots[]</c> (what it
    /// named and did not get, and, once its subjects are prepared, <c>subjects</c>, <c>subjectPath</c> and
    /// <c>subjectFacts</c> exactly as an inline step records them).
    /// </summary>
    private sealed class PlannedShotSubjects
    {
        public required IReadOnlyList<ResolvedAvatar> Kept { get; init; }

        public required bool FramesDropped { get; init; }

        public required JsonObject Entry { get; init; }
    }

    /// <summary>
    /// Resolves the offer ids every shot names (<see cref="ShotPlan.Subjects"/>), before anything is read or bought:
    /// each against the project's subjects this run may show (<see cref="PlannedSubjects.UsableAsync"/>, the one rule
    /// and the deployment's switches), then the step's allowlist and the count rules. A step that does not show planned
    /// subjects drops every one (<c>planned_subjects_off</c>, <c>egress_off</c>). An id of an avatar this run could show
    /// that is not usable now fails the step with the avatar's own code, as an inline step does; any other id that names
    /// nothing usable is <c>unknown_subject</c>. Null when no shot names a subject.
    /// </summary>
    private async Task<(IReadOnlyList<PlannedShotSubjects?>? Shots, StepExecutionResult? Refusal)> ResolvePlannedSubjectsAsync(
        StepExecutionContext context, VideoGenerateStepConfig config, IReadOnlyList<ShotPlan> shots)
    {
        List<IReadOnlyList<string>> named = [.. shots.Select(s => PlannedSubjects.ParseOfferIds(s?.Subjects))];
        if (named.All(n => n.Count == 0))
        {
            return (null, null);
        }

        var execution = context.Execution;
        CancellationToken ct = context.CancellationToken;
        string? notShown = PlannedSubjects.WhyNotShown(config);
        bool admin = false;
        IReadOnlyList<ResolvedAvatar> usable = [];
        if (notShown is null && _avatars is not null)
        {
            admin = _avatarOptions.Generation == AvatarGenerationMode.PlatformAdminOnly
                && await InitiatedByPlatformAdminAsync(execution, ct);
            usable = await PlannedSubjects.UsableAsync(_avatars, _avatarOptions, admin, execution.ProjectId, ct);
        }

        HashSet<Guid>? allowed = config.PlannedSubjectIds is { } ids ? [.. ids] : null;
        List<(Guid Id, AvatarSubjectKind Kind)>? known = null;
        var result = new PlannedShotSubjects?[shots.Count];
        for (int i = 0; i < shots.Count; i++)
        {
            if (named[i].Count == 0)
            {
                continue;
            }

            var dropped = new JsonArray();
            List<ResolvedAvatar> kept = [];
            if (notShown is not null)
            {
                foreach (string offerId in named[i])
                {
                    dropped.Add(new DroppedSubject(offerId, notShown).ToNode());
                }
            }
            else
            {
                ShotSubjectResolution resolution = PlannedSubjects.ResolveShot(named[i], usable, allowed);
                kept.AddRange(resolution.Kept);
                foreach (DroppedSubject drop in resolution.Dropped)
                {
                    dropped.Add(drop.ToNode());
                }

                foreach (string offerId in resolution.Unresolved)
                {
                    known ??= await KnownSubjectsAsync(execution.ProjectId, admin, ct);
                    List<Guid> matches = [.. known
                        .Where(k => string.Equals(SubjectOfferId.For(k.Id), offerId, StringComparison.OrdinalIgnoreCase))
                        .Select(k => k.Id)];
                    if (matches.Count == 1 && allowed is not null && !allowed.Contains(matches[0]))
                    {
                        dropped.Add(new DroppedSubject(offerId, PlannedSubjects.SubjectNotAllowed).ToNode());
                        continue;
                    }

                    if (matches.Count == 1 && _avatars is not null)
                    {
                        ResolvedAvatar now = await _avatars.ResolveAsync(execution.ProjectId, matches[0], ct);
                        if (!now.Usable)
                        {
                            string reason = now.Reason ?? ProjectAvatar.ReasonConsentMissing;
                            return (null, CreateFailedResult(execution.Id, context.Step.StepOrder, AvatarFailureCodes.For(reason),
                                $"An avatar planned shot {i + 1} shows cannot be used ({reason}). Nothing was submitted.",
                                context.CurrentStepIndex, context.IterationCount));
                        }
                    }

                    dropped.Add(new DroppedSubject(offerId, PlannedSubjects.UnknownSubject).ToNode());
                }
            }

            ShotPlan shot = shots[i];
            bool framesDropped = kept.Count > 0
                && (!string.IsNullOrWhiteSpace(shot.FirstFrame) || !string.IsNullOrWhiteSpace(shot.LastFrame));
            var entry = new JsonObject { ["shotIndex"] = i + 1 };
            if (dropped.Count > 0)
            {
                entry["dropped"] = dropped;
            }

            if (framesDropped)
            {
                entry["frameDropped"] = PlannedSubjects.FirstFrameDroppedForSubjects;
            }

            result[i] = new PlannedShotSubjects { Kept = kept, FramesDropped = framesDropped, Entry = entry };
        }

        return (result, null);
    }

    /// <summary>
    /// Every avatar of the project of a kind this run may show, by id and kind, read from the rows alone (no reference
    /// is read): how an offer id that names no usable subject is told apart from one that names an unusable avatar.
    /// </summary>
    private async Task<List<(Guid Id, AvatarSubjectKind Kind)>> KnownSubjectsAsync(Guid projectId, bool admin, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var rows = await db.ProjectAvatars.AsNoTracking()
                .Where(a => a.ProjectId == projectId)
                .Select(a => new { a.Id, a.SubjectKind })
                .ToListAsync(ct);
            return [.. rows
                .Where(r => PlannedSubjects.MayShow(_avatarOptions, r.SubjectKind, admin))
                .Select(r => (r.Id, r.SubjectKind))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown: an id that names nothing usable is then reported as unknown, never sent.
            _logger.LogWarning(ex, "Could not list the avatars of project {ProjectId}; planned subjects that are not usable are reported as unknown.", projectId);
            return [];
        }
    }

    /// <summary>
    /// Prepares one planned shot's subjects through the inline step's own <see cref="PrepareSubjectsAsync"/> (switches,
    /// egress, the path table, every subject through the one rule, the material read and re-hashed), once per distinct
    /// set of subjects, and copies the recorded facts into the shot's entry.
    /// </summary>
    private async Task<(AvatarInputs? Inputs, StepExecutionResult? Refusal)> PrepareShotSubjectsAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        IVideoGenerationClient client,
        PlannedShotSubjects shot,
        Dictionary<string, (AvatarInputs Inputs, JsonObject Facts)> prepared)
    {
        string key = string.Join(",", shot.Kept.Select(k => k.Id.ToString("N")));
        if (!prepared.TryGetValue(key, out var found))
        {
            var facts = new JsonObject();
            var (inputs, refusal) = await PrepareSubjectsAsync(
                context, config, provider, client, [.. shot.Kept.Select(k => new VideoGenerateSubject(k.Id))], namesSubjects: true, facts);
            if (refusal is not null)
            {
                return (null, refusal);
            }

            found = (inputs!, facts);
            prepared[key] = found;
        }

        foreach (KeyValuePair<string, JsonNode?> fact in found.Facts)
        {
            shot.Entry[fact.Key] = fact.Value?.DeepClone();
        }

        return (found.Inputs, null);
    }

    /// <summary>A copy of <paramref name="shot"/> without its first and last frame (the subjects it shows took their place).</summary>
    private static ShotPlan WithoutFrames(ShotPlan shot) => new()
    {
        Purpose = shot.Purpose,
        Anchor = shot.Anchor,
        FirstFrame = null,
        LastFrame = null,
        Camera = shot.Camera,
        Duration = shot.Duration,
        Prompt = shot.Prompt,
        Reason = shot.Reason,
        Subjects = shot.Subjects,
    };

    /// <summary>
    /// <c>meta.plannedSubjects.shots[]</c>: one entry per shot that named a subject. And <c>meta.subjects[]</c>: every
    /// subject a clip of this step shows, once, with the fingerprint it was shown under, which is what the reuse guard
    /// checks before a recorded result of this step is used again.
    /// </summary>
    private static void WritePlannedSubjectFacts(JsonObject meta, IReadOnlyList<PlannedShotSubjects?> shots)
    {
        var entries = new JsonArray();
        var union = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlannedShotSubjects? shot in shots)
        {
            if (shot is null)
            {
                continue;
            }

            if (shot.Entry[SubjectOutputFacts.MetaKey] is JsonArray shown)
            {
                foreach (JsonObject subject in shown.OfType<JsonObject>())
                {
                    if (subject["avatarId"]?.GetValue<string>() is { } id && seen.Add(id))
                    {
                        union.Add(new JsonObject
                        {
                            ["avatarId"] = id,
                            ["name"] = subject["name"]?.DeepClone(),
                            ["kind"] = subject["kind"]?.DeepClone(),
                            ["fingerprint"] = subject["fingerprint"]?.DeepClone(),
                        });
                    }
                }
            }

            entries.Add(shot.Entry);
        }

        meta["plannedSubjects"] = new JsonObject { ["shots"] = entries };
        if (union.Count > 0)
        {
            meta[SubjectOutputFacts.MetaKey] = union;
        }
    }

    // ---------------------------------------------------------------------
    // Shared: one atomic reservation, then the per-clip dispatch loop
    // ---------------------------------------------------------------------

    /// <summary>
    /// The single atomic reservation for the whole step, followed by the per-clip
    /// submit/poll/deliver loop. Shared by both plan sources so exactly one piece of code applies
    /// every phase-1 ledger exit rule, and so the plan path adds no exit path of its own.
    /// </summary>
    private async Task<StepExecutionResult> ReserveAndDispatchAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        IVideoGenerationClient client,
        VideoGenerationPricingBasis pricing,
        DispatchPlan plan,
        System.Diagnostics.Stopwatch sw)
    {
        var execution = context.Execution;
        var step = context.Step;
        var meta = plan.Meta;

        // T-105: an unset ConformHeight defaults to the run's explicit output resolution (short side), so a clip is
        // never left below the picture the run asked for; a run on Auto changes nothing. `config` is this step's own
        // freshly deserialised instance, and the resolved value is reported in the output so it is never silent.
        ConformHeightResolution conform = VideoGenerateStepConfig.ResolveConformHeight(
            config.ConformHeight, RunStyleOverrides.OutputResolutionOf(execution), config.Ratio);
        config.ConformHeight = conform.Height;
        if (conform.Source == "run")
        {
            meta["conformHeight"] = new JsonObject { ["height"] = conform.Height, ["source"] = conform.Source };
        }

        var dispatches = plan.Slots.Where(s => s.Request is not null).ToList();

        // Price each dispatched clip fail-closed (throws rather than returning <= 0). The clip
        // ordinal doubles as the request-hash discriminator, so two planned shots that happen to
        // share a prompt can never collapse onto one ledger row.
        //
        // Each take also carries pricing.Quote — the vendor's OWN figure when the price came from an
        // oracle, null when it came from a published table. The budget gate enforces that a quoted
        // take is charged the quote's own total, because "the ledger records the vendor's exact
        // figure" is an invariant of the component that writes money, not a convention of one caller.
        var takeRequests = new List<VideoGenerationTakeRequest>(dispatches.Count);
        try
        {
            for (int i = 0; i < dispatches.Count; i++)
            {
                var slot = dispatches[i];
                // Avatars: the vendor's input-material charge for the references (extra pictures, input video) is
                // part of every take's price, so the reservation, the step cap and the hold all see it. A planned shot's
                // own subjects (slot.Avatar) are this take's; a take without them is exactly what it always was.
                AvatarInputs? slotAvatar = slot.Avatar ?? plan.Avatar;
                takeRequests.Add(new VideoGenerationTakeRequest(
                    slot.ClipId,
                    ComputeRequestHash(
                        slot.Request!.PromptText, config, slot.HashOrdinal ?? (i + 1),
                        slot.Retake ? plan.Change?.RequestId : null, slotAvatar?.RequestKey),
                    BudgetUnit(
                        pricing.ClipCostUsd(config.Resolution, config.Ratio, slot.Request!.DurationSeconds)
                            + (slotAvatar?.InputMaterialUsd ?? 0m),
                        provider),
                    pricing.Quote,
                    AvatarId: slot.Avatar?.AvatarId,
                    SubjectIds: slot.Avatar?.SubjectIds));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return CreateFailedResult(execution.Id, step.StepOrder, "CONFIG_INVALID", ex.Message, context.CurrentStepIndex, context.IterationCount);
        }

        var clips = new List<JsonObject>(plan.Slots.Count);
        var failures = new List<string>();
        string? firstFailureDetail = null;
        (string Code, string Detail)? avatarStop = null;
        string? firstSucceededStorageKey = null;
        decimal totalEstimatedCost = 0m;

        // REUSE (default): a take whose identical request already has a Succeeded ledger row with a
        // stored clip is served from that clip and never reaches the budget gate, so it reserves
        // and submits nothing. Regenerate (execution flag, or loop-back + LoopBackBehavior) skips it.
        var clipsByClipId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var regenerate = _runFlags.RegenerateVideoClips(execution.Id)
            || (context.IterationCount > 0 && config.LoopBackBehavior == VideoGenerateLoopBackBehavior.Regenerate);
        var reusable = regenerate
            ? new Dictionary<string, ReusableClip>(StringComparer.Ordinal)
            : await FindReusableClipsAsync(execution, provider, takeRequests, context.CancellationToken);
        decimal reusedSaved = 0m;
        var buyIndices = new List<int>(dispatches.Count);
        for (int i = 0; i < dispatches.Count; i++)
        {
            var slot = dispatches[i];
            AvatarInputs? reusedAvatar = slot.Avatar ?? plan.Avatar;
            if (reusable.TryGetValue(takeRequests[i].RequestHash, out var hit))
            {
                clipsByClipId[slot.ClipId] = BuildClip(new ClipFacts
                {
                    ClipId = slot.ClipId,
                    ShotIndex = slot.ShotIndex,
                    Take = slot.Take,
                    Shot = slot.Shot,
                    DurationSeconds = slot.DurationSeconds,
                    Status = StatusSucceeded,
                    StorageKey = hit.StorageKey,
                    Cost = 0m,
                    CostBasis = CostBasisReleased,
                    Keyframe = slot.Keyframe,
                    Reused = true,
                    LookWords = slot.LookWords,
                    CameraDefaulted = slot.CameraDefaulted,
                    AvatarFingerprint = reusedAvatar is { SubjectsOnClips: false } ? reusedAvatar.Fingerprint : null,
                    SubjectsFingerprint = reusedAvatar is { SubjectsOnClips: true } ? reusedAvatar.Fingerprint : null,
                    Subjects = reusedAvatar?.Shown,
                });
                firstSucceededStorageKey ??= hit.StorageKey;
                reusedSaved += takeRequests[i].CostEstimate;
            }
            else
            {
                buyIndices.Add(i);
            }
        }

        var buyRequests = buyIndices.Select(i => takeRequests[i]).ToList();

        if (buyRequests.Count > 0)
        {
            // SQFU-S1: a run this instance no longer owns (the sweeper handed it on after a stall) reserves nothing.
            if (_leases is not null && !await _leases.TryHoldAsync(execution.Id, CancellationToken.None))
            {
                return CreateFailedResult(execution.Id, step.StepOrder, "NOT_SUBMITTED",
                    Services.ExecutionLeaseService.LeaseLostReason + " No take was reserved or submitted.",
                    context.CurrentStepIndex, context.IterationCount);
            }

            // Atomically check every cap and reserve every take's ledger row, once, before any submit.
            var reservation = await _budgetGate.ReserveAsync(
                new VideoGenerationReservationRequest(
                    execution.ProjectId, execution.Id, context.StepResultId, provider.ProviderId ?? Guid.Empty,
                    StepCap(config, provider), buyRequests,
                    IsGpuBudgeted(provider) ? VideoGenerationCostUnit.GpuSeconds : VideoGenerationCostUnit.Credits,
                    AvatarId: plan.Avatar?.AvatarId,
                    SubjectIds: plan.Avatar?.SubjectIds),
                context.CancellationToken);

            if (!reservation.Allowed)
            {
                var denyReason = reservation.DenyReason ?? VideoBudgetMessages.Code + ":";
                var failureCode = denyReason.StartsWith("CONFIG_INVALID:", StringComparison.Ordinal)
                    ? "CONFIG_INVALID"
                    : denyReason.StartsWith("PLAN_NOT_ENTITLED:", StringComparison.Ordinal)
                        ? "PLAN_NOT_ENTITLED"
                        : denyReason.StartsWith("RESERVATION_ORPHANED:", StringComparison.Ordinal)
                            ? "RESERVATION_ORPHANED"
                            : VideoBudgetMessages.Code;

                // The text a person reads ("Last error: ..."): the gate's sentence without its code
                // prefix, and never blank, whatever a gate implementation returned.
                var denyText = denyReason.StartsWith(failureCode + ":", StringComparison.Ordinal)
                    ? denyReason[(failureCode.Length + 1)..].Trim()
                    : denyReason.Trim();
                if (denyText.Length == 0)
                {
                    denyText = failureCode == VideoBudgetMessages.Code
                        ? VideoBudgetMessages.GenericSentence
                        : $"Video generation was refused ({failureCode}).";
                }

                return CreateFailedResult(execution.Id, step.StepOrder, failureCode,
                    denyText, context.CurrentStepIndex, context.IterationCount);
            }

            totalEstimatedCost = buyRequests.Sum(t => t.CostEstimate);

            // Process each take. Every exit path persists the ledger status the T-020 contract
            // names (see TakeOutcome/ApplyTerminalAsync); terminal writes use CancellationToken.None
            // so a stopped step still records them. The context is created with None too: a
            // cancelled step must still reach each fresh take to release it as NOT_SUBMITTED.
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var providerSlot = ProviderSlot(provider.ProviderId ?? Guid.Empty);

            for (int takeIndex = 0; takeIndex < reservation.Takes.Count; takeIndex++)
            {
                var reservedTake = reservation.Takes[takeIndex];
                var slot = dispatches[buyIndices[takeIndex]];
                AvatarInputs? takeAvatar = slot.Avatar ?? plan.Avatar;
                var state = new TakeState();
                TakeOutcome outcome;
                try
                {
                    outcome = await ProcessClipAsync(
                        client, pricing, config, slot.Request!, execution, step, takeIndex + 1, reservedTake, dbContext,
                        providerSlot, state, context.CancellationToken, provider, takeAvatar,
                        slot.Avatar is not null ? slot.SubjectMeta : meta);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing take {TakeIndex} for execution {ExecutionId}",
                        takeIndex + 1, execution.Id);
                    outcome = await HandleUnexpectedAsync(ex, reservedTake, state, dbContext);
                }

                var (basis, cost) = ResolveCostLabel(state.Job, reservedTake);
                clipsByClipId[reservedTake.ClipId] = BuildClip(new ClipFacts
                {
                    ClipId = reservedTake.ClipId,
                    ShotIndex = slot.ShotIndex,
                    Take = slot.Take,
                    Shot = slot.Shot,
                    DurationSeconds = slot.DurationSeconds,
                    Status = outcome.StorageKey is null ? StatusFailed : StatusSucceeded,
                    StorageKey = outcome.StorageKey,
                    Cost = cost,
                    CostBasis = basis,
                    Keyframe = slot.Keyframe,
                    FailureReason = outcome.StorageKey is null ? outcome.Code : null,
                    Detail = outcome.StorageKey is null ? outcome.Detail : null,
                    LookWords = slot.LookWords,
                    CameraDefaulted = slot.CameraDefaulted,
                    AvatarFingerprint = takeAvatar is { SubjectsOnClips: false } ? takeAvatar.Fingerprint : null,
                    SubjectsFingerprint = takeAvatar is { SubjectsOnClips: true } ? takeAvatar.Fingerprint : null,
                    Subjects = takeAvatar?.Shown,
                    Fidelity = outcome.StorageKey is null ? null : state.Fidelity,
                });

                if (outcome.StorageKey is null)
                {
                    failures.Add($"{reservedTake.ClipId}:{outcome.Code}");
                    firstFailureDetail ??= outcome.Detail;
                    // A take refused because the avatar stopped being usable fails the whole step with that code
                    // (UserInput): a step asked to show a person never ends "partly" without saying why.
                    if (outcome.Code is { } code && code.StartsWith("AVATAR_", StringComparison.Ordinal))
                    {
                        avatarStop ??= (code, outcome.Detail ?? code);
                    }
                }
                else
                {
                    firstSucceededStorageKey ??= outcome.StorageKey;
                }
            }

        }

        // Nothing to buy covers an empty plan, one whose clips were all skipped or unusable, and one
        // fully served from reuse: no gate call, so no reservation can exist.
        foreach (var slot in plan.Slots)
        {
            clips.Add(slot.Request is null ? slot.PreResolvedClip! : clipsByClipId[slot.ClipId]);
        }

        // Clips carried over from the finished step were paid for there: they are not this attempt's cost.
        var carriedClips = plan.Slots.Where(s => s.Carried).Select(s => s.PreResolvedClip!).ToHashSet();
        var madeClips = clips.Where(c => !carriedClips.Contains(c)).ToList();
        if (plan.Change is not null)
        {
            firstSucceededStorageKey = clips
                .Where(c => c["status"]?.GetValue<string>() == StatusSucceeded && c["storageKey"] is not null)
                .Select(c => c["storageKey"]!.GetValue<string>())
                .FirstOrDefault();
        }

        decimal SumBasis(string basis) => madeClips
            .Where(c => c["costBasis"]!.GetValue<string>() == basis)
            .Sum(c => c["cost"]!.GetValue<decimal>());

        // Phase 5: every cost figure below is denominated in this unit. For a priced vendor it is
        // "credits" (1 credit = $0.006 of vendor cost, converted by CreditCurrency at the boundary);
        // for a self-hosted ComfyUI it is "gpuSeconds" and the figures are GPU time, not money.
        meta["costUnit"] = IsGpuBudgeted(provider) ? "gpuSeconds" : ExternalGenerationJob.CreditsUnit;
        meta["totalCost"] = madeClips.Sum(c => c["cost"]!.GetValue<decimal>());
        meta["actualCost"] = SumBasis(CostBasisActual);
        meta["unconfirmedCost"] = SumBasis(CostBasisEstimate);
        meta["estimatedCost"] = totalEstimatedCost;
        meta["clipsGenerated"] = madeClips.Count(c => c["status"]?.GetValue<string>() == StatusSucceeded);
        meta["clipsFailed"] = madeClips.Count(c => c["status"]?.GetValue<string>() == StatusFailed);
        meta["clipsReused"] = madeClips.Count(c => c["reused"]?.GetValue<bool>() == true);
        meta["reusedSaved"] = reusedSaved;
        meta["regenerate"] = regenerate;
        meta["clipsSkipped"] = madeClips.Count(c => c["status"]?.GetValue<string>() == StatusSkipped);
        if (plan.Change is not null)
        {
            meta["clipsCarried"] = carriedClips.Count;
            ChangeRequestEngineDiagnostics.Record(nameof(StepType.VideoGenerate), plan.Slots.Count(s => !s.Carried && s.Request is not null), carriedClips.Count);
            meta["changeRequest"] = new JsonObject
            {
                ["requestId"] = plan.Change.RequestId.ToString("D"),
                ["redoneClipIds"] = new JsonArray(plan.Slots.Where(s => !s.Carried && s.Request is not null)
                    .Select(s => (JsonNode)JsonValue.Create(s.ClipId)!).ToArray()),
                ["reusedClipCount"] = carriedClips.Count
            };
        }

        if (plan.IsPlanPath)
        {
            meta["shotsPlanned"] = plan.ShotsPlanned;
            meta["keyframesRequested"] = clips.Count(c => c["keyframeRequested"]!.GetValue<bool>());
            meta["keyframesNotApplied"] = clips.Count(c =>
                c["keyframeRequested"]!.GetValue<bool>() && !c["keyframeApplied"]!.GetValue<bool>());
        }

        var allFailed = dispatches.Count > 0 && failures.Count == dispatches.Count;
        if (allFailed)
        {
            meta["failed"] = "ALL_TAKES_FAILED";
        }

        // Avatars: a take refused at its submit because the avatar is no longer usable fails the step with the
        // avatar's code (UserInput, the refused takes were released). Clips already delivered stay in the manifest.
        if (avatarStop is { } stop)
        {
            meta["failed"] = stop.Code;
            meta["error"] = stop.Detail;
        }

        // The per-clip manifest is the ONLY place a clip's storage key is written down for a
        // consumer: OutputStorageKey carries just the first success, and the step's Output JSON
        // deliberately carries none (see ToOutputClip).
        string? manifestStorageKey = null;
        try
        {
            manifestStorageKey = await UploadManifestAsync(context, config, provider, plan, clips, meta);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Generated-clips manifest for execution {ExecutionId} was not written: the step was stopped.", execution.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not write the generated-clips manifest for execution {ExecutionId}; the clips themselves are unaffected.",
                execution.Id);
        }

        var output = new JsonObject
        {
            { "clips", new JsonArray(clips.Select(c => (JsonNode)ToOutputClip(c)).ToArray()) },
            { "meta", meta }
        };

        sw.Stop();

        return new StepExecutionResult
        {
            Status = allFailed || avatarStop is not null ? StepStatus.Failed : StepStatus.Completed,
            Output = output.ToJsonString(OutputJsonOptions),
            ErrorDetails = avatarStop is { } avatarFailure
                ? avatarFailure.Detail
                : allFailed ? string.Join(", ", failures) + (firstFailureDetail is null ? string.Empty : $". {firstFailureDetail}") : null,
            OutputStorageKey = firstSucceededStorageKey,
            ArtifactStorageKey = manifestStorageKey,
            DurationMs = (int)sw.ElapsedMilliseconds,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount
        };
    }

    // ---------------------------------------------------------------------
    // Plan / analysis resolution
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolves an <see cref="ExtractInputRef"/> (<c>Previous</c>/<c>Step</c> only) to the
    /// <c>StepOutputHistory</c> entry it names — the same convention
    /// <c>VideoCompileStepExecutor.ResolveDecisionJson</c> and the rooms established.
    /// <c>LastOrDefault</c>, not <c>FirstOrDefault</c>: after a ReviewLoop loop-back, history can
    /// hold two entries for one StepOrder and an explicit reference must resolve to the freshest.
    /// </summary>
    private static StepOutputHistoryEntry? ResolveHistoryEntry(StepExecutionContext context, ExtractInputRef reference) =>
        reference.From switch
        {
            ExtractInputSource.Previous => context.StepOutputHistory
                .LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Output)),
            ExtractInputSource.Step => reference.StepOrder.HasValue
                ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == reference.StepOrder.Value)
                : null,
            _ => null,
        };

    /// <summary>
    /// Resolves a step reference to the JSON object inside that step's raw output.
    /// </summary>
    /// <remarks>
    /// The stored output is the agent's raw completion text, not a re-serialized JSON value: a
    /// reasoning-capable model routinely emits the object and keeps going (trailing prose, a stray
    /// fence, leaked tool-call-closing tags) and its reasoning may itself contain small balanced
    /// brace pairs long before the real answer. <see cref="RobustJsonExtractor.ExtractLastJsonObject"/>
    /// is what makes that survivable — the same call, for the same reasons, as the compile step's
    /// decision resolution.
    /// </remarks>
    private static (string? Json, StepOutputHistoryEntry? Entry, string? Error) ResolveStepJson(
        StepExecutionContext context, ExtractInputRef reference, string label)
    {
        var entry = ResolveHistoryEntry(context, reference);
        if (entry is null)
        {
            return (null, null, $"{label} input ({DescribeReference(reference)}) did not resolve to a completed step output.");
        }

        string? extracted = RobustJsonExtractor.ExtractLastJsonObject(entry.Output);
        return extracted is null
            ? (null, entry, $"{label} input (step {entry.StepOrder}) did not contain a recognizable JSON object.")
            : (extracted, entry, null);
    }

    private static string DescribeReference(ExtractInputRef reference) =>
        reference.From == ExtractInputSource.Step
            ? $"From=Step, StepOrder={reference.StepOrder?.ToString() ?? "<null>"}"
            : $"From={reference.From}";

    /// <summary>
    /// Downloads the referenced <c>VideoAnalyze</c> step's full artifact and returns the set of ids
    /// it actually OFFERED the planner.
    /// </summary>
    /// <remarks>
    /// The bounded view's <c>OfferedIds</c> — not "every id present in the artifact" — is the
    /// authoritative set (plan §4.3), so an id the artifact happens to contain but never showed the
    /// model does not become a usable keyframe.
    /// </remarks>
    private async Task<(VideoAnalysisArtifact? Artifact, StepOutputHistoryEntry? Entry, string? Error)> LoadAnalysisArtifactAsync(
        StepExecutionContext context, ExtractInputRef analysisRef)
    {
        var (json, entry, error) = ResolveStepJson(context, analysisRef, "Analysis");
        if (json is null)
        {
            return (null, entry, error);
        }

        if (entry is null || string.IsNullOrWhiteSpace(entry.ArtifactStorageKey))
        {
            return (null, entry, "Analysis input resolved to a step output with no ArtifactStorageKey; the full VideoAnalyze artifact is required to resolve keyframe ids.");
        }

        string localPath = Path.GetTempFileName();
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(
                context.Execution.ProjectId, entry.ArtifactStorageKey, localPath, context.CancellationToken);

            string artifactJson = await File.ReadAllTextAsync(localPath, context.CancellationToken);
            var artifact = JsonSerializer.Deserialize<VideoAnalysisArtifact>(artifactJson, ArtifactJsonOptions);
            if (artifact is null)
            {
                return (null, entry, "Analysis artifact deserialized to null.");
            }

            return (artifact, entry, null);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return (null, entry, $"Analysis artifact is not valid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            // S-39: tenants read this output; the storage exception (hosts, buckets) stays in the log.
            _logger.LogWarning(ex, "Analysis artifact for execution {ExecutionId} could not be read", context.Execution.Id);
            return (null, entry, "The analysis artifact could not be read; the details are in the server log.");
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    /// <summary>
    /// The measured descriptors of the REAL shot a planned shot's anchor sits in: the shot itself
    /// for an <c>s{n}</c> anchor, else the shot of the same source containing the moment a
    /// <c>g{n}</c>/<c>t{n}</c> anchor begins. Null when the artifact, the anchor or the
    /// descriptors are missing — every caller then degrades to "no clause / no default".
    /// </summary>
    internal static VideoAnalysisShotVisual? FindAnchorShotVisual(VideoAnalysisArtifact? artifact, string? anchorId)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(anchorId))
        {
            return null;
        }

        string id = anchorId.Trim();
        VideoAnalysisShot? shot = artifact.Shots?.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (shot is not null)
        {
            return shot.Visual;
        }

        (double StartSec, int SourceIndex)? moment = null;
        VideoAnalysisSilenceSpan? silence = artifact.SilenceSpans?.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
        if (silence is not null)
        {
            moment = (silence.StartSec, silence.SourceIndex);
        }
        else
        {
            VideoAnalysisSegment? segment = artifact.Segments?.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            if (segment is not null)
            {
                moment = (segment.StartSec, segment.SourceIndex);
            }
        }

        if (moment is null || artifact.Shots is null)
        {
            return null;
        }

        VideoAnalysisShot? containing = artifact.Shots
            .Where(s => s.SourceIndex == moment.Value.SourceIndex && s.StartSec <= moment.Value.StartSec && moment.Value.StartSec < s.EndSec)
            .OrderBy(s => s.StartSec)
            .FirstOrDefault();
        return containing?.Visual;
    }

    /// <summary>Whether the resolved provider is budgeted in GPU time rather than money (self-hosted ComfyUI).</summary>
    private static bool IsGpuBudgeted(ResolvedVideoGenerationProvider provider) =>
        provider.Kind == InferenceProviderKind.ComfyUI;

    /// <summary>
    /// The step's own cap in the provider's unit: credits for a priced vendor, GPU-SECONDS
    /// (<c>MaxGpuMinutes × 60</c>) for ComfyUI, whose estimates are GPU-seconds too.
    /// </summary>
    private static decimal StepCap(VideoGenerateStepConfig config, ResolvedVideoGenerationProvider provider) =>
        IsGpuBudgeted(provider) ? config.MaxGpuMinutes * 60m : config.MaxSpendCredits;

    /// <summary>
    /// Converts a pricing figure into the budget unit the gate and ledger use: for a priced vendor
    /// the pricing basis returns the vendor's OWN dollar figure and the platform ledger is credits
    /// (1 credit = <c>$0.006</c>, <see cref="CreditCurrency"/>), so it is converted here at the
    /// boundary; a ComfyUI basis already returns GPU-seconds, which pass through untouched.
    /// </summary>
    private static decimal BudgetUnit(decimal pricingFigure, ResolvedVideoGenerationProvider provider) =>
        IsGpuBudgeted(provider) ? pricingFigure : CreditCurrency.CreditsFromUsd(pricingFigure);

    /// <summary>
    /// The keyframe state a clip carries when it never reaches a ledger row, or when the shot's ids
    /// did not resolve at all.
    /// </summary>
    /// <remarks>
    /// A clip that is skipped past <c>MaxClips</c> (or whose prompt sanitizes to nothing) has no frame
    /// applied because there is no clip: no frame was cut for it and none was needed. That is a third,
    /// distinct outcome from "the id did not resolve" and from "the frame could not be cut", and it is
    /// recorded as such rather than reusing one of them.
    /// </remarks>
    private static KeyframeState UndispatchedKeyframe(ShotKeyframeResolution keyframe) =>
        keyframe.Reason is not null
            ? KeyframeState.NotApplied(keyframe.Reason)
            : keyframe.Resolved
                ? KeyframeState.NotApplied(ShotKeyframeReasons.NotDispatched)
                : KeyframeState.NotRequested;

    /// <summary>How many frames one planned shot asks for: 0, 1 or 2.</summary>
    private static int RequestedFrameCount(ShotPlan shot) =>
        (string.IsNullOrWhiteSpace(shot.FirstFrame) ? 0 : 1) + (string.IsNullOrWhiteSpace(shot.LastFrame) ? 0 : 1);

    private static string DescribeRequestedFrames(ShotPlan shot)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(shot.FirstFrame))
        {
            parts.Add($"firstFrame '{shot.FirstFrame.Trim()}'");
        }

        if (!string.IsNullOrWhiteSpace(shot.LastFrame))
        {
            parts.Add($"lastFrame '{shot.LastFrame.Trim()}'");
        }

        return string.Join(", ", parts);
    }

    // ---------------------------------------------------------------------
    // Manifest
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes the per-clip manifest to
    /// <c>projects/{projectId}/agentFiles/video-analysis/{executionId}/step-{order}-generated-clips.json</c>
    /// and returns its storage key.
    /// </summary>
    /// <remarks>
    /// The <c>video-analysis</c> prefix is deliberate: it is the prefix
    /// <c>StepResultArtifactsController</c> already validates against, so this document is
    /// reachable through the existing artifact endpoint and can never be confused with a playable
    /// render (which lives under <c>outputFiles</c>).
    /// </remarks>
    private async Task<string> UploadManifestAsync(
        StepExecutionContext context,
        VideoGenerateStepConfig config,
        ResolvedVideoGenerationProvider provider,
        DispatchPlan plan,
        IReadOnlyList<JsonObject> clips,
        JsonObject meta)
    {
        var manifest = new JsonObject
        {
            { "version", 1 },
            { "planStepOrder", plan.PlanStepOrder },
            { "analysisStepOrder", plan.AnalysisStepOrder },
            { "providerId", provider.ProviderId },
            { "model", config.Model },
            { "resolution", config.Resolution },
            { "ratio", config.Ratio },
            { "takes", config.Takes },
            // Deep-cloned: JsonNode instances are single-parent, and the same clip objects and meta
            // are also serialized into the step's Output below.
            { "clips", new JsonArray(clips.Select(c => (JsonNode)c.DeepClone()).ToArray()) },
            { "meta", meta.DeepClone() },
        };

        string localPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(localPath, manifest.ToJsonString(OutputJsonOptions), context.CancellationToken);

            string fileName = $"video-analysis/{context.Execution.Id:D}/step-{context.Step.StepOrder}-generated-clips.json";
            return await _workspace.UploadArtifactAsync(
                context.Execution.ProjectId, localPath, fileName, "application/json", context.CancellationToken);
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    // ---------------------------------------------------------------------
    // Clip shapes
    // ---------------------------------------------------------------------

    /// <summary>What one clip's manifest entry needs to know about the plan's own words.</summary>
    private sealed record ClipFacts
    {
        public required string ClipId { get; init; }
        public int? ShotIndex { get; init; }
        public int Take { get; init; }
        public ShotPlan? Shot { get; init; }
        public int? DurationSeconds { get; init; }
        public required string Status { get; init; }
        public string? StorageKey { get; init; }
        public decimal Cost { get; init; }
        public string? CostBasis { get; init; }
        public KeyframeState Keyframe { get; init; }
        public string? FailureReason { get; init; }
        public string? Detail { get; init; }
        public string? SkipReason { get; init; }
        public bool Reused { get; init; }

        /// <summary>Phase 4: the look-matching clause appended to this clip's prompt, or null when off / unmeasured.</summary>
        public string? LookWords { get; init; }

        /// <summary>Phase 4: whether the camera word was defaulted from the anchor's measured move (never overriding a planned word).</summary>
        public bool CameraDefaulted { get; init; }

        /// <summary>Avatars: the fingerprint of the avatar this clip shows (null without one); see <see cref="AvatarFingerprint"/>.</summary>
        public string? AvatarFingerprint { get; init; }

        /// <summary>
        /// Several subjects: the fingerprint of the SET this clip shows (a step that names <c>Subjects</c>; null
        /// otherwise); see <see cref="Services.Avatars.SubjectsFingerprint"/>.
        /// </summary>
        public string? SubjectsFingerprint { get; init; }

        /// <summary>Who and what the clip shows, by name and kind (null without a subject): the manifest's <c>subjects</c>.</summary>
        public IReadOnlyList<ShownSubject>? Subjects { get; init; }

        /// <summary>The take check's verdict word (<see cref="SubjectFidelityStatus"/>), null when the check did not apply: the manifest's <c>subjectFidelity.status</c>.</summary>
        public string? Fidelity { get; init; }
    }

    /// <summary>
    /// Builds one clip's manifest entry — the FULL record, storage key included. Every field the
    /// compile side will read is written here in one place so the manifest cannot drift from the
    /// step's Output, which is a projection of this (<see cref="ToOutputClip"/>).
    /// </summary>
    private static JsonObject BuildClip(ClipFacts facts)
    {
        var shot = facts.Shot;
        var clip = new JsonObject
        {
            { "clipId", facts.ClipId },
            { "shotIndex", facts.ShotIndex },
            { "take", facts.Take },
            { "purpose", shot?.Purpose },
            { "anchor", shot?.Anchor },
            { "firstFrame", shot?.FirstFrame },
            { "lastFrame", shot?.LastFrame },
            { "camera", shot?.Camera },
            { "duration", shot?.Duration },
            // What the clip shows, as planned — VideoAnalyze describes the clip from this rather
            // than asking a vision model to guess back what was asked for.
            { "description", shot?.Prompt },
            { "durationSeconds", facts.DurationSeconds },
            { "status", facts.Status },
            { "storageKey", facts.StorageKey },
            { "cost", facts.Cost },
            { "costBasis", facts.CostBasis },
            { "keyframeRequested", facts.Keyframe.Requested },
            { "keyframeApplied", facts.Keyframe.Applied },
            { "keyframeReason", facts.Keyframe.Reason },
            { "failureReason", facts.FailureReason },
            { "detail", facts.Detail },
            { "skipReason", facts.SkipReason },
            { "reused", facts.Reused },
            // Phase 4 additive keys (null/false when the features are off).
            { "lookWords", facts.LookWords },
            { "cameraDefaulted", facts.CameraDefaulted },
        };
        // Avatars, additive and only when set so a clip without one keeps its exact manifest entry: the clip is
        // AI-generated and shows a consented likeness, and the fingerprint lets a later change request keep it only
        // while the avatar is unchanged.
        if (facts.AvatarFingerprint is not null)
        {
            clip["aiGenerated"] = true;
            clip[AvatarOutputFacts.ClipFingerprintKey] = facts.AvatarFingerprint;
        }
        else if (facts.SubjectsFingerprint is not null)
        {
            clip["aiGenerated"] = true;
            clip[SubjectOutputFacts.ClipFingerprintKey] = facts.SubjectsFingerprint;
        }

        // Who and what the clip shows, by name and kind (docs/design/avatar-objects.md): what a VideoAnalyze over these
        // clips copies into view.sources[], which the dailies room reads. Only on a clip that shows a subject.
        if (facts.Subjects is { Count: > 0 } shown)
        {
            clip["subjects"] = new JsonArray([.. shown.Select(s => (JsonNode)s.ToNode())]);
        }

        // The take check's verdict (docs/design/avatar-objects.md "Object fidelity"), only on a clip that was checked.
        if (facts.Fidelity is { } fidelity)
        {
            clip[SubjectFidelityStatus.ClipKey] = new JsonObject { ["status"] = fidelity };
        }

        return clip;
    }

    /// <summary>
    /// The step's Output JSON projection of a manifest clip: the same status/cost vocabulary with
    /// the storage key REPLACED by a <c>stored</c> boolean.
    /// </summary>
    /// <remarks>
    /// WHY the key is dropped rather than kept: <c>OutputJson</c> is fed to later agents under
    /// <c>AgentInputContextMode.FullWorkflow</c>, so a storage path here would put an internal
    /// object key in a model's prompt — the exact leak this wave forbids. Every consumer that
    /// needs the key has the manifest instead.
    /// </remarks>
    private static JsonObject ToOutputClip(JsonObject clip)
    {
        var output = new JsonObject
        {
            { "clipId", clip["clipId"]!.GetValue<string>() },
            { "status", clip["status"]!.GetValue<string>() },
        };

        if (clip["failureReason"] is JsonNode failureReason)
        {
            output["failureReason"] = failureReason.GetValue<string>();
            output["detail"] = clip["detail"]?.GetValue<string>();
        }

        if (clip["skipReason"] is JsonNode skipReason)
        {
            output["skipReason"] = skipReason.GetValue<string>();
        }

        // What the clip shows and how long it is: a change request lists its items (and their prompts) from here.
        if (clip["description"] is JsonValue desc && desc.TryGetValue(out string? description) && !string.IsNullOrWhiteSpace(description))
        {
            output["description"] = description.Length <= StepChangeCapabilities.MaxItemTextLength
                ? description
                : description[..StepChangeCapabilities.MaxItemTextLength] + "...";
        }

        if (clip["durationSeconds"] is JsonValue dur && dur.TryGetValue(out int durationSeconds))
        {
            output["durationSeconds"] = durationSeconds;
        }

        if (clip[SubjectFidelityStatus.ClipKey] is JsonObject fidelityNode && fidelityNode["status"] is JsonValue fidelityStatus)
        {
            output[SubjectFidelityStatus.ClipKey] = new JsonObject { ["status"] = fidelityStatus.GetValue<string>() };
        }

        output["stored"] = clip["storageKey"] is not null;
        output["reused"] = clip["reused"]?.GetValue<bool>() ?? false;
        output["cost"] = clip["cost"]!.GetValue<decimal>();
        output["costBasis"] = clip["costBasis"]!.GetValue<string>();
        if (clip["aiGenerated"] is JsonValue ai && ai.TryGetValue(out bool aiGenerated) && aiGenerated)
        {
            output["aiGenerated"] = true;
            if (clip[SubjectOutputFacts.ClipFingerprintKey] is JsonValue subjectsFingerprint)
            {
                output[SubjectOutputFacts.ClipFingerprintKey] = subjectsFingerprint.GetValue<string>();
            }
            else
            {
                output[AvatarOutputFacts.ClipFingerprintKey] = clip[AvatarOutputFacts.ClipFingerprintKey]?.GetValue<string>();
            }
        }

        return output;
    }

    // ---------------------------------------------------------------------
    // Plan-shape carriers
    // ---------------------------------------------------------------------

    /// <summary>
    /// One clip this step ATTEMPTED. <see cref="Request"/> is null for a clip that never reached a
    /// ledger row (skipped past the cap, or a prompt that sanitizes to nothing), in which case
    /// <see cref="PreResolvedClip"/> already holds its finished manifest entry.
    /// </summary>
    private sealed record ClipSlot(
        string ClipId,
        int? ShotIndex,
        int Take,
        ShotPlan? Shot,
        int? DurationSeconds,
        VideoGenerationRequest? Request,
        KeyframeState Keyframe,
        JsonObject? PreResolvedClip,
        string? LookWords = null,
        bool CameraDefaulted = false,
        int? HashOrdinal = null,
        bool Retake = false,
        bool Carried = false,
        // Planned subjects (docs/design/avatar-objects.md): the subjects THIS clip shows when they are its shot's own
        // (a plan-sourced step), and the shot's facts object the delivery records c2pa in. Null: the step-wide
        // DispatchPlan.Avatar (an inline step) or none.
        AvatarInputs? Avatar = null,
        JsonObject? SubjectMeta = null);

    /// <summary>Everything one step attempt needs after its clips have been decided.</summary>
    private sealed record DispatchPlan(
        IReadOnlyList<ClipSlot> Slots,
        JsonObject Meta,
        bool IsPlanPath,
        int? PlanStepOrder,
        int? AnalysisStepOrder,
        int ShotsPlanned,
        ChangeRun? Change = null,
        AvatarInputs? Avatar = null);

    /// <summary>A change request being applied by this attempt: the request row and which clips it redoes.</summary>
    internal sealed record ChangeRun(Guid RequestId, VideoClipChangePlan Plan);

    private static string GeneratedClipKeyPrefix(Guid projectId) => $"projects/{projectId}/agentFiles/generated/";

    private static string ManifestKeyPrefix(Guid projectId) => $"projects/{projectId}/agentFiles/video-analysis/";

    private const long MaxManifestBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Reads the previous step result's clips manifest (its <c>ArtifactStorageKey</c>). The key must lie under this
    /// project's own manifest prefix: a foreign project's key, or any other object, is refused and never read.
    /// </summary>
    private async Task<(IReadOnlyList<JsonObject>? Clips, string? Error)> LoadPreviousClipsAsync(
        Guid projectId, string? storageKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageKey)
            || !storageKey.StartsWith(ManifestKeyPrefix(projectId), StringComparison.Ordinal)
            || storageKey.Contains("..", StringComparison.Ordinal))
        {
            return (null, "The clips of the finished step are not available for this project.");
        }

        string localPath = Path.GetTempFileName();
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(projectId, storageKey, localPath, MaxManifestBytes, ct);
            JsonNode? root = JsonNode.Parse(await File.ReadAllTextAsync(localPath, ct));
            if (root?["clips"] is not JsonArray array)
            {
                return (null, "The clips of the finished step could not be read.");
            }

            return (array.OfType<JsonObject>().Select(c => (JsonObject)c.DeepClone()).ToList(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The previous clips manifest of project {ProjectId} could not be read.", projectId);
            return (null, "The clips of the finished step could not be read.");
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    /// <summary>
    /// A clip the request does not redo: its previous manifest entry, verbatim, with no ledger row, no budget and no
    /// provider call. A delivered clip's storage key must lie under this project's generated-clip prefix.
    /// </summary>
    private static (ClipSlot? Slot, StepExecutionResult? Refusal) TryCarry(
        StepExecutionContext context, ChangeRun changeRun, string clipId, int? shotIndex, int take, ShotPlan? shot, KeyframeState keyframe)
    {
        JsonObject? previous = changeRun.Plan.Previous(clipId);
        if (previous is null)
        {
            return (null, CreateFailedResult(context.Execution.Id, context.Step.StepOrder, "CHANGE_NOT_APPLIED",
                $"The finished step has no clip '{clipId}' to keep; nothing was made.", context.CurrentStepIndex, context.IterationCount));
        }

        if (previous["status"] is JsonValue st && st.TryGetValue(out string? status) && status == StatusSucceeded)
        {
            string? key = previous["storageKey"] is JsonValue k && k.TryGetValue(out string? kv) ? kv : null;
            if (string.IsNullOrEmpty(key)
                || !key.StartsWith(GeneratedClipKeyPrefix(context.Execution.ProjectId), StringComparison.Ordinal)
                || key.Contains("..", StringComparison.Ordinal))
            {
                return (null, CreateFailedResult(context.Execution.Id, context.Step.StepOrder, "CHANGE_NOT_APPLIED",
                    $"The stored clip '{clipId}' is not available for this project; nothing was made.", context.CurrentStepIndex, context.IterationCount));
            }
        }

        // Kept verbatim, but this run paid nothing for it: cost and basis say so (the way audio carries its takes).
        // Nothing sums these for billing: the step's totals are over the clips made this run only.
        JsonObject carried = (JsonObject)previous.DeepClone();
        carried["cost"] = 0m;
        carried["costBasis"] = "carried";
        return (new ClipSlot(clipId, shotIndex, take, shot, null, null, keyframe, carried, Carried: true), null);
    }

    /// <summary>What happened to one clip's requested keyframe.</summary>
    private readonly record struct KeyframeState(bool Requested, bool Applied, string? Reason)
    {
        /// <summary>The shot named no frame: nothing to resolve, nothing to transport.</summary>
        public static KeyframeState NotRequested => new(false, false, null);

        /// <summary>A frame was requested and will not be applied, for <paramref name="reason"/>.</summary>
        public static KeyframeState NotApplied(string reason) => new(true, false, reason);

        /// <summary>
        /// A frame the shot asked for was cut and put on the request this step submitted.
        /// </summary>
        /// <remarks>
        /// This is a claim about the REQUEST, which is the half this executor owns: the bytes are on
        /// <see cref="VideoGenerationRequest.Images"/> when it reaches the resolved client. Transporting
        /// them to the vendor is the client's half, and a clip is never recorded as applied on the
        /// strength of a frame that was not cut.
        /// </remarks>
        public static KeyframeState AppliedTo() => new(true, true, null);
    }

    private const string CostBasisActual = "actual";
    private const string CostBasisEstimate = "estimate";

    /// <summary>
    /// The <c>$0</c> label for a clip that never held a ledger row (skipped past <c>MaxClips</c>,
    /// refused by prompt hygiene) or whose row ended Failed/Cancelled. Kept as one of the three
    /// existing labels so the cost vocabulary stays closed.
    /// </summary>
    private const string CostBasisReleased = "released";

    /// <summary>What the per-take code learned before an exit, so the catch can apply the contract.</summary>
    internal sealed class TakeState
    {
        public ExternalGenerationJob? Job { get; set; }
        public bool SubmitInvoked { get; set; }

        /// <summary>The take check's verdict word (<see cref="SubjectFidelityStatus"/>); null when the check did not apply.</summary>
        public string? Fidelity { get; set; }
    }

    /// <summary>A take's exit: failure code + human detail, or the delivered storage key.</summary>
    internal sealed record TakeOutcome(string? Code, string? Detail, string? StorageKey)
    {
        public static TakeOutcome Failed(string code, string detail) => new(code, detail, null);
        public static TakeOutcome Delivered(string storageKey) => new(null, null, storageKey);
    }

    /// <summary>
    /// The one media session this plan-path attempt cuts its frames in, opened on the first shot that
    /// actually needs one and held until the attempt ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY one session and not one per frame. The session is the expensive object — on the compute
    /// fabric it is a runner job whose lease is held per execution — and a plan can name a frame per
    /// shot, so a session per frame would churn leases to cut a few hundred kilobytes. It is opened
    /// lazily for the mirror-image reason: a plan that names no frame, or whose keyframe-requiring
    /// clips were all trimmed away, opens nothing.
    /// </para>
    /// <para>
    /// The job type is <see cref="RunnerJobType.VideoAnalyzeExtract"/> and the work is runner-ELIGIBLE,
    /// because the ops this session runs (stage an object, seek one frame with ffmpeg, read the small
    /// result back) are exactly the <c>keyframes</c> arm of the analyze-extract job type that the
    /// worker's argv validator is already written against. The frame is decoded where the media
    /// already is and only the encoded frame crosses back, which is the only way the engine — the one
    /// party that can put bytes on a provider request — can get it.
    /// </para>
    /// </remarks>
    private sealed class KeyframeMediaSession(
        IMediaWorkspaceFactory mediaWorkspaces,
        StepExecutionContext context,
        ILogger logger) : IAsyncDisposable
    {
        private MediaWorkspaceLease? _lease;

        /// <summary>Why the session could not be opened, when it could not.</summary>
        public string? RefusalCode { get; private set; }

        /// <inheritdoc cref="RefusalCode"/>
        public string? RefusalMessage { get; private set; }

        /// <summary>The session's workspace, or null when it could not be opened (see <see cref="RefusalCode"/>).</summary>
        public async Task<IMediaWorkspace?> WorkspaceAsync(CancellationToken ct)
        {
            if (_lease is not null)
            {
                return _lease.Workspace;
            }

            MediaWorkspaceOpenResult opened = await mediaWorkspaces.OpenAsync(
                new MediaWorkspaceRequest(
                    ExecutionId: context.Execution.Id,
                    OrganizationId: context.Execution.OrganizationId,
                    ProjectId: context.Execution.ProjectId,
                    StepId: context.Step.Id,
                    StepOrder: context.Step.StepOrder,
                    JobType: RunnerJobType.VideoAnalyzeExtract,
                    Purpose: $"keyframe.transport:{context.Step.StepOrder}",
                    Iteration: context.IterationCount,
                    RunnerEligible: true),
                ct).ConfigureAwait(false);

            if (opened.Refused || opened.Lease is null)
            {
                RefusalCode = opened.FailureCode;
                RefusalMessage = opened.Message;
                return null;
            }

            _lease = opened.Lease;
            logger.LogInformation(
                "VideoGenerate step {StepOrder}: cutting the plan's frames on the {Target} media session.",
                context.Step.StepOrder, opened.Lease.Decision.Target);
            return _lease.Workspace;
        }

        public async ValueTask DisposeAsync()
        {
            if (_lease is not null)
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<TakeOutcome> ProcessClipAsync(
        IVideoGenerationClient client,
        VideoGenerationPricingBasis pricing,
        VideoGenerateStepConfig config,
        VideoGenerationRequest request,
        WorkflowExecution execution,
        WorkflowStep step,
        int clipIndex,
        ReservedTake reservedTake,
        WorkflowEngineDbContext dbContext,
        SemaphoreSlim providerSlot,
        TakeState state,
        CancellationToken ct,
        ResolvedVideoGenerationProvider meteredProvider,
        AvatarInputs? avatar = null,
        JsonObject? stepMeta = null)
    {
        var clipId = reservedTake.ClipId;

        // The budget gate already reserved (or found) this take's ledger row; load it by id.
        var job = await dbContext.Set<ExternalGenerationJob>()
            .FirstOrDefaultAsync(j => j.Id == reservedTake.JobId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Reserved job {reservedTake.JobId} for {clipId} not found.");
        state.Job = job;

        ExternalJobHandle? remoteHandle;

        if (!string.IsNullOrEmpty(job.RemoteJobId))
        {
            // A prior attempt got a job id from the vendor (Submitted or Running): poll only.
            _logger.LogInformation(
                "Crash-resume detected for execution {ExecutionId}: polling existing job {RemoteJobId}",
                execution.Id, job.RemoteJobId);
            remoteHandle = new ExternalJobHandle(job.RemoteJobId);

            if (!await TryAcquireSlotAsync(providerSlot, ct))
            {
                await ApplyTerminalAsync(dbContext, job, null);
                return TakeOutcome.Failed("POLL_ERROR",
                    $"The step was stopped while job {remoteHandle.RemoteJobId} waited for a provider slot; it stays charged and is polled again on retry.");
            }
        }
        else if (reservedTake.Existing)
        {
            // Reserved by a prior attempt that may have reached the vendor before recording the
            // job id. Re-submitting could pay twice, so leave the row exactly as it is.
            _logger.LogWarning(
                "Orphaned reservation {JobId} ({ClipId}) for execution {ExecutionId}; not re-submitting",
                job.Id, clipId, execution.Id);
            return TakeOutcome.Failed("RESERVATION_ORPHANED",
                $"A prior attempt of this step reserved this take (ledger row {job.Id}, CreatedAt {job.CreatedAt:O} UTC) " +
                "and may have reached the vendor without recording a job id. It was not re-submitted, to avoid double billing. " +
                "Check the provider console for a task created near the row's CreatedAt. The reservation stays charged " +
                "against the budget until the UTC day rolls over.");
        }
        else
        {
            if (ct.IsCancellationRequested)
            {
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
                return TakeOutcome.Failed("NOT_SUBMITTED", "The step was stopped before this take was submitted; its reservation was released.");
            }

            if (!await TryAcquireSlotAsync(providerSlot, ct))
            {
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
                return TakeOutcome.Failed("NOT_SUBMITTED",
                    "The step was stopped while this take waited for a provider slot, before it was submitted; its reservation was released.");
            }

            remoteHandle = null;
        }

        // The provider slot is held from here (before SubmitAsync, or before the first PollAsync
        // of a resumed take) until polling ends, and released on every exit — before delivery,
        // so a slow download/normalize/upload never blocks another take's submit.
        ExternalJobStatus succeededStatus;
        try
        {
            var polled = await SubmitAndPollAsync(client, config, request, job, remoteHandle, dbContext, state, ct, avatar, execution.ProjectId);
            if (polled.Exit is { } exit)
            {
                return exit;
            }

            succeededStatus = polled.Succeeded!;
        }
        finally
        {
            providerSlot.Release();
        }

        return await DeliverAsync(client, pricing, config, execution, step, clipIndex, job, succeededStatus, ct);

        async Task<TakeOutcome> DeliverAsync(
            IVideoGenerationClient c, VideoGenerationPricingBasis basis, VideoGenerateStepConfig cfg,
            WorkflowExecution exec, WorkflowStep stp, int index, ExternalGenerationJob row,
            ExternalJobStatus succeeded, CancellationToken token)
        {
            // Record the vendor-metered cost before anything else can fail.
            row.ActualCost = ComputeActualCost(basis, cfg.Resolution, succeeded.UsageTotalSeconds, IsGpuBudgeted(meteredProvider));
            if (!await ApplyTerminalAsync(dbContext, row, null))
            {
                // A newer attempt changed the row (abandoned it) while this one polled: the take is no longer this
                // attempt's to deliver, record or bill, and a delivery would overwrite the newer attempt's StorageKey.
                return TakeOutcome.Failed("RESERVATION_ORPHANED",
                    $"Ledger row {row.Id} was taken over by a newer attempt of this run (now {row.Status}) while this attempt polled it; "
                    + "this attempt did not deliver it.");
            }

            await RecordVideoGenUsageAsync(meteredProvider, request, row, succeeded, basis, LipSyncStepResolution.CapabilityFor(cfg));
            if (avatar is not null)
            {
                await RecordInputMaterialUsageAsync(meteredProvider, request, row, avatar, LipSyncStepResolution.CapabilityFor(cfg));
            }

            if (string.IsNullOrEmpty(succeeded.DownloadUrl))
            {
                return TakeOutcome.Failed("NO_DOWNLOAD_URL",
                    "The provider reported success but returned no download URL; the clip is billed but was not delivered.");
            }

            string storageKey;
            var tempPath = Path.GetTempFileName();
            var normalizedPath = Path.GetTempFileName();
            try
            {
                await using (var stream = await c.DownloadAsync(succeeded.DownloadUrl, token))
                await using (var fileStream = File.Create(tempPath))
                {
                    await stream.CopyToAsync(fileStream, token);
                }

                // Avatars (AV-9): every clip of a person is marked machine-readably; the avatar's Visible label also
                // burns "AI-generated" into the picture. A clip that shows only an object gets the neutral marking and
                // no label. A clip without an avatar is normalized exactly as before.
                bool showsPerson = avatar is { SubjectKind: AvatarSubjectKind.Person };
                await _normalizer.NormalizeAsync(
                    tempPath, normalizedPath, token,
                    GeneratedClipConformTarget.FromConfig(cfg.ConformFps, cfg.ConformHeight),
                    avatar is null ? null
                    : showsPerson ? new GeneratedClipDisclosure(avatar.Disclosure == AvatarDisclosure.Visible)
                    : GeneratedClipDisclosure.Neutral);

                // The take check (docs/design/avatar-objects.md "Object fidelity"): one Vision call, fail open.
                state.Fidelity = await CheckObjectFidelityAsync(avatar, normalizedPath, token);

                if (avatar is not null && stepMeta?[avatar.FactsKey] is JsonObject avatarMeta)
                {
                    ProvenanceOutcome signed = await _provenance.SignAsync(
                        normalizedPath, new GeneratedClipProvenanceClaim("ReelBolt", request.Model, ShowsConsentedLikeness: showsPerson), token);
                    avatarMeta["c2pa"] = signed switch
                    {
                        ProvenanceOutcome.Signed => "signed",
                        ProvenanceOutcome.Failed => "failed",
                        _ => "not_available",
                    };
                }

                var fileName = $"generated/{exec.Id:D}/step-{stp.StepOrder}-v{index}.mp4";
                storageKey = await _workspace.UploadArtifactAsync(
                    exec.ProjectId, normalizedPath, fileName, "video/mp4", token, "agentFiles");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delivery of job {RemoteJobId} failed", row.RemoteJobId);
                return TakeOutcome.Failed("DELIVERY_FAILED",
                    "The provider finished the clip but download/normalize/upload failed.");
            }
            finally
            {
                TryDelete(tempPath);
                TryDelete(normalizedPath);
            }

            row.StorageKey = storageKey;
            if (!await ApplyTerminalAsync(dbContext, row, ExternalGenerationJobStatus.Succeeded))
            {
                return TakeOutcome.Failed("RESERVATION_ORPHANED",
                    $"Ledger row {row.Id} was taken over by a newer attempt of this run (now {row.Status}) while this attempt delivered it; "
                    + "its result was not recorded.");
            }

            return TakeOutcome.Delivered(storageKey);
        }
    }

    /// <summary>
    /// The take check (docs/design/avatar-objects.md "Object fidelity"): for a clip that shows only OBJECTS, one Vision call
    /// over the objects' pictures, their facts and a frame of the normalized take. Null when it does not apply (no check
    /// registered, a person among the subjects, no object); otherwise a <see cref="SubjectFidelityStatus"/> word.
    /// Never throws and never fails the step.
    /// </summary>
    private async Task<string?> CheckObjectFidelityAsync(AvatarInputs? avatar, string clipPath, CancellationToken ct)
    {
        if (_fidelity is null || avatar is null || avatar.ShowsPerson || avatar.AnimatesStill) return null;
        if (!avatar.Resolved.Any(r => r.SubjectKind == AvatarSubjectKind.Object)) return null;

        List<SubjectFidelityObject> checkedObjects = SubjectFidelityInputs.Build(
            avatar.Resolved, avatar.References, avatar.SceneSubjects);

        try
        {
            SubjectFidelityResult result = await _fidelity.CheckAsync(new SubjectFidelityRequest(clipPath, checkedObjects), ct);
            return result.Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The subject fidelity check failed; the take is kept unchecked");
            return SubjectFidelityStatus.Unavailable;
        }
    }

    /// <summary>
    /// Submits a fresh take (when <paramref name="remoteHandle"/> is null) and polls it until the
    /// vendor reaches a terminal state or TimeoutSeconds elapses. Runs with the provider slot held.
    /// Returns either an exit outcome (row already persisted per the contract) or the vendor's
    /// Succeeded status for delivery, which happens after the slot is released.
    /// </summary>
    private async Task<(TakeOutcome? Exit, ExternalJobStatus? Succeeded)> SubmitAndPollAsync(
        IVideoGenerationClient client,
        VideoGenerateStepConfig config,
        VideoGenerationRequest request,
        ExternalGenerationJob job,
        ExternalJobHandle? remoteHandle,
        WorkflowEngineDbContext dbContext,
        TakeState state,
        CancellationToken ct,
        AvatarInputs? avatar = null,
        Guid projectId = default)
    {
        static (TakeOutcome?, ExternalJobStatus?) Exit(TakeOutcome outcome) => (outcome, null);

        if (remoteHandle is null)
        {
            ExternalJobHandle? jobHandle;
            // Re-read the row right before paying: a newer attempt may have abandoned it while this one waited.
            var current = await dbContext.Set<ExternalGenerationJob>().AsNoTracking()
                .Where(j => j.Id == job.Id)
                .Select(j => (ExternalGenerationJobStatus?)j.Status)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (current != ExternalGenerationJobStatus.Reserved)
            {
                _logger.LogWarning(
                    "Ledger row {JobId} is {Status}, no longer Reserved, before submit; not submitting", job.Id, current);
                return Exit(TakeOutcome.Failed("RESERVATION_ORPHANED",
                    $"Ledger row {job.Id} was taken over by a newer attempt of this run (now {current}); this attempt did not submit it."));
            }

            // SQFU-S1: last check before paying. A run handed to another instance never submits; nothing was sent,
            // so the reservation is released.
            if (_leases is not null && !await _leases.TryHoldAsync(job.ExecutionId, CancellationToken.None))
            {
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
                return Exit(TakeOutcome.Failed("NOT_SUBMITTED",
                    Services.ExecutionLeaseService.LeaseLostReason + " This take was not submitted; its reservation was released."));
            }

            // Avatars: consent is re-checked right before EACH take is paid for, as the lease is. A revoke, an expiry
            // or a changed reference stops this take; nothing was sent, so its reservation is released.
            if (avatar is not null && await RecheckAvatarAsync(projectId, avatar, ct) is { } refused)
            {
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
                return Exit(refused);
            }

            try
            {
                state.SubmitInvoked = true;
                // The ledger row id is the durable idempotency token: a retry that re-submits this
                // same reserved take after a crash presents the same key (Higgsfield's documented
                // Idempotency-Key header; ComfyUI's caller-chosen prompt_id). MiniMax ignores it.
                jobHandle = await client.SubmitAsync(request with { IdempotencyKey = job.Id.ToString("D") }, ct);
            }
            catch (ReferenceAudioFormatException ex)
            {
                // Refused by our own client before any request: nothing reached the vendor, so the reservation is
                // released and the customer is told what to fix (a UserInput failure of the avatar).
                _logger.LogWarning(ex, "A reference recording of the avatar is not WAV or MP3; take {JobId} not submitted", job.Id);
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
                return Exit(TakeOutcome.Failed("AVATAR_REFERENCE_AUDIO_FORMAT", ReferenceAudioFormat.ReuploadMessage));
            }
            catch (HttpRequestException ex) when (IsDefiniteRejection(ex.StatusCode))
            {
                // A 4xx (other than 408) means the vendor refused the task: nothing was billed.
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Failed);
                var status = (int)ex.StatusCode!.Value;
                var code = status switch
                {
                    402 => "PROVIDER_INSUFFICIENT_BALANCE",
                    401 or 403 => "PROVIDER_AUTH_FAILED",
                    _ => "PROVIDER_REJECTED"
                };
                _logger.LogWarning(ex, "Video generation submission rejected (HTTP {Status}) for job {JobId}", status, job.Id);
                return Exit(TakeOutcome.Failed(code, $"The provider rejected the submission (HTTP {status})."));
            }
            catch (Exception ex)
            {
                // 5xx, 408, network, malformed 2xx, cancellation: the vendor may have accepted it.
                _logger.LogWarning(ex, "Video generation submission outcome unknown for job {JobId}", job.Id);
                await ApplyTerminalAsync(dbContext, job, null);
                return Exit(TakeOutcome.Failed("SUBMIT_OUTCOME_UNKNOWN",
                    "Submission outcome unknown; the reservation stays charged."));
            }

            if (jobHandle is null || string.IsNullOrEmpty(jobHandle.RemoteJobId))
            {
                await ApplyTerminalAsync(dbContext, job, null);
                return Exit(TakeOutcome.Failed("SUBMIT_OUTCOME_UNKNOWN",
                    "The provider accepted the submission but returned no job id; the reservation stays charged."));
            }

            remoteHandle = jobHandle;
            job.RemoteJobId = jobHandle.RemoteJobId;
            job.Status = ExternalGenerationJobStatus.Submitted;
            job.UpdatedAt = DateTime.UtcNow;
            if (!await TrySaveJobAsync(dbContext, job))
            {
                _logger.LogWarning(
                    "Vendor job {RemoteJobId} was accepted for ledger row {JobId}, which a newer attempt took over; not polling it",
                    jobHandle.RemoteJobId, job.Id);
                return Exit(TakeOutcome.Failed("RESERVATION_ORPHANED",
                    $"Ledger row {job.Id} was taken over by a newer attempt of this run while this attempt submitted it " +
                    $"(vendor job {jobHandle.RemoteJobId}); the result was not recorded here."));
            }
        }

        // Poll for completion. The TimeoutSeconds clock starts only now, with the slot held: time spent
        // waiting for a slot is not time the vendor spent on this job.
        var startTime = _timeProvider.GetUtcNow();
        var timeoutSec = config.TimeoutSeconds;

        while ((_timeProvider.GetUtcNow() - startTime).TotalSeconds < timeoutSec)
        {
            ExternalJobStatus status;
            try
            {
                status = await client.PollAsync(remoteHandle, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling video generation job {RemoteJobId} failed", remoteHandle.RemoteJobId);
                await ApplyTerminalAsync(dbContext, job, null);
                return Exit(TakeOutcome.Failed("POLL_ERROR",
                    $"Polling job {remoteHandle.RemoteJobId} failed; it stays charged and is polled again on retry."));
            }

            switch (status.State)
            {
                case ExternalJobState.Succeeded:
                    return (null, status);

                case ExternalJobState.Failed:
                    {
                        var reason = status.FailureReason ?? "Unknown error";
                        // Vendor prose reaches step output (and so later agent prompts): bound it.
                        if (reason.Length > 300) reason = reason[..300] + "…";
                        await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Failed);
                        _logger.LogWarning(
                            "Vendor job {RemoteJobId} for ledger row {JobId} failed. Reason={Reason}",
                            remoteHandle.RemoteJobId, job.Id, reason);
                        var code = reason.Contains("402") || reason.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                            ? "PROVIDER_INSUFFICIENT_BALANCE"
                            : "PROVIDER_FAILED";
                        return Exit(TakeOutcome.Failed(code, $"The provider reported the job failed: {reason}"));
                    }

                case ExternalJobState.Cancelled:
                    await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
                    return Exit(TakeOutcome.Failed("PROVIDER_CANCELLED", "The provider reported the job cancelled."));
            }

            // Still running - wait a bit
            try
            {
                await Task.Delay(Math.Max(100, config.PollIntervalSeconds * 1000), ct);
            }
            catch (OperationCanceledException)
            {
                await ApplyTerminalAsync(dbContext, job, null);
                return Exit(TakeOutcome.Failed("POLL_ERROR",
                    $"The step was stopped while job {remoteHandle.RemoteJobId} was running; it stays charged and is polled again on retry."));
            }
        }

        await ApplyTerminalAsync(dbContext, job, null);
        return Exit(TakeOutcome.Failed("POLL_TIMEOUT",
            $"Job {remoteHandle.RemoteJobId} did not finish within {timeoutSec}s; it stays charged and is polled again on retry."));
    }

    /// <summary>
    /// Waits for a provider slot with the step's token; false if the step was stopped first.
    /// </summary>
    private static async Task<bool> TryAcquireSlotAsync(SemaphoreSlim slot, CancellationToken ct)
    {
        try
        {
            await slot.WaitAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The per-take catch: an exception outside the mapped paths still leaves the row per the
    /// contract, judged by whether SubmitAsync was invoked and whether a RemoteJobId is known.
    /// </summary>
    internal async Task<TakeOutcome> HandleUnexpectedAsync(
        Exception ex, ReservedTake reservedTake, TakeState state, WorkflowEngineDbContext dbContext)
    {
        var job = state.Job;
        _logger.LogError(ex, "Unexpected error in a video generation take");
        const string detail = "Unexpected error while generating this clip.";
        if (job is null)
        {
            return TakeOutcome.Failed("UNEXPECTED_ERROR", detail + " The ledger row could not be loaded; it stays charged.");
        }

        // Abandoned is terminal too: a newer attempt owns the take, and releasing the row would drop it from every cap.
        if (job.Status is ExternalGenerationJobStatus.Succeeded or ExternalGenerationJobStatus.Failed or ExternalGenerationJobStatus.Cancelled
            or ExternalGenerationJobStatus.Abandoned)
        {
            return TakeOutcome.Failed("UNEXPECTED_ERROR", detail);
        }

        try
        {
            if (!state.SubmitInvoked && string.IsNullOrEmpty(job.RemoteJobId) && !reservedTake.Existing)
            {
                // Never reached the vendor: release the reservation.
                await ApplyTerminalAsync(dbContext, job, ExternalGenerationJobStatus.Cancelled);
            }
            else
            {
                // Possibly billed (Reserved after an invoked submit, or Submitted with a job id): keep it charged.
                await ApplyTerminalAsync(dbContext, job, null);
            }
        }
        catch (Exception saveEx)
        {
            _logger.LogError(saveEx, "Could not persist ledger status for job {JobId}", job.Id);
        }

        return TakeOutcome.Failed("UNEXPECTED_ERROR", detail);
    }

    /// <summary>
    /// Persists a take's exit: optionally a new status (never Running), always a fresh UpdatedAt,
    /// always with <see cref="CancellationToken.None"/> so a stopped step still records it. Returns false when the
    /// write was refused because another writer changed the row first (see <see cref="TrySaveJobAsync"/>).
    /// </summary>
    private async Task<bool> ApplyTerminalAsync(
        WorkflowEngineDbContext dbContext, ExternalGenerationJob job, ExternalGenerationJobStatus? status)
    {
        if (status is { } s)
        {
            job.Status = s;
        }

        job.UpdatedAt = DateTime.UtcNow;
        return await TrySaveJobAsync(dbContext, job);
    }

    /// <summary>
    /// Saves <paramref name="job"/> only if its status is still the one this attempt read
    /// (<see cref="ExternalGenerationJob.Status"/> is a concurrency token). When another writer changed it first (a newer
    /// attempt abandoned the row while this one, lease lost, kept running), the lost update is logged and never applied:
    /// the row is reloaded as it is. Returns whether the write was applied.
    /// </summary>
    private async Task<bool> TrySaveJobAsync(WorkflowEngineDbContext dbContext, ExternalGenerationJob job)
    {
        try
        {
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            var entry = dbContext.Entry(job);
            var attempted = job.Status;
            await entry.ReloadAsync(CancellationToken.None);
            _logger.LogWarning(
                "Ledger row {JobId} changed under this attempt (now {Status}); its write ({Attempted}) was refused, not applied",
                job.Id, job.Status, attempted);
            return false;
        }
    }

    private static bool IsDefiniteRejection(System.Net.HttpStatusCode? statusCode) =>
        statusCode is { } code && (int)code >= 400 && (int)code < 500 && (int)code != 408;

    /// <summary>
    /// The credit-side record of a settled take (B3c): <see cref="UsageKind.VideoGenSeconds"/>, linked to the
    /// ledger row by key <c>videogen:{job.Id}</c>, so a replayed settle (a retry that re-polls the same job) is a
    /// no-op. The credit-denominated spend ledger (<c>external_generation_jobs</c>, the budget gate) is untouched and stays the
    /// spend-side record; this row is what the credit gate prices, so the two never double count. The quantity is
    /// the platform-side job spec (the take's requested seconds), not the vendor's reported usage, which stays in
    /// <c>ActualCost</c>; a vendor that reports exactly zero usage billed nothing, so nothing is charged.
    /// <para>
    /// The price variant is the resolution the take was requested at (docs/design/model-catalog.md §2.2). An
    /// oracle-priced provider (Higgsfield) has no per-resolution price row: the take is charged from the
    /// vendor's own quote for it, converted at the platform markup, with no price id.
    /// </para>
    /// </summary>
    private async Task RecordVideoGenUsageAsync(
        ResolvedVideoGenerationProvider? provider, VideoGenerationRequest request, ExternalGenerationJob job,
        ExternalJobStatus succeeded, VideoGenerationPricingBasis basis, InferenceProviderCapability capability)
    {
        if (_usageRecorder is null || provider is null || succeeded.UsageTotalSeconds == 0) return;

        try
        {
            decimal? quotedUsd = basis.Quote is { } quote
                ? request.DurationSeconds == quote.DurationSeconds
                    ? quote.TotalUsd
                    : quote.PricePerSecondUsd * request.DurationSeconds
                : null;
            await _usageRecorder.RecordAsync(new UsageRecordRequest(
                UsageKind.VideoGenSeconds, request.DurationSeconds, provider.ProviderId, provider.Kind,
                string.IsNullOrWhiteSpace(request.Model) ? provider.ModelName : request.Model,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                IdempotencyKey: $"videogen:{job.Id:N}",
                Capability: capability,
                Variant: string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution,
                AccountId: provider.AccountId,
                QuotedCostUsd: quotedUsd), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metering video generation job {JobId} failed", job.Id);
        }
    }

    /// <summary>
    /// Vendor-metered cost in the ledger's unit: usage seconds x the model's per-second price for the
    /// resolution, converted to credits for a priced vendor; 0 for zero usage; null when the vendor
    /// did not report usage (or reported nonsense). GPU-seconds pass through unconverted.
    /// </summary>
    /// <remarks>
    /// On the rate-derived arm this is unchanged. On the ORACLE arm there is no per-second rate to
    /// multiply — the vendor quoted one total for one duration — so <paramref name="usageTotalSeconds"/>
    /// is honoured only when it equals the quoted duration, in which case the actual is the quote's
    /// own total. Any other reported usage leaves the actual NULL, so the exact quote stands as the
    /// charged estimate rather than re-deriving a figure from a rate the vendor never quoted. The
    /// <c>costBasis</c> label set is untouched: an oracle-priced clip keeps <c>estimate</c>.
    /// </remarks>
    private static decimal? ComputeActualCost(
        VideoGenerationPricingBasis pricing, string resolution, int? usageTotalSeconds, bool gpuBudgeted)
    {
        decimal? unitFigure = usageTotalSeconds is not > 0
            ? usageTotalSeconds switch
            {
                0 => 0m,
                _ => null
            }
            : pricing.Quote is { } quote
                ? usageTotalSeconds.Value == quote.DurationSeconds ? quote.TotalUsd : null
                : VideoGenerationPricing.EstimateClipCostUsd(
                    pricing.Capabilities, resolution, usageTotalSeconds.Value);

        // The pricing basis returns the vendor's own USD figure for a priced kind; the ledger column
        // is unit-neutral, so the conversion to credits happens here, exactly like the reservation.
        return unitFigure is null || gpuBudgeted ? unitFigure : CreditCurrency.CreditsFromUsd(unitFigure.Value);
    }

    /// <summary>
    /// A reserved clip's cost label: <c>released</c> (row Failed/Cancelled, $0),
    /// <c>actual</c> (vendor-metered cost known), else <c>estimate</c> (charged, reserved estimate).
    /// </summary>
    private static (string Basis, decimal Cost) ResolveCostLabel(ExternalGenerationJob? job, ReservedTake reservedTake)
    {
        if (job?.Status is ExternalGenerationJobStatus.Failed or ExternalGenerationJobStatus.Cancelled)
        {
            return (CostBasisReleased, 0m);
        }

        if (job?.ActualCost is { } actual)
        {
            return (CostBasisActual, actual);
        }

        return (CostBasisEstimate, job?.CostEstimate ?? reservedTake.CostEstimate);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // best effort scratch cleanup
        }
    }

    private static StepExecutionResult CreateFailedResult(
        Guid executionId, int stepOrder, string failureCode, string message,
        int currentStepIndex, int iterationCount, bool userFacing = false)
    {
        // A failure the person causes (bad config, no avatar, speech too short, a model the provider lacks) fails the same
        // way on every attempt: stop at once instead of burning the remaining attempts. Provider and platform failures
        // (poll errors, delivery, unexpected) keep retrying.
        bool deterministic = FailureClassifier.ForCode(failureCode) is FailureClass.UserInput or FailureClass.EntitlementDenied;
        return new StepExecutionResult
        {
            Status = StepStatus.Failed,
            Output = CreateErrorOutput(executionId, stepOrder, failureCode, message),
            ErrorDetails = message,
            NextStepIndex = currentStepIndex + 1,
            NewIterationCount = iterationCount,
            Retryable = !deterministic,
            // Only a sentence written for the person (the avatar refusals) reaches the run page verbatim.
            UserMessage = deterministic && userFacing ? message : null
        };
    }

    /// <summary>What a tenant reads for an unexpected step failure (S-13): never the exception's own text.</summary>
    private const string UnexpectedStepSentence = "The video generation step failed unexpectedly; the details are in the server log.";

    private static string CreateErrorOutput(
        Guid executionId, int stepOrder, string failureCode, string message)
    {
        var output = new JsonObject
        {
            { "clips", new JsonArray() },
            {
                "meta", new JsonObject
                {
                    { "failed", failureCode },
                    { "error", message }
                }
            }
        };

        return output.ToJsonString(OutputJsonOptions);
    }

    private sealed record ReusableClip(Guid JobId, string StorageKey);

    /// <summary>
    /// Finds, per request hash, a previously delivered clip for the identical request: same project
    /// and provider, ledger row <c>Succeeded</c> with a stored object that still exists. This
    /// execution's own rows win (a ReviewLoop loop-back, or a retry — which re-queues the same
    /// execution); otherwise the most recent earlier execution's (a re-run). Any lookup failure
    /// means "not reusable": the budget gate, not this method, is what bounds a fresh purchase.
    /// </summary>
    private async Task<Dictionary<string, ReusableClip>> FindReusableClipsAsync(
        WorkflowExecution execution,
        ResolvedVideoGenerationProvider provider,
        IReadOnlyList<VideoGenerationTakeRequest> takeRequests,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ReusableClip>(StringComparer.Ordinal);
        if (takeRequests.Count == 0)
        {
            return result;
        }

        try
        {
            var hashes = takeRequests.Select(t => t.RequestHash).Distinct().ToList();
            var providerId = provider.ProviderId ?? Guid.Empty;
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            // A clip's hash does not cover its input frames, so a project with a quarantined file reuses only clips of
            // runs that began after its marker.
            DateTime? marker = await ProjectQuarantineState.ReadMarkerAsync(db, execution.ProjectId, ct);
            DateTime? madeAfter = marker + ProjectQuarantineState.ClockSkewMargin;

            var rows = await db.Set<ExternalGenerationJob>()
                .Where(j => (madeAfter == null
                        || (j.CreatedAt > madeAfter
                            && db.WorkflowExecutions.Any(e => e.Id == j.ExecutionId && e.FirstStartedAt > madeAfter)))
                    && j.ProjectId == execution.ProjectId
                    && j.ProviderId == providerId
                    && hashes.Contains(j.RequestHash)
                    && j.Status == ExternalGenerationJobStatus.Succeeded
                    && j.StorageKey != null)
                .ToListAsync(ct);

            foreach (var group in rows.GroupBy(r => r.RequestHash))
            {
                foreach (var row in group
                    .OrderByDescending(r => r.ExecutionId == execution.Id)
                    .ThenByDescending(r => r.UpdatedAt))
                {
                    if (await _workspace.ArtifactExistsAsync(execution.ProjectId, row.StorageKey!, ct))
                    {
                        result[group.Key] = new ReusableClip(row.Id, row.StorageKey!);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not look up reusable video clips for execution {ExecutionId}; generating fresh.", execution.Id);
            result.Clear();
        }

        return result;
    }

    private static string ComputeRequestHash(string prompt, VideoGenerateStepConfig config, int clipIndex, Guid? retakeSalt = null, string? avatarKey = null)
    {
        // A retake carries its change request's id, so it can never match an earlier row: a fresh ledger row and a
        // fresh idempotency key even when the prompt is unchanged. Without a salt the material is byte-identical to before.
        var material = retakeSalt is Guid salt
            ? $"{config.Model}|{config.Resolution}|{config.Ratio}|{config.DurationSeconds}|{clipIndex}|{prompt}|retake:{salt:N}"
            : $"{config.Model}|{config.Resolution}|{config.Ratio}|{config.DurationSeconds}|{clipIndex}|{prompt}";
        // Avatars: the avatar as it is now (consent time, reference digests, the references sent) joins the hash only
        // when set, so a clip of a person is reused from the ledger only while that avatar is unchanged.
        if (avatarKey is not null)
        {
            material += $"|avatar:{avatarKey}";
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }
}
