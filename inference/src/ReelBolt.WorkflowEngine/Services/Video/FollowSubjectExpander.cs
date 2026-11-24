using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One <c>followSubject</c> entry's outcome, as reported in the step output and the EDL.</summary>
internal sealed record FollowSubjectReportEntry(
    string TrackId,
    int SourceIndex,
    bool Applied,
    string? Reason,
    double ZoomCap,
    bool ZoomCapLimited,
    IReadOnlyList<FollowLostSpan> LostSpans,
    int ClampCount,
    int Keyframes,
    int Reframes,
    bool FramingFixed = false);

/// <summary>The result of <see cref="FollowSubjectExpander.Expand"/>: the produced reframes, or a refusal.</summary>
internal sealed record FollowSubjectExpansion(
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<VideoReframe> Reframes,
    IReadOnlyList<FollowSubjectReportEntry> Report)
{
    internal static FollowSubjectExpansion Fail(string code, string message) => new(code, message, [], []);

    /// <summary>The <c>followSubject</c> report node.</summary>
    internal JsonArray ToNode()
    {
        var array = new JsonArray();
        foreach (FollowSubjectReportEntry e in Report)
        {
            var lost = new JsonArray();
            foreach (FollowLostSpan s in e.LostSpans)
                lost.Add(new JsonObject { ["fromSec"] = s.FromSec, ["toSec"] = s.ToSec });
            var node = new JsonObject
            {
                ["trackId"] = e.TrackId,
                ["sourceIndex"] = e.SourceIndex,
                ["applied"] = e.Applied
            };
            if (e.Reason is not null)
                node["reason"] = e.Reason;
            node["zoomCap"] = e.ZoomCap;
            node["zoomCapLimited"] = e.ZoomCapLimited;
            node["lostSpans"] = lost;
            node["clampCount"] = e.ClampCount;
            node["keyframes"] = e.Keyframes;
            node["reframes"] = e.Reframes;
            // Only when true, so a follow whose camera can move keeps the report's exact shape.
            if (e.FramingFixed)
                node["framingFixed"] = true;
            array.Add(node);
        }

        return array;
    }

    /// <summary>
    /// The compile warnings for entries that were applied but cannot move the picture: the zoom cap held
    /// the camera at 1 and the clip's fit shows its whole frame (padded or already the canvas shape), so
    /// there is nothing to pan across. <c>{ code, message }</c>, one per such entry; empty otherwise.
    /// </summary>
    internal IEnumerable<JsonObject> BuildWarnings()
    {
        foreach (FollowSubjectReportEntry e in Report.Where(e => e.Applied && e.FramingFixed))
        {
            yield return new JsonObject
            {
                ["code"] = "follow_subject_cannot_move",
                ["message"] =
                    $"Following {e.TrackId} on clip {e.SourceIndex + 1} cannot move the picture: the clip is shown whole in this shape " +
                    "and zooming in further would make it blurry, so it stays still. Set the output fit to Crop or Subject for this clip, " +
                    "or turn on soft zoom (allowSoftZoom)."
            };
        }
    }
}

