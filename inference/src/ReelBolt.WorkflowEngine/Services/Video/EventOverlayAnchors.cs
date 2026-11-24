using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The word table behind <c>MotionGraphicsOverlay.Kind</c> on an overlay anchored to an event (docs/video-editing.md
/// "Graphics on an event"). Every geometry number is here, in code: a plan only ever carries the word.
/// </summary>
internal static class EventOverlayLayout
{
    // The three overlay bands, as the analysis names them (full width, a third of the frame high).
    private const string UpperThird = "UpperThird";
    private const string CenterBand = "CenterBand";
    private const string LowerThird = "LowerThird";

    /// <summary>The named band and the normalised rectangle an overlay of <paramref name="kind"/> uses for an event whose region sits at <paramref name="region"/>.</summary>
    internal static (string Region, VideoAnalysisRect Rect) For(string? kind, ReelBolt.Shared.Workflows.VideoEvents.VideoEventRegion? region)
    {
        (double cx, double cy) = EventMoveExpander.CellCentre(region);
        switch (kind)
        {
            case "LowerThird":
                return (LowerThird, Band(LowerThird));
            case "Title":
                return (CenterBand, Band(CenterBand));
            case "Tag":
                {
                    string band = BandOfRow(cy);
                    int column = cx < 1.0 / 3 ? 0 : cx >= 2.0 / 3 ? 2 : 1;
                    VideoAnalysisRect row = Band(band);
                    return (band, new VideoAnalysisRect(column / 3.0, row.Y, 1.0 / 3, row.H));
                }
            default: // Callout, and any word the table does not know.
                {
                    string band = BandOfRow(cy);
                    return (band, Band(band));
                }
        }
    }

    private static string BandOfRow(double cy) => cy < 1.0 / 3 ? UpperThird : cy >= 2.0 / 3 ? LowerThird : CenterBand;

    private static VideoAnalysisRect Band(string band) => band switch
    {
        UpperThird => new VideoAnalysisRect(0, 0, 1, 1.0 / 3),
        LowerThird => new VideoAnalysisRect(0, 2.0 / 3, 1, 1.0 / 3),
        _ => new VideoAnalysisRect(0, 1.0 / 3, 1, 1.0 / 3)
    };
}

/// <summary>
/// Graphics on an event. An overlay naming <c>EventId</c> instead of <c>PlacementId</c> is turned into an ordinary overlay
/// on a synthetic placement that starts on the event's moment, so the whole placement pipeline (cut mapping, fit, legibility,
/// sizing, drawtext or asset compositing) applies unchanged. Pure; overlays that name a placement are never touched.
/// </summary>
internal static class EventOverlayAnchors
{
    public const string BothAnchorsReason = "placement_and_event_both_set";
    public const string UnknownAnchorReason = "unknown_anchor_id";

    /// <summary>
    /// Rewrites each event overlay of <paramref name="plan"/> in place (its <c>PlacementId</c> becomes the event id, or
    /// <c>e3#2</c> for a second overlay on the same event), registers its synthetic placement, and removes the ones that
    /// cannot apply. Returns those as (id, reason) for the graphics report.
    /// </summary>
    internal static List<(string Id, string Reason)> Prepare(
        MotionGraphicsPlanOutput plan,
        VideoAnalysisArtifact artifact,
        OutputTimeline timeline,
        VideoCompileStepConfig config,
        IDictionary<string, VideoAnalysisPlacement> placementById,
        ISet<string> offeredPlacementIds)
    {
        List<(string, string)> dropped = [];
        if (!plan.Overlays.Any(o => !string.IsNullOrWhiteSpace(o.EventId)))
            return dropped;

        HashSet<string> offeredEventIds = new(artifact.OfferedEventIds ?? [], StringComparer.Ordinal);
        double windowSec = Math.Max(1, Math.Max(config.OverlayShortMs, Math.Max(config.OverlayMediumMs, config.OverlayHoldMs)) / 1000.0);
        Dictionary<string, int> uses = new(StringComparer.Ordinal);
        List<MotionGraphicsOverlay> removed = [];

        foreach (MotionGraphicsOverlay overlay in plan.Overlays)
        {
            if (string.IsNullOrWhiteSpace(overlay.EventId))
                continue;

            string eventId = overlay.EventId;
            if (!string.IsNullOrWhiteSpace(overlay.PlacementId))
            {
                dropped.Add((overlay.PlacementId, BothAnchorsReason));
                removed.Add(overlay);
                continue;
            }

            if (!offeredEventIds.Contains(eventId) || VideoEventOffers.Find(artifact, eventId) is not { } ev)
            {
                dropped.Add((eventId, UnknownAnchorReason));
                removed.Add(overlay);
                continue;
            }

            // The same lookup the sound cues use: a moment the edit cut away (or insert content that was not composited) drops.
            (double? outputSec, string? reason) = RenderEventAnchors.ResolveOutputSec(ev, timeline, []);
            if (outputSec is null || ev.SourceIndex is not { } sourceIndex)
            {
                dropped.Add((eventId, reason ?? RenderEventAnchors.InsertNotAppliedReason));
                removed.Add(overlay);
                continue;
            }

            (string region, VideoAnalysisRect rect) = EventOverlayLayout.For(overlay.Kind, ev.Region);
            int n = uses.GetValueOrDefault(eventId) + 1;
            uses[eventId] = n;
            string key = n == 1 ? eventId : $"{eventId}#{n}";
            placementById[key] = new VideoAnalysisPlacement(
                key, string.Empty, region, rect, "Event", ev.ContentSec, ev.ContentSec + windowSec, 1.0, "Light", sourceIndex);
            offeredPlacementIds.Add(key);
            overlay.PlacementId = key;
        }

        foreach (MotionGraphicsOverlay overlay in removed)
            plan.Overlays.Remove(overlay);
        return dropped;
    }
}
