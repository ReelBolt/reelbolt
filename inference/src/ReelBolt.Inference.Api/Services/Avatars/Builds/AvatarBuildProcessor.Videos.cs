using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

// The stages that run hidden AvatarBuild workflows in the engine (docs/design/avatar-objects.md): a variant's videos
// (stage 2: grab each video's first frame, change it like a picture, make the video again from it), and an object
// pulled out of a video (find it in stills, cut it out of the frames at full size).
public sealed partial class AvatarBuildProcessor
{
    /// <summary>How often a job that waits for a hidden run, or for stills, looks again.</summary>
    public static readonly TimeSpan RunPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long a job waits before it asks again when the plan's concurrency cap refused its run.</summary>
    public static readonly TimeSpan BusyRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often stills that have not arrived are asked for again (the first ask can be dropped while a file is still being scanned).</summary>
    public static readonly TimeSpan StillsAskAgainAfter = TimeSpan.FromMinutes(3);

    /// <summary>How long the link a segmentation model reads a still through lives.</summary>
    public static readonly TimeSpan StillLinkTtl = TimeSpan.FromMinutes(10);

    /// <summary>The longest one segmentation call may take before that moment is kept whole.</summary>
    public static readonly TimeSpan SegmentationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A step of a hidden run and the item it stands for.</summary>
    private sealed record RunItem(string Key, AvatarBuildRunStep Step);

    // ------------------------------------------------------------------ stage: videos (a variant)

    /// <summary>
    /// Stage 2 of a variant: the first frame of each chosen video is grabbed (a free <c>ExtractFrame</c> in a hidden
    /// run), changed like a picture (here, within the job's hold), and the video is made again from it by the video
    /// model the request chose (an inline <c>VideoGenerate</c> in a second hidden run, through the engine's own credit
    /// and budget gates). A video that fails at any point is skipped; the variant is still made from its pictures.
    /// </summary>
    private async Task<Step> RunVideosAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        (Project? project, ProjectAvatar? source, ProjectAvatar? root, List<ProjectAvatarReference>? references) = await LoadVariantAsync(job, ct);
        if (project is null || source is null || root is null || references is null)
        {
            return await FailAsync(job, p, AvatarBuildCodes.AvatarNotUsable,
                "The avatar this variant is made from is no longer in the project.", keepCharge: true);
        }

        List<ProjectAvatarReference> videos = [.. p.VideoReferenceIds
            .Select(id => references.FirstOrDefault(r => r.Id == id && r.Kind == AvatarReferenceKind.Video))
            .OfType<ProjectAvatarReference>()];
        if (p.VideoModel is null || _runs is not { Available: true } || videos.Count == 0)
        {
            p.Note = "The videos could not be made again here, so the variant was made from its pictures.";
            return await AdvanceAsync(job, p, AvatarBuildStates.Creating, ct);
        }

        // ---- What the request checked, again and fresh, before anything is sent (the helper the pictures stage uses too). ----
        SendRecheck recheck = await RecheckBeforeSendingAsync(job, videos, ct);
        if (recheck.Refusal is { } stop) return await FailRecheckAsync(job, p, stop);

        // ---- Only a video that still has the bytes it was attested with and is not held is used (never sent changed). ----
        if (!p.Runs.Any(r => r.Purpose == AvatarBuildRun.Grabs))
        {
            IReadOnlyList<AvatarReferenceState> states = recheck.States;
            for (int i = 0; i < videos.Count; i++)
            {
                if (states[i] is { Present: true, Held: false }) continue;
                p.RunOutcomes[AvatarBuildItem.GrabKey(videos[i].Id)] = new AvatarBuildRunOutcome { Ok = false, FailureCode = AvatarBuildCodes.AvatarNotUsable };
            }
        }

        // ---- 1. The first frame of each video (free). ----
        List<RunItem> grabs = [.. videos.Select((v, i) => new RunItem(
            AvatarBuildItem.GrabKey(v.Id),
            new AvatarBuildRunStep(
                $"First frame of video {i + 1}",
                StepType.ExtractFrame,
                JsonSerializer.Serialize(
                    new ExtractFrameStepConfig(new VideoSourceRef(VideoSourceKind.ProjectFile, v.ProjectFileId), FrameAt.First, RegisterProjectFile: false),
                    ExtractFrameStepConfig.JsonOptions))))];
        if (await DriveRunsAsync(job, p, AvatarBuildRun.Grabs, grabs, $"Avatar variant frames: {job.Name}", ct) is { } waitingForGrabs)
            return waitingForGrabs;

