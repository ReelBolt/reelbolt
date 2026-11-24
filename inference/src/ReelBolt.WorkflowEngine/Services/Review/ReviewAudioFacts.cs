using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>What the engine's ffmpeg measured on the film's sound (ebur128). Null members could not be read.</summary>
/// <param name="Momentary">Momentary loudness (400 ms window) every 100 ms: (time in s, LUFS; -inf as -120).</param>
public sealed record ReviewAudioMeasurement(
    double? IntegratedLufs,
    double? RangeLu,
    double? TruePeakDbtp,
    IReadOnlyList<(double AtSec, double Lufs)> Momentary);

/// <summary>
/// The <c>[AUDIO FACTS]</c> block (<c>visualCheck.audioFacts</c>): first-party sound facts measured with the engine's
/// ffmpeg over the film under review (no audio leaves the host and no model hears it): integrated loudness, loudness
/// range, true peak and probable clipping, silences, how loud each sound-effect cue lands against the second before
/// it, and the biggest loudness jumps at cuts. Narration words are not transcribed: no review provider understands
/// audio, so the reviewer is told to judge word timing from the timeline facts instead.
/// </summary>
public static partial class ReviewAudioFacts
{
    /// <summary>The longest block appended to the reviewer's input.</summary>
    public const int MaxChars = 2500;

    /// <summary>Momentary loudness under this is silence.</summary>
    public const double SilenceLufs = -50;

    /// <summary>The shortest silence reported, in seconds.</summary>
    public const double MinSilenceSec = 0.8;

    /// <summary>A true peak at or above this (dBTP) is reported as probable clipping.</summary>
    public const double ClippingDbtp = -0.1;

    /// <summary>A loudness change at a cut at least this large (LU) is reported.</summary>
    public const double CutJumpLu = 6;

    /// <summary>Most momentary samples kept (a long film's log is cut, never the summary).</summary>
    public const int MaxMomentarySamples = 36_000;

    private const int MaxListed = 8;
    private const string Header = "[AUDIO FACTS]";
    private const string Footer = "[END AUDIO FACTS]";

    /// <summary>The argv that makes ffmpeg print ebur128's per-100-ms lines and summary on stderr.</summary>
    public static IReadOnlyList<string> MeasureArgs(string inputPath) =>
    [
        "-hide_banner", "-nostdin", "-nostats", .. UserMediaInputFormats.InputOptions(), "-i", inputPath, "-map", "0:a:0", "-vn",
        "-af", "ebur128=peak=true", "-f", "null", "-"
    ];

    /// <summary>Parses ffmpeg's ebur128 stderr; null when it carries neither a summary nor a momentary line.</summary>
    public static ReviewAudioMeasurement? Parse(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return null;

        List<(double, double)> momentary = [];
        foreach (Match m in MomentaryLine().Matches(stderr))
        {
            if (momentary.Count >= MaxMomentarySamples)
                break;
            if (TryNum(m.Groups["t"].Value, out double t) && TryLoud(m.Groups["m"].Value, out double lufs))
                momentary.Add((t, lufs));
        }

        int summaryAt = stderr.LastIndexOf("Summary:", StringComparison.Ordinal);
        double? integrated = null, range = null, peak = null;
        if (summaryAt >= 0)
        {
            string summary = stderr[summaryAt..];
            if (SummaryI().Match(summary) is { Success: true } i && TryLoud(i.Groups[1].Value, out double iv))
                integrated = iv;
            if (SummaryLra().Match(summary) is { Success: true } l && TryNum(l.Groups[1].Value, out double lv))
                range = lv;
            if (SummaryPeak().Match(summary) is { Success: true } p && TryLoud(p.Groups[1].Value, out double pv))
                peak = pv;
        }

        return momentary.Count == 0 && integrated is null ? null : new ReviewAudioMeasurement(integrated, range, peak, momentary);
    }

