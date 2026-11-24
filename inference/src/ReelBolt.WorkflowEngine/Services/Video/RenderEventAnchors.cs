using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// T-110: <c>e{n}</c> cue anchors on events INSIDE a composition (docs/video-editing.md "Event anchors
/// inside a composition"). Pure — no I/O. At analysis, each render event sidecar the analysis can see
/// (a source, or insert content a downstream compile pins) becomes offered events: the seconds stay in
/// the artifact, the model sees an opaque id and a sanitized label only. At compile, an event is mapped to
/// OUTPUT time through the cut (a source event) or the applied insert's on-screen window (an insert event);
/// an event the edit does not keep drops its cue, never relocates it.
/// </summary>
public static class RenderEventAnchors
{
    /// <summary>The whole offer's cap, across every manifest the analysis sees.</summary>
    public const int MaxOfferedEvents = RenderEventManifest.MaxEvents;

    /// <summary>Dropped-cue reason: the event's insert content was not composited by this compile.</summary>
    public const string InsertNotAppliedReason = "event_insert_not_applied";

    /// <summary>Dropped-cue reason: the event's moment is not in the kept output (shared with cut anchors).</summary>
    public const string CutAwayReason = "anchor_cut_away";

    /// <summary>One sidecar the analysis found, and which timeline its events are on.</summary>
    public sealed record EventSource(
        RenderEventManifest Manifest, int? SourceIndex, double TrimInSec, double? DurationSec, string? InsertContentStorageKey);

    /// <summary>A sidecar of analysed source <paramref name="sourceIndex"/>: times relative to the analysed window.</summary>
    public static EventSource ForSource(RenderEventManifest manifest, int sourceIndex, double trimInSec, double? durationSec) =>
        new(manifest, sourceIndex, trimInSec, durationSec, null);

    /// <summary>A sidecar of insert content <paramref name="contentStorageKey"/>: times on the content's own timeline.</summary>
    public static EventSource ForInsertContent(RenderEventManifest manifest, string contentStorageKey) =>
        new(manifest, null, 0, null, contentStorageKey);

    /// <summary>
    /// Every usable event, in source order then frame order, numbered <c>e0..</c> contiguously and capped at
    /// <see cref="MaxOfferedEvents"/>. A manifest without a frame rate contributes nothing; a source event
    /// outside the analysed window is left out.
    /// </summary>
    public static List<VideoAnalysisRenderEvent> BuildEvents(IReadOnlyList<EventSource> sources)
    {
        List<VideoAnalysisRenderEvent> events = [];
        foreach (EventSource source in sources)
        {
            if (source.Manifest.Fps is not > 0)
                continue;

            double fps = source.Manifest.Fps.Value;
            foreach (RenderEvent e in source.Manifest.Events.OrderBy(e => e.Frame))
            {
                if (events.Count >= MaxOfferedEvents)
                    return events;

                double sec = e.Frame / fps - source.TrimInSec;
                if (sec < 0 || (source.DurationSec is { } duration && sec > duration))
                    continue;

                string label = RenderEventManifest.SanitizeLabel(e.Label);
                events.Add(new VideoAnalysisRenderEvent(
                    Id: "e" + events.Count.ToString(CultureInfo.InvariantCulture),
                    Name: e.Name,
                    Label: label.Length > 0 ? label : e.Name,
                    Frame: e.Frame,
                    ContentSec: sec,
                    SourceIndex: source.SourceIndex,
                    InsertContentStorageKey: source.InsertContentStorageKey));
            }
        }

        return events;
    }

    /// <summary>
    /// <c>view.renderEvents</c>: <c>{id, label, src}</c> for an event in a source, <c>{id, label, insert: true}</c>
    /// for one inside pinned insert content. No time, no frame, no storage key.
    /// </summary>
    public static JsonArray ViewNode(IReadOnlyList<VideoAnalysisRenderEvent> events) =>
        new([.. events.Select(e =>
        {
            JsonObject node = new() { ["id"] = e.Id, ["label"] = e.Label };
            if (e.SourceIndex is { } src)
                node["src"] = src;
            else
                node["insert"] = true;
            return (JsonNode)node;
        })]);

    /// <summary>
    /// SHA-256 over every offered event (what it is, where, when). Carried in the analysis output's
    /// <c>meta.renderEvents.digest</c>, which the compile's step-cache key covers through its upstream digest.
    /// </summary>
    public static string Digest(IReadOnlyList<VideoAnalysisRenderEvent> events)
    {
        StringBuilder sb = new();
        foreach (VideoAnalysisRenderEvent e in events)
        {
            sb.Append(e.Id).Append('|').Append(e.Name).Append('|').Append(e.Label.Length).Append(':').Append(e.Label).Append('|')
              .Append(e.Frame.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(e.ContentSec.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(e.SourceIndex?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(e.InsertContentStorageKey ?? "-").Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// The output second <paramref name="ev"/> happens at, or null plus why the cue is dropped. A source event
    /// maps through <see cref="OutputTimeline.MapToOutputSec"/>. An insert event lands at the applied insert's
    /// <see cref="ResolvedScreenInsert.OutputStartSec"/> plus its content second (the content plays from there),
    /// inside the insert's on-screen window; with the same content on several plates the earliest window that
    /// still shows the moment wins.
    /// <para>
    /// QA-P1-19 item 3: when the insert's content had near-black dips REMOVED
    /// (<see cref="ResolvedScreenInsert.RemovedContentDips"/>, <c>InsertContentDips = Cut</c>), the encoded
    /// content is shorter than the file the event was declared against, so the content second is shifted back
    /// by every removed second before it; a moment that fell inside a removed dip no longer exists in the film.
    /// </para>
    /// </summary>
    public static (double? OutputSec, string? DropReason) ResolveOutputSec(
        VideoAnalysisRenderEvent ev, OutputTimeline timeline, IReadOnlyList<ResolvedScreenInsert> inserts)
    {
        if (ev.SourceIndex is { } sourceIndex)
        {
            double? mapped = timeline.MapToOutputSec(ev.ContentSec, sourceIndex);
            return mapped is null ? (null, CutAwayReason) : (mapped, null);
        }

        List<ResolvedScreenInsert> carrying = inserts
            .Where(i => i.GuardFillColor is null && ev.InsertContentStorageKey is not null &&
                        string.Equals(i.AssetStorageKey, ev.InsertContentStorageKey, StringComparison.Ordinal))
            .OrderBy(i => i.OutputStartSec)
            .ToList();
        if (carrying.Count == 0)
            return (null, InsertNotAppliedReason);

        foreach (ResolvedScreenInsert insert in carrying)
        {
            double contentSec = ev.ContentSec;
            if (insert.RemovedContentDips is { Count: > 0 } removed)
            {
                double removedBefore = 0;
                bool insideRemovedDip = false;
                foreach ((double start, double end) in removed)
                {
                    if (contentSec < start)
                        break;
                    if (contentSec < end)
                    {
                        insideRemovedDip = true;
                        break;
                    }

                    removedBefore += end - start;
                }

                // The moment itself was cut out of the content: there is nothing left to anchor to.
                if (insideRemovedDip)
                    continue;

                contentSec -= removedBefore;
            }

            double at = insert.OutputStartSec + contentSec;
            if (at < insert.OutputEndSec)
                return (at, null);
        }

        return (null, CutAwayReason);
    }
}
