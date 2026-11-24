using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>
/// Runs ONE avatar build job as far as it can go now (docs/design/avatar-objects.md "Avatar variants", "Pulling an
/// object out of a video"). Scoped: <see cref="AvatarBuildWorker"/> makes one per job, in its own DI scope under a
/// system tenant scope, and it is never cancelled by a request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The lease.</b> A worker takes a job by writing a lease into its notes (<see cref="AvatarBuildParams.LeaseUntil"/>,
/// moved forward with every step it saves) under the row's <c>xmin</c> token, so two API replicas cannot both take it.
/// A job that must wait (for the new files' safety checks, for a hidden run) gives its lease back with a time to look
/// again (<see cref="AvatarBuildParams.RetryAt"/>); the sweeper offers it then. A job whose worker stopped is taken
/// again when its lease has passed.
/// </para>
/// <para>
/// <b>Nothing is bought twice.</b> Every picture is noted in the job the moment it is dealt with, the file it becomes
/// has an id derived from the job and its source (<see cref="AvatarBuildParams.FileIdFor"/>), and before a picture is
/// sent the job looks for that file's row and for its stored object. A job run again goes on after the last noted
/// picture.
/// </para>
/// <para>
/// <b>Money.</b> The one hold taken with the request is closed exactly once, when the job ends: at what the job
/// metered when the member keeps what was made or stopped it themselves (ready, cancelled, a refusal that is theirs to
/// fix), and released when nothing was made or the fault is ours.
/// </para>
/// </remarks>
public sealed partial class AvatarBuildProcessor
{
    /// <summary>What a stage answers: go on with the next stage, wait and be offered again, or the job has ended.</summary>
    private enum Step
    {
        Continue,
        Wait,
        Ended
    }

    /// <summary>How long a job waits before it asks again whether the new files passed their checks.</summary>
    public static readonly TimeSpan ChecksPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>How many times a job is tried again after a fault of ours before it fails.</summary>
    public const int MaxFaults = 3;

    /// <summary>The attach refusals that only mean "not yet": the job waits for them to pass.</summary>
    private static readonly HashSet<string> PendingCodes = new(StringComparer.Ordinal)
    {
        "AVATAR_REFERENCE_PENDING",
        "AVATAR_REFERENCE_UNSCANNED",
        "CONTENT_MODERATION_PENDING",
        "OBJECT_PERSON_CHECK_UNAVAILABLE"
    };

    private readonly InferenceApiDbContext _db;
    private readonly AvatarOptions _options;
    private readonly AvatarBuildOptions _build;
    private readonly AvatarVariantPictures _pictures;
    private readonly AvatarBuildFiles _files;
    private readonly ObjectAvatarCreator _creator;
    private readonly IFileStorageService _storage;
    private readonly ILogger<AvatarBuildProcessor> _logger;
    private readonly INonWorkflowChargeService? _charges;
    private readonly IContentModerationGate? _moderation;
    private readonly TimeProvider _time;
    private readonly AvatarBuildRuns? _runs;
    private readonly AvatarReferenceChecks? _checks;
    private readonly Outputs.IOutputDescriber? _describer;
    private readonly ReelBolt.Shared.Inference.IInferenceProviderResolver? _providers;
    private readonly ReelBolt.Shared.Storage.IPresignedUrlService? _presignedUrls;
    private readonly ReelBolt.Shared.Inference.IImageAnalysisClientFactory? _analysisClients;
    private readonly Moderation.IStoredCopyGate? _storedCopies;

