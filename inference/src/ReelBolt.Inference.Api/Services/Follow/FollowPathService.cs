using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;

namespace ReelBolt.Inference.Api.Services.Follow;

/// <summary>
/// "Follow this subject", step two: computes the camera path of a tracked subject for one editor clip and answers it as
/// <c>transform.positionX/positionY/scale</c> keys anchored to SOURCE time. Pure compute: it reads the timeline and the tracker
/// artifact, writes nothing, bumps no revision and spends no credits.
/// </summary>
public sealed partial class FollowPathService
{
    /// <summary>Most boxes of one track the computation accepts.</summary>
    public const int MaxTrackBoxes = 100_000;

    /// <summary>Longest source range of a clip the computation accepts, seconds.</summary>
    public const double MaxRangeSeconds = 3600;

    [GeneratedRegex("^(r|g)([0-9]{1,6})$")]
    private static partial Regex TrackIdPattern();

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    public FollowPathService(InferenceApiDbContext db, IFileStorageService storage, ILogger<FollowPathService> logger, TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private static ObjectAnalysisResult<FollowResponse> Refuse(int status, string code, string message) =>
        ObjectAnalysisResult<FollowResponse>.Refuse(status, new { code, message });

    public async Task<ObjectAnalysisResult<FollowResponse>> ComputeAsync(
        Guid projectId, Guid timelineId, FollowRequestBody? body, CancellationToken ct)
    {
        if (body is null || body.ItemId == Guid.Empty || body.Track is null || body.Track.AnalysisStepResultId == Guid.Empty
            || string.IsNullOrWhiteSpace(body.Track.TrackId) || (body.Revision is { } rev && rev < 1))
        {
            return Refuse(StatusCodes.Status400BadRequest, "FOLLOW_REQUEST_INVALID", "Choose a clip and a tracked subject to follow.");
        }

        if (!TryEnum(body.Tightness, FollowTightness.Medium, out FollowTightness tightness)
            || !TryEnum(body.Smoothness, FollowSmoothness.Normal, out FollowSmoothness smoothness)
            || !TryEnum(body.OnLost, FollowOnLost.Hold, out FollowOnLost onLost))
        {
            return Refuse(StatusCodes.Status400BadRequest, "FOLLOW_REQUEST_INVALID",
                "Tightness must be Loose, Medium or Tight; smoothness Gentle, Normal or Snappy; onLost Hold or Widen.");
        }

        // ---- the document: latest, or the named revision's snapshot
        EditTimelineEntity? row = await _db.EditTimelines.AsNoTracking().FirstOrDefaultAsync(t => t.Id == timelineId && t.ProjectId == projectId, ct);
        if (row is null) return ObjectAnalysisResult<FollowResponse>.Refuse(StatusCodes.Status404NotFound);

        int revision = row.Revision;
        string documentJson = row.DocumentJson;
        if (body.Revision is { } wanted && wanted != row.Revision)
        {
            DateTime now = _clock.GetUtcNow().UtcDateTime;
            string? snapshot = await _db.EditTimelineSnapshots.AsNoTracking()
                .Where(s => s.TimelineId == timelineId && s.Revision == wanted && (s.ExpiresAt == null || s.ExpiresAt > now))
                .Select(s => s.DocumentJson)
                .FirstOrDefaultAsync(ct);
            if (snapshot is null) return ObjectAnalysisResult<FollowResponse>.Refuse(StatusCodes.Status404NotFound);
            revision = wanted;
            documentJson = snapshot;
        }

        EditTimeline? document;
        try
        {
            document = JsonSerializer.Deserialize<EditTimeline>(documentJson, EditTimelineJson.Options);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_TIMELINE_UNREADABLE", "This timeline could not be read.");

        // ---- the clip
        MediaClip? clip = document.Tracks.Concat((document.Sequences ?? []).SelectMany(s => s.Tracks))
            .SelectMany(t => t.Items)
            .OfType<MediaClip>()
            .FirstOrDefault(c => c.Id == body.ItemId);
        if (clip is null) return Refuse(StatusCodes.Status404NotFound, "FOLLOW_CLIP_NOT_FOUND", "That clip is not on this timeline.");

        if (ClipConflict(clip) is { } conflict) return conflict;

        // ---- the track
        string? key = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.Id == body.Track.AnalysisStepResultId
                && r.WorkflowExecution.ProjectId == projectId
                && r.WorkflowStep.StepType == StepType.VideoAnalyze)
            .Select(r => r.ArtifactStorageKey)
            .FirstOrDefaultAsync(ct);
        if (key is null
            || !key.StartsWith(SegmentationTrackIdLookup.ArtifactPrefix(projectId), StringComparison.Ordinal)
            || key.Contains("..", StringComparison.Ordinal))
        {
            return ObjectAnalysisResult<FollowResponse>.Refuse(StatusCodes.Status404NotFound);
        }

        if (await OutputGate.IsResultHeldAsync(_db, body.Track.AnalysisStepResultId, ct))
        {
            Microsoft.AspNetCore.Mvc.ObjectResult held = OutputGate.Unavailable();
            return ObjectAnalysisResult<FollowResponse>.Refuse(held.StatusCode ?? StatusCodes.Status404NotFound, held.Value);
        }

        Match idMatch = TrackIdPattern().Match(body.Track.TrackId!.Trim());
        if (!idMatch.Success) return TrackNotFound();

        VideoAnalysisArtifact? artifact;
        try
        {
            await using Stream stream = await _storage.DownloadAsync(projectId, key, ct);
            artifact = await SegmentationTrackIdLookup.ParseAsync(stream, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the video-analysis artifact of project {ProjectId}", projectId);
            return Refuse(StatusCodes.Status503ServiceUnavailable, "FOLLOW_TRACK_UNREADABLE", "We could not read that track right now. Try again in a moment.");
        }

        if (artifact is null) return TrackNotFound();

        string trackId = idMatch.Value;
        IReadOnlyList<SubjectSample> samples;
        int sourceIndex;
        if (idMatch.Groups[1].Value == "r")
        {
            VideoInsertRegionTrack? region = (artifact.InsertRegions ?? [])
                .FirstOrDefault(t => t.Id == trackId && string.Equals(t.Method, "feature", StringComparison.OrdinalIgnoreCase));
            if (region is null) return TrackNotFound();
            if ((region.Keyframes?.Count ?? 0) > MaxTrackBoxes) return TooLarge();
            samples = SubjectPathAdapters.FromInsertTrack(region);
            sourceIndex = region.SourceIndex;
        }
        else
        {
            VideoSegmentationTrack? segmentation = (artifact.SegmentationTracks ?? []).FirstOrDefault(t => t.Id == trackId);
            if (segmentation is null) return TrackNotFound();
            if ((segmentation.BoxKeyframes?.Count ?? 0) > MaxTrackBoxes) return TooLarge();
            samples = SubjectPathAdapters.FromSegmentationTrack(segmentation);
            sourceIndex = segmentation.SourceIndex;
        }

        if (!samples.Any(s => s.Present))
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_TRACK_EMPTY", "That track has no usable positions to follow.");

        // ---- the source must be the clip's own media
        VideoAnalysisSourceInfo? source = artifact.Sources?.FirstOrDefault(s => s.SourceIndex == sourceIndex);
        bool sameSource = source is not null && (clip.AssetRef.Kind == AssetRefKind.ProjectFile
            ? clip.AssetRef.ProjectFileId is { } fileId && source.ProjectFileId == fileId
            : clip.AssetRef.StepResultId is { } resultId && source.StepResultId == resultId);
        if (!sameSource)
        {
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_TRACK_OTHER_SOURCE",
                "That track was made from a different video than this clip.");
        }

        // The artifact's geometry is in display orientation (after the decoder's rotation), which is what the renderer fits.
        VideoAnalysisMedia media = source!.Media;
        int sw = media.Width, sh = media.Height;
        EditTimelineSettings settings = document.Settings;
        if (sw <= 0 || sh <= 0 || settings.Width <= 0 || settings.Height <= 0 || settings.Rate.Num <= 0 || settings.Rate.Den <= 0)
        {
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_SIZE_UNKNOWN", "The size of this video or timeline is not known, so it cannot be followed.");
        }

        // ---- the clip's used source range (constant speed; reverse reads the same span backwards)
        double fps = (double)settings.Rate.Num / settings.Rate.Den;
        double rate = Math.Clamp(clip.PlaybackRate > 0 && double.IsFinite(clip.PlaybackRate) ? clip.PlaybackRate : 1.0, 0.1, 10.0);
        double fromSec = Math.Max(0, clip.SourceIn) / fps;
        double toSec = (Math.Max(0, clip.SourceIn) + (clip.RecordOut - clip.RecordIn) * rate) / fps;
        if (!(toSec > fromSec))
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_RANGE_INVALID", "This clip has no length to follow a subject through.");
        if (toSec - fromSec > MaxRangeSeconds)
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_RANGE_TOO_LONG", "This clip is longer than 1 hour, which is more than we can follow a subject through.");

        // ---- compute
        EditorFollowFit fit = EditorFollowTransform.Fit(sw, sh, settings.Width, settings.Height);
        FollowOptions options = new(
            tightness, smoothness, onLost, body.AllowSoftZoom ?? false, fit.Framing,
            SourceHeightPx: sh, OutputHeightPx: settings.Height, MaxKeys: EditTimelineValidator.MaxKeyframes, MaxZoomLimit: fit.MaxZoomLimit);
        CameraPathResult path;
        try
        {
            path = SubjectCameraPath.Compute(samples, fromSec, toSec, options);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "A follow path could not be computed for timeline {TimelineId}", timelineId);
            return Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_FIT_UNSUPPORTED", "That picture and canvas shape cannot be followed.");
        }

        List<FollowKeyResponse> keys = [];
        int clampCount = path.ClampCount;
        bool rangeClamped = false;
        foreach (CameraKey k in path.Keys)
        {
            EditorTransformValue v = EditorFollowTransform.ToTransform(fit, k, out bool clamped);
            if (clamped) { rangeClamped = true; clampCount++; }
            keys.Add(new FollowKeyResponse(k.TimeSec, v.PositionX, v.PositionY, v.Scale));
        }

        List<string> warnings = [];
        if (rangeClamped)
        {
            warnings.Add("Some framing was beyond what the editor can express and was limited, so the subject may sit off-centre or partly out of frame in places.");
        }

        if (path.LostSpans.Count > 0)
        {
            warnings.Add(onLost == FollowOnLost.Widen
                ? "The subject was lost for part of this clip; the camera widens to the full frame there."
                : "The subject was lost for part of this clip; the camera holds its last framing there.");
        }

        if (path.ZoomCapLimited)
        {
            warnings.Add("The subject would need more zoom than the limit for a sharp picture, so it is framed wider there.");
        }

        return ObjectAnalysisResult<FollowResponse>.Ok(new FollowResponse(
            revision,
            clip.Id,
            keys,
            new FollowAssumedResponse(settings.Width, settings.Height, sw, sh),
            path.ZoomCap,
            path.ZoomCapLimited,
            path.LostSpans.Select(s => new FollowLostSpanResponse(s.FromSec, s.ToSec)).ToList(),
            clampCount,
            warnings));
    }