/// <summary>
/// Turns <see cref="VideoCompileStepConfig.FollowSubject"/> into ordinary <see cref="VideoReframe"/>
/// spans through the follow core. Pure apart from its inputs: the request names a track and words,
/// every number comes from the analysis artifact and <see cref="SubjectCameraPath"/>.
/// </summary>
internal static class FollowSubjectExpander
{
    /// <param name="config">The compile config (hand-written Reframes count against the shared caps).</param>
    /// <param name="artifact">The analysis artifact holding the tracks.</param>
    /// <param name="sourceMedia">A source's own probed media (display size).</param>
    /// <param name="canvas">The output canvas.</param>
    /// <param name="coversSource">Whether a source is covered (cropped) into the canvas by ITS OWN fit (per-source
    /// OutputFit, Pad, subject pad); false frames the whole source.</param>
    internal static FollowSubjectExpansion Expand(
        VideoCompileStepConfig config,
        VideoAnalysisArtifact artifact,
        Func<int, VideoAnalysisMedia> sourceMedia,
        VideoAnalysisMedia canvas,
        Func<int, bool> coversSource)
    {
        if (config.FollowSubject is not { Count: > 0 } requests)
            return new FollowSubjectExpansion(null, null, [], []);

        IReadOnlyList<VideoReframe> hand = config.Reframes ?? [];
        int keysUsed = hand.Sum(r => r?.Keyframes?.Count ?? 0);
        int reframesUsed = hand.Count;
        var perSource = new Dictionary<int, int>();
        foreach (VideoReframe r in hand.Where(r => r is not null))
            perSource[r.SourceIndex] = perSource.GetValueOrDefault(r.SourceIndex) + 1;

        var taken = new Dictionary<int, List<(double From, double To)>>();
        var produced = new List<VideoReframe>();
        var report = new List<FollowSubjectReportEntry>();

        foreach (FollowSubjectRequest request in requests)
        {
            if (!TryResolve(artifact, request.TrackId, out int trackSource, out double trackStart, out double trackEnd, out IReadOnlyList<SubjectSample>? samples))
                return FollowSubjectExpansion.Fail(FollowSubjectCodes.TrackNotFound, $"followSubject track '{request.TrackId}' is not in the analysis.");
            if (request.SourceIndex is { } wanted && wanted != trackSource)
                return FollowSubjectExpansion.Fail(FollowSubjectCodes.TrackNotFound, $"followSubject track '{request.TrackId}' belongs to source {trackSource}, not source {wanted}.");
            if (!samples!.Any(s => s.Present))
                return FollowSubjectExpansion.Fail(FollowSubjectCodes.TrackEmpty, $"followSubject track '{request.TrackId}' has no usable boxes.");
            if (hand.Any(r => r is not null && r.SourceIndex == trackSource))
                return FollowSubjectExpansion.Fail(FollowSubjectCodes.ConflictsReframe, $"Source {trackSource} has both a followSubject entry and a hand-written Reframes entry; one owner per source.");

            VideoAnalysisMedia source = sourceMedia(trackSource);
            if (source.Width <= 0 || source.Height <= 0 || canvas.Width <= 0 || canvas.Height <= 0)
            {
                report.Add(Skipped(request.TrackId, trackSource, "source_size_unknown"));
                continue;
            }

            // The range is the config's, so it is bounded here: to the source's length when known, else to the track.
            double duration = source.DurationSec > 0 && double.IsFinite(source.DurationSec) ? source.DurationSec : 0;
            double lo = duration > 0 ? 0 : trackStart;
            double hi = duration > 0 ? duration : trackEnd;
            double from = request.FromSec is { } askedFrom && double.IsFinite(askedFrom) ? Math.Clamp(askedFrom, lo, Math.Max(lo, hi)) : lo;
            double to = request.ToSec is { } askedTo
                ? (double.IsFinite(askedTo) ? Math.Min(askedTo, hi) : double.NaN)
                : (duration > 0 ? Math.Max(duration, trackEnd) : trackEnd);
            if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from)
            {
                report.Add(Skipped(request.TrackId, trackSource, "range_empty"));
                continue;
            }

            // Two entries on one source over overlapping ranges would fail REFRAME_OVERLAP downstream: the later one is skipped.
            if (taken.GetValueOrDefault(trackSource) is { } earlier && earlier.Any(t => from < t.To - 1e-9 && t.From < to - 1e-9))
            {
                report.Add(Skipped(request.TrackId, trackSource, "overlap"));
                continue;
            }

            int sourceUsed = perSource.GetValueOrDefault(trackSource);
            int reframeRoom = Math.Min(VideoReframe.MaxReframesTotal - reframesUsed, VideoReframe.MaxReframesPerSource - sourceUsed);
            int keyRoom = Math.Min(VideoReframe.MaxKeyframesTotal - keysUsed, CameraPathReframes.MaxKeysForWorkflow);
            int maxKeys = reframeRoom < 1
                ? 0
                : Math.Min(keyRoom, reframeRoom * (VideoReframe.MaxKeyframes - 1) + 1);
            if (maxKeys < 2)
            {
                report.Add(Skipped(request.TrackId, trackSource, "reframe_budget_exhausted"));
                continue;
            }

            FollowFraming framing = coversSource(trackSource)
                ? FollowFraming.Cover(source.Width / (double)source.Height, canvas.Width / (double)canvas.Height)
                : FollowFraming.Full;
            var options = new FollowOptions(
                request.Tightness, request.Smoothness, request.OnLost, request.AllowSoftZoom, framing,
                SourceHeightPx: source.Height, OutputHeightPx: canvas.Height, MaxKeys: maxKeys);

            CameraPathResult path;
            IReadOnlyList<VideoReframe> spans;
            try
            {
                path = SubjectCameraPath.Compute(samples!, from, to, options);
                // Visible anchor: the core's keyframes are the centre of what the canvas shows.
                spans = CameraPathReframes.ToReframes(path, trackSource, from, to)
                    .Select(s => s with { Anchor = ReframeAnchor.Visible })
                    .ToList();
            }
            catch (ArgumentException)
            {
                report.Add(Skipped(request.TrackId, trackSource, "range_too_long"));
                continue;
            }

            produced.AddRange(spans);
            if (!taken.TryGetValue(trackSource, out List<(double From, double To)>? ranges))
                taken[trackSource] = ranges = [];
            ranges.Add((from, to));
            int keyCount = spans.Sum(s => s.Keyframes.Count);
            keysUsed += keyCount;
            reframesUsed += spans.Count;
            perSource[trackSource] = sourceUsed + spans.Count;
            // Nothing to follow with: no zoom past the cap, and the fit already shows the whole frame.
            bool framingFixed = path.ZoomCap <= 1 + 1e-9 && framing.VisibleWidth >= 1 - 1e-9 && framing.VisibleHeight >= 1 - 1e-9;
            report.Add(new FollowSubjectReportEntry(
                request.TrackId, trackSource, true, null, path.ZoomCap, path.ZoomCapLimited,
                path.LostSpans, path.ClampCount, keyCount, spans.Count, framingFixed));
        }

