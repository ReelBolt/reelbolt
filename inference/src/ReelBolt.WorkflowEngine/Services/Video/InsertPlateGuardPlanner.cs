using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Plans PLATE GUARDS: every stretch of output where a tracked chroma plate is on screen but no
/// insert is being composited into it, keyed to a neutral dark screen instead of showing raw
/// green — and reported, so a customer knows it happened. See docs/video-editing.md "Tracking
/// quality on the customer clips".
///
/// <para>
/// <b>Why.</b> The customer's four-clip reel ended on five seconds of raw green: its fourth phone
/// track was dropped (<c>max_inserts_exceeded</c> — <c>MaxInserts</c> defaults to 3), and nothing
/// else ever touched that plate. The other clips flashed green for a few frames each, where the
/// tracker deliberately emits nothing — a phone turning through glare, the first frames of a whip
/// into place, a plate too briefly seen to be a track (<see
/// cref="VideoInsertRegionTrack.UncoveredPlateSpans"/>). An insert can only be as good as its
/// geometry, and over those frames there is none worth trusting; but the plate is still there,
/// and green is the one thing it must not show.
/// </para>
/// <para>
/// <b>How.</b> A guard is an ordinary <see cref="ResolvedScreenInsert"/> whose content is a solid
/// dark colour (<see cref="ResolvedScreenInsert.GuardFillColor"/>), matted to the plate with the
/// track's own solved key exactly as a real insert is, so it can only darken pixels that ARE the
/// plate — the bezel, a hand in front, the room stay as filmed — with the plate's reflections
/// carried over, so it reads as a screen that is off. Within a track's span its geometry is the
/// track's own; outside it (an uncovered span), it is the box the analyze step saw plate pixels
/// in over that span, grown by <see cref="HeldOverscan"/> — no quad is trustworthy there, but
/// where the plate WAS is measured, and the matte, not the box, decides which pixels are plate.
/// </para>
/// <para>
/// A track without a solved key cannot be guarded (there is nothing to matte with), and a track
/// far under the confidence floor (<see cref="GuardConfidenceFraction"/>) may not be a plate at
/// all — both are reported as unguarded rather than darkened.
/// </para>
/// </summary>
internal static class InsertPlateGuardPlanner
{
    /// <summary>The guard's screen colour: near-black, neutral — a phone or tablet screen that is switched off.</summary>
    internal const string FillColor = "0x101010";

    /// <summary>Overscan of a guard inside its track's span: the same generous figure a matted insert uses.</summary>
    internal const double TrackedOverscan = InsertCompositeGeometry.MattedInsertOverscan;

    /// <summary>
    /// Overscan of a guard over an UNCOVERED span: the box of plate pixels the analyze step saw
    /// there, grown by a tenth of its size on every side to cover the tracking grid's coarseness
    /// (the matte, not the box, decides which pixels inside it are plate).
    /// </summary>
    internal const double HeldOverscan = 0.2;

    /// <summary>A track below this fraction of <c>MinInsertConfidence</c> is not guarded: too doubtful to be sure it is a plate at all.</summary>
    internal const double GuardConfidenceFraction = 0.5;

    /// <summary>Cap on guards per compile — each is a full matte pipeline in the filtergraph.</summary>
    internal const int MaxGuards = 12;

    /// <summary>Windows shorter than this (seconds) are not worth a guard — less than a frame at any real rate.</summary>
    private const double MinWindowSec = 0.02;

    internal sealed record Plan(List<ResolvedScreenInsert> Guards, JsonArray Report);

