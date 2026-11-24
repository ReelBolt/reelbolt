using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

// The two optional pipelines of an avatar build (docs/design/avatar-objects.md): making a variant's videos again, and
// pulling an object out of a video. Both run hidden AvatarBuild workflows in the engine (AvatarBuildProcessor.Videos).
public sealed partial class AvatarBuildService
{
    /// <summary>The most moments an extraction looks at (the design's figure).</summary>
    public const int MaxExtractionMoments = 6;

    /// <summary>The length a take gets when the original video's length is not known.</summary>
    public const int DefaultTakeSeconds = 6;

    /// <summary>The resolution a take is made at when the model offers it (the step's own default).</summary>
    public const string PreferredResolution = "768P";

    /// <summary>
    /// The video part of a variant (stage 2): the plan feature <c>video_generation</c>, then the default
    /// <c>VideoGeneration</c> model, which must start from a picture (<c>409 VARIANT_VIDEO_MODEL_MISSING</c>), each
    /// video's take shaped for it (the original's length clamped to the model, the nearest frame shape), and the most
    /// the takes may cost (the estimate's "up to" part, and each take's own spend cap).
    /// </summary>
    private async Task<(long CeilingMillis, AvatarBuildVideoModel? Model, AvatarRefusal? Refusal)> PlanVideosAsync(
        Project project, IReadOnlyList<ProjectAvatarReference> videos, CancellationToken ct)
    {
        if (_entitlements is not null)
        {
            OrganizationEntitlementSet set = await _entitlements.GetAsync(project.OrganizationId, ct);
            if (PlanGate.CheckStep(StepType.VideoGenerate, set) is { } violation) return (0, null, FromPlan(violation));
        }

        if (_providers is null || _videoClients is null) return (0, null, VideoModelMissing(null));

        ResolvedVideoGenerationProvider? provider;
        try
        {
            provider = await _providers.ResolveVideoGenerationAsync(null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolving the video model for an avatar variant failed");
            provider = null;
        }

        string model = provider?.ModelName?.Trim() ?? string.Empty;
        if (provider is null || model.Length == 0 || !VideoGenerationImageSupport.CanAcceptInputImages(provider.Kind))
            return (0, null, VideoModelMissing(null));

        IVideoGenerationClient client;
        VideoGenerationCapabilities capabilities;
        try
        {
            client = _videoClients.Get(provider);
            if (client.DeclaredImageInputSupport(model) == VideoImageInputSupport.None) return (0, null, VideoModelMissing(model));
            capabilities = client.Describe(model);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _logger.LogInformation(ex, "The default video model {Model} cannot make a variant's videos here", model);
            return (0, null, VideoModelMissing(model));
        }

        if (capabilities.PricePerSecondByResolution.Count == 0 || capabilities.AllowedRatios.Count == 0)
            return (0, null, VideoModelMissing(model));

        // A self-hosted ComfyUI server bills nothing and is budgeted in GPU minutes: a take carries a credit spend cap
        // only, which such a step refuses (MaxGpuMinutes must be set), so the videos would fail when they run and be left
        // out. Its "prices" are GPU seconds per second, never dollars. Refused before anything is held.
        if (provider.Kind == InferenceProviderKind.ComfyUI)
            return (0, null, VideoModelUnpriced(model, gpuBudgeted: true));

        string resolution = capabilities.PricePerSecondByResolution.ContainsKey(PreferredResolution)
            ? PreferredResolution
            : capabilities.PricePerSecondByResolution.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        decimal pricePerSecondUsd = capabilities.PricePerSecondByResolution[resolution];

        var plan = new AvatarBuildVideoModel { ProviderId = provider.ProviderId, Model = model, Resolution = resolution };
        long ceiling = 0;
        foreach (ProjectAvatarReference video in videos)
        {
            int seconds = TakeSeconds(video.DurationMs, capabilities);
            string ratio = NearestRatio(video.Width, video.Height, capabilities.AllowedRatios);
            // The take's own spend cap is what the budget gate prices it at (the vendor's price in credits), rounded up.
            decimal cap = Math.Max(0.01m, Math.Ceiling(CreditCurrency.CreditsFromUsd(pricePerSecondUsd * seconds) * 100m) / 100m);
            var take = new AvatarBuildVideoTake { ReferenceId = video.Id, DurationSeconds = seconds, Ratio = ratio, MaxSpendCredits = cap };
            plan.Takes.Add(take);

            if (!_pictures.BillingEnabled || _prices is null) continue;
            long? held = await VideoGenerateCreditPricing.TryHoldMillisAsync(
                JsonSerializer.Serialize(TakeConfig(plan, take, null, "price"), VideoGenerateStepConfig.JsonOptions),
                _providers, _videoClients, _prices, ct);
            // A take this deployment cannot price is never free: the videos are refused, not given away.
            if (held is not { } millis) return (0, null, VideoModelUnpriced(model, gpuBudgeted: false));
            ceiling += millis;
        }

        return (ceiling, plan, null);
    }

    /// <summary>
    /// The step config of one take: an inline <c>VideoGenerate</c> of one clip from the changed first frame, on the model
    /// the request chose, with the take's own spend cap. <c>AllowSourceMediaEgress</c> is set by the server here, only
    /// because the request's media permission was established (the project's consent, or the per-call one).
    /// </summary>
    internal static VideoGenerateStepConfig TakeConfig(AvatarBuildVideoModel plan, AvatarBuildVideoTake take, Guid? firstFrameFileId, string? instruction) =>
        new()
        {
            PlanSource = VideoGeneratePlanSource.Inline,
            InlinePrompt = AvatarVariantInstruction.ForVideo(instruction),
            Model = plan.Model,
            Resolution = plan.Resolution,
            DurationSeconds = take.DurationSeconds,
            Ratio = take.Ratio,
            ProviderId = plan.ProviderId,
            Takes = 1,
            MaxClips = 1,
            MaxSpendCredits = take.MaxSpendCredits,
            AllowSourceMediaEgress = true,
            FirstFrameProjectFileId = firstFrameFileId
        };

    /// <summary>The original's length in whole seconds, clamped to what the model makes (its default when the length is unknown).</summary>
    internal static int TakeSeconds(int? durationMs, VideoGenerationCapabilities capabilities)
    {
        int seconds = durationMs is > 0 ? (int)Math.Round(durationMs.Value / 1000.0, MidpointRounding.AwayFromZero) : DefaultTakeSeconds;
        int min = Math.Max(1, capabilities.MinDurationSeconds);
        int max = Math.Max(min, capabilities.MaxDurationSeconds);
        return Math.Clamp(seconds, min, max);
    }

    /// <summary>The model's frame shape nearest to the video's own (the first one the model lists when the size is unknown).</summary>
    internal static string NearestRatio(int? width, int? height, IReadOnlyList<string> allowed)
    {
        if (width is not > 0 || height is not > 0) return allowed[0];
        double aspect = Math.Log((double)width.Value / height.Value);
        string best = allowed[0];
        double bestDistance = double.MaxValue;
        foreach (string ratio in allowed)
        {
            string[] parts = ratio.Split(':');
            if (parts.Length != 2
                || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)
                || w <= 0 || h <= 0)
                continue;
            double distance = Math.Abs(Math.Log(w / h) - aspect);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = ratio;
            }
        }

