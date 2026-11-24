using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>
/// <see cref="IAvatarBuildService"/>: the request half of an avatar build. It checks, prices, holds and writes the job
/// row; <see cref="AvatarBuildProcessor"/> (run by <see cref="AvatarBuildWorker"/>) does the work.
/// </summary>
/// <remarks>
/// <b>The order of a variant's checks</b> is the design's (docs/design/avatar-objects.md "Avatar variants"): the
/// objects switch (404), then (the caller's) Edit access, a verified email, the plan <c>avatar_objects</c>, the
/// original is a usable object (the one rule), the object statement, the media permission (the project's recorded
/// consent, or the per-call permission the caller established; else <c>409 CONSENT_REQUIRED</c>), a default
/// <c>ImageEdit</c> model that takes several pictures, with videos a video model and the plan, the limits, credits
/// (<c>402</c>), the paid run-rate bucket (<c>429</c>). Nothing is written before the last of them passed.
/// </remarks>
public sealed partial class AvatarBuildService : IAvatarBuildService
{
    /// <summary>The <c>run_rate_events.source</c> word of a build.</summary>
    public const string RunRateSource = "avatar_build";

    /// <summary>The most builds a list answers.</summary>
    public const int MaxListed = 50;

    private readonly InferenceApiDbContext _db;
    private readonly AvatarOptions _options;
    private readonly AvatarReferenceChecks _checks;
    private readonly AvatarVariantPictures _pictures;
    private readonly AvatarBuildQueue _queue;
    private readonly ILogger<AvatarBuildService> _logger;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly INonWorkflowChargeService? _charges;
    private readonly IAssistantExecutionGuard? _runRate;
    private readonly IContentModerationGate? _moderation;
    private readonly TimeProvider _time;
    private readonly Outputs.IOutputDescriber? _describer;
    private readonly ReelBolt.Shared.Inference.IInferenceProviderResolver? _providers;
    private readonly ReelBolt.Shared.Inference.IVideoGenerationClientFactory? _videoClients;
    private readonly IModelPriceService? _prices;
    private readonly bool _segmentationAvailable;
    private readonly Moderation.IStoredCopyGate? _storedCopies;

    public AvatarBuildService(
        InferenceApiDbContext db,
        IOptions<AvatarOptions> options,
        AvatarReferenceChecks checks,
        AvatarVariantPictures pictures,
        AvatarBuildQueue queue,
        ILogger<AvatarBuildService> logger,
        IOrganizationEntitlements? entitlements = null,
        INonWorkflowChargeService? charges = null,
        IAssistantExecutionGuard? runRate = null,
        IContentModerationGate? moderation = null,
        TimeProvider? time = null,
        Outputs.IOutputDescriber? describer = null,
        ReelBolt.Shared.Inference.IInferenceProviderResolver? providers = null,
        ReelBolt.Shared.Inference.IVideoGenerationClientFactory? videoClients = null,
        IModelPriceService? prices = null,
        ReelBolt.Shared.Storage.IPresignedUrlService? presignedUrls = null,
        ReelBolt.Shared.Inference.IImageAnalysisClientFactory? analysisClients = null,
        Moderation.IStoredCopyGate? storedCopies = null)
    {
        _db = db;
        _options = options.Value;
        _checks = checks;
        _pictures = pictures;
        _queue = queue;
        _logger = logger;
        _entitlements = entitlements;
        _charges = charges;
        _runRate = runRate;
        _moderation = moderation;
        _time = time ?? TimeProvider.System;
        _describer = describer;
        _providers = providers;
        _videoClients = videoClients;
        _prices = prices;
        // A still reaches a hosted segmentation model only as a short-lived link, so both are needed to look for an object.
        _segmentationAvailable = presignedUrls is not null && analysisClients is not null;
        _storedCopies = storedCopies;
    }

    // ------------------------------------------------------------------ variants

