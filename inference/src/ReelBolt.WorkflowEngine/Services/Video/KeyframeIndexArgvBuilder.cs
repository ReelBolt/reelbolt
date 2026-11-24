using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Builds the ffprobe argv for the keyframe index, parses its output, and emits the index sidecar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a sibling builder and not a reuse of the existing
/// <see cref="IKeyframeExtractor"/>.</b> That pair (<c>IKeyframeExtractor</c> /
/// <c>FfmpegKeyframeExtractor</c>, backed by <c>FfmpegArgvBuilder.BuildKeyframeArgs</c> and
/// <c>BuildContactSheetArgs</c>) serves the ANALYSIS path: it produces a <i>picture</i> — one
/// representative JPEG, or an hstacked contact sheet of N of them — at a caller-chosen time. It
/// never asks ffmpeg or ffprobe what the source's keyframes actually <i>are</i>, so it cannot
/// answer "where may I seek to cheaply?", which is the entire content of this derivative. Reading
/// packet flags is also cheaper than the alternative of decoding the file with
/// <c>-skip_frame nokey</c>, because it never touches the decoder at all.
/// </para>
/// <para>
/// Kept in one type with its parser so the argv and the shape it produces cannot drift apart —
/// they are two halves of one contract. <c>-protocol_whitelist file</c> is carried for the same
/// reason every other builder carries it (see <see cref="FfmpegArgvBuilder"/>).
/// </para>
/// </remarks>
public static class KeyframeIndexArgvBuilder
{
    /// <summary>
    /// <c>-select_streams v:0 -show_entries packet=pts_time,flags</c> — every video packet's
    /// presentation timestamp and its flag string. A keyframe packet's flags contain <c>K</c>
    /// (typically <c>K__</c>).
    /// </summary>
    public static string[] BuildProbeArgs(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        return
        [
            "-v", "error",
            "-print_format", "json",
            "-select_streams", MediaDerivativeSpec.KeyframeStreamSelector,
            "-show_entries", "packet=pts_time,flags",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            inputPath
        ];
    }

    /// <summary>
    /// Extracts the keyframe presentation times, ascending and de-duplicated. Packets whose
    /// <c>pts_time</c> is missing or unparseable (ffprobe writes <c>"N/A"</c>) are skipped rather
    /// than treated as time zero — a phantom keyframe at 0 s would make every seek plan wrong.
    /// A malformed document yields an empty list rather than throwing, matching this feature's
    /// "never throws" discipline.
    /// </summary>
    public static IReadOnlyList<double> ParseKeyframeTimes(string ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson))
            return [];

        var times = new List<double>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ffprobeJson);
            if (!doc.RootElement.TryGetProperty("packets", out JsonElement packets) ||
                packets.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (JsonElement packet in packets.EnumerateArray())
            {
                if (!packet.TryGetProperty("flags", out JsonElement flagsElement) ||
                    flagsElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? flags = flagsElement.GetString();
                if (flags is null || !flags.Contains('K', StringComparison.Ordinal))
                    continue;

                if (!packet.TryGetProperty("pts_time", out JsonElement ptsElement))
                    continue;

                string? raw = ptsElement.ValueKind == JsonValueKind.String
                    ? ptsElement.GetString()
                    : ptsElement.ToString();

                if (raw is null ||
                    !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value) ||
                    value < 0)
                {
                    continue;
                }

                times.Add(value);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        times.Sort();
        var deduped = new List<double>(times.Count);
        foreach (double t in times)
        {
            if (deduped.Count == 0 || Math.Abs(deduped[^1] - t) > 1e-9)
                deduped.Add(t);
        }

        return deduped;
    }

    /// <summary>
    /// The typical distance between consecutive keyframes, as the MEDIAN of the deltas — a
    /// seek-planning hint, not a bound. The median rather than the mean because a single
    /// scene-cut keyframe in the middle of an otherwise uniform GOP would drag a mean well away
    /// from the value that describes the file. Zero when there are fewer than two keyframes.
    /// </summary>
    public static double ComputeGopHintSec(IReadOnlyList<double> timesSec)
    {
        ArgumentNullException.ThrowIfNull(timesSec);
        if (timesSec.Count < 2)
            return 0;

        var deltas = new List<double>(timesSec.Count - 1);
        for (int i = 1; i < timesSec.Count; i++)
        {
            double delta = timesSec[i] - timesSec[i - 1];
            if (delta > 0)
                deltas.Add(delta);
        }

        if (deltas.Count == 0)
            return 0;

        deltas.Sort();
        return Math.Round(deltas[deltas.Count / 2], 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The index sidecar: <c>{ timesSec: [...], count, gopHintSec }</c>.
    /// </summary>
    public static string BuildIndexJson(IReadOnlyList<double> timesSec, double gopHintSec)
    {
        ArgumentNullException.ThrowIfNull(timesSec);

        var builder = new StringBuilder();
        builder.Append("{\"timesSec\":[");
        for (int i = 0; i < timesSec.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(Number(timesSec[i]));
        }

        builder.Append("],\"count\":").Append(timesSec.Count.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"gopHintSec\":").Append(Number(gopHintSec));
        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>The object leaf of the keyframe index sidecar.</summary>
    public static string IndexLeaf(string paramsHash) => $"keyframes-{paramsHash}.json";

    /// <summary>
    /// Six decimals — microsecond resolution, comfortably finer than one frame at 240 fps, and
    /// invariant-culture so the document is identical on a comma-decimal host (R8).
    /// </summary>
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
