using System.Globalization;
using System.Text;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>One caption cue of the sidecar, in milliseconds from the start of the film.</summary>
public sealed record CaptionCue(long StartMs, long EndMs, string Text);

/// <summary>
/// R09.7: the SRT/VTT sidecar written next to the film. The cue list is every caption item in the
/// (flattened) document - enabled or not, because "sidecar only" is a disabled captions track - ordered
/// by start, clipped to the render range and shifted so the film starts at zero. Text is the sanitised
/// caption lines, the same lines the burn-in draws.
/// </summary>
public static class CaptionSidecar
{
    public static IReadOnlyList<CaptionCue> Cues(EditTimeline document, TimelineRenderRange? range)
    {
        double fps = document.Settings.Rate is { Den: > 0, Num: > 0 } r ? r.Num / (double)r.Den : 30.0;
        long rangeStart = range?.InFrame ?? 0;
        long rangeEnd = range?.OutFrame ?? long.MaxValue;
        List<(long In, long Out, string Text)> found = [];
        foreach (Track track in document.Tracks)
        {
            foreach (Item item in track.Items)
            {
                if (item is not Caption caption) continue;
                long start = Math.Max(rangeStart, caption.RecordIn);
                long end = Math.Min(rangeEnd, caption.RecordOut);
                if (end <= start) continue;
                IReadOnlyList<string> lines = CaptionBurnIn.Lines(caption.Text, RegionFilterGraphBuilder.MaxCaptionChars);
                if (lines.Count == 0) continue;
                found.Add((start - rangeStart, end - rangeStart, string.Join('\n', lines)));
            }
        }

        return found
            .OrderBy(c => c.In).ThenBy(c => c.Out)
            .Select(c => new CaptionCue((long)Math.Round(c.In * 1000.0 / fps), (long)Math.Round(c.Out * 1000.0 / fps), c.Text))
            .ToList();
    }

    public static string Srt(IReadOnlyList<CaptionCue> cues)
    {
        StringBuilder sb = new();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Stamp(cues[i].StartMs, ',')).Append(" --> ").Append(Stamp(cues[i].EndMs, ',')).Append('\n');
            sb.Append(cues[i].Text).Append("\n\n");
        }

        return sb.ToString();
    }

    public static string Vtt(IReadOnlyList<CaptionCue> cues)
    {
        StringBuilder sb = new("WEBVTT\n\n");
        foreach (CaptionCue cue in cues)
        {
            sb.Append(Stamp(cue.StartMs, '.')).Append(" --> ").Append(Stamp(cue.EndMs, '.')).Append('\n');
            // The sanitiser already strips markup characters; escape the VTT specials defensively.
            sb.Append(cue.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string Stamp(long ms, char sep)
    {
        long h = ms / 3_600_000;
        long m = ms / 60_000 % 60;
        long s = ms / 1000 % 60;
        long f = ms % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:D2}:{m:D2}:{s:D2}{sep}{f:D3}");
    }
}