    /// <summary>The block; null when nothing was measured. <paramref name="manifest"/> supplies the cues and cuts.</summary>
    public static string? Render(ReviewAudioMeasurement? measurement, TimelineManifest? manifest)
    {
        if (measurement is null)
            return null;

        List<string> lines = [];
        lines.Add("Integrated loudness " + Lufs(measurement.IntegratedLufs) + " LUFS, loudness range " + Lu(measurement.RangeLu)
            + " LU, true peak " + Lufs(measurement.TruePeakDbtp) + " dBTP.");
        if (measurement.TruePeakDbtp is >= ClippingDbtp)
            lines.Add("Probable clipping: the true peak reaches full scale.");

        IReadOnlyList<(double Start, double End)> silences = Silences(measurement.Momentary);
        if (silences.Count > 0)
        {
            lines.Add($"Silences of {Num(MinSilenceSec)} s or more ({silences.Count}): "
                + string.Join(", ", silences.Take(MaxListed).Select(s => $"{Num(s.Start)}–{Num(s.End)} s"))
                + (silences.Count > MaxListed ? ", …" : string.Empty) + ".");
        }

        if (manifest is not null)
        {
            List<string> cues = [];
            foreach (TimelineItem cue in manifest.Tracks.Where(t => t.Type == "sfx").SelectMany(t => t.Items).OrderBy(i => i.TimelineStartSec))
            {
                double start = cue.TimelineStartSec;
                double end = cue.TimelineEndSec ?? start + Math.Min(cue.DurationSec ?? 1, 1);
                double? during = Max(measurement.Momentary, start, Math.Min(end, start + 1.0));
                double? before = Mean(measurement.Momentary, start - 1.0, start);
                if (during is null)
                    continue;
                cues.Add($"{Num(start)} s peaks at {Lufs(during)} LUFS" + (before is { } b ? $" ({Signed(during.Value - b)} LU vs the second before)" : string.Empty));
            }

            if (cues.Count > 0)
            {
                lines.Add("Sound-effect cues (momentary loudness): " + string.Join("; ", cues.Take(MaxListed))
                    + (cues.Count > MaxListed ? $"; … {cues.Count - MaxListed} more" : string.Empty) + ".");
            }

            List<(double At, double Jump)> jumps = [];
            // A cut is where the picture changes: the start of every item after the first, and the end of a generated clip
            // that is not the film's last frame (a cutaway hands the picture back to the body there; a cold open's end is the
            // body's start already, and an end card's end is the end of the film).
            List<TimelineItem> videoItems = [.. manifest.Tracks.Where(t => t.Type == "video").SelectMany(t => t.Items).OrderBy(i => i.TimelineStartSec)];
            List<double> cutTimes = [.. videoItems.Skip(1).Select(i => i.TimelineStartSec)];
            foreach (TimelineItem generated in videoItems.Where(i => i.SourceIndex is null && i.TimelineEndSec is not null))
            {
                double end = generated.TimelineEndSec!.Value;
                if (end < manifest.OutputVideo.DurationSec - 0.05 && !cutTimes.Any(t => Math.Abs(t - end) < 0.001))
                    cutTimes.Add(end);
            }

            foreach (double at in cutTimes.Order())
            {
                if (Mean(measurement.Momentary, at - 0.5, at) is { } b && Mean(measurement.Momentary, at, at + 0.5) is { } a && Math.Abs(a - b) >= CutJumpLu)
                    jumps.Add((at, a - b));
            }

            if (jumps.Count > 0)
            {
                lines.Add($"Loudness jumps of {Num(CutJumpLu)} LU or more at cuts: "
                    + string.Join(", ", jumps.OrderByDescending(j => Math.Abs(j.Jump)).Take(MaxListed).Select(j => $"{Num(j.At)} s ({Signed(j.Jump)} LU)")) + ".");
            }
        }

        lines.Add("Narration words are not transcribed here (no review provider listens to audio); judge where lines land from the timeline facts.");

        StringBuilder block = new();
        block.Append(Header).Append(" (measured by the platform with ffmpeg on the film under review; no audio was sent anywhere)");
        foreach (string line in lines)
        {
            if (block.Length + line.Length + Footer.Length + 2 > MaxChars)
                break;
            block.Append('\n').Append(line);
        }

        block.Append('\n').Append(Footer);
        return block.ToString();
    }

    /// <summary>Runs of momentary loudness under <see cref="SilenceLufs"/> lasting at least <see cref="MinSilenceSec"/>.</summary>
    public static IReadOnlyList<(double Start, double End)> Silences(IReadOnlyList<(double AtSec, double Lufs)> momentary)
    {
        List<(double, double)> silences = [];
        double? start = null;
        double last = 0;
        foreach ((double at, double lufs) in momentary)
        {
            if (lufs < SilenceLufs)
                start ??= at;
            else if (start is { } s)
            {
                if (at - s >= MinSilenceSec)
                    silences.Add((s, at));
                start = null;
            }

            last = at;
        }

        if (start is { } open && last - open >= MinSilenceSec)
            silences.Add((open, last));
        return silences;
    }

    private static double? Max(IReadOnlyList<(double AtSec, double Lufs)> m, double from, double to)
    {
        double? best = null;
        foreach ((double at, double l) in m)
        {
            if (at >= from && at <= to && (best is null || l > best))
                best = l;
        }

        return best;
    }

    private static double? Mean(IReadOnlyList<(double AtSec, double Lufs)> m, double from, double to)
    {
        double sum = 0;
        int n = 0;
        foreach ((double at, double l) in m)
        {
            if (at >= from && at < to)
            {
                sum += l;
                n++;
            }
        }

        return n == 0 ? null : sum / n;
    }

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryLoud(string text, out double value)
    {
        if (text.Trim().EndsWith("inf", StringComparison.OrdinalIgnoreCase))
        {
            value = -120;
            return true;
        }

        return TryNum(text, out value);
    }

    private static string Lufs(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";
    private static string Lu(double? value) => Lufs(value);
    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Signed(double value) => (value >= 0 ? "+" : string.Empty) + value.ToString("0.0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"t:\s*(?<t>[0-9.]+)\s+TARGET:[^\n]*?M:\s*(?<m>-?inf|-?[0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex MomentaryLine();

    [GeneratedRegex(@"I:\s*(-?inf|-?[0-9.]+)\s*LUFS", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryI();

    [GeneratedRegex(@"LRA:\s*([0-9.]+)\s*LU", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryLra();

    [GeneratedRegex(@"Peak:\s*(-?inf|-?[0-9.]+)\s*dBFS", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPeak();
}