        // ---- 2. Each frame changed like a picture, the first finished picture going along (within the job's hold). ----
        string directory = AvatarBuildFiles.VariantDirectory(root.Name, job.VariantLabel ?? "variant");
        string stem = AvatarBuildFiles.Segment(job.VariantLabel, "variant");
        List<ProjectAvatarReference> pictures = [.. references.Where(r => p.PictureReferenceIds.Contains(r.Id) && r.Kind == AvatarReferenceKind.Image)];
        VariantPictureInput? firstResult = null;
        bool firstLoaded = false;
        VariantPictureModel? model = null;
        for (int i = 0; i < videos.Count; i++)
        {
            ProjectAvatarReference video = videos[i];
            string key = AvatarBuildItem.FrameKey(video.Id);
            if (p.Find(key) is not null) continue;
            if (await IsCancelledAsync(job, ct)) return await StopCancelledAsync(job, p);
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);

            if (p.RunOutcomes.GetValueOrDefault(AvatarBuildItem.GrabKey(video.Id)) is not { Ok: true, StepResultId: { } grabbed }
                || await OutputKeyAsync(job.ProjectId, grabbed, ct) is not { } frameKey
                || await ReadBoundedAsync(project.Id, frameKey, ProjectAvatarLimits.MaxImageBytes, ct) is not { } frame)
            {
                Note(p, key, AvatarBuildItem.Failed, null, video);
                continue;
            }

            Guid fileId = AvatarBuildParams.FileIdFor(job.Id, key);
            string baseName = stem + "-video-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-frame";
            (ProjectFile? earlier, PlanGateViolation? earlierQuota) = await FindEarlierAsync(project, job, fileId, directory, baseName, ct);
            if (earlierQuota is not null) return await FailAsync(job, p, earlierQuota.Code, earlierQuota.Message, keepCharge: true);
            if (earlier is not null)
            {
                Note(p, key, AvatarBuildItem.Made, earlier.Id, video);
                continue;
            }

            if (model is null)
            {
                (model, AvatarRefusal? modelRefusal) = await _pictures.ResolveAsync(ct);
                if (model is null) return await FailAsync(job, p, modelRefusal!.Code, modelRefusal.Message, keepCharge: true);
            }

            if (!firstLoaded)
            {
                firstResult = AsReference(await LoadFirstResultAsync(project.Id, p, pictures, ct));
                firstLoaded = true;
            }

            (int? width, int? height) = ImageHeader.TryReadDisplaySize(frame, out int w, out int h) ? (w, h) : ((int?)null, (int?)null);
            VariantPictureInput? input = AvatarVariantPictures.Prepare(frame, "image/png", width, height);
            if (input is null)
            {
                Note(p, key, AvatarBuildItem.Failed, null, video);
                continue;
            }

            VariantPictureResult result;
            using (UsageScope.Begin(job.OrganizationId, job.ProjectId, null, null, job.RequestedByUserId, UsageSource.Editor))
            {
                result = await _pictures.EditAsync(model, input, firstResult, job.Instruction, p.PerPictureMillis, _build.PictureTimeout, ct);
            }

            p.MeteredMillis += result.MeteredMillis;
            if (result.MeteredMillis > 0) await SaveProgressAsync(job, p, ct);
            if (result.Bytes is null)
            {
                Note(p, key, AvatarBuildItem.Failed, null, video);
                continue;
            }