    /// <summary>A clip whose picture is already reframed, cropped, stabilized or retimed non-uniformly cannot be followed.</summary>
    public static ObjectAnalysisResult<FollowResponse>? ClipConflict(MediaClip clip)
    {
        if (clip.Crop is { } crop && (crop.Left > 0 || crop.Top > 0 || crop.Right > 0 || crop.Bottom > 0))
        {
            return Refuse(StatusCodes.Status409Conflict, "FOLLOW_CLIP_CONFLICT",
                "This clip is cropped. Remove the crop first, then follow the subject.");
        }

        if (clip.Reframe is not null)
        {
            return Refuse(StatusCodes.Status409Conflict, "FOLLOW_CLIP_CONFLICT",
                "This clip already uses auto reframe. Turn it off first, then follow the subject.");
        }

        if (clip.Stabilize)
        {
            return Refuse(StatusCodes.Status409Conflict, "FOLLOW_CLIP_CONFLICT",
                "This clip is stabilized. Turn stabilization off first, then follow the subject.");
        }

        if (clip.Hold)
        {
            return Refuse(StatusCodes.Status409Conflict, "FOLLOW_CLIP_SPEED",
                "This clip is a freeze frame, so there is nothing to follow.");
        }

        if (!double.IsFinite(clip.PlaybackRate) || clip.PlaybackRate <= 0)
        {
            return Refuse(StatusCodes.Status409Conflict, "FOLLOW_CLIP_SPEED",
                "This clip's speed is not a plain constant, so a subject cannot be followed on it.");
        }

        return null;
    }

    private static ObjectAnalysisResult<FollowResponse> TrackNotFound() =>
        Refuse(StatusCodes.Status404NotFound, "FOLLOW_TRACK_NOT_FOUND", "That tracked subject could not be found.");

    private static ObjectAnalysisResult<FollowResponse> TooLarge() =>
        Refuse(StatusCodes.Status422UnprocessableEntity, "FOLLOW_TRACK_TOO_LARGE", "That track is too long to follow. Track a shorter stretch.");

    private static bool TryEnum<T>(string? raw, T fallback, out T value) where T : struct, Enum
    {
        value = fallback;
        return string.IsNullOrWhiteSpace(raw) || Enum.TryParse(raw.Trim(), ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}
