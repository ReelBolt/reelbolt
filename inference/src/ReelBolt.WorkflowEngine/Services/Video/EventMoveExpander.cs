using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One event move's outcome, as reported in the step output.</summary>
internal sealed record EventMoveReportEntry(string EventId, bool Applied, string? Reason, int SourceIndex, int Keyframes);

/// <summary>The result of <see cref="EventMoveExpander.Expand"/>: the produced reframes and a per-entry report.</summary>
internal sealed record EventMoveExpansion(IReadOnlyList<VideoReframe> Reframes, IReadOnlyList<EventMoveReportEntry> Report)
{
    internal static readonly EventMoveExpansion Empty = new([], []);

    internal JsonObject ToNode()
    {
        var dropped = new JsonArray();
        var applied = new JsonArray();
        foreach (EventMoveReportEntry e in Report)
        {
            if (e.Applied)
                applied.Add(new JsonObject { ["eventId"] = e.EventId, ["sourceIndex"] = e.SourceIndex, ["keyframes"] = e.Keyframes });
            else
                dropped.Add(new JsonObject { ["eventId"] = e.EventId, ["reason"] = e.Reason });
        }

        return new JsonObject { ["movesApplied"] = applied, ["dropped"] = dropped };
    }
}

/// <summary>
/// Turns <see cref="VideoCompileStepConfig.EventMoves"/> into ordinary <see cref="VideoReframe"/>s (docs/video-editing.md
/// "Moves on events"). The event id resolves to the source second and region in the analysis; every number below comes from
/// the word tables in this file, never from config. Pure: no I/O, deterministic (the shake seed is a hash of the event).
/// </summary>
internal static class EventMoveExpander
{
    public const string UnknownAnchorReason = "unknown_anchor_id";
    public const string CutAwayReason = "anchor_cut_away";
    public const string NotInSourceReason = "event_not_in_source";
    public const string OverlapReason = "overlaps_other_reframe";

    // ---- Word tables. Peak zoom of a zoom move (1 = the full frame). ----
    internal static double PeakZoom(EventMoveIntensity i) => i switch
    {
        EventMoveIntensity.Subtle => 1.10,
        EventMoveIntensity.Strong => 1.50,
        _ => 1.25
    };

    // Pans need headroom to travel in: the zoom they hold while panning.
    internal static double PanZoom(EventMoveIntensity i) => i switch
    {
        EventMoveIntensity.Subtle => 1.15,
        EventMoveIntensity.Strong => 1.40,
        _ => 1.25
    };

    // Share of the available room (half of 1 - 1/zoom, each way) a pan travels across.
    internal static double PanTravel(EventMoveIntensity i) => i switch
    {
        EventMoveIntensity.Subtle => 0.4,
        EventMoveIntensity.Strong => 1.0,
        _ => 0.7
    };

    // Seconds the move lasts after the event.
    internal static double HoldSeconds(EventMoveHold h) => h switch
    {
        EventMoveHold.Short => 0.4,
        EventMoveHold.Long => 1.8,
        _ => 0.9
    };

    // Shake: constant zoom headroom and per-intensity offset amplitude (fraction of the frame), sampled at ShakeHz.
    internal const double ShakeZoom = 1.08;
    internal const double ShakeHz = 10;

    internal static double ShakeAmplitude(EventMoveIntensity i) => i switch
    {
        EventMoveIntensity.Subtle => 0.006,
        EventMoveIntensity.Strong => 0.022,
        _ => 0.012
    };

    // Seconds the framing takes to snap in / settle out.
    internal const double SnapSeconds = 0.12;
    internal const double ReleaseSeconds = 0.35;

    internal const double ShakeLeadSeconds = 0.1;

    /// <summary>The 3x3 centre of an event region (or the middle when it has none).</summary>
    internal static (double X, double Y) CellCentre(ReelBolt.Shared.Workflows.VideoEvents.VideoEventRegion? region)
    {
        if (region is null)
            return (0.5, 0.5);
        double cx = region.X + region.W / 2, cy = region.Y + region.H / 2;
        static double Cell(double v) => v < 1.0 / 3 ? 1.0 / 6 : v >= 2.0 / 3 ? 5.0 / 6 : 0.5;
        return (Cell(cx), Cell(cy));
    }