            string mime = result.ContentType ?? "image/jpeg";
            AvatarBuildFileOutcome saved = await _files.SaveAsync(
                project, job.RequestedByUserId, fileId, directory, baseName + "." + AvatarBuildFiles.ExtensionOf(mime), mime, result.Bytes, ct);
            if (saved.OverQuota is { } overQuota) return await FailAsync(job, p, overQuota.Code, overQuota.Message, keepCharge: true);
            Note(p, key, AvatarBuildItem.Made, saved.File!.Id, video);
            await RecordMeteredAsync(job, p);
        }

        if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);

        // ---- 3. Each video made again from its changed frame (paid, through the engine's own gates). ----
        List<RunItem> takes = [];
        for (int i = 0; i < videos.Count; i++)
        {
            ProjectAvatarReference video = videos[i];
            if (p.Find(AvatarBuildItem.FrameKey(video.Id)) is not { Outcome: AvatarBuildItem.Made, FileId: { } frameFileId }) continue;
            if (p.VideoModel.Takes.FirstOrDefault(t => t.ReferenceId == video.Id) is not { } take) continue;
            takes.Add(new RunItem(
                AvatarBuildItem.TakeKey(video.Id),
                new AvatarBuildRunStep(
                    $"Video {i + 1} made again",
                    StepType.VideoGenerate,
                    JsonSerializer.Serialize(AvatarBuildService.TakeConfig(p.VideoModel, take, frameFileId, job.Instruction), VideoGenerateStepConfig.JsonOptions))));
        }

        if (takes.Count > 0
            && await DriveRunsAsync(job, p, AvatarBuildRun.Takes, takes, $"Avatar variant videos: {job.Name}", ct) is { } waitingForTakes)
            return waitingForTakes;

        // ---- 4. The delivered videos become the variant's own files. ----
        for (int i = 0; i < videos.Count; i++)
        {
            ProjectAvatarReference video = videos[i];
            string key = AvatarBuildItem.VideoKey(video.Id);
            if (p.Find(key) is not null) continue;

            if (p.RunOutcomes.GetValueOrDefault(AvatarBuildItem.TakeKey(video.Id)) is not { Ok: true, StepResultId: { } made }
                || await OutputKeyAsync(job.ProjectId, made, ct) is not { } outputKey)
            {
                Note(p, key, AvatarBuildItem.Failed, null, video);
                continue;
            }

            Guid fileId = AvatarBuildParams.FileIdFor(job.Id, key);
            AvatarBuildFileOutcome copied = await _files.CopyAsync(
                project, job.RequestedByUserId, fileId, directory,
                stem + "-video-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp4", "video/mp4",
                outputKey, AvatarBuildFiles.VariantSourceKind, ct);
            if (copied.OverQuota is { } overQuota) return await FailAsync(job, p, overQuota.Code, overQuota.Message, keepCharge: true);
            if (copied.File is null)
            {
                Note(p, key, AvatarBuildItem.Failed, null, video);
                continue;
            }

            Note(p, key, AvatarBuildItem.Made, copied.File.Id, video);
            job.Done++;
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
        }

        int madeVideos = videos.Count(v => p.Find(AvatarBuildItem.VideoKey(v.Id)) is { Outcome: AvatarBuildItem.Made });
        if (madeVideos < videos.Count)
        {
            p.Note = madeVideos == 0
                ? "The videos could not be made again, so the variant was made from its pictures."
                : $"{videos.Count - madeVideos} of {videos.Count} videos could not be made again; the variant has the others.";
        }

        return await AdvanceAsync(job, p, AvatarBuildStates.Creating, ct);
    }

    // ------------------------------------------------------------------ stage: finding (an extraction)

    /// <summary>
    /// Stage 1 of an extraction: stills of up to 6 moments of the video (the <c>Stills</c> derivative, asked for and
    /// waited for), then, when the request allowed it, the object looked for in each still by the segmentation model
    /// with the member's description as its text prompt: a hit gives a box, a miss drops that moment. Without the model
    /// (or the permission) the whole frames are used and the job says so.
    /// </summary>
    private async Task<Step> RunFindingAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        Project? project = await _db.Projects.FirstOrDefaultAsync(pr => pr.Id == job.ProjectId, ct);
        OutputSource? source = project is null ? null : await ExtractionSourceAsync(job, ct);
        if (project is null || source is null)
        {
            return await FailAsync(job, p, AvatarBuildCodes.SourceUnavailable,
                "The video can no longer be read (it was deleted, or it is on hold), so nothing was made.", keepCharge: false);
        }

        // ---- What the request checked, again and fresh, before any frame is read or sent. ----
        SendRecheck recheck = await RecheckBeforeSendingAsync(job, [], ct);
        if (recheck.Refusal is { } stopped) return await FailRecheckAsync(job, p, stopped);

        // ---- The video's size, so a crop is at least 256 of its own pixels a side. ----
        if (p.SourceWidth is null
            && (await StoredMediaProbes.LoadAsync(_db, project.Id, [source.StorageKey], ct)).TryGetValue(source.StorageKey, out MediaSourceProbe? probe)
            && probe.Width is > 0 && probe.Height is > 0)
        {
            (p.SourceWidth, p.SourceHeight) = (probe.Width, probe.Height);
        }

        // ---- The stills: asked for once (and again while they do not arrive), then waited for. ----
        int[] timesMs = MediaDerivativeStillTimes.NormalizeToMs(p.AtSeconds);
        IReadOnlyList<OutputStillObject>? stills = await _describer!.FindStillsAsync(project.Id, source, timesMs, ct);
        if (stills is not { Count: > 0 })
        {
            DateTime now = Now;
            if (p.StillsRequestedAt is { } asked && now - (p.WaitingSince ?? asked) > _build.WaitTimeout)
            {
                return await FailAsync(job, p, AvatarBuildCodes.SourceUnavailable,
                    "The video's frames could not be read in time, so nothing was made. Try again in a few minutes.", keepCharge: false);
            }

            if (p.StillsRequestedAt is not { } last || now - last >= StillsAskAgainAfter)
            {
                await _describer.RequestStillsAsync(project.Id, source, timesMs, ct);
                p.StillsRequestedAt = now;
            }

            return await WaitAsync(job, p, RunPollInterval, ct);
        }

        List<OutputStillObject> chosen = ChooseStills(stills, AvatarBuildService.MaxExtractionMoments);
        if (p.Moments is null)
        {
            p.Moments = [.. chosen.Select(s => new AvatarBuildMoment { AtSeconds = Math.Round(s.AtSec, 3) })];
            job.Total = p.Moments.Count;
            p.WaitingSince = null;
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
        }

        // ---- The object in each still, when the request allowed looking for it. ----
        ResolvedImageAnalysisProvider? segmenter = p.Segment ? await SegmenterAsync(ct) : null;

        // The stills of a video that is still an output's bytes (a render) are shown to the segmentation model only with
        // a current verdict for that video, read again here (the request read it too; the verdict may have changed since).
        // A refusal sends nothing more and ends the job: what a resumed job already metered is charged, the rest released.
        if (segmenter is not null && p.Moments.Any(m => !m.Looked)
            && await AvatarBuildService.StoredCopyRefusalAsync(_storedCopies, project.Id, source.StorageKey, ct) is { } held)
        {
            _logger.LogInformation("Avatar build {JobId}: the video's frames are not sent to the segmentation model ({Code})", job.Id, held.Code);
            return await FailAsync(job, p, held.Code, held.Message, keepCharge: p.MeteredMillis > 0);
        }

        // Every still is checked for a person BEFORE any still is sent to the segmentation model. A person in one of them
        // refuses the whole extraction, like a person in an output frame does; a check that cannot answer waits, then times out.
        if (segmenter is not null && p.Moments.Any(m => !m.Looked))
        {
            (AvatarRefusal? shown, bool retry) = await CheckStillsForPeopleAsync(job, p, project, p.Moments, chosen, ct);
            if (shown is not null && !retry) return await FailAsync(job, p, shown.Code, shown.Message, keepCharge: p.MeteredMillis > 0);
            if (shown is not null)
            {
                if (Now - (p.WaitingSince ?? Now) >= _build.ChecksTimeout)
                {
                    return await FailAsync(job, p, AvatarBuildCodes.ChecksTimedOut,
                        "The frames could not be checked for people in time, so nothing was sent and nothing was made. Try again in a few minutes.",
                        keepCharge: p.MeteredMillis > 0);
                }

                return await WaitAsync(job, p, ChecksPollInterval, ct);
            }
        }

        bool fellBack = false;
        for (int i = 0; i < p.Moments.Count && i < chosen.Count; i++)
        {
            AvatarBuildMoment moment = p.Moments[i];
            if (moment.Looked) continue;
            if (await IsCancelledAsync(job, ct)) return await StopCancelledAsync(job, p);
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);

            if (segmenter is null)
            {
                moment.Found = null;
            }
            else
            {
                (bool? found, NormalizedBox? box, int stillWidth, int stillHeight) = await LookAsync(job, p, project, segmenter, chosen[i], ct);
                moment.Found = found;
                fellBack |= found is null;
                if (found == true && box is not null
                    && ExtractionCrop.Compute(box, p.SourceWidth ?? stillWidth, p.SourceHeight ?? stillHeight) is { } crop)
                {
                    (moment.X, moment.Y, moment.Width, moment.Height) = (crop.X, crop.Y, crop.W, crop.H);
                }
            }

            moment.Looked = true;
            await RecordMeteredAsync(job, p);
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
        }

        if (segmenter is null)
        {
            p.Note = p.Segment
                ? "The object could not be looked for in the frames (the segmentation model is not available now), so whole frames were used."
                : "The object was not looked for in the frames (no segmentation model is set up, or sending the frames to one was not allowed), so whole frames were used.";
        }
        else if (fellBack)
        {
            p.Note = "The object could not be looked for in some frames, so those frames were used whole.";
        }

        return await AdvanceAsync(job, p, AvatarBuildStates.Cutting, ct);
    }

    /// <summary>
    /// Shows each still that has not been looked at to the Vision provider, before any of them is sent to the
    /// segmentation model. A person is a refusal (<c>object_reference_shows_person</c>) that ends the job; a check that
    /// could not answer (an unavailable provider, an unreadable still) is <c>Retry</c> (fail closed: nothing is sent).
    /// </summary>
    private async Task<(AvatarRefusal? Refusal, bool Retry)> CheckStillsForPeopleAsync(
        AvatarBuildJob job, AvatarBuildParams p, Project project, List<AvatarBuildMoment> moments, List<OutputStillObject> chosen, CancellationToken ct)
    {
        if (_checks is null) return (AvatarReferenceChecks.VisionRequired(), false);
        for (int i = 0; i < moments.Count && i < chosen.Count; i++)
        {
            if (moments[i].Looked) continue;
            string label = $"The frame at {moments[i].AtSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s";
            byte[]? bytes = await ReadBoundedAsync(project.Id, chosen[i].StorageKey, ProjectAvatarLimits.MaxImageBytes, ct);
            if (bytes is null) return (AvatarRefusal.Unavailable("OBJECT_PERSON_CHECK_UNAVAILABLE", $"{label} could not be read to check it for people."), true);

            AvatarRefusal? refused = await _checks.CheckPictureAsync(job.OrganizationId, job.ProjectId, bytes, label, null, ct);
            if (refused is null) continue;
            return (refused, refused.Code == "OBJECT_PERSON_CHECK_UNAVAILABLE");
        }

        return (null, false);
    }

    /// <summary>
    /// One still shown to the segmentation model: true and a box when it found the object, false when it did not (the
    /// moment is dropped), null when the call failed (the frame is used whole). What the call metered joins the job.
    /// </summary>
    private async Task<(bool? Found, NormalizedBox? Box, int StillWidth, int StillHeight)> LookAsync(
        AvatarBuildJob job, AvatarBuildParams p, Project project, ResolvedImageAnalysisProvider segmenter, OutputStillObject still, CancellationToken ct)
    {
        byte[]? bytes = await ReadBoundedAsync(project.Id, still.StorageKey, ProjectAvatarLimits.MaxImageBytes, ct);
        (int stillWidth, int stillHeight) = bytes is not null && ImageHeader.TryReadDisplaySize(bytes, out int w, out int h) ? (w, h) : (0, 0);
        if (_presignedUrls is null || _analysisClients is null) return (null, null, stillWidth, stillHeight);

        try
        {
            string url = await _presignedUrls.CreateGetAsync(project.Id, still.StorageKey, StillLinkTtl);
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(SegmentationTimeout);
            SegmentationResult result;
            long metered = 0;
            using (UsageScope.Begin(job.OrganizationId, job.ProjectId, null, null, job.RequestedByUserId, UsageSource.Editor))
            {
                long before = UsageScope.RunningCreditsMillis;
                try
                {
                    result = await _analysisClients.Get(segmenter).SegmentAsync(
                        new SegmentationRequest(segmenter.ModelName, url, Prompt: job.ObjectDescription), bounded.Token);
                }
                finally
                {
                    // What a call metered joins the job whether it answered or not.
                    metered = Math.Max(0, UsageScope.RunningCreditsMillis - before);
                    p.MeteredMillis += metered;
                }
            }

            // A billed call that answered is never free: usage the meter missed is charged at what was held for it.
            if (metered <= 0 && segmenter.IsPlatformManaged && _pictures.BillingEnabled) p.MeteredMillis += p.PerStillMillis;

            SegmentedMask? best = result.AllMasks.OrderByDescending(m => m.Score ?? 0).FirstOrDefault();
            NormalizedBox? box = best is null
                ? null
                : ExtractionCrop.FromCentre(best.BoxCxCyWh) ?? ExtractionCrop.FromMask(best.Matte?.Bytes ?? best.Cutout.Bytes);
            return box is null ? (false, null, stillWidth, stillHeight) : (true, box, stillWidth, stillHeight);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The vendor's own words stay in the log; this frame is used whole.
            _logger.LogWarning(ex, "Looking for the object of avatar build {JobId} in a still failed", job.Id);
            return (null, null, stillWidth, stillHeight);
        }
    }

    /// <summary>At most <paramref name="count"/> stills, evenly spread over the set (in time order).</summary>
    internal static List<OutputStillObject> ChooseStills(IReadOnlyList<OutputStillObject> stills, int count)
    {
        List<OutputStillObject> ordered = [.. stills.OrderBy(s => s.AtSec)];
        if (ordered.Count <= count) return ordered;
        return [.. Enumerable.Range(0, count).Select(i => ordered[(int)Math.Floor((i + 0.5) * ordered.Count / count)])];
    }

    // ------------------------------------------------------------------ stage: cutting (an extraction)

    /// <summary>
    /// Stage 2 of an extraction: one free <c>ExtractFrame</c> per kept moment on the source video, at its full size,
    /// cropped to the box (the whole frame when there is none), in a hidden run; each frame becomes an ordinary
    /// project file under <c>userFiles/objects/{name}/</c>. None usable = <c>OBJECT_NOT_FOUND_IN_VIDEO</c>.
    /// </summary>
    private async Task<Step> RunCuttingAsync(AvatarBuildJob job, AvatarBuildParams p, CancellationToken ct)
    {
        Project? project = await _db.Projects.FirstOrDefaultAsync(pr => pr.Id == job.ProjectId, ct);
        if (project is null) return await FailAsync(job, p, AvatarBuildCodes.BuildFailed, "The project is gone.", keepCharge: false);

        List<(AvatarBuildMoment Moment, int Index)> kept = [.. (p.Moments ?? []).Select((m, i) => (m, i)).Where(t => t.m.Found != false)];
        if (kept.Count == 0)
        {
            return await FailAsync(job, p, AvatarBuildCodes.ObjectNotFoundInVideo,
                "The object was not found in any frame of the video, so no avatar was made. Describe it differently, or choose other moments.",
                keepCharge: true);
        }

        if (!AvatarBuildService.TryParseMediaRef(job.SourceMediaRef, out string refKind, out Guid refId) || _runs is not { Available: true })
        {
            return await FailAsync(job, p, AvatarBuildCodes.SourceUnavailable,
                "The video can no longer be read, so nothing was made.", keepCharge: true);
        }

        VideoSourceRef sourceRef = refKind == "file"
            ? new VideoSourceRef(VideoSourceKind.ProjectFile, refId)
            : new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: refId);
        List<RunItem> cuts = [.. kept.Select(t => new RunItem(
            AvatarBuildItem.CutKey(t.Index),
            new AvatarBuildRunStep(
                $"Frame at {t.Moment.AtSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s",
                StepType.ExtractFrame,
                JsonSerializer.Serialize(
                    new ExtractFrameStepConfig(
                        sourceRef, FrameAt.Time, AtSec: t.Moment.AtSeconds, Format: "png",
                        Crop: t.Moment is { X: { } x, Y: { } y, Width: { } cw, Height: { } chh } ? new NormalizedCrop(x, y, cw, chh) : null,
                        RegisterProjectFile: false),
                    ExtractFrameStepConfig.JsonOptions))))];
        if (await DriveRunsAsync(job, p, AvatarBuildRun.Cuts, cuts, $"Object frames: {job.Name}", ct) is { } waiting) return waiting;

        // ---- Each frame becomes an ordinary project file; the first one shows the object from the front. ----
        string directory = AvatarBuildFiles.ObjectDirectory(job.Name);
        string stem = AvatarBuildFiles.Segment(job.Name, "object");
        foreach ((AvatarBuildMoment _, int index) in kept)
        {
            string key = AvatarBuildItem.MomentKey(index);
            if (p.Find(key) is not null) continue;

            if (p.RunOutcomes.GetValueOrDefault(AvatarBuildItem.CutKey(index)) is not { Ok: true, StepResultId: { } cut }
                || await OutputKeyAsync(job.ProjectId, cut, ct) is not { } frameKey)
            {
                NoteItem(p, key, AvatarBuildItem.Failed, null, null, index);
                continue;
            }

            Guid fileId = AvatarBuildParams.FileIdFor(job.Id, key);
            AvatarBuildFileOutcome copied = await _files.CopyAsync(
                project, job.RequestedByUserId, fileId, directory,
                stem + "-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png", "image/png",
                frameKey, AvatarBuildFiles.ExtractionSourceKind, ct);
            if (copied.OverQuota is { } overQuota) return await FailAsync(job, p, overQuota.Code, overQuota.Message, keepCharge: true);
            if (copied.File is null)
            {
                NoteItem(p, key, AvatarBuildItem.Failed, null, null, index);
                continue;
            }

            bool first = !p.Items.Any(i => i.Key.StartsWith("moment:", StringComparison.Ordinal) && i.Outcome == AvatarBuildItem.Made);
            NoteItem(p, key, AvatarBuildItem.Made, copied.File.Id, (first ? AvatarReferenceRole.Front : AvatarReferenceRole.Angle).ToString(), index);
            job.Done++;
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
        }

        return await AdvanceAsync(job, p, AvatarBuildStates.Creating, ct);
    }

    /// <summary>The extraction's video as the stills path addresses it, or null when it can no longer be read.</summary>
    private async Task<OutputSource?> ExtractionSourceAsync(AvatarBuildJob job, CancellationToken ct)
    {
        if (_describer is null || !AvatarBuildService.TryParseMediaRef(job.SourceMediaRef, out string refKind, out Guid refId)) return null;
        OutputSource? source = await _describer.FindSourceAsync(job.ProjectId, refId, ct);
        return source is not null
               && string.Equals(source.Kind, refKind == "file" ? "file" : "render", StringComparison.Ordinal)
               && source.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            ? source
            : null;
    }

    /// <summary>The default segmentation model, or null when there is none or it cannot be used here (whole frames then).</summary>
    private async Task<ResolvedImageAnalysisProvider?> SegmenterAsync(CancellationToken ct)
    {
        if (_providers is null || _presignedUrls is null || _analysisClients is null) return null;
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
            _logger.LogInformation(ex, "No usable segmentation model for an object extraction; whole frames will be used");
            return null;
        }
    }

    // ------------------------------------------------------------------ hidden runs

    /// <summary>
    /// Drives the hidden runs of one purpose until every item has an outcome: queues a run of the items that have none
    /// (a run's ids are derived from the job, so a restart finds it again), waits for it, and reads what each of its
    /// steps made. The engine stops a run at its first failed step, so the items a run never reached go in the next
    /// run; a run that ended with no outcome at all fails its items (they would only fail again). Answers null when every
    /// item has an outcome, else the step the job is at now (it waits, or it ended).
    /// </summary>
    private async Task<Step?> DriveRunsAsync(
        AvatarBuildJob job, AvatarBuildParams p, string purpose, IReadOnlyList<RunItem> items, string runName, CancellationToken ct)
    {
        while (true)
        {
            AvatarBuildRun? open = p.Runs.LastOrDefault(r => r.Purpose == purpose && !r.Read);
            if (open is not null)
            {
                if (!open.Queued)
                {
                    List<AvatarBuildRunStep> steps = [.. open.Items.Select(k => items.First(i => i.Key == k).Step)];
                    PlanGateViolation? busy = await _runs!.QueueAsync(job, open, runName, steps, ct);
                    if (busy is not null)
                    {
                        if (p.WaitingSince is { } since && Now - since > _build.WaitTimeout)
                        {
                            Settle(p, open, AvatarBuildCodes.RunNotStarted);
                            continue;
                        }

                        return await WaitAsync(job, p, BusyRetryInterval, ct);
                    }

                    open.Queued = true;
                    open.QueuedAt = Now;
                    job.ExecutionId = open.ExecutionId;
                    p.WaitingSince = null;
                    return await WaitAsync(job, p, RunPollInterval, ct);
                }

                AvatarBuildRunState state = await _runs!.ReadAsync(job.ProjectId, open.ExecutionId, ct);
                if (!state.Ended)
                {
                    TimeSpan limit = purpose == AvatarBuildRun.Takes ? _build.WaitTimeout * Math.Max(1, open.Items.Count) : _build.WaitTimeout;
                    if (open.QueuedAt is { } queuedAt && Now - queuedAt > limit)
                    {
                        await _runs.StopAsync(open.ExecutionId, job.RequestedByUserId);
                        Settle(p, open, AvatarBuildCodes.RunTimedOut);
                        continue;
                    }

                    return await WaitAsync(job, p, RunPollInterval, ct);
                }

                int settled = 0;
                for (int i = 0; i < open.Items.Count; i++)
                {
                    string key = open.Items[i];
                    if (state.Steps.TryGetValue(i + 1, out AvatarBuildStepOutcome? outcome)
                        && outcome.Status == StepStatus.Completed && !string.IsNullOrEmpty(outcome.OutputStorageKey))
                    {
                        p.RunOutcomes[key] = new AvatarBuildRunOutcome { Ok = true, StepResultId = outcome.StepResultId };
                        settled++;
                    }
                    else if (outcome is not null && outcome.Status == StepStatus.Failed)
                    {
                        p.RunOutcomes[key] = new AvatarBuildRunOutcome { Ok = false, FailureCode = outcome.FailureCode };
                        settled++;
                    }
                }

                // Stopped, or a run that reached no step at all: what it did not reach is not tried again.
                if (state.Status == ExecutionStatus.Cancelled || settled == 0)
                    Settle(p, open, state.Status == ExecutionStatus.Cancelled ? AvatarBuildCodes.Cancelled : AvatarBuildCodes.RunNotStarted);
                open.Read = true;
                p.WaitingSince = null;
                if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
                continue;
            }

            List<RunItem> pending = [.. items.Where(i => !p.RunOutcomes.ContainsKey(i.Key))];
            if (pending.Count == 0) return null;

            int index = p.Runs.Count(r => r.Purpose == purpose);
            var run = new AvatarBuildRun
            {
                Purpose = purpose,
                Index = index,
                WorkflowId = AvatarBuildParams.FileIdFor(job.Id, $"run:{purpose}:{index}:workflow"),
                ExecutionId = AvatarBuildParams.FileIdFor(job.Id, $"run:{purpose}:{index}:execution"),
                Items = [.. pending.Select(i => i.Key)]
            };
            p.Runs.Add(run);
            // Noted before it is queued, so a restart queues this run (same ids) and never a second one.
            if (!await SaveProgressAsync(job, p, ct)) return await StopCancelledAsync(job, p);
        }
    }

    /// <summary>Every item of <paramref name="run"/> without an outcome is noted as not made, with <paramref name="code"/>.</summary>
    private static void Settle(AvatarBuildParams p, AvatarBuildRun run, string code)
    {
        foreach (string key in run.Items.Where(k => !p.RunOutcomes.ContainsKey(k)))
            p.RunOutcomes[key] = new AvatarBuildRunOutcome { Ok = false, FailureCode = code };
        run.Read = true;
    }

    /// <summary>Stops the job's hidden runs that may still be going (a cancelled job). Best effort.</summary>
    private async Task StopOpenRunsAsync(AvatarBuildJob job, AvatarBuildParams p)
    {
        if (_runs is null) return;
        foreach (AvatarBuildRun run in p.Runs.Where(r => r.Queued && !r.Read))
        {
            await _runs.StopAsync(run.ExecutionId, job.RequestedByUserId);
            run.Read = true;
        }
    }

    /// <summary>The stored object of one of this project's step results, when it may be read (not held).</summary>
    private async Task<string?> OutputKeyAsync(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        string? key = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId)
            .Select(r => r.OutputStorageKey)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(key) || !key.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)) return null;
        return await OutputGate.IsResultHeldAsync(_db, stepResultId, ct) ? null : key;
    }

    private static void NoteItem(AvatarBuildParams p, string key, string outcome, Guid? fileId, string? role, int ordinal) =>
        p.Items.Add(new AvatarBuildItem { Key = key, Outcome = outcome, FileId = fileId, Role = role, Ordinal = ordinal });
}