    public async Task<AvatarBuildOutcome<VariantEstimate>> EstimateVariantAsync(
        Project project, AvatarBuildActor actor, Guid avatarId, VariantBuildRequest request, CancellationToken ct)
    {
        (VariantPlan? plan, AvatarRefusal? refusal) = await PlanVariantAsync(project, actor, avatarId, request, forStart: false, mediaAllowed: false, ct);
        if (refusal is not null) return AvatarBuildOutcome<VariantEstimate>.Refused(refusal);

        VariantPlan planned = plan!;
        bool billing = _pictures.BillingEnabled && _charges is not null;
        if (planned.UnavailableReason is { } why)
        {
            // No model can make it here: no figure, not affordable, and the code of the reason (the one a start answers).
            return AvatarBuildOutcome<VariantEstimate>.Of(
                new VariantEstimate(billing, null, null, null, false, planned.Pictures.Count, planned.Videos.Count, why));
        }

        if (!billing)
        {
            return AvatarBuildOutcome<VariantEstimate>.Of(
                new VariantEstimate(false, null, null, null, true, planned.Pictures.Count, planned.Videos.Count));
        }

        long? balance = await _charges!.GetAvailableMillisAsync(project.OrganizationId, ct);
        long high = planned.TotalMillis + planned.VideoCeilingMillis;
        return AvatarBuildOutcome<VariantEstimate>.Of(new VariantEstimate(
            true,
            NonWorkflowChargeResults.ToCredits(planned.TotalMillis),
            NonWorkflowChargeResults.ToCredits(high),
            balance is { } available ? NonWorkflowChargeResults.ToCredits(available) : null,
            balance is not { } known || known >= high,
            planned.Pictures.Count,
            planned.Videos.Count,
            planned.UnavailableReason));
    }