    /// <param name="applied">The inserts actually being composited (guards are never planned over their windows).</param>
    /// <param name="fitFor">Where a source's picture sits on the canvas — the same fit the inserts use.</param>
    internal static Plan Build(
        IReadOnlyList<VideoInsertRegionTrack>? tracks, OutputTimeline timeline, VideoAnalysisMedia canvas,
        IReadOnlyList<ResolvedScreenInsert> applied, Func<VideoInsertRegionTrack, VideoCompileStepExecutor.SourceCanvasFit> fitFor,
        bool matteEnabled, double minInsertConfidence, int maxKeyframes)
    {
        var guards = new List<ResolvedScreenInsert>();
        var report = new JsonArray();
        int fpsNum = canvas.FpsNum > 0 ? canvas.FpsNum : 30;
        int fpsDen = canvas.FpsDen > 0 ? canvas.FpsDen : 1;

        foreach (VideoInsertRegionTrack track in (tracks ?? [])
                     .Where(t => string.Equals(t.Method, "chroma", StringComparison.Ordinal) && t.Keyframes is { Count: > 0 })
                     .OrderBy(t => t.StartSec)
                     .ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            ResolvedScreenInsert? insert = applied.FirstOrDefault(a => a.GuardFillColor is null && string.Equals(a.RegionId, track.Id, StringComparison.Ordinal));

            // Source stretches showing this plate: the track's own span (when it is not the one
            // being filled, or beyond the part of it that is), plus what it left uncovered.
            var stretches = new List<(double Start, double End, VideoInsertPlateSpan? Span, string Reason)>
            {
                (track.StartSec, track.EndSec, null, insert is null ? "track_not_composited" : "outside_insert_window")
            };
            stretches.AddRange((track.UncoveredPlateSpans ?? []).Select(s => (s.StartSec, s.EndSec, (VideoInsertPlateSpan?)s, "untracked_frames")));

            foreach ((double sStart, double sEnd, VideoInsertPlateSpan? span, string reason) in stretches)
            {
                foreach ((double oStart0, double oEnd0) in timeline.MapWindowToOutputAll(sStart, sEnd, track.SourceIndex))
                {
                    // Same hold as the insert's own window: a stretch that runs to its clip's last
                    // tracked frame also covers the segment's repeated last frame.
                    (double oStart, double oEnd) = timeline.ExtendToSegmentEnd(
                        (oStart0, oEnd0), 2.0 * fpsDen / fpsNum);
                    foreach ((double wStart, double wEnd) in Subtract(oStart, oEnd, insert))
                    {
                        if (wEnd - wStart < MinWindowSec)
                            continue;

                        string? unguardedWhy =
                            !matteEnabled ? "matte_disabled"
                            : track.ChromaKey is null ? "no_plate_key"
                            : track.Confidence < minInsertConfidence * GuardConfidenceFraction ? "confidence_too_low_to_guard"
                            : guards.Count >= MaxGuards ? "max_guards_exceeded"
                            : null;

                        ResolvedScreenInsert? guard = unguardedWhy is null
                            ? BuildGuard(track, sStart, sEnd, span, (wStart, wEnd), timeline, canvas, fitFor(track), maxKeyframes, fpsNum, fpsDen)
                            : null;
                        if (unguardedWhy is null && guard is null)
                            unguardedWhy = "no_geometry";
                        if (guard is not null)
                            guards.Add(guard);

                        report.Add(new JsonObject
                        {
                            ["code"] = "insert_untracked_plate_visible",
                            ["regionId"] = track.Id,
                            ["reason"] = reason,
                            ["outputStartSec"] = Math.Round(wStart, 3),
                            ["outputEndSec"] = Math.Round(wEnd, 3),
                            ["neutralized"] = guard is not null,
                            ["notNeutralizedReason"] = unguardedWhy
                        });
                    }
                }
            }
        }

        return new Plan(guards, report);
    }