        return best;
    }

    private static AvatarRefusal VideoModelMissing(string? model) =>
        AvatarRefusal.Conflict(AvatarBuildCodes.VariantVideoModelMissing,
            model is null
                ? "Making a variant's videos needs a video model that starts from a picture, set up as the default for video generation, and there is none. Leave the videos out, or ask an administrator to add one."
                : $"The default video model \"{model}\" cannot start a video from a picture, which a variant's videos need. Leave the videos out, or ask an administrator to set a model that can.");

    private static AvatarRefusal VideoModelUnpriced(string model, bool gpuBudgeted) =>
        AvatarRefusal.Conflict(AvatarBuildCodes.VariantVideoModelUnpriced,
            gpuBudgeted
                ? $"The default video model \"{model}\" runs on a self-hosted server budgeted in GPU time, not in credits, so a variant's videos cannot be made with it. Leave the videos out, or ask an administrator to set a priced model as the default."
                : $"The default video model \"{model}\" has no price set up here, so a variant's videos cannot be made with it. Leave the videos out, or ask an administrator to price it.");

    // ------------------------------------------------------------------ pulling an object out of a video

    public async Task<AvatarBuildOutcome<ExtractionEstimate>> EstimateExtractionAsync(
        Project project, AvatarBuildActor actor, int? moments, bool mediaAllowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(actor);

        if (!_options.Objects.Enabled) return AvatarBuildOutcome<ExtractionEstimate>.Refused(NotFound());
        if (!actor.EmailVerified) return AvatarBuildOutcome<ExtractionEstimate>.Refused(EmailNotVerified());
        if (await PlanRefusalAsync(project, ct) is { } notEntitled) return AvatarBuildOutcome<ExtractionEstimate>.Refused(notEntitled);
        if (await _checks.VisionRefusalAsync(ct) is { } noVision) return AvatarBuildOutcome<ExtractionEstimate>.Refused(noVision);

        int count = moments is > 0 ? Math.Min(moments.Value, MaxExtractionMoments) : MaxExtractionMoments;
        bool permitted = project.SubjectMediaConsentAt is not null || mediaAllowed;
        ResolvedImageAnalysisProvider? segmenter = permitted ? await SegmenterAsync(ct) : null;
        bool billing = segmenter is not null && _charges is not null && _pictures.BillingEnabled;
        if (!billing)
            return AvatarBuildOutcome<ExtractionEstimate>.Of(new ExtractionEstimate(_pictures.BillingEnabled, segmenter is not null, count, null, null, true));

        long held = await PerStillMillisAsync(segmenter!, ct) * count;
        long? balance = await _charges!.GetAvailableMillisAsync(project.OrganizationId, ct);
        return AvatarBuildOutcome<ExtractionEstimate>.Of(new ExtractionEstimate(
            true,
            true,
            count,
            NonWorkflowChargeResults.ToCredits(held),
            balance is { } available ? NonWorkflowChargeResults.ToCredits(available) : null,
            balance is not { } known || known >= held));
    }

    /// <summary>What showing the segmentation model one still costs (millicredits), the figure an extraction holds per still.</summary>
    private Task<long> PerStillMillisAsync(ResolvedImageAnalysisProvider segmenter, CancellationToken ct) =>
        _charges!.EstimateOneUnitAsync(new PriceRequest(
            segmenter.ProviderId,
            segmenter.AccountId,
            segmenter.Kind,
            segmenter.Capability,
            segmenter.ModelName,
            UsageKind.SegmentationImages,
            null,
            segmenter.IsPlatformManaged ? UsageBillingMode.Platform : UsageBillingMode.Byo,
            1m), ct);

    public async Task<AvatarBuildOutcome<AvatarBuildStarted>> StartExtractionAsync(
        Project project, AvatarBuildActor actor, ObjectExtractionRequest request, bool mediaAllowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        // ---- The objects switch, a verified email, the plan, the object statement. ----
        if (!_options.Objects.Enabled) return Refused(NotFound());
        if (!actor.EmailVerified) return Refused(EmailNotVerified());
        if (await PlanRefusalAsync(project, ct) is { } notEntitled) return Refused(notEntitled);
        if (ObjectAvatarCreator.StatementRefusal(request.ConsentLocale, request.ConsentStatement) is { } statement) return Refused(statement);
        // The person check needs a Vision provider: refused before anything is held, queued or sent.
        if (await _checks.VisionRefusalAsync(ct) is { } noVision) return Refused(noVision);

        // ---- What is asked for. ----
        string objectDescription = request.ObjectDescription?.Trim() ?? string.Empty;
        if (objectDescription.Length == 0 || objectDescription.Length > AvatarBuildJob.MaxObjectDescriptionLength || objectDescription.Any(char.IsControl))
        {
            return Refused(AvatarRefusal.BadRequest("object_description_invalid",
                $"Say which object to take out of the video in at most {AvatarBuildJob.MaxObjectDescriptionLength} characters, for example \"the red kettle\"."));
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > ProjectAvatarLimits.MaxNameLength)
            return Refused(AvatarRefusal.BadRequest("name_invalid", $"Give the avatar a name of at most {ProjectAvatarLimits.MaxNameLength} characters."));
        string description = string.IsNullOrWhiteSpace(request.Description) ? objectDescription : request.Description.Trim();
        if (description.Length > ProjectAvatarLimits.MaxDescriptionLength)
            return Refused(AvatarRefusal.BadRequest("description_too_long", $"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters."));

        List<double>? atSeconds = null;
        if (request.AtSeconds is { Count: > 0 } asked)
        {
            if (asked.Count > MaxExtractionMoments || asked.Any(t => !double.IsFinite(t) || t < 0))
            {
                return Refused(AvatarRefusal.BadRequest("at_seconds_invalid",
                    $"Name at most {MaxExtractionMoments} moments of the video, in seconds from its start."));
            }

            atSeconds = [.. asked.Distinct().Order()];
        }

        // ---- The video: a video of this project that may be read. ----
        if (!TryParseMediaRef(request.MediaRef, out string refKind, out Guid refId))
        {
            return Refused(AvatarRefusal.BadRequest("media_ref_invalid",
                "Name the video as file:<id> (a project file) or step_result:<id> (a render)."));
        }

        OutputSource? source = _describer is null ? null : await _describer.FindSourceAsync(project.Id, refId, ct);
        if (source is null
            || !string.Equals(source.Kind, refKind == "file" ? "file" : "render", StringComparison.Ordinal)
            || !source.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return Refused(AvatarRefusal.Conflict(AvatarBuildCodes.SourceUnavailable,
                "This video cannot be used: it is not a video of this project, or it is on hold."));
        }

        if (atSeconds is not null && source.DurationSec is { } length && atSeconds.Any(t => t > length))
        {
            return Refused(AvatarRefusal.BadRequest("at_seconds_invalid",
                $"Every moment must be inside the video, which lasts {length:0.#} seconds."));
        }

        // ---- The project and its limits (objects are counted apart from people; a running extraction counts). ----
        if (await ObjectAvatarCreator.QuarantineRefusalAsync(_db, project, ct) is { } quarantined) return Refused(quarantined);
        List<AvatarBuildJob> open = await _db.AvatarBuildJobs.AsNoTracking()
            .Where(j => j.ProjectId == project.Id
                        && j.State != AvatarBuildStates.Ready && j.State != AvatarBuildStates.Failed && j.State != AvatarBuildStates.Cancelled)
            .ToListAsync(ct);
        int objects = await _db.ProjectAvatars.CountAsync(
            a => a.ProjectId == project.Id && a.SubjectKind == AvatarSubjectKind.Object && a.VariantOfAvatarId == null, ct);
        if (objects + open.Count(j => j.Kind == AvatarBuildKinds.Extraction) >= ProjectAvatarLimits.MaxObjectsPerProject)
        {
            return Refused(AvatarRefusal.Conflict("AVATAR_LIMIT_REACHED",
                $"A project can hold at most {ProjectAvatarLimits.MaxObjectsPerProject} object avatars. Delete one first."));
        }

        if (await _db.ProjectAvatars.AnyAsync(a => a.ProjectId == project.Id && a.ConsentRevokedAt == null && a.Name == name, ct)
            || open.Any(j => string.Equals(j.Name, name, StringComparison.Ordinal)))
            return Refused(AvatarRefusal.Conflict("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{name}\"."));

        // ---- Finding the object: a segmentation model AND the permission to send the frames, else whole frames. ----
        bool permitted = project.SubjectMediaConsentAt is not null || mediaAllowed;
        ResolvedImageAnalysisProvider? segmenter = permitted ? await SegmenterAsync(ct) : null;
        int moments = atSeconds?.Count ?? MaxExtractionMoments;

        // ---- A video that is still an output's bytes (a render) leaves the host only with a current verdict: the
        // segmentation model is shown its frames, so the publish gate the picture editor applies to an output's bytes
        // applies here too (an upload passes at once). ----
        if (segmenter is not null && await StoredCopyRefusalAsync(project.Id, source.StorageKey, ct) is { } held)
            return Refused(held);

        // ---- The member's words, screened like a prompt: they go to the segmentation model as its text prompt. ----
        if (segmenter is not null && await InstructionRefusalAsync(project, actor, objectDescription, ct) is { } blocked)
            return Refused(blocked);

        // ---- Credits: one hold for every still the segmentation model may be shown. ----
        Guid jobId = Guid.NewGuid();
        ChargeHold? hold = null;
        long perStill = 0;
        if (segmenter is not null && _charges is not null && _pictures.BillingEnabled)
        {
            perStill = await PerStillMillisAsync(segmenter, ct);
            if (perStill * moments > 0)
            {
                hold = await _charges.BeginHoldAsync(
                    project.OrganizationId, UsageSource.Editor, perStill * moments, jobId,
                    new CreditLedgerContext(project.Id, Note: "object from a video"), ct);
                if (!hold.Allowed) return Refused(HoldRefusal(hold, "object extraction"));
            }
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        var parameters = new AvatarBuildParams
        {
            Description = description,
            AtSeconds = atSeconds,
            Segment = segmenter is not null,
            InstructionScreened = segmenter is not null && _moderation is not null,
            HoldMillis = hold?.ReservationId is null ? 0 : perStill * moments,
            PerStillMillis = perStill,
            HeldStills = moments,
            PerPictureMillis = perStill
        };
        var job = new AvatarBuildJob
        {
            Id = jobId,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            RequestedByUserId = actor.UserId,
            Kind = AvatarBuildKinds.Extraction,
            SourceMediaRef = refKind + ":" + refId.ToString("D"),
            ObjectDescription = objectDescription,
            Name = name,
            ParamsJson = parameters.Write(),
            State = AvatarBuildStates.Queued,
            Stage = AvatarBuildStates.Queued,
            Done = 0,
            Total = moments,
            StatementLocale = request.ConsentLocale!,
            AllowedBy = segmenter is null
                ? null
                : project.SubjectMediaConsentAt is not null ? AvatarBuildJob.AllowedByProjectConsent : AvatarBuildJob.AllowedByCall,
            HoldId = hold?.ReservationId,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Looking with the segmentation model is billed work (the paid bucket); cutting whole frames is free.
        if (await AdmitAsync(job, hold, ct, segmenter is not null ? RunRateBucket.Paid : RunRateBucket.Free) is { } limited)
            return Refused(limited);

        _queue.Enqueue(job.Id);
        _logger.LogInformation(
            "Queued an object extraction {JobId} in project {ProjectId}: {Moments} moment(s), segmentation {Segment}",
            job.Id, project.Id, moments, segmenter is not null);
        return AvatarBuildOutcome<AvatarBuildStarted>.Of(new AvatarBuildStarted(job.Id, job.State));
    }

    private Task<AvatarRefusal?> StoredCopyRefusalAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        StoredCopyRefusalAsync(_storedCopies, projectId, storageKey, ct);

    /// <summary>
    /// Whether the stored bytes at <paramref name="storageKey"/> may leave the host (the publish gate,
    /// <see cref="Moderation.IStoredCopyGate"/>): null when they may, which is always the case for an upload and for a
    /// deployment without content moderation; otherwise the refusal, worded as the avatar reference checks word it
    /// (<c>422 CONTENT_BLOCKED</c>, else <c>409</c> with the gate's code, <c>CONTENT_MODERATION_PENDING</c> while the
    /// output is still being checked). Shared by the request and the worker, so a verdict that changed in between is
    /// read again before any frame is sent.
    /// </summary>
    internal static async Task<AvatarRefusal?> StoredCopyRefusalAsync(
        Moderation.IStoredCopyGate? gate, Guid projectId, string storageKey, CancellationToken ct)
    {
        if (gate is null || string.IsNullOrWhiteSpace(storageKey)) return null;
        Moderation.PublishModerationOutcome? refused = await gate.GateStoredCopyAsync(projectId, storageKey, ct);
        if (refused is null) return null;
        return new AvatarRefusal(
            refused.Code == "CONTENT_BLOCKED" ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status409Conflict,
            refused.Code ?? Moderation.PublishModerationOutcome.PendingCode,
            string.IsNullOrWhiteSpace(refused.Message) ? "This video cannot be sent to the AI service right now." : refused.Message,
            new Dictionary<string, object?> { ["final"] = refused.Final });
    }

    /// <summary>The default <c>Segmentation</c> capability, or null when there is none (or it cannot be used here).</summary>
    private async Task<ResolvedImageAnalysisProvider?> SegmenterAsync(CancellationToken ct)
    {
        if (_providers is null || !_segmentationAvailable) return null;
        try
        {
            return await _providers.ResolveSegmentationAsync(null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unpriced or unreadable segmenter means whole frames, never an unpriced call.
            _logger.LogInformation(ex, "No usable segmentation model for an object extraction; whole frames will be used");
            return null;
        }
    }

    /// <summary><c>file:&lt;guid&gt;</c> or <c>step_result:&lt;guid&gt;</c>.</summary>
    internal static bool TryParseMediaRef(string? mediaRef, out string kind, out Guid id)
    {
        kind = string.Empty;
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(mediaRef) || mediaRef.Length > AvatarBuildJob.MaxMediaRefLength) return false;
        string[] parts = mediaRef.Trim().Split(':', 2);
        if (parts.Length != 2 || parts[0] is not ("file" or "step_result") || !Guid.TryParse(parts[1], out id) || id == Guid.Empty) return false;
        kind = parts[0];
        return true;
    }
}