    public AvatarBuildProcessor(
        InferenceApiDbContext db,
        IOptions<AvatarOptions> options,
        IOptions<AvatarBuildOptions> buildOptions,
        AvatarVariantPictures pictures,
        AvatarBuildFiles files,
        ObjectAvatarCreator creator,
        IFileStorageService storage,
        ILogger<AvatarBuildProcessor> logger,
        INonWorkflowChargeService? charges = null,
        IContentModerationGate? moderation = null,
        TimeProvider? time = null,
        AvatarBuildRuns? runs = null,
        AvatarReferenceChecks? checks = null,
        Outputs.IOutputDescriber? describer = null,
        ReelBolt.Shared.Inference.IInferenceProviderResolver? providers = null,
        ReelBolt.Shared.Storage.IPresignedUrlService? presignedUrls = null,
        ReelBolt.Shared.Inference.IImageAnalysisClientFactory? analysisClients = null,
        Moderation.IStoredCopyGate? storedCopies = null)
    {
        _db = db;
        _options = options.Value;
        _build = buildOptions.Value;
        _pictures = pictures;
        _files = files;
        _creator = creator;
        _storage = storage;
        _logger = logger;
        _charges = charges;
        _moderation = moderation;
        _time = time ?? TimeProvider.System;
        _runs = runs;
        _checks = checks;
        _describer = describer;
        _providers = providers;
        _presignedUrls = presignedUrls;
        _analysisClients = analysisClients;
        _storedCopies = storedCopies;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Takes the job when it is due and runs its stages until it ends or has to wait. Returns quietly when another
    /// worker holds it, when it is not due yet, or when it does not exist. Never throws except on cancellation.
    /// </summary>
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        AvatarBuildJob? job = await _db.AvatarBuildJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        AvatarBuildParams p = AvatarBuildParams.Read(job.ParamsJson);

        if (AvatarBuildStates.IsTerminal(job.State))
        {
            // Cancelled while no worker held it (or ended with its hold still open after a crash): close the hold.
            await CloseEndedJobAsync(job, p);
            return;
        }

        if (!await TryClaimAsync(job, p, ct)) return;

        try
        {
            while (true)
            {
                Step step = job.State switch
                {
                    AvatarBuildStates.Pictures => await RunPicturesAsync(job, p, ct),
                    AvatarBuildStates.Videos => await RunVideosAsync(job, p, ct),
                    AvatarBuildStates.Finding => await RunFindingAsync(job, p, ct),
                    AvatarBuildStates.Cutting => await RunCuttingAsync(job, p, ct),
                    AvatarBuildStates.Creating => await RunCreatingAsync(job, p, ct),
                    _ => Step.Ended
                };
                if (step != Step.Continue) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping: give the lease back so the job is taken again at once after the restart.
            await ReleaseLeaseQuietlyAsync(job, p);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Avatar build {JobId} stopped on a fault", jobId);
            await RecoverFromFaultAsync(jobId);
        }
    }

    // ------------------------------------------------------------------ the lease

    /// <summary>
    /// Takes the job: writes the lease (and, for a queued job, its first stage) under the row's concurrency token.
    /// False when another worker holds it, it is not due yet, or another worker's write won.
    /// </summary>
    private async Task<bool> TryClaimAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        DateTime now = Now;
        if (job.State != AvatarBuildStates.Queued && !p.IsDue(now)) return false;

        if (job.State == AvatarBuildStates.Queued)
        {
            job.State = job.Kind == AvatarBuildKinds.Extraction ? AvatarBuildStates.Finding : AvatarBuildStates.Pictures;
            job.Stage = job.State;
        }

        p.LeaseUntil = now + _build.StaleAfter;
        p.RetryAt = null;
        job.ParamsJson = p.Write();
        job.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker (another API replica) took it, or it was cancelled at the same moment.
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Saves the job's progress and moves its lease forward. False when the job was cancelled meanwhile (the caller
    /// stops): a save that loses to a cancel is redone on a fresh read unless the job has ended.
    /// </summary>
    private async Task<bool> SaveProgressAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            DateTime now = Now;
            if (p.LeaseUntil is not null) p.LeaseUntil = now + _build.StaleAfter;
            job.ParamsJson = p.Write();
            job.UpdatedAt = now;
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // Only a cancel writes the row while a worker holds it: an ended row stands, anything else is this
                // worker's to write again on the fresh row.
                (string state, string? stage, int done, int total, Guid? executionId) = (job.State, job.Stage, job.Done, job.Total, job.ExecutionId);
                await _db.Entry(job).ReloadAsync(CancellationToken.None);
                if (_db.Entry(job).State == EntityState.Detached || AvatarBuildStates.IsTerminal(job.State)) return false;
                (job.State, job.Stage, job.Done, job.Total, job.ExecutionId) = (state, stage, done, total, executionId);
            }
        }
    }

    /// <summary>Whether the member cancelled the job since the worker last looked (read fresh, never from the tracked row).</summary>
    private async Task<bool> IsCancelledAsync(AvatarBuildJob job, CancellationToken ct)
    {
        string? state = await _db.AvatarBuildJobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.State).FirstOrDefaultAsync(ct);
        return state is null || state == AvatarBuildStates.Cancelled;
    }

    /// <summary>The job waits: it gives the lease back and says when to look again.</summary>
    private async Task<Step> WaitAsync(AvatarBuildJob job, AvatarBuildParams p, TimeSpan delay, CancellationToken ct)
    {
        p.LeaseUntil = null;
        p.RetryAt = Now + delay;
        p.WaitingSince ??= Now;
        return await SaveProgressAsync(job, p, ct) ? Step.Wait : await StopCancelledAsync(job, p);
    }

    /// <summary>Moves the job to its next stage.</summary>
    private async Task<Step> AdvanceAsync(AvatarBuildJob job, AvatarBuildParams p, string nextState, CancellationToken ct)
    {
        job.State = nextState;
        job.Stage = nextState;
        p.WaitingSince = null;
        return await SaveProgressAsync(job, p, ct) ? Step.Continue : await StopCancelledAsync(job, p);
    }

    // ------------------------------------------------------------------ ends

    /// <summary>
    /// The job failed. <paramref name="keepCharge"/>: the hold settles at what was metered (the member keeps what was
    /// made, or the refusal is theirs to fix); otherwise it is released (nothing made, or the fault is ours).
    /// </summary>
    private async Task<Step> FailAsync(AvatarBuildJob job, AvatarBuildParams p, string code, string message, bool keepCharge)
    {
        await CloseHoldAsync(job, p, keepCharge);
        // A cancel that landed first stands: the member asked for that, not for a failure.
        if (await IsCancelledAsync(job, CancellationToken.None))
        {
            await _db.Entry(job).ReloadAsync(CancellationToken.None);
        }
        else
        {
            job.State = AvatarBuildStates.Failed;
            job.FailureCode = code;
            job.Message = message.Length <= AvatarBuildJob.MaxMessageLength ? message : message[..AvatarBuildJob.MaxMessageLength];
        }

        p.LeaseUntil = null;
        p.RetryAt = null;
        await SaveEndAsync(job, p);
        _logger.LogInformation("Avatar build {JobId} ended {State} ({Code})", job.Id, job.State, code);
        return Step.Ended;
    }

    /// <summary>The member cancelled: what was bought is settled, the rest of the hold is given back.</summary>
    private async Task<Step> StopCancelledAsync(AvatarBuildJob job, AvatarBuildParams p)
    {
        await _db.Entry(job).ReloadAsync(CancellationToken.None);
        await CloseEndedJobAsync(job, p);
        return Step.Ended;
    }

    /// <summary>Closes the hold of a job that has ended and notes it. Idempotent.</summary>
    private async Task CloseEndedJobAsync(AvatarBuildJob job, AvatarBuildParams p)
    {
        // A cancelled job's hidden runs are stopped (a take still going would otherwise be bought).
        bool runsOpen = job.State == AvatarBuildStates.Cancelled && p.Runs.Any(r => r.Queued && !r.Read);
        if (runsOpen) await StopOpenRunsAsync(job, p);
        if (p.HoldClosed && p.LeaseUntil is null && p.RetryAt is null && !runsOpen) return;

        // A cancelled or ready job keeps what it bought; a failed job's hold was closed by the failure itself.
        await CloseHoldAsync(job, p, keepCharge: job.State != AvatarBuildStates.Failed);
        p.LeaseUntil = null;
        p.RetryAt = null;
        await SaveEndAsync(job, p);
    }

    /// <summary>
    /// Closes the job's hold once: settled at what the job metered (or released when that is nothing) with
    /// <paramref name="keepCharge"/>, else released.
    /// </summary>
    private async Task CloseHoldAsync(AvatarBuildJob job, AvatarBuildParams p, bool keepCharge)
    {
        if (p.HoldClosed) return;

        if (_charges is not null && job.HoldId is { } reservationId)
        {
            var hold = new ChargeHold(true, reservationId, p.HoldMillis, 0, null)
            {
                OrganizationId = job.OrganizationId,
                Source = UsageSource.Editor,
                RequestId = job.Id
            };
            await NonWorkflowChargeResults.CloseHoldAsync(
                _charges, hold, keepCharge ? p.MeteredMillis : null, _logger, uncapped: p.SizePriced);
        }

        p.HoldClosed = true;
    }

    /// <summary>The last save of an ended job; never the request's or the host's token, and a lost race is retried on a fresh row.</summary>
    private async Task SaveEndAsync(AvatarBuildJob job, AvatarBuildParams p)
    {
        for (int attempt = 1; ; attempt++)
        {
            job.ParamsJson = p.Write();
            job.UpdatedAt = Now;
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                string state = job.State;
                string? code = job.FailureCode, message = job.Message;
                Guid? avatarId = job.AvatarId;
                await _db.Entry(job).ReloadAsync(CancellationToken.None);
                // A cancel stands; any other state is this worker's to write.
                if (job.State != AvatarBuildStates.Cancelled)
                {
                    job.State = state;
                    job.FailureCode = code;
                    job.Message = message;
                    job.AvatarId = avatarId;
                }
            }
        }
    }

    /// <summary>
    /// After a fault of ours: the job is tried again a little later, a few times; then it fails and its hold is released
    /// (the fault is not the member's). Works on a fresh context state, because the fault may have left the unit of work
    /// unusable.
    /// </summary>
    private async Task RecoverFromFaultAsync(Guid jobId)
    {
        try
        {
            _db.ChangeTracker.Clear();
            AvatarBuildJob? job = await _db.AvatarBuildJobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
            if (job is null || AvatarBuildStates.IsTerminal(job.State)) return;

            AvatarBuildParams p = AvatarBuildParams.Read(job.ParamsJson);
            p.Faults++;
            if (p.Faults < MaxFaults)
            {
                p.LeaseUntil = null;
                p.RetryAt = Now + TimeSpan.FromSeconds(30);
                await SaveEndAsync(job, p);
                return;
            }

            await FailAsync(job, p, AvatarBuildCodes.BuildFailed,
                "This could not be finished because of a problem on our side. Nothing was charged; please try again.", keepCharge: false);
        }
        catch (Exception ex)
        {
            // The lease passes on its own; the sweeper offers the job again.
            _logger.LogError(ex, "Could not record the fault of avatar build {JobId}", jobId);
        }
    }

    private async Task ReleaseLeaseQuietlyAsync(AvatarBuildJob job, AvatarBuildParams p)
    {
        try
        {
            if (AvatarBuildStates.IsTerminal(job.State)) return;
            p.LeaseUntil = null;
            p.RetryAt = null;
            job.ParamsJson = p.Write();
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not give back the lease of avatar build {JobId}; it passes on its own", job.Id);
        }
    }

    // ------------------------------------------------------------------ stage: pictures (a variant)

    /// <summary>
    /// Stage 1 of a variant: each chosen picture of the original is checked against its attested digest and changed by
    /// the picture model, the first finished result going along from the second picture on. A picture that fails is
    /// skipped; none made fails the job.
    /// </summary>
    private async Task<Step> RunPicturesAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        (Project? project, ProjectAvatar? source, ProjectAvatar? root, List<ProjectAvatarReference>? references) = await LoadVariantAsync(job, ct);
        if (project is null || source is null || root is null || references is null)
        {
            return await FailAsync(job, p, AvatarBuildCodes.AvatarNotUsable,
                "The avatar this variant is made from is no longer in the project.", keepCharge: true);
        }

        List<ProjectAvatarReference> chosen = [.. references.Where(r => p.PictureReferenceIds.Contains(r.Id) && r.Kind == AvatarReferenceKind.Image)];
        if (chosen.Count != p.PictureReferenceIds.Count)
        {
            return await FailAsync(job, p, AvatarBuildCodes.AvatarNotUsable,
                "The avatar's pictures were changed after this variant was asked for. Ask for it again.", keepCharge: true);
        }

        (VariantPictureModel? model, AvatarRefusal? modelRefusal) = await _pictures.ResolveAsync(ct);
        if (model is null) return await FailAsync(job, p, modelRefusal!.Code, modelRefusal.Message, keepCharge: true);

        // What the request checked, again and fresh, before anything is sent (the same helper the video stage uses).
        // (The job-level part now; each source picture is observed right before it is sent, below.)
        SendRecheck entry = await RecheckBeforeSendingAsync(job, [], ct);
        if (entry.Refusal is { } entryRefusal) return await FailRecheckAsync(job, p, entryRefusal);

        // The member's words, before any vendor call (once: a request that screened them says so).
        if (!p.InstructionScreened && _moderation is not null)
        {
            bool allowed = await _moderation.ScreenGenerationPromptAsync(
                job.Instruction ?? string.Empty, [],
                new ModerationAttribution(job.OrganizationId, job.ProjectId, job.RequestedByUserId, null, null,
                    ModerationSurface.GenerationPrompt, ModerationContentKind.Text),
                ct);
            if (!allowed)
            {
                return await FailAsync(job, p, AvatarBuildCodes.ContentBlocked,
                    "What was asked for may violate the content policy, so nothing was made.", keepCharge: false);
            }

            p.InstructionScreened = true;
        }

        Guid[] sourceFileIds = [.. chosen.Select(r => r.ProjectFileId)];
        Dictionary<Guid, ProjectFile> sourceFiles = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == project.Id && sourceFileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        // Storage: asked before anything is bought, with the size of the pictures being changed as the guess.
        bool anythingNoted = chosen.Any(r => p.Find(AvatarBuildItem.PictureKey(r.Id)) is not null);
        if (!anythingNoted
            && await _files.QuotaAsync(project, sourceFiles.Values.Sum(f => f.SizeBytes), ct) is { } noRoom)
            return await FailAsync(job, p, noRoom.Code, noRoom.Message, keepCharge: false);

        string directory = AvatarBuildFiles.VariantDirectory(root.Name, job.VariantLabel ?? "variant");
        string stem = AvatarBuildFiles.Segment(job.VariantLabel, "variant");
        // The first finished result goes along with every later picture, so every view gets the same change.
        VariantPictureInput? firstResult = AsReference(await LoadFirstResultAsync(project.Id, p, chosen, ct));

        for (int i = 0; i < chosen.Count; i++)
        {
            ProjectAvatarReference reference = chosen[i];
            string key = AvatarBuildItem.PictureKey(reference.Id);
            if (p.Find(key) is not null) continue;

            if (await IsCancelledAsync(job, ct)) return await StopCancelledAsync(job, p);
            // The heartbeat, before the slow part.
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);

            Guid fileId = AvatarBuildParams.FileIdFor(job.Id, key);
            string baseName = stem + "-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // ---- Already made by an earlier run of this job? Then it is not bought again. ----
            (ProjectFile? earlier, PlanGateViolation? earlierQuota) = await FindEarlierAsync(project, job, fileId, directory, baseName, ct);
            if (earlierQuota is not null) return await FailAsync(job, p, earlierQuota.Code, earlierQuota.Message, keepCharge: true);
            if (earlier is not null)
            {
                Note(p, key, AvatarBuildItem.Made, earlier.Id, reference);
                job.Done++;
                firstResult ??= AsReference(await ReadBoundedAsync(project.Id, earlier.StorageKey, ProjectAvatarLimits.MaxImageBytes, ct));
                if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
                continue;
            }

            // ---- Just before this picture leaves: the holds, the permission and the member's access, read fresh again. ----
            SendRecheck before = await RecheckBeforeSendingAsync(job, [reference], ct);
            if (before.Refusal is { } stopped) return await FailRecheckAsync(job, p, stopped);
            if (before.States[0] is not { Present: true, Held: false }) return await FailPictureNotUsableAsync(job, p);

            // ---- The picture as attested: its bytes must hash to the digest the avatar was confirmed with. ----
            if (!sourceFiles.TryGetValue(reference.ProjectFileId, out ProjectFile? sourceFile)
                || await ReadBoundedAsync(project.Id, sourceFile.StorageKey, ProjectAvatarLimits.MaxImageBytes, ct) is not { } bytes
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(job, p, AvatarBuildCodes.AvatarNotUsable,
                    "A picture of the avatar is missing or was changed after it was added, so the variant was not finished. " +
                    "Confirm the avatar's pictures again in the Avatars tab, then ask for the variant again.", keepCharge: true);
            }

            VariantPictureInput? input = AvatarVariantPictures.Prepare(bytes, sourceFile.MimeType, reference.Width, reference.Height);
            if (input is null)
            {
                Note(p, key, AvatarBuildItem.Failed, null, reference);
                if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
                continue;
            }

            VariantPictureResult result;
            using (UsageScope.Begin(job.OrganizationId, job.ProjectId, null, null, job.RequestedByUserId, UsageSource.Editor))
            {
                result = await _pictures.EditAsync(model, input, firstResult, job.Instruction, p.PerPictureMillis, _build.PictureTimeout, ct);
            }

            p.MeteredMillis += result.MeteredMillis;
            // What was just bought is noted before anything else can fail (a cancel is seen at the next picture).
            if (result.MeteredMillis > 0) await SaveProgressAsync(job, p, ct);
            if (result.Bytes is null)
            {
                Note(p, key, AvatarBuildItem.Failed, null, reference);
                if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
                continue;
            }

            string mime = result.ContentType ?? "image/jpeg";
            AvatarBuildFileOutcome saved = await _files.SaveAsync(
                project, job.RequestedByUserId, fileId, directory, baseName + "." + AvatarBuildFiles.ExtensionOf(mime), mime, result.Bytes, ct);
            if (saved.OverQuota is { } overQuota)
            {
                // The picture was made and paid for at the vendor, but there is no room to keep it.
                return await FailAsync(job, p, overQuota.Code, overQuota.Message, keepCharge: true);
            }

            Note(p, key, AvatarBuildItem.Made, saved.File!.Id, reference);
            job.Done++;
            firstResult ??= AsReference(result.Bytes);
            await RecordMeteredAsync(job, p);
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
        }

        if (!chosen.Any(r => p.Find(AvatarBuildItem.PictureKey(r.Id)) is { Outcome: AvatarBuildItem.Made }))
        {
            return await FailAsync(job, p, AvatarBuildCodes.VariantFailed,
                "None of the pictures could be changed, so no variant was made. Nothing was charged; try again, or word the change differently.",
                keepCharge: false);
        }

        return await AdvanceAsync(job, p, p.IncludeVideos && p.VideoReferenceIds.Count > 0 ? AvatarBuildStates.Videos : AvatarBuildStates.Creating, ct);
    }

    /// <summary>The project, the avatar the pictures come from, the original the variant attaches to, and the source's references.</summary>
    private async Task<(Project? Project, ProjectAvatar? Source, ProjectAvatar? Root, List<ProjectAvatarReference>? References)> LoadVariantAsync(
        AvatarBuildJob job, CancellationToken ct)
    {
        Project? project = await _db.Projects.FirstOrDefaultAsync(pr => pr.Id == job.ProjectId, ct);
        if (project is null || job.SourceAvatarId is not { } sourceId) return (project, null, null, null);

        ProjectAvatar? source = await _db.ProjectAvatars.FirstOrDefaultAsync(a => a.Id == sourceId && a.ProjectId == job.ProjectId, ct);
        if (source is null || source.SubjectKind != AvatarSubjectKind.Object || !_options.Objects.Enabled) return (project, null, null, null);

        ProjectAvatar root = await AvatarVariantRules.FindOriginalAsync(_db, _options, job.ProjectId, sourceId, ct) ?? source;
        List<ProjectAvatarReference> references = await _db.ProjectAvatarReferences.AsNoTracking()
            .Where(r => r.AvatarId == sourceId)
            .OrderBy(r => r.Ordinal)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
        return (project, source, root, references);
    }

    /// <summary>
    /// The file an earlier run of this job made for one source: its row, or its stored object without a row (the row is
    /// then written). A picture's type is only known once it is made, so each possible name is looked for.
    /// </summary>
    private async Task<(ProjectFile? File, PlanGateViolation? OverQuota)> FindEarlierAsync(
        Project project, AvatarBuildJob job, Guid fileId, string directory, string baseName, CancellationToken ct)
    {
        if (await _files.FindAsync(project.Id, fileId, ct) is { } row) return (row, null);

        foreach ((string extension, string mime) in new[] { ("jpg", "image/jpeg"), ("png", "image/png"), ("webp", "image/webp") })
        {
            AvatarBuildFileOutcome recovered = await _files.RecoverAsync(
                project, job.RequestedByUserId, fileId, directory, baseName + "." + extension, mime, ct);
            if (recovered.OverQuota is not null) return (null, recovered.OverQuota);
            if (recovered.File is not null) return (recovered.File, null);
        }

        return (null, null);
    }

    /// <summary>The first picture the job already made (in the original's order), to send along with the later ones.</summary>
    private async Task<byte[]?> LoadFirstResultAsync(
        Guid projectId, AvatarBuildParams p, IReadOnlyList<ProjectAvatarReference> chosen, CancellationToken ct)
    {
        foreach (ProjectAvatarReference reference in chosen)
        {
            if (p.Find(AvatarBuildItem.PictureKey(reference.Id)) is not { Outcome: AvatarBuildItem.Made, FileId: { } fileId }) continue;
            string? key = await _db.ProjectFiles.AsNoTracking()
                .Where(f => f.Id == fileId && f.ProjectId == projectId).Select(f => f.StorageKey).FirstOrDefaultAsync(ct);
            return key is null ? null : await ReadBoundedAsync(projectId, key, ProjectAvatarLimits.MaxImageBytes, ct);
        }

        return null;
    }

    /// <summary>A finished result prepared to go along as the second picture of a later edit; null when there is none or it cannot be sent.</summary>
    private static VariantPictureInput? AsReference(byte[]? result)
    {
        if (result is null) return null;
        (int? width, int? height) = ReelBolt.Shared.Inference.ImageHeader.TryReadDisplaySize(result, out int w, out int h) ? (w, h) : ((int?)null, (int?)null);
        return AvatarVariantPictures.Prepare(result, null, width, height);
    }

    private static void Note(AvatarBuildParams p, string key, string outcome, Guid? fileId, ProjectAvatarReference reference) =>
        p.Items.Add(new AvatarBuildItem
        {
            Key = key,
            Outcome = outcome,
            FileId = fileId,
            Role = reference.Role.ToString(),
            Ordinal = reference.Ordinal
        });

    /// <summary>
    /// Notes what the job has metered on its still-open hold, so a hold swept as abandoned (a long job, a stopped
    /// process) is charged that instead of released. Best effort.
    /// </summary>
    private async Task RecordMeteredAsync(AvatarBuildJob job, AvatarBuildParams p)
    {
        if (_charges is null || job.HoldId is not { } reservationId || p.MeteredMillis <= 0) return;
        try
        {
            await _charges.RecordUnsettledHoldAsync(
                new ChargeHold(true, reservationId, p.HoldMillis, 0, null)
                {
                    OrganizationId = job.OrganizationId,
                    Source = UsageSource.Editor,
                    RequestId = job.Id
                },
                p.MeteredMillis, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not note the metered usage of avatar build {JobId} on its hold", job.Id);
        }
    }

    // ------------------------------------------------------------------ stage: creating (both kinds)

    /// <summary>
    /// The last stage: the avatar is created through the SAME create path as the REST route
    /// (<see cref="ObjectAvatarCreator"/>), for the member who asked, under the object statement they sent. A refusal
    /// that only means "not yet" (the new files are still being scanned) makes the job wait; any other ends it.
    /// </summary>
    private async Task<Step> RunCreatingAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        if (await IsCancelledAsync(job, ct)) return await StopCancelledAsync(job, p);

        Project? project = await _db.Projects.FirstOrDefaultAsync(pr => pr.Id == job.ProjectId, ct);
        if (project is null) return await FailAsync(job, p, AvatarBuildCodes.BuildFailed, "The project is gone.", keepCharge: false);

        bool variant = job.Kind == AvatarBuildKinds.Variant;
        List<AvatarReferenceAsk> asks = [.. p.Items
            .Where(i => i.Outcome == AvatarBuildItem.Made && i.FileId is not null && !i.Key.StartsWith("frame:", StringComparison.Ordinal))
            .OrderBy(i => i.Key.StartsWith("vid:", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(i => i.Ordinal)
            .Select(i => new AvatarReferenceAsk(i.FileId!.Value, i.Role))];
        if (asks.Count == 0)
        {
            return await FailAsync(job, p, variant ? AvatarBuildCodes.VariantFailed : AvatarBuildCodes.ObjectNotFoundInVideo,
                variant ? "No picture was made, so no variant was created." : "No usable picture of the object came out of the video.",
                keepCharge: true);
        }

        p.CreatingSince ??= Now;
        ObjectAvatarCreateOutcome created = await _creator.CreateAsync(
            project,
            job.RequestedByUserId,
            new ObjectAvatarCreateRequest(
                job.Name,
                p.Description,
                job.StatementLocale,
                AvatarObjectStatement.For(job.StatementLocale),
                asks,
                variant ? new AvatarVariantAsk(job.SourceAvatarId, job.VariantLabel, p.VariantDescription) : null,
                variant ? ObjectAvatarCreator.ViaVariantBuild : ObjectAvatarCreator.ViaExtraction),
            ct);

        if (created.Refusal is { } refusal)
        {
            if (PendingCodes.Contains(refusal.Code))
            {
                if (Now - p.CreatingSince.Value < _build.ChecksTimeout) return await WaitAsync(job, p, ChecksPollInterval, ct);
                return await FailAsync(job, p, AvatarBuildCodes.ChecksTimedOut,
                    "The new files were made and are in the project, but their safety checks did not finish in time, so the avatar was not created. " +
                    "You can create it from those files in the Avatars tab.", keepCharge: true);
            }

            // No Vision provider: the person check cannot be made, so no avatar is created and the hold is released.
            if (refusal.Code == AvatarReferenceChecks.VisionProviderRequiredCode)
                return await FailAsync(job, p, refusal.Code, refusal.Message + " Nothing was charged.", keepCharge: false);

            // The files were made and stay in the project; the create path's own words say why no avatar was made.
            return await FailAsync(job, p, refusal.Code, refusal.Message, keepCharge: true);
        }

        await CloseHoldAsync(job, p, keepCharge: true);
        job.AvatarId = created.Avatar!.Id;
        job.State = AvatarBuildStates.Ready;
        job.Message = p.Note;
        p.LeaseUntil = null;
        p.RetryAt = null;
        await SaveEndAsync(job, p);
        _logger.LogInformation("Avatar build {JobId} made avatar {AvatarId}", job.Id, created.Avatar.Id);
        return Step.Ended;
    }

    /// <summary>A source picture is missing, was changed after it was attested, or is on hold: nothing is sent.</summary>
    private Task<Step> FailPictureNotUsableAsync(AvatarBuildJob job, AvatarBuildParams p) =>
        FailAsync(job, p, AvatarBuildCodes.AvatarNotUsable,
            "A picture of the avatar is missing, was changed after it was added, or is on hold, so the variant was not finished. " +
            "Confirm the avatar's pictures again in the Avatars tab, then ask for the variant again.", keepCharge: p.MeteredMillis > 0);

    // ------------------------------------------------------------------ bytes

    /// <summary>The object's bytes when it can be read and is at most <paramref name="maxBytes"/>; otherwise null. Never throws except on cancellation.</summary>
    private async Task<byte[]?> ReadBoundedAsync(Guid projectId, string storageKey, long maxBytes, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, storageKey, ct);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read an object of project {ProjectId} for an avatar build", projectId);
            return null;
        }
    }
}