    /// <summary>
    /// The compile's customer-facing C1 warning for an inserts node carrying
    /// <c>unfilledPlates</c>, or <c>null</c> when every visible plate was filled.
    /// </summary>
    internal static JsonObject? CompileWarning(JsonObject? insertsNode)
    {
        if (insertsNode?["unfilledPlates"] is not JsonArray { Count: > 0 } unfilled)
            return null;

        double seconds = 0;
        bool allNeutralized = true;
        foreach (JsonNode? entry in unfilled)
        {
            seconds += (entry?["outputEndSec"]?.GetValue<double>() ?? 0) - (entry?["outputStartSec"]?.GetValue<double>() ?? 0);
            allNeutralized &= entry?["neutralized"]?.GetValue<bool>() == true;
        }

        // Under a twentieth of a second rounds to "0.0" at one decimal, which reads as "nothing
        // was visible" — say "for a moment" instead (a single frame is still worth telling).
        string howLong = seconds < 0.05
            ? "for a moment"
            : $"for about {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} seconds";
        return new JsonObject
        {
            ["code"] = "insert_untracked_plate_visible",
            ["message"] = allNeutralized
                ? $"A green screen was visible {howLong} where the inserted content could not follow it; " +
                  "those moments show a dark screen instead of the green."
                : $"A green screen was visible {howLong} where the inserted content could not follow it, " +
                  "and some of it could not be darkened, so green may show there."
        };
    }

    /// <summary>The output window minus the applied insert's own window (which a guard must never paint over).</summary>
    private static IEnumerable<(double Start, double End)> Subtract(double start, double end, ResolvedScreenInsert? insert)
    {
        if (insert is null || insert.OutputEndSec <= start || insert.OutputStartSec >= end)
        {
            yield return (start, end);
            yield break;
        }

        if (insert.OutputStartSec > start)
            yield return (start, insert.OutputStartSec);
        if (insert.OutputEndSec < end)
            yield return (insert.OutputEndSec, end);
    }

    private static ResolvedScreenInsert? BuildGuard(
        VideoInsertRegionTrack track, double sStart, double sEnd, VideoInsertPlateSpan? span, (double Start, double End) window,
        OutputTimeline timeline, VideoAnalysisMedia canvas, VideoCompileStepExecutor.SourceCanvasFit fit,
        int maxKeyframes, int fpsNum, int fpsDen)
    {
        // Geometry: the track's own keyframes inside its span; over an uncovered stretch, the box
        // the analyze step saw plate pixels in across that stretch (or, from an artifact that
        // predates the box, the nearest tracked quad held across it).
        List<VideoInsertQuadKeyframe> keys;
        if (span is { W: > 0, H: > 0 } box && !(box.W >= 1 && box.H >= 1 && box.X <= 0 && box.Y <= 0))
        {
            VideoInsertQuadKeyframe rect = new(sStart, box.X, box.Y, box.X + box.W, box.Y, box.X, box.Y + box.H, box.X + box.W, box.Y + box.H);
            keys = [rect, rect with { TimeSec = Math.Max(sStart, sEnd - 1e-3) }];
        }
        else
        {
            keys = track.Keyframes.Where(k => k.TimeSec >= sStart - 1e-6 && k.TimeSec <= sEnd + 1e-6).ToList();
            if (keys.Count == 0)
            {
                VideoInsertQuadKeyframe nearest = track.Keyframes.OrderBy(k => Math.Abs(k.TimeSec - (sStart + sEnd) / 2)).First();
                keys = [nearest with { TimeSec = sStart, SurfaceCorners = null }, nearest with { TimeSec = Math.Max(sStart, sEnd - 1e-3), SurfaceCorners = null }];
            }
        }

        VideoInsertRegionTrack synthetic = track with { StartSec = sStart, EndSec = sEnd, Keyframes = keys };
        IReadOnlyList<InsertQuadFrame> frames = VideoCompileStepExecutor.BuildInsertKeyframes(
            synthetic, timeline, canvas, window, maxKeyframes, span is null ? TrackedOverscan : HeldOverscan, fit);
        if (frames.Count == 0)
            return null;

        return new ResolvedScreenInsert(
            track.Id, string.Empty, window.Start, window.End, frames, fpsNum, fpsDen,
            Mesh: null, Matte: track.ChromaKey, TransferReflections: true, GuardFillColor: FillColor);
    }
}
