using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>What the first loudnorm pass measured on the finished program.</summary>
public sealed record LoudnessMeasurement(
    double IntegratedLufs, double TruePeakDbtp, double RangeLu, double ThresholdLufs, double TargetOffset);

/// <summary>
/// <c>VideoCompileStepConfig.LoudnessTarget</c>: brings the finished program to a platform
/// loudness in two ffmpeg passes over the ENCODED file — measure with <c>loudnorm</c>, then apply
/// the measured values with <c>linear=true</c> (one gain for the whole program, so the mix's own
/// balance between narration, music and effects is untouched) and a true-peak ceiling. Only the
/// audio is re-encoded; the video stream is copied, so the picture is bit-identical.
/// </summary>
/// <remarks>
/// A narrated program measured -22.5 LUFS before this existed — about 8 dB quieter than every
/// other video in a social feed, which normalizes playback toward roughly -14.
/// </remarks>
public static class LoudnessNormalizer
{
    /// <summary>The integrated loudness, true-peak ceiling and loudness range for a target.</summary>
    public static (double Lufs, double TruePeak, double Range)? Target(ProgramLoudness target) => target switch
    {
        ProgramLoudness.Social => (-14, -1.5, 11),
        ProgramLoudness.Web => (-16, -1.5, 11),
        ProgramLoudness.Broadcast => (-23, -2.0, 15),
        _ => null
    };

    /// <summary>The measuring pass: decode the program's audio, print loudnorm's JSON on stderr.</summary>
    public static IReadOnlyList<string> MeasureArgs(string inputPath, (double Lufs, double TruePeak, double Range) target) =>
    [
        "-hide_banner", "-nostdin", "-i", inputPath, "-map", "0:a:0", "-vn",
        "-af", $"loudnorm=I={Num(target.Lufs)}:TP={Num(target.TruePeak)}:LRA={Num(target.Range)}:print_format=json",
        "-f", "null", "-"
    ];

    /// <summary>The applying pass: video copied, audio normalized with the measured values.</summary>
    /// <param name="extraHeadroomDb">Lowers the limiter's ceiling further: the AAC encode after the
    /// limiter overshoots it on hard transients (a hip-hop reel measured -0.14 dBTP against -1.5), so a
    /// re-run after verification passes the measured overshoot here.</param>
    public static IReadOnlyList<string> ApplyArgs(
        string inputPath, string outputPath, (double Lufs, double TruePeak, double Range) target, LoudnessMeasurement m,
        double extraHeadroomDb = 0) =>
    [
        "-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map", "0:v?", "-map", "0:a:0", "-c:v", "copy",
        "-af",
        $"loudnorm=I={Num(target.Lufs)}:TP={Num(target.TruePeak)}:LRA={Num(target.Range)}" +
        $":measured_I={Num(m.IntegratedLufs)}:measured_TP={Num(m.TruePeakDbtp)}:measured_LRA={Num(m.RangeLu)}" +
        $":measured_thresh={Num(m.ThresholdLufs)}:offset={Num(m.TargetOffset)}:linear=true:print_format=summary" +
        $",alimiter=limit={Num(LimiterCeiling(target.TruePeak - Math.Max(0, extraHeadroomDb)))}:level=false:latency=true",
        "-ar", "48000", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputPath
    ];

    /// <summary>The fallback pass when normalisation failed: video copied, audio through a true-peak limiter only (no gain change).</summary>
    public static IReadOnlyList<string> FallbackLimiterArgs(string inputPath, string outputPath, double truePeakDb) =>
    [
        "-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map", "0:v?", "-map", "0:a:0", "-c:v", "copy",
        "-af", $"alimiter=limit={Num(LimiterCeiling(Math.Min(truePeakDb, -1.0)))}:level=false:latency=true",
        "-ar", "48000", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputPath
    ];

    /// <summary>
    /// The sample-peak ceiling of the limiter after <c>loudnorm</c>, half a dB under the true-peak
    /// target. When one linear gain cannot reach the target without breaching the ceiling, loudnorm
    /// falls back to its dynamic mode, which does NOT hold the ceiling: a narrated launch story came
    /// out at +0.5 dBTP against a -1.5 target. The limiter (lookahead compensated) is what holds it.
    /// </summary>
    public static double LimiterCeiling(double truePeakDb) => Math.Round(Math.Pow(10, (truePeakDb - 0.5) / 20), 3);

    /// <summary>Tolerance before a verified true peak counts as over the target.</summary>
    public const double TruePeakToleranceDb = 0.2;

    /// <summary>Warning recorded when an explicit <c>AudioCodec=copy</c> leaves the audio un-normalised and un-limited.</summary>
    public const string SkippedAudioCopyWarning = "loudness_skipped_audio_copy";

    /// <summary>
    /// Records the FINAL measurement of the delivered audio (<c>outputTruePeak</c>, <c>outputLufs</c>) on the loudness
    /// node; called after the verify pass and again after a true-peak redo, so neither figure goes stale.
    /// </summary>
    public static void RecordFinalMeasurement(JsonObject node, LoudnessMeasurement m)
    {
        node["outputTruePeak"] = Math.Round(m.TruePeakDbtp, 1);
        node["outputLufs"] = Math.Round(m.IntegratedLufs, 1);
    }

    /// <summary>
    /// The JSON block loudnorm prints at the end of the measuring pass, or null when there is none or
    /// it measured silence (an integrated loudness of -inf cannot be normalized).
    /// </summary>
    public static LoudnessMeasurement? ParseMeasurement(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return null;
        int open = stderr.LastIndexOf('{');
        int close = stderr.LastIndexOf('}');
        if (open < 0 || close < open)
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stderr[open..(close + 1)]);
            JsonElement root = doc.RootElement;
            double? Read(string name) =>
                root.TryGetProperty(name, out JsonElement e) &&
                double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
                double.IsFinite(v)
                    ? v
                    : null;

            return Read("input_i") is { } i && Read("input_tp") is { } tp && Read("input_lra") is { } lra &&
                   Read("input_thresh") is { } thresh && Read("target_offset") is { } offset
                ? new LoudnessMeasurement(i, tp, lra, thresh, offset)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