    public async Task<AvatarBuildOutcome<AvatarBuildStarted>> StartVariantAsync(
        Project project, AvatarBuildActor actor, Guid avatarId, VariantBuildRequest request, bool mediaAllowed, CancellationToken ct)
    {
        (VariantPlan? plan, AvatarRefusal? refusal) = await PlanVariantAsync(project, actor, avatarId, request, forStart: true, mediaAllowed, ct);
        if (refusal is not null) return Refused(refusal);
        VariantPlan planned = plan!;

        // ---- The member's words are screened before anything is held or sent (the picture editor's rule). ----
        if (await InstructionRefusalAsync(project, actor, request.Instruction!, ct) is { } blocked) return Refused(blocked);

        // ---- Credits: one hold for every picture, before anything is written. ----
        Guid jobId = Guid.NewGuid();
        ChargeHold? hold = null;
        if (_charges is not null)
        {
            hold = await _charges.BeginHoldAsync(
                project.OrganizationId, UsageSource.Editor, planned.TotalMillis, jobId,
                new CreditLedgerContext(project.Id, Note: "avatar variant"), ct);
            if (!hold.Allowed) return Refused(HoldRefusal(hold, "variant"));
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        var parameters = new AvatarBuildParams
        {
            Description = planned.Description,
            VariantDescription = planned.VariantDescription,
            PictureReferenceIds = [.. planned.Pictures.Select(r => r.Id)],
            VideoReferenceIds = [.. planned.Videos.Select(r => r.Id)],
            IncludeVideos = planned.Videos.Count > 0,
            InstructionScreened = _moderation is not null,
            HoldMillis = hold?.ReservationId is null ? 0 : planned.TotalMillis,
            PerPictureMillis = planned.PerPictureMillis,
            SizePriced = planned.SizePriced,
            VideoMaxSpendCredits = planned.VideoCeilingMillis > 0 ? NonWorkflowChargeResults.ToCredits(planned.VideoCeilingMillis) : null,
            VideoModel = planned.Videos.Count > 0 ? planned.VideoModel : null
        };
        var job = new AvatarBuildJob
        {
            Id = jobId,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            RequestedByUserId = actor.UserId,
            Kind = AvatarBuildKinds.Variant,
            SourceAvatarId = planned.Source.Id,
            Instruction = request.Instruction!.Trim(),
            Name = planned.Name,
            VariantLabel = planned.Label,
            ParamsJson = parameters.Write(),
            State = AvatarBuildStates.Queued,
            Stage = AvatarBuildStates.Queued,
            Done = 0,
            Total = planned.Pictures.Count + planned.Videos.Count,
            StatementLocale = request.ConsentLocale!,
            AllowedBy = project.SubjectMediaConsentAt is not null ? AvatarBuildJob.AllowedByProjectConsent : AvatarBuildJob.AllowedByCall,
            HoldId = hold?.ReservationId,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (await AdmitAsync(job, hold, ct) is { } limited) return Refused(limited);

        _queue.Enqueue(job.Id);
        _logger.LogInformation(
            "Queued avatar variant build {JobId} in project {ProjectId}: {Pictures} picture(s), {Videos} video(s)",
            job.Id, project.Id, planned.Pictures.Count, planned.Videos.Count);
        return AvatarBuildOutcome<AvatarBuildStarted>.Of(new AvatarBuildStarted(job.Id, job.State));
    }

    /// <summary>What a variant request resolves to once every check up to the limits passed.</summary>
    private sealed record VariantPlan(
        ProjectAvatar Source,
        ProjectAvatar Root,
        VariantPictureModel? Model,
        IReadOnlyList<ProjectAvatarReference> Pictures,
        IReadOnlyList<ProjectAvatarReference> Videos,
        string Name,
        string Label,
        string VariantDescription,
        string? Description,
        long TotalMillis,
        long PerPictureMillis,
        bool SizePriced,
        long VideoCeilingMillis,
        string? UnavailableReason,
        AvatarBuildVideoModel? VideoModel = null);

    /// <summary>
    /// Every check of a variant request, in the design's order, up to and including the limits. An estimate
    /// (<paramref name="forStart"/> false) needs neither the statement, the media permission, nor the fields that only
    /// name the result (instruction, label, name): it prices what a start with the same pictures would hold.
    /// </summary>
    private async Task<(VariantPlan? Plan, AvatarRefusal? Refusal)> PlanVariantAsync(
        Project project, AvatarBuildActor actor, Guid avatarId, VariantBuildRequest request, bool forStart, bool mediaAllowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        // ---- The objects switch, a verified email, the plan. ----
        if (!_options.Objects.Enabled) return (null, NotFound());
        if (!actor.EmailVerified) return (null, EmailNotVerified());
        if (await PlanRefusalAsync(project, ct) is { } notEntitled) return (null, notEntitled);

        // ---- The original: a usable object of this project (the one rule). ----
        ProjectAvatar? source = await _db.ProjectAvatars.FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == project.Id, ct);
        if (source is null) return (null, NotFound());
        if (source.SubjectKind != AvatarSubjectKind.Object)
        {
            // A row of a kind whose switch is off does not exist here.
            if (!_options.Enabled) return (null, NotFound());
            return (null, AvatarRefusal.Conflict(AvatarBuildCodes.VariantNotForPeople,
                "A variant of a person cannot be made by the picture model: it would be new, made-up pictures of them. " +
                "Add a variant of a person from other photos or videos of that person instead."));
        }

        List<ProjectAvatarReference> references = await _db.ProjectAvatarReferences.AsNoTracking()
            .Where(r => r.AvatarId == source.Id)
            .OrderBy(r => r.Ordinal)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
        IReadOnlyList<AvatarReferenceState> states = await AvatarReferenceObserver.ObserveAsync(
            _db, project.Id, references, _checks.DigestAsync, ct);
        if (source.UnselectableReason(_time.GetUtcNow().UtcDateTime, [.. states]) is { } reason)
        {
            return (null, AvatarRefusal.Conflict(AvatarBuildCodes.AvatarNotUsable,
                "This avatar cannot be used right now, so no variant can be made from it. Open it in the Avatars tab to see why.",
                new Dictionary<string, object?> { ["reason"] = reason }));
        }

        // One level only: a variant of a variant attaches to the original; its pictures still come from the one named.
        ProjectAvatar root = await AvatarVariantRules.FindOriginalAsync(_db, _options, project.Id, source.Id, ct) ?? source;

        if (forStart)
        {
            // ---- The object statement, then the permission to send the pictures out. ----
            if (ObjectAvatarCreator.StatementRefusal(request.ConsentLocale, request.ConsentStatement) is { } statement)
                return (null, statement);
            if (project.SubjectMediaConsentAt is null && !mediaAllowed) return (null, ConsentRequired());
        }

        // ---- The person check's Vision provider: mandatory, refused before the picture model or anything is held. ----
        AvatarRefusal? visionRefusal = await _checks.VisionRefusalAsync(ct);
        if (visionRefusal is not null && forStart) return (null, visionRefusal);

        // ---- The picture model. ----
        (VariantPictureModel? model, AvatarRefusal? modelRefusal) = await _pictures.ResolveAsync(ct);
        // A start is refused; an estimate answers 200 and says why no figure can be formed (unavailableReason).
        if (modelRefusal is not null && forStart) return (null, modelRefusal);
        string? unavailable = visionRefusal?.Code ?? modelRefusal?.Code;

        // ---- Which pictures and videos. ----
        (List<ProjectAvatarReference>? pictures, List<ProjectAvatarReference>? videos, AvatarRefusal? chosenRefusal) =
            Choose(references, request.ReferenceIds, request.IncludeVideos);
        if (chosenRefusal is not null) return (null, chosenRefusal);

        // ---- Videos need a video model that starts from a picture, and the plan. ----
        long videoCeilingMillis = 0;
        AvatarBuildVideoModel? videoModel = null;
        if (videos!.Count > 0)
        {
            (long ceiling, AvatarBuildVideoModel? chosen, AvatarRefusal? videoRefusal) = await PlanVideosAsync(project, videos, ct);
            if (videoRefusal is not null && forStart) return (null, videoRefusal);
            videoCeilingMillis = ceiling;
            videoModel = chosen;
            unavailable ??= videoRefusal?.Code;
        }

        if (unavailable is not null)
        {
            return (new VariantPlan(source, root, model, pictures!, videos, string.Empty, string.Empty, string.Empty, null,
                0, 0, false, 0, unavailable), null);
        }

        // ---- The limits. ----
        string label = string.Empty, variantDescription = string.Empty, name = string.Empty;
        string? description = string.IsNullOrWhiteSpace(request.Description) ? source.Description : request.Description.Trim();
        if (forStart)
        {
            string instruction = request.Instruction?.Trim() ?? string.Empty;
            if (instruction.Length == 0 || instruction.Length > ProjectAvatarLimits.MaxVariantInstructionLength)
            {
                return (null, AvatarRefusal.BadRequest("variant_instruction_invalid",
                    $"Say what to change in at most {ProjectAvatarLimits.MaxVariantInstructionLength} characters, for example \"paint it matte red\"."));
            }

            // What sets the variant apart defaults to the instruction (cut to the description's length).
            string describedAs = string.IsNullOrWhiteSpace(request.VariantDescription)
                ? instruction.Length <= ProjectAvatarLimits.MaxVariantDescriptionLength
                    ? instruction
                    : instruction[..ProjectAvatarLimits.MaxVariantDescriptionLength]
                : request.VariantDescription;
            (label, variantDescription, AvatarRefusal? invalid) = AvatarVariantRules.Validate(request.VariantLabel, describedAs);
            if (invalid is not null) return (null, invalid);

            name = request.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) name = ProjectAvatar.VariantDisplayName(root.Name, label);
            if (name.Length > ProjectAvatarLimits.MaxNameLength)
                return (null, AvatarRefusal.BadRequest("name_invalid", $"Give the avatar a name of at most {ProjectAvatarLimits.MaxNameLength} characters."));
            if (description is { Length: > ProjectAvatarLimits.MaxDescriptionLength })
                return (null, AvatarRefusal.BadRequest("description_too_long", $"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters."));

            if (await ObjectAvatarCreator.QuarantineRefusalAsync(_db, project, ct) is { } quarantined) return (null, quarantined);

            // A build that is still running counts: its variant will exist when it ends.
            List<AvatarBuildJob> open = await OpenVariantJobsAsync(project.Id, ct);
            HashSet<Guid> family = [root.Id, .. await _db.ProjectAvatars.Where(a => a.VariantOfAvatarId == root.Id).Select(a => a.Id).ToListAsync(ct)];
            List<AvatarBuildJob> openOfRoot = [.. open.Where(j => j.SourceAvatarId is { } s && family.Contains(s))];
            if (await AvatarVariantRules.CountAsync(_db, root.Id, ct) + openOfRoot.Count >= ProjectAvatarLimits.MaxVariantsPerAvatar)
            {
                return (null, AvatarRefusal.Conflict(AvatarVariantRules.LimitReachedCode,
                    $"An avatar can have at most {ProjectAvatarLimits.MaxVariantsPerAvatar} variants. Delete one first."));
            }

            if (await AvatarVariantRules.LabelTakenAsync(_db, root.Id, label, null, ct)
                || openOfRoot.Any(j => string.Equals(j.VariantLabel, label, StringComparison.Ordinal)))
                return (null, AvatarVariantRules.LabelTaken(label));
            if (await _db.ProjectAvatars.AnyAsync(a => a.ProjectId == project.Id && a.ConsentRevokedAt == null && a.Name == name, ct)
                || open.Any(j => string.Equals(j.Name, name, StringComparison.Ordinal)))
                return (null, AvatarRefusal.Conflict("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{name}\"."));
        }

        // ---- The price: every picture, the second and later ones with the first result as a reference. ----
        long total = 0, perPicture = 0;
        bool sizePriced = false;
        (int Width, int Height)? firstSize = pictures!.Count > 0 ? AvatarVariantPictures.SentSize(pictures[0].Width, pictures[0].Height) : null;
        // The grabbed first frame of every video is changed like a picture (docs: stage 2), at the same price rule.
        int edits = pictures.Count + videos.Count;
        for (int i = 0; i < edits; i++)
        {
            ProjectAvatarReference edited = i < pictures.Count ? pictures[i] : videos[i - pictures.Count];
            (int Width, int Height)? sent = AvatarVariantPictures.SentSize(edited.Width, edited.Height);
            bool withReference = i > 0;
            long millis = await _pictures.EstimatePictureMillisAsync(model!, sent, withReference ? firstSize ?? sent : null, ct);
            total += millis;
            perPicture = Math.Max(perPicture, millis);
            sizePriced |= AvatarVariantPictures.SizePriced(model!, withReference);
        }

        return (new VariantPlan(source, root, model!, pictures, videos, name, label, variantDescription, description,
            total, perPicture, sizePriced, videoCeilingMillis, null, videoModel), null);
    }

