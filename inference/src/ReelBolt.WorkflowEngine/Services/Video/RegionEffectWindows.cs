using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The program-time span a region effect is limited to. A null end is open (program start or end); <see cref="Never"/>
/// means a named event could not be placed, so the effect is held off rather than silently running the whole track.
/// </summary>
internal sealed record RegionEffectWindow(double? FromSec, double? ToSec, bool Never = false)
{
    // Seconds a frame boundary may be missed by (float timestamps): the gate opens this much early.
    private const double GateEpsilonSeconds = 0.001;

    /// <summary>The overlay <c>enable</c> expression for this window.</summary>
    internal string EnableExpression()
    {
        if (Never)
            return "0";
        var terms = new List<string>();
        if (FromSec is { } from)
            terms.Add($"gte(t,{FfmpegArgvFormat.Number(Math.Round(Math.Max(0, from - GateEpsilonSeconds), 4))})");
        if (ToSec is { } to)
            terms.Add($"lt(t,{FfmpegArgvFormat.Number(Math.Round(to - GateEpsilonSeconds, 4))})");
        return string.Join("*", terms);
    }

    /// <summary>
    /// One effect limited to this window: the program is split, the effect chain runs on one copy, and the result
    /// is overlaid back with the window as the overlay's <c>enable</c>, so outside the window the picture is untouched.
    /// <paramref name="buildEffect"/> gets the input and output labels of the inner chain.
    /// </summary>
    internal string Wrap(string inputLabel, string outputLabel, int slot, Func<string, string, string> buildEffect)
    {
        string b = $"[rew{slot}b]", i = $"[rew{slot}i]", o = $"[rew{slot}o]";
        return $"{inputLabel}split=2{b}{i};" + buildEffect(i, o) +
               $";{b}{o}overlay=x=0:y=0:format=yuv420:enable='{EnableExpression()}'{outputLabel}";
    }
}

/// <summary>Resolves the optional <c>FromEventId</c>/<c>ToEventId</c> of <see cref="VideoCompileStepConfig.RegionEffects"/> to program time.</summary>
internal static class RegionEffectWindows
{
    /// <summary>
    /// Attaches a window to each resolved effect whose entry names an event (entry order = effect order). Entries with
    /// no event id keep their effect untouched, so a config without windows returns the same list and no report.
    /// </summary>
    internal static (List<VideoCompileStepExecutor.ResolvedRegionEffect> Effects, JsonArray? Report) Apply(
        VideoCompileStepConfig config, VideoAnalysisArtifact artifact, OutputTimeline timeline,
        IReadOnlyList<ResolvedScreenInsert> inserts, List<VideoCompileStepExecutor.ResolvedRegionEffect> resolved)
    {
        IReadOnlyList<VideoRegionEffect> entries = config.RegionEffects ?? [];
        if (!entries.Any(e => e.FromEventId is not null || e.ToEventId is not null))
            return (resolved, null);

        var offered = new HashSet<string>(artifact.OfferedEventIds ?? [], StringComparer.Ordinal);
        var report = new JsonArray();
        var result = new List<VideoCompileStepExecutor.ResolvedRegionEffect>(resolved);
        for (int i = 0; i < result.Count && i < entries.Count; i++)
        {
            VideoRegionEffect entry = entries[i];
            if (entry.FromEventId is null && entry.ToEventId is null)
                continue;

            (double? from, string? fromReason) = Resolve(entry.FromEventId, artifact, offered, timeline, inserts);
            (double? to, string? toReason) = Resolve(entry.ToEventId, artifact, offered, timeline, inserts);
            string? reason = fromReason ?? toReason;
            if (reason is null && from is { } f && to is { } t && t <= f)
                reason = "window_empty";

            RegionEffectWindow window = reason is null ? new(from, to) : new(null, null, Never: true);
            result[i] = result[i] with { Window = window };
            report.Add(reason is null
                ? new JsonObject { ["entry"] = i, ["fromSec"] = from, ["toSec"] = to }
                : new JsonObject { ["entry"] = i, ["reason"] = reason });
        }

        return (result, report);
    }

    private static (double? Sec, string? Reason) Resolve(
        string? eventId, VideoAnalysisArtifact artifact, HashSet<string> offered, OutputTimeline timeline, IReadOnlyList<ResolvedScreenInsert> inserts)
    {
        if (eventId is null)
            return (null, null);
        if (!offered.Contains(eventId) || VideoEventOffers.Find(artifact, eventId) is not { } ev)
            return (null, EventMoveExpander.UnknownAnchorReason);
        (double? at, string? reason) = RenderEventAnchors.ResolveOutputSec(ev, timeline, inserts);
        return at is null ? (null, reason ?? EventMoveExpander.CutAwayReason) : (Math.Round(at.Value, 3), null);
    }
}
