using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One flash cue's outcome, as reported in the step output.</summary>
internal sealed record FlashReportEntry(string EventId, bool Applied, string? Reason, double AtSec);

/// <summary>The result of <see cref="FlashCueExpander.Expand"/>: one filter chain for the program (or null) and a report.</summary>
internal sealed record FlashCueExpansion(string? Filter, IReadOnlyList<FlashReportEntry> Report)
{
    internal static readonly FlashCueExpansion Empty = new(null, []);

    internal JsonObject ToNode()
    {
        var applied = new JsonArray();
        var dropped = new JsonArray();
        foreach (FlashReportEntry e in Report)
        {
            if (e.Applied)
                applied.Add(new JsonObject { ["eventId"] = e.EventId, ["atSec"] = e.AtSec });
            else
                dropped.Add(new JsonObject { ["eventId"] = e.EventId, ["reason"] = e.Reason });
        }

        return new JsonObject { ["flashesApplied"] = applied, ["dropped"] = dropped };
    }
}

/// <summary>
/// Turns <see cref="VideoCompileStepConfig.FlashCues"/> into a chain of time-gated full-frame tints on the program
/// (docs/video-editing.md "Flash and glow"). Event ids resolve through the same lookup and output mapping the sound
/// cues use; every number comes from the word tables below, never from config. Pure and deterministic.
/// </summary>
internal static class FlashCueExpander
{
    // Seconds a frame boundary may be missed by (float timestamps): the gate opens this much early.
    private const double GateEpsilonSeconds = 0.001;

    // Share of the flash spent at full strength; the rest fades at half strength.
    private const double PeakShare = 0.4;

    // ---- Word tables. ----
    internal static string ColourHex(FlashColour c) => c switch
    {
        FlashColour.Warm => "0xFFD699",
        FlashColour.Cool => "0xAACDFF",
        _ => "0xFFFFFF"
    };

    // Opacity of the tint at the flash's peak.
    internal static double PeakAlpha(FlashStrength s) => s switch
    {
        FlashStrength.Subtle => 0.30,
        FlashStrength.Strong => 0.85,
        _ => 0.55
    };

    // Seconds the whole flash lasts, from the event.
    internal static double LengthSeconds(FlashLength l) => l switch
    {
        FlashLength.Medium => 0.20,
        FlashLength.Long => 0.40,
        _ => 0.10
    };

    internal static FlashCueExpansion Expand(
        VideoCompileStepConfig config, VideoAnalysisArtifact artifact, OutputTimeline timeline, IReadOnlyList<ResolvedScreenInsert> inserts)
    {
        if (config.FlashCues is not { Count: > 0 } cues)
            return FlashCueExpansion.Empty;

        var offered = new HashSet<string>(artifact.OfferedEventIds ?? [], StringComparer.Ordinal);
        var report = new List<FlashReportEntry>();
        var stages = new List<string>();
        foreach (FlashCueRequest cue in cues)
        {
            if (!offered.Contains(cue.EventId) || VideoEventOffers.Find(artifact, cue.EventId) is not { } ev)
            {
                report.Add(new(cue.EventId, false, EventMoveExpander.UnknownAnchorReason, 0));
                continue;
            }

            (double? at, string? reason) = RenderEventAnchors.ResolveOutputSec(ev, timeline, inserts);
            if (at is null)
            {
                report.Add(new(cue.EventId, false, reason ?? EventMoveExpander.CutAwayReason, 0));
                continue;
            }

            double start = at.Value;
            double length = LengthSeconds(cue.Length);
            double peakEnd = start + length * PeakShare;
            double end = start + length;
            double alpha = PeakAlpha(cue.Strength);
            string colour = ColourHex(cue.Colour);
            stages.Add(Box(colour, alpha, start, peakEnd));
            stages.Add(Box(colour, alpha / 2, peakEnd, end));
            report.Add(new(cue.EventId, true, null, Math.Round(start, 3)));
        }

        return new FlashCueExpansion(stages.Count == 0 ? null : string.Join(",", stages), report);
    }

    private static string Box(string colour, double alpha, double from, double to) =>
        $"drawbox=x=0:y=0:w=iw:h=ih:color={colour}@{FfmpegArgvFormat.Number(Math.Round(alpha, 3))}:t=fill:" +
        $"enable='gte(t,{FfmpegArgvFormat.Number(Math.Round(Math.Max(0, from - GateEpsilonSeconds), 4))})*lt(t,{FfmpegArgvFormat.Number(Math.Round(to - GateEpsilonSeconds, 4))})'";
}