        return new FollowSubjectExpansion(null, null, produced, report);
    }

    /// <summary>
    /// The sources hand-written <c>Reframes</c> or <c>followSubject</c> entries will frame (a follow entry's
    /// own <c>SourceIndex</c>, else its track's source). Known before the canvas fit is planned, so the fit
    /// never pads a clip a reframe moves across. An unresolvable track adds nothing (the expansion refuses it).
    /// </summary>
    internal static IReadOnlySet<int> ReframedSources(VideoCompileStepConfig config, VideoAnalysisArtifact artifact)
    {
        var sources = new HashSet<int>();
        foreach (VideoReframe r in config.Reframes ?? [])
        {
            if (r is not null)
                sources.Add(r.SourceIndex);
        }

        foreach (FollowSubjectRequest f in config.FollowSubject ?? [])
        {
            if (f is null)
                continue;
            if (f.SourceIndex is { } explicitSource)
                sources.Add(explicitSource);
            else if (f.TrackId is { } id && TryResolve(artifact, id, out int trackSource, out _, out _, out _))
                sources.Add(trackSource);
        }

        return sources;
    }

    private static FollowSubjectReportEntry Skipped(string trackId, int sourceIndex, string reason) =>
        new(trackId, sourceIndex, false, reason, 0, false, [], 0, 0, 0);

    private static bool TryResolve(
        VideoAnalysisArtifact artifact, string trackId, out int sourceIndex, out double startSec, out double endSec,
        out IReadOnlyList<SubjectSample>? samples)
    {
        sourceIndex = 0;
        startSec = 0;
        endSec = 0;
        samples = null;
        if (trackId.Length < 2)
            return false;
        if (trackId[0] == 'r')
        {
            VideoInsertRegionTrack? track = artifact.InsertRegions?.FirstOrDefault(t =>
                t is not null && string.Equals(t.Method, "feature", StringComparison.Ordinal) && string.Equals(t.Id, trackId, StringComparison.Ordinal));
            if (track is null)
                return false;
            (sourceIndex, startSec, endSec) = (track.SourceIndex, track.StartSec, track.EndSec);
            samples = SubjectPathAdapters.FromInsertTrack(track);
            return true;
        }

        if (trackId[0] == 'g')
        {
            VideoSegmentationTrack? track = artifact.SegmentationTracks?.FirstOrDefault(t =>
                t is not null && string.Equals(t.Id, trackId, StringComparison.Ordinal));
            if (track is null)
                return false;
            (sourceIndex, startSec, endSec) = (track.SourceIndex, track.StartSec, track.EndSec);
            samples = SubjectPathAdapters.FromSegmentationTrack(track);
            return true;
        }

        return false;
    }

}