    /// <summary>
    /// The pictures and videos of the original a request changes: every picture when it names none, else the ones it
    /// names (each must be a reference of the original). Videos are taken only with <paramref name="includeVideos"/>:
    /// the named ones, or every video when the request names no reference at all.
    /// </summary>
    private static (List<ProjectAvatarReference>? Pictures, List<ProjectAvatarReference>? Videos, AvatarRefusal? Refusal) Choose(
        IReadOnlyList<ProjectAvatarReference> references, IReadOnlyList<Guid>? chosenIds, bool includeVideos)
    {
        List<ProjectAvatarReference> chosen;
        if (chosenIds is null || chosenIds.Count == 0)
        {
            chosen = [.. references.Where(r => r.Kind == AvatarReferenceKind.Image || (includeVideos && r.Kind == AvatarReferenceKind.Video))];
        }
        else
        {
            HashSet<Guid> wanted = [.. chosenIds];
            if (wanted.Count != chosenIds.Count || wanted.Any(id => references.All(r => r.Id != id)))
            {
                return (null, null, AvatarRefusal.BadRequest("variant_reference_invalid",
                    "Choose the pictures and videos to change from this avatar's own, each one once."));
            }

            chosen = [.. references.Where(r => wanted.Contains(r.Id))];
        }

        List<ProjectAvatarReference> pictures = [.. chosen.Where(r => r.Kind == AvatarReferenceKind.Image)];
        List<ProjectAvatarReference> videos = includeVideos ? [.. chosen.Where(r => r.Kind == AvatarReferenceKind.Video)] : [];
        return pictures.Count == 0
            ? (null, null, AvatarRefusal.BadRequest("variant_no_pictures", "Choose at least one picture of the avatar to change."))
            : (pictures, videos, null);
    }