    internal static ulong Seed(string eventId, int sourceIndex, int frame)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{eventId}|{sourceIndex}|{frame}")));
        return BitConverter.ToUInt64(hash, 0) | 1UL;
    }

    private static double Unit(ref ulong state)
    {
        // xorshift64*: deterministic across platforms.
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        ulong r = state * 2685821657736338717UL;
        return (r >> 11) * (1.0 / (1UL << 53));
    }

    private static double ClampCentre(double c, double zoom) => Math.Clamp(c, 0.5 / zoom, 1 - 0.5 / zoom);

    /// <summary>The keyframes of one move, times relative to the reframe's start (which is the first keyframe at 0).</summary>
    internal static List<ReframeKeyframe> Keyframes(EventMoveRequest m, double cx, double cy, ulong seed)
    {
        double hold = HoldSeconds(m.Hold);
        ReframeEasing e = m.Easing;
        var k = new List<ReframeKeyframe>();
        switch (m.Move)
        {
            case EventMoveKind.PunchIn:
                {
                    double z = PeakZoom(m.Intensity);
                    k.Add(new(0, 1.0, 0.5, 0.5, e));
                    k.Add(new(SnapSeconds, z, ClampCentre(cx, z), ClampCentre(cy, z), ReframeEasing.Hold));
                    k.Add(new(SnapSeconds + hold, z, ClampCentre(cx, z), ClampCentre(cy, z), e));
                    k.Add(new(SnapSeconds + hold + ReleaseSeconds, 1.0, 0.5, 0.5, ReframeEasing.Linear));
                    break;
                }
            case EventMoveKind.PushIn:
                {
                    double z = PeakZoom(m.Intensity);
                    k.Add(new(0, 1.0, 0.5, 0.5, e));
                    k.Add(new(hold, z, ClampCentre(cx, z), ClampCentre(cy, z), e));
                    k.Add(new(hold + ReleaseSeconds, 1.0, 0.5, 0.5, ReframeEasing.Linear));
                    break;
                }
            case EventMoveKind.PullOut:
                {
                    double z = PeakZoom(m.Intensity);
                    k.Add(new(0, z, ClampCentre(cx, z), ClampCentre(cy, z), e));
                    k.Add(new(hold, 1.0, 0.5, 0.5, ReframeEasing.Linear));
                    break;
                }
            case EventMoveKind.PanLeft:
            case EventMoveKind.PanRight:
            case EventMoveKind.PanUp:
            case EventMoveKind.PanDown:
                {
                    double z = PanZoom(m.Intensity);
                    double room = 0.5 * (1 - 1 / z) * PanTravel(m.Intensity);
                    (double dx, double dy) = m.Move switch
                    {
                        EventMoveKind.PanLeft => (-room, 0.0),
                        EventMoveKind.PanRight => (room, 0.0),
                        EventMoveKind.PanUp => (0.0, -room),
                        _ => (0.0, room)
                    };
                    double bx = ClampCentre(cx, z), by = ClampCentre(cy, z);
                    // Start half a travel behind the region centre and end half a travel past it, so the event sits mid-move.
                    k.Add(new(0, 1.0, 0.5, 0.5, e));
                    k.Add(new(SnapSeconds, z, ClampCentre(bx - dx, z), ClampCentre(by - dy, z), e));
                    k.Add(new(SnapSeconds + hold, z, ClampCentre(bx + dx, z), ClampCentre(by + dy, z), e));
                    k.Add(new(SnapSeconds + hold + ReleaseSeconds, 1.0, 0.5, 0.5, ReframeEasing.Linear));
                    break;
                }
            default: // Shake
                {
                    double amp = ShakeAmplitude(m.Intensity);
                    int steps = Math.Max(2, (int)Math.Round(hold * ShakeHz));
                    double step = 1.0 / ShakeHz;
                    ulong state = seed;
                    k.Add(new(0, 1.0, 0.5, 0.5, ReframeEasing.Linear));
                    for (int i = 0; i < steps; i++)
                    {
                        double decay = 1.0 - (double)i / steps;
                        double ox = (Unit(ref state) * 2 - 1) * amp * decay;
                        double oy = (Unit(ref state) * 2 - 1) * amp * decay;
                        k.Add(new(ShakeLeadSeconds + i * step, ShakeZoom, ClampCentre(0.5 + ox, ShakeZoom), ClampCentre(0.5 + oy, ShakeZoom), ReframeEasing.Linear));
                    }

                    k.Add(new(ShakeLeadSeconds + steps * step, 1.0, 0.5, 0.5, ReframeEasing.Linear));
                    break;
                }
        }

        return k;
    }

    /// <summary>
    /// Expands every entry. Unknown ids, events outside the kept cut, events not in a source clip and moves that would
    /// overlap another reframe of the same source are dropped with a reason, never relocated.
    /// </summary>
    internal static EventMoveExpansion Expand(
        VideoCompileStepConfig config,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<(int SourceIndex, double Start, double End)> keptSpans)
    {
        if (config.EventMoves is not { Count: > 0 } requests)
            return EventMoveExpansion.Empty;

        var offered = new HashSet<string>(artifact.OfferedEventIds ?? [], StringComparer.Ordinal);
        var taken = new Dictionary<int, List<(double From, double To)>>();
        foreach (VideoReframe r in config.Reframes ?? [])
        {
            if (r is null)
                continue;
            if (!taken.TryGetValue(r.SourceIndex, out var list))
                taken[r.SourceIndex] = list = [];
            double to = r.ToSec ?? r.StartSec + (r.Keyframes.Count > 0 ? r.Keyframes[^1].AtSec : 0);
            list.Add((r.StartSec, r.ToSec is null && r.FromSec is null ? double.PositiveInfinity : to));
        }

        // A followSubject entry that names its source owns that range too: a move there drops (overlaps_other_reframe)
        // instead of failing REFRAME_OVERLAP once the follow is expanded. Entries without a SourceIndex resolve later.
        foreach (FollowSubjectRequest f in config.FollowSubject ?? [])
        {
            if (f?.SourceIndex is not { } fs)
                continue;
            if (!taken.TryGetValue(fs, out var fl))
                taken[fs] = fl = [];
            fl.Add((f.FromSec ?? 0, f.ToSec ?? double.PositiveInfinity));
        }

        var produced = new List<VideoReframe>();
        var report = new List<EventMoveReportEntry>();
        foreach (EventMoveRequest request in requests)
        {
            if (!offered.Contains(request.EventId) || VideoEventOffers.Find(artifact, request.EventId) is not { } ev)
            {
                report.Add(new(request.EventId, false, UnknownAnchorReason, -1, 0));
                continue;
            }

            if (ev.SourceIndex is not { } source)
            {
                report.Add(new(request.EventId, false, NotInSourceReason, -1, 0));
                continue;
            }

            double at = ev.ContentSec;
            if (!keptSpans.Any(s => s.SourceIndex == source && at >= s.Start - 1e-9 && at < s.End))
            {
                report.Add(new(request.EventId, false, CutAwayReason, source, 0));
                continue;
            }

            (double cx, double cy) = CellCentre(ev.Region);
            List<ReframeKeyframe> keys = Keyframes(request, cx, cy, Seed(request.EventId, source, ev.Frame));
            // The framing starts on the event's own second; a shake starts a beat earlier so its first jolt lands on the event.
            double from = request.Move == EventMoveKind.Shake ? Math.Max(0, at - ShakeLeadSeconds) : at;
            double span = keys[^1].AtSec;
            double reframeTo = from + span;

            if (taken.GetValueOrDefault(source) is { } earlier && earlier.Any(t => from < t.To - 1e-9 && t.From < reframeTo - 1e-9))
            {
                report.Add(new(request.EventId, false, OverlapReason, source, 0));
                continue;
            }

            if (!taken.TryGetValue(source, out var mine))
                taken[source] = mine = [];
            mine.Add((from, reframeTo));
            produced.Add(new VideoReframe(source, from, reframeTo, keys));
            report.Add(new(request.EventId, true, null, source, keys.Count));
        }

        return new EventMoveExpansion(produced, report);
    }
}
