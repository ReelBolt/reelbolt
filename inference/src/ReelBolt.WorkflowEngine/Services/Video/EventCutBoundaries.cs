using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One keep span whose ends were resolved, partly or wholly, from events (see <see cref="EventCutBoundaries"/>).</summary>
internal sealed record EventCutSpan(double Start, double End, int SourceIndex, string? ErrorCode, string? Message)
{
    public static EventCutSpan Fail(string code, string message) => new(0, 0, 0, code, message);
}

/// <summary>
/// Cut on an event (<c>VideoCompileStepConfig.CutOnEvents</c>, docs/video-editing.md "Cutting on an event"): lets a
/// Keep span begin or end on an offered event id <c>e{n}</c>. A SEPARATE lookup beside <c>BuildIdTimeIndex</c> - the
/// s/g/t index is untouched. An event is a zero-length anchor: the boundary is the event's frame snapped to a whole
/// frame of its source, except that when a transcript segment covers the event the cut moves to the middle of the
/// nearest silence gap within <see cref="GapReachSec"/>, so it never lands inside a word. No number comes from the model.
/// </summary>
internal static class EventCutBoundaries
{
    /// <summary>How far from the event a silence gap may be and still be preferred to the event itself.</summary>
    public const double GapReachSec = 0.3;

    /// <summary>How a boundary was placed, for the report.</summary>
    public const string ViaEvent = "event";
    public const string ViaSilenceGap = "silence_gap";

    /// <summary>
    /// Resolves a span with an event on either end. Null when neither end is an OFFERED event id (the caller's ordinary
    /// path then runs unchanged, so an unoffered <c>e{n}</c> still fails UNKNOWN_ID there). The plain end of a mixed
    /// span must be an offered cut-anchor id, else UNKNOWN_ID.
    /// </summary>
    internal static EventCutSpan? ResolveSpan(
        VideoAnalysisArtifact artifact,
        VideoEditKeepSpan span,
        IReadOnlyDictionary<string, (double Start, double End, int SourceIndex)> idTimes,
        ISet<string> offeredIds,
        Func<int, double> fpsOf,
        List<JsonObject> report)
    {
        HashSet<string> offeredEvents = new(artifact.OfferedEventIds ?? [], StringComparer.Ordinal);
        bool fromIsEvent = EventMoveRequest.IsEventId(span.FromId) && offeredEvents.Contains(span.FromId);
        bool toIsEvent = EventMoveRequest.IsEventId(span.ToId) && offeredEvents.Contains(span.ToId);
        if (!fromIsEvent && !toIsEvent)
            return null;

        double start, end;
        int fromSource, toSource;
        if (fromIsEvent)
        {
            if (!TryBoundary(artifact, span.FromId, fpsOf, out (double Sec, int Source, string Via) b, out string? why))
                return EventCutSpan.Fail(EventCutCodes.NotInSource, why!);
            (start, fromSource) = (b.Sec, b.Source);
            report.Add(Node(span.FromId, b.Via));
        }
        else
        {
            if (!offeredIds.Contains(span.FromId) || !idTimes.TryGetValue(span.FromId, out var f))
                return EventCutSpan.Fail("UNKNOWN_ID", UnknownMessage(span.FromId));
            (start, fromSource) = (f.Start, f.SourceIndex);
        }

        if (toIsEvent)
        {
            if (!TryBoundary(artifact, span.ToId, fpsOf, out (double Sec, int Source, string Via) b, out string? why))
                return EventCutSpan.Fail(EventCutCodes.NotInSource, why!);
            (end, toSource) = (b.Sec, b.Source);
            report.Add(Node(span.ToId, b.Via));
        }
        else
        {
            if (!offeredIds.Contains(span.ToId) || !idTimes.TryGetValue(span.ToId, out var t))
                return EventCutSpan.Fail("UNKNOWN_ID", UnknownMessage(span.ToId));
            (end, toSource) = (t.End, t.SourceIndex);
        }

        if (fromSource != toSource)
        {
            return EventCutSpan.Fail(
                "MIXED_SOURCE_SPAN",
                $"Keep span from '{span.FromId}' (source {fromSource}) to '{span.ToId}' (source {toSource}) spans two different source clips. " +
                "A single Keep span must stay within one clip - express a cross-clip edit as a sequence of separate single-clip Keep spans instead.");
        }

        if (end <= start)
        {
            return EventCutSpan.Fail(
                "INVALID_SPAN",
                $"Keep span from '{span.FromId}' to '{span.ToId}' resolves to a non-positive duration ([{start:F3}, {end:F3})). ToId must not precede FromId.");
        }

        return new EventCutSpan(start, end, fromSource, null, null);
    }

    /// <summary>The cut second of one event, snapped to a whole frame of its source; false (with a reason) when the event is not on a source clip.</summary>
    internal static bool TryBoundary(
        VideoAnalysisArtifact artifact, string eventId, Func<int, double> fpsOf,
        out (double Sec, int Source, string Via) boundary, out string? reason)
    {
        boundary = default;
        reason = null;
        if (VideoEventOffers.Find(artifact, eventId) is not { SourceIndex: { } source } ev)
        {
            reason = $"Event '{eventId}' is not on an analysed source clip (it belongs to an inserted render), so a span cannot begin or end on it.";
            return false;
        }

        double fps = fpsOf(source);
        double at = ev.ContentSec;
        string via = ViaEvent;
        bool spoken = (artifact.Segments ?? []).Any(s => s.SourceIndex == source && at >= s.StartSec && at < s.EndSec);
        if (spoken)
        {
            VideoAnalysisSilenceSpan? best = null;
            double bestDistance = double.MaxValue;
            foreach (VideoAnalysisSilenceSpan gap in artifact.SilenceSpans ?? [])
            {
                if (gap.SourceIndex != source)
                    continue;
                double distance = at < gap.StartSec ? gap.StartSec - at : at > gap.EndSec ? at - gap.EndSec : 0;
                if (distance < bestDistance)
                    (best, bestDistance) = (gap, distance);
            }

            // A gap that already holds the event leaves the event alone (it is not inside a word).
            if (best is not null && bestDistance > 0 && bestDistance <= GapReachSec)
            {
                double mid = Snap((best.StartSec + best.EndSec) / 2, fps);
                if (mid >= best.StartSec && mid <= best.EndSec)
                    (at, via) = (mid, ViaSilenceGap);
            }
        }

        boundary = (via == ViaEvent ? Snap(at, fps) : at, source, via);
        return true;
    }

    /// <summary>The nearest whole frame at <paramref name="fps"/>; unchanged when the rate is unknown.</summary>
    internal static double Snap(double sec, double fps) =>
        fps > 0 && double.IsFinite(fps) ? Math.Round(sec * fps) / fps : sec;

    private static JsonObject Node(string id, string via) => new() { ["eventId"] = id, ["placedOn"] = via };

    private static string UnknownMessage(string id) =>
        $"Keep span references id '{id}', which was not among the ids offered to the story editor. " +
        "Only ids that appeared in the bounded analysis view may be referenced.";
}