    /// <summary>The project's variant builds that have not ended.</summary>
    private async Task<List<AvatarBuildJob>> OpenVariantJobsAsync(Guid projectId, CancellationToken ct) =>
        await _db.AvatarBuildJobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Kind == AvatarBuildKinds.Variant
                        && j.State != AvatarBuildStates.Ready && j.State != AvatarBuildStates.Failed && j.State != AvatarBuildStates.Cancelled)
            .ToListAsync(ct);

    // ------------------------------------------------------------------ reads and cancel

    public async Task<IReadOnlyList<AvatarBuildStatus>> ListBuildsAsync(Guid projectId, CancellationToken ct)
    {
        List<AvatarBuildJob> jobs = await _db.AvatarBuildJobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(MaxListed)
            .ToListAsync(ct);
        return [.. jobs.Select(ToStatus)];
    }

    public async Task<AvatarBuildStatus?> GetBuildAsync(Guid projectId, Guid jobId, CancellationToken ct)
    {
        AvatarBuildJob? job = await _db.AvatarBuildJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, ct);
        return job is null ? null : ToStatus(job);
    }

    public async Task<AvatarBuildStatus?> CancelBuildAsync(Guid projectId, Guid jobId, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            AvatarBuildJob? job = await _db.AvatarBuildJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, ct);
            if (job is null) return null;
            if (AvatarBuildStates.IsTerminal(job.State)) return ToStatus(job);

            job.State = AvatarBuildStates.Cancelled;
            job.Message = "Cancelled.";
            job.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // A worker saved its progress at the same moment: read the row again and cancel that.
                _db.ChangeTracker.Clear();
                continue;
            }

            // The worker stops between two pictures and closes the hold at what was bought; a job no worker holds is
            // offered to one now, so its hold is closed without waiting for the sweeper.
            _queue.Enqueue(job.Id);
            return ToStatus(job);
        }
    }

    /// <summary>The job as it is served: no storage key, no file id, credits instead of millicredits.</summary>
    public static AvatarBuildStatus ToStatus(AvatarBuildJob job)
    {
        AvatarBuildParams parameters = AvatarBuildParams.Read(job.ParamsJson);
        return new AvatarBuildStatus(
            job.Id,
            job.Kind,
            job.State,
            job.Stage,
            job.Done,
            job.Total,
            job.Name,
            job.AvatarId,
            job.FailureCode,
            job.Message,
            parameters.HoldMillis > 0 ? NonWorkflowChargeResults.ToCredits(parameters.HoldMillis) : null,
            job.CreatedAt,
            job.UpdatedAt);
    }

    // ------------------------------------------------------------------ shared pieces

    /// <summary>
    /// The last gate and the write: the paid run-rate bucket is hit once, and the job row is written inside it (so a
    /// refused request writes nothing and consumes nothing). A refusal, or a failed write, releases the hold.
    /// </summary>
    private async Task<AvatarRefusal?> AdmitAsync(
        AvatarBuildJob job, ChargeHold? hold, CancellationToken ct, RunRateBucket bucket = RunRateBucket.Paid)
    {
        try
        {
            if (_runRate is null)
            {
                _db.AvatarBuildJobs.Add(job);
                await _db.SaveChangesAsync(ct);
                return null;
            }

            RunRateDecision decision = await _runRate.TryAcquireAsync(
                job.RequestedByUserId, job.ProjectId, bucket, RunRateSource,
                async () =>
                {
                    _db.AvatarBuildJobs.Add(job);
                    await _db.SaveChangesAsync(ct);
                    return job.Id;
                },
                ct);
            if (decision.Allowed) return null;

            await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, null, _logger);
            return new AvatarRefusal(StatusCodes.Status429TooManyRequests, RunRateRefusal.Code,
                decision.Busy
                    ? "The run limiter is busy right now. Try again in a few seconds."
                    : $"Run limit reached: {decision.Used} of {decision.Limit} {(bucket == RunRateBucket.Paid ? "paid" : "free")} runs in this project in the last hour. " +
                      $"Try again in {RunRateRefusal.Wait(decision.RetryAfterSeconds)}.",
                new Dictionary<string, object?>
                {
                    ["used"] = decision.Used,
                    ["limit"] = decision.Limit,
                    ["windowMinutes"] = decision.WindowSeconds / 60,
                    ["retryAfterSeconds"] = decision.RetryAfterSeconds
                });
        }
        catch
        {
            await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, null, _logger);
            throw;
        }
    }

    /// <summary>The member's own words, screened like a picture editor prompt: only a block refuses (the screen fails open).</summary>
    private async Task<AvatarRefusal?> InstructionRefusalAsync(Project project, AvatarBuildActor actor, string text, CancellationToken ct)
    {
        if (_moderation is null) return null;

        ModerationDecision decision = await _moderation.ModerateAsync(
            new ModerationInput(text, [], ModerationContentKind.Text),
            new ModerationAttribution(project.OrganizationId, project.Id, actor.UserId, null, null,
                ModerationSurface.GenerationPrompt, ModerationContentKind.Text),
            ct);
        if (decision.Action != ModerationAction.Block) return null;

        return AvatarRefusal.BadRequest(AvatarBuildCodes.ContentBlocked,
            string.IsNullOrWhiteSpace(decision.UserMessage) ? "This request may violate the content policy." : decision.UserMessage);
    }

    private async Task<AvatarRefusal?> PlanRefusalAsync(Project project, CancellationToken ct)
    {
        if (_entitlements is null) return null;
        OrganizationEntitlementSet set = await _entitlements.GetAsync(project.OrganizationId, ct);
        return PlanGate.CheckAvatarObjects(set) is { } violation ? FromPlan(violation) : null;
    }

    private static AvatarRefusal FromPlan(PlanGateViolation violation) =>
        new(violation.StatusCode, violation.Code, violation.Message, new Dictionary<string, object?>
        {
            ["feature"] = violation.Feature,
            ["limit"] = violation.Limit,
            ["current"] = violation.Current,
            ["incoming"] = violation.Incoming,
            ["plan"] = violation.Plan
        });

    /// <summary>The refusal of a hold: <c>402 INSUFFICIENT_CREDITS</c> with the figures, or <c>503 GATE_UNAVAILABLE</c> (fail closed).</summary>
    private static AvatarRefusal HoldRefusal(ChargeHold hold, string action)
    {
        if (hold.Code != NonWorkflowChargeCodes.InsufficientCredits)
        {
            return new AvatarRefusal(StatusCodes.Status503ServiceUnavailable, NonWorkflowChargeCodes.GateUnavailable,
                $"Your workspace's credits could not be checked just now, so the {action} was not started. Please try again in a moment.");
        }

        double required = NonWorkflowChargeResults.ToCredits(hold.RequiredMillis);
        double balance = Math.Max(0, NonWorkflowChargeResults.ToCredits(hold.BalanceMillis));
        return new AvatarRefusal(StatusCodes.Status402PaymentRequired, NonWorkflowChargeCodes.InsufficientCredits,
            $"This {action} needs about {required:0.##} credits and the workspace has {balance:0.##} available. " +
            "Add credits on the Plan & billing page to keep going.",
            new Dictionary<string, object?> { ["required"] = required, ["balance"] = balance, ["missing"] = Math.Max(0, required - balance) });
    }

    /// <summary>The refusal every route answers as a bare <c>404</c>.</summary>
    internal static AvatarRefusal NotFound() => new(StatusCodes.Status404NotFound, "not_found", "Not found.");

    private static AvatarRefusal EmailNotVerified() =>
        new(StatusCodes.Status403Forbidden, EmailVerificationGate.ErrorCode,
            "Verify your email address first. Check your inbox for the verification link.");

    private static AvatarRefusal ConsentRequired() =>
        AvatarRefusal.Conflict(AvatarBuildCodes.ConsentRequired,
            "Making this sends the avatar's pictures to an AI provider. Agree to that once for this project in the Avatars tab, or allow it for this request.");

    private static AvatarBuildOutcome<AvatarBuildStarted> Refused(AvatarRefusal refusal) =>
        AvatarBuildOutcome<AvatarBuildStarted>.Refused(refusal);
}
